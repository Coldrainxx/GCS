using HelixToolkit.Wpf;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;

namespace GCS.Views;

public partial class Model3DTabView : UserControl
{
    private const string DefaultStlFilename = "WCR.master_1.stl";
    private const double ModelScale = 0.01;
    private const double InitialYawOffset = 0.0;
    private const int UpdateIntervalMs = 33;

    private Transform3DGroup? _modelTransformGroup;
    private AxisAngleRotation3D? _rotationRoll;
    private AxisAngleRotation3D? _rotationPitch;
    private AxisAngleRotation3D? _rotationYaw;
    private bool _modelLoaded;

    // ── Swarm view ───────────────────────────────────────────────────
    // In swarm mode the tab shows every drone in its real relative position
    // instead of one model. Meshes are shared between drones — the STL is large.
    private const string SwarmStlFilename = "swarmdrone.stl";
    // Preferred drone size in scene units. Actual size is capped so neighbours
    // never overlap — see UpdateSwarmModels — so this and the fleet extent below
    // no longer have to be balanced against each other by hand.
    private const double SwarmDroneSize = 3.0;
    private const double SwarmTargetExtent = 3.0;   // scene units the fleet spans
    private const double SwarmMinSeparationFactor = 0.75; // of the gap to nearest neighbour
    private const double MetresPerDegreeLat = 110540.0;
    private const double MetresPerDegreeLonAtEquator = 111320.0;

    private GCS.ViewModels.MainViewModel? _mainVm;
    private readonly ModelVisual3D _swarmRoot = new();
    private readonly System.Collections.Generic.List<MeshGeometry3D> _sharedMeshes = new();
    private readonly System.Collections.Generic.Dictionary<byte, SwarmDrone> _swarmDrones = new();
    private double _swarmModelScale = 1.0;
    private bool _swarmMeshesAttempted;

    private sealed class SwarmDrone
    {
        public ModelVisual3D Visual = null!;
        public TranslateTransform3D Position = null!;
        public ScaleTransform3D Scale = null!;
        public AxisAngleRotation3D Roll = null!;
        public AxisAngleRotation3D Pitch = null!;
        public AxisAngleRotation3D Yaw = null!;
        public bool IsLeader;
        public Model3DGroup Model = null!;
    }

    private readonly DispatcherTimer _updateTimer;
    private double _targetRoll;
    private double _targetPitch;
    private double _targetYaw;
    private bool _needsUpdate;

    #region Dependency Properties

    public static readonly DependencyProperty RollProperty =
        DependencyProperty.Register(nameof(Roll), typeof(double), typeof(Model3DTabView),
            new PropertyMetadata(0.0, OnAttitudeChanged));

    public static readonly DependencyProperty PitchProperty =
        DependencyProperty.Register(nameof(Pitch), typeof(double), typeof(Model3DTabView),
            new PropertyMetadata(0.0, OnAttitudeChanged));

    public static readonly DependencyProperty YawProperty =
        DependencyProperty.Register(nameof(Yaw), typeof(double), typeof(Model3DTabView),
            new PropertyMetadata(0.0, OnAttitudeChanged));

    public static readonly DependencyProperty StlModelPathProperty =
        DependencyProperty.Register(nameof(StlModelPath), typeof(string), typeof(Model3DTabView),
            new PropertyMetadata(DefaultStlFilename, OnStlPathChanged));

    public double Roll
    {
        get => (double)GetValue(RollProperty);
        set => SetValue(RollProperty, value);
    }

    public double Pitch
    {
        get => (double)GetValue(PitchProperty);
        set => SetValue(PitchProperty, value);
    }

    public double Yaw
    {
        get => (double)GetValue(YawProperty);
        set => SetValue(YawProperty, value);
    }

    public string StlModelPath
    {
        get => (string)GetValue(StlModelPathProperty);
        set => SetValue(StlModelPathProperty, value);
    }

    private static void OnAttitudeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is Model3DTabView view)
        {
            view._targetRoll = view.Roll;
            view._targetPitch = view.Pitch;
            view._targetYaw = view.Yaw;
            view._needsUpdate = true;

            // If the timer is stopped (tab not visible), apply immediately
            // so the model is correct when the tab becomes visible again
            if (!view._updateTimer.IsEnabled && view._modelLoaded)
            {
                view.UpdateModelRotation();
            }
        }
    }

    private static void OnStlPathChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is Model3DTabView view && view.IsLoaded)
            view.LoadSTLModel();
    }

    #endregion

    public Model3DTabView()
    {
        InitializeComponent();

        _updateTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(UpdateIntervalMs)
        };
        _updateTimer.Tick += OnUpdateTimerTick;

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += OnVisibleChanged;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Debug.WriteLine("[Model3D] OnLoaded");
        LoadSTLModel();

        if (!Viewport3D.Children.Contains(_swarmRoot))
            Viewport3D.Children.Add(_swarmRoot);

        if (_mainVm == null && Window.GetWindow(this)?.DataContext is GCS.ViewModels.MainViewModel vm)
            _mainVm = vm;

        if (IsVisible) _updateTimer.Start();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => _updateTimer.Stop();

    private void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true && IsLoaded)
        {
            // Force an immediate update when becoming visible,
            // so the model shows the latest attitude even if
            // values didn't change while the tab was hidden.
            _needsUpdate = true;
            UpdateModelRotation();
            _updateTimer.Start();
        }
        else
        {
            _updateTimer.Stop();
        }
    }

    private void OnUpdateTimerTick(object? sender, EventArgs e)
    {
        bool swarmMode = _mainVm?.IsSwarmMode == true;

        if (swarmMode)
        {
            UpdateSwarmModels();
            return;
        }

        if (_swarmDrones.Count > 0) ClearSwarmModels();

        if (!_needsUpdate || !_modelLoaded) return;
        _needsUpdate = false;
        UpdateModelRotation();
    }

    // ═══════════════════════════════════════════════════════════════
    // Swarm rendering
    // ═══════════════════════════════════════════════════════════════

    private void UpdateSwarmModels()
    {
        var swarm = _mainVm?.Swarm;
        if (swarm == null) return;

        EnsureSwarmMeshes();            // loads swarmdrone.stl on first use
        if (_sharedMeshes.Count == 0) return;

        var vehicles = swarm.Vehicles.Where(v => v.HasPosition).ToList();
        if (vehicles.Count == 0) { ClearSwarmModels(); return; }

        // Single model is replaced by the fleet.
        UAVModelVisual.Content = null;

        // Frame relative to the leader when there is one, else the first vehicle.
        var reference = vehicles.FirstOrDefault(v => v.IsLeader) ?? vehicles[0];
        double refLat = reference.Latitude, refLon = reference.Longitude, refAlt = reference.AltitudeRel;
        double lonScale = MetresPerDegreeLonAtEquator * Math.Cos(refLat * Math.PI / 180.0);

        // Local ENU offsets in metres.
        var offsets = new System.Collections.Generic.Dictionary<byte, (double E, double N, double U)>();
        double maxExtent = 0;
        foreach (var v in vehicles)
        {
            double e = (v.Longitude - refLon) * lonScale;
            double n = (v.Latitude - refLat) * MetresPerDegreeLat;
            double u = v.AltitudeRel - refAlt;
            offsets[v.SystemId] = (e, n, u);
            maxExtent = Math.Max(maxExtent, Math.Max(Math.Abs(e), Math.Max(Math.Abs(n), Math.Abs(u))));
        }

        // Fit the fleet into a sensible volume regardless of real separation.
        double sceneScale = maxExtent > 1.0 ? SwarmTargetExtent / maxExtent : 0.05;

        // Cap drone size at a fraction of the closest gap between two aircraft,
        // so a tight formation shrinks the models instead of turning into a blob.
        double droneSize = SwarmDroneSize;
        double closestGap = ClosestPairDistance(offsets, sceneScale);
        if (closestGap > 0)
            droneSize = Math.Min(droneSize, closestGap * SwarmMinSeparationFactor);
        double modelScale = _swarmModelScale * (droneSize / SwarmDroneSize);

        foreach (var v in vehicles)
        {
            var drone = EnsureSwarmDrone(v.SystemId, v.IsLeader);
            if (drone == null) continue;

            drone.Scale.ScaleX = drone.Scale.ScaleY = drone.Scale.ScaleZ = modelScale;

            if (drone.IsLeader != v.IsLeader)
            {
                drone.IsLeader = v.IsLeader;
                ApplySwarmMaterial(drone.Model, v.IsLeader);
            }

            var (e, n, u) = offsets[v.SystemId];
            drone.Position.OffsetX = e * sceneScale;
            drone.Position.OffsetY = n * sceneScale;
            drone.Position.OffsetZ = u * sceneScale;

            drone.Roll.Angle = v.RollDeg;
            drone.Pitch.Angle = -v.PitchDeg;
            drone.Yaw.Angle = v.YawDeg;
        }

        // Drop drones that are gone.
        var live = vehicles.Select(v => v.SystemId).ToHashSet();
        foreach (var id in _swarmDrones.Keys.Where(k => !live.Contains(k)).ToList())
        {
            _swarmRoot.Children.Remove(_swarmDrones[id].Visual);
            _swarmDrones.Remove(id);
        }
    }

    /// <summary>
    /// Distance between the two closest aircraft, in scene units. Returns 0 for a
    /// single vehicle (nothing to collide with, so the preferred size stands).
    /// The fleet is small — a few dozen at most — so the naive pairwise sweep is fine.
    /// </summary>
    private static double ClosestPairDistance(
        System.Collections.Generic.Dictionary<byte, (double E, double N, double U)> offsets,
        double sceneScale)
    {
        if (offsets.Count < 2) return 0;

        var pts = new System.Collections.Generic.List<(double E, double N, double U)>(offsets.Values);
        double closest = double.MaxValue;

        for (int i = 0; i < pts.Count; i++)
            for (int j = i + 1; j < pts.Count; j++)
            {
                double de = pts[i].E - pts[j].E;
                double dn = pts[i].N - pts[j].N;
                double du = pts[i].U - pts[j].U;
                double d = Math.Sqrt(de * de + dn * dn + du * du);
                if (d < closest) closest = d;
            }

        // Two drones reporting the same position would otherwise scale them to
        // nothing; leave the preferred size alone instead.
        double gap = closest * sceneScale;
        return gap > 1e-4 ? gap : 0;
    }

    private SwarmDrone? EnsureSwarmDrone(byte systemId, bool isLeader)
    {
        if (_swarmDrones.TryGetValue(systemId, out var existing)) return existing;
        if (_sharedMeshes.Count == 0) return null;

        var model = new Model3DGroup();
        foreach (var mesh in _sharedMeshes)
            model.Children.Add(new GeometryModel3D { Geometry = mesh });   // mesh shared, not copied
        ApplySwarmMaterial(model, isLeader);

        var transforms = new Transform3DGroup();
        var scale = new ScaleTransform3D(_swarmModelScale, _swarmModelScale, _swarmModelScale);
        transforms.Children.Add(scale);

        var yaw = new AxisAngleRotation3D(new Vector3D(0, 0, 1), 0);
        var pitch = new AxisAngleRotation3D(new Vector3D(-1, 0, 0), 0);
        var roll = new AxisAngleRotation3D(new Vector3D(0, 1, 0), 0);
        transforms.Children.Add(new RotateTransform3D(yaw));
        transforms.Children.Add(new RotateTransform3D(pitch));
        transforms.Children.Add(new RotateTransform3D(roll));

        var position = new TranslateTransform3D(0, 0, 0);
        transforms.Children.Add(position);
        model.Transform = transforms;

        var visual = new ModelVisual3D { Content = model };
        _swarmRoot.Children.Add(visual);

        var drone = new SwarmDrone
        {
            Visual = visual, Model = model, Position = position, Scale = scale,
            Roll = roll, Pitch = pitch, Yaw = yaw, IsLeader = isLeader
        };
        _swarmDrones[systemId] = drone;
        return drone;
    }

    /// <summary>
    /// Load the swarm drone model once and keep its meshes pre-centred and frozen,
    /// so every drone in the fleet references the same geometry rather than the
    /// app re-reading a large STL per vehicle. Falls back to the single-vehicle
    /// model if the swarm STL isn't present.
    /// </summary>
    private void EnsureSwarmMeshes()
    {
        if (_sharedMeshes.Count > 0 || _swarmMeshesAttempted) return;
        _swarmMeshesAttempted = true;

        try
        {
            string? path = FindStlFile(SwarmStlFilename) ?? FindStlFile(DefaultStlFilename);
            if (path == null) { Debug.WriteLine("[Model3D] No swarm STL found"); return; }

            var model = new StLReader().Read(path);
            if (model == null || model.Children.Count == 0) return;

            var b = model.Bounds;
            var center = new Point3D(b.X + b.SizeX / 2, b.Y + b.SizeY / 2, b.Z + b.SizeZ / 2);

            foreach (var child in model.Children)
            {
                if (child is not GeometryModel3D g || g.Geometry is not MeshGeometry3D mesh) continue;

                var centred = new MeshGeometry3D();
                foreach (var p in mesh.Positions)
                    centred.Positions.Add(new Point3D(p.X - center.X, p.Y - center.Y, p.Z - center.Z));
                foreach (var i in mesh.TriangleIndices) centred.TriangleIndices.Add(i);
                foreach (var n in mesh.Normals) centred.Normals.Add(n);
                centred.Freeze();               // frozen => safely shared across visuals
                _sharedMeshes.Add(centred);
            }

            // Scale from this model's own bounds, so it doesn't matter what units
            // the STL was exported in or how it compares to the other model.
            double extent = Math.Max(b.SizeX, Math.Max(b.SizeY, b.SizeZ));
            _swarmModelScale = extent > 0 ? SwarmDroneSize / extent : 1.0;

            Debug.WriteLine($"[Model3D] Swarm model loaded from {path} (extent {extent:F1}, scale {_swarmModelScale:G3})");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Model3D] Swarm model load failed: {ex.Message}");
        }
    }

    private static void ApplySwarmMaterial(Model3DGroup model, bool isLeader)
    {
        var colour = isLeader ? Color.FromRgb(255, 176, 0) : Color.FromRgb(57, 208, 216);
        var material = new DiffuseMaterial(new SolidColorBrush(colour));
        material.Freeze();
        foreach (var child in model.Children)
        {
            if (child is GeometryModel3D g) { g.Material = material; g.BackMaterial = material; }
        }
    }

    private void ClearSwarmModels()
    {
        _swarmRoot.Children.Clear();
        _swarmDrones.Clear();
        // Put the single model back for single-vehicle mode.
        if (_modelLoaded && UAVModelVisual.Content == null) LoadSTLModel();
    }

    private void LoadSTLModel()
    {
        try
        {
            string? stlPath = FindStlFile(StlModelPath ?? DefaultStlFilename);

            if (stlPath != null)
            {
                Debug.WriteLine($"[Model3D] Found STL at: {stlPath}");
                var importer = new StLReader();
                var model = importer.Read(stlPath);

                if (model != null && model.Children.Count > 0)
                {
                    var bounds = model.Bounds;
                    var center = new Point3D(
                        bounds.X + bounds.SizeX / 2,
                        bounds.Y + bounds.SizeY / 2,
                        bounds.Z + bounds.SizeZ / 2);

                    SetupModelTransforms(model, center);
                    ApplyMaterial(model);
                    UAVModelVisual.Content = model;
                    _modelLoaded = true;
                    Debug.WriteLine("[Model3D] STL loaded successfully!");
                    return;
                }
            }

            Debug.WriteLine("[Model3D] STL not found, using fallback model");
            LoadFallbackModel();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Model3D] Error: {ex.Message}");
            LoadFallbackModel();
        }
    }

    private string? FindStlFile(string filename)
    {
        if (Path.IsPathRooted(filename) && File.Exists(filename))
            return filename;

        string justFilename = Path.GetFileName(filename);
        string exeDir = AppDomain.CurrentDomain.BaseDirectory;
        string? projectDir = GetProjectDirectory();

        // Map\models is the canonical home for the STLs: the map has to serve them
        // over the WebView2 virtual host from there, so the 3D tab reads the same
        // copy rather than the build shipping a second one. Models\ stays in the
        // list so a hand-placed or user-supplied model still resolves.
        var searchPaths = new[]
        {
            Path.Combine(exeDir, filename),
            Path.Combine(exeDir, "Map", "models", justFilename),
            Path.Combine(exeDir, "Models", justFilename),
            Path.Combine(exeDir, "Assets", justFilename),
            Path.Combine(exeDir, justFilename),
            projectDir != null ? Path.Combine(projectDir, "Map", "models", justFilename) : null,
            projectDir != null ? Path.Combine(projectDir, "Models", justFilename) : null,
            projectDir != null ? Path.Combine(projectDir, "Assets", justFilename) : null,
            projectDir != null ? Path.Combine(projectDir, justFilename) : null,
        };

        foreach (var path in searchPaths)
        {
            if (path != null && File.Exists(path))
            {
                Debug.WriteLine($"[Model3D] Found at: {path}");
                return path;
            }
            Debug.WriteLine($"[Model3D] Not found: {path}");
        }

        return null;
    }

    private string? GetProjectDirectory()
    {
        try
        {
            string? dir = AppDomain.CurrentDomain.BaseDirectory;
            for (int i = 0; i < 4 && dir != null; i++)
            {
                dir = Directory.GetParent(dir)?.FullName;
            }

            if (dir != null && (File.Exists(Path.Combine(dir, "GCS.csproj")) ||
                               Directory.Exists(Path.Combine(dir, "Models"))))
            {
                return dir;
            }
        }
        catch { }

        return null;
    }

    private void SetupModelTransforms(Model3DGroup model, Point3D modelCenter)
    {
        _modelTransformGroup = new Transform3DGroup();

        _modelTransformGroup.Children.Add(new TranslateTransform3D(
            -modelCenter.X, -modelCenter.Y, -modelCenter.Z));

        _modelTransformGroup.Children.Add(new ScaleTransform3D(ModelScale, ModelScale, ModelScale));

        if (Math.Abs(InitialYawOffset) > 0.001)
        {
            _modelTransformGroup.Children.Add(new RotateTransform3D(
                new AxisAngleRotation3D(new Vector3D(0, 0, 1), InitialYawOffset)));
        }

        _rotationYaw = new AxisAngleRotation3D(new Vector3D(0, 0, 1), 0);
        _rotationPitch = new AxisAngleRotation3D(new Vector3D(-1, 0, 0), 0);
        _rotationRoll = new AxisAngleRotation3D(new Vector3D(0, 1, 0), 0);

        _modelTransformGroup.Children.Add(new RotateTransform3D(_rotationYaw));
        _modelTransformGroup.Children.Add(new RotateTransform3D(_rotationPitch));
        _modelTransformGroup.Children.Add(new RotateTransform3D(_rotationRoll));

        model.Transform = _modelTransformGroup;
    }

    private void ApplyMaterial(Model3DGroup model)
    {
        var material = new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(200, 200, 210)));
        material.Freeze();

        foreach (var child in model.Children)
        {
            if (child is GeometryModel3D geometry)
            {
                geometry.Material = material;
                geometry.BackMaterial = material;
            }
        }
    }

    private void LoadFallbackModel()
    {
        var model = new Model3DGroup();

        var bodyMaterial = new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(100, 100, 110)));
        bodyMaterial.Freeze();
        var wingMaterial = new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(80, 80, 90)));
        wingMaterial.Freeze();
        var noseMaterial = new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(255, 100, 50)));
        noseMaterial.Freeze();

        var fuselage = new GeometryModel3D(CreateBoxMesh(0.08, 0.5, 0.06), bodyMaterial);
        fuselage.BackMaterial = bodyMaterial;
        model.Children.Add(fuselage);

        var wing = new GeometryModel3D(CreateBoxMesh(0.8, 0.1, 0.015), wingMaterial);
        wing.BackMaterial = wingMaterial;
        wing.Transform = new TranslateTransform3D(0, -0.05, 0.01);
        model.Children.Add(wing);

        var tailVert = new GeometryModel3D(CreateBoxMesh(0.015, 0.08, 0.12), wingMaterial);
        tailVert.BackMaterial = wingMaterial;
        tailVert.Transform = new TranslateTransform3D(0, -0.22, 0.06);
        model.Children.Add(tailVert);

        var tailHoriz = new GeometryModel3D(CreateBoxMesh(0.25, 0.05, 0.01), wingMaterial);
        tailHoriz.BackMaterial = wingMaterial;
        tailHoriz.Transform = new TranslateTransform3D(0, -0.22, 0.1);
        model.Children.Add(tailHoriz);

        var nose = new GeometryModel3D(CreatePyramidMesh(0.12, 0.04), noseMaterial);
        nose.BackMaterial = noseMaterial;
        var noseTransform = new Transform3DGroup();
        noseTransform.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(0, 0, 1), 90)));
        noseTransform.Children.Add(new TranslateTransform3D(0, 0.28, 0));
        nose.Transform = noseTransform;
        model.Children.Add(nose);

        SetupModelTransforms(model, new Point3D(0, 0, 0));
        UAVModelVisual.Content = model;
        _modelLoaded = true;
    }

    private static MeshGeometry3D CreateBoxMesh(double sizeX, double sizeY, double sizeZ)
    {
        var mesh = new MeshGeometry3D();
        double hx = sizeX / 2, hy = sizeY / 2, hz = sizeZ / 2;

        mesh.Positions.Add(new Point3D(-hx, -hy, -hz));
        mesh.Positions.Add(new Point3D(hx, -hy, -hz));
        mesh.Positions.Add(new Point3D(hx, hy, -hz));
        mesh.Positions.Add(new Point3D(-hx, hy, -hz));
        mesh.Positions.Add(new Point3D(-hx, -hy, hz));
        mesh.Positions.Add(new Point3D(hx, -hy, hz));
        mesh.Positions.Add(new Point3D(hx, hy, hz));
        mesh.Positions.Add(new Point3D(-hx, hy, hz));

        int[] indices = { 0, 2, 1, 0, 3, 2, 4, 5, 6, 4, 6, 7, 0, 1, 5, 0, 5, 4, 2, 3, 7, 2, 7, 6, 0, 4, 7, 0, 7, 3, 1, 2, 6, 1, 6, 5 };
        foreach (var i in indices) mesh.TriangleIndices.Add(i);

        mesh.Freeze();
        return mesh;
    }

    private static MeshGeometry3D CreatePyramidMesh(double length, double baseRadius)
    {
        var mesh = new MeshGeometry3D();
        double hl = length / 2;

        mesh.Positions.Add(new Point3D(hl, 0, 0));
        mesh.Positions.Add(new Point3D(-hl, -baseRadius, -baseRadius));
        mesh.Positions.Add(new Point3D(-hl, baseRadius, -baseRadius));
        mesh.Positions.Add(new Point3D(-hl, baseRadius, baseRadius));
        mesh.Positions.Add(new Point3D(-hl, -baseRadius, baseRadius));

        int[] indices = { 0, 1, 2, 0, 2, 3, 0, 3, 4, 0, 4, 1, 1, 3, 2, 1, 4, 3 };
        foreach (var i in indices) mesh.TriangleIndices.Add(i);

        mesh.Freeze();
        return mesh;
    }

    private void UpdateModelRotation()
    {
        if (_rotationRoll == null || _rotationPitch == null || _rotationYaw == null) return;
        _rotationRoll.Angle = _targetRoll;
        _rotationPitch.Angle = -_targetPitch;
        _rotationYaw.Angle = _targetYaw;
    }

    public void ReloadModel()
    {
        _modelLoaded = false;
        LoadSTLModel();
    }

    public void ResetCamera()
    {
        if (Viewport3D?.Camera is PerspectiveCamera camera)
        {
            camera.Position = new Point3D(1, -1, 0.5);
            camera.LookDirection = new Vector3D(-1, 1, -0.3);
            camera.UpDirection = new Vector3D(0, 0, 1);
        }
    }
}
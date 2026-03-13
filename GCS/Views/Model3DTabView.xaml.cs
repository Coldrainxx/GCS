using HelixToolkit.Wpf;
using System;
using System.Diagnostics;
using System.IO;
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
        if (!_needsUpdate || !_modelLoaded) return;
        _needsUpdate = false;
        UpdateModelRotation();
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

        var searchPaths = new[]
        {
            Path.Combine(exeDir, filename),
            Path.Combine(exeDir, "Models", justFilename),
            Path.Combine(exeDir, "Assets", justFilename),
            Path.Combine(exeDir, justFilename),
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
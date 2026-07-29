using Microsoft.Web.WebView2.Core;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace GCS.Views;

public partial class MapView : UserControl
{
    private bool _isMapInitialized = false;
    private bool _hasReceivedFirstPosition = false;
    private double _lastLatitude = 0;
    private double _lastLongitude = 0;
    private double _lastHeading = 0;
    private double _lastAltRel = 0;
    private double _lastRoll = 0;
    private double _lastPitch = 0;
    private GCS.ViewModels.MissionViewModel? _missionVm;
    private GCS.ViewModels.MainViewModel? _mainVm;

    // The swarm is pushed on a timer rather than per property change: with several
    // vehicles updating at ~30 fps each, per-change scripting would flood WebView2.
    private System.Windows.Threading.DispatcherTimer? _swarmTimer;
    private bool _swarmWasDrawn;

    public MapView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var window = Window.GetWindow(this);
        if (window?.DataContext is GCS.ViewModels.MainViewModel mainVm)
        {
            _mainVm = mainVm;
            _missionVm = mainVm.Mission;
            _missionVm.WaypointsCleared += OnWaypointsCleared;
            _missionVm.WaypointAdded += OnWaypointAdded;
            _missionVm.WaypointUpdated += OnWaypointUpdated;
            _missionVm.WaypointsRebuilt += OnWaypointsRebuilt;

            _swarmTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(100)   // 10 Hz is plenty for map markers
            };
            _swarmTimer.Tick += OnSwarmTick;
            _swarmTimer.Start();

            mainVm.PropertyChanged += OnMainViewModelPropertyChanged;
        }
    }

    private void OnSwarmTick(object? sender, EventArgs e)
    {
        var vm = _mainVm;
        if (vm == null || !_isMapInitialized) return;

        // Single-UAV mode draws only the active vehicle, the way it always did.
        if (!vm.IsSwarmMode) return;

        if (vm.Swarm.Count > 0)
        {
            UpdateSwarm(vm.Swarm.Vehicles);
            _swarmWasDrawn = true;
        }
        else if (_swarmWasDrawn)
        {
            // Last vehicle went away — take the markers off the map once.
            ClearSwarm();
            _swarmWasDrawn = false;
        }
    }

    private void OnMainViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(GCS.ViewModels.MainViewModel.IsSwarmMode)) return;
        bool on = _mainVm?.IsSwarmMode == true;
        ExecuteScript($"setSwarmMode({(on ? "true" : "false")});");
        if (!on) _swarmWasDrawn = false;
    }

    private void OnWaypointsCleared() => ExecuteScript("clearWaypoints();");

    private void OnWaypointAdded(GCS.ViewModels.MissionItemViewModel wp)
    {
        string type = wp.CommandName;
        string script = string.Format(CultureInfo.InvariantCulture,
            "addWaypoint({0:F7}, {1:F7}, {2}, '{3}', {4:F1});",
            wp.Latitude, wp.Longitude, wp.Sequence, type, wp.Radius);
        ExecuteScript(script);
    }

    private void OnWaypointUpdated(GCS.ViewModels.MissionItemViewModel wp)
    {
        string type = wp.CommandName;
        string script = string.Format(CultureInfo.InvariantCulture,
            "updateWaypoint({0}, {1:F7}, {2:F7}, '{3}', {4:F1});",
            wp.Sequence, wp.Latitude, wp.Longitude, type, wp.Radius);
        ExecuteScript(script);
    }

    private void OnWaypointsRebuilt() => ExecuteScript("updatePathLine();");

    private async void ExecuteScript(string script)
    {
        if (!_isMapInitialized || MapWebView?.CoreWebView2 == null) return;
        try { await MapWebView.CoreWebView2.ExecuteScriptAsync(script); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[MapView] Script error: {ex.Message}"); }
    }

    private async void MapWebView_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await InitializeWebView();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Map initialization failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task InitializeWebView()
    {
        await MapWebView.EnsureCoreWebView2Async(null);
        var web = MapWebView.CoreWebView2;

        // Serve the local Map folder over a virtual https host. This is what lets
        // MapLibre/PMTiles issue HTTP range requests against the offline .pmtiles
        // file - NavigateToString cannot.
        string mapFolder = Path.Combine(AppContext.BaseDirectory, "Map");
        web.SetVirtualHostNameToFolderMapping(
            "gcs.local", mapFolder, CoreWebView2HostResourceAccessKind.Allow);

        string config =
            "window.GCS_CONFIG = { centerLat: 40.4093, centerLon: 49.8671 };";
        await web.AddScriptToExecuteOnDocumentCreatedAsync(config);

        web.WebMessageReceived += OnWebMessageReceived;
        web.NavigationCompleted += OnNavigationCompleted;
        web.Navigate("https://gcs.local/index.html");
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            string message = e.TryGetWebMessageAsString();

            if (message.StartsWith("click:"))
            {
                var coords = message.Substring(6).Split(',');
                if (coords.Length == 2 &&
                    double.TryParse(coords[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double lat) &&
                    double.TryParse(coords[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double lon))
                {
                    _missionVm?.AddWaypoint(lat, lon);
                }
            }
            else if (message.StartsWith("flyto:"))
            {
                var coords = message.Substring(6).Split(',');
                if (coords.Length == 2 &&
                    double.TryParse(coords[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double lat) &&
                    double.TryParse(coords[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double lon))
                {
                    _mainVm?.FlyTo(lat, lon);
                }
            }
            else if (message.StartsWith("drag:"))
            {
                var parts = message.Substring(5).Split(',');
                if (parts.Length == 3 &&
                    int.TryParse(parts[0], out int index) &&
                    double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double lat) &&
                    double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double lon))
                {
                    _missionVm?.UpdateWaypointPosition(index, lat, lon);
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MapView] WebMessage error: {ex.Message}");
        }
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (e.IsSuccess)
        {
            _isMapInitialized = true;
            if (LoadingOverlay != null) LoadingOverlay.Visibility = Visibility.Collapsed;

            // The map starts in single-UAV mode; sync it in case we're already in swarm mode.
            if (_mainVm?.IsSwarmMode == true) ExecuteScript("setSwarmMode(true);");

            if (DataContext is GCS.ViewModels.TelemetryViewModel vm)
            {
                UpdateUAVPosition(vm.Latitude, vm.Longitude, vm.Altitude, vm.AltitudeRelative, vm.Groundspeed, vm.Airspeed, vm.Heading);
                UpdateAttitude(vm.Roll, vm.Pitch);
                UpdateGpsStatus(vm.GpsSatellites, vm.GpsFixString, vm.GpsHdop);
            }
        }
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is GCS.ViewModels.TelemetryViewModel oldVm)
            oldVm.PropertyChanged -= OnTelemetryPropertyChanged;
        if (e.NewValue is GCS.ViewModels.TelemetryViewModel newVm)
            newVm.PropertyChanged += OnTelemetryPropertyChanged;
    }

    private void OnTelemetryPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (DataContext is not GCS.ViewModels.TelemetryViewModel vm) return;

        if (e.PropertyName == nameof(GCS.ViewModels.TelemetryViewModel.Latitude) ||
            e.PropertyName == nameof(GCS.ViewModels.TelemetryViewModel.Longitude) ||
            e.PropertyName == nameof(GCS.ViewModels.TelemetryViewModel.Heading) ||
            e.PropertyName == nameof(GCS.ViewModels.TelemetryViewModel.AltitudeRelative))
        {
            UpdateUAVPosition(vm.Latitude, vm.Longitude, vm.Altitude, vm.AltitudeRelative, vm.Groundspeed, vm.Airspeed, vm.Heading);
        }

        if (e.PropertyName == nameof(GCS.ViewModels.TelemetryViewModel.Roll) ||
            e.PropertyName == nameof(GCS.ViewModels.TelemetryViewModel.Pitch))
        {
            UpdateAttitude(vm.Roll, vm.Pitch);
        }

        if (e.PropertyName == nameof(GCS.ViewModels.TelemetryViewModel.GpsSatellites) ||
            e.PropertyName == nameof(GCS.ViewModels.TelemetryViewModel.GpsFixString))
        {
            UpdateGpsStatus(vm.GpsSatellites, vm.GpsFixString, vm.GpsHdop);
        }
    }

    public void UpdateUAVPosition(double lat, double lon, double alt, double altRel, double groundSpeed, double airSpeed, double heading)
    {
        if (!_isMapInitialized || MapWebView?.CoreWebView2 == null) return;

        bool positionChanged = Math.Abs(lat - _lastLatitude) > 0.000001 || Math.Abs(lon - _lastLongitude) > 0.000001;
        bool headingChanged = Math.Abs(heading - _lastHeading) > 1.0;
        bool altChanged = Math.Abs(altRel - _lastAltRel) > 0.5;  // so straight climbs/descents update the 3D model

        if (!positionChanged && !headingChanged && !altChanged && _hasReceivedFirstPosition) return;

        _lastLatitude = lat;
        _lastLongitude = lon;
        _lastHeading = heading;
        _lastAltRel = altRel;

        bool centerMap = !_hasReceivedFirstPosition && (lat != 0 || lon != 0);
        _hasReceivedFirstPosition = _hasReceivedFirstPosition || (lat != 0 || lon != 0);

        string script = string.Format(CultureInfo.InvariantCulture,
            "updateUAV({0:F7}, {1:F7}, {2:F2}, {3:F2}, {4:F2}, {5:F1}, {6}, {7:F2});",
            lat, lon, alt, groundSpeed, airSpeed, heading, centerMap ? "true" : "false", altRel);
        ExecuteScript(script);
    }

    public void UpdateAttitude(double roll, double pitch)
    {
        if (!_isMapInitialized || MapWebView?.CoreWebView2 == null) return;

        if (Math.Abs(roll - _lastRoll) < 0.5 && Math.Abs(pitch - _lastPitch) < 0.5) return;
        _lastRoll = roll;
        _lastPitch = pitch;

        string script = string.Format(CultureInfo.InvariantCulture,
            "updateAttitude({0:F1}, {1:F1});", roll, pitch);
        ExecuteScript(script);
    }

    public void UpdateGpsStatus(byte satellites, string fixType, float hdop)
    {
        if (!_isMapInitialized || MapWebView?.CoreWebView2 == null) return;

        string script = string.Format(CultureInfo.InvariantCulture,
            "updateGpsStatus({0}, '{1}', {2:F1});",
            satellites, fixType, hdop);
        ExecuteScript(script);
    }

    /// <summary>
    /// Push the whole swarm to the map: one marker (2D) and one 3D model per
    /// vehicle, with the leader highlighted. Vehicles absent from the list are
    /// removed, so this call is the complete picture.
    /// </summary>
    public void UpdateSwarm(IEnumerable<GCS.ViewModels.VehicleViewModel> vehicles)
    {
        if (!_isMapInitialized || MapWebView?.CoreWebView2 == null) return;

        var list = vehicles.ToList();
        var leader = list.FirstOrDefault(v => v.IsLeader && v.HasPosition);

        var sb = new StringBuilder("[");
        bool first = true;
        foreach (var v in list)
        {
            if (!v.HasPosition) continue;   // nothing to draw until it has a fix
            if (!first) sb.Append(',');
            first = false;
            sb.AppendFormat(CultureInfo.InvariantCulture,
                "{{\"id\":{0},\"lat\":{1:F7},\"lon\":{2:F7},\"alt\":{3:F1},\"hdg\":{4:F1}," +
                "\"roll\":{5:F1},\"pitch\":{6:F1},\"leader\":{7},\"active\":{8}",
                v.SystemId, v.Latitude, v.Longitude, v.AltitudeRel, v.Heading,
                v.RollDeg, v.PitchDeg,
                v.IsLeader ? "true" : "false",
                v.IsActive ? "true" : "false");

            // Where this follower's formation station sits on the ground, so the
            // shape can be seen before it's flown.
            if (leader != null && !v.IsLeader && v.Station is { } station)
            {
                var (slat, slon) = GCS.Core.Swarm.FormationGeometry.StationPosition(
                    leader.Latitude, leader.Longitude, leader.Heading, station);
                sb.AppendFormat(CultureInfo.InvariantCulture,
                    ",\"slat\":{0:F7},\"slon\":{1:F7}", slat, slon);
            }
            sb.Append('}');
        }
        sb.Append(']');

        ExecuteScript("updateSwarm(" + sb + ");");
    }

    public void ClearSwarm() => ExecuteScript("clearSwarm();");
}

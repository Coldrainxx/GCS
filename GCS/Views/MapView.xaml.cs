using Microsoft.Web.WebView2.Core;
using System;
using System.Globalization;
using System.IO;
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
        }
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
}

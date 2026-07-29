using CommunityToolkit.Mvvm.Input;
using GCS.Core.Domain;
using GCS.Core.Mission;
using GCS.Core.Settings;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;

namespace GCS.ViewModels;

public class MissionViewModel : ViewModelBase
{
    private IMissionService? _missionService;

    private string _status = "No mission";
    private int _progress;
    private int _total;
    private bool _isConnected;
    private bool _isBusy;
    private float _defaultAltitude = 100;
    private float _defaultRadius = 10;
    private byte _defaultFrame = 3; // MAV_FRAME_GLOBAL_RELATIVE_ALT
    private float _cruiseSpeedMps = 15;
    private int _selectedIndex = -1;

    // Latest vehicle position (for "Set home from vehicle").
    private double _vehLat, _vehLon;
    private float _vehAlt;
    private bool _hasVehiclePosition;
    private int _selectedCommandIndex = 0;
    private MissionItemViewModel? _selectedWaypoint;

    // Statistics
    private double _totalDistance;
    private string _estimatedTime = "--:--";

    public ObservableCollection<MissionItemViewModel> Waypoints { get; } = new();

    #region Properties

    public string Status { get => _status; set => SetProperty(ref _status, value); }
    public int Progress { get => _progress; set => SetProperty(ref _progress, value); }
    public int Total { get => _total; set => SetProperty(ref _total, value); }

    public bool IsConnected
    {
        get => _isConnected;
        set { if (SetProperty(ref _isConnected, value)) CommandManager.InvalidateRequerySuggested(); }
    }

    public bool IsBusy
    {
        get => _isBusy;
        set { if (SetProperty(ref _isBusy, value)) CommandManager.InvalidateRequerySuggested(); }
    }

    public float DefaultAltitude { get => _defaultAltitude; set => SetProperty(ref _defaultAltitude, value); }
    public float DefaultRadius { get => _defaultRadius; set => SetProperty(ref _defaultRadius, value); }

    /// <summary>Altitude frame for new waypoints; changing it applies to all existing waypoints too.</summary>
    public byte DefaultFrame
    {
        get => _defaultFrame;
        set
        {
            if (SetProperty(ref _defaultFrame, value))
                foreach (var wp in Waypoints)
                    wp.Frame = value;
        }
    }

    /// <summary>Average speed used for the ETA estimate.</summary>
    public float CruiseSpeedMps
    {
        get => _cruiseSpeedMps;
        set { if (SetProperty(ref _cruiseSpeedMps, value)) CalculateStatistics(); }
    }

    /// <summary>Frame options for the altitude-frame selector.</summary>
    public static IReadOnlyList<FrameOption> FrameOptions { get; } = new[]
    {
        new FrameOption(3, "Rel alt"),   // MAV_FRAME_GLOBAL_RELATIVE_ALT
        new FrameOption(0, "Abs alt"),   // MAV_FRAME_GLOBAL
        new FrameOption(10, "Terrain"),  // MAV_FRAME_GLOBAL_TERRAIN_ALT
    };

    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            if (SetProperty(ref _selectedIndex, value))
            {
                SelectedWaypoint = (value >= 0 && value < Waypoints.Count) ? Waypoints[value] : null;
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public int SelectedCommandIndex { get => _selectedCommandIndex; set => SetProperty(ref _selectedCommandIndex, value); }

    public MissionItemViewModel? SelectedWaypoint
    {
        get => _selectedWaypoint;
        set { if (SetProperty(ref _selectedWaypoint, value)) OnPropertyChanged(nameof(HasSelection)); }
    }

    public bool HasSelection => SelectedWaypoint != null;

    public double TotalDistance { get => _totalDistance; set => SetProperty(ref _totalDistance, value); }
    public string EstimatedTime { get => _estimatedTime; set => SetProperty(ref _estimatedTime, value); }
    public string TotalDistanceText => TotalDistance > 1000 ? $"{TotalDistance / 1000:F2} km" : $"{TotalDistance:F0} m";

    #endregion

    #region Events

    public event Action? WaypointsCleared;
    public event Action<MissionItemViewModel>? WaypointAdded;
    public event Action<MissionItemViewModel>? WaypointUpdated;
    public event Action? WaypointsRebuilt;

    #endregion

    #region Commands

    public ICommand UploadCommand { get; }
    public ICommand DownloadCommand { get; }
    public ICommand ClearCommand { get; }
    public ICommand ClearOnFCCommand { get; }
    public ICommand RemoveSelectedCommand { get; }
    public ICommand InsertBeforeCommand { get; }
    public ICommand InsertAfterCommand { get; }
    public ICommand MoveUpCommand { get; }
    public ICommand MoveDownCommand { get; }
    public ICommand SetHomeCommand { get; }
    public ICommand SetHomeFromVehicleCommand { get; }
    public ICommand SetCurrentCommand { get; }
    public ICommand ExportCommand { get; }
    public ICommand ImportCommand { get; }
    public ICommand ApplyEditCommand { get; }

    #endregion

    public MissionViewModel()
    {
        var s = SettingsStore.Current;
        _defaultAltitude = s.DefaultAltitude;
        _defaultRadius = s.DefaultRadius;
        _defaultFrame = s.DefaultFrame;
        _cruiseSpeedMps = s.CruiseSpeedMps;

        UploadCommand = new AsyncRelayCommand(UploadAsync, () => IsConnected && !IsBusy);
        DownloadCommand = new AsyncRelayCommand(DownloadAsync, () => IsConnected && !IsBusy);
        ClearCommand = new RelayCommand(ClearAll, () => Waypoints.Count > 0 && !IsBusy);
        ClearOnFCCommand = new AsyncRelayCommand(ClearOnFCAsync, () => IsConnected && !IsBusy);
        RemoveSelectedCommand = new RelayCommand(RemoveSelected, () => SelectedIndex >= 0 && !IsBusy);
        InsertBeforeCommand = new RelayCommand(InsertBefore, () => SelectedIndex >= 0 && !IsBusy);
        InsertAfterCommand = new RelayCommand(InsertAfter, () => SelectedIndex >= 0 && !IsBusy);
        MoveUpCommand = new RelayCommand(MoveUp, () => SelectedIndex > 0 && !IsBusy);
        MoveDownCommand = new RelayCommand(MoveDown, () => SelectedIndex >= 0 && SelectedIndex < Waypoints.Count - 1 && !IsBusy);
        SetHomeCommand = new RelayCommand(SetHomeFromSelected, () => SelectedIndex >= 0 && !IsBusy);
        SetHomeFromVehicleCommand = new RelayCommand(SetHomeFromVehicle, () => _hasVehiclePosition && !IsBusy);
        SetCurrentCommand = new AsyncRelayCommand(SetCurrentAsync, () => IsConnected && SelectedIndex >= 0 && !IsBusy);
        ExportCommand = new RelayCommand(ExportMission, () => Waypoints.Count > 0);
        ImportCommand = new RelayCommand(ImportMission);
        ApplyEditCommand = new RelayCommand(ApplyEdit, () => SelectedWaypoint != null);

        Waypoints.CollectionChanged += (s, e) => CalculateStatistics();
    }

    public void SetMissionService(IMissionService service)
    {
        _missionService = service;
        _missionService.MissionStateChanged += OnMissionStateChanged;
    }

    #region Add/Edit Waypoints

    private ushort GetSelectedCommand() => SelectedCommandIndex switch
    {
        0 => MavCmd.Waypoint,
        1 => MavCmd.Takeoff,
        2 => MavCmd.Land,
        3 => MavCmd.Loiter,
        4 => MavCmd.ReturnToLaunch,
        _ => MavCmd.Waypoint
    };

    public static string GetCommandName(ushort cmd, bool isHome = false)
    {
        if (isHome) return "HOME";
        return cmd switch
        {
            MavCmd.Waypoint => "WP",
            MavCmd.Takeoff => "TKOF",
            MavCmd.Land => "LAND",
            MavCmd.Loiter => "LOIT",
            MavCmd.ReturnToLaunch => "RTL",
            MavCmd.LoiterTurns => "LTRN",
            MavCmd.LoiterTime => "LTIM",
            _ => $"C{cmd}"
        };
    }

    public void AddWaypoint(double lat, double lon)
    {
        if (Application.Current?.Dispatcher?.CheckAccess() == false)
            Application.Current.Dispatcher.BeginInvoke(() => AddWaypointInternal(lat, lon));
        else
            AddWaypointInternal(lat, lon);
    }

    private void AddWaypointInternal(double lat, double lon)
    {
        var item = new MissionItem(
            Sequence: Waypoints.Count,
            Command: GetSelectedCommand(),
            LatitudeDeg: lat,
            LongitudeDeg: lon,
            AltitudeMeters: DefaultAltitude,
            Param2: DefaultRadius,
            Frame: DefaultFrame
        );

        var vm = new MissionItemViewModel(item, isHome: false);
        Waypoints.Add(vm);
        UpdateStatus();
        WaypointAdded?.Invoke(vm);
        CommandManager.InvalidateRequerySuggested();
    }

    public void UpdateWaypointPosition(int index, double lat, double lon)
    {
        if (index < 0 || index >= Waypoints.Count) return;
        var wp = Waypoints[index];
        wp.Latitude = lat;
        wp.Longitude = lon;
        CalculateStatistics();
        WaypointUpdated?.Invoke(wp);
    }

    private void ApplyEdit()
    {
        if (SelectedWaypoint == null) return;
        CalculateStatistics();
        WaypointUpdated?.Invoke(SelectedWaypoint);
        RebuildMapMarkers();
    }

    #endregion

    #region Insert/Reorder

    private void InsertBefore() { if (SelectedIndex >= 0) InsertWaypointAt(SelectedIndex); }
    private void InsertAfter() { if (SelectedIndex >= 0) InsertWaypointAt(SelectedIndex + 1); }

    private void InsertWaypointAt(int index)
    {
        double lat, lon;
        if (Waypoints.Count == 0) { lat = 0; lon = 0; }
        else if (index == 0) { lat = Waypoints[0].Latitude; lon = Waypoints[0].Longitude - 0.001; }
        else if (index >= Waypoints.Count) { lat = Waypoints[^1].Latitude; lon = Waypoints[^1].Longitude + 0.001; }
        else
        {
            var prev = Waypoints[index - 1];
            var curr = Waypoints[index];
            lat = (prev.Latitude + curr.Latitude) / 2;
            lon = (prev.Longitude + curr.Longitude) / 2;
        }

        var item = new MissionItem(index, MavCmd.Waypoint, lat, lon, DefaultAltitude, Param2: DefaultRadius, Frame: DefaultFrame);
        var vm = new MissionItemViewModel(item, isHome: false);
        Waypoints.Insert(index, vm);
        RenumberWaypoints();
        RebuildMapMarkers();
        UpdateStatus();
        SelectedIndex = index;
    }

    private void MoveUp()
    {
        if (SelectedIndex <= 0) return;
        int idx = SelectedIndex;
        var item = Waypoints[idx];
        Waypoints.RemoveAt(idx);
        Waypoints.Insert(idx - 1, item);
        RenumberWaypoints();
        RebuildMapMarkers();
        SelectedIndex = idx - 1;
    }

    private void MoveDown()
    {
        if (SelectedIndex < 0 || SelectedIndex >= Waypoints.Count - 1) return;
        int idx = SelectedIndex;
        var item = Waypoints[idx];
        Waypoints.RemoveAt(idx);
        Waypoints.Insert(idx + 1, item);
        RenumberWaypoints();
        RebuildMapMarkers();
        SelectedIndex = idx + 1;
    }

    /// <summary>Move a waypoint to a new index (drag-and-drop reorder).</summary>
    public void MoveWaypoint(MissionItemViewModel item, int toIndex)
    {
        int from = Waypoints.IndexOf(item);
        if (from < 0) return;
        toIndex = Math.Clamp(toIndex, 0, Waypoints.Count - 1);
        if (from == toIndex) return;

        Waypoints.Move(from, toIndex);
        RenumberWaypoints();
        RebuildMapMarkers();
        SelectedIndex = toIndex;
    }

    private void RemoveSelected()
    {
        if (SelectedIndex < 0 || SelectedIndex >= Waypoints.Count) return;
        Waypoints.RemoveAt(SelectedIndex);
        RenumberWaypoints();
        RebuildMapMarkers();
        UpdateStatus();
        CommandManager.InvalidateRequerySuggested();
    }

    private void SetHomeFromSelected()
    {
        if (SelectedIndex < 0) return;
        var item = Waypoints[SelectedIndex];
        Waypoints.RemoveAt(SelectedIndex);
        Waypoints.Insert(0, item);
        item.IsHome = true;
        item.Command = MavCmd.Waypoint;
        RenumberWaypoints();
        RebuildMapMarkers();
        SelectedIndex = 0;
    }

    private void SetHomeFromVehicle()
    {
        if (!_hasVehiclePosition) return;

        var home = new MissionItem(0, MavCmd.Waypoint, _vehLat, _vehLon, _vehAlt, Frame: 0);
        var vm = new MissionItemViewModel(home, isHome: true);

        if (Waypoints.Count > 0 && Waypoints[0].IsHome) Waypoints[0] = vm;
        else Waypoints.Insert(0, vm);

        RenumberWaypoints();
        RebuildMapMarkers();
        UpdateStatus();
        SelectedIndex = 0;
    }

    private async Task SetCurrentAsync()
    {
        if (_missionService == null || SelectedIndex < 0) return;
        try
        {
            await _missionService.SetCurrentAsync((ushort)SelectedIndex, CancellationToken.None);
            Status = $"Set current waypoint → {SelectedIndex}";
        }
        catch (Exception ex)
        {
            Status = $"Set current failed: {ex.Message}";
        }
    }

    #endregion

    #region Statistics

    private void CalculateStatistics()
    {
        if (Waypoints.Count < 2)
        {
            TotalDistance = 0;
            EstimatedTime = "--:--";
            OnPropertyChanged(nameof(TotalDistanceText));
            return;
        }

        double totalDist = 0;
        for (int i = 1; i < Waypoints.Count; i++)
            totalDist += GeoMath.DistanceMeters(Waypoints[i - 1].Latitude, Waypoints[i - 1].Longitude,
                                                Waypoints[i].Latitude, Waypoints[i].Longitude);

        TotalDistance = totalDist;
        double cruise = CruiseSpeedMps > 0 ? CruiseSpeedMps : 15.0;
        var ts = TimeSpan.FromSeconds(totalDist / cruise);
        EstimatedTime = ts.TotalHours >= 1 ? $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}" : $"{ts.Minutes:D2}:{ts.Seconds:D2}";
        OnPropertyChanged(nameof(TotalDistanceText));
    }

    #endregion

    #region Import/Export

    private void ExportMission()
    {
        var dialog = new SaveFileDialog
        {
            Filter = "Waypoint Files (*.waypoints)|*.waypoints",
            DefaultExt = ".waypoints",
            FileName = $"mission_{DateTime.Now:yyyyMMdd_HHmmss}"
        };

        if (dialog.ShowDialog() == true)
        {
            try
            {
                var items = Waypoints.Select((w, i) => w.ToMissionItem() with { Sequence = i }).ToList();
                File.WriteAllText(dialog.FileName, WaypointFile.Serialize(items));
                Status = $"Exported {items.Count} waypoints";
            }
            catch (Exception ex) { Status = $"Export failed: {ex.Message}"; }
        }
    }

    private void ImportMission()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Waypoint Files (*.waypoints)|*.waypoints|All Files (*.*)|*.*"
        };

        if (dialog.ShowDialog() == true)
        {
            try
            {
                var items = WaypointFile.Parse(File.ReadAllText(dialog.FileName));

                ClearAllInternal();
                foreach (var item in items)
                {
                    bool isHome = item.Sequence == 0 && item.Command == MavCmd.Waypoint;
                    Waypoints.Add(new MissionItemViewModel(item, isHome));
                }
                RebuildMapMarkers();
                UpdateStatus();
                Status = $"Imported {Waypoints.Count} waypoints";
            }
            catch (FormatException) { Status = "Invalid file format"; }
            catch (Exception ex) { Status = $"Import failed: {ex.Message}"; }
        }
    }

    #endregion

    #region Upload/Download/Clear on FC

    public bool HasWaypoints => Waypoints.Count > 0;

    /// <summary>
    /// Build the mission exactly as <see cref="UploadAsync"/> does, so a swarm
    /// upload sends every vehicle an identical list.
    /// </summary>
    public List<GCS.Core.Domain.MissionItem> BuildItems()
    {
        var items = Waypoints.Select((w, i) => w.ToMissionItem() with { Sequence = i }).ToList();
        // Item 0 is home: ArduPilot expects it in the global (absolute-alt) frame.
        if (Waypoints.Count > 0 && Waypoints[0].IsHome) items[0] = items[0] with { Frame = 0 };
        return items;
    }

    /// <summary>Validation warnings for a built item list (empty when clean).</summary>
    public static IReadOnlyList<string> Validate(IReadOnlyList<GCS.Core.Domain.MissionItem> items)
        => MissionValidator.Validate(items);

    /// <summary>
    /// Send a prepared mission to whichever vehicle is currently targeted. Used by
    /// the swarm uploader, which retargets between calls.
    /// </summary>
    public async Task SendItemsAsync(IReadOnlyList<GCS.Core.Domain.MissionItem> items, CancellationToken ct = default)
    {
        if (_missionService == null) throw new InvalidOperationException("Not connected");

        if (items.Count == 0)
            await _missionService.ClearAsync(ct);
        else
            await _missionService.UploadAsync(items.ToList(), ct);
    }

    private async Task UploadAsync()
    {
        if (_missionService == null) return;

        try
        {
            IsBusy = true;

            if (Waypoints.Count == 0)
            {
                Status = "Clearing mission on FC...";
                await _missionService.ClearAsync(CancellationToken.None);
                Status = "Mission cleared on FC";
            }
            else
            {
                var items = Waypoints.Select((w, i) => w.ToMissionItem() with { Sequence = i }).ToList();
                // Item 0 is home: ArduPilot expects it in the global (absolute-alt) frame.
                if (Waypoints[0].IsHome) items[0] = items[0] with { Frame = 0 };

                var warnings = MissionValidator.Validate(items);
                if (warnings.Count > 0)
                {
                    var msg = "Mission warnings:\n\n • " + string.Join("\n • ", warnings) + "\n\nUpload anyway?";
                    if (MessageBox.Show(msg, "Mission validation", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                    {
                        Status = "Upload cancelled";
                        return;
                    }
                }

                Status = "Uploading...";
                await _missionService.UploadAsync(items, CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            Status = $"Upload failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task DownloadAsync()
    {
        if (_missionService == null) return;
        try
        {
            IsBusy = true;
            Status = "Downloading...";
            var items = await _missionService.DownloadAsync(CancellationToken.None);
            Application.Current?.Dispatcher?.BeginInvoke(() =>
            {
                ClearAllInternal();
                foreach (var item in items)
                {
                    bool isHome = item.Sequence == 0 && item.Command == MavCmd.Waypoint;
                    Waypoints.Add(new MissionItemViewModel(item, isHome));
                }
                RebuildMapMarkers();
                UpdateStatus();
                IsBusy = false;
            });
        }
        catch (Exception ex) { Status = $"Download failed: {ex.Message}"; IsBusy = false; }
    }

    private async Task ClearOnFCAsync()
    {
        if (_missionService == null) return;
        try
        {
            IsBusy = true;
            Status = "Clearing mission on FC...";
            await _missionService.ClearAsync(CancellationToken.None);
            Status = "Mission cleared on FC";
            IsBusy = false;
        }
        catch (Exception ex)
        {
            Status = $"Clear failed: {ex.Message}";
            IsBusy = false;
        }
    }

    #endregion

    #region Helpers

    private void ClearAll() { ClearAllInternal(); CommandManager.InvalidateRequerySuggested(); }

    private void ClearAllInternal()
    {
        Waypoints.Clear();
        SelectedIndex = -1;
        SelectedWaypoint = null;
        UpdateStatus();
        WaypointsCleared?.Invoke();
    }

    private void RenumberWaypoints() { for (int i = 0; i < Waypoints.Count; i++) Waypoints[i].Sequence = i; }

    private void RebuildMapMarkers()
    {
        WaypointsCleared?.Invoke();
        foreach (var wp in Waypoints) WaypointAdded?.Invoke(wp);
        WaypointsRebuilt?.Invoke();
    }

    private void UpdateStatus() => Status = Waypoints.Count > 0 ? $"{Waypoints.Count} waypoints" : "No mission";

    private void OnMissionStateChanged(MissionState state)
    {
        Application.Current?.Dispatcher?.BeginInvoke(() =>
        {
            Progress = state.Progress;
            Total = state.Total;
            Status = state.State switch
            {
                MissionTransferState.Uploading => $"Uploading {state.Progress}/{state.Total}...",
                MissionTransferState.Downloading => $"Downloading {state.Progress}/{state.Total}...",
                MissionTransferState.Completed => $"Complete! {state.Total} items",
                MissionTransferState.Failed => $"Failed: {state.ErrorMessage}",
                _ => Status
            };
            if (state.State == MissionTransferState.Completed || state.State == MissionTransferState.Failed) IsBusy = false;
            CommandManager.InvalidateRequerySuggested();
        });
    }

    public void UpdateConnectionState(bool isConnected) => IsConnected = isConnected;

    /// <summary>Latest vehicle position, pushed from telemetry, for "set home from vehicle".</summary>
    public void UpdateVehiclePosition(double lat, double lon, float alt)
    {
        _vehLat = lat; _vehLon = lon; _vehAlt = alt;
        if (!_hasVehiclePosition && (lat != 0 || lon != 0))
        {
            _hasVehiclePosition = true;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    #endregion
}

/// <summary>A MAV_FRAME option for the altitude-frame selector.</summary>
public sealed record FrameOption(byte Value, string Name)
{
    public override string ToString() => Name;
}

/// <summary>Per-command parameter labels for the mission editor.</summary>
public static class MissionParams
{
    public static string Label(ushort command, int param) => command switch
    {
        MavCmd.Waypoint => param switch { 1 => "Hold (s)", 2 => "Radius (m)", 3 => "Pass-by (m)", 4 => "Yaw (deg)", _ => "" },
        MavCmd.Takeoff => param switch { 1 => "Pitch (deg)", 2 => "—", 3 => "—", 4 => "Yaw (deg)", _ => "" },
        MavCmd.Land => param switch { 1 => "Abort alt (m)", 2 => "—", 3 => "—", 4 => "Yaw (deg)", _ => "" },
        MavCmd.Loiter => param switch { 1 => "—", 2 => "—", 3 => "Radius (m)", 4 => "Yaw (deg)", _ => "" },
        MavCmd.LoiterTurns => param switch { 1 => "Turns", 2 => "—", 3 => "Radius (m)", 4 => "Yaw (deg)", _ => "" },
        MavCmd.LoiterTime => param switch { 1 => "Time (s)", 2 => "—", 3 => "Radius (m)", 4 => "Yaw (deg)", _ => "" },
        MavCmd.ReturnToLaunch => param switch { _ => "—" },
        _ => param switch { 1 => "Param 1", 2 => "Param 2", 3 => "Param 3", 4 => "Param 4", _ => "" }
    };
}

public class MissionItemViewModel : ViewModelBase
{
    private int _sequence;
    private ushort _command;
    private double _latitude;
    private double _longitude;
    private float _altitude;
    private float _radius;
    private float _param1;
    private float _param3;
    private float _param4;
    private byte _frame = 3;
    private bool _isHome;

    public int Sequence { get => _sequence; set { if (SetProperty(ref _sequence, value)) OnPropertyChanged(nameof(DisplayIndex)); } }
    public ushort Command
    {
        get => _command;
        set
        {
            if (SetProperty(ref _command, value))
            {
                OnPropertyChanged(nameof(CommandName));
                OnPropertyChanged(nameof(Param1Label));
                OnPropertyChanged(nameof(Param2Label));
                OnPropertyChanged(nameof(Param3Label));
                OnPropertyChanged(nameof(Param4Label));
            }
        }
    }
    public double Latitude { get => _latitude; set => SetProperty(ref _latitude, value); }
    public double Longitude { get => _longitude; set => SetProperty(ref _longitude, value); }
    public float Altitude { get => _altitude; set => SetProperty(ref _altitude, value); }
    public float Radius { get => _radius; set => SetProperty(ref _radius, value); }
    public float Param1 { get => _param1; set => SetProperty(ref _param1, value); }
    public float Param3 { get => _param3; set => SetProperty(ref _param3, value); }
    public float Param4 { get => _param4; set => SetProperty(ref _param4, value); }
    public byte Frame { get => _frame; set => SetProperty(ref _frame, value); }
    public bool IsHome { get => _isHome; set { if (SetProperty(ref _isHome, value)) OnPropertyChanged(nameof(CommandName)); } }

    public string CommandName => MissionViewModel.GetCommandName(Command, IsHome);
    public int DisplayIndex => Sequence;

    public string Param1Label => MissionParams.Label(Command, 1);
    public string Param2Label => MissionParams.Label(Command, 2);
    public string Param3Label => MissionParams.Label(Command, 3);
    public string Param4Label => MissionParams.Label(Command, 4);

    public MissionItemViewModel(MissionItem item, bool isHome = false)
    {
        _sequence = item.Sequence;
        _command = item.Command;
        _latitude = item.LatitudeDeg;
        _longitude = item.LongitudeDeg;
        _altitude = item.AltitudeMeters;
        _radius = item.Param2 > 0 ? item.Param2 : 10;
        _param1 = item.Param1;
        _param3 = item.Param3;
        _param4 = item.Param4;
        _frame = item.Frame;
        _isHome = isHome;
    }

    public MissionItem ToMissionItem() => new(Sequence, Command, Latitude, Longitude, Altitude, Param1, Radius, Param3, Param4, Frame);
}
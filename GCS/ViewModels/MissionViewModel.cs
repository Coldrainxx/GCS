using GCS.Core.Domain;
using GCS.Core.Mission;
using Microsoft.Win32;
using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
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
    private int _selectedIndex = -1;
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
    public ICommand ExportCommand { get; }
    public ICommand ImportCommand { get; }
    public ICommand ApplyEditCommand { get; }

    #endregion

    public MissionViewModel()
    {
        // Upload now works even with 0 waypoints (sends empty mission to clear FC)
        UploadCommand = new RelayCommand(async () => await UploadAsync(), () => IsConnected && !IsBusy);
        DownloadCommand = new RelayCommand(async () => await DownloadAsync(), () => IsConnected && !IsBusy);
        ClearCommand = new RelayCommand(ClearAll, () => Waypoints.Count > 0 && !IsBusy);
        ClearOnFCCommand = new RelayCommand(async () => await ClearOnFCAsync(), () => IsConnected && !IsBusy);
        RemoveSelectedCommand = new RelayCommand(RemoveSelected, () => SelectedIndex >= 0 && !IsBusy);
        InsertBeforeCommand = new RelayCommand(InsertBefore, () => SelectedIndex >= 0 && !IsBusy);
        InsertAfterCommand = new RelayCommand(InsertAfter, () => SelectedIndex >= 0 && !IsBusy);
        MoveUpCommand = new RelayCommand(MoveUp, () => SelectedIndex > 0 && !IsBusy);
        MoveDownCommand = new RelayCommand(MoveDown, () => SelectedIndex >= 0 && SelectedIndex < Waypoints.Count - 1 && !IsBusy);
        SetHomeCommand = new RelayCommand(SetHomeFromSelected, () => SelectedIndex >= 0 && !IsBusy);
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
            Param2: DefaultRadius
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

        var item = new MissionItem(index, MavCmd.Waypoint, lat, lon, DefaultAltitude, Param2: DefaultRadius);
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
            totalDist += CalculateDistance(Waypoints[i - 1].Latitude, Waypoints[i - 1].Longitude,
                                           Waypoints[i].Latitude, Waypoints[i].Longitude);

        TotalDistance = totalDist;
        var ts = TimeSpan.FromSeconds(totalDist / 15.0); // 15 m/s avg
        EstimatedTime = ts.TotalHours >= 1 ? $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}" : $"{ts.Minutes:D2}:{ts.Seconds:D2}";
        OnPropertyChanged(nameof(TotalDistanceText));
    }

    public static double CalculateDistance(double lat1, double lon1, double lat2, double lon2)
    {
        const double R = 6371000;
        double dLat = (lat2 - lat1) * Math.PI / 180;
        double dLon = (lon2 - lon1) * Math.PI / 180;
        double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                   Math.Cos(lat1 * Math.PI / 180) * Math.Cos(lat2 * Math.PI / 180) *
                   Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return R * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    public static double CalculateBearing(double lat1, double lon1, double lat2, double lon2)
    {
        double dLon = (lon2 - lon1) * Math.PI / 180;
        double y = Math.Sin(dLon) * Math.Cos(lat2 * Math.PI / 180);
        double x = Math.Cos(lat1 * Math.PI / 180) * Math.Sin(lat2 * Math.PI / 180) -
                   Math.Sin(lat1 * Math.PI / 180) * Math.Cos(lat2 * Math.PI / 180) * Math.Cos(dLon);
        return (Math.Atan2(y, x) * 180 / Math.PI + 360) % 360;
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
                var sb = new StringBuilder();
                sb.AppendLine("QGC WPL 110");
                foreach (var wp in Waypoints)
                {
                    int current = wp.Sequence == 0 ? 1 : 0;
                    sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                        "{0}\t{1}\t{2}\t{3}\t{4:F6}\t{5:F6}\t{6:F6}\t{7:F6}\t{8:F8}\t{9:F8}\t{10:F6}\t1",
                        wp.Sequence, current, wp.Frame, wp.Command, wp.Param1, wp.Radius, wp.Param3, wp.Param4,
                        wp.Latitude, wp.Longitude, wp.Altitude));
                }
                File.WriteAllText(dialog.FileName, sb.ToString());
                Status = $"Exported {Waypoints.Count} waypoints";
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
                var lines = File.ReadAllLines(dialog.FileName);
                if (lines.Length == 0 || !lines[0].StartsWith("QGC WPL")) { Status = "Invalid file format"; return; }

                ClearAllInternal();
                for (int i = 1; i < lines.Length; i++)
                {
                    var parts = lines[i].Split('\t');
                    if (parts.Length < 12) continue;

                    var item = new MissionItem(
                        int.Parse(parts[0]),
                        ushort.Parse(parts[3]),
                        double.Parse(parts[8], CultureInfo.InvariantCulture),
                        double.Parse(parts[9], CultureInfo.InvariantCulture),
                        float.Parse(parts[10], CultureInfo.InvariantCulture),
                        float.Parse(parts[4], CultureInfo.InvariantCulture),
                        float.Parse(parts[5], CultureInfo.InvariantCulture),
                        float.Parse(parts[6], CultureInfo.InvariantCulture),
                        float.Parse(parts[7], CultureInfo.InvariantCulture),
                        byte.Parse(parts[2])
                    );
                    bool isHome = item.Sequence == 0 && item.Command == MavCmd.Waypoint;
                    Waypoints.Add(new MissionItemViewModel(item, isHome));
                }
                RebuildMapMarkers();
                UpdateStatus();
                Status = $"Imported {Waypoints.Count} waypoints";
            }
            catch (Exception ex) { Status = $"Import failed: {ex.Message}"; }
        }
    }

    #endregion

    #region Upload/Download/Clear on FC

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
                Status = "Uploading...";
                var items = Waypoints.Select((w, i) => w.ToMissionItem() with { Sequence = i }).ToList();
                await _missionService.UploadAsync(items, CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            Status = $"Upload failed: {ex.Message}";
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

    #endregion
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
    public ushort Command { get => _command; set { if (SetProperty(ref _command, value)) OnPropertyChanged(nameof(CommandName)); } }
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
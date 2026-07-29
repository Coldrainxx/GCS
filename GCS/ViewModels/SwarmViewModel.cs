using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using GCS.Core.Mavlink;
using GCS.Core.Swarm;
using GCS.Notifications;
using FlightModeEnum = GCS.Core.Domain.FlightMode;

namespace GCS.ViewModels;

/// <summary>
/// The set of vehicles heard on the link. Creates a <see cref="VehicleViewModel"/>
/// per system id as drones are discovered, and tracks which one is "active" (the
/// vehicle single-vehicle screens act on) and which one is the formation leader.
/// </summary>
public sealed class SwarmViewModel : ViewModelBase, IDisposable
{
    private IMavlinkBackend? _backend;
    private SynchronizationContext? _context;

    public ObservableCollection<VehicleViewModel> Vehicles { get; } = new();

    public int Count => Vehicles.Count;
    public bool HasSwarm => Vehicles.Count > 1;

    private VehicleViewModel? _active;
    /// <summary>
    /// The vehicle PARAMS / SETUP / FailSafe and the action bar act on. Setting it
    /// re-points the backend so un-targeted commands go to this drone.
    /// </summary>
    public VehicleViewModel? ActiveVehicle
    {
        get => _active;
        set
        {
            if (_active == value) return;
            if (_active != null) _active.IsActive = false;
            _active = value;
            if (_active != null)
            {
                _active.IsActive = true;
                _backend?.SetPrimaryVehicle(_active.SystemId);
            }
            OnPropertyChanged();
            OnPropertyChanged(nameof(ActiveSystemId));
        }
    }

    public byte ActiveSystemId => _active?.SystemId ?? 0;

    private VehicleViewModel? _leader;
    /// <summary>The formation leader; followers hold an offset from this vehicle.</summary>
    public VehicleViewModel? Leader
    {
        get => _leader;
        set
        {
            if (_leader == value) return;
            if (_leader != null) _leader.IsLeader = false;
            _leader = value;
            if (_leader != null) _leader.IsLeader = true;
            OnPropertyChanged();
            OnPropertyChanged(nameof(LeaderSystemId));
            PreviewFormation();   // stations are relative to the leader
        }
    }

    public byte LeaderSystemId => _leader?.SystemId ?? 0;

    private string _status = "Not connected";
    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    // ═══════════════════════════════════════════════════════════════
    // Fleet health watchdog
    // ═══════════════════════════════════════════════════════════════

    private readonly System.Windows.Threading.DispatcherTimer _healthTimer;
    private readonly System.Collections.Generic.Dictionary<byte, VehicleAlertLevel> _lastAlertLevel = new();

    private string _fleetHealthText = "";
    /// <summary>Summary of vehicles needing attention, "" when the fleet is clean.</summary>
    public string FleetHealthText
    {
        get => _fleetHealthText;
        private set { if (SetProperty(ref _fleetHealthText, value)) OnPropertyChanged(nameof(FleetHasAlert)); }
    }

    public bool FleetHasAlert => _fleetHealthText.Length > 0;

    private void OnHealthTick(object? sender, EventArgs e)
    {
        var now = DateTime.UtcNow;
        int warnings = 0, criticals = 0;

        foreach (var v in Vehicles)
        {
            var previous = _lastAlertLevel.TryGetValue(v.SystemId, out var p) ? p : VehicleAlertLevel.None;
            v.EvaluateHealth(now);
            var current = v.AlertLevel;
            _lastAlertLevel[v.SystemId] = current;

            if (current == VehicleAlertLevel.Critical) criticals++;
            else if (current == VehicleAlertLevel.Warning) warnings++;

            // Announce only on escalation, so a persistent condition doesn't
            // spam a toast every second.
            if (current > previous)
            {
                string message = $"{v.Name}: {v.AlertText}";
                if (current == VehicleAlertLevel.Critical) Notifier.Error(message);
                else Notifier.Warning(message);
            }
        }

        // Forget vehicles that have left, so rejoining re-announces.
        foreach (var id in _lastAlertLevel.Keys.Where(k => Vehicles.All(v => v.SystemId != k)).ToList())
            _lastAlertLevel.Remove(id);

        FleetHealthText = (criticals, warnings) switch
        {
            (0, 0) => "",
            (0, var w) => $"⚠ {w} vehicle(s) need attention",
            (var c, 0) => $"✖ {c} vehicle(s) critical",
            var (c, w) => $"✖ {c} critical · ⚠ {w} warning",
        };
    }

    // ═══════════════════════════════════════════════════════════════
    // Group commands — act on every vehicle on the link
    // ═══════════════════════════════════════════════════════════════

    public ObservableCollection<FlightModeItem> AvailableModes { get; } = new()
    {
        new FlightModeItem(FlightModeEnum.Auto, "AUTO", false),
        new FlightModeItem(FlightModeEnum.Guided, "GUIDED", false),
        new FlightModeItem(FlightModeEnum.Loiter, "LOITER", false),
        new FlightModeItem(FlightModeEnum.Rtl, "RTL", false),
        new FlightModeItem(FlightModeEnum.Fbwa, "FBW-A", false),
        new FlightModeItem(FlightModeEnum.Cruise, "CRUISE", false),
        new FlightModeItem(FlightModeEnum.QHover, "QHOVER", true),
        new FlightModeItem(FlightModeEnum.QLoiter, "QLOITER", true),
        new FlightModeItem(FlightModeEnum.QLand, "QLAND", true),
        new FlightModeItem(FlightModeEnum.QRtl, "QRTL", true),
    };

    private int _selectedModeIndex = -1;
    public int SelectedModeIndex
    {
        get => _selectedModeIndex;
        set => SetProperty(ref _selectedModeIndex, value);
    }

    // ═══════════════════════════════════════════════════════════════
    // Formation (ArduPilot Follow)
    // ═══════════════════════════════════════════════════════════════

    public Array FormationTypes => Enum.GetValues(typeof(FormationType));

    private FormationType _formation = FormationType.Vee;
    public FormationType SelectedFormation
    {
        get => _formation;
        set { if (SetProperty(ref _formation, value)) PreviewFormation(); }
    }

    private double _spacingM = 50;
    public double SpacingM
    {
        get => _spacingM;
        set { if (SetProperty(ref _spacingM, value)) PreviewFormation(); }
    }

    private double _verticalStepM = 5;
    public double VerticalStepM
    {
        get => _verticalStepM;
        set { if (SetProperty(ref _verticalStepM, value)) PreviewFormation(); }
    }

    private double _maxDistanceM = 0;
    /// <summary>FOLL_DIST_MAX — followers give up beyond this. 0 disables the check.</summary>
    public double MaxDistanceM
    {
        get => _maxDistanceM;
        set => SetProperty(ref _maxDistanceM, value);
    }

    public ICommand ApplyFormationCommand { get; }
    public ICommand StopFollowingCommand { get; }

    // ═══════════════════════════════════════════════════════════════
    // Swarm mission upload
    // ═══════════════════════════════════════════════════════════════

    // Uploads the currently planned mission to one vehicle. Supplied by
    // MainViewModel, which owns retargeting the mission protocol.
    private Func<byte, Task>? _uploadMissionTo;
    private Func<bool>? _missionHasWaypoints;

    public void SetMissionUploader(Func<byte, Task> uploadTo, Func<bool> hasWaypoints)
    {
        _uploadMissionTo = uploadTo;
        _missionHasWaypoints = hasWaypoints;
    }

    public ICommand UploadMissionToLeaderCommand { get; }
    public ICommand UploadMissionToAllCommand { get; }

    private async Task UploadMissionToLeaderAsync()
    {
        var leader = Leader;
        if (leader == null) { Status = "No leader selected"; return; }

        if (!Confirm(
                $"Upload the planned mission to the leader (UAV {leader.SystemId})?\n\n" +
                "Followers stay on station and will fly the route with it.",
                "Confirm mission upload")) return;

        await UploadMissionAsync(new[] { leader });
    }

    private async Task UploadMissionToAllAsync()
    {
        var targets = Vehicles.ToList();
        if (targets.Count == 0) return;

        if (!Confirm(
                $"Upload the SAME mission to all {targets.Count} vehicle(s)?\n\n" +
                "Every drone will fly the identical route — they will not hold formation, " +
                "so make sure the route keeps them separated.",
                "Confirm mission upload to all")) return;

        await UploadMissionAsync(targets);
    }

    /// <summary>
    /// Upload sequentially. The MAVLink mission protocol is a stateful handshake,
    /// so two transfers at once would corrupt each other — never parallelise this.
    /// </summary>
    private async Task UploadMissionAsync(IReadOnlyList<VehicleViewModel> targets)
    {
        if (_uploadMissionTo == null) { Status = "Not connected"; return; }

        int done = 0;
        var failed = new System.Collections.Generic.List<byte>();

        foreach (var v in targets)
        {
            Status = $"Uploading mission to UAV {v.SystemId} ({done + 1} of {targets.Count})…";
            try
            {
                await _uploadMissionTo(v.SystemId);
                done++;
            }
            catch (Exception ex)
            {
                failed.Add(v.SystemId);
                System.Diagnostics.Debug.WriteLine($"[Swarm] Mission upload -> {v.SystemId} failed: {ex.Message}");
            }
        }

        Status = failed.Count == 0
            ? $"Mission uploaded to {done} vehicle(s)"
            : $"Mission: {done} uploaded, failed for UAV {string.Join(", ", failed)}";

        if (failed.Count == 0) Notifier.Success(Status); else Notifier.Warning(Status);
    }

    /// <summary>Followers in stable order — the leader keeps no station of its own.</summary>
    private System.Collections.Generic.List<VehicleViewModel> Followers() =>
        Vehicles.Where(v => !v.IsLeader).ToList();

    /// <summary>Recompute stations so the table shows where each follower will sit.</summary>
    private void PreviewFormation()
    {
        var followers = Followers();
        var offsets = FormationGeometry.Compute(
            SelectedFormation, followers.Count, SpacingM, VerticalStepM);

        for (int i = 0; i < followers.Count; i++)
            followers[i].Station = offsets[i];

        foreach (var v in Vehicles.Where(v => v.IsLeader))
            v.Station = null;
    }

    private async Task ApplyFormationAsync()
    {
        var backend = _backend;
        var leader = Leader;
        if (backend == null || leader == null) { Status = "No leader selected"; return; }

        var followers = Followers();
        if (followers.Count == 0) { Status = "No followers to configure"; return; }

        if (!Confirm(
                $"Put {followers.Count} follower(s) into {FormationGeometry.DisplayName(SelectedFormation)} " +
                $"behind UAV {leader.SystemId}?\n\n" +
                $"Spacing {SpacingM:F0} m, vertical step {VerticalStepM:F0} m.\n\n" +
                "This writes FOLL_* parameters and enables following on each vehicle.",
                "Confirm formation")) return;

        PreviewFormation();

        int configured = 0;
        var failed = new System.Collections.Generic.List<byte>();

        foreach (var follower in followers)
        {
            if (follower.Station is not { } station) continue;
            var parameters = FollowConfiguration.ForFollower(
                leader.SystemId, station, FollowYawBehaviour.SameAsLeadVehicle, (float)MaxDistanceM);

            try
            {
                foreach (var p in parameters)
                {
                    await backend.SetParameterAsync(p.Key, p.Value, targetSystem: follower.SystemId);
                    await Task.Delay(25);   // shared radio link: don't burst
                }
                configured++;
            }
            catch (Exception ex)
            {
                failed.Add(follower.SystemId);
                System.Diagnostics.Debug.WriteLine($"[Swarm] Follow config -> {follower.SystemId} failed: {ex.Message}");
            }
        }

        Status = failed.Count == 0
            ? $"{FormationGeometry.DisplayName(SelectedFormation)} applied to {configured} follower(s). " +
              "Put them into Follow mode to engage."
            : $"Formation: {configured} configured, failed for UAV {string.Join(", ", failed)}";

        if (failed.Count == 0) Notifier.Success(Status); else Notifier.Warning(Status);
    }

    private async Task StopFollowingAsync()
    {
        if (!Confirm("Disable following on all vehicles?\n\nEach follower stops holding station.",
                     "Confirm stop following")) return;

        await ForEachVehicle("Stop following", async (backend, sysid) =>
        {
            foreach (var p in FollowConfiguration.Disable())
                await backend.SetParameterAsync(p.Key, p.Value, targetSystem: sysid);
        });
    }

    public ICommand ArmAllCommand { get; }
    public ICommand DisarmAllCommand { get; }
    public ICommand RtlAllCommand { get; }
    public ICommand SetModeAllCommand { get; }
    public ICommand MakeActiveCommand { get; }
    public ICommand MakeLeaderCommand { get; }

    public SwarmViewModel()
    {
        ArmAllCommand = new AsyncRelayCommand(ArmAllAsync, () => Count > 0);
        DisarmAllCommand = new AsyncRelayCommand(DisarmAllAsync, () => Count > 0);
        RtlAllCommand = new AsyncRelayCommand(() => SetModeAllAsync(FlightModeEnum.Rtl, "RTL"), () => Count > 0);
        SetModeAllCommand = new AsyncRelayCommand(SetSelectedModeAllAsync,
            () => Count > 0 && SelectedModeIndex >= 0);
        MakeActiveCommand = new RelayCommand<VehicleViewModel>(v => { if (v != null) ActiveVehicle = v; });
        MakeLeaderCommand = new RelayCommand<VehicleViewModel>(v => { if (v != null) Leader = v; });

        ApplyFormationCommand = new AsyncRelayCommand(ApplyFormationAsync, () => Count > 1);
        StopFollowingCommand = new AsyncRelayCommand(StopFollowingAsync, () => Count > 0);

        UploadMissionToLeaderCommand = new AsyncRelayCommand(
            UploadMissionToLeaderAsync, () => Leader != null && _missionHasWaypoints?.Invoke() == true);
        UploadMissionToAllCommand = new AsyncRelayCommand(
            UploadMissionToAllAsync, () => Count > 0 && _missionHasWaypoints?.Invoke() == true);

        // Watches every vehicle, not just the active one — a follower in trouble
        // must still raise an alert.
        _healthTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _healthTimer.Tick += OnHealthTick;
        _healthTimer.Start();
    }

    private async Task ArmAllAsync()
    {
        if (!Confirm($"ARM all {Count} vehicle(s)?\n\nEvery drone's motors may start spinning immediately.",
                     "Confirm ARM ALL")) return;
        await ForEachVehicle("ARM", (backend, sysid) => backend.SendArmDisarmAsync(true, targetSystem: sysid));
    }

    private async Task DisarmAllAsync()
    {
        bool anyArmed = Vehicles.Any(v => v.IsArmed);
        string message = anyArmed
            ? $"DISARM all {Count} vehicle(s)?\n\n⚠ At least one vehicle is ARMED. If it is flying, the motors WILL STOP."
            : $"DISARM all {Count} vehicle(s)?";
        if (!Confirm(message, "Confirm DISARM ALL")) return;
        await ForEachVehicle("DISARM", (backend, sysid) => backend.SendArmDisarmAsync(false, targetSystem: sysid));
    }

    private async Task SetSelectedModeAllAsync()
    {
        if (SelectedModeIndex < 0 || SelectedModeIndex >= AvailableModes.Count) return;
        var item = AvailableModes[SelectedModeIndex];
        await SetModeAllAsync(item.Mode, item.DisplayName);
    }

    private async Task SetModeAllAsync(FlightModeEnum mode, string label)
    {
        if (!Confirm($"Set ALL {Count} vehicle(s) to {label}?", $"Confirm {label} ALL")) return;

        uint customMode = ArdupilotPlaneFlightModeMapper.ToCustomMode(mode);
        await ForEachVehicle(label, (backend, sysid) =>
        {
            // Base mode must reflect that vehicle's own armed state.
            var vehicle = Vehicles.FirstOrDefault(v => v.SystemId == sysid);
            byte baseMode = (byte)(vehicle?.IsArmed == true ? 0xD1 : 0x51);
            return backend.SendSetModeAsync(baseMode, customMode, targetSystem: sysid);
        });
    }

    /// <summary>Send one command to every known vehicle, reporting partial failures.</summary>
    private async Task ForEachVehicle(string label, Func<IMavlinkBackend, byte, Task> send)
    {
        var backend = _backend;
        if (backend == null) { Status = "Not connected"; return; }

        var targets = Vehicles.Select(v => v.SystemId).ToList();
        int ok = 0;
        var failed = new System.Collections.Generic.List<byte>();

        foreach (var sysid in targets)
        {
            try
            {
                await send(backend, sysid);
                ok++;
                await Task.Delay(30); // don't burst a shared radio link
            }
            catch (Exception ex)
            {
                failed.Add(sysid);
                System.Diagnostics.Debug.WriteLine($"[Swarm] {label} -> {sysid} failed: {ex.Message}");
            }
        }

        if (failed.Count == 0)
        {
            Status = $"{label} sent to {ok} vehicle(s)";
            Notifier.Success(Status);
        }
        else
        {
            Status = $"{label}: {ok} sent, failed for UAV {string.Join(", ", failed)}";
            Notifier.Warning(Status);
        }
    }

    private static bool Confirm(string message, string title) =>
        MessageBox.Show(message, title, MessageBoxButton.YesNo,
                        MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;

    /// <summary>Start tracking vehicles on a newly connected link.</summary>
    public void Attach(IMavlinkBackend backend, SynchronizationContext? context)
    {
        Detach();
        _backend = backend;
        _context = context;
        Status = "Listening for vehicles…";

        backend.VehicleDiscovered += OnVehicleDiscovered;
        backend.VehicleLost += OnVehicleLost;

        // Anything already heard before we attached.
        foreach (var sysid in backend.KnownSystems)
            OnVehicleDiscovered(sysid);
    }

    public void Detach()
    {
        if (_backend != null)
        {
            _backend.VehicleDiscovered -= OnVehicleDiscovered;
            _backend.VehicleLost -= OnVehicleLost;
            _backend = null;
        }

        OnUi(() =>
        {
            foreach (var v in Vehicles) v.Dispose();
            Vehicles.Clear();
            ActiveVehicle = null;
            Leader = null;
            _lastAlertLevel.Clear();
            FleetHealthText = "";
            RaiseCounts();
        });
    }

    private void OnVehicleDiscovered(byte systemId)
    {
        var backend = _backend;
        if (backend == null) return;

        OnUi(() =>
        {
            if (Vehicles.Any(v => v.SystemId == systemId)) return;

            var vehicle = new VehicleViewModel(backend, systemId, _context);
            // Keep the list in system-id order so the UI doesn't reshuffle.
            int index = 0;
            while (index < Vehicles.Count && Vehicles[index].SystemId < systemId) index++;
            Vehicles.Insert(index, vehicle);

            // First drone heard becomes both active and leader by default.
            ActiveVehicle ??= vehicle;
            Leader ??= vehicle;
            RaiseCounts();
        });
    }

    private void OnVehicleLost(byte systemId)
    {
        OnUi(() =>
        {
            var vehicle = Vehicles.FirstOrDefault(v => v.SystemId == systemId);
            if (vehicle == null) return;

            Vehicles.Remove(vehicle);
            vehicle.Dispose();

            // Don't leave the app pointing at a vehicle that's gone.
            if (ActiveVehicle == vehicle) ActiveVehicle = Vehicles.FirstOrDefault();
            if (Leader == vehicle) Leader = Vehicles.FirstOrDefault();
            RaiseCounts();
        });
    }

    private void RaiseCounts()
    {
        OnPropertyChanged(nameof(Count));
        OnPropertyChanged(nameof(HasSwarm));
        OnPropertyChanged(nameof(CountText));
        CommandManager.InvalidateRequerySuggested();
        PreviewFormation();   // the roster changed, so stations shift
    }

    public string CountText => Count switch
    {
        0 => "No vehicles",
        1 => "1 vehicle",
        _ => $"{Count} vehicles"
    };

    // Discovery events arrive on the transport thread; the collection is bound to the UI.
    private static void OnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess()) dispatcher.Invoke(action);
        else action();
    }

    public void Dispose() => Detach();
}

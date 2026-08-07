using GCS.Core.Domain;
using GCS.Core.Mavlink;
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows.Input;
using FlightModeEnum = GCS.Core.Domain.FlightMode;

namespace GCS.ViewModels;

public class ActionsViewModel : ViewModelBase
{
    private readonly IMavlinkBackend _backend;

    private string _flightMode = "UNKNOWN";
    private int _selectedModeIndex = -1;
    private bool _isConnected;
    private bool _isArmed;
    private bool _isVtolMode;
    private string _lastCommandResult = "";

    public string FlightMode
    {
        get => _flightMode;
        set
        {
            if (SetProperty(ref _flightMode, value))
            {
                IsVtolMode = value.StartsWith("Q");
            }
        }
    }

    public int SelectedModeIndex
    {
        get => _selectedModeIndex;
        set => SetProperty(ref _selectedModeIndex, value);
    }

    public bool IsConnected
    {
        get => _isConnected;
        set
        {
            if (SetProperty(ref _isConnected, value))
            {
                System.Windows.Application.Current?.Dispatcher?.Invoke(() =>
                {
                    CommandManager.InvalidateRequerySuggested();
                });
            }
        }
    }

    public bool IsArmed
    {
        get => _isArmed;
        set
        {
            if (SetProperty(ref _isArmed, value))
            {
                OnPropertyChanged(nameof(ArmStatusText));
                System.Windows.Application.Current?.Dispatcher?.Invoke(() =>
                {
                    CommandManager.InvalidateRequerySuggested();
                });
            }
        }
    }

    public bool IsVtolMode
    {
        get => _isVtolMode;
        set => SetProperty(ref _isVtolMode, value);
    }

    public string ArmStatusText => IsArmed ? "ARMED" : "DISARMED";

    public string LastCommandResult
    {
        get => _lastCommandResult;
        set => SetProperty(ref _lastCommandResult, value);
    }

    public ObservableCollection<FlightModeItem> AvailableModes { get; } = new();

    // ═══════════════════════════════════════════════════════════════
    // Commands
    // ═══════════════════════════════════════════════════════════════

    public ICommand ArmCommand { get; }
    public ICommand DisarmCommand { get; }
    public ICommand RtlCommand { get; }
    public ICommand LoiterCommand { get; }
    public ICommand AutoCommand { get; }
    public ICommand GuidedCommand { get; }
    public ICommand CruiseCommand { get; }
    public ICommand QHoverCommand { get; }
    public ICommand QLoiterCommand { get; }
    public ICommand QLandCommand { get; }
    public ICommand QRtlCommand { get; }
    public ICommand SetModeCommand { get; }

    public ActionsViewModel(IMavlinkBackend backend)
    {
        _backend = backend;

        InitializeModes();

        ArmCommand = new AsyncRelayCommand(ArmAsync, CanExecuteConnected);
        DisarmCommand = new AsyncRelayCommand(DisarmAsync, CanExecuteConnected);

        RtlCommand = new AsyncRelayCommand(() => SetModeAsync(FlightModeEnum.Rtl), CanExecuteConnected);
        LoiterCommand = new AsyncRelayCommand(() => SetModeAsync(FlightModeEnum.Loiter), CanExecuteConnected);
        AutoCommand = new AsyncRelayCommand(() => SetModeAsync(FlightModeEnum.Auto), CanExecuteConnected);
        GuidedCommand = new AsyncRelayCommand(() => SetModeAsync(FlightModeEnum.Guided), CanExecuteConnected);
        CruiseCommand = new AsyncRelayCommand(() => SetModeAsync(FlightModeEnum.Cruise), CanExecuteConnected);

        QHoverCommand = new AsyncRelayCommand(() => SetModeAsync(FlightModeEnum.QHover), CanExecuteConnected);
        QLoiterCommand = new AsyncRelayCommand(() => SetModeAsync(FlightModeEnum.QLoiter), CanExecuteConnected);
        QLandCommand = new AsyncRelayCommand(() => SetModeAsync(FlightModeEnum.QLand), CanExecuteConnected);
        QRtlCommand = new AsyncRelayCommand(() => SetModeAsync(FlightModeEnum.QRtl), CanExecuteConnected);

        SetModeCommand = new AsyncRelayCommand(SetSelectedModeAsync, () => IsConnected && SelectedModeIndex >= 0);

        _backend.ConnectionStateChanged += OnConnectionStateChanged;

        Debug.WriteLine("[ActionsViewModel] Created and subscribed to ConnectionStateChanged");
    }

    private bool CanExecuteConnected() => IsConnected;

    private void OnConnectionStateChanged(ConnectionState state)
    {
        IsConnected = state.IsConnected;
    }

    private void InitializeModes()
    {
        // Fixed-wing modes
        AvailableModes.Add(new FlightModeItem(FlightModeEnum.Manual, "MANUAL", false));
        AvailableModes.Add(new FlightModeItem(FlightModeEnum.Stabilize, "STABILIZE", false));
        AvailableModes.Add(new FlightModeItem(FlightModeEnum.Fbwa, "FBW-A", false));
        AvailableModes.Add(new FlightModeItem(FlightModeEnum.Fbwb, "FBW-B", false));
        AvailableModes.Add(new FlightModeItem(FlightModeEnum.Cruise, "CRUISE", false));
        AvailableModes.Add(new FlightModeItem(FlightModeEnum.Auto, "AUTO", false));
        AvailableModes.Add(new FlightModeItem(FlightModeEnum.Rtl, "RTL", false));
        AvailableModes.Add(new FlightModeItem(FlightModeEnum.Loiter, "LOITER", false));
        AvailableModes.Add(new FlightModeItem(FlightModeEnum.Guided, "GUIDED", false));
        AvailableModes.Add(new FlightModeItem(FlightModeEnum.Circle, "CIRCLE", false));
        AvailableModes.Add(new FlightModeItem(FlightModeEnum.Autotune, "AUTOTUNE", false));

        // VTOL modes
        AvailableModes.Add(new FlightModeItem(FlightModeEnum.QStabilize, "QSTABILIZE", true));
        AvailableModes.Add(new FlightModeItem(FlightModeEnum.QHover, "QHOVER", true));
        AvailableModes.Add(new FlightModeItem(FlightModeEnum.QLoiter, "QLOITER", true));
        AvailableModes.Add(new FlightModeItem(FlightModeEnum.QLand, "QLAND", true));
        AvailableModes.Add(new FlightModeItem(FlightModeEnum.QRtl, "QRTL", true));
        AvailableModes.Add(new FlightModeItem(FlightModeEnum.QAutotune, "QAUTOTUNE", true));
        AvailableModes.Add(new FlightModeItem(FlightModeEnum.QAcro, "QACRO", true));
    }

    /// <summary>
    /// Replace the mode list with the connected vehicle's own modes. The startup
    /// list is ArduPlane's; on a Copter every entry in it is either absent or means
    /// something else.
    /// </summary>
    private void RebuildModes()
    {
        if (_vehicleKind is GCS.Core.Mavlink.VehicleKind.Plane or GCS.Core.Mavlink.VehicleKind.Unknown)
            return;   // the startup list already is the plane list

        int previous = SelectedModeIndex;

        AvailableModes.Clear();
        foreach (var (name, _) in GCS.Core.Mavlink.ArdupilotFlightModes.ModesFor(_vehicleKind))
        {
            // The enum only spans ArduPlane, so it is not meaningful here; the name
            // is what SetModeAsync resolves against.
            AvailableModes.Add(new FlightModeItem(FlightModeEnum.Unknown, name, false));
        }

        SelectedModeIndex = previous >= 0 && previous < AvailableModes.Count ? previous : -1;
        Debug.WriteLine($"[ActionsViewModel] Mode list rebuilt for {_vehicleKind}");
    }

    private async Task ArmAsync()
    {
        // Arming spins motors on a QuadPlane — always confirm.
        var confirm = System.Windows.MessageBox.Show(
            "ARM the vehicle?\n\nMotors may start spinning immediately.",
            "Confirm ARM",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning,
            System.Windows.MessageBoxResult.No);
        if (confirm != System.Windows.MessageBoxResult.Yes) return;

        try
        {
            LastCommandResult = "Sending ARM...";
            Debug.WriteLine("[ActionsViewModel] Sending ARM command...");
            await _backend.SendArmDisarmAsync(arm: true);
            LastCommandResult = "ARM sent";
        }
        catch (Exception ex)
        {
            LastCommandResult = $"ARM failed: {ex.Message}";
            Debug.WriteLine($"[ActionsViewModel] ARM failed: {ex.Message}");
        }
    }

    private async Task DisarmAsync()
    {
        // Disarming in flight stops the motors — confirm hard when armed.
        string message = IsArmed
            ? "DISARM the vehicle?\n\n⚠ The vehicle is ARMED. If it is flying, the motors WILL STOP."
            : "DISARM the vehicle?";
        var confirm = System.Windows.MessageBox.Show(
            message,
            "Confirm DISARM",
            System.Windows.MessageBoxButton.YesNo,
            IsArmed ? System.Windows.MessageBoxImage.Stop : System.Windows.MessageBoxImage.Question,
            System.Windows.MessageBoxResult.No);
        if (confirm != System.Windows.MessageBoxResult.Yes) return;

        try
        {
            LastCommandResult = "Sending DISARM...";
            Debug.WriteLine("[ActionsViewModel] Sending DISARM command...");
            await _backend.SendArmDisarmAsync(arm: false);
            LastCommandResult = "DISARM sent";
        }
        catch (Exception ex)
        {
            LastCommandResult = $"DISARM failed: {ex.Message}";
            Debug.WriteLine($"[ActionsViewModel] DISARM failed: {ex.Message}");
        }
    }

    private Task SetModeAsync(FlightModeEnum mode) => SetModeByNameAsync(mode.ToString().ToUpper());

    private async Task SetModeByNameAsync(string modeName)
    {
        try
        {
            LastCommandResult = $"Setting {modeName}...";
            Debug.WriteLine($"[ActionsViewModel] Setting mode to {modeName}...");

            // Encode for the connected vehicle family, not always as a plane: the
            // same number means different modes, so asking a Copter for RTL with
            // plane numbering would select DRIFT instead.
            uint? resolved = ArdupilotFlightModes.ToCustomMode(_vehicleKind, modeName);

            if (resolved is null)
            {
                LastCommandResult = $"{modeName} is not available on this vehicle";
                Debug.WriteLine($"[ActionsViewModel] {modeName} unsupported for {_vehicleKind}");
                return;
            }

            byte baseMode = (byte)(IsArmed ? 0xD1 : 0x51);

            await _backend.SendSetModeAsync(baseMode, resolved.Value);
            LastCommandResult = $"{modeName} sent";
        }
        catch (Exception ex)
        {
            LastCommandResult = $"SetMode failed: {ex.Message}";
            Debug.WriteLine($"[ActionsViewModel] SetMode failed: {ex.Message}");
        }
    }

    private async Task SetSelectedModeAsync()
    {
        if (SelectedModeIndex < 0 || SelectedModeIndex >= AvailableModes.Count) return;

        // By display name, not the enum: on a Copter the list is built from that
        // vehicle's own modes and the plane enum cannot represent them.
        await SetModeByNameAsync(AvailableModes[SelectedModeIndex].DisplayName);
    }

    private GCS.Core.Mavlink.VehicleKind _vehicleKind = GCS.Core.Mavlink.VehicleKind.Unknown;

    /// <summary>
    /// Whether the VTOL quick-access buttons apply. They send QHOVER/QLOITER/QLAND/
    /// QRTL, which exist only on ArduPlane — on a copter they would be refused, so
    /// the panel hides them rather than offering dead controls.
    /// </summary>
    public bool IsVtolVehicle =>
        _vehicleKind is GCS.Core.Mavlink.VehicleKind.Plane or GCS.Core.Mavlink.VehicleKind.Unknown;

    public string VehicleKindText => _vehicleKind switch
    {
        GCS.Core.Mavlink.VehicleKind.Copter => "Multirotor",
        GCS.Core.Mavlink.VehicleKind.Plane => "Fixed wing / VTOL",
        GCS.Core.Mavlink.VehicleKind.Rover => "Rover",
        GCS.Core.Mavlink.VehicleKind.Submarine => "Submarine",
        _ => "",
    };

    public void UpdateFromVehicleState(VehicleState state)
    {
        // The mode list and the numbers sent both depend on what kind of vehicle
        // this is, which only the heartbeat can tell us.
        if (state.Kind != _vehicleKind && state.Kind != GCS.Core.Mavlink.VehicleKind.Unknown)
        {
            _vehicleKind = state.Kind;
            RebuildModes();
            OnPropertyChanged(nameof(IsVtolVehicle));
            OnPropertyChanged(nameof(VehicleKindText));
        }

        if (!string.IsNullOrEmpty(state.FlightModeName))
        {
            FlightMode = state.FlightModeName;
        }
        else if (state.FlightMode.HasValue)
        {
            FlightMode = state.FlightMode.Value.ToString().ToUpper();
        }

        if (state.Connection != null)
        {
            IsConnected = state.Connection.IsConnected;
        }
    }

    public void UpdateArmedState(bool isArmed)
    {
        IsArmed = isArmed;
    }
}

public class FlightModeItem
{
    public FlightMode Mode { get; }
    public string DisplayName { get; }
    public bool IsVtol { get; }

    public FlightModeItem(FlightMode mode, string displayName, bool isVtol)
    {
        Mode = mode;
        DisplayName = displayName;
        IsVtol = isVtol;
    }

    public override string ToString() => DisplayName;
}
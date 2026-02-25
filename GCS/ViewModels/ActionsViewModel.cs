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
                // Force UI to re-evaluate CanExecute for all commands
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

    private const ushort MAV_CMD_COMPONENT_ARM_DISARM = 400;

    public ActionsViewModel(IMavlinkBackend backend)
    {
        _backend = backend;

        InitializeModes();

        // Use simpler RelayCommand that checks IsConnected directly
        ArmCommand = new RelayCommand(async () => await ArmAsync(), CanExecuteConnected);
        DisarmCommand = new RelayCommand(async () => await DisarmAsync(), CanExecuteConnected);

        RtlCommand = new RelayCommand(async () => await SetModeAsync(FlightModeEnum.Rtl), CanExecuteConnected);
        LoiterCommand = new RelayCommand(async () => await SetModeAsync(FlightModeEnum.Loiter), CanExecuteConnected);
        AutoCommand = new RelayCommand(async () => await SetModeAsync(FlightModeEnum.Auto), CanExecuteConnected);
        GuidedCommand = new RelayCommand(async () => await SetModeAsync(FlightModeEnum.Guided), CanExecuteConnected);
        CruiseCommand = new RelayCommand(async () => await SetModeAsync(FlightModeEnum.Cruise), CanExecuteConnected);

        QHoverCommand = new RelayCommand(async () => await SetModeAsync(FlightModeEnum.QHover), CanExecuteConnected);
        QLoiterCommand = new RelayCommand(async () => await SetModeAsync(FlightModeEnum.QLoiter), CanExecuteConnected);
        QLandCommand = new RelayCommand(async () => await SetModeAsync(FlightModeEnum.QLand), CanExecuteConnected);
        QRtlCommand = new RelayCommand(async () => await SetModeAsync(FlightModeEnum.QRtl), CanExecuteConnected);

        SetModeCommand = new RelayCommand(async () => await SetSelectedModeAsync(), () => IsConnected && SelectedModeIndex >= 0);

        // Subscribe to backend connection state directly
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

    private async Task ArmAsync()
    {
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

    private async Task SetModeAsync(FlightModeEnum mode)
    {
        try
        {
            string modeName = mode.ToString().ToUpper();
            LastCommandResult = $"Setting {modeName}...";
            Debug.WriteLine($"[ActionsViewModel] Setting mode to {modeName}...");

            uint customMode = ArdupilotPlaneFlightModeMapper.ToCustomMode(mode);
            byte baseMode = (byte)(IsArmed ? 0xD1 : 0x51);

            await _backend.SendSetModeAsync(baseMode, customMode);
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
        if (SelectedModeIndex >= 0 && SelectedModeIndex < AvailableModes.Count)
        {
            await SetModeAsync(AvailableModes[SelectedModeIndex].Mode);
        }
    }

    public void UpdateFromVehicleState(VehicleState state)
    {


        if (state.FlightMode.HasValue)
        {
            FlightMode = state.FlightMode.Value.ToString().ToUpper();
        }

        // Update IsConnected from state
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
using GCS.Core;
using GCS.Core.Alerts;
using GCS.Core.Domain;
using GCS.Core.Health;
using GCS.Core.Mavlink;
using GCS.Core.Mavlink.Messages;
using GCS.Core.Mavlink.Tx;
using GCS.Core.Mission;
using GCS.Core.Preflight;
using GCS.Core.State;
using GCS.Core.Transport;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace GCS.ViewModels;

public class MainViewModel : ViewModelBase, IDisposable
{
    // ═══════════════════════════════════════════════════════════════
    // Backend Services
    // ═══════════════════════════════════════════════════════════════

    private ITransport? _transport;
    private IMavlinkBackend? _backend;
    private IVehicleStateStore? _stateStore;
    private IVehicleHealthMonitor? _healthMonitor;
    private IAlertEngine? _alertEngine;
    private IPreflightCheckEngine? _preflightEngine;
    private IMissionService? _missionService;

    private CancellationTokenSource? _cts;
    private bool _disposed;

    // Store mission event handlers so we can unsubscribe
    private Action<ushort>? _onMissionCount;
    private Action<MissionItem>? _onMissionItem;
    private Action<ushort>? _onMissionRequest;
    private Action<byte>? _onMissionAck;

    // ═══════════════════════════════════════════════════════════════
    // Child ViewModels
    // ═══════════════════════════════════════════════════════════════

    public ConnectionViewModel Connection { get; }
    public TelemetryViewModel Telemetry { get; }
    public AlertsViewModel Alerts { get; }
    public PreflightViewModel Preflight { get; }
    public MessagesViewModel Messages { get; }
    public RcChannelsViewModel RcChannels { get; }
    public ActionsViewModel? Actions { get; private set; }
    public MissionViewModel Mission { get; } = new();
    public WeatherViewModel Weather { get; }
    public FailsafeViewModel Failsafe { get; }

    // ═══════════════════════════════════════════════════════════════
    // Constructor
    // ═══════════════════════════════════════════════════════════════

    public MainViewModel()
    {
        var config = AppConfig.Load();

        Connection = new ConnectionViewModel();
        Telemetry = new TelemetryViewModel();
        Alerts = new AlertsViewModel();
        Preflight = new PreflightViewModel();
        Messages = new MessagesViewModel();
        RcChannels = new RcChannelsViewModel();

        Weather = new WeatherViewModel(config.WeatherApiKey, config.WeatherCity, config.WeatherCountry);

        Failsafe = new FailsafeViewModel(
            setParamFunc: async (name, value) =>
            {
                if (_backend != null)
                    await _backend.SetParameterAsync(name, value);
            },
            requestParamFunc: async (name) =>
            {
                if (_backend != null)
                    await _backend.RequestParameterAsync(name);
            }
        );

        Connection.ConnectRequested += OnConnectRequested;
        Connection.DisconnectRequested += OnDisconnectRequested;
    }

    // ═══════════════════════════════════════════════════════════════
    // Connection Lifecycle
    // ═══════════════════════════════════════════════════════════════

    private async void OnConnectRequested(TransportConfig config)
    {
        try
        {
            var syncContext = SynchronizationContext.Current
                ?? throw new InvalidOperationException("Must be called from UI thread");

            // 1. Create transport
            _transport = TransportFactory.Create(config);

            // 2. Create MAVLink backend
            _backend = new MavlinkBackend(_transport);
            _backend.TransportStateChanged += OnTransportStateChanged;
            _backend.AutopilotMessageReceived += OnAutopilotMessage;
            _backend.RcChannelsReceived += OnRcChannelsReceived;
            _backend.ParameterReceived += OnParameterReceived;

            // 3. Create Actions ViewModel
            Actions = new ActionsViewModel(_backend);
            OnPropertyChanged(nameof(Actions));

            // 4. Setup Preflight
            Preflight.SetBackend(_backend);

            // 5. Setup Mission Service
            var sender = new MavlinkSender(_backend);
            _missionService = new MissionService(sender, _backend);
            Mission.SetMissionService(_missionService);

            // Wire mission events (keep references for cleanup)
            _onMissionCount = async (count) =>
            {
                try { await _missionService.OnMissionCount(count, CancellationToken.None); }
                catch (Exception ex) { Debug.WriteLine($"[MainVM] MissionCount error: {ex.Message}"); }
            };
            _onMissionItem = async (item) =>
            {
                try { await _missionService.OnMissionItem(item, CancellationToken.None); }
                catch (Exception ex) { Debug.WriteLine($"[MainVM] MissionItem error: {ex.Message}"); }
            };
            _onMissionRequest = async (seq) =>
            {
                try { await _missionService.OnMissionRequest(seq, CancellationToken.None); }
                catch (Exception ex) { Debug.WriteLine($"[MainVM] MissionRequest error: {ex.Message}"); }
            };
            _onMissionAck = (result) => _missionService.OnMissionAck(result);

            _backend.MissionCountReceived += _onMissionCount;
            _backend.MissionItemReceived += _onMissionItem;
            _backend.MissionRequestReceived += _onMissionRequest;
            _backend.MissionAckReceived += _onMissionAck;

            // 6. Create state store (aggregates all telemetry)
            _stateStore = new VehicleStateStore(_backend, syncContext);
            _stateStore.StateChanged += OnVehicleStateChanged;

            // 7. Create health monitor
            var healthPolicy = new HealthPolicy(
                HeartbeatTimeout: TimeSpan.FromSeconds(3),
                AttitudeStaleAfter: TimeSpan.FromSeconds(2),
                PositionStaleAfter: TimeSpan.FromSeconds(2)
            );
            _healthMonitor = new VehicleHealthMonitor(_stateStore, healthPolicy, syncContext);
            _healthMonitor.HealthChanged += OnHealthStateChanged;

            // 8. Create alert engine
            var alertPolicy = new AlertPolicy(
                LinkLostSeverity: AlertSeverity.Critical,
                AttitudeStaleSeverity: AlertSeverity.Warning,
                PositionStaleSeverity: AlertSeverity.Warning
            );
            _alertEngine = new AlertEngine(_healthMonitor, alertPolicy, syncContext);
            _alertEngine.AlertsChanged += OnAlertsChanged;

            // 9. Create preflight engine
            var preflightPolicy = new PreflightPolicy(
                MinBatteryVoltage: 10.5f,
                MinBatteryPercent: 20
            );
            _preflightEngine = new VehiclePreflightCheckEngine(
                _stateStore, _healthMonitor, preflightPolicy, syncContext);
            _preflightEngine.PreflightChanged += OnPreflightChanged;

            // 10. Start backend
            _cts = new CancellationTokenSource();
            await _backend.StartAsync(_cts.Token);

            Connection.SetConnected();
            Failsafe.UpdateConnectionState(true);
            _ = Failsafe.RefreshFailsafeParams();
        }
        catch (Exception ex)
        {
            Connection.SetError(ex.Message);
            await CleanupAsync();
        }
    }

    private async void OnDisconnectRequested()
    {
        try
        {
            await CleanupAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MainVM] Disconnect error: {ex.Message}");
        }
        Connection.SetDisconnected();
    }

    // ═══════════════════════════════════════════════════════════════
    // Event Handlers
    // ═══════════════════════════════════════════════════════════════

    private void OnParameterReceived(string paramId, float value)
    {
        Failsafe.OnParameterReceived(paramId, value);
    }

    private void OnTransportStateChanged(TransportState state)
    {
        switch (state)
        {
            case TransportState.Connecting:
                Connection.StatusMessage = "Connecting...";
                break;
            case TransportState.Connected:
                Connection.StatusMessage = "Transport connected, waiting for heartbeat...";
                break;
            case TransportState.Error:
                Connection.SetError("Transport error");
                break;
            case TransportState.Disconnected:
                Connection.SetDisconnected();
                break;
        }
    }

    private void OnVehicleStateChanged(VehicleState state)
    {
        Telemetry.UpdateState(state);
        Actions?.UpdateFromVehicleState(state);

        bool isConnected = state.FlightMode.HasValue || state.Position != null || state.Attitude != null;
        Preflight.UpdateConnectionState(isConnected);
        Mission.UpdateConnectionState(isConnected);

        if (state.Connection?.IsConnected == true && Connection.IsConnected)
        {
            Connection.StatusMessage = $"Connected - SysID: {state.Connection.SystemId}";
        }
    }

    private void OnHealthStateChanged(HealthState health)
    {
        Telemetry.UpdateHealth(health);
    }

    private void OnAlertsChanged(IReadOnlyList<AlertState> alerts)
    {
        Alerts.UpdateAlerts(alerts);
    }

    private void OnPreflightChanged(PreflightState preflight)
    {
        Preflight.UpdatePreflight(preflight);
    }

    private void OnAutopilotMessage(AutopilotMessage message)
    {
        Messages.AddMessage(message);
    }

    private void OnRcChannelsReceived(RcChannelsData data)
    {
        RcChannels.UpdateChannels(data);
    }

    // ═══════════════════════════════════════════════════════════════
    // Cleanup
    // ═══════════════════════════════════════════════════════════════

    private async Task CleanupAsync()
    {
        if (_preflightEngine != null)
        {
            _preflightEngine.PreflightChanged -= OnPreflightChanged;
            if (_preflightEngine is IDisposable d) d.Dispose();
            _preflightEngine = null;
        }

        if (_alertEngine != null)
        {
            _alertEngine.AlertsChanged -= OnAlertsChanged;
            if (_alertEngine is IDisposable d) d.Dispose();
            _alertEngine = null;
        }

        if (_healthMonitor != null)
        {
            _healthMonitor.HealthChanged -= OnHealthStateChanged;
            if (_healthMonitor is IDisposable d) d.Dispose();
            _healthMonitor = null;
        }

        if (_stateStore != null)
        {
            _stateStore.StateChanged -= OnVehicleStateChanged;
            if (_stateStore is IDisposable d) d.Dispose();
            _stateStore = null;
        }

        if (_backend != null)
        {
            _backend.TransportStateChanged -= OnTransportStateChanged;
            _backend.AutopilotMessageReceived -= OnAutopilotMessage;
            _backend.RcChannelsReceived -= OnRcChannelsReceived;
            _backend.ParameterReceived -= OnParameterReceived;

            // Unsubscribe mission events (previously leaked)
            if (_onMissionCount != null) _backend.MissionCountReceived -= _onMissionCount;
            if (_onMissionItem != null) _backend.MissionItemReceived -= _onMissionItem;
            if (_onMissionRequest != null) _backend.MissionRequestReceived -= _onMissionRequest;
            if (_onMissionAck != null) _backend.MissionAckReceived -= _onMissionAck;
            _onMissionCount = null;
            _onMissionItem = null;
            _onMissionRequest = null;
            _onMissionAck = null;

            if (_cts != null)
            {
                _cts.Cancel();
                _cts.Dispose();
                _cts = null;
            }

            await _backend.StopAsync();
            _backend.Dispose();
            _backend = null;
        }

        _transport = null;
        _missionService = null;
        Failsafe.UpdateConnectionState(false);
    }

    public async Task ShutdownAsync()
    {
        await CleanupAsync();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Synchronous cleanup — prefer ShutdownAsync from Window.OnClosing
        CleanupAsync().GetAwaiter().GetResult();

        GC.SuppressFinalize(this);
    }
}
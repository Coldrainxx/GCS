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
using GCS.Infrastructure;
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

    // The whole backend object graph for the current connection. Null when
    // disconnected. Owns its own construction, wiring and teardown.
    private VehicleSession? _session;

    private bool _disposed;

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
                var backend = _session?.Backend;
                if (backend != null)
                    await backend.SetParameterAsync(name, value);
            },
            requestParamFunc: async (name) =>
            {
                var backend = _session?.Backend;
                if (backend != null)
                    await backend.RequestParameterAsync(name);
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

            // Build the entire backend graph in one place.
            _session = new VehicleSession(config, syncContext);

            // Subscribe this view-model's handlers to the aggregated session events.
            _session.TransportStateChanged += OnTransportStateChanged;
            _session.AutopilotMessageReceived += OnAutopilotMessage;
            _session.RcChannelsReceived += OnRcChannelsReceived;
            _session.ParameterReceived += OnParameterReceived;
            _session.VehicleStateChanged += OnVehicleStateChanged;
            _session.HealthChanged += OnHealthStateChanged;
            _session.AlertsChanged += OnAlertsChanged;
            _session.PreflightChanged += OnPreflightChanged;

            // Hand the backend/mission service to the dependent view-models.
            Actions = new ActionsViewModel(_session.Backend);
            OnPropertyChanged(nameof(Actions));
            Preflight.SetBackend(_session.Backend);
            Mission.SetMissionService(_session.MissionService);

            await _session.StartAsync(CancellationToken.None);

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
        Alerts.UpdateFromTelemetry(
            linkAlive: Telemetry.LinkAlive,
            attitudeFresh: Telemetry.AttitudeFresh,
            positionFresh: Telemetry.PositionFresh,
            isArmed: state.IsArmed,
            voltage: state.Battery?.VoltageVolts ?? 0,
            batteryPercent: state.Battery?.RemainingPercent ?? 0,
            gpsFixType: state.Gps?.FixType ?? 0,
            gpsSatellites: state.Gps?.SatellitesVisible ?? 0,
            gpsFixString: state.Gps?.FixTypeString ?? "NO GPS");
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
        Alerts.OnAutopilotMessage(message);
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
        if (_session != null)
        {
            _session.TransportStateChanged -= OnTransportStateChanged;
            _session.AutopilotMessageReceived -= OnAutopilotMessage;
            _session.RcChannelsReceived -= OnRcChannelsReceived;
            _session.ParameterReceived -= OnParameterReceived;
            _session.VehicleStateChanged -= OnVehicleStateChanged;
            _session.HealthChanged -= OnHealthStateChanged;
            _session.AlertsChanged -= OnAlertsChanged;
            _session.PreflightChanged -= OnPreflightChanged;

            await _session.DisposeAsync();
            _session = null;
        }

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
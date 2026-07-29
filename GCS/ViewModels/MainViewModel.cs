using GCS.Core;
using GCS.Core.Alerts;
using GCS.Core.Domain;
using GCS.Core.Health;
using GCS.Core.Mavlink;
using GCS.Core.Mavlink.Messages;
using GCS.Core.Mavlink.Tx;
using GCS.Core.Mission;
using GCS.Core.Preflight;
using GCS.Core.Settings;
using GCS.Core.State;
using GCS.Core.Transport;
using GCS.Infrastructure;
using GCS.Notifications;
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
    private TransportConfig? _lastConfig;
    private volatile bool _userDisconnect;
    private bool _reconnecting;

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
    public ParametersViewModel Parameters { get; }
    public FirmwareViewModel Firmware { get; }
    public SetupViewModel Setup { get; }
    public SwarmViewModel Swarm { get; } = new();
    public ToastsViewModel Toasts { get; } = new();

    private bool _isSwarmMode;
    /// <summary>
    /// Swarm mode reshapes the whole app around the fleet: the side panel becomes
    /// the vehicle roster and formation controls, and the action bar drives every
    /// vehicle instead of one. The map stays visible in both modes.
    /// </summary>
    public bool IsSwarmMode
    {
        get => _isSwarmMode;
        set
        {
            if (SetProperty(ref _isSwarmMode, value))
                OnPropertyChanged(nameof(IsSingleVehicleMode));
        }
    }

    public bool IsSingleVehicleMode => !_isSwarmMode;

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

        Parameters = new ParametersViewModel(
            setParam: async (name, value) =>
            {
                var backend = _session?.Backend;
                if (backend != null)
                    await backend.SetParameterAsync(name, value);
            },
            requestParam: async (name) =>
            {
                var backend = _session?.Backend;
                if (backend != null)
                    await backend.RequestParameterAsync(name);
            }
        );

        // MAV_CMD_PREFLIGHT_REBOOT_SHUTDOWN (246), param1=3 => reboot and hold in bootloader.
        Firmware = new FirmwareViewModel(
            rebootToBootloader: async () =>
            {
                var backend = _session?.Backend
                    ?? throw new InvalidOperationException("Not connected to a vehicle.");
                await backend.SendCommandLongAsync(246, param1: 3);
            },
            disconnectVehicle: async () =>
            {
                await CleanupAsync();
                Connection.SetDisconnected();
            });

        Setup = new SetupViewModel(
            setParam: async (name, value) =>
            {
                var backend = _session?.Backend;
                if (backend != null)
                    await backend.SetParameterAsync(name, value);
            },
            requestParam: async (name) =>
            {
                var backend = _session?.Backend;
                if (backend != null)
                    await backend.RequestParameterAsync(name);
            },
            sendCommand: async (cmd, p1, p2, p3, p4, p5, p6, p7) =>
            {
                var backend = _session?.Backend;
                if (backend != null)
                    await backend.SendCommandLongAsync(cmd, p1, p2, p3, p4, p5, p6, p7);
            },
            failsafe: Failsafe,
            firmware: Firmware);

        Swarm.PropertyChanged += OnSwarmPropertyChanged;

        // Mission upload targets whichever vehicle is primary, so a swarm upload
        // retargets around each transfer and restores the selection afterwards.
        Swarm.SetMissionUploader(
            uploadTo: async systemId =>
            {
                var backend = _session?.Backend
                    ?? throw new InvalidOperationException("Not connected");

                byte previous = backend.SystemId;
                var items = Mission.BuildItems();

                backend.SetPrimaryVehicle(systemId);
                try
                {
                    await Mission.SendItemsAsync(items);
                }
                finally
                {
                    // Always hand the app back to the vehicle the user selected,
                    // even if the transfer threw part-way through.
                    if (previous != 0) backend.SetPrimaryVehicle(previous);
                }
            },
            hasWaypoints: () => Mission.HasWaypoints);

        Connection.ConnectRequested += OnConnectRequested;
        Connection.DisconnectRequested += OnDisconnectRequested;
    }

    // Number of vehicles at the last mode decision, so we switch on the crossing
    // rather than on every count change — otherwise a manual override would be
    // undone by the next heartbeat.
    private int _lastVehicleCount;

    private void OnSwarmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SwarmViewModel.Count)) return;

        int count = Swarm.Count;
        int previous = _lastVehicleCount;
        _lastVehicleCount = count;

        // A second vehicle appearing turns the app into a swarm controller;
        // dropping back to one (or none) returns it to the single-UAV app.
        // Between those crossings the Swarm button still overrides manually.
        if (previous <= 1 && count > 1)
        {
            IsSwarmMode = true;
            Notifier.Info($"{count} vehicles detected — swarm mode");
        }
        else if (previous > 1 && count <= 1)
        {
            IsSwarmMode = false;
            if (count == 1) Notifier.Info("Single vehicle — swarm mode off");
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // Connection Lifecycle
    // ═══════════════════════════════════════════════════════════════

    private async void OnConnectRequested(TransportConfig config)
    {
        _userDisconnect = false;
        _lastConfig = config;
        try
        {
            await ConnectAsync(config);
            PersistProfile(config);
            Notifier.Success("Connected");
            if (_session?.TlogPath is string tlog)
                Notifier.Info($"Recording {System.IO.Path.GetFileName(tlog)}");
        }
        catch (Exception ex)
        {
            Connection.SetError(ex.Message);
            Notifier.Error($"Connection failed: {ex.Message}");
            await CleanupAsync();
        }
    }

    /// <summary>Builds + starts the backend session. Throws on failure (caller cleans up).</summary>
    private async Task ConnectAsync(TransportConfig config)
    {
        var syncContext = SynchronizationContext.Current
            ?? throw new InvalidOperationException("Must be called from UI thread");

        await CleanupAsync(); // ensure a clean slate (no-op when already disconnected)

        _session = new VehicleSession(config, syncContext);

        _session.TransportStateChanged += OnTransportStateChanged;
        _session.AutopilotMessageReceived += OnAutopilotMessage;
        _session.RcChannelsReceived += OnRcChannelsReceived;
        _session.ServoOutputReceived += OnServoOutputReceived;
        _session.MagCalProgressReceived += OnMagCalProgress;
        _session.MagCalReportReceived += OnMagCalReport;
        _session.ParameterReceived += OnParameterReceived;
        _session.VehicleStateChanged += OnVehicleStateChanged;
        _session.HealthChanged += OnHealthStateChanged;
        _session.AlertsChanged += OnAlertsChanged;
        _session.PreflightChanged += OnPreflightChanged;

        Actions = new ActionsViewModel(_session.Backend);
        OnPropertyChanged(nameof(Actions));
        Swarm.Attach(_session.Backend, syncContext);
        Preflight.SetBackend(_session.Backend);
        Mission.SetMissionService(_session.MissionService);

        if (SettingsStore.Current.TelemetryLogging)
        {
            var logDir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "GCS", "logs");
            _session.StartTelemetryLog(logDir);
        }

        await _session.StartAsync(CancellationToken.None);

        Connection.SetConnected();
        Failsafe.UpdateConnectionState(true);
        Parameters.UpdateConnectionState(true);
        Setup.UpdateConnectionState(true);
        _ = Failsafe.RefreshFailsafeParams();
    }

    private async void OnDisconnectRequested()
    {
        _userDisconnect = true;
        try
        {
            await CleanupAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MainVM] Disconnect error: {ex.Message}");
        }
        Connection.SetDisconnected();
        Notifier.Info("Disconnected");
    }

    /// <summary>Command the vehicle to fly to a map point in GUIDED mode (from the map's right-click).</summary>
    public async void FlyTo(double lat, double lon)
    {
        var backend = _session?.Backend;
        if (backend == null)
        {
            Notifier.Warning("Not connected — can't fly to point.");
            return;
        }

        float alt = Mission.DefaultAltitude;
        var result = System.Windows.MessageBox.Show(
            $"Fly to:\n{lat:F6}, {lon:F6}\nat {alt:F0} m (relative), in GUIDED mode?",
            "Fly here", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);
        if (result != System.Windows.MessageBoxResult.Yes) return;

        try
        {
            await backend.SendGuidedGotoAsync(lat, lon, alt);
            Notifier.Success($"Flying to {lat:F5}, {lon:F5}");
        }
        catch (Exception ex)
        {
            Notifier.Error($"Fly-to failed: {ex.Message}");
        }
    }

    private void MaybeAutoReconnect()
    {
        if (_userDisconnect || _reconnecting || _lastConfig == null || !SettingsStore.Current.AutoReconnect)
            return;
        _ = AutoReconnectLoop();
    }

    private async Task AutoReconnectLoop()
    {
        _reconnecting = true;
        Notifier.Warning("Link lost — reconnecting…");
        try
        {
            await CleanupAsync();
            int attempt = 0;
            while (!_userDisconnect && SettingsStore.Current.AutoReconnect && _lastConfig != null)
            {
                attempt++;
                Connection.StatusMessage = $"Reconnecting (attempt {attempt})…";
                await Task.Delay(2000);
                if (_userDisconnect) break;
                try
                {
                    await ConnectAsync(_lastConfig);
                    Notifier.Success("Reconnected");
                    return;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[MainVM] Reconnect attempt {attempt} failed: {ex.Message}");
                    await CleanupAsync();
                }
            }
        }
        finally
        {
            _reconnecting = false;
        }
    }

    private static void PersistProfile(TransportConfig config)
    {
        ConnectionProfile? p = config switch
        {
            SerialTransportConfig s => new ConnectionProfile { Kind = TransportKind.Serial, PortName = s.PortName, BaudRate = s.BaudRate },
            TcpTransportConfig t => new ConnectionProfile { Kind = TransportKind.Tcp, Host = t.Host, Port = t.Port },
            UdpTransportConfig u => new ConnectionProfile { Kind = TransportKind.Udp, LocalPort = u.LocalPort, RemoteHost = u.RemoteHost, RemotePort = u.RemotePort },
            _ => null
        };
        if (p == null) return;
        SettingsStore.Current.Remember(p);
        SettingsStore.Save();
    }

    // ═══════════════════════════════════════════════════════════════
    // Event Handlers
    // ═══════════════════════════════════════════════════════════════

    private void OnParameterReceived(byte systemId, string paramId, float value)
    {
        // The parameter/setup editors act on one vehicle at a time. On a shared
        // swarm link every drone's PARAM_VALUE arrives here, so anything that
        // isn't the active vehicle must be dropped — otherwise drone 2's values
        // would silently overwrite what's shown (and then be written back) for
        // drone 1.
        var backend = _session?.Backend;
        if (backend != null && backend.SystemId != 0 && systemId != backend.SystemId)
            return;

        Failsafe.OnParameterReceived(paramId, value);
        Parameters.OnParameterReceived(paramId, value);
        Setup.OnParameter(paramId, value);
    }

    private void OnTransportStateChanged(TransportState state)
    {
        // This can arrive on the transport thread; marshal UI/state work to the UI thread.
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        Action apply = () =>
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
                    MaybeAutoReconnect();
                    break;
                case TransportState.Disconnected:
                    Connection.SetDisconnected();
                    break;
            }
        };

        if (dispatcher != null && !dispatcher.CheckAccess())
            dispatcher.BeginInvoke(apply);
        else
            apply();
    }

    private void OnVehicleStateChanged(VehicleState state)
    {
        // The session-level store is unfiltered, so on a shared link it merges
        // every drone into one state — the HUD would blend them. Once vehicles
        // have been discovered, use the active one's own filtered state instead
        // so the HUD, map marker and action bar all describe the same aircraft.
        var active = Swarm.ActiveVehicle;
        if (active != null)
        {
            var owned = active.State;
            // Connection is a property of the link, not of one vehicle.
            state = owned with { Connection = owned.Connection ?? state.Connection };
        }

        Telemetry.UpdateState(state);
        Actions?.UpdateFromVehicleState(state);

        bool isConnected = state.FlightMode.HasValue || state.Position != null || state.Attitude != null;
        Preflight.UpdateConnectionState(isConnected);
        Mission.UpdateConnectionState(isConnected);
        if (state.Position != null)
            Mission.UpdateVehiclePosition(state.Position.LatitudeDeg, state.Position.LongitudeDeg, state.Position.AltitudeRelMeters);

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
        Setup.OnMessage(message);
    }

    private void OnRcChannelsReceived(RcChannelsData data)
    {
        RcChannels.UpdateChannels(data);
        Setup.OnRcChannels(data);
    }

    private void OnServoOutputReceived(ServoOutputData data)
    {
        Setup.OnServoOutput(data);
    }

    private void OnMagCalProgress(MagCalProgressData data) => Setup.OnMagCalProgress(data);
    private void OnMagCalReport(MagCalReportData data) => Setup.OnMagCalReport(data);

    // ═══════════════════════════════════════════════════════════════
    // Cleanup
    // ═══════════════════════════════════════════════════════════════

    private async Task CleanupAsync()
    {
        Swarm.Detach();

        if (_session != null)
        {
            _session.TransportStateChanged -= OnTransportStateChanged;
            _session.AutopilotMessageReceived -= OnAutopilotMessage;
            _session.RcChannelsReceived -= OnRcChannelsReceived;
            _session.ServoOutputReceived -= OnServoOutputReceived;
            _session.MagCalProgressReceived -= OnMagCalProgress;
            _session.MagCalReportReceived -= OnMagCalReport;
            _session.ParameterReceived -= OnParameterReceived;
            _session.VehicleStateChanged -= OnVehicleStateChanged;
            _session.HealthChanged -= OnHealthStateChanged;
            _session.AlertsChanged -= OnAlertsChanged;
            _session.PreflightChanged -= OnPreflightChanged;

            await _session.DisposeAsync();
            _session = null;
        }

        Failsafe.UpdateConnectionState(false);
        Parameters.UpdateConnectionState(false);
        Setup.UpdateConnectionState(false);
    }

    public async Task ShutdownAsync()
    {
        _userDisconnect = true;
        SaveSettings();
        await CleanupAsync();
    }

    private void SaveSettings()
    {
        var s = SettingsStore.Current;
        s.DefaultAltitude = Mission.DefaultAltitude;
        s.DefaultRadius = Mission.DefaultRadius;
        s.DefaultFrame = Mission.DefaultFrame;
        s.CruiseSpeedMps = Mission.CruiseSpeedMps;
        SettingsStore.Save();
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
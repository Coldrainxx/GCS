using GCS.Core.Alerts;
using GCS.Core.Domain;
using GCS.Core.Health;
using GCS.Core.Logging;
using GCS.Core.Mavlink;
using GCS.Core.Mavlink.Messages;
using GCS.Core.Mavlink.Tx;
using GCS.Core.Mission;
using GCS.Core.Preflight;
using GCS.Core.State;
using GCS.Core.Transport;
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace GCS.Infrastructure;

/// <summary>
/// Owns the entire backend object graph for a single vehicle connection:
/// transport, MAVLink backend, telemetry store, health/alert/preflight engines,
/// and the mission service. Construction wires every internal subscription;
/// <see cref="DisposeAsync"/> tears all of them down in one place so the UI
/// layer cannot leak a subscription or forget to dispose a service.
/// </summary>
public sealed class VehicleSession : IAsyncDisposable
{
    private static readonly HealthPolicy HealthPolicy = new(
        HeartbeatTimeout: TimeSpan.FromSeconds(3),
        AttitudeStaleAfter: TimeSpan.FromSeconds(2),
        PositionStaleAfter: TimeSpan.FromSeconds(2));

    private static readonly AlertPolicy AlertPolicy = new(
        LinkLostSeverity: AlertSeverity.Critical,
        AttitudeStaleSeverity: AlertSeverity.Warning,
        PositionStaleSeverity: AlertSeverity.Warning);

    private static readonly PreflightPolicy PreflightPolicy = new(
        MinBatteryVoltage: 10.5f,
        MinBatteryPercent: 20);

    private readonly ITransport _transport;
    private readonly MavlinkBackend _backend;
    private readonly VehicleStateStore _stateStore;
    private readonly VehicleHealthMonitor _healthMonitor;
    private readonly AlertEngine _alertEngine;
    private readonly VehiclePreflightCheckEngine _preflightEngine;
    private readonly MissionService _missionService;

    // Mission RX handlers kept so they can be unsubscribed on teardown.
    private readonly Action<ushort> _onMissionCount;
    private readonly Action<MissionItem> _onMissionItem;
    private readonly Action<ushort> _onMissionRequest;
    private readonly Action<byte> _onMissionAck;

    private CancellationTokenSource? _cts;
    private bool _disposed;
    private TelemetryLogger? _tlog;

    public IMavlinkBackend Backend => _backend;
    public IMissionService MissionService => _missionService;

    /// <summary>Path of the telemetry log for this session, or null when logging is off.</summary>
    public string? TlogPath => _tlog?.FilePath;

    /// <summary>Start recording all RX/TX MAVLink traffic to a .tlog file. No-op on failure.</summary>
    public void StartTelemetryLog(string directory)
    {
        if (_tlog != null) return;
        try
        {
            var path = System.IO.Path.Combine(directory, $"gcs_{DateTime.Now:yyyyMMdd_HHmmss}.tlog");
            _tlog = new TelemetryLogger(path);
            _backend.RawFrameReceived += OnRawFrame;
            _backend.RawFrameSent += OnRawFrame;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VehicleSession] tlog start failed: {ex.Message}");
            _tlog = null;
        }
    }

    private void OnRawFrame(ReadOnlyMemory<byte> packet) => _tlog?.Write(packet.Span);

    // Aggregated events the UI layer subscribes to (single object, symmetric
    // subscribe/unsubscribe). They simply re-raise from the internal services.
    public event Action<TransportState>? TransportStateChanged;
    public event Action<AutopilotMessage>? AutopilotMessageReceived;
    public event Action<RcChannelsData>? RcChannelsReceived;
    public event Action<ServoOutputData>? ServoOutputReceived;
    public event Action<MagCalProgressData>? MagCalProgressReceived;
    public event Action<MagCalReportData>? MagCalReportReceived;
    public event Action<string, float>? ParameterReceived;
    public event Action<VehicleState>? VehicleStateChanged;
    public event Action<HealthState>? HealthChanged;
    public event Action<IReadOnlyList<AlertState>>? AlertsChanged;
    public event Action<PreflightState>? PreflightChanged;

    public VehicleSession(TransportConfig config, SynchronizationContext syncContext)
    {
        _transport = TransportFactory.Create(config);
        _backend = new MavlinkBackend(_transport);

        var sender = new MavlinkSender(_backend);
        _missionService = new MissionService(sender, _backend);

        _stateStore = new VehicleStateStore(_backend, syncContext);
        _healthMonitor = new VehicleHealthMonitor(_stateStore, HealthPolicy, syncContext);
        _alertEngine = new AlertEngine(_healthMonitor, AlertPolicy, syncContext);
        _preflightEngine = new VehiclePreflightCheckEngine(
            _stateStore, _healthMonitor, PreflightPolicy, syncContext);

        // Re-raise to the UI layer.
        _backend.TransportStateChanged += RaiseTransportState;
        _backend.AutopilotMessageReceived += RaiseAutopilotMessage;
        _backend.RcChannelsReceived += RaiseRcChannels;
        _backend.ServoOutputReceived += RaiseServoOutput;
        _backend.MagCalProgressReceived += RaiseMagCalProgress;
        _backend.MagCalReportReceived += RaiseMagCalReport;
        _backend.ParameterReceived += RaiseParameter;
        _stateStore.StateChanged += RaiseVehicleState;
        _healthMonitor.HealthChanged += RaiseHealth;
        _alertEngine.AlertsChanged += RaiseAlerts;
        _preflightEngine.PreflightChanged += RaisePreflight;

        // Drive the mission protocol from incoming frames.
        _onMissionCount = async count =>
        {
            try { await _missionService.OnMissionCount(count, CancellationToken.None); }
            catch (Exception ex) { Debug.WriteLine($"[VehicleSession] MissionCount error: {ex.Message}"); }
        };
        _onMissionItem = async item =>
        {
            try { await _missionService.OnMissionItem(item, CancellationToken.None); }
            catch (Exception ex) { Debug.WriteLine($"[VehicleSession] MissionItem error: {ex.Message}"); }
        };
        _onMissionRequest = async seq =>
        {
            try { await _missionService.OnMissionRequest(seq, CancellationToken.None); }
            catch (Exception ex) { Debug.WriteLine($"[VehicleSession] MissionRequest error: {ex.Message}"); }
        };
        _onMissionAck = result => _missionService.OnMissionAck(result);

        _backend.MissionCountReceived += _onMissionCount;
        _backend.MissionItemReceived += _onMissionItem;
        _backend.MissionRequestReceived += _onMissionRequest;
        _backend.MissionAckReceived += _onMissionAck;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        await _backend.StartAsync(_cts.Token);
    }

    private void RaiseTransportState(TransportState s) => TransportStateChanged?.Invoke(s);
    private void RaiseAutopilotMessage(AutopilotMessage m) => AutopilotMessageReceived?.Invoke(m);
    private void RaiseRcChannels(RcChannelsData d) => RcChannelsReceived?.Invoke(d);
    private void RaiseServoOutput(ServoOutputData d) => ServoOutputReceived?.Invoke(d);
    private void RaiseMagCalProgress(MagCalProgressData d) => MagCalProgressReceived?.Invoke(d);
    private void RaiseMagCalReport(MagCalReportData d) => MagCalReportReceived?.Invoke(d);
    private void RaiseParameter(string id, float v) => ParameterReceived?.Invoke(id, v);
    private void RaiseVehicleState(VehicleState s) => VehicleStateChanged?.Invoke(s);
    private void RaiseHealth(HealthState h) => HealthChanged?.Invoke(h);
    private void RaiseAlerts(IReadOnlyList<AlertState> a) => AlertsChanged?.Invoke(a);
    private void RaisePreflight(PreflightState p) => PreflightChanged?.Invoke(p);

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _cts?.Cancel();

        // Unsubscribe everything we wired in the constructor.
        _backend.TransportStateChanged -= RaiseTransportState;
        _backend.AutopilotMessageReceived -= RaiseAutopilotMessage;
        _backend.RcChannelsReceived -= RaiseRcChannels;
        _backend.ServoOutputReceived -= RaiseServoOutput;
        _backend.MagCalProgressReceived -= RaiseMagCalProgress;
        _backend.MagCalReportReceived -= RaiseMagCalReport;
        _backend.ParameterReceived -= RaiseParameter;
        _stateStore.StateChanged -= RaiseVehicleState;
        _healthMonitor.HealthChanged -= RaiseHealth;
        _alertEngine.AlertsChanged -= RaiseAlerts;
        _preflightEngine.PreflightChanged -= RaisePreflight;

        _backend.MissionCountReceived -= _onMissionCount;
        _backend.MissionItemReceived -= _onMissionItem;
        _backend.MissionRequestReceived -= _onMissionRequest;
        _backend.MissionAckReceived -= _onMissionAck;

        _backend.RawFrameReceived -= OnRawFrame;
        _backend.RawFrameSent -= OnRawFrame;
        _tlog?.Dispose();
        _tlog = null;

        // Dispose engines (reverse construction order), then stop the backend.
        _preflightEngine.Dispose();
        _alertEngine.Dispose();
        _healthMonitor.Dispose();
        _stateStore.Dispose();

        await _backend.StopAsync();
        _backend.Dispose();

        _cts?.Dispose();
        _cts = null;
    }
}

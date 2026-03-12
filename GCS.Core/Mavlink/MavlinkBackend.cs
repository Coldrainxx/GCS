using GCS.Core.Domain;
using GCS.Core.Mavlink.CommandAck;
using GCS.Core.Mavlink.Connection;
using GCS.Core.Mavlink.Dispatch;
using GCS.Core.Mavlink.Messages;
using GCS.Core.Transport;
using MavLinkSharp;
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace GCS.Core.Mavlink;

public sealed class MavlinkBackend : IMavlinkBackend
{
    // ═══════════════════════════════════════════════════════════════
    // Dependencies
    // ═══════════════════════════════════════════════════════════════

    private readonly ITransport _transport;
    private readonly MavlinkDispatcher _dispatcher;
    private readonly MavlinkConnectionTracker _connection;
    private readonly CommandAckTracker _commandAckTracker;
    private readonly MavlinkFrameBuffer _frameBuffer = new();

    // ═══════════════════════════════════════════════════════════════
    // State
    // ═══════════════════════════════════════════════════════════════

    private CancellationTokenSource? _cts;
    private Task? _tickTask;
    private TransportState _transportState = TransportState.Disconnected;
    private bool _disposed;

    // ═══════════════════════════════════════════════════════════════
    // Constants
    // ═══════════════════════════════════════════════════════════════

    private const byte GcsSysId = 255;
    private const byte GcsCompId = 190; // MAV_COMP_ID_MISSIONPLANNER
    private const ushort MAV_CMD_COMPONENT_ARM_DISARM = 400;

    // ═══════════════════════════════════════════════════════════════
    // Events - Telemetry
    // ═══════════════════════════════════════════════════════════════

    public event Action<HeartbeatState>? HeartbeatReceived;
    public event Action<AttitudeState>? AttitudeReceived;
    public event Action<PositionState>? PositionReceived;
    public event Action<VfrHudState>? VfrHudReceived;
    public event Action<BatteryState>? BatteryReceived;
    public event Action<RcChannelsData>? RcChannelsReceived;

    // ═══════════════════════════════════════════════════════════════
    // Events - Messages & Acks
    // ═══════════════════════════════════════════════════════════════

    public event Action<AutopilotMessage>? AutopilotMessageReceived;
    public event Action<ushort, byte>? CommandAckReceived;

    // ═══════════════════════════════════════════════════════════════
    // Events - Mission Protocol
    // ═══════════════════════════════════════════════════════════════

    public event Action<ushort>? MissionCountReceived;
    public event Action<MissionItem>? MissionItemReceived;
    public event Action<ushort>? MissionRequestReceived;
    public event Action<byte>? MissionAckReceived;

    // ═══════════════════════════════════════════════════════════════
    // Events - Parameters
    // ═══════════════════════════════════════════════════════════════

    public event Action<string, float>? ParameterReceived;
    public event Action<GpsState>? GpsStateReceived;

    // ═══════════════════════════════════════════════════════════════
    // Events - Connection State
    // ═══════════════════════════════════════════════════════════════

    public event Action<ConnectionState>? ConnectionStateChanged;
    public event Action<TransportState>? TransportStateChanged;

    // ═══════════════════════════════════════════════════════════════
    // Properties
    // ═══════════════════════════════════════════════════════════════

    public bool IsConnected => _connection.IsConnected;
    public byte SystemId => _connection.SystemId;
    public byte ComponentId => _connection.ComponentId;

    // ═══════════════════════════════════════════════════════════════
    // Constructor
    // ═══════════════════════════════════════════════════════════════

    public MavlinkBackend(ITransport transport)
    {
        _transport = transport;

        _transport.DataReceived += OnDataReceived;
        _transport.TransportError += OnTransportError;

        _connection = new MavlinkConnectionTracker(TimeSpan.FromSeconds(3));
        _connection.ConnectionChanged += OnConnectionChanged;

        _commandAckTracker = new CommandAckTracker();

        _dispatcher = new MavlinkDispatcher(CreateHandlers());
    }

    private IMavlinkMessageHandler[] CreateHandlers()
    {
        return new IMavlinkMessageHandler[]
        {
            // Telemetry handlers
            new HeartbeatHandler(_connection, s => HeartbeatReceived?.Invoke(s)),
            new AttitudeHandler(s => AttitudeReceived?.Invoke(s)),
            new GlobalPositionHandler(s => PositionReceived?.Invoke(s)),
            new VfrHudHandler(s => VfrHudReceived?.Invoke(s)),
            new SysStatusHandler(s => BatteryReceived?.Invoke(s)),
            new RcChannelsHandler(s => RcChannelsReceived?.Invoke(s)),
            new MissionRequestHandler(seq => MissionRequestReceived?.Invoke(seq)),
            new GpsRawIntHandler(s => GpsStateReceived?.Invoke(s)),
            // Message handlers
            new StatustextHandler(s => AutopilotMessageReceived?.Invoke(s)),
            
            // Command ack handler
            new CommandAckHandler(_commandAckTracker),
            
            // Mission protocol handlers
            new MissionCountHandler(count => MissionCountReceived?.Invoke(count)),
            new MissionItemIntRxHandler(item => MissionItemReceived?.Invoke(item)),
            new MissionRequestIntHandler(seq => MissionRequestReceived?.Invoke(seq)),
            new MissionAckHandler(result => MissionAckReceived?.Invoke(result)),
            
            // Parameter handler
            new ParamValueHandler((id, val) => ParameterReceived?.Invoke(id, val)),
        };
    }

    // ═══════════════════════════════════════════════════════════════
    // Lifecycle
    // ═══════════════════════════════════════════════════════════════

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(MavlinkBackend));

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        SetTransportState(TransportState.Connecting);

        try
        {
            await _transport.StartAsync(_cts.Token);
            SetTransportState(TransportState.Connected);
        }
        catch (Exception)
        {
            SetTransportState(TransportState.Error);
            throw;
        }

        _tickTask = Task.Run(() => TickLoop(_cts.Token), _cts.Token);
    }

    public async Task StopAsync()
    {
        if (_cts == null)
            return;

        _cts.Cancel();

        if (_tickTask != null)
        {
            try { await _tickTask; }
            catch (OperationCanceledException) { }
        }

        await _transport.StopAsync();
        _connection.Reset();
        _cts.Dispose();
        _cts = null;

        SetTransportState(TransportState.Disconnected);
    }

    // ═══════════════════════════════════════════════════════════════
    // RX - Data Processing
    // ═══════════════════════════════════════════════════════════════

    private void OnDataReceived(ReadOnlyMemory<byte> data)
    {
        foreach (var frameData in _frameBuffer.AddData(data.Span))
        {
            var frame = new Frame();
            if (!frame.TryParse(frameData.Span))
                continue;

            _dispatcher.Dispatch(frame);
        }
    }

    private void OnTransportError(Exception ex)
    {
        SetTransportState(TransportState.Error);
        Debug.WriteLine($"[MavlinkBackend] Transport error: {ex.Message}");
    }

    private void OnConnectionChanged(MavlinkConnectionState state)
    {
        ConnectionStateChanged?.Invoke(
            new ConnectionState(
                state.IsConnected,
                state.SystemId,
                state.ComponentId,
                state.LastHeartbeatUtc
            )
        );
    }

    // ═══════════════════════════════════════════════════════════════
    // Tick Loop
    // ═══════════════════════════════════════════════════════════════

    private async Task TickLoop(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var now = DateTime.UtcNow;
                _connection.Tick(now);
                _commandAckTracker.Tick();
                await Task.Delay(200, token);
            }
        }
        catch (OperationCanceledException) { }
    }

    // ═══════════════════════════════════════════════════════════════
    // TX - Commands
    // ═══════════════════════════════════════════════════════════════

    public async Task SendCommandLongAsync(
        ushort command,
        float param1 = 0, float param2 = 0, float param3 = 0, float param4 = 0,
        float param5 = 0, float param6 = 0, float param7 = 0,
        byte confirmation = 0,
        CancellationToken ct = default)
    {
        EnsureConnected();

        var packet = Mavlink2Serializer.CommandLong(
            targetSys: _connection.SystemId,
            targetComp: _connection.ComponentId,
            senderSys: GcsSysId,
            senderComp: GcsCompId,
            command: command,
            confirmation: confirmation,
            p1: param1, p2: param2, p3: param3, p4: param4,
            p5: param5, p6: param6, p7: param7);

        await _transport.SendAsync(packet, ct);
    }

    public async Task SendSetModeAsync(
        byte baseMode,
        uint customMode,
        CancellationToken ct = default)
    {
        EnsureConnected();

        var packet = Mavlink2Serializer.SetMode(
            targetSys: _connection.SystemId,
            senderSys: GcsSysId,
            senderComp: GcsCompId,
            baseMode: baseMode,
            customMode: customMode);

        await _transport.SendAsync(packet, ct);
    }

    public async Task SendArmDisarmAsync(bool arm, CancellationToken ct = default)
    {
        await SendCommandLongAsync(
            command: MAV_CMD_COMPONENT_ARM_DISARM,
            param1: arm ? 1f : 0f,
            ct: ct);
    }

    public async Task SendRawAsync(ReadOnlyMemory<byte> packet, CancellationToken ct = default)
    {
        await _transport.SendAsync(packet, ct);
    }

    // ═══════════════════════════════════════════════════════════════
    // TX - Parameters (now using Mavlink2Serializer)
    // ═══════════════════════════════════════════════════════════════

    public async Task SetParameterAsync(string paramId, float value, CancellationToken ct = default)
    {
        EnsureConnected();

        var packet = Mavlink2Serializer.ParamSet(
            targetSys: _connection.SystemId,
            targetComp: _connection.ComponentId,
            senderSys: GcsSysId,
            senderComp: GcsCompId,
            paramId: paramId,
            value: value);

        await _transport.SendAsync(packet, ct);

        Debug.WriteLine($"[MavlinkBackend] SetParameter: {paramId} = {value}");
    }

    public async Task RequestParameterAsync(string paramId, CancellationToken ct = default)
    {
        EnsureConnected();

        var packet = Mavlink2Serializer.ParamRequestRead(
            targetSys: _connection.SystemId,
            targetComp: _connection.ComponentId,
            senderSys: GcsSysId,
            senderComp: GcsCompId,
            paramId: paramId);

        await _transport.SendAsync(packet, ct);

        Debug.WriteLine($"[MavlinkBackend] RequestParameter: {paramId}");
    }

    // ═══════════════════════════════════════════════════════════════
    // TX - With Acknowledgement
    // ═══════════════════════════════════════════════════════════════

    public async Task<CommandAckResult> SendCommandWithAckAsync(
        ushort command,
        float param1 = 0, float param2 = 0, float param3 = 0, float param4 = 0,
        float param5 = 0, float param6 = 0, float param7 = 0,
        CancellationToken ct = default)
    {
        EnsureConnected();

        var ackTask = _commandAckTracker.Register(
            command,
            _connection.SystemId,
            _connection.ComponentId);

        await SendCommandLongAsync(
            command, param1, param2, param3, param4, param5, param6, param7,
            confirmation: 0, ct: ct);

        return await ackTask;
    }

    // ═══════════════════════════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════════════════════════

    private void EnsureConnected()
    {
        if (!_connection.IsConnected)
            throw new InvalidOperationException("Not connected to vehicle");
    }

    private void SetTransportState(TransportState state)
    {
        if (_transportState == state) return;
        _transportState = state;
        TransportStateChanged?.Invoke(state);
    }

    // ═══════════════════════════════════════════════════════════════
    // Disposal
    // ═══════════════════════════════════════════════════════════════

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _transport.DataReceived -= OnDataReceived;
        _transport.TransportError -= OnTransportError;
        _connection.ConnectionChanged -= OnConnectionChanged;

        _cts?.Cancel();
        _cts?.Dispose();
        _transport.Dispose();
    }
}
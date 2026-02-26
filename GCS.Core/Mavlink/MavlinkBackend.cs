using GCS.Core.Domain;
using GCS.Core.Mavlink.CommandAck;
using GCS.Core.Mavlink.Connection;
using GCS.Core.Mavlink.Dispatch;
using GCS.Core.Mavlink.Messages;
using GCS.Core.Transport;
using MavLinkSharp;
using MavLinkSharp.Enums;
using System;
using System.Text;
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
    private byte _sequence = 0;

    // ═══════════════════════════════════════════════════════════════
    // Constants
    // ═══════════════════════════════════════════════════════════════

    private const byte GcsSysId = 255;
    private const byte GcsCompId = 190; // MAV_COMP_ID_MISSIONPLANNER
    private const ushort MAV_CMD_COMPONENT_ARM_DISARM = 400;
    private const byte MAV_PARAM_TYPE_REAL32 = 9;

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
            if (!Message.TryParse(frameData.Span, out var frame))
                continue;

            if (frame.ErrorReason != ErrorReason.None)
                continue;

            _dispatcher.Dispatch(frame);
        }
    }

    private void OnTransportError(Exception ex)
    {
        SetTransportState(TransportState.Error);
        System.Diagnostics.Debug.WriteLine($"[MavlinkBackend] Transport error: {ex.Message}");
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
    // TX - Parameters
    // ═══════════════════════════════════════════════════════════════

    public async Task SetParameterAsync(string paramId, float value, CancellationToken ct = default)
    {
        EnsureConnected();

        // PARAM_SET (ID 23): target_sys(1) + target_comp(1) + param_id(16) + param_value(4) + param_type(1) = 23 bytes
        var payload = new byte[23];
        payload[0] = _connection.SystemId;
        payload[1] = _connection.ComponentId;

        var paramBytes = Encoding.ASCII.GetBytes(paramId);
        Array.Copy(paramBytes, 0, payload, 2, Math.Min(paramBytes.Length, 16));

        BitConverter.GetBytes(value).CopyTo(payload, 18);
        payload[22] = MAV_PARAM_TYPE_REAL32;

        var packet = BuildMavlink2Packet(23, payload);
        await _transport.SendAsync(packet, ct);

        System.Diagnostics.Debug.WriteLine($"[MavlinkBackend] SetParameter: {paramId} = {value}");
    }

    public async Task RequestParameterAsync(string paramId, CancellationToken ct = default)
    {
        EnsureConnected();

        // PARAM_REQUEST_READ (ID 20): target_sys(1) + target_comp(1) + param_id(16) + param_index(2) = 20 bytes
        var payload = new byte[20];
        payload[0] = _connection.SystemId;
        payload[1] = _connection.ComponentId;

        var paramBytes = Encoding.ASCII.GetBytes(paramId);
        Array.Copy(paramBytes, 0, payload, 2, Math.Min(paramBytes.Length, 16));

        BitConverter.GetBytes((short)-1).CopyTo(payload, 18); // -1 = named lookup

        var packet = BuildMavlink2Packet(20, payload);
        await _transport.SendAsync(packet, ct);

        System.Diagnostics.Debug.WriteLine($"[MavlinkBackend] RequestParameter: {paramId}");
    }

    private byte[] BuildMavlink2Packet(uint msgId, byte[] payload)
    {
        // MAVLink 2: STX(1) + LEN(1) + INCOMPAT(1) + COMPAT(1) + SEQ(1) + SYSID(1) + COMPID(1) + MSGID(3) + payload + CRC(2)
        var packet = new byte[10 + payload.Length + 2];

        packet[0] = 0xFD;                           // STX (MAVLink 2)
        packet[1] = (byte)payload.Length;           // Payload length
        packet[2] = 0;                              // Incompatibility flags
        packet[3] = 0;                              // Compatibility flags
        packet[4] = _sequence++;                    // Sequence
        packet[5] = GcsSysId;                       // System ID
        packet[6] = GcsCompId;                      // Component ID
        packet[7] = (byte)(msgId & 0xFF);           // Message ID (low)
        packet[8] = (byte)((msgId >> 8) & 0xFF);    // Message ID (mid)
        packet[9] = (byte)((msgId >> 16) & 0xFF);   // Message ID (high)

        Array.Copy(payload, 0, packet, 10, payload.Length);

        ushort crc = CalculateCrc(packet, 1, 9 + payload.Length, GetCrcExtra(msgId));
        packet[10 + payload.Length] = (byte)(crc & 0xFF);
        packet[11 + payload.Length] = (byte)(crc >> 8);

        return packet;
    }

    private static ushort CalculateCrc(byte[] buffer, int start, int length, byte crcExtra)
    {
        ushort crc = 0xFFFF;

        for (int i = start; i < start + length; i++)
        {
            byte tmp = (byte)(buffer[i] ^ (crc & 0xFF));
            tmp ^= (byte)(tmp << 4);
            crc = (ushort)((crc >> 8) ^ (tmp << 8) ^ (tmp << 3) ^ (tmp >> 4));
        }

        // Add CRC extra
        byte tmp2 = (byte)(crcExtra ^ (crc & 0xFF));
        tmp2 ^= (byte)(tmp2 << 4);
        crc = (ushort)((crc >> 8) ^ (tmp2 << 8) ^ (tmp2 << 3) ^ (tmp2 >> 4));

        return crc;
    }

    private static byte GetCrcExtra(uint msgId) => msgId switch
    {
        20 => 214,  // PARAM_REQUEST_READ
        22 => 220,  // PARAM_VALUE
        23 => 168,  // PARAM_SET
        _ => 0
    };

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
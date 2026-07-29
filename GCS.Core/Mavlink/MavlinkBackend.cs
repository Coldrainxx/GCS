using GCS.Core.Domain;
using GCS.Core.Mavlink.CommandAck;
using GCS.Core.Mavlink.Connection;
using GCS.Core.Mavlink.Dispatch;
using GCS.Core.Mavlink.Messages;
using GCS.Core.Transport;
using MavLinkSharp;
using System;
using System.Collections.Generic;
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
    private readonly MavlinkVehicleTracker _vehicles;
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
    // Telemetry events carry the source system id so a shared swarm link can be
    // demultiplexed into per-vehicle state.
    public event Action<byte, AttitudeState>? AttitudeReceived;
    public event Action<byte, PositionState>? PositionReceived;
    public event Action<byte, VfrHudState>? VfrHudReceived;
    public event Action<byte, BatteryState>? BatteryReceived;
    public event Action<RcChannelsData>? RcChannelsReceived;
    public event Action<ServoOutputData>? ServoOutputReceived;
    public event Action<MagCalProgressData>? MagCalProgressReceived;
    public event Action<MagCalReportData>? MagCalReportReceived;
    public event Action<ReadOnlyMemory<byte>>? RawFrameReceived;
    public event Action<ReadOnlyMemory<byte>>? RawFrameSent;

    // ═══════════════════════════════════════════════════════════════
    // Events - Messages & Acks
    // ═══════════════════════════════════════════════════════════════

    public event Action<AutopilotMessage>? AutopilotMessageReceived;

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

    public event Action<byte, string, float>? ParameterReceived;
    public event Action<byte, GpsState>? GpsStateReceived;

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
    // Multi-vehicle (swarm) discovery
    // ═══════════════════════════════════════════════════════════════

    /// <summary>System ids currently heartbeating on this link.</summary>
    public IReadOnlyList<byte> KnownSystems => _vehicles.KnownSystems;

    public event Action<byte>? VehicleDiscovered
    {
        add => _vehicles.VehicleDiscovered += value;
        remove => _vehicles.VehicleDiscovered -= value;
    }

    public event Action<byte>? VehicleLost
    {
        add => _vehicles.VehicleLost += value;
        remove => _vehicles.VehicleLost -= value;
    }

    /// <summary>Choose which vehicle un-targeted (single-vehicle) operations act on.</summary>
    public void SetPrimaryVehicle(byte systemId)
    {
        byte comp = _vehicles.ComponentIdOf(systemId);
        _connection.SetPrimary(systemId, comp == 0 ? (byte)1 : comp);
    }

    /// <summary>
    /// Resolve a command's destination. 0 means "the primary vehicle"; anything
    /// else addresses that system explicitly (component falls back to 1 = autopilot).
    /// </summary>
    private (byte Sys, byte Comp) ResolveTarget(byte targetSystem)
    {
        if (targetSystem == 0)
            return (_connection.SystemId, _connection.ComponentId);

        byte comp = _vehicles.ComponentIdOf(targetSystem);
        return (targetSystem, comp == 0 ? (byte)1 : comp);
    }

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
        _vehicles = new MavlinkVehicleTracker(TimeSpan.FromSeconds(5));

        _commandAckTracker = new CommandAckTracker();

        _dispatcher = new MavlinkDispatcher(CreateHandlers());
    }

    private IMavlinkMessageHandler[] CreateHandlers()
    {
        return new IMavlinkMessageHandler[]
        {
            // Telemetry handlers
            new HeartbeatHandler(_connection, s =>
            {
                // On a shared link every drone heartbeats here — this is what
                // turns one stream into a known set of vehicles.
                _vehicles.OnHeartbeat(s.SystemId, s.ComponentId, DateTime.UtcNow);
                HeartbeatReceived?.Invoke(s);
            }),
            new AttitudeHandler((sys, s) => AttitudeReceived?.Invoke(sys, s)),
            new GlobalPositionHandler((sys, s) => PositionReceived?.Invoke(sys, s)),
            new VfrHudHandler((sys, s) => VfrHudReceived?.Invoke(sys, s)),
            new SysStatusHandler((sys, s) => BatteryReceived?.Invoke(sys, s)),
            new RcChannelsHandler(s => RcChannelsReceived?.Invoke(s)),
            new ServoOutputHandler(s => ServoOutputReceived?.Invoke(s)),
            new MagCalProgressHandler(s => MagCalProgressReceived?.Invoke(s)),
            new MagCalReportHandler(s => MagCalReportReceived?.Invoke(s)),
            new MissionRequestHandler(seq => MissionRequestReceived?.Invoke(seq)),
            new GpsRawIntHandler((sys, s) => GpsStateReceived?.Invoke(sys, s)),
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
            new ParamValueHandler((sys, id, val) => ParameterReceived?.Invoke(sys, id, val)),
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

            RawFrameReceived?.Invoke(frameData);
            _dispatcher.Dispatch(frame);
        }
    }

    /// <summary>Single TX funnel so every outgoing packet is also logged.</summary>
    private async Task SendPacketAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        RawFrameSent?.Invoke(data);
        await _transport.SendAsync(data, ct);
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
                _vehicles.Tick(now);
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
        byte targetSystem = 0,
        CancellationToken ct = default)
    {
        EnsureConnected();
        var (sys, comp) = ResolveTarget(targetSystem);

        var packet = Mavlink2Serializer.CommandLong(
            targetSys: sys,
            targetComp: comp,
            senderSys: GcsSysId,
            senderComp: GcsCompId,
            command: command,
            confirmation: confirmation,
            p1: param1, p2: param2, p3: param3, p4: param4,
            p5: param5, p6: param6, p7: param7);

        await SendPacketAsync(packet, ct);
    }

    public async Task SendSetModeAsync(
        byte baseMode,
        uint customMode,
        byte targetSystem = 0,
        CancellationToken ct = default)
    {
        EnsureConnected();
        var (sys, _) = ResolveTarget(targetSystem);

        var packet = Mavlink2Serializer.SetMode(
            targetSys: sys,
            senderSys: GcsSysId,
            senderComp: GcsCompId,
            baseMode: baseMode,
            customMode: customMode);

        await SendPacketAsync(packet, ct);
    }

    public async Task SendArmDisarmAsync(bool arm, byte targetSystem = 0, CancellationToken ct = default)
    {
        await SendCommandLongAsync(
            command: MAV_CMD_COMPONENT_ARM_DISARM,
            param1: arm ? 1f : 0f,
            targetSystem: targetSystem,
            ct: ct);
    }

    public async Task SendGuidedGotoAsync(
        double latitudeDeg, double longitudeDeg, float altitudeMeters,
        byte targetSystem = 0, CancellationToken ct = default)
    {
        EnsureConnected();
        var (sys, comp) = ResolveTarget(targetSystem);

        // COMMAND_INT (75) / MAV_CMD_DO_REPOSITION (192). param2 bit0 = change to
        // GUIDED. Lat/Lon carried as int32 (1e7) so precision isn't lost.
        var packet = Mavlink2Serializer.Build(
            messageId: 75,
            sysId: GcsSysId,
            compId: GcsCompId,
            fieldValues: new()
            {
                ["target_system"] = sys,
                ["target_component"] = comp,
                ["frame"] = (byte)6,       // MAV_FRAME_GLOBAL_RELATIVE_ALT_INT
                ["command"] = (ushort)192, // MAV_CMD_DO_REPOSITION
                ["current"] = (byte)0,
                ["autocontinue"] = (byte)0,
                ["param1"] = -1f,          // ground speed: default
                ["param2"] = 1f,           // MAV_DO_REPOSITION_FLAGS: change to guided
                ["param3"] = 0f,
                ["param4"] = float.NaN,     // yaw: unchanged
                ["x"] = (int)(latitudeDeg * 1e7),
                ["y"] = (int)(longitudeDeg * 1e7),
                ["z"] = altitudeMeters
            });

        await SendPacketAsync(packet, ct);
        Debug.WriteLine($"[MavlinkBackend] Guided goto {latitudeDeg:F6},{longitudeDeg:F6} @ {altitudeMeters}m");
    }

    public async Task SendRawAsync(ReadOnlyMemory<byte> packet, CancellationToken ct = default)
    {
        await SendPacketAsync(packet, ct);
    }

    // ═══════════════════════════════════════════════════════════════
    // TX - Parameters (now using Mavlink2Serializer)
    // ═══════════════════════════════════════════════════════════════

    public async Task SetParameterAsync(string paramId, float value, byte targetSystem = 0, CancellationToken ct = default)
    {
        EnsureConnected();
        var (sys, comp) = ResolveTarget(targetSystem);

        var packet = Mavlink2Serializer.ParamSet(
            targetSys: sys,
            targetComp: comp,
            senderSys: GcsSysId,
            senderComp: GcsCompId,
            paramId: paramId,
            value: value);

        await SendPacketAsync(packet, ct);

        Debug.WriteLine($"[MavlinkBackend] SetParameter: {paramId} = {value}");
    }

    public async Task RequestParameterAsync(string paramId, byte targetSystem = 0, CancellationToken ct = default)
    {
        EnsureConnected();
        var (sys, comp) = ResolveTarget(targetSystem);

        var packet = Mavlink2Serializer.ParamRequestRead(
            targetSys: sys,
            targetComp: comp,
            senderSys: GcsSysId,
            senderComp: GcsCompId,
            paramId: paramId);

        await SendPacketAsync(packet, ct);

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
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
    private const ushort MAV_CMD_SET_MESSAGE_INTERVAL = 511;
    private const ushort MAV_CMD_DO_SET_MODE = 176;
    private const float MavModeFlagCustomModeEnabled = 1f;

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

    // Health telemetry, streamed only after RequestHealthStreamsAsync.
    public event Action<byte, VibrationState>? VibrationReceived;
    public event Action<byte, EkfStatusState>? EkfStatusReceived;
    public event Action<byte, BatteryStatusState>? BatteryStatusReceived;
    public event Action<byte, PowerStatusState>? PowerStatusReceived;
    public event Action<byte, EscTelemetryState>? EscTelemetryReceived;
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
        var handlers = new List<IMavlinkMessageHandler>
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

            // Health telemetry (streamed only after RequestHealthStreamsAsync).
            new VibrationHandler((sys, s) => VibrationReceived?.Invoke(sys, s)),
            new EkfStatusHandler((sys, s) => EkfStatusReceived?.Invoke(sys, s)),
            new EstimatorStatusHandler((sys, s) => EkfStatusReceived?.Invoke(sys, s)),
            new BatteryStatusHandler((sys, s) => BatteryStatusReceived?.Invoke(sys, s)),
            new PowerStatusHandler((sys, s) => PowerStatusReceived?.Invoke(sys, s)),
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
            new ParamValueHandler(
                (sys, id, val) => ParameterReceived?.Invoke(sys, id, val),
                (sys, id, type) => _paramTypes[(sys, id)] = type),
        };

        // Three messages, four ESCs each, merged into one array before publishing
        // so consumers see the whole set rather than blocks.
        handlers.AddRange(EscTelemetryHandler.All(OnEscBlock));

        return handlers.ToArray();
    }

    private readonly EscReading[] _escReadings = new EscReading[12];

    private void OnEscBlock(byte systemId, int blockIndex, EscReading[] readings)
    {
        int offset = blockIndex * 4;
        for (int i = 0; i < readings.Length && offset + i < _escReadings.Length; i++)
            _escReadings[offset + i] = readings[i];

        EscTelemetryReceived?.Invoke(systemId,
            new EscTelemetryState((EscReading[])_escReadings.Clone(), DateTime.UtcNow));
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

    /// <summary>
    /// Single TX funnel so every outgoing packet is also logged.
    ///
    /// The destination system is passed on to the transport: a UDP link with each
    /// drone on its own IP needs it to send to the right one. Links where every
    /// vehicle shares one address ignore it.
    /// </summary>
    private async Task SendPacketAsync(
        ReadOnlyMemory<byte> data, byte targetSystemId, CancellationToken ct)
    {
        RawFrameSent?.Invoke(data);
        await _transport.SendToAsync(data, targetSystemId, ct);
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

    /// <summary>
    /// Each vehicle's declared type per parameter, as reported in PARAM_VALUE.
    ///
    /// Keyed per vehicle rather than by name alone: a type is a property of the
    /// firmware answering, not of the name.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(byte Sys, string Id), byte>
        _paramTypes = new();

    /// <summary>Ticks between GCS heartbeats — 200 ms each, so 1 Hz.</summary>
    private const int HeartbeatEveryTicks = 5;

    private async Task TickLoop(CancellationToken token)
    {
        int tick = 0;

        try
        {
            while (!token.IsCancellationRequested)
            {
                var now = DateTime.UtcNow;
                _connection.Tick(now);
                _vehicles.Tick(now);
                _commandAckTracker.Tick();

                if (tick++ % HeartbeatEveryTicks == 0)
                    await SendGcsHeartbeatAsync(token);

                await Task.Delay(200, token);
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>
    /// Announce this GCS to the vehicles, once a second.
    ///
    /// Not optional: PX4 refuses to arm while it has never seen a MAV_TYPE_GCS
    /// heartbeat and NAV_DLL_ACT is set, and ArduPilot's FS_GCS_ENABL failsafe
    /// watches for the same message. It also serves as the first packet a vehicle
    /// hears from us, which is what makes a link start streaming where the vehicle
    /// waits to be spoken to before it replies.
    ///
    /// Sent from the moment the transport starts, before any vehicle is known —
    /// waiting for a heartbeat before sending one would deadlock exactly those
    /// links.
    /// </summary>
    private async Task SendGcsHeartbeatAsync(CancellationToken token)
    {
        var packet = Mavlink2Serializer.GcsHeartbeat(GcsSysId, GcsCompId);
        var known = _vehicles.KnownSystems;

        try
        {
            if (known.Count == 0)
            {
                // Nobody discovered yet: the configured address is all we have.
                await SendPacketAsync(packet, 0, token);
                return;
            }

            // Addressed per vehicle so it reaches each one on a link where they
            // have separate addresses. HEARTBEAT carries no target of its own.
            foreach (byte sysId in known)
                await SendPacketAsync(packet, sysId, token);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Backend] GCS heartbeat failed: {ex.Message}");
        }
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

        await SendPacketAsync(packet, sys, ct);
    }

    /// <summary>
    /// Ask the autopilot to stream the health messages.
    ///
    /// ArduPilot sends none of these by default, which is why vibration, EKF, motor
    /// output and power analysis had nothing to work with. Requested at 1-2 Hz
    /// rather than the HUD's rate: these drive thresholds and trends, and a
    /// 57600-baud radio is already carrying attitude and VFR_HUD at ~10 Hz.
    ///
    /// Failures are ignored on purpose — an autopilot that does not support a
    /// message simply never sends it, and the health rules already report absent
    /// data as unmonitored.
    /// </summary>
    /// <summary>
    /// Ask the vehicle to start sending telemetry at all.
    ///
    /// ArduPilot streams nothing but HEARTBEAT on a port whose SRn_* rates are zero,
    /// which looks exactly like a broken link: the mode shows, and no other value
    /// ever arrives. Mission Planner avoids this by sending REQUEST_DATA_STREAM on
    /// connect; this does the same.
    ///
    /// Both mechanisms are sent. REQUEST_DATA_STREAM is deprecated but is what
    /// ArduPilot reliably honours, including on MAVLink 1 links; SET_MESSAGE_INTERVAL
    /// is the modern per-message equivalent. Sending both costs a handful of packets
    /// once per connection and covers old and new firmware alike.
    /// </summary>
    public Task RequestTelemetryStreamsAsync(byte targetSystem = 0, CancellationToken ct = default) =>
        RequestTelemetryStreamsAsync(AutopilotKind.Unknown, targetSystem, ct);

    /// <inheritdoc cref="RequestTelemetryStreamsAsync(byte, CancellationToken)"/>
    public async Task RequestTelemetryStreamsAsync(
        AutopilotKind autopilot, byte targetSystem = 0, CancellationToken ct = default)
    {
        var (sys, comp) = ResolveTarget(targetSystem);

        // MAV_DATA_STREAM groups, with the rates Mission Planner uses as a guide.
        // Kept modest so a 57600-baud radio is not saturated.
        var streams = new (byte Id, ushort Rate)[]
        {
            (2, 2),    // EXTENDED_STATUS — SYS_STATUS, GPS_RAW_INT
            (6, 3),    // POSITION        — GLOBAL_POSITION_INT
            (10, 4),   // EXTRA1          — ATTITUDE
            (11, 4),   // EXTRA2          — VFR_HUD
            (12, 2),   // EXTRA3          — AHRS, VIBRATION, EKF_STATUS_REPORT
            (3, 2),    // RC_CHANNELS     — RC_CHANNELS, SERVO_OUTPUT_RAW
        };

        foreach (var (id, rate) in streams)
        {
            try
            {
                await SendPacketAsync(
                    Mavlink2Serializer.RequestDataStream(sys, comp, GcsSysId, GcsCompId, id, rate),
                    sys, ct);
                await Task.Delay(40, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Backend] Stream group {id} request failed: {ex.Message}");
            }
        }

        // Per-message intervals for the core telemetry, for firmware that prefers
        // the modern mechanism.
        var core = new (uint Id, int IntervalUs)[]
        {
            // HEARTBEAT is sent unconditionally by both firmwares and PX4 refuses to
            // have its interval set, so it is not requested.
            (1, 500_000),      // SYS_STATUS
            (24, 500_000),     // GPS_RAW_INT
            (30, 250_000),     // ATTITUDE
            (33, 333_000),     // GLOBAL_POSITION_INT
            (74, 250_000),     // VFR_HUD
        };

        foreach (var (id, interval) in core)
        {
            try
            {
                await SendCommandLongAsync(MAV_CMD_SET_MESSAGE_INTERVAL,
                    param1: id, param2: interval, targetSystem: targetSystem, ct: ct);
                await Task.Delay(40, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Backend] Interval request for {id} failed: {ex.Message}");
            }
        }

        await RequestHealthStreamsAsync(autopilot, targetSystem, ct);
    }

    public async Task RequestHealthStreamsAsync(byte targetSystem = 0, CancellationToken ct = default) =>
        await RequestHealthStreamsAsync(AutopilotKind.Unknown, targetSystem, ct);

    /// <param name="autopilot">
    /// Skips messages the firmware does not implement. PX4 answered FAILED to the
    /// ArduPilot-only requests, which is harmless but wastes commands on a slow link
    /// and fills the log with failures that look like a problem.
    /// </param>
    public async Task RequestHealthStreamsAsync(
        AutopilotKind autopilot, byte targetSystem = 0, CancellationToken ct = default)
    {
        bool px4 = autopilot == AutopilotKind.Px4;

        // (message id, interval in microseconds)
        var wanted = new List<(uint Id, int IntervalUs)>
        {
            (241, 500_000),    // VIBRATION            2 Hz — both
            (36,  500_000),    // SERVO_OUTPUT_RAW     2 Hz — motor balance
            (147, 1_000_000),  // BATTERY_STATUS       1 Hz — both
        };

        if (px4)
        {
            wanted.Add((230, 500_000));    // ESTIMATOR_STATUS
        }
        else
        {
            wanted.Add((193, 500_000));    // EKF_STATUS_REPORT
            wanted.Add((230, 500_000));    // ESTIMATOR_STATUS — some builds send both
            wanted.Add((125, 1_000_000));  // POWER_STATUS  (ArduPilot)
            wanted.Add((Messages.EscTelemetryHandler.Block1To4, 1_000_000));
            wanted.Add((Messages.EscTelemetryHandler.Block5To8, 1_000_000));
            wanted.Add((Messages.EscTelemetryHandler.Block9To12, 1_000_000));
        }

        foreach (var (id, interval) in wanted)
        {
            try
            {
                await SendCommandLongAsync(
                    MAV_CMD_SET_MESSAGE_INTERVAL,
                    param1: id,
                    param2: interval,
                    targetSystem: targetSystem,
                    ct: ct);

                // Spaced out so a burst of eight commands cannot swamp a slow link.
                await Task.Delay(60, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Backend] Stream request for {id} failed: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Command a mode, using whichever mechanism the firmware understands.
    ///
    /// ArduPilot takes SET_MODE with a flat custom_mode. PX4 needs
    /// MAV_CMD_DO_SET_MODE carrying its main and sub mode as separate parameters —
    /// a packed custom_mode sent through SET_MODE is not accepted.
    /// </summary>
    public Task SendFlightModeAsync(
        FlightModeChoice mode,
        AutopilotKind autopilot,
        bool isArmed,
        byte targetSystem = 0,
        CancellationToken ct = default)
    {
        if (autopilot == AutopilotKind.Px4)
        {
            return SendCommandLongAsync(
                MAV_CMD_DO_SET_MODE,
                param1: MavModeFlagCustomModeEnabled,
                param2: mode.Px4MainMode,
                param3: mode.Px4SubMode,
                targetSystem: targetSystem,
                ct: ct);
        }

        byte baseMode = (byte)(isArmed ? 0xD1 : 0x51);
        return SendSetModeAsync(baseMode, mode.CustomMode, targetSystem, ct);
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

        await SendPacketAsync(packet, sys, ct);
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

        await SendPacketAsync(packet, sys, ct);
        Debug.WriteLine($"[MavlinkBackend] Guided goto {latitudeDeg:F6},{longitudeDeg:F6} @ {altitudeMeters}m");
    }

    /// <summary>
    /// Send a packet built elsewhere — mission upload and download.
    ///
    /// Those addressed the primary vehicle when they were built (via
    /// <see cref="SystemId"/>), so that is where the packet is routed.
    /// </summary>
    public async Task SendRawAsync(ReadOnlyMemory<byte> packet, CancellationToken ct = default)
    {
        await SendPacketAsync(packet, _connection.SystemId, ct);
    }

    /// <inheritdoc/>
    public async Task SendRawToAsync(
        ReadOnlyMemory<byte> packet, byte targetSystem, CancellationToken ct = default)
    {
        await SendPacketAsync(packet, targetSystem, ct);
    }

    // ═══════════════════════════════════════════════════════════════
    // TX - Parameters (now using Mavlink2Serializer)
    // ═══════════════════════════════════════════════════════════════

    public async Task SetParameterAsync(string paramId, float value, byte targetSystem = 0, CancellationToken ct = default)
    {
        EnsureConnected();
        var (sys, comp) = ResolveTarget(targetSystem);

        // PX4 checks the declared type against what it holds and rejects a
        // mismatch outright — "param types mismatch param: FLW_TGT_ALT_M" — so an
        // integer parameter written as REAL32 is silently never applied. The type
        // the vehicle itself reported is the reliable one.
        byte paramType = MavParamValue.TypeForWrite(
            paramId,
            _paramTypes.TryGetValue((sys, paramId), out var learned) ? learned : null);

        var packet = Mavlink2Serializer.ParamSet(
            targetSys: sys,
            targetComp: comp,
            senderSys: GcsSysId,
            senderComp: GcsCompId,
            paramId: paramId,
            value: MavParamValue.Encode(value, paramType),
            paramType: paramType);

        await SendPacketAsync(packet, sys, ct);

        Debug.WriteLine($"[MavlinkBackend] SetParameter: {paramId} = {value} (type {paramType})");
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

        await SendPacketAsync(packet, sys, ct);

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
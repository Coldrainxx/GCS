using GCS.Core.Domain;
using GCS.Core.Mavlink.CommandAck;
using GCS.Core.Mavlink.Messages;

namespace GCS.Core.Mavlink;

public interface IMavlinkBackend : IDisposable
{
    // ═══════════════════════════════════════════════════════════════
    // RX Events - Telemetry
    // ═══════════════════════════════════════════════════════════════

    event Action<HeartbeatState>? HeartbeatReceived;
    // Telemetry events carry the source system id (first argument) so a shared
    // swarm link can be demultiplexed into per-vehicle state.
    event Action<byte, AttitudeState>? AttitudeReceived;
    event Action<byte, PositionState>? PositionReceived;
    event Action<byte, VfrHudState>? VfrHudReceived;
    event Action<byte, BatteryState>? BatteryReceived;
    event Action<RcChannelsData>? RcChannelsReceived;
    event Action<ServoOutputData>? ServoOutputReceived;
    event Action<MagCalProgressData>? MagCalProgressReceived;
    event Action<MagCalReportData>? MagCalReportReceived;
    event Action<byte, GpsState>? GpsStateReceived;

    // Health telemetry. Silent until RequestHealthStreamsAsync asks for it —
    // ArduPilot streams none of these by default.
    event Action<byte, VibrationState>? VibrationReceived;
    event Action<byte, EkfStatusState>? EkfStatusReceived;
    event Action<byte, BatteryStatusState>? BatteryStatusReceived;
    event Action<byte, PowerStatusState>? PowerStatusReceived;
    event Action<byte, EscTelemetryState>? EscTelemetryReceived;

    /// <summary>Ask the autopilot to start streaming the health messages.</summary>
    Task RequestHealthStreamsAsync(byte targetSystem = 0, CancellationToken ct = default);

    /// <summary>
    /// Ask the autopilot to stream telemetry at all. Needed because a vehicle whose
    /// SRn_* rates are zero sends only heartbeats.
    /// </summary>
    Task RequestTelemetryStreamsAsync(byte targetSystem = 0, CancellationToken ct = default);

    /// <summary>Raw complete MAVLink packets, for telemetry logging (RX / TX).</summary>
    event Action<ReadOnlyMemory<byte>>? RawFrameReceived;
    event Action<ReadOnlyMemory<byte>>? RawFrameSent;

    // ═══════════════════════════════════════════════════════════════
    // RX Events - Messages & Acks
    // ═══════════════════════════════════════════════════════════════

    event Action<AutopilotMessage>? AutopilotMessageReceived;

    // ═══════════════════════════════════════════════════════════════
    // RX Events - Mission Protocol
    // ═══════════════════════════════════════════════════════════════

    event Action<ushort>? MissionCountReceived;      // count
    event Action<MissionItem>? MissionItemReceived;
    event Action<ushort>? MissionRequestReceived;    // sequence
    event Action<byte>? MissionAckReceived;          // result

    // ═══════════════════════════════════════════════════════════════
    // RX Events - Parameters
    // ═══════════════════════════════════════════════════════════════

    event Action<byte, string, float>? ParameterReceived;  // systemId, paramId, value

    // ═══════════════════════════════════════════════════════════════
    // Connection State Events
    // ═══════════════════════════════════════════════════════════════

    event Action<ConnectionState>? ConnectionStateChanged;
    event Action<TransportState>? TransportStateChanged;

    // ═══════════════════════════════════════════════════════════════
    // Connection Info (read-only)
    // ═══════════════════════════════════════════════════════════════

    bool IsConnected { get; }
    byte SystemId { get; }
    byte ComponentId { get; }

    // ═══════════════════════════════════════════════════════════════
    // Multi-vehicle (swarm) discovery
    // ═══════════════════════════════════════════════════════════════

    /// <summary>System ids currently heartbeating on this link.</summary>
    IReadOnlyList<byte> KnownSystems { get; }

    event Action<byte>? VehicleDiscovered;
    event Action<byte>? VehicleLost;

    /// <summary>Choose which vehicle un-targeted operations act on.</summary>
    void SetPrimaryVehicle(byte systemId);

    // ═══════════════════════════════════════════════════════════════
    // Lifecycle
    // ═══════════════════════════════════════════════════════════════

    Task StartAsync(CancellationToken cancellationToken);
    Task StopAsync();

    // ═══════════════════════════════════════════════════════════════
    // TX Methods - Commands
    // ═══════════════════════════════════════════════════════════════

    Task SendCommandLongAsync(
        ushort command,
        float param1 = 0, float param2 = 0, float param3 = 0, float param4 = 0,
        float param5 = 0, float param6 = 0, float param7 = 0,
        byte confirmation = 0,
        byte targetSystem = 0,
        CancellationToken ct = default);
    Task<CommandAckResult> SendCommandWithAckAsync(
        ushort command,
        float param1 = 0, float param2 = 0, float param3 = 0, float param4 = 0,
        float param5 = 0, float param6 = 0, float param7 = 0,
    CancellationToken ct = default);
    Task SendSetModeAsync(
        byte baseMode,
        uint customMode,
        byte targetSystem = 0,
        CancellationToken ct = default);

    Task SendArmDisarmAsync(
        bool arm,
        byte targetSystem = 0,
        CancellationToken ct = default);

    /// <summary>
    /// Command the vehicle to fly to a location in GUIDED mode
    /// (MAV_CMD_DO_REPOSITION with the change-mode flag set).
    /// </summary>
    Task SendGuidedGotoAsync(
        double latitudeDeg,
        double longitudeDeg,
        float altitudeMeters,
        byte targetSystem = 0,
        CancellationToken ct = default);

    // ═══════════════════════════════════════════════════════════════
    // TX Methods - Parameters
    // ═══════════════════════════════════════════════════════════════

    Task SetParameterAsync(string paramId, float value, byte targetSystem = 0, CancellationToken ct = default);
    Task RequestParameterAsync(string paramId, byte targetSystem = 0, CancellationToken ct = default);

    // ═══════════════════════════════════════════════════════════════
    // TX Methods - Raw Packet
    // ═══════════════════════════════════════════════════════════════

    Task SendRawAsync(
        ReadOnlyMemory<byte> packet,
        CancellationToken ct = default);
}
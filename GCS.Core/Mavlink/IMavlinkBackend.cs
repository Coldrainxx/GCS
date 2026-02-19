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
    event Action<AttitudeState>? AttitudeReceived;
    event Action<PositionState>? PositionReceived;
    event Action<VfrHudState>? VfrHudReceived;
    event Action<BatteryState>? BatteryReceived;
    event Action<RcChannelsData>? RcChannelsReceived;

    // ═══════════════════════════════════════════════════════════════
    // RX Events - Messages & Acks
    // ═══════════════════════════════════════════════════════════════

    event Action<AutopilotMessage>? AutopilotMessageReceived;
    event Action<ushort, byte>? CommandAckReceived;  // command, result

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

    event Action<string, float>? ParameterReceived;  // paramId, value

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
        CancellationToken ct = default);
    Task<CommandAckResult> SendCommandWithAckAsync(
        ushort command,
        float param1 = 0, float param2 = 0, float param3 = 0, float param4 = 0,
        float param5 = 0, float param6 = 0, float param7 = 0,
    CancellationToken ct = default);
    Task SendSetModeAsync(
        byte baseMode,
        uint customMode,
        CancellationToken ct = default);

    Task SendArmDisarmAsync(
        bool arm,
        CancellationToken ct = default);

    // ═══════════════════════════════════════════════════════════════
    // TX Methods - Parameters
    // ═══════════════════════════════════════════════════════════════

    Task SetParameterAsync(string paramId, float value, CancellationToken ct = default);
    Task RequestParameterAsync(string paramId, CancellationToken ct = default);

    // ═══════════════════════════════════════════════════════════════
    // TX Methods - Raw Packet
    // ═══════════════════════════════════════════════════════════════

    Task SendRawAsync(
        ReadOnlyMemory<byte> packet,
        CancellationToken ct = default);
}
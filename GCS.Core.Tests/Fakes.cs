using GCS.Core.Domain;
using GCS.Core.Mavlink;
using GCS.Core.Mavlink.CommandAck;
using GCS.Core.Mavlink.Messages;
using GCS.Core.Mavlink.Tx;

namespace GCS.Core.Tests;

/// <summary>Collects packets handed to it; never touches a real transport.</summary>
internal sealed class FakeSender : IMavlinkSender
{
    public List<byte[]> Sent { get; } = new();

    public Task SendAsync(byte[] packet, CancellationToken ct = default)
    {
        Sent.Add(packet);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Minimal IMavlinkBackend that only exposes a system/component id - enough to
/// drive MissionService. RX events are part of the contract but never raised here.
/// </summary>
#pragma warning disable CS0067 // events are required by the interface but unused in tests
internal sealed class FakeBackend : IMavlinkBackend
{
    public FakeBackend(byte systemId = 1, byte componentId = 1)
    {
        SystemId = systemId;
        ComponentId = componentId;
    }

    public bool IsConnected { get; set; } = true;
    public byte SystemId { get; }
    public byte ComponentId { get; }

    public event Action<HeartbeatState>? HeartbeatReceived;
    public event Action<AttitudeState>? AttitudeReceived;
    public event Action<PositionState>? PositionReceived;
    public event Action<VfrHudState>? VfrHudReceived;
    public event Action<BatteryState>? BatteryReceived;
    public event Action<RcChannelsData>? RcChannelsReceived;
    public event Action<ServoOutputData>? ServoOutputReceived;
    public event Action<MagCalProgressData>? MagCalProgressReceived;
    public event Action<MagCalReportData>? MagCalReportReceived;
    public event Action<GpsState>? GpsStateReceived;
    public event Action<ReadOnlyMemory<byte>>? RawFrameReceived;
    public event Action<ReadOnlyMemory<byte>>? RawFrameSent;
    public event Action<AutopilotMessage>? AutopilotMessageReceived;
    public event Action<ushort>? MissionCountReceived;
    public event Action<MissionItem>? MissionItemReceived;
    public event Action<ushort>? MissionRequestReceived;
    public event Action<byte>? MissionAckReceived;
    public event Action<string, float>? ParameterReceived;
    public event Action<ConnectionState>? ConnectionStateChanged;
    public event Action<TransportState>? TransportStateChanged;

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync() => Task.CompletedTask;

    public Task SendCommandLongAsync(
        ushort command,
        float param1 = 0, float param2 = 0, float param3 = 0, float param4 = 0,
        float param5 = 0, float param6 = 0, float param7 = 0,
        byte confirmation = 0,
        CancellationToken ct = default) => Task.CompletedTask;

    public Task<CommandAckResult> SendCommandWithAckAsync(
        ushort command,
        float param1 = 0, float param2 = 0, float param3 = 0, float param4 = 0,
        float param5 = 0, float param6 = 0, float param7 = 0,
        CancellationToken ct = default) => Task.FromResult(CommandAckResult.Accepted);

    public Task SendSetModeAsync(byte baseMode, uint customMode, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task SendArmDisarmAsync(bool arm, CancellationToken ct = default) => Task.CompletedTask;

    public Task SendGuidedGotoAsync(double latitudeDeg, double longitudeDeg, float altitudeMeters, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task SetParameterAsync(string paramId, float value, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task RequestParameterAsync(string paramId, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task SendRawAsync(ReadOnlyMemory<byte> packet, CancellationToken ct = default)
        => Task.CompletedTask;

    public void Dispose() { }
}
#pragma warning restore CS0067

/// <summary>
/// MavLink metadata must be initialised once per process before any packet is
/// serialised. Safe to call from every test.
/// </summary>
internal static class MavlinkInit
{
    private static readonly object Gate = new();
    private static bool _done;

    public static void EnsureInitialized()
    {
        lock (Gate)
        {
            if (_done) return;
            MavlinkBootstrap.Init();
            _done = true;
        }
    }
}

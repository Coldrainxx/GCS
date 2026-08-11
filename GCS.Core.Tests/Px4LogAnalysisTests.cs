using GCS.Core.Logging;
using GCS.Core.Mavlink;
using Xunit;

namespace GCS.Core.Tests;

/// <summary>
/// Replaying a PX4 log. The mode numbers in a heartbeat mean different things per
/// firmware, so a log read as ArduPilot shows a packed integer where a mode name
/// belongs.
/// </summary>
public class Px4LogAnalysisTests
{
    private const byte MavAutopilotArdupilot = 3;
    private const byte MavAutopilotPx4 = 12;
    private const byte MavTypeQuadrotor = 2;
    private const byte MavTypeFixedWing = 1;

    private static TlogRecord Heartbeat(
        DateTime at, byte autopilot, byte mavType, uint customMode, bool armed = false)
    {
        MavlinkInit.EnsureInitialized();

        var packet = Mavlink2Serializer.Build(0, sysId: 1, compId: 1, new()
        {
            ["type"] = mavType,
            ["autopilot"] = autopilot,
            ["base_mode"] = (byte)(armed ? 0x80 : 0x00),
            ["custom_mode"] = customMode,
            ["system_status"] = (byte)4,
            ["mavlink_version"] = (byte)3,
        });

        return new TlogRecord(at, packet);
    }

    private static List<FlightEvent> ModeEvents(FlightLogSummary summary) =>
        summary.Events.Where(e => e.Kind == "Mode").ToList();

    [Fact]
    public void APx4LogShowsModeNamesRatherThanAPackedNumber()
    {
        var t0 = new DateTime(2026, 8, 3, 12, 0, 0, DateTimeKind.Utc);

        var records = new[]
        {
            // PX4 packs main mode into bits 16-23 and sub mode into 24-31.
            Heartbeat(t0, MavAutopilotPx4, MavTypeQuadrotor,
                      Px4FlightModes.Pack(mainMode: 3, subMode: 0)),               // POSITION
            Heartbeat(t0.AddSeconds(5), MavAutopilotPx4, MavTypeQuadrotor,
                      Px4FlightModes.Pack(mainMode: 4, subMode: 3), armed: true),  // HOLD
            Heartbeat(t0.AddSeconds(10), MavAutopilotPx4, MavTypeQuadrotor,
                      Px4FlightModes.Pack(mainMode: 4, subMode: 5), armed: true),  // RETURN
        };

        var summary = FlightLogAnalyzer.Analyze(records);
        var modes = ModeEvents(summary);

        Assert.Equal(3, modes.Count);
        Assert.Contains("POSITION", modes[0].Text);
        Assert.Contains("HOLD", modes[1].Text);
        Assert.Contains("RETURN", modes[2].Text);

        // The failure this guards: a packed custom_mode read as an ArduPilot number.
        Assert.DoesNotContain(modes, m => m.Text.Contains("196608"));
        Assert.DoesNotContain(modes, m => m.Text.Contains("262144"));
    }

    [Fact]
    public void AnArduPilotLogStillDecodesAsArduPilot()
    {
        var t0 = new DateTime(2026, 8, 3, 12, 0, 0, DateTimeKind.Utc);

        var records = new[]
        {
            // ArduPlane: 0 = MANUAL, 10 = AUTO.
            Heartbeat(t0, MavAutopilotArdupilot, MavTypeFixedWing, 0),
            Heartbeat(t0.AddSeconds(5), MavAutopilotArdupilot, MavTypeFixedWing, 10, armed: true),
        };

        var summary = FlightLogAnalyzer.Analyze(records);
        var modes = ModeEvents(summary);

        Assert.Equal(2, modes.Count);
        Assert.Contains("MANUAL", modes[0].Text);
        Assert.Contains("AUTO", modes[1].Text);
    }

    /// <summary>
    /// The same custom_mode number means different modes on the two firmwares, so
    /// reading the autopilot out of the heartbeat is what makes the log correct.
    /// </summary>
    [Fact]
    public void TheSameNumberDecodesDifferentlyPerFirmware()
    {
        var t0 = new DateTime(2026, 8, 3, 12, 0, 0, DateTimeKind.Utc);
        uint px4Hold = Px4FlightModes.Pack(mainMode: 4, subMode: 3);

        var asPx4 = FlightLogAnalyzer.Analyze(
            new[] { Heartbeat(t0, MavAutopilotPx4, MavTypeQuadrotor, px4Hold) });
        var asArduPilot = FlightLogAnalyzer.Analyze(
            new[] { Heartbeat(t0, MavAutopilotArdupilot, MavTypeQuadrotor, px4Hold) });

        Assert.NotEqual(ModeEvents(asPx4)[0].Text, ModeEvents(asArduPilot)[0].Text);
        Assert.Contains("HOLD", ModeEvents(asPx4)[0].Text);
    }

    [Fact]
    public void ArmAndDisarmAreFoundInAPx4LogToo()
    {
        var t0 = new DateTime(2026, 8, 3, 12, 0, 0, DateTimeKind.Utc);
        uint position = Px4FlightModes.Pack(3, 0);

        var records = new[]
        {
            Heartbeat(t0, MavAutopilotPx4, MavTypeQuadrotor, position),
            Heartbeat(t0.AddSeconds(2), MavAutopilotPx4, MavTypeQuadrotor, position, armed: true),
            Heartbeat(t0.AddSeconds(62), MavAutopilotPx4, MavTypeQuadrotor, position, armed: false),
        };

        var summary = FlightLogAnalyzer.Analyze(records);

        Assert.Equal(1, summary.ArmCount);
        Assert.Equal(TimeSpan.FromSeconds(60), summary.ArmedDuration);
    }
}

using GCS.Core.Domain;
using GCS.Core.Mavlink;
using GCS.Core.Swarm;
using MavLinkSharp;
using Xunit;

namespace GCS.Core.Tests;

/// <summary>
/// PX4 Follow-Me formation. PX4 has no AP_Follow, so a formation is flown by the
/// GCS streaming the leader's position while each follower holds a polar station
/// around it — which makes the angle convention and the message contents the two
/// things that decide where a drone actually flies.
/// </summary>
public class Px4FollowConfigurationTests
{
    private const float Tolerance = 0.01f;

    /// <summary>
    /// PX4 measures the follow angle clockwise from the target's course: 0 is in
    /// front of it, +90 off its right, ±180 behind, -90 off its left. Getting the
    /// sign wrong puts the whole formation on the wrong side of the leader.
    /// </summary>
    [Theory]
    [InlineData(100, 0, 0)]        // straight ahead
    [InlineData(0, 100, 90)]       // off the right wing
    [InlineData(-100, 0, 180)]     // directly behind
    [InlineData(0, -100, -90)]     // off the left wing
    [InlineData(100, 100, 45)]     // ahead and right
    [InlineData(-100, 100, 135)]   // behind and right
    [InlineData(-100, -100, -135)] // behind and left
    [InlineData(100, -100, -45)]   // ahead and left
    public void FollowAngleIsClockwiseFromTheLeadersCourse(
        double forward, double right, float expectedDeg)
    {
        var offset = new FormationOffset(forward, right, 0);

        Assert.Equal(expectedDeg, Px4FollowConfiguration.FollowAngleDeg(offset), Tolerance);
    }

    [Fact]
    public void TheAngleAlwaysLandsInTheRangePx4Accepts()
    {
        // Every station a formation can produce, swept all the way round.
        for (int deg = 0; deg < 360; deg++)
        {
            double rad = deg * Math.PI / 180.0;
            var offset = new FormationOffset(50 * Math.Cos(rad), 50 * Math.Sin(rad), 0);

            float angle = Px4FollowConfiguration.FollowAngleDeg(offset);

            Assert.InRange(angle, -180f, 180f);
        }
    }

    [Fact]
    public void DistanceIsTheHorizontalSeparationIgnoringTheVerticalStep()
    {
        // 3-4-5 triangle, with 20 m of altitude stagger that must not inflate it.
        var offset = new FormationOffset(-30, 40, 20);

        Assert.Equal(50f, Px4FollowConfiguration.FollowDistanceM(offset), Tolerance);
    }

    [Fact]
    public void DistanceIsRaisedToWhatPx4WillAccept()
    {
        // PX4 rejects anything under 1 m; a station closer than that would be
        // silently overridden by the vehicle.
        var offset = new FormationOffset(0.2, 0.2, 0);

        Assert.Equal(Px4FollowConfiguration.MinDistanceM,
                     Px4FollowConfiguration.FollowDistanceM(offset));
    }

    [Fact]
    public void HeightIsTheLeadersAltitudeLessTheFormationsVerticalStep()
    {
        var offset = new FormationOffset(-50, 0, 15);   // 15 m below the leader

        Assert.Equal(45f, Px4FollowConfiguration.FollowHeightM(offset, 60f), Tolerance);
    }

    [Fact]
    public void HeightNeverDropsBelowPx4sFloor()
    {
        // A low leader with a big vertical step would otherwise ask for a height
        // the vehicle will not fly.
        var offset = new FormationOffset(-50, 0, 20);

        Assert.Equal(Px4FollowConfiguration.MinHeightM,
                     Px4FollowConfiguration.FollowHeightM(offset, 12f));
    }

    [Fact]
    public void AStationOnTopOfTheLeaderDoesNotProduceAnUndefinedAngle()
    {
        var offset = new FormationOffset(0, 0, 0);

        Assert.Equal(0f, Px4FollowConfiguration.FollowAngleDeg(offset));
        Assert.Equal(Px4FollowConfiguration.MinDistanceM,
                     Px4FollowConfiguration.FollowDistanceM(offset));
    }

    [Fact]
    public void TheParameterSetCarriesEveryValuePx4NeedsAndNoEnableFlag()
    {
        var offset = new FormationOffset(-40, 30, 10);
        var parameters = Px4FollowConfiguration.ForFollower(offset, leaderHeightAboveHomeM: 50);

        var byName = parameters.ToDictionary(p => p.Key, p => p.Value);

        Assert.Equal(50f, byName["FLW_TGT_DST"], Tolerance);
        Assert.Equal(143.13f, byName["FLW_TGT_FA"], 0.01f);
        Assert.Equal(40f, byName["FLW_TGT_HT"], Tolerance);
        Assert.Equal(0f, byName["FLW_TGT_ALT_M"]);

        // Following is a flight mode on PX4, not a parameter — an enable flag here
        // would be a parameter the vehicle does not have.
        Assert.DoesNotContain(parameters, p => p.Key.Contains("ENABLE"));
        Assert.DoesNotContain(parameters, p => p.Key.StartsWith("FOLL_"));
    }

    /// <summary>
    /// A column formation must sit behind the leader on PX4 exactly as it does on
    /// ArduPilot — the two firmwares describe the same station different ways.
    /// </summary>
    [Fact]
    public void AColumnFormationTrailsTheLeaderOnPx4Too()
    {
        var stations = FormationGeometry.Compute(FormationType.Column, followerCount: 3, spacingM: 40);

        foreach (var station in stations)
            Assert.Equal(180f, Math.Abs(Px4FollowConfiguration.FollowAngleDeg(station)), Tolerance);

        Assert.Equal(40f, Px4FollowConfiguration.FollowDistanceM(stations[0]), Tolerance);
        Assert.Equal(120f, Px4FollowConfiguration.FollowDistanceM(stations[2]), Tolerance);
    }

    /// <summary>
    /// Echelon right puts every follower behind and to the right, so the angles
    /// must all sit in the rear-right quadrant (90°..180°).
    /// </summary>
    [Fact]
    public void EchelonRightKeepsEveryFollowerBehindAndRight()
    {
        var stations = FormationGeometry.Compute(FormationType.EchelonRight, 3, 40);

        foreach (var station in stations)
            Assert.InRange(Px4FollowConfiguration.FollowAngleDeg(station), 90f, 180f);
    }

    [Fact]
    public void EchelonLeftMirrorsIt()
    {
        var stations = FormationGeometry.Compute(FormationType.EchelonLeft, 3, 40);

        foreach (var station in stations)
            Assert.InRange(Px4FollowConfiguration.FollowAngleDeg(station), -180f, -90f);
    }
}

/// <summary>The FOLLOW_TARGET packet the relay streams, and when it streams it.</summary>
public class FollowTargetRelayTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private static PositionState Leader(DateTime at, double lat = 40.4516, double lon = 50.0659) =>
        new(lat, lon,
            AltitudeMslMeters: 120f, AltitudeRelMeters: 60f, HeadingDeg: 90f,
            VelocityNorthMps: 1.5f, VelocityEastMps: -2.5f, VelocityDownMps: 0.25f,
            TimestampUtc: at);

    [Fact]
    public void APositionThatIsCurrentGetsRelayed()
    {
        var action = FollowTargetRelay.Decide(
            Leader(T0), followerCount: 2, nowUtc: T0.AddSeconds(1), FollowTargetRelay.LeaderStaleAfter);

        Assert.Equal(RelayAction.Send, action);
    }

    [Fact]
    public void AFrozenLeaderPositionIsNotRelayed()
    {
        // Streaming a stale position would hold the followers around a place the
        // leader has already left.
        var action = FollowTargetRelay.Decide(
            Leader(T0), followerCount: 2, nowUtc: T0.AddSeconds(5), FollowTargetRelay.LeaderStaleAfter);

        Assert.Equal(RelayAction.LeaderStale, action);
    }

    [Fact]
    public void NothingIsSentBeforeTheLeaderHasAPosition()
    {
        var action = FollowTargetRelay.Decide(null, 2, T0, FollowTargetRelay.LeaderStaleAfter);

        Assert.Equal(RelayAction.NoLeaderPosition, action);
    }

    [Fact]
    public void NothingIsSentWithNoFollowers()
    {
        var action = FollowTargetRelay.Decide(Leader(T0), 0, T0, FollowTargetRelay.LeaderStaleAfter);

        Assert.Equal(RelayAction.NoFollowers, action);
    }

    /// <summary>
    /// We go quiet before PX4's own 3 s target timeout, so a follower reaches its
    /// hold because we stopped, not because a dropout went unnoticed.
    /// </summary>
    [Fact]
    public void WeStopSendingSoonerThanPx4GivesUp()
    {
        Assert.True(FollowTargetRelay.LeaderStaleAfter < TimeSpan.FromSeconds(3));
    }

    /// <summary>
    /// PX4 fuses a new target position at most every 500 ms and calls the target
    /// lost after 3 s, so the rate has to sit between those two.
    /// </summary>
    [Fact]
    public void TheRateSuitsWhatPx4DoesWithTheMessages()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(500), FollowTargetRelay.Interval);
        Assert.True(FollowTargetRelay.Interval < TimeSpan.FromSeconds(3));
    }

    /// <summary>
    /// The packet has to survive a round trip through the MAVLink framing with
    /// every field intact — PX4 reads lat, lon, alt and the velocity triplet, and
    /// a wrong scale factor or field name would fly the formation somewhere else.
    /// </summary>
    [Fact]
    public void TheFollowTargetPacketCarriesTheLeadersPositionAndVelocity()
    {
        MavlinkInit.EnsureInitialized();
        var leader = Leader(T0);

        var packet = Mavlink2Serializer.FollowTarget(
            senderSys: 255, senderComp: 190,
            leader.LatitudeDeg, leader.LongitudeDeg, leader.AltitudeMslMeters,
            leader.VelocityNorthMps, leader.VelocityEastMps, leader.VelocityDownMps,
            timestampMs: 1234);

        var frame = new Frame();
        Assert.True(frame.TryParse(packet.Span));
        Assert.Equal(144u, frame.MessageId);
        Assert.Equal(255, frame.SystemId);

        var fields = frame.Fields;
        Assert.Equal(404516000, Convert.ToInt32(fields["lat"]));
        Assert.Equal(500659000, Convert.ToInt32(fields["lon"]));
        Assert.Equal(120f, Convert.ToSingle(fields["alt"]), 0.001f);
        Assert.Equal(1234UL, Convert.ToUInt64(fields["timestamp"]));

        // NED, in the order PX4 reads as vx, vy, vz.
        var velocity = (Array)fields["vel"];
        Assert.Equal(1.5f, Convert.ToSingle(velocity.GetValue(0)), 0.001f);
        Assert.Equal(-2.5f, Convert.ToSingle(velocity.GetValue(1)), 0.001f);
        Assert.Equal(0.25f, Convert.ToSingle(velocity.GetValue(2)), 0.001f);

        // Bit 0 = position, bit 1 = velocity: what we actually know about the leader.
        Assert.Equal(0b11, Convert.ToInt32(fields["est_capabilities"]));
    }

    /// <summary>
    /// A fleet-wide mode command must not put the leader into follow: the target
    /// being streamed is the leader's own position, so it would chase itself.
    /// </summary>
    [Theory]
    [InlineData("FOLLOW", true)]        // ArduPilot
    [InlineData("FOLLOW ME", true)]     // PX4
    [InlineData("FOLLOW_ME", true)]     // as it may be typed
    [InlineData("follow me", true)]
    [InlineData("GUIDED", false)]
    [InlineData("RTL", false)]
    [InlineData("HOLD", false)]
    [InlineData("LOITER", false)]
    [InlineData("", false)]
    public void FollowModesAreRecognisedAcrossBothFirmwares(string modeName, bool expected)
    {
        Assert.Equal(expected, FlightModeTable.IsFollowMode(modeName));
    }

    /// <summary>The name in the PX4 mode list has to be one the guard catches.</summary>
    [Fact]
    public void Px4sOwnFollowModeNameTripsTheGuard()
    {
        var followMe = Px4FlightModes.All.Single(m => FlightModeTable.IsFollowMode(m.Name));

        // AUTO main mode, FOLLOW_TARGET sub mode.
        Assert.Equal(4, followMe.Px4MainMode);
        Assert.Equal(8, followMe.Px4SubMode);
    }

    /// <summary>
    /// PX4 clears "no connection to the ground control station" only on a
    /// heartbeat whose type is MAV_TYPE_GCS, and blocks arming without one when
    /// NAV_DLL_ACT is set. ArduPilot's FS_GCS_ENABL watches the same message.
    /// </summary>
    [Fact]
    public void TheGcsHeartbeatIsTheTypeAVehicleLooksFor()
    {
        MavlinkInit.EnsureInitialized();

        var packet = Mavlink2Serializer.GcsHeartbeat(senderSys: 255, senderComp: 190);

        var frame = new Frame();
        Assert.True(frame.TryParse(packet.Span));
        Assert.Equal(0u, frame.MessageId);
        Assert.Equal(255, frame.SystemId);

        // MAV_TYPE_GCS. Anything else and the vehicle keeps waiting.
        Assert.Equal(6, Convert.ToInt32(frame.Fields["type"]));

        // MAV_AUTOPILOT_INVALID: we are not an autopilot, and claiming to be one
        // would put a phantom vehicle in our own roster.
        Assert.Equal(8, Convert.ToInt32(frame.Fields["autopilot"]));
    }

    [Fact]
    public void SouthernAndWesternPositionsKeepTheirSign()
    {
        MavlinkInit.EnsureInitialized();

        var packet = Mavlink2Serializer.FollowTarget(
            255, 190, -33.8688, -151.2093, 50f, 0, 0, 0, timestampMs: 1);

        var frame = new Frame();
        Assert.True(frame.TryParse(packet.Span));
        Assert.Equal(-338688000, Convert.ToInt32(frame.Fields["lat"]));
        Assert.Equal(-1512093000, Convert.ToInt32(frame.Fields["lon"]));
    }
}

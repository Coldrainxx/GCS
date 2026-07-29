using GCS.Core.Swarm;
using Xunit;

namespace GCS.Core.Tests;

public class FormationGeometryTests
{
    private const double Tol = 1e-6;

    [Fact]
    public void NoFollowersProducesNoStations()
    {
        Assert.Empty(FormationGeometry.Compute(FormationType.Vee, 0, 50));
        Assert.Empty(FormationGeometry.Compute(FormationType.Vee, -3, 50));
    }

    [Fact]
    public void ColumnTrailsDirectlyBehindWithEvenSpacing()
    {
        var f = FormationGeometry.Compute(FormationType.Column, 3, 40);

        Assert.Equal(3, f.Count);
        Assert.All(f, o => Assert.Equal(0, o.Right, Tol));      // no lateral offset
        Assert.All(f, o => Assert.True(o.Forward < 0));          // all behind the leader
        Assert.Equal(-40, f[0].Forward, Tol);
        Assert.Equal(-80, f[1].Forward, Tol);
        Assert.Equal(-120, f[2].Forward, Tol);
    }

    [Fact]
    public void LineAbreastAlternatesSidesAndStaysLevelWithLeader()
    {
        var f = FormationGeometry.Compute(FormationType.LineAbreast, 4, 30);

        Assert.All(f, o => Assert.Equal(0, o.Forward, Tol));     // nobody fore or aft
        Assert.Equal(30, f[0].Right, Tol);   // first to the right
        Assert.Equal(-30, f[1].Right, Tol);  // second to the left
        Assert.Equal(60, f[2].Right, Tol);
        Assert.Equal(-60, f[3].Right, Tol);
    }

    [Fact]
    public void VeeSweepsBackAndOutSymmetrically()
    {
        var f = FormationGeometry.Compute(FormationType.Vee, 4, 50);

        Assert.All(f, o => Assert.True(o.Forward < 0));                     // swept back
        Assert.Equal(f[0].Forward, f[1].Forward, Tol);                      // pairs level
        Assert.Equal(f[0].Right, -f[1].Right, Tol);                         // mirrored
        Assert.True(System.Math.Abs(f[2].Right) > System.Math.Abs(f[0].Right)); // wider further out

        // Spacing is the distance along the arm, not its components.
        Assert.Equal(50, f[0].HorizontalDistance, 1e-4);
        Assert.Equal(100, f[2].HorizontalDistance, 1e-4);
    }

    [Theory]
    [InlineData(FormationType.EchelonRight, 1)]
    [InlineData(FormationType.EchelonLeft, -1)]
    public void EchelonKeepsEveryFollowerOnOneSide(FormationType type, int expectedSign)
    {
        var f = FormationGeometry.Compute(type, 3, 25);

        Assert.All(f, o => Assert.True(o.Forward < 0));
        Assert.All(f, o => Assert.True(System.Math.Sign(o.Right) == expectedSign));
        // Each station is further out than the last.
        Assert.True(System.Math.Abs(f[1].Right) > System.Math.Abs(f[0].Right));
        Assert.True(System.Math.Abs(f[2].Right) > System.Math.Abs(f[1].Right));
    }

    [Fact]
    public void CircleSpacesFollowersEvenlyAtTheGivenRadius()
    {
        var f = FormationGeometry.Compute(FormationType.Circle, 4, 60);

        Assert.All(f, o => Assert.Equal(60, o.HorizontalDistance, 1e-4));
        // Four followers => quarters: ahead, right, behind, left.
        Assert.Equal(60, f[0].Forward, 1e-4);
        Assert.Equal(60, f[1].Right, 1e-4);
        Assert.Equal(-60, f[2].Forward, 1e-4);
        Assert.Equal(-60, f[3].Right, 1e-4);
    }

    [Fact]
    public void DiamondPlacesWingmenThenSlot()
    {
        var f = FormationGeometry.Compute(FormationType.Diamond, 3, 40);

        Assert.True(f[0].Right < 0);                       // left wing
        Assert.True(f[1].Right > 0);                       // right wing
        Assert.Equal(f[0].Forward, f[1].Forward, Tol);     // wingmen level
        Assert.Equal(0, f[2].Right, Tol);                  // slot dead astern
        Assert.True(f[2].Forward < f[0].Forward);          // and further back
    }

    [Fact]
    public void VerticalStepStacksFollowersBelowTheLeader()
    {
        var f = FormationGeometry.Compute(FormationType.LineAbreast, 3, 30, verticalStepM: 5);

        Assert.Equal(0, f[0].Down, Tol);
        Assert.Equal(5, f[1].Down, Tol);
        Assert.Equal(10, f[2].Down, Tol);
    }

    [Fact]
    public void ZeroVerticalStepLeavesTheWholeFormationAtLeaderAltitude()
    {
        var f = FormationGeometry.Compute(FormationType.Vee, 4, 50);
        Assert.All(f, o => Assert.Equal(0, o.Down, Tol));
    }

    [Fact]
    public void NegativeSpacingIsTreatedAsDistance()
    {
        var a = FormationGeometry.Compute(FormationType.Column, 2, 40);
        var b = FormationGeometry.Compute(FormationType.Column, 2, -40);
        Assert.Equal(a[0].Forward, b[0].Forward, Tol);
    }

    // ── Station -> ground position (drives the map preview) ──────────

    [Fact]
    public void HeadingNorth_ForwardOffsetGoesNorth()
    {
        var (lat, lon) = FormationGeometry.StationPosition(
            40.0, 50.0, leaderHeadingDeg: 0, new FormationOffset(Forward: 111.0, Right: 0, Down: 0));

        Assert.True(lat > 40.0);                 // ahead => north
        Assert.Equal(50.0, lon, 5);              // no lateral drift
        Assert.Equal(0.001, lat - 40.0, 4);      // ~111 m ≈ 0.001°
    }

    [Fact]
    public void HeadingEast_ForwardOffsetGoesEast()
    {
        var (lat, lon) = FormationGeometry.StationPosition(
            40.0, 50.0, leaderHeadingDeg: 90, new FormationOffset(Forward: 100.0, Right: 0, Down: 0));

        Assert.Equal(40.0, lat, 5);
        Assert.True(lon > 50.0);
    }

    [Fact]
    public void HeadingNorth_RightOffsetGoesEast()
    {
        var (lat, lon) = FormationGeometry.StationPosition(
            40.0, 50.0, leaderHeadingDeg: 0, new FormationOffset(Forward: 0, Right: 100.0, Down: 0));

        Assert.Equal(40.0, lat, 5);
        Assert.True(lon > 50.0);   // leader's right, facing north, is east
    }

    [Fact]
    public void HeadingSouth_RightOffsetGoesWest()
    {
        var (_, lon) = FormationGeometry.StationPosition(
            40.0, 50.0, leaderHeadingDeg: 180, new FormationOffset(Forward: 0, Right: 100.0, Down: 0));

        Assert.True(lon < 50.0);   // formation rotates with the leader
    }

    [Fact]
    public void ZeroOffsetSitsOnTheLeader()
    {
        var (lat, lon) = FormationGeometry.StationPosition(
            40.0, 50.0, 123, new FormationOffset(0, 0, 0));

        Assert.Equal(40.0, lat, 9);
        Assert.Equal(50.0, lon, 9);
    }
}

public class FollowConfigurationTests
{
    [Fact]
    public void BuildsTheFollowParameterSetForAStation()
    {
        var offset = new FormationOffset(-35.0, 35.0, 5.0);

        var p = FollowConfiguration.ForFollower(
            leaderSystemId: 7, offset, FollowYawBehaviour.SameAsLeadVehicle, maxDistanceM: 200);

        var map = new Dictionary<string, float>(p);

        Assert.Equal(7, map["FOLL_SYSID"]);
        Assert.Equal(1, map["FOLL_OFS_TYPE"]);     // relative to leader heading
        Assert.Equal(-35f, map["FOLL_OFS_X"]);     // forward (behind)
        Assert.Equal(35f, map["FOLL_OFS_Y"]);      // right
        Assert.Equal(5f, map["FOLL_OFS_Z"]);       // below
        Assert.Equal(2, map["FOLL_YAW_BEHAVE"]);
        Assert.Equal(200f, map["FOLL_DIST_MAX"]);
        Assert.Equal(1, map["FOLL_ENABLE"]);
    }

    [Fact]
    public void EnableIsWrittenLastSoTheFollowerIsFullyConfiguredFirst()
    {
        var p = FollowConfiguration.ForFollower(1, new FormationOffset(-20, 0, 0));
        Assert.Equal("FOLL_ENABLE", p[^1].Key);
    }

    [Fact]
    public void DisableTurnsFollowingOff()
    {
        var p = FollowConfiguration.Disable();
        Assert.Single(p);
        Assert.Equal("FOLL_ENABLE", p[0].Key);
        Assert.Equal(0, p[0].Value);
    }
}

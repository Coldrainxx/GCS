using GCS.Core.Domain;
using GCS.Core.Mission;

namespace GCS.Core.Tests;

public class WaypointFileTests
{
    [Fact]
    public void RoundTrip_PreservesItems()
    {
        var items = new List<MissionItem>
        {
            new(0, MavCmd.Waypoint, 40.4093, 49.8671, 0, Frame: 0),
            new(1, MavCmd.Takeoff, 40.4100, 49.8680, 50, Param1: 12f, Frame: 3),
            new(2, MavCmd.Waypoint, 40.4200, 49.8700, 120, Param2: 25f, Frame: 3),
            new(3, MavCmd.ReturnToLaunch, 0, 0, 0, Frame: 3),
        };

        var text = WaypointFile.Serialize(items);
        var parsed = WaypointFile.Parse(text);

        Assert.Equal(items.Count, parsed.Count);
        for (int i = 0; i < items.Count; i++)
        {
            Assert.Equal(items[i].Sequence, parsed[i].Sequence);
            Assert.Equal(items[i].Command, parsed[i].Command);
            Assert.Equal(items[i].Frame, parsed[i].Frame);
            Assert.Equal(items[i].LatitudeDeg, parsed[i].LatitudeDeg, 6);
            Assert.Equal(items[i].LongitudeDeg, parsed[i].LongitudeDeg, 6);
            Assert.Equal(items[i].AltitudeMeters, parsed[i].AltitudeMeters, 3);
            Assert.Equal(items[i].Param2, parsed[i].Param2, 3);
        }
    }

    [Fact]
    public void Parse_SkipsMalformedLines()
    {
        var text = "QGC WPL 110\n" +
                   "0\t1\t0\t16\t0\t0\t0\t0\t40.1\t49.1\t100\t1\n" +
                   "garbage line that is not valid\n" +
                   "2\t0\t3\t16\t0\t0\t0\t0\t40.2\t49.2\t110\t1\n";

        var parsed = WaypointFile.Parse(text);

        Assert.Equal(2, parsed.Count); // the junk line is skipped, not fatal
    }

    [Fact]
    public void Parse_BadHeader_Throws()
    {
        Assert.Throws<FormatException>(() => WaypointFile.Parse("not a waypoints file\n0\t1\t..."));
    }
}

public class MissionValidatorTests
{
    [Fact]
    public void Empty_Warns()
    {
        var w = MissionValidator.Validate(new List<MissionItem>());
        Assert.Contains(w, m => m.Contains("empty"));
    }

    [Fact]
    public void GoodMission_NoWarnings()
    {
        var items = new List<MissionItem>
        {
            new(0, MavCmd.Waypoint, 40.4, 49.8, 0, Frame: 0),      // home
            new(1, MavCmd.Takeoff, 40.4, 49.8, 50, Frame: 3),
            new(2, MavCmd.Waypoint, 40.41, 49.81, 120, Frame: 3),
            new(3, MavCmd.ReturnToLaunch, 0, 0, 0, Frame: 3),
        };
        Assert.Empty(MissionValidator.Validate(items));
    }

    [Fact]
    public void NotEndingWithLandOrRtl_Warns()
    {
        var items = new List<MissionItem>
        {
            new(0, MavCmd.Waypoint, 40.4, 49.8, 0, Frame: 0),
            new(1, MavCmd.Waypoint, 40.41, 49.81, 120, Frame: 3),
        };
        Assert.Contains(MissionValidator.Validate(items), m => m.Contains("LAND or RTL"));
    }

    [Fact]
    public void ZeroAltitudeWaypoint_Warns()
    {
        var items = new List<MissionItem>
        {
            new(0, MavCmd.Waypoint, 40.4, 49.8, 0, Frame: 0),
            new(1, MavCmd.Waypoint, 40.41, 49.81, 0, Frame: 3),   // rel-alt 0 -> warn
            new(2, MavCmd.Land, 40.42, 49.82, 0, Frame: 3),
        };
        Assert.Contains(MissionValidator.Validate(items), m => m.Contains("altitude"));
    }
}

public class GeoMathTests
{
    [Fact]
    public void Distance_KnownValue()
    {
        // ~1 degree of latitude ≈ 111 km.
        double d = GeoMath.DistanceMeters(0, 0, 1, 0);
        Assert.InRange(d, 110_000, 112_000);
    }

    [Fact]
    public void Distance_SamePoint_IsZero()
    {
        Assert.Equal(0, GeoMath.DistanceMeters(40.4, 49.8, 40.4, 49.8), 3);
    }

    [Fact]
    public void Bearing_DueNorthAndEast()
    {
        double north = GeoMath.BearingDeg(0, 0, 1, 0);
        Assert.True(north < 0.5 || north > 359.5, $"north bearing was {north}"); // ~0
        Assert.InRange(GeoMath.BearingDeg(0, 0, 0, 1), 89.5, 90.5);              // ~90 (east)
    }
}

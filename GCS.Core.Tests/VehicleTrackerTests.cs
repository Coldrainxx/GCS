using GCS.Core.Mavlink.Connection;
using Xunit;

namespace GCS.Core.Tests;

public class MavlinkVehicleTrackerTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void DiscoversEverySystemOnASharedLink()
    {
        var tracker = new MavlinkVehicleTracker(TimeSpan.FromSeconds(5));
        var discovered = new List<byte>();
        tracker.VehicleDiscovered += id => discovered.Add(id);

        // Three drones interleaved on one radio, several heartbeats each.
        for (int i = 0; i < 3; i++)
            foreach (byte sysid in new byte[] { 1, 2, 3 })
                tracker.OnHeartbeat(sysid, 1, T0.AddSeconds(i));

        Assert.Equal(new byte[] { 1, 2, 3 }, tracker.KnownSystems);
        Assert.Equal(3, tracker.Count);
        // Discovery fires once per vehicle, not once per heartbeat.
        Assert.Equal(new byte[] { 1, 2, 3 }, discovered);
    }

    [Fact]
    public void RetiresOnlyTheVehicleThatWentQuiet()
    {
        var tracker = new MavlinkVehicleTracker(TimeSpan.FromSeconds(5));
        var lost = new List<byte>();
        tracker.VehicleLost += id => lost.Add(id);

        tracker.OnHeartbeat(1, 1, T0);
        tracker.OnHeartbeat(2, 1, T0);

        // Drone 1 keeps talking, drone 2 goes silent.
        tracker.OnHeartbeat(1, 1, T0.AddSeconds(10));
        tracker.Tick(T0.AddSeconds(10));

        Assert.Equal(new byte[] { 2 }, lost);
        Assert.Equal(new byte[] { 1 }, tracker.KnownSystems);
        Assert.False(tracker.IsKnown(2));
    }

    [Fact]
    public void ComponentIdIsRememberedPerVehicle()
    {
        var tracker = new MavlinkVehicleTracker(TimeSpan.FromSeconds(5));
        tracker.OnHeartbeat(7, 1, T0);

        Assert.Equal(1, tracker.ComponentIdOf(7));
        Assert.Equal(0, tracker.ComponentIdOf(99)); // unknown
    }
}

// Connection-tracker tests (including the shared-link primary rules) live in
// MavlinkConnectionTrackerTests.cs.

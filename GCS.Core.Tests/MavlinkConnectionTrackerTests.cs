using GCS.Core.Mavlink.Connection;

namespace GCS.Core.Tests;

public class MavlinkConnectionTrackerTests
{
    private static MavlinkConnectionTracker NewTracker(out List<MavlinkConnectionState> events)
    {
        var captured = new List<MavlinkConnectionState>();
        var tracker = new MavlinkConnectionTracker(TimeSpan.FromSeconds(3));
        tracker.ConnectionChanged += s => captured.Add(s);
        events = captured;
        return tracker;
    }

    [Fact]
    public void FirstHeartbeat_Connects_AndRaisesOnce()
    {
        var tracker = NewTracker(out var events);
        var now = DateTime.UtcNow;

        tracker.OnHeartbeat(1, 1, now);

        Assert.True(tracker.IsConnected);
        Assert.Equal(1, (int)tracker.SystemId);
        Assert.Single(events);
        Assert.True(events[0].IsConnected);
    }

    [Fact]
    public void SecondHeartbeatSameVehicle_DoesNotRaiseAgain()
    {
        var tracker = NewTracker(out var events);
        var now = DateTime.UtcNow;

        tracker.OnHeartbeat(1, 1, now);
        tracker.OnHeartbeat(1, 1, now.AddSeconds(1));

        Assert.Single(events);
    }

    [Fact]
    public void HeartbeatTimeout_Disconnects()
    {
        var tracker = NewTracker(out var events);
        var now = DateTime.UtcNow;

        tracker.OnHeartbeat(1, 1, now);
        tracker.Tick(now.AddSeconds(4));   // exceeds the 3s timeout

        Assert.False(tracker.IsConnected);
        Assert.Equal(2, events.Count);     // connect + disconnect
        Assert.False(events[1].IsConnected);
    }

    [Fact]
    public void TickBeforeTimeout_StaysConnected()
    {
        var tracker = NewTracker(out var events);
        var now = DateTime.UtcNow;

        tracker.OnHeartbeat(1, 1, now);
        tracker.Tick(now.AddSeconds(2));

        Assert.True(tracker.IsConnected);
        Assert.Single(events);
    }

    [Fact]
    public void DifferentVehicleMidSession_RaisesEvent()
    {
        var tracker = NewTracker(out var events);
        var now = DateTime.UtcNow;

        tracker.OnHeartbeat(1, 1, now);
        tracker.OnHeartbeat(2, 1, now.AddSeconds(1));

        Assert.Equal(2, events.Count);
        Assert.Equal(2, (int)tracker.SystemId);
    }

    [Fact]
    public void Reset_WhenConnected_RaisesDisconnect()
    {
        var tracker = NewTracker(out var events);
        var now = DateTime.UtcNow;

        tracker.OnHeartbeat(1, 1, now);
        tracker.Reset();

        Assert.False(tracker.IsConnected);
        Assert.Equal(2, events.Count);
    }
}

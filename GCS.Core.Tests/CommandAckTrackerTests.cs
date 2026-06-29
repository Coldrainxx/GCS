using GCS.Core.Mavlink.CommandAck;

namespace GCS.Core.Tests;

public class CommandAckTrackerTests
{
    [Fact]
    public async Task RegisterThenAck_CompletesWithMappedResult()
    {
        var tracker = new CommandAckTracker();
        var task = tracker.Register(commandId: 400, systemId: 1, componentId: 1);

        Assert.False(task.IsCompleted);

        tracker.OnAck(commandId: 400, systemId: 1, componentId: 1, result: 2); // MAV_RESULT_DENIED

        Assert.Equal(CommandAckResult.Denied, await task);
    }

    [Fact]
    public async Task RegisterThenAck_Accepted()
    {
        var tracker = new CommandAckTracker();
        var task = tracker.Register(400, 1, 1);

        tracker.OnAck(400, 1, 1, 0); // MAV_RESULT_ACCEPTED

        Assert.Equal(CommandAckResult.Accepted, await task);
    }

    [Fact]
    public void AckForDifferentKey_DoesNotComplete()
    {
        var tracker = new CommandAckTracker();
        var task = tracker.Register(400, 1, 1);

        tracker.OnAck(401, 1, 1, 0);          // different command
        tracker.OnAck(400, 2, 1, 0);          // different system

        Assert.False(task.IsCompleted);
    }

    [Fact]
    public async Task DuplicateRegister_CompletesPreviousAwaiter()
    {
        var tracker = new CommandAckTracker();

        var first = tracker.Register(400, 1, 1);
        var second = tracker.Register(400, 1, 1);   // same key supersedes the first

        Assert.True(first.IsCompleted);
        Assert.Equal(CommandAckResult.Failed, await first);
        Assert.False(second.IsCompleted);

        tracker.OnAck(400, 1, 1, 0);
        Assert.Equal(CommandAckResult.Accepted, await second);
    }

    [Fact]
    public void AckWithoutRegister_DoesNotThrow()
    {
        var tracker = new CommandAckTracker();
        tracker.OnAck(400, 1, 1, 0);   // no registration - must be a no-op
    }
}

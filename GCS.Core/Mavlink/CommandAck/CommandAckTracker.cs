using System.Collections.Concurrent;

namespace GCS.Core.Mavlink.CommandAck;

public sealed class CommandAckTracker
{
    private readonly ConcurrentDictionary<CommandKey, PendingCommand> _pending = new();


    private readonly TimeSpan _timeout = TimeSpan.FromSeconds(3);

    public Task<CommandAckResult> Register(
        ushort commandId,
        byte systemId,
        byte componentId)
    {
        var key = new CommandKey(commandId, systemId, componentId);
        var pending = new PendingCommand(commandId);

        // If an identical command is still pending, complete the old awaiter
        // so it cannot hang forever once we overwrite its dictionary slot.
        if (_pending.TryGetValue(key, out var previous))
            previous.Tcs.TrySetResult(CommandAckResult.Failed);

        _pending[key] = pending;
        return pending.Tcs.Task;
    }



    public void OnAck(
        ushort commandId,
        byte systemId,
        byte componentId,
        byte result)
    {
        var key = new CommandKey(commandId, systemId, componentId);

        if (!_pending.TryRemove(key, out var pending))
            return;

        pending.Tcs.TrySetResult(MapResult(result));
    }


    public void Tick()
    {
        var now = DateTime.UtcNow;

        foreach (var kvp in _pending)
        {
            if (now - kvp.Value.SentAtUtc > _timeout)
            {
                if (_pending.TryRemove(kvp.Key, out var pending))
                {
                    pending.Tcs.TrySetResult(CommandAckResult.Timeout);
                }
            }
        }
    }

    private static CommandAckResult MapResult(byte mavResult)
    {
        return mavResult switch
        {
            0 => CommandAckResult.Accepted,   // MAV_RESULT_ACCEPTED
            1 => CommandAckResult.Temporary,  // not always used
            2 => CommandAckResult.Denied,
            3 => CommandAckResult.Unsupported,
            4 => CommandAckResult.Failed,
            5 => CommandAckResult.InProgress,
            _ => CommandAckResult.Failed
        };
    }
}

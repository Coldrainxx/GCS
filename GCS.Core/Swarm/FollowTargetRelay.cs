using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using GCS.Core.Domain;
using GCS.Core.Mavlink;
using GCS.Core.Mission;

namespace GCS.Core.Swarm;

/// <summary>What the relay decided to do on one tick.</summary>
public enum RelayAction
{
    /// <summary>Leader position is current — stream it.</summary>
    Send,

    /// <summary>No position from the leader at all yet.</summary>
    NoLeaderPosition,

    /// <summary>
    /// The leader's position has stopped updating. Sending it anyway would park
    /// the followers around a ghost, so the relay goes quiet instead.
    /// </summary>
    LeaderStale,

    /// <summary>Nobody to relay to.</summary>
    NoFollowers,
}

/// <summary>
/// Streams the leader's position to PX4 followers so they can hold formation.
///
/// PX4's Follow-Me has no notion of following another aircraft — it follows
/// whatever position a ground station streams to it in FOLLOW_TARGET. So to fly
/// a PX4 formation, this reads the leader drone's telemetry and re-broadcasts it
/// as the follow target. The GCS is a required part of the loop, unlike
/// ArduPilot's AP_Follow where the followers listen to the leader directly.
///
/// Sent at 2 Hz: PX4 fuses a new position at most every 500 ms, and treats the
/// target as lost after 3 s of silence, so a faster rate buys nothing and a
/// slower one risks a dropout. A follower that loses the target holds position
/// rather than doing anything abrupt.
/// </summary>
public sealed class FollowTargetRelay : IDisposable
{
    /// <summary>Matches PX4's minimum interval between position fusions.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// How old the leader's position may be before the relay stops sending.
    ///
    /// Deliberately shorter than PX4's own 3 s target timeout, so followers reach
    /// their hold because we stopped talking, not because the link died in a way
    /// we never noticed.
    /// </summary>
    public static readonly TimeSpan LeaderStaleAfter = TimeSpan.FromSeconds(2);

    private readonly Func<PositionState?> _leaderPosition;
    private readonly Func<IReadOnlyList<byte>> _followers;
    private readonly Func<ReadOnlyMemory<byte>, byte, CancellationToken, Task> _send;

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private readonly Stopwatch _uptime = new();

    /// <summary>Raised when the relay starts or stops sending, with the reason.</summary>
    public event Action<RelayAction>? ActionChanged;

    private RelayAction _lastAction = RelayAction.NoLeaderPosition;

    public bool IsRunning => _loop is { IsCompleted: false };

    /// <param name="leaderPosition">The leader's latest position, or null if it has none.</param>
    /// <param name="followers">System ids of the PX4 vehicles to feed, re-read each tick.</param>
    /// <param name="send">Sends one packet to one vehicle.</param>
    public FollowTargetRelay(
        Func<PositionState?> leaderPosition,
        Func<IReadOnlyList<byte>> followers,
        Func<ReadOnlyMemory<byte>, byte, CancellationToken, Task> send)
    {
        _leaderPosition = leaderPosition;
        _followers = followers;
        _send = send;
    }

    /// <summary>
    /// Whether the leader's position is worth relaying.
    ///
    /// Split out from the loop so the rule can be tested without a clock.
    /// </summary>
    public static RelayAction Decide(
        PositionState? leader, int followerCount, DateTime nowUtc, TimeSpan staleAfter)
    {
        if (followerCount <= 0) return RelayAction.NoFollowers;
        if (leader is null) return RelayAction.NoLeaderPosition;
        if (nowUtc - leader.TimestampUtc > staleAfter) return RelayAction.LeaderStale;
        return RelayAction.Send;
    }

    public void Start()
    {
        if (IsRunning) return;

        _cts = new CancellationTokenSource();
        _uptime.Restart();
        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    public async Task StopAsync()
    {
        if (_cts is null) return;

        _cts.Cancel();

        try
        {
            if (_loop is not null) await _loop;
        }
        catch (OperationCanceledException) { }

        _cts.Dispose();
        _cts = null;
        _loop = null;
        _uptime.Stop();
    }

    private async Task RunAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(Interval);

        try
        {
            while (await timer.WaitForNextTickAsync(token))
            {
                try
                {
                    await TickAsync(token);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    // A relay that dies on one bad tick would leave the followers
                    // holding position with nothing on screen to say why.
                    Debug.WriteLine($"[FollowRelay] Tick failed: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task TickAsync(CancellationToken token)
    {
        var followers = _followers();
        var leader = _leaderPosition();
        var action = Decide(leader, followers.Count, DateTime.UtcNow, LeaderStaleAfter);

        if (action != _lastAction)
        {
            _lastAction = action;
            ActionChanged?.Invoke(action);
        }

        if (action != RelayAction.Send || leader is null) return;

        // FOLLOW_TARGET carries no target_system, so each follower is addressed by
        // where its copy is sent. Built per follower rather than once and repeated,
        // so every packet carries its own sequence number.
        foreach (byte sysId in followers)
        {
            if (token.IsCancellationRequested) return;

            try
            {
                var packet = Mavlink2Serializer.FollowTarget(
                    GcsIdentity.SystemId, GcsIdentity.ComponentId,
                    leader.LatitudeDeg, leader.LongitudeDeg, leader.AltitudeMslMeters,
                    leader.VelocityNorthMps, leader.VelocityEastMps, leader.VelocityDownMps,
                    (ulong)_uptime.ElapsedMilliseconds);

                await _send(packet, sysId, token);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                // One unreachable follower must not stop the others being fed.
                Debug.WriteLine($"[FollowRelay] Send to #{sysId} failed: {ex.Message}");
            }
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }
}

using System;
using GCS.Core.Domain;

namespace GCS.Core.State;

/// <summary>
/// How recently the aircraft actually said anything.
///
/// <see cref="ConnectionState.LastHeartbeatUtc"/> looks like the obvious source but
/// is not: the connection tracker only republishes it on transitions (connect,
/// primary swap, timeout), so on a healthy link it ages forever while telemetry
/// streams normally. Anything needing a live "are we still hearing from it?" signal
/// must use the per-message timestamps below, plus
/// <see cref="ConnectionState.IsConnected"/> for the up/down flag.
/// </summary>
public static class TelemetryFreshness
{
    /// <summary>
    /// Newest timestamp across the streams that arrive continuously, or null when
    /// nothing has been decoded yet.
    /// </summary>
    public static DateTime? LatestUtc(VehicleState state)
    {
        DateTime? newest = null;

        void Consider(DateTime? t)
        {
            if (t is not null && (newest is null || t > newest)) newest = t;
        }

        Consider(state.Attitude?.TimestampUtc);
        Consider(state.Position?.TimestampUtc);
        Consider(state.VfrHud?.TimestampUtc);
        Consider(state.Battery?.TimestampUtc);
        Consider(state.Gps?.TimestampUtc);

        return newest;
    }

    /// <summary>
    /// Whether the link should be considered alive: the tracker says it is up, and
    /// telemetry (if any has ever arrived) is not stale.
    /// </summary>
    public static bool IsLinkAlive(VehicleState state, DateTime nowUtc, TimeSpan timeout)
    {
        if (state.Connection is not { IsConnected: true }) return false;

        DateTime? latest = LatestUtc(state);

        // Connected but nothing decoded yet — the heartbeat itself is the evidence.
        if (latest is null) return true;

        return nowUtc - latest.Value <= timeout;
    }
}

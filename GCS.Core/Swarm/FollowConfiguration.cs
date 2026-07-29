using System.Collections.Generic;

namespace GCS.Core.Swarm;

/// <summary>What a follower points its nose at (ArduPilot FOLL_YAW_BEHAVE).</summary>
public enum FollowYawBehaviour
{
    None = 0,
    FaceLeadVehicle = 1,
    SameAsLeadVehicle = 2,
    DirectionOfFlight = 3,
}

/// <summary>
/// Builds the ArduPilot <c>FOLL_*</c> parameter set that puts one follower on
/// station behind a leader.
///
/// The follower does the station-keeping itself from the leader's position
/// broadcasts, so the formation survives the GCS going away — the GCS only
/// configures it. <c>FOLL_OFS_TYPE = 1</c> makes the offset relative to the
/// leader's heading, so the formation turns with the leader.
/// </summary>
public static class FollowConfiguration
{
    /// <summary>Offset frame: 0 = North/East/Down, 1 = relative to leader heading.</summary>
    public const int OffsetTypeRelativeToLeaderHeading = 1;

    /// <summary>
    /// Parameters to write to a follower, in the order they should be sent
    /// (FOLL_ENABLE last, so the vehicle is fully configured before it engages).
    /// </summary>
    /// <param name="leaderSystemId">MAVLink system id of the leader to follow.</param>
    /// <param name="offset">Station in the leader's body frame.</param>
    /// <param name="yaw">Yaw behaviour while following.</param>
    /// <param name="maxDistanceM">
    /// Distance beyond which the follower gives up (FOLL_DIST_MAX). 0 disables the check.
    /// </param>
    public static IReadOnlyList<KeyValuePair<string, float>> ForFollower(
        byte leaderSystemId,
        FormationOffset offset,
        FollowYawBehaviour yaw = FollowYawBehaviour.SameAsLeadVehicle,
        float maxDistanceM = 0)
    {
        return new List<KeyValuePair<string, float>>
        {
            new("FOLL_SYSID",    leaderSystemId),
            new("FOLL_OFS_TYPE", OffsetTypeRelativeToLeaderHeading),
            new("FOLL_OFS_X",    (float)offset.Forward),
            new("FOLL_OFS_Y",    (float)offset.Right),
            new("FOLL_OFS_Z",    (float)offset.Down),
            new("FOLL_YAW_BEHAVE", (int)yaw),
            new("FOLL_DIST_MAX", maxDistanceM),
            // Enabled last: everything above is in place before the follower acts on it.
            new("FOLL_ENABLE",   1),
        };
    }

    /// <summary>Parameters that take a vehicle out of following.</summary>
    public static IReadOnlyList<KeyValuePair<string, float>> Disable() =>
        new List<KeyValuePair<string, float>> { new("FOLL_ENABLE", 0) };
}

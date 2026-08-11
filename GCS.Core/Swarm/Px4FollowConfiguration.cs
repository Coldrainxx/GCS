using System;
using System.Collections.Generic;

namespace GCS.Core.Swarm;

/// <summary>How a PX4 follower handles altitude while following (FLW_TGT_ALT_M).</summary>
public enum Px4FollowAltitudeMode
{
    /// <summary>Hold a constant altitude above its own home, tracking XY only.</summary>
    ConstantAboveHome = 0,

    /// <summary>Hold a constant height above the terrain below it.</summary>
    ConstantAboveTerrain = 1,

    /// <summary>
    /// Track the target's own altitude. PX4 warns that GPS altitude bias usually
    /// makes this useless, so it is not the default here either.
    /// </summary>
    TrackTargetAltitude = 2,
}

/// <summary>
/// Builds the PX4 <c>FLW_TGT_*</c> parameter set that puts one follower on station.
///
/// This is the PX4 counterpart to <see cref="FollowConfiguration"/>, and the two
/// work quite differently. ArduPilot's follower listens for the leader's own
/// position broadcasts and holds a Forward/Right/Down offset from it, so the
/// formation survives the GCS disappearing. PX4 has no such mechanism: its
/// Follow-Me expects a ground station to stream FOLLOW_TARGET, and each drone
/// holds a polar station — distance and angle — around whatever that message
/// says. So the GCS has to stay in the loop and relay the leader's position.
///
/// The consequence worth knowing: stop the relay and PX4 followers hold position
/// where they are, whereas ArduPilot followers keep following.
/// </summary>
public static class Px4FollowConfiguration
{
    /// <summary>FLW_TGT_DST will not accept less than this.</summary>
    public const float MinDistanceM = 1.0f;

    /// <summary>FLW_TGT_HT will not accept less than this.</summary>
    public const float MinHeightM = 8.0f;

    /// <summary>
    /// Where the station sits around the target, in degrees.
    ///
    /// PX4 measures this clockwise from the target's course: 0 is straight in
    /// front of it, +90 off its right, ±180 directly behind, -90 off its left.
    /// A station given as Forward/Right in the leader's body frame is exactly
    /// atan2(Right, Forward) in that convention, which is why no sign juggling
    /// appears here.
    /// </summary>
    public static float FollowAngleDeg(FormationOffset offset)
    {
        // A station on top of the leader has no meaningful direction; treat it as
        // straight ahead rather than letting atan2(0,0) decide.
        if (offset.Forward == 0 && offset.Right == 0) return 0f;

        return (float)(Math.Atan2(offset.Right, offset.Forward) * 180.0 / Math.PI);
    }

    /// <summary>Horizontal separation from the target, clamped to what PX4 accepts.</summary>
    public static float FollowDistanceM(FormationOffset offset) =>
        Math.Max((float)offset.HorizontalDistance, MinDistanceM);

    /// <summary>
    /// The altitude the follower will hold, in metres above home.
    ///
    /// The formation's vertical step is a distance below the leader, so this is
    /// the leader's height at the moment the formation is applied, less that
    /// step. PX4 clamps it to 8 m, and a formation is not worth flying at a
    /// height the vehicle is going to silently override.
    /// </summary>
    public static float FollowHeightM(FormationOffset offset, float leaderHeightAboveHomeM) =>
        Math.Max(leaderHeightAboveHomeM - (float)offset.Down, MinHeightM);

    /// <summary>
    /// Parameters to write to one follower.
    ///
    /// Unlike the ArduPilot set there is no enable flag at the end: PX4 engages
    /// following by entering the FOLLOW ME flight mode, so these only decide
    /// where the vehicle sits once it is in that mode.
    /// </summary>
    public static IReadOnlyList<KeyValuePair<string, float>> ForFollower(
        FormationOffset offset,
        float leaderHeightAboveHomeM,
        Px4FollowAltitudeMode altitudeMode = Px4FollowAltitudeMode.ConstantAboveHome)
    {
        return new List<KeyValuePair<string, float>>
        {
            new("FLW_TGT_DST",   FollowDistanceM(offset)),
            new("FLW_TGT_FA",    FollowAngleDeg(offset)),
            new("FLW_TGT_HT",    FollowHeightM(offset, leaderHeightAboveHomeM)),
            new("FLW_TGT_ALT_M", (int)altitudeMode),
        };
    }
}

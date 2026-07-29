using System;
using System.Collections.Generic;

namespace GCS.Core.Swarm;

public enum FormationType
{
    /// <summary>Single file directly behind the leader.</summary>
    Column,
    /// <summary>Side by side on the leader's wingline, alternating right then left.</summary>
    LineAbreast,
    /// <summary>Classic V: swept back and out on alternating sides.</summary>
    Vee,
    /// <summary>All followers stacked back-right of the leader.</summary>
    EchelonRight,
    /// <summary>All followers stacked back-left of the leader.</summary>
    EchelonLeft,
    /// <summary>Evenly spaced around the leader at <c>spacing</c> radius.</summary>
    Circle,
    /// <summary>Left wing, right wing, slot — extra vehicles trail in column.</summary>
    Diamond,
}

/// <summary>
/// A follower's station relative to the leader, in the <b>leader's body frame</b>,
/// in metres: <see cref="Forward"/> towards the leader's nose, <see cref="Right"/>
/// off its right wing, <see cref="Down"/> below it.
///
/// This is the frame ArduPilot's AP_Follow uses when <c>FOLL_OFS_TYPE = 1</c>, so
/// the formation rotates with the leader without the GCS having to recompute it.
/// </summary>
public readonly record struct FormationOffset(double Forward, double Right, double Down)
{
    /// <summary>Horizontal distance from the leader (m).</summary>
    public double HorizontalDistance => Math.Sqrt(Forward * Forward + Right * Right);
}

/// <summary>
/// Turns a formation shape into per-follower offsets. Pure geometry: no MAVLink,
/// no vehicle state, so it can be reasoned about and tested on its own.
/// </summary>
public static class FormationGeometry
{
    // 45° arms: the requested spacing is the distance along the arm, not its
    // forward/lateral components, so "50 m" means 50 m of actual separation.
    private const double Diag = 0.70710678118654752;

    /// <summary>
    /// Station for each follower, ordered by follower index.
    /// </summary>
    /// <param name="type">Formation shape.</param>
    /// <param name="followerCount">Number of followers (leader excluded).</param>
    /// <param name="spacingM">Separation between adjacent stations, metres.</param>
    /// <param name="verticalStepM">
    /// Altitude stagger per follower, metres below the leader. 0 puts the whole
    /// formation at the leader's altitude — deliberate, but it removes vertical
    /// separation as a collision guard.
    /// </param>
    public static IReadOnlyList<FormationOffset> Compute(
        FormationType type,
        int followerCount,
        double spacingM,
        double verticalStepM = 0)
    {
        var result = new List<FormationOffset>();
        if (followerCount <= 0) return result;

        double s = Math.Abs(spacingM);

        for (int i = 0; i < followerCount; i++)
        {
            // rank = how far out from the leader; side alternates right/left.
            int rank = i / 2 + 1;
            int side = (i % 2 == 0) ? 1 : -1;
            double down = verticalStepM * i;

            FormationOffset o = type switch
            {
                FormationType.Column =>
                    new FormationOffset(-(i + 1) * s, 0, down),

                FormationType.LineAbreast =>
                    new FormationOffset(0, side * rank * s, down),

                FormationType.Vee =>
                    new FormationOffset(-rank * s * Diag, side * rank * s * Diag, down),

                FormationType.EchelonRight =>
                    new FormationOffset(-(i + 1) * s * Diag, (i + 1) * s * Diag, down),

                FormationType.EchelonLeft =>
                    new FormationOffset(-(i + 1) * s * Diag, -(i + 1) * s * Diag, down),

                FormationType.Circle =>
                    CircleStation(i, followerCount, s, down),

                FormationType.Diamond =>
                    DiamondStation(i, s, down),

                _ => new FormationOffset(-(i + 1) * s, 0, down),
            };

            result.Add(o);
        }

        return result;
    }

    private static FormationOffset CircleStation(int index, int count, double radius, double down)
    {
        double angle = 2.0 * Math.PI * index / count;
        return new FormationOffset(radius * Math.Cos(angle), radius * Math.Sin(angle), down);
    }

    private static FormationOffset DiamondStation(int index, double s, double down) => index switch
    {
        0 => new FormationOffset(-s * Diag, -s * Diag, down),  // left wing
        1 => new FormationOffset(-s * Diag, s * Diag, down),   // right wing
        2 => new FormationOffset(-2 * s * Diag, 0, down),      // slot
        // Beyond a 4-ship the diamond is full: trail the rest in column.
        _ => new FormationOffset(-(index) * s * Diag - s * Diag, 0, down),
    };

    private const double MetresPerDegreeLat = 110540.0;
    private const double MetresPerDegreeLonAtEquator = 111320.0;

    /// <summary>
    /// Where a station sits on the ground, given the leader's position and heading.
    /// Offsets are in the leader's body frame, so they rotate with it — this is
    /// what lets the formation be drawn on the map before it's flown.
    /// </summary>
    public static (double Lat, double Lon) StationPosition(
        double leaderLat, double leaderLon, double leaderHeadingDeg, FormationOffset offset)
    {
        double hdg = leaderHeadingDeg * Math.PI / 180.0;
        double cos = Math.Cos(hdg), sin = Math.Sin(hdg);

        // Heading is clockwise from north: forward = (cos, sin) in (north, east),
        // right is that rotated 90° clockwise = (-sin, cos).
        double north = offset.Forward * cos - offset.Right * sin;
        double east = offset.Forward * sin + offset.Right * cos;

        double lat = leaderLat + north / MetresPerDegreeLat;
        double lonScale = MetresPerDegreeLonAtEquator * Math.Cos(leaderLat * Math.PI / 180.0);
        double lon = leaderLon + (Math.Abs(lonScale) < 1e-6 ? 0 : east / lonScale);

        return (lat, lon);
    }

    public static string DisplayName(FormationType type) => type switch
    {
        FormationType.Column => "Column (trail)",
        FormationType.LineAbreast => "Line abreast",
        FormationType.Vee => "V formation",
        FormationType.EchelonRight => "Echelon right",
        FormationType.EchelonLeft => "Echelon left",
        FormationType.Circle => "Circle",
        FormationType.Diamond => "Diamond",
        _ => type.ToString()
    };
}

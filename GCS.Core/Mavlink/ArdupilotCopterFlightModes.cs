using System.Collections.Generic;

namespace GCS.Core.Mavlink;

/// <summary>Vehicle families whose mode numbers differ, taken from HEARTBEAT.type.</summary>
public enum VehicleKind
{
    Unknown = 0,
    Plane,
    Copter,
    Rover,
    Submarine,
}

/// <summary>
/// Resolves a HEARTBEAT custom_mode to a mode name for the right vehicle family.
///
/// ArduPilot reuses the same numbers for different modes across vehicles: mode 2 is
/// Stabilize on Plane but AltHold on Copter, mode 5 is FBWA on Plane but Loiter on
/// Copter. Decoding a Copter through the Plane table produces confident, wrong mode
/// names — worse than showing nothing.
/// </summary>
public static class ArdupilotFlightModes
{
    /// <summary>MAV_TYPE values, from the HEARTBEAT message.</summary>
    public static VehicleKind KindFromMavType(byte mavType) => mavType switch
    {
        1 => VehicleKind.Plane,           // FIXED_WING
        2 or 13 or 14 or 15 => VehicleKind.Copter,  // QUADROTOR, HEXAROTOR, OCTOROTOR, TRICOPTER
        3 => VehicleKind.Copter,          // COAXIAL
        4 => VehicleKind.Copter,          // HELICOPTER
        10 or 11 => VehicleKind.Rover,    // GROUND_ROVER, SURFACE_BOAT
        12 => VehicleKind.Submarine,      // SUBMARINE

        // VTOL types (19-25) run ArduPlane and use the plane mode table, including
        // the Q modes.
        >= 19 and <= 25 => VehicleKind.Plane,

        _ => VehicleKind.Unknown,
    };

    private static readonly Dictionary<uint, string> CopterModes = new()
    {
        [0] = "STABILIZE", [1] = "ACRO", [2] = "ALT_HOLD", [3] = "AUTO",
        [4] = "GUIDED", [5] = "LOITER", [6] = "RTL", [7] = "CIRCLE",
        [9] = "LAND", [11] = "DRIFT", [13] = "SPORT", [14] = "FLIP",
        [15] = "AUTOTUNE", [16] = "POSHOLD", [17] = "BRAKE", [18] = "THROW",
        [19] = "AVOID_ADSB", [20] = "GUIDED_NOGPS", [21] = "SMART_RTL",
        [22] = "FLOWHOLD", [23] = "FOLLOW", [24] = "ZIGZAG", [25] = "SYSTEMID",
        [26] = "AUTOROTATE", [27] = "AUTO_RTL",
    };

    private static readonly Dictionary<uint, string> RoverModes = new()
    {
        [0] = "MANUAL", [1] = "ACRO", [3] = "STEERING", [4] = "HOLD",
        [5] = "LOITER", [6] = "FOLLOW", [7] = "SIMPLE", [10] = "AUTO",
        [11] = "RTL", [12] = "SMART_RTL", [15] = "GUIDED", [16] = "INITIALISING",
    };

    /// <summary>
    /// Display name for the mode. Falls back to the raw number rather than guessing,
    /// so an unrecognised vehicle shows something honest.
    /// </summary>
    public static string Describe(VehicleKind kind, uint customMode)
    {
        switch (kind)
        {
            case VehicleKind.Copter:
                return CopterModes.TryGetValue(customMode, out var copter)
                    ? copter : $"MODE {customMode}";

            case VehicleKind.Rover:
            case VehicleKind.Submarine:
                return RoverModes.TryGetValue(customMode, out var rover)
                    ? rover : $"MODE {customMode}";

            case VehicleKind.Plane:
            {
                var mode = ArdupilotPlaneFlightModeMapper.FromCustomMode(customMode);
                return mode is null or Domain.FlightMode.Unknown
                    ? $"MODE {customMode}"
                    : mode.Value.ToString().ToUpperInvariant();
            }

            default:
                return $"MODE {customMode}";
        }
    }

    /// <summary>
    /// Modes this vehicle family offers, in the order a mode list should show them.
    /// </summary>
    public static IReadOnlyList<(string Name, uint CustomMode)> ModesFor(VehicleKind kind)
    {
        switch (kind)
        {
            case VehicleKind.Copter:
                return new[]
                {
                    ("STABILIZE", 0u), ("ALT_HOLD", 2u), ("LOITER", 5u), ("POSHOLD", 16u),
                    ("AUTO", 3u), ("GUIDED", 4u), ("RTL", 6u), ("SMART_RTL", 21u),
                    ("LAND", 9u), ("BRAKE", 17u), ("CIRCLE", 7u), ("ACRO", 1u),
                    ("AUTOTUNE", 15u), ("FOLLOW", 23u),
                };

            case VehicleKind.Rover:
            case VehicleKind.Submarine:
                return new[]
                {
                    ("MANUAL", 0u), ("ACRO", 1u), ("STEERING", 3u), ("HOLD", 4u),
                    ("LOITER", 5u), ("FOLLOW", 6u), ("AUTO", 10u), ("RTL", 11u),
                    ("SMART_RTL", 12u), ("GUIDED", 15u),
                };

            default:
                return new[]
                {
                    ("MANUAL", 0u), ("STABILIZE", 2u), ("FBWA", 5u), ("FBWB", 6u),
                    ("CRUISE", 7u), ("AUTO", 10u), ("RTL", 11u), ("LOITER", 12u),
                    ("GUIDED", 15u), ("CIRCLE", 1u), ("AUTOTUNE", 8u), ("TAKEOFF", 13u),
                    ("QSTABILIZE", 17u), ("QHOVER", 18u), ("QLOITER", 19u),
                    ("QLAND", 20u), ("QRTL", 21u),
                };
        }
    }

    /// <summary>
    /// The custom_mode number to send for a mode name on this vehicle.
    ///
    /// Encoding has to be family-aware just as decoding does: sending plane numbers
    /// to a Copter selects whatever mode happens to share that number — asking for
    /// RTL (plane 11) would put a Copter into DRIFT.
    ///
    /// Returns null when the family has no such mode, so the caller can refuse
    /// rather than send something arbitrary.
    /// </summary>
    public static uint? ToCustomMode(VehicleKind kind, string modeName)
    {
        if (string.IsNullOrWhiteSpace(modeName)) return null;

        string wanted = modeName.Trim().ToUpperInvariant().Replace("-", "").Replace("_", "");

        foreach (var (name, mode) in ModesFor(kind))
        {
            if (name.Replace("_", "").Equals(wanted, System.StringComparison.Ordinal))
                return mode;
        }

        return null;
    }

    /// <summary>
    /// The plane-typed mode, for the screens built around ArduPlane. Null for other
    /// vehicle families, whose numbers mean something different — callers must use
    /// <see cref="Describe"/> for display.
    /// </summary>
    public static Domain.FlightMode? PlaneMode(VehicleKind kind, uint customMode) =>
        kind is VehicleKind.Plane or VehicleKind.Unknown
            ? ArdupilotPlaneFlightModeMapper.FromCustomMode(customMode)
            : null;
}

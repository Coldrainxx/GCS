using System;
using System.Collections.Generic;
using System.Linq;

namespace GCS.Core.Mavlink;

/// <summary>Which autopilot firmware is flying the vehicle, from HEARTBEAT.autopilot.</summary>
public enum AutopilotKind
{
    Unknown = 0,
    ArduPilot,
    Px4,
}

/// <summary>
/// One selectable mode, carrying everything needed to both recognise and command it.
///
/// ArduPilot identifies a mode by a single number; PX4 needs a main/sub pair sent as
/// command parameters. Holding all three means call sites do not have to branch on
/// the autopilot to send a mode.
/// </summary>
public readonly record struct FlightModeChoice(
    string Name,
    uint CustomMode,
    byte Px4MainMode = 0,
    byte Px4SubMode = 0);

/// <summary>
/// PX4's flight modes.
///
/// PX4 does not use a flat mode number. HEARTBEAT.custom_mode packs a main mode into
/// bits 16-23 and a sub mode into bits 24-31, and a mode is commanded with
/// MAV_CMD_DO_SET_MODE carrying those two values rather than SET_MODE carrying one.
/// Decoding it as an ArduPilot number yields values like 196608.
/// </summary>
public static class Px4FlightModes
{
    // PX4_CUSTOM_MAIN_MODE_*
    private const byte MainManual = 1;
    private const byte MainAltCtl = 2;
    private const byte MainPosCtl = 3;
    private const byte MainAuto = 4;
    private const byte MainAcro = 5;
    private const byte MainOffboard = 6;
    private const byte MainStabilized = 7;

    // PX4_CUSTOM_SUB_MODE_AUTO_*
    private const byte AutoReady = 1;
    private const byte AutoTakeoff = 2;
    private const byte AutoLoiter = 3;
    private const byte AutoMission = 4;
    private const byte AutoRtl = 5;
    private const byte AutoLand = 6;
    private const byte AutoFollowTarget = 8;
    private const byte AutoPrecland = 9;

    /// <summary>Pack a main/sub pair the way PX4 reports it in custom_mode.</summary>
    public static uint Pack(byte mainMode, byte subMode) =>
        ((uint)subMode << 24) | ((uint)mainMode << 16);

    public static (byte Main, byte Sub) Unpack(uint customMode) =>
        ((byte)((customMode >> 16) & 0xFF), (byte)((customMode >> 24) & 0xFF));

    /// <summary>
    /// Modes offered for a PX4 vehicle. The list is the same across airframes —
    /// PX4 names modes by function, not by vehicle type.
    /// </summary>
    public static IReadOnlyList<FlightModeChoice> All { get; } = new[]
    {
        Choice("MANUAL", MainManual),
        Choice("STABILIZED", MainStabilized),
        Choice("ACRO", MainAcro),
        Choice("ALTITUDE", MainAltCtl),
        Choice("POSITION", MainPosCtl),
        Choice("HOLD", MainAuto, AutoLoiter),
        Choice("MISSION", MainAuto, AutoMission),
        Choice("TAKEOFF", MainAuto, AutoTakeoff),
        Choice("LAND", MainAuto, AutoLand),
        Choice("RETURN", MainAuto, AutoRtl),
        Choice("FOLLOW ME", MainAuto, AutoFollowTarget),
        Choice("PRECISION LAND", MainAuto, AutoPrecland),
        Choice("OFFBOARD", MainOffboard),
    };

    private static FlightModeChoice Choice(string name, byte main, byte sub = 0) =>
        new(name, Pack(main, sub), main, sub);

    public static string Describe(uint customMode)
    {
        var (main, sub) = Unpack(customMode);

        var exact = All.FirstOrDefault(m => m.Px4MainMode == main && m.Px4SubMode == sub);
        if (!string.IsNullOrEmpty(exact.Name)) return exact.Name;

        // An AUTO sub-mode we do not list is still recognisably automatic; saying so
        // beats printing a packed integer.
        if (main == MainAuto) return $"AUTO ({sub})";

        // READY is transient and not offered as a choice, but is worth naming.
        if (main == MainAuto && sub == AutoReady) return "READY";

        return main == 0 ? $"MODE {customMode}" : $"MODE {main}.{sub}";
    }

    public static FlightModeChoice? Find(string modeName)
    {
        if (string.IsNullOrWhiteSpace(modeName)) return null;

        string wanted = modeName.Trim().ToUpperInvariant().Replace("_", " ");
        foreach (var choice in All)
            if (choice.Name.Equals(wanted, StringComparison.Ordinal)) return choice;

        return null;
    }

    /// <summary>MAV_AUTOPILOT values from HEARTBEAT.</summary>
    public static AutopilotKind KindFromMavAutopilot(byte autopilot) => autopilot switch
    {
        3 => AutopilotKind.ArduPilot,     // MAV_AUTOPILOT_ARDUPILOTMEGA
        12 => AutopilotKind.Px4,          // MAV_AUTOPILOT_PX4
        _ => AutopilotKind.Unknown,
    };
}

/// <summary>
/// Mode handling across autopilots. Call sites use this rather than the per-firmware
/// tables so adding a firmware does not mean touching every screen.
/// </summary>
public static class FlightModeTable
{
    public static string Describe(AutopilotKind autopilot, VehicleKind kind, uint customMode) =>
        autopilot == AutopilotKind.Px4
            ? Px4FlightModes.Describe(customMode)
            : ArdupilotFlightModes.Describe(kind, customMode);

    public static IReadOnlyList<FlightModeChoice> ModesFor(AutopilotKind autopilot, VehicleKind kind) =>
        autopilot == AutopilotKind.Px4
            ? Px4FlightModes.All
            : ArdupilotFlightModes.ModesFor(kind)
                .Select(m => new FlightModeChoice(m.Name, m.CustomMode))
                .ToList();

    /// <summary>
    /// Whether a mode makes the vehicle hold station on something else —
    /// ArduPilot's FOLLOW, PX4's FOLLOW ME.
    ///
    /// Worth asking before commanding a whole fleet at once: the leader of a
    /// formation is the one thing that must never be put into it, or it ends up
    /// chasing the position it is itself producing.
    /// </summary>
    public static bool IsFollowMode(string modeName)
    {
        if (string.IsNullOrWhiteSpace(modeName)) return false;

        string normalised = modeName.Trim().Replace("_", " ");
        return normalised.Equals("FOLLOW", StringComparison.OrdinalIgnoreCase)
            || normalised.Equals("FOLLOW ME", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The mode to send for a name, or null when this vehicle has no such mode —
    /// so a caller can refuse rather than command something arbitrary.
    /// </summary>
    public static FlightModeChoice? Find(AutopilotKind autopilot, VehicleKind kind, string modeName)
    {
        if (autopilot == AutopilotKind.Px4) return Px4FlightModes.Find(modeName);

        uint? mode = ArdupilotFlightModes.ToCustomMode(kind, modeName);
        return mode is null ? null : new FlightModeChoice(modeName, mode.Value);
    }
}

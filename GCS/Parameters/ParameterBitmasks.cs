using System.Collections.Generic;

namespace GCS.Parameters;

/// <summary>
/// Bit definitions for bitmask ArduPilot parameters, keyed by primary name.
/// Labels/order mirror the Mission Planner bitmask editors. A parameter listed
/// here renders as a checkbox list; the integer value is the OR of checked bits.
/// </summary>
public static class ParameterBitmasks
{
    // Build bits from an ordered label list: bit i => value (1 << i).
    private static ParamBit[] B(params string[] labels)
    {
        var bits = new ParamBit[labels.Length];
        for (int i = 0; i < labels.Length; i++)
            bits[i] = new ParamBit(1 << i, labels[i]);
        return bits;
    }

    private static readonly Dictionary<string, ParamBit[]> Map = new()
    {
        ["ARSPD_OPTIONS"] = B(
            "SpeedMismatchDisable",
            "AllowSpeedMismatchRecovery",
            "DisableVoltageCorrection",
            "UseEkf3Consistency",
            "ReportOffset"),

        ["BARO_PROBE_EXT"] = B(
            "BMP085", "BMP280", "MS5611", "MS5607", "MS5637", "FBM320", "DPS280",
            "LPS25H", "Keller", "MS5837", "BMP388", "SPL06", "MSP", "BMP581", "AUAV"),

        ["BRD_SAFETYOPTION"] = B(
            "ActiveForSafetyDisable",
            "ActiveForSafetyEnable",
            "ActiveWhenArmed",
            "Force safety on when the aircraft disarms"),

        ["FLIGHT_OPTIONS"] = B(
            "Rudder mixing in direct flight modes only (Manual/Stabilize/Acro)",
            "Use centered throttle in Cruise or FBWB to indicate trim airspeed",
            "Disable attitude check for takeoff arming",
            "Force target airspeed to trim airspeed in Cruise or FBWB",
            "Climb to RTL_ALTITUDE before turning for RTL",
            "Enable yaw damper in acro mode",
            "Suppress speed scaling during auto takeoffs (prevent oscillations w/o airspeed)",
            "EnableDefaultAirspeed for takeoff",
            "Remove PTCH_TRIM_DEG on the GCS horizon",
            "Remove PTCH_TRIM_DEG on the OSD horizon",
            "Adjust mid-throttle to TRIM_THROTTLE in non-auto throttle modes except MANUAL",
            "Disable suppression of fixed-wing rate gains in ground mode",
            "Enable FBWB style loiter altitude control",
            "Indicate takeoff waiting for neutral rudder with flight control surfaces",
            "In AUTO climb to next waypoint altitude immediately instead of linear climb",
            "Enable autoflap in manual modes (use min of target and actual speed for flap)",
            "Enable aerodynamic load-factor roll limits (airspeed sensor + AIRSPEED_STALL set)"),

        ["MIS_OPTIONS"] = B(
            "Clear Mission on reboot",
            "Use distance to land calc on battery failsafe",
            "ContinueAfterLand",
            "DontZeroCounter"),

        ["TKOFF_OPTIONS"] = B(
            "Let TECS control throttle between min and max during takeoff (airspeed sensor required)"),
    };

    public static IReadOnlyList<ParamBit>? For(string name) =>
        Map.TryGetValue(name, out var bits) ? bits : null;
}

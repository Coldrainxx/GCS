using System.Collections.Generic;

namespace GCS.Parameters;

/// <summary>
/// Enumerated value choices for non-numeric ArduPilot parameters, keyed by
/// primary parameter name. Labels and values mirror the Mission Planner
/// dropdowns. A parameter listed here renders as a dropdown in the editor;
/// everything else stays a numeric text box.
///
/// Bitmask parameters (ARSPD_OPTIONS, BARO_PROBE_EXT, BRD_SAFETYOPTION,
/// FLIGHT_OPTIONS, MIS_OPTIONS, TKOFF_OPTIONS) are intentionally left numeric —
/// they combine multiple bits and need a checkbox editor, not a single select.
/// </summary>
public static class ParameterOptions
{
    private static readonly ParamOption[] OnOff =
    {
        new(0, "Disabled"),
        new(1, "Enabled"),
    };

    private static readonly Dictionary<string, ParamOption[]> Map = new()
    {
        // ── Airspeed / Baro ──────────────────────────────────────────
        ["ARSPD_TUBE_ORDR"] = new ParamOption[]
        {
            new(0, "Auto Detect"),
            new(1, "Normal"),
            new(2, "Swapped"),
        },
        ["ARSPD_AUTOCAL"] = OnOff,
        ["ARSPD_SKIP_CAL"] = new ParamOption[]
        {
            new(0, "Calibrate offset on boot"),
            new(1, "Skip startup calibration"),
        },
        ["ARSPD_TYPE"] = new ParamOption[]
        {
            new(0, "None"),
            new(1, "I2C-MS4525D0"),
            new(2, "Analog"),
            new(3, "I2C-MS5525"),
            new(4, "I2C-MS5525 (0x76)"),
            new(5, "I2C-MS5525 (0x77)"),
            new(6, "I2C-SDP3X"),
            new(7, "I2C-DLVR-5in"),
            new(8, "DroneCAN"),
            new(9, "I2C-DLVR-10in"),
            new(10, "I2C-DLVR-20in"),
            new(12, "I2C-DLVR-60in"),
            new(13, "NMEA water speed"),
            new(14, "MSP"),
            new(15, "ASP5033"),
            new(16, "ExternalAHRS"),
            new(17, "AUAV-10in"),
            new(18, "AUAV-5in"),
            new(19, "AUAV-30in"),
        },
        ["ARSPD_USE"] = new ParamOption[]
        {
            new(0, "Do Not Use"),
            new(1, "Use"),
            new(2, "Use When Zero Throttle"),
        },
        ["BARO_PRIMARY"] = new ParamOption[]
        {
            new(0, "First baro"),
            new(1, "2nd baro"),
            new(2, "3rd baro"),
        },

        // ── Battery ──────────────────────────────────────────────────
        ["BATT_MONITOR"] = new ParamOption[]
        {
            new(0, "Disabled"),
            new(3, "Analog Voltage Only"),
            new(4, "Analog Voltage and Current"),
            new(5, "Solo"),
            new(6, "Bebop"),
            new(7, "SMBus-Generic"),
            new(8, "DroneCAN-BatteryInfo"),
            new(9, "ESC"),
            new(10, "Sum Of Selected Monitors"),
            new(11, "FuelFlow"),
            new(12, "FuelLevelPWM"),
            new(13, "FuelLevelAnalog"),
            new(14, "Analog Current Only"),
            new(15, "INA2XX"),
            new(16, "LTC2946"),
            new(17, "Torqeedo"),
            new(18, "Rotoye"),
            new(19, "MPPT"),
            new(20, "INA3221"),
            new(21, "EFI"),
            new(22, "AD7091R5"),
            new(23, "Scripting"),
        },
        ["BATT_FS_LOW_ACT"] = BattAction(),
        ["BATT_FS_CRT_ACT"] = BattAction(),

        // ── System & Failsafe ────────────────────────────────────────
        ["EFI_TYPE"] = new ParamOption[]
        {
            new(0, "None"),
            new(1, "Serial-MS"),
            new(2, "NWPMU"),
            new(3, "Serial-Lutan"),
            new(5, "DroneCAN"),
            new(6, "Currawong-ECU"),
            new(7, "Scripting"),
            new(8, "Hirth"),
            new(9, "MAVLink"),
            new(10, "Loweheiser"),
        },
        ["FLTMODE_CH"] = FlightModeChannel(),
        ["FS_GCS_ENABLE"] = new ParamOption[]
        {
            new(0, "Disabled"),
            new(1, "Heartbeat"),
            new(2, "Heartbeat and REMRSSI"),
            new(3, "Heartbeat and AUTO"),
        },
        ["FS_LONG_ACTN"] = new ParamOption[]
        {
            new(0, "Continue"),
            new(1, "ReturnToLaunch"),
            new(2, "Glide"),
            new(3, "Deploy Parachute"),
            new(4, "Auto"),
            new(5, "AUTOLAND"),
        },
        ["FS_SHORT_ACTN"] = new ParamOption[]
        {
            new(0, "CIRCLE / no change (if in AUTO/GUIDED/LOITER)"),
            new(1, "CIRCLE"),
            new(2, "FBWA at zero throttle"),
            new(3, "Disable"),
            new(4, "FBWB"),
        },
        ["GEN_TYPE"] = new ParamOption[]
        {
            new(0, "Disabled"),
            new(1, "IE 650w 800w Fuel Cell"),
            new(2, "IE 2.4kW Fuel Cell"),
            new(3, "Richenpower"),
            new(4, "Loweheiser"),
            new(5, "CORTEX"),
        },
        ["ICE_ENABLE"] = OnOff,
        ["DID_ENABLE"] = OnOff,
        ["LGR_ENABLE"] = OnOff,
        ["PUP_ENABLE"] = OnOff,
        ["MIS_RESTART"] = new ParamOption[]
        {
            new(0, "Resume Mission"),
            new(1, "Restart Mission"),
        },
        ["INITIAL_MODE"] = new ParamOption[]
        {
            new(0, "Manual"),
            new(1, "CIRCLE"),
            new(2, "STABILIZE"),
            new(3, "TRAINING"),
            new(4, "ACRO"),
            new(5, "FBWA"),
            new(6, "FBWB"),
            new(7, "CRUISE"),
            new(8, "AUTOTUNE"),
            new(10, "Auto"),
            new(11, "RTL"),
            new(12, "Loiter"),
            new(13, "TAKEOFF"),
            new(14, "AVOID_ADSB"),
            new(15, "Guided"),
            new(17, "QSTABILIZE"),
            new(18, "QHOVER"),
            new(19, "QLOITER"),
            new(20, "QLAND"),
            new(21, "QRTL"),
            new(22, "QAUTOTUNE"),
            new(23, "QACRO"),
            new(24, "THERMAL"),
            new(25, "Loiter to QLand"),
            new(26, "AUTOLAND"),
        },

        // ── Throttle / RTL ───────────────────────────────────────────
        ["THR_FAILSAFE"] = new ParamOption[]
        {
            new(0, "Disabled"),
            new(1, "Enabled"),
            new(2, "EnabledNoFailsafe"),
        },
        ["RTL_AUTOLAND"] = new ParamOption[]
        {
            new(0, "Disable"),
            new(1, "Fly HOME then land via DO_LAND_START mission item"),
            new(2, "Go directly to landing sequence via DO_LAND_START mission item"),
            new(3, "OnlyForGoAround"),
            new(4, "Go directly to landing sequence via DO_RETURN_PATH_START mission item"),
        },

        // ── QuadPlane ────────────────────────────────────────────────
        ["Q_ENABLE"] = OnOff,
        ["Q_FRAME_CLASS"] = new ParamOption[]
        {
            new(0, "Undefined"),
            new(1, "Quad"),
            new(2, "Hexa"),
            new(3, "Octa"),
            new(4, "OctaQuad"),
            new(5, "Y6"),
            new(7, "Tri"),
            new(10, "Single/Dual"),
            new(12, "DodecaHexa"),
            new(14, "Deca"),
            new(15, "Scripting Matrix"),
            new(17, "Dynamic Scripting Matrix"),
        },
        ["Q_FRAME_TYPE"] = new ParamOption[]
        {
            new(0, "Plus"),
            new(1, "X"),
            new(2, "V"),
            new(3, "H"),
            new(4, "V-Tail"),
            new(5, "A-Tail"),
            new(10, "Y6B"),
            new(11, "Y6F"),
            new(12, "BetaFlightX"),
            new(13, "DJIX"),
            new(14, "ClockwiseX"),
            new(15, "I"),
            new(16, "NYT Plus"),
            new(17, "NYT X"),
            new(18, "BetaFlightXReversed"),
            new(19, "Y4"),
        },
        ["Q_M_BAT_IDX"] = new ParamOption[]
        {
            new(0, "First battery"),
            new(1, "Second battery"),
        },
        ["Q_WVANE_ENABLE"] = new ParamOption[]
        {
            new(0, "Disabled"),
            new(1, "Nose into wind"),
            new(2, "Nose or tail into wind"),
            new(3, "Side into wind"),
            new(4, "Tail into wind"),
        },
        ["Q_WVANE_TAKEOFF"] = new ParamOption[]
        {
            new(-1, "No override"),
            new(0, "Disabled"),
            new(1, "Nose into wind"),
            new(2, "Nose or tail into wind"),
            new(3, "Side into wind"),
            new(4, "Tail into wind"),
        },
    };

    // Plane battery-failsafe actions (BATT_FS_LOW_ACT / BATT_FS_CRT_ACT).
    private static ParamOption[] BattAction() => new ParamOption[]
    {
        new(0, "Warn only"),
        new(1, "RTL"),
        new(2, "Land"),
        new(3, "Terminate"),
        new(4, "QLand"),
        new(5, "Parachute"),
        new(6, "Loiter to QLand"),
        new(7, "AUTOLAND or RTL"),
    };

    private static ParamOption[] FlightModeChannel()
    {
        var list = new List<ParamOption> { new(0, "Disabled") };
        for (int i = 1; i <= 16; i++) list.Add(new(i, $"Channel {i}"));
        return list.ToArray();
    }

    public static IReadOnlyList<ParamOption>? For(string name) =>
        Map.TryGetValue(name, out var options) ? options : null;
}

using GCS.Core.Domain;

namespace GCS.Core.Mavlink;

/// <summary>
/// Maps ArduPilot Plane/QuadPlane custom_mode values to FlightMode enum.
/// Reference: https://ardupilot.org/plane/docs/flight-modes.html
/// </summary>
public static class ArdupilotPlaneFlightModeMapper
{
    /// <summary>
    /// Convert FlightMode enum to ArduPilot custom_mode value for SET_MODE command.
    /// </summary>
    public static uint ToCustomMode(FlightMode mode)
    {
        return mode switch
        {
            // Fixed-wing modes
            FlightMode.Manual => 0,
            FlightMode.Circle => 1,
            FlightMode.Stabilize => 2,
            FlightMode.Training => 3,
            FlightMode.Acro => 4,
            FlightMode.Fbwa => 5,
            FlightMode.Fbwb => 6,
            FlightMode.Cruise => 7,
            FlightMode.Autotune => 8,
            FlightMode.Auto => 10,
            FlightMode.Rtl => 11,
            FlightMode.Loiter => 12,
            FlightMode.Takeoff => 13,
            FlightMode.AvoidAdsb => 14,
            FlightMode.Guided => 15,
            FlightMode.Initialising => 16,

            // QuadPlane VTOL modes
            FlightMode.QStabilize => 17,
            FlightMode.QHover => 18,
            FlightMode.QLoiter => 19,
            FlightMode.QLand => 20,
            FlightMode.QRtl => 21,
            FlightMode.QAutotune => 22,
            FlightMode.QAcro => 23,
            FlightMode.Thermal => 24,

            _ => 0
        };
    }

    /// <summary>
    /// Convert ArduPilot custom_mode value from HEARTBEAT to FlightMode enum.
    /// </summary>
    public static FlightMode? FromCustomMode(uint customMode)
    {
        return customMode switch
        {
            // Fixed-wing modes
            0 => FlightMode.Manual,
            1 => FlightMode.Circle,
            2 => FlightMode.Stabilize,
            3 => FlightMode.Training,
            4 => FlightMode.Acro,
            5 => FlightMode.Fbwa,
            6 => FlightMode.Fbwb,
            7 => FlightMode.Cruise,
            8 => FlightMode.Autotune,
            10 => FlightMode.Auto,
            11 => FlightMode.Rtl,
            12 => FlightMode.Loiter,
            13 => FlightMode.Takeoff,
            14 => FlightMode.AvoidAdsb,
            15 => FlightMode.Guided,
            16 => FlightMode.Initialising,

            // QuadPlane VTOL modes
            17 => FlightMode.QStabilize,
            18 => FlightMode.QHover,
            19 => FlightMode.QLoiter,
            20 => FlightMode.QLand,
            21 => FlightMode.QRtl,
            22 => FlightMode.QAutotune,
            23 => FlightMode.QAcro,
            24 => FlightMode.Thermal,

            _ => null
        };
    }

    /// <summary>
    /// Get display name for flight mode.
    /// </summary>
    public static string GetDisplayName(FlightMode mode)
    {
        return mode switch
        {
            // Fixed-wing
            FlightMode.Manual => "MANUAL",
            FlightMode.Circle => "CIRCLE",
            FlightMode.Stabilize => "STABILIZE",
            FlightMode.Training => "TRAINING",
            FlightMode.Acro => "ACRO",
            FlightMode.Fbwa => "FBW-A",
            FlightMode.Fbwb => "FBW-B",
            FlightMode.Cruise => "CRUISE",
            FlightMode.Autotune => "AUTOTUNE",
            FlightMode.Auto => "AUTO",
            FlightMode.Rtl => "RTL",
            FlightMode.Loiter => "LOITER",
            FlightMode.Takeoff => "TAKEOFF",
            FlightMode.AvoidAdsb => "AVOID ADSB",
            FlightMode.Guided => "GUIDED",
            FlightMode.Initialising => "INIT",

            // VTOL
            FlightMode.QStabilize => "QSTABILIZE",
            FlightMode.QHover => "QHOVER",
            FlightMode.QLoiter => "QLOITER",
            FlightMode.QLand => "QLAND",
            FlightMode.QRtl => "QRTL",
            FlightMode.QAutotune => "QAUTOTUNE",
            FlightMode.QAcro => "QACRO",
            FlightMode.Thermal => "THERMAL",

            _ => mode.ToString().ToUpper()
        };
    }

    /// <summary>
    /// Check if mode is a VTOL/QuadPlane mode.
    /// </summary>
    public static bool IsVtolMode(FlightMode mode)
    {
        return mode switch
        {
            FlightMode.QStabilize => true,
            FlightMode.QHover => true,
            FlightMode.QLoiter => true,
            FlightMode.QLand => true,
            FlightMode.QRtl => true,
            FlightMode.QAutotune => true,
            FlightMode.QAcro => true,
            _ => false
        };
    }
}
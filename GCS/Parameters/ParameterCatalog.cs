using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace GCS.Parameters;

/// <summary>
/// One curated parameter. <see cref="Names"/> lists the primary name first and
/// any firmware-rename aliases after it; requests are sent for all names and
/// whichever the vehicle answers becomes the one we write back to.
/// </summary>
public sealed record ParameterDef(string[] Names, string Group, string Label, string Description)
{
    public string Units { get; init; } = "";
    public int Decimals { get; init; } = 2;
    public double? Min { get; init; }
    public double? Max { get; init; }

    public string Name => Names[0];

    public string RangeText =>
        Min.HasValue && Max.HasValue
            ? $"{Fmt(Min.Value)} – {Fmt(Max.Value)}"
            : "";

    /// <summary>Enumerated value choices (rendered as a dropdown), or null for a numeric field.</summary>
    public IReadOnlyList<ParamOption>? Options => ParameterOptions.For(Name);

    /// <summary>True when this parameter is a selection (enum) rather than a free number.</summary>
    public bool HasOptions => Options is { Count: > 0 };

    /// <summary>Bit definitions (rendered as a checkbox list) for a bitmask parameter, else null.</summary>
    public IReadOnlyList<ParamBit>? Bits => ParameterBitmasks.For(Name);

    /// <summary>True when this parameter is a bitmask (multiple selectable flags).</summary>
    public bool HasBits => Bits is { Count: > 0 };

    public bool Matches(string name) =>
        Names.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));

    private static string Fmt(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);
}

/// <summary>One selectable value for an enumerated parameter.</summary>
public sealed record ParamOption(double Value, string Label)
{
    // ToString drives the ComboBox selection-box display (the app's global
    // ComboBox template doesn't honor DisplayMemberPath for the selected item).
    public override string ToString() => Label;
}

/// <summary>One flag of a bitmask parameter. <see cref="Mask"/> is the bit value (1, 2, 4, …).</summary>
public sealed record ParamBit(int Mask, string Label);

/// <summary>
/// The parameter list shown on the Parameters tab, with ArduPilot Plane /
/// QuadPlane names and value ranges. Edit this list to add/remove parameters.
/// </summary>
public static class ParameterCatalog
{
    private const string Airspeed = "Airspeed & Baro";
    private const string Ahrs = "AHRS & Altitude";
    private const string Battery = "Battery";
    private const string System = "System & Failsafe";
    private const string Nav = "Navigation (L1)";
    private const string Tuning = "Pitch / Roll Tuning";
    private const string Tecs = "TECS";
    private const string Takeoff = "Takeoff";
    private const string RtlWp = "RTL & Waypoints";
    private const string Throttle = "Throttle";
    private const string Quad = "QuadPlane";
    private const string QuadPid = "QuadPlane PIDs";

    // ── Copter groups ────────────────────────────────────────────────
    private const string CopterFrame = "Copter frame";
    private const string CopterFs = "Copter failsafe";
    private const string CopterFlight = "Copter flight";
    private const string CopterPid = "Copter PIDs";

    /// <summary>
    /// Groups that only exist on one vehicle family. A copter has no airspeed
    /// sensor and no Q-modes; a plane has none of the copter groups. Showing the
    /// wrong set is not dangerous — the values simply never load — but it buries
    /// the parameters that do apply.
    /// </summary>
    private static readonly string[] PlaneOnlyGroups =
        { Airspeed, Takeoff, Quad, QuadPid, Nav, Tecs };

    private static readonly string[] CopterOnlyGroups =
        { CopterFrame, CopterFs, CopterFlight, CopterPid };

    // ── PX4 groups ───────────────────────────────────────────────────
    private const string Px4System = "PX4 system";
    private const string Px4Fs = "PX4 failsafe";
    private const string Px4Flight = "PX4 flight";
    private const string Px4Pid = "PX4 rate control";

    private static readonly string[] Px4Groups =
        { Px4System, Px4Fs, Px4Flight, Px4Pid };

    /// <summary>Whether a group applies to this vehicle. Unknown shows everything.</summary>
    public static bool AppliesTo(string group, GCS.Core.Mavlink.VehicleKind kind) => kind switch
    {
        GCS.Core.Mavlink.VehicleKind.Copter => !PlaneOnlyGroups.Contains(group),
        GCS.Core.Mavlink.VehicleKind.Plane => !CopterOnlyGroups.Contains(group),
        _ => true,
    };

    /// <summary>The catalogue filtered to one vehicle family.</summary>
    public static IReadOnlyList<ParameterDef> For(GCS.Core.Mavlink.VehicleKind kind) =>
        All.Where(p => AppliesTo(p.Group, kind)).ToList();

    /// <summary>
    /// The catalogue for a firmware and airframe.
    ///
    /// PX4 shares no parameter names with ArduPilot, so the two sets are disjoint
    /// rather than filtered — showing ArduPilot names to a PX4 vehicle leaves the
    /// whole screen blank, which reads as a broken connection.
    /// </summary>
    public static IReadOnlyList<ParameterDef> For(
        GCS.Core.Mavlink.AutopilotKind autopilot, GCS.Core.Mavlink.VehicleKind kind)
    {
        if (autopilot == GCS.Core.Mavlink.AutopilotKind.Px4)
            return All.Where(p => Px4Groups.Contains(p.Group)).ToList();

        return All.Where(p => !Px4Groups.Contains(p.Group) && AppliesTo(p.Group, kind)).ToList();
    }

    public static readonly IReadOnlyList<ParameterDef> All = new List<ParameterDef>
    {
        // ── Airspeed & Baro ──────────────────────────────────────────
        new(new[]{ "AIRSPEED_CRUISE", "TRIM_ARSPD_CM" }, Airspeed, "Cruise airspeed", "Target cruise airspeed.") { Units="m/s", Decimals=1 },
        new(new[]{ "AIRSPEED_MAX", "ARSPD_FBW_MAX" }, Airspeed, "Max airspeed", "Maximum airspeed the autopilot will command.") { Units="m/s", Decimals=1, Min=5, Max=100 },
        new(new[]{ "AIRSPEED_MIN", "ARSPD_FBW_MIN" }, Airspeed, "Min airspeed", "Minimum (stall-safe) airspeed.") { Units="m/s", Decimals=1, Min=5, Max=100 },
        new(new[]{ "AIRSPEED_STALL" }, Airspeed, "Stall airspeed", "Stall speed used for scaling protections.") { Units="m/s", Decimals=1, Min=5, Max=75 },
        new(new[]{ "ARSPD_TUBE_ORDR" }, Airspeed, "Pitot tube order", "Pitot tube pin order.") { Decimals=0 },
        new(new[]{ "ARSPD_AUTOCAL" }, Airspeed, "Auto-calibrate", "Continuously calibrate the airspeed ratio.") { Decimals=0 },
        new(new[]{ "ARSPD_OPTIONS" }, Airspeed, "Airspeed options", "Airspeed options bitmask.") { Decimals=0 },
        new(new[]{ "ARSPD_PSI_RANGE" }, Airspeed, "Sensor PSI range", "Pressure sensor PSI range.") { Decimals=2 },
        new(new[]{ "ARSPD_RATIO" }, Airspeed, "Airspeed ratio", "Airspeed calibration ratio.") { Decimals=3 },
        new(new[]{ "ARSPD_SKIP_CAL" }, Airspeed, "Skip cal", "Skip airspeed offset calibration at boot.") { Decimals=0 },
        new(new[]{ "ARSPD_TYPE" }, Airspeed, "Sensor type", "Airspeed sensor type.") { Decimals=0 },
        new(new[]{ "ARSPD_USE" }, Airspeed, "Use airspeed", "0=disabled, 1=use, 2=use w/o throttle.") { Decimals=0 },
        new(new[]{ "ARSPD_WIND_GATE" }, Airspeed, "Wind gate", "Wind estimate consistency gate.") { Decimals=1, Min=0, Max=10 },
        new(new[]{ "ARSPD_WIND_MAX" }, Airspeed, "Wind max", "Max wind before airspeed is rejected.") { Units="m/s", Decimals=1 },
        new(new[]{ "ARSPD_WIND_WARN" }, Airspeed, "Wind warn", "Wind speed warning threshold.") { Units="m/s", Decimals=1 },
        new(new[]{ "ARSPD_OFFSET" }, Airspeed, "Airspeed offset", "Airspeed sensor zero offset.") { Decimals=2 },
        new(new[]{ "BARO_FLTR_RNG" }, Airspeed, "Baro filter range", "Range of allowed baro sample change.") { Units="%", Decimals=0, Min=0, Max=100 },
        new(new[]{ "BARO_PRIMARY" }, Airspeed, "Primary baro", "Which barometer is primary.") { Decimals=0 },
        new(new[]{ "BARO_PROBE_EXT" }, Airspeed, "External baro probe", "External I2C baro probe bitmask.") { Decimals=0 },

        // ── AHRS & Altitude ──────────────────────────────────────────
        new(new[]{ "AHRS_TRIM_X" }, Ahrs, "Board roll trim", "Board mounting roll trim.") { Units="rad", Decimals=4, Min=-0.1745, Max=0.1745 },
        new(new[]{ "AHRS_TRIM_Y" }, Ahrs, "Board pitch trim", "Board mounting pitch trim.") { Units="rad", Decimals=4, Min=-0.1745, Max=0.1745 },
        new(new[]{ "AHRS_TRIM_Z" }, Ahrs, "Board yaw trim", "Board mounting yaw trim.") { Units="rad", Decimals=4, Min=-0.1745, Max=0.1745 },
        new(new[]{ "AHRS_COMP_BETA" }, Ahrs, "Complementary beta", "AHRS complementary filter beta.") { Decimals=3 },
        new(new[]{ "ALT_SLOPE_MAXHGT" }, Ahrs, "Alt slope max hgt", "Max height change to trigger alt slope.") { Units="m", Decimals=0 },
        new(new[]{ "ALT_SLOPE_MIN" }, Ahrs, "Alt slope min", "Minimum altitude slope distance.") { Units="m", Decimals=0, Min=0, Max=1000 },

        // ── Battery ──────────────────────────────────────────────────
        new(new[]{ "BATT_MONITOR" }, Battery, "Battery monitor", "Monitor type (0=off, 4=volt+current).") { Decimals=0 },
        new(new[]{ "BATT_CAPACITY" }, Battery, "Capacity", "Pack capacity.") { Units="mAh", Decimals=0 },
        new(new[]{ "BATT_LOW_VOLT" }, Battery, "Low voltage", "Low-battery failsafe voltage.") { Units="V", Decimals=1 },
        new(new[]{ "BATT_CRT_VOLT" }, Battery, "Critical voltage", "Critical failsafe voltage.") { Units="V", Decimals=1 },
        new(new[]{ "BATT_LOW_MAH" }, Battery, "Low mAh", "Consumed-mAh low failsafe.") { Units="mAh", Decimals=0 },
        new(new[]{ "BATT_CRT_MAH" }, Battery, "Critical mAh", "Consumed-mAh critical failsafe.") { Units="mAh", Decimals=0 },
        new(new[]{ "BATT_LOW_TIMER" }, Battery, "Low timer", "Time below threshold before failsafe.") { Units="s", Decimals=0, Min=0, Max=120 },
        new(new[]{ "BATT_FS_LOW_ACT" }, Battery, "Low FS action", "Action on low-battery failsafe.") { Decimals=0 },
        new(new[]{ "BATT_FS_CRT_ACT" }, Battery, "Critical FS action", "Action on critical-battery failsafe.") { Decimals=0 },
        new(new[]{ "BATT_ARM_VOLT" }, Battery, "Arming voltage", "Minimum voltage required to arm.") { Units="V", Decimals=1 },
        new(new[]{ "BATT_AMP_PERVLT" }, Battery, "Amps per volt", "Current sensor amps per volt.") { Decimals=2 },

        // ── System & Failsafe ────────────────────────────────────────
        new(new[]{ "BRD_SAFETYOPTION" }, System, "Safety options", "Safety switch options bitmask.") { Decimals=0 },
        new(new[]{ "EFI_TYPE" }, System, "EFI type", "Electronic fuel injection type.") { Decimals=0 },
        new(new[]{ "FLIGHT_OPTIONS" }, System, "Flight options", "Flight options bitmask.") { Decimals=0 },
        new(new[]{ "FLTMODE_CH" }, System, "Mode channel", "RC channel used to select flight modes.") { Decimals=0 },
        new(new[]{ "FS_GCS_ENABLE" }, System, "GCS failsafe", "Enable GCS (telemetry) failsafe.") { Decimals=0 },
        new(new[]{ "FS_LONG_ACTN" }, System, "Long FS action", "Action on long failsafe.") { Decimals=0 },
        new(new[]{ "FS_LONG_TIMEOUT" }, System, "Long FS timeout", "Time before long failsafe triggers.") { Units="s", Decimals=0, Min=1, Max=300 },
        new(new[]{ "FS_SHORT_ACTN" }, System, "Short FS action", "Action on short failsafe.") { Decimals=0 },
        new(new[]{ "GEN_TYPE" }, System, "Generator type", "Onboard generator type.") { Decimals=0 },
        new(new[]{ "ICE_ENABLE" }, System, "ICE enable", "Internal combustion engine support.") { Decimals=0 },
        new(new[]{ "INITIAL_MODE" }, System, "Initial mode", "Flight mode entered at boot.") { Decimals=0 },
        new(new[]{ "INS_GYRO_FILTER" }, System, "Gyro filter", "Gyro low-pass filter cutoff.") { Units="Hz", Decimals=0, Min=0, Max=256 },
        new(new[]{ "INS_ACCEL_FILTER" }, System, "Accel filter", "Accelerometer low-pass filter cutoff.") { Units="Hz", Decimals=0, Min=0, Max=256 },
        new(new[]{ "DID_ENABLE" }, System, "Remote ID", "Remote ID (DroneID) enable.") { Decimals=0 },
        new(new[]{ "KFF_RDDRMIX" }, System, "Rudder mix", "Roll-to-rudder feed-forward mix.") { Decimals=2, Min=0, Max=1 },
        new(new[]{ "KFF_THR2PTCH" }, System, "Throttle→pitch FF", "Throttle-to-pitch feed-forward.") { Decimals=2, Min=-5, Max=5 },
        new(new[]{ "LEVEL_ROLL_LIMIT" }, System, "Level roll limit", "Roll limit while wings-level (takeoff/land).") { Units="deg", Decimals=0, Min=0, Max=45 },
        new(new[]{ "LGR_ENABLE" }, System, "Landing gear", "Retractable landing gear enable.") { Decimals=0 },
        new(new[]{ "MIS_OPTIONS" }, System, "Mission options", "Mission options bitmask.") { Decimals=0 },
        new(new[]{ "MIS_RESTART" }, System, "Mission restart", "Restart mission from first item on mode entry.") { Decimals=0 },
        new(new[]{ "PUP_ENABLE" }, System, "Power-up checks", "Power-up arming checks.") { Decimals=0 },

        // ── Navigation (L1) ──────────────────────────────────────────
        new(new[]{ "NAVL1_PERIOD" }, Nav, "L1 period", "Navigation controller period (lower = tighter).") { Units="s", Decimals=1, Min=1, Max=60 },
        new(new[]{ "NAVL1_DAMPING" }, Nav, "L1 damping", "Navigation controller damping.") { Decimals=2, Min=0.6, Max=1 },
        new(new[]{ "NAVL1_LIM_BANK" }, Nav, "L1 bank limit", "Navigation bank angle limit.") { Units="deg", Decimals=0, Min=0, Max=89 },
        new(new[]{ "NAVL1_XTRACK_I" }, Nav, "L1 cross-track I", "Cross-track integrator gain.") { Decimals=3, Min=0, Max=0.1 },

        // ── Pitch / Roll Tuning ──────────────────────────────────────
        new(new[]{ "PTCH2SRV_RLL" }, Tuning, "Pitch-roll comp", "Pitch compensation for bank angle.") { Decimals=2, Min=0.7, Max=1.5 },
        new(new[]{ "PTCH2SRV_RMAX_DN" }, Tuning, "Max pitch-down rate", "Maximum pitch-down rate.") { Units="deg/s", Decimals=0, Min=0, Max=100 },
        new(new[]{ "PTCH2SRV_RMAX_UP" }, Tuning, "Max pitch-up rate", "Maximum pitch-up rate.") { Units="deg/s", Decimals=0, Min=0, Max=100 },
        new(new[]{ "PTCH2SRV_TCONST" }, Tuning, "Pitch time const", "Pitch controller time constant.") { Units="s", Decimals=2, Min=0.4, Max=1 },
        new(new[]{ "PTCH_LIM_MAX_DEG" }, Tuning, "Pitch up limit", "Maximum commanded nose-up pitch.") { Units="deg", Decimals=0, Min=0, Max=90 },
        new(new[]{ "PTCH_LIM_MIN_DEG" }, Tuning, "Pitch down limit", "Maximum commanded nose-down pitch.") { Units="deg", Decimals=0, Min=-90, Max=0 },
        new(new[]{ "PTCH_TRIM_DEG" }, Tuning, "Pitch trim", "Level-flight pitch trim.") { Units="deg", Decimals=1, Min=-45, Max=45 },
        new(new[]{ "RLL2SRV_RMAX" }, Tuning, "Max roll rate", "Maximum roll rate.") { Units="deg/s", Decimals=0, Min=0, Max=180 },
        new(new[]{ "RLL2SRV_TCONST" }, Tuning, "Roll time const", "Roll controller time constant.") { Units="s", Decimals=2, Min=0.4, Max=1 },
        new(new[]{ "ROLL_LIMIT_DEG" }, Tuning, "Roll limit", "Maximum bank angle.") { Units="deg", Decimals=0, Min=0, Max=90 },

        // ── TECS ─────────────────────────────────────────────────────
        new(new[]{ "TECS_TIME_CONST" }, Tecs, "Time constant", "Energy controller response time.") { Units="s", Decimals=1, Min=3, Max=10 },
        new(new[]{ "TECS_SPDWEIGHT" }, Tecs, "Speed weight", "Balance of pitch to speed vs height (0-2).") { Decimals=1, Min=0, Max=2 },
        new(new[]{ "TECS_PTCH_DAMP" }, Tecs, "Pitch damping", "TECS pitch damping.") { Decimals=2, Min=0.1, Max=1 },
        new(new[]{ "TECS_RLL2THR" }, Tecs, "Roll→throttle FF", "Throttle added for bank angle.") { Decimals=0, Min=5, Max=30 },
        new(new[]{ "TECS_CLMB_MAX" }, Tecs, "Max climb rate", "Max climb rate at full throttle.") { Units="m/s", Decimals=1, Min=0.1, Max=20 },
        new(new[]{ "TECS_SINK_MAX" }, Tecs, "Max sink rate", "Max commanded sink rate.") { Units="m/s", Decimals=1, Min=0, Max=20 },
        new(new[]{ "TECS_SINK_MIN" }, Tecs, "Min sink rate", "Min sink rate at idle throttle.") { Units="m/s", Decimals=1, Min=0.1, Max=10 },

        // ── Takeoff ──────────────────────────────────────────────────
        new(new[]{ "TKOFF_ACCEL_CNT" }, Takeoff, "Accel event count", "Accel events to trigger launch.") { Decimals=0, Min=1, Max=10 },
        new(new[]{ "TKOFF_ALT" }, Takeoff, "Takeoff altitude", "Target altitude for auto takeoff.") { Units="m", Decimals=0, Min=0, Max=200 },
        new(new[]{ "TKOFF_DIST" }, Takeoff, "Takeoff distance", "Distance to fly during takeoff.") { Units="m", Decimals=0, Min=0, Max=500 },
        new(new[]{ "TKOFF_LVL_ALT" }, Takeoff, "Level-off altitude", "Altitude to hold wings level to.") { Units="m", Decimals=0, Min=0, Max=50 },
        new(new[]{ "TKOFF_LVL_PITCH" }, Takeoff, "Level pitch", "Pitch target during initial climb.") { Units="deg", Decimals=0, Min=0, Max=30 },
        new(new[]{ "TKOFF_OPTIONS" }, Takeoff, "Takeoff options", "Takeoff options bitmask.") { Decimals=0 },
        new(new[]{ "TKOFF_THR_DELAY" }, Takeoff, "Throttle delay", "Delay before throttle up (0.1 s units).") { Decimals=0, Min=0, Max=127 },
        new(new[]{ "TKOFF_THR_IDLE" }, Takeoff, "Idle throttle", "Idle throttle before launch.") { Units="%", Decimals=0, Min=0, Max=100 },
        new(new[]{ "TKOFF_THR_MAX" }, Takeoff, "Max throttle", "Max throttle during takeoff.") { Units="%", Decimals=0, Min=0, Max=100 },
        new(new[]{ "TKOFF_THR_MAX_T" }, Takeoff, "Max throttle time", "Time to hold max throttle.") { Units="s", Decimals=0, Min=0, Max=10 },
        new(new[]{ "TKOFF_THR_MIN" }, Takeoff, "Min throttle", "Min throttle during takeoff.") { Units="%", Decimals=0, Min=0, Max=100 },
        new(new[]{ "TKOFF_THR_MINACC" }, Takeoff, "Min launch accel", "Acceleration to detect hand/bungee launch.") { Units="m/s²", Decimals=0, Min=0, Max=30 },
        new(new[]{ "TKOFF_THR_MINSPD" }, Takeoff, "Min launch speed", "Speed to detect launch.") { Units="m/s", Decimals=0, Min=0, Max=30 },
        new(new[]{ "TKOFF_THR_SLEW" }, Takeoff, "Throttle slew", "Throttle slew during takeoff (-1=default).") { Units="%/s", Decimals=0, Min=-1, Max=500 },
        new(new[]{ "TKOFF_TIMEOUT" }, Takeoff, "Takeoff timeout", "Abort takeoff if not airborne in time.") { Units="s", Decimals=0, Min=0, Max=120 },

        // ── RTL & Waypoints ──────────────────────────────────────────
        new(new[]{ "RTL_ALTITUDE" }, RtlWp, "RTL altitude", "Return-to-launch altitude.") { Units="m", Decimals=0 },
        new(new[]{ "RTL_AUTOLAND" }, RtlWp, "RTL autoland", "Auto-land / DO_LAND_START behaviour on RTL.") { Decimals=0 },
        new(new[]{ "RTL_CLIMB_MIN" }, RtlWp, "RTL min climb", "Minimum climb before turning home.") { Units="m", Decimals=0, Min=0, Max=30 },
        new(new[]{ "RTL_RADIUS" }, RtlWp, "RTL radius", "Loiter radius at home (sign = direction).") { Units="m", Decimals=0, Min=-32767, Max=32767 },
        new(new[]{ "WP_RADIUS" }, RtlWp, "Waypoint radius", "Distance at which a waypoint is reached.") { Units="m", Decimals=0, Min=1, Max=32767 },
        new(new[]{ "WP_MAX_RADIUS" }, RtlWp, "WP max radius", "Max acceptance radius override.") { Units="m", Decimals=0, Min=0, Max=32767 },
        new(new[]{ "WP_LOITER_RAD" }, RtlWp, "Loiter radius", "Default loiter radius (sign = direction).") { Units="m", Decimals=0, Min=-32767, Max=32767 },

        // ── Throttle ─────────────────────────────────────────────────
        new(new[]{ "THR_MIN" }, Throttle, "Throttle min", "Minimum throttle.") { Units="%", Decimals=0, Min=-100, Max=100 },
        new(new[]{ "THR_MAX" }, Throttle, "Throttle max", "Maximum throttle.") { Units="%", Decimals=0, Min=0, Max=100 },
        new(new[]{ "TRIM_THROTTLE" }, Throttle, "Cruise throttle", "Base throttle at cruise airspeed.") { Units="%", Decimals=0, Min=0, Max=100 },
        new(new[]{ "THR_SLEWRATE" }, Throttle, "Throttle slew", "Max throttle change per second (0=off).") { Units="%/s", Decimals=0, Min=0, Max=500 },
        new(new[]{ "THR_FAILSAFE" }, Throttle, "Throttle failsafe", "Enable throttle-based RC failsafe.") { Decimals=0 },
        new(new[]{ "THR_FS_VALUE" }, Throttle, "Throttle FS PWM", "PWM below which failsafe triggers.") { Units="PWM", Decimals=0, Min=925, Max=2200 },

        // ── QuadPlane ────────────────────────────────────────────────
        new(new[]{ "Q_ENABLE" }, Quad, "QuadPlane enable", "Enable VTOL/QuadPlane (needs reboot).") { Decimals=0 },
        new(new[]{ "Q_A_ANGLE_MAX" }, Quad, "Max lean angle", "Max lean angle in VTOL modes.") { Units="cdeg", Decimals=0 },
        new(new[]{ "Q_ASSIST_SPEED" }, Quad, "Assist speed", "Airspeed below which VTOL assists.") { Units="m/s", Decimals=1, Min=0, Max=100 },
        new(new[]{ "Q_ASSIST_ANGLE" }, Quad, "Assist angle", "Attitude error angle that triggers assist.") { Units="deg", Decimals=0, Min=0, Max=90 },
        new(new[]{ "Q_A_ACCEL_P_MAX" }, Quad, "Max pitch accel", "Max angular pitch acceleration.") { Units="cdeg/s²", Decimals=0 },
        new(new[]{ "Q_A_ACCEL_R_MAX" }, Quad, "Max roll accel", "Max angular roll acceleration.") { Units="cdeg/s²", Decimals=0 },
        new(new[]{ "Q_A_ACCEL_Y_MAX" }, Quad, "Max yaw accel", "Max angular yaw acceleration.") { Units="cdeg/s²", Decimals=0 },
        new(new[]{ "Q_A_RATE_P_MAX" }, Quad, "Max pitch rate", "Max pitch rate in VTOL.") { Units="deg/s", Decimals=0 },
        new(new[]{ "Q_A_RATE_R_MAX" }, Quad, "Max roll rate", "Max roll rate in VTOL.") { Units="deg/s", Decimals=0 },
        new(new[]{ "Q_A_RATE_Y_MAX" }, Quad, "Max yaw rate", "Max yaw rate in VTOL.") { Units="deg/s", Decimals=0 },
        new(new[]{ "Q_A_THR_MIX_MAX" }, Quad, "Throttle mix max", "Max attitude-vs-throttle priority.") { Decimals=2, Min=0.5, Max=0.9 },
        new(new[]{ "Q_FRAME_CLASS" }, Quad, "Frame class", "VTOL frame class.") { Decimals=0 },
        new(new[]{ "Q_FRAME_TYPE" }, Quad, "Frame type", "VTOL frame type/layout.") { Decimals=0 },
        new(new[]{ "Q_LAND_FINAL_ALT" }, Quad, "Land final alt", "Altitude to begin final landing stage.") { Units="m", Decimals=1, Min=0.5, Max=50 },
        new(new[]{ "Q_LAND_FINAL_SPD" }, Quad, "Land final speed", "Descent speed for final landing.") { Units="m/s", Decimals=2, Min=0.3, Max=2 },
        new(new[]{ "Q_LOIT_ANG_MAX" }, Quad, "Loiter angle max", "Max lean angle in VTOL loiter.") { Units="deg", Decimals=0, Min=0, Max=45 },
        new(new[]{ "Q_LOIT_BRK_ACC_M" }, Quad, "Loiter brake accel", "Loiter braking acceleration.") { Units="m/s²", Decimals=2, Min=0.25, Max=2.5 },
        new(new[]{ "Q_LOIT_BRK_JRK_M" }, Quad, "Loiter brake jerk", "Loiter braking jerk limit.") { Units="m/s³", Decimals=0, Min=5, Max=50 },
        new(new[]{ "Q_LOIT_SPEED_MS" }, Quad, "Loiter speed", "Max horizontal loiter speed.") { Units="m/s", Decimals=2, Min=0.2, Max=35 },
        new(new[]{ "Q_M_BAT_IDX" }, Quad, "Motor batt index", "Battery monitor used for motor comp.") { Decimals=0 },
        new(new[]{ "Q_M_BAT_VOLT_MAX" }, Quad, "Motor batt V max", "Voltage for full thrust scaling.") { Units="V", Decimals=1, Min=6, Max=53 },
        new(new[]{ "Q_M_BAT_VOLT_MIN" }, Quad, "Motor batt V min", "Voltage for min thrust scaling.") { Units="V", Decimals=1, Min=6, Max=42 },
        new(new[]{ "Q_M_THST_EXPO" }, Quad, "Thrust expo", "Motor thrust curve expo.") { Decimals=2, Min=-1, Max=1 },
        new(new[]{ "Q_M_THST_HOVER" }, Quad, "Hover thrust", "Hover throttle (learned).") { Decimals=4, Min=0.125, Max=0.6875 },
        new(new[]{ "Q_M_YAW_HEADROOM" }, Quad, "Yaw headroom", "PWM reserved for yaw control.") { Decimals=0, Min=0, Max=500 },
        new(new[]{ "Q_P_JERK_NE" }, Quad, "Horizontal jerk", "Horizontal jerk limit.") { Units="m/s³", Decimals=0, Min=1, Max=50 },
        new(new[]{ "Q_P_NE_POS_P" }, Quad, "Position P", "Horizontal position P gain.") { Decimals=2, Min=0.5, Max=4 },
        new(new[]{ "Q_P_NE_VEL_D" }, Quad, "Velocity D", "Horizontal velocity D gain.") { Decimals=3, Min=0, Max=1 },
        new(new[]{ "Q_P_NE_VEL_I" }, Quad, "Velocity I", "Horizontal velocity I gain.") { Decimals=2, Min=0.1, Max=10 },
        new(new[]{ "Q_P_NE_VEL_P" }, Quad, "Velocity P", "Horizontal velocity P gain.") { Decimals=2, Min=0.1, Max=10 },
        new(new[]{ "Q_RTL_ALT" }, Quad, "Q RTL altitude", "QuadPlane RTL altitude.") { Units="m", Decimals=0, Min=1, Max=200 },
        new(new[]{ "Q_THROTTLE_EXPO" }, Quad, "Throttle expo", "Manual throttle expo in VTOL.") { Decimals=2, Min=0, Max=1 },
        new(new[]{ "Q_TRANS_DECEL" }, Quad, "Transition decel", "Deceleration for FW→VTOL transition.") { Units="m/s²", Decimals=1, Min=0.2, Max=5 },
        new(new[]{ "Q_TRAN_PIT_MAX" }, Quad, "Transition pitch max", "Max pitch during transition.") { Units="deg", Decimals=0, Min=0, Max=30 },
        new(new[]{ "Q_PILOT_SPD_UP" }, Quad, "Pilot climb speed", "Max pilot-commanded climb rate.") { Units="m/s", Decimals=1, Min=0.5, Max=5 },
        new(new[]{ "Q_VFWD_ALT" }, Quad, "Fwd throttle alt", "Altitude below which fwd throttle disabled.") { Units="m", Decimals=0, Min=0, Max=10 },
        new(new[]{ "Q_VFWD_GAIN" }, Quad, "Fwd throttle gain", "Forward throttle gain in VTOL.") { Decimals=2, Min=0, Max=0.5 },
        new(new[]{ "Q_WVANE_ENABLE" }, Quad, "Weathervane enable", "Weathervaning enable.") { Decimals=0 },
        new(new[]{ "Q_WVANE_GAIN" }, Quad, "Weathervane gain", "Weathervaning gain.") { Decimals=1, Min=0.5, Max=4 },
        new(new[]{ "Q_WVANE_ANG_MIN" }, Quad, "Weathervane min ang", "Min lean angle for weathervaning.") { Units="deg", Decimals=0, Min=0, Max=10 },
        new(new[]{ "Q_WVANE_TAKEOFF" }, Quad, "Weathervane on tkoff", "Weathervane during takeoff.") { Decimals=0 },
        new(new[]{ "Q_TRANSITION_MS" }, Quad, "Transition time", "Time to transition VTOL→FW.") { Units="ms", Decimals=0, Min=500, Max=30000 },
        new(new[]{ "Q_BACKTRANS_MS" }, Quad, "Back-transition time", "FW→VTOL back-transition time.") { Units="ms", Decimals=0, Min=0, Max=10000 },
        new(new[]{ "Q_BCK_PIT_LIM" }, Quad, "Back-trans pitch lim", "Pitch limit during back-transition.") { Units="deg", Decimals=0, Min=0, Max=15 },

        // ── QuadPlane PIDs (Q_A_RAT_*) ───────────────────────────────
        new(new[]{ "Q_A_RAT_PIT_P" }, QuadPid, "Pitch rate P", "VTOL pitch rate P.") { Decimals=3, Min=0.01, Max=0.5 },
        new(new[]{ "Q_A_RAT_PIT_I" }, QuadPid, "Pitch rate I", "VTOL pitch rate I.") { Decimals=3, Min=0.01, Max=2 },
        new(new[]{ "Q_A_RAT_PIT_D" }, QuadPid, "Pitch rate D", "VTOL pitch rate D.") { Decimals=4, Min=0, Max=0.05 },
        new(new[]{ "Q_A_RAT_PIT_FF" }, QuadPid, "Pitch rate FF", "VTOL pitch rate feed-forward.") { Decimals=3, Min=0, Max=0.5 },
        new(new[]{ "Q_A_RAT_PIT_IMAX" }, QuadPid, "Pitch I max", "VTOL pitch integrator limit.") { Decimals=2, Min=0, Max=1 },
        new(new[]{ "Q_A_RAT_PIT_FLTD" }, QuadPid, "Pitch D filter", "Pitch D-term filter.") { Units="Hz", Decimals=0, Min=5, Max=100 },
        new(new[]{ "Q_A_RAT_PIT_FLTE" }, QuadPid, "Pitch E filter", "Pitch error filter.") { Units="Hz", Decimals=0, Min=0, Max=100 },
        new(new[]{ "Q_A_RAT_PIT_FLTT" }, QuadPid, "Pitch T filter", "Pitch target filter.") { Units="Hz", Decimals=0, Min=5, Max=100 },
        new(new[]{ "Q_A_RAT_PIT_SMAX" }, QuadPid, "Pitch slew max", "Pitch slew-rate limit.") { Decimals=0, Min=0, Max=200 },

        new(new[]{ "Q_A_RAT_RLL_P" }, QuadPid, "Roll rate P", "VTOL roll rate P.") { Decimals=3, Min=0.01, Max=0.5 },
        new(new[]{ "Q_A_RAT_RLL_I" }, QuadPid, "Roll rate I", "VTOL roll rate I.") { Decimals=3, Min=0.01, Max=2 },
        new(new[]{ "Q_A_RAT_RLL_D" }, QuadPid, "Roll rate D", "VTOL roll rate D.") { Decimals=4, Min=0, Max=0.05 },
        new(new[]{ "Q_A_RAT_RLL_FF" }, QuadPid, "Roll rate FF", "VTOL roll rate feed-forward.") { Decimals=3, Min=0, Max=0.5 },
        new(new[]{ "Q_A_RAT_RLL_IMAX" }, QuadPid, "Roll I max", "VTOL roll integrator limit.") { Decimals=2, Min=0, Max=1 },
        new(new[]{ "Q_A_RAT_RLL_FLTD" }, QuadPid, "Roll D filter", "Roll D-term filter.") { Units="Hz", Decimals=0, Min=5, Max=100 },
        new(new[]{ "Q_A_RAT_RLL_FLTE" }, QuadPid, "Roll E filter", "Roll error filter.") { Units="Hz", Decimals=0, Min=0, Max=100 },
        new(new[]{ "Q_A_RAT_RLL_FLTT" }, QuadPid, "Roll T filter", "Roll target filter.") { Units="Hz", Decimals=0, Min=5, Max=100 },
        new(new[]{ "Q_A_RAT_RLL_SMAX" }, QuadPid, "Roll slew max", "Roll slew-rate limit.") { Decimals=0, Min=0, Max=200 },

        new(new[]{ "Q_A_RAT_YAW_P" }, QuadPid, "Yaw rate P", "VTOL yaw rate P.") { Decimals=3, Min=0.1, Max=2.5 },
        new(new[]{ "Q_A_RAT_YAW_I" }, QuadPid, "Yaw rate I", "VTOL yaw rate I.") { Decimals=3, Min=0.01, Max=1 },
        new(new[]{ "Q_A_RAT_YAW_D" }, QuadPid, "Yaw rate D", "VTOL yaw rate D.") { Decimals=4, Min=0, Max=0.02 },
        new(new[]{ "Q_A_RAT_YAW_FF" }, QuadPid, "Yaw rate FF", "VTOL yaw rate feed-forward.") { Decimals=3, Min=0, Max=0.5 },
        new(new[]{ "Q_A_RAT_YAW_IMAX" }, QuadPid, "Yaw I max", "VTOL yaw integrator limit.") { Decimals=2, Min=0, Max=1 },
        new(new[]{ "Q_A_RAT_YAW_FLTD" }, QuadPid, "Yaw D filter", "Yaw D-term filter.") { Units="Hz", Decimals=0, Min=5, Max=50 },
        new(new[]{ "Q_A_RAT_YAW_FLTE" }, QuadPid, "Yaw E filter", "Yaw error filter.") { Units="Hz", Decimals=0, Min=0, Max=20 },
        new(new[]{ "Q_A_RAT_YAW_FLTT" }, QuadPid, "Yaw T filter", "Yaw target filter.") { Units="Hz", Decimals=0, Min=1, Max=50 },
        new(new[]{ "Q_A_RAT_YAW_SMAX" }, QuadPid, "Yaw slew max", "Yaw slew-rate limit.") { Decimals=0, Min=0, Max=200 },

        // ══ ArduCopter ═══════════════════════════════════════════════
        // Only shown when the connected vehicle is a multirotor. Names are
        // ArduCopter's own — several overlap conceptually with the plane entries
        // above but are spelled differently (FS_THR_ENABLE vs THR_FAILSAFE).

        // ── Frame ────────────────────────────────────────────────────
        new(new[]{ "FRAME_CLASS" }, CopterFrame, "Frame class", "Airframe layout: quad, hexa, octa, and so on.") { Decimals=0, Min=0, Max=15 },
        new(new[]{ "FRAME_TYPE" }, CopterFrame, "Frame type", "Motor arrangement within the frame class (X, plus, V…).") { Decimals=0, Min=0, Max=18 },
        new(new[]{ "MOT_PWM_TYPE" }, CopterFrame, "Motor PWM type", "Output protocol to the ESCs (normal PWM, OneShot, DShot).") { Decimals=0, Min=0, Max=8 },
        new(new[]{ "MOT_SPIN_ARM" }, CopterFrame, "Spin when armed", "Motor output when armed and throttle is at minimum.") { Decimals=3, Min=0, Max=0.5 },
        new(new[]{ "MOT_SPIN_MIN" }, CopterFrame, "Minimum spin", "Lowest motor output used in flight.") { Decimals=3, Min=0, Max=0.5 },
        new(new[]{ "MOT_SPIN_MAX" }, CopterFrame, "Maximum spin", "Highest motor output used in flight.") { Decimals=3, Min=0.9, Max=1 },
        new(new[]{ "MOT_THST_HOVER" }, CopterFrame, "Hover thrust", "Learned throttle needed to hover.") { Decimals=3, Min=0.08, Max=0.8 },
        new(new[]{ "MOT_BAT_VOLT_MAX" }, CopterFrame, "Battery volt max", "Pack voltage for thrust scaling at full charge.") { Units="V", Decimals=1, Min=0, Max=60 },
        new(new[]{ "MOT_BAT_VOLT_MIN" }, CopterFrame, "Battery volt min", "Pack voltage for thrust scaling when flat.") { Units="V", Decimals=1, Min=0, Max=60 },

        // ── Failsafe ─────────────────────────────────────────────────
        new(new[]{ "FS_THR_ENABLE" }, CopterFs, "Throttle failsafe", "What to do when the RC link is lost.") { Decimals=0, Min=0, Max=6 },
        new(new[]{ "FS_THR_VALUE" }, CopterFs, "Throttle FS PWM", "Throttle PWM below which the RC link counts as lost.") { Units="PWM", Decimals=0, Min=910, Max=1100 },
        new(new[]{ "FS_GCS_ENABLE" }, CopterFs, "GCS failsafe", "What to do when the ground station link is lost.") { Decimals=0, Min=0, Max=7 },
        new(new[]{ "FS_EKF_ACTION" }, CopterFs, "EKF failsafe action", "Response to an unhealthy position estimate.") { Decimals=0, Min=1, Max=3 },
        new(new[]{ "FS_EKF_THRESH" }, CopterFs, "EKF failsafe threshold", "Variance above which the EKF counts as failed.") { Decimals=1, Min=0.6, Max=1 },
        new(new[]{ "FS_CRASH_CHECK" }, CopterFs, "Crash check", "Disarm automatically when a crash is detected.") { Decimals=0, Min=0, Max=1 },
        new(new[]{ "FS_VIBE_ENABLE" }, CopterFs, "Vibration failsafe", "Compensate when vibration corrupts the estimate.") { Decimals=0, Min=0, Max=1 },
        new(new[]{ "FS_OPTIONS" }, CopterFs, "Failsafe options", "Bitmask of extra failsafe behaviours.") { Decimals=0, Min=0, Max=255 },

        // ── Flight behaviour ─────────────────────────────────────────
        new(new[]{ "PILOT_SPEED_UP" }, CopterFlight, "Max climb rate", "Fastest climb the pilot can command.") { Units="cm/s", Decimals=0, Min=50, Max=2000 },
        new(new[]{ "PILOT_SPEED_DN" }, CopterFlight, "Max descent rate", "Fastest descent the pilot can command. 0 uses the climb rate.") { Units="cm/s", Decimals=0, Min=0, Max=2000 },
        new(new[]{ "PILOT_ACCEL_Z" }, CopterFlight, "Vertical acceleration", "Vertical acceleration limit.") { Units="cm/s/s", Decimals=0, Min=50, Max=500 },
        new(new[]{ "ANGLE_MAX" }, CopterFlight, "Max lean angle", "Largest lean angle the pilot can command.") { Units="cdeg", Decimals=0, Min=1000, Max=8000 },
        new(new[]{ "WPNAV_SPEED" }, CopterFlight, "Waypoint speed", "Horizontal speed between waypoints.") { Units="cm/s", Decimals=0, Min=20, Max=2000 },
        new(new[]{ "WPNAV_SPEED_UP" }, CopterFlight, "Waypoint climb speed", "Climb speed during auto missions.") { Units="cm/s", Decimals=0, Min=10, Max=1000 },
        new(new[]{ "WPNAV_SPEED_DN" }, CopterFlight, "Waypoint descent speed", "Descent speed during auto missions.") { Units="cm/s", Decimals=0, Min=10, Max=500 },
        new(new[]{ "WPNAV_RADIUS" }, CopterFlight, "Waypoint radius", "Distance at which a waypoint counts as reached.") { Units="cm", Decimals=0, Min=5, Max=1000 },
        new(new[]{ "RTL_ALT" }, CopterFlight, "RTL altitude", "Altitude climbed to before returning. 0 returns at the current height.") { Units="cm", Decimals=0, Min=0, Max=30000 },
        new(new[]{ "RTL_LOIT_TIME" }, CopterFlight, "RTL loiter time", "Pause above home before descending.") { Units="ms", Decimals=0, Min=0, Max=60000 },
        new(new[]{ "LAND_SPEED" }, CopterFlight, "Landing speed", "Descent rate for the final part of a landing.") { Units="cm/s", Decimals=0, Min=30, Max=200 },
        new(new[]{ "LAND_ALT_LOW" }, CopterFlight, "Landing slow-down height", "Height at which the slower landing speed begins.") { Units="cm", Decimals=0, Min=100, Max=10000 },

        // ── Attitude PIDs ────────────────────────────────────────────
        new(new[]{ "ATC_ANG_RLL_P" }, CopterPid, "Roll angle P", "Roll angle controller gain.") { Decimals=3, Min=3, Max=12 },
        new(new[]{ "ATC_ANG_PIT_P" }, CopterPid, "Pitch angle P", "Pitch angle controller gain.") { Decimals=3, Min=3, Max=12 },
        new(new[]{ "ATC_ANG_YAW_P" }, CopterPid, "Yaw angle P", "Yaw angle controller gain.") { Decimals=3, Min=3, Max=12 },
        new(new[]{ "ATC_RAT_RLL_P" }, CopterPid, "Roll rate P", "Roll rate controller P gain.") { Decimals=4, Min=0.01, Max=0.5 },
        new(new[]{ "ATC_RAT_RLL_I" }, CopterPid, "Roll rate I", "Roll rate controller I gain.") { Decimals=4, Min=0.01, Max=2 },
        new(new[]{ "ATC_RAT_RLL_D" }, CopterPid, "Roll rate D", "Roll rate controller D gain.") { Decimals=4, Min=0, Max=0.05 },
        new(new[]{ "ATC_RAT_PIT_P" }, CopterPid, "Pitch rate P", "Pitch rate controller P gain.") { Decimals=4, Min=0.01, Max=0.5 },
        new(new[]{ "ATC_RAT_PIT_I" }, CopterPid, "Pitch rate I", "Pitch rate controller I gain.") { Decimals=4, Min=0.01, Max=2 },
        new(new[]{ "ATC_RAT_PIT_D" }, CopterPid, "Pitch rate D", "Pitch rate controller D gain.") { Decimals=4, Min=0, Max=0.05 },
        new(new[]{ "ATC_RAT_YAW_P" }, CopterPid, "Yaw rate P", "Yaw rate controller P gain.") { Decimals=4, Min=0.1, Max=2.5 },
        new(new[]{ "ATC_RAT_YAW_I" }, CopterPid, "Yaw rate I", "Yaw rate controller I gain.") { Decimals=4, Min=0.01, Max=1 },
        new(new[]{ "ATC_RAT_YAW_D" }, CopterPid, "Yaw rate D", "Yaw rate controller D gain.") { Decimals=4, Min=0, Max=0.02 },
        new(new[]{ "ATC_THR_MIX_MAN" }, CopterPid, "Throttle mix manual", "Attitude-versus-throttle priority when flown manually.") { Decimals=2, Min=0.1, Max=2 },
        new(new[]{ "ATC_ACCEL_R_MAX" }, CopterPid, "Roll accel max", "Maximum roll acceleration.") { Units="cdeg/s/s", Decimals=0, Min=0, Max=180000 },
        new(new[]{ "ATC_ACCEL_P_MAX" }, CopterPid, "Pitch accel max", "Maximum pitch acceleration.") { Units="cdeg/s/s", Decimals=0, Min=0, Max=180000 },
        new(new[]{ "ATC_ACCEL_Y_MAX" }, CopterPid, "Yaw accel max", "Maximum yaw acceleration.") { Units="cdeg/s/s", Decimals=0, Min=0, Max=72000 },

        // ══ PX4 ══════════════════════════════════════════════════════
        // A disjoint set: PX4 shares no parameter names with ArduPilot. Shown only
        // when the heartbeat reports PX4 firmware.

        // ── System ───────────────────────────────────────────────────
        new(new[]{ "MAV_SYS_ID" }, Px4System, "System ID", "MAVLink system id of this vehicle.") { Decimals=0, Min=1, Max=250 },
        new(new[]{ "SYS_AUTOSTART" }, Px4System, "Airframe", "Airframe configuration id.") { Decimals=0, Min=0, Max=1000000 },
        new(new[]{ "COM_ARM_WO_GPS" }, Px4System, "Arm without GPS", "Allow arming with no position estimate.") { Decimals=0, Min=0, Max=1 },
        new(new[]{ "COM_DISARM_LAND" }, Px4System, "Disarm after landing", "Seconds after landing before auto-disarm.") { Units="s", Decimals=1, Min=0, Max=20 },
        new(new[]{ "COM_PREARM_MODE" }, Px4System, "Prearm mode", "When prearm checks are allowed to pass.") { Decimals=0, Min=0, Max=2 },
        new(new[]{ "CBRK_SUPPLY_CHK" }, Px4System, "Power check bypass", "Circuit breaker for the power supply check.") { Decimals=0, Min=0, Max=894281 },

        // ── Failsafe ─────────────────────────────────────────────────
        new(new[]{ "NAV_RCL_ACT" }, Px4Fs, "RC loss action", "What to do when the RC link is lost.") { Decimals=0, Min=0, Max=6 },
        new(new[]{ "NAV_DLL_ACT" }, Px4Fs, "Data link loss action", "What to do when the GCS link is lost.") { Decimals=0, Min=0, Max=6 },
        new(new[]{ "COM_RC_LOSS_T" }, Px4Fs, "RC loss timeout", "Seconds without RC before the failsafe triggers.") { Units="s", Decimals=1, Min=0, Max=35 },
        new(new[]{ "COM_DL_LOSS_T" }, Px4Fs, "Data link timeout", "Seconds without a GCS before the failsafe triggers.") { Units="s", Decimals=0, Min=0, Max=100 },
        new(new[]{ "BAT_LOW_THR" }, Px4Fs, "Low battery threshold", "Remaining fraction counted as low.") { Decimals=2, Min=0.05, Max=0.5 },
        new(new[]{ "BAT_CRIT_THR" }, Px4Fs, "Critical battery threshold", "Remaining fraction counted as critical.") { Decimals=2, Min=0.05, Max=0.5 },
        new(new[]{ "BAT_EMERGEN_THR" }, Px4Fs, "Emergency battery threshold", "Remaining fraction counted as an emergency.") { Decimals=2, Min=0.03, Max=0.5 },
        new(new[]{ "COM_LOW_BAT_ACT" }, Px4Fs, "Low battery action", "Response to a low battery.") { Decimals=0, Min=0, Max=3 },
        new(new[]{ "GF_ACTION" }, Px4Fs, "Geofence action", "Response to breaching the geofence.") { Decimals=0, Min=0, Max=5 },
        new(new[]{ "COM_POS_FS_EPH" }, Px4Fs, "Position failsafe radius", "Horizontal accuracy beyond which position is unusable.") { Units="m", Decimals=1, Min=0, Max=1000 },

        // ── Flight limits ────────────────────────────────────────────
        new(new[]{ "MPC_XY_VEL_MAX" }, Px4Flight, "Max horizontal speed", "Fastest horizontal speed in position control.") { Units="m/s", Decimals=1, Min=0, Max=20 },
        new(new[]{ "MPC_Z_VEL_MAX_UP" }, Px4Flight, "Max climb rate", "Fastest commanded climb.") { Units="m/s", Decimals=1, Min=0.5, Max=8 },
        new(new[]{ "MPC_Z_VEL_MAX_DN" }, Px4Flight, "Max descent rate", "Fastest commanded descent.") { Units="m/s", Decimals=1, Min=0.5, Max=4 },
        new(new[]{ "MPC_TILTMAX_AIR" }, Px4Flight, "Max tilt angle", "Largest lean angle in flight.") { Units="deg", Decimals=0, Min=20, Max=89 },
        new(new[]{ "MPC_THR_HOVER" }, Px4Flight, "Hover throttle", "Throttle needed to hover.") { Decimals=2, Min=0.1, Max=0.8 },
        new(new[]{ "MIS_TAKEOFF_ALT" }, Px4Flight, "Takeoff altitude", "Altitude climbed to on takeoff.") { Units="m", Decimals=1, Min=0, Max=80 },
        new(new[]{ "RTL_RETURN_ALT" }, Px4Flight, "Return altitude", "Altitude climbed to before returning home.") { Units="m", Decimals=1, Min=0, Max=150 },
        new(new[]{ "RTL_DESCEND_ALT" }, Px4Flight, "Return descend altitude", "Altitude descended to above home before landing.") { Units="m", Decimals=1, Min=2, Max=100 },
        new(new[]{ "MPC_LAND_SPEED" }, Px4Flight, "Landing speed", "Descent rate for the final landing phase.") { Units="m/s", Decimals=1, Min=0.6, Max=5 },
        new(new[]{ "NAV_ACC_RAD" }, Px4Flight, "Waypoint radius", "Distance at which a waypoint counts as reached.") { Units="m", Decimals=1, Min=0.05, Max=200 },

        // ── Rate control ─────────────────────────────────────────────
        new(new[]{ "MC_ROLLRATE_P" }, Px4Pid, "Roll rate P", "Roll rate controller P gain.") { Decimals=3, Min=0.01, Max=0.5 },
        new(new[]{ "MC_ROLLRATE_I" }, Px4Pid, "Roll rate I", "Roll rate controller I gain.") { Decimals=3, Min=0, Max=1 },
        new(new[]{ "MC_ROLLRATE_D" }, Px4Pid, "Roll rate D", "Roll rate controller D gain.") { Decimals=4, Min=0, Max=0.01 },
        new(new[]{ "MC_PITCHRATE_P" }, Px4Pid, "Pitch rate P", "Pitch rate controller P gain.") { Decimals=3, Min=0.01, Max=0.6 },
        new(new[]{ "MC_PITCHRATE_I" }, Px4Pid, "Pitch rate I", "Pitch rate controller I gain.") { Decimals=3, Min=0, Max=1 },
        new(new[]{ "MC_PITCHRATE_D" }, Px4Pid, "Pitch rate D", "Pitch rate controller D gain.") { Decimals=4, Min=0, Max=0.01 },
        new(new[]{ "MC_YAWRATE_P" }, Px4Pid, "Yaw rate P", "Yaw rate controller P gain.") { Decimals=3, Min=0, Max=0.6 },
        new(new[]{ "MC_YAWRATE_I" }, Px4Pid, "Yaw rate I", "Yaw rate controller I gain.") { Decimals=3, Min=0, Max=1 },
        new(new[]{ "MC_ROLL_P" }, Px4Pid, "Roll angle P", "Roll attitude controller gain.") { Decimals=2, Min=0, Max=12 },
        new(new[]{ "MC_PITCH_P" }, Px4Pid, "Pitch angle P", "Pitch attitude controller gain.") { Decimals=2, Min=0, Max=12 },
        new(new[]{ "MC_YAW_P" }, Px4Pid, "Yaw angle P", "Yaw attitude controller gain.") { Decimals=2, Min=0, Max=5 },
    };
}

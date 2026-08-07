using System;
using System.Collections.Generic;
using System.Linq;

namespace GCS.Core.Mavlink;

/// <summary>
/// The parameter names behind each failsafe setting, which differ by vehicle.
///
/// ArduPlane and ArduCopter name the same concepts differently — radio failsafe is
/// THR_FAILSAFE on a plane and FS_THR_ENABLE on a copter — and the plane's
/// short/long action pair has no copter equivalent at all. Writing plane names to a
/// copter does not fail loudly; it silently configures nothing, which is the worst
/// outcome for a safety screen.
/// </summary>
public sealed record FailsafeParameterSet(
    string RadioEnable,
    string RadioPwm,
    string GcsEnable,
    string? ShortAction,
    string? LongAction)
{
    /// <summary>True when this vehicle has the plane's short/long action pair.</summary>
    public bool HasShortLongActions => ShortAction != null && LongAction != null;

    /// <summary>Battery failsafe names are the same across vehicles.</summary>
    public static readonly string[] BatteryParameters =
    {
        "BATT_LOW_VOLT", "BATT_LOW_MAH", "BATT_LOW_TIMER", "BATT_FS_LOW_ACT",
    };

    public IEnumerable<string> AllNames()
    {
        foreach (var name in BatteryParameters) yield return name;

        yield return RadioEnable;
        yield return RadioPwm;
        yield return GcsEnable;

        if (ShortAction != null) yield return ShortAction;
        if (LongAction != null) yield return LongAction;
    }

    public static FailsafeParameterSet For(VehicleKind kind) => kind switch
    {
        VehicleKind.Copter or VehicleKind.Rover or VehicleKind.Submarine => new(
            RadioEnable: "FS_THR_ENABLE",
            RadioPwm: "FS_THR_VALUE",
            GcsEnable: "FS_GCS_ENABLE",
            ShortAction: null,      // no plane-style short/long pair
            LongAction: null),

        // Plane, VTOL, and unknown — the app's original airframe.
        _ => new(
            RadioEnable: "THR_FAILSAFE",
            RadioPwm: "THR_FS_VALUE",
            GcsEnable: "FS_GCS_ENABL",
            ShortAction: "FS_SHORT_ACTN",
            LongAction: "FS_LONG_ACTN"),
    };

    /// <summary>
    /// Whether a received parameter is this set's radio-enable, allowing for the
    /// other vehicle's spelling so a value is never silently dropped.
    /// </summary>
    public static bool IsRadioEnable(string name) =>
        Matches(name, "THR_FAILSAFE", "FS_THR_ENABLE");

    public static bool IsRadioPwm(string name) =>
        Matches(name, "THR_FS_VALUE", "FS_THR_VALUE");

    public static bool IsGcsEnable(string name) =>
        Matches(name, "FS_GCS_ENABL", "FS_GCS_ENABLE");

    private static bool Matches(string name, params string[] candidates) =>
        candidates.Any(c => string.Equals(c, name, StringComparison.OrdinalIgnoreCase));
}

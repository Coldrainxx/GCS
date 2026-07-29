using System.Collections.Generic;

namespace GCS.Core.Swarm;

public enum VehicleAlertLevel { None = 0, Warning = 1, Critical = 2 }

public readonly record struct VehicleHealthResult(VehicleAlertLevel Level, string Text)
{
    public bool HasAlert => Level != VehicleAlertLevel.None;
}

/// <summary>
/// Assesses one vehicle's health. Pure so every drone in a swarm can be judged
/// the same way and the rules can be tested — the alert engine historically only
/// ever watched the active vehicle, which left followers unmonitored.
/// </summary>
public static class VehicleHealthEvaluator
{
    public const int BatteryWarnPercent = 25;
    public const int BatteryCriticalPercent = 15;
    public const double TelemetryStaleSeconds = 4.0;

    /// <param name="secondsSinceUpdate">Age of the most recent telemetry.</param>
    /// <param name="batteryPercent">Remaining percent; ignored when <paramref name="hasBattery"/> is false.</param>
    /// <param name="hasBattery">Whether a battery reading has ever arrived.</param>
    /// <param name="hasGps">Whether a GPS report has ever arrived.</param>
    /// <param name="hasGpsFix">Whether that report shows a usable fix.</param>
    /// <param name="isArmed">Armed vehicles are judged harder — losing GPS in flight matters more.</param>
    public static VehicleHealthResult Evaluate(
        double secondsSinceUpdate,
        int batteryPercent,
        bool hasBattery,
        bool hasGps,
        bool hasGpsFix,
        bool isArmed)
    {
        var reasons = new List<string>();
        var level = VehicleAlertLevel.None;

        void Raise(VehicleAlertLevel l, string reason)
        {
            reasons.Add(reason);
            if (l > level) level = l;
        }

        // Silence outranks everything: nothing below can be trusted once the
        // vehicle has stopped reporting.
        if (secondsSinceUpdate > TelemetryStaleSeconds)
        {
            Raise(VehicleAlertLevel.Critical, $"no telemetry {secondsSinceUpdate:F0}s");
            return new VehicleHealthResult(level, string.Join(" · ", reasons));
        }

        // A 0% reading before any real measurement would cry wolf on every boot.
        if (hasBattery && batteryPercent > 0)
        {
            if (batteryPercent <= BatteryCriticalPercent)
                Raise(VehicleAlertLevel.Critical, $"battery {batteryPercent}%");
            else if (batteryPercent <= BatteryWarnPercent)
                Raise(VehicleAlertLevel.Warning, $"battery {batteryPercent}%");
        }

        if (hasGps && !hasGpsFix)
            Raise(isArmed ? VehicleAlertLevel.Critical : VehicleAlertLevel.Warning, "no GPS fix");

        return new VehicleHealthResult(level, reasons.Count == 0 ? "" : string.Join(" · ", reasons));
    }
}

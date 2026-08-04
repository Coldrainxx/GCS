using System;
using System.Collections.Generic;
using System.Linq;
using GCS.Core.Domain;

namespace GCS.Core.Advisor;

/// <summary>
/// Turns a telemetry snapshot into a per-component health assessment.
///
/// Two rules shape this class:
/// <list type="number">
/// <item>Only components with telemetry are scored. Anything the GCS cannot see is
/// reported as <see cref="ComponentStatus.NoData"/> and left out of the average.</item>
/// <item>Every deduction is independent. Chained conditions silently collapse into
/// one another and make whole rules unreachable.</item>
/// </list>
/// Pure and static so the rules can be tested without an aircraft.
/// </summary>
public static class FlightHealthAnalyzer
{
    // ── Thresholds ──────────────────────────────────────────────────

    public const double LinkStaleSeconds = 4.0;
    public const double TelemetryStaleSeconds = 4.0;

    /// <summary>Per-cell volts. Below this a LiPo is being damaged, not just drained.</summary>
    public const double CellCriticalVolts = 3.30;
    public const double CellWarnVolts = 3.50;

    /// <summary>Shared with the swarm evaluator so both surfaces agree on "low".</summary>
    public const int BatteryCriticalPercent = Swarm.VehicleHealthEvaluator.BatteryCriticalPercent;
    public const int BatteryWarnPercent = Swarm.VehicleHealthEvaluator.BatteryWarnPercent;

    /// <summary>Volts per minute. Steeper than this and the pack is draining unusually fast.</summary>
    public const double BatteryFastDrainVoltsPerMinute = -0.25;

    /// <summary>Spread between best and worst cell. A healthy pack stays tight.</summary>
    public const float CellImbalanceWarn = 0.15f;
    public const float CellImbalanceCritical = 0.30f;

    public const float BatteryHotC = 60f;

    public const int GpsMinSatellites = 6;
    public const double GpsMaxHdopMeters = 2.0;

    public const double ExtremeRollDeg = 60.0;
    public const double ExtremePitchDeg = 45.0;

    /// <summary>Maximum a LiPo cell reaches when fully charged, plus a little margin.</summary>
    private const double MaxCellVolts = 4.35;

    /// <summary>
    /// Below this, the autopilot is not measuring a pack at all. Even a deeply
    /// over-discharged 1S cell reads well above it.
    /// </summary>
    public const double MinPlausiblePackVolts = 3.0;

    // ── Entry point ─────────────────────────────────────────────────

    public static FlightHealthReport Analyze(
        VehicleState state,
        DateTime nowUtc,
        BatteryTrend? batteryTrend = null)
    {
        var components = new List<ComponentHealth>
        {
            AnalyzeLink(state, nowUtc),
            AnalyzeBattery(state, batteryTrend),
            AnalyzeGps(state),
            AnalyzeAttitude(state, nowUtc),
            AnalyzeVibration(state),
            AnalyzeEkf(state),
            AnalyzeMotors(state),
            AnalyzeEsc(state),
            AnalyzePower(state),
        };

        return new FlightHealthReport(components, nowUtc);
    }

    // ── Vibration ───────────────────────────────────────────────────

    /// <summary>Vibe levels in m/s². ArduPilot's own guidance: under 30 is good, over 60 is trouble.</summary>
    public const float VibrationWarn = 30f;
    public const float VibrationCritical = 60f;

    private static ComponentHealth AnalyzeVibration(VehicleState state)
    {
        if (state.Vibration is not { } vibe)
            return ComponentHealth.Unmonitored("Vibration", "VIBRATION not being streamed", isVital: false);

        var evidence = new List<HealthEvidence>();
        int score = 100;

        if (vibe.Worst >= VibrationCritical)
        {
            evidence.Add(new HealthEvidence(ComponentStatus.Critical,
                $"Vibration {vibe.Worst:F0} m/s² — above {VibrationCritical:F0}"));
            score -= 60;
        }
        else if (vibe.Worst >= VibrationWarn)
        {
            evidence.Add(new HealthEvidence(ComponentStatus.Warning,
                $"Vibration {vibe.Worst:F0} m/s² — elevated"));
            score -= 25;
        }

        // Clipping means the accelerometers saturated; even a few events corrupt
        // the position estimate.
        if (vibe.TotalClipping > 0)
        {
            evidence.Add(new HealthEvidence(ComponentStatus.Critical,
                $"Accelerometer clipping: {vibe.TotalClipping} events"));
            score -= 40;
        }

        return Build("Vibration", score, evidence,
            $"X {vibe.VibrationX:F0} · Y {vibe.VibrationY:F0} · Z {vibe.VibrationZ:F0} m/s²");
    }

    // ── EKF ─────────────────────────────────────────────────────────

    /// <summary>Variance ratios: below 0.5 healthy, at or above 1.0 the estimator is failing.</summary>
    public const float EkfVarianceWarn = 0.5f;
    public const float EkfVarianceCritical = 1.0f;

    private static ComponentHealth AnalyzeEkf(VehicleState state)
    {
        if (state.Ekf is not { } ekf)
            return ComponentHealth.Unmonitored("EKF", "EKF_STATUS_REPORT not being streamed", isVital: false);

        var evidence = new List<HealthEvidence>();
        int score = 100;

        void Check(string name, float variance)
        {
            if (variance >= EkfVarianceCritical)
            {
                evidence.Add(new HealthEvidence(ComponentStatus.Critical,
                    $"{name} variance {variance:F2}"));
                score -= 40;
            }
            else if (variance >= EkfVarianceWarn)
            {
                evidence.Add(new HealthEvidence(ComponentStatus.Warning,
                    $"{name} variance {variance:F2}"));
                score -= 15;
            }
        }

        Check("Velocity", ekf.VelocityVariance);
        Check("Horizontal position", ekf.PosHorizVariance);
        Check("Vertical position", ekf.PosVertVariance);
        Check("Compass", ekf.CompassVariance);

        // Flags are only meaningful once the estimator has started reporting.
        if (ekf.Flags != 0 && !ekf.AttitudeHealthy)
        {
            evidence.Add(new HealthEvidence(ComponentStatus.Critical, "EKF attitude estimate not healthy"));
            score -= 40;
        }

        return Build("EKF", score, evidence, $"worst variance {ekf.WorstVariance:F2}");
    }

    // ── Motors (from servo outputs) ─────────────────────────────────

    /// <summary>
    /// Spread across motor outputs, as a fraction of full range. A well-trimmed
    /// multirotor holds its motors within a few percent of each other; a wide
    /// spread means one motor is working much harder — a failing motor, a heavy
    /// arm, or a twisted frame.
    /// </summary>
    public const double MotorImbalanceWarn = 0.12;
    public const double MotorImbalanceCritical = 0.25;

    /// <summary>Output this close to maximum has no headroom left to stabilise.</summary>
    public const int MotorSaturationPwm = 1950;

    private static ComponentHealth AnalyzeMotors(VehicleState state)
    {
        if (state.ServoOutput is not { } servo)
            return ComponentHealth.Unmonitored("Motors", "SERVO_OUTPUT_RAW not being streamed", isVital: false);

        var outputs = servo.Active();

        // Only meaningful under power: on the ground every output sits at minimum,
        // where the spread is zero and tells us nothing.
        if (!state.IsArmed || outputs.Length < 2)
        {
            return ComponentHealth.Unmonitored("Motors",
                state.IsArmed ? "Too few active outputs to compare" : "Only assessed while armed",
                isVital: false);
        }

        var evidence = new List<HealthEvidence>();
        int score = 100;

        int min = outputs.Min(), max = outputs.Max();
        double spread = (max - min) / 1000.0;   // PWM range is ~1000-2000 µs

        if (spread >= MotorImbalanceCritical)
        {
            evidence.Add(new HealthEvidence(ComponentStatus.Critical,
                $"Motor outputs differ by {spread * 100:F0}% ({min}-{max} µs)"));
            score -= 55;
        }
        else if (spread >= MotorImbalanceWarn)
        {
            evidence.Add(new HealthEvidence(ComponentStatus.Warning,
                $"Motor outputs differ by {spread * 100:F0}% ({min}-{max} µs)"));
            score -= 25;
        }

        if (max >= MotorSaturationPwm)
        {
            evidence.Add(new HealthEvidence(ComponentStatus.Critical,
                $"Motor output saturated at {max} µs — no control headroom"));
            score -= 40;
        }

        return Build("Motors", score, evidence,
            $"{outputs.Length} outputs · {min}-{max} µs");
    }

    // ── ESC ─────────────────────────────────────────────────────────

    public const byte EscTempWarnC = 80;
    public const byte EscTempCriticalC = 100;
    public const int EscRpmSpreadWarn = 1500;

    private static ComponentHealth AnalyzeEsc(VehicleState state)
    {
        var active = state.Esc?.Active ?? Array.Empty<EscReading>();

        // Absent on most airframes: ESC telemetry needs bidirectional DShot or a
        // dedicated serial line, so silence here is a hardware fact, not a fault.
        if (state.Esc is null || active.Length == 0)
            return ComponentHealth.Unmonitored("ESC", "No ESC telemetry hardware reporting", isVital: false);

        var evidence = new List<HealthEvidence>();
        int score = 100;
        var esc = state.Esc;

        if (esc.MaxTemperatureC >= EscTempCriticalC)
        {
            evidence.Add(new HealthEvidence(ComponentStatus.Critical,
                $"ESC at {esc.MaxTemperatureC} °C"));
            score -= 55;
        }
        else if (esc.MaxTemperatureC >= EscTempWarnC)
        {
            evidence.Add(new HealthEvidence(ComponentStatus.Warning,
                $"ESC at {esc.MaxTemperatureC} °C"));
            score -= 25;
        }

        if (state.IsArmed && esc.RpmSpread >= EscRpmSpreadWarn)
        {
            evidence.Add(new HealthEvidence(ComponentStatus.Warning,
                $"Motor RPM spread {esc.RpmSpread}"));
            score -= 20;
        }

        return Build("ESC", score, evidence,
            $"{active.Length} ESCs · max {esc.MaxTemperatureC} °C");
    }

    // ── Power rails ─────────────────────────────────────────────────

    /// <summary>The 5 V rail. Below this the flight controller is close to browning out.</summary>
    public const float RailVoltsWarn = 4.6f;
    public const float RailVoltsCritical = 4.3f;

    private static ComponentHealth AnalyzePower(VehicleState state)
    {
        if (state.Power is not { } power)
            return ComponentHealth.Unmonitored("Power", "POWER_STATUS not being streamed", isVital: false);

        var evidence = new List<HealthEvidence>();
        int score = 100;

        if (power.RailVolts > 0 && power.RailVolts < RailVoltsCritical)
        {
            evidence.Add(new HealthEvidence(ComponentStatus.Critical,
                $"5V rail at {power.RailVolts:F2} V — brownout risk"));
            score -= 60;
        }
        else if (power.RailVolts > 0 && power.RailVolts < RailVoltsWarn)
        {
            evidence.Add(new HealthEvidence(ComponentStatus.Warning,
                $"5V rail low at {power.RailVolts:F2} V"));
            score -= 25;
        }

        if (power.Overcurrent)
        {
            evidence.Add(new HealthEvidence(ComponentStatus.Critical, "Peripheral overcurrent"));
            score -= 50;
        }

        return Build("Power", score, evidence, $"rail {power.RailVolts:F2} V");
    }

    // ── Link ────────────────────────────────────────────────────────

    private static ComponentHealth AnalyzeLink(VehicleState state, DateTime nowUtc)
    {
        if (state.Connection is null)
            return ComponentHealth.Unmonitored("Link", "No heartbeat received");

        // ConnectionState.LastHeartbeatUtc is NOT a live clock: the tracker only
        // republishes it on transitions (connect, primary swap, timeout), so it
        // would otherwise age forever while telemetry is flowing normally.
        // IsConnected is the authoritative liveness flag — the tracker's own Tick
        // clears it on timeout — and telemetry timestamps give the finer signal.
        if (!state.Connection.IsConnected)
        {
            return Build("Link", 0,
                new List<HealthEvidence> { new(ComponentStatus.Critical, "Link lost") },
                "Link lost");
        }

        DateTime? freshest = State.TelemetryFreshness.LatestUtc(state);
        if (freshest is null)
            return Build("Link", 100, new List<HealthEvidence>(), "Connected");

        double age = (nowUtc - freshest.Value).TotalSeconds;
        if (age > LinkStaleSeconds)
        {
            return Build("Link", 0,
                new List<HealthEvidence>
                {
                    new(ComponentStatus.Critical, $"No telemetry for {age:F0}s")
                },
                "Telemetry stalled");
        }

        return Build("Link", 100, new List<HealthEvidence>(), "Connected");
    }


    // ── Battery ─────────────────────────────────────────────────────

    private static ComponentHealth AnalyzeBattery(VehicleState state, BatteryTrend? trend)
    {
        var battery = state.Battery;

        // A near-zero reading means no battery monitor is configured, not a flat
        // pack: ArduPilot reports 0 V (and -1%) when BATT_MONITOR is off, and even
        // a single ruined LiPo cell sits far above this. Treating it as real
        // produced a permanent "0.24 V — CRITICAL" on aircraft with no sensor.
        if (battery is null || battery.VoltageVolts < MinPlausiblePackVolts)
            return ComponentHealth.Unmonitored("Battery", "No battery monitor configured");

        var evidence = new List<HealthEvidence>();
        int score = 100;

        // Infer pack size from the fullest reading seen, not the current one: a
        // half-empty pack is genuinely ambiguous about its cell count, and guessing
        // wrong there produces either a missed critical or a false alarm.
        double referenceVolts = trend is { PeakVolts: > 0 }
            ? Math.Max(trend.PeakVolts, battery.VoltageVolts)
            : battery.VoltageVolts;

        int cells = EstimateCellCount(referenceVolts);
        double perCell = battery.VoltageVolts / cells;

        // Each check stands alone — deliberately not nested.
        if (perCell < CellCriticalVolts)
        {
            evidence.Add(new HealthEvidence(ComponentStatus.Critical,
                $"Cell voltage {perCell:F2} V ({cells}S pack) — below {CellCriticalVolts:F2} V"));
            score -= 60;
        }
        else if (perCell < CellWarnVolts)
        {
            evidence.Add(new HealthEvidence(ComponentStatus.Warning,
                $"Cell voltage {perCell:F2} V ({cells}S pack) — getting low"));
            score -= 25;
        }

        // Remaining percent is only meaningful once the autopilot has reported it.
        if (battery.RemainingPercent > 0)
        {
            if (battery.RemainingPercent <= BatteryCriticalPercent)
            {
                evidence.Add(new HealthEvidence(ComponentStatus.Critical,
                    $"Battery {battery.RemainingPercent}% remaining"));
                score -= 50;
            }
            else if (battery.RemainingPercent <= BatteryWarnPercent)
            {
                evidence.Add(new HealthEvidence(ComponentStatus.Warning,
                    $"Battery {battery.RemainingPercent}% remaining"));
                score -= 20;
            }
        }

        if (trend is { HasEnoughData: true } && trend.SlopeVoltsPerMinute <= BatteryFastDrainVoltsPerMinute)
        {
            evidence.Add(new HealthEvidence(ComponentStatus.Warning,
                $"Voltage falling {Math.Abs(trend.SlopeVoltsPerMinute):F2} V/min"));
            score -= 15;
        }

        // BATTERY_STATUS, when streamed, gives per-cell voltages — which catch a
        // single failing cell that the pack total hides entirely.
        if (state.BatteryStatus is { } detail && detail.CellCount >= 2)
        {
            if (detail.CellImbalanceVolts >= CellImbalanceCritical)
            {
                evidence.Add(new HealthEvidence(ComponentStatus.Critical,
                    $"Cell imbalance {detail.CellImbalanceVolts:F2} V across {detail.CellCount} cells"));
                score -= 50;
            }
            else if (detail.CellImbalanceVolts >= CellImbalanceWarn)
            {
                evidence.Add(new HealthEvidence(ComponentStatus.Warning,
                    $"Cell imbalance {detail.CellImbalanceVolts:F2} V"));
                score -= 20;
            }

            if (detail.HasTemperature && detail.TemperatureC >= BatteryHotC)
            {
                evidence.Add(new HealthEvidence(ComponentStatus.Warning,
                    $"Pack at {detail.TemperatureC:F0} °C"));
                score -= 15;
            }
        }

        string summary = $"{battery.VoltageVolts:F1} V" +
                         (battery.RemainingPercent > 0 ? $" · {battery.RemainingPercent}%" : "");

        return Build("Battery", score, evidence, summary);
    }

    /// <summary>
    /// Fewest cells that could produce this pack voltage without any cell exceeding
    /// its maximum charge. Rounding to a nominal figure instead would call a full
    /// 25.2 V 6S pack a 7S one; this cannot, and it errs toward the higher per-cell
    /// reading, so an ambiguous pack is never reported as more critical than it is.
    /// </summary>
    public static int EstimateCellCount(double packVolts)
    {
        int cells = (int)Math.Ceiling(packVolts / MaxCellVolts);
        return Math.Clamp(cells, 1, 14);
    }

    // ── GPS ─────────────────────────────────────────────────────────

    private static ComponentHealth AnalyzeGps(VehicleState state)
    {
        var gps = state.Gps;
        if (gps is null)
            return ComponentHealth.Unmonitored("GPS", "No GPS telemetry");

        var evidence = new List<HealthEvidence>();
        int score = 100;

        if (!gps.HasFix)
        {
            // Losing the fix on the ground is a delay; losing it airborne is an emergency.
            var severity = state.IsArmed ? ComponentStatus.Critical : ComponentStatus.Warning;
            evidence.Add(new HealthEvidence(severity, $"No GPS fix ({gps.FixTypeString})"));
            score -= state.IsArmed ? 70 : 40;
        }
        else
        {
            if (gps.SatellitesVisible < GpsMinSatellites)
            {
                evidence.Add(new HealthEvidence(ComponentStatus.Warning,
                    $"Only {gps.SatellitesVisible} satellites"));
                score -= 20;
            }

            if (gps.HdopMeters > GpsMaxHdopMeters)
            {
                evidence.Add(new HealthEvidence(ComponentStatus.Warning,
                    $"HDOP {gps.HdopMeters:F1} — poor position accuracy"));
                score -= 15;
            }
        }

        return Build("GPS", score, evidence, $"{gps.FixTypeString} · {gps.SatellitesVisible} sats");
    }

    // ── Attitude ────────────────────────────────────────────────────

    private static ComponentHealth AnalyzeAttitude(VehicleState state, DateTime nowUtc)
    {
        var attitude = state.Attitude;
        if (attitude is null)
            return ComponentHealth.Unmonitored("Attitude", "No attitude telemetry");

        var evidence = new List<HealthEvidence>();
        int score = 100;

        double age = (nowUtc - attitude.TimestampUtc).TotalSeconds;
        if (age > TelemetryStaleSeconds)
        {
            evidence.Add(new HealthEvidence(ComponentStatus.Warning,
                $"Attitude stale for {age:F0}s"));
            score -= 30;
        }

        double rollDeg = attitude.RollRad * 180.0 / Math.PI;
        double pitchDeg = attitude.PitchRad * 180.0 / Math.PI;

        // Only meaningful in flight — a parked aircraft on a slope is not a fault.
        if (state.IsArmed && Math.Abs(rollDeg) > ExtremeRollDeg)
        {
            evidence.Add(new HealthEvidence(ComponentStatus.Warning,
                $"Bank angle {Math.Abs(rollDeg):F0}°"));
            score -= 20;
        }

        if (state.IsArmed && Math.Abs(pitchDeg) > ExtremePitchDeg)
        {
            evidence.Add(new HealthEvidence(ComponentStatus.Warning,
                $"Pitch angle {Math.Abs(pitchDeg):F0}°"));
            score -= 20;
        }

        return Build("Attitude", score, evidence, $"Roll {rollDeg:F0}° · Pitch {pitchDeg:F0}°");
    }

    // ── Helpers ─────────────────────────────────────────────────────

    private static ComponentHealth Build(
        string name, int score, List<HealthEvidence> evidence, string summary)
    {
        score = Math.Clamp(score, 0, 100);

        var status = evidence.Count == 0
            ? ComponentStatus.Ok
            : evidence.Max(e => e.Severity);

        return new ComponentHealth(name, status, score, summary, evidence);
    }
}

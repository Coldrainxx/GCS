using System;
using System.Collections.Generic;

namespace GCS.Core.Logging;

/// <summary>A moment worth showing on a timeline.</summary>
public sealed record FlightEvent(DateTime TimestampUtc, string Kind, string Text);

/// <summary>
/// One point on the flight's trace. Sampled rather than per-packet: telemetry
/// arrives several times a second and a plot cannot show more than a few thousand
/// points usefully.
/// </summary>
public readonly record struct FlightSample(
    DateTime TimestampUtc,
    float AltitudeRelM,
    float BatteryVolts,
    float GroundspeedMps,
    double Lat,
    double Lon,
    bool HasPosition,
    bool IsArmed);

/// <summary>
/// What a recorded flight contained.
///
/// Every figure here is derived from telemetry the GCS actually received. A .tlog
/// holds only what came down the link, so anything the aircraft never reported is
/// absent by nature — see <see cref="Notes"/>.
/// </summary>
public sealed class FlightLogSummary
{
    public string FilePath { get; init; } = "";
    public string FileName => System.IO.Path.GetFileName(FilePath);

    public DateTime StartUtc { get; set; }
    public DateTime EndUtc { get; set; }
    public TimeSpan Duration => EndUtc > StartUtc ? EndUtc - StartUtc : TimeSpan.Zero;

    public long PacketCount { get; set; }
    public SortedSet<byte> SystemIds { get; } = new();

    // ── Flight envelope ─────────────────────────────────────────────
    public float MaxAltitudeRelM { get; set; }
    public float MaxGroundspeedMps { get; set; }
    public float MaxAirspeedMps { get; set; }
    public double DistanceTravelledM { get; set; }
    public bool HasPosition { get; set; }

    // ── Battery ─────────────────────────────────────────────────────
    public bool HasBattery { get; set; }
    public float BatteryStartVolts { get; set; }
    public float BatteryEndVolts { get; set; }
    public float BatteryMinVolts { get; set; } = float.MaxValue;
    public int BatteryMinPercent { get; set; } = int.MaxValue;

    // ── Health telemetry ────────────────────────────────────────────
    // Present only when the autopilot was streaming these; older logs predate the
    // GCS asking for them, so absence means "not recorded", not "healthy".

    public bool HasVibration { get; set; }
    public float MaxVibration { get; set; }
    public uint MaxClipping { get; set; }

    public bool HasEkf { get; set; }
    public float MaxEkfVariance { get; set; }

    public bool HasServoOutput { get; set; }
    /// <summary>Widest spread across motor outputs while armed, as a fraction of range.</summary>
    public double MaxMotorImbalance { get; set; }

    public bool HasPower { get; set; }
    public float MinRailVolts { get; set; }

    // ── GPS ─────────────────────────────────────────────────────────
    public bool HasGps { get; set; }
    public byte WorstGpsFix { get; set; } = byte.MaxValue;
    public byte MinSatellites { get; set; } = byte.MaxValue;

    // ── Timeline ────────────────────────────────────────────────────
    public List<FlightEvent> Events { get; } = new();

    /// <summary>Sampled trace of the flight, for plotting altitude, battery and track.</summary>
    public List<FlightSample> Samples { get; } = new();

    /// <summary>Total time spent armed, summed across arm/disarm cycles.</summary>
    public TimeSpan ArmedDuration { get; set; }

    public int ArmCount { get; set; }

    /// <summary>Distinct problems the health rules found while replaying.</summary>
    public List<string> Findings { get; } = new();

    /// <summary>Limitations of this log, so absent data is not read as good data.</summary>
    public List<string> Notes { get; } = new();

    public string DurationText => Duration.TotalHours >= 1
        ? $"{(int)Duration.TotalHours}h {Duration.Minutes}m {Duration.Seconds}s"
        : Duration.TotalMinutes >= 1
            ? $"{Duration.Minutes}m {Duration.Seconds}s"
            : $"{Duration.Seconds}s";

    public string DistanceText => DistanceTravelledM >= 1000
        ? $"{DistanceTravelledM / 1000.0:F2} km"
        : $"{DistanceTravelledM:F0} m";
}

using System;
using System.Collections.Generic;
using System.Linq;

namespace GCS.Core.Advisor;

/// <summary>
/// Rolling battery-voltage history and its slope.
///
/// A single voltage reading says little — a pack under load reads low and recovers.
/// The useful signal is the trend, so this keeps a short window and fits a slope
/// across it. Kept separate from the analyzer so it can be tested with synthetic
/// samples rather than a live aircraft.
/// </summary>
public sealed class BatteryTrend
{
    /// <summary>Samples closer together than this are ignored, to bound memory at high telemetry rates.</summary>
    public const double MinSampleIntervalSeconds = 1.0;

    /// <summary>Enough samples spanning enough time to fit a meaningful slope.</summary>
    public const int MinSamplesForTrend = 5;
    public const double MinSpanSeconds = 10.0;

    private readonly TimeSpan _window;
    private readonly List<(DateTime Time, double Volts)> _samples = new();

    public BatteryTrend(TimeSpan? window = null) =>
        _window = window ?? TimeSpan.FromMinutes(2);

    public int SampleCount => _samples.Count;

    /// <summary>
    /// Highest pack voltage seen this session. Cell count can only be inferred
    /// reliably from a full-ish pack — at 19.2 V a reading is equally a healthy 5S
    /// and a dangerously flat 6S — so the peak is retained and used for that,
    /// rather than the instantaneous voltage. Survives window eviction.
    /// </summary>
    public double PeakVolts { get; private set; }

    public void Add(DateTime timeUtc, double volts)
    {
        // A pack reading near zero is not discharging, it is not being measured.
        // Filtering here also keeps PeakVolts (which drives cell-count inference)
        // clean when no battery monitor is configured.
        if (volts < FlightHealthAnalyzer.MinPlausiblePackVolts) return;

        if (_samples.Count > 0)
        {
            var last = _samples[^1];

            // Clock went backwards (replay, log seek): start over rather than fit
            // noise. Checked before the rate limit, since a backwards jump also
            // looks like "too soon" and would otherwise be dropped silently.
            if (timeUtc < last.Time)
            {
                _samples.Clear();
            }
            else if ((timeUtc - last.Time).TotalSeconds < MinSampleIntervalSeconds)
            {
                return;
            }
        }

        PeakVolts = Math.Max(PeakVolts, volts);
        _samples.Add((timeUtc, volts));

        DateTime cutoff = timeUtc - _window;
        _samples.RemoveAll(s => s.Time < cutoff);
    }

    public void Reset()
    {
        _samples.Clear();
        PeakVolts = 0;
    }

    public double SpanSeconds =>
        _samples.Count < 2 ? 0 : (_samples[^1].Time - _samples[0].Time).TotalSeconds;

    public bool HasEnoughData =>
        _samples.Count >= MinSamplesForTrend && SpanSeconds >= MinSpanSeconds;

    /// <summary>
    /// Least-squares slope in volts per minute. Negative means discharging.
    /// Returns 0 until <see cref="HasEnoughData"/> — an unfounded trend is worse
    /// than none, because it drives advisories.
    /// </summary>
    public double SlopeVoltsPerMinute
    {
        get
        {
            if (!HasEnoughData) return 0;

            double t0 = _samples[0].Time.Ticks / (double)TimeSpan.TicksPerMinute;
            double meanT = _samples.Average(s => s.Time.Ticks / (double)TimeSpan.TicksPerMinute - t0);
            double meanV = _samples.Average(s => s.Volts);

            double num = 0, den = 0;
            foreach (var (time, volts) in _samples)
            {
                double t = time.Ticks / (double)TimeSpan.TicksPerMinute - t0 - meanT;
                num += t * (volts - meanV);
                den += t * t;
            }

            return Math.Abs(den) < 1e-9 ? 0 : num / den;
        }
    }

    public double LatestVolts => _samples.Count == 0 ? 0 : _samples[^1].Volts;
}

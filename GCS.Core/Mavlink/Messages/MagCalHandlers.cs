using GCS.Core.Mavlink.Dispatch;
using MavLinkSharp;
using System;

namespace GCS.Core.Mavlink.Messages;

/// <summary>Handles MAG_CAL_PROGRESS (message ID 191) — live compass calibration progress.</summary>
public class MagCalProgressHandler : IMavlinkMessageHandler
{
    private readonly Action<MagCalProgressData> _onProgress;

    public MagCalProgressHandler(Action<MagCalProgressData> onProgress)
    {
        _onProgress = onProgress ?? throw new ArgumentNullException(nameof(onProgress));
    }

    public uint MessageId => 191;

    public void Handle(Frame frame)
    {
        try
        {
            _onProgress(new MagCalProgressData
            {
                CompassId = MagCalFields.Byte(frame, "compass_id"),
                CalStatus = MagCalFields.Byte(frame, "cal_status"),
                CompletionPct = MagCalFields.Byte(frame, "completion_pct"),
                CompletionMask = MagCalFields.ByteArray(frame, "completion_mask", 10),
                DirectionX = MagCalFields.Float(frame, "direction_x"),
                DirectionY = MagCalFields.Float(frame, "direction_y"),
                DirectionZ = MagCalFields.Float(frame, "direction_z"),
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"MAG_CAL_PROGRESS parse error: {ex.Message}");
        }
    }
}

/// <summary>Handles MAG_CAL_REPORT (message ID 192) — the result of a compass calibration.</summary>
public class MagCalReportHandler : IMavlinkMessageHandler
{
    private readonly Action<MagCalReportData> _onReport;

    public MagCalReportHandler(Action<MagCalReportData> onReport)
    {
        _onReport = onReport ?? throw new ArgumentNullException(nameof(onReport));
    }

    public uint MessageId => 192;

    public void Handle(Frame frame)
    {
        try
        {
            _onReport(new MagCalReportData
            {
                CompassId = MagCalFields.Byte(frame, "compass_id"),
                CalStatus = MagCalFields.Byte(frame, "cal_status"),
                Autosaved = MagCalFields.Byte(frame, "autosaved"),
                Fitness = MagCalFields.Float(frame, "fitness"),
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"MAG_CAL_REPORT parse error: {ex.Message}");
        }
    }
}

public record MagCalProgressData
{
    public byte CompassId { get; init; }
    public byte CalStatus { get; init; }
    public byte CompletionPct { get; init; }
    /// <summary>Bitmask of the 80 sphere sections sampled so far (10 bytes).</summary>
    public byte[] CompletionMask { get; init; } = Array.Empty<byte>();
    public float DirectionX { get; init; }
    public float DirectionY { get; init; }
    public float DirectionZ { get; init; }
}

public record MagCalReportData
{
    public byte CompassId { get; init; }
    public byte CalStatus { get; init; }
    public byte Autosaved { get; init; }
    public float Fitness { get; init; }
}

internal static class MagCalFields
{
    public static byte Byte(Frame frame, string field)
    {
        if (!frame.Fields.TryGetValue(field, out var v)) return 0;
        return v switch { byte b => b, sbyte s => (byte)s, int i => (byte)i, uint u => (byte)u, _ => 0 };
    }

    public static float Float(Frame frame, string field)
    {
        if (!frame.Fields.TryGetValue(field, out var v)) return 0f;
        return v switch { float f => f, double d => (float)d, int i => i, _ => 0f };
    }

    public static byte[] ByteArray(Frame frame, string field, int length)
    {
        if (frame.Fields.TryGetValue(field, out var v) && v is byte[] arr)
            return arr;
        return new byte[length];
    }
}

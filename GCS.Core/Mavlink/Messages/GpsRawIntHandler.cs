using GCS.Core.Domain;
using GCS.Core.Mavlink.Dispatch;
using MavLinkSharp;
using System;

namespace GCS.Core.Mavlink.Messages;

/// <summary>
/// Handles GPS_RAW_INT (msg 24) - GPS fix info and satellite count
/// </summary>
public sealed class GpsRawIntHandler : IMavlinkMessageHandler
{
    public uint MessageId => 24;  // GPS_RAW_INT

    private readonly Action<byte, GpsState> _onGpsState;

    public GpsRawIntHandler(Action<byte, GpsState> onGpsState)
    {
        _onGpsState = onGpsState;
    }

    public void Handle(Frame frame)
    {
        // cog is UINT16_MAX when the receiver has no course; eph/epv likewise.
        ushort cog = Read(frame, "cog", (ushort)0);

        var state = new GpsState(
            FixType: Convert.ToByte(frame.Fields["fix_type"]),
            SatellitesVisible: Convert.ToByte(frame.Fields["satellites_visible"]),
            Eph: Convert.ToUInt16(frame.Fields["eph"]),
            Epv: Convert.ToUInt16(frame.Fields["epv"]),
            TimestampUtc: DateTime.UtcNow,

            // The receiver's own fix. Kept because the estimator may have no fused
            // global position while the GPS itself is perfectly healthy.
            LatitudeDeg: Read(frame, "lat", 0) / 1e7,
            LongitudeDeg: Read(frame, "lon", 0) / 1e7,
            AltitudeMslMeters: Read(frame, "alt", 0) / 1000f,
            CourseOverGroundDeg: cog == ushort.MaxValue ? 0f : cog / 100f
        );

        _onGpsState(frame.SystemId, state);
    }

    private static int Read(Frame frame, string name, int fallback) =>
        frame.Fields.TryGetValue(name, out var v) ? Convert.ToInt32(v) : fallback;

    private static ushort Read(Frame frame, string name, ushort fallback) =>
        frame.Fields.TryGetValue(name, out var v) ? Convert.ToUInt16(v) : fallback;
}
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

    private readonly Action<GpsState> _onGpsState;

    public GpsRawIntHandler(Action<GpsState> onGpsState)
    {
        _onGpsState = onGpsState;
    }

    public void Handle(Frame frame)
    {
        var state = new GpsState(
            FixType: Convert.ToByte(frame.Fields["fix_type"]),
            SatellitesVisible: Convert.ToByte(frame.Fields["satellites_visible"]),
            Eph: Convert.ToUInt16(frame.Fields["eph"]),
            Epv: Convert.ToUInt16(frame.Fields["epv"]),
            TimestampUtc: DateTime.UtcNow
        );

        _onGpsState(state);
    }
}
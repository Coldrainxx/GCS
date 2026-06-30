using GCS.Core.Domain;
using GCS.Core.Mavlink.Dispatch;
using MavLinkSharp;
using System;
using System.Diagnostics;

namespace GCS.Core.Mavlink.Messages;

/// <summary>
/// Handles GLOBAL_POSITION_INT (msg 33) - GPS position data.
/// </summary>
public sealed class GlobalPositionHandler : IMavlinkMessageHandler
{
    public uint MessageId => 33;

    private readonly Action<PositionState> _onPosition;

    public GlobalPositionHandler(Action<PositionState> onPosition)
    {
        _onPosition = onPosition;
     
    }

    public void Handle(Frame frame)
    {
     

        try
        {


  

            int latE7 = Convert.ToInt32(frame.Fields["lat"]);
            int lonE7 = Convert.ToInt32(frame.Fields["lon"]);
            int altMm = Convert.ToInt32(frame.Fields["alt"]);
            int relAltMm = Convert.ToInt32(frame.Fields["relative_alt"]);
            short vx = Convert.ToInt16(frame.Fields["vx"]);
            short vy = Convert.ToInt16(frame.Fields["vy"]);
            short vz = Convert.ToInt16(frame.Fields["vz"]);
            ushort hdgCdeg = Convert.ToUInt16(frame.Fields["hdg"]);

            double lat = latE7 / 1e7;
            double lon = lonE7 / 1e7;

            float velNorth = vx / 100.0f;
            float velEast = vy / 100.0f;

            // hdg == 65535 (UINT16_MAX) is the MAVLink "heading unavailable" sentinel.
            // When set, fall back to course-over-ground derived from the NED velocity
            // (atan2(east, north)) so the map icon still points sensibly while moving.
            float headingDeg;
            if (hdgCdeg == ushort.MaxValue)
            {
                if (Math.Abs(velNorth) > 0.1f || Math.Abs(velEast) > 0.1f)
                {
                    double course = Math.Atan2(velEast, velNorth) * (180.0 / Math.PI);
                    headingDeg = (float)((course + 360.0) % 360.0);
                }
                else
                {
                    headingDeg = 0f; // stationary and no compass heading
                }
            }
            else
            {
                headingDeg = hdgCdeg / 100.0f;
            }

            var state = new PositionState(
                LatitudeDeg: lat,
                LongitudeDeg: lon,
                AltitudeMslMeters: (float)(altMm / 1000.0),
                AltitudeRelMeters: (float)(relAltMm / 1000.0),
                HeadingDeg: headingDeg,
                VelocityNorthMps: velNorth,
                VelocityEastMps: velEast,
                VelocityDownMps: vz / 100.0f,
                TimestampUtc: DateTime.UtcNow
            );


            _onPosition(state);
 
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[GlobalPositionHandler] ERROR: {ex.Message}");
            Debug.WriteLine($"[GlobalPositionHandler] Stack: {ex.StackTrace}");
        }
    }
}
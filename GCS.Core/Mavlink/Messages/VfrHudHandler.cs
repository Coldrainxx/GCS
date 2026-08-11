using GCS.Core.Domain;
using GCS.Core.Mavlink.Dispatch;
using MavLinkSharp;
using System;

namespace GCS.Core.Mavlink.Messages;

public sealed class VfrHudHandler : IMavlinkMessageHandler
{
    public uint MessageId => 74; // VFR_HUD

    private readonly Action<byte, VfrHudState> _onHud;

    public VfrHudHandler(Action<byte, VfrHudState> onHud)
    {
        _onHud = onHud;
    }

    public void Handle(Frame frame)
    {
        // PX4 reports NaN for a value it has no sensor for — airspeed on a copter,
        // typically. NaN must not escape the handler: it renders as "NaN" and
        // poisons every comparison and average it later reaches.
        float airspeed = Finite(frame.Fields["airspeed"]);
        float groundspeed = Finite(frame.Fields["groundspeed"]);
        short headingRaw = Convert.ToInt16(frame.Fields["heading"]);
        float climb = Finite(frame.Fields["climb"]);

        _onHud(frame.SystemId,
            new VfrHudState(
                AirspeedMps: airspeed,
                GroundspeedMps: groundspeed,
                HeadingDeg: headingRaw,
                ClimbMps: climb,
                TimestampUtc: DateTime.UtcNow,
                HasAirspeed: IsFinite(frame.Fields["airspeed"])
            )
        );
    }

    private static bool IsFinite(object? field)
    {
        float v = Convert.ToSingle(field);
        return !float.IsNaN(v) && !float.IsInfinity(v);
    }

    private static float Finite(object? field)
    {
        float v = Convert.ToSingle(field);
        return float.IsNaN(v) || float.IsInfinity(v) ? 0f : v;
    }
}

using GCS.Core.Domain;
using GCS.Core.Mavlink.Connection;
using GCS.Core.Mavlink.Dispatch;
using MavLinkSharp;
using System;

namespace GCS.Core.Mavlink.Messages;

public sealed class HeartbeatHandler : IMavlinkMessageHandler
{
    public uint MessageId => 0;

    private readonly MavlinkConnectionTracker _connection;
    private readonly Action<HeartbeatState> _onHeartbeat;

    public HeartbeatHandler(
        MavlinkConnectionTracker connection,
        Action<HeartbeatState> onHeartbeat)
    {
        _connection = connection;
        _onHeartbeat = onHeartbeat;
    }

    public void Handle(Frame frame)
    {
        var now = DateTime.UtcNow;

        uint customMode = Convert.ToUInt32(frame.Fields["custom_mode"]);
        byte baseMode = Convert.ToByte(frame.Fields["base_mode"]);

        // Check armed status from base_mode (bit 7 = 0x80 = 128)
        bool isArmed = (baseMode & 0x80) != 0;

        // Mode numbers are per vehicle family: Copter mode 5 is Loiter, Plane 5 is
        // FBWA. Decode against the right table rather than assuming a plane.
        byte mavType = frame.Fields.TryGetValue("type", out var t) ? Convert.ToByte(t) : (byte)0;
        var kind = ArdupilotFlightModes.KindFromMavType(mavType);

        // PX4 packs main/sub modes into custom_mode instead of using a flat number,
        // so which firmware is flying decides how the value is read at all.
        byte autopilotId = frame.Fields.TryGetValue("autopilot", out var a) ? Convert.ToByte(a) : (byte)0;
        var autopilot = Px4FlightModes.KindFromMavAutopilot(autopilotId);

        // The plane-typed enum is ArduPilot's; PX4 mode numbers do not map onto it.
        var mode = autopilot == AutopilotKind.Px4
            ? null
            : ArdupilotFlightModes.PlaneMode(kind, customMode);

        string modeName = FlightModeTable.Describe(autopilot, kind, customMode);

        _connection.OnHeartbeat(
            frame.SystemId,
            frame.ComponentId,
            now
        );

        _onHeartbeat(new HeartbeatState(
            frame.SystemId,
            frame.ComponentId,
            mode,
            isArmed,
            now,
            kind,
            modeName,
            autopilot
        ));
    }
}
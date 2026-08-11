using MavLinkSharp;
using MavLinkSharp.Enums;

namespace GCS.Core.Mavlink;

public static class MavlinkBootstrap
{
    private static readonly object Gate = new();
    private static bool _initialised;

    /// <summary>
    /// Initialise the MAVLink dialect once per process.
    ///
    /// Anything that parses frames needs this, not just the live link — log replay
    /// runs with no connection and previously threw "MavLink.Initialize() must be
    /// called" because only app startup did it.
    /// </summary>
    public static void EnsureInitialized()
    {
        if (_initialised) return;

        lock (Gate)
        {
            if (_initialised) return;
            Init();
            _initialised = true;
        }
    }

    public static void Init()
    {
        // ArduPilot's own dialect, not Common: EKF_STATUS_REPORT (193) and the
        // ESC_TELEMETRY messages are ArduPilot extensions and are rejected outright
        // by the Common dialect. Ardupilotmega is a superset, so everything that
        // already worked still parses.
        MavLink.Initialize(DialectType.Ardupilotmega, new uint[]
        {
            0,   // HEARTBEAT
            1,   // SYS_STATUS
            11,  // SET_MODE
            
            // Parameter protocol
            20,  // PARAM_REQUEST_READ
            21,  // PARAM_REQUEST_LIST
            22,  // PARAM_VALUE
            23,  // PARAM_SET
            
            24,  // GPS_RAW_INT
            30,  // ATTITUDE
            33,  // GLOBAL_POSITION_INT
            
            // Mission protocol
            39,  // MISSION_ITEM
            40,  // MISSION_REQUEST
            41,  // MISSION_SET_CURRENT
            43,  // MISSION_REQUEST_LIST
            44,  // MISSION_COUNT
            45,  // MISSION_CLEAR_ALL
            47,  // MISSION_ACK
            51,  // MISSION_REQUEST_INT
            73,  // MISSION_ITEM_INT
            
            36,  // SERVO_OUTPUT_RAW  (motor output balance)
            66,  // REQUEST_DATA_STREAM (asks the vehicle to start streaming)
            65,  // RC_CHANNELS
            74,  // VFR_HUD

            // Health telemetry. ArduPilot does not stream these by default, so the
            // backend asks for them on connect — see RequestHealthStreamsAsync.
            125, // POWER_STATUS
            147, // BATTERY_STATUS      (per-cell, consumed mAh, temperature)
            193, // EKF_STATUS_REPORT   (ArduPilot)
            230, // ESTIMATOR_STATUS    (PX4's equivalent)
            241, // VIBRATION
            // ESC telemetry lives at 11030+ in ArduPilot's dialect. The 291-293
            // range is a different message set in Common — using it would have
            // silently decoded the wrong thing.
            11030, // ESC_TELEMETRY_1_TO_4
            11031, // ESC_TELEMETRY_5_TO_8
            11032, // ESC_TELEMETRY_9_TO_12

            // PX4 Follow-Me: the GCS streams the leader's position and each
            // follower holds a station around it. See FollowTargetRelay.
            144, // FOLLOW_TARGET

            75,  // COMMAND_INT (guided goto / DO_REPOSITION)
            76,  // COMMAND_LONG
            77,  // COMMAND_ACK
            253  // STATUSTEXT
        });
    }
}
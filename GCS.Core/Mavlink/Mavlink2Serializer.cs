using MavLinkSharp;
using System;
using System.Collections.Generic;

namespace GCS.Core.Mavlink;

/// <summary>
/// Builds outgoing MAVLink v2 packets using MavLinkSharp's Frame API.
/// All serialization, field ordering, and CRC are handled by the library.
/// </summary>
public static class Mavlink2Serializer
{
    private static byte _seq;

    /// <summary>
    /// Generic serializer: looks up the message definition from Metadata,
    /// uses Frame.SetFields() + Frame.ToBytes() for correct serialization.
    /// </summary>
    public static ReadOnlyMemory<byte> Build(
        uint messageId,
        byte sysId,
        byte compId,
        Dictionary<string, object> fieldValues)
    {
        var msg = Metadata.Messages[messageId];

        var frame = new Frame
        {
            StartMarker = Protocol.V2.StartMarker,
            SystemId = sysId,
            ComponentId = compId,
            MessageId = messageId,
            Message = msg,
            PacketSequence = unchecked(_seq++)
        };

        frame.SetFields(fieldValues);

        return frame.ToBytes();
    }

    // ── convenience wrappers ────────────────────────────────────────

    /// <summary>COMMAND_LONG (76)</summary>
    public static ReadOnlyMemory<byte> CommandLong(
        byte targetSys, byte targetComp,
        byte senderSys, byte senderComp,
        ushort command, byte confirmation = 0,
        float p1 = 0, float p2 = 0, float p3 = 0, float p4 = 0,
        float p5 = 0, float p6 = 0, float p7 = 0)
    {
        return Build(76, senderSys, senderComp, new()
        {
            ["target_system"] = targetSys,
            ["target_component"] = targetComp,
            ["command"] = command,
            ["confirmation"] = confirmation,
            ["param1"] = p1,
            ["param2"] = p2,
            ["param3"] = p3,
            ["param4"] = p4,
            ["param5"] = p5,
            ["param6"] = p6,
            ["param7"] = p7,
        });
    }

    /// <summary>
    /// REQUEST_DATA_STREAM (66). Deprecated in the MAVLink spec but still what
    /// ArduPilot actually honours, and what Mission Planner sends on connect.
    /// Without it a vehicle whose SRn_* rates are zero sends only heartbeats.
    /// </summary>
    public static ReadOnlyMemory<byte> RequestDataStream(
        byte targetSys, byte targetComp,
        byte senderSys, byte senderComp,
        byte streamId, ushort rateHz, bool start = true)
    {
        return Build(66, senderSys, senderComp, new()
        {
            ["target_system"] = targetSys,
            ["target_component"] = targetComp,
            ["req_stream_id"] = streamId,
            ["req_message_rate"] = rateHz,
            ["start_stop"] = (byte)(start ? 1 : 0),
        });
    }

    /// <summary>SET_MODE (11)</summary>
    public static ReadOnlyMemory<byte> SetMode(
        byte targetSys,
        byte senderSys, byte senderComp,
        byte baseMode, uint customMode)
    {
        return Build(11, senderSys, senderComp, new()
        {
            ["target_system"] = targetSys,
            ["base_mode"] = baseMode,
            ["custom_mode"] = customMode,
        });
    }

    /// <summary>PARAM_SET (23)</summary>
    public static ReadOnlyMemory<byte> ParamSet(
        byte targetSys, byte targetComp,
        byte senderSys, byte senderComp,
        string paramId, float value, byte paramType = 9) // 9 = MAV_PARAM_TYPE_REAL32
    {
        // param_id is a char[16] field — pass as char array
        var paramChars = new char[16];
        for (int i = 0; i < Math.Min(paramId.Length, 16); i++)
            paramChars[i] = paramId[i];

        return Build(23, senderSys, senderComp, new()
        {
            ["target_system"] = targetSys,
            ["target_component"] = targetComp,
            ["param_id"] = paramChars,
            ["param_value"] = value,
            ["param_type"] = paramType,
        });
    }

    /// <summary>
    /// FOLLOW_TARGET (144) — the position PX4's Follow-Me mode flies around.
    ///
    /// PX4 reads only lat/lon/alt and the velocity triplet; it stamps the message
    /// with its own clock on arrival and ignores everything else. The remaining
    /// fields are still sent, zeroed, because the spec defines zero as "unknown"
    /// for each of them.
    ///
    /// The message carries no target_system, so it is addressed by where it is
    /// sent rather than by its contents.
    /// </summary>
    /// <param name="timestampMs">Sender's time since boot. PX4 overwrites it.</param>
    /// <param name="altMslMeters">Target altitude above mean sea level.</param>
    public static ReadOnlyMemory<byte> FollowTarget(
        byte senderSys, byte senderComp,
        double latitudeDeg, double longitudeDeg, float altMslMeters,
        float velNorthMps, float velEastMps, float velDownMps,
        ulong timestampMs)
    {
        // est_capabilities bit positions: POS = 0, VEL = 1, ACCEL = 2, ATT+RATES = 3.
        const byte capabilities = (1 << 0) | (1 << 1);

        return Build(144, senderSys, senderComp, new()
        {
            ["timestamp"] = timestampMs,
            ["est_capabilities"] = capabilities,
            ["lat"] = (int)Math.Round(latitudeDeg * 1e7),
            ["lon"] = (int)Math.Round(longitudeDeg * 1e7),
            ["alt"] = altMslMeters,
            ["vel"] = new[] { velNorthMps, velEastMps, velDownMps },
            ["acc"] = new float[3],
            ["attitude_q"] = new float[4],
            ["rates"] = new float[3],
            ["position_cov"] = new float[3],
            ["custom_state"] = 0UL,
        });
    }

    /// <summary>PARAM_REQUEST_READ (20)</summary>
    public static ReadOnlyMemory<byte> ParamRequestRead(
        byte targetSys, byte targetComp,
        byte senderSys, byte senderComp,
        string paramId)
    {
        var paramChars = new char[16];
        for (int i = 0; i < Math.Min(paramId.Length, 16); i++)
            paramChars[i] = paramId[i];

        return Build(20, senderSys, senderComp, new()
        {
            ["target_system"] = targetSys,
            ["target_component"] = targetComp,
            ["param_id"] = paramChars,
            ["param_index"] = (short)-1, // named lookup
        });
    }
}
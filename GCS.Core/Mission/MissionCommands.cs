using GCS.Core.Domain;
using System.Diagnostics;
using GCS.Core.Mavlink;

namespace GCS.Core.Mission;

/// <summary>
/// GCS identity for outgoing packets. Public because anything that builds a
/// packet outside the backend needs the same identity — see FollowTargetRelay.
/// </summary>
public static class GcsIdentity
{
    public const byte SystemId = 255;
    public const byte ComponentId = 190;
}

public static class MissionClearAllCommand
{
    public static byte[] Create(byte targetSys, byte targetComp)
    {
        return Mavlink2Serializer.Build(
            messageId: 45,
            sysId: GcsIdentity.SystemId,
            compId: GcsIdentity.ComponentId,
            fieldValues: new()
            {
                ["target_system"] = targetSys,
                ["target_component"] = targetComp,
                ["mission_type"] = (byte)0
            }
        ).ToArray();
    }
}

public static class MissionCountCommand
{
    public static byte[] Create(byte targetSys, byte targetComp, ushort count)
    {
        return Mavlink2Serializer.Build(
            messageId: 44,
            sysId: GcsIdentity.SystemId,
            compId: GcsIdentity.ComponentId,
            fieldValues: new()
            {
                ["target_system"] = targetSys,
                ["target_component"] = targetComp,
                ["count"] = count,
                ["mission_type"] = (byte)0
            }
        ).ToArray();
    }
}

public static class MissionRequestListCommand
{
    public static byte[] Create(byte targetSys, byte targetComp)
    {
        return Mavlink2Serializer.Build(
            messageId: 43,
            sysId: GcsIdentity.SystemId,
            compId: GcsIdentity.ComponentId,
            fieldValues: new()
            {
                ["target_system"] = targetSys,
                ["target_component"] = targetComp,
                ["mission_type"] = (byte)0
            }
        ).ToArray();
    }
}

public static class MissionRequestIntCommand
{
    public static byte[] Create(ushort sequence, byte targetSys, byte targetComp)
    {
        return Mavlink2Serializer.Build(
            messageId: 51,
            sysId: GcsIdentity.SystemId,
            compId: GcsIdentity.ComponentId,
            fieldValues: new()
            {
                ["target_system"] = targetSys,
                ["target_component"] = targetComp,
                ["seq"] = sequence,
                ["mission_type"] = (byte)0
            }
        ).ToArray();
    }
}

public static class MissionItemIntCommand
{
    public static byte[] Create(MissionItem item, byte targetSys, byte targetComp)
    {
        int latInt = (int)(item.LatitudeDeg * 1e7);
        int lonInt = (int)(item.LongitudeDeg * 1e7);

        Debug.WriteLine($"[MissionItemIntCommand] Building: seq={item.Sequence}, CMD={item.Command}, frame={item.Frame}, lat={item.LatitudeDeg:F6}, lon={item.LongitudeDeg:F6}, alt={item.AltitudeMeters}");

        return Mavlink2Serializer.Build(
            messageId: 73,
            sysId: GcsIdentity.SystemId,
            compId: GcsIdentity.ComponentId,
            fieldValues: new()
            {
                ["target_system"] = targetSys,
                ["target_component"] = targetComp,
                ["seq"] = (ushort)item.Sequence,
                ["frame"] = item.Frame,
                ["command"] = item.Command,
                ["current"] = (byte)(item.Sequence == 0 ? 1 : 0),
                ["autocontinue"] = (byte)(item.AutoContinue ? 1 : 0),
                ["param1"] = item.Param1,
                ["param2"] = item.Param2,
                ["param3"] = item.Param3,
                ["param4"] = item.Param4,
                ["x"] = latInt,
                ["y"] = lonInt,
                ["z"] = item.AltitudeMeters,
                ["mission_type"] = (byte)0
            }
        ).ToArray();
    }
}

public static class MissionAckCommand
{
    public static byte[] Create(byte targetSys, byte targetComp, byte result)
    {
        return Mavlink2Serializer.Build(
            messageId: 47,
            sysId: GcsIdentity.SystemId,
            compId: GcsIdentity.ComponentId,
            fieldValues: new()
            {
                ["target_system"] = targetSys,
                ["target_component"] = targetComp,
                ["type"] = result,
                ["mission_type"] = (byte)0
            }
        ).ToArray();
    }
}

/// <summary>MISSION_SET_CURRENT (msg 41) - tell the vehicle to go to a mission item.</summary>
public static class MissionSetCurrentCommand
{
    public static byte[] Create(byte targetSys, byte targetComp, ushort sequence)
    {
        return Mavlink2Serializer.Build(
            messageId: 41,
            sysId: GcsIdentity.SystemId,
            compId: GcsIdentity.ComponentId,
            fieldValues: new()
            {
                ["target_system"] = targetSys,
                ["target_component"] = targetComp,
                ["seq"] = sequence
            }
        ).ToArray();
    }
}

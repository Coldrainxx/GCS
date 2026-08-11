using GCS.Core.Mavlink.Dispatch;
using MavLinkSharp;
using System;
using System.Diagnostics;

namespace GCS.Core.Mavlink.Messages;

/// <summary>
/// Handles PARAM_VALUE message (ID 22).
/// Uses Frame.Fields for decoding — consistent with all other handlers.
/// </summary>
public sealed class ParamValueHandler : IMavlinkMessageHandler
{
    public uint MessageId => 22;

    private readonly Action<byte, string, float> _onParamValue;

    public ParamValueHandler(Action<byte, string, float> onParamValue)
    {
        _onParamValue = onParamValue;
    }

    public void Handle(Frame frame)
    {
        try
        {
            float raw = Convert.ToSingle(frame.Fields["param_value"]);
            byte paramType = frame.Fields.TryGetValue("param_type", out var t)
                ? Convert.ToByte(t) : MavParamTypeReal32;

            float paramValue = DecodeValue(raw, paramType);
            string paramId = ExtractParamId(frame.Fields["param_id"]);

            Debug.WriteLine($"[ParamValueHandler] {paramId} = {paramValue} (type {paramType})");

            _onParamValue(frame.SystemId, paramId, paramValue);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ParamValueHandler] Error: {ex.Message}");
        }
    }

    // MAV_PARAM_TYPE
    private const byte MavParamTypeUint8 = 1;
    private const byte MavParamTypeInt8 = 2;
    private const byte MavParamTypeUint16 = 3;
    private const byte MavParamTypeInt16 = 4;
    private const byte MavParamTypeUint32 = 5;
    private const byte MavParamTypeInt32 = 6;
    private const byte MavParamTypeReal32 = 9;

    /// <summary>
    /// Recover an integer parameter's real value.
    ///
    /// PARAM_VALUE always carries a float, but for integer parameters PX4 puts the
    /// integer's *bit pattern* into that float rather than converting it. Read
    /// naively, MAV_SYS_ID = 1 arrives as 1.4e-45 — the float whose bits are 1.
    /// ArduPilot stores every parameter as a float and reports REAL32, so it is
    /// unaffected either way.
    /// </summary>
    public static float DecodeValue(float raw, byte paramType)
    {
        switch (paramType)
        {
            case MavParamTypeUint8:
            case MavParamTypeInt8:
            case MavParamTypeUint16:
            case MavParamTypeInt16:
            case MavParamTypeUint32:
            case MavParamTypeInt32:
                break;
            default:
                return raw;   // REAL32 and anything unrecognised pass through
        }

        // Reinterpret the four bytes as the integer they actually are.
        int bits = BitConverter.SingleToInt32Bits(raw);

        return paramType switch
        {
            MavParamTypeUint8 => (byte)bits,
            MavParamTypeInt8 => (sbyte)bits,
            MavParamTypeUint16 => (ushort)bits,
            MavParamTypeInt16 => (short)bits,
            MavParamTypeUint32 => (uint)bits,
            _ => bits,
        };
    }

    private static string ExtractParamId(object field)
    {
        return field switch
        {
            string s => s.TrimEnd('\0'),
            char[] c => new string(c).TrimEnd('\0'),
            byte[] b => System.Text.Encoding.ASCII.GetString(b).TrimEnd('\0'),
            _ => field?.ToString()?.TrimEnd('\0') ?? string.Empty
        };
    }
}
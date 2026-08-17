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
    private readonly Action<byte, string, byte>? _onParamType;

    /// <param name="onParamType">
    /// Told the parameter's declared type. Writing it back needs the same type, so
    /// the only reliable source is the vehicle that reported it.
    /// </param>
    public ParamValueHandler(
        Action<byte, string, float> onParamValue,
        Action<byte, string, byte>? onParamType = null)
    {
        _onParamValue = onParamValue;
        _onParamType = onParamType;
    }

    public void Handle(Frame frame)
    {
        try
        {
            float raw = Convert.ToSingle(frame.Fields["param_value"]);
            byte paramType = frame.Fields.TryGetValue("param_type", out var t)
                ? Convert.ToByte(t) : MavParamValue.Real32;

            float paramValue = MavParamValue.Decode(raw, paramType);
            string paramId = ExtractParamId(frame.Fields["param_id"]);

            Debug.WriteLine($"[ParamValueHandler] {paramId} = {paramValue} (type {paramType})");

            _onParamType?.Invoke(frame.SystemId, paramId, paramType);
            _onParamValue(frame.SystemId, paramId, paramValue);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ParamValueHandler] Error: {ex.Message}");
        }
    }

    /// <inheritdoc cref="MavParamValue.Decode"/>
    public static float DecodeValue(float raw, byte paramType) =>
        MavParamValue.Decode(raw, paramType);

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
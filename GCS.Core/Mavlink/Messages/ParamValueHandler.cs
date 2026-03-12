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

    private readonly Action<string, float> _onParamValue;

    public ParamValueHandler(Action<string, float> onParamValue)
    {
        _onParamValue = onParamValue;
    }

    public void Handle(Frame frame)
    {
        try
        {
            float paramValue = Convert.ToSingle(frame.Fields["param_value"]);
            string paramId = ExtractParamId(frame.Fields["param_id"]);

            Debug.WriteLine($"[ParamValueHandler] {paramId} = {paramValue}");

            _onParamValue(paramId, paramValue);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ParamValueHandler] Error: {ex.Message}");
        }
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
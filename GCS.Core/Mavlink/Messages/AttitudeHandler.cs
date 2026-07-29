using GCS.Core.Domain;
using GCS.Core.Mavlink.Dispatch;
using MavLinkSharp;
using System;
using System.Diagnostics;

namespace GCS.Core.Mavlink.Messages;

/// <summary>
/// Handles ATTITUDE (msg 30) - Roll, pitch, yaw in radians.
/// </summary>
public sealed class AttitudeHandler : IMavlinkMessageHandler
{
    public uint MessageId => 30;

    private readonly Action<byte, AttitudeState> _onAttitude;

    public AttitudeHandler(Action<byte, AttitudeState> onAttitude)
    {
        _onAttitude = onAttitude;
 
    }

    public void Handle(Frame frame)
    {
 
        
        try
        {


            float roll = Convert.ToSingle(frame.Fields["roll"]);
            float pitch = Convert.ToSingle(frame.Fields["pitch"]);
            float yaw = Convert.ToSingle(frame.Fields["yaw"]);

 

            _onAttitude(frame.SystemId, new AttitudeState(
                RollRad: roll,
                PitchRad: pitch,
                YawRad: yaw,
                TimestampUtc: DateTime.UtcNow
            ));
            
   
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AttitudeHandler] ERROR: {ex.Message}");
        }
    }
}
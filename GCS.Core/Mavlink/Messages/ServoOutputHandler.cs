using GCS.Core.Mavlink.Dispatch;
using MavLinkSharp;
using System;

namespace GCS.Core.Mavlink.Messages;

/// <summary>
/// Handles SERVO_OUTPUT_RAW (message ID 36) — the live PWM going out to each
/// servo / motor output.
/// </summary>
public class ServoOutputHandler : IMavlinkMessageHandler
{
    private readonly Action<ServoOutputData> _onServoOutput;

    public ServoOutputHandler(Action<ServoOutputData> onServoOutput)
    {
        _onServoOutput = onServoOutput ?? throw new ArgumentNullException(nameof(onServoOutput));
    }

    public uint MessageId => 36; // SERVO_OUTPUT_RAW

    public void Handle(Frame frame)
    {
        try
        {
            var data = new ServoOutputData
            {
                Servo1Raw = U16(frame, "servo1_raw"),
                Servo2Raw = U16(frame, "servo2_raw"),
                Servo3Raw = U16(frame, "servo3_raw"),
                Servo4Raw = U16(frame, "servo4_raw"),
                Servo5Raw = U16(frame, "servo5_raw"),
                Servo6Raw = U16(frame, "servo6_raw"),
                Servo7Raw = U16(frame, "servo7_raw"),
                Servo8Raw = U16(frame, "servo8_raw"),
                Servo9Raw = U16(frame, "servo9_raw"),
                Servo10Raw = U16(frame, "servo10_raw"),
                Servo11Raw = U16(frame, "servo11_raw"),
                Servo12Raw = U16(frame, "servo12_raw"),
                Servo13Raw = U16(frame, "servo13_raw"),
                Servo14Raw = U16(frame, "servo14_raw"),
                Servo15Raw = U16(frame, "servo15_raw"),
                Servo16Raw = U16(frame, "servo16_raw"),
            };
            _onServoOutput(data);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"SERVO_OUTPUT_RAW parse error: {ex.Message}");
        }
    }

    private static ushort U16(Frame frame, string field)
    {
        if (!frame.Fields.TryGetValue(field, out var value)) return 0;
        return value switch
        {
            byte b => b,
            ushort u => u,
            short s => (ushort)s,
            int i => (ushort)i,
            uint ui => (ushort)ui,
            _ => 0
        };
    }
}

/// <summary>Live servo/motor output PWM from SERVO_OUTPUT_RAW.</summary>
public record ServoOutputData
{
    public ushort Servo1Raw { get; init; }
    public ushort Servo2Raw { get; init; }
    public ushort Servo3Raw { get; init; }
    public ushort Servo4Raw { get; init; }
    public ushort Servo5Raw { get; init; }
    public ushort Servo6Raw { get; init; }
    public ushort Servo7Raw { get; init; }
    public ushort Servo8Raw { get; init; }
    public ushort Servo9Raw { get; init; }
    public ushort Servo10Raw { get; init; }
    public ushort Servo11Raw { get; init; }
    public ushort Servo12Raw { get; init; }
    public ushort Servo13Raw { get; init; }
    public ushort Servo14Raw { get; init; }
    public ushort Servo15Raw { get; init; }
    public ushort Servo16Raw { get; init; }

    public ushort[] ToArray() => new[]
    {
        Servo1Raw, Servo2Raw, Servo3Raw, Servo4Raw,
        Servo5Raw, Servo6Raw, Servo7Raw, Servo8Raw,
        Servo9Raw, Servo10Raw, Servo11Raw, Servo12Raw,
        Servo13Raw, Servo14Raw, Servo15Raw, Servo16Raw
    };
}

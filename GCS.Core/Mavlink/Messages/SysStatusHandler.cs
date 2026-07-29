using GCS.Core.Domain;
using GCS.Core.Mavlink.Dispatch;
using MavLinkSharp;
using System;

namespace GCS.Core.Mavlink.Messages;

public sealed class SysStatusHandler : IMavlinkMessageHandler
{
    public uint MessageId => 1; // SYS_STATUS

    private readonly Action<byte, BatteryState> _onBattery;

    public SysStatusHandler(Action<byte, BatteryState> onBattery)
    {
        _onBattery = onBattery;
    }

    public void Handle(Frame frame)
    {
        // SYS_STATUS has single uint16 voltage_battery (in mV), not an array
        ushort voltageMv = Convert.ToUInt16(frame.Fields["voltage_battery"]);

        // current in centiamps (10 mA units), -1 if unknown
        short currentRaw = Convert.ToInt16(frame.Fields["current_battery"]);

        // remaining in percent, -1 if unknown
        sbyte remaining = Convert.ToSByte(frame.Fields["battery_remaining"]);

        float voltage = voltageMv / 1000f;
        float current = currentRaw >= 0 ? currentRaw / 100f : 0f;

        _onBattery(frame.SystemId,
            new BatteryState(
                VoltageVolts: voltage,
                CurrentAmps: current,
                RemainingPercent: remaining,
                TimestampUtc: DateTime.UtcNow
            )
        );
    }
}
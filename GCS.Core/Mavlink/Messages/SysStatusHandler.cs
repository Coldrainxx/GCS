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
        // SYS_STATUS has single uint16 voltage_battery (in mV), not an array.
        // UINT16_MAX is MAVLink's "not measured" sentinel — a USB-powered board with
        // no pack reports it. Taken literally it becomes 65.54 V, which looks like a
        // real reading and defeats every "is a battery fitted" check downstream.
        ushort voltageMv = Convert.ToUInt16(frame.Fields["voltage_battery"]);
        bool voltageKnown = voltageMv != ushort.MaxValue;

        // current in centiamps (10 mA units), -1 if unknown
        short currentRaw = Convert.ToInt16(frame.Fields["current_battery"]);

        // remaining in percent, -1 if unknown
        sbyte remaining = Convert.ToSByte(frame.Fields["battery_remaining"]);

        float voltage = voltageKnown ? voltageMv / 1000f : 0f;
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
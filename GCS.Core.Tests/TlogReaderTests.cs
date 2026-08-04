using System.Buffers.Binary;
using GCS.Core.Logging;
using Xunit;

namespace GCS.Core.Tests;

public class TlogReaderTests
{
    private static readonly DateTime Epoch = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>A syntactically valid MAVLink v2 frame with the given payload length.</summary>
    private static byte[] Packet(byte payloadLen, byte sysId = 1, bool signed = false)
    {
        int total = 10 + payloadLen + 2 + (signed ? 13 : 0);
        var packet = new byte[total];
        packet[0] = 0xFD;
        packet[1] = payloadLen;
        packet[2] = (byte)(signed ? 0x01 : 0x00);
        packet[5] = sysId;
        return packet;
    }

    private static void Append(MemoryStream stream, DateTime time, byte[] packet)
    {
        var timestamp = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(timestamp, (ulong)((time - Epoch).Ticks / 10));
        stream.Write(timestamp);
        stream.Write(packet);
    }

    [Fact]
    public void ReadsTimestampAndPacketBackOut()
    {
        var time = new DateTime(2026, 8, 3, 12, 0, 0, DateTimeKind.Utc);
        using var stream = new MemoryStream();
        Append(stream, time, Packet(5));
        Append(stream, time.AddSeconds(1), Packet(9));
        stream.Position = 0;

        var records = TlogReader.Read(stream).ToList();

        Assert.Equal(2, records.Count);
        Assert.Equal(time, records[0].TimestampUtc);
        Assert.Equal(17, records[0].Packet.Length);            // 10 + 5 + 2
        Assert.Equal(time.AddSeconds(1), records[1].TimestampUtc);
        Assert.Equal(21, records[1].Packet.Length);            // 10 + 9 + 2
    }

    [Fact]
    public void SignedFramesIncludeTheirSignature()
    {
        using var stream = new MemoryStream();
        Append(stream, Epoch.AddYears(56), Packet(4, signed: true));
        stream.Position = 0;

        var records = TlogReader.Read(stream).ToList();

        Assert.Single(records);
        Assert.Equal(29, records[0].Packet.Length);            // 10 + 4 + 2 + 13
    }

    [Fact]
    public void ATruncatedTailIsTreatedAsEndOfFileNotAnError()
    {
        // Recording stops mid-packet whenever the app is killed.
        var time = new DateTime(2026, 8, 3, 12, 0, 0, DateTimeKind.Utc);
        using var stream = new MemoryStream();
        Append(stream, time, Packet(5));

        var partial = Packet(20);
        stream.Write(new byte[8]);                              // timestamp
        stream.Write(partial, 0, 6);                            // half a packet
        stream.Position = 0;

        var records = TlogReader.Read(stream).ToList();

        Assert.Single(records);                                 // the complete one
    }

    [Fact]
    public void EmptyFileYieldsNothing()
    {
        using var stream = new MemoryStream();
        Assert.Empty(TlogReader.Read(stream));
    }

    [Fact]
    public void ResynchronisesAfterAStrayByte()
    {
        var time = new DateTime(2026, 8, 3, 12, 0, 0, DateTimeKind.Utc);
        using var stream = new MemoryStream();

        var timestamp = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(timestamp, (ulong)((time - Epoch).Ticks / 10));
        stream.Write(timestamp);
        stream.WriteByte(0x00);                                 // garbage before the start marker
        stream.Write(Packet(3));
        stream.Position = 0;

        var records = TlogReader.Read(stream).ToList();

        Assert.Single(records);
        Assert.Equal(15, records[0].Packet.Length);
    }

    [Fact]
    public void LogGroundingNamesWhatWasNotRecorded()
    {
        // Same discipline as the live snapshot: absent data must be stated, not
        // omitted, or the model fills the gap.
        var log = new GCS.Core.Logging.FlightLogSummary { FilePath = "test.tlog" };
        log.Notes.Add("A .tlog records only what was received over the telemetry link.");

        string snapshot = GCS.Core.Advisor.Ai.GroundingBuilder.BuildLogSnapshot(log);

        Assert.Contains("Battery: NOT RECORDED", snapshot);
        Assert.Contains("GPS: NOT RECORDED", snapshot);
        Assert.Contains("Never armed", snapshot);
        Assert.Contains("Limitations of this log", snapshot);
    }

    [Fact]
    public void LogAnswersDescribeTheLogNotTheLiveAircraft()
    {
        var log = new GCS.Core.Logging.FlightLogSummary
        {
            FilePath = "flight.tlog",
            HasBattery = true,
            BatteryStartVolts = 25.2f,
            BatteryEndVolts = 21.8f,
            BatteryMinVolts = 21.5f,
        };

        string reply = GCS.Core.Advisor.AssistantResponder.RespondAboutLog(
            GCS.Core.Advisor.AssistantIntent.Battery, log);

        Assert.Contains("25.2", reply);
        Assert.Contains("21.8", reply);
    }

    [Fact]
    public void ALogWithoutBatteryDataSaysSoRatherThanReportingZero()
    {
        var log = new GCS.Core.Logging.FlightLogSummary { FilePath = "flight.tlog" };

        string reply = GCS.Core.Advisor.AssistantResponder.RespondAboutLog(
            GCS.Core.Advisor.AssistantIntent.Battery, log);

        Assert.Contains("No battery telemetry", reply);
        Assert.DoesNotContain("0.00 V", reply);
    }

    [Fact]
    public void AbsurdTimestampsAreFlaggedRatherThanThrowing()
    {
        using var stream = new MemoryStream();
        var timestamp = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(timestamp, ulong.MaxValue);
        stream.Write(timestamp);
        stream.Write(Packet(3));
        stream.Position = 0;

        var records = TlogReader.Read(stream).ToList();

        Assert.Single(records);
        Assert.Equal(DateTime.MinValue, records[0].TimestampUtc);
    }
}

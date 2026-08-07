using GCS.Core.Mavlink;
using Xunit;

namespace GCS.Core.Tests;

public class MavlinkV1FramingTests
{
    private static byte[] V1Frame(byte payloadLen)
    {
        // STX, LEN, SEQ, SYSID, COMPID, MSGID, payload, CRC(2)
        var frame = new byte[6 + payloadLen + 2];
        frame[0] = 0xFE;
        frame[1] = payloadLen;
        frame[3] = 1;      // sysid
        return frame;
    }

    private static byte[] V2Frame(byte payloadLen)
    {
        var frame = new byte[10 + payloadLen + 2];
        frame[0] = 0xFD;
        frame[1] = payloadLen;
        frame[5] = 1;
        return frame;
    }

    [Fact]
    public void AMavlink1FrameIsExtracted()
    {
        // The bug this fixes: a vehicle with SERIALn_PROTOCOL = 1 sends 0xFE frames,
        // which were discarded wholesale — the link looked completely dead.
        var buffer = new MavlinkFrameBuffer();

        var frames = buffer.AddData(V1Frame(9)).ToList();

        Assert.Single(frames);
        Assert.Equal(17, frames[0].Length);      // 6 + 9 + 2
        Assert.Equal(0xFE, frames[0].Span[0]);
    }

    [Fact]
    public void Mavlink1AndMavlink2FramesCanShareAStream()
    {
        // A radio bridging two vehicles can genuinely mix versions.
        var buffer = new MavlinkFrameBuffer();

        var mixed = V1Frame(4).Concat(V2Frame(4)).Concat(V1Frame(2)).ToArray();
        var frames = buffer.AddData(mixed).ToList();

        Assert.Equal(3, frames.Count);
        Assert.Equal(0xFE, frames[0].Span[0]);
        Assert.Equal(0xFD, frames[1].Span[0]);
        Assert.Equal(0xFE, frames[2].Span[0]);
    }

    [Fact]
    public void AFragmentedMavlink1FrameIsReassembled()
    {
        var buffer = new MavlinkFrameBuffer();
        var frame = V1Frame(12);

        Assert.Empty(buffer.AddData(frame.AsSpan(0, 5).ToArray()));

        var frames = buffer.AddData(frame.AsSpan(5).ToArray()).ToList();

        Assert.Single(frames);
        Assert.Equal(frame.Length, frames[0].Length);
    }

    [Fact]
    public void GarbageBeforeAMavlink1FrameIsSkipped()
    {
        var buffer = new MavlinkFrameBuffer();
        var data = new byte[] { 0x11, 0x22, 0x33 }.Concat(V1Frame(3)).ToArray();

        var frames = buffer.AddData(data).ToList();

        Assert.Single(frames);
        Assert.Equal(0xFE, frames[0].Span[0]);
    }
}

public class VehicleModeMappingTests
{
    [Theory]
    [InlineData(1, VehicleKind.Plane)]        // FIXED_WING
    [InlineData(2, VehicleKind.Copter)]       // QUADROTOR
    [InlineData(13, VehicleKind.Copter)]      // HEXAROTOR
    [InlineData(10, VehicleKind.Rover)]       // GROUND_ROVER
    [InlineData(20, VehicleKind.Plane)]       // VTOL — runs ArduPlane
    public void VehicleFamilyComesFromMavType(byte mavType, VehicleKind expected) =>
        Assert.Equal(expected, ArdupilotFlightModes.KindFromMavType(mavType));

    [Theory]
    [InlineData(2, "ALT_HOLD")]
    [InlineData(5, "LOITER")]
    [InlineData(6, "RTL")]
    [InlineData(16, "POSHOLD")]
    public void CopterModesUseTheCopterTable(uint mode, string expected) =>
        Assert.Equal(expected, ArdupilotFlightModes.Describe(VehicleKind.Copter, mode));

    [Fact]
    public void TheSameNumberMeansDifferentThingsPerVehicle()
    {
        // Mode 5 is Loiter on a Copter and FBWA on a Plane. Decoding a Copter
        // through the plane table produced confident, wrong mode names.
        Assert.Equal("LOITER", ArdupilotFlightModes.Describe(VehicleKind.Copter, 5));
        Assert.Equal("FBWA", ArdupilotFlightModes.Describe(VehicleKind.Plane, 5));
    }

    [Fact]
    public void AnUnknownModeShowsItsNumberRatherThanGuessing() =>
        Assert.Equal("MODE 99", ArdupilotFlightModes.Describe(VehicleKind.Copter, 99));

    [Fact]
    public void PlaneTypedModeIsNullForNonPlaneVehicles()
    {
        // The plane enum cannot represent a Copter mode, so screens built around it
        // get null instead of a wrong value.
        Assert.Null(ArdupilotFlightModes.PlaneMode(VehicleKind.Copter, 5));
        Assert.NotNull(ArdupilotFlightModes.PlaneMode(VehicleKind.Plane, 5));
    }

    [Theory]
    [InlineData(VehicleKind.Copter, "RTL", 6u)]
    [InlineData(VehicleKind.Copter, "LOITER", 5u)]
    [InlineData(VehicleKind.Copter, "ALT_HOLD", 2u)]
    [InlineData(VehicleKind.Plane, "RTL", 11u)]
    [InlineData(VehicleKind.Plane, "QHOVER", 18u)]
    public void EncodingIsAlsoVehicleAware(VehicleKind kind, string name, uint expected) =>
        Assert.Equal(expected, ArdupilotFlightModes.ToCustomMode(kind, name));

    [Fact]
    public void AskingACopterForRtlDoesNotSendThePlaneNumber()
    {
        // Plane RTL is 11; copter 11 is DRIFT. Sending the plane number would put
        // the aircraft into a completely different mode.
        Assert.Equal(6u, ArdupilotFlightModes.ToCustomMode(VehicleKind.Copter, "RTL"));
        Assert.Equal(11u, ArdupilotFlightModes.ToCustomMode(VehicleKind.Plane, "RTL"));
    }

    [Fact]
    public void AModeTheVehicleDoesNotHaveIsRefusedRatherThanGuessed()
    {
        // QHOVER is a QuadPlane mode; a Copter has no equivalent number.
        Assert.Null(ArdupilotFlightModes.ToCustomMode(VehicleKind.Copter, "QHOVER"));
    }

    [Fact]
    public void ModeNamesMatchRegardlessOfSeparators() =>
        Assert.Equal(2u, ArdupilotFlightModes.ToCustomMode(VehicleKind.Copter, "alt hold".Replace(" ", "_")));

    [Fact]
    public void EveryOfferedModeCanBeEncodedBack()
    {
        // A mode shown in the picker that cannot be sent would fail silently.
        foreach (var kind in new[] { VehicleKind.Plane, VehicleKind.Copter, VehicleKind.Rover })
            foreach (var (name, mode) in ArdupilotFlightModes.ModesFor(kind))
                Assert.Equal(mode, ArdupilotFlightModes.ToCustomMode(kind, name));
    }

    [Fact]
    public void ALogFromACopterDecodesModesWithTheCopterTable()
    {
        // Replay used the plane enum, so a copter log showed plane mode names for
        // the whole flight.
        Assert.Equal("LOITER", ArdupilotFlightModes.Describe(VehicleKind.Copter, 5));
        Assert.Null(ArdupilotFlightModes.PlaneMode(VehicleKind.Copter, 5));
    }

    [Fact]
    public void QuadPlaneModesStillResolveThroughThePlaneTable()
    {
        // Regression guard for the existing airframe: 17 is QSTABILIZE.
        Assert.Equal("QSTABILIZE", ArdupilotFlightModes.Describe(VehicleKind.Plane, 17));
    }
}

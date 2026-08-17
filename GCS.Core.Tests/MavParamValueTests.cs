using GCS.Core.Mavlink;
using Xunit;

namespace GCS.Core.Tests;

/// <summary>
/// PX4 puts an integer parameter's bit pattern in the float field and rejects a
/// write whose declared type does not match — "param types mismatch param:
/// FLW_TGT_ALT_M", with the write silently never applied. ArduPilot reports
/// everything as REAL32 and is unaffected either way.
/// </summary>
public class MavParamValueTests
{
    [Theory]
    [InlineData(MavParamValue.Int32, true)]
    [InlineData(MavParamValue.Uint32, true)]
    [InlineData(MavParamValue.Int16, true)]
    [InlineData(MavParamValue.Uint16, true)]
    [InlineData(MavParamValue.Int8, true)]
    [InlineData(MavParamValue.Uint8, true)]
    [InlineData(MavParamValue.Real32, false)]
    [InlineData((byte)10, false)]      // REAL64 — not something we encode
    [InlineData((byte)0, false)]       // unset
    public void OnlyIntegerTypesGetTheBitwiseTreatment(byte paramType, bool expected)
    {
        Assert.Equal(expected, MavParamValue.IsInteger(paramType));
    }

    [Theory]
    [InlineData(MavParamValue.Int32, 0f)]
    [InlineData(MavParamValue.Int32, 1f)]
    [InlineData(MavParamValue.Int32, 2f)]
    [InlineData(MavParamValue.Int32, -1f)]
    [InlineData(MavParamValue.Int32, 250f)]
    [InlineData(MavParamValue.Uint8, 3f)]
    [InlineData(MavParamValue.Int16, -300f)]
    [InlineData(MavParamValue.Uint32, 100000f)]
    public void AnIntegerSurvivesTheRoundTrip(byte paramType, float value)
    {
        float wire = MavParamValue.Encode(value, paramType);

        Assert.Equal(value, MavParamValue.Decode(wire, paramType));
    }

    [Fact]
    public void AFloatParameterIsLeftAlone()
    {
        // Touching a REAL32 would corrupt every ArduPilot parameter we write.
        Assert.Equal(12.75f, MavParamValue.Encode(12.75f, MavParamValue.Real32));
        Assert.Equal(12.75f, MavParamValue.Decode(12.75f, MavParamValue.Real32));
    }

    /// <summary>The failure that started this: MAV_SYS_ID = 1 read as 1.4e-45.</summary>
    [Fact]
    public void TheBitPatternIsWhatPx4ActuallyPutsOnTheWire()
    {
        float wire = MavParamValue.Encode(1f, MavParamValue.Int32);

        Assert.Equal(1, BitConverter.SingleToInt32Bits(wire));
        Assert.Equal(float.Epsilon, wire);          // 1.4e-45, the float whose bits are 1
        Assert.Equal(1f, MavParamValue.Decode(wire, MavParamValue.Int32));
    }

    [Fact]
    public void AValueThatIsNotQuiteAnIntegerRoundsRatherThanTruncating()
    {
        // A UI bound to a float can hand us 1.9999997 for what the user typed as 2.
        float wire = MavParamValue.Encode(1.9999997f, MavParamValue.Int32);

        Assert.Equal(2f, MavParamValue.Decode(wire, MavParamValue.Int32));
    }

    [Fact]
    public void TheTypeTheVehicleReportedAlwaysWins()
    {
        // Even for a name in the fallback table: the firmware is the authority.
        Assert.Equal(MavParamValue.Real32,
                     MavParamValue.TypeForWrite("FLW_TGT_ALT_M", learned: MavParamValue.Real32));

        Assert.Equal(MavParamValue.Int32,
                     MavParamValue.TypeForWrite("SOME_OTHER_PARAM", learned: MavParamValue.Int32));
    }

    /// <summary>
    /// Applying a formation writes FLW_TGT_ALT_M without reading it first, so
    /// nothing has taught us its type by then.
    /// </summary>
    [Fact]
    public void ParametersWrittenBlindStillGetTheRightType()
    {
        Assert.Equal(MavParamValue.Int32, MavParamValue.TypeForWrite("FLW_TGT_ALT_M", learned: null));
        Assert.Equal(MavParamValue.Int32, MavParamValue.TypeForWrite("NAV_RCL_ACT", learned: null));
        Assert.Equal(MavParamValue.Int32, MavParamValue.TypeForWrite("NAV_DLL_ACT", learned: null));
    }

    [Fact]
    public void AnythingUnknownIsWrittenAsAFloat()
    {
        // Correct for ArduPilot, which reports every parameter as REAL32, and for
        // every PX4 float.
        Assert.Equal(MavParamValue.Real32, MavParamValue.TypeForWrite("FOLL_OFS_X", learned: null));
        Assert.Equal(MavParamValue.Real32, MavParamValue.TypeForWrite("FLW_TGT_DST", learned: null));
    }
}

using GCS.Core.Mavlink;
using Xunit;

namespace GCS.Core.Tests;

public class FailsafeParameterTests
{
    [Fact]
    public void PlaneAndCopterUseDifferentNamesForTheSameSettings()
    {
        // Writing plane names to a copter does not fail loudly — it configures
        // nothing while the screen looks like it worked.
        var plane = FailsafeParameterSet.For(VehicleKind.Plane);
        var copter = FailsafeParameterSet.For(VehicleKind.Copter);

        Assert.Equal("THR_FAILSAFE", plane.RadioEnable);
        Assert.Equal("FS_THR_ENABLE", copter.RadioEnable);

        Assert.Equal("THR_FS_VALUE", plane.RadioPwm);
        Assert.Equal("FS_THR_VALUE", copter.RadioPwm);

        Assert.Equal("FS_GCS_ENABL", plane.GcsEnable);
        Assert.Equal("FS_GCS_ENABLE", copter.GcsEnable);
    }

    [Fact]
    public void OnlyPlaneHasTheShortAndLongActionPair()
    {
        Assert.True(FailsafeParameterSet.For(VehicleKind.Plane).HasShortLongActions);
        Assert.False(FailsafeParameterSet.For(VehicleKind.Copter).HasShortLongActions);
    }

    [Fact]
    public void AnUnknownVehicleKeepsThePlaneNames()
    {
        // The app's own airframe, and the safest default before a heartbeat arrives.
        Assert.Equal("THR_FAILSAFE", FailsafeParameterSet.For(VehicleKind.Unknown).RadioEnable);
    }

    [Fact]
    public void RequestedNamesCoverBatteryAndVehicleSpecificSettings()
    {
        var names = FailsafeParameterSet.For(VehicleKind.Copter).AllNames().ToList();

        Assert.Contains("BATT_LOW_VOLT", names);
        Assert.Contains("FS_THR_ENABLE", names);
        Assert.DoesNotContain("FS_SHORT_ACTN", names);   // not a copter parameter
    }

    [Theory]
    [InlineData("THR_FAILSAFE")]
    [InlineData("FS_THR_ENABLE")]
    [InlineData("fs_thr_enable")]
    public void EitherSpellingIsRecognisedOnReceive(string name) =>
        Assert.True(FailsafeParameterSet.IsRadioEnable(name));

    [Fact]
    public void GcsSpellingsBothMatch()
    {
        // ArduPilot renamed this; both spellings appear in the wild.
        Assert.True(FailsafeParameterSet.IsGcsEnable("FS_GCS_ENABL"));
        Assert.True(FailsafeParameterSet.IsGcsEnable("FS_GCS_ENABLE"));
    }

    [Fact]
    public void Px4UsesItsOwnFailsafeParametersEntirely()
    {
        var px4 = FailsafeParameterSet.For(AutopilotKind.Px4, VehicleKind.Copter);

        Assert.Equal("NAV_RCL_ACT", px4.RadioEnable);
        Assert.Equal("NAV_DLL_ACT", px4.GcsEnable);
        Assert.False(px4.HasShortLongActions);

        // Battery is a remaining fraction on PX4, not volts and mAh.
        Assert.Contains("BAT_LOW_THR", px4.AllNames());
        Assert.DoesNotContain("BATT_LOW_VOLT", px4.AllNames());
    }

    [Fact]
    public void TheThresholdFieldIsLabelledForTheFirmware()
    {
        // ArduPilot's field is a PWM level; PX4's is a timeout in seconds. Showing
        // "FS PWM" above a seconds value would invite entering 1500.
        Assert.Equal("FS PWM", FailsafeParameterSet.For(AutopilotKind.ArduPilot, VehicleKind.Plane).RadioThresholdLabel);
        Assert.Contains("timeout", FailsafeParameterSet.For(AutopilotKind.Px4, VehicleKind.Copter).RadioThresholdLabel);
    }

    [Fact]
    public void Px4NamesAreRecognisedOnReceive()
    {
        Assert.True(FailsafeParameterSet.IsRadioEnable("NAV_RCL_ACT"));
        Assert.True(FailsafeParameterSet.IsGcsEnable("NAV_DLL_ACT"));
        Assert.True(FailsafeParameterSet.IsRadioPwm("COM_RC_LOSS_T"));
    }

    [Fact]
    public void ArduPilotSetIsUnchangedByTheAutopilotAxis()
    {
        // The single-argument overload is what existing callers use; it must keep
        // meaning ArduPilot.
        Assert.Equal(FailsafeParameterSet.For(AutopilotKind.ArduPilot, VehicleKind.Plane),
                     FailsafeParameterSet.For(VehicleKind.Plane));
    }

    [Fact]
    public void UnrelatedParametersAreNotMisread()
    {
        Assert.False(FailsafeParameterSet.IsRadioEnable("BATT_LOW_VOLT"));
        Assert.False(FailsafeParameterSet.IsGcsEnable("FS_EKF_ACTION"));
    }
}

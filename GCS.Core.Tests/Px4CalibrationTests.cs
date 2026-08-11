using GCS.Core.Mavlink;
using Xunit;

namespace GCS.Core.Tests;

public class Px4CalibrationTests
{
    private static Px4CalibrationState Feed(params string[] messages)
    {
        var state = Px4CalibrationState.Idle;
        foreach (var m in messages) state = Px4CalibrationParser.Apply(state, m);
        return state;
    }

    [Fact]
    public void OnlyCalibrationMessagesAreConsumed()
    {
        Assert.True(Px4CalibrationParser.IsCalibrationMessage("[cal] progress 40"));
        Assert.False(Px4CalibrationParser.IsCalibrationMessage("Preflight check failed"));
        Assert.False(Px4CalibrationParser.IsCalibrationMessage(null));
    }

    [Fact]
    public void UnrelatedStatusTextDoesNotDisturbACalibrationInProgress()
    {
        // A GPS or prearm message arriving mid-calibration must not reset it.
        var state = Feed(
            "[cal] calibration started: 2 accel",
            "[cal] pending: down front left right up back",
            "EKF2 IMU0 tilt alignment complete");

        Assert.Equal(Px4CalibrationPhase.AwaitingSide, state.Phase);
        Assert.Equal(6, state.Pending.Count);
    }

    [Fact]
    public void AFullAccelCalibrationRunsThroughToDone()
    {
        var state = Feed(
            "[cal] calibration started: 2 accel",
            "[cal] pending: down front left right up back",
            "[cal] down orientation detected",
            "[cal] progress 17",
            "[cal] down side done, rotate to a pending side",
            "[cal] pending: front left right up back",
            "[cal] front orientation detected",
            "[cal] progress 34",
            "[cal] calibration done: accel");

        Assert.Equal(Px4CalibrationPhase.Done, state.Phase);
        Assert.Equal("accel", state.Sensor);
        Assert.Equal(100, state.ProgressPercent);
        Assert.Empty(state.Pending);
        Assert.Contains("complete", state.Instruction);
    }

    [Fact]
    public void PendingSidesAreListedForTheOperator()
    {
        var state = Feed(
            "[cal] calibration started: 2 accel",
            "[cal] pending: down front left right up back");

        Assert.Equal(new[] { "down", "front", "left", "right", "up", "back" }, state.Pending);
        Assert.Contains("Rotate the vehicle to", state.Instruction);
        Assert.Contains("down", state.Instruction);
    }

    [Fact]
    public void DetectingASideSwitchesToHoldStill()
    {
        var state = Feed(
            "[cal] calibration started: 2 accel",
            "[cal] pending: down front left right up back",
            "[cal] down orientation detected",
            "[cal] progress 25");

        Assert.Equal(Px4CalibrationPhase.Measuring, state.Phase);
        Assert.Equal("down", state.CurrentSide);
        Assert.Equal(25, state.ProgressPercent);
        Assert.Contains("Hold still", state.Instruction);
    }

    [Fact]
    public void FinishingASideReturnsToWaiting()
    {
        var state = Feed(
            "[cal] calibration started: 2 accel",
            "[cal] down orientation detected",
            "[cal] down side done, rotate to a pending side");

        Assert.Equal(Px4CalibrationPhase.AwaitingSide, state.Phase);
        Assert.Null(state.CurrentSide);
    }

    [Fact]
    public void FailureIsReportedWithItsReason()
    {
        var state = Feed(
            "[cal] calibration started: 2 accel",
            "[cal] calibration failed: timeout waiting for orientation");

        Assert.Equal(Px4CalibrationPhase.Failed, state.Phase);
        Assert.False(state.IsRunning);
        Assert.Contains("timeout", state.Instruction);
    }

    [Fact]
    public void AnAbortIsTreatedAsFailureRatherThanLeftRunning()
    {
        var state = Feed(
            "[cal] calibration started: 2 mag",
            "[cal] calibration aborted");

        Assert.Equal(Px4CalibrationPhase.Failed, state.Phase);
    }

    [Fact]
    public void ProgressWithoutDigitsIsIgnoredRatherThanZeroing()
    {
        var state = Feed(
            "[cal] calibration started: 2 accel",
            "[cal] down orientation detected",
            "[cal] progress 60",
            "[cal] progress");

        Assert.Equal(60, state.ProgressPercent);
    }

    [Fact]
    public void MagnetometerCalibrationIsRecognisedToo()
    {
        var state = Feed("[cal] calibration started: 2 mag");

        Assert.Equal("mag", state.Sensor);
        Assert.True(state.IsRunning);
    }

    [Fact]
    public void CommandParametersMatchPx4sExpectedSlots()
    {
        // Accelerometer is param5 = 1; level horizon is the same slot set to 2.
        Assert.Equal(1f, Px4CalibrationCommands.Accelerometer.P5);
        Assert.Equal(2f, Px4CalibrationCommands.LevelHorizon.P5);
        Assert.Equal(1f, Px4CalibrationCommands.Magnetometer.P2);
        Assert.Equal(1f, Px4CalibrationCommands.Gyro.P1);

        // Cancel is all zeroes.
        var cancel = Px4CalibrationCommands.Cancel;
        Assert.Equal(0f, cancel.P1 + cancel.P2 + cancel.P3 + cancel.P4 + cancel.P5 + cancel.P6 + cancel.P7);
    }
}

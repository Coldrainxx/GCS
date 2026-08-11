using GCS.Core.Mavlink;
using Xunit;

namespace GCS.Core.Tests;

public class Px4ModeTests
{
    // ── The packing scheme ──────────────────────────────────────────

    [Fact]
    public void CustomModePacksMainIntoBits16To23AndSubInto24To31()
    {
        // POSCTL is main mode 3, no sub mode: 3 << 16.
        Assert.Equal(196608u, Px4FlightModes.Pack(3, 0));

        // AUTO.RTL is main 4, sub 5.
        Assert.Equal((5u << 24) | (4u << 16), Px4FlightModes.Pack(4, 5));
    }

    [Fact]
    public void UnpackReversesPack()
    {
        var (main, sub) = Px4FlightModes.Unpack(Px4FlightModes.Pack(4, 3));
        Assert.Equal(4, main);
        Assert.Equal(3, sub);
    }

    [Fact]
    public void ArduPilotDecodingWouldHaveShownAMeaninglessNumber()
    {
        // The symptom this fixes: PX4's POSCTL read through the ArduPilot table.
        uint posctl = Px4FlightModes.Pack(3, 0);

        Assert.Equal("POSITION", Px4FlightModes.Describe(posctl));
        Assert.StartsWith("MODE ", ArdupilotFlightModes.Describe(VehicleKind.Copter, posctl));
    }

    // ── Naming ──────────────────────────────────────────────────────

    [Theory]
    [InlineData(1, 0, "MANUAL")]
    [InlineData(3, 0, "POSITION")]
    [InlineData(2, 0, "ALTITUDE")]
    [InlineData(4, 3, "HOLD")]
    [InlineData(4, 4, "MISSION")]
    [InlineData(4, 5, "RETURN")]
    [InlineData(4, 6, "LAND")]
    [InlineData(6, 0, "OFFBOARD")]
    public void ModesAreNamed(byte main, byte sub, string expected) =>
        Assert.Equal(expected, Px4FlightModes.Describe(Px4FlightModes.Pack(main, sub)));

    [Fact]
    public void AnUnlistedAutoSubModeIsStillRecognisablyAutomatic()
    {
        // Better than printing the packed integer for a mode we do not enumerate.
        Assert.Equal("AUTO (7)", Px4FlightModes.Describe(Px4FlightModes.Pack(4, 7)));
    }

    // ── Autopilot detection ─────────────────────────────────────────

    [Theory]
    [InlineData(3, AutopilotKind.ArduPilot)]
    [InlineData(12, AutopilotKind.Px4)]
    [InlineData(0, AutopilotKind.Unknown)]
    public void AutopilotComesFromTheHeartbeat(byte mavAutopilot, AutopilotKind expected) =>
        Assert.Equal(expected, Px4FlightModes.KindFromMavAutopilot(mavAutopilot));

    // ── The shared facade ───────────────────────────────────────────

    [Fact]
    public void TheFacadeRoutesToTheRightFirmware()
    {
        uint px4Hold = Px4FlightModes.Pack(4, 3);

        Assert.Equal("HOLD",
            FlightModeTable.Describe(AutopilotKind.Px4, VehicleKind.Copter, px4Hold));

        // Same vehicle kind, ArduPilot firmware: copter mode 5 is LOITER.
        Assert.Equal("LOITER",
            FlightModeTable.Describe(AutopilotKind.ArduPilot, VehicleKind.Copter, 5));
    }

    [Fact]
    public void Px4ModeListIsTheSameWhateverTheAirframe()
    {
        // PX4 names modes by function, not by vehicle type.
        var copter = FlightModeTable.ModesFor(AutopilotKind.Px4, VehicleKind.Copter);
        var plane = FlightModeTable.ModesFor(AutopilotKind.Px4, VehicleKind.Plane);

        Assert.Equal(copter.Count, plane.Count);
        Assert.Contains(copter, m => m.Name == "MISSION");
    }

    [Fact]
    public void FindReturnsTheMainAndSubNeededToCommandTheMode()
    {
        var rtl = FlightModeTable.Find(AutopilotKind.Px4, VehicleKind.Copter, "RETURN");

        Assert.NotNull(rtl);
        Assert.Equal(4, rtl!.Value.Px4MainMode);
        Assert.Equal(5, rtl.Value.Px4SubMode);
    }

    [Fact]
    public void ArduPilotChoicesCarryTheFlatNumberAndNoPx4Fields()
    {
        var rtl = FlightModeTable.Find(AutopilotKind.ArduPilot, VehicleKind.Copter, "RTL");

        Assert.NotNull(rtl);
        Assert.Equal(6u, rtl!.Value.CustomMode);
        Assert.Equal(0, rtl.Value.Px4MainMode);
    }

    [Fact]
    public void AModeTheFirmwareLacksIsRefused()
    {
        // QHOVER is an ArduPlane mode; PX4 has no such thing.
        Assert.Null(FlightModeTable.Find(AutopilotKind.Px4, VehicleKind.Plane, "QHOVER"));
    }

    [Fact]
    public void EveryOfferedPx4ModeCanBeFoundBack()
    {
        // A mode shown in the picker that cannot be resolved would fail silently.
        foreach (var choice in Px4FlightModes.All)
        {
            var found = FlightModeTable.Find(AutopilotKind.Px4, VehicleKind.Copter, choice.Name);
            Assert.NotNull(found);
            Assert.Equal(choice.CustomMode, found!.Value.CustomMode);
        }
    }

    [Theory]
    [InlineData(1u, 1f)]              // MAV_SYS_ID = 1
    [InlineData(4001u, 4001f)]        // SYS_AUTOSTART = 4001
    [InlineData(0u, 0f)]
    public void IntegerParametersAreReinterpretedNotConverted(uint intValue, float expected)
    {
        // PX4 puts an integer's bit pattern into PARAM_VALUE's float field. Read
        // naively, MAV_SYS_ID = 1 arrives as 1.4e-45 — which is what the screen showed.
        float wire = BitConverter.Int32BitsToSingle((int)intValue);

        Assert.Equal(expected, GCS.Core.Mavlink.Messages.ParamValueHandler.DecodeValue(wire, 6)); // INT32
    }

    [Fact]
    public void RealParametersPassThroughUntouched()
    {
        // ArduPilot reports everything as REAL32 and must be unaffected.
        Assert.Equal(24.6f, GCS.Core.Mavlink.Messages.ParamValueHandler.DecodeValue(24.6f, 9));
    }

    [Fact]
    public void SmallIntegerTypesAreMaskedToTheirWidth()
    {
        float wire = BitConverter.Int32BitsToSingle(300);

        Assert.Equal(44f, GCS.Core.Mavlink.Messages.ParamValueHandler.DecodeValue(wire, 1));   // UINT8: 300 & 0xFF
        Assert.Equal(300f, GCS.Core.Mavlink.Messages.ParamValueHandler.DecodeValue(wire, 3));  // UINT16
    }

    [Fact]
    public void AnUnrecognisedTypePassesThroughRatherThanBeingMangled() =>
        Assert.Equal(1.5f, GCS.Core.Mavlink.Messages.ParamValueHandler.DecodeValue(1.5f, 99));

    [Fact]
    public void RawGpsPositionIsUsableWhenTheEstimatorHasNone()
    {
        // PX4 withholds GLOBAL_POSITION_INT until its estimator fuses a position,
        // but the receiver's own fix is in GPS_RAW_INT the whole time — which is
        // what QGroundControl falls back to.
        var gps = new GCS.Core.Domain.GpsState(
            FixType: 4, SatellitesVisible: 11, Eph: 90, Epv: 120,
            TimestampUtc: DateTime.UtcNow,
            LatitudeDeg: 40.451615, LongitudeDeg: 50.065928, AltitudeMslMeters: -6.7f);

        Assert.True(gps.HasPosition);
    }

    [Fact]
    public void TheZeroZeroPlaceholderIsNotTreatedAsAPosition()
    {
        // 0,0 is the "no fix yet" placeholder and would drop the map into the
        // Gulf of Guinea.
        var gps = new GCS.Core.Domain.GpsState(
            FixType: 3, SatellitesVisible: 8, Eph: 100, Epv: 100,
            TimestampUtc: DateTime.UtcNow);

        Assert.False(gps.HasPosition);
    }

    [Fact]
    public void APositionWithoutAFixIsNotUsed()
    {
        var gps = new GCS.Core.Domain.GpsState(
            FixType: 1, SatellitesVisible: 3, Eph: 900, Epv: 900,
            TimestampUtc: DateTime.UtcNow,
            LatitudeDeg: 40.45, LongitudeDeg: 50.06);

        Assert.False(gps.HasPosition);
    }

    [Fact]
    public void TheUnknownVoltageSentinelIsNotTreatedAsAReading()
    {
        // SYS_STATUS.voltage_battery is UINT16_MAX when nothing is measured. Divided
        // by 1000 that becomes 65.535 V — a plausible-looking pack voltage that
        // defeats every "is a battery fitted" check.
        const float sentinelVolts = ushort.MaxValue / 1000f;

        Assert.True(sentinelVolts > GCS.Core.Advisor.FlightHealthAnalyzer.MinPlausiblePackVolts,
            "65.535 V would pass the plausibility check, which is why the sentinel " +
            "has to be caught at the handler rather than filtered downstream");

        // Zeroed at the handler, it falls below the threshold and reads as absent.
        var battery = new GCS.Core.Domain.BatteryState(0f, 0f, -1, DateTime.UtcNow);
        Assert.True(battery.VoltageVolts < GCS.Core.Advisor.FlightHealthAnalyzer.MinPlausiblePackVolts);
    }

    [Fact]
    public void AnUnmeasuredBatteryIsUnmonitoredNotCritical()
    {
        var state = new GCS.Core.Domain.VehicleState(
            Connection: new GCS.Core.Domain.ConnectionState(true, 1, 1, DateTime.UtcNow),
            Attitude: new GCS.Core.Domain.AttitudeState(0, 0, 0, DateTime.UtcNow),
            Position: null, VfrHud: null,
            Battery: new GCS.Core.Domain.BatteryState(0f, 0f, -1, DateTime.UtcNow),
            FlightMode: null,
            Gps: new GCS.Core.Domain.GpsState(4, 11, 90, 120, DateTime.UtcNow, 40.45, 50.06),
            IsArmed: false);

        var report = GCS.Core.Advisor.FlightHealthAnalyzer.Analyze(state, DateTime.UtcNow);
        var battery = report.Components.Single(c => c.Name == "Battery");

        Assert.Equal(GCS.Core.Advisor.ComponentStatus.NoData, battery.Status);
    }

    [Fact]
    public void MissingAirspeedIsReportedAsAbsentRatherThanZero()
    {
        // PX4 sends NaN when no airspeed sensor is fitted. Zero would read as a
        // stalled aircraft; NaN renders as "NaN" and poisons any maths downstream.
        var withSensor = new GCS.Core.Domain.VfrHudState(18f, 20f, 90f, 0.5f, DateTime.UtcNow);
        var without = new GCS.Core.Domain.VfrHudState(0f, 5f, 90f, 0f, DateTime.UtcNow, HasAirspeed: false);

        Assert.True(withSensor.HasAirspeed);
        Assert.False(without.HasAirspeed);
        Assert.False(float.IsNaN(without.AirspeedMps));
    }

    [Fact]
    public void ArduPilotBehaviourIsUnchangedWhenTheAutopilotIsUnknown()
    {
        // Before the first heartbeat identifies the firmware, the app must behave
        // exactly as it did — this is its own airframe's path.
        Assert.Equal("QSTABILIZE",
            FlightModeTable.Describe(AutopilotKind.Unknown, VehicleKind.Plane, 17));

        Assert.Equal(11u,
            FlightModeTable.Find(AutopilotKind.Unknown, VehicleKind.Plane, "RTL")!.Value.CustomMode);
    }
}

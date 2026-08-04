namespace GCS.Core.Domain;

public record VehicleState(
    ConnectionState? Connection,
    AttitudeState? Attitude,
    PositionState? Position,
    VfrHudState? VfrHud,
    BatteryState? Battery,
    FlightMode? FlightMode,
    GpsState? Gps,
    bool IsArmed = false,

    // Health telemetry. Null until the message arrives, which for most of these
    // means the autopilot was asked for them and has started streaming — see
    // MavlinkBackend.RequestHealthStreamsAsync.
    VibrationState? Vibration = null,
    EkfStatusState? Ekf = null,
    ServoOutputState? ServoOutput = null,
    BatteryStatusState? BatteryStatus = null,
    PowerStatusState? Power = null,
    EscTelemetryState? Esc = null
);
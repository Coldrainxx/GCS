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
    // Display name for the current mode, correct for whatever vehicle family this
    // is. FlightMode above is plane-typed and is null on a Copter or Rover.
    string? FlightModeName = null,
    Mavlink.VehicleKind Kind = Mavlink.VehicleKind.Unknown,
    Mavlink.AutopilotKind Autopilot = Mavlink.AutopilotKind.Unknown,

    VibrationState? Vibration = null,
    EkfStatusState? Ekf = null,
    ServoOutputState? ServoOutput = null,
    BatteryStatusState? BatteryStatus = null,
    PowerStatusState? Power = null,
    EscTelemetryState? Esc = null
);
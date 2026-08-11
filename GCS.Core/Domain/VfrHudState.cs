namespace GCS.Core.Domain;

public sealed record VfrHudState(
    float AirspeedMps,
    float GroundspeedMps,
    float HeadingDeg,
    float ClimbMps,
    DateTime TimestampUtc,

    // False when the autopilot reported no airspeed at all (PX4 sends NaN when no
    // sensor is fitted). Lets the UI say "not fitted" rather than showing 0 m/s,
    // which would look like a stalled aircraft.
    bool HasAirspeed = true
) : TimestampedState(TimestampUtc);

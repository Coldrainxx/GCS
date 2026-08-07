namespace GCS.Core.Domain;

public record HeartbeatState(
    byte SystemId,
    byte ComponentId,
    FlightMode? Mode,
    bool IsArmed,
    DateTime TimestampUtc,

    // Mode numbers mean different things per vehicle family, so the decoded name
    // travels with the heartbeat. Mode above stays plane-typed and is null for
    // other families — display should prefer ModeName.
    Mavlink.VehicleKind Kind = Mavlink.VehicleKind.Unknown,
    string? ModeName = null
);
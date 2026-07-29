using System;

namespace GCS.Core.Mavlink.Connection;

/// <summary>
/// Tracks link liveness and which vehicle is the "primary" (the one single-vehicle
/// screens act on). On a shared telemetry network several system ids heartbeat on
/// the same link: any heartbeat keeps the link alive, but the reported system id
/// only changes when the primary is explicitly switched — otherwise the connection
/// state would flip between drones several times a second.
/// </summary>
public sealed class MavlinkConnectionTracker
{
    private readonly TimeSpan _timeout;

    private byte? _systemId;
    private byte? _componentId;
    private DateTime _lastHeartbeat;        // any vehicle on the link
    private DateTime _lastPrimaryHeartbeat; // the primary specifically

    public bool IsConnected { get; private set; }

    public event Action<MavlinkConnectionState>? ConnectionChanged;

    public byte SystemId => _systemId ?? 0;
    public byte ComponentId => _componentId ?? 0;

    public MavlinkConnectionTracker(TimeSpan timeout)
    {
        _timeout = timeout;
    }

    /// <summary>
    /// Choose which vehicle single-vehicle operations target. Ignored when the
    /// id is 0 (unset).
    /// </summary>
    public void SetPrimary(byte systemId, byte componentId)
    {
        if (systemId == 0) return;
        if (_systemId == systemId && _componentId == componentId) return;

        _systemId = systemId;
        _componentId = componentId;
        _lastPrimaryHeartbeat = _lastHeartbeat;
        if (IsConnected) Raise();
    }

    public void OnHeartbeat(byte systemId, byte componentId, DateTime timestampUtc)
    {
        // Any vehicle's heartbeat proves the link is alive.
        _lastHeartbeat = timestampUtc;

        bool isPrimary = _systemId == systemId;
        if (isPrimary)
            _lastPrimaryHeartbeat = timestampUtc;

        // Adopt a new primary only when there isn't one, or when the current
        // primary has gone quiet (vehicle swapped on the same link). A different
        // drone heartbeating while the primary is alive must NOT steal it —
        // on a shared swarm link that would flip the target several times a second.
        bool adopted = false;
        if (_systemId == null ||
            (!isPrimary && timestampUtc - _lastPrimaryHeartbeat > _timeout))
        {
            _systemId = systemId;
            _componentId = componentId;
            _lastPrimaryHeartbeat = timestampUtc;
            adopted = true;
        }

        if (!IsConnected)
        {
            IsConnected = true;
            Raise();
        }
        else if (adopted)
        {
            Raise();
        }
    }

    public void Tick(DateTime nowUtc)
    {
        if (!IsConnected)
            return;

        if (nowUtc - _lastHeartbeat > _timeout)
        {
            IsConnected = false;
            Raise();
        }
    }

    /// <summary>
    /// Reset tracker to disconnected state.
    /// </summary>
    public void Reset()
    {
        if (IsConnected)
        {
            IsConnected = false;
            Raise();
        }

        _systemId = null;
        _componentId = null;
    }

    private void Raise()
    {
        ConnectionChanged?.Invoke(
            new MavlinkConnectionState(
                IsConnected,
                _systemId ?? 0,
                _componentId ?? 0,
                _lastHeartbeat
            )
        );
    }
}

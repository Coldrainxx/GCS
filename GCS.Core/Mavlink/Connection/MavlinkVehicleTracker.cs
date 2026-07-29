using System;
using System.Collections.Generic;
using System.Linq;

namespace GCS.Core.Mavlink.Connection;

/// <summary>
/// Tracks every vehicle heartbeating on a link, keyed by MAVLink system id.
/// On a shared telemetry network all drones transmit on the same connection,
/// so this is what turns one byte stream into a known set of vehicles.
/// </summary>
public sealed class MavlinkVehicleTracker
{
    private readonly TimeSpan _timeout;
    private readonly object _gate = new();
    private readonly Dictionary<byte, VehicleLink> _vehicles = new();

    public MavlinkVehicleTracker(TimeSpan timeout) => _timeout = timeout;

    /// <summary>A vehicle started heartbeating (system id).</summary>
    public event Action<byte>? VehicleDiscovered;

    /// <summary>A vehicle stopped heartbeating for longer than the timeout.</summary>
    public event Action<byte>? VehicleLost;

    /// <summary>System ids currently heartbeating, ascending.</summary>
    public IReadOnlyList<byte> KnownSystems
    {
        get { lock (_gate) return _vehicles.Keys.OrderBy(id => id).ToList(); }
    }

    public int Count
    {
        get { lock (_gate) return _vehicles.Count; }
    }

    public bool IsKnown(byte systemId)
    {
        lock (_gate) return _vehicles.ContainsKey(systemId);
    }

    /// <summary>Component id last seen for a vehicle (0 when unknown).</summary>
    public byte ComponentIdOf(byte systemId)
    {
        lock (_gate) return _vehicles.TryGetValue(systemId, out var v) ? v.ComponentId : (byte)0;
    }

    public void OnHeartbeat(byte systemId, byte componentId, DateTime timestampUtc)
    {
        bool isNew;
        lock (_gate)
        {
            isNew = !_vehicles.ContainsKey(systemId);
            _vehicles[systemId] = new VehicleLink(componentId, timestampUtc);
        }

        if (isNew) VehicleDiscovered?.Invoke(systemId);
    }

    /// <summary>Retire vehicles that have gone quiet. Call periodically.</summary>
    public void Tick(DateTime nowUtc)
    {
        List<byte>? lost = null;
        lock (_gate)
        {
            foreach (var (id, link) in _vehicles)
            {
                if (nowUtc - link.LastHeartbeatUtc > _timeout)
                    (lost ??= new List<byte>()).Add(id);
            }
            if (lost != null)
                foreach (var id in lost) _vehicles.Remove(id);
        }

        if (lost == null) return;
        foreach (var id in lost) VehicleLost?.Invoke(id);
    }

    public void Reset()
    {
        List<byte> known;
        lock (_gate)
        {
            known = _vehicles.Keys.ToList();
            _vehicles.Clear();
        }
        foreach (var id in known) VehicleLost?.Invoke(id);
    }

    private readonly record struct VehicleLink(byte ComponentId, DateTime LastHeartbeatUtc);
}

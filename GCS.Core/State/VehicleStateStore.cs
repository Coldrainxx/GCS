using GCS.Core.Domain;
using GCS.Core.Mavlink;
using System;
using System.Threading;

namespace GCS.Core.State;

public sealed class VehicleStateStore : IVehicleStateStore, IDisposable
{
    private readonly IMavlinkBackend _backend;
    private readonly SynchronizationContext? _context;

    private VehicleState _current = new(
        Connection: null,
        Attitude: null,
        Position: null,
        VfrHud: null,
        Battery: null,
        FlightMode: null,
        IsArmed: false,
         Gps: null  // NEW
    );

    public VehicleState Current => _current;

    public event Action<VehicleState>? StateChanged;

    public VehicleStateStore(
        IMavlinkBackend backend,
        SynchronizationContext? context = null)
    {
        _backend = backend;
        _context = context ?? SynchronizationContext.Current;

        _backend.ConnectionStateChanged += OnConnectionState;
        _backend.HeartbeatReceived += OnHeartbeat;
        _backend.AttitudeReceived += OnAttitude;
        _backend.PositionReceived += OnPosition;
        _backend.VfrHudReceived += OnVfrHud;
        _backend.BatteryReceived += OnBattery;
        _backend.GpsStateReceived += OnGpsState;  // NEW
    }

    private void OnConnectionState(ConnectionState state)
    {
        Update(_current with { Connection = state });
    }

    private void OnHeartbeat(HeartbeatState hb)
    {
        // Update FlightMode and IsArmed from heartbeat
        var next = _current;

        if (hb.Mode != null)
        {
            next = next with { FlightMode = hb.Mode };
        }

        next = next with { IsArmed = hb.IsArmed };

        Update(next);
    }

    private void OnAttitude(AttitudeState attitude)
    {
        Update(_current with { Attitude = attitude });
    }

    private void OnPosition(PositionState position)
    {
        Update(_current with { Position = position });
    }

    private void OnVfrHud(VfrHudState hud)
    {
        Update(_current with { VfrHud = hud });
    }

    private void OnBattery(BatteryState battery)
    {
        Update(_current with { Battery = battery });
    }
    private void OnGpsState(GpsState gps)  // NEW
    {
        Update(_current with { Gps = gps });
    }
    private void Update(VehicleState next)
    {
        if (_context != null)
        {
            _context.Post(_ =>
            {
                _current = next;
                StateChanged?.Invoke(_current);
            }, null);
        }
        else
        {
            _current = next;
            StateChanged?.Invoke(_current);
        }
    }

    public void Dispose()
    {
        _backend.ConnectionStateChanged -= OnConnectionState;
        _backend.HeartbeatReceived -= OnHeartbeat;
        _backend.AttitudeReceived -= OnAttitude;
        _backend.PositionReceived -= OnPosition;
        _backend.VfrHudReceived -= OnVfrHud;
        _backend.BatteryReceived -= OnBattery;
        _backend.GpsStateReceived -= OnGpsState;  // NEW
    }
}
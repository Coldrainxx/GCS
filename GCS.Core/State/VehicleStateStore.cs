using GCS.Core.Domain;
using GCS.Core.Mavlink;
using System;
using System.Threading;

namespace GCS.Core.State;

public sealed class VehicleStateStore : IVehicleStateStore, IDisposable
{
    private readonly IMavlinkBackend _backend;
    private readonly SynchronizationContext? _context;
    private readonly Timer _throttleTimer;

    private VehicleState _current = new(
        Connection: null,
        Attitude: null,
        Position: null,
        VfrHud: null,
        Battery: null,
        FlightMode: null,
        IsArmed: false,
        Gps: null
    );

    // Accumulated state from background threads — written freely, read on timer tick
    private VehicleState _pending;
    private volatile bool _hasPendingUpdate;

    private const int ThrottleIntervalMs = 33; // ~30 fps max to UI

    public VehicleState Current => _current;

    public event Action<VehicleState>? StateChanged;

    public VehicleStateStore(
        IMavlinkBackend backend,
        SynchronizationContext? context = null)
    {
        _backend = backend;
        _context = context ?? SynchronizationContext.Current;
        _pending = _current;

        _backend.ConnectionStateChanged += OnConnectionState;
        _backend.HeartbeatReceived += OnHeartbeat;
        _backend.AttitudeReceived += OnAttitude;
        _backend.PositionReceived += OnPosition;
        _backend.VfrHudReceived += OnVfrHud;
        _backend.BatteryReceived += OnBattery;
        _backend.GpsStateReceived += OnGpsState;

        // Timer fires on a threadpool thread, then posts to UI
        _throttleTimer = new Timer(OnThrottleTick, null, ThrottleIntervalMs, ThrottleIntervalMs);
    }

    private void OnConnectionState(ConnectionState state)
    {
        AccumulateUpdate(_pending with { Connection = state });
    }

    private void OnHeartbeat(HeartbeatState hb)
    {
        var next = _pending;

        if (hb.Mode != null)
        {
            next = next with { FlightMode = hb.Mode };
        }

        next = next with { IsArmed = hb.IsArmed };

        AccumulateUpdate(next);
    }

    private void OnAttitude(AttitudeState attitude)
    {
        AccumulateUpdate(_pending with { Attitude = attitude });
    }

    private void OnPosition(PositionState position)
    {
        AccumulateUpdate(_pending with { Position = position });
    }

    private void OnVfrHud(VfrHudState hud)
    {
        AccumulateUpdate(_pending with { VfrHud = hud });
    }

    private void OnBattery(BatteryState battery)
    {
        AccumulateUpdate(_pending with { Battery = battery });
    }

    private void OnGpsState(GpsState gps)
    {
        AccumulateUpdate(_pending with { Gps = gps });
    }

    /// <summary>
    /// Accumulates the update without touching the UI thread.
    /// Multiple telemetry messages between timer ticks are merged.
    /// </summary>
    private void AccumulateUpdate(VehicleState next)
    {
        _pending = next;
        _hasPendingUpdate = true;
    }

    /// <summary>
    /// Fires at ~30 fps. Pushes accumulated state to UI thread if anything changed.
    /// </summary>
    private void OnThrottleTick(object? state)
    {
        if (!_hasPendingUpdate) return;
        _hasPendingUpdate = false;

        var snapshot = _pending;

        if (_context != null)
        {
            _context.Post(_ =>
            {
                _current = snapshot;
                StateChanged?.Invoke(_current);
            }, null);
        }
        else
        {
            _current = snapshot;
            StateChanged?.Invoke(_current);
        }
    }

    public void Dispose()
    {
        _throttleTimer.Dispose();

        _backend.ConnectionStateChanged -= OnConnectionState;
        _backend.HeartbeatReceived -= OnHeartbeat;
        _backend.AttitudeReceived -= OnAttitude;
        _backend.PositionReceived -= OnPosition;
        _backend.VfrHudReceived -= OnVfrHud;
        _backend.BatteryReceived -= OnBattery;
        _backend.GpsStateReceived -= OnGpsState;
    }
}
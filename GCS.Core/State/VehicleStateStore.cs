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

    /// <summary>
    /// System id this store represents. 0 means "whatever arrives" (single-vehicle
    /// behaviour); any other value ignores telemetry from other vehicles, which is
    /// what makes one store per drone possible on a shared link.
    /// </summary>
    private readonly byte _systemId;

    public byte SystemId => _systemId;

    private bool Mine(byte systemId) => _systemId == 0 || systemId == _systemId;

    // Guards _pending, _current and _hasPendingUpdate. Telemetry events arrive
    // on the transport thread while the throttle timer reads on a pool thread,
    // and Current may be read from the UI thread - all go through this lock.
    private readonly object _gate = new();

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

    // Accumulated state from background threads - merged, read on timer tick.
    private VehicleState _pending;
    private bool _hasPendingUpdate;

    private const int ThrottleIntervalMs = 33; // ~30 fps max to UI

    public VehicleState Current
    {
        get { lock (_gate) return _current; }
    }

    public event Action<VehicleState>? StateChanged;

    public VehicleStateStore(
        IMavlinkBackend backend,
        SynchronizationContext? context = null,
        byte systemId = 0)
    {
        _backend = backend;
        _context = context ?? SynchronizationContext.Current;
        _systemId = systemId;
        _pending = _current;

        _backend.ConnectionStateChanged += OnConnectionState;
        _backend.HeartbeatReceived += OnHeartbeat;
        _backend.AttitudeReceived += OnAttitude;
        _backend.PositionReceived += OnPosition;
        _backend.VfrHudReceived += OnVfrHud;
        _backend.BatteryReceived += OnBattery;
        _backend.GpsStateReceived += OnGpsState;

        // Timer fires on a threadpool thread, then posts to UI.
        _throttleTimer = new Timer(OnThrottleTick, null, ThrottleIntervalMs, ThrottleIntervalMs);
    }

    private void OnConnectionState(ConnectionState state)
        => Mutate(s => s with { Connection = state });

    private void OnHeartbeat(HeartbeatState hb)
    {
        if (!Mine(hb.SystemId)) return;
        Mutate(s =>
        {
            if (hb.Mode != null)
                s = s with { FlightMode = hb.Mode };
            return s with { IsArmed = hb.IsArmed };
        });
    }

    private void OnAttitude(byte sysId, AttitudeState attitude)
    {
        if (Mine(sysId)) Mutate(s => s with { Attitude = attitude });
    }

    private void OnPosition(byte sysId, PositionState position)
    {
        if (Mine(sysId)) Mutate(s => s with { Position = position });
    }

    private void OnVfrHud(byte sysId, VfrHudState hud)
    {
        if (Mine(sysId)) Mutate(s => s with { VfrHud = hud });
    }

    private void OnBattery(byte sysId, BatteryState battery)
    {
        if (Mine(sysId)) Mutate(s => s with { Battery = battery });
    }

    private void OnGpsState(byte sysId, GpsState gps)
    {
        if (Mine(sysId)) Mutate(s => s with { Gps = gps });
    }

    /// <summary>
    /// Applies an update to the accumulated state without touching the UI thread.
    /// Multiple telemetry messages between timer ticks are merged.
    /// </summary>
    private void Mutate(Func<VehicleState, VehicleState> update)
    {
        lock (_gate)
        {
            _pending = update(_pending);
            _hasPendingUpdate = true;
        }
    }

    /// <summary>
    /// Fires at ~30 fps. Pushes accumulated state to the UI thread if anything changed.
    /// </summary>
    private void OnThrottleTick(object? state)
    {
        VehicleState snapshot;
        lock (_gate)
        {
            if (!_hasPendingUpdate) return;
            _hasPendingUpdate = false;
            snapshot = _pending;
        }

        if (_context != null)
            _context.Post(_ => Publish(snapshot), null);
        else
            Publish(snapshot);
    }

    private void Publish(VehicleState snapshot)
    {
        lock (_gate) _current = snapshot;
        StateChanged?.Invoke(snapshot);
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

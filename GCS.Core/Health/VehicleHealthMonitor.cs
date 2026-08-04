using GCS.Core.Domain;
using GCS.Core.State;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace GCS.Core.Health;

public sealed class VehicleHealthMonitor :
    IVehicleHealthMonitor, IDisposable
{
    private readonly IVehicleStateStore _stateStore;
    private readonly HealthPolicy _policy;
    private readonly SynchronizationContext _context;

    private HealthState _current = new(
        LinkAlive: false,
        AttitudeFresh: false,
        PositionFresh: false,
        EvaluatedAtUtc: DateTime.UtcNow
    );

    private CancellationTokenSource _cts = new();

    public HealthState Current => _current;

    public event Action<HealthState>? HealthChanged;

    public VehicleHealthMonitor(
        IVehicleStateStore stateStore,
        HealthPolicy policy,
        SynchronizationContext context)
    {
        _stateStore = stateStore;
        _policy = policy;
        _context = context;

        _stateStore.StateChanged += OnVehicleState;
        StartLoop();
    }

    private void OnVehicleState(VehicleState _)
    {
        Evaluate();
    }

    private void StartLoop()
    {
        Task.Run(async () =>
        {
            try
            {
                while (!_cts.Token.IsCancellationRequested)
                {
                    Evaluate();
                    await Task.Delay(200, _cts.Token);
                }
            }
            catch (OperationCanceledException) { }
        });
    }

    private void Evaluate()
    {
        var now = DateTime.UtcNow;
        var vs = _stateStore.Current;

        // Not Connection.LastHeartbeatUtc: that is only republished on transitions,
        // so it aged past the timeout a few seconds after connecting and left the
        // link indicator stuck red while telemetry was streaming fine.
        bool linkAlive =
            TelemetryFreshness.IsLinkAlive(vs, now, _policy.HeartbeatTimeout);

        bool attitudeFresh =
            vs.Attitude != null &&
            (now - vs.Attitude.TimestampUtc)
                <= _policy.AttitudeStaleAfter;

        bool positionFresh =
            vs.Position != null &&
            (now - vs.Position.TimestampUtc)
                <= _policy.PositionStaleAfter;

        var next = new HealthState(
            LinkAlive: linkAlive,
            AttitudeFresh: attitudeFresh,
            PositionFresh: positionFresh,
            EvaluatedAtUtc: now
        );

        if (next != _current)
        {
            _context.Post(_ =>
            {
                _current = next;
                HealthChanged?.Invoke(_current);
            }, null);
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _stateStore.StateChanged -= OnVehicleState;
    }
}

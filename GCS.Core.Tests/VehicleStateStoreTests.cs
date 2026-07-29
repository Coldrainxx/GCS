using GCS.Core.Domain;
using GCS.Core.State;
using Xunit;

namespace GCS.Core.Tests;

/// <summary>
/// The state store is what turns a shared swarm link into per-drone state:
/// each store only accepts telemetry from its own system id.
/// </summary>
public class VehicleStateStoreTests
{
    // The store batches updates on a ~33 ms timer before publishing.
    private static Task LetItPublish() => Task.Delay(150);

    private static AttitudeState Roll(float rad) => new(rad, 0f, 0f, DateTime.UtcNow);

    [Fact]
    public async Task IgnoresTelemetryFromOtherVehicles()
    {
        var backend = new FakeBackend();
        using var store = new VehicleStateStore(backend, context: null, systemId: 2);

        backend.RaiseAttitude(1, Roll(1.0f));   // another drone on the same link
        backend.RaiseAttitude(2, Roll(0.5f));   // ours
        backend.RaiseAttitude(3, Roll(-1.0f));  // another drone

        await LetItPublish();

        Assert.NotNull(store.Current.Attitude);
        Assert.Equal(0.5f, store.Current.Attitude!.RollRad);
    }

    [Fact]
    public async Task TwoStoresOnOneLinkHoldIndependentState()
    {
        var backend = new FakeBackend();
        using var one = new VehicleStateStore(backend, context: null, systemId: 1);
        using var two = new VehicleStateStore(backend, context: null, systemId: 2);

        backend.RaiseAttitude(1, Roll(0.25f));
        backend.RaiseAttitude(2, Roll(0.75f));
        backend.RaiseBattery(1, new BatteryState(12.6f, 5f, 90, DateTime.UtcNow));
        backend.RaiseBattery(2, new BatteryState(11.1f, 8f, 40, DateTime.UtcNow));

        await LetItPublish();

        Assert.Equal(0.25f, one.Current.Attitude!.RollRad);
        Assert.Equal(0.75f, two.Current.Attitude!.RollRad);
        Assert.Equal(90, one.Current.Battery!.RemainingPercent);
        Assert.Equal(40, two.Current.Battery!.RemainingPercent);
    }

    [Fact]
    public async Task SystemIdZeroAcceptsAnyVehicle()
    {
        // Backwards-compatible single-vehicle behaviour.
        var backend = new FakeBackend();
        using var store = new VehicleStateStore(backend, context: null, systemId: 0);

        backend.RaiseAttitude(7, Roll(0.42f));

        await LetItPublish();

        Assert.Equal(0.42f, store.Current.Attitude!.RollRad);
    }

    [Fact]
    public async Task ArmedStateFollowsOnlyItsOwnVehiclesHeartbeat()
    {
        var backend = new FakeBackend();
        using var store = new VehicleStateStore(backend, context: null, systemId: 2);

        backend.RaiseHeartbeat(new HeartbeatState(1, 1, FlightMode.Auto, true, DateTime.UtcNow));
        await LetItPublish();
        Assert.False(store.Current.IsArmed); // drone 1 armed, not us

        backend.RaiseHeartbeat(new HeartbeatState(2, 1, FlightMode.Loiter, true, DateTime.UtcNow));
        await LetItPublish();
        Assert.True(store.Current.IsArmed);
    }
}

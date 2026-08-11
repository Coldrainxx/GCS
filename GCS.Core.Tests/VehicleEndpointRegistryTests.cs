using System.Net;
using GCS.Core.Transport;
using Xunit;

namespace GCS.Core.Tests;

/// <summary>
/// Routing for a UDP link where each drone has its own IP. Without it every
/// command lands on the single configured address, so one aircraft receives the
/// whole fleet's commands.
/// </summary>
public class VehicleEndpointRegistryTests
{
    private static readonly IPEndPoint Fallback = Endpoint("192.168.1.255");

    private static IPEndPoint Endpoint(string ip, int port = 14550) =>
        new(IPAddress.Parse(ip), port);

    /// <summary>MAVLink 2 header: magic, len, incompat, compat, seq, sysid, compid, msgid×3.</summary>
    private static byte[] V2Frame(byte sysId) =>
        new byte[] { 0xFD, 9, 0, 0, 0, sysId, 1, 0, 0, 0, /* payload+crc */ 0, 0 };

    /// <summary>MAVLink 1 header: magic, len, seq, sysid, compid, msgid.</summary>
    private static byte[] V1Frame(byte sysId) =>
        new byte[] { 0xFE, 9, 0, sysId, 1, 0, 0, 0 };

    [Fact]
    public void EachVehicleGetsTheAddressItsHeartbeatsCameFrom()
    {
        var registry = new VehicleEndpointRegistry();

        registry.Learn(V2Frame(1), Endpoint("192.168.1.11"));
        registry.Learn(V2Frame(2), Endpoint("192.168.1.12"));
        registry.Learn(V2Frame(3), Endpoint("192.168.1.13"));

        Assert.Equal(Endpoint("192.168.1.11"), registry.Resolve(1, Fallback));
        Assert.Equal(Endpoint("192.168.1.12"), registry.Resolve(2, Fallback));
        Assert.Equal(Endpoint("192.168.1.13"), registry.Resolve(3, Fallback));
    }

    [Fact]
    public void ReadsTheSystemIdFromMavlink1FramesToo()
    {
        var registry = new VehicleEndpointRegistry();

        registry.Learn(V1Frame(7), Endpoint("10.0.0.7"));

        Assert.Equal(Endpoint("10.0.0.7"), registry.Resolve(7, Fallback));
    }

    [Fact]
    public void ABroadcastGoesToTheConfiguredAddress()
    {
        var registry = new VehicleEndpointRegistry();
        registry.Learn(V2Frame(1), Endpoint("192.168.1.11"));

        Assert.Equal(Fallback, registry.Resolve(0, Fallback));
    }

    [Fact]
    public void AVehicleWeHaveNotHeardFromFallsBackRatherThanGuessing()
    {
        var registry = new VehicleEndpointRegistry();
        registry.Learn(V2Frame(1), Endpoint("192.168.1.11"));

        // Sending to vehicle 9's neighbour would be worse than sending nowhere
        // useful: on a shared radio the fallback is in fact correct.
        Assert.Equal(Fallback, registry.Resolve(9, Fallback));
    }

    [Fact]
    public void AVehicleThatMovesToANewAddressIsFollowed()
    {
        var registry = new VehicleEndpointRegistry();

        registry.Learn(V2Frame(1), Endpoint("192.168.1.11"));
        // Same drone, new DHCP lease.
        registry.Learn(V2Frame(1), Endpoint("192.168.1.44"));

        Assert.Equal(Endpoint("192.168.1.44"), registry.Resolve(1, Fallback));
        Assert.Single(registry.Known);
    }

    [Fact]
    public void TheSameVehicleOnADifferentPortIsANewEndpoint()
    {
        var registry = new VehicleEndpointRegistry();

        registry.Learn(V2Frame(1), Endpoint("192.168.1.11", 14550));
        registry.Learn(V2Frame(1), Endpoint("192.168.1.11", 14551));

        Assert.Equal(Endpoint("192.168.1.11", 14551), registry.Resolve(1, Fallback));
    }

    [Fact]
    public void AnotherGroundStationOnTheLinkIsNotTreatedAsAVehicle()
    {
        var registry = new VehicleEndpointRegistry();

        // 255 is a GCS — ours echoed back, or a second one sharing the link.
        Assert.Equal(0, registry.Learn(V2Frame(255), Endpoint("192.168.1.50")));
        Assert.Empty(registry.Known);
    }

    [Theory]
    [InlineData(new byte[0])]                                  // empty datagram
    [InlineData(new byte[] { 0xFD, 9, 0, 0, 0, 1 })]           // v2 header cut short
    [InlineData(new byte[] { 0xFE, 9, 0 })]                    // v1 header cut short
    [InlineData(new byte[] { 0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A, 0x0B })] // not MAVLink
    public void GarbageOnThePortTeachesUsNothing(byte[] datagram)
    {
        var registry = new VehicleEndpointRegistry();

        Assert.Equal(0, registry.Learn(datagram, Endpoint("192.168.1.99")));
        Assert.Empty(registry.Known);
    }

    [Fact]
    public void SystemIdZeroIsNotAnAddressableVehicle()
    {
        var registry = new VehicleEndpointRegistry();

        Assert.Equal(0, registry.Learn(V2Frame(0), Endpoint("192.168.1.60")));
        Assert.Empty(registry.Known);
    }

    [Fact]
    public void SharedRadioSendsEverythingToTheOneConfiguredAddress()
    {
        var registry = new VehicleEndpointRegistry();

        // Three vehicles behind one telemetry radio: same source endpoint for all.
        var radio = Endpoint("192.168.1.20");
        foreach (byte sysId in new byte[] { 1, 2, 3 })
            registry.Learn(V2Frame(sysId), radio);

        foreach (byte sysId in new byte[] { 1, 2, 3 })
            Assert.Equal(radio, registry.Resolve(sysId, Fallback));
    }

    [Fact]
    public void ReconnectingStartsFromNothingLearned()
    {
        var registry = new VehicleEndpointRegistry();
        registry.Learn(V2Frame(1), Endpoint("192.168.1.11"));

        registry.Clear();

        Assert.Empty(registry.Known);
        Assert.Equal(Fallback, registry.Resolve(1, Fallback));
    }
}

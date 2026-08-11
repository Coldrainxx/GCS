using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;

namespace GCS.Core.Transport;

/// <summary>
/// Remembers which address each vehicle is talking from, so a reply can go back
/// to that one vehicle rather than to a single configured address.
///
/// This only changes anything when vehicles have separate addresses — several
/// drones on WiFi, each with its own IP. Behind a shared radio or a router every
/// vehicle resolves to the same endpoint and the map is harmless.
///
/// Only the MAVLink header is read. The sending system's id sits at a fixed
/// offset in both framing versions, so the transport layer never has to know
/// what any message means.
/// </summary>
public sealed class VehicleEndpointRegistry
{
    /// <summary>MAVLink 2 frame start byte.</summary>
    private const byte Mavlink2Magic = 0xFD;

    /// <summary>MAVLink 1 frame start byte.</summary>
    private const byte Mavlink1Magic = 0xFE;

    /// <summary>Shortest legal v1 frame: 6 header bytes, no payload, 2 CRC bytes.</summary>
    private const int MinV1Length = 8;

    /// <summary>Shortest legal v2 frame: 10 header bytes, no payload, 2 CRC bytes.</summary>
    private const int MinV2Length = 12;

    private readonly ConcurrentDictionary<byte, IPEndPoint> _endpoints = new();

    /// <summary>Vehicles heard from so far, and where each one answered from.</summary>
    public IReadOnlyDictionary<byte, IPEndPoint> Known => _endpoints;

    /// <summary>
    /// Note the address a packet arrived from, if it came from a vehicle.
    /// </summary>
    /// <returns>The system id learned, or 0 when the packet taught us nothing.</returns>
    public byte Learn(ReadOnlySpan<byte> packet, IPEndPoint from)
    {
        byte systemId = ReadSystemId(packet);
        if (systemId == 0) return 0;

        _endpoints[systemId] = from;
        return systemId;
    }

    /// <summary>
    /// Where to send a packet meant for <paramref name="targetSystemId"/>.
    ///
    /// Falls back to <paramref name="fallback"/> for broadcasts (target 0) and for
    /// a vehicle that has not been heard from — on a shared link that address is
    /// correct anyway, and on a per-IP link there is nothing better to guess.
    /// </summary>
    public IPEndPoint Resolve(byte targetSystemId, IPEndPoint fallback) =>
        targetSystemId != 0 && _endpoints.TryGetValue(targetSystemId, out var known)
            ? known
            : fallback;

    /// <summary>Forget every learned address — used when a link is reopened.</summary>
    public void Clear() => _endpoints.Clear();

    /// <summary>
    /// The system id in a MAVLink frame header, or 0 if there isn't a usable one.
    ///
    /// A UDP datagram can hold several frames back to back; the first one is
    /// enough, since a vehicle's packets all arrive from the same socket.
    /// </summary>
    private static byte ReadSystemId(ReadOnlySpan<byte> packet)
    {
        if (packet.Length == 0) return 0;

        byte systemId = packet[0] switch
        {
            Mavlink2Magic when packet.Length >= MinV2Length => packet[5],
            Mavlink1Magic when packet.Length >= MinV1Length => packet[3],
            _ => 0,
        };

        // 255 is a ground station — ours, or another one sharing the link — and 0
        // is unset. Neither is a vehicle we can send commands to.
        return systemId is 255 ? (byte)0 : systemId;
    }
}

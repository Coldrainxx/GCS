namespace GCS.Core.Transport;

public interface ITransport : IDisposable
{
    /// <summary>
    /// Fired when raw bytes are received.
    /// Called from transport thread.
    /// </summary>
    event Action<ReadOnlyMemory<byte>> DataReceived;

    /// <summary>
    /// Fired on transport-level error (port closed, socket error, etc).
    /// </summary>
    event Action<Exception> TransportError;

    Task StartAsync(CancellationToken cancellationToken);
    Task StopAsync();

    /// <summary>
    /// Optional: send raw bytes (for commands, heartbeat, etc).
    /// </summary>
    Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken);

    /// <summary>
    /// Send to a particular vehicle.
    ///
    /// Only matters where vehicles have separate addresses — several drones on WiFi,
    /// each with its own IP. A shared radio or a router is one destination, so this
    /// falls back to <see cref="SendAsync"/> there. Pass 0 when the packet is not
    /// aimed at a specific vehicle.
    /// </summary>
    Task SendToAsync(ReadOnlyMemory<byte> data, byte targetSystemId, CancellationToken cancellationToken)
        => SendAsync(data, cancellationToken);
}

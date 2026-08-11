using System;
using System.Threading;
using System.Threading.Tasks;

namespace GCS.Core.Transport;

public abstract class TransportBase : ITransport
{
    public event Action<ReadOnlyMemory<byte>>? DataReceived;
    public event Action<Exception>? TransportError;

    protected CancellationTokenSource? Cts;
    protected Task? IoTask;

    public abstract Task StartAsync(CancellationToken cancellationToken);
    public abstract Task StopAsync();
    public abstract Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken);

    /// <summary>
    /// One address serves every vehicle on a serial link or a single TCP socket,
    /// so the target is ignored unless a transport says otherwise.
    /// </summary>
    public virtual Task SendToAsync(
        ReadOnlyMemory<byte> data, byte targetSystemId, CancellationToken cancellationToken)
        => SendAsync(data, cancellationToken);

    protected void RaiseData(ReadOnlyMemory<byte> data)
        => DataReceived?.Invoke(data);

    protected void RaiseError(Exception ex)
        => TransportError?.Invoke(ex);

    public virtual void Dispose()
    {
        Cts?.Cancel();
        Cts?.Dispose();
    }
}

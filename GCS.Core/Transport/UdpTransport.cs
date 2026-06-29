using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace GCS.Core.Transport;

public sealed class UdpTransport : TransportBase
{
    private readonly UdpClient _client;
    private readonly IPEndPoint _remote;

    public UdpTransport(int localPort, string remoteHost, int remotePort)
    {
        _client = new UdpClient(localPort);
        _remote = new IPEndPoint(IPAddress.Parse(remoteHost), remotePort);
    }

    public override Task StartAsync(CancellationToken externalToken)
    {
        Cts = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
        IoTask = Task.Run(() => ReadLoop(Cts.Token), Cts.Token);
        return Task.CompletedTask;
    }

    public override async Task StopAsync()
    {
        if (Cts != null)
        {
            Cts.Cancel();
            if (IoTask != null)
                await IoTask;
            Cts.Dispose();
        }

        _client.Close();
    }

    private async Task ReadLoop(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var result = await _client.ReceiveAsync(token);
                RaiseData(result.Buffer);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            RaiseError(ex);
        }
    }

    public override async Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken token)
    {
        try
        {
            await _client.SendAsync(data.ToArray(), data.Length, _remote);
        }
        catch (Exception ex)
        {
            RaiseError(ex);
            throw;
        }
    }

    public override void Dispose()
    {
        base.Dispose();
        _client.Dispose();
    }
}

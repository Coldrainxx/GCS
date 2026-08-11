using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace GCS.Core.Transport;

public sealed class UdpTransport : TransportBase
{
    private readonly UdpClient _client;
    private readonly IPEndPoint _remote;

    /// <summary>
    /// Where each vehicle's packets came from, learned as they arrive.
    ///
    /// Without this, every command would go to the one configured address, so on a
    /// fleet of WiFi drones with individual IPs a single aircraft would receive
    /// commands meant for all of them.
    /// </summary>
    private readonly VehicleEndpointRegistry _vehicles = new();

    public UdpTransport(int localPort, string remoteHost, int remotePort)
    {
        _client = new UdpClient(localPort);
        _remote = new IPEndPoint(IPAddress.Parse(remoteHost), remotePort);
    }

    /// <summary>Vehicles seen so far and the address each is reachable at.</summary>
    public IReadOnlyDictionary<byte, IPEndPoint> KnownEndpoints => _vehicles.Known;

    public override Task StartAsync(CancellationToken externalToken)
    {
        // Addresses learned on a previous run of this socket may have been handed
        // to different aircraft since.
        _vehicles.Clear();

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

                _vehicles.Learn(result.Buffer, result.RemoteEndPoint);
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

    public override Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken token) =>
        SendToAsync(data, 0, token);

    public override async Task SendToAsync(
        ReadOnlyMemory<byte> data, byte targetSystemId, CancellationToken token)
    {
        var destination = _vehicles.Resolve(targetSystemId, _remote);

        try
        {
            await _client.SendAsync(data.ToArray(), data.Length, destination);
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

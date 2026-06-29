using System;
using System.Diagnostics;
using System.IO.Ports;
using System.Threading;
using System.Threading.Tasks;

namespace GCS.Core.Transport;

public sealed class SerialTransport : TransportBase
{
    private readonly SerialPort _port;

    public SerialTransport(string portName, int baudRate)
    {
        _port = new SerialPort(portName, baudRate)
        {
            ReadTimeout = 500,
            WriteTimeout = 500
        };
    }

    public override Task StartAsync(CancellationToken externalToken)
    {
        Debug.WriteLine($"[SerialTransport] Opening {_port.PortName}, IsOpen={_port.IsOpen}");

        Cts = CancellationTokenSource.CreateLinkedTokenSource(externalToken);

        try
        {
            _port.Open();
            Debug.WriteLine("[SerialTransport] Open OK");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SerialTransport] Open FAILED: {ex}");
            RaiseError(ex);
            throw;
        }

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
            Cts = null;
        }

        if (_port.IsOpen)
            _port.Close();
    }

    private async Task ReadLoop(CancellationToken token)
    {
        byte[] buffer = new byte[4096];
        Debug.WriteLine("[SerialTransport] ReadLoop started");

        try
        {
            while (!token.IsCancellationRequested)
            {
                int bytes = await _port.BaseStream.ReadAsync(
                    buffer.AsMemory(),
                    token
                );

                if (bytes > 0)
                    RaiseData(buffer.AsMemory(0, bytes));
            }
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
        catch (Exception ex)
        {
            RaiseError(ex);
            Cts?.Cancel();
        }
    }

    public override async Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken token)
    {
        try
        {
            await _port.BaseStream.WriteAsync(data, token);
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
        _port.Dispose();
    }
}

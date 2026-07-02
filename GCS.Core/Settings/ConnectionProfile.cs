namespace GCS.Core.Settings;

public enum TransportKind { Serial, Tcp, Udp }

/// <summary>A remembered connection, serialisable to user settings.</summary>
public sealed class ConnectionProfile
{
    public TransportKind Kind { get; set; } = TransportKind.Serial;

    public string PortName { get; set; } = "";
    public int BaudRate { get; set; } = 57600;

    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 5760;

    public int LocalPort { get; set; } = 14550;
    public string RemoteHost { get; set; } = "127.0.0.1";
    public int RemotePort { get; set; } = 14551;

    public string Label => Kind switch
    {
        TransportKind.Serial => $"Serial  {PortName} @ {BaudRate}",
        TransportKind.Tcp => $"TCP  {Host}:{Port}",
        TransportKind.Udp => $"UDP  :{LocalPort} → {RemoteHost}:{RemotePort}",
        _ => "?"
    };

    /// <summary>Same target (ignores nothing that matters for dedup of the recent list).</summary>
    public bool SameAs(ConnectionProfile o) => Kind switch
    {
        TransportKind.Serial => o.Kind == Kind && o.PortName == PortName && o.BaudRate == BaudRate,
        TransportKind.Tcp => o.Kind == Kind && o.Host == Host && o.Port == Port,
        TransportKind.Udp => o.Kind == Kind && o.LocalPort == LocalPort && o.RemoteHost == RemoteHost && o.RemotePort == RemotePort,
        _ => false
    };
}

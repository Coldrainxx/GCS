using GCS.Core.Settings;
using GCS.Core.Transport;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;

namespace GCS.ViewModels;

public class ConnectionViewModel : ViewModelBase
{
    private string _selectedTransportType = "Serial";
    private string _selectedSerialPort = "";
    private int _baudRate = 57600;
    private string _tcpHost = "127.0.0.1";
    private int _tcpPort = 5760;
    private int _udpLocalPort = 14550;
    private string _udpRemoteHost = "127.0.0.1";
    private int _udpRemotePort = 14551;
    private bool _isConnected;
    private bool _isConnecting;
    private string _statusMessage = "Disconnected";

    public ObservableCollection<string> TransportTypes { get; } = new()
    {
        "Serial",
        "TCP",
        "UDP"
    };

    public ObservableCollection<string> AvailableSerialPorts { get; } = new();

    /// <summary>Recently used connections (most recent first), from persisted settings.</summary>
    public ObservableCollection<ConnectionProfile> RecentConnections { get; } = new();

    private ConnectionProfile? _selectedProfile;
    /// <summary>Bound to the "recent" dropdown; applying it fills the connection fields.</summary>
    public ConnectionProfile? SelectedProfile
    {
        get => _selectedProfile;
        set { if (SetProperty(ref _selectedProfile, value)) ApplyProfile(value); }
    }

    /// <summary>Auto-reconnect on link loss (persisted).</summary>
    public bool AutoReconnect
    {
        get => SettingsStore.Current.AutoReconnect;
        set
        {
            if (SettingsStore.Current.AutoReconnect != value)
            {
                SettingsStore.Current.AutoReconnect = value;
                SettingsStore.Save();
                OnPropertyChanged();
            }
        }
    }

    public ObservableCollection<int> BaudRates { get; } = new()
    {
        9600,
        19200,
        38400,
        57600,
        115200,
        230400,
        460800,
        921600
    };

    // Properties
    public string SelectedTransportType
    {
        get => _selectedTransportType;
        set
        {
            if (SetProperty(ref _selectedTransportType, value))
            {
                OnPropertyChanged(nameof(IsSerialSelected));
                OnPropertyChanged(nameof(IsTcpSelected));
                OnPropertyChanged(nameof(IsUdpSelected));
            }
        }
    }

    public string SelectedSerialPort
    {
        get => _selectedSerialPort;
        set => SetProperty(ref _selectedSerialPort, value);
    }

    public int BaudRate
    {
        get => _baudRate;
        set => SetProperty(ref _baudRate, value);
    }

    public string TcpHost
    {
        get => _tcpHost;
        set => SetProperty(ref _tcpHost, value);
    }

    public int TcpPort
    {
        get => _tcpPort;
        set => SetProperty(ref _tcpPort, value);
    }

    public int UdpLocalPort
    {
        get => _udpLocalPort;
        set => SetProperty(ref _udpLocalPort, value);
    }

    public string UdpRemoteHost
    {
        get => _udpRemoteHost;
        set => SetProperty(ref _udpRemoteHost, value);
    }

    public int UdpRemotePort
    {
        get => _udpRemotePort;
        set => SetProperty(ref _udpRemotePort, value);
    }

    public bool IsConnected
    {
        get => _isConnected;
        set
        {
            if (SetProperty(ref _isConnected, value))
            {
                OnPropertyChanged(nameof(IsDisconnected));
                OnPropertyChanged(nameof(CanEditSettings));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public bool IsConnecting
    {
        get => _isConnecting;
        set
        {
            if (SetProperty(ref _isConnecting, value))
            {
                OnPropertyChanged(nameof(CanEditSettings));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public bool IsDisconnected => !IsConnected;

    public bool CanEditSettings => !IsConnected && !IsConnecting;

    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    // Visibility helpers
    public bool IsSerialSelected => SelectedTransportType == "Serial";
    public bool IsTcpSelected => SelectedTransportType == "TCP";
    public bool IsUdpSelected => SelectedTransportType == "UDP";

    // Commands
    public ICommand ConnectCommand { get; }
    public ICommand DisconnectCommand { get; }
    public ICommand RefreshPortsCommand { get; }
    public ICommand ApplyProfileCommand { get; }

    // Events
    public event Action<TransportConfig>? ConnectRequested;
    public event Action? DisconnectRequested;

    public ConnectionViewModel()
    {
        ConnectCommand = new RelayCommand(OnConnect, () => !IsConnected && !IsConnecting);
        DisconnectCommand = new RelayCommand(OnDisconnect, () => IsConnected && !IsConnecting);
        RefreshPortsCommand = new RelayCommand(RefreshSerialPorts, () => CanEditSettings);
        ApplyProfileCommand = new RelayCommand<ConnectionProfile>(ApplyProfile, _ => CanEditSettings);

        RefreshSerialPorts();
        LoadRecentConnections();
        if (RecentConnections.Count > 0) ApplyProfile(RecentConnections[0]); // restore last used
    }

    private void LoadRecentConnections()
    {
        RecentConnections.Clear();
        foreach (var p in SettingsStore.Current.RecentConnections)
            RecentConnections.Add(p);
    }

    /// <summary>Populate the connection fields from a saved profile.</summary>
    public void ApplyProfile(ConnectionProfile? p)
    {
        if (p == null) return;
        switch (p.Kind)
        {
            case TransportKind.Serial:
                SelectedTransportType = "Serial";
                if (!string.IsNullOrEmpty(p.PortName)) SelectedSerialPort = p.PortName;
                BaudRate = p.BaudRate;
                break;
            case TransportKind.Tcp:
                SelectedTransportType = "TCP";
                TcpHost = p.Host;
                TcpPort = p.Port;
                break;
            case TransportKind.Udp:
                SelectedTransportType = "UDP";
                UdpLocalPort = p.LocalPort;
                UdpRemoteHost = p.RemoteHost;
                UdpRemotePort = p.RemotePort;
                break;
        }
    }

    private void RefreshSerialPorts()
    {
        AvailableSerialPorts.Clear();
        var ports = SerialPortDiscovery.GetAvailablePorts();

        foreach (var port in ports)
        {
            AvailableSerialPorts.Add(port);
        }

        if (AvailableSerialPorts.Any() && string.IsNullOrEmpty(SelectedSerialPort))
        {
            SelectedSerialPort = AvailableSerialPorts.First();
        }

        StatusMessage = $"Found {ports.Count} serial port(s)";
    }

    private void OnConnect()
    {
        TransportConfig? config = SelectedTransportType switch
        {
            "Serial" => new SerialTransportConfig(SelectedSerialPort, BaudRate),
            "TCP" => new TcpTransportConfig(TcpHost, TcpPort),
            "UDP" => new UdpTransportConfig(UdpLocalPort, UdpRemoteHost, UdpRemotePort),
            _ => null
        };

        if (config != null)
        {
            IsConnecting = true;
            StatusMessage = "Connecting...";
            ConnectRequested?.Invoke(config);
        }
    }

    private void OnDisconnect()
    {
        IsConnecting = true;
        StatusMessage = "Disconnecting...";
        DisconnectRequested?.Invoke();
    }

    public void SetConnected()
    {
        IsConnecting = false;
        IsConnected = true;
        StatusMessage = $"Connected via {SelectedTransportType}";
    }

    public void SetDisconnected()
    {
        IsConnecting = false;
        IsConnected = false;
        StatusMessage = "Disconnected";
    }

    public void SetError(string message)
    {
        IsConnecting = false;
        IsConnected = false;
        StatusMessage = $"Error: {message}";
    }
}
using GCS.Core.Mavlink.Messages;
using System.Collections.ObjectModel;

namespace GCS.ViewModels;

public class RcChannelsViewModel : ViewModelBase
{
    private byte _channelCount;
    private byte _rssi;
    private string _lastUpdate = "N/A";

    public ObservableCollection<RcChannelItemViewModel> Channels { get; } = new();

    public byte ChannelCount
    {
        get => _channelCount;
        private set => SetProperty(ref _channelCount, value);
    }

    public byte Rssi
    {
        get => _rssi;
        private set => SetProperty(ref _rssi, value);
    }

    public string RssiPercent => Rssi == 255 ? "N/A" : $"{Rssi}%";

    public string LastUpdate
    {
        get => _lastUpdate;
        private set => SetProperty(ref _lastUpdate, value);
    }

    public RcChannelsViewModel()
    {
        // Initialize 18 channels
        for (int i = 1; i <= 18; i++)
        {
            Channels.Add(new RcChannelItemViewModel(i));
        }
    }

    public void UpdateChannels(RcChannelsData data)
    {
        ChannelCount = data.Chancount;
        Rssi = data.Rssi;
        LastUpdate = DateTime.Now.ToString("HH:mm:ss.fff");

        var values = data.ToArray();
        for (int i = 0; i < Math.Min(values.Length, Channels.Count); i++)
        {
            Channels[i].UpdateValue(values[i]);
        }

        OnPropertyChanged(nameof(RssiPercent));
    }
}

public class RcChannelItemViewModel : ViewModelBase
{
    private ushort _rawValue;
    private double _normalizedValue;  // 0 to 100 (center = 50)

    public int ChannelNumber { get; }
    public string ChannelName => GetChannelLabel();

    public ushort RawValue
    {
        get => _rawValue;
        private set => SetProperty(ref _rawValue, value);
    }

    /// <summary>
    /// Normalized value: 0-100 where 50 = center (1500 PWM)
    /// 1000 PWM = 0, 1500 PWM = 50, 2000 PWM = 100
    /// </summary>
    public double NormalizedValue
    {
        get => _normalizedValue;
        private set => SetProperty(ref _normalizedValue, value);
    }

    public string BarColor => ChannelNumber switch
    {
        1 => "#2196F3",  // Roll - Blue
        2 => "#4CAF50",  // Pitch - Green
        3 => "#FF9800",  // Throttle - Orange
        4 => "#9C27B0",  // Yaw - Purple
        5 => "#F44336",  // Mode - Red
        _ => "#607D8B"   // Others - Gray
    };

    public RcChannelItemViewModel(int channelNumber)
    {
        ChannelNumber = channelNumber;
        _normalizedValue = 50; // Start at center
    }

    private string GetChannelLabel()
    {
        return ChannelNumber switch
        {
            1 => "CH1 ROLL",
            2 => "CH2 PITCH",
            3 => "CH3 THROTTLE",
            4 => "CH4 YAW",
            5 => "CH5 MODE",
            6 => "CH6 AUX1",
            7 => "CH7 AUX2",
            8 => "CH8 AUX3",
            _ => $"CH{ChannelNumber}"
        };
    }

    public void UpdateValue(ushort rawValue)
    {
        RawValue = rawValue;

        // Handle invalid values
        if (rawValue == 0 || rawValue == 65535)
        {
            NormalizedValue = 50; // Show at center when invalid
            return;
        }

        // Normalize PWM (1000-2000) to 0-100 where 50 = center (1500)
        // 1000 -> 0, 1500 -> 50, 2000 -> 100
        NormalizedValue = Math.Clamp((rawValue - 1000.0) / 10.0, 0, 100);
    }
}
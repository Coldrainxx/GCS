using GCS.Core.Mavlink.Messages;
using System;
using System.Collections.ObjectModel;
using System.Windows;

namespace GCS.ViewModels;

public class RcChannelsViewModel : ViewModelBase
{
    private byte _channelCount;
    private byte _rssi;
    private string _lastUpdate = "N/A";

    // Stick positions (normalized -1 to 1)
    private double _leftStickX;   // Yaw
    private double _leftStickY;   // Throttle
    private double _rightStickX;  // Roll
    private double _rightStickY;  // Pitch

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
    public double RssiNormalized => Rssi == 255 ? 0 : Rssi;

    public string RssiColor => Rssi switch
    {
        255 => "#8B949E",
        >= 70 => "#3FB950",
        >= 40 => "#FF9500",
        _ => "#F85149"
    };

    public string LastUpdate
    {
        get => _lastUpdate;
        private set => SetProperty(ref _lastUpdate, value);
    }

    // Stick positions for visualization
    public double LeftStickX { get => _leftStickX; private set => SetProperty(ref _leftStickX, value); }
    public double LeftStickY { get => _leftStickY; private set => SetProperty(ref _leftStickY, value); }
    public double RightStickX { get => _rightStickX; private set => SetProperty(ref _rightStickX, value); }
    public double RightStickY { get => _rightStickY; private set => SetProperty(ref _rightStickY, value); }

    // Channel assignments (can be customized)
    public int RollChannel { get; set; } = 1;
    public int PitchChannel { get; set; } = 2;
    public int ThrottleChannel { get; set; } = 3;
    public int YawChannel { get; set; } = 4;

    public RcChannelsViewModel()
    {
        // Initialize 18 channels with default names
        string[] defaultNames = { "Roll", "Pitch", "Throttle", "Yaw", "Mode", "Aux 1", "Aux 2", "Aux 3",
                                  "Aux 4", "Aux 5", "Aux 6", "Aux 7", "Aux 8", "Aux 9", "Aux 10",
                                  "Aux 11", "Aux 12", "Aux 13" };

        for (int i = 1; i <= 18; i++)
        {
            string name = i <= defaultNames.Length ? defaultNames[i - 1] : $"CH{i}";
            Channels.Add(new RcChannelItemViewModel(i, name));
        }
    }

    public void UpdateChannels(RcChannelsData data)
    {
        Application.Current?.Dispatcher?.BeginInvoke(() =>
        {
            ChannelCount = data.Chancount;
            Rssi = data.Rssi;
            LastUpdate = DateTime.Now.ToString("HH:mm:ss.fff");

            var values = data.ToArray();
            for (int i = 0; i < Math.Min(values.Length, Channels.Count); i++)
            {
                Channels[i].UpdateValue(values[i], i < ChannelCount);
            }

            // Update stick positions (Mode 2: Left=Thr/Yaw, Right=Roll/Pitch)
            if (values.Length >= 4)
            {
                RightStickX = NormalizePwm(values[RollChannel - 1]);     // Roll
                RightStickY = -NormalizePwm(values[PitchChannel - 1]);   // Pitch (inverted)
                LeftStickY = -NormalizePwmThrottle(values[ThrottleChannel - 1]); // Throttle
                LeftStickX = NormalizePwm(values[YawChannel - 1]);       // Yaw
            }

            OnPropertyChanged(nameof(RssiPercent));
            OnPropertyChanged(nameof(RssiNormalized));
            OnPropertyChanged(nameof(RssiColor));
        });
    }

    private static double NormalizePwm(ushort pwm)
    {
        // 1000-2000 -> -1 to 1 (center at 1500)
        if (pwm == 0 || pwm == 65535) return 0;
        return Math.Clamp((pwm - 1500.0) / 500.0, -1, 1);
    }

    private static double NormalizePwmThrottle(ushort pwm)
    {
        // 1000-2000 -> -1 to 1 (bottom at 1000)
        if (pwm == 0 || pwm == 65535) return -1;
        return Math.Clamp((pwm - 1500.0) / 500.0, -1, 1);
    }
}

public class RcChannelItemViewModel : ViewModelBase
{
    private ushort _rawValue;
    private double _normalizedValue;
    private double _centerOffset;
    private bool _isActive = true;
    private string _displayName;

    public int ChannelNumber { get; }
    public string ChannelLabel => $"CH{ChannelNumber}";

    public string DisplayName
    {
        get => _displayName;
        set => SetProperty(ref _displayName, value);
    }

    public ushort RawValue
    {
        get => _rawValue;
        private set => SetProperty(ref _rawValue, value);
    }

    public double NormalizedValue
    {
        get => _normalizedValue;
        private set => SetProperty(ref _normalizedValue, value);
    }

    // Offset from center (-50 to +50 for bar positioning)
    public double CenterOffset
    {
        get => _centerOffset;
        private set => SetProperty(ref _centerOffset, value);
    }

    public bool IsActive
    {
        get => _isActive;
        private set => SetProperty(ref _isActive, value);
    }

    public string BarColor => ChannelNumber switch
    {
        1 => "#2196F3",  // Roll - Blue
        2 => "#4CAF50",  // Pitch - Green
        3 => "#FF9500",  // Throttle - Orange
        4 => "#9C27B0",  // Yaw - Purple
        5 => "#F44336",  // Mode - Red
        6 => "#00BCD4",  // Aux1 - Cyan
        7 => "#FFEB3B",  // Aux2 - Yellow
        8 => "#E91E63",  // Aux3 - Pink
        _ => "#607D8B"   // Others - Gray
    };

    public string TextColor => IsActive ? "#E6EDF3" : "#4A5568";

    public RcChannelItemViewModel(int channelNumber, string displayName = "")
    {
        ChannelNumber = channelNumber;
        _displayName = string.IsNullOrEmpty(displayName) ? $"CH{channelNumber}" : displayName;
    }

    public void UpdateValue(ushort rawValue, bool isActive = true)
    {
        RawValue = rawValue;
        IsActive = isActive;

        if (rawValue == 0 || rawValue == 65535 || !isActive)
        {
            NormalizedValue = 50; // Center
            CenterOffset = 0;
        }
        else
        {
            // Normalize PWM (1000-2000) to 0-100%
            NormalizedValue = Math.Clamp((rawValue - 1000.0) / 10.0, 0, 100);
            // Offset from center (1500 = 0)
            CenterOffset = Math.Clamp((rawValue - 1500.0) / 10.0, -50, 50);
        }

        OnPropertyChanged(nameof(TextColor));
    }
}
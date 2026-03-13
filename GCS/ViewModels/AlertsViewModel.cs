using GCS.Core.Domain;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace GCS.ViewModels;

public class AlertsViewModel : ViewModelBase
{
    private bool _hasActiveAlerts;
    private AlertSeverity _highestSeverity = AlertSeverity.Info;
    private int _activeAlertCount;
    private string _latestMessage = "";
    private string _latestMessageColor = "#8B949E";

    // Status indicators
    private bool _linkAlive;
    private bool _attitudeFresh;
    private bool _positionFresh;
    private bool _isArmed;
    private float _batteryVoltage;
    private int _batteryPercent;
    private byte _gpsFixType;
    private byte _gpsSatellites;
    private string _gpsFixString = "NO GPS";

    public ObservableCollection<AlertItemViewModel> ActiveAlerts { get; } = new();
    public ObservableCollection<AlertHistoryItem> AlertHistory { get; } = new();

    private const int MaxHistoryItems = 50;

    #region Status Properties

    public bool LinkAlive
    {
        get => _linkAlive;
        set => SetProperty(ref _linkAlive, value);
    }

    public bool AttitudeFresh
    {
        get => _attitudeFresh;
        set => SetProperty(ref _attitudeFresh, value);
    }

    public bool PositionFresh
    {
        get => _positionFresh;
        set => SetProperty(ref _positionFresh, value);
    }

    public bool IsArmed
    {
        get => _isArmed;
        set
        {
            if (SetProperty(ref _isArmed, value))
            {
                OnPropertyChanged(nameof(ArmedStatusText));
                OnPropertyChanged(nameof(ArmedStatusColor));
            }
        }
    }

    public float BatteryVoltage
    {
        get => _batteryVoltage;
        set
        {
            if (SetProperty(ref _batteryVoltage, value))
                OnPropertyChanged(nameof(BatteryStatusColor));
        }
    }

    public int BatteryPercent
    {
        get => _batteryPercent;
        set
        {
            if (SetProperty(ref _batteryPercent, value))
                OnPropertyChanged(nameof(BatteryStatusColor));
        }
    }

    public byte GpsFixType
    {
        get => _gpsFixType;
        set
        {
            if (SetProperty(ref _gpsFixType, value))
                OnPropertyChanged(nameof(GpsStatusColor));
        }
    }

    public byte GpsSatellites
    {
        get => _gpsSatellites;
        set => SetProperty(ref _gpsSatellites, value);
    }

    public string GpsFixString
    {
        get => _gpsFixString;
        set => SetProperty(ref _gpsFixString, value);
    }

    // Derived display properties
    public string ArmedStatusText => IsArmed ? "ARMED" : "DISARMED";
    public string ArmedStatusColor => IsArmed ? "#F85149" : "#3FB950";

    public string BatteryStatusColor =>
        BatteryPercent <= 20 ? "#F85149" :
        BatteryPercent <= 40 ? "#FF9500" :
        "#3FB950";

    public string GpsStatusColor =>
        GpsFixType >= 3 ? "#3FB950" :
        GpsFixType >= 2 ? "#FF9500" :
        "#F85149";

    #endregion

    #region Alert Properties

    public bool HasActiveAlerts
    {
        get => _hasActiveAlerts;
        private set => SetProperty(ref _hasActiveAlerts, value);
    }

    public int ActiveAlertCount
    {
        get => _activeAlertCount;
        private set => SetProperty(ref _activeAlertCount, value);
    }

    public AlertSeverity HighestSeverity
    {
        get => _highestSeverity;
        private set => SetProperty(ref _highestSeverity, value);
    }

    public string SeverityColor => HighestSeverity switch
    {
        AlertSeverity.Critical => "#F85149",
        AlertSeverity.Warning => "#FF9500",
        _ => "#3FB950"
    };

    public string LatestMessage
    {
        get => _latestMessage;
        private set => SetProperty(ref _latestMessage, value);
    }

    public string LatestMessageColor
    {
        get => _latestMessageColor;
        private set => SetProperty(ref _latestMessageColor, value);
    }

    #endregion

    /// <summary>
    /// Called from MainViewModel when health-based alerts change.
    /// </summary>
    public void UpdateAlerts(IReadOnlyList<AlertState> alerts)
    {
        ActiveAlerts.Clear();

        var active = alerts.Where(a => a.Active).ToList();

        foreach (var alert in active)
        {
            ActiveAlerts.Add(new AlertItemViewModel(alert));
        }

        HasActiveAlerts = active.Any();
        ActiveAlertCount = active.Count;

        HighestSeverity = active.Any()
            ? active.Max(a => a.Severity)
            : AlertSeverity.Info;

        OnPropertyChanged(nameof(SeverityColor));
    }

    /// <summary>
    /// Called when an autopilot STATUSTEXT message arrives.
    /// Shows in the ticker and adds to history.
    /// </summary>
    public void OnAutopilotMessage(AutopilotMessage message)
    {
        LatestMessage = message.Text;
        LatestMessageColor = message.Severity switch
        {
            AutopilotMessageSeverity.Critical => "#F85149",
            AutopilotMessageSeverity.Error => "#F85149",
            AutopilotMessageSeverity.Warning => "#FF9500",
            _ => "#E6EDF3"
        };

        AlertHistory.Insert(0, new AlertHistoryItem(
            message.TimestampUtc.ToLocalTime(),
            message.Severity,
            message.Text));

        while (AlertHistory.Count > MaxHistoryItems)
            AlertHistory.RemoveAt(AlertHistory.Count - 1);
    }

    /// <summary>
    /// Called from MainViewModel on each vehicle state update.
    /// </summary>
    public void UpdateFromTelemetry(
        bool linkAlive, bool attitudeFresh, bool positionFresh,
        bool isArmed, float voltage, int batteryPercent,
        byte gpsFixType, byte gpsSatellites, string gpsFixString)
    {
        LinkAlive = linkAlive;
        AttitudeFresh = attitudeFresh;
        PositionFresh = positionFresh;
        IsArmed = isArmed;
        BatteryVoltage = voltage;
        BatteryPercent = batteryPercent;
        GpsFixType = gpsFixType;
        GpsSatellites = gpsSatellites;
        GpsFixString = gpsFixString;
    }
}

public class AlertItemViewModel : ViewModelBase
{
    public AlertType Type { get; }
    public AlertSeverity Severity { get; }
    public string Message { get; }
    public string Timestamp { get; }
    public string SeverityColor { get; }
    public string SeverityIcon { get; }

    public AlertItemViewModel(AlertState alert)
    {
        Type = alert.Type;
        Severity = alert.Severity;
        Timestamp = alert.TimestampUtc.ToLocalTime().ToString("HH:mm:ss");

        Message = alert.Type switch
        {
            AlertType.LinkLost => "LINK LOST — No heartbeat",
            AlertType.AttitudeStale => "ATTITUDE STALE — No IMU data",
            AlertType.PositionStale => "POSITION STALE — No GPS data",
            _ => alert.Type.ToString()
        };

        SeverityColor = alert.Severity switch
        {
            AlertSeverity.Critical => "#F85149",
            AlertSeverity.Warning => "#FF9500",
            _ => "#58A6FF"
        };

        SeverityIcon = alert.Severity switch
        {
            AlertSeverity.Critical => "⚠",
            AlertSeverity.Warning => "⚡",
            _ => "ℹ"
        };
    }
}

public class AlertHistoryItem
{
    public DateTime Timestamp { get; }
    public AutopilotMessageSeverity Severity { get; }
    public string Text { get; }
    public string TimeString { get; }
    public string Color { get; }

    public AlertHistoryItem(DateTime timestamp, AutopilotMessageSeverity severity, string text)
    {
        Timestamp = timestamp;
        Severity = severity;
        Text = text;
        TimeString = timestamp.ToString("HH:mm:ss");
        Color = severity switch
        {
            AutopilotMessageSeverity.Critical => "#F85149",
            AutopilotMessageSeverity.Error => "#F85149",
            AutopilotMessageSeverity.Warning => "#FF9500",
            _ => "#8B949E"
        };
    }
}
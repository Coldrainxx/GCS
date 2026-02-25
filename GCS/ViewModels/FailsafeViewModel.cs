using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;

namespace GCS.ViewModels;

public class FailsafeViewModel : ViewModelBase
{
    private readonly Func<string, float, Task>? _setParamFunc;
    private readonly Func<string, Task>? _requestParamFunc;

    #region Failsafe Status Properties

    // RC Failsafe (FS_THR_ENABLE)
    private bool _rcFailsafeEnabled;
    private bool _rcFailsafeTriggered;
    private float _rcFailsafeThreshold = 950;

    // Battery Failsafe (FS_BATT_ENABLE / BATT_FS_LOW_ACT)
    private bool _batteryFailsafeEnabled;
    private bool _batteryFailsafeTriggered;
    private float _batteryFailsafeVoltage = 10.5f;

    // GPS Failsafe (FS_GCS_ENABLE for ArduPlane uses GCS, GPS is automatic)
    private bool _gpsFailsafeEnabled = true; // GPS failsafe is usually always on
    private bool _gpsFailsafeTriggered;

    // GCS Failsafe (FS_GCS_ENABLE)
    private bool _gcsFailsafeEnabled;
    private bool _gcsFailsafeTriggered;
    private float _gcsFailsafeTimeout = 5;

    // General
    private bool _isConnected;
    private bool _isLoading;
    private string _statusMessage = "Not connected";

    public bool RcFailsafeEnabled
    {
        get => _rcFailsafeEnabled;
        set { if (SetProperty(ref _rcFailsafeEnabled, value)) OnPropertyChanged(nameof(RcStatusText)); }
    }

    public bool RcFailsafeTriggered
    {
        get => _rcFailsafeTriggered;
        set { if (SetProperty(ref _rcFailsafeTriggered, value)) { OnPropertyChanged(nameof(RcStatusText)); OnPropertyChanged(nameof(RcStatusColor)); } }
    }

    public float RcFailsafeThreshold
    {
        get => _rcFailsafeThreshold;
        set => SetProperty(ref _rcFailsafeThreshold, value);
    }

    public bool BatteryFailsafeEnabled
    {
        get => _batteryFailsafeEnabled;
        set { if (SetProperty(ref _batteryFailsafeEnabled, value)) OnPropertyChanged(nameof(BatteryStatusText)); }
    }

    public bool BatteryFailsafeTriggered
    {
        get => _batteryFailsafeTriggered;
        set { if (SetProperty(ref _batteryFailsafeTriggered, value)) { OnPropertyChanged(nameof(BatteryStatusText)); OnPropertyChanged(nameof(BatteryStatusColor)); } }
    }

    public float BatteryFailsafeVoltage
    {
        get => _batteryFailsafeVoltage;
        set => SetProperty(ref _batteryFailsafeVoltage, value);
    }

    public bool GpsFailsafeEnabled
    {
        get => _gpsFailsafeEnabled;
        set { if (SetProperty(ref _gpsFailsafeEnabled, value)) OnPropertyChanged(nameof(GpsStatusText)); }
    }

    public bool GpsFailsafeTriggered
    {
        get => _gpsFailsafeTriggered;
        set { if (SetProperty(ref _gpsFailsafeTriggered, value)) { OnPropertyChanged(nameof(GpsStatusText)); OnPropertyChanged(nameof(GpsStatusColor)); } }
    }

    public bool GcsFailsafeEnabled
    {
        get => _gcsFailsafeEnabled;
        set { if (SetProperty(ref _gcsFailsafeEnabled, value)) OnPropertyChanged(nameof(GcsStatusText)); }
    }

    public bool GcsFailsafeTriggered
    {
        get => _gcsFailsafeTriggered;
        set { if (SetProperty(ref _gcsFailsafeTriggered, value)) { OnPropertyChanged(nameof(GcsStatusText)); OnPropertyChanged(nameof(GcsStatusColor)); } }
    }

    public float GcsFailsafeTimeout
    {
        get => _gcsFailsafeTimeout;
        set => SetProperty(ref _gcsFailsafeTimeout, value);
    }

    public bool IsConnected
    {
        get => _isConnected;
        set { if (SetProperty(ref _isConnected, value)) CommandManager.InvalidateRequerySuggested(); }
    }

    public bool IsLoading
    {
        get => _isLoading;
        set => SetProperty(ref _isLoading, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    #endregion

    #region Status Display Properties

    public string RcStatusText => RcFailsafeTriggered ? "TRIGGERED!" : (RcFailsafeEnabled ? "ENABLED" : "DISABLED");
    public string RcStatusColor => RcFailsafeTriggered ? "#F85149" : (RcFailsafeEnabled ? "#3FB950" : "#8B949E");

    public string BatteryStatusText => BatteryFailsafeTriggered ? "TRIGGERED!" : (BatteryFailsafeEnabled ? "ENABLED" : "DISABLED");
    public string BatteryStatusColor => BatteryFailsafeTriggered ? "#F85149" : (BatteryFailsafeEnabled ? "#3FB950" : "#8B949E");

    public string GpsStatusText => GpsFailsafeTriggered ? "TRIGGERED!" : (GpsFailsafeEnabled ? "ENABLED" : "DISABLED");
    public string GpsStatusColor => GpsFailsafeTriggered ? "#F85149" : (GpsFailsafeEnabled ? "#3FB950" : "#8B949E");

    public string GcsStatusText => GcsFailsafeTriggered ? "TRIGGERED!" : (GcsFailsafeEnabled ? "ENABLED" : "DISABLED");
    public string GcsStatusColor => GcsFailsafeTriggered ? "#F85149" : (GcsFailsafeEnabled ? "#3FB950" : "#8B949E");

    #endregion

    #region Commands

    public ICommand ToggleRcFailsafeCommand { get; }
    public ICommand ToggleBatteryFailsafeCommand { get; }
    public ICommand ToggleGcsFailsafeCommand { get; }
    public ICommand RefreshCommand { get; }

    #endregion

    public FailsafeViewModel() : this(null, null) { }

    public FailsafeViewModel(Func<string, float, Task>? setParamFunc, Func<string, Task>? requestParamFunc)
    {
        _setParamFunc = setParamFunc;
        _requestParamFunc = requestParamFunc;

        ToggleRcFailsafeCommand = new RelayCommand(async () => await ToggleRcFailsafe(), () => IsConnected && !IsLoading);
        ToggleBatteryFailsafeCommand = new RelayCommand(async () => await ToggleBatteryFailsafe(), () => IsConnected && !IsLoading);
        ToggleGcsFailsafeCommand = new RelayCommand(async () => await ToggleGcsFailsafe(), () => IsConnected && !IsLoading);
        RefreshCommand = new RelayCommand(async () => await RefreshFailsafeParams(), () => IsConnected && !IsLoading);
    }

    #region Parameter Handling

    public void OnParameterReceived(string paramId, float value)
    {
        Application.Current?.Dispatcher?.BeginInvoke(() =>
        {
            Debug.WriteLine($"[Failsafe] Param received: {paramId} = {value}");

            switch (paramId.ToUpperInvariant())
            {
                // ArduPlane throttle failsafe
                case "FS_THR_ENABLE":
                case "THR_FAILSAFE":
                    RcFailsafeEnabled = value > 0;
                    break;
                case "FS_THR_VALUE":
                case "THR_FS_VALUE":
                    RcFailsafeThreshold = value;
                    break;

                // Battery failsafe
                case "FS_BATT_ENABLE":
                case "BATT_FS_LOW_ACT":
                    BatteryFailsafeEnabled = value > 0;
                    break;
                case "FS_BATT_VOLTAGE":
                case "BATT_LOW_VOLT":
                    BatteryFailsafeVoltage = value;
                    break;

                // GCS failsafe
                case "FS_GCS_ENABLE":
                case "FS_GCS_ENABL": // Some versions truncate
                    GcsFailsafeEnabled = value > 0;
                    break;
                case "FS_GCS_TIMEOUT":
                    GcsFailsafeTimeout = value;
                    break;

                // GPS failsafe (usually always enabled)
                case "FS_EKF_ACTION":
                case "GPS_TYPE":
                    GpsFailsafeEnabled = value > 0;
                    break;
            }

            StatusMessage = "Parameters loaded";
        });
    }

    public void OnSysStatusReceived(uint onboardControlSensorsHealth, uint onboardControlSensorsEnabled)
    {
        // MAV_SYS_STATUS_SENSOR flags
        const uint GPS = 0x04;
        const uint RC_RECEIVER = 0x10000;
        const uint BATTERY = 0x400000;

        Application.Current?.Dispatcher?.BeginInvoke(() =>
        {
            // Check if sensors are healthy
            GpsFailsafeTriggered = (onboardControlSensorsEnabled & GPS) != 0 && (onboardControlSensorsHealth & GPS) == 0;
            RcFailsafeTriggered = (onboardControlSensorsEnabled & RC_RECEIVER) != 0 && (onboardControlSensorsHealth & RC_RECEIVER) == 0;
            // Battery is usually reported via separate message
        });
    }

    public async Task RefreshFailsafeParams()
    {
        if (_requestParamFunc == null) return;

        IsLoading = true;
        StatusMessage = "Loading parameters...";

        try
        {
            // Request all failsafe-related parameters
            var paramsToRequest = new[]
            {
                "FS_THR_ENABLE", "FS_THR_VALUE",
                "FS_BATT_ENABLE", "FS_BATT_VOLTAGE", "BATT_FS_LOW_ACT", "BATT_LOW_VOLT",
                "FS_GCS_ENABLE", "FS_GCS_TIMEOUT",
                "THR_FAILSAFE", "THR_FS_VALUE"
            };

            foreach (var param in paramsToRequest)
            {
                await _requestParamFunc(param);
                await Task.Delay(50); // Small delay between requests
            }

            StatusMessage = "Parameters loaded";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
            Debug.WriteLine($"[Failsafe] Refresh error: {ex.Message}");
        }
        finally
        {
            IsLoading = false;
        }
    }

    #endregion

    #region Toggle Commands

    private async Task ToggleRcFailsafe()
    {
        if (_setParamFunc == null) return;

        try
        {
            IsLoading = true;
            float newValue = RcFailsafeEnabled ? 0 : 1;
            await _setParamFunc("FS_THR_ENABLE", newValue);
            RcFailsafeEnabled = !RcFailsafeEnabled;
            StatusMessage = $"RC Failsafe {(RcFailsafeEnabled ? "enabled" : "disabled")}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task ToggleBatteryFailsafe()
    {
        if (_setParamFunc == null) return;

        try
        {
            IsLoading = true;
            float newValue = BatteryFailsafeEnabled ? 0 : 1;
            await _setParamFunc("FS_BATT_ENABLE", newValue);
            BatteryFailsafeEnabled = !BatteryFailsafeEnabled;
            StatusMessage = $"Battery Failsafe {(BatteryFailsafeEnabled ? "enabled" : "disabled")}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task ToggleGcsFailsafe()
    {
        if (_setParamFunc == null) return;

        try
        {
            IsLoading = true;
            float newValue = GcsFailsafeEnabled ? 0 : 1;
            await _setParamFunc("FS_GCS_ENABLE", newValue);
            GcsFailsafeEnabled = !GcsFailsafeEnabled;
            StatusMessage = $"GCS Failsafe {(GcsFailsafeEnabled ? "enabled" : "disabled")}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    #endregion

    public void UpdateConnectionState(bool isConnected)
    {
        IsConnected = isConnected;
        if (isConnected)
        {
            StatusMessage = "Connected - Click Refresh";
        }
        else
        {
            StatusMessage = "Not connected";
            // Reset triggered states
            RcFailsafeTriggered = false;
            BatteryFailsafeTriggered = false;
            GcsFailsafeTriggered = false;
        }
    }
}
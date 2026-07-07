using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using GCS.Parameters;

namespace GCS.ViewModels;

/// <summary>
/// FailSafe setup, laid out like the Mission Planner Plane screen:
/// Battery (low voltage / reserved mAh / low timer / action),
/// Radio (FS PWM / throttle failsafe) and GCS (heartbeat / short / long).
/// Values are written to the vehicle live as they change (like Mission Planner).
/// </summary>
public class FailsafeViewModel : ViewModelBase
{
    private readonly Func<string, float, Task>? _setParamFunc;
    private readonly Func<string, Task>? _requestParamFunc;

    // Suppresses the live write-back while applying values coming FROM the vehicle.
    private bool _applying;

    private float _battLowVolt;
    private float _battLowMah;
    private float _battLowTimer;
    private double _battAction;

    private float _fsPwm = 1100;
    private bool _throttleFailsafe;

    private bool _gcsFailsafe;
    private bool _failsafeShort;
    private bool _failsafeLong;

    private bool _isConnected;
    private bool _isLoading;
    private string _statusMessage = "Not connected";

    public IReadOnlyList<ParamOption> BatteryActions { get; } =
        ParameterOptions.For("BATT_FS_LOW_ACT") ?? Array.Empty<ParamOption>();

    // ── Battery ──────────────────────────────────────────────────────
    public float BattLowVolt { get => _battLowVolt; set => SetFloat(ref _battLowVolt, value, nameof(BattLowVolt), "BATT_LOW_VOLT"); }
    public float BattLowMah { get => _battLowMah; set => SetFloat(ref _battLowMah, value, nameof(BattLowMah), "BATT_LOW_MAH"); }
    public float BattLowTimer { get => _battLowTimer; set => SetFloat(ref _battLowTimer, value, nameof(BattLowTimer), "BATT_LOW_TIMER"); }
    public double BattAction { get => _battAction; set => SetDouble(ref _battAction, value, nameof(BattAction), "BATT_FS_LOW_ACT"); }

    // ── Radio ────────────────────────────────────────────────────────
    public float FsPwm { get => _fsPwm; set => SetFloat(ref _fsPwm, value, nameof(FsPwm), "THR_FS_VALUE"); }
    public bool ThrottleFailsafe { get => _throttleFailsafe; set => SetBool(ref _throttleFailsafe, value, nameof(ThrottleFailsafe), "THR_FAILSAFE"); }

    // ── GCS ──────────────────────────────────────────────────────────
    public bool GcsFailsafe { get => _gcsFailsafe; set => SetBool(ref _gcsFailsafe, value, nameof(GcsFailsafe), "FS_GCS_ENABL"); }
    public bool FailsafeShort { get => _failsafeShort; set => SetBool(ref _failsafeShort, value, nameof(FailsafeShort), "FS_SHORT_ACTN"); }
    public bool FailsafeLong { get => _failsafeLong; set => SetBool(ref _failsafeLong, value, nameof(FailsafeLong), "FS_LONG_ACTN"); }

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

    public ICommand RefreshCommand { get; }

    public FailsafeViewModel() : this(null, null) { }

    public FailsafeViewModel(Func<string, float, Task>? setParamFunc, Func<string, Task>? requestParamFunc)
    {
        _setParamFunc = setParamFunc;
        _requestParamFunc = requestParamFunc;
        RefreshCommand = new AsyncRelayCommand(RefreshFailsafeParams, () => IsConnected && !IsLoading);
    }

    // ── Live-write helpers ───────────────────────────────────────────

    private void SetFloat(ref float field, float value, string propName, string paramId)
    {
        if (Math.Abs(field - value) < 1e-6f) return;
        field = value;
        OnPropertyChanged(propName);
        if (!_applying) Write(paramId, value);
    }

    private void SetDouble(ref double field, double value, string propName, string paramId)
    {
        if (Math.Abs(field - value) < 1e-9) return;
        field = value;
        OnPropertyChanged(propName);
        if (!_applying) Write(paramId, (float)value);
    }

    private void SetBool(ref bool field, bool value, string propName, string paramId)
    {
        if (field == value) return;
        field = value;
        OnPropertyChanged(propName);
        if (!_applying) Write(paramId, value ? 1f : 0f);
    }

    private async void Write(string paramId, float value)
    {
        if (_setParamFunc == null) return;
        try
        {
            await _setParamFunc(paramId, value);
            StatusMessage = $"{paramId} = {value:0.###}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Write error: {ex.Message}";
        }
    }

    public void OnParameterReceived(string paramId, float value)
    {
        Application.Current?.Dispatcher?.BeginInvoke(() =>
        {
            _applying = true;
            try
            {
                switch (paramId.ToUpperInvariant())
                {
                    case "BATT_LOW_VOLT": BattLowVolt = value; break;
                    case "BATT_LOW_MAH": BattLowMah = value; break;
                    case "BATT_LOW_TIMER": BattLowTimer = value; break;
                    case "BATT_FS_LOW_ACT": BattAction = value; break;
                    case "THR_FS_VALUE": FsPwm = value; break;
                    case "THR_FAILSAFE": ThrottleFailsafe = value > 0; break;
                    case "FS_GCS_ENABL":
                    case "FS_GCS_ENABLE": GcsFailsafe = value > 0; break;
                    case "FS_SHORT_ACTN": FailsafeShort = value > 0; break;
                    case "FS_LONG_ACTN": FailsafeLong = value > 0; break;
                }
            }
            finally { _applying = false; }
        });
    }

    public async Task RefreshFailsafeParams()
    {
        if (_requestParamFunc == null) return;

        IsLoading = true;
        StatusMessage = "Loading failsafe parameters…";
        try
        {
            foreach (var p in new[]
            {
                "BATT_LOW_VOLT", "BATT_LOW_MAH", "BATT_LOW_TIMER", "BATT_FS_LOW_ACT",
                "THR_FS_VALUE", "THR_FAILSAFE",
                "FS_GCS_ENABL", "FS_SHORT_ACTN", "FS_LONG_ACTN"
            })
            {
                await _requestParamFunc(p);
                await Task.Delay(40);
            }
            StatusMessage = "Failsafe parameters loaded";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
            Debug.WriteLine($"[Failsafe] Refresh error: {ex.Message}");
        }
        finally { IsLoading = false; }
    }

    public void UpdateConnectionState(bool isConnected)
    {
        IsConnected = isConnected;
        StatusMessage = isConnected ? "Connected — click Refresh to read values" : "Not connected";
    }
}

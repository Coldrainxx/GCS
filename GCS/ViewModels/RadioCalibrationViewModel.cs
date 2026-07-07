using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using GCS.Core.Mavlink.Messages;

namespace GCS.ViewModels;

/// <summary>
/// RC transmitter calibration: shows live channel PWM, captures min/max while
/// the pilot sweeps every stick and switch, then writes RCn_MIN/MAX/TRIM.
/// </summary>
public sealed class RadioCalibrationViewModel : ViewModelBase
{
    private const int ChannelCount = 8;

    private readonly Func<string, float, Task> _setParam;
    private readonly Func<string, Task> _requestParam;

    public ObservableCollection<RcCalChannel> Channels { get; } = new();

    private bool _isConnected;
    public bool IsConnected
    {
        get => _isConnected;
        private set { if (SetProperty(ref _isConnected, value)) CommandManager.InvalidateRequerySuggested(); }
    }

    private bool _isCalibrating;
    public bool IsCalibrating
    {
        get => _isCalibrating;
        private set { if (SetProperty(ref _isCalibrating, value)) CommandManager.InvalidateRequerySuggested(); }
    }

    private string _status = "Not connected";
    public string Status
    {
        get => _status;
        set => SetProperty(ref _status, value);
    }

    public ICommand StartCommand { get; }
    public ICommand FinishCommand { get; }
    public ICommand CancelCommand { get; }

    public RadioCalibrationViewModel(Func<string, float, Task> setParam, Func<string, Task> requestParam)
    {
        _setParam = setParam;
        _requestParam = requestParam;

        for (int i = 1; i <= ChannelCount; i++)
            Channels.Add(new RcCalChannel(i));

        StartCommand = new RelayCommand(StartCalibration, () => IsConnected && !IsCalibrating);
        FinishCommand = new AsyncRelayCommand(FinishAsync, () => IsConnected && IsCalibrating);
        CancelCommand = new RelayCommand(() => { IsCalibrating = false; Status = "Calibration cancelled"; },
                                         () => IsCalibrating);
    }

    public void SetConnected(bool connected)
    {
        IsConnected = connected;
        if (!connected) { IsCalibrating = false; Status = "Not connected"; }
    }

    private void StartCalibration()
    {
        foreach (var c in Channels) c.BeginCapture();
        IsCalibrating = true;
        Status = "Move ALL sticks and switches through their full range…";
    }

    private async Task FinishAsync()
    {
        IsCalibrating = false;
        Status = "Writing RC calibration…";
        foreach (var c in Channels)
        {
            if (!c.HasValidRange) continue;
            await _setParam($"RC{c.Number}_MIN", c.CapturedMin);
            await Task.Delay(20);
            await _setParam($"RC{c.Number}_MAX", c.CapturedMax);
            await Task.Delay(20);
            await _setParam($"RC{c.Number}_TRIM", c.Pwm);   // current (neutral) position
            await Task.Delay(20);
        }
        Status = "RC calibration written. Center sticks were saved as trim.";
    }

    public void OnRcChannels(RcChannelsData data)
    {
        var values = data.ToArray();
        Application.Current?.Dispatcher?.BeginInvoke(() =>
        {
            for (int i = 0; i < Channels.Count && i < values.Length; i++)
                Channels[i].Observe(values[i], IsCalibrating);
        });
    }

    public void OnParameter(string name, float value) { /* live PWM drives the UI; no param read-back needed */ }
}

public sealed class RcCalChannel : ViewModelBase
{
    public int Number { get; }
    public string Label => $"CH{Number}";

    public RcCalChannel(int number) => Number = number;

    private ushort _pwm;
    public ushort Pwm
    {
        get => _pwm;
        private set { if (SetProperty(ref _pwm, value)) { OnPropertyChanged(nameof(BarFraction)); OnPropertyChanged(nameof(PwmText)); } }
    }

    public string PwmText => _pwm is 0 or 65535 ? "—" : _pwm.ToString();

    private ushort _min = 1500;
    public ushort CapturedMin
    {
        get => _min;
        private set { if (SetProperty(ref _min, value)) OnPropertyChanged(nameof(RangeText)); }
    }

    private ushort _max = 1500;
    public ushort CapturedMax
    {
        get => _max;
        private set { if (SetProperty(ref _max, value)) OnPropertyChanged(nameof(RangeText)); }
    }

    public bool HasValidRange => CapturedMax - CapturedMin >= 200;
    public string RangeText => $"{CapturedMin} – {CapturedMax}";

    /// <summary>0..1 position of the live PWM across the 1000-2000 span, for the bar.</summary>
    public double BarFraction => _pwm is 0 or 65535 ? 0.5 : Math.Clamp((_pwm - 1000.0) / 1000.0, 0, 1);

    public void BeginCapture()
    {
        CapturedMin = _pwm is 0 or 65535 ? (ushort)1500 : _pwm;
        CapturedMax = CapturedMin;
    }

    public void Observe(ushort pwm, bool capturing)
    {
        if (pwm is 0 or 65535) return;
        Pwm = pwm;
        if (capturing)
        {
            if (pwm < CapturedMin) CapturedMin = pwm;
            if (pwm > CapturedMax) CapturedMax = pwm;
        }
    }
}

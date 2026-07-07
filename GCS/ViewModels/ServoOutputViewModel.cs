using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using GCS.Core.Mavlink.Messages;
using GCS.Parameters;

namespace GCS.ViewModels;

/// <summary>
/// Servo / motor output setup: shows the live PWM going to each output
/// (SERVO_OUTPUT_RAW) and lets you assign the output function and reverse it
/// (SERVOn_FUNCTION / SERVOn_REVERSED).
/// </summary>
public sealed class ServoOutputViewModel : ViewModelBase
{
    private const int OutputCount = 14;

    private readonly Func<string, float, Task> _setParam;
    private readonly Func<string, Task> _requestParam;

    // Common SERVOn_FUNCTION values (QuadPlane-relevant subset).
    public IReadOnlyList<ParamOption> Functions { get; } = new ParamOption[]
    {
        new(0, "Disabled"),
        new(1, "RCPassThru"),
        new(4, "Aileron"),
        new(19, "Elevator"),
        new(21, "Rudder"),
        new(26, "Ground Steering"),
        new(77, "Elevon Left"),
        new(78, "Elevon Right"),
        new(79, "VTail Left"),
        new(80, "VTail Right"),
        new(2, "Flap"),
        new(3, "Flap Auto"),
        new(24, "Flaperon Left"),
        new(25, "Flaperon Right"),
        new(70, "Throttle"),
        new(73, "Throttle Left"),
        new(74, "Throttle Right"),
        new(33, "Motor 1"),
        new(34, "Motor 2"),
        new(35, "Motor 3"),
        new(36, "Motor 4"),
        new(37, "Motor 5"),
        new(38, "Motor 6"),
        new(39, "Motor 7"),
        new(40, "Motor 8"),
    };

    public ObservableCollection<ServoChannel> Outputs { get; } = new();

    private bool _isConnected;
    public bool IsConnected
    {
        get => _isConnected;
        private set { if (SetProperty(ref _isConnected, value)) CommandManager.InvalidateRequerySuggested(); }
    }

    private string _status = "Not connected";
    public string Status
    {
        get => _status;
        set => SetProperty(ref _status, value);
    }

    public ICommand RefreshCommand { get; }

    public ServoOutputViewModel(Func<string, float, Task> setParam, Func<string, Task> requestParam)
    {
        _setParam = setParam;
        _requestParam = requestParam;

        for (int i = 1; i <= OutputCount; i++)
            Outputs.Add(new ServoChannel(i, WriteChannelParam));

        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => IsConnected);
    }

    public void SetConnected(bool connected)
    {
        IsConnected = connected;
        if (!connected) Status = "Not connected";
    }

    private void WriteChannelParam(int number, string suffix, float value) =>
        _ = _setParam($"SERVO{number}_{suffix}", value);

    public async Task RefreshAsync()
    {
        Status = "Reading output functions…";
        foreach (var ch in Outputs)
        {
            await _requestParam($"SERVO{ch.Number}_FUNCTION");
            await _requestParam($"SERVO{ch.Number}_REVERSED");
            await _requestParam($"SERVO{ch.Number}_MIN");
            await _requestParam($"SERVO{ch.Number}_MAX");
            await _requestParam($"SERVO{ch.Number}_TRIM");
            await Task.Delay(20);
        }
        Status = "Output functions read";
    }

    public void OnServoOutput(ServoOutputData data)
    {
        var values = data.ToArray();
        Application.Current?.Dispatcher?.BeginInvoke(() =>
        {
            for (int i = 0; i < Outputs.Count && i < values.Length; i++)
                Outputs[i].Pwm = values[i];
        });
    }

    public void OnParameter(string name, float value)
    {
        if (!name.StartsWith("SERVO", StringComparison.OrdinalIgnoreCase)) return;

        // SERVO<n>_<SUFFIX>
        int us = name.IndexOf('_');
        if (us <= 5) return;
        if (!int.TryParse(name.AsSpan(5, us - 5), out int n) || n < 1 || n > Outputs.Count) return;
        string suffix = name[(us + 1)..].ToUpperInvariant();

        Application.Current?.Dispatcher?.BeginInvoke(() =>
        {
            var ch = Outputs[n - 1];
            ch.Applying = true;
            try
            {
                switch (suffix)
                {
                    case "FUNCTION": ch.FunctionValue = value; break;
                    case "REVERSED": ch.Reversed = value > 0; break;
                    case "MIN": ch.Min = (int)value; break;
                    case "MAX": ch.Max = (int)value; break;
                    case "TRIM": ch.Trim = (int)value; break;
                }
            }
            finally { ch.Applying = false; }
        });
    }
}

public sealed class ServoChannel : ViewModelBase
{
    private readonly Action<int, string, float> _write;

    public int Number { get; }
    public string Label => $"SERVO {Number}";

    /// <summary>Set while applying values from the vehicle, to suppress write-back.</summary>
    public bool Applying { get; set; }

    public ServoChannel(int number, Action<int, string, float> write)
    {
        Number = number;
        _write = write;
    }

    private double _functionValue;
    public double FunctionValue
    {
        get => _functionValue;
        set { if (SetProperty(ref _functionValue, value) && !Applying) _write(Number, "FUNCTION", (float)value); }
    }

    private bool _reversed;
    public bool Reversed
    {
        get => _reversed;
        set { if (SetProperty(ref _reversed, value) && !Applying) _write(Number, "REVERSED", value ? 1f : 0f); }
    }

    private int _min;
    public int Min
    {
        get => _min;
        set { if (SetProperty(ref _min, value) && !Applying) _write(Number, "MIN", value); }
    }

    private int _max;
    public int Max
    {
        get => _max;
        set { if (SetProperty(ref _max, value) && !Applying) _write(Number, "MAX", value); }
    }

    private int _trim;
    public int Trim
    {
        get => _trim;
        set { if (SetProperty(ref _trim, value) && !Applying) _write(Number, "TRIM", value); }
    }

    private ushort _pwm;
    public ushort Pwm
    {
        get => _pwm;
        set { if (SetProperty(ref _pwm, value)) OnPropertyChanged(nameof(PwmText)); }
    }

    public string PwmText => _pwm is 0 or 65535 ? "—" : _pwm.ToString();
}

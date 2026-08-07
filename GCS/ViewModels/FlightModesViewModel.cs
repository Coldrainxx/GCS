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
/// Flight-mode switch setup: maps the 6 PWM bands of the mode channel
/// (FLTMODE_CH) to flight modes (FLTMODE1..6), and highlights the band the
/// transmitter switch is currently in.
/// </summary>
public sealed class FlightModesViewModel : ViewModelBase
{
    private readonly Func<string, float, Task> _setParam;
    private readonly Func<string, Task> _requestParam;

    // ArduPilot 6-position switch PWM boundaries.
    private static readonly (int Lo, int Hi, string Text)[] Bands =
    {
        (0,    1230, "≤ 1230"),
        (1231, 1360, "1231 – 1360"),
        (1361, 1490, "1361 – 1490"),
        (1491, 1620, "1491 – 1620"),
        (1621, 1749, "1621 – 1749"),
        (1750, 9999, "≥ 1750"),
    };

    /// <summary>
    /// Choices offered for each switch position.
    ///
    /// FLTMODE1-6 are named the same on every vehicle, but the numbers they hold
    /// mean different modes — so the list has to come from the connected vehicle's
    /// own table. Offering plane modes on a copter would write a number that
    /// selects something else entirely.
    /// </summary>
    public ObservableCollection<ParamOption> Modes { get; } =
        new(ParameterOptions.For("INITIAL_MODE") ?? Array.Empty<ParamOption>());

    private GCS.Core.Mavlink.VehicleKind _vehicleKind = GCS.Core.Mavlink.VehicleKind.Unknown;

    public void SetVehicleKind(GCS.Core.Mavlink.VehicleKind kind)
    {
        if (kind == _vehicleKind || kind == GCS.Core.Mavlink.VehicleKind.Unknown) return;
        _vehicleKind = kind;

        if (kind == GCS.Core.Mavlink.VehicleKind.Plane) return;   // already the plane list

        Modes.Clear();
        foreach (var (name, mode) in GCS.Core.Mavlink.ArdupilotFlightModes.ModesFor(kind))
            Modes.Add(new ParamOption(mode, name));

        // Re-resolve each slot against the new list, so a value already read from
        // the vehicle keeps showing the right name rather than an empty box.
        foreach (var slot in Slots) slot.RefreshSelection();
    }

    public ObservableCollection<FlightModeSlot> Slots { get; } = new();

    private int _modeChannel = 8;
    public int ModeChannel
    {
        get => _modeChannel;
        set => SetProperty(ref _modeChannel, value);
    }

    private int _currentPwm;
    public int CurrentPwm
    {
        get => _currentPwm;
        private set { if (SetProperty(ref _currentPwm, value)) UpdateActiveSlot(); }
    }

    private bool _isConnected;
    public bool IsConnected
    {
        get => _isConnected;
        private set { if (SetProperty(ref _isConnected, value)) System.Windows.Input.CommandManager.InvalidateRequerySuggested(); }
    }

    private string _status = "Not connected";
    public string Status
    {
        get => _status;
        set => SetProperty(ref _status, value);
    }

    public ICommand RefreshCommand { get; }
    public ICommand WriteCommand { get; }

    public FlightModesViewModel(Func<string, float, Task> setParam, Func<string, Task> requestParam)
    {
        _setParam = setParam;
        _requestParam = requestParam;

        for (int i = 1; i <= 6; i++)
            Slots.Add(new FlightModeSlot(i, Bands[i - 1].Text));

        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => IsConnected);
        WriteCommand = new AsyncRelayCommand(WriteAsync, () => IsConnected);
    }

    public void SetConnected(bool connected)
    {
        IsConnected = connected;
        if (!connected) Status = "Not connected";
    }

    public async Task RefreshAsync()
    {
        Status = "Reading flight-mode setup…";
        await _requestParam("FLTMODE_CH");
        for (int i = 1; i <= 6; i++)
        {
            await _requestParam($"FLTMODE{i}");
            await Task.Delay(25);
        }
        Status = "Read FLTMODE1-6 + channel";
    }

    public async Task WriteAsync()
    {
        Status = "Writing flight modes…";
        await _setParam("FLTMODE_CH", ModeChannel);
        foreach (var slot in Slots)
        {
            await _setParam($"FLTMODE{slot.Number}", (float)slot.ModeValue);
            await Task.Delay(30);
        }
        Status = "Flight modes written";
    }

    public void OnParameter(string name, float value)
    {
        Application.Current?.Dispatcher?.BeginInvoke(() =>
        {
            if (name.Equals("FLTMODE_CH", StringComparison.OrdinalIgnoreCase))
            {
                ModeChannel = (int)value;
                return;
            }
            if (name.StartsWith("FLTMODE", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(name.AsSpan("FLTMODE".Length), out int idx) &&
                idx >= 1 && idx <= 6)
            {
                Slots[idx - 1].ModeValue = (int)value;
            }
        });
    }

    public void OnRcChannels(RcChannelsData data)
    {
        var values = data.ToArray();
        int ch = ModeChannel - 1;
        if (ch < 0 || ch >= values.Length) return;
        ushort pwm = values[ch];
        if (pwm == 0 || pwm == 65535) return;
        Application.Current?.Dispatcher?.BeginInvoke(() => CurrentPwm = pwm);
    }

    private void UpdateActiveSlot()
    {
        int active = -1;
        for (int i = 0; i < Bands.Length; i++)
            if (CurrentPwm >= Bands[i].Lo && CurrentPwm <= Bands[i].Hi) { active = i; break; }
        for (int i = 0; i < Slots.Count; i++)
            Slots[i].IsActive = (i == active);
    }
}

public sealed class FlightModeSlot : ViewModelBase
{
    public int Number { get; }
    public string PwmRange { get; }

    public FlightModeSlot(int number, string pwmRange)
    {
        Number = number;
        PwmRange = pwmRange;
    }

    private double _modeValue;
    /// <summary>The mode number (bound to the ComboBox SelectedValue).</summary>
    public double ModeValue
    {
        get => _modeValue;
        set => SetProperty(ref _modeValue, value);
    }

    /// <summary>
    /// Re-raise the bound value so the ComboBox resolves it against a replaced
    /// choice list; otherwise the box goes blank after the list changes even though
    /// the value read from the vehicle is still correct.
    /// </summary>
    public void RefreshSelection() => OnPropertyChanged(nameof(ModeValue));

    private bool _isActive;
    public bool IsActive
    {
        get => _isActive;
        set => SetProperty(ref _isActive, value);
    }
}

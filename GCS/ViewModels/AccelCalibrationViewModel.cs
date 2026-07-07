using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using GCS.Core.Domain;

namespace GCS.ViewModels;

/// <summary>
/// Accelerometer calibration: board "level" trim plus the interactive 6-point
/// full calibration. Uses MAV_CMD_PREFLIGHT_CALIBRATION (241) to start and
/// MAV_CMD_ACCELCAL_VEHICLE_POS (42429) to advance each orientation; the vehicle
/// drives the prompts, which arrive as STATUSTEXT messages.
/// </summary>
public sealed class AccelCalibrationViewModel : ViewModelBase
{
    private const ushort MAV_CMD_PREFLIGHT_CALIBRATION = 241;
    private const ushort MAV_CMD_ACCELCAL_VEHICLE_POS = 42429;

    // Orientation, position code and instruction for each of the 6 steps.
    private static readonly (int Pos, string Name, string Instruction)[] Steps =
    {
        (1, "LEVEL",      "Place the vehicle LEVEL (as it sits on the ground), then click Next."),
        (2, "LEFT side",  "Place the vehicle on its LEFT side, then click Next."),
        (3, "RIGHT side", "Place the vehicle on its RIGHT side, then click Next."),
        (4, "NOSE DOWN",  "Point the vehicle's NOSE DOWN, then click Next."),
        (5, "NOSE UP",    "Point the vehicle's NOSE UP, then click Next."),
        (6, "on its BACK","Place the vehicle on its BACK (upside-down), then click Next."),
    };

    private readonly Func<ushort, float, float, float, float, float, float, float, Task> _sendCommand;

    public ObservableCollection<string> Log { get; } = new();

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
        private set
        {
            if (SetProperty(ref _isCalibrating, value))
            {
                OnPropertyChanged(nameof(StepText));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    private int _step;                       // 0 = not started; 1..6 = current orientation
    public string StepText => IsCalibrating ? $"Step {_step} of 6 — {Steps[_step - 1].Name}" : "";

    private string _instruction = "Press \"Start Accel Cal\" to begin the 6-point calibration.";
    public string Instruction
    {
        get => _instruction;
        private set => SetProperty(ref _instruction, value);
    }

    private string _status = "Not connected";
    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public ICommand LevelCommand { get; }
    public ICommand StartCommand { get; }
    public ICommand NextCommand { get; }
    public ICommand CancelCommand { get; }

    public AccelCalibrationViewModel(Func<ushort, float, float, float, float, float, float, float, Task> sendCommand)
    {
        _sendCommand = sendCommand;

        LevelCommand = new AsyncRelayCommand(CalibrateLevelAsync, () => IsConnected && !IsCalibrating);
        StartCommand = new AsyncRelayCommand(StartAsync, () => IsConnected && !IsCalibrating);
        NextCommand = new AsyncRelayCommand(NextAsync, () => IsConnected && IsCalibrating);
        CancelCommand = new RelayCommand(Cancel, () => IsCalibrating);
    }

    public void SetConnected(bool connected)
    {
        IsConnected = connected;
        if (!connected) { IsCalibrating = false; _step = 0; Status = "Not connected"; }
    }

    private async Task CalibrateLevelAsync()
    {
        Status = "Calibrating level — keep the vehicle still and level…";
        // param5 = 2 => board level (trim) calibration.
        await _sendCommand(MAV_CMD_PREFLIGHT_CALIBRATION, 0, 0, 0, 0, 2, 0, 0);
        Status = "Level calibration sent. Watch messages for the result.";
    }

    private async Task StartAsync()
    {
        Log.Clear();
        _step = 1;
        IsCalibrating = true;
        OnPropertyChanged(nameof(StepText));
        Instruction = Steps[0].Instruction;
        Status = "Accelerometer calibration started.";
        // param5 = 1 => full accelerometer (6-point) calibration.
        await _sendCommand(MAV_CMD_PREFLIGHT_CALIBRATION, 0, 0, 0, 0, 1, 0, 0);
    }

    private async Task NextAsync()
    {
        if (_step < 1 || _step > 6) return;

        int pos = Steps[_step - 1].Pos;
        await _sendCommand(MAV_CMD_ACCELCAL_VEHICLE_POS, pos, 0, 0, 0, 0, 0, 0);

        if (_step >= 6)
        {
            IsCalibrating = false;
            _step = 0;
            Instruction = "Final position captured. Waiting for the vehicle to confirm…";
            Status = "Calibration data sent — check the messages for success/failure.";
        }
        else
        {
            _step++;
            OnPropertyChanged(nameof(StepText));
            Instruction = Steps[_step - 1].Instruction;
            Status = $"Captured. Now: {Steps[_step - 1].Name}.";
        }
    }

    private void Cancel()
    {
        IsCalibrating = false;
        _step = 0;
        Instruction = "Calibration cancelled.";
        Status = "Cancelled — reboot the vehicle if it stays in calibration mode.";
    }

    /// <summary>Feed STATUSTEXT messages so calibration prompts show up during the flow.</summary>
    public void OnMessage(AutopilotMessage message)
    {
        if (!IsCalibrating &&
            message.Text.IndexOf("calibrat", StringComparison.OrdinalIgnoreCase) < 0)
            return;

        Application.Current?.Dispatcher?.BeginInvoke(() =>
        {
            Log.Insert(0, $"{message.TimestampUtc.ToLocalTime():HH:mm:ss}  {message.Text}");
            while (Log.Count > 8) Log.RemoveAt(Log.Count - 1);

            if (message.Text.IndexOf("success", StringComparison.OrdinalIgnoreCase) >= 0)
                Status = "✔ Calibration successful.";
            else if (message.Text.IndexOf("fail", StringComparison.OrdinalIgnoreCase) >= 0)
                Status = "✖ Calibration failed — try again.";
        });
    }
}

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
/// Compass setup, mirroring the Mission Planner screen: compass-priority table
/// (decoded DEV_ID, external, orientation, reorder), Use 1/2/3, learn-offsets,
/// reboot, and the onboard magnetometer calibration with live per-mag progress.
/// </summary>
public sealed class CompassViewModel : ViewModelBase
{
    private const ushort MAV_CMD_DO_START_MAG_CAL = 42424;
    private const ushort MAV_CMD_DO_ACCEPT_MAG_CAL = 42425;
    private const ushort MAV_CMD_DO_CANCEL_MAG_CAL = 42426;
    private const ushort MAV_CMD_PREFLIGHT_REBOOT_SHUTDOWN = 246;

    private readonly Func<string, float, Task> _setParam;
    private readonly Func<string, Task> _requestParam;
    private readonly Func<ushort, float, float, float, float, float, float, float, Task> _sendCommand;

    private bool _applying;
    private readonly uint[] _prio = new uint[3];  // COMPASS_PRIO1/2/3_ID

    public ObservableCollection<CompassDevice> Devices { get; } = new();
    public ObservableCollection<CompassSlot> Mags { get; } = new();

    /// <summary>The 80 sphere sections sampled during calibration (the "axes" coverage).</summary>
    public ObservableCollection<CoverageCell> Coverage { get; } = new();

    public IReadOnlyList<ParamOption> Orientations { get; } = new ParamOption[]
    {
        new(0, "None"), new(1, "Yaw45"), new(2, "Yaw90"), new(3, "Yaw135"),
        new(4, "Yaw180"), new(5, "Yaw225"), new(6, "Yaw270"), new(7, "Yaw315"),
        new(8, "Roll180"), new(9, "Roll180Yaw45"), new(10, "Roll180Yaw90"),
        new(11, "Roll180Yaw135"), new(12, "Pitch180"), new(13, "Roll180Yaw225"),
        new(14, "Roll180Yaw270"), new(15, "Roll180Yaw315"),
        new(16, "Roll90"), new(20, "Roll270"), new(24, "Pitch90"), new(25, "Pitch270"),
    };

    public IReadOnlyList<string> FitnessOptions { get; } =
        new[] { "Default", "Relaxed", "Very Relaxed" };

    private string _fitness = "Default";
    public string Fitness { get => _fitness; set => SetProperty(ref _fitness, value); }

    private bool _use1, _use2, _use3, _learn;
    public bool UseCompass1 { get => _use1; set => SetBool(ref _use1, value, nameof(UseCompass1), Use(1)); }
    public bool UseCompass2 { get => _use2; set => SetBool(ref _use2, value, nameof(UseCompass2), Use(2)); }
    public bool UseCompass3 { get => _use3; set => SetBool(ref _use3, value, nameof(UseCompass3), Use(3)); }
    public bool LearnOffsets { get => _learn; set => SetBool(ref _learn, value, nameof(LearnOffsets), "COMPASS_LEARN"); }

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
    public string Status { get => _status; private set => SetProperty(ref _status, value); }

    public ICommand RefreshCommand { get; }
    public ICommand RebootCommand { get; }
    public ICommand StartCommand { get; }
    public ICommand AcceptCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand MoveUpCommand { get; }
    public ICommand MoveDownCommand { get; }

    public CompassViewModel(
        Func<string, float, Task> setParam,
        Func<string, Task> requestParam,
        Func<ushort, float, float, float, float, float, float, float, Task> sendCommand)
    {
        _setParam = setParam;
        _requestParam = requestParam;
        _sendCommand = sendCommand;

        for (int i = 1; i <= 3; i++) Devices.Add(new CompassDevice(i, WriteDeviceParam, Orientations));
        for (int i = 0; i < 3; i++) Mags.Add(new CompassSlot(i));
        for (int i = 0; i < 80; i++) Coverage.Add(new CoverageCell());

        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => IsConnected);
        RebootCommand = new AsyncRelayCommand(RebootAsync, () => IsConnected);
        StartCommand = new AsyncRelayCommand(StartAsync, () => IsConnected && !IsCalibrating);
        AcceptCommand = new AsyncRelayCommand(AcceptAsync, () => IsConnected);
        CancelCommand = new AsyncRelayCommand(CancelAsync, () => IsConnected && IsCalibrating);
        MoveUpCommand = new RelayCommand<CompassDevice>(d => Move(d, -1), _ => IsConnected);
        MoveDownCommand = new RelayCommand<CompassDevice>(d => Move(d, +1), _ => IsConnected);
    }

    public void SetConnected(bool connected)
    {
        IsConnected = connected;
        if (!connected) { IsCalibrating = false; Status = "Not connected"; }
        else Status = "Connected — click Read to load compasses.";
    }

    // ── Param names (instance-1 uses the base name, 2/3 are suffixed) ──
    private static string Use(int n) => n == 1 ? "COMPASS_USE" : $"COMPASS_USE{n}";

    private void WriteDeviceParam(int instance, string kind, float value)
    {
        string name = kind switch
        {
            "EXTERNAL" => instance == 1 ? "COMPASS_EXTERNAL" : $"COMPASS_EXTERN{instance}",
            "ORIENT" => instance == 1 ? "COMPASS_ORIENT" : $"COMPASS_ORIENT{instance}",
            _ => ""
        };
        if (name.Length > 0) _ = _setParam(name, value);
    }

    private void SetBool(ref bool field, bool value, string prop, string paramId)
    {
        if (field == value) return;
        field = value;
        OnPropertyChanged(prop);
        if (!_applying) _ = _setParam(paramId, value ? 1f : 0f);
    }

    public async Task RefreshAsync()
    {
        Status = "Reading compass configuration…";
        for (int n = 1; n <= 3; n++)
        {
            await _requestParam(n == 1 ? "COMPASS_DEV_ID" : $"COMPASS_DEV_ID{n}");
            await _requestParam(n == 1 ? "COMPASS_EXTERNAL" : $"COMPASS_EXTERN{n}");
            await _requestParam(n == 1 ? "COMPASS_ORIENT" : $"COMPASS_ORIENT{n}");
            await _requestParam(Use(n));
            await _requestParam($"COMPASS_PRIO{n}_ID");
            await Task.Delay(20);
        }
        await _requestParam("COMPASS_LEARN");
        Status = "Compass configuration read.";
    }

    private async Task RebootAsync()
    {
        Status = "Rebooting autopilot…";
        await _sendCommand(MAV_CMD_PREFLIGHT_REBOOT_SHUTDOWN, 1, 0, 0, 0, 0, 0, 0);
        Status = "Reboot command sent.";
    }

    private async Task StartAsync()
    {
        foreach (var m in Mags) m.Reset();
        foreach (var c in Coverage) c.Covered = false;
        IsCalibrating = true;
        Status = "Rotate the vehicle slowly through all axes until each mag reaches 100%.";
        await _sendCommand(MAV_CMD_DO_START_MAG_CAL, 0, 1, 1, 0, 0, 0, 0);
    }

    private async Task AcceptAsync()
    {
        await _sendCommand(MAV_CMD_DO_ACCEPT_MAG_CAL, 0, 0, 0, 0, 0, 0, 0);
        IsCalibrating = false;
        Status = "Calibration accepted — reboot to apply.";
    }

    private async Task CancelAsync()
    {
        await _sendCommand(MAV_CMD_DO_CANCEL_MAG_CAL, 0, 0, 0, 0, 0, 0, 0);
        IsCalibrating = false;
        Status = "Calibration cancelled.";
    }

    // ── Priority reorder (swaps COMPASS_PRIOn_ID) ─────────────────────
    private void Move(CompassDevice? device, int delta)
    {
        if (device == null || device.DevId == 0) return;
        int idx = Array.IndexOf(_prio, device.DevId);
        if (idx < 0) return;
        int other = idx + delta;
        if (other < 0 || other > 2) return;

        (_prio[idx], _prio[other]) = (_prio[other], _prio[idx]);
        _ = _setParam($"COMPASS_PRIO{idx + 1}_ID", _prio[idx]);
        _ = _setParam($"COMPASS_PRIO{other + 1}_ID", _prio[other]);
        RecomputePriorities();
        Status = "Priority changed — a reboot is required to apply.";
    }

    private void RecomputePriorities()
    {
        foreach (var d in Devices)
        {
            int i = Array.IndexOf(_prio, d.DevId);
            d.Priority = (d.DevId != 0 && i >= 0) ? i + 1 : 0;
        }
    }

    public void OnParameter(string name, float value)
    {
        if (!name.StartsWith("COMPASS_", StringComparison.OrdinalIgnoreCase)) return;
        string up = name.ToUpperInvariant();

        Application.Current?.Dispatcher?.BeginInvoke(() =>
        {
            _applying = true;
            try
            {
                switch (up)
                {
                    case "COMPASS_USE": UseCompass1 = value > 0; break;
                    case "COMPASS_USE2": UseCompass2 = value > 0; break;
                    case "COMPASS_USE3": UseCompass3 = value > 0; break;
                    case "COMPASS_LEARN": LearnOffsets = value > 0; break;
                    case "COMPASS_PRIO1_ID": _prio[0] = (uint)value; RecomputePriorities(); break;
                    case "COMPASS_PRIO2_ID": _prio[1] = (uint)value; RecomputePriorities(); break;
                    case "COMPASS_PRIO3_ID": _prio[2] = (uint)value; RecomputePriorities(); break;
                    case "COMPASS_DEV_ID": ApplyDev(1, value); break;
                    case "COMPASS_DEV_ID2": ApplyDev(2, value); break;
                    case "COMPASS_DEV_ID3": ApplyDev(3, value); break;
                    case "COMPASS_EXTERNAL": ApplyExternal(1, value); break;
                    case "COMPASS_EXTERN2": ApplyExternal(2, value); break;
                    case "COMPASS_EXTERN3": ApplyExternal(3, value); break;
                    case "COMPASS_ORIENT": ApplyOrient(1, value); break;
                    case "COMPASS_ORIENT2": ApplyOrient(2, value); break;
                    case "COMPASS_ORIENT3": ApplyOrient(3, value); break;
                }
            }
            finally { _applying = false; }
        });
    }

    private void ApplyDev(int inst, float value)
    {
        Devices[inst - 1].SetDevId((uint)value);
        RecomputePriorities();
    }
    private void ApplyExternal(int inst, float value) { Devices[inst - 1].Applying = true; Devices[inst - 1].External = value > 0; Devices[inst - 1].Applying = false; }
    private void ApplyOrient(int inst, float value) { Devices[inst - 1].Applying = true; Devices[inst - 1].Orientation = value; Devices[inst - 1].Applying = false; }

    // ── Calibration progress ──────────────────────────────────────────
    public void OnProgress(MagCalProgressData data)
    {
        Application.Current?.Dispatcher?.BeginInvoke(() =>
        {
            if (data.CompassId >= Mags.Count) return;
            var m = Mags[data.CompassId];
            m.Active = true;
            m.Percent = data.CompletionPct;
            m.StatusText = StatusName(data.CalStatus);
            ApplyCoverage(data.CompletionMask);
        });
    }

    // OR each compass's section mask into the shared coverage sphere.
    private void ApplyCoverage(byte[] mask)
    {
        if (mask == null) return;
        for (int i = 0; i < Coverage.Count && i / 8 < mask.Length; i++)
            if ((mask[i / 8] & (1 << (i % 8))) != 0)
                Coverage[i].Covered = true;
    }

    public void OnReport(MagCalReportData data)
    {
        Application.Current?.Dispatcher?.BeginInvoke(() =>
        {
            if (data.CompassId >= Mags.Count) return;
            var m = Mags[data.CompassId];
            m.Active = true;
            m.Fitness = data.Fitness;
            m.StatusText = StatusName(data.CalStatus);
            if (data.CalStatus == 4) m.Percent = 100;

            bool running = false;
            foreach (var c in Mags) if (c.Active && c.Percent < 100 && c.Fitness == null) running = true;
            if (!running)
            {
                IsCalibrating = false;
                Status = data.CalStatus == 4
                    ? "Calibration complete — press Accept to save, then reboot."
                    : "Calibration finished with issues — retry if needed.";
            }
        });
    }

    private static string StatusName(byte status) => status switch
    {
        0 => "Not started", 1 => "Waiting", 2 => "Running (1)", 3 => "Running (2)",
        4 => "Success", 5 => "Failed", 6 => "Bad orientation", 7 => "Bad radius",
        _ => $"Status {status}"
    };
}

/// <summary>One row of the compass-priority table (a physical compass instance).</summary>
public sealed class CompassDevice : ViewModelBase
{
    private readonly Action<int, string, float> _write;

    public int Instance { get; }
    public IReadOnlyList<ParamOption> Orientations { get; }
    public bool Applying { get; set; }

    public CompassDevice(int instance, Action<int, string, float> write, IReadOnlyList<ParamOption> orientations)
    {
        Instance = instance;
        _write = write;
        Orientations = orientations;
    }

    public uint DevId { get; private set; }
    public bool Present => DevId != 0;

    public string DevIdText => DevId == 0 ? "—" : DevId.ToString();
    public string BusTypeText { get; private set; } = "—";
    public string BusText { get; private set; } = "—";
    public string AddressText { get; private set; } = "—";
    public string DevTypeText { get; private set; } = "—";

    private int _priority;
    public int Priority { get => _priority; set { if (SetProperty(ref _priority, value)) OnPropertyChanged(nameof(PriorityText)); } }
    public string PriorityText => _priority > 0 ? _priority.ToString() : "—";

    private bool _external;
    public bool External
    {
        get => _external;
        set { if (SetProperty(ref _external, value) && !Applying) _write(Instance, "EXTERNAL", value ? 1f : 0f); }
    }

    private double _orientation;
    public double Orientation
    {
        get => _orientation;
        set { if (SetProperty(ref _orientation, value) && !Applying) _write(Instance, "ORIENT", (float)value); }
    }

    public void SetDevId(uint id)
    {
        DevId = id;
        if (id == 0)
        {
            BusTypeText = BusText = AddressText = DevTypeText = "—";
        }
        else
        {
            uint busType = id & 0x7;
            uint bus = (id >> 3) & 0x1F;
            uint addr = (id >> 8) & 0xFF;
            uint devType = (id >> 16) & 0xFF;
            BusTypeText = BusTypeName(busType);
            BusText = bus.ToString();
            AddressText = addr.ToString();
            DevTypeText = DevTypeName(devType);
        }
        OnPropertyChanged(nameof(DevId));
        OnPropertyChanged(nameof(Present));
        OnPropertyChanged(nameof(DevIdText));
        OnPropertyChanged(nameof(BusTypeText));
        OnPropertyChanged(nameof(BusText));
        OnPropertyChanged(nameof(AddressText));
        OnPropertyChanged(nameof(DevTypeText));
    }

    private static string BusTypeName(uint t) => t switch
    {
        1 => "I2C", 2 => "SPI", 3 => "DroneCAN", 4 => "SITL", 5 => "MSP", 6 => "SERIAL", _ => t.ToString()
    };

    private static string DevTypeName(uint d) => d switch
    {
        0x01 => "HMC5883-old", 0x02 => "LSM303D", 0x04 => "AK8963", 0x05 => "BMM150",
        0x06 => "LSM9DS1", 0x07 => "HMC5883", 0x08 => "LIS3MDL", 0x09 => "AK09916",
        0x0A => "IST8310", 0x0B => "ICM20948", 0x0C => "MMC3416", 0x0D => "QMC5883L",
        0x0E => "MAG3110", 0x0F => "SITL", 0x10 => "IST8308", 0x11 => "RM3100",
        0x12 => "RM3100-2", 0x13 => "MMC5883", 0x14 => "AK09918", 0x15 => "AK09915",
        0x16 => "QMC5883P", 0x17 => "BMM350", 0x18 => "IIS2MDC", 0xFF => "DroneCAN",
        _ => $"0x{d:X2}"
    };
}

/// <summary>One section of the calibration-coverage sphere.</summary>
public sealed class CoverageCell : ViewModelBase
{
    private bool _covered;
    public bool Covered
    {
        get => _covered;
        set { if (SetProperty(ref _covered, value)) OnPropertyChanged(nameof(Color)); }
    }

    public string Color => _covered ? "#3FB950" : "#22282F";
}

public sealed class CompassSlot : ViewModelBase
{
    public int Id { get; }
    public string Name => $"Mag {Id + 1}";

    public CompassSlot(int id) => Id = id;

    private bool _active;
    public bool Active { get => _active; set => SetProperty(ref _active, value); }

    private double _percent;
    public double Percent { get => _percent; set => SetProperty(ref _percent, value); }

    private string _statusText = "—";
    public string StatusText { get => _statusText; set => SetProperty(ref _statusText, value); }

    private float? _fitness;
    public float? Fitness
    {
        get => _fitness;
        set { if (SetProperty(ref _fitness, value)) OnPropertyChanged(nameof(FitnessText)); }
    }

    public string FitnessText => _fitness.HasValue ? $"fitness {_fitness.Value:0.0}" : "";

    public void Reset()
    {
        Active = false;
        Percent = 0;
        StatusText = "—";
        Fitness = null;
    }
}

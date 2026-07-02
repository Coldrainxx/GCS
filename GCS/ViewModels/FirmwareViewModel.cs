using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO.Ports;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using GCS.Core.Firmware;
using Microsoft.Win32;

namespace GCS.ViewModels;

/// <summary>
/// Firmware loader (Phase 1): browse/download official ArduPilot firmware or load
/// a local .apj, parse it, and detect the connected board's bootloader. The actual
/// flash step is added next.
/// </summary>
public sealed class FirmwareViewModel : ViewModelBase
{
    private readonly ArduPilotManifestClient _manifest = new();
    private readonly FirmwareUploader _uploader = new();
    private readonly Func<Task>? _rebootToBootloader;
    private readonly Func<Task>? _disconnectVehicle;

    private List<ArduPilotFirmwareEntry> _all = new();
    private ApjFirmware? _loaded;

    public ObservableCollection<string> VehicleTypes { get; } = new();
    public ObservableCollection<string> Boards { get; } = new();
    public ObservableCollection<ArduPilotFirmwareEntry> Versions { get; } = new();
    public ObservableCollection<string> SerialPorts { get; } = new();

    private string? _selectedVehicleType;
    private string? _selectedBoard;
    private ArduPilotFirmwareEntry? _selectedFirmware;
    private string? _selectedPort;
    private string _loadedInfo = "No firmware loaded.";
    private string _detectedInfo = "";
    private string _matchInfo = "";
    private bool _matchOk;
    private bool _isBusy;
    private bool _flashing;
    private double _flashPercent;
    private bool _statusIsError;
    private string _statusMessage = "Load the firmware list, or browse for a local .apj file.";

    public string? SelectedVehicleType
    {
        get => _selectedVehicleType;
        set { if (SetProperty(ref _selectedVehicleType, value)) UpdateBoards(); }
    }

    public string? SelectedBoard
    {
        get => _selectedBoard;
        set { if (SetProperty(ref _selectedBoard, value)) UpdateVersions(); }
    }

    public ArduPilotFirmwareEntry? SelectedFirmware
    {
        get => _selectedFirmware;
        set => SetProperty(ref _selectedFirmware, value);
    }

    public string? SelectedPort
    {
        get => _selectedPort;
        set { if (SetProperty(ref _selectedPort, value)) { OnPropertyChanged(nameof(CanFlash)); CommandManager.InvalidateRequerySuggested(); } }
    }

    public string LoadedInfo { get => _loadedInfo; private set => SetProperty(ref _loadedInfo, value); }
    public string DetectedInfo { get => _detectedInfo; private set => SetProperty(ref _detectedInfo, value); }
    public string MatchInfo { get => _matchInfo; private set => SetProperty(ref _matchInfo, value); }
    public bool MatchOk { get => _matchOk; private set { if (SetProperty(ref _matchOk, value)) OnPropertyChanged(nameof(MatchColor)); } }
    public string MatchColor => MatchOk ? "#3FB950" : "#F85149";

    public bool IsBusy
    {
        get => _isBusy;
        set { if (SetProperty(ref _isBusy, value)) CommandManager.InvalidateRequerySuggested(); }
    }

    public string StatusMessage { get => _statusMessage; set => SetProperty(ref _statusMessage, value); }

    public bool StatusIsError
    {
        get => _statusIsError;
        private set { if (SetProperty(ref _statusIsError, value)) OnPropertyChanged(nameof(StatusColor)); }
    }

    /// <summary>Orange when something went wrong, green otherwise.</summary>
    public string StatusColor => _statusIsError ? "#FF9500" : "#3FB950";

    private void Ok(string message) { StatusIsError = false; StatusMessage = message; }
    private void Warn(string message) { StatusIsError = true; StatusMessage = message; }

    public bool Flashing { get => _flashing; private set => SetProperty(ref _flashing, value); }
    public double FlashPercent { get => _flashPercent; private set => SetProperty(ref _flashPercent, value); }

    public bool CanFlash => _loaded != null && !string.IsNullOrEmpty(SelectedPort) && !IsBusy;

    public ICommand LoadManifestCommand { get; }
    public ICommand RefreshPortsCommand { get; }
    public ICommand BrowseLocalCommand { get; }
    public ICommand DownloadCommand { get; }
    public ICommand DetectCommand { get; }
    public ICommand RebootToBootloaderCommand { get; }
    public ICommand FlashCommand { get; }

    public FirmwareViewModel(Func<Task>? rebootToBootloader = null, Func<Task>? disconnectVehicle = null)
    {
        _rebootToBootloader = rebootToBootloader;
        _disconnectVehicle = disconnectVehicle;

        LoadManifestCommand = new AsyncRelayCommand(LoadManifestAsync, () => !IsBusy);
        RefreshPortsCommand = new RelayCommand(RefreshPorts);
        BrowseLocalCommand = new RelayCommand(BrowseLocal, () => !IsBusy);
        DownloadCommand = new AsyncRelayCommand(DownloadAsync, () => !IsBusy && SelectedFirmware != null);
        DetectCommand = new AsyncRelayCommand(DetectAsync, () => !IsBusy && !string.IsNullOrEmpty(SelectedPort));
        RebootToBootloaderCommand = new AsyncRelayCommand(RebootToBootloaderAsync, () => !IsBusy);
        FlashCommand = new AsyncRelayCommand(FlashAsync, () => CanFlash);
        RefreshPorts();
    }

    private async Task FlashAsync()
    {
        if (_loaded == null || string.IsNullOrEmpty(SelectedPort)) return;

        var confirm = System.Windows.MessageBox.Show(
            $"{LoadedInfo}\n\nThis will ERASE and reprogram the board on {SelectedPort}.\n" +
            "Do NOT disconnect power or unplug the board during flashing.\n\nContinue?",
            "Confirm firmware flash",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning);
        if (confirm != System.Windows.MessageBoxResult.Yes) return;

        IsBusy = true;
        Flashing = true;
        FlashPercent = 0;
        try
        {
            // Free the serial port if we're still connected over MAVLink.
            if (_disconnectVehicle != null)
            {
                Ok("Disconnecting vehicle to free the serial port...");
                await _disconnectVehicle();
                await Task.Delay(700);
            }

            var progress = new Progress<FlashProgress>(p =>
            {
                FlashPercent = p.Percent;
                Ok($"{p.Phase}  {p.Percent:F0}%");
            });

            await _uploader.FlashAsync(SelectedPort!, _loaded, progress);

            FlashPercent = 100;
            Ok("✓ Flash complete. The board has rebooted into the new firmware.");
        }
        catch (Exception ex)
        {
            Warn($"Flash failed: {ex.Message}");
            Debug.WriteLine($"[Firmware] Flash error: {ex}");
        }
        finally
        {
            Flashing = false;
            IsBusy = false;
        }
    }

    private async Task RebootToBootloaderAsync()
    {
        if (_rebootToBootloader == null)
        {
            Warn("Connect to the vehicle (LINK) first, then reboot it to the bootloader.");
            return;
        }

        IsBusy = true;
        Ok("Rebooting vehicle to bootloader...");
        try
        {
            var portsBefore = new HashSet<string>(SerialPort.GetPortNames());

            await _rebootToBootloader();
            await Task.Delay(300);

            // Free the original serial port so the OS can re-enumerate the bootloader.
            if (_disconnectVehicle != null)
                await _disconnectVehicle();

            Ok("Reboot sent. Waiting for the bootloader port to appear...");

            string? newPort = await WaitForNewPortAsync(portsBefore, TimeSpan.FromSeconds(8));

            RefreshPorts();
            if (newPort != null && SerialPorts.Contains(newPort))
            {
                SelectedPort = newPort;
                Ok($"Bootloader port {newPort} detected. Click Detect (or Flash).");
            }
            else
            {
                Warn("Rebooted. No new port appeared - if the COM port is unchanged just select it and click Detect, otherwise press ⟳.");
            }
        }
        catch (Exception ex)
        {
            Warn($"Reboot failed (are you connected?): {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Poll for a serial port that appears after <paramref name="before"/> was captured.</summary>
    private static async Task<string?> WaitForNewPortAsync(HashSet<string> before, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var added = SerialPort.GetPortNames().FirstOrDefault(p => !before.Contains(p));
            if (added != null) return added;
            await Task.Delay(300);
        }
        return null;
    }

    private async Task LoadManifestAsync()
    {
        IsBusy = true;
        Ok("Downloading firmware list from firmware.ardupilot.org...");
        try
        {
            var all = await _manifest.GetFirmwareAsync();
            _all = all.ToList();

            VehicleTypes.Clear();
            foreach (var v in _all.Select(e => e.VehicleType).Distinct().OrderBy(v => v))
                VehicleTypes.Add(v);

            Boards.Clear();
            Versions.Clear();
            Ok($"Loaded {_all.Count} builds. Pick a vehicle type, board and version.");
        }
        catch (Exception ex)
        {
            Warn($"Failed to load firmware list: {ex.Message}");
            Debug.WriteLine($"[Firmware] Manifest error: {ex}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void UpdateBoards()
    {
        Boards.Clear();
        Versions.Clear();
        SelectedFirmware = null;
        if (_selectedVehicleType == null) return;

        foreach (var b in _all.Where(e => e.VehicleType == _selectedVehicleType)
                               .Select(e => e.Platform).Distinct().OrderBy(b => b))
            Boards.Add(b);
    }

    private void UpdateVersions()
    {
        Versions.Clear();
        SelectedFirmware = null;
        if (_selectedVehicleType == null || _selectedBoard == null) return;

        var entries = _all
            .Where(e => e.VehicleType == _selectedVehicleType && e.Platform == _selectedBoard)
            .OrderByDescending(e => e.Latest)
            .ThenByDescending(e => e.Version, StringComparer.OrdinalIgnoreCase);

        foreach (var e in entries)
            Versions.Add(e);

        SelectedFirmware = Versions.FirstOrDefault();
    }

    private void RefreshPorts()
    {
        var current = SelectedPort;
        SerialPorts.Clear();
        foreach (var p in SerialPort.GetPortNames().OrderBy(p => p))
            SerialPorts.Add(p);
        SelectedPort = SerialPorts.Contains(current ?? "") ? current : SerialPorts.FirstOrDefault();
    }

    private void BrowseLocal()
    {
        var dlg = new OpenFileDialog
        {
            Title = "Select firmware (.apj)",
            Filter = "ArduPilot firmware (*.apj)|*.apj|All files (*.*)|*.*"
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            _loaded = ApjFirmware.ParseFile(dlg.FileName);
            OnFirmwareLoaded(System.IO.Path.GetFileName(dlg.FileName));
        }
        catch (Exception ex)
        {
            Warn($"Failed to parse .apj: {ex.Message}");
        }
    }

    private async Task DownloadAsync()
    {
        var fw = SelectedFirmware;
        if (fw == null) return;

        IsBusy = true;
        Ok($"Downloading {fw.VehicleType} {fw.Version} for {fw.Platform}...");
        try
        {
            var text = await _manifest.DownloadApjTextAsync(fw.Url);
            _loaded = ApjFirmware.Parse(text);
            OnFirmwareLoaded($"{fw.VehicleType} {fw.Version} ({fw.Platform})");
        }
        catch (Exception ex)
        {
            Warn($"Download failed: {ex.Message}");
            Debug.WriteLine($"[Firmware] Download error: {ex}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void OnFirmwareLoaded(string label)
    {
        LoadedInfo = _loaded == null
            ? "No firmware loaded."
            : $"{label}\nBoard ID: {_loaded.BoardId}   Image: {_loaded.Image.Length:N0} bytes";
        Ok("Firmware loaded. Put the board in bootloader mode, then Detect and Flash.");
        UpdateMatch();
        OnPropertyChanged(nameof(CanFlash));
        CommandManager.InvalidateRequerySuggested();
    }

    private async Task DetectAsync()
    {
        if (string.IsNullOrEmpty(SelectedPort)) return;

        IsBusy = true;
        Ok($"Detecting board on {SelectedPort} (must be in bootloader mode)...");
        try
        {
            var result = await _uploader.DetectAsync(SelectedPort);
            if (result.Success && result.Info != null)
            {
                DetectedInfo = result.Message;
                Ok("Board detected.");
                UpdateMatch(result.Info.BoardId);
            }
            else
            {
                DetectedInfo = "";
                MatchInfo = "";
                Warn(result.Message);
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void UpdateMatch(uint? detectedBoardId = null)
    {
        if (_loaded == null || (detectedBoardId == null && string.IsNullOrEmpty(DetectedInfo)))
        {
            MatchInfo = "";
            return;
        }
        if (detectedBoardId == null)
        {
            MatchInfo = "";
            return;
        }

        bool ok = _loaded.BoardId == detectedBoardId.Value;
        MatchOk = ok;
        MatchInfo = ok
            ? $"✓ Firmware board id {_loaded.BoardId} matches the connected board."
            : $"✗ Mismatch: firmware is for board {_loaded.BoardId}, connected board is {detectedBoardId.Value}. Do NOT flash.";
    }
}

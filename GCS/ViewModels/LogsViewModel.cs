using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using GCS.Core.Logging;

namespace GCS.ViewModels;

/// <summary>One recorded log on disk.</summary>
public sealed class LogFileItem
{
    public string FullPath { get; init; } = "";
    public string Name { get; init; } = "";
    public long SizeBytes { get; init; }
    public DateTime ModifiedLocal { get; init; }

    public string SizeText => SizeBytes >= 1024 * 1024
        ? $"{SizeBytes / 1024.0 / 1024.0:F1} MB"
        : $"{SizeBytes / 1024.0:F0} KB";

    public string ModifiedText => ModifiedLocal.ToString("yyyy-MM-dd HH:mm");
}

/// <summary>
/// Post-flight log review: pick a .tlog and see what the flight contained.
///
/// Analysis runs off the UI thread — a 4 MB log is ~200 ms, but logs grow with
/// flight time and a long session should not freeze the window.
/// </summary>
public sealed class LogsViewModel : ViewModelBase
{
    /// <summary>Where <see cref="TelemetryLogger"/> writes, so recent flights are listed automatically.</summary>
    public static string DefaultLogDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "GCS", "logs");

    public ObservableCollection<LogFileItem> RecentLogs { get; } = new();
    public ObservableCollection<FlightEvent> Events { get; } = new();
    public ObservableCollection<string> Findings { get; } = new();
    public ObservableCollection<string> Notes { get; } = new();

    public RelayCommand RefreshCommand { get; }
    public RelayCommand OpenFileCommand { get; }

    /// <summary>Set by the view, which owns the file dialog.</summary>
    public Func<string?>? PickFile { get; set; }

    private LogFileItem? _selectedLog;
    public LogFileItem? SelectedLog
    {
        get => _selectedLog;
        set
        {
            if (!SetProperty(ref _selectedLog, value)) return;
            if (value != null) _ = AnalyzeAsync(value.FullPath);
        }
    }

    private FlightLogSummary? _summary;
    public FlightLogSummary? Summary
    {
        get => _summary;
        private set
        {
            if (!SetProperty(ref _summary, value)) return;
            OnPropertyChanged(nameof(HasSummary));
            OnPropertyChanged(nameof(BatteryText));
            OnPropertyChanged(nameof(GpsText));
            OnPropertyChanged(nameof(ArmedText));
            OnPropertyChanged(nameof(AltitudeText));
            OnPropertyChanged(nameof(SpeedText));
            OnPropertyChanged(nameof(VehiclesText));
            OnPropertyChanged(nameof(Samples));
        }
    }

    public bool HasSummary => _summary != null;

    /// <summary>The sampled track, drawn on the real map rather than plotted here.</summary>
    public IReadOnlyList<FlightSample> Samples =>
        _summary?.Samples ?? (IReadOnlyList<FlightSample>)Array.Empty<FlightSample>();

    private bool _isBusy;
    public bool IsBusy { get => _isBusy; private set => SetProperty(ref _isBusy, value); }

    private string _status = "Select a log to analyse.";
    public string Status { get => _status; private set => SetProperty(ref _status, value); }

    // ── Derived display ─────────────────────────────────────────────

    public string BatteryText => _summary is null ? "—"
        : !_summary.HasBattery ? "Not recorded"
        : $"{_summary.BatteryStartVolts:F2} → {_summary.BatteryEndVolts:F2} V" +
          (_summary.BatteryMinPercent > 0 ? $" · min {_summary.BatteryMinPercent}%" : "");

    public string GpsText => _summary is null ? "—"
        : !_summary.HasGps ? "Not recorded"
        : $"worst fix {FixName(_summary.WorstGpsFix)} · min {_summary.MinSatellites} sats";

    public string ArmedText => _summary is null ? "—"
        : _summary.ArmCount == 0 ? "Never armed"
        : $"{Format(_summary.ArmedDuration)} over {_summary.ArmCount} arm(s)";

    public string AltitudeText => _summary is null ? "—" : $"{_summary.MaxAltitudeRelM:F1} m";

    public string SpeedText => _summary is null ? "—"
        : $"{_summary.MaxGroundspeedMps:F1} m/s ground · {_summary.MaxAirspeedMps:F1} m/s air";

    public string VehiclesText => _summary is null || _summary.SystemIds.Count == 0
        ? "—"
        : string.Join(", ", _summary.SystemIds.Select(id => $"UAV {id}"));

    public LogsViewModel()
    {
        RefreshCommand = new RelayCommand(RefreshRecent, () => !IsBusy);
        OpenFileCommand = new RelayCommand(OpenFile, () => !IsBusy);
    }

    /// <summary>Drop the loaded flight when review ends.</summary>
    public void Close()
    {
        SelectedLog = null;
        Summary = null;
        Events.Clear();
        Findings.Clear();
        Notes.Clear();
        Status = "Select a log to analyse.";
    }

    public void RefreshRecent()
    {
        RecentLogs.Clear();

        try
        {
            var dir = new DirectoryInfo(DefaultLogDirectory);
            if (!dir.Exists)
            {
                Status = "No logs yet — they are written automatically while connected.";
                return;
            }

            foreach (var file in dir.GetFiles("*.tlog").OrderByDescending(f => f.LastWriteTime))
            {
                RecentLogs.Add(new LogFileItem
                {
                    FullPath = file.FullName,
                    Name = file.Name,
                    SizeBytes = file.Length,
                    ModifiedLocal = file.LastWriteTime,
                });
            }

            Status = RecentLogs.Count == 0
                ? "No logs found."
                : $"{RecentLogs.Count} log(s) found.";
        }
        catch (Exception ex)
        {
            Status = $"Could not list logs: {ex.Message}";
        }
    }

    private void OpenFile()
    {
        string? path = PickFile?.Invoke();
        if (string.IsNullOrWhiteSpace(path)) return;

        // Show it in the list even when it came from elsewhere on disk.
        var info = new FileInfo(path);
        var item = new LogFileItem
        {
            FullPath = info.FullName,
            Name = info.Name,
            SizeBytes = info.Exists ? info.Length : 0,
            ModifiedLocal = info.Exists ? info.LastWriteTime : DateTime.Now,
        };

        var existing = RecentLogs.FirstOrDefault(l =>
            string.Equals(l.FullPath, item.FullPath, StringComparison.OrdinalIgnoreCase));

        if (existing == null)
        {
            RecentLogs.Insert(0, item);
            SelectedLog = item;
        }
        else
        {
            SelectedLog = existing;
        }
    }

    private async Task AnalyzeAsync(string path)
    {
        IsBusy = true;
        Status = $"Analysing {Path.GetFileName(path)}…";
        Summary = null;
        Events.Clear();
        Findings.Clear();
        Notes.Clear();

        try
        {
            var summary = await Task.Run(() => FlightLogAnalyzer.Analyze(path)).ConfigureAwait(true);

            Summary = summary;
            foreach (var e in summary.Events) Events.Add(e);
            foreach (var f in summary.Findings) Findings.Add(f);
            foreach (var n in summary.Notes) Notes.Add(n);

            Status = summary.PacketCount == 0
                ? "No MAVLink packets could be read from this file."
                : $"{summary.PacketCount:N0} packets · {summary.DurationText}";
        }
        catch (Exception ex)
        {
            Status = $"Could not analyse: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static string Format(TimeSpan span) => span.TotalHours >= 1
        ? $"{(int)span.TotalHours}h {span.Minutes}m"
        : span.TotalMinutes >= 1 ? $"{span.Minutes}m {span.Seconds}s" : $"{span.Seconds}s";

    private static string FixName(byte fix) => fix switch
    {
        0 => "no GPS",
        1 => "no fix",
        2 => "2D",
        3 => "3D",
        4 => "DGPS",
        5 => "RTK float",
        6 => "RTK fixed",
        _ => fix.ToString(),
    };
}

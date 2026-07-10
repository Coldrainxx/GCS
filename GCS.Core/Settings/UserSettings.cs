using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace GCS.Core.Settings;

/// <summary>Persisted user settings (connection history, mission defaults, options).</summary>
public sealed class UserSettings
{
    public List<ConnectionProfile> RecentConnections { get; set; } = new();
    public bool AutoReconnect { get; set; } = true;
    public bool TelemetryLogging { get; set; } = true;

    public float DefaultAltitude { get; set; } = 100;
    public float DefaultRadius { get; set; } = 10;
    public byte DefaultFrame { get; set; } = 3;
    public float CruiseSpeedMps { get; set; } = 15;

    // ── Window / layout state (restored on launch) ──────────────────
    public double WindowWidth { get; set; }      // 0 = use XAML default
    public double WindowHeight { get; set; }
    public double WindowLeft { get; set; } = double.NaN;
    public double WindowTop { get; set; } = double.NaN;
    public bool WindowMaximized { get; set; }
    public double FlightPanelWidth { get; set; } // 0 = use XAML default

    private const int MaxRecent = 8;

    /// <summary>Move a profile to the front of the recent list (dedup, cap length).</summary>
    public void Remember(ConnectionProfile profile)
    {
        RecentConnections.RemoveAll(p => p.SameAs(profile));
        RecentConnections.Insert(0, profile);
        if (RecentConnections.Count > MaxRecent)
            RecentConnections.RemoveRange(MaxRecent, RecentConnections.Count - MaxRecent);
    }
}

/// <summary>Loads/saves <see cref="UserSettings"/> as JSON under %AppData%\GCS.</summary>
public static class SettingsStore
{
    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GCS");
    private static readonly string FilePath = Path.Combine(Dir, "settings.json");

    private static UserSettings? _current;
    public static UserSettings Current => _current ??= Load();

    public static UserSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonConvert.DeserializeObject<UserSettings>(File.ReadAllText(FilePath)) ?? new UserSettings();
        }
        catch { /* fall through to defaults */ }
        return new UserSettings();
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonConvert.SerializeObject(Current, Formatting.Indented));
        }
        catch { /* best-effort persistence */ }
    }
}

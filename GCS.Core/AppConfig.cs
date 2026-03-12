using System;
using System.IO;
using Newtonsoft.Json;

namespace GCS.Core;

/// <summary>
/// Application configuration loaded from appsettings.json.
/// Keeps API keys and configurable values out of source code.
/// </summary>
public sealed class AppConfig
{
    public string WeatherApiKey { get; set; } = "";
    public string WeatherCity { get; set; } = "Baku";
    public string WeatherCountry { get; set; } = "AZ";

    private static AppConfig? _instance;

    /// <summary>
    /// Loads configuration from appsettings.json next to the executable.
    /// Falls back to defaults if file is missing or unreadable.
    /// </summary>
    public static AppConfig Load()
    {
        if (_instance != null) return _instance;

        var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings.json");

        if (File.Exists(path))
        {
            try
            {
                var json = File.ReadAllText(path);
                _instance = JsonConvert.DeserializeObject<AppConfig>(json) ?? new AppConfig();
            }
            catch
            {
                _instance = new AppConfig();
            }
        }
        else
        {
            _instance = new AppConfig();
        }

        return _instance;
    }
}
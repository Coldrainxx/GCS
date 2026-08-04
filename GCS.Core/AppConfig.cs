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

    /// <summary>
    /// Optional LLM assistant. Left empty the app uses its built-in deterministic
    /// answers, so the advisor always works with no key, no account and no network.
    /// </summary>
    public Advisor.Ai.AssistantOptions Assistant { get; set; } = new();

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

    private static string ConfigPath =>
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings.json");

    /// <summary>
    /// Persist the assistant settings.
    ///
    /// Rewrites only the Assistant node and leaves the rest of the file byte-for-byte
    /// alone — the file is hand-edited and holds unrelated keys, so serialising this
    /// object wholesale would silently drop anything it does not model.
    /// </summary>
    public static bool SaveAssistant(Advisor.Ai.AssistantOptions options, out string? error)
    {
        error = null;

        try
        {
            var root = File.Exists(ConfigPath)
                ? Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(ConfigPath))
                : new Newtonsoft.Json.Linq.JObject();

            root["Assistant"] = Newtonsoft.Json.Linq.JObject.FromObject(new
            {
                options.Provider,
                options.BaseUrl,
                options.ApiKey,
                options.Model,
                options.TimeoutSeconds,
            });

            File.WriteAllText(ConfigPath, root.ToString(Newtonsoft.Json.Formatting.Indented));

            if (_instance != null) _instance.Assistant = options;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
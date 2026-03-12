using System;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace GCS.Core.Utils;

public class WeatherData
{
    public double Temperature { get; set; }      // Celsius
    public int Humidity { get; set; }            // Percent
    public double WindSpeed { get; set; }        // km/h
    public int WindDirection { get; set; }       // Degrees
    public string Description { get; set; } = "";
    public double Visibility { get; set; }       // km (-1 if unavailable)
    public double Pressure { get; set; }         // hPa
    public bool IsGoodForFlight { get; set; }
    public DateTime LastUpdated { get; set; }
}

public interface IWeatherService
{
    Task<WeatherData?> GetWeatherAsync(string city, string country);
}

public class WeatherService : IWeatherService
{
    private static readonly HttpClient SharedClient = new();

    private readonly HttpClient _client;
    private readonly string _apiKey;

    public WeatherService(string apiKey, HttpClient? client = null)
    {
        _apiKey = apiKey;
        _client = client ?? SharedClient;
    }

    public async Task<WeatherData?> GetWeatherAsync(string city, string country)
    {
        try
        {
            string url = $"https://api.openweathermap.org/data/2.5/weather?q={city},{country}&appid={_apiKey}&units=metric&lang=az";
            var response = await _client.GetStringAsync(url);
            var json = JObject.Parse(response);

            double temperature = json["main"]!["temp"]!.Value<double>();
            int humidity = json["main"]!["humidity"]!.Value<int>();
            double windSpeedMs = json["wind"]!["speed"]!.Value<double>();
            double windSpeed = windSpeedMs * 3.6; // m/s to km/h
            int windDirection = json["wind"]?["deg"]?.Value<int>() ?? 0;
            string description = json["weather"]![0]!["description"]!.Value<string>() ?? "";

            double visibility = json.ContainsKey("visibility")
                ? json["visibility"]!.Value<double>() / 1000.0  // meters to km
                : -1;

            double pressure = json["main"]!["pressure"]!.Value<double>();

            // Flight suitability check
            bool isGoodForFlight = windSpeed < 15 * 3.6 && // < 15 m/s in km/h
                                   temperature > -1 &&
                                   humidity < 90 &&
                                   visibility > 3;

            return new WeatherData
            {
                Temperature = temperature,
                Humidity = humidity,
                WindSpeed = windSpeed,
                WindDirection = windDirection,
                Description = description,
                Visibility = visibility,
                Pressure = pressure,
                IsGoodForFlight = isGoodForFlight,
                LastUpdated = DateTime.Now
            };
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[WeatherService] Error: {ex.Message}");
            return null;
        }
    }

    public static string GetWindDirectionName(int degrees)
    {
        return degrees switch
        {
            >= 337 or < 23 => "N",
            >= 23 and < 68 => "NE",
            >= 68 and < 113 => "E",
            >= 113 and < 158 => "SE",
            >= 158 and < 203 => "S",
            >= 203 and < 248 => "SW",
            >= 248 and < 293 => "W",
            >= 293 and < 337 => "NW",
           
        };
    }
}
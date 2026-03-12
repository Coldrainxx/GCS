using CommunityToolkit.Mvvm.Input;
using GCS.Core.Utils;
using System;
using System.Threading.Tasks;
using System.Windows.Input;

namespace GCS.ViewModels;

public class WeatherViewModel : ViewModelBase
{
    private readonly WeatherService _weatherService;
    private readonly string _city;
    private readonly string _country;

    private bool _isLoading;
    private bool _hasData;
    private bool _isPopupOpen;

    // Weather data
    private double _temperature;
    private int _humidity;
    private double _windSpeed;
    private string _windDirection = "";
    private string _description = "";
    private double _visibility;
    private double _pressure;
    private bool _isGoodForFlight;
    private string _lastUpdated = "";

    public bool IsLoading
    {
        get => _isLoading;
        set => SetProperty(ref _isLoading, value);
    }

    public bool HasData
    {
        get => _hasData;
        set => SetProperty(ref _hasData, value);
    }

    public bool IsPopupOpen
    {
        get => _isPopupOpen;
        set => SetProperty(ref _isPopupOpen, value);
    }

    public double Temperature
    {
        get => _temperature;
        set => SetProperty(ref _temperature, value);
    }

    public int Humidity
    {
        get => _humidity;
        set => SetProperty(ref _humidity, value);
    }

    public double WindSpeed
    {
        get => _windSpeed;
        set => SetProperty(ref _windSpeed, value);
    }

    public string WindDirection
    {
        get => _windDirection;
        set => SetProperty(ref _windDirection, value);
    }

    public string Description
    {
        get => _description;
        set => SetProperty(ref _description, value);
    }

    public double Visibility
    {
        get => _visibility;
        set => SetProperty(ref _visibility, value);
    }

    public double Pressure
    {
        get => _pressure;
        set => SetProperty(ref _pressure, value);
    }

    public bool IsGoodForFlight
    {
        get => _isGoodForFlight;
        set
        {
            if (SetProperty(ref _isGoodForFlight, value))
            {
                OnPropertyChanged(nameof(FlightConditionText));
                OnPropertyChanged(nameof(FlightConditionColor));
            }
        }
    }

    public string FlightConditionText => IsGoodForFlight ? "✓ Good for Flight" : "✗ Risky Conditions";
    public string FlightConditionColor => IsGoodForFlight ? "#3FB950" : "#F85149";

    public string LastUpdated
    {
        get => _lastUpdated;
        set => SetProperty(ref _lastUpdated, value);
    }

    public string VisibilityText => Visibility >= 0 ? $"{Visibility:F1} km" : "N/A";

    public ICommand TogglePopupCommand { get; }
    public ICommand RefreshCommand { get; }

    public WeatherViewModel(string apiKey, string city = "Baku", string country = "AZ")
    {
        _weatherService = new WeatherService(apiKey);
        _city = city;
        _country = country;

        TogglePopupCommand = new RelayCommand(() => IsPopupOpen = !IsPopupOpen);
        RefreshCommand = new AsyncRelayCommand(RefreshWeatherAsync);

        _ = RefreshWeatherAsync();
    }

    public async Task RefreshWeatherAsync()
    {
        IsLoading = true;

        var data = await _weatherService.GetWeatherAsync(_city, _country);

        if (data != null)
        {
            Temperature = data.Temperature;
            Humidity = data.Humidity;
            WindSpeed = data.WindSpeed;
            WindDirection = $"{WeatherService.GetWindDirectionName(data.WindDirection)} ({data.WindDirection}°)";
            Description = data.Description;
            Visibility = data.Visibility;
            Pressure = data.Pressure;
            IsGoodForFlight = data.IsGoodForFlight;
            LastUpdated = data.LastUpdated.ToString("HH:mm");
            HasData = true;
        }
        else
        {
            HasData = false;
        }

        IsLoading = false;
        OnPropertyChanged(nameof(VisibilityText));
    }
}
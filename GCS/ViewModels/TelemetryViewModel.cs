using GCS.Core.Domain;
using System;

namespace GCS.ViewModels;

public class TelemetryViewModel : ViewModelBase
{
    private bool _isConnected;
    private byte _systemId;
    private byte _componentId;
    private string _flightMode = "UNKNOWN";
    private string _lastHeartbeat = "N/A";
    private bool _isArmed;
    private double _roll, _pitch, _yaw;
    private double _latitude, _longitude;
    private float _altitude, _altitudeRelative, _heading;
    private float _groundspeed, _airspeed, _climbRate;
    private float _voltage, _current;
    private int _batteryRemaining;
    private bool _linkAlive, _attitudeFresh, _positionFresh;
    private byte _gpsFixType;
    private byte _gpsSatellites;
    private string _gpsFixString = "NO GPS";
    private float _gpsHdop;

    public bool IsConnected { get => _isConnected; set => SetProperty(ref _isConnected, value); }
    public byte SystemId { get => _systemId; set => SetProperty(ref _systemId, value); }
    public byte ComponentId { get => _componentId; set => SetProperty(ref _componentId, value); }
    public string FlightMode { get => _flightMode; set => SetProperty(ref _flightMode, value); }
    public string LastHeartbeat { get => _lastHeartbeat; set => SetProperty(ref _lastHeartbeat, value); }
    public bool IsArmed { get => _isArmed; set => SetProperty(ref _isArmed, value); }
    public string ArmedStatus => IsArmed ? "ARMED" : "DISARMED";
    public string ArmedColor => IsArmed ? "#F85149" : "#3FB950";  // Red when armed, Green when safe

    public double Roll { get => _roll; set => SetProperty(ref _roll, value); }
    public double Pitch { get => _pitch; set => SetProperty(ref _pitch, value); }
    public double Yaw { get => _yaw; set => SetProperty(ref _yaw, value); }

    public double Latitude { get => _latitude; set => SetProperty(ref _latitude, value); }
    public double Longitude { get => _longitude; set => SetProperty(ref _longitude, value); }
    public float Altitude { get => _altitude; set => SetProperty(ref _altitude, value); }
    public float AltitudeRelative { get => _altitudeRelative; set => SetProperty(ref _altitudeRelative, value); }
    public float Heading { get => _heading; set => SetProperty(ref _heading, value); }

    public float Groundspeed { get => _groundspeed; set => SetProperty(ref _groundspeed, value); }
    public float Airspeed { get => _airspeed; set => SetProperty(ref _airspeed, value); }
    public float ClimbRate { get => _climbRate; set => SetProperty(ref _climbRate, value); }

    public float Voltage { get => _voltage; set => SetProperty(ref _voltage, value); }
    public float Current { get => _current; set => SetProperty(ref _current, value); }
    public int BatteryRemaining { get => _batteryRemaining; set => SetProperty(ref _batteryRemaining, value); }

    public byte GpsFixType
    {
        get => _gpsFixType;
        set => SetProperty(ref _gpsFixType, value);
    }

    public byte GpsSatellites
    {
        get => _gpsSatellites;
        set => SetProperty(ref _gpsSatellites, value);
    }

    public string GpsFixString
    {
        get => _gpsFixString;
        set => SetProperty(ref _gpsFixString, value);
    }

    public float GpsHdop
    {
        get => _gpsHdop;
        set => SetProperty(ref _gpsHdop, value);
    }

    public bool HasGpsFix => GpsFixType >= 2;

    public bool LinkAlive { get => _linkAlive; set => SetProperty(ref _linkAlive, value); }
    public bool AttitudeFresh { get => _attitudeFresh; set => SetProperty(ref _attitudeFresh, value); }
    public bool PositionFresh { get => _positionFresh; set => SetProperty(ref _positionFresh, value); }

    private const double RadToDeg = 180.0 / Math.PI;

    public void UpdateState(VehicleState state)
    {
        if (state.Connection != null)
        {
            IsConnected = state.Connection.IsConnected;
            SystemId = state.Connection.SystemId;
            ComponentId = state.Connection.ComponentId;
            LastHeartbeat = state.Connection.LastHeartbeatUtc.ToLocalTime().ToString("HH:mm:ss");
        }

        if (state.FlightMode.HasValue)
            FlightMode = state.FlightMode.Value.ToString();

        // Update armed status
        IsArmed = state.IsArmed;
        OnPropertyChanged(nameof(ArmedStatus));
        OnPropertyChanged(nameof(ArmedColor));

        if (state.Attitude != null)
        {
            Roll = state.Attitude.RollRad * RadToDeg;
            Pitch = state.Attitude.PitchRad * RadToDeg;
            Yaw = state.Attitude.YawRad * RadToDeg;
        }

        if (state.Gps != null)
        {
            GpsFixType = state.Gps.FixType;
            GpsSatellites = state.Gps.SatellitesVisible;
            GpsFixString = state.Gps.FixTypeString;
            GpsHdop = state.Gps.HdopMeters;
        }

        if (state.Position != null)
        {
            Latitude = state.Position.LatitudeDeg;
            Longitude = state.Position.LongitudeDeg;
            Altitude = state.Position.AltitudeMslMeters;
            AltitudeRelative = state.Position.AltitudeRelMeters;
            Heading = state.Position.HeadingDeg;
        }

        if (state.VfrHud != null)
        {
            Groundspeed = state.VfrHud.GroundspeedMps;
            Airspeed = state.VfrHud.AirspeedMps;
            ClimbRate = state.VfrHud.ClimbMps;
        }

        if (state.Battery != null)
        {
            Voltage = state.Battery.VoltageVolts;
            Current = state.Battery.CurrentAmps;
            BatteryRemaining = state.Battery.RemainingPercent;
        }
    }

    public void UpdateHealth(HealthState health)
    {
        LinkAlive = health.LinkAlive;
        AttitudeFresh = health.AttitudeFresh;
        PositionFresh = health.PositionFresh;
    }

    public void Reset()
    {
        IsConnected = false; SystemId = 0; ComponentId = 0;
        FlightMode = "UNKNOWN"; LastHeartbeat = "N/A";
        IsArmed = false;
        Roll = Pitch = Yaw = 0;
        Latitude = Longitude = 0;
        Altitude = AltitudeRelative = Heading = 0;
        Groundspeed = Airspeed = ClimbRate = 0;
        Voltage = Current = 0; BatteryRemaining = 0;
        LinkAlive = AttitudeFresh = PositionFresh = false;
        GpsFixType = 0;
        GpsSatellites = 0;
        GpsFixString = "NO GPS";
        GpsHdop = 0;

        OnPropertyChanged(nameof(ArmedStatus));
        OnPropertyChanged(nameof(ArmedColor));
    }
}
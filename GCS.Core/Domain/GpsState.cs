namespace GCS.Core.Domain;

/// <summary>
/// GPS status from GPS_RAW_INT (msg 24)
/// </summary>
public record GpsState(
    byte FixType,           // 0=No GPS, 1=No Fix, 2=2D, 3=3D, 4=DGPS, 5=RTK Float, 6=RTK Fixed
    byte SatellitesVisible, // Number of satellites visible
    ushort Eph,             // GPS HDOP (horizontal dilution) * 100
    ushort Epv,             // GPS VDOP (vertical dilution) * 100
    DateTime TimestampUtc
)
{
    public string FixTypeString => FixType switch
    {
        0 => "NO GPS",
        1 => "NO FIX",
        2 => "2D FIX",
        3 => "3D FIX",
        4 => "DGPS",
        5 => "RTK FLOAT",
        6 => "RTK FIXED",
        _ => "UNKNOWN"
    };

    public bool HasFix => FixType >= 2;

    public bool IsRtk => FixType >= 5;

    public float HdopMeters => Eph / 100f;

    public float VdopMeters => Epv / 100f;
}
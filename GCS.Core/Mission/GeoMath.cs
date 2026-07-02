using System;

namespace GCS.Core.Mission;

/// <summary>Great-circle helpers shared by mission planning.</summary>
public static class GeoMath
{
    private const double EarthRadiusM = 6371000.0;

    /// <summary>Haversine distance between two lat/lon points, in metres.</summary>
    public static double DistanceMeters(double lat1, double lon1, double lat2, double lon2)
    {
        double dLat = ToRad(lat2 - lat1);
        double dLon = ToRad(lon2 - lon1);
        double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                   Math.Cos(ToRad(lat1)) * Math.Cos(ToRad(lat2)) *
                   Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return EarthRadiusM * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    /// <summary>Initial bearing from point 1 to point 2, in degrees (0..360, 0 = north).</summary>
    public static double BearingDeg(double lat1, double lon1, double lat2, double lon2)
    {
        double dLon = ToRad(lon2 - lon1);
        double y = Math.Sin(dLon) * Math.Cos(ToRad(lat2));
        double x = Math.Cos(ToRad(lat1)) * Math.Sin(ToRad(lat2)) -
                   Math.Sin(ToRad(lat1)) * Math.Cos(ToRad(lat2)) * Math.Cos(dLon);
        return (Math.Atan2(y, x) * 180 / Math.PI + 360) % 360;
    }

    private static double ToRad(double deg) => deg * Math.PI / 180.0;
}

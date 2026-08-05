namespace GCS.Core.Domain;

/// <summary>
/// Names for the mode numbers stored in FLTMODE1..6.
///
/// Those parameters hold raw numbers, and reading a flight-mode switch as
/// "FLTMODE1 = 10" is not much use to anyone.
/// </summary>
public static class FlightModeNames
{
    public static string Describe(int mode) =>
        System.Enum.IsDefined(typeof(FlightMode), mode)
            ? $"{(FlightMode)mode} ({mode})"
            : $"Unknown ({mode})";
}

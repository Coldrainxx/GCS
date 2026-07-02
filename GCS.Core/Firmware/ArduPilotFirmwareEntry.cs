using System;

namespace GCS.Core.Firmware;

/// <summary>
/// One flashable firmware build from the ArduPilot manifest.
/// </summary>
public sealed record ArduPilotFirmwareEntry(
    string VehicleType,   // Plane, Copter, Rover, ...
    string Platform,      // board name, e.g. Pixhawk1, CubeOrange
    string Version,       // e.g. 4.5.7
    string ReleaseType,   // OFFICIAL / STABLE / BETA / DEV
    string Url,           // .apj download URL
    int BoardId,          // must match the bootloader's board id
    bool Latest)
{
    /// <summary>Short release label ("STABLE-4.6.3" -> "STABLE").</summary>
    public string ShortType => ReleaseType.StartsWith("STABLE", StringComparison.OrdinalIgnoreCase)
        ? "STABLE"
        : ReleaseType;

    public string Display => $"{Version}  ({ShortType})";
}

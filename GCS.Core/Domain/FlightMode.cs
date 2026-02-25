namespace GCS.Core.Domain;

public enum FlightMode
{
    // ═══════════════════════════════════════════════════════════════
    // Fixed-Wing Modes (0-15)
    // ═══════════════════════════════════════════════════════════════
    Manual = 0,
    Circle = 1,
    Stabilize = 2,
    Training = 3,
    Acro = 4,
    Fbwa = 5,
    Fbwb = 6,
    Cruise = 7,
    Autotune = 8,
    Auto = 10,
    Rtl = 11,
    Loiter = 12,
    Takeoff = 13,
    AvoidAdsb = 14,
    Guided = 15,
    Initialising = 16,

    // ═══════════════════════════════════════════════════════════════
    // QuadPlane VTOL Modes (17-24)
    // ═══════════════════════════════════════════════════════════════
    QStabilize = 17,
    QHover = 18,
    QLoiter = 19,
    QLand = 20,
    QRtl = 21,
    QAutotune = 22,
    QAcro = 23,
    Thermal = 24,

    // ═══════════════════════════════════════════════════════════════
    // Unknown / Not Mapped
    // ═══════════════════════════════════════════════════════════════
    Unknown = 255
}
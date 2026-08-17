using System;
using System.Collections.Generic;

namespace GCS.Core.Mavlink;

/// <summary>
/// Moving a parameter value on and off the wire.
///
/// PARAM_SET and PARAM_VALUE both carry a float, but that float means different
/// things depending on the parameter's declared type. For an integer parameter
/// PX4 puts the integer's *bit pattern* in the float rather than converting it,
/// and rejects a write whose declared type does not match what it holds —
/// "param types mismatch param: FLW_TGT_ALT_M". ArduPilot stores everything as a
/// float and reports REAL32, so it is unaffected in either direction.
/// </summary>
public static class MavParamValue
{
    // MAV_PARAM_TYPE
    public const byte Uint8 = 1;
    public const byte Int8 = 2;
    public const byte Uint16 = 3;
    public const byte Int16 = 4;
    public const byte Uint32 = 5;
    public const byte Int32 = 6;
    public const byte Real32 = 9;

    public static bool IsInteger(byte paramType) =>
        paramType is Uint8 or Int8 or Uint16 or Int16 or Uint32 or Int32;

    /// <summary>
    /// Recover a parameter's real value from the float a vehicle sent.
    ///
    /// Read naively, PX4's MAV_SYS_ID = 1 arrives as 1.4e-45 — the float whose
    /// bits are 1.
    /// </summary>
    public static float Decode(float raw, byte paramType)
    {
        if (!IsInteger(paramType)) return raw;

        int bits = BitConverter.SingleToInt32Bits(raw);

        return paramType switch
        {
            Uint8 => (byte)bits,
            Int8 => (sbyte)bits,
            Uint16 => (ushort)bits,
            Int16 => (short)bits,
            Uint32 => (uint)bits,
            _ => bits,
        };
    }

    /// <summary>
    /// Put a value into the float field the way the vehicle expects to read it.
    /// The inverse of <see cref="Decode"/>.
    /// </summary>
    public static float Encode(float value, byte paramType)
    {
        if (!IsInteger(paramType)) return value;

        // Round rather than truncate: a UI that shows 2 may be holding 1.9999997.
        int rounded = (int)Math.Round(value);

        return BitConverter.Int32BitsToSingle(rounded);
    }

    /// <summary>
    /// Types for the PX4 integer parameters this app writes without reading them
    /// first, which is the one case the learned types cannot cover.
    ///
    /// Every screen that edits parameters reads them before offering an edit, so
    /// their types are known by the time anything is written. Applying a formation
    /// does not — it writes a computed set straight out — and the failsafe screen
    /// can be written before its read completes. All of these names are PX4-only,
    /// so there is nothing for them to collide with on an ArduPilot vehicle.
    /// </summary>
    private static readonly Dictionary<string, byte> WrittenWithoutReading = new(StringComparer.Ordinal)
    {
        ["FLW_TGT_ALT_M"] = Int32,   // altitude mode enum
        ["NAV_RCL_ACT"] = Int32,     // RC loss action enum
        ["NAV_DLL_ACT"] = Int32,     // data link loss action enum
    };

    /// <summary>
    /// The type to declare when writing <paramref name="paramId"/>.
    ///
    /// A type learned from the vehicle itself always wins — it is the truth for
    /// that firmware and that version. REAL32 is the fallback, which is correct
    /// for ArduPilot and for every PX4 float.
    /// </summary>
    public static byte TypeForWrite(string paramId, byte? learned)
    {
        if (learned is { } known) return known;

        return WrittenWithoutReading.TryGetValue(paramId, out var fallback)
            ? fallback
            : Real32;
    }
}

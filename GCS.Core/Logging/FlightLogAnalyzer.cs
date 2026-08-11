using System;
using System.Collections.Generic;
using System.Linq;
using GCS.Core.Advisor;
using GCS.Core.Domain;
using GCS.Core.Mavlink;
using MavLinkSharp;

namespace GCS.Core.Logging;

/// <summary>
/// Replays a .tlog and summarises the flight.
///
/// Decoding mirrors the live handlers, with one deliberate difference: state is
/// stamped with the time from the log rather than <c>DateTime.UtcNow</c>, so
/// staleness and trends are judged against when things actually happened. The
/// health rules are the same <see cref="FlightHealthAnalyzer"/> the live advisor
/// uses, so a replay cannot disagree with what was shown at the time.
/// </summary>
public static class FlightLogAnalyzer
{
    /// <summary>Health is re-evaluated at most this often; per-packet would be wasted work.</summary>
    private static readonly TimeSpan HealthSampleInterval = TimeSpan.FromSeconds(5);

    /// <summary>Trace resolution. One point a second is finer than any plot can show.</summary>
    private static readonly TimeSpan TraceSampleInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Ceiling on trace points, so an all-day log cannot balloon memory or stall
    /// rendering. Reached at roughly two hours; beyond that the trace is decimated.
    /// </summary>
    private const int MaxSamples = 7200;

    /// <summary>Consumer GPS wanders by a metre or so at rest; below this is noise.</summary>
    private const double MinDistanceStepM = 1.0;

    /// <summary>A single step longer than this is a fix glitch, not travel.</summary>
    private const double MaxPlausibleStepM = 1000.0;

    private const uint Heartbeat = 0;
    private const uint SysStatus = 1;
    private const uint GpsRawInt = 24;
    private const uint Attitude = 30;
    private const uint GlobalPositionInt = 33;
    private const uint ServoOutputRaw = 36;
    private const uint VfrHud = 74;
    private const uint PowerStatus = 125;
    private const uint EkfStatus = 193;
    private const uint Vibration = 241;
    private const uint StatusText = 253;

    private const byte MavModeFlagSafetyArmed = 0x80;

    public static FlightLogSummary Analyze(string path, byte? onlySystemId = null)
        => Analyze(TlogReader.Read(path), path, onlySystemId);

    public static FlightLogSummary Analyze(
        IEnumerable<TlogRecord> records, string path = "", byte? onlySystemId = null)
    {
        // Replay runs with no live link, so the dialect may not be registered yet.
        MavlinkBootstrap.EnsureInitialized();

        var summary = new FlightLogSummary { FilePath = path };

        var state = new VehicleState(null, null, null, null, null, null, null, false);
        var trend = new BatteryTrend();

        DateTime lastHealthCheck = DateTime.MinValue;
        DateTime lastSample = DateTime.MinValue;
        DateTime? armedSince = null;
        bool? wasArmed = null;
        string? lastModeName = null;
        var kind = Mavlink.VehicleKind.Unknown;
        var autopilot = Mavlink.AutopilotKind.Unknown;
        double? lastLat = null, lastLon = null;
        var findings = new HashSet<string>(StringComparer.Ordinal);

        foreach (var record in records)
        {
            var frame = new Frame();
            if (!frame.TryParse(record.Packet.Span)) continue;
            if (onlySystemId is byte want && frame.SystemId != want) continue;

            DateTime now = record.TimestampUtc;
            if (now == DateTime.MinValue) continue;   // corrupt timestamp

            summary.PacketCount++;
            summary.SystemIds.Add(frame.SystemId);

            if (summary.StartUtc == default) summary.StartUtc = now;
            summary.EndUtc = now;

            try
            {
                switch (frame.MessageId)
                {
                    case Heartbeat:
                    {
                        uint baseMode = Convert.ToUInt32(frame.Fields["base_mode"]);
                        uint customMode = Convert.ToUInt32(frame.Fields["custom_mode"]);
                        bool armed = (baseMode & MavModeFlagSafetyArmed) != 0;

                        // Mode numbers mean different things per vehicle family and
                        // per firmware, so decode against what this log was actually
                        // recorded from rather than assuming an ArduPlane. PX4 packs
                        // a main and sub mode into custom_mode, which read as an
                        // ArduPilot number gives values like 196608.
                        byte mavType = frame.Fields.TryGetValue("type", out var typeField)
                            ? Convert.ToByte(typeField) : (byte)0;

                        if (kind == Mavlink.VehicleKind.Unknown)
                            kind = Mavlink.ArdupilotFlightModes.KindFromMavType(mavType);

                        if (autopilot == Mavlink.AutopilotKind.Unknown &&
                            frame.Fields.TryGetValue("autopilot", out var autopilotField))
                        {
                            autopilot = Mavlink.Px4FlightModes.KindFromMavAutopilot(
                                Convert.ToByte(autopilotField));
                        }

                        string modeName = Mavlink.FlightModeTable.Describe(autopilot, kind, customMode);

                        // The plane-typed mode enum is ArduPlane's; it has no meaning
                        // on PX4, where FlightModeName is the only mode the log has.
                        var mode = autopilot == Mavlink.AutopilotKind.Px4
                            ? null
                            : Mavlink.ArdupilotFlightModes.PlaneMode(kind, customMode);

                        if (wasArmed != armed)
                        {
                            if (armed)
                            {
                                summary.ArmCount++;
                                armedSince = now;
                                summary.Events.Add(new FlightEvent(now, "Arm", "Armed"));
                            }
                            else if (armedSince is DateTime since)
                            {
                                summary.ArmedDuration += now - since;
                                armedSince = null;
                                summary.Events.Add(new FlightEvent(now, "Disarm", "Disarmed"));
                            }
                            wasArmed = armed;
                        }

                        if (lastModeName != modeName)
                        {
                            // The first heartbeat is the starting mode, not a change.
                            summary.Events.Add(new FlightEvent(now, "Mode",
                                lastModeName is null ? $"Mode {modeName}" : $"Mode {lastModeName} → {modeName}"));
                            lastModeName = modeName;
                        }

                        state = state with
                        {
                            FlightMode = mode,
                            FlightModeName = modeName,
                            Kind = kind,
                            Autopilot = autopilot,
                            IsArmed = armed,
                            Connection = new ConnectionState(true, frame.SystemId, frame.ComponentId, now),
                        };
                        break;
                    }

                    case SysStatus:
                    {
                        float volts = Convert.ToUInt16(frame.Fields["voltage_battery"]) / 1000f;
                        short currentRaw = Convert.ToInt16(frame.Fields["current_battery"]);
                        sbyte remaining = Convert.ToSByte(frame.Fields["battery_remaining"]);

                        state = state with
                        {
                            Battery = new BatteryState(volts, currentRaw >= 0 ? currentRaw / 100f : 0f,
                                remaining, now)
                        };

                        if (volts >= FlightHealthAnalyzer.MinPlausiblePackVolts)
                        {
                            trend.Add(now, volts);
                            if (!summary.HasBattery)
                            {
                                summary.HasBattery = true;
                                summary.BatteryStartVolts = volts;
                            }
                            summary.BatteryEndVolts = volts;
                            summary.BatteryMinVolts = Math.Min(summary.BatteryMinVolts, volts);
                            if (remaining > 0)
                                summary.BatteryMinPercent = Math.Min(summary.BatteryMinPercent, remaining);
                        }
                        break;
                    }

                    case Attitude:
                    {
                        state = state with
                        {
                            Attitude = new AttitudeState(
                                Convert.ToSingle(frame.Fields["roll"]),
                                Convert.ToSingle(frame.Fields["pitch"]),
                                Convert.ToSingle(frame.Fields["yaw"]),
                                now)
                        };
                        break;
                    }

                    case Vibration:
                    {
                        state = state with
                        {
                            Vibration = new VibrationState(
                                Convert.ToSingle(frame.Fields["vibration_x"]),
                                Convert.ToSingle(frame.Fields["vibration_y"]),
                                Convert.ToSingle(frame.Fields["vibration_z"]),
                                Convert.ToUInt32(frame.Fields["clipping_0"]),
                                Convert.ToUInt32(frame.Fields["clipping_1"]),
                                Convert.ToUInt32(frame.Fields["clipping_2"]),
                                now)
                        };
                        summary.HasVibration = true;
                        summary.MaxVibration = Math.Max(summary.MaxVibration, state.Vibration.Worst);
                        summary.MaxClipping = Math.Max(summary.MaxClipping, state.Vibration.TotalClipping);
                        break;
                    }

                    case EkfStatus:
                    {
                        state = state with
                        {
                            Ekf = new EkfStatusState(
                                Convert.ToUInt16(frame.Fields["flags"]),
                                Convert.ToSingle(frame.Fields["velocity_variance"]),
                                Convert.ToSingle(frame.Fields["pos_horiz_variance"]),
                                Convert.ToSingle(frame.Fields["pos_vert_variance"]),
                                Convert.ToSingle(frame.Fields["compass_variance"]),
                                Convert.ToSingle(frame.Fields["terrain_alt_variance"]),
                                now)
                        };
                        summary.HasEkf = true;
                        summary.MaxEkfVariance = Math.Max(summary.MaxEkfVariance, state.Ekf.WorstVariance);
                        break;
                    }

                    case ServoOutputRaw:
                    {
                        var raw = new ushort[16];
                        for (int i = 0; i < 16; i++)
                        {
                            string key = $"servo{i + 1}_raw";
                            raw[i] = frame.Fields.TryGetValue(key, out var v)
                                ? Convert.ToUInt16(v) : (ushort)0;
                        }

                        state = state with { ServoOutput = new ServoOutputState(raw, now) };
                        summary.HasServoOutput = true;

                        // Imbalance only means anything under power.
                        if (state.IsArmed)
                        {
                            var active = state.ServoOutput.Active();
                            if (active.Length >= 2)
                            {
                                double spread = (active.Max() - active.Min()) / 1000.0;
                                summary.MaxMotorImbalance = Math.Max(summary.MaxMotorImbalance, spread);
                            }
                        }
                        break;
                    }

                    case PowerStatus:
                    {
                        state = state with
                        {
                            Power = new PowerStatusState(
                                Convert.ToUInt16(frame.Fields["Vcc"]) / 1000f,
                                Convert.ToUInt16(frame.Fields["Vservo"]) / 1000f,
                                Convert.ToUInt16(frame.Fields["flags"]),
                                now)
                        };
                        summary.HasPower = true;
                        summary.MinRailVolts = summary.MinRailVolts <= 0
                            ? state.Power.RailVolts
                            : Math.Min(summary.MinRailVolts, state.Power.RailVolts);
                        break;
                    }

                    case GpsRawInt:
                    {
                        byte fix = Convert.ToByte(frame.Fields["fix_type"]);
                        byte sats = Convert.ToByte(frame.Fields["satellites_visible"]);
                        ushort eph = Convert.ToUInt16(frame.Fields["eph"]);

                        state = state with { Gps = new GpsState(fix, sats, eph, 0, now) };

                        summary.HasGps = true;
                        summary.WorstGpsFix = Math.Min(summary.WorstGpsFix, fix);
                        if (sats > 0) summary.MinSatellites = Math.Min(summary.MinSatellites, sats);
                        break;
                    }

                    case GlobalPositionInt:
                    {
                        double lat = Convert.ToInt32(frame.Fields["lat"]) / 1e7;
                        double lon = Convert.ToInt32(frame.Fields["lon"]) / 1e7;
                        float relAlt = Convert.ToInt32(frame.Fields["relative_alt"]) / 1000f;

                        // 0,0 is the "no fix yet" placeholder, not a position off Africa.
                        if (Math.Abs(lat) > 0.0001 || Math.Abs(lon) > 0.0001)
                        {
                            summary.HasPosition = true;

                            if (lastLat is double plat && lastLon is double plon)
                            {
                                double step = HaversineMetres(plat, plon, lat, lon);

                                // Only distance covered while armed counts as flown.
                                // A parked aircraft's GPS wanders by metres, which
                                // summed to 1.4 km over a 54-minute bench session —
                                // a plausible-looking number that meant nothing.
                                bool moving = state.IsArmed;

                                if (step > MaxPlausibleStepM)
                                {
                                    // Fix glitch: re-anchor without crediting the jump.
                                    lastLat = lat;
                                    lastLon = lon;
                                }
                                else if (step >= MinDistanceStepM)
                                {
                                    if (moving) summary.DistanceTravelledM += step;
                                    lastLat = lat;
                                    lastLon = lon;
                                }
                                // Below the threshold the reference point is kept, so
                                // genuinely slow movement still accumulates once it
                                // exceeds it.
                            }
                            else
                            {
                                lastLat = lat;
                                lastLon = lon;
                            }
                        }

                        summary.MaxAltitudeRelM = Math.Max(summary.MaxAltitudeRelM, relAlt);

                        state = state with
                        {
                            Position = new PositionState(lat, lon,
                                Convert.ToInt32(frame.Fields["alt"]) / 1000f, relAlt, 0, 0, 0, 0, now)
                        };
                        break;
                    }

                    case VfrHud:
                    {
                        float airspeed = Convert.ToSingle(frame.Fields["airspeed"]);
                        float groundspeed = Convert.ToSingle(frame.Fields["groundspeed"]);

                        summary.MaxAirspeedMps = Math.Max(summary.MaxAirspeedMps, airspeed);
                        summary.MaxGroundspeedMps = Math.Max(summary.MaxGroundspeedMps, groundspeed);

                        state = state with
                        {
                            VfrHud = new VfrHudState(airspeed, groundspeed, 0,
                                Convert.ToSingle(frame.Fields["climb"]), now)
                        };
                        break;
                    }

                    case StatusText:
                    {
                        byte severity = Convert.ToByte(frame.Fields["severity"]);
                        string text = ReadText(frame.Fields["text"]);

                        // 0-4 = emergency..warning. Info and debug would bury the
                        // timeline in routine chatter.
                        if (severity <= 4 && text.Length > 0)
                            summary.Events.Add(new FlightEvent(now, SeverityName(severity), text));
                        break;
                    }
                }
            }
            catch
            {
                // One malformed packet must not abandon the rest of the flight.
                continue;
            }

            if (now - lastSample >= TraceSampleInterval)
            {
                lastSample = now;
                summary.Samples.Add(new FlightSample(
                    now,
                    state.Position?.AltitudeRelMeters ?? 0f,
                    state.Battery?.VoltageVolts ?? 0f,
                    state.VfrHud?.GroundspeedMps ?? 0f,
                    state.Position?.LatitudeDeg ?? 0,
                    state.Position?.LongitudeDeg ?? 0,
                    state.Position != null,
                    state.IsArmed));
            }

            if (now - lastHealthCheck >= HealthSampleInterval)
            {
                lastHealthCheck = now;
                var report = FlightHealthAnalyzer.Analyze(state, now, trend);

                foreach (var component in report.Measured)
                    foreach (var evidence in component.Evidence)
                        findings.Add($"{component.Name}: {evidence.Text}");
            }
        }

        // Log ended while still armed — count the time up to the last packet.
        if (armedSince is DateTime openSince)
            summary.ArmedDuration += summary.EndUtc - openSince;

        Normalise(summary, findings);
        return summary;
    }

    private static void Normalise(FlightLogSummary summary, HashSet<string> findings)
    {
        if (summary.BatteryMinVolts == float.MaxValue) summary.BatteryMinVolts = 0;
        if (summary.BatteryMinPercent == int.MaxValue) summary.BatteryMinPercent = 0;
        if (summary.WorstGpsFix == byte.MaxValue) summary.WorstGpsFix = 0;
        if (summary.MinSatellites == byte.MaxValue) summary.MinSatellites = 0;

        summary.Findings.AddRange(findings.OrderBy(f => f, StringComparer.Ordinal));

        Decimate(summary);

        // State plainly what a telemetry log cannot contain, so a clean summary is
        // not mistaken for a clean aircraft.
        summary.Notes.Add("A .tlog records only what was received over the telemetry link.");
        if (!summary.HasBattery) summary.Notes.Add("No battery telemetry was recorded.");
        if (!summary.HasGps) summary.Notes.Add("No GPS telemetry was recorded.");
        if (!summary.HasPosition) summary.Notes.Add("No valid position was recorded.");
        if (!summary.HasVibration) summary.Notes.Add("No vibration data was recorded.");
        if (!summary.HasEkf) summary.Notes.Add("No EKF status was recorded.");
        if (!summary.HasServoOutput) summary.Notes.Add("No motor output data was recorded.");

        // Logs recorded before the GCS began requesting the health streams contain
        // none of the above, which is why they are called out individually.
        summary.Notes.Add("Full-rate sensor data (raw IMU, per-motor currents) is only " +
                          "in an onboard .bin dataflash log, never a telemetry log.");
    }

    /// <summary>
    /// Thin an over-long trace by keeping every nth point. Crude on purpose: peaks
    /// are already reported exactly as max values, so the trace only has to convey
    /// the shape of the flight.
    /// </summary>
    private static void Decimate(FlightLogSummary summary)
    {
        if (summary.Samples.Count <= MaxSamples) return;

        int stride = (summary.Samples.Count / MaxSamples) + 1;
        var kept = new List<FlightSample>(MaxSamples + 1);

        for (int i = 0; i < summary.Samples.Count; i += stride)
            kept.Add(summary.Samples[i]);

        summary.Samples.Clear();
        summary.Samples.AddRange(kept);
    }

    private static string SeverityName(byte severity) => severity switch
    {
        0 => "Emergency",
        1 => "Alert",
        2 => "Critical",
        3 => "Error",
        _ => "Warning",
    };

    /// <summary>STATUSTEXT arrives as a char array or string depending on the decoder.</summary>
    private static string ReadText(object? raw)
    {
        string text = raw switch
        {
            null => "",
            string s => s,
            byte[] bytes => System.Text.Encoding.ASCII.GetString(bytes),
            char[] chars => new string(chars),
            System.Collections.IEnumerable seq => new string(seq.Cast<object>()
                .Select(o => Convert.ToChar(o)).ToArray()),
            _ => raw.ToString() ?? "",
        };

        return text.TrimEnd('\0', ' ');
    }

    private const double EarthRadiusM = 6371000.0;

    private static double HaversineMetres(double lat1, double lon1, double lat2, double lon2)
    {
        double dLat = (lat2 - lat1) * Math.PI / 180.0;
        double dLon = (lon2 - lon1) * Math.PI / 180.0;
        double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                   Math.Cos(lat1 * Math.PI / 180.0) * Math.Cos(lat2 * Math.PI / 180.0) *
                   Math.Sin(dLon / 2) * Math.Sin(dLon / 2);

        return 2 * EarthRadiusM * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }
}

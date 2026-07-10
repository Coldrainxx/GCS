using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using GCS.Core.Domain;
using GCS.Core.Mavlink.Messages;

namespace GCS.ViewModels;

/// <summary>
/// Host for the "Mandatory Hardware" setup screens (Mission-Planner-style).
/// Owns the left-nav sections and routes vehicle data to the child screens.
/// </summary>
public sealed class SetupViewModel : ViewModelBase
{
    public FlightModesViewModel FlightModes { get; }
    public RadioCalibrationViewModel RadioCal { get; }
    public AccelCalibrationViewModel AccelCal { get; }
    public CompassViewModel Compass { get; }
    public ServoOutputViewModel ServoOutput { get; }
    public PidTuningViewModel BasicTuning { get; }
    public PidTuningViewModel ExtendedTuning { get; }

    public ObservableCollection<SetupSection> Sections { get; } = new();

    private bool _connected;

    private SetupSection? _selected;
    public SetupSection? SelectedSection
    {
        get => _selected;
        set { if (SetProperty(ref _selected, value)) AutoRead(value); }
    }

    /// <summary>Read a section's vehicle values automatically when it's opened while connected.</summary>
    private void AutoRead(SetupSection? section)
    {
        if (!_connected || section == null) return;
        Func<Task>? read = section.Content switch
        {
            FlightModesViewModel fm => fm.RefreshAsync,
            ServoOutputViewModel so => so.RefreshAsync,
            CompassViewModel c => c.RefreshAsync,
            PidTuningViewModel p => p.RefreshAsync,
            FailsafeViewModel f => f.RefreshFailsafeParams,
            _ => null
        };
        if (read == null) return;
        _ = SafeReadAsync(read);
    }

    private static async Task SafeReadAsync(Func<Task> read)
    {
        try { await read(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Setup] Auto-read failed: {ex.Message}"); }
    }

    public SetupViewModel(
        Func<string, float, Task> setParam,
        Func<string, Task> requestParam,
        Func<ushort, float, float, float, float, float, float, float, Task> sendCommand,
        FailsafeViewModel failsafe,
        FirmwareViewModel firmware)
    {
        FlightModes = new FlightModesViewModel(setParam, requestParam);
        RadioCal = new RadioCalibrationViewModel(setParam, requestParam);
        AccelCal = new AccelCalibrationViewModel(sendCommand);
        Compass = new CompassViewModel(setParam, requestParam, sendCommand);
        ServoOutput = new ServoOutputViewModel(setParam, requestParam);
        BasicTuning = new PidTuningViewModel(
            "BASIC TUNING",
            "Fixed-wing attitude controller gains. Read first, then adjust — values are written to the vehicle as you edit.",
            BasicTuningGroups(), setParam, requestParam);
        ExtendedTuning = new PidTuningViewModel(
            "QUADPLANE EXTENDED TUNING",
            "VTOL rate controller gains (Q_A_RAT_*). Read first, then adjust — values are written live.",
            ExtendedTuningGroups(), setParam, requestParam);

        Sections.Add(new SetupSection("Install Firmware", firmware));
        Sections.Add(new SetupSection("Accel Calibration", AccelCal));
        Sections.Add(new SetupSection("Compass", Compass));
        Sections.Add(new SetupSection("Radio Calibration", RadioCal));
        Sections.Add(new SetupSection("Servo Output", ServoOutput));
        Sections.Add(new SetupSection("ESC Calibration", new EscCalibrationViewModel()));
        var flightModes = new SetupSection("Flight Modes", FlightModes);
        Sections.Add(flightModes);
        Sections.Add(new SetupSection("FailSafe", failsafe));
        Sections.Add(new SetupSection("Basic Tuning", BasicTuning));
        Sections.Add(new SetupSection("Extended Tuning", ExtendedTuning));

        SelectedSection = flightModes;
    }

    private static IReadOnlyList<PidGroupDef> BasicTuningGroups() => new[]
    {
        new PidGroupDef("Servo Roll PID", new[]
        {
            new PidFieldDef("P", "RLL_RATE_P"),
            new PidFieldDef("I", "RLL_RATE_I"),
            new PidFieldDef("D", "RLL_RATE_D"),
            new PidFieldDef("INT_MAX", "RLL_RATE_IMAX"),
        }),
        new PidGroupDef("Servo Pitch PID", new[]
        {
            new PidFieldDef("P", "PTCH_RATE_P"),
            new PidFieldDef("I", "PTCH_RATE_I"),
            new PidFieldDef("D", "PTCH_RATE_D"),
            new PidFieldDef("INT_MAX", "PTCH_RATE_IMAX"),
        }),
        new PidGroupDef("Servo Yaw", new[]
        {
            new PidFieldDef("Yaw 2 Roll", "YAW2SRV_RLL"),
            new PidFieldDef("Integral", "YAW2SRV_INT"),
            new PidFieldDef("Dampening", "YAW2SRV_DAMP"),
            new PidFieldDef("Integrator Max", "YAW2SRV_IMAX"),
        }),
    };

    private static IReadOnlyList<PidGroupDef> ExtendedTuningGroups() => new[]
    {
        RateGroup("Rate Roll", "RLL"),
        RateGroup("Rate Pitch", "PIT"),
        RateGroup("Rate Yaw", "YAW"),
    };

    private static PidGroupDef RateGroup(string title, string axis) => new(title, new[]
    {
        new PidFieldDef("P", $"Q_A_RAT_{axis}_P"),
        new PidFieldDef("I", $"Q_A_RAT_{axis}_I"),
        new PidFieldDef("D", $"Q_A_RAT_{axis}_D"),
        new PidFieldDef("IMAX", $"Q_A_RAT_{axis}_IMAX"),
        new PidFieldDef("FLTE", $"Q_A_RAT_{axis}_FLTE"),
        new PidFieldDef("FLTD", $"Q_A_RAT_{axis}_FLTD"),
        new PidFieldDef("FLTT", $"Q_A_RAT_{axis}_FLTT"),
    });

    public void OnParameter(string name, float value)
    {
        FlightModes.OnParameter(name, value);
        RadioCal.OnParameter(name, value);
        ServoOutput.OnParameter(name, value);
        Compass.OnParameter(name, value);
        BasicTuning.OnParameter(name, value);
        ExtendedTuning.OnParameter(name, value);
    }

    public void OnServoOutput(ServoOutputData data)
    {
        ServoOutput.OnServoOutput(data);
    }

    public void OnRcChannels(RcChannelsData data)
    {
        FlightModes.OnRcChannels(data);
        RadioCal.OnRcChannels(data);
    }

    public void OnMessage(AutopilotMessage message)
    {
        AccelCal.OnMessage(message);
    }

    public void OnMagCalProgress(MagCalProgressData data) => Compass.OnProgress(data);
    public void OnMagCalReport(MagCalReportData data) => Compass.OnReport(data);

    public void UpdateConnectionState(bool connected)
    {
        _connected = connected;
        FlightModes.SetConnected(connected);
        RadioCal.SetConnected(connected);
        AccelCal.SetConnected(connected);
        Compass.SetConnected(connected);
        ServoOutput.SetConnected(connected);
        BasicTuning.SetConnected(connected);
        ExtendedTuning.SetConnected(connected);

        // Freshly connected: populate whichever section is on screen.
        if (connected) AutoRead(SelectedSection);
    }
}

/// <summary>One entry in the setup left-nav: a title and the screen VM it shows.</summary>
public sealed class SetupSection
{
    public string Title { get; }
    public object Content { get; }
    public SetupSection(string title, object content)
    {
        Title = title;
        Content = content;
    }
}

/// <summary>Placeholder content for a setup screen that isn't built yet.</summary>
public sealed class SetupPlaceholderViewModel : ViewModelBase
{
    public string Title { get; }
    public string Message { get; }
    public SetupPlaceholderViewModel(string title, string message)
    {
        Title = title;
        Message = message;
    }
}

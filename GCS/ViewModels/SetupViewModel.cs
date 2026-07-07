using System;
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

    public ObservableCollection<SetupSection> Sections { get; } = new();

    private SetupSection? _selected;
    public SetupSection? SelectedSection
    {
        get => _selected;
        set => SetProperty(ref _selected, value);
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

        Sections.Add(new SetupSection("Install Firmware", firmware));
        Sections.Add(new SetupSection("Accel Calibration", AccelCal));
        Sections.Add(new SetupSection("Compass", Compass));
        Sections.Add(new SetupSection("Radio Calibration", RadioCal));
        Sections.Add(new SetupSection("Servo Output", ServoOutput));
        Sections.Add(new SetupSection("ESC Calibration", new EscCalibrationViewModel()));
        var flightModes = new SetupSection("Flight Modes", FlightModes);
        Sections.Add(flightModes);
        Sections.Add(new SetupSection("FailSafe", failsafe));

        SelectedSection = flightModes;
    }

    public void OnParameter(string name, float value)
    {
        FlightModes.OnParameter(name, value);
        RadioCal.OnParameter(name, value);
        ServoOutput.OnParameter(name, value);
        Compass.OnParameter(name, value);
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
        FlightModes.SetConnected(connected);
        RadioCal.SetConnected(connected);
        AccelCal.SetConnected(connected);
        Compass.SetConnected(connected);
        ServoOutput.SetConnected(connected);
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

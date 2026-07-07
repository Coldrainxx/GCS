namespace GCS.ViewModels;

/// <summary>
/// ESC calibration is a boot-time / manual procedure on QuadPlane and is unsafe
/// to automate (motors spin), so this screen is guidance only. Marker type so the
/// setup nav can resolve its view.
/// </summary>
public sealed class EscCalibrationViewModel : ViewModelBase
{
}

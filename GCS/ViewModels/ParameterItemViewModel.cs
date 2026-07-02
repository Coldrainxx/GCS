using System;
using System.Globalization;
using GCS.Parameters;

namespace GCS.ViewModels;

/// <summary>
/// One editable parameter row: the onboard value received from the vehicle plus
/// the (possibly edited) value the user wants to write.
/// </summary>
public sealed class ParameterItemViewModel : ViewModelBase
{
    public ParameterDef Def { get; }

    public ParameterItemViewModel(ParameterDef def) => Def = def;

    public string Name => Def.Name;
    public string Group => Def.Group;
    public string Label => Def.Label;
    public string Description => Def.Description;
    public string Units => Def.Units;
    public string RangeText => Def.RangeText;

    private string? _resolvedName;   // actual name the vehicle reported (may be an alias)
    private float? _onboardValue;
    private double _editValue;
    private bool _hasValue;

    /// <summary>Name to write back to (the alias the vehicle actually uses).</summary>
    public string ResolvedName => _resolvedName ?? Def.Name;

    public bool HasValue
    {
        get => _hasValue;
        private set { if (SetProperty(ref _hasValue, value)) { OnPropertyChanged(nameof(OnboardText)); OnPropertyChanged(nameof(IsDirty)); OnPropertyChanged(nameof(IsOutOfRange)); } }
    }

    /// <summary>True when the edited value falls outside the parameter's documented range.</summary>
    public bool IsOutOfRange =>
        HasValue && Def.Min.HasValue && Def.Max.HasValue &&
        (EditValue < Def.Min.Value || EditValue > Def.Max.Value);

    public float? OnboardValue
    {
        get => _onboardValue;
        private set { if (SetProperty(ref _onboardValue, value)) { OnPropertyChanged(nameof(OnboardText)); OnPropertyChanged(nameof(IsDirty)); } }
    }

    /// <summary>The value bound to the editable cell.</summary>
    public double EditValue
    {
        get => _editValue;
        set { if (SetProperty(ref _editValue, value)) { OnPropertyChanged(nameof(IsDirty)); OnPropertyChanged(nameof(IsOutOfRange)); } }
    }

    public string OnboardText => HasValue
        ? _onboardValue!.Value.ToString("0.#####", CultureInfo.InvariantCulture)
        : "—";

    /// <summary>True when the user's value differs from the onboard value.</summary>
    public bool IsDirty => HasValue && Math.Abs(EditValue - _onboardValue!.Value) > 1e-6;

    /// <summary>Apply a PARAM_VALUE from the vehicle without clobbering an in-progress edit.</summary>
    public void SetFromVehicle(string name, float value)
    {
        bool wasDirty = IsDirty;
        _resolvedName = name;
        OnboardValue = value;
        HasValue = true;
        if (!wasDirty) EditValue = value;
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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
    public bool HasOptions => Def.HasOptions;
    public IReadOnlyList<ParamOption>? Options => Def.Options;
    public bool HasBits => Def.HasBits;

    private List<ParameterBitOptionViewModel>? _bitOptions;
    /// <summary>Checkbox rows for a bitmask parameter (null for non-bitmask params).</summary>
    public IReadOnlyList<ParameterBitOptionViewModel>? BitOptions
    {
        get
        {
            if (!HasBits) return null;
            return _bitOptions ??= Def.Bits!
                .Select(b => new ParameterBitOptionViewModel(this, b.Mask, b.Label))
                .ToList();
        }
    }

    /// <summary>Human-readable summary of the checked bits, shown on the bitmask button.</summary>
    public string BitmaskSummary
    {
        get
        {
            if (!HasBits) return "";
            long v = (long)EditValue;
            if (v == 0) return "None";
            var names = Def.Bits!.Where(b => (v & b.Mask) != 0).Select(b => b.Label).ToList();
            return names.Count > 0 ? string.Join(", ", names) : v.ToString();
        }
    }

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
        set
        {
            if (SetProperty(ref _editValue, value))
            {
                OnPropertyChanged(nameof(IsDirty));
                OnPropertyChanged(nameof(IsOutOfRange));
                if (HasBits)
                {
                    if (_bitOptions != null)
                        foreach (var b in _bitOptions) b.Refresh();
                    OnPropertyChanged(nameof(BitmaskSummary));
                }
            }
        }
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

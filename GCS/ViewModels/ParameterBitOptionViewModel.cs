namespace GCS.ViewModels;

/// <summary>
/// One checkbox in a bitmask parameter editor. Reads/writes a single bit of the
/// owning row's <see cref="ParameterItemViewModel.EditValue"/>.
/// </summary>
public sealed class ParameterBitOptionViewModel : ViewModelBase
{
    private readonly ParameterItemViewModel _owner;

    public int Mask { get; }
    public string Label { get; }

    public ParameterBitOptionViewModel(ParameterItemViewModel owner, int mask, string label)
    {
        _owner = owner;
        Mask = mask;
        Label = label;
    }

    public bool IsChecked
    {
        get => ((long)_owner.EditValue & (uint)Mask) != 0;
        set
        {
            long current = (long)_owner.EditValue;
            long next = value ? (current | (uint)Mask) : (current & ~(uint)Mask);
            if (next != current)
                _owner.EditValue = next; // triggers the owner to refresh every bit + summary
        }
    }

    public void Refresh() => OnPropertyChanged(nameof(IsChecked));
}

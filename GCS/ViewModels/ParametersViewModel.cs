using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using GCS.Parameters;

namespace GCS.ViewModels;

/// <summary>
/// "Most used" parameter editor (Mission Planner style): grouped, filterable list
/// of curated parameters you can read from and write back to the vehicle.
/// </summary>
public sealed class ParametersViewModel : ViewModelBase
{
    private readonly Func<string, float, Task>? _setParam;
    private readonly Func<string, Task>? _requestParam;

    public ObservableCollection<ParameterItemViewModel> Items { get; }
    public ICollectionView ItemsView { get; }

    private string _filter = "";
    private bool _isConnected;
    private bool _isLoading;
    private string _statusMessage = "Not connected";

    public string Filter
    {
        get => _filter;
        set { if (SetProperty(ref _filter, value)) ItemsView.Refresh(); }
    }

    public bool IsConnected
    {
        get => _isConnected;
        set { if (SetProperty(ref _isConnected, value)) CommandManager.InvalidateRequerySuggested(); }
    }

    public bool IsLoading
    {
        get => _isLoading;
        set { if (SetProperty(ref _isLoading, value)) CommandManager.InvalidateRequerySuggested(); }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    public ICommand RefreshCommand { get; }
    public ICommand WriteAllCommand { get; }

    public ParametersViewModel() : this(null, null) { }

    public ParametersViewModel(Func<string, float, Task>? setParam, Func<string, Task>? requestParam)
    {
        _setParam = setParam;
        _requestParam = requestParam;

        Items = new ObservableCollection<ParameterItemViewModel>(
            ParameterCatalog.All.Select(d => new ParameterItemViewModel(d)));

        ItemsView = CollectionViewSource.GetDefaultView(Items);
        ItemsView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ParameterItemViewModel.Group)));
        ItemsView.Filter = FilterItem;

        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => IsConnected && !IsLoading);
        WriteAllCommand = new AsyncRelayCommand(WriteAllAsync, () => IsConnected && !IsLoading);
    }

    private bool FilterItem(object obj)
    {
        if (string.IsNullOrWhiteSpace(_filter)) return true;
        var item = (ParameterItemViewModel)obj;
        var f = _filter.Trim();
        return item.Name.Contains(f, StringComparison.OrdinalIgnoreCase)
            || item.Label.Contains(f, StringComparison.OrdinalIgnoreCase)
            || item.Description.Contains(f, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Route a PARAM_VALUE (from MainViewModel) to the matching row.</summary>
    public void OnParameterReceived(string name, float value)
    {
        Application.Current?.Dispatcher?.BeginInvoke(() =>
        {
            foreach (var item in Items)
            {
                if (item.Def.Matches(name))
                {
                    item.SetFromVehicle(name, value);
                    break;
                }
            }
        });
    }

    public async Task RefreshAsync()
    {
        if (_requestParam == null) return;

        IsLoading = true;
        StatusMessage = "Requesting parameters...";
        try
        {
            foreach (var item in Items)
            {
                foreach (var name in item.Def.Names)
                {
                    await _requestParam(name);
                    await Task.Delay(30);
                }
            }
            StatusMessage = "Parameters requested";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
            Debug.WriteLine($"[Parameters] Refresh error: {ex.Message}");
        }
        finally
        {
            IsLoading = false;
        }
    }

    public async Task WriteAllAsync()
    {
        if (_setParam == null) return;

        // Write every parameter that has actually been loaded from the vehicle.
        // Un-loaded rows are skipped so we never push a default 0 over a real setting.
        var toWrite = Items.Where(i => i.HasValue).ToList();
        if (toWrite.Count == 0)
        {
            StatusMessage = "Nothing loaded yet - click Refresh first";
            return;
        }

        var outOfRange = toWrite.Where(i => i.IsOutOfRange).ToList();
        if (outOfRange.Count > 0)
        {
            var list = string.Join("\n • ", outOfRange.Take(15)
                .Select(i => $"{i.Name} = {i.EditValue} (range {i.RangeText})"));
            if (outOfRange.Count > 15) list += $"\n … and {outOfRange.Count - 15} more";

            var choice = System.Windows.MessageBox.Show(
                $"{outOfRange.Count} value(s) are outside their recommended range:\n\n • {list}\n\nWrite anyway?",
                "Out-of-range parameters",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Warning);
            if (choice != System.Windows.MessageBoxResult.Yes)
            {
                StatusMessage = "Write cancelled";
                return;
            }
        }

        IsLoading = true;
        StatusMessage = $"Writing {toWrite.Count} parameter(s)...";
        try
        {
            foreach (var item in toWrite)
            {
                await _setParam(item.ResolvedName, (float)item.EditValue);
                await Task.Delay(40);
                if (_requestParam != null) await _requestParam(item.ResolvedName); // read back to confirm
            }
            StatusMessage = $"Wrote {toWrite.Count} parameter(s)";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
            Debug.WriteLine($"[Parameters] Write error: {ex.Message}");
        }
        finally
        {
            IsLoading = false;
        }
    }

    public void UpdateConnectionState(bool connected)
    {
        IsConnected = connected;
        StatusMessage = connected ? "Connected - click Refresh to load" : "Not connected";
    }
}

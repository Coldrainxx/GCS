using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;

namespace GCS.ViewModels;

/// <summary>Definition of one tuning field: a display label and its parameter name.</summary>
public sealed record PidFieldDef(string Label, string Param);

/// <summary>Definition of one tuning group (a Mission-Planner-style box of fields).</summary>
public sealed record PidGroupDef(string Title, IReadOnlyList<PidFieldDef> Fields);

/// <summary>
/// Generic PID / tuning editor: a set of groups (roll/pitch/yaw) each with a few
/// numeric parameters. Values read from and write to the vehicle live, mirroring
/// Mission Planner's Basic / Extended tuning screens.
/// </summary>
public sealed class PidTuningViewModel : ViewModelBase
{
    private readonly Func<string, float, Task> _setParam;
    private readonly Func<string, Task> _requestParam;
    private readonly Dictionary<string, PidField> _byParam = new(StringComparer.OrdinalIgnoreCase);
    private bool _applying;

    public string Title { get; }
    public string Description { get; }
    public ObservableCollection<PidGroup> Groups { get; } = new();

    private bool _isConnected;
    public bool IsConnected
    {
        get => _isConnected;
        private set { if (SetProperty(ref _isConnected, value)) CommandManager.InvalidateRequerySuggested(); }
    }

    private string _status = "Not connected";
    public string Status { get => _status; private set => SetProperty(ref _status, value); }

    public ICommand RefreshCommand { get; }

    public PidTuningViewModel(
        string title, string description,
        IReadOnlyList<PidGroupDef> defs,
        Func<string, float, Task> setParam, Func<string, Task> requestParam)
    {
        Title = title;
        Description = description;
        _setParam = setParam;
        _requestParam = requestParam;

        foreach (var g in defs)
        {
            var group = new PidGroup(g.Title);
            foreach (var f in g.Fields)
            {
                var field = new PidField(f.Label, f.Param, Write);
                group.Fields.Add(field);
                _byParam[f.Param] = field;
            }
            Groups.Add(group);
        }

        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => IsConnected);
    }

    public void SetConnected(bool connected)
    {
        IsConnected = connected;
        if (!connected) Status = "Not connected";
    }

    private void Write(string param, float value)
    {
        if (_applying) return;
        _ = WriteAsync(param, value);
    }

    private async Task WriteAsync(string param, float value)
    {
        try
        {
            await _setParam(param, value);
            Status = $"{param} = {value:0.#####}";
        }
        catch (Exception ex)
        {
            Status = $"Write error: {ex.Message}";
        }
    }

    public async Task RefreshAsync()
    {
        Status = "Reading tuning parameters…";
        foreach (var param in _byParam.Keys)
        {
            await _requestParam(param);
            await Task.Delay(15);
        }
        Status = "Tuning parameters read.";
    }

    public void OnParameter(string name, float value)
    {
        if (!_byParam.TryGetValue(name, out var field)) return;
        Application.Current?.Dispatcher?.BeginInvoke(() =>
        {
            _applying = true;
            try { field.Value = value; }
            finally { _applying = false; }
        });
    }
}

public sealed class PidGroup
{
    public string Title { get; }
    public ObservableCollection<PidField> Fields { get; } = new();
    public PidGroup(string title) => Title = title;
}

public sealed class PidField : ViewModelBase
{
    private readonly Action<string, float> _write;

    public string Label { get; }
    public string Param { get; }

    public PidField(string label, string param, Action<string, float> write)
    {
        Label = label;
        Param = param;
        _write = write;
    }

    private float _value;
    public float Value
    {
        get => _value;
        set { if (SetProperty(ref _value, value)) _write(Param, value); }
    }
}

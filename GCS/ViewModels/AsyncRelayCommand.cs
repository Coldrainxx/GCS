using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows.Input;

namespace GCS.ViewModels;

/// <summary>
/// ICommand implementation for async operations.
/// Unlike wrapping async lambdas in Action (which creates async void),
/// this properly awaits the task and catches exceptions.
/// </summary>
public class AsyncRelayCommand : ICommand
{
    private readonly Func<Task> _execute;
    private readonly Func<bool>? _canExecute;
    private bool _isExecuting;

    public AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) =>
        !_isExecuting && (_canExecute?.Invoke() ?? true);

    public async void Execute(object? parameter)
    {
        if (_isExecuting) return;

        _isExecuting = true;
        CommandManager.InvalidateRequerySuggested();

        try
        {
            await _execute();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AsyncRelayCommand] Unhandled exception: {ex}");
        }
        finally
        {
            _isExecuting = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }
}
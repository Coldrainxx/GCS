using System;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using GCS.Notifications;

namespace GCS.ViewModels;

/// <summary>A single on-screen toast.</summary>
public sealed class ToastViewModel : ViewModelBase
{
    public string Message { get; }
    public ToastSeverity Severity { get; }

    public ToastViewModel(ToastSeverity severity, string message)
    {
        Severity = severity;
        Message = message;
    }

    public string Color => Severity switch
    {
        ToastSeverity.Success => "#3FB950",
        ToastSeverity.Warning => "#FF9500",
        ToastSeverity.Error => "#F85149",
        _ => "#58A6FF"
    };

    public string Icon => Severity switch
    {
        ToastSeverity.Success => "✓",
        ToastSeverity.Warning => "⚠",
        ToastSeverity.Error => "✕",
        _ => "ℹ"
    };
}

/// <summary>Collects toasts raised via <see cref="Notifier"/> and auto-dismisses them.</summary>
public sealed class ToastsViewModel : ViewModelBase
{
    public ObservableCollection<ToastViewModel> Items { get; } = new();

    public ICommand DismissCommand { get; }

    public ToastsViewModel()
    {
        DismissCommand = new RelayCommand<ToastViewModel>(t => { if (t != null) Items.Remove(t); });
        Notifier.Toast += OnToast;
    }

    private void OnToast(ToastSeverity severity, string message)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null) return;

        dispatcher.BeginInvoke(() =>
        {
            var toast = new ToastViewModel(severity, message);
            Items.Add(toast);

            var timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(severity == ToastSeverity.Error ? 6 : 4)
            };
            timer.Tick += (_, _) => { timer.Stop(); Items.Remove(toast); };
            timer.Start();
        });
    }
}

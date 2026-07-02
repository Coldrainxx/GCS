using System;

namespace GCS.Notifications;

public enum ToastSeverity { Info, Success, Warning, Error }

/// <summary>
/// App-wide notification hub. Any code can raise a toast; the ToastsViewModel
/// subscribes and renders them. Keeps callers decoupled from the UI.
/// </summary>
public static class Notifier
{
    public static event Action<ToastSeverity, string>? Toast;

    public static void Info(string message) => Toast?.Invoke(ToastSeverity.Info, message);
    public static void Success(string message) => Toast?.Invoke(ToastSeverity.Success, message);
    public static void Warning(string message) => Toast?.Invoke(ToastSeverity.Warning, message);
    public static void Error(string message) => Toast?.Invoke(ToastSeverity.Error, message);
}

using GCS.Core.Domain;
using System;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;

namespace GCS.ViewModels;

public class MessagesViewModel : ViewModelBase
{
    private const int MaxMessages = 500;

    public ObservableCollection<MessageItemViewModel> Messages { get; } = new();

    public ICommand ClearCommand { get; }

    public MessagesViewModel()
    {
        ClearCommand = new RelayCommand(Clear);
    }
    
    public void AddMessage(AutopilotMessage message)
    {
        // Must update ObservableCollection on UI thread
        if (Application.Current?.Dispatcher?.CheckAccess() == false)
        {
            Application.Current.Dispatcher.BeginInvoke(() => AddMessageInternal(message));
        }
        else
        {
            AddMessageInternal(message);
        }
    }

    private void AddMessageInternal(AutopilotMessage message)
    {
        // Add at the beginning (newest first)
        Messages.Insert(0, new MessageItemViewModel(message));

        // Trim old messages
        while (Messages.Count > MaxMessages)
        {
            Messages.RemoveAt(Messages.Count - 1);
        }
    }

    private void Clear()
    {
        Messages.Clear();
    }
}

public class MessageItemViewModel : ViewModelBase
{
    public AutopilotMessageSeverity Severity { get; }
    public string Text { get; }
    public string Timestamp { get; }
    public string SeverityText { get; }
    public string SeverityColor { get; }

    public MessageItemViewModel(AutopilotMessage message)
    {
        Severity = message.Severity;
        Text = message.Text;
        Timestamp = message.TimestampUtc.ToLocalTime().ToString("HH:mm:ss.fff");

        SeverityText = message.Severity switch
        {
            AutopilotMessageSeverity.Critical => "CRIT",
            AutopilotMessageSeverity.Error => "ERR",
            AutopilotMessageSeverity.Warning => "WARN",
            _ => "INFO"
        };

        SeverityColor = message.Severity switch
        {
            AutopilotMessageSeverity.Critical => "#F44336",
            AutopilotMessageSeverity.Error => "#FF5722",
            AutopilotMessageSeverity.Warning => "#FF9800",
            _ => "#4CAF50"
        };
    }
}
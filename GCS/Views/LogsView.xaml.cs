using System.Windows;
using System.Windows.Controls;
using GCS.ViewModels;
using Microsoft.Win32;

namespace GCS.Views;

public partial class LogsView : UserControl
{
    public LogsView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (DataContext is not LogsViewModel vm) return;

        // The dialog is a view concern; the ViewModel just asks for a path.
        vm.PickFile = () =>
        {
            var dialog = new OpenFileDialog
            {
                Title = "Open telemetry log",
                Filter = "Telemetry logs (*.tlog)|*.tlog|All files (*.*)|*.*",
                InitialDirectory = LogsViewModel.DefaultLogDirectory,
            };

            return dialog.ShowDialog() == true ? dialog.FileName : null;
        };
    }

    /// <summary>
    /// Hand the analysed flight to the assistant and close this panel — the popup
    /// lives over the main content, which is hidden while the log view is open.
    /// </summary>
    private void AskAdvisor_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not LogsViewModel { Summary: not null } vm) return;
        if (Window.GetWindow(this) is not MainWindow window) return;
        if (window.DataContext is not MainViewModel main) return;

        main.Advisor.ReviewLog(vm.Summary);
        window.CloseFullScreenViews();
    }
}

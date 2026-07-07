using GCS.Core.Mavlink;
using GCS.ViewModels;
using System.Windows;
using System.Windows.Input;

namespace GCS;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow()
    {
        InitializeComponent();

        // Initialize MAVLink
        MavlinkBootstrap.Init();

        // Create and set ViewModel
        _viewModel = new MainViewModel();
        DataContext = _viewModel;

        MouseLeftButtonDown += (s, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed)
                DragMove();
        };
    }

    private void LinkButton_Click(object sender, RoutedEventArgs e)
    {
        // Toggle connection popup
        ConnectionPopup.Visibility =
            ConnectionPopup.Visibility == Visibility.Visible
                ? Visibility.Collapsed
                : Visibility.Visible;
    }

    private void ParamsButton_Click(object sender, RoutedEventArgs e)
    {
        ShowFullScreen(ParamsView.Visibility != Visibility.Visible ? FullScreen.Params : FullScreen.None);
    }

    private void SetupButton_Click(object sender, RoutedEventArgs e)
    {
        ShowFullScreen(SetupPanel.Visibility != Visibility.Visible ? FullScreen.Setup : FullScreen.None);
    }

    private enum FullScreen { None, Params, Setup }

    /// <summary>
    /// Show one full-window setup view (or none). The map is a WebView2 (native
    /// window) that renders above WPF overlays, so we hide the whole main content
    /// instead of layering over it.
    /// </summary>
    private void ShowFullScreen(FullScreen which)
    {
        ParamsView.Visibility = which == FullScreen.Params ? Visibility.Visible : Visibility.Collapsed;
        SetupPanel.Visibility = which == FullScreen.Setup ? Visibility.Visible : Visibility.Collapsed;
        MainContentGrid.Visibility = which == FullScreen.None ? Visibility.Visible : Visibility.Collapsed;

        if (which != FullScreen.None)
            ConnectionPopup.Visibility = Visibility.Collapsed;
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized 
            ? WindowState.Normal 
            : WindowState.Maximized;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    protected override async void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // Clean shutdown
        await _viewModel.ShutdownAsync();
        base.OnClosing(e);
    }
    private void TopBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            MaximizeButton_Click(sender, e);
        }
        else
        {
            DragMove();
        }
    }
}

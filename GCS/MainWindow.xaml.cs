using GCS.Core.Mavlink;
using GCS.Core.Settings;
using GCS.ViewModels;
using System;
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

        RestoreWindowState();

        MouseLeftButtonDown += (s, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed)
                DragMove();
        };

        KeyDown += OnWindowKeyDown;
    }

    // ═══════════════════════════════════════════════════════════════
    // Keyboard shortcuts: Esc closes panels, Ctrl+P params, Ctrl+S setup
    // ═══════════════════════════════════════════════════════════════

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (ParamsView.Visibility == Visibility.Visible ||
                SetupPanel.Visibility == Visibility.Visible)
            {
                ShowFullScreen(FullScreen.None);
                e.Handled = true;
            }
            else if (ConnectionPopup.Visibility == Visibility.Visible)
            {
                ConnectionPopup.Visibility = Visibility.Collapsed;
                e.Handled = true;
            }
            return;
        }

        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (e.Key == Key.P)
            {
                ParamsButton_Click(this, new RoutedEventArgs());
                e.Handled = true;
            }
            else if (e.Key == Key.S)
            {
                SetupButton_Click(this, new RoutedEventArgs());
                e.Handled = true;
            }
            else if (e.Key == Key.W)
            {
                SwarmButton_Click(this, new RoutedEventArgs());
                e.Handled = true;
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // Window / layout state persistence
    // ═══════════════════════════════════════════════════════════════

    private void RestoreWindowState()
    {
        var s = SettingsStore.Current;

        if (s.WindowWidth >= 800 && s.WindowHeight >= 500)
        {
            Width = s.WindowWidth;
            Height = s.WindowHeight;
        }

        // Restore position only if it lands on a visible screen.
        if (!double.IsNaN(s.WindowLeft) && !double.IsNaN(s.WindowTop) &&
            s.WindowLeft >= SystemParameters.VirtualScreenLeft - 8 &&
            s.WindowTop >= SystemParameters.VirtualScreenTop - 8 &&
            s.WindowLeft < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 100 &&
            s.WindowTop < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 100)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = s.WindowLeft;
            Top = s.WindowTop;
        }

        if (s.WindowMaximized)
            WindowState = WindowState.Maximized;

        if (s.FlightPanelWidth >= 380 && s.FlightPanelWidth <= 900)
            FlightPanelColumn.Width = new GridLength(s.FlightPanelWidth);
    }

    private void SaveWindowState()
    {
        var s = SettingsStore.Current;
        bool maximized = WindowState == WindowState.Maximized;
        var bounds = maximized ? RestoreBounds : new Rect(Left, Top, Width, Height);

        s.WindowMaximized = maximized;
        s.WindowWidth = bounds.Width;
        s.WindowHeight = bounds.Height;
        s.WindowLeft = bounds.Left;
        s.WindowTop = bounds.Top;
        s.FlightPanelWidth = FlightPanelColumn.ActualWidth;
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

    /// <summary>
    /// Swarm mode normally engages on its own when a second vehicle is heard.
    /// This button is a manual override — useful to focus on one aircraft while
    /// several are connected, or to preview the swarm view before they arrive.
    /// </summary>
    private void SwarmButton_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.IsSwarmMode = !_viewModel.IsSwarmMode;

        // Leaving a full-window panel open would hide the mode change.
        if (ParamsView.Visibility == Visibility.Visible || SetupPanel.Visibility == Visibility.Visible)
            ShowFullScreen(FullScreen.None);
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

        // First open on a connection: pull the parameters automatically.
        if (which == FullScreen.Params)
            _viewModel.Parameters.AutoRefreshIfNeeded();
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
        // Persist layout, then clean shutdown (ShutdownAsync saves the settings file).
        SaveWindowState();
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

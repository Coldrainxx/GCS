using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace GCS.Views;

public partial class FlightPanelView : UserControl
{
    private GCS.ViewModels.MainViewModel? _mainVm;
    private object? _tabBeforeSwarm;

    public FlightPanelView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_mainVm != null) return;
        if (Window.GetWindow(this)?.DataContext is not GCS.ViewModels.MainViewModel vm) return;

        _mainVm = vm;
        vm.PropertyChanged += OnMainViewModelPropertyChanged;

        // SWARM is the first tab, and WPF selects index 0 even when that tab is
        // Collapsed — which would show the swarm view on startup in single-vehicle
        // mode. Pick the intended tab explicitly instead.
        if (vm.IsSwarmMode) SelectSwarmTab();
        else SelectDataTab();
    }

    private void SelectDataTab()
    {
        var tabs = FindTabControl();
        if (tabs != null && DataTab != null) tabs.SelectedItem = DataTab;
    }

    // Entering swarm mode jumps to the fleet view; leaving it returns to whatever
    // tab was open before, rather than stranding the user on a hidden tab.
    private void OnMainViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(GCS.ViewModels.MainViewModel.IsSwarmMode)) return;

        if (_mainVm?.IsSwarmMode == true)
            SelectSwarmTab();
        else
            RestorePreviousTab();
    }

    private void SelectSwarmTab()
    {
        var tabs = FindTabControl();
        if (tabs == null || SwarmTab == null) return;
        if (ReferenceEquals(tabs.SelectedItem, SwarmTab)) return;

        _tabBeforeSwarm = tabs.SelectedItem;
        tabs.SelectedItem = SwarmTab;
    }

    private void RestorePreviousTab()
    {
        var tabs = FindTabControl();
        if (tabs == null) return;

        // Only move if we're sitting on the tab that's about to disappear.
        if (!ReferenceEquals(tabs.SelectedItem, SwarmTab)) return;

        if (_tabBeforeSwarm is TabItem previous && previous.Visibility == Visibility.Visible)
            tabs.SelectedItem = previous;
        else
            SelectDataTab();
        _tabBeforeSwarm = null;
    }

    private TabControl? FindTabControl() => SwarmTab?.Parent as TabControl;

    // Universal scroll: whatever tab / inner control the cursor is over, walk up
    // to the first ScrollViewer that can move in the wheel direction and scroll it.
    // Runs on the tunneling PreviewMouseWheel so inner lists can't "eat" the wheel.
    private void Tabs_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled) return;

        var element = e.OriginalSource as DependencyObject;
        while (element != null)
        {
            if (element is ScrollViewer sv && sv.ScrollableHeight > 0)
            {
                bool canMove = (e.Delta > 0 && sv.VerticalOffset > 0) ||
                               (e.Delta < 0 && sv.VerticalOffset < sv.ScrollableHeight);
                if (canMove)
                {
                    double target = sv.VerticalOffset - e.Delta;
                    sv.ScrollToVerticalOffset(Math.Max(0, Math.Min(sv.ScrollableHeight, target)));
                    e.Handled = true;
                    return;
                }
            }
            element = VisualTreeHelper.GetParent(element);
        }
    }
}

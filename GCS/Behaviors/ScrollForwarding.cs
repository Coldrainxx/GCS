using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace GCS.Behaviors;

/// <summary>
/// Attached behavior that forwards the mouse wheel to the parent scroll viewer
/// when the control itself can't scroll further in that direction. Fixes the
/// common WPF problem where hovering an inner list "eats" the wheel and the
/// surrounding tab won't scroll.
/// </summary>
public static class ScrollForwarding
{
    public static readonly DependencyProperty EnabledProperty =
        DependencyProperty.RegisterAttached(
            "Enabled", typeof(bool), typeof(ScrollForwarding),
            new PropertyMetadata(false, OnEnabledChanged));

    public static void SetEnabled(DependencyObject o, bool value) => o.SetValue(EnabledProperty, value);
    public static bool GetEnabled(DependencyObject o) => (bool)o.GetValue(EnabledProperty);

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element) return;
        if ((bool)e.NewValue) element.PreviewMouseWheel += OnPreviewMouseWheel;
        else element.PreviewMouseWheel -= OnPreviewMouseWheel;
    }

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled) return;

        var inner = FindScrollViewer((DependencyObject)sender);
        bool atBoundary =
            inner == null ||
            (e.Delta > 0 && inner.VerticalOffset <= 0.0) ||
            (e.Delta < 0 && inner.VerticalOffset >= inner.ScrollableHeight);

        if (!atBoundary) return; // let the inner control keep scrolling

        e.Handled = true;
        var forwarded = new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
        {
            RoutedEvent = UIElement.MouseWheelEvent,
            Source = sender
        };
        (VisualTreeHelper.GetParent((DependencyObject)sender) as UIElement)?.RaiseEvent(forwarded);
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer sv) return sv;
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var found = FindScrollViewer(VisualTreeHelper.GetChild(root, i));
            if (found != null) return found;
        }
        return null;
    }
}

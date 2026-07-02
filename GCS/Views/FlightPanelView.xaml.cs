using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace GCS.Views;

public partial class FlightPanelView : UserControl
{
    public FlightPanelView()
    {
        InitializeComponent();
    }

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

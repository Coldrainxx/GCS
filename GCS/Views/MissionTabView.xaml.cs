using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using GCS.ViewModels;

namespace GCS.Views;

public partial class MissionTabView : UserControl
{
    private Point _dragStart;
    private MissionItemViewModel? _dragItem;

    public MissionTabView()
    {
        InitializeComponent();
    }

    private void WaypointList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(null);
        _dragItem = ItemFrom(e.OriginalSource as DependencyObject);
    }

    private void WaypointList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragItem == null) return;

        var diff = _dragStart - e.GetPosition(null);
        if (Math.Abs(diff.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(diff.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        try { DragDrop.DoDragDrop(WaypointList, _dragItem, DragDropEffects.Move); }
        finally { _dragItem = null; }
    }

    private void WaypointList_Drop(object sender, DragEventArgs e)
    {
        if (DataContext is not MissionViewModel vm) return;
        if (e.Data.GetData(typeof(MissionItemViewModel)) is not MissionItemViewModel dragged) return;

        var target = ItemFrom(e.OriginalSource as DependencyObject);
        int toIndex = target != null ? vm.Waypoints.IndexOf(target) : vm.Waypoints.Count - 1;
        vm.MoveWaypoint(dragged, toIndex);
    }

    private static MissionItemViewModel? ItemFrom(DependencyObject? source)
    {
        while (source != null && source is not ListBoxItem)
            source = VisualTreeHelper.GetParent(source);
        return (source as ListBoxItem)?.DataContext as MissionItemViewModel;
    }
}

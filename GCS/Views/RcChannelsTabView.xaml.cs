using System.Windows;
using System.Windows.Controls;
using System.Windows.Shapes;
using GCS.ViewModels;

namespace GCS.Views;

public partial class RcChannelsTabView : UserControl
{
    public RcChannelsTabView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is RcChannelsViewModel oldVm)
        {
            oldVm.PropertyChanged -= OnViewModelPropertyChanged;
        }

        if (e.NewValue is RcChannelsViewModel newVm)
        {
            newVm.PropertyChanged += OnViewModelPropertyChanged;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is not RcChannelsViewModel vm) return;

        // Update stick positions when stick values change
        if (e.PropertyName == nameof(RcChannelsViewModel.LeftStickX) ||
            e.PropertyName == nameof(RcChannelsViewModel.LeftStickY))
        {
            UpdateStickPosition(LeftStickDot, vm.LeftStickX, vm.LeftStickY);
        }
        else if (e.PropertyName == nameof(RcChannelsViewModel.RightStickX) ||
                 e.PropertyName == nameof(RcChannelsViewModel.RightStickY))
        {
            UpdateStickPosition(RightStickDot, vm.RightStickX, vm.RightStickY);
        }
    }

    private static void UpdateStickPosition(Ellipse? dot, double x, double y)
    {
        if (dot == null) return;

        // Canvas is 120x120, dot is 20x20
        // Center position = (50, 50), range = 40 pixels each direction
        const double center = 50;
        const double range = 40;

        double left = center + (x * range);
        double top = center + (y * range);

        Canvas.SetLeft(dot, left);
        Canvas.SetTop(dot, top);
    }
}
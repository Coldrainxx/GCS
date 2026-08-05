using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Navigation;
using System.Windows.Threading;
using GCS.ViewModels;

namespace GCS.Views;

/// <summary>
/// Floating flight-advisor widget.
///
/// The content lives in a <see cref="Popup"/> rather than inline because the map is
/// a WebView2 — a native child HWND — and WPF content cannot be composited over one.
/// A Popup has its own top-level window, so it renders above the map. The cost is
/// that it no longer moves with the parent automatically, hence the window tracking
/// below.
/// </summary>
public partial class AssistantPopupView : UserControl
{
    private AdvisorViewModel? _vm;
    private Window? _window;

    // Displacement the operator has dragged the panel by, relative to its default
    // bottom-right corner. Applied in the placement callback rather than stored as
    // an absolute position, so the panel keeps its corner behaviour when the window
    // is resized.
    private double _dragX;
    private double _dragY;

    private const double MinPanelWidth = 300;
    private const double MinPanelHeight = 260;

    // Cheap enough to run continuously; only ever reads the foreground window.
    private readonly DispatcherTimer _foregroundTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(400)
    };

    public AssistantPopupView()
    {
        InitializeComponent();

        HostPopup.CustomPopupPlacementCallback = PlaceAboveAnchor;

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        DataContextChanged += OnDataContextChanged;
    }

    // ── Placement ───────────────────────────────────────────────────

    /// <summary>
    /// Anchor the popup's bottom-right corner to the anchor point, so the panel
    /// grows up and to the left out of the corner regardless of its size, then
    /// apply whatever the operator has dragged.
    /// </summary>
    private CustomPopupPlacement[] PlaceAboveAnchor(
        Size popupSize, Size targetSize, Point offset) =>
        new[]
        {
            new CustomPopupPlacement(
                new Point(-popupSize.Width + _dragX, -popupSize.Height + _dragY),
                PopupPrimaryAxis.None)
        };

    // ── Drag and resize ─────────────────────────────────────────────

    private void DragThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        _dragX += e.HorizontalChange;
        _dragY += e.VerticalChange;
        Reposition();
    }

    private void ResizeGrip_DragDelta(object sender, DragDeltaEventArgs e)
    {
        // The panel is pinned bottom-right, so dragging the top-left grip left or
        // up must grow it — hence the subtraction.
        double width = ChatPanel.ActualWidth - e.HorizontalChange;
        double height = ChatPanel.ActualHeight - e.VerticalChange;

        ChatPanel.Width = Math.Max(MinPanelWidth, Math.Min(width, MaxPanelWidth()));
        ChatPanel.Height = Math.Max(MinPanelHeight, Math.Min(height, MaxPanelHeight()));

        Reposition();
    }

    private double MaxPanelWidth() => Math.Max(MinPanelWidth, (_window?.ActualWidth ?? 1200) - 60);

    private double MaxPanelHeight() => Math.Max(MinPanelHeight, (_window?.ActualHeight ?? 800) - 60);

    /// <summary>
    /// Pull the panel back inside the window. Without this a drag could park it
    /// off-screen, where there is no way to get it back.
    /// </summary>
    private void ClampToWindow()
    {
        if (_window == null || !HostPopup.IsOpen) return;
        if (ChatPanel.Visibility != Visibility.Visible) return;
        if (!Anchor.IsVisible || !_window.IsVisible) return;

        double panelWidth = ChatPanel.ActualWidth > 0 ? ChatPanel.ActualWidth : ChatPanel.Width;
        double panelHeight = ChatPanel.ActualHeight > 0 ? ChatPanel.ActualHeight : ChatPanel.Height;
        if (double.IsNaN(panelWidth) || double.IsNaN(panelHeight)) return;

        Point anchorPoint;
        try
        {
            anchorPoint = Anchor.TransformToAncestor(_window).Transform(new Point(0, 0));
        }
        catch (InvalidOperationException)
        {
            return;   // not in the same visual tree yet
        }

        double left = anchorPoint.X - panelWidth + _dragX;
        double top = anchorPoint.Y - panelHeight + _dragY;

        double clampedLeft = Math.Clamp(left, 0, Math.Max(0, _window.ActualWidth - panelWidth));
        double clampedTop = Math.Clamp(top, 0, Math.Max(0, _window.ActualHeight - panelHeight));

        _dragX += clampedLeft - left;
        _dragY += clampedTop - top;
    }

    /// <summary>
    /// Force the popup to recompute its position. Nudging an offset is the
    /// supported way to do this — there is no public Reposition on Popup.
    /// </summary>
    private void Reposition()
    {
        if (!HostPopup.IsOpen) return;

        ClampToWindow();

        double offset = HostPopup.HorizontalOffset;
        HostPopup.HorizontalOffset = offset + 1;
        HostPopup.HorizontalOffset = offset;
    }

    // ── Window tracking ─────────────────────────────────────────────

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_window != null) return;

        _window = Window.GetWindow(this);
        if (_window == null) return;

        _window.LocationChanged += OnWindowMoved;
        _window.SizeChanged += OnWindowResized;
        _window.StateChanged += OnWindowStateChanged;
        _window.Activated += OnWindowActivated;
        _window.Deactivated += OnWindowDeactivated;

        _foregroundTimer.Tick += OnForegroundTick;
        _foregroundTimer.Start();

        UpdateVisibility();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        DetachViewModel();

        _foregroundTimer.Stop();
        _foregroundTimer.Tick -= OnForegroundTick;

        if (_window != null)
        {
            _window.LocationChanged -= OnWindowMoved;
            _window.SizeChanged -= OnWindowResized;
            _window.StateChanged -= OnWindowStateChanged;
            _window.Activated -= OnWindowActivated;
            _window.Deactivated -= OnWindowDeactivated;
            _window = null;
        }

        HostPopup.IsOpen = false;
    }

    private void OnWindowMoved(object? sender, EventArgs e) => Reposition();

    private void OnWindowResized(object sender, SizeChangedEventArgs e) => Reposition();

    private void OnWindowStateChanged(object? sender, EventArgs e) => UpdateVisibility();

    private void OnWindowActivated(object? sender, EventArgs e)
    {
        UpdateVisibility();

        // Coming back to the app with the chat open should leave the caret where the
        // operator left it, not in the main window behind the panel.
        if (_vm?.IsChatOpen == true) RestoreQuestionFocus();
    }

    /// <summary>
    /// Keep the caret in the box when focus is being taken by another window rather
    /// than by something the operator clicked.
    ///
    /// The map is a WebView2 — a child HWND of the main window — and while a swarm is
    /// connected it is scripted ten times a second. That activity pulls activation
    /// back to the main window, and because this panel is its own top-level window it
    /// silently lost the keyboard, so typing stopped working.
    ///
    /// A deliberate click on another control reports a real NewFocus and is honoured;
    /// only focus vanishing to nothing is refused.
    /// </summary>
    private void QuestionBox_PreviewLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_vm?.IsChatOpen == true && e.NewFocus == null)
            e.Handled = true;
    }

    private void RestoreQuestionFocus() =>
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (_vm?.IsChatOpen != true) return;
            if (QuestionBox.IsKeyboardFocusWithin) return;
            QuestionBox.Focus();
            QuestionBox.CaretIndex = QuestionBox.Text.Length;
        }));

    /// <summary>
    /// A popup is a top-level window, so it would otherwise hover over whatever app
    /// the user switches to.
    ///
    /// The check has to be "is our process still in front?", asked of the OS. WPF's
    /// own focus properties are no help: keyboard focus stays inside the popup even
    /// after Windows has moved to another application, so guarding on
    /// IsKeyboardFocusWithin left it floating over everything. Conversely the popup
    /// is its own HWND, so clicking into it genuinely deactivates the owner window —
    /// hiding unconditionally would make the chat unusable.
    /// </summary>
    private void OnWindowDeactivated(object? sender, EventArgs e)
    {
        // Queued: at the moment Deactivated fires the new foreground window is not
        // necessarily set yet.
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (IsOwnProcessInForeground()) return;
            HostPopup.IsOpen = false;
        }));
    }

    private static bool IsOwnProcessInForeground()
    {
        IntPtr foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero) return false;

        _ = GetWindowThreadProcessId(foreground, out uint pid);
        return pid == (uint)Environment.ProcessId;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    private void UpdateVisibility()
    {
        bool shouldShow =
            _window is { IsVisible: true, WindowState: not WindowState.Minimized } &&
            IsVisible &&
            IsOwnProcessInForeground();

        if (HostPopup.IsOpen != shouldShow) HostPopup.IsOpen = shouldShow;
        if (shouldShow) Reposition();
    }

    /// <summary>
    /// Backstop for the window events. Once the popup's own HWND has taken the
    /// foreground, the owner window is already deactivated — so switching to a third
    /// application raises no further Deactivated event and the popup would be left
    /// hovering over it. Polling the foreground window catches every transition
    /// regardless of which HWND held it.
    /// </summary>
    private void OnForegroundTick(object? sender, EventArgs e) => UpdateVisibility();

    // ── ViewModel ───────────────────────────────────────────────────

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        DetachViewModel();

        if (DataContext is not AdvisorViewModel vm) return;

        _vm = vm;
        _vm.PropertyChanged += OnViewModelPropertyChanged;
        _vm.Conversation.CollectionChanged += OnConversationChanged;

        // Seed the masked field with whatever was loaded from appsettings.json.
        _syncingKey = true;
        KeyPasswordBox.Password = _vm.ApiKey ?? "";
        _syncingKey = false;
    }

    private void DetachViewModel()
    {
        if (_vm == null) return;
        _vm.PropertyChanged -= OnViewModelPropertyChanged;
        _vm.Conversation.CollectionChanged -= OnConversationChanged;
        _vm = null;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(AdvisorViewModel.IsChatOpen)) return;

        // Closing returns the widget to its home corner. The launcher shares the
        // popup with the panel, so without this it would be left stranded wherever
        // the panel was last dragged — and the small circle is much harder to find
        // than the panel was. Reopening then grows out of the corner again, so the
        // panel always appears where its launcher was.
        if (_vm?.IsChatOpen == false)
        {
            _dragX = 0;
            _dragY = 0;
        }

        // The popup changes size between launcher and panel, so it has to be
        // re-anchored or the panel would hang off the corner.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            Reposition();

            if (_vm?.IsChatOpen != true) return;
            QuestionBox.Focus();
            QuestionBox.CaretIndex = QuestionBox.Text.Length;
        }));
    }

    // ── Settings ────────────────────────────────────────────────────

    // PasswordBox.Password is deliberately not a DependencyProperty, so it cannot
    // be bound; these keep it in sync with the ViewModel by hand.
    private bool _syncingKey;

    private void KeyPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_syncingKey || _vm == null) return;

        _syncingKey = true;
        _vm.ApiKey = KeyPasswordBox.Password;
        _syncingKey = false;
    }

    private void RevealKey_Changed(object sender, RoutedEventArgs e)
    {
        bool reveal = RevealKeyCheck.IsChecked == true;

        _syncingKey = true;
        if (reveal)
        {
            // The TextBox is bound, so push the typed password into the ViewModel
            // before swapping, or the visible field would start empty.
            if (_vm != null) _vm.ApiKey = KeyPasswordBox.Password;
        }
        else if (_vm != null)
        {
            KeyPasswordBox.Password = _vm.ApiKey ?? "";
        }
        _syncingKey = false;

        KeyTextBox.Visibility = reveal ? Visibility.Visible : Visibility.Collapsed;
        KeyPasswordBox.Visibility = reveal ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Open the provider's key page in the default browser.</summary>
    private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Assistant] Could not open {e.Uri}: {ex.Message}");
        }

        e.Handled = true;
    }

    private void OnConversationChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset) return;

        // Queued: the new item has not been laid out when this fires.
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            TranscriptScroll.ScrollToEnd();
            Reposition();
        }));
    }
}

using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace GCS.Views;

public partial class ArtificialHorizon : UserControl
{
    // =====================================================
    // Dependency properties (with change callbacks)
    // =====================================================

    public static readonly DependencyProperty RollProperty =
        DependencyProperty.Register(
            nameof(Roll),
            typeof(double),
            typeof(ArtificialHorizon),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty PitchProperty =
        DependencyProperty.Register(
            nameof(Pitch),
            typeof(double),
            typeof(ArtificialHorizon),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Roll
    {
        get => (double)GetValue(RollProperty);
        set => SetValue(RollProperty, value);
    }

    public double Pitch
    {
        get => (double)GetValue(PitchProperty);
        set => SetValue(PitchProperty, value);
    }

    // =====================================================
    // Cached resources
    // =====================================================

    private static readonly Brush SkyBrush;
    private static readonly Brush GroundBrush;
    private static readonly Pen MajorPen;
    private static readonly Pen MinorPen;
    private static readonly Pen WhitePen;

    static ArtificialHorizon()
    {
        SkyBrush = new SolidColorBrush(Color.FromRgb(40, 90, 140));
        GroundBrush = new SolidColorBrush(Color.FromRgb(120, 70, 30));
        MajorPen = new Pen(Brushes.White, 2);
        MinorPen = new Pen(Brushes.White, 1);
        WhitePen = new Pen(Brushes.White, 2);

        SkyBrush.Freeze();
        GroundBrush.Freeze();
        MajorPen.Freeze();
        MinorPen.Freeze();
        WhitePen.Freeze();
    }

    // =====================================================
    // Rendering state
    // =====================================================

    private DrawingGroup? _staticLayer;
    private bool _staticDirty = true;

    private EllipseGeometry? _clip;
    private readonly StreamGeometry _rollPointerGeom = new();

    // =====================================================
    // ctor / lifecycle
    // =====================================================

    public ArtificialHorizon()
    {
        InitializeComponent();

        BuildRollPointerGeometry();
        _rollPointerGeom.Freeze();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        _staticDirty = true;
        _clip = null;
    }

    // =====================================================
    // Main render (triggered by AffectsRender on DP change)
    // =====================================================

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        double w = ActualWidth;
        double h = ActualHeight;
        if (w <= 0 || h <= 0)
            return;

        double cx = w / 2.0;
        double cy = h / 2.0;
        double radius = Math.Min(w, h) / 2.0;

        if (_staticDirty || _staticLayer == null)
            BuildStaticLayer(radius);

        if (_clip == null)
        {
            _clip = new EllipseGeometry(new Point(cx, cy), radius, radius);
            _clip.Freeze();
        }

        // ===== Horizon + Pitch ladder (CLIPPED) =====
        dc.PushClip(_clip);
        DrawDynamicHorizon(dc, cx, cy, radius, Pitch, Roll);
        DrawDynamicPitchLadder(dc, cx, cy, radius, Pitch);
        dc.Pop();

        // ===== Static overlay =====
        dc.PushTransform(new TranslateTransform(cx, cy));
        dc.DrawDrawing(_staticLayer);
        dc.Pop();

        // ===== Roll pointer =====
        DrawRollPointer(dc, cx, cy, radius, Roll);
    }

    // =====================================================
    // Static layer
    // =====================================================

    private void BuildStaticLayer(double radius)
    {
        _staticLayer = new DrawingGroup();

        using (var dc = _staticLayer.Open())
        {
            DrawFixedSymbolStatic(dc, radius);
            DrawRollScaleStatic(dc, radius);
        }

        _staticLayer.Freeze();
        _staticDirty = false;
    }

    // =====================================================
    // Dynamic horizon (pitch + roll)
    // =====================================================

    private void DrawDynamicHorizon(
        DrawingContext dc,
        double cx,
        double cy,
        double radius,
        double pitch,
        double roll)
    {
        double pitchPx = Math.Clamp(pitch, -90, 90) * (radius / 30.0);

        dc.PushTransform(new TranslateTransform(cx, cy));
        dc.PushTransform(new RotateTransform(-roll));
        dc.PushTransform(new TranslateTransform(0, pitchPx));

        dc.DrawRectangle(
            SkyBrush,
            null,
            new Rect(-radius * 2, -radius * 2, radius * 4, radius * 2));

        dc.DrawRectangle(
            GroundBrush,
            null,
            new Rect(-radius * 2, 0, radius * 4, radius * 2));

        dc.DrawLine(
            WhitePen,
            new Point(-radius * 2, 0),
            new Point(radius * 2, 0));

        dc.Pop();
        dc.Pop();
        dc.Pop();
    }

    // =====================================================
    // Dynamic pitch ladder
    // =====================================================

    private void DrawDynamicPitchLadder(
        DrawingContext dc,
        double cx,
        double cy,
        double radius,
        double pitch)
    {
        double scale = radius / 30.0;
        double pitchPx = Math.Clamp(pitch, -90, 90) * scale;

        dc.PushTransform(new TranslateTransform(cx, cy));
        dc.PushTransform(new TranslateTransform(0, pitchPx));

        Typeface typeface = new Typeface("Segoe UI");
        double fontSize = radius * 0.08;

        for (int p = -90; p <= 90; p += 5)
        {
            double y = -p * scale;

            if (Math.Abs(y) > radius)
                continue;

            bool major = p % 10 == 0;
            double len = major ? radius * 0.5 : radius * 0.3;

            dc.DrawLine(
                major ? MajorPen : MinorPen,
                new Point(-len, y),
                new Point(len, y));

            if (major && p != 0)
            {
                var ft = new FormattedText(
                    Math.Abs(p).ToString(),
                    CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,
                    typeface,
                    fontSize,
                    Brushes.White,
                    1.0);

                dc.DrawText(ft, new Point(len + 6, y - ft.Height / 2));
                dc.DrawText(ft, new Point(-len - ft.Width - 6, y - ft.Height / 2));
            }
        }

        dc.Pop();
        dc.Pop();
    }

    // =====================================================
    // Fixed aircraft symbol
    // =====================================================

    private void DrawFixedSymbolStatic(DrawingContext dc, double radius)
    {
        dc.DrawLine(
            WhitePen,
            new Point(-radius * 0.4, 0),
            new Point(-radius * 0.1, 0));

        dc.DrawLine(
            WhitePen,
            new Point(radius * 0.1, 0),
            new Point(radius * 0.4, 0));

        dc.DrawEllipse(
            Brushes.White,
            null,
            new Point(0, 0),
            3, 3);
    }

    // =====================================================
    // Static roll scale
    // =====================================================

    private void DrawRollScaleStatic(DrawingContext dc, double radius)
    {
        double arcRadius = radius * 0.85;

        for (int angle = -60; angle <= 60; angle += 10)
        {
            double a = angle * Math.PI / 180.0;
            double sin = Math.Sin(a);
            double cos = Math.Cos(a);

            dc.DrawLine(
                WhitePen,
                new Point((arcRadius - 10) * sin, -(arcRadius - 10) * cos),
                new Point(arcRadius * sin, -arcRadius * cos));
        }
    }

    // =====================================================
    // Roll pointer
    // =====================================================

    private void BuildRollPointerGeometry()
    {
        using var ctx = _rollPointerGeom.Open();
        ctx.BeginFigure(new Point(0, 0), true, true);
        ctx.LineTo(new Point(-6, -10), true, false);
        ctx.LineTo(new Point(6, -10), true, false);
    }

    private void DrawRollPointer(
        DrawingContext dc,
        double cx,
        double cy,
        double radius,
        double roll)
    {
        double arcRadius = radius * 0.85;
        double rollRad = roll * Math.PI / 180.0;

        double x = cx + arcRadius * Math.Sin(rollRad);
        double y = cy - arcRadius * Math.Cos(rollRad);

        dc.PushTransform(new TranslateTransform(x, y));
        dc.DrawGeometry(Brushes.White, null, _rollPointerGeom);
        dc.Pop();
    }
}
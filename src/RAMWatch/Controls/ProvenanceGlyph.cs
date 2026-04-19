using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using RAMWatch.Services;

namespace RAMWatch.Controls;

/// <summary>
/// 10×10 dot next to each sensor label. Three independent visual channels
/// encode three independent facts about the value the user is staring at:
///
/// • Shape — Provenance. Circle = direct observation, Diamond = derived
///   from other sensors, Square = unknown / unlabelled.
/// • Fill saturation — Freshness. Bright = the value moved within the last
///   few seconds, medium = it moved within the last minute, dim = it hasn't
///   moved in a while (or never has).
/// • Border ring — Status. Green/amber/red when the sensor has a threshold
///   configured and the value falls in the corresponding band; absent when
///   no threshold is defined. Most sensors have no threshold and draw
///   without a ring.
///
/// Shape and saturation are auto-computed from the sensor registry and the
/// <see cref="ProvenanceObserver"/>; status is a one-way binding from the
/// view model. Drawn with <see cref="OnRender"/> rather than via XAML
/// templates — no font dependency, pixel-exact placement, and frozen
/// geometries/brushes keep the render path allocation-free.
/// </summary>
public sealed class ProvenanceGlyph : FrameworkElement
{
    // ── Brushes ─────────────────────────────────────────────────────────
    // Frozen SolidColorBrush per (Provenance × Freshness). Saturation drop
    // is achieved by precomputing the dimmer colours, not by stacking
    // opacity layers at render time — the latter would allocate per paint.
    // Measured/Reported stay in the green family so the dot never fights
    // the amber/red border semantics; Static drops to grey. Palette mirrors
    // Tailwind green-400 / green-700 / green-900 and gray-400 / 600 / 700.

    private static readonly SolidColorBrush MeasuredLive = Freeze(0x4A, 0xDE, 0x80);
    private static readonly SolidColorBrush MeasuredWarm = Freeze(0x2E, 0x8E, 0x56);
    private static readonly SolidColorBrush MeasuredCold = Freeze(0x1B, 0x4D, 0x30);

    private static readonly SolidColorBrush ReportedLive = Freeze(0x4A, 0x7A, 0x5A);
    private static readonly SolidColorBrush ReportedWarm = Freeze(0x35, 0x5A, 0x42);
    private static readonly SolidColorBrush ReportedCold = Freeze(0x23, 0x3D, 0x2D);

    private static readonly SolidColorBrush StaticLive = Freeze(0x9C, 0xA3, 0xAF);
    private static readonly SolidColorBrush StaticWarm = Freeze(0x6B, 0x72, 0x80);
    private static readonly SolidColorBrush StaticCold = Freeze(0x4B, 0x50, 0x5B);

    // Border pens — drawn as a 1px ring around the filled shape. The None
    // case never draws a ring so sensors without thresholds look identical
    // to how they did before status bindings were introduced.
    private static readonly Pen PassPen = FreezePen(0x4A, 0xDE, 0x80); // green
    private static readonly Pen WarnPen = FreezePen(0xFB, 0xBF, 0x24); // amber
    private static readonly Pen CritPen = FreezePen(0xEF, 0x44, 0x44); // red

    private static readonly Geometry DiamondGeometry = BuildDiamond();

    private static SolidColorBrush Freeze(byte r, byte g, byte b)
    {
        var br = new SolidColorBrush(Color.FromRgb(r, g, b));
        br.Freeze();
        return br;
    }

    private static Pen FreezePen(byte r, byte g, byte b)
    {
        var pen = new Pen(Freeze(r, g, b), 1.0);
        pen.Freeze();
        return pen;
    }

    private static Geometry BuildDiamond()
    {
        var figure = new PathFigure { StartPoint = new Point(5, 1), IsClosed = true, IsFilled = true };
        figure.Segments.Add(new LineSegment(new Point(9, 5), isStroked: false));
        figure.Segments.Add(new LineSegment(new Point(5, 9), isStroked: false));
        figure.Segments.Add(new LineSegment(new Point(1, 5), isStroked: false));
        var geo = new PathGeometry();
        geo.Figures.Add(figure);
        geo.Freeze();
        return geo;
    }

    // ── Dependency properties ───────────────────────────────────────────

    public static readonly DependencyProperty SensorKeyProperty = DependencyProperty.Register(
        nameof(SensorKey), typeof(string), typeof(ProvenanceGlyph),
        new FrameworkPropertyMetadata(
            defaultValue: "",
            flags: FrameworkPropertyMetadataOptions.AffectsRender,
            propertyChangedCallback: OnVisualInputChanged));

    public static readonly DependencyProperty SensorValueProperty = DependencyProperty.Register(
        nameof(SensorValue), typeof(double), typeof(ProvenanceGlyph),
        new FrameworkPropertyMetadata(
            defaultValue: double.NaN,
            flags: FrameworkPropertyMetadataOptions.AffectsRender,
            propertyChangedCallback: OnVisualInputChanged));

    public static readonly DependencyProperty DisplayNameProperty = DependencyProperty.Register(
        nameof(DisplayName), typeof(string), typeof(ProvenanceGlyph),
        new PropertyMetadata("", OnVisualInputChanged));

    public static readonly DependencyProperty StatusProperty = DependencyProperty.Register(
        nameof(Status), typeof(StatusLevel), typeof(ProvenanceGlyph),
        new FrameworkPropertyMetadata(
            defaultValue: StatusLevel.None,
            flags: FrameworkPropertyMetadataOptions.AffectsRender));

    public string SensorKey
    {
        get => (string)GetValue(SensorKeyProperty);
        set => SetValue(SensorKeyProperty, value);
    }

    public double SensorValue
    {
        get => (double)GetValue(SensorValueProperty);
        set => SetValue(SensorValueProperty, value);
    }

    public string DisplayName
    {
        get => (string)GetValue(DisplayNameProperty);
        set => SetValue(DisplayNameProperty, value);
    }

    public StatusLevel Status
    {
        get => (StatusLevel)GetValue(StatusProperty);
        set => SetValue(StatusProperty, value);
    }

    // ── Lifetime ────────────────────────────────────────────────────────

    public ProvenanceGlyph()
    {
        Loaded   += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        SensorProvenanceResolver.Observer.SensorUpdated += OnObserverUpdate;
        RefreshTooltip();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        SensorProvenanceResolver.Observer.SensorUpdated -= OnObserverUpdate;
    }

    private void OnObserverUpdate(string key)
    {
        if (!string.Equals(key, SensorKey, StringComparison.Ordinal)) return;

        if (Dispatcher.CheckAccess())
        {
            InvalidateVisual();
            RefreshTooltip();
        }
        else
        {
            Dispatcher.BeginInvoke(() =>
            {
                InvalidateVisual();
                RefreshTooltip();
            });
        }
    }

    // ── Layout / render ─────────────────────────────────────────────────

    protected override Size MeasureOverride(Size availableSize) => new(10, 10);

    protected override void OnRender(DrawingContext dc)
    {
        var info = ResolveInfo();
        FreshnessBucket freshness = FreshnessComputer.Compute(
            SensorProvenanceResolver.Observer.GetLastChangeUtc(SensorKey),
            DateTime.UtcNow);

        Brush fill = PickFill(info.Provenance, freshness);
        Pen? borderPen = Status switch
        {
            StatusLevel.Pass => PassPen,
            StatusLevel.Warn => WarnPen,
            StatusLevel.Crit => CritPen,
            _                => null,
        };

        switch (info.Shape)
        {
            case ProvenanceShape.Circle:
                // Border rendered at the same radius as the fill so the 1px
                // pen draws half inside / half outside the edge, which gives
                // the ring a consistent 1px visual thickness.
                dc.DrawEllipse(fill, borderPen, new Point(5, 5), radiusX: 4, radiusY: 4);
                break;
            case ProvenanceShape.Diamond:
                dc.DrawGeometry(fill, borderPen, DiamondGeometry);
                break;
            case ProvenanceShape.Square:
                dc.DrawRectangle(fill, borderPen, new Rect(1, 1, 8, 8));
                break;
        }
    }

    private static Brush PickFill(Provenance p, FreshnessBucket f)
    {
        // Derived folds onto Reported colours — derived-from-reported is the
        // only tier combo we actually use today (drift, config change).
        // Unknown folds onto Static (grey family) so unlabelled sensors read
        // as inert rather than claiming a freshness they can't back up.
        return (p, f) switch
        {
            (Provenance.Measured, FreshnessBucket.Live) => MeasuredLive,
            (Provenance.Measured, FreshnessBucket.Warm) => MeasuredWarm,
            (Provenance.Measured, FreshnessBucket.Cold) => MeasuredCold,

            (Provenance.Reported, FreshnessBucket.Live) => ReportedLive,
            (Provenance.Reported, FreshnessBucket.Warm) => ReportedWarm,
            (Provenance.Reported, FreshnessBucket.Cold) => ReportedCold,

            (Provenance.Derived,  FreshnessBucket.Live) => ReportedLive,
            (Provenance.Derived,  FreshnessBucket.Warm) => ReportedWarm,
            (Provenance.Derived,  FreshnessBucket.Cold) => ReportedCold,

            (Provenance.Static,   FreshnessBucket.Live) => StaticLive,
            (Provenance.Static,   FreshnessBucket.Warm) => StaticWarm,
            (Provenance.Static,   FreshnessBucket.Cold) => StaticCold,

            _ /* Unknown */                             => StaticCold,
        };
    }

    // ── Tooltip ─────────────────────────────────────────────────────────

    private static void OnVisualInputChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ProvenanceGlyph g) g.RefreshTooltip();
    }

    private void RefreshTooltip()
    {
        var info = ResolveInfo();
        string label = string.IsNullOrEmpty(DisplayName) ? SensorKey : DisplayName;
        ToolTip = new ToolTip
        {
            Content = $"{label} — {info.Provenance} ({info.Source})\n{info.Detail}",
        };
    }

    private SensorProvenanceInfo ResolveInfo()
        => double.IsNaN(SensorValue)
            ? SensorProvenanceResolver.Resolve(SensorKey)
            : SensorProvenanceResolver.Resolve(SensorKey, SensorValue);
}

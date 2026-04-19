using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using RAMWatch.Services;

namespace RAMWatch.Controls;

/// <summary>
/// 10×10 dot next to each sensor label. Circle / diamond / square with
/// green / amber / grey tells the user in one glance whether the number
/// they're staring at is a live measurement, a reported setpoint, static
/// config, or something the observer no longer trusts. Hovering reveals
/// the exact source pipe and a one-paragraph detail.
///
/// Drawn with <see cref="OnRender"/> rather than via XAML templates: no
/// font dependency, pixel-exact placement, and frozen geometries so the
/// render path is allocation-free after first construction.
/// </summary>
public sealed class ProvenanceGlyph : FrameworkElement
{
    // ── Brushes (frozen; hardcoded semantic colours, not theme tokens) ──
    // Green "measured" is a contract: a theme must never be able to turn
    // it red. Palette mirrors Tailwind green-400 / amber-400 / gray-400.

    // All three "data-tier" brushes stay in the green family so dot colour
    // no longer collides with the status-border semantics (amber = warn,
    // red = crit). Saturation drops as the data tier cools: Measured is a
    // bright live green, Reported a muted polled green, Static a grey-green
    // on the edge of disengaged. Shape still carries the provenance
    // distinction; colour now carries "is everything OK" at a glance.
    // Full design: docs/STATUS-DOT-LEGEND-DESIGN.md (freshness via fill
    // saturation, status via border ring) — the one-line fix here is a
    // stop-gap until the Freshness/Status DPs land.
    private static readonly SolidColorBrush MeasuredBrush = Freeze(Color.FromRgb(0x4A, 0xDE, 0x80));
    private static readonly SolidColorBrush ReportedBrush = Freeze(Color.FromRgb(0x4A, 0x7A, 0x5A));
    private static readonly SolidColorBrush StaticBrush   = Freeze(Color.FromRgb(0x9C, 0xA3, 0xAF));
    private static readonly SolidColorBrush UnknownBrush  = StaticBrush;

    private static readonly Geometry DiamondGeometry = BuildDiamond();

    private static SolidColorBrush Freeze(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
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

    // ── Lifetime ────────────────────────────────────────────────────────

    public ProvenanceGlyph()
    {
        // Subscribe on Loaded / unsubscribe on Unloaded — idiomatic WPF
        // lifetime for visual-tree controls. Holds no reference across
        // DataContext swaps or template re-inflation.
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
        var brush = info.Provenance switch
        {
            Provenance.Measured => MeasuredBrush,
            Provenance.Reported => ReportedBrush,
            Provenance.Static   => StaticBrush,
            Provenance.Derived  => info.Provenance switch
            {
                Provenance.Measured => MeasuredBrush,
                Provenance.Reported => ReportedBrush,
                _                   => StaticBrush,
            },
            _                   => UnknownBrush,
        };

        switch (info.Shape)
        {
            case ProvenanceShape.Circle:
                dc.DrawEllipse(brush, pen: null, new Point(5, 5), radiusX: 4, radiusY: 4);
                break;
            case ProvenanceShape.Diamond:
                dc.DrawGeometry(brush, pen: null, DiamondGeometry);
                break;
            case ProvenanceShape.Square:
                dc.DrawRectangle(brush, pen: null, new Rect(1, 1, 8, 8));
                break;
        }
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

using System.Globalization;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Documents;
using System.Windows.Media;
using NetStrip.Core;
using NetStrip.Core.Formatting;
using NetStrip.Core.Presentation;

namespace NetStrip.Flyout;

/// <summary>
/// Last-60-seconds traffic graph, custom drawn (no chart library): download as a 30 % filled area,
/// upload as a 1.5 px line, one max label top-left. Plots <see cref="View"/>'s rates from the shared buffer, scaled to
/// that view's own peak. Redraw by calling <see cref="Refresh"/>.
/// </summary>
public sealed class GraphControl : FrameworkElement
{
    public static readonly DependencyProperty ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner(typeof(GraphControl),
            new FrameworkPropertyMetadata(SystemColors.ControlTextBrush, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty DownloadBrushProperty = DependencyProperty.Register(
        nameof(DownloadBrush), typeof(Brush), typeof(GraphControl),
        new FrameworkPropertyMetadata(SystemColors.HighlightBrush, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty UploadBrushProperty = DependencyProperty.Register(
        nameof(UploadBrush), typeof(Brush), typeof(GraphControl),
        new FrameworkPropertyMetadata(Brushes.Teal, FrameworkPropertyMetadataOptions.AffectsRender));

    private const double LabelFontSize = 11;
    private const double LinePadding = 4;

    private GraphBuffer? _buffer;
    private SpeedUnits _units;
    private AdapterView _view = AdapterView.Configured;

    public Brush Foreground
    {
        get => (Brush)GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public Brush DownloadBrush
    {
        get => (Brush)GetValue(DownloadBrushProperty);
        set => SetValue(DownloadBrushProperty, value);
    }

    public Brush UploadBrush
    {
        get => (Brush)GetValue(UploadBrushProperty);
        set => SetValue(UploadBrushProperty, value);
    }

    public void Bind(GraphBuffer buffer, SpeedUnits units)
    {
        _buffer = buffer;
        _units = units;
        Refresh();
    }

    public SpeedUnits Units
    {
        get => _units;
        set { _units = value; Refresh(); }
    }

    /// <summary>Which rates of each sample to plot (the flyout's adapter switcher); the configured view by default.</summary>
    public AdapterView View
    {
        get => _view;
        set { _view = value; Refresh(); }
    }

    /// <summary>Redraws; call on each sample while the flyout is visible.</summary>
    public void Refresh()
    {
        InvalidateVisual();
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new GraphPeer(this);

    private string HelpText => _buffer is null ? "" : $"Peak {SpeedFormatter.AccessibleSpeed(_view.PeakRate(_buffer.Items), _units)}";

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        // Plot surface: invisible hit area so the control has a defined extent.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        if (_buffer is null) return;

        var items = _buffer.Items;
        var view = _view;
        var peak = view.PeakRate(items);
        var max = (double)peak;
        const int capacity = 60;
        var step = w / (capacity - 1);
        var top = LinePadding;
        var plotHeight = Math.Max(1, h - top - LinePadding);
        var n = items.Count;

        if (n >= 2)
        {
            // Newest sample sits at the right edge.
            double X(int i) => w - (n - 1 - i) * step;
            double Y(long v) => top + plotHeight - Math.Min(v / max, 1.0) * plotHeight;

            var area = new StreamGeometry();
            using (var ctx = area.Open())
            {
                ctx.BeginFigure(new Point(X(0), top + plotHeight), isFilled: true, isClosed: true);
                for (var i = 0; i < n; i++) ctx.LineTo(new Point(X(i), Y(view.RatesFor(items[i]).Down)), true, false);
                ctx.LineTo(new Point(X(n - 1), top + plotHeight), true, false);
            }
            area.Freeze();

            var line = new StreamGeometry();
            using (var ctx = line.Open())
            {
                ctx.BeginFigure(new Point(X(0), Y(view.RatesFor(items[0]).Up)), isFilled: false, isClosed: false);
                for (var i = 1; i < n; i++) ctx.LineTo(new Point(X(i), Y(view.RatesFor(items[i]).Up)), true, true);
            }
            line.Freeze();

            var fill = DownloadBrush.CloneCurrentValue();
            fill.Opacity = 0.30;
            dc.DrawGeometry(fill, null, area);
            var pen = new Pen(UploadBrush, 1.5) { LineJoin = PenLineJoin.Round };
            pen.Freeze();
            dc.DrawGeometry(null, pen, line);
        }

        var text = new FormattedText(
            SpeedFormatter.FormatSpeed(peak, _units),
            CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI Variable Small, Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
            LabelFontSize, Foreground, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(text, new Point(2, 0));
    }

    private sealed class GraphPeer(GraphControl owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Image;
        protected override string GetClassNameCore() => nameof(GraphControl);
        protected override string GetHelpTextCore() => owner.HelpText;
        protected override bool IsControlElementCore() => true;
    }
}

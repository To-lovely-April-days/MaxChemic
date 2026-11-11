using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using MaxChemical.Modules.Lab3D.Models;

namespace MaxChemical.Modules.Lab3D.Views;

public sealed class Lab3DRealtimeChart : FrameworkElement
{
    private const double PlotPadding = 6;
    private static readonly Brush PrimaryBrush = CreateBrush(0xA4, 0x16, 0x26, 1);
    private static readonly Brush SecondaryBrush = CreateBrush(0x5A, 0x5F, 0x63, 1);
    private static readonly Brush GridBrush = CreateBrush(0xE7, 0xE4, 0xDE, 1);
    private static readonly Brush StrongBrush = CreateBrush(0xCF, 0xCB, 0xC4, 1);
    private static readonly Brush ValueBrush = CreateBrush(0x1F, 0x23, 0x26, 1);
    private static readonly Brush TextBrush = CreateBrush(0x5A, 0x5F, 0x63, 1);
    private static readonly Brush PlaceholderBrush = CreateBrush(0x7A, 0x7E, 0x82, 1);
    private static readonly Brush SurfaceBrush = CreateBrush(0xFB, 0xFA, 0xF7, 1);
    private static readonly Brush TooltipBrush = CreateBrush(0xFF, 0xFF, 0xFF, 1);
    private static readonly Pen PrimaryPen = new(PrimaryBrush, 1.6);
    private static readonly Pen SecondaryPen = new(SecondaryBrush, 1.2);
    private static readonly Pen GridPen = CreatePen(GridBrush, 1);
    private static readonly Pen BaselinePen = CreatePen(StrongBrush, 1);
    private static readonly Pen MarkerPen = CreatePen(PrimaryBrush, 1.5);
    private static readonly Pen TooltipPen = CreatePen(StrongBrush, 1);
    private static readonly Typeface NormalTypeface = new(new FontFamily("Segoe UI, Microsoft YaHei UI"),
        FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly Typeface SemiBoldTypeface = new(new FontFamily("Segoe UI, Microsoft YaHei UI"),
        FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private DateTime _start;
    private DateTime _end;
    private double _minimum;
    private double _maximum;
    private IReadOnlyList<Lab3DParameterPoint> _primary = [];
    private IReadOnlyList<Lab3DParameterPoint> _secondary = [];
    private DateTime? _hoverTime;

    static Lab3DRealtimeChart()
    {
        PrimaryPen.StartLineCap = PenLineCap.Round;
        PrimaryPen.EndLineCap = PenLineCap.Round;
        PrimaryPen.LineJoin = PenLineJoin.Round;
        PrimaryPen.Freeze();
        SecondaryPen.DashStyle = new DashStyle(new double[] { 2.5, 2.5 }, 0);
        SecondaryPen.DashCap = PenLineCap.Flat;
        SecondaryPen.Freeze();
    }

    public Lab3DRealtimeChart()
    {
        Cursor = Cursors.Cross;
        ClipToBounds = true;
        SnapsToDevicePixels = true;
        IsVisibleChanged += (_, _) =>
        {
            _hoverTime = null;
            InvalidateVisual();
        };
    }

    public static readonly DependencyProperty PrimarySamplesProperty = RegisterSamplesProperty(nameof(PrimarySamples));
    public static readonly DependencyProperty SecondarySamplesProperty = RegisterSamplesProperty(nameof(SecondarySamples));
    public static readonly DependencyProperty PrimaryNameProperty = RegisterTextProperty(nameof(PrimaryName), "主参数");
    public static readonly DependencyProperty SecondaryNameProperty = RegisterTextProperty(nameof(SecondaryName), "设定值");
    public static readonly DependencyProperty PrimaryUnitProperty = RegisterTextProperty(nameof(PrimaryUnit), string.Empty);
    public static readonly DependencyProperty SecondaryUnitProperty = RegisterTextProperty(nameof(SecondaryUnit), string.Empty);

    public IReadOnlyList<Lab3DParameterPoint> PrimarySamples
    {
        get => (IReadOnlyList<Lab3DParameterPoint>)GetValue(PrimarySamplesProperty);
        set => SetValue(PrimarySamplesProperty, value);
    }

    public IReadOnlyList<Lab3DParameterPoint> SecondarySamples
    {
        get => (IReadOnlyList<Lab3DParameterPoint>)GetValue(SecondarySamplesProperty);
        set => SetValue(SecondarySamplesProperty, value);
    }

    public string PrimaryName
    {
        get => (string)GetValue(PrimaryNameProperty);
        set => SetValue(PrimaryNameProperty, value);
    }

    public string SecondaryName
    {
        get => (string)GetValue(SecondaryNameProperty);
        set => SetValue(SecondaryNameProperty, value);
    }

    public string PrimaryUnit
    {
        get => (string)GetValue(PrimaryUnitProperty);
        set => SetValue(PrimaryUnitProperty, value);
    }

    public string SecondaryUnit
    {
        get => (string)GetValue(SecondaryUnitProperty);
        set => SetValue(SecondaryUnitProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? 0 : availableSize.Height);

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        double width = ActualWidth;
        double height = ActualHeight;
        if (width < 20 || height < 20) return;
        // 透明底使整个绘图区可命中，悬停提示不再只在线条像素上触发
        drawingContext.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, width, height));

        _primary = PrimarySamples ?? [];
        _secondary = SecondarySamples ?? [];
        if (_primary.Count == 0 && _secondary.Count == 0)
        {
            _hoverTime = null;
            DrawGrid(drawingContext, width, height);
            var placeholder = CreateText("--\u3000等待有效采样", 12, PlaceholderBrush, Math.Max(1, width - 20));
            double pillWidth = Math.Min(width, placeholder.Width + 20);
            const double pillHeight = 22;
            drawingContext.DrawRoundedRectangle(SurfaceBrush, null,
                new Rect((width - pillWidth) / 2, (height - pillHeight) / 2, pillWidth, pillHeight), 4, 4);
            drawingContext.DrawText(placeholder,
                new Point((width - placeholder.Width) / 2, (height - placeholder.Height) / 2));
            return;
        }

        _end = new[] { _primary.LastOrDefault()?.Timestamp, _secondary.LastOrDefault()?.Timestamp }
            .Where(timestamp => timestamp.HasValue).Max(timestamp => timestamp!.Value);
        _start = _end.AddSeconds(-60);
        _primary = _primary.Where(point => point.Timestamp >= _start).ToArray();
        _secondary = _secondary.Where(point => point.Timestamp >= _start).ToArray();
        var values = _primary.Select(point => point.Value).Concat(_secondary.Select(point => point.Value)).ToArray();
        _minimum = values.Min();
        _maximum = values.Max();
        if (_maximum - _minimum < 0.000001)
        {
            double padding = Math.Max(0.5, Math.Abs(_maximum) * 0.05);
            _minimum -= padding;
            _maximum += padding;
        }

        DrawGrid(drawingContext, width, height);
        DrawSeries(drawingContext, _secondary, SecondaryPen, width, height);
        DrawSeries(drawingContext, _primary, PrimaryPen, width, height);
        if (_hoverTime.HasValue) DrawHover(drawingContext, width, height, _hoverTime.Value);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_primary.Count == 0 && _secondary.Count == 0) return;
        double ratio = Math.Clamp(e.GetPosition(this).X / Math.Max(1, ActualWidth), 0, 1);
        _hoverTime = _start.AddSeconds(ratio * 60);
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _hoverTime = null;
        InvalidateVisual();
    }

    private void DrawGrid(DrawingContext drawingContext, double width, double height)
    {
        double middle = Math.Floor(height / 2) + 0.5;
        drawingContext.DrawLine(GridPen, new Point(0, 0.5), new Point(width, 0.5));
        drawingContext.DrawLine(GridPen, new Point(0, middle), new Point(width, middle));
        drawingContext.DrawLine(BaselinePen, new Point(0, height - 0.5), new Point(width, height - 0.5));
    }

    private void DrawSeries(DrawingContext drawingContext, IReadOnlyList<Lab3DParameterPoint> samples,
        Pen pen, double width, double height)
    {
        if (samples.Count == 0) return;
        if (samples.Count == 1)
        {
            drawingContext.DrawEllipse(pen.Brush, null, Project(samples[0], width, height), 2, 2);
            return;
        }
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(Project(samples[0], width, height), false, false);
            for (int i = 1; i < samples.Count; i++) context.LineTo(Project(samples[i], width, height), true, false);
        }
        geometry.Freeze();
        drawingContext.DrawGeometry(null, pen, geometry);
    }

    private void DrawHover(DrawingContext drawingContext, double width, double height, DateTime timestamp)
    {
        var primary = Closest(_primary, timestamp);
        var secondary = Closest(_secondary, timestamp);
        var marker = primary ?? secondary;
        if (marker is null) return;
        var markerPoint = Project(marker, width, height);
        double x = markerPoint.X;
        drawingContext.DrawLine(BaselinePen, new Point(x, 0), new Point(x, height));
        drawingContext.DrawEllipse(TooltipBrush, MarkerPen, markerPoint, 3.5, 3.5);

        const double tooltipWidth = 128;
        const double tooltipHeight = 58;
        const double tooltipTop = 2;
        const double textWidth = 112;
        const double linePitch = 16;
        double tooltipX = x + 8 + tooltipWidth > width ? Math.Max(0, x - tooltipWidth - 8) : x + 8;
        var tooltip = new Rect(tooltipX, tooltipTop, tooltipWidth, tooltipHeight);
        drawingContext.DrawRoundedRectangle(TooltipBrush, TooltipPen, tooltip, 6, 6);
        DateTime referenceTime = _primary.LastOrDefault()?.Timestamp ?? _secondary.LastOrDefault()!.Timestamp;
        int secondsAgo = Math.Max(0, (int)(referenceTime - timestamp).TotalSeconds);
        double textX = tooltipX + 8;
        double textY = tooltipTop + 5;
        DrawText(drawingContext, $"{secondsAgo} 秒前", 12, TextBrush, new Point(textX, textY), textWidth);
        DrawTooltipRow(drawingContext, PrimaryName, FormatNumber(primary, PrimaryUnit),
            new Point(textX, textY + linePitch), textWidth);
        DrawTooltipRow(drawingContext, SecondaryName, FormatNumber(secondary, SecondaryUnit),
            new Point(textX, textY + linePitch * 2), textWidth);
    }

    // 先右对齐画数值，再把名称限制在剩余宽度内，避免名称与数值重叠
    private void DrawTooltipRow(DrawingContext drawingContext, string name, string value, Point position, double rowWidth)
    {
        double valueWidth = Math.Min(rowWidth, CreateText(value, 12, ValueBrush, rowWidth, true).Width);
        DrawText(drawingContext, value, 12, ValueBrush, position, rowWidth, true, TextAlignment.Right);
        double nameWidth = rowWidth - valueWidth - 6;
        if (nameWidth < 12) return;
        DrawText(drawingContext, name, 12, TextBrush, position, nameWidth);
    }

    private Point Project(Lab3DParameterPoint point, double width, double height)
    {
        double x = (point.Timestamp - _start).TotalSeconds / 60 * width;
        double y = PlotPadding + (_maximum - point.Value) * (height - PlotPadding * 2) / (_maximum - _minimum);
        return new Point(Math.Clamp(x, 0, width), Math.Clamp(y, PlotPadding, height - PlotPadding));
    }

    private static Lab3DParameterPoint? Closest(IEnumerable<Lab3DParameterPoint> samples, DateTime timestamp) =>
        samples.OrderBy(point => Math.Abs((point.Timestamp - timestamp).Ticks)).FirstOrDefault();

    private static string FormatValue(string name, Lab3DParameterPoint? point, string unit) =>
        $"{name}  {(point is null ? "--" : point.Value.ToString("0.##", CultureInfo.InvariantCulture))} {unit}".TrimEnd();

    private static string FormatNumber(Lab3DParameterPoint? point, string unit) =>
        $"{(point is null ? "--" : point.Value.ToString("0.##", CultureInfo.InvariantCulture))} {unit}".TrimEnd();

    private void DrawText(DrawingContext drawingContext, string text, double size, Brush brush,
        Point position, double maximumWidth, bool semiBold = false, TextAlignment alignment = TextAlignment.Left)
    {
        drawingContext.DrawText(CreateText(text, size, brush, maximumWidth, semiBold, alignment), position);
    }

    private FormattedText CreateText(string text, double size, Brush brush, double maximumWidth,
        bool semiBold = false, TextAlignment alignment = TextAlignment.Left)
    {
        return new FormattedText(text ?? string.Empty, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            semiBold ? SemiBoldTypeface : NormalTypeface, size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip)
        {
            MaxTextWidth = maximumWidth,
            MaxTextHeight = 18,
            Trimming = TextTrimming.CharacterEllipsis,
            TextAlignment = alignment
        };
    }

    private static DependencyProperty RegisterSamplesProperty(string name) =>
        DependencyProperty.Register(name, typeof(IReadOnlyList<Lab3DParameterPoint>), typeof(Lab3DRealtimeChart),
            new FrameworkPropertyMetadata(Array.Empty<Lab3DParameterPoint>(), OnPropertyChanged));

    private static DependencyProperty RegisterTextProperty(string name, string defaultValue) =>
        DependencyProperty.Register(name, typeof(string), typeof(Lab3DRealtimeChart),
            new FrameworkPropertyMetadata(defaultValue, OnPropertyChanged));

    private static void OnPropertyChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e) =>
        ((Lab3DRealtimeChart)sender).InvalidateVisual();

    private static SolidColorBrush CreateBrush(byte red, byte green, byte blue, double opacity)
    {
        var brush = new SolidColorBrush(Color.FromRgb(red, green, blue)) { Opacity = opacity };
        brush.Freeze();
        return brush;
    }

    private static Pen CreatePen(Brush brush, double thickness)
    {
        var pen = new Pen(brush, thickness);
        pen.Freeze();
        return pen;
    }
}

using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using MaxChemical.Modules.Lab3D.Models;

namespace MaxChemical.Modules.Lab3D.Views;

public sealed class Lab3DRealtimeChart : FrameworkElement
{
    private static readonly Brush PrimaryBrush = CreateBrush(0xA4, 0x16, 0x26, 1);
    private static readonly Brush SecondaryBrush = CreateBrush(0x88, 0x87, 0x80, 0.9);
    private static readonly Brush GridBrush = CreateBrush(0xE8, 0xE5, 0xE2, 1);
    private static readonly Brush TextBrush = CreateBrush(0x5F, 0x5E, 0x5A, 1);
    private static readonly Brush TooltipBrush = CreateBrush(0xFF, 0xFF, 0xFF, 0.97);
    private static readonly Pen PrimaryPen = CreatePen(PrimaryBrush, 1.4);
    private static readonly Pen SecondaryPen = new(SecondaryBrush, 1);
    private static readonly Pen GridPen = CreatePen(GridBrush, 0.8);
    private static readonly Pen TooltipPen = CreatePen(CreateBrush(0xD3, 0xD1, 0xC7, 1), 0.8);
    private DateTime _start;
    private DateTime _end;
    private double _minimum;
    private double _maximum;
    private IReadOnlyList<Lab3DParameterPoint> _primary = [];
    private IReadOnlyList<Lab3DParameterPoint> _secondary = [];
    private DateTime? _hoverTime;

    static Lab3DRealtimeChart()
    {
        SecondaryPen.DashStyle = new DashStyle([2, 2], 0);
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

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        double width = ActualWidth;
        double height = ActualHeight;
        if (width < 20 || height < 20) return;

        _primary = PrimarySamples ?? [];
        _secondary = SecondarySamples ?? [];
        if (_primary.Count == 0 && _secondary.Count == 0)
        {
            _hoverTime = null;
            DrawGrid(drawingContext, width, height);
            DrawText(drawingContext, "--  等待有效采样", 8.5, TextBrush,
                new Point(width / 2 - 41, height / 2 - 7), 120);
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
        for (int i = 0; i < 3; i++)
        {
            double y = 4 + i * (height - 8) / 2;
            drawingContext.DrawLine(GridPen, new Point(0, y), new Point(width, y));
        }
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
        double x = Project(marker, width, height).X;
        drawingContext.DrawLine(GridPen, new Point(x, 3), new Point(x, height - 3));
        drawingContext.DrawEllipse(PrimaryBrush, null, Project(marker, width, height), 2.5, 2.5);

        const double tooltipWidth = 96;
        const double tooltipHeight = 49;
        double tooltipX = x + 8 + tooltipWidth > width ? Math.Max(0, x - tooltipWidth - 8) : x + 8;
        var tooltip = new Rect(tooltipX, 7, tooltipWidth, tooltipHeight);
        drawingContext.DrawRectangle(TooltipBrush, TooltipPen, tooltip);
        DateTime referenceTime = _primary.LastOrDefault()?.Timestamp ?? _secondary.LastOrDefault()!.Timestamp;
        int secondsAgo = Math.Max(0, (int)(referenceTime - timestamp).TotalSeconds);
        DrawText(drawingContext, $"{secondsAgo} 秒前", 8, TextBrush, new Point(tooltipX + 7, 12), tooltipWidth - 12);
        DrawText(drawingContext, FormatValue(PrimaryName, primary, PrimaryUnit), 8.5, TextBrush,
            new Point(tooltipX + 7, 25), tooltipWidth - 12);
        DrawText(drawingContext, FormatValue(SecondaryName, secondary, SecondaryUnit), 8.5, TextBrush,
            new Point(tooltipX + 7, 39), tooltipWidth - 12);
    }

    private Point Project(Lab3DParameterPoint point, double width, double height)
    {
        double x = (point.Timestamp - _start).TotalSeconds / 60 * width;
        double y = 4 + (_maximum - point.Value) * (height - 8) / (_maximum - _minimum);
        return new Point(Math.Clamp(x, 0, width), Math.Clamp(y, 4, height - 4));
    }

    private static Lab3DParameterPoint? Closest(IEnumerable<Lab3DParameterPoint> samples, DateTime timestamp) =>
        samples.OrderBy(point => Math.Abs((point.Timestamp - timestamp).Ticks)).FirstOrDefault();

    private static string FormatValue(string name, Lab3DParameterPoint? point, string unit) =>
        $"{name}  {(point is null ? "--" : point.Value.ToString("0.##", CultureInfo.InvariantCulture))} {unit}".TrimEnd();

    private static void DrawText(DrawingContext drawingContext, string text, double size, Brush brush,
        Point position, double maximumWidth)
    {
        var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface("Microsoft YaHei UI"), size, brush, 1)
        {
            MaxTextWidth = maximumWidth,
            MaxTextHeight = 15,
            Trimming = TextTrimming.CharacterEllipsis
        };
        drawingContext.DrawText(formatted, position);
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

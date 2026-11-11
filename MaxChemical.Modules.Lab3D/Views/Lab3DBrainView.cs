using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using MaxChemical.Modules.Lab3D.Models;

namespace MaxChemical.Modules.Lab3D.Views;

/// <summary>
/// Side-profile point-cloud brain with four input curves on the left and three output curves on the right.
/// Designed for a 280x150 box; anything else is scaled uniformly and centred.
/// </summary>
public sealed class Lab3DBrainView : FrameworkElement
{
    private const double DesignWidth = 280;
    private const double DesignHeight = 150;
    // A normalised reference point (x, y) maps to design (BrainLeft + BrainScale * x, BrainTop + BrainScale * y).
    private const double BrainLeft = 56;
    private const double BrainTop = -6.8;
    private const double BrainScale = 168;
    // Stroke unit relative to the layout scale (reference renderer: 0.64). One constant for tuning.
    private const double StrokeUnit = 0.8;
    private const double HitScale = 1000;
    private const int CerebrumPointCount = 760;
    private const int CerebellumPointCount = 120;
    private const int StemPointCount = 40;
    private const int SulcusPointCount = 15;
    private const int SampleGuard = 200000;
    private const double ConnectionDistanceSquared = 0.0016;
    private const double WavePeriod = 7.5;
    private const double WaveWidth = 0.11;
    private const double PulsePeriod = 3.3;
    private const int InputCount = 4;
    private const int OutputCount = 3;
    private const double CerebellumCenterX = 0.745;
    private const double CerebellumCenterY = 0.685;
    private const double CerebellumRadiusX = 0.125;
    private const double CerebellumRadiusY = 0.085;
    private const double CerebellumRotation = -0.15;

    private static readonly double[][] Sulci =
    {
        new[] { .30, .56, .42, .50, .56, .47, .69, .40 },
        new[] { .50, .10, .53, .22, .46, .32, .50, .43 },
        new[] { .33, .15, .37, .26, .30, .34, .34, .44 },
        new[] { .66, .11, .69, .21, .63, .28, .67, .36 },
        new[] { .20, .34, .26, .36, .25, .44, .31, .47 },
        new[] { .76, .22, .80, .32, .74, .40, .79, .50 },
        new[] { .38, .60, .46, .58, .52, .62, .60, .57 },
        new[] { .40, .68, .46, .66, .52, .69, .57, .65 },
        new[] { .57, .14, .60, .24, .56, .30, .59, .38 },
        new[] { .42, .14, .44, .24, .39, .32, .42, .42 },
        new[] { .24, .22, .29, .27, .25, .32, .28, .38 },
        new[] { .72, .44, .76, .47, .80, .44, .85, .46 }
    };

    // Design-space cubic control points (x0, y0, x1, y1, x2, y2, x3, y3): 4 inputs, then 3 outputs.
    private static readonly double[][] Curves =
    {
        new double[] { 0, 21, 60, 21, 100, 75, 140, 75 },
        new double[] { 0, 57, 60, 57, 100, 75, 140, 75 },
        new double[] { 0, 93, 60, 93, 100, 75, 140, 75 },
        new double[] { 0, 129, 60, 129, 100, 75, 140, 75 },
        new double[] { 140, 75, 180, 75, 220, 27, 280, 27 },
        new double[] { 140, 75, 180, 75, 220, 75, 280, 75 },
        new double[] { 140, 75, 180, 75, 220, 123, 280, 123 }
    };

    // Indexed by StateIndex: off, live, stale, alert.
    private static readonly Color[] LineColors =
    {
        Color.FromRgb(0xCF, 0xCB, 0xC4),
        Color.FromRgb(0x12, 0x70, 0x4F),
        Color.FromRgb(0x8A, 0x5A, 0x00),
        Color.FromRgb(0xA4, 0x16, 0x26)
    };

    private static readonly Color SlateColor = Color.FromRgb(82, 97, 107);

    // Wave colours; index 0 (off) is never drawn as a wave and falls back to slate.
    private static readonly Color[] WaveColors =
    {
        SlateColor,
        Color.FromRgb(29, 158, 117),
        Color.FromRgb(138, 90, 0),
        Color.FromRgb(164, 22, 38)
    };

    private static readonly Brush[] PulseBrushes = CreateBrushes(LineColors, 1);
    private static readonly Brush[] WaveBrightBrushes = CreateBrushes(WaveColors, 0.98);
    private static readonly Brush[] WaveBrushes = CreateBrushes(WaveColors, 0.78);
    private static readonly Brush[] WaveSoftBrushes = CreateBrushes(WaveColors, 0.45);
    private static readonly Brush DotBrush = CreateBrush(SlateColor, 0.74);
    private static readonly Brush DotBrightBrush = CreateBrush(Color.FromRgb(120, 136, 146), 0.92);

    private readonly DispatcherTimer _timer = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(40) };
    private readonly double[] _pointX;
    private readonly double[] _pointY;
    private readonly double[] _pointPhase;
    private readonly int[] _connectionFrom;
    private readonly int[] _connectionTo;
    private readonly double[] _connectionPhase;
    private readonly int _pointCount;
    private readonly Lab3DSignalState[] _inputs = new Lab3DSignalState[InputCount];
    private readonly Lab3DSignalState[] _outputs = new Lab3DSignalState[OutputCount];
    private Lab3DSignalState _overall = Lab3DSignalState.Off;

    // Size-dependent cache, rebuilt lazily in OnRender.
    private DrawingGroup? _staticLayer;
    private double _cachedWidth = -1;
    private double _cachedHeight = -1;
    private double _unit = 1;
    private double _originX;
    private double _originY;
    private Point[] _screen = [];
    private Geometry[] _curveGeometries = [];
    private Pen[] _curvePens = [];
    private Pen[] _waveLinePens = [];
    private Pen[] _focusPens = [];

    private double _time;
    private int _focusIndex = -1;
    private bool _animationAllowed = true;

    public Lab3DBrainView()
    {
        var random = new Random(731);
        var xs = new List<double>(1100);
        var ys = new List<double>(1100);

        var cerebrum = CreateCerebrum(0, 0, HitScale);
        var stem = CreateStem(0, 0, HitScale);
        SamplePoints(random, xs, ys, CerebrumPointCount, .08, .96, .04, .80,
            (x, y) => cerebrum.FillContains(new Point(x * HitScale, y * HitScale)));
        SamplePoints(random, xs, ys, CerebellumPointCount, .60, .90, .58, .80, IsInsideCerebellum);
        SamplePoints(random, xs, ys, StemPointCount, .52, .68, .64, .90,
            (x, y) => stem.FillContains(new Point(x * HitScale, y * HitScale)));

        // Extra points hugging the sulci so the folds read.
        foreach (double[] sulcus in Sulci)
        {
            for (int i = 0; i < SulcusPointCount; i++)
            {
                double t = i / (SulcusPointCount - 1.0);
                double x = Cubic(sulcus[0], sulcus[2], sulcus[4], sulcus[6], t);
                double y = Cubic(sulcus[1], sulcus[3], sulcus[5], sulcus[7], t);
                xs.Add(x + (random.NextDouble() - 0.5) * 0.012);
                ys.Add(y + (random.NextDouble() - 0.5) * 0.012);
            }
        }

        _pointCount = xs.Count;
        _pointX = xs.ToArray();
        _pointY = ys.ToArray();
        _pointPhase = new double[_pointCount];
        for (int i = 0; i < _pointCount; i++) _pointPhase[i] = _pointX[i] + _pointY[i] * 0.35;

        // Connections: each point to its 3 nearest neighbours (kept once, only when close enough).
        var from = new List<int>(1600);
        var to = new List<int>(1600);
        for (int i = 0; i < _pointCount; i++)
        {
            int first = -1, second = -1, third = -1;
            double firstDistance = double.MaxValue, secondDistance = double.MaxValue, thirdDistance = double.MaxValue;
            for (int j = 0; j < _pointCount; j++)
            {
                if (j == i) continue;
                double dx = _pointX[i] - _pointX[j];
                double dy = _pointY[i] - _pointY[j];
                double distance = dx * dx + dy * dy;
                if (distance < firstDistance)
                {
                    third = second;
                    thirdDistance = secondDistance;
                    second = first;
                    secondDistance = firstDistance;
                    first = j;
                    firstDistance = distance;
                }
                else if (distance < secondDistance)
                {
                    third = second;
                    thirdDistance = secondDistance;
                    second = j;
                    secondDistance = distance;
                }
                else if (distance < thirdDistance)
                {
                    third = j;
                    thirdDistance = distance;
                }
            }
            if (first > i && firstDistance < ConnectionDistanceSquared) { from.Add(i); to.Add(first); }
            if (second > i && secondDistance < ConnectionDistanceSquared) { from.Add(i); to.Add(second); }
            if (third > i && thirdDistance < ConnectionDistanceSquared) { from.Add(i); to.Add(third); }
        }
        _connectionFrom = from.ToArray();
        _connectionTo = to.ToArray();
        _connectionPhase = new double[_connectionFrom.Length];
        for (int i = 0; i < _connectionFrom.Length; i++)
            _connectionPhase[i] = (_pointPhase[_connectionFrom[i]] + _pointPhase[_connectionTo[i]]) / 2;

        SnapsToDevicePixels = true;
        Loaded += (_, _) => UpdateAnimation();
        IsVisibleChanged += (_, _) => UpdateAnimation();
        Unloaded += (_, _) => _timer.Stop();
        _timer.Tick += (_, _) =>
        {
            _time += 0.04;
            if (_overall == Lab3DSignalState.Off && _focusIndex < 0
                && Array.TrueForAll(_inputs, state => state == Lab3DSignalState.Off)
                && Array.TrueForAll(_outputs, state => state == Lab3DSignalState.Off)) return;
            InvalidateVisual();
        };
    }

    public void SetFocus(string? id)
    {
        int index = -1;
        if (!string.IsNullOrEmpty(id) && _pointCount > 0)
        {
            int hash = 17;
            unchecked
            {
                foreach (char character in id) hash = hash * 31 + character;
                index = (int)((uint)hash % (uint)_pointCount);
            }
        }
        if (index == _focusIndex) return;
        _focusIndex = index;
        InvalidateVisual();
    }

    public void SetSignals(IReadOnlyList<Lab3DSignalState>? inputs, IReadOnlyList<Lab3DSignalState>? outputs,
        Lab3DSignalState overall)
    {
        bool changed = false;
        for (int i = 0; i < InputCount; i++)
        {
            var state = inputs is not null && i < inputs.Count ? inputs[i] : Lab3DSignalState.Off;
            if (_inputs[i] == state) continue;
            _inputs[i] = state;
            changed = true;
        }
        for (int i = 0; i < OutputCount; i++)
        {
            var state = outputs is not null && i < outputs.Count ? outputs[i] : Lab3DSignalState.Off;
            if (_outputs[i] == state) continue;
            _outputs[i] = state;
            changed = true;
        }
        if (_overall != overall)
        {
            _overall = overall;
            changed = true;
        }
        if (changed) InvalidateVisual();
    }

    internal void SetAnimationActive(bool active)
    {
        _animationAllowed = active;
        UpdateAnimation();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        _staticLayer = null;
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        double width = ActualWidth;
        double height = ActualHeight;
        if (width < 10 || height < 10) return;

        var layer = _staticLayer;
        if (layer is null || width != _cachedWidth || height != _cachedHeight)
            layer = BuildCache(width, height);

        DrawCurves(drawingContext);
        drawingContext.DrawDrawing(layer);
        if (_overall != Lab3DSignalState.Off) DrawWave(drawingContext);
        DrawFocus(drawingContext);
    }

    private DrawingGroup BuildCache(double width, double height)
    {
        double unit = Math.Min(width / DesignWidth, height / DesignHeight);
        _unit = unit;
        _originX = (width - DesignWidth * unit) / 2;
        _originY = (height - DesignHeight * unit) / 2;
        _cachedWidth = width;
        _cachedHeight = height;

        double offsetX = _originX + BrainLeft * unit;
        double offsetY = _originY + BrainTop * unit;
        double scale = BrainScale * unit;
        double k = StrokeUnit * unit;

        if (_screen.Length != _pointCount) _screen = new Point[_pointCount];
        for (int i = 0; i < _pointCount; i++)
            _screen[i] = new Point(offsetX + _pointX[i] * scale, offsetY + _pointY[i] * scale);

        // Curves and state pens.
        var curves = new Geometry[Curves.Length];
        for (int i = 0; i < Curves.Length; i++)
        {
            double[] c = Curves[i];
            var geometry = new StreamGeometry();
            using (var context = geometry.Open())
            {
                context.BeginFigure(Design(c[0], c[1]), false, false);
                context.BezierTo(Design(c[2], c[3]), Design(c[4], c[5]), Design(c[6], c[7]), true, false);
            }
            geometry.Freeze();
            curves[i] = geometry;
        }
        _curveGeometries = curves;

        var curvePens = new Pen[LineColors.Length];
        var waveLinePens = new Pen[LineColors.Length];
        var focusPens = new Pen[LineColors.Length];
        for (int i = 0; i < LineColors.Length; i++)
        {
            var curvePen = new Pen(CreateBrush(LineColors[i], 1), 1.3 * unit);
            if (i == 0)
            {
                // Dash lengths are multiples of the pen thickness: 3 design px on, 3 off.
                curvePen.DashStyle = new DashStyle(new double[] { 3 / 1.3, 3 / 1.3 }, 0);
                curvePen.DashCap = PenLineCap.Flat;
            }
            else
            {
                curvePen.StartLineCap = PenLineCap.Round;
                curvePen.EndLineCap = PenLineCap.Round;
            }
            curvePen.Freeze();
            curvePens[i] = curvePen;
            waveLinePens[i] = CreatePen(WaveColors[i], 0.6, 0.6 * k);
            focusPens[i] = CreatePen(WaveColors[i], i == 0 ? 0.6 : 0.85, 0.8 * unit);
        }
        _curvePens = curvePens;
        _waveLinePens = waveLinePens;
        _focusPens = focusPens;

        // Static layer: silhouette, sulci, cerebellum arcs, base connections, base dots.
        var cerebrum = CreateCerebrum(offsetX, offsetY, scale);
        var stem = CreateStem(offsetX, offsetY, scale);
        var cerebellumCenter = new Point(offsetX + CerebellumCenterX * scale, offsetY + CerebellumCenterY * scale);
        var cerebellum = new EllipseGeometry(cerebellumCenter, CerebellumRadiusX * scale, CerebellumRadiusY * scale)
        {
            Transform = new RotateTransform(-8.594, cerebellumCenter.X, cerebellumCenter.Y)
        };
        cerebellum.Freeze();

        var sulci = new StreamGeometry();
        using (var context = sulci.Open())
        {
            foreach (double[] s in Sulci)
            {
                context.BeginFigure(new Point(offsetX + s[0] * scale, offsetY + s[1] * scale), false, false);
                context.BezierTo(
                    new Point(offsetX + s[2] * scale, offsetY + s[3] * scale),
                    new Point(offsetX + s[4] * scale, offsetY + s[5] * scale),
                    new Point(offsetX + s[6] * scale, offsetY + s[7] * scale), true, false);
            }
        }
        sulci.Freeze();

        var arcs = new StreamGeometry();
        using (var context = arcs.Open())
        {
            double cosine = Math.Cos(CerebellumRotation);
            double sine = Math.Sin(CerebellumRotation);
            for (int i = 0; i < 7; i++)
            {
                double centerX = .80;
                double centerY = .60 + i * .012;
                double radiusX = .06 + i * .028;
                double radiusY = .03 + i * .02;
                for (int step = 0; step < 16; step++)
                {
                    double angle = Math.PI * (.55 + (1.25 - .55) * step / 15.0);
                    double localX = radiusX * Math.Cos(angle);
                    double localY = radiusY * Math.Sin(angle);
                    var point = new Point(
                        offsetX + (centerX + localX * cosine - localY * sine) * scale,
                        offsetY + (centerY + localX * sine + localY * cosine) * scale);
                    if (step == 0) context.BeginFigure(point, false, false);
                    else context.LineTo(point, true, true);
                }
            }
        }
        arcs.Freeze();

        var network = new StreamGeometry();
        using (var context = network.Open())
        {
            for (int i = 0; i < _connectionFrom.Length; i++)
            {
                context.BeginFigure(_screen[_connectionFrom[i]], false, false);
                context.LineTo(_screen[_connectionTo[i]], true, false);
            }
        }
        network.Freeze();

        var dots = new StreamGeometry { FillRule = FillRule.Nonzero };
        var brightDots = new StreamGeometry { FillRule = FillRule.Nonzero };
        using (var context = dots.Open())
        using (var brightContext = brightDots.Open())
        {
            for (int i = 0; i < _pointCount; i++)
            {
                if (i % 5 == 0) AddDot(brightContext, _screen[i], 0.95 * k);
                else AddDot(context, _screen[i], 0.7 * k);
            }
        }
        dots.Freeze();
        brightDots.Freeze();

        var silhouettePen = CreatePen(SlateColor, 0.5, 0.55 * k);
        var sulciPen = CreatePen(SlateColor, 0.36, 0.5 * k);
        var arcPen = CreatePen(SlateColor, 0.34, 0.45 * k);
        var networkPen = CreatePen(SlateColor, 0.2, 0.4 * k);

        var group = new DrawingGroup();
        using (var dc = group.Open())
        {
            dc.DrawGeometry(null, silhouettePen, cerebrum);
            dc.DrawGeometry(null, silhouettePen, cerebellum);
            dc.DrawGeometry(null, silhouettePen, stem);
            dc.PushClip(cerebrum);
            dc.DrawGeometry(null, sulciPen, sulci);
            dc.Pop();
            dc.PushClip(cerebellum);
            dc.DrawGeometry(null, arcPen, arcs);
            dc.Pop();
            dc.DrawGeometry(null, networkPen, network);
            dc.DrawGeometry(DotBrush, null, dots);
            dc.DrawGeometry(DotBrightBrush, null, brightDots);
        }
        group.Freeze();
        _staticLayer = group;
        return group;
    }

    private void DrawCurves(DrawingContext drawingContext)
    {
        if (_curveGeometries.Length != Curves.Length || _curvePens.Length != LineColors.Length) return;
        double radius = 2.6 * _unit;
        for (int i = 0; i < Curves.Length; i++)
        {
            var state = i < InputCount ? _inputs[i] : _outputs[i - InputCount];
            int index = StateIndex(state);
            drawingContext.DrawGeometry(null, _curvePens[index], _curveGeometries[i]);
            if (state == Lab3DSignalState.Off) continue;

            double[] c = Curves[i];
            double t = _time / PulsePeriod + i * 0.19;
            t -= Math.Floor(t);
            var pulse = Design(Cubic(c[0], c[2], c[4], c[6], t), Cubic(c[1], c[3], c[5], c[7], t));
            drawingContext.DrawEllipse(PulseBrushes[index], null, pulse, radius, radius);
        }
    }

    private void DrawWave(DrawingContext drawingContext)
    {
        if (_screen.Length != _pointCount || _waveLinePens.Length != LineColors.Length) return;
        int index = StateIndex(_overall);
        double cycle = _time % WavePeriod / WavePeriod;
        double wave = -0.15 + cycle * 1.5;
        double k = StrokeUnit * _unit;

        var lines = new StreamGeometry();
        using (var context = lines.Open())
        {
            for (int i = 0; i < _connectionFrom.Length; i++)
            {
                if (Activation(_connectionPhase[i], wave) < 0.4) continue;
                context.BeginFigure(_screen[_connectionFrom[i]], false, false);
                context.LineTo(_screen[_connectionTo[i]], true, false);
            }
        }
        lines.Freeze();
        drawingContext.DrawGeometry(null, _waveLinePens[index], lines);

        var bright = new StreamGeometry { FillRule = FillRule.Nonzero };
        var medium = new StreamGeometry { FillRule = FillRule.Nonzero };
        var soft = new StreamGeometry { FillRule = FillRule.Nonzero };
        using (var brightContext = bright.Open())
        using (var mediumContext = medium.Open())
        using (var softContext = soft.Open())
        {
            for (int i = 0; i < _pointCount; i += 2)
            {
                double activation = Activation(_pointPhase[i], wave);
                if (activation <= 0.07) continue;
                double radius = ((i % 5 == 0 ? 0.95 : 0.7) + activation * 0.75) * k;
                var target = activation > 0.55 ? brightContext : activation > 0.2 ? mediumContext : softContext;
                AddDot(target, _screen[i], radius);
            }
        }
        bright.Freeze();
        medium.Freeze();
        soft.Freeze();
        drawingContext.DrawGeometry(WaveSoftBrushes[index], null, soft);
        drawingContext.DrawGeometry(WaveBrushes[index], null, medium);
        drawingContext.DrawGeometry(WaveBrightBrushes[index], null, bright);
    }

    private void DrawFocus(DrawingContext drawingContext)
    {
        if (_focusIndex < 0 || _focusIndex >= _screen.Length || _focusPens.Length != LineColors.Length) return;
        int index = StateIndex(_overall);
        var center = _screen[_focusIndex];
        double dot = 1.1 * _unit;
        double ring = (2.8 + Math.Sin(_time * 2) * 0.35) * _unit;
        drawingContext.DrawEllipse(WaveBrightBrushes[index], null, center, dot, dot);
        drawingContext.DrawEllipse(null, _focusPens[index], center, ring, ring);
    }

    private void UpdateAnimation()
    {
        if (_animationAllowed && IsVisible) _timer.Start();
        else _timer.Stop();
    }

    private Point Design(double x, double y) => new(_originX + x * _unit, _originY + y * _unit);

    private static int StateIndex(Lab3DSignalState state) => state switch
    {
        Lab3DSignalState.Live => 1,
        Lab3DSignalState.Stale => 2,
        Lab3DSignalState.Alert => 3,
        _ => 0
    };

    private static double Activation(double phase, double wave)
    {
        double distance = (phase - wave) / WaveWidth;
        // exp(-9) is far below every threshold used; skip the call.
        if (distance > 3 || distance < -3) return 0;
        return Math.Exp(-distance * distance);
    }

    private static double Cubic(double p0, double p1, double p2, double p3, double t)
    {
        double u = 1 - t;
        return u * u * u * p0 + 3 * u * u * t * p1 + 3 * u * t * t * p2 + t * t * t * p3;
    }

    private static void SamplePoints(Random random, List<double> xs, List<double> ys, int count,
        double minimumX, double maximumX, double minimumY, double maximumY, Func<double, double, bool> contains)
    {
        int found = 0;
        int guard = 0;
        while (found < count && guard++ < SampleGuard)
        {
            double x = minimumX + random.NextDouble() * (maximumX - minimumX);
            double y = minimumY + random.NextDouble() * (maximumY - minimumY);
            if (!contains(x, y)) continue;
            xs.Add(x);
            ys.Add(y);
            found++;
        }
    }

    private static bool IsInsideCerebellum(double x, double y)
    {
        double dx = x - CerebellumCenterX;
        double dy = y - CerebellumCenterY;
        double cosine = Math.Cos(CerebellumRotation);
        double sine = Math.Sin(CerebellumRotation);
        double localX = dx * cosine + dy * sine;
        double localY = -dx * sine + dy * cosine;
        return localX * localX / (CerebellumRadiusX * CerebellumRadiusX)
            + localY * localY / (CerebellumRadiusY * CerebellumRadiusY) <= 1;
    }

    private static StreamGeometry CreateCerebrum(double offsetX, double offsetY, double scale)
    {
        Point P(double x, double y) => new(offsetX + x * scale, offsetY + y * scale);

        var geometry = new StreamGeometry { FillRule = FillRule.Nonzero };
        using (var context = geometry.Open())
        {
            context.BeginFigure(P(.17, .50), true, true);
            context.BezierTo(P(.10, .36), P(.20, .16), P(.40, .11), true, true);
            context.BezierTo(P(.58, .06), P(.80, .12), P(.88, .30), true, true);
            context.BezierTo(P(.94, .44), P(.88, .56), P(.78, .58), true, true);
            context.BezierTo(P(.70, .60), P(.66, .62), P(.60, .66), true, true);
            context.BezierTo(P(.52, .74), P(.38, .76), P(.32, .68), true, true);
            context.BezierTo(P(.29, .63), P(.30, .58), P(.27, .56), true, true);
            context.BezierTo(P(.23, .55), P(.19, .55), P(.17, .50), true, true);
        }
        geometry.Freeze();
        return geometry;
    }

    private static StreamGeometry CreateStem(double offsetX, double offsetY, double scale)
    {
        Point P(double x, double y) => new(offsetX + x * scale, offsetY + y * scale);

        var geometry = new StreamGeometry { FillRule = FillRule.Nonzero };
        using (var context = geometry.Open())
        {
            context.BeginFigure(P(.545, .69), true, true);
            context.LineTo(P(.625, .67), true, true);
            context.BezierTo(P(.635, .75), P(.64, .81), P(.635, .86), true, true);
            context.BezierTo(P(.63, .885), P(.60, .89), P(.592, .868), true, true);
            context.BezierTo(P(.58, .81), P(.565, .75), P(.545, .69), true, true);
        }
        geometry.Freeze();
        return geometry;
    }

    private static void AddDot(StreamGeometryContext context, Point center, double radius)
    {
        var size = new Size(radius, radius);
        context.BeginFigure(new Point(center.X - radius, center.Y), true, true);
        context.ArcTo(new Point(center.X + radius, center.Y), size, 0, false, SweepDirection.Clockwise, true, false);
        context.ArcTo(new Point(center.X - radius, center.Y), size, 0, false, SweepDirection.Clockwise, true, false);
    }

    private static Brush[] CreateBrushes(Color[] colors, double opacity)
    {
        var brushes = new Brush[colors.Length];
        for (int i = 0; i < colors.Length; i++) brushes[i] = CreateBrush(colors[i], opacity);
        return brushes;
    }

    private static SolidColorBrush CreateBrush(Color color, double opacity)
    {
        var brush = new SolidColorBrush(color) { Opacity = opacity };
        brush.Freeze();
        return brush;
    }

    private static Pen CreatePen(Color color, double opacity, double thickness)
    {
        var pen = new Pen(CreateBrush(color, opacity), thickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round
        };
        pen.Freeze();
        return pen;
    }
}

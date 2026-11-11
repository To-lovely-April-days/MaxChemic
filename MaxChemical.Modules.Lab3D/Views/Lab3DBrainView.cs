using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;

namespace MaxChemical.Modules.Lab3D.Views;

public sealed class Lab3DBrainView : FrameworkElement
{
    private const int ParticleCount = 540;
    private const int CortexPointCount = 460;
    private readonly Vector3D[] _particles = new Vector3D[ParticleCount];
    private readonly List<Vector3D[]> _corticalFolds = [];
    private readonly List<(int From, int To)> _connections = [];
    private readonly Point[] _projected = new Point[ParticleCount];
    private readonly DispatcherTimer _timer = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(40) };
    private readonly Brush _pointBrush = CreateBrush(0x52, 0x61, 0x6B, 0.68);
    private readonly Brush _brightBrush = CreateBrush(0x8C, 0xA0, 0xA9, 0.82);
    private readonly Brush _signalSoftBrush = CreateBrush(0x1D, 0x9E, 0x75, 0.35);
    private readonly Brush _signalBrush = CreateBrush(0x1D, 0x9E, 0x75, 0.72);
    private readonly Brush _signalBrightBrush = CreateBrush(0x1D, 0x9E, 0x75, 0.96);
    private readonly Pen _linePen = CreatePen(0x52, 0x61, 0x6B, 0.2, 0.5);
    private readonly Pen _foldPen = CreatePen(0x52, 0x61, 0x6B, 0.28, 0.55);
    private readonly Pen _signalLinePen = CreatePen(0x1D, 0x9E, 0x75, 0.4, 0.65);
    private readonly Pen _signalTailPen = CreatePen(0x1D, 0x9E, 0x75, 0.5, 1);
    private readonly Pen _sideLinePen = CreatePen(0x1D, 0x9E, 0x75, 0.42, 0.7);
    private readonly Pen _focusPen = CreatePen(0x1D, 0x9E, 0x75, 0.85, 0.8);
    private double _time;
    private int _focusIndex;
    private bool _animationAllowed = true;

    public Lab3DBrainView()
    {
        var random = new Random(731);
        var lobes = new (double X, double Y, double Z, double RadiusX, double RadiusY, double RadiusZ)[]
        {
            (0.23, 0.29, 0.01, 0.31, 0.29, 0.33),
            (0.27, 0.01, 0.0, 0.33, 0.29, 0.35),
            (0.23, -0.25, -0.02, 0.29, 0.24, 0.30),
            (0.36, -0.23, 0.08, 0.22, 0.23, 0.27)
        };
        for (int i = 0; i < ParticleCount; i++)
        {
            double side = i % 2 == 0 ? -1 : 1;
            if (i < CortexPointCount)
            {
                var lobe = lobes[i / (CortexPointCount / lobes.Length)];
                var point = RandomUnitVector(random, 1);
                double fold = 1 + Math.Sin(point.Y * 11 + point.Z * 7) * 0.055
                    + Math.Sin(point.X * 13 + point.Y * 5) * 0.025;
                double x = side * lobe.X + point.X * lobe.RadiusX;
                x = side * Math.Max(0.055, Math.Abs(x)) * fold;
                double y = lobe.Y + point.Y * lobe.RadiusY + Math.Sin(x * 17 + point.Z * 11) * 0.018;
                double z = lobe.Z + point.Z * lobe.RadiusZ * fold;
                _particles[i] = new Vector3D(x, y, z);
            }
            else if (i < 510)
            {
                var point = RandomUnitVector(random, 1);
                _particles[i] = new Vector3D(
                    side * 0.14 + point.X * 0.21,
                    -0.51 + point.Y * 0.145 + Math.Sin(point.X * 20) * 0.008,
                    -0.045 + point.Z * 0.23);
            }
            else
            {
                var point = RandomUnitVector(random, 1);
                _particles[i] = new Vector3D(0.015 + point.X * 0.055, -0.69 + point.Y * 0.17, 0.04 + point.Z * 0.065);
            }
        }
        BuildCorticalFolds();
        for (int i = 0; i < ParticleCount; i++)
        {
            var nearest = Enumerable.Range(0, ParticleCount)
                .Where(index => index != i)
                .OrderBy(index => DistanceSquared(_particles[i], _particles[index]))
                .Take(3);
            foreach (int index in nearest)
            {
                if (index > i && DistanceSquared(_particles[i], _particles[index]) < 0.058)
                    _connections.Add((i, index));
            }
        }
        SnapsToDevicePixels = true;
        Loaded += (_, _) => UpdateAnimation();
        IsVisibleChanged += (_, _) => UpdateAnimation();
        Unloaded += (_, _) => _timer.Stop();
        _timer.Tick += (_, _) =>
        {
            _time += 0.04;
            InvalidateVisual();
        };
    }

    public void SetFocus(string? id)
    {
        int hash = 17;
        foreach (char character in id ?? string.Empty) hash = hash * 31 + character;
        _focusIndex = Math.Abs(hash) % ParticleCount;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        double width = ActualWidth;
        double height = ActualHeight;
        if (width < 10 || height < 10) return;

        double scale = Math.Min(width / 2.2, height / 1.85);
        double centerX = width / 2;
        double centerY = height * 0.45;
        double angle = 0.16 + Math.Sin(_time * 0.9) * 0.27;
        double cosine = Math.Cos(angle);
        double sine = Math.Sin(angle);
        double breathing = 1 + Math.Sin(_time * 0.85) * 0.012;
        double cycle = (_time % 7.5) / 7.5;
        double easedCycle = cycle * cycle * (3 - 2 * cycle);
        double waveCenter = 0.7 - easedCycle * 1.4;
        double envelope = Math.Sin(cycle * Math.PI);

        for (int i = 0; i < ParticleCount; i++)
        {
            var point = _particles[i];
            double x = (point.X * cosine + point.Z * sine) * breathing;
            double z = point.Z * cosine - point.X * sine;
            double y = point.Y * 0.96 - z * 0.13;
            _projected[i] = new Point(centerX + x * scale * 1.25, centerY - y * scale);
        }

        DrawCorticalFolds(drawingContext, cosine, sine, breathing, scale, centerX, centerY);
        DrawSideSignals(drawingContext, width, height, centerX, centerY);
        DrawNetwork(drawingContext, waveCenter, envelope);
        DrawNodes(drawingContext, waveCenter, envelope);
        DrawTravellingSignals(drawingContext, waveCenter, envelope);
    }

    private void DrawNetwork(DrawingContext drawingContext, double waveCenter, double envelope)
    {
        var network = new StreamGeometry();
        var active = new StreamGeometry();
        using (var context = network.Open())
        {
            foreach (var connection in _connections)
            {
                context.BeginFigure(_projected[connection.From], false, false);
                context.LineTo(_projected[connection.To], true, false);
            }
        }
        using (var context = active.Open())
        {
            foreach (var connection in _connections)
            {
                double x = (_particles[connection.From].X + _particles[connection.To].X) / 2;
                if (Activation(x, waveCenter, envelope) < 0.45) continue;
                context.BeginFigure(_projected[connection.From], false, false);
                context.LineTo(_projected[connection.To], true, false);
            }
        }
        network.Freeze();
        active.Freeze();
        drawingContext.DrawGeometry(null, _linePen, network);
        drawingContext.DrawGeometry(null, _signalLinePen, active);
    }

    private void DrawNodes(DrawingContext drawingContext, double waveCenter, double envelope)
    {
        for (int i = 0; i < ParticleCount; i++)
        {
            double activation = i % 3 == 0 ? Activation(_particles[i].X, waveCenter, envelope) : 0;
            bool focused = (i * 61 + 17) % ParticleCount == _focusIndex;
            var brush = activation > 0.58 ? _signalBrightBrush : activation > 0.17 ? _signalBrush :
                activation > 0.06 ? _signalSoftBrush : i % 5 == 0 ? _brightBrush : _pointBrush;
            double radius = (i % 5 == 0 ? 0.95 : 0.72) + activation * 0.65;
            drawingContext.DrawEllipse(focused ? _signalBrightBrush : brush, null, _projected[i], radius, radius);
            if (focused)
                drawingContext.DrawEllipse(null, _focusPen, _projected[i], 2.4 + Math.Sin(_time * 2) * 0.35, 2.4 + Math.Sin(_time * 2) * 0.35);
        }
    }

    private void DrawTravellingSignals(DrawingContext drawingContext, double waveCenter, double envelope)
    {
        for (int i = 0; i < 16; i++)
        {
            var connection = _connections[(i * 43) % _connections.Count];
            int from = connection.From;
            int to = connection.To;
            if (_particles[from].X < _particles[to].X) (from, to) = (to, from);
            double progress = (_time * 0.56 + i * 0.157) % 1;
            double worldX = _particles[from].X + (_particles[to].X - _particles[from].X) * progress;
            double activation = Activation(worldX, waveCenter, envelope);
            if (activation < 0.16) continue;
            var start = _projected[from];
            var end = _projected[to];
            double tail = Math.Max(0, progress - 0.08);
            drawingContext.DrawLine(_signalTailPen,
                new Point(start.X + (end.X - start.X) * tail, start.Y + (end.Y - start.Y) * tail),
                new Point(start.X + (end.X - start.X) * progress, start.Y + (end.Y - start.Y) * progress));
            drawingContext.DrawEllipse(_signalBrightBrush, null,
                new Point(start.X + (end.X - start.X) * progress, start.Y + (end.Y - start.Y) * progress),
                0.9 + activation * 0.4, 0.9 + activation * 0.4);
        }
    }

    private void DrawSideSignals(DrawingContext drawingContext, double width, double height, double centerX, double centerY)
    {
        var hub = GetConvergenceNode(centerX, centerY);
        for (int i = 0; i < 4; i++)
        {
            double labelY = height / 2 + (i - 1.5) * 15;
            var start = new Point(32, labelY);
            var first = new Point(start.X + 24, labelY);
            var second = new Point(hub.X - 14, hub.Y);
            DrawSideCurve(drawingContext, start, first, second, hub, _time * 0.3 + i * 0.19, false);
        }
        for (int i = 0; i < 3; i++)
        {
            double labelY = height / 2 + (i - 1) * 17;
            var end = new Point(width - 32, labelY);
            var first = new Point(hub.X + 14, hub.Y);
            var second = new Point(end.X - 24, labelY);
            DrawSideCurve(drawingContext, hub, first, second, end, _time * 0.3 + i * 0.23, true);
        }
    }

    private void BuildCorticalFolds()
    {
        foreach (double side in new[] { -1.0, 1.0 })
        {
            for (int band = 0; band < 7; band++)
            {
                var fold = new Vector3D[40];
                for (int i = 0; i < fold.Length; i++)
                {
                    double t = i / (fold.Length - 1.0);
                    double y = 0.52 - t * 0.97;
                    double envelope = Math.Sqrt(Math.Max(0.08, 1 - Math.Pow(y / 0.68, 2)));
                    double x = side * (0.08 + 0.44 * envelope * (0.82 + Math.Sin(t * 18 + band * 0.8) * 0.12) + band * 0.004);
                    double z = (band - 3) * 0.105 * envelope + Math.Sin(t * 22 + band * 0.9) * 0.035;
                    fold[i] = new Vector3D(x, y, z);
                }
                _corticalFolds.Add(fold);
            }
        }
        _corticalFolds.Add(Enumerable.Range(0, 40)
            .Select(i => new Vector3D(0, 0.51 - i / 39.0 * 0.91, 0.025)).ToArray());
    }

    private void DrawCorticalFolds(DrawingContext drawingContext, double cosine, double sine,
        double breathing, double scale, double centerX, double centerY)
    {
        Point Project(Vector3D point)
        {
            double x = (point.X * cosine + point.Z * sine) * breathing;
            double z = point.Z * cosine - point.X * sine;
            double y = point.Y * 0.96 - z * 0.13;
            return new Point(centerX + x * scale * 1.25, centerY - y * scale);
        }

        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            foreach (var fold in _corticalFolds)
            {
                context.BeginFigure(Project(fold[0]), false, false);
                for (int i = 1; i < fold.Length; i++) context.LineTo(Project(fold[i]), true, false);
            }
        }
        geometry.Freeze();
        drawingContext.DrawGeometry(null, _foldPen, geometry);
    }

    private Point GetConvergenceNode(double centerX, double centerY)
    {
        int index = Enumerable.Range(0, CortexPointCount)
            .OrderBy(i => Math.Pow(_projected[i].X - centerX, 2) + Math.Pow(_projected[i].Y - centerY, 2))
            .First();
        return _projected[index];
    }

    private void DrawSideCurve(DrawingContext drawingContext, Point start, Point first, Point second,
        Point end, double progress, bool outgoing)
    {
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(start, false, false);
            context.BezierTo(first, second, end, true, false);
        }
        geometry.Freeze();
        drawingContext.DrawGeometry(null, _sideLinePen, geometry);
        double position = progress % 1;
        var pulse = Bezier(start, first, second, end, position);
        drawingContext.DrawEllipse(_signalBrush, null, pulse, 1.5, 1.5);
        var endpoint = outgoing ? end : start;
        double radius = 1.1 + Math.Sin(position * Math.PI) * 0.65;
        drawingContext.DrawEllipse(_signalBrightBrush, null, endpoint, radius, radius);
    }

    internal void SetAnimationActive(bool active)
    {
        _animationAllowed = active;
        UpdateAnimation();
    }

    private void UpdateAnimation()
    {
        if (_animationAllowed && IsVisible) _timer.Start();
        else _timer.Stop();
    }

    private static Vector3D RandomUnitVector(Random random, double radius)
    {
        double theta = random.NextDouble() * Math.PI * 2;
        double latitude = random.NextDouble() * 2 - 1;
        double ring = Math.Sqrt(1 - latitude * latitude);
        double length = Math.Pow(random.NextDouble(), 1 / 3.0) * radius;
        return new Vector3D(Math.Cos(theta) * ring * length, latitude * length, Math.Sin(theta) * ring * length);
    }

    private static Point Bezier(Point start, Point first, Point second, Point end, double progress)
    {
        double inverse = 1 - progress;
        return new Point(
            inverse * inverse * inverse * start.X + 3 * inverse * inverse * progress * first.X
                + 3 * inverse * progress * progress * second.X + progress * progress * progress * end.X,
            inverse * inverse * inverse * start.Y + 3 * inverse * inverse * progress * first.Y
                + 3 * inverse * progress * progress * second.Y + progress * progress * progress * end.Y);
    }

    private static double Activation(double x, double center, double envelope) =>
        Math.Exp(-Math.Pow((x - center) / 0.16, 2)) * envelope;

    private static double DistanceSquared(Vector3D first, Vector3D second) => (first - second).LengthSquared;

    private static SolidColorBrush CreateBrush(byte red, byte green, byte blue, double opacity)
    {
        var brush = new SolidColorBrush(Color.FromRgb(red, green, blue)) { Opacity = opacity };
        brush.Freeze();
        return brush;
    }

    private static Pen CreatePen(byte red, byte green, byte blue, double opacity, double thickness)
    {
        var pen = new Pen(CreateBrush(red, green, blue, opacity), thickness);
        pen.Freeze();
        return pen;
    }
}

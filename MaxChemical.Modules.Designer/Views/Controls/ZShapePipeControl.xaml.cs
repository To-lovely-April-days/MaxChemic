using MaxChemical.Modules.Designer.Service;
using MaxChemical.Modules.Designer.Views.Controls.Service;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace MaxChemical.Modules.Designer.Views.Controls
{
    /// <summary>
    /// Z形管道控件 — 实现 IPipeProperties 以支持统一属性对话框
    /// </summary>
    public partial class ZShapePipeControl : UserControl, INotifyPropertyChanged, IPipeSnapPoints, IPipeProperties
    {
        #region 事件定义

        public event PropertyChangedEventHandler PropertyChanged;
        public event EventHandler<ZPipeDragEventArgs> PipeDragStarted;
        public event EventHandler<ZPipeDragEventArgs> PipeDragging;
        public event EventHandler<ZPipeDragEventArgs> PipeDragCompleted;
        public event EventHandler<ZPipeResizeEventArgs> PipeResizeStarted;
        public event EventHandler<ZPipeResizeEventArgs> PipeResizing;
        public event EventHandler<ZPipeResizeEventArgs> PipeResizeCompleted;
        public event EventHandler<ZPipeRotateEventArgs> PipeRotateStarted;
        public event EventHandler<ZPipeRotateEventArgs> PipeRotating;
        public event EventHandler<ZPipeRotateEventArgs> PipeRotateCompleted;
        public event EventHandler<string> PipeSelected;
        public event EventHandler<string> PipeDeselected;
        public event EventHandler<string> PipeDoubleClicked;

        #endregion

        #region 依赖属性

        public static readonly DependencyProperty IsSelectedProperty =
            DependencyProperty.Register(nameof(IsSelected), typeof(bool), typeof(ZShapePipeControl),
                new PropertyMetadata(false, OnIsSelectedChanged));
        public bool IsSelected { get => (bool)GetValue(IsSelectedProperty); set => SetValue(IsSelectedProperty, value); }
        private static void OnIsSelectedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is ZShapePipeControl c) { c.UpdateSelectionVisual((bool)e.NewValue); c.OnPropertyChanged(nameof(IsSelected)); }
        }

        public static readonly DependencyProperty PipeColorProperty =
            DependencyProperty.Register(nameof(PipeColor), typeof(Color), typeof(ZShapePipeControl),
                new PropertyMetadata(Color.FromRgb(0x72, 0x71, 0x71), OnPipeColorChanged));
        public Color PipeColor { get => (Color)GetValue(PipeColorProperty); set => SetValue(PipeColorProperty, value); }
        private static void OnPipeColorChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        { if (d is ZShapePipeControl c) c.ApplyPipeColor(); }

        public static readonly DependencyProperty FlangeColorProperty =
            DependencyProperty.Register(nameof(FlangeColor), typeof(Color), typeof(ZShapePipeControl),
                new PropertyMetadata(Color.FromRgb(0xD9, 0xD9, 0xD9), OnFlangeColorChanged));
        public Color FlangeColor { get => (Color)GetValue(FlangeColorProperty); set => SetValue(FlangeColorProperty, value); }
        private static void OnFlangeColorChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        { if (d is ZShapePipeControl c) c.ApplyFlangeColor(); }

        public static readonly DependencyProperty FluidColorProperty =
            DependencyProperty.Register(nameof(FluidColor), typeof(Color), typeof(ZShapePipeControl),
                new PropertyMetadata(Color.FromRgb(0x1E, 0xC1, 0xF4), OnFluidColorChanged));
        public Color FluidColor { get => (Color)GetValue(FluidColorProperty); set => SetValue(FluidColorProperty, value); }
        private static void OnFluidColorChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        { if (d is ZShapePipeControl c) c.ApplyFluidColor(); }

        public static readonly DependencyProperty FluidOpacityProperty =
            DependencyProperty.Register(nameof(FluidOpacity), typeof(double), typeof(ZShapePipeControl),
                new PropertyMetadata(0.5, OnFluidOpacityChanged));
        public double FluidOpacity { get => (double)GetValue(FluidOpacityProperty); set => SetValue(FluidOpacityProperty, value); }
        private static void OnFluidOpacityChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        { if (d is ZShapePipeControl c) c.ApplyFluidOpacity(); }

        public static readonly DependencyProperty IsFluidVisibleProperty =
            DependencyProperty.Register(nameof(IsFluidVisible), typeof(bool), typeof(ZShapePipeControl),
                new PropertyMetadata(false, OnIsFluidVisibleChanged));
        public bool IsFluidVisible { get => (bool)GetValue(IsFluidVisibleProperty); set => SetValue(IsFluidVisibleProperty, value); }
        private static void OnIsFluidVisibleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        { if (d is ZShapePipeControl c) c.ApplyFluidVisibility(); }

        public static readonly DependencyProperty UseDefaultStyleProperty =
            DependencyProperty.Register(nameof(UseDefaultStyle), typeof(bool), typeof(ZShapePipeControl),
                new PropertyMetadata(false, OnUseDefaultStyleChanged));
        public bool UseDefaultStyle { get => (bool)GetValue(UseDefaultStyleProperty); set => SetValue(UseDefaultStyleProperty, value); }
        private static void OnUseDefaultStyleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        { if (d is ZShapePipeControl c) c.ApplyDefaultStyleSwitch(); }

        public static readonly DependencyProperty IsFlowingProperty =
            DependencyProperty.Register(nameof(IsFlowing), typeof(bool), typeof(ZShapePipeControl),
                new PropertyMetadata(false, OnIsFlowingChanged));
        public bool IsFlowing { get => (bool)GetValue(IsFlowingProperty); set => SetValue(IsFlowingProperty, value); }
        private static void OnIsFlowingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        { if (d is ZShapePipeControl c) c.ApplyFlowAnimation(); }

        public static readonly DependencyProperty PipeFlowDirProperty =
            DependencyProperty.Register(nameof(PipeFlowDir), typeof(PipeFlowDirection), typeof(ZShapePipeControl),
                new PropertyMetadata(PipeFlowDirection.LeftToRight, OnPipeFlowDirChanged));
        public PipeFlowDirection PipeFlowDir { get => (PipeFlowDirection)GetValue(PipeFlowDirProperty); set => SetValue(PipeFlowDirProperty, value); }
        private static void OnPipeFlowDirChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        { if (d is ZShapePipeControl c) c.ApplyFlowAnimation(); }

        public static readonly DependencyProperty FlowSpeedProperty =
            DependencyProperty.Register(nameof(FlowSpeed), typeof(double), typeof(ZShapePipeControl),
                new PropertyMetadata(1.0, OnFlowSpeedChanged));
        public double FlowSpeed { get => (double)GetValue(FlowSpeedProperty); set => SetValue(FlowSpeedProperty, value); }
        private static void OnFlowSpeedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        { if (d is ZShapePipeControl c) c.ApplyFlowAnimation(); }

        #endregion

        #region 私有字段

        private bool _isDraggingPipe;
        private bool _isResizing;
        private bool _isRotating;
        private ZAnchorPosition? _resizingAnchor;
        private Point _dragStartPoint;
        private Point _originalPosition;
        private ZPipeSize _originalSize;
        private double _originalRotation;
        private Point _fixedWorldPoint;

        private const double MinHorizontalLength = 10;
        private const double MaxHorizontalLength = 10000;
        private const double MinVerticalOffset = 15;
        private const double MaxVerticalOffset = 10000;
        private const double MinPipeWidth = 4;
        private const double MaxPipeWidth = 10000;
        private const double FlangeWidth = 2.54;
        private const double FlangeOffset = 0.35;
        private const double FlangeSpacing = 2.0;
        private const double FLANGE_EXTENSION = 5.8;

        private double _topHorizontalLength = 15;
        private double _bottomHorizontalLength = 15;
        private double _verticalOffset = 20;
        private double _pipeWidth = 20;
        private double _rotationAngle;

        private RotateTransform _rotateTransform;
        private TransformGroup _transformGroup;
        private DispatcherTimer _rotationLabelTimer;

        private TranslateTransform _topHorizFlowTransform;
        private TranslateTransform _diagFlowTransform;
        private TranslateTransform _bottomHorizFlowTransform;
        private DrawingBrush _topHorizPatternBrush;
        private DrawingBrush _diagPatternBrush;
        private DrawingBrush _bottomHorizPatternBrush;

        private DoubleAnimation _cyberMainAnim;
        private DoubleAnimation _cyberParticleAnim;
        private DoubleAnimation _cyberSparkAnim;
        private double _cyberMainPeriod = 120;
        private double _cyberParticlePeriod = 58;
        private double _cyberSparkPeriod = 84;

        private const double PATTERN_W = 172.0;
        private const double PATTERN_H = 72.0;

        private static readonly (double X, double Y, double R)[] BubbleData = new[]
        {
            (21.0, 15.0, 8.0),   (150.0, 15.0, 8.0),  (113.0, 16.0, 8.0),
            (34.0, 58.0, 8.0),   (155.0, 58.0, 8.0),
            (33.0, 26.0, 5.0),   (162.0, 26.0, 5.0),   (5.0, 37.0, 5.0),
            (94.0, 8.0, 4.0),    (72.0, 60.0, 4.0),
            (112.0, 37.0, 5.0),  (59.0, 39.0, 5.0),    (115.0, 62.0, 5.0),
            (139.0, 42.0, 5.0),  (76.0, 21.0, 5.0),
            (126.5, 50.5, 2.5),  (169.5, 46.5, 2.5),   (57.5, 9.5, 2.5),
            (96.5, 35.5, 2.5),   (91.5, 23.5, 2.5),    (22.5, 40.5, 2.5),
            (124.5, 23.5, 2.5),  (86.5, 47.5, 2.5),    (51.5, 21.5, 2.5),
            (48.5, 51.5, 2.5),
            (14.0, 64.0, 4.0),   (135.0, 64.0, 4.0),
            (95.5, 58.5, 9.5)
        };

        #endregion

        #region CLR 属性

        public string PipeId { get; set; }

        public double TopHorizontalLength
        {
            get => _topHorizontalLength;
            set
            {
                var nv = Math.Max(MinHorizontalLength, Math.Min(MaxHorizontalLength, value));
                if (Math.Abs(_topHorizontalLength - nv) > 0.01)
                { _topHorizontalLength = nv; UpdatePipeGeometry(); OnPropertyChanged(nameof(TopHorizontalLength)); }
            }
        }

        public double BottomHorizontalLength
        {
            get => _bottomHorizontalLength;
            set
            {
                var nv = Math.Max(MinHorizontalLength, Math.Min(MaxHorizontalLength, value));
                if (Math.Abs(_bottomHorizontalLength - nv) > 0.01)
                { _bottomHorizontalLength = nv; UpdatePipeGeometry(); OnPropertyChanged(nameof(BottomHorizontalLength)); }
            }
        }

        public double VerticalOffset
        {
            get => _verticalOffset;
            set
            {
                var nv = Math.Max(MinVerticalOffset, Math.Min(MaxVerticalOffset, value));
                if (Math.Abs(_verticalOffset - nv) > 0.01)
                { _verticalOffset = nv; UpdatePipeGeometry(); OnPropertyChanged(nameof(VerticalOffset)); }
            }
        }

        public double PipeWidth
        {
            get => _pipeWidth;
            set
            {
                var nv = Math.Max(MinPipeWidth, Math.Min(MaxPipeWidth, value));
                if (Math.Abs(_pipeWidth - nv) > 0.01)
                { _pipeWidth = nv; UpdatePipeGeometry(); OnPropertyChanged(nameof(PipeWidth)); }
            }
        }

        public double RotationAngle
        {
            get => _rotationAngle;
            set
            {
                _rotationAngle = value % 360;
                if (_rotationAngle < 0) _rotationAngle += 360;
                UpdateRotation();
                OnPropertyChanged(nameof(RotationAngle));
            }
        }

        #endregion

        #region 构造

        public ZShapePipeControl()
        {
            InitializeComponent();
            PipeId = Guid.NewGuid().ToString();
            DataContext = this;

            _rotateTransform = new RotateTransform(0);
            _transformGroup = new TransformGroup();
            _transformGroup.Children.Add(_rotateTransform);
            RenderTransform = _transformGroup;
            RenderTransformOrigin = new Point(0.5, 0.5);

            InitializeAppearance();

            if (!DesignerProperties.GetIsInDesignMode(this))
            {
                _rotationLabelTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                _rotationLabelTimer.Tick += (s, e) => { RotationLabel.Visibility = Visibility.Collapsed; _rotationLabelTimer.Stop(); };

                MouseEnter += OnPipeMouseEnter;
                MouseLeave += OnPipeMouseLeave;
                MouseDown += OnControlMouseDown;
                MouseMove += OnControlMouseMove;
                MouseUp += OnControlMouseUp;
                MouseLeave += OnControlMouseLeave;
                SetupAnchorEvents();
            }

            Loaded += (s, e) => { UpdatePipeGeometry(); ApplyFlowAnimation(); };

            RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
            RenderOptions.SetEdgeMode(this, EdgeMode.Unspecified);
            RenderOptions.SetCachingHint(this, CachingHint.Cache);
            RenderOptions.SetCacheInvalidationThresholdMinimum(this, 0.5);
            RenderOptions.SetCacheInvalidationThresholdMaximum(this, 2.0);

            CacheMode = new BitmapCache { RenderAtScale = 1.0, SnapsToDevicePixels = true, EnableClearType = false };
            SnapsToDevicePixels = true;
            UseLayoutRounding = true;
        }

        private void InitializeAppearance()
        {
            ApplyPipeColor();
            ApplyFlangeColor();
            ApplyFluidColor();
            RebuildFluidPatternBrushes();
            ApplyFluidVisibility();
            ApplyDefaultStyleSwitch();
        }

        #endregion

        #region 管体外观

        private void ApplyPipeColor()
        {
            if (TopHorizontalPipe == null) return;
            var c = PipeColor;

            var hGrad = MakeCylinderGradient(c, new Point(0, 0), new Point(0, 1));
            TopHorizontalPipe.Fill = hGrad;
            BottomHorizontalPipe.Fill = hGrad;

            var vGrad = MakeCylinderGradient(c, new Point(0, 0), new Point(1, 0));
            DiagonalPipe.Fill = vGrad;

            ApplyElbowGradient(TopElbowPath, c, new Point(1, 0), new Point(0, 1));
            ApplyElbowGradient(BottomElbowPath, c, new Point(0, 1), new Point(1, 0));
            ApplyElbowHighlight(TopElbowHighlight, c, new Point(1, 0), new Point(0, 1));
            ApplyElbowHighlight(BottomElbowHighlight, c, new Point(0, 1), new Point(1, 0));

            var strokeBrush = new SolidColorBrush(c);
            strokeBrush.Freeze();
            TopElbowPath.Stroke = strokeBrush;
            BottomElbowPath.Stroke = strokeBrush;
            LeftFlangeBorder.Stroke = strokeBrush;
            RightFlangeBorder.Stroke = strokeBrush;
            LeftConnection.Fill = strokeBrush;
            RightConnection.Fill = strokeBrush;

            RebuildFluidPatternBrushes();
        }

        private LinearGradientBrush MakeCylinderGradient(Color c, Point start, Point end)
        {
            var g = new LinearGradientBrush { StartPoint = start, EndPoint = end };
            g.GradientStops.Add(new GradientStop(c, 0));
            g.GradientStops.Add(new GradientStop(Color.FromArgb(0x59, c.R, c.G, c.B), 0.264));
            g.GradientStops.Add(new GradientStop(Color.FromArgb(0x1A, c.R, c.G, c.B), 0.418));
            g.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 0.498));
            g.GradientStops.Add(new GradientStop(Color.FromArgb(0x1A, c.R, c.G, c.B), 0.581));
            g.GradientStops.Add(new GradientStop(Color.FromArgb(0x59, c.R, c.G, c.B), 0.719));
            g.GradientStops.Add(new GradientStop(c, 1));
            g.Freeze();
            return g;
        }

        private void ApplyElbowGradient(Path elbowPath, Color c, Point start, Point end)
        {
            var darkC = DarkenColor(c, 25);
            var lightC = LightenColor(c, 25);
            var midDark = DarkenColor(c, 12);
            var midLight = LightenColor(c, 8);

            var g = new LinearGradientBrush { StartPoint = start, EndPoint = end };
            g.GradientStops.Add(new GradientStop(darkC, 0));
            g.GradientStops.Add(new GradientStop(midDark, 0.12));
            g.GradientStops.Add(new GradientStop(c, 0.22));
            g.GradientStops.Add(new GradientStop(Color.FromArgb(0x59, c.R, c.G, c.B), 0.32));
            g.GradientStops.Add(new GradientStop(Color.FromArgb(0x40, c.R, c.G, c.B), 0.40));
            g.GradientStops.Add(new GradientStop(Color.FromArgb(0x1A, c.R, c.G, c.B), 0.46));
            g.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 0.50));
            g.GradientStops.Add(new GradientStop(Color.FromArgb(0x1A, c.R, c.G, c.B), 0.54));
            g.GradientStops.Add(new GradientStop(Color.FromArgb(0x40, c.R, c.G, c.B), 0.60));
            g.GradientStops.Add(new GradientStop(Color.FromArgb(0x59, c.R, c.G, c.B), 0.68));
            g.GradientStops.Add(new GradientStop(c, 0.78));
            g.GradientStops.Add(new GradientStop(midLight, 0.88));
            g.GradientStops.Add(new GradientStop(lightC, 1));
            g.Freeze();
            elbowPath.Fill = g;
        }

        private void ApplyElbowHighlight(Path hlPath, Color c, Point start, Point end)
        {
            var g = new LinearGradientBrush { StartPoint = start, EndPoint = end };
            g.GradientStops.Add(new GradientStop(Colors.Transparent, 0));
            g.GradientStops.Add(new GradientStop(LightenColor(c, 80), 0.35));
            g.GradientStops.Add(new GradientStop(LightenColor(c, 110), 0.65));
            g.GradientStops.Add(new GradientStop(LightenColor(c, 120), 1));
            g.Freeze();
            hlPath.Fill = g;
        }

        private void ApplyFlangeColor()
        {
            if (LeftFlangeBase == null) return;
            var c = FlangeColor;
            var baseBrush = new SolidColorBrush(c);
            baseBrush.Freeze();
            LeftFlangeBase.Fill = baseBrush;
            RightFlangeBase.Fill = baseBrush;

            var midColor = BlendColors(c, Colors.Gray, 0.4);

            var leftGrad = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
            leftGrad.GradientStops.Add(new GradientStop(c, 0));
            leftGrad.GradientStops.Add(new GradientStop(midColor, 0.5));
            leftGrad.GradientStops.Add(new GradientStop(c, 1));
            leftGrad.Freeze();
            LeftFlangeGradient.Fill = leftGrad;

            var rightGrad = new LinearGradientBrush { StartPoint = new Point(1, 0.5), EndPoint = new Point(0, 0.5) };
            rightGrad.GradientStops.Add(new GradientStop(c, 0));
            rightGrad.GradientStops.Add(new GradientStop(midColor, 0.5));
            rightGrad.GradientStops.Add(new GradientStop(c, 1));
            rightGrad.Freeze();
            RightFlangeGradient.Fill = rightGrad;
        }

        #endregion

        #region 流体颜色与可见性

        private void ApplyFluidColor()
        {
            if (TopHorizontalFluidBackground == null) return;
            var brush = new SolidColorBrush(FluidColor);
            TopHorizontalFluidBackground.Fill = brush;
            DiagonalFluidBackground.Fill = brush;
            BottomHorizontalFluidBackground.Fill = brush;
            TopElbowFluidBackground.Fill = brush;
            BottomElbowFluidBackground.Fill = brush;
            ApplyFluidOpacity();

            if (UseDefaultStyle && CyberMainStream != null)
                ApplyCyberStreamColors();
        }

        private void ApplyFluidOpacity()
        {
            if (TopHorizontalFluidBackground == null) return;
            TopHorizontalFluidBackground.Opacity = FluidOpacity;
            DiagonalFluidBackground.Opacity = FluidOpacity;
            BottomHorizontalFluidBackground.Opacity = FluidOpacity;
            TopElbowFluidBackground.Opacity = FluidOpacity;
            BottomElbowFluidBackground.Opacity = FluidOpacity;
        }

        private void ApplyFluidVisibility()
        {
            if (TopHorizontalFluidBackground == null) return;

            if (UseDefaultStyle)
            {
                TopHorizontalFluidBackground.Visibility = Visibility.Collapsed;
                TopHorizontalFluidPattern.Visibility = Visibility.Collapsed;
                DiagonalFluidBackground.Visibility = Visibility.Collapsed;
                DiagonalFluidPattern.Visibility = Visibility.Collapsed;
                BottomHorizontalFluidBackground.Visibility = Visibility.Collapsed;
                BottomHorizontalFluidPattern.Visibility = Visibility.Collapsed;
                TopElbowFluidBackground.Visibility = Visibility.Collapsed;
                BottomElbowFluidBackground.Visibility = Visibility.Collapsed;
                return;
            }

            var vis = IsFluidVisible ? Visibility.Visible : Visibility.Collapsed;
            TopHorizontalFluidBackground.Visibility = vis;
            TopHorizontalFluidPattern.Visibility = vis;
            DiagonalFluidBackground.Visibility = vis;
            DiagonalFluidPattern.Visibility = vis;
            BottomHorizontalFluidBackground.Visibility = vis;
            BottomHorizontalFluidPattern.Visibility = vis;
            TopElbowFluidBackground.Visibility = vis;
            BottomElbowFluidBackground.Visibility = vis;

            if (IsFluidVisible && IsFlowing) StartFlowAnimation(); else StopFlowAnimation();
        }

        private void RebuildFluidPatternBrushes()
        {
            if (TopHorizontalFluidPattern == null) return;
            var pipeC = PipeColor;

            var hDrawing = CreateBubbleDrawing(pipeC, false);
            _topHorizFlowTransform = new TranslateTransform(0, 0);
            _topHorizPatternBrush = new DrawingBrush(hDrawing) { TileMode = TileMode.Tile, Viewbox = new Rect(0, 0, PATTERN_W, PATTERN_H), ViewboxUnits = BrushMappingMode.Absolute, ViewportUnits = BrushMappingMode.Absolute, Transform = _topHorizFlowTransform };
            TopHorizontalFluidPattern.Fill = _topHorizPatternBrush;

            _bottomHorizFlowTransform = new TranslateTransform(0, 0);
            _bottomHorizPatternBrush = new DrawingBrush(hDrawing) { TileMode = TileMode.Tile, Viewbox = new Rect(0, 0, PATTERN_W, PATTERN_H), ViewboxUnits = BrushMappingMode.Absolute, ViewportUnits = BrushMappingMode.Absolute, Transform = _bottomHorizFlowTransform };
            BottomHorizontalFluidPattern.Fill = _bottomHorizPatternBrush;

            var vDrawing = CreateBubbleDrawing(pipeC, true);
            _diagFlowTransform = new TranslateTransform(0, 0);
            _diagPatternBrush = new DrawingBrush(vDrawing) { TileMode = TileMode.Tile, Viewbox = new Rect(0, 0, PATTERN_W, PATTERN_H), ViewboxUnits = BrushMappingMode.Absolute, ViewportUnits = BrushMappingMode.Absolute, Transform = _diagFlowTransform };
            DiagonalFluidPattern.Fill = _diagPatternBrush;

            UpdateFluidPatternViewports();
            if (IsFlowing && IsFluidVisible && !UseDefaultStyle) StartFlowAnimation();
        }

        private DrawingGroup CreateBubbleDrawing(Color pipeC, bool isVertical)
        {
            var group = new DrawingGroup();
            foreach (var (x, y, r) in BubbleData)
            {
                var radial = new RadialGradientBrush { GradientOrigin = new Point(0.3, 0.3), Center = new Point(0.5, 0.5) };
                radial.GradientStops.Add(new GradientStop(Color.FromArgb(102, 255, 255, 255), 0));
                radial.GradientStops.Add(new GradientStop(Color.FromArgb(38, 0, 0, 0), 1));
                radial.Freeze();
                group.Children.Add(new GeometryDrawing(radial, null, new EllipseGeometry(new Point(x, y), r, r)));
            }

            // ★ 去掉边带 — 之前边带占 11% 上 + 11% 下,管子细的时候把气泡挤到中间一条,
            //   导致设了管道颜色后视觉上气泡跟管壁融成一片看不出动静
            return group;
        }

        private void UpdateFluidPatternViewports()
        {
            // ★ 气泡尺寸放大 1.5 倍 — 全局视图下也能清晰看见气泡流动
            const double BUBBLE_SCALE = 1.5;
            double tileH = _pipeWidth * BUBBLE_SCALE;
            double tileW = tileH * (PATTERN_W / PATTERN_H);
            if (_topHorizPatternBrush != null) _topHorizPatternBrush.Viewport = new Rect(0, 0, tileW, tileH);
            if (_bottomHorizPatternBrush != null) _bottomHorizPatternBrush.Viewport = new Rect(0, 0, tileW, tileH);
            if (_diagPatternBrush != null) _diagPatternBrush.Viewport = new Rect(0, 0, tileW, tileH);
        }

        #endregion

        #region 流体动画（气泡）

        private void ApplyFlowAnimation()
        {
            if (UseDefaultStyle)
            {
                if (IsFlowing) StartCyberAnimation(); else StopCyberAnimation();
                return;
            }
            if (IsFlowing && IsFluidVisible) StartFlowAnimation(); else StopFlowAnimation();
        }

        private void StartFlowAnimation()
        {
            if (_topHorizFlowTransform == null) return;
            StopFlowAnimation();
            // ★ 关键修复:滚动距离用 PATTERN_W (=172) 而不是 tileW (≈48)
            //   跟原版 ThingsBoard 一致 — 流动感大幅增强
            // ★ 速度调慢:基础时长 2000ms 而不是 1000ms
            double baseDurationMs = 2000.0 / Math.Max(0.1, FlowSpeed);
            bool leftToRight = PipeFlowDir == PipeFlowDirection.LeftToRight;

            double topTo = leftToRight ? PATTERN_W : -PATTERN_W;
            _topHorizFlowTransform.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation { From = 0, To = topTo, Duration = TimeSpan.FromMilliseconds(baseDurationMs), RepeatBehavior = RepeatBehavior.Forever });

            double vTo = leftToRight ? PATTERN_W : -PATTERN_W;
            _diagFlowTransform.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation { From = 0, To = vTo, Duration = TimeSpan.FromMilliseconds(baseDurationMs), RepeatBehavior = RepeatBehavior.Forever });

            double bottomTo = leftToRight ? PATTERN_W : -PATTERN_W;
            _bottomHorizFlowTransform.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation { From = 0, To = bottomTo, Duration = TimeSpan.FromMilliseconds(baseDurationMs), RepeatBehavior = RepeatBehavior.Forever });
        }

        private void StopFlowAnimation()
        {
            if (_topHorizFlowTransform != null) { _topHorizFlowTransform.BeginAnimation(TranslateTransform.XProperty, null); _topHorizFlowTransform.X = 0; }
            if (_diagFlowTransform != null) { _diagFlowTransform.BeginAnimation(TranslateTransform.YProperty, null); _diagFlowTransform.Y = 0; }
            if (_bottomHorizFlowTransform != null) { _bottomHorizFlowTransform.BeginAnimation(TranslateTransform.XProperty, null); _bottomHorizFlowTransform.X = 0; }
        }

        #endregion

        #region 赛博流光

        private void ApplyDefaultStyleSwitch()
        {
            if (CyberMainStream == null) return;

            if (UseDefaultStyle)
            {
                if (TopHorizontalPipe != null) TopHorizontalPipe.Visibility = Visibility.Collapsed;
                if (DiagonalPipe != null) DiagonalPipe.Visibility = Visibility.Collapsed;
                if (BottomHorizontalPipe != null) BottomHorizontalPipe.Visibility = Visibility.Collapsed;
                if (TopElbowPath != null) TopElbowPath.Visibility = Visibility.Collapsed;
                if (BottomElbowPath != null) BottomElbowPath.Visibility = Visibility.Collapsed;
                if (TopElbowHighlight != null) TopElbowHighlight.Visibility = Visibility.Collapsed;
                if (BottomElbowHighlight != null) BottomElbowHighlight.Visibility = Visibility.Collapsed;
                // ★ Background 层不隐藏,改成透明 fill —— 保留命中(双击/拖拽)
                if (TopHorizontalPipeBackground != null) { TopHorizontalPipeBackground.Fill = Brushes.Transparent; TopHorizontalPipeBackground.Visibility = Visibility.Visible; }
                if (DiagonalPipeBackground != null) { DiagonalPipeBackground.Fill = Brushes.Transparent; DiagonalPipeBackground.Visibility = Visibility.Visible; }
                if (BottomHorizontalPipeBackground != null) { BottomHorizontalPipeBackground.Fill = Brushes.Transparent; BottomHorizontalPipeBackground.Visibility = Visibility.Visible; }
                if (TopElbowBackground != null) { TopElbowBackground.Fill = Brushes.Transparent; TopElbowBackground.Visibility = Visibility.Visible; }
                if (BottomElbowBackground != null) { BottomElbowBackground.Fill = Brushes.Transparent; BottomElbowBackground.Visibility = Visibility.Visible; }

                BuildCyberLayers();

                CyberTopHorizMetal.Visibility = Visibility.Visible;
                CyberTopElbowMetal.Visibility = Visibility.Visible;
                CyberDiagMetal.Visibility = Visibility.Visible;
                CyberBottomElbowMetal.Visibility = Visibility.Visible;
                CyberBottomHorizMetal.Visibility = Visibility.Visible;
                CyberTopHorizDark.Visibility = Visibility.Visible;
                CyberTopElbowDark.Visibility = Visibility.Visible;
                CyberDiagDark.Visibility = Visibility.Visible;
                CyberBottomElbowDark.Visibility = Visibility.Visible;
                CyberBottomHorizDark.Visibility = Visibility.Visible;
                CyberMainStream.Visibility = Visibility.Visible;
                CyberParticleStream.Visibility = Visibility.Visible;
                CyberSparkStream.Visibility = Visibility.Visible;

                if (IsFlowing) StartCyberAnimation(); else StopCyberAnimation();
                StopFlowAnimation();
                ApplyFluidVisibility();
            }
            else
            {
                CyberTopHorizMetal.Visibility = Visibility.Collapsed;
                CyberTopElbowMetal.Visibility = Visibility.Collapsed;
                CyberDiagMetal.Visibility = Visibility.Collapsed;
                CyberBottomElbowMetal.Visibility = Visibility.Collapsed;
                CyberBottomHorizMetal.Visibility = Visibility.Collapsed;
                CyberTopHorizDark.Visibility = Visibility.Collapsed;
                CyberTopElbowDark.Visibility = Visibility.Collapsed;
                CyberDiagDark.Visibility = Visibility.Collapsed;
                CyberBottomElbowDark.Visibility = Visibility.Collapsed;
                CyberBottomHorizDark.Visibility = Visibility.Collapsed;
                CyberMainStream.Visibility = Visibility.Collapsed;
                CyberParticleStream.Visibility = Visibility.Collapsed;
                CyberSparkStream.Visibility = Visibility.Collapsed;
                StopCyberAnimation();

                if (TopHorizontalPipe != null) TopHorizontalPipe.Visibility = Visibility.Visible;
                if (DiagonalPipe != null) DiagonalPipe.Visibility = Visibility.Visible;
                if (BottomHorizontalPipe != null) BottomHorizontalPipe.Visibility = Visibility.Visible;
                if (TopElbowPath != null) TopElbowPath.Visibility = Visibility.Visible;
                if (BottomElbowPath != null) BottomElbowPath.Visibility = Visibility.Visible;
                if (TopElbowHighlight != null) TopElbowHighlight.Visibility = Visibility.Visible;
                if (BottomElbowHighlight != null) BottomElbowHighlight.Visibility = Visibility.Visible;
                // ★ 还原 Background 白底
                if (TopHorizontalPipeBackground != null) { TopHorizontalPipeBackground.Fill = Brushes.White; TopHorizontalPipeBackground.Visibility = Visibility.Visible; }
                if (DiagonalPipeBackground != null) { DiagonalPipeBackground.Fill = Brushes.White; DiagonalPipeBackground.Visibility = Visibility.Visible; }
                if (BottomHorizontalPipeBackground != null) { BottomHorizontalPipeBackground.Fill = Brushes.White; BottomHorizontalPipeBackground.Visibility = Visibility.Visible; }
                if (TopElbowBackground != null) { TopElbowBackground.Fill = Brushes.White; TopElbowBackground.Visibility = Visibility.Visible; }
                if (BottomElbowBackground != null) { BottomElbowBackground.Fill = Brushes.White; BottomElbowBackground.Visibility = Visibility.Visible; }
                ApplyFluidVisibility();
            }
        }

        private void BuildCyberLayers()
        {
            if (CyberTopHorizMetal == null) return;

            var hMetal = MakeMetalGradient(false);
            CyberTopHorizMetal.Fill = hMetal;
            CyberBottomHorizMetal.Fill = hMetal;
            var vMetal = MakeMetalGradient(true);
            CyberDiagMetal.Fill = vMetal;

            var topElbowMetal = new LinearGradientBrush { StartPoint = new Point(1, 0), EndPoint = new Point(0, 1) };
            topElbowMetal.GradientStops.Add(new GradientStop(Color.FromRgb(0x1A, 0x20, 0x30), 0));
            topElbowMetal.GradientStops.Add(new GradientStop(Color.FromRgb(0x4A, 0x5A, 0x7A), 0.4));
            topElbowMetal.GradientStops.Add(new GradientStop(Color.FromRgb(0x8A, 0xA0, 0xC0), 0.6));
            topElbowMetal.GradientStops.Add(new GradientStop(Color.FromRgb(0x4A, 0x5A, 0x7A), 0.8));
            topElbowMetal.GradientStops.Add(new GradientStop(Color.FromRgb(0x1A, 0x20, 0x30), 1));
            topElbowMetal.Freeze();
            CyberTopElbowMetal.Fill = topElbowMetal;

            var bottomElbowMetal = new LinearGradientBrush { StartPoint = new Point(0, 1), EndPoint = new Point(1, 0) };
            bottomElbowMetal.GradientStops.Add(new GradientStop(Color.FromRgb(0x1A, 0x20, 0x30), 0));
            bottomElbowMetal.GradientStops.Add(new GradientStop(Color.FromRgb(0x4A, 0x5A, 0x7A), 0.4));
            bottomElbowMetal.GradientStops.Add(new GradientStop(Color.FromRgb(0x8A, 0xA0, 0xC0), 0.6));
            bottomElbowMetal.GradientStops.Add(new GradientStop(Color.FromRgb(0x4A, 0x5A, 0x7A), 0.8));
            bottomElbowMetal.GradientStops.Add(new GradientStop(Color.FromRgb(0x1A, 0x20, 0x30), 1));
            bottomElbowMetal.Freeze();
            CyberBottomElbowMetal.Fill = bottomElbowMetal;

            ApplyCyberStreamColors();
            CyberMainStream.StrokeDashArray = new DoubleCollection { 30, 90 };
            CyberParticleStream.StrokeDashArray = new DoubleCollection { 8, 50 };
            CyberSparkStream.StrokeDashArray = new DoubleCollection { 4, 80 };

            UpdateCyberGeometry();
        }

        private LinearGradientBrush MakeMetalGradient(bool vertical)
        {
            var g = vertical
                ? new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) }
                : new LinearGradientBrush { StartPoint = new Point(0.5, 0), EndPoint = new Point(0.5, 1) };
            g.GradientStops.Add(new GradientStop(Color.FromRgb(0x1A, 0x20, 0x30), 0));
            g.GradientStops.Add(new GradientStop(Color.FromRgb(0x4A, 0x5A, 0x7A), 0.2));
            g.GradientStops.Add(new GradientStop(Color.FromRgb(0x8A, 0xA0, 0xC0), 0.5));
            g.GradientStops.Add(new GradientStop(Color.FromRgb(0x4A, 0x5A, 0x7A), 0.8));
            g.GradientStops.Add(new GradientStop(Color.FromRgb(0x1A, 0x20, 0x30), 1));
            g.Freeze();
            return g;
        }

        private void ApplyCyberStreamColors()
        {
            if (CyberMainStream == null) return;
            var c = FluidColor;
            var brightC = LightenColor(c, 30);
            var sparkC = LightenColor(c, 60);

            var mainBrush = new SolidColorBrush(brightC);
            mainBrush.Freeze();
            CyberMainStream.Stroke = mainBrush;

            var particleBrush = new SolidColorBrush(sparkC);
            particleBrush.Freeze();
            CyberParticleStream.Stroke = particleBrush;
        }

        /// <summary>
        /// Z 形 5 段中心线流光路径（关键创新）
        /// </summary>
        private void UpdateCyberGeometry()
        {
            if (CyberTopHorizMetal == null) return;

            double topFlangeY = 16;
            double topPipeLeft = FlangeOffset + FlangeWidth + FlangeSpacing;
            double topPipeTop = topFlangeY;
            double topPipeWidth = _topHorizontalLength;

            double elbow1Radius = _pipeWidth;
            double elbow1CenterX = topPipeLeft + topPipeWidth + elbow1Radius;
            double elbow1CenterY = topPipeTop + _pipeWidth / 2;

            double diagPipeLeft = elbow1CenterX - _pipeWidth / 2;
            double diagPipeTop = elbow1CenterY + elbow1Radius;
            double diagPipeHeight = _verticalOffset;

            double elbow2Radius = _pipeWidth;
            double elbow2CenterX = diagPipeLeft + _pipeWidth / 2;
            double elbow2CenterY = diagPipeTop + diagPipeHeight + elbow2Radius;

            double bottomPipeLeft = elbow2CenterX + elbow2Radius;
            double bottomPipeTop = elbow2CenterY - _pipeWidth / 2;
            double bottomPipeWidth = _bottomHorizontalLength;

            // 金属层尺寸
            CyberTopHorizMetal.Width = topPipeWidth; CyberTopHorizMetal.Height = _pipeWidth;
            Canvas.SetLeft(CyberTopHorizMetal, topPipeLeft); Canvas.SetTop(CyberTopHorizMetal, topPipeTop);
            CyberTopHorizDark.Width = topPipeWidth; CyberTopHorizDark.Height = _pipeWidth;
            Canvas.SetLeft(CyberTopHorizDark, topPipeLeft); Canvas.SetTop(CyberTopHorizDark, topPipeTop);

            CyberDiagMetal.Width = _pipeWidth; CyberDiagMetal.Height = diagPipeHeight;
            Canvas.SetLeft(CyberDiagMetal, diagPipeLeft); Canvas.SetTop(CyberDiagMetal, diagPipeTop);
            CyberDiagDark.Width = _pipeWidth; CyberDiagDark.Height = diagPipeHeight;
            Canvas.SetLeft(CyberDiagDark, diagPipeLeft); Canvas.SetTop(CyberDiagDark, diagPipeTop);

            CyberBottomHorizMetal.Width = bottomPipeWidth; CyberBottomHorizMetal.Height = _pipeWidth;
            Canvas.SetLeft(CyberBottomHorizMetal, bottomPipeLeft); Canvas.SetTop(CyberBottomHorizMetal, bottomPipeTop);
            CyberBottomHorizDark.Width = bottomPipeWidth; CyberBottomHorizDark.Height = _pipeWidth;
            Canvas.SetLeft(CyberBottomHorizDark, bottomPipeLeft); Canvas.SetTop(CyberBottomHorizDark, bottomPipeTop);

            if (TopElbowPath != null && TopElbowPath.Data != null)
            { CyberTopElbowMetal.Data = TopElbowPath.Data; CyberTopElbowDark.Data = TopElbowPath.Data; }
            if (BottomElbowPath != null && BottomElbowPath.Data != null)
            { CyberBottomElbowMetal.Data = BottomElbowPath.Data; CyberBottomElbowDark.Data = BottomElbowPath.Data; }

            // ★★★ Z 形中心线流光 path ★★★
            double startX = topPipeLeft;
            double startY = elbow1CenterY;
            double upElbowEntryX = topPipeLeft + topPipeWidth;
            double upElbowEntryY = elbow1CenterY;
            double upElbowExitX = elbow1CenterX;
            double upElbowExitY = elbow1CenterY + elbow1Radius;
            double diagEndX = elbow2CenterX;
            double diagEndY = elbow2CenterY - elbow2Radius;
            double downElbowExitX = elbow2CenterX + elbow2Radius;
            double downElbowExitY = elbow2CenterY;
            double endX = bottomPipeLeft + bottomPipeWidth;
            double endY = elbow2CenterY;

            var fig = new PathFigure { StartPoint = new Point(startX, startY) };
            fig.Segments.Add(new LineSegment(new Point(upElbowEntryX, upElbowEntryY), true));
            fig.Segments.Add(new ArcSegment(
                new Point(upElbowExitX, upElbowExitY),
                new Size(elbow1Radius, elbow1Radius),
                0, false, SweepDirection.Clockwise, true));
            fig.Segments.Add(new LineSegment(new Point(diagEndX, diagEndY), true));
            fig.Segments.Add(new ArcSegment(
                new Point(downElbowExitX, downElbowExitY),
                new Size(elbow2Radius, elbow2Radius),
                0, false, SweepDirection.Counterclockwise, true));
            fig.Segments.Add(new LineSegment(new Point(endX, endY), true));

            var geo = new PathGeometry();
            geo.Figures.Add(fig);
            geo.Freeze();

            CyberMainStream.Data = geo;
            CyberParticleStream.Data = geo;
            CyberSparkStream.Data = geo;

            CyberMainStream.StrokeThickness = Math.Max(2, _pipeWidth * 0.6);
            CyberParticleStream.StrokeThickness = Math.Max(1.5, _pipeWidth * 0.3);
            CyberSparkStream.StrokeThickness = Math.Max(1, _pipeWidth * 0.15);
        }

        private void StartCyberAnimation()
        {
            if (CyberMainStream == null) return;
            StopCyberAnimation();
            int sign = PipeFlowDir == PipeFlowDirection.LeftToRight ? -1 : 1;
            double speed = Math.Max(0.1, FlowSpeed);

            _cyberMainAnim = new DoubleAnimation { From = 0, To = sign * _cyberMainPeriod, Duration = TimeSpan.FromMilliseconds(2000.0 / speed), RepeatBehavior = RepeatBehavior.Forever };
            CyberMainStream.BeginAnimation(Path.StrokeDashOffsetProperty, _cyberMainAnim);

            _cyberParticleAnim = new DoubleAnimation { From = 0, To = sign * _cyberParticlePeriod, Duration = TimeSpan.FromMilliseconds(1500.0 / speed), RepeatBehavior = RepeatBehavior.Forever };
            CyberParticleStream.BeginAnimation(Path.StrokeDashOffsetProperty, _cyberParticleAnim);

            _cyberSparkAnim = new DoubleAnimation { From = sign * -30, To = sign * (_cyberSparkPeriod - 30), Duration = TimeSpan.FromMilliseconds(1200.0 / speed), RepeatBehavior = RepeatBehavior.Forever };
            CyberSparkStream.BeginAnimation(Path.StrokeDashOffsetProperty, _cyberSparkAnim);
        }

        private void StopCyberAnimation()
        {
            if (CyberMainStream == null) return;
            CyberMainStream.BeginAnimation(Path.StrokeDashOffsetProperty, null);
            CyberParticleStream.BeginAnimation(Path.StrokeDashOffsetProperty, null);
            CyberSparkStream.BeginAnimation(Path.StrokeDashOffsetProperty, null);
        }

        #endregion

        #region 颜色辅助

        private static Color LightenColor(Color c, byte amount)
        {
            return Color.FromRgb(
                (byte)Math.Min(255, c.R + amount),
                (byte)Math.Min(255, c.G + amount),
                (byte)Math.Min(255, c.B + amount));
        }

        private static Color DarkenColor(Color c, byte amount)
        {
            return Color.FromRgb(
                (byte)Math.Max(0, c.R - amount),
                (byte)Math.Max(0, c.G - amount),
                (byte)Math.Max(0, c.B - amount));
        }

        private static Color BlendColors(Color c1, Color c2, double ratio)
        {
            return Color.FromRgb(
                (byte)(c1.R * (1 - ratio) + c2.R * ratio),
                (byte)(c1.G * (1 - ratio) + c2.G * ratio),
                (byte)(c1.B * (1 - ratio) + c2.B * ratio));
        }

        #endregion

        #region 锚点显示

        private void OnPipeMouseEnter(object sender, MouseEventArgs e)
        {
            if (!_isResizing && !_isDraggingPipe && !_isRotating) ShowAllAnchors();
        }
        private void OnPipeMouseLeave(object sender, MouseEventArgs e)
        {
            if (!_isResizing && !_isDraggingPipe && !_isRotating && !IsSelected) HideAllAnchors();
        }
        private void ShowAllAnchors()
        {
            TopLengthAnchor.Opacity = 0.6;
            BottomLengthAnchor.Opacity = 0.6;
            VerticalOffsetAnchor.Opacity = 0.6;
            PipeWidthAnchor.Opacity = 0.6;
            RotateAnchor.Opacity = 0.6;
            RotateAnchorLine.Opacity = 0.6;
        }
        private void HideAllAnchors()
        {
            TopLengthAnchor.Opacity = 0;
            BottomLengthAnchor.Opacity = 0;
            VerticalOffsetAnchor.Opacity = 0;
            PipeWidthAnchor.Opacity = 0;
            RotateAnchor.Opacity = 0;
            RotateAnchorLine.Opacity = 0;
        }
        private void UpdateSelectionVisual(bool isSelected)
        {
            if (isSelected)
            {
                SelectionBorder.Visibility = Visibility.Visible;
                SelectionBorderM.Visibility = Visibility.Visible;
                SelectionBorderB.Visibility = Visibility.Visible;
                ShowAllAnchors();
            }
            else
            {
                SelectionBorder.Visibility = Visibility.Collapsed;
                SelectionBorderM.Visibility = Visibility.Collapsed;
                SelectionBorderB.Visibility = Visibility.Collapsed;
                if (!IsMouseOver && !_isResizing && !_isRotating) HideAllAnchors();
            }
        }

        #endregion

        #region 锚点事件

        private void SetupAnchorEvents()
        {
            TopLengthAnchor.MouseEnter += OnAnchorMouseEnter;
            TopLengthAnchor.MouseLeave += OnAnchorMouseLeave;
            TopLengthAnchor.MouseDown += (s, e) => StartResize(ZAnchorPosition.TopLength, e);
            TopLengthAnchor.MouseMove += OnAnchorMouseMove;
            TopLengthAnchor.MouseUp += OnAnchorMouseUp;

            BottomLengthAnchor.MouseEnter += OnAnchorMouseEnter;
            BottomLengthAnchor.MouseLeave += OnAnchorMouseLeave;
            BottomLengthAnchor.MouseDown += (s, e) => StartResize(ZAnchorPosition.BottomLength, e);
            BottomLengthAnchor.MouseMove += OnAnchorMouseMove;
            BottomLengthAnchor.MouseUp += OnAnchorMouseUp;

            VerticalOffsetAnchor.MouseEnter += OnAnchorMouseEnter;
            VerticalOffsetAnchor.MouseLeave += OnAnchorMouseLeave;
            VerticalOffsetAnchor.MouseDown += (s, e) => StartResize(ZAnchorPosition.VerticalOffset, e);
            VerticalOffsetAnchor.MouseMove += OnAnchorMouseMove;
            VerticalOffsetAnchor.MouseUp += OnAnchorMouseUp;

            PipeWidthAnchor.MouseEnter += OnAnchorMouseEnter;
            PipeWidthAnchor.MouseLeave += OnAnchorMouseLeave;
            PipeWidthAnchor.MouseDown += (s, e) => StartResize(ZAnchorPosition.PipeWidth, e);
            PipeWidthAnchor.MouseMove += OnAnchorMouseMove;
            PipeWidthAnchor.MouseUp += OnAnchorMouseUp;

            RotateAnchor.MouseEnter += OnRotateAnchorMouseEnter;
            RotateAnchor.MouseLeave += OnRotateAnchorMouseLeave;
            RotateAnchor.MouseDown += OnRotateAnchorMouseDown;
            RotateAnchor.MouseMove += OnRotateAnchorMouseMove;
            RotateAnchor.MouseUp += OnRotateAnchorMouseUp;
        }
        private void OnAnchorMouseEnter(object sender, MouseEventArgs e)
        { if (sender is Ellipse anchor && !_isRotating) anchor.Opacity = 1.0; }
        private void OnAnchorMouseLeave(object sender, MouseEventArgs e)
        { if (sender is Ellipse anchor && !_isResizing && !_isRotating) anchor.Opacity = 0.6; }
        private void OnRotateAnchorMouseEnter(object sender, MouseEventArgs e)
        { if (!_isRotating) { RotateAnchor.Opacity = 1.0; RotateAnchorLine.Opacity = 1.0; } }
        private void OnRotateAnchorMouseLeave(object sender, MouseEventArgs e)
        { if (!_isRotating) { RotateAnchor.Opacity = 0.6; RotateAnchorLine.Opacity = 0.6; } }

        #endregion

        #region 拖拽移动

        private void OnControlMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left && !_isResizing && !_isRotating)
            {
                var clicked = e.OriginalSource as FrameworkElement;
                if (clicked != null &&
                    (clicked.Name == "TopLengthAnchor" || clicked.Name == "BottomLengthAnchor" ||
                     clicked.Name == "VerticalOffsetAnchor" || clicked.Name == "PipeWidthAnchor" ||
                     clicked.Name == "RotateAnchor"))
                    return;

                if (e.ClickCount == 2)
                {
                    _isDraggingPipe = false;
                    ReleaseMouseCapture();
                    Opacity = 1.0;
                    PipeDoubleClicked?.Invoke(this, PipeId);
                    e.Handled = true;
                    return;
                }

                PipeSelected?.Invoke(this, PipeId);
                IsSelected = true;

                _isDraggingPipe = true;
                _dragStartPoint = e.GetPosition(Parent as UIElement);
                _originalPosition = new Point(Canvas.GetLeft(this), Canvas.GetTop(this));
                if (double.IsNaN(_originalPosition.X)) _originalPosition.X = 0;
                if (double.IsNaN(_originalPosition.Y)) _originalPosition.Y = 0;

                CacheMode = null;
                RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.LowQuality);

                CaptureMouse();
                Opacity = 0.7;
                e.Handled = true;

                PipeDragStarted?.Invoke(this, new ZPipeDragEventArgs
                {
                    PipeId = PipeId,
                    StartPosition = _originalPosition,
                    CurrentPosition = _originalPosition
                });
            }
        }

        private void OnControlMouseMove(object sender, MouseEventArgs e)
        {
            if (_isDraggingPipe && e.LeftButton == MouseButtonState.Pressed)
            {
                Point currentPoint = e.GetPosition(Parent as UIElement);
                double deltaX = currentPoint.X - _dragStartPoint.X;
                double deltaY = currentPoint.Y - _dragStartPoint.Y;
                double newLeft = _originalPosition.X + deltaX;
                double newTop = _originalPosition.Y + deltaY;

                Canvas.SetLeft(this, newLeft);
                Canvas.SetTop(this, newTop);

                PipeDragging?.Invoke(this, new ZPipeDragEventArgs
                {
                    PipeId = PipeId,
                    StartPosition = _originalPosition,
                    CurrentPosition = new Point(newLeft, newTop),
                    DeltaX = deltaX,
                    DeltaY = deltaY
                });
                e.Handled = true;
            }
        }

        private void OnControlMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (_isDraggingPipe && e.ChangedButton == MouseButton.Left)
            {
                Point finalPosition = new Point(Canvas.GetLeft(this), Canvas.GetTop(this));

                PipeDragCompleted?.Invoke(this, new ZPipeDragEventArgs
                {
                    PipeId = PipeId,
                    StartPosition = _originalPosition,
                    CurrentPosition = finalPosition,
                    DeltaX = finalPosition.X - _originalPosition.X,
                    DeltaY = finalPosition.Y - _originalPosition.Y
                });

                Opacity = 1.0;
                _isDraggingPipe = false;

                RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
                CacheMode = new BitmapCache { RenderAtScale = 1.0, SnapsToDevicePixels = true, EnableClearType = false };

                ReleaseMouseCapture();
                e.Handled = true;
            }
        }

        private void OnControlMouseLeave(object sender, MouseEventArgs e) { }

        #endregion

        #region 调整大小

        private Point ComputeFixedWorldPoint(ZAnchorPosition anchor)
        {
            double pipeLeft = Canvas.GetLeft(this);
            double pipeTop = Canvas.GetTop(this);
            if (double.IsNaN(pipeLeft)) pipeLeft = 0;
            if (double.IsNaN(pipeTop)) pipeTop = 0;
            Point center = new Point(pipeLeft + Width / 2, pipeTop + Height / 2);

            Point localFixed = ComputeLocalFixedPoint(anchor, _originalSize);

            double radians = _rotationAngle * Math.PI / 180.0;
            double cos = Math.Cos(radians);
            double sin = Math.Sin(radians);
            Point worldOffset = new Point(localFixed.X * cos - localFixed.Y * sin,
                                          localFixed.X * sin + localFixed.Y * cos);

            return new Point(center.X + worldOffset.X, center.Y + worldOffset.Y);
        }

        private Point ComputeLocalFixedPoint(ZAnchorPosition anchor, ZPipeSize size)
        {
            double flangeHeight = size.PipeWidth + FLANGE_EXTENSION;
            double topFlangeY = 16;
            double leftFlangeTop = topFlangeY + (size.PipeWidth - flangeHeight) / 2;

            double topPipeLeft = FlangeOffset + FlangeWidth + FlangeSpacing;
            double topPipeTop = topFlangeY;

            double elbow1Radius = size.PipeWidth;
            double elbow1CenterX = topPipeLeft + size.TopHorizontalLength + elbow1Radius;
            double elbow1CenterY = topPipeTop + size.PipeWidth / 2;

            double diagPipeLeft = elbow1CenterX - size.PipeWidth / 2;
            double diagPipeTop = elbow1CenterY + elbow1Radius;
            double diagPipeHeight = size.VerticalOffset;

            double elbow2Radius = size.PipeWidth;
            double elbow2CenterX = diagPipeLeft + size.PipeWidth / 2;
            double elbow2CenterY = diagPipeTop + diagPipeHeight + elbow2Radius;

            double bottomPipeLeft = elbow2CenterX + elbow2Radius;
            double bottomPipeTop = elbow2CenterY - size.PipeWidth / 2;
            double bottomPipeWidth = size.BottomHorizontalLength;

            double rightFlangeX = bottomPipeLeft + bottomPipeWidth;
            double rightFlangeY = bottomPipeTop + (size.PipeWidth - flangeHeight) / 2;

            double totalW = rightFlangeX + FlangeWidth + 4;
            double totalH = Math.Max(rightFlangeY + flangeHeight, leftFlangeTop + flangeHeight) + 4;

            double cx = totalW / 2;
            double cy = totalH / 2;

            switch (anchor)
            {
                case ZAnchorPosition.TopLength:
                    return new Point(rightFlangeX - cx, elbow2CenterY - cy);
                case ZAnchorPosition.BottomLength:
                    return new Point(FlangeOffset - cx, elbow1CenterY - cy);
                case ZAnchorPosition.VerticalOffset:
                    return new Point(FlangeOffset - cx, elbow1CenterY - cy);
                case ZAnchorPosition.PipeWidth:
                    return new Point(FlangeOffset - cx, elbow1CenterY - cy);
                default:
                    return new Point(0, 0);
            }
        }

        private void RepositionByFixedPoint()
        {
            if (!_resizingAnchor.HasValue) return;

            var currentSize = new ZPipeSize
            {
                TopHorizontalLength = TopHorizontalLength,
                BottomHorizontalLength = BottomHorizontalLength,
                VerticalOffset = VerticalOffset,
                PipeWidth = PipeWidth
            };
            Point newLocalFixed = ComputeLocalFixedPoint(_resizingAnchor.Value, currentSize);

            double radians = _rotationAngle * Math.PI / 180.0;
            double cos = Math.Cos(radians);
            double sin = Math.Sin(radians);
            Point newWorldOffset = new Point(newLocalFixed.X * cos - newLocalFixed.Y * sin,
                                             newLocalFixed.X * sin + newLocalFixed.Y * cos);

            Point newCenter = new Point(_fixedWorldPoint.X - newWorldOffset.X,
                                        _fixedWorldPoint.Y - newWorldOffset.Y);

            Canvas.SetLeft(this, newCenter.X - Width / 2);
            Canvas.SetTop(this, newCenter.Y - Height / 2);
        }

        private void StartResize(ZAnchorPosition anchor, MouseButtonEventArgs e)
        {
            _isResizing = true;
            _resizingAnchor = anchor;
            _dragStartPoint = e.GetPosition(Parent as UIElement);
            _originalSize = new ZPipeSize
            {
                TopHorizontalLength = TopHorizontalLength,
                BottomHorizontalLength = BottomHorizontalLength,
                VerticalOffset = VerticalOffset,
                PipeWidth = PipeWidth
            };

            _originalPosition = new Point(Canvas.GetLeft(this), Canvas.GetTop(this));
            if (double.IsNaN(_originalPosition.X)) _originalPosition.X = 0;
            if (double.IsNaN(_originalPosition.Y)) _originalPosition.Y = 0;
            _fixedWorldPoint = ComputeFixedWorldPoint(anchor);

            CacheMode = null;
            RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.LowQuality);

            (e.OriginalSource as UIElement)?.CaptureMouse();
            e.Handled = true;

            PipeResizeStarted?.Invoke(this, new ZPipeResizeEventArgs
            {
                PipeId = PipeId,
                AnchorPosition = anchor,
                OriginalSize = _originalSize,
                CurrentSize = _originalSize
            });
        }

        private void OnAnchorMouseMove(object sender, MouseEventArgs e)
        {
            if (!_isResizing || e.LeftButton != MouseButtonState.Pressed || !_resizingAnchor.HasValue)
                return;

            Point currentPoint = e.GetPosition(Parent as UIElement);
            double rawDx = currentPoint.X - _dragStartPoint.X;
            double rawDy = currentPoint.Y - _dragStartPoint.Y;

            double radians = _rotationAngle * Math.PI / 180.0;
            double cos = Math.Cos(radians);
            double sin = Math.Sin(radians);
            double localDx = rawDx * cos + rawDy * sin;
            double localDy = -rawDx * sin + rawDy * cos;

            switch (_resizingAnchor.Value)
            {
                case ZAnchorPosition.TopLength:
                    TopHorizontalLength = _originalSize.TopHorizontalLength - localDx;
                    break;
                case ZAnchorPosition.BottomLength:
                    BottomHorizontalLength = _originalSize.BottomHorizontalLength + localDx;
                    break;
                case ZAnchorPosition.VerticalOffset:
                    VerticalOffset = _originalSize.VerticalOffset + localDy;
                    break;
                case ZAnchorPosition.PipeWidth:
                    PipeWidth = _originalSize.PipeWidth + localDy;
                    break;
            }

            RepositionByFixedPoint();

            PipeResizing?.Invoke(this, new ZPipeResizeEventArgs
            {
                PipeId = PipeId,
                AnchorPosition = _resizingAnchor.Value,
                OriginalSize = _originalSize,
                CurrentSize = new ZPipeSize
                {
                    TopHorizontalLength = TopHorizontalLength,
                    BottomHorizontalLength = BottomHorizontalLength,
                    VerticalOffset = VerticalOffset,
                    PipeWidth = PipeWidth
                }
            });
            e.Handled = true;
        }

        private void OnAnchorMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (_isResizing && e.ChangedButton == MouseButton.Left)
            {
                if (_resizingAnchor.HasValue)
                {
                    PipeResizeCompleted?.Invoke(this, new ZPipeResizeEventArgs
                    {
                        PipeId = PipeId,
                        AnchorPosition = _resizingAnchor.Value,
                        OriginalSize = _originalSize,
                        CurrentSize = new ZPipeSize
                        {
                            TopHorizontalLength = TopHorizontalLength,
                            BottomHorizontalLength = BottomHorizontalLength,
                            VerticalOffset = VerticalOffset,
                            PipeWidth = PipeWidth
                        }
                    });
                }

                _isResizing = false;
                _resizingAnchor = null;
                RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
                CacheMode = new BitmapCache { RenderAtScale = 1.0, SnapsToDevicePixels = true, EnableClearType = false };
                (e.OriginalSource as UIElement)?.ReleaseMouseCapture();
                e.Handled = true;
            }
        }

        #endregion

        #region 旋转

        private void OnRotateAnchorMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                _isRotating = true;
                _dragStartPoint = e.GetPosition(Parent as UIElement);
                _originalRotation = RotationAngle;

                CacheMode = null;
                RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.LowQuality);

                RotateAnchor.CaptureMouse();
                RotationLabel.Visibility = Visibility.Visible;
                e.Handled = true;

                PipeRotateStarted?.Invoke(this, new ZPipeRotateEventArgs
                {
                    PipeId = PipeId,
                    OriginalAngle = _originalRotation,
                    CurrentAngle = _originalRotation
                });
            }
        }

        private void OnRotateAnchorMouseMove(object sender, MouseEventArgs e)
        {
            if (_isRotating && e.LeftButton == MouseButtonState.Pressed)
            {
                Point currentPoint = e.GetPosition(Parent as UIElement);
                var pipeLeft = Canvas.GetLeft(this);
                var pipeTop = Canvas.GetTop(this);
                if (double.IsNaN(pipeLeft)) pipeLeft = 0;
                if (double.IsNaN(pipeTop)) pipeTop = 0;

                Point center = new Point(pipeLeft + ActualWidth / 2, pipeTop + ActualHeight / 2);
                double startAngle = Math.Atan2(_dragStartPoint.Y - center.Y, _dragStartPoint.X - center.X) * 180 / Math.PI;
                double currentAngle = Math.Atan2(currentPoint.Y - center.Y, currentPoint.X - center.X) * 180 / Math.PI;
                double newAngle = _originalRotation + (currentAngle - startAngle);

                if (Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift))
                    newAngle = Math.Round(newAngle / 15) * 15;

                RotationAngle = newAngle;
                RotationText.Text = $"{Math.Round(RotationAngle)}°";

                _rotationLabelTimer.Stop();
                _rotationLabelTimer.Start();

                PipeRotating?.Invoke(this, new ZPipeRotateEventArgs
                {
                    PipeId = PipeId,
                    OriginalAngle = _originalRotation,
                    CurrentAngle = RotationAngle
                });
                e.Handled = true;
            }
        }

        private void OnRotateAnchorMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (_isRotating && e.ChangedButton == MouseButton.Left)
            {
                PipeRotateCompleted?.Invoke(this, new ZPipeRotateEventArgs
                {
                    PipeId = PipeId,
                    OriginalAngle = _originalRotation,
                    CurrentAngle = RotationAngle
                });

                _isRotating = false;
                RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
                CacheMode = new BitmapCache { RenderAtScale = 1.0, SnapsToDevicePixels = true, EnableClearType = false };
                RotateAnchor.ReleaseMouseCapture();
                e.Handled = true;
            }
        }

        private void UpdateRotation()
        {
            if (_rotateTransform != null)
                _rotateTransform.Angle = _rotationAngle;
        }

        #endregion

        #region 几何更新

        private void UpdatePipeGeometry()
        {
            try
            {
                PipeCanvas.BeginInit();

                double flangeHeight = _pipeWidth + FLANGE_EXTENSION;
                double topFlangeY = 16;

                double leftFlangeTop = topFlangeY + (_pipeWidth - flangeHeight) / 2;
                Canvas.SetTop(LeftFlangeGroup, leftFlangeTop);
                LeftFlangeGroup.Width = FlangeWidth;
                LeftFlangeGroup.Height = flangeHeight;
                LeftFlangeBase.Width = FlangeWidth;
                LeftFlangeBase.Height = flangeHeight;
                LeftFlangeGradient.Width = FlangeWidth;
                LeftFlangeGradient.Height = flangeHeight;
                LeftFlangeBorder.Width = FlangeWidth;
                LeftFlangeBorder.Height = flangeHeight;

                double topPipeLeft = FlangeOffset + FlangeWidth + FlangeSpacing;
                double topPipeTop = topFlangeY;
                double topPipeWidth = _topHorizontalLength;

                SetRectGeometry(TopHorizontalPipeBackground, topPipeLeft, topPipeTop, topPipeWidth, _pipeWidth);
                SetRectGeometry(TopHorizontalFluidBackground, topPipeLeft, topPipeTop, topPipeWidth, _pipeWidth);
                SetRectGeometry(TopHorizontalFluidPattern, topPipeLeft, topPipeTop, topPipeWidth, _pipeWidth);
                SetRectGeometry(TopHorizontalPipe, topPipeLeft, topPipeTop, topPipeWidth, _pipeWidth);

                TopHorizontalTopEdge.X1 = topPipeLeft;
                TopHorizontalTopEdge.Y1 = topPipeTop + 0.32;
                TopHorizontalTopEdge.X2 = topPipeLeft + topPipeWidth;
                TopHorizontalTopEdge.Y2 = topPipeTop + 0.32;

                TopHorizontalBottomEdge.X1 = topPipeLeft;
                TopHorizontalBottomEdge.Y1 = topPipeTop + _pipeWidth - 0.32;
                TopHorizontalBottomEdge.X2 = topPipeLeft + topPipeWidth;
                TopHorizontalBottomEdge.Y2 = topPipeTop + _pipeWidth - 0.32;

                double elbow1Radius = _pipeWidth;
                double elbow1CenterX = topPipeLeft + topPipeWidth + elbow1Radius;
                double elbow1CenterY = topPipeTop + _pipeWidth / 2;
                UpdateTopElbow(elbow1CenterX, elbow1CenterY, elbow1Radius);

                double diagPipeLeft = elbow1CenterX - _pipeWidth / 2;
                double diagPipeTop = elbow1CenterY + elbow1Radius;
                double diagPipeHeight = _verticalOffset;

                SetRectGeometry(DiagonalPipeBackground, diagPipeLeft, diagPipeTop, _pipeWidth, diagPipeHeight);
                SetRectGeometry(DiagonalFluidBackground, diagPipeLeft, diagPipeTop, _pipeWidth, diagPipeHeight);
                SetRectGeometry(DiagonalFluidPattern, diagPipeLeft, diagPipeTop, _pipeWidth, diagPipeHeight);
                SetRectGeometry(DiagonalPipe, diagPipeLeft, diagPipeTop, _pipeWidth, diagPipeHeight);

                DiagonalLeftEdge.X1 = diagPipeLeft + 0.32;
                DiagonalLeftEdge.Y1 = diagPipeTop;
                DiagonalLeftEdge.X2 = diagPipeLeft + 0.32;
                DiagonalLeftEdge.Y2 = diagPipeTop + diagPipeHeight;

                DiagonalRightEdge.X1 = diagPipeLeft + _pipeWidth - 0.32;
                DiagonalRightEdge.Y1 = diagPipeTop;
                DiagonalRightEdge.X2 = diagPipeLeft + _pipeWidth - 0.32;
                DiagonalRightEdge.Y2 = diagPipeTop + diagPipeHeight;

                double elbow2Radius = _pipeWidth;
                double elbow2CenterX = diagPipeLeft + _pipeWidth / 2;
                double elbow2CenterY = diagPipeTop + diagPipeHeight + elbow2Radius;
                UpdateBottomElbow(elbow2CenterX, elbow2CenterY, elbow2Radius);

                double bottomPipeLeft = elbow2CenterX + elbow2Radius;
                double bottomPipeTop = elbow2CenterY - _pipeWidth / 2;
                double bottomPipeWidth = _bottomHorizontalLength;

                SetRectGeometry(BottomHorizontalPipeBackground, bottomPipeLeft, bottomPipeTop, bottomPipeWidth, _pipeWidth);
                SetRectGeometry(BottomHorizontalFluidBackground, bottomPipeLeft, bottomPipeTop, bottomPipeWidth, _pipeWidth);
                SetRectGeometry(BottomHorizontalFluidPattern, bottomPipeLeft, bottomPipeTop, bottomPipeWidth, _pipeWidth);
                SetRectGeometry(BottomHorizontalPipe, bottomPipeLeft, bottomPipeTop, bottomPipeWidth, _pipeWidth);

                BottomHorizontalTopEdge.X1 = bottomPipeLeft;
                BottomHorizontalTopEdge.Y1 = bottomPipeTop + 0.32;
                BottomHorizontalTopEdge.X2 = bottomPipeLeft + bottomPipeWidth;
                BottomHorizontalTopEdge.Y2 = bottomPipeTop + 0.32;

                BottomHorizontalBottomEdge.X1 = bottomPipeLeft;
                BottomHorizontalBottomEdge.Y1 = bottomPipeTop + _pipeWidth - 0.32;
                BottomHorizontalBottomEdge.X2 = bottomPipeLeft + bottomPipeWidth;
                BottomHorizontalBottomEdge.Y2 = bottomPipeTop + _pipeWidth - 0.32;

                double rightFlangeX = bottomPipeLeft + bottomPipeWidth;
                double rightFlangeY = bottomPipeTop + (_pipeWidth - flangeHeight) / 2;
                Canvas.SetLeft(RightFlangeGroup, rightFlangeX);
                Canvas.SetTop(RightFlangeGroup, rightFlangeY);
                RightFlangeGroup.Width = FlangeWidth;
                RightFlangeGroup.Height = flangeHeight;
                RightFlangeBase.Width = FlangeWidth;
                RightFlangeBase.Height = flangeHeight;
                RightFlangeGradient.Width = FlangeWidth;
                RightFlangeGradient.Height = flangeHeight;
                RightFlangeBorder.Width = FlangeWidth;
                RightFlangeBorder.Height = flangeHeight;

                Canvas.SetLeft(LeftConnection, FlangeOffset + FlangeWidth);
                Canvas.SetTop(LeftConnection, topPipeTop);
                LeftConnection.Height = _pipeWidth;

                Canvas.SetLeft(RightConnection, rightFlangeX - 2);
                Canvas.SetTop(RightConnection, bottomPipeTop);
                RightConnection.Height = _pipeWidth;

                UpdateAnchorPositions(topPipeLeft, topPipeTop, rightFlangeX, rightFlangeY,
                    diagPipeLeft, diagPipeTop, diagPipeHeight);

                double totalWidth = rightFlangeX + FlangeWidth + 4;
                double totalHeight = Math.Max(rightFlangeY + flangeHeight, leftFlangeTop + flangeHeight) + 4;
                Width = totalWidth;
                Height = totalHeight;

                SelectionBorder.Width = (elbow1CenterX + _pipeWidth / 2) - topPipeLeft;
                SelectionBorder.Height = _pipeWidth;
                Canvas.SetLeft(SelectionBorder, topPipeLeft);
                Canvas.SetTop(SelectionBorder, topPipeTop);

                SelectionBorderM.Width = _pipeWidth;
                SelectionBorderM.Height = (elbow2CenterY + _pipeWidth / 2) - (elbow1CenterY - _pipeWidth / 2);
                Canvas.SetLeft(SelectionBorderM, diagPipeLeft);
                Canvas.SetTop(SelectionBorderM, elbow1CenterY - _pipeWidth / 2);

                SelectionBorderB.Width = (bottomPipeLeft + bottomPipeWidth) - (elbow2CenterX - _pipeWidth / 2);
                SelectionBorderB.Height = _pipeWidth;
                Canvas.SetLeft(SelectionBorderB, elbow2CenterX - _pipeWidth / 2);
                Canvas.SetTop(SelectionBorderB, bottomPipeTop);

                PipeCanvas.EndInit();

                UpdateFluidPatternViewports();
                if (IsFlowing && IsFluidVisible && !UseDefaultStyle) StartFlowAnimation();

                // ★ 同步赛博层
                UpdateCyberGeometry();
                if (UseDefaultStyle && IsFlowing) StartCyberAnimation();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"UpdatePipeGeometry error: {ex.Message}");
            }
        }

        private void SetRectGeometry(Rectangle rect, double left, double top, double width, double height)
        {
            Canvas.SetLeft(rect, left);
            Canvas.SetTop(rect, top);
            rect.Width = width;
            rect.Height = height;
        }

        private void UpdateTopElbow(double centerX, double centerY, double radius)
        {
            PathGeometry geo = new PathGeometry();
            PathFigure fig = new PathFigure();

            fig.StartPoint = new Point(centerX - radius, centerY - _pipeWidth / 2);

            fig.Segments.Add(new ArcSegment(
                new Point(centerX + _pipeWidth / 2, centerY + radius),
                new Size(radius + _pipeWidth / 2, radius + _pipeWidth / 2),
                0, false, SweepDirection.Clockwise, true));

            fig.Segments.Add(new LineSegment(
                new Point(centerX - _pipeWidth / 2, centerY + radius), true));

            fig.Segments.Add(new ArcSegment(
                new Point(centerX - radius, centerY + _pipeWidth / 2),
                new Size(radius - _pipeWidth / 2, radius - _pipeWidth / 2),
                0, false, SweepDirection.Counterclockwise, true));

            fig.IsClosed = true;
            geo.Figures.Add(fig);
            geo.Freeze();

            TopElbowBackground.Data = geo;
            TopElbowFluidBackground.Data = geo;
            TopElbowPath.Data = geo;

            UpdateTopElbowHighlight(centerX, centerY, radius);
        }

        private void UpdateTopElbowHighlight(double centerX, double centerY, double radius)
        {
            PathGeometry hlGeo = new PathGeometry();
            PathFigure hlFig = new PathFigure();

            double hlRadius = radius - _pipeWidth / 4;
            hlFig.StartPoint = new Point(centerX - hlRadius, centerY + _pipeWidth / 4);

            hlFig.Segments.Add(new ArcSegment(
                new Point(centerX + _pipeWidth / 4, centerY + hlRadius),
                new Size(hlRadius, hlRadius),
                0, false, SweepDirection.Clockwise, true));

            hlGeo.Figures.Add(hlFig);
            TopElbowHighlight.Data = hlGeo;
        }

        private void UpdateBottomElbow(double centerX, double centerY, double radius)
        {
            PathGeometry geo = new PathGeometry();
            PathFigure fig = new PathFigure();

            fig.StartPoint = new Point(centerX - _pipeWidth / 2, centerY - radius);

            fig.Segments.Add(new ArcSegment(
                new Point(centerX + radius, centerY + _pipeWidth / 2),
                new Size(radius + _pipeWidth / 2, radius + _pipeWidth / 2),
                0, false, SweepDirection.Counterclockwise, true));

            fig.Segments.Add(new LineSegment(
                new Point(centerX + radius, centerY - _pipeWidth / 2), true));

            fig.Segments.Add(new ArcSegment(
                new Point(centerX + _pipeWidth / 2, centerY - radius),
                new Size(radius - _pipeWidth / 2, radius - _pipeWidth / 2),
                0, false, SweepDirection.Clockwise, true));

            fig.IsClosed = true;
            geo.Figures.Add(fig);
            geo.Freeze();

            BottomElbowBackground.Data = geo;
            BottomElbowFluidBackground.Data = geo;
            BottomElbowPath.Data = geo;

            UpdateBottomElbowHighlight(centerX, centerY, radius);
        }

        private void UpdateBottomElbowHighlight(double centerX, double centerY, double radius)
        {
            PathGeometry hlGeo = new PathGeometry();
            PathFigure hlFig = new PathFigure();

            double hlRadius = radius - _pipeWidth / 4;
            hlFig.StartPoint = new Point(centerX + _pipeWidth / 4, centerY - hlRadius);

            hlFig.Segments.Add(new ArcSegment(
                new Point(centerX + hlRadius, centerY + _pipeWidth / 4),
                new Size(hlRadius, hlRadius),
                0, false, SweepDirection.Counterclockwise, true));

            hlGeo.Figures.Add(hlFig);
            BottomElbowHighlight.Data = hlGeo;
        }

        private void UpdateAnchorPositions(double topPipeLeft, double topPipeTop,
            double rightFlangeX, double rightFlangeY,
            double diagPipeLeft, double diagPipeTop, double diagPipeHeight)
        {
            Canvas.SetLeft(TopLengthAnchor, -6);
            Canvas.SetTop(TopLengthAnchor, topPipeTop + _pipeWidth / 2 - 6);

            Canvas.SetLeft(BottomLengthAnchor, rightFlangeX + 6);
            Canvas.SetTop(BottomLengthAnchor, rightFlangeY + 14 - 6);

            Canvas.SetLeft(VerticalOffsetAnchor, diagPipeLeft + _pipeWidth + 4);
            Canvas.SetTop(VerticalOffsetAnchor, diagPipeTop + diagPipeHeight / 2 - 6);

            Canvas.SetLeft(PipeWidthAnchor, topPipeLeft + _topHorizontalLength / 2 - 6);
            Canvas.SetTop(PipeWidthAnchor, topPipeTop + _pipeWidth + 4);

            Canvas.SetLeft(RotateAnchor, Width / 2 - 6);
            Canvas.SetTop(RotateAnchor, -30);

            RotateAnchorLine.X1 = Width / 2;
            RotateAnchorLine.Y1 = 0;
            RotateAnchorLine.X2 = Width / 2;
            RotateAnchorLine.Y2 = -24;

            Canvas.SetLeft(RotationLabel, Width / 2 - 20);
            Canvas.SetTop(RotationLabel, -50);
        }

        #endregion

        #region INotifyPropertyChanged

        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        #endregion

        #region 公共方法

        public void SetPosition(double left, double top)
        {
            Canvas.SetLeft(this, left);
            Canvas.SetTop(this, top);
        }

        public Point GetPosition()
        {
            return new Point(Canvas.GetLeft(this), Canvas.GetTop(this));
        }

        public ZPipeSize GetSize()
        {
            return new ZPipeSize
            {
                TopHorizontalLength = TopHorizontalLength,
                BottomHorizontalLength = BottomHorizontalLength,
                VerticalOffset = VerticalOffset,
                PipeWidth = PipeWidth
            };
        }

        public void SetSize(ZPipeSize size)
        {
            TopHorizontalLength = size.TopHorizontalLength;
            BottomHorizontalLength = size.BottomHorizontalLength;
            VerticalOffset = size.VerticalOffset;
            PipeWidth = size.PipeWidth;
        }

        public void SetSelected(bool selected) => IsSelected = selected;

        #endregion

        #region IPipeSnapPoints 实现

        public List<PipeSnapPoint> GetSnapPoints()
        {
            var snapPoints = new List<PipeSnapPoint>();

            var parent = Parent as Canvas;
            if (parent == null) return snapPoints;

            var pipeLeft = Canvas.GetLeft(this);
            var pipeTop = Canvas.GetTop(this);
            if (double.IsNaN(pipeLeft)) pipeLeft = 0;
            if (double.IsNaN(pipeTop)) pipeTop = 0;

            Point center = new Point(pipeLeft + ActualWidth / 2, pipeTop + ActualHeight / 2);
            double radians = _rotationAngle * Math.PI / 180.0;

            double leftFX = 4 - ActualWidth / 2;
            double leftFY = Canvas.GetTop(LeftFlangeGroup) + LeftFlangeGroup.Height / 2 - ActualHeight / 2;
            Point leftWorld = RotatePoint(new Point(leftFX, leftFY), radians);
            snapPoints.Add(new PipeSnapPoint
            {
                WorldPosition = new Point(center.X + leftWorld.X, center.Y + leftWorld.Y),
                Direction = GetSnapDirectionFromAngle(radians + Math.PI),
                Description = $"Z形管道左上法兰 (上段:{_topHorizontalLength:F0})"
            });

            double rightFX = Canvas.GetLeft(RightFlangeGroup) + 2 - ActualWidth / 2;
            double rightFY = Canvas.GetTop(RightFlangeGroup) + RightFlangeGroup.Height / 2 - ActualHeight / 2;
            Point rightWorld = RotatePoint(new Point(rightFX, rightFY), radians);
            snapPoints.Add(new PipeSnapPoint
            {
                WorldPosition = new Point(center.X + rightWorld.X, center.Y + rightWorld.Y),
                Direction = GetSnapDirectionFromAngle(radians),
                Description = $"Z形管道右下法兰 (下段:{_bottomHorizontalLength:F0})"
            });

            return snapPoints;
        }

        private Point RotatePoint(Point lp, double radians)
        {
            double cos = Math.Cos(radians);
            double sin = Math.Sin(radians);
            return new Point(lp.X * cos - lp.Y * sin, lp.X * sin + lp.Y * cos);
        }

        private SnapDirection GetSnapDirectionFromAngle(double radians)
        {
            double deg = (radians * 180 / Math.PI + 360) % 360;
            if (deg >= 315 || deg < 45) return SnapDirection.Right;
            if (deg >= 45 && deg < 135) return SnapDirection.Down;
            if (deg >= 135 && deg < 225) return SnapDirection.Left;
            return SnapDirection.Up;
        }

        #endregion
    }

    #region 辅助类和枚举

    public enum ZAnchorPosition
    {
        TopLength,
        BottomLength,
        VerticalOffset,
        PipeWidth
    }

    public class ZPipeSize
    {
        public double TopHorizontalLength { get; set; }
        public double BottomHorizontalLength { get; set; }
        public double VerticalOffset { get; set; }
        public double PipeWidth { get; set; }
    }

    public class ZPipeDragEventArgs : EventArgs
    {
        public string PipeId { get; set; }
        public Point StartPosition { get; set; }
        public Point CurrentPosition { get; set; }
        public double DeltaX { get; set; }
        public double DeltaY { get; set; }
    }

    public class ZPipeResizeEventArgs : EventArgs
    {
        public string PipeId { get; set; }
        public ZAnchorPosition AnchorPosition { get; set; }
        public ZPipeSize OriginalSize { get; set; }
        public ZPipeSize CurrentSize { get; set; }
    }

    public class ZPipeRotateEventArgs : EventArgs
    {
        public string PipeId { get; set; }
        public double OriginalAngle { get; set; }
        public double CurrentAngle { get; set; }
    }

    #endregion
}
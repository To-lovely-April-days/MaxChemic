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
    public partial class ElbowPipeControl : UserControl, IPipeSnapPoints, IPipeProperties
    {
        #region 事件定义

        public event EventHandler<ElbowPipeDragEventArgs> PipeDragStarted;
        public event EventHandler<ElbowPipeDragEventArgs> PipeDragging;
        public event EventHandler<ElbowPipeDragEventArgs> PipeDragCompleted;
        public event EventHandler<ElbowPipeResizeEventArgs> PipeResizeStarted;
        public event EventHandler<ElbowPipeResizeEventArgs> PipeResizing;
        public event EventHandler<ElbowPipeResizeEventArgs> PipeResizeCompleted;
        public event EventHandler<ElbowPipeRotateEventArgs> PipeRotateStarted;
        public event EventHandler<ElbowPipeRotateEventArgs> PipeRotating;
        public event EventHandler<ElbowPipeRotateEventArgs> PipeRotateCompleted;
        public event EventHandler<ElbowPipeRotateEventArgs> PipeRotated;
        public event EventHandler<ElbowPipeEventArgs> PipeDeleted;
        public event EventHandler<ElbowPipeEventArgs> ShowPipeProperties;
        public event EventHandler<string> PipeSelected;
        public event EventHandler<string> PipeDeselected;
        public event EventHandler<string> PipeDoubleClicked;

        #endregion

        #region 依赖属性 — 选择状态

        public static readonly DependencyProperty IsSelectedProperty =
            DependencyProperty.Register(nameof(IsSelected), typeof(bool), typeof(ElbowPipeControl),
                new PropertyMetadata(false, OnIsSelectedChanged));

        public bool IsSelected
        {
            get => (bool)GetValue(IsSelectedProperty);
            set => SetValue(IsSelectedProperty, value);
        }

        private static void OnIsSelectedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is ElbowPipeControl c) c.UpdateSelectionVisual((bool)e.NewValue);
        }

        #endregion

        #region 依赖属性 — 管道外观

        public static readonly DependencyProperty PipeColorProperty =
            DependencyProperty.Register(nameof(PipeColor), typeof(Color), typeof(ElbowPipeControl),
                new PropertyMetadata(Color.FromRgb(0x72, 0x71, 0x71), OnPipeColorChanged));

        public Color PipeColor
        {
            get => (Color)GetValue(PipeColorProperty);
            set => SetValue(PipeColorProperty, value);
        }

        private static void OnPipeColorChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is ElbowPipeControl c) c.ApplyPipeColor();
        }

        public static readonly DependencyProperty FlangeColorProperty =
            DependencyProperty.Register(nameof(FlangeColor), typeof(Color), typeof(ElbowPipeControl),
                new PropertyMetadata(Color.FromRgb(0xD9, 0xD9, 0xD9), OnFlangeColorChanged));

        public Color FlangeColor
        {
            get => (Color)GetValue(FlangeColorProperty);
            set => SetValue(FlangeColorProperty, value);
        }

        private static void OnFlangeColorChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is ElbowPipeControl c) c.ApplyFlangeColor();
        }

        #endregion

        #region 依赖属性 — 流体

        public static readonly DependencyProperty FluidColorProperty =
            DependencyProperty.Register(nameof(FluidColor), typeof(Color), typeof(ElbowPipeControl),
                new PropertyMetadata(Color.FromRgb(0x1E, 0xC1, 0xF4), OnFluidColorChanged));

        public Color FluidColor
        {
            get => (Color)GetValue(FluidColorProperty);
            set => SetValue(FluidColorProperty, value);
        }

        private static void OnFluidColorChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is ElbowPipeControl c) c.ApplyFluidColor();
        }

        public static readonly DependencyProperty FluidOpacityProperty =
            DependencyProperty.Register(nameof(FluidOpacity), typeof(double), typeof(ElbowPipeControl),
                new PropertyMetadata(0.5, OnFluidOpacityChanged));

        public double FluidOpacity
        {
            get => (double)GetValue(FluidOpacityProperty);
            set => SetValue(FluidOpacityProperty, value);
        }

        private static void OnFluidOpacityChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is ElbowPipeControl c) c.ApplyFluidOpacity();
        }

        public static readonly DependencyProperty IsFluidVisibleProperty =
            DependencyProperty.Register(nameof(IsFluidVisible), typeof(bool), typeof(ElbowPipeControl),
                new PropertyMetadata(false, OnIsFluidVisibleChanged));

        public bool IsFluidVisible
        {
            get => (bool)GetValue(IsFluidVisibleProperty);
            set => SetValue(IsFluidVisibleProperty, value);
        }

        private static void OnIsFluidVisibleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is ElbowPipeControl c) c.ApplyFluidVisibility();
        }

        // ═══ 默认流动样式（赛博流光） ═══
        public static readonly DependencyProperty UseDefaultStyleProperty =
            DependencyProperty.Register(nameof(UseDefaultStyle), typeof(bool), typeof(ElbowPipeControl),
                new PropertyMetadata(false, OnUseDefaultStyleChanged));

        public bool UseDefaultStyle
        {
            get => (bool)GetValue(UseDefaultStyleProperty);
            set => SetValue(UseDefaultStyleProperty, value);
        }

        private static void OnUseDefaultStyleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is ElbowPipeControl c) c.ApplyDefaultStyleSwitch();
        }

        #endregion

        #region 依赖属性 — 流动控制

        public static readonly DependencyProperty IsFlowingProperty =
            DependencyProperty.Register(nameof(IsFlowing), typeof(bool), typeof(ElbowPipeControl),
                new PropertyMetadata(false, OnIsFlowingChanged));

        public bool IsFlowing
        {
            get => (bool)GetValue(IsFlowingProperty);
            set => SetValue(IsFlowingProperty, value);
        }

        private static void OnIsFlowingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is ElbowPipeControl c) c.ApplyFlowAnimation();
        }

        public static readonly DependencyProperty PipeFlowDirProperty =
            DependencyProperty.Register(nameof(PipeFlowDir), typeof(PipeFlowDirection), typeof(ElbowPipeControl),
                new PropertyMetadata(PipeFlowDirection.LeftToRight, OnPipeFlowDirChanged));

        public PipeFlowDirection PipeFlowDir
        {
            get => (PipeFlowDirection)GetValue(PipeFlowDirProperty);
            set => SetValue(PipeFlowDirProperty, value);
        }

        private static void OnPipeFlowDirChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is ElbowPipeControl c) c.ApplyFlowAnimation();
        }

        public static readonly DependencyProperty FlowSpeedProperty =
            DependencyProperty.Register(nameof(FlowSpeed), typeof(double), typeof(ElbowPipeControl),
                new PropertyMetadata(1.0, OnFlowSpeedChanged));

        public double FlowSpeed
        {
            get => (double)GetValue(FlowSpeedProperty);
            set => SetValue(FlowSpeedProperty, value);
        }

        private static void OnFlowSpeedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is ElbowPipeControl c) c.ApplyFlowAnimation();
        }

        #endregion

        #region 私有字段

        public string PipeId { get; set; }

        private bool _isDraggingPipe;
        private bool _isResizing;
        private bool _isRotating;
        private ElbowAnchorPosition? _resizingAnchor;
        private Point _dragStartPoint;
        private Point _originalPosition;
        private ElbowPipeSize _originalSize;
        private double _originalRotation;
        private Point _fixedPoint;

        private double _horizontalLength = 50;
        private double _verticalLength = 60;
        private double _pipeWidth = 20;
        private double _rotationAngle;

        private const double MinArmLength = 15;
        private const double MaxArmLength = 10000;
        private const double MinPipeWidth = 4;
        private const double MaxPipeWidth = 10000;
        private const double FlangeWidth = 2.54;
        private const double FlangeOffset = 0.35;
        private const double FLANGE_EXTENSION = 5.8;

        private RotateTransform _rotateTransform;
        private TransformGroup _transformGroup;
        private ScaleTransform _flipTransform;
        private DispatcherTimer _rotationLabelTimer;

        // 原气泡流体动画
        private TranslateTransform _horizontalFlowTransform;
        private TranslateTransform _verticalFlowTransform;
        private DrawingBrush _horizontalPatternBrush;
        private DrawingBrush _verticalPatternBrush;

        // ═══ 赛博流光 ═══
        private DoubleAnimation _cyberMainAnim;
        private DoubleAnimation _cyberParticleAnim;
        private DoubleAnimation _cyberSparkAnim;
        // 周期适配整条 L 形长度（运行时根据实际长度计算）
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

        public double HorizontalLength
        {
            get => _horizontalLength;
            set
            {
                _horizontalLength = Math.Max(MinArmLength, Math.Min(MaxArmLength, value));
                UpdatePipeGeometry();
            }
        }

        public double VerticalLength
        {
            get => _verticalLength;
            set
            {
                _verticalLength = Math.Max(MinArmLength, Math.Min(MaxArmLength, value));
                UpdatePipeGeometry();
            }
        }

        public double PipeWidth
        {
            get => _pipeWidth;
            set
            {
                _pipeWidth = Math.Max(MinPipeWidth, Math.Min(MaxPipeWidth, value));
                UpdatePipeGeometry();
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
            }
        }

        #endregion

        #region 构造函数

        public ElbowPipeControl()
        {
            InitializeComponent();
            PipeId = Guid.NewGuid().ToString();
            DataContext = this;

            _rotateTransform = new RotateTransform(0);
            _flipTransform = new ScaleTransform(1, 1);
            _transformGroup = new TransformGroup();
            _transformGroup.Children.Add(_flipTransform);
            _transformGroup.Children.Add(_rotateTransform);
            RenderTransform = _transformGroup;
            RenderTransformOrigin = new Point(0.5, 0.5);

            InitializeAppearance();

            if (!DesignerProperties.GetIsInDesignMode(this))
            {
                _rotationLabelTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                _rotationLabelTimer.Tick += (s, e) =>
                {
                    RotationLabel.Visibility = Visibility.Collapsed;
                    _rotationLabelTimer.Stop();
                };

                MouseEnter += OnPipeMouseEnter;
                MouseLeave += OnPipeMouseLeave;
                MouseDown += OnControlMouseDown;
                MouseMove += OnControlMouseMove;
                MouseUp += OnControlMouseUp;
                MouseLeave += OnControlMouseLeave;
                SetupAnchorEvents();
                UpdatePipeGeometry();
            }

            Loaded += (s, e) =>
            {
                UpdatePipeGeometry();
                ApplyFlowAnimation();
            };

            Dispatcher.BeginInvoke(new Action(() => UpdatePipeGeometry()),
                DispatcherPriority.Loaded);

            RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
            RenderOptions.SetEdgeMode(this, EdgeMode.Unspecified);
            RenderOptions.SetCachingHint(this, CachingHint.Cache);
            RenderOptions.SetCacheInvalidationThresholdMinimum(this, 0.5);
            RenderOptions.SetCacheInvalidationThresholdMaximum(this, 2.0);

            CacheMode = new BitmapCache
            {
                RenderAtScale = 1.0,
                SnapsToDevicePixels = true,
                EnableClearType = false
            };

            SnapsToDevicePixels = true;
            UseLayoutRounding = true;

            ElbowPath.CacheMode = null;
            ElbowOuterEdge.CacheMode = null;
            ElbowHighlight.CacheMode = null;
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

        #region 管道外观 — 管体渐变

        private void ApplyPipeColor()
        {
            if (HorizontalPipe == null) return;

            var c = PipeColor;

            var hGrad = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
            hGrad.GradientStops.Add(new GradientStop(c, 0));
            hGrad.GradientStops.Add(new GradientStop(Color.FromArgb(0x59, c.R, c.G, c.B), 0.264));
            hGrad.GradientStops.Add(new GradientStop(Color.FromArgb(0x1A, c.R, c.G, c.B), 0.418));
            hGrad.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 0.498));
            hGrad.GradientStops.Add(new GradientStop(Color.FromArgb(0x1A, c.R, c.G, c.B), 0.581));
            hGrad.GradientStops.Add(new GradientStop(Color.FromArgb(0x59, c.R, c.G, c.B), 0.719));
            hGrad.GradientStops.Add(new GradientStop(c, 1));
            hGrad.Freeze();
            HorizontalPipe.Fill = hGrad;

            var vGrad = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
            vGrad.GradientStops.Add(new GradientStop(c, 0));
            vGrad.GradientStops.Add(new GradientStop(Color.FromArgb(0x59, c.R, c.G, c.B), 0.264));
            vGrad.GradientStops.Add(new GradientStop(Color.FromArgb(0x1A, c.R, c.G, c.B), 0.418));
            vGrad.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 0.498));
            vGrad.GradientStops.Add(new GradientStop(Color.FromArgb(0x1A, c.R, c.G, c.B), 0.581));
            vGrad.GradientStops.Add(new GradientStop(Color.FromArgb(0x59, c.R, c.G, c.B), 0.719));
            vGrad.GradientStops.Add(new GradientStop(c, 1));
            vGrad.Freeze();
            VerticalPipe.Fill = vGrad;

            var darkC = DarkenColor(c, 25);
            var lightC = LightenColor(c, 25);
            var eGrad = new LinearGradientBrush { StartPoint = new Point(1, 1), EndPoint = new Point(0, 0) };
            eGrad.GradientStops.Add(new GradientStop(darkC, 0));
            eGrad.GradientStops.Add(new GradientStop(c, 0.22));
            eGrad.GradientStops.Add(new GradientStop(Color.FromArgb(0x40, c.R, c.G, c.B), 0.40));
            eGrad.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 0.50));
            eGrad.GradientStops.Add(new GradientStop(Color.FromArgb(0x40, c.R, c.G, c.B), 0.60));
            eGrad.GradientStops.Add(new GradientStop(c, 0.78));
            eGrad.GradientStops.Add(new GradientStop(lightC, 1));
            eGrad.Freeze();
            ElbowPath.Fill = eGrad;

            var strokeBrush = new SolidColorBrush(c);
            strokeBrush.Freeze();
            ElbowPath.Stroke = strokeBrush;

            var outerEdgeStroke = new LinearGradientBrush { StartPoint = new Point(1, 1), EndPoint = new Point(0, 0) };
            outerEdgeStroke.GradientStops.Add(new GradientStop(DarkenColor(c, 50), 0));
            outerEdgeStroke.GradientStops.Add(new GradientStop(DarkenColor(c, 25), 0.5));
            outerEdgeStroke.GradientStops.Add(new GradientStop(Colors.Transparent, 1));
            outerEdgeStroke.Freeze();
            ElbowOuterEdge.Stroke = outerEdgeStroke;

            var highlightStroke = new LinearGradientBrush { StartPoint = new Point(1, 1), EndPoint = new Point(0, 0) };
            highlightStroke.GradientStops.Add(new GradientStop(Colors.Transparent, 0));
            highlightStroke.GradientStops.Add(new GradientStop(LightenColor(c, 100), 0.5));
            highlightStroke.GradientStops.Add(new GradientStop(LightenColor(c, 120), 1));
            highlightStroke.Freeze();
            ElbowHighlight.Stroke = highlightStroke;

            LeftFlangeBorder.Stroke = strokeBrush;
            TopFlangeBorder.Stroke = strokeBrush;

            LeftConnection.Fill = new SolidColorBrush(c);
            TopConnection.Fill = new SolidColorBrush(c);

            RebuildFluidPatternBrushes();
        }

        #endregion

        #region 管道外观 — 法兰颜色

        private void ApplyFlangeColor()
        {
            if (LeftFlangeBase == null) return;

            var c = FlangeColor;
            var baseBrush = new SolidColorBrush(c);
            baseBrush.Freeze();

            LeftFlangeBase.Fill = baseBrush;
            TopFlangeBase.Fill = baseBrush;

            var midColor = BlendColors(c, Colors.Gray, 0.4);

            var leftGrad = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
            leftGrad.GradientStops.Add(new GradientStop(c, 0));
            leftGrad.GradientStops.Add(new GradientStop(midColor, 0.5));
            leftGrad.GradientStops.Add(new GradientStop(c, 1));
            leftGrad.Freeze();
            LeftFlangeGradient.Fill = leftGrad;

            var topGrad = new LinearGradientBrush { StartPoint = new Point(0.5, 0), EndPoint = new Point(0.5, 1) };
            topGrad.GradientStops.Add(new GradientStop(c, 0));
            topGrad.GradientStops.Add(new GradientStop(midColor, 0.5));
            topGrad.GradientStops.Add(new GradientStop(c, 1));
            topGrad.Freeze();
            TopFlangeGradient.Fill = topGrad;
        }

        #endregion

        #region 流体 — 颜色、透明度与可见性

        private void ApplyFluidColor()
        {
            if (HorizontalFluidBackground == null) return;

            var brush = new SolidColorBrush(FluidColor);

            HorizontalFluidBackground.Fill = brush;
            VerticalFluidBackground.Fill = brush;
            ElbowFluidBackground.Fill = brush;

            ApplyFluidOpacity();

            // ★ 赛博样式下流光颜色跟着变
            if (UseDefaultStyle && CyberMainStream != null)
                ApplyCyberStreamColors();
        }

        private void ApplyFluidOpacity()
        {
            if (HorizontalFluidBackground == null) return;

            HorizontalFluidBackground.Opacity = FluidOpacity;
            VerticalFluidBackground.Opacity = FluidOpacity;
            ElbowFluidBackground.Opacity = FluidOpacity;
        }

        private void ApplyFluidVisibility()
        {
            if (HorizontalFluidBackground == null) return;

            // ★ 赛博样式下原气泡层永远隐藏
            if (UseDefaultStyle)
            {
                HorizontalFluidBackground.Visibility = Visibility.Collapsed;
                HorizontalFluidPattern.Visibility = Visibility.Collapsed;
                VerticalFluidBackground.Visibility = Visibility.Collapsed;
                VerticalFluidPattern.Visibility = Visibility.Collapsed;
                ElbowFluidBackground.Visibility = Visibility.Collapsed;
                return;
            }

            var vis = IsFluidVisible ? Visibility.Visible : Visibility.Collapsed;

            HorizontalFluidBackground.Visibility = vis;
            HorizontalFluidPattern.Visibility = vis;
            VerticalFluidBackground.Visibility = vis;
            VerticalFluidPattern.Visibility = vis;
            ElbowFluidBackground.Visibility = vis;

            if (IsFluidVisible && IsFlowing)
                StartFlowAnimation();
            else
                StopFlowAnimation();
        }

        #endregion

        #region 流体 — 气泡纹理画刷

        private void RebuildFluidPatternBrushes()
        {
            if (HorizontalFluidPattern == null) return;

            var pipeC = PipeColor;

            var hDrawing = CreateBubbleDrawing(pipeC, false);
            _horizontalFlowTransform = new TranslateTransform(0, 0);
            _horizontalPatternBrush = new DrawingBrush(hDrawing)
            {
                TileMode = TileMode.Tile,
                Viewbox = new Rect(0, 0, PATTERN_W, PATTERN_H),
                ViewboxUnits = BrushMappingMode.Absolute,
                ViewportUnits = BrushMappingMode.Absolute,
                Transform = _horizontalFlowTransform
            };
            UpdateFluidPatternViewports();
            HorizontalFluidPattern.Fill = _horizontalPatternBrush;

            var vDrawing = CreateBubbleDrawing(pipeC, true);
            _verticalFlowTransform = new TranslateTransform(0, 0);
            _verticalPatternBrush = new DrawingBrush(vDrawing)
            {
                TileMode = TileMode.Tile,
                Viewbox = new Rect(0, 0, PATTERN_W, PATTERN_H),
                ViewboxUnits = BrushMappingMode.Absolute,
                ViewportUnits = BrushMappingMode.Absolute,
                Transform = _verticalFlowTransform
            };
            UpdateFluidPatternViewports();
            VerticalFluidPattern.Fill = _verticalPatternBrush;

            if (IsFlowing && IsFluidVisible && !UseDefaultStyle)
                StartFlowAnimation();
        }

        private DrawingGroup CreateBubbleDrawing(Color pipeC, bool isVertical)
        {
            var group = new DrawingGroup();

            foreach (var (x, y, r) in BubbleData)
            {
                var radial = new RadialGradientBrush
                {
                    GradientOrigin = new Point(0.3, 0.3),
                    Center = new Point(0.5, 0.5)
                };
                radial.GradientStops.Add(new GradientStop(Color.FromArgb(102, 255, 255, 255), 0));
                radial.GradientStops.Add(new GradientStop(Color.FromArgb(38, 0, 0, 0), 1));
                radial.Freeze();

                group.Children.Add(new GeometryDrawing(
                    radial, null,
                    new EllipseGeometry(new Point(x, y), r, r)));
            }

            // ★ 去掉边带 — 之前边带占 11% 上 + 11% 下,管子细的时候把气泡挤到中间一条,
            //   导致设了管道颜色后视觉上气泡跟管壁融成一片看不出动静
            //   去掉后气泡可以用整个管道高度,无论管道什么颜色都明显

            return group;
        }

        private void UpdateFluidPatternViewports()
        {
            // ★ 气泡尺寸放大 1.5 倍 — 全局视图下也能清晰看见气泡流动
            const double BUBBLE_SCALE = 1.5;
            double tileH = _pipeWidth * BUBBLE_SCALE;
            double tileW = tileH * (PATTERN_W / PATTERN_H);

            if (_horizontalPatternBrush != null)
                _horizontalPatternBrush.Viewport = new Rect(0, 0, tileW, tileH);

            if (_verticalPatternBrush != null)
                _verticalPatternBrush.Viewport = new Rect(0, 0, tileW, tileH);
        }

        #endregion

        #region 流体 — 流动动画（原气泡）

        private void ApplyFlowAnimation()
        {
            // ★ 赛博样式走自己的动画
            if (UseDefaultStyle)
            {
                if (IsFlowing)
                    StartCyberAnimation();
                else
                    StopCyberAnimation();
                return;
            }

            if (IsFlowing && IsFluidVisible)
                StartFlowAnimation();
            else
                StopFlowAnimation();
        }

        private void StartFlowAnimation()
        {
            if (_horizontalFlowTransform == null || _verticalFlowTransform == null) return;

            _horizontalFlowTransform.BeginAnimation(TranslateTransform.XProperty, null);
            _verticalFlowTransform.BeginAnimation(TranslateTransform.YProperty, null);

            // ★ 关键修复:滚动距离用 PATTERN_W (=172) 而不是 tileW (≈48)
            //   这跟原版 ThingsBoard SVG 完全一致 — 内层 rect 滚 172 像素重置
            //   原来用 tileW 时,管子细 → 滚动距离短 → 视觉上"看不到在动"
            //   PATTERN_W 是固定值,不随管粗变化,流动感强烈明显
            // ★ 速度调慢:基础时长 2000ms 而不是 1000ms — 让气泡流动更舒缓
            double baseDurationMs = 2000.0 / Math.Max(0.1, FlowSpeed);

            double hTo = PipeFlowDir == PipeFlowDirection.LeftToRight ? PATTERN_W : -PATTERN_W;
            var hAnim = new DoubleAnimation
            {
                From = 0,
                To = hTo,
                Duration = TimeSpan.FromMilliseconds(baseDurationMs),
                RepeatBehavior = RepeatBehavior.Forever
            };
            _horizontalFlowTransform.BeginAnimation(TranslateTransform.XProperty, hAnim);

            // 垂直段同样用 PATTERN_W,跟水平段同步速率
            double vTo = PipeFlowDir == PipeFlowDirection.LeftToRight ? -PATTERN_W : PATTERN_W;
            var vAnim = new DoubleAnimation
            {
                From = 0,
                To = vTo,
                Duration = TimeSpan.FromMilliseconds(baseDurationMs),
                RepeatBehavior = RepeatBehavior.Forever
            };
            _verticalFlowTransform.BeginAnimation(TranslateTransform.YProperty, vAnim);
        }

        private void StopFlowAnimation()
        {
            if (_horizontalFlowTransform != null)
            {
                _horizontalFlowTransform.BeginAnimation(TranslateTransform.XProperty, null);
                _horizontalFlowTransform.X = 0;
            }
            if (_verticalFlowTransform != null)
            {
                _verticalFlowTransform.BeginAnimation(TranslateTransform.YProperty, null);
                _verticalFlowTransform.Y = 0;
            }
        }

        #endregion

        #region 赛博流光（默认流动样式）

        private void ApplyDefaultStyleSwitch()
        {
            if (CyberMainStream == null) return;

            if (UseDefaultStyle)
            {
                // 隐藏原层
                if (HorizontalPipe != null) HorizontalPipe.Visibility = Visibility.Collapsed;
                if (VerticalPipe != null) VerticalPipe.Visibility = Visibility.Collapsed;
                if (ElbowPath != null) ElbowPath.Visibility = Visibility.Collapsed;
                if (ElbowOuterEdge != null) ElbowOuterEdge.Visibility = Visibility.Collapsed;
                if (ElbowHighlight != null) ElbowHighlight.Visibility = Visibility.Collapsed;
                // ★ Background 层不隐藏,改成透明 fill —— 这样它仍能接住鼠标命中(双击/拖拽)
                //   赛博金属层在它之上,视觉上还是赛博效果
                if (HorizontalPipeBackground != null) { HorizontalPipeBackground.Fill = Brushes.Transparent; HorizontalPipeBackground.Visibility = Visibility.Visible; }
                if (VerticalPipeBackground != null) { VerticalPipeBackground.Fill = Brushes.Transparent; VerticalPipeBackground.Visibility = Visibility.Visible; }
                if (ElbowPipeBackground != null) { ElbowPipeBackground.Fill = Brushes.Transparent; ElbowPipeBackground.Visibility = Visibility.Visible; }
                if (HorizontalFluidBackground != null) HorizontalFluidBackground.Visibility = Visibility.Collapsed;
                if (VerticalFluidBackground != null) VerticalFluidBackground.Visibility = Visibility.Collapsed;
                if (ElbowFluidBackground != null) ElbowFluidBackground.Visibility = Visibility.Collapsed;
                if (HorizontalFluidPattern != null) HorizontalFluidPattern.Visibility = Visibility.Collapsed;
                if (VerticalFluidPattern != null) VerticalFluidPattern.Visibility = Visibility.Collapsed;

                BuildCyberLayers();

                CyberHorizontalMetal.Visibility = Visibility.Visible;
                CyberVerticalMetal.Visibility = Visibility.Visible;
                CyberElbowMetal.Visibility = Visibility.Visible;
                CyberHorizontalDark.Visibility = Visibility.Visible;
                CyberVerticalDark.Visibility = Visibility.Visible;
                CyberElbowDark.Visibility = Visibility.Visible;
                CyberMainStream.Visibility = Visibility.Visible;
                CyberParticleStream.Visibility = Visibility.Visible;
                CyberSparkStream.Visibility = Visibility.Visible;

                if (IsFlowing) StartCyberAnimation();
                else StopCyberAnimation();

                StopFlowAnimation();
            }
            else
            {
                CyberHorizontalMetal.Visibility = Visibility.Collapsed;
                CyberVerticalMetal.Visibility = Visibility.Collapsed;
                CyberElbowMetal.Visibility = Visibility.Collapsed;
                CyberHorizontalDark.Visibility = Visibility.Collapsed;
                CyberVerticalDark.Visibility = Visibility.Collapsed;
                CyberElbowDark.Visibility = Visibility.Collapsed;
                CyberMainStream.Visibility = Visibility.Collapsed;
                CyberParticleStream.Visibility = Visibility.Collapsed;
                CyberSparkStream.Visibility = Visibility.Collapsed;
                StopCyberAnimation();

                if (HorizontalPipe != null) HorizontalPipe.Visibility = Visibility.Visible;
                if (VerticalPipe != null) VerticalPipe.Visibility = Visibility.Visible;
                if (ElbowPath != null) ElbowPath.Visibility = Visibility.Visible;
                if (ElbowOuterEdge != null) ElbowOuterEdge.Visibility = Visibility.Visible;
                if (ElbowHighlight != null) ElbowHighlight.Visibility = Visibility.Visible;
                // ★ 还原 Background 白底
                if (HorizontalPipeBackground != null) { HorizontalPipeBackground.Fill = Brushes.White; HorizontalPipeBackground.Visibility = Visibility.Visible; }
                if (VerticalPipeBackground != null) { VerticalPipeBackground.Fill = Brushes.White; VerticalPipeBackground.Visibility = Visibility.Visible; }
                if (ElbowPipeBackground != null) { ElbowPipeBackground.Fill = Brushes.White; ElbowPipeBackground.Visibility = Visibility.Visible; }
                ApplyFluidVisibility();
            }
        }

        private void BuildCyberLayers()
        {
            if (CyberHorizontalMetal == null) return;

            // ── 金属管壁渐变 ──
            var hMetalGrad = MakeMetalGradient(false);
            CyberHorizontalMetal.Fill = hMetalGrad;

            var vMetalGrad = MakeMetalGradient(true);
            CyberVerticalMetal.Fill = vMetalGrad;

            // 弯头金属用径向偏一点的渐变（外暗内亮）
            var elbowMetal = new LinearGradientBrush { StartPoint = new Point(1, 1), EndPoint = new Point(0, 0) };
            elbowMetal.GradientStops.Add(new GradientStop(Color.FromRgb(0x1A, 0x20, 0x30), 0));
            elbowMetal.GradientStops.Add(new GradientStop(Color.FromRgb(0x4A, 0x5A, 0x7A), 0.4));
            elbowMetal.GradientStops.Add(new GradientStop(Color.FromRgb(0x8A, 0xA0, 0xC0), 0.6));
            elbowMetal.GradientStops.Add(new GradientStop(Color.FromRgb(0x4A, 0x5A, 0x7A), 0.8));
            elbowMetal.GradientStops.Add(new GradientStop(Color.FromRgb(0x1A, 0x20, 0x30), 1));
            elbowMetal.Freeze();
            CyberElbowMetal.Fill = elbowMetal;

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
            var darkC = DarkenColor(c, 60);
            var brightC = LightenColor(c, 30);
            var sparkC = LightenColor(c, 60);

            // 主流光：用纯色而不是渐变（path 横跨整条 L 形，渐变会扭曲）
            var mainBrush = new SolidColorBrush(brightC);
            mainBrush.Freeze();
            CyberMainStream.Stroke = mainBrush;

            var particleBrush = new SolidColorBrush(sparkC);
            particleBrush.Freeze();
            CyberParticleStream.Stroke = particleBrush;
            // CyberSparkStream 保持白色
        }

        /// <summary>
        /// 同步赛博层尺寸 + 构造 L 形流光路径（关键：path 自然顺着弯头转弯）
        /// </summary>
        private void UpdateCyberGeometry()
        {
            if (CyberHorizontalMetal == null) return;

            double elbowRadius = _pipeWidth;
            double horizontalCenterY = Height - 10 - _pipeWidth / 2;
            double verticalCenterX = Width - 10 - _pipeWidth / 2;
            double elbowCenterX = verticalCenterX;
            double elbowCenterY = horizontalCenterY;

            // 水平段尺寸
            double hPipeLeft = FlangeOffset + FlangeWidth;
            double hPipeWidth = Math.Max(0, elbowCenterX - elbowRadius - hPipeLeft);
            double hPipeTop = horizontalCenterY - _pipeWidth / 2;

            CyberHorizontalMetal.Width = hPipeWidth;
            CyberHorizontalMetal.Height = _pipeWidth;
            Canvas.SetLeft(CyberHorizontalMetal, hPipeLeft);
            Canvas.SetTop(CyberHorizontalMetal, hPipeTop);

            CyberHorizontalDark.Width = hPipeWidth;
            CyberHorizontalDark.Height = _pipeWidth;
            Canvas.SetLeft(CyberHorizontalDark, hPipeLeft);
            Canvas.SetTop(CyberHorizontalDark, hPipeTop);

            // 垂直段尺寸
            double vPipeTop = FlangeOffset + FlangeWidth;
            double vPipeHeight = Math.Max(0, elbowCenterY - elbowRadius - vPipeTop);
            double vPipeLeft = verticalCenterX - _pipeWidth / 2;

            CyberVerticalMetal.Width = _pipeWidth;
            CyberVerticalMetal.Height = vPipeHeight;
            Canvas.SetLeft(CyberVerticalMetal, vPipeLeft);
            Canvas.SetTop(CyberVerticalMetal, vPipeTop);

            CyberVerticalDark.Width = _pipeWidth;
            CyberVerticalDark.Height = vPipeHeight;
            Canvas.SetLeft(CyberVerticalDark, vPipeLeft);
            Canvas.SetTop(CyberVerticalDark, vPipeTop);

            // 弯头金属/暗腔（复用原弯头几何）
            if (ElbowPath != null && ElbowPath.Data != null)
            {
                CyberElbowMetal.Data = ElbowPath.Data;
                CyberElbowDark.Data = ElbowPath.Data;
            }

            // ★ L 形中心线流光 path：水平 → 弯头弧线 → 垂直
            // 流体流向：LeftToRight = 从左法兰流向上法兰
            //   起点：水平段左端（左法兰内侧）中点
            //   终点：垂直段顶端（上法兰内侧）中点
            //   中间：用 ArcSegment 走弯头中线
            double startX = hPipeLeft;
            double startY = horizontalCenterY;
            double cornerX = elbowCenterX - elbowRadius;  // 弯头入口（水平段尽头中心）
            double cornerY = horizontalCenterY;

            double arcEndX = elbowCenterX;
            double arcEndY = elbowCenterY - elbowRadius;  // 弯头出口（垂直段尽头中心）

            double endX = verticalCenterX;
            double endY = vPipeTop;

            var fig = new PathFigure { StartPoint = new Point(startX, startY) };
            // 水平段直线
            fig.Segments.Add(new LineSegment(new Point(cornerX, cornerY), true));
            // 弯头弧线（半径 = 弯头中心到管中线的距离 = elbowRadius）
            fig.Segments.Add(new ArcSegment(
                new Point(arcEndX, arcEndY),
                new Size(elbowRadius, elbowRadius),
                0, false, SweepDirection.Counterclockwise, true));
            // 垂直段直线
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

            // 总长度 = 水平段 + 1/4 弧周长 + 垂直段（用于动画周期，但 dasharray 是固定节奏所以不用精确）
        }

        private void StartCyberAnimation()
        {
            if (CyberMainStream == null) return;

            StopCyberAnimation();

            int sign = PipeFlowDir == PipeFlowDirection.LeftToRight ? -1 : 1;
            double speed = Math.Max(0.1, FlowSpeed);

            _cyberMainAnim = new DoubleAnimation
            {
                From = 0,
                To = sign * _cyberMainPeriod,
                Duration = TimeSpan.FromMilliseconds(2000.0 / speed),
                RepeatBehavior = RepeatBehavior.Forever
            };
            CyberMainStream.BeginAnimation(Path.StrokeDashOffsetProperty, _cyberMainAnim);

            _cyberParticleAnim = new DoubleAnimation
            {
                From = 0,
                To = sign * _cyberParticlePeriod,
                Duration = TimeSpan.FromMilliseconds(1500.0 / speed),
                RepeatBehavior = RepeatBehavior.Forever
            };
            CyberParticleStream.BeginAnimation(Path.StrokeDashOffsetProperty, _cyberParticleAnim);

            _cyberSparkAnim = new DoubleAnimation
            {
                From = sign * -30,
                To = sign * (_cyberSparkPeriod - 30),
                Duration = TimeSpan.FromMilliseconds(1200.0 / speed),
                RepeatBehavior = RepeatBehavior.Forever
            };
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

        #region 辅助颜色

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

        #region 锚点显示控制

        private void OnPipeMouseEnter(object sender, MouseEventArgs e)
        {
            if (!_isResizing && !_isDraggingPipe && !_isRotating) ShowAllAnchors();
        }

        private void OnPipeMouseLeave(object sender, MouseEventArgs e)
        {
            if (!_isResizing && !_isRotating && !IsSelected) HideAllAnchors();
        }

        private void ShowAllAnchors()
        {
            LeftAnchor.Opacity = 0.6;
            TopAnchor.Opacity = 0.6;
            HorizontalWidthAnchor.Opacity = 0.6;
            VerticalWidthAnchor.Opacity = 0.6;
            RotateAnchor.Opacity = 0.6;
            RotateAnchorLine.Opacity = 0.6;
        }

        private void HideAllAnchors()
        {
            LeftAnchor.Opacity = 0;
            TopAnchor.Opacity = 0;
            HorizontalWidthAnchor.Opacity = 0;
            VerticalWidthAnchor.Opacity = 0;
            RotateAnchor.Opacity = 0;
            RotateAnchorLine.Opacity = 0;
        }

        private void UpdateSelectionVisual(bool isSelected)
        {
            if (isSelected)
            {
                SelectionBorder.Visibility = Visibility.Visible;
                SelectionBorderV.Visibility = Visibility.Visible;
                ShowAllAnchors();
            }
            else
            {
                SelectionBorder.Visibility = Visibility.Collapsed;
                SelectionBorderV.Visibility = Visibility.Collapsed;
                if (!IsMouseOver && !_isResizing && !_isRotating) HideAllAnchors();
            }
        }

        #endregion

        #region 右键菜单事件处理

        private void RotateClockwise15_Click(object sender, RoutedEventArgs e) => RotatePipe(15);
        private void RotateClockwise30_Click(object sender, RoutedEventArgs e) => RotatePipe(30);
        private void RotateClockwise45_Click(object sender, RoutedEventArgs e) => RotatePipe(45);
        private void RotateClockwise90_Click(object sender, RoutedEventArgs e) => RotatePipe(90);
        private void Rotate180_Click(object sender, RoutedEventArgs e) => RotatePipe(180);
        private void RotateCounterClockwise15_Click(object sender, RoutedEventArgs e) => RotatePipe(-15);
        private void RotateCounterClockwise30_Click(object sender, RoutedEventArgs e) => RotatePipe(-30);
        private void RotateCounterClockwise45_Click(object sender, RoutedEventArgs e) => RotatePipe(-45);
        private void RotateCounterClockwise90_Click(object sender, RoutedEventArgs e) => RotatePipe(-90);

        private void ResetRotation_Click(object sender, RoutedEventArgs e)
        {
            double oldAngle = _rotationAngle;
            RotationAngle = 0;
            PipeRotated?.Invoke(this, new ElbowPipeRotateEventArgs
            {
                PipeId = PipeId,
                OriginalAngle = oldAngle,
                CurrentAngle = _rotationAngle
            });
            ShowRotationLabel();
        }

        private void FlipHorizontal_Click(object sender, RoutedEventArgs e) => _flipTransform.ScaleX *= -1;
        private void FlipVertical_Click(object sender, RoutedEventArgs e) => _flipTransform.ScaleY *= -1;

        private void ShowProperties_Click(object sender, RoutedEventArgs e)
        {
            ShowPipeProperties?.Invoke(this, new ElbowPipeEventArgs { PipeId = PipeId });
        }

        private void DeletePipe_Click(object sender, RoutedEventArgs e)
        {
            PipeDeleted?.Invoke(this, new ElbowPipeEventArgs { PipeId = PipeId });
        }

        private void RotatePipe(double deltaAngle)
        {
            double oldAngle = _rotationAngle;
            RotationAngle = _rotationAngle + deltaAngle;
            PipeRotated?.Invoke(this, new ElbowPipeRotateEventArgs
            {
                PipeId = PipeId,
                OriginalAngle = oldAngle,
                CurrentAngle = _rotationAngle
            });
            ShowRotationLabel();
        }

        private void ShowRotationLabel()
        {
            RotationLabel.Visibility = Visibility.Visible;
            UpdateRotationLabelPosition();
            _rotationLabelTimer.Stop();
            _rotationLabelTimer.Start();
        }

        private void UpdateRotationLabelPosition()
        {
            double centerX = Width / 2;
            Canvas.SetLeft(RotationLabel, centerX - 20);
            Canvas.SetTop(RotationLabel, -50);
        }

        #endregion

        #region 锚点事件设置

        private void SetupAnchorEvents()
        {
            LeftAnchor.MouseEnter += OnAnchorMouseEnter;
            LeftAnchor.MouseLeave += OnAnchorMouseLeave;
            LeftAnchor.MouseDown += (s, e) => OnAnchorMouseDown(ElbowAnchorPosition.Left, e);

            TopAnchor.MouseEnter += OnAnchorMouseEnter;
            TopAnchor.MouseLeave += OnAnchorMouseLeave;
            TopAnchor.MouseDown += (s, e) => OnAnchorMouseDown(ElbowAnchorPosition.Top, e);

            HorizontalWidthAnchor.MouseEnter += OnAnchorMouseEnter;
            HorizontalWidthAnchor.MouseLeave += OnAnchorMouseLeave;
            HorizontalWidthAnchor.MouseDown += (s, e) => OnAnchorMouseDown(ElbowAnchorPosition.HorizontalWidth, e);

            VerticalWidthAnchor.MouseEnter += OnAnchorMouseEnter;
            VerticalWidthAnchor.MouseLeave += OnAnchorMouseLeave;
            VerticalWidthAnchor.MouseDown += (s, e) => OnAnchorMouseDown(ElbowAnchorPosition.VerticalWidth, e);

            RotateAnchor.MouseEnter += OnRotateAnchorMouseEnter;
            RotateAnchor.MouseLeave += OnRotateAnchorMouseLeave;
            RotateAnchor.MouseDown += OnRotateAnchorMouseDown;
        }

        private void OnAnchorMouseEnter(object sender, MouseEventArgs e)
        {
            if (sender is Ellipse anchor) anchor.Opacity = 1.0;
        }

        private void OnAnchorMouseLeave(object sender, MouseEventArgs e)
        {
            if (sender is Ellipse anchor && !_isResizing) anchor.Opacity = 0.6;
        }

        private void OnRotateAnchorMouseEnter(object sender, MouseEventArgs e)
        {
            RotateAnchor.Opacity = 1.0;
            RotateAnchorLine.Opacity = 1.0;
        }

        private void OnRotateAnchorMouseLeave(object sender, MouseEventArgs e)
        {
            if (!_isRotating)
            {
                RotateAnchor.Opacity = 0.6;
                RotateAnchorLine.Opacity = 0.6;
            }
        }

        #endregion

        #region 管道几何更新

        private void UpdatePipeGeometry()
        {
            PipeCanvas.BeginInit();

            double flangeSize = _pipeWidth + FLANGE_EXTENSION;

            Width = _horizontalLength + 20;
            Height = _verticalLength + 20;

            double elbowRadius = _pipeWidth;
            double horizontalCenterY = Height - 10 - _pipeWidth / 2;
            double verticalCenterX = Width - 10 - _pipeWidth / 2;
            double elbowCenterX = verticalCenterX;
            double elbowCenterY = horizontalCenterY;

            double pipeLeft = FlangeOffset + FlangeWidth;
            double pipeWidth = Math.Max(0, elbowCenterX - elbowRadius - pipeLeft);
            double pipeTop = horizontalCenterY - _pipeWidth / 2;

            HorizontalPipeBackground.Width = pipeWidth;
            HorizontalPipeBackground.Height = _pipeWidth;
            Canvas.SetLeft(HorizontalPipeBackground, pipeLeft);
            Canvas.SetTop(HorizontalPipeBackground, pipeTop);

            HorizontalFluidBackground.Width = pipeWidth;
            HorizontalFluidBackground.Height = _pipeWidth;
            Canvas.SetLeft(HorizontalFluidBackground, pipeLeft);
            Canvas.SetTop(HorizontalFluidBackground, pipeTop);

            HorizontalFluidPattern.Width = pipeWidth;
            HorizontalFluidPattern.Height = _pipeWidth;
            Canvas.SetLeft(HorizontalFluidPattern, pipeLeft);
            Canvas.SetTop(HorizontalFluidPattern, pipeTop);

            HorizontalPipe.Width = pipeWidth;
            HorizontalPipe.Height = _pipeWidth;
            Canvas.SetLeft(HorizontalPipe, pipeLeft);
            Canvas.SetTop(HorizontalPipe, pipeTop);

            double edgeOffset = 0.38;
            HorizontalTopEdge.X1 = pipeLeft + edgeOffset;
            HorizontalTopEdge.X2 = pipeLeft + pipeWidth - edgeOffset;
            HorizontalTopEdge.Y1 = pipeTop + 0.32;
            HorizontalTopEdge.Y2 = pipeTop + 0.32;

            HorizontalBottomEdge.X1 = pipeLeft + edgeOffset;
            HorizontalBottomEdge.X2 = pipeLeft + pipeWidth - edgeOffset;
            HorizontalBottomEdge.Y1 = pipeTop + _pipeWidth - 0.32;
            HorizontalBottomEdge.Y2 = pipeTop + _pipeWidth - 0.32;

            double vPipeTop = FlangeOffset + FlangeWidth;
            double pipeHeight = Math.Max(0, elbowCenterY - elbowRadius - vPipeTop);
            double vPipeLeft = verticalCenterX - _pipeWidth / 2;

            VerticalPipeBackground.Width = _pipeWidth;
            VerticalPipeBackground.Height = pipeHeight;
            Canvas.SetLeft(VerticalPipeBackground, vPipeLeft);
            Canvas.SetTop(VerticalPipeBackground, vPipeTop);

            VerticalFluidBackground.Width = _pipeWidth;
            VerticalFluidBackground.Height = pipeHeight;
            Canvas.SetLeft(VerticalFluidBackground, vPipeLeft);
            Canvas.SetTop(VerticalFluidBackground, vPipeTop);

            VerticalFluidPattern.Width = _pipeWidth;
            VerticalFluidPattern.Height = pipeHeight;
            Canvas.SetLeft(VerticalFluidPattern, vPipeLeft);
            Canvas.SetTop(VerticalFluidPattern, vPipeTop);

            VerticalPipe.Width = _pipeWidth;
            VerticalPipe.Height = pipeHeight;
            Canvas.SetLeft(VerticalPipe, vPipeLeft);
            Canvas.SetTop(VerticalPipe, vPipeTop);

            VerticalLeftEdge.X1 = vPipeLeft + 0.32;
            VerticalLeftEdge.X2 = vPipeLeft + 0.32;
            VerticalLeftEdge.Y1 = vPipeTop + edgeOffset;
            VerticalLeftEdge.Y2 = vPipeTop + pipeHeight - edgeOffset;

            VerticalRightEdge.X1 = vPipeLeft + _pipeWidth - 0.32;
            VerticalRightEdge.X2 = vPipeLeft + _pipeWidth - 0.32;
            VerticalRightEdge.Y1 = vPipeTop + edgeOffset;
            VerticalRightEdge.Y2 = vPipeTop + pipeHeight - edgeOffset;

            UpdateElbow(elbowCenterX, elbowCenterY, elbowRadius);

            Canvas.SetLeft(LeftFlangeGroup, FlangeOffset);
            Canvas.SetTop(LeftFlangeGroup, horizontalCenterY - flangeSize / 2);
            LeftFlangeGroup.Width = FlangeWidth;
            LeftFlangeGroup.Height = flangeSize;
            LeftFlangeBase.Width = FlangeWidth;
            LeftFlangeBase.Height = flangeSize;
            LeftFlangeGradient.Width = FlangeWidth;
            LeftFlangeGradient.Height = flangeSize;
            LeftFlangeBorder.Width = FlangeWidth;
            LeftFlangeBorder.Height = flangeSize;

            Canvas.SetLeft(TopFlangeGroup, verticalCenterX - flangeSize / 2);
            Canvas.SetTop(TopFlangeGroup, FlangeOffset);
            TopFlangeGroup.Width = flangeSize;
            TopFlangeGroup.Height = FlangeWidth;
            TopFlangeBase.Width = flangeSize;
            TopFlangeBase.Height = FlangeWidth;
            TopFlangeGradient.Width = flangeSize;
            TopFlangeGradient.Height = FlangeWidth;
            TopFlangeBorder.Width = flangeSize;
            TopFlangeBorder.Height = FlangeWidth;

            LeftConnection.Height = _pipeWidth;
            Canvas.SetLeft(LeftConnection, FlangeWidth + FlangeOffset);
            Canvas.SetTop(LeftConnection, horizontalCenterY - _pipeWidth / 2);

            TopConnection.Width = _pipeWidth;
            Canvas.SetLeft(TopConnection, verticalCenterX - _pipeWidth / 2);
            Canvas.SetTop(TopConnection, FlangeWidth + FlangeOffset);

            SelectionBorder.Width = (elbowCenterX + _pipeWidth / 2) - pipeLeft;
            SelectionBorder.Height = _pipeWidth;
            Canvas.SetLeft(SelectionBorder, pipeLeft);
            Canvas.SetTop(SelectionBorder, pipeTop);

            SelectionBorderV.Width = _pipeWidth;
            SelectionBorderV.Height = (elbowCenterY + _pipeWidth / 2) - vPipeTop;
            Canvas.SetLeft(SelectionBorderV, vPipeLeft);
            Canvas.SetTop(SelectionBorderV, vPipeTop);

            UpdateAnchorPositions(horizontalCenterY, verticalCenterX);
            UpdateRotationLabelPosition();

            PipeCanvas.EndInit();

            UpdateFluidPatternViewports();
            if (IsFlowing && IsFluidVisible && !UseDefaultStyle)
                StartFlowAnimation();

            // ★ 同步赛博层
            UpdateCyberGeometry();
            if (UseDefaultStyle && IsFlowing)
                StartCyberAnimation();
        }

        private void UpdateElbow(double centerX, double centerY, double radius)
        {
            PathGeometry elbowGeometry = new PathGeometry();
            PathFigure figure = new PathFigure();

            Point outerStart = new Point(centerX - radius, centerY + _pipeWidth / 2);
            figure.StartPoint = outerStart;

            ArcSegment outerArc = new ArcSegment(
                new Point(centerX + _pipeWidth / 2, centerY - radius),
                new Size(radius + _pipeWidth / 2, radius + _pipeWidth / 2),
                0, false, SweepDirection.Counterclockwise, true);
            figure.Segments.Add(outerArc);

            LineSegment toInner = new LineSegment(
                new Point(centerX - _pipeWidth / 2, centerY - radius), true);
            figure.Segments.Add(toInner);

            ArcSegment innerArc = new ArcSegment(
                new Point(centerX - radius, centerY - _pipeWidth / 2),
                new Size(radius - _pipeWidth / 2, radius - _pipeWidth / 2),
                0, false, SweepDirection.Clockwise, true);
            figure.Segments.Add(innerArc);

            figure.IsClosed = true;
            elbowGeometry.Figures.Add(figure);
            elbowGeometry.Freeze();

            ElbowPipeBackground.Data = elbowGeometry;
            ElbowFluidBackground.Data = elbowGeometry;
            ElbowPath.Data = elbowGeometry;

            if (ElbowOuterEdge != null)
            {
                PathGeometry outerEdgeGeometry = new PathGeometry();
                PathFigure outerEdgeFigure = new PathFigure();
                double edgeOff = 0.5;
                outerEdgeFigure.StartPoint = new Point(centerX - radius, centerY + _pipeWidth / 2 - edgeOff);
                outerEdgeFigure.Segments.Add(new ArcSegment(
                    new Point(centerX + _pipeWidth / 2 - edgeOff, centerY - radius),
                    new Size(radius + _pipeWidth / 2 - edgeOff, radius + _pipeWidth / 2 - edgeOff),
                    0, false, SweepDirection.Counterclockwise, true));
                outerEdgeGeometry.Figures.Add(outerEdgeFigure);
                ElbowOuterEdge.Data = outerEdgeGeometry;
            }

            if (ElbowHighlight != null)
            {
                PathGeometry highlightGeometry = new PathGeometry();
                PathFigure highlightFigure = new PathFigure();
                double hlOff = _pipeWidth * 0.25;
                double hlRadius = radius - hlOff;
                highlightFigure.StartPoint = new Point(centerX - hlRadius, centerY - hlOff);
                highlightFigure.Segments.Add(new ArcSegment(
                    new Point(centerX - hlOff, centerY - hlRadius),
                    new Size(hlRadius, hlRadius),
                    0, false, SweepDirection.Counterclockwise, true));
                highlightGeometry.Figures.Add(highlightFigure);
                ElbowHighlight.Data = highlightGeometry;
            }
        }

        private void UpdateAnchorPositions(double horizontalY, double verticalX)
        {
            Canvas.SetLeft(LeftAnchor, -6);
            Canvas.SetTop(LeftAnchor, horizontalY - 6);
            Canvas.SetLeft(TopAnchor, verticalX - 6);
            Canvas.SetTop(TopAnchor, -6);
            Canvas.SetLeft(HorizontalWidthAnchor, _horizontalLength / 2);
            Canvas.SetTop(HorizontalWidthAnchor, Height - 6);
            Canvas.SetLeft(VerticalWidthAnchor, Width - 6);
            Canvas.SetTop(VerticalWidthAnchor, _verticalLength / 2);

            double centerX = Width / 2;
            Canvas.SetLeft(RotateAnchor, centerX - 6);
            Canvas.SetTop(RotateAnchor, -30);
            RotateAnchorLine.X1 = centerX;
            RotateAnchorLine.Y1 = 0;
            RotateAnchorLine.X2 = centerX;
            RotateAnchorLine.Y2 = -24;
        }

        private void UpdateRotation()
        {
            _rotateTransform.Angle = _rotationAngle;
            RotationText.Text = $"{_rotationAngle:F0}°";
        }

        #endregion

        #region 拖动、调整大小、旋转

        private void OnControlMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (IsClickOnAnchor(e.OriginalSource)) return;

            if (e.LeftButton == MouseButtonState.Pressed && !_isResizing && !_isRotating)
            {
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
                var parent = Parent as Canvas;
                if (parent != null)
                {
                    _dragStartPoint = e.GetPosition(parent);
                    _originalPosition = new Point(Canvas.GetLeft(this), Canvas.GetTop(this));
                    if (double.IsNaN(_originalPosition.X)) _originalPosition.X = 0;
                    if (double.IsNaN(_originalPosition.Y)) _originalPosition.Y = 0;
                }

                CacheMode = null;
                RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.LowQuality);
                CaptureMouse();
                Opacity = 0.7;

                PipeDragStarted?.Invoke(this, new ElbowPipeDragEventArgs
                {
                    PipeId = PipeId,
                    StartPosition = _originalPosition
                });
                e.Handled = true;
            }
        }

        private void OnControlMouseMove(object sender, MouseEventArgs e)
        {
            if (_isDraggingPipe && e.LeftButton == MouseButtonState.Pressed)
            {
                var parent = Parent as Canvas;
                if (parent != null)
                {
                    var currentPoint = e.GetPosition(parent);
                    double deltaX = currentPoint.X - _dragStartPoint.X;
                    double deltaY = currentPoint.Y - _dragStartPoint.Y;
                    double newLeft = Math.Max(0, Math.Min(parent.ActualWidth - ActualWidth, _originalPosition.X + deltaX));
                    double newTop = Math.Max(0, Math.Min(parent.ActualHeight - ActualHeight, _originalPosition.Y + deltaY));
                    Canvas.SetLeft(this, newLeft);
                    Canvas.SetTop(this, newTop);
                    PipeDragging?.Invoke(this, new ElbowPipeDragEventArgs { PipeId = PipeId, CurrentPosition = new Point(newLeft, newTop) });
                }
                e.Handled = true;
            }
            else if (_isResizing && _resizingAnchor.HasValue)
            {
                HandleResize(e);
                e.Handled = true;
            }
            else if (_isRotating)
            {
                HandleRotate(e);
                e.Handled = true;
            }
        }

        private void OnControlMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (_isDraggingPipe) { CompletePipeDrag(); e.Handled = true; }
            else if (_isResizing) { CompleteResize(); e.Handled = true; }
            else if (_isRotating) { CompleteRotate(); e.Handled = true; }
        }

        private void OnControlMouseLeave(object sender, MouseEventArgs e)
        {
            if (_isDraggingPipe) CompletePipeDrag();
            else if (_isResizing) CompleteResize();
            else if (_isRotating) CompleteRotate();
        }

        private void CompletePipeDrag()
        {
            if (!_isDraggingPipe) return;

            var finalPosition = new Point(Canvas.GetLeft(this), Canvas.GetTop(this));
            PipeDragCompleted?.Invoke(this, new ElbowPipeDragEventArgs
            {
                PipeId = PipeId,
                StartPosition = _originalPosition,
                CurrentPosition = finalPosition
            });

            Opacity = 1.0;
            _isDraggingPipe = false;

            RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
            CacheMode = new BitmapCache { RenderAtScale = 1.0, SnapsToDevicePixels = true, EnableClearType = false };
            ReleaseMouseCapture();
        }

        private bool IsClickOnAnchor(object source)
        {
            return source == LeftAnchor || source == TopAnchor ||
                   source == HorizontalWidthAnchor || source == VerticalWidthAnchor ||
                   source == RotateAnchor;
        }

        #endregion

        #region 锚点调整大小

        private void OnAnchorMouseDown(ElbowAnchorPosition position, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                _isResizing = true;
                _resizingAnchor = position;

                var parent = Parent as Canvas;
                if (parent != null)
                {
                    _dragStartPoint = e.GetPosition(parent);
                    _originalPosition = new Point(Canvas.GetLeft(this), Canvas.GetTop(this));
                    _originalSize = new ElbowPipeSize
                    {
                        HorizontalLength = _horizontalLength,
                        VerticalLength = _verticalLength,
                        PipeWidth = _pipeWidth
                    };
                    if (double.IsNaN(_originalPosition.X)) _originalPosition.X = 0;
                    if (double.IsNaN(_originalPosition.Y)) _originalPosition.Y = 0;
                    _fixedPoint = CalculateElbowFixedPoint(position);
                }

                CacheMode = null;
                RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.LowQuality);
                CaptureMouse();

                PipeResizeStarted?.Invoke(this, new ElbowPipeResizeEventArgs
                {
                    PipeId = PipeId,
                    AnchorPosition = position,
                    OriginalSize = _originalSize
                });
                e.Handled = true;
            }
        }

        private Point CalculateElbowFixedPoint(ElbowAnchorPosition draggedAnchor)
        {
            var parent = Parent as Canvas;
            if (parent == null) return new Point();

            var pipeLeft = Canvas.GetLeft(this);
            var pipeTop = Canvas.GetTop(this);
            if (double.IsNaN(pipeLeft)) pipeLeft = 0;
            if (double.IsNaN(pipeTop)) pipeTop = 0;

            Point center = new Point(pipeLeft + ActualWidth / 2, pipeTop + ActualHeight / 2);
            double radians = _rotationAngle * Math.PI / 180.0;

            Point localFixed;
            switch (draggedAnchor)
            {
                case ElbowAnchorPosition.Left:
                case ElbowAnchorPosition.Top:
                    localFixed = new Point(
                        Width / 2 - 10 - _pipeWidth / 2,
                        Height / 2 - 10 - _pipeWidth / 2);
                    break;
                default:
                    localFixed = new Point(0, 0);
                    break;
            }

            Point worldFixed = RotatePoint(localFixed, radians);
            return new Point(center.X + worldFixed.X, center.Y + worldFixed.Y);
        }

        private void HandleResize(MouseEventArgs e)
        {
            if (!_resizingAnchor.HasValue) return;
            var parent = Parent as Canvas;
            if (parent == null) return;

            var currentPoint = e.GetPosition(parent);

            switch (_resizingAnchor.Value)
            {
                case ElbowAnchorPosition.Left:
                    ResizeHorizontalArmFromLeft(currentPoint);
                    break;
                case ElbowAnchorPosition.Top:
                    ResizeVerticalArmFromTop(currentPoint);
                    break;
                case ElbowAnchorPosition.HorizontalWidth:
                case ElbowAnchorPosition.VerticalWidth:
                    ResizePipeWidthFromAnchor(currentPoint);
                    break;
            }

            PipeResizing?.Invoke(this, new ElbowPipeResizeEventArgs
            {
                PipeId = PipeId,
                AnchorPosition = _resizingAnchor.Value,
                CurrentSize = new ElbowPipeSize
                {
                    HorizontalLength = _horizontalLength,
                    VerticalLength = _verticalLength,
                    PipeWidth = _pipeWidth
                }
            });
        }

        private void ResizeHorizontalArmFromLeft(Point currentMousePosition)
        {
            EnsureGeometryUpdated();

            double deltaX = currentMousePosition.X - _fixedPoint.X;
            double deltaY = currentMousePosition.Y - _fixedPoint.Y;
            double radians = _rotationAngle * Math.PI / 180.0;
            double projectedLength = -(deltaX * Math.Cos(radians) + deltaY * Math.Sin(radians));
            double newLength = Math.Max(MinArmLength, Math.Min(MaxArmLength, projectedLength));
            _horizontalLength = newLength;

            UpdatePipeGeometry();
            RepositionByFixedPoint();
        }

        private void ResizeVerticalArmFromTop(Point currentMousePosition)
        {
            EnsureGeometryUpdated();

            double deltaX = currentMousePosition.X - _fixedPoint.X;
            double deltaY = currentMousePosition.Y - _fixedPoint.Y;
            double radians = _rotationAngle * Math.PI / 180.0;
            double projectedLength = -(-deltaX * Math.Sin(radians) + deltaY * Math.Cos(radians));
            double newLength = Math.Max(MinArmLength, Math.Min(MaxArmLength, projectedLength));
            _verticalLength = newLength;

            UpdatePipeGeometry();
            RepositionByFixedPoint();
        }

        private void RepositionByFixedPoint()
        {
            double w = RenderSize.Width > 0 ? RenderSize.Width : Width;
            double h = RenderSize.Height > 0 ? RenderSize.Height : Height;

            double localFixedX = (w - 10 - _pipeWidth / 2) - w / 2;
            double localFixedY = (h - 10 - _pipeWidth / 2) - h / 2;

            double radians = _rotationAngle * Math.PI / 180.0;
            Point worldOffset = RotatePoint(new Point(localFixedX, localFixedY), radians);

            Point newCenter = new Point(_fixedPoint.X - worldOffset.X, _fixedPoint.Y - worldOffset.Y);
            Canvas.SetLeft(this, newCenter.X - w / 2);
            Canvas.SetTop(this, newCenter.Y - h / 2);
        }

        private void EnsureGeometryUpdated()
        {
            if (Width <= 0 || double.IsNaN(Width)) UpdatePipeGeometry();
        }

        private void ResizePipeWidthFromAnchor(Point currentMousePosition)
        {
            double deltaX = currentMousePosition.X - _dragStartPoint.X;
            double deltaY = currentMousePosition.Y - _dragStartPoint.Y;
            double delta = Math.Sqrt(deltaX * deltaX + deltaY * deltaY);
            if (deltaX < 0 || deltaY < 0) delta = -delta;

            double newWidth = _originalSize.PipeWidth + delta * 0.5;
            newWidth = Math.Max(MinPipeWidth, Math.Min(MaxPipeWidth, newWidth));

            double widthRatio = newWidth / _originalSize.PipeWidth;
            double newHL = Math.Max(MinArmLength, Math.Min(MaxArmLength, _originalSize.HorizontalLength * widthRatio));
            double newVL = Math.Max(MinArmLength, Math.Min(MaxArmLength, _originalSize.VerticalLength * widthRatio));

            _pipeWidth = newWidth;
            _horizontalLength = newHL;
            _verticalLength = newVL;
            UpdatePipeGeometry();
        }

        private void CompleteResize()
        {
            if (!_isResizing) return;

            PipeResizeCompleted?.Invoke(this, new ElbowPipeResizeEventArgs
            {
                PipeId = PipeId,
                AnchorPosition = _resizingAnchor.Value,
                OriginalSize = _originalSize,
                CurrentSize = new ElbowPipeSize
                {
                    HorizontalLength = _horizontalLength,
                    VerticalLength = _verticalLength,
                    PipeWidth = _pipeWidth
                }
            });

            _isResizing = false;
            _resizingAnchor = null;
            RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
            CacheMode = new BitmapCache { RenderAtScale = 1.0, SnapsToDevicePixels = true, EnableClearType = false };
            ReleaseMouseCapture();
        }

        #endregion

        #region 旋转功能

        private void OnRotateAnchorMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                _isRotating = true;
                _originalRotation = _rotationAngle;

                var parent = Parent as Canvas;
                if (parent != null) _dragStartPoint = e.GetPosition(parent);

                CacheMode = null;
                RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.LowQuality);
                CaptureMouse();

                PipeRotateStarted?.Invoke(this, new ElbowPipeRotateEventArgs
                {
                    PipeId = PipeId,
                    OriginalAngle = _originalRotation
                });
                e.Handled = true;
            }
        }

        private void HandleRotate(MouseEventArgs e)
        {
            var parent = Parent as Canvas;
            if (parent == null) return;
            var currentPoint = e.GetPosition(parent);
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
            ShowRotationLabel();
            PipeRotating?.Invoke(this, new ElbowPipeRotateEventArgs { PipeId = PipeId, CurrentAngle = _rotationAngle });
        }

        private void CompleteRotate()
        {
            if (!_isRotating) return;

            PipeRotateCompleted?.Invoke(this, new ElbowPipeRotateEventArgs
            {
                PipeId = PipeId,
                OriginalAngle = _originalRotation,
                CurrentAngle = _rotationAngle
            });

            _isRotating = false;
            RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
            CacheMode = new BitmapCache { RenderAtScale = 1.0, SnapsToDevicePixels = true, EnableClearType = false };
            ReleaseMouseCapture();
        }

        #endregion

        #region 选择状态

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

            double leftFlangeX = FlangeOffset + FlangeWidth / 2 - ActualWidth / 2;
            double leftFlangeY = Canvas.GetTop(LeftFlangeGroup) + LeftFlangeGroup.Height / 2 - ActualHeight / 2;
            Point leftFlangeWorld = RotatePoint(new Point(leftFlangeX, leftFlangeY), radians);
            snapPoints.Add(new PipeSnapPoint
            {
                WorldPosition = new Point(center.X + leftFlangeWorld.X, center.Y + leftFlangeWorld.Y),
                Direction = GetSnapDirectionFromAngle(radians + Math.PI),
                Description = $"弯管水平法兰 (水平:{_horizontalLength:F0})"
            });

            double topFlangeX = Canvas.GetLeft(TopFlangeGroup) + TopFlangeGroup.Width / 2 - ActualWidth / 2;
            double topFlangeY = FlangeOffset + FlangeWidth / 2 - ActualHeight / 2;
            Point topFlangeWorld = RotatePoint(new Point(topFlangeX, topFlangeY), radians);
            snapPoints.Add(new PipeSnapPoint
            {
                WorldPosition = new Point(center.X + topFlangeWorld.X, center.Y + topFlangeWorld.Y),
                Direction = GetSnapDirectionFromAngle(radians - Math.PI / 2),
                Description = $"弯管垂直法兰 (垂直:{_verticalLength:F0})"
            });

            return snapPoints;
        }

        private Point RotatePoint(Point localPoint, double radians)
        {
            double cos = Math.Cos(radians);
            double sin = Math.Sin(radians);
            return new Point(
                localPoint.X * cos - localPoint.Y * sin,
                localPoint.X * sin + localPoint.Y * cos);
        }

        private SnapDirection GetSnapDirectionFromAngle(double radians)
        {
            double degrees = (radians * 180 / Math.PI + 360) % 360;
            if (degrees >= 315 || degrees < 45) return SnapDirection.Right;
            if (degrees >= 45 && degrees < 135) return SnapDirection.Down;
            if (degrees >= 135 && degrees < 225) return SnapDirection.Left;
            return SnapDirection.Up;
        }

        #endregion
    }

    #region 事件参数类

    public class ElbowPipeDragEventArgs : EventArgs
    {
        public string PipeId { get; set; }
        public Point StartPosition { get; set; }
        public Point CurrentPosition { get; set; }
    }

    public class ElbowPipeResizeEventArgs : EventArgs
    {
        public string PipeId { get; set; }
        public ElbowAnchorPosition AnchorPosition { get; set; }
        public ElbowPipeSize OriginalSize { get; set; }
        public ElbowPipeSize CurrentSize { get; set; }
    }

    public class ElbowPipeRotateEventArgs : EventArgs
    {
        public string PipeId { get; set; }
        public double OriginalAngle { get; set; }
        public double CurrentAngle { get; set; }
    }

    public class ElbowPipeEventArgs : EventArgs
    {
        public string PipeId { get; set; }
    }

    public struct ElbowPipeSize
    {
        public double HorizontalLength { get; set; }
        public double VerticalLength { get; set; }
        public double PipeWidth { get; set; }
    }

    public enum ElbowAnchorPosition
    {
        Left,
        Top,
        HorizontalWidth,
        VerticalWidth
    }

    #endregion
}
using MaxChemical.Modules.Designer.Service;
using MaxChemical.Modules.Designer.Views.Controls.Service;
using System;
using System.Collections.Generic;
using System.ComponentModel;
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
    /// 流体流动方向
    /// </summary>
    public enum PipeFlowDirection
    {
        /// <summary>从左法兰流向右法兰</summary>
        LeftToRight,
        /// <summary>从右法兰流向左法兰</summary>
        RightToLeft
    }

    public partial class StraightPipeControl : UserControl, IPipeSnapPoints, IPipeProperties
    {
        #region 事件定义

        public event EventHandler<PipeDragEventArgs> PipeDragStarted;
        public event EventHandler<PipeDragEventArgs> PipeDragging;
        public event EventHandler<PipeDragEventArgs> PipeDragCompleted;
        public event EventHandler<PipeResizeEventArgs> PipeResizeStarted;
        public event EventHandler<PipeResizeEventArgs> PipeResizing;
        public event EventHandler<PipeResizeEventArgs> PipeResizeCompleted;
        public event EventHandler<PipeRotateEventArgs> PipeRotateStarted;
        public event EventHandler<PipeRotateEventArgs> PipeRotating;
        public event EventHandler<PipeRotateEventArgs> PipeRotateCompleted;
        public event EventHandler<string> PipeSelected;
        public event EventHandler<string> PipeDeselected;
        /// <summary>管道双击事件（打开属性对话框）</summary>
        public event EventHandler<string> PipeDoubleClicked;
        public string PipeId { get; set; }

        #endregion

        #region 依赖属性 — 选择状态

        public static readonly DependencyProperty IsSelectedProperty =
            DependencyProperty.Register(nameof(IsSelected), typeof(bool), typeof(StraightPipeControl),
                new PropertyMetadata(false, OnIsSelectedChanged));

        public bool IsSelected
        {
            get => (bool)GetValue(IsSelectedProperty);
            set => SetValue(IsSelectedProperty, value);
        }

        private static void OnIsSelectedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is StraightPipeControl c) c.UpdateSelectionVisual((bool)e.NewValue);
        }

        #endregion

        #region 依赖属性 — 管道外观

        public static readonly DependencyProperty PipeColorProperty =
            DependencyProperty.Register(nameof(PipeColor), typeof(Color), typeof(StraightPipeControl),
                new PropertyMetadata(Color.FromRgb(0x72, 0x71, 0x71), OnPipeColorChanged));

        public Color PipeColor
        {
            get => (Color)GetValue(PipeColorProperty);
            set => SetValue(PipeColorProperty, value);
        }

        private static void OnPipeColorChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is StraightPipeControl c) c.ApplyPipeColor();
        }

        public static readonly DependencyProperty FlangeColorProperty =
            DependencyProperty.Register(nameof(FlangeColor), typeof(Color), typeof(StraightPipeControl),
                new PropertyMetadata(Color.FromRgb(0xD9, 0xD9, 0xD9), OnFlangeColorChanged));

        public Color FlangeColor
        {
            get => (Color)GetValue(FlangeColorProperty);
            set => SetValue(FlangeColorProperty, value);
        }

        private static void OnFlangeColorChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is StraightPipeControl c) c.ApplyFlangeColor();
        }

        #endregion

        #region 依赖属性 — 流体

        public static readonly DependencyProperty FluidColorProperty =
            DependencyProperty.Register(nameof(FluidColor), typeof(Color), typeof(StraightPipeControl),
                new PropertyMetadata(Color.FromRgb(0x1E, 0xC1, 0xF4), OnFluidColorChanged));

        public Color FluidColor
        {
            get => (Color)GetValue(FluidColorProperty);
            set => SetValue(FluidColorProperty, value);
        }

        private static void OnFluidColorChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is StraightPipeControl c) c.ApplyFluidColor();
        }

        public static readonly DependencyProperty FluidOpacityProperty =
            DependencyProperty.Register(nameof(FluidOpacity), typeof(double), typeof(StraightPipeControl),
                new PropertyMetadata(0.85, OnFluidOpacityChanged));

        public double FluidOpacity
        {
            get => (double)GetValue(FluidOpacityProperty);
            set => SetValue(FluidOpacityProperty, value);
        }

        private static void OnFluidOpacityChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is StraightPipeControl c && c.FluidBackground != null)
                c.FluidBackground.Opacity = (double)e.NewValue;
        }

        public static readonly DependencyProperty IsFluidVisibleProperty =
            DependencyProperty.Register(nameof(IsFluidVisible), typeof(bool), typeof(StraightPipeControl),
                new PropertyMetadata(false, OnIsFluidVisibleChanged));

        public bool IsFluidVisible
        {
            get => (bool)GetValue(IsFluidVisibleProperty);
            set => SetValue(IsFluidVisibleProperty, value);
        }

        private static void OnIsFluidVisibleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is StraightPipeControl c) c.ApplyFluidVisibility();
        }

        // ═══ 是否使用默认流动样式（赛博流光） ═══
        public static readonly DependencyProperty UseDefaultStyleProperty =
            DependencyProperty.Register(nameof(UseDefaultStyle), typeof(bool), typeof(StraightPipeControl),
                new PropertyMetadata(false, OnUseDefaultStyleChanged));

        public bool UseDefaultStyle
        {
            get => (bool)GetValue(UseDefaultStyleProperty);
            set => SetValue(UseDefaultStyleProperty, value);
        }

        private static void OnUseDefaultStyleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is StraightPipeControl c) c.ApplyDefaultStyleSwitch();
        }

        #endregion

        #region 依赖属性 — 流动控制

        public static readonly DependencyProperty IsFlowingProperty =
            DependencyProperty.Register(nameof(IsFlowing), typeof(bool), typeof(StraightPipeControl),
                new PropertyMetadata(false, OnIsFlowingChanged));

        public bool IsFlowing
        {
            get => (bool)GetValue(IsFlowingProperty);
            set => SetValue(IsFlowingProperty, value);
        }

        private static void OnIsFlowingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is StraightPipeControl c) c.ApplyFlowAnimation();
        }

        public static readonly DependencyProperty FlowDirectionProperty =
            DependencyProperty.Register("FlowDirection", typeof(PipeFlowDirection), typeof(StraightPipeControl),
                new PropertyMetadata(PipeFlowDirection.LeftToRight, OnFlowDirectionChanged));

        public PipeFlowDirection PipeFlowDir
        {
            get => (PipeFlowDirection)GetValue(FlowDirectionProperty);
            set => SetValue(FlowDirectionProperty, value);
        }

        private static void OnFlowDirectionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is StraightPipeControl c) c.ApplyFlowAnimation();
        }

        public static readonly DependencyProperty FlowSpeedProperty =
            DependencyProperty.Register(nameof(FlowSpeed), typeof(double), typeof(StraightPipeControl),
                new PropertyMetadata(1.0, OnFlowSpeedChanged));

        public double FlowSpeed
        {
            get => (double)GetValue(FlowSpeedProperty);
            set => SetValue(FlowSpeedProperty, value);
        }

        private static void OnFlowSpeedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is StraightPipeControl c) c.ApplyFlowAnimation();
        }

        #endregion

        #region 私有字段

        private bool _isDraggingPipe;
        private bool _isResizing;
        private bool _isRotating;
        private PipeAnchorPosition? _resizingAnchor;
        private Point _dragStartPoint;
        private Point _originalPosition;
        private Size _originalSize;
        private double _originalRotation;
        private Point _fixedPoint;

        private double _pipeLength = 85.9;
        private double _pipeWidth = 16.6;
        private double _rotationAngle;

        private RotateTransform _rotateTransform;
        private TransformGroup _transformGroup;
        private DispatcherTimer _rotationLabelTimer;

        // 原气泡流体动画
        private TranslateTransform _flowTransform;
        private DrawingBrush _fluidPatternBrush;

        // ═══ 赛博流光动画 ═══
        private DoubleAnimation _cyberMainAnim;
        private DoubleAnimation _cyberParticleAnim;
        private DoubleAnimation _cyberSparkAnim;
        // 赛博流光的 dash 周期总长（必须等于 dasharray 的两段之和）
        private const double CYBER_MAIN_PERIOD = 120;     // dasharray = 30 + 90
        private const double CYBER_PARTICLE_PERIOD = 58;  // dasharray = 8 + 50
        private const double CYBER_SPARK_PERIOD = 84;     // dasharray = 4 + 80

        private const double MinPipeLength = 20;
        private const double MaxPipeLength = 10000;
        private const double MinPipeWidth = 4;
        private const double MaxPipeWidth = 10000;
        private const double SCALE_FACTOR = 0.230833;
        private const double LEFT_FLANGE_LEFT = 0.35;
        private const double LEFT_FLANGE_WIDTH = 2.54;
        private const double PIPE_LEFT = 3.2;
        private const double RIGHT_FLANGE_WIDTH = 2.54;

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

        private const double PATTERN_W = 172.0;
        private const double PATTERN_H = 72.0;

        #endregion

        #region CLR 属性（尺寸）

        public double PipeLength
        {
            get => _pipeLength;
            set
            {
                _pipeLength = Math.Max(MinPipeLength, Math.Min(MaxPipeLength, value));
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

        public StraightPipeControl()
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
            }

            Loaded += (s, e) =>
            {
                UpdatePipeGeometry();
                ApplyFlowAnimation();
            };
            UpdatePipeGeometry();

            RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
            RenderOptions.SetCachingHint(this, CachingHint.Cache);
            RenderOptions.SetCacheInvalidationThresholdMinimum(this, 0.5);
            RenderOptions.SetCacheInvalidationThresholdMaximum(this, 2.0);
        }

        private void InitializeAppearance()
        {
            ApplyPipeColor();
            ApplyFlangeColor();
            ApplyFluidColor();
            RebuildFluidPatternBrush();
            ApplyFluidVisibility();

            // ★ 应用默认样式开关
            ApplyDefaultStyleSwitch();
        }

        #endregion

        #region 管道外观 — 管体渐变

        private void ApplyPipeColor()
        {
            if (MainPipe == null) return;

            var c = PipeColor;
            var gradient = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
            gradient.GradientStops.Add(new GradientStop(c, 0));
            gradient.GradientStops.Add(new GradientStop(Color.FromArgb(0x59, c.R, c.G, c.B), 0.264));
            gradient.GradientStops.Add(new GradientStop(Color.FromArgb(0x1A, c.R, c.G, c.B), 0.418));
            gradient.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 0.498));
            gradient.GradientStops.Add(new GradientStop(Color.FromArgb(0x1A, c.R, c.G, c.B), 0.581));
            gradient.GradientStops.Add(new GradientStop(Color.FromArgb(0x59, c.R, c.G, c.B), 0.719));
            gradient.GradientStops.Add(new GradientStop(c, 1));
            gradient.Freeze();

            MainPipe.Fill = gradient;

            var strokeBrush = new SolidColorBrush(c);
            strokeBrush.Freeze();
            LeftFlangeBorder.Stroke = strokeBrush;
            RightFlangeBorder.Stroke = strokeBrush;

            RebuildFluidPatternBrush();
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

        private static Color BlendColors(Color c1, Color c2, double ratio)
        {
            return Color.FromRgb(
                (byte)(c1.R * (1 - ratio) + c2.R * ratio),
                (byte)(c1.G * (1 - ratio) + c2.G * ratio),
                (byte)(c1.B * (1 - ratio) + c2.B * ratio));
        }

        #endregion

        #region 流体 — 颜色与可见性

        private void ApplyFluidColor()
        {
            if (FluidBackground == null) return;
            FluidBackground.Fill = new SolidColorBrush(FluidColor);
            FluidBackground.Opacity = FluidOpacity;

            // 流体颜色变了,气泡纹理颜色也要跟着变
            RebuildFluidPatternBrush();

            // ★ 赛博样式下,主流光、粒子流颜色也要跟着变
            if (UseDefaultStyle && CyberMainStream != null)
            {
                ApplyCyberStreamColors();
            }
        }

        private void ApplyFluidVisibility()
        {
            if (FluidBackground == null || FluidPattern == null) return;

            // ★ 赛博样式下,原气泡层永远隐藏
            if (UseDefaultStyle)
            {
                FluidBackground.Visibility = Visibility.Collapsed;
                FluidPattern.Visibility = Visibility.Collapsed;
                return;
            }

            var vis = IsFluidVisible ? Visibility.Visible : Visibility.Collapsed;
            FluidBackground.Visibility = vis;
            FluidPattern.Visibility = vis;

            if (IsFluidVisible && IsFlowing)
                StartFlowAnimation();
            else
                StopFlowAnimation();
        }

        #endregion

        #region 流体 — 气泡纹理画刷

        private void RebuildFluidPatternBrush()
        {
            if (FluidPattern == null) return;

            var fluidC = FluidColor;
            var group = new DrawingGroup();

            Color bubbleLight = Color.FromArgb(220, 255, 255, 255);
            Color bubbleDark = Color.FromArgb(140,
                (byte)(fluidC.R * 0.4),
                (byte)(fluidC.G * 0.4),
                (byte)(fluidC.B * 0.4));

            foreach (var (x, y, r) in BubbleData)
            {
                var radial = new RadialGradientBrush
                {
                    GradientOrigin = new Point(0.3, 0.3),
                    Center = new Point(0.5, 0.5)
                };
                radial.GradientStops.Add(new GradientStop(bubbleLight, 0));
                radial.GradientStops.Add(new GradientStop(bubbleDark, 1));
                radial.Freeze();

                group.Children.Add(new GeometryDrawing(
                    radial, null,
                    new EllipseGeometry(new Point(x, y), r, r)));
            }

            Color edgeColor = Color.FromArgb(255,
                (byte)(fluidC.R * 0.6),
                (byte)(fluidC.G * 0.6),
                (byte)(fluidC.B * 0.6));

            var edgeBrush = new LinearGradientBrush { StartPoint = new Point(0.5, 0), EndPoint = new Point(0.5, 1) };
            edgeBrush.GradientStops.Add(new GradientStop(edgeColor, 0));
            edgeBrush.GradientStops.Add(new GradientStop(edgeColor, 0.109));
            edgeBrush.GradientStops.Add(new GradientStop(Color.FromArgb(0, 255, 255, 255), 0.113));
            edgeBrush.GradientStops.Add(new GradientStop(Color.FromArgb(0, 255, 255, 255), 0.887));
            edgeBrush.GradientStops.Add(new GradientStop(edgeColor, 0.891));
            edgeBrush.GradientStops.Add(new GradientStop(edgeColor, 1));
            edgeBrush.Freeze();

            group.Children.Add(new GeometryDrawing(
                edgeBrush, null,
                new RectangleGeometry(new Rect(0, 0, PATTERN_W, PATTERN_H))));

            _flowTransform = new TranslateTransform(0, 0);
            _fluidPatternBrush = new DrawingBrush(group)
            {
                TileMode = TileMode.Tile,
                Viewbox = new Rect(0, 0, PATTERN_W, PATTERN_H),
                ViewboxUnits = BrushMappingMode.Absolute,
                ViewportUnits = BrushMappingMode.Absolute,
                Transform = _flowTransform
            };

            UpdateFluidPatternViewport();
            FluidPattern.Fill = _fluidPatternBrush;

            if (IsFlowing && IsFluidVisible && !UseDefaultStyle)
                StartFlowAnimation();
        }

        private void UpdateFluidPatternViewport()
        {
            if (_fluidPatternBrush == null) return;

            double tileH = _pipeWidth;
            double tileW = tileH * (PATTERN_W / PATTERN_H);
            _fluidPatternBrush.Viewport = new Rect(0, 0, tileW, tileH);
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
            if (_flowTransform == null) return;

            _flowTransform.BeginAnimation(TranslateTransform.XProperty, null);

            double tileW = _pipeWidth * (PATTERN_W / PATTERN_H);
            double to = PipeFlowDir == PipeFlowDirection.LeftToRight ? tileW : -tileW;
            double durationMs = 1000.0 / Math.Max(0.1, FlowSpeed);

            var anim = new DoubleAnimation
            {
                From = 0,
                To = to,
                Duration = TimeSpan.FromMilliseconds(durationMs),
                RepeatBehavior = RepeatBehavior.Forever
            };

            _flowTransform.BeginAnimation(TranslateTransform.XProperty, anim);
        }

        private void StopFlowAnimation()
        {
            if (_flowTransform == null) return;
            _flowTransform.BeginAnimation(TranslateTransform.XProperty, null);
            _flowTransform.X = 0;
        }

        #endregion

        #region 赛博流光（默认流动样式）

        /// <summary>
        /// 切换原层 / 赛博层显示
        /// </summary>
        private void ApplyDefaultStyleSwitch()
        {
            if (CyberMetalPipe == null) return;

            if (UseDefaultStyle)
            {
                // 隐藏原管壁渐变 + 流体气泡
                if (MainPipe != null) MainPipe.Visibility = Visibility.Collapsed;
                if (FluidBackground != null) FluidBackground.Visibility = Visibility.Collapsed;
                if (FluidPattern != null) FluidPattern.Visibility = Visibility.Collapsed;
                if (PipeBackground != null) PipeBackground.Visibility = Visibility.Collapsed;

                // 构建并显示赛博层
                BuildCyberLayers();
                CyberMetalPipe.Visibility = Visibility.Visible;
                CyberDarkCore.Visibility = Visibility.Visible;
                CyberMainStream.Visibility = Visibility.Visible;
                CyberParticleStream.Visibility = Visibility.Visible;
                CyberSparkStream.Visibility = Visibility.Visible;
                CyberTopShine.Visibility = Visibility.Visible;

                if (IsFlowing) StartCyberAnimation();
                else StopCyberAnimation();

                // 停止原气泡动画
                StopFlowAnimation();
            }
            else
            {
                // 隐藏赛博层
                CyberMetalPipe.Visibility = Visibility.Collapsed;
                CyberDarkCore.Visibility = Visibility.Collapsed;
                CyberMainStream.Visibility = Visibility.Collapsed;
                CyberParticleStream.Visibility = Visibility.Collapsed;
                CyberSparkStream.Visibility = Visibility.Collapsed;
                CyberTopShine.Visibility = Visibility.Collapsed;
                StopCyberAnimation();

                // 恢复原层
                if (PipeBackground != null) PipeBackground.Visibility = Visibility.Visible;
                if (MainPipe != null) MainPipe.Visibility = Visibility.Visible;
                ApplyFluidVisibility();
            }
        }

        /// <summary>
        /// 构建赛博 6 层的画刷与几何
        /// </summary>
        private void BuildCyberLayers()
        {
            if (CyberMetalPipe == null) return;

            // ── 1. 金属管壁渐变（5-stop：暗-中-亮-中-暗） ──
            var metal = new LinearGradientBrush
            {
                StartPoint = new Point(0.5, 0),
                EndPoint = new Point(0.5, 1)
            };
            metal.GradientStops.Add(new GradientStop(Color.FromRgb(0x1A, 0x20, 0x30), 0.0));
            metal.GradientStops.Add(new GradientStop(Color.FromRgb(0x4A, 0x5A, 0x7A), 0.2));
            metal.GradientStops.Add(new GradientStop(Color.FromRgb(0x8A, 0xA0, 0xC0), 0.5));
            metal.GradientStops.Add(new GradientStop(Color.FromRgb(0x4A, 0x5A, 0x7A), 0.8));
            metal.GradientStops.Add(new GradientStop(Color.FromRgb(0x1A, 0x20, 0x30), 1.0));
            metal.Freeze();
            CyberMetalPipe.Fill = metal;

            // ── 2. 内部暗腔 — 已在 XAML 设置 Fill="#000814" Opacity="0.85" ──

            // ── 3-5. 流光颜色（跟随 FluidColor） ──
            ApplyCyberStreamColors();
            CyberMainStream.StrokeDashArray = new DoubleCollection { 30, 90 };
            CyberParticleStream.StrokeDashArray = new DoubleCollection { 8, 50 };
            CyberSparkStream.StrokeDashArray = new DoubleCollection { 4, 80 };

            // ── 6. 顶部高光带 ──
            var topShine = new LinearGradientBrush
            {
                StartPoint = new Point(0.5, 0),
                EndPoint = new Point(0.5, 1)
            };
            topShine.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 0.0));
            topShine.GradientStops.Add(new GradientStop(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF), 0.4));
            topShine.GradientStops.Add(new GradientStop(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF), 0.5));
            topShine.GradientStops.Add(new GradientStop(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF), 0.6));
            topShine.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 1.0));
            topShine.Freeze();
            CyberTopShine.Fill = topShine;

            UpdateCyberGeometry();
        }

        /// <summary>
        /// 让主流光 + 粒子流的颜色跟随 FluidColor（白热闪点保持纯白）
        /// </summary>
        private void ApplyCyberStreamColors()
        {
            if (CyberMainStream == null) return;

            var c = FluidColor;
            var darkC = DarkenColorSafe(c, 60);
            var brightC = LightenColorSafe(c, 30);
            var sparkC = LightenColorSafe(c, 60);

            // 主流光：渐变（暗→亮→暗）模拟流体光泽
            var mainBrush = new LinearGradientBrush
            {
                StartPoint = new Point(0.5, 0),
                EndPoint = new Point(0.5, 1)
            };
            mainBrush.GradientStops.Add(new GradientStop(darkC, 0));
            mainBrush.GradientStops.Add(new GradientStop(brightC, 0.5));
            mainBrush.GradientStops.Add(new GradientStop(darkC, 1));
            mainBrush.Freeze();
            CyberMainStream.Stroke = mainBrush;

            // 粒子流：纯色亮色
            var particleBrush = new SolidColorBrush(sparkC);
            particleBrush.Freeze();
            CyberParticleStream.Stroke = particleBrush;

            // 闪点保持白色（XAML 中已设 Stroke="#FFFFFF"，这里不改）
        }

        /// <summary>
        /// 同步赛博层尺寸 + 流光路径
        /// </summary>
        private void UpdateCyberGeometry()
        {
            if (CyberMetalPipe == null) return;

            double pipeTopOffset = (Height - _pipeWidth) / 2;

            // 矩形层（金属壁/暗腔/顶光）
            CyberMetalPipe.Width = _pipeLength;
            CyberMetalPipe.Height = _pipeWidth;
            Canvas.SetLeft(CyberMetalPipe, PIPE_LEFT);
            Canvas.SetTop(CyberMetalPipe, pipeTopOffset);

            CyberDarkCore.Width = _pipeLength;
            CyberDarkCore.Height = _pipeWidth;
            Canvas.SetLeft(CyberDarkCore, PIPE_LEFT);
            Canvas.SetTop(CyberDarkCore, pipeTopOffset);

            CyberTopShine.Width = _pipeLength;
            CyberTopShine.Height = _pipeWidth;
            Canvas.SetLeft(CyberTopShine, PIPE_LEFT);
            Canvas.SetTop(CyberTopShine, pipeTopOffset);

            // 流光路径：管道中线
            double centerY = pipeTopOffset + _pipeWidth / 2;
            double startX = PIPE_LEFT;
            double endX = PIPE_LEFT + _pipeLength;

            var fig = new PathFigure { StartPoint = new Point(startX, centerY) };
            fig.Segments.Add(new LineSegment(new Point(endX, centerY), true));
            var geo = new PathGeometry();
            geo.Figures.Add(fig);
            geo.Freeze();

            CyberMainStream.Data = geo;
            CyberParticleStream.Data = geo;
            CyberSparkStream.Data = geo;

            // stroke 粗细按管宽比例
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

            _cyberMainAnim = new DoubleAnimation
            {
                From = 0,
                To = sign * CYBER_MAIN_PERIOD,
                Duration = TimeSpan.FromMilliseconds(2000.0 / speed),
                RepeatBehavior = RepeatBehavior.Forever
            };
            CyberMainStream.BeginAnimation(Path.StrokeDashOffsetProperty, _cyberMainAnim);

            _cyberParticleAnim = new DoubleAnimation
            {
                From = 0,
                To = sign * CYBER_PARTICLE_PERIOD,
                Duration = TimeSpan.FromMilliseconds(1500.0 / speed),
                RepeatBehavior = RepeatBehavior.Forever
            };
            CyberParticleStream.BeginAnimation(Path.StrokeDashOffsetProperty, _cyberParticleAnim);

            _cyberSparkAnim = new DoubleAnimation
            {
                From = sign * -30,
                To = sign * (CYBER_SPARK_PERIOD - 30),
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

        private static Color LightenColorSafe(Color c, byte amount)
        {
            return Color.FromRgb(
                (byte)Math.Min(255, c.R + amount),
                (byte)Math.Min(255, c.G + amount),
                (byte)Math.Min(255, c.B + amount));
        }

        private static Color DarkenColorSafe(Color c, byte amount)
        {
            return Color.FromRgb(
                (byte)Math.Max(0, c.R - amount),
                (byte)Math.Max(0, c.G - amount),
                (byte)Math.Max(0, c.B - amount));
        }

        #endregion

        #region 管道几何更新

        private void UpdatePipeGeometry()
        {
            Width = LEFT_FLANGE_LEFT + LEFT_FLANGE_WIDTH + _pipeLength + RIGHT_FLANGE_WIDTH;

            double flangeExtension = 5.8;
            Height = _pipeWidth + flangeExtension;

            double pipeTopOffset = flangeExtension / 2;

            PipeBackground.Width = _pipeLength;
            PipeBackground.Height = _pipeWidth;
            Canvas.SetLeft(PipeBackground, PIPE_LEFT);
            Canvas.SetTop(PipeBackground, pipeTopOffset);

            FluidBackground.Width = _pipeLength;
            FluidBackground.Height = _pipeWidth;
            Canvas.SetLeft(FluidBackground, PIPE_LEFT);
            Canvas.SetTop(FluidBackground, pipeTopOffset);

            FluidPattern.Width = _pipeLength;
            FluidPattern.Height = _pipeWidth;
            Canvas.SetLeft(FluidPattern, PIPE_LEFT);
            Canvas.SetTop(FluidPattern, pipeTopOffset);

            MainPipe.Width = _pipeLength;
            MainPipe.Height = _pipeWidth;
            Canvas.SetLeft(MainPipe, PIPE_LEFT);
            Canvas.SetTop(MainPipe, pipeTopOffset);

            double edgeLeft = PIPE_LEFT + 0.38;
            double edgeRight = PIPE_LEFT + _pipeLength - 0.38;
            double topEdgeY = pipeTopOffset + 0.32;
            double bottomEdgeY = pipeTopOffset + _pipeWidth - 0.32;

            TopEdgeLine.X1 = edgeLeft; TopEdgeLine.X2 = edgeRight;
            TopEdgeLine.Y1 = topEdgeY; TopEdgeLine.Y2 = topEdgeY;
            BottomEdgeLine.X1 = edgeLeft; BottomEdgeLine.X2 = edgeRight;
            BottomEdgeLine.Y1 = bottomEdgeY; BottomEdgeLine.Y2 = bottomEdgeY;

            UpdateFlanges();

            SelectionBorder.Width = _pipeLength;
            SelectionBorder.Height = _pipeWidth;
            Canvas.SetLeft(SelectionBorder, PIPE_LEFT);
            Canvas.SetTop(SelectionBorder, pipeTopOffset);

            UpdateAnchorPositions();
            UpdateRotationLabelPosition();

            UpdateFluidPatternViewport();
            if (IsFlowing && IsFluidVisible && !UseDefaultStyle)
                StartFlowAnimation();

            // ★ 同步赛博层
            UpdateCyberGeometry();
            if (UseDefaultStyle && IsFlowing)
                StartCyberAnimation();
        }

        private void UpdateFlanges()
        {
            double flangeExtension = 5.8;
            double flangeHeight = _pipeWidth + flangeExtension;
            double flangeTop = 0;

            Canvas.SetLeft(LeftFlangeGroup, LEFT_FLANGE_LEFT);
            Canvas.SetTop(LeftFlangeGroup, flangeTop);
            LeftFlangeBase.Height = flangeHeight;
            LeftFlangeGradient.Height = flangeHeight;
            LeftFlangeBorder.Height = flangeHeight;

            double rightFlangeLeft = LEFT_FLANGE_LEFT + LEFT_FLANGE_WIDTH + _pipeLength +
                                    (PIPE_LEFT - LEFT_FLANGE_LEFT - LEFT_FLANGE_WIDTH);
            Canvas.SetLeft(RightFlangeGroup, rightFlangeLeft);
            Canvas.SetTop(RightFlangeGroup, flangeTop);
            RightFlangeBase.Height = flangeHeight;
            RightFlangeGradient.Height = flangeHeight;
            RightFlangeBorder.Height = flangeHeight;
        }

        private void UpdateAnchorPositions()
        {
            double centerY = Height / 2;
            double centerX = Width / 2;

            Canvas.SetLeft(LeftAnchor, -6);
            Canvas.SetTop(LeftAnchor, centerY - 6);
            Canvas.SetLeft(RightAnchor, Width - 6);
            Canvas.SetTop(RightAnchor, centerY - 6);
            Canvas.SetLeft(TopAnchor, centerX - 6);
            Canvas.SetTop(TopAnchor, 4);
            Canvas.SetLeft(BottomAnchor, centerX - 6);
            Canvas.SetTop(BottomAnchor, Height - 16);
            Canvas.SetLeft(RotateAnchor, centerX - 6);
            Canvas.SetTop(RotateAnchor, -30);

            RotateAnchorLine.X1 = centerX;
            RotateAnchorLine.Y1 = 0;
            RotateAnchorLine.X2 = centerX;
            RotateAnchorLine.Y2 = -24;
        }

        private void UpdateRotationLabelPosition()
        {
            double centerX = Width / 2;
            Canvas.SetLeft(RotationLabel, centerX - 20);
            Canvas.SetTop(RotationLabel, -50);
        }

        private void UpdateRotation()
        {
            _rotateTransform.Angle = _rotationAngle;
            RotationText.Text = $"{_rotationAngle:F0}°";
        }

        private void UpdateSelectionVisual(bool isSelected)
        {
            if (isSelected)
            {
                var orangeBrush = new SolidColorBrush(Colors.Orange);
                var glowEffect = new DropShadowEffect
                {
                    Color = Colors.Orange,
                    BlurRadius = 10,
                    ShadowDepth = 0,
                    Opacity = 0.8
                };

                if (MainPipe != null)
                {
                    MainPipe.Stroke = orangeBrush;
                    MainPipe.StrokeThickness = 2;
                    MainPipe.Effect = glowEffect;
                }

                SelectionBorder.Visibility = Visibility.Visible;
                ShowAllAnchors();
            }
            else
            {
                if (MainPipe != null)
                {
                    MainPipe.Stroke = null;
                    MainPipe.StrokeThickness = 0;
                    MainPipe.Effect = null;
                }

                SelectionBorder.Visibility = Visibility.Collapsed;

                if (!IsMouseOver && !_isResizing && !_isRotating)
                    HideAllAnchors();
            }
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
            RightAnchor.Opacity = 0.6;
            TopAnchor.Opacity = 0.6;
            BottomAnchor.Opacity = 0.6;
            RotateAnchor.Opacity = 0.6;
            RotateAnchorLine.Opacity = 0.6;
        }

        private void HideAllAnchors()
        {
            LeftAnchor.Opacity = 0;
            RightAnchor.Opacity = 0;
            TopAnchor.Opacity = 0;
            BottomAnchor.Opacity = 0;
            RotateAnchor.Opacity = 0;
            RotateAnchorLine.Opacity = 0;
        }

        #endregion

        #region 锚点事件设置

        private void SetupAnchorEvents()
        {
            LeftAnchor.MouseEnter += OnAnchorMouseEnter;
            LeftAnchor.MouseLeave += OnAnchorMouseLeave;
            LeftAnchor.MouseDown += (s, e) => OnAnchorMouseDown(PipeAnchorPosition.Left, e);

            RightAnchor.MouseEnter += OnAnchorMouseEnter;
            RightAnchor.MouseLeave += OnAnchorMouseLeave;
            RightAnchor.MouseDown += (s, e) => OnAnchorMouseDown(PipeAnchorPosition.Right, e);

            TopAnchor.MouseEnter += OnAnchorMouseEnter;
            TopAnchor.MouseLeave += OnAnchorMouseLeave;
            TopAnchor.MouseDown += (s, e) => OnAnchorMouseDown(PipeAnchorPosition.Top, e);

            BottomAnchor.MouseEnter += OnAnchorMouseEnter;
            BottomAnchor.MouseLeave += OnAnchorMouseLeave;
            BottomAnchor.MouseDown += (s, e) => OnAnchorMouseDown(PipeAnchorPosition.Bottom, e);

            RotateAnchor.MouseEnter += OnRotateAnchorMouseEnter;
            RotateAnchor.MouseLeave += OnRotateAnchorMouseLeave;
            RotateAnchor.MouseDown += OnRotateAnchorMouseDown;
        }

        #endregion

        #region 整个管道拖动

        private void OnControlMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (IsClickOnAnchor(e.OriginalSource)) return;

            if (e.LeftButton == MouseButtonState.Pressed && !_isResizing && !_isRotating)
            {
                if (e.ClickCount == 2)
                {
                    _isDraggingPipe = false;
                    this.ReleaseMouseCapture();
                    this.Opacity = 1.0;

                    PipeDoubleClicked?.Invoke(this, PipeId);
                    e.Handled = true;
                    return;
                }

                PipeSelected?.Invoke(this, this.PipeId);
                this.IsSelected = true;

                _isDraggingPipe = true;
                var parent = this.Parent as Canvas;
                if (parent != null)
                {
                    _dragStartPoint = e.GetPosition(parent);
                    _originalPosition = new Point(Canvas.GetLeft(this), Canvas.GetTop(this));
                    if (double.IsNaN(_originalPosition.X)) _originalPosition.X = 0;
                    if (double.IsNaN(_originalPosition.Y)) _originalPosition.Y = 0;
                }
                this.CaptureMouse();
                this.Opacity = 0.7;
                PipeDragStarted?.Invoke(this, new PipeDragEventArgs { PipeId = PipeId, StartPosition = _originalPosition });
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
                    PipeDragging?.Invoke(this, new PipeDragEventArgs { PipeId = PipeId, CurrentPosition = new Point(newLeft, newTop) });
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
            var finalPos = new Point(Canvas.GetLeft(this), Canvas.GetTop(this));
            PipeDragCompleted?.Invoke(this, new PipeDragEventArgs
            {
                PipeId = PipeId,
                StartPosition = _originalPosition,
                CurrentPosition = finalPos
            });
            Opacity = 1.0;
            _isDraggingPipe = false;
            ReleaseMouseCapture();
        }

        private bool IsClickOnAnchor(object source)
        {
            return source == LeftAnchor || source == RightAnchor ||
                   source == TopAnchor || source == BottomAnchor ||
                   source == RotateAnchor;
        }

        #endregion

        #region 锚点调整大小

        private void OnAnchorMouseEnter(object sender, MouseEventArgs e)
        {
            if (sender is Ellipse anchor) anchor.Opacity = 1.0;
        }

        private void OnAnchorMouseLeave(object sender, MouseEventArgs e)
        {
            if (sender is Ellipse anchor && !_isResizing) anchor.Opacity = 0.6;
        }

        private void OnAnchorMouseDown(PipeAnchorPosition position, MouseButtonEventArgs e)
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
                    _originalSize = new Size(_pipeLength, _pipeWidth);
                    if (double.IsNaN(_originalPosition.X)) _originalPosition.X = 0;
                    if (double.IsNaN(_originalPosition.Y)) _originalPosition.Y = 0;
                    _fixedPoint = CalculateFixedPoint(position);
                }

                CaptureMouse();
                PipeResizeStarted?.Invoke(this, new PipeResizeEventArgs
                {
                    PipeId = PipeId,
                    AnchorPosition = position,
                    OriginalSize = _originalSize
                });
                e.Handled = true;
            }
        }

        private Point CalculateFixedPoint(PipeAnchorPosition draggedAnchor)
        {
            var parent = Parent as Canvas;
            if (parent == null) return new Point();

            EnsureGeometryUpdated();

            var pipeLeft = Canvas.GetLeft(this);
            var pipeTop = Canvas.GetTop(this);
            if (double.IsNaN(pipeLeft)) pipeLeft = 0;
            if (double.IsNaN(pipeTop)) pipeTop = 0;

            double w = RenderSize.Width > 0 ? RenderSize.Width : Width;
            double h = RenderSize.Height > 0 ? RenderSize.Height : Height;

            Point center = new Point(pipeLeft + w / 2, pipeTop + h / 2);
            double radians = _rotationAngle * Math.PI / 180.0;

            double leftLocalX = (LEFT_FLANGE_LEFT + LEFT_FLANGE_WIDTH / 2) - w / 2;
            double rightLocalX = (w - RIGHT_FLANGE_WIDTH / 2) - w / 2;
            double topLocalY = -_pipeWidth / 2;
            double bottomLocalY = _pipeWidth / 2;

            Point localFixed;
            switch (draggedAnchor)
            {
                case PipeAnchorPosition.Left: localFixed = new Point(rightLocalX, 0); break;
                case PipeAnchorPosition.Right: localFixed = new Point(leftLocalX, 0); break;
                case PipeAnchorPosition.Top: localFixed = new Point(0, bottomLocalY); break;
                case PipeAnchorPosition.Bottom: localFixed = new Point(0, topLocalY); break;
                default: localFixed = new Point(); break;
            }

            Point worldFixed = RotatePoint(localFixed, radians);
            return new Point(center.X + worldFixed.X, center.Y + worldFixed.Y);
        }

        private void EnsureGeometryUpdated()
        {
            if (Width <= 0 || double.IsNaN(Width))
            {
                UpdatePipeGeometry();
            }
        }

        private void HandleResize(MouseEventArgs e)
        {
            if (!_resizingAnchor.HasValue) return;
            var parent = Parent as Canvas;
            if (parent == null) return;

            var currentPoint = e.GetPosition(parent);

            switch (_resizingAnchor.Value)
            {
                case PipeAnchorPosition.Left:
                case PipeAnchorPosition.Right:
                    ResizeLengthFromAnchor(currentPoint);
                    break;
                case PipeAnchorPosition.Top:
                case PipeAnchorPosition.Bottom:
                    ResizeWidthFromAnchor(currentPoint);
                    break;
            }

            PipeResizing?.Invoke(this, new PipeResizeEventArgs
            {
                PipeId = PipeId,
                AnchorPosition = _resizingAnchor.Value,
                CurrentSize = new Size(_pipeLength, _pipeWidth)
            });
        }

        private void ResizeLengthFromAnchor(Point currentMousePosition)
        {
            double deltaX = currentMousePosition.X - _fixedPoint.X;
            double deltaY = currentMousePosition.Y - _fixedPoint.Y;

            double radians = _rotationAngle * Math.PI / 180.0;
            double cos = Math.Cos(radians);
            double sin = Math.Sin(radians);

            double projectedLength = deltaX * cos + deltaY * sin;
            if (_resizingAnchor == PipeAnchorPosition.Left) projectedLength = -projectedLength;

            double newLength = Math.Max(MinPipeLength, Math.Min(MaxPipeLength, Math.Abs(projectedLength)));
            _pipeLength = newLength;

            double fixedLocalX = (_resizingAnchor == PipeAnchorPosition.Right) ? -_pipeLength / 2 : _pipeLength / 2;
            Point fixedOffset = new Point(fixedLocalX * cos, fixedLocalX * sin);
            Point newCenter = new Point(_fixedPoint.X - fixedOffset.X, _fixedPoint.Y - fixedOffset.Y);

            Canvas.SetLeft(this, newCenter.X - ActualWidth / 2);
            Canvas.SetTop(this, newCenter.Y - ActualHeight / 2);
            UpdatePipeGeometry();
        }

        private void ResizeWidthFromAnchor(Point currentMousePosition)
        {
            double deltaX = currentMousePosition.X - _fixedPoint.X;
            double deltaY = currentMousePosition.Y - _fixedPoint.Y;

            double radians = _rotationAngle * Math.PI / 180.0;
            double cos = Math.Cos(radians);
            double sin = Math.Sin(radians);

            double perpDelta = -deltaX * sin + deltaY * cos;
            double newWidth = Math.Max(MinPipeWidth, Math.Min(MaxPipeWidth, Math.Abs(perpDelta)));
            _pipeWidth = newWidth;

            double fixedLocalY = (_resizingAnchor == PipeAnchorPosition.Bottom) ? -_pipeWidth / 2 : _pipeWidth / 2;
            Point fixedOffset = new Point(-fixedLocalY * sin, fixedLocalY * cos);
            Point newCenter = new Point(_fixedPoint.X - fixedOffset.X, _fixedPoint.Y - fixedOffset.Y);

            Canvas.SetLeft(this, newCenter.X - ActualWidth / 2);
            Canvas.SetTop(this, newCenter.Y - ActualHeight / 2);
            UpdatePipeGeometry();
        }

        private void CompleteResize()
        {
            if (!_isResizing) return;
            PipeResizeCompleted?.Invoke(this, new PipeResizeEventArgs
            {
                PipeId = PipeId,
                AnchorPosition = _resizingAnchor.Value,
                OriginalSize = _originalSize,
                CurrentSize = new Size(_pipeLength, _pipeWidth)
            });
            _isResizing = false;
            _resizingAnchor = null;
            ReleaseMouseCapture();
        }

        #endregion

        #region 旋转功能

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

        private void OnRotateAnchorMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                _isRotating = true;
                _originalRotation = _rotationAngle;

                var parent = Parent as Canvas;
                if (parent != null) _dragStartPoint = e.GetPosition(parent);

                CaptureMouse();
                PipeRotateStarted?.Invoke(this, new PipeRotateEventArgs { PipeId = PipeId, OriginalAngle = _originalRotation });
                ShowRotationLabel();
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
            PipeRotating?.Invoke(this, new PipeRotateEventArgs { PipeId = PipeId, CurrentAngle = _rotationAngle });
        }

        private void CompleteRotate()
        {
            if (!_isRotating) return;
            PipeRotateCompleted?.Invoke(this, new PipeRotateEventArgs
            {
                PipeId = PipeId,
                OriginalAngle = _originalRotation,
                CurrentAngle = _rotationAngle
            });
            _isRotating = false;
            ReleaseMouseCapture();
        }

        private void ShowRotationLabel()
        {
            RotationLabel.Visibility = Visibility.Visible;
            _rotationLabelTimer.Stop();
            _rotationLabelTimer.Start();
        }

        #endregion

        #region 选择状态

        public void SetSelected(bool selected) => IsSelected = selected;

        #endregion

        #region 辅助方法

        public Point GetAnchorPosition(PipeAnchorPosition position)
        {
            var parent = Parent as Canvas;
            if (parent == null) return new Point();

            var pipeLeft = Canvas.GetLeft(this);
            var pipeTop = Canvas.GetTop(this);
            if (double.IsNaN(pipeLeft)) pipeLeft = 0;
            if (double.IsNaN(pipeTop)) pipeTop = 0;

            switch (position)
            {
                case PipeAnchorPosition.Left:
                    return new Point(pipeLeft + LEFT_FLANGE_LEFT + LEFT_FLANGE_WIDTH / 2, pipeTop + Height / 2);
                case PipeAnchorPosition.Right:
                    return new Point(pipeLeft + Width - RIGHT_FLANGE_WIDTH / 2, pipeTop + Height / 2);
                case PipeAnchorPosition.Top:
                    return new Point(pipeLeft + Width / 2, pipeTop + 14.8);
                case PipeAnchorPosition.Bottom:
                    return new Point(pipeLeft + Width / 2, pipeTop + 14.8 + _pipeWidth);
                default:
                    return new Point();
            }
        }

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

            double leftFlangeX = (LEFT_FLANGE_LEFT + LEFT_FLANGE_WIDTH / 2) - ActualWidth / 2;
            Point leftWorld = RotatePoint(new Point(leftFlangeX, 0), radians);
            snapPoints.Add(new PipeSnapPoint
            {
                WorldPosition = new Point(center.X + leftWorld.X, center.Y + leftWorld.Y),
                Direction = GetSnapDirectionFromAngle(radians + Math.PI),
                Description = $"直管左法兰 (长度:{_pipeLength:F0})"
            });

            double rightFlangeX = ActualWidth - RIGHT_FLANGE_WIDTH / 2 - ActualWidth / 2;
            Point rightWorld = RotatePoint(new Point(rightFlangeX, 0), radians);
            snapPoints.Add(new PipeSnapPoint
            {
                WorldPosition = new Point(center.X + rightWorld.X, center.Y + rightWorld.Y),
                Direction = GetSnapDirectionFromAngle(radians),
                Description = $"直管右法兰 (长度:{_pipeLength:F0})"
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

    public class PipeDragEventArgs : EventArgs
    {
        public string PipeId { get; set; }
        public Point StartPosition { get; set; }
        public Point CurrentPosition { get; set; }
    }

    public class PipeResizeEventArgs : EventArgs
    {
        public string PipeId { get; set; }
        public PipeAnchorPosition AnchorPosition { get; set; }
        public Size OriginalSize { get; set; }
        public Size CurrentSize { get; set; }
    }

    public class PipeRotateEventArgs : EventArgs
    {
        public string PipeId { get; set; }
        public double OriginalAngle { get; set; }
        public double CurrentAngle { get; set; }
    }

    public enum PipeAnchorPosition
    {
        Left, Right, Top, Bottom
    }

    #endregion
}
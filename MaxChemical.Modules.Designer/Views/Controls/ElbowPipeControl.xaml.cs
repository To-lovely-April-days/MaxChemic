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
        /// <summary>双击事件（打开属性对话框）</summary>
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

        #region 依赖属性 — 管道外观（与 StraightPipeControl 完全一致）

        // ═══ 管道颜色 ═══
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

        // ═══ 法兰颜色 ═══
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

        // ═══ 流体颜色 ═══
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

        // ═══ 流体不透明度 ═══
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

        // ═══ 流体是否可见 ═══
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

        #endregion

        #region 依赖属性 — 流动控制

        // ═══ 是否流动 ═══
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

        // ═══ 流动方向 ═══
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

        // ═══ 流动速度 ═══
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

        // 交互状态
        private bool _isDraggingPipe;
        private bool _isResizing;
        private bool _isRotating;
        private ElbowAnchorPosition? _resizingAnchor;
        private Point _dragStartPoint;
        private Point _originalPosition;
        private ElbowPipeSize _originalSize;
        private double _originalRotation;
        private Point _fixedPoint;

        // 尺寸
        private double _horizontalLength = 50;
        private double _verticalLength = 60;
        private double _pipeWidth = 20;
        private double _rotationAngle;

        // 常量
        private const double MinArmLength = 15;
        private const double MaxArmLength = 10000;
        private const double MinPipeWidth = 4;
        private const double MaxPipeWidth = 10000;
        private const double FlangeWidth = 2.54;
        private const double FlangeOffset = 0.35;
        private const double FLANGE_EXTENSION = 5.8;

        // 变换
        private RotateTransform _rotateTransform;
        private TransformGroup _transformGroup;
        private ScaleTransform _flipTransform;
        private DispatcherTimer _rotationLabelTimer;

        // ═══ 流体动画字段 ═══
        private TranslateTransform _horizontalFlowTransform;
        private TranslateTransform _verticalFlowTransform;
        private DrawingBrush _horizontalPatternBrush;
        private DrawingBrush _verticalPatternBrush;

        // SVG 气泡图案数据（与 StraightPipeControl 一致）
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

        #region CLR 属性（尺寸）

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

            // ★ 初始化外观（使用依赖属性默认值）
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

        /// <summary>
        /// 初始化外观：根据默认属性值生成所有画刷
        /// </summary>
        private void InitializeAppearance()
        {
            ApplyPipeColor();
            ApplyFlangeColor();
            ApplyFluidColor();
            RebuildFluidPatternBrushes();
            ApplyFluidVisibility();
        }

        #endregion

        #region 管道外观 — 管体渐变（动态颜色）

        /// <summary>
        /// 根据 PipeColor 生成水平段 / 垂直段 / 弯头段的圆柱渐变画刷
        /// </summary>
        private void ApplyPipeColor()
        {
            if (HorizontalPipe == null) return;

            var c = PipeColor;

            // ─── 水平管道渐变（上→下）─── 与 StraightPipeControl 相同的 7-stop 对称渐变
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

            // ─── 垂直管道渐变（左→右）───
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

            // ─── 弯头渐变（对角线）───
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

            // ─── 弯头描边 ───
            var strokeBrush = new SolidColorBrush(c);
            strokeBrush.Freeze();
            ElbowPath.Stroke = strokeBrush;

            // ─── 弯头外侧强化边缘 ───
            var outerEdgeStroke = new LinearGradientBrush { StartPoint = new Point(1, 1), EndPoint = new Point(0, 0) };
            outerEdgeStroke.GradientStops.Add(new GradientStop(DarkenColor(c, 50), 0));
            outerEdgeStroke.GradientStops.Add(new GradientStop(DarkenColor(c, 25), 0.5));
            outerEdgeStroke.GradientStops.Add(new GradientStop(Colors.Transparent, 1));
            outerEdgeStroke.Freeze();
            ElbowOuterEdge.Stroke = outerEdgeStroke;

            // ─── 弯头高光 ───
            var highlightStroke = new LinearGradientBrush { StartPoint = new Point(1, 1), EndPoint = new Point(0, 0) };
            highlightStroke.GradientStops.Add(new GradientStop(Colors.Transparent, 0));
            highlightStroke.GradientStops.Add(new GradientStop(LightenColor(c, 100), 0.5));
            highlightStroke.GradientStops.Add(new GradientStop(LightenColor(c, 120), 1));
            highlightStroke.Freeze();
            ElbowHighlight.Stroke = highlightStroke;

            // ─── 法兰边框跟随管道色 ───
            LeftFlangeBorder.Stroke = strokeBrush;
            TopFlangeBorder.Stroke = strokeBrush;

            // ─── 连接处颜色 ───
            LeftConnection.Fill = new SolidColorBrush(c);
            TopConnection.Fill = new SolidColorBrush(c);

            // 流体纹理边带使用管道色 → 需要重建
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

            // 左侧法兰渐变（左→右 = 圆柱感）
            var leftGrad = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
            leftGrad.GradientStops.Add(new GradientStop(c, 0));
            leftGrad.GradientStops.Add(new GradientStop(midColor, 0.5));
            leftGrad.GradientStops.Add(new GradientStop(c, 1));
            leftGrad.Freeze();
            LeftFlangeGradient.Fill = leftGrad;

            // 顶部法兰渐变（上→下 = 圆柱感）
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

        /// <summary>
        /// 重建水平段 + 垂直段的气泡纹理画刷
        /// </summary>
        private void RebuildFluidPatternBrushes()
        {
            if (HorizontalFluidPattern == null) return;

            var pipeC = PipeColor;

            // ── 水平段画刷（与 StraightPipeControl 完全一致）──
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

            // ── 垂直段画刷（边带方向为左/右，配合垂直管道的左右渐变）──
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

            // 如果正在流动，重新启动动画
            if (IsFlowing && IsFluidVisible)
                StartFlowAnimation();
        }

        /// <summary>
        /// 创建气泡 DrawingGroup
        /// </summary>
        /// <param name="pipeC">管道颜色（用于边带）</param>
        /// <param name="isVertical">true = 边带在左/右（用于垂直管道）</param>
        private DrawingGroup CreateBubbleDrawing(Color pipeC, bool isVertical)
        {
            var group = new DrawingGroup();

            // ── 气泡 ──
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

            // ── 边带（让流体融合到管壁颜色）──
            LinearGradientBrush edgeBrush;
            if (!isVertical)
            {
                // 水平管道 → 边带在上/下
                edgeBrush = new LinearGradientBrush { StartPoint = new Point(0.5, 0), EndPoint = new Point(0.5, 1) };
            }
            else
            {
                // 垂直管道 → 边带在左/右
                edgeBrush = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
            }
            edgeBrush.GradientStops.Add(new GradientStop(pipeC, 0));
            edgeBrush.GradientStops.Add(new GradientStop(pipeC, 0.109));
            edgeBrush.GradientStops.Add(new GradientStop(Color.FromArgb(0, 255, 255, 255), 0.113));
            edgeBrush.GradientStops.Add(new GradientStop(Color.FromArgb(0, 255, 255, 255), 0.887));
            edgeBrush.GradientStops.Add(new GradientStop(pipeC, 0.891));
            edgeBrush.GradientStops.Add(new GradientStop(pipeC, 1));
            edgeBrush.Freeze();

            group.Children.Add(new GeometryDrawing(
                edgeBrush, null,
                new RectangleGeometry(new Rect(0, 0, PATTERN_W, PATTERN_H))));

            return group;
        }

        /// <summary>
        /// 根据管道当前宽度，更新纹理平铺尺寸
        /// </summary>
        private void UpdateFluidPatternViewports()
        {
            double tileH = _pipeWidth;
            double tileW = tileH * (PATTERN_W / PATTERN_H);

            if (_horizontalPatternBrush != null)
                _horizontalPatternBrush.Viewport = new Rect(0, 0, tileW, tileH);

            if (_verticalPatternBrush != null)
                _verticalPatternBrush.Viewport = new Rect(0, 0, tileW, tileH);
        }

        #endregion

        #region 流体 — 流动动画

        private void ApplyFlowAnimation()
        {
            if (IsFlowing && IsFluidVisible)
                StartFlowAnimation();
            else
                StopFlowAnimation();
        }

        private void StartFlowAnimation()
        {
            if (_horizontalFlowTransform == null || _verticalFlowTransform == null) return;

            // 停止旧动画
            _horizontalFlowTransform.BeginAnimation(TranslateTransform.XProperty, null);
            _verticalFlowTransform.BeginAnimation(TranslateTransform.YProperty, null);

            double tileW = _pipeWidth * (PATTERN_W / PATTERN_H);
            double baseDurationMs = 1000.0 / Math.Max(0.1, FlowSpeed);

            // ── 水平段动画：左法兰 → 弯头 → 上法兰 ──
            // LeftToRight = 左→上（水平段向右流）
            double hTo = PipeFlowDir == PipeFlowDirection.LeftToRight ? tileW : -tileW;
            var hAnim = new DoubleAnimation
            {
                From = 0,
                To = hTo,
                Duration = TimeSpan.FromMilliseconds(baseDurationMs),
                RepeatBehavior = RepeatBehavior.Forever
            };
            _horizontalFlowTransform.BeginAnimation(TranslateTransform.XProperty, hAnim);

            // ── 垂直段动画 ──
            // LeftToRight 时流体向上流（-Y），RightToLeft 时向下（+Y）
            double vPeriod = _pipeWidth; // 垂直方向的平铺高度
            double vDurationMs = baseDurationMs * (vPeriod / tileW); // 保持同样的视觉速度
            double vTo = PipeFlowDir == PipeFlowDirection.LeftToRight ? -vPeriod : vPeriod;
            var vAnim = new DoubleAnimation
            {
                From = 0,
                To = vTo,
                Duration = TimeSpan.FromMilliseconds(vDurationMs),
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

        #region 辅助方法（颜色计算）

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

            // ═══ 水平管道 ═══
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

            // ═══ 垂直管道 ═══
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

            // ═══ 弯头 ═══
            UpdateElbow(elbowCenterX, elbowCenterY, elbowRadius);

            // ═══ 法兰 ═══
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

            // ═══ 连接处 ═══
            LeftConnection.Height = _pipeWidth;
            Canvas.SetLeft(LeftConnection, FlangeWidth + FlangeOffset);
            Canvas.SetTop(LeftConnection, horizontalCenterY - _pipeWidth / 2);

            TopConnection.Width = _pipeWidth;
            Canvas.SetLeft(TopConnection, verticalCenterX - _pipeWidth / 2);
            Canvas.SetTop(TopConnection, FlangeWidth + FlangeOffset);

            // 选中描边（L 形：水平段 + 垂直段）
            // 两段都延伸到弯头中心，视觉上在拐角处重叠形成完整 L
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

            // ═══ 更新纹理平铺 + 重启动画 ═══
            UpdateFluidPatternViewports();
            if (IsFlowing && IsFluidVisible)
                StartFlowAnimation();
        }

        private void UpdateElbow(double centerX, double centerY, double radius)
        {
            // ── 弯头几何 ──
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

            // 共享几何到白底 + 流体 + 渐变层
            ElbowPipeBackground.Data = elbowGeometry;
            ElbowFluidBackground.Data = elbowGeometry;
            ElbowPath.Data = elbowGeometry;

            // ── 弯头外侧强化边缘 ──
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

            // ── 弯头高光 ──
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
                // ★ 双击 → 打开属性对话框
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
            // 弯头在右,拖左锚点 → projected 应为负;取负值的绝对值得到长度
            double projectedLength = -(deltaX * Math.Cos(radians) + deltaY * Math.Sin(radians));
            double newLength = Math.Max(MinArmLength, Math.Min(MaxArmLength, projectedLength));
            _horizontalLength = newLength;

            UpdatePipeGeometry();         // 先更新几何,Width/Height 才是新值
            RepositionByFixedPoint();     // ★ 用统一的"按固定点反推位置"代替原来的 RecalculatePositionForElbow
        }


        private void ResizeVerticalArmFromTop(Point currentMousePosition)
        {
            EnsureGeometryUpdated();

            double deltaX = currentMousePosition.X - _fixedPoint.X;
            double deltaY = currentMousePosition.Y - _fixedPoint.Y;
            double radians = _rotationAngle * Math.PI / 180.0;
            // 弯头在下,拖上锚点 → projected (Y方向) 应为负
            double projectedLength = -(-deltaX * Math.Sin(radians) + deltaY * Math.Cos(radians));
            double newLength = Math.Max(MinArmLength, Math.Min(MaxArmLength, projectedLength));
            _verticalLength = newLength;

            UpdatePipeGeometry();
            RepositionByFixedPoint();
        }
        /// <summary>
        /// 调整大小后,根据"固定点"反推控件应该贴在画布的哪个 Left/Top
        /// 公式:fixed 点 = center + R(localFixed) → center = fixed - R(localFixed) → Left = center - W/2
        /// 这是直管/弯管/Z管/T管通用的"按固定点回贴"逻辑
        /// </summary>
        private void RepositionByFixedPoint()
        {
            double w = RenderSize.Width > 0 ? RenderSize.Width : Width;
            double h = RenderSize.Height > 0 ? RenderSize.Height : Height;

            // 弯管的固定点:水平管右端 + 垂直管下端 = 弯头交汇点
            // 弯头交汇点的本地坐标(以控件中心为原点):
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

            // 左侧法兰
            double leftFlangeX = FlangeOffset + FlangeWidth / 2 - ActualWidth / 2;
            double leftFlangeY = Canvas.GetTop(LeftFlangeGroup) + LeftFlangeGroup.Height / 2 - ActualHeight / 2;
            Point leftFlangeWorld = RotatePoint(new Point(leftFlangeX, leftFlangeY), radians);
            snapPoints.Add(new PipeSnapPoint
            {
                WorldPosition = new Point(center.X + leftFlangeWorld.X, center.Y + leftFlangeWorld.Y),
                Direction = GetSnapDirectionFromAngle(radians + Math.PI),
                Description = $"弯管水平法兰 (水平:{_horizontalLength:F0})"
            });

            // 顶部法兰
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

    #region 事件参数类（保持不变）

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
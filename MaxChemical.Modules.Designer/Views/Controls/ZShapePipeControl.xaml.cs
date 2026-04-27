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
        /// <summary>双击事件（打开属性对话框）</summary>
        public event EventHandler<string> PipeDoubleClicked;

        #endregion

        #region 依赖属性 — 选择状态

        public static readonly DependencyProperty IsSelectedProperty =
            DependencyProperty.Register(nameof(IsSelected), typeof(bool), typeof(ZShapePipeControl),
                new PropertyMetadata(false, OnIsSelectedChanged));

        public bool IsSelected
        {
            get => (bool)GetValue(IsSelectedProperty);
            set => SetValue(IsSelectedProperty, value);
        }

        private static void OnIsSelectedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is ZShapePipeControl c)
            {
                c.UpdateSelectionVisual((bool)e.NewValue);
                c.OnPropertyChanged(nameof(IsSelected));
            }
        }

        #endregion

        #region 依赖属性 — 管道外观

        // ═══ 管道颜色 ═══
        public static readonly DependencyProperty PipeColorProperty =
            DependencyProperty.Register(nameof(PipeColor), typeof(Color), typeof(ZShapePipeControl),
                new PropertyMetadata(Color.FromRgb(0x72, 0x71, 0x71), OnPipeColorChanged));

        public Color PipeColor
        {
            get => (Color)GetValue(PipeColorProperty);
            set => SetValue(PipeColorProperty, value);
        }

        private static void OnPipeColorChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is ZShapePipeControl c) c.ApplyPipeColor();
        }

        // ═══ 法兰颜色 ═══
        public static readonly DependencyProperty FlangeColorProperty =
            DependencyProperty.Register(nameof(FlangeColor), typeof(Color), typeof(ZShapePipeControl),
                new PropertyMetadata(Color.FromRgb(0xD9, 0xD9, 0xD9), OnFlangeColorChanged));

        public Color FlangeColor
        {
            get => (Color)GetValue(FlangeColorProperty);
            set => SetValue(FlangeColorProperty, value);
        }

        private static void OnFlangeColorChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is ZShapePipeControl c) c.ApplyFlangeColor();
        }

        #endregion

        #region 依赖属性 — 流体

        // ═══ 流体颜色 ═══
        public static readonly DependencyProperty FluidColorProperty =
            DependencyProperty.Register(nameof(FluidColor), typeof(Color), typeof(ZShapePipeControl),
                new PropertyMetadata(Color.FromRgb(0x1E, 0xC1, 0xF4), OnFluidColorChanged));

        public Color FluidColor
        {
            get => (Color)GetValue(FluidColorProperty);
            set => SetValue(FluidColorProperty, value);
        }

        private static void OnFluidColorChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is ZShapePipeControl c) c.ApplyFluidColor();
        }

        // ═══ 流体不透明度 ═══
        public static readonly DependencyProperty FluidOpacityProperty =
            DependencyProperty.Register(nameof(FluidOpacity), typeof(double), typeof(ZShapePipeControl),
                new PropertyMetadata(0.5, OnFluidOpacityChanged));

        public double FluidOpacity
        {
            get => (double)GetValue(FluidOpacityProperty);
            set => SetValue(FluidOpacityProperty, value);
        }

        private static void OnFluidOpacityChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is ZShapePipeControl c) c.ApplyFluidOpacity();
        }

        // ═══ 流体是否可见 ═══
        public static readonly DependencyProperty IsFluidVisibleProperty =
            DependencyProperty.Register(nameof(IsFluidVisible), typeof(bool), typeof(ZShapePipeControl),
                new PropertyMetadata(false, OnIsFluidVisibleChanged));

        public bool IsFluidVisible
        {
            get => (bool)GetValue(IsFluidVisibleProperty);
            set => SetValue(IsFluidVisibleProperty, value);
        }

        private static void OnIsFluidVisibleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is ZShapePipeControl c) c.ApplyFluidVisibility();
        }

        #endregion

        #region 依赖属性 — 流动控制

        // ═══ 是否流动 ═══
        public static readonly DependencyProperty IsFlowingProperty =
            DependencyProperty.Register(nameof(IsFlowing), typeof(bool), typeof(ZShapePipeControl),
                new PropertyMetadata(false, OnIsFlowingChanged));

        public bool IsFlowing
        {
            get => (bool)GetValue(IsFlowingProperty);
            set => SetValue(IsFlowingProperty, value);
        }

        private static void OnIsFlowingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is ZShapePipeControl c) c.ApplyFlowAnimation();
        }

        // ═══ 流动方向 ═══
        public static readonly DependencyProperty PipeFlowDirProperty =
            DependencyProperty.Register(nameof(PipeFlowDir), typeof(PipeFlowDirection), typeof(ZShapePipeControl),
                new PropertyMetadata(PipeFlowDirection.LeftToRight, OnPipeFlowDirChanged));

        public PipeFlowDirection PipeFlowDir
        {
            get => (PipeFlowDirection)GetValue(PipeFlowDirProperty);
            set => SetValue(PipeFlowDirProperty, value);
        }

        private static void OnPipeFlowDirChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is ZShapePipeControl c) c.ApplyFlowAnimation();
        }

        // ═══ 流动速度 ═══
        public static readonly DependencyProperty FlowSpeedProperty =
            DependencyProperty.Register(nameof(FlowSpeed), typeof(double), typeof(ZShapePipeControl),
                new PropertyMetadata(1.0, OnFlowSpeedChanged));

        public double FlowSpeed
        {
            get => (double)GetValue(FlowSpeedProperty);
            set => SetValue(FlowSpeedProperty, value);
        }

        private static void OnFlowSpeedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is ZShapePipeControl c) c.ApplyFlowAnimation();
        }

        #endregion

        #region 私有字段

        // 交互状态
        private bool _isDraggingPipe;
        private bool _isResizing;
        private bool _isRotating;
        private ZAnchorPosition? _resizingAnchor;
        private Point _dragStartPoint;
        private Point _originalPosition;
        private ZPipeSize _originalSize;
        private double _originalRotation;

        // 尺寸常量
        private const double MinHorizontalLength = 30;
        private const double MaxHorizontalLength = 200;
        private const double MinVerticalOffset = 40;
        private const double MaxVerticalOffset = 150;
        private const double MinPipeWidth = 8;
        private const double MaxPipeWidth = 40;
        private const double FlangeWidth = 2.54;
        private const double FlangeOffset = 0.35;
        private const double FlangeSpacing = 2.0;
        private const double FLANGE_EXTENSION = 5.8;

        // 尺寸字段
        private double _topHorizontalLength = 15;
        private double _bottomHorizontalLength = 15;
        private double _verticalOffset = 20;
        private double _pipeWidth = 20;
        private double _rotationAngle;

        // 变换
        private RotateTransform _rotateTransform;
        private TransformGroup _transformGroup;
        private DispatcherTimer _rotationLabelTimer;

        // ═══ 流体动画字段 ═══
        private TranslateTransform _topHorizFlowTransform;
        private TranslateTransform _diagFlowTransform;
        private TranslateTransform _bottomHorizFlowTransform;
        private DrawingBrush _topHorizPatternBrush;
        private DrawingBrush _diagPatternBrush;
        private DrawingBrush _bottomHorizPatternBrush;

        // 气泡图案常量（与 StraightPipeControl / ElbowPipeControl 一致）
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

        public string PipeId { get; set; }

        public double TopHorizontalLength
        {
            get => _topHorizontalLength;
            set
            {
                var nv = Math.Max(MinHorizontalLength, Math.Min(MaxHorizontalLength, value));
                if (Math.Abs(_topHorizontalLength - nv) > 0.01)
                {
                    _topHorizontalLength = nv;
                    UpdatePipeGeometry();
                    OnPropertyChanged(nameof(TopHorizontalLength));
                }
            }
        }

        public double BottomHorizontalLength
        {
            get => _bottomHorizontalLength;
            set
            {
                var nv = Math.Max(MinHorizontalLength, Math.Min(MaxHorizontalLength, value));
                if (Math.Abs(_bottomHorizontalLength - nv) > 0.01)
                {
                    _bottomHorizontalLength = nv;
                    UpdatePipeGeometry();
                    OnPropertyChanged(nameof(BottomHorizontalLength));
                }
            }
        }

        public double VerticalOffset
        {
            get => _verticalOffset;
            set
            {
                var nv = Math.Max(MinVerticalOffset, Math.Min(MaxVerticalOffset, value));
                if (Math.Abs(_verticalOffset - nv) > 0.01)
                {
                    _verticalOffset = nv;
                    UpdatePipeGeometry();
                    OnPropertyChanged(nameof(VerticalOffset));
                }
            }
        }

        public double PipeWidth
        {
            get => _pipeWidth;
            set
            {
                var nv = Math.Max(MinPipeWidth, Math.Min(MaxPipeWidth, value));
                if (Math.Abs(_pipeWidth - nv) > 0.01)
                {
                    _pipeWidth = nv;
                    UpdatePipeGeometry();
                    OnPropertyChanged(nameof(PipeWidth));
                }
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

        #region 构造函数

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

            // ★ 初始化外观
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

            // 硬件加速
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
        }

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

        private void ApplyPipeColor()
        {
            if (TopHorizontalPipe == null) return;

            var c = PipeColor;

            // ─── 水平管道渐变（上→下）───
            var hGrad = MakeCylinderGradient(c, new Point(0, 0), new Point(0, 1));
            TopHorizontalPipe.Fill = hGrad;
            BottomHorizontalPipe.Fill = hGrad;

            // ─── 垂直管道渐变（左→右）───
            var vGrad = MakeCylinderGradient(c, new Point(0, 0), new Point(1, 0));
            DiagonalPipe.Fill = vGrad;

            // ─── 弯头渐变 ───
            ApplyElbowGradient(TopElbowPath, c, new Point(1, 0), new Point(0, 1));
            ApplyElbowGradient(BottomElbowPath, c, new Point(0, 1), new Point(1, 0));

            // ─── 弯头高光 ───
            ApplyElbowHighlight(TopElbowHighlight, c, new Point(1, 0), new Point(0, 1));
            ApplyElbowHighlight(BottomElbowHighlight, c, new Point(0, 1), new Point(1, 0));

            // ─── 描边 / 边框 / 连接处 ───
            var strokeBrush = new SolidColorBrush(c);
            strokeBrush.Freeze();

            TopElbowPath.Stroke = strokeBrush;
            BottomElbowPath.Stroke = strokeBrush;
            LeftFlangeBorder.Stroke = strokeBrush;
            RightFlangeBorder.Stroke = strokeBrush;
            LeftConnection.Fill = strokeBrush;
            RightConnection.Fill = strokeBrush;

            // 纹理边带要跟管道色 → 需要重建
            RebuildFluidPatternBrushes();
        }

        /// <summary>
        /// 创建 7-stop 圆柱形渐变
        /// </summary>
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

        /// <summary>
        /// 弯头 13-stop 渐变填充
        /// </summary>
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

        /// <summary>
        /// 弯头高光
        /// </summary>
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

            // 左法兰渐变（水平方向）
            var leftGrad = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
            leftGrad.GradientStops.Add(new GradientStop(c, 0));
            leftGrad.GradientStops.Add(new GradientStop(midColor, 0.5));
            leftGrad.GradientStops.Add(new GradientStop(c, 1));
            leftGrad.Freeze();
            LeftFlangeGradient.Fill = leftGrad;

            // 右法兰渐变（水平方向，反方向）
            var rightGrad = new LinearGradientBrush { StartPoint = new Point(1, 0.5), EndPoint = new Point(0, 0.5) };
            rightGrad.GradientStops.Add(new GradientStop(c, 0));
            rightGrad.GradientStops.Add(new GradientStop(midColor, 0.5));
            rightGrad.GradientStops.Add(new GradientStop(c, 1));
            rightGrad.Freeze();
            RightFlangeGradient.Fill = rightGrad;
        }

        #endregion

        #region 流体 — 颜色、透明度与可见性

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

            var vis = IsFluidVisible ? Visibility.Visible : Visibility.Collapsed;

            TopHorizontalFluidBackground.Visibility = vis;
            TopHorizontalFluidPattern.Visibility = vis;
            DiagonalFluidBackground.Visibility = vis;
            DiagonalFluidPattern.Visibility = vis;
            BottomHorizontalFluidBackground.Visibility = vis;
            BottomHorizontalFluidPattern.Visibility = vis;
            TopElbowFluidBackground.Visibility = vis;
            BottomElbowFluidBackground.Visibility = vis;

            if (IsFluidVisible && IsFlowing)
                StartFlowAnimation();
            else
                StopFlowAnimation();
        }

        #endregion

        #region 流体 — 气泡纹理画刷

        private void RebuildFluidPatternBrushes()
        {
            if (TopHorizontalFluidPattern == null) return;

            var pipeC = PipeColor;

            // ── 水平段画刷（边带在上/下）──
            var hDrawing = CreateBubbleDrawing(pipeC, false);
            _topHorizFlowTransform = new TranslateTransform(0, 0);
            _topHorizPatternBrush = new DrawingBrush(hDrawing)
            {
                TileMode = TileMode.Tile,
                Viewbox = new Rect(0, 0, PATTERN_W, PATTERN_H),
                ViewboxUnits = BrushMappingMode.Absolute,
                ViewportUnits = BrushMappingMode.Absolute,
                Transform = _topHorizFlowTransform
            };
            TopHorizontalFluidPattern.Fill = _topHorizPatternBrush;

            _bottomHorizFlowTransform = new TranslateTransform(0, 0);
            var bottomHorizBrush = new DrawingBrush(hDrawing)
            {
                TileMode = TileMode.Tile,
                Viewbox = new Rect(0, 0, PATTERN_W, PATTERN_H),
                ViewboxUnits = BrushMappingMode.Absolute,
                ViewportUnits = BrushMappingMode.Absolute,
                Transform = _bottomHorizFlowTransform
            };
            BottomHorizontalFluidPattern.Fill = bottomHorizBrush;
            _bottomHorizPatternBrush = bottomHorizBrush;

            // ── 垂直段画刷（边带在左/右）──
            var vDrawing = CreateBubbleDrawing(pipeC, true);
            _diagFlowTransform = new TranslateTransform(0, 0);
            _diagPatternBrush = new DrawingBrush(vDrawing)
            {
                TileMode = TileMode.Tile,
                Viewbox = new Rect(0, 0, PATTERN_W, PATTERN_H),
                ViewboxUnits = BrushMappingMode.Absolute,
                ViewportUnits = BrushMappingMode.Absolute,
                Transform = _diagFlowTransform
            };
            DiagonalFluidPattern.Fill = _diagPatternBrush;

            UpdateFluidPatternViewports();

            if (IsFlowing && IsFluidVisible)
                StartFlowAnimation();
        }

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

            // ── 边带 ──
            LinearGradientBrush edgeBrush;
            if (!isVertical)
                edgeBrush = new LinearGradientBrush { StartPoint = new Point(0.5, 0), EndPoint = new Point(0.5, 1) };
            else
                edgeBrush = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };

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

        private void UpdateFluidPatternViewports()
        {
            double tileH = _pipeWidth;
            double tileW = tileH * (PATTERN_W / PATTERN_H);

            if (_topHorizPatternBrush != null)
                _topHorizPatternBrush.Viewport = new Rect(0, 0, tileW, tileH);
            if (_bottomHorizPatternBrush != null)
                _bottomHorizPatternBrush.Viewport = new Rect(0, 0, tileW, tileH);
            if (_diagPatternBrush != null)
                _diagPatternBrush.Viewport = new Rect(0, 0, tileW, tileH);
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
            if (_topHorizFlowTransform == null) return;

            StopFlowAnimation();

            double tileW = _pipeWidth * (PATTERN_W / PATTERN_H);
            double baseDurationMs = 1000.0 / Math.Max(0.1, FlowSpeed);

            // Z形管道流向：LeftToRight = 左法兰→上段→弯头→中间段(向下)→弯头→下段→右法兰
            bool leftToRight = PipeFlowDir == PipeFlowDirection.LeftToRight;

            // ── 上水平段 ──
            double topTo = leftToRight ? tileW : -tileW;
            _topHorizFlowTransform.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation { From = 0, To = topTo, Duration = TimeSpan.FromMilliseconds(baseDurationMs), RepeatBehavior = RepeatBehavior.Forever });

            // ── 中间垂直段 ──
            double vPeriod = _pipeWidth;
            double vDurationMs = baseDurationMs * (vPeriod / tileW);
            double vTo = leftToRight ? vPeriod : -vPeriod;
            _diagFlowTransform.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation { From = 0, To = vTo, Duration = TimeSpan.FromMilliseconds(vDurationMs), RepeatBehavior = RepeatBehavior.Forever });

            // ── 下水平段 ──
            double bottomTo = leftToRight ? tileW : -tileW;
            _bottomHorizFlowTransform.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation { From = 0, To = bottomTo, Duration = TimeSpan.FromMilliseconds(baseDurationMs), RepeatBehavior = RepeatBehavior.Forever });
        }

        private void StopFlowAnimation()
        {
            if (_topHorizFlowTransform != null)
            {
                _topHorizFlowTransform.BeginAnimation(TranslateTransform.XProperty, null);
                _topHorizFlowTransform.X = 0;
            }
            if (_diagFlowTransform != null)
            {
                _diagFlowTransform.BeginAnimation(TranslateTransform.YProperty, null);
                _diagFlowTransform.Y = 0;
            }
            if (_bottomHorizFlowTransform != null)
            {
                _bottomHorizFlowTransform.BeginAnimation(TranslateTransform.XProperty, null);
                _bottomHorizFlowTransform.X = 0;
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

        #region 锚点事件设置

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
        {
            if (sender is Ellipse anchor && !_isRotating) anchor.Opacity = 1.0;
        }

        private void OnAnchorMouseLeave(object sender, MouseEventArgs e)
        {
            if (sender is Ellipse anchor && !_isResizing && !_isRotating) anchor.Opacity = 0.6;
        }

        private void OnRotateAnchorMouseEnter(object sender, MouseEventArgs e)
        {
            if (!_isRotating) { RotateAnchor.Opacity = 1.0; RotateAnchorLine.Opacity = 1.0; }
        }

        private void OnRotateAnchorMouseLeave(object sender, MouseEventArgs e)
        {
            if (!_isRotating) { RotateAnchor.Opacity = 0.6; RotateAnchorLine.Opacity = 0.6; }
        }

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
                _dragStartPoint = e.GetPosition(Parent as UIElement);
                _originalPosition = new Point(Canvas.GetLeft(this), Canvas.GetTop(this));
                if (double.IsNaN(_originalPosition.X)) _originalPosition.X = 0;
                if (double.IsNaN(_originalPosition.Y)) _originalPosition.Y = 0;

                // 拖拽优化
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

        private void OnControlMouseLeave(object sender, MouseEventArgs e)
        {
            // 拖拽期间鼠标离开不做处理
        }

        #endregion

        #region 调整大小

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
            if (_isResizing && e.LeftButton == MouseButtonState.Pressed && _resizingAnchor.HasValue)
            {
                Point currentPoint = e.GetPosition(Parent as UIElement);
                double deltaX = currentPoint.X - _dragStartPoint.X;
                double deltaY = currentPoint.Y - _dragStartPoint.Y;

                switch (_resizingAnchor.Value)
                {
                    case ZAnchorPosition.TopLength:
                        TopHorizontalLength = _originalSize.TopHorizontalLength - deltaX;
                        break;
                    case ZAnchorPosition.BottomLength:
                        BottomHorizontalLength = _originalSize.BottomHorizontalLength + deltaX;
                        break;
                    case ZAnchorPosition.VerticalOffset:
                        VerticalOffset = _originalSize.VerticalOffset + deltaY;
                        break;
                    case ZAnchorPosition.PipeWidth:
                        PipeWidth = _originalSize.PipeWidth + deltaY;
                        break;
                }

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

                // ═══ 左侧法兰 ═══
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

                // ═══ 上方水平管道 ═══
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

                // ═══ 上弯头 ═══
                double elbow1Radius = _pipeWidth;
                double elbow1CenterX = topPipeLeft + topPipeWidth + elbow1Radius;
                double elbow1CenterY = topPipeTop + _pipeWidth / 2;
                UpdateTopElbow(elbow1CenterX, elbow1CenterY, elbow1Radius);

                // ═══ 中间垂直管道 ═══
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

                // ═══ 下弯头 ═══
                double elbow2Radius = _pipeWidth;
                double elbow2CenterX = diagPipeLeft + _pipeWidth / 2;
                double elbow2CenterY = diagPipeTop + diagPipeHeight + elbow2Radius;
                UpdateBottomElbow(elbow2CenterX, elbow2CenterY, elbow2Radius);

                // ═══ 下方水平管道 ═══
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

                // ═══ 右侧法兰 ═══
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

                // ═══ 连接处 ═══
                Canvas.SetLeft(LeftConnection, FlangeOffset + FlangeWidth);
                Canvas.SetTop(LeftConnection, topPipeTop);
                LeftConnection.Height = _pipeWidth;

                Canvas.SetLeft(RightConnection, rightFlangeX - 2);
                Canvas.SetTop(RightConnection, bottomPipeTop);
                RightConnection.Height = _pipeWidth;

                // ═══ 尺寸 + 锚点 ═══
                UpdateAnchorPositions(topPipeLeft, topPipeTop, rightFlangeX, rightFlangeY,
                    diagPipeLeft, diagPipeTop, diagPipeHeight);

                double totalWidth = rightFlangeX + FlangeWidth + 4;
                double totalHeight = Math.Max(rightFlangeY + flangeHeight, leftFlangeTop + flangeHeight) + 4;
                Width = totalWidth;
                Height = totalHeight;

                // 选中描边（Z 形：3 段）
                // 上水平段：从左端延伸到弯头1中心
                SelectionBorder.Width = (elbow1CenterX + _pipeWidth / 2) - topPipeLeft;
                SelectionBorder.Height = _pipeWidth;
                Canvas.SetLeft(SelectionBorder, topPipeLeft);
                Canvas.SetTop(SelectionBorder, topPipeTop);

                // 中间垂直段：从弯头1中心到弯头2中心
                SelectionBorderM.Width = _pipeWidth;
                SelectionBorderM.Height = (elbow2CenterY + _pipeWidth / 2) - (elbow1CenterY - _pipeWidth / 2);
                Canvas.SetLeft(SelectionBorderM, diagPipeLeft);
                Canvas.SetTop(SelectionBorderM, elbow1CenterY - _pipeWidth / 2);

                // 下水平段：从弯头2中心到右端
                SelectionBorderB.Width = (bottomPipeLeft + bottomPipeWidth) - (elbow2CenterX - _pipeWidth / 2);
                SelectionBorderB.Height = _pipeWidth;
                Canvas.SetLeft(SelectionBorderB, elbow2CenterX - _pipeWidth / 2);
                Canvas.SetTop(SelectionBorderB, bottomPipeTop);

                PipeCanvas.EndInit();

                // ═══ 更新纹理 + 动画 ═══
                UpdateFluidPatternViewports();
                if (IsFlowing && IsFluidVisible) StartFlowAnimation();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"UpdatePipeGeometry error: {ex.Message}");
            }
        }

        /// <summary>
        /// 统一设置 Rectangle 的位置和尺寸
        /// </summary>
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

            // 左法兰
            double leftFX = 4 - ActualWidth / 2;
            double leftFY = Canvas.GetTop(LeftFlangeGroup) + LeftFlangeGroup.Height / 2 - ActualHeight / 2;
            Point leftWorld = RotatePoint(new Point(leftFX, leftFY), radians);
            snapPoints.Add(new PipeSnapPoint
            {
                WorldPosition = new Point(center.X + leftWorld.X, center.Y + leftWorld.Y),
                Direction = GetSnapDirectionFromAngle(radians + Math.PI),
                Description = $"Z形管道左上法兰 (上段:{_topHorizontalLength:F0})"
            });

            // 右法兰
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

    #region 辅助类和枚举（保持不变）

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
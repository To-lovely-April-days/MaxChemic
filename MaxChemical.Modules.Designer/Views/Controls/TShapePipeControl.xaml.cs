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
    /// 三角通（T型管道）控件 — 实现 IPipeProperties 以支持统一属性对话框
    /// </summary>
    public partial class TShapePipeControl : UserControl, INotifyPropertyChanged, IPipeSnapPoints, IPipeProperties
    {
        #region 事件定义

        public event PropertyChangedEventHandler PropertyChanged;
        public event EventHandler<TPipeDragEventArgs> PipeDragStarted;
        public event EventHandler<TPipeDragEventArgs> PipeDragging;
        public event EventHandler<TPipeDragEventArgs> PipeDragCompleted;
        public event EventHandler<TPipeResizeEventArgs> PipeResizeStarted;
        public event EventHandler<TPipeResizeEventArgs> PipeResizing;
        public event EventHandler<TPipeResizeEventArgs> PipeResizeCompleted;
        public event EventHandler<TPipeRotateEventArgs> PipeRotateStarted;
        public event EventHandler<TPipeRotateEventArgs> PipeRotating;
        public event EventHandler<TPipeRotateEventArgs> PipeRotateCompleted;
        public event EventHandler<string> PipeSelected;
        public event EventHandler<string> PipeDeselected;
        /// <summary>双击事件（打开属性对话框）</summary>
        public event EventHandler<string> PipeDoubleClicked;

        #endregion

        #region 依赖属性 — 选择状态

        public static readonly DependencyProperty IsSelectedProperty =
            DependencyProperty.Register(nameof(IsSelected), typeof(bool), typeof(TShapePipeControl),
                new PropertyMetadata(false, OnIsSelectedChanged));

        public bool IsSelected
        {
            get => (bool)GetValue(IsSelectedProperty);
            set => SetValue(IsSelectedProperty, value);
        }

        private static void OnIsSelectedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is TShapePipeControl c)
            {
                c.UpdateSelectionVisual((bool)e.NewValue);
                c.OnPropertyChanged(nameof(IsSelected));
            }
        }

        #endregion

        #region 依赖属性 — 管道外观

        public static readonly DependencyProperty PipeColorProperty =
            DependencyProperty.Register(nameof(PipeColor), typeof(Color), typeof(TShapePipeControl),
                new PropertyMetadata(Color.FromRgb(0x72, 0x71, 0x71), OnPipeColorChanged));

        public Color PipeColor
        {
            get => (Color)GetValue(PipeColorProperty);
            set => SetValue(PipeColorProperty, value);
        }

        private static void OnPipeColorChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is TShapePipeControl c) c.ApplyPipeColor();
        }

        public static readonly DependencyProperty FlangeColorProperty =
            DependencyProperty.Register(nameof(FlangeColor), typeof(Color), typeof(TShapePipeControl),
                new PropertyMetadata(Color.FromRgb(0xD9, 0xD9, 0xD9), OnFlangeColorChanged));

        public Color FlangeColor
        {
            get => (Color)GetValue(FlangeColorProperty);
            set => SetValue(FlangeColorProperty, value);
        }

        private static void OnFlangeColorChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is TShapePipeControl c) c.ApplyFlangeColor();
        }

        #endregion

        #region 依赖属性 — 流体

        public static readonly DependencyProperty FluidColorProperty =
            DependencyProperty.Register(nameof(FluidColor), typeof(Color), typeof(TShapePipeControl),
                new PropertyMetadata(Color.FromRgb(0x1E, 0xC1, 0xF4), OnFluidColorChanged));

        public Color FluidColor
        {
            get => (Color)GetValue(FluidColorProperty);
            set => SetValue(FluidColorProperty, value);
        }

        private static void OnFluidColorChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is TShapePipeControl c) c.ApplyFluidColor();
        }

        public static readonly DependencyProperty FluidOpacityProperty =
            DependencyProperty.Register(nameof(FluidOpacity), typeof(double), typeof(TShapePipeControl),
                new PropertyMetadata(0.5, OnFluidOpacityChanged));

        public double FluidOpacity
        {
            get => (double)GetValue(FluidOpacityProperty);
            set => SetValue(FluidOpacityProperty, value);
        }

        private static void OnFluidOpacityChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is TShapePipeControl c) c.ApplyFluidOpacity();
        }

        public static readonly DependencyProperty IsFluidVisibleProperty =
            DependencyProperty.Register(nameof(IsFluidVisible), typeof(bool), typeof(TShapePipeControl),
                new PropertyMetadata(false, OnIsFluidVisibleChanged));

        public bool IsFluidVisible
        {
            get => (bool)GetValue(IsFluidVisibleProperty);
            set => SetValue(IsFluidVisibleProperty, value);
        }

        private static void OnIsFluidVisibleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is TShapePipeControl c) c.ApplyFluidVisibility();
        }

        #endregion

        #region 依赖属性 — 流动控制

        public static readonly DependencyProperty IsFlowingProperty =
            DependencyProperty.Register(nameof(IsFlowing), typeof(bool), typeof(TShapePipeControl),
                new PropertyMetadata(false, OnIsFlowingChanged));

        public bool IsFlowing
        {
            get => (bool)GetValue(IsFlowingProperty);
            set => SetValue(IsFlowingProperty, value);
        }

        private static void OnIsFlowingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is TShapePipeControl c) c.ApplyFlowAnimation();
        }

        public static readonly DependencyProperty PipeFlowDirProperty =
            DependencyProperty.Register(nameof(PipeFlowDir), typeof(PipeFlowDirection), typeof(TShapePipeControl),
                new PropertyMetadata(PipeFlowDirection.LeftToRight, OnPipeFlowDirChanged));

        public PipeFlowDirection PipeFlowDir
        {
            get => (PipeFlowDirection)GetValue(PipeFlowDirProperty);
            set => SetValue(PipeFlowDirProperty, value);
        }

        private static void OnPipeFlowDirChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is TShapePipeControl c) c.ApplyFlowAnimation();
        }

        public static readonly DependencyProperty FlowSpeedProperty =
            DependencyProperty.Register(nameof(FlowSpeed), typeof(double), typeof(TShapePipeControl),
                new PropertyMetadata(1.0, OnFlowSpeedChanged));

        public double FlowSpeed
        {
            get => (double)GetValue(FlowSpeedProperty);
            set => SetValue(FlowSpeedProperty, value);
        }

        private static void OnFlowSpeedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is TShapePipeControl c) c.ApplyFlowAnimation();
        }

        #endregion

        #region 私有字段

        // 交互状态
        private bool _isDraggingPipe;
        private bool _isResizing;
        private bool _isRotating;
        private TAnchorPosition? _resizingAnchor;
        private Point _dragStartPoint;
        private Point _originalPosition;
        private TPipeSize _originalSize;
        private double _originalRotation;

        // 尺寸常量
        private const double MinMainLength = 30;
        private const double MaxMainLength = 300;
        private const double MinBranchLength = 15;
        private const double MaxBranchLength = 150;
        private const double MinPipeWidth = 4;
        private const double MaxPipeWidth = 40;
        private const double FlangeWidthConst = 2.54;
        private const double FlangeOffset = 0.35;
        private const double FlangeSpacing = 2.0;
        private const double FLANGE_EXTENSION = 5.8;
        private const double MAIN_PIPE_CENTER_Y = 70;

        // 尺寸字段
        private double _mainPipeLength = 120;
        private double _branchPipeLength = 54;
        private double _branchPosition = 60;
        private double _pipeWidth = 20;
        private double _rotationAngle;

        // 变换
        private RotateTransform _rotateTransform;
        private TransformGroup _transformGroup;
        private DispatcherTimer _rotationLabelTimer;

        // ═══ 流体动画字段 ═══
        private TranslateTransform _mainHorizFlowTransform;
        private TranslateTransform _branchFlowTransform;
        private DrawingBrush _mainHorizPatternBrush;
        private DrawingBrush _branchPatternBrush;

        // 气泡图案常量（与其它管道控件一致）
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

        public double MainPipeLength
        {
            get => _mainPipeLength;
            set
            {
                var nv = Math.Max(MinMainLength, Math.Min(MaxMainLength, value));
                if (Math.Abs(_mainPipeLength - nv) > 0.01)
                {
                    _mainPipeLength = nv;
                    if (_branchPosition > _mainPipeLength - _pipeWidth)
                        _branchPosition = _mainPipeLength / 2;
                    UpdatePipeGeometry();
                    OnPropertyChanged(nameof(MainPipeLength));
                }
            }
        }

        public double BranchPipeLength
        {
            get => _branchPipeLength;
            set
            {
                var nv = Math.Max(MinBranchLength, Math.Min(MaxBranchLength, value));
                if (Math.Abs(_branchPipeLength - nv) > 0.01)
                {
                    _branchPipeLength = nv;
                    UpdatePipeGeometry();
                    OnPropertyChanged(nameof(BranchPipeLength));
                }
            }
        }

        public double BranchPosition
        {
            get => _branchPosition;
            set
            {
                var minPos = _pipeWidth;
                var maxPos = _mainPipeLength - _pipeWidth;
                var nv = Math.Max(minPos, Math.Min(maxPos, value));
                if (Math.Abs(_branchPosition - nv) > 0.01)
                {
                    _branchPosition = nv;
                    UpdatePipeGeometry();
                    OnPropertyChanged(nameof(BranchPosition));
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

        public TShapePipeControl()
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

        #region 管道外观 — 管体渐变

        private void ApplyPipeColor()
        {
            if (MainHorizontalPipe == null) return;

            var c = PipeColor;

            // ─── 主水平管道渐变（上→下）───
            var hGrad = MakeCylinderGradient(c, new Point(0, 0), new Point(0, 1));
            MainHorizontalPipe.Fill = hGrad;

            // ─── 支管垂直渐变（左→右）───
            var vGrad = MakeCylinderGradient(c, new Point(0, 0), new Point(1, 0));
            BranchVerticalPipe.Fill = vGrad;

            // ─── T型连接处 ───
            var junctionBrush = new SolidColorBrush(DarkenColor(c, 15));
            junctionBrush.Freeze();
            TJunction.Fill = junctionBrush;

            // ─── 描边 / 边框 / 连接处 ───
            var strokeBrush = new SolidColorBrush(c);
            strokeBrush.Freeze();
            LeftFlangeBorder.Stroke = strokeBrush;
            RightFlangeBorder.Stroke = strokeBrush;
            TopFlangeBorder.Stroke = strokeBrush;

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
            TopFlangeBase.Fill = baseBrush;

            var midColor = BlendColors(c, Colors.Gray, 0.4);

            // 左法兰渐变（水平）
            var leftGrad = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
            leftGrad.GradientStops.Add(new GradientStop(c, 0));
            leftGrad.GradientStops.Add(new GradientStop(midColor, 0.5));
            leftGrad.GradientStops.Add(new GradientStop(c, 1));
            leftGrad.Freeze();
            LeftFlangeGradient.Fill = leftGrad;

            // 右法兰渐变（水平，反方向）
            var rightGrad = new LinearGradientBrush { StartPoint = new Point(1, 0.5), EndPoint = new Point(0, 0.5) };
            rightGrad.GradientStops.Add(new GradientStop(c, 0));
            rightGrad.GradientStops.Add(new GradientStop(midColor, 0.5));
            rightGrad.GradientStops.Add(new GradientStop(c, 1));
            rightGrad.Freeze();
            RightFlangeGradient.Fill = rightGrad;

            // 顶法兰渐变（垂直）
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
            if (MainHorizontalFluidBackground == null) return;

            var brush = new SolidColorBrush(FluidColor);
            MainHorizontalFluidBackground.Fill = brush;
            BranchFluidBackground.Fill = brush;

            ApplyFluidOpacity();
        }

        private void ApplyFluidOpacity()
        {
            if (MainHorizontalFluidBackground == null) return;

            MainHorizontalFluidBackground.Opacity = FluidOpacity;
            BranchFluidBackground.Opacity = FluidOpacity;
        }

        private void ApplyFluidVisibility()
        {
            if (MainHorizontalFluidBackground == null) return;

            var vis = IsFluidVisible ? Visibility.Visible : Visibility.Collapsed;

            MainHorizontalFluidBackground.Visibility = vis;
            MainHorizontalFluidPattern.Visibility = vis;
            BranchFluidBackground.Visibility = vis;
            BranchFluidPattern.Visibility = vis;

            if (IsFluidVisible && IsFlowing)
                StartFlowAnimation();
            else
                StopFlowAnimation();
        }

        #endregion

        #region 流体 — 气泡纹理画刷

        private void RebuildFluidPatternBrushes()
        {
            if (MainHorizontalFluidPattern == null) return;

            var pipeC = PipeColor;

            // ── 主水平段画刷（边带上/下）──
            var hDrawing = CreateBubbleDrawing(pipeC, false);
            _mainHorizFlowTransform = new TranslateTransform(0, 0);
            _mainHorizPatternBrush = new DrawingBrush(hDrawing)
            {
                TileMode = TileMode.Tile,
                Viewbox = new Rect(0, 0, PATTERN_W, PATTERN_H),
                ViewboxUnits = BrushMappingMode.Absolute,
                ViewportUnits = BrushMappingMode.Absolute,
                Transform = _mainHorizFlowTransform
            };
            MainHorizontalFluidPattern.Fill = _mainHorizPatternBrush;

            // ── 支管垂直段画刷（边带左/右）──
            var vDrawing = CreateBubbleDrawing(pipeC, true);
            _branchFlowTransform = new TranslateTransform(0, 0);
            _branchPatternBrush = new DrawingBrush(vDrawing)
            {
                TileMode = TileMode.Tile,
                Viewbox = new Rect(0, 0, PATTERN_W, PATTERN_H),
                ViewboxUnits = BrushMappingMode.Absolute,
                ViewportUnits = BrushMappingMode.Absolute,
                Transform = _branchFlowTransform
            };
            BranchFluidPattern.Fill = _branchPatternBrush;

            UpdateFluidPatternViewports();

            if (IsFlowing && IsFluidVisible) StartFlowAnimation();
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

            if (_mainHorizPatternBrush != null)
                _mainHorizPatternBrush.Viewport = new Rect(0, 0, tileW, tileH);
            if (_branchPatternBrush != null)
                _branchPatternBrush.Viewport = new Rect(0, 0, tileW, tileH);
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
            if (_mainHorizFlowTransform == null) return;

            StopFlowAnimation();

            double tileW = _pipeWidth * (PATTERN_W / PATTERN_H);
            double baseDurationMs = 1000.0 / Math.Max(0.1, FlowSpeed);

            // T型管道：主管道水平流动 + 支管向上流动
            bool leftToRight = PipeFlowDir == PipeFlowDirection.LeftToRight;

            // ── 主水平段 ──
            double mainTo = leftToRight ? tileW : -tileW;
            _mainHorizFlowTransform.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation
                {
                    From = 0,
                    To = mainTo,
                    Duration = TimeSpan.FromMilliseconds(baseDurationMs),
                    RepeatBehavior = RepeatBehavior.Forever
                });

            // ── 支管垂直段（流体从主管道往上流出）──
            double branchPeriod = _pipeWidth;
            double branchDurationMs = baseDurationMs * (branchPeriod / tileW);
            // 支管默认向上流动（负值）
            double branchTo = -branchPeriod;
            _branchFlowTransform.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation
                {
                    From = 0,
                    To = branchTo,
                    Duration = TimeSpan.FromMilliseconds(branchDurationMs),
                    RepeatBehavior = RepeatBehavior.Forever
                });
        }

        private void StopFlowAnimation()
        {
            if (_mainHorizFlowTransform != null)
            {
                _mainHorizFlowTransform.BeginAnimation(TranslateTransform.XProperty, null);
                _mainHorizFlowTransform.X = 0;
            }
            if (_branchFlowTransform != null)
            {
                _branchFlowTransform.BeginAnimation(TranslateTransform.YProperty, null);
                _branchFlowTransform.Y = 0;
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
            MainLengthAnchor.Opacity = 0.6;
            BranchLengthAnchor.Opacity = 0.6;
            BranchPositionAnchor.Opacity = 0.6;
            PipeWidthAnchor.Opacity = 0.6;
            RotateAnchor.Opacity = 0.6;
            RotateAnchorLine.Opacity = 0.6;
        }

        private void HideAllAnchors()
        {
            MainLengthAnchor.Opacity = 0;
            BranchLengthAnchor.Opacity = 0;
            BranchPositionAnchor.Opacity = 0;
            PipeWidthAnchor.Opacity = 0;
            RotateAnchor.Opacity = 0;
            RotateAnchorLine.Opacity = 0;
        }

        private void UpdateSelectionVisual(bool isSelected)
        {
            if (isSelected)
            {
                SelectionBorder.Visibility = Visibility.Visible;
                ShowAllAnchors();
            }
            else
            {
                SelectionBorder.Visibility = Visibility.Collapsed;
                if (!IsMouseOver && !_isResizing && !_isRotating) HideAllAnchors();
            }
        }

        #endregion

        #region 锚点事件设置

        private void SetupAnchorEvents()
        {
            MainLengthAnchor.MouseEnter += OnAnchorMouseEnter;
            MainLengthAnchor.MouseLeave += OnAnchorMouseLeave;
            MainLengthAnchor.MouseDown += (s, e) => StartResize(TAnchorPosition.MainLength, e);
            MainLengthAnchor.MouseMove += OnAnchorMouseMove;
            MainLengthAnchor.MouseUp += OnAnchorMouseUp;

            BranchLengthAnchor.MouseEnter += OnAnchorMouseEnter;
            BranchLengthAnchor.MouseLeave += OnAnchorMouseLeave;
            BranchLengthAnchor.MouseDown += (s, e) => StartResize(TAnchorPosition.BranchLength, e);
            BranchLengthAnchor.MouseMove += OnAnchorMouseMove;
            BranchLengthAnchor.MouseUp += OnAnchorMouseUp;

            BranchPositionAnchor.MouseEnter += OnAnchorMouseEnter;
            BranchPositionAnchor.MouseLeave += OnAnchorMouseLeave;
            BranchPositionAnchor.MouseDown += (s, e) => StartResize(TAnchorPosition.BranchPosition, e);
            BranchPositionAnchor.MouseMove += OnAnchorMouseMove;
            BranchPositionAnchor.MouseUp += OnAnchorMouseUp;

            PipeWidthAnchor.MouseEnter += OnAnchorMouseEnter;
            PipeWidthAnchor.MouseLeave += OnAnchorMouseLeave;
            PipeWidthAnchor.MouseDown += (s, e) => StartResize(TAnchorPosition.PipeWidth, e);
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
                    (clicked.Name == "MainLengthAnchor" || clicked.Name == "BranchLengthAnchor" ||
                     clicked.Name == "BranchPositionAnchor" || clicked.Name == "PipeWidthAnchor" ||
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

                CacheMode = null;
                RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.LowQuality);

                CaptureMouse();
                Opacity = 0.7;
                e.Handled = true;

                PipeDragStarted?.Invoke(this, new TPipeDragEventArgs
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

                PipeDragging?.Invoke(this, new TPipeDragEventArgs
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

                PipeDragCompleted?.Invoke(this, new TPipeDragEventArgs
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

        private void StartResize(TAnchorPosition anchor, MouseButtonEventArgs e)
        {
            _isResizing = true;
            _resizingAnchor = anchor;
            _dragStartPoint = e.GetPosition(Parent as UIElement);
            _originalSize = new TPipeSize
            {
                MainPipeLength = MainPipeLength,
                BranchPipeLength = BranchPipeLength,
                BranchPosition = BranchPosition,
                PipeWidth = PipeWidth
            };

            CacheMode = null;
            RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.LowQuality);

            (e.OriginalSource as UIElement)?.CaptureMouse();
            e.Handled = true;

            PipeResizeStarted?.Invoke(this, new TPipeResizeEventArgs
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
                    case TAnchorPosition.MainLength:
                        MainPipeLength = _originalSize.MainPipeLength + deltaX;
                        break;
                    case TAnchorPosition.BranchLength:
                        BranchPipeLength = _originalSize.BranchPipeLength - deltaY;
                        break;
                    case TAnchorPosition.BranchPosition:
                        BranchPosition = _originalSize.BranchPosition + deltaX;
                        break;
                    case TAnchorPosition.PipeWidth:
                        PipeWidth = _originalSize.PipeWidth + deltaY;
                        break;
                }

                PipeResizing?.Invoke(this, new TPipeResizeEventArgs
                {
                    PipeId = PipeId,
                    AnchorPosition = _resizingAnchor.Value,
                    OriginalSize = _originalSize,
                    CurrentSize = new TPipeSize
                    {
                        MainPipeLength = MainPipeLength,
                        BranchPipeLength = BranchPipeLength,
                        BranchPosition = BranchPosition,
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
                    PipeResizeCompleted?.Invoke(this, new TPipeResizeEventArgs
                    {
                        PipeId = PipeId,
                        AnchorPosition = _resizingAnchor.Value,
                        OriginalSize = _originalSize,
                        CurrentSize = new TPipeSize
                        {
                            MainPipeLength = MainPipeLength,
                            BranchPipeLength = BranchPipeLength,
                            BranchPosition = BranchPosition,
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

                PipeRotateStarted?.Invoke(this, new TPipeRotateEventArgs
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

                PipeRotating?.Invoke(this, new TPipeRotateEventArgs
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
                PipeRotateCompleted?.Invoke(this, new TPipeRotateEventArgs
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

                // ═══ 计算法兰尺寸 ═══
                double flangeHeight = _pipeWidth + FLANGE_EXTENSION;

                // ═══ 主水平管道 ═══
                double mainPipeLeft = FlangeOffset + FlangeWidthConst;
                double mainPipeTop = MAIN_PIPE_CENTER_Y - _pipeWidth / 2;

                SetRectGeometry(MainHorizontalPipeBackground, mainPipeLeft, mainPipeTop, _mainPipeLength, _pipeWidth);
                SetRectGeometry(MainHorizontalFluidBackground, mainPipeLeft, mainPipeTop, _mainPipeLength, _pipeWidth);
                SetRectGeometry(MainHorizontalFluidPattern, mainPipeLeft, mainPipeTop, _mainPipeLength, _pipeWidth);
                SetRectGeometry(MainHorizontalPipe, mainPipeLeft, mainPipeTop, _mainPipeLength, _pipeWidth);

                MainHorizontalTopEdge.X1 = mainPipeLeft;
                MainHorizontalTopEdge.Y1 = mainPipeTop + 0.32;
                MainHorizontalTopEdge.X2 = mainPipeLeft + _mainPipeLength;
                MainHorizontalTopEdge.Y2 = mainPipeTop + 0.32;

                MainHorizontalBottomEdge.X1 = mainPipeLeft;
                MainHorizontalBottomEdge.Y1 = mainPipeTop + _pipeWidth - 0.32;
                MainHorizontalBottomEdge.X2 = mainPipeLeft + _mainPipeLength;
                MainHorizontalBottomEdge.Y2 = mainPipeTop + _pipeWidth - 0.32;

                // ═══ 左侧法兰 ═══
                double sideFlangeTop = MAIN_PIPE_CENTER_Y - flangeHeight / 2;

                Canvas.SetLeft(LeftFlangeGroup, FlangeOffset);
                Canvas.SetTop(LeftFlangeGroup, sideFlangeTop);
                LeftFlangeGroup.Width = FlangeWidthConst;
                LeftFlangeGroup.Height = flangeHeight;
                LeftFlangeBase.Width = FlangeWidthConst;
                LeftFlangeBase.Height = flangeHeight;
                LeftFlangeGradient.Width = FlangeWidthConst;
                LeftFlangeGradient.Height = flangeHeight;
                LeftFlangeBorder.Width = FlangeWidthConst;
                LeftFlangeBorder.Height = flangeHeight;

                // ═══ 右侧法兰 ═══
                double rightFlangeX = mainPipeLeft + _mainPipeLength;

                Canvas.SetLeft(RightFlangeGroup, rightFlangeX);
                Canvas.SetTop(RightFlangeGroup, sideFlangeTop);
                RightFlangeGroup.Width = FlangeWidthConst;
                RightFlangeGroup.Height = flangeHeight;
                RightFlangeBase.Width = FlangeWidthConst;
                RightFlangeBase.Height = flangeHeight;
                RightFlangeGradient.Width = FlangeWidthConst;
                RightFlangeGradient.Height = flangeHeight;
                RightFlangeBorder.Width = FlangeWidthConst;
                RightFlangeBorder.Height = flangeHeight;

                // ═══ 支管垂直管道 ═══
                double branchCenterX = mainPipeLeft + _branchPosition;
                double branchPipeLeft = branchCenterX - _pipeWidth / 2;
                double branchPipeBottom = mainPipeTop;
                double branchPipeTop = branchPipeBottom - _branchPipeLength;

                SetRectGeometry(BranchPipeBackground, branchPipeLeft, branchPipeTop, _pipeWidth, _branchPipeLength);
                SetRectGeometry(BranchFluidBackground, branchPipeLeft, branchPipeTop, _pipeWidth, _branchPipeLength);
                SetRectGeometry(BranchFluidPattern, branchPipeLeft, branchPipeTop, _pipeWidth, _branchPipeLength);
                SetRectGeometry(BranchVerticalPipe, branchPipeLeft, branchPipeTop, _pipeWidth, _branchPipeLength);

                BranchLeftEdge.X1 = branchPipeLeft + 0.32;
                BranchLeftEdge.Y1 = branchPipeTop;
                BranchLeftEdge.X2 = branchPipeLeft + 0.32;
                BranchLeftEdge.Y2 = branchPipeBottom;

                BranchRightEdge.X1 = branchPipeLeft + _pipeWidth - 0.32;
                BranchRightEdge.Y1 = branchPipeTop;
                BranchRightEdge.X2 = branchPipeLeft + _pipeWidth - 0.32;
                BranchRightEdge.Y2 = branchPipeBottom;

                // ═══ 顶部法兰 ═══
                double topFlangeWidth = _pipeWidth + FLANGE_EXTENSION;
                double topFlangeLeft = branchCenterX - topFlangeWidth / 2;
                double topFlangeTop = branchPipeTop - FlangeWidthConst;

                Canvas.SetLeft(TopFlangeGroup, topFlangeLeft);
                Canvas.SetTop(TopFlangeGroup, topFlangeTop);
                TopFlangeGroup.Width = topFlangeWidth;
                TopFlangeGroup.Height = FlangeWidthConst;
                TopFlangeBase.Width = topFlangeWidth;
                TopFlangeBase.Height = FlangeWidthConst;
                TopFlangeGradient.Width = topFlangeWidth;
                TopFlangeGradient.Height = FlangeWidthConst;
                TopFlangeBorder.Width = topFlangeWidth;
                TopFlangeBorder.Height = FlangeWidthConst;

                // ═══ T型连接处 ═══
                double junctionSize = _pipeWidth - 2;
                Canvas.SetLeft(TJunction, branchCenterX - junctionSize / 2);
                Canvas.SetTop(TJunction, MAIN_PIPE_CENTER_Y - junctionSize / 2);
                TJunction.Width = junctionSize;
                TJunction.Height = junctionSize;

                // ═══ 控件尺寸 & 选中框 ═══
                double totalWidth = rightFlangeX + FlangeWidthConst + 4;
                double totalHeight = (mainPipeTop + _pipeWidth) + (FLANGE_EXTENSION / 2) + 20;
                Width = Math.Max(totalWidth, 100);
                Height = Math.Max(totalHeight, 100);

                SelectionBorder.Width = Width;
                SelectionBorder.Height = Height;

                // ═══ 锚点 ═══
                UpdateAnchorPositions(rightFlangeX, mainPipeTop, branchPipeLeft, branchPipeTop, branchCenterX, MAIN_PIPE_CENTER_Y);

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

        private void SetRectGeometry(Rectangle rect, double left, double top, double width, double height)
        {
            Canvas.SetLeft(rect, left);
            Canvas.SetTop(rect, top);
            rect.Width = width;
            rect.Height = height;
        }

        private void UpdateAnchorPositions(
            double rightFlangeX, double mainPipeTop,
            double branchPipeLeft, double branchPipeTop,
            double branchCenterX, double mainPipeCenterY)
        {
            Canvas.SetLeft(MainLengthAnchor, rightFlangeX + 8);
            Canvas.SetTop(MainLengthAnchor, mainPipeCenterY - 6);

            Canvas.SetLeft(BranchLengthAnchor, branchCenterX - 6);
            Canvas.SetTop(BranchLengthAnchor, branchPipeTop - 12);

            Canvas.SetLeft(BranchPositionAnchor, branchCenterX - 26);
            Canvas.SetTop(BranchPositionAnchor, mainPipeCenterY - 6);

            Canvas.SetLeft(PipeWidthAnchor, branchCenterX - 6);
            Canvas.SetTop(PipeWidthAnchor, mainPipeTop + _pipeWidth + 8);

            Canvas.SetLeft(RotateAnchor, Width / 2 - 6);
            Canvas.SetTop(RotateAnchor, -40);

            RotateAnchorLine.X1 = Width / 2;
            RotateAnchorLine.Y1 = 0;
            RotateAnchorLine.X2 = Width / 2;
            RotateAnchorLine.Y2 = -34;

            Canvas.SetLeft(RotationLabel, Width / 2 - 20);
            Canvas.SetTop(RotationLabel, -60);
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

        public TPipeSize GetSize()
        {
            return new TPipeSize
            {
                MainPipeLength = MainPipeLength,
                BranchPipeLength = BranchPipeLength,
                BranchPosition = BranchPosition,
                PipeWidth = PipeWidth
            };
        }

        public void SetSize(TPipeSize size)
        {
            MainPipeLength = size.MainPipeLength;
            BranchPipeLength = size.BranchPipeLength;
            BranchPosition = size.BranchPosition;
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

            Point center = new Point(pipeLeft + Width / 2, pipeTop + Height / 2);
            double radians = _rotationAngle * Math.PI / 180.0;

            // ═══ 左侧吸附点 ═══
            double leftSnapLocalX = FlangeOffset;
            double leftSnapLocalY = MAIN_PIPE_CENTER_Y;
            Point leftRel = new Point(leftSnapLocalX - Width / 2, leftSnapLocalY - Height / 2);
            Point leftWorld = RotatePoint(leftRel, radians);
            snapPoints.Add(new PipeSnapPoint
            {
                WorldPosition = new Point(center.X + leftWorld.X, center.Y + leftWorld.Y),
                Direction = GetSnapDirectionFromAngle(radians + Math.PI),
                Description = "T型左端口"
            });

            // ═══ 右侧吸附点 ═══
            double rightSnapLocalX = FlangeOffset + FlangeWidthConst + _mainPipeLength + FlangeWidthConst;
            double rightSnapLocalY = MAIN_PIPE_CENTER_Y;
            Point rightRel = new Point(rightSnapLocalX - Width / 2, rightSnapLocalY - Height / 2);
            Point rightWorld = RotatePoint(rightRel, radians);
            snapPoints.Add(new PipeSnapPoint
            {
                WorldPosition = new Point(center.X + rightWorld.X, center.Y + rightWorld.Y),
                Direction = GetSnapDirectionFromAngle(radians),
                Description = "T型右端口"
            });

            // ═══ 顶部吸附点（支管）═══
            double branchCenterX = FlangeOffset + FlangeWidthConst + _branchPosition;
            double mainPipeTopY = MAIN_PIPE_CENTER_Y - _pipeWidth / 2;
            double branchTopY = mainPipeTopY - _branchPipeLength;
            double topSnapLocalY = branchTopY - FlangeWidthConst;
            Point topRel = new Point(branchCenterX - Width / 2, topSnapLocalY - Height / 2);
            Point topWorld = RotatePoint(topRel, radians);
            snapPoints.Add(new PipeSnapPoint
            {
                WorldPosition = new Point(center.X + topWorld.X, center.Y + topWorld.Y),
                Direction = GetSnapDirectionFromAngle(radians - Math.PI / 2),
                Description = "T型支管端口"
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

    public enum TAnchorPosition
    {
        MainLength,
        BranchLength,
        BranchPosition,
        PipeWidth
    }

    public class TPipeSize
    {
        public double MainPipeLength { get; set; }
        public double BranchPipeLength { get; set; }
        public double BranchPosition { get; set; }
        public double PipeWidth { get; set; }
    }

    public class TPipeDragEventArgs : EventArgs
    {
        public string PipeId { get; set; }
        public Point StartPosition { get; set; }
        public Point CurrentPosition { get; set; }
        public double DeltaX { get; set; }
        public double DeltaY { get; set; }
    }

    public class TPipeResizeEventArgs : EventArgs
    {
        public string PipeId { get; set; }
        public TAnchorPosition AnchorPosition { get; set; }
        public TPipeSize OriginalSize { get; set; }
        public TPipeSize CurrentSize { get; set; }
    }

    public class TPipeRotateEventArgs : EventArgs
    {
        public string PipeId { get; set; }
        public double OriginalAngle { get; set; }
        public double CurrentAngle { get; set; }
    }

    #endregion
}
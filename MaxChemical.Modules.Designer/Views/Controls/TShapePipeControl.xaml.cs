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
        public event EventHandler<string> PipeDoubleClicked;

        #endregion

        #region 依赖属性

        public static readonly DependencyProperty IsSelectedProperty =
            DependencyProperty.Register(nameof(IsSelected), typeof(bool), typeof(TShapePipeControl),
                new PropertyMetadata(false, OnIsSelectedChanged));
        public bool IsSelected { get => (bool)GetValue(IsSelectedProperty); set => SetValue(IsSelectedProperty, value); }
        private static void OnIsSelectedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        { if (d is TShapePipeControl c) { c.UpdateSelectionVisual((bool)e.NewValue); c.OnPropertyChanged(nameof(IsSelected)); } }

        public static readonly DependencyProperty PipeColorProperty =
            DependencyProperty.Register(nameof(PipeColor), typeof(Color), typeof(TShapePipeControl),
                new PropertyMetadata(Color.FromRgb(0x72, 0x71, 0x71), OnPipeColorChanged));
        public Color PipeColor { get => (Color)GetValue(PipeColorProperty); set => SetValue(PipeColorProperty, value); }
        private static void OnPipeColorChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        { if (d is TShapePipeControl c) c.ApplyPipeColor(); }

        public static readonly DependencyProperty FlangeColorProperty =
            DependencyProperty.Register(nameof(FlangeColor), typeof(Color), typeof(TShapePipeControl),
                new PropertyMetadata(Color.FromRgb(0xD9, 0xD9, 0xD9), OnFlangeColorChanged));
        public Color FlangeColor { get => (Color)GetValue(FlangeColorProperty); set => SetValue(FlangeColorProperty, value); }
        private static void OnFlangeColorChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        { if (d is TShapePipeControl c) c.ApplyFlangeColor(); }

        public static readonly DependencyProperty FluidColorProperty =
            DependencyProperty.Register(nameof(FluidColor), typeof(Color), typeof(TShapePipeControl),
                new PropertyMetadata(Color.FromRgb(0x1E, 0xC1, 0xF4), OnFluidColorChanged));
        public Color FluidColor { get => (Color)GetValue(FluidColorProperty); set => SetValue(FluidColorProperty, value); }
        private static void OnFluidColorChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        { if (d is TShapePipeControl c) c.ApplyFluidColor(); }

        public static readonly DependencyProperty FluidOpacityProperty =
            DependencyProperty.Register(nameof(FluidOpacity), typeof(double), typeof(TShapePipeControl),
                new PropertyMetadata(0.5, OnFluidOpacityChanged));
        public double FluidOpacity { get => (double)GetValue(FluidOpacityProperty); set => SetValue(FluidOpacityProperty, value); }
        private static void OnFluidOpacityChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        { if (d is TShapePipeControl c) c.ApplyFluidOpacity(); }

        public static readonly DependencyProperty IsFluidVisibleProperty =
            DependencyProperty.Register(nameof(IsFluidVisible), typeof(bool), typeof(TShapePipeControl),
                new PropertyMetadata(false, OnIsFluidVisibleChanged));
        public bool IsFluidVisible { get => (bool)GetValue(IsFluidVisibleProperty); set => SetValue(IsFluidVisibleProperty, value); }
        private static void OnIsFluidVisibleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        { if (d is TShapePipeControl c) c.ApplyFluidVisibility(); }

        public static readonly DependencyProperty UseDefaultStyleProperty =
            DependencyProperty.Register(nameof(UseDefaultStyle), typeof(bool), typeof(TShapePipeControl),
                new PropertyMetadata(false, OnUseDefaultStyleChanged));
        public bool UseDefaultStyle { get => (bool)GetValue(UseDefaultStyleProperty); set => SetValue(UseDefaultStyleProperty, value); }
        private static void OnUseDefaultStyleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        { if (d is TShapePipeControl c) c.ApplyDefaultStyleSwitch(); }

        public static readonly DependencyProperty IsFlowingProperty =
            DependencyProperty.Register(nameof(IsFlowing), typeof(bool), typeof(TShapePipeControl),
                new PropertyMetadata(false, OnIsFlowingChanged));
        public bool IsFlowing { get => (bool)GetValue(IsFlowingProperty); set => SetValue(IsFlowingProperty, value); }
        private static void OnIsFlowingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        { if (d is TShapePipeControl c) c.ApplyFlowAnimation(); }

        public static readonly DependencyProperty PipeFlowDirProperty =
            DependencyProperty.Register(nameof(PipeFlowDir), typeof(PipeFlowDirection), typeof(TShapePipeControl),
                new PropertyMetadata(PipeFlowDirection.LeftToRight, OnPipeFlowDirChanged));
        public PipeFlowDirection PipeFlowDir { get => (PipeFlowDirection)GetValue(PipeFlowDirProperty); set => SetValue(PipeFlowDirProperty, value); }
        private static void OnPipeFlowDirChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        { if (d is TShapePipeControl c) c.ApplyFlowAnimation(); }

        public static readonly DependencyProperty FlowSpeedProperty =
            DependencyProperty.Register(nameof(FlowSpeed), typeof(double), typeof(TShapePipeControl),
                new PropertyMetadata(1.0, OnFlowSpeedChanged));
        public double FlowSpeed { get => (double)GetValue(FlowSpeedProperty); set => SetValue(FlowSpeedProperty, value); }
        private static void OnFlowSpeedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        { if (d is TShapePipeControl c) c.ApplyFlowAnimation(); }

        #endregion

        #region 私有字段

        private bool _isDraggingPipe;
        private bool _isResizing;
        private bool _isRotating;
        private TAnchorPosition? _resizingAnchor;
        private Point _dragStartPoint;
        private Point _originalPosition;
        private TPipeSize _originalSize;
        private double _originalRotation;
        private Point _fixedWorldPoint;

        private const double MinMainLength = 20;
        private const double MaxMainLength = 10000;
        private const double MinBranchLength = 10;
        private const double MaxBranchLength = 10000;
        private const double MinPipeWidth = 4;
        private const double MaxPipeWidth = 10000;
        private const double FlangeWidthConst = 2.54;
        private const double FlangeOffset = 0.35;
        private const double FlangeSpacing = 2.0;
        private const double FLANGE_EXTENSION = 5.8;
        private const double MAIN_PIPE_CENTER_Y = 70;

        private double _mainPipeLength = 120;
        private double _branchPipeLength = 54;
        private double _branchPosition = 60;
        private double _pipeWidth = 20;
        private double _rotationAngle;

        private RotateTransform _rotateTransform;
        private TransformGroup _transformGroup;
        private DispatcherTimer _rotationLabelTimer;

        private TranslateTransform _mainHorizFlowTransform;
        private TranslateTransform _branchFlowTransform;
        private DrawingBrush _mainHorizPatternBrush;
        private DrawingBrush _branchPatternBrush;

        // 赛博流光
        private DoubleAnimation _cyberMainMainAnim;
        private DoubleAnimation _cyberMainParticleAnim;
        private DoubleAnimation _cyberMainSparkAnim;
        private DoubleAnimation _cyberBranchMainAnim;
        private DoubleAnimation _cyberBranchParticleAnim;
        private DoubleAnimation _cyberBranchSparkAnim;
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

        public double MainPipeLength
        {
            get => _mainPipeLength;
            set
            {
                var nv = Math.Max(MinMainLength, Math.Min(MaxMainLength, value));
                if (Math.Abs(_mainPipeLength - nv) > 0.01)
                {
                    _mainPipeLength = nv;
                    double minBranchPos = _pipeWidth;
                    double maxBranchPos = Math.Max(minBranchPos, _mainPipeLength - _pipeWidth);
                    if (_branchPosition > maxBranchPos)
                        _branchPosition = (minBranchPos + maxBranchPos) / 2;
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
                { _branchPipeLength = nv; UpdatePipeGeometry(); OnPropertyChanged(nameof(BranchPipeLength)); }
            }
        }

        public double BranchPosition
        {
            get => _branchPosition;
            set
            {
                var minPos = _pipeWidth;
                var maxPos = Math.Max(minPos, _mainPipeLength - _pipeWidth);
                var nv = Math.Max(minPos, Math.Min(maxPos, value));
                if (Math.Abs(_branchPosition - nv) > 0.01)
                { _branchPosition = nv; UpdatePipeGeometry(); OnPropertyChanged(nameof(BranchPosition)); }
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
                UpdatePipeGeometry();
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
            if (MainHorizontalPipe == null) return;
            var c = PipeColor;

            var hGrad = MakeCylinderGradient(c, new Point(0, 0), new Point(0, 1));
            MainHorizontalPipe.Fill = hGrad;

            var vGrad = MakeCylinderGradient(c, new Point(0, 0), new Point(1, 0));
            BranchVerticalPipe.Fill = vGrad;

            var junctionBrush = new SolidColorBrush(DarkenColor(c, 15));
            junctionBrush.Freeze();
            TJunction.Fill = junctionBrush;

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

            var topGrad = new LinearGradientBrush { StartPoint = new Point(0.5, 0), EndPoint = new Point(0.5, 1) };
            topGrad.GradientStops.Add(new GradientStop(c, 0));
            topGrad.GradientStops.Add(new GradientStop(midColor, 0.5));
            topGrad.GradientStops.Add(new GradientStop(c, 1));
            topGrad.Freeze();
            TopFlangeGradient.Fill = topGrad;
        }

        #endregion

        #region 流体 — 颜色与可见性

        private void ApplyFluidColor()
        {
            if (MainHorizontalFluidBackground == null) return;
            var brush = new SolidColorBrush(FluidColor);
            MainHorizontalFluidBackground.Fill = brush;
            BranchFluidBackground.Fill = brush;
            ApplyFluidOpacity();

            if (UseDefaultStyle && CyberMainMainStream != null)
                ApplyCyberStreamColors();
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

            if (UseDefaultStyle)
            {
                MainHorizontalFluidBackground.Visibility = Visibility.Collapsed;
                MainHorizontalFluidPattern.Visibility = Visibility.Collapsed;
                BranchFluidBackground.Visibility = Visibility.Collapsed;
                BranchFluidPattern.Visibility = Visibility.Collapsed;
                return;
            }

            var vis = IsFluidVisible ? Visibility.Visible : Visibility.Collapsed;
            MainHorizontalFluidBackground.Visibility = vis;
            MainHorizontalFluidPattern.Visibility = vis;
            BranchFluidBackground.Visibility = vis;
            BranchFluidPattern.Visibility = vis;

            if (IsFluidVisible && IsFlowing) StartFlowAnimation(); else StopFlowAnimation();
        }

        private void RebuildFluidPatternBrushes()
        {
            if (MainHorizontalFluidPattern == null) return;
            var pipeC = PipeColor;

            var hDrawing = CreateBubbleDrawing(pipeC, false);
            _mainHorizFlowTransform = new TranslateTransform(0, 0);
            _mainHorizPatternBrush = new DrawingBrush(hDrawing) { TileMode = TileMode.Tile, Viewbox = new Rect(0, 0, PATTERN_W, PATTERN_H), ViewboxUnits = BrushMappingMode.Absolute, ViewportUnits = BrushMappingMode.Absolute, Transform = _mainHorizFlowTransform };
            MainHorizontalFluidPattern.Fill = _mainHorizPatternBrush;

            var vDrawing = CreateBubbleDrawing(pipeC, true);
            _branchFlowTransform = new TranslateTransform(0, 0);
            _branchPatternBrush = new DrawingBrush(vDrawing) { TileMode = TileMode.Tile, Viewbox = new Rect(0, 0, PATTERN_W, PATTERN_H), ViewboxUnits = BrushMappingMode.Absolute, ViewportUnits = BrushMappingMode.Absolute, Transform = _branchFlowTransform };
            BranchFluidPattern.Fill = _branchPatternBrush;

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
            if (_mainHorizPatternBrush != null) _mainHorizPatternBrush.Viewport = new Rect(0, 0, tileW, tileH);
            if (_branchPatternBrush != null) _branchPatternBrush.Viewport = new Rect(0, 0, tileW, tileH);
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
            if (_mainHorizFlowTransform == null) return;
            StopFlowAnimation();
            // ★ 关键修复:滚动距离用 PATTERN_W (=172) 而不是 tileW (≈48)
            //   跟原版 ThingsBoard 一致 — 流动感大幅增强
            // ★ 速度调慢:基础时长 2000ms 而不是 1000ms
            double baseDurationMs = 2000.0 / Math.Max(0.1, FlowSpeed);
            bool leftToRight = PipeFlowDir == PipeFlowDirection.LeftToRight;

            double mainTo = leftToRight ? PATTERN_W : -PATTERN_W;
            _mainHorizFlowTransform.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation { From = 0, To = mainTo, Duration = TimeSpan.FromMilliseconds(baseDurationMs), RepeatBehavior = RepeatBehavior.Forever });

            // 支管永远向上流出
            double branchTo = -PATTERN_W;
            _branchFlowTransform.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation { From = 0, To = branchTo, Duration = TimeSpan.FromMilliseconds(baseDurationMs), RepeatBehavior = RepeatBehavior.Forever });
        }

        private void StopFlowAnimation()
        {
            if (_mainHorizFlowTransform != null) { _mainHorizFlowTransform.BeginAnimation(TranslateTransform.XProperty, null); _mainHorizFlowTransform.X = 0; }
            if (_branchFlowTransform != null) { _branchFlowTransform.BeginAnimation(TranslateTransform.YProperty, null); _branchFlowTransform.Y = 0; }
        }

        #endregion

        #region 赛博流光

        private void ApplyDefaultStyleSwitch()
        {
            if (CyberMainMainStream == null) return;

            if (UseDefaultStyle)
            {
                if (MainHorizontalPipe != null) MainHorizontalPipe.Visibility = Visibility.Collapsed;
                if (BranchVerticalPipe != null) BranchVerticalPipe.Visibility = Visibility.Collapsed;
                if (TJunction != null) TJunction.Visibility = Visibility.Collapsed;
                // ★ Background 层不隐藏,改成透明 fill —— 保留命中(双击/拖拽)
                if (MainHorizontalPipeBackground != null) { MainHorizontalPipeBackground.Fill = Brushes.Transparent; MainHorizontalPipeBackground.Visibility = Visibility.Visible; }
                if (BranchPipeBackground != null) { BranchPipeBackground.Fill = Brushes.Transparent; BranchPipeBackground.Visibility = Visibility.Visible; }

                BuildCyberLayers();

                CyberMainMetal.Visibility = Visibility.Visible;
                CyberBranchMetal.Visibility = Visibility.Visible;
                CyberMainDark.Visibility = Visibility.Visible;
                CyberBranchDark.Visibility = Visibility.Visible;
                CyberMainMainStream.Visibility = Visibility.Visible;
                CyberMainParticleStream.Visibility = Visibility.Visible;
                CyberMainSparkStream.Visibility = Visibility.Visible;
                CyberBranchMainStream.Visibility = Visibility.Visible;
                CyberBranchParticleStream.Visibility = Visibility.Visible;
                CyberBranchSparkStream.Visibility = Visibility.Visible;

                if (IsFlowing) StartCyberAnimation(); else StopCyberAnimation();
                StopFlowAnimation();
                ApplyFluidVisibility();
            }
            else
            {
                CyberMainMetal.Visibility = Visibility.Collapsed;
                CyberBranchMetal.Visibility = Visibility.Collapsed;
                CyberMainDark.Visibility = Visibility.Collapsed;
                CyberBranchDark.Visibility = Visibility.Collapsed;
                CyberMainMainStream.Visibility = Visibility.Collapsed;
                CyberMainParticleStream.Visibility = Visibility.Collapsed;
                CyberMainSparkStream.Visibility = Visibility.Collapsed;
                CyberBranchMainStream.Visibility = Visibility.Collapsed;
                CyberBranchParticleStream.Visibility = Visibility.Collapsed;
                CyberBranchSparkStream.Visibility = Visibility.Collapsed;
                StopCyberAnimation();

                if (MainHorizontalPipe != null) MainHorizontalPipe.Visibility = Visibility.Visible;
                if (BranchVerticalPipe != null) BranchVerticalPipe.Visibility = Visibility.Visible;
                if (TJunction != null) TJunction.Visibility = Visibility.Visible;
                // ★ 还原 Background 白底
                if (MainHorizontalPipeBackground != null) { MainHorizontalPipeBackground.Fill = Brushes.White; MainHorizontalPipeBackground.Visibility = Visibility.Visible; }
                if (BranchPipeBackground != null) { BranchPipeBackground.Fill = Brushes.White; BranchPipeBackground.Visibility = Visibility.Visible; }
                ApplyFluidVisibility();
            }
        }

        private void BuildCyberLayers()
        {
            if (CyberMainMetal == null) return;

            CyberMainMetal.Fill = MakeMetalGradient(false);
            CyberBranchMetal.Fill = MakeMetalGradient(true);

            ApplyCyberStreamColors();

            CyberMainMainStream.StrokeDashArray = new DoubleCollection { 30, 90 };
            CyberMainParticleStream.StrokeDashArray = new DoubleCollection { 8, 50 };
            CyberMainSparkStream.StrokeDashArray = new DoubleCollection { 4, 80 };
            CyberBranchMainStream.StrokeDashArray = new DoubleCollection { 30, 90 };
            CyberBranchParticleStream.StrokeDashArray = new DoubleCollection { 8, 50 };
            CyberBranchSparkStream.StrokeDashArray = new DoubleCollection { 4, 80 };

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
            if (CyberMainMainStream == null) return;
            var c = FluidColor;
            var brightC = LightenColor(c, 30);
            var sparkC = LightenColor(c, 60);

            var mainBrush = new SolidColorBrush(brightC);
            mainBrush.Freeze();
            CyberMainMainStream.Stroke = mainBrush;
            CyberBranchMainStream.Stroke = mainBrush;

            var particleBrush = new SolidColorBrush(sparkC);
            particleBrush.Freeze();
            CyberMainParticleStream.Stroke = particleBrush;
            CyberBranchParticleStream.Stroke = particleBrush;
        }

        /// <summary>
        /// T 型几何 + 两条 path：
        ///   - 主管 path：横穿主管中线（左→右）
        ///   - 支管 path：从主管中线垂直向上到支管顶
        /// </summary>
        private void UpdateCyberGeometry()
        {
            if (CyberMainMetal == null) return;

            double mainPipeLeft = FlangeOffset + FlangeWidthConst;
            double mainPipeTop = MAIN_PIPE_CENTER_Y - _pipeWidth / 2;
            double branchCenterX = mainPipeLeft + _branchPosition;
            double branchPipeLeft = branchCenterX - _pipeWidth / 2;
            double branchPipeTop = mainPipeTop - _branchPipeLength;

            // 主管金属/暗腔
            CyberMainMetal.Width = _mainPipeLength; CyberMainMetal.Height = _pipeWidth;
            Canvas.SetLeft(CyberMainMetal, mainPipeLeft); Canvas.SetTop(CyberMainMetal, mainPipeTop);
            CyberMainDark.Width = _mainPipeLength; CyberMainDark.Height = _pipeWidth;
            Canvas.SetLeft(CyberMainDark, mainPipeLeft); Canvas.SetTop(CyberMainDark, mainPipeTop);

            // 支管金属/暗腔
            CyberBranchMetal.Width = _pipeWidth; CyberBranchMetal.Height = _branchPipeLength;
            Canvas.SetLeft(CyberBranchMetal, branchPipeLeft); Canvas.SetTop(CyberBranchMetal, branchPipeTop);
            CyberBranchDark.Width = _pipeWidth; CyberBranchDark.Height = _branchPipeLength;
            Canvas.SetLeft(CyberBranchDark, branchPipeLeft); Canvas.SetTop(CyberBranchDark, branchPipeTop);

            // ★ 主管中心线 path（横穿主管中线）
            var mainFig = new PathFigure { StartPoint = new Point(mainPipeLeft, MAIN_PIPE_CENTER_Y) };
            mainFig.Segments.Add(new LineSegment(new Point(mainPipeLeft + _mainPipeLength, MAIN_PIPE_CENTER_Y), true));
            var mainGeo = new PathGeometry();
            mainGeo.Figures.Add(mainFig);
            mainGeo.Freeze();
            CyberMainMainStream.Data = mainGeo;
            CyberMainParticleStream.Data = mainGeo;
            CyberMainSparkStream.Data = mainGeo;

            // ★ 支管中心线 path（从主管中线垂直向上到支管顶）
            var branchFig = new PathFigure { StartPoint = new Point(branchCenterX, MAIN_PIPE_CENTER_Y) };
            branchFig.Segments.Add(new LineSegment(new Point(branchCenterX, branchPipeTop), true));
            var branchGeo = new PathGeometry();
            branchGeo.Figures.Add(branchFig);
            branchGeo.Freeze();
            CyberBranchMainStream.Data = branchGeo;
            CyberBranchParticleStream.Data = branchGeo;
            CyberBranchSparkStream.Data = branchGeo;

            double mainThick = Math.Max(2, _pipeWidth * 0.6);
            double partThick = Math.Max(1.5, _pipeWidth * 0.3);
            double sparkThick = Math.Max(1, _pipeWidth * 0.15);

            CyberMainMainStream.StrokeThickness = mainThick;
            CyberMainParticleStream.StrokeThickness = partThick;
            CyberMainSparkStream.StrokeThickness = sparkThick;
            CyberBranchMainStream.StrokeThickness = mainThick;
            CyberBranchParticleStream.StrokeThickness = partThick;
            CyberBranchSparkStream.StrokeThickness = sparkThick;
        }

        private void StartCyberAnimation()
        {
            if (CyberMainMainStream == null) return;
            StopCyberAnimation();
            int sign = PipeFlowDir == PipeFlowDirection.LeftToRight ? -1 : 1;
            double speed = Math.Max(0.1, FlowSpeed);

            // 主管：跟流向
            _cyberMainMainAnim = new DoubleAnimation { From = 0, To = sign * _cyberMainPeriod, Duration = TimeSpan.FromMilliseconds(2000.0 / speed), RepeatBehavior = RepeatBehavior.Forever };
            CyberMainMainStream.BeginAnimation(Path.StrokeDashOffsetProperty, _cyberMainMainAnim);

            _cyberMainParticleAnim = new DoubleAnimation { From = 0, To = sign * _cyberParticlePeriod, Duration = TimeSpan.FromMilliseconds(1500.0 / speed), RepeatBehavior = RepeatBehavior.Forever };
            CyberMainParticleStream.BeginAnimation(Path.StrokeDashOffsetProperty, _cyberMainParticleAnim);

            _cyberMainSparkAnim = new DoubleAnimation { From = sign * -30, To = sign * (_cyberSparkPeriod - 30), Duration = TimeSpan.FromMilliseconds(1200.0 / speed), RepeatBehavior = RepeatBehavior.Forever };
            CyberMainSparkStream.BeginAnimation(Path.StrokeDashOffsetProperty, _cyberMainSparkAnim);

            // 支管：永远向上流出（path 起点在主管中线，终点在支管顶）
            // path 方向是 main→top 即从下往上，所以 dashOffset 减小 = 流光向起点(下)滚动 = 视觉向上
            _cyberBranchMainAnim = new DoubleAnimation { From = 0, To = -_cyberMainPeriod, Duration = TimeSpan.FromMilliseconds(2000.0 / speed), RepeatBehavior = RepeatBehavior.Forever };
            CyberBranchMainStream.BeginAnimation(Path.StrokeDashOffsetProperty, _cyberBranchMainAnim);

            _cyberBranchParticleAnim = new DoubleAnimation { From = 0, To = -_cyberParticlePeriod, Duration = TimeSpan.FromMilliseconds(1500.0 / speed), RepeatBehavior = RepeatBehavior.Forever };
            CyberBranchParticleStream.BeginAnimation(Path.StrokeDashOffsetProperty, _cyberBranchParticleAnim);

            _cyberBranchSparkAnim = new DoubleAnimation { From = -30, To = -(_cyberSparkPeriod - 30), Duration = TimeSpan.FromMilliseconds(1200.0 / speed), RepeatBehavior = RepeatBehavior.Forever };
            CyberBranchSparkStream.BeginAnimation(Path.StrokeDashOffsetProperty, _cyberBranchSparkAnim);
        }

        private void StopCyberAnimation()
        {
            if (CyberMainMainStream == null) return;
            CyberMainMainStream.BeginAnimation(Path.StrokeDashOffsetProperty, null);
            CyberMainParticleStream.BeginAnimation(Path.StrokeDashOffsetProperty, null);
            CyberMainSparkStream.BeginAnimation(Path.StrokeDashOffsetProperty, null);
            CyberBranchMainStream.BeginAnimation(Path.StrokeDashOffsetProperty, null);
            CyberBranchParticleStream.BeginAnimation(Path.StrokeDashOffsetProperty, null);
            CyberBranchSparkStream.BeginAnimation(Path.StrokeDashOffsetProperty, null);
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
        { if (!_isResizing && !_isDraggingPipe && !_isRotating) ShowAllAnchors(); }
        private void OnPipeMouseLeave(object sender, MouseEventArgs e)
        { if (!_isResizing && !_isDraggingPipe && !_isRotating && !IsSelected) HideAllAnchors(); }
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
                SelectionBorderBranch.Visibility = Visibility.Visible;
                ShowAllAnchors();
            }
            else
            {
                SelectionBorder.Visibility = Visibility.Collapsed;
                SelectionBorderBranch.Visibility = Visibility.Collapsed;
                if (!IsMouseOver && !_isResizing && !_isRotating) HideAllAnchors();
            }
        }

        #endregion

        #region 锚点事件

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
                    (clicked.Name == "MainLengthAnchor" || clicked.Name == "BranchLengthAnchor" ||
                     clicked.Name == "BranchPositionAnchor" || clicked.Name == "PipeWidthAnchor" ||
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

        private Point ComputeFixedWorldPoint(TAnchorPosition anchor)
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

        private Point ComputeLocalFixedPoint(TAnchorPosition anchor, TPipeSize size)
        {
            double mainPipeRight = FlangeOffset + FlangeWidthConst + size.MainPipeLength;
            double totalW = mainPipeRight + FlangeWidthConst + 4;
            double totalH = MAIN_PIPE_CENTER_Y + size.PipeWidth / 2 + FLANGE_EXTENSION / 2 + 20;

            double cx = totalW / 2;
            double cy = totalH / 2;

            switch (anchor)
            {
                case TAnchorPosition.MainLength:
                    return new Point(FlangeOffset - cx, MAIN_PIPE_CENTER_Y - cy);

                case TAnchorPosition.BranchLength:
                    double branchCenterX = FlangeOffset + FlangeWidthConst + size.BranchPosition;
                    double mainTop = MAIN_PIPE_CENTER_Y - size.PipeWidth / 2;
                    return new Point(branchCenterX - cx, mainTop - cy);

                case TAnchorPosition.PipeWidth:
                    double bcx = FlangeOffset + FlangeWidthConst + size.BranchPosition;
                    return new Point(bcx - cx, MAIN_PIPE_CENTER_Y - cy);

                case TAnchorPosition.BranchPosition:
                default:
                    return new Point(0, 0);
            }
        }

        private void RepositionByFixedPoint()
        {
            if (_resizingAnchor == TAnchorPosition.BranchPosition) return;

            var currentSize = new TPipeSize
            {
                MainPipeLength = MainPipeLength,
                BranchPipeLength = BranchPipeLength,
                BranchPosition = BranchPosition,
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
            _originalPosition = new Point(Canvas.GetLeft(this), Canvas.GetTop(this));
            if (double.IsNaN(_originalPosition.X)) _originalPosition.X = 0;
            if (double.IsNaN(_originalPosition.Y)) _originalPosition.Y = 0;

            _fixedWorldPoint = ComputeFixedWorldPoint(anchor);

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
                case TAnchorPosition.MainLength:
                    MainPipeLength = _originalSize.MainPipeLength + localDx;
                    break;
                case TAnchorPosition.BranchLength:
                    BranchPipeLength = _originalSize.BranchPipeLength - localDy;
                    break;
                case TAnchorPosition.BranchPosition:
                    BranchPosition = _originalSize.BranchPosition + localDx;
                    break;
                case TAnchorPosition.PipeWidth:
                    PipeWidth = _originalSize.PipeWidth + localDy;
                    break;
            }

            RepositionByFixedPoint();

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

                double flangeHeight = _pipeWidth + FLANGE_EXTENSION;

                // 主水平管道
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

                // 左法兰
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

                // 右法兰
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

                // 支管
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

                // 顶部法兰
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

                // T 型连接处
                double junctionSize = _pipeWidth - 2;
                Canvas.SetLeft(TJunction, branchCenterX - junctionSize / 2);
                Canvas.SetTop(TJunction, MAIN_PIPE_CENTER_Y - junctionSize / 2);
                TJunction.Width = junctionSize;
                TJunction.Height = junctionSize;

                // 控件尺寸
                double totalWidth = rightFlangeX + FlangeWidthConst + 4;
                double totalHeight = (mainPipeTop + _pipeWidth) + (FLANGE_EXTENSION / 2) + 20;
                Width = totalWidth;
                Height = totalHeight;

                // 选中描边
                SelectionBorder.Width = _mainPipeLength;
                SelectionBorder.Height = _pipeWidth;
                Canvas.SetLeft(SelectionBorder, mainPipeLeft);
                Canvas.SetTop(SelectionBorder, mainPipeTop);

                SelectionBorderBranch.Width = _pipeWidth;
                SelectionBorderBranch.Height = _branchPipeLength + _pipeWidth / 2;
                Canvas.SetLeft(SelectionBorderBranch, branchPipeLeft);
                Canvas.SetTop(SelectionBorderBranch, branchPipeTop);

                // 锚点
                UpdateAnchorPositions(rightFlangeX, mainPipeTop, branchPipeLeft, branchPipeTop, branchCenterX, MAIN_PIPE_CENTER_Y);

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

        private void UpdateAnchorPositions(
            double rightFlangeX, double mainPipeTop,
            double branchPipeLeft, double branchPipeTop,
            double branchCenterX, double mainPipeCenterY)
        {
            Canvas.SetLeft(MainLengthAnchor, rightFlangeX + FlangeWidthConst + 2);
            Canvas.SetTop(MainLengthAnchor, mainPipeCenterY - 6);

            Canvas.SetLeft(BranchLengthAnchor, branchCenterX - 6);
            Canvas.SetTop(BranchLengthAnchor, branchPipeTop - FlangeWidthConst - 14);

            Canvas.SetLeft(BranchPositionAnchor, branchCenterX - _pipeWidth / 2 - 14);
            Canvas.SetTop(BranchPositionAnchor, mainPipeCenterY - 6);

            Canvas.SetLeft(PipeWidthAnchor, branchCenterX - 6);
            Canvas.SetTop(PipeWidthAnchor, mainPipeTop + _pipeWidth + 4);

            double rotateAnchorY = branchPipeTop - FlangeWidthConst - 28;
            Canvas.SetLeft(RotateAnchor, branchCenterX - 6);
            Canvas.SetTop(RotateAnchor, rotateAnchorY);

            RotateAnchorLine.X1 = branchCenterX;
            RotateAnchorLine.Y1 = branchPipeTop - FlangeWidthConst;
            RotateAnchorLine.X2 = branchCenterX;
            RotateAnchorLine.Y2 = rotateAnchorY + 6;

            Canvas.SetLeft(RotationLabel, branchCenterX - 20);
            Canvas.SetTop(RotationLabel, rotateAnchorY - 22);
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

    #region 辅助类和枚举

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
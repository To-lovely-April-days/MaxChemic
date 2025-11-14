using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using MaxChemical.Modules.Designer.Service;

namespace MaxChemical.Modules.Designer.Views.Controls
{
    public partial class ConicalFlaskControl : UserControl, INotifyPropertyChanged, IPipeSnapPoints
    {
        #region 事件定义

        public event PropertyChangedEventHandler PropertyChanged;
        public event EventHandler<FlaskDragEventArgs> FlaskDragStarted;
        public event EventHandler<FlaskDragEventArgs> FlaskDragging;
        public event EventHandler<FlaskDragEventArgs> FlaskDragCompleted;
        public event EventHandler<FlaskResizeEventArgs> FlaskResizeStarted;
        public event EventHandler<FlaskResizeEventArgs> FlaskResizing;
        public event EventHandler<FlaskResizeEventArgs> FlaskResizeCompleted;
        public event EventHandler<string> FlaskSelected;
        public event EventHandler<string> FlaskDeselected;

        #endregion

        #region 字段

        public string FlaskId { get; set; }

        private bool _isDraggingFlask = false;
        private bool _isResizing = false;
        private FlaskAnchorPosition? _resizingAnchor = null;
        private Point _dragStartPoint;
        private Point _originalPosition;
        private Size _originalSize;

        private const double MinFlaskWidth = 40;
        private const double MaxFlaskWidth = 150;
        private const double MinFlaskHeight = 60;
        private const double MaxFlaskHeight = 200;

        private double _flaskWidth = 80;
        private double _flaskHeight = 120;

        // 液体水位相关字段
        private double _liquidLevel = 0;
        private Color _liquidColor = Color.FromArgb(128, 128, 205, 225); // 半透明蓝色

        // 波动动画控制
        private bool _isWaveAnimationEnabled = true;
        private bool _waveAnimationRunning = false;
        private Storyboard _waveStoryboard;

        #endregion

        #region 公共属性

        public double FlaskWidth
        {
            get => _flaskWidth;
            set
            {
                if (Math.Abs(_flaskWidth - value) > 0.01)
                {
                    _flaskWidth = Math.Max(MinFlaskWidth, Math.Min(MaxFlaskWidth, value));
                    UpdateFlaskGeometry();
                    OnPropertyChanged(nameof(FlaskWidth));
                }
            }
        }

        public double FlaskHeight
        {
            get => _flaskHeight;
            set
            {
                if (Math.Abs(_flaskHeight - value) > 0.01)
                {
                    _flaskHeight = Math.Max(MinFlaskHeight, Math.Min(MaxFlaskHeight, value));
                    UpdateFlaskGeometry();
                    OnPropertyChanged(nameof(FlaskHeight));
                }
            }
        }

        public double LiquidLevel
        {
            get => _liquidLevel;
            set
            {
                var newLevel = Math.Max(0, Math.Min(100, value));
                if (Math.Abs(_liquidLevel - newLevel) > 0.01)
                {
                    _liquidLevel = newLevel;
                    UpdateLiquidDisplay();
                    OnPropertyChanged(nameof(LiquidLevel));
                }
            }
        }

        public Color LiquidColor
        {
            get => _liquidColor;
            set
            {
                if (_liquidColor != value)
                {
                    _liquidColor = value;
                    UpdateLiquidColor();
                    OnPropertyChanged(nameof(LiquidColor));
                }
            }
        }

        public bool IsWaveAnimationEnabled
        {
            get => _isWaveAnimationEnabled;
            set
            {
                if (_isWaveAnimationEnabled != value)
                {
                    _isWaveAnimationEnabled = value;

                    if (value && _liquidLevel > 0)
                    {
                        StartWaveAnimation();
                    }
                    else
                    {
                        StopWaveAnimation();
                    }

                    OnPropertyChanged(nameof(IsWaveAnimationEnabled));
                    Debug.WriteLine($"液体波动动画: {(value ? "开启" : "关闭")}");
                }
            }
        }

        #endregion

        #region IsSelected 依赖属性

        public static readonly DependencyProperty IsSelectedProperty =
            DependencyProperty.Register(
                nameof(IsSelected),
                typeof(bool),
                typeof(ConicalFlaskControl),
                new PropertyMetadata(false, OnIsSelectedChanged));

        public bool IsSelected
        {
            get => (bool)GetValue(IsSelectedProperty);
            set => SetValue(IsSelectedProperty, value);
        }

        private static void OnIsSelectedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is ConicalFlaskControl control)
            {
                control.UpdateSelectionVisual((bool)e.NewValue);
            }
        }

        #endregion

        #region 液体水位动画依赖属性

        public static readonly DependencyProperty LiquidLevelAnimatedProperty =
            DependencyProperty.Register(
                "LiquidLevelAnimated",
                typeof(double),
                typeof(ConicalFlaskControl),
                new PropertyMetadata(0.0, OnLiquidLevelAnimatedChanged));

        private static void OnLiquidLevelAnimatedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is ConicalFlaskControl control)
            {
                control._liquidLevel = (double)e.NewValue;
                control.UpdateLiquidDisplay();
            }
        }

        #endregion

        #region 构造函数

        public ConicalFlaskControl()
        {
            InitializeComponent();
            FlaskId = Guid.NewGuid().ToString();
            this.DataContext = this;

            bool isInDesignMode = DesignerProperties.GetIsInDesignMode(this);

            if (!isInDesignMode)
            {
                this.MouseEnter += OnFlaskMouseEnter;
                this.MouseLeave += OnFlaskMouseLeave;
                this.MouseDown += OnControlMouseDown;
                this.MouseMove += OnControlMouseMove;
                this.MouseUp += OnControlMouseUp;
                this.MouseLeave += OnControlMouseLeave;

                SetupAnchorEvents();
            }

            this.Loaded += (s, e) =>
            {
                UpdateFlaskGeometry();
                UpdateLiquidDisplay();

                // 获取动画资源
                _waveStoryboard = this.Resources["WaveAnimation"] as Storyboard;

                // ✅ 新增：优化波动动画的硬件加速
                if (_waveStoryboard != null)
                {
                    Timeline.SetDesiredFrameRate(_waveStoryboard, 30); // 限制帧率
                }
            };

            // ========== ✅ 硬件加速配置（新增） ==========

            // 1. 高质量渲染
            RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
            RenderOptions.SetEdgeMode(this, EdgeMode.Unspecified);

            // 2. 启用缓存提示
            RenderOptions.SetCachingHint(this, CachingHint.Cache);
            RenderOptions.SetCacheInvalidationThresholdMinimum(this, 0.5);
            RenderOptions.SetCacheInvalidationThresholdMaximum(this, 2.0);

            // 3. 配置位图缓存
            CacheMode = new BitmapCache
            {
                RenderAtScale = 1.0,
                SnapsToDevicePixels = true,
                EnableClearType = false
            };

            // 4. 像素对齐
            this.SnapsToDevicePixels = true;
            this.UseLayoutRounding = true;

            Debug.WriteLine($" 锥形瓶 {FlaskId} 硬件加速已启用");
        }

        #endregion

        #region 液体水位控制方法

        public void SetLiquidLevel(double percentLevel, bool animated = true)
        {
            try
            {
                percentLevel = Math.Max(0, Math.Min(100, percentLevel));

                if (animated)
                {
                    var animation = new DoubleAnimation
                    {
                        From = _liquidLevel,
                        To = percentLevel,
                        Duration = TimeSpan.FromMilliseconds(600),
                        EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut }
                    };

                    animation.Completed += (s, e) =>
                    {
                        _liquidLevel = percentLevel;
                        OnPropertyChanged(nameof(LiquidLevel));
                    };

                    this.BeginAnimation(LiquidLevelAnimatedProperty, animation);
                }
                else
                {
                    LiquidLevel = percentLevel;
                }

                Debug.WriteLine($"锥形瓶 {FlaskId} 水位设置为: {percentLevel:F1}%");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"设置液体水位失败: {ex.Message}");
            }
        }

        public void SetLiquidColor(string colorName)
        {
            try
            {
                var color = colorName?.ToLower() switch
                {
                    "蓝色" or "blue" => Color.FromArgb(128, 128, 205, 225),
                    "绿色" or "green" => Color.FromArgb(128, 102, 187, 106),
                    "红色" or "red" => Color.FromArgb(128, 239, 83, 80),
                    "黄色" or "yellow" => Color.FromArgb(128, 255, 235, 59),
                    "紫色" or "purple" => Color.FromArgb(128, 156, 39, 176),
                    "橙色" or "orange" => Color.FromArgb(128, 255, 152, 0),
                    "粉色" or "pink" => Color.FromArgb(128, 233, 30, 99),
                    "青色" or "cyan" => Color.FromArgb(128, 0, 188, 212),
                    _ => Color.FromArgb(128, 128, 205, 225)
                };

                LiquidColor = color;
                Debug.WriteLine($"液体颜色设置为: {colorName}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"设置液体颜色失败: {ex.Message}");
            }
        }

        public void SetLiquidColor(byte r, byte g, byte b, byte a = 128)
        {
            try
            {
                LiquidColor = Color.FromArgb(a, r, g, b);
                Debug.WriteLine($"液体颜色设置为: RGBA({r}, {g}, {b}, {a})");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"设置液体颜色失败: {ex.Message}");
            }
        }

        private void UpdateLiquidDisplay()
        {
            try
            {
                if (_liquidLevel <= 0)
                {
                    FlaskLiquid.Visibility = Visibility.Collapsed;
                    LiquidSurface.Visibility = Visibility.Collapsed;
                    Wave1.Visibility = Visibility.Collapsed;
                    Wave2.Visibility = Visibility.Collapsed;
                    StopWaveAnimation();
                    return;
                }

                FlaskLiquid.Visibility = Visibility.Visible;
                LiquidSurface.Visibility = Visibility.Visible;
                Wave1.Visibility = Visibility.Visible;
                Wave2.Visibility = Visibility.Visible;

                UpdateLiquidGeometry();

                // 根据开关决定是否启动波动动画
                if (_isWaveAnimationEnabled)
                {
                    StartWaveAnimation();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"更新液体显示失败: {ex.Message}");
            }
        }

        private void UpdateLiquidGeometry()
        {
            try
            {
                double neckWidth = _flaskWidth * 0.3;
                double neckHeight = _flaskHeight * 0.1;
                double transitionHeight = _flaskHeight * 0.15;

                double bodyTop = 2 + neckHeight + transitionHeight;
                double bodyBottom = _flaskHeight - 12;
                double bodyHeight = bodyBottom - bodyTop;

                double bodyTopWidth = neckWidth;
                double bodyBottomWidth = _flaskWidth * 0.8;
                double bodyBottomLeft = (_flaskWidth - bodyBottomWidth) / 2;
                double neckLeft = (_flaskWidth - neckWidth) / 2;

                double liquidHeight = bodyHeight * (_liquidLevel / 100.0);
                double liquidTop = bodyBottom - liquidHeight;

                double ratio = liquidHeight / bodyHeight;
                double liquidTopWidth = bodyTopWidth + (bodyBottomWidth - bodyTopWidth) * (1 - ratio);
                double liquidTopLeft = (_flaskWidth - liquidTopWidth) / 2;

                // 更新液体路径
                var liquidGeometry = new PathGeometry();
                var liquidFigure = new PathFigure
                {
                    StartPoint = new Point(liquidTopLeft, liquidTop)
                };
                liquidFigure.Segments.Add(new LineSegment(new Point(bodyBottomLeft + 2, bodyBottom - 2), true));
                liquidFigure.Segments.Add(new LineSegment(new Point(bodyBottomLeft + bodyBottomWidth - 2, bodyBottom - 2), true));
                liquidFigure.Segments.Add(new LineSegment(new Point(liquidTopLeft + liquidTopWidth, liquidTop), true));
                liquidFigure.IsClosed = true;
                liquidGeometry.Figures.Add(liquidFigure);
                FlaskLiquid.Data = liquidGeometry;

                // 更新液面椭圆
                Canvas.SetLeft(LiquidSurface, liquidTopLeft);
                Canvas.SetTop(LiquidSurface, liquidTop - 2);
                LiquidSurface.Width = liquidTopWidth;
                LiquidSurface.Height = 4;

                // 更新波纹1
                Canvas.SetLeft(Wave1, liquidTopLeft + 3);
                Canvas.SetTop(Wave1, liquidTop - 1);
                Wave1.Width = liquidTopWidth - 6;
                Wave1.Height = 3;

                // 更新波纹2
                Canvas.SetLeft(Wave2, liquidTopLeft + 1.5);
                Canvas.SetTop(Wave2, liquidTop - 1.5);
                Wave2.Width = liquidTopWidth - 3;
                Wave2.Height = 3;

                // 更新液体容器裁剪区域
                var clipGeometry = new PathGeometry();
                var clipFigure = new PathFigure
                {
                    StartPoint = new Point(neckLeft, bodyTop)
                };
                clipFigure.Segments.Add(new LineSegment(new Point(bodyBottomLeft + 2, bodyBottom - 2), true));
                clipFigure.Segments.Add(new LineSegment(new Point(bodyBottomLeft + bodyBottomWidth - 2, bodyBottom - 2), true));
                clipFigure.Segments.Add(new LineSegment(new Point(neckLeft + bodyTopWidth, bodyTop), true));
                clipFigure.IsClosed = true;
                clipGeometry.Figures.Add(clipFigure);
                LiquidContainer.Clip = clipGeometry;

                Debug.WriteLine($"液体几何更新: 水位 {_liquidLevel:F1}%, 高度 {liquidHeight:F0}px");


            }
            catch (Exception ex)
            {
                Debug.WriteLine($"更新液体几何失败: {ex.Message}");
            }
        }

        private void StartWaveAnimation()
        {
            try
            {
                if (_waveStoryboard != null && !_waveAnimationRunning && _isWaveAnimationEnabled)
                {
                    // ✅ 动画时禁用液体容器缓存
                    LiquidContainer.CacheMode = null;

                    _waveStoryboard.Begin(this, true);
                    _waveAnimationRunning = true;
                    Debug.WriteLine("💧 液体波动动画已启动（GPU 加速）");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"启动波动动画失败: {ex.Message}");
            }
        }


        private void StopWaveAnimation()
        {
            try
            {
                if (_waveStoryboard != null && _waveAnimationRunning)
                {
                    _waveStoryboard.Stop(this);
                    _waveAnimationRunning = false;

                    // ✅ 动画停止后恢复液体容器缓存（可选）
                    // 注意：如果液体频繁变化，保持 null
                    // LiquidContainer.CacheMode = new BitmapCache { RenderAtScale = 1.0 };

                    Debug.WriteLine("💧 液体波动动画已停止");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"停止波动动画失败: {ex.Message}");
            }
        }

        private void UpdateLiquidColor()
        {
            try
            {
                // 更新液体主体渐变
                var liquidBrush = new LinearGradientBrush
                {
                    StartPoint = new Point(0, 0.5),
                    EndPoint = new Point(1, 0.5)
                };

                Color lighterColor = LightenColor(_liquidColor, 0.15);
                Color darkerColor = DarkenColor(_liquidColor, 0.1);

                liquidBrush.GradientStops.Add(new GradientStop(_liquidColor, 0));
                liquidBrush.GradientStops.Add(new GradientStop(lighterColor, 0.3));
                liquidBrush.GradientStops.Add(new GradientStop(darkerColor, 0.5));
                liquidBrush.GradientStops.Add(new GradientStop(lighterColor, 0.7));
                liquidBrush.GradientStops.Add(new GradientStop(_liquidColor, 1));

                FlaskLiquid.Fill = liquidBrush;

                // 更新液面颜色
                var surfaceBrush = new RadialGradientBrush();
                surfaceBrush.GradientStops.Add(new GradientStop(LightenColor(_liquidColor, 0.5), 0));
                surfaceBrush.GradientStops.Add(new GradientStop(LightenColor(_liquidColor, 0.25), 0.4));
                surfaceBrush.GradientStops.Add(new GradientStop(LightenColor(_liquidColor, 0.1), 0.7));
                surfaceBrush.GradientStops.Add(new GradientStop(_liquidColor, 1));
                LiquidSurface.Fill = surfaceBrush;

                // 更新波纹颜色
                var wave1Brush = new RadialGradientBrush();
                wave1Brush.GradientStops.Add(new GradientStop(LightenColor(_liquidColor, 0.6), 0));
                wave1Brush.GradientStops.Add(new GradientStop(LightenColor(_liquidColor, 0.3), 0.5));
                wave1Brush.GradientStops.Add(new GradientStop(Color.FromArgb(0, _liquidColor.R, _liquidColor.G, _liquidColor.B), 1));
                Wave1.Fill = wave1Brush;

                var wave2Brush = new RadialGradientBrush();
                wave2Brush.GradientStops.Add(new GradientStop(LightenColor(_liquidColor, 0.8), 0));
                wave2Brush.GradientStops.Add(new GradientStop(LightenColor(_liquidColor, 0.4), 0.4));
                wave2Brush.GradientStops.Add(new GradientStop(Color.FromArgb(0, _liquidColor.R, _liquidColor.G, _liquidColor.B), 1));
                Wave2.Fill = wave2Brush;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"更新液体颜色失败: {ex.Message}");
            }
        }

        private Color LightenColor(Color color, double amount)
        {
            return Color.FromArgb(
                color.A,
                (byte)Math.Min(255, color.R + (255 - color.R) * amount),
                (byte)Math.Min(255, color.G + (255 - color.G) * amount),
                (byte)Math.Min(255, color.B + (255 - color.B) * amount)
            );
        }

        private Color DarkenColor(Color color, double amount)
        {
            return Color.FromArgb(
                color.A,
                (byte)Math.Max(0, color.R * (1 - amount)),
                (byte)Math.Max(0, color.G * (1 - amount)),
                (byte)Math.Max(0, color.B * (1 - amount))
            );
        }

        #endregion

        #region 锚点显示控制、拖拽、调整大小

        private void OnFlaskMouseEnter(object sender, MouseEventArgs e)
        {
            if (!_isResizing && !_isDraggingFlask)
            {
                ShowAllAnchors();
            }
        }

        private void OnFlaskMouseLeave(object sender, MouseEventArgs e)
        {
            if (!_isResizing && !IsSelected)
            {
                HideAllAnchors();
            }
        }

        private void ShowAllAnchors()
        {
            TopAnchor.Opacity = 0.6;
            BottomAnchor.Opacity = 0.6;
            LeftAnchor.Opacity = 0.6;
            RightAnchor.Opacity = 0.6;
        }

        private void HideAllAnchors()
        {
            TopAnchor.Opacity = 0;
            BottomAnchor.Opacity = 0;
            LeftAnchor.Opacity = 0;
            RightAnchor.Opacity = 0;
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
                if (!this.IsMouseOver && !_isResizing)
                {
                    HideAllAnchors();
                }
            }
        }

        private void SetupAnchorEvents()
        {
            TopAnchor.MouseEnter += OnAnchorMouseEnter;
            TopAnchor.MouseLeave += OnAnchorMouseLeave;
            TopAnchor.MouseDown += (s, e) => OnAnchorMouseDown(FlaskAnchorPosition.Top, e);

            BottomAnchor.MouseEnter += OnAnchorMouseEnter;
            BottomAnchor.MouseLeave += OnAnchorMouseLeave;
            BottomAnchor.MouseDown += (s, e) => OnAnchorMouseDown(FlaskAnchorPosition.Bottom, e);

            LeftAnchor.MouseEnter += OnAnchorMouseEnter;
            LeftAnchor.MouseLeave += OnAnchorMouseLeave;
            LeftAnchor.MouseDown += (s, e) => OnAnchorMouseDown(FlaskAnchorPosition.Left, e);

            RightAnchor.MouseEnter += OnAnchorMouseEnter;
            RightAnchor.MouseLeave += OnAnchorMouseLeave;
            RightAnchor.MouseDown += (s, e) => OnAnchorMouseDown(FlaskAnchorPosition.Right, e);
        }

        private void OnAnchorMouseEnter(object sender, MouseEventArgs e)
        {
            if (sender is Ellipse anchor) anchor.Opacity = 1.0;
        }

        private void OnAnchorMouseLeave(object sender, MouseEventArgs e)
        {
            if (sender is Ellipse anchor && !_isResizing) anchor.Opacity = 0.6;
        }

        private void OnControlMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (IsClickOnAnchor(e.OriginalSource)) return;

            if (e.LeftButton == MouseButtonState.Pressed && !_isResizing)
            {
                FlaskSelected?.Invoke(this, this.FlaskId);
                this.IsSelected = true;

                _isDraggingFlask = true;
                var parent = this.Parent as Canvas;
                if (parent != null)
                {
                    _dragStartPoint = e.GetPosition(parent);
                    _originalPosition = new Point(Canvas.GetLeft(this), Canvas.GetTop(this));
                    if (double.IsNaN(_originalPosition.X)) _originalPosition.X = 0;
                    if (double.IsNaN(_originalPosition.Y)) _originalPosition.Y = 0;
                }

                // ✅ 拖拽时禁用缓存
                this.CacheMode = null;
                RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.LowQuality);

                this.CaptureMouse();
                this.Opacity = 0.7;

                FlaskDragStarted?.Invoke(this, new FlaskDragEventArgs
                {
                    FlaskId = FlaskId,
                    StartPosition = _originalPosition
                });
                e.Handled = true;
            }
        }

        private void OnControlMouseMove(object sender, MouseEventArgs e)
        {
            if (_isDraggingFlask && e.LeftButton == MouseButtonState.Pressed)
            {
                var parent = this.Parent as Canvas;
                if (parent != null)
                {
                    var currentPoint = e.GetPosition(parent);
                    double deltaX = currentPoint.X - _dragStartPoint.X;
                    double deltaY = currentPoint.Y - _dragStartPoint.Y;
                    double newLeft = Math.Max(0, Math.Min(parent.ActualWidth - this.ActualWidth, _originalPosition.X + deltaX));
                    double newTop = Math.Max(0, Math.Min(parent.ActualHeight - this.ActualHeight, _originalPosition.Y + deltaY));
                    Canvas.SetLeft(this, newLeft);
                    Canvas.SetTop(this, newTop);
                    FlaskDragging?.Invoke(this, new FlaskDragEventArgs
                    {
                        FlaskId = FlaskId,
                        CurrentPosition = new Point(newLeft, newTop)
                    });
                }
                e.Handled = true;
            }
            else if (_isResizing && _resizingAnchor.HasValue)
            {
                HandleResize(e);
                e.Handled = true;
            }
        }

        private void OnControlMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (_isDraggingFlask)
            {
                CompleteFlaskDrag();
                e.Handled = true;
            }
            else if (_isResizing)
            {
                CompleteResize();
                e.Handled = true;
            }
        }

        private void OnControlMouseLeave(object sender, MouseEventArgs e)
        {
            if (_isDraggingFlask) CompleteFlaskDrag();
            else if (_isResizing) CompleteResize();
        }

        private void CompleteFlaskDrag()
        {
            if (!_isDraggingFlask) return;

            var finalPosition = new Point(Canvas.GetLeft(this), Canvas.GetTop(this));

            FlaskDragCompleted?.Invoke(this, new FlaskDragEventArgs
            {
                FlaskId = FlaskId,
                StartPosition = _originalPosition,
                CurrentPosition = finalPosition
            });

            this.Opacity = 1.0;
            _isDraggingFlask = false;

            // ✅ 拖拽结束后恢复缓存
            RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
            this.CacheMode = new BitmapCache
            {
                RenderAtScale = 1.0,
                SnapsToDevicePixels = true,
                EnableClearType = false
            };

            this.ReleaseMouseCapture();
        }

        private bool IsClickOnAnchor(object source)
        {
            return source == TopAnchor || source == BottomAnchor ||
                   source == LeftAnchor || source == RightAnchor;
        }

        private void OnAnchorMouseDown(FlaskAnchorPosition position, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                _isResizing = true;
                _resizingAnchor = position;

                var parent = this.Parent as Canvas;
                if (parent != null)
                {
                    _dragStartPoint = e.GetPosition(parent);
                    _originalPosition = new Point(Canvas.GetLeft(this), Canvas.GetTop(this));
                    _originalSize = new Size(_flaskWidth, _flaskHeight);
                    if (double.IsNaN(_originalPosition.X)) _originalPosition.X = 0;
                    if (double.IsNaN(_originalPosition.Y)) _originalPosition.Y = 0;
                }

                // ✅ 调整大小时禁用缓存
                this.CacheMode = null;
                RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.LowQuality);

                this.CaptureMouse();

                FlaskResizeStarted?.Invoke(this, new FlaskResizeEventArgs
                {
                    FlaskId = FlaskId,
                    AnchorPosition = position,
                    OriginalSize = _originalSize
                });
                e.Handled = true;
            }
        }


        private void HandleResize(MouseEventArgs e)
        {
            if (!_resizingAnchor.HasValue) return;
            var parent = this.Parent as Canvas;
            if (parent == null) return;

            var currentPoint = e.GetPosition(parent);
            double deltaX = currentPoint.X - _dragStartPoint.X;
            double deltaY = currentPoint.Y - _dragStartPoint.Y;

            switch (_resizingAnchor.Value)
            {
                case FlaskAnchorPosition.Top:
                    ResizeHeight(-deltaY, true);
                    break;
                case FlaskAnchorPosition.Bottom:
                    ResizeHeight(deltaY, false);
                    break;
                case FlaskAnchorPosition.Left:
                    ResizeWidth(-deltaX, true);
                    break;
                case FlaskAnchorPosition.Right:
                    ResizeWidth(deltaX, false);
                    break;
            }

            FlaskResizing?.Invoke(this, new FlaskResizeEventArgs
            {
                FlaskId = FlaskId,
                AnchorPosition = _resizingAnchor.Value,
                CurrentSize = new Size(_flaskWidth, _flaskHeight)
            });
        }

        private void ResizeHeight(double delta, bool fromTop)
        {
            double newHeight = Math.Max(MinFlaskHeight, Math.Min(MaxFlaskHeight, _originalSize.Height + delta));
            _flaskHeight = newHeight;

            if (fromTop)
            {
                double heightDelta = newHeight - _originalSize.Height;
                Canvas.SetTop(this, _originalPosition.Y - heightDelta);
            }

            UpdateFlaskGeometry();
        }

        private void ResizeWidth(double delta, bool fromLeft)
        {
            double newWidth = Math.Max(MinFlaskWidth, Math.Min(MaxFlaskWidth, _originalSize.Width + delta));
            _flaskWidth = newWidth;

            if (fromLeft)
            {
                double widthDelta = newWidth - _originalSize.Width;
                Canvas.SetLeft(this, _originalPosition.X - widthDelta);
            }

            UpdateFlaskGeometry();
        }

        private void CompleteResize()
        {
            if (!_isResizing) return;

            FlaskResizeCompleted?.Invoke(this, new FlaskResizeEventArgs
            {
                FlaskId = FlaskId,
                AnchorPosition = _resizingAnchor.Value,
                OriginalSize = _originalSize,
                CurrentSize = new Size(_flaskWidth, _flaskHeight)
            });

            _isResizing = false;
            _resizingAnchor = null;

            // ✅ 调整大小结束后恢复缓存
            RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
            this.CacheMode = new BitmapCache
            {
                RenderAtScale = 1.0,
                SnapsToDevicePixels = true,
                EnableClearType = false
            };

            this.ReleaseMouseCapture();
        }


        private void UpdateFlaskGeometry()
        {
            this.Width = _flaskWidth;
            this.Height = _flaskHeight;

            double neckWidth = _flaskWidth * 0.3;
            double neckHeight = _flaskHeight * 0.1;
            double neckLeft = (_flaskWidth - neckWidth) / 2;

            FlaskNeck.Width = neckWidth;
            FlaskNeck.Height = neckHeight;
            Canvas.SetLeft(FlaskNeck, neckLeft);
            Canvas.SetTop(FlaskNeck, 2);

            double transitionTop = 2 + neckHeight;
            double transitionHeight = _flaskHeight * 0.15;

            var neckTransitionGeometry = new PathGeometry();
            var neckTransitionFigure = new PathFigure
            {
                StartPoint = new Point(neckLeft, transitionTop)
            };
            neckTransitionFigure.Segments.Add(new LineSegment(new Point(neckLeft, transitionTop + transitionHeight), true));
            neckTransitionFigure.Segments.Add(new LineSegment(new Point(neckLeft + neckWidth, transitionTop + transitionHeight), true));
            neckTransitionFigure.Segments.Add(new LineSegment(new Point(neckLeft + neckWidth, transitionTop), true));
            neckTransitionFigure.IsClosed = true;
            neckTransitionGeometry.Figures.Add(neckTransitionFigure);
            FlaskNeckTransition.Data = neckTransitionGeometry;

            double bodyTop = transitionTop + transitionHeight;
            double bodyBottom = _flaskHeight - 12;
            double bodyBottomWidth = _flaskWidth * 0.8;
            double bodyBottomLeft = (_flaskWidth - bodyBottomWidth) / 2;

            var bodyGeometry = new PathGeometry();
            var bodyFigure = new PathFigure
            {
                StartPoint = new Point(neckLeft, bodyTop)
            };
            bodyFigure.Segments.Add(new LineSegment(new Point(bodyBottomLeft, bodyBottom), true));
            bodyFigure.Segments.Add(new LineSegment(new Point(bodyBottomLeft + bodyBottomWidth, bodyBottom), true));
            bodyFigure.Segments.Add(new LineSegment(new Point(neckLeft + neckWidth, bodyTop), true));
            bodyFigure.IsClosed = true;
            bodyGeometry.Figures.Add(bodyFigure);
            FlaskBody.Data = bodyGeometry;

            var darkGeometry = new PathGeometry();
            var darkFigure = new PathFigure
            {
                StartPoint = new Point(neckLeft + neckWidth - 2, bodyTop + 4)
            };
            darkFigure.Segments.Add(new LineSegment(new Point(bodyBottomLeft + bodyBottomWidth - 4, bodyBottom - 8), true));
            darkFigure.Segments.Add(new LineSegment(new Point(bodyBottomLeft + bodyBottomWidth, bodyBottom - 8), true));
            darkFigure.Segments.Add(new LineSegment(new Point(neckLeft + neckWidth, bodyTop + 4), true));
            darkFigure.IsClosed = true;
            darkGeometry.Figures.Add(darkFigure);
            RightDarkArea.Data = darkGeometry;

            var highlightGeometry = new PathGeometry();
            var highlightFigure = new PathFigure
            {
                StartPoint = new Point(neckLeft + 2, bodyTop + 4)
            };
            highlightFigure.Segments.Add(new LineSegment(new Point(bodyBottomLeft + 4, bodyBottom - 8), true));
            highlightFigure.Segments.Add(new LineSegment(new Point(bodyBottomLeft + 10, bodyBottom - 8), true));
            highlightFigure.Segments.Add(new LineSegment(new Point(neckLeft + 6, bodyTop + 6), true));
            highlightFigure.IsClosed = true;
            highlightGeometry.Figures.Add(highlightFigure);
            LeftHighlight.Data = highlightGeometry;

            var topHighlightGeometry = new PathGeometry();
            var topHighlightFigure = new PathFigure
            {
                StartPoint = new Point(neckLeft + 2, bodyTop)
            };
            topHighlightFigure.Segments.Add(new LineSegment(new Point(neckLeft + neckWidth - 2, bodyTop), true));
            topHighlightFigure.Segments.Add(new LineSegment(new Point(neckLeft + neckWidth - 4, bodyTop + 4), true));
            topHighlightFigure.Segments.Add(new LineSegment(new Point(neckLeft + 4, bodyTop + 4), true));
            topHighlightFigure.IsClosed = true;
            topHighlightGeometry.Figures.Add(topHighlightFigure);
            TopHighlight.Data = topHighlightGeometry;

            // ========== ✅ 修正：刻度线位置计算 ==========
            double bodyHeight = bodyBottom - bodyTop;

            // 100ml 刻度线（接近顶部 5%）
            double height1Percent = 0.05;
            double width1 = neckWidth + (bodyBottomWidth - neckWidth) * height1Percent;
            double left1 = (_flaskWidth - width1) / 2;
            ScaleLine1.X1 = left1;
            ScaleLine1.Y1 = bodyTop + bodyHeight * height1Percent;
            ScaleLine1.X2 = left1 + 10;
            ScaleLine1.Y2 = ScaleLine1.Y1;

            // 75ml 刻度线（25% 位置）
            double height2Percent = 0.25;
            double width2 = neckWidth + (bodyBottomWidth - neckWidth) * height2Percent;
            double left2 = (_flaskWidth - width2) / 2;
            ScaleLine2.X1 = left2;
            ScaleLine2.Y1 = bodyTop + bodyHeight * height2Percent;
            ScaleLine2.X2 = left2 + 10;
            ScaleLine2.Y2 = ScaleLine2.Y1;

            // 50ml 刻度线（50% 位置）
            double height3Percent = 0.50;
            double width3 = neckWidth + (bodyBottomWidth - neckWidth) * height3Percent;
            double left3 = (_flaskWidth - width3) / 2;
            ScaleLine3.X1 = left3;
            ScaleLine3.Y1 = bodyTop + bodyHeight * height3Percent;
            ScaleLine3.X2 = left3 + 10;
            ScaleLine3.Y2 = ScaleLine3.Y1;

            // 25ml 刻度线（75% 位置）
            double height4Percent = 0.75;
            double width4 = neckWidth + (bodyBottomWidth - neckWidth) * height4Percent;
            double left4 = (_flaskWidth - width4) / 2;
            ScaleLine4.X1 = left4;
            ScaleLine4.Y1 = bodyTop + bodyHeight * height4Percent;
            ScaleLine4.X2 = left4 + 10;
            ScaleLine4.Y2 = ScaleLine4.Y1;

            // 刻度文本位置（统一使用 -17 保证对齐）
            Canvas.SetLeft(ScaleText1, ScaleLine1.X1 - 17);
            Canvas.SetTop(ScaleText1, ScaleLine1.Y1 - 3);
            Canvas.SetLeft(ScaleText2, ScaleLine2.X1 - 17);
            Canvas.SetTop(ScaleText2, ScaleLine2.Y1 - 3);
            Canvas.SetLeft(ScaleText3, ScaleLine3.X1 - 17);
            Canvas.SetTop(ScaleText3, ScaleLine3.Y1 - 3);
            Canvas.SetLeft(ScaleText4, ScaleLine4.X1 - 17);
            Canvas.SetTop(ScaleText4, ScaleLine4.Y1 - 3);

            Canvas.SetLeft(FlaskBottom, bodyBottomLeft);
            Canvas.SetTop(FlaskBottom, bodyBottom - 4);
            FlaskBottom.Width = bodyBottomWidth;
            FlaskBottom.Height = 10;

            Canvas.SetLeft(BottomHighlight, bodyBottomLeft + 6);
            Canvas.SetTop(BottomHighlight, bodyBottom - 3);
            BottomHighlight.Width = bodyBottomWidth - 12;
            BottomHighlight.Height = 4;

            Canvas.SetLeft(FlaskGroundShadow, bodyBottomLeft - 4);
            Canvas.SetTop(FlaskGroundShadow, _flaskHeight - 6);
            FlaskGroundShadow.Width = bodyBottomWidth + 8;

            SelectionBorder.Width = _flaskWidth + 4;
            SelectionBorder.Height = _flaskHeight + 4;

            UpdateAnchorPositions();
            UpdateLiquidDisplay();
        }
        public void DebugLiquidGeometry()
        {
            Debug.WriteLine("========== 液体几何调试信息 ==========");
            Debug.WriteLine($"控件尺寸: {_flaskWidth} x {_flaskHeight}");
            Debug.WriteLine($"液体水位: {_liquidLevel}%");
            Debug.WriteLine($"液体颜色: {_liquidColor}");

            Debug.WriteLine("\n元素可见性:");
            Debug.WriteLine($"  FlaskLiquid: {FlaskLiquid.Visibility}");
            Debug.WriteLine($"  LiquidSurface: {LiquidSurface.Visibility}");
            Debug.WriteLine($"  Wave1: {Wave1.Visibility}");
            Debug.WriteLine($"  Wave2: {Wave2.Visibility}");

            if (FlaskLiquid.Data is PathGeometry geo && geo.Figures.Count > 0)
            {
                var figure = geo.Figures[0];
                Debug.WriteLine("\nFlaskLiquid 路径坐标:");
                Debug.WriteLine($"  StartPoint: {figure.StartPoint}");
                foreach (var segment in figure.Segments)
                {
                    if (segment is LineSegment line)
                    {
                        Debug.WriteLine($"  LineSegment: {line.Point}");
                    }
                }
            }

            Debug.WriteLine($"\nLiquidSurface 位置:");
            Debug.WriteLine($"  Left: {Canvas.GetLeft(LiquidSurface)}");
            Debug.WriteLine($"  Top: {Canvas.GetTop(LiquidSurface)}");
            Debug.WriteLine($"  Width: {LiquidSurface.Width}");
            Debug.WriteLine($"  Height: {LiquidSurface.Height}");
        }
        private void UpdateAnchorPositions()
        {
            Canvas.SetLeft(TopAnchor, _flaskWidth / 2 - 6);
            Canvas.SetTop(TopAnchor, -6);

            Canvas.SetLeft(BottomAnchor, _flaskWidth / 2 - 6);
            Canvas.SetTop(BottomAnchor, _flaskHeight - 6);

            Canvas.SetLeft(LeftAnchor, -6);
            Canvas.SetTop(LeftAnchor, _flaskHeight / 2 - 6);

            Canvas.SetLeft(RightAnchor, _flaskWidth - 6);
            Canvas.SetTop(RightAnchor, _flaskHeight / 2 - 6);
        }

        #endregion
        #region IPipeSnapPoints 实现

        /// <summary>
        /// 获取锥形瓶的吸附点（修正版 - 紧贴边缘）
        /// </summary>
        public List<PipeSnapPoint> GetSnapPoints()
        {
            var snapPoints = new List<PipeSnapPoint>();

            var parent = this.Parent as Canvas;
            if (parent == null) return snapPoints;

            // 获取锥形瓶在画布上的位置
            var flaskLeft = Canvas.GetLeft(this);
            var flaskTop = Canvas.GetTop(this);
            if (double.IsNaN(flaskLeft)) flaskLeft = 0;
            if (double.IsNaN(flaskTop)) flaskTop = 0;

            // 🔬 锥形瓶关键几何参数
            double neckWidth = _flaskWidth * 0.3;
            double neckHeight = _flaskHeight * 0.1;
            double neckLeft = (_flaskWidth - neckWidth) / 2;
            double transitionHeight = _flaskHeight * 0.15;
            double bodyTop = 2 + neckHeight + transitionHeight;
            double bodyBottomWidth = _flaskWidth * 0.8;
            double bodyBottomLeft = (_flaskWidth - bodyBottomWidth) / 2;
            double bodyBottom = _flaskHeight - 12;

            // ========== ✅ 修复：顶部吸附点（瓶口正上方外侧） ==========
            snapPoints.Add(new PipeSnapPoint
            {
                WorldPosition = new Point(
                    flaskLeft + _flaskWidth / 2,  // 水平中心
                    flaskTop + 0  // ✅ 紧贴瓶口顶部外边缘
                ),
                Direction = SnapDirection.Up,
                Description = $"锥形瓶顶部（瓶口）"
            });

            // ========== ✅ 修复：左侧吸附点（瓶颈底部左外侧） ==========
            snapPoints.Add(new PipeSnapPoint
            {
                WorldPosition = new Point(
                    flaskLeft + neckLeft - 5,  // ✅ 瓶颈左边缘外侧 5px
                    flaskTop + bodyTop
                ),
                Direction = SnapDirection.Left,
                Description = $"锥形瓶左侧（瓶颈）"
            });

            // ========== ✅ 修复：右侧吸附点（瓶颈底部右外侧） ==========
            snapPoints.Add(new PipeSnapPoint
            {
                WorldPosition = new Point(
                    flaskLeft + neckLeft + neckWidth + 5,  // ✅ 瓶颈右边缘外侧 5px
                    flaskTop + bodyTop
                ),
                Direction = SnapDirection.Right,
                Description = $"锥形瓶右侧（瓶颈）"
            });

            // ========== ✅ 修复：底部吸附点（瓶底正下方外侧） ==========
            snapPoints.Add(new PipeSnapPoint
            {
                WorldPosition = new Point(
                    flaskLeft + _flaskWidth / 2,
                    flaskTop + _flaskHeight + 5  // ✅ 瓶底外侧 5px
                ),
                Direction = SnapDirection.Down,
                Description = $"锥形瓶底部"
            });

            Debug.WriteLine($"🔬 锥形瓶 {FlaskId} 提供 {snapPoints.Count} 个吸附点:");
            foreach (var point in snapPoints)
            {
                Debug.WriteLine($"  📍 {point.Description} at ({point.WorldPosition.X:F1}, {point.WorldPosition.Y:F1}) 方向:{point.Direction}");
            }

            return snapPoints;
        }

        #endregion

        #region INotifyPropertyChanged

        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        #endregion

        #region 公共方法

        public void SetSelected(bool selected)
        {
            IsSelected = selected;
        }

        public (double level, Color color) GetLiquidInfo()
        {
            return (_liquidLevel, _liquidColor);
        }

        public void ClearLiquid(bool animated = true)
        {
            SetLiquidLevel(0, animated);
        }

        public void FillLiquid(bool animated = true)
        {
            SetLiquidLevel(100, animated);
        }

        #endregion
    }

    #region 辅助类和枚举

    public enum FlaskAnchorPosition
    {
        Top,
        Bottom,
        Left,
        Right
    }

    public class FlaskDragEventArgs : EventArgs
    {
        public string FlaskId { get; set; }
        public Point StartPosition { get; set; }
        public Point CurrentPosition { get; set; }
    }

    public class FlaskResizeEventArgs : EventArgs
    {
        public string FlaskId { get; set; }
        public FlaskAnchorPosition AnchorPosition { get; set; }
        public Size OriginalSize { get; set; }
        public Size CurrentSize { get; set; }
    }

    #endregion
}
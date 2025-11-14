using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using MaxChemical.Modules.Designer.Service;

namespace MaxChemical.Modules.Designer.Views.Controls
{
    public partial class StraightPipeControl : UserControl, IPipeSnapPoints
    {
        // 事件定义
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

        public string PipeId { get; set; }

        #region 依赖属性

        public static readonly DependencyProperty IsSelectedProperty =
            DependencyProperty.Register(
                nameof(IsSelected),
                typeof(bool),
                typeof(StraightPipeControl),
                new PropertyMetadata(false, OnIsSelectedChanged));

        public bool IsSelected
        {
            get => (bool)GetValue(IsSelectedProperty);
            set => SetValue(IsSelectedProperty, value);
        }

        private static void OnIsSelectedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is StraightPipeControl control)
            {
                control.UpdateSelectionVisual((bool)e.NewValue);
            }
        }

        #endregion

        private bool _isDraggingPipe = false;
        private bool _isResizing = false;
        private bool _isRotating = false;
        private PipeAnchorPosition? _resizingAnchor = null;
        private Point _dragStartPoint;
        private Point _originalPosition;
        private Size _originalSize;
        private double _originalRotation = 0;
        private Point _fixedPoint;

        private const double MinPipeLength = 50;
        private const double MaxPipeLength = 500;
        private const double MinPipeWidth = 8;
        private const double MaxPipeWidth = 100;

        // ✅ 默认管道长度和宽度（匹配XAML初始值）
        private double _pipeLength = 85.9;  // SVG: 372 * 0.230833
        private double _pipeWidth = 16.6;   // SVG: 72 * 0.230833
        private double _rotationAngle = 0;

        private RotateTransform _rotateTransform;
        private TransformGroup _transformGroup;
        private DispatcherTimer _rotationLabelTimer;

        // ✅ SVG坐标系常量
        private const double SCALE_FACTOR = 0.230833;
        private const double LEFT_FLANGE_LEFT = 0.35;      // SVG: 1.5 * 0.230833
        private const double LEFT_FLANGE_WIDTH = 2.54;     // SVG: 11 * 0.230833
        private const double PIPE_LEFT = 3.2;              // SVG: 14 * 0.230833
        private const double RIGHT_FLANGE_WIDTH = 2.54;    // SVG: 11 * 0.230833

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

        public StraightPipeControl()
        {
            InitializeComponent();
            PipeId = Guid.NewGuid().ToString();
            this.DataContext = this;

            _rotateTransform = new RotateTransform(0);
            _transformGroup = new TransformGroup();
            _transformGroup.Children.Add(_rotateTransform);
            this.RenderTransform = _transformGroup;
            this.RenderTransformOrigin = new Point(0.5, 0.5);

            bool isInDesignMode = DesignerProperties.GetIsInDesignMode(this);

            if (!isInDesignMode)
            {
                _rotationLabelTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(2)
                };
                _rotationLabelTimer.Tick += (s, e) =>
                {
                    RotationLabel.Visibility = Visibility.Collapsed;
                    _rotationLabelTimer.Stop();
                };

                this.MouseEnter += OnPipeMouseEnter;
                this.MouseLeave += OnPipeMouseLeave;
                this.MouseDown += OnControlMouseDown;
                this.MouseMove += OnControlMouseMove;
                this.MouseUp += OnControlMouseUp;
                this.MouseLeave += OnControlMouseLeave;

                SetupAnchorEvents();
            }

            this.Loaded += (s, e) => UpdatePipeGeometry();
            Dispatcher.BeginInvoke(new Action(() => UpdatePipeGeometry()),
                System.Windows.Threading.DispatcherPriority.Loaded);

            // 1. 启用硬件渲染（强制使用 GPU）
            RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality); // 最高质量
            RenderOptions.SetEdgeMode(this, EdgeMode.Aliased); // 关闭抗锯齿提升性能

            // 2. 启用位图缓存（关键优化）
            RenderOptions.SetCachingHint(this, CachingHint.Cache);
            RenderOptions.SetCacheInvalidationThresholdMinimum(this, 0.5);
            RenderOptions.SetCacheInvalidationThresholdMaximum(this, 2.0);

            // 3. 配置位图缓存
            CacheMode = new BitmapCache
            {
                RenderAtScale = 1.0,        // 按实际比例缓存
                SnapsToDevicePixels = true, // 对齐像素边界
                EnableClearType = false     // 禁用 ClearType（GPU 不支持）
            };

            // 4. 强制硬件加速（关键）
            this.SetValue(RenderOptions.EdgeModeProperty, EdgeMode.Unspecified);
        }

        #region 锚点显示控制

        private void OnPipeMouseEnter(object sender, MouseEventArgs e)
        {
            if (!_isResizing && !_isDraggingPipe && !_isRotating)
            {
                ShowAllAnchors();
            }
        }

        private void OnPipeMouseLeave(object sender, MouseEventArgs e)
        {
            if (!_isResizing && !_isRotating && !IsSelected)
            {
                HideAllAnchors();
            }
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

        #region 管道几何更新

        private void UpdatePipeGeometry()
        {
            // ✅ 控件总宽度 = 左法兰left + 左法兰宽 + 管道宽 + 右法兰宽
            this.Width = LEFT_FLANGE_LEFT + LEFT_FLANGE_WIDTH + _pipeLength + RIGHT_FLANGE_WIDTH;

            // ✅ 控件总高度根据管道宽度动态计算
            // 端盖高度 = 管道宽度 + 上下各延伸部分
            double flangeExtension = 5.8;  // SVG中端盖比管道多出的高度 (97-72)/2 * 0.230833 ≈ 2.9 * 2
            this.Height = _pipeWidth + flangeExtension;

            // ✅ 管道背景和主体位置
            double pipeTopOffset = flangeExtension / 2;  // 管道距离顶部的偏移

            PipeBackground.Width = _pipeLength;
            PipeBackground.Height = _pipeWidth;
            Canvas.SetLeft(PipeBackground, PIPE_LEFT);
            Canvas.SetTop(PipeBackground, pipeTopOffset);

            MainPipe.Width = _pipeLength;
            MainPipe.Height = _pipeWidth;
            Canvas.SetLeft(MainPipe, PIPE_LEFT);
            Canvas.SetTop(MainPipe, pipeTopOffset);

            // ✅ 顶部和底部边缘线
            double edgeLeft = PIPE_LEFT + 0.38;
            double edgeRight = PIPE_LEFT + _pipeLength - 0.38;
            double topEdgeY = pipeTopOffset + 0.32;
            double bottomEdgeY = pipeTopOffset + _pipeWidth - 0.32;

            TopEdgeLine.X1 = edgeLeft;
            TopEdgeLine.X2 = edgeRight;
            TopEdgeLine.Y1 = topEdgeY;
            TopEdgeLine.Y2 = topEdgeY;

            BottomEdgeLine.X1 = edgeLeft;
            BottomEdgeLine.X2 = edgeRight;
            BottomEdgeLine.Y1 = bottomEdgeY;
            BottomEdgeLine.Y2 = bottomEdgeY;

            UpdateFlanges();

            // ✅ 选择边框
            SelectionBorder.Width = _pipeLength + 4;
            SelectionBorder.Height = _pipeWidth + 4;
            Canvas.SetLeft(SelectionBorder, PIPE_LEFT - 2);
            Canvas.SetTop(SelectionBorder, pipeTopOffset - 2);

            UpdateAnchorPositions();
            UpdateRotationLabelPosition();
        }

        private void UpdateFlanges()
        {
            // ✅ 法兰高度 = 管道宽度 + 上下延伸
            double flangeExtension = 5.8;  // 上下各延伸 2.9
            double flangeHeight = _pipeWidth + flangeExtension;
            double flangeTop = 0;  // 法兰从控件顶部开始

            // ✅ 左侧法兰
            Canvas.SetLeft(LeftFlangeGroup, LEFT_FLANGE_LEFT);
            Canvas.SetTop(LeftFlangeGroup, flangeTop);
            LeftFlangeBase.Height = flangeHeight;
            LeftFlangeGradient.Height = flangeHeight;
            LeftFlangeBorder.Height = flangeHeight;

            // ✅ 右侧法兰 - 动态计算位置
            double rightFlangeLeft = LEFT_FLANGE_LEFT + LEFT_FLANGE_WIDTH + _pipeLength + (PIPE_LEFT - LEFT_FLANGE_LEFT - LEFT_FLANGE_WIDTH);
            Canvas.SetLeft(RightFlangeGroup, rightFlangeLeft);
            Canvas.SetTop(RightFlangeGroup, flangeTop);
            RightFlangeBase.Height = flangeHeight;
            RightFlangeGradient.Height = flangeHeight;
            RightFlangeBorder.Height = flangeHeight;
        }

        private void UpdateAnchorPositions()
        {
            double centerY = this.Height / 2;
            double centerX = this.Width / 2;

            Canvas.SetLeft(LeftAnchor, -6);
            Canvas.SetTop(LeftAnchor, centerY - 6);

            Canvas.SetLeft(RightAnchor, this.Width - 6);
            Canvas.SetTop(RightAnchor, centerY - 6);

            Canvas.SetLeft(TopAnchor, centerX - 6);
            Canvas.SetTop(TopAnchor, 4);

            Canvas.SetLeft(BottomAnchor, centerX - 6);
            Canvas.SetTop(BottomAnchor, this.Height - 16);

            Canvas.SetLeft(RotateAnchor, centerX - 6);
            Canvas.SetTop(RotateAnchor, -30);

            RotateAnchorLine.X1 = centerX;
            RotateAnchorLine.Y1 = 0;
            RotateAnchorLine.X2 = centerX;
            RotateAnchorLine.Y2 = -24;
        }

        private void UpdateRotationLabelPosition()
        {
            double centerX = this.Width / 2;
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
                var glowEffect = new System.Windows.Media.Effects.DropShadowEffect
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

                if (!this.IsMouseOver && !_isResizing && !_isRotating)
                {
                    HideAllAnchors();
                }
            }
        }

        #endregion

        #region 整个管道拖动

        private void OnControlMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (IsClickOnAnchor(e.OriginalSource)) return;

            if (e.LeftButton == MouseButtonState.Pressed && !_isResizing && !_isRotating)
            {
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
            var finalPosition = new Point(Canvas.GetLeft(this), Canvas.GetTop(this));
            PipeDragCompleted?.Invoke(this, new PipeDragEventArgs { PipeId = PipeId, StartPosition = _originalPosition, CurrentPosition = finalPosition });
            this.Opacity = 1.0;
            _isDraggingPipe = false;
            this.ReleaseMouseCapture();
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
                var parent = this.Parent as Canvas;
                if (parent != null)
                {
                    _dragStartPoint = e.GetPosition(parent);
                    _originalPosition = new Point(Canvas.GetLeft(this), Canvas.GetTop(this));
                    _originalSize = new Size(_pipeLength, _pipeWidth);
                    if (double.IsNaN(_originalPosition.X)) _originalPosition.X = 0;
                    if (double.IsNaN(_originalPosition.Y)) _originalPosition.Y = 0;

                    _fixedPoint = CalculateFixedPoint(position);
                }
                this.CaptureMouse();
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
            var parent = this.Parent as Canvas;
            if (parent == null) return new Point();

            var pipeLeft = Canvas.GetLeft(this);
            var pipeTop = Canvas.GetTop(this);
            if (double.IsNaN(pipeLeft)) pipeLeft = 0;
            if (double.IsNaN(pipeTop)) pipeTop = 0;

            Point center = new Point(
                pipeLeft + this.ActualWidth / 2,
                pipeTop + this.ActualHeight / 2
            );

            double radians = _rotationAngle * Math.PI / 180.0;
            double cos = Math.Cos(radians);
            double sin = Math.Sin(radians);

            Point localFixed = new Point();

            switch (draggedAnchor)
            {
                case PipeAnchorPosition.Left:
                    localFixed = new Point(_pipeLength / 2, 0);
                    break;
                case PipeAnchorPosition.Right:
                    localFixed = new Point(-_pipeLength / 2, 0);
                    break;
                case PipeAnchorPosition.Top:
                    localFixed = new Point(0, _pipeWidth / 2);
                    break;
                case PipeAnchorPosition.Bottom:
                    localFixed = new Point(0, -_pipeWidth / 2);
                    break;
            }

            Point worldFixed = new Point(
                localFixed.X * cos - localFixed.Y * sin,
                localFixed.X * sin + localFixed.Y * cos
            );

            return new Point(
                center.X + worldFixed.X,
                center.Y + worldFixed.Y
            );
        }

        private void HandleResize(MouseEventArgs e)
        {
            if (!_resizingAnchor.HasValue) return;
            var parent = this.Parent as Canvas;
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

            if (_resizingAnchor == PipeAnchorPosition.Left)
            {
                projectedLength = -projectedLength;
            }

            double newLength = Math.Abs(projectedLength);
            newLength = Math.Max(MinPipeLength, Math.Min(MaxPipeLength, newLength));

            _pipeLength = newLength;

            double fixedLocalX = (_resizingAnchor == PipeAnchorPosition.Right)
                ? -_pipeLength / 2
                : _pipeLength / 2;

            Point fixedOffset = new Point(
                fixedLocalX * cos,
                fixedLocalX * sin
            );

            Point newCenter = new Point(
                _fixedPoint.X - fixedOffset.X,
                _fixedPoint.Y - fixedOffset.Y
            );

            Canvas.SetLeft(this, newCenter.X - this.ActualWidth / 2);
            Canvas.SetTop(this, newCenter.Y - this.ActualHeight / 2);

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

            double newWidth = Math.Abs(perpDelta);
            newWidth = Math.Max(MinPipeWidth, Math.Min(MaxPipeWidth, newWidth));

            _pipeWidth = newWidth;

            double fixedLocalY = (_resizingAnchor == PipeAnchorPosition.Bottom)
                ? -_pipeWidth / 2
                : _pipeWidth / 2;

            Point fixedOffset = new Point(
                -fixedLocalY * sin,
                fixedLocalY * cos
            );

            Point newCenter = new Point(
                _fixedPoint.X - fixedOffset.X,
                _fixedPoint.Y - fixedOffset.Y
            );

            Canvas.SetLeft(this, newCenter.X - this.ActualWidth / 2);
            Canvas.SetTop(this, newCenter.Y - this.ActualHeight / 2);

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
            this.ReleaseMouseCapture();
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
                var parent = this.Parent as Canvas;
                if (parent != null) _dragStartPoint = e.GetPosition(parent);
                this.CaptureMouse();
                PipeRotateStarted?.Invoke(this, new PipeRotateEventArgs { PipeId = PipeId, OriginalAngle = _originalRotation });
                ShowRotationLabel();
                e.Handled = true;
            }
        }

        private void HandleRotate(MouseEventArgs e)
        {
            var parent = this.Parent as Canvas;
            if (parent == null) return;
            var currentPoint = e.GetPosition(parent);
            var pipeLeft = Canvas.GetLeft(this);
            var pipeTop = Canvas.GetTop(this);
            if (double.IsNaN(pipeLeft)) pipeLeft = 0;
            if (double.IsNaN(pipeTop)) pipeTop = 0;

            Point center = new Point(pipeLeft + this.ActualWidth / 2, pipeTop + this.ActualHeight / 2);
            double startAngle = Math.Atan2(_dragStartPoint.Y - center.Y, _dragStartPoint.X - center.X) * 180 / Math.PI;
            double currentAngle = Math.Atan2(currentPoint.Y - center.Y, currentPoint.X - center.X) * 180 / Math.PI;
            double deltaAngle = currentAngle - startAngle;
            double newAngle = _originalRotation + deltaAngle;

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
            this.ReleaseMouseCapture();
        }

        private void ShowRotationLabel()
        {
            RotationLabel.Visibility = Visibility.Visible;
            _rotationLabelTimer.Stop();
            _rotationLabelTimer.Start();
        }

        #endregion

        #region 选择状态

        public void SetSelected(bool selected)
        {
            IsSelected = selected;
        }

        #endregion

        #region 辅助方法

        /// <summary>
        /// ✅ 获取锚点位置（考虑SVG布局）
        /// </summary>
        public Point GetAnchorPosition(PipeAnchorPosition position)
        {
            var parent = this.Parent as Canvas;
            if (parent == null) return new Point();

            var pipeLeft = Canvas.GetLeft(this);
            var pipeTop = Canvas.GetTop(this);
            if (double.IsNaN(pipeLeft)) pipeLeft = 0;
            if (double.IsNaN(pipeTop)) pipeTop = 0;

            switch (position)
            {
                case PipeAnchorPosition.Left:
                    // 左法兰中心：left + 法兰left + 法兰宽/2
                    return new Point(
                        pipeLeft + LEFT_FLANGE_LEFT + LEFT_FLANGE_WIDTH / 2,
                        pipeTop + this.Height / 2
                    );

                case PipeAnchorPosition.Right:
                    // 右法兰中心：控件宽 - 右法兰宽/2
                    return new Point(
                        pipeLeft + this.Width - RIGHT_FLANGE_WIDTH / 2,
                        pipeTop + this.Height / 2
                    );

                case PipeAnchorPosition.Top:
                    return new Point(pipeLeft + this.Width / 2, pipeTop + 14.8);

                case PipeAnchorPosition.Bottom:
                    return new Point(pipeLeft + this.Width / 2, pipeTop + 14.8 + _pipeWidth);

                default:
                    return new Point();
            }
        }

        #endregion

        #region IPipeSnapPoints 实现

        /// <summary>
        /// ✅ 获取直管的两个法兰吸附点（SVG精确坐标）
        /// </summary>
        public List<PipeSnapPoint> GetSnapPoints()
        {
            var snapPoints = new List<PipeSnapPoint>();

            var parent = this.Parent as Canvas;
            if (parent == null) return snapPoints;

            var pipeLeft = Canvas.GetLeft(this);
            var pipeTop = Canvas.GetTop(this);
            if (double.IsNaN(pipeLeft)) pipeLeft = 0;
            if (double.IsNaN(pipeTop)) pipeTop = 0;

            // 控件中心点
            Point center = new Point(
                pipeLeft + this.ActualWidth / 2,
                pipeTop + this.ActualHeight / 2
            );

            double radians = _rotationAngle * Math.PI / 180.0;
            double cos = Math.Cos(radians);
            double sin = Math.Sin(radians);

            // ✅ 左侧法兰中心（相对控件中心）
            double leftFlangeX = (LEFT_FLANGE_LEFT + LEFT_FLANGE_WIDTH / 2) - this.ActualWidth / 2;
            double leftFlangeY = 0;

            Point leftFlangeLocal = new Point(leftFlangeX, leftFlangeY);
            Point leftFlangeWorld = RotatePoint(leftFlangeLocal, radians);

            snapPoints.Add(new PipeSnapPoint
            {
                WorldPosition = new Point(
                    center.X + leftFlangeWorld.X,
                    center.Y + leftFlangeWorld.Y
                ),
                Direction = GetSnapDirectionFromAngle(radians + Math.PI),
                Description = $"直管左法兰 (长度:{_pipeLength:F0})"
            });

            // ✅ 右侧法兰中心（相对控件中心）
            double rightFlangeX = this.ActualWidth - RIGHT_FLANGE_WIDTH / 2 - this.ActualWidth / 2;
            double rightFlangeY = 0;

            Point rightFlangeLocal = new Point(rightFlangeX, rightFlangeY);
            Point rightFlangeWorld = RotatePoint(rightFlangeLocal, radians);

            snapPoints.Add(new PipeSnapPoint
            {
                WorldPosition = new Point(
                    center.X + rightFlangeWorld.X,
                    center.Y + rightFlangeWorld.Y
                ),
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
                localPoint.X * sin + localPoint.Y * cos
            );
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
        Left,
        Right,
        Top,
        Bottom
    }

    #endregion
}
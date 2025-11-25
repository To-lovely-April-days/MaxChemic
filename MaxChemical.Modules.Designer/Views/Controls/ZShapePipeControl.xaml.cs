using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using MaxChemical.Modules.Designer.Service;

namespace MaxChemical.Modules.Designer.Views.Controls
{
    /// <summary>
    /// Z形管道控件
    /// </summary>
    public partial class ZShapePipeControl : UserControl, INotifyPropertyChanged, IPipeSnapPoints
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

        // ✅ 新增：管道选中事件
        public event EventHandler<string> PipeSelected;
        public event EventHandler<string> PipeDeselected;

        #endregion

        #region 字段

        private bool _isDraggingPipe = false;
        private bool _isResizing = false;
        private bool _isRotating = false;
        private ZAnchorPosition? _resizingAnchor = null;
        private Point _dragStartPoint;
        private Point _originalPosition;
        private ZPipeSize _originalSize;
        private double _originalRotation = 0;

        private const double MinHorizontalLength = 30;
        private const double MaxHorizontalLength = 200;
        private const double MinVerticalOffset = 40;
        private const double MaxVerticalOffset = 150;
        private const double MinPipeWidth = 8;
        private const double MaxPipeWidth = 40;

        private double _topHorizontalLength = 15;
        private double _bottomHorizontalLength = 15;
        private double _verticalOffset = 20;
        private double _pipeWidth = 20;
        private double _rotationAngle = 0;

        private RotateTransform _rotateTransform;
        private TransformGroup _transformGroup;
        private DispatcherTimer _rotationLabelTimer;
        // ✅ 与 ElbowPipeControl 保持一致
        private const double FlangeWidth = 2.54;      // 法兰宽度
        private const double FlangeOffset = 0.35;     // 法兰偏移
        private const double FlangeSpacing = 2.0;     // 法兰与管道间距
        #endregion

        #region ✅ IsSelected 依赖属性

        /// <summary>
        /// 是否选中的依赖属性
        /// </summary>
        public static readonly DependencyProperty IsSelectedProperty =
            DependencyProperty.Register(
                nameof(IsSelected),
                typeof(bool),
                typeof(ZShapePipeControl),
                new PropertyMetadata(false, OnIsSelectedChanged));

        /// <summary>
        /// 是否选中
        /// </summary>
        public bool IsSelected
        {
            get => (bool)GetValue(IsSelectedProperty);
            set => SetValue(IsSelectedProperty, value);
        }

        /// <summary>
        /// 选中状态变化回调
        /// </summary>
        private static void OnIsSelectedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is ZShapePipeControl control)
            {
                control.UpdateSelectionVisual((bool)e.NewValue);
                control.OnPropertyChanged(nameof(IsSelected));
            }
        }

        #endregion

        #region 属性

        public string PipeId { get; set; }

        public double TopHorizontalLength
        {
            get => _topHorizontalLength;
            set
            {
                var newValue = Math.Max(MinHorizontalLength, Math.Min(MaxHorizontalLength, value));
                if (Math.Abs(_topHorizontalLength - newValue) > 0.01)
                {
                    _topHorizontalLength = newValue;
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
                var newValue = Math.Max(MinHorizontalLength, Math.Min(MaxHorizontalLength, value));
                if (Math.Abs(_bottomHorizontalLength - newValue) > 0.01)
                {
                    _bottomHorizontalLength = newValue;
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
                var newValue = Math.Max(MinVerticalOffset, Math.Min(MaxVerticalOffset, value));
                if (Math.Abs(_verticalOffset - newValue) > 0.01)
                {
                    _verticalOffset = newValue;
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
                var newValue = Math.Max(MinPipeWidth, Math.Min(MaxPipeWidth, value));
                if (Math.Abs(_pipeWidth - newValue) > 0.01)
                {
                    _pipeWidth = newValue;
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
            this.DataContext = this;

            // 初始化旋转变换
            _rotateTransform = new RotateTransform(0);
            _transformGroup = new TransformGroup();
            _transformGroup.Children.Add(_rotateTransform);
            this.RenderTransform = _transformGroup;
            this.RenderTransformOrigin = new Point(0.5, 0.5);

            // 检查是否在设计模式
            bool isInDesignMode = DesignerProperties.GetIsInDesignMode(this);

            if (!isInDesignMode)
            {
                // 初始化旋转标签定时器
                _rotationLabelTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(2)
                };
                _rotationLabelTimer.Tick += (s, e) =>
                {
                    RotationLabel.Visibility = Visibility.Collapsed;
                    _rotationLabelTimer.Stop();
                };

                // 绑定事件
                this.MouseEnter += OnPipeMouseEnter;
                this.MouseLeave += OnPipeMouseLeave;
                this.MouseDown += OnControlMouseDown;
                this.MouseMove += OnControlMouseMove;
                this.MouseUp += OnControlMouseUp;
                this.MouseLeave += OnControlMouseLeave;

                SetupAnchorEvents();
            }

            this.Loaded += (s, e) => UpdatePipeGeometry();

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

        #endregion

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
            if (!_isResizing && !_isDraggingPipe && !_isRotating && !IsSelected)
            {
                HideAllAnchors();
            }
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

        /// <summary>
        /// ✅ 更新选中视觉效果（取消选中时完全恢复原样）
        /// </summary>
        private void UpdateSelectionVisual(bool isSelected)
        {
            if (isSelected)
            {
                // ========== 选中时：只显示选择边框和锚点 ==========
                SelectionBorder.Visibility = Visibility.Visible;
                ShowAllAnchors();
            }
            else
            {
                // ========== ✅ 取消选中时：完全恢复原始状态 ==========
                SelectionBorder.Visibility = Visibility.Collapsed;

                if (!this.IsMouseOver && !_isResizing && !_isRotating)
                {
                    HideAllAnchors();
                }
            }
        }

        #endregion

        #region 锚点事件设置

        private void SetupAnchorEvents()
        {
            // 上方水平长度锚点
            TopLengthAnchor.MouseEnter += OnAnchorMouseEnter;
            TopLengthAnchor.MouseLeave += OnAnchorMouseLeave;
            TopLengthAnchor.MouseDown += (s, e) => StartResize(ZAnchorPosition.TopLength, e);
            TopLengthAnchor.MouseMove += OnAnchorMouseMove;
            TopLengthAnchor.MouseUp += OnAnchorMouseUp;

            // 下方水平长度锚点
            BottomLengthAnchor.MouseEnter += OnAnchorMouseEnter;
            BottomLengthAnchor.MouseLeave += OnAnchorMouseLeave;
            BottomLengthAnchor.MouseDown += (s, e) => StartResize(ZAnchorPosition.BottomLength, e);
            BottomLengthAnchor.MouseMove += OnAnchorMouseMove;
            BottomLengthAnchor.MouseUp += OnAnchorMouseUp;

            // 垂直偏移锚点
            VerticalOffsetAnchor.MouseEnter += OnAnchorMouseEnter;
            VerticalOffsetAnchor.MouseLeave += OnAnchorMouseLeave;
            VerticalOffsetAnchor.MouseDown += (s, e) => StartResize(ZAnchorPosition.VerticalOffset, e);
            VerticalOffsetAnchor.MouseMove += OnAnchorMouseMove;
            VerticalOffsetAnchor.MouseUp += OnAnchorMouseUp;

            // 管道宽度锚点
            PipeWidthAnchor.MouseEnter += OnAnchorMouseEnter;
            PipeWidthAnchor.MouseLeave += OnAnchorMouseLeave;
            PipeWidthAnchor.MouseDown += (s, e) => StartResize(ZAnchorPosition.PipeWidth, e);
            PipeWidthAnchor.MouseMove += OnAnchorMouseMove;
            PipeWidthAnchor.MouseUp += OnAnchorMouseUp;

            // 旋转锚点
            RotateAnchor.MouseEnter += OnRotateAnchorMouseEnter;
            RotateAnchor.MouseLeave += OnRotateAnchorMouseLeave;
            RotateAnchor.MouseDown += OnRotateAnchorMouseDown;
            RotateAnchor.MouseMove += OnRotateAnchorMouseMove;
            RotateAnchor.MouseUp += OnRotateAnchorMouseUp;
        }

        private void OnAnchorMouseEnter(object sender, MouseEventArgs e)
        {
            if (sender is Ellipse anchor && !_isRotating)
            {
                anchor.Opacity = 1.0;
            }
        }

        private void OnAnchorMouseLeave(object sender, MouseEventArgs e)
        {
            if (sender is Ellipse anchor && !_isResizing && !_isRotating)
            {
                anchor.Opacity = 0.6;
            }
        }

        private void OnRotateAnchorMouseEnter(object sender, MouseEventArgs e)
        {
            if (!_isRotating)
            {
                RotateAnchor.Opacity = 1.0;
                RotateAnchorLine.Opacity = 1.0;
            }
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

        #region 拖拽移动

        private void OnControlMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left && !_isResizing && !_isRotating)
            {
                var clickedElement = e.OriginalSource as FrameworkElement;
                if (clickedElement != null &&
                    (clickedElement.Name == "TopLengthAnchor" ||
                     clickedElement.Name == "BottomLengthAnchor" ||
                     clickedElement.Name == "VerticalOffsetAnchor" ||
                     clickedElement.Name == "PipeWidthAnchor" ||
                     clickedElement.Name == "RotateAnchor"))
                {
                    return;
                }

                // ✅ 新增：通知画布管道被选中
                PipeSelected?.Invoke(this, this.PipeId);

                // ✅ 设置选中状态
                this.IsSelected = true;

                _isDraggingPipe = true;
                _dragStartPoint = e.GetPosition(this.Parent as UIElement);
                _originalPosition = new Point(Canvas.GetLeft(this), Canvas.GetTop(this));
                if (double.IsNaN(_originalPosition.X)) _originalPosition.X = 0;
                if (double.IsNaN(_originalPosition.Y)) _originalPosition.Y = 0;

                this.CaptureMouse();
                this.Opacity = 0.7;
                e.Handled = true;

                PipeDragStarted?.Invoke(this, new ZPipeDragEventArgs
                {
                    PipeId = this.PipeId,
                    StartPosition = _originalPosition,
                    CurrentPosition = _originalPosition
                });
            }
        }

        private void OnControlMouseMove(object sender, MouseEventArgs e)
        {
            if (_isDraggingPipe && e.LeftButton == MouseButtonState.Pressed)
            {
                Point currentPoint = e.GetPosition(this.Parent as UIElement);
                double deltaX = currentPoint.X - _dragStartPoint.X;
                double deltaY = currentPoint.Y - _dragStartPoint.Y;

                double newLeft = _originalPosition.X + deltaX;
                double newTop = _originalPosition.Y + deltaY;

                Canvas.SetLeft(this, newLeft);
                Canvas.SetTop(this, newTop);

                PipeDragging?.Invoke(this, new ZPipeDragEventArgs
                {
                    PipeId = this.PipeId,
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
                    PipeId = this.PipeId,
                    StartPosition = _originalPosition,
                    CurrentPosition = finalPosition,
                    DeltaX = finalPosition.X - _originalPosition.X,
                    DeltaY = finalPosition.Y - _originalPosition.Y
                });

                this.Opacity = 1.0;
                _isDraggingPipe = false;
                this.ReleaseMouseCapture();
                e.Handled = true;
            }
        }

        private void OnControlMouseLeave(object sender, MouseEventArgs e)
        {
            // 如果鼠标离开控件但仍在拖拽,不做处理
        }

        #endregion

        #region 调整大小

        private void StartResize(ZAnchorPosition anchor, MouseButtonEventArgs e)
        {
            _isResizing = true;
            _resizingAnchor = anchor;
            _dragStartPoint = e.GetPosition(this.Parent as UIElement);
            _originalSize = new ZPipeSize
            {
                TopHorizontalLength = this.TopHorizontalLength,
                BottomHorizontalLength = this.BottomHorizontalLength,
                VerticalOffset = this.VerticalOffset,
                PipeWidth = this.PipeWidth
            };

            (e.OriginalSource as UIElement)?.CaptureMouse();
            e.Handled = true;

            PipeResizeStarted?.Invoke(this, new ZPipeResizeEventArgs
            {
                PipeId = this.PipeId,
                AnchorPosition = anchor,
                OriginalSize = _originalSize,
                CurrentSize = _originalSize
            });
        }

        private void OnAnchorMouseMove(object sender, MouseEventArgs e)
        {
            if (_isResizing && e.LeftButton == MouseButtonState.Pressed && _resizingAnchor.HasValue)
            {
                Point currentPoint = e.GetPosition(this.Parent as UIElement);
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

                var currentSize = new ZPipeSize
                {
                    TopHorizontalLength = this.TopHorizontalLength,
                    BottomHorizontalLength = this.BottomHorizontalLength,
                    VerticalOffset = this.VerticalOffset,
                    PipeWidth = this.PipeWidth
                };

                PipeResizing?.Invoke(this, new ZPipeResizeEventArgs
                {
                    PipeId = this.PipeId,
                    AnchorPosition = _resizingAnchor.Value,
                    OriginalSize = _originalSize,
                    CurrentSize = currentSize
                });

                e.Handled = true;
            }
        }

        private void OnAnchorMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (_isResizing && e.ChangedButton == MouseButton.Left)
            {
                var finalSize = new ZPipeSize
                {
                    TopHorizontalLength = this.TopHorizontalLength,
                    BottomHorizontalLength = this.BottomHorizontalLength,
                    VerticalOffset = this.VerticalOffset,
                    PipeWidth = this.PipeWidth
                };

                if (_resizingAnchor.HasValue)
                {
                    PipeResizeCompleted?.Invoke(this, new ZPipeResizeEventArgs
                    {
                        PipeId = this.PipeId,
                        AnchorPosition = _resizingAnchor.Value,
                        OriginalSize = _originalSize,
                        CurrentSize = finalSize
                    });
                }

                _isResizing = false;
                _resizingAnchor = null;
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
                _dragStartPoint = e.GetPosition(this.Parent as UIElement);
                _originalRotation = this.RotationAngle;

                RotateAnchor.CaptureMouse();
                RotationLabel.Visibility = Visibility.Visible;
                e.Handled = true;

                PipeRotateStarted?.Invoke(this, new ZPipeRotateEventArgs
                {
                    PipeId = this.PipeId,
                    OriginalAngle = _originalRotation,
                    CurrentAngle = _originalRotation
                });
            }
        }

        private void OnRotateAnchorMouseMove(object sender, MouseEventArgs e)
        {
            if (_isRotating && e.LeftButton == MouseButtonState.Pressed)
            {
                Point currentPoint = e.GetPosition(this.Parent as UIElement);

                var pipeLeft = Canvas.GetLeft(this);
                var pipeTop = Canvas.GetTop(this);
                if (double.IsNaN(pipeLeft)) pipeLeft = 0;
                if (double.IsNaN(pipeTop)) pipeTop = 0;

                Point center = new Point(
                    pipeLeft + this.ActualWidth / 2,
                    pipeTop + this.ActualHeight / 2
                );

                double startAngle = Math.Atan2(_dragStartPoint.Y - center.Y, _dragStartPoint.X - center.X) * 180 / Math.PI;
                double currentAngle = Math.Atan2(currentPoint.Y - center.Y, currentPoint.X - center.X) * 180 / Math.PI;
                double deltaAngle = currentAngle - startAngle;

                double newAngle = _originalRotation + deltaAngle;

                // 如果按住 Shift 键,以 15 度为步进
                if (Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift))
                {
                    newAngle = Math.Round(newAngle / 15) * 15;
                }

                RotationAngle = newAngle;
                RotationText.Text = $"{Math.Round(RotationAngle)}°";

                _rotationLabelTimer.Stop();
                _rotationLabelTimer.Start();

                PipeRotating?.Invoke(this, new ZPipeRotateEventArgs
                {
                    PipeId = this.PipeId,
                    OriginalAngle = _originalRotation,
                    CurrentAngle = this.RotationAngle
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
                    PipeId = this.PipeId,
                    OriginalAngle = _originalRotation,
                    CurrentAngle = this.RotationAngle
                });

                _isRotating = false;
                RotateAnchor.ReleaseMouseCapture();
                e.Handled = true;
            }
        }

        private void UpdateRotation()
        {
            if (_rotateTransform != null)
            {
                _rotateTransform.Angle = _rotationAngle;
            }
        }

        #endregion

        #region 几何更新

        private void UpdatePipeGeometry()
        {
            try
            {
                // ✅ 1. 尺寸常数定义 (与 Elbow/Straight/TShape 保持完全一致)
                // =======================================================================
                const double FLANGE_WIDTH = 2.54;
                const double FLANGE_OFFSET = 0.35;
                // 关键：法兰比管道宽出的总长度
                const double FLANGE_EXTENSION = 5.8;

                // 计算法兰尺寸
                double flangeHeight = _pipeWidth + FLANGE_EXTENSION;

                // 固定左上角法兰的垂直位置（基准）
                double topFlangeY = 16;

                // =======================================================================
                // 2. 更新左侧法兰 (Left Flange)
                // =======================================================================
                // 垂直居中对齐：在管道中心线上
                double leftFlangeTop = topFlangeY + (_pipeWidth - flangeHeight) / 2;

                Canvas.SetTop(LeftFlangeGroup, leftFlangeTop);
                LeftFlangeGroup.Width = FLANGE_WIDTH;
                LeftFlangeGroup.Height = flangeHeight;

                LeftFlangeBase.Width = FLANGE_WIDTH;
                LeftFlangeBase.Height = flangeHeight;
                LeftFlangeGradient.Width = FLANGE_WIDTH;
                LeftFlangeGradient.Height = flangeHeight;
                LeftFlangeBorder.Width = FLANGE_WIDTH;
                LeftFlangeBorder.Height = flangeHeight;

                // =======================================================================
                // 3. 更新上方水平管道
                // =======================================================================
                // 左侧起点 = 间隙 + 法兰宽 + 间距
                double topPipeLeft = FLANGE_OFFSET + FLANGE_WIDTH + FlangeSpacing;

                // 上管道Y坐标
                double topPipeTop = topFlangeY;
                double topPipeWidth = _topHorizontalLength;
                double topPipeHeight = _pipeWidth;

                TopHorizontalPipe.Width = topPipeWidth;
                TopHorizontalPipe.Height = topPipeHeight;
                Canvas.SetLeft(TopHorizontalPipe, topPipeLeft);
                Canvas.SetTop(TopHorizontalPipe, topPipeTop);

                // 边缘线微调
                double edgeOffset = 0.38;
                TopHorizontalTopEdge.X1 = topPipeLeft;
                TopHorizontalTopEdge.Y1 = topPipeTop + 0.32;
                TopHorizontalTopEdge.X2 = topPipeLeft + topPipeWidth;
                TopHorizontalTopEdge.Y2 = topPipeTop + 0.32;

                TopHorizontalBottomEdge.X1 = topPipeLeft;
                TopHorizontalBottomEdge.Y1 = topPipeTop + topPipeHeight - 0.32;
                TopHorizontalBottomEdge.X2 = topPipeLeft + topPipeWidth;
                TopHorizontalBottomEdge.Y2 = topPipeTop + topPipeHeight - 0.32;

                // =======================================================================
                // 4. 第一个弯头 (Top Elbow)
                // =======================================================================
                double elbow1Radius = _pipeWidth;
                double elbow1CenterX = topPipeLeft + topPipeWidth + elbow1Radius;
                double elbow1CenterY = topPipeTop + _pipeWidth / 2;

                UpdateTopElbow(elbow1CenterX, elbow1CenterY, elbow1Radius);

                // =======================================================================
                // 5. 中间垂直管道 (Diagonal/Vertical Pipe)
                // =======================================================================
                double diagPipeLeft = elbow1CenterX - _pipeWidth / 2;
                double diagPipeTop = elbow1CenterY + elbow1Radius;
                double diagPipeHeight = _verticalOffset;

                DiagonalPipe.Width = _pipeWidth;
                DiagonalPipe.Height = diagPipeHeight;
                Canvas.SetLeft(DiagonalPipe, diagPipeLeft);
                Canvas.SetTop(DiagonalPipe, diagPipeTop);

                DiagonalLeftEdge.X1 = diagPipeLeft + 0.32;
                DiagonalLeftEdge.Y1 = diagPipeTop;
                DiagonalLeftEdge.X2 = diagPipeLeft + 0.32;
                DiagonalLeftEdge.Y2 = diagPipeTop + diagPipeHeight;

                DiagonalRightEdge.X1 = diagPipeLeft + _pipeWidth - 0.32;
                DiagonalRightEdge.Y1 = diagPipeTop;
                DiagonalRightEdge.X2 = diagPipeLeft + _pipeWidth - 0.32;
                DiagonalRightEdge.Y2 = diagPipeTop + diagPipeHeight;

                // =======================================================================
                // 6. 第二个弯头 (Bottom Elbow)
                // =======================================================================
                double elbow2Radius = _pipeWidth;
                double elbow2CenterX = diagPipeLeft + _pipeWidth / 2;
                double elbow2CenterY = diagPipeTop + diagPipeHeight + elbow2Radius;

                UpdateBottomElbow(elbow2CenterX, elbow2CenterY, elbow2Radius);

                // =======================================================================
                // 7. 下方水平管道
                // =======================================================================
                double bottomPipeLeft = elbow2CenterX + elbow2Radius;
                double bottomPipeTop = elbow2CenterY - _pipeWidth / 2;
                double bottomPipeWidth = _bottomHorizontalLength;

                BottomHorizontalPipe.Width = bottomPipeWidth;
                BottomHorizontalPipe.Height = _pipeWidth;
                Canvas.SetLeft(BottomHorizontalPipe, bottomPipeLeft);
                Canvas.SetTop(BottomHorizontalPipe, bottomPipeTop);

                BottomHorizontalTopEdge.X1 = bottomPipeLeft;
                BottomHorizontalTopEdge.Y1 = bottomPipeTop + 0.32;
                BottomHorizontalTopEdge.X2 = bottomPipeLeft + bottomPipeWidth;
                BottomHorizontalTopEdge.Y2 = bottomPipeTop + 0.32;

                BottomHorizontalBottomEdge.X1 = bottomPipeLeft;
                BottomHorizontalBottomEdge.Y1 = bottomPipeTop + _pipeWidth - 0.32;
                BottomHorizontalBottomEdge.X2 = bottomPipeLeft + bottomPipeWidth;
                BottomHorizontalBottomEdge.Y2 = bottomPipeTop + _pipeWidth - 0.32;

                // =======================================================================
                // 8. 更新右侧法兰 (Right Flange)
                // =======================================================================
                double rightFlangeX = bottomPipeLeft + bottomPipeWidth;
                // 垂直居中对齐
                double rightFlangeY = bottomPipeTop + (_pipeWidth - flangeHeight) / 2;

                Canvas.SetLeft(RightFlangeGroup, rightFlangeX);
                Canvas.SetTop(RightFlangeGroup, rightFlangeY);
                RightFlangeGroup.Width = FLANGE_WIDTH;
                RightFlangeGroup.Height = flangeHeight;

                RightFlangeBase.Width = FLANGE_WIDTH;
                RightFlangeBase.Height = flangeHeight;
                RightFlangeGradient.Width = FLANGE_WIDTH;
                RightFlangeGradient.Height = flangeHeight;
                RightFlangeBorder.Width = FLANGE_WIDTH;
                RightFlangeBorder.Height = flangeHeight;

                // =======================================================================
                // 9. 更新连接处
                // =======================================================================
                // 左连接：紧贴左法兰右侧
                Canvas.SetLeft(LeftConnection, FLANGE_OFFSET + FLANGE_WIDTH);
                Canvas.SetTop(LeftConnection, topPipeTop);
                LeftConnection.Height = topPipeHeight;

                // 右连接：紧贴右法兰左侧
                Canvas.SetLeft(RightConnection, rightFlangeX - 2); // 减去连接处宽度2
                Canvas.SetTop(RightConnection, bottomPipeTop);
                RightConnection.Height = _pipeWidth;

                // =======================================================================
                // 10. 更新杂项 (锚点、尺寸、选中框)
                // =======================================================================
                UpdateAnchorPositions(topPipeLeft, topPipeTop, rightFlangeX, rightFlangeY, diagPipeLeft, diagPipeTop, diagPipeHeight);

                double totalWidth = rightFlangeX + FLANGE_WIDTH + 4;
                double totalHeight = Math.Max(rightFlangeY + flangeHeight, leftFlangeTop + flangeHeight) + 4;
                this.Width = totalWidth;
                this.Height = totalHeight;

                SelectionBorder.Width = totalWidth + 4;
                SelectionBorder.Height = totalHeight + 4;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"UpdatePipeGeometry error: {ex.Message}");
            }
        }

        private void UpdateTopElbow(double centerX, double centerY, double radius)
        {
            PathGeometry elbowGeometry = new PathGeometry();
            PathFigure figure = new PathFigure();

            Point outerStart = new Point(centerX - radius, centerY - _pipeWidth / 2);
            figure.StartPoint = outerStart;

            ArcSegment outerArc = new ArcSegment(
                new Point(centerX + _pipeWidth / 2, centerY + radius),
                new Size(radius + _pipeWidth / 2, radius + _pipeWidth / 2),
                0, false, SweepDirection.Clockwise, true);
            figure.Segments.Add(outerArc);

            LineSegment toInner = new LineSegment(
                new Point(centerX - _pipeWidth / 2, centerY + radius), true);
            figure.Segments.Add(toInner);

            ArcSegment innerArc = new ArcSegment(
                new Point(centerX - radius, centerY + _pipeWidth / 2),
                new Size(radius - _pipeWidth / 2, radius - _pipeWidth / 2),
                0, false, SweepDirection.Counterclockwise, true);
            figure.Segments.Add(innerArc);

            figure.IsClosed = true;
            elbowGeometry.Figures.Add(figure);
            TopElbowPath.Data = elbowGeometry;

            UpdateTopElbowHighlight(centerX, centerY, radius);
        }

        private void UpdateTopElbowHighlight(double centerX, double centerY, double radius)
        {
            PathGeometry highlightGeometry = new PathGeometry();
            PathFigure highlightFigure = new PathFigure();

            double highlightRadius = radius - _pipeWidth / 4;
            Point highlightStart = new Point(centerX - highlightRadius, centerY + _pipeWidth / 4);
            highlightFigure.StartPoint = highlightStart;

            ArcSegment highlightArc = new ArcSegment(
                new Point(centerX + _pipeWidth / 4, centerY + highlightRadius),
                new Size(highlightRadius, highlightRadius),
                0, false, SweepDirection.Clockwise, true);
            highlightFigure.Segments.Add(highlightArc);

            highlightGeometry.Figures.Add(highlightFigure);
            TopElbowHighlight.Data = highlightGeometry;
        }

        private void UpdateBottomElbow(double centerX, double centerY, double radius)
        {
            PathGeometry elbowGeometry = new PathGeometry();
            PathFigure figure = new PathFigure();

            Point outerStart = new Point(centerX - _pipeWidth / 2, centerY - radius);
            figure.StartPoint = outerStart;

            ArcSegment outerArc = new ArcSegment(
                new Point(centerX + radius, centerY + _pipeWidth / 2),
                new Size(radius + _pipeWidth / 2, radius + _pipeWidth / 2),
                0, false, SweepDirection.Counterclockwise, true);
            figure.Segments.Add(outerArc);

            LineSegment toInner = new LineSegment(
                new Point(centerX + radius, centerY - _pipeWidth / 2), true);
            figure.Segments.Add(toInner);

            ArcSegment innerArc = new ArcSegment(
                new Point(centerX + _pipeWidth / 2, centerY - radius),
                new Size(radius - _pipeWidth / 2, radius - _pipeWidth / 2),
                0, false, SweepDirection.Clockwise, true);
            figure.Segments.Add(innerArc);

            figure.IsClosed = true;
            elbowGeometry.Figures.Add(figure);
            BottomElbowPath.Data = elbowGeometry;

            UpdateBottomElbowHighlight(centerX, centerY, radius);
        }

        private void UpdateBottomElbowHighlight(double centerX, double centerY, double radius)
        {
            PathGeometry highlightGeometry = new PathGeometry();
            PathFigure highlightFigure = new PathFigure();

            double highlightRadius = radius - _pipeWidth / 4;
            Point highlightStart = new Point(centerX + _pipeWidth / 4, centerY - highlightRadius);
            highlightFigure.StartPoint = highlightStart;

            ArcSegment highlightArc = new ArcSegment(
                new Point(centerX + highlightRadius, centerY + _pipeWidth / 4),
                new Size(highlightRadius, highlightRadius),
                0, false, SweepDirection.Counterclockwise, true);
            highlightFigure.Segments.Add(highlightArc);

            highlightGeometry.Figures.Add(highlightFigure);
            BottomElbowHighlight.Data = highlightGeometry;
        }

        private void UpdateAnchorPositions(double topPipeLeft, double topPipeTop, double rightFlangeX, double rightFlangeY, double diagPipeLeft, double diagPipeTop, double diagPipeHeight)
        {
            // 上方水平长度锚点
            Canvas.SetLeft(TopLengthAnchor, -6);
            Canvas.SetTop(TopLengthAnchor, topPipeTop + _pipeWidth / 2 - 6);

            // 下方水平长度锚点
            Canvas.SetLeft(BottomLengthAnchor, rightFlangeX + 6);
            Canvas.SetTop(BottomLengthAnchor, rightFlangeY + 14 - 6);

            // 垂直偏移锚点
            Canvas.SetLeft(VerticalOffsetAnchor, diagPipeLeft + _pipeWidth + 4);
            Canvas.SetTop(VerticalOffsetAnchor, diagPipeTop + diagPipeHeight / 2 - 6);

            // 管道宽度锚点
            Canvas.SetLeft(PipeWidthAnchor, topPipeLeft + _topHorizontalLength / 2 - 6);
            Canvas.SetTop(PipeWidthAnchor, topPipeTop + _pipeWidth + 4);

            // 旋转锚点
            Canvas.SetLeft(RotateAnchor, this.Width / 2 - 6);
            Canvas.SetTop(RotateAnchor, -30);

            RotateAnchorLine.X1 = this.Width / 2;
            RotateAnchorLine.Y1 = 0;
            RotateAnchorLine.X2 = this.Width / 2;
            RotateAnchorLine.Y2 = -24;

            // 旋转标签
            Canvas.SetLeft(RotationLabel, this.Width / 2 - 20);
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
                TopHorizontalLength = this.TopHorizontalLength,
                BottomHorizontalLength = this.BottomHorizontalLength,
                VerticalOffset = this.VerticalOffset,
                PipeWidth = this.PipeWidth
            };
        }

        public void SetSize(ZPipeSize size)
        {
            this.TopHorizontalLength = size.TopHorizontalLength;
            this.BottomHorizontalLength = size.BottomHorizontalLength;
            this.VerticalOffset = size.VerticalOffset;
            this.PipeWidth = size.PipeWidth;
        }

        public void SetSelected(bool selected)
        {
            IsSelected = selected;
        }

        #endregion

        #region IPipeSnapPoints 实现

        /// <summary>
        /// 获取Z形管道的两个法兰吸附点
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

            Point center = new Point(
                pipeLeft + this.ActualWidth / 2,
                pipeTop + this.ActualHeight / 2
            );

            double radians = _rotationAngle * Math.PI / 180.0;

            // ✅ 左侧法兰中心位置：Canvas.Left=2, 法兰宽度=4, 中心=2+2=4
            double leftFlangeX = 4 - this.ActualWidth / 2;  // 相对于中心
            double leftFlangeY = Canvas.GetTop(LeftFlangeGroup) + LeftFlangeGroup.Height / 2 - this.ActualHeight / 2;

            Point leftTopLocal = new Point(leftFlangeX, leftFlangeY);
            Point leftTopWorld = RotatePoint(leftTopLocal, radians);

            snapPoints.Add(new PipeSnapPoint
            {
                WorldPosition = new Point(
                    center.X + leftTopWorld.X,
                    center.Y + leftTopWorld.Y
                ),
                Direction = GetSnapDirectionFromAngle(radians + Math.PI), // 向左
                Description = $"Z形管道左上法兰 (上段:{_topHorizontalLength:F0})"
            });

            // ✅ 右侧法兰中心位置：Canvas.Left + 法兰宽度/2
            double rightFlangeX = Canvas.GetLeft(RightFlangeGroup) + 2 - this.ActualWidth / 2;  // 法兰宽度4/2=2
            double rightFlangeY = Canvas.GetTop(RightFlangeGroup) + RightFlangeGroup.Height / 2 - this.ActualHeight / 2;

            Point rightBottomLocal = new Point(rightFlangeX, rightFlangeY);
            Point rightBottomWorld = RotatePoint(rightBottomLocal, radians);

            snapPoints.Add(new PipeSnapPoint
            {
                WorldPosition = new Point(
                    center.X + rightBottomWorld.X,
                    center.Y + rightBottomWorld.Y
                ),
                Direction = GetSnapDirectionFromAngle(radians), // 向右
                Description = $"Z形管道右下法兰 (下段:{_bottomHorizontalLength:F0})"
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

    #region 辅助类和枚举

    /// <summary>
    /// 锚点位置枚举
    /// </summary>
    public enum ZAnchorPosition
    {
        TopLength,
        BottomLength,
        VerticalOffset,
        PipeWidth
    }

    /// <summary>
    /// Z形管道尺寸
    /// </summary>
    public class ZPipeSize
    {
        public double TopHorizontalLength { get; set; }
        public double BottomHorizontalLength { get; set; }
        public double VerticalOffset { get; set; }
        public double PipeWidth { get; set; }
    }

    /// <summary>
    /// 拖拽事件参数
    /// </summary>
    public class ZPipeDragEventArgs : EventArgs
    {
        public string PipeId { get; set; }
        public Point StartPosition { get; set; }
        public Point CurrentPosition { get; set; }
        public double DeltaX { get; set; }
        public double DeltaY { get; set; }
    }

    /// <summary>
    /// 调整大小事件参数
    /// </summary>
    public class ZPipeResizeEventArgs : EventArgs
    {
        public string PipeId { get; set; }
        public ZAnchorPosition AnchorPosition { get; set; }
        public ZPipeSize OriginalSize { get; set; }
        public ZPipeSize CurrentSize { get; set; }
    }

    /// <summary>
    /// 旋转事件参数
    /// </summary>
    public class ZPipeRotateEventArgs : EventArgs
    {
        public string PipeId { get; set; }
        public double OriginalAngle { get; set; }
        public double CurrentAngle { get; set; }
    }

    #endregion
}
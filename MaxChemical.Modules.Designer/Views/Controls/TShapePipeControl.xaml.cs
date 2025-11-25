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
    /// 三角通（T型管道）控件
    /// </summary>
    public partial class TShapePipeControl : UserControl, INotifyPropertyChanged, IPipeSnapPoints
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

        #endregion

        #region 字段

        private bool _isDraggingPipe = false;
        private bool _isResizing = false;
        private bool _isRotating = false;
        private TAnchorPosition? _resizingAnchor = null;
        private Point _dragStartPoint;
        private Point _originalPosition;
        private TPipeSize _originalSize;
        private double _originalRotation = 0;

        // 适合小型绘图的尺寸范围
        private const double MinMainLength = 30;      // 主管道最小长度（原60）
        private const double MaxMainLength = 300;     // 保持不变
        private const double MinBranchLength = 15;    // 支管最小长度（原30）
        private const double MaxBranchLength = 150;   // 保持不变
        private const double MinPipeWidth = 4;        // 管道最小宽度（原8）
        private const double MaxPipeWidth = 40;       // 保持不变

        private double _mainPipeLength = 120;
        private double _branchPipeLength = 54;
        private double _branchPosition = 60; // 支管在主管上的位置
        private double _pipeWidth = 20;
        private double _rotationAngle = 0;

        private RotateTransform _rotateTransform;
        private TransformGroup _transformGroup;
        private DispatcherTimer _rotationLabelTimer;

        private const double FlangeWidth = 2.54;
        private const double FlangeOffset = 0.35;
        private const double FlangeSpacing = 2.0;

        #endregion

        #region IsSelected 依赖属性

        public static readonly DependencyProperty IsSelectedProperty =
            DependencyProperty.Register(
                nameof(IsSelected),
                typeof(bool),
                typeof(TShapePipeControl),
                new PropertyMetadata(false, OnIsSelectedChanged));

        public bool IsSelected
        {
            get => (bool)GetValue(IsSelectedProperty);
            set => SetValue(IsSelectedProperty, value);
        }

        private static void OnIsSelectedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is TShapePipeControl control)
            {
                control.UpdateSelectionVisual((bool)e.NewValue);
                control.OnPropertyChanged(nameof(IsSelected));
            }
        }

        #endregion

        #region 属性

        public string PipeId { get; set; }

        public double MainPipeLength
        {
            get => _mainPipeLength;
            set
            {
                var newValue = Math.Max(MinMainLength, Math.Min(MaxMainLength, value));
                if (Math.Abs(_mainPipeLength - newValue) > 0.01)
                {
                    _mainPipeLength = newValue;
                    // 确保支管位置不超出主管范围
                    if (_branchPosition > _mainPipeLength - _pipeWidth)
                    {
                        _branchPosition = _mainPipeLength / 2;
                    }
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
                var newValue = Math.Max(MinBranchLength, Math.Min(MaxBranchLength, value));
                if (Math.Abs(_branchPipeLength - newValue) > 0.01)
                {
                    _branchPipeLength = newValue;
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
                // 限制支管位置在主管范围内
                var minPos = _pipeWidth;
                var maxPos = _mainPipeLength - _pipeWidth;
                var newValue = Math.Max(minPos, Math.Min(maxPos, value));

                if (Math.Abs(_branchPosition - newValue) > 0.01)
                {
                    _branchPosition = newValue;
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

        public TShapePipeControl()
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

            // 性能优化
            RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
            RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);
            RenderOptions.SetCachingHint(this, CachingHint.Cache);
            RenderOptions.SetCacheInvalidationThresholdMinimum(this, 0.5);
            RenderOptions.SetCacheInvalidationThresholdMaximum(this, 2.0);

            CacheMode = new BitmapCache
            {
                RenderAtScale = 1.0,
                SnapsToDevicePixels = true,
                EnableClearType = false
            };

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
            // 主管道长度锚点
            MainLengthAnchor.MouseEnter += OnAnchorMouseEnter;
            MainLengthAnchor.MouseLeave += OnAnchorMouseLeave;
            MainLengthAnchor.MouseDown += (s, e) => StartResize(TAnchorPosition.MainLength, e);
            MainLengthAnchor.MouseMove += OnAnchorMouseMove;
            MainLengthAnchor.MouseUp += OnAnchorMouseUp;

            // 支管长度锚点
            BranchLengthAnchor.MouseEnter += OnAnchorMouseEnter;
            BranchLengthAnchor.MouseLeave += OnAnchorMouseLeave;
            BranchLengthAnchor.MouseDown += (s, e) => StartResize(TAnchorPosition.BranchLength, e);
            BranchLengthAnchor.MouseMove += OnAnchorMouseMove;
            BranchLengthAnchor.MouseUp += OnAnchorMouseUp;

            // 支管位置锚点
            BranchPositionAnchor.MouseEnter += OnAnchorMouseEnter;
            BranchPositionAnchor.MouseLeave += OnAnchorMouseLeave;
            BranchPositionAnchor.MouseDown += (s, e) => StartResize(TAnchorPosition.BranchPosition, e);
            BranchPositionAnchor.MouseMove += OnAnchorMouseMove;
            BranchPositionAnchor.MouseUp += OnAnchorMouseUp;

            // 管道宽度锚点
            PipeWidthAnchor.MouseEnter += OnAnchorMouseEnter;
            PipeWidthAnchor.MouseLeave += OnAnchorMouseLeave;
            PipeWidthAnchor.MouseDown += (s, e) => StartResize(TAnchorPosition.PipeWidth, e);
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
                    (clickedElement.Name == "MainLengthAnchor" ||
                     clickedElement.Name == "BranchLengthAnchor" ||
                     clickedElement.Name == "BranchPositionAnchor" ||
                     clickedElement.Name == "PipeWidthAnchor" ||
                     clickedElement.Name == "RotateAnchor"))
                {
                    return;
                }

                PipeSelected?.Invoke(this, this.PipeId);
                this.IsSelected = true;

                _isDraggingPipe = true;
                _dragStartPoint = e.GetPosition(this.Parent as UIElement);
                _originalPosition = new Point(Canvas.GetLeft(this), Canvas.GetTop(this));
                if (double.IsNaN(_originalPosition.X)) _originalPosition.X = 0;
                if (double.IsNaN(_originalPosition.Y)) _originalPosition.Y = 0;

                this.CaptureMouse();
                this.Opacity = 0.7;
                e.Handled = true;

                PipeDragStarted?.Invoke(this, new TPipeDragEventArgs
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

                PipeDragging?.Invoke(this, new TPipeDragEventArgs
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

                PipeDragCompleted?.Invoke(this, new TPipeDragEventArgs
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

        private void StartResize(TAnchorPosition anchor, MouseButtonEventArgs e)
        {
            _isResizing = true;
            _resizingAnchor = anchor;
            _dragStartPoint = e.GetPosition(this.Parent as UIElement);
            _originalSize = new TPipeSize
            {
                MainPipeLength = this.MainPipeLength,
                BranchPipeLength = this.BranchPipeLength,
                BranchPosition = this.BranchPosition,
                PipeWidth = this.PipeWidth
            };

            (e.OriginalSource as UIElement)?.CaptureMouse();
            e.Handled = true;

            PipeResizeStarted?.Invoke(this, new TPipeResizeEventArgs
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

                var currentSize = new TPipeSize
                {
                    MainPipeLength = this.MainPipeLength,
                    BranchPipeLength = this.BranchPipeLength,
                    BranchPosition = this.BranchPosition,
                    PipeWidth = this.PipeWidth
                };

                PipeResizing?.Invoke(this, new TPipeResizeEventArgs
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
                var finalSize = new TPipeSize
                {
                    MainPipeLength = this.MainPipeLength,
                    BranchPipeLength = this.BranchPipeLength,
                    BranchPosition = this.BranchPosition,
                    PipeWidth = this.PipeWidth
                };

                if (_resizingAnchor.HasValue)
                {
                    PipeResizeCompleted?.Invoke(this, new TPipeResizeEventArgs
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

                PipeRotateStarted?.Invoke(this, new TPipeRotateEventArgs
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

                if (Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift))
                {
                    newAngle = Math.Round(newAngle / 15) * 15;
                }

                RotationAngle = newAngle;
                RotationText.Text = $"{Math.Round(RotationAngle)}°";

                _rotationLabelTimer.Stop();
                _rotationLabelTimer.Start();

                PipeRotating?.Invoke(this, new TPipeRotateEventArgs
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
                PipeRotateCompleted?.Invoke(this, new TPipeRotateEventArgs
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
                // =======================================================================
                //  1. 尺寸常数定义 (与 StraightPipeControl 保持完全一致)
                // =======================================================================
                // 法兰宽度 (固定)
                const double FLANGE_WIDTH = 2.54;
                // 法兰左侧/上侧间隙
                const double FLANGE_OFFSET = 0.35;
                // 法兰比管道宽出的总长度 (StraightPipeControl 是 5.8)
                const double FLANGE_EXTENSION = 5.8;
                // 主管道中心线 Y 坐标
                const double MAIN_PIPE_CENTER_Y = 70;

                // =======================================================================
                //  2. 计算主水平管道 (Main Pipe)
                // =======================================================================
                // 主管道左侧起点 = 间隙 + 左法兰宽
                double mainPipeLeft = FLANGE_OFFSET + FLANGE_WIDTH;
                double mainPipeTop = MAIN_PIPE_CENTER_Y - _pipeWidth / 2;

                MainHorizontalPipe.Width = _mainPipeLength;
                MainHorizontalPipe.Height = _pipeWidth;
                Canvas.SetLeft(MainHorizontalPipe, mainPipeLeft);
                Canvas.SetTop(MainHorizontalPipe, mainPipeTop);

                // 主管道边缘线
                MainHorizontalTopEdge.X1 = mainPipeLeft;
                MainHorizontalTopEdge.Y1 = mainPipeTop + 0.32; // 微调线条位置
                MainHorizontalTopEdge.X2 = mainPipeLeft + _mainPipeLength;
                MainHorizontalTopEdge.Y2 = mainPipeTop + 0.32;

                MainHorizontalBottomEdge.X1 = mainPipeLeft;
                MainHorizontalBottomEdge.Y1 = mainPipeTop + _pipeWidth - 0.32;
                MainHorizontalBottomEdge.X2 = mainPipeLeft + _mainPipeLength;
                MainHorizontalBottomEdge.Y2 = mainPipeTop + _pipeWidth - 0.32;

                // =======================================================================
                //  3. 左侧法兰 (Left Flange)
                // =======================================================================
                double flangeHeight = _pipeWidth + FLANGE_EXTENSION; // 统一高度计算
                double sideFlangeTop = MAIN_PIPE_CENTER_Y - flangeHeight / 2;

                Canvas.SetLeft(LeftFlangeGroup, FLANGE_OFFSET);
                Canvas.SetTop(LeftFlangeGroup, sideFlangeTop);
                LeftFlangeGroup.Width = FLANGE_WIDTH;
                LeftFlangeGroup.Height = flangeHeight;

                LeftFlangeBase.Height = flangeHeight;
                LeftFlangeGradient.Height = flangeHeight;
                LeftFlangeBorder.Height = flangeHeight;

                // =======================================================================
                //  4. 右侧法兰 (Right Flange)
                // =======================================================================
                double rightFlangeX = mainPipeLeft + _mainPipeLength;

                Canvas.SetLeft(RightFlangeGroup, rightFlangeX);
                Canvas.SetTop(RightFlangeGroup, sideFlangeTop);
                RightFlangeGroup.Width = FLANGE_WIDTH;
                RightFlangeGroup.Height = flangeHeight;

                RightFlangeBase.Height = flangeHeight;
                RightFlangeGradient.Height = flangeHeight;
                RightFlangeBorder.Height = flangeHeight;

                // =======================================================================
                //  5. 支管垂直管道 (Branch Pipe)
                // =======================================================================
                double branchCenterX = mainPipeLeft + _branchPosition;
                double branchPipeLeft = branchCenterX - _pipeWidth / 2;

                // 支管底部与主管道顶部重合
                double branchPipeBottom = mainPipeTop;
                double branchPipeTop = branchPipeBottom - _branchPipeLength;

                BranchVerticalPipe.Width = _pipeWidth;
                BranchVerticalPipe.Height = _branchPipeLength;
                Canvas.SetLeft(BranchVerticalPipe, branchPipeLeft);
                Canvas.SetTop(BranchVerticalPipe, branchPipeTop);

                // 支管边缘线
                BranchLeftEdge.X1 = branchPipeLeft + 0.32;
                BranchLeftEdge.Y1 = branchPipeTop;
                BranchLeftEdge.X2 = branchPipeLeft + 0.32;
                BranchLeftEdge.Y2 = branchPipeBottom;

                BranchRightEdge.X1 = branchPipeLeft + _pipeWidth - 0.32;
                BranchRightEdge.Y1 = branchPipeTop;
                BranchRightEdge.X2 = branchPipeLeft + _pipeWidth - 0.32;
                BranchRightEdge.Y2 = branchPipeBottom;

                // =======================================================================
                //  6. 顶部法兰 (Top Flange) - 尺寸与侧面法兰逻辑对应
                // =======================================================================
                // 顶部法兰宽度 = 管道宽 + 延伸量 (与侧面法兰高度一致)
                double topFlangeWidth = _pipeWidth + FLANGE_EXTENSION;
                double topFlangeLeft = branchCenterX - topFlangeWidth / 2;
                double topFlangeTop = branchPipeTop - FLANGE_WIDTH; // 紧贴支管顶部

                Canvas.SetLeft(TopFlangeGroup, topFlangeLeft);
                Canvas.SetTop(TopFlangeGroup, topFlangeTop);
                TopFlangeGroup.Width = topFlangeWidth;
                TopFlangeGroup.Height = FLANGE_WIDTH;

                TopFlangeBase.Width = topFlangeWidth;
                TopFlangeGradient.Width = topFlangeWidth;
                TopFlangeBorder.Width = topFlangeWidth;

                // =======================================================================
                //  7. T型连接处 (Junction) - 视觉修复
                // =======================================================================
                double junctionSize = _pipeWidth - 2;
                Canvas.SetLeft(TJunction, branchCenterX - junctionSize / 2);
                Canvas.SetTop(TJunction, MAIN_PIPE_CENTER_Y - junctionSize / 2);
                TJunction.Width = junctionSize;
                TJunction.Height = junctionSize;

                // =======================================================================
                //  8. 更新控件总尺寸 & 选中框
                // =======================================================================
                // 宽度 = 右法兰X坐标 + 法兰宽 + 预留间隙
                double totalWidth = rightFlangeX + FLANGE_WIDTH + 4;
                // 高度 = 主管道底部Y + 延伸量的一半 + 预留间隙
                // 这里为了包裹住下方可能存在的锚点区域，给一些余量
                double totalHeight = (mainPipeTop + _pipeWidth) + (FLANGE_EXTENSION / 2) + 20;

                this.Width = Math.Max(totalWidth, 100); // 最小宽度防止消失
                this.Height = Math.Max(totalHeight, 100);

                SelectionBorder.Width = this.Width;
                SelectionBorder.Height = this.Height;

                // =======================================================================
                //  9. 更新锚点位置
                // =======================================================================
                UpdateAnchorPositions(
                    rightFlangeX, mainPipeTop,
                    branchPipeLeft, branchPipeTop,
                    branchCenterX, MAIN_PIPE_CENTER_Y);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"UpdatePipeGeometry error: {ex.Message}");
            }
        }

        private void UpdateAnchorPositions(
        double rightFlangeX, double mainPipeTop,
        double branchPipeLeft, double branchPipeTop,
        double branchCenterX, double mainPipeCenterY)  //  改用中心线参数
        {
            // 主管道长度锚点（右侧）
            Canvas.SetLeft(MainLengthAnchor, rightFlangeX + 8);
            Canvas.SetTop(MainLengthAnchor, mainPipeCenterY - 6);  //  使用中心线

            // 支管长度锚点（顶部）
            Canvas.SetLeft(BranchLengthAnchor, branchCenterX - 6);
            Canvas.SetTop(BranchLengthAnchor, branchPipeTop - 12);

            // 支管位置锚点（左侧，主管道上）
            Canvas.SetLeft(BranchPositionAnchor, branchCenterX - 26);
            Canvas.SetTop(BranchPositionAnchor, mainPipeCenterY - 6);  //  使用中心线

            // 管道宽度锚点（底部）
            Canvas.SetLeft(PipeWidthAnchor, branchCenterX - 6);
            Canvas.SetTop(PipeWidthAnchor, mainPipeTop + _pipeWidth + 8);

            // 旋转锚点（顶部中心）
            Canvas.SetLeft(RotateAnchor, this.Width / 2 - 6);
            Canvas.SetTop(RotateAnchor, -40);

            RotateAnchorLine.X1 = this.Width / 2;
            RotateAnchorLine.Y1 = 0;
            RotateAnchorLine.X2 = this.Width / 2;
            RotateAnchorLine.Y2 = -34;

            Canvas.SetLeft(RotationLabel, this.Width / 2 - 20);
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
                MainPipeLength = this.MainPipeLength,
                BranchPipeLength = this.BranchPipeLength,
                BranchPosition = this.BranchPosition,
                PipeWidth = this.PipeWidth
            };
        }

        public void SetSize(TPipeSize size)
        {
            this.MainPipeLength = size.MainPipeLength;
            this.BranchPipeLength = size.BranchPipeLength;
            this.BranchPosition = size.BranchPosition;
            this.PipeWidth = size.PipeWidth;
        }

        public void SetSelected(bool selected)
        {
            IsSelected = selected;
        }

        #endregion

        #region IPipeSnapPoints 实现

        /// <summary>
        /// ✅ 获取精确的 T型管吸附点
        /// </summary>
        public List<PipeSnapPoint> GetSnapPoints()
        {
            var snapPoints = new List<PipeSnapPoint>();

            var parent = this.Parent as Canvas;
            if (parent == null) return snapPoints;

            // 1. 获取控件在画布上的位置
            var pipeLeft = Canvas.GetLeft(this);
            var pipeTop = Canvas.GetTop(this);
            if (double.IsNaN(pipeLeft)) pipeLeft = 0;
            if (double.IsNaN(pipeTop)) pipeTop = 0;

            // 2. 几何常量 (必须与 UpdatePipeGeometry 保持完全一致)
            const double FLANGE_WIDTH = 2.54;
            const double FLANGE_OFFSET = 0.35;
            const double MAIN_PIPE_CENTER_Y = 70; // 主管道中心线固定 Y 坐标

            // 3. 计算控件中心点 (旋转的基准点)
            Point center = new Point(
                pipeLeft + this.Width / 2,
                pipeTop + this.Height / 2
            );

            double radians = _rotationAngle * Math.PI / 180.0;

            // =================================================================
            // 📍 左侧吸附点 (Left Snap)
            // =================================================================
            // 位置：左侧法兰的最左边缘中心
            // X = 偏移量 (即法兰的左边缘)
            // Y = 主管道中心线
            double leftSnapLocalX = FLANGE_OFFSET;
            double leftSnapLocalY = MAIN_PIPE_CENTER_Y;

            // 转换为相对于控件中心的坐标
            Point leftRelPoint = new Point(
                leftSnapLocalX - this.Width / 2,
                leftSnapLocalY - this.Height / 2
            );

            // 旋转并转换到世界坐标
            Point leftWorld = RotatePoint(leftRelPoint, radians);

            snapPoints.Add(new PipeSnapPoint
            {
                WorldPosition = new Point(center.X + leftWorld.X, center.Y + leftWorld.Y),
                Direction = GetSnapDirectionFromAngle(radians + Math.PI), // 方向向左 (180度) + 旋转
                Description = "T型左端口"
            });

            // =================================================================
            // 📍 右侧吸附点 (Right Snap)
            // =================================================================
            // 位置：右侧法兰的最右边缘中心
            // X = 偏移 + 左法兰宽 + 管道长 + 右法兰宽
            double rightSnapLocalX = FLANGE_OFFSET + FLANGE_WIDTH + _mainPipeLength + FLANGE_WIDTH;
            double rightSnapLocalY = MAIN_PIPE_CENTER_Y;

            // 转换为相对于控件中心的坐标
            Point rightRelPoint = new Point(
                rightSnapLocalX - this.Width / 2,
                rightSnapLocalY - this.Height / 2
            );

            Point rightWorld = RotatePoint(rightRelPoint, radians);

            snapPoints.Add(new PipeSnapPoint
            {
                WorldPosition = new Point(center.X + rightWorld.X, center.Y + rightWorld.Y),
                Direction = GetSnapDirectionFromAngle(radians), // 方向向右 (0度) + 旋转
                Description = "T型右端口"
            });

            // =================================================================
            // 📍 顶部吸附点 (Top/Branch Snap)
            // =================================================================
            // X = 偏移 + 左法兰宽 + 支管位置
            double branchCenterX = FLANGE_OFFSET + FLANGE_WIDTH + _branchPosition;

            // Y计算逻辑：
            // 主管道顶部 = 中心线 - 半径
            // 支管顶部 = 主管道顶部 - 支管长度
            // 法兰顶部边缘 = 支管顶部 - 法兰宽度 (注意：顶部法兰是横向的，高度由 FLANGE_WIDTH 决定)
            double mainPipeTopY = MAIN_PIPE_CENTER_Y - _pipeWidth / 2;
            double branchTopY = mainPipeTopY - _branchPipeLength;
            double topSnapLocalY = branchTopY - FLANGE_WIDTH; // 吸附点在法兰最上沿

            Point topRelPoint = new Point(
                branchCenterX - this.Width / 2,
                topSnapLocalY - this.Height / 2
            );

            Point topWorld = RotatePoint(topRelPoint, radians);

            snapPoints.Add(new PipeSnapPoint
            {
                WorldPosition = new Point(center.X + topWorld.X, center.Y + topWorld.Y),
                Direction = GetSnapDirectionFromAngle(radians - Math.PI / 2), // 方向向上 (-90度) + 旋转
                Description = "T型支管端口"
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

    public enum TAnchorPosition
    {
        MainLength,      // 主管道长度
        BranchLength,    // 支管长度
        BranchPosition,  // 支管位置
        PipeWidth        // 管道宽度
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
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
using MaxChemical.Modules.Designer.Service;

namespace MaxChemical.Modules.Designer.Views.Controls
{
    public partial class ValveControl : UserControl, IPipeSnapPoints
    {
        #region 依赖属性

        public static readonly DependencyProperty IsOpenProperty =
            DependencyProperty.Register(
                nameof(IsOpen),
                typeof(bool),
                typeof(ValveControl),
                new PropertyMetadata(false, OnIsOpenChanged));

        public bool IsOpen
        {
            get => (bool)GetValue(IsOpenProperty);
            set => SetValue(IsOpenProperty, value);
        }

        public static readonly DependencyProperty BodyColorProperty =
            DependencyProperty.Register(
                nameof(BodyColor),
                typeof(Brush),
                typeof(ValveControl),
                new PropertyMetadata(null));

        public Brush BodyColor
        {
            get => (Brush)GetValue(BodyColorProperty);
            set => SetValue(BodyColorProperty, value);
        }

        public static readonly DependencyProperty CenterColorProperty =
            DependencyProperty.Register(
                nameof(CenterColor),
                typeof(Brush),
                typeof(ValveControl),
                new PropertyMetadata(null));

        public Brush CenterColor
        {
            get => (Brush)GetValue(CenterColorProperty);
            set => SetValue(CenterColorProperty, value);
        }

        public static readonly DependencyProperty IsSelectedProperty =
            DependencyProperty.Register(
                nameof(IsSelected),
                typeof(bool),
                typeof(ValveControl),
                new PropertyMetadata(false, OnIsSelectedChanged));

        public bool IsSelected
        {
            get => (bool)GetValue(IsSelectedProperty);
            set => SetValue(IsSelectedProperty, value);
        }

        #endregion

        #region 事件定义

        public event EventHandler<ValveDragEventArgs> ValveDragStarted;
        public event EventHandler<ValveDragEventArgs> ValveDragging;
        public event EventHandler<ValveDragEventArgs> ValveDragCompleted;
        public event EventHandler<ValveRotateEventArgs> ValveRotateStarted;
        public event EventHandler<ValveRotateEventArgs> ValveRotating;
        public event EventHandler<ValveRotateEventArgs> ValveRotateCompleted;
        public event EventHandler<string> ValveSelected;
        public event EventHandler<string> ValveDeselected;

        #endregion

        #region 字段

        private bool _isDragging = false;
        private bool _isRotating = false;
        private Point _dragStartPoint;
        private Point _originalPosition;
        private double _originalRotation = 0;
        private double _rotationAngle = 0;

        private DateTime _mouseDownTime;
        private Point _mouseDownPosition;
        private const int ClickTimeThresholdMs = 200;
        private const double ClickDistanceThreshold = 5.0;

        private RotateTransform _rotateTransform;
        private TransformGroup _transformGroup;
        private DispatcherTimer _rotationLabelTimer;

        public string ValveId { get; set; }

        #endregion

        #region 构造函数

        public ValveControl()
        {
            InitializeComponent();

            ValveId = Guid.NewGuid().ToString();
            Debug.WriteLine($"ValveControl 构造: ValveId={ValveId}");

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

                this.MouseEnter += OnValveMouseEnter;
                this.MouseLeave += OnValveMouseLeave;
                SetupRotateAnchorEvents();
            }

            UpdateVisualState(false);

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

        #region 状态变化处理

        private static void OnIsSelectedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is ValveControl valve)
            {
                valve.UpdateSelectionVisual((bool)e.NewValue);
            }
        }

        private void UpdateSelectionVisual(bool isSelected)
        {
            var selBorder = this.FindName("SelectionBorder") as System.Windows.Shapes.Rectangle;
            if (selBorder != null)
                selBorder.Visibility = Visibility.Collapsed;

            if (isSelected) ShowAllAnchors();
            else if (!this.IsMouseOver && !_isRotating) HideAllAnchors();
        }
        private static void OnIsOpenChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is ValveControl valve)
            {
                bool isOpen = (bool)e.NewValue;
                valve.UpdateVisualState(isOpen);
            }
        }

        private void UpdateVisualState(bool isOpen)
        {
            double baseAngle = _rotationAngle % 360;
            if (baseAngle < 0) baseAngle += 360;

            double handleRelativeAngle = isOpen ? 90 : 0;
            double currentHandleAngle = HandleRotation.Angle;
            double delta = handleRelativeAngle - currentHandleAngle;

            while (delta > 180) delta -= 360;
            while (delta < -180) delta += 360;

            double animationTarget = currentHandleAngle + delta;

            var rotateAnimation = new DoubleAnimation
            {
                From = currentHandleAngle,
                To = animationTarget,
                Duration = TimeSpan.FromMilliseconds(300),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
            };

            HandleRotation.BeginAnimation(RotateTransform.AngleProperty, rotateAnimation);

            if (isOpen)
            {
                //  使用资源中定义的打开状态渐变
                BodyColor = (LinearGradientBrush)this.Resources["OpenBodyGradient"];
                CenterColor = (RadialGradientBrush)this.Resources["OpenCenterGradient"];

                Debug.WriteLine($" 阀门打开 | 角度:{_rotationAngle:F0}°");
            }
            else
            {
                //  使用资源中定义的关闭状态渐变
                BodyColor = (LinearGradientBrush)this.Resources["BodyGradient"];
                CenterColor = (RadialGradientBrush)this.Resources["CenterGradient"];

                Debug.WriteLine($" 阀门关闭 | 角度:{_rotationAngle:F0}°");
            }
        }

        #endregion

        #region 锚点显示控制

        private void OnValveMouseEnter(object sender, MouseEventArgs e)
        {
            if (!_isDragging && !_isRotating)
            {
                ShowAllAnchors();
            }
        }

        private void OnValveMouseLeave(object sender, MouseEventArgs e)
        {
            if (!_isRotating && !IsSelected)
            {
                HideAllAnchors();
            }
        }

        private void ShowAllAnchors()
        {
            RotateAnchor.Opacity = 0.7;
            RotateAnchorLine.Opacity = 0.7;
        }

        private void HideAllAnchors()
        {
            RotateAnchor.Opacity = 0;
            RotateAnchorLine.Opacity = 0;
        }

        #endregion

        #region 统一的交互覆盖层事件处理

        private void OnOverlayMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                if (IsClickOnRotateAnchor(e.OriginalSource))
                {
                    return;
                }

                _mouseDownTime = DateTime.Now;
                _mouseDownPosition = e.GetPosition(this);

                ValveSelected?.Invoke(this, this.ValveId);
                this.IsSelected = true;

                var parent = this.Parent as Canvas;
                if (parent != null)
                {
                    _dragStartPoint = e.GetPosition(parent);
                    _originalPosition = new Point(Canvas.GetLeft(this), Canvas.GetTop(this));
                    if (double.IsNaN(_originalPosition.X)) _originalPosition.X = 0;
                    if (double.IsNaN(_originalPosition.Y)) _originalPosition.Y = 0;
                }

                //  先捕获鼠标
                ((UIElement)sender).CaptureMouse();
                e.Handled = true;
            }
        }

        private void OnOverlayMouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed && !_isRotating)
            {
                var currentPosition = e.GetPosition(this);
                double deltaX = currentPosition.X - _mouseDownPosition.X;
                double deltaY = currentPosition.Y - _mouseDownPosition.Y;
                double distance = Math.Sqrt(deltaX * deltaX + deltaY * deltaY);

                if (!_isDragging && distance > ClickDistanceThreshold)
                {
                    Debug.WriteLine(" 超过阈值，开始拖拽模式");
                    _isDragging = true;

                    //  释放阀门的鼠标捕获
                    if (((UIElement)sender).IsMouseCaptured)
                    {
                        ((UIElement)sender).ReleaseMouseCapture();
                    }

                    //  查找并触发设备容器的拖拽
                    var deviceBorder = FindParentOfType<Border>(this);
                    if (deviceBorder != null && deviceBorder.Tag is string deviceId)
                    {
                        Debug.WriteLine($"将拖拽交给设备容器: {deviceId}");

                        //  让设备容器捕获鼠标
                        deviceBorder.CaptureMouse();

                        //  创建新的鼠标事件参数，传递给设备容器
                        var canvas = FindParentOfType<Canvas>(deviceBorder);
                        var canvasPosition = e.GetPosition(canvas);

                        var newArgs = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                        {
                            RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent
                        };

                        deviceBorder.RaiseEvent(newArgs);
                    }
                }

                //  不处理这个事件，因为如果是拖拽，已经释放了捕获
                // 如果是点击（未超过阈值），保持捕获
            }
        }


        private void OnOverlayMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Released)
            {
                var clickDuration = (DateTime.Now - _mouseDownTime).TotalMilliseconds;
                var currentPosition = e.GetPosition(this);
                var moveDistance = Math.Sqrt(
                    Math.Pow(currentPosition.X - _mouseDownPosition.X, 2) +
                    Math.Pow(currentPosition.Y - _mouseDownPosition.Y, 2));

                bool isClick = clickDuration < ClickTimeThresholdMs &&
                               moveDistance < ClickDistanceThreshold;

                if (isClick && !_isDragging)
                {
                    Debug.WriteLine(" 确认是点击 - 切换阀门状态");
                    IsOpen = !IsOpen;
                    ShowTemporarySelection();
                }

                this.Opacity = 1.0;
                _isDragging = false;

                if (((UIElement)sender).IsMouseCaptured)
                {
                    ((UIElement)sender).ReleaseMouseCapture();
                }

                e.Handled = true;
            }
        }


        /// <summary>
        ///  显示临时选中效果（自动消失）
        /// </summary>
        private void ShowTemporarySelection()
        {
            this.IsSelected = true;
            ValveSelected?.Invoke(this, this.ValveId);

            var autoHideTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(500)
            };

            autoHideTimer.Tick += (s, args) =>
            {
                this.IsSelected = false;
                ValveDeselected?.Invoke(this, this.ValveId);
                autoHideTimer.Stop();

                Debug.WriteLine(" 阀门选中效果已自动取消");
            };

            autoHideTimer.Start();
        }

        private void OnOverlayMouseLeave(object sender, MouseEventArgs e)
        {
            if (_isDragging)
            {
                CompleteDrag();
            }
        }

        private void CompleteDrag()
        {
            if (!_isDragging) return;

            var finalPosition = new Point(Canvas.GetLeft(this), Canvas.GetTop(this));
            ValveDragCompleted?.Invoke(this, new ValveDragEventArgs
            {
                ValveId = ValveId,
                StartPosition = _originalPosition,
                CurrentPosition = finalPosition
            });

            this.Opacity = 1.0;
            _isDragging = false;
            InteractionOverlay.ReleaseMouseCapture();
        }

        private bool IsClickOnRotateAnchor(object source)
        {
            return source == RotateAnchor;
        }

        #endregion

        #region 旋转功能

        private void SetupRotateAnchorEvents()
        {
            RotateAnchor.MouseEnter += OnRotateAnchorMouseEnter;
            RotateAnchor.MouseLeave += OnRotateAnchorMouseLeave;
            RotateAnchor.MouseDown += OnRotateAnchorMouseDown;
            RotateAnchor.MouseMove += OnRotateAnchorMouseMove;
            RotateAnchor.MouseUp += OnRotateAnchorMouseUp;
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
                RotateAnchor.Opacity = 0.7;
                RotateAnchorLine.Opacity = 0.7;
            }
        }

        private Point _rotateCenter;

        private void OnRotateAnchorMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                _isRotating = true;
                _originalRotation = _rotationAngle;

                var canvas = FindParentCanvas(this);
                var deviceBorder = FindParentOfType<Border>(this);

                if (canvas != null && deviceBorder != null)
                {
                    var borderLeft = Canvas.GetLeft(deviceBorder);
                    var borderTop = Canvas.GetTop(deviceBorder);
                    if (double.IsNaN(borderLeft)) borderLeft = 0;
                    if (double.IsNaN(borderTop)) borderTop = 0;

                    _rotateCenter = new Point(
                        borderLeft + deviceBorder.ActualWidth / 2,
                        borderTop + deviceBorder.ActualHeight / 2
                    );

                    _dragStartPoint = e.GetPosition(canvas);
                }

                ((UIElement)sender).CaptureMouse();

                ValveRotateStarted?.Invoke(this, new ValveRotateEventArgs
                {
                    ValveId = this.ValveId,
                    OriginalAngle = _originalRotation
                });

                ShowRotationLabel();
                e.Handled = true;
            }
        }

        private void OnRotateAnchorMouseMove(object sender, MouseEventArgs e)
        {
            if (_isRotating && e.LeftButton == MouseButtonState.Pressed)
            {
                HandleRotate(e);
                e.Handled = true;
            }
        }

        private void OnRotateAnchorMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (_isRotating)
            {
                CompleteRotate();
                e.Handled = true;
            }
        }

        private void HandleRotate(MouseEventArgs e)
        {
            var canvas = FindParentCanvas(this);
            if (canvas == null) return;

            var currentPoint = e.GetPosition(canvas);

            double startAngle = Math.Atan2(
                _dragStartPoint.Y - _rotateCenter.Y,
                _dragStartPoint.X - _rotateCenter.X
            ) * 180 / Math.PI;

            double currentAngle = Math.Atan2(
                currentPoint.Y - _rotateCenter.Y,
                currentPoint.X - _rotateCenter.X
            ) * 180 / Math.PI;

            double deltaAngle = currentAngle - startAngle;
            double newAngle = _originalRotation + deltaAngle;

            if (Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift))
            {
                newAngle = Math.Round(newAngle / 15) * 15;
            }

            newAngle = newAngle % 360;
            if (newAngle < 0) newAngle += 360;

            _rotationAngle = newAngle;
            UpdateRotation();

            ValveRotating?.Invoke(this, new ValveRotateEventArgs
            {
                ValveId = this.ValveId,
                CurrentAngle = _rotationAngle
            });
        }

        private T FindParentOfType<T>(DependencyObject child) where T : DependencyObject
        {
            var parent = VisualTreeHelper.GetParent(child);
            if (parent == null) return null;
            if (parent is T typedParent) return typedParent;
            return FindParentOfType<T>(parent);
        }

        private Canvas FindParentCanvas(DependencyObject child)
        {
            return FindParentOfType<Canvas>(child);
        }

        private void CompleteRotate()
        {
            if (!_isRotating) return;

            ValveRotateCompleted?.Invoke(this, new ValveRotateEventArgs
            {
                ValveId = this.ValveId,
                OriginalAngle = _originalRotation,
                CurrentAngle = _rotationAngle
            });

            _isRotating = false;
            ((UIElement)RotateAnchor).ReleaseMouseCapture();
        }

        public double RotationAngle
        {
            get => _rotationAngle;
            set
            {
                _rotationAngle = value % 360;
                if (_rotationAngle < 0) _rotationAngle += 360;  //  修正：使用 _rotationAngle
                UpdateRotation();
            }
        }

        private void UpdateRotation()
        {
            if (_rotateTransform != null)
            {
                _rotateTransform.Angle = _rotationAngle;
            }

            if (RotationText != null)
            {
                RotationText.Text = $"{_rotationAngle:F0}°";
            }
        }

        private void ShowRotationLabel()
        {
            RotationLabel.Visibility = Visibility.Visible;
            _rotationLabelTimer?.Stop();
            _rotationLabelTimer?.Start();
        }

        #endregion

        #region 公共方法

        public void ShowSelection()
        {
            IsSelected = true;
        }

        public void HideSelection()
        {
            IsSelected = false;
        }

        #endregion

        #region IPipeSnapPoints 实现

        public List<PipeSnapPoint> GetSnapPoints()
        {
            var snapPoints = new List<PipeSnapPoint>();

            var parent = this.Parent as Canvas;
            if (parent == null) return snapPoints;

            var valveLeft = Canvas.GetLeft(this);
            var valveTop = Canvas.GetTop(this);
            if (double.IsNaN(valveLeft)) valveLeft = 0;
            if (double.IsNaN(valveTop)) valveTop = 0;

            Point center = new Point(valveLeft + 100, valveTop + 100);
            double radians = _rotationAngle * Math.PI / 180.0;
            double radius = 50;

            AddSnapPoint(snapPoints, center, radians, 0, "右侧", radius);
            AddSnapPoint(snapPoints, center, radians, Math.PI / 2, "下侧", radius);
            AddSnapPoint(snapPoints, center, radians, Math.PI, "左侧", radius);
            AddSnapPoint(snapPoints, center, radians, Math.PI * 3 / 2, "上侧", radius);

            return snapPoints;
        }

        private void AddSnapPoint(List<PipeSnapPoint> snapPoints, Point center, double baseRotation, double angleOffset, string description, double radius)
        {
            double totalAngle = baseRotation + angleOffset;

            Point offset = new Point(
                radius * Math.Cos(totalAngle),
                radius * Math.Sin(totalAngle)
            );

            snapPoints.Add(new PipeSnapPoint
            {
                WorldPosition = new Point(center.X + offset.X, center.Y + offset.Y),
                Direction = GetSnapDirectionFromAngle(totalAngle),
                Description = $"阀门{description} (角度:{_rotationAngle:F0}°)"
            });
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

    public class ValveDragEventArgs : EventArgs
    {
        public string ValveId { get; set; }
        public Point StartPosition { get; set; }
        public Point CurrentPosition { get; set; }
    }

    public class ValveRotateEventArgs : EventArgs
    {
        public string ValveId { get; set; }
        public double OriginalAngle { get; set; }
        public double CurrentAngle { get; set; }
    }

    #endregion
}
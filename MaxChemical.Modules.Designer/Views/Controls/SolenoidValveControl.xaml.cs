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
    public partial class SolenoidValveControl : UserControl, IPipeSnapPoints
    {
        #region 依赖属性

        public static readonly DependencyProperty IsOpenProperty =
            DependencyProperty.Register(
                nameof(IsOpen),
                typeof(bool),
                typeof(SolenoidValveControl),
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
                typeof(SolenoidValveControl),
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
                typeof(SolenoidValveControl),
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
                typeof(SolenoidValveControl),
                new PropertyMetadata(false, OnIsSelectedChanged));

        public bool IsSelected
        {
            get => (bool)GetValue(IsSelectedProperty);
            set => SetValue(IsSelectedProperty, value);
        }
        // 新增：是否处于错误状态
        public static readonly DependencyProperty IsErrorProperty =
            DependencyProperty.Register(
                nameof(IsError),
                typeof(bool),
                typeof(SolenoidValveControl),
                new PropertyMetadata(false, OnIsErrorChanged));

        public bool IsError
        {
            get => (bool)GetValue(IsErrorProperty);
            set => SetValue(IsErrorProperty, value);
        }

        #endregion

        #region 事件定义
        //  新增：请求状态切换事件（控件 → 外部）
        public event EventHandler<ValveStateChangeRequestEventArgs> StateChangeRequested;
        public event EventHandler<string> ValveSelected;
        public event EventHandler<string> ValveDeselected;
        public event EventHandler<ValveRotateEventArgs> ValveRotateStarted;
        public event EventHandler<ValveRotateEventArgs> ValveRotating;
        public event EventHandler<ValveRotateEventArgs> ValveRotateCompleted;

        #endregion

        #region 字段

        private bool _isDragging = false;
        private bool _isRotating = false;
        private Point _dragStartPoint;
        private Point _rotateCenter;
        private double _originalRotation = 0;

        private DateTime _mouseDownTime;
        private Point _mouseDownPosition;
        private const int ClickTimeThresholdMs = 200;
        private const double ClickDistanceThreshold = 5.0;

        private double _rotationAngle = 0;
        private RotateTransform _rotateTransform;
        private TransformGroup _transformGroup;
        private DispatcherTimer _rotationLabelTimer;
        //  新增：错误状态动画
        private Storyboard _errorBlinkAnimation;
        //private DispatcherTimer _errorResetTimer;

        //  新增：加载状态动画
        private Storyboard _loadingAnimation;
        private bool _isLoading = false;

        //  新增：错误保持动画
        private Storyboard _errorStateHoldAnimation;

        public string ValveId { get; set; }

        #endregion

        #region 构造函数

        public SolenoidValveControl()
        {
            InitializeComponent();

            ValveId = Guid.NewGuid().ToString();
            Debug.WriteLine($"SolenoidValveControl 构造: ValveId={ValveId}");

            _rotateTransform = new RotateTransform(0);
            _transformGroup = new TransformGroup();
            _transformGroup.Children.Add(_rotateTransform);
            this.RenderTransform = _transformGroup;
            this.RenderTransformOrigin = new Point(0.5, 0.5);

            bool isInDesignMode = DesignerProperties.GetIsInDesignMode(this);

            if (!isInDesignMode)
            {
                // 旋转标签定时器（保留原有代码）
                _rotationLabelTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(2)
                };
                _rotationLabelTimer.Tick += (s, e) =>
                {
                    RotationLabel.Visibility = Visibility.Collapsed;
                    _rotationLabelTimer.Stop();
                };

                // ===== 注释掉动画资源初始化 =====
                /*
                //  新增：加载动画资源初始化
                _loadingAnimation = (Storyboard)this.Resources["LoadingAnimation"];
                _errorBlinkAnimation = (Storyboard)this.Resources["ErrorBlinkAnimation"];
                _errorStateHoldAnimation = (Storyboard)this.Resources["ErrorStateHoldAnimation"];

                //  新增：错误动画完成后自动切换到保持动画
                _errorBlinkAnimation.Completed += (s, e) =>
                {
                    if (IsError)
                    {
                        _errorStateHoldAnimation.Begin(this, true);
                        Debug.WriteLine($"⏱️ 错误闪烁完成，切换到持续脉冲 | ValveId:{ValveId}");
                    }
                };
                */

                this.MouseEnter += OnValveMouseEnter;
                this.MouseLeave += OnValveMouseLeave;
                SetupRotateAnchorEvents();

                UpdateVisualState(false);
            }

            // 硬件加速配置
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

        #region 状态变化处理

        private static void OnIsSelectedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is SolenoidValveControl valve)
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
            if (d is SolenoidValveControl valve)
            {
                bool isOpen = (bool)e.NewValue;
                valve.UpdateVisualState(isOpen);
            }
        }

        private void UpdateVisualState(bool isOpen)
        {
            // ===== 注释掉轮盘旋转动画，改为直接设置角度 =====
            // 原动画代码：
            /*
            double targetAngle = isOpen ? 0 : 90;

            var rotateAnimation = new DoubleAnimation
            {
                To = targetAngle,
                Duration = TimeSpan.FromMilliseconds(300),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
            };

            WheelRotation.BeginAnimation(RotateTransform.AngleProperty, rotateAnimation);
            SpokesRotation.BeginAnimation(RotateTransform.AngleProperty, rotateAnimation);
            */

            // 新代码：直接设置角度，无动画
            double targetAngle = isOpen ? 0 : 90;
            WheelRotation.Angle = targetAngle;
            SpokesRotation.Angle = targetAngle;

            // 保留颜色状态切换（无动画）
            if (isOpen)
            {
                BodyColor = (LinearGradientBrush)this.Resources["OpenBodyGradient"];
                CenterColor = (RadialGradientBrush)this.Resources["OpenCenterGradient"];
                Debug.WriteLine($"电磁阀打开 | ValveId:{ValveId}, 角度:{_rotationAngle:F0}°");
            }
            else
            {
                BodyColor = (LinearGradientBrush)this.Resources["ClosedBodyGradient"];
                CenterColor = (RadialGradientBrush)this.Resources["ClosedCenterGradient"];
                Debug.WriteLine($"电磁阀关闭 | ValveId:{ValveId}, 角度:{_rotationAngle:F0}°");
            }
        }

        #endregion

        #region 交互覆盖层事件处理

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

                    if (((UIElement)sender).IsMouseCaptured)
                    {
                        ((UIElement)sender).ReleaseMouseCapture();
                    }

                    var deviceBorder = FindParentOfType<Border>(this);
                    if (deviceBorder != null && deviceBorder.Tag is string deviceId)
                    {
                        Debug.WriteLine($"将拖拽交给设备容器: {deviceId}");
                        deviceBorder.CaptureMouse();

                        var newArgs = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                        {
                            RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent
                        };

                        deviceBorder.RaiseEvent(newArgs);
                    }
                }
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
                    Debug.WriteLine($" 电磁阀点击 | ValveId:{ValveId}, 当前状态:{IsOpen}");

                    //  显示加载动画
                    ShowLoading();

                    // 触发状态切换请求
                    var requestedState = !IsOpen;

                    StateChangeRequested?.Invoke(this, new ValveStateChangeRequestEventArgs
                    {
                        ValveId = this.ValveId,
                        CurrentState = IsOpen,
                        RequestedState = requestedState
                    });
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

        private void OnOverlayMouseLeave(object sender, MouseEventArgs e)
        {
            // 不处理，保持简洁
        }

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

                Debug.WriteLine(" 电磁阀选中效果已自动取消");
            };

            autoHideTimer.Start();
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

        #region 辅助方法

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

        #region 错误状态处理

        /// <summary>
        /// 错误状态变化回调
        /// </summary>
        private static void OnIsErrorChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is SolenoidValveControl valve)
            {
                bool isError = (bool)e.NewValue;
                valve.UpdateErrorState(isError);
            }
        }

        /// <summary>
        /// 更新错误状态视觉效果
        /// </summary>
        private void UpdateErrorState(bool isError)
        {
            if (isError)
            {
                Debug.WriteLine($"显示错误状态 | ValveId:{ValveId}");

                // ===== 注释掉动画，改为直接显示 =====
                /*
                // 停止所有动画
                _loadingAnimation?.Stop();
                _errorBlinkAnimation?.Stop();
                _errorStateHoldAnimation?.Stop();

                // 启动闪烁动画
                _errorBlinkAnimation.Begin(this, true);
                */

                // 新代码：直接显示错误覆盖层
                ErrorOverlay.Opacity = 0.4;
                ErrorCenterOverlay.Opacity = 0.4;
                ErrorWarningIcon.Opacity = 1.0;
            }
            else
            {
                Debug.WriteLine($"清除错误状态 | ValveId:{ValveId}");

                // ===== 注释掉动画，改为直接隐藏 =====
                /*
                // 停止所有动画
                _errorBlinkAnimation?.Stop();
                _errorStateHoldAnimation?.Stop();

                // 淡出动画
                var fadeOut = new DoubleAnimation
                {
                    From = ErrorOverlay.Opacity,
                    To = 0,
                    Duration = TimeSpan.FromMilliseconds(300)
                };

                ErrorOverlay.BeginAnimation(OpacityProperty, fadeOut);
                ErrorCenterOverlay.BeginAnimation(OpacityProperty, fadeOut);
                ErrorWarningIcon.BeginAnimation(OpacityProperty, fadeOut);
                */

                // 新代码：直接隐藏
                ErrorOverlay.Opacity = 0;
                ErrorCenterOverlay.Opacity = 0;
                ErrorWarningIcon.Opacity = 0;

                // 重置震动
                ValveShakeTransform.X = 0;
            }
        }

        /// <summary>
        ///  公共方法：显示操作失败动画
        /// </summary>
        public void ShowOperationFailure()
        {
            IsError = true;
        }

        /// <summary>
        ///  公共方法：清除错误状态
        /// </summary>
        public void ClearError()
        {
            IsError = false;
        }

        #endregion

        #region 加载状态处理

        /// <summary>
        ///  显示加载动画
        /// </summary>
        public void ShowLoading()
        {
            if (_isLoading) return;

            _isLoading = true;
            Debug.WriteLine($"显示加载状态 | ValveId:{ValveId}");

            // ===== 注释掉动画，改为直接显示覆盖层 =====
            /*
            // 停止错误动画
            if (IsError)
            {
                IsError = false;
            }

            // 启动加载动画
            _loadingAnimation.Begin(this, true);
            LoadingWheelContainer.Opacity = 1;
            */

            // 新代码：直接显示加载覆盖层，无动画
            LoadingOverlay.Opacity = 0.3;
            LoadingCenterOverlay.Opacity = 0.3;
            LoadingWheelContainer.Opacity = 1;
        }



        /// <summary>
        ///  隐藏加载动画
        /// </summary>
        public void HideLoading()
        {

            if (!_isLoading) return;

            _isLoading = false;
            Debug.WriteLine($"✅ 隐藏加载状态 | ValveId:{ValveId}");

            // ===== 注释掉动画，改为直接隐藏 =====
            /*
            // 停止动画
            _loadingAnimation?.Stop();

            // 淡出效果
            var fadeOut = new DoubleAnimation
            {
                From = LoadingOverlay.Opacity,
                To = 0,
                Duration = TimeSpan.FromMilliseconds(200)
            };

            var fadeOutCenter = new DoubleAnimation
            {
                From = LoadingCenterOverlay.Opacity,
                To = 0,
                Duration = TimeSpan.FromMilliseconds(200)
            };

            var fadeOutWheel = new DoubleAnimation
            {
                From = LoadingWheelContainer.Opacity,
                To = 0,
                Duration = TimeSpan.FromMilliseconds(200)
            };

            LoadingOverlay.BeginAnimation(OpacityProperty, fadeOut);
            LoadingCenterOverlay.BeginAnimation(OpacityProperty, fadeOutCenter);
            LoadingWheelContainer.BeginAnimation(OpacityProperty, fadeOutWheel);
            */

            // 新代码：直接隐藏
            LoadingOverlay.Opacity = 0;
            LoadingCenterOverlay.Opacity = 0;
            LoadingWheelContainer.Opacity = 0;

            // 重置旋转角度
            LoadingWheelRotation.Angle = 0;
        }


        /// <summary>
        ///  公共属性：是否正在加载
        /// </summary>
        public bool IsLoading
        {
            get => _isLoading;
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
                Description = $"电磁阀{description} (角度:{_rotationAngle:F0}°)"
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

        //  新增：状态切换请求事件参数
        public class ValveStateChangeRequestEventArgs : EventArgs
        {
            public string ValveId { get; set; }
            public bool CurrentState { get; set; }  // 当前状态
            public bool RequestedState { get; set; } // 请求的新状态
        }
    }

    
}
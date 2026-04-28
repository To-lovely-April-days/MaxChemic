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
using MaxChemical.Core;
using MaxChemical.Modules.Designer.Service;

namespace MaxChemical.Modules.Designer.Views.Controls
{
    public partial class MassFlowMeterControl : UserControl, IPipeSnapPoints
    {
        #region 依赖属性

        public static readonly DependencyProperty CurrentFlowProperty =
            DependencyProperty.Register(
                nameof(CurrentFlow),
                typeof(double),
                typeof(MassFlowMeterControl),
                new PropertyMetadata(0.0, OnCurrentFlowChanged));

        public double CurrentFlow
        {
            get => (double)GetValue(CurrentFlowProperty);
            set => SetValue(CurrentFlowProperty, value);
        }

        public static readonly DependencyProperty TargetFlowProperty =
            DependencyProperty.Register(
                nameof(TargetFlow),
                typeof(double),
                typeof(MassFlowMeterControl),
                new PropertyMetadata(20.0, OnTargetFlowChanged));

        public double TargetFlow
        {
            get => (double)GetValue(TargetFlowProperty);
            set => SetValue(TargetFlowProperty, value);
        }

        public static readonly DependencyProperty MaxFlowProperty =
            DependencyProperty.Register(
                nameof(MaxFlow),
                typeof(double),
                typeof(MassFlowMeterControl),
                new PropertyMetadata(150.0));

        public double MaxFlow
        {
            get => (double)GetValue(MaxFlowProperty);
            set => SetValue(MaxFlowProperty, value);
        }

        public static readonly DependencyProperty IsFlowingProperty =
            DependencyProperty.Register(
                nameof(IsFlowing),
                typeof(bool),
                typeof(MassFlowMeterControl),
                new PropertyMetadata(false, OnIsFlowingChanged));

        public bool IsFlowing
        {
            get => (bool)GetValue(IsFlowingProperty);
            set => SetValue(IsFlowingProperty, value);
        }

        public static readonly DependencyProperty IsSelectedProperty =
            DependencyProperty.Register(
                nameof(IsSelected),
                typeof(bool),
                typeof(MassFlowMeterControl),
                new PropertyMetadata(false, OnIsSelectedChanged));

        public bool IsSelected
        {
            get => (bool)GetValue(IsSelectedProperty);
            set => SetValue(IsSelectedProperty, value);
        }

        #endregion

        #region 事件定义

        public event EventHandler<string> MeterSelected;
        public event EventHandler<string> MeterDeselected;
        public event EventHandler<MeterRotateEventArgs> MeterRotateStarted;
        public event EventHandler<MeterRotateEventArgs> MeterRotating;
        public event EventHandler<MeterRotateEventArgs> MeterRotateCompleted;
        public event EventHandler<FlowValueChangedEventArgs> TargetFlowChanged;
        public event EventHandler<FlowStateChangedEventArgs> FlowStateChanged;

        public event EventHandler<FlowMeterStateChangeRequestEventArgs> StateChangeRequested;
        public event EventHandler<FlowMeterTargetFlowChangeRequestEventArgs> TargetFlowChangeRequested;

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

        private bool _isLoading = false;
        public bool IsLoading => _isLoading;

        public string MeterId { get; set; }

        #endregion

        #region 构造函数

        public MassFlowMeterControl()
        {
            InitializeComponent();

            MeterId = Guid.NewGuid().ToString();
            Debug.WriteLine($"MassFlowMeterControl 构造: MeterId={MeterId}");

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
                    if (RotationLabel != null)
                    {
                        RotationLabel.Visibility = Visibility.Collapsed;
                    }
                    _rotationLabelTimer.Stop();
                };

                this.Loaded += OnControlLoaded;
                this.MouseEnter += OnMeterMouseEnter;
                this.MouseLeave += OnMeterMouseLeave;
                SetupRotateAnchorEvents();

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

                this.SnapsToDevicePixels = true;
                this.UseLayoutRounding = true;

                Debug.WriteLine($"流量计 {MeterId} 硬件加速已启用");
            }
        }

        private void OnControlLoaded(object sender, RoutedEventArgs e)
        {
            Debug.WriteLine("OnControlLoaded 开始");

            UpdatePointerAngle(CurrentFlow);
            UpdateCurrentValueText(CurrentFlow);
            UpdateTargetValueText(TargetFlow);
            UpdateMeterColorsByFlowValue(CurrentFlow);

            Debug.WriteLine("OnControlLoaded 结束");
        }

        #endregion

        #region 颜色更新

        private void UpdateMeterColors()
        {
            try
            {
                var meterShell = this.FindName("MeterShell") as Rectangle;

                if (IsFlowing)
                {
                    var runningShellBrush = this.Resources["RunningShellGradient"] as LinearGradientBrush;
                    if (meterShell != null && runningShellBrush != null)
                        meterShell.Fill = runningShellBrush;

                    Debug.WriteLine($"流量计颜色已更新 | MeterId:{MeterId} | 状态:蓝绿色(运行中)");
                }
                else
                {
                    var stoppedShellBrush = this.Resources["StoppedShellGradient"] as LinearGradientBrush;
                    if (meterShell != null && stoppedShellBrush != null)
                        meterShell.Fill = stoppedShellBrush;

                    Debug.WriteLine($"流量计颜色已更新 | MeterId:{MeterId} | 状态:深灰色(已停止)");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"更新流量计颜色失败 | MeterId:{MeterId} | 错误:{ex.Message}");
            }
        }

        /// <summary>
        /// 根据实时流量值更新流量计颜色
        /// </summary>
        private void UpdateMeterColorsByFlowValue(double flowValue)
        {
            try
            {
                var meterShell = this.FindName("MeterShell") as Rectangle;

                if (flowValue > 0)
                {
                    var runningShellBrush = this.Resources["RunningShellGradient"] as LinearGradientBrush;
                    if (meterShell != null && runningShellBrush != null)
                        meterShell.Fill = runningShellBrush;

                    Debug.WriteLine($"流量计颜色已更新 | MeterId:{MeterId} | 流量:{flowValue:F1} | 状态:蓝绿色(运行中)");
                }
                else
                {
                    var stoppedShellBrush = this.Resources["StoppedShellGradient"] as LinearGradientBrush;
                    if (meterShell != null && stoppedShellBrush != null)
                        meterShell.Fill = stoppedShellBrush;

                    Debug.WriteLine($"流量计颜色已更新 | MeterId:{MeterId} | 流量:0.0 | 状态:深灰色(已停止)");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"更新流量计颜色失败 | MeterId:{MeterId} | 错误:{ex.Message}");
            }
        }

        #endregion

        #region 加载和错误状态

        public void ShowLoading()
        {
            if (_isLoading) return;
            _isLoading = true;
            Debug.WriteLine($"显示加载状态 | MeterId:{MeterId}");

            var meterShell = this.FindName("MeterShell") as Rectangle;
            if (meterShell != null)
                meterShell.Fill = new SolidColorBrush(Color.FromRgb(255, 193, 7));
        }

        public void HideLoading()
        {
            if (!_isLoading) return;
            _isLoading = false;
            Debug.WriteLine($"隐藏加载状态 | MeterId:{MeterId}");

            UpdateMeterColorsByFlowValue(CurrentFlow);
        }

        public void ShowOperationFailure()
        {
            Debug.WriteLine($"显示操作失败状态 | MeterId:{MeterId}");

            HideLoading();

            var meterShell = this.FindName("MeterShell") as Rectangle;
            if (meterShell != null)
                meterShell.Fill = new SolidColorBrush(Color.FromRgb(255, 59, 48));
        }

        public void ClearError()
        {
            Debug.WriteLine($"清除错误状态 | MeterId:{MeterId}");
            UpdateMeterColorsByFlowValue(CurrentFlow);
        }

        #endregion

        #region 状态变化回调

        private static void OnIsFlowingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is MassFlowMeterControl meter)
            {
                bool isFlowing = (bool)e.NewValue;

                meter.FlowStateChanged?.Invoke(meter, new FlowStateChangedEventArgs
                {
                    MeterId = meter.MeterId,
                    IsFlowing = isFlowing
                });

                Debug.WriteLine($"流量计 {meter.MeterId} IsFlowing属性变更: {(isFlowing ? "启动" : "停止")}");
            }
        }

        private static void OnIsSelectedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is MassFlowMeterControl meter)
            {
                meter.UpdateSelectionVisual((bool)e.NewValue);
            }
        }

        private void UpdateSelectionVisual(bool isSelected)
        {
            // ★ 旧的 SelectionBorder 由统一系统接管,这里永远关闭
            if (SelectionBorder != null)
                SelectionBorder.Visibility = Visibility.Collapsed;

            // 锚点显隐保持原有逻辑
            if (isSelected) ShowAllAnchors();
            else if (!this.IsMouseOver && !_isRotating) HideAllAnchors();
        }

        private static void OnCurrentFlowChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is MassFlowMeterControl meter)
            {
                double newValue = (double)e.NewValue;
                meter.UpdatePointerAngle(newValue);
                meter.UpdateCurrentValueText(newValue);
                meter.UpdateMeterColorsByFlowValue(newValue);

                Debug.WriteLine($"流量计 {meter.MeterId} 当前流量: {newValue:F1} g/min");
            }
        }

        private static void OnTargetFlowChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is MassFlowMeterControl meter)
            {
                double newValue = (double)e.NewValue;
                meter.UpdateTargetValueText(newValue);
                Debug.WriteLine($"流量计 {meter.MeterId} 目标流量: {newValue:F1} g/min");
            }
        }

        private void UpdatePointerAngle(double flowValue)
        {
            if (PointerRotation == null) return;
            double normalizedValue = Math.Max(0, Math.Min(flowValue, MaxFlow));
            double angle = (normalizedValue / MaxFlow) * 270 - 90;
            PointerRotation.Angle = angle;
        }

        private void UpdateCurrentValueText(double value)
        {
            if (CurrentValueText != null)
            {
                CurrentValueText.Text = $"{value:F1}";
            }
        }

        private void UpdateTargetValueText(double value)
        {
            if (TargetValueText != null)
            {
                TargetValueText.Text = $"{value:F1}";
            }
        }

        #endregion

        #region 锚点显示

        private void OnMeterMouseEnter(object sender, MouseEventArgs e)
        {
            if (!_isDragging && !_isRotating)
            {
                ShowAllAnchors();
            }
        }

        private void OnMeterMouseLeave(object sender, MouseEventArgs e)
        {
            if (!_isRotating && !IsSelected)
            {
                HideAllAnchors();
            }
        }

        private void ShowAllAnchors()
        {
            if (RotateAnchor != null) RotateAnchor.Opacity = 0.7;
            if (RotateAnchorLine != null) RotateAnchorLine.Opacity = 0.7;
        }

        private void HideAllAnchors()
        {
            if (RotateAnchor != null) RotateAnchor.Opacity = 0;
            if (RotateAnchorLine != null) RotateAnchorLine.Opacity = 0;
        }

        #endregion

        #region 按钮点击

        private void OnIncreaseButtonClick(object sender, RoutedEventArgs e)
        {
            try
            {
                double newFlow = Math.Min(TargetFlow + 1, MaxFlow);
                Debug.WriteLine($"增加流量: {TargetFlow:F1} → {newFlow:F1}");

                ShowLoading();

                TargetFlowChangeRequested?.Invoke(this, new FlowMeterTargetFlowChangeRequestEventArgs
                {
                    MeterId = this.MeterId,
                    CurrentTargetFlow = TargetFlow,
                    RequestedTargetFlow = newFlow,
                    ChangeType = FlowChangeType.Increase,
                    OnCompleted = (success) =>
                    {
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            HideLoading();
                            if (!success)
                            {
                                ShowOperationFailure();
                                Debug.WriteLine($"流量增加失败");
                            }
                            else
                            {
                                Debug.WriteLine($"流量增加成功: {newFlow:F1}");
                            }
                        }));
                    }
                });

                e.Handled = true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"增加流量失败: {ex.Message}");
                HideLoading();
                ShowOperationFailure();
            }
        }

        private void OnDecreaseButtonClick(object sender, RoutedEventArgs e)
        {
            try
            {
                double newFlow = Math.Max(TargetFlow - 1, 0);
                Debug.WriteLine($"减少流量: {TargetFlow:F1} → {newFlow:F1}");

                ShowLoading();

                TargetFlowChangeRequested?.Invoke(this, new FlowMeterTargetFlowChangeRequestEventArgs
                {
                    MeterId = this.MeterId,
                    CurrentTargetFlow = TargetFlow,
                    RequestedTargetFlow = newFlow,
                    ChangeType = FlowChangeType.Decrease,
                    OnCompleted = (success) =>
                    {
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            HideLoading();
                            if (!success)
                            {
                                ShowOperationFailure();
                            }
                        }));
                    }
                });

                e.Handled = true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"减少流量失败: {ex.Message}");
                HideLoading();
                ShowOperationFailure();
            }
        }

        private void OnSetButtonClick(object sender, RoutedEventArgs e)
        {
            try
            {
                Debug.WriteLine($"SET按钮点击: 目标流量={TargetFlow:F1}");

                ShowLoading();

                StateChangeRequested?.Invoke(this, new FlowMeterStateChangeRequestEventArgs
                {
                    MeterId = this.MeterId,
                    CurrentState = IsFlowing,
                    RequestedState = true,
                    TargetFlow = TargetFlow,
                    OnCompleted = (success) =>
                    {
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            HideLoading();
                            if (success)
                            {
                                ShowTemporarySelection();
                            }
                            else
                            {
                                ShowOperationFailure();
                            }
                        }));
                    }
                });

                e.Handled = true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"SET按钮处理失败: {ex.Message}");
                HideLoading();
                ShowOperationFailure();
            }
        }

        #endregion

        #region 覆盖层事件

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

                MeterSelected?.Invoke(this, this.MeterId);
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
                    Debug.WriteLine("超过阈值,开始拖拽模式");
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
                    Debug.WriteLine($"点击切换流动状态");

                    ShowLoading();

                    StateChangeRequested?.Invoke(this, new FlowMeterStateChangeRequestEventArgs
                    {
                        MeterId = this.MeterId,
                        CurrentState = IsFlowing,
                        RequestedState = !IsFlowing,
                        TargetFlow = TargetFlow,
                        OnCompleted = (success) =>
                        {
                            Dispatcher.BeginInvoke(new Action(() =>
                            {
                                HideLoading();
                                if (success)
                                {
                                    ShowTemporarySelection();
                                }
                                else
                                {
                                    ShowOperationFailure();
                                }
                            }));
                        }
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

        private void ShowTemporarySelection()
        {
            this.IsSelected = true;
            MeterSelected?.Invoke(this, this.MeterId);

            var autoHideTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(500)
            };

            autoHideTimer.Tick += (s, args) =>
            {
                this.IsSelected = false;
                MeterDeselected?.Invoke(this, this.MeterId);
                autoHideTimer.Stop();
                Debug.WriteLine("流量计选中效果已自动取消");
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
            if (RotateAnchor != null)
            {
                RotateAnchor.MouseEnter += OnRotateAnchorMouseEnter;
                RotateAnchor.MouseLeave += OnRotateAnchorMouseLeave;
                RotateAnchor.MouseDown += OnRotateAnchorMouseDown;
                RotateAnchor.MouseMove += OnRotateAnchorMouseMove;
                RotateAnchor.MouseUp += OnRotateAnchorMouseUp;
            }
        }

        private void OnRotateAnchorMouseEnter(object sender, MouseEventArgs e)
        {
            if (RotateAnchor != null) RotateAnchor.Opacity = 1.0;
            if (RotateAnchorLine != null) RotateAnchorLine.Opacity = 1.0;
        }

        private void OnRotateAnchorMouseLeave(object sender, MouseEventArgs e)
        {
            if (!_isRotating)
            {
                if (RotateAnchor != null) RotateAnchor.Opacity = 0.7;
                if (RotateAnchorLine != null) RotateAnchorLine.Opacity = 0.7;
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

                this.CacheMode = null;
                RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.LowQuality);

                ((UIElement)sender).CaptureMouse();

                MeterRotateStarted?.Invoke(this, new MeterRotateEventArgs
                {
                    MeterId = this.MeterId,
                    OriginalAngle = _originalRotation
                });

                ShowRotationLabel();
                e.Handled = true;
            }
        }

        private void CompleteRotate()
        {
            if (!_isRotating) return;

            MeterRotateCompleted?.Invoke(this, new MeterRotateEventArgs
            {
                MeterId = this.MeterId,
                OriginalAngle = _originalRotation,
                CurrentAngle = _rotationAngle
            });

            _isRotating = false;

            RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
            this.CacheMode = new BitmapCache
            {
                RenderAtScale = 1.0,
                SnapsToDevicePixels = true,
                EnableClearType = false
            };

            if (RotateAnchor != null)
            {
                ((UIElement)RotateAnchor).ReleaseMouseCapture();
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

            MeterRotating?.Invoke(this, new MeterRotateEventArgs
            {
                MeterId = this.MeterId,
                CurrentAngle = _rotationAngle
            });
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
            if (RotationLabel != null)
            {
                RotationLabel.Visibility = Visibility.Visible;
            }
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

        public void SetFlowValue(double value)
        {
            CurrentFlow = Math.Max(0, Math.Min(value, MaxFlow));
        }

        public void StartFlow()
        {
            IsFlowing = true;
        }

        public void StopFlow()
        {
            IsFlowing = false;
        }

        #endregion

        #region IPipeSnapPoints 实现

        public List<PipeSnapPoint> GetSnapPoints()
        {
            var snapPoints = new List<PipeSnapPoint>();

            var parent = this.Parent as Canvas;
            if (parent == null) return snapPoints;

            var meterLeft = Canvas.GetLeft(this);
            var meterTop = Canvas.GetTop(this);
            if (double.IsNaN(meterLeft)) meterLeft = 0;
            if (double.IsNaN(meterTop)) meterTop = 0;

            // 中心点: 控件中心 (200, 250)
            // 左管端头位置: x=-16+4=-12 (相对控件), y=467 (水平管中心)
            //   → 相对中心: offsetX = -212, offsetY = 217
            // 右管端头位置: x=416 (相对控件), y=467
            //   → 相对中心: offsetX = 216, offsetY = 217
            Point center = new Point(meterLeft + 200, meterTop + 250);
            double radians = _rotationAngle * Math.PI / 180.0;

            AddSnapPoint(snapPoints, center, radians, Math.PI, "左侧进口", -212, 217);
            AddSnapPoint(snapPoints, center, radians, 0, "右侧出口", 216, 217);

            return snapPoints;
        }

        private void AddSnapPoint(List<PipeSnapPoint> snapPoints, Point center, double baseRotation,
            double angleOffset, string description, double offsetX, double offsetY)
        {
            double totalAngle = baseRotation + angleOffset;

            double rotatedX = offsetX * Math.Cos(baseRotation) - offsetY * Math.Sin(baseRotation);
            double rotatedY = offsetX * Math.Sin(baseRotation) + offsetY * Math.Cos(baseRotation);

            Point snapPosition = new Point(center.X + rotatedX, center.Y + rotatedY);

            snapPoints.Add(new PipeSnapPoint
            {
                WorldPosition = snapPosition,
                Direction = GetSnapDirectionFromAngle(totalAngle),
                Description = $"流量计{description} (角度:{_rotationAngle:F0}°)"
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

    public class MeterRotateEventArgs : EventArgs
    {
        public string MeterId { get; set; }
        public double OriginalAngle { get; set; }
        public double CurrentAngle { get; set; }
    }

    public class FlowValueChangedEventArgs : EventArgs
    {
        public string MeterId { get; set; }
        public double OldValue { get; set; }
        public double NewValue { get; set; }
    }

    public class FlowStateChangedEventArgs : EventArgs
    {
        public string MeterId { get; set; }
        public bool IsFlowing { get; set; }
    }

    #endregion
}
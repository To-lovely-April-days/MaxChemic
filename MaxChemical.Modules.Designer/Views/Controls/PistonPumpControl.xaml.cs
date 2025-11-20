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
using MaxChemical.Modules.Designer.Service;

namespace MaxChemical.Modules.Designer.Views.Controls
{
    /// <summary>
    /// PistonPumpControl.xaml 的交互逻辑
    /// 支持设备驱动 ↔ UI 双向联动
    /// </summary>
    public partial class PistonPumpControl : UserControl, INotifyPropertyChanged, IPipeSnapPoints
    {
        #region 事件定义

        public event PropertyChangedEventHandler PropertyChanged;
        public event EventHandler<PumpDragEventArgs> PumpDragStarted;
        public event EventHandler<PumpDragEventArgs> PumpDragging;
        public event EventHandler<PumpDragEventArgs> PumpDragCompleted;
        public event EventHandler<string> PumpSelected;
        public event EventHandler<string> PumpDeselected;
        public event EventHandler<PumpStateChangedEventArgs> PumpStateChanged;

        // ✅ 新增: UI → 设备驱动的请求事件
        public event EventHandler<PumpStateChangeRequestEventArgs> StateChangeRequested;
        public event EventHandler<PumpSpeedChangeRequestEventArgs> SpeedChangeRequested;

        #endregion

        #region 字段

        public string PumpId { get; set; }

        private bool _isDraggingPump = false;
        private Point _dragStartPoint;
        private Point _originalPosition;

        // 泵运行状态
        private bool _isRunning = false;
        private int _pumpSpeed = 20;
        private Storyboard _pistonStoryboard;

        // ✅ 新增: 加载和错误状态
        private bool _isLoading = false;
        private DispatcherTimer _errorResetTimer;
        private Storyboard _errorBlinkAnimation;
        private Storyboard _loadingAnimation;
        /// <summary>
        /// 公共属性：是否正在加载
        /// </summary>
        public bool IsLoading
        {
            get => _isLoading;
        }
        #endregion

        #region 依赖属性

        /// <summary>
        /// 泵是否运行中
        /// </summary>
        public static readonly DependencyProperty IsRunningProperty =
            DependencyProperty.Register(
                nameof(IsRunning),
                typeof(bool),
                typeof(PistonPumpControl),
                new PropertyMetadata(false, OnIsRunningChanged));

        public bool IsRunning
        {
            get => (bool)GetValue(IsRunningProperty);
            set => SetValue(IsRunningProperty, value);
        }

        private static void OnIsRunningChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is PistonPumpControl control)
            {
                control.UpdatePumpRunningState();
                control.OnPropertyChanged(nameof(IsRunning));
            }
        }

        /// <summary>
        /// 泵速度 (0-100)
        /// </summary>
        public static readonly DependencyProperty PumpSpeedProperty =
            DependencyProperty.Register(
                nameof(PumpSpeed),
                typeof(int),
                typeof(PistonPumpControl),
                new PropertyMetadata(20, OnPumpSpeedChanged));

        public int PumpSpeed
        {
            get => (int)GetValue(PumpSpeedProperty);
            set => SetValue(PumpSpeedProperty, Math.Max(0, Math.Min(100, value)));
        }

        private static void OnPumpSpeedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is PistonPumpControl control)
            {
                control.UpdatePumpSpeedDisplay();
                control.UpdatePistonAnimationSpeed();
                control.OnPropertyChanged(nameof(PumpSpeed));
            }
        }

        /// <summary>
        /// 选中状态
        /// </summary>
        public static readonly DependencyProperty IsSelectedProperty =
            DependencyProperty.Register(
                nameof(IsSelected),
                typeof(bool),
                typeof(PistonPumpControl),
                new PropertyMetadata(false, OnIsSelectedChanged));

        public bool IsSelected
        {
            get => (bool)GetValue(IsSelectedProperty);
            set => SetValue(IsSelectedProperty, value);
        }

        private static void OnIsSelectedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is PistonPumpControl control)
            {
                control.UpdateSelectionVisual((bool)e.NewValue);
            }
        }

        #endregion

        #region 构造函数

        public PistonPumpControl()
        {
            InitializeComponent();
            PumpId = Guid.NewGuid().ToString();
            this.DataContext = this;

            bool isInDesignMode = DesignerProperties.GetIsInDesignMode(this);

            this.Loaded += OnPumpControlLoaded;

            if (!isInDesignMode)
            { // ✅ 初始化错误重置定时器
                _errorResetTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(4.5) // 3次闪烁 × 1.5秒
                };
                // ✅ 初始化错误重置定时器
                _errorResetTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(3)
                };
                _errorResetTimer.Tick += (s, e) =>
                {
                    ResetErrorState();
                    _errorResetTimer.Stop();
                };

                // 硬件加速配置
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

                Debug.WriteLine($"✅ 柱塞泵 {PumpId} 硬件加速已启用");
            }

            Debug.WriteLine($"✅ 柱塞泵控件 {PumpId} 已创建");
        }

        private void OnPumpControlLoaded(object sender, RoutedEventArgs e)
        {
            try
            {
                _pistonStoryboard = this.Resources["PistonMotionStoryboard"] as Storyboard;

                // ✅ 加载动画资源
                _errorBlinkAnimation = this.Resources["ErrorBlinkAnimation"] as Storyboard;
                _loadingAnimation = this.Resources["LoadingAnimation"] as Storyboard;

                if (_pistonStoryboard == null)
                {
                    Debug.WriteLine("⚠️ 警告: 柱塞动画资源未找到");
                }

                if (_errorBlinkAnimation == null || _loadingAnimation == null)
                {
                    Debug.WriteLine("⚠️ 警告: 错误/加载动画资源未找到");
                }

                UpdatePumpSpeedDisplay();
                UpdatePumpRunningState();

                Debug.WriteLine($"✅ 柱塞泵控件 {PumpId} 已加载完成");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ 柱塞泵控件加载失败: {ex.Message}");
            }
        }

        #endregion

        #region 泵控制方法 (保留原有公共方法)

        public void StartPump()
        {
            try
            {
                if (!IsRunning)
                {
                    IsRunning = true;
                    Debug.WriteLine($"🟢 柱塞泵 {PumpId} 已启动，速度: {PumpSpeed}");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ 启动柱塞泵失败: {ex.Message}");
            }
        }

        public void StopPump()
        {
            try
            {
                if (IsRunning)
                {
                    IsRunning = false;
                    Debug.WriteLine($"🔴 柱塞泵 {PumpId} 已停止");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ 停止柱塞泵失败: {ex.Message}");
            }
        }

        public void SetSpeed(int speed)
        {
            try
            {
                PumpSpeed = speed;
                Debug.WriteLine($"⚙️ 柱塞泵 {PumpId} 速度设置为: {PumpSpeed}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ 设置柱塞泵速度失败: {ex.Message}");
            }
        }

        #endregion

        #region UI 状态更新方法

        /// <summary>
        /// 更新泵运行状态 (设备驱动 → UI)
        /// </summary>
        private void UpdatePumpRunningState()
        {
            try
            {
                if (_pistonStoryboard != null)
                {
                    if (IsRunning)
                    {
                        Timeline.SetDesiredFrameRate(_pistonStoryboard, 30);
                        _pistonStoryboard.Begin(this, true);
                        Debug.WriteLine("🔄 柱塞动画已启动（30fps GPU加速）");
                    }
                    else
                    {
                        _pistonStoryboard.Stop(this);
                        Debug.WriteLine("⏸️ 柱塞动画已停止");
                    }
                }

                UpdatePowerButtonColor();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ 更新泵运行状态失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 更新电源按钮颜色
        /// </summary>
        private void UpdatePowerButtonColor()
        {
            try
            {
                var powerIcon = this.FindName("PowerIcon") as System.Windows.Shapes.Path;
                if (powerIcon != null)
                {
                    if (IsRunning)
                    {
                        // 运行时 - 红色
                        powerIcon.Fill = new SolidColorBrush(Color.FromRgb(255, 59, 48));
                        var effect = powerIcon.Effect as DropShadowEffect;
                        if (effect != null)
                        {
                            effect.Color = Color.FromRgb(255, 59, 48);
                        }
                    }
                    else
                    {
                        // 停止时 - 绿色
                        powerIcon.Fill = new SolidColorBrush(Color.FromRgb(26, 187, 72));
                        var effect = powerIcon.Effect as DropShadowEffect;
                        if (effect != null)
                        {
                            effect.Color = Color.FromRgb(26, 187, 72);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ 更新电源按钮颜色失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 更新泵速度显示
        /// </summary>
        private void UpdatePumpSpeedDisplay()
        {
            try
            {
                Debug.WriteLine($"📊 泵速度显示更新为: {PumpSpeed}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ 更新速度显示失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 更新柱塞动画速度
        /// </summary>
        private void UpdatePistonAnimationSpeed()
        {
            try
            {
                if (_pistonStoryboard != null && IsRunning)
                {
                    double speedRatio = 0.5 + (PumpSpeed / 100.0) * 1.5;
                    _pistonStoryboard.SetSpeedRatio(this, speedRatio);
                    Debug.WriteLine($"⚡ 动画速率调整为: {speedRatio:F2}x (速度: {PumpSpeed})");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ 更新动画速度失败: {ex.Message}");
            }
        }

        #endregion

        #region ✅ 新增: 按钮事件处理 (UI → 设备驱动)

        /// <summary>
        /// 电源按钮点击 - 请求切换泵的运行状态
        /// </summary>
        private void OnPowerButtonClick(object sender, RoutedEventArgs e)
        {
            try
            {
                Debug.WriteLine($"💡 电源按钮点击 | PumpId:{PumpId}, 当前状态:{IsRunning}");

                // ✅ 显示加载动画
                ShowLoading();

                // ✅ 触发状态切换请求事件（带完成回调）
                StateChangeRequested?.Invoke(this, new PumpStateChangeRequestEventArgs
                {
                    PumpId = this.PumpId,
                    CurrentState = IsRunning,
                    RequestedState = !IsRunning,
                    OnCompleted = (success) =>
                    {
                        // 回到UI线程执行
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            HideLoading();
                            if (!success)
                            {
                                ShowOperationFailure();
                                Debug.WriteLine($"❌ 电源状态切换失败 | PumpId:{PumpId}");
                            }
                            else
                            {
                                Debug.WriteLine($"✅ 电源状态切换成功 | PumpId:{PumpId}, 新状态:{!IsRunning}");
                            }
                        }));
                    }
                });

                e.Handled = true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ 电源按钮点击处理失败: {ex.Message}");
                HideLoading();
                ShowOperationFailure();
            }
        }

        /// <summary>
        /// 增加速度按钮点击
        /// </summary>
        private void OnIncreaseButtonClick(object sender, RoutedEventArgs e)
        {
            try
            {
                int newSpeed = PumpSpeed + 10;
                Debug.WriteLine($"⬆️ 增加速度按钮点击: {PumpSpeed} → {newSpeed}");

                // ✅ 显示加载动画
                ShowLoading();

                // ✅ 触发速度变化请求事件（带完成回调）
                SpeedChangeRequested?.Invoke(this, new PumpSpeedChangeRequestEventArgs
                {
                    PumpId = this.PumpId,
                    CurrentSpeed = PumpSpeed,
                    RequestedSpeed = newSpeed,
                    ChangeType = SpeedChangeType.Increase,
                    OnCompleted = (success) =>
                    {
                        // 回到UI线程执行
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            HideLoading();
                            if (!success)
                            {
                                ShowOperationFailure();
                                Debug.WriteLine($"❌ 速度增加失败 | PumpId:{PumpId}");
                            }
                            else
                            {
                                Debug.WriteLine($"✅ 速度增加成功 | PumpId:{PumpId}, 新速度:{newSpeed}");
                            }
                        }));
                    }
                });

                e.Handled = true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ 增加速度按钮点击处理失败: {ex.Message}");
                HideLoading();
                ShowOperationFailure();
            }
        }

        /// <summary>
        /// 减少速度按钮点击
        /// </summary>
        private void OnDecreaseButtonClick(object sender, RoutedEventArgs e)
        {
            try
            {
                int newSpeed = PumpSpeed - 10;
                Debug.WriteLine($"⬇️ 减少速度按钮点击: {PumpSpeed} → {newSpeed}");

                // ✅ 显示加载动画
                ShowLoading();

                // ✅ 触发速度变化请求事件（带完成回调）
                SpeedChangeRequested?.Invoke(this, new PumpSpeedChangeRequestEventArgs
                {
                    PumpId = this.PumpId,
                    CurrentSpeed = PumpSpeed,
                    RequestedSpeed = newSpeed,
                    ChangeType = SpeedChangeType.Decrease,
                    OnCompleted = (success) =>
                    {
                        // 回到UI线程执行
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            HideLoading();
                            if (!success)
                            {
                                ShowOperationFailure();
                                Debug.WriteLine($"❌ 速度减少失败 | PumpId:{PumpId}");
                            }
                            else
                            {
                                Debug.WriteLine($"✅ 速度减少成功 | PumpId:{PumpId}, 新速度:{newSpeed}");
                            }
                        }));
                    }
                });

                e.Handled = true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ 减少速度按钮点击处理失败: {ex.Message}");
                HideLoading();
                ShowOperationFailure();
            }
        }

        #endregion

        #region ✅ 新增: 加载和错误动画

        /// <summary>
        /// 显示加载动画
        /// </summary>
        public void ShowLoading()
        {
            if (_isLoading) return;

            _isLoading = true;
            Debug.WriteLine($"⏳ 显示加载动画 | PumpId:{PumpId}");

            try
            {
                // 清除错误状态
                ClearError();

                // 启动加载动画
                if (_loadingAnimation != null)
                {
                    _loadingAnimation.Begin(this, true);
                }

                // 显示加载指示器
                if (LoadingIndicatorContainer != null)
                {
                    LoadingIndicatorContainer.Opacity = 1;
                }

                // 显示脉冲圆环
                if (LoadingPulse != null)
                {
                    LoadingPulse.Opacity = 0.3;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ 显示加载动画失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 隐藏加载动画
        /// </summary>
        public void HideLoading()
        {
            if (!_isLoading) return;

            _isLoading = false;
            Debug.WriteLine($"✅ 隐藏加载动画 | PumpId:{PumpId}");

            try
            {
                // 停止动画
                if (_loadingAnimation != null)
                {
                    _loadingAnimation.Stop(this);
                }

                // 淡出效果
                var fadeOut = new DoubleAnimation
                {
                    From = LoadingOverlay?.Opacity ?? 0,
                    To = 0,
                    Duration = TimeSpan.FromMilliseconds(200)
                };

                var fadeOutIndicator = new DoubleAnimation
                {
                    From = LoadingIndicatorContainer?.Opacity ?? 0,
                    To = 0,
                    Duration = TimeSpan.FromMilliseconds(200)
                };

                var fadeOutPulse = new DoubleAnimation
                {
                    From = LoadingPulse?.Opacity ?? 0,
                    To = 0,
                    Duration = TimeSpan.FromMilliseconds(200)
                };

                LoadingOverlay?.BeginAnimation(OpacityProperty, fadeOut);
                LoadingIndicatorContainer?.BeginAnimation(OpacityProperty, fadeOutIndicator);
                LoadingPulse?.BeginAnimation(OpacityProperty, fadeOutPulse);

                // 重置旋转角度
                if (LoadingIndicatorRotation != null)
                {
                    LoadingIndicatorRotation.Angle = 0;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ 隐藏加载动画失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 显示操作失败动画
        /// </summary>
        public void ShowOperationFailure()
        {
            Debug.WriteLine($"🚨 显示操作失败动画 | PumpId:{PumpId}");

            try
            {
                // 隐藏加载动画
                HideLoading();

                // 停止之前的错误动画
                if (_errorBlinkAnimation != null)
                {
                    _errorBlinkAnimation.Stop(this);
                }

                // 从资源加载动画（如果未加载）
                if (_errorBlinkAnimation == null)
                {
                    _errorBlinkAnimation = this.Resources["ErrorBlinkAnimation"] as Storyboard;
                }

                // 启动闪烁动画
                if (_errorBlinkAnimation != null)
                {
                    _errorBlinkAnimation.Begin(this, true);
                }

                // 同时显示错误边框
                if (ErrorBorder != null)
                {
                    var borderBlinkAnimation = new DoubleAnimationUsingKeyFrames
                    {
                        Duration = TimeSpan.FromSeconds(1.5),
                        RepeatBehavior = new RepeatBehavior(3)
                    };
                    borderBlinkAnimation.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0))));
                    borderBlinkAnimation.KeyFrames.Add(new LinearDoubleKeyFrame(0.8, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.3))));
                    borderBlinkAnimation.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.6))));
                    borderBlinkAnimation.KeyFrames.Add(new LinearDoubleKeyFrame(0.8, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.9))));
                    borderBlinkAnimation.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1.2))));
                    borderBlinkAnimation.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1.5))));

                    ErrorBorder.BeginAnimation(OpacityProperty, borderBlinkAnimation);
                }

                // 启动自动重置定时器
                _errorResetTimer?.Stop();
                _errorResetTimer?.Start();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ 显示操作失败动画失败: {ex.Message}");
            }
        }
        /// <summary>
        /// 清除错误状态
        /// </summary>
        public void ClearError()
        {
            Debug.WriteLine($"✅ 清除错误状态 | PumpId:{PumpId}");

            try
            {
                // 停止错误动画
                if (_errorBlinkAnimation != null)
                {
                    _errorBlinkAnimation.Stop(this);
                }

                // 重置覆盖层透明度
                if (ErrorOverlay != null)
                {
                    ErrorOverlay.Opacity = 0;
                }

                if (ErrorBorder != null)
                {
                    ErrorBorder.Opacity = 0;
                }

                // 停止定时器
                _errorResetTimer?.Stop();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ 清除错误状态失败: {ex.Message}");
            }
        }


        /// <summary>
        /// 重置错误状态
        /// </summary>
        private void ResetErrorState()
        {
            Debug.WriteLine($"✅ 重置错误状态 | PumpId:{PumpId}");
            // 可以在这里重置UI状态
        }

        #endregion

        #region 拖拽控制 (保留原有代码)

        private void OnDraggableAreaMouseDown(object sender, MouseButtonEventArgs e)
        {
            try
            {
                if (e.LeftButton == MouseButtonState.Pressed)
                {
                    PumpSelected?.Invoke(this, this.PumpId);
                    this.IsSelected = true;

                    _isDraggingPump = true;
                    var parent = this.Parent as Canvas;
                    if (parent != null)
                    {
                        _dragStartPoint = e.GetPosition(parent);
                        _originalPosition = new Point(Canvas.GetLeft(this), Canvas.GetTop(this));
                        if (double.IsNaN(_originalPosition.X)) _originalPosition.X = 0;
                        if (double.IsNaN(_originalPosition.Y)) _originalPosition.Y = 0;
                    }

                    this.CacheMode = null;
                    RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.LowQuality);

                    var draggableArea = sender as Rectangle;
                    draggableArea?.CaptureMouse();

                    this.Opacity = 0.7;

                    PumpDragStarted?.Invoke(this, new PumpDragEventArgs
                    {
                        PumpId = PumpId,
                        StartPosition = _originalPosition
                    });

                    e.Handled = true;
                    Debug.WriteLine($"🖱️ 开始拖拽柱塞泵 {PumpId}");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ 鼠标按下事件处理失败: {ex.Message}");
            }
        }

        private void OnDraggableAreaMouseMove(object sender, MouseEventArgs e)
        {
            try
            {
                if (_isDraggingPump && e.LeftButton == MouseButtonState.Pressed)
                {
                    var parent = this.Parent as Canvas;
                    if (parent != null)
                    {
                        var currentPoint = e.GetPosition(parent);
                        double deltaX = currentPoint.X - _dragStartPoint.X;
                        double deltaY = currentPoint.Y - _dragStartPoint.Y;

                        double newLeft = Math.Max(0, Math.Min(parent.ActualWidth - this.ActualWidth,
                                                              _originalPosition.X + deltaX));
                        double newTop = Math.Max(0, Math.Min(parent.ActualHeight - this.ActualHeight,
                                                             _originalPosition.Y + deltaY));

                        Canvas.SetLeft(this, newLeft);
                        Canvas.SetTop(this, newTop);

                        PumpDragging?.Invoke(this, new PumpDragEventArgs
                        {
                            PumpId = PumpId,
                            CurrentPosition = new Point(newLeft, newTop)
                        });
                    }
                    e.Handled = true;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ 鼠标移动事件处理失败: {ex.Message}");
            }
        }

        private void OnDraggableAreaMouseUp(object sender, MouseButtonEventArgs e)
        {
            try
            {
                if (_isDraggingPump)
                {
                    CompletePumpDrag();

                    var draggableArea = sender as Rectangle;
                    draggableArea?.ReleaseMouseCapture();

                    e.Handled = true;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ 鼠标释放事件处理失败: {ex.Message}");
            }
        }

        private void OnDraggableAreaMouseLeave(object sender, MouseEventArgs e)
        {
            try
            {
                if (_isDraggingPump)
                {
                    CompletePumpDrag();

                    var draggableArea = sender as Rectangle;
                    draggableArea?.ReleaseMouseCapture();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ 鼠标离开事件处理失败: {ex.Message}");
            }
        }

        private void CompletePumpDrag()
        {
            try
            {
                if (!_isDraggingPump) return;

                var finalPosition = new Point(Canvas.GetLeft(this), Canvas.GetTop(this));

                PumpDragCompleted?.Invoke(this, new PumpDragEventArgs
                {
                    PumpId = PumpId,
                    StartPosition = _originalPosition,
                    CurrentPosition = finalPosition
                });

                this.Opacity = 1.0;
                _isDraggingPump = false;

                RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
                this.CacheMode = new BitmapCache
                {
                    RenderAtScale = 1.0,
                    SnapsToDevicePixels = true,
                    EnableClearType = false
                };

                Debug.WriteLine($"✅ 柱塞泵 {PumpId} 拖拽完成: ({finalPosition.X:F0}, {finalPosition.Y:F0})");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ 完成拖拽失败: {ex.Message}");
            }
        }

        private void UpdateSelectionVisual(bool isSelected)
        {
            try
            {
                if (isSelected)
                {
                    Debug.WriteLine($"✅ 柱塞泵 {PumpId} 已选中");
                }
                else
                {
                    Debug.WriteLine($"❌ 柱塞泵 {PumpId} 取消选中");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ 更新选中视觉效果失败: {ex.Message}");
            }
        }

        #endregion

        #region IPipeSnapPoints 实现 (保留原有代码)

        public List<PipeSnapPoint> GetSnapPoints()
        {
            var snapPoints = new List<PipeSnapPoint>();

            try
            {
                var parent = this.Parent as Canvas;
                if (parent == null)
                {
                    Debug.WriteLine("⚠️ 柱塞泵未添加到Canvas，无法获取吸附点");
                    return snapPoints;
                }

                var pumpLeft = Canvas.GetLeft(this);
                var pumpTop = Canvas.GetTop(this);
                if (double.IsNaN(pumpLeft)) pumpLeft = 0;
                if (double.IsNaN(pumpTop)) pumpTop = 0;

                snapPoints.Add(new PipeSnapPoint
                {
                    WorldPosition = new Point(pumpLeft + 14, pumpTop + 100),
                    Direction = SnapDirection.Left,
                    Description = $"柱塞泵入口（左上）"
                });

                snapPoints.Add(new PipeSnapPoint
                {
                    WorldPosition = new Point(pumpLeft + 868, pumpTop + 300),
                    Direction = SnapDirection.Right,
                    Description = $"柱塞泵出口（右下）"
                });

                snapPoints.Add(new PipeSnapPoint
                {
                    WorldPosition = new Point(pumpLeft + 440, pumpTop + 0),
                    Direction = SnapDirection.Up,
                    Description = $"柱塞泵顶部"
                });

                snapPoints.Add(new PipeSnapPoint
                {
                    WorldPosition = new Point(pumpLeft + 440, pumpTop + 600),
                    Direction = SnapDirection.Down,
                    Description = $"柱塞泵底部"
                });

                Debug.WriteLine($"⚙️ 柱塞泵 {PumpId} 提供 {snapPoints.Count} 个吸附点");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ 获取吸附点失败: {ex.Message}");
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
    }

    #region ✅ 新增: 事件参数类

    /// <summary>
    /// 泵状态切换请求事件参数 (UI → 设备驱动)
    /// </summary>
    public class PumpStateChangeRequestEventArgs : EventArgs
    {
        public string PumpId { get; set; }
        public bool CurrentState { get; set; }   // 当前运行状态
        public bool RequestedState { get; set; }  // 请求的新状态

        public Action<bool> OnCompleted { get; set; }
    }

    /// <summary>
    /// 泵速度变化请求事件参数 (UI → 设备驱动)
    /// </summary>
    public class PumpSpeedChangeRequestEventArgs : EventArgs
    {
        public string PumpId { get; set; }
        public int CurrentSpeed { get; set; }     // 当前速度
        public int RequestedSpeed { get; set; }    // 请求的速度
        public SpeedChangeType ChangeType { get; set; } // 增加/减少/设置

        // ✅ 新增：操作完成回调
        public Action<bool> OnCompleted { get; set; }
    }

    /// <summary>
    /// 速度变化类型
    /// </summary>
    public enum SpeedChangeType
    {
        Increase,  // 增加
        Decrease,  // 减少
        Set        // 设置
    }

    /// <summary>
    /// 泵拖拽事件参数 (保留原有)
    /// </summary>
    public class PumpDragEventArgs : EventArgs
    {
        public string PumpId { get; set; }
        public Point StartPosition { get; set; }
        public Point CurrentPosition { get; set; }
    }

    /// <summary>
    /// 泵状态变化事件参数 (保留原有)
    /// </summary>
    public class PumpStateChangedEventArgs : EventArgs
    {
        public string PumpId { get; set; }
        public bool IsRunning { get; set; }
        public int Speed { get; set; }
    }

    #endregion
}
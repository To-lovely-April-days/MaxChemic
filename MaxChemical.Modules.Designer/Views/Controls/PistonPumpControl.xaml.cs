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
    /// 支持设备驱动与UI双向联动
    /// 注释掉所有动画以提升性能
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

        // UI向设备驱动的请求事件
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
        private int _pumpSpeed = 120;

        // 动画相关字段保留但不使用
        private Storyboard _rotorStoryboard;
        private bool _isLoading = false;
        private DispatcherTimer _errorResetTimer;
        private Storyboard _errorBlinkAnimation;
        private Storyboard _loadingAnimation;
        private Storyboard _errorStateHoldAnimation;

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
        /// 泵速度范围 0-600 (rpm)
        /// </summary>
        public static readonly DependencyProperty PumpSpeedProperty =
            DependencyProperty.Register(
                nameof(PumpSpeed),
                typeof(int),
                typeof(PistonPumpControl),
                new PropertyMetadata(120, OnPumpSpeedChanged));

        public int PumpSpeed
        {
            get => (int)GetValue(PumpSpeedProperty);
            set => SetValue(PumpSpeedProperty, Math.Max(0, Math.Min(600, value)));
        }

        private static void OnPumpSpeedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is PistonPumpControl control)
            {
                control.UpdatePumpSpeedDisplay();
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
            {
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

                Debug.WriteLine($"蠕动泵硬件加速已启用 | PumpId:{PumpId}");
            }

            Debug.WriteLine($"蠕动泵控件已创建 | PumpId:{PumpId}");
        }

        private void OnPumpControlLoaded(object sender, RoutedEventArgs e)
        {
            try
            {
                UpdatePumpSpeedDisplay();
                UpdatePumpRunningState();

                Debug.WriteLine($"蠕动泵控件已加载完成 | PumpId:{PumpId}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"蠕动泵控件加载失败 | PumpId:{PumpId} | 错误:{ex.Message}");
            }
        }

        #endregion

        #region 泵控制方法

        public void StartPump()
        {
            try
            {
                if (!IsRunning)
                {
                    IsRunning = true;
                    Debug.WriteLine($"蠕动泵已启动 | PumpId:{PumpId} | 速度:{PumpSpeed}rpm");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"启动蠕动泵失败 | PumpId:{PumpId} | 错误:{ex.Message}");
            }
        }

        public void StopPump()
        {
            try
            {
                if (IsRunning)
                {
                    IsRunning = false;
                    Debug.WriteLine($"蠕动泵已停止 | PumpId:{PumpId}");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"停止蠕动泵失败 | PumpId:{PumpId} | 错误:{ex.Message}");
            }
        }

        public void SetSpeed(int speed)
        {
            try
            {
                PumpSpeed = speed;
                Debug.WriteLine($"蠕动泵速度已设置 | PumpId:{PumpId} | 速度:{PumpSpeed}rpm");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"设置蠕动泵速度失败 | PumpId:{PumpId} | 错误:{ex.Message}");
            }
        }

        #endregion

        #region UI状态更新方法

        /// <summary>
        /// 更新泵运行状态（设备驱动到UI）
        /// 无动画版本，仅更新颜色
        /// </summary>
        private void UpdatePumpRunningState()
        {
            try
            {
                // 更新电源指示灯
                UpdatePowerLed();
                // 更改外壳颜色
                UpdatePumpColors();

                Debug.WriteLine($"蠕动泵运行状态已更新 | PumpId:{PumpId} | 状态:{(IsRunning ? "运行中" : "已停止")}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"更新蠕动泵运行状态失败 | PumpId:{PumpId} | 错误:{ex.Message}");
            }
        }

        /// <summary>
        /// 更新电源指示灯颜色
        /// 运行时亮绿，停止时暗绿
        /// </summary>
        private void UpdatePowerLed()
        {
            try
            {
                var powerLed = this.FindName("PowerLed") as Ellipse;
                var powerLedGlow = this.FindName("PowerLedGlow") as Ellipse;

                if (powerLed != null && powerLedGlow != null)
                {
                    if (IsRunning)
                    {
                        // 运行时 - 亮绿
                        powerLed.Fill = new SolidColorBrush(Color.FromRgb(26, 140, 50));
                        powerLed.Opacity = 1.0;
                        powerLedGlow.Fill = new SolidColorBrush(Color.FromRgb(80, 255, 100));
                        powerLedGlow.Opacity = 0.9;
                    }
                    else
                    {
                        // 停止时 - 暗绿
                        powerLed.Fill = new SolidColorBrush(Color.FromRgb(26, 90, 40));
                        powerLed.Opacity = 0.8;
                        powerLedGlow.Fill = new SolidColorBrush(Color.FromRgb(64, 224, 80));
                        powerLedGlow.Opacity = 0.6;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"更新电源指示灯失败 | PumpId:{PumpId} | 错误:{ex.Message}");
            }
        }

        /// <summary>
        /// 更新泵的外壳颜色
        /// 运行时显示蓝绿色,停止时显示蓝灰色
        /// </summary>
        private void UpdatePumpColors()
        {
            try
            {
                var pumpShell = this.FindName("PumpShell") as System.Windows.Shapes.Path;

                if (IsRunning)
                {
                    // 运行状态 - 蓝绿色渐变
                    var runningShellBrush = this.Resources["RunningShellGradient"] as LinearGradientBrush;

                    if (pumpShell != null && runningShellBrush != null)
                        pumpShell.Fill = runningShellBrush;
                }
                else
                {
                    // 停止状态 - 蓝灰色渐变
                    var stoppedShellBrush = this.Resources["StoppedShellGradient"] as LinearGradientBrush;

                    if (pumpShell != null && stoppedShellBrush != null)
                        pumpShell.Fill = stoppedShellBrush;
                }

                Debug.WriteLine($"蠕动泵颜色已更新 | PumpId:{PumpId} | 状态:{(IsRunning ? "蓝绿色(运行中)" : "蓝灰色(已停止)")}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"更新蠕动泵颜色失败 | PumpId:{PumpId} | 错误:{ex.Message}");
            }
        }

        /// <summary>
        /// 更新泵速度显示
        /// </summary>
        private void UpdatePumpSpeedDisplay()
        {
            try
            {
                Debug.WriteLine($"蠕动泵速度显示已更新 | PumpId:{PumpId} | 速度:{PumpSpeed}rpm");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"更新速度显示失败 | PumpId:{PumpId} | 错误:{ex.Message}");
            }
        }

        #endregion

        #region 按钮事件处理（UI到设备驱动）

        /// <summary>
        /// RUN按钮点击事件 — 请求启动泵
        /// </summary>
        private void OnRunButtonClick(object sender, RoutedEventArgs e)
        {
            try
            {
                if (IsRunning)
                {
                    Debug.WriteLine($"蠕动泵已在运行中，忽略RUN | PumpId:{PumpId}");
                    return;
                }

                Debug.WriteLine($"RUN按钮点击 | PumpId:{PumpId}");

                ShowLoading();

                StateChangeRequested?.Invoke(this, new PumpStateChangeRequestEventArgs
                {
                    PumpId = this.PumpId,
                    CurrentState = IsRunning,
                    RequestedState = true,
                    OnCompleted = (success) =>
                    {
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            HideLoading();
                            if (!success)
                            {
                                ShowOperationFailure();
                                Debug.WriteLine($"启动蠕动泵失败 | PumpId:{PumpId}");
                            }
                            else
                            {
                                Debug.WriteLine($"启动蠕动泵成功 | PumpId:{PumpId}");
                            }
                        }));
                    }
                });

                e.Handled = true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"RUN按钮处理失败 | PumpId:{PumpId} | 错误:{ex.Message}");
                HideLoading();
                ShowOperationFailure();
            }
        }

        /// <summary>
        /// STOP按钮点击事件 — 请求停止泵
        /// </summary>
        private void OnStopButtonClick(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!IsRunning)
                {
                    Debug.WriteLine($"蠕动泵已停止，忽略STOP | PumpId:{PumpId}");
                    return;
                }

                Debug.WriteLine($"STOP按钮点击 | PumpId:{PumpId}");

                ShowLoading();

                StateChangeRequested?.Invoke(this, new PumpStateChangeRequestEventArgs
                {
                    PumpId = this.PumpId,
                    CurrentState = IsRunning,
                    RequestedState = false,
                    OnCompleted = (success) =>
                    {
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            HideLoading();
                            if (!success)
                            {
                                ShowOperationFailure();
                                Debug.WriteLine($"停止蠕动泵失败 | PumpId:{PumpId}");
                            }
                            else
                            {
                                Debug.WriteLine($"停止蠕动泵成功 | PumpId:{PumpId}");
                            }
                        }));
                    }
                });

                e.Handled = true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"STOP按钮处理失败 | PumpId:{PumpId} | 错误:{ex.Message}");
                HideLoading();
                ShowOperationFailure();
            }
        }

        /// <summary>
        /// 电源按钮点击事件 — 切换运行/停止
        /// </summary>
        private void OnPowerButtonClick(object sender, RoutedEventArgs e)
        {
            try
            {
                Debug.WriteLine($"电源按钮点击 | PumpId:{PumpId} | 当前状态:{IsRunning}");

                ShowLoading();

                StateChangeRequested?.Invoke(this, new PumpStateChangeRequestEventArgs
                {
                    PumpId = this.PumpId,
                    CurrentState = IsRunning,
                    RequestedState = !IsRunning,
                    OnCompleted = (success) =>
                    {
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            HideLoading();
                            if (!success)
                            {
                                ShowOperationFailure();
                                Debug.WriteLine($"电源状态切换失败 | PumpId:{PumpId}");
                            }
                            else
                            {
                                Debug.WriteLine($"电源状态切换成功 | PumpId:{PumpId} | 新状态:{!IsRunning}");
                            }
                        }));
                    }
                });

                e.Handled = true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"电源按钮处理失败 | PumpId:{PumpId} | 错误:{ex.Message}");
                HideLoading();
                ShowOperationFailure();
            }
        }

        /// <summary>
        /// 增加速度按钮点击事件
        /// </summary>
        private void OnIncreaseButtonClick(object sender, RoutedEventArgs e)
        {
            try
            {
                int newSpeed = PumpSpeed + 10;
                Debug.WriteLine($"增加速度按钮点击 | PumpId:{PumpId} | 当前速度:{PumpSpeed} | 目标速度:{newSpeed}");

                ShowLoading();

                SpeedChangeRequested?.Invoke(this, new PumpSpeedChangeRequestEventArgs
                {
                    PumpId = this.PumpId,
                    CurrentSpeed = PumpSpeed,
                    RequestedSpeed = newSpeed,
                    ChangeType = SpeedChangeType.Increase,
                    OnCompleted = (success) =>
                    {
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            HideLoading();
                            if (!success)
                            {
                                ShowOperationFailure();
                                Debug.WriteLine($"速度增加失败 | PumpId:{PumpId}");
                            }
                            else
                            {
                                Debug.WriteLine($"速度增加成功 | PumpId:{PumpId} | 新速度:{newSpeed}rpm");
                            }
                        }));
                    }
                });

                e.Handled = true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"增加速度按钮点击处理失败 | PumpId:{PumpId} | 错误:{ex.Message}");
                HideLoading();
                ShowOperationFailure();
            }
        }

        /// <summary>
        /// 减少速度按钮点击事件
        /// </summary>
        private void OnDecreaseButtonClick(object sender, RoutedEventArgs e)
        {
            try
            {
                int newSpeed = PumpSpeed - 10;
                Debug.WriteLine($"减少速度按钮点击 | PumpId:{PumpId} | 当前速度:{PumpSpeed} | 目标速度:{newSpeed}");

                ShowLoading();

                SpeedChangeRequested?.Invoke(this, new PumpSpeedChangeRequestEventArgs
                {
                    PumpId = this.PumpId,
                    CurrentSpeed = PumpSpeed,
                    RequestedSpeed = newSpeed,
                    ChangeType = SpeedChangeType.Decrease,
                    OnCompleted = (success) =>
                    {
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            HideLoading();
                            if (!success)
                            {
                                ShowOperationFailure();
                                Debug.WriteLine($"速度减少失败 | PumpId:{PumpId}");
                            }
                            else
                            {
                                Debug.WriteLine($"速度减少成功 | PumpId:{PumpId} | 新速度:{newSpeed}rpm");
                            }
                        }));
                    }
                });

                e.Handled = true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"减少速度按钮点击处理失败 | PumpId:{PumpId} | 错误:{ex.Message}");
                HideLoading();
                ShowOperationFailure();
            }
        }

        #endregion

        #region 加载和错误状态（无动画版本）

        /// <summary>
        /// 显示加载状态（无动画）
        /// 通过改变LCD数字颜色表示加载中
        /// </summary>
        public void ShowLoading()
        {
            if (_isLoading) return;
            _isLoading = true;
            Debug.WriteLine($"显示加载状态 | PumpId:{PumpId}");

            try
            {
                ClearError();

                // 改变LCD数字颜色为琥珀黄表示加载中
                var speedDisplay = this.FindName("SpeedDisplay") as TextBlock;
                if (speedDisplay != null)
                {
                    speedDisplay.Foreground = new SolidColorBrush(Color.FromRgb(255, 193, 7));
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"显示加载状态失败 | PumpId:{PumpId} | 错误:{ex.Message}");
            }
        }

        /// <summary>
        /// 隐藏加载状态（无动画）
        /// </summary>
        public void HideLoading()
        {
            if (!_isLoading) return;
            _isLoading = false;
            Debug.WriteLine($"隐藏加载状态 | PumpId:{PumpId}");

            try
            {
                // 恢复正常LCD数字颜色
                ResetDisplayColor();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"隐藏加载状态失败 | PumpId:{PumpId} | 错误:{ex.Message}");
            }
        }

        /// <summary>
        /// 重置LCD数字颜色为默认白色
        /// </summary>
        private void ResetDisplayColor()
        {
            var speedDisplay = this.FindName("SpeedDisplay") as TextBlock;
            if (speedDisplay != null)
            {
                speedDisplay.Foreground = new SolidColorBrush(Color.FromRgb(232, 244, 255)); // #E8F4FF
            }
        }

        /// <summary>
        /// 显示操作失败状态（无动画）
        /// 通过改变LCD颜色和光效表示错误
        /// </summary>
        public void ShowOperationFailure()
        {
            Debug.WriteLine($"显示操作失败状态 | PumpId:{PumpId}");

            try
            {
                HideLoading();

                // 直接显示红色错误状态
                var speedDisplay = this.FindName("SpeedDisplay") as TextBlock;
                if (speedDisplay != null)
                {
                    var originalText = speedDisplay.Text;
                    speedDisplay.Text = "Err";
                    speedDisplay.Foreground = new SolidColorBrush(Color.FromRgb(255, 59, 48));

                    // 2秒后自动恢复
                    var timer = new DispatcherTimer
                    {
                        Interval = TimeSpan.FromSeconds(2)
                    };
                    timer.Tick += (s, ev) =>
                    {
                        speedDisplay.Text = originalText;
                        ResetDisplayColor();
                        ResetAllGlowEffects();
                        timer.Stop();
                    };
                    timer.Start();
                }

                // 设置错误光效
                SetErrorGlowEffects();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"显示操作失败状态失败 | PumpId:{PumpId} | 错误:{ex.Message}");
            }
        }

        /// <summary>
        /// 设置错误光效（直接设置，无动画过渡）
        /// </summary>
        private void SetErrorGlowEffects()
        {
            try
            {
                var shellGlow = this.FindName("ShellGlow") as DropShadowEffect;

                Color errorColor = Color.FromRgb(255, 59, 48);
                double errorBlur = 15;

                if (shellGlow != null)
                {
                    shellGlow.Color = errorColor;
                    shellGlow.BlurRadius = errorBlur;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"设置错误光效失败 | PumpId:{PumpId} | 错误:{ex.Message}");
            }
        }

        /// <summary>
        /// 清除错误状态（无动画）
        /// </summary>
        public void ClearError()
        {
            Debug.WriteLine($"清除错误状态 | PumpId:{PumpId}");

            try
            {
                // 重置所有光效
                ResetAllGlowEffects();

                // 重置LCD颜色
                ResetDisplayColor();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"清除错误状态失败 | PumpId:{PumpId} | 错误:{ex.Message}");
            }
        }

        /// <summary>
        /// 重置所有光效为透明状态
        /// </summary>
        private void ResetAllGlowEffects()
        {
            try
            {
                var shellGlow = this.FindName("ShellGlow") as DropShadowEffect;

                if (shellGlow != null)
                {
                    shellGlow.Color = Colors.Transparent;
                    shellGlow.BlurRadius = 0;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"重置光效失败 | PumpId:{PumpId} | 错误:{ex.Message}");
            }
        }

        #endregion

        #region 拖拽控制

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
                    Debug.WriteLine($"开始拖拽蠕动泵 | PumpId:{PumpId}");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"鼠标按下事件处理失败 | PumpId:{PumpId} | 错误:{ex.Message}");
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
                Debug.WriteLine($"鼠标移动事件处理失败 | PumpId:{PumpId} | 错误:{ex.Message}");
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
                Debug.WriteLine($"鼠标释放事件处理失败 | PumpId:{PumpId} | 错误:{ex.Message}");
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
                Debug.WriteLine($"鼠标离开事件处理失败 | PumpId:{PumpId} | 错误:{ex.Message}");
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

                Debug.WriteLine($"蠕动泵拖拽完成 | PumpId:{PumpId} | 位置:({finalPosition.X:F0}, {finalPosition.Y:F0})");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"完成拖拽失败 | PumpId:{PumpId} | 错误:{ex.Message}");
            }
        }

        private void UpdateSelectionVisual(bool isSelected)
        {
            try
            {
                var selectionBorder = this.FindName("SelectionBorder") as Rectangle;
                if (selectionBorder != null)
                    selectionBorder.Visibility = Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"更新选中视觉效果失败 | PumpId:{PumpId} | 错误:{ex.Message}");
            }
        }

        #endregion

        #region IPipeSnapPoints实现

        public List<PipeSnapPoint> GetSnapPoints()
        {
            var snapPoints = new List<PipeSnapPoint>();

            try
            {
                var parent = this.Parent as Canvas;
                if (parent == null)
                {
                    Debug.WriteLine("蠕动泵未添加到Canvas，无法获取吸附点");
                    return snapPoints;
                }

                var pumpLeft = Canvas.GetLeft(this);
                var pumpTop = Canvas.GetTop(this);
                if (double.IsNaN(pumpLeft)) pumpLeft = 0;
                if (double.IsNaN(pumpTop)) pumpTop = 0;

                // 入口 — 泵头左侧IN管口
                snapPoints.Add(new PipeSnapPoint
                {
                    WorldPosition = new Point(pumpLeft + 2, pumpTop + 374),
                    Direction = SnapDirection.Left,
                    Description = $"蠕动泵入口(IN)"
                });

                // 出口 — 泵头左侧OUT管口
                snapPoints.Add(new PipeSnapPoint
                {
                    WorldPosition = new Point(pumpLeft + 2, pumpTop + 426),
                    Direction = SnapDirection.Left,
                    Description = $"蠕动泵出口(OUT)"
                });

                // 顶部
                snapPoints.Add(new PipeSnapPoint
                {
                    WorldPosition = new Point(pumpLeft + 235, pumpTop + 0),
                    Direction = SnapDirection.Up,
                    Description = $"蠕动泵顶部"
                });

                // 底部
                snapPoints.Add(new PipeSnapPoint
                {
                    WorldPosition = new Point(pumpLeft + 235, pumpTop + 500),
                    Direction = SnapDirection.Down,
                    Description = $"蠕动泵底部"
                });

                Debug.WriteLine($"蠕动泵提供吸附点 | PumpId:{PumpId} | 数量:{snapPoints.Count}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"获取吸附点失败 | PumpId:{PumpId} | 错误:{ex.Message}");
            }

            return snapPoints;
        }

        #endregion

        #region INotifyPropertyChanged实现

        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        #endregion
    }
}
#region 事件参数类（保留不变）

/// <summary>
/// 泵状态切换请求事件参数
/// 从UI到设备驱动的请求
/// </summary>
public class PumpStateChangeRequestEventArgs : EventArgs
{
    public string PumpId { get; set; }
    public bool CurrentState { get; set; }
    public bool RequestedState { get; set; }
    public Action<bool> OnCompleted { get; set; }
}

/// <summary>
/// 泵速度变化请求事件参数
/// 从UI到设备驱动的请求
/// </summary>
public class PumpSpeedChangeRequestEventArgs : EventArgs
{
    public string PumpId { get; set; }
    public int CurrentSpeed { get; set; }
    public int RequestedSpeed { get; set; }
    public SpeedChangeType ChangeType { get; set; }
    public Action<bool> OnCompleted { get; set; }
}

/// <summary>
/// 速度变化类型枚举
/// </summary>
public enum SpeedChangeType
{
    Increase,
    Decrease,
    Set
}

/// <summary>
/// 泵拖拽事件参数
/// </summary>
public class PumpDragEventArgs : EventArgs
{
    public string PumpId { get; set; }
    public Point StartPosition { get; set; }
    public Point CurrentPosition { get; set; }
}

/// <summary>
/// 泵状态变化事件参数
/// </summary>
public class PumpStateChangedEventArgs : EventArgs
{
    public string PumpId { get; set; }
    public bool IsRunning { get; set; }
    public int Speed { get; set; }
}

#endregion
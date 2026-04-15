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
        private int _pumpSpeed = 20;

        // 动画相关字段保留但不使用
        private Storyboard _pistonStoryboard;
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
        /// 泵速度范围 0-100
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
                // 注释掉动画速度更新
                // control.UpdatePistonAnimationSpeed();
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
                // 注释掉错误重置定时器（已不需要）
                /*
                _errorResetTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(3)
                };
                _errorResetTimer.Tick += (s, e) =>
                {
                    ResetErrorState();
                    _errorResetTimer.Stop();
                };
                */

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

                Debug.WriteLine($"柱塞泵硬件加速已启用 | PumpId:{PumpId}");
            }

            Debug.WriteLine($"柱塞泵控件已创建 | PumpId:{PumpId}");
        }

        private void OnPumpControlLoaded(object sender, RoutedEventArgs e)
        {
            try
            {
                // 注释掉动画资源加载
                /*
                _pistonStoryboard = this.Resources["PistonMotionStoryboard"] as Storyboard;
                _errorBlinkAnimation = this.Resources["ErrorBlinkAnimation"] as Storyboard;
                _loadingAnimation = this.Resources["LoadingAnimation"] as Storyboard;
                _errorStateHoldAnimation = this.Resources["ErrorStateHoldAnimation"] as Storyboard;

                if (_pistonStoryboard == null)
                {
                    Debug.WriteLine("警告: 柱塞动画资源未找到");
                }

                if (_errorBlinkAnimation == null || _loadingAnimation == null)
                {
                    Debug.WriteLine("警告: 错误或加载动画资源未找到");
                }
                */

                UpdatePumpSpeedDisplay();
                UpdatePumpRunningState();

                Debug.WriteLine($"柱塞泵控件已加载完成 | PumpId:{PumpId}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"柱塞泵控件加载失败 | PumpId:{PumpId} | 错误:{ex.Message}");
            }
        }

        #endregion

        #region 泵控制方法（保留原有公共方法）

        public void StartPump()
        {
            try
            {
                if (!IsRunning)
                {
                    IsRunning = true;
                    Debug.WriteLine($"柱塞泵已启动 | PumpId:{PumpId} | 速度:{PumpSpeed}");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"启动柱塞泵失败 | PumpId:{PumpId} | 错误:{ex.Message}");
            }
        }

        public void StopPump()
        {
            try
            {
                if (IsRunning)
                {
                    IsRunning = false;
                    Debug.WriteLine($"柱塞泵已停止 | PumpId:{PumpId}");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"停止柱塞泵失败 | PumpId:{PumpId} | 错误:{ex.Message}");
            }
        }

        public void SetSpeed(int speed)
        {
            try
            {
                PumpSpeed = speed;
                Debug.WriteLine($"柱塞泵速度已设置 | PumpId:{PumpId} | 速度:{PumpSpeed}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"设置柱塞泵速度失败 | PumpId:{PumpId} | 错误:{ex.Message}");
            }
        }

        #endregion

        #region UI状态更新方法

        /// <summary>
        /// 更新泵运行状态（设备驱动到UI）
        /// 无动画版本，仅更新按钮颜色
        /// </summary>
        private void UpdatePumpRunningState()
        {
            try
            {
                // 注释掉柱塞动画，改为仅更新按钮颜色
                /*
                if (_pistonStoryboard != null)
                {
                    if (IsRunning)
                    {
                        Timeline.SetDesiredFrameRate(_pistonStoryboard, 30);
                        _pistonStoryboard.Begin(this, true);
                        Debug.WriteLine("柱塞动画已启动（30fps GPU加速）");
                    }
                    else
                    {
                        _pistonStoryboard.Stop(this);
                        Debug.WriteLine("柱塞动画已停止");
                    }
                }
                */

                // 只更新电源按钮颜色
                UpdatePowerButtonColor();
                //更改外壳颜色
                UpdatePumpColors();

                Debug.WriteLine($"泵运行状态已更新 | PumpId:{PumpId} | 状态:{(IsRunning ? "运行中" : "已停止")}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"更新泵运行状态失败 | PumpId:{PumpId} | 错误:{ex.Message}");
            }
        }

        /// <summary>
        /// 更新电源按钮颜色
        /// 运行时显示红色，停止时显示绿色
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
                Debug.WriteLine($"更新电源按钮颜色失败 | PumpId:{PumpId} | 错误:{ex.Message}");
            }
        }

        /// <summary>
        /// 更新泵的外壳和气缸颜色
        /// 运行时显示蓝绿色,停止时显示灰色
        /// </summary>
        private void UpdatePumpColors()
        {
            try
            {
                // 获取主外壳元素
                var pumpShell = this.FindName("PumpShell") as Rectangle;
                var pumpCurvedShell = this.FindName("PumpCurvedShell") as System.Windows.Shapes.Path;
                var pumpBody = this.FindName("PumpBody") as Rectangle;

                // 获取气缸元素
                var cylinder1 = this.FindName("Cylinder1") as Rectangle;
                var cylinder2 = this.FindName("Cylinder2") as Rectangle;
                var cylinder3 = this.FindName("Cylinder3") as Rectangle;

                if (IsRunning)
                {
                    // 运行状态 - 蓝绿色渐变
                    var runningShellBrush = this.Resources["RunningShellGradient"] as LinearGradientBrush;
                    var runningCylinderBrush = this.Resources["RunningCylinderGradient"] as RadialGradientBrush;

                    if (pumpShell != null && runningShellBrush != null)
                        pumpShell.Fill = runningShellBrush;

                    if (pumpCurvedShell != null && runningShellBrush != null)
                        pumpCurvedShell.Fill = runningShellBrush;

                    if (pumpBody != null && runningShellBrush != null)
                        pumpBody.Fill = runningShellBrush;

                    if (cylinder1 != null && runningCylinderBrush != null)
                        cylinder1.Fill = runningCylinderBrush;

                    if (cylinder2 != null && runningCylinderBrush != null)
                        cylinder2.Fill = runningCylinderBrush;

                    if (cylinder3 != null && runningCylinderBrush != null)
                        cylinder3.Fill = runningCylinderBrush;
                }
                else
                {
                    // 停止状态 - 薰衣草靛蓝
                    var stoppedShellBrush = this.Resources["StoppedShellGradient"] as LinearGradientBrush;
                    var stoppedCylinderBrush = this.Resources["StoppedCylinderGradient"] as LinearGradientBrush;

                    Brush shellFill = (Brush)stoppedShellBrush
                        ?? new SolidColorBrush(Color.FromRgb(0xDF, 0xE3, 0xF0));
                    Brush bodyFill = (Brush)stoppedShellBrush
                        ?? new SolidColorBrush(Color.FromRgb(0xCD, 0xD3, 0xE8));

                    if (pumpShell != null)
                        pumpShell.Fill = shellFill;

                    if (pumpCurvedShell != null)
                        pumpCurvedShell.Fill = shellFill;

                    if (pumpBody != null)
                        pumpBody.Fill = bodyFill;

                    if (cylinder1 != null && stoppedCylinderBrush != null)
                        cylinder1.Fill = stoppedCylinderBrush;

                    if (cylinder2 != null && stoppedCylinderBrush != null)
                        cylinder2.Fill = stoppedCylinderBrush;

                    if (cylinder3 != null && stoppedCylinderBrush != null)
                        cylinder3.Fill = stoppedCylinderBrush;
                }

                Debug.WriteLine($"泵颜色已更新 | PumpId:{PumpId} | 状态:{(IsRunning ? "蓝绿色(运行中)" : "灰色(已停止)")}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"更新泵颜色失败 | PumpId:{PumpId} | 错误:{ex.Message}");
            }
        }
        /// <summary>
        /// 更新泵速度显示
        /// </summary>
        private void UpdatePumpSpeedDisplay()
        {
            try
            {
                Debug.WriteLine($"泵速度显示已更新 | PumpId:{PumpId} | 速度:{PumpSpeed}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"更新速度显示失败 | PumpId:{PumpId} | 错误:{ex.Message}");
            }
        }

        // 注释掉动画速度更新方法（已不需要）
        /*
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
                    Debug.WriteLine($"动画速率调整为: {speedRatio:F2}x (速度: {PumpSpeed})");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"更新动画速度失败: {ex.Message}");
            }
        }
        */

        #endregion

        #region 按钮事件处理（UI到设备驱动）

        /// <summary>
        /// 电源按钮点击事件
        /// 请求切换泵的运行状态
        /// </summary>
        private void OnPowerButtonClick(object sender, RoutedEventArgs e)
        {
            try
            {
                Debug.WriteLine($"电源按钮点击 | PumpId:{PumpId} | 当前状态:{IsRunning}");

                // 显示加载状态（无动画）
                ShowLoading();

                // 触发状态切换请求事件（带完成回调）
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
                Debug.WriteLine($"电源按钮点击处理失败 | PumpId:{PumpId} | 错误:{ex.Message}");
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

                // 显示加载状态（无动画）
                ShowLoading();

                // 触发速度变化请求事件（带完成回调）
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
                                Debug.WriteLine($"速度增加失败 | PumpId:{PumpId}");
                            }
                            else
                            {
                                Debug.WriteLine($"速度增加成功 | PumpId:{PumpId} | 新速度:{newSpeed}");
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

                // 显示加载状态（无动画）
                ShowLoading();

                // 触发速度变化请求事件（带完成回调）
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
                                Debug.WriteLine($"速度减少失败 | PumpId:{PumpId}");
                            }
                            else
                            {
                                Debug.WriteLine($"速度减少成功 | PumpId:{PumpId} | 新速度:{newSpeed}");
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
        /// 通过改变柱塞杆颜色表示加载中
        /// </summary>
        public void ShowLoading()
        {
            if (_isLoading) return;
            _isLoading = true;
            Debug.WriteLine($"显示加载状态 | PumpId:{PumpId}");

            try
            {
                ClearError();

                // 注释掉所有动画，改为直接设置状态
                /*
                // 停止正常动画
                if (_pistonStoryboard != null && IsRunning)
                {
                    _pistonStoryboard.Pause(this);
                }

                // 启动加载动画
                if (_loadingAnimation != null)
                {
                    _loadingAnimation.Begin(this, true);
                }
                */

                // 新代码：直接改变柱塞杆颜色表示加载中（黄色）
                var piston1 = this.FindName("Piston1Rod") as Rectangle;
                var piston2 = this.FindName("Piston2Rod") as Rectangle;
                var piston3 = this.FindName("Piston3Rod") as Rectangle;

                if (piston1 != null) piston1.Fill = new SolidColorBrush(Color.FromRgb(255, 193, 7));
                if (piston2 != null) piston2.Fill = new SolidColorBrush(Color.FromRgb(255, 193, 7));
                if (piston3 != null) piston3.Fill = new SolidColorBrush(Color.FromRgb(255, 193, 7));
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
                // 注释掉所有动画
                /*
                // 停止加载动画
                if (_loadingAnimation != null)
                {
                    _loadingAnimation.Stop(this);
                }

                // 恢复正常动画
                if (_pistonStoryboard != null && IsRunning)
                {
                    _pistonStoryboard.Resume(this);
                }
                */

                // 恢复正常柱塞杆颜色
                ResetPistonColors();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"隐藏加载状态失败 | PumpId:{PumpId} | 错误:{ex.Message}");
            }
        }

        /// <summary>
        /// 重置柱塞杆颜色为默认灰色
        /// </summary>
        private void ResetPistonColors()
        {
            var piston1 = this.FindName("Piston1Rod") as Rectangle;
            var piston2 = this.FindName("Piston2Rod") as Rectangle;
            var piston3 = this.FindName("Piston3Rod") as Rectangle;

            if (piston1 != null) piston1.Fill = new SolidColorBrush(Color.FromRgb(176, 188, 195));
            if (piston2 != null) piston2.Fill = new SolidColorBrush(Color.FromRgb(176, 188, 195));
            if (piston3 != null) piston3.Fill = new SolidColorBrush(Color.FromRgb(176, 188, 195));
        }

        /// <summary>
        /// 显示操作失败状态（无动画）
        /// 通过改变柱塞杆颜色和光效表示错误
        /// </summary>
        public void ShowOperationFailure()
        {
            Debug.WriteLine($"显示操作失败状态 | PumpId:{PumpId}");

            try
            {
                HideLoading();

                // 注释掉所有动画，改为直接显示红色状态
                /*
                // 停止正常动画
                if (_pistonStoryboard != null && IsRunning)
                {
                    _pistonStoryboard.Stop(this);
                }

                // 停止之前的错误状态保持动画
                _errorStateHoldAnimation?.Stop(this);

                // 启动错误闪烁动画
                if (_errorBlinkAnimation != null)
                {
                    _errorBlinkAnimation.Completed -= OnErrorBlinkCompleted;
                    _errorBlinkAnimation.Completed += OnErrorBlinkCompleted;
                    _errorBlinkAnimation.Begin(this, true);
                }
                */

                // 新代码：直接显示红色错误状态
                var piston1 = this.FindName("Piston1Rod") as Rectangle;
                var piston2 = this.FindName("Piston2Rod") as Rectangle;
                var piston3 = this.FindName("Piston3Rod") as Rectangle;

                if (piston1 != null) piston1.Fill = new SolidColorBrush(Color.FromRgb(255, 59, 48));
                if (piston2 != null) piston2.Fill = new SolidColorBrush(Color.FromRgb(255, 59, 48));
                if (piston3 != null) piston3.Fill = new SolidColorBrush(Color.FromRgb(255, 59, 48));

                // 直接设置光效（可选）
                SetErrorGlowEffects();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"显示操作失败状态失败 | PumpId:{PumpId} | 错误:{ex.Message}");
            }
        }

        // 注释掉错误闪烁完成事件（已不需要）
        /*
        private void OnErrorBlinkCompleted(object sender, EventArgs e)
        {
            Debug.WriteLine($"错误闪烁完成，进入持续错误状态 | PumpId:{PumpId}");
            _errorStateHoldAnimation?.Begin(this, true);
        }
        */

        /// <summary>
        /// 设置错误光效（直接设置，无动画过渡）
        /// </summary>
        private void SetErrorGlowEffects()
        {
            try
            {
                var shellGlow = this.FindName("ShellGlow") as DropShadowEffect;
                var baseGlow = this.FindName("BaseGlow") as DropShadowEffect;
                var cylinder1Glow = this.FindName("Cylinder1Glow") as DropShadowEffect;
                var cylinder2Glow = this.FindName("Cylinder2Glow") as DropShadowEffect;
                var cylinder3Glow = this.FindName("Cylinder3Glow") as DropShadowEffect;

                Color errorColor = Color.FromRgb(255, 59, 48);
                double errorBlur = 15;

                if (shellGlow != null)
                {
                    shellGlow.Color = errorColor;
                    shellGlow.BlurRadius = errorBlur;
                }
                if (baseGlow != null)
                {
                    baseGlow.Color = errorColor;
                    baseGlow.BlurRadius = errorBlur;
                }
                if (cylinder1Glow != null)
                {
                    cylinder1Glow.Color = errorColor;
                    cylinder1Glow.BlurRadius = errorBlur;
                }
                if (cylinder2Glow != null)
                {
                    cylinder2Glow.Color = errorColor;
                    cylinder2Glow.BlurRadius = errorBlur;
                }
                if (cylinder3Glow != null)
                {
                    cylinder3Glow.Color = errorColor;
                    cylinder3Glow.BlurRadius = errorBlur;
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
                // 注释掉所有动画停止操作
                /*
                // 停止错误动画
                _errorBlinkAnimation?.Stop(this);
                _errorStateHoldAnimation?.Stop(this);

                // 恢复正常动画
                if (_pistonStoryboard != null && IsRunning)
                {
                    _pistonStoryboard.Begin(this, true);
                }
                */

                // 重置所有光效
                ResetAllGlowEffects();

                // 重置柱塞杆颜色
                ResetPistonColors();
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
                var baseGlow = this.FindName("BaseGlow") as DropShadowEffect;
                var cylinder1Glow = this.FindName("Cylinder1Glow") as DropShadowEffect;
                var cylinder2Glow = this.FindName("Cylinder2Glow") as DropShadowEffect;
                var cylinder3Glow = this.FindName("Cylinder3Glow") as DropShadowEffect;

                if (shellGlow != null)
                {
                    shellGlow.Color = Colors.Transparent;
                    shellGlow.BlurRadius = 0;
                }
                if (baseGlow != null)
                {
                    baseGlow.Color = Colors.Transparent;
                    baseGlow.BlurRadius = 0;
                }
                if (cylinder1Glow != null)
                {
                    cylinder1Glow.Color = Colors.Transparent;
                    cylinder1Glow.BlurRadius = 0;
                }
                if (cylinder2Glow != null)
                {
                    cylinder2Glow.Color = Colors.Transparent;
                    cylinder2Glow.BlurRadius = 0;
                }
                if (cylinder3Glow != null)
                {
                    cylinder3Glow.Color = Colors.Transparent;
                    cylinder3Glow.BlurRadius = 0;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"重置光效失败 | PumpId:{PumpId} | 错误:{ex.Message}");
            }
        }

        // 注释掉重置错误状态方法（已不需要）
        /*
        private void ResetErrorState()
        {
            Debug.WriteLine($"重置错误状态 | PumpId:{PumpId}");
        }
        */

        #endregion

        #region 拖拽控制（保留原有代码）

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
                    Debug.WriteLine($"开始拖拽柱塞泵 | PumpId:{PumpId}");
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

                Debug.WriteLine($"柱塞泵拖拽完成 | PumpId:{PumpId} | 位置:({finalPosition.X:F0}, {finalPosition.Y:F0})");
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
                if (isSelected)
                {
                    Debug.WriteLine($"柱塞泵已选中 | PumpId:{PumpId}");
                }
                else
                {
                    Debug.WriteLine($"柱塞泵取消选中 | PumpId:{PumpId}");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"更新选中视觉效果失败 | PumpId:{PumpId} | 错误:{ex.Message}");
            }
        }

        #endregion

        #region IPipeSnapPoints实现（保留原有代码）

        public List<PipeSnapPoint> GetSnapPoints()
        {
            var snapPoints = new List<PipeSnapPoint>();

            try
            {
                var parent = this.Parent as Canvas;
                if (parent == null)
                {
                    Debug.WriteLine("柱塞泵未添加到Canvas，无法获取吸附点");
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

                Debug.WriteLine($"柱塞泵提供吸附点 | PumpId:{PumpId} | 数量:{snapPoints.Count}");
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
}
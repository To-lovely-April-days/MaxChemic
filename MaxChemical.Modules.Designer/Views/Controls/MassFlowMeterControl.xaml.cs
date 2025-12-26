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

        private Storyboard _fluidFlowAnimation;
        private Storyboard _dataGlowAnimation;

        public string MeterId { get; set; }

        #endregion

        #region 新增：UI → 设备驱动的请求事件

        public event EventHandler<FlowMeterStateChangeRequestEventArgs> StateChangeRequested;
        public event EventHandler<FlowMeterTargetFlowChangeRequestEventArgs> TargetFlowChangeRequested;

        #endregion

        #region 新增：加载和错误动画资源

        //private Storyboard _loadingAnimation;
        //private Storyboard _errorBlinkAnimation;
        //private Storyboard _errorStateHoldAnimation;
        //private DispatcherTimer _errorResetTimer;
        private bool _isLoading = false;

        public bool IsLoading => _isLoading;

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

                // 注释: 错误重置定时器已禁用
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

                this.Loaded += OnControlLoaded;
                this.MouseEnter += OnMeterMouseEnter;
                this.MouseLeave += OnMeterMouseLeave;
                SetupRotateAnchorEvents();

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

                // 注释: 液体动画层和指针缓存禁用已移除
                /*
                if (FluidFlowContainer != null)
                {
                    FluidFlowContainer.CacheMode = null;
                }

                if (CurrentPointer != null)
                {
                    CurrentPointer.CacheMode = null;
                }
                */

                Debug.WriteLine($"流量计 {MeterId} 硬件加速已启用");
            }
        }



        private void OnControlLoaded(object sender, RoutedEventArgs e)
        {
            Debug.WriteLine("OnControlLoaded 开始");

            // 初始化显示
            UpdatePointerAngle(CurrentFlow);
            UpdateCurrentValueText(CurrentFlow);
            UpdateTargetValueText(TargetFlow);

            // 注释: 数据发光动画已禁用
            /*
            _dataGlowAnimation = this.Resources["DataGlowAnimation"] as Storyboard;
            _dataGlowAnimation?.Begin();
            */

            // 注释: 加载和错误动画资源加载已禁用
            /*
            _loadingAnimation = this.Resources["LoadingAnimation"] as Storyboard;
            _errorBlinkAnimation = this.Resources["ErrorBlinkAnimation"] as Storyboard;
            _errorStateHoldAnimation = this.Resources["ErrorStateHoldAnimation"] as Storyboard;

            if (_errorBlinkAnimation != null)
            {
                _errorBlinkAnimation.Completed += OnErrorBlinkCompleted;
            }
            */

            // 根据当前流量值更新颜色
            UpdateMeterColorsByFlowValue(CurrentFlow);

            Debug.WriteLine("OnControlLoaded 结束");
        }


        #endregion

        // 新增: 更新流量计颜色方法
        private void UpdateMeterColors()
        {
            try
            {
                // 获取主外壳元素
                var meterShell = this.FindName("MeterShell") as Rectangle;
                var dialInnerCircle = this.FindName("DialInnerCircle") as Ellipse;

                if (IsFlowing)
                {
                    // 运行状态 - 蓝绿色渐变
                    var runningShellBrush = this.Resources["RunningShellGradient"] as LinearGradientBrush;
                    var runningDialBrush = this.Resources["RunningDialGradient"] as RadialGradientBrush;

                    if (meterShell != null && runningShellBrush != null)
                        meterShell.Fill = runningShellBrush;

                    if (dialInnerCircle != null && runningDialBrush != null)
                        dialInnerCircle.Fill = runningDialBrush;

                    Debug.WriteLine($"流量计颜色已更新 | MeterId:{MeterId} | 状态:蓝绿色(运行中)");
                }
                else
                {
                    // 停止状态 - 灰色渐变
                    if (meterShell != null)
                        meterShell.Fill = new SolidColorBrush(Color.FromRgb(240, 244, 246));

                    var stoppedDialBrush = this.Resources["StoppedDialGradient"] as RadialGradientBrush;
                    if (dialInnerCircle != null && stoppedDialBrush != null)
                        dialInnerCircle.Fill = stoppedDialBrush;

                    Debug.WriteLine($"流量计颜色已更新 | MeterId:{MeterId} | 状态:灰色(已停止)");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"更新流量计颜色失败 | MeterId:{MeterId} | 错误:{ex.Message}");
            }
        }

        #region  新增：加载和错误动画方法

        /// <summary>
        /// 显示加载动画
        /// </summary>
        public void ShowLoading()
        {
            if (_isLoading) return;
            _isLoading = true;
            Debug.WriteLine($"显示加载状态(无动画) | MeterId:{MeterId}");

            // 注释: 仅改变外壳颜色为黄色表示加载中
            var meterShell = this.FindName("MeterShell") as Rectangle;
            if (meterShell != null)
                meterShell.Fill = new SolidColorBrush(Color.FromRgb(255, 193, 7));
        }


        /// <summary>
        /// 隐藏加载动画
        /// </summary>
        public void HideLoading()
        {
            if (!_isLoading) return;
            _isLoading = false;
            Debug.WriteLine($"隐藏加载状态(无动画) | MeterId:{MeterId}");

            // 恢复正常颜色 - 根据当前流量值
            UpdateMeterColorsByFlowValue(CurrentFlow);
        }

        /// <summary>
        /// 显示操作失败动画
        /// </summary>
        public void ShowOperationFailure()
        {
            Debug.WriteLine($"显示操作失败状态(无动画) | MeterId:{MeterId}");

            HideLoading();

            // 注释: 仅改变外壳颜色为红色表示错误
            var meterShell = this.FindName("MeterShell") as Rectangle;
            if (meterShell != null)
                meterShell.Fill = new SolidColorBrush(Color.FromRgb(255, 59, 48));
        }


        ///// <summary>
        ///// 错误闪烁完成后启动持续状态
        ///// </summary>
        //private void OnErrorBlinkCompleted(object sender, EventArgs e)
        //{
        //    Debug.WriteLine($" 错误闪烁完成，进入持续错误状态");
        //    _errorStateHoldAnimation?.Begin(this, true);
        //}

        public void ClearError()
        {
            Debug.WriteLine($"清除错误状态 | MeterId:{MeterId}");

            // 恢复正常颜色 - 根据当前流量值
            UpdateMeterColorsByFlowValue(CurrentFlow);
        }
        ///// <summary>
        ///// 重置所有光效
        ///// </summary>
        //private void ResetAllGlowEffects()
        //{
        //    try
        //    {
        //        var shellGlow = this.FindName("ShellGlow") as DropShadowEffect;
        //        if (shellGlow != null)
        //        {
        //            shellGlow.Color = Colors.Transparent;
        //            shellGlow.BlurRadius = 0;
        //        }

        //        // 恢复数据发光
        //        if (_dataGlowAnimation != null)
        //        {
        //            _dataGlowAnimation.Begin();
        //        }
        //    }
        //    catch (Exception ex)
        //    {
        //        Debug.WriteLine($" 重置光效失败: {ex.Message}");
        //    }
        //}

        //private void ResetErrorState()
        //{
        //    Debug.WriteLine($" 重置错误状态");
        //}

        #endregion


        #region 动画控制

        private static void OnIsFlowingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is MassFlowMeterControl meter)
            {
                bool isFlowing = (bool)e.NewValue;

                // 注释: 颜色更新已移至CurrentFlow属性变化时处理
                // 此处仅触发流动状态变化事件
                meter.FlowStateChanged?.Invoke(meter, new FlowStateChangedEventArgs
                {
                    MeterId = meter.MeterId,
                    IsFlowing = isFlowing
                });

                Debug.WriteLine($"流量计 {meter.MeterId} IsFlowing属性变更: {(isFlowing ? "启动" : "停止")}");
            }
        }

        //private void UpdateFlowAnimationState(bool isFlowing)
        //{
        //    Debug.WriteLine($" UpdateFlowAnimationState 调用: isFlowing={isFlowing}");

        //    if (FluidFlowContainer == null)
        //    {
        //        Debug.WriteLine(" FluidFlowContainer 为 NULL!");
        //        return;
        //    }

        //    Debug.WriteLine($" FluidFlowContainer 存在, Children={FluidFlowContainer.Children.Count}");

        //    if (isFlowing)
        //    {
        //        // 显示容器
        //        FluidFlowContainer.Visibility = Visibility.Visible;
        //        Debug.WriteLine(" 容器已显示");

        //        // 创建并启动动画
        //        CreateAndStartFluidAnimation();
        //    }
        //    else
        //    {
        //        // 停止动画
        //        StopAllFluidAnimations();

        //        // 隐藏容器
        //        FluidFlowContainer.Visibility = Visibility.Collapsed;
        //        Debug.WriteLine(" 容器已隐藏");
        //    }
        //}

        //private void CreateAndStartFluidAnimation()
        //{
        //    Debug.WriteLine("🎬 开始创建液体流动动画...");

        //    StopAllFluidAnimations();

        //    _fluidFlowAnimation = new Storyboard
        //    {
        //        RepeatBehavior = RepeatBehavior.Forever
        //    };

        //    //  设置动画帧率为30fps（降低CPU占用）
        //    Timeline.SetDesiredFrameRate(_fluidFlowAnimation, 30);

        //    // 1. 渐变流动动画
        //    if (FluidGradientTransform != null)
        //    {
        //        var gradientAnim = new DoubleAnimation
        //        {
        //            From = 0,
        //            To = 100,
        //            Duration = TimeSpan.FromSeconds(2),
        //            RepeatBehavior = RepeatBehavior.Forever
        //        };
        //        Storyboard.SetTarget(gradientAnim, FluidGradientTransform);
        //        Storyboard.SetTargetProperty(gradientAnim, new PropertyPath("X"));
        //        _fluidFlowAnimation.Children.Add(gradientAnim);
        //        Debug.WriteLine(" 渐变动画已添加");
        //    }

        //    // 2. 波浪动画
        //    if (WaveTransform1 != null)
        //    {
        //        var waveAnim = new DoubleAnimation
        //        {
        //            From = 0,
        //            To = 100,
        //            Duration = TimeSpan.FromSeconds(2),
        //            RepeatBehavior = RepeatBehavior.Forever
        //        };
        //        Storyboard.SetTarget(waveAnim, WaveTransform1);
        //        Storyboard.SetTargetProperty(waveAnim, new PropertyPath("X"));
        //        _fluidFlowAnimation.Children.Add(waveAnim);
        //    }

        //    if (WaveTransform2 != null)
        //    {
        //        var waveAnim = new DoubleAnimation
        //        {
        //            From = 0,
        //            To = 100,
        //            Duration = TimeSpan.FromSeconds(2),
        //            RepeatBehavior = RepeatBehavior.Forever
        //        };
        //        Storyboard.SetTarget(waveAnim, WaveTransform2);
        //        Storyboard.SetTargetProperty(waveAnim, new PropertyPath("X"));
        //        _fluidFlowAnimation.Children.Add(waveAnim);
        //    }

        //    // 3. 气泡动画
        //    AddBubbleAnimation(Bubble1, 4.0, 20, 8, 2.0, 0);
        //    AddBubbleAnimation(Bubble2, 3.5, 24, 12, 1.8, 0.8);
        //    AddBubbleAnimation(Bubble3, 4.2, 16, 6, 2.2, 1.5);
        //    AddBubbleAnimation(Bubble4, 3.8, 22, 10, 1.9, 2.2);
        //    AddBubbleAnimation(Bubble5, 4.5, 18, 7, 2.5, 0.5);

        //    try
        //    {
        //        _fluidFlowAnimation.Begin();
        //        Debug.WriteLine(" 液体流动动画已启动（30fps GPU加速）! ");
        //    }
        //    catch (Exception ex)
        //    {
        //        Debug.WriteLine($" 动画启动失败: {ex.Message}");
        //    }
        //}

        //private void AddBubbleAnimation(Ellipse bubble, double duration, double fromTop, double toTop,
        //                                 double verticalDuration, double delay)
        //{
        //    if (bubble == null)
        //    {
        //        Debug.WriteLine(" 气泡元素为 NULL,跳过");
        //        return;
        //    }

        //    // 水平移动动画
        //    var leftAnim = new DoubleAnimation
        //    {
        //        From = -10,
        //        To = 440,
        //        Duration = TimeSpan.FromSeconds(duration),
        //        BeginTime = TimeSpan.FromSeconds(delay),
        //        RepeatBehavior = RepeatBehavior.Forever
        //    };
        //    Storyboard.SetTarget(leftAnim, bubble);
        //    Storyboard.SetTargetProperty(leftAnim, new PropertyPath("(Canvas.Left)"));
        //    _fluidFlowAnimation.Children.Add(leftAnim);

        //    // 垂直波动动画
        //    var topAnim = new DoubleAnimation
        //    {
        //        From = fromTop,
        //        To = toTop,
        //        Duration = TimeSpan.FromSeconds(verticalDuration),
        //        AutoReverse = true,
        //        RepeatBehavior = RepeatBehavior.Forever
        //    };
        //    Storyboard.SetTarget(topAnim, bubble);
        //    Storyboard.SetTargetProperty(topAnim, new PropertyPath("(Canvas.Top)"));
        //    _fluidFlowAnimation.Children.Add(topAnim);

        //    Debug.WriteLine($" 气泡动画已添加: {bubble.Name ?? "未命名气泡"}");
        //}

        //private void StopAllFluidAnimations()
        //{
        //    if (_fluidFlowAnimation != null)
        //    {
        //        _fluidFlowAnimation.Stop();
        //        _fluidFlowAnimation = null;
        //        Debug.WriteLine(" 液体动画已停止");
        //    }

        //    // 重置所有 Transform 位置
        //    if (FluidGradientTransform != null)
        //        FluidGradientTransform.X = 0;
        //    if (WaveTransform1 != null)
        //        WaveTransform1.X = 0;
        //    if (WaveTransform2 != null)
        //        WaveTransform2.X = 0;

        //    // 重置气泡位置
        //    ResetBubblePosition(Bubble1);
        //    ResetBubblePosition(Bubble2);
        //    ResetBubblePosition(Bubble3);
        //    ResetBubblePosition(Bubble4);
        //    ResetBubblePosition(Bubble5);
        //}

        //private void ResetBubblePosition(Ellipse bubble)
        //{
        //    if (bubble != null)
        //    {
        //        Canvas.SetLeft(bubble, -10);
        //    }
        //}

        //private void StartFlowAnimation()
        //{
        //    IsFlowing = true;
        //}

        //private void StopFlowAnimation()
        //{
        //    IsFlowing = false;
        //}

        #endregion

        #region 锚点显示控制

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
            if (RotateAnchor != null)
            {
                RotateAnchor.Opacity = 0.7;
            }
            if (RotateAnchorLine != null)
            {
                RotateAnchorLine.Opacity = 0.7;
            }
        }

        private void HideAllAnchors()
        {
            if (RotateAnchor != null)
            {
                RotateAnchor.Opacity = 0;
            }
            if (RotateAnchorLine != null)
            {
                RotateAnchorLine.Opacity = 0;
            }
        }

        #endregion

        #region 状态变化处理

        private static void OnIsSelectedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is MassFlowMeterControl meter)
            {
                meter.UpdateSelectionVisual((bool)e.NewValue);
            }
        }

        private void UpdateSelectionVisual(bool isSelected)
        {
            if (SelectionBorder != null)
            {
                if (isSelected)
                {
                    SelectionBorder.Visibility = Visibility.Visible;
                    ShowAllAnchors();
                }
                else
                {
                    SelectionBorder.Visibility = Visibility.Collapsed;
                    if (!this.IsMouseOver && !_isRotating)
                    {
                        HideAllAnchors();
                    }
                }
            }
        }

        private static void OnCurrentFlowChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is MassFlowMeterControl meter)
            {
                double newValue = (double)e.NewValue;
                meter.UpdatePointerAngle(newValue);
                meter.UpdateCurrentValueText(newValue);

                // 根据实时流量值更新颜色状态
                meter.UpdateMeterColorsByFlowValue(newValue);

                Debug.WriteLine($"流量计 {meter.MeterId} 当前流量: {newValue:F1} g/min");
            }
        }
        /// <summary>
        /// 根据实时流量值更新流量计颜色
        /// 注释: 只要有流量数据(CurrentFlow > 0)就显示运行状态颜色
        /// </summary>
        private void UpdateMeterColorsByFlowValue(double flowValue)
        {
            try
            {
                var meterShell = this.FindName("MeterShell") as Rectangle;
                var dialInnerCircle = this.FindName("DialInnerCircle") as Ellipse;

                if (flowValue > 0)
                {
                    // 有流量 - 运行状态 - 蓝绿色渐变
                    var runningShellBrush = this.Resources["RunningShellGradient"] as LinearGradientBrush;
                    var runningDialBrush = this.Resources["RunningDialGradient"] as RadialGradientBrush;

                    if (meterShell != null && runningShellBrush != null)
                        meterShell.Fill = runningShellBrush;

                    if (dialInnerCircle != null && runningDialBrush != null)
                        dialInnerCircle.Fill = runningDialBrush;

                    Debug.WriteLine($"流量计颜色已更新 | MeterId:{MeterId} | 流量:{flowValue:F1} | 状态:蓝绿色(运行中)");
                }
                else
                {
                    // 无流量 - 停止状态 - 灰色渐变
                    var stoppedShellBrush = this.Resources["StoppedShellGradient"] as LinearGradientBrush;
                    var stoppedDialBrush = this.Resources["StoppedDialGradient"] as RadialGradientBrush;

                    if (meterShell != null && stoppedShellBrush != null)
                        meterShell.Fill = stoppedShellBrush;

                    if (dialInnerCircle != null && stoppedDialBrush != null)
                        dialInnerCircle.Fill = stoppedDialBrush;

                    Debug.WriteLine($"流量计颜色已更新 | MeterId:{MeterId} | 流量:0.0 | 状态:灰色(已停止)");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"更新流量计颜色失败 | MeterId:{MeterId} | 错误:{ex.Message}");
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

            // 注释: 动画过渡已禁用, 直接设置角度
            // var animation = new DoubleAnimation { ... };
            // PointerRotation.BeginAnimation(RotateTransform.AngleProperty, animation);

            PointerRotation.Angle = angle;
        }

        private void UpdateCurrentValueText(double value)
        {
            if (CurrentValueText != null)
            {
                CurrentValueText.Text = $"{value:F1} g/min";
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

        #region 按钮事件处理

        private void OnIncreaseButtonClick(object sender, RoutedEventArgs e)
        {
            try
            {
                double newFlow = Math.Min(TargetFlow + 1, MaxFlow);
                Debug.WriteLine($"⬆️ 增加流量: {TargetFlow:F1} → {newFlow:F1}");

                //  显示加载动画
                ShowLoading();

                //  触发目标流量变化请求事件
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
                                Debug.WriteLine($" 流量增加失败");
                            }
                            else
                            {
                                Debug.WriteLine($" 流量增加成功: {newFlow:F1}");
                            }
                        }));
                    }
                });

                e.Handled = true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($" 增加流量失败: {ex.Message}");
                HideLoading();
                ShowOperationFailure();
            }
        }

        private void OnDecreaseButtonClick(object sender, RoutedEventArgs e)
        {
            try
            {
                double newFlow = Math.Max(TargetFlow - 1, 0);
                Debug.WriteLine($" 减少流量: {TargetFlow:F1} → {newFlow:F1}");

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
                Debug.WriteLine($" 减少流量失败: {ex.Message}");
                HideLoading();
                ShowOperationFailure();
            }
        }

        private void OnSetButtonClick(object sender, RoutedEventArgs e)
        {
            try
            {
                Debug.WriteLine($" SET按钮点击: 目标流量={TargetFlow:F1}");

                ShowLoading();

                //  触发状态切换请求（启动流量控制）
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
                Debug.WriteLine($" SET按钮处理失败: {ex.Message}");
                HideLoading();
                ShowOperationFailure();
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
                    Debug.WriteLine(" 超过阈值,开始拖拽模式");
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

                    //  触发状态切换请求
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
                Debug.WriteLine(" 流量计选中效果已自动取消");
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

                //  旋转时禁用缓存
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

            //  旋转结束后恢复缓存
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

            Point center = new Point(meterLeft + 200, meterTop + 250);
            double radians = _rotationAngle * Math.PI / 180.0;

            AddSnapPoint(snapPoints, center, radians, Math.PI, "左侧进口", 184, -147);
            AddSnapPoint(snapPoints, center, radians, 0, "右侧出口", 114, -147);

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
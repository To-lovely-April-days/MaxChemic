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
    /// <summary>
    /// MagneticStirrerControl.xaml 的交互逻辑
    /// 加热磁力搅拌器自定义控件,支持设备驱动与UI双向联动
    /// </summary>
    public partial class MagneticStirrerControl : UserControl, INotifyPropertyChanged, IPipeSnapPoints
    {
        #region 事件定义

        public event PropertyChangedEventHandler PropertyChanged;
        public event EventHandler<StirrerDragEventArgs> StirrerDragStarted;
        public event EventHandler<StirrerDragEventArgs> StirrerDragging;
        public event EventHandler<StirrerDragEventArgs> StirrerDragCompleted;
        public event EventHandler<string> StirrerSelected;
        public event EventHandler<string> StirrerDeselected;
        public event EventHandler<StirrerStateChangedEventArgs> StirrerStateChanged;

        // UI向设备驱动的请求事件
        public event EventHandler<HeatStateChangeRequestEventArgs> HeatStateChangeRequested;
        public event EventHandler<StirStateChangeRequestEventArgs> StirStateChangeRequested;
        public event EventHandler<TimerRequestEventArgs> TimerRequested;

        #endregion

        #region 字段

        public string StirrerId { get; set; }

        private bool _isDraggingStirrer = false;
        private Point _dragStartPoint;
        private Point _originalPosition;
        private bool _isLoading = false;

        // 搅拌子自转动画
        private Storyboard _stirStoryboard;

        // 旋钮指针旋转变换(在代码中挂载,便于按设定值转动)
        private RotateTransform _leftKnobRotate;
        private RotateTransform _rightKnobRotate;

        // 量程(与面板丝印一致)
        private const double TempRangeMax = 320.0;
        private const double SpeedRangeMax = 2000.0;
        // 旋钮可转角度范围:-135° ~ +135°
        private const double KnobAngleSpan = 135.0;

        #endregion

        #region 依赖属性

        /// <summary>
        /// 当前温度示值(℃),显示在屏幕左侧橙色数码区
        /// </summary>
        public static readonly DependencyProperty TemperatureProperty =
            DependencyProperty.Register(
                nameof(Temperature),
                typeof(double),
                typeof(MagneticStirrerControl),
                new PropertyMetadata(0.0, OnTemperatureChanged));

        public double Temperature
        {
            get => (double)GetValue(TemperatureProperty);
            set => SetValue(TemperatureProperty, value);
        }

        private static void OnTemperatureChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is MagneticStirrerControl control)
            {
                control.UpdateTemperatureDisplay();
                control.UpdateHeatVisual();   // 辉光强度随温度变化
                control.OnPropertyChanged(nameof(Temperature));
            }
        }

        /// <summary>
        /// 温度设定值(℃),驱动左旋钮指针角度
        /// </summary>
        public static readonly DependencyProperty TemperatureSetpointProperty =
            DependencyProperty.Register(
                nameof(TemperatureSetpoint),
                typeof(double),
                typeof(MagneticStirrerControl),
                new PropertyMetadata(0.0, OnTemperatureSetpointChanged));

        public double TemperatureSetpoint
        {
            get => (double)GetValue(TemperatureSetpointProperty);
            set => SetValue(TemperatureSetpointProperty, value);
        }

        private static void OnTemperatureSetpointChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is MagneticStirrerControl control)
            {
                control.UpdateKnobAngles();
                control.OnPropertyChanged(nameof(TemperatureSetpoint));
            }
        }

        /// <summary>
        /// 当前转速示值(rpm),显示在屏幕右侧绿色数码区
        /// </summary>
        public static readonly DependencyProperty SpeedProperty =
            DependencyProperty.Register(
                nameof(Speed),
                typeof(double),
                typeof(MagneticStirrerControl),
                new PropertyMetadata(0.0, OnSpeedChanged));

        public double Speed
        {
            get => (double)GetValue(SpeedProperty);
            set => SetValue(SpeedProperty, value);
        }

        private static void OnSpeedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is MagneticStirrerControl control)
            {
                control.UpdateSpeedDisplay();
                control.UpdateStirAnimation();
                control.OnPropertyChanged(nameof(Speed));
            }
        }

        /// <summary>
        /// 转速设定值(rpm),驱动右旋钮指针角度
        /// </summary>
        public static readonly DependencyProperty SpeedSetpointProperty =
            DependencyProperty.Register(
                nameof(SpeedSetpoint),
                typeof(double),
                typeof(MagneticStirrerControl),
                new PropertyMetadata(0.0, OnSpeedSetpointChanged));

        public double SpeedSetpoint
        {
            get => (double)GetValue(SpeedSetpointProperty);
            set => SetValue(SpeedSetpointProperty, value);
        }

        private static void OnSpeedSetpointChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is MagneticStirrerControl control)
            {
                control.UpdateKnobAngles();
                control.OnPropertyChanged(nameof(SpeedSetpoint));
            }
        }

        /// <summary>
        /// 加热开关状态,控制陶瓷盘辉光
        /// </summary>
        public static readonly DependencyProperty IsHeatingProperty =
            DependencyProperty.Register(
                nameof(IsHeating),
                typeof(bool),
                typeof(MagneticStirrerControl),
                new PropertyMetadata(false, OnIsHeatingChanged));

        public bool IsHeating
        {
            get => (bool)GetValue(IsHeatingProperty);
            set => SetValue(IsHeatingProperty, value);
        }

        private static void OnIsHeatingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is MagneticStirrerControl control)
            {
                control.UpdateHeatVisual();
                control.OnPropertyChanged(nameof(IsHeating));
                control.RaiseStateChanged();
            }
        }

        /// <summary>
        /// 搅拌开关状态,控制搅拌子自转
        /// </summary>
        public static readonly DependencyProperty IsStirringProperty =
            DependencyProperty.Register(
                nameof(IsStirring),
                typeof(bool),
                typeof(MagneticStirrerControl),
                new PropertyMetadata(false, OnIsStirringChanged));

        public bool IsStirring
        {
            get => (bool)GetValue(IsStirringProperty);
            set => SetValue(IsStirringProperty, value);
        }

        private static void OnIsStirringChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is MagneticStirrerControl control)
            {
                control.UpdateStirAnimation();
                control.OnPropertyChanged(nameof(IsStirring));
                control.RaiseStateChanged();
            }
        }

        /// <summary>
        /// 是否显示锥形瓶(未放置容器时可隐藏)
        /// </summary>
        public static readonly DependencyProperty ShowFlaskProperty =
            DependencyProperty.Register(
                nameof(ShowFlask),
                typeof(bool),
                typeof(MagneticStirrerControl),
                new PropertyMetadata(true, OnShowFlaskChanged));

        public bool ShowFlask
        {
            get => (bool)GetValue(ShowFlaskProperty);
            set => SetValue(ShowFlaskProperty, value);
        }

        private static void OnShowFlaskChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is MagneticStirrerControl control)
            {
                control.UpdateFlaskVisibility();
                control.OnPropertyChanged(nameof(ShowFlask));
            }
        }

        /// <summary>
        /// 选中状态
        /// </summary>
        public static readonly DependencyProperty IsSelectedProperty =
            DependencyProperty.Register(
                nameof(IsSelected),
                typeof(bool),
                typeof(MagneticStirrerControl),
                new PropertyMetadata(false, OnIsSelectedChanged));

        public bool IsSelected
        {
            get => (bool)GetValue(IsSelectedProperty);
            set => SetValue(IsSelectedProperty, value);
        }

        private static void OnIsSelectedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is MagneticStirrerControl control)
            {
                control.UpdateSelectionVisual((bool)e.NewValue);
            }
        }

        #endregion

        #region 构造函数

        public MagneticStirrerControl()
        {
            InitializeComponent();
            StirrerId = Guid.NewGuid().ToString();
            this.DataContext = this;

            bool isInDesignMode = DesignerProperties.GetIsInDesignMode(this);

            this.Loaded += OnStirrerControlLoaded;
            this.Unloaded += OnStirrerControlUnloaded;

            if (!isInDesignMode)
            {
                // 硬件加速配置(与电子秤控件保持一致)
                RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
                RenderOptions.SetEdgeMode(this, EdgeMode.Unspecified);
                RenderOptions.SetCachingHint(this, CachingHint.Cache);
                RenderOptions.SetCacheInvalidationThresholdMinimum(this, 0.5);
                RenderOptions.SetCacheInvalidationThresholdMaximum(this, 2.0);

                this.SnapsToDevicePixels = true;
                this.UseLayoutRounding = true;

                Debug.WriteLine($"磁力搅拌器硬件加速已启用 | StirrerId:{StirrerId}");
            }

            Debug.WriteLine($"磁力搅拌器控件已创建 | StirrerId:{StirrerId}");
        }

        private void OnStirrerControlLoaded(object sender, RoutedEventArgs e)
        {
            try
            {
                AttachKnobTransforms();
                UpdateTemperatureDisplay();
                UpdateSpeedDisplay();
                UpdateHeatVisual();
                UpdateKnobAngles();
                UpdateFlaskVisibility();
                UpdateStirAnimation();

                Debug.WriteLine($"磁力搅拌器控件已加载完成 | StirrerId:{StirrerId}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"磁力搅拌器控件加载失败 | StirrerId:{StirrerId} | 错误:{ex.Message}");
            }
        }

        private void OnStirrerControlUnloaded(object sender, RoutedEventArgs e)
        {
            // 控件被移除时必须停掉动画,否则 Storyboard 会持有引用导致搅拌子一直转
            StopStirAnimation();
        }

        #endregion

        #region 对外控制方法

        /// <summary>
        /// 开启加热
        /// </summary>
        public void StartHeating() => IsHeating = true;

        /// <summary>
        /// 关闭加热
        /// </summary>
        public void StopHeating() => IsHeating = false;

        /// <summary>
        /// 开启搅拌
        /// </summary>
        public void StartStirring() => IsStirring = true;

        /// <summary>
        /// 关闭搅拌
        /// </summary>
        public void StopStirring() => IsStirring = false;

        /// <summary>
        /// 一次性刷新示值(供驱动采集回调调用)
        /// </summary>
        public void UpdateReadings(double temperature, double speed)
        {
            try
            {
                Temperature = temperature;
                Speed = speed;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"刷新示值失败 | StirrerId:{StirrerId} | 错误:{ex.Message}");
            }
        }

        #endregion

        #region UI状态更新方法

        private void UpdateTemperatureDisplay()
        {
            try
            {
                if (TempValueText == null) return;
                TempValueText.Text = Temperature.ToString("F1");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"更新温度显示失败 | StirrerId:{StirrerId} | 错误:{ex.Message}");
            }
        }

        private void UpdateSpeedDisplay()
        {
            try
            {
                if (SpeedValueText == null) return;
                SpeedValueText.Text = Math.Round(Speed).ToString("F0");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"更新转速显示失败 | StirrerId:{StirrerId} | 错误:{ex.Message}");
            }
        }

        /// <summary>
        /// 加热视觉:辉光与盘面着色随温度深浅变化
        /// </summary>
        private void UpdateHeatVisual()
        {
            try
            {
                if (HeatGlow == null || PlateHeatTint == null) return;

                if (!IsHeating)
                {
                    HeatGlow.Opacity = 0;
                    PlateHeatTint.Opacity = 0;
                    return;
                }

                // 温度越高辉光越强,上限在 320℃ 处封顶
                double ratio = Math.Max(0.0, Math.Min(1.0, Temperature / TempRangeMax));
                HeatGlow.Opacity = 0.25 + 0.55 * ratio;
                PlateHeatTint.Opacity = 0.10 + 0.35 * ratio;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"更新加热视觉失败 | StirrerId:{StirrerId} | 错误:{ex.Message}");
            }
        }

        private void UpdateFlaskVisibility()
        {
            try
            {
                var vis = ShowFlask ? Visibility.Visible : Visibility.Collapsed;
                if (FlaskLiquid != null) FlaskLiquid.Visibility = vis;
                if (FlaskLiquidSurface != null) FlaskLiquidSurface.Visibility = vis;
                if (FlaskGlassBody != null) FlaskGlassBody.Visibility = vis;
                if (FlaskGlassOutline != null) FlaskGlassOutline.Visibility = vis;
                if (FlaskRimBack != null) FlaskRimBack.Visibility = vis;
                if (FlaskRimFront != null) FlaskRimFront.Visibility = vis;
                if (FlaskHighlight != null) FlaskHighlight.Visibility = vis;
                // 瓶子没了,液面旋涡也不该留着
                if (!ShowFlask && SwirlLayer != null) SwirlLayer.Visibility = Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"更新锥形瓶显示失败 | StirrerId:{StirrerId} | 错误:{ex.Message}");
            }
        }

        /// <summary>
        /// 为两个旋钮指针挂载旋转变换,旋转中心取指针根部(即旋钮圆心)
        /// </summary>
        private void AttachKnobTransforms()
        {
            try
            {
                if (LeftKnobPointer != null && _leftKnobRotate == null)
                {
                    _leftKnobRotate = new RotateTransform(0, LeftKnobPointer.X1, LeftKnobPointer.Y1);
                    LeftKnobPointer.RenderTransform = _leftKnobRotate;
                }

                if (RightKnobPointer != null && _rightKnobRotate == null)
                {
                    _rightKnobRotate = new RotateTransform(0, RightKnobPointer.X1, RightKnobPointer.Y1);
                    RightKnobPointer.RenderTransform = _rightKnobRotate;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"挂载旋钮变换失败 | StirrerId:{StirrerId} | 错误:{ex.Message}");
            }
        }

        /// <summary>
        /// 旋钮指针角度 = 设定值在量程中的占比映射到 -135°~+135°
        /// </summary>
        private void UpdateKnobAngles()
        {
            try
            {
                if (_leftKnobRotate != null)
                {
                    double r = Math.Max(0.0, Math.Min(1.0, TemperatureSetpoint / TempRangeMax));
                    _leftKnobRotate.Angle = -KnobAngleSpan + 2 * KnobAngleSpan * r;
                }

                if (_rightKnobRotate != null)
                {
                    double r = Math.Max(0.0, Math.Min(1.0, SpeedSetpoint / SpeedRangeMax));
                    _rightKnobRotate.Angle = -KnobAngleSpan + 2 * KnobAngleSpan * r;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"更新旋钮角度失败 | StirrerId:{StirrerId} | 错误:{ex.Message}");
            }
        }

        #endregion

        #region 搅拌子动画

        // 三条动画的周期直接照搬原图的 animateTransform:
        //   搅拌子(含下影) 14°→374° / 0.55s,液面旋涡 0°→360° / 1.6s。
        // 搅拌子从 14° 起转而不是 0°,是为了停机时停在原图那个略微倾斜的静止姿态。
        private const double StirBarStartAngle = 14.0;
        private const double StirBarTurnSeconds = 0.55;
        private const double SwirlTurnSeconds = 1.6;

        /// <summary>
        /// 搅拌开 → 搅拌子与液面旋涡按原图节奏转;搅拌关 → 全部静止,旋涡收起。
        ///
        /// 周期是固定的,不随转速变 —— 原图就是这么定的。若要让快慢跟着实际转速走,
        /// 把 StirBarTurnSeconds 换成按 Speed 算出来的值即可(只影响观感,不影响控制)。
        /// </summary>
        private void UpdateStirAnimation()
        {
            try
            {
                if (StirBarRotate == null) return;

                if (!IsStirring)
                {
                    StopStirAnimation();
                    return;
                }

                // 已经在转就别重建,否则每来一轮采集刷新搅拌子就跳回起点
                if (_stirStoryboard != null) return;

                _stirStoryboard = new Storyboard();

                AddRotationTrack(_stirStoryboard, StirBarRotate,
                    StirBarStartAngle, StirBarStartAngle + 360, StirBarTurnSeconds);
                AddRotationTrack(_stirStoryboard, StirBarShadowRotate,
                    StirBarStartAngle, StirBarStartAngle + 360, StirBarTurnSeconds);
                AddRotationTrack(_stirStoryboard, SwirlRotate, 0, 360, SwirlTurnSeconds);

                // 旋涡只在转起来时才有:停机的液面是平的,留一圈静止的白弧会像贴纸
                if (SwirlLayer != null && ShowFlask)
                    SwirlLayer.Visibility = Visibility.Visible;

                _stirStoryboard.Begin(this, true);

                Debug.WriteLine($"搅拌动画已启动 | StirrerId:{StirrerId} | " +
                                $"搅拌子 {StirBarTurnSeconds}s/圈, 旋涡 {SwirlTurnSeconds}s/圈");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"更新搅拌动画失败 | StirrerId:{StirrerId} | 错误:{ex.Message}");
            }
        }

        /// <summary>往 Storyboard 里加一条匀速自转轨道(线性,无缓动,首尾角度差 360 才能无缝循环)。</summary>
        private static void AddRotationTrack(Storyboard storyboard, RotateTransform target,
                                             double from, double to, double seconds)
        {
            if (target == null) return;

            var animation = new DoubleAnimation
            {
                From = from,
                To = to,
                Duration = new Duration(TimeSpan.FromSeconds(seconds)),
                RepeatBehavior = RepeatBehavior.Forever
            };
            Storyboard.SetTarget(animation, target);
            Storyboard.SetTargetProperty(animation, new PropertyPath(RotateTransform.AngleProperty));
            storyboard.Children.Add(animation);
        }

        private void StopStirAnimation()
        {
            try
            {
                if (_stirStoryboard != null)
                {
                    _stirStoryboard.Stop(this);
                    _stirStoryboard.Remove(this);
                    _stirStoryboard = null;
                }

                // Remove 之后角度会回到 XAML 初值,这里显式停在原图的静止姿态
                if (StirBarRotate != null) StirBarRotate.Angle = StirBarStartAngle;
                if (StirBarShadowRotate != null) StirBarShadowRotate.Angle = StirBarStartAngle;
                if (SwirlRotate != null) SwirlRotate.Angle = 0;
                if (SwirlLayer != null) SwirlLayer.Visibility = Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"停止搅拌动画失败 | StirrerId:{StirrerId} | 错误:{ex.Message}");
            }
        }

        #endregion

        #region 触控键事件处理

        /// <summary>
        /// 加热键:请求驱动切换温度开关
        /// </summary>
        private void OnHeatKeyClick(object sender, RoutedEventArgs e)
        {
            try
            {
                Debug.WriteLine($"加热键点击 | StirrerId:{StirrerId} | 当前状态:{IsHeating}");

                // 没有订阅者就不亮"通信中",否则回调永远不来、遮罩下不去
                if (HeatStateChangeRequested == null)
                {
                    Debug.WriteLine($"加热键无订阅者,忽略 | StirrerId:{StirrerId}");
                    e.Handled = true;
                    return;
                }

                ShowLoading();

                HeatStateChangeRequested?.Invoke(this, new HeatStateChangeRequestEventArgs
                {
                    StirrerId = this.StirrerId,
                    CurrentState = IsHeating,
                    RequestedState = !IsHeating,
                    TemperatureSetpoint = this.TemperatureSetpoint,
                    OnCompleted = (success) =>
                    {
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            HideLoading();
                            if (!success)
                            {
                                ShowOperationFailure();
                                Debug.WriteLine($"加热开关切换失败 | StirrerId:{StirrerId}");
                            }
                        }));
                    }
                });

                e.Handled = true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"加热键处理失败 | StirrerId:{StirrerId} | 错误:{ex.Message}");
                HideLoading();
                ShowOperationFailure();
            }
        }

        /// <summary>
        /// 搅拌键:请求驱动切换速度开关
        /// </summary>
        private void OnStirKeyClick(object sender, RoutedEventArgs e)
        {
            try
            {
                Debug.WriteLine($"搅拌键点击 | StirrerId:{StirrerId} | 当前状态:{IsStirring}");

                if (StirStateChangeRequested == null)
                {
                    Debug.WriteLine($"搅拌键无订阅者,忽略 | StirrerId:{StirrerId}");
                    e.Handled = true;
                    return;
                }

                ShowLoading();

                StirStateChangeRequested?.Invoke(this, new StirStateChangeRequestEventArgs
                {
                    StirrerId = this.StirrerId,
                    CurrentState = IsStirring,
                    RequestedState = !IsStirring,
                    SpeedSetpoint = this.SpeedSetpoint,
                    OnCompleted = (success) =>
                    {
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            HideLoading();
                            if (!success)
                            {
                                ShowOperationFailure();
                                Debug.WriteLine($"搅拌开关切换失败 | StirrerId:{StirrerId}");
                            }
                        }));
                    }
                });

                e.Handled = true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"搅拌键处理失败 | StirrerId:{StirrerId} | 错误:{ex.Message}");
                HideLoading();
                ShowOperationFailure();
            }
        }

        /// <summary>
        /// 定时键:请求驱动读取或复位计时
        /// </summary>
        private void OnTimerKeyClick(object sender, RoutedEventArgs e)
        {
            try
            {
                Debug.WriteLine($"定时键点击 | StirrerId:{StirrerId}");

                if (TimerRequested == null)
                {
                    Debug.WriteLine($"定时键无订阅者,忽略 | StirrerId:{StirrerId}");
                    e.Handled = true;
                    return;
                }

                ShowLoading();

                TimerRequested?.Invoke(this, new TimerRequestEventArgs
                {
                    StirrerId = this.StirrerId,
                    OnCompleted = (success) =>
                    {
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            HideLoading();
                            if (!success)
                            {
                                ShowOperationFailure();
                                Debug.WriteLine($"定时操作失败 | StirrerId:{StirrerId}");
                            }
                        }));
                    }
                });

                e.Handled = true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"定时键处理失败 | StirrerId:{StirrerId} | 错误:{ex.Message}");
                HideLoading();
                ShowOperationFailure();
            }
        }

        #endregion

        #region 加载和错误状态

        public void ShowLoading()
        {
            if (_isLoading) return;
            _isLoading = true;

            try
            {
                ClearError();

                if (LoadingOverlay != null)
                    LoadingOverlay.Visibility = Visibility.Visible;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"显示加载状态失败 | StirrerId:{StirrerId} | 错误:{ex.Message}");
            }
        }

        public void HideLoading()
        {
            if (!_isLoading) return;
            _isLoading = false;

            try
            {
                if (LoadingOverlay != null)
                    LoadingOverlay.Visibility = Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"隐藏加载状态失败 | StirrerId:{StirrerId} | 错误:{ex.Message}");
            }
        }

        /// <summary>
        /// 通信失败时数码区闪 Err,2 秒后恢复
        /// </summary>
        public void ShowOperationFailure()
        {
            try
            {
                HideLoading();

                if (TempValueText == null) return;

                var originalText = TempValueText.Text;
                var originalBrush = TempValueText.Foreground;

                TempValueText.Text = "Err";
                TempValueText.Foreground = new SolidColorBrush(Color.FromRgb(255, 59, 48));

                var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                timer.Tick += (s, e) =>
                {
                    TempValueText.Text = originalText;
                    TempValueText.Foreground = originalBrush;
                    timer.Stop();
                };
                timer.Start();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"显示操作失败状态失败 | StirrerId:{StirrerId} | 错误:{ex.Message}");
            }
        }

        public void ClearError()
        {
            try
            {
                if (TempValueText != null)
                    TempValueText.Foreground = new SolidColorBrush(Color.FromRgb(0xEF, 0x9F, 0x27));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"清除错误状态失败 | StirrerId:{StirrerId} | 错误:{ex.Message}");
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
                    StirrerSelected?.Invoke(this, this.StirrerId);
                    this.IsSelected = true;

                    _isDraggingStirrer = true;
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

                    StirrerDragStarted?.Invoke(this, new StirrerDragEventArgs
                    {
                        StirrerId = StirrerId,
                        StartPosition = _originalPosition
                    });

                    e.Handled = true;
                    Debug.WriteLine($"开始拖拽磁力搅拌器 | StirrerId:{StirrerId}");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"鼠标按下事件处理失败 | StirrerId:{StirrerId} | 错误:{ex.Message}");
            }
        }

        private void OnDraggableAreaMouseMove(object sender, MouseEventArgs e)
        {
            try
            {
                if (_isDraggingStirrer && e.LeftButton == MouseButtonState.Pressed)
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

                        StirrerDragging?.Invoke(this, new StirrerDragEventArgs
                        {
                            StirrerId = StirrerId,
                            CurrentPosition = new Point(newLeft, newTop)
                        });
                    }
                    e.Handled = true;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"鼠标移动事件处理失败 | StirrerId:{StirrerId} | 错误:{ex.Message}");
            }
        }

        private void OnDraggableAreaMouseUp(object sender, MouseButtonEventArgs e)
        {
            try
            {
                if (_isDraggingStirrer)
                {
                    CompleteStirrerDrag();

                    var draggableArea = sender as Rectangle;
                    draggableArea?.ReleaseMouseCapture();

                    e.Handled = true;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"鼠标释放事件处理失败 | StirrerId:{StirrerId} | 错误:{ex.Message}");
            }
        }

        private void OnDraggableAreaMouseLeave(object sender, MouseEventArgs e)
        {
            try
            {
                if (_isDraggingStirrer)
                {
                    CompleteStirrerDrag();

                    var draggableArea = sender as Rectangle;
                    draggableArea?.ReleaseMouseCapture();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"鼠标离开事件处理失败 | StirrerId:{StirrerId} | 错误:{ex.Message}");
            }
        }

        private void CompleteStirrerDrag()
        {
            try
            {
                if (!_isDraggingStirrer) return;

                var finalPosition = new Point(Canvas.GetLeft(this), Canvas.GetTop(this));

                StirrerDragCompleted?.Invoke(this, new StirrerDragEventArgs
                {
                    StirrerId = StirrerId,
                    StartPosition = _originalPosition,
                    CurrentPosition = finalPosition
                });

                this.Opacity = 1.0;
                _isDraggingStirrer = false;

                RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);

                Debug.WriteLine($"磁力搅拌器拖拽完成 | StirrerId:{StirrerId} | 位置:({finalPosition.X:F0}, {finalPosition.Y:F0})");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"完成拖拽失败 | StirrerId:{StirrerId} | 错误:{ex.Message}");
            }
        }

        private void UpdateSelectionVisual(bool isSelected)
        {
            // 选中框由设计器统一系统接管,这里保持隐藏
            if (SelectionBorder != null)
                SelectionBorder.Visibility = Visibility.Collapsed;
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
                    Debug.WriteLine("磁力搅拌器未添加到Canvas，无法获取吸附点");
                    return snapPoints;
                }

                var left = Canvas.GetLeft(this);
                var top = Canvas.GetTop(this);
                if (double.IsNaN(left)) left = 0;
                if (double.IsNaN(top)) top = 0;

                double w = this.ActualWidth > 0 ? this.ActualWidth : this.Width;
                double h = this.ActualHeight > 0 ? this.ActualHeight : this.Height;
                if (double.IsNaN(w) || w <= 0) w = 680;
                if (double.IsNaN(h) || h <= 0) h = 450;

                snapPoints.Add(new PipeSnapPoint
                {
                    WorldPosition = new Point(left + w / 2, top),
                    Direction = SnapDirection.Up,
                    Description = "磁力搅拌器顶部"
                });

                snapPoints.Add(new PipeSnapPoint
                {
                    WorldPosition = new Point(left + w / 2, top + h),
                    Direction = SnapDirection.Down,
                    Description = "磁力搅拌器底部"
                });

                snapPoints.Add(new PipeSnapPoint
                {
                    WorldPosition = new Point(left, top + h / 2),
                    Direction = SnapDirection.Left,
                    Description = "磁力搅拌器左侧"
                });

                snapPoints.Add(new PipeSnapPoint
                {
                    WorldPosition = new Point(left + w, top + h / 2),
                    Direction = SnapDirection.Right,
                    Description = "磁力搅拌器右侧"
                });

                Debug.WriteLine($"磁力搅拌器提供吸附点 | StirrerId:{StirrerId} | 数量:{snapPoints.Count}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"获取吸附点失败 | StirrerId:{StirrerId} | 错误:{ex.Message}");
            }

            return snapPoints;
        }

        #endregion

        #region INotifyPropertyChanged实现

        private void RaiseStateChanged()
        {
            StirrerStateChanged?.Invoke(this, new StirrerStateChangedEventArgs
            {
                StirrerId = StirrerId,
                IsHeating = IsHeating,
                IsStirring = IsStirring,
                Temperature = Temperature,
                Speed = Speed
            });
        }

        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        #endregion
    }

    #region 事件参数类

    /// <summary>
    /// 加热开关切换请求事件参数
    /// </summary>
    public class HeatStateChangeRequestEventArgs : EventArgs
    {
        public string StirrerId { get; set; }
        public bool CurrentState { get; set; }
        public bool RequestedState { get; set; }
        public double TemperatureSetpoint { get; set; }
        public Action<bool> OnCompleted { get; set; }
    }

    /// <summary>
    /// 搅拌开关切换请求事件参数
    /// </summary>
    public class StirStateChangeRequestEventArgs : EventArgs
    {
        public string StirrerId { get; set; }
        public bool CurrentState { get; set; }
        public bool RequestedState { get; set; }
        public double SpeedSetpoint { get; set; }
        public Action<bool> OnCompleted { get; set; }
    }

    /// <summary>
    /// 定时操作请求事件参数
    /// </summary>
    public class TimerRequestEventArgs : EventArgs
    {
        public string StirrerId { get; set; }
        public Action<bool> OnCompleted { get; set; }
    }

    /// <summary>
    /// 磁力搅拌器拖拽事件参数
    /// </summary>
    public class StirrerDragEventArgs : EventArgs
    {
        public string StirrerId { get; set; }
        public Point StartPosition { get; set; }
        public Point CurrentPosition { get; set; }
    }

    /// <summary>
    /// 磁力搅拌器状态变化事件参数
    /// </summary>
    public class StirrerStateChangedEventArgs : EventArgs
    {
        public string StirrerId { get; set; }
        public bool IsHeating { get; set; }
        public bool IsStirring { get; set; }
        public double Temperature { get; set; }
        public double Speed { get; set; }
    }

    #endregion
}

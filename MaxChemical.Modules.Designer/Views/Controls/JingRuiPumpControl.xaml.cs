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
    /// JingRuiPumpControl.xaml 的交互逻辑。
    /// 精睿系列柱塞泵(杭州精进科技,RS485 Modbus)画布控件。
    ///
    /// 这是一个纯显示控件:实时流量/压力、设定流量、压力条、三盏状态灯、
    /// 柱塞往复与两条管路里的液流动画,全部由驱动推上来的状态驱动。
    /// 操作一律走双击 → 设备屏幕,所以画布上不放任何可点的按钮。
    /// </summary>
    public partial class JingRuiPumpControl : UserControl, INotifyPropertyChanged, IPipeSnapPoints
    {
        #region 事件定义

        public event PropertyChangedEventHandler PropertyChanged;
        public event EventHandler<JingRuiPumpDragEventArgs> PumpDragStarted;
        public event EventHandler<JingRuiPumpDragEventArgs> PumpDragging;
        public event EventHandler<JingRuiPumpDragEventArgs> PumpDragCompleted;
        public event EventHandler<string> PumpSelected;

        #endregion

        #region 字段

        public string PumpId { get; set; }

        private bool _isDraggingPump;
        private Point _dragStartPoint;
        private Point _originalPosition;
        private bool _isLoading;

        /// <summary>柱塞往复 + 两条管路液流,合成一条 Storyboard 统一起停。</summary>
        private Storyboard _runStoryboard;

        /// <summary>当前动画周期(秒)。流量变化不大时不重建动画,免得液柱每秒跳一次。</summary>
        private double _currentPeriodSeconds;

        // 压力条几何(与 XAML 里的 Rectangle 对齐)
        private const double BarLeft = 108.0;
        private const double BarWidth = 194.0;

        // 液流动画:一个虚线周期 = 2.2 实 + 2.2 空 = 4.4,位移整周期才能无缝循环
        private const double DashCycle = 4.4;
        private const double PlungerTravel = 14.0;

        // 动画周期上下限:流量再小也不至于慢到看不出在动,再大也不至于快到闪
        private const double MinPeriodSeconds = 0.22;
        private const double MaxPeriodSeconds = 1.60;

        private readonly Brush _ledOff = new SolidColorBrush(Color.FromRgb(0x2C, 0x32, 0x37));
        private readonly Brush _ledRun = new SolidColorBrush(Color.FromRgb(0x35, 0xE0, 0x7A));
        private readonly Brush _ledDose = new SolidColorBrush(Color.FromRgb(0x4C, 0xA8, 0xE7));
        private readonly Brush _ledAlarm = new SolidColorBrush(Color.FromRgb(0xE7, 0x4C, 0x3C));
        private readonly Brush _flowNormal = new SolidColorBrush(Color.FromRgb(0x5D, 0xCA, 0xA5));
        private readonly Brush _pressureNormal = new SolidColorBrush(Color.FromRgb(0xEF, 0x9F, 0x27));
        private readonly Brush _textMuted = new SolidColorBrush(Color.FromRgb(0x79, 0x80, 0x86));
        private readonly Brush _textAlarm = new SolidColorBrush(Color.FromRgb(0xE7, 0x4C, 0x3C));

        #endregion

        #region 依赖属性

        /// <summary>实时流量(mL/min),显示在屏幕上行绿色数码区。</summary>
        public static readonly DependencyProperty FlowProperty =
            DependencyProperty.Register(nameof(Flow), typeof(double), typeof(JingRuiPumpControl),
                new PropertyMetadata(0.0, OnFlowChanged));

        public double Flow
        {
            get => (double)GetValue(FlowProperty);
            set => SetValue(FlowProperty, value);
        }

        private static void OnFlowChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is JingRuiPumpControl c)
            {
                c.UpdateFlowDisplay();
                c.UpdateRunAnimation();   // 流量决定柱塞与液流的快慢
                c.OnPropertyChanged(nameof(Flow));
            }
        }

        /// <summary>设定流量(mL/min),以 SV 小字显示在屏幕左上。</summary>
        public static readonly DependencyProperty FlowSetpointProperty =
            DependencyProperty.Register(nameof(FlowSetpoint), typeof(double), typeof(JingRuiPumpControl),
                new PropertyMetadata(0.0, OnFlowSetpointChanged));

        public double FlowSetpoint
        {
            get => (double)GetValue(FlowSetpointProperty);
            set => SetValue(FlowSetpointProperty, value);
        }

        private static void OnFlowSetpointChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is JingRuiPumpControl c)
            {
                c.UpdateSetpointDisplay();
                c.OnPropertyChanged(nameof(FlowSetpoint));
            }
        }

        /// <summary>实时压力(MPa),显示在屏幕下行琥珀色数码区并驱动压力条。</summary>
        public static readonly DependencyProperty PressureProperty =
            DependencyProperty.Register(nameof(Pressure), typeof(double), typeof(JingRuiPumpControl),
                new PropertyMetadata(0.0, OnPressureChanged));

        public double Pressure
        {
            get => (double)GetValue(PressureProperty);
            set => SetValue(PressureProperty, value);
        }

        private static void OnPressureChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is JingRuiPumpControl c)
            {
                c.UpdatePressureDisplay();
                c.UpdatePressureBar();
                c.OnPropertyChanged(nameof(Pressure));
            }
        }

        /// <summary>压力上限(MPa),压力条上的红色刻线位置。</summary>
        public static readonly DependencyProperty PressureLimitProperty =
            DependencyProperty.Register(nameof(PressureLimit), typeof(double), typeof(JingRuiPumpControl),
                new PropertyMetadata(4.0, OnScaleChanged));

        public double PressureLimit
        {
            get => (double)GetValue(PressureLimitProperty);
            set => SetValue(PressureLimitProperty, value);
        }

        /// <summary>压力条满量程(MPa),取泵本体最大压力。</summary>
        public static readonly DependencyProperty PressureRangeProperty =
            DependencyProperty.Register(nameof(PressureRange), typeof(double), typeof(JingRuiPumpControl),
                new PropertyMetadata(10.0, OnScaleChanged));

        public double PressureRange
        {
            get => (double)GetValue(PressureRangeProperty);
            set => SetValue(PressureRangeProperty, value);
        }

        /// <summary>流量满量程(mL/min),用来把流量映射成动画快慢。</summary>
        public static readonly DependencyProperty FlowRangeProperty =
            DependencyProperty.Register(nameof(FlowRange), typeof(double), typeof(JingRuiPumpControl),
                new PropertyMetadata(100.0, OnScaleChanged));

        public double FlowRange
        {
            get => (double)GetValue(FlowRangeProperty);
            set => SetValue(FlowRangeProperty, value);
        }

        private static void OnScaleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is JingRuiPumpControl c)
            {
                c.UpdatePressureBar();
                c.UpdateRunAnimation();
            }
        }

        /// <summary>泵是否在运行。停机时液流与柱塞立即停住,不做惯性收尾。</summary>
        public static readonly DependencyProperty IsRunningProperty =
            DependencyProperty.Register(nameof(IsRunning), typeof(bool), typeof(JingRuiPumpControl),
                new PropertyMetadata(false, OnIsRunningChanged));

        public bool IsRunning
        {
            get => (bool)GetValue(IsRunningProperty);
            set => SetValue(IsRunningProperty, value);
        }

        private static void OnIsRunningChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is JingRuiPumpControl c)
            {
                c.UpdateRunAnimation();
                c.UpdateLeds();
                c.OnPropertyChanged(nameof(IsRunning));
            }
        }

        /// <summary>是否处于定量输送。</summary>
        public static readonly DependencyProperty IsDosingProperty =
            DependencyProperty.Register(nameof(IsDosing), typeof(bool), typeof(JingRuiPumpControl),
                new PropertyMetadata(false, OnIsDosingChanged));

        public bool IsDosing
        {
            get => (bool)GetValue(IsDosingProperty);
            set => SetValue(IsDosingProperty, value);
        }

        private static void OnIsDosingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is JingRuiPumpControl c)
            {
                c.UpdateLeds();
                c.OnPropertyChanged(nameof(IsDosing));
            }
        }

        /// <summary>
        /// 故障码。0=正常,5=超上限,6=超下限,1=电路,7=泵体,44=驱动器。
        /// 用 double 而不是 int:驱动侧状态一律以 double 推送,DP 类型对不上会被静默丢弃。
        /// </summary>
        public static readonly DependencyProperty FaultCodeProperty =
            DependencyProperty.Register(nameof(FaultCode), typeof(double), typeof(JingRuiPumpControl),
                new PropertyMetadata(0.0, OnFaultCodeChanged));

        public double FaultCode
        {
            get => (double)GetValue(FaultCodeProperty);
            set => SetValue(FaultCodeProperty, value);
        }

        private static void OnFaultCodeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is JingRuiPumpControl c)
            {
                c.UpdateLeds();
                c.UpdateFaultText();
                c.OnPropertyChanged(nameof(FaultCode));
            }
        }

        /// <summary>选中状态。</summary>
        public static readonly DependencyProperty IsSelectedProperty =
            DependencyProperty.Register(nameof(IsSelected), typeof(bool), typeof(JingRuiPumpControl),
                new PropertyMetadata(false, OnIsSelectedChanged));

        public bool IsSelected
        {
            get => (bool)GetValue(IsSelectedProperty);
            set => SetValue(IsSelectedProperty, value);
        }

        private static void OnIsSelectedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is JingRuiPumpControl c) c.UpdateSelectionVisual((bool)e.NewValue);
        }

        #endregion

        #region 构造与生命周期

        public JingRuiPumpControl()
        {
            InitializeComponent();
            PumpId = Guid.NewGuid().ToString();
            DataContext = this;

            bool isInDesignMode = DesignerProperties.GetIsInDesignMode(this);

            Loaded += OnPumpControlLoaded;
            Unloaded += OnPumpControlUnloaded;

            if (!isInDesignMode)
            {
                // 硬件加速配置(与画布上其他自定义控件保持一致)
                RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
                RenderOptions.SetEdgeMode(this, EdgeMode.Unspecified);
                RenderOptions.SetCachingHint(this, CachingHint.Cache);
                RenderOptions.SetCacheInvalidationThresholdMinimum(this, 0.5);
                RenderOptions.SetCacheInvalidationThresholdMaximum(this, 2.0);

                SnapsToDevicePixels = true;
                UseLayoutRounding = true;
            }

            Debug.WriteLine($"精睿泵控件已创建 | PumpId:{PumpId}");
        }

        private void OnPumpControlLoaded(object sender, RoutedEventArgs e)
        {
            try
            {
                UpdateFlowDisplay();
                UpdateSetpointDisplay();
                UpdatePressureDisplay();
                UpdatePressureBar();
                UpdateLeds();
                UpdateFaultText();
                UpdateRunAnimation();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"精睿泵控件加载失败 | PumpId:{PumpId} | 错误:{ex.Message}");
            }
        }

        private void OnPumpControlUnloaded(object sender, RoutedEventArgs e)
        {
            // 控件被移除时必须停掉动画,否则 Storyboard 会持有引用导致液流一直跑
            StopRunAnimation();
        }

        #endregion

        #region 对外控制方法

        /// <summary>一次性把一轮读数填进控件,省得外部逐个属性赋值。</summary>
        public void UpdateReadings(double flow, double flowSetpoint, double pressure,
                                   bool isRunning, bool isDosing, double faultCode)
        {
            Flow = flow;
            FlowSetpoint = flowSetpoint;
            Pressure = pressure;
            IsRunning = isRunning;
            IsDosing = isDosing;
            FaultCode = faultCode;
        }

        #endregion

        #region 显示刷新

        private void UpdateFlowDisplay()
        {
            try
            {
                if (FlowValueText == null) return;
                FlowValueText.Text = Flow.ToString("F2");
                FlowValueText.Foreground = _flowNormal;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"更新流量示值失败 | PumpId:{PumpId} | 错误:{ex.Message}");
            }
        }

        private void UpdateSetpointDisplay()
        {
            try
            {
                if (SetpointText == null) return;
                SetpointText.Text = FlowSetpoint > 0 ? $"SV {FlowSetpoint:F2}" : "SV --";
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"更新设定流量失败 | PumpId:{PumpId} | 错误:{ex.Message}");
            }
        }

        private void UpdatePressureDisplay()
        {
            try
            {
                if (PressureValueText == null) return;
                PressureValueText.Text = Pressure.ToString("F2");

                // 触到上限就把示值本身变红 —— 压力条的颜色变化在小尺寸下不够醒目
                bool overLimit = PressureLimit > 0 && Pressure >= PressureLimit;
                PressureValueText.Foreground = overLimit ? _textAlarm : _pressureNormal;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"更新压力示值失败 | PumpId:{PumpId} | 错误:{ex.Message}");
            }
        }

        private void UpdatePressureBar()
        {
            try
            {
                if (PressureBar == null) return;

                double range = PressureRange > 0 ? PressureRange : 10.0;
                double ratio = Clamp01(Pressure / range);
                PressureBar.Width = BarWidth * ratio;

                if (PressureLimitTick != null)
                {
                    if (PressureLimit > 0 && PressureLimit < range)
                    {
                        Canvas.SetLeft(PressureLimitTick, BarLeft + BarWidth * (PressureLimit / range));
                        PressureLimitTick.Visibility = Visibility.Visible;
                    }
                    else
                    {
                        // 上限没设或超出量程时不画刻线,免得永远贴在最右端让人误以为已经到顶
                        PressureLimitTick.Visibility = Visibility.Collapsed;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"更新压力条失败 | PumpId:{PumpId} | 错误:{ex.Message}");
            }
        }

        private void UpdateLeds()
        {
            try
            {
                if (LedRun != null) LedRun.Fill = IsRunning ? _ledRun : _ledOff;
                if (LedDose != null) LedDose.Fill = IsDosing ? _ledDose : _ledOff;
                if (LedFault != null) LedFault.Fill = FaultCode != 0 ? _ledAlarm : _ledOff;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"更新状态灯失败 | PumpId:{PumpId} | 错误:{ex.Message}");
            }
        }

        private void UpdateFaultText()
        {
            try
            {
                if (FaultText == null) return;

                int code = (int)Math.Round(FaultCode);
                if (code == 0)
                {
                    FaultText.Text = "RS485 · Modbus";
                    FaultText.Foreground = _textMuted;
                }
                else
                {
                    FaultText.Text = DescribeFault(code);
                    FaultText.Foreground = _textAlarm;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"更新故障文本失败 | PumpId:{PumpId} | 错误:{ex.Message}");
            }
        }

        /// <summary>故障码 → 中文描述。与驱动里的 DescribeFault 保持一致。</summary>
        private static string DescribeFault(int code) => code switch
        {
            1 => "电路故障",
            5 => "压力超上限",
            6 => "压力超下限",
            7 => "泵体故障",
            44 => "驱动器故障",
            _ => $"故障 {code}"
        };

        #endregion

        #region 柱塞与液流动画

        /// <summary>
        /// 泵在跑就让柱塞往复、两条管里的液柱走起来,周期随流量变快;停机则全部静止。
        ///
        /// 每轮采集都会调到这里,所以做了两层保护:
        ///   ① 流量变化不足以让周期明显改变时,不重建动画 —— 否则液柱每秒被打回起点,看着像卡顿;
        ///   ② 停机时把 DashOffset 与柱塞位移显式归零,免得 Storyboard.Remove 之后停在半路。
        /// </summary>
        private void UpdateRunAnimation()
        {
            try
            {
                if (PlungerMove == null) return;

                // 流量读数为 0 的运行状态(刚下发启动、泵还没起转)也当作静止处理
                if (!IsRunning || Flow <= 0.001)
                {
                    StopRunAnimation();
                    return;
                }

                double period = PeriodForFlow(Flow);

                // 周期变化小于 8% 就沿用现有动画,避免每轮采集都重建
                if (_runStoryboard != null &&
                    Math.Abs(period - _currentPeriodSeconds) / _currentPeriodSeconds < 0.08)
                {
                    return;
                }

                StopRunAnimation();
                _currentPeriodSeconds = period;

                _runStoryboard = new Storyboard();

                // 液流:虚线整周期位移,负方向 = 沿 Path 数据的走向流动
                AddDashTrack(_runStoryboard, OutletFlowPath, period);
                AddDashTrack(_runStoryboard, InletFlowPath, period);

                // 柱塞:半周期下行、半周期上行
                var plunger = new DoubleAnimation
                {
                    From = 0,
                    To = PlungerTravel,
                    Duration = new Duration(TimeSpan.FromSeconds(period / 2)),
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever
                };
                Storyboard.SetTarget(plunger, PlungerMove);
                Storyboard.SetTargetProperty(plunger, new PropertyPath(TranslateTransform.YProperty));
                _runStoryboard.Children.Add(plunger);

                if (OutletFlowPath != null) OutletFlowPath.Opacity = 1;
                if (InletFlowPath != null) InletFlowPath.Opacity = 1;

                _runStoryboard.Begin(this, true);

                Debug.WriteLine($"精睿泵运行动画已启动 | PumpId:{PumpId} | 周期 {period:F2}s @ {Flow:F2} mL/min");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"更新运行动画失败 | PumpId:{PumpId} | 错误:{ex.Message}");
            }
        }

        /// <summary>流量越大周期越短。线性映射到 [MinPeriod, MaxPeriod] 并限幅。</summary>
        private double PeriodForFlow(double flow)
        {
            double range = FlowRange > 0 ? FlowRange : 100.0;
            double r = Clamp01(flow / range);
            return MaxPeriodSeconds - (MaxPeriodSeconds - MinPeriodSeconds) * r;
        }

        /// <summary>往 Storyboard 里加一条虚线滚动轨道(线性,位移必须是虚线周期的整数倍才能无缝循环)。</summary>
        private static void AddDashTrack(Storyboard storyboard, Shape target, double seconds)
        {
            if (target == null) return;

            var animation = new DoubleAnimation
            {
                From = 0,
                To = -DashCycle,
                Duration = new Duration(TimeSpan.FromSeconds(seconds)),
                RepeatBehavior = RepeatBehavior.Forever
            };
            Storyboard.SetTarget(animation, target);
            Storyboard.SetTargetProperty(animation, new PropertyPath(Shape.StrokeDashOffsetProperty));
            storyboard.Children.Add(animation);
        }

        private void StopRunAnimation()
        {
            try
            {
                if (_runStoryboard != null)
                {
                    _runStoryboard.Stop(this);
                    _runStoryboard.Remove(this);
                    _runStoryboard = null;
                }
                _currentPeriodSeconds = 0;

                // Remove 之后属性回到 XAML 初值,但柱塞可能停在半路,显式归位
                if (PlungerMove != null) PlungerMove.Y = 0;
                if (OutletFlowPath != null) { OutletFlowPath.StrokeDashOffset = 0; OutletFlowPath.Opacity = 0; }
                if (InletFlowPath != null) { InletFlowPath.StrokeDashOffset = 0; InletFlowPath.Opacity = 0; }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"停止运行动画失败 | PumpId:{PumpId} | 错误:{ex.Message}");
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
                if (LoadingOverlay != null) LoadingOverlay.Visibility = Visibility.Visible;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"显示加载状态失败 | PumpId:{PumpId} | 错误:{ex.Message}");
            }
        }

        public void HideLoading()
        {
            if (!_isLoading) return;
            _isLoading = false;

            try
            {
                if (LoadingOverlay != null) LoadingOverlay.Visibility = Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"隐藏加载状态失败 | PumpId:{PumpId} | 错误:{ex.Message}");
            }
        }

        /// <summary>通信失败时流量数码区闪 Err,2 秒后恢复。</summary>
        public void ShowOperationFailure()
        {
            try
            {
                HideLoading();
                if (FlowValueText == null) return;

                var originalText = FlowValueText.Text;
                var originalBrush = FlowValueText.Foreground;

                FlowValueText.Text = "Err";
                FlowValueText.Foreground = _textAlarm;

                var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                timer.Tick += (s, e) =>
                {
                    FlowValueText.Text = originalText;
                    FlowValueText.Foreground = originalBrush;
                    timer.Stop();
                };
                timer.Start();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"显示操作失败状态失败 | PumpId:{PumpId} | 错误:{ex.Message}");
            }
        }

        public void ClearError()
        {
            try
            {
                if (FlowValueText != null) FlowValueText.Foreground = _flowNormal;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"清除错误状态失败 | PumpId:{PumpId} | 错误:{ex.Message}");
            }
        }

        #endregion

        #region 拖拽控制

        private void OnDraggableAreaMouseDown(object sender, MouseButtonEventArgs e)
        {
            try
            {
                if (e.LeftButton != MouseButtonState.Pressed) return;

                PumpSelected?.Invoke(this, PumpId);
                IsSelected = true;

                _isDraggingPump = true;
                if (Parent is Canvas parent)
                {
                    _dragStartPoint = e.GetPosition(parent);
                    _originalPosition = new Point(Canvas.GetLeft(this), Canvas.GetTop(this));
                    if (double.IsNaN(_originalPosition.X)) _originalPosition.X = 0;
                    if (double.IsNaN(_originalPosition.Y)) _originalPosition.Y = 0;
                }

                CacheMode = null;
                RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.LowQuality);

                (sender as Rectangle)?.CaptureMouse();
                Opacity = 0.7;

                PumpDragStarted?.Invoke(this, new JingRuiPumpDragEventArgs
                {
                    PumpId = PumpId,
                    StartPosition = _originalPosition
                });

                e.Handled = true;
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
                if (!_isDraggingPump || e.LeftButton != MouseButtonState.Pressed) return;

                if (Parent is Canvas parent)
                {
                    var currentPoint = e.GetPosition(parent);
                    double deltaX = currentPoint.X - _dragStartPoint.X;
                    double deltaY = currentPoint.Y - _dragStartPoint.Y;

                    double newLeft = Math.Max(0, Math.Min(parent.ActualWidth - ActualWidth,
                                                          _originalPosition.X + deltaX));
                    double newTop = Math.Max(0, Math.Min(parent.ActualHeight - ActualHeight,
                                                         _originalPosition.Y + deltaY));

                    Canvas.SetLeft(this, newLeft);
                    Canvas.SetTop(this, newTop);

                    PumpDragging?.Invoke(this, new JingRuiPumpDragEventArgs
                    {
                        PumpId = PumpId,
                        CurrentPosition = new Point(newLeft, newTop)
                    });
                }
                e.Handled = true;
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
                if (!_isDraggingPump) return;
                CompletePumpDrag();
                (sender as Rectangle)?.ReleaseMouseCapture();
                e.Handled = true;
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
                if (!_isDraggingPump) return;
                CompletePumpDrag();
                (sender as Rectangle)?.ReleaseMouseCapture();
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

                PumpDragCompleted?.Invoke(this, new JingRuiPumpDragEventArgs
                {
                    PumpId = PumpId,
                    StartPosition = _originalPosition,
                    CurrentPosition = finalPosition
                });

                Opacity = 1.0;
                _isDraggingPump = false;
                RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"完成拖拽失败 | PumpId:{PumpId} | 错误:{ex.Message}");
            }
        }

        private void UpdateSelectionVisual(bool isSelected)
        {
            // 选中框由设计器统一系统接管,这里保持隐藏
            if (SelectionBorder != null)
                SelectionBorder.Visibility = Visibility.Collapsed;
        }

        #endregion

        #region IPipeSnapPoints 实现

        /// <summary>
        /// 吸附点按泵的实际接口位置给,而不是简单取四条边的中点:
        /// 进液口在底部偏左(画布 x≈59/320),出液口在顶部同一条竖线上 —— 管路接上去才对得住图。
        /// </summary>
        public List<PipeSnapPoint> GetSnapPoints()
        {
            var snapPoints = new List<PipeSnapPoint>();

            try
            {
                if (Parent is not Canvas)
                {
                    Debug.WriteLine("精睿泵未添加到 Canvas,无法获取吸附点");
                    return snapPoints;
                }

                double left = Canvas.GetLeft(this);
                double top = Canvas.GetTop(this);
                if (double.IsNaN(left)) left = 0;
                if (double.IsNaN(top)) top = 0;

                double w = ActualWidth > 0 ? ActualWidth : Width;
                double h = ActualHeight > 0 ? ActualHeight : Height;
                if (double.IsNaN(w) || w <= 0) w = 320;
                if (double.IsNaN(h) || h <= 0) h = 250;

                // 设计画布 320×250 上的接口位置,按实际渲染尺寸等比换算
                double portX = left + w * (59.0 / 320.0);

                snapPoints.Add(new PipeSnapPoint
                {
                    WorldPosition = new Point(portX, top),
                    Direction = SnapDirection.Up,
                    Description = "精睿泵出液口"
                });

                snapPoints.Add(new PipeSnapPoint
                {
                    WorldPosition = new Point(portX, top + h),
                    Direction = SnapDirection.Down,
                    Description = "精睿泵进液口"
                });

                // 出液管横段走到右边缘,从右侧接出去也说得通
                snapPoints.Add(new PipeSnapPoint
                {
                    WorldPosition = new Point(left + w, top + h * (16.0 / 250.0)),
                    Direction = SnapDirection.Right,
                    Description = "精睿泵出液管右端"
                });

                snapPoints.Add(new PipeSnapPoint
                {
                    WorldPosition = new Point(left, top + h / 2),
                    Direction = SnapDirection.Left,
                    Description = "精睿泵左侧"
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"获取吸附点失败 | PumpId:{PumpId} | 错误:{ex.Message}");
            }

            return snapPoints;
        }

        #endregion

        #region 工具

        private static double Clamp01(double v) => v < 0 ? 0 : (v > 1 ? 1 : v);

        protected void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        #endregion
    }

    /// <summary>精睿泵拖拽事件参数。</summary>
    public class JingRuiPumpDragEventArgs : EventArgs
    {
        public string PumpId { get; set; }
        public Point StartPosition { get; set; }
        public Point CurrentPosition { get; set; }
    }
}

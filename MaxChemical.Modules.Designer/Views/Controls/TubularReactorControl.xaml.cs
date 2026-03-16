using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using MaxChemical.Modules.Designer.Service;

namespace MaxChemical.Modules.Designer.Views.Controls
{
    public partial class TubularReactorControl : UserControl, IPipeSnapPoints
    {
        #region 依赖属性

        public static readonly DependencyProperty TemperatureProperty =
            DependencyProperty.Register(nameof(Temperature), typeof(double), typeof(TubularReactorControl),
                new PropertyMetadata(120.0, OnTemperatureChanged));

        public double Temperature
        {
            get => (double)GetValue(TemperatureProperty);
            set => SetValue(TemperatureProperty, value);
        }

        public static readonly DependencyProperty PressureProperty =
            DependencyProperty.Register(nameof(Pressure), typeof(double), typeof(TubularReactorControl),
                new PropertyMetadata(1.50, OnPressureChanged));

        public double Pressure
        {
            get => (double)GetValue(PressureProperty);
            set => SetValue(PressureProperty, value);
        }

        public static readonly DependencyProperty IsReactingProperty =
            DependencyProperty.Register(nameof(IsReacting), typeof(bool), typeof(TubularReactorControl),
                new PropertyMetadata(false, OnIsReactingChanged));

        public bool IsReacting
        {
            get => (bool)GetValue(IsReactingProperty);
            set => SetValue(IsReactingProperty, value);
        }

        public static readonly DependencyProperty IsSelectedProperty =
            DependencyProperty.Register(nameof(IsSelected), typeof(bool), typeof(TubularReactorControl),
                new PropertyMetadata(false, OnIsSelectedChanged));

        public bool IsSelected
        {
            get => (bool)GetValue(IsSelectedProperty);
            set => SetValue(IsSelectedProperty, value);
        }

        public static readonly DependencyProperty IsFlowEnabledProperty =
            DependencyProperty.Register(nameof(IsFlowEnabled), typeof(bool), typeof(TubularReactorControl),
                new PropertyMetadata(false, OnIsFlowEnabledChanged));

        public bool IsFlowEnabled
        {
            get => (bool)GetValue(IsFlowEnabledProperty);
            set => SetValue(IsFlowEnabledProperty, value);
        }

        public static readonly DependencyProperty FlowRateProperty =
            DependencyProperty.Register(nameof(FlowRate), typeof(double), typeof(TubularReactorControl),
                new PropertyMetadata(100.0, OnFlowRateChanged));

        public double FlowRate
        {
            get => (double)GetValue(FlowRateProperty);
            set => SetValue(FlowRateProperty, value);
        }

        public double RotationAngle
        {
            get => _rotationAngle;
            set
            {
                _rotationAngle = value % 360;
                if (_rotationAngle < 0) _rotationAngle += 360;
            }
        }

        #endregion

        #region 事件定义

        public event EventHandler<string> ReactorSelected;
        public event EventHandler<string> ReactorDeselected;
        public event EventHandler<TubularTemperatureChangedEventArgs> TemperatureChanged;
        public event EventHandler<TubularPressureChangedEventArgs> PressureChanged;
        public event EventHandler<TubularReactorStateChangedEventArgs> ReactorStateChanged;
        public event EventHandler<TubularFlowStateChangedEventArgs> FlowStateChanged;

        #endregion

        #region 私有字段

        private bool _isDragging = false;
        private Point _mouseDownPosition;
        private DateTime _mouseDownTime;
        private const int ClickTimeThresholdMs = 200;
        private const double ClickDistanceThreshold = 5.0;

        private double _rotationAngle = 0;
        private RotateTransform _rotateTransform;
        private TransformGroup _transformGroup;

        private Storyboard _inletPipeStoryboard;
        private Storyboard _vesselFluidStoryboard;
        private Storyboard _outletPipeStoryboard;

        private DispatcherTimer _simulationTimer;
        private Random _random = new Random();
        private bool _isLoaded = false;

        public string ReactorId { get; set; }

        #endregion

        #region 构造函数

        public TubularReactorControl()
        {
            InitializeComponent();

            ReactorId = Guid.NewGuid().ToString();
            Debug.WriteLine($"TubularReactorControl 构造: ReactorId={ReactorId}");

            _rotateTransform = new RotateTransform(0);
            _transformGroup = new TransformGroup();
            _transformGroup.Children.Add(_rotateTransform);
            this.RenderTransform = _transformGroup;
            this.RenderTransformOrigin = new Point(0.5, 0.5);

            bool isInDesignMode = DesignerProperties.GetIsInDesignMode(this);

            if (!isInDesignMode)
            {
                this.Loaded += OnControlLoaded;
                this.MouseEnter += OnReactorMouseEnter;
                this.MouseLeave += OnReactorMouseLeave;

                RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
                RenderOptions.SetEdgeMode(this, EdgeMode.Unspecified);
                RenderOptions.SetCachingHint(this, CachingHint.Cache);

                this.CacheMode = new BitmapCache
                {
                    RenderAtScale = 1.0,
                    SnapsToDevicePixels = true,
                    EnableClearType = false
                };

                this.SnapsToDevicePixels = true;
                this.UseLayoutRounding = true;

                InitializeSimulationTimer();
            }
        }

        private void OnControlLoaded(object sender, RoutedEventArgs e)
        {
            Debug.WriteLine($"TubularReactorControl OnControlLoaded: {ReactorId}");
            _isLoaded = true;
        }

        #endregion

        #region 流动状态控制

        private static void OnIsFlowEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is TubularReactorControl reactor)
                reactor.UpdateFlowState((bool)e.NewValue);
        }

        private void UpdateFlowState(bool isEnabled)
        {
            if (!_isLoaded)
            {
                Dispatcher.BeginInvoke(new Action(() => UpdateFlowState(isEnabled)),
                    DispatcherPriority.Loaded);
                return;
            }

            if (isEnabled)
            {
                InletFluidContainer.Visibility = Visibility.Visible;
                VesselFluidContainer.Visibility = Visibility.Visible;
                OutletFluidContainer.Visibility = Visibility.Visible;

                StartInletAnimation();
                StartVesselAnimation();
                StartOutletAnimation();

                Debug.WriteLine($"管式反应器 {ReactorId} 流动已启动");
            }
            else
            {
                StopInletAnimation();
                StopVesselAnimation();
                StopOutletAnimation();

                InletFluidContainer.Visibility = Visibility.Collapsed;
                VesselFluidContainer.Visibility = Visibility.Collapsed;
                OutletFluidContainer.Visibility = Visibility.Collapsed;

                Debug.WriteLine($"管式反应器 {ReactorId} 流动已停止");
            }

            FlowStateChanged?.Invoke(this, new TubularFlowStateChangedEventArgs
            {
                ReactorId = ReactorId,
                IsEnabled = isEnabled
            });
        }

        #endregion

        #region 进料管道动画

        private void StartInletAnimation()
        {
            StopInletAnimation();

            if (InletFluidGradientTransform == null) return;

            var storyboard = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };
            Timeline.SetDesiredFrameRate(storyboard, 30);

            var animation = new DoubleAnimation
            {
                From = -1.0,
                To = 0.0,
                Duration = TimeSpan.FromSeconds(GetFlowDuration()),
                RepeatBehavior = RepeatBehavior.Forever
            };

            Storyboard.SetTarget(animation, InletFluidGradientTransform);
            Storyboard.SetTargetProperty(animation, new PropertyPath("Y"));
            storyboard.Children.Add(animation);
            storyboard.Begin();

            _inletPipeStoryboard = storyboard;
            Debug.WriteLine("进料管道动画已启动");
        }

        private void StopInletAnimation()
        {
            _inletPipeStoryboard?.Stop();
            _inletPipeStoryboard = null;
            if (InletFluidGradientTransform != null)
                InletFluidGradientTransform.Y = 0;
        }

        #endregion

        #region 筒体流体动画

        private void StartVesselAnimation()
        {
            StopVesselAnimation();

            if (VesselFluid == null) return;

            var storyboard = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };
            Timeline.SetDesiredFrameRate(storyboard, 30);

            var opacityAnim = new DoubleAnimation
            {
                From = 0.45,
                To = 0.75,
                Duration = TimeSpan.FromSeconds(1.8),
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            };

            Storyboard.SetTarget(opacityAnim, VesselFluid);
            Storyboard.SetTargetProperty(opacityAnim, new PropertyPath("Opacity"));
            storyboard.Children.Add(opacityAnim);
            storyboard.Begin();

            _vesselFluidStoryboard = storyboard;
        }

        private void StopVesselAnimation()
        {
            _vesselFluidStoryboard?.Stop();
            _vesselFluidStoryboard = null;
            if (VesselFluid != null)
                VesselFluid.Opacity = 0;
        }

        #endregion

        #region 出料管道动画

        private void StartOutletAnimation()
        {
            StopOutletAnimation();

            if (OutletFluidGradientTransform == null) return;

            var storyboard = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };
            Timeline.SetDesiredFrameRate(storyboard, 30);

            var animation = new DoubleAnimation
            {
                From = -1.0,
                To = 0.0,
                Duration = TimeSpan.FromSeconds(GetFlowDuration()),
                RepeatBehavior = RepeatBehavior.Forever
            };

            Storyboard.SetTarget(animation, OutletFluidGradientTransform);
            Storyboard.SetTargetProperty(animation, new PropertyPath("Y"));
            storyboard.Children.Add(animation);
            storyboard.Begin();

            _outletPipeStoryboard = storyboard;
            Debug.WriteLine("出料管道动画已启动");
        }

        private void StopOutletAnimation()
        {
            _outletPipeStoryboard?.Stop();
            _outletPipeStoryboard = null;
            if (OutletFluidGradientTransform != null)
                OutletFluidGradientTransform.Y = 0;
        }

        #endregion

        #region 流速控制

        private static void OnFlowRateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is TubularReactorControl reactor && reactor.IsFlowEnabled)
                reactor.UpdateFlowSpeed();
        }

        private void UpdateFlowSpeed()
        {
            double duration = GetFlowDuration();
            UpdateStoryboardDuration(_inletPipeStoryboard, duration);
            UpdateStoryboardDuration(_vesselFluidStoryboard, duration * 0.8);
            UpdateStoryboardDuration(_outletPipeStoryboard, duration);
            Debug.WriteLine($"管式反应器流速更新: {FlowRate} kg/h → 周期 {duration:F2}s");
        }

        private double GetFlowDuration()
        {
            return Math.Max(0.4, Math.Min(6.0, 250.0 / FlowRate));
        }

        private void UpdateStoryboardDuration(Storyboard storyboard, double durationSeconds)
        {
            if (storyboard == null) return;

            bool wasRunning = storyboard.GetCurrentState() == ClockState.Active;
            if (wasRunning) storyboard.Stop();

            foreach (Timeline timeline in storyboard.Children)
            {
                if (timeline is DoubleAnimation anim)
                    anim.Duration = TimeSpan.FromSeconds(durationSeconds);
                else if (timeline is DoubleAnimationUsingKeyFrames keyAnim)
                    keyAnim.Duration = TimeSpan.FromSeconds(durationSeconds);
            }

            if (wasRunning) storyboard.Begin();
        }

        public void SetFlowRate(double rate)
        {
            FlowRate = Math.Max(10, Math.Min(rate, 500));
        }

        #endregion

        #region 温度与压力仿真

        private void InitializeSimulationTimer()
        {
            _simulationTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _simulationTimer.Tick += OnSimulationTimerTick;
        }

        private void OnSimulationTimerTick(object sender, EventArgs e)
        {
            if (!IsReacting) return;

            Temperature += (_random.NextDouble() - 0.5) * 1.0;
            Pressure += (_random.NextDouble() - 0.5) * 0.04;

            Temperature = Math.Max(60, Math.Min(Temperature, 280));
            Pressure = Math.Max(0.1, Math.Min(Pressure, 8.0));
        }

        private static void OnTemperatureChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is TubularReactorControl reactor)
            {
                reactor.TemperatureChanged?.Invoke(reactor, new TubularTemperatureChangedEventArgs
                {
                    ReactorId = reactor.ReactorId,
                    OldValue = (double)e.OldValue,
                    NewValue = (double)e.NewValue
                });
            }
        }

        private static void OnPressureChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is TubularReactorControl reactor)
            {
                reactor.PressureChanged?.Invoke(reactor, new TubularPressureChangedEventArgs
                {
                    ReactorId = reactor.ReactorId,
                    OldValue = (double)e.OldValue,
                    NewValue = (double)e.NewValue
                });
            }
        }

        #endregion

        #region 反应状态管理

        private static void OnIsReactingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is TubularReactorControl reactor)
            {
                bool isReacting = (bool)e.NewValue;
                reactor.UpdateReactingState(isReacting);

                reactor.ReactorStateChanged?.Invoke(reactor, new TubularReactorStateChangedEventArgs
                {
                    ReactorId = reactor.ReactorId,
                    IsReacting = isReacting
                });

                Debug.WriteLine($"管式反应器 {reactor.ReactorId} 状态: {(isReacting ? "运行" : "停止")}");
            }
        }

        private void UpdateReactingState(bool isReacting)
        {
            if (isReacting)
                _simulationTimer?.Start();
            else
                _simulationTimer?.Stop();
        }

        #endregion

        #region 选中状态管理

        private static void OnIsSelectedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is TubularReactorControl reactor)
            {
                bool isSelected = (bool)e.NewValue;
                reactor.UpdateSelectionVisual(isSelected);
            }
        }

        private void UpdateSelectionVisual(bool isSelected)
        {
            if (isSelected)
            {
                ReactorSelected?.Invoke(this, ReactorId);
                Debug.WriteLine($"管式反应器 {ReactorId} 已选中");
            }
            else
            {
                ReactorDeselected?.Invoke(this, ReactorId);
                Debug.WriteLine($"管式反应器 {ReactorId} 取消选中");
            }
        }

        #endregion

        #region 鼠标交互

        private void OnReactorMouseEnter(object sender, MouseEventArgs e)
        {
            if (!_isDragging)
                Debug.WriteLine($"鼠标进入管式反应器 {ReactorId}");
        }

        private void OnReactorMouseLeave(object sender, MouseEventArgs e)
        {
            if (!IsSelected)
                Debug.WriteLine($"鼠标离开管式反应器 {ReactorId}");
        }

        #endregion

        #region 公共方法

        public void StartFlow() => IsFlowEnabled = true;

        public void StopFlow() => IsFlowEnabled = false;

        #endregion

        #region IPipeSnapPoints 实现

        public List<PipeSnapPoint> GetSnapPoints()
        {
            var snapPoints = new List<PipeSnapPoint>();

            var parent = this.Parent as Canvas;
            if (parent == null) return snapPoints;

            var reactorLeft = Canvas.GetLeft(this);
            var reactorTop = Canvas.GetTop(this);
            if (double.IsNaN(reactorLeft)) reactorLeft = 0;
            if (double.IsNaN(reactorTop)) reactorTop = 0;

            // 控件旋转中心 (RenderTransformOrigin=0.5,0.5): 680/2=340, 900/2=450
            Point center = new Point(reactorLeft + 340, reactorTop + 450);
            double radians = _rotationAngle * Math.PI / 180.0;

            // 进料口顶端: 端盖 Canvas.Left=180 Top=30 W=80 H=12
            //   中心 x=220, 顶端 y=30 → 偏移 (-120, -420)
            AddSnapPoint(snapPoints, center, radians, "进料口", -120, -420, SnapDirection.Up);

            // 出料口底端: 端盖 Canvas.Left=180 Top=828 W=80 H=12
            //   中心 x=220, 底端 y=840 → 偏移 (-120, +390)
            AddSnapPoint(snapPoints, center, radians, "出料口", -120, +390, SnapDirection.Down);

            return snapPoints;
        }

        private void AddSnapPoint(List<PipeSnapPoint> snapPoints, Point center,
            double baseRotationRadians, string description,
            double offsetX, double offsetY, SnapDirection direction)
        {
            double rotatedX = offsetX * Math.Cos(baseRotationRadians)
                            - offsetY * Math.Sin(baseRotationRadians);
            double rotatedY = offsetX * Math.Sin(baseRotationRadians)
                            + offsetY * Math.Cos(baseRotationRadians);

            Point snapPosition = new Point(center.X + rotatedX, center.Y + rotatedY);

            SnapDirection rotatedDirection = GetSnapDirectionFromAngle(
                GetDirectionAngle(direction) + _rotationAngle);

            snapPoints.Add(new PipeSnapPoint
            {
                WorldPosition = snapPosition,
                Direction = rotatedDirection,
                Description = $"管式反应器{description} (角度:{_rotationAngle:F0}°)"
            });
        }

        private double GetDirectionAngle(SnapDirection direction)
        {
            return direction switch
            {
                SnapDirection.Up => 270,
                SnapDirection.Down => 90,
                SnapDirection.Left => 180,
                SnapDirection.Right => 0,
                _ => 0
            };
        }

        private SnapDirection GetSnapDirectionFromAngle(double degrees)
        {
            degrees = ((degrees % 360) + 360) % 360;

            if (degrees >= 315 || degrees < 45) return SnapDirection.Right;
            if (degrees >= 45 && degrees < 135) return SnapDirection.Down;
            if (degrees >= 135 && degrees < 225) return SnapDirection.Left;
            return SnapDirection.Up;
        }

        #endregion
    }

    #region 事件参数类

    public class TubularTemperatureChangedEventArgs : EventArgs
    {
        public string ReactorId { get; set; }
        public double OldValue { get; set; }
        public double NewValue { get; set; }
    }

    public class TubularPressureChangedEventArgs : EventArgs
    {
        public string ReactorId { get; set; }
        public double OldValue { get; set; }
        public double NewValue { get; set; }
    }

    public class TubularReactorStateChangedEventArgs : EventArgs
    {
        public string ReactorId { get; set; }
        public bool IsReacting { get; set; }
    }

    public class TubularFlowStateChangedEventArgs : EventArgs
    {
        public string ReactorId { get; set; }
        public bool IsEnabled { get; set; }
    }

    #endregion
}
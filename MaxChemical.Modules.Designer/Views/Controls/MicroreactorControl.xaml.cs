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
    public partial class MicroreactorControl : UserControl, IPipeSnapPoints
    {
        #region 依赖属性

        public static readonly DependencyProperty Temperature1Property =
            DependencyProperty.Register(nameof(Temperature1), typeof(double), typeof(MicroreactorControl),
                new PropertyMetadata(85.5, OnTemperatureChanged));

        public double Temperature1
        {
            get => (double)GetValue(Temperature1Property);
            set => SetValue(Temperature1Property, value);
        }

        public static readonly DependencyProperty Temperature2Property =
            DependencyProperty.Register(nameof(Temperature2), typeof(double), typeof(MicroreactorControl),
                new PropertyMetadata(92.3, OnTemperatureChanged));

        public double Temperature2
        {
            get => (double)GetValue(Temperature2Property);
            set => SetValue(Temperature2Property, value);
        }

        public static readonly DependencyProperty Temperature3Property =
            DependencyProperty.Register(nameof(Temperature3), typeof(double), typeof(MicroreactorControl),
                new PropertyMetadata(98.7, OnTemperatureChanged));

        public double Temperature3
        {
            get => (double)GetValue(Temperature3Property);
            set => SetValue(Temperature3Property, value);
        }

        public static readonly DependencyProperty IsReactingProperty =
            DependencyProperty.Register(nameof(IsReacting), typeof(bool), typeof(MicroreactorControl),
                new PropertyMetadata(false, OnIsReactingChanged));

        public bool IsReacting
        {
            get => (bool)GetValue(IsReactingProperty);
            set => SetValue(IsReactingProperty, value);
        }

        public static readonly DependencyProperty IsSelectedProperty =
            DependencyProperty.Register(nameof(IsSelected), typeof(bool), typeof(MicroreactorControl),
                new PropertyMetadata(false, OnIsSelectedChanged));

        public bool IsSelected
        {
            get => (bool)GetValue(IsSelectedProperty);
            set => SetValue(IsSelectedProperty, value);
        }

        public static readonly DependencyProperty IsFlow1EnabledProperty =
            DependencyProperty.Register(nameof(IsFlow1Enabled), typeof(bool), typeof(MicroreactorControl),
                new PropertyMetadata(false, OnFlow1EnabledChanged));

        public bool IsFlow1Enabled
        {
            get => (bool)GetValue(IsFlow1EnabledProperty);
            set => SetValue(IsFlow1EnabledProperty, value);
        }

        public static readonly DependencyProperty IsFlow2EnabledProperty =
            DependencyProperty.Register(nameof(IsFlow2Enabled), typeof(bool), typeof(MicroreactorControl),
                new PropertyMetadata(false, OnFlow2EnabledChanged));

        public bool IsFlow2Enabled
        {
            get => (bool)GetValue(IsFlow2EnabledProperty);
            set => SetValue(IsFlow2EnabledProperty, value);
        }

        public static readonly DependencyProperty IsFlow3EnabledProperty =
            DependencyProperty.Register(nameof(IsFlow3Enabled), typeof(bool), typeof(MicroreactorControl),
                new PropertyMetadata(false, OnFlow3EnabledChanged));

        public bool IsFlow3Enabled
        {
            get => (bool)GetValue(IsFlow3EnabledProperty);
            set => SetValue(IsFlow3EnabledProperty, value);
        }

        public static readonly DependencyProperty FlowRateProperty =
            DependencyProperty.Register(nameof(FlowRate), typeof(double), typeof(MicroreactorControl),
                new PropertyMetadata(100.0, OnFlowRateChanged));

        public double FlowRate
        {
            get => (double)GetValue(FlowRateProperty);
            set => SetValue(FlowRateProperty, value);
        }

        #endregion

        #region 事件定义

        public event EventHandler<string> ReactorSelected;
        public event EventHandler<string> ReactorDeselected;
        public event EventHandler<TemperatureChangedEventArgs> TemperatureChanged;
        public event EventHandler<ReactorStateChangedEventArgs> ReactorStateChanged;
        public event EventHandler<MicroreactorFlowStateChangedEventArgs> FlowStateChanged;

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

        private DispatcherTimer _temperatureUpdateTimer;
        private Random _random = new Random();

        // ✅ 改进后的流动控制
        private Storyboard _valveFlowStoryboard;
        private Storyboard _inputPipeStoryboard1;
        private Storyboard _inputPipeStoryboard2;
        private Storyboard _inputPipeStoryboard3;
        private Storyboard _outputPipeStoryboard;

        public string ReactorId { get; set; }
        private bool _isLoaded = false;

        // ✅ 特斯拉阀液体元素数组
        private Path[] _valveFluidPaths = new Path[9];
        private SolidColorBrush[] _valveFluidBrushes = new SolidColorBrush[9];

        #endregion

        #region 构造函数

        public MicroreactorControl()
        {
            InitializeComponent();

            ReactorId = Guid.NewGuid().ToString();
            Debug.WriteLine($"✅ MicroreactorControl 构造: ReactorId={ReactorId}");

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

                InitializeTemperatureTimer();
            }
        }

        private void OnControlLoaded(object sender, RoutedEventArgs e)
        {
            Debug.WriteLine("===== MicroreactorControl OnControlLoaded 开始 =====");

            _isLoaded = true;

            // ✅ 初始化特斯拉阀液体元素引用
            InitializeValveFluidReferences();

            UpdateTemperatureDisplays();

            Debug.WriteLine("===== MicroreactorControl OnControlLoaded 结束 =====");
        }

        /// <summary>
        /// ✅ 初始化特斯拉阀液体元素引用
        /// </summary>
        private void InitializeValveFluidReferences()
        {
            _valveFluidPaths[0] = ValveFluid_1_1;
            _valveFluidPaths[1] = ValveFluid_1_2;
            _valveFluidPaths[2] = ValveFluid_1_3;
            _valveFluidPaths[3] = ValveFluid_2_1;
            _valveFluidPaths[4] = ValveFluid_2_2;
            _valveFluidPaths[5] = ValveFluid_2_3;
            _valveFluidPaths[6] = ValveFluid_3_1;
            _valveFluidPaths[7] = ValveFluid_3_2;
            _valveFluidPaths[8] = ValveFluid_3_3;

            _valveFluidBrushes[0] = ValveFluidBrush_1_1;
            _valveFluidBrushes[1] = ValveFluidBrush_1_2;
            _valveFluidBrushes[2] = ValveFluidBrush_1_3;
            _valveFluidBrushes[3] = ValveFluidBrush_2_1;
            _valveFluidBrushes[4] = ValveFluidBrush_2_2;
            _valveFluidBrushes[5] = ValveFluidBrush_2_3;
            _valveFluidBrushes[6] = ValveFluidBrush_3_1;
            _valveFluidBrushes[7] = ValveFluidBrush_3_2;
            _valveFluidBrushes[8] = ValveFluidBrush_3_3;

            Debug.WriteLine("✅ 特斯拉阀液体元素初始化完成");
        }

        #endregion

        #region 液体流动控制

        private static void OnFlow1EnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is MicroreactorControl reactor)
            {
                bool isEnabled = (bool)e.NewValue;
                reactor.UpdateFlow1State(isEnabled);
            }
        }

        private static void OnFlow2EnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is MicroreactorControl reactor)
            {
                bool isEnabled = (bool)e.NewValue;
                reactor.UpdateFlow2State(isEnabled);
            }
        }

        private static void OnFlow3EnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is MicroreactorControl reactor)
            {
                bool isEnabled = (bool)e.NewValue;
                reactor.UpdateFlow3State(isEnabled);
            }
        }

        private void UpdateFlow1State(bool isEnabled)
        {
            Debug.WriteLine($"🌊 UpdateFlow1State: isEnabled={isEnabled}");

            if (!_isLoaded)
            {
                Debug.WriteLine("⚠️ 控件未加载,延迟启动");
                Dispatcher.BeginInvoke(new Action(() => UpdateFlow1State(isEnabled)), DispatcherPriority.Loaded);
                return;
            }

            if (isEnabled)
            {
                InputPipeFluid1Container.Visibility = Visibility.Visible;
                StartInputPipeAnimation(1);
                UpdateValveFlowState();
                Debug.WriteLine("✅ 进料1流动已启动");
            }
            else
            {
                StopInputPipeAnimation(1);
                InputPipeFluid1Container.Visibility = Visibility.Collapsed;
                UpdateValveFlowState();
                Debug.WriteLine("⏹️ 进料1流动已停止");
            }

            FlowStateChanged?.Invoke(this, new MicroreactorFlowStateChangedEventArgs
            {
                ReactorId = ReactorId,
                FlowPath = 1,
                IsEnabled = isEnabled
            });
        }

        private void UpdateFlow2State(bool isEnabled)
        {
            Debug.WriteLine($"🌊 UpdateFlow2State: isEnabled={isEnabled}");

            if (!_isLoaded)
            {
                Debug.WriteLine("⚠️ 控件未加载,延迟启动");
                Dispatcher.BeginInvoke(new Action(() => UpdateFlow2State(isEnabled)), DispatcherPriority.Loaded);
                return;
            }

            if (isEnabled)
            {
                InputPipeFluid2Container.Visibility = Visibility.Visible;
                StartInputPipeAnimation(2);
                UpdateValveFlowState();
                Debug.WriteLine("✅ 进料2流动已启动");
            }
            else
            {
                StopInputPipeAnimation(2);
                InputPipeFluid2Container.Visibility = Visibility.Collapsed;
                UpdateValveFlowState();
                Debug.WriteLine("⏹️ 进料2流动已停止");
            }

            FlowStateChanged?.Invoke(this, new MicroreactorFlowStateChangedEventArgs
            {
                ReactorId = ReactorId,
                FlowPath = 2,
                IsEnabled = isEnabled
            });
        }

        private void UpdateFlow3State(bool isEnabled)
        {
            Debug.WriteLine($"🌊 UpdateFlow3State: isEnabled={isEnabled}");

            if (!_isLoaded)
            {
                Debug.WriteLine("⚠️ 控件未加载,延迟启动");
                Dispatcher.BeginInvoke(new Action(() => UpdateFlow3State(isEnabled)), DispatcherPriority.Loaded);
                return;
            }

            if (isEnabled)
            {
                InputPipeFluid3Container.Visibility = Visibility.Visible;
                StartInputPipeAnimation(3);
                UpdateValveFlowState();
                Debug.WriteLine("✅ 进料3流动已启动");
            }
            else
            {
                StopInputPipeAnimation(3);
                InputPipeFluid3Container.Visibility = Visibility.Collapsed;
                UpdateValveFlowState();
                Debug.WriteLine("⏹️ 进料3流动已停止");
            }

            FlowStateChanged?.Invoke(this, new MicroreactorFlowStateChangedEventArgs
            {
                ReactorId = ReactorId,
                FlowPath = 3,
                IsEnabled = isEnabled
            });
        }

        /// <summary>
        /// ✅ 更新特斯拉阀流动状态（核心方法）
        /// </summary>
        private void UpdateValveFlowState()
        {
            bool anyFlowEnabled = IsFlow1Enabled || IsFlow2Enabled || IsFlow3Enabled;

            if (anyFlowEnabled)
            {
                ValveFluidLayerContainer.Visibility = Visibility.Visible;
                StartValveFlowAnimation();
                StartOutputPipeAnimation();
                Debug.WriteLine("✅ 特斯拉阀流动动画已启动");
            }
            else
            {
                StopValveFlowAnimation();
                StopOutputPipeAnimation();
                ValveFluidLayerContainer.Visibility = Visibility.Collapsed;
                Debug.WriteLine("⏹️ 特斯拉阀流动动画已停止");
            }
        }

        #endregion

        #region 进料管道动画

        /// <summary>
        /// ✅ 启动进料管道动画
        /// </summary>
        private void StartInputPipeAnimation(int pipeIndex)
        {
            TranslateTransform transform = GetInputPipeTransform(pipeIndex);
            if (transform == null) return;

            var storyboard = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };
            Timeline.SetDesiredFrameRate(storyboard, 30);

            var animation = new DoubleAnimation
            {
                From = -150,
                To = 0,
                Duration = TimeSpan.FromSeconds(2.0),
                RepeatBehavior = RepeatBehavior.Forever
            };

            Storyboard.SetTarget(animation, transform);
            Storyboard.SetTargetProperty(animation, new PropertyPath("X"));
            storyboard.Children.Add(animation);

            storyboard.Begin();
            SaveInputPipeStoryboard(pipeIndex, storyboard);

            Debug.WriteLine($"✅ 进料管道{pipeIndex}动画已启动");
        }

        /// <summary>
        /// ✅ 停止进料管道动画
        /// </summary>
        private void StopInputPipeAnimation(int pipeIndex)
        {
            Storyboard storyboard = GetInputPipeStoryboard(pipeIndex);
            if (storyboard != null)
            {
                storyboard.Stop();
                Debug.WriteLine($"⏹️ 进料管道{pipeIndex}动画已停止");
            }

            TranslateTransform transform = GetInputPipeTransform(pipeIndex);
            if (transform != null)
                transform.X = 0;
        }

        private TranslateTransform GetInputPipeTransform(int pipeIndex)
        {
            switch (pipeIndex)
            {
                case 1: return InputPipeGradientTransform1;
                case 2: return InputPipeGradientTransform2;
                case 3: return InputPipeGradientTransform3;
                default: return null;
            }
        }

        private Storyboard GetInputPipeStoryboard(int pipeIndex)
        {
            switch (pipeIndex)
            {
                case 1: return _inputPipeStoryboard1;
                case 2: return _inputPipeStoryboard2;
                case 3: return _inputPipeStoryboard3;
                default: return null;
            }
        }

        private void SaveInputPipeStoryboard(int pipeIndex, Storyboard storyboard)
        {
            switch (pipeIndex)
            {
                case 1: _inputPipeStoryboard1 = storyboard; break;
                case 2: _inputPipeStoryboard2 = storyboard; break;
                case 3: _inputPipeStoryboard3 = storyboard; break;
            }
        }

        #endregion

        #region 特斯拉阀流动动画（核心）

        /// <summary>
        /// ✅ 启动特斯拉阀流动动画（波浪式依次点亮）
        /// </summary>
        private void StartValveFlowAnimation()
        {
            StopValveFlowAnimation();

            var storyboard = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };
            Timeline.SetDesiredFrameRate(storyboard, 30);

            double baseDuration = 3.5; // 基础周期
            double valveDelay = baseDuration / 9.0; // 每个阀延迟时间

            // ✅ 为每个特斯拉阀创建依次点亮动画
            for (int i = 0; i < 9; i++)
            {
                if (_valveFluidPaths[i] == null || _valveFluidBrushes[i] == null)
                    continue;

                double delay = i * valveDelay;

                // 透明度动画（0 → 0.85 → 0）
                var opacityAnim = new DoubleAnimationUsingKeyFrames
                {
                    BeginTime = TimeSpan.FromSeconds(delay),
                    Duration = TimeSpan.FromSeconds(baseDuration)
                };

                opacityAnim.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(0)));
                opacityAnim.KeyFrames.Add(new EasingDoubleKeyFrame(0.85, KeyTime.FromPercent(0.15),
                    new QuadraticEase { EasingMode = EasingMode.EaseIn }));
                opacityAnim.KeyFrames.Add(new EasingDoubleKeyFrame(0.85, KeyTime.FromPercent(0.85)));
                opacityAnim.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(1.0),
                    new QuadraticEase { EasingMode = EasingMode.EaseOut }));

                Storyboard.SetTarget(opacityAnim, _valveFluidPaths[i]);
                Storyboard.SetTargetProperty(opacityAnim, new PropertyPath("Opacity"));
                storyboard.Children.Add(opacityAnim);

                // 颜色动画（增加亮度变化）
                var colorAnim = new ColorAnimationUsingKeyFrames
                {
                    BeginTime = TimeSpan.FromSeconds(delay),
                    Duration = TimeSpan.FromSeconds(baseDuration)
                };

                colorAnim.KeyFrames.Add(new DiscreteColorKeyFrame(Color.FromArgb(176, 133, 193, 240), KeyTime.FromPercent(0)));
                colorAnim.KeyFrames.Add(new EasingColorKeyFrame(Color.FromArgb(220, 174, 214, 241), KeyTime.FromPercent(0.15),
                    new QuadraticEase { EasingMode = EasingMode.EaseIn }));
                colorAnim.KeyFrames.Add(new LinearColorKeyFrame(Color.FromArgb(220, 174, 214, 241), KeyTime.FromPercent(0.85)));
                colorAnim.KeyFrames.Add(new EasingColorKeyFrame(Color.FromArgb(176, 133, 193, 240), KeyTime.FromPercent(1.0),
                    new QuadraticEase { EasingMode = EasingMode.EaseOut }));

                Storyboard.SetTarget(colorAnim, _valveFluidBrushes[i]);
                Storyboard.SetTargetProperty(colorAnim, new PropertyPath("Color"));
                storyboard.Children.Add(colorAnim);
            }

            // ✅ 第一排气泡（顶部3个阀，Y约15）
            AddEnhancedBubbleAnimation(storyboard, BigBubble1_1, 15, 4.5, 0);
            AddEnhancedBubbleAnimation(storyboard, BigBubble1_2, 20, 5.0, 1.5);
            AddEnhancedBubbleAnimation(storyboard, BigBubble1_3, 10, 4.8, 3.0);

            // ✅ 第二排气泡（中间3个阀，Y约125）
            AddEnhancedBubbleAnimation(storyboard, BigBubble2_1, 125, 4.5, 0.5);
            AddEnhancedBubbleAnimation(storyboard, BigBubble2_2, 130, 5.0, 2.0);
            AddEnhancedBubbleAnimation(storyboard, BigBubble2_3, 120, 4.8, 3.5);

            // ✅ 第三排气泡（底部3个阀，Y约235）
            AddEnhancedBubbleAnimation(storyboard, BigBubble3_1, 235, 4.5, 1.0);
            AddEnhancedBubbleAnimation(storyboard, BigBubble3_2, 240, 5.0, 2.5);
            AddEnhancedBubbleAnimation(storyboard, BigBubble3_3, 230, 4.8, 4.0);

            storyboard.Begin();
            _valveFlowStoryboard = storyboard;

            Debug.WriteLine("🌊 特斯拉阀波浪式流动动画已启动");
        }

        /// <summary>
        /// ✅ 添加增强气泡动画（水平穿过特斯拉阀）
        /// </summary>
        private void AddEnhancedBubbleAnimation(Storyboard storyboard, Ellipse bubble,
            double centerTop, double duration, double delay)
        {
            if (bubble == null) return;

            // 水平移动动画（从-20到390，覆盖3个阀的宽度）
            var leftAnim = new DoubleAnimation
            {
                From = -20,
                To = 390,
                Duration = TimeSpan.FromSeconds(duration),
                BeginTime = TimeSpan.FromSeconds(delay),
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            };
            Storyboard.SetTarget(leftAnim, bubble);
            Storyboard.SetTargetProperty(leftAnim, new PropertyPath("(Canvas.Left)"));
            storyboard.Children.Add(leftAnim);

            // 垂直波动（上下±6像素）
            var topAnim = new DoubleAnimation
            {
                From = centerTop - 6,
                To = centerTop + 6,
                Duration = TimeSpan.FromSeconds(1.2),
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            };
            Storyboard.SetTarget(topAnim, bubble);
            Storyboard.SetTargetProperty(topAnim, new PropertyPath("(Canvas.Top)"));
            storyboard.Children.Add(topAnim);

            // 透明度闪烁
            var opacityAnim = new DoubleAnimation
            {
                From = 0.5,
                To = 0.9,
                Duration = TimeSpan.FromSeconds(0.8),
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever
            };
            Storyboard.SetTarget(opacityAnim, bubble);
            Storyboard.SetTargetProperty(opacityAnim, new PropertyPath("Opacity"));
            storyboard.Children.Add(opacityAnim);
        }
        /// <summary>
        /// ✅ 停止特斯拉阀流动动画
        /// </summary>
        /// <summary>
        /// ✅ 停止特斯拉阀流动动画
        /// </summary>
        private void StopValveFlowAnimation()
        {
            if (_valveFlowStoryboard != null)
            {
                _valveFlowStoryboard.Stop();
                _valveFlowStoryboard = null;
            }

            // 重置所有阀的液体透明度
            for (int i = 0; i < 9; i++)
            {
                if (_valveFluidPaths[i] != null)
                    _valveFluidPaths[i].Opacity = 0;
            }

            // ✅ 重置所有气泡（9个）
            ResetBubble(BigBubble1_1);
            ResetBubble(BigBubble1_2);
            ResetBubble(BigBubble1_3);

            ResetBubble(BigBubble2_1);
            ResetBubble(BigBubble2_2);
            ResetBubble(BigBubble2_3);

            ResetBubble(BigBubble3_1);
            ResetBubble(BigBubble3_2);
            ResetBubble(BigBubble3_3);

            Debug.WriteLine("⏹️ 特斯拉阀流动动画已停止");
        }

        private void ResetBubble(Ellipse bubble)
        {
            if (bubble != null)
            {
                bubble.Opacity = 0;
                Canvas.SetLeft(bubble, -20);
            }
        }

        #endregion

        #region 产品出口动画

        /// <summary>
        /// ✅ 启动产品出口动画
        /// </summary>
        private void StartOutputPipeAnimation()
        {
            if (OutputPipeGradientTransform == null) return;

            OutputPipeFluidContainer.Visibility = Visibility.Visible;

            var storyboard = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };
            Timeline.SetDesiredFrameRate(storyboard, 30);

            var animation = new DoubleAnimation
            {
                From = -150,
                To = 0,
                Duration = TimeSpan.FromSeconds(1.8),
                RepeatBehavior = RepeatBehavior.Forever
            };

            Storyboard.SetTarget(animation, OutputPipeGradientTransform);
            Storyboard.SetTargetProperty(animation, new PropertyPath("X"));
            storyboard.Children.Add(animation);

            storyboard.Begin();
            _outputPipeStoryboard = storyboard;

            Debug.WriteLine("✅ 产品出口动画已启动");
        }

        /// <summary>
        /// ✅ 停止产品出口动画
        /// </summary>
        private void StopOutputPipeAnimation()
        {
            if (_outputPipeStoryboard != null)
            {
                _outputPipeStoryboard.Stop();
                _outputPipeStoryboard = null;
            }

            if (OutputPipeGradientTransform != null)
                OutputPipeGradientTransform.X = 0;

            OutputPipeFluidContainer.Visibility = Visibility.Collapsed;

            Debug.WriteLine("⏹️ 产品出口动画已停止");
        }

        #endregion

        #region 流速控制

        private static void OnFlowRateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is MicroreactorControl reactor)
            {
                double newRate = (double)e.NewValue;
                reactor.UpdateFlowSpeed(newRate);
            }
        }

        private void UpdateFlowSpeed(double flowRate)
        {
            // 流速范围: 10-500 kg/h
            // 动画时长: 1-8秒（反比关系）
            double baseDuration = Math.Max(1.0, Math.Min(8.0, 350.0 / flowRate));

            // 更新进料管道速度
            UpdateStoryboardDuration(_inputPipeStoryboard1, baseDuration * 0.8);
            UpdateStoryboardDuration(_inputPipeStoryboard2, baseDuration * 0.8);
            UpdateStoryboardDuration(_inputPipeStoryboard3, baseDuration * 0.8);

            // 更新特斯拉阀流动速度
            UpdateStoryboardDuration(_valveFlowStoryboard, baseDuration);

            // 更新产品出口速度
            UpdateStoryboardDuration(_outputPipeStoryboard, baseDuration * 0.7);

            Debug.WriteLine($"🚀 流速更新: {flowRate} kg/h → 基础周期 {baseDuration:F1}秒");
        }

        private void UpdateStoryboardDuration(Storyboard storyboard, double durationSeconds)
        {
            if (storyboard == null) return;

            bool wasRunning = storyboard.GetCurrentState() == ClockState.Active;

            if (wasRunning)
                storyboard.Stop();

            foreach (Timeline timeline in storyboard.Children)
            {
                if (timeline is DoubleAnimation doubleAnim)
                {
                    doubleAnim.Duration = TimeSpan.FromSeconds(durationSeconds);
                }
                else if (timeline is DoubleAnimationUsingKeyFrames keyFrameAnim)
                {
                    keyFrameAnim.Duration = TimeSpan.FromSeconds(durationSeconds);
                }
                else if (timeline is ColorAnimationUsingKeyFrames colorKeyFrameAnim)
                {
                    colorKeyFrameAnim.Duration = TimeSpan.FromSeconds(durationSeconds);
                }
            }

            if (wasRunning)
                storyboard.Begin();
        }

        #endregion

        #region 温度管理

        private void InitializeTemperatureTimer()
        {
            _temperatureUpdateTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(2)
            };
            _temperatureUpdateTimer.Tick += OnTemperatureTimerTick;
        }

        private void OnTemperatureTimerTick(object sender, EventArgs e)
        {
            if (IsReacting)
            {
                Temperature1 += (_random.NextDouble() - 0.5) * 0.5;
                Temperature2 += (_random.NextDouble() - 0.5) * 0.5;
                Temperature3 += (_random.NextDouble() - 0.5) * 0.5;

                Temperature1 = Math.Max(80, Math.Min(Temperature1, 95));
                Temperature2 = Math.Max(85, Math.Min(Temperature2, 100));
                Temperature3 = Math.Max(90, Math.Min(Temperature3, 105));
            }
        }

        private static void OnTemperatureChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is MicroreactorControl reactor)
            {
                reactor.UpdateTemperatureDisplays();

                string sensorName = e.Property.Name switch
                {
                    nameof(Temperature1) => "T1",
                    nameof(Temperature2) => "T2",
                    nameof(Temperature3) => "T3",
                    _ => "Unknown"
                };

                reactor.TemperatureChanged?.Invoke(reactor, new TemperatureChangedEventArgs
                {
                    ReactorId = reactor.ReactorId,
                    SensorName = sensorName,
                    OldValue = (double)e.OldValue,
                    NewValue = (double)e.NewValue
                });
            }
        }

        private void UpdateTemperatureDisplays()
        {
            // 温度通过XAML绑定自动更新
        }

        #endregion

        #region 反应状态管理

        private static void OnIsReactingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is MicroreactorControl reactor)
            {
                bool isReacting = (bool)e.NewValue;
                reactor.UpdateReactingState(isReacting);

                reactor.ReactorStateChanged?.Invoke(reactor, new ReactorStateChangedEventArgs
                {
                    ReactorId = reactor.ReactorId,
                    IsReacting = isReacting
                });

                Debug.WriteLine($"🔄 反应器 {reactor.ReactorId} 状态: {(isReacting ? "✅ 运行" : "❌ 停止")}");
            }
        }

        private void UpdateReactingState(bool isReacting)
        {
            if (isReacting)
            {
                _temperatureUpdateTimer?.Start();
            }
            else
            {
                _temperatureUpdateTimer?.Stop();
            }
        }

        #endregion

        #region 选中状态管理

        private static void OnIsSelectedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is MicroreactorControl reactor)
            {
                reactor.UpdateSelectionVisual((bool)e.NewValue);
            }
        }

        private void UpdateSelectionVisual(bool isSelected)
        {
            if (isSelected)
            {
                Debug.WriteLine($"✅ 反应器 {ReactorId} 已选中");
            }
            else
            {
                Debug.WriteLine($"❌ 反应器 {ReactorId} 取消选中");
            }
        }

        #endregion

        #region 鼠标交互

        private void OnReactorMouseEnter(object sender, MouseEventArgs e)
        {
            if (!_isDragging)
            {
                Debug.WriteLine($"🖱️ 鼠标进入反应器 {ReactorId}");
            }
        }

        private void OnReactorMouseLeave(object sender, MouseEventArgs e)
        {
            if (!IsSelected)
            {
                Debug.WriteLine($"🖱️ 鼠标离开反应器 {ReactorId}");
            }
        }

        #endregion

        #region 公共方法

        public void StartFlow(int flowPath)
        {
            switch (flowPath)
            {
                case 1: IsFlow1Enabled = true; break;
                case 2: IsFlow2Enabled = true; break;
                case 3: IsFlow3Enabled = true; break;
            }
        }

        public void StopFlow(int flowPath)
        {
            switch (flowPath)
            {
                case 1: IsFlow1Enabled = false; break;
                case 2: IsFlow2Enabled = false; break;
                case 3: IsFlow3Enabled = false; break;
            }
        }

        public void StartAllFlows()
        {
            IsFlow1Enabled = true;
            IsFlow2Enabled = true;
            IsFlow3Enabled = true;
        }

        public void StopAllFlows()
        {
            IsFlow1Enabled = false;
            IsFlow2Enabled = false;
            IsFlow3Enabled = false;
        }

        public void SetFlowRate(double rate)
        {
            FlowRate = Math.Max(10, Math.Min(rate, 500));
        }

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

            Point center = new Point(reactorLeft + 440, reactorTop + 300);
            double radians = _rotationAngle * Math.PI / 180.0;

            AddSnapPoint(snapPoints, center, radians, Math.PI, "进料口", -440, -17.5);
            AddSnapPoint(snapPoints, center, radians, 0, "产品出口", 440, -12.5);

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
                Description = $"反应器{description} (角度:{_rotationAngle:F0}°)"
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

    public class TemperatureChangedEventArgs : EventArgs
    {
        public string ReactorId { get; set; }
        public string SensorName { get; set; }
        public double OldValue { get; set; }
        public double NewValue { get; set; }
    }

    public class ReactorStateChangedEventArgs : EventArgs
    {
        public string ReactorId { get; set; }
        public bool IsReacting { get; set; }
    }

    public class MicroreactorFlowStateChangedEventArgs : EventArgs
    {
        public string ReactorId { get; set; }
        public int FlowPath { get; set; }
        public bool IsEnabled { get; set; }
    }

    #endregion
}
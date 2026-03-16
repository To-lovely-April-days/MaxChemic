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

        //  为每个通道单独管理Storyboard
        private Storyboard _valveFlowStoryboard1;
        private Storyboard _valveFlowStoryboard2;
        private Storyboard _valveFlowStoryboard3;
        private Storyboard _inputPipeStoryboard1;
        private Storyboard _inputPipeStoryboard2;
        private Storyboard _inputPipeStoryboard3;
        private Storyboard _outputPipeStoryboard;

        public string ReactorId { get; set; }
        private bool _isLoaded = false;

        //  特斯拉阀液体元素数组
        private Path[] _valveFluidPaths = new Path[9];
        private SolidColorBrush[] _valveFluidBrushes = new SolidColorBrush[9];
        // 添加小气泡数组
        private Ellipse[] _smallBubbles = new Ellipse[9]; // 每排3个小气泡
                                                          
        private TranslateTransform[] _valveFluidTransforms = new TranslateTransform[9];
        private Ellipse[][] _flowParticles = new Ellipse[3][]; // 每排5个粒子
        #endregion

        #region 构造函数

        public MicroreactorControl()
        {
            InitializeComponent();

            ReactorId = Guid.NewGuid().ToString();
            Debug.WriteLine($" MicroreactorControl 构造: ReactorId={ReactorId}");

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

            //  初始化特斯拉阀液体元素引用
            InitializeValveFluidReferences();

            UpdateTemperatureDisplays();

            Debug.WriteLine("===== MicroreactorControl OnControlLoaded 结束 =====");
        }

        /// <summary>
        ///  初始化特斯拉阀液体元素引用
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

            //  初始化渐变变换
            _valveFluidTransforms[0] = ValveFluidTransform_1_1;
            _valveFluidTransforms[1] = ValveFluidTransform_1_2;
            _valveFluidTransforms[2] = ValveFluidTransform_1_3;
            _valveFluidTransforms[3] = ValveFluidTransform_2_1;
            _valveFluidTransforms[4] = ValveFluidTransform_2_2;
            _valveFluidTransforms[5] = ValveFluidTransform_2_3;
            _valveFluidTransforms[6] = ValveFluidTransform_3_1;
            _valveFluidTransforms[7] = ValveFluidTransform_3_2;
            _valveFluidTransforms[8] = ValveFluidTransform_3_3;

            //  初始化流动粒子
            _flowParticles[0] = new[] { Particle1_1, Particle1_2, Particle1_3, Particle1_4, Particle1_5 };
            _flowParticles[1] = new[] { Particle2_1, Particle2_2, Particle2_3, Particle2_4, Particle2_5 };
            _flowParticles[2] = new[] { Particle3_1, Particle3_2, Particle3_3, Particle3_4, Particle3_5 };

            Debug.WriteLine(" 特斯拉阀液体流动元素初始化完成");
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
            Debug.WriteLine($" UpdateFlow1State: isEnabled={isEnabled}");

            if (!_isLoaded)
            {
                Debug.WriteLine(" 控件未加载,延迟启动");
                Dispatcher.BeginInvoke(new Action(() => UpdateFlow1State(isEnabled)), DispatcherPriority.Loaded);
                return;
            }

            if (isEnabled)
            {
                InputPipeFluid1Container.Visibility = Visibility.Visible;
                StartInputPipeAnimation(1);
                UpdateValveFlowState(1, true); //  只控制通道1
                Debug.WriteLine(" 进料1流动已启动");
            }
            else
            {
                StopInputPipeAnimation(1);
                InputPipeFluid1Container.Visibility = Visibility.Collapsed;
                UpdateValveFlowState(1, false); //  只控制通道1
                Debug.WriteLine(" 进料1流动已停止");
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
            Debug.WriteLine($" UpdateFlow2State: isEnabled={isEnabled}");

            if (!_isLoaded)
            {
                Debug.WriteLine(" 控件未加载,延迟启动");
                Dispatcher.BeginInvoke(new Action(() => UpdateFlow2State(isEnabled)), DispatcherPriority.Loaded);
                return;
            }

            if (isEnabled)
            {
                InputPipeFluid2Container.Visibility = Visibility.Visible;
                StartInputPipeAnimation(2);
                UpdateValveFlowState(2, true); //  只控制通道2
                Debug.WriteLine(" 进料2流动已启动");
            }
            else
            {
                StopInputPipeAnimation(2);
                InputPipeFluid2Container.Visibility = Visibility.Collapsed;
                UpdateValveFlowState(2, false); //  只控制通道2
                Debug.WriteLine(" 进料2流动已停止");
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
            Debug.WriteLine($" UpdateFlow3State: isEnabled={isEnabled}");

            if (!_isLoaded)
            {
                Debug.WriteLine(" 控件未加载,延迟启动");
                Dispatcher.BeginInvoke(new Action(() => UpdateFlow3State(isEnabled)), DispatcherPriority.Loaded);
                return;
            }

            if (isEnabled)
            {
                InputPipeFluid3Container.Visibility = Visibility.Visible;
                StartInputPipeAnimation(3);
                UpdateValveFlowState(3, true); //  只控制通道3
                Debug.WriteLine(" 进料3流动已启动");
            }
            else
            {
                StopInputPipeAnimation(3);
                InputPipeFluid3Container.Visibility = Visibility.Collapsed;
                UpdateValveFlowState(3, false); //  只控制通道3
                Debug.WriteLine(" 进料3流动已停止");
            }

            FlowStateChanged?.Invoke(this, new MicroreactorFlowStateChangedEventArgs
            {
                ReactorId = ReactorId,
                FlowPath = 3,
                IsEnabled = isEnabled
            });
        }

        /// <summary>
        ///  更新特定通道的特斯拉阀流动状态
        /// </summary>
        private void UpdateValveFlowState(int channel, bool isEnabled)
        {
            if (isEnabled)
            {
                ValveFluidLayerContainer.Visibility = Visibility.Visible;
                StartValveFlowAnimation(channel);

                // 检查是否有任何通道启用，决定是否启动出口动画
                if (IsFlow1Enabled || IsFlow2Enabled || IsFlow3Enabled)
                {
                    StartOutputPipeAnimation();
                }

                Debug.WriteLine($" 通道{channel}特斯拉阀流动动画已启动");
            }
            else
            {
                StopValveFlowAnimation(channel);

                // 检查是否所有通道都关闭，决定是否停止出口动画
                if (!IsFlow1Enabled && !IsFlow2Enabled && !IsFlow3Enabled)
                {
                    StopOutputPipeAnimation();
                    ValveFluidLayerContainer.Visibility = Visibility.Collapsed;
                }

                Debug.WriteLine($" 通道{channel}特斯拉阀流动动画已停止");
            }
        }

        #endregion

        #region 进料管道动画

        /// <summary>
        ///  启动进料管道动画
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

            Debug.WriteLine($" 进料管道{pipeIndex}动画已启动");
        }

        /// <summary>
        ///  停止进料管道动画
        /// </summary>
        private void StopInputPipeAnimation(int pipeIndex)
        {
            Storyboard storyboard = GetInputPipeStoryboard(pipeIndex);
            if (storyboard != null)
            {
                storyboard.Stop();
                Debug.WriteLine($" 进料管道{pipeIndex}动画已停止");
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
        ///  启动特斯拉阀流动动画（波浪式依次点亮）
        /// </summary>
        private void StartValveFlowAnimation(int channel)
        {
            StopValveFlowAnimation(channel);

            var storyboard = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };
            Timeline.SetDesiredFrameRate(storyboard, 30);

            double baseDuration = 2.5;
            double valveDelay = 0.4; // 阀之间的延迟

            int startIndex = (channel - 1) * 3;

            //  为该通道的3个特斯拉阀创建流动动画
            for (int i = 0; i < 3; i++)
            {
                int valveIndex = startIndex + i;

                if (_valveFluidPaths[valveIndex] == null || _valveFluidTransforms[valveIndex] == null)
                    continue;

                double delay = i * valveDelay;

                //  透明度动画（快速出现，持续显示）
                var opacityAnim = new DoubleAnimationUsingKeyFrames
                {
                    BeginTime = TimeSpan.FromSeconds(delay),
                    Duration = TimeSpan.FromSeconds(baseDuration)
                };

                opacityAnim.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromPercent(0)));
                opacityAnim.KeyFrames.Add(new EasingDoubleKeyFrame(0.85, KeyTime.FromPercent(0.05),
                    new QuadraticEase { EasingMode = EasingMode.EaseOut }));
                opacityAnim.KeyFrames.Add(new LinearDoubleKeyFrame(0.85, KeyTime.FromPercent(0.95)));
                opacityAnim.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(1.0),
                    new QuadraticEase { EasingMode = EasingMode.EaseIn }));

                Storyboard.SetTarget(opacityAnim, _valveFluidPaths[valveIndex]);
                Storyboard.SetTargetProperty(opacityAnim, new PropertyPath("Opacity"));
                storyboard.Children.Add(opacityAnim);

                //  水流渐变移动动画（营造流动感）
                var gradientAnim = new DoubleAnimation
                {
                    From = -2.0,
                    To = 2.0,
                    Duration = TimeSpan.FromSeconds(1.2),
                    RepeatBehavior = RepeatBehavior.Forever,
                    AutoReverse = true,
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
                };

                Storyboard.SetTarget(gradientAnim, _valveFluidTransforms[valveIndex]);
                Storyboard.SetTargetProperty(gradientAnim, new PropertyPath("X"));
                storyboard.Children.Add(gradientAnim);
            }

            //  添加流动粒子动画
            AddFlowParticlesAnimation(storyboard, channel);

            storyboard.Begin();

            switch (channel)
            {
                case 1: _valveFlowStoryboard1 = storyboard; break;
                case 2: _valveFlowStoryboard2 = storyboard; break;
                case 3: _valveFlowStoryboard3 = storyboard; break;
            }

            Debug.WriteLine($" 通道{channel}水流动画已启动");
        }
        /// <summary>
        ///  添加流动粒子动画
        /// </summary>
        private void AddFlowParticlesAnimation(Storyboard storyboard, int channel)
        {
            int particleRowIndex = channel - 1;
            if (particleRowIndex < 0 || particleRowIndex >= _flowParticles.Length)
                return;

            var particles = _flowParticles[particleRowIndex];
            if (particles == null) return;

            double[] delays = { 0, 0.3, 0.6, 0.9, 1.2 };
            double[] topOffsets = { 0, 3, -2, 4, -3 };

            for (int i = 0; i < particles.Length; i++)
            {
                if (particles[i] == null) continue;

                double particleDuration = 2.8 + (i * 0.15);

                // 水平移动
                var leftAnim = new DoubleAnimation
                {
                    From = -10,
                    To = 400,
                    Duration = TimeSpan.FromSeconds(particleDuration),
                    BeginTime = TimeSpan.FromSeconds(delays[i]),
                    RepeatBehavior = RepeatBehavior.Forever,
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
                };
                Storyboard.SetTarget(leftAnim, particles[i]);
                Storyboard.SetTargetProperty(leftAnim, new PropertyPath("(Canvas.Left)"));
                storyboard.Children.Add(leftAnim);

                // 垂直微波动
                var topAnim = new DoubleAnimation
                {
                    From = topOffsets[i] - 2,
                    To = topOffsets[i] + 2,
                    Duration = TimeSpan.FromSeconds(0.8),
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
                };
                Storyboard.SetTarget(topAnim, particles[i]);
                Storyboard.SetTargetProperty(topAnim, new PropertyPath("(Canvas.Top)"));
                storyboard.Children.Add(topAnim);

                // 透明度闪烁
                var opacityAnim = new DoubleAnimation
                {
                    From = 0.5,
                    To = 0.9,
                    Duration = TimeSpan.FromSeconds(0.6),
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever
                };
                Storyboard.SetTarget(opacityAnim, particles[i]);
                Storyboard.SetTargetProperty(opacityAnim, new PropertyPath("Opacity"));
                storyboard.Children.Add(opacityAnim);
            }
        }
      

        /// <summary>
        ///  停止指定通道的特斯拉阀流动动画
        /// </summary>
        private void StopValveFlowAnimation(int channel)
        {
            Storyboard storyboard = null;

            switch (channel)
            {
                case 1: storyboard = _valveFlowStoryboard1; break;
                case 2: storyboard = _valveFlowStoryboard2; break;
                case 3: storyboard = _valveFlowStoryboard3; break;
            }

            if (storyboard != null)
            {
                storyboard.Stop();
            }

            // 重置该通道的阀液体
            int startIndex = (channel - 1) * 3;
            for (int i = 0; i < 3; i++)
            {
                int valveIndex = startIndex + i;
                if (_valveFluidPaths[valveIndex] != null)
                    _valveFluidPaths[valveIndex].Opacity = 0;
                if (_valveFluidTransforms[valveIndex] != null)
                    _valveFluidTransforms[valveIndex].X = 0;
            }

            // 重置该通道的粒子
            ResetParticlesForChannel(channel);

            switch (channel)
            {
                case 1: _valveFlowStoryboard1 = null; break;
                case 2: _valveFlowStoryboard2 = null; break;
                case 3: _valveFlowStoryboard3 = null; break;
            }

            Debug.WriteLine($" 通道{channel}水流动画已停止");
        }
        /// <summary>
        ///  重置指定通道的粒子
        /// </summary>
        private void ResetParticlesForChannel(int channel)
        {
            int particleRowIndex = channel - 1;
            if (particleRowIndex < 0 || particleRowIndex >= _flowParticles.Length)
                return;

            var particles = _flowParticles[particleRowIndex];
            if (particles != null)
            {
                foreach (var particle in particles)
                {
                    if (particle != null)
                    {
                        particle.Opacity = 0;
                        Canvas.SetLeft(particle, -10);
                        Canvas.SetTop(particle, 0);
                    }
                }
            }
        }
     


        /// <summary>
        ///  添加增强气泡动画（水平穿过特斯拉阀）
        /// </summary>
        /// <summary>
        ///  增强气泡动画（更明显的效果）
        /// </summary>
        private void AddEnhancedBubbleAnimation(Storyboard storyboard, Ellipse bubble,
            double centerTop, double duration, double delay, bool isBigBubble)
        {
            if (bubble == null) return;

            //  水平移动动画
            var leftAnim = new DoubleAnimation
            {
                From = -30,
                To = 400,
                Duration = TimeSpan.FromSeconds(duration),
                BeginTime = TimeSpan.FromSeconds(delay),
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
            };
            Storyboard.SetTarget(leftAnim, bubble);
            Storyboard.SetTargetProperty(leftAnim, new PropertyPath("(Canvas.Left)"));
            storyboard.Children.Add(leftAnim);

            //  垂直波动（大气泡波动更大）
            double verticalRange = isBigBubble ? 10 : 6;
            var topAnim = new DoubleAnimation
            {
                From = centerTop - verticalRange,
                To = centerTop + verticalRange,
                Duration = TimeSpan.FromSeconds(1.0),
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            };
            Storyboard.SetTarget(topAnim, bubble);
            Storyboard.SetTargetProperty(topAnim, new PropertyPath("(Canvas.Top)"));
            storyboard.Children.Add(topAnim);

            //  透明度闪烁（更明显）
            var opacityAnim = new DoubleAnimation
            {
                From = 0.7,
                To = 1.0,
                Duration = TimeSpan.FromSeconds(0.6),
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever
            };
            Storyboard.SetTarget(opacityAnim, bubble);
            Storyboard.SetTargetProperty(opacityAnim, new PropertyPath("Opacity"));
            storyboard.Children.Add(opacityAnim);

            //  大小脉动（气泡呼吸效果）
            if (isBigBubble)
            {
                var scaleTransform = new ScaleTransform(1, 1, 0.5, 0.5);
                if (bubble.RenderTransform is TransformGroup transformGroup)
                {
                    transformGroup.Children.Add(scaleTransform);
                }
                else
                {
                    bubble.RenderTransform = scaleTransform;
                    bubble.RenderTransformOrigin = new Point(0.5, 0.5);
                }

                var scaleAnim = new DoubleAnimation
                {
                    From = 1.0,
                    To = 1.3,
                    Duration = TimeSpan.FromSeconds(0.8),
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
                };

                Storyboard.SetTarget(scaleAnim, scaleTransform);
                Storyboard.SetTargetProperty(scaleAnim, new PropertyPath("ScaleX"));
                storyboard.Children.Add(scaleAnim);

                var scaleAnimY = new DoubleAnimation
                {
                    From = 1.0,
                    To = 1.3,
                    Duration = TimeSpan.FromSeconds(0.8),
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
                };

                Storyboard.SetTarget(scaleAnimY, scaleTransform);
                Storyboard.SetTargetProperty(scaleAnimY, new PropertyPath("ScaleY"));
                storyboard.Children.Add(scaleAnimY);
            }
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
        ///  启动产品出口动画
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

            Debug.WriteLine(" 产品出口动画已启动");
        }

        /// <summary>
        ///  停止产品出口动画
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

            Debug.WriteLine(" 产品出口动画已停止");
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
            double baseDuration = Math.Max(1.0, Math.Min(8.0, 350.0 / flowRate));

            // 更新进料管道速度
            UpdateStoryboardDuration(_inputPipeStoryboard1, baseDuration * 0.8);
            UpdateStoryboardDuration(_inputPipeStoryboard2, baseDuration * 0.8);
            UpdateStoryboardDuration(_inputPipeStoryboard3, baseDuration * 0.8);

            //  更新每个通道的特斯拉阀流动速度
            UpdateStoryboardDuration(_valveFlowStoryboard1, baseDuration);
            UpdateStoryboardDuration(_valveFlowStoryboard2, baseDuration);
            UpdateStoryboardDuration(_valveFlowStoryboard3, baseDuration);

            // 更新产品出口速度
            UpdateStoryboardDuration(_outputPipeStoryboard, baseDuration * 0.7);

            Debug.WriteLine($" 流速更新: {flowRate} kg/h → 基础周期 {baseDuration:F1}秒");
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

                Debug.WriteLine($"反应器 {reactor.ReactorId} 状态: {(isReacting ? " 运行" : " 停止")}");
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
                Debug.WriteLine($" 反应器 {ReactorId} 已选中");
            }
            else
            {
                Debug.WriteLine($" 反应器 {ReactorId} 取消选中");
            }
        }

        #endregion

        #region 鼠标交互

        private void OnReactorMouseEnter(object sender, MouseEventArgs e)
        {
            if (!_isDragging)
            {
                Debug.WriteLine($" 鼠标进入反应器 {ReactorId}");
            }
        }

        private void OnReactorMouseLeave(object sender, MouseEventArgs e)
        {
            if (!IsSelected)
            {
                Debug.WriteLine($" 鼠标离开反应器 {ReactorId}");
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
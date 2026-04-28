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
    public partial class HeatExchangerControl : UserControl, IPipeSnapPoints
    {
        #region 依赖属性

        public static readonly DependencyProperty TemperatureProperty =
            DependencyProperty.Register(
                nameof(Temperature),
                typeof(double),
                typeof(HeatExchangerControl),
                new PropertyMetadata(85.0, OnTemperatureChanged));

        public double Temperature
        {
            get => (double)GetValue(TemperatureProperty);
            set => SetValue(TemperatureProperty, value);
        }

        public static readonly DependencyProperty PressureProperty =
            DependencyProperty.Register(
                nameof(Pressure),
                typeof(double),
                typeof(HeatExchangerControl),
                new PropertyMetadata(2.5, OnPressureChanged));

        public double Pressure
        {
            get => (double)GetValue(PressureProperty);
            set => SetValue(PressureProperty, value);
        }

        public static readonly DependencyProperty IsOperatingProperty =
            DependencyProperty.Register(
                nameof(IsOperating),
                typeof(bool),
                typeof(HeatExchangerControl),
                new PropertyMetadata(false, OnIsOperatingChanged));

        public bool IsOperating
        {
            get => (bool)GetValue(IsOperatingProperty);
            set => SetValue(IsOperatingProperty, value);
        }

        public static readonly DependencyProperty IsSelectedProperty =
            DependencyProperty.Register(
                nameof(IsSelected),
                typeof(bool),
                typeof(HeatExchangerControl),
                new PropertyMetadata(false, OnIsSelectedChanged));

        public bool IsSelected
        {
            get => (bool)GetValue(IsSelectedProperty);
            set => SetValue(IsSelectedProperty, value);
        }

        #endregion

        #region 事件定义

        public event EventHandler<string> ExchangerSelected;
        public event EventHandler<string> ExchangerDeselected;
        public event EventHandler<ExchangerRotateEventArgs> ExchangerRotateStarted;
        public event EventHandler<ExchangerRotateEventArgs> ExchangerRotating;
        public event EventHandler<ExchangerRotateEventArgs> ExchangerRotateCompleted;
        public event EventHandler<ExchangerStateChangedEventArgs> OperatingStateChanged;

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

        private Storyboard _hotFluidAnimation;
        private Storyboard _coldFluidAnimation;
        private Storyboard _dataGlowAnimation;

        public string ExchangerId { get; set; }

        #endregion

        #region 构造函数

        public HeatExchangerControl()
        {
            InitializeComponent();

            ExchangerId = Guid.NewGuid().ToString();
            Debug.WriteLine($" HeatExchangerControl 构造: ExchangerId={ExchangerId}");

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
                this.MouseEnter += OnExchangerMouseEnter;
                this.MouseLeave += OnExchangerMouseLeave;
                SetupRotateAnchorEvents();

                // ==========  硬件加速配置 ==========
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

                //  液体动画层禁用缓存（使用 GPU）
                if (HotFluidFlowContainer != null)
                {
                    HotFluidFlowContainer.CacheMode = null;
                }
                if (ColdFluidFlowContainer != null)
                {
                    ColdFluidFlowContainer.CacheMode = null;
                }

                Debug.WriteLine($"换热器 {ExchangerId} 硬件加速已启用");
            }
        }

        private void OnControlLoaded(object sender, RoutedEventArgs e)
        {
            Debug.WriteLine("===== OnControlLoaded 开始 =====");

            UpdateTemperatureText(Temperature);
            UpdatePressureText(Pressure);

            // 移除：启动状态指示灯动画（已删除）

            UpdateFlowAnimationState(IsOperating);

            Debug.WriteLine("===== OnControlLoaded 结束 =====");
        }

        #endregion

        #region 动画控制

        private static void OnIsOperatingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is HeatExchangerControl exchanger)
            {
                bool isOperating = (bool)e.NewValue;
                exchanger.UpdateFlowAnimationState(isOperating);

                exchanger.OperatingStateChanged?.Invoke(exchanger, new ExchangerStateChangedEventArgs
                {
                    ExchangerId = exchanger.ExchangerId,
                    IsOperating = isOperating
                });

                Debug.WriteLine($"换热器 {exchanger.ExchangerId} 运行状态变更: {(isOperating ? " 启动" : " 停止")}");
            }
        }

        private void UpdateFlowAnimationState(bool isOperating)
        {
            if (isOperating)
            {
                if (HotFluidFlowContainer != null)
                    HotFluidFlowContainer.Visibility = Visibility.Visible;
                if (ColdFluidFlowContainer != null)
                    ColdFluidFlowContainer.Visibility = Visibility.Visible;

                CreateAndStartFluidAnimations();
            }
            else
            {
                StopAllFluidAnimations();

                if (HotFluidFlowContainer != null)
                    HotFluidFlowContainer.Visibility = Visibility.Collapsed;
                if (ColdFluidFlowContainer != null)
                    ColdFluidFlowContainer.Visibility = Visibility.Collapsed;
                if (HeatFlowContainer != null)
                    HeatFlowContainer.Visibility = Visibility.Collapsed;
                if (ColdFlowContainer != null)
                    ColdFlowContainer.Visibility = Visibility.Collapsed;

                // 停止核心动画
                var heatCoreGlow = this.Resources["HeatExchangePulse"] as Storyboard;
                heatCoreGlow?.Stop();
                var tempPulse = this.Resources["TempIndicatorPulse"] as Storyboard;
                tempPulse?.Stop();
            }
        }
        private void CreateAndStartFluidAnimations()
        {
            Debug.WriteLine("🎬 开始创建管道液体流动动画...");

            StopAllFluidAnimations();

            // ========== 热侧进口液体流动 ==========
            if (HotFluidFlowContainer != null)
            {
                HotFluidFlowContainer.Visibility = Visibility.Visible;

                _hotFluidAnimation = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };
                Timeline.SetDesiredFrameRate(_hotFluidAnimation, 60);

                // 渐变动画
                if (HotFluidGradientTransform != null)
                {
                    var gradientAnim = new DoubleAnimation
                    {
                        From = 0,
                        To = 30,
                        Duration = TimeSpan.FromSeconds(1.2),
                        RepeatBehavior = RepeatBehavior.Forever
                    };
                    Storyboard.SetTarget(gradientAnim, HotFluidGradientTransform);
                    Storyboard.SetTargetProperty(gradientAnim, new PropertyPath("X"));
                    _hotFluidAnimation.Children.Add(gradientAnim);
                }

                // 波纹动画
                if (HotWaveTransform1 != null)
                {
                    var waveAnim1 = new DoubleAnimation
                    {
                        From = 0,
                        To = 28,
                        Duration = TimeSpan.FromSeconds(0.9),
                        RepeatBehavior = RepeatBehavior.Forever
                    };
                    Storyboard.SetTarget(waveAnim1, HotWaveTransform1);
                    Storyboard.SetTargetProperty(waveAnim1, new PropertyPath("X"));
                    _hotFluidAnimation.Children.Add(waveAnim1);
                }

                if (HotWaveTransform2 != null)
                {
                    var waveAnim2 = new DoubleAnimation
                    {
                        From = 0,
                        To = 28,
                        Duration = TimeSpan.FromSeconds(1.1),
                        RepeatBehavior = RepeatBehavior.Forever
                    };
                    Storyboard.SetTarget(waveAnim2, HotWaveTransform2);
                    Storyboard.SetTargetProperty(waveAnim2, new PropertyPath("X"));
                    _hotFluidAnimation.Children.Add(waveAnim2);
                }

                // 粒子流动
                AnimatePipeParticles(_hotFluidAnimation, "HotFluidParticle", 4, 35, 0.7, 1.1);

                _hotFluidAnimation.Begin();
            }

            // ========== 热侧出口液体流动 ==========
            if (HotFluidOutletContainer != null)
            {
                HotFluidOutletContainer.Visibility = Visibility.Visible;

                if (HotOutletGradientTransform != null)
                {
                    var outletAnim = new DoubleAnimation
                    {
                        From = 0,
                        To = 30,
                        Duration = TimeSpan.FromSeconds(1.2),
                        RepeatBehavior = RepeatBehavior.Forever
                    };
                    outletAnim.BeginAnimation(TranslateTransform.XProperty, outletAnim);
                    HotOutletGradientTransform.BeginAnimation(TranslateTransform.XProperty, outletAnim);
                }

                if (HotOutletWaveTransform != null)
                {
                    var waveAnim = new DoubleAnimation
                    {
                        From = 0,
                        To = 28,
                        Duration = TimeSpan.FromSeconds(0.9),
                        RepeatBehavior = RepeatBehavior.Forever
                    };
                    HotOutletWaveTransform.BeginAnimation(TranslateTransform.XProperty, waveAnim);
                }
            }

            // ========== 冷侧进口液体流动 ==========
            if (ColdFluidFlowContainer != null)
            {
                ColdFluidFlowContainer.Visibility = Visibility.Visible;

                _coldFluidAnimation = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };
                Timeline.SetDesiredFrameRate(_coldFluidAnimation, 60);

                if (ColdFluidGradientTransform != null)
                {
                    var gradientAnim = new DoubleAnimation
                    {
                        From = 0,
                        To = 30,
                        Duration = TimeSpan.FromSeconds(1.2),
                        RepeatBehavior = RepeatBehavior.Forever
                    };
                    Storyboard.SetTarget(gradientAnim, ColdFluidGradientTransform);
                    Storyboard.SetTargetProperty(gradientAnim, new PropertyPath("X"));
                    _coldFluidAnimation.Children.Add(gradientAnim);
                }

                if (ColdWaveTransform1 != null)
                {
                    var waveAnim1 = new DoubleAnimation
                    {
                        From = 0,
                        To = 28,
                        Duration = TimeSpan.FromSeconds(0.9),
                        RepeatBehavior = RepeatBehavior.Forever
                    };
                    Storyboard.SetTarget(waveAnim1, ColdWaveTransform1);
                    Storyboard.SetTargetProperty(waveAnim1, new PropertyPath("X"));
                    _coldFluidAnimation.Children.Add(waveAnim1);
                }

                if (ColdWaveTransform2 != null)
                {
                    var waveAnim2 = new DoubleAnimation
                    {
                        From = 0,
                        To = 28,
                        Duration = TimeSpan.FromSeconds(1.1),
                        RepeatBehavior = RepeatBehavior.Forever
                    };
                    Storyboard.SetTarget(waveAnim2, ColdWaveTransform2);
                    Storyboard.SetTargetProperty(waveAnim2, new PropertyPath("X"));
                    _coldFluidAnimation.Children.Add(waveAnim2);
                }

                // 粒子流动
                AnimatePipeParticles(_coldFluidAnimation, "ColdFluidParticle", 4, 35, 0.7, 1.1);

                _coldFluidAnimation.Begin();
            }

            // ========== 冷侧出口液体流动 ==========
            if (ColdFluidOutletContainer != null)
            {
                ColdFluidOutletContainer.Visibility = Visibility.Visible;

                if (ColdOutletGradientTransform != null)
                {
                    var outletAnim = new DoubleAnimation
                    {
                        From = 0,
                        To = 30,
                        Duration = TimeSpan.FromSeconds(1.2),
                        RepeatBehavior = RepeatBehavior.Forever
                    };
                    ColdOutletGradientTransform.BeginAnimation(TranslateTransform.XProperty, outletAnim);
                }

                if (ColdOutletWaveTransform != null)
                {
                    var waveAnim = new DoubleAnimation
                    {
                        From = 0,
                        To = 28,
                        Duration = TimeSpan.FromSeconds(0.9),
                        RepeatBehavior = RepeatBehavior.Forever
                    };
                    ColdOutletWaveTransform.BeginAnimation(TranslateTransform.XProperty, waveAnim);
                }
            }

            // 核心热交换动画
            StartHeatExchangeAnimation();
        }
        // 管道粒子动画
        private void AnimatePipeParticles(Storyboard storyboard, string particlePrefix, int count, double distance, double minDuration, double maxDuration)
        {
            for (int i = 1; i <= count; i++)
            {
                var particle = this.FindName($"{particlePrefix}{i}") as Ellipse;
                if (particle != null)
                {
                    double duration = minDuration + (maxDuration - minDuration) * (i - 1) / (count - 1);
                    double startPos = -5 - (i - 1) * 8;

                    var particleAnim = new DoubleAnimation
                    {
                        From = startPos,
                        To = distance,
                        Duration = TimeSpan.FromSeconds(duration),
                        RepeatBehavior = RepeatBehavior.Forever
                    };

                    Storyboard.SetTarget(particleAnim, particle);
                    Storyboard.SetTargetProperty(particleAnim, new PropertyPath("(Canvas.Left)"));
                    storyboard.Children.Add(particleAnim);
                }
            }
        }

        // 新增方法：流体粒子动画
        private void AnimateFluidParticles(Storyboard storyboard, string particlePrefix, int count, double distance, double minDuration, double maxDuration)
        {
            for (int i = 1; i <= count; i++)
            {
                var particle = this.FindName($"{particlePrefix}{i}") as Ellipse;
                if (particle != null)
                {
                    double duration = minDuration + (maxDuration - minDuration) * (i - 1) / (count - 1);

                    var particleAnim = new DoubleAnimation
                    {
                        From = -5,
                        To = distance,
                        Duration = TimeSpan.FromSeconds(duration),
                        RepeatBehavior = RepeatBehavior.Forever
                    };

                    Storyboard.SetTarget(particleAnim, particle);
                    Storyboard.SetTargetProperty(particleAnim, new PropertyPath("(Canvas.Left)"));
                    storyboard.Children.Add(particleAnim);
                }
            }
        }


        //  新增方法：热交换核心粒子动画
        private void StartHeatExchangeAnimation()
        {
            if (HeatFlowContainer != null)
            {
                HeatFlowContainer.Visibility = Visibility.Visible;
            }
            if (ColdFlowContainer != null)
            {
                ColdFlowContainer.Visibility = Visibility.Visible;
            }

            // 核心发光动画
            var heatCoreGlow = this.Resources["HeatExchangePulse"] as Storyboard;
            heatCoreGlow?.Begin();

            var tempPulse = this.Resources["TempIndicatorPulse"] as Storyboard;
            tempPulse?.Begin();

            // 热流粒子（更大更明显）
            for (int i = 1; i <= 5; i++)
            {
                var particle = this.FindName($"HeatParticle{i}") as Ellipse;
                if (particle != null)
                {
                    // 设置更大的粒子
                    particle.Width = 8;
                    particle.Height = 8;

                    var anim = new DoubleAnimation
                    {
                        From = -10 - (i - 1) * 25,
                        To = 230,
                        Duration = TimeSpan.FromSeconds(1.8 + i * 0.2), // 更快的流动
                        RepeatBehavior = RepeatBehavior.Forever
                    };
                    particle.BeginAnimation(Canvas.TopProperty, anim);

                    var leftAnim = new DoubleAnimation
                    {
                        From = 15 + i * 20,
                        To = 15 + i * 20 + (i % 2 == 0 ? 15 : -15), // 更大的摆动
                        Duration = TimeSpan.FromSeconds(0.8),
                        AutoReverse = true,
                        RepeatBehavior = RepeatBehavior.Forever
                    };
                    particle.BeginAnimation(Canvas.LeftProperty, leftAnim);

                    // 添加不透明度动画
                    var opacityAnim = new DoubleAnimation
                    {
                        From = 0.6,
                        To = 1.0,
                        Duration = TimeSpan.FromSeconds(0.5),
                        AutoReverse = true,
                        RepeatBehavior = RepeatBehavior.Forever
                    };
                    particle.BeginAnimation(Ellipse.OpacityProperty, opacityAnim);
                }
            }

            // 冷流粒子（更大更明显）
            for (int i = 1; i <= 4; i++)
            {
                var particle = this.FindName($"ColdParticle{i}") as Ellipse;
                if (particle != null)
                {
                    particle.Width = 7;
                    particle.Height = 7;

                    var anim = new DoubleAnimation
                    {
                        From = 230 + (i - 1) * 25,
                        To = -10,
                        Duration = TimeSpan.FromSeconds(2 + i * 0.2),
                        RepeatBehavior = RepeatBehavior.Forever
                    };
                    particle.BeginAnimation(Canvas.TopProperty, anim);

                    var leftAnim = new DoubleAnimation
                    {
                        From = 20 + i * 25,
                        To = 20 + i * 25 + (i % 2 == 0 ? -12 : 12),
                        Duration = TimeSpan.FromSeconds(1),
                        AutoReverse = true,
                        RepeatBehavior = RepeatBehavior.Forever
                    };
                    particle.BeginAnimation(Canvas.LeftProperty, leftAnim);

                    var opacityAnim = new DoubleAnimation
                    {
                        From = 0.5,
                        To = 1.0,
                        Duration = TimeSpan.FromSeconds(0.6),
                        AutoReverse = true,
                        RepeatBehavior = RepeatBehavior.Forever
                    };
                    particle.BeginAnimation(Ellipse.OpacityProperty, opacityAnim);
                }
            }

            Debug.WriteLine("🔥 增强版热交换核心动画已启动!");
        }

        private void StopAllFluidAnimations()
        {
            if (_hotFluidAnimation != null)
            {
                _hotFluidAnimation.Stop();
                _hotFluidAnimation = null;
            }
            if (_coldFluidAnimation != null)
            {
                _coldFluidAnimation.Stop();
                _coldFluidAnimation = null;
            }

            // 重置所有容器
            if (HotFluidFlowContainer != null)
                HotFluidFlowContainer.Visibility = Visibility.Collapsed;
            if (HotFluidOutletContainer != null)
                HotFluidOutletContainer.Visibility = Visibility.Collapsed;
            if (ColdFluidFlowContainer != null)
                ColdFluidFlowContainer.Visibility = Visibility.Collapsed;
            if (ColdFluidOutletContainer != null)
                ColdFluidOutletContainer.Visibility = Visibility.Collapsed;

            Debug.WriteLine(" 液体动画已停止");
        }

        #endregion

        #region 锚点显示控制

        private void OnExchangerMouseEnter(object sender, MouseEventArgs e)
        {
            if (!_isDragging && !_isRotating)
            {
                ShowAllAnchors();
            }
        }

        private void OnExchangerMouseLeave(object sender, MouseEventArgs e)
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
            if (d is HeatExchangerControl exchanger)
            {
                exchanger.UpdateSelectionVisual((bool)e.NewValue);
            }
        }

        private void UpdateSelectionVisual(bool isSelected)
        {
            if (SelectionBorder != null)
                SelectionBorder.Visibility = Visibility.Collapsed;

            // 锚点显隐保持原有逻辑
            if (isSelected) ShowAllAnchors();
            else if (!this.IsMouseOver && !_isRotating) HideAllAnchors();
        }

        private static void OnTemperatureChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is HeatExchangerControl exchanger)
            {
                double newValue = (double)e.NewValue;
                exchanger.UpdateTemperatureText(newValue);
                Debug.WriteLine($"换热器 {exchanger.ExchangerId} 温度: {newValue:F1}°C");
            }
        }

        private static void OnPressureChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is HeatExchangerControl exchanger)
            {
                double newValue = (double)e.NewValue;
                exchanger.UpdatePressureText(newValue);
                Debug.WriteLine($"换热器 {exchanger.ExchangerId} 压力: {newValue:F1} MPa");
            }
        }

        private void UpdateTemperatureText(double value)
        {
            if (TemperatureText != null)
            {
                // 只显示数值，单位已在XAML中分离
                TemperatureText.Text = $"{value:F1}";
            }
        }

        private void UpdatePressureText(double value)
        {
            if (PressureText != null)
            {
                // 只显示数值，单位已在XAML中分离
                PressureText.Text = $"{value:F1}";
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

                ExchangerSelected?.Invoke(this, this.ExchangerId);
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
                    IsOperating = !IsOperating;
                    Debug.WriteLine($" 点击切换运行状态: {(IsOperating ? " 启动" : " 停止")}");
                    ShowTemporarySelection();
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
            ExchangerSelected?.Invoke(this, this.ExchangerId);

            var autoHideTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(500)
            };

            autoHideTimer.Tick += (s, args) =>
            {
                this.IsSelected = false;
                ExchangerDeselected?.Invoke(this, this.ExchangerId);
                autoHideTimer.Stop();
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

                ExchangerRotateStarted?.Invoke(this, new ExchangerRotateEventArgs
                {
                    ExchangerId = this.ExchangerId,
                    OriginalAngle = _originalRotation
                });

                ShowRotationLabel();
                e.Handled = true;
            }
        }

        private void CompleteRotate()
        {
            if (!_isRotating) return;

            ExchangerRotateCompleted?.Invoke(this, new ExchangerRotateEventArgs
            {
                ExchangerId = this.ExchangerId,
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

            ExchangerRotating?.Invoke(this, new ExchangerRotateEventArgs
            {
                ExchangerId = this.ExchangerId,
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

        public void SetTemperature(double value)
        {
            Temperature = value;
        }

        public void SetPressure(double value)
        {
            Pressure = value;
        }

        public void StartOperation()
        {
            IsOperating = true;
        }

        public void StopOperation()
        {
            IsOperating = false;
        }

        #endregion

        #region IPipeSnapPoints 实现

        // 在 AddSnapPoint 方法调用中更新偏移量
        public List<PipeSnapPoint> GetSnapPoints()
        {
            var snapPoints = new List<PipeSnapPoint>();

            var parent = this.Parent as Canvas;
            if (parent == null) return snapPoints;

            var exchangerLeft = Canvas.GetLeft(this);
            var exchangerTop = Canvas.GetTop(this);
            if (double.IsNaN(exchangerLeft)) exchangerLeft = 0;
            if (double.IsNaN(exchangerTop)) exchangerTop = 0;

            Point center = new Point(exchangerLeft + 140, exchangerTop + 210);
            double radians = _rotationAngle * Math.PI / 180.0;

            // 热侧进口（左上）
            AddSnapPoint(snapPoints, center, radians, Math.PI, "热侧进口", 95, -90);

            // 热侧出口（右上）
            AddSnapPoint(snapPoints, center, radians, 0, "热侧出口", -95, -90);

            // 冷侧进口（左下）
            AddSnapPoint(snapPoints, center, radians, Math.PI, "冷侧进口", 95, 112);

            // 冷侧出口（右下）
            AddSnapPoint(snapPoints, center, radians, 0, "冷侧出口", -95, 112);

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
                Description = $"换热器{description} (角度:{_rotationAngle:F0}°)"
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

    public class ExchangerRotateEventArgs : EventArgs
    {
        public string ExchangerId { get; set; }
        public double OriginalAngle { get; set; }
        public double CurrentAngle { get; set; }
    }

    public class ExchangerStateChangedEventArgs : EventArgs
    {
        public string ExchangerId { get; set; }
        public bool IsOperating { get; set; }
    }

    #endregion
}
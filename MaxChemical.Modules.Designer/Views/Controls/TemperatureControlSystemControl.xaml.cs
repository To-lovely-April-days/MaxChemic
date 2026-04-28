using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using MaxChemical.Modules.Designer.Service;

namespace MaxChemical.Modules.Designer.Views.Controls
{
    /// <summary>
    /// 高低温循环装置控件
    /// 状态指示: LCD边框颜色 + 指示灯 + 屏幕覆盖层 + 边框辉光
    /// 无自动恢复, 所有状态需手动切换
    /// </summary>
    public partial class TemperatureControlSystemControl : UserControl, INotifyPropertyChanged, IPipeSnapPoints
    {
        #region 状态枚举

        /// <summary>
        /// 设备视觉状态
        /// </summary>
        private enum DeviceVisualState
        {
            Idle,       // 灰色
            Running,    // 青色
            Loading,    // 琥珀色
            Error       // 红色
        }

        private DeviceVisualState _currentVisualState = DeviceVisualState.Idle;

        #endregion

        #region 事件定义

        public event PropertyChangedEventHandler PropertyChanged;
        public event EventHandler<TempSystemDragEventArgs> SystemDragStarted;
        public event EventHandler<TempSystemDragEventArgs> SystemDragging;
        public event EventHandler<TempSystemDragEventArgs> SystemDragCompleted;
        public event EventHandler<string> SystemSelected;
        public event EventHandler<string> SystemDeselected;

        public event EventHandler<TemperatureSetRequestEventArgs> TemperatureSetRequested;
        public event EventHandler<CirculationRequestEventArgs> CirculationRequested;
        public event EventHandler<HeatingRequestEventArgs> HeatingRequested;
        public event EventHandler<CoolingRequestEventArgs> CoolingRequested;

        #endregion

        #region 字段

        public string SystemId { get; set; }

        private bool _isDragging = false;
        private Point _dragStartPoint;
        private Point _originalPosition;

        private bool _isCirculating = false;
        private bool _isHeating = false;
        private bool _isCooling = false;
        private bool _isLoading = false;
        private bool _isError = false;

        public bool IsLoading => _isLoading;
        public bool IsError => _isError;

        #endregion

        #region 依赖属性

        public static readonly DependencyProperty SetTemperatureProperty =
            DependencyProperty.Register(
                nameof(SetTemperature),
                typeof(double),
                typeof(TemperatureControlSystemControl),
                new PropertyMetadata(-40.0, OnSetTemperatureChanged));

        public double SetTemperature
        {
            get => (double)GetValue(SetTemperatureProperty);
            set => SetValue(SetTemperatureProperty, value);
        }

        private static void OnSetTemperatureChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is TemperatureControlSystemControl control)
                control.OnPropertyChanged(nameof(SetTemperature));
        }

        public static readonly DependencyProperty CurrentTemperatureProperty =
            DependencyProperty.Register(
                nameof(CurrentTemperature),
                typeof(double),
                typeof(TemperatureControlSystemControl),
                new PropertyMetadata(24.5, OnCurrentTemperatureChanged));

        public double CurrentTemperature
        {
            get => (double)GetValue(CurrentTemperatureProperty);
            set => SetValue(CurrentTemperatureProperty, value);
        }

        private static void OnCurrentTemperatureChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is TemperatureControlSystemControl control)
                control.OnPropertyChanged(nameof(CurrentTemperature));
        }

        public static readonly DependencyProperty IsSelectedProperty =
            DependencyProperty.Register(
                nameof(IsSelected),
                typeof(bool),
                typeof(TemperatureControlSystemControl),
                new PropertyMetadata(false, OnIsSelectedChanged));

        public bool IsSelected
        {
            get => (bool)GetValue(IsSelectedProperty);
            set => SetValue(IsSelectedProperty, value);
        }

        private static void OnIsSelectedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is TemperatureControlSystemControl control)
                control.UpdateSelectionVisual((bool)e.NewValue);
        }

        #endregion

        #region 构造函数

        public TemperatureControlSystemControl()
        {
            InitializeComponent();
            SystemId = Guid.NewGuid().ToString();
            this.DataContext = this;

            bool isInDesignMode = DesignerProperties.GetIsInDesignMode(this);
            this.Loaded += OnSystemControlLoaded;

            if (!isInDesignMode)
            {
                RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
                RenderOptions.SetEdgeMode(this, EdgeMode.Unspecified);
                RenderOptions.SetCachingHint(this, CachingHint.Cache);

                CacheMode = new BitmapCache
                {
                    RenderAtScale = 1.0,
                    SnapsToDevicePixels = true,
                    EnableClearType = false
                };

                this.SnapsToDevicePixels = true;
                this.UseLayoutRounding = true;
            }

            Debug.WriteLine($"温控系统控件已创建 | SystemId:{SystemId}");
        }

        private void OnSystemControlLoaded(object sender, RoutedEventArgs e)
        {
            try
            {
                ApplyVisualState(DeviceVisualState.Idle);
                Debug.WriteLine($"温控系统控件已加载完成 | SystemId:{SystemId}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"温控系统控件加载失败 | SystemId:{SystemId} | 错误:{ex.Message}");
            }
        }

        #endregion

        #region 核心视觉状态机

        /// <summary>
        /// 应用视觉状态 - 统一入口, 一次性更新所有视觉元素
        /// </summary>
        private void ApplyVisualState(DeviceVisualState state)
        {
            _currentVisualState = state;

            switch (state)
            {
                case DeviceVisualState.Idle:
                    ApplyIdleVisual();
                    break;
                case DeviceVisualState.Running:
                    ApplyRunningVisual();
                    break;
                case DeviceVisualState.Loading:
                    ApplyLoadingVisual();
                    break;
                case DeviceVisualState.Error:
                    ApplyErrorVisual();
                    break;
            }

            UpdateButtonStates();
            Debug.WriteLine($"视觉状态切换 | SystemId:{SystemId} | 状态:{state}");
        }

        /// <summary>
        /// 空闲状态: 深灰边框, 灰色指示灯, 无辉光, 无覆盖
        /// </summary>
        private void ApplyIdleVisual()
        {
            SetLCDFrame(
                this.Resources["IdleFrameGradient"] as Brush,
                Color.FromRgb(45, 50, 56),
                Colors.Transparent, 0, 0);

            SetIndicator(
                Color.FromRgb(158, 158, 158),
                Colors.Transparent, 0,
                "就绪", Color.FromRgb(93, 99, 105));

            SetAccentBar(this.Resources["AccentRedGradient"] as Brush);
            SetScreenOverlay(Colors.Transparent, 0);
        }

        /// <summary>
        /// 运行状态: 明亮青色边框, 绿色指示灯, 青色辉光, 微弱青色覆盖
        /// </summary>
        private void ApplyRunningVisual()
        {
            SetLCDFrame(
                this.Resources["RunningFrameGradient"] as Brush,
                Color.FromRgb(0, 105, 92),
                Color.FromRgb(0, 137, 123), 15, 0.7);

            SetIndicator(
                Color.FromRgb(76, 175, 80),
                Color.FromRgb(76, 175, 80), 10,
                "运行", Color.FromRgb(46, 125, 50));

            SetAccentBar(new SolidColorBrush(Color.FromRgb(0, 137, 123)));
            SetScreenOverlay(Color.FromRgb(0, 137, 123), 0.06);
        }

        /// <summary>
        /// 加载状态: 明亮琥珀色边框, 琥珀指示灯, 琥珀辉光, 琥珀覆盖
        /// </summary>
        private void ApplyLoadingVisual()
        {
            SetLCDFrame(
                this.Resources["LoadingFrameGradient"] as Brush,
                Color.FromRgb(184, 134, 11),
                Color.FromRgb(230, 168, 23), 18, 0.8);

            SetIndicator(
                Color.FromRgb(255, 193, 7),
                Color.FromRgb(255, 193, 7), 12,
                "处理", Color.FromRgb(200, 150, 0));

            SetAccentBar(new SolidColorBrush(Color.FromRgb(230, 168, 23)));
            SetScreenOverlay(Color.FromRgb(255, 193, 7), 0.08);
        }

        /// <summary>
        /// 异常状态: 明亮红色边框, 红色指示灯, 红色辉光, 红色覆盖
        /// 不自动恢复, 需手动调用 ClearError()
        /// </summary>
        private void ApplyErrorVisual()
        {
            SetLCDFrame(
                this.Resources["ErrorFrameGradient"] as Brush,
                Color.FromRgb(198, 40, 40),
                Color.FromRgb(229, 57, 53), 20, 0.85);

            SetIndicator(
                Color.FromRgb(244, 67, 54),
                Color.FromRgb(244, 67, 54), 14,
                "异常", Color.FromRgb(211, 47, 47));

            SetAccentBar(new SolidColorBrush(Color.FromRgb(229, 57, 53)));
            SetScreenOverlay(Color.FromRgb(244, 67, 54), 0.10);
        }

        #endregion

        #region 视觉元素设置方法

        private void SetLCDFrame(Brush background, Color borderColor, Color glowColor, double glowRadius, double glowOpacity)
        {
            try
            {
                var frame = this.FindName("LCDFrame") as Border;
                if (frame == null) return;

                if (background != null)
                    frame.Background = background;
                frame.BorderBrush = new SolidColorBrush(borderColor);

                var glow = this.FindName("LCDFrameGlow") as DropShadowEffect;
                if (glow != null)
                {
                    glow.Color = glowColor;
                    glow.BlurRadius = glowRadius;
                    glow.ShadowDepth = 0;
                    glow.Opacity = glowOpacity;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"SetLCDFrame失败 | {ex.Message}");
            }
        }

        private void SetIndicator(Color fillColor, Color glowColor, double glowRadius, string text, Color textColor)
        {
            try
            {
                var indicator = this.FindName("RunningIndicator") as Ellipse;
                if (indicator != null)
                {
                    indicator.Fill = new SolidColorBrush(fillColor);

                    if (glowRadius > 0)
                    {
                        indicator.Effect = new DropShadowEffect
                        {
                            Color = glowColor,
                            BlurRadius = glowRadius,
                            ShadowDepth = 0,
                            Opacity = 0.8,
                            RenderingBias = RenderingBias.Performance
                        };
                    }
                    else
                    {
                        indicator.Effect = null;
                    }
                }

                var statusText = this.FindName("StatusText") as TextBlock;
                if (statusText != null)
                {
                    statusText.Text = text;
                    statusText.Foreground = new SolidColorBrush(textColor);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"SetIndicator失败 | {ex.Message}");
            }
        }

        private void SetAccentBar(Brush brush)
        {
            try
            {
                var bar = this.FindName("AccentBar") as Border;
                if (bar != null && brush != null)
                    bar.Background = brush;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"SetAccentBar失败 | {ex.Message}");
            }
        }

        private void SetScreenOverlay(Color color, double opacity)
        {
            try
            {
                var overlay = this.FindName("ScreenOverlay") as Border;
                if (overlay != null)
                {
                    overlay.Background = new SolidColorBrush(color);
                    overlay.Opacity = opacity;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"SetScreenOverlay失败 | {ex.Message}");
            }
        }

        private void UpdateButtonStates()
        {
            try
            {
                if (CirculationButton != null)
                    CirculationButton.Tag = _isCirculating ? "Active" : null;
                if (HeatingButton != null)
                    HeatingButton.Tag = _isHeating ? "Active" : null;
                if (CoolingButton != null)
                    CoolingButton.Tag = _isCooling ? "Active" : null;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"UpdateButtonStates失败 | {ex.Message}");
            }
        }

        private bool IsAnyModeRunning()
        {
            return _isCirculating || _isHeating || _isCooling;
        }

        /// <summary>
        /// 根据当前运行模式决定应显示的视觉状态
        /// </summary>
        private void RefreshVisualState()
        {
            if (_isError)
                ApplyVisualState(DeviceVisualState.Error);
            else if (_isLoading)
                ApplyVisualState(DeviceVisualState.Loading);
            else if (IsAnyModeRunning())
                ApplyVisualState(DeviceVisualState.Running);
            else
                ApplyVisualState(DeviceVisualState.Idle);
        }

        #endregion

        #region 温度控制方法

        public void SetTargetTemperature(double temperature)
        {
            SetTemperature = Math.Round(temperature, 1);
        }

        public void UpdateCurrentTemperature(double temperature)
        {
            CurrentTemperature = Math.Round(temperature, 1);
        }

        #endregion

        #region 按钮事件处理

        private void OnTempUpClick(object sender, RoutedEventArgs e)
        {
            try
            {
                double newTemp = SetTemperature + 0.5;
                if (newTemp <= 100.0)
                    RequestTemperatureSet(newTemp);
                e.Handled = true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"温度上调失败 | {ex.Message}");
            }
        }

        private void OnTempDownClick(object sender, RoutedEventArgs e)
        {
            try
            {
                double newTemp = SetTemperature - 0.5;
                if (newTemp >= -80.0)
                    RequestTemperatureSet(newTemp);
                e.Handled = true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"温度下调失败 | {ex.Message}");
            }
        }

        private void OnCirculationClick(object sender, RoutedEventArgs e)
        {
            try
            {
                if (CirculationRequested == null)
                {
                    _isCirculating = !_isCirculating;
                    _isError = false;
                    RefreshVisualState();
                    e.Handled = true;
                    return;
                }

                ShowLoading();

                CirculationRequested.Invoke(this, new CirculationRequestEventArgs
                {
                    SystemId = this.SystemId,
                    CurrentState = _isCirculating,
                    RequestedState = !_isCirculating,
                    OnCompleted = (success) =>
                    {
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            HideLoading();
                            if (success)
                            {
                                _isCirculating = !_isCirculating;
                                _isError = false;
                                RefreshVisualState();
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
                Debug.WriteLine($"循环按钮处理失败 | {ex.Message}");
                HideLoading();
            }
        }

        private void OnHeatingClick(object sender, RoutedEventArgs e)
        {
            try
            {
                if (HeatingRequested == null)
                {
                    _isHeating = !_isHeating;
                    _isError = false;
                    RefreshVisualState();
                    e.Handled = true;
                    return;
                }

                ShowLoading();

                HeatingRequested.Invoke(this, new HeatingRequestEventArgs
                {
                    SystemId = this.SystemId,
                    CurrentState = _isHeating,
                    RequestedState = !_isHeating,
                    OnCompleted = (success) =>
                    {
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            HideLoading();
                            if (success)
                            {
                                _isHeating = !_isHeating;
                                _isError = false;
                                RefreshVisualState();
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
                Debug.WriteLine($"加热按钮处理失败 | {ex.Message}");
                HideLoading();
            }
        }

        private void OnCoolingClick(object sender, RoutedEventArgs e)
        {
            try
            {
                if (CoolingRequested == null)
                {
                    _isCooling = !_isCooling;
                    _isError = false;
                    RefreshVisualState();
                    e.Handled = true;
                    return;
                }

                ShowLoading();

                CoolingRequested.Invoke(this, new CoolingRequestEventArgs
                {
                    SystemId = this.SystemId,
                    CurrentState = _isCooling,
                    RequestedState = !_isCooling,
                    OnCompleted = (success) =>
                    {
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            HideLoading();
                            if (success)
                            {
                                _isCooling = !_isCooling;
                                _isError = false;
                                RefreshVisualState();
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
                Debug.WriteLine($"制冷按钮处理失败 | {ex.Message}");
                HideLoading();
            }
        }

        private void RequestTemperatureSet(double temperature)
        {
            if (TemperatureSetRequested == null)
            {
                SetTemperature = Math.Round(temperature, 1);
                return;
            }

            ShowLoading();

            TemperatureSetRequested.Invoke(this, new TemperatureSetRequestEventArgs
            {
                SystemId = this.SystemId,
                CurrentTemperature = SetTemperature,
                RequestedTemperature = temperature,
                OnCompleted = (success) =>
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        HideLoading();
                        if (!success)
                            ShowOperationFailure();
                    }));
                }
            });
        }

        #endregion

        #region 加载和错误状态 (无自动恢复)

        /// <summary>
        /// 显示加载状态 - 琥珀色, 不自动恢复
        /// </summary>
        public void ShowLoading()
        {
            if (_isLoading) return;
            _isLoading = true;
            _isError = false;
            ApplyVisualState(DeviceVisualState.Loading);
        }

        /// <summary>
        /// 隐藏加载状态 - 恢复为运行/空闲态
        /// </summary>
        public void HideLoading()
        {
            if (!_isLoading) return;
            _isLoading = false;
            RefreshVisualState();
        }

        /// <summary>
        /// 显示操作失败 - 红色, 不自动恢复, 需手动调用 ClearError()
        /// </summary>
        public void ShowOperationFailure()
        {
            _isLoading = false;
            _isError = true;
            ApplyVisualState(DeviceVisualState.Error);
        }

        /// <summary>
        /// 手动清除错误状态
        /// </summary>
        public void ClearError()
        {
            _isError = false;
            RefreshVisualState();
            Debug.WriteLine($"错误状态已清除 | SystemId:{SystemId}");
        }

        #endregion

        #region 拖拽控制

        private void OnDraggableAreaMouseDown(object sender, MouseButtonEventArgs e)
        {
            try
            {
                if (e.LeftButton == MouseButtonState.Pressed)
                {
                    SystemSelected?.Invoke(this, this.SystemId);
                    this.IsSelected = true;

                    _isDragging = true;
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

                    var draggableArea = sender as Border;
                    draggableArea?.CaptureMouse();
                    this.Opacity = 0.7;

                    SystemDragStarted?.Invoke(this, new TempSystemDragEventArgs
                    {
                        SystemId = SystemId,
                        StartPosition = _originalPosition
                    });

                    e.Handled = true;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"拖拽开始失败 | {ex.Message}");
            }
        }

        private void OnDraggableAreaMouseMove(object sender, MouseEventArgs e)
        {
            try
            {
                if (_isDragging && e.LeftButton == MouseButtonState.Pressed)
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

                        SystemDragging?.Invoke(this, new TempSystemDragEventArgs
                        {
                            SystemId = SystemId,
                            CurrentPosition = new Point(newLeft, newTop)
                        });
                    }
                    e.Handled = true;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"拖拽移动失败 | {ex.Message}");
            }
        }

        private void OnDraggableAreaMouseUp(object sender, MouseButtonEventArgs e)
        {
            try
            {
                if (_isDragging)
                {
                    CompleteDrag();
                    var draggableArea = sender as Border;
                    draggableArea?.ReleaseMouseCapture();
                    e.Handled = true;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"拖拽结束失败 | {ex.Message}");
            }
        }

        private void OnDraggableAreaMouseLeave(object sender, MouseEventArgs e)
        {
            try
            {
                if (_isDragging)
                {
                    CompleteDrag();
                    var draggableArea = sender as Border;
                    draggableArea?.ReleaseMouseCapture();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"拖拽离开失败 | {ex.Message}");
            }
        }

        private void CompleteDrag()
        {
            try
            {
                if (!_isDragging) return;

                var finalPosition = new Point(Canvas.GetLeft(this), Canvas.GetTop(this));

                SystemDragCompleted?.Invoke(this, new TempSystemDragEventArgs
                {
                    SystemId = SystemId,
                    StartPosition = _originalPosition,
                    CurrentPosition = finalPosition
                });

                this.Opacity = 1.0;
                _isDragging = false;

                RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
                this.CacheMode = new BitmapCache
                {
                    RenderAtScale = 1.0,
                    SnapsToDevicePixels = true,
                    EnableClearType = false
                };
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"完成拖拽失败 | {ex.Message}");
            }
        }

        private void UpdateSelectionVisual(bool isSelected)
        {
            try
            {
                var selectionBorder = this.FindName("SelectionBorder") as Border;
                if (selectionBorder != null)
                    selectionBorder.Visibility = Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"更新选中视觉效果失败 | {ex.Message}");
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
                if (parent == null) return snapPoints;

                var left = Canvas.GetLeft(this);
                var top = Canvas.GetTop(this);
                if (double.IsNaN(left)) left = 0;
                if (double.IsNaN(top)) top = 0;

                snapPoints.Add(new PipeSnapPoint
                {
                    WorldPosition = new Point(left + 180, top + 0),
                    Direction = SnapDirection.Up,
                    Description = "温控系统顶部"
                });

                snapPoints.Add(new PipeSnapPoint
                {
                    WorldPosition = new Point(left + 180, top + 500),
                    Direction = SnapDirection.Down,
                    Description = "温控系统底部"
                });

                snapPoints.Add(new PipeSnapPoint
                {
                    WorldPosition = new Point(left + 0, top + 250),
                    Direction = SnapDirection.Left,
                    Description = "温控系统左侧"
                });

                snapPoints.Add(new PipeSnapPoint
                {
                    WorldPosition = new Point(left + 340, top + 250),
                    Direction = SnapDirection.Right,
                    Description = "温控系统右侧"
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"获取吸附点失败 | {ex.Message}");
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

    #region 事件参数类

    public class TemperatureSetRequestEventArgs : EventArgs
    {
        public string SystemId { get; set; }
        public double CurrentTemperature { get; set; }
        public double RequestedTemperature { get; set; }
        public Action<bool> OnCompleted { get; set; }
    }

    public class CirculationRequestEventArgs : EventArgs
    {
        public string SystemId { get; set; }
        public bool CurrentState { get; set; }
        public bool RequestedState { get; set; }
        public Action<bool> OnCompleted { get; set; }
    }

    public class HeatingRequestEventArgs : EventArgs
    {
        public string SystemId { get; set; }
        public bool CurrentState { get; set; }
        public bool RequestedState { get; set; }
        public Action<bool> OnCompleted { get; set; }
    }

    public class CoolingRequestEventArgs : EventArgs
    {
        public string SystemId { get; set; }
        public bool CurrentState { get; set; }
        public bool RequestedState { get; set; }
        public Action<bool> OnCompleted { get; set; }
    }

    public class TempSystemDragEventArgs : EventArgs
    {
        public string SystemId { get; set; }
        public Point StartPosition { get; set; }
        public Point CurrentPosition { get; set; }
    }

    #endregion
}
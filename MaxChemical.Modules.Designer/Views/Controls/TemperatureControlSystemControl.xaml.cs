using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MaxChemical.Modules.Designer.Service;

namespace MaxChemical.Modules.Designer.Views.Controls
{
    /// <summary>
    /// 高低温循环装置控件
    /// 支持设备驱动与UI双向联动
    /// </summary>
    public partial class TemperatureControlSystemControl : UserControl, INotifyPropertyChanged, IPipeSnapPoints
    {
        #region 事件定义

        public event PropertyChangedEventHandler PropertyChanged;
        public event EventHandler<TempSystemDragEventArgs> SystemDragStarted;
        public event EventHandler<TempSystemDragEventArgs> SystemDragging;
        public event EventHandler<TempSystemDragEventArgs> SystemDragCompleted;
        public event EventHandler<string> SystemSelected;
        public event EventHandler<string> SystemDeselected;

        // UI向设备驱动的请求事件
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

        // 温度控制状态
        private double _setTemperature = -40.0;
        private double _currentTemperature = 24.5;
        private double _outletTemperature = -38.2;
        private bool _isCirculating = false;
        private bool _isHeating = false;
        private bool _isCooling = false;
        private bool _isLoading = false;

        #endregion

        #region 依赖属性

        /// <summary>
        /// 设置温度
        /// </summary>
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
            {
                control.OnPropertyChanged(nameof(SetTemperature));
            }
        }

        /// <summary>
        /// 当前温度
        /// </summary>
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
            {
                control.OnPropertyChanged(nameof(CurrentTemperature));
            }
        }

        /// <summary>
        /// 出口温度
        /// </summary>
        public static readonly DependencyProperty OutletTemperatureProperty =
            DependencyProperty.Register(
                nameof(OutletTemperature),
                typeof(double),
                typeof(TemperatureControlSystemControl),
                new PropertyMetadata(-38.2, OnOutletTemperatureChanged));

        public double OutletTemperature
        {
            get => (double)GetValue(OutletTemperatureProperty);
            set => SetValue(OutletTemperatureProperty, value);
        }

        private static void OnOutletTemperatureChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is TemperatureControlSystemControl control)
            {
                control.OnPropertyChanged(nameof(OutletTemperature));
            }
        }

        /// <summary>
        /// 选中状态
        /// </summary>
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
            {
                control.UpdateSelectionVisual((bool)e.NewValue);
            }
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
                // 硬件加速配置
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

                Debug.WriteLine($"温控系统硬件加速已启用 | SystemId:{SystemId}");
            }

            Debug.WriteLine($"温控系统控件已创建 | SystemId:{SystemId}");
        }

        private void OnSystemControlLoaded(object sender, RoutedEventArgs e)
        {
            try
            {
                Debug.WriteLine($"温控系统控件已加载完成 | SystemId:{SystemId}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"温控系统控件加载失败 | SystemId:{SystemId} | 错误:{ex.Message}");
            }
        }

        #endregion

        #region 温度控制方法

        /// <summary>
        /// 设置目标温度
        /// </summary>
        public void SetTargetTemperature(double temperature)
        {
            try
            {
                SetTemperature = Math.Round(temperature, 1);
                Debug.WriteLine($"目标温度已设置 | SystemId:{SystemId} | 温度:{SetTemperature}°C");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"设置目标温度失败 | SystemId:{SystemId} | 错误:{ex.Message}");
            }
        }

        /// <summary>
        /// 更新当前温度
        /// </summary>
        public void UpdateCurrentTemperature(double temperature)
        {
            try
            {
                CurrentTemperature = Math.Round(temperature, 1);
                Debug.WriteLine($"当前温度已更新 | SystemId:{SystemId} | 温度:{CurrentTemperature}°C");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"更新当前温度失败 | SystemId:{SystemId} | 错误:{ex.Message}");
            }
        }

        /// <summary>
        /// 更新出口温度
        /// </summary>
        public void UpdateOutletTemperature(double temperature)
        {
            try
            {
                OutletTemperature = Math.Round(temperature, 1);
                Debug.WriteLine($"出口温度已更新 | SystemId:{SystemId} | 温度:{OutletTemperature}°C");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"更新出口温度失败 | SystemId:{SystemId} | 错误:{ex.Message}");
            }
        }

        #endregion

        #region 按钮事件处理

        /// <summary>
        /// 温度上调按钮
        /// </summary>
        private void OnTempUpClick(object sender, RoutedEventArgs e)
        {
            try
            {
                double newTemp = SetTemperature + 0.5;
                if (newTemp <= 100.0)
                {
                    RequestTemperatureSet(newTemp);
                }
                e.Handled = true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"温度上调失败 | SystemId:{SystemId} | 错误:{ex.Message}");
            }
        }

        /// <summary>
        /// 温度下调按钮
        /// </summary>
        private void OnTempDownClick(object sender, RoutedEventArgs e)
        {
            try
            {
                double newTemp = SetTemperature - 0.5;
                if (newTemp >= -80.0)
                {
                    RequestTemperatureSet(newTemp);
                }
                e.Handled = true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"温度下调失败 | SystemId:{SystemId} | 错误:{ex.Message}");
            }
        }

        /// <summary>
        /// 循环按钮点击
        /// </summary>
        private void OnCirculationClick(object sender, RoutedEventArgs e)
        {
            try
            {
                Debug.WriteLine($"循环按钮点击 | SystemId:{SystemId}");
                ShowLoading();

                CirculationRequested?.Invoke(this, new CirculationRequestEventArgs
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
                                Debug.WriteLine($"循环状态切换成功 | SystemId:{SystemId} | 状态:{_isCirculating}");
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
                Debug.WriteLine($"循环按钮处理失败 | SystemId:{SystemId} | 错误:{ex.Message}");
                HideLoading();
            }
        }

        /// <summary>
        /// 加热按钮点击
        /// </summary>
        private void OnHeatingClick(object sender, RoutedEventArgs e)
        {
            try
            {
                Debug.WriteLine($"加热按钮点击 | SystemId:{SystemId}");
                ShowLoading();

                HeatingRequested?.Invoke(this, new HeatingRequestEventArgs
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
                                Debug.WriteLine($"加热状态切换成功 | SystemId:{SystemId} | 状态:{_isHeating}");
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
                Debug.WriteLine($"加热按钮处理失败 | SystemId:{SystemId} | 错误:{ex.Message}");
                HideLoading();
            }
        }

        /// <summary>
        /// 制冷按钮点击
        /// </summary>
        private void OnCoolingClick(object sender, RoutedEventArgs e)
        {
            try
            {
                Debug.WriteLine($"制冷按钮点击 | SystemId:{SystemId}");
                ShowLoading();

                CoolingRequested?.Invoke(this, new CoolingRequestEventArgs
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
                                Debug.WriteLine($"制冷状态切换成功 | SystemId:{SystemId} | 状态:{_isCooling}");
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
                Debug.WriteLine($"制冷按钮处理失败 | SystemId:{SystemId} | 错误:{ex.Message}");
                HideLoading();
            }
        }

        /// <summary>
        /// 请求温度设置
        /// </summary>
        private void RequestTemperatureSet(double temperature)
        {
            ShowLoading();

            TemperatureSetRequested?.Invoke(this, new TemperatureSetRequestEventArgs
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
                        {
                            ShowOperationFailure();
                        }
                    }));
                }
            });
        }

        #endregion

        #region 加载和错误状态

        /// <summary>
        /// 显示加载状态
        /// </summary>
        public void ShowLoading()
        {
            if (_isLoading) return;
            _isLoading = true;

            try
            {
                var loadingOverlay = this.FindName("LoadingOverlay") as Border;
                if (loadingOverlay != null)
                {
                    loadingOverlay.Visibility = Visibility.Visible;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"显示加载状态失败 | SystemId:{SystemId} | 错误:{ex.Message}");
            }
        }

        /// <summary>
        /// 隐藏加载状态
        /// </summary>
        public void HideLoading()
        {
            if (!_isLoading) return;
            _isLoading = false;

            try
            {
                var loadingOverlay = this.FindName("LoadingOverlay") as Border;
                if (loadingOverlay != null)
                {
                    loadingOverlay.Visibility = Visibility.Collapsed;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"隐藏加载状态失败 | SystemId:{SystemId} | 错误:{ex.Message}");
            }
        }

        /// <summary>
        /// 显示操作失败
        /// </summary>
        public void ShowOperationFailure()
        {
            try
            {
                var setTempValue = this.FindName("SetTempValue") as TextBlock;
                if (setTempValue != null)
                {
                    var originalBrush = setTempValue.Foreground;
                    setTempValue.Foreground = new SolidColorBrush(Color.FromRgb(255, 59, 48));

                    var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                    timer.Tick += (s, e) =>
                    {
                        setTempValue.Foreground = originalBrush;
                        timer.Stop();
                    };
                    timer.Start();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"显示操作失败失败 | SystemId:{SystemId} | 错误:{ex.Message}");
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
                Debug.WriteLine($"拖拽开始失败 | SystemId:{SystemId} | 错误:{ex.Message}");
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
                Debug.WriteLine($"拖拽移动失败 | SystemId:{SystemId} | 错误:{ex.Message}");
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
                Debug.WriteLine($"拖拽结束失败 | SystemId:{SystemId} | 错误:{ex.Message}");
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
                Debug.WriteLine($"拖拽离开失败 | SystemId:{SystemId} | 错误:{ex.Message}");
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
                Debug.WriteLine($"完成拖拽失败 | SystemId:{SystemId} | 错误:{ex.Message}");
            }
        }

        private void UpdateSelectionVisual(bool isSelected)
        {
            try
            {
                var selectionBorder = this.FindName("SelectionBorder") as Border;
                if (selectionBorder != null)
                {
                    selectionBorder.Visibility = isSelected ? Visibility.Visible : Visibility.Collapsed;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"更新选中视觉失败 | SystemId:{SystemId} | 错误:{ex.Message}");
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
                Debug.WriteLine($"获取吸附点失败 | SystemId:{SystemId} | 错误:{ex.Message}");
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

    /// <summary>
    /// 温度设置请求事件参数
    /// </summary>
    public class TemperatureSetRequestEventArgs : EventArgs
    {
        public string SystemId { get; set; }
        public double CurrentTemperature { get; set; }
        public double RequestedTemperature { get; set; }
        public Action<bool> OnCompleted { get; set; }
    }

    /// <summary>
    /// 循环请求事件参数
    /// </summary>
    public class CirculationRequestEventArgs : EventArgs
    {
        public string SystemId { get; set; }
        public bool CurrentState { get; set; }
        public bool RequestedState { get; set; }
        public Action<bool> OnCompleted { get; set; }
    }

    /// <summary>
    /// 加热请求事件参数
    /// </summary>
    public class HeatingRequestEventArgs : EventArgs
    {
        public string SystemId { get; set; }
        public bool CurrentState { get; set; }
        public bool RequestedState { get; set; }
        public Action<bool> OnCompleted { get; set; }
    }

    /// <summary>
    /// 制冷请求事件参数
    /// </summary>
    public class CoolingRequestEventArgs : EventArgs
    {
        public string SystemId { get; set; }
        public bool CurrentState { get; set; }
        public bool RequestedState { get; set; }
        public Action<bool> OnCompleted { get; set; }
    }

    /// <summary>
    /// 温控系统拖拽事件参数
    /// </summary>
    public class TempSystemDragEventArgs : EventArgs
    {
        public string SystemId { get; set; }
        public Point StartPosition { get; set; }
        public Point CurrentPosition { get; set; }
    }

    #endregion
}
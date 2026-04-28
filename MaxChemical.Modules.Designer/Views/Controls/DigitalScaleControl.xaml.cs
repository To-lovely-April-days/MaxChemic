using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using MaxChemical.Modules.Designer.Service;

namespace MaxChemical.Modules.Designer.Views.Controls
{
    /// <summary>
    /// DigitalScaleControl.xaml 的交互逻辑
    /// 支持设备驱动与UI双向联动
    /// 无动画版本以提升性能
    /// </summary>
    public partial class DigitalScaleControl : UserControl, INotifyPropertyChanged, IPipeSnapPoints
    {
        #region 事件定义

        public event PropertyChangedEventHandler PropertyChanged;
        public event EventHandler<ScaleDragEventArgs> ScaleDragStarted;
        public event EventHandler<ScaleDragEventArgs> ScaleDragging;
        public event EventHandler<ScaleDragEventArgs> ScaleDragCompleted;
        public event EventHandler<string> ScaleSelected;
        public event EventHandler<string> ScaleDeselected;
        public event EventHandler<ScaleStateChangedEventArgs> ScaleStateChanged;

        // UI向设备驱动的请求事件
        public event EventHandler<PowerStateChangeRequestEventArgs> PowerStateChangeRequested;
        public event EventHandler<TareRequestEventArgs> TareRequested;
        public event EventHandler<UnitChangeRequestEventArgs> UnitChangeRequested;

        #endregion

        #region 字段

        public string ScaleId { get; set; }

        private bool _isDraggingScale = false;
        private Point _dragStartPoint;
        private Point _originalPosition;

        // 电子秤状态
        private bool _isPoweredOn = false;
        private double _weightValue = 0.0;
        private string _currentUnit = "g";
        private bool _isTared = false;
        private bool _isLoading = false;

        // 支持的单位列表
        private readonly string[] _supportedUnits = { "g", "kg", "lb", "oz" };
        private int _currentUnitIndex = 0;

        #endregion

        #region 依赖属性

        /// <summary>
        /// 电源状态
        /// </summary>
        public static readonly DependencyProperty IsPoweredOnProperty =
            DependencyProperty.Register(
                nameof(IsPoweredOn),
                typeof(bool),
                typeof(DigitalScaleControl),
                new PropertyMetadata(false, OnIsPoweredOnChanged));

        public bool IsPoweredOn
        {
            get => (bool)GetValue(IsPoweredOnProperty);
            set => SetValue(IsPoweredOnProperty, value);
        }

        private static void OnIsPoweredOnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is DigitalScaleControl control)
            {
                control.UpdatePowerState();
                control.OnPropertyChanged(nameof(IsPoweredOn));
            }
        }

        /// <summary>
        /// 重量值
        /// </summary>
        public static readonly DependencyProperty WeightValueProperty =
            DependencyProperty.Register(
                nameof(WeightValue),
                typeof(double),
                typeof(DigitalScaleControl),
                new PropertyMetadata(0.0, OnWeightValueChanged));

        public double WeightValue
        {
            get => (double)GetValue(WeightValueProperty);
            set => SetValue(WeightValueProperty, value);
        }

        private static void OnWeightValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is DigitalScaleControl control)
            {
                control.UpdateWeightDisplay();
                control.OnPropertyChanged(nameof(WeightValue));
            }
        }

        /// <summary>
        /// 当前单位
        /// </summary>
        public static readonly DependencyProperty CurrentUnitProperty =
            DependencyProperty.Register(
                nameof(CurrentUnit),
                typeof(string),
                typeof(DigitalScaleControl),
                new PropertyMetadata("g", OnCurrentUnitChanged));

        public string CurrentUnit
        {
            get => (string)GetValue(CurrentUnitProperty);
            set => SetValue(CurrentUnitProperty, value);
        }

        private static void OnCurrentUnitChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is DigitalScaleControl control)
            {
                control.OnPropertyChanged(nameof(CurrentUnit));
            }
        }

        /// <summary>
        /// 是否已去皮
        /// </summary>
        public static readonly DependencyProperty IsTaredProperty =
            DependencyProperty.Register(
                nameof(IsTared),
                typeof(bool),
                typeof(DigitalScaleControl),
                new PropertyMetadata(false, OnIsTaredChanged));

        public bool IsTared
        {
            get => (bool)GetValue(IsTaredProperty);
            set => SetValue(IsTaredProperty, value);
        }

        private static void OnIsTaredChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is DigitalScaleControl control)
            {
                control.OnPropertyChanged(nameof(IsTared));
            }
        }

        /// <summary>
        /// 选中状态
        /// </summary>
        public static readonly DependencyProperty IsSelectedProperty =
            DependencyProperty.Register(
                nameof(IsSelected),
                typeof(bool),
                typeof(DigitalScaleControl),
                new PropertyMetadata(false, OnIsSelectedChanged));

        public bool IsSelected
        {
            get => (bool)GetValue(IsSelectedProperty);
            set => SetValue(IsSelectedProperty, value);
        }

        private static void OnIsSelectedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is DigitalScaleControl control)
            {
                control.UpdateSelectionVisual((bool)e.NewValue);
            }
        }

        #endregion

        #region 构造函数

        public DigitalScaleControl()
        {
            InitializeComponent();
            ScaleId = Guid.NewGuid().ToString();
            this.DataContext = this;

            bool isInDesignMode = DesignerProperties.GetIsInDesignMode(this);

            this.Loaded += OnScaleControlLoaded;

            if (!isInDesignMode)
            {
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

                Debug.WriteLine($"电子秤硬件加速已启用 | ScaleId:{ScaleId}");
            }

            Debug.WriteLine($"电子秤控件已创建 | ScaleId:{ScaleId}");
        }

        private void OnScaleControlLoaded(object sender, RoutedEventArgs e)
        {
            try
            {
                UpdatePowerState();
                UpdateWeightDisplay();

                Debug.WriteLine($"电子秤控件已加载完成 | ScaleId:{ScaleId}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"电子秤控件加载失败 | ScaleId:{ScaleId} | 错误:{ex.Message}");
            }
        }

        #endregion

        #region 电子秤控制方法

        /// <summary>
        /// 开机
        /// </summary>
        public void PowerOn()
        {
            try
            {
                if (!IsPoweredOn)
                {
                    IsPoweredOn = true;
                    Debug.WriteLine($"电子秤已开机 | ScaleId:{ScaleId}");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"电子秤开机失败 | ScaleId:{ScaleId} | 错误:{ex.Message}");
            }
        }

        /// <summary>
        /// 关机
        /// </summary>
        public void PowerOff()
        {
            try
            {
                if (IsPoweredOn)
                {
                    IsPoweredOn = false;
                    Debug.WriteLine($"电子秤已关机 | ScaleId:{ScaleId}");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"电子秤关机失败 | ScaleId:{ScaleId} | 错误:{ex.Message}");
            }
        }

        /// <summary>
        /// 设置重量值
        /// </summary>
        public void SetWeight(double weight)
        {
            try
            {
                WeightValue = weight;
                Debug.WriteLine($"电子秤重量已设置 | ScaleId:{ScaleId} | 重量:{WeightValue:F2}{CurrentUnit}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"设置电子秤重量失败 | ScaleId:{ScaleId} | 错误:{ex.Message}");
            }
        }

        /// <summary>
        /// 去皮
        /// </summary>
        public void Tare()
        {
            try
            {
                IsTared = true;
                WeightValue = 0.0;
                Debug.WriteLine($"电子秤已去皮 | ScaleId:{ScaleId}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"电子秤去皮失败 | ScaleId:{ScaleId} | 错误:{ex.Message}");
            }
        }

        /// <summary>
        /// 切换单位
        /// </summary>
        public void SwitchUnit()
        {
            try
            {
                _currentUnitIndex = (_currentUnitIndex + 1) % _supportedUnits.Length;
                CurrentUnit = _supportedUnits[_currentUnitIndex];
                Debug.WriteLine($"电子秤单位已切换 | ScaleId:{ScaleId} | 单位:{CurrentUnit}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"电子秤切换单位失败 | ScaleId:{ScaleId} | 错误:{ex.Message}");
            }
        }

        #endregion

        #region UI状态更新方法

        /// <summary>
        /// 更新电源状态
        /// </summary>
        private void UpdatePowerState()
        {
            try
            {
                Debug.WriteLine($"电源状态已更新 | ScaleId:{ScaleId} | 状态:{(IsPoweredOn ? "开机" : "关机")}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"更新电源状态失败 | ScaleId:{ScaleId} | 错误:{ex.Message}");
            }
        }

        /// <summary>
        /// 更新重量显示
        /// </summary>
        private void UpdateWeightDisplay()
        {
            try
            {
                Debug.WriteLine($"重量显示已更新 | ScaleId:{ScaleId} | 重量:{WeightValue:F2}{CurrentUnit}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"更新重量显示失败 | ScaleId:{ScaleId} | 错误:{ex.Message}");
            }
        }

        #endregion

        #region 按钮事件处理

        /// <summary>
        /// 电源按钮点击事件
        /// </summary>
        private void OnPowerButtonClick(object sender, RoutedEventArgs e)
        {
            try
            {
                Debug.WriteLine($"电源按钮点击 | ScaleId:{ScaleId} | 当前状态:{IsPoweredOn}");

                ShowLoading();

                PowerStateChangeRequested?.Invoke(this, new PowerStateChangeRequestEventArgs
                {
                    ScaleId = this.ScaleId,
                    CurrentState = IsPoweredOn,
                    RequestedState = !IsPoweredOn,
                    OnCompleted = (success) =>
                    {
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            HideLoading();
                            if (!success)
                            {
                                ShowOperationFailure();
                                Debug.WriteLine($"电源状态切换失败 | ScaleId:{ScaleId}");
                            }
                            else
                            {
                                Debug.WriteLine($"电源状态切换成功 | ScaleId:{ScaleId} | 新状态:{!IsPoweredOn}");
                            }
                        }));
                    }
                });

                e.Handled = true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"电源按钮点击处理失败 | ScaleId:{ScaleId} | 错误:{ex.Message}");
                HideLoading();
                ShowOperationFailure();
            }
        }

        /// <summary>
        /// 去皮按钮点击事件
        /// </summary>
        private void OnTareButtonClick(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!IsPoweredOn)
                {
                    Debug.WriteLine($"去皮失败：电子秤未开机 | ScaleId:{ScaleId}");
                    return;
                }

                Debug.WriteLine($"去皮按钮点击 | ScaleId:{ScaleId}");

                ShowLoading();

                TareRequested?.Invoke(this, new TareRequestEventArgs
                {
                    ScaleId = this.ScaleId,
                    CurrentWeight = WeightValue,
                    OnCompleted = (success) =>
                    {
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            HideLoading();
                            if (!success)
                            {
                                ShowOperationFailure();
                                Debug.WriteLine($"去皮操作失败 | ScaleId:{ScaleId}");
                            }
                            else
                            {
                                Debug.WriteLine($"去皮操作成功 | ScaleId:{ScaleId}");
                            }
                        }));
                    }
                });

                e.Handled = true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"去皮按钮点击处理失败 | ScaleId:{ScaleId} | 错误:{ex.Message}");
                HideLoading();
                ShowOperationFailure();
            }
        }

        /// <summary>
        /// 单位按钮点击事件
        /// </summary>
        private void OnUnitButtonClick(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!IsPoweredOn)
                {
                    Debug.WriteLine($"切换单位失败：电子秤未开机 | ScaleId:{ScaleId}");
                    return;
                }

                int nextUnitIndex = (_currentUnitIndex + 1) % _supportedUnits.Length;
                string nextUnit = _supportedUnits[nextUnitIndex];

                Debug.WriteLine($"单位按钮点击 | ScaleId:{ScaleId} | 当前单位:{CurrentUnit} | 目标单位:{nextUnit}");

                ShowLoading();

                UnitChangeRequested?.Invoke(this, new UnitChangeRequestEventArgs
                {
                    ScaleId = this.ScaleId,
                    CurrentUnit = CurrentUnit,
                    RequestedUnit = nextUnit,
                    OnCompleted = (success) =>
                    {
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            HideLoading();
                            if (!success)
                            {
                                ShowOperationFailure();
                                Debug.WriteLine($"单位切换失败 | ScaleId:{ScaleId}");
                            }
                            else
                            {
                                Debug.WriteLine($"单位切换成功 | ScaleId:{ScaleId} | 新单位:{nextUnit}");
                            }
                        }));
                    }
                });

                e.Handled = true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"单位按钮点击处理失败 | ScaleId:{ScaleId} | 错误:{ex.Message}");
                HideLoading();
                ShowOperationFailure();
            }
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
            Debug.WriteLine($"显示加载状态 | ScaleId:{ScaleId}");

            try
            {
                ClearError();

                var loadingOverlay = this.FindName("LoadingOverlay") as Border;
                if (loadingOverlay != null)
                {
                    loadingOverlay.Visibility = Visibility.Visible;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"显示加载状态失败 | ScaleId:{ScaleId} | 错误:{ex.Message}");
            }
        }

        /// <summary>
        /// 隐藏加载状态
        /// </summary>
        public void HideLoading()
        {
            if (!_isLoading) return;
            _isLoading = false;
            Debug.WriteLine($"隐藏加载状态 | ScaleId:{ScaleId}");

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
                Debug.WriteLine($"隐藏加载状态失败 | ScaleId:{ScaleId} | 错误:{ex.Message}");
            }
        }

        /// <summary>
        /// 显示操作失败状态
        /// </summary>
        public void ShowOperationFailure()
        {
            Debug.WriteLine($"显示操作失败状态 | ScaleId:{ScaleId}");

            try
            {
                HideLoading();

                var valueText = this.FindName("ValueText") as TextBlock;
                if (valueText != null)
                {
                    var originalText = valueText.Text;
                    valueText.Text = "Err";
                    valueText.Foreground = new SolidColorBrush(Color.FromRgb(255, 59, 48));

                    var timer = new DispatcherTimer
                    {
                        Interval = TimeSpan.FromSeconds(2)
                    };
                    timer.Tick += (s, e) =>
                    {
                        valueText.Text = originalText;
                        valueText.Foreground = this.Resources["LcdTextBrush"] as SolidColorBrush;
                        timer.Stop();
                    };
                    timer.Start();
                }

                var scaleBody = this.FindName("ScaleBody") as Rectangle;
                if (scaleBody != null)
                {
                    var originalBrush = scaleBody.Fill;
                    scaleBody.Fill = new SolidColorBrush(Color.FromArgb(50, 255, 59, 48));

                    var timer = new DispatcherTimer
                    {
                        Interval = TimeSpan.FromSeconds(2)
                    };
                    timer.Tick += (s, e) =>
                    {
                        scaleBody.Fill = originalBrush;
                        timer.Stop();
                    };
                    timer.Start();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"显示操作失败状态失败 | ScaleId:{ScaleId} | 错误:{ex.Message}");
            }
        }

        /// <summary>
        /// 清除错误状态
        /// </summary>
        public void ClearError()
        {
            Debug.WriteLine($"清除错误状态 | ScaleId:{ScaleId}");

            try
            {
                var valueText = this.FindName("ValueText") as TextBlock;
                if (valueText != null)
                {
                    valueText.Foreground = this.Resources["LcdTextBrush"] as SolidColorBrush;
                }

                var scaleBody = this.FindName("ScaleBody") as Rectangle;
                if (scaleBody != null)
                {
                    scaleBody.Fill = this.Resources["BodyWhiteGradient"] as LinearGradientBrush;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"清除错误状态失败 | ScaleId:{ScaleId} | 错误:{ex.Message}");
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
                    ScaleSelected?.Invoke(this, this.ScaleId);
                    this.IsSelected = true;

                    _isDraggingScale = true;
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

                    ScaleDragStarted?.Invoke(this, new ScaleDragEventArgs
                    {
                        ScaleId = ScaleId,
                        StartPosition = _originalPosition
                    });

                    e.Handled = true;
                    Debug.WriteLine($"开始拖拽电子秤 | ScaleId:{ScaleId}");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"鼠标按下事件处理失败 | ScaleId:{ScaleId} | 错误:{ex.Message}");
            }
        }

        private void OnDraggableAreaMouseMove(object sender, MouseEventArgs e)
        {
            try
            {
                if (_isDraggingScale && e.LeftButton == MouseButtonState.Pressed)
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

                        ScaleDragging?.Invoke(this, new ScaleDragEventArgs
                        {
                            ScaleId = ScaleId,
                            CurrentPosition = new Point(newLeft, newTop)
                        });
                    }
                    e.Handled = true;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"鼠标移动事件处理失败 | ScaleId:{ScaleId} | 错误:{ex.Message}");
            }
        }

        private void OnDraggableAreaMouseUp(object sender, MouseButtonEventArgs e)
        {
            try
            {
                if (_isDraggingScale)
                {
                    CompleteScaleDrag();

                    var draggableArea = sender as Rectangle;
                    draggableArea?.ReleaseMouseCapture();

                    e.Handled = true;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"鼠标释放事件处理失败 | ScaleId:{ScaleId} | 错误:{ex.Message}");
            }
        }

        private void OnDraggableAreaMouseLeave(object sender, MouseEventArgs e)
        {
            try
            {
                if (_isDraggingScale)
                {
                    CompleteScaleDrag();

                    var draggableArea = sender as Rectangle;
                    draggableArea?.ReleaseMouseCapture();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"鼠标离开事件处理失败 | ScaleId:{ScaleId} | 错误:{ex.Message}");
            }
        }

        private void CompleteScaleDrag()
        {
            try
            {
                if (!_isDraggingScale) return;

                var finalPosition = new Point(Canvas.GetLeft(this), Canvas.GetTop(this));

                ScaleDragCompleted?.Invoke(this, new ScaleDragEventArgs
                {
                    ScaleId = ScaleId,
                    StartPosition = _originalPosition,
                    CurrentPosition = finalPosition
                });

                this.Opacity = 1.0;
                _isDraggingScale = false;

                RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
                this.CacheMode = new BitmapCache
                {
                    RenderAtScale = 1.0,
                    SnapsToDevicePixels = true,
                    EnableClearType = false
                };

                Debug.WriteLine($"电子秤拖拽完成 | ScaleId:{ScaleId} | 位置:({finalPosition.X:F0}, {finalPosition.Y:F0})");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"完成拖拽失败 | ScaleId:{ScaleId} | 错误:{ex.Message}");
            }
        }

        private void UpdateSelectionVisual(bool isSelected)
        {
            // ★ 旧的 SelectionBorder 由统一系统接管,这里不再处理视觉
            var selectionBorder = this.FindName("SelectionBorder") as Rectangle;
            if (selectionBorder != null)
                selectionBorder.Visibility = Visibility.Collapsed;
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
                    Debug.WriteLine("电子秤未添加到Canvas，无法获取吸附点");
                    return snapPoints;
                }

                var scaleLeft = Canvas.GetLeft(this);
                var scaleTop = Canvas.GetTop(this);
                if (double.IsNaN(scaleLeft)) scaleLeft = 0;
                if (double.IsNaN(scaleTop)) scaleTop = 0;

                snapPoints.Add(new PipeSnapPoint
                {
                    WorldPosition = new Point(scaleLeft + 200, scaleTop + 0),
                    Direction = SnapDirection.Up,
                    Description = $"电子秤顶部"
                });

                snapPoints.Add(new PipeSnapPoint
                {
                    WorldPosition = new Point(scaleLeft + 200, scaleTop + 400),
                    Direction = SnapDirection.Down,
                    Description = $"电子秤底部"
                });

                snapPoints.Add(new PipeSnapPoint
                {
                    WorldPosition = new Point(scaleLeft + 0, scaleTop + 200),
                    Direction = SnapDirection.Left,
                    Description = $"电子秤左侧"
                });

                snapPoints.Add(new PipeSnapPoint
                {
                    WorldPosition = new Point(scaleLeft + 400, scaleTop + 200),
                    Direction = SnapDirection.Right,
                    Description = $"电子秤右侧"
                });

                Debug.WriteLine($"电子秤提供吸附点 | ScaleId:{ScaleId} | 数量:{snapPoints.Count}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"获取吸附点失败 | ScaleId:{ScaleId} | 错误:{ex.Message}");
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
    /// 电源状态切换请求事件参数
    /// </summary>
    public class PowerStateChangeRequestEventArgs : EventArgs
    {
        public string ScaleId { get; set; }
        public bool CurrentState { get; set; }
        public bool RequestedState { get; set; }
        public Action<bool> OnCompleted { get; set; }
    }

    /// <summary>
    /// 去皮请求事件参数
    /// </summary>
    public class TareRequestEventArgs : EventArgs
    {
        public string ScaleId { get; set; }
        public double CurrentWeight { get; set; }
        public Action<bool> OnCompleted { get; set; }
    }

    /// <summary>
    /// 单位切换请求事件参数
    /// </summary>
    public class UnitChangeRequestEventArgs : EventArgs
    {
        public string ScaleId { get; set; }
        public string CurrentUnit { get; set; }
        public string RequestedUnit { get; set; }
        public Action<bool> OnCompleted { get; set; }
    }

    /// <summary>
    /// 电子秤拖拽事件参数
    /// </summary>
    public class ScaleDragEventArgs : EventArgs
    {
        public string ScaleId { get; set; }
        public Point StartPosition { get; set; }
        public Point CurrentPosition { get; set; }
    }

    /// <summary>
    /// 电子秤状态变化事件参数
    /// </summary>
    public class ScaleStateChangedEventArgs : EventArgs
    {
        public string ScaleId { get; set; }
        public bool IsPoweredOn { get; set; }
        public double WeightValue { get; set; }
        public string CurrentUnit { get; set; }
        public bool IsTared { get; set; }
    }

    #endregion
}
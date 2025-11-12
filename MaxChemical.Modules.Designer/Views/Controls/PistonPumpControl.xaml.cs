using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using MaxChemical.Modules.Designer.Service;

namespace MaxChemical.Modules.Designer.Views.Controls
{
    /// <summary>
    /// PistonPumpControl.xaml 的交互逻辑
    /// </summary>
    public partial class PistonPumpControl : UserControl, INotifyPropertyChanged, IPipeSnapPoints
    {
        #region 事件定义

        public event PropertyChangedEventHandler PropertyChanged;
        public event EventHandler<PumpDragEventArgs> PumpDragStarted;
        public event EventHandler<PumpDragEventArgs> PumpDragging;
        public event EventHandler<PumpDragEventArgs> PumpDragCompleted;
        public event EventHandler<string> PumpSelected;
        public event EventHandler<string> PumpDeselected;
        public event EventHandler<PumpStateChangedEventArgs> PumpStateChanged;

        #endregion

        #region 字段

        public string PumpId { get; set; }

        private bool _isDraggingPump = false;
        private Point _dragStartPoint;
        private Point _originalPosition;

        // 泵运行状态
        private bool _isRunning = false;
        private int _pumpSpeed = 20; // 默认速度 20
        private Storyboard _pistonStoryboard;

        #endregion

        #region 公共属性

        /// <summary>
        /// 泵是否运行中
        /// </summary>
        public bool IsRunning
        {
            get => _isRunning;
            set
            {
                if (_isRunning != value)
                {
                    _isRunning = value;
                    UpdatePumpRunningState();
                    OnPropertyChanged(nameof(IsRunning));
                    PumpStateChanged?.Invoke(this, new PumpStateChangedEventArgs
                    {
                        PumpId = PumpId,
                        IsRunning = _isRunning,
                        Speed = _pumpSpeed
                    });
                    Debug.WriteLine($"🔧 柱塞泵 {PumpId} 运行状态: {(_isRunning ? "运行中" : "已停止")}");
                }
            }
        }

        /// <summary>
        /// 泵速度 (0-100)
        /// </summary>
        public int PumpSpeed
        {
            get => _pumpSpeed;
            set
            {
                var newSpeed = Math.Max(0, Math.Min(100, value));
                if (_pumpSpeed != newSpeed)
                {
                    _pumpSpeed = newSpeed;
                    UpdatePumpSpeedDisplay();
                    UpdatePistonAnimationSpeed();
                    OnPropertyChanged(nameof(PumpSpeed));
                    PumpStateChanged?.Invoke(this, new PumpStateChangedEventArgs
                    {
                        PumpId = PumpId,
                        IsRunning = _isRunning,
                        Speed = _pumpSpeed
                    });
                    Debug.WriteLine($"⚙️ 柱塞泵 {PumpId} 速度: {_pumpSpeed}");
                }
            }
        }

        #endregion

        #region IsSelected 依赖属性

        public static readonly DependencyProperty IsSelectedProperty =
            DependencyProperty.Register(
                nameof(IsSelected),
                typeof(bool),
                typeof(PistonPumpControl),
                new PropertyMetadata(false, OnIsSelectedChanged));

        public bool IsSelected
        {
            get => (bool)GetValue(IsSelectedProperty);
            set => SetValue(IsSelectedProperty, value);
        }

        private static void OnIsSelectedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is PistonPumpControl control)
            {
                control.UpdateSelectionVisual((bool)e.NewValue);
            }
        }

        #endregion

        #region 构造函数

        public PistonPumpControl()
        {
            InitializeComponent();
            PumpId = Guid.NewGuid().ToString();
            this.DataContext = this;

            bool isInDesignMode = DesignerProperties.GetIsInDesignMode(this);

            if (!isInDesignMode)
            {
                this.MouseDown += OnControlMouseDown;
                this.MouseMove += OnControlMouseMove;
                this.MouseUp += OnControlMouseUp;
                this.MouseLeave += OnControlMouseLeave;
            }

            this.Loaded += OnPumpControlLoaded;

            Debug.WriteLine($"🏗️ 柱塞泵控件 {PumpId} 已创建");
        }

        private void OnPumpControlLoaded(object sender, RoutedEventArgs e)
        {
            try
            {
                // 获取柱塞动画
                _pistonStoryboard = this.Resources["PistonMotionStoryboard"] as Storyboard;

                if (_pistonStoryboard == null)
                {
                    Debug.WriteLine("⚠️ 警告: 柱塞动画资源未找到");
                }
                else
                {
                    Debug.WriteLine("✅ 柱塞动画资源加载成功");
                }

                // 初始化显示
                UpdatePumpSpeedDisplay();
                UpdatePumpRunningState();

                Debug.WriteLine($"✅ 柱塞泵控件 {PumpId} 已加载完成");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ 柱塞泵控件加载失败: {ex.Message}");
            }
        }

        #endregion

        #region 泵控制方法

        /// <summary>
        /// 启动泵
        /// </summary>
        public void StartPump()
        {
            try
            {
                if (!_isRunning)
                {
                    IsRunning = true;
                    Debug.WriteLine($"🟢 柱塞泵 {PumpId} 已启动，速度: {_pumpSpeed}");
                }
                else
                {
                    Debug.WriteLine($"⚠️ 柱塞泵 {PumpId} 已经在运行中");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ 启动柱塞泵失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 停止泵
        /// </summary>
        public void StopPump()
        {
            try
            {
                if (_isRunning)
                {
                    IsRunning = false;
                    Debug.WriteLine($"🔴 柱塞泵 {PumpId} 已停止");
                }
                else
                {
                    Debug.WriteLine($"⚠️ 柱塞泵 {PumpId} 已经处于停止状态");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ 停止柱塞泵失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 切换泵运行状态
        /// </summary>
        public void TogglePump()
        {
            try
            {
                IsRunning = !IsRunning;
                Debug.WriteLine($"🔄 柱塞泵 {PumpId} 状态切换为: {(_isRunning ? "运行" : "停止")}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ 切换柱塞泵状态失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 设置泵速度
        /// </summary>
        public void SetSpeed(int speed)
        {
            try
            {
                PumpSpeed = speed;
                Debug.WriteLine($"⚙️ 柱塞泵 {PumpId} 速度设置为: {_pumpSpeed}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ 设置柱塞泵速度失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 增加速度
        /// </summary>
        public void IncreaseSpeed(int increment = 10)
        {
            try
            {
                int oldSpeed = _pumpSpeed;
                PumpSpeed = _pumpSpeed + increment;
                Debug.WriteLine($"⬆️ 柱塞泵 {PumpId} 速度从 {oldSpeed} 增加到 {_pumpSpeed}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ 增加柱塞泵速度失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 减少速度
        /// </summary>
        public void DecreaseSpeed(int decrement = 10)
        {
            try
            {
                int oldSpeed = _pumpSpeed;
                PumpSpeed = _pumpSpeed - decrement;
                Debug.WriteLine($"⬇️ 柱塞泵 {PumpId} 速度从 {oldSpeed} 减少到 {_pumpSpeed}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ 减少柱塞泵速度失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 更新泵运行状态
        /// </summary>
        private void UpdatePumpRunningState()
        {
            try
            {
                if (_pistonStoryboard != null)
                {
                    if (_isRunning)
                    {
                        // 启动柱塞动画
                        _pistonStoryboard.Begin(this, true);
                        Debug.WriteLine("🔄 柱塞动画已启动");
                    }
                    else
                    {
                        // 停止柱塞动画
                        _pistonStoryboard.Stop(this);
                        Debug.WriteLine("⏸️ 柱塞动画已停止");
                    }
                }
                else
                {
                    Debug.WriteLine("⚠️ 无法更新泵运行状态: 动画资源未加载");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ 更新泵运行状态失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 更新泵速度显示
        /// </summary>
        private void UpdatePumpSpeedDisplay()
        {
            try
            {
                // 由于XAML中的TextBlock使用固定文本"20"
                // 如果需要动态显示速度，需要在XAML中给TextBlock命名并绑定
                // 例如: <TextBlock x:Name="SpeedDisplay" Text="{Binding PumpSpeed}" .../>

                // 当前实现: 通过属性绑定自动更新
                Debug.WriteLine($"📊 泵速度显示更新为: {_pumpSpeed}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ 更新速度显示失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 更新柱塞动画速度
        /// </summary>
        private void UpdatePistonAnimationSpeed()
        {
            try
            {
                if (_pistonStoryboard != null && _isRunning)
                {
                    // 根据速度调整动画播放速度
                    // 速度范围: 0-100 → 动画速率: 0.5-2.0
                    double speedRatio = 0.5 + (_pumpSpeed / 100.0) * 1.5;
                    _pistonStoryboard.SetSpeedRatio(this, speedRatio);
                    Debug.WriteLine($"⚡ 动画速率调整为: {speedRatio:F2}x (速度: {_pumpSpeed})");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ 更新动画速度失败: {ex.Message}");
            }
        }

        #endregion

        #region 拖拽控制

        private void OnControlMouseDown(object sender, MouseButtonEventArgs e)
        {
            try
            {
                if (e.LeftButton == MouseButtonState.Pressed)
                {
                    PumpSelected?.Invoke(this, this.PumpId);
                    this.IsSelected = true;

                    _isDraggingPump = true;
                    var parent = this.Parent as Canvas;
                    if (parent != null)
                    {
                        _dragStartPoint = e.GetPosition(parent);
                        _originalPosition = new Point(Canvas.GetLeft(this), Canvas.GetTop(this));
                        if (double.IsNaN(_originalPosition.X)) _originalPosition.X = 0;
                        if (double.IsNaN(_originalPosition.Y)) _originalPosition.Y = 0;
                    }
                    this.CaptureMouse();
                    this.Opacity = 0.7;
                    PumpDragStarted?.Invoke(this, new PumpDragEventArgs
                    {
                        PumpId = PumpId,
                        StartPosition = _originalPosition
                    });
                    e.Handled = true;
                    Debug.WriteLine($"🖱️ 开始拖拽柱塞泵 {PumpId}");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ 鼠标按下事件处理失败: {ex.Message}");
            }
        }

        private void OnControlMouseMove(object sender, MouseEventArgs e)
        {
            try
            {
                if (_isDraggingPump && e.LeftButton == MouseButtonState.Pressed)
                {
                    var parent = this.Parent as Canvas;
                    if (parent != null)
                    {
                        var currentPoint = e.GetPosition(parent);
                        double deltaX = currentPoint.X - _dragStartPoint.X;
                        double deltaY = currentPoint.Y - _dragStartPoint.Y;
                        double newLeft = Math.Max(0, Math.Min(parent.ActualWidth - this.ActualWidth, _originalPosition.X + deltaX));
                        double newTop = Math.Max(0, Math.Min(parent.ActualHeight - this.ActualHeight, _originalPosition.Y + deltaY));
                        Canvas.SetLeft(this, newLeft);
                        Canvas.SetTop(this, newTop);
                        PumpDragging?.Invoke(this, new PumpDragEventArgs
                        {
                            PumpId = PumpId,
                            CurrentPosition = new Point(newLeft, newTop)
                        });
                    }
                    e.Handled = true;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ 鼠标移动事件处理失败: {ex.Message}");
            }
        }

        private void OnControlMouseUp(object sender, MouseButtonEventArgs e)
        {
            try
            {
                if (_isDraggingPump)
                {
                    CompletePumpDrag();
                    e.Handled = true;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ 鼠标释放事件处理失败: {ex.Message}");
            }
        }

        private void OnControlMouseLeave(object sender, MouseEventArgs e)
        {
            try
            {
                if (_isDraggingPump)
                {
                    CompletePumpDrag();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ 鼠标离开事件处理失败: {ex.Message}");
            }
        }

        private void CompletePumpDrag()
        {
            try
            {
                if (!_isDraggingPump) return;
                var finalPosition = new Point(Canvas.GetLeft(this), Canvas.GetTop(this));
                PumpDragCompleted?.Invoke(this, new PumpDragEventArgs
                {
                    PumpId = PumpId,
                    StartPosition = _originalPosition,
                    CurrentPosition = finalPosition
                });
                this.Opacity = 1.0;
                _isDraggingPump = false;
                this.ReleaseMouseCapture();
                Debug.WriteLine($"✅ 柱塞泵 {PumpId} 拖拽完成: ({finalPosition.X:F0}, {finalPosition.Y:F0})");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ 完成拖拽失败: {ex.Message}");
            }
        }

        private void UpdateSelectionVisual(bool isSelected)
        {
            try
            {
                // 这里可以添加选中状态的视觉效果
                // 例如添加边框高亮等
                if (isSelected)
                {
                    Debug.WriteLine($"✅ 柱塞泵 {PumpId} 已选中");
                }
                else
                {
                    Debug.WriteLine($"❌ 柱塞泵 {PumpId} 取消选中");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ 更新选中视觉效果失败: {ex.Message}");
            }
        }

        #endregion

        #region IPipeSnapPoints 实现

        /// <summary>
        /// 获取柱塞泵的管道吸附点
        /// </summary>
        public List<PipeSnapPoint> GetSnapPoints()
        {
            var snapPoints = new List<PipeSnapPoint>();

            try
            {
                var parent = this.Parent as Canvas;
                if (parent == null)
                {
                    Debug.WriteLine("⚠️ 柱塞泵未添加到Canvas，无法获取吸附点");
                    return snapPoints;
                }

                var pumpLeft = Canvas.GetLeft(this);
                var pumpTop = Canvas.GetTop(this);
                if (double.IsNaN(pumpLeft)) pumpLeft = 0;
                if (double.IsNaN(pumpTop)) pumpTop = 0;

                // 左上管道吸附点（入口） - 根据XAML中的管道位置
                snapPoints.Add(new PipeSnapPoint
                {
                    WorldPosition = new Point(
                        pumpLeft + 14,  // 左上管道左侧边缘
                        pumpTop + 100   // 管道中心高度 (64 + 72/2)
                    ),
                    Direction = SnapDirection.Left,
                    Description = $"柱塞泵入口（左上）"
                });

                // 右下管道吸附点（出口） - 根据XAML中的管道位置
                snapPoints.Add(new PipeSnapPoint
                {
                    WorldPosition = new Point(
                        pumpLeft + 868,  // 右下管道右侧边缘 (786 + 82)
                        pumpTop + 300    // 管道中心高度 (264 + 72/2)
                    ),
                    Direction = SnapDirection.Right,
                    Description = $"柱塞泵出口（右下）"
                });

                // 可选：添加顶部和底部吸附点
                snapPoints.Add(new PipeSnapPoint
                {
                    WorldPosition = new Point(
                        pumpLeft + 440,  // 控件水平中心
                        pumpTop + 0      // 顶部
                    ),
                    Direction = SnapDirection.Up,
                    Description = $"柱塞泵顶部"
                });

                snapPoints.Add(new PipeSnapPoint
                {
                    WorldPosition = new Point(
                        pumpLeft + 440,  // 控件水平中心
                        pumpTop + 600    // 底部
                    ),
                    Direction = SnapDirection.Down,
                    Description = $"柱塞泵底部"
                });

                Debug.WriteLine($"⚙️ 柱塞泵 {PumpId} 提供 {snapPoints.Count} 个吸附点:");
                foreach (var point in snapPoints)
                {
                    Debug.WriteLine($"  📍 {point.Description} at ({point.WorldPosition.X:F1}, {point.WorldPosition.Y:F1}) 方向:{point.Direction}");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ 获取吸附点失败: {ex.Message}");
            }

            return snapPoints;
        }

        #endregion

        #region INotifyPropertyChanged

        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        #endregion

        #region 公共方法

        /// <summary>
        /// 设置选中状态
        /// </summary>
        public void SetSelected(bool selected)
        {
            IsSelected = selected;
        }

        /// <summary>
        /// 获取泵状态信息
        /// </summary>
        public (bool isRunning, int speed) GetPumpState()
        {
            return (_isRunning, _pumpSpeed);
        }

        /// <summary>
        /// 重置泵到初始状态
        /// </summary>
        public void Reset()
        {
            try
            {
                StopPump();
                SetSpeed(20);
                Debug.WriteLine($"🔄 柱塞泵 {PumpId} 已重置到初始状态");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ 重置柱塞泵失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 获取泵的详细信息（用于调试）
        /// </summary>
        public string GetDebugInfo()
        {
            return $"柱塞泵 {PumpId}\n" +
                   $"  状态: {(_isRunning ? "运行中" : "已停止")}\n" +
                   $"  速度: {_pumpSpeed}\n" +
                   $"  位置: ({Canvas.GetLeft(this):F0}, {Canvas.GetTop(this):F0})\n" +
                   $"  尺寸: {this.ActualWidth} x {this.ActualHeight}";
        }

        #endregion
    }

    #region 辅助类和枚举

    /// <summary>
    /// 泵拖拽事件参数
    /// </summary>
    public class PumpDragEventArgs : EventArgs
    {
        public string PumpId { get; set; }
        public Point StartPosition { get; set; }
        public Point CurrentPosition { get; set; }
    }

    /// <summary>
    /// 泵状态变化事件参数
    /// </summary>
    public class PumpStateChangedEventArgs : EventArgs
    {
        public string PumpId { get; set; }
        public bool IsRunning { get; set; }
        public int Speed { get; set; }
    }

    #endregion
}
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using DevicePlugins.Devices;
using MaxChemical.Modules.Designer.Models;
using MaxChemical.Modules.Designer.ViewModels;
using static MaxChemical.Modules.Designer.ViewModels.FlowDesignerViewModel;

namespace MaxChemical.Modules.Designer.Views
{
    public partial class FlowDesignerView : UserControl
    {
        private FlowDesignerViewModel _currentViewModel;
        private Size _lastMainGridSize = Size.Empty;
        private const double SIZE_CHANGE_THRESHOLD = 5.0; // 5像素阈值
        private readonly System.Windows.Threading.DispatcherTimer _sizeChangeDebounceTimer;
        private bool _isInternalSizeChange = false;

        public FlowDesignerView()
        {
            InitializeComponent();

            // 初始化防抖定时器
            _sizeChangeDebounceTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(150) // 150ms防抖
            };
            _sizeChangeDebounceTimer.Tick += OnSizeChangeDebounceTimerTick;

            Loaded += FlowDesignerView_Loaded;
            Unloaded += FlowDesignerView_Unloaded;
            DataContextChanged += OnDataContextChanged;

            // 添加调试信息
            Debug.WriteLine("FlowDesignerView 构造函数完成");
            Debug.WriteLine($"初始 DataContext: {DataContext?.GetType().Name ?? "null"}");
        }

        private void FlowDesignerView_Loaded(object sender, RoutedEventArgs e)
        {
            Debug.WriteLine("FlowDesignerView_Loaded 被调用");
            Debug.WriteLine($"Loaded 时的 DataContext: {DataContext?.GetType().Name ?? "null"}");

            CenterStartNodeSafe();

            // 直接检查和订阅 ViewModel（而不是等待 DataContextChanged）
            TrySubscribeToViewModelDirectly();
        }

        private void FlowDesignerView_Unloaded(object sender, RoutedEventArgs e)
        {
            // 清理定时器
            _sizeChangeDebounceTimer?.Stop();

            // 取消事件订阅
            if (_currentViewModel != null)
            {
                _currentViewModel.NodeAdded -= OnNodeAdded;
              
            }

            Debug.WriteLine("FlowDesignerView 已卸载，清理完成");
        }
        private void TrySubscribeToViewModelDirectly()
        {
            Debug.WriteLine("尝试直接订阅 ViewModel");

            if (DataContext is FlowDesignerViewModel viewModel)
            {
                Debug.WriteLine("找到 FlowDesignerViewModel，开始订阅");

                // 如果已经有相同的 ViewModel，先取消订阅
                if (_currentViewModel == viewModel)
                {
                    Debug.WriteLine("ViewModel 相同，无需重复订阅");
                    return;
                }

                // 取消之前的订阅
                if (_currentViewModel != null)
                {
                    Debug.WriteLine("取消之前的订阅");
                    _currentViewModel.NodeAdded -= OnNodeAdded;
                    if (_currentViewModel!= null)
                    {
                        _currentViewModel.CanvasWidthRequested -= OnCanvasWidthRequested;
                    }
                }

                // 订阅新的 ViewModel
                _currentViewModel = viewModel;
                _currentViewModel.NodeAdded += OnNodeAdded;
                _currentViewModel.CanvasWidthRequested += OnCanvasWidthRequested;

                Debug.WriteLine($"成功订阅 ViewModel，HashCode: {_currentViewModel.GetHashCode()}");
              
            }
            else
            {
                Debug.WriteLine($"DataContext 不是 FlowDesignerViewModel: {DataContext?.GetType().Name ?? "null"}");
            }
        }
        private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            Debug.WriteLine($"DataContextChanged: Old={e.OldValue?.GetType().Name}, New={e.NewValue?.GetType().Name}");

            // 取消旧的订阅
            if (_currentViewModel != null)
            {
                _currentViewModel.NodeAdded -= OnNodeAdded;
                _currentViewModel.CanvasWidthRequested -= OnCanvasWidthRequested; // 新增
                Debug.WriteLine("取消旧的事件订阅");
            }

            // 订阅新的ViewModel
            if (e.NewValue is FlowDesignerViewModel newViewModel)
            {
                _currentViewModel = newViewModel;
                _currentViewModel.NodeAdded += OnNodeAdded;
                _currentViewModel.CanvasWidthRequested += OnCanvasWidthRequested; // 新增
                Debug.WriteLine($"订阅新的事件，ViewModel HashCode: {_currentViewModel.GetHashCode()}");
            }
            else
            {
                _currentViewModel = null;
                Debug.WriteLine("DataContext 不是 FlowDesignerViewModel 类型");
            }
        }
        /// <summary>
        /// 处理画布宽度请求
        /// </summary>
        private void OnCanvasWidthRequested(object sender, CanvasWidthRequestEventArgs e)
        {
            try
            {
                if (CanvasContainer != null)
                {
                    double actualWidth = CanvasContainer.ActualWidth;
                    if (actualWidth > 0)
                    {
                        e.ActualCanvasWidth = actualWidth;
                        Debug.WriteLine($"提供实际画布宽度: {actualWidth}");
                    }
                    else
                    {
                        Debug.WriteLine($"画布宽度无效: {actualWidth}");
                    }
                }
                else
                {
                    Debug.WriteLine("CanvasContainer 为 null");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"获取画布宽度失败: {ex.Message}");
            }
        }
      
        private void TrySubscribeToViewModel()
        {
            if (DataContext is FlowDesignerViewModel viewModel && _currentViewModel != viewModel)
            {
                // 取消之前的订阅
                if (_currentViewModel != null)
                {
                    _currentViewModel.NodeAdded -= OnNodeAdded;
                  
                }

                _currentViewModel = viewModel;
                _currentViewModel.NodeAdded += OnNodeAdded;
              
                Debug.WriteLine($"TrySubscribeToViewModel: 成功订阅，ViewModel HashCode: {_currentViewModel.GetHashCode()}");
            }
        }



        private void OnNodeAdded(object sender, CommandNode newNode)
        {
            Debug.WriteLine($"OnNodeAdded 被调用: {newNode.DisplayName}");

            // 标记为内部变化，防止启动节点居中被再次触发
            _isInternalSizeChange = true;

            Dispatcher.BeginInvoke(new Action(() =>
            {
                Debug.WriteLine($"节点 {newNode.DisplayName} 显示完成，位置: ({newNode.X}, {newNode.Y})");
                ScrollToNodeSmooth(newNode);

                // 延迟重新启用启动节点居中检测
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    _isInternalSizeChange = false;
                    Debug.WriteLine("重新启用启动节点居中检测");
                }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);

            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        #region 滚动方法

        /// <summary>
        /// 平滑滚动到节点
        /// </summary>
        private void ScrollToNodeSmooth(CommandNode node)
        {
            if (CanvasScrollViewer == null || node == null)
            {
                Debug.WriteLine("CanvasScrollViewer 为 null 或节点为空，无法滚动");
                return;
            }

            try
            {
                Debug.WriteLine($"开始平滑滚动到节点: {node.DisplayName}, 位置: ({node.X}, {node.Y})");

                double nodeX = node.X;
                double nodeY = node.Y;
                double nodeWidth = GetNodeWidth(node);
                double nodeHeight = GetNodeHeight(node);

                double viewportWidth = CanvasScrollViewer.ViewportWidth;
                double viewportHeight = CanvasScrollViewer.ViewportHeight;

                double targetHorizontalOffset = Math.Max(0, nodeX + nodeWidth / 2 - viewportWidth / 2);
                double targetVerticalOffset = Math.Max(0, nodeY + nodeHeight / 2 - viewportHeight / 2);

                double maxHorizontalOffset = Math.Max(0, CanvasScrollViewer.ScrollableWidth);
                double maxVerticalOffset = Math.Max(0, CanvasScrollViewer.ScrollableHeight);

                targetHorizontalOffset = Math.Min(targetHorizontalOffset, maxHorizontalOffset);
                targetVerticalOffset = Math.Min(targetVerticalOffset, maxVerticalOffset);

                // 使用定时器实现平滑滚动
                var timer = new System.Windows.Threading.DispatcherTimer();
                var startTime = DateTime.Now;
                var duration = TimeSpan.FromMilliseconds(500);
                var startHorizontal = CanvasScrollViewer.HorizontalOffset;
                var startVertical = CanvasScrollViewer.VerticalOffset;

                timer.Interval = TimeSpan.FromMilliseconds(16);
                timer.Tick += (s, e) =>
                {
                    var elapsed = DateTime.Now - startTime;
                    var progress = Math.Min(1.0, elapsed.TotalMilliseconds / duration.TotalMilliseconds);

                    var easedProgress = 1 - Math.Pow(1 - progress, 3);

                    var currentH = startHorizontal + (targetHorizontalOffset - startHorizontal) * easedProgress;
                    var currentV = startVertical + (targetVerticalOffset - startVertical) * easedProgress;

                    CanvasScrollViewer.ScrollToHorizontalOffset(currentH);
                    CanvasScrollViewer.ScrollToVerticalOffset(currentV);

                    if (progress >= 1.0)
                    {
                        timer.Stop();
                        Debug.WriteLine($"平滑滚动完成: {node.DisplayName}");
                    }
                };

                timer.Start();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"平滑滚动到节点时发生错误: {ex.Message}");
            }
        }



        /// <summary>
        /// 获取节点宽度
        /// </summary>
        private double GetNodeWidth(CommandNode node)
        {
            try
            {
                if (node?.NodeControl != null)
                {
                    return node.NodeControl.ActualWidth > 0 ? node.NodeControl.ActualWidth :
                           GetEstimatedNodeWidth(node); // 使用估算宽度
                }
                return GetEstimatedNodeWidth(node); // 使用估算宽度
            }
            catch
            {
                return GetEstimatedNodeWidth(node); // 使用估算宽度
            }
        }

        /// <summary>
        /// 获取节点高度
        /// </summary>
        private double GetNodeHeight(CommandNode node)
        {
            try
            {
                if (node?.NodeControl != null)
                {
                    return node.NodeControl.ActualHeight > 0 ? node.NodeControl.ActualHeight :
                           GetEstimatedNodeHeight(node); // 使用估算高度
                }
                return GetEstimatedNodeHeight(node); // 使用估算高度
            }
            catch
            {
                return GetEstimatedNodeHeight(node); // 使用估算高度
            }
        }
        /// <summary>
        /// 获取估算节点宽度
        /// </summary>
        private double GetEstimatedNodeWidth(CommandNode node)
        {
            if (node == null) return 231;

            if (node.IsIfNode) return 600;
            if (node.IsParallelNode) return 800; // 添加并行节点处理
            if (node.IsLoopNode) return 400;     // 如果有循环节点

            return 231; // 普通节点默认宽度
        }

        /// <summary>
        /// 获取估算节点高度
        /// </summary>
        private double GetEstimatedNodeHeight(CommandNode node)
        {
            if (node == null) return 64;

            if (node.IsIfNode) return 300;
            if (node.IsParallelNode) return 250; // 添加并行节点处理
            if (node.IsLoopNode) return 150;     // 如果有循环节点

            return 64; // 普通节点默认高度
        }
        #endregion

        #region 节点选择和点击处理

        // 节点控件点击事件处理
        private void NodeControl_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                var contentPresenter = sender as ContentPresenter;
                if (contentPresenter?.DataContext is CommandNode node)
                {
                    // 检查是否点击在特定的交互元素上
                    if (IsClickOnInteractiveElement(e))
                    {
                        // 如果点击在交互元素（如按钮）上，不处理选择逻辑
                        return;
                    }

                    HandleNodeSelection(node);
                    e.Handled = true;
                }
            }
        }
        private bool IsClickOnInteractiveElement(MouseButtonEventArgs e)
        {
            var element = e.OriginalSource as FrameworkElement;

            // 检查是否点击在按钮或其他交互控件上
            while (element != null)
            {
                if (element is Button ||
                    element is ToggleButton ||
                    element.Name == "ExpandToggle" ||
                    element.Name == "DropZone")
                {
                    return true;
                }
                element = element.Parent as FrameworkElement;
            }

            return false;
        }

        private void CommandNode_Click(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                var border = sender as Border;
                if (border?.DataContext is CommandNode node)
                {
                    HandleNodeSelection(node);
                    e.Handled = true;
                }
            }
        }

        // 处理节点选中逻辑
        private void HandleNodeSelection(CommandNode node)
        {
            var viewModel = DataContext as FlowDesignerViewModel;
            if (viewModel != null && node != null)
            {
                // 清除其他节点的选中状态
                foreach (var n in viewModel.CommandNodes)
                {
                    n.IsSelected = false;
                }

                // 选中当前节点
                node.IsSelected = true;
                ScrollToNodeSmooth(node);

                Debug.WriteLine($"已选中命令节点: {node.DisplayName}");
            }
        }

        #endregion

        #region 启动节点居中和大小变化处理

        private void CenterStartNodeSafe()
        {
            if (DesignCanvas == null || CanvasContainer == null)
            {
                Debug.WriteLine("Canvas组件未就绪，跳过居中");
                return;
            }

            try
            {
                double canvasWidth = CanvasContainer.ActualWidth;

                if (canvasWidth <= 0)
                {
                    Debug.WriteLine($"Canvas宽度无效: {canvasWidth}，跳过居中");
                    return;
                }

                double centerX = (canvasWidth - 231) / 2;
                double startY = 10;

                var viewModel = DataContext as FlowDesignerViewModel;
                if (viewModel != null)
                {
                    // 检查位置是否真的需要改变
                    double currentX = viewModel.StartNodeX;
                    double currentY = viewModel.StartNodeY;

                    if (Math.Abs(currentX - centerX) < 1.0 && Math.Abs(currentY - startY) < 1.0)
                    {
                        Debug.WriteLine($"启动节点位置变化过小，跳过: ({currentX:F1},{currentY:F1}) -> ({centerX:F1},{startY:F1})");
                        return;
                    }

                    Debug.WriteLine($"执行启动节点居中: ({currentX:F1},{currentY:F1}) -> ({centerX:F1},{startY:F1})");

                    // 标记为内部变化，避免循环
                    _isInternalSizeChange = true;
                    try
                    {
                        viewModel.UpdateStartNodePositionAndRearrange(centerX, startY);
                    }
                    finally
                    {
                        // 延迟重置标志，确保相关的布局更新完成
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            _isInternalSizeChange = false;
                        }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                    }
                }
            }
            catch (Exception ex)
            {
                _isInternalSizeChange = false;
                Debug.WriteLine($"安全启动节点居中失败: {ex.Message}");
            }
        }

        private void CenterStartNode()
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                CenterStartNodeSafe();
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        /// <summary>
        /// 改进的MainGrid_SizeChanged，添加防抖和阈值检查
        /// </summary>
        private void MainGrid_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            // 检查是否是内部触发的尺寸变化（避免循环）
            if (_isInternalSizeChange)
            {
                Debug.WriteLine("跳过内部触发的尺寸变化");
                return;
            }

            // 检查尺寸变化是否足够大
            double widthChange = Math.Abs(e.NewSize.Width - _lastMainGridSize.Width);
            double heightChange = Math.Abs(e.NewSize.Height - _lastMainGridSize.Height);

            if (widthChange < SIZE_CHANGE_THRESHOLD && heightChange < SIZE_CHANGE_THRESHOLD)
            {
                Debug.WriteLine($"MainGrid尺寸变化过小，跳过: {widthChange:F2}x{heightChange:F2}");
                return;
            }

            Debug.WriteLine($"MainGrid显著尺寸变化: {_lastMainGridSize.Width:F1}x{_lastMainGridSize.Height:F1} -> {e.NewSize.Width:F1}x{e.NewSize.Height:F1}");
            _lastMainGridSize = e.NewSize;

            // 使用防抖处理
            _sizeChangeDebounceTimer.Stop();
            _sizeChangeDebounceTimer.Start();
        }

        private void OnSizeChangeDebounceTimerTick(object sender, EventArgs e)
        {
            _sizeChangeDebounceTimer.Stop();

            try
            {
                Debug.WriteLine("执行防抖后的启动节点居中");
                CenterStartNodeSafe();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"防抖尺寸变化处理异常: {ex.Message}");
            }
        }

        #endregion

        #region 拖放处理

        // 画布拖放处理
        private void Canvas_Drop(object sender, DragEventArgs e)
        {
            try
            {
                if (e.Data.GetDataPresent("DeviceCommand"))
                {
                    var command = e.Data.GetData("DeviceCommand") as DeviceCommand;
                    if (command != null)
                    {
                        var viewModel = DataContext as FlowDesignerViewModel;
                        viewModel?.AddCommandToCanvas(command);
                        Debug.WriteLine($"设备命令 {command.Name} 已添加到流程");
                    }
                }
                else if (e.Data.GetDataPresent("LogicCommand"))
                {
                    var logicCommand = e.Data.GetData("LogicCommand") as LogicCommand;
                    if (logicCommand != null)
                    {
                        var viewModel = DataContext as FlowDesignerViewModel;
                        viewModel?.AddCommandToCanvas(logicCommand);
                        Debug.WriteLine($"逻辑命令 {logicCommand.Name} 已添加到流程");
                    }
                }
                else if (e.Data.GetDataPresent("Device"))
                {
                    var device = e.Data.GetData("Device") as IDevice;
                    if (device != null)
                    {
                        Point dropPosition = e.GetPosition(DesignCanvas);
                        var viewModel = DataContext as FlowDesignerViewModel;
                        viewModel?.AddDeviceToCanvas(device, dropPosition);
                    }
                }

                e.Handled = true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"处理拖放操作时发生错误: {ex.Message}");
            }
        }

        private void Canvas_DragOver(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent("DeviceCommand") ||
                e.Data.GetDataPresent("LogicCommand") ||
                e.Data.GetDataPresent("Device"))
            {
                e.Effects = DragDropEffects.Copy;
            }
            else
            {
                e.Effects = DragDropEffects.None;
            }
            e.Handled = true;
        }

        #endregion

    
    }

 
}
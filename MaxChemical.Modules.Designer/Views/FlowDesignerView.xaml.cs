// FlowDesignerView.xaml.cs (重构后)
using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DevicePlugins.Devices;
using MaxChemical.Modules.Designer.Models;
using MaxChemical.Modules.Designer.ViewModels;
using MaxChemical.Modules.Designer.Services;
using MaxChemical.Logging;
using static MaxChemical.Modules.Designer.ViewModels.FlowDesignerViewModel;

namespace MaxChemical.Modules.Designer.Views
{
    public partial class FlowDesignerView : UserControl
    {
        private FlowDesignerViewModel _currentViewModel;
        private Size _lastMainGridSize = Size.Empty;
        private const double SIZE_CHANGE_THRESHOLD = 5.0;
        private readonly System.Windows.Threading.DispatcherTimer _sizeChangeDebounceTimer;
        private bool _isInternalSizeChange = false;
        public event EventHandler<CommandNode> NodeDeleteRequested;
        private readonly ILogService _logger;
        private readonly NodeManagementService _nodeManagementService;
        private readonly DesignerDragDropService _dragDropService;

        public FlowDesignerView()
        {
            InitializeComponent();

            // 初始化服务
            _logger = new LogService().ForContext<FlowDesignerView>();
            _nodeManagementService = new NodeManagementService(_logger);
            _dragDropService = new DesignerDragDropService(_logger);

            // 初始化防抖定时器
            _sizeChangeDebounceTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(150)
            };
            _sizeChangeDebounceTimer.Tick += OnSizeChangeDebounceTimerTick;

            Loaded += FlowDesignerView_Loaded;
            Unloaded += FlowDesignerView_Unloaded;
            DataContextChanged += OnDataContextChanged;

            _logger.LogDebug("FlowDesignerView 构造函数完成");
            _logger.LogDebug("初始 DataContext: {Type}", DataContext?.GetType().Name ?? "null");
        }

        private void FlowDesignerView_Loaded(object sender, RoutedEventArgs e)
        {
            _logger.LogDebug("FlowDesignerView_Loaded 被调用");
            _logger.LogDebug("Loaded 时的 DataContext: {Type}", DataContext?.GetType().Name ?? "null");

            CenterStartNodeSafe();
            TrySubscribeToViewModelDirectly();
        }

        private void FlowDesignerView_Unloaded(object sender, RoutedEventArgs e)
        {
            _sizeChangeDebounceTimer?.Stop();

            if (_currentViewModel != null)
            {
                _currentViewModel.NodeAdded -= OnNodeAdded;
                _currentViewModel.CanvasWidthRequested -= OnCanvasWidthRequested;
            }

            _logger.LogDebug("FlowDesignerView 已卸载，清理完成");
        }

        private void TrySubscribeToViewModelDirectly()
        {
            _logger.LogDebug("尝试直接订阅 ViewModel");

            if (DataContext is FlowDesignerViewModel viewModel)
            {
                _logger.LogDebug("找到 FlowDesignerViewModel，开始订阅");

                if (_currentViewModel == viewModel)
                {
                    _logger.LogDebug("ViewModel 相同，无需重复订阅");
                    return;
                }

                if (_currentViewModel != null)
                {
                    _logger.LogDebug("取消之前的订阅");
                    _currentViewModel.NodeAdded -= OnNodeAdded;
                    _currentViewModel.CanvasWidthRequested -= OnCanvasWidthRequested;
                }

                _currentViewModel = viewModel;
                _currentViewModel.NodeAdded += OnNodeAdded;
                _currentViewModel.CanvasWidthRequested += OnCanvasWidthRequested;

                _logger.LogDebug("成功订阅 ViewModel，HashCode: {HashCode}", _currentViewModel.GetHashCode());
            }
            else
            {
                _logger.LogDebug("DataContext 不是 FlowDesignerViewModel: {Type}", DataContext?.GetType().Name ?? "null");
            }
        }

        private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            _logger.LogDebug("DataContextChanged: Old={Old}, New={New}",
                e.OldValue?.GetType().Name, e.NewValue?.GetType().Name);

            if (_currentViewModel != null)
            {
                _currentViewModel.NodeAdded -= OnNodeAdded;
                _currentViewModel.CanvasWidthRequested -= OnCanvasWidthRequested;
                _logger.LogDebug("取消旧的事件订阅");
            }

            if (e.NewValue is FlowDesignerViewModel newViewModel)
            {
                _currentViewModel = newViewModel;
                _currentViewModel.NodeAdded += OnNodeAdded;
                _currentViewModel.CanvasWidthRequested += OnCanvasWidthRequested;
                _logger.LogDebug("订阅新的事件，ViewModel HashCode: {HashCode}", _currentViewModel.GetHashCode());
            }
            else
            {
                _currentViewModel = null;
                _logger.LogDebug("DataContext 不是 FlowDesignerViewModel 类型");
            }
        }

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
                        _logger.LogTrace("提供实际画布宽度: {Width}", actualWidth);
                    }
                    else
                    {
                        _logger.LogTrace("画布宽度无效: {Width}", actualWidth);
                    }
                }
                else
                {
                    _logger.LogTrace("CanvasContainer 为 null");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取画布宽度失败");
            }
        }

        private void OnNodeAdded(object sender, CommandNode newNode)
        {
            _logger.LogDebug("OnNodeAdded 被调用: {NodeName}", newNode.DisplayName);

            _isInternalSizeChange = true;

            Dispatcher.BeginInvoke(new Action(() =>
            {
                _logger.LogDebug("节点 {NodeName} 显示完成，位置: ({X}, {Y})", newNode.DisplayName, newNode.X, newNode.Y);
                ScrollToNodeSmooth(newNode);

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    _isInternalSizeChange = false;
                    _logger.LogTrace("重新启用启动节点居中检测");
                }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);

            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        #region 滚动方法

        private void ScrollToNodeSmooth(CommandNode node)
        {
            if (CanvasScrollViewer == null || node == null)
            {
                _logger.LogWarning("CanvasScrollViewer 为 null 或节点为空，无法滚动");
                return;
            }

            try
            {
                _logger.LogDebug("开始平滑滚动到节点: {NodeName}, 位置: ({X}, {Y})", node.DisplayName, node.X, node.Y);

                double nodeX = node.X;
                double nodeY = node.Y;
                double nodeWidth = GetNodeWidth(node);
                double nodeHeight = GetNodeHeight(node);

                double viewportWidth = CanvasScrollViewer.ViewportWidth;
                double viewportHeight = CanvasScrollViewer.ViewportHeight;

                // 计算节点中心点
                double nodeCenterX = nodeX + nodeWidth / 2;
                double nodeCenterY = nodeY + nodeHeight / 2;

                // 计算目标偏移量，使节点中心位于视口中心
                double targetHorizontalOffset = nodeCenterX - viewportWidth / 2;
                double targetVerticalOffset = nodeCenterY - viewportHeight / 2;

                // 确保偏移量在有效范围内
                double maxHorizontalOffset = Math.Max(0, CanvasScrollViewer.ScrollableWidth);
                double maxVerticalOffset = Math.Max(0, CanvasScrollViewer.ScrollableHeight);

                targetHorizontalOffset = Math.Max(0, Math.Min(targetHorizontalOffset, maxHorizontalOffset));
                targetVerticalOffset = Math.Max(0, Math.Min(targetVerticalOffset, maxVerticalOffset));

                _logger.LogDebug("目标滚动位置: H={H}, V={V}, 视口大小: {W}x{H}",
                    targetHorizontalOffset, targetVerticalOffset, viewportWidth, viewportHeight);

                var timer = new System.Windows.Threading.DispatcherTimer();
                var startTime = DateTime.Now;
                var duration = TimeSpan.FromMilliseconds(500);
                var startHorizontal = CanvasScrollViewer.HorizontalOffset;
                var startVertical = CanvasScrollViewer.VerticalOffset;

                timer.Interval = TimeSpan.FromMilliseconds(16); // ~60fps
                timer.Tick += (s, e) =>
                {
                    var elapsed = DateTime.Now - startTime;
                    var progress = Math.Min(1.0, elapsed.TotalMilliseconds / duration.TotalMilliseconds);

                    // 使用缓动函数，让滚动更平滑
                    var easedProgress = EaseOutCubic(progress);

                    var currentH = startHorizontal + (targetHorizontalOffset - startHorizontal) * easedProgress;
                    var currentV = startVertical + (targetVerticalOffset - startVertical) * easedProgress;

                    CanvasScrollViewer.ScrollToHorizontalOffset(currentH);
                    CanvasScrollViewer.ScrollToVerticalOffset(currentV);

                    if (progress >= 1.0)
                    {
                        timer.Stop();
                        _logger.LogDebug("平滑滚动完成: {NodeName}, 最终位置: H={H}, V={V}",
                            node.DisplayName, CanvasScrollViewer.HorizontalOffset, CanvasScrollViewer.VerticalOffset);
                    }
                };

                timer.Start();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "平滑滚动到节点时发生错误: {NodeName}", node?.DisplayName);
            }
        }

        // 缓动函数：三次方缓出
        private double EaseOutCubic(double t)
        {
            return 1 - Math.Pow(1 - t, 3);
        }

        // 添加一个重载方法，可以指定滚动到特定位置
        private void ScrollToPositionSmooth(double x, double y)
        {
            if (CanvasScrollViewer == null)
            {
                _logger.LogWarning("CanvasScrollViewer 为 null，无法滚动");
                return;
            }

            try
            {
                _logger.LogDebug("开始平滑滚动到位置: ({X}, {Y})", x, y);

                double viewportWidth = CanvasScrollViewer.ViewportWidth;
                double viewportHeight = CanvasScrollViewer.ViewportHeight;

                // 计算目标偏移量，使指定位置位于视口中心
                double targetHorizontalOffset = x - viewportWidth / 2;
                double targetVerticalOffset = y - viewportHeight / 2;

                // 确保偏移量在有效范围内
                double maxHorizontalOffset = Math.Max(0, CanvasScrollViewer.ScrollableWidth);
                double maxVerticalOffset = Math.Max(0, CanvasScrollViewer.ScrollableHeight);

                targetHorizontalOffset = Math.Max(0, Math.Min(targetHorizontalOffset, maxHorizontalOffset));
                targetVerticalOffset = Math.Max(0, Math.Min(targetVerticalOffset, maxVerticalOffset));

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

                    var easedProgress = EaseOutCubic(progress);

                    var currentH = startHorizontal + (targetHorizontalOffset - startHorizontal) * easedProgress;
                    var currentV = startVertical + (targetVerticalOffset - startVertical) * easedProgress;

                    CanvasScrollViewer.ScrollToHorizontalOffset(currentH);
                    CanvasScrollViewer.ScrollToVerticalOffset(currentV);

                    if (progress >= 1.0)
                    {
                        timer.Stop();
                        _logger.LogDebug("平滑滚动到位置完成: ({X}, {Y})", x, y);
                    }
                };

                timer.Start();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "平滑滚动到位置时发生错误: ({X}, {Y})", x, y);
            }
        }

        // 滚动到画布中心
        private void ScrollToCanvasCenter()
        {
            if (CanvasScrollViewer == null)
            {
                _logger.LogWarning("CanvasScrollViewer 为 null，无法滚动到中心");
                return;
            }

            double canvasCenterX = CanvasScrollViewer.ScrollableWidth / 2;
            double canvasCenterY = CanvasScrollViewer.ScrollableHeight / 2;

            ScrollToPositionSmooth(canvasCenterX, canvasCenterY);
        }

        private double GetNodeWidth(CommandNode node)
        {
            try
            {
                if (node?.NodeControl != null)
                {
                    return node.NodeControl.ActualWidth > 0 ? node.NodeControl.ActualWidth :
                           GetEstimatedNodeWidth(node);
                }
                return GetEstimatedNodeWidth(node);
            }
            catch
            {
                return GetEstimatedNodeWidth(node);
            }
        }

        private double GetNodeHeight(CommandNode node)
        {
            try
            {
                if (node?.NodeControl != null)
                {
                    return node.NodeControl.ActualHeight > 0 ? node.NodeControl.ActualHeight :
                           GetEstimatedNodeHeight(node);
                }
                return GetEstimatedNodeHeight(node);
            }
            catch
            {
                return GetEstimatedNodeHeight(node);
            }
        }

        private double GetEstimatedNodeWidth(CommandNode node)
        {
            if (node == null) return 231;

            if (node.IsIfNode) return 600;
            if (node.IsParallelNode) return 800;
            if (node.IsLoopNode) return 400;

            return 231;
        }

        private double GetEstimatedNodeHeight(CommandNode node)
        {
            if (node == null) return 64;

            if (node.IsIfNode) return 300;
            if (node.IsParallelNode) return 250;
            if (node.IsLoopNode) return 150;

            return 64;
        }

        #endregion

        #region 节点选择和点击处理
        // 设备节点选择点击
        private void DeviceNode_SelectClick(object sender, MouseButtonEventArgs e)
        {
            _logger.LogDebug("🔍 设备节点选择点击");

            try
            {
                if (sender is FrameworkElement element && element.DataContext is CommandNode node)
                {
                    _logger.LogDebug("选择设备节点: {NodeName}", node.DisplayName);
                    HandleNodeSelection(node);
                    e.Handled = true;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "设备节点选择失败");
            }
        }

        // 设备节点删除点击
        private void DeviceNode_DeleteClick(object sender, RoutedEventArgs e)
        {
            _logger.LogDebug("设备节点删除点击");

            try
            {
                if (sender is Border border && border.Tag is CommandNode nodeToDelete)
                {
                    _logger.LogDebug("删除设备节点: {NodeName}", nodeToDelete.DisplayName);

                    var viewModel = DataContext as FlowDesignerViewModel;
                    if (viewModel?.RemoveCommandNodeCommand?.CanExecute(nodeToDelete) == true)
                    {
                        viewModel.RemoveCommandNodeCommand.Execute(nodeToDelete);
                        _logger.LogDebug("删除设备节点成功");
                    }
                    else
                    {
                        _logger.LogWarning("删除命令无法执行");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "删除设备节点失败");
            }
        }

        private void NodeControl_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // 这个方法现在只处理逻辑节点（IF、循环、并行等）
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                _logger.LogTrace("逻辑节点 PreviewMouseLeftButtonDown 触发");

                var contentPresenter = sender as ContentPresenter;
                if (contentPresenter?.DataContext is CommandNode node)
                {
                    _logger.LogTrace("逻辑节点: {NodeName}", node.DisplayName);

                    if (IsClickFromNestedNode(e.OriginalSource, node))
                    {
                        _logger.LogTrace("点击来自嵌套节点，跳过处理");
                        return;
                    }

                    if (IsClickOnLogicNodeInteractiveElement(e))
                    {
                        _logger.LogTrace("点击在逻辑节点的交互元素上，跳过选择");
                        return;
                    }

                    _logger.LogTrace("处理逻辑节点选择: {NodeName}", node.DisplayName);
                    HandleNodeSelection(node);
                    e.Handled = true;
                }
            }
        }

        // 专门为逻辑节点检查交互元素
        private bool IsClickOnLogicNodeInteractiveElement(MouseButtonEventArgs e)
        {
            var element = e.OriginalSource as FrameworkElement;

            while (element != null)
            {
                if (element is Button ||
                    element is System.Windows.Controls.Primitives.ToggleButton ||
                    element.Name == "ExpandToggle" ||
                    element.Name == "DropZone" ||
                    element.Name == "TrueBranchDropZone" ||
                    element.Name == "FalseBranchDropZone")
                {
                    return true;
                }
                element = element.Parent as FrameworkElement;
            }

            return false;
        }
     

        private bool IsClickFromNestedNode(object originalSource, CommandNode parentNode)
        {
            try
            {
                var element = originalSource as DependencyObject;

                while (element != null)
                {
                    if (element is FrameworkElement fe && fe.Tag is CommandNode tagNode)
                    {
                        if (tagNode != parentNode)
                        {
                            _logger.LogTrace("发现嵌套节点: {NestedNode} (父节点: {ParentNode})",
                                tagNode.DisplayName, parentNode.DisplayName);
                            return true;
                        }
                    }

                    if (element is StackPanel sp)
                    {
                        var spName = sp.Name;
                        if (spName == "TrueBranchContent" || spName == "FalseBranchContent")
                        {
                            _logger.LogTrace("点击在分支内容区域: {Name}", spName);
                            return true;
                        }
                    }

                    if (element is Border border)
                    {
                        var borderName = border.Name;
                        if (borderName == "TrueBranchDropZone" || borderName == "FalseBranchDropZone" ||
                            borderName == "TrueBranchWrapper" || borderName == "FalseBranchWrapper")
                        {
                            _logger.LogTrace("点击在分支区域: {Name}", borderName);
                            return true;
                        }
                    }

                    element = VisualTreeHelper.GetParent(element);
                }

                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "检查嵌套节点点击失败");
                return false;
            }
        }

        private bool IsClickOnInteractiveElement(MouseButtonEventArgs e)
        {
            var element = e.OriginalSource as FrameworkElement;

            while (element != null)
            {
                if (element is Button ||
                    element is System.Windows.Controls.Primitives.ToggleButton ||
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

        private void HandleNodeSelection(CommandNode node)
        {
            var viewModel = DataContext as FlowDesignerViewModel;
            if (viewModel != null && node != null)
            {
                _nodeManagementService.ClearAllSelections(viewModel);
                node.IsSelected = true;
                ScrollToNodeSmooth(node);

                _logger.LogDebug("已选中命令节点: {NodeName}", node.DisplayName);
            }
        }

        private void OnNodeClick(object sender, RoutedEventArgs e)
        {
            try
            {
                CommandNode clickedNode = null;

                if (e.OriginalSource is CommandNode node)
                {
                    clickedNode = node;
                }
                else if (sender is FrameworkElement element && element.DataContext is CommandNode dataNode)
                {
                    clickedNode = dataNode;
                }
                else if (sender is FrameworkElement elem && elem.Tag is CommandNode tagNode)
                {
                    clickedNode = tagNode;
                }

                if (clickedNode != null && this.DataContext is FlowDesignerViewModel viewModel)
                {
                    viewModel.HandleNodeClick(clickedNode);
                    e.Handled = true;
                    _logger.LogDebug("处理节点点击: {NodeName}", clickedNode.DisplayName);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "处理节点点击事件失败");
            }
        }

        private void OnComplexNodeClick(object sender, RoutedEventArgs e)
        {
            try
            {
                if (e.OriginalSource is CommandNode clickedNode)
                {
                    if (this.DataContext is FlowDesignerViewModel viewModel)
                    {
                        viewModel.HandleNodeClick(clickedNode);
                        e.Handled = true;
                        _logger.LogDebug("处理复杂节点点击: {NodeName}", clickedNode.DisplayName);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "处理复杂节点点击事件失败");
            }
        }

        private void ComplexNodeControl_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                if (sender is ContentPresenter presenter && presenter.Content is UserControl nodeControl)
                {
                    switch (nodeControl)
                    {
                        case IfNodeControlView ifControl:
                            ifControl.NodeClicked += OnComplexNodeClicked;
                            _logger.LogTrace("为IF节点订阅了点击事件");
                            break;
                        case LoopNodeControlView loopControl:
                            loopControl.NodeClicked += OnComplexNodeClicked;
                            _logger.LogTrace("为循环节点订阅了点击事件");
                            break;
                        case ParallelNodeControlView parallelControl:
                            parallelControl.NodeClicked += OnComplexNodeClicked;
                            _logger.LogTrace("为并行节点订阅了点击事件");
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "订阅复杂节点点击事件失败");
            }
        }

        private void OnComplexNodeClicked(object sender, CommandNode clickedNode)
        {
            try
            {
                if (clickedNode != null && this.DataContext is FlowDesignerViewModel viewModel)
                {
                    viewModel.HandleNodeClick(clickedNode);
                    _logger.LogDebug("处理复杂节点点击: {NodeName}", clickedNode.DisplayName);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "处理复杂节点点击事件失败");
            }
        }

        #endregion

        #region 启动节点居中和大小变化处理

        private void CenterStartNodeSafe()
        {
            if (DesignCanvas == null || CanvasContainer == null)
            {
                _logger.LogDebug("Canvas组件未就绪，跳过居中");
                return;
            }

            try
            {
                double canvasWidth = CanvasContainer.ActualWidth;

                if (canvasWidth <= 0)
                {
                    _logger.LogDebug("Canvas宽度无效: {Width}，跳过居中", canvasWidth);
                    return;
                }

                double centerX = (canvasWidth - 231) / 2;
                double startY = 10;

                var viewModel = DataContext as FlowDesignerViewModel;
                if (viewModel != null)
                {
                    double currentX = viewModel.StartNodeX;
                    double currentY = viewModel.StartNodeY;

                    if (Math.Abs(currentX - centerX) < 1.0 && Math.Abs(currentY - startY) < 1.0)
                    {
                        _logger.LogTrace("启动节点位置变化过小，跳过: ({CurrentX:F1},{CurrentY:F1}) -> ({CenterX:F1},{StartY:F1})",
                            currentX, currentY, centerX, startY);
                        return;
                    }

                    _logger.LogDebug("执行启动节点居中: ({CurrentX:F1},{CurrentY:F1}) -> ({CenterX:F1},{StartY:F1})",
                        currentX, currentY, centerX, startY);

                    _isInternalSizeChange = true;
                    try
                    {
                        viewModel.UpdateStartNodePositionAndRearrange(centerX, startY);
                    }
                    finally
                    {
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
                _logger.LogError(ex, "安全启动节点居中失败");
            }
        }

        private void MainGrid_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (_isInternalSizeChange)
            {
                _logger.LogTrace("跳过内部触发的尺寸变化");
                return;
            }

            double widthChange = Math.Abs(e.NewSize.Width - _lastMainGridSize.Width);
            double heightChange = Math.Abs(e.NewSize.Height - _lastMainGridSize.Height);

            if (widthChange < SIZE_CHANGE_THRESHOLD && heightChange < SIZE_CHANGE_THRESHOLD)
            {
                _logger.LogTrace("MainGrid尺寸变化过小，跳过: {WidthChange:F2}x{HeightChange:F2}", widthChange, heightChange);
                return;
            }

            _logger.LogDebug("MainGrid显著尺寸变化: {OldWidth:F1}x{OldHeight:F1} -> {NewWidth:F1}x{NewHeight:F1}",
                _lastMainGridSize.Width, _lastMainGridSize.Height, e.NewSize.Width, e.NewSize.Height);
            _lastMainGridSize = e.NewSize;

            _sizeChangeDebounceTimer.Stop();
            _sizeChangeDebounceTimer.Start();
        }

        private void OnSizeChangeDebounceTimerTick(object sender, EventArgs e)
        {
            _sizeChangeDebounceTimer.Stop();

            try
            {
                _logger.LogDebug("执行防抖后的启动节点居中");
                CenterStartNodeSafe();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "防抖尺寸变化处理异常");
            }
        }

        #endregion

        #region 拖放处理

        private void Canvas_Drop(object sender, DragEventArgs e)
        {
            try
            {
                var command = _dragDropService.ExtractCommandFromDragData(e.Data);
                if (command != null)
                {
                    var viewModel = DataContext as FlowDesignerViewModel;

                    if (command is DeviceCommand deviceCmd)
                    {
                        viewModel?.AddCommandToCanvas(deviceCmd);
                        _logger.LogDebug("设备命令 {CommandName} 已添加到流程", deviceCmd.Name);
                    }
                    else if (command is LogicCommand logicCmd)
                    {
                        viewModel?.AddCommandToCanvas(logicCmd);
                        _logger.LogDebug("逻辑命令 {CommandName} 已添加到流程", logicCmd.Name);
                    }
                    else if (command is Services.PluginIdRequest pluginRequest)
                    {
                        var logicCommand = viewModel?.CreateLogicCommandFromPluginId(pluginRequest.PluginId);
                        if (logicCommand != null)
                        {
                            viewModel.AddCommandToCanvas(logicCommand);
                            _logger.LogDebug("通过插件ID创建的逻辑命令 {CommandName} 已添加到流程", logicCommand.Name);
                        }
                    }
                }
                else if (e.Data.GetDataPresent("Device") && e.Data.GetData("Device") is IDevice device)
                {
                    Point dropPosition = e.GetPosition(DesignCanvas);
                    var viewModel = DataContext as FlowDesignerViewModel;
                    viewModel?.AddDeviceToCanvas(device, dropPosition);
                }

                e.Handled = true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "处理拖放操作时发生错误");
            }
        }

        private void Canvas_DragOver(object sender, DragEventArgs e)
        {
            if (_dragDropService.CanAcceptDrop(e.Data) || e.Data.GetDataPresent("Device"))
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
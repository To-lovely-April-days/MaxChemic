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
using MaxChemical.Modules.Designer.Service.Execution;
using Prism.Events;
using System.Windows.Threading;

namespace MaxChemical.Modules.Designer.Views
{
    public partial class FlowDesignerView : UserControl
    {
        private FlowDesignerViewModel _currentViewModel;
        private Size _lastMainGridSize = Size.Empty;
        private const double SIZE_CHANGE_THRESHOLD = 5.0;
        private readonly System.Windows.Threading.DispatcherTimer _sizeChangeDebounceTimer;
        private bool _isInternalSizeChange = false;
        private DragInsertPosition _currentDragPosition;
        public event EventHandler<CommandNode> NodeDeleteRequested;
        private readonly ILogService _logger;
        private readonly NodeManagementService _nodeManagementService;
        private readonly DesignerDragDropService _dragDropService;
        private readonly DragInsertPositionService _dragInsertService;
        private readonly ManualCommandExecutor _manualExecutor;
        private readonly IExperimentDataAdapter _experimentDataAdapter; // 新增：实验数据适配器
                                                                        // ── 1. 在类顶部新增一个私有字段，用来记录是否已完成首次居中 ──
        private bool _initialCenterDone = false;
        public FlowDesignerView(IEventAggregator eventAggregator, IExperimentDataAdapter experimentDataAdapter)
        {
            InitializeComponent();

            // 初始化服务
            _logger = new LogService().ForContext<FlowDesignerView>();
            _nodeManagementService = new NodeManagementService(_logger);
            _dragDropService = new DesignerDragDropService(_logger);
            _dragInsertService = new DragInsertPositionService(_logger);
            _experimentDataAdapter = experimentDataAdapter;
            _manualExecutor = new ManualCommandExecutor(eventAggregator, _experimentDataAdapter);
            // 初始化防抖定时器
            _sizeChangeDebounceTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(150)
            };
            _sizeChangeDebounceTimer.Tick += OnSizeChangeDebounceTimerTick;

            Loaded += FlowDesignerView_Loaded;
            Unloaded += FlowDesignerView_Unloaded;
            DataContextChanged += OnDataContextChanged;
            IsVisibleChanged += FlowDesignerView_IsVisibleChanged;
            InitializeZoom();
            _logger.LogDebug("FlowDesignerView 构造函数完成");
            _logger.LogDebug("初始 DataContext: {Type}", DataContext?.GetType().Name ?? "null");
        }
        private async void DeviceNode_ExecuteToggleClick(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is CommandNode node)
            {
                try
                {
                    // 根据当前执行状态决定执行什么操作
                    if (node.IsManualExecuting)
                    {
                        // 当前正在执行，执行停止操作
                        _logger.LogDebug("停止执行命令: {NodeName}", node.DisplayName);
                        _manualExecutor.Stop(node.NodeId);
                    }
                    else
                    {
                        // 当前未执行，执行启动操作
                        if (!node.HasDeviceBinding)
                        {
                            MessageBox.Show("请先绑定设备后再执行命令。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                            return;
                        }

                        _logger.LogDebug("开始执行命令: {NodeName}", node.DisplayName);
                        await _manualExecutor.StartAsync(node);
                    }
                }
                catch (Exception ex)
                {
                    string operation = node.IsManualExecuting ? "停止" : "执行";
                    _logger.LogError(ex, "{Operation}命令失败: {NodeName}", operation, node.DisplayName);
                    MessageBox.Show($"{operation}失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }
        private void FlowDesignerView_Loaded(object sender, RoutedEventArgs e)
        {
            // Loaded 可能触发多次（TabControl 切换等），用标记防重
            if (_initialCenterDone) return;

            _logger.LogDebug("FlowDesignerView_Loaded 被调用");
            TrySubscribeToViewModelDirectly();

            // 监听 LayoutUpdated，这是布局真正完成的信号
            if (CanvasScrollViewer != null)
            {
                CanvasScrollViewer.LayoutUpdated -= OnCanvasScrollViewer_LayoutUpdated;
                CanvasScrollViewer.LayoutUpdated += OnCanvasScrollViewer_LayoutUpdated;
            }
        }

        private void OnCanvasScrollViewer_LayoutUpdated(object sender, EventArgs e)
        {
            if (CanvasScrollViewer?.ViewportHeight > 0 && !_initialCenterDone)
            {
                _initialCenterDone = true;
                CanvasScrollViewer.LayoutUpdated -= OnCanvasScrollViewer_LayoutUpdated;

                CenterStartNodeSafe();

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    ScrollToStartNode();
                }), System.Windows.Threading.DispatcherPriority.Render);
            }
        }
        /// <summary>
        /// 页面加载后自动滚动，让 StartNode 出现在视口的垂直中心
        /// </summary>
        private void ScrollToStartNode()
        {
            if (CanvasScrollViewer == null) return;
            var viewModel = DataContext as FlowDesignerViewModel;
            if (viewModel == null) return;

            // StartNode 已经被 CenterStartNodeSafe 放在垂直中心了
            // 滚动到 offset=0 即可（竖向不需要偏移，节点本身就在中心）
            CanvasScrollViewer.ScrollToHorizontalOffset(0);
            CanvasScrollViewer.ScrollToVerticalOffset(0);
        }

        private void FlowDesignerView_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if ((bool)e.NewValue == true)
            {
                _initialCenterDone = false;
                if (CanvasScrollViewer != null)
                {
                    CanvasScrollViewer.LayoutUpdated -= OnCanvasScrollViewer_LayoutUpdated;
                    CanvasScrollViewer.LayoutUpdated += OnCanvasScrollViewer_LayoutUpdated;
                }
            }
        }


        private void FlowDesignerView_Unloaded(object sender, RoutedEventArgs e)
        {
            _sizeChangeDebounceTimer?.Stop();
            _initialCenterDone = false; // 重置，下次加载重新居中

            if (CanvasScrollViewer != null)
                CanvasScrollViewer.LayoutUpdated -= OnCanvasScrollViewer_LayoutUpdated;

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
            _isInternalSizeChange = true;

            // 等布局完全稳定后再滚动（多延迟一帧）
            Dispatcher.BeginInvoke(new Action(() =>
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    RealignNodeVerticalCenter(newNode);

                    // 再等一帧，确保 CanvasWidth 已更新、ScrollableWidth 已刷新
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        ScrollToNodeSmooth(newNode);

                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            _isInternalSizeChange = false;
                        }), DispatcherPriority.ApplicationIdle);

                    }), DispatcherPriority.Render);

                }), DispatcherPriority.Render);

            }), DispatcherPriority.Loaded);
        }

        //  新增方法：用实际渲染高度重新对齐节点垂直中心
        private void RealignNodeVerticalCenter(CommandNode node)
        {
            try
            {
                var viewModel = DataContext as FlowDesignerViewModel;
                if (viewModel == null || node == null) return;

                double startNodeCenterY = viewModel.StartNodeY + 32.0;
                double correctY;

                if (node.IsIfNode && node.NodeControl is IfNodeControlView ifView)
                {
                    correctY = startNodeCenterY - ifView.CardCenterOffsetY;
                }
                else
                {
                    double actualHeight = node.NodeControl?.ActualHeight > 0
                        ? node.NodeControl.ActualHeight
                        : GetNodeActualHeightFromVisualTree(node);
                    if (actualHeight <= 0) return;
                    correctY = startNodeCenterY - actualHeight / 2.0;
                    correctY = Math.Max(0, correctY);
                }

                if (Math.Abs(node.Y - correctY) > 1.0)
                {
                    node.Y = correctY;

                    //  新增：如果 Y 为负，让 ViewModel 整体下移
                    viewModel.EnsureAllNodesVisible();  // 需要把这个方法改为 public

                    viewModel.RebuildAllConnectionsPublic();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "节点Y轴对齐失败: {NodeName}", node?.DisplayName);
            }
        }

        //  新增方法：从视觉树获取节点的实际渲染高度
        private double GetNodeActualHeightFromVisualTree(CommandNode node)
        {
            try
            {
                if (DesignCanvas == null) return 0;

                // 遍历 DesignCanvas 的子元素，找到对应这个节点的 ContentPresenter
                for (int i = 0; i < VisualTreeHelper.GetChildrenCount(DesignCanvas); i++)
                {
                    var child = VisualTreeHelper.GetChild(DesignCanvas, i);
                    if (child is not FrameworkElement fe) continue;

                    // ItemsControl 渲染出来的是 ContentPresenter，DataContext 就是 CommandNode
                    if (fe.DataContext == node && fe.ActualHeight > 0)
                    {
                        _logger.LogDebug("找到节点 {NodeName} 的视觉元素，实际高度: {H}",
                            node.DisplayName, fe.ActualHeight);
                        return fe.ActualHeight;
                    }
                }

                // 如果直接找不到，递归往下找一层（ItemsControl 有嵌套结构）
                return FindNodeHeightInVisualTree(DesignCanvas, node);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "从视觉树获取节点高度失败");
                return 0;
            }
        }

        private double FindNodeHeightInVisualTree(DependencyObject parent, CommandNode targetNode)
        {
            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);

                if (child is FrameworkElement fe && fe.DataContext == targetNode && fe.ActualHeight > 0)
                    return fe.ActualHeight;

                // 只往下找两层，避免性能问题
                if (child != null)
                {
                    int grandCount = VisualTreeHelper.GetChildrenCount(child);
                    for (int j = 0; j < grandCount; j++)
                    {
                        var grandChild = VisualTreeHelper.GetChild(child, j);
                        if (grandChild is FrameworkElement gfe
                            && gfe.DataContext == targetNode
                            && gfe.ActualHeight > 0)
                            return gfe.ActualHeight;
                    }
                }
            }
            return 0;
        }

        #region 滚动方法

        private void ScrollToNodeSmooth(CommandNode node)
        {
            if (CanvasScrollViewer == null || node == null) return;

            try
            {
                double nodeX = node.X;
                double nodeWidth = GetNodeWidth(node);
                double viewportW = CanvasScrollViewer.ViewportWidth;
                double viewportH = CanvasScrollViewer.ViewportHeight;

                //  防御：如果节点坐标或视口尺寸无效，跳过滚动
                if (double.IsNaN(nodeX) || double.IsNaN(node.Y) ||
                    double.IsNaN(nodeWidth) || double.IsInfinity(nodeX) || double.IsInfinity(node.Y) ||
                    viewportW <= 0 || viewportH <= 0)
                {
                    _logger.LogWarning("滚动参数无效，跳过滚动: nodeX={NodeX}, nodeY={NodeY}, nodeWidth={NodeWidth}, viewportW={VW}, viewportH={VH}",
                        nodeX, node.Y, nodeWidth, viewportW, viewportH);
                    return;
                }

                // 水平方向：让节点中心出现在视口中心
                double nodeCenterX = nodeX + nodeWidth / 2.0;
                double targetH = nodeCenterX - viewportW / 2.0;
                targetH = Math.Max(0, targetH); // 不用 ScrollableWidth 限制，画布会自动扩大

                // 垂直方向：节点垂直居中，一般变化不大
                double nodeY = node.Y;
                double nodeHeight = GetNodeHeight(node);
                double nodeCenterY = nodeY + nodeHeight / 2.0;
                double targetV = nodeCenterY - viewportH / 2.0;
                targetV = Math.Max(0, Math.Min(targetV, CanvasScrollViewer.ScrollableHeight));

                //  防御：最终目标值必须有效
                if (double.IsNaN(targetH)) targetH = 0;
                if (double.IsNaN(targetV)) targetV = 0;

                double currentH = CanvasScrollViewer.HorizontalOffset;
                double currentV = CanvasScrollViewer.VerticalOffset;

                if (Math.Abs(currentH - targetH) < 5 && Math.Abs(currentV - targetV) < 5)
                {
                    CanvasScrollViewer.ScrollToHorizontalOffset(targetH);
                    CanvasScrollViewer.ScrollToVerticalOffset(targetV);
                    return;
                }

                StartSmoothScroll(currentH, currentV, targetH, targetV, node.DisplayName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "平滑滚动到节点时发生错误: {NodeName}", node?.DisplayName);
            }
        }
        private void StartSmoothScroll(double startH, double startV, double targetH, double targetV, string nodeName = "")
        {
            //  防御：任一参数为 NaN 或 Infinity 时，直接跳过动画
            if (double.IsNaN(startH) || double.IsNaN(startV) || double.IsNaN(targetH) || double.IsNaN(targetV) ||
                double.IsInfinity(startH) || double.IsInfinity(startV) || double.IsInfinity(targetH) || double.IsInfinity(targetV))
            {
                _logger.LogWarning("平滑滚动参数包含 NaN/Infinity，跳过: startH={SH}, startV={SV}, targetH={TH}, targetV={TV}",
                    startH, startV, targetH, targetV);
                return;
            }

            var startTime = DateTime.UtcNow;
            var duration = TimeSpan.FromMilliseconds(300); // 减少动画时间，让滚动更快

            EventHandler renderingHandler = null;
            renderingHandler = (s, e) =>
            {
                var elapsed = DateTime.UtcNow - startTime;
                var progress = Math.Min(1.0, elapsed.TotalMilliseconds / duration.TotalMilliseconds);

                // 使用更平滑的缓动函数
                var easedProgress = EaseInOutQuart(progress);

                var currentH = startH + (targetH - startH) * easedProgress;
                var currentV = startV + (targetV - startV) * easedProgress;

                //  防御：确保插值结果有效
                if (!double.IsNaN(currentH) && !double.IsNaN(currentV))
                {
                    CanvasScrollViewer.ScrollToHorizontalOffset(currentH);
                    CanvasScrollViewer.ScrollToVerticalOffset(currentV);
                }

                if (progress >= 1.0)
                {
                    CompositionTarget.Rendering -= renderingHandler;
                    _logger.LogDebug("平滑滚动完成: {NodeName}, 最终位置: H={H}, V={V}",
                        nodeName, CanvasScrollViewer.HorizontalOffset, CanvasScrollViewer.VerticalOffset);
                }
            };

            CompositionTarget.Rendering += renderingHandler;
        }
        // 更平滑的缓动函数：四次方缓入缓出
        private double EaseInOutQuart(double t)
        {
            return t < 0.5 ? 8 * t * t * t * t : 1 - 8 * (--t) * t * t * t;
        }
        // 添加一个重载方法，可以指定滚动到特定位置
        private void ScrollToPositionSmooth(double x, double y)
        {
            if (CanvasScrollViewer == null)
            {
                _logger.LogWarning("CanvasScrollViewer 为 null，无法滚动");
                return;
            }

            //  防御：参数无效时跳过
            if (double.IsNaN(x) || double.IsNaN(y) || double.IsInfinity(x) || double.IsInfinity(y))
            {
                _logger.LogWarning("ScrollToPositionSmooth 参数无效，跳过: x={X}, y={Y}", x, y);
                return;
            }

            try
            {
                _logger.LogDebug("开始平滑滚动到位置: ({X}, {Y})", x, y);

                double viewportWidth = CanvasScrollViewer.ViewportWidth;
                double viewportHeight = CanvasScrollViewer.ViewportHeight;

                double targetHorizontalOffset = x - viewportWidth / 2;
                double targetVerticalOffset = y - viewportHeight / 2;

                double maxHorizontalOffset = Math.Max(0, CanvasScrollViewer.ScrollableWidth);
                double maxVerticalOffset = Math.Max(0, CanvasScrollViewer.ScrollableHeight);

                targetHorizontalOffset = Math.Max(0, Math.Min(targetHorizontalOffset, maxHorizontalOffset));
                targetVerticalOffset = Math.Max(0, Math.Min(targetVerticalOffset, maxVerticalOffset));

                double currentH = CanvasScrollViewer.HorizontalOffset;
                double currentV = CanvasScrollViewer.VerticalOffset;

                StartSmoothScroll(currentH, currentV, targetHorizontalOffset, targetVerticalOffset, $"位置({x}, {y})");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "平滑滚动到位置时发生错误: ({X}, {Y})", x, y);
            }
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
        // Add this new helper method near your existing IsClickOnInteractiveElement and IsClickOnLogicNodeInteractiveElement
        private bool IsClickOnDeviceNodeInteractiveElement(MouseButtonEventArgs e)
        {
            var element = e.OriginalSource as FrameworkElement;

            while (element != null)
            {
                // Check for buttons (execute, stop) or the delete border/button
                if (element is Button ||
                    element.Name == "DeleteButton" ||  // Your delete border's name
                    (element is TextBlock tb && tb.Text == "×"))  // The '×' in delete button
                {
                    return true;
                }
                element = element.Parent as FrameworkElement;
            }

            return false;
        }
        // 设备节点选择点击
        private void DeviceNode_SelectClick(object sender, MouseButtonEventArgs e)
        {
            _logger.LogDebug("设备节点选择点击");

            try
            {
                // NEW: Skip if click is on interactive elements like buttons or delete
                if (IsClickOnDeviceNodeInteractiveElement(e))
                {
                    _logger.LogTrace("点击在交互元素上（如按钮或删除），跳过弹出窗口");
                    return;  // Don't trigger popup
                }

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


                    if (IsClickOnLogicNodeInteractiveElement(e))
                    {
                        _logger.LogTrace("点击在逻辑节点的交互元素上，跳过选择");
                        return;
                    }

                    if (IsClickFromNestedNode(e.OriginalSource, node))
                    {
                        _logger.LogTrace("点击来自嵌套节点，跳过处理");
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
            var element = e.OriginalSource as DependencyObject;

            while (element != null)
            {
                if (element is Button ||
                    element is System.Windows.Controls.Primitives.ToggleButton ||
                    (element is FrameworkElement fe &&
                     (fe.Name == "ExpandToggle" ||
                      fe.Name == "DeleteButton" ||
                      fe.Name == "DropZone" ||
                      fe.Name == "TrueBranchDropZone" ||
                      fe.Name == "FalseBranchDropZone")))
                {
                    return true;
                }
                element = VisualTreeHelper.GetParent(element);
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
                            if (tagNode.IsLogicCommand)
                            {
                                HandleNodeSelection(tagNode);
                            }
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
                viewModel.NodeClickCommand?.Execute(node);
                _logger.LogDebug("已选中命令节点: {NodeName}", node.DisplayName);

                // 选中节点时自动展开属性栏
                if (!_isPropertyPanelOpen)
                {
                    _isPropertyPanelOpen = true;
                    UpdatePropertyPanelState();
                }
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

                            // 新增：订阅布局变化，动态修正 node.Y
                            ifControl.LayoutChanged += (s, cardCenterOffsetY) =>
                            {
                                var node = presenter.DataContext as CommandNode;
                                if (node == null) return;
                                var viewModel = DataContext as FlowDesignerViewModel;
                                if (viewModel == null) return;

                                double startNodeCenterY = viewModel.StartNodeY + 32.0;
                                double correctY = startNodeCenterY - cardCenterOffsetY;

                                if (Math.Abs(node.Y - correctY) <= 1.0) return;

                                double deltaY = node.Y - correctY; // 正数=IF1需要上移, 负数=IF1需要下移

                                if (deltaY > 1.0)
                                {
                                    // IF1 需要上移（TRUE分支向上扩展，cardCenterOffsetY 增大）
                                    // 判断 correctY 是否会出界（<0）
                                    if (correctY < 0)
                                    {
                                        // 需要整体下移，让 correctY >= 0
                                        double pushDown = -correctY + 10; // 多留10px余量
                                        viewModel.StartNodeY += pushDown;
                                        foreach (var cmdNode in viewModel.CommandNodes)
                                            cmdNode.Y += pushDown;

                                        // ScrollViewer 补偿，视觉上不跳
                                        double newOffset = (CanvasScrollViewer?.VerticalOffset ?? 0) + pushDown;
                                        Dispatcher.BeginInvoke(new Action(() =>
                                        {
                                            CanvasScrollViewer?.ScrollToVerticalOffset(newOffset);
                                        }), DispatcherPriority.Render);

                                        // 重算 correctY（StartNodeY 已变）
                                        startNodeCenterY = viewModel.StartNodeY + 32.0;
                                        correctY = startNodeCenterY - cardCenterOffsetY;
                                    }
                                    // 只修正 IF1.Y，不动 A/B
                                    node.Y = correctY;
                                }
                                else
                                {
                                    // IF1 需要下移（FALSE分支扩展），直接修正，不影响 A/B
                                    node.Y = correctY;
                                }

                                viewModel.RebuildAllConnectionsPublic();
                            };
                            _logger.LogTrace("为IF节点订阅了点击和布局事件");
                            break;

                        case LoopNodeControlView loopControl:
                            loopControl.NodeClicked += OnComplexNodeClicked;
                            break;
                        case ParallelNodeControlView parallelControl:
                            parallelControl.NodeClicked += OnComplexNodeClicked;
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
            if (DesignCanvas == null || CanvasScrollViewer == null) return;
            var viewModel = DataContext as FlowDesignerViewModel;
            if (viewModel == null) return;

            double viewportHeight = CanvasScrollViewer.ViewportHeight;
            if (viewportHeight <= 0) return;

            const double CANVAS_TITLE_HEIGHT = 35.0;  // Canvas 的 Margin Top
            const double START_NODE_HEIGHT = 64.0;

            //  修复：从视口中心减去 Canvas 的上边距，再减去节点半高
            double targetStartNodeY = viewportHeight / 2.0 - CANVAS_TITLE_HEIGHT - START_NODE_HEIGHT / 2.0;
            targetStartNodeY = Math.Max(10.0, targetStartNodeY);

            if (Math.Abs(viewModel.StartNodeY - targetStartNodeY) < 2.0) return;

            _isInternalSizeChange = true;
            try
            {
                viewModel.StartNodeY = targetStartNodeY;

                if (viewModel.CanvasHeight < viewportHeight + 100)
                    viewModel.CanvasHeight = viewportHeight + 100;

                viewModel.RearrangeNodes();
                viewModel.RebuildAllConnectionsPublic();
            }
            finally
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    _isInternalSizeChange = false;
                }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            }
        }

        //  记录上一次视口尺寸，用于区分"窗口大小变化"和"画布内容变化"
        private Size _lastViewportSize = Size.Empty;

        private void MainGrid_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (_isInternalSizeChange)
            {
                _logger.LogTrace("跳过内部触发的尺寸变化");
                return;
            }

            //  首次居中尚未完成时，SizeChanged 来自初始布局，交给 LayoutUpdated 处理
            if (!_initialCenterDone)
            {
                _logger.LogTrace("首次居中未完成，忽略 SizeChanged: {W}x{H}",
                    e.NewSize.Width, e.NewSize.Height);
                return;
            }

            //  修复：只在视口（ScrollViewer）尺寸真正变化时才重新居中
            //   画布内容增删导致 CanvasWidth/CanvasHeight 改变时，Grid 的 SizeChanged 会触发，
            //   但视口大小不变，此时不应该跳回起始位置
            if (CanvasScrollViewer != null)
            {
                var currentViewport = new Size(CanvasScrollViewer.ViewportWidth, CanvasScrollViewer.ViewportHeight);
                if (_lastViewportSize != Size.Empty)
                {
                    double vpWidthChange = Math.Abs(currentViewport.Width - _lastViewportSize.Width);
                    double vpHeightChange = Math.Abs(currentViewport.Height - _lastViewportSize.Height);

                    if (vpWidthChange < SIZE_CHANGE_THRESHOLD && vpHeightChange < SIZE_CHANGE_THRESHOLD)
                    {
                        // 视口没变，是画布内容变化导致的 SizeChanged，跳过
                        _logger.LogTrace("视口尺寸未变化，跳过居中滚动（画布内容变化）");
                        _lastMainGridSize = e.NewSize;
                        return;
                    }
                }
                _lastViewportSize = currentViewport;
            }

            double widthChange = Math.Abs(e.NewSize.Width - _lastMainGridSize.Width);
            double heightChange = Math.Abs(e.NewSize.Height - _lastMainGridSize.Height);

            if (widthChange < SIZE_CHANGE_THRESHOLD && heightChange < SIZE_CHANGE_THRESHOLD)
            {
                _logger.LogTrace("尺寸变化过小，跳过");
                return;
            }

            _logger.LogDebug("MainGrid 显著尺寸变化: {OldW:F1}x{OldH:F1} -> {NewW:F1}x{NewH:F1}",
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
                CenterStartNodeSafe();
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    ScrollToStartNode();
                }), System.Windows.Threading.DispatcherPriority.Render);
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
                // 清除高亮
                var viewModel = DataContext as FlowDesignerViewModel;
                _dragInsertService.ClearHighlight(viewModel);

                // ★ 用新的 ExtractCommandDragInfo,可以拿到 SourceDeviceType
                var dragInfo = _dragDropService.ExtractCommandDragInfo(e.Data);
                if (dragInfo?.Command != null)
                {
                    var command = dragInfo.Command;          // DeviceCommand 类型
                    var sourceDeviceType = dragInfo.SourceDeviceType;   // 可能为 null(老路径)

                    // 获取插入位置
                    int insertIndex = _currentDragPosition?.IsValidPosition == true ?
                        _currentDragPosition.InsertIndex : -1;

                    // ★ 设备命令:把 sourceDeviceType 也传给 ViewModel
                    if (insertIndex >= 0)
                    {
                        viewModel?.AddCommandToCanvasAtPosition(command, insertIndex, sourceDeviceType);
                    }
                    else
                    {
                        viewModel?.AddCommandToCanvas(command, sourceDeviceType);
                    }

                    _logger.LogDebug("设备命令 {CommandName} 已添加到流程,位置: {Position},来源类型: {Type}",
                        command.Name,
                        insertIndex >= 0 ? insertIndex.ToString() : "末尾",
                        sourceDeviceType ?? "(null)");
                }
                else
                {
                    // ★ 不是设备命令(可能是 LogicCommand),走老的提取路径
                    var command = _dragDropService.ExtractCommandFromDragData(e.Data);
                    if (command != null)
                    {
                        int insertIndex = _currentDragPosition?.IsValidPosition == true ?
                            _currentDragPosition.InsertIndex : -1;

                        if (command is LogicCommand logicCmd)
                        {
                            if (insertIndex >= 0)
                            {
                                viewModel?.AddCommandToCanvasAtPosition(logicCmd, insertIndex);
                            }
                            else
                            {
                                viewModel?.AddCommandToCanvas(logicCmd);
                            }
                            _logger.LogDebug("逻辑命令 {CommandName} 已添加到流程,位置: {Position}",
                                logicCmd.Name, insertIndex >= 0 ? insertIndex.ToString() : "末尾");
                        }
                    }
                    else if (e.Data.GetDataPresent("Device") && e.Data.GetData("Device") is IDevice device)
                    {
                        Point dropPosition = e.GetPosition(DesignCanvas);
                        viewModel?.AddDeviceToCanvas(device, dropPosition);
                    }
                }

                // 重置拖拽位置
                _currentDragPosition = null;
                e.Handled = true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "处理拖放操作时发生错误");
            }
        }

        private void Canvas_DragOver(object sender, DragEventArgs e)
        {
            try
            {
                if (_dragDropService.CanAcceptDrop(e.Data) || e.Data.GetDataPresent("Device"))
                {
                    e.Effects = DragDropEffects.Copy;

                    // 检测插入位置
                    var viewModel = DataContext as FlowDesignerViewModel;
                    if (viewModel != null && _dragDropService.CanAcceptDrop(e.Data))
                    {
                        Point mousePosition = e.GetPosition(DesignCanvas);
                        var dragPosition = _dragInsertService.DetectInsertPosition(mousePosition, DesignCanvas, viewModel);

                        // 更新高亮显示
                        if (dragPosition.IsValidPosition)
                        {
                            if (_currentDragPosition?.HighlightNode != dragPosition.HighlightNode)
                            {
                                _dragInsertService.ClearHighlight(viewModel);
                                if (dragPosition.HighlightNode != null)
                                {
                                    _dragInsertService.SetHighlight(dragPosition.HighlightNode, viewModel);
                                }
                            }
                            _currentDragPosition = dragPosition;
                        }
                        else
                        {
                            _dragInsertService.ClearHighlight(viewModel);
                            _currentDragPosition = null;
                        }
                    }
                }
                else
                {
                    e.Effects = DragDropEffects.None;
                }
                e.Handled = true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "处理拖拽悬停时发生错误");
                e.Effects = DragDropEffects.None;
                e.Handled = true;
            }
        }
        private void Canvas_DragLeave(object sender, DragEventArgs e)
        {
            try
            {
                // 清除高亮显示
                var viewModel = DataContext as FlowDesignerViewModel;
                _dragInsertService.ClearHighlight(viewModel);
                _currentDragPosition = null;
                e.Handled = true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "处理拖拽离开时发生错误");
            }
        }
        #endregion
        private void PropertyPanelRoot_MouseDown(object sender, MouseButtonEventArgs e)
        {
            // 让事件在此处终止，不再冒泡到整体的"清空选中"逻辑
            e.Handled = true;
        }
        private bool _isPropertyPanelOpen = false;
        private void TogglePropertyPanel_Click(object sender, MouseButtonEventArgs e)
        {
            _isPropertyPanelOpen = !_isPropertyPanelOpen;
            UpdatePropertyPanelState();
            e.Handled = true;
        }
        private void UpdatePropertyPanelState()
        {
            if (_isPropertyPanelOpen)
            {
                PropertyPanelRoot.Visibility = Visibility.Visible;
                // 箭头指向右（提示可折叠）
                PanelToggleChevron.Data = Geometry.Parse("M1,1 L5,5 L1,9");
            }
            else
            {
                PropertyPanelRoot.Visibility = Visibility.Collapsed;
                // 箭头指向左（提示可展开）
                PanelToggleChevron.Data = Geometry.Parse("M5,1 L1,5 L5,9");
            }
        }
        private void CanvasHost_MouseDown(object sender, MouseButtonEventArgs e)
        {
            var original = e.OriginalSource as DependencyObject;
            if (IsWithinPropertyPanel(original))
                return;


        }
        private bool IsWithinPropertyPanel(DependencyObject d)
        {
            while (d != null)
            {
                if (d is FrameworkElement fe && fe.Name == "PropertyPanelRoot")
                    return true;
                d = VisualTreeHelper.GetParent(d);
            }
            return false;
        }
       
        #region 缩放功能

        private double _currentZoom = 1.0;
        private const double ZoomMin = 0.25;
        private const double ZoomMax = 2.0;
        private const double ZoomStep = 0.1;

        // 预定义缩放级别，用于 Ctrl+滚轮时步进到最近的级别
        private static readonly double[] ZoomLevels = { 0.25, 0.33, 0.5, 0.67, 0.75, 0.8, 0.9, 1.0, 1.1, 1.25, 1.5, 1.75, 2.0 };

        /// <summary>
        /// 在构造函数或 Loaded 事件中调用此方法来初始化缩放
        /// </summary>
        private void InitializeZoom()
        {
            // Ctrl+滚轮缩放
            CanvasScrollViewer.PreviewMouseWheel += OnCanvasPreviewMouseWheel;

            // 键盘快捷键
            this.PreviewKeyDown += OnZoomKeyDown;
        }

        /// <summary>
        /// Ctrl+滚轮缩放
        /// </summary>
        private void OnCanvasPreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (Keyboard.Modifiers != ModifierKeys.Control)
                return;

            e.Handled = true;

            // 获取鼠标在 ScrollViewer 中的位置（用于以鼠标为中心缩放）
            var mousePos = e.GetPosition(CanvasScrollViewer);
            var viewportCenterX = mousePos.X / CanvasScrollViewer.ViewportWidth;
            var viewportCenterY = mousePos.Y / CanvasScrollViewer.ViewportHeight;

            if (e.Delta > 0)
                ZoomToNextLevel(true);
            else
                ZoomToNextLevel(false);

            // 缩放后调整滚动位置，让鼠标指向的内容保持在鼠标下方
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    var newScrollX = viewportCenterX * CanvasScrollViewer.ScrollableWidth;
                    var newScrollY = viewportCenterY * CanvasScrollViewer.ScrollableHeight;
                    CanvasScrollViewer.ScrollToHorizontalOffset(newScrollX);
                    CanvasScrollViewer.ScrollToVerticalOffset(newScrollY);
                }
                catch { }
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        /// <summary>
        /// 键盘快捷键：Ctrl+0 自适应, Ctrl+1 还原100%, Ctrl+= 放大, Ctrl+- 缩小
        /// </summary>
        private void OnZoomKeyDown(object sender, KeyEventArgs e)
        {
            if (Keyboard.Modifiers != ModifierKeys.Control)
                return;

            switch (e.Key)
            {
                case Key.D0:
                case Key.NumPad0:
                    ZoomFit_Click(null, null);
                    e.Handled = true;
                    break;
                case Key.D1:
                case Key.NumPad1:
                    ZoomReset_Click(null, null);
                    e.Handled = true;
                    break;
                case Key.OemPlus:
                case Key.Add:
                    ZoomIn_Click(null, null);
                    e.Handled = true;
                    break;
                case Key.OemMinus:
                case Key.Subtract:
                    ZoomOut_Click(null, null);
                    e.Handled = true;
                    break;
            }
        }

        /// <summary>
        /// 步进到下一个缩放级别
        /// </summary>
        private void ZoomToNextLevel(bool zoomIn)
        {
            double target = _currentZoom;

            if (zoomIn)
            {
                // 找到比当前大的最近级别
                foreach (var level in ZoomLevels)
                {
                    if (level > _currentZoom + 0.01)
                    {
                        target = level;
                        break;
                    }
                }
            }
            else
            {
                // 找到比当前小的最近级别
                for (int i = ZoomLevels.Length - 1; i >= 0; i--)
                {
                    if (ZoomLevels[i] < _currentZoom - 0.01)
                    {
                        target = ZoomLevels[i];
                        break;
                    }
                }
            }

            ApplyZoom(target);
        }

        /// <summary>
        /// 应用缩放
        /// </summary>
        private void ApplyZoom(double zoom)
        {
            zoom = Math.Max(ZoomMin, Math.Min(zoom, ZoomMax));
            _currentZoom = zoom;

            if (CanvasScaleTransform != null)
            {
                CanvasScaleTransform.ScaleX = zoom;
                CanvasScaleTransform.ScaleY = zoom;
            }

            // 更新百分比显示
            if (ZoomLevelText != null)
            {
                ZoomLevelText.Text = $"{(int)Math.Round(zoom * 100)}%";
            }
        }

        /// <summary>
        /// 放大按钮
        /// </summary>
        private void ZoomIn_Click(object sender, RoutedEventArgs e)
        {
            ZoomToNextLevel(true);
        }

        /// <summary>
        /// 缩小按钮
        /// </summary>
        private void ZoomOut_Click(object sender, RoutedEventArgs e)
        {
            ZoomToNextLevel(false);
        }

        /// <summary>
        /// 还原 100%
        /// </summary>
        private void ZoomReset_Click(object sender, RoutedEventArgs e)
        {
            ApplyZoom(1.0);

            // 滚动到起始位置
            CanvasScrollViewer?.ScrollToHorizontalOffset(0);
            CanvasScrollViewer?.ScrollToVerticalOffset(0);
        }

        /// <summary>
        /// 自适应画布：缩放到让所有节点都可见
        /// </summary>
        private void ZoomFit_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var viewModel = DataContext as FlowDesignerViewModel;
                if (viewModel == null) return;

                // 先还原到 1:1 来计算真实边界
                ApplyZoom(1.0);
                UpdateLayout();

                // 计算所有节点的边界矩形
                double minX = double.MaxValue, minY = double.MaxValue;
                double maxX = 0, maxY = 0;

                // 包含启动节点
                double startX = viewModel.StartNodeX;
                double startY = viewModel.StartNodeY;
                minX = Math.Min(minX, startX);
                minY = Math.Min(minY, startY);
                maxX = Math.Max(maxX, startX + 231);
                maxY = Math.Max(maxY, startY + 64);

                // 遍历所有命令节点
                if (viewModel.CommandNodes != null)
                {
                    foreach (var node in viewModel.CommandNodes)
                    {
                        if (node.NodeControl != null)
                        {
                            var pos = node.NodeControl.TranslatePoint(new System.Windows.Point(0, 0), DesignCanvas);
                            var w = node.NodeControl.ActualWidth > 0 ? node.NodeControl.ActualWidth : 231;
                            var h = node.NodeControl.ActualHeight > 0 ? node.NodeControl.ActualHeight : 66;

                            minX = Math.Min(minX, pos.X);
                            minY = Math.Min(minY, pos.Y);
                            maxX = Math.Max(maxX, pos.X + w);
                            maxY = Math.Max(maxY, pos.Y + h);
                        }
                    }
                }

                if (minX == double.MaxValue) return; // 没有节点

                double contentW = maxX - minX + 80; // 加 padding
                double contentH = maxY - minY + 80;

                double viewportW = CanvasScrollViewer.ViewportWidth;
                double viewportH = CanvasScrollViewer.ViewportHeight;

                if (viewportW <= 0 || viewportH <= 0) return;

                // 计算缩放比例
                double scaleX = viewportW / contentW;
                double scaleY = viewportH / contentH;
                double scale = Math.Min(scaleX, scaleY);
                scale = Math.Max(ZoomMin, Math.Min(scale, 1.0)); // 最大不超过 100%

                ApplyZoom(scale);

                // 滚动到内容中心
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        double centerX = (minX + maxX) / 2 * scale;
                        double centerY = (minY + maxY) / 2 * scale;

                        double scrollX = Math.Max(0, centerX - viewportW / 2);
                        double scrollY = Math.Max(0, centerY - viewportH / 2);

                        CanvasScrollViewer.ScrollToHorizontalOffset(scrollX);
                        CanvasScrollViewer.ScrollToVerticalOffset(scrollY);
                    }
                    catch { }
                }), System.Windows.Threading.DispatcherPriority.Background);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "自适应画布失败");
            }
        }

        #endregion

        /// <summary>
        /// 同步时间轴列头和数据行的横向滚动位置。
        /// 在 XAML 中为 SwimlaneDataScroll 绑定 ScrollChanged 事件：
        ///   ScrollChanged="SwimlaneDataScroll_ScrollChanged"
        /// </summary>
        private void SwimlaneDataScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            // 只处理水平滚动变化
            if (Math.Abs(e.HorizontalChange) < 0.01) return;

            if (sender is ScrollViewer dataScroll && TimelineHeaderScroll != null)
            {
                TimelineHeaderScroll.ScrollToHorizontalOffset(dataScroll.HorizontalOffset);
            }
        }
    }
}
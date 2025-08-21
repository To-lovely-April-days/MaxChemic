using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using MaxChemical.Modules.Designer.Models;
using DevicePlugins.Devices;
using MaxChemical.DataModule.Model;
using MaxChemical.Modules.Designer.ViewModels;
using System.Windows.Data;
using MaxChemical.Logging;
using MaxChemical.Modules.Designer.Events;
using MaxChemical.Modules.Designer.Services;
using CommandDragInfo = MaxChemical.Modules.Designer.Events.CommandDragInfo;
using System.Windows.Threading;

namespace MaxChemical.Modules.Designer.Views
{
    /// <summary>
    /// LoopNodeControlView.xaml 的交互逻辑
    /// </summary>
    public partial class LoopNodeControlView : UserControl
    {
        public event EventHandler ContentChanged;
        public event EventHandler<NestedCommandEventArgs> NestedCommandAdded;
        public event EventHandler<NestedCommandEventArgs> NestedCommandRemoved;
        public event EventHandler<LogicCommandRequestEventArgs> LogicCommandRequested;
        public event EventHandler<CommandNode> NodeDeleteRequested;
        public event EventHandler<CommandNode> NodeClicked;

        private bool _isExpanded = true;
        private Size _lastMeasuredSize;

        // 高亮相关字段
        private int _currentInsertionIndex = -1;
        private CommandNode _highlightedNode;
        private readonly List<CommandNode> _loopCommands = new List<CommandNode>();

        private readonly ILogService _logger;
        private readonly NodeControlFactory _nodeControlFactory;
        private readonly DesignerDragDropService _dragDropService;
        private readonly NodeManagementService _nodeManagementService;

        public LoopNodeControlView()
        {
            InitializeComponent();

            // 初始化服务
            _logger = new LogService().ForContext<LoopNodeControlView>();
            _nodeControlFactory = new NodeControlFactory(_logger);
            _dragDropService = new DesignerDragDropService(_logger);
            _nodeManagementService = new NodeManagementService(_logger);

            // 订阅事件
            Loaded += OnLoaded;
            SizeChanged += OnSizeChanged;
            DataContextChanged += OnDataContextChanged;
            IsVisibleChanged += OnIsVisibleChanged;
        }
        private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            try
            {
                if ((bool)e.NewValue == true) // 变为可见
                {
                    _logger.LogDebug("并行节点控件变为可见，延迟同步UI");

                    // 延迟同步UI，确保布局完成
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (DataContext is CommandNode loopNode && loopNode.Children.Count > 0)
                        {
                            _logger.LogDebug($"可见性变化触发UI同步: {loopNode.DisplayName}，子节点数: {loopNode.Children.Count}");
                            OnLoopContentChanged();
                            ToggleExpand();

                        }
                    }), DispatcherPriority.Background);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "处理可见性变化失败");
            }
        }
        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            UpdateLoopCanvasLayout();
        }

        private void OnSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (e.NewSize != _lastMeasuredSize)
            {
                _lastMeasuredSize = e.NewSize;
                UpdateLoopCanvasLayout();
            }
        }

        private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (e.NewValue is CommandNode loopNode)
            {
                // 同步Children到UI
                UpdateChildrenDisplay();
            }
        }

        // 展开/折叠按钮点击事件
        private void ExpandToggle_Click(object sender, RoutedEventArgs e)
        {
            ToggleExpand();
            e.Handled = true;
        }

        // 节点点击事件
        private void Node_Click(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed && DataContext is CommandNode node)
            {
                _logger.LogDebug("LoopNodeControlView.Node_Click: {NodeName}", node.DisplayName);
                HandleLoopNodeClick(node);
                var routedEventArgs = new RoutedEventArgs(NodeClickEvent, node);
                RaiseEvent(routedEventArgs);
                e.Handled = true;
            }
        }

        /// <summary>
        /// 处理循环节点点击
        /// </summary>
        private void HandleLoopNodeClick(CommandNode loopNode)
        {
            try
            {
                _logger.LogDebug("处理循环节点点击: {NodeName}", loopNode.DisplayName);

                var flowViewModel = _nodeManagementService.GetFlowDesignerViewModel(this);
                _nodeManagementService.ClearAllSelections(flowViewModel);

                loopNode.IsSelected = true;
                NodeClicked?.Invoke(this, loopNode);

                _logger.LogDebug("循环节点 {NodeName} 已被选中", loopNode.DisplayName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "处理循环节点点击失败: {NodeName}", loopNode?.DisplayName);
            }
        }

        /// <summary>
        /// 处理嵌套节点点击
        /// </summary>
        private void HandleNestedNodeClick(CommandNode nestedNode)
        {
            try
            {
                _logger.LogDebug("处理循环内嵌套节点点击: {NodeName}", nestedNode.DisplayName);

                var flowViewModel = _nodeManagementService.GetFlowDesignerViewModel(this);
                _nodeManagementService.ClearAllSelections(flowViewModel);

                nestedNode.IsSelected = true;

                if (flowViewModel != null)
                {
                    flowViewModel.HandleNodeClick(nestedNode);
                    _logger.LogDebug("已通知FlowDesignerViewModel处理嵌套节点点击");
                }

                NodeClicked?.Invoke(this, nestedNode);
                _logger.LogDebug("嵌套节点 {NodeName} 处理完成", nestedNode.DisplayName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "处理嵌套节点点击失败: {NodeName}", nestedNode?.DisplayName);
            }
        }

        private T FindVisualParent<T>(DependencyObject child) where T : DependencyObject
        {
            DependencyObject parentObject = VisualTreeHelper.GetParent(child);

            if (parentObject == null) return null;

            if (parentObject is T parent)
                return parent;

            return FindVisualParent<T>(parentObject);
        }

        // 展开/折叠逻辑
        public void ToggleExpand()
        {
            _isExpanded = !_isExpanded;
            var duration = TimeSpan.FromMilliseconds(200);

            if (_isExpanded)
            {
                ChildrenContainer.Visibility = Visibility.Visible;
                ExpandToggle.ToolTip = "折叠循环";
                var rotateAnimation = new DoubleAnimation(0, duration);
                ExpandIconTransform.BeginAnimation(RotateTransform.AngleProperty, rotateAnimation);
            }
            else
            {
                ChildrenContainer.Visibility = Visibility.Collapsed;
                ExpandToggle.ToolTip = "展开循环";
                var rotateAnimation = new DoubleAnimation(-90, duration);
                ExpandIconTransform.BeginAnimation(RotateTransform.AngleProperty, rotateAnimation);
            }

            Dispatcher.BeginInvoke(new Action(() =>
            {
                UpdateLoopCanvasLayout();
                ContentChanged?.Invoke(this, EventArgs.Empty);
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        /// <summary>
        /// 删除按钮点击事件
        /// </summary>
        private void DeleteButton_Click(object sender, MouseButtonEventArgs e)
        {
            try
            {
                if (DataContext is CommandNode loopNode)
                {
                    _logger.LogDebug("请求删除循环节点: {NodeName}", loopNode.DisplayName);
                    NodeDeleteRequested?.Invoke(this, loopNode);
                    e.Handled = true;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "删除循环节点失败");
            }
        }

        #region 布局更新方法 - 核心新增部分

        /// <summary>
        /// 更新循环Canvas布局 - 类似并行节点的UpdateBranchCanvasLayout
        /// </summary>
        public void UpdateLoopCanvasLayout()
        {
            try
            {
                _logger.LogDebug("开始更新循环Canvas布局");

                // 强制更新内容布局
                LoopChildrenContent.UpdateLayout();
                LoopContentWrapper.UpdateLayout();

                // 重新计算并设置Canvas和容器大小
                CalculateAndUpdateCanvasSize();

                _logger.LogDebug("循环Canvas布局更新完成");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "更新循环Canvas布局失败");
            }
        }

        /// <summary>
        /// 计算并更新Canvas大小 - 核心方法
        /// </summary>
        private void CalculateAndUpdateCanvasSize()
        {
            try
            {
                // 测量内容容器的理想大小
                LoopContentWrapper.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                var contentSize = LoopContentWrapper.DesiredSize;

                _logger.LogDebug("内容容器理想大小: {Width}x{Height}", contentSize.Width, contentSize.Height);

                // 计算Canvas的新尺寸
                var canvasWidth = Math.Max(500, contentSize.Width + 100);  // 最小500，内容宽度+边距
                var canvasHeight = Math.Max(100, contentSize.Height + 20); // 最小100，内容高度+边距

                // 更新Canvas尺寸
                LoopCanvas.Width = canvasWidth;
                LoopCanvas.Height = canvasHeight;

                // 重新计算内容容器在Canvas中的位置（居中）
                var wrapperLeft = (canvasWidth - contentSize.Width) / 2;
                Canvas.SetLeft(LoopContentWrapper, wrapperLeft);

                _logger.LogDebug("Canvas尺寸更新为: {Width}x{Height}, 内容位置: Left={Left}",
                    canvasWidth, canvasHeight, wrapperLeft);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "计算Canvas大小失败");
            }
        }

        /// <summary>
        /// 内容变化通知方法 - 类似并行节点的OnBranchContentChanged
        /// </summary>
        public void OnLoopContentChanged()
        {
            try
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    UpdateLoopCanvasLayout();
                    ContentChanged?.Invoke(this, EventArgs.Empty);
                    _logger.LogDebug("循环内容变化通知完成");
                }), System.Windows.Threading.DispatcherPriority.Background);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "循环内容变化通知失败");
            }
        }

        #endregion

        #region 拖拽事件处理

        private void DropArea_DragEnter(object sender, DragEventArgs e)
        {
            _logger.LogTrace("LoopNode DropArea_DragEnter 被调用");

            if (_dragDropService.CanAcceptDrop(e.Data) && sender is Border border)
            {
                _dragDropService.SetDragOverStyle(border);
                e.Effects = DragDropEffects.Copy;
            }
            else
            {
                e.Effects = DragDropEffects.None;
            }
            e.Handled = true;
        }

        private void DropArea_DragLeave(object sender, DragEventArgs e)
        {
            if (sender is Border border)
            {
                _dragDropService.ClearDragOverStyle(border);
            }
            ClearHighlight();
            e.Handled = true;
        }

        private void DropArea_DragOver(object sender, DragEventArgs e)
        {
            if (_dragDropService.CanAcceptDrop(e.Data))
            {
                e.Effects = DragDropEffects.Copy;

                // 获取相对于LoopChildrenContent的鼠标位置
                var mousePosition = e.GetPosition(LoopChildrenContent);
                var insertionInfo = GetInsertionInfo(mousePosition);
                HighlightPreviousNode(insertionInfo);

                _currentInsertionIndex = insertionInfo.InsertionIndex;
            }
            else
            {
                e.Effects = DragDropEffects.None;
            }
            e.Handled = true;
        }

        private void DropArea_Drop(object sender, DragEventArgs e)
        {
            _logger.LogDebug("LoopNode DropArea_Drop 被调用");

            try
            {
                // 记录当前的插入索引，然后清除高亮
                int insertionIndex = _currentInsertionIndex;
                ClearHighlight();

                if (DataContext is CommandNode loopNode)
                {
                    var command = _dragDropService.ExtractCommandFromDragData(e.Data);

                    if (command is Services.PluginIdRequest pluginRequest)
                    {
                        var args = new LogicCommandRequestEventArgs { PluginId = pluginRequest.PluginId };
                        LogicCommandRequested?.Invoke(this, args);
                        command = args.ResultCommand;
                    }

                    if (command != null)
                    {
                        _logger.LogDebug("提取到命令: {Type}, 插入位置: {Index}", command.GetType().Name, insertionIndex);
                        AddCommandToLoop(command, loopNode, insertionIndex);
                    }
                    else
                    {
                        _logger.LogWarning("未能提取到有效命令");
                    }
                }

                if (sender is Border border)
                {
                    _dragDropService.ClearDragOverStyle(border);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "LoopNode 处理拖放失败");
            }
            finally
            {
                ClearHighlight();
            }

            e.Handled = true;
        }

        #endregion

        #region 高亮逻辑

        private class InsertionInfo
        {
            public int InsertionIndex { get; set; }
            public CommandNode PreviousNode { get; set; }
            public bool IsInsertAtBeginning { get; set; }
        }

        private InsertionInfo GetInsertionInfo(Point mousePosition)
        {
            try
            {
                var info = new InsertionInfo();

                // 如果循环为空，插入位置为0，没有前一个节点
                if (_loopCommands.Count == 0)
                {
                    info.InsertionIndex = 0;
                    info.IsInsertAtBeginning = true;
                    return info;
                }

                double cumulativeHeight = 0;

                for (int i = 0; i < _loopCommands.Count; i++)
                {
                    var node = _loopCommands[i];
                    var control = FindControlForNode(node);

                    if (control != null)
                    {
                        // 获取控件的实际高度
                        double controlHeight = control.ActualHeight;
                        if (controlHeight == 0)
                        {
                            control.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                            controlHeight = control.DesiredSize.Height;
                        }
                        if (controlHeight == 0) controlHeight = 40; // 默认高度

                        // 检查鼠标是否在当前控件的上半部分
                        if (mousePosition.Y <= cumulativeHeight + controlHeight / 2)
                        {
                            // 插入到当前节点之前
                            info.InsertionIndex = i;
                            if (i == 0)
                            {
                                info.IsInsertAtBeginning = true;
                            }
                            else
                            {
                                info.PreviousNode = _loopCommands[i - 1];
                            }

                            _logger.LogTrace("鼠标位置 {Y} 在节点 {Index} 上半部分，插入位置: {InsertIndex}",
                                mousePosition.Y, i, i);
                            return info;
                        }

                        cumulativeHeight += controlHeight + 8; // 8是margin
                    }
                }

                // 如果鼠标在所有元素下方，插入到末尾
                info.InsertionIndex = _loopCommands.Count;
                info.PreviousNode = _loopCommands[_loopCommands.Count - 1];

                _logger.LogTrace("鼠标位置 {Y} 在所有节点下方，插入位置: {InsertIndex}",
                    mousePosition.Y, _loopCommands.Count);
                return info;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "计算插入信息失败");
                return new InsertionInfo { InsertionIndex = 0, IsInsertAtBeginning = true };
            }
        }

        private UserControl FindControlForNode(CommandNode node)
        {
            foreach (UIElement child in LoopChildrenContent.Children)
            {
                if (child is UserControl control)
                {
                    if (control.Tag == node || (control.DataContext is CommandNode contextNode && contextNode == node))
                    {
                        return control;
                    }
                }
            }
            return null;
        }

        private void HighlightPreviousNode(InsertionInfo insertionInfo)
        {
            try
            {
                // 先清除之前的高亮
                ClearHighlight();

                if (insertionInfo.IsInsertAtBeginning)
                {
                    // 插入到开头，不高亮任何节点
                    _logger.LogTrace("插入到循环开头，无需高亮节点");
                    return;
                }

                if (insertionInfo.PreviousNode != null)
                {
                    // 高亮前一个节点 - 只设置数据绑定属性
                    _highlightedNode = insertionInfo.PreviousNode;
                    _highlightedNode.IsInsertHighlight = true;

                    _logger.LogTrace("高亮前一个节点: {NodeName}", _highlightedNode.DisplayName);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "设置节点高亮失败");
            }
        }

        private void ClearHighlight()
        {
            try
            {
                if (_highlightedNode != null)
                {
                    _highlightedNode.IsInsertHighlight = false;
                    _logger.LogDebug("清除节点 {NodeName} 的高亮状态", _highlightedNode.DisplayName);
                    _highlightedNode = null;
                }

                _currentInsertionIndex = -1;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "清除节点高亮失败");
            }
        }

        #endregion

        #region 命令管理

        private void AddCommandToLoop(object command, CommandNode loopNode, int insertionIndex = -1)
        {
            try
            {
                // 立即清除高亮
                ClearHighlight();

                CommandNode childNode = null;

                if (command is DeviceCommand deviceCmd)
                {
                    childNode = new CommandNode(deviceCmd);
                }
                else if (command is LogicCommand logicCmd)
                {
                    childNode = new CommandNode(logicCmd);
                }

                if (childNode != null)
                {
                    // 如果指定了插入索引，则在指定位置插入
                    if (insertionIndex >= 0 && insertionIndex <= _loopCommands.Count)
                    {
                        _loopCommands.Insert(insertionIndex, childNode);
                        loopNode.InsertChild(insertionIndex, childNode);

                        _logger.LogDebug("在循环位置 {Index} 插入命令: {NodeName}", insertionIndex, childNode.DisplayName);
                    }
                    else
                    {
                        // 添加到末尾
                        _loopCommands.Add(childNode);
                        loopNode.AddChildToLoop(childNode);

                        _logger.LogDebug("添加命令到循环末尾: {NodeName}", childNode.DisplayName);
                    }

                    // 订阅嵌套复杂节点的内容变化事件
                    SubscribeToNestedComplexNodeEvents(childNode);

                    UpdateChildrenDisplay();

                    // 重要：延迟触发布局更新和事件，确保UI已更新
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        // 再次确保高亮被清除
                        ClearHighlight();

                        // 关键：触发布局更新
                        OnLoopContentChanged();

                        NestedCommandAdded?.Invoke(this, new NestedCommandEventArgs
                        {
                            NestedCommand = childNode,
                            IsTrueBranch = false,
                            ParentIfNode = loopNode
                        });

                    }), System.Windows.Threading.DispatcherPriority.Loaded);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "添加命令到循环失败");
            }
            finally
            {
                // 最终保险
                ClearHighlight();
            }
        }

        /// <summary>
        /// 订阅嵌套复杂节点的内容变化事件
        /// </summary>
        private void SubscribeToNestedComplexNodeEvents(CommandNode nestedNode)
        {
            try
            {
                if (nestedNode.NodeControl == null) return;

                _logger.LogDebug("订阅嵌套节点事件: {NodeType} - {NodeName}",
                    nestedNode.NodeControl.GetType().Name, nestedNode.DisplayName);

                switch (nestedNode.NodeControl)
                {
                    case ParallelNodeControlView nestedParallelControl:
                        nestedParallelControl.ContentChanged += (s, e) =>
                        {
                            _logger.LogDebug("循环内嵌套并行节点内容变化: {NodeName}", nestedNode.DisplayName);
                            OnLoopContentChanged();
                        };
                        break;

                    case IfNodeControlView nestedIfControl:
                        nestedIfControl.ContentChanged += (s, e) =>
                        {
                            _logger.LogDebug("循环内嵌套IF节点内容变化: {NodeName}", nestedNode.DisplayName);
                            OnLoopContentChanged();
                        };
                        break;

                    case LoopNodeControlView nestedLoopControl:
                        nestedLoopControl.ContentChanged += (s, e) =>
                        {
                            _logger.LogDebug("循环内嵌套循环节点内容变化: {NodeName}", nestedNode.DisplayName);
                            OnLoopContentChanged();
                        };
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "订阅嵌套复杂节点事件失败: {NodeName}", nestedNode?.DisplayName);
            }
        }

        private void UpdateChildrenDisplay()
        {
            try
            {
                if (DataContext is not CommandNode loopNode)
                    return;

                LoopChildrenContent.Children.Clear();
                _loopCommands.Clear();

                // 同步数据模型到本地列表
                foreach (var child in loopNode.Children)
                {
                    _loopCommands.Add(child);
                }

                bool hasChildren = _loopCommands.Count > 0;
                EmptyHint.Text = hasChildren ? "拖拽更多步骤" : "+ 添加步骤";

                foreach (var child in _loopCommands)
                {
                    var childControl = _nodeControlFactory.CreateNestedCommandControl(
                        child,
                        HandleNestedNodeClick,
                        RemoveLoopCommand);
                    LoopChildrenContent.Children.Add(childControl);

                    // 订阅嵌套节点事件
                    SubscribeToNestedComplexNodeEvents(child);
                }

                _logger.LogDebug("循环节点UI更新完成，子项数: {Count}", _loopCommands.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "更新子命令显示失败");
            }
        }

        public void RemoveLoopCommand(CommandNode childNode)
        {
            try
            {
                if (DataContext is CommandNode loopNode && loopNode.RemoveChild(childNode))
                {
                    _loopCommands.Remove(childNode);
                    _logger.LogDebug("从循环移除命令: {NodeName}", childNode.DisplayName);

                    UpdateChildrenDisplay();

                    // 触发布局更新
                    OnLoopContentChanged();

                    NestedCommandRemoved?.Invoke(this, new NestedCommandEventArgs
                    {
                        NestedCommand = childNode,
                        IsTrueBranch = false,
                        ParentIfNode = loopNode
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "移除循环命令失败: {NodeName}", childNode?.DisplayName);
            }
        }

        public void ClearLoopCommands()
        {
            try
            {
                if (DataContext is CommandNode loopNode)
                {
                    loopNode.ClearChildren();
                    _loopCommands.Clear();
                    UpdateChildrenDisplay();
                    OnLoopContentChanged();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "清空循环命令失败");
            }
        }

        public IReadOnlyList<CommandNode> GetLoopCommands()
        {
            return _loopCommands.AsReadOnly();
        }

        #endregion

        // 节点点击事件
        public static readonly RoutedEvent NodeClickEvent = EventManager.RegisterRoutedEvent(
            "NodeClick", RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(LoopNodeControlView));

        public event RoutedEventHandler NodeClick
        {
            add { AddHandler(NodeClickEvent, value); }
            remove { RemoveHandler(NodeClickEvent, value); }
        }

        // 显示命令选择器事件
        public static readonly RoutedEvent ShowCommandSelectorEvent = EventManager.RegisterRoutedEvent(
            "ShowCommandSelector", RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(LoopNodeControlView));

        public event RoutedEventHandler ShowCommandSelector
        {
            add { AddHandler(ShowCommandSelectorEvent, value); }
            remove { RemoveHandler(ShowCommandSelectorEvent, value); }
        }
    }
}
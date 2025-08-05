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

            DataContextChanged += OnDataContextChanged;
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
            e.Handled = true; // 防止事件冒泡
        }

        // 节点点击事件 - 只处理文字区域的点击
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


        /// <summary>
        /// 清除所有节点的选中状态
        /// </summary>
        private void ClearAllNodeSelections()
        {
            try
            {
                var flowDesignerView = FindVisualParent<FlowDesignerView>(this);
                if (flowDesignerView?.DataContext is FlowDesignerViewModel flowViewModel)
                {
                    foreach (var node in flowViewModel.CommandNodes)
                    {
                        node.IsSelected = false;
                        ClearNodeChildrenSelection(node);
                    }
                }

                if (DataContext is CommandNode loopNode)
                {
                    loopNode.IsSelected = false;
                }

                System.Diagnostics.Debug.WriteLine("已清除所有节点的选中状态");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"清除节点选中状态失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 递归清除节点子节点的选中状态
        /// </summary>
        private void ClearNodeChildrenSelection(CommandNode node)
        {
            if (node?.HasChildren == true)
            {
                foreach (var child in node.Children)
                {
                    child.IsSelected = false;
                    ClearNodeChildrenSelection(child);
                }
            }
        }

        /// <summary>
        /// 查找视觉树中的父级元素
        /// </summary>
        private T FindVisualParent<T>(DependencyObject child) where T : DependencyObject
        {
            DependencyObject parentObject = VisualTreeHelper.GetParent(child);

            if (parentObject == null) return null;

            if (parentObject is T parent)
                return parent;

            return FindVisualParent<T>(parentObject);
        }
        // 展开/折叠逻辑
        private void ToggleExpand()
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
            e.Handled = true;
        }

        private void DropArea_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = _dragDropService.CanAcceptDrop(e.Data) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void DropArea_Drop(object sender, DragEventArgs e)
        {
            _logger.LogDebug("LoopNode DropArea_Drop 被调用");

            try
            {
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
                        _logger.LogDebug("提取到命令: {Type}", command.GetType().Name);
                        AddCommandToLoop(command, loopNode);
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

            e.Handled = true;
        }

        #endregion


        #region 命令管理

        private void AddCommandToLoop(object command, CommandNode loopNode)
        {
            try
            {
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
                    loopNode.AddChildToLoop(childNode);

                    _logger.LogDebug("添加命令到循环: {NodeName}", childNode.DisplayName);

                    UpdateChildrenDisplay();
                    // 重要：延迟触发内容变化事件，确保UI已更新
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        ContentChanged?.Invoke(this, EventArgs.Empty);

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
        }

        private void UpdateChildrenDisplay()
        {
            try
            {
                if (DataContext is not CommandNode loopNode)
                    return;

                LoopChildrenContent.Children.Clear();

                bool hasChildren = loopNode.Children.Count > 0;
                EmptyHint.Text = hasChildren ? "拖拽更多步骤" : "+ 添加步骤";

                foreach (var child in loopNode.Children)
                {
                    var childControl = _nodeControlFactory.CreateNestedCommandControl(
                        child,
                        HandleNestedNodeClick,
                        RemoveLoopCommand);
                    LoopChildrenContent.Children.Add(childControl);
                }

                // 强制更新布局
                this.UpdateLayout();

                _logger.LogDebug("循环节点UI更新完成，子项数: {Count}", loopNode.Children.Count);
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
                    _logger.LogDebug("从循环移除命令: {NodeName}", childNode.DisplayName);

                    UpdateChildrenDisplay();

                    NestedCommandRemoved?.Invoke(this, new NestedCommandEventArgs
                    {
                        NestedCommand = childNode,
                        IsTrueBranch = false,
                        ParentIfNode = loopNode
                    });

                    ContentChanged?.Invoke(this, EventArgs.Empty);
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
                    UpdateChildrenDisplay();
                    ContentChanged?.Invoke(this, EventArgs.Empty);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "清空循环命令失败");
            }
        }

        public IReadOnlyList<CommandNode> GetLoopCommands()
        {
            if (DataContext is CommandNode loopNode)
            {
                return loopNode.Children.ToList().AsReadOnly();
            }
            return new List<CommandNode>().AsReadOnly();
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
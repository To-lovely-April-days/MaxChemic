using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MaxChemical.Modules.Designer.Models;
using MaxChemical.Modules.Designer.Services;
using MaxChemical.Modules.Designer.Events;
using MaxChemical.Modules.Designer.ViewModels;
using MaxChemical.Logging;
using DevicePlugins.Devices;
using System.Windows.Threading;

namespace MaxChemical.Modules.Designer.Views
{
    public partial class IfNodeControlView : UserControl
    {
        public event EventHandler ContentChanged;
        public event EventHandler<NestedCommandEventArgs> NestedCommandAdded;
        public event EventHandler<NestedCommandEventArgs> NestedCommandRemoved;
        public event EventHandler<LogicCommandRequestEventArgs> LogicCommandRequested;
        public event EventHandler<CommandNode> NodeDeleteRequested;
        public event EventHandler<CommandNode> NodeClicked;

        private Size _lastMeasuredSize;
        private readonly List<CommandNode> _trueBranchCommands = new List<CommandNode>();
        private readonly List<CommandNode> _falseBranchCommands = new List<CommandNode>();

        // 新增：插入位置相关字段
        private int _currentInsertionIndex = -1;
        private bool _currentIsTrueBranch = true;
        private Border _insertionIndicator;
        private CommandNode _highlightedNode; // 当前高亮的节点
        private UserControl _highlightedControl; // 当前高亮的控件
 
        private readonly ILogService _logger;
        private readonly NodeControlFactory _nodeControlFactory;
        private readonly DesignerDragDropService _dragDropService;
        private readonly NodeManagementService _nodeManagementService;

        public IfNodeControlView()
        {
            InitializeComponent();

            // 初始化服务
            _logger = new LogService().ForContext<IfNodeControlView>();
            _nodeControlFactory = new NodeControlFactory(_logger);
            _dragDropService = new DesignerDragDropService(_logger);
            _nodeManagementService = new NodeManagementService(_logger);

            // 初始化插入指示器
            InitializeInsertionIndicator();

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
                        if (DataContext is CommandNode parallelNode && parallelNode.Children.Count > 0)
                        {
                            _logger.LogDebug($"可见性变化触发UI同步: {parallelNode.DisplayName}，子节点数: {parallelNode.Children.Count}");
                            SyncChildrenToUI();
                            
                        }
                    }), DispatcherPriority.Background);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "处理可见性变化失败");
            }
        }
        private void InitializeInsertionIndicator()
        {
            _insertionIndicator = new Border
            {
                Height = 2,
                Background = new SolidColorBrush(Color.FromRgb(0x00, 0x78, 0xD4)),
                CornerRadius = new CornerRadius(1),
                Visibility = Visibility.Collapsed,
                IsHitTestVisible = false
            };
        }

        private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (e.NewValue is CommandNode ifNode)
            {
                SyncChildrenToUI();
            }
        }

        public void SyncChildrenToUI()
        {
            if (DataContext is not CommandNode ifNode) return;

            try
            {
                _trueBranchCommands.Clear();
                _falseBranchCommands.Clear();
                TrueBranchContent.Children.Clear();
                FalseBranchContent.Children.Clear();

                foreach (var child in ifNode.Children)
                {
                    var branchType = child.GetExecutionProperty<string>("BranchType");
                    var isTrueBranch = branchType == "True";

                    var nestedControl = _nodeControlFactory.CreateNestedCommandControl(
                        child,
                        HandleNestedNodeClick,
                        RemoveNestedCommand);

                    if (isTrueBranch)
                    {
                        _trueBranchCommands.Add(child);
                        TrueBranchContent.Children.Add(nestedControl);
                    }
                    else
                    {
                        _falseBranchCommands.Add(child);
                        FalseBranchContent.Children.Add(nestedControl);
                    }
                }

                TrueHint.Visibility = TrueBranchContent.Children.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                FalseHint.Visibility = FalseBranchContent.Children.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

                UpdateBranchCanvasHeight();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "同步Children到UI失败");
            }
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            UpdateBranchCanvasHeight();
        }

        private void OnSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (e.NewSize != _lastMeasuredSize)
            {
                _lastMeasuredSize = e.NewSize;
                UpdateBranchCanvasHeight();
            }
        }

        #region 事件处理

        private void Node_Click(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed && DataContext is CommandNode node)
            {
                _logger.LogDebug("IF节点被点击: {NodeName}", node.DisplayName);
                HandleIfNodeClick(node);
                e.Handled = true;
            }
        }

        private void HandleIfNodeClick(CommandNode ifNode)
        {
            try
            {
                _logger.LogDebug("处理IF节点点击: {NodeName}", ifNode.DisplayName);

                var flowViewModel = _nodeManagementService.GetFlowDesignerViewModel(this);
                _nodeManagementService.ClearAllSelections(flowViewModel);

                ifNode.IsSelected = true;
                NodeClicked?.Invoke(this, ifNode);

                _logger.LogDebug("IF节点 {NodeName} 已被选中", ifNode.DisplayName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "处理IF节点点击失败: {NodeName}", ifNode?.DisplayName);
            }
        }

        private void HandleNestedNodeClick(CommandNode nestedNode)
        {
            try
            {
                _logger.LogDebug("IfNodeControlView.HandleNestedNodeClick: {NodeName}", nestedNode.DisplayName);

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

        #endregion

        #region 拖拽事件处理

        private void TrueBranch_DragEnter(object sender, DragEventArgs e)
        {
            if (_dragDropService.CanAcceptDrop(e.Data))
            {
                _dragDropService.SetDragOverStyle(TrueBranchDropZone);
                e.Effects = DragDropEffects.Copy;
            }
            else
            {
                e.Effects = DragDropEffects.None;
            }
            e.Handled = true;
        }

        private void TrueBranch_DragOver(object sender, DragEventArgs e)
        {
            if (_dragDropService.CanAcceptDrop(e.Data))
            {
                e.Effects = DragDropEffects.Copy;

                // 计算插入位置并高亮前一个节点
                var insertionInfo = GetInsertionInfo(e.GetPosition(TrueBranchContent), TrueBranchContent, true);
                HighlightPreviousNode(insertionInfo);

                _currentInsertionIndex = insertionInfo.InsertionIndex;
                _currentIsTrueBranch = true;
            }
            else
            {
                e.Effects = DragDropEffects.None;
            }
            e.Handled = true;
        }


        private void TrueBranch_DragLeave(object sender, DragEventArgs e)
        {
            _dragDropService.ClearDragOverStyle(TrueBranchDropZone);
            ClearHighlight();
            e.Handled = true;
        }

        private void TrueBranch_Drop(object sender, DragEventArgs e)
        {
            _dragDropService.ClearDragOverStyle(TrueBranchDropZone);

            // 记录当前的插入索引，然后清除高亮
            int insertionIndex = _currentInsertionIndex;
            ClearHighlight();

            _logger.LogDebug("TRUE分支拖拽完成，插入索引: {Index}", insertionIndex);

            HandleCommandDrop(e, true, insertionIndex);
            e.Handled = true;
        }

        private void FalseBranch_DragEnter(object sender, DragEventArgs e)
        {
            if (_dragDropService.CanAcceptDrop(e.Data))
            {
                _dragDropService.SetDragOverStyle(FalseBranchDropZone);
                e.Effects = DragDropEffects.Copy;
            }
            else
            {
                e.Effects = DragDropEffects.None;
            }
            e.Handled = true;
        }

        private void FalseBranch_DragOver(object sender, DragEventArgs e)
        {
            if (_dragDropService.CanAcceptDrop(e.Data))
            {
                e.Effects = DragDropEffects.Copy;

                // 计算插入位置并高亮前一个节点
                var insertionInfo = GetInsertionInfo(e.GetPosition(FalseBranchContent), FalseBranchContent, false);
                HighlightPreviousNode(insertionInfo);

                _currentInsertionIndex = insertionInfo.InsertionIndex;
                _currentIsTrueBranch = false;
            }
            else
            {
                e.Effects = DragDropEffects.None;
            }
            e.Handled = true;
        }

        private void FalseBranch_DragLeave(object sender, DragEventArgs e)
        {
            _dragDropService.ClearDragOverStyle(FalseBranchDropZone);
            ClearHighlight();
            e.Handled = true;
        }

        private void FalseBranch_Drop(object sender, DragEventArgs e)
        {
            _dragDropService.ClearDragOverStyle(FalseBranchDropZone);

            // 记录当前的插入索引，然后清除高亮
            int insertionIndex = _currentInsertionIndex;
            ClearHighlight();

            _logger.LogDebug("FALSE分支拖拽完成，插入索引: {Index}", insertionIndex);

            HandleCommandDrop(e, false, insertionIndex);
            e.Handled = true;
        }

        #endregion
        #region 高亮逻辑

        private class InsertionInfo
        {
            public int InsertionIndex { get; set; }
            public CommandNode PreviousNode { get; set; }
            public UserControl PreviousControl { get; set; }
            public bool IsInsertAtBeginning { get; set; }
        }

        private InsertionInfo GetInsertionInfo(Point mousePosition, StackPanel branchContent, bool isTrueBranch)
        {
            try
            {
                var commandList = isTrueBranch ? _trueBranchCommands : _falseBranchCommands;
                var info = new InsertionInfo();

                // 如果分支为空，插入位置为0，没有前一个节点
                if (commandList.Count == 0)
                {
                    info.InsertionIndex = 0;
                    info.IsInsertAtBeginning = true;
                    return info;
                }

                double cumulativeHeight = 0;

                for (int i = 0; i < commandList.Count; i++)
                {
                    var node = commandList[i];
                    var control = FindControlForNode(branchContent, node);

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
                                info.PreviousNode = commandList[i - 1];
                                info.PreviousControl = FindControlForNode(branchContent, info.PreviousNode);
                            }

                            _logger.LogTrace("鼠标位置 {Y} 在节点 {Index} 上半部分，插入位置: {InsertIndex}",
                                mousePosition.Y, i, i);
                            return info;
                        }

                        cumulativeHeight += controlHeight + 8; // 8是margin
                    }
                }

                // 如果鼠标在所有元素下方，插入到末尾
                info.InsertionIndex = commandList.Count;
                info.PreviousNode = commandList[commandList.Count - 1];
                info.PreviousControl = FindControlForNode(branchContent, info.PreviousNode);

                _logger.LogTrace("鼠标位置 {Y} 在所有节点下方，插入位置: {InsertIndex}",
                    mousePosition.Y, commandList.Count);
                return info;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "计算插入信息失败");
                return new InsertionInfo { InsertionIndex = 0, IsInsertAtBeginning = true };
            }
        }

        private UserControl FindControlForNode(StackPanel branchContent, CommandNode node)
        {
            foreach (UIElement child in branchContent.Children)
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
                    _logger.LogTrace("插入到IF分支开头，无需高亮节点");
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
        #region 插入位置计算和指示器

        private void EnsureInsertionIndicatorInBranch(StackPanel branchContent)
        {
            if (!branchContent.Children.Contains(_insertionIndicator))
            {
                // 从其他分支移除
                TrueBranchContent.Children.Remove(_insertionIndicator);
                FalseBranchContent.Children.Remove(_insertionIndicator);

                // 添加到当前分支（暂时添加到末尾，稍后会调整位置）
                branchContent.Children.Add(_insertionIndicator);
            }
        }


        private int GetInsertionIndex(Point mousePosition, StackPanel branchContent)
        {
            try
            {
                // 获取实际的命令控件（排除插入指示器）
                var commandControls = new List<UIElement>();
                foreach (UIElement child in branchContent.Children)
                {
                    if (child != _insertionIndicator)
                    {
                        commandControls.Add(child);
                    }
                }

                // 如果分支为空，插入位置为0
                if (commandControls.Count == 0)
                {
                    return 0;
                }

                double cumulativeHeight = 0;

                for (int i = 0; i < commandControls.Count; i++)
                {
                    var control = commandControls[i];

                    // 获取控件的实际高度
                    double controlHeight = 0;
                    if (control is FrameworkElement fe)
                    {
                        controlHeight = fe.ActualHeight;
                        if (controlHeight == 0)
                        {
                            fe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                            controlHeight = fe.DesiredSize.Height;
                        }
                    }
                    if (controlHeight == 0) controlHeight = 40; // 默认高度

                    // 检查鼠标是否在当前控件的上半部分
                    if (mousePosition.Y <= cumulativeHeight + controlHeight / 2)
                    {
                        _logger.LogTrace("鼠标位置 {Y} 在控件 {Index} 上半部分，插入位置: {InsertIndex}",
                            mousePosition.Y, i, i);
                        return i;
                    }

                    cumulativeHeight += controlHeight + 8; // 8是margin
                }

                // 如果鼠标在所有元素下方，插入到末尾
                _logger.LogTrace("鼠标位置 {Y} 在所有控件下方，插入位置: {InsertIndex}",
                    mousePosition.Y, commandControls.Count);
                return commandControls.Count;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "计算插入位置失败");
                return 0;
            }
        }

        private void ShowInsertionIndicator(int insertionIndex, StackPanel branchContent, bool isTrueBranch)
        {
            try
            {
                // 先移除指示器
                branchContent.Children.Remove(_insertionIndicator);

                // 获取实际的命令控件（排除插入指示器）
                var commandControls = new List<UIElement>();
                foreach (UIElement child in branchContent.Children)
                {
                    if (child != _insertionIndicator)
                    {
                        commandControls.Add(child);
                    }
                }

                // 计算插入位置
                int actualInsertIndex;

                if (insertionIndex <= 0)
                {
                    actualInsertIndex = 0;
                }
                else if (insertionIndex >= commandControls.Count)
                {
                    actualInsertIndex = branchContent.Children.Count;
                }
                else
                {
                    // 找到第insertionIndex个命令控件在branchContent.Children中的实际位置
                    var targetControl = commandControls[insertionIndex];
                    actualInsertIndex = branchContent.Children.IndexOf(targetControl);
                }

                // 在指定位置插入指示器
                branchContent.Children.Insert(actualInsertIndex, _insertionIndicator);

                _insertionIndicator.Visibility = Visibility.Visible;
                _insertionIndicator.Width = Math.Max(200, branchContent.ActualWidth - 16);

                _logger.LogTrace("显示插入指示器: 分支={Branch}, 逻辑索引={LogicIndex}, 实际索引={ActualIndex}",
                    isTrueBranch ? "TRUE" : "FALSE", insertionIndex, actualInsertIndex);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "显示插入指示器失败");
            }
        }

        private void HideInsertionIndicator()
        {
            try
            {
                _insertionIndicator.Visibility = Visibility.Collapsed;
                TrueBranchContent.Children.Remove(_insertionIndicator);
                FalseBranchContent.Children.Remove(_insertionIndicator);
                _currentInsertionIndex = -1;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "隐藏插入指示器失败");
            }
        }


        #endregion

        #region 命令添加

        private void HandleCommandDrop(DragEventArgs e, bool isTrueBranch, int insertionIndex = -1)
        {
            try
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
                    _logger.LogDebug("准备添加命令到 {Branch} 分支位置 {Index}: {Type}",
                        isTrueBranch ? "TRUE" : "FALSE", insertionIndex, command.GetType().Name);
                    AddCommandToBranch(command, isTrueBranch, insertionIndex);
                }
                else
                {
                    _logger.LogWarning("未找到有效的命令数据");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "处理命令拖拽失败");
            }
        }

        private void AddCommandToBranch(object command, bool isTrueBranch, int insertionIndex = -1)
        {
            try
            {
                CommandNode nestedNode = null;

                if (command is DeviceCommand deviceCmd)
                {
                    nestedNode = new CommandNode(deviceCmd);
                }
                else if (command is LogicCommand logicCmd)
                {
                    nestedNode = new CommandNode(logicCmd);
                }

                if (nestedNode != null && DataContext is CommandNode ifNode)
                {
                    // 创建UI控件
                    var nestedControl = _nodeControlFactory.CreateNestedCommandControl(
                        nestedNode,
                        HandleNestedNodeClick,
                        RemoveNestedCommand);

                    // 订阅复杂节点的内容变化事件
                    SubscribeToNestedControlEvents(nestedControl);

                    _logger.LogDebug("准备插入命令 {CommandName} 到 {Branch} 分支，插入索引: {Index}",
                        nestedNode.DisplayName, isTrueBranch ? "TRUE" : "FALSE", insertionIndex);

                    // 同步更新数据模型和UI
                    if (isTrueBranch)
                    {
                        if (insertionIndex >= 0 && insertionIndex <= _trueBranchCommands.Count)
                        {
                            // 插入到指定位置
                            _logger.LogDebug("在TRUE分支位置 {Index} 插入（当前有 {Count} 个命令）",
                                insertionIndex, _trueBranchCommands.Count);

                            // 1. 先更新UI集合
                            _trueBranchCommands.Insert(insertionIndex, nestedNode);

                            // 2. 计算UI插入位置并插入
                            int uiInsertIndex = Math.Min(insertionIndex, TrueBranchContent.Children.Count);
                            TrueBranchContent.Children.Insert(uiInsertIndex, nestedControl);

                            // 3. 更新数据模型
                            ifNode.AddChildToBranchAtIndex(nestedNode, isTrueBranch, insertionIndex);

                            _logger.LogDebug("TRUE分支插入完成：UI索引={UIIndex}, 数据索引={DataIndex}",
                                uiInsertIndex, insertionIndex);
                        }
                        else
                        {
                            // 添加到末尾
                            _trueBranchCommands.Add(nestedNode);
                            TrueBranchContent.Children.Add(nestedControl);
                            ifNode.AddChildToBranch(nestedNode, isTrueBranch);

                            _logger.LogDebug("TRUE分支添加到末尾");
                        }
                        TrueHint.Visibility = Visibility.Collapsed;
                    }
                    else
                    {
                        if (insertionIndex >= 0 && insertionIndex <= _falseBranchCommands.Count)
                        {
                            // 插入到指定位置
                            _logger.LogDebug("在FALSE分支位置 {Index} 插入（当前有 {Count} 个命令）",
                                insertionIndex, _falseBranchCommands.Count);

                            // 1. 先更新UI集合
                            _falseBranchCommands.Insert(insertionIndex, nestedNode);

                            // 2. 计算UI插入位置并插入
                            int uiInsertIndex = Math.Min(insertionIndex, FalseBranchContent.Children.Count);
                            FalseBranchContent.Children.Insert(uiInsertIndex, nestedControl);

                            // 3. 更新数据模型
                            ifNode.AddChildToBranchAtIndex(nestedNode, isTrueBranch, insertionIndex);

                            _logger.LogDebug("FALSE分支插入完成：UI索引={UIIndex}, 数据索引={DataIndex}",
                                uiInsertIndex, insertionIndex);
                        }
                        else
                        {
                            // 添加到末尾
                            _falseBranchCommands.Add(nestedNode);
                            FalseBranchContent.Children.Add(nestedControl);
                            ifNode.AddChildToBranch(nestedNode, isTrueBranch);

                            _logger.LogDebug("FALSE分支添加到末尾");
                        }
                        FalseHint.Visibility = Visibility.Collapsed;
                    }

                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        UpdateBranchCanvasHeight();
                        ContentChanged?.Invoke(this, EventArgs.Empty);

                        // 触发嵌套命令添加事件
                        NestedCommandAdded?.Invoke(this, new NestedCommandEventArgs
                        {
                            NestedCommand = nestedNode,
                            IsTrueBranch = isTrueBranch,
                            ParentIfNode = ifNode
                        });

                        _logger.LogInformation("命令 {CommandName} 已添加到 {Branch} 分支位置 {Index}",
                            nestedNode.DisplayName, isTrueBranch ? "TRUE" : "FALSE", insertionIndex);

                    }), System.Windows.Threading.DispatcherPriority.Loaded);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "添加嵌套命令失败");
            }
        }
        private void SubscribeToNestedControlEvents(UserControl nestedControl)
        {
            if (nestedControl is ParallelNodeControlView parallelControl)
            {
                parallelControl.ContentChanged += (s, e) =>
                {
                    _logger.LogDebug("IF节点内的并行节点内容发生变化");
                    OnBranchContentChanged();
                };
            }
            else if (nestedControl is IfNodeControlView nestedIfControl)
            {
                nestedIfControl.ContentChanged += (s, e) =>
                {
                    _logger.LogDebug("IF节点内的嵌套IF节点内容发生变化");
                    OnBranchContentChanged();
                };
            }
            else if (nestedControl is LoopNodeControlView nestedLoopControl)
            {
                nestedLoopControl.ContentChanged += (s, e) =>
                {
                    _logger.LogDebug("IF节点内的嵌套Loop节点内容发生变化");
                    OnBranchContentChanged();
                };
            }
        }


        #endregion

        #region 命令管理

        private void DeleteButton_Click(object sender, MouseButtonEventArgs e)
        {
            try
            {
                if (DataContext is CommandNode ifNode)
                {
                    _logger.LogDebug("请求删除IF节点: {NodeName}", ifNode.DisplayName);
                    NodeDeleteRequested?.Invoke(this, ifNode);
                    e.Handled = true;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "删除IF节点失败");
            }
        }

        public void RemoveNestedCommand(CommandNode nestedNode)
        {
            try
            {
                if (DataContext is not CommandNode ifNode) return;

                bool isTrueBranch = _trueBranchCommands.Contains(nestedNode);
                bool removed = false;

                // 从数据模型移除
                ifNode.RemoveChild(nestedNode);

                if (isTrueBranch)
                {
                    int logicalIndex = _trueBranchCommands.IndexOf(nestedNode);
                    _trueBranchCommands.Remove(nestedNode);

                    // 从UI移除 - 找到对应的控件
                    UserControl controlToRemove = null;
                    foreach (UIElement child in TrueBranchContent.Children)
                    {
                        if (child is UserControl control)
                        {
                            if (control.Tag == nestedNode ||
                                (control.DataContext is CommandNode node && node == nestedNode))
                            {
                                controlToRemove = control;
                                break;
                            }
                        }
                    }

                    if (controlToRemove != null)
                    {
                        TrueBranchContent.Children.Remove(controlToRemove);
                        removed = true;
                    }

                    TrueHint.Visibility = _trueBranchCommands.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                }
                else if (_falseBranchCommands.Contains(nestedNode))
                {
                    int logicalIndex = _falseBranchCommands.IndexOf(nestedNode);
                    _falseBranchCommands.Remove(nestedNode);

                    // 从UI移除 - 找到对应的控件
                    UserControl controlToRemove = null;
                    foreach (UIElement child in FalseBranchContent.Children)
                    {
                        if (child is UserControl control)
                        {
                            if (control.Tag == nestedNode ||
                                (control.DataContext is CommandNode node && node == nestedNode))
                            {
                                controlToRemove = control;
                                break;
                            }
                        }
                    }

                    if (controlToRemove != null)
                    {
                        FalseBranchContent.Children.Remove(controlToRemove);
                        removed = true;
                    }

                    FalseHint.Visibility = _falseBranchCommands.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                }

                if (removed)
                {
                    NestedCommandRemoved?.Invoke(this, new NestedCommandEventArgs
                    {
                        NestedCommand = nestedNode,
                        IsTrueBranch = isTrueBranch,
                        ParentIfNode = ifNode
                    });

                    UpdateBranchCanvasHeight();
                    OnBranchContentChanged();

                    _logger.LogInformation("命令 {CommandName} 已从 {Branch} 分支移除",
                        nestedNode.DisplayName, isTrueBranch ? "TRUE" : "FALSE");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "移除嵌套命令失败: {NodeName}", nestedNode?.DisplayName);
            }
        }

        public void ClearAllBranches()
        {
            try
            {
                if (DataContext is CommandNode ifNode)
                {
                    ifNode.ClearChildren();
                }

                _trueBranchCommands.Clear();
                _falseBranchCommands.Clear();
                TrueBranchContent.Children.Clear();
                FalseBranchContent.Children.Clear();
                TrueHint.Visibility = Visibility.Visible;
                FalseHint.Visibility = Visibility.Visible;

                UpdateBranchCanvasHeight();
                OnBranchContentChanged();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "清理所有分支失败");
            }
        }

        public IReadOnlyList<CommandNode> GetTrueBranchCommands() => _trueBranchCommands.AsReadOnly();
        public IReadOnlyList<CommandNode> GetFalseBranchCommands() => _falseBranchCommands.AsReadOnly();
        public int GetTrueBranchCount() => _trueBranchCommands.Count;
        public int GetFalseBranchCount() => _falseBranchCommands.Count;

        #endregion

        #region 布局更新

        private void UpdateBranchCanvasHeight()
        {
            try
            {
                TrueBranchWrapper.UpdateLayout();
                FalseBranchWrapper.UpdateLayout();

                var trueBranchSize = CalculateBranchContentSize(TrueBranchContent);
                var falseBranchSize = CalculateBranchContentSize(FalseBranchContent);

                _logger.LogTrace("TRUE分支尺寸: {Width}x{Height}", trueBranchSize.Width, trueBranchSize.Height);
                _logger.LogTrace("FALSE分支尺寸: {Width}x{Height}", falseBranchSize.Width, falseBranchSize.Height);

                AdjustBranchContainers(trueBranchSize, falseBranchSize);
                UpdateCanvasSizeBasedOnBranches(trueBranchSize, falseBranchSize);

                _logger.LogTrace("Canvas尺寸更新完成: {Width}x{Height}", BranchCanvas.Width, BranchCanvas.Height);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "更新分支布局失败");
                BranchCanvas.Width = 600;
                BranchCanvas.Height = 200;
            }
        }

        private Size CalculateBranchContentSize(StackPanel branchContent)
        {
            double maxWidth = 200;
            double totalHeight = 0;

            foreach (UIElement child in branchContent.Children)
            {
                // 跳过插入指示器
                if (child == _insertionIndicator) continue;

                try
                {
                    child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    var childSize = child.DesiredSize;
                    maxWidth = Math.Max(maxWidth, childSize.Width);
                    totalHeight += childSize.Height;

                    if (child is IfNodeControlView)
                    {
                        maxWidth = Math.Max(maxWidth, 620);
                    }
                    else if (child is ParallelNodeControlView)
                    {
                        maxWidth = Math.Max(maxWidth, 1020);
                    }
                    else if (child is LoopNodeControlView)
                    {
                        maxWidth = Math.Max(maxWidth, 320);
                    }

                    _logger.LogTrace("  子元素尺寸: {Width}x{Height}", childSize.Width, childSize.Height);
                }
                catch (Exception ex)
                {
                    _logger.LogCritical(ex, "测量子元素失败");
                    totalHeight += 40;
                }
            }

            // 计算实际子元素数量（排除插入指示器）
            int actualChildCount = 0;
            foreach (UIElement child in branchContent.Children)
            {
                if (child != _insertionIndicator)
                {
                    actualChildCount++;
                }
            }

            totalHeight += Math.Max(0, actualChildCount - 1) * 8;
            totalHeight += 16;

            return new Size(maxWidth, Math.Max(60, totalHeight));
        }

        private void AdjustBranchContainers(Size trueBranchSize, Size falseBranchSize)
        {
            try
            {
                double mainCardCenterX = CalculateMainCardCenterX();

                const double FALSE_BOUNDARY_OFFSET = -50;
                const double TRUE_BOUNDARY_OFFSET = 50;

                double FALSE_FIXED_RIGHT_BOUNDARY = mainCardCenterX + FALSE_BOUNDARY_OFFSET;
                double TRUE_FIXED_LEFT_BOUNDARY = mainCardCenterX + TRUE_BOUNDARY_OFFSET;

                double trueBranchWidth = Math.Max(200, trueBranchSize.Width + 20);
                double falseBranchWidth = Math.Max(200, falseBranchSize.Width + 20);

                double falseRight = FALSE_FIXED_RIGHT_BOUNDARY;
                double falseLeft = falseRight - falseBranchWidth;

                double trueLeft = TRUE_FIXED_LEFT_BOUNDARY;

                Canvas.SetLeft(FalseBranchWrapper, falseLeft);
                Canvas.SetLeft(TrueBranchWrapper, trueLeft);

                FalseBranchWrapper.Width = falseBranchWidth;
                TrueBranchWrapper.Width = trueBranchWidth;
                FalseBranchWrapper.MinWidth = falseBranchWidth;
                TrueBranchWrapper.MinWidth = trueBranchWidth;

                double falseCenterX = falseLeft + falseBranchWidth / 2;
                double trueCenterX = trueLeft + trueBranchWidth / 2;

                UpdateConnectionLines(falseCenterX, trueCenterX);

                _logger.LogTrace("分支中心 - FALSE: {FalseCenter}, TRUE: {TrueCenter}", falseCenterX, trueCenterX);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "调整分支容器失败");
            }
        }

        private void UpdateConnectionLines(double falseCenterX, double trueCenterX)
        {
            try
            {
                double mainCardCenterX = CalculateMainCardCenterX();

                const double FALSE_OFFSET = -150;
                const double TRUE_OFFSET = 150;

                double falseConnectionX = mainCardCenterX + FALSE_OFFSET;
                double trueConnectionX = mainCardCenterX + TRUE_OFFSET;

                MainVerticalLine.X1 = mainCardCenterX;
                MainVerticalLine.X2 = mainCardCenterX;

                LeftHorizontalLine.X1 = falseConnectionX;
                LeftHorizontalLine.X2 = mainCardCenterX;

                RightHorizontalLine.X1 = mainCardCenterX;
                RightHorizontalLine.X2 = trueConnectionX;

                FalseVerticalLine.X1 = falseConnectionX;
                FalseVerticalLine.X2 = falseConnectionX;

                TrueVerticalLine.X1 = trueConnectionX;
                TrueVerticalLine.X2 = trueConnectionX;

                Canvas.SetLeft(FalseArrow, falseConnectionX - 4);
                Canvas.SetLeft(TrueArrow, trueConnectionX - 4);

                _logger.LogTrace("连接线更新: 主卡片中心({MainCenter}), FALSE连接点({FalseConnection}), TRUE连接点({TrueConnection})",
                    mainCardCenterX, falseConnectionX, trueConnectionX);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "更新连接线失败");
            }
        }

        private double CalculateMainCardCenterX()
        {
            try
            {
                double canvasWidth = BranchCanvas.ActualWidth > 0 ? BranchCanvas.ActualWidth : BranchCanvas.Width;
                double mainCardCenterX = canvasWidth / 2;

                _logger.LogTrace("Canvas宽度: {Width}, 主卡片中心: {Center}", canvasWidth, mainCardCenterX);
                return mainCardCenterX;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "计算主卡片中心失败");
                double defaultWidth = BranchCanvas.Width > 0 ? BranchCanvas.Width : 600;
                return defaultWidth / 2;
            }
        }

        private void UpdateCanvasSizeBasedOnBranches(Size trueBranchSize, Size falseBranchSize)
        {
            try
            {
                double trueBranchWidth = Math.Max(200, trueBranchSize.Width + 20);
                double falseBranchWidth = Math.Max(200, falseBranchSize.Width + 20);

                double maxBranchExtension = Math.Max(
                    falseBranchWidth - 200,
                    trueBranchWidth - 200
                );

                double canvasWidth = Math.Max(600, 600 + maxBranchExtension * 2);

                double maxBranchHeight = Math.Max(trueBranchSize.Height, falseBranchSize.Height);
                double canvasHeight = Math.Max(200, maxBranchHeight + 80);

                BranchCanvas.Width = canvasWidth;
                BranchCanvas.Height = canvasHeight;

                _logger.LogTrace("Canvas尺寸计算:");
                _logger.LogTrace("  FALSE分支宽度: {FalseWidth}, TRUE分支宽度: {TrueWidth}", falseBranchWidth, trueBranchWidth);
                _logger.LogTrace("  最大扩展: {MaxExtension}", maxBranchExtension);
                _logger.LogTrace("  最终Canvas宽度: {Width}x{Height}", canvasWidth, canvasHeight);

                BranchCanvas.UpdateLayout();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "更新Canvas尺寸失败");
            }
        }

        public void OnBranchContentChanged()
        {
            try
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    UpdateBranchCanvasHeight();
                    ContentChanged?.Invoke(this, EventArgs.Empty);
                    _logger.LogTrace("IF节点内容变化通知完成，当前控件尺寸: {Width}x{Height}",
                        this.ActualWidth, this.ActualHeight);
                }), System.Windows.Threading.DispatcherPriority.Background);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "OnBranchContentChanged 错误");
            }
        }
        public void RefreshConditionDisplay()
        {
            try
            {
                if (DataContext is not CommandNode node) return;
                if (node.LogicCommand?.Properties == null) return;

                System.Diagnostics.Debug.WriteLine($"RefreshConditionDisplay called for: {node.DisplayName}");

                // 获取属性值
                var leftOperand = node.LogicCommand.Properties.ContainsKey("LeftOperand")
                    ? node.LogicCommand.Properties["LeftOperand"]?.ToString() ?? "A"
                    : "A";

                var operatorStr = node.LogicCommand.Properties.ContainsKey("Operator")
                    ? node.LogicCommand.Properties["Operator"]?.ToString() ?? "Equal"
                    : "Equal";

                var rightOperand = node.LogicCommand.Properties.ContainsKey("RightOperand")
                    ? node.LogicCommand.Properties["RightOperand"]?.ToString() ?? "B"
                    : "B";

                // 转换操作符显示
                var operatorDisplay = ConvertOperatorForDisplay(operatorStr);

                // 直接更新TextBlock的文本
                if (ConditionSummaryText != null)
                {
                    ConditionSummaryText.Text = $"{leftOperand} {operatorDisplay} {rightOperand}";
                    System.Diagnostics.Debug.WriteLine($"Updated condition display: {leftOperand} {operatorDisplay} {rightOperand}");
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine("ConditionSummaryText is null");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "刷新IF节点条件显示失败");
                System.Diagnostics.Debug.WriteLine($"RefreshConditionDisplay error: {ex.Message}");
            }
        }

        private string ConvertOperatorForDisplay(string operatorValue)
        {
            return operatorValue switch
            {
                "Equal" => "==",
                "NotEqual" => "!=",
                "GreaterThan" => ">",
                "GreaterThanOrEqual" => ">=",
                "LessThan" => "<",
                "LessThanOrEqual" => "<=",
                "Contains" => "包含",
                "NotContains" => "不包含",
                "StartsWith" => "开始于",
                "EndsWith" => "结束于",
                _ => operatorValue
            };
        }
        #endregion
    }
}
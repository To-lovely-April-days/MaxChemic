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
        /// <summary>
        /// 返回整个 IF 节点控件的实际总宽度（RootCanvas.Width）
        /// </summary>
        public double TotalActualWidth => RootCanvas.Width > 0 ? RootCanvas.Width : ActualWidth;
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
                Width = 2,
                Height = 60,  // 初始高度，ShowInsertionIndicator 时会动态调整
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

                double cumulativeWidth = 0;

                for (int i = 0; i < commandList.Count; i++)
                {
                    var node = commandList[i];
                    var control = FindControlForNode(branchContent, node);

                    if (control != null)
                    {
                        // 水平布局：获取控件的实际宽度
                        double controlWidth = control.ActualWidth;
                        if (controlWidth == 0)
                        {
                            control.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                            controlWidth = control.DesiredSize.Width;
                        }
                        if (controlWidth == 0) controlWidth = 231; // 默认宽度

                        // 检查鼠标是否在当前控件的左半部分（水平布局用 X）
                        if (mousePosition.X <= cumulativeWidth + controlWidth / 2)
                        {
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

                            _logger.LogTrace("鼠标位置 {X} 在节点 {Index} 左半部分，插入位置: {InsertIndex}",
                                mousePosition.X, i, i);
                            return info;
                        }

                        cumulativeWidth += controlWidth + 8; // 8是间距
                    }
                }

                // 如果鼠标在所有元素右侧，插入到末尾
                info.InsertionIndex = commandList.Count;
                info.PreviousNode = commandList[commandList.Count - 1];
                info.PreviousControl = FindControlForNode(branchContent, info.PreviousNode);

                _logger.LogTrace("鼠标位置 {X} 在所有节点右侧，插入位置: {InsertIndex}",
                    mousePosition.X, commandList.Count);
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

        // =====================================================================
        // 以下方法完整替换 cs 文件中的 #region 布局更新 区域
        // =====================================================================

        // 布局常量
        private const double CARD_WIDTH = 231.0;
        private const double CARD_HEIGHT = 64.0;
        private const double BRANCH_GAP_V = 20.0;   // TRUE/FALSE 分支之间的垂直间距
        private const double BRANCH_OFFSET_X = 60.0; // 主卡片右边到分支容器左边的距离（走线区域）
        private const double MIN_BRANCH_WIDTH = 200.0;
        private const double MIN_BRANCH_HEIGHT = 76.0; // label(20) + padding(8) + minContent(40) + padding(8)

        private void UpdateBranchCanvasHeight()
        {
            try
            {
                // 1. 手动计算两个分支的实际内容尺寸（比依赖 DesiredSize 更可靠）
                var trueSize = CalculateBranchContentSize(TrueBranchContent);
                var falseSize = CalculateBranchContentSize(FalseBranchContent);

                // 加上 label 高度(约20) + dropzone padding(8*2) + border(1*2)
                const double WRAPPER_EXTRA_H = 38.0;

                double trueW = Math.Max(MIN_BRANCH_WIDTH, trueSize.Width);
                double trueH = Math.Max(MIN_BRANCH_HEIGHT, trueSize.Height + WRAPPER_EXTRA_H);
                double falseW = Math.Max(MIN_BRANCH_WIDTH, falseSize.Width);
                double falseH = Math.Max(MIN_BRANCH_HEIGHT, falseSize.Height + WRAPPER_EXTRA_H);

                // 2. 计算整体高度
                //    TRUE 分支在上，FALSE 在下，中间留 BRANCH_GAP_V
                //    主卡片垂直居中于两个分支叠加后的总高度
                double totalBranchH = trueH + BRANCH_GAP_V + falseH;
                double cardTop = Math.Max(0, (totalBranchH - CARD_HEIGHT) / 2.0);

                double canvasH = Math.Max(CARD_HEIGHT, totalBranchH);
                double branchX = CARD_WIDTH + BRANCH_OFFSET_X; // 分支容器的 Canvas.Left

                // 3. 计算分支容器的 Canvas.Top
                double trueTop = (canvasH - totalBranchH) / 2.0;
                double falseTop = trueTop + trueH + BRANCH_GAP_V;

                // 4. 设置分支容器位置和尺寸
                Canvas.SetLeft(TrueBranchWrapper, branchX);
                Canvas.SetTop(TrueBranchWrapper, trueTop);
                TrueBranchWrapper.Width = trueW;

                Canvas.SetLeft(FalseBranchWrapper, branchX);
                Canvas.SetTop(FalseBranchWrapper, falseTop);
                FalseBranchWrapper.Width = falseW;

                // 5. 主卡片垂直居中
                Canvas.SetLeft(MainStepCard, 0);
                Canvas.SetTop(MainStepCard, cardTop);

                // 6. 计算连线坐标
                //    主卡片右中点 → 分叉点 → 分叉出 TRUE/FALSE
                double cardRightX = CARD_WIDTH;
                double cardCenterY = cardTop + CARD_HEIGHT / 2.0;

                double forkX = CARD_WIDTH + BRANCH_OFFSET_X / 2.0; // 分叉点在走线区域中间

                double trueCenterY = trueTop + trueH / 2.0;
                double falseCenterY = falseTop + falseH / 2.0;

                // 主横线：卡片右边 → 分叉点
                MainHorizontalLine.X1 = cardRightX;
                MainHorizontalLine.Y1 = cardCenterY;
                MainHorizontalLine.X2 = forkX;
                MainHorizontalLine.Y2 = cardCenterY;

                // 分叉竖线：连接 TRUE 中心 Y 到 FALSE 中心 Y
                BranchVerticalLine.X1 = forkX;
                BranchVerticalLine.Y1 = trueCenterY;
                BranchVerticalLine.X2 = forkX;
                BranchVerticalLine.Y2 = falseCenterY;

                // TRUE 横线：分叉点 → TRUE 容器左边，箭头在末端
                TrueHorizontalLine.X1 = forkX;
                TrueHorizontalLine.Y1 = trueCenterY;
                TrueHorizontalLine.X2 = branchX - 8; // 留出箭头空间
                TrueHorizontalLine.Y2 = trueCenterY;

                Canvas.SetLeft(TrueArrow, branchX - 8);
                Canvas.SetTop(TrueArrow, trueCenterY - 4);

                // FALSE 横线：分叉点 → FALSE 容器左边
                FalseHorizontalLine.X1 = forkX;
                FalseHorizontalLine.Y1 = falseCenterY;
                FalseHorizontalLine.X2 = branchX - 8;
                FalseHorizontalLine.Y2 = falseCenterY;

                Canvas.SetLeft(FalseArrow, branchX - 8);
                Canvas.SetTop(FalseArrow, falseCenterY - 4);

                // 7. 设置 Canvas 总尺寸（让父级能正确测量）
                double maxBranchW = Math.Max(trueW, falseW);
                RootCanvas.Width = branchX + maxBranchW + 16;
                RootCanvas.Height = canvasH;

                _logger.LogTrace("IF横向布局: Canvas={W}x{H}, CardTop={CT}, TrueTop={TT}, FalseTop={FT}",
                    RootCanvas.Width, RootCanvas.Height, cardTop, trueTop, falseTop);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "IF节点横向布局失败");
                RootCanvas.Width = 600;
                RootCanvas.Height = 200;
            }
        }

        // CalculateBranchContentSize 保留但内容改为测量横向 StackPanel
        private Size CalculateBranchContentSize(StackPanel branchContent)
        {
            double totalWidth = 0;
            double maxHeight = 0;
            int childCount = 0;

            foreach (UIElement child in branchContent.Children)
            {
                if (child == _insertionIndicator) continue;
                try
                {
                    double childW = 0, childH = 0;

                    if (child is FrameworkElement fe)
                    {
                        // 优先用实际渲染尺寸
                        childW = fe.ActualWidth > 0 ? fe.ActualWidth : 0;
                        childH = fe.ActualHeight > 0 ? fe.ActualHeight : 0;
                    }

                    // 实际尺寸为0时再测量
                    if (childW <= 0 || childH <= 0)
                    {
                        child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                        if (childW <= 0) childW = child.DesiredSize.Width;
                        if (childH <= 0) childH = child.DesiredSize.Height;
                    }

                    if (childW <= 0) childW = 231;
                    if (childH <= 0) childH = 64;

                    totalWidth += childW;
                    maxHeight = Math.Max(maxHeight, childH);
                    childCount++;
                }
                catch
                {
                    totalWidth += 231;
                    maxHeight = Math.Max(maxHeight, 64);
                    childCount++;
                }
            }

            if (childCount > 1) totalWidth += (childCount - 1) * 8; // 节点间距
            totalWidth += 16; // 内边距

            return new Size(Math.Max(MIN_BRANCH_WIDTH, totalWidth),
                            Math.Max(0, maxHeight)); // 高度不在这里加padding，交给调用方
        }

        // AdjustBranchContainers / UpdateConnectionLines / UpdateCanvasSizeBasedOnBranches
        // 这三个旧方法保留空实现即可（UpdateBranchCanvasHeight 已合并所有逻辑）
        private void AdjustBranchContainers(Size t, Size f) { }
        private void UpdateConnectionLines(double a, double b) { }
        private void UpdateCanvasSizeBasedOnBranches(Size t, Size f) { }
        private double CalculateMainCardCenterX() => CARD_WIDTH / 2.0;

        public void OnBranchContentChanged()
        {
            try
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    UpdateBranchCanvasHeight();
                    ContentChanged?.Invoke(this, EventArgs.Empty);
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

                var leftOperand = node.LogicCommand.Properties.ContainsKey("LeftOperand")
                    ? node.LogicCommand.Properties["LeftOperand"]?.ToString() ?? "变量"
                    : "变量";

                var operatorStr = node.LogicCommand.Properties.ContainsKey("Operator")
                    ? node.LogicCommand.Properties["Operator"]?.ToString() ?? "=="
                    : "==";

                var rightOperand = node.LogicCommand.Properties.ContainsKey("RightOperand")
                    ? node.LogicCommand.Properties["RightOperand"]?.ToString() ?? "值"
                    : "值";

                // 将存储的 Operator 枚举值转换为显示符号
                var operatorDisplay = ConvertOperatorForDisplay(operatorStr);

                if (ConditionSummaryText != null)
                {
                    ConditionSummaryText.Text = $"{leftOperand} {operatorDisplay} {rightOperand}";
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "刷新IF节点条件显示失败");
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
    }
}
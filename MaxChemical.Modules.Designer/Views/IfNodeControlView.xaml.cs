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
        // 新增：布局尺寸变化时通知外层修正 node.Y
        // 参数是"主卡片中心在 RootCanvas 内的 Y 偏移"
        public event EventHandler<double> LayoutChanged;
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

                //  修复：清空后重置 Canvas 尺寸，防止残留旧值
                TrueBranchContent.Width = 0;
                TrueBranchContent.Height = 0;
                FalseBranchContent.Width = 0;
                FalseBranchContent.Height = 0;

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
                        if (_trueBranchCommands.Count > 0) // 已有节点时才加连接线
                            TrueBranchContent.Children.Add(CreateBranchConnector());
                        _trueBranchCommands.Add(child);
                        TrueBranchContent.Children.Add(nestedControl);
                    }
                    else
                    {
                        if (_falseBranchCommands.Count > 0) // ← 修复：FALSE 分支同样要加连接线
                            FalseBranchContent.Children.Add(CreateBranchConnector());
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

        private InsertionInfo GetInsertionInfo(Point mousePosition, Canvas branchContent, bool isTrueBranch)
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
                        double controlLeft = Canvas.GetLeft(control);
                        double controlWidth = control.ActualWidth;
                        if (controlWidth == 0)
                        {
                            control.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                            controlWidth = control.DesiredSize.Width;
                        }
                        if (controlWidth == 0) controlWidth = 231;

                        // 鼠标在此控件左半部分
                        if (mousePosition.X <= controlLeft + controlWidth / 2)
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
                            return info;
                        }
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

        private UserControl FindControlForNode(Canvas branchContent, CommandNode node)
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

                // ★ 直接从 dragData 里取 SourceDeviceType,不污染 service
                string sourceDeviceType = ExtractSourceDeviceTypeFromDragData(e.Data);

                if (command != null)
                {
                    _logger.LogDebug("准备添加命令到 {Branch} 分支位置 {Index}: {Type}, 来源设备类型: {SrcType}",
                        isTrueBranch ? "TRUE" : "FALSE", insertionIndex, command.GetType().Name,
                        sourceDeviceType ?? "(null)");
                    AddCommandToBranch(command, isTrueBranch, insertionIndex, sourceDeviceType);
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

        /// <summary>
        /// 从拖拽数据中提取 SourceDeviceType。
        /// 用反射读 CommandDragInfo.SourceDeviceType 属性,跨命名空间也能用。
        /// 没有则返回 null。
        /// </summary>
        private string ExtractSourceDeviceTypeFromDragData(IDataObject data)
        {
            try
            {
                if (data == null || !data.GetDataPresent("CommandDragInfo")) return null;
                var raw = data.GetData("CommandDragInfo");
                if (raw == null) return null;

                var prop = raw.GetType().GetProperty("SourceDeviceType");
                return prop?.GetValue(raw) as string;
            }
            catch
            {
                return null;
            }
        }

        private void AddCommandToBranch(object command, bool isTrueBranch, int insertionIndex = -1, string sourceDeviceType = null)
        {
            try
            {
                CommandNode nestedNode = null;

                if (command is DeviceCommand deviceCmd)
                {
                    nestedNode = new CommandNode(deviceCmd);
                    nestedNode.SourceDeviceType = sourceDeviceType;   // ★ 写入源设备类型
                }
                else if (command is LogicCommand logicCmd)
                {
                    nestedNode = new CommandNode(logicCmd);
                }

                if (nestedNode == null || DataContext is not CommandNode ifNode) return;

                var nestedControl = _nodeControlFactory.CreateNestedCommandControl(
                    nestedNode, HandleNestedNodeClick, RemoveNestedCommand);
                // 关键修复：让子控件在水平 StackPanel 内垂直居中
                nestedControl.VerticalAlignment = VerticalAlignment.Center;
                SubscribeToNestedControlEvents(nestedControl);

                var commandList = isTrueBranch ? _trueBranchCommands : _falseBranchCommands;
                var branchPanel = isTrueBranch ? TrueBranchContent : FalseBranchContent;
                var hintBlock = isTrueBranch ? TrueHint : FalseHint;

                // 规范化逻辑插入索引
                bool appendToEnd = insertionIndex < 0 || insertionIndex >= commandList.Count;
                int logicalIndex = appendToEnd ? commandList.Count : insertionIndex;

                _logger.LogDebug("添加命令 {Name} 到 {Branch} 分支，逻辑索引={Idx}（共{Count}个）",
                    nestedNode.DisplayName, isTrueBranch ? "TRUE" : "FALSE", logicalIndex, commandList.Count);

                // ── 更新逻辑列表 ──
                commandList.Insert(logicalIndex, nestedNode);

                // ── 更新 UI ──
                // StackPanel 里的布局：节点0 | 连线 | 节点1 | 连线 | 节点2 ...
                // 第 i 个节点的 UI 索引 = i * 2
                // 第 i 个连接线的 UI 索引 = i * 2 - 1  (i >= 1)
                if (commandList.Count == 1)
                {
                    // 第一个节点，直接加，不需要连接线
                    branchPanel.Children.Add(nestedControl);
                }
                else if (appendToEnd)
                {
                    // 追加到末尾：先加连接线，再加节点
                    branchPanel.Children.Add(CreateBranchConnector());
                    branchPanel.Children.Add(nestedControl);
                }
                else
                {
                    // 插入到中间或开头
                    int uiNodeIndex = logicalIndex * 2; // 新节点应处于的 UI 位置

                    if (logicalIndex == 0)
                    {
                        // 插到最前面：加节点，再在节点后面加连接线
                        branchPanel.Children.Insert(0, CreateBranchConnector()); // 节点和原来第一个之间的线
                        branchPanel.Children.Insert(0, nestedControl);           // 节点放最前
                    }
                    else
                    {
                        // 插到中间：在 uiNodeIndex 处先插连接线，再在其后插节点
                        // uiNodeIndex 此时指向"原来第logicalIndex个节点"的位置
                        branchPanel.Children.Insert(uiNodeIndex, nestedControl);           // 插节点
                        branchPanel.Children.Insert(uiNodeIndex, CreateBranchConnector()); // 连接线插在节点前
                    }
                }

                // ── 更新数据模型 ──
                if (appendToEnd)
                    ifNode.AddChildToBranch(nestedNode, isTrueBranch);
                else
                    ifNode.AddChildToBranchAtIndex(nestedNode, isTrueBranch, logicalIndex);

                hintBlock.Visibility = Visibility.Collapsed;

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    UpdateBranchCanvasHeight();
                    ContentChanged?.Invoke(this, EventArgs.Empty);
                    NestedCommandAdded?.Invoke(this, new NestedCommandEventArgs
                    {
                        NestedCommand = nestedNode,
                        IsTrueBranch = isTrueBranch,
                        ParentIfNode = ifNode
                    });
                    _logger.LogInformation("命令 {Name} 已添加到 {Branch} 分支位置 {Idx}",
                        nestedNode.DisplayName, isTrueBranch ? "TRUE" : "FALSE", logicalIndex);
                }), System.Windows.Threading.DispatcherPriority.Loaded);
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

                //  修复：冒泡嵌套命令事件到外层
                parallelControl.NestedCommandAdded += (s, e) =>
                {
                    _logger.LogDebug("IF节点内的并行节点添加了嵌套命令，冒泡事件");
                    NestedCommandAdded?.Invoke(this, e);
                };
                parallelControl.NestedCommandRemoved += (s, e) =>
                {
                    _logger.LogDebug("IF节点内的并行节点移除了嵌套命令，冒泡事件");
                    NestedCommandRemoved?.Invoke(this, e);
                };
                parallelControl.LogicCommandRequested += (s, e) =>
                {
                    LogicCommandRequested?.Invoke(this, e);
                };
            }
            else if (nestedControl is IfNodeControlView nestedIfControl)
            {
                nestedIfControl.ContentChanged += (s, e) =>
                {
                    _logger.LogDebug("IF节点内的嵌套IF节点内容发生变化");
                    OnBranchContentChanged();
                };

                // 嵌套 IF 布局变化时，也触发外层重算
                nestedIfControl.LayoutChanged += (s, offsetY) =>
                {
                    _logger.LogDebug("IF节点内的嵌套IF节点布局变化，CardCenterOffsetY={0}", offsetY);
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        UpdateBranchCanvasHeight();
                        ContentChanged?.Invoke(this, EventArgs.Empty);
                    }), DispatcherPriority.Render);
                };

                //  修复：冒泡嵌套命令事件到外层，使深层嵌套节点的添加/删除能通知到 ViewModel
                nestedIfControl.NestedCommandAdded += (s, e) =>
                {
                    _logger.LogDebug("IF节点内的嵌套IF添加了命令，冒泡事件: {Name}", e.NestedCommand?.DisplayName);
                    NestedCommandAdded?.Invoke(this, e);
                };
                nestedIfControl.NestedCommandRemoved += (s, e) =>
                {
                    _logger.LogDebug("IF节点内的嵌套IF移除了命令，冒泡事件: {Name}", e.NestedCommand?.DisplayName);
                    NestedCommandRemoved?.Invoke(this, e);
                };
                nestedIfControl.LogicCommandRequested += (s, e) =>
                {
                    LogicCommandRequested?.Invoke(this, e);
                };
                // 注意：不冒泡 NodeDeleteRequested，因为 NodeControlFactory 
                // 已经订阅了该事件并调用 RemoveNestedCommand 回调来处理删除
            }
            else if (nestedControl is LoopNodeControlView nestedLoopControl)
            {
                nestedLoopControl.ContentChanged += (s, e) =>
                {
                    _logger.LogDebug("IF节点内的嵌套Loop节点内容发生变化");
                    OnBranchContentChanged();
                };

                //  修复：冒泡嵌套命令事件到外层
                nestedLoopControl.NestedCommandAdded += (s, e) =>
                {
                    _logger.LogDebug("IF节点内的Loop添加了嵌套命令，冒泡事件: {Name}", e.NestedCommand?.DisplayName);
                    NestedCommandAdded?.Invoke(this, e);
                };
                nestedLoopControl.NestedCommandRemoved += (s, e) =>
                {
                    _logger.LogDebug("IF节点内的Loop移除了嵌套命令，冒泡事件: {Name}", e.NestedCommand?.DisplayName);
                    NestedCommandRemoved?.Invoke(this, e);
                };
                nestedLoopControl.LogicCommandRequested += (s, e) =>
                {
                    LogicCommandRequested?.Invoke(this, e);
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

                //  修复：删除前弹出确认框，包含子命令数量提示
                string message = $"确定要删除节点 \"{nestedNode.DisplayName}\" 吗？";
                if (nestedNode.Children != null && nestedNode.Children.Count > 0)
                {
                    message = $"该条件判断包含 {nestedNode.Children.Count} 个子命令，删除后所有子命令也将被移除。\n\n确定要删除节点 \"{nestedNode.DisplayName}\" 吗？";
                }

                var result = MessageBox.Show(
                    message,
                    "确认删除",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (result != MessageBoxResult.Yes) return;

                bool isTrueBranch = _trueBranchCommands.Contains(nestedNode);
                bool removed = false;

                // 从数据模型移除
                ifNode.RemoveChild(nestedNode);

                if (isTrueBranch)
                {
                    int logicalIndex = _trueBranchCommands.IndexOf(nestedNode);
                    _trueBranchCommands.Remove(nestedNode);
                    ifNode.RemoveChild(nestedNode);
                    RebuildBranchUI(TrueBranchContent, _trueBranchCommands);
                    //  修复：立即重置 Canvas 尺寸，避免残留旧的大尺寸
                    ResetBranchCanvasSize(TrueBranchContent, _trueBranchCommands);
                    TrueHint.Visibility = _trueBranchCommands.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                    removed = true;
                }
                else if (_falseBranchCommands.Contains(nestedNode))
                {
                    _falseBranchCommands.Remove(nestedNode);
                    ifNode.RemoveChild(nestedNode);
                    RebuildBranchUI(FalseBranchContent, _falseBranchCommands);
                    //  修复：立即重置 Canvas 尺寸，避免残留旧的大尺寸
                    ResetBranchCanvasSize(FalseBranchContent, _falseBranchCommands);
                    FalseHint.Visibility = _falseBranchCommands.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                    removed = true;
                }

                if (removed)
                {
                    NestedCommandRemoved?.Invoke(this, new NestedCommandEventArgs
                    {
                        NestedCommand = nestedNode,
                        IsTrueBranch = isTrueBranch,
                        ParentIfNode = ifNode
                    });

                    //  修复：先做一次即时布局更新（用估算尺寸），
                    //   然后延迟一帧等新控件渲染完毕后再做精确布局
                    UpdateBranchCanvasHeight();
                    ContentChanged?.Invoke(this, EventArgs.Empty);

                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        UpdateBranchCanvasHeight();
                        ContentChanged?.Invoke(this, EventArgs.Empty);
                    }), DispatcherPriority.Loaded);

                    _logger.LogInformation("命令 {CommandName} 已从 {Branch} 分支移除",
                        nestedNode.DisplayName, isTrueBranch ? "TRUE" : "FALSE");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "移除嵌套命令失败: {NodeName}", nestedNode?.DisplayName);
            }
        }

        /// <summary>
        /// 删除子节点后，立即将分支 Canvas 的尺寸重置为当前子节点的估算尺寸，
        /// 防止残留被删除节点（如嵌套IF）撑大的旧尺寸。
        /// </summary>
        private void ResetBranchCanvasSize(Canvas branchCanvas, List<CommandNode> commandList)
        {
            if (commandList.Count == 0)
            {
                branchCanvas.Width = 0;
                branchCanvas.Height = 0;
                return;
            }

            double totalWidth = 0;
            double maxHeight = 0;

            foreach (UIElement child in branchCanvas.Children)
            {
                double w = 0, h = 0;
                if (child is FrameworkElement fe)
                {
                    // 新建的控件还没渲染，ActualWidth/Height 为 0，使用 Width/Height 或估算值
                    w = fe.ActualWidth > 0 ? fe.ActualWidth : (double.IsNaN(fe.Width) ? 0 : fe.Width);
                    h = fe.ActualHeight > 0 ? fe.ActualHeight : (double.IsNaN(fe.Height) ? 0 : fe.Height);
                }
                if (w <= 0) w = 231;  // 普通节点默认宽度
                if (h <= 0) h = 64;   // 普通节点默认高度

                totalWidth += w + 8;  // 8 = CHILD_GAP
                maxHeight = Math.Max(maxHeight, h);
            }

            branchCanvas.Width = Math.Max(0, totalWidth - 8);
            branchCanvas.Height = maxHeight;
        }
        /// <summary>
        /// 完整重建分支UI，保证节点和连接线交替排列
        /// </summary>
        private void RebuildBranchUI(Canvas branchPanel, List<CommandNode> commandList)
        {
            branchPanel.Children.Clear();

            //  修复：清空后立即重置 Canvas 尺寸，防止残留旧值导致分支过高
            branchPanel.Width = 0;
            branchPanel.Height = 0;

            for (int i = 0; i < commandList.Count; i++)
            {
                if (i > 0)
                    branchPanel.Children.Add(CreateBranchConnector());
                var ctrl = _nodeControlFactory.CreateNestedCommandControl(
                    commandList[i], HandleNestedNodeClick, RemoveNestedCommand);
                ctrl.VerticalAlignment = VerticalAlignment.Center;
                SubscribeToNestedControlEvents(ctrl);
                branchPanel.Children.Add(ctrl);
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
                //  修复：清空后重置 Canvas 尺寸
                TrueBranchContent.Width = 0;
                TrueBranchContent.Height = 0;
                FalseBranchContent.Width = 0;
                FalseBranchContent.Height = 0;
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
        /// <summary>
        /// 创建分支内节点间的带箭头连接线（Canvas，水平方向）
        /// </summary>
        private UIElement CreateBranchConnector()
        {
            var connectorColor = Color.FromRgb(0xCC, 0xCE, 0xD3);
            var connectorBrush = new SolidColorBrush(connectorColor);

            // 容器：宽32（线24+箭头8），高按节点高度居中
            var canvas = new System.Windows.Controls.Canvas
            {
                Width = 32,
                Height = 64,   // 与节点同高，让 StackPanel 竖向对齐正确
                IsHitTestVisible = false,
                VerticalAlignment = VerticalAlignment.Center
            };

            // 水平线
            var line = new System.Windows.Shapes.Rectangle
            {
                Width = 24,
                Height = 2,
                Fill = connectorBrush
            };
            System.Windows.Controls.Canvas.SetLeft(line, 0);
            System.Windows.Controls.Canvas.SetTop(line, 31); // 64/2 - 1 = 31
            canvas.Children.Add(line);

            // 向右箭头三角形（与外层 TrueArrow / FalseArrow 一致）
            var arrow = new System.Windows.Shapes.Polygon
            {
                Fill = connectorBrush,
                Points = new System.Windows.Media.PointCollection
        {
            new System.Windows.Point(0, 0),
            new System.Windows.Point(0, 8),
            new System.Windows.Point(8, 4)
        }
            };
            System.Windows.Controls.Canvas.SetLeft(arrow, 24);
            System.Windows.Controls.Canvas.SetTop(arrow, 28); // 31 - 4 + 1 = 28，让箭头垂直居中
            canvas.Children.Add(arrow);

            return canvas;
        }
        // 布局常量
        private const double CARD_WIDTH = 231.0;
        private const double CARD_HEIGHT = 64.0;
        private const double BRANCH_GAP_V = 20.0;
        private const double BRANCH_OFFSET_X = 60.0;
        private const double MIN_BRANCH_WIDTH = 200.0;
        private const double MIN_BRANCH_HEIGHT = 76.0;

        /// <summary>
        /// 主卡片中心在 RootCanvas 内的 Y 偏移（外层 ViewModel 用来修正 node.Y）
        /// </summary>
        public double CardCenterOffsetY { get; private set; }

        private void UpdateBranchCanvasHeight()
        {
            try
            {
                var trueSize = CalculateBranchContentSize(TrueBranchContent);
                var falseSize = CalculateBranchContentSize(FalseBranchContent);

                const double WRAPPER_EXTRA_H = 38.0;
                const double WRAPPER_EXTRA_W = 18.0;

                double trueContentH = trueSize.Height;
                double falseContentH = falseSize.Height;

                //  修复：如果分支内有嵌套 IF，需要额外考虑其向上/向下展开的空间
                //   嵌套 IF 放在 StackPanel(Horizontal) 里时是 Top-aligned，
                //   但其主卡片在 CardCenterOffsetY 处，FALSE 分支在下方延伸。
                //   为了让嵌套 IF 的主卡片在分支容器内垂直居中对齐，
                //   需要确保容器高度足以容纳最大的"上半部分"和最大的"下半部分"。
                double trueMaxAbove = CARD_HEIGHT / 2.0;  // 普通节点：上半 = 32
                double trueMaxBelow = CARD_HEIGHT / 2.0;  // 普通节点：下半 = 32
                foreach (UIElement child in TrueBranchContent.Children)
                {
                    if (child is IfNodeControlView nestedIf && nestedIf.ActualHeight > 0)
                    {
                        double above = nestedIf.CardCenterOffsetY;  // 主卡中心到顶部
                        double below = nestedIf.ActualHeight - nestedIf.CardCenterOffsetY;  // 主卡中心到底部
                        trueMaxAbove = Math.Max(trueMaxAbove, above);
                        trueMaxBelow = Math.Max(trueMaxBelow, below);
                    }
                }
                // 分支容器需要的最小内容高度 = 最大上半 + 最大下半
                double trueNeededH = trueMaxAbove + trueMaxBelow;
                trueContentH = Math.Max(trueContentH, trueNeededH);

                double falseMaxAbove = CARD_HEIGHT / 2.0;
                double falseMaxBelow = CARD_HEIGHT / 2.0;
                foreach (UIElement child in FalseBranchContent.Children)
                {
                    if (child is IfNodeControlView nestedIf && nestedIf.ActualHeight > 0)
                    {
                        double above = nestedIf.CardCenterOffsetY;
                        double below = nestedIf.ActualHeight - nestedIf.CardCenterOffsetY;
                        falseMaxAbove = Math.Max(falseMaxAbove, above);
                        falseMaxBelow = Math.Max(falseMaxBelow, below);
                    }
                }
                double falseNeededH = falseMaxAbove + falseMaxBelow;
                falseContentH = Math.Max(falseContentH, falseNeededH);

                double trueH = Math.Max(MIN_BRANCH_HEIGHT, trueContentH + WRAPPER_EXTRA_H);
                double trueW = Math.Max(MIN_BRANCH_WIDTH, trueSize.Width + WRAPPER_EXTRA_W);
                double falseH = Math.Max(MIN_BRANCH_HEIGHT, falseContentH + WRAPPER_EXTRA_H);
                double falseW = Math.Max(MIN_BRANCH_WIDTH, falseSize.Width + WRAPPER_EXTRA_W);

                double branchX = CARD_WIDTH + BRANCH_OFFSET_X;

                // ── 主卡片固定在 Canvas.Top = 0，node.Y 就是主卡片顶部，外层无需修正 ──
                double cardTopInCanvas = 0;
                double cardCenterInCanvas = CARD_HEIGHT / 2.0;

                // TRUE 分支：底部与主卡片顶部之间留 BRANCH_GAP_V/2
                double trueBottom = cardTopInCanvas - BRANCH_GAP_V / 2.0;
                double trueTop = trueBottom - trueH;

                // FALSE 分支：顶部与主卡片底部之间留 BRANCH_GAP_V/2
                double falseTop = cardTopInCanvas + CARD_HEIGHT + BRANCH_GAP_V / 2.0;

                // TRUE/FALSE 分支的垂直中心
                double trueCenterInCanvas = trueTop + trueH / 2.0;
                double falseCenterInCanvas = falseTop + falseH / 2.0;

                // 用 offsetY 把所有坐标平移为非负值
                double offsetY = Math.Max(0, -trueTop);

                double cardTopFinal = cardTopInCanvas + offsetY;
                double trueTopFinal = trueTop + offsetY;
                double falseTopFinal = falseTop + offsetY;
                double cardCenterFinal = cardCenterInCanvas + offsetY;
                double trueCenterFinal = trueCenterInCanvas + offsetY;
                double falseCenterFinal = falseCenterInCanvas + offsetY;

                double canvasH = Math.Max(trueTopFinal + trueH, falseTopFinal + falseH);
                double maxBranchW = Math.Max(trueW, falseW);

                // 设置分支位置
                Canvas.SetLeft(TrueBranchWrapper, branchX);
                Canvas.SetTop(TrueBranchWrapper, trueTopFinal);
                TrueBranchWrapper.Width = trueW;

                Canvas.SetLeft(FalseBranchWrapper, branchX);
                Canvas.SetTop(FalseBranchWrapper, falseTopFinal);
                FalseBranchWrapper.Width = falseW;

                // 主卡片
                Canvas.SetLeft(MainStepCard, 2);
                Canvas.SetTop(MainStepCard, cardTopFinal+2);
                Canvas.SetLeft(ShadowBorder, 0);
                Canvas.SetTop(ShadowBorder, cardTopFinal);

                // 连线
                double forkX = CARD_WIDTH + BRANCH_OFFSET_X / 2.0;

                MainHorizontalLine.X1 = CARD_WIDTH; MainHorizontalLine.Y1 = cardCenterFinal;
                MainHorizontalLine.X2 = forkX; MainHorizontalLine.Y2 = cardCenterFinal;

                BranchVerticalLine.X1 = forkX; BranchVerticalLine.Y1 = trueCenterFinal;
                BranchVerticalLine.X2 = forkX; BranchVerticalLine.Y2 = falseCenterFinal;

                TrueHorizontalLine.X1 = forkX; TrueHorizontalLine.Y1 = trueCenterFinal;
                TrueHorizontalLine.X2 = branchX - 8; TrueHorizontalLine.Y2 = trueCenterFinal;
                Canvas.SetLeft(TrueArrow, branchX - 8);
                Canvas.SetTop(TrueArrow, trueCenterFinal - 4);

                FalseHorizontalLine.X1 = forkX; FalseHorizontalLine.Y1 = falseCenterFinal;
                FalseHorizontalLine.X2 = branchX - 8; FalseHorizontalLine.Y2 = falseCenterFinal;
                Canvas.SetLeft(FalseArrow, branchX - 8);
                Canvas.SetTop(FalseArrow, falseCenterFinal - 4);

                // RootCanvas 和 UserControl 尺寸
                RootCanvas.Width = branchX + maxBranchW + 16;
                RootCanvas.Height = canvasH;
                this.Width = RootCanvas.Width;
                this.Height = RootCanvas.Height;

                this.Margin = new Thickness(0);
                LayoutBranchChildren(TrueBranchContent, trueH);
                LayoutBranchChildren(FalseBranchContent, falseH);
                CardCenterOffsetY = cardCenterFinal;

                InvalidateMeasure();
                InvalidateArrange();
                UpdateLayout();

                LayoutChanged?.Invoke(this, CardCenterOffsetY);

                _logger.LogTrace("IF布局: Canvas={W}x{H}, offsetY={OY}, cardTop={CT}, trueTop={TT}, falseTop={FT}, CardCenterOffsetY={CCY}",
                    RootCanvas.Width, RootCanvas.Height, offsetY, cardTopFinal, trueTopFinal, falseTopFinal, CardCenterOffsetY);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "IF节点布局失败");
                RootCanvas.Width = 600; RootCanvas.Height = 200;
            }
        }

        // CalculateBranchContentSize 保留但内容改为测量横向 StackPanel
        private Size CalculateBranchContentSize(Canvas branchContent)
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
                        childW = fe.ActualWidth > 0 ? fe.ActualWidth : 0;
                        childH = fe.ActualHeight > 0 ? fe.ActualHeight : 0;

                        //  修复：嵌套 IF 直接用 ActualHeight（包含 TRUE 向上 + FALSE 向下的完整高度）
                        if (child is IfNodeControlView nestedIf && nestedIf.ActualHeight > 0)
                        {
                            childH = nestedIf.ActualHeight;
                        }
                    }

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

            if (childCount > 1) totalWidth += (childCount - 1) * 8;
            totalWidth += 32;

            return new Size(Math.Max(MIN_BRANCH_WIDTH, totalWidth),
                            Math.Max(0, maxHeight));
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
                }), System.Windows.Threading.DispatcherPriority.Render);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "OnBranchContentChanged 错误");
            }
        }
        /// <summary>
        /// 手动布局分支 Canvas 内的子控件，确保所有子节点的"主卡片中心"对齐到同一水平线。
        /// 替代 StackPanel 的自动居中对齐（自动居中按几何中心对齐，无法处理嵌套 IF 的 CardCenterOffsetY）。
        /// </summary>
        private void LayoutBranchChildren(Canvas branchCanvas, double branchWrapperHeight)
        {
            if (branchCanvas == null) return;

            //  修复：当分支为空时，必须将 Canvas 尺寸归零，
            //   否则会残留被删除节点撑大的旧 Height/Width
            if (branchCanvas.Children.Count == 0)
            {
                branchCanvas.Width = 0;
                branchCanvas.Height = 0;
                return;
            }

            try
            {
                // ── Step 1: 计算所有子控件的"主卡片中心到顶部"的最大值 ──
                // 普通节点：cardCenterFromTop = CARD_HEIGHT / 2 = 32
                // 嵌套 IF：cardCenterFromTop = CardCenterOffsetY
                // 连接线（Canvas 32x64）：cardCenterFromTop = 32
                double maxCenterFromTop = CARD_HEIGHT / 2.0;

                foreach (UIElement child in branchCanvas.Children)
                {
                    double centerFromTop = GetChildCardCenterFromTop(child);
                    maxCenterFromTop = Math.Max(maxCenterFromTop, centerFromTop);
                }

                // 对齐线 Y = 分支容器高度的中间（减去 Wrapper 额外的标题区域高度约 20px）
                // 实际上我们用 maxCenterFromTop 作为基准：
                // 所有子控件的"主卡片中心"都对齐到 Y = maxCenterFromTop
                double alignY = maxCenterFromTop;

                // ── Step 2: 水平排列并垂直对齐 ──
                double currentX = 0;
                const double CHILD_GAP = 8.0; // 子控件之间的水平间距

                foreach (UIElement child in branchCanvas.Children)
                {
                    double childW = 0, childH = 0;

                    if (child is FrameworkElement fe)
                    {
                        childW = fe.ActualWidth > 0 ? fe.ActualWidth : fe.Width;
                        childH = fe.ActualHeight > 0 ? fe.ActualHeight : fe.Height;

                        if (child is IfNodeControlView nestedIf)
                        {
                            if (nestedIf.ActualWidth > 0) childW = nestedIf.ActualWidth;
                            if (nestedIf.ActualHeight > 0) childH = nestedIf.ActualHeight;
                        }
                    }

                    if (childW <= 0 || childH <= 0)
                    {
                        child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                        if (childW <= 0) childW = child.DesiredSize.Width;
                        if (childH <= 0) childH = child.DesiredSize.Height;
                    }

                    if (childW <= 0) childW = 231;
                    if (childH <= 0) childH = 64;

                    // 计算此子控件的"主卡片中心距顶部"的偏移
                    double centerFromTop = GetChildCardCenterFromTop(child);

                    // 垂直位置：让此子控件的主卡片中心对齐到 alignY
                    double childY = alignY - centerFromTop;

                    Canvas.SetLeft(child, currentX);
                    Canvas.SetTop(child, childY);

                    currentX += childW + CHILD_GAP;
                }

                // ── Step 3: 设置 Canvas 尺寸 ──
                double totalWidth = Math.Max(0, currentX - CHILD_GAP); // 去掉最后一个间距

                // Canvas 高度 = 对齐线上方最大空间 + 对齐线下方最大空间
                double maxBelow = 0;
                foreach (UIElement child in branchCanvas.Children)
                {
                    double childH = 0;
                    if (child is FrameworkElement fe2)
                        childH = fe2.ActualHeight > 0 ? fe2.ActualHeight : (fe2.Height > 0 ? fe2.Height : 64);
                    if (childH <= 0)
                    {
                        child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                        childH = child.DesiredSize.Height > 0 ? child.DesiredSize.Height : 64;
                    }

                    double centerFromTop = GetChildCardCenterFromTop(child);
                    double below = childH - centerFromTop;
                    maxBelow = Math.Max(maxBelow, below);
                }

                double canvasHeight = maxCenterFromTop + maxBelow;
                branchCanvas.Width = totalWidth;
                branchCanvas.Height = canvasHeight;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "LayoutBranchChildren 失败");
            }
        }

        /// <summary>
        /// 获取子控件的"主卡片中心距控件顶部"的距离。
        /// 普通节点 = CARD_HEIGHT / 2 = 32
        /// 嵌套 IF = CardCenterOffsetY
        /// 连接线 = 32 (固定 64px 高的连接线 Canvas)
        /// </summary>
        private double GetChildCardCenterFromTop(UIElement child)
        {
            if (child is IfNodeControlView nestedIf && nestedIf.CardCenterOffsetY > 0)
            {
                return nestedIf.CardCenterOffsetY;
            }

            // 普通节点或连接线
            double h = CARD_HEIGHT; // 默认 64
            if (child is FrameworkElement fe)
            {
                double actualH = fe.ActualHeight > 0 ? fe.ActualHeight : fe.Height;
                if (actualH > 0) h = actualH;
            }
            return h / 2.0;
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
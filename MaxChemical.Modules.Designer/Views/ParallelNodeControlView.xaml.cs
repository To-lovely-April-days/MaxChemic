using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using MaxChemical.Modules.Designer.Models;
using DevicePlugins.Devices;
using MaxChemical.DataModule.Model;
using System.Windows.Media.Imaging;
using System.Diagnostics;
using MaxChemical.Modules.Designer.ViewModels;
using System.Windows.Data;
using MaxChemical.Logging;
using MaxChemical.Modules.Designer.Events;
using MaxChemical.Modules.Designer.Services;

namespace MaxChemical.Modules.Designer.Views
{
    /// <summary>
    /// ParallelNodeControlView.xaml 的交互逻辑
    /// 样式3 · 大括号包裹: 主卡片 → { → 垂直排列的水平分支行 → }
    /// </summary>
    public partial class ParallelNodeControlView : UserControl, IDisposable
    {
        public event EventHandler ContentChanged;
        public event EventHandler<NestedCommandEventArgs> NestedCommandAdded;
        public event EventHandler<NestedCommandEventArgs> NestedCommandRemoved;
        public event EventHandler<LogicCommandRequestEventArgs> LogicCommandRequested;
        public event EventHandler<CommandNode> NodeDeleteRequested;
        public event EventHandler<CommandNode> NodeClicked;

        private Size _lastMeasuredSize;
        private readonly List<ParallelBranchInfo> _parallelBranches = new List<ParallelBranchInfo>();
        private int _currentMaxParallelCount = 0;

        private int _currentInsertionIndex = -1;
        private int _currentBranchIndex = -1;
        private CommandNode _highlightedNode; // 当前高亮的节点
        private UserControl _highlightedControl; // 当前高亮的控件

        private readonly ILogService _logger;
        private readonly NodeControlFactory _nodeControlFactory;
        private readonly DesignerDragDropService _dragDropService;
        private readonly NodeManagementService _nodeManagementService;
        /// <summary>
        /// 整个并行节点控件的实际总宽度（包括主卡片+连线+花括号+分支）
        /// </summary>
        public double TotalActualWidth
        {
            get
            {
                try
                {
                    if (RootGrid.ActualWidth > 0)
                        return RootGrid.ActualWidth;

                    // 兜底：手动累加各列宽度
                    double total = 0;
                    foreach (var col in RootGrid.ColumnDefinitions)
                    {
                        total += col.ActualWidth;
                    }
                    return total > 0 ? total : NodeSizeEstimator.PARALLEL_NODE_MIN_WIDTH;
                }
                catch
                {
                    return NodeSizeEstimator.PARALLEL_NODE_MIN_WIDTH;
                }
            }
        }
        // 分支行颜色（参照样式3 SVG中的配色）
        private static readonly Color[] BranchColors = new[]
        {
            Color.FromRgb(0x37, 0x8A, 0xDD), // 蓝色
            Color.FromRgb(0x1D, 0x9E, 0x75), // 青绿色
            Color.FromRgb(0xD8, 0x5A, 0x30), // 珊瑚色
            Color.FromRgb(0x9B, 0x59, 0xB6), // 紫色
            Color.FromRgb(0xE6, 0x7E, 0x22), // 橙色
            Color.FromRgb(0x27, 0xAE, 0x60), // 绿色
            Color.FromRgb(0xC0, 0x39, 0x2B), // 红色
            Color.FromRgb(0x29, 0x80, 0xB9), // 深蓝
        };

        public ParallelNodeControlView()
        {
            InitializeComponent();

            // 初始化服务
            _logger = new LogService().ForContext<ParallelNodeControlView>();
            _nodeControlFactory = new NodeControlFactory(_logger);
            _dragDropService = new DesignerDragDropService(_logger);
            _nodeManagementService = new NodeManagementService(_logger);

            Loaded += OnLoaded;
            SizeChanged += OnSizeChanged;
            DataContextChanged += OnDataContextChanged;
        }


        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            InitializeParallelBranches();
            //  加载完成后再次强制刷新，确保外层Canvas正确测量
            Dispatcher.BeginInvoke(new Action(() =>
            {
                InvalidateMeasure();
                InvalidateArrange();
                UpdateLayout();
                UpdateBraceLayout();
                ContentChanged?.Invoke(this, EventArgs.Empty);
            }), System.Windows.Threading.DispatcherPriority.Render);
        }

        private void OnSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (e.NewSize != _lastMeasuredSize)
            {
                _lastMeasuredSize = e.NewSize;
                UpdateBraceLayout();
            }
        }

        private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (e.NewValue is CommandNode node && node.LogicCommand != null)
            {
                var maxParallelCount = GetMaxParallelCount(node);
                if (maxParallelCount != _currentMaxParallelCount)
                {
                    _currentMaxParallelCount = maxParallelCount;
                    InitializeParallelBranches();
                }

                SyncChildrenToUI();
            }
        }

        private int GetMaxParallelCount(CommandNode node)
        {
            try
            {
                if (node.LogicCommand?.Properties != null &&
                    node.LogicCommand.Properties.TryGetValue("MaxParallelCount", out var value))
                {
                    if (value is int intValue)
                        return intValue;
                    if (int.TryParse(value.ToString(), out var parsedValue))
                        return parsedValue;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取MaxParallelCount失败");
            }
            return 4;
        }

        private void SyncChildrenToUI()
        {
            if (DataContext is not CommandNode parallelNode) return;

            try
            {
                _logger.LogDebug($"=== 同步并行节点Children到UI: {parallelNode.DisplayName} ===");
                _logger.LogDebug($"Children数量: {parallelNode.Children.Count}");

                // 清空所有分支的UI
                foreach (var branchInfo in _parallelBranches)
                {
                    var branchContent = FindBranchContentContainer(branchInfo.Index);
                    if (branchContent != null)
                    {
                        branchContent.Children.Clear();
                    }
                    branchInfo.Commands.Clear();
                }

                // 重新分组并创建UI
                var branchGroups = parallelNode.Children
                    .GroupBy(child => child.GetExecutionProperty<int>("BranchIndex"))
                    .Where(group => group.Key >= 0 && group.Key < _parallelBranches.Count)
                    .ToList();

                foreach (var branchGroup in branchGroups)
                {
                    int branchIndex = branchGroup.Key;
                    var branchInfo = _parallelBranches[branchIndex];
                    var branchContent = FindBranchContentContainer(branchIndex);

                    if (branchContent == null)
                    {
                        _logger.LogWarning($"未找到分支 {branchIndex} 的内容容器");
                        continue;
                    }

                    _logger.LogDebug($"处理分支 {branchIndex}，子节点数: {branchGroup.Count()}");

                    int commandIndex = 0;
                    foreach (var child in branchGroup.OrderBy(c => parallelNode.Children.IndexOf(c)))
                    {
                        branchInfo.Commands.Add(child);

                        var nestedControl = _nodeControlFactory.CreateNestedCommandControl(
                            child,
                            HandleNestedNodeClick,
                            (node) => RemoveNestedCommand(node, branchIndex));

                        if (nestedControl != null)
                        {  //  新增：确保嵌套控件在分支行内垂直居中
                            nestedControl.VerticalAlignment = VerticalAlignment.Center;
                            // 如果不是第一个命令，先插入箭头
                            if (commandIndex > 0)
                            {
                                branchContent.Children.Add(CreateBranchArrow());
                            }
                            branchContent.Children.Add(nestedControl);
                            _logger.LogDebug($"为子节点 {child.DisplayName} 创建了UI控件");
                            SubscribeToNestedComplexNodeEvents(nestedControl, child, branchIndex);
                            commandIndex++;
                        }
                        else
                        {
                            _logger.LogError($"为子节点 {child.DisplayName} 创建UI控件失败");
                        }
                    }

                    // 更新提示文本的可见性
                    var hint = FindBranchHint(branchIndex);
                    if (hint != null)
                    {
                        hint.Visibility = branchContent.Children.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                    }
                }

                UpdateBraceLayout();
                //  强制重新测量
                InvalidateMeasure();
                InvalidateArrange();
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    UpdateLayout();
                    UpdateBraceLayout();
                    ContentChanged?.Invoke(this, EventArgs.Empty);
                }), System.Windows.Threading.DispatcherPriority.Loaded);
                _logger.LogDebug("=== 并行节点Children同步到UI完成 ===");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "同步并行节点Children到UI失败");
            }
        }


        #region 并行分支管理

        private void InitializeParallelBranches()
        {
            try
            {
                if (DataContext is not CommandNode node || node.LogicCommand == null)
                    return;

                var maxParallelCount = GetMaxParallelCount(node);
                ClearAllBranches();

                for (int i = 0; i < maxParallelCount; i++)
                {
                    CreateParallelBranch(i);
                }

                UpdateBraceLayout();

                //  关键修复：强制整个控件重新测量和排列
                InvalidateMeasure();
                InvalidateArrange();

                // 延迟一帧再次更新，确保布局系统完成
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    UpdateLayout();
                    UpdateBraceLayout();
                    ContentChanged?.Invoke(this, EventArgs.Empty);
                }), System.Windows.Threading.DispatcherPriority.Loaded);

                _logger.LogDebug("创建了 {Count} 个并行分支", maxParallelCount);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "初始化并行分支失败");
            }
        }


        /// <summary>
        /// 创建一个并行分支 —— 样式3: 水平行布局
        /// 每个分支是一行，内部命令从左到右水平排列
        /// </summary>
        private void CreateParallelBranch(int branchIndex)
        {
            var branchInfo = new ParallelBranchInfo
            {
                Index = branchIndex,
                Commands = new List<CommandNode>()
            };

            var branchContainer = CreateBranchRowContainer(branchIndex);
            branchInfo.Container = branchContainer;

            ParallelBranchesContainer.Children.Add(branchContainer);
            _parallelBranches.Add(branchInfo);
        }

        /// <summary>
        /// 创建分支行容器 —— 水平排列的一行
        /// 结构: [分支标签] [拖放区域(水平StackPanel)]
        /// </summary>
        private Border CreateBranchRowContainer(int branchIndex)
        {
            Debug.WriteLine($"=== 创建分支行容器 {branchIndex} ===");

            var branchColor = BranchColors[branchIndex % BranchColors.Length];
            var branchBrush = new SolidColorBrush(branchColor);

            // 外层包裹
            var branchWrapper = new Border
            {
                MinHeight = 36,
                Margin = new Thickness(0, 3, 0, 3),
                Padding = new Thickness(0),
            };

            // 水平布局: 分支标签 + 拖放区域
            var rowPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
            };

            // 分支标签（带颜色标识）
            var branchLabel = new Border
            {
                Width = 4,
                MinHeight = 30,
                CornerRadius = new CornerRadius(2),
                Background = branchBrush,
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Stretch,
                Opacity = 0.7,
            };

            // 拖放区域
            var dropZone = new Border
            {
                Name = $"BranchDropZone_{branchIndex}",
                Background = new SolidColorBrush(Color.FromArgb(0x06, branchColor.R, branchColor.G, branchColor.B)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x20, branchColor.R, branchColor.G, branchColor.B)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(6, 4, 6, 4),
                MinHeight = 36,
                MinWidth = 120,
                AllowDrop = true,
                UseLayoutRounding = true,
                SnapsToDevicePixels = true,
                Tag = branchIndex
            };

            Debug.WriteLine($"创建拖放区域，AllowDrop: {dropZone.AllowDrop}, Tag: {dropZone.Tag}");

            dropZone.DragEnter += Branch_DragEnter;
            dropZone.DragOver += Branch_DragOver;
            dropZone.DragLeave += Branch_DragLeave;
            dropZone.Drop += Branch_Drop;

            // 拖放区域内部: Grid包含提示文本和水平排列的命令
            var dropZoneGrid = new Grid
            {
                UseLayoutRounding = true
            };

            var hint = new TextBlock
            {
                Name = $"BranchHint_{branchIndex}",
                Text = $"拖拽步骤到分支 {branchIndex + 1}",
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(Color.FromArgb(0x60, branchColor.R, branchColor.G, branchColor.B)),
                FontSize = 11,
                FontStyle = FontStyles.Italic,
                FontFamily = new FontFamily("Segoe UI"),
                UseLayoutRounding = true,
                Margin = new Thickness(8, 0, 8, 0),
            };

            // 水平排列的命令容器（核心改变：从垂直变水平）
            var branchContent = new StackPanel
            {
                Name = $"BranchContent_{branchIndex}",
                Orientation = Orientation.Horizontal,
                UseLayoutRounding = true,
                VerticalAlignment = VerticalAlignment.Center,
            };

            dropZoneGrid.Children.Add(hint);
            dropZoneGrid.Children.Add(branchContent);
            dropZone.Child = dropZoneGrid;

            rowPanel.Children.Add(branchLabel);
            rowPanel.Children.Add(dropZone);
            branchWrapper.Child = rowPanel;

            Debug.WriteLine($"=== 分支行容器 {branchIndex} 创建完成 ===");
            return branchWrapper;
        }

        #endregion

        /// <summary>
        /// 删除按钮点击事件
        /// </summary>
        private void DeleteButton_Click(object sender, MouseButtonEventArgs e)
        {
            try
            {
                if (DataContext is CommandNode parallelNode)
                {
                    _logger.LogDebug("请求删除并行节点: {NodeName}", parallelNode.DisplayName);
                    NodeDeleteRequested?.Invoke(this, parallelNode);
                    e.Handled = true;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "删除并行节点失败");
            }
        }

        #region 拖拽事件处理

        private void Branch_DragEnter(object sender, DragEventArgs e)
        {
            _logger.LogTrace("Branch_DragEnter 被调用");

            if (sender is Border border && _dragDropService.CanAcceptDrop(e.Data))
            {
                _dragDropService.SetDragOverStyle(border);
                e.Effects = DragDropEffects.Copy;
                _logger.LogTrace("设置拖放效果为 Copy");
            }
            else
            {
                e.Effects = DragDropEffects.None;
                _logger.LogTrace("拒绝拖放");
            }
            e.Handled = true;
        }

        private void Branch_DragOver(object sender, DragEventArgs e)
        {
            if (_dragDropService.CanAcceptDrop(e.Data))
            {
                e.Effects = DragDropEffects.Copy;

                // 计算插入位置并高亮前一个节点
                if (sender is Border border && border.Tag is int branchIndex)
                {
                    var branchContent = FindBranchContentContainer(branchIndex);
                    if (branchContent != null)
                    {
                        var insertionInfo = GetInsertionInfo(e.GetPosition(branchContent), branchIndex);
                        HighlightPreviousNode(insertionInfo, branchIndex);

                        _currentInsertionIndex = insertionInfo.InsertionIndex;
                        _currentBranchIndex = branchIndex;
                    }
                }
            }
            else
            {
                e.Effects = DragDropEffects.None;
            }
            e.Handled = true;
        }

        private void Branch_DragLeave(object sender, DragEventArgs e)
        {
            _logger.LogTrace("Branch_DragLeave 被调用");
            if (sender is Border border)
            {
                _dragDropService.ClearDragOverStyle(border);
                _logger.LogTrace("清除拖放悬停状态");
            }
            ClearHighlight();
            e.Handled = true;
        }


        private void Branch_Drop(object sender, DragEventArgs e)
        {
            _logger.LogDebug("Branch_Drop 被调用");

            if (sender is Border border)
            {
                _dragDropService.ClearDragOverStyle(border);
                _logger.LogTrace("清除拖放状态");

                int insertionIndex = _currentInsertionIndex;
                int branchIndex = _currentBranchIndex;
                ClearHighlight();

                if (border.Tag is int tagBranchIndex)
                {
                    _logger.LogDebug("分支索引: {BranchIndex}, 插入索引: {InsertionIndex}", tagBranchIndex, insertionIndex);
                    HandleCommandDrop(e, tagBranchIndex, insertionIndex);
                }
                else
                {
                    _logger.LogWarning("Border.Tag 不是 int 类型: {Type}，值: {Value}",
                        border.Tag?.GetType().Name ?? "null", border.Tag);
                }
            }
            else
            {
                _logger.LogWarning("Sender 不是 Border 类型");
            }
            e.Handled = true;
        }

        private void HandleCommandDrop(DragEventArgs e, int branchIndex, int insertionIndex = -1)
        {
            try
            {
                _logger.LogDebug("处理分支 {BranchIndex} 位置 {InsertionIndex} 的命令拖放", branchIndex, insertionIndex);

                var command = _dragDropService.ExtractCommandFromDragData(e.Data);

                if (command is Services.PluginIdRequest pluginRequest)
                {
                    var args = new LogicCommandRequestEventArgs { PluginId = pluginRequest.PluginId };
                    LogicCommandRequested?.Invoke(this, args);
                    command = args.ResultCommand;
                }

                // ★ 直接从 dragData 里取 SourceDeviceType
                string sourceDeviceType = ExtractSourceDeviceTypeFromDragData(e.Data);

                if (command != null)
                {
                    _logger.LogDebug("提取到命令: {Type}, 来源设备类型: {SrcType}",
                        command.GetType().Name, sourceDeviceType ?? "(null)");
                    AddCommandToBranch(command, branchIndex, insertionIndex, sourceDeviceType);
                }
                else
                {
                    _logger.LogWarning("未能提取到有效命令");
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


        #endregion

        #region 高亮逻辑（并行分支版本 - 水平方向）

        private class InsertionInfo
        {
            public int InsertionIndex { get; set; }
            public CommandNode PreviousNode { get; set; }
            public UserControl PreviousControl { get; set; }
            public bool IsInsertAtBeginning { get; set; }
        }

        /// <summary>
        /// 计算插入位置 —— 水平方向：根据鼠标X坐标判断插入位置
        /// </summary>
        private InsertionInfo GetInsertionInfo(Point mousePosition, int branchIndex)
        {
            try
            {
                var info = new InsertionInfo();

                if (branchIndex < 0 || branchIndex >= _parallelBranches.Count)
                {
                    info.InsertionIndex = 0;
                    info.IsInsertAtBeginning = true;
                    return info;
                }

                var branchCommands = _parallelBranches[branchIndex].Commands;

                if (branchCommands.Count == 0)
                {
                    info.InsertionIndex = 0;
                    info.IsInsertAtBeginning = true;
                    return info;
                }

                var branchContent = FindBranchContentContainer(branchIndex);
                if (branchContent == null)
                {
                    info.InsertionIndex = 0;
                    info.IsInsertAtBeginning = true;
                    return info;
                }

                double cumulativeWidth = 0;

                for (int i = 0; i < branchCommands.Count; i++)
                {
                    var node = branchCommands[i];
                    var control = FindControlForNodeInBranch(branchContent, node);

                    if (control != null)
                    {
                        double controlWidth = control.ActualWidth;
                        if (controlWidth == 0)
                        {
                            control.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                            controlWidth = control.DesiredSize.Width;
                        }
                        if (controlWidth == 0) controlWidth = 120; // 默认宽度

                        // 水平方向：检查鼠标是否在当前控件的左半部分
                        if (mousePosition.X <= cumulativeWidth + controlWidth / 2)
                        {
                            info.InsertionIndex = i;
                            if (i == 0)
                            {
                                info.IsInsertAtBeginning = true;
                            }
                            else
                            {
                                info.PreviousNode = branchCommands[i - 1];
                                info.PreviousControl = FindControlForNodeInBranch(branchContent, info.PreviousNode);
                            }

                            _logger.LogTrace("鼠标位置 {X} 在分支 {BranchIndex} 节点 {Index} 左半部分，插入位置: {InsertIndex}",
                                mousePosition.X, branchIndex, i, i);
                            return info;
                        }

                        cumulativeWidth += controlWidth + 8; // 8是margin
                    }
                }

                // 如果鼠标在所有元素右方，插入到末尾
                info.InsertionIndex = branchCommands.Count;
                info.PreviousNode = branchCommands[branchCommands.Count - 1];
                info.PreviousControl = FindControlForNodeInBranch(branchContent, info.PreviousNode);

                _logger.LogTrace("鼠标位置 {X} 在分支 {BranchIndex} 所有节点右方，插入位置: {InsertIndex}",
                    mousePosition.X, branchIndex, branchCommands.Count);
                return info;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "计算插入信息失败");
                return new InsertionInfo { InsertionIndex = 0, IsInsertAtBeginning = true };
            }
        }

        private UserControl FindControlForNodeInBranch(StackPanel branchContent, CommandNode node)
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

        private void HighlightPreviousNode(InsertionInfo insertionInfo, int branchIndex)
        {
            try
            {
                ClearHighlight();

                if (insertionInfo.IsInsertAtBeginning)
                {
                    _logger.LogTrace("插入到分支 {BranchIndex} 开头，无需高亮节点", branchIndex);
                    return;
                }

                if (insertionInfo.PreviousNode != null)
                {
                    _highlightedNode = insertionInfo.PreviousNode;
                    _highlightedNode.IsInsertHighlight = true;

                    _logger.LogTrace("高亮分支 {BranchIndex} 的前一个节点: {NodeName}", branchIndex, _highlightedNode.DisplayName);
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
                _currentBranchIndex = -1;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "清除节点高亮失败");
            }
        }



        #endregion

        #region 命令管理

        private void AddCommandToBranch(object command, int branchIndex, int insertionIndex = -1, string sourceDeviceType = null)
        {
            try
            {
                if (branchIndex < 0 || branchIndex >= _parallelBranches.Count)
                    return;

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

                if (nestedNode != null && DataContext is CommandNode parallelNode)
                {
                    parallelNode.AddChildToParallelBranch(nestedNode, branchIndex);

                    var branchInfo = _parallelBranches[branchIndex];

                    if (insertionIndex >= 0 && insertionIndex <= branchInfo.Commands.Count)
                    {
                        branchInfo.Commands.Insert(insertionIndex, nestedNode);
                        _logger.LogDebug("在分支 {BranchIndex} 位置 {InsertionIndex} 插入命令: {NodeName}",
                            branchIndex, insertionIndex, nestedNode.DisplayName);
                    }
                    else
                    {
                        branchInfo.Commands.Add(nestedNode);
                        _logger.LogDebug("添加命令到分支 {BranchIndex} 末尾: {NodeName}", branchIndex, nestedNode.DisplayName);
                    }

                    var nestedControl = _nodeControlFactory.CreateNestedCommandControl(
                        nestedNode,
                        HandleNestedNodeClick,
                        (node) => RemoveNestedCommand(node, branchIndex));
                    //  新增：确保嵌套控件在分支行内垂直居中
                    nestedControl.VerticalAlignment = VerticalAlignment.Center;
                    SubscribeToNestedComplexNodeEvents(nestedControl, nestedNode, branchIndex);

                    var branchContent = FindBranchContentContainer(branchIndex);
                    if (branchContent != null)
                    {
                        // 计算当前分支中已有的命令数量（不含箭头）
                        int existingCommandCount = branchContent.Children
                            .OfType<UIElement>()
                            .Count(c => !IsBranchArrow(c));

                        if (insertionIndex >= 0 && insertionIndex <= branchInfo.Commands.Count)
                        {
                            // 计算在 StackPanel 中的实际插入位置（考虑箭头元素）
                            // 每个命令前面都有一个箭头（除了第一个），所以：
                            // 命令0 在位置 0，命令1 在位置 2（箭头+命令），命令2 在位置 4...
                            int uiInsertIndex;
                            if (insertionIndex == 0)
                            {
                                uiInsertIndex = 0;
                                // 如果分支里已有命令，需要在新命令后面加箭头
                                if (existingCommandCount > 0)
                                {
                                    branchContent.Children.Insert(0, CreateBranchArrow());
                                    branchContent.Children.Insert(0, nestedControl);
                                }
                                else
                                {
                                    branchContent.Children.Insert(0, nestedControl);
                                }
                            }
                            else
                            {
                                // 插入到中间位置：先插入箭头，再插入命令
                                uiInsertIndex = insertionIndex * 2 - 1; // 箭头位置
                                if (uiInsertIndex > branchContent.Children.Count)
                                    uiInsertIndex = branchContent.Children.Count;

                                branchContent.Children.Insert(uiInsertIndex, nestedControl);
                                branchContent.Children.Insert(uiInsertIndex, CreateBranchArrow());
                            }
                        }
                        else
                        {
                            // 添加到末尾
                            if (existingCommandCount > 0)
                            {
                                // 前面已有命令，先加箭头再加命令
                                branchContent.Children.Add(CreateBranchArrow());
                            }
                            branchContent.Children.Add(nestedControl);
                        }

                        var hint = FindBranchHint(branchIndex);
                        if (hint != null)
                        {
                            hint.Visibility = Visibility.Collapsed;
                        }
                    }

                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        UpdateBraceLayout();
                        OnBranchContentChanged();

                        NestedCommandAdded?.Invoke(this, new NestedCommandEventArgs
                        {
                            NestedCommand = nestedNode,
                            IsTrueBranch = false,
                            ParentIfNode = parallelNode,
                            BranchIndex = branchIndex
                        });

                    }), System.Windows.Threading.DispatcherPriority.Loaded);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "添加嵌套命令失败");
            }
        }
        /// <summary>
        /// 创建分支内命令之间的水平箭头连接线
        /// 样式与主流程箭头一致：灰色细线 + 向右三角箭头
        /// </summary>
        private UIElement CreateBranchArrow()
        {
            // 容器 Canvas，宽30，高10，与主流程箭头风格一致
            var canvas = new Canvas
            {
                Width = 30,
                Height = 10,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(2, 0, 2, 0),
                //  用 Tag 标记为箭头，方便后续识别和删除
                Tag = "BranchArrow"
            };

            // 水平线
            var line = new Rectangle
            {
                Width = 22,
                Height = 1,
                Fill = new SolidColorBrush(Color.FromRgb(0x97, 0x97, 0x97))
            };
            Canvas.SetLeft(line, 0);
            Canvas.SetTop(line, 4);
            canvas.Children.Add(line);

            // 向右箭头三角形 (与主流程 Data="M0,0 L0,8 L8,4 Z" 一致)
            var arrow = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("M0,0 L0,8 L8,4 Z"),
                Fill = new SolidColorBrush(Color.FromRgb(0x97, 0x97, 0x97))
            };
            Canvas.SetLeft(arrow, 22);
            Canvas.SetTop(arrow, 1);
            canvas.Children.Add(arrow);

            return canvas;
        }
        /// <summary>
        /// 判断一个 UI 元素是否是分支箭头
        /// </summary>
        private bool IsBranchArrow(UIElement element)
        {
            return element is Canvas canvas && canvas.Tag is string tag && tag == "BranchArrow";
        }
        private void SubscribeToNestedComplexNodeEvents(UserControl nestedControl, CommandNode nestedNode, int branchIndex)
        {
            try
            {
                _logger.LogDebug("订阅嵌套节点事件: {NodeType} - {NodeName}",
                    nestedControl.GetType().Name, nestedNode.DisplayName);

                switch (nestedControl)
                {
                    case ParallelNodeControlView nestedParallelControl:
                        _logger.LogDebug("订阅嵌套并行节点事件: {NodeName}", nestedNode.DisplayName);

                        nestedParallelControl.ContentChanged += (s, e) =>
                        {
                            _logger.LogDebug("并行节点内的嵌套并行节点内容发生变化: {NodeName}", nestedNode.DisplayName);
                            OnBranchContentChanged();
                        };

                        nestedParallelControl.NestedCommandAdded += (s, e) =>
                        {
                            _logger.LogDebug("并行节点内的嵌套并行节点添加了命令: {NestedCommand}", e.NestedCommand.DisplayName);
                            OnBranchContentChanged();
                        };

                        nestedParallelControl.NestedCommandRemoved += (s, e) =>
                        {
                            _logger.LogDebug("并行节点内的嵌套并行节点移除了命令: {NestedCommand}", e.NestedCommand.DisplayName);
                            OnBranchContentChanged();
                        };
                        nestedParallelControl.SizeChanged += (s, e) =>
                        {
                            if (e.HeightChanged) OnBranchContentChanged();
                        };
                        break;

                    case Views.IfNodeControlView nestedIfControl:
                        _logger.LogDebug("订阅嵌套IF节点事件: {NodeName}", nestedNode.DisplayName);

                        nestedIfControl.ContentChanged += (s, e) =>
                        {
                            _logger.LogDebug("并行节点内的嵌套IF节点内容发生变化: {NodeName}", nestedNode.DisplayName);
                            OnBranchContentChanged();
                        };

                        nestedIfControl.NestedCommandAdded += (s, e) =>
                        {
                            _logger.LogDebug("并行节点内的嵌套IF节点添加了命令: {NestedCommand}", e.NestedCommand.DisplayName);
                            OnBranchContentChanged();
                        };

                        nestedIfControl.NestedCommandRemoved += (s, e) =>
                        {
                            _logger.LogDebug("并行节点内的嵌套IF节点移除了命令: {NestedCommand}", e.NestedCommand.DisplayName);
                            OnBranchContentChanged();
                        };

                        //  新增：订阅IF控件的SizeChanged，当嵌套IF导致高度变化时
                        // 强制同分支内的其他节点重新居中对齐
                        nestedIfControl.SizeChanged += (s, e) =>
                        {
                            if (e.HeightChanged)
                            {
                                _logger.LogDebug("嵌套IF节点高度变化: {NodeName}, {OldH} -> {NewH}",
                                    nestedNode.DisplayName, e.PreviousSize.Height, e.NewSize.Height);
                                OnBranchContentChanged();
                            }
                        };
                        break;

                    case Views.LoopNodeControlView nestedLoopControl:
                        _logger.LogDebug("订阅嵌套循环节点事件: {NodeName}", nestedNode.DisplayName);

                        nestedLoopControl.ContentChanged += (s, e) =>
                        {
                            _logger.LogDebug("并行节点内的嵌套循环节点内容发生变化: {NodeName}", nestedNode.DisplayName);
                            OnBranchContentChanged();
                        };

                        nestedLoopControl.NestedCommandAdded += (s, e) =>
                        {
                            _logger.LogDebug("并行节点内的嵌套循环节点添加了命令: {NestedCommand}", e.NestedCommand.DisplayName);
                            OnBranchContentChanged();
                        };

                        nestedLoopControl.NestedCommandRemoved += (s, e) =>
                        {
                            _logger.LogDebug("并行节点内的嵌套循环节点移除了命令: {NestedCommand}", e.NestedCommand.DisplayName);
                            OnBranchContentChanged();
                        };
                        nestedLoopControl.SizeChanged += (s, e) =>
                        {
                            if (e.HeightChanged) OnBranchContentChanged();
                        };
                        break;

                    default:
                        _logger.LogTrace("普通嵌套节点不需要特殊事件订阅: {NodeType}", nestedControl.GetType().Name);
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "订阅嵌套复杂节点事件失败: {NodeName}", nestedNode?.DisplayName);
            }
        }

        public void RemoveNestedCommand(CommandNode nestedNode, int branchIndex)
        {
            try
            {
                if (branchIndex < 0 || branchIndex >= _parallelBranches.Count)
                    return;

                if (DataContext is not CommandNode parallelNode) return;

                var branchInfo = _parallelBranches[branchIndex];
                if (!branchInfo.Commands.Contains(nestedNode))
                    return;

                parallelNode.RemoveChild(nestedNode);
                branchInfo.Commands.Remove(nestedNode);

                var branchContent = FindBranchContentContainer(branchIndex);
                if (branchContent != null)
                {
                    UserControl controlToRemove = null;

                    controlToRemove = branchContent.Children
                        .OfType<UserControl>()
                        .FirstOrDefault(c => c.Tag == nestedNode);

                    if (controlToRemove == null)
                    {
                        controlToRemove = branchContent.Children
                            .OfType<UserControl>()
                            .FirstOrDefault(c => c.DataContext == nestedNode);
                    }

                    if (controlToRemove == null)
                    {
                        foreach (UIElement child in branchContent.Children)
                        {
                            if (child is UserControl control)
                            {
                                if (control.GetType().Name == "DeviceCommandControlView" &&
                                    control.DataContext == nestedNode)
                                {
                                    controlToRemove = control;
                                    break;
                                }

                                if ((control is Views.IfNodeControlView ||
                                     control is Views.ParallelNodeControlView ||
                                     control is Views.LoopNodeControlView) &&
                                    control.DataContext == nestedNode)
                                {
                                    controlToRemove = control;
                                    break;
                                }
                            }
                        }
                    }

                    if (controlToRemove != null)
                    {
                        int controlIndex = branchContent.Children.IndexOf(controlToRemove);

                        //  删除相邻的箭头
                        // 情况1：命令后面有箭头（不是最后一个命令）→ 删除后面的箭头
                        // 情况2：命令前面有箭头（不是第一个命令）→ 删除前面的箭头
                        // 情况3：只有一个命令，没有箭头
                        if (controlIndex >= 0)
                        {
                            // 先删除命令控件
                            branchContent.Children.RemoveAt(controlIndex);

                            // 删除后，检查相邻位置是否有箭头需要清理
                            if (controlIndex < branchContent.Children.Count &&
                                IsBranchArrow(branchContent.Children[controlIndex]))
                            {
                                // 命令后面紧跟的箭头（原来的下一个箭头现在在 controlIndex 位置）
                                branchContent.Children.RemoveAt(controlIndex);
                            }
                            else if (controlIndex > 0 &&
                                     IsBranchArrow(branchContent.Children[controlIndex - 1]))
                            {
                                // 命令前面的箭头
                                branchContent.Children.RemoveAt(controlIndex - 1);
                            }
                        }

                        _logger.LogDebug("已从UI中移除控件及相邻箭头: {NodeName}", nestedNode.DisplayName);

                        try
                        {
                            if (controlToRemove is IDisposable disposable)
                            {
                                disposable.Dispose();
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning($"{ex}清理控件事件订阅失败: {nestedNode.DisplayName}");
                        }
                    }
                    else
                    {
                        _logger.LogWarning("未找到要移除的控件: {NodeName}", nestedNode.DisplayName);

                        _logger.LogDebug("分支 {BranchIndex} 中的控件数量: {Count}", branchIndex, branchContent.Children.Count);
                        for (int i = 0; i < branchContent.Children.Count; i++)
                        {
                            var child = branchContent.Children[i];
                            if (child is FrameworkElement fe)
                            {
                                _logger.LogDebug("控件 {Index}: Type={Type}, Tag={Tag}, DataContext={DataContext}",
                                    i, child.GetType().Name,
                                    fe.Tag?.ToString() ?? "null",
                                    fe.DataContext?.ToString() ?? "null");
                            }
                        }
                    }

                    var hint = FindBranchHint(branchIndex);
                    if (hint != null)
                    {
                        hint.Visibility = branchContent.Children.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                    }
                }
                else
                {
                    _logger.LogWarning("未找到分支内容容器: {BranchIndex}", branchIndex);
                }

                NestedCommandRemoved?.Invoke(this, new NestedCommandEventArgs
                {
                    NestedCommand = nestedNode,
                    IsTrueBranch = false,
                    ParentIfNode = parallelNode,
                    BranchIndex = branchIndex
                });

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    UpdateBraceLayout();
                    OnBranchContentChanged();
                }), System.Windows.Threading.DispatcherPriority.Background);

                _logger.LogDebug("从并行分支 {BranchIndex} 移除命令: {NodeName}", branchIndex, nestedNode.DisplayName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "移除嵌套命令失败: {NodeName}", nestedNode?.DisplayName);
            }
        }


        #endregion


        #region 辅助方法

        private StackPanel FindBranchContentContainer(int branchIndex)
        {
            try
            {
                if (branchIndex < 0 || branchIndex >= _parallelBranches.Count)
                    return null;

                var branchContainer = _parallelBranches[branchIndex].Container;
                return FindElementByName<StackPanel>(branchContainer, $"BranchContent_{branchIndex}");
            }
            catch
            {
                return null;
            }
        }

        private TextBlock FindBranchHint(int branchIndex)
        {
            try
            {
                if (branchIndex < 0 || branchIndex >= _parallelBranches.Count)
                    return null;

                var branchContainer = _parallelBranches[branchIndex].Container;
                return FindElementByName<TextBlock>(branchContainer, $"BranchHint_{branchIndex}");
            }
            catch
            {
                return null;
            }
        }

        private T FindElementByName<T>(DependencyObject parent, string name) where T : FrameworkElement
        {
            if (parent == null) return null;

            // 先检查逻辑树（对于还没渲染的元素）
            if (parent is FrameworkElement fe && fe is T match && match.Name == name)
                return match;

            // 检查逻辑子元素
            if (parent is Panel panel)
            {
                foreach (UIElement child in panel.Children)
                {
                    if (child is T element && element.Name == name)
                        return element;
                    var result = FindElementByName<T>(child, name);
                    if (result != null) return result;
                }
            }
            else if (parent is Border border && border.Child != null)
            {
                if (border.Child is T element && element.Name == name)
                    return element;
                var result = FindElementByName<T>(border.Child, name);
                if (result != null) return result;
            }
            else if (parent is ContentControl cc && cc.Content is DependencyObject content)
            {
                if (content is T element && element.Name == name)
                    return element;
                var result = FindElementByName<T>(content, name);
                if (result != null) return result;
            }

            // 回退到视觉树
            try
            {
                for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
                {
                    var child = VisualTreeHelper.GetChild(parent, i);

                    if (child is T velement && velement.Name == name)
                        return velement;

                    var vresult = FindElementByName<T>(child, name);
                    if (vresult != null) return vresult;
                }
            }
            catch { }

            return null;
        }

        #endregion

        #region 布局更新 —— 花括号绘制

        /// <summary>
        /// 更新花括号布局 —— 核心布局方法
        /// 根据分支容器的实际高度绘制 { 和 } 花括号路径
        /// 并更新左侧连线位置
        /// </summary>
        private void UpdateBraceLayout()
        {
            try
            {
                if (_parallelBranches.Count == 0) return;

                // 先让布局系统计算好尺寸
                ParallelBranchesContainer.UpdateLayout();
                ParallelBranchesContainer.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

                var containerHeight = ParallelBranchesContainer.ActualHeight;
                if (containerHeight <= 0)
                {
                    containerHeight = ParallelBranchesContainer.DesiredSize.Height;
                }
                if (containerHeight <= 0) containerHeight = 100; // 最小默认高度

                // 绘制左花括号 {
                DrawLeftBrace(containerHeight);
                // 绘制右花括号 }
                DrawRightBrace(containerHeight);
                // 更新左侧连线位置（连接主卡片到左花括号中点）
                UpdateLeftConnector(containerHeight);

                Debug.WriteLine($"花括号布局更新完成，容器高度: {containerHeight}，共 {_parallelBranches.Count} 个分支");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"更新花括号布局失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 绘制左花括号 {
        /// 参照SVG样式3中的路径:
        /// M130 20 Q124 20 124 32 L124 93 Q124 103 118 103 Q124 103 124 113 L124 178 Q124 190 130 190
        /// 花括号的尖端在中间，向左凸出
        /// </summary>
        private void DrawLeftBrace(double totalHeight)
        {
            try
            {
                double w = 14;       // 花括号Canvas宽度
                double r = 8;        // 圆角半径
                double tipX = 1;     // 尖端X（最左点）
                double bodyX = w;    // 主体X（最右点）
                double midY = totalHeight / 2;
                double topY = 0;
                double botY = totalHeight;

                // 设置Canvas高度
                LeftBraceCanvas.Height = totalHeight;

                // 构造Path: 从顶部开始，到中间尖端，再到底部
                // 上半部分: 从(bodyX, topY+r) 经过圆角到 (bodyX, midY-r) 然后弯向尖端 (tipX, midY)
                // 下半部分: 从(tipX, midY) 弯到 (bodyX, midY+r) 然后到 (bodyX, botY-r)
                var geometry = new PathGeometry();
                var figure = new PathFigure
                {
                    StartPoint = new Point(bodyX, topY + r),
                    IsClosed = false,
                    IsFilled = false
                };

                // 顶部圆角（从右上往下开始）
                figure.Segments.Add(new QuadraticBezierSegment(
                    new Point(bodyX, topY), new Point(bodyX - r, topY), true));

                // 什么都不加，直接从顶部往下 —— 不对，要画完整
                // 重新设计：
                // 起点: (bodyX, topY) 的右下角
                // 路径: 顶端 → 向下 → 中间尖端向左凸 → 向下 → 底端

                figure = new PathFigure
                {
                    StartPoint = new Point(bodyX, topY),
                    IsClosed = false,
                    IsFilled = false
                };

                // 顶端向下，经过圆角弯到主体线
                // 上段直线到接近中间
                figure.Segments.Add(new QuadraticBezierSegment(
                    new Point(bodyX, topY), new Point(bodyX, topY + r), true));

                // 上段主体直线
                figure.Segments.Add(new LineSegment(new Point(bodyX, midY - r), true));

                // 弯向中间尖端
                figure.Segments.Add(new QuadraticBezierSegment(
                    new Point(bodyX, midY), new Point(tipX, midY), true));

                // 从尖端弯回主体
                figure.Segments.Add(new QuadraticBezierSegment(
                    new Point(bodyX, midY), new Point(bodyX, midY + r), true));

                // 下段主体直线
                figure.Segments.Add(new LineSegment(new Point(bodyX, botY - r), true));

                // 底端圆角
                figure.Segments.Add(new QuadraticBezierSegment(
                    new Point(bodyX, botY), new Point(bodyX, botY), true));

                geometry.Figures.Add(figure);
                LeftBracePath.Data = geometry;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"绘制左花括号失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 绘制右花括号 }
        /// 参照SVG样式3中的路径:
        /// M445 20 Q451 20 451 32 L451 93 Q451 103 457 103 Q451 103 451 113 L451 178 Q451 190 445 190
        /// 花括号的尖端在中间，向右凸出
        /// </summary>
        private void DrawRightBrace(double totalHeight)
        {
            try
            {
                double w = 14;
                double r = 8;
                double tipX = w - 1;  // 尖端X（最右点）
                double bodyX = 0;     // 主体X（最左点）
                double midY = totalHeight / 2;
                double topY = 0;
                double botY = totalHeight;

                RightBraceCanvas.Height = totalHeight;

                var geometry = new PathGeometry();
                var figure = new PathFigure
                {
                    StartPoint = new Point(bodyX, topY),
                    IsClosed = false,
                    IsFilled = false
                };

                // 顶端圆角
                figure.Segments.Add(new QuadraticBezierSegment(
                    new Point(bodyX, topY), new Point(bodyX, topY + r), true));

                // 上段直线
                figure.Segments.Add(new LineSegment(new Point(bodyX, midY - r), true));

                // 弯向尖端（向右凸）
                figure.Segments.Add(new QuadraticBezierSegment(
                    new Point(bodyX, midY), new Point(tipX, midY), true));

                // 从尖端弯回
                figure.Segments.Add(new QuadraticBezierSegment(
                    new Point(bodyX, midY), new Point(bodyX, midY + r), true));

                // 下段直线
                figure.Segments.Add(new LineSegment(new Point(bodyX, botY - r), true));

                // 底端圆角
                figure.Segments.Add(new QuadraticBezierSegment(
                    new Point(bodyX, botY), new Point(bodyX, botY), true));

                geometry.Figures.Add(figure);
                RightBracePath.Data = geometry;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"绘制右花括号失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 更新左侧连线 —— 连接主卡片右侧到左花括号尖端
        /// </summary>
        private void UpdateLeftConnector(double containerHeight)
        {
            try
            {
                double midY = containerHeight / 2;

                // 设置Canvas高度和连线Y坐标
                LeftConnectorCanvas.Height = containerHeight;
                LeftConnectorLine.Y1 = midY;
                LeftConnectorLine.Y2 = midY;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"更新左连线失败: {ex.Message}");
            }
        }

        private void OnBranchContentChanged()
        {
            try
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    //  强制重新布局所有分支内的子元素垂直居中
                    RealignBranchChildren();

                    UpdateBraceLayout();
                    ContentChanged?.Invoke(this, EventArgs.Empty);
                }), System.Windows.Threading.DispatcherPriority.Background);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"OnBranchContentChanged 错误: {ex.Message}");
            }
        }
        /// <summary>
        /// 手动调整分支内所有子元素的垂直位置，使其主卡片中心对齐
        /// 
        ///   核心修复：
        ///   对于普通节点（cm1、cm2），逻辑中心 = ActualHeight / 2
        ///   对于 IF 节点，逻辑中心 = CardCenterOffsetY（主卡片在控件内的实际Y中心）
        ///   所有节点按逻辑中心对齐，而不是按几何中心对齐
        ///   
        ///   各分支行独立计算，互不干涉
        /// </summary>
        private void RealignBranchChildren()
        {
            foreach (var branchInfo in _parallelBranches)
            {
                var branchContent = FindBranchContentContainer(branchInfo.Index);
                if (branchContent == null || branchContent.Children.Count == 0) continue;

                // 先强制测量
                branchContent.UpdateLayout();

                // ════════════════════════════════════════════════════
                // 第一步：清除之前的垂直Margin，重新获取真实高度
                // ════════════════════════════════════════════════════
                foreach (UIElement child in branchContent.Children)
                {
                    if (child is FrameworkElement fe)
                    {
                        var m = fe.Margin;
                        if (m.Top != 0 || m.Bottom != 0)
                        {
                            fe.Margin = new Thickness(m.Left, 0, m.Right, 0);
                        }
                    }
                }
                branchContent.UpdateLayout();

                // ════════════════════════════════════════════════════
                // 第二步：找出本分支内每个子元素的"逻辑中心"偏移
                //   逻辑中心 = 该节点主卡片的垂直中心在控件内的位置
                //   普通节点: h / 2
                //   IF 节点:  CardCenterOffsetY（因为TRUE分支在上方，主卡片被推下去了）
                // ════════════════════════════════════════════════════
                double maxAbove = 0; // 所有子元素中，逻辑中心到顶部的最大距离
                double maxBelow = 0; // 所有子元素中，逻辑中心到底部的最大距离

                foreach (UIElement child in branchContent.Children)
                {
                    if (child is FrameworkElement fe)
                    {
                        double h = fe.ActualHeight > 0 ? fe.ActualHeight : fe.DesiredSize.Height;
                        if (h <= 0) continue;

                        double above, below;
                        GetNodeCenterOffsets(child, h, out above, out below);

                        maxAbove = Math.Max(maxAbove, above);
                        maxBelow = Math.Max(maxBelow, below);
                    }
                }

                if (maxAbove + maxBelow <= 0) continue;

                // ════════════════════════════════════════════════════
                // 第三步：给每个子元素设置 Margin，使其逻辑中心
                //   都对齐到 maxAbove 这条线上
                //
                //   例如：cm1 高 44px, above=22, maxAbove=100（被IF1撑大）
                //         → topMargin = 100 - 22 = 78
                //         → cm1 被往下推78px，刚好和 IF1 的主卡片中心对齐
                //
                //   例如：IF1 高 200px, CardCenterOffsetY=100, above=100
                //         → topMargin = 100 - 100 = 0
                //         → IF1 不需要额外偏移
                // ════════════════════════════════════════════════════
                foreach (UIElement child in branchContent.Children)
                {
                    if (child is FrameworkElement fe)
                    {
                        double h = fe.ActualHeight > 0 ? fe.ActualHeight : fe.DesiredSize.Height;
                        if (h <= 0) continue;

                        double above, below;
                        GetNodeCenterOffsets(child, h, out above, out below);

                        double topMargin = maxAbove - above;
                        double bottomMargin = maxBelow - below;

                        var currentMargin = fe.Margin;
                        fe.Margin = new Thickness(
                            currentMargin.Left,
                            Math.Max(0, topMargin),
                            currentMargin.Right,
                            Math.Max(0, bottomMargin)
                        );
                    }
                }
            }
        }

        /// <summary>
        /// 获取节点的"逻辑中心"偏移量（主卡片中心在控件内的垂直位置）
        /// above = 逻辑中心到控件顶部的距离
        /// below = 逻辑中心到控件底部的距离
        /// </summary>
        private void GetNodeCenterOffsets(UIElement child, double totalHeight, out double above, out double below)
        {
            if (child is Views.IfNodeControlView nestedIf && nestedIf.CardCenterOffsetY > 0)
            {
                // IF 节点：主卡片中心不在 h/2，而在 CardCenterOffsetY
                // 因为 TRUE 分支在主卡片上方，把主卡片推下去了
                above = nestedIf.CardCenterOffsetY;
                below = totalHeight - nestedIf.CardCenterOffsetY;
            }
            // 如果以后并行节点或循环节点也有类似的中心偏移属性，在这里扩展：
            // else if (child is ParallelNodeControlView nestedParallel) { ... }
            else
            {
                // 普通节点（cm1、cm2 等）、箭头元素：几何中心 = h/2
                above = totalHeight / 2.0;
                below = totalHeight / 2.0;
            }
        }
        #endregion

        #region 清理和公共方法

        public void ClearAllBranches()
        {
            try
            {
                foreach (var branchInfo in _parallelBranches)
                {
                    var branchContent = FindBranchContentContainer(branchInfo.Index);
                    if (branchContent != null) branchContent.Children.Clear();
                    branchInfo.Commands.Clear();
                    var hint = FindBranchHint(branchInfo.Index);
                    if (hint != null) hint.Visibility = Visibility.Visible;
                }

                ParallelBranchesContainer.Children.Clear();
                _parallelBranches.Clear();

                OnBranchContentChanged();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"清理所有分支失败: {ex.Message}");
            }
        }

        public IReadOnlyList<CommandNode> GetBranchCommands(int branchIndex)
        {
            if (branchIndex >= 0 && branchIndex < _parallelBranches.Count)
            {
                return _parallelBranches[branchIndex].Commands.AsReadOnly();
            }
            return new List<CommandNode>().AsReadOnly();
        }

        public int GetBranchCount() => _parallelBranches.Count;

        public int GetTotalCommandCount()
        {
            return _parallelBranches.Sum(b => b.Commands.Count);
        }

        public void Dispose()
        {
            try
            {
                ClearAllBranches();
                ContentChanged = null;
                NestedCommandAdded = null;
                NestedCommandRemoved = null;
                LogicCommandRequested = null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Dispose 失败");
            }
        }

        #endregion

        private void Node_Click(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed && DataContext is CommandNode node)
            {
                _logger.LogDebug("ParallelNodeControlView.Node_Click: {NodeName}", node.DisplayName);
                HandleParallelNodeClick(node);
                var routedEventArgs = new RoutedEventArgs(NodeClickEvent, node);
                RaiseEvent(routedEventArgs);
                e.Handled = true;
            }
        }

        /// <summary>
        /// 处理并行节点点击
        /// </summary>
        private void HandleParallelNodeClick(CommandNode parallelNode)
        {
            try
            {
                _logger.LogDebug("处理并行节点点击: {NodeName}", parallelNode.DisplayName);

                var flowViewModel = _nodeManagementService.GetFlowDesignerViewModel(this);
                _nodeManagementService.ClearAllSelections(flowViewModel);

                parallelNode.IsSelected = true;
                NodeClicked?.Invoke(this, parallelNode);

                _logger.LogDebug("并行节点 {NodeName} 已被选中", parallelNode.DisplayName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "处理并行节点点击失败: {NodeName}", parallelNode?.DisplayName);
            }
        }

        /// <summary>
        /// 处理嵌套节点点击
        /// </summary>
        private void HandleNestedNodeClick(CommandNode nestedNode)
        {
            try
            {
                _logger.LogDebug("处理并行分支内嵌套节点点击: {NodeName}", nestedNode.DisplayName);

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

                if (DataContext is CommandNode parallelNode)
                {
                    parallelNode.IsSelected = false;
                }

                Debug.WriteLine("已清除所有节点的选中状态");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"清除节点选中状态失败: {ex.Message}");
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

        public static readonly RoutedEvent NodeClickEvent = EventManager.RegisterRoutedEvent(
            "NodeClick", RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(ParallelNodeControlView));

        public event RoutedEventHandler NodeClick
        {
            add { AddHandler(NodeClickEvent, value); }
            remove { RemoveHandler(NodeClickEvent, value); }
        }

        public void RefreshForPropertyChange()
        {
            try
            {
                InitializeParallelBranches();
                SyncChildrenToUI();
                UpdateBraceLayout();
                OnBranchContentChanged();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "并行节点属性变化刷新失败");
            }
        }
    }

    #region 辅助类

    public class ParallelBranchInfo
    {
        public int Index { get; set; }
        public List<CommandNode> Commands { get; set; } = new List<CommandNode>();
        public Border Container { get; set; }
    }

    public class NestedNodeInfo
    {
        public CommandNode Node { get; set; }
        public int BranchIndex { get; set; }
    }

    #endregion
}
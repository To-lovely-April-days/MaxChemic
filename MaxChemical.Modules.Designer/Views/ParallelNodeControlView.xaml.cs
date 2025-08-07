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
        }

        private void OnSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (e.NewSize != _lastMeasuredSize)
            {
                _lastMeasuredSize = e.NewSize;
                UpdateBranchCanvasLayout();
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
                foreach (var branchInfo in _parallelBranches)
                {
                    var branchContent = FindBranchContentContainer(branchInfo.Index);
                    if (branchContent != null)
                    {
                        branchContent.Children.Clear();
                        branchInfo.Commands.Clear();
                    }
                }

                foreach (var child in parallelNode.Children)
                {
                    var branchIndex = child.GetExecutionProperty<int>("BranchIndex");
                    if (branchIndex >= 0 && branchIndex < _parallelBranches.Count)
                    {
                        var branchInfo = _parallelBranches[branchIndex];
                        branchInfo.Commands.Add(child);

                        var nestedControl = _nodeControlFactory.CreateNestedCommandControl(
                            child,
                            HandleNestedNodeClick,
                            (node) => RemoveNestedCommand(node, branchIndex));

                        var branchContent = FindBranchContentContainer(branchIndex);
                        if (branchContent != null)
                        {
                            branchContent.Children.Add(nestedControl);

                            var hint = FindBranchHint(branchIndex);
                            if (hint != null)
                            {
                                hint.Visibility = Visibility.Collapsed;
                            }
                        }
                    }
                }

                UpdateBranchCanvasLayout();
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

                UpdateBranchCanvasLayout();
                _logger.LogDebug("创建了 {Count} 个并行分支", maxParallelCount);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "初始化并行分支失败");
            }
        }


        private void CreateParallelBranch(int branchIndex)
        {
            var branchInfo = new ParallelBranchInfo
            {
                Index = branchIndex,
                Commands = new List<CommandNode>()
            };

            var branchContainer = CreateBranchContainer(branchIndex);
            branchInfo.Container = branchContainer;

            ParallelBranchesContainer.Children.Add(branchContainer);
            _parallelBranches.Add(branchInfo);

            CreateBranchConnectionElements(branchIndex, branchContainer);
        }

        private Border CreateBranchContainer(int branchIndex)
        {
            Debug.WriteLine($"=== 创建分支容器 {branchIndex} ===");

            var branchWrapper = new Border
            {
                MinWidth = 250,
                Margin = new Thickness(10, 0, 10, 0)
            };

            var stackPanel = new StackPanel
            {
                MinWidth = 250
            };

            var branchLabel = new TextBlock
            {
                Text = $"分支 {branchIndex + 1}",
                FontSize = 14,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(0x60, 0x7D, 0x8B)),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 8)
            };

            var dropZone = new Border
            {
                Name = $"BranchDropZone_{branchIndex}",
                Background = new SolidColorBrush(Color.FromArgb(0x08, 0x60, 0x7D, 0x8B)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x1E, 0x60, 0x7D, 0x8B)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8),
                MinHeight = 60,
                MinWidth = 250,
                AllowDrop = true,
                UseLayoutRounding = true,
                SnapsToDevicePixels = true,
                Margin = new Thickness(0, 4, 0, 0),
                Tag = branchIndex
            };

            Debug.WriteLine($"创建拖放区域，AllowDrop: {dropZone.AllowDrop}, Tag: {dropZone.Tag}");

            dropZone.DragEnter += Branch_DragEnter;
            dropZone.DragOver += Branch_DragOver;
            dropZone.DragLeave += Branch_DragLeave;
            dropZone.Drop += Branch_Drop;

            Debug.WriteLine($"已订阅拖放事件");

            var dropZoneGrid = new Grid
            {
                UseLayoutRounding = true
            };

            var hint = new TextBlock
            {
                Name = $"BranchHint_{branchIndex}",
                Text = "拖拽步骤到此处",
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(Color.FromArgb(0x80, 0x60, 0x7D, 0x8B)),
                FontSize = 11,
                FontStyle = FontStyles.Italic,
                FontFamily = new FontFamily("Segoe UI"),
                UseLayoutRounding = true
            };

            var branchContent = new StackPanel
            {
                Name = $"BranchContent_{branchIndex}",
                UseLayoutRounding = true
            };

            dropZoneGrid.Children.Add(hint);
            dropZoneGrid.Children.Add(branchContent);
            dropZone.Child = dropZoneGrid;

            stackPanel.Children.Add(branchLabel);
            stackPanel.Children.Add(dropZone);
            branchWrapper.Child = stackPanel;

            Debug.WriteLine($"=== 分支容器 {branchIndex} 创建完成 ===");
            return branchWrapper;
        }

        private void CreateBranchConnectionElements(int branchIndex, Border branchContainer)
        {
            double branchCenterX = 150 + (branchIndex * 200);

            var verticalLine = new Line
            {
                Name = $"BranchVerticalLine_{branchIndex}",
                X1 = branchCenterX,
                Y1 = 24,
                X2 = branchCenterX,
                Y2 = 36,
                Stroke = new SolidColorBrush(Color.FromRgb(0x60, 0x7D, 0x8B)),
                StrokeThickness = 2
            };

            var arrow = new Polygon
            {
                Name = $"BranchArrow_{branchIndex}",
                Fill = new SolidColorBrush(Color.FromRgb(0x60, 0x7D, 0x8B)),
                Points = new PointCollection(new[]
                {
                    new Point(0, 0),
                    new Point(8, 0),
                    new Point(4, 8)
                })
            };

            Canvas.SetLeft(arrow, branchCenterX - 4);
            Canvas.SetTop(arrow, 32);

            BranchCanvas.Children.Add(verticalLine);
            BranchCanvas.Children.Add(arrow);
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

                // 记录当前的插入索引，然后清除高亮
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

                if (command != null)
                {
                    _logger.LogDebug("提取到命令: {Type}", command.GetType().Name);
                    AddCommandToBranch(command, branchIndex, insertionIndex);
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


        #endregion

        #region 高亮逻辑（并行分支版本）

        private class InsertionInfo
        {
            public int InsertionIndex { get; set; }
            public CommandNode PreviousNode { get; set; }
            public UserControl PreviousControl { get; set; }
            public bool IsInsertAtBeginning { get; set; }
        }

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

                // 如果分支为空，插入位置为0，没有前一个节点
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

                double cumulativeHeight = 0;

                for (int i = 0; i < branchCommands.Count; i++)
                {
                    var node = branchCommands[i];
                    var control = FindControlForNodeInBranch(branchContent, node);

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
                                info.PreviousNode = branchCommands[i - 1];
                                info.PreviousControl = FindControlForNodeInBranch(branchContent, info.PreviousNode);
                            }

                            _logger.LogTrace("鼠标位置 {Y} 在分支 {BranchIndex} 节点 {Index} 上半部分，插入位置: {InsertIndex}",
                                mousePosition.Y, branchIndex, i, i);
                            return info;
                        }

                        cumulativeHeight += controlHeight + 8; // 8是margin
                    }
                }

                // 如果鼠标在所有元素下方，插入到末尾
                info.InsertionIndex = branchCommands.Count;
                info.PreviousNode = branchCommands[branchCommands.Count - 1];
                info.PreviousControl = FindControlForNodeInBranch(branchContent, info.PreviousNode);

                _logger.LogTrace("鼠标位置 {Y} 在分支 {BranchIndex} 所有节点下方，插入位置: {InsertIndex}",
                    mousePosition.Y, branchIndex, branchCommands.Count);
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
                // 先清除之前的高亮
                ClearHighlight();

                if (insertionInfo.IsInsertAtBeginning)
                {
                    // 插入到开头，不高亮任何节点
                    _logger.LogTrace("插入到分支 {BranchIndex} 开头，无需高亮节点", branchIndex);
                    return;
                }

                if (insertionInfo.PreviousNode != null)
                {
                    // 高亮前一个节点 - 只设置数据绑定属性，让DataTrigger处理样式
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
                // 只清除数据绑定属性，让DataTrigger处理样式恢复
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

        private void AddCommandToBranch(object command, int branchIndex, int insertionIndex = -1)
        {
            try
            {
                if (branchIndex < 0 || branchIndex >= _parallelBranches.Count)
                    return;

                CommandNode nestedNode = null;

                if (command is DeviceCommand deviceCmd)
                {
                    nestedNode = new CommandNode(deviceCmd);
                }
                else if (command is LogicCommand logicCmd)
                {
                    nestedNode = new CommandNode(logicCmd);
                }

                if (nestedNode != null && DataContext is CommandNode parallelNode)
                {
                    parallelNode.AddChildToParallelBranch(nestedNode, branchIndex);

                    var branchInfo = _parallelBranches[branchIndex];

                    // 如果指定了插入索引，则在指定位置插入
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

                    // 订阅嵌套复杂节点的内容变化事件
                    SubscribeToNestedComplexNodeEvents(nestedControl, nestedNode, branchIndex);

                    var branchContent = FindBranchContentContainer(branchIndex);
                    if (branchContent != null)
                    {
                        // 在UI中也插入到对应位置
                        if (insertionIndex >= 0 && insertionIndex <= branchContent.Children.Count)
                        {
                            branchContent.Children.Insert(insertionIndex, nestedControl);
                        }
                        else
                        {
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
                        UpdateBranchCanvasLayout();
                        OnBranchContentChanged();

                        // 触发嵌套命令添加事件
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
        // 新增：订阅嵌套复杂节点事件的方法
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
                        break;

                    case Views.LoopNodeControlView nestedLoopControl:
                        _logger.LogDebug("订阅嵌套循环节点事件: {NodeName}", nestedNode.DisplayName);

                        // 假设LoopNodeControlView也有ContentChanged等事件
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
                        break;

                    default:
                        // 对于普通命令节点（DeviceCommand等），不需要特殊处理
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
                    // 修改这里：直接比较 Tag 是否为目标节点，或者使用 DataContext 比较
                    var controlToRemove = branchContent.Children
                        .OfType<UserControl>()
                        .FirstOrDefault(c => c.Tag == nestedNode || c.DataContext == nestedNode);

                    if (controlToRemove != null)
                    {
                        branchContent.Children.Remove(controlToRemove);
                        _logger.LogDebug("已从UI中移除控件: {NodeName}", nestedNode.DisplayName);
                    }
                    else
                    {
                        _logger.LogWarning("未找到要移除的控件: {NodeName}", nestedNode.DisplayName);

                        // 备用方案：遍历所有控件进行调试
                        _logger.LogDebug("分支 {BranchIndex} 中的控件数量: {Count}", branchIndex, branchContent.Children.Count);
                        for (int i = 0; i < branchContent.Children.Count; i++)
                        {
                            var child = branchContent.Children[i];
                            _logger.LogDebug("控件 {Index}: Type={Type}, Tag={Tag}, DataContext={DataContext}",
                                i, child.GetType().Name, child is FrameworkElement fe ? fe.Tag?.ToString() : "null",
                                child is FrameworkElement fe2 ? fe2.DataContext?.ToString() : "null");
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

                UpdateBranchCanvasLayout();
                OnBranchContentChanged();

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

            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);

                if (child is T element && element.Name == name)
                {
                    return element;
                }

                var result = FindElementByName<T>(child, name);
                if (result != null)
                {
                    return result;
                }
            }

            return null;
        }

        #endregion

        #region 布局更新

        private void UpdateBranchCanvasLayout()
        {
            try
            {
                if (_parallelBranches.Count == 0) return;

                ParallelBranchesContainer.UpdateLayout();
                CalculateBranchPositions();
                UpdateCanvasSize();
                UpdateConnectionLines();

                System.Diagnostics.Debug.WriteLine($"并行分支布局更新完成，共 {_parallelBranches.Count} 个分支");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"更新并行分支布局失败: {ex.Message}");
            }
        }

        private void CalculateBranchPositions()
        {
            try
            {
                var totalWidth = 0.0;
                var maxHeight = 0.0;

                foreach (var branchInfo in _parallelBranches)
                {
                    branchInfo.Container.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    var size = branchInfo.Container.DesiredSize;

                    totalWidth += size.Width + 20;
                    maxHeight = Math.Max(maxHeight, size.Height);
                }

                var canvasWidth = Math.Max(800, totalWidth + 100);
                var canvasHeight = Math.Max(200, maxHeight + 80);

                BranchCanvas.Width = canvasWidth;
                BranchCanvas.Height = canvasHeight;

                var containerLeft = (canvasWidth - totalWidth) / 2;
                Canvas.SetLeft(ParallelBranchesContainer, containerLeft);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"计算分支位置失败: {ex.Message}");
            }
        }

        private void UpdateCanvasSize()
        {
            try
            {
                ParallelBranchesContainer.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                var containerSize = ParallelBranchesContainer.DesiredSize;

                var canvasWidth = Math.Max(800, containerSize.Width + 200);
                var canvasHeight = Math.Max(200, containerSize.Height + 80);

                BranchCanvas.Width = canvasWidth;
                BranchCanvas.Height = canvasHeight;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"更新Canvas尺寸失败: {ex.Message}");
            }
        }

        private void UpdateConnectionLines()
        {
            try
            {
                var canvasCenterX = BranchCanvas.Width / 2;

                MainVerticalLine.X1 = canvasCenterX;
                MainVerticalLine.X2 = canvasCenterX;

                if (_parallelBranches.Count > 0)
                {
                    var containerLeft = Canvas.GetLeft(ParallelBranchesContainer);
                    ParallelBranchesContainer.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    var containerWidth = ParallelBranchesContainer.DesiredSize.Width;

                    var lineLeft = containerLeft;
                    var lineRight = containerLeft + containerWidth;

                    HorizontalDistributionLine.X1 = lineLeft;
                    HorizontalDistributionLine.X2 = lineRight;
                }

                UpdateBranchConnectionLines();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"更新连接线失败: {ex.Message}");
            }
        }

        private void UpdateBranchConnectionLines()
        {
            try
            {
                var containerLeft = Canvas.GetLeft(ParallelBranchesContainer);
                var currentX = containerLeft;

                for (int i = 0; i < _parallelBranches.Count; i++)
                {
                    var branchContainer = _parallelBranches[i].Container;
                    branchContainer.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    var branchWidth = branchContainer.DesiredSize.Width;
                    var branchCenterX = currentX + branchWidth / 2;

                    var verticalLine = BranchCanvas.Children
                        .OfType<Line>()
                        .FirstOrDefault(l => l.Name == $"BranchVerticalLine_{i}");

                    if (verticalLine != null)
                    {
                        verticalLine.X1 = branchCenterX;
                        verticalLine.X2 = branchCenterX;
                    }

                    var arrow = BranchCanvas.Children
                        .OfType<Polygon>()
                        .FirstOrDefault(p => p.Name == $"BranchArrow_{i}");

                    if (arrow != null)
                    {
                        Canvas.SetLeft(arrow, branchCenterX - 4);
                    }

                    currentX += branchWidth + 20;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"更新分支连接线失败: {ex.Message}");
            }
        }

        private void OnBranchContentChanged()
        {
            try
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    UpdateBranchCanvasLayout();
                    ContentChanged?.Invoke(this, EventArgs.Empty);
                    System.Diagnostics.Debug.WriteLine("并行节点内容变化通知完成");
                }), System.Windows.Threading.DispatcherPriority.Background);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"OnBranchContentChanged 错误: {ex.Message}");
            }
        }

        #endregion

        #region 清理和公共方法

        public void ClearAllBranches()
        {
            try
            {
                if (DataContext is CommandNode parallelNode)
                {
                    parallelNode.ClearChildren();
                }

                foreach (var branchInfo in _parallelBranches)
                {
                    var branchContent = FindBranchContentContainer(branchInfo.Index);
                    if (branchContent != null)
                    {
                        branchContent.Children.Clear();
                    }

                    branchInfo.Commands.Clear();

                    var hint = FindBranchHint(branchInfo.Index);
                    if (hint != null)
                    {
                        hint.Visibility = Visibility.Visible;
                    }
                }

                var elementsToRemove = BranchCanvas.Children
                    .OfType<FrameworkElement>()
                    .Where(e => e.Name != null &&
                               (e.Name.StartsWith("BranchVerticalLine_") ||
                                e.Name.StartsWith("BranchArrow_")))
                    .ToList();

                foreach (var element in elementsToRemove)
                {
                    BranchCanvas.Children.Remove(element);
                }

                ParallelBranchesContainer.Children.Clear();
                _parallelBranches.Clear();

                UpdateBranchCanvasLayout();
                OnBranchContentChanged();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"清理所有分支失败: {ex.Message}");
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
        public static readonly RoutedEvent NodeClickEvent = EventManager.RegisterRoutedEvent(
            "NodeClick", RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(ParallelNodeControlView));

        public event RoutedEventHandler NodeClick
        {
            add { AddHandler(NodeClickEvent, value); }
            remove { RemoveHandler(NodeClickEvent, value); }
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
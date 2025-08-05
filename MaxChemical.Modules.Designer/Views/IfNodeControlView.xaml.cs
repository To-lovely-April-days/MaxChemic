// IfNodeControlView.xaml.cs (重构后)
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

            Loaded += OnLoaded;
            SizeChanged += OnSizeChanged;
            DataContextChanged += OnDataContextChanged;
        }

        private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (e.NewValue is CommandNode ifNode)
            {
                SyncChildrenToUI();
            }
        }

        private void SyncChildrenToUI()
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
            e.Effects = _dragDropService.CanAcceptDrop(e.Data) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void TrueBranch_DragLeave(object sender, DragEventArgs e)
        {
            _dragDropService.ClearDragOverStyle(TrueBranchDropZone);
            e.Handled = true;
        }

        private void TrueBranch_Drop(object sender, DragEventArgs e)
        {
            _dragDropService.ClearDragOverStyle(TrueBranchDropZone);
            HandleCommandDrop(e, true);
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
            e.Effects = _dragDropService.CanAcceptDrop(e.Data) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void FalseBranch_DragLeave(object sender, DragEventArgs e)
        {
            _dragDropService.ClearDragOverStyle(FalseBranchDropZone);
            e.Handled = true;
        }

        private void FalseBranch_Drop(object sender, DragEventArgs e)
        {
            _dragDropService.ClearDragOverStyle(FalseBranchDropZone);
            HandleCommandDrop(e, false);
            e.Handled = true;
        }

        private void HandleCommandDrop(DragEventArgs e, bool isTrueBranch)
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
                    _logger.LogDebug("准备添加命令到 {Branch} 分支: {Type}",
                        isTrueBranch ? "TRUE" : "FALSE", command.GetType().Name);
                    AddCommandToBranch(command, isTrueBranch);
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

        private void AddCommandToBranch(object command, bool isTrueBranch)
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
                    ifNode.AddChildToBranch(nestedNode, isTrueBranch);

                    var nestedControl = _nodeControlFactory.CreateNestedCommandControl(
                        nestedNode,
                        HandleNestedNodeClick,
                        RemoveNestedCommand);
                    // 重要：如果创建的是复杂节点，手动订阅其内容变化事件
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
                    else if (nestedControl is LoopNodeControlView nestedloopControl)
                    {
                        nestedloopControl.ContentChanged += (s, e) =>
                        {
                            _logger.LogDebug("IF节点内的嵌套Loop节点内容发生变化");
                            OnBranchContentChanged();
                        };
                    }
                    if (isTrueBranch)
                    {
                        _trueBranchCommands.Add(nestedNode);
                        TrueBranchContent.Children.Add(nestedControl);
                        TrueHint.Visibility = Visibility.Collapsed;
                    }
                    else
                    {
                        _falseBranchCommands.Add(nestedNode);
                        FalseBranchContent.Children.Add(nestedControl);
                        FalseHint.Visibility = Visibility.Collapsed;
                    }

                    NestedCommandAdded?.Invoke(this, new NestedCommandEventArgs
                    {
                        NestedCommand = nestedNode,
                        IsTrueBranch = isTrueBranch,
                        ParentIfNode = ifNode
                    });

                    UpdateBranchCanvasHeight();
                   
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "添加嵌套命令失败");
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

                ifNode.RemoveChild(nestedNode);

                if (isTrueBranch)
                {
                    _trueBranchCommands.Remove(nestedNode);
                    var controlToRemove = TrueBranchContent.Children
                        .OfType<UserControl>()
                        .FirstOrDefault(c => c.Tag == nestedNode);
                    if (controlToRemove != null)
                    {
                        TrueBranchContent.Children.Remove(controlToRemove);
                        removed = true;
                    }
                    TrueHint.Visibility = TrueBranchContent.Children.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                }
                else if (_falseBranchCommands.Contains(nestedNode))
                {
                    _falseBranchCommands.Remove(nestedNode);
                    var controlToRemove = FalseBranchContent.Children
                        .OfType<UserControl>()
                        .FirstOrDefault(c => c.Tag == nestedNode);
                    if (controlToRemove != null)
                    {
                        FalseBranchContent.Children.Remove(controlToRemove);
                        removed = true;
                    }
                    FalseHint.Visibility = FalseBranchContent.Children.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
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

            totalHeight += Math.Max(0, branchContent.Children.Count - 1) * 8;
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

        #endregion


    }
}
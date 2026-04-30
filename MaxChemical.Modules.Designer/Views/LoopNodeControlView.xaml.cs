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
using System.Windows.Shapes;

namespace MaxChemical.Modules.Designer.Views
{
    /// <summary>
    /// LoopNodeControlView.xaml 的交互逻辑
    /// 方案C：紧凑卡片 + 展开/折叠，子区域自适应大小，带回路箭头
    /// 头部卡片和子区域宽度始终保持同步
    /// </summary>
    public partial class LoopNodeControlView : UserControl
    {
        public event EventHandler ContentChanged;
        public event EventHandler<NestedCommandEventArgs> NestedCommandAdded;
        public event EventHandler<NestedCommandEventArgs> NestedCommandRemoved;
        public event EventHandler<LogicCommandRequestEventArgs> LogicCommandRequested;
        public event EventHandler<CommandNode> NodeDeleteRequested;
        public event EventHandler<CommandNode> NodeClicked;

        private bool _isExpanded = false; // 方案C：默认折叠
        private Size _lastMeasuredSize;
        private const double DEFAULT_WIDTH = 231.0; // 与主流程节点等宽

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
                if ((bool)e.NewValue == true)
                {
                    _logger.LogDebug("循环节点控件变为可见，延迟同步UI");

                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (DataContext is CommandNode loopNode && loopNode.Children.Count > 0)
                        {
                            _logger.LogDebug($"可见性变化触发UI同步: {loopNode.DisplayName}，子节点数: {loopNode.Children.Count}");
                            UpdateChildrenDisplay();
                            UpdateSummaryText();
                            if (!_isExpanded)
                            {
                                ToggleExpand();
                            }
                        }
                        else
                        {
                            UpdateSummaryText();
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
            UpdateSummaryText();
            UpdateExpandVisual();

            if (_isExpanded)
            {
                ChildrenContainer.Visibility = Visibility.Visible;
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    SyncWidths();
                    UpdateRepeatArrow();
                }), DispatcherPriority.Loaded);
            }
        }

        private void OnSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (e.NewSize != _lastMeasuredSize)
            {
                _lastMeasuredSize = e.NewSize;
                if (_isExpanded)
                {
                    UpdateRepeatArrow();
                }
            }
        }

        private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (e.NewValue is CommandNode loopNode)
            {
                UpdateChildrenDisplay();
                UpdateSummaryText();
            }
        }

        #region 宽度同步（核心修复）

        /// <summary>
        /// 同步头部卡片和子区域的宽度：
        /// 1. 临时清除容器固定宽度，让子节点自由报告自然尺寸
        /// 2. 遍历子控件取最大实际宽度
        /// 3. 目标宽度 = max(DEFAULT_WIDTH, 最大子控件宽度 + padding)
        /// 4. 同时设置头部和子区域的 Width
        /// </summary>
        private void SyncWidths()
        {
            try
            {
                if (!_isExpanded)
                {
                    // 折叠时全部回到默认
                    MainContainer.Width = DEFAULT_WIDTH;
                    MainContainer.MinWidth = DEFAULT_WIDTH;
                    LoopContentWrapper.MinWidth = DEFAULT_WIDTH;
                    RootPanel.MinWidth = 0;
                    this.MinWidth = 0;
                    this.Width = double.NaN;
                    return;
                }

                //  先清除所有固定宽度和MinWidth，让子内容完全自由
                MainContainer.Width = double.NaN;
                MainContainer.MinWidth = DEFAULT_WIDTH;
                LoopContentWrapper.Width = double.NaN;
                LoopContentWrapper.MinWidth = DEFAULT_WIDTH;
                RootPanel.MinWidth = 0;
                this.MinWidth = 0;
                this.Width = double.NaN;

                // 强制布局更新
                LoopChildrenContent.UpdateLayout();

                // 遍历子控件取最大自然宽度（跳过连接线箭头）
                double maxChildWidth = 0;
                foreach (UIElement child in LoopChildrenContent.Children)
                {
                    if (child is FrameworkElement fe)
                    {
                        if (fe.Tag is string tag && tag == "ConnectionArrow")
                            continue;

                        fe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                        double w = fe.DesiredSize.Width;
                        if (w <= 0) w = fe.ActualWidth;

                        if (w > maxChildWidth)
                            maxChildWidth = w;
                    }
                }

                // 容器宽度 = 最大子控件宽度 + Padding(10*2) + Border(1*2)
                double childAreaNeeded = maxChildWidth + 22;
                double targetWidth = Math.Max(DEFAULT_WIDTH, childAreaNeeded);

                //  只用 MinWidth，不设固定 Width
                //   这样删除子节点后 MinWidth 会被重算为更小的值，控件自然缩小
                MainContainer.MinWidth = targetWidth;
                LoopContentWrapper.MinWidth = targetWidth;
                RootPanel.MinWidth = targetWidth;
                this.MinWidth = targetWidth;

                _logger.LogDebug("宽度同步完成: maxChild={MaxW:F0}, target={TargetW:F0}",
                    maxChildWidth, targetWidth);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "宽度同步失败");
            }
        }

        /// <summary>
        /// 重置所有尺寸约束，让控件可以自由缩小
        /// 之后由 SyncWidths 根据当前子节点重新计算
        /// </summary>
        private void ResetWidths()
        {
            MainContainer.Width = double.NaN;
            MainContainer.MinWidth = DEFAULT_WIDTH;
            LoopContentWrapper.Width = double.NaN;
            LoopContentWrapper.MinWidth = DEFAULT_WIDTH;
            RootPanel.MinWidth = 0;
            this.MinWidth = 0;
            this.Width = double.NaN;
            this.Height = double.NaN;
            this.MinHeight = 0;
            LoopContentWrapper.Height = double.NaN;

            //  关键修复：清除 RepeatArrowCanvas 的固定高度
            //   否则它的旧高度会撑住 ChildAreaGrid，阻止容器缩小
            RepeatArrowCanvas.Height = double.NaN;
        }

        #endregion

        #region 展开/折叠逻辑（方案C核心）

        private void ExpandToggle_Click(object sender, RoutedEventArgs e)
        {
            ToggleExpand();
            e.Handled = true;
        }

        public void ToggleExpand()
        {
            _isExpanded = !_isExpanded;
            UpdateExpandVisual();

            if (_isExpanded)
            {
                ChildrenContainer.Visibility = Visibility.Visible;

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    SyncWidths();
                    UpdateRepeatArrow();
                    ContentChanged?.Invoke(this, EventArgs.Empty);
                }), DispatcherPriority.Loaded);
            }
            else
            {
                ChildrenContainer.Visibility = Visibility.Collapsed;

                // 折叠时重置宽度为默认
                MainContainer.Width = DEFAULT_WIDTH;

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    ContentChanged?.Invoke(this, EventArgs.Empty);
                }), DispatcherPriority.Loaded);
            }

            UpdateSummaryText();
        }

        private void UpdateExpandVisual()
        {
            if (_isExpanded)
            {
                ExpandIconTransform.Angle = 0;   // 箭头朝下（展开状态）
                ExpandToggle.ToolTip = "折叠循环";
            }
            else
            {
                ExpandIconTransform.Angle = -90; // 箭头朝右（折叠状态）
                ExpandToggle.ToolTip = "展开循环";
            }
        }

        private void UpdateSummaryText()
        {
            try
            {
                int stepCount = 0;
                if (DataContext is CommandNode loopNode)
                {
                    stepCount = loopNode.Children?.Count ?? 0;
                }

                if (_isExpanded)
                {
                    SummaryText.Text = stepCount > 0 ? $"{stepCount} 个步骤" : "无步骤";
                }
                else
                {
                    SummaryText.Text = stepCount > 0
                        ? $"{stepCount} 个步骤 · 点击展开"
                        : "点击展开添加步骤";
                }
                SummaryText.Foreground = new SolidColorBrush(Color.FromRgb(0x18, 0x5F, 0xA5));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "更新摘要文字失败");
            }
        }

        /// <summary>
        /// 绘制回路箭头（右侧曲线 + repeat 文字）
        /// </summary>
        private void UpdateRepeatArrow()
        {
            try
            {
                RepeatArrowCanvas.Children.Clear();

                if (!_isExpanded || _loopCommands.Count == 0)
                    return;
                //  先清除自身固定高度，让 Grid 行高完全由 LoopContentWrapper 决定
                RepeatArrowCanvas.Height = double.NaN;
                LoopContentWrapper.UpdateLayout();
                double contentHeight = LoopContentWrapper.ActualHeight;
                if (contentHeight <= 0) contentHeight = 60;

                RepeatArrowCanvas.Height = contentHeight;

                double arrowX = 12;
                double topY = contentHeight * 0.15;
                double bottomY = contentHeight * 0.85;
                double curveOffset = 18;

                // 回路曲线
                var path = new Path
                {
                    Stroke = new SolidColorBrush(Color.FromRgb(0x37, 0x8A, 0xDD)),
                    StrokeThickness = 1.2,
                    Fill = Brushes.Transparent,
                    StrokeDashArray = new DoubleCollection(new[] { 4.0, 3.0 }),
                    Opacity = 0.6
                };

                var pathGeometry = new PathGeometry();
                var figure = new PathFigure
                {
                    StartPoint = new Point(arrowX, bottomY),
                    IsClosed = false
                };

                figure.Segments.Add(new BezierSegment(
                    new Point(arrowX + curveOffset, bottomY),
                    new Point(arrowX + curveOffset, topY),
                    new Point(arrowX, topY),
                    true));

                pathGeometry.Figures.Add(figure);
                path.Data = pathGeometry;
                RepeatArrowCanvas.Children.Add(path);

                // 箭头
                var arrowHead = new Polygon
                {
                    Fill = new SolidColorBrush(Color.FromRgb(0x37, 0x8A, 0xDD)),
                    Opacity = 0.6,
                    Points = new PointCollection(new[]
                    {
                        new Point(arrowX - 3, topY + 6),
                        new Point(arrowX + 3, topY + 6),
                        new Point(arrowX, topY)
                    })
                };
                RepeatArrowCanvas.Children.Add(arrowHead);

                // "repeat" 文字（竖排）
                var repeatText = new TextBlock
                {
                    Text = "repeat",
                    FontSize = 9,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x37, 0x8A, 0xDD)),
                    Opacity = 0.7,
                    RenderTransformOrigin = new Point(0.5, 0.5),
                    RenderTransform = new RotateTransform(-90)
                };

                repeatText.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                double textWidth = repeatText.DesiredSize.Width;

                Canvas.SetLeft(repeatText, arrowX + curveOffset + 2);
                Canvas.SetTop(repeatText, (topY + bottomY) / 2 + textWidth / 2);

                RepeatArrowCanvas.Children.Add(repeatText);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "绘制回路箭头失败");
            }
        }

        #endregion

        #region 节点点击事件

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

        private void HandleLoopNodeClick(CommandNode loopNode)
        {
            try
            {
                _logger.LogDebug("处理循环节点点击: {NodeName}", loopNode.DisplayName);

                var flowViewModel = _nodeManagementService.GetFlowDesignerViewModel(this);
                _nodeManagementService.ClearAllSelections(flowViewModel);

                loopNode.IsSelected = true;
                NodeClicked?.Invoke(this, loopNode);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "处理循环节点点击失败: {NodeName}", loopNode?.DisplayName);
            }
        }

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
                }

                NodeClicked?.Invoke(this, nestedNode);
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
            if (parentObject is T parent) return parent;
            return FindVisualParent<T>(parentObject);
        }

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

        #endregion

        #region 布局更新方法

        /// <summary>
        /// 内容变化通知 - 同步宽度 + 更新箭头 + 通知父级
        /// </summary>
        public void OnLoopContentChanged()
        {
            try
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    UpdateSummaryText();
                    SyncWidths();

                    if (_isExpanded)
                    {
                        UpdateRepeatArrow();
                    }

                    ContentChanged?.Invoke(this, EventArgs.Empty);

                    //  二次同步：等子控件完全渲染后再校正一次宽度
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        SyncWidths();
                        if (_isExpanded)
                        {
                            UpdateRepeatArrow();
                        }
                    }), DispatcherPriority.Render);

                    _logger.LogDebug("循环内容变化通知完成");
                }), DispatcherPriority.Background);
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

            // 如果当前是折叠状态，自动展开
            if (!_isExpanded)
            {
                ToggleExpand();
            }

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

                    // 直接从 dragData 里取 SourceDeviceType
                    string sourceDeviceType = ExtractSourceDeviceTypeFromDragData(e.Data);

                    if (command != null)
                    {
                        _logger.LogDebug("提取到命令: {Type}, 插入位置: {Index}, 来源设备类型: {SrcType}",
                            command.GetType().Name, insertionIndex, sourceDeviceType ?? "(null)");
                        AddCommandToLoop(command, loopNode, insertionIndex, sourceDeviceType);
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
                        double controlHeight = control.ActualHeight;
                        if (controlHeight == 0)
                        {
                            control.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                            controlHeight = control.DesiredSize.Height;
                        }
                        if (controlHeight == 0) controlHeight = 40;

                        if (mousePosition.Y <= cumulativeHeight + controlHeight / 2)
                        {
                            info.InsertionIndex = i;
                            if (i == 0)
                            {
                                info.IsInsertAtBeginning = true;
                            }
                            else
                            {
                                info.PreviousNode = _loopCommands[i - 1];
                            }
                            return info;
                        }

                        cumulativeHeight += controlHeight + 8;
                    }
                }

                info.InsertionIndex = _loopCommands.Count;
                info.PreviousNode = _loopCommands[_loopCommands.Count - 1];
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
                ClearHighlight();

                if (insertionInfo.IsInsertAtBeginning)
                    return;

                if (insertionInfo.PreviousNode != null)
                {
                    _highlightedNode = insertionInfo.PreviousNode;
                    _highlightedNode.IsInsertHighlight = true;
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

        private void AddCommandToLoop(object command, CommandNode loopNode, int insertionIndex = -1, string sourceDeviceType = null)
        {
            try
            {
                ClearHighlight();

                CommandNode childNode = null;

                if (command is DeviceCommand deviceCmd)
                {
                    childNode = new CommandNode(deviceCmd);
                    childNode.SourceDeviceType = sourceDeviceType;   // ★ 写入源设备类型
                }
                else if (command is LogicCommand logicCmd)
                {
                    childNode = new CommandNode(logicCmd);
                }

                if (childNode != null)
                {
                    if (insertionIndex >= 0 && insertionIndex <= _loopCommands.Count)
                    {
                        _loopCommands.Insert(insertionIndex, childNode);
                        loopNode.InsertChild(insertionIndex, childNode);
                        _logger.LogDebug("在循环位置 {Index} 插入命令: {NodeName}", insertionIndex, childNode.DisplayName);
                    }
                    else
                    {
                        _loopCommands.Add(childNode);
                        loopNode.AddChildToLoop(childNode);
                        _logger.LogDebug("添加命令到循环末尾: {NodeName}", childNode.DisplayName);
                    }

                    SubscribeToNestedComplexNodeEvents(childNode);
                    UpdateChildrenDisplay();

                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        ClearHighlight();

                        //  核心：添加子节点后同步宽度
                        OnLoopContentChanged();

                        NestedCommandAdded?.Invoke(this, new NestedCommandEventArgs
                        {
                            NestedCommand = childNode,
                            IsTrueBranch = false,
                            ParentIfNode = loopNode
                        });
                    }), DispatcherPriority.Loaded);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "添加命令到循环失败");
            }
            finally
            {
                ClearHighlight();
            }
        }

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
                        nestedParallelControl.NestedCommandAdded += (s, e) => NestedCommandAdded?.Invoke(this, e);
                        nestedParallelControl.NestedCommandRemoved += (s, e) => NestedCommandRemoved?.Invoke(this, e);
                        nestedParallelControl.LogicCommandRequested += (s, e) => LogicCommandRequested?.Invoke(this, e);
                        break;

                    case IfNodeControlView nestedIfControl:
                        nestedIfControl.ContentChanged += (s, e) =>
                        {
                            _logger.LogDebug("循环内嵌套IF节点内容变化: {NodeName}", nestedNode.DisplayName);
                            OnLoopContentChanged();
                        };
                        nestedIfControl.NestedCommandAdded += (s, e) =>
                        {
                            _logger.LogDebug("循环内嵌套IF添加了命令，冒泡事件: {Name}", e.NestedCommand?.DisplayName);
                            NestedCommandAdded?.Invoke(this, e);
                        };
                        nestedIfControl.NestedCommandRemoved += (s, e) => NestedCommandRemoved?.Invoke(this, e);
                        nestedIfControl.LogicCommandRequested += (s, e) => LogicCommandRequested?.Invoke(this, e);
                        break;

                    case LoopNodeControlView nestedLoopControl:
                        nestedLoopControl.ContentChanged += (s, e) =>
                        {
                            _logger.LogDebug("循环内嵌套循环节点内容变化: {NodeName}", nestedNode.DisplayName);
                            OnLoopContentChanged();
                        };
                        nestedLoopControl.NestedCommandAdded += (s, e) => NestedCommandAdded?.Invoke(this, e);
                        nestedLoopControl.NestedCommandRemoved += (s, e) => NestedCommandRemoved?.Invoke(this, e);
                        nestedLoopControl.LogicCommandRequested += (s, e) => LogicCommandRequested?.Invoke(this, e);
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "订阅嵌套复杂节点事件失败: {NodeName}", nestedNode?.DisplayName);
            }
        }

        /// <summary>
        /// 创建子节点之间的垂直连接线 + 向下箭头
        /// 线和箭头都严格居中对齐
        /// </summary>
        private FrameworkElement CreateConnectionArrow()
        {
            const double totalHeight = 20.0;
            const double lineTop = 2.0;
            const double arrowTipBottom = 2.0;
            const double arrowH = 5.0;
            const double arrowHalfW = 4.0;
            var strokeColor = new SolidColorBrush(Color.FromRgb(0x97, 0x97, 0x97));

            // 用一个固定高度的 Border 包裹一个 Canvas
            // Canvas 不参与宽度计算，箭头始终画在 LoopChildrenContent 的水平中心
            var wrapper = new Border
            {
                Height = totalHeight,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Background = Brushes.Transparent,
                Tag = "ConnectionArrow",
                ClipToBounds = false
            };

            // SizeChanged 时根据实际宽度把线和箭头画在正中间
            wrapper.Loaded += (s, e) =>
            {
                DrawArrowInWrapper(wrapper, totalHeight, lineTop, arrowTipBottom, arrowH, arrowHalfW, strokeColor);
            };
            wrapper.SizeChanged += (s, e) =>
            {
                DrawArrowInWrapper(wrapper, totalHeight, lineTop, arrowTipBottom, arrowH, arrowHalfW, strokeColor);
            };

            return wrapper;
        }

        /// <summary>
        /// 在 wrapper 内绘制居中的竖线+箭头
        /// </summary>
        private void DrawArrowInWrapper(Border wrapper, double totalHeight,
            double lineTop, double arrowTipBottom, double arrowH, double arrowHalfW,
            SolidColorBrush strokeColor)
        {
            // 获取父级 StackPanel 的宽度作为居中参考
            double centerX;
            if (wrapper.ActualWidth > 0)
                centerX = wrapper.ActualWidth / 2.0;
            else
            {
                // 还没渲染，用 LoopChildrenContent 的宽度
                centerX = LoopChildrenContent.ActualWidth > 0
                    ? LoopChildrenContent.ActualWidth / 2.0
                    : DEFAULT_WIDTH / 2.0;
            }

            double lineBottom = totalHeight - arrowTipBottom - arrowH;

            // 清除旧内容
            var canvas = wrapper.Child as Canvas;
            if (canvas == null)
            {
                canvas = new Canvas
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment = VerticalAlignment.Stretch,
                    ClipToBounds = false
                };
                wrapper.Child = canvas;
            }
            canvas.Children.Clear();

            // 竖线
            var line = new Line
            {
                X1 = centerX,
                Y1 = lineTop,
                X2 = centerX,
                Y2 = lineBottom,
                Stroke = strokeColor,
                StrokeThickness = 1.5
            };
            canvas.Children.Add(line);

            // 向下箭头三角
            var arrow = new Polygon
            {
                Fill = strokeColor,
                Points = new PointCollection(new[]
                {
                    new Point(centerX - arrowHalfW, lineBottom),
                    new Point(centerX + arrowHalfW, lineBottom),
                    new Point(centerX, lineBottom + arrowH)
                })
            };
            canvas.Children.Add(arrow);
        }

        private void UpdateChildrenDisplay(bool skipSyncWidths = false)
        {
            try
            {
                if (DataContext is not CommandNode loopNode)
                    return;

                LoopChildrenContent.Children.Clear();
                _loopCommands.Clear();

                foreach (var child in loopNode.Children)
                {
                    _loopCommands.Add(child);
                }

                bool hasChildren = _loopCommands.Count > 0;
                EmptyHint.Text = hasChildren ? "拖拽更多步骤" : "+ 拖拽步骤到此处";

                for (int i = 0; i < _loopCommands.Count; i++)
                {
                    var child = _loopCommands[i];
                    if (i > 0)
                    {
                        LoopChildrenContent.Children.Add(CreateConnectionArrow());
                    }
                    var childControl = _nodeControlFactory.CreateNestedCommandControl(
                        child,
                        HandleNestedNodeClick,
                        RemoveLoopCommand);
                    LoopChildrenContent.Children.Add(childControl);
                    SubscribeToNestedComplexNodeEvents(child);
                }

                UpdateSummaryText();

                //  只有非删除场景才在这里调用 SyncWidths
                if (!skipSyncWidths)
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        SyncWidths();
                    }), DispatcherPriority.Loaded);
                }

                _logger.LogDebug("循环节点UI更新完成，子项数: {Count}", _loopCommands.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "更新子命令显示失败");
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
        public void RemoveLoopCommand(CommandNode childNode)
        {
            try
            {
                string message = $"确定要删除节点 \"{childNode.DisplayName}\" 吗？";
                if (childNode.Children != null && childNode.Children.Count > 0)
                {
                    message = $"该节点包含 {childNode.Children.Count} 个子命令，删除后所有子命令也将被移除。\n\n确定要删除节点 \"{childNode.DisplayName}\" 吗？";
                }

                var result = MessageBox.Show(
                    message,
                    "确认删除",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (result != MessageBoxResult.Yes) return;

                if (DataContext is CommandNode loopNode && loopNode.RemoveChild(childNode))
                {
                    _loopCommands.Remove(childNode);
                    _logger.LogDebug("从循环移除命令: {NodeName}", childNode.DisplayName);

                    //  先重置所有宽度/高度约束
                    ResetWidths();

                    //  重建子节点显示（这里面会清空 LoopChildrenContent 并重新添加）
                    UpdateChildrenDisplay();

                    // 第一轮延迟：重新计算尺寸
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        this.Height = double.NaN;
                        this.MinHeight = 0;
                        LoopContentWrapper.Height = double.NaN;

                        //  关键修复：先清除 RepeatArrowCanvas 的固定高度，打破死循环
                        RepeatArrowCanvas.Height = double.NaN;

                        LoopChildrenContent.InvalidateMeasure();
                        LoopContentWrapper.InvalidateMeasure();
                        ChildAreaGrid.InvalidateMeasure();  //  Grid 也要 Invalidate
                        this.InvalidateMeasure();
                        this.UpdateLayout();

                        SyncWidths();
                        if (_isExpanded) UpdateRepeatArrow();

                        // 通知父级内容变化（触发 RefreshNodeLayout）
                        ContentChanged?.Invoke(this, EventArgs.Empty);

                        // 第二轮延迟
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            this.UpdateLayout();
                            ContentChanged?.Invoke(this, EventArgs.Empty);
                        }), DispatcherPriority.Render);

                    }), DispatcherPriority.Loaded);

                    NestedCommandRemoved?.Invoke(this, new NestedCommandEventArgs
                    {
                        NestedCommand = childNode,
                        IsTrueBranch = false,
                        ParentIfNode = loopNode
                    });
                    UpdateChildrenDisplay(skipSyncWidths: true);
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
                    ResetWidths();
                    UpdateChildrenDisplay();

                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        this.Height = double.NaN;
                        this.MinHeight = 0;
                        LoopContentWrapper.Height = double.NaN;
                        RepeatArrowCanvas.Height = double.NaN;  //  加这行
                        LoopChildrenContent.InvalidateMeasure();
                        LoopContentWrapper.InvalidateMeasure();
                        ChildAreaGrid.InvalidateMeasure();       //  加这行
                        this.InvalidateMeasure();
                        this.UpdateLayout();

                        SyncWidths();
                        if (_isExpanded) UpdateRepeatArrow();
                        ContentChanged?.Invoke(this, EventArgs.Empty);

                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            this.UpdateLayout();
                            ContentChanged?.Invoke(this, EventArgs.Empty);
                        }), DispatcherPriority.Render);
                    }), DispatcherPriority.Loaded);
                }
                UpdateChildrenDisplay(skipSyncWidths: true);
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
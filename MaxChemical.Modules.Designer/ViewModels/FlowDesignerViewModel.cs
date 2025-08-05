using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using DevicePlugins.Devices;
using MaxChemical.Infrastructure.Services;
using MaxChemical.Modules.Designer.Event;
using MaxChemical.Modules.Designer.Models;
using MaxChemical.Modules.Designer.Plugins;
using MaxChemical.Modules.Designer.Views;
using Prism.Commands;
using Prism.Events;
using Prism.Mvvm;
using Serilog;

namespace MaxChemical.Modules.Designer.ViewModels
{
    public class FlowDesignerViewModel : BindableBase
    {
        private readonly IDeviceManager _deviceManager;
        private readonly IEventAggregator _eventAggregator;
        private readonly ILogicCommandPluginManager _pluginManager;

        // 画布属性
        private double _canvasWidth = 1200;
        private double _canvasHeight = 800;
        private double _startNodeX = 100;
        private double _startNodeY = 20;

        // 集合
        public ObservableCollection<CanvasDevice> CanvasDevices { get; private set; }
        public ObservableCollection<CommandNode> CommandNodes { get; private set; }
        public ObservableCollection<NodeConnection> Connections { get; private set; }
        public ObservableCollection<LogicCommandPluginWrapper> LogicCommandPlugins { get; private set; }

        // 命令
        public ICommand RemoveCommandNodeCommand { get; private set; }
        public ICommand ValidateAllNodesCommand { get; private set; }
        public ICommand RefreshPluginsCommand { get; private set; }
        // 添加节点点击命令
        public ICommand NodeClickCommand { get; private set; }
        // 事件
        public event EventHandler<CommandNode> NodeAdded;



        // 添加画布宽度请求事件
        public event EventHandler<CanvasWidthRequestEventArgs> CanvasWidthRequested;
     
        /// <summary>
        /// 画布宽度请求事件参数
        /// </summary>
        public class CanvasWidthRequestEventArgs : EventArgs
        {
            public double ActualCanvasWidth { get; set; }
        }
        // 添加节点可见性控制
        private bool _nodesVisible = true;
        public bool NodesVisible
        {
            get => _nodesVisible;
            set => SetProperty(ref _nodesVisible, value);
        }
        // 添加选中节点属性
        private CommandNode _selectedNode;
        public CommandNode SelectedNode
        {
            get => _selectedNode;
            set => SetProperty(ref _selectedNode, value);
        }
        public FlowDesignerViewModel(
            IDeviceManager deviceManager,
            IEventAggregator eventAggregator,
            ILogicCommandPluginManager pluginManager)
        {
            _deviceManager = deviceManager;
            _eventAggregator = eventAggregator;
            _pluginManager = pluginManager;

            // 初始化集合
            CanvasDevices = new ObservableCollection<CanvasDevice>();
            CommandNodes = new ObservableCollection<CommandNode>();
            Connections = new ObservableCollection<NodeConnection>();
            LogicCommandPlugins = new ObservableCollection<LogicCommandPluginWrapper>();

            // 初始化命令
            InitializeCommands();

            // 加载插件
            if (_pluginManager != null)
            {
                LoadLogicCommandPlugins();
            }

            // 订阅事件
            if (_eventAggregator != null)
            {
                _eventAggregator.GetEvent<CommandDragFCompleteEvent>().Subscribe(OnCommandDragComplete);
            }

            Log.Information("FlowDesignerViewModel 初始化完成");
        }

        /// <summary>
        /// 判断是否为复杂节点（需要居中对齐的逻辑命令）
        /// </summary>
        private bool IsComplexLogicCommand(LogicCommand logicCommand)
        {
            if (logicCommand == null) return false;

            var commandType = logicCommand.CommandType.ToString().ToLower();
            return commandType == "if" ||
                   commandType == "parallel" ||
                   commandType == "loop" ||
                   commandType == "for" ||
                   commandType == "while";
        }
        #region 属性

        public double CanvasWidth
        {
            get => _canvasWidth;
            set => SetProperty(ref _canvasWidth, value);
        }

        public double CanvasHeight
        {
            get => _canvasHeight;
            set => SetProperty(ref _canvasHeight, value);
        }

        public double StartNodeX
        {
            get => _startNodeX;
            set => SetProperty(ref _startNodeX, value);
        }

        public double StartNodeY
        {
            get => _startNodeY;
            set => SetProperty(ref _startNodeY, value);
        }

        #endregion

        #region 初始化方法

        private void InitializeCommands()
        {
            RemoveCommandNodeCommand = new DelegateCommand<CommandNode>(RemoveCommandNode);
            ValidateAllNodesCommand = new DelegateCommand(ValidateAllNodes);
            RefreshPluginsCommand = new DelegateCommand(RefreshPlugins);
            // 新增：节点点击命令
            NodeClickCommand = new DelegateCommand<CommandNode>(HandleNodeClick);
        }

        private void LoadLogicCommandPlugins()
        {
            try
            {
                LogicCommandPlugins.Clear();
                Debug.WriteLine("=== 开始加载逻辑命令插件 ===");

                if (_pluginManager == null)
                {
                    Debug.WriteLine("错误：插件管理器为null");
                    Log.Warning("插件管理器为null，无法加载逻辑命令插件");
                    return;
                }

                var plugins = _pluginManager.GetAllPlugins();
                Debug.WriteLine($"从插件管理器获取到 {plugins?.Count() ?? 0} 个插件");

                if (plugins == null || !plugins.Any())
                {
                    Debug.WriteLine("警告：没有找到任何插件");
                    Log.Warning("没有找到任何逻辑命令插件");
                    return;
                }

                foreach (var plugin in plugins)
                {
                    try
                    {
                        Debug.WriteLine($"正在处理插件: {plugin.Name} (ID: {plugin.Id})");
                        var wrapper = new LogicCommandPluginWrapper(plugin, this);
                        LogicCommandPlugins.Add(wrapper);
                        Debug.WriteLine($"成功添加插件: {plugin.Name}");
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"添加插件 {plugin.Name} 失败: {ex.Message}");
                        Log.Error(ex, $"添加插件 {plugin.Name} 失败");
                    }
                }

                Debug.WriteLine($"=== 插件加载完成，总计: {LogicCommandPlugins.Count} 个 ===");
                Log.Information($"已加载 {LogicCommandPlugins.Count} 个逻辑命令插件到工具栏");

                RaisePropertyChanged(nameof(LogicCommandPlugins));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"加载逻辑命令插件到工具栏失败: {ex.Message}");
                Log.Error(ex, "加载逻辑命令插件到工具栏失败");
            }
        }

        #endregion

        #region 添加节点方法

        public void AddCommandToCanvas(DeviceCommand command)
        {
            try
            {
                System.Windows.Point newPosition = CalculateNextNodePosition();

                var commandNode = new CommandNode(command)
                {
                    X = newPosition.X,
                    Y = newPosition.Y,
                    Order = CommandNodes.Count + 1
                };

                CommandNodes.Add(commandNode);

                // 立即创建连接线
                CreateConnectionForNewNode(commandNode);

                UpdateCanvasSize();

                Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
                {
                    NodeAdded?.Invoke(this, commandNode);
                }));

                Debug.WriteLine($"设备命令节点 {command.Name} 已添加，当前节点数: {CommandNodes.Count}, 连接线数: {Connections.Count}");
                Log.Information($"设备命令节点 {command.Name} 已添加到流程设计器");
            }
            catch (Exception ex)
            {
                Log.Error(ex, $"添加设备命令节点失败: {ex.Message}");
            }
        }

        public void AddCommandToCanvas(LogicCommand logicCommand)
        {
            try
            {
                Debug.WriteLine($"=== 开始添加逻辑命令: {logicCommand.Name} ===");

                // *** 关键修复：在位置计算前先确保启动节点居中 ***
                if (CommandNodes.Count == 0)
                {
                    // 如果这是第一个节点，先触发启动节点居中
                    EnsureStartNodeCentered(logicCommand);
                }

                // 阶段1: 预计算位置（现在使用正确的启动节点位置）
                var newPosition = CalculateNextNodePositionStable(logicCommand);

                var commandNode = new CommandNode(logicCommand)
                {
                    X = newPosition.X,
                    Y = newPosition.Y,
                    Order = CommandNodes.Count + 1,
                    IsVisible = false // 初始不可见
                };

                // 阶段2: 创建节点控件但不立即显示
                var plugin = _pluginManager?.GetPlugin(logicCommand.CommandType.ToString());
                if (plugin != null)
                {
                    commandNode.NodeControl = plugin.CreateNodeControl(commandNode);

                    // 根据节点类型订阅相应事件
                    if (commandNode.NodeControl is IfNodeControlView ifControl)
                    {
                        SubscribeToIfNodeEvents(ifControl, commandNode);

                        var estimatedSize = NodeSizeEstimator.EstimateIfNodeSize(0, 0);
                        ifControl.Width = estimatedSize.Width;
                        ifControl.MinWidth = estimatedSize.Width;
                    }
                    else if (commandNode.NodeControl is ParallelNodeControlView parallelControl)
                    {
                        SubscribeToParallelNodeEvents(parallelControl, commandNode);

                        var estimatedSize = NodeSizeEstimator.EstimateParallelNodeSize(commandNode);
                        parallelControl.Width = estimatedSize.Width;
                        parallelControl.MinWidth = estimatedSize.Width;
                    }
                    else if (commandNode.NodeControl is LoopNodeControlView loopControl)
                    {
                        SubscribeToLoopNodeEvents(loopControl, commandNode);

                        var estimatedSize = NodeSizeEstimator.EstimateLoopNodeSize(commandNode);
                        loopControl.Width = estimatedSize.Width;
                        loopControl.MinWidth = estimatedSize.Width;
                    }
                    else if (commandNode.NodeControl is WaitNodeControlView waitControl)
                    {
                        SubscribeToWaitNodeEvents(waitControl, commandNode);
                    }
                    else if (commandNode.NodeControl is SetVariableNodeControlView setVarControl)
                    {
                        SubscribeToSetVariableNodeEvents(setVarControl, commandNode);
                    }
                }

                // 阶段3: 添加到集合但保持不可见
                CommandNodes.Add(commandNode);
                CreateConnectionForNewNode(commandNode);

                // 阶段4: 延迟显示，确保位置计算完成
                Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
                {
                    // 显示节点
                    commandNode.IsVisible = true;

                    // 触发添加事件
                    NodeAdded?.Invoke(this, commandNode);

                    Debug.WriteLine($"节点 {logicCommand.Name} 显示完成，最终位置: ({commandNode.X}, {commandNode.Y})");

                }), System.Windows.Threading.DispatcherPriority.Loaded);

                // 更新画布大小（基于预估尺寸）
                UpdateCanvasSizeStable();

                Debug.WriteLine($"=== 逻辑命令添加完成: {logicCommand.Name} ===");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"添加逻辑命令节点失败: {ex.Message}");
                Log.Error(ex, $"添加逻辑命令节点失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 确保启动节点居中（在添加第一个节点前调用）
        /// </summary>
        private void EnsureStartNodeCentered(LogicCommand upcomingCommand = null)
        {
            try
            {
                Debug.WriteLine("=== 开始确保启动节点居中 ===");
                Debug.WriteLine($"当前启动节点位置: ({StartNodeX}, {StartNodeY})");
                Debug.WriteLine($"当前画布宽度: {CanvasWidth}");
                Debug.WriteLine($"即将添加的命令: {upcomingCommand?.Name ?? "null"}");

                // 获取当前画布宽度
                double actualCanvasWidth = 0;
                Application.Current?.Dispatcher.Invoke(() =>
                {
                    var args = new CanvasWidthRequestEventArgs();
                    CanvasWidthRequested?.Invoke(this, args);
                    actualCanvasWidth = args.ActualCanvasWidth;
                    Debug.WriteLine($"通过事件获取的画布宽度: {actualCanvasWidth}");
                });

                if (actualCanvasWidth <= 0)
                {
                    actualCanvasWidth = Math.Max(CanvasWidth, 1200);
                    Debug.WriteLine($"使用默认画布宽度: {actualCanvasWidth}");
                }

                // 预测添加节点后的画布宽度
                double predictedCanvasWidth = actualCanvasWidth;
                if (CommandNodes.Count == 0 && upcomingCommand != null)
                {
                    // 根据即将添加的节点类型预测画布扩展
                    double nodeWidth = NodeSizeEstimator.GetComplexNodeWidth(upcomingCommand);
                    double nodeX = actualCanvasWidth / 2 - nodeWidth / 2; // 预计节点位置

                    // 计算节点右边界
                    double nodeRightBoundary = nodeX + nodeWidth;

                    // 如果节点会超出当前画布，预测新的画布宽度
                    if (nodeRightBoundary > actualCanvasWidth)
                    {
                        predictedCanvasWidth = nodeRightBoundary + 100; // 添加一些边距
                        Debug.WriteLine($"节点会超出画布，预测扩展后宽度: {predictedCanvasWidth}");
                    }
                    else
                    {
                        // 即使不超出，也可能有最小扩展（基于之前的日志观察）
                        predictedCanvasWidth = Math.Max(actualCanvasWidth, 1260);
                        Debug.WriteLine($"使用观察到的最小扩展宽度: {predictedCanvasWidth}");
                    }
                }

                // 计算正确的居中位置
                const double startNodeWidth = 231;
                double correctCenterX = (predictedCanvasWidth - startNodeWidth) / 2;
                double correctStartY = 10;

                Debug.WriteLine($"基于预测宽度 {predictedCanvasWidth} 计算的启动节点位置: ({correctCenterX}, {correctStartY})");

                // 强制更新启动节点位置
                if (Math.Abs(StartNodeX - correctCenterX) > 0.1 || Math.Abs(StartNodeY - correctStartY) > 0.1)
                {
                    Debug.WriteLine($"强制更新启动节点位置: ({StartNodeX}, {StartNodeY}) -> ({correctCenterX}, {correctStartY})");
                    StartNodeX = correctCenterX;
                    StartNodeY = correctStartY;
                }
                else
                {
                    Debug.WriteLine("启动节点位置已经正确，无需更新");
                }

                Debug.WriteLine($"确保启动节点居中完成: ({StartNodeX}, {StartNodeY})");
                Debug.WriteLine("=== 启动节点居中确保完成 ===");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"确保启动节点居中失败: {ex.Message}");
                Log.Error(ex, "确保启动节点居中失败");
            }
        }

        /// <summary>
        /// 订阅并行节点事件的方法
        /// </summary>
        private void SubscribeToParallelNodeEvents(ParallelNodeControlView parallelControl, CommandNode commandNode)
        {
            try
            {
                // 订阅内容变化事件
                parallelControl.ContentChanged += (s, e) =>
                {
                    Debug.WriteLine($"并行节点内容发生变化: {commandNode.DisplayName}");
                    Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        RefreshNodeLayout(commandNode);
                    }), System.Windows.Threading.DispatcherPriority.Background);
                };

                // 订阅嵌套命令添加事件
                parallelControl.NestedCommandAdded += (s, e) =>
                {
                    Debug.WriteLine($"嵌套命令已添加到并行节点: {e.NestedCommand.DisplayName} -> 分支{e.BranchIndex}");
                    HandleNestedCommandAdded(e);
                };

                // 订阅嵌套命令移除事件
                parallelControl.NestedCommandRemoved += (s, e) =>
                {
                    Debug.WriteLine($"嵌套命令已从并行节点移除: {e.NestedCommand.DisplayName} -> 分支{e.BranchIndex}");
                    HandleNestedCommandRemoved(e);
                };

                // 订阅LogicCommand创建请求事件
                parallelControl.LogicCommandRequested += (s, e) =>
                {
                    var createdCommand = CreateLogicCommandFromPluginId(e.PluginId);
                    e.ResultCommand = createdCommand;
                    Debug.WriteLine($"为嵌套拖拽创建LogicCommand: {createdCommand?.Name ?? "失败"}");
                };
                // **新增：订阅删除请求事件**
                parallelControl.NodeDeleteRequested += (s, nodeToDelete) =>
                {
                    Debug.WriteLine($"收到并行节点删除请求: {nodeToDelete.DisplayName}");
                    HandleNodeDeleteRequest(nodeToDelete);
                };
                Debug.WriteLine($"为并行节点 {commandNode.DisplayName} 订阅了所有事件");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"订阅并行节点事件失败: {ex.Message}");
                Log.Error(ex, "订阅并行节点事件失败");
            }
        }

        /// <summary>
        /// 稳定的节点位置计算（基于预估尺寸）
        /// </summary>
        private System.Windows.Point CalculateNextNodePositionStable(LogicCommand logicCommand = null)
        {
            const double nodeSpacing = 80;
            const double standardNodeWidth = NodeSizeEstimator.DEVICE_NODE_WIDTH;
            const double standardNodeHeight = NodeSizeEstimator.DEVICE_NODE_HEIGHT;

            Debug.WriteLine($"=== 位置计算详细信息 ===");
            Debug.WriteLine($"计算前启动节点位置: StartNodeX={StartNodeX}, StartNodeY={StartNodeY}");
            Debug.WriteLine($"当前节点数量: {CommandNodes.Count}");

            if (CommandNodes.Count == 0)
            {
                Debug.WriteLine("处理第一个节点");

                if (IsComplexLogicCommand(logicCommand))
                {
                    // 复杂逻辑节点：与启动节点中心对齐
                    double startNodeCenterX = StartNodeX + (standardNodeWidth / 2);
                    double estimatedWidth = NodeSizeEstimator.GetComplexNodeWidth(logicCommand);
                    double nodeX = startNodeCenterX - (estimatedWidth / 2);

                    Debug.WriteLine($"第一个复杂节点位置计算:");
                    Debug.WriteLine($"  启动节点X: {StartNodeX}");
                    Debug.WriteLine($"  启动节点宽度: {standardNodeWidth}");
                    Debug.WriteLine($"  启动节点中心X: {startNodeCenterX}");
                    Debug.WriteLine($"  当前节点预估宽度: {estimatedWidth}");
                    Debug.WriteLine($"  计算的X位置: {nodeX}");

                    // 验证计算结果是否合理
                    if (nodeX < 0)
                    {
                        Debug.WriteLine($"警告：计算出的X位置为负数: {nodeX}");
                    }

                    var result = new System.Windows.Point(nodeX, StartNodeY + standardNodeHeight + nodeSpacing);
                    Debug.WriteLine($"第一个复杂节点最终计算位置: ({result.X}, {result.Y})");
                    Debug.WriteLine($"============================");
                    return result;
                }
                else
                {
                    Debug.WriteLine($"第一个普通节点位置: ({StartNodeX}, {StartNodeY + standardNodeHeight + nodeSpacing})");
                    Debug.WriteLine($"============================");
                    return new System.Windows.Point(StartNodeX, StartNodeY + standardNodeHeight + nodeSpacing);
                }
            }

            // 后续节点逻辑保持不变...
            var lastNode = CommandNodes.Last();
            double lastNodeHeight = GetEstimatedNodeHeight(lastNode);
            double nextY = lastNode.Y + lastNodeHeight + nodeSpacing;

            if (IsComplexLogicCommand(logicCommand))
            {
                double lastNodeCenterX = GetEstimatedNodeCenterX(lastNode);
                double estimatedWidth = NodeSizeEstimator.GetComplexNodeWidth(logicCommand);
                double nodeX = lastNodeCenterX - (estimatedWidth / 2);

                Debug.WriteLine($"后续复杂节点位置计算:");
                Debug.WriteLine($"  上一节点中心X: {lastNodeCenterX}");
                Debug.WriteLine($"  当前节点预估宽度: {estimatedWidth}");
                Debug.WriteLine($"  计算的X位置: {nodeX}");
                Debug.WriteLine($"============================");

                return new System.Windows.Point(nodeX, nextY);
            }
            else
            {
                double lastNodeCenterX = GetEstimatedNodeCenterX(lastNode);
                double normalNodeX = lastNodeCenterX - (standardNodeWidth / 2);
                Debug.WriteLine($"============================");
                return new System.Windows.Point(normalNodeX, nextY);
            }
        }


        /// <summary>
        /// 订阅循环节点事件的方法
        /// </summary>
        private void SubscribeToLoopNodeEvents(LoopNodeControlView loopControl, CommandNode commandNode)
        {
            try
            {
                // 订阅内容变化事件
                loopControl.ContentChanged += (s, e) =>
                {
                    Debug.WriteLine($"循环节点内容发生变化: {commandNode.DisplayName}");
                    Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        RefreshNodeLayout(commandNode);
                    }), System.Windows.Threading.DispatcherPriority.Background);
                };

                // 订阅嵌套命令添加事件
                loopControl.NestedCommandAdded += (s, e) =>
                {
                    Debug.WriteLine($"嵌套命令已添加到循环节点: {e.NestedCommand.DisplayName}");
                    HandleNestedCommandAdded(e);
                };

                // 订阅嵌套命令移除事件
                loopControl.NestedCommandRemoved += (s, e) =>
                {
                    Debug.WriteLine($"嵌套命令已从循环节点移除: {e.NestedCommand.DisplayName}");
                    HandleNestedCommandRemoved(e);
                };

                // 订阅LogicCommand创建请求事件
                loopControl.LogicCommandRequested += (s, e) =>
                {
                    var createdCommand = CreateLogicCommandFromPluginId(e.PluginId);
                    e.ResultCommand = createdCommand;
                    Debug.WriteLine($"为循环节点嵌套拖拽创建LogicCommand: {createdCommand?.Name ?? "失败"}");
                };
                // **新增：订阅删除请求事件**
                loopControl.NodeDeleteRequested += (s, nodeToDelete) =>
                {
                    Debug.WriteLine($"收到循环节点删除请求: {nodeToDelete.DisplayName}");
                    HandleNodeDeleteRequest(nodeToDelete);
                };
                Debug.WriteLine($"为循环节点 {commandNode.DisplayName} 订阅了所有事件");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"订阅循环节点事件失败: {ex.Message}");
                Log.Error(ex, "订阅循环节点事件失败");
            }
        }

        /// <summary>
        /// 订阅等待节点事件的方法
        /// </summary>
        private void SubscribeToWaitNodeEvents(WaitNodeControlView waitControl, CommandNode commandNode)
        {
            try
            {
                // **订阅删除请求事件**
                waitControl.NodeDeleteRequested += (s, nodeToDelete) =>
                {
                    Debug.WriteLine($"收到等待节点删除请求: {nodeToDelete.DisplayName}");
                    HandleNodeDeleteRequest(nodeToDelete);
                };

                // **订阅点击事件（如果需要）**
                waitControl.AddHandler(WaitNodeControlView.NodeClickEvent, new RoutedEventHandler((s, e) =>
                {
                    if (e.OriginalSource is RoutedEventArgs args && args.Source is CommandNode clickedNode)
                    {
                        Debug.WriteLine($"等待节点被点击: {clickedNode.DisplayName}");
                        HandleNodeClick(clickedNode);
                    }
                }));

                Debug.WriteLine($"为等待节点 {commandNode.DisplayName} 订阅了所有事件");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"订阅等待节点事件失败: {ex.Message}");
                Log.Error(ex, "订阅等待节点事件失败");
            }
        }

        /// <summary>
        /// 订阅设置变量节点事件的方法
        /// </summary>
        private void SubscribeToSetVariableNodeEvents(SetVariableNodeControlView setVarControl, CommandNode commandNode)
        {
            try
            {
                // **订阅删除请求事件**
                setVarControl.NodeDeleteRequested += (s, nodeToDelete) =>
                {
                    Debug.WriteLine($"收到设置变量节点删除请求: {nodeToDelete.DisplayName}");
                    HandleNodeDeleteRequest(nodeToDelete);
                };

                // **订阅点击事件（如果需要）**
                setVarControl.AddHandler(SetVariableNodeControlView.NodeClickEvent, new RoutedEventHandler((s, e) =>
                {
                    if (e.OriginalSource is RoutedEventArgs args && args.Source is CommandNode clickedNode)
                    {
                        Debug.WriteLine($"设置变量节点被点击: {clickedNode.DisplayName}");
                        HandleNodeClick(clickedNode);
                    }
                }));

                Debug.WriteLine($"为设置变量节点 {commandNode.DisplayName} 订阅了所有事件");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"订阅设置变量节点事件失败: {ex.Message}");
                Log.Error(ex, "订阅设置变量节点事件失败");
            }
        }
        /// <summary>
        /// 获取节点的预估高度
        /// </summary>
        private double GetEstimatedNodeHeight(CommandNode node)
        {
            return NodeSizeEstimator.GetNodeHeight(node);
        }

        /// <summary>
        /// 获取节点的预估中心X坐标
        /// </summary>
        private double GetEstimatedNodeCenterX(CommandNode node)
        {
            try
            {
                Debug.WriteLine($"GetEstimatedNodeCenterX - 开始计算节点 {node.DisplayName} 的中心");
                Debug.WriteLine($"  节点类型: {node.CommandType}");
                Debug.WriteLine($"  节点X位置: {node.X}");

                // 优先使用实际中心（如果节点控件已经创建并渲染）
                if (node.NodeControl != null)
                {
                    try
                    {
                        var actualCenterX = node.GetMainCardCenterX();
                        if (actualCenterX > 0)
                        {
                            Debug.WriteLine($"  使用实际中心X: {actualCenterX}");
                            return actualCenterX;
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"  获取实际中心失败: {ex.Message}");
                    }
                }

                // 使用预估宽度计算中心
                var estimatedWidth = NodeSizeEstimator.GetNodeWidth(node);
                var centerX = node.X + estimatedWidth / 2;

                Debug.WriteLine($"  使用预估计算 - 宽度: {estimatedWidth}, 中心X: {centerX}");
                return centerX;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"获取节点中心X坐标失败: {ex.Message}");
                // 使用默认计算
                double defaultWidth = node.IsIfNode ? NodeSizeEstimator.IF_NODE_MIN_WIDTH :
                                     node.IsParallelNode ? NodeSizeEstimator.PARALLEL_NODE_MIN_WIDTH :
                                     NodeSizeEstimator.DEVICE_NODE_WIDTH;
                var fallbackCenterX = node.X + defaultWidth / 2;
                Debug.WriteLine($"  使用默认计算 - 默认宽度: {defaultWidth}, 中心X: {fallbackCenterX}");
                return fallbackCenterX;
            }
        }

        /// <summary>
        /// 最终化节点位置（基于预估尺寸的微调）
        /// </summary>
        private void FinalizeNodePosition(CommandNode node)
        {
            try
            {
                // 这里可以进行基于预估尺寸的最终位置微调
                // 但不依赖实际渲染尺寸，避免跳动

                Debug.WriteLine($"最终化节点位置: {node.DisplayName} at ({node.X}, {node.Y})");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"最终化节点位置失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 稳定的画布大小更新（基于预估尺寸）
        /// </summary>
        private void UpdateCanvasSizeStable()
        {
            try
            {
                if (CommandNodes.Count == 0)
                {
                    CanvasWidth = Math.Max(1200, StartNodeX + 400);
                    CanvasHeight = Math.Max(800, StartNodeY + 200);
                    return;
                }

                double maxX = StartNodeX + 231; // 启动节点宽度
                double maxY = StartNodeY + 64;  // 启动节点高度

                foreach (var node in CommandNodes)
                {
                    var estimatedSize = NodeSizeEstimator.EstimateNodeSize(node);
                    double nodeRight = node.X + estimatedSize.Width;
                    double nodeBottom = node.Y + estimatedSize.Height;

                    maxX = Math.Max(maxX, nodeRight);
                    maxY = Math.Max(maxY, nodeBottom);
                }

                CanvasWidth = Math.Max(1200, maxX + 100);
                CanvasHeight = Math.Max(800, maxY + 200);

                Debug.WriteLine($"稳定画布大小更新: {CanvasWidth}x{CanvasHeight}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"更新稳定画布大小失败: {ex.Message}");
            }
        }
        /// <summary>
        /// 订阅IF节点的所有事件
        /// </summary>
        private void SubscribeToIfNodeEvents(IfNodeControlView ifControl, CommandNode commandNode)
        {
            try
            {
                // 订阅内容变化事件
                ifControl.ContentChanged += (s, e) =>
                {
                    Debug.WriteLine($"IF节点内容发生变化: {commandNode.DisplayName}");
                    Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        RefreshNodeLayout(commandNode);
                    }), System.Windows.Threading.DispatcherPriority.Background);

                    //RequestScrollToNode(changedNode);
                };

                // 订阅嵌套命令添加事件
                ifControl.NestedCommandAdded += (s, e) =>
                {
                    Debug.WriteLine($"嵌套命令已添加到IF节点: {e.NestedCommand.DisplayName} -> {(e.IsTrueBranch ? "TRUE" : "FALSE")}分支");
                    HandleNestedCommandAdded(e);
                };

                // 订阅嵌套命令移除事件
                ifControl.NestedCommandRemoved += (s, e) =>
                {
                    Debug.WriteLine($"嵌套命令已从IF节点移除: {e.NestedCommand.DisplayName} -> {(e.IsTrueBranch ? "TRUE" : "FALSE")}分支");
                    HandleNestedCommandRemoved(e);
                };

                // 订阅LogicCommand创建请求事件
                ifControl.LogicCommandRequested += (s, e) =>
                {
                    var createdCommand = CreateLogicCommandFromPluginId(e.PluginId);
                    e.ResultCommand = createdCommand;
                    Debug.WriteLine($"为嵌套拖拽创建LogicCommand: {createdCommand?.Name ?? "失败"}");
                };
                // **新增：订阅删除请求事件**
                ifControl.NodeDeleteRequested += (s, nodeToDelete) =>
                {
                    Debug.WriteLine($"收到IF节点删除请求: {nodeToDelete.DisplayName}");
                    HandleNodeDeleteRequest(nodeToDelete);
                };

                Debug.WriteLine($"为IF节点 {commandNode.DisplayName} 订阅了所有事件");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"订阅IF节点事件失败: {ex.Message}");
                Log.Error(ex, "订阅IF节点事件失败");
            }
        }
        /// <summary>
        /// 处理节点删除请求
        /// </summary>
        private void HandleNodeDeleteRequest(CommandNode nodeToDelete)
        {
            try
            {
                if (nodeToDelete == null) return;

                Debug.WriteLine($"=== 处理节点删除请求: {nodeToDelete.DisplayName} ===");

                // 根据节点类型生成确认消息
                string confirmMessage = GenerateDeleteConfirmMessage(nodeToDelete);

                var result = MessageBox.Show(
                    confirmMessage,
                    "确认删除",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question,
                    MessageBoxResult.No);

                if (result == MessageBoxResult.Yes)
                {
                    RemoveCommandNode(nodeToDelete);
                    Debug.WriteLine($"节点 {nodeToDelete.DisplayName} 已删除");
                }
                else
                {
                    Debug.WriteLine($"用户取消删除节点 {nodeToDelete.DisplayName}");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"处理节点删除请求失败: {ex.Message}");
                Log.Error(ex, "处理节点删除请求失败");
                MessageBox.Show($"删除节点时发生错误：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        /// <summary>
        /// 生成删除确认消息
        /// </summary>
        private string GenerateDeleteConfirmMessage(CommandNode node)
        {
            string nodeType = node.IsIfNode ? "条件判断节点" :
                             node.IsLoopNode ? "循环节点" :
                             node.IsParallelNode ? "并行节点" :
                             "节点";

            string childrenInfo = "";
            if (node.HasChildren)
            {
                childrenInfo = $"\n\n该{nodeType}包含 {node.ChildrenCount} 个子命令，删除后所有子命令也将被移除。";
            }

            return $"确定要删除{nodeType} '{node.DisplayName}' 吗？{childrenInfo}";
        }
        public void AddDeviceToCanvas(IDevice device, System.Windows.Point position)
        {
            try
            {
                var canvasDevice = new CanvasDevice(device)
                {
                    X = position.X,
                    Y = position.Y
                };

                CanvasDevices.Add(canvasDevice);
                UpdateCanvasSize();

                Log.Information($"设备 {device.Name} 已添加到画布");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "添加设备到画布失败");
            }
        }

        #endregion

        #region 嵌套命令处理

        /// <summary>
        /// 处理嵌套命令添加事件
        /// </summary>
        private void HandleNestedCommandAdded(NestedCommandEventArgs e)
        {
            try
            {
                // 这里可以添加嵌套命令的管理逻辑
                // 例如：记录嵌套关系、更新执行顺序等

                Debug.WriteLine($"处理嵌套命令添加: {e.NestedCommand.DisplayName}");

                // 如果需要在全局集合中跟踪嵌套命令，可以在这里实现
                // 例如：NestedCommands.Add(new NestedCommandInfo { ... });

                // 触发布局更新
                if (e.ParentIfNode != null)
                {
                    Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        RefreshNodeLayout(e.ParentIfNode);
                    }), System.Windows.Threading.DispatcherPriority.Background);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"处理嵌套命令添加失败: {ex.Message}");
                Log.Error(ex, "处理嵌套命令添加失败");
            }
        }

        /// <summary>
        /// 处理嵌套命令移除事件
        /// </summary>
        private void HandleNestedCommandRemoved(NestedCommandEventArgs e)
        {
            try
            {
                Debug.WriteLine($"处理嵌套命令移除: {e.NestedCommand.DisplayName}");

                // 这里可以添加嵌套命令的清理逻辑
                // 例如：从全局集合中移除、清理资源等

                // 触发布局更新
                if (e.ParentIfNode != null)
                {
                    Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        RefreshNodeLayout(e.ParentIfNode);
                    }), System.Windows.Threading.DispatcherPriority.Background);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"处理嵌套命令移除失败: {ex.Message}");
                Log.Error(ex, "处理嵌套命令移除失败");
            }
        }

        /// <summary>
        /// 根据插件ID创建LogicCommand
        /// </summary>
        public LogicCommand CreateLogicCommandFromPluginId(string pluginId)
        {
            try
            {
                if (string.IsNullOrEmpty(pluginId))
                {
                    Debug.WriteLine("插件ID为空，无法创建LogicCommand");
                    return null;
                }

                var plugin = _pluginManager?.GetPlugin(pluginId);
                if (plugin == null)
                {
                    Debug.WriteLine($"未找到插件: {pluginId}");
                    return null;
                }

                // 根据插件创建LogicCommand
                var logicCommand = new LogicCommand
                {
                    CommandType = Enum.TryParse<LogicCommandType>(plugin.Name, out var commandType)
                        ? commandType
                        : LogicCommandType.Unknown,
                    Name = plugin.Name,
                    HelpText = plugin.Description,
                    ImageLocation = plugin.IconPath
                };

                // 初始化默认属性
                if (logicCommand.Properties == null)
                {
                    logicCommand.Properties = new System.Collections.Generic.Dictionary<string, object>();
                }

                // 根据插件类型设置默认属性
                SetDefaultPropertiesForCommand(logicCommand, plugin);

                Debug.WriteLine($"成功创建LogicCommand: {logicCommand.Name}");
                return logicCommand;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"根据插件ID创建LogicCommand失败: {ex.Message}");
                Log.Error(ex, $"根据插件ID创建LogicCommand失败: {pluginId}");
                return null;
            }
        }

        /// <summary>
        /// 为命令设置默认属性
        /// </summary>
        private void SetDefaultPropertiesForCommand(LogicCommand command, ILogicCommandPlugin plugin)
        {
            try
            {
                switch (command.CommandType)
                {
                    case LogicCommandType.If:
                        command.Properties["LeftOperand"] = "变量";
                        command.Properties["Operator"] = "==";
                        command.Properties["RightOperand"] = "值";
                        break;

                    case LogicCommandType.Loop:
                        command.Properties["LoopCount"] = "10";
                        command.Properties["LoopVariable"] = "i";
                        break;

                    case LogicCommandType.Wait:
                        command.Properties["WaitTime"] = "1000";
                        command.Properties["WaitType"] = "Milliseconds";
                        break;

                    default:
                        // 其他类型的默认属性
                        break;
                }

                Debug.WriteLine($"为命令 {command.Name} 设置了默认属性");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"设置命令默认属性失败: {ex.Message}");
            }
        }

        #endregion

        #region 布局方法

        private System.Windows.Point CalculateNextNodePosition(LogicCommand logicCommand = null)
        {
            const double nodeSpacing = 80;
            const double standardNodeWidth = 231;
            const double standardNodeHeight = 64;

            if (CommandNodes.Count == 0)
            {
                // 第一个节点紧接在启动节点下方
                if (IsComplexLogicCommand(logicCommand))
                {
                    // 复杂逻辑节点：与启动节点中心对齐
                    double startNodeCenterX = StartNodeX + (standardNodeWidth / 2);
                    double estimatedWidth = NodeSizeEstimator.GetComplexNodeWidth(logicCommand);
                    double nodeX = startNodeCenterX - (estimatedWidth / 2);
                    return new System.Windows.Point(nodeX, StartNodeY + standardNodeHeight + nodeSpacing);
                }
                else
                {
                    // 普通节点
                    return new System.Windows.Point(StartNodeX, StartNodeY + standardNodeHeight + nodeSpacing);
                }
            }

            // 后续节点依次向下排列
            var lastNode = CommandNodes.Last();
            double nextY;

            // 根据上一个节点的实际高度计算Y位置
            try
            {
                double lastNodeActualHeight = lastNode.GetActualHeight();
                nextY = lastNode.Y + lastNodeActualHeight + nodeSpacing;
                Debug.WriteLine($"上一个节点实际高度: {lastNodeActualHeight}, 新节点Y位置: {nextY}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"获取上一个节点高度失败: {ex.Message}，使用默认值");
                // 如果获取实际高度失败，使用默认值
                double defaultHeight = GetDefaultNodeHeight(lastNode);
                nextY = lastNode.Y + defaultHeight + nodeSpacing;
            }

            if (IsComplexLogicCommand(logicCommand))
            {
                // 当前是复杂逻辑节点：与上一个节点中心对齐
                double lastNodeCenterX = GetNodeCenterX(lastNode);
                double estimatedWidth = NodeSizeEstimator.GetComplexNodeWidth(logicCommand);
                double nodeX = lastNodeCenterX - (estimatedWidth / 2);
                return new System.Windows.Point(nodeX, nextY);
            }
            else
            {
                // 当前是普通节点：与上一个节点中心对齐
                double lastNodeCenterX = GetNodeCenterX(lastNode);
                double normalNodeX = lastNodeCenterX - (standardNodeWidth / 2);
                return new System.Windows.Point(normalNodeX, nextY);
            }
        }
        /// <summary>
        /// 获取默认节点高度的方法
        /// </summary>
        private double GetDefaultNodeHeight(CommandNode node)
        {
            return NodeSizeEstimator.GetDefaultNodeHeight(node);
        }

        // 辅助方法：判断是否为IF命令
        private bool IsIfCommand(LogicCommand logicCommand)
        {
            return logicCommand != null && logicCommand.CommandType.ToString() == "If";
        }

        // 获取节点中心X坐标的辅助方法
        private double GetNodeCenterX(CommandNode node)
        {
            try
            {
                return node.GetMainCardCenterX();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"获取节点中心X坐标失败: {ex.Message}，使用默认值");
                // 如果获取失败，使用节点X坐标加上默认宽度的一半
                double defaultWidth = node.IsIfNode ? 600 : 231;
                return node.X + defaultWidth / 2;
            }
        }

        /// <summary>
        /// 当IF节点内容变化时，重新调整布局
        /// </summary>
        public void RefreshNodeLayout(CommandNode changedNode)
        {
            try
            {
                if (changedNode == null) return;

                System.Diagnostics.Debug.WriteLine($"=== 刷新节点布局: {changedNode.DisplayName} ===");

                // 延迟执行，确保IF节点内部布局已完成
                Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
                {
                    // 找到变化节点的索引
                    int changedIndex = CommandNodes.IndexOf(changedNode);
                    if (changedIndex == -1)
                    {
                        System.Diagnostics.Debug.WriteLine("未找到变化的节点");
                        return;
                    }

                    // 强制更新变化节点的布局
                    if (changedNode.NodeControl != null)
                    {
                        changedNode.NodeControl.UpdateLayout();
                    }

                    // 重新排列后续所有节点
                    RearrangeNodesFromIndex(changedIndex + 1);

                    // 重建连接线
                    RebuildAllConnections();

                    // 更新画布大小
                    UpdateCanvasSize();

                    System.Diagnostics.Debug.WriteLine("=== 节点布局刷新完成 ===");

                }), System.Windows.Threading.DispatcherPriority.Background);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"刷新节点布局失败: {ex.Message}");
                Log.Error(ex, "刷新节点布局失败");
            }
        }
        /// <summary>
        /// 获取节点的预估宽度
        /// </summary>
        private double GetEstimatedNodeWidth(CommandNode node)
        {
            return NodeSizeEstimator.GetNodeWidth(node);
        }
        /// <summary>
        /// 从指定索引开始重新排列节点
        /// </summary>
        private void RearrangeNodesFromIndex(int startIndex)
        {
            try
            {
                if (startIndex >= CommandNodes.Count) return;

                System.Diagnostics.Debug.WriteLine($"从索引 {startIndex} 开始重新排列节点");
                const double nodeSpacing = 80;

                for (int i = startIndex; i < CommandNodes.Count; i++)
                {
                    var currentNode = CommandNodes[i];
                    var previousNode = CommandNodes[i - 1];

                    // 获取上一个节点的实际高度
                    double previousHeight = GetEstimatedNodeHeight(previousNode);
                    double newY = previousNode.Y + previousHeight + nodeSpacing;

                    // 保持X位置的中心对齐
                    double previousCenterX = GetEstimatedNodeCenterX(previousNode);
                    double newX;

                    if (currentNode.IsComplexNode) // 使用新的复杂节点判断
                    {
                        double currentNodeWidth = GetEstimatedNodeWidth(currentNode);
                        newX = previousCenterX - (currentNodeWidth / 2);
                    }
                    else
                    {
                        newX = previousCenterX - (NodeSizeEstimator.DEVICE_NODE_WIDTH / 2);
                    }

                    currentNode.X = newX;
                    currentNode.Y = newY;

                    System.Diagnostics.Debug.WriteLine($"重排节点 {i}: {currentNode.DisplayName} -> ({newX}, {newY})");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"重新排列节点失败: {ex.Message}");
            }
        }

        private void CreateConnectionForNewNode(CommandNode toNode)
        {
            try
            {
                NodeConnection connection;

                if (CommandNodes.Count == 1)
                {
                    // 第一个节点连接到启动节点
                    double startNodeCenterX = StartNodeX + 115;
                    var toConnectionPoint = toNode.GetTopConnectionPoint();

                    connection = new NodeConnection
                    {
                        FromNodeId = "StartNode",
                        ToNodeId = toNode.NodeId,
                        X = startNodeCenterX,
                        Y = StartNodeY + 64 + 2,
                        LineLength = (int)(toConnectionPoint.Y - (StartNodeY + 64 + 2) - 8), // 动态计算连接线长度
                        ArrowTop = (int)(toConnectionPoint.Y - (StartNodeY + 64 + 2) - 8),
                        TotalHeight = (int)(toConnectionPoint.Y - (StartNodeY + 64 + 2))
                    };
                }
                else
                {
                    // 连接到前一个节点
                    var previousNode = CommandNodes[CommandNodes.Count - 2];

                    var fromConnectionPoint = previousNode.GetBottomConnectionPoint();
                    var toConnectionPoint = toNode.GetTopConnectionPoint();

                    connection = new NodeConnection
                    {
                        FromNodeId = previousNode.NodeId,
                        ToNodeId = toNode.NodeId,
                        X = fromConnectionPoint.X,
                        Y = fromConnectionPoint.Y + 2,
                        LineLength = (int)(toConnectionPoint.Y - fromConnectionPoint.Y - 10), // 动态计算连接线长度
                        ArrowTop = (int)(toConnectionPoint.Y - fromConnectionPoint.Y - 10),
                        TotalHeight = (int)(toConnectionPoint.Y - fromConnectionPoint.Y - 2)
                    };
                }

                Connections.Add(connection);
                Debug.WriteLine($"创建连接线: 从 {connection.FromNodeId} 到 {connection.ToNodeId}, 位置: X={connection.X}, Y={connection.Y}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"创建连接线失败: {ex.Message}");
                Log.Error(ex, "创建连接线失败");
            }
        }

        private void RebuildAllConnections()
        {
            try
            {
                Debug.WriteLine("=== 开始重建所有连接线 ===");

                // 清空现有连接线
                Connections.Clear();

                // 为每个节点重新创建连接线
                for (int i = 0; i < CommandNodes.Count; i++)
                {
                    var currentNode = CommandNodes[i];
                    NodeConnection connection;

                    if (i == 0)
                    {
                        // 第一个节点连接到启动节点
                        double startNodeCenterX = StartNodeX + 115;

                        connection = new NodeConnection
                        {
                            FromNodeId = "StartNode",
                            ToNodeId = currentNode.NodeId,
                            X = startNodeCenterX,
                            Y = StartNodeY + 64 + 2,
                            LineLength = 60,
                            ArrowTop = 60,
                            TotalHeight = 68
                        };
                    }
                    else
                    {
                        // 连接到前一个节点
                        var previousNode = CommandNodes[i - 1];
                        double fromX = GetNodeCenterX(previousNode);

                        // 修正：使用整个节点的实际高度
                        double fromY;
                        try
                        {
                            double previousHeight = previousNode.GetActualHeight();
                            fromY = previousNode.Y + previousHeight + 2; // 从整个节点底部开始
                        }
                        catch
                        {
                            // 如果获取实际高度失败，使用默认值
                            double defaultHeight = previousNode.IsIfNode ? 284 : 64;
                            fromY = previousNode.Y + defaultHeight + 2;
                        }

                        connection = new NodeConnection
                        {
                            FromNodeId = previousNode.NodeId,
                            ToNodeId = currentNode.NodeId,
                            X = fromX,
                            Y = fromY,
                            LineLength = 60,
                            ArrowTop = 60,
                            TotalHeight = 68
                        };
                    }

                    Connections.Add(connection);
                    Debug.WriteLine($"重建连接线 {i + 1}: 从 {connection.FromNodeId} 到 {connection.ToNodeId}, 位置: ({connection.X}, {connection.Y})");
                }

                Debug.WriteLine($"=== 连接线重建完成，总数: {Connections.Count} ===");

                // 强制更新UI
                RaisePropertyChanged(nameof(Connections));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"重建连接线失败: {ex.Message}");
                Log.Error(ex, "重建连接线失败");
            }
        }

        private void RearrangeNodes()
        {
            try
            {
                Debug.WriteLine("=== 开始重新排列节点 ===");
                const double nodeSpacing = 80;
                const double standardNodeHeight = 64;

                // 重新计算所有节点位置
                for (int i = 0; i < CommandNodes.Count; i++)
                {
                    var node = CommandNodes[i];
                    node.Order = i + 1;

                    if (i == 0)
                    {
                        // 第一个节点
                        if (node.IsIfNode)
                        {
                            double startNodeCenterX = StartNodeX + 115;
                            double nodeActualWidth = node.GetActualWidth();
                            node.X = startNodeCenterX - (nodeActualWidth / 2);
                            Debug.WriteLine($"第一个IF节点 {node.DisplayName}: 中心对齐后X={node.X}");
                        }
                        else if (node.IsParallelNode) // 添加并行节点处理
                        {
                            double startNodeCenterX = StartNodeX + 115;
                            double nodeActualWidth = node.GetActualWidth();
                            node.X = startNodeCenterX - (nodeActualWidth / 2);
                            Debug.WriteLine($"第一个并行节点 {node.DisplayName}: 中心对齐后X={node.X}");
                        }
                        else
                        {
                            node.X = StartNodeX;
                            Debug.WriteLine($"第一个普通节点 {node.DisplayName}: X={node.X}");
                        }
                        node.Y = StartNodeY + standardNodeHeight + nodeSpacing;
                    }
                    else
                    {
                        var previousNode = CommandNodes[i - 1];

                        // 计算Y位置
                        double previousHeight = previousNode.GetActualHeight();
                        node.Y = previousNode.Y + previousHeight + nodeSpacing;

                        // 计算X位置 - 保持中心对齐
                        double previousCenterX = GetNodeCenterX(previousNode);

                        if (node.IsIfNode)
                        {
                            double nodeActualWidth = node.GetActualWidth();
                            node.X = previousCenterX - (nodeActualWidth / 2);
                            Debug.WriteLine($"IF节点 {node.DisplayName}: 与 {previousNode.DisplayName} 中心对齐");
                            Debug.WriteLine($"  前节点中心X: {previousCenterX}, 当前节点宽度: {nodeActualWidth}, 计算X: {node.X}");
                        }
                        else if (node.IsParallelNode) // 添加并行节点处理
                        {
                            double nodeActualWidth = node.GetActualWidth();
                            node.X = previousCenterX - (nodeActualWidth / 2);
                            Debug.WriteLine($"并行节点 {node.DisplayName}: 与 {previousNode.DisplayName} 中心对齐");
                            Debug.WriteLine($"  前节点中心X: {previousCenterX}, 当前节点宽度: {nodeActualWidth}, 计算X: {node.X}");
                        }
                        else
                        {
                            node.X = previousCenterX - 115;
                            Debug.WriteLine($"普通节点 {node.DisplayName}: 与 {previousNode.DisplayName} 中心对齐, X={node.X}");
                        }
                    }

                    Debug.WriteLine($"节点 {i + 1} ({node.DisplayName}) 最终位置: ({node.X}, {node.Y})");
                }

                // 重建所有连接线
                RebuildAllConnections();
                UpdateCanvasSize();

                Debug.WriteLine("=== 节点重新排列完成 ===");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"重新排列节点失败: {ex.Message}");
                Log.Error(ex, "重新排列节点失败");
            }
        }

        public void UpdateStartNodePositionAndRearrange(double newX, double newY)
        {
            StartNodeX = newX;
            StartNodeY = newY;

            // 重新排列所有节点
            RearrangeNodes();
        }

        private void UpdateCanvasSize()
        {
            try
            {
                Debug.WriteLine("=== 开始更新主画布大小 ===");

                if (CommandNodes.Count == 0)
                {
                    CanvasWidth = Math.Max(1200, StartNodeX + 400);
                    CanvasHeight = Math.Max(800, StartNodeY + 200);
                    Debug.WriteLine($"无节点时画布大小: {CanvasWidth}x{CanvasHeight}");
                    return;
                }

                double maxX = StartNodeX + 231; // 启动节点宽度
                double maxY = StartNodeY + 64;  // 启动节点高度

                Debug.WriteLine($"初始边界: 启动节点 {StartNodeX + 231}x{StartNodeY + 64}");

                foreach (var node in CommandNodes)
                {
                    try
                    {
                        // 强制更新节点布局
                        if (node.NodeControl != null)
                        {
                            Application.Current?.Dispatcher.Invoke(() =>
                            {
                                node.NodeControl.UpdateLayout();
                            });
                        }

                        var nodeSize = node.GetActualSize();
                        double nodeRight = node.X + nodeSize.Width;
                        double nodeBottom = node.Y + nodeSize.Height;

                        maxX = Math.Max(maxX, nodeRight);
                        maxY = Math.Max(maxY, nodeBottom);

                        Debug.WriteLine($"节点 {node.DisplayName}: 位置({node.X}, {node.Y}), 尺寸({nodeSize.Width}x{nodeSize.Height}), 边界({nodeRight}, {nodeBottom})");

                        // 特别处理IF节点的扩展情况
                        if (node.IsIfNode && node.NodeControl is IfNodeControlView ifControl)
                        {
                            try
                            {
                                // 获取IF节点内部Canvas的实际尺寸
                                var branchCanvas = ifControl.BranchCanvas;
                                if (branchCanvas != null)
                                {
                                    double canvasWidth = branchCanvas.ActualWidth > 0 ? branchCanvas.ActualWidth : branchCanvas.Width;
                                    double ifNodeActualRight = node.X + canvasWidth;
                                    maxX = Math.Max(maxX, ifNodeActualRight);

                                    Debug.WriteLine($"IF节点 {node.DisplayName} 内部Canvas宽度: {canvasWidth}, 实际右边界: {ifNodeActualRight}");
                                }
                            }
                            catch (Exception ex)
                            {
                                Debug.WriteLine($"获取IF节点 {node.DisplayName} 内部Canvas尺寸失败: {ex.Message}");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"获取节点 {node.DisplayName} 尺寸失败: {ex.Message}");
                        // 使用默认尺寸
                        double defaultWidth = node.IsIfNode ? 800 : 231;  // IF节点默认给更大的宽度
                        double defaultHeight = node.IsIfNode ? 300 : 64;
                        maxX = Math.Max(maxX, node.X + defaultWidth);
                        maxY = Math.Max(maxY, node.Y + defaultHeight);
                    }
                }

                // 添加边距
                double newCanvasWidth = Math.Max(1200, maxX + 100);  // 右侧100px边距
                double newCanvasHeight = Math.Max(800, maxY + 200);  // 底部200px边距

                // 检查是否需要更新
                bool sizeChanged = Math.Abs(CanvasWidth - newCanvasWidth) > 1 || Math.Abs(CanvasHeight - newCanvasHeight) > 1;

                CanvasWidth = newCanvasWidth;
                CanvasHeight = newCanvasHeight;

                if (sizeChanged)
                {
                    Debug.WriteLine($"画布大小已更新: {CanvasWidth}x{CanvasHeight}");

                    // 强制更新UI
                    RaisePropertyChanged(nameof(CanvasWidth));
                    RaisePropertyChanged(nameof(CanvasHeight));
                }

                Debug.WriteLine("=== 主画布大小更新完成 ===");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"更新画布大小失败: {ex.Message}");
                Log.Error(ex, "更新画布大小失败");
            }
        }

        #endregion

        #region 命令处理方法

        private void RemoveCommandNode(CommandNode node)
        {
            try
            {
                if (node != null)
                {
                    Debug.WriteLine($"=== 删除节点: {node.DisplayName} ===");

                    // 如果是IF节点，取消事件订阅
                    if (node.NodeControl is IfNodeControlView ifControl)
                    {
                        // 注意：这里可能需要具体的取消订阅逻辑
                        // ifControl.ContentChanged -= ...;
                    }

                    CommandNodes.Remove(node);

                    // 重新排列剩余节点和连接线
                    RearrangeNodes();

                    Debug.WriteLine($"节点删除完成，剩余节点: {CommandNodes.Count}, 连接线: {Connections.Count}");
                    Log.Information($"命令节点 {node.DisplayName} 已从流程设计器移除");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"移除命令节点失败: {ex.Message}");
                Log.Error(ex, "移除命令节点失败");
            }
        }

        private void ValidateAllNodes()
        {
            try
            {
                var invalidNodes = new System.Collections.Generic.List<string>();

                foreach (var node in CommandNodes)
                {
                    if (node.LogicCommand != null)
                    {
                        var plugin = _pluginManager?.GetPlugin(node.LogicCommand.CommandType.ToString());
                        if (plugin != null)
                        {
                            var result = plugin.ValidateNodeConfiguration(node);
                            if (!result.IsValid)
                            {
                                invalidNodes.Add($"{node.DisplayName}: {result.ErrorMessage}");
                            }
                        }
                    }
                }

                if (invalidNodes.Any())
                {
                    var message = $"发现 {invalidNodes.Count} 个配置错误:\n\n" + string.Join("\n", invalidNodes);
                    MessageBox.Show(message, "验证结果", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                else
                {
                    MessageBox.Show("所有节点配置验证通过！", "验证结果", MessageBoxButton.OK, MessageBoxImage.Information);
                }

                Log.Information($"节点配置验证完成，发现 {invalidNodes.Count} 个错误");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "验证节点配置失败");
                MessageBox.Show($"验证过程中发生错误：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void RefreshPlugins()
        {
            try
            {
                _pluginManager?.LoadPlugins();
                LoadLogicCommandPlugins();
                Log.Information("插件刷新完成");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "刷新插件失败");
            }
        }
        /// <summary>
        /// 处理节点点击事件
        /// </summary>
        public void HandleNodeClick(CommandNode clickedNode)
        {
            try
            {
                if (clickedNode == null) return;

                Debug.WriteLine($"=== 节点被点击: {clickedNode.DisplayName} ===");

                // 取消所有主节点的选中状态
                foreach (var node in CommandNodes)
                {
                    node.IsSelected = false;
                    // 同时取消所有嵌套节点的选中状态
                    ClearNestedNodeSelection(node);
                }

                // 选中被点击的节点
                clickedNode.IsSelected = true;
                Debug.WriteLine($"节点 {clickedNode.DisplayName} 已被选中");

                // 触发属性变更通知
                RaisePropertyChanged(nameof(CommandNodes));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"处理节点点击失败: {ex.Message}");
                Log.Error(ex, "处理节点点击失败");
            }
        }

        /// <summary>
        /// 清除嵌套节点的选中状态
        /// </summary>
        private void ClearNestedNodeSelection(CommandNode parentNode)
        {
            try
            {
                if (parentNode?.HasChildren == true)
                {
                    foreach (var child in parentNode.Children)
                    {
                        child.IsSelected = false;
                        // 递归清除更深层的嵌套节点
                        ClearNestedNodeSelection(child);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"清除嵌套节点选中状态失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 处理嵌套节点点击（通过路由事件调用）
        /// </summary>
        public void HandleNestedNodeClick(CommandNode clickedNode)
        {
            HandleNodeClick(clickedNode);
        }
        #endregion

        #region 事件处理

        private void OnCommandDragComplete(CommandDragCompleteEventArgs args)
        {
            try
            {
                if (args.Command is DeviceCommand deviceCmd)
                {
                    AddCommandToCanvas(deviceCmd);
                }
                else if (args.Command is LogicCommand logicCmd)
                {
                    AddCommandToCanvas(logicCmd);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "处理命令拖拽完成事件失败");
            }
        }

        #endregion

        #region 公共方法

        public void ClearCanvas()
        {
            try
            {
                CommandNodes.Clear();
                Connections.Clear();
                CanvasDevices.Clear();
                UpdateCanvasSize();
                Log.Information("画布已清空");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "清空画布失败");
            }
        }

        public void SelectNode(CommandNode node)
        {
            try
            {
                // 清除所有选中状态
                foreach (var n in CommandNodes)
                {
                    n.IsSelected = false;
                }

                // 选中指定节点
                if (node != null)
                {
                    node.IsSelected = true;
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "选择节点失败");
            }
        }

        #endregion

    }

}
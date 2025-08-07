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
using MaxChemical.Modules.Designer.Services;
using MaxChemical.Modules.Designer.Events;
using MaxChemical.Logging;
using Prism.Commands;
using Prism.Events;
using Prism.Mvvm;
using MaxChemical.Modules.Designer.Service.Execution;

namespace MaxChemical.Modules.Designer.ViewModels
{
    public class FlowDesignerViewModel : BindableBase
    {
        private readonly IDeviceManager _deviceManager;
        private readonly IEventAggregator _eventAggregator;
        private readonly ILogicCommandPluginManager _pluginManager;
        private readonly ILogService _logger;
        private readonly NodeManagementService _nodeManagementService;
        private readonly DesignerDragDropService _dragDropService;
        private readonly NodeControlFactory _nodeControlFactory;

        // 画布属性
        private double _canvasWidth = 1200;
        private double _canvasHeight = 800;
        private double _startNodeX = 100;
        private double _startNodeY = 20;
        private bool _nodesVisible = true;
        private CommandNode _selectedNode;

        // 集合
        public ObservableCollection<CanvasDevice> CanvasDevices { get; private set; }
        public ObservableCollection<CommandNode> CommandNodes { get; private set; }
        public ObservableCollection<NodeConnection> Connections { get; private set; }
        public ObservableCollection<LogicCommandPluginWrapper> LogicCommandPlugins { get; private set; }

        // 命令
        public ICommand RemoveCommandNodeCommand { get; private set; }
        public ICommand ValidateAllNodesCommand { get; private set; }
        public ICommand RefreshPluginsCommand { get; private set; }
        public ICommand NodeClickCommand { get; private set; }

        // 事件
        public event EventHandler<CommandNode> NodeAdded;
        public event EventHandler<CanvasWidthRequestEventArgs> CanvasWidthRequested;

        public class CanvasWidthRequestEventArgs : EventArgs
        {
            public double ActualCanvasWidth { get; set; }
        }

        public FlowDesignerViewModel(
       IDeviceManager deviceManager,
       IEventAggregator eventAggregator,
       ILogicCommandPluginManager pluginManager,
       INodeExecutionStatusService executionStatusService = null, // 添加这个参数
       ILogService logService = null)
        {
            _deviceManager = deviceManager;
            _eventAggregator = eventAggregator;
            _pluginManager = pluginManager;
            _logger = logService?.ForContext<FlowDesignerViewModel>() ?? new LogService().ForContext<FlowDesignerViewModel>();

            // 初始化服务
            _nodeManagementService = new NodeManagementService(_logger);
            _dragDropService = new DesignerDragDropService(_logger);
            _nodeControlFactory = new NodeControlFactory(_logger);

            // 初始化集合和命令
            InitializeCollections();
            InitializeCommands();

            // 加载插件和订阅事件
            LoadLogicCommandPlugins();
            SubscribeToEvents();

            // 设置当前视图模型到状态服务
            executionStatusService?.SetCurrentFlowViewModel(this);

            _logger.LogInformation("FlowDesignerViewModel 初始化完成");
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

        public bool NodesVisible
        {
            get => _nodesVisible;
            set => SetProperty(ref _nodesVisible, value);
        }

        public CommandNode SelectedNode
        {
            get => _selectedNode;
            set => SetProperty(ref _selectedNode, value);
        }

        #endregion

        #region 初始化方法

        private void InitializeCollections()
        {
            CanvasDevices = new ObservableCollection<CanvasDevice>();
            CommandNodes = new ObservableCollection<CommandNode>();
            Connections = new ObservableCollection<NodeConnection>();
            LogicCommandPlugins = new ObservableCollection<LogicCommandPluginWrapper>();
        }

        private void InitializeCommands()
        {
            RemoveCommandNodeCommand = new DelegateCommand<CommandNode>(
     RemoveCommandNode,
     CanRemoveCommandNode);
            ValidateAllNodesCommand = new DelegateCommand(ValidateAllNodes);
            RefreshPluginsCommand = new DelegateCommand(RefreshPlugins);
            NodeClickCommand = new DelegateCommand<CommandNode>(HandleNodeClick);
        }

        private void LoadLogicCommandPlugins()
        {
            try
            {
                LogicCommandPlugins.Clear();
                _logger.LogDebug("开始加载逻辑命令插件");

                if (_pluginManager == null)
                {
                    _logger.LogWarning("插件管理器为null，无法加载逻辑命令插件");
                    return;
                }

                var plugins = _pluginManager.GetAllPlugins();
                _logger.LogDebug("从插件管理器获取到 {Count} 个插件", plugins?.Count() ?? 0);

                if (plugins == null || !plugins.Any())
                {
                    _logger.LogWarning("没有找到任何逻辑命令插件");
                    return;
                }

                foreach (var plugin in plugins)
                {
                    try
                    {
                        _logger.LogTrace("正在处理插件: {PluginName} (ID: {PluginId})", plugin.Name, plugin.Id);
                        var wrapper = new LogicCommandPluginWrapper(plugin, this);
                        LogicCommandPlugins.Add(wrapper);
                        _logger.LogTrace("成功添加插件: {PluginName}", plugin.Name);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "添加插件 {PluginName} 失败", plugin.Name);
                    }
                }

                _logger.LogInformation("已加载 {Count} 个逻辑命令插件到工具栏", LogicCommandPlugins.Count);
                RaisePropertyChanged(nameof(LogicCommandPlugins));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "加载逻辑命令插件到工具栏失败");
            }
        }

        private void SubscribeToEvents()
        {
            if (_eventAggregator != null)
            {
                _eventAggregator.GetEvent<CommandDragFCompleteEvent>().Subscribe(OnCommandDragComplete);
                _eventAggregator?.GetEvent<GetCurrentFlowDesignerRequestEvent>()?.Subscribe(OnGetCurrentFlowDesignerRequest);
            }
        }
        private void OnGetCurrentFlowDesignerRequest(Action<FlowDesignerViewModel> callback)
        {
            try
            {
                callback?.Invoke(this);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "响应获取流程设计器请求失败");
                callback?.Invoke(null);
            }
        }
        #endregion

        #region 添加节点方法

        public void AddCommandToCanvas(DeviceCommand command)
        {
            try
            {
                _logger.LogDebug("添加设备命令到画布: {CommandName}", command.Name);

                System.Windows.Point newPosition = CalculateNextNodePosition();

                var commandNode = new CommandNode(command)
                {
                    X = newPosition.X,
                    Y = newPosition.Y,
                    Order = CommandNodes.Count + 1
                };

                CommandNodes.Add(commandNode);
                CreateConnectionForNewNode(commandNode);
                UpdateCanvasSize();

                Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
                {
                    NodeAdded?.Invoke(this, commandNode);
                }));

                _logger.LogInformation("设备命令节点 {CommandName} 已添加到流程设计器", command.Name);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "添加设备命令节点失败: {CommandName}", command?.Name);
            }
        }

        public void AddCommandToCanvas(LogicCommand logicCommand)
        {
            try
            {
                _logger.LogDebug("添加逻辑命令到画布: {CommandName}", logicCommand.Name);

                if (CommandNodes.Count == 0)
                {
                    EnsureStartNodeCentered(logicCommand);
                }

                var newPosition = CalculateNextNodePositionStable(logicCommand);

                var commandNode = new CommandNode(logicCommand)
                {
                    X = newPosition.X,
                    Y = newPosition.Y,
                    Order = CommandNodes.Count + 1,
                    IsVisible = false
                };

                var plugin = _pluginManager?.GetPlugin(logicCommand.CommandType.ToString());
                if (plugin != null)
                {
                    commandNode.NodeControl = plugin.CreateNodeControl(commandNode);
                    var estimatedSize = NodeSizeEstimator.EstimateLoopNodeSize(commandNode);
                    commandNode.NodeControl.Width = estimatedSize.Width;
                    commandNode.NodeControl.MinWidth = estimatedSize.Width;
                    SubscribeToNodeControlEvents(commandNode, plugin);
                }

                CommandNodes.Add(commandNode);
                CreateConnectionForNewNode(commandNode);

                Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
                {
                    commandNode.IsVisible = true;
                    NodeAdded?.Invoke(this, commandNode);
                    _logger.LogDebug("节点 {CommandName} 显示完成，最终位置: ({X}, {Y})",
                        logicCommand.Name, commandNode.X, commandNode.Y);
                }), System.Windows.Threading.DispatcherPriority.Loaded);

                UpdateCanvasSizeStable();
                _logger.LogInformation("逻辑命令节点 {CommandName} 已添加到流程设计器", logicCommand.Name);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "添加逻辑命令节点失败: {CommandName}", logicCommand?.Name);
            }
        }
        #region 位置插入方法 - 新增这个区域

        public void AddCommandToCanvasAtPosition(DeviceCommand command, int insertIndex)
        {
            try
            {
                _logger.LogDebug("在位置 {Position} 添加设备命令: {CommandName}", insertIndex, command.Name);

                var commandNode = new CommandNode(command)
                {
                    Order = insertIndex + 1
                };

                // 插入到指定位置
                if (insertIndex >= 0 && insertIndex <= CommandNodes.Count)
                {
                    CommandNodes.Insert(insertIndex, commandNode);
                }
                else
                {
                    CommandNodes.Add(commandNode);
                }

                // 重新排列所有节点
                RearrangeNodesFromInsert(insertIndex);
                RebuildAllConnections();
                UpdateCanvasSize();

                Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
                {
                    NodeAdded?.Invoke(this, commandNode);
                }));

                _logger.LogInformation("设备命令节点 {CommandName} 已添加到位置 {Position}", command.Name, insertIndex);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "在位置添加设备命令节点失败: {CommandName}, Position: {Position}",
                    command?.Name, insertIndex);
            }
        }

        public void AddCommandToCanvasAtPosition(LogicCommand logicCommand, int insertIndex)
        {
            try
            {
                _logger.LogDebug("在位置 {Position} 添加逻辑命令: {CommandName}", insertIndex, logicCommand.Name);

                if (CommandNodes.Count == 0 && insertIndex == 0)
                {
                    EnsureStartNodeCentered(logicCommand);
                }

                var commandNode = new CommandNode(logicCommand)
                {
                    Order = insertIndex + 1,
                    IsVisible = false
                };

                // 创建节点控件
                var plugin = _pluginManager?.GetPlugin(logicCommand.CommandType.ToString());
                if (plugin != null)
                {
                    commandNode.NodeControl = plugin.CreateNodeControl(commandNode);
                    var estimatedSize = NodeSizeEstimator.EstimateLoopNodeSize(commandNode);
                    commandNode.NodeControl.Width = estimatedSize.Width;
                    commandNode.NodeControl.MinWidth = estimatedSize.Width;
                    SubscribeToNodeControlEvents(commandNode, plugin);
                }

                // 插入到指定位置
                if (insertIndex >= 0 && insertIndex <= CommandNodes.Count)
                {
                    CommandNodes.Insert(insertIndex, commandNode);
                }
                else
                {
                    CommandNodes.Add(commandNode);
                }

                // 重新排列所有节点
                RearrangeNodesFromInsert(insertIndex);
                RebuildAllConnections();

                Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
                {
                    commandNode.IsVisible = true;
                    NodeAdded?.Invoke(this, commandNode);
                    _logger.LogDebug("节点 {CommandName} 在位置 {Position} 显示完成",
                        logicCommand.Name, insertIndex);
                }), System.Windows.Threading.DispatcherPriority.Loaded);

                UpdateCanvasSizeStable();
                _logger.LogInformation("逻辑命令节点 {CommandName} 已添加到位置 {Position}", logicCommand.Name, insertIndex);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "在位置添加逻辑命令节点失败: {CommandName}, Position: {Position}",
                    logicCommand?.Name, insertIndex);
            }
        }

        private void RearrangeNodesFromInsert(int insertIndex)
        {
            try
            {
                _logger.LogDebug("从插入位置 {Position} 重新排列节点", insertIndex);

                // 更新所有节点的Order
                for (int i = 0; i < CommandNodes.Count; i++)
                {
                    CommandNodes[i].Order = i + 1;
                }

                // 计算所有节点的新位置
                const double nodeSpacing = 80;
                const double standardNodeHeight = 64;

                for (int i = 0; i < CommandNodes.Count; i++)
                {
                    var node = CommandNodes[i];

                    if (i == 0)
                    {
                        // 第一个节点的位置计算
                        var newPosition = CalculateFirstNodePosition(node);
                        node.X = newPosition.X;
                        node.Y = newPosition.Y;
                    }
                    else
                    {
                        // 后续节点的位置计算
                        var previousNode = CommandNodes[i - 1];
                        double previousHeight = GetEstimatedNodeHeight(previousNode);
                        double newY = previousNode.Y + previousHeight + nodeSpacing;

                        double previousCenterX = GetEstimatedNodeCenterX(previousNode);
                        double newX;

                        if (node.IsComplexNode)
                        {
                            double currentNodeWidth = GetEstimatedNodeWidth(node);
                            newX = previousCenterX - (currentNodeWidth / 2);
                        }
                        else
                        {
                            newX = previousCenterX - (NodeSizeEstimator.DEVICE_NODE_WIDTH / 2);
                        }

                        node.X = newX;
                        node.Y = newY;
                    }

                    _logger.LogTrace("重排节点 {Index}: {NodeName} -> ({X}, {Y})",
                        i, node.DisplayName, node.X, node.Y);
                }

                _logger.LogDebug("节点重新排列完成");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "重新排列节点失败");
            }
        }

        private Point CalculateFirstNodePosition(CommandNode node)
        {
            const double standardNodeWidth = NodeSizeEstimator.DEVICE_NODE_WIDTH;
            const double standardNodeHeight = NodeSizeEstimator.DEVICE_NODE_HEIGHT;
            const double nodeSpacing = 80;

            if (IsComplexLogicCommand(node.LogicCommand))
            {
                double startNodeCenterX = StartNodeX + (standardNodeWidth / 2);
                double estimatedWidth = NodeSizeEstimator.GetComplexNodeWidth(node.LogicCommand);
                double nodeX = startNodeCenterX - (estimatedWidth / 2);
                return new Point(nodeX, StartNodeY + standardNodeHeight + nodeSpacing);
            }
            else
            {
                return new Point(StartNodeX, StartNodeY + standardNodeHeight + nodeSpacing);
            }
        }

        #endregion
        private void SubscribeToNodeControlEvents(CommandNode commandNode, ILogicCommandPlugin plugin)
        {
            try
            {
                if (commandNode.NodeControl == null) return;

                switch (commandNode.NodeControl)
                {
                    case Views.IfNodeControlView ifControl:
                        SubscribeToIfNodeEvents(ifControl, commandNode);
                        break;
                    case Views.ParallelNodeControlView parallelControl:
                        SubscribeToParallelNodeEvents(parallelControl, commandNode);
                        break;
                    case Views.LoopNodeControlView loopControl:
                        SubscribeToLoopNodeEvents(loopControl, commandNode);
                        break;
                    case Views.WaitNodeControlView waitControl:
                        SubscribeToWaitNodeEvents(waitControl, commandNode);
                        break;
                    case Views.SetVariableNodeControlView setVarControl:
                        SubscribeToSetVariableNodeEvents(setVarControl, commandNode);
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "订阅节点控件事件失败: {NodeName}", commandNode.DisplayName);
            }
        }

        private void SubscribeToIfNodeEvents(Views.IfNodeControlView ifControl, CommandNode commandNode)
        {
            ifControl.ContentChanged += (s, e) =>
            {
                _logger.LogDebug("IF节点内容发生变化: {NodeName}", commandNode.DisplayName);
                Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
                {
                    RefreshNodeLayout(commandNode);
                }), System.Windows.Threading.DispatcherPriority.Background);
            };

            ifControl.NestedCommandAdded += (s, e) =>
            {
                _logger.LogDebug("嵌套命令已添加到IF节点: {NestedCommand} -> {Branch}分支",
                    e.NestedCommand.DisplayName, e.IsTrueBranch ? "TRUE" : "FALSE");
               
                HandleNestedCommandAdded(e);
            };

            ifControl.NestedCommandRemoved += (s, e) =>
            {
                _logger.LogDebug("嵌套命令已从IF节点移除: {NestedCommand} -> {Branch}分支",
                    e.NestedCommand.DisplayName, e.IsTrueBranch ? "TRUE" : "FALSE");
                HandleNestedCommandRemoved(e);
            };

            ifControl.LogicCommandRequested += (s, e) =>
            {
                var createdCommand = CreateLogicCommandFromPluginId(e.PluginId);
                e.ResultCommand = createdCommand;
                _logger.LogDebug("为嵌套拖拽创建LogicCommand: {CommandName}", createdCommand?.Name ?? "失败");
            };

            ifControl.NodeDeleteRequested += (s, nodeToDelete) =>
            {
                _logger.LogDebug("收到IF节点删除请求: {NodeName}", nodeToDelete.DisplayName);
                _nodeManagementService.HandleNodeDeleteRequest(nodeToDelete, this);
            };
        }

        private void SubscribeToParallelNodeEvents(Views.ParallelNodeControlView parallelControl, CommandNode commandNode)
        {
            parallelControl.ContentChanged += (s, e) =>
            {
                _logger.LogDebug("并行节点内容发生变化: {NodeName}", commandNode.DisplayName);
                Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
                {
                    RefreshNodeLayout(commandNode);
                }), System.Windows.Threading.DispatcherPriority.Background);
            };

            parallelControl.NestedCommandAdded += (s, e) =>
            {
                _logger.LogDebug("嵌套命令已添加到并行节点: {NestedCommand} -> 分支{BranchIndex}",
                    e.NestedCommand.DisplayName, e.BranchIndex);
                HandleNestedCommandAdded(e);
            };

            parallelControl.NestedCommandRemoved += (s, e) =>
            {
                _logger.LogDebug("嵌套命令已从并行节点移除: {NestedCommand} -> 分支{BranchIndex}",
                    e.NestedCommand.DisplayName, e.BranchIndex);
                HandleNestedCommandRemoved(e);
            };

            parallelControl.LogicCommandRequested += (s, e) =>
            {
                var createdCommand = CreateLogicCommandFromPluginId(e.PluginId);
                e.ResultCommand = createdCommand;
                _logger.LogDebug("为并行节点嵌套拖拽创建LogicCommand: {CommandName}", createdCommand?.Name ?? "失败");
            };

            parallelControl.NodeDeleteRequested += (s, nodeToDelete) =>
            {
                _logger.LogDebug("收到并行节点删除请求: {NodeName}", nodeToDelete.DisplayName);
                _nodeManagementService.HandleNodeDeleteRequest(nodeToDelete, this);
            };
        }

        private void SubscribeToLoopNodeEvents(Views.LoopNodeControlView loopControl, CommandNode commandNode)
        {
            loopControl.ContentChanged += (s, e) =>
            {
                _logger.LogDebug("循环节点内容发生变化: {NodeName}", commandNode.DisplayName);
                Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
                {
                    RefreshNodeLayout(commandNode);
                }), System.Windows.Threading.DispatcherPriority.Background);
            };

            loopControl.NestedCommandAdded += (s, e) =>
            {
                _logger.LogDebug("嵌套命令已添加到循环节点: {NestedCommand}", e.NestedCommand.DisplayName);
                HandleNestedCommandAdded(e);
            };

            loopControl.NestedCommandRemoved += (s, e) =>
            {
                _logger.LogDebug("嵌套命令已从循环节点移除: {NestedCommand}", e.NestedCommand.DisplayName);
                HandleNestedCommandRemoved(e);
            };

            loopControl.LogicCommandRequested += (s, e) =>
            {
                var createdCommand = CreateLogicCommandFromPluginId(e.PluginId);
                e.ResultCommand = createdCommand;
                _logger.LogDebug("为循环节点嵌套拖拽创建LogicCommand: {CommandName}", createdCommand?.Name ?? "失败");
            };

            loopControl.NodeDeleteRequested += (s, nodeToDelete) =>
            {
                _logger.LogDebug("收到循环节点删除请求: {NodeName}", nodeToDelete.DisplayName);
                _nodeManagementService.HandleNodeDeleteRequest(nodeToDelete, this);
            };
        }

        private void SubscribeToWaitNodeEvents(Views.WaitNodeControlView waitControl, CommandNode commandNode)
        {
            waitControl.NodeDeleteRequested += (s, nodeToDelete) =>
            {
                _logger.LogDebug("收到等待节点删除请求: {NodeName}", nodeToDelete.DisplayName);
                _nodeManagementService.HandleNodeDeleteRequest(nodeToDelete, this);
            };

            waitControl.AddHandler(Views.WaitNodeControlView.NodeClickEvent, new RoutedEventHandler((s, e) =>
            {
                if (e.OriginalSource is CommandNode clickedNode)
                {
                    _logger.LogDebug("等待节点被点击: {NodeName}", clickedNode.DisplayName);
                    HandleNodeClick(clickedNode);
                }
            }));
        }

        private void SubscribeToSetVariableNodeEvents(Views.SetVariableNodeControlView setVarControl, CommandNode commandNode)
        {
            setVarControl.NodeDeleteRequested += (s, nodeToDelete) =>
            {
                _logger.LogDebug("收到设置变量节点删除请求: {NodeName}", nodeToDelete.DisplayName);
                _nodeManagementService.HandleNodeDeleteRequest(nodeToDelete, this);
            };

            setVarControl.AddHandler(Views.SetVariableNodeControlView.NodeClickEvent, new RoutedEventHandler((s, e) =>
            {
                if (e.OriginalSource is CommandNode clickedNode)
                {
                    _logger.LogDebug("设置变量节点被点击: {NodeName}", clickedNode.DisplayName);
                    HandleNodeClick(clickedNode);
                }
            }));
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

                _logger.LogInformation("设备 {DeviceName} 已添加到画布", device.Name);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "添加设备到画布失败: {DeviceName}", device?.Name);
            }
        }

        #endregion

        #region 嵌套命令处理

        private void HandleNestedCommandAdded(NestedCommandEventArgs e)
        {
            try
            {
                _logger.LogDebug("处理嵌套命令添加: {NestedCommand}", e.NestedCommand.DisplayName);

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
                _logger.LogError(ex, "处理嵌套命令添加失败: {NestedCommand}", e.NestedCommand?.DisplayName);
            }
        }

        private void HandleNestedCommandRemoved(NestedCommandEventArgs e)
        {
            try
            {
                _logger.LogDebug("处理嵌套命令移除: {NestedCommand}", e.NestedCommand.DisplayName);

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
                _logger.LogError(ex, "处理嵌套命令移除失败: {NestedCommand}", e.NestedCommand?.DisplayName);
            }
        }

        public LogicCommand CreateLogicCommandFromPluginId(string pluginId)
        {
            try
            {
                if (string.IsNullOrEmpty(pluginId))
                {
                    _logger.LogWarning("插件ID为空，无法创建LogicCommand");
                    return null;
                }

                var plugin = _pluginManager?.GetPlugin(pluginId);
                if (plugin == null)
                {
                    _logger.LogWarning("未找到插件: {PluginId}", pluginId);
                    return null;
                }

                var logicCommand = new LogicCommand
                {
                    CommandType = Enum.TryParse<LogicCommandType>(plugin.Name, out var commandType)
                        ? commandType
                        : LogicCommandType.Unknown,
                    Name = plugin.Name,
                    HelpText = plugin.Description,
                    ImageLocation = plugin.IconPath
                };

                if (logicCommand.Properties == null)
                {
                    logicCommand.Properties = new System.Collections.Generic.Dictionary<string, object>();
                }

                SetDefaultPropertiesForCommand(logicCommand, plugin);

                _logger.LogDebug("成功创建LogicCommand: {CommandName}", logicCommand.Name);
                return logicCommand;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "根据插件ID创建LogicCommand失败: {PluginId}", pluginId);
                return null;
            }
        }

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

                    case LogicCommandType.Parallel:
                        command.Properties["MaxParallelCount"] = "4";
                        break;

                    default:
                        break;
                }

                _logger.LogTrace("为命令 {CommandName} 设置了默认属性", command.Name);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "设置命令默认属性失败: {CommandName}", command?.Name);
            }
        }

        #endregion

        #region 布局方法

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

        private void EnsureStartNodeCentered(LogicCommand upcomingCommand = null)
        {
            try
            {
                _logger.LogDebug("确保启动节点居中");
                _logger.LogDebug("当前启动节点位置: ({X}, {Y})", StartNodeX, StartNodeY);
                _logger.LogDebug("当前画布宽度: {Width}", CanvasWidth);
                _logger.LogDebug("即将添加的命令: {CommandName}", upcomingCommand?.Name ?? "null");

                double actualCanvasWidth = 0;
                Application.Current?.Dispatcher.Invoke(() =>
                {
                    var args = new CanvasWidthRequestEventArgs();
                    CanvasWidthRequested?.Invoke(this, args);
                    actualCanvasWidth = args.ActualCanvasWidth;
                    _logger.LogDebug("通过事件获取的画布宽度: {Width}", actualCanvasWidth);
                });

                if (actualCanvasWidth <= 0)
                {
                    actualCanvasWidth = Math.Max(CanvasWidth, 1200);
                    _logger.LogDebug("使用默认画布宽度: {Width}", actualCanvasWidth);
                }

                double predictedCanvasWidth = actualCanvasWidth;
                if (CommandNodes.Count == 0 && upcomingCommand != null)
                {
                    double nodeWidth = NodeSizeEstimator.GetComplexNodeWidth(upcomingCommand);
                    double nodeX = actualCanvasWidth / 2 - nodeWidth / 2;
                    double nodeRightBoundary = nodeX + nodeWidth;

                    if (nodeRightBoundary > actualCanvasWidth)
                    {
                        predictedCanvasWidth = nodeRightBoundary + 100;
                        _logger.LogDebug("节点会超出画布，预测扩展后宽度: {Width}", predictedCanvasWidth);
                    }
                    else
                    {
                        predictedCanvasWidth = Math.Max(actualCanvasWidth, 1260);
                        _logger.LogDebug("使用观察到的最小扩展宽度: {Width}", predictedCanvasWidth);
                    }
                }

                const double startNodeWidth = 231;
                double correctCenterX = (predictedCanvasWidth - startNodeWidth) / 2;
                double correctStartY = 10;

                _logger.LogDebug("基于预测宽度 {Width} 计算的启动节点位置: ({X}, {Y})",
                    predictedCanvasWidth, correctCenterX, correctStartY);

                if (Math.Abs(StartNodeX - correctCenterX) > 0.1 || Math.Abs(StartNodeY - correctStartY) > 0.1)
                {
                    _logger.LogDebug("强制更新启动节点位置: ({OldX}, {OldY}) -> ({NewX}, {NewY})",
                        StartNodeX, StartNodeY, correctCenterX, correctStartY);
                    StartNodeX = correctCenterX;
                    StartNodeY = correctStartY;
                }
                else
                {
                    _logger.LogDebug("启动节点位置已经正确，无需更新");
                }

                _logger.LogDebug("确保启动节点居中完成: ({X}, {Y})", StartNodeX, StartNodeY);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "确保启动节点居中失败");
            }
        }

        private System.Windows.Point CalculateNextNodePosition(LogicCommand logicCommand = null)
        {
            const double nodeSpacing = 80;
            const double standardNodeWidth = 231;
            const double standardNodeHeight = 64;

            if (CommandNodes.Count == 0)
            {
                if (IsComplexLogicCommand(logicCommand))
                {
                    double startNodeCenterX = StartNodeX + (standardNodeWidth / 2);
                    double estimatedWidth = NodeSizeEstimator.GetComplexNodeWidth(logicCommand);
                    double nodeX = startNodeCenterX - (estimatedWidth / 2);
                    return new System.Windows.Point(nodeX, StartNodeY + standardNodeHeight + nodeSpacing);
                }
                else
                {
                    return new System.Windows.Point(StartNodeX, StartNodeY + standardNodeHeight + nodeSpacing);
                }
            }

            var lastNode = CommandNodes.Last();
            double nextY;

            try
            {
                double lastNodeActualHeight = lastNode.GetActualHeight();
                nextY = lastNode.Y + lastNodeActualHeight + nodeSpacing;
                _logger.LogTrace("上一个节点实际高度: {Height}, 新节点Y位置: {Y}", lastNodeActualHeight, nextY);
            }
            catch (Exception ex)
            {
                _logger.LogCritical(ex, "获取上一个节点高度失败，使用默认值");
                double defaultHeight = GetDefaultNodeHeight(lastNode);
                nextY = lastNode.Y + defaultHeight + nodeSpacing;
            }

            if (IsComplexLogicCommand(logicCommand))
            {
                double lastNodeCenterX = GetNodeCenterX(lastNode);
                double estimatedWidth = NodeSizeEstimator.GetComplexNodeWidth(logicCommand);
                double nodeX = lastNodeCenterX - (estimatedWidth / 2);
                return new System.Windows.Point(nodeX, nextY);
            }
            else
            {
                double lastNodeCenterX = GetNodeCenterX(lastNode);
                double normalNodeX = lastNodeCenterX - (standardNodeWidth / 2);
                return new System.Windows.Point(normalNodeX, nextY);
            }
        }

        private System.Windows.Point CalculateNextNodePositionStable(LogicCommand logicCommand = null)
        {
            const double nodeSpacing = 80;
            const double standardNodeWidth = NodeSizeEstimator.DEVICE_NODE_WIDTH;
            const double standardNodeHeight = NodeSizeEstimator.DEVICE_NODE_HEIGHT;

            _logger.LogTrace("位置计算详细信息");
            _logger.LogTrace("计算前启动节点位置: StartNodeX={X}, StartNodeY={Y}", StartNodeX, StartNodeY);
            _logger.LogTrace("当前节点数量: {Count}", CommandNodes.Count);

            if (CommandNodes.Count == 0)
            {
                _logger.LogTrace("处理第一个节点");

                if (IsComplexLogicCommand(logicCommand))
                {
                    double startNodeCenterX = StartNodeX + (standardNodeWidth / 2);
                    double estimatedWidth = NodeSizeEstimator.GetComplexNodeWidth(logicCommand);
                    double nodeX = startNodeCenterX - (estimatedWidth / 2);

                    _logger.LogTrace("第一个复杂节点位置计算:");
                    _logger.LogTrace("  启动节点X: {X}", StartNodeX);
                    _logger.LogTrace("  启动节点宽度: {Width}", standardNodeWidth);
                    _logger.LogTrace("  启动节点中心X: {CenterX}", startNodeCenterX);
                    _logger.LogTrace("  当前节点预估宽度: {Width}", estimatedWidth);
                    _logger.LogTrace("  计算的X位置: {X}", nodeX);

                    if (nodeX < 0)
                    {
                        _logger.LogWarning("计算出的X位置为负数: {X}", nodeX);
                    }

                    var result = new System.Windows.Point(nodeX, StartNodeY + standardNodeHeight + nodeSpacing);
                    _logger.LogTrace("第一个复杂节点最终计算位置: ({X}, {Y})", result.X, result.Y);
                    return result;
                }
                else
                {
                    _logger.LogTrace("第一个普通节点位置: ({X}, {Y})", StartNodeX, StartNodeY + standardNodeHeight + nodeSpacing);
                    return new System.Windows.Point(StartNodeX, StartNodeY + standardNodeHeight + nodeSpacing);
                }
            }

            var lastNode = CommandNodes.Last();
            double lastNodeHeight = GetEstimatedNodeHeight(lastNode);
            double nextY = lastNode.Y + lastNodeHeight + nodeSpacing;

            if (IsComplexLogicCommand(logicCommand))
            {
                double lastNodeCenterX = GetEstimatedNodeCenterX(lastNode);
                double estimatedWidth = NodeSizeEstimator.GetComplexNodeWidth(logicCommand);
                double nodeX = lastNodeCenterX - (estimatedWidth / 2);

                _logger.LogTrace("后续复杂节点位置计算:");
                _logger.LogTrace("  上一节点中心X: {CenterX}", lastNodeCenterX);
                _logger.LogTrace("  当前节点预估宽度: {Width}", estimatedWidth);
                _logger.LogTrace("  计算的X位置: {X}", nodeX);

                return new System.Windows.Point(nodeX, nextY);
            }
            else
            {
                double lastNodeCenterX = GetEstimatedNodeCenterX(lastNode);
                double normalNodeX = lastNodeCenterX - (standardNodeWidth / 2);
                return new System.Windows.Point(normalNodeX, nextY);
            }
        }

        private double GetDefaultNodeHeight(CommandNode node)
        {
            return NodeSizeEstimator.GetDefaultNodeHeight(node);
        }

        private double GetNodeCenterX(CommandNode node)
        {
            try
            {
                return node.GetMainCardCenterX();
            }
            catch (Exception ex)
            {
                _logger.LogCritical(ex, "获取节点中心X坐标失败，使用默认值");
                double defaultWidth = node.IsIfNode ? 600 : 231;
                return node.X + defaultWidth / 2;
            }
        }

        private double GetEstimatedNodeHeight(CommandNode node)
        {
            return NodeSizeEstimator.GetNodeHeight(node);
        }

        private double GetEstimatedNodeCenterX(CommandNode node)
        {
            try
            {
                _logger.LogTrace("GetEstimatedNodeCenterX - 开始计算节点 {NodeName} 的中心", node.DisplayName);
                _logger.LogTrace("  节点类型: {Type}", node.CommandType);
                _logger.LogTrace("  节点X位置: {X}", node.X);

                if (node.NodeControl != null)
                {
                    try
                    {
                        var actualCenterX = node.GetMainCardCenterX();
                        if (actualCenterX > 0)
                        {
                            _logger.LogTrace("  使用实际中心X: {CenterX}", actualCenterX);
                            return actualCenterX;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogTrace("  获取实际中心失败: {Message}", ex.Message);
                    }
                }

                var estimatedWidth = NodeSizeEstimator.GetNodeWidth(node);
                var centerX = node.X + estimatedWidth / 2;

                _logger.LogTrace("  使用预估计算 - 宽度: {Width}, 中心X: {CenterX}", estimatedWidth, centerX);
                return centerX;
            }
            catch (Exception ex)
            {
                _logger.LogCritical(ex, "获取节点中心X坐标失败");
                double defaultWidth = node.IsIfNode ? NodeSizeEstimator.IF_NODE_MIN_WIDTH :
                                     node.IsParallelNode ? NodeSizeEstimator.PARALLEL_NODE_MIN_WIDTH :
                                     NodeSizeEstimator.DEVICE_NODE_WIDTH;
                var fallbackCenterX = node.X + defaultWidth / 2;
                _logger.LogTrace("  使用默认计算 - 默认宽度: {Width}, 中心X: {CenterX}", defaultWidth, fallbackCenterX);
                return fallbackCenterX;
            }
        }

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

                double maxX = StartNodeX + 231;
                double maxY = StartNodeY + 64;

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

                _logger.LogTrace("稳定画布大小更新: {Width}x{Height}", CanvasWidth, CanvasHeight);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "更新稳定画布大小失败");
            }
        }

        public void RefreshNodeLayout(CommandNode changedNode)
        {
            try
            {
                if (changedNode == null) return;

                _logger.LogDebug("刷新节点布局: {NodeName}", changedNode.DisplayName);

                Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
                {
                    int changedIndex = CommandNodes.IndexOf(changedNode);
                    if (changedIndex == -1)
                    {
                        _logger.LogWarning("未找到变化的节点");
                        return;
                    }

                    if (changedNode.NodeControl != null)
                    {
                        changedNode.NodeControl.UpdateLayout();
                    }

                    RearrangeNodesFromIndex(changedIndex + 1);
                    RebuildAllConnections();
                    UpdateCanvasSize();
                  
                    _logger.LogDebug("节点布局刷新完成");

                }), System.Windows.Threading.DispatcherPriority.Background);


            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "刷新节点布局失败: {NodeName}", changedNode?.DisplayName);
            }
        }

        private void RearrangeNodesFromIndex(int startIndex)
        {
            try
            {
                if (startIndex >= CommandNodes.Count) return;

                _logger.LogTrace("从索引 {Index} 开始重新排列节点", startIndex);
                const double nodeSpacing = 80;

                for (int i = startIndex; i < CommandNodes.Count; i++)
                {
                    var currentNode = CommandNodes[i];
                    var previousNode = CommandNodes[i - 1];

                    double previousHeight = GetEstimatedNodeHeight(previousNode);
                    double newY = previousNode.Y + previousHeight + nodeSpacing;

                    double previousCenterX = GetEstimatedNodeCenterX(previousNode);
                    double newX;

                    if (currentNode.IsComplexNode)
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

                    _logger.LogTrace("重排节点 {Index}: {NodeName} -> ({X}, {Y})", i, currentNode.DisplayName, newX, newY);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "重新排列节点失败");
            }
        }

        private double GetEstimatedNodeWidth(CommandNode node)
        {
            return NodeSizeEstimator.GetNodeWidth(node);
        }

        private void CreateConnectionForNewNode(CommandNode toNode)
        {
            try
            {
                NodeConnection connection;

                if (CommandNodes.Count == 1)
                {
                    double startNodeCenterX = StartNodeX + 115;
                    var toConnectionPoint = toNode.GetTopConnectionPoint();

                    connection = new NodeConnection
                    {
                        FromNodeId = "StartNode",
                        ToNodeId = toNode.NodeId,
                        X = startNodeCenterX,
                        Y = StartNodeY + 64 + 2,
                        LineLength = (int)(toConnectionPoint.Y - (StartNodeY + 64 + 2) - 8),
                        ArrowTop = (int)(toConnectionPoint.Y - (StartNodeY + 64 + 2) - 8),
                        TotalHeight = (int)(toConnectionPoint.Y - (StartNodeY + 64 + 2))
                    };
                }
                else
                {
                    var previousNode = CommandNodes[CommandNodes.Count - 2];

                    var fromConnectionPoint = previousNode.GetBottomConnectionPoint();
                    var toConnectionPoint = toNode.GetTopConnectionPoint();

                    connection = new NodeConnection
                    {
                        FromNodeId = previousNode.NodeId,
                        ToNodeId = toNode.NodeId,
                        X = fromConnectionPoint.X,
                        Y = fromConnectionPoint.Y + 2,
                        LineLength = (int)(toConnectionPoint.Y - fromConnectionPoint.Y - 10),
                        ArrowTop = (int)(toConnectionPoint.Y - fromConnectionPoint.Y - 10),
                        TotalHeight = (int)(toConnectionPoint.Y - fromConnectionPoint.Y - 2)
                    };
                }

                Connections.Add(connection);
                _logger.LogTrace("创建连接线: 从 {FromId} 到 {ToId}, 位置: X={X}, Y={Y}",
                    connection.FromNodeId, connection.ToNodeId, connection.X, connection.Y);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "创建连接线失败: {NodeName}", toNode?.DisplayName);
            }
        }

        private void RebuildAllConnections()
        {
            try
            {
                _logger.LogDebug("开始重建所有连接线");

                Connections.Clear();

                for (int i = 0; i < CommandNodes.Count; i++)
                {
                    var currentNode = CommandNodes[i];
                    NodeConnection connection;

                    if (i == 0)
                    {
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
                        var previousNode = CommandNodes[i - 1];
                        double fromX = GetNodeCenterX(previousNode);

                        double fromY;
                        try
                        {
                            double previousHeight = previousNode.GetActualHeight();
                            fromY = previousNode.Y + previousHeight + 2;
                        }
                        catch
                        {
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
                    _logger.LogTrace("重建连接线 {Index}: 从 {FromId} 到 {ToId}, 位置: ({X}, {Y})",
                        i + 1, connection.FromNodeId, connection.ToNodeId, connection.X, connection.Y);
                }

                _logger.LogDebug("连接线重建完成，总数: {Count}", Connections.Count);
                RaisePropertyChanged(nameof(Connections));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "重建连接线失败");
            }
        }

        private void RearrangeNodes()
        {
            try
            {
                _logger.LogDebug("开始重新排列节点");
                const double nodeSpacing = 80;
                const double standardNodeHeight = 64;

                for (int i = 0; i < CommandNodes.Count; i++)
                {
                    var node = CommandNodes[i];
                    node.Order = i + 1;

                    if (i == 0)
                    {
                        if (node.IsIfNode)
                        {
                            double startNodeCenterX = StartNodeX + 115;
                            double nodeActualWidth = node.GetActualWidth();
                            node.X = startNodeCenterX - (nodeActualWidth / 2);
                            _logger.LogTrace("第一个IF节点 {NodeName}: 中心对齐后X={X}", node.DisplayName, node.X);
                        }
                        else if (node.IsParallelNode)
                        {
                            double startNodeCenterX = StartNodeX + 115;
                            double nodeActualWidth = node.GetActualWidth();
                            node.X = startNodeCenterX - (nodeActualWidth / 2);
                            _logger.LogTrace("第一个并行节点 {NodeName}: 中心对齐后X={X}", node.DisplayName, node.X);
                        }
                        else
                        {
                            node.X = StartNodeX;
                            _logger.LogTrace("第一个普通节点 {NodeName}: X={X}", node.DisplayName, node.X);
                        }
                        node.Y = StartNodeY + standardNodeHeight + nodeSpacing;
                    }
                    else
                    {
                        var previousNode = CommandNodes[i - 1];

                        double previousHeight = previousNode.GetActualHeight();
                        node.Y = previousNode.Y + previousHeight + nodeSpacing;

                        double previousCenterX = GetNodeCenterX(previousNode);

                        if (node.IsIfNode)
                        {
                            double nodeActualWidth = node.GetActualWidth();
                            node.X = previousCenterX - (nodeActualWidth / 2);
                            _logger.LogTrace("IF节点 {NodeName}: 与 {PrevNodeName} 中心对齐", node.DisplayName, previousNode.DisplayName);
                            _logger.LogTrace("  前节点中心X: {PrevCenterX}, 当前节点宽度: {Width}, 计算X: {X}",
                                previousCenterX, nodeActualWidth, node.X);
                        }
                        else if (node.IsParallelNode)
                        {
                            double nodeActualWidth = node.GetActualWidth();
                            node.X = previousCenterX - (nodeActualWidth / 2);
                            _logger.LogTrace("并行节点 {NodeName}: 与 {PrevNodeName} 中心对齐", node.DisplayName, previousNode.DisplayName);
                            _logger.LogTrace("  前节点中心X: {PrevCenterX}, 当前节点宽度: {Width}, 计算X: {X}",
                                previousCenterX, nodeActualWidth, node.X);
                        }
                        else
                        {
                            node.X = previousCenterX - 115;
                            _logger.LogTrace("普通节点 {NodeName}: 与 {PrevNodeName} 中心对齐, X={X}",
                                node.DisplayName, previousNode.DisplayName, node.X);
                        }
                    }

                    _logger.LogTrace("节点 {Index} ({NodeName}) 最终位置: ({X}, {Y})", i + 1, node.DisplayName, node.X, node.Y);
                }

                RebuildAllConnections();
                UpdateCanvasSize();

                _logger.LogDebug("节点重新排列完成");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "重新排列节点失败");
            }
        }

        public void UpdateStartNodePositionAndRearrange(double newX, double newY)
        {
            StartNodeX = newX;
            StartNodeY = newY;
            RearrangeNodes();
        }

        public void UpdateCanvasSize()
        {
            try
            {
                _logger.LogTrace("开始更新主画布大小");

                if (CommandNodes.Count == 0)
                {
                    CanvasWidth = Math.Max(1200, StartNodeX + 800);
                    CanvasHeight = Math.Max(800, StartNodeY + 300);
                    _logger.LogTrace("无节点时画布大小: {Width}x{Height}", CanvasWidth, CanvasHeight);
                    return;
                }

                double maxX = StartNodeX + 231; // 启动节点的右边界
                double maxY = StartNodeY + 64;  // 启动节点的底边界

                _logger.LogTrace("初始边界: 启动节点右边界={MaxX}, 底边界={MaxY}", maxX, maxY);

                foreach (var node in CommandNodes)
                {
                    try
                    {
                        // 强制更新节点控件布局
                        if (node.NodeControl != null)
                        {
                            Application.Current?.Dispatcher.Invoke(() =>
                            {
                                node.NodeControl.UpdateLayout();
                            });
                        }

                        // 计算节点的实际边界
                        double nodeRight, nodeBottom;

                        if (node.IsParallelNode && node.NodeControl is Views.ParallelNodeControlView parallelControl)
                        {
                            try
                            {
                                // 获取并行节点的实际尺寸
                                double parallelControlWidth = 0;
                                double parallelControlHeight = 0;

                                Application.Current?.Dispatcher.Invoke(() =>
                                {
                                    parallelControl.UpdateLayout();

                                    // 获取BranchCanvas的实际尺寸
                                    var branchCanvas = parallelControl.BranchCanvas;
                                    if (branchCanvas != null)
                                    {
                                        branchCanvas.UpdateLayout();
                                        parallelControlWidth = Math.Max(branchCanvas.ActualWidth, branchCanvas.Width);
                                        parallelControlHeight = Math.Max(branchCanvas.ActualHeight, branchCanvas.Height);

                                        // 如果ActualWidth/Height为0，使用DesiredSize
                                        if (parallelControlWidth <= 0)
                                        {
                                            branchCanvas.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                                            parallelControlWidth = branchCanvas.DesiredSize.Width;
                                        }
                                        if (parallelControlHeight <= 0)
                                        {
                                            branchCanvas.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                                            parallelControlHeight = branchCanvas.DesiredSize.Height;
                                        }
                                    }

                                    // 如果还是0，使用整个并行控件的尺寸
                                    if (parallelControlWidth <= 0)
                                    {
                                        parallelControlWidth = Math.Max(parallelControl.ActualWidth, parallelControl.Width);
                                    }
                                    if (parallelControlHeight <= 0)
                                    {
                                        parallelControlHeight = Math.Max(parallelControl.ActualHeight, parallelControl.Height);
                                    }

                                    // 最后的备用尺寸
                                    if (parallelControlWidth <= 0) parallelControlWidth = 1000;
                                    if (parallelControlHeight <= 0) parallelControlHeight = 300;
                                });

                                nodeRight = node.X + parallelControlWidth;
                                nodeBottom = node.Y + parallelControlHeight + 64; // 64是主卡片高度

                                _logger.LogTrace("并行节点 {NodeName}: 位置({X}, {Y}), 控件尺寸({Width}x{Height}), 边界({Right}, {Bottom})",
                                    node.DisplayName, node.X, node.Y, parallelControlWidth, parallelControlHeight, nodeRight, nodeBottom);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "获取并行节点 {NodeName} 尺寸失败，使用默认值", node.DisplayName);
                                nodeRight = node.X + 1000;
                                nodeBottom = node.Y + 400;
                            }
                        }
                        else if (node.IsIfNode && node.NodeControl is Views.IfNodeControlView ifControl)
                        {
                            try
                            {
                                double ifControlWidth = 0;
                                double ifControlHeight = 0;

                                Application.Current?.Dispatcher.Invoke(() =>
                                {
                                    ifControl.UpdateLayout();
                                    var branchCanvas = ifControl.BranchCanvas;
                                    if (branchCanvas != null)
                                    {
                                        branchCanvas.UpdateLayout();
                                        ifControlWidth = Math.Max(branchCanvas.ActualWidth, branchCanvas.Width);
                                        ifControlHeight = Math.Max(branchCanvas.ActualHeight, branchCanvas.Height);

                                        if (ifControlWidth <= 0)
                                        {
                                            branchCanvas.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                                            ifControlWidth = branchCanvas.DesiredSize.Width;
                                        }
                                        if (ifControlHeight <= 0)
                                        {
                                            branchCanvas.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                                            ifControlHeight = branchCanvas.DesiredSize.Height;
                                        }
                                    }

                                    if (ifControlWidth <= 0) ifControlWidth = 800;
                                    if (ifControlHeight <= 0) ifControlHeight = 300;
                                });

                                nodeRight = node.X + ifControlWidth;
                                nodeBottom = node.Y + ifControlHeight + 64;

                                _logger.LogTrace("IF节点 {NodeName}: 位置({X}, {Y}), 控件尺寸({Width}x{Height}), 边界({Right}, {Bottom})",
                                    node.DisplayName, node.X, node.Y, ifControlWidth, ifControlHeight, nodeRight, nodeBottom);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "获取IF节点 {NodeName} 尺寸失败，使用默认值", node.DisplayName);
                                nodeRight = node.X + 800;
                                nodeBottom = node.Y + 350;
                            }
                        }
                        else
                        {
                            // 普通节点（设备命令等）
                            var nodeSize = node.GetActualSize();
                            nodeRight = node.X + nodeSize.Width;
                            nodeBottom = node.Y + nodeSize.Height;

                            _logger.LogTrace("普通节点 {NodeName}: 位置({X}, {Y}), 尺寸({Width}x{Height}), 边界({Right}, {Bottom})",
                                node.DisplayName, node.X, node.Y, nodeSize.Width, nodeSize.Height, nodeRight, nodeBottom);
                        }

                        maxX = Math.Max(maxX, nodeRight);
                        maxY = Math.Max(maxY, nodeBottom);

                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "获取节点 {NodeName} 尺寸失败，使用默认值", node.DisplayName);
                        double defaultWidth = node.IsIfNode ? 800 : node.IsParallelNode ? 1000 : 231;
                        double defaultHeight = node.IsIfNode ? 350 : node.IsParallelNode ? 400 : 64;
                        maxX = Math.Max(maxX, node.X + defaultWidth);
                        maxY = Math.Max(maxY, node.Y + defaultHeight);
                    }
                }

                // 计算新的画布尺寸，添加足够的边距
                double newCanvasWidth = Math.Max(1200, maxX + 200);  // 增加右边距
                double newCanvasHeight = Math.Max(800, maxY + 200);  // 增加下边距

                _logger.LogDebug("画布尺寸计算: maxX={MaxX}, maxY={MaxY} -> 新尺寸: {Width}x{Height}",
                    maxX, maxY, newCanvasWidth, newCanvasHeight);

                bool sizeChanged = Math.Abs(CanvasWidth - newCanvasWidth) > 1 || Math.Abs(CanvasHeight - newCanvasHeight) > 1;

                if (sizeChanged)
                {
                    _logger.LogDebug("画布大小将更新: {OldWidth}x{OldHeight} -> {NewWidth}x{NewHeight}",
                        CanvasWidth, CanvasHeight, newCanvasWidth, newCanvasHeight);
                }

                CanvasWidth = newCanvasWidth;
                CanvasHeight = newCanvasHeight;

                if (sizeChanged)
                {
                    _logger.LogInformation("画布大小已更新: {Width}x{Height}", CanvasWidth, CanvasHeight);
                    RaisePropertyChanged(nameof(CanvasWidth));
                    RaisePropertyChanged(nameof(CanvasHeight));
                }

                _logger.LogTrace("主画布大小更新完成");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "更新画布大小失败");
            }
        }

        #endregion

        #region 命令处理方法
        private bool CanRemoveCommandNode(CommandNode node)
        {
            bool canExecute = node != null;
            _logger.LogTrace("CanRemoveCommandNode 检查: Node={NodeName}, CanExecute={CanExecute}",
                node?.DisplayName ?? "null", canExecute);
            return canExecute;
        }
        private void RemoveCommandNode(CommandNode node)
        {
            try
            {
                if (node != null)
                {
                    _logger.LogDebug("删除节点: {NodeName}", node.DisplayName);
                    CommandNodes.Remove(node);
                    RearrangeNodes();

                    _logger.LogInformation("命令节点 {NodeName} 已从流程设计器移除，剩余节点: {Count}, 连接线: {ConnectionCount}",
                        node.DisplayName, CommandNodes.Count, Connections.Count);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "移除命令节点失败: {NodeName}", node?.DisplayName);
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

                _logger.LogInformation("节点配置验证完成，发现 {Count} 个错误", invalidNodes.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "验证节点配置失败");
                MessageBox.Show($"验证过程中发生错误：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void RefreshPlugins()
        {
            try
            {
                _pluginManager?.LoadPlugins();
                LoadLogicCommandPlugins();
                _logger.LogInformation("插件刷新完成");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "刷新插件失败");
            }
        }

        public void HandleNodeClick(CommandNode clickedNode)
        {
            _nodeManagementService.HandleNodeClick(clickedNode, this);
        }

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
                _logger.LogError(ex, "处理命令拖拽完成事件失败");
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
                _logger.LogInformation("画布已清空");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "清空画布失败");
            }
        }

        public void SelectNode(CommandNode node)
        {
            try
            {
                foreach (var n in CommandNodes)
                {
                    n.IsSelected = false;
                }

                if (node != null)
                {
                    node.IsSelected = true;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "选择节点失败: {NodeName}", node?.DisplayName);
            }
        }

        #endregion
    }
}
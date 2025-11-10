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
using MaxChemical.Modules.Designer.Views;
using System.Windows.Threading;
using System.Xml.Linq;
using MaxChemical.Infrastructure.Events;

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
        // 右侧属性栏设备候选
        public class DeviceOption
        {
            public string Id { get; set; }
            public string Display { get; set; }
        }
        private ObservableCollection<DeviceOption> _deviceOptionsForSelectedNode = new ObservableCollection<DeviceOption>();
        public ObservableCollection<DeviceOption> DeviceOptionsForSelectedNode
        {
            get => _deviceOptionsForSelectedNode;
            private set => SetProperty(ref _deviceOptionsForSelectedNode, value);
        }

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
            set
            {
                if (_selectedNode == value) return;

                _selectedNode = value;

                if (_selectedNode?.DeviceCommand != null)
                {
                    RefreshDeviceOptionsForSelectedNode(_selectedNode);
                }
                else
                {
                    DeviceOptionsForSelectedNode = null; // 或 new ObservableCollection<DeviceOption>()
                }

                RaisePropertyChanged();
            }
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
                    _logger.LogError("插件管理器为null，无法加载逻辑命令插件");
                    return;
                }

                var plugins = _pluginManager.GetAllPlugins();
                _logger.LogDebug("从插件管理器获取到 {Count} 个插件", plugins?.Count() ?? 0);

                if (plugins == null || !plugins.Any())
                {
                    _logger.LogError("没有找到任何逻辑命令插件");
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
                // 订阅UI刷新事件
                _eventAggregator?.GetEvent<FlowDesignerUIRefreshRequestEvent>().Subscribe(OnUIRefreshRequested);
                // 添加新的事件订阅
                SubscribeToFlowDataEvents();
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
        /// <summary>
        /// 处理UI刷新请求
        /// </summary>
        private void OnUIRefreshRequested()
        {
            try
            {
                _logger.LogDebug("收到UI刷新请求，开始同步所有并行节点");

                // 延迟执行以确保UI完全加载
                Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
                {
                    RefreshAllParallelNodesUI();
                   
                }), DispatcherPriority.Background);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "处理UI刷新请求失败");
            }
        }
        /// <summary>
        /// 刷新所有并行节点的UI
        /// </summary>
        private void RefreshAllParallelNodesUI()
        {
            try
            {
                _logger.LogDebug("=== 开始刷新所有并行节点UI ===");

                var parallelNodes = CommandNodes.Where(node => node.IsParallelNode).ToList();
                _logger.LogDebug($"找到 {parallelNodes.Count} 个并行节点需要刷新");

                foreach (var parallelNode in parallelNodes)
                {
                    _logger.LogDebug($"刷新并行节点: {parallelNode.DisplayName}，子节点数: {parallelNode.Children.Count}");

                    if (parallelNode.NodeControl is Views.ParallelNodeControlView parallelControl)
                    {
                        try
                        {
                            // 方法2: 调用刷新方法
                            parallelControl.RefreshForPropertyChange();

                            _logger.LogDebug($"并行节点 {parallelNode.DisplayName} UI刷新完成");
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, $"刷新并行节点 {parallelNode.DisplayName} UI失败");
                        }
                    }
                    
                }

                _logger.LogDebug("=== 所有并行节点UI刷新完成 ===");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "刷新所有并行节点UI失败");
            }
        }
     

        #region 流程数据事件订阅和处理

        private void SubscribeToFlowDataEvents()
        {
            _eventAggregator?.GetEvent<GetCurrentFlowDataRequestEvent>()?.Subscribe(OnGetCurrentFlowDataRequest);
            _eventAggregator?.GetEvent<ClearFlowRequestEvent>()?.Subscribe(OnClearFlowRequest);
            _eventAggregator?.GetEvent<NewFlowCreatedEvent>()?.Subscribe(OnNewFlowCreated);
            _eventAggregator?.GetEvent<LoadFlowRequestEvent>()?.Subscribe(OnLoadFlowRequest);
            _eventAggregator?.GetEvent<FlowLoadedEvent>()?.Subscribe(OnFlowLoaded);
        }

        /// <summary>
        /// 响应获取当前流程数据请求
        /// </summary>
        private void OnGetCurrentFlowDataRequest(Action<FlowDocument> callback)
        {
            try
            {
                var flowData = CreateFlowDocument();
                CollectCommandNodeData(flowData);
                CollectConnectionData(flowData);

                callback?.Invoke(flowData);
                _logger.LogDebug($"收集流程数据完成: 节点{flowData.CommandNodes.Count}个, 连接{flowData.Connections.Count}个");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "收集流程数据失败");
                callback?.Invoke(null);
            }
        }

        /// <summary>
        /// 创建流程文档
        /// </summary>
        private FlowDocument CreateFlowDocument()
        {
            return new FlowDocument
            {
                Name = "当前流程",
                CanvasWidth = CanvasWidth,
                CanvasHeight = CanvasHeight,
                StartNodeX = StartNodeX,
                StartNodeY = StartNodeY,
                LastModifiedTime = DateTime.Now
            };
        }

        /// <summary>
        /// 收集命令节点数据
        /// </summary>
        private void CollectCommandNodeData(FlowDocument flowData)
        {
            if (CommandNodes == null) return;

            foreach (var commandNode in CommandNodes)
            {
                var nodeData = ConvertCommandNodeToData(commandNode);
                flowData.CommandNodes.Add(nodeData);
            }
        }

        /// <summary>
        /// 将命令节点转换为数据模型
        /// </summary>
        private FlowCommandNodeData ConvertCommandNodeToData(CommandNode commandNode)
        {
            var nodeData = new FlowCommandNodeData
            {
                NodeId = commandNode.NodeId,
                DisplayName = commandNode.DisplayName,
                X = commandNode.X,
                Y = commandNode.Y,
                Order = commandNode.Order,
                IsVisible = commandNode.IsVisible,
                ExecutionStatus = commandNode.ExecutionStatus.ToString(),
                ExecutionStartTime = commandNode.ExecutionStartTime,
                ExecutionEndTime = commandNode.ExecutionEndTime,
                ExecutionErrorMessage = commandNode.ExecutionErrorMessage
            };

            // 处理设备命令
            if (commandNode.IsDeviceCommand && commandNode.DeviceCommand != null)
            {
                nodeData.CommandType = "DeviceCommand";
                nodeData.DeviceCommandName = commandNode.DeviceCommand.Name;
                nodeData.DeviceCommandType = commandNode.DeviceCommand.GetType().Name;
                nodeData.BoundDeviceId = commandNode.BoundDeviceId;
                nodeData.BoundDeviceName = commandNode.BoundDeviceName;

                // 保存参数覆盖
                if (commandNode.ParameterOverrides != null)
                {
                    nodeData.ParameterOverrides = new Dictionary<string, string>(commandNode.ParameterOverrides);
                }

                // 保存设备命令属性
                CollectDeviceCommandProperties(commandNode.DeviceCommand, nodeData.DeviceCommandProperties);
            }
            // 处理逻辑命令
            else if (commandNode.IsLogicCommand && commandNode.LogicCommand != null)
            {
                nodeData.CommandType = "LogicCommand";
                nodeData.LogicCommandType = commandNode.LogicCommand.CommandType.ToString();
                nodeData.LogicCommandName = commandNode.LogicCommand.Name;

                // 保存逻辑命令属性
                if (commandNode.LogicCommand.Properties != null)
                {
                    nodeData.LogicCommandProperties = new Dictionary<string, object>(commandNode.LogicCommand.Properties);
                }
            }

            // 保存执行属性
            if (commandNode.ExecutionProperties != null)
            {
                nodeData.ExecutionProperties = new Dictionary<string, object>(commandNode.ExecutionProperties);
            }

            // 递归保存子节点 - 关键是这里要正确处理分支
            if (commandNode.HasChildren)
            {
                SaveChildNodesWithBranchInfo(commandNode, nodeData);
            }

            // 保存节点控件相关属性
            CollectNodeControlProperties(commandNode, nodeData.NodeControlProperties);

            return nodeData;
        }
        /// <summary>
        /// 保存子节点并正确设置分支信息
        /// </summary>
        private void SaveChildNodesWithBranchInfo(CommandNode parentNode, FlowCommandNodeData parentNodeData)
        {
            try
            {
                var logicCommandType = parentNode.LogicCommand?.CommandType;

                switch (logicCommandType)
                {
                    case LogicCommandType.Parallel:
                        SaveParallelNodeChildren(parentNode, parentNodeData);
                        break;

                    case LogicCommandType.If:
                        SaveIfNodeChildren(parentNode, parentNodeData);
                        break;

                    case LogicCommandType.Loop:
                        SaveLoopNodeChildren(parentNode, parentNodeData);
                        break;

                    default:
                        // 普通的顺序子节点
                        SaveSequentialChildren(parentNode, parentNodeData);
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "保存子节点分支信息失败: {NodeName}", parentNode.DisplayName);

                // Fallback: 保存所有子节点但不设置分支信息
                foreach (var child in parentNode.Children)
                {
                    var childData = ConvertCommandNodeToData(child);
                    parentNodeData.Children.Add(childData);
                }
            }
        }

        /// <summary>
        /// 保存并行节点的子节点
        /// </summary>
        private void SaveParallelNodeChildren(CommandNode parallelNode, FlowCommandNodeData parallelNodeData)
        {
            try
            {
                _logger.LogDebug($"保存并行节点 {parallelNode.DisplayName} 的分支子节点");

                // 方法1: 如果CommandNode有分支管理
                if (HasBranchManagement(parallelNode))
                {
                    SaveFromBranchManagement(parallelNode, parallelNodeData);
                }
                // 方法2: 根据ExecutionProperties中的BranchIndex分组
                else if (parallelNode.Children != null && parallelNode.Children.Any())
                {
                    SaveFromBranchIndex(parallelNode, parallelNodeData, "Parallel");
                }

                _logger.LogDebug($"并行节点保存完成，共 {parallelNodeData.Children.Count} 个子节点");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "保存并行节点子节点失败");
            }
        }

        /// <summary>
        /// 保存IF节点的子节点
        /// </summary>
        private void SaveIfNodeChildren(CommandNode ifNode, FlowCommandNodeData ifNodeData)
        {
            try
            {
                _logger.LogDebug($"=== 保存IF节点 {ifNode.DisplayName} 的分支子节点 ===");

                if (ifNode.Children == null || !ifNode.Children.Any())
                {
                    _logger.LogDebug("IF节点没有子节点");
                    return;
                }

                foreach (var child in ifNode.Children)
                {
                    var childData = ConvertCommandNodeToData(child);

                    // 更准确地确定分支类型
                    string branchType = DetermineIfBranchTypeAccurately(child, ifNode);

                    // 设置分支信息到ExecutionProperties
                    if (childData.ExecutionProperties == null)
                    {
                        childData.ExecutionProperties = new Dictionary<string, object>();
                    }
                    childData.ExecutionProperties["BranchType"] = branchType;
                    childData.ExecutionProperties["ParentIfNodeId"] = ifNode.NodeId; // 添加父节点引用

                    _logger.LogDebug($"保存IF子节点: {child.DisplayName} -> {branchType} 分支");
                    ifNodeData.Children.Add(childData);
                }

                _logger.LogDebug($"IF节点保存完成，共 {ifNodeData.Children.Count} 个子节点");

                // 输出调试信息
                foreach (var child in ifNodeData.Children)
                {
                    var branchType = child.ExecutionProperties?.ContainsKey("BranchType") == true
                        ? child.ExecutionProperties["BranchType"]
                        : "Unknown";
                    _logger.LogDebug($"  - {child.DisplayName}: {branchType}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "保存IF节点子节点失败");
            }
        }
        /// <summary>
        /// 更准确地确定IF分支类型
        /// </summary>
        private string DetermineIfBranchTypeAccurately(CommandNode childNode, CommandNode ifNode)
        {
            try
            {
                // 方法1: 优先从ExecutionProperties获取
                var existingBranchType = childNode.GetExecutionProperty<string>("BranchType", null);
                if (!string.IsNullOrEmpty(existingBranchType))
                {
                    _logger.LogDebug($"从ExecutionProperties获取分支类型: {existingBranchType}");
                    return existingBranchType;
                }

                // 方法2: 从IF控件获取分支信息
                if (ifNode.NodeControl is Views.IfNodeControlView ifControl)
                {
                    string controlBranchType = GetBranchTypeFromIfControl(childNode, ifControl);
                    if (!string.IsNullOrEmpty(controlBranchType))
                    {
                        _logger.LogDebug($"从IF控件获取分支类型: {controlBranchType}");
                        return controlBranchType;
                    }
                }

                // 方法3: 根据子节点在Children中的位置推断（简单逻辑）
                var childIndex = ifNode.Children.IndexOf(childNode);
                if (childIndex >= 0)
                {
                    // 假设偶数索引为True分支，奇数索引为False分支
                    // 这是一个简化的逻辑，你可以根据实际情况调整
                    string inferredBranchType = (childIndex % 2 == 0) ? "True" : "False";
                    _logger.LogDebug($"根据索引推断分支类型: 索引{childIndex} -> {inferredBranchType}");
                    return inferredBranchType;
                }

                // 默认为True分支
                _logger.LogDebug("使用默认分支类型: True");
                return "True";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "确定IF分支类型失败");
                return "True";
            }
        }
        /// <summary>
        /// 从IF控件获取分支类型
        /// </summary>
        private string GetBranchTypeFromIfControl(CommandNode childNode, Views.IfNodeControlView ifControl)
        {
            try
            {
                // 这里需要根据你的IfNodeControlView具体实现来获取分支信息
                // 假设你的IF控件有方法可以查询节点属于哪个分支

                // 方法1: 如果IF控件有公开的方法
                if (ifControl.GetType().GetMethod("GetNodeBranchType") != null)
                {
                    var result = ifControl.GetType().GetMethod("GetNodeBranchType")
                        .Invoke(ifControl, new object[] { childNode });
                    if (result is string branchType && !string.IsNullOrEmpty(branchType))
                    {
                        return branchType;
                    }
                }

                // 方法2: 检查子节点的特定属性
                var branchProperty = childNode.GetExecutionProperty<string>("IfBranchType", null);
                if (!string.IsNullOrEmpty(branchProperty))
                {
                    return branchProperty;
                }

                // 方法3: 其他获取方式...

                return null; // 无法确定
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "从IF控件获取分支类型失败");
                return null;
            }
        }
        /// <summary>
        /// 保存循环节点的子节点
        /// </summary>
        private void SaveLoopNodeChildren(CommandNode loopNode, FlowCommandNodeData loopNodeData)
        {
            try
            {
                _logger.LogDebug($"保存循环节点 {loopNode.DisplayName} 的子节点");

                if (loopNode.Children != null && loopNode.Children.Any())
                {
                    foreach (var child in loopNode.Children)
                    {
                        var childData = ConvertCommandNodeToData(child);

                        // 循环节点的子节点按顺序执行
                        if (childData.ExecutionProperties == null)
                        {
                            childData.ExecutionProperties = new Dictionary<string, object>();
                        }
                        childData.ExecutionProperties["LoopChildIndex"] = loopNodeData.Children.Count;

                        loopNodeData.Children.Add(childData);
                    }
                }

                _logger.LogDebug($"循环节点保存完成，共 {loopNodeData.Children.Count} 个子节点");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "保存循环节点子节点失败");
            }
        }

        /// <summary>
        /// 保存顺序子节点
        /// </summary>
        private void SaveSequentialChildren(CommandNode parentNode, FlowCommandNodeData parentNodeData)
        {
            try
            {
                if (parentNode.Children != null && parentNode.Children.Any())
                {
                    foreach (var child in parentNode.Children)
                    {
                        var childData = ConvertCommandNodeToData(child);
                        parentNodeData.Children.Add(childData);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "保存顺序子节点失败");
            }
        }

        /// <summary>
        /// 检查节点是否有分支管理能力
        /// </summary>
        private bool HasBranchManagement(CommandNode node)
        {
            // 根据你的CommandNode实现检查是否有Branches属性
            try
            {
                // 使用反射检查是否有Branches属性
                var branchesProperty = node.GetType().GetProperty("Branches");
                return branchesProperty != null;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 从分支管理保存子节点
        /// </summary>
        private void SaveFromBranchManagement(CommandNode parentNode, FlowCommandNodeData parentNodeData)
        {
            try
            {
                // 假设CommandNode有Branches属性
                var branchesProperty = parentNode.GetType().GetProperty("Branches");
                if (branchesProperty?.GetValue(parentNode) is System.Collections.IList branches)
                {
                    for (int branchIndex = 0; branchIndex < branches.Count; branchIndex++)
                    {
                        // 修正泛型类型检查
                        if (branches[branchIndex] is IList<CommandNode> branchChildren)
                        {
                            foreach (var child in branchChildren)
                            {
                                var childData = ConvertCommandNodeToData(child);

                                // 设置分支索引
                                if (childData.ExecutionProperties == null)
                                {
                                    childData.ExecutionProperties = new Dictionary<string, object>();
                                }
                                childData.ExecutionProperties["BranchIndex"] = branchIndex;

                                parentNodeData.Children.Add(childData);
                                _logger.LogDebug($"从分支 {branchIndex} 保存子节点: {child.DisplayName}");
                            }
                        }
                        // 如果不是IList<CommandNode>，尝试其他类型
                        else if (branches[branchIndex] is System.Collections.IList genericBranchList)
                        {
                            foreach (var item in genericBranchList)
                            {
                                if (item is CommandNode child)
                                {
                                    var childData = ConvertCommandNodeToData(child);

                                    if (childData.ExecutionProperties == null)
                                    {
                                        childData.ExecutionProperties = new Dictionary<string, object>();
                                    }
                                    childData.ExecutionProperties["BranchIndex"] = branchIndex;

                                    parentNodeData.Children.Add(childData);
                                    _logger.LogDebug($"从分支 {branchIndex} 保存子节点: {child.DisplayName}");
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "从分支管理保存子节点失败");
            }
        }

        /// <summary>
        /// 从BranchIndex保存子节点
        /// </summary>
        private void SaveFromBranchIndex(CommandNode parentNode, FlowCommandNodeData parentNodeData, string nodeType)
        {
            try
            {
                foreach (var child in parentNode.Children)
                {
                    var childData = ConvertCommandNodeToData(child);

                    // 尝试从ExecutionProperties获取分支信息 - 明确指定类型
                    var branchIndexObj = child.GetExecutionProperty<object>("BranchIndex", null);
                    if (branchIndexObj != null)
                    {
                        if (childData.ExecutionProperties == null)
                        {
                            childData.ExecutionProperties = new Dictionary<string, object>();
                        }
                        childData.ExecutionProperties["BranchIndex"] = branchIndexObj;

                        _logger.LogDebug($"{nodeType}子节点 {child.DisplayName} 来自分支 {branchIndexObj}");
                    }
                    else
                    {
                        _logger.LogWarning($"{nodeType}子节点 {child.DisplayName} 没有分支索引信息");
                    }

                    parentNodeData.Children.Add(childData);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "从BranchIndex保存子节点失败");
            }
        }

        /// <summary>
        /// 确定IF分支类型
        /// </summary>
        private string DetermineBranchType(CommandNode childNode, CommandNode ifNode)
        {
            try
            {
                // 方法1: 从ExecutionProperties获取 - 明确指定类型
                var branchTypeObj = childNode.GetExecutionProperty<string>("BranchType", null);
                if (!string.IsNullOrEmpty(branchTypeObj))
                {
                    return branchTypeObj;
                }

                // 方法2: 根据节点在IF控件中的位置判断
                if (ifNode.NodeControl is Views.IfNodeControlView ifControl)
                {
                    // 这里需要根据你的具体IfNodeControlView实现来判断
                    return DetermineBranchFromControl(childNode, ifControl);
                }

                // 方法3: 默认逻辑
                return "True"; // 默认为True分支
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "确定IF分支类型失败");
                return "True";
            }
        }

        /// <summary>
        /// 从控件确定分支类型
        /// </summary>
        private string DetermineBranchFromControl(CommandNode childNode, Views.IfNodeControlView ifControl)
        {
            try
            {
                // 检查childNode的某个属性 - 明确指定类型
                var branchProperty = childNode.GetExecutionProperty<string>("IfBranchType", null);
                if (!string.IsNullOrEmpty(branchProperty))
                {
                    return branchProperty;
                }

                return "True"; // 默认
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "从控件确定分支类型失败");
                return "True";
            }
        }
        /// <summary>
        /// 收集设备命令属性
        /// </summary>
        private void CollectDeviceCommandProperties(DeviceCommand deviceCommand, Dictionary<string, object> properties)
        {
            if (deviceCommand?.InputParameters?.Variables == null) return;

            foreach (var parameter in deviceCommand.InputParameters.Variables)
            {
                if (parameter.Value != null)
                {
                    properties[parameter.Name] = parameter.Value;
                }
            }
        }

        /// <summary>
        /// 收集节点控件属性
        /// </summary>
        private void CollectNodeControlProperties(CommandNode commandNode, Dictionary<string, object> properties)
        {
            if (commandNode.NodeControl == null) return;

            try
            {
                // 根据不同的节点控件类型收集不同的属性
                switch (commandNode.NodeControl)
                {
                    case Views.IfNodeControlView ifControl:
                        CollectIfNodeProperties(ifControl, properties);
                        break;
                    case Views.LoopNodeControlView loopControl:
                        CollectLoopNodeProperties(loopControl, properties);
                        break;
                    case Views.ParallelNodeControlView parallelControl:
                        CollectParallelNodeProperties(parallelControl, properties);
                        break;
                    case Views.WaitNodeControlView waitControl:
                        CollectWaitNodeProperties(waitControl, properties);
                        break;
                    case Views.SetVariableNodeControlView setVarControl:
                        CollectSetVariableNodeProperties(setVarControl, properties);
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "收集节点控件属性失败: {NodeName}", commandNode.DisplayName);
            }
        }

        private void CollectIfNodeProperties(Views.IfNodeControlView ifControl, Dictionary<string, object> properties)
        {
            try
            {
                if (ifControl.DataContext is CommandNode node)
                {
                    if (node.LogicCommand?.Properties != null)
                    {
                        foreach (var prop in node.LogicCommand.Properties)
                        {
                            properties[$"If_{prop.Key}"] = prop.Value;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "收集IF节点属性失败");
            }
        }

        private void CollectLoopNodeProperties(Views.LoopNodeControlView loopControl, Dictionary<string, object> properties)
        {
            try
            {
                if (loopControl.DataContext is CommandNode node)
                {
                    if (node.LogicCommand?.Properties != null)
                    {
                        foreach (var prop in node.LogicCommand.Properties)
                        {
                            properties[$"Loop_{prop.Key}"] = prop.Value;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "收集循环节点属性失败");
            }
        }

        private void CollectParallelNodeProperties(Views.ParallelNodeControlView parallelControl, Dictionary<string, object> properties)
        {
            try
            {
                if (parallelControl.DataContext is CommandNode node)
                {
                    if (node.LogicCommand?.Properties != null)
                    {
                        foreach (var prop in node.LogicCommand.Properties)
                        {
                            properties[$"Parallel_{prop.Key}"] = prop.Value;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "收集并行节点属性失败");
            }
        }

        private void CollectWaitNodeProperties(Views.WaitNodeControlView waitControl, Dictionary<string, object> properties)
        {
            try
            {
                if (waitControl.DataContext is CommandNode node)
                {
                    if (node.LogicCommand?.Properties != null)
                    {
                        foreach (var prop in node.LogicCommand.Properties)
                        {
                            properties[$"Wait_{prop.Key}"] = prop.Value;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "收集等待节点属性失败");
            }
        }

        private void CollectSetVariableNodeProperties(Views.SetVariableNodeControlView setVarControl, Dictionary<string, object> properties)
        {
            try
            {
                if (setVarControl.DataContext is CommandNode node)
                {
                    if (node.LogicCommand?.Properties != null)
                    {
                        foreach (var prop in node.LogicCommand.Properties)
                        {
                            properties[$"SetVariable_{prop.Key}"] = prop.Value;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "收集设置变量节点属性失败");
            }
        }

        /// <summary>
        /// 收集连接数据
        /// </summary>
        private void CollectConnectionData(FlowDocument flowData)
        {
            if (Connections == null) return;

            foreach (var connection in Connections)
            {
                var connectionData = new FlowConnectionData
                {
                    FromNodeId = connection.FromNodeId,
                    ToNodeId = connection.ToNodeId,
                    X = connection.X,
                    Y = connection.Y,
                    //LineLength = connection.LineLength,
                    //ArrowTop = connection.ArrowTop,
                    //TotalHeight = connection.TotalHeight
                };

                flowData.Connections.Add(connectionData);
            }
        }

        /// <summary>
        /// ✅ 修复：清空流程请求处理（包括设备）
        /// </summary>
        private void OnClearFlowRequest()
        {
            try
            {
                _logger.LogDebug("========== 开始清空流程 ==========");
                _logger.LogDebug("  清空前 - 节点: {NodeCount}, 连接: {ConnectionCount}, 设备: {DeviceCount}",
                    CommandNodes?.Count ?? 0,
                    Connections?.Count ?? 0,
                    CanvasDevices?.Count ?? 0);

                // ✅ 1. 清空所有命令节点
                if (CommandNodes != null && CommandNodes.Count > 0)
                {
                    _logger.LogDebug("清除 {Count} 个命令节点", CommandNodes.Count);
                    CommandNodes.Clear();
                }

                // ✅ 2. 清空所有连接
                if (Connections != null && Connections.Count > 0)
                {
                    _logger.LogDebug("清除 {Count} 个连接", Connections.Count);
                    Connections.Clear();
                }

                // ✅ 3. 清空所有画布设备（这是关键！）
                if (CanvasDevices != null && CanvasDevices.Count > 0)
                {
                    _logger.LogDebug("清除 {Count} 个画布设备", CanvasDevices.Count);
                    CanvasDevices.Clear();
                }

                // ✅ 4. 重置画布属性
                CanvasWidth = 1200;
                CanvasHeight = 800;
                StartNodeX = (CanvasWidth - 231) / 2;
                StartNodeY = 20;

                // ✅ 5. 清空选择状态
                SelectedNode = null;

                // ✅ 6. 清空设备选项
                DeviceOptionsForSelectedNode?.Clear();

                _logger.LogDebug("  清空后 - 节点: {NodeCount}, 连接: {ConnectionCount}, 设备: {DeviceCount}",
                    CommandNodes?.Count ?? 0,
                    Connections?.Count ?? 0,
                    CanvasDevices?.Count ?? 0);
                _logger.LogDebug("========== 流程清空完成 ==========");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "清空流程失败");
            }
        }


        /// <summary>
        /// 新流程创建事件处理
        /// </summary>
        private void OnNewFlowCreated(FlowDocument newFlow)
        {
            try
            {
                // 应用新流程的画布设置
                CanvasWidth = newFlow.CanvasWidth;
                CanvasHeight = newFlow.CanvasHeight;
                StartNodeX = newFlow.StartNodeX;
                StartNodeY = newFlow.StartNodeY;

                _logger.LogDebug($"新流程已创建: {newFlow.Name}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "处理新流程创建事件失败");
            }
        }

        /// <summary>
        /// 加载流程请求处理
        /// </summary>
        private void OnLoadFlowRequest(FlowDocument flowDocument)
        {
            try
            {
                _logger.LogDebug($"开始加载流程: {flowDocument.Name}");
                _logger.LogDebug($"节点数量: {flowDocument.CommandNodes?.Count ?? 0}");
                _logger.LogDebug($"连接数量: {flowDocument.Connections?.Count ?? 0}");

                // 更新画布设置
                CanvasWidth = flowDocument.CanvasWidth;
                CanvasHeight = flowDocument.CanvasHeight;
                StartNodeX = flowDocument.StartNodeX;
                StartNodeY = flowDocument.StartNodeY;

                // 恢复命令节点
                RestoreCommandNodes(flowDocument.CommandNodes);

                // 延迟恢复连接确保节点已创建
                System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                {
                    RestoreConnections(flowDocument.Connections);
                    _eventAggregator?.GetEvent<FlowLoadedEvent>()?.Publish(flowDocument);
                }), System.Windows.Threading.DispatcherPriority.Loaded);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "加载流程失败");
            }
        }

        /// <summary>
        /// 恢复命令节点
        /// </summary>
        private void RestoreCommandNodes(List<FlowCommandNodeData> nodeDataList)
        {
            if (nodeDataList == null || !nodeDataList.Any())
            {
                _logger.LogDebug("没有节点需要恢复");
                return;
            }

            _logger.LogDebug($"开始恢复 {nodeDataList.Count} 个节点");

            foreach (var nodeData in nodeDataList)
            {
                try
                {
                    RestoreSingleCommandNode(nodeData);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "恢复节点 {NodeName} 失败", nodeData.DisplayName);
                }
            }

            _logger.LogDebug($"节点恢复完成，当前节点数量: {CommandNodes?.Count ?? 0}");
        }

        /// <summary>
        /// 恢复单个命令节点
        /// </summary>
        private void RestoreSingleCommandNode(FlowCommandNodeData nodeData)
        {
            _logger.LogDebug($"恢复节点: {nodeData.DisplayName} ID: {nodeData.NodeId}");

            CommandNode commandNode = null;

            // 根据命令类型创建相应的节点
            if (nodeData.CommandType == "DeviceCommand")
            {
                var deviceCommand = RestoreDeviceCommandFromNodeData(nodeData);
                if (deviceCommand != null)
                {
                    commandNode = new CommandNode(deviceCommand);
                }
            }
            else if (nodeData.CommandType == "LogicCommand")
            {
                var logicCommand = RestoreLogicCommandFromNodeData(nodeData);
                if (logicCommand != null)
                {
                    commandNode = new CommandNode(logicCommand);

                    // 为逻辑命令创建节点控件
                    var plugin = _pluginManager?.GetPlugin(logicCommand.CommandType.ToString());
                    if (plugin != null)
                    {
                        commandNode.NodeControl = plugin.CreateNodeControl(commandNode);
                        SubscribeToNodeControlEvents(commandNode, plugin);
                    }
                }
            }

            if (commandNode == null)
            {
                _logger.LogError($"无法恢复节点: {nodeData.DisplayName}");
                return;
            }

            // 恢复节点基本属性
            RestoreNodeBasicProperties(commandNode, nodeData);

            // 恢复设备绑定信息
            RestoreNodeDeviceBinding(commandNode, nodeData);

            // 恢复参数覆盖
            RestoreNodeParameterOverrides(commandNode, nodeData);

            // 恢复执行属性
            RestoreNodeExecutionProperties(commandNode, nodeData);

            // 恢复执行状态
            RestoreNodeExecutionStatus(commandNode, nodeData);

            // 递归恢复子节点
            RestoreChildNodes(commandNode, nodeData.Children);
           
            // 恢复节点控件属性
            RestoreNodeControlProperties(commandNode, nodeData.NodeControlProperties);

            // 添加到集合
            CommandNodes.Add(commandNode);

            _logger.LogDebug($"节点 {nodeData.DisplayName} 恢复成功");
        }

        private DeviceCommand RestoreDeviceCommandFromNodeData(FlowCommandNodeData nodeData)
        {
            try
            {
                // 从设备管理器中查找设备命令
                var allDevices = _deviceManager.GetAllDevices();

                foreach (var device in allDevices)
                {
                    var command = device.Commands?.FirstOrDefault(c => c.Name == nodeData.DeviceCommandName);
                    if (command != null)
                    {
                        // 创建命令副本
                        var restoredCommand = new DeviceCommand
                        {
                            Name = command.Name,
                            HelpText = command.HelpText,
                            ImageLocation = command.ImageLocation,
                            InputParameters = CloneDeviceParameters(command.InputParameters),
                            OutputParameters = CloneDeviceParameters(command.OutputParameters)
                        };

                        // 恢复设备命令的属性
                        if (nodeData.DeviceCommandProperties != null && restoredCommand.InputParameters?.Variables != null)
                        {
                            foreach (var prop in nodeData.DeviceCommandProperties)
                            {
                                var parameter = restoredCommand.InputParameters.Variables.FirstOrDefault(v => v.Name == prop.Key);
                                if (parameter != null)
                                {
                                    parameter.Value = prop.Value;
                                }
                            }
                        }

                        return restoredCommand;
                    }
                }

                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "恢复设备命令失败");
                return null;
            }
        }

        private LogicCommand RestoreLogicCommandFromNodeData(FlowCommandNodeData nodeData)
        {
            try
            {
                var logicCommand = new LogicCommand
                {
                    Name = nodeData.LogicCommandName ?? nodeData.LogicCommandType,
                    CommandType = Enum.TryParse<LogicCommandType>(nodeData.LogicCommandType, out var commandType)
                        ? commandType
                        : LogicCommandType.Unknown
                };

                // 恢复逻辑命令的属性
                if (nodeData.LogicCommandProperties != null)
                {
                    logicCommand.Properties = new Dictionary<string, object>(nodeData.LogicCommandProperties);
                }
                else
                {
                    logicCommand.Properties = new Dictionary<string, object>();
                }

                return logicCommand;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "恢复逻辑命令失败");
                return null;
            }
        }

        private DeviceParameters CloneDeviceParameters(DeviceParameters original)
        {
            if (original?.Variables == null) return null;

            var cloned = new DeviceParameters();
            foreach (var variable in original.Variables)
            {
                cloned.Variables.Add(variable);
            }
            return cloned;
        }

        private void RestoreNodeBasicProperties(CommandNode commandNode, FlowCommandNodeData nodeData)
        {
            commandNode.NodeId = nodeData.NodeId;
            commandNode.DisplayName = nodeData.DisplayName;
            commandNode.X = nodeData.X;
            commandNode.Y = nodeData.Y;
            commandNode.Order = nodeData.Order;
            commandNode.IsVisible = nodeData.IsVisible;
        }

        private void RestoreNodeDeviceBinding(CommandNode commandNode, FlowCommandNodeData nodeData)
        {
            if (!string.IsNullOrEmpty(nodeData.BoundDeviceId))
            {
                commandNode.BoundDeviceId = nodeData.BoundDeviceId;
                commandNode.BoundDeviceName = nodeData.BoundDeviceName;
            }
        }

        private void RestoreNodeParameterOverrides(CommandNode commandNode, FlowCommandNodeData nodeData)
        {
            if (nodeData.ParameterOverrides != null && nodeData.ParameterOverrides.Any())
            {
                commandNode.ApplyParameterOverrides(nodeData.ParameterOverrides);
            }
        }

        private void RestoreNodeExecutionProperties(CommandNode commandNode, FlowCommandNodeData nodeData)
        {
            if (nodeData.ExecutionProperties == null) return;

            try
            {
                foreach (var prop in nodeData.ExecutionProperties)
                {
                    // 确保正确设置执行属性
                    commandNode.SetExecutionProperty(prop.Key, prop.Value);

                    _logger.LogDebug($"恢复执行属性: {prop.Key} = {prop.Value} for node {commandNode.DisplayName}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "恢复执行属性失败: {NodeName}", commandNode.DisplayName);
            }
        }

        private void RestoreNodeExecutionStatus(CommandNode commandNode, FlowCommandNodeData nodeData)
        {
            if (Enum.TryParse<CommandNode.NodeExecutionStatus>(nodeData.ExecutionStatus, out var status))
            {
                commandNode.ExecutionStatus = status;
            }
            commandNode.ExecutionStartTime = nodeData.ExecutionStartTime;
            commandNode.ExecutionEndTime = nodeData.ExecutionEndTime;
            commandNode.ExecutionErrorMessage = nodeData.ExecutionErrorMessage;
        }

        private void RestoreChildNodes(CommandNode parentNode, List<FlowCommandNodeData> childDataList)
        {
            if (childDataList == null || !childDataList.Any())
            {
                _logger.LogDebug("没有子节点需要恢复");
                return;
            }

            _logger.LogDebug($"开始恢复 {childDataList.Count} 个子节点到父节点: {parentNode.DisplayName}");

            try
            {
                var logicCommandType = parentNode.LogicCommand?.CommandType;

                switch (logicCommandType)
                {
                    case LogicCommandType.Parallel:
                        RestoreParallelNodeChildren(parentNode, childDataList);
                        break;

                    case LogicCommandType.If:
                        RestoreIfNodeChildren(parentNode, childDataList);
                        break;

                    case LogicCommandType.Loop:
                        RestoreLoopNodeChildren(parentNode, childDataList);
                        break;

                    default:
                        // 普通的顺序子节点
                        RestoreSequentialChildren(parentNode, childDataList);
                        break;
                }

                
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "恢复子节点失败: {NodeName}", parentNode.DisplayName);

                // Fallback: 简单恢复所有子节点
                foreach (var childData in childDataList)
                {
                    var childNode = RestoreChildCommandNode(childData);
                    if (childNode != null)
                    {
                        parentNode.Children.Add(childNode);
                    }
                }
            }

            _logger.LogDebug($"子节点恢复完成，父节点 {parentNode.DisplayName} 现有子节点数: {parentNode.Children.Count}");
        }
        /// <summary>
        /// 恢复并行节点的子节点
        /// </summary>
        private void RestoreParallelNodeChildren(CommandNode parallelNode, List<FlowCommandNodeData> childDataList)
        {
            try
            {
                _logger.LogDebug($"=== 开始恢复并行节点 {parallelNode.DisplayName} ===");
                _logger.LogDebug($"要恢复的子节点数量: {childDataList.Count}");
                _logger.LogDebug($"并行节点当前Children数量: {parallelNode.Children.Count}");

                // 按分支索引分组
                var branchGroups = childDataList
                    .GroupBy(child => GetBranchIndex(child))
                    .OrderBy(g => g.Key)
                    .ToList();

                _logger.LogDebug($"分支分组情况:");
                foreach (var group in branchGroups)
                {
                    _logger.LogDebug($"  分支 {group.Key}: {group.Count()} 个节点");
                }

                foreach (var branchGroup in branchGroups)
                {
                    int branchIndex = branchGroup.Key;
                    _logger.LogDebug($"--- 处理分支 {branchIndex} ---");

                    foreach (var childData in branchGroup)
                    {
                        var childNode = RestoreChildCommandNode(childData);
                        if (childNode != null)
                        {
                            _logger.LogDebug($"恢复子节点: {childNode.DisplayName}");

                            // 设置分支信息
                            childNode.SetExecutionProperty("BranchIndex", branchIndex);
                            _logger.LogDebug($"设置BranchIndex: {branchIndex}");

                            // 添加到父节点
                            parallelNode.Children.Add(childNode);
                            _logger.LogDebug($"添加到父节点Children，当前总数: {parallelNode.Children.Count}");
                        }
                    }
                }

                _logger.LogDebug($"数据恢复完成，Children总数: {parallelNode.Children.Count}");

                // 延迟触发UI同步
                Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
                {
                    TriggerParallelNodeUISync(parallelNode);
                }), DispatcherPriority.Loaded);

            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "恢复并行节点子节点失败");
            }
        }

        /// <summary>
        /// 触发并行节点UI同步
        /// </summary>
        private void TriggerParallelNodeUISync(CommandNode parallelNode)
        {
            try
            {
                _logger.LogDebug($"=== 触发并行节点UI同步: {parallelNode.DisplayName} ===");

                if (parallelNode.NodeControl is Views.ParallelNodeControlView parallelControl)
                {
                    _logger.LogDebug("找到ParallelNodeControlView，开始同步UI");

                    //  调用刷新方法
                    try
                    {
                        parallelControl.RefreshForPropertyChange();
                        _logger.LogDebug("RefreshForPropertyChange 调用成功");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "RefreshForPropertyChange 失败");
                    }
                }
                else
                {
                    _logger.LogError("NodeControl 不是 ParallelNodeControlView 类型");
                    if (parallelNode.NodeControl != null)
                    {
                        _logger.LogError($"实际类型: {parallelNode.NodeControl.GetType().Name}");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "触发并行节点UI同步失败");
            }
        }

        /// <summary>
        /// 恢复IF节点的子节点
        /// </summary>
        private void RestoreIfNodeChildren(CommandNode ifNode, List<FlowCommandNodeData> childDataList)
        {
            try
            {
                _logger.LogDebug($"=== 开始恢复IF节点 {ifNode.DisplayName} ===");
                _logger.LogDebug($"要恢复的子节点数量: {childDataList.Count}");

                // 输出所有子节点的分支信息
                foreach (var childData in childDataList)
                {
                    var branchType = GetBranchType(childData);
                    _logger.LogDebug($"  子节点: {childData.DisplayName} -> {branchType} 分支");
                }

                // 按分支类型分组
                var branchGroups = childDataList
                    .GroupBy(child => GetBranchType(child))
                    .ToList();

                _logger.LogDebug($"分支分组情况:");
                foreach (var group in branchGroups)
                {
                    _logger.LogDebug($"  {group.Key} 分支: {group.Count()} 个节点");
                }

                foreach (var branchGroup in branchGroups)
                {
                    string branchType = branchGroup.Key;
                    _logger.LogDebug($"--- 处理 {branchType} 分支 ---");

                    foreach (var childData in branchGroup)
                    {
                        var childNode = RestoreChildCommandNode(childData);
                        if (childNode != null)
                        {
                            _logger.LogDebug($"恢复子节点: {childNode.DisplayName}");

                            // 设置分支信息
                            childNode.SetExecutionProperty("BranchType", branchType);
                            childNode.SetExecutionProperty("ParentIfNodeId", ifNode.NodeId);
                            _logger.LogDebug($"设置分支类型: {branchType}");

                            // 添加到父节点
                            ifNode.Children.Add(childNode);
                            _logger.LogDebug($"添加到父节点Children，当前总数: {ifNode.Children.Count}");
                        }
                    }
                }

                _logger.LogDebug($"数据恢复完成，Children总数: {ifNode.Children.Count}");

                // 延迟触发IF节点UI同步
                Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
                {
                    TriggerIfNodeUISync(ifNode);
                }), DispatcherPriority.Loaded);

            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "恢复IF节点子节点失败");
            }
        }
        /// <summary>
        /// 触发IF节点UI同步
        /// </summary>
        private void TriggerIfNodeUISync(CommandNode ifNode)
        {
            try
            {
                _logger.LogDebug($"=== 触发IF节点UI同步: {ifNode.DisplayName} ===");

                if (ifNode.NodeControl is Views.IfNodeControlView ifControl)
                {
                    _logger.LogDebug("找到IfNodeControlView，开始同步UI");

                    try
                    {

                      ifControl.SyncChildrenToUI();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "IF节点UI同步失败");
                    }
                }
                else
                {
                    _logger.LogError("NodeControl 不是 IfNodeControlView 类型");
                    if (ifNode.NodeControl != null)
                    {
                        _logger.LogError($"实际类型: {ifNode.NodeControl.GetType().Name}");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "触发IF节点UI同步失败");
            }
        }

        /// <summary>
        /// 恢复循环节点的子节点
        /// </summary>
        private void RestoreLoopNodeChildren(CommandNode loopNode, List<FlowCommandNodeData> childDataList)
        {
            try
            {
                _logger.LogDebug($"恢复循环节点 {loopNode.DisplayName} 的子节点");

                // 按LoopChildIndex排序
                var sortedChildren = childDataList
                    .OrderBy(child => GetLoopChildIndex(child))
                    .ToList();

                foreach (var childData in sortedChildren)
                {
                    var childNode = RestoreChildCommandNode(childData);
                    if (childNode != null)
                    {
                        loopNode.Children.Add(childNode);
                        _logger.LogDebug($"恢复循环子节点 {childNode.DisplayName}");
                    }
                }
               
                _logger.LogDebug($"循环节点子节点恢复完成，共 {loopNode.Children.Count} 个");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "恢复循环节点子节点失败");
            }
        }
       
        /// <summary>
        /// 恢复顺序子节点
        /// </summary>
        private void RestoreSequentialChildren(CommandNode parentNode, List<FlowCommandNodeData> childDataList)
        {
            try
            {
                foreach (var childData in childDataList)
                {
                    var childNode = RestoreChildCommandNode(childData);
                    if (childNode != null)
                    {
                        parentNode.Children.Add(childNode);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "恢复顺序子节点失败");
            }
        }

        /// <summary>
        /// 获取分支索引
        /// </summary>
        private int GetBranchIndex(FlowCommandNodeData nodeData)
        {
            try
            {
                if (nodeData.ExecutionProperties != null &&
                    nodeData.ExecutionProperties.ContainsKey("BranchIndex"))
                {
                    var branchIndexValue = nodeData.ExecutionProperties["BranchIndex"];
                    if (branchIndexValue != null && int.TryParse(branchIndexValue.ToString(), out int branchIndex))
                    {
                        return branchIndex;
                    }
                }
                return 0; // 默认分支0
            }
            catch
            {
                return 0;
            }
        }


        /// <summary>
        /// 获取分支类型
        /// </summary>
        private string GetBranchType(FlowCommandNodeData nodeData)
        {
            try
            {
                if (nodeData.ExecutionProperties != null &&
                    nodeData.ExecutionProperties.ContainsKey("BranchType"))
                {
                    var branchTypeValue = nodeData.ExecutionProperties["BranchType"];
                    return branchTypeValue?.ToString() ?? "True";
                }
                return "True"; // 默认True分支
            }
            catch
            {
                return "True";
            }
        }

        /// <summary>
        /// 获取循环子节点索引
        /// </summary>
        private int GetLoopChildIndex(FlowCommandNodeData nodeData)
        {
            try
            {
                if (nodeData.ExecutionProperties != null &&
                    nodeData.ExecutionProperties.ContainsKey("LoopChildIndex"))
                {
                    var indexValue = nodeData.ExecutionProperties["LoopChildIndex"];
                    if (indexValue != null && int.TryParse(indexValue.ToString(), out int index))
                    {
                        return index;
                    }
                }
                return 0;
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>
        /// 将子节点添加到并行分支
        /// </summary>
        private void AddChildToBranch(CommandNode parentNode, CommandNode childNode, int branchIndex)
        {
            try
            {
                childNode.SetExecutionProperty("BranchIndex", branchIndex);
                parentNode.Children.Add(childNode);

                _logger.LogDebug($"成功将节点 {childNode.DisplayName} 添加到并行分支 {branchIndex}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "添加子节点到并行分支失败");
                parentNode.Children.Add(childNode); // fallback
            }
        }

        /// <summary>
        /// 将子节点添加到IF分支
        /// </summary>
        private void AddChildToIfBranch(CommandNode ifNode, CommandNode childNode, string branchType)
        {
            try
            {
                // 设置分支类型
                childNode.SetExecutionProperty("BranchType", branchType);

                // 如果有IF控件，添加到相应的分支容器
                if (ifNode.NodeControl is Views.IfNodeControlView ifControl)
                {
                    AddToIfControlBranch(ifControl, childNode, branchType);
                }

                // 添加到Children
                ifNode.Children.Add(childNode);

                _logger.LogDebug($"成功将节点 {childNode.DisplayName} 添加到IF {branchType} 分支");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "添加子节点到IF分支失败");
                ifNode.Children.Add(childNode); // fallback
            }
        }

        /// <summary>
        /// 添加到分支管理
        /// </summary>
        private void AddToBranchManagement(CommandNode parentNode, CommandNode childNode, int branchIndex)
        {
            try
            {
                var branchesProperty = parentNode.GetType().GetProperty("Branches");
                if (branchesProperty?.GetValue(parentNode) is System.Collections.IList branches)
                {
                    // 确保分支存在
                    while (branches.Count <= branchIndex)
                    {
                        var newBranch = new List<CommandNode>();
                        branches.Add(newBranch);
                    }

                    // 修正类型检查
                    if (branches[branchIndex] is IList<CommandNode> branchChildren)
                    {
                        branchChildren.Add(childNode);
                    }
                    else if (branches[branchIndex] is System.Collections.IList genericBranchList)
                    {
                        genericBranchList.Add(childNode);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "添加到分支管理失败");
            }
        }

        /// <summary>
        /// 添加到IF控件分支
        /// </summary>
        private void AddToIfControlBranch(Views.IfNodeControlView ifControl, CommandNode childNode, string branchType)
        {
            try
            {
                // 这里需要根据你的IfNodeControlView具体实现
                // 例如：
                /*
                if (branchType == "True" && ifControl.TrueBranchContainer != null)
                {
                    ifControl.TrueBranchContainer.Children.Add(childNode.NodeControl);
                }
                else if (branchType == "False" && ifControl.FalseBranchContainer != null)
                {
                    ifControl.FalseBranchContainer.Children.Add(childNode.NodeControl);
                }
                */

                _logger.LogDebug($"将节点控件添加到IF控件的 {branchType} 分支");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "添加到IF控件分支失败");
            }
        }
       

        private CommandNode RestoreChildCommandNode(FlowCommandNodeData nodeData)
        {
            try
            {
                CommandNode childNode = null;

                if (nodeData.CommandType == "DeviceCommand")
                {
                    var deviceCommand = RestoreDeviceCommandFromNodeData(nodeData);
                    if (deviceCommand != null)
                    {
                        childNode = new CommandNode(deviceCommand);
                    }
                }
                else if (nodeData.CommandType == "LogicCommand")
                {
                    var logicCommand = RestoreLogicCommandFromNodeData(nodeData);
                    if (logicCommand != null)
                    {
                        childNode = new CommandNode(logicCommand);

                        // 为逻辑命令创建节点控件
                        var plugin = _pluginManager?.GetPlugin(logicCommand.CommandType.ToString());
                        if (plugin != null)
                        {
                            childNode.NodeControl = plugin.CreateNodeControl(childNode);
                            SubscribeToNodeControlEvents(childNode, plugin);
                        }
                    }
                }

                if (childNode == null) return null;

                // 恢复基本属性
                RestoreNodeBasicProperties(childNode, nodeData);
                RestoreNodeDeviceBinding(childNode, nodeData);
                RestoreNodeParameterOverrides(childNode, nodeData);
                RestoreNodeExecutionProperties(childNode, nodeData);
                RestoreNodeExecutionStatus(childNode, nodeData);

                // 递归恢复子节点的子节点
                RestoreChildNodes(childNode, nodeData.Children);

                // 恢复节点控件属性
                RestoreNodeControlProperties(childNode, nodeData.NodeControlProperties);

                _logger.LogDebug($"子节点 {nodeData.DisplayName} 恢复成功");
                return childNode;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "恢复子节点失败: {NodeName}", nodeData.DisplayName);
                return null;
            }
        }


        private void RestoreNodeControlProperties(CommandNode commandNode, Dictionary<string, object> properties)
        {
            if (commandNode.NodeControl == null || properties == null || !properties.Any()) return;

            try
            {
                // 根据不同的节点控件类型恢复不同的属性
                switch (commandNode.NodeControl)
                {
                    case Views.IfNodeControlView ifControl:
                        RestoreIfNodeProperties(ifControl, properties);
                        break;
                    case Views.LoopNodeControlView loopControl:
                        RestoreLoopNodeProperties(loopControl, properties);
                        break;
                    case Views.ParallelNodeControlView parallelControl:
                        RestoreParallelNodeProperties(parallelControl, properties);
                        break;
                    case Views.WaitNodeControlView waitControl:
                        RestoreWaitNodeProperties(waitControl, properties);
                        break;
                    case Views.SetVariableNodeControlView setVarControl:
                        RestoreSetVariableNodeProperties(setVarControl, properties);
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "恢复节点控件属性失败: {NodeName}", commandNode.DisplayName);
            }
        }

        private void RestoreIfNodeProperties(Views.IfNodeControlView ifControl, Dictionary<string, object> properties)
        {
            try
            {
                if (ifControl.DataContext is CommandNode node && node.LogicCommand != null)
                {
                    foreach (var prop in properties.Where(p => p.Key.StartsWith("If_")))
                    {
                        var key = prop.Key.Substring(3); // 移除"If_"前缀
                        node.LogicCommand.Properties[key] = prop.Value;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "恢复IF节点属性失败");
            }
        }

        private void RestoreLoopNodeProperties(Views.LoopNodeControlView loopControl, Dictionary<string, object> properties)
        {
            try
            {
                if (loopControl.DataContext is CommandNode node && node.LogicCommand != null)
                {
                    foreach (var prop in properties.Where(p => p.Key.StartsWith("Loop_")))
                    {
                        var key = prop.Key.Substring(5); // 移除"Loop_"前缀
                        node.LogicCommand.Properties[key] = prop.Value;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "恢复循环节点属性失败");
            }
        }

        private void RestoreParallelNodeProperties(Views.ParallelNodeControlView parallelControl, Dictionary<string, object> properties)
        {
            try
            {
                if (parallelControl.DataContext is CommandNode node && node.LogicCommand != null)
                {
                    foreach (var prop in properties.Where(p => p.Key.StartsWith("Parallel_")))
                    {
                        var key = prop.Key.Substring(9); // 移除"Parallel_"前缀
                        node.LogicCommand.Properties[key] = prop.Value;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "恢复并行节点属性失败");
            }
        }

        private void RestoreWaitNodeProperties(Views.WaitNodeControlView waitControl, Dictionary<string, object> properties)
        {
            try
            {
                if (waitControl.DataContext is CommandNode node && node.LogicCommand != null)
                {
                    foreach (var prop in properties.Where(p => p.Key.StartsWith("Wait_")))
                    {
                        var key = prop.Key.Substring(5); // 移除"Wait_"前缀
                        node.LogicCommand.Properties[key] = prop.Value;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "恢复等待节点属性失败");
            }
        }

        private void RestoreSetVariableNodeProperties(Views.SetVariableNodeControlView setVarControl, Dictionary<string, object> properties)
        {
            try
            {
                if (setVarControl.DataContext is CommandNode node && node.LogicCommand != null)
                {
                    foreach (var prop in properties.Where(p => p.Key.StartsWith("SetVariable_")))
                    {
                        var key = prop.Key.Substring(12); // 移除"SetVariable_"前缀
                        node.LogicCommand.Properties[key] = prop.Value;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "恢复设置变量节点属性失败");
            }
        }

        /// <summary>
        /// 恢复连接
        /// </summary>
        private void RestoreConnections(List<FlowConnectionData> connectionDataList)
        {
            if (connectionDataList == null || !connectionDataList.Any())
            {
                _logger.LogDebug("没有连接需要恢复");
                return;
            }

            _logger.LogDebug($"开始恢复 {connectionDataList.Count} 个连接");

            foreach (var connectionData in connectionDataList)
            {
                try
                {
                    var connection = new NodeConnection
                    {
                        FromNodeId = connectionData.FromNodeId,
                        ToNodeId = connectionData.ToNodeId,
                        X = connectionData.X,
                        Y = connectionData.Y,
                        LineLength = connectionData.LineLength,
                        ArrowTop = connectionData.ArrowTop,
                        TotalHeight = connectionData.TotalHeight
                    };

                    Connections.Add(connection);
                    _logger.LogDebug($"连接恢复成功: {connectionData.FromNodeId} -> {connectionData.ToNodeId}");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "恢复连接失败: {FromId} -> {ToId}",
                        connectionData.FromNodeId, connectionData.ToNodeId);
                }
            }

            _logger.LogDebug($"连接恢复完成，当前连接数量: {Connections?.Count ?? 0}");
        }

        /// <summary>
        /// 流程加载完成事件处理
        /// </summary>
        private void OnFlowLoaded(FlowDocument flowDocument)
        {
            try
            {
                _logger.LogDebug($"流程加载完成: {flowDocument.Name}");
                _logger.LogDebug($"最终节点数量: {CommandNodes?.Count ?? 0}");
                _logger.LogDebug($"最终连接数量: {Connections?.Count ?? 0}");

                // 重建连接以确保位置正确
                RebuildAllConnections();

                // 更新画布大小
                UpdateCanvasSize();

                _logger.LogDebug("流程加载处理完成");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "流程加载完成处理失败");
            }
        }

        #endregion
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
                    _logger.LogError("插件ID为空，无法创建LogicCommand");
                    return null;
                }

                var plugin = _pluginManager?.GetPlugin(pluginId);
                if (plugin == null)
                {
                    _logger.LogError("未找到插件: {PluginId}", pluginId);
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
                        _logger.LogError("计算出的X位置为负数: {X}", nodeX);
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
                        _logger.LogError("未找到变化的节点");
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
            try
            {
                if (clickedNode == null) return;

                _nodeManagementService.ClearAllSelections(this);
                clickedNode.IsSelected = true;
                SelectedNode = clickedNode;

                // 原有逻辑
                //_nodeManagementService.HandleNodeClick(clickedNode, this);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "处理节点点击失败: {NodeName}", clickedNode?.DisplayName);
            }
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

        //构造候选列表（筛选支持命令 & 未被其它节点占用）
        public List<DeviceOption> GetBindingCandidatesForNode(CommandNode node)
        {
            try
            {
                if (node?.DeviceCommand == null) return new List<DeviceOption>();

                // 1) 请求画布设备（通过回调拿到结果）
                List<CanvasDeviceViewModel> canvasDevices = null;
                _eventAggregator?.GetEvent<RequestCanvasDevicesEvent>()?.Publish(result =>
                {
                    canvasDevices = result ?? new List<CanvasDeviceViewModel>();
                });

                // 2) 支持该命令
                var supports = (canvasDevices ?? new List<CanvasDeviceViewModel>())
                    .Where(d => d?.Device?.Commands?.Any(c => c.Name == node.DeviceCommand.Name) == true)
                    .ToList();

                //// 3) 剔除已被其它节点占用的设备（允许当前节点保留自己的绑定）
                //var usedIds = CommandNodes
                //    .Where(n => n != node && !string.IsNullOrEmpty(n.BoundDeviceId))
                //    .Select(n => n.BoundDeviceId)
                //    .ToHashSet();

                //var candidates = supports.Where(d => !usedIds.Contains(d.DeviceId)).ToList();

                // 4) 请求显示名映射
                Dictionary<string, string> displayNames = null;
                _eventAggregator?.GetEvent<GetDeviceDisplayNamesRequestEvent>()?.Publish(map =>
                {
                    displayNames = map ?? new Dictionary<string, string>();
                });

                // 5) 组装 DeviceOption 列表
                return supports.Select(d =>
                {
                    var display = (displayNames != null && displayNames.TryGetValue(d.DeviceId, out var dn))
                        ? dn
                        : (d.Device?.Name ?? d.DeviceId);
                    return new DeviceOption { Id = d.DeviceId, Display = display };
                }).ToList();
            }
            catch
            {
                return new List<DeviceOption>();
            }
        }

        public void RefreshDeviceOptionsForSelectedNode(CommandNode node)
        {
            var list = GetBindingCandidatesForNode(node) ?? new List<DeviceOption>();

            // 确保当前已绑定设备在列表中（避免 ComboBox 丢选择）
            if (!string.IsNullOrEmpty(node.BoundDeviceId) && !list.Any(o => o.Id == node.BoundDeviceId))
            {
                var display = string.IsNullOrEmpty(node.BoundDeviceName) ? node.BoundDeviceId : node.BoundDeviceName;
                list.Insert(0, new DeviceOption { Id = node.BoundDeviceId, Display = display });
            }

            DeviceOptionsForSelectedNode = new ObservableCollection<DeviceOption>(list);
        }
        #endregion
    }
}
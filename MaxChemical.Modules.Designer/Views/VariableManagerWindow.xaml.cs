using DevicePlugins.Devices;
using MaxChemical.Data.Models;
using MaxChemical.Data.Services;
using MaxChemical.Infrastructure.Events;
using MaxChemical.Infrastructure.Services;
using MaxChemical.Logging;
using MaxChemical.Modules.Designer.Event;
using MaxChemical.Modules.Designer.Models;
using Prism.Events;
using Prism.Ioc;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace MaxChemical.Modules.Designer.Views
{
    public partial class VariableManagerWindow : Window
    {
        private ObservableCollection<VariableViewModel> _allVariables;
        private ICollectionView _variablesView;
        private Dictionary<string, VariableViewModel> _variableMap;
        private Dictionary<string, string> _originalVariableNames; // 跟踪变量名更改
        private IEnumerable<CommandNode> _allNodes;
        private IEventAggregator _eventAggregator;
        private ICustomVariablePersistenceService _persistenceService;
        private SubscriptionToken _updateToken;
        private SubscriptionToken _variableUpdateToken;
        private bool _isSubscribed = false;
        private bool _isWindowActive = false;
        private bool _customVariableOnlyMode = false;
        private ILogService _logger;

        // 用于标记需要保存的变量
        private readonly HashSet<VariableViewModel> _pendingSaveVariables = new HashSet<VariableViewModel>();
        private readonly object _pendingSaveLock = new object();
        private System.Threading.Timer _saveTimer;

        // 使用静态集合来保持自定义变量的持久性（作为数据库的缓存）
        private static ObservableCollection<VariableViewModel> _staticCustomVariables = new ObservableCollection<VariableViewModel>();
        private static readonly object _staticVariablesLock = new object();

        // 编辑状态标志和挂起Refresh标志
        private bool _isEditing = false;
        private bool _pendingRefresh = false;

        public VariableViewModel SelectedVariable { get; private set; }
        public bool IsClosed { get; private set; } = false;

        /// <summary>
        /// 新增：搜索框水印
        /// </summary>
        public string SearchMark { get; set; }

        public string LocalizationTitle { get; set; }

        public string VariableCount { get; set; }


        public VariableManagerWindow(IEventAggregator eventAggregator, IEnumerable<CommandNode> allNodes = null,
            bool customVariableOnlyMode = false, ICustomVariablePersistenceService persistenceService = null)
        {
            InitializeComponent();

            _allVariables = new ObservableCollection<VariableViewModel>();
            _variableMap = new Dictionary<string, VariableViewModel>();
            _originalVariableNames = new Dictionary<string, string>();
            _allNodes = allNodes;
            _eventAggregator = eventAggregator ?? throw new ArgumentNullException(nameof(eventAggregator));
            _persistenceService = persistenceService ?? ContainerLocator.Container.Resolve<ICustomVariablePersistenceService>();
            _customVariableOnlyMode = customVariableOnlyMode;
            _logger = ContainerLocator.Container.Resolve<ILogService>();

            // 初始化保存定时器
            _saveTimer = new System.Threading.Timer(OnSaveTimerElapsed, null, Timeout.Infinite, Timeout.Infinite);

            // 如果是自定义变量模式，默认选中筛选框并隐藏它
            if (_customVariableOnlyMode)
            {
                ShowCustomOnlyCheckBox.IsChecked = true;
                ShowCustomOnlyCheckBox.IsEnabled = false;
                ShowCustomOnlyCheckBox.Visibility = Visibility.Collapsed;

                this.Title = "选择自定义变量";
                //TitleTextBlock.Text = "选择自定义变量";
            }

            LoadVariables(allNodes);

            _variablesView = CollectionViewSource.GetDefaultView(_allVariables);
            _variablesView.Filter = FilterVariables;
            VariablesDataGrid.ItemsSource = _variablesView;

            // 监听DataGrid编辑事件
            VariablesDataGrid.BeginningEdit += VariablesDataGrid_BeginningEdit;
            VariablesDataGrid.CellEditEnding += VariablesDataGrid_CellEditEnding;
            VariablesDataGrid.RowEditEnding += VariablesDataGrid_RowEditEnding;

            Loaded += VariableManagerWindow_Loaded;
            Unloaded += VariableManagerWindow_Unloaded;

            // 订阅变量属性更改事件
            foreach (var variable in _allVariables)
            {
                if (variable.IsCustomVariable)
                {
                    variable.PropertyChanged += Variable_PropertyChanged;
                }
            }
            // 新增：本地化
            OnLanguageChanged("");
        }

        // 编辑开始时设置标志
        private void VariablesDataGrid_BeginningEdit(object sender, DataGridBeginningEditEventArgs e)
        {
            _isEditing = true;
            _logger?.LogDebug("DataGrid开始编辑");
        }

        // 单元格编辑结束时检查并更新视图
        private void VariablesDataGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
        {
            if (e.EditAction == DataGridEditAction.Commit)
            {
                _isEditing = false;
                if (_pendingRefresh)
                {
                    UpdateViewWithDefer();
                    _pendingRefresh = false;
                    _logger?.LogDebug("执行挂起的视图更新 (CellEditEnding)");
                }
            }
            if (e.Row.Item is VariableViewModel variable && variable.IsCustomVariable)
            {
                _logger?.LogDebug("单元格编辑结束: {VariableName}", variable.VariableName);
            }
        }

        // 行编辑结束时检查并更新视图
        private void VariablesDataGrid_RowEditEnding(object sender, DataGridRowEditEndingEventArgs e)
        {
            if (e.EditAction == DataGridEditAction.Commit)
            {
                _isEditing = false;
                if (_pendingRefresh)
                {
                    UpdateViewWithDefer();
                    _pendingRefresh = false;
                    _logger?.LogDebug("执行挂起的视图更新 (RowEditEnding)");
                }
            }
            if (e.Row.Item is VariableViewModel variable && variable.IsCustomVariable)
            {
                _logger?.LogDebug("行编辑结束: {VariableName}", variable.VariableName);
            }
        }

        private async void VariableManagerWindow_Loaded(object sender, RoutedEventArgs e)
        {
            _isWindowActive = true;
            SubscribeToEvents();

            // 异步加载数据库中的自定义变量
            await LoadPersistedCustomVariablesAsync();

            Dispatcher.BeginInvoke(new Action(() =>
            {
                RefreshVariableValues();
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        private async Task LoadPersistedCustomVariablesAsync()
        {
            try
            {
                _logger?.LogDebug("开始从数据库加载自定义变量...");

                var persistedVariables = await _persistenceService.LoadCustomVariablesAsync();

                if (persistedVariables?.Count > 0)
                {
                    _logger?.LogDebug("从数据库加载到 {Count} 个自定义变量", persistedVariables.Count);

                    // 更新静态缓存
                    lock (_staticVariablesLock)
                    {
                        _staticCustomVariables.Clear();
                        foreach (var persistedVar in persistedVariables)
                        {
                            var staticVar = new VariableViewModel
                            {
                                VariableName = persistedVar.Name,
                                VariableValue = persistedVar.Value,
                                VariableType = persistedVar.Type,
                                IsCustomVariable = true,
                                HasExecutionData = persistedVar.IsFromExecutionContext,
                                LastExecutionTime = persistedVar.LastModifiedTime,
                                Id = persistedVar.Id
                            };
                            _staticCustomVariables.Add(staticVar);
                        }
                    }

                    // 添加到当前变量列表（避免重复）
                    foreach (var persistedVar in persistedVariables)
                    {
                        var existingKey = $"custom.{persistedVar.Name}";
                        if (!_variableMap.ContainsKey(existingKey))
                        {
                            var variableViewModel = new VariableViewModel
                            {
                                VariableName = persistedVar.Name,
                                VariableValue = persistedVar.Value,
                                VariableType = persistedVar.Type,
                                IsCustomVariable = true,
                                HasExecutionData = persistedVar.IsFromExecutionContext,
                                LastExecutionTime = persistedVar.LastModifiedTime,
                                Id = persistedVar.Id
                            };

                            // 订阅属性更改事件
                            variableViewModel.PropertyChanged += Variable_PropertyChanged;

                            _allVariables.Add(variableViewModel);
                            _variableMap[existingKey] = variableViewModel;
                            _originalVariableNames[GetVariableInstanceKey(variableViewModel)] = persistedVar.Name;

                            _logger?.LogDebug("已加载数据库变量: {Name} = {Value}", persistedVar.Name, persistedVar.Value);
                        }
                    }

                    RequestViewUpdate();
                }
                else
                {
                    _logger?.LogDebug("数据库中没有自定义变量");
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "从数据库加载自定义变量失败");
                MessageBox.Show($"加载自定义变量失败：{ex.Message}", "错误",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void Variable_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (sender is VariableViewModel variable && variable.IsCustomVariable)
            {
                // 当自定义变量的属性发生更改时，标记为需要保存
                if (e.PropertyName == nameof(VariableViewModel.VariableName) ||
                    e.PropertyName == nameof(VariableViewModel.VariableValue) ||
                    e.PropertyName == nameof(VariableViewModel.VariableType))
                {
                    MarkVariableForSave(variable);
                    _logger?.LogDebug("变量属性更改: {Name}.{Property} = {Value}",
                        variable.VariableName, e.PropertyName,
                        e.PropertyName == nameof(VariableViewModel.VariableName) ? variable.VariableName :
                        e.PropertyName == nameof(VariableViewModel.VariableValue) ? variable.VariableValue :
                        variable.VariableType);

                    RequestViewUpdate();
                }
            }
        }

        private string GetVariableInstanceKey(VariableViewModel variable)
        {
            return variable.GetHashCode().ToString();
        }

        private void VariableManagerWindow_Unloaded(object sender, RoutedEventArgs e)
        {
            _isWindowActive = false;
            UnsubscribeFromEvents();

            // 清理定时器
            _saveTimer?.Dispose();

            // 取消订阅所有变量的属性更改事件
            foreach (var variable in _allVariables)
            {
                if (variable.IsCustomVariable)
                {
                    variable.PropertyChanged -= Variable_PropertyChanged;
                }
            }

            // 移除DataGrid事件监听
            VariablesDataGrid.BeginningEdit -= VariablesDataGrid_BeginningEdit;
            VariablesDataGrid.CellEditEnding -= VariablesDataGrid_CellEditEnding;
            VariablesDataGrid.RowEditEnding -= VariablesDataGrid_RowEditEnding;

            // 新添加：设为null防止后续调用
            _variablesView = null;
            _allVariables = null;
        }

        /// <summary>
        /// 本地化
        /// </summary>
        /// <param name="s"></param>
        private void OnLanguageChanged(string s)
        {
            ILocalizationService service = ContainerLocator.Container.Resolve<ILocalizationService>();
            if (service is null)
            {
                return;
            }

            TbNewVariable.Text = service.GetString("VarManage_New");
            BtnDeleteSelect.Content = service.GetString("VarManage_Delete");
            ShowCustomOnlyCheckBox.Content = service.GetString("VarManage_OnlyCustonVar");
            ColHeaderSource.Header = service.GetString("VarManage_Col_Source");
            ColHeaderCommand.Header = service.GetString("VarManage_Col_Command");
            ColHeaderFormat.Header = service.GetString("VarManage_Col_Format");
            ColHeaderType.Header = service.GetString("VarManage_Col_Type");
            ColHeaderValue.Header = service.GetString("VarManage_Col_Value");
            ColHeaderVarName.Header = service.GetString("VarManage_Col_Name");
            BtnOk.Content = service.GetString("VarManage_OK");
            BtnCancell.Content = service.GetString("VarManage_Cancel");
            SearchMark = service.GetString("VarManage_Watermark");
            LocalizationTitle = service.GetString(Title);
            VariableCount = service.GetString("VarManage_VarCount");
        }

        private void SubscribeToEvents()
        {
            try
            {
                UnsubscribeFromEvents();

                if (_eventAggregator != null && _isWindowActive)
                {
                    if (!_customVariableOnlyMode)
                    {
                        _updateToken = _eventAggregator.GetEvent<NodeExecutionResultUpdatedEvent>()
                            .Subscribe(OnNodeExecutionResultUpdated, ThreadOption.UIThread, true);
                    }

                    // 订阅变量更新事件
                    _variableUpdateToken = _eventAggregator.GetEvent<VariableUpdatedEvent>()
                        .Subscribe(OnVariableUpdated, ThreadOption.UIThread, true);

                    _isSubscribed = true;
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "订阅事件失败");
            }
        }

        private void UnsubscribeFromEvents()
        {
            try
            {
                if (_updateToken != null && _eventAggregator != null && _isSubscribed)
                {
                    _eventAggregator.GetEvent<NodeExecutionResultUpdatedEvent>().Unsubscribe(_updateToken);
                }

                if (_variableUpdateToken != null && _eventAggregator != null && _isSubscribed)
                {
                    _eventAggregator.GetEvent<VariableUpdatedEvent>().Unsubscribe(_variableUpdateToken);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "取消订阅事件失败");
            }
            finally
            {
                _updateToken = null;
                _variableUpdateToken = null;
                _isSubscribed = false;
            }
        }

        protected override void OnActivated(EventArgs e)
        {
            base.OnActivated(e);

            if (!_isSubscribed && _isWindowActive)
            {
                SubscribeToEvents();
            }

            RefreshVariableValues();
        }

        public void ForceRefresh()
        {
            try
            {
                if (!_isWindowActive) return;

                SubscribeToEvents();
                RefreshVariableValues();
                RequestViewUpdate();
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "强制刷新失败");
            }
        }

        public void RefreshData(IEnumerable<CommandNode> allNodes)
        {
            try
            {
                _allNodes = allNodes;
                LoadVariables(allNodes);
                RequestViewUpdate();

                if (!_isSubscribed && _isWindowActive)
                {
                    SubscribeToEvents();
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "刷新数据失败");
            }
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 1)
            {
                DragMove();
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void OnNodeExecutionResultUpdated(NodeExecutionResultEventArgs args)
        {
            try
            {
                if (!Dispatcher.CheckAccess())
                {
                    Dispatcher.BeginInvoke(new Action(() => OnNodeExecutionResultUpdated(args)));
                    return;
                }

                if (!_isWindowActive || !_isSubscribed) return;
                if (args?.OutputParameters?.Variables == null) return;

                bool hasUpdates = false;

                foreach (var param in args.OutputParameters.Variables)
                {
                    if (!string.IsNullOrEmpty(args.NodeId))
                    {
                        string exactKey = $"{args.DeviceName}.{args.CommandName}.{param.Name}.{args.NodeId}";
                        if (_variableMap.TryGetValue(exactKey, out var exactVariable))
                        {
                            UpdateVariableValue(exactVariable, param);
                            hasUpdates = true;
                            continue;
                        }
                    }

                    string key = $"{args.DeviceName}.{args.CommandName}.{param.Name}";
                    var matchingVariables = _variableMap.Where(kvp => kvp.Key.StartsWith(key)).ToList();

                    foreach (var kvp in matchingVariables)
                    {
                        UpdateVariableValue(kvp.Value, param);
                        hasUpdates = true;
                    }
                }

                if (hasUpdates)
                {
                    RequestViewUpdate();
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "处理节点执行结果更新失败");
            }
        }

        private async void OnVariableUpdated(VariableUpdatedEventArgs args)
        {
            try
            {
                if (!Dispatcher.CheckAccess())
                {
                    await Dispatcher.InvokeAsync(() => OnVariableUpdated(args));
                    return;
                }

                if (!_isWindowActive || !_isSubscribed) return;

                _logger?.LogDebug("UI处理变量更新事件: {Name} = {Value} ({Type})",
                    args.VariableName, args.VariableValue, args.VariableType);

                // 只处理UI更新，数据库保存由后台服务处理
                var customVariableKey = $"custom.{args.VariableName}";

                if (_variableMap.TryGetValue(customVariableKey, out var customVariable))
                {
                    // 更新现有变量UI
                    customVariable.VariableValue = args.VariableValue;
                    customVariable.VariableType = args.VariableType;
                    customVariable.LastExecutionTime = args.UpdateTime;
                    customVariable.HasExecutionData = true;

                    // 同时更新静态缓存
                    lock (_staticVariablesLock)
                    {
                        var staticVar = _staticCustomVariables.FirstOrDefault(v => v.VariableName == args.VariableName);
                        if (staticVar != null)
                        {
                            staticVar.VariableValue = args.VariableValue;
                            staticVar.VariableType = args.VariableType;
                            staticVar.LastExecutionTime = args.UpdateTime;
                            staticVar.HasExecutionData = true;
                        }
                    }
                }
                else
                {
                    // 创建新的UI变量
                    var newVariable = new VariableViewModel
                    {
                        VariableName = args.VariableName,
                        VariableValue = args.VariableValue,
                        VariableType = args.VariableType,
                        IsCustomVariable = true,
                        LastExecutionTime = args.UpdateTime,
                        HasExecutionData = true
                    };

                    // 订阅属性更改事件
                    newVariable.PropertyChanged += Variable_PropertyChanged;

                    _allVariables.Add(newVariable);
                    _variableMap[customVariableKey] = newVariable;
                    _originalVariableNames[GetVariableInstanceKey(newVariable)] = args.VariableName;

                    // 添加到静态缓存
                    lock (_staticVariablesLock)
                    {
                        var staticVar = new VariableViewModel
                        {
                            VariableName = args.VariableName,
                            VariableValue = args.VariableValue,
                            VariableType = args.VariableType,
                            IsCustomVariable = true,
                            LastExecutionTime = args.UpdateTime,
                            HasExecutionData = true
                        };
                        _staticCustomVariables.Add(staticVar);
                    }
                }

                // 更新UI视图
                RequestViewUpdate();

                _logger?.LogDebug("UI变量更新完成: {Name}", args.VariableName);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "UI处理变量更新失败");
            }
        }

    

        private void UpdateVariableValue(VariableViewModel variable, ParameterBase param)
        {
            variable.VariableValue = param.Value;

            if (param is NumberParameter numParam && param.Value is double d)
            {
                variable.VariableValue = Math.Round(d, numParam.DecimalPlaces);
            }
        }

        private void LoadVariables(IEnumerable<CommandNode> allNodes)
        {
            _allVariables.Clear();
            _variableMap.Clear();
            _originalVariableNames.Clear();

            // 如果是自定义变量模式，只加载自定义变量
            if (_customVariableOnlyMode)
            {
                LoadCustomVariables();
                LoadContextVariables();
                return;
            }

            // 非自定义变量模式的逻辑保持不变
            if (allNodes == null) return;

            var uniqueVariables = new Dictionary<string, (CommandNode Node, ParameterBase Param, DateTime? LastExecutionTime)>();
            var allNodesFlattened = GetAllNodesRecursive(allNodes);

            foreach (var node in allNodesFlattened)
            {
                if (node.DeviceCommand != null)
                {
                    string deviceName = node.DeviceName;
                    string commandName = node.CommandName;

                    DeviceParameters outputParams = null;
                    DateTime? lastExecutionTime = node.LastExecutionResult?.ExecutionTime;

                    if (node.LastOutputParameters?.Variables != null && node.LastOutputParameters.Variables.Count > 0)
                    {
                        outputParams = node.LastOutputParameters;
                    }
                    else if (node.DeviceCommand.OutputParameters?.Variables != null && node.DeviceCommand.OutputParameters.Variables.Count > 0)
                    {
                        outputParams = node.DeviceCommand.OutputParameters;
                        lastExecutionTime = null;
                    }

                    if (outputParams?.Variables != null)
                    {
                        foreach (var param in outputParams.Variables)
                        {
                            if (param.IsVisibleInVariableManager)
                            {
                                string uniqueKey = $"{deviceName}.{commandName}.{param.Name}";

                                if (uniqueVariables.TryGetValue(uniqueKey, out var existing))
                                {
                                    bool shouldReplace = false;

                                    if (lastExecutionTime.HasValue && !existing.LastExecutionTime.HasValue)
                                    {
                                        shouldReplace = true;
                                    }
                                    else if (lastExecutionTime.HasValue && existing.LastExecutionTime.HasValue)
                                    {
                                        shouldReplace = lastExecutionTime.Value > existing.LastExecutionTime.Value;
                                    }

                                    if (shouldReplace)
                                    {
                                        uniqueVariables[uniqueKey] = (node, param, lastExecutionTime);
                                    }
                                }
                                else
                                {
                                    uniqueVariables[uniqueKey] = (node, param, lastExecutionTime);
                                }
                            }
                        }
                    }
                }
            }

            foreach (var kvp in uniqueVariables)
            {
                var (node, param, lastExecutionTime) = kvp.Value;
                string deviceName = node.DeviceName;
                string commandName = node.CommandName;

                var variable = CreateVariableViewModel(deviceName, commandName, param, false, node.NodeId);

                variable.HasExecutionData = lastExecutionTime.HasValue;
                variable.LastExecutionTime = lastExecutionTime;

                _allVariables.Add(variable);

                string exactKey = $"{deviceName}.{commandName}.{param.Name}.{node.NodeId}";
                string compatKey = $"{deviceName}.{commandName}.{param.Name}";

                _variableMap[exactKey] = variable;
                if (!_variableMap.ContainsKey(compatKey))
                {
                    _variableMap[compatKey] = variable;
                }
            }

            LoadCustomVariables();
        }

        private void LoadContextVariables()
        {
            try
            {
                _logger?.LogDebug("开始加载上下文变量");

                // 通过事件请求当前的执行上下文变量
                Dictionary<string, object> contextVariables = null;

                _eventAggregator?.GetEvent<RequestContextVariablesEvent>()?.Publish(vars => contextVariables = vars);

                if (contextVariables != null && contextVariables.Count > 0)
                {
                    _logger?.LogDebug("收到上下文变量: {Count} 个", contextVariables.Count);

                    foreach (var kvp in contextVariables)
                    {
                        var variableName = kvp.Key;
                        var variableValue = kvp.Value;
                        var variableType = GetVariableTypeName(variableValue);

                        _logger?.LogDebug("处理上下文变量: {Name} = {Value} ({Type})", variableName, variableValue, variableType);

                        // 检查是否已经存在
                        var existingKey = $"custom.{variableName}";
                        if (_variableMap.ContainsKey(existingKey))
                        {
                            // 更新现有变量
                            var existingVar = _variableMap[existingKey];
                            existingVar.VariableValue = variableValue;
                            existingVar.VariableType = variableType;
                            existingVar.HasExecutionData = true;
                            existingVar.LastExecutionTime = DateTime.Now;
                            _logger?.LogDebug("更新现有变量: {Name}", variableName);
                        }
                        else
                        {
                            // 创建新变量
                            var contextVar = new VariableViewModel
                            {
                                VariableName = variableName,
                                VariableValue = variableValue,
                                VariableType = variableType,
                                IsCustomVariable = true,
                                HasExecutionData = true,
                                LastExecutionTime = DateTime.Now
                            };

                            // 订阅属性更改事件
                            contextVar.PropertyChanged += Variable_PropertyChanged;

                            _allVariables.Add(contextVar);
                            _variableMap[existingKey] = contextVar;
                            _originalVariableNames[GetVariableInstanceKey(contextVar)] = variableName;
                            _logger?.LogDebug("创建新的上下文变量: {Name}", variableName);
                        }
                    }
                }
                else
                {
                    _logger?.LogDebug("没有收到上下文变量或变量为空");
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "加载上下文变量失败");
            }
        }

        // 添加辅助方法来获取变量类型名称
        private string GetVariableTypeName(object value)
        {
            if (value == null) return "Object";

            return value.GetType().Name switch
            {
                "String" => "String",
                "Int32" => "Int32",
                "Double" => "Double",
                "Boolean" => "Boolean",
                "DateTime" => "DateTime",
                _ => value.GetType().Name
            };
        }

        private IEnumerable<CommandNode> GetAllNodesRecursive(IEnumerable<CommandNode> nodes)
        {
            if (nodes == null) yield break;

            foreach (var node in nodes)
            {
                yield return node;

                if (node.HasChildren)
                {
                    foreach (var child in GetAllNodesRecursive(node.Children))
                    {
                        yield return child;
                    }
                }
            }
        }

        private VariableViewModel CreateVariableViewModel(string deviceName, string commandName, ParameterBase param, bool isCustom, string nodeId = null)
        {
            string typeName = GetParameterTypeName(param);
            object value = param.Value;

            if (param is NumberParameter numParam && value is double d)
            {
                value = Math.Round(d, numParam.DecimalPlaces);
            }

            var variable = new VariableViewModel
            {
                DeviceName = deviceName,
                CommandName = commandName,
                VariableName = param.Name,
                VariableValue = value,
                VariableType = typeName,
                IsCustomVariable = isCustom,
                NodeId = nodeId
            };

            if (isCustom)
            {
                variable.PropertyChanged += Variable_PropertyChanged;
                _originalVariableNames[GetVariableInstanceKey(variable)] = param.Name;
            }

            return variable;
        }

        private string GetParameterTypeName(ParameterBase parameter)
        {
            return parameter switch
            {
                StringParameter => "String",
                NumberParameter => "Double",
                BooleanParameter => "Boolean",
                EnumParameter enumParam => enumParam.EnumType?.Name ?? "Enum",
                _ => parameter.Value?.GetType().Name ?? "Object"
            };
        }

        public void RefreshVariableValues()
        {
            if (_allNodes == null || !_isWindowActive) return;

            foreach (var node in _allNodes)
            {
                if (node.DeviceCommand != null && node.LastOutputParameters?.Variables != null)
                {
                    var deviceName = node.DeviceName;
                    var commandName = node.CommandName;

                    foreach (var param in node.LastOutputParameters.Variables)
                    {
                        string key = $"{deviceName}.{commandName}.{param.Name}";
                        if (_variableMap.TryGetValue(key, out var variable))
                        {
                            variable.VariableValue = param.Value;

                            if (param is NumberParameter numParam && param.Value is double d)
                            {
                                variable.VariableValue = Math.Round(d, numParam.DecimalPlaces);
                            }
                        }
                    }
                }
            }

            RequestViewUpdate();
        }

        private void LoadCustomVariables()
        {
            lock (_staticVariablesLock)
            {
                _logger?.LogDebug("加载静态自定义变量: {Count} 个", _staticCustomVariables.Count);

                // 将静态保存的自定义变量添加到当前变量列表
                foreach (var staticVar in _staticCustomVariables)
                {
                    // 创建副本以避免引用问题
                    var variableCopy = new VariableViewModel
                    {
                        VariableName = staticVar.VariableName,
                        VariableValue = staticVar.VariableValue,
                        VariableType = staticVar.VariableType,
                        IsCustomVariable = true,
                        HasExecutionData = staticVar.HasExecutionData,
                        LastExecutionTime = staticVar.LastExecutionTime,
                        Id = staticVar.Id
                    };

                    // 订阅属性更改事件
                    variableCopy.PropertyChanged += Variable_PropertyChanged;

                    _allVariables.Add(variableCopy);
                    _variableMap[$"custom.{variableCopy.VariableName}"] = variableCopy;
                    _originalVariableNames[GetVariableInstanceKey(variableCopy)] = staticVar.VariableName;

                    _logger?.LogDebug("已加载自定义变量: {Name} = {Value}", variableCopy.VariableName, variableCopy.VariableValue);
                }
            }

            // 从执行上下文加载变量
            LoadContextVariables();
        }

        private async void AddVariable_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // 生成唯一的变量名
                string uniqueName = await GenerateUniqueVariableNameAsync();

                var newVariable = new VariableViewModel
                {
                    VariableName = uniqueName,
                    VariableValue = "",
                    VariableType = "String",
                    IsCustomVariable = true
                };

                // 订阅属性更改事件
                newVariable.PropertyChanged += Variable_PropertyChanged;

                // 添加到界面
                _allVariables.Add(newVariable);
                _variableMap[$"custom.{newVariable.VariableName}"] = newVariable;
                _originalVariableNames[GetVariableInstanceKey(newVariable)] = uniqueName;

                // 添加到静态缓存
                lock (_staticVariablesLock)
                {
                    var staticVariable = new VariableViewModel
                    {
                        VariableName = newVariable.VariableName,
                        VariableValue = newVariable.VariableValue,
                        VariableType = newVariable.VariableType,
                        IsCustomVariable = true
                    };
                    _staticCustomVariables.Add(staticVariable);
                }

                // 立即保存到数据库并获取ID
                var variableData = new CustomVariableData
                {
                    Name = newVariable.VariableName,
                    Value = newVariable.VariableValue,
                    Type = newVariable.VariableType,
                    IsFromExecutionContext = false,
                    Category = "User",
                    Description = "用户创建的自定义变量",
                    CreatedTime = DateTime.Now,
                    LastModifiedTime = DateTime.Now
                };

                int newId = await _persistenceService.SaveCustomVariableAsync(variableData);
                newVariable.Id = newId; // 更新ViewModel的Id
                _logger?.LogDebug("新变量已保存到数据库，ID: {Id}, Name: {Name}", newId, uniqueName);

                // 延迟选中和编辑
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    RequestViewUpdate();
                    VariablesDataGrid.SelectedItem = newVariable;
                    VariablesDataGrid.ScrollIntoView(newVariable);

                    var row = VariablesDataGrid.ItemContainerGenerator.ContainerFromItem(newVariable) as DataGridRow;
                    if (row != null)
                    {
                        var cell = GetCell(VariablesDataGrid, row, 2); // 名称列
                        if (cell != null)
                        {
                            cell.Focus();
                            VariablesDataGrid.BeginEdit();
                        }
                    }
                }), System.Windows.Threading.DispatcherPriority.Loaded);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "添加变量失败");
                MessageBox.Show($"添加变量失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task<string> GenerateUniqueVariableNameAsync()
        {
            const string baseName = "Variable";

            // 先检查不带序号的名称是否可用
            if (!await IsVariableNameExistsAsync(baseName))
            {
                return baseName;
            }

            // 如果基础名称已存在，查找现有的最大序号
            int maxNumber = await GetMaxVariableNumberAsync(baseName);

            // 生成下一个可用的序号
            int nextNumber = maxNumber + 1;
            string uniqueName = $"{baseName}{nextNumber}";

            // 确保生成的名称是唯一的（防止并发问题）
            while (await IsVariableNameExistsAsync(uniqueName))
            {
                nextNumber++;
                uniqueName = $"{baseName}{nextNumber}";
            }

            _logger?.LogDebug("生成唯一变量名: {Name} (基于最大序号: {MaxNumber})", uniqueName, maxNumber);

            return uniqueName;
        }

        private async Task<bool> IsVariableNameExistsAsync(string variableName, VariableViewModel excludeVariable = null)
        {
            // 检查内存中的变量（排除指定的变量，忽略大小写）
            var memoryExists = _allVariables.Any(v =>
                v != excludeVariable &&
                v.IsCustomVariable &&
                string.Equals(v.VariableName, variableName, StringComparison.OrdinalIgnoreCase));

            if (memoryExists)
            {
                _logger?.LogDebug("变量名 {Name} 在内存中已存在", variableName);
                return true;
            }

            // 检查静态缓存（排除指定的变量，忽略大小写）
            lock (_staticVariablesLock)
            {
                var staticExists = _staticCustomVariables.Any(v =>
                    (excludeVariable == null || !string.Equals(v.VariableName, excludeVariable.VariableName, StringComparison.OrdinalIgnoreCase)) &&
                    string.Equals(v.VariableName, variableName, StringComparison.OrdinalIgnoreCase));
                if (staticExists)
                {
                    _logger?.LogDebug("变量名 {Name} 在静态缓存中已存在", variableName);
                    return true;
                }
            }

            // 检查数据库
            var dbExists = await _persistenceService.VariableExistsAsync(variableName);
            if (dbExists)
            {
                _logger?.LogDebug("变量名 {Name} 在数据库中已存在", variableName);
            }

            return dbExists;
        }

        private async Task<int> GetMaxVariableNumberAsync(string baseName)
        {
            int maxNumber = 0;

            try
            {
                // 从内存变量映射中查找
                var memoryNumbers = _variableMap.Keys
                    .Where(k => k.StartsWith("custom."))
                    .Select(k => k.Substring(7)) // 移除 "custom." 前缀
                    .Where(name => name.StartsWith(baseName))
                    .Select(name => ExtractNumberFromVariableName(name, baseName))
                    .Where(num => num > 0);

                if (memoryNumbers.Any())
                {
                    maxNumber = Math.Max(maxNumber, memoryNumbers.Max());
                }

                // 从静态缓存中查找
                lock (_staticVariablesLock)
                {
                    var staticNumbers = _staticCustomVariables
                        .Where(v => v.VariableName.StartsWith(baseName))
                        .Select(v => ExtractNumberFromVariableName(v.VariableName, baseName))
                        .Where(num => num > 0);

                    if (staticNumbers.Any())
                    {
                        maxNumber = Math.Max(maxNumber, staticNumbers.Max());
                    }
                }

                // 从数据库中查找
                var allVariables = await _persistenceService.LoadCustomVariablesAsync();
                if (allVariables != null && allVariables.Count > 0)
                {
                    var dbNumbers = allVariables
                        .Where(v => v.Name.StartsWith(baseName))
                        .Select(v => ExtractNumberFromVariableName(v.Name, baseName))
                        .Where(num => num > 0);

                    if (dbNumbers.Any())
                    {
                        maxNumber = Math.Max(maxNumber, dbNumbers.Max());
                    }
                }

                _logger?.LogDebug("找到变量 '{BaseName}' 的最大序号: {MaxNumber}", baseName, maxNumber);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "获取最大变量序号失败，使用默认值0");
            }

            return maxNumber;
        }

        private int ExtractNumberFromVariableName(string variableName, string baseName)
        {
            if (string.IsNullOrEmpty(variableName) || !variableName.StartsWith(baseName))
            {
                return 0;
            }

            // 如果变量名就是基础名称，没有序号
            if (variableName == baseName)
            {
                return 0;
            }

            // 提取序号部分
            string numberPart = variableName.Substring(baseName.Length);

            // 尝试解析为整数
            if (int.TryParse(numberPart, out int number) && number > 0)
            {
                return number;
            }

            return 0;
        }

        private void MarkVariableForSave(VariableViewModel variable)
        {
            lock (_pendingSaveLock)
            {
                _pendingSaveVariables.Add(variable);
            }

            // 重置定时器
            _saveTimer?.Change(1000, Timeout.Infinite); // 1秒后保存
        }

        private async void OnSaveTimerElapsed(object state)
        {
            try
            {
                List<VariableViewModel> toSave;
                lock (_pendingSaveLock)
                {
                    toSave = new List<VariableViewModel>(_pendingSaveVariables);
                    _pendingSaveVariables.Clear();
                }

                foreach (var variable in toSave)
                {
                    await SaveVariableChanges(variable);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "定时保存变量失败");
            }
        }

        private async Task SaveVariableChanges(VariableViewModel variable)
        {
            string originalName = null;
            try
            {
                _logger?.LogDebug("保存变量更改: {Name} = {Value} ({Type})",
                    variable.VariableName, variable.VariableValue, variable.VariableType);

                var instanceKey = GetVariableInstanceKey(variable);
                if (!_originalVariableNames.TryGetValue(instanceKey, out originalName))
                {
                    originalName = variable.VariableName;
                }

                // 如果变量名发生了更改
                if (originalName != variable.VariableName)
                {
                    _logger?.LogDebug("检测到变量名更改: {OldName} -> {NewName}", originalName, variable.VariableName);

                    // 检查新名称是否唯一（排除自身）
                    if (await IsVariableNameExistsAsync(variable.VariableName, variable))
                    {
                        _logger?.LogWarning("变量名 {Name} 已存在，恢复原名称 {OriginalName}", variable.VariableName, originalName);

                        Dispatcher.Invoke(() => {
                            variable.VariableName = originalName;
                        });

                        MessageBox.Show($"变量名 '{variable.VariableName}' 已存在，请使用其他名称。", "重复变量名",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }

                    // 处理数据库更新（重命名）
                    await HandleVariableRename(variable, originalName);

                    // 更新内存映射
                    UpdateVariableMapping(variable, originalName);

                    // 更新原始名称记录
                    _originalVariableNames[instanceKey] = variable.VariableName;
                }
                else
                {
                    // 变量名没有改变，只更新值和类型
                    await UpdateExistingVariable(variable);
                }

                // 更新静态缓存 (不更新LastExecutionTime)
                UpdateStaticCache(variable, originalName);

                _logger?.LogDebug("变量更改已保存: {Name}", variable.VariableName);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "保存变量更改失败: {Name}", variable.VariableName);

                // 如果是重命名失败，回滚UI名称
                if (originalName != null && originalName != variable.VariableName)
                {
                    Dispatcher.Invoke(() => {
                        variable.VariableName = originalName;
                    });
                    MessageBox.Show($"重命名失败，无法保存更改（可能数据库错误）。已恢复原名称 '{originalName}'。", "保存失败",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
                else
                {
                    MessageBox.Show("保存变量失败，请重试。", "保存失败",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private async Task HandleVariableRename(VariableViewModel variable, string originalName)
        {
            try
            {
                // 检查原名称是否在数据库中存在
                var originalVar = await _persistenceService.GetCustomVariableAsync(originalName);

                if (originalVar != null)
                {
                    // 物理删除原记录
                    bool deleteSuccess = await _persistenceService.DeleteCustomVariableAsync(originalName);
                    _logger?.LogDebug("尝试物理删除原变量记录: {OriginalName}, 结果: {Success}", originalName, deleteSuccess);

                    // 验证删除是否成功
                    if (await _persistenceService.VariableExistsAsync(originalName))
                    {
                        _logger?.LogError("物理删除原变量记录失败: {OriginalName} 仍然存在", originalName);
                        throw new InvalidOperationException($"无法删除原变量 '{originalName}'");
                    }
                    _logger?.LogDebug("原变量记录已删除: {OriginalName}", originalName);

                    // 创建新记录，使用当前ViewModel的值/类型
                    var renamedVar = new CustomVariableData
                    {
                        Name = variable.VariableName,
                        Value = variable.VariableValue, // 使用当前值 (e.g., 30)
                        Type = variable.VariableType, // 使用当前类型 (e.g., Int32)
                        IsFromExecutionContext = originalVar.IsFromExecutionContext,
                        Category = originalVar.Category,
                        Description = originalVar.Description ?? "用户编辑的自定义变量",
                        CreatedTime = originalVar.CreatedTime,
                        LastModifiedTime = DateTime.Now
                    };

                    int newId = await _persistenceService.SaveCustomVariableAsync(renamedVar);
                    variable.Id = newId; // 更新ViewModel的Id
                    _logger?.LogDebug("已创建重命名后的变量记录: {NewName}, Value: {Value}, Type: {Type}, ID: {Id}", variable.VariableName, variable.VariableValue, variable.VariableType, newId);
                }
                else
                {
                    // 如果原名称不存在，直接创建新记录
                    var variableData = new CustomVariableData
                    {
                        Name = variable.VariableName,
                        Value = variable.VariableValue,
                        Type = variable.VariableType,
                        IsFromExecutionContext = false,
                        Category = "User",
                        Description = "用户编辑的自定义变量",
                        CreatedTime = DateTime.Now,
                        LastModifiedTime = DateTime.Now
                    };

                    int newId = await _persistenceService.SaveCustomVariableAsync(variableData);
                    variable.Id = newId;
                    _logger?.LogDebug("已创建新的变量记录: {Name}, Value: {Value}, Type: {Type}, ID: {Id}", variable.VariableName, variable.VariableValue, variable.VariableType, newId);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "处理变量重命名失败: {OriginalName} -> {NewName}", originalName, variable.VariableName);
                throw;
            }
        }

        private async Task UpdateExistingVariable(VariableViewModel variable)
        {
            try
            {
                var variableData = new CustomVariableData
                {
                    Id = variable.Id, // 使用ViewModel的Id更新
                    Name = variable.VariableName,
                    Value = variable.VariableValue,
                    Type = variable.VariableType,
                    LastModifiedTime = DateTime.Now
                };

                bool success = await _persistenceService.UpdateCustomVariableAsync(variableData);
                if (success)
                {
                    _logger?.LogDebug("变量已在数据库中更新: {Name} = {Value} ({Type}), ID: {Id}", variable.VariableName, variable.VariableValue, variable.VariableType, variable.Id);
                }
                else
                {
                    _logger?.LogWarning("数据库更新失败: {Name} (ID: {Id})", variable.VariableName, variable.Id);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "更新现有变量失败: {Name} (ID: {Id})", variable.VariableName, variable.Id);
                throw;
            }
        }

        private void UpdateVariableMapping(VariableViewModel variable, string originalName)
        {
            // 移除旧的映射
            var oldKey = $"custom.{originalName}";
            if (_variableMap.ContainsKey(oldKey))
            {
                _variableMap.Remove(oldKey);
            }

            // 添加新的映射
            var newKey = $"custom.{variable.VariableName}";
            _variableMap[newKey] = variable;

            _logger?.LogDebug("更新变量映射: {OldKey} -> {NewKey}", oldKey, newKey);
        }

        private void UpdateStaticCache(VariableViewModel variable, string originalName)
        {
            lock (_staticVariablesLock)
            {
                // 查找并更新静态缓存中的变量
                var staticVar = _staticCustomVariables.FirstOrDefault(v => v.VariableName == originalName);
                if (staticVar != null)
                {
                    staticVar.VariableName = variable.VariableName;
                    staticVar.VariableValue = variable.VariableValue;
                    staticVar.VariableType = variable.VariableType;
                    // 移除这行：staticVar.LastExecutionTime = DateTime.Now;  // 不更新LastExecutionTime（用户编辑不视为执行）
                    staticVar.Id = variable.Id; // 更新Id

                    _logger?.LogDebug("更新静态缓存: {OldName} -> {Name} = {Value} ({Type}), ID: {Id}",
                        originalName, variable.VariableName, variable.VariableValue, variable.VariableType, variable.Id);
                }
                else
                {
                    // 如果没找到，添加新的
                    var newStaticVar = new VariableViewModel
                    {
                        VariableName = variable.VariableName,
                        VariableValue = variable.VariableValue,
                        VariableType = variable.VariableType,
                        IsCustomVariable = true,
                        // 移除这行：LastExecutionTime = DateTime.Now,  // 不设置（用户编辑不视为执行）
                        Id = variable.Id
                    };
                    _staticCustomVariables.Add(newStaticVar);

                    _logger?.LogDebug("添加到静态缓存: {Name} = {Value} ({Type}), ID: {Id}",
                        variable.VariableName, variable.VariableValue, variable.VariableType, variable.Id);
                }
            }
        }

        private DataGridCell GetCell(DataGrid dataGrid, DataGridRow row, int column)
        {
            if (row != null)
            {
                var presenter = GetVisualChild<DataGridCellsPresenter>(row);
                if (presenter != null)
                {
                    var cell = (DataGridCell)presenter.ItemContainerGenerator.ContainerFromIndex(column);
                    if (cell == null)
                    {
                        dataGrid.ScrollIntoView(row, dataGrid.Columns[column]);
                        cell = (DataGridCell)presenter.ItemContainerGenerator.ContainerFromIndex(column);
                    }
                    return cell;
                }
            }
            return null;
        }

        private T GetVisualChild<T>(Visual parent) where T : Visual
        {
            T child = default(T);
            int numVisuals = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < numVisuals; i++)
            {
                var v = (Visual)System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
                child = v as T ?? GetVisualChild<T>(v);
                if (child != null) break;
            }
            return child;
        }

        private async void DeleteSelected_Click(object sender, RoutedEventArgs e)
        {
            var selectedItems = VariablesDataGrid.SelectedItems.Cast<VariableViewModel>()
                .Where(v => v.IsCustomVariable).ToList();

            if (selectedItems.Count == 0)
            {
                MessageBox.Show("请选择要删除的自定义变量", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            try
            {
                foreach (var item in selectedItems)
                {
                    await DeleteVariable(item);
                }
                RequestViewUpdate();
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "批量删除变量失败");
                MessageBox.Show($"删除变量失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void DeleteRow_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.Tag is VariableViewModel variable && variable.IsCustomVariable)
            {
                try
                {
                    await DeleteVariable(variable);
                    RequestViewUpdate();
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "删除变量失败");
                    MessageBox.Show($"删除变量失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private async Task DeleteVariable(VariableViewModel variable)
        {
            // 取消订阅事件
            variable.PropertyChanged -= Variable_PropertyChanged;

            // 从当前列表移除
            _allVariables.Remove(variable);
            _variableMap.Remove($"custom.{variable.VariableName}");

            var instanceKey = GetVariableInstanceKey(variable);
            if (_originalVariableNames.ContainsKey(instanceKey))
            {
                _originalVariableNames.Remove(instanceKey);
            }

            // 从静态列表移除
            lock (_staticVariablesLock)
            {
                var staticVar = _staticCustomVariables.FirstOrDefault(v => v.VariableName == variable.VariableName);
                if (staticVar != null)
                {
                    _staticCustomVariables.Remove(staticVar);
                }
            }

            // 从数据库物理删除
            await _persistenceService.DeleteCustomVariableAsync(variable.VariableName);
            _logger?.LogDebug("变量已从数据库物理删除: {Name}", variable.VariableName);
        }

        private void DataGrid_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            // 支持F2键开始编辑
            if (e.Key == Key.F2 && VariablesDataGrid.SelectedItem is VariableViewModel variable && variable.IsCustomVariable)
            {
                VariablesDataGrid.BeginEdit();
                e.Handled = true;
            }
        }

        private bool FilterVariables(object item)
        {
            if (item is not VariableViewModel variable) return false;

            // 如果是自定义变量模式，只显示自定义变量
            if (_customVariableOnlyMode && !variable.IsCustomVariable)
                return false;

            if (ShowCustomOnlyCheckBox.IsChecked == true && !variable.IsCustomVariable)
                return false;

            if (!string.IsNullOrWhiteSpace(SearchBox.Text))
            {
                var searchText = SearchBox.Text.ToLower();
                return variable.DeviceName?.ToLower().Contains(searchText) == true ||
                       variable.CommandName?.ToLower().Contains(searchText) == true ||
                       variable.VariableName?.ToLower().Contains(searchText) == true;
            }

            return true;
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            RequestViewUpdate();
        }

        private void FilterChanged(object sender, RoutedEventArgs e)
        {
            RequestViewUpdate();
        }

        private void DataGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            var dataGrid = sender as DataGrid;
            if (dataGrid?.CurrentCell != null)
            {
                dataGrid.BeginEdit();
            }
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            if (VariablesDataGrid.SelectedItem is VariableViewModel variable)
            {
                SelectedVariable = variable;
                DialogResult = true;
                Close();
            }
            else
            {
                MessageBox.Show("请选择一个变量", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        protected override void OnClosed(EventArgs e)
        {
            IsClosed = true;
            _isWindowActive = false;
            UnsubscribeFromEvents();

            // 强制保存所有挂起的更改
            if (_pendingSaveVariables.Count > 0)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        List<VariableViewModel> toSave;
                        lock (_pendingSaveLock)
                        {
                            toSave = new List<VariableViewModel>(_pendingSaveVariables);
                            _pendingSaveVariables.Clear();
                        }

                        foreach (var variable in toSave)
                        {
                            await SaveVariableChanges(variable);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogError(ex, "关闭窗口时保存变量失败");
                    }
                });
            }

            base.OnClosed(e);
        }

        // 静态方法清空缓存（在应用程序重启或需要重新加载时使用）
        public static void ClearVariableCache()
        {
            lock (_staticVariablesLock)
            {
                _staticCustomVariables.Clear();
            }
        }

        // 新添加：请求视图更新的方法（使用DeferRefresh）
        private void RequestViewUpdate()
        {
            if (!_isWindowActive || _variablesView == null) return; // 安全检查

            if (_isEditing)
            {
                _pendingRefresh = true;
                _logger?.LogDebug("视图更新请求挂起 (正在编辑中)");
            }
            else
            {
                UpdateViewWithDefer();
                _pendingRefresh = false;
                _logger?.LogDebug("执行视图更新");
            }
        }

        // 新添加：使用DeferRefresh安全更新视图
        private void UpdateViewWithDefer()
        {
            if (_variablesView == null)
            {
                _logger?.LogWarning("无法执行视图更新: _variablesView 为 null（可能窗口已卸载）");
                return;
            }

            try
            {
                using (_variablesView.DeferRefresh())
                {
                    // 重新应用过滤器（这会批量更新视图，而不中断事务）
                    _variablesView.Filter = FilterVariables;
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "视图更新失败");
            }
        }
    }

    
}
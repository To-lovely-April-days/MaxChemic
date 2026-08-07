using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MaxChemical.Modules.Designer.Models;
using MaxChemical.Modules.Designer.ViewModels;
using MaxChemical.Logging;
using DevicePlugins.Devices;
using MaxChemical.Modules.Designer.Service.Execution;
using MaxChemical.Modules.Designer.Services.Execution;
using Prism.Events;
using MaxChemical.Modules.Designer.Event;
using System.Diagnostics;
using System.Collections.ObjectModel;
using System.Globalization;
using MaxChemical.Modules.Designer.Service;
using System.Windows;
using MaxChemical.Modules.Designer.Views;
using MaxChemical.Data.Services;
using MaxChemical.Core;
using System.Diagnostics.Tracing;

namespace MaxChemical.Modules.Designer.Services.Execution
{
    public class FlowExecutionEngine : IFlowExecutionEngine
    {
        private readonly ILogService _logger;
        private FlowExecutionState _state = FlowExecutionState.Idle;
        private CancellationTokenSource _cancellationTokenSource;
        // 是否由[结束]指令主动结束流程：用于把"停止"区分为"正常结束"而非"被用户停止"
        private volatile bool _endedByCommand = false;
        private readonly ManualResetEventSlim _pauseEvent = new ManualResetEventSlim(true);

        // 新增：用户交互暂停事件
        private readonly ManualResetEventSlim _userInteractionPauseEvent = new ManualResetEventSlim(true);
        private volatile bool _isWaitingForUserInteraction = false;

        // 新增：用户交互事件
        public event EventHandler<UserInteractionEventArgs> UserInteractionStarted;
        public event EventHandler<UserInteractionEventArgs> UserInteractionEnded;
        // 标记当前是否处于"后台异步任务"执行上下文。
        // 后台异步任务（如异步 Loop 采集泵）不应被用户交互暂停（BeginUserInteraction）冻结，
        // 但仍响应用户主动暂停（_pauseEvent）。
        // AsyncLocal 会沿 await 调用链自动流动，前台主流程读到 false，Task.Run 出去的后台链读到 true。
        private readonly AsyncLocal<bool> _isInBackgroundAsyncContext = new AsyncLocal<bool>();
        private FlowExecutionContext _executionContext;
        private readonly INodeExecutionStatusService _statusService;
        private readonly IEventAggregator _eventAggregator;
        private readonly IDeviceNameResolverService _deviceNameResolver;
        // 添加后台任务追踪
        private readonly List<Task> _backgroundTasks = new List<Task>();
        private readonly object _backgroundTasksLock = new object();
        private ICustomVariablePersistenceService _persistenceService;
        // *** 新增：模拟模式状态跟踪 ***
        private bool _currentSimulationMode = false;
        // 添加执行进度跟踪
        private class ExecutionProgress
        {
            private volatile bool _disposed = false;
            private readonly object _lock = new object();
            private int _totalNodes;
            private int _completedNodes;

            public int TotalNodes
            {
                get
                {
                    lock (_lock)
                    {
                        return _disposed ? 0 : _totalNodes;
                    }
                }
                set
                {
                    lock (_lock)
                    {
                        if (!_disposed)
                            _totalNodes = value;
                    }
                }
            }

            public int CompletedNodes
            {
                get
                {
                    lock (_lock)
                    {
                        return _disposed ? 0 : _completedNodes;
                    }
                }
            }

            public object Lock => _lock;

            public void IncrementCompleted()
            {
                lock (_lock)
                {
                    if (!_disposed)
                    {
                        _completedNodes++;
                    }
                }
            }

            public double GetPercentage()
            {
                lock (_lock)
                {
                    if (_disposed || _totalNodes <= 0)
                        return 0;
                    return (double)_completedNodes / _totalNodes * 100;
                }
            }

            public void AddTotalNodes(int count)
            {
                lock (_lock)
                {
                    if (!_disposed)
                    {
                        _totalNodes += count;
                    }
                }
            }

            public void Dispose()
            {
                lock (_lock)
                {
                    _disposed = true;
                }
            }
        }

        private ExecutionProgress _currentProgress;

        public FlowExecutionState State
        {
            get => _state;
            private set
            {
                var oldState = _state;
                _state = value;
                var eventArgs = new FlowExecutionStateChangedEventArgs
                {
                    OldState = oldState,
                    NewState = value
                };
                // 发布到本地事件
                StateChanged?.Invoke(this, eventArgs);

                // 发布到全局事件聚合器
                try
                {
                    _eventAggregator?.GetEvent<FlowExecutionStateChangedEvent>()?.Publish(eventArgs);
                    _logger.LogDebug("已发布流程状态变更事件: {OldState} -> {NewState}", oldState, value);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "发布流程状态变更事件失败");
                }
            }
        }

        public event EventHandler<FlowExecutionStateChangedEventArgs> StateChanged;
        public event EventHandler<NodeExecutionEventArgs> NodeExecuting;
        public event EventHandler<NodeExecutionEventArgs> NodeExecuted;
        public event EventHandler<FlowExecutionErrorEventArgs> ExecutionError;
        public event EventHandler<FlowExecutionProgressEventArgs> ProgressChanged;

        private readonly IExperimentDataAdapter _experimentDataAdapter; // 新增：实验数据适配器

        private string _currentProjectName = "未命名流程";
        private string? _currentExperimentId;


        public FlowExecutionEngine(INodeExecutionStatusService statusService, IEventAggregator eventAggregator,
            IDeviceNameResolverService deviceNameResolver, ICustomVariablePersistenceService persistenceService,
             IExperimentDataAdapter experimentDataAdapter)
        {
            _logger = _logger?.ForContext<FlowExecutionEngine>() ?? new LogService().ForContext<FlowExecutionEngine>();
            _statusService = statusService ?? throw new ArgumentNullException(nameof(statusService));
            _eventAggregator = eventAggregator ?? throw new ArgumentNullException(nameof(eventAggregator));
            _deviceNameResolver = deviceNameResolver ?? throw new ArgumentNullException(nameof(deviceNameResolver));
            _persistenceService= persistenceService ?? throw new ArgumentNullException(nameof(persistenceService));

            _experimentDataAdapter = experimentDataAdapter ?? throw new ArgumentNullException(nameof(experimentDataAdapter));
            // *** 新增：订阅模拟模式相关事件 ***
            SubscribeSimulationModeEvents();
            // 订阅上下文变量请求事件，用于变量管理器获取当前变量
            _eventAggregator.GetEvent<RequestContextVariablesEvent>().Subscribe(callback =>
            {
                try
                {
                    if (_executionContext?.Variables != null)
                    {
                        // 将 ConcurrentDictionary 转换为普通 Dictionary
                        var variables = _executionContext.Variables.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
                        _logger.LogDebug("提供上下文变量: {Count} 个变量", variables.Count);
                        foreach (var kvp in variables)
                        {
                            _logger.LogDebug("  {Key} = {Value}", kvp.Key, kvp.Value);
                        }
                        callback?.Invoke(variables);
                    }
                    else
                    {
                        _logger.LogDebug("执行上下文为空或没有变量");
                        callback?.Invoke(new Dictionary<string, object>());
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "获取上下文变量失败");
                    callback?.Invoke(new Dictionary<string, object>());
                }
            }, ThreadOption.PublisherThread, true);
            // 订阅项目名称更新事件
            _eventAggregator.GetEvent<ProjectNameUpdatedEvent>().Subscribe(OnProjectNameUpdated);
            //// 在应用程序启动时设置
            //DeviceConnectionConfig.CommunicationMode = CommunicationMode.PLC; // 启用PLC模式

            // 配置PLC参数
            //DeviceConnectionConfig.PLCSettings = new PLCConnectionSettings
            //{
            //    IpAddress = "192.168.2.190", // 您的PLC IP地址
            //    Rack = 0,
            //    Slot = 1,
            //    ConnectionType = "S7-300" // 或者您使用的PLC型号
            //};
            DeviceConnectionConfig.CommunicationMode = CommunicationMode.ModbusTcp;
            DeviceConnectionConfig.ModbusTcpSettings = new ModbusTcpConnectionSettings
            {
                IpAddress = "192.168.1.66",//192.168.2.190
                Port = 502,
                ConnectTimeout = 3000,
                ReadWriteTimeout = 1000,
                EnableReconnect = true,
                ReconnectInterval = 5000,
                MaxReconnectAttempts = 3
            };

        }
        #region 新增：模拟模式支持

        /// <summary>
        /// 订阅模拟模式相关事件
        /// </summary>
        private void SubscribeSimulationModeEvents()
        {
            try
            {
                // 订阅模拟模式状态请求事件
                _eventAggregator?.GetEvent<GetSimulationModeRequestEvent>()?.Subscribe(callback =>
                {
                    callback?.Invoke(_currentSimulationMode);
                }, ThreadOption.PublisherThread, true);

                // 订阅模拟模式变更事件
                _eventAggregator?.GetEvent<DeviceSimulationModeChangedEvent>()?.Subscribe(OnSimulationModeChanged,
                    ThreadOption.PublisherThread, true);

                _logger.LogDebug("已订阅模拟模式相关事件");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "订阅模拟模式事件失败");
            }
        }

        /// <summary>
        /// 处理模拟模式变更事件
        /// </summary>
        private void OnSimulationModeChanged(DeviceSimulationModeEventArgs args)
        {
            try
            {
                _currentSimulationMode = args.IsSimulationMode;
                _logger.LogInformation("流程执行引擎模拟模式已更新: {Mode}",
                    _currentSimulationMode ? "模拟模式" : "实际模式");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "处理模拟模式变更失败");
            }
        }

        /// <summary>
        /// 获取当前模拟模式状态
        /// </summary>
        private bool GetCurrentSimulationMode()
        {
            bool isSimulationMode = _currentSimulationMode;

            try
            {
                // 通过事件获取最新的模拟模式状态
                var resetEvent = new ManualResetEventSlim(false);
                bool latestMode = _currentSimulationMode;

                _eventAggregator?.GetEvent<GetSimulationModeRequestEvent>()?.Publish((mode) =>
                {
                    latestMode = mode;
                    resetEvent.Set();
                });

                // 等待500毫秒获取响应
                if (resetEvent.Wait(500))
                {
                    isSimulationMode = latestMode;
                    _currentSimulationMode = latestMode; // 同步本地状态
                }

                resetEvent.Dispose();

            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取模拟模式状态失败，使用缓存状态: {Mode}", _currentSimulationMode);
            }

            _logger.LogDebug("当前模拟模式状态: {Mode}", isSimulationMode ? "模拟模式" : "实际模式");
            return isSimulationMode;
        }

        /// <summary>
        /// 设置设备的模拟模式
        /// </summary>
        private void ApplySimulationModeToDevices(List<RequiredDeviceInfo> requiredDevices, bool isSimulationMode)
        {
            try
            {
                // 获取画布设备列表
                List<CanvasDeviceViewModel> canvasDevices = null;
                _eventAggregator?.GetEvent<RequestCanvasDevicesEvent>()?.Publish(list => canvasDevices = list);

                if (canvasDevices == null || !canvasDevices.Any())
                {
                    _logger.LogWarning("未找到画布设备，无法应用模拟模式");
                    return;
                }

                int updatedCount = 0;
                foreach (var deviceInfo in requiredDevices)
                {
                    var canvasDevice = canvasDevices.FirstOrDefault(d => d.DeviceId == deviceInfo.DeviceId);
                    if (canvasDevice?.Device != null)
                    {
                        try
                        {
                            canvasDevice.Device.IsSimulationMode = isSimulationMode;
                            updatedCount++;
                            _logger.LogDebug("设备模拟模式已更新: {DeviceId} -> {Mode}",
                                deviceInfo.DeviceId, isSimulationMode ? "模拟" : "实际");
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "更新设备模拟模式失败: {DeviceId}", deviceInfo.DeviceId);
                        }
                    }
                }

                _logger.LogInformation("已更新 {Count} 个设备的模拟模式为: {Mode}",
                    updatedCount, isSimulationMode ? "模拟模式" : "实际模式");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "批量应用设备模拟模式失败");
            }
        }

        #endregion

        /// <summary>
        /// 项目名称更新处理
        /// </summary>
        private void OnProjectNameUpdated(string projectName)
        {
            _currentProjectName = projectName ?? "未命名流程";
            _logger.LogDebug("项目名称已更新: {ProjectName}", _currentProjectName);
        }

        public async Task<FlowExecutionResult> ExecuteAsync(FlowDesignerViewModel flowViewModel, CancellationToken cancellationToken = default)
        {
            if (State != FlowExecutionState.Idle)
            {
                throw new InvalidOperationException("执行引擎正在运行中");
            }

            var startTime = DateTime.Now;
            _executionContext = new FlowExecutionContext();
            var persistedVariables = await _persistenceService.LoadCustomVariablesAsync();
           
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
                _executionContext.SetVariable(staticVar.VariableName, staticVar.VariableValue);
            }
            _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _endedByCommand = false;   // 每次执行开始重置[结束]指令标志

            // 清空后台任务列表
            lock (_backgroundTasksLock)
            {
                _backgroundTasks.Clear();
            }

            try
            {  // 初始化进度跟踪
                var totalNodes = CalculateTotalNodes(flowViewModel.CommandNodes);

                // *** 新增：更新实验进度 ***
                await _experimentDataAdapter.UpdateExperimentProgressAsync(_currentExperimentId, 0, totalNodes);
                _lastProgressDbWriteUtc = DateTime.UtcNow; // 节流基准随本次执行重置

                _currentProgress = new ExecutionProgress
                {
                    TotalNodes = totalNodes,
                   

                };

                _logger.LogInformation("流程包含总节点数: {TotalNodes} (含嵌套节点)", totalNodes);

                // *** 新增：开始实验记录 ***
                _currentExperimentId = await _experimentDataAdapter.StartExperimentAsync(
                     _currentProjectName,
                     _currentSimulationMode,
                    $"自动执行流程 - {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
                    Environment.UserName,
                    "自动执行");
                // *** 流程开始时通知所有设备 ***
                await NotifyFlowStartedAsync(flowViewModel, startTime);

                _statusService.SetCurrentFlowViewModel(flowViewModel);
                _statusService.ResetAllExecutionStatus();

                State = FlowExecutionState.Running;
                _logger.LogInformation("开始执行流程，共 {Count} 个顶层节点", flowViewModel.CommandNodes.Count);
                // 1. 流程结构验证
                var validation = ValidateFlow(flowViewModel);
                if (!validation.IsValid)
                {
                    throw new InvalidOperationException($"流程验证失败: {string.Join(", ", validation.Errors)}");
                }
                // 2. *** 新增：设备连接验证 ***
                _logger.LogInformation("开始验证流程中所有设备的连接状态...");
                var deviceValidation = await ValidateDeviceConnectionsAsync(flowViewModel, _cancellationTokenSource.Token);
                if (!deviceValidation.IsValid)
                {
                    throw new InvalidOperationException($"设备连接验证失败: {string.Join(", ", deviceValidation.Errors)}");
                }
                _logger.LogInformation("所有设备连接验证通过");
              
           



                // 执行所有顶层节点
                foreach (var node in flowViewModel.CommandNodes)
                {
                    _cancellationTokenSource.Token.ThrowIfCancellationRequested();
                    _pauseEvent.Wait(_cancellationTokenSource.Token);

                    if (State == FlowExecutionState.Stopping)
                        break;

                    await ExecuteNodeWithProgressAsync(node, _executionContext, _cancellationTokenSource.Token);
                }

                // 等待所有后台任务完成
                Task[] tasksToWait;
                lock (_backgroundTasksLock)
                {
                    tasksToWait = _backgroundTasks.ToArray();
                }

                if (tasksToWait.Length > 0)
                {
                    _logger.LogInformation("等待 {Count} 个后台并行任务完成...", tasksToWait.Length);

                    try
                    {
                        await Task.WhenAll(tasksToWait);
                        _logger.LogInformation("所有后台并行任务已完成");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "后台并行任务执行出错");
                        throw;
                    }
                }

                // 检查是否所有节点都执行完成
                if (_currentProgress.CompletedNodes < _currentProgress.TotalNodes && State != FlowExecutionState.Stopping)
                {
                    _logger.LogWarning("流程执行不完整: 预期 {Expected} 个节点，实际完成 {Actual} 个",
                        _currentProgress.TotalNodes, _currentProgress.CompletedNodes);
                }
                // *** 在执行完成前，最后一次确保进度同步 ***
                if (!string.IsNullOrEmpty(_currentExperimentId) && _currentProgress != null)
                {
                    await _experimentDataAdapter.UpdateExperimentProgressAsync(
                        _currentExperimentId,
                        _currentProgress.CompletedNodes,
                        _currentProgress.TotalNodes);

                    _logger.LogInformation("最终实验进度: {Completed}/{Total} ({Percentage:F1}%)",
                        _currentProgress.CompletedNodes, _currentProgress.TotalNodes, _currentProgress.GetPercentage());
                }
                State = FlowExecutionState.Completed;


                // *** 新增：完成实验记录 ***
                var finalProgress = _currentProgress; // 获取本地引用
                if (!string.IsNullOrEmpty(_currentExperimentId) && finalProgress != null)
                {
                    try
                    {
                        await _experimentDataAdapter.UpdateExperimentProgressAsync(
                            _currentExperimentId,
                            finalProgress.CompletedNodes,
                            finalProgress.TotalNodes);
                        // *** 添加这里：标记实验成功完成 ***
                        await _experimentDataAdapter.CompleteExperimentAsync(
                            _currentExperimentId,
                            true,  // 成功完成
                            "流程执行成功完成");
                        _logger.LogInformation("最终实验进度: {Completed}/{Total} ({Percentage:F1}%)",
                            finalProgress.CompletedNodes, finalProgress.TotalNodes, finalProgress.GetPercentage());
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "更新最终实验进度失败");
                    }
                }

                // *** 流程完成时通知所有设备 ***
                var completionContext = new FlowCompletionContext
                {
                    IsSuccessful = true,
                    FlowName ="流程名称",
                    CompletionTime = DateTime.Now,
                    Duration = DateTime.Now - startTime
                };
                await NotifyFlowCompletedAsync(flowViewModel, completionContext);

                _logger.LogInformation("流程执行完成，共执行 {Completed}/{Total} 个节点",
                    _currentProgress.CompletedNodes, _currentProgress.TotalNodes);

                //_ = Task.Delay(2000).ContinueWith(_ =>
                //{
                //    if (State == FlowExecutionState.Completed)
                //    {
                //        State = FlowExecutionState.Idle;
                //    }
                //});
                State = FlowExecutionState.Idle;
                return new FlowExecutionResult
                {
                    IsSuccessful = true,
                    Duration = DateTime.Now - startTime,
                    TotalNodesExecuted = _currentProgress.CompletedNodes,
                    StartTime = startTime,
                    EndTime = DateTime.Now
                };
            }
            catch (OperationCanceledException) when (State == FlowExecutionState.Stopping || _endedByCommand)
            {
                // 若停止是由[结束]指令主动触发的，视为流程正常结束（成功），而不是"被用户停止"
                if (_endedByCommand)
                {
                    if (!string.IsNullOrEmpty(_currentExperimentId))
                    {
                        await _experimentDataAdapter.CompleteExperimentAsync(_currentExperimentId, true, "流程已按[结束]指令正常结束");
                    }

                    Task[] endTasks;
                    lock (_backgroundTasksLock) { endTasks = _backgroundTasks.ToArray(); }
                    if (endTasks.Length > 0)
                    {
                        try { await Task.WhenAll(endTasks).WaitAsync(TimeSpan.FromSeconds(3)); }
                        catch { /* 忽略后台任务收尾异常 */ }
                    }

                    _statusService.ResetAllExecutionStatus();
                    State = FlowExecutionState.Completed;
                    _logger.LogInformation("流程按[结束]指令正常结束，已执行 {Completed}/{Total} 个节点",
                        _currentProgress?.CompletedNodes ?? 0, _currentProgress?.TotalNodes ?? 0);
                    State = FlowExecutionState.Idle;

                    return new FlowExecutionResult
                    {
                        IsSuccessful = true,
                        Duration = DateTime.Now - startTime,
                        TotalNodesExecuted = _currentProgress?.CompletedNodes ?? 0,
                        StartTime = startTime,
                        EndTime = DateTime.Now
                    };
                }

                // *** 新增：专门处理停止时的取消 ***
                if (!string.IsNullOrEmpty(_currentExperimentId))
                {
                    await _experimentDataAdapter.CompleteExperimentAsync(_currentExperimentId, false, "流程被用户停止");
                }

                // 取消后台任务
                Task[] tasksToCancel;
                lock (_backgroundTasksLock)
                {
                    tasksToCancel = _backgroundTasks.ToArray();
                }

                if (tasksToCancel.Length > 0)
                {
                    _logger.LogInformation("取消 {Count} 个后台任务", tasksToCancel.Length);
                    try
                    {
                        await Task.WhenAll(tasksToCancel).WaitAsync(TimeSpan.FromSeconds(3));
                    }
                    catch (TimeoutException)
                    {
                        _logger.LogWarning("部分后台任务未能及时取消");
                    }
                    catch
                    {
                        // 忽略取消导致的异常
                    }
                }

                _statusService.ResetAllExecutionStatus();
                State = FlowExecutionState.Stopped;
                var stoppedProgress = _currentProgress;
                _logger.LogInformation("流程因用户停止而取消，已执行 {Completed}/{Total} 个节点",
                    stoppedProgress?.CompletedNodes ?? 0, stoppedProgress?.TotalNodes ?? 0);

                //_ = Task.Delay(1000).ContinueWith(_ =>
                //{
                //    if (State == FlowExecutionState.Stopped)
                //    {
                //        State = FlowExecutionState.Idle;
                //    }
                //});
                State = FlowExecutionState.Idle;

                return new FlowExecutionResult
                {
                    IsSuccessful = false,
                    Duration = DateTime.Now - startTime,
                    ErrorMessage = "流程被用户停止",
                    TotalNodesExecuted = _currentProgress?.CompletedNodes ?? 0,
                    StartTime = startTime,
                    EndTime = DateTime.Now
                };
            }
            catch (OperationCanceledException)
            {
                // *** 新增：记录取消 ***
                if (!string.IsNullOrEmpty(_currentExperimentId))
                {
                    await _experimentDataAdapter.CompleteExperimentAsync(_currentExperimentId, false, "执行被取消");
                }

                // 取消后台任务
                Task[] tasksToCancel;
                lock (_backgroundTasksLock)
                {
                    tasksToCancel = _backgroundTasks.ToArray();
                }

                // 等待后台任务被取消
                if (tasksToCancel.Length > 0)
                {
                    try
                    {
                        await Task.WhenAll(tasksToCancel);
                    }
                    catch
                    {
                        // 忽略取消导致的异常
                    }
                }

                _statusService.ResetAllExecutionStatus();
                var cancelledProgress = _currentProgress;
                State = FlowExecutionState.Stopped;
                _logger.LogInformation("流程执行被取消，已执行 {Completed}/{Total} 个节点",
                    cancelledProgress?.CompletedNodes ?? 0, cancelledProgress?.TotalNodes ?? 0);

                //_ = Task.Delay(1000).ContinueWith(_ =>
                //{
                //    if (State == FlowExecutionState.Stopped)
                //    {
                //        State = FlowExecutionState.Idle;
                //    }
                //});
                State = FlowExecutionState.Idle;
                return new FlowExecutionResult
                {
                    IsSuccessful = false,
                    Duration = DateTime.Now - startTime,
                    ErrorMessage = "执行被取消",
                    TotalNodesExecuted = _currentProgress?.CompletedNodes ?? 0,
                    StartTime = startTime,
                    EndTime = DateTime.Now
                };
            }
            catch (Exception ex)
            {
                // *** 新增：记录失败 ***
                if (!string.IsNullOrEmpty(_currentExperimentId))
                {
                    await _experimentDataAdapter.CompleteExperimentAsync(_currentExperimentId, false, ex.Message);
                }

                // 取消后台任务
                _cancellationTokenSource?.Cancel();

                Task[] tasksToWait;
                lock (_backgroundTasksLock)
                {
                    tasksToWait = _backgroundTasks.ToArray();
                }

                if (tasksToWait.Length > 0)
                {
                    try
                    {
                        await Task.WhenAll(tasksToWait);
                    }
                    catch
                    {
                        // 忽略后台任务的异常
                    }
                }

                _statusService.FinalizeExecutionStatus();

                State = FlowExecutionState.Error;
                var errorProgress = _currentProgress;
                // *** 流程失败时通知所有设备 ***
                var failureContext = new FlowFailureContext
                {
                    IsSuccessful = false,
                    FlowName = "流程名称",
                    CompletionTime = DateTime.Now,
                    Duration = DateTime.Now - startTime,
                    ErrorMessage = ex.Message,
                    Exception = ex
                };

                await NotifyFlowFailedAsync(flowViewModel, failureContext);

                _logger.LogError(ex, "流程执行失败，已执行 {Completed}/{Total} 个节点",
                    errorProgress?.CompletedNodes ?? 0, errorProgress?.TotalNodes ?? 0);

                ExecutionError?.Invoke(this, new FlowExecutionErrorEventArgs
                {
                    Exception = ex,
                    ErrorMessage = ex.Message
                });

                //_ = Task.Delay(3000).ContinueWith(_ =>
                //{
                //    if (State == FlowExecutionState.Error)
                //    {
                //        State = FlowExecutionState.Idle;
                //    }
                //});
                State = FlowExecutionState.Idle;
                return new FlowExecutionResult
                {
                    IsSuccessful = false,
                    Duration = DateTime.Now - startTime,
                    ErrorMessage = ex.Message,
                    Exception = ex,
                    TotalNodesExecuted = _currentProgress?.CompletedNodes ?? 0,
                    StartTime = startTime,
                    EndTime = DateTime.Now
                };
            }
            finally
            {
                _cancellationTokenSource?.Dispose();
                _cancellationTokenSource = null;
                var progressToDispose = _currentProgress;
                _currentProgress = null;
                if (progressToDispose != null)
                {
                    try
                    {
                        progressToDispose.Dispose(); // 如果ExecutionProgress实现了IDisposable
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "清理进度对象失败");
                    }
                }
                lock (_backgroundTasksLock)
                {
                    _backgroundTasks.Clear();
                }
            }
        }

        /// <summary>
        /// 通知流程中绑定的设备流程开始
        /// </summary>
        private async Task NotifyFlowStartedAsync(FlowDesignerViewModel flowViewModel, DateTime startTime)
        {
            try
            {
                var devices = GetFlowBoundLifecycleAwareDevices(flowViewModel);

                if (!devices.Any())
                {
                    _logger.LogInformation("没有设备需要通知流程开始");
                    return;
                }

                var startContext = new FlowStartContext
                {
                    FlowName = "测试流程",
                    StartTime = startTime,
                    ExpectedNodeCount = CalculateTotalNodes(flowViewModel.CommandNodes),
                    Properties = new Dictionary<string, object>
                    {
                        //["FlowId"] = flowViewModel.Id,
                        //["BoundDeviceCount"] = devices.Count
                    }
                };

                _logger.LogInformation("通知 {Count} 个绑定设备流程开始: {FlowName}", devices.Count, "测试流程");

                var tasks = devices.Select(async device =>
                {
                    try
                    {
                        await device.OnFlowStartedAsync(startContext);
                        _logger.LogDebug("设备流程开始通知成功: {DeviceType}", device.GetType().Name);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "设备流程开始通知失败: {DeviceType}", device.GetType().Name);
                    }
                });

                await Task.WhenAll(tasks);
                _logger.LogInformation("流程开始通知完成");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "通知设备流程开始失败");
            }
        }

        /// <summary>
        /// 通知流程中绑定的设备流程完成
        /// </summary>
        private async Task NotifyFlowCompletedAsync(FlowDesignerViewModel flowViewModel, FlowCompletionContext context)
        {
            try
            {
                var devices = GetFlowBoundLifecycleAwareDevices(flowViewModel);

                if (!devices.Any())
                {
                    _logger.LogInformation("没有设备需要通知流程完成");
                    return;
                }

                // 添加额外的上下文信息
                //context.Properties["FlowId"] = flowViewModel.Id;
                context.Properties["BoundDeviceCount"] = devices.Count;

                _logger.LogInformation("通知 {Count} 个绑定设备流程完成: {FlowName}", devices.Count, "测试流程");

                var tasks = devices.Select(async device =>
                {
                    try
                    {
                        var success = await device.OnFlowCompletedAsync(context);
                        _logger.LogDebug("设备流程完成通知{Result}: {DeviceType}",
                            success ? "成功" : "失败", device.GetType().Name);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "设备流程完成通知失败: {DeviceType}", device.GetType().Name);
                    }
                });

                await Task.WhenAll(tasks);
                _logger.LogInformation("流程完成通知完成");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "通知设备流程完成失败");
            }
        }


        /// <summary>
        /// 通知流程中绑定的设备流程失败
        /// </summary>
        private async Task NotifyFlowFailedAsync(FlowDesignerViewModel flowViewModel, FlowFailureContext context)
        {
            try
            {
                var devices = GetFlowBoundLifecycleAwareDevices(flowViewModel);

                if (!devices.Any())
                {
                    _logger.LogInformation("没有设备需要通知流程失败");
                    return;
                }

                // 添加额外的上下文信息
                //context.Properties["FlowId"] = flowViewModel.Id;
                context.Properties["BoundDeviceCount"] = devices.Count;

                _logger.LogInformation("通知 {Count} 个绑定设备流程失败: {FlowName}", devices.Count, "测试流程");

                var tasks = devices.Select(async device =>
                {
                    try
                    {
                        var success = await device.OnFlowFailedAsync(context);
                        _logger.LogDebug("设备流程失败通知{Result}: {DeviceType}",
                            success ? "成功" : "失败", device.GetType().Name);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "设备流程失败通知失败: {DeviceType}", device.GetType().Name);
                    }
                });

                await Task.WhenAll(tasks);
                _logger.LogInformation("流程失败通知完成");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "通知设备流程失败失败");
            }
        }

        /// <summary>
        /// 获取所有支持流程生命周期的设备
        /// </summary>
        private List<IFlowLifecycleAware> GetAllFlowLifecycleAwareDevices()
        {
            List<CanvasDeviceViewModel> canvasDevices = null;
            _eventAggregator?.GetEvent<RequestCanvasDevicesEvent>()?.Publish(list => canvasDevices = list);

            return canvasDevices?
                .Where(d => d.Device is IFlowLifecycleAware)
                .Select(d => d.Device as IFlowLifecycleAware)
                .ToList() ?? new List<IFlowLifecycleAware>();
        }

        /// <summary>
        /// 获取当前流程中绑定的且支持流程生命周期的设备
        /// </summary>
        private List<IFlowLifecycleAware> GetFlowBoundLifecycleAwareDevices(FlowDesignerViewModel flowViewModel)
        {
            try
            {
                // 1. 收集流程中所有绑定的设备ID
                var boundDeviceIds = CollectBoundDeviceIds(flowViewModel);

                if (!boundDeviceIds.Any())
                {
                    _logger.LogInformation("流程中没有绑定任何设备");
                    return new List<IFlowLifecycleAware>();
                }

                _logger.LogDebug("流程中绑定的设备: {DeviceIds}", string.Join(", ", boundDeviceIds));

                // 2. 获取画布设备列表
                List<CanvasDeviceViewModel> canvasDevices = null;
                _eventAggregator?.GetEvent<RequestCanvasDevicesEvent>()?.Publish(list => canvasDevices = list);

                if (canvasDevices == null || !canvasDevices.Any())
                {
                    _logger.LogWarning("画布上没有设备");
                    return new List<IFlowLifecycleAware>();
                }

                // 3. 筛选出流程中绑定的且支持生命周期的设备
                var lifecycleAwareDevices = canvasDevices
                    .Where(d => boundDeviceIds.Contains(d.DeviceId) && d.Device is IFlowLifecycleAware)
                    .Select(d => d.Device as IFlowLifecycleAware)
                    .ToList();

                _logger.LogInformation("找到 {Count} 个绑定的生命周期感知设备", lifecycleAwareDevices.Count);

                return lifecycleAwareDevices;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取流程绑定的生命周期感知设备失败");
                return new List<IFlowLifecycleAware>();
            }
        }

        /// <summary>
        /// 收集流程中所有绑定的设备ID
        /// </summary>
        private HashSet<string> CollectBoundDeviceIds(FlowDesignerViewModel flowViewModel)
        {
            var deviceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (flowViewModel?.CommandNodes == null)
                return deviceIds;

            // 递归收集所有节点（包括子节点）的绑定设备ID
            CollectDeviceIdsFromNodes(flowViewModel.CommandNodes, deviceIds);

            return deviceIds;
        }

        /// <summary>
        /// 从节点集合中递归收集设备ID
        /// </summary>
        private void CollectDeviceIdsFromNodes(IEnumerable<CommandNode> nodes, HashSet<string> deviceIds)
        {
            foreach (var node in nodes)
            {
                // 只收集设备命令节点的绑定设备ID
                if (node.DeviceCommand != null && !string.IsNullOrEmpty(node.BoundDeviceId))
                {
                    deviceIds.Add(node.BoundDeviceId);
                    _logger.LogDebug("收集到设备绑定: 节点={NodeName}, 设备ID={DeviceId}",
                        node.DisplayName, node.BoundDeviceId);
                }

                // 递归处理子节点
                if (node.HasChildren)
                {
                    CollectDeviceIdsFromNodes(node.Children, deviceIds);
                }
            }
        }

        #region 新增：设备连接验证

        /// <summary>
        /// 验证流程中所有设备的连接状态
        /// </summary>
        /// <summary>
        /// 验证流程中所有设备的连接状态（支持模拟模式）
        /// </summary>
        private async Task<FlowValidationResult> ValidateDeviceConnectionsAsync(FlowDesignerViewModel flowViewModel, CancellationToken cancellationToken)
        {
            var result = new FlowValidationResult { IsValid = true };

            try
            {
                // 1. 收集流程中所有用到的设备
                var requiredDevices = CollectRequiredDevices(flowViewModel);

                if (!requiredDevices.Any())
                {
                    _logger.LogInformation("流程中没有使用任何设备，跳过设备连接验证");
                    return result;
                }

                _logger.LogInformation("流程需要 {Count} 个设备: {Devices}",
                    requiredDevices.Count, string.Join(", ", requiredDevices.Select(d => $"{d.DeviceName}({d.DeviceId})")));

                // 2. *** 新增：检查模拟模式状态 ***
                bool isSimulationMode = GetCurrentSimulationMode();
                _logger.LogInformation("当前运行模式: {Mode}", isSimulationMode ? "模拟模式" : "实际模式");

                // 3. 获取画布设备列表
                List<CanvasDeviceViewModel> canvasDevices = null;
                _eventAggregator?.GetEvent<RequestCanvasDevicesEvent>()?.Publish(list => canvasDevices = list);

                if (canvasDevices == null || !canvasDevices.Any())
                {
                    result.IsValid = false;
                    result.Errors.Add("未找到任何画布设备，无法执行流程");
                    return result;
                }

                // 4. *** 新增：应用模拟模式到所有相关设备 ***
                ApplySimulationModeToDevices(requiredDevices, isSimulationMode);

                // 5. *** 修改：根据模拟模式决定验证策略 ***
                if (isSimulationMode)
                {
                    _logger.LogInformation("模拟模式下，跳过实际设备连接验证");

                    // 模拟模式下，确保所有设备都存在于画布上即可
                    foreach (var requiredDevice in requiredDevices)
                    {
                        var canvasDevice = canvasDevices.FirstOrDefault(d => d.DeviceId == requiredDevice.DeviceId);
                        if (canvasDevice?.Device == null)
                        {
                            result.IsValid = false;
                            result.Errors.Add($"未在画布上找到设备: {requiredDevice.DeviceName} ({requiredDevice.DeviceId})");
                        }
                        else
                        {
                            _logger.LogInformation("模拟模式设备验证通过: {DeviceName} ({DeviceId})",
                                requiredDevice.DeviceName, requiredDevice.DeviceId);
                        }
                    }

                    return result;
                }

                // 6. 实际模式下的设备连接验证
                _logger.LogInformation("实际模式下，执行设备连接验证");
                var deviceValidationTasks = new List<Task<DeviceConnectionValidationResult>>();

                foreach (var requiredDevice in requiredDevices)
                {
                    var canvasDevice = canvasDevices.FirstOrDefault(d => d.DeviceId == requiredDevice.DeviceId);
                    if (canvasDevice?.Device == null)
                    {
                        result.IsValid = false;
                        result.Errors.Add($"未在画布上找到设备: {requiredDevice.DeviceName} ({requiredDevice.DeviceId})");
                        continue;
                    }

                    // 异步验证设备连接
                    var task = ValidateSingleDeviceConnectionAsync(canvasDevice.Device, requiredDevice, cancellationToken);
                    deviceValidationTasks.Add(task);
                }

                // 7. 等待所有设备验证完成
                if (deviceValidationTasks.Any())
                {
                    var deviceResults = await Task.WhenAll(deviceValidationTasks);

                    foreach (var deviceResult in deviceResults)
                    {
                        if (!deviceResult.IsConnected)
                        {
                            result.IsValid = false;
                            result.Errors.Add($"设备连接失败: {deviceResult.DeviceName} - {deviceResult.ErrorMessage}");
                        }
                        else
                        {
                            _logger.LogInformation("设备连接验证通过: {DeviceName} ({DeviceId})",
                                deviceResult.DeviceName, deviceResult.DeviceId);
                        }
                    }
                }

                return result;
            }
            catch (Exception ex)
            {
                result.IsValid = false;
                result.Errors.Add($"设备连接验证异常: {ex.Message}");
                _logger.LogError(ex, "设备连接验证过程中发生异常");
                return result;
            }
        }

        /// <summary>
        /// 验证单个设备的连接状态
        /// </summary>
        private async Task<DeviceConnectionValidationResult> ValidateSingleDeviceConnectionAsync(
             IDevice device, RequiredDeviceInfo deviceInfo, CancellationToken cancellationToken)
        {
            var result = new DeviceConnectionValidationResult
            {
                DeviceId = deviceInfo.DeviceId,
                DeviceName = deviceInfo.DeviceName,
                IsConnected = false
            };

            try
            {
                _logger.LogDebug("验证设备连接: {DeviceName} ({DeviceId}) - 模拟模式: {SimulationMode}",
                    deviceInfo.DeviceName, deviceInfo.DeviceId, device.IsSimulationMode);

                // 检查当前连接状态
                if (device.IsConnected)
                {
                    // 已连接，验证连接是否有效
                    _logger.LogDebug("设备已连接，验证连接有效性: {DeviceName}", deviceInfo.DeviceName);

                    var isConnectionValid = await device.CheckConnectionAsync();
                    if (isConnectionValid)
                    {
                        result.IsConnected = true;
                        _logger.LogDebug("设备连接有效: {DeviceName}", deviceInfo.DeviceName);
                        return result;
                    }
                    else
                    {
                        _logger.LogWarning("设备连接无效，尝试重新连接: {DeviceName}", deviceInfo.DeviceName);
                    }
                }

                // 尝试连接设备
                _logger.LogInformation("正在连接设备: {DeviceName} ({DeviceId}) - 模式: {Mode}",
                    deviceInfo.DeviceName, deviceInfo.DeviceId, device.IsSimulationMode ? "模拟" : "实际");

                var connectResult = await device.ConnectAsync();
                if (connectResult)
                {
                    result.IsConnected = true;
                    _logger.LogInformation("设备连接成功: {DeviceName} ({DeviceId}) - 模式: {Mode}",
                        deviceInfo.DeviceName, deviceInfo.DeviceId, device.IsSimulationMode ? "模拟" : "实际");
                }
                else
                {
                    result.ErrorMessage = "设备连接失败，无具体错误信息";
                    _logger.LogError("设备连接失败: {DeviceName} ({DeviceId}) - 模式: {Mode}",
                        deviceInfo.DeviceName, deviceInfo.DeviceId, device.IsSimulationMode ? "模拟" : "实际");
                }

                return result;
            }
            catch (OperationCanceledException)
            {
                result.ErrorMessage = "连接验证被取消";
                _logger.LogWarning("设备连接验证被取消: {DeviceName}", deviceInfo.DeviceName);
                return result;
            }
            catch (Exception ex)
            {
                result.ErrorMessage = ex.Message;
                _logger.LogError(ex, "设备连接验证异常: {DeviceName} ({DeviceId})",
                    deviceInfo.DeviceName, deviceInfo.DeviceId);
                return result;
            }
        }

        /// <summary>
        /// 收集流程中所有需要的设备信息
        /// </summary>
        private List<RequiredDeviceInfo> CollectRequiredDevices(FlowDesignerViewModel flowViewModel)
        {
            var devices = new Dictionary<string, RequiredDeviceInfo>();
            var allNodes = GetAllNodes(flowViewModel.CommandNodes);

            foreach (var node in allNodes)
            {
                if (node.DeviceCommand != null && !string.IsNullOrEmpty(node.BoundDeviceId))
                {
                    if (!devices.ContainsKey(node.BoundDeviceId))
                    {
                        devices[node.BoundDeviceId] = new RequiredDeviceInfo
                        {
                            DeviceId = node.BoundDeviceId,
                            DeviceName = node.BoundDeviceName ?? node.BoundDeviceId,
                            Commands = new List<string>()
                        };
                    }

                    // 记录使用的命令
                    if (!string.IsNullOrEmpty(node.DeviceCommand.Name) &&
                        !devices[node.BoundDeviceId].Commands.Contains(node.DeviceCommand.Name))
                    {
                        devices[node.BoundDeviceId].Commands.Add(node.DeviceCommand.Name);
                    }
                }
            }

            return devices.Values.ToList();
        }

        #endregion
        // ── 实验进度写库节流 ──
        // 进度只是 completed/total 两个数字,但此前每执行一个节点就 await 一次 MySQL 写入
        // (循环体内还额外重复一次):循环类流程每拍要等 1~2 趟数据库往返,执行节奏被拖慢,
        // 用户感知为「监控标签更新慢」。这里统一节流为至多每秒一次;流程结束处的最终写入
        // (不经本方法)保证落库终值准确。
        private DateTime _lastProgressDbWriteUtc = DateTime.MinValue;

        private async Task WriteExperimentProgressThrottledAsync(string context)
        {
            var progress = _currentProgress;
            if (progress == null || string.IsNullOrEmpty(_currentExperimentId)) return;

            var now = DateTime.UtcNow;
            if ((now - _lastProgressDbWriteUtc).TotalMilliseconds < 1000) return;
            _lastProgressDbWriteUtc = now;

            try
            {
                await _experimentDataAdapter.UpdateExperimentProgressAsync(
                    _currentExperimentId, progress.CompletedNodes, progress.TotalNodes).ConfigureAwait(false);
                _logger.LogDebug("实验进度已更新({Context}): {ExperimentId} - {Completed}/{Total}",
                    context, _currentExperimentId, progress.CompletedNodes, progress.TotalNodes);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "更新实验进度失败({Context})", context);
            }
        }

        private async Task ExecuteNodeWithProgressAsync(CommandNode node, FlowExecutionContext context, CancellationToken cancellationToken)
        {
            // [结束]指令已请求结束流程：跳过后续(含 If/Loop/Parallel 内)节点的调度，让各层循环尽快收敛
            if (State == FlowExecutionState.Stopping) return;

            await ExecuteNodeAsync(node, context, cancellationToken);

            var currentProgress = _currentProgress; // 获取本地引用
            if (currentProgress != null)
            {
                // 更新进度
                currentProgress.IncrementCompleted();

                // 更新实验进度到数据库(节流:见 WriteExperimentProgressThrottledAsync 注释)
                await WriteExperimentProgressThrottledAsync("节点完成").ConfigureAwait(false);

                // 发送进度事件
                try
                {
                    ProgressChanged?.Invoke(this, new FlowExecutionProgressEventArgs
                    {
                        TotalNodes = currentProgress.TotalNodes,
                        CompletedNodes = currentProgress.CompletedNodes,
                        ProgressPercentage = currentProgress.GetPercentage(),
                        CurrentNode = node
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "发送进度事件失败");
                }

                _logger.LogDebug("节点执行进度: {Completed}/{Total} ({Percentage:F1}%)",
                    currentProgress.CompletedNodes, currentProgress.TotalNodes, currentProgress.GetPercentage());
            }
            else
            {
                _logger.LogDebug("节点 {NodeName} 执行完成，但进度跟踪已被清理（流程可能已停止）", node.DisplayName);
            }
        }

        /// <summary>
        /// 执行节点并更新进度
        /// </summary>
        private async Task ExecuteNodeAsync(CommandNode node, FlowExecutionContext context, CancellationToken cancellationToken)
        {
            var startTime = DateTime.Now;
            var executionArgs = new NodeExecutionEventArgs
            {
                Node = node,
                StartTime = startTime
            };

            try
            {
                _logger.LogDebug("开始执行节点: {NodeName} ({NodeType})", node.DisplayName, node.CommandType);
                _statusService.StartNodeExecution(node.NodeId);
                NodeExecuting?.Invoke(this, executionArgs);
                // *** 新增：在节点执行前检查暂停状态 ***
                cancellationToken.ThrowIfCancellationRequested();
                _pauseEvent.Wait(cancellationToken);   // 用户主动暂停：前台后台都响应（后台采集也会停）
                                                       // 后台异步任务（如异步 Loop 采集泵）不响应"用户交互暂停"，避免等待节点弹框时冻结后台采集
                if (!_isInBackgroundAsyncContext.Value)
                {
                    _userInteractionPauseEvent.Wait(cancellationToken);
                }

                object result = null;

                if (node.DeviceCommand != null)
                {
                    result = await ExecuteDeviceCommandAsync(node, context, cancellationToken);
                }
                else if (node.LogicCommand != null)
                {
                    result = await ExecuteLogicCommandAsync(node, context, cancellationToken);
                }
                var endTime = DateTime.Now;
                executionArgs.EndTime = endTime;
                executionArgs.IsSuccessful = true;
                executionArgs.Result = result;

                _statusService.CompleteNodeExecution(node.NodeId, true);

                // *** 新增：记录节点执行 ***
                if (!string.IsNullOrEmpty(_currentExperimentId))
                {
                    await _experimentDataAdapter.RecordNodeExecutionAsync(_currentExperimentId, node, startTime, endTime, true);
                }


                context.AddExecutionHistory(new NodeExecutionRecord
                {
                    NodeId = node.NodeId,
                    NodeName = node.DisplayName,
                    StartTime = startTime,
                    EndTime = DateTime.Now,
                    IsSuccessful = true,
                    Result = result
                });

                _logger.LogDebug("节点执行成功: {NodeName}, 耗时: {Duration}ms",
                    node.DisplayName, (DateTime.Now - startTime).TotalMilliseconds);
            }
            catch (Exception ex)
            {
                var endTime = DateTime.Now;
              
                executionArgs.EndTime = endTime;
                executionArgs.IsSuccessful = false;
                executionArgs.ErrorMessage = ex.Message;
                _statusService.CompleteNodeExecution(node.NodeId, false, ex.Message);

                // *** 新增：记录节点执行失败 ***
                if (!string.IsNullOrEmpty(_currentExperimentId))
                {
                    await _experimentDataAdapter.RecordNodeExecutionAsync(_currentExperimentId, node, startTime, endTime, false, ex.Message);
                }


                context.AddExecutionHistory(new NodeExecutionRecord
                {
                    NodeId = node.NodeId,
                    NodeName = node.DisplayName,
                    StartTime = startTime,
                    EndTime = DateTime.Now,
                    IsSuccessful = false,
                    ErrorMessage = ex.Message
                });

                _logger.LogError(ex, "节点执行失败: {NodeName}", node.DisplayName);
                throw;
            }
            finally
            {
                NodeExecuted?.Invoke(this, executionArgs);
            }
        }
        /// <summary>
        /// 开始用户交互（暂停所有分支）
        /// </summary>
        private void BeginUserInteraction(CommandNode node, string interactionType, string description)
        {
            if (_isWaitingForUserInteraction)
            {
                _logger.LogWarning("已经在等待用户交互，忽略新的交互请求: {NodeName}", node.DisplayName);
                return;
            }

            _isWaitingForUserInteraction = true;
            _userInteractionPauseEvent.Reset();

            var eventArgs = new UserInteractionEventArgs
            {
                NodeId = node.NodeId,
                NodeName = node.DisplayName,
                InteractionType = interactionType,
                Description = description,
                StartTime = DateTime.Now
            };

            _logger.LogInformation("开始用户交互，暂停所有并行分支: {NodeName} - {Type}", node.DisplayName, interactionType);
            UserInteractionStarted?.Invoke(this, eventArgs);
        }

        /// <summary>
        /// 结束用户交互（恢复所有分支）
        /// </summary>
        private void EndUserInteraction(CommandNode node, string interactionType)
        {
            if (!_isWaitingForUserInteraction)
            {
                _logger.LogWarning("当前不在用户交互状态，忽略结束请求: {NodeName}", node.DisplayName);
                return;
            }

            _isWaitingForUserInteraction = false;
            _userInteractionPauseEvent.Set();

            var eventArgs = new UserInteractionEventArgs
            {
                NodeId = node.NodeId,
                NodeName = node.DisplayName,
                InteractionType = interactionType,
                StartTime = DateTime.Now
            };

            _logger.LogInformation("结束用户交互，恢复所有并行分支: {NodeName} - {Type}", node.DisplayName, interactionType);
            UserInteractionEnded?.Invoke(this, eventArgs);
        }
        private async Task<object> ExecuteDeviceCommandAsync(CommandNode node, FlowExecutionContext context, CancellationToken cancellationToken)
        {
            if (node.DeviceCommand == null)
                throw new InvalidOperationException("DeviceCommand 为空");

            if (string.IsNullOrEmpty(node.BoundDeviceId))
                throw new InvalidOperationException($"设备命令节点未绑定设备: {node.DisplayName}");

            // 通过事件请求画布设备列表
            List<CanvasDeviceViewModel> canvasDevices = null;
            _eventAggregator?.GetEvent<RequestCanvasDevicesEvent>()?.Publish(list => canvasDevices = list);

            var targetCanvasDevice = canvasDevices?.FirstOrDefault(d => d.DeviceId == node.BoundDeviceId);
            var targetDevice = targetCanvasDevice?.Device;
            if (targetDevice == null)
                throw new InvalidOperationException($"未在画布上找到设备实例: {node.BoundDeviceId}");

            // 保存设备引用到节点
            node.Device = targetDevice;
            //node.BoundDeviceName = targetDevice.Name;

            var targetCmd = targetDevice.Commands?.FirstOrDefault(c => c.Name == node.DeviceCommand.Name);
            if (targetCmd == null)
                throw new InvalidOperationException($"设备 {targetDevice.Name} 不支持命令: {node.DeviceCommand.Name}");
            // *** 新增：确保设备处于正确的模拟模式 ***
            bool currentSimulationMode = GetCurrentSimulationMode();
            if (targetDevice.IsSimulationMode != currentSimulationMode)
            {
                _logger.LogInformation("调整设备模拟模式: {DeviceId} {OldMode} -> {NewMode}",
                    node.BoundDeviceId, targetDevice.IsSimulationMode ? "模拟" : "实际", currentSimulationMode ? "模拟" : "实际");
                targetDevice.IsSimulationMode = currentSimulationMode;
            }
            // 合并"模板参数 + 节点覆盖值"得到生效参数副本
            var inputParameters = BuildEffectiveParametersForNode(node, targetCmd);

            // 通知画布开始动画
            _eventAggregator?.GetEvent<DeviceExecutionStartedEvent>()?.Publish(new DeviceExecutionEventArgs
            {
                DeviceId = node.BoundDeviceId,
                CommandName = targetCmd.Name
            });

            // 记录开始日志
            _logger.LogInformation(
                "执行设备命令开始: Node={Node} NodeId={NodeId} DeviceId={DeviceId} DeviceName={DeviceName} Command={Command} Params={Params}",
                node.DisplayName, node.NodeId, node.BoundDeviceId, node.BoundDeviceName, targetCmd.Name, FormatParameters(inputParameters));

            var sw = Stopwatch.StartNew();
            try
            {
                // 在后台线程执行设备命令，避免阻塞
                // 在后台线程执行设备命令,避免阻塞
                DeviceParameters resultParams;

                if (targetCmd.AsyncAction != null)
                {
                    // 优先用异步 Action(直接 await,无需 Task.Run 包裹,避免嵌套 Task)
                    resultParams = await targetCmd.AsyncAction.Invoke(inputParameters).ConfigureAwait(false);
                }
                else if (targetCmd.Action != null)
                {
                    // 兼容只设置了同步 Action 的命令,放到线程池执行
                    resultParams = await Task.Run(() => targetCmd.Action.Invoke(inputParameters), cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    throw new InvalidOperationException(
                        $"设备命令 {targetCmd.Name} 既未设置 Action 也未设置 AsyncAction");
                }

                // 检查取消(Task.Run 之外的取消检查)
                cancellationToken.ThrowIfCancellationRequested();

                sw.Stop();

             

                // 保存执行结果到节点
                node.LastExecutionResult = new DeviceCommandResult
                {
                    Success = true,
                    OutputParameters = resultParams,
                    ExecutionTime = DateTime.Now
                };

                // LastOutputParameters 会通过 setter 自动更新

                // 确保在UI线程发布事件
                if (_eventAggregator != null)
                {
                    var eventArgs = new NodeExecutionResultEventArgs
                    {
                        NodeId = node.NodeId,
                        DeviceName = node.BoundDeviceName,
                        CommandName = targetCmd.Name,
                        OutputParameters = resultParams
                    };

                    // 使用UI线程发布事件
                    Application.Current?.Dispatcher?.BeginInvoke(new Action(() =>
                    {
                        _eventAggregator.GetEvent<NodeExecutionResultUpdatedEvent>()?.Publish(eventArgs);
                    }));
                }
                // 新增：发布监控卡片更新事件
                PublishMonitoringCardUpdateEvent(node.BoundDeviceId, targetCmd.Name, resultParams);

                _logger.LogInformation(
                    "执行设备命令完成: Node={Node} NodeId={NodeId} DeviceId={DeviceId} DeviceName={DeviceName} Command={Command} DurationMs={Duration} Output={Output}",
                    node.DisplayName, node.NodeId, node.BoundDeviceId, targetDevice.Name, targetCmd.Name, sw.ElapsedMilliseconds, FormatParameters(resultParams));

                _eventAggregator?.GetEvent<DeviceExecutionEndedEvent>()?.Publish(new DeviceExecutionEventArgs
                {
                    DeviceId = node.BoundDeviceId,
                    CommandName = targetCmd.Name,
                    IsSuccessful = true
                });
                // *** 新增：记录设备数据（实时记录所有数据） ***
                if (!string.IsNullOrEmpty(_currentExperimentId))
                {
                    await _experimentDataAdapter.RecordDeviceDataAsync(
                        _currentExperimentId,
                        node.BoundDeviceId,
                        node.BoundDeviceName ?? targetDevice.Name,
                        targetCmd.Name,
                        inputParameters,
                        resultParams,
                        node.NodeId,
                        node.DisplayName);
                }

                return resultParams;
            }
            catch (Exception ex)
            {
                sw.Stop();

                // 保存失败结果
                node.LastExecutionResult = new DeviceCommandResult
                {
                    Success = false,
                    ErrorMessage = ex.Message,
                    Exception = ex,
                    ExecutionTime = DateTime.Now
                };

                _logger.LogError(ex,
                    "执行设备命令失败: Node={Node} NodeId={NodeId} DeviceId={DeviceId} DeviceName={DeviceName} Command={Command} DurationMs={Duration} Params={Params}",
                    node.DisplayName, node.NodeId, node.BoundDeviceId, targetDevice.Name, targetCmd.Name, sw.ElapsedMilliseconds, FormatParameters(inputParameters));

                _eventAggregator?.GetEvent<DeviceExecutionEndedEvent>()?.Publish(new DeviceExecutionEventArgs
                {
                    DeviceId = node.BoundDeviceId,
                    CommandName = targetCmd.Name,
                    IsSuccessful = false,
                    ErrorMessage = ex.Message
                });

                throw;
            }
        }
        /// <summary>
        /// 发布监控卡片更新事件
        /// </summary>
        private void PublishMonitoringCardUpdateEvent(string deviceId, string commandName, DeviceParameters outputParameters)
        {
            try
            {
                if (outputParameters?.Variables?.Any() == true)
                {
                    _eventAggregator?.GetEvent<MonitoringCardUpdateEvent>()?.Publish(new MonitoringCardUpdateEventArgs
                    {
                        DeviceId = deviceId,
                        CommandName = commandName,
                        OutputParameters = outputParameters,
                        UpdateTime = DateTime.Now
                    });

                    Debug.WriteLine($"发布监控卡片更新事件: 设备 {deviceId}, 命令 {commandName}, 参数数量 {outputParameters.Variables.Count}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "发布监控卡片更新事件失败");
            }
        }
        private async Task<object> ExecuteLogicCommandAsync(CommandNode node, FlowExecutionContext context, CancellationToken cancellationToken)
        {
            switch (node.LogicCommand.CommandType)
            {
                case LogicCommandType.If:
                    return await ExecuteIfCommandAsync(node, context, cancellationToken);

                case LogicCommandType.Loop:
                    return await ExecuteLoopCommandAsync(node, context, cancellationToken);

                case LogicCommandType.Parallel:
                    return await ExecuteParallelCommandAsync(node, context, cancellationToken);

                case LogicCommandType.Wait:
                    return await ExecuteWaitCommandAsync(node, context, cancellationToken);

                case LogicCommandType.SetVariable:
                    return await ExecuteSetVariableCommandAsync(node, context, cancellationToken);

                case LogicCommandType.End:
                    return await ExecuteEndCommandAsync(node, context, cancellationToken);

                default:
                    _logger.LogWarning("未知的逻辑命令类型: {CommandType}", node.LogicCommand.CommandType);
                    return null;
            }
        }

        /// <summary>
        /// 执行[结束]指令：把流程状态置为 Stopping，使顶层 foreach 与嵌套循环停止调度后续节点，
        /// 从而优雅地结束整个流程（走正常完成路径，标记为"成功完成"，而不是"被取消/被停止"）。
        /// 关键：这里不取消 CancellationToken，避免抛出 OperationCanceledException 被判定为"被取消"。
        /// </summary>
        private async Task<object> ExecuteEndCommandAsync(CommandNode node, FlowExecutionContext context, CancellationToken cancellationToken)
        {
            _logger.LogInformation("执行[结束]指令，请求结束整个流程: {NodeName}", node.DisplayName);

            _endedByCommand = true;   // 标记为"主动正常结束"，据此把停止判为成功而非"被用户停止"
            if (State == FlowExecutionState.Running)
            {
                State = FlowExecutionState.Stopping;
            }

            await Task.CompletedTask;
            return new { Ended = true };
        }

        private async Task<object> ExecuteIfCommandAsync(CommandNode ifNode, FlowExecutionContext context, CancellationToken cancellationToken)
        {
            _logger.LogDebug("执行IF命令: {NodeName}", ifNode.DisplayName);
            try
            {
                // 获取IF节点的条件参数
                var leftOperand = ifNode.LogicCommand.Properties?.GetValueOrDefault("LeftOperand", "")?.ToString() ?? "";
                var operatorStr = ifNode.LogicCommand.Properties?.GetValueOrDefault("Operator", "Equal")?.ToString() ?? "Equal";
                var rightOperand = ifNode.LogicCommand.Properties?.GetValueOrDefault("RightOperand", "")?.ToString() ?? "";

                _logger.LogDebug("IF条件参数: Left={Left}, Operator={Op}, Right={Right}",
                    leftOperand, operatorStr, rightOperand);

                // 解析左操作数的值
                var leftValue = ResolveOperandValue(leftOperand, context, ifNode);

                // 解析右操作数的值
                var rightValue = ResolveOperandValue(rightOperand, context, ifNode);

                _logger.LogDebug("解析后的值: Left={LeftVal} (Type={LeftType}), Right={RightVal} (Type={RightType})",
                    leftValue, leftValue?.GetType().Name ?? "null",
                    rightValue, rightValue?.GetType().Name ?? "null");

                // 评估条件
                bool conditionResult = EvaluateCondition(leftValue, operatorStr, rightValue);

                _logger.LogDebug("IF条件评估结果: {Result}", conditionResult);

                // 选择要执行的分支
                var branchToExecute = conditionResult ? "True" : "False";
                var branchChildren = ifNode.Children?.Where(child =>
                    child.GetExecutionProperty<string>("BranchType") == branchToExecute).ToList() ?? new List<CommandNode>();

                _logger.LogDebug("执行 {Branch} 分支，包含 {Count} 个子节点", branchToExecute, branchChildren.Count);

                foreach (var childNode in branchChildren)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    _pauseEvent.Wait(cancellationToken);

                    await ExecuteNodeWithProgressAsync(childNode, context, cancellationToken);
                }

                return conditionResult;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "IF命令执行失败: {NodeName}", ifNode.DisplayName);
                throw;
            }
        }


        private async Task<object> ExecuteLoopCommandAsync(CommandNode loopNode, FlowExecutionContext context, CancellationToken cancellationToken)
        {
            _logger.LogDebug("执行循环命令: {NodeName}", loopNode.DisplayName);

            try
            {
                // 读取 LoopType（兼容枚举/字符串/数值）
                var loopTypeObj = loopNode.LogicCommand.Properties?.GetValueOrDefault("LoopType", LoopType.FixedCount);
                var loopType = ReadLoopType(loopTypeObj);

                // 读取循环间隔时间（毫秒）
                var waitTimeObj = loopNode.LogicCommand.Properties?.GetValueOrDefault("WaitTime", 10);
                int waitTimeMs = SafeToInt(waitTimeObj, 10);
                waitTimeMs = Math.Max(0, waitTimeMs);

                // 循环变量名（可选），默认 i
                var loopVariable = loopNode.LogicCommand.Properties?.GetValueOrDefault("LoopVariable", "i")?.ToString() ?? "i";

                // ★ 新增：读取执行模式（兼容枚举/字符串）
                var execModeObj = loopNode.LogicCommand.Properties?.GetValueOrDefault("ExecutionMode", ExecutionMode.Synchronous);
                var executionMode = ReadExecutionModeForEngine(execModeObj);

                // 子节点快照
                var children = loopNode.Children?.ToList() ?? new List<CommandNode>();

                _logger.LogDebug("循环参数: Type={Type}, Mode={Mode}, WaitTime={WaitTime}ms, Variable={Variable}, Children={Count}",
                    loopType, executionMode, waitTimeMs, loopVariable, children.Count);

                // ★ 防呆：无限循环 + 异步 = 危险组合，强制回退为同步
                if (executionMode == ExecutionMode.Asynchronous && loopType == LoopType.Infinite)
                {
                    _logger.LogWarning("循环节点 {NodeName} 配置为无限循环+异步，已强制回退为同步执行（避免流程无法收尾）",
                        loopNode.DisplayName);
                    executionMode = ExecutionMode.Synchronous;
                }

                // ★ 异步分支：丢到后台跑，登记到 _backgroundTasks，主流程立即返回
                if (executionMode == ExecutionMode.Asynchronous)
                {
                    _logger.LogInformation("循环节点 {NodeName} 以异步模式启动，将在后台执行", loopNode.DisplayName);

                    var bgTask = Task.Run(async () =>
                    {
                        _isInBackgroundAsyncContext.Value = true;   //  本任务链为后台异步，豁免用户交互暂停
                        try
                        {
                            switch (loopType)
                            {
                                case LoopType.FixedCount:
                                    await ExecuteFixedCountLoopAsync(loopNode, context, cancellationToken, children, waitTimeMs, loopVariable).ConfigureAwait(false);
                                    break;
                                case LoopType.Conditional:
                                    await ExecuteConditionalLoopAsync(loopNode, context, cancellationToken, children, waitTimeMs, loopVariable).ConfigureAwait(false);
                                    break;
                                default:
                                    await ExecuteFixedCountLoopAsync(loopNode, context, cancellationToken, children, waitTimeMs, loopVariable).ConfigureAwait(false);
                                    break;
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            _logger.LogInformation("后台异步循环被取消: {NodeName}", loopNode.DisplayName);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "后台异步循环执行失败: {NodeName}", loopNode.DisplayName);
                        }
                    }, cancellationToken);

                    // 登记到后台任务列表（流程结束时会被 Task.WhenAll 等待，停止时会被取消）
                    lock (_backgroundTasksLock)
                    {
                        _backgroundTasks.Add(bgTask);
                    }

                    // 任务完成后从列表里移除
                    _ = bgTask.ContinueWith(t =>
                    {
                        lock (_backgroundTasksLock)
                        {
                            _backgroundTasks.Remove(t);
                        }
                    }, TaskContinuationOptions.ExecuteSynchronously);

                    // 主流程立即返回，继续执行后续节点
                    return 0;
                }

                // 同步分支：原有逻辑保持不变
                switch (loopType)
                {
                    case LoopType.FixedCount:
                        return await ExecuteFixedCountLoopAsync(loopNode, context, cancellationToken, children, waitTimeMs, loopVariable);

                    case LoopType.Conditional:
                        return await ExecuteConditionalLoopAsync(loopNode, context, cancellationToken, children, waitTimeMs, loopVariable);

                    case LoopType.Infinite:
                        return await ExecuteInfiniteLoopAsync(loopNode, context, cancellationToken, children, waitTimeMs, loopVariable);

                    default:
                        _logger.LogWarning("未识别的 LoopType: {Type}，按固定次数1次处理", loopType);
                        return await ExecuteFixedCountLoopAsync(loopNode, context, cancellationToken, children, waitTimeMs, loopVariable);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "循环命令执行失败: {NodeName}", loopNode.DisplayName);
                throw;
            }
        }

        /// <summary>
        /// 引擎内部读取 ExecutionMode 的兼容方法（不污染已有的 ReadLoopType / SafeToInt 命名）
        /// </summary>
        private static ExecutionMode ReadExecutionModeForEngine(object val)
        {
            if (val is ExecutionMode em) return em;
            if (val is string s && Enum.TryParse<ExecutionMode>(s, true, out var p)) return p;
            try
            {
                var i = Convert.ToInt32(val);
                if (i >= 0 && i <= 1) return (ExecutionMode)i;
            }
            catch { }
            return ExecutionMode.Synchronous;
        }

        /// <summary>
        /// 执行固定次数循环
        /// </summary>
        private async Task<int> ExecuteFixedCountLoopAsync(CommandNode loopNode, FlowExecutionContext context,
            CancellationToken cancellationToken, List<CommandNode> children, int waitTimeMs, string loopVariable)
        {
            // 读取 LoopCount（支持变量/字符串）
            object countRaw = loopNode.LogicCommand.Properties?.GetValueOrDefault("LoopCount", 1);
            int loopCount = 1;

            // 将 LoopCount 当作表达式/变量名解析
            if (countRaw != null)
            {
                var countVal = ResolveOperandValue(countRaw.ToString(), context, loopNode);
                if (countVal is int i) loopCount = i;
                else if (countVal is double d) loopCount = (int)d;
                else if (!int.TryParse(countVal?.ToString() ?? "1", out loopCount))
                    loopCount = 1;
            }

            loopCount = Math.Max(0, loopCount);
            _logger.LogDebug("固定次数循环: 次数={LoopCount}, 间隔={WaitTime}ms, 变量={LoopVariable}",
                loopCount, waitTimeMs, loopVariable);

           
            // 预调整总节点数（避免进度超过100%）
            if (loopCount > 1 && children.Count > 0)
            {
                var extraExecutions = (loopCount - 1) * CalculateTotalNodes(children);
                var currentProgress = _currentProgress; // 获取本地引用
                if (currentProgress != null)
                {
                    currentProgress.AddTotalNodes(extraExecutions);
                    _logger.LogDebug("进度总量调整: +{Extra} → Total={Total}", extraExecutions, currentProgress.TotalNodes);
                }
            }
            for (int i = 0; i < loopCount; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _pauseEvent.Wait(cancellationToken);

                context.SetVariable(loopVariable, i);
                _logger.LogDebug("固定次数循环第 {Current}/{Total} 次，{Variable}={Value}",
                    i + 1, loopCount, loopVariable, i);

                // 执行子节点
                foreach (var childNode in children)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    _pauseEvent.Wait(cancellationToken);

                    await ExecuteNodeWithProgressAsync(childNode, context, cancellationToken);
                    // 实验进度已在 ExecuteNodeWithProgressAsync 内节流写库,此处原来的
                    // 「立即更新」是纯重复的 MySQL 往返,循环越快拖得越狠,已移除
                }

                // 循环间隔等待（最后一次循环不需要等待）
                if (i < loopCount - 1 && waitTimeMs > 0)
                {
                    _logger.LogDebug("循环间隔等待 {WaitTime}ms", waitTimeMs);
                    await Task.Delay(waitTimeMs, cancellationToken);
                }
            }

            _logger.LogDebug("固定次数循环完成: {NodeName}, 执行次数: {Count}", loopNode.DisplayName, loopCount);
            return loopCount;
        }

        /// <summary>
        /// 执行条件循环（支持 Do-While 和 While，支持容差比较）
        /// </summary>
        private async Task<int> ExecuteConditionalLoopAsync(CommandNode loopNode, FlowExecutionContext context,
            CancellationToken cancellationToken, List<CommandNode> children, int waitTimeMs, string loopVariable)
        {
            // 读取三元条件和Do-While开关
            var leftOperand = loopNode.LogicCommand.Properties?.GetValueOrDefault("LeftOperand", "A")?.ToString() ?? "A";
            var operatorValue = loopNode.LogicCommand.Properties?.GetValueOrDefault("Operator", ComparisonOperator.Equal);
            var rightOperand = loopNode.LogicCommand.Properties?.GetValueOrDefault("RightOperand", "B")?.ToString() ?? "B";
            var isDoWhile = SafeToBool(loopNode.LogicCommand.Properties?.GetValueOrDefault("IsDoWhile", false), false);

            // ★ 新增：读取容差配置
            var toleranceModeObj = loopNode.LogicCommand.Properties?.GetValueOrDefault("ToleranceMode", ToleranceMode.None);
            var toleranceMode = ReadToleranceModeForEngine(toleranceModeObj);
            var toleranceValueObj = loopNode.LogicCommand.Properties?.GetValueOrDefault("ToleranceValue", 0.0);
            TryToDouble(toleranceValueObj, out double toleranceValue);

            _logger.LogDebug("条件循环参数: Left={Left}, Op={Op}, Right={Right}, DoWhile={DoWhile}, 容差模式={TolMode}, 容差值={TolVal}, 间隔={WaitTime}ms",
                leftOperand, operatorValue, rightOperand, isDoWhile, toleranceMode, toleranceValue, waitTimeMs);

            // 安全阈值，防止死循环
            const int maxIterations = 100000;
            int iteration = 0;

            if (isDoWhile)
            {
                // Do-While 循环：先执行，后判断
                do
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    _pauseEvent.Wait(cancellationToken);

                    // 动态累加总节点
                    if (children.Count > 0)
                    {
                        lock (_currentProgress.Lock)
                        {
                            _currentProgress.TotalNodes += CalculateTotalNodes(children);
                        }
                    }

                    context.SetVariable(loopVariable, iteration);
                    _logger.LogDebug("Do-While循环第 {Current} 次，{Var}={Val}，执行子节点",
                        iteration + 1, loopVariable, iteration);

                    // 执行子节点
                    foreach (var childNode in children)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        _pauseEvent.Wait(cancellationToken);
                        await ExecuteNodeWithProgressAsync(childNode, context, cancellationToken);

                        // 立即更新实验进度(节流,重复写由节流器吸收)
                        var currentProgress = _currentProgress;
                        if (!string.IsNullOrEmpty(_currentExperimentId) && currentProgress != null)
                        {
                            try
                            {
                                await WriteExperimentProgressThrottledAsync("Do-While循环").ConfigureAwait(false);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "Do-While循环中更新实验进度失败");
                            }
                        }
                    }

                    iteration++;

                    // 循环间隔等待
                    if (waitTimeMs > 0)
                    {
                        _logger.LogDebug("Do-While循环间隔等待 {WaitTime}ms", waitTimeMs);
                        await Task.Delay(waitTimeMs, cancellationToken);
                    }

                    // 检查是否继续循环（带容差）
                    bool shouldContinue = EvaluateLoopTripletWithTolerance(
                        leftOperand, operatorValue, rightOperand,
                        toleranceMode, toleranceValue,
                        context, loopNode);

                    if (!shouldContinue)
                    {
                        _logger.LogDebug("Do-While循环第 {Current} 次后，条件为假，退出循环", iteration);
                        break;
                    }

                } while (iteration < maxIterations);
            }
            else
            {
                // While 循环：先判断，后执行
                while (iteration < maxIterations)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    _pauseEvent.Wait(cancellationToken);

                    // 先判断条件（带容差）
                    bool shouldContinue = EvaluateLoopTripletWithTolerance(
                        leftOperand, operatorValue, rightOperand,
                        toleranceMode, toleranceValue,
                        context, loopNode);

                    if (!shouldContinue)
                    {
                        _logger.LogDebug("While循环第 {Current} 次，条件为假，退出循环", iteration + 1);
                        break;
                    }

                    // 动态累加总节点
                    if (children.Count > 0)
                    {
                        lock (_currentProgress.Lock)
                        {
                            _currentProgress.TotalNodes += CalculateTotalNodes(children);
                        }
                    }

                    context.SetVariable(loopVariable, iteration);
                    _logger.LogDebug("While循环第 {Current} 次，{Var}={Val}，条件为真，执行子节点",
                        iteration + 1, loopVariable, iteration);

                    // 执行子节点
                    foreach (var childNode in children)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        _pauseEvent.Wait(cancellationToken);
                        await ExecuteNodeWithProgressAsync(childNode, context, cancellationToken);

                        // 立即更新实验进度(节流,重复写由节流器吸收)
                        await WriteExperimentProgressThrottledAsync("While循环").ConfigureAwait(false);
                    }

                    iteration++;

                    // 循环间隔等待
                    if (waitTimeMs > 0)
                    {
                        _logger.LogDebug("While循环间隔等待 {WaitTime}ms", waitTimeMs);
                        await Task.Delay(waitTimeMs, cancellationToken);
                    }
                }
            }

            if (iteration >= maxIterations)
                _logger.LogWarning("条件循环达到最大迭代次数 {MaxIterations}，强制退出", maxIterations);

            _logger.LogDebug("条件循环完成: 迭代次数={Count}, 模式={Mode}", iteration, isDoWhile ? "Do-While" : "While");
            return iteration;
        }

        /// <summary>
        /// 使用三元字段（LeftOperand/Operator/RightOperand）评估循环条件，支持容差。
        /// 仅当操作符为 ==/!= 且容差模式不为 None 时启用容差比较，其它情况走原有逻辑。
        /// </summary>
        private bool EvaluateLoopTripletWithTolerance(
            string leftOperand, object operatorValue, string rightOperand,
            ToleranceMode toleranceMode, double toleranceValue,
            FlowExecutionContext context, CommandNode loopNode)
        {
            try
            {
                var leftVal = ResolveOperandValue(leftOperand, context, loopNode);
                var rightVal = ResolveOperandValue(rightOperand, context, loopNode);
                var opStr = ReadOperatorString(operatorValue);

                bool isEqualityOp = opStr == "Equal" || opStr == "NotEqual";

                if (isEqualityOp && toleranceMode != ToleranceMode.None)
                {
                    // 走容差比较
                    bool isEqual = opStr == "Equal";
                    bool result = EvaluateEqualityWithTolerance(leftVal, rightVal, isEqual, toleranceMode, toleranceValue);

                    _logger.LogDebug("循环条件评估（带容差）: {Left}({LeftVal}) {Op} {Right}({RightVal}), 容差={TolMode}/{TolVal} → {Result}",
                        leftOperand, leftVal, opStr, rightOperand, rightVal, toleranceMode, toleranceValue, result);
                    return result;
                }
                else
                {
                    // 走原有逻辑
                    bool result = EvaluateCondition(leftVal, opStr, rightVal);

                    _logger.LogDebug("循环条件评估: {Left}({LeftVal}) {Op} {Right}({RightVal}) → {Result}",
                        leftOperand, leftVal, opStr, rightOperand, rightVal, result);
                    return result;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "循环条件评估失败: {Left} {Op} {Right}", leftOperand, operatorValue, rightOperand);
                return false;
            }
        }
        /// <summary>
        /// 执行无限循环
        /// </summary>
        private async Task<int> ExecuteInfiniteLoopAsync(CommandNode loopNode, FlowExecutionContext context,
            CancellationToken cancellationToken, List<CommandNode> children, int waitTimeMs, string loopVariable)
        {
            // 无限循环，直到 Stop/Cancel。设安全阈值以免极端情况下长时间跑满进度。
            const int maxIterations = 1000000;
            int iteration = 0;

            _logger.LogDebug("无限循环开始，间隔={WaitTime}ms", waitTimeMs);

            while (State != FlowExecutionState.Stopping && iteration < maxIterations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _pauseEvent.Wait(cancellationToken);

                // 动态累加总节点
                if (children.Count > 0)
                {
                    lock (_currentProgress.Lock)
                    {
                        _currentProgress.TotalNodes += CalculateTotalNodes(children);
                    }
                }

                context.SetVariable(loopVariable, iteration);
                _logger.LogDebug("无限循环第 {Current} 次，{Var}={Val}", iteration + 1, loopVariable, iteration);

                // 执行子节点
                foreach (var childNode in children)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    _pauseEvent.Wait(cancellationToken);
                    await ExecuteNodeWithProgressAsync(childNode, context, cancellationToken);
                }

                iteration++;

                // 循环间隔等待
                if (waitTimeMs > 0)
                {
                    _logger.LogDebug("无限循环间隔等待 {WaitTime}ms", waitTimeMs);
                    await Task.Delay(waitTimeMs, cancellationToken);
                }
            }

            if (iteration >= maxIterations)
                _logger.LogWarning("无限循环达到安全上限 {MaxIterations} 次，自动退出", maxIterations);

            _logger.LogDebug("无限循环结束: 迭代次数={Count}", iteration);
            return iteration;
        }

        /// <summary>
        /// 安全转换为布尔值
        /// </summary>
        private static bool SafeToBool(object val, bool defaultValue)
        {
            try
            {
                if (val is bool b) return b;
                if (val is string s)
                {
                    if (bool.TryParse(s, out var result)) return result;
                    // 兼容数字字符串：1 = true, 0 = false
                    if (s == "1") return true;
                    if (s == "0") return false;
                    // 兼容中文
                    if (s.Equals("是", StringComparison.OrdinalIgnoreCase) ||
                        s.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
                        s.Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
                    if (s.Equals("否", StringComparison.OrdinalIgnoreCase) ||
                        s.Equals("no", StringComparison.OrdinalIgnoreCase) ||
                        s.Equals("false", StringComparison.OrdinalIgnoreCase)) return false;
                }
                if (val is int i) return i != 0;
                if (val is double d) return d != 0.0;
                if (val is float f) return f != 0.0f;
            }
            catch { }
            return defaultValue;
        }

        /// <summary>
        /// 使用三元字段（LeftOperand/Operator/RightOperand）评估循环条件
        /// </summary>
        private bool EvaluateLoopTriplet(string leftOperand, object operatorValue, string rightOperand,
            FlowExecutionContext context, CommandNode loopNode)
        {
            try
            {
                // 解析左右操作数的值
                var leftVal = ResolveOperandValue(leftOperand, context, loopNode);
                var rightVal = ResolveOperandValue(rightOperand, context, loopNode);
                var opStr = ReadOperatorString(operatorValue);

                _logger.LogDebug("循环条件评估: {Left}({LeftVal}) {Op} {Right}({RightVal})",
                    leftOperand, leftVal, opStr, rightOperand, rightVal);

                bool result = EvaluateCondition(leftVal, opStr, rightVal);

                _logger.LogDebug("循环条件结果: {Result}", result);
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "循环条件评估失败: {Left} {Op} {Right}", leftOperand, operatorValue, rightOperand);
                return false;
            }
        }

        /// <summary>
        /// 改进的操作符字符串读取
        /// </summary>
        private static string ReadOperatorString(object val)
        {
            // 1. 如果是枚举，直接映射
            if (val is ComparisonOperator op)
            {
                return op switch
                {
                    ComparisonOperator.Equal => "Equal",
                    ComparisonOperator.NotEqual => "NotEqual",
                    ComparisonOperator.GreaterThan => "GreaterThan",
                    ComparisonOperator.GreaterThanOrEqual => "GreaterThanOrEqual",
                    ComparisonOperator.LessThan => "LessThan",
                    ComparisonOperator.LessThanOrEqual => "LessThanOrEqual",
                    ComparisonOperator.Contains => "Contains",
                    ComparisonOperator.NotContains => "NotContains",
                    _ => "Equal"
                };
            }

            // 2. 转为字符串
            var s = val?.ToString() ?? "Equal";

            // 3. 先处理符号
            s = s switch
            {
                "==" => "Equal",
                "!=" => "NotEqual",
                ">" => "GreaterThan",
                ">=" => "GreaterThanOrEqual",
                "<" => "LessThan",
                "<=" => "LessThanOrEqual",
                _ => s
            };

            // 4. 如果还不是标准名称，尝试解析为整数（枚举索引）
            if (!IsStandardOperatorName(s))
            {
                if (int.TryParse(s, out int index) && Enum.IsDefined(typeof(ComparisonOperator), index))
                {
                    var opFromIndex = (ComparisonOperator)index;
                    return ReadOperatorString(opFromIndex); // 递归调用，复用逻辑
                }
            }

            // 5. 如果无法识别，返回原值或默认
            return IsStandardOperatorName(s) ? s : "Equal";
        }

        // 辅助方法：判断是否是有效的操作符名称
        private static bool IsStandardOperatorName(string name)
        {
            return name is "Equal" or "NotEqual" or "GreaterThan" or "GreaterThanOrEqual"
                or "LessThan" or "LessThanOrEqual" or "Contains" or "NotContains";
        }

        /// <summary>
        /// 安全转换为整数
        /// </summary>
        private static int SafeToInt(object val, int defaultValue)
        {
            try
            {
                if (val is int i) return i;
                if (val is double d) return (int)d;
                if (val is float f) return (int)f;
                if (val is decimal dec) return (int)dec;
                if (val is string s && int.TryParse(s, out var parsed)) return parsed;
                if (val != null)
                {
                    var conv = Convert.ToInt32(val, CultureInfo.InvariantCulture);
                    return conv;
                }
            }
            catch { }
            return defaultValue;
        }
        private static LoopType ReadLoopType(object val)
        {
            if (val is LoopType lt) return lt;
            if (val is string s && Enum.TryParse<LoopType>(s, true, out var parsed)) return parsed;
            try
            {
                var i = Convert.ToInt32(val, CultureInfo.InvariantCulture);
                return (LoopType)Enum.ToObject(typeof(LoopType), i);
            }
            catch { }
            return LoopType.FixedCount;
        }

        private async Task<object> ExecuteParallelCommandAsync(CommandNode parallelNode, FlowExecutionContext context, CancellationToken cancellationToken)
        {
            _logger.LogDebug("执行并行命令: {NodeName}", parallelNode.DisplayName);

            try
            {
                // 读取属性并做容错
                var maxParallelStr = parallelNode.LogicCommand.Properties?.GetValueOrDefault("MaxParallelCount", "4")?.ToString() ?? "4";
                if (!int.TryParse(maxParallelStr, out var maxParallel)) maxParallel = 4;
                maxParallel = Math.Max(1, maxParallel);

                var waitStrategyStr = parallelNode.LogicCommand.Properties?.GetValueOrDefault("WaitStrategy", "NoWait")?.ToString() ?? "NoWait";
                if (!Enum.TryParse(waitStrategyStr, true, out ParallelWaitStrategy waitStrategy))
                {
                    waitStrategy = ParallelWaitStrategy.NoWait;
                }

                var timeoutStr = parallelNode.LogicCommand.Properties?.GetValueOrDefault("TimeoutSeconds", "0")?.ToString() ?? "0";
                if (!double.TryParse(timeoutStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var timeoutSeconds))
                {
                    timeoutSeconds = 0;
                }

                var branchGroups = parallelNode.Children?
                    .GroupBy(child => child.GetExecutionProperty<int>("BranchIndex"))
                    .OrderBy(g => g.Key)
                    .ToList() ?? new List<IGrouping<int, CommandNode>>();

                _logger.LogDebug("并行执行 {BranchCount} 个分支，最大并行数: {MaxParallel}，等待策略: {Strategy}，超时: {Timeout}s",
                    branchGroups.Count, maxParallel, waitStrategy, timeoutSeconds);

                // *** 修改：创建更好的取消令牌链 ***
                var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

                // *** 新增：监听主取消令牌和停止状态 ***
                var stopMonitorCts = new CancellationTokenSource();
                var combinedCts = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken, linkedCts.Token, stopMonitorCts.Token);

                var throttler = new SemaphoreSlim(maxParallel);
                var branchTasks = new List<Task>();

                // *** 新增：启动停止监控任务 ***
                var stopMonitorTask = Task.Run(async () =>
                {
                    try
                    {
                        while (!combinedCts.Token.IsCancellationRequested)
                        {
                            // 检查流程是否被停止
                            if (State == FlowExecutionState.Stopping)
                            {
                                _logger.LogInformation("检测到流程停止信号，取消所有并行分支");
                                stopMonitorCts.Cancel();
                                return;
                            }

                            await Task.Delay(100, combinedCts.Token);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        // 正常取消，忽略
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "停止监控任务异常");
                    }
                }, combinedCts.Token);

                foreach (var branchGroup in branchGroups)
                {
                    var branchIndex = branchGroup.Key;
                    var branchNodes = branchGroup.ToList();

                    var task = Task.Run(async () =>
                    {
                        await throttler.WaitAsync(combinedCts.Token).ConfigureAwait(false);
                        try
                        {
                            _logger.LogDebug("开始执行并行分支 {BranchIndex}，包含 {Count} 个节点", branchIndex, branchNodes.Count);

                            foreach (var childNode in branchNodes)
                            {
                                // *** 修改：优先检查取消 ***
                                combinedCts.Token.ThrowIfCancellationRequested();

                                // 检查流程状态
                                if (State == FlowExecutionState.Stopping)
                                {
                                    _logger.LogInformation("并行分支 {BranchIndex} 检测到停止信号，退出执行", branchIndex);
                                    return;
                                }

                                // *** 新增：检查用户交互暂停和常规暂停 ***
                                try
                                {
                                    _pauseEvent.Wait(combinedCts.Token);
                                    _userInteractionPauseEvent.Wait(combinedCts.Token);
                                }
                                catch (OperationCanceledException)
                                {
                                    _logger.LogDebug("并行分支 {BranchIndex} 在等待恢复时被取消", branchIndex);
                                    return;
                                }

                                await ExecuteNodeWithProgressAsync(childNode, context, combinedCts.Token).ConfigureAwait(false);

                                // *** 修改：增加异常处理的实验进度更新(节流,重复写由节流器吸收) ***
                                var currentProgress = _currentProgress; // 获取本地引用
                                if (!string.IsNullOrEmpty(_currentExperimentId) && currentProgress != null)
                                {
                                    try
                                    {
                                        await WriteExperimentProgressThrottledAsync("并行分支").ConfigureAwait(false);
                                    }
                                    catch (Exception ex)
                                    {
                                        _logger.LogError(ex, "并行分支更新实验进度失败");
                                    }
                                }
                            }

                            _logger.LogDebug("并行分支 {BranchIndex} 执行完成", branchIndex);
                        }
                        catch (OperationCanceledException)
                        {
                            _logger.LogInformation("并行分支 {BranchIndex} 被取消", branchIndex);
                            throw; // 重新抛出以便上层处理
                        }
                        finally
                        {
                            throttler.Release();
                        }
                    }, combinedCts.Token);

                    branchTasks.Add(task);
                }

                // 根据等待策略处理
                if (waitStrategy == ParallelWaitStrategy.NoWait)
                {
                    // NoWait策略：将任务添加到后台任务列表，主流程继续
                    _logger.LogInformation("并行节点 {NodeName} 使用NoWait策略，{Count} 个分支将在后台执行",
                        parallelNode.DisplayName, branchTasks.Count);

                    lock (_backgroundTasksLock)
                    {
                        _backgroundTasks.AddRange(branchTasks);
                    }

                    // 添加任务完成后的清理
                    _ = Task.WhenAll(branchTasks).ContinueWith(t =>
                    {
                        // *** 新增：清理取消令牌 ***
                        try
                        {
                            stopMonitorCts?.Cancel();
                            stopMonitorCts?.Dispose();
                            combinedCts?.Dispose();
                            linkedCts?.Dispose();
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "清理并行任务资源失败");
                        }

                        lock (_backgroundTasksLock)
                        {
                            foreach (var task in branchTasks)
                            {
                                _backgroundTasks.Remove(task);
                            }
                        }

                        if (t.IsFaulted)
                        {
                            _logger.LogError(t.Exception, "后台并行任务执行失败");
                        }
                        else if (t.IsCanceled)
                        {
                            _logger.LogInformation("后台并行任务被取消");
                        }
                        else
                        {
                            _logger.LogDebug("并行节点 {NodeName} 的后台任务全部完成", parallelNode.DisplayName);
                        }
                    }, TaskContinuationOptions.ExecuteSynchronously);

                    return branchGroups.Count;
                }

                // WaitAll 或 WaitAny 策略
                try
                {
                    Task timeoutTask = null;
                    if (timeoutSeconds > 0)
                    {
                        timeoutTask = Task.Delay(TimeSpan.FromSeconds(timeoutSeconds), combinedCts.Token);
                    }

                    switch (waitStrategy)
                    {
                        case ParallelWaitStrategy.WaitAll:
                            {
                                _logger.LogDebug("并行节点 {NodeName} 使用WaitAll策略，等待所有分支完成", parallelNode.DisplayName);

                                var allTask = Task.WhenAll(branchTasks);

                                if (timeoutTask != null)
                                {
                                    var completed = await Task.WhenAny(allTask, timeoutTask).ConfigureAwait(false);

                                    // *** 修改：优先检查取消状态 ***
                                    if (combinedCts.Token.IsCancellationRequested)
                                    {
                                        _logger.LogInformation("并行命令被取消（WaitAll）");
                                        throw new OperationCanceledException("并行命令执行被取消", combinedCts.Token);
                                    }

                                    if (completed == timeoutTask && !combinedCts.Token.IsCancellationRequested)
                                    {
                                        _logger.LogWarning("并行命令超时（WaitAll），TimeoutSeconds={Timeout}", timeoutSeconds);
                                        linkedCts.Cancel(); // 取消未完成的任务
                                        throw new TimeoutException($"并行命令等待全部分支超时（{timeoutSeconds}s）");
                                    }
                                }

                                await allTask.ConfigureAwait(false); // 传播异常
                                break;
                            }

                        case ParallelWaitStrategy.WaitAny:
                            {
                                _logger.LogDebug("并行节点 {NodeName} 使用WaitAny策略，等待任一分支完成", parallelNode.DisplayName);

                                if (branchTasks.Count == 0) return 0;

                                var anyTask = Task.WhenAny(branchTasks);

                                if (timeoutTask != null)
                                {
                                    var completed = await Task.WhenAny(anyTask, timeoutTask).ConfigureAwait(false);

                                    // *** 修改：优先检查取消状态 ***
                                    if (combinedCts.Token.IsCancellationRequested)
                                    {
                                        _logger.LogInformation("并行命令被取消（WaitAny）");
                                        throw new OperationCanceledException("并行命令执行被取消", combinedCts.Token);
                                    }

                                    if (completed == timeoutTask && !combinedCts.Token.IsCancellationRequested)
                                    {
                                        _logger.LogWarning("并行命令超时（WaitAny），TimeoutSeconds={Timeout}", timeoutSeconds);
                                        linkedCts.Cancel(); // 取消未完成的任务
                                        throw new TimeoutException($"并行命令等待任一分支超时（{timeoutSeconds}s）");
                                    }
                                }

                                var firstCompleted = await anyTask.ConfigureAwait(false);

                                // WaitAny完成后，其他任务继续在后台执行
                                var remainingTasks = branchTasks.Where(t => t != firstCompleted).ToList();
                                if (remainingTasks.Count > 0)
                                {
                                    _logger.LogDebug("WaitAny策略：首个分支已完成，{Count} 个分支继续在后台执行", remainingTasks.Count);
                                    lock (_backgroundTasksLock)
                                    {
                                        _backgroundTasks.AddRange(remainingTasks);
                                    }

                                    // 清理后台任务
                                    _ = Task.WhenAll(remainingTasks).ContinueWith(t =>
                                    {
                                        lock (_backgroundTasksLock)
                                        {
                                            foreach (var task in remainingTasks)
                                            {
                                                _backgroundTasks.Remove(task);
                                            }
                                        }
                                    }, TaskContinuationOptions.ExecuteSynchronously);
                                }
                                break;
                            }
                    }

                    _logger.LogDebug("并行命令执行完成: {NodeName}", parallelNode.DisplayName);
                    return branchGroups.Count;
                }
                finally
                {
                    // *** 新增：确保资源清理 ***
                    try
                    {
                        stopMonitorCts?.Cancel();
                        stopMonitorCts?.Dispose();
                        combinedCts?.Dispose();
                        linkedCts?.Dispose();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "清理并行任务资源失败");
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || State == FlowExecutionState.Stopping)
            {
                _logger.LogInformation("并行命令被正常取消: {NodeName}", parallelNode.DisplayName);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "并行命令执行失败: {NodeName}", parallelNode.DisplayName);
                throw;
            }
        }

        private async Task<object> ExecuteWaitCommandAsync(CommandNode waitNode, FlowExecutionContext context, CancellationToken cancellationToken)
        {
            _logger.LogDebug("执行等待命令: {NodeName}", waitNode.DisplayName);
            try
            {
                // 检查是否是条件等待
                var leftVariable = waitNode.LogicCommand.Properties?.GetValueOrDefault("ConditionLeftVariable", "")?.ToString() ?? "";

                if (!string.IsNullOrWhiteSpace(leftVariable))
                {
                    // 条件等待
                    return await ExecuteConditionWaitAsync(waitNode, context, cancellationToken);
                }
                else
                {
                    // 固定时间等待
                    return await ExecuteFixedTimeWaitAsync(waitNode, context, cancellationToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "等待命令执行失败: {NodeName}", waitNode.DisplayName);
                throw;
            }
        }

        /// <summary>
        /// 执行固定时间等待（带倒计时对话框，非模态）
        /// </summary>
        private async Task<object> ExecuteFixedTimeWaitAsync(CommandNode waitNode, FlowExecutionContext context, CancellationToken cancellationToken)
        {
            var waitTimeStr = waitNode.LogicCommand.Properties?.GetValueOrDefault("WaitTime", "5")?.ToString() ?? "5";
            var timeUnit = waitNode.LogicCommand.Properties?.GetValueOrDefault("TimeUnit", "Seconds")?.ToString() ?? "Seconds";

            // 解析等待时间（支持变量）
            var waitTimeValue = ResolveOperandValue(waitTimeStr, context, waitNode);
            double waitTime = 5;
            if (waitTimeValue != null && double.TryParse(waitTimeValue.ToString(), out var parsed))
                waitTime = parsed;

            // 转换为TimeSpan
            TimeSpan waitTimeSpan = timeUnit switch
            {
                "Milliseconds" => TimeSpan.FromMilliseconds(waitTime),
                "Seconds" => TimeSpan.FromSeconds(waitTime),
                "Minutes" => TimeSpan.FromMinutes(waitTime),
                "Hours" => TimeSpan.FromHours(waitTime),
                _ => TimeSpan.FromSeconds(waitTime)
            };

            _logger.LogDebug("固定时间等待: {WaitTime} {Unit}", waitTime, timeUnit);

            // 诊断：把所有相关属性都打出来
            var props = waitNode.LogicCommand.Properties;
            _logger.LogInformation(">>> [WaitNode] 节点属性诊断:");
            _logger.LogInformation(">>>   WaitForStability(原始) = {Raw}, 类型={Type}",
                props?.GetValueOrDefault("WaitForStability"),
                props?.GetValueOrDefault("WaitForStability")?.GetType()?.Name ?? "null");
            _logger.LogInformation(">>>   StabilityDeviceIds(原始) = {Raw}, 类型={Type}",
                props?.GetValueOrDefault("StabilityDeviceIds"),
                props?.GetValueOrDefault("StabilityDeviceIds")?.GetType()?.Name ?? "null");
            _logger.LogInformation(">>>   StabilityTimeoutSeconds(原始) = {Raw}",
                props?.GetValueOrDefault("StabilityTimeoutSeconds"));

            // 判断分支
            bool waitForStability = SafeToBool(
                props?.GetValueOrDefault("WaitForStability", false), false);

            _logger.LogInformation(">>> 解析后 waitForStability = {Result}", waitForStability);

            if (waitForStability)
            {
                _logger.LogInformation(">>> 走等稳定 + 停留分支");
                return await ExecuteWaitForStabilityAndDelayAsync(waitNode, waitTimeSpan, cancellationToken);
            }

            _logger.LogInformation(">>> 走原倒计时弹框分支（非模态）");

            // 弹窗标题优先用用户为该等待节点设置的"描述"；未自定义(空或仍为默认占位)时回退到"等待 X 单位"
            var waitDesc = waitNode.LogicCommand?.Properties?.GetValueOrDefault("Description")?.ToString();
            string waitDialogInfo = (!string.IsNullOrWhiteSpace(waitDesc) && waitDesc != "等待指定时间")
                ? waitDesc
                : $"等待 {waitTime} {GetTimeUnitDisplay(timeUnit)}";

            // *** 开始用户交互，暂停其他分支（UI 非模态，用户仍可操作界面） ***
            BeginUserInteraction(waitNode, "FixedTimeWait", waitDialogInfo);

            CountdownDialog countdownDialog = null;
            var countdownDone = new TaskCompletionSource<CountdownResult>();

            try
            {
                // 在UI线程以【非模态】方式显示对话框
                Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        var mainWindow = Application.Current.MainWindow;
                        countdownDialog = new CountdownDialog(waitTimeSpan, waitDialogInfo)
                        {
                            Owner = mainWindow
                        };

                        // 关闭时通过标志读取结果（非模态没有 DialogResult 返回值）
                        countdownDialog.Closed += (s, e) =>
                        {
                            countdownDone.TrySetResult(new CountdownResult
                            {
                                IsCompleted = countdownDialog.IsCompleted,
                                IsSkipped = countdownDialog.IsSkipped,
                                IsStopped = countdownDialog.IsStopped,
                                ActualWaitTime = waitTimeSpan.TotalMilliseconds
                            });
                        };

                        countdownDialog.Show();          // 非模态
                        countdownDialog.StartCountdown(); // 启动倒计时
                    }
                    catch (Exception ex)
                    {
                        countdownDone.TrySetException(ex);
                    }
                }));

                // 等待对话框关闭（倒计时结束 / 跳过 / 停止）
                // 同时响应流程取消：取消时主动关闭对话框
                using (cancellationToken.Register(() =>
                {
                    Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        try { if (countdownDialog?.IsVisible == true) countdownDialog.Close(); } catch { }
                    }));
                }))
                {
                    var result = await countdownDone.Task;

                    // 检查用户操作
                    if (result.IsStopped)
                    {
                        _logger.LogInformation("用户停止了固定时间等待");
                        throw new OperationCanceledException("用户停止了等待流程");
                    }

                    if (result.IsSkipped)
                    {
                        _logger.LogInformation("用户跳过了固定时间等待");
                        return 0; // 跳过等待，立即返回
                    }

                    _logger.LogDebug("固定时间等待完成: {NodeName}", waitNode.DisplayName);
                    return result.ActualWaitTime;
                }
            }
            finally
            {
                // 兜底：确保对话框被关闭
                Application.Current?.Dispatcher.Invoke(() =>
                {
                    try { if (countdownDialog?.IsVisible == true) countdownDialog.Close(); } catch { }
                });

                // *** 结束用户交互，恢复其他分支 ***
                EndUserInteraction(waitNode, "FixedTimeWait");
            }
        }

        // ★ 等稳定 + 停留分支（非模态）
        private async Task<object> ExecuteWaitForStabilityAndDelayAsync(
            CommandNode waitNode, TimeSpan delayAfterStable, CancellationToken cancellationToken)
        {
            var deviceIds = ParseDeviceIdsList(waitNode.LogicCommand.Properties);
            var stabilityTimeoutSec = SafeToInt(
                waitNode.LogicCommand.Properties?.GetValueOrDefault("StabilityTimeoutSeconds", 600), 600);

            _logger.LogInformation(">>> [Stability] 解析到 {Count} 个 DeviceId: {Ids}",
                deviceIds.Count, string.Join(", ", deviceIds));

            List<CanvasDeviceViewModel> canvasDevices = null;
            _eventAggregator?.GetEvent<RequestCanvasDevicesEvent>()?.Publish(list => canvasDevices = list);

            _logger.LogInformation(">>> [Stability] 画布共 {Count} 个设备", canvasDevices?.Count ?? 0);
            if (canvasDevices != null)
            {
                foreach (var cd in canvasDevices)
                {
                    _logger.LogInformation(">>>   画布设备: DeviceId={Id}, Name={Name}, Type={Type}",
                        cd.DeviceId, cd.Device?.Name, cd.Device?.GetType().Name);
                }
            }

            var devices = canvasDevices?
                .Where(d => deviceIds.Contains(d.DeviceId))
                .Select(d => d.Device)
                .Where(d => d != null)
                .ToList()
                ?? new List<IDevice>();

            _logger.LogInformation(">>> [Stability] 匹配到 {Count} 个设备实例", devices.Count);

            if (!devices.Any())
            {
                _logger.LogWarning(">>> [Stability] 未找到任何匹配设备，将直接停留计时");
            }
            else
            {
                foreach (var dev in devices)
                {
                    try
                    {
                        var st = dev.GetCurrentState();
                        if (st?.States != null)
                        {
                            var hasIsStable = st.States.ContainsKey("IsStable");
                            var isStableVal = hasIsStable ? st.States["IsStable"] : "[字段不存在]";
                            _logger.LogInformation(">>>   设备 {Name} 当前状态: 字段数={N}, 含IsStable={H}, IsStable值={V}",
                                dev.Name, st.States.Count, hasIsStable, isStableVal);
                            _logger.LogInformation(">>>     所有字段: {Keys}", string.Join(", ", st.States.Keys));
                        }
                        else
                        {
                            _logger.LogWarning(">>>   设备 {Name} GetCurrentState 返回 null 或 States 为空", dev.Name);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, ">>>   设备 {Name} 取状态异常", dev.Name);
                    }
                }
            }

            var deviceCountText = devices.Count == 0 ? "未选择设备" : $"{devices.Count} 个设备";

            // 流程逻辑上仍然停在这里，暂停其他并行分支；但 UI 非模态，用户可操作界面
            BeginUserInteraction(waitNode, "WaitForStability", $"等待 {deviceCountText} 稳定后停留");

            CountdownDialog stabilityDialog = null;
            var dialogReady = new TaskCompletionSource<bool>();

            try
            {
                // 在 UI 线程创建并以【非模态】方式显示对话框（先进入"稳定等待"阶段）
                Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        stabilityDialog = new CountdownDialog(delayAfterStable, $"等待 {deviceCountText} 稳定")
                        {
                            Owner = Application.Current.MainWindow
                        };
                        stabilityDialog.EnterStabilityPhase(deviceCountText);
                        stabilityDialog.Show();   // 非模态，不阻塞 UI 线程
                        dialogReady.TrySetResult(true);
                    }
                    catch (Exception ex)
                    {
                        dialogReady.TrySetException(ex);
                    }
                }));

                await dialogReady.Task;

                // ===== 阶段一：后台轮询设备稳定 =====
                var stabilityStart = DateTime.Now;
                int pollCount = 0;
                bool stabilized = devices.Count == 0; // 没有设备则直接视为已稳定

                while (!stabilized)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (stabilityDialog?.IsStopped == true)
                        throw new OperationCanceledException("用户停止了等待流程");
                    if (stabilityDialog?.IsSkipped == true)
                    {
                        _logger.LogInformation(">>> [Stability] 用户在稳定阶段跳过了节点");
                        return 0;
                    }

                    int stableCount = devices.Count(IsDeviceStable);
                    pollCount++;
                    var elapsedSec = (int)(DateTime.Now - stabilityStart).TotalSeconds;
                    _logger.LogDebug(">>> [Stability] 第{N}次轮询: {Stable}/{Total} 稳定",
                        pollCount, stableCount, devices.Count);

                    stabilityDialog?.UpdateStabilityProgress(stableCount, devices.Count, elapsedSec);

                    if (stableCount == devices.Count)
                    {
                        _logger.LogInformation(">>> [Stability] 所有设备已稳定（轮询{N}次）", pollCount);
                        stabilized = true;
                        break;
                    }

                    if ((DateTime.Now - stabilityStart).TotalSeconds > stabilityTimeoutSec)
                    {
                        var unstableNames = devices.Where(d => !IsDeviceStable(d)).Select(d => d.Name);
                        Application.Current?.Dispatcher.Invoke(() =>
                        {
                            try { if (stabilityDialog?.IsVisible == true) stabilityDialog.Close(); } catch { }
                        });
                        throw new TimeoutException(
                            $"等待设备稳定超时（{stabilityTimeoutSec}秒）：" + string.Join(", ", unstableNames));
                    }

                    await Task.Delay(1000, cancellationToken);
                }

                // ===== 阶段二：切换到倒计时，等待对话框自然关闭 =====
                _logger.LogInformation(">>> [Stability] 进入停留计时: {Delay}", delayAfterStable);

                var countdownDone = new TaskCompletionSource<CountdownResult>();
                Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        stabilityDialog.Closed += (s, e) =>
                        {
                            countdownDone.TrySetResult(new CountdownResult
                            {
                                IsCompleted = stabilityDialog.IsCompleted,   // 非模态：读标志而非 DialogResult
                                IsSkipped = stabilityDialog.IsSkipped,
                                IsStopped = stabilityDialog.IsStopped,
                                ActualWaitTime = delayAfterStable.TotalMilliseconds
                            });
                        };

                        stabilityDialog.EnterCountdownPhase(); // 内部启动计时器
                    }
                    catch (Exception ex)
                    {
                        countdownDone.TrySetException(ex);
                    }
                }));

                var result = await countdownDone.Task;

                if (result.IsStopped)
                {
                    _logger.LogInformation(">>> [Stability] 用户停止了停留计时");
                    throw new OperationCanceledException("用户停止了等待流程");
                }
                if (result.IsSkipped)
                {
                    _logger.LogInformation(">>> [Stability] 用户跳过了停留计时");
                    return 0;
                }

                _logger.LogInformation(">>> [Stability] 停留完成");
                return result.ActualWaitTime;
            }
            finally
            {
                Application.Current?.Dispatcher.Invoke(() =>
                {
                    try { if (stabilityDialog?.IsVisible == true) stabilityDialog.Close(); } catch { }
                });

                EndUserInteraction(waitNode, "WaitForStability");
            }
        }
        /// <summary>
        /// 判断设备是否处于稳定状态。
        /// 设备未实现 IsStable 字段时视为"已稳定"，避免卡死流程。
        /// </summary>
        private static bool IsDeviceStable(IDevice device)
        {
            if (device == null) return true;

            try
            {
                var state = device.GetCurrentState();
                if (state?.States == null) return true;

                if (!state.States.TryGetValue("IsStable", out var raw))
                    return true; // 没有这个字段 → 视为稳定，让流程继续

                if (raw is bool b) return b;

                // 兼容字符串 "true"/"false" 这种取值
                if (raw != null && bool.TryParse(raw.ToString(), out var parsed))
                    return parsed;

                return true; // 解析不出来也视为稳定，不卡流程
            }
            catch
            {
                return true; // 取状态失败也不卡流程
            }
        }
        private List<string> ParseDeviceIdsList(IDictionary<string, object> props)
        {
            if (props == null || !props.TryGetValue("StabilityDeviceIds", out var raw))
                return new List<string>();
            if (raw is List<string> list) return list;
            if (raw is IEnumerable<object> enumerable)
                return enumerable.Select(o => o?.ToString()).Where(s => !string.IsNullOrEmpty(s)).ToList();
            if (raw is string s) return s.Split(',').Select(x => x.Trim())
                                          .Where(x => !string.IsNullOrEmpty(x)).ToList();
            return new List<string>();
        }
        /// <summary>
        /// 执行条件等待（循环检查条件）
        /// </summary>
        private async Task<object> ExecuteConditionWaitAsync(CommandNode waitNode, FlowExecutionContext context, CancellationToken cancellationToken)
        {
            var leftVariable = waitNode.LogicCommand.Properties?.GetValueOrDefault("ConditionLeftVariable", "")?.ToString() ?? "";
            var operatorStr = waitNode.LogicCommand.Properties?.GetValueOrDefault("ConditionOperator", "Equals")?.ToString() ?? "Equals";
            var rightValue = waitNode.LogicCommand.Properties?.GetValueOrDefault("ConditionRightValue", "")?.ToString() ?? "";
            var executionMode = waitNode.LogicCommand.Properties?.GetValueOrDefault("ExecutionMode", "Synchronous")?.ToString() ?? "Synchronous";
            var isInfiniteTimeout = Convert.ToBoolean(waitNode.LogicCommand.Properties?.GetValueOrDefault("IsInfiniteTimeout", false));
            var timeoutSeconds = Convert.ToDouble(waitNode.LogicCommand.Properties?.GetValueOrDefault("TimeoutSeconds", 30.0));
            var checkInterval = Convert.ToDouble(waitNode.LogicCommand.Properties?.GetValueOrDefault("CheckIntervalSeconds", 1.0));
            bool isWarning = false;
            if (waitNode.LogicCommand?.Properties != null &&
                waitNode.LogicCommand.Properties.TryGetValue("IsWarning", out var warningVal))
            {
                isWarning = warningVal is bool b ? b : Convert.ToBoolean(warningVal);
            }
            bool isAsyncMode = executionMode.Equals("Asynchronous", StringComparison.OrdinalIgnoreCase);

            _logger.LogDebug("条件等待: {Left} {Op} {Right}, 模式: {Mode}, 无限超时: {Infinite}",
                leftVariable, operatorStr, rightValue, executionMode, isInfiniteTimeout);

            var startTime = DateTime.Now;
            var checkIntervalSpan = TimeSpan.FromSeconds(checkInterval);
            int iterationCount = 0;

            // 构建条件表达式显示
            string conditionExpression = BuildConditionExpression(leftVariable, operatorStr, rightValue);

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // 检查超时
                if (!isInfiniteTimeout && (DateTime.Now - startTime).TotalSeconds >= timeoutSeconds)
                {
                    _logger.LogInformation("条件等待超时（条件未满足），节点正常跳过: {Expression}, 超时: {Timeout}秒",
                        conditionExpression, timeoutSeconds);

                    // 超时 = 条件没满足，正常完成节点（跳过），不算失败
                    return (DateTime.Now - startTime).TotalMilliseconds;
                }

                iterationCount++;
                _logger.LogDebug("条件等待第 {Iteration} 次检查", iterationCount);

                // 评估条件
                bool conditionMet = EvaluateWaitCondition(leftVariable, operatorStr, rightValue, context, waitNode);

                if (conditionMet)
                {
                    _logger.LogDebug("条件已满足: {Expression}", conditionExpression);

                    // *** 开始用户交互，暂停其他分支 ***
                    BeginUserInteraction(waitNode, "ConditionWait", $"条件满足: {conditionExpression}");

                    try
                    {
                       
                        double? currentNumericValue = null;
                        var leftVal = ResolveOperandValue(leftVariable, context, waitNode);
                        if (leftVal != null && double.TryParse(leftVal.ToString(),
                            NumberStyles.Float, CultureInfo.InvariantCulture, out double parsedCurrent))
                        {
                            currentNumericValue = parsedCurrent;
                        }

                        double? thresholdNumericValue = null;
                        if (double.TryParse(rightValue, NumberStyles.Float,
                            CultureInfo.InvariantCulture, out double parsedThreshold))
                        {
                            thresholdNumericValue = parsedThreshold;
                        }

                        var userChoice = await ShowConditionMetDialogAsync(
                            conditionExpression, isAsyncMode, isWarning,
                            currentNumericValue, thresholdNumericValue, leftVariable);

                        // 检查用户操作
                        if (userChoice.IsStopped)
                        {
                            _logger.LogInformation("用户停止了条件等待");
                            throw new OperationCanceledException("用户停止了等待流程");
                        }

                        if (userChoice.IsContinue)
                        {
                            _logger.LogInformation("用户确认继续执行，条件等待完成");
                            return (DateTime.Now - startTime).TotalMilliseconds;
                        }
                    }
                    finally
                    {
                        // *** 结束用户交互，恢复其他分支 ***
                        EndUserInteraction(waitNode, "ConditionWait");
                    }
                }

                // 等待检查间隔
                _logger.LogDebug("条件未满足，等待 {Interval} 秒后重新检查", checkInterval);
                await Task.Delay(checkIntervalSpan, cancellationToken);
            }
        }

        /// <summary>
        /// 异步显示条件满足对话框
        /// </summary>
        private async Task<ConditionDialogResult> ShowConditionMetDialogAsync(
                    string conditionExpression, bool isAsyncMode,
                    bool isWarning = false,
                    double? currentValue = null,
                    double? thresholdValue = null,
                    string parameterName = null)
        {
            var tcs = new TaskCompletionSource<ConditionDialogResult>();

            // 在UI线程显示对话框
            Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    var mainWindow = Application.Current.MainWindow;
                    var conditionDialog = new ConditionMetDialog(
                        conditionExpression, isAsyncMode, isWarning,
                        currentValue, thresholdValue, parameterName);

                    // 显示对话框并处理结果
                    var dialogResult = conditionDialog.ShowDialog();

                    // 设置结果
                    var result = new ConditionDialogResult
                    {
                        DialogResult = dialogResult == true,
                        IsContinue = conditionDialog.IsContinue,
                        IsStopped = conditionDialog.IsStopped
                    };

                    tcs.SetResult(result);
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            }));

            return await tcs.Task;
        }

        /// <summary>
        /// 评估等待条件
        /// </summary>
        private bool EvaluateWaitCondition(string leftVariable, string operatorStr, string rightValue,
            FlowExecutionContext context, CommandNode waitNode)
        {
            try
            {
                // 解析左侧变量值
                var leftVal = ResolveOperandValue(leftVariable, context, waitNode);

                // 解析右侧值
                var rightVal = ResolveOperandValue(rightValue, context, waitNode);

                _logger.LogDebug("条件评估: {Left}({LeftVal}) {Op} {Right}({RightVal})",
                    leftVariable, leftVal, operatorStr, rightValue, rightVal);

                // 根据操作符类型，某些操作符不需要右侧值
                switch (operatorStr)
                {
                    case "IsTrue":
                        return SafeToBool(leftVal, false);
                    case "IsFalse":
                        return !SafeToBool(leftVal, false);
                    case "IsEmpty":
                        return leftVal == null || string.IsNullOrWhiteSpace(leftVal.ToString());
                    case "IsNotEmpty":
                        return leftVal != null && !string.IsNullOrWhiteSpace(leftVal.ToString());
                    default:
                        // 需要右侧值的操作符
                        return EvaluateCondition(leftVal, operatorStr, rightVal);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "条件评估失败: {Left} {Op} {Right}", leftVariable, operatorStr, rightValue);
                return false;
            }
        }

        /// <summary>
        /// 构建条件表达式显示文本
        /// </summary>
        private string BuildConditionExpression(string leftVariable, string operatorStr, string rightValue)
        {
            var operatorDisplay = operatorStr switch
            {
                "Equals" => "==",
                "NotEquals" => "!=",
                "GreaterThan" => ">",
                "GreaterThanOrEqual" => ">=",
                "LessThan" => "<",
                "LessThanOrEqual" => "<=",
                "Contains" => "包含",
                "NotContains" => "不包含",
                "IsTrue" => "为真",
                "IsFalse" => "为假",
                "IsEmpty" => "为空",
                "IsNotEmpty" => "不为空",
                _ => operatorStr
            };

            return operatorStr switch
            {
                "IsTrue" or "IsFalse" or "IsEmpty" or "IsNotEmpty" => $"{leftVariable} {operatorDisplay}",
                _ => $"{leftVariable} {operatorDisplay} {rightValue}"
            };
        }

        /// <summary>
        /// 获取时间单位显示文本
        /// </summary>
        private string GetTimeUnitDisplay(string timeUnit)
        {
            return timeUnit switch
            {
                "Milliseconds" => "毫秒",
                "Seconds" => "秒",
                "Minutes" => "分钟",
                "Hours" => "小时",
                _ => "秒"
            };
        }


        private async Task<object> ExecuteSetVariableCommandAsync(CommandNode setVarNode, FlowExecutionContext context, CancellationToken cancellationToken)
        {
            _logger.LogDebug("执行设置变量命令: {NodeName}", setVarNode.DisplayName);
            try
            {
                var variableNameRaw = setVarNode.LogicCommand.Properties?.GetValueOrDefault("VariableName", "")?.ToString() ?? "";
                var variableValueStr = setVarNode.LogicCommand.Properties?.GetValueOrDefault("VariableValue", "")?.ToString() ?? "";
                var variableType = setVarNode.LogicCommand.Properties?.GetValueOrDefault("VariableType", "String")?.ToString() ?? "String";

                if (string.IsNullOrEmpty(variableNameRaw))
                {
                    throw new InvalidOperationException("变量名不能为空");
                }

                // 解析变量名 - 剥离 ${ } 或 $ 前缀，提取实际的变量名
                string actualVariableName = ExtractActualVariableName(variableNameRaw);

                _logger.LogDebug("设置变量: 原始名称={Raw}, 实际名称={Actual}", variableNameRaw, actualVariableName);

                // 解析变量值（支持引用其他变量或设备输出）
                var resolvedValue = ResolveOperandValue(variableValueStr, context, setVarNode);

                // 根据指定的类型进行转换
                object finalValue = ConvertToType(resolvedValue, variableType);

                // 设置变量到上下文中（使用实际的变量名）
                context.SetVariable(actualVariableName, finalValue);

                // *** 新增：记录变量变化 ***
                if (!string.IsNullOrEmpty(_currentExperimentId))
                {
                    await _experimentDataAdapter.RecordVariableAsync(
                        _currentExperimentId,
                        actualVariableName,
                        finalValue,
                        variableType,
                        setVarNode.NodeId);
                }

                // 发布变量更新事件（后台服务会处理持久化）
                _eventAggregator?.GetEvent<VariableUpdatedEvent>()?.Publish(new VariableUpdatedEventArgs
                {
                    VariableName = actualVariableName,
                    VariableValue = finalValue,
                    VariableType = variableType,
                    UpdateTime = DateTime.Now,
                    NodeId = setVarNode.NodeId,
                    NodeName = setVarNode.DisplayName
                });

                _logger.LogInformation("设置变量成功: {Name} = {Value} (类型: {Type})",
                    actualVariableName, finalValue, variableType);

                // 只需要短暂延迟确保事件发布完成
                await Task.Delay(50, cancellationToken);

                return finalValue;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "设置变量命令执行失败: {NodeName}", setVarNode.DisplayName);
                throw;
            }
        }

        /// <summary>
        /// 从 FullVariableName 格式中提取实际的变量名（修复版本：处理 ${ } 和 $ 前缀）
        /// </summary>
        private string ExtractActualVariableName(string variableNameInput)
        {
            if (string.IsNullOrWhiteSpace(variableNameInput))
                return "";

            variableNameInput = variableNameInput.Trim();

            // 新添加：处理 ${variable} 格式，剥离 ${ } 返回内部变量名
            if (variableNameInput.StartsWith("${") && variableNameInput.EndsWith("}"))
            {
                return variableNameInput.Substring(2, variableNameInput.Length - 3).Trim();
            }

            // 新添加：处理 $variable 格式，剥离 $ 返回变量名
            if (variableNameInput.StartsWith("$") && !variableNameInput.StartsWith("${"))
            {
                return variableNameInput.Substring(1).Trim();
            }

            // 原逻辑：处理含 . 的格式（如 custom.variableName 或 device.cmd.var）
            if (variableNameInput.Contains("."))
            {
                var parts = variableNameInput.Split('.');

                if (parts.Length >= 2)
                {
                    // 如果是自定义变量格式：custom.variableName
                    if (parts[0].Equals("custom", StringComparison.OrdinalIgnoreCase))
                    {
                        return string.Join(".", parts.Skip(1)); // 返回 custom. 后面的部分
                    }

                    // 如果是设备输出变量格式：设备名.命令名.变量名
                    if (parts.Length >= 3)
                    {
                        return string.Join(".", parts.Skip(2)); // 返回设备名.命令名. 后面的部分
                    }

                    // 如果只有两部分，返回最后一部分
                    return parts.Last();
                }
            }

            // 如果不包含点号，直接返回原始名称
            return variableNameInput;
        }

        /// <summary>
        /// 根据指定类型转换值
        /// </summary>
        private object ConvertToType(object value, string typeName)
        {
            if (value == null)
                return null;

            // 处理枚举名称和字符串形式
            switch (typeName)
            {
                case "String":
                case "System.String":
                    if (Enum.TryParse<VariableType>(typeName, out var strType) && strType == VariableType.String)
                        return value.ToString();
                    return value.ToString();

                case "Number":
                case "Double":
                case "System.Double":
                    if (Enum.TryParse<VariableType>(typeName, out var numType) && numType == VariableType.Number)
                        goto ParseDouble;
                    goto ParseDouble;

                case "Integer":
                case "Int":
                case "Int32":
                case "System.Int32":
                    if (Enum.TryParse<VariableType>(typeName, out var intType) && intType == VariableType.Integer)
                        goto ParseInt;
                    goto ParseInt;

                case "Boolean":
                case "Bool":
                case "System.Boolean":
                    if (Enum.TryParse<VariableType>(typeName, out var boolType) && boolType == VariableType.Boolean)
                        goto ParseBool;
                    goto ParseBool;

                case "DateTime":
                case "System.DateTime":
                    if (Enum.TryParse<VariableType>(typeName, out var dateType) && dateType == VariableType.DateTime)
                        goto ParseDateTime;
                    goto ParseDateTime;

                default:
                    // 尝试解析为VariableType枚举
                    if (Enum.TryParse<VariableType>(typeName, out var varType))
                    {
                        return ConvertToVariableType(value, varType);
                    }
                    return value;
            }

        ParseDouble:
            if (double.TryParse(value.ToString(), NumberStyles.Float | NumberStyles.AllowThousands,
                CultureInfo.InvariantCulture, out double doubleValue))
                return doubleValue;
            return 0.0;

        ParseInt:
            if (int.TryParse(value.ToString(), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out int intValue))
                return intValue;
            return 0;

        ParseBool:
            if (value is bool boolValue)
                return boolValue;
            if (bool.TryParse(value.ToString(), out bool parsed))
                return parsed;
            // 将非零数字视为true
            if (double.TryParse(value.ToString(), out double num))
                return num != 0;
            // 将非空字符串视为true
            return !string.IsNullOrWhiteSpace(value.ToString());

        ParseDateTime:
            if (value is DateTime dt)
                return dt;
            if (DateTime.TryParse(value.ToString(), out DateTime parsedDate))
                return parsedDate;
            return DateTime.Now;
        }

        /// <summary>
        /// 根据VariableType枚举转换值
        /// </summary>
        private object ConvertToVariableType(object value, VariableType type)
        {
            if (value == null)
                return null;

            switch (type)
            {
                case VariableType.String:
                    return value.ToString();

                case VariableType.Number:
                    if (double.TryParse(value.ToString(), NumberStyles.Float | NumberStyles.AllowThousands,
                        CultureInfo.InvariantCulture, out double doubleValue))
                        return doubleValue;
                    return 0.0;

                case VariableType.Integer:
                    if (int.TryParse(value.ToString(), NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out int intValue))
                        return intValue;
                    return 0;

                case VariableType.Boolean:
                    if (value is bool boolValue)
                        return boolValue;
                    if (bool.TryParse(value.ToString(), out bool parsed))
                        return parsed;
                    // 将非零数字视为true
                    if (double.TryParse(value.ToString(), out double num))
                        return num != 0;
                    // 将非空字符串视为true
                    return !string.IsNullOrWhiteSpace(value.ToString());

                case VariableType.DateTime:
                    if (value is DateTime dt)
                        return dt;
                    if (DateTime.TryParse(value.ToString(), out DateTime parsedDate))
                        return parsedDate;
                    return DateTime.Now;

                default:
                    return value;
            }
        }

        public async Task PauseAsync()
        {
            if (State != FlowExecutionState.Running)
                return;

            State = FlowExecutionState.Pausing;
            _pauseEvent.Reset();

            _logger.LogInformation("开始暂停流程，同时暂停所有设备的活跃状态");

            // 获取所有可控制的设备
            List<CanvasDeviceViewModel> canvasDevices = null;
            _eventAggregator?.GetEvent<RequestCanvasDevicesEvent>()?.Publish(list => canvasDevices = list);

            var controllableDevices = canvasDevices?
                .Where(d => d.Device is IControllableDevice)
                .Select(d => d.Device as IControllableDevice)
                .Where(d => d.ActiveStates.Any())
                .ToList() ?? new List<IControllableDevice>();

            if (controllableDevices.Any())
            {
                _logger.LogInformation("找到 {Count} 个有活跃状态的设备需要暂停", controllableDevices.Count);

                var pauseTasks = controllableDevices.Select(device => device.PauseAllActiveStatesAsync()).ToList();

                try
                {
                    var results = await Task.WhenAll(pauseTasks);
                    var successCount = results.Count(r => r);
                    _logger.LogInformation("设备暂停完成: 成功 {Success}/{Total}", successCount, results.Length);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "暂停设备状态失败");
                }
            }
            else
            {
                _logger.LogInformation("没有设备有活跃状态需要暂停");
            }

            State = FlowExecutionState.Paused;
            _logger.LogInformation("流程已暂停");
        }

        public async Task ResumeAsync()
        {
            if (State != FlowExecutionState.Paused)
                return;

            State = FlowExecutionState.Resuming;

            _logger.LogInformation("开始恢复流程，同时恢复所有设备的暂停状态");

            // 获取所有可控制的设备
            List<CanvasDeviceViewModel> canvasDevices = null;
            _eventAggregator?.GetEvent<RequestCanvasDevicesEvent>()?.Publish(list => canvasDevices = list);

            var controllableDevices = canvasDevices?
                .Where(d => d.Device is IControllableDevice)
                .Select(d => d.Device as IControllableDevice)
                .ToList() ?? new List<IControllableDevice>();

            if (controllableDevices.Any())
            {
                _logger.LogInformation("恢复 {Count} 个设备的暂停状态", controllableDevices.Count);

                var resumeTasks = controllableDevices.Select(device => device.ResumeAllPausedStatesAsync()).ToList();

                try
                {
                    var results = await Task.WhenAll(resumeTasks);
                    var successCount = results.Count(r => r);
                    _logger.LogInformation("设备恢复完成: 成功 {Success}/{Total}", successCount, results.Length);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "恢复设备状态失败");
                }
            }

            State = FlowExecutionState.Running;
            _pauseEvent.Set();
            _logger.LogInformation("流程已恢复");
        }

        public async Task StopAsync()
        {
            if (State != FlowExecutionState.Running && State != FlowExecutionState.Paused)
                return;

            var oldState = State;
            State = FlowExecutionState.Stopping;

            // *** 修改：立即释放暂停事件，让并行任务能检测到停止状态 ***
            _pauseEvent.Set();
            _userInteractionPauseEvent.Set();

            _logger.LogInformation("开始停止流程，当前状态从 {OldState} 变更为 {NewState}", oldState, State);

            // *** 新增：先取消执行引擎 ***
            try
            {
                _cancellationTokenSource?.Cancel();
                _logger.LogDebug("已发送取消信号");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "发送取消信号失败");
            }

            // *** 修改：给并行任务一些时间响应取消信号 ***
            await Task.Delay(500); // 给任务500ms时间响应取消

            // 获取所有可控制的设备
            List<CanvasDeviceViewModel> canvasDevices = null;
            _eventAggregator?.GetEvent<RequestCanvasDevicesEvent>()?.Publish(list => canvasDevices = list);

            var controllableDevices = canvasDevices?
                .Where(d => d.Device is IControllableDevice)
                .Select(d => d.Device as IControllableDevice)
                .ToList() ?? new List<IControllableDevice>();

            if (controllableDevices.Any())
            {
                _logger.LogInformation("停止 {Count} 个设备的所有活跃状态", controllableDevices.Count);

                var stopTasks = controllableDevices.Select(device => device.StopAllActiveStatesAsync()).ToList();

                try
                {
                    // 给设备一定时间停止
                    await Task.WhenAll(stopTasks).WaitAsync(TimeSpan.FromSeconds(10));
                    _logger.LogInformation("所有设备状态已停止");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "停止设备状态失败");
                }
            }

            // *** 新增：等待后台任务完成或取消 ***
            Task[] tasksToWait;
            lock (_backgroundTasksLock)
            {
                tasksToWait = _backgroundTasks.ToArray();
            }

            if (tasksToWait.Length > 0)
            {
                _logger.LogInformation("等待 {Count} 个后台任务停止...", tasksToWait.Length);
                try
                {
                    // 等待最多5秒让后台任务完成
                    await Task.WhenAll(tasksToWait).WaitAsync(TimeSpan.FromSeconds(5));
                    _logger.LogInformation("所有后台任务已停止");
                }
                catch (TimeoutException)
                {
                    _logger.LogWarning("部分后台任务未能在超时时间内停止");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "等待后台任务停止时发生异常");
                }
            }

            _logger.LogInformation("流程停止完成");
        }
        /// <summary>
        /// 解析操作数的值（可能是常量、变量引用或设备输出变量）
        /// </summary>
        private object ResolveOperandValue(string operand, FlowExecutionContext context, CommandNode currentNode)
        {
            if (string.IsNullOrWhiteSpace(operand))
                return null;

            operand = operand.Trim();

            // 1. 检查是否是变量引用格式 ${variableName} 或 $variableName
            if (operand.StartsWith("${") && operand.EndsWith("}"))
            {
                var varName = operand.Substring(2, operand.Length - 3);
                return ResolveVariable(varName, context, currentNode);
            }
            else if (operand.StartsWith("$"))
            {
                var varName = operand.Substring(1);
                return ResolveVariable(varName, context, currentNode);
            }

            // 2. 检查是否是 FullVariableName 格式（用于设置变量时的值引用）
            if (operand.Contains("."))
            {
                // 首先尝试作为设备输出变量解析
                var deviceValue = ResolveDeviceOutputVariable(operand, context, currentNode);
                if (deviceValue != null)
                {
                    _logger.LogDebug("解析设备输出变量: {Operand} = {Value}", operand, deviceValue);
                    return deviceValue;
                }

                // 如果是 custom.variableName 格式，从上下文变量中获取
                if (operand.StartsWith("custom.", StringComparison.OrdinalIgnoreCase))
                {
                    var actualVarName = operand.Substring(7); // 去掉 "custom." 前缀
                    if (context.HasVariable(actualVarName))
                    {
                        var value = context.GetVariable(actualVarName);
                        _logger.LogDebug("解析自定义变量: {Operand} = {Value}", operand, value);
                        return value;
                    }
                }
            }

            // 3. 尝试从上下文变量中获取（如果操作数本身就是变量名）
            if (context.HasVariable(operand))
            {
                return context.GetVariable(operand);
            }

            // 4. 解析为常量值
            return ParseConstantValue(operand);
        }
        /// <summary>
        /// 数值比较带容差（仅用于 == / !=）。
        /// 左右都能解析为数值时按容差比较；任一方非数值则回退为字符串比较。
        /// </summary>
        private static bool EvaluateEqualityWithTolerance(
            object left, object right, bool isEqual,
            ToleranceMode toleranceMode, double toleranceValue)
        {
            // 尝试数值比较
            if (TryToDouble(left, out var l) && TryToDouble(right, out var r))
            {
                double diff = Math.Abs(l - r);
                double effectiveTol = toleranceMode switch
                {
                    ToleranceMode.Absolute => Math.Max(0, toleranceValue),
                    ToleranceMode.Relative => Math.Max(0, toleranceValue) * Math.Abs(r),
                    _ => 0.0
                };

                bool equal = diff <= effectiveTol;   // tol = 0 时退化为严格相等
                return isEqual ? equal : !equal;
            }

            // 非数值：回退到字符串比较，忽略容差
            var sl = left?.ToString() ?? "";
            var sr = right?.ToString() ?? "";
            bool strEqual = string.Equals(sl, sr);
            return isEqual ? strEqual : !strEqual;
        }

        private static bool TryToDouble(object val, out double result)
        {
            result = 0;
            if (val == null) return false;
            if (val is double d) { result = d; return true; }
            if (val is float f) { result = f; return true; }
            if (val is int i) { result = i; return true; }
            if (val is long lg) { result = lg; return true; }
            if (val is decimal dec) { result = (double)dec; return true; }
            if (val is bool b) { result = b ? 1 : 0; return true; }
            var s = val.ToString();
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out result);
        }

        private static ToleranceMode ReadToleranceModeForEngine(object val)
        {
            if (val is ToleranceMode tm) return tm;
            if (val is string s && Enum.TryParse<ToleranceMode>(s, true, out var p)) return p;
            try
            {
                var i = Convert.ToInt32(val);
                if (i >= 0 && i <= 2) return (ToleranceMode)i;
            }
            catch { }
            return ToleranceMode.None;
        }
        /// <summary>
        /// <summary>
        /// 解析设备输出变量
        /// </summary>
        private object ResolveDeviceOutputVariable(string variablePath, FlowExecutionContext context, CommandNode currentNode)
        {
            try
            {
                var parts = variablePath.Split('.');
                if (parts.Length < 2)
                    return null;

                string deviceDisplayName = null;
                string deviceId = null;
                string commandName = null;
                string variableName = null;

                if (parts.Length == 2)
                {
                    // 格式: CommandName.VariableName
                    commandName = parts[0];
                    variableName = parts[1];
                }
                else if (parts.Length >= 3)
                {
                    // 格式: 泵#1.启动.流量
                    deviceDisplayName = parts[0];
                    commandName = parts[1];
                    variableName = string.Join(".", parts.Skip(2));

                    // 将显示名称转换为DeviceId
                    deviceId = _deviceNameResolver.GetDeviceIdByDisplayName(deviceDisplayName);

                    if (string.IsNullOrEmpty(deviceId))
                    {
                        _logger.LogWarning("无法解析设备名称: {DeviceName}", deviceDisplayName);
                        return null;
                    }
                }

                // 从执行历史中查找
                var executedNode = FindExecutedNodeWithOutput(context, deviceId, commandName);
                if (executedNode?.LastOutputParameters?.Variables != null)
                {
                    var outputVar = executedNode.LastOutputParameters.Variables
                        .FirstOrDefault(v => v.Name == variableName);

                    if (outputVar != null)
                    {
                        _logger.LogDebug("找到设备输出变量: {Path} = {Value}", variablePath, outputVar.Value);
                        return outputVar.Value;
                    }
                }

                // 从全局已执行的节点中查找
                var flowViewModel = _statusService?.GetCurrentFlowViewModel();
                if (flowViewModel != null)
                {
                    var allNodes = GetAllNodes(flowViewModel.CommandNodes);

                    var matchingNodes = allNodes.Where(node =>
                    {
                        if (node.DeviceCommand == null || node.CommandName != commandName)
                            return false;

                        if (node.LastOutputParameters?.Variables == null)
                            return false;

                        // 如果没有指定设备，匹配所有
                        if (string.IsNullOrEmpty(deviceId))
                            return true;

                        // 比较DeviceId
                        return !string.IsNullOrEmpty(node.BoundDeviceId) &&
                               node.BoundDeviceId.Equals(deviceId, StringComparison.OrdinalIgnoreCase);
                    })
                    .OrderByDescending(n => n.LastExecutionResult?.ExecutionTime ?? DateTime.MinValue)
                    .ToList();

                    foreach (var node in matchingNodes)
                    {
                        var outputVar = node.LastOutputParameters.Variables
                            .FirstOrDefault(v => v.Name == variableName);

                        if (outputVar != null)
                        {
                            var nodeDisplayName = _deviceNameResolver.GetDisplayNameByDeviceId(node.BoundDeviceId);
                            _logger.LogDebug("从全局节点找到设备输出变量: {Path} = {Value} (设备: {Device})",
                                variablePath, outputVar.Value, nodeDisplayName ?? node.BoundDeviceId);
                            return outputVar.Value;
                        }
                    }
                }

                // 智能匹配：如果用户只输入了基础设备名（不带编号）
                if (!string.IsNullOrEmpty(deviceDisplayName) && !deviceDisplayName.Contains("#"))
                {
                    _logger.LogDebug("尝试模糊匹配设备名: {DeviceName}", deviceDisplayName);

                    // 获取所有匹配基础名称的设备
                    var allDisplayNames = _deviceNameResolver.GetAllDisplayNames();
                    var matchingDeviceIds = allDisplayNames
                        .Where(kvp => kvp.Value.StartsWith(deviceDisplayName + "#", StringComparison.OrdinalIgnoreCase))
                        .Select(kvp => kvp.Key)
                        .ToList();

                    if (matchingDeviceIds.Any())
                    {
                        _logger.LogDebug("找到 {Count} 个匹配的设备", matchingDeviceIds.Count);

                        var allNodes = GetAllNodes(flowViewModel.CommandNodes);

                        // 查找最近执行的匹配设备
                        var matchingNodesLoose = allNodes
                            .Where(n => n.DeviceCommand != null &&
                                       n.CommandName == commandName &&
                                       !string.IsNullOrEmpty(n.BoundDeviceId) &&
                                       matchingDeviceIds.Contains(n.BoundDeviceId) &&
                                       n.LastOutputParameters?.Variables != null)
                            .OrderByDescending(n => n.LastExecutionResult?.ExecutionTime ?? DateTime.MinValue)
                            .ToList();

                        foreach (var node in matchingNodesLoose)
                        {
                            var outputVar = node.LastOutputParameters.Variables
                                .FirstOrDefault(v => v.Name == variableName);

                            if (outputVar != null)
                            {
                                var nodeDisplayName = _deviceNameResolver.GetDisplayNameByDeviceId(node.BoundDeviceId);
                                _logger.LogDebug("通过模糊匹配找到设备输出变量: {Path} = {Value} (设备: {Device})",
                                    variablePath, outputVar.Value, nodeDisplayName ?? node.BoundDeviceId);
                                return outputVar.Value;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogCritical(ex, "解析设备输出变量失败: {Path}", variablePath);
            }

            return null;
        }

        /// <summary>
        /// 查找已执行的节点输出（使用DeviceId匹配）
        /// </summary>
        private CommandNode FindExecutedNodeWithOutput(FlowExecutionContext context, string deviceId, string commandName)
        {
            // 从执行历史中反向查找（最新的优先）。裁剪会在写入侧移除旧元素,
            // 这里在同一把锁下拍快照再枚举,避免"枚举时集合被修改"异常
            List<NodeExecutionRecord> snapshot;
            lock (context.ExecutionHistory)
            {
                snapshot = new List<NodeExecutionRecord>(context.ExecutionHistory);
            }
            var records = snapshot.AsEnumerable().Reverse();
            foreach (var record in records)
            {
                if (!string.IsNullOrEmpty(record.NodeId))
                {
                    var node = FindNodeById(record.NodeId);
                    if (node?.DeviceCommand != null && node.CommandName == commandName)
                    {
                        // 如果没有指定设备ID，则匹配任何设备的该命令
                        if (string.IsNullOrEmpty(deviceId))
                        {
                            return node;
                        }

                        // 直接比较DeviceId
                        if (!string.IsNullOrEmpty(node.BoundDeviceId) &&
                            node.BoundDeviceId.Equals(deviceId, StringComparison.OrdinalIgnoreCase))
                        {
                            return node;
                        }
                    }
                }
            }
            return null;
        }




        /// <summary>
        /// 根据NodeId查找节点
        /// </summary>
        private CommandNode FindNodeById(string nodeId)
        {
            var flowViewModel = _statusService?.GetCurrentFlowViewModel();
            if (flowViewModel == null)
                return null;

            var allNodes = GetAllNodes(flowViewModel.CommandNodes);
            return allNodes.FirstOrDefault(n => n.NodeId == nodeId);
        }

        /// <summary>
        /// 递归获取所有节点（包括子节点）
        /// </summary>
        private List<CommandNode> GetAllNodes(IEnumerable<CommandNode> nodes)
        {
            var result = new List<CommandNode>();
            foreach (var node in nodes)
            {
                result.Add(node);
                if (node.HasChildren)
                {
                    result.AddRange(GetAllNodes(node.Children));
                }
            }
            return result;
        }

        /// <summary>
        /// 获取当前流程视图模型（辅助方法）
        /// </summary>
        private FlowDesignerViewModel GetCurrentFlowViewModel()
        {
            return _statusService?.GetCurrentFlowViewModel();
        }

        /// <summary>
        /// 解析变量引用
        /// </summary>
        private object ResolveVariable(string variableName, FlowExecutionContext context, CommandNode currentNode)
        {
            // 1. 如果是 FullVariableName 格式，先提取实际变量名
            string actualVarName = ExtractActualVariableName(variableName);

            // 2. 首先尝试从上下文变量中获取（使用实际变量名）
            if (context.HasVariable(actualVarName))
            {
                var value = context.GetVariable(actualVarName);
                _logger.LogDebug("从上下文找到变量: {VariableName} (实际名: {ActualName}) = {Value}",
                    variableName, actualVarName, value);
                return value;
            }

            // 3. 如果实际变量名和原始变量名不同，尝试使用原始变量名
            if (actualVarName != variableName && context.HasVariable(variableName))
            {
                var value = context.GetVariable(variableName);
                _logger.LogDebug("从上下文找到变量(原始名): {VariableName} = {Value}", variableName, value);
                return value;
            }

            // 4. 尝试作为设备输出变量路径解析（使用原始变量名）
            var deviceVarValue = ResolveDeviceOutputVariable(variableName, context, currentNode);
            if (deviceVarValue != null)
            {
                _logger.LogDebug("从设备输出找到变量: {VariableName} = {Value}", variableName, deviceVarValue);
                return deviceVarValue;
            }

            // 5. 返回null表示未找到
            _logger.LogWarning("未找到变量: {VariableName} (实际名: {ActualName})", variableName, actualVarName);
            return null;
        }

        /// <summary>
        /// 解析常量值
        /// </summary>
        private object ParseConstantValue(string value)
        {
            // 去除前后空格
            value = value.Trim();

            // 1. 布尔值
            if (value.Equals("true", StringComparison.OrdinalIgnoreCase))
                return true;
            if (value.Equals("false", StringComparison.OrdinalIgnoreCase))
                return false;

            // 2. null值
            if (value.Equals("null", StringComparison.OrdinalIgnoreCase))
                return null;

            // 3. 字符串（带引号）
            if ((value.StartsWith("'") && value.EndsWith("'")) ||
                (value.StartsWith("\"") && value.EndsWith("\"")))
            {
                return value.Substring(1, value.Length - 2);
            }

            // 4. 数字
            // 尝试解析为整数
            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int intVal))
                return intVal;

            // 尝试解析为浮点数
            if (double.TryParse(value, NumberStyles.Float | NumberStyles.AllowThousands,
                CultureInfo.InvariantCulture, out double doubleVal))
                return doubleVal;

            // 5. 默认作为字符串返回
            return value;
        }

        /// <summary>
        /// 评估条件
        /// </summary>
        private bool EvaluateCondition(object leftValue, string operatorStr, object rightValue)
        {
            try
            {
                _logger.LogDebug("评估条件: {Left} {Op} {Right}", leftValue, operatorStr, rightValue);

                // 处理null值
                if (leftValue == null || rightValue == null)
                {
                    return operatorStr switch
                    {
                        "Equal" => leftValue == rightValue,
                        "NotEqual" => leftValue != rightValue,
                        _ => false
                    };
                }

                // 根据操作符进行比较
                switch (operatorStr)
                {
                    case "Equal":
                    case "Equals":
                        return AreEqual(leftValue, rightValue);

                    case "NotEqual":
                    case "NotEquals":
                        return !AreEqual(leftValue, rightValue);

                    case "GreaterThan":
                        return Compare(leftValue, rightValue) > 0;

                    case "GreaterThanOrEqual":
                        return Compare(leftValue, rightValue) >= 0;

                    case "LessThan":
                        return Compare(leftValue, rightValue) < 0;

                    case "LessThanOrEqual":
                        return Compare(leftValue, rightValue) <= 0;

                    case "Contains":
                        return Contains(leftValue, rightValue);

                    case "NotContains":
                        return !Contains(leftValue, rightValue);

                    case "StartsWith":
                        return StartsWith(leftValue, rightValue);

                    case "EndsWith":
                        return EndsWith(leftValue, rightValue);

                    default:
                        _logger.LogWarning("未知的操作符: {Operator}，默认返回false", operatorStr);
                        return false;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "条件评估失败");
                return false;
            }
        }

        /// <summary>
        /// 判断两个值是否相等
        /// </summary>
        private bool AreEqual(object left, object right)
        {
            // 如果类型相同，直接比较
            if (left.GetType() == right.GetType())
            {
                return left.Equals(right);
            }

            // 尝试数值比较
            if (TryConvertToDouble(left, out double leftDouble) &&
                TryConvertToDouble(right, out double rightDouble))
            {
                return Math.Abs(leftDouble - rightDouble) < 0.0000001; // 考虑浮点数精度
            }

            // 字符串比较
            return left.ToString().Equals(right.ToString(), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 比较两个值的大小
        /// </summary>
        private int Compare(object left, object right)
        {
            // 尝试数值比较
            if (TryConvertToDouble(left, out double leftDouble) &&
                TryConvertToDouble(right, out double rightDouble))
            {
                return leftDouble.CompareTo(rightDouble);
            }

            // 字符串比较
            return string.Compare(left.ToString(), right.ToString(), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 判断是否包含
        /// </summary>
        private bool Contains(object left, object right)
        {
            var leftStr = left?.ToString() ?? "";
            var rightStr = right?.ToString() ?? "";
            return leftStr.IndexOf(rightStr, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// 判断是否以指定字符串开始
        /// </summary>
        private bool StartsWith(object left, object right)
        {
            var leftStr = left?.ToString() ?? "";
            var rightStr = right?.ToString() ?? "";
            return leftStr.StartsWith(rightStr, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 判断是否以指定字符串结束
        /// </summary>
        private bool EndsWith(object left, object right)
        {
            var leftStr = left?.ToString() ?? "";
            var rightStr = right?.ToString() ?? "";
            return leftStr.EndsWith(rightStr, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 尝试将值转换为double
        /// </summary>
        private bool TryConvertToDouble(object value, out double result)
        {
            result = 0;

            if (value == null)
                return false;

            if (value is double d)
            {
                result = d;
                return true;
            }

            if (value is int i)
            {
                result = i;
                return true;
            }

            if (value is float f)
            {
                result = f;
                return true;
            }

            if (value is decimal dec)
            {
                result = (double)dec;
                return true;
            }

            if (value is bool b)
            {
                result = b ? 1 : 0;
                return true;
            }

            return double.TryParse(value.ToString(), NumberStyles.Float | NumberStyles.AllowThousands,
                CultureInfo.InvariantCulture, out result);
        }
        public FlowValidationResult ValidateFlow(FlowDesignerViewModel flowViewModel)
        {
            var result = new FlowValidationResult { IsValid = true };

            try
            {
                if (flowViewModel?.CommandNodes == null || !flowViewModel.CommandNodes.Any())
                {
                    result.IsValid = false;
                    result.Errors.Add("流程中没有任何节点");
                    return result;
                }

                foreach (var node in flowViewModel.CommandNodes)
                {
                    var nodeValidation = ValidateNode(node);
                    result.Errors.AddRange(nodeValidation.Errors);
                    result.Warnings.AddRange(nodeValidation.Warnings);
                }

                result.IsValid = !result.Errors.Any();
            }
            catch (Exception ex)
            {
                result.IsValid = false;
                result.Errors.Add($"流程验证异常: {ex.Message}");
            }

            return result;
        }

        private FlowValidationResult ValidateNode(CommandNode node)
        {
            var result = new FlowValidationResult { IsValid = true };

            if (string.IsNullOrEmpty(node.DisplayName))
            {
                result.Warnings.Add($"节点缺少显示名称: {node.NodeId}");
            }

            if (node.DeviceCommand != null)
            {
                if (string.IsNullOrEmpty(node.DeviceCommand.Name))
                {
                    result.Errors.Add($"设备命令缺少名称: {node.NodeId}");
                }
                // NEW: 必须绑定设备
                if (string.IsNullOrEmpty(node.BoundDeviceId))
                {
                    result.Errors.Add($"设备命令未绑定设备: {node.DisplayName}");
                }
            }
            else if (node.LogicCommand != null)
            {
                switch (node.LogicCommand.CommandType)
                {
                    case LogicCommandType.If:
                        ValidateIfNode(node, result);
                        break;
                    case LogicCommandType.Loop:
                        ValidateLoopNode(node, result);
                        break;
                    case LogicCommandType.Wait:
                        ValidateWaitNode(node, result);
                        break;
                    case LogicCommandType.SetVariable:
                        ValidateSetVariableNode(node, result);
                        break;
                }

                if (node.HasChildren)
                {
                    foreach (var child in node.Children)
                    {
                        var childValidation = ValidateNode(child);
                        result.Errors.AddRange(childValidation.Errors);
                        result.Warnings.AddRange(childValidation.Warnings);
                    }
                }
            }
            else
            {
                result.Errors.Add($"节点既不是设备命令也不是逻辑命令: {node.NodeId}");
            }

            return result;
        }

        private void ValidateIfNode(CommandNode ifNode, FlowValidationResult result)
        {
            var condition = ifNode.LogicCommand.Properties?.GetValueOrDefault("Condition", "")?.ToString();
            if (string.IsNullOrWhiteSpace(condition))
            {
                result.Warnings.Add($"IF节点缺少条件表达式: {ifNode.DisplayName}");
            }
        }

        private void ValidateLoopNode(CommandNode loopNode, FlowValidationResult result)
        {
            var typeObj = loopNode.LogicCommand.Properties?.GetValueOrDefault("LoopType", LoopType.FixedCount);
            var loopType = ReadLoopType(typeObj);

            switch (loopType)
            {
                case LoopType.FixedCount:
                    {
                        var loopCountObj = loopNode.LogicCommand.Properties?.GetValueOrDefault("LoopCount", null);
                        if (loopCountObj == null)
                        {
                            result.Errors.Add($"循环节点缺少循环次数: {loopNode.DisplayName}");
                            break;
                        }

                        var loopCountStr = loopCountObj.ToString();

                        // 允许变量/表达式（如 $n 或 ${n}），这类不能静态验证数字
                        bool looksLikeVariable = loopCountStr.StartsWith("$") || loopCountStr.Contains("${");
                        if (!looksLikeVariable)
                        {
                            if (!int.TryParse(loopCountStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var lc) || lc <= 0)
                            {
                                result.Errors.Add($"循环节点的循环次数无效: {loopNode.DisplayName}");
                            }
                        }
                        break;
                    }

                case LoopType.Conditional:
                    {
                        var leftExists = loopNode.LogicCommand.Properties?.ContainsKey("LeftOperand") == true;
                        var opExists = loopNode.LogicCommand.Properties?.ContainsKey("Operator") == true;
                        var rightExists = loopNode.LogicCommand.Properties?.ContainsKey("RightOperand") == true;

                        if (!leftExists || !opExists || !rightExists)
                            result.Errors.Add($"条件循环缺少必要字段（Left/Operator/Right）: {loopNode.DisplayName}");

                        var left = loopNode.LogicCommand.Properties?["LeftOperand"]?.ToString();
                        var right = loopNode.LogicCommand.Properties?["RightOperand"]?.ToString();

                        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
                            result.Warnings.Add($"条件循环的操作数为空（Left/Right）: {loopNode.DisplayName}");

                        // 验证 IsDoWhile 字段
                        var isDoWhileExists = loopNode.LogicCommand.Properties?.ContainsKey("IsDoWhile") == true;
                        if (!isDoWhileExists)
                        {
                            // 如果不存在，设置默认值
                            if (loopNode.LogicCommand.Properties != null)
                            {
                                loopNode.LogicCommand.Properties["IsDoWhile"] = false;
                            }
                        }

                        break;
                    }

                case LoopType.Infinite:
                    // 无限循环不强制校验，但可以检查是否有合理的退出机制
                    result.Warnings.Add($"无限循环节点需要确保有合理的退出机制: {loopNode.DisplayName}");
                    break;

                default:
                    result.Warnings.Add($"未知 LoopType: {loopType}，节点: {loopNode.DisplayName}");
                    break;
            }

            // 验证循环间隔时间
            var waitTimeObj = loopNode.LogicCommand.Properties?.GetValueOrDefault("WaitTime", 10);
            var waitTimeMs = SafeToInt(waitTimeObj, 10);
            if (waitTimeMs < 0)
            {
                result.Warnings.Add($"循环节点的等待时间不应为负数: {loopNode.DisplayName}");
            }
        }

        private void ValidateWaitNode(CommandNode waitNode, FlowValidationResult result)
        {
            var waitTimeStr = waitNode.LogicCommand.Properties?.GetValueOrDefault("WaitTime", "")?.ToString();
            if (string.IsNullOrWhiteSpace(waitTimeStr) || !int.TryParse(waitTimeStr, out var waitTime) || waitTime < 0)
            {
                result.Errors.Add($"等待节点的等待时间无效: {waitNode.DisplayName}");
            }
        }

        private void ValidateSetVariableNode(CommandNode setVarNode, FlowValidationResult result)
        {
            var variableName = setVarNode.LogicCommand.Properties?.GetValueOrDefault("VariableName", "")?.ToString();
            if (string.IsNullOrWhiteSpace(variableName))
            {
                result.Errors.Add($"设置变量节点缺少变量名: {setVarNode.DisplayName}");
            }
        }

        private int CalculateTotalNodes(IEnumerable<CommandNode> nodes)
        {
            var total = 0;
            foreach (var node in nodes)
            {
                total++;
                if (node.HasChildren)
                {
                    total += CalculateTotalNodes(node.Children);
                }
            }
            return total;
        }

        // 深拷贝模板参数 + 应用节点覆盖值
        private static DeviceParameters BuildEffectiveParametersForNode(CommandNode node, DeviceCommand targetCmd)
        {
            var result = new DeviceParameters
            {
                Variables = new ObservableCollection<ParameterBase>()
            };

            var defs = targetCmd?.InputParameters?.Variables;
            if (defs == null || defs.Count == 0)
                return result;

            foreach (var def in defs)
            {
                var name = def.Name;

                // 覆盖值字符串（用不区分大小写的键存储更稳妥）
                string ovStr = null;
                if (node.ParameterOverrides != null)
                    node.ParameterOverrides.TryGetValue(name, out ovStr);

                ParameterBase clone = def switch
                {
                    StringParameter sp => CloneString(sp, ovStr),
                    NumberParameter np => CloneNumber(np, ovStr),
                    BooleanParameter bp => CloneBoolean(bp, ovStr),
                    EnumParameter ep => CloneEnum(ep, ovStr),
                    _ => CloneAsStringFallback(def, ovStr)
                };

                result.Variables.Add(clone);
            }

            return result;

            static StringParameter CloneString(StringParameter t, string overrideStr)
            {
                var val = overrideStr ?? (t.Value?.ToString() ?? string.Empty);
                var p = new StringParameter(t.Name, val, t.HelpText)
                {
                    MaxLength = t.MaxLength,
                    Options = new ObservableCollection<string>(t.Options ?? new ObservableCollection<string>())
                };
                return p;
            }

            static NumberParameter CloneNumber(NumberParameter t, string overrideStr)
            {
                double fallback = t.Value is double dv ? dv : 0d;
                double val = fallback;

                if (!string.IsNullOrWhiteSpace(overrideStr))
                {
                    // 先 Invariant，再 CurrentCulture，兼容 1.23 和 1,23
                    if (!double.TryParse(overrideStr, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out val) &&
                        !double.TryParse(overrideStr, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.CurrentCulture, out val))
                    {
                        val = fallback;
                    }
                }

                var p = new NumberParameter(t.Name, t.MinValue, t.MaxValue, val, t.HelpText)
                {
                    DecimalPlaces = t.DecimalPlaces
                };
                return p;
            }

            static BooleanParameter CloneBoolean(BooleanParameter t, string overrideStr)
            {
                bool val = t.Value is bool bv && bv;

                if (!string.IsNullOrWhiteSpace(overrideStr))
                {
                    if (bool.TryParse(overrideStr, out var b)) val = b;
                    else if (overrideStr == "1") val = true;
                    else if (overrideStr == "0") val = false;
                    else if (overrideStr == "是") val = true;  // 可选：本地化支持
                    else if (overrideStr == "否") val = false;
                }

                return new BooleanParameter(t.Name, val, t.HelpText);
            }

            static EnumParameter CloneEnum(EnumParameter t, string overrideStr)
            {
                object val = t.Value ?? Enum.GetValues(t.EnumType).GetValue(0);

                if (!string.IsNullOrWhiteSpace(overrideStr))
                {
                    try
                    {
                        if (int.TryParse(overrideStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i))
                        {
                            val = Enum.ToObject(t.EnumType, i);
                        }
                        else
                        {
                            // 正确签名：Enum.Parse(Type, string, bool)
                            val = Enum.Parse(t.EnumType, overrideStr, true);
                        }
                    }
                    catch
                    {
                        val = t.Value ?? Enum.GetValues(t.EnumType).GetValue(0);
                    }
                }

                return new EnumParameter(t.Name, t.EnumType, val, t.HelpText);
            }

            static StringParameter CloneAsStringFallback(ParameterBase t, string overrideStr)
            {
                var val = overrideStr ?? (t.Value?.ToString() ?? string.Empty);
                return new StringParameter(t.Name, val, t.HelpText);
            }
        }

        // 日志用：把参数集合转成易读文本
        private static string FormatParameters(DeviceParameters p)
        {
            if (p?.Variables == null || p.Variables.Count == 0) return "(none)";
            var parts = new List<string>(p.Variables.Count);

            foreach (var v in p.Variables)
            {
                string one = v switch
                {
                    NumberParameter np => $"{np.Name}={FormatNumber(np)}",
                    BooleanParameter bp => $"{bp.Name}={(bp.Value is bool b && b ? "true" : "false")}",
                    EnumParameter ep => $"{ep.Name}={ep.Value}",
                    StringParameter sp => $"{sp.Name}=\"{sp.Value}\"",
                    _ => $"{v.Name}={v.Value}"
                };
                parts.Add(one);
            }
            return string.Join(", ", parts);
        }

        private static string FormatNumber(NumberParameter np)
        {
            if (np.Value is double d)
                return d.ToString($"F{Math.Max(0, np.DecimalPlaces)}", CultureInfo.InvariantCulture);

            try
            {
                var dv = Convert.ToDouble(np.Value ?? 0, CultureInfo.InvariantCulture);
                return dv.ToString($"F{Math.Max(0, np.DecimalPlaces)}", CultureInfo.InvariantCulture);
            }
            catch
            {
                return np.Value?.ToString() ?? "0";
            }
        }
    }
}
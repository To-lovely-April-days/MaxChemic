using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using FlowEngine.Core;

namespace FlowEngine.Core
{
    /// <summary>
    /// 流程执行引擎
    /// </summary>
    public class FlowExecutionEngine : INotifyPropertyChanged, IDisposable
    {
        private FlowEngineState _state = FlowEngineState.Stopped;
        private CancellationTokenSource _cancellationTokenSource;
        private readonly ManualResetEventSlim _pauseEvent = new ManualResetEventSlim(true);
        private Task _executionTask;
        private readonly object _stateLock = new object();
        private bool _disposed = false;

        // 命令结果变量管理器
        private CommandResultVariableManager _variableManager;

        public FlowEngineState State
        {
            get
            {
                lock (_stateLock)
                {
                    return _state;
                }
            }
            private set
            {
                lock (_stateLock)
                {
                    if (_state != value)
                    {
                        _state = value;
                        OnPropertyChanged();
                        StateChanged?.Invoke(this, value);
                    }
                }
            }
        }

        public FlowContext Context { get; private set; }
        public List<IFlowNode> Nodes { get; set; } = new List<IFlowNode>();
        public string FlowName { get; set; } = "未命名流程";
        public TimeSpan TotalExecutionTime { get; private set; }
        public DateTime? StartTime { get; private set; }
        public DateTime? EndTime { get; private set; }

        // 事件
        public event PropertyChangedEventHandler PropertyChanged;
        public event EventHandler<FlowEngineState> StateChanged;
        public event EventHandler<FlowNodeExecutedEventArgs> NodeExecuted;
        public event EventHandler<FlowLogEventArgs> LogMessage;
        public event EventHandler<FlowExecutionResult> FlowCompleted;

        public FlowExecutionEngine(IServiceProvider serviceProvider = null)
        {
            Context = new FlowContext
            {
                ServiceProvider = serviceProvider,
                Logger = new FlowEngineLogger(this)
            };

            // 初始化命令结果变量管理器
            var deviceService = serviceProvider?.GetService<IDeviceService>();
            if (deviceService != null)
            {
                _variableManager = new CommandResultVariableManager(deviceService, Context.Logger, Context);
                _variableManager.VariableChanged += OnCommandVariableChanged;
                _variableManager.VariableRefreshError += OnCommandVariableError;
            }
        }

        /// <summary>
        /// 启动流程
        /// </summary>
        public async Task<bool> StartAsync()
        {
            if (State != FlowEngineState.Stopped)
            {
                LogMessage?.Invoke(this, new FlowLogEventArgs("流程已在运行中", LogLevel.Warning));
                return false;
            }

            try
            {
                ValidateFlow();

                _cancellationTokenSource = new CancellationTokenSource();
                Context.CancellationToken = _cancellationTokenSource.Token;
                Context.FlowId = Guid.NewGuid().ToString();
                Context.StartTime = DateTime.Now;
                _pauseEvent.Set();

                StartTime = DateTime.Now;
                EndTime = null;
                State = FlowEngineState.Running;

                LogMessage?.Invoke(this, new FlowLogEventArgs($"开始执行流程: {FlowName}", LogLevel.Info));

                // 启动命令结果变量管理器
                if (_variableManager != null)
                {
                    await _variableManager.StartAllAsync();
                }

                _executionTask = Task.Run(ExecuteFlowAsync);

                return true;
            }
            catch (Exception ex)
            {
                State = FlowEngineState.Failed;
                LogMessage?.Invoke(this, new FlowLogEventArgs($"启动流程失败: {ex.Message}", LogLevel.Error));
                return false;
            }
        }

        /// <summary>
        /// 暂停流程
        /// </summary>
        public void Pause()
        {
            if (State == FlowEngineState.Running)
            {
                State = FlowEngineState.Paused;
                _pauseEvent.Reset();
                LogMessage?.Invoke(this, new FlowLogEventArgs("流程已暂停", LogLevel.Info));
            }
        }

        /// <summary>
        /// 恢复流程
        /// </summary>
        public void Resume()
        {
            if (State == FlowEngineState.Paused)
            {
                State = FlowEngineState.Running;
                _pauseEvent.Set();
                LogMessage?.Invoke(this, new FlowLogEventArgs("流程已恢复", LogLevel.Info));
            }
        }

        /// <summary>
        /// 停止流程
        /// </summary>
        public async Task<bool> StopAsync()
        {
            if (State == FlowEngineState.Stopped)
                return true;

            try
            {
                State = FlowEngineState.Stopping;
                LogMessage?.Invoke(this, new FlowLogEventArgs("正在停止流程...", LogLevel.Info));

                _cancellationTokenSource?.Cancel();
                _pauseEvent.Set();

                if (_executionTask != null)
                {
                    await _executionTask.ConfigureAwait(false);
                }

                // 停止命令结果变量管理器
                _variableManager?.StopAll();

                EndTime = DateTime.Now;
                if (StartTime.HasValue)
                {
                    TotalExecutionTime = EndTime.Value - StartTime.Value;
                }

                State = FlowEngineState.Stopped;
                LogMessage?.Invoke(this, new FlowLogEventArgs("流程已停止", LogLevel.Info));

                return true;
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke(this, new FlowLogEventArgs($"停止流程时发生错误: {ex.Message}", LogLevel.Error));
                return false;
            }
        }

        /// <summary>
        /// 注册命令结果变量
        /// </summary>
        public bool RegisterCommandVariable(CommandResultVariableDefinition definition)
        {
            return _variableManager?.RegisterVariable(definition) ?? false;
        }

        /// <summary>
        /// 批量注册命令结果变量
        /// </summary>
        public void RegisterCommandVariables(IEnumerable<CommandResultVariableDefinition> definitions)
        {
            if (definitions == null || _variableManager == null)
                return;

            foreach (var definition in definitions)
            {
                RegisterCommandVariable(definition);
            }
        }

        /// <summary>
        /// 获取所有变量
        /// </summary>
        public Dictionary<string, object> GetAllVariables()
        {
            var allVariables = Context.GetAllVariables();

            if (_variableManager != null)
            {
                var commandVariables = _variableManager.GetAllVariableInfo();
                foreach (var varInfo in commandVariables)
                {
                    allVariables[varInfo.Name] = varInfo.Value;
                }
            }

            return allVariables;
        }

        /// <summary>
        /// 获取命令结果变量信息
        /// </summary>
        public List<VariableInfo> GetCommandVariableInfo()
        {
            return _variableManager?.GetAllVariableInfo() ?? new List<VariableInfo>();
        }

        /// <summary>
        /// 手动刷新命令结果变量
        /// </summary>
        public async Task<bool> RefreshCommandVariableAsync(string variableName)
        {
            return _variableManager != null && await _variableManager.RefreshVariableAsync(variableName);
        }

        /// <summary>
        /// 手动刷新所有命令结果变量
        /// </summary>
        public async Task RefreshAllCommandVariablesAsync()
        {
            if (_variableManager != null)
            {
                await _variableManager.RefreshAllVariablesAsync();
            }
        }

        /// <summary>
        /// 添加变量
        /// </summary>
        public void AddVariable(string name, object value)
        {
            Context.SetVariable(name, value);
        }

        /// <summary>
        /// 获取变量
        /// </summary>
        public T GetVariable<T>(string name, T defaultValue = default(T))
        {
            // 触发按需刷新
            _variableManager?.RefreshOnDemandVariables($"{{{name}}}");

            return Context.GetVariable(name, defaultValue);
        }

        /// <summary>
        /// 清除变量
        /// </summary>
        public void ClearVariables()
        {
            Context.ClearVariables();
        }

        /// <summary>
        /// 验证流程
        /// </summary>
        private void ValidateFlow()
        {
            if (Nodes == null || Nodes.Count == 0)
                throw new InvalidOperationException("流程中没有节点");

            foreach (var node in Nodes)
            {
                try
                {
                    node.Validate();
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException($"节点 '{node.Name}' 验证失败: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// 执行流程
        /// </summary>
        private async Task ExecuteFlowAsync()
        {
            try
            {
                var result = await ExecuteNodesAsync(Nodes);

                EndTime = DateTime.Now;
                if (StartTime.HasValue)
                {
                    TotalExecutionTime = EndTime.Value - StartTime.Value;
                }

                if (result.IsSuccess)
                {
                    State = FlowEngineState.Completed;
                    LogMessage?.Invoke(this, new FlowLogEventArgs($"流程执行完成，耗时: {TotalExecutionTime}", LogLevel.Info));
                }
                else
                {
                    State = FlowEngineState.Failed;
                    LogMessage?.Invoke(this, new FlowLogEventArgs($"流程执行失败: {result.Message}", LogLevel.Error));
                }

                FlowCompleted?.Invoke(this, result);
            }
            catch (OperationCanceledException)
            {
                State = FlowEngineState.Stopped;
                LogMessage?.Invoke(this, new FlowLogEventArgs("流程被取消", LogLevel.Info));
            }
            catch (Exception ex)
            {
                State = FlowEngineState.Failed;
                LogMessage?.Invoke(this, new FlowLogEventArgs($"流程执行异常: {ex.Message}", LogLevel.Error));

                FlowCompleted?.Invoke(this, new FlowExecutionResult
                {
                    IsSuccess = false,
                    Status = FlowExecutionStatus.Failed,
                    Message = ex.Message,
                    Exception = ex
                });
            }
        }

        /// <summary>
        /// 执行节点列表
        /// </summary>
        private async Task<FlowExecutionResult> ExecuteNodesAsync(List<IFlowNode> nodes)
        {
            foreach (var node in nodes)
            {
                // 检查暂停状态
                try
                {
                    _pauseEvent.Wait(_cancellationTokenSource.Token);
                }
                catch (OperationCanceledException)
                {
                    return new FlowExecutionResult
                    {
                        IsSuccess = false,
                        Status = FlowExecutionStatus.Cancelled,
                        Message = "流程被取消"
                    };
                }

                if (_cancellationTokenSource.Token.IsCancellationRequested)
                {
                    return new FlowExecutionResult
                    {
                        IsSuccess = false,
                        Status = FlowExecutionStatus.Cancelled,
                        Message = "流程被取消"
                    };
                }

                var startTime = DateTime.Now;
                LogMessage?.Invoke(this, new FlowLogEventArgs($"开始执行节点: {node.Name}", LogLevel.Info));

                // 触发事件驱动变量刷新
                _variableManager?.RefreshEventDrivenVariables();

                var result = await node.ExecuteAsync(Context, _cancellationTokenSource.Token);

                var args = new FlowNodeExecutedEventArgs
                {
                    Node = node,
                    Result = result,
                    ExecutionTime = DateTime.Now - startTime
                };

                NodeExecuted?.Invoke(this, args);

                if (!result.IsSuccess && result.Status != FlowExecutionStatus.Warning)
                {
                    LogMessage?.Invoke(this, new FlowLogEventArgs($"节点执行失败: {node.Name} - {result.Message}", LogLevel.Error));
                    return result;
                }

                LogMessage?.Invoke(this, new FlowLogEventArgs($"节点执行完成: {node.Name} - {result.Message}", LogLevel.Info));
            }

            return new FlowExecutionResult
            {
                IsSuccess = true,
                Status = FlowExecutionStatus.Success,
                Message = "所有节点执行成功"
            };
        }

        /// <summary>
        /// 命令变量值变更事件处理
        /// </summary>
        private void OnCommandVariableChanged(object sender, VariableValueChangedEventArgs e)
        {
            LogMessage?.Invoke(this, new FlowLogEventArgs(
                $"命令变量更新: {e.VariableName} = {e.NewValue}", LogLevel.Debug));
        }

        /// <summary>
        /// 命令变量刷新错误事件处理
        /// </summary>
        private void OnCommandVariableError(object sender, (string VariableName, Exception Error) e)
        {
            LogMessage?.Invoke(this, new FlowLogEventArgs(
                $"命令变量刷新错误: {e.VariableName} - {e.Error.Message}", LogLevel.Error));
        }

        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                StopAsync().GetAwaiter().GetResult();

                _variableManager?.Dispose();
                _cancellationTokenSource?.Dispose();
                _pauseEvent?.Dispose();

                _disposed = true;
            }
        }
    }

    /// <summary>
    /// 流程引擎专用日志器
    /// </summary>
    internal class FlowEngineLogger : IFlowLogger
    {
        private readonly FlowExecutionEngine _engine;

        public FlowEngineLogger(FlowExecutionEngine engine)
        {
            _engine = engine;
        }

        public void LogInfo(string message)
        {
           
        }

        public void LogWarning(string message)
        {
           
        }

        public void LogError(string message)
        {
           
        }

        public void LogDebug(string message)
        {
           
        }
    }
}
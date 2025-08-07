// MaxChemical.Modules.Designer.Services.Execution.FlowExecutionEngine.cs
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

namespace MaxChemical.Modules.Designer.Services.Execution
{
    public class FlowExecutionEngine : IFlowExecutionEngine
    {
        private readonly ILogService _logger;
        private FlowExecutionState _state = FlowExecutionState.Idle;
        private CancellationTokenSource _cancellationTokenSource;
        private readonly ManualResetEventSlim _pauseEvent = new ManualResetEventSlim(true);
        private FlowExecutionContext _executionContext;
        private readonly INodeExecutionStatusService _statusService;
        public FlowExecutionState State
        {
            get => _state;
            private set
            {
                var oldState = _state;
                _state = value;
                StateChanged?.Invoke(this, new FlowExecutionStateChangedEventArgs
                {
                    OldState = oldState,
                    NewState = value
                });
            }
        }

        public event EventHandler<FlowExecutionStateChangedEventArgs> StateChanged;
        public event EventHandler<NodeExecutionEventArgs> NodeExecuting;
        public event EventHandler<NodeExecutionEventArgs> NodeExecuted;
        public event EventHandler<FlowExecutionErrorEventArgs> ExecutionError;
        public event EventHandler<FlowExecutionProgressEventArgs> ProgressChanged;

        public FlowExecutionEngine(INodeExecutionStatusService statusService)
        {
            _logger = _logger?.ForContext<FlowExecutionEngine>() ?? new LogService().ForContext<FlowExecutionEngine>();
            _statusService = statusService ?? throw new ArgumentNullException(nameof(statusService));
        }

        public async Task<FlowExecutionResult> ExecuteAsync(FlowDesignerViewModel flowViewModel, CancellationToken cancellationToken = default)
        {
            if (State != FlowExecutionState.Idle)
            {
                throw new InvalidOperationException("执行引擎正在运行中");
            }

            var startTime = DateTime.Now;
            _executionContext = new FlowExecutionContext();
            _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            try
            {
                // 设置当前流程视图模型
                _statusService.SetCurrentFlowViewModel(flowViewModel);

                // 重置所有节点状态
                _statusService.ResetAllExecutionStatus();

                State = FlowExecutionState.Running;
                _logger.LogInformation("开始执行流程，共 {Count} 个节点", flowViewModel.CommandNodes.Count);

                // 验证流程
                var validation = ValidateFlow(flowViewModel);
                if (!validation.IsValid)
                {
                    throw new InvalidOperationException($"流程验证失败: {string.Join(", ", validation.Errors)}");
                }

                var totalNodes = CalculateTotalNodes(flowViewModel.CommandNodes);
                var completedNodes = 0;

                // 按顺序执行节点
                foreach (var node in flowViewModel.CommandNodes)
                {
                    // 检查取消和暂停
                    _cancellationTokenSource.Token.ThrowIfCancellationRequested();
                    _pauseEvent.Wait(_cancellationTokenSource.Token);

                    if (State == FlowExecutionState.Stopping)
                        break;

                    await ExecuteNodeAsync(node, _executionContext, _cancellationTokenSource.Token);

                    completedNodes++;
                    ProgressChanged?.Invoke(this, new FlowExecutionProgressEventArgs
                    {
                        TotalNodes = totalNodes,
                        CompletedNodes = completedNodes,
                        ProgressPercentage = (double)completedNodes / totalNodes * 100,
                        CurrentNode = node
                    });
                }

                State = FlowExecutionState.Completed;
                _logger.LogInformation("流程执行完成");

                // 延迟重置状态
                _ = Task.Delay(2000).ContinueWith(_ =>
                {
                    if (State == FlowExecutionState.Completed)
                    {
                        State = FlowExecutionState.Idle;
                    }
                });

                return new FlowExecutionResult
                {
                    IsSuccessful = true,
                    Duration = DateTime.Now - startTime,
                    TotalNodesExecuted = completedNodes,
                    StartTime = startTime,
                    EndTime = DateTime.Now
                };
            }
            catch (OperationCanceledException)
            {
                // 重置所有节点状态
                _statusService.ResetAllExecutionStatus();

                State = FlowExecutionState.Stopped;
                _logger.LogInformation("流程执行被取消");

                _ = Task.Delay(1000).ContinueWith(_ =>
                {
                    if (State == FlowExecutionState.Stopped)
                    {
                        State = FlowExecutionState.Idle;
                    }
                });

                return new FlowExecutionResult
                {
                    IsSuccessful = false,
                    Duration = DateTime.Now - startTime,
                    ErrorMessage = "执行被取消",
                    StartTime = startTime,
                    EndTime = DateTime.Now
                };
            }
            catch (Exception ex)
            {
                // 重置所有节点状态
                _statusService.ResetAllExecutionStatus();

                State = FlowExecutionState.Error;
                _logger.LogError(ex, "流程执行失败");

                ExecutionError?.Invoke(this, new FlowExecutionErrorEventArgs
                {
                    Exception = ex,
                    ErrorMessage = ex.Message
                });

                _ = Task.Delay(3000).ContinueWith(_ =>
                {
                    if (State == FlowExecutionState.Error)
                    {
                        State = FlowExecutionState.Idle;
                    }
                });

                return new FlowExecutionResult
                {
                    IsSuccessful = false,
                    Duration = DateTime.Now - startTime,
                    ErrorMessage = ex.Message,
                    Exception = ex,
                    StartTime = startTime,
                    EndTime = DateTime.Now
                };
            }
            finally
            {
                _cancellationTokenSource?.Dispose();
                _cancellationTokenSource = null;
            }
        }

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

                // 设置节点为执行状态
                _statusService.StartNodeExecution(node.NodeId);

                NodeExecuting?.Invoke(this, executionArgs);

                object result = null;

                if (node.DeviceCommand != null)
                {
                    // 执行设备命令
                    result = await ExecuteDeviceCommandAsync(node.DeviceCommand, context, cancellationToken);
                }
                else if (node.LogicCommand != null)
                {
                    // 执行逻辑命令
                    result = await ExecuteLogicCommandAsync(node, context, cancellationToken);
                }

                executionArgs.EndTime = DateTime.Now;
                executionArgs.IsSuccessful = true;
                executionArgs.Result = result;

                // 设置节点为完成状态
                _statusService.CompleteNodeExecution(node.NodeId, true);

                // 记录执行历史
                context.ExecutionHistory.Add(new NodeExecutionRecord
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
                executionArgs.EndTime = DateTime.Now;
                executionArgs.IsSuccessful = false;
                executionArgs.ErrorMessage = ex.Message;

                // 设置节点为失败状态
                _statusService.CompleteNodeExecution(node.NodeId, false);

                // 记录执行历史
                context.ExecutionHistory.Add(new NodeExecutionRecord
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

        private async Task<object> ExecuteDeviceCommandAsync(DeviceCommand deviceCommand, FlowExecutionContext context, CancellationToken cancellationToken)
        {
            try
            {
                _logger.LogDebug("执行设备命令: {CommandName}", deviceCommand.Name);

                // 准备输入参数
                var inputParameters = deviceCommand.InputParameters ?? new DeviceParameters();

                // 执行命令
                var result = deviceCommand.Action?.Invoke(inputParameters);

                // 模拟异步执行时间
                await Task.Delay(100, cancellationToken);

                _logger.LogDebug("设备命令执行完成: {CommandName}", deviceCommand.Name);
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "设备命令执行失败: {CommandName}", deviceCommand.Name);
                throw;
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

                default:
                    _logger.LogWarning("未知的逻辑命令类型: {CommandType}", node.LogicCommand.CommandType);
                    return null;
            }
        }

        private async Task<object> ExecuteIfCommandAsync(CommandNode ifNode, FlowExecutionContext context, CancellationToken cancellationToken)
        {
            _logger.LogDebug("执行IF命令: {NodeName}", ifNode.DisplayName);

            try
            {
                // 获取条件
                var condition = ifNode.LogicCommand.Properties?.GetValueOrDefault("Condition", "true")?.ToString() ?? "true";

                // 评估条件
                var conditionResult = context.EvaluateCondition(condition);
                _logger.LogDebug("IF条件评估结果: {Condition} = {Result}", condition, conditionResult);

                // 执行对应分支
                var branchToExecute = conditionResult ? "True" : "False";
                var branchChildren = ifNode.Children?.Where(child =>
                    child.GetExecutionProperty<string>("BranchType") == branchToExecute).ToList() ?? new List<CommandNode>();

                _logger.LogDebug("执行 {Branch} 分支，包含 {Count} 个子节点", branchToExecute, branchChildren.Count);

                foreach (var childNode in branchChildren)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    _pauseEvent.Wait(cancellationToken);

                    await ExecuteNodeAsync(childNode, context, cancellationToken);
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
                // 获取循环参数
                var loopCountStr = loopNode.LogicCommand.Properties?.GetValueOrDefault("LoopCount", "1")?.ToString() ?? "1";
                var loopVariable = loopNode.LogicCommand.Properties?.GetValueOrDefault("LoopVariable", "i")?.ToString() ?? "i";

                if (!int.TryParse(loopCountStr, out var loopCount))
                {
                    loopCount = 1;
                }

                _logger.LogDebug("循环参数: 次数={LoopCount}, 变量={LoopVariable}", loopCount, loopVariable);

                // 执行循环
                for (int i = 0; i < loopCount; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    _pauseEvent.Wait(cancellationToken);

                    // 设置循环变量
                    context.SetVariable(loopVariable, i);
                    _logger.LogDebug("循环第 {Current}/{Total} 次，{Variable}={Value}", i + 1, loopCount, loopVariable, i);

                    // 执行循环体 - 修改这里
                    foreach (var childNode in loopNode.Children ?? Enumerable.Empty<CommandNode>())
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        _pauseEvent.Wait(cancellationToken);

                        await ExecuteNodeAsync(childNode, context, cancellationToken);
                    }
                }

                _logger.LogDebug("循环命令执行完成: {NodeName}", loopNode.DisplayName);
                return loopCount;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "循环命令执行失败: {NodeName}", loopNode.DisplayName);
                throw;
            }
        }

        private async Task<object> ExecuteParallelCommandAsync(CommandNode parallelNode, FlowExecutionContext context, CancellationToken cancellationToken)
        {
            _logger.LogDebug("执行并行命令: {NodeName}", parallelNode.DisplayName);

            try
            {
                // 获取最大并行数
                var maxParallelStr = parallelNode.LogicCommand.Properties?.GetValueOrDefault("MaxParallelCount", "4")?.ToString() ?? "4";
                if (!int.TryParse(maxParallelStr, out var maxParallel))
                {
                    maxParallel = 4;
                }

                // 按分支分组子节点
                var branchGroups = parallelNode.Children?.GroupBy(child =>
                    child.GetExecutionProperty<int>("BranchIndex")).ToList() ?? new List<IGrouping<int, CommandNode>>();

                _logger.LogDebug("并行执行 {BranchCount} 个分支，最大并行数: {MaxParallel}", branchGroups.Count, maxParallel);

                // 创建并行任务
                var tasks = new List<Task>();

                foreach (var branchGroup in branchGroups)
                {
                    var branchIndex = branchGroup.Key;
                    var branchNodes = branchGroup.ToList();

                    var task = Task.Run(async () =>
                    {
                        _logger.LogDebug("开始执行并行分支 {BranchIndex}，包含 {Count} 个节点", branchIndex, branchNodes.Count);

                        foreach (var childNode in branchNodes)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            _pauseEvent.Wait(cancellationToken);

                            await ExecuteNodeAsync(childNode, context, cancellationToken);
                        }

                        _logger.LogDebug("并行分支 {BranchIndex} 执行完成", branchIndex);
                    }, cancellationToken);

                    tasks.Add(task);
                }

                // 等待所有分支完成
                await Task.WhenAll(tasks);

                _logger.LogDebug("并行命令执行完成: {NodeName}", parallelNode.DisplayName);
                return branchGroups.Count;
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
                // 获取等待时间
                var waitTimeStr = waitNode.LogicCommand.Properties?.GetValueOrDefault("WaitTime", "1000")?.ToString() ?? "1000";
                if (!int.TryParse(waitTimeStr, out var waitTimeMs))
                {
                    waitTimeMs = 1000;
                }

                _logger.LogDebug("等待 {WaitTime} 毫秒", waitTimeMs);

                // 执行等待
                await Task.Delay(waitTimeMs, cancellationToken);

                _logger.LogDebug("等待命令执行完成: {NodeName}", waitNode.DisplayName);
                return waitTimeMs;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "等待命令执行失败: {NodeName}", waitNode.DisplayName);
                throw;
            }
        }

        private async Task<object> ExecuteSetVariableCommandAsync(CommandNode setVarNode, FlowExecutionContext context, CancellationToken cancellationToken)
        {
            _logger.LogDebug("执行设置变量命令: {NodeName}", setVarNode.DisplayName);

            try
            {
                // 获取变量名和值
                var variableName = setVarNode.LogicCommand.Properties?.GetValueOrDefault("VariableName", "")?.ToString() ?? "";
                var variableValue = setVarNode.LogicCommand.Properties?.GetValueOrDefault("VariableValue", "")?.ToString() ?? "";

                if (string.IsNullOrEmpty(variableName))
                {
                    throw new InvalidOperationException("变量名不能为空");
                }

                // 设置变量
                context.SetVariable(variableName, variableValue);
                _logger.LogDebug("设置变量: {Name} = {Value}", variableName, variableValue);

                // 模拟执行时间
                await Task.Delay(50, cancellationToken);

                _logger.LogDebug("设置变量命令执行完成: {NodeName}", setVarNode.DisplayName);
                return variableValue;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "设置变量命令执行失败: {NodeName}", setVarNode.DisplayName);
                throw;
            }
        }

        public async Task PauseAsync()
        {
            if (State == FlowExecutionState.Running)
            {
                State = FlowExecutionState.Paused;
                _pauseEvent.Reset();
                _logger.LogInformation("流程执行已暂停");
            }
        }

        public async Task ResumeAsync()
        {
            if (State == FlowExecutionState.Paused)
            {
                State = FlowExecutionState.Running;
                _pauseEvent.Set();
                _logger.LogInformation("流程执行已恢复");
            }
        }

        public async Task StopAsync()
        {
            if (State == FlowExecutionState.Running || State == FlowExecutionState.Paused)
            {
                State = FlowExecutionState.Stopping;
                _pauseEvent.Set(); // 确保恢复执行以便能够响应停止
                _cancellationTokenSource?.Cancel();
                _logger.LogInformation("流程执行停止中...");
            }
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

                // 验证每个节点
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

            // 基本验证
            if (string.IsNullOrEmpty(node.DisplayName))
            {
                result.Warnings.Add($"节点缺少显示名称: {node.NodeId}");
            }

            // 设备命令验证
            if (node.DeviceCommand != null)
            {
                if (string.IsNullOrEmpty(node.DeviceCommand.Name))
                {
                    result.Errors.Add($"设备命令缺少名称: {node.NodeId}");
                }
            }
            // 逻辑命令验证
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

                // 递归验证子节点
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
            var loopCountStr = loopNode.LogicCommand.Properties?.GetValueOrDefault("LoopCount", "")?.ToString();
            if (string.IsNullOrWhiteSpace(loopCountStr) || !int.TryParse(loopCountStr, out var loopCount) || loopCount <= 0)
            {
                result.Errors.Add($"循环节点的循环次数无效: {loopNode.DisplayName}");
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
                total++; // 当前节点
                if (node.HasChildren)
                {
                    total += CalculateTotalNodes(node.Children); // 子节点
                }
            }
            return total;
        }
    }
}
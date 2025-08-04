using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DevicePlugins.Devices;
using FlowEngine.Core;

namespace FlowEngine.Nodes
{
    /// <summary>
    /// 设备命令节点
    /// </summary>
    public class DeviceCommandNode : IFlowNode
    {
        public string Id { get; } = Guid.NewGuid().ToString();
        public string Name { get; set; } = "设备命令";
        public string Description { get; set; }
        public string DeviceName { get; set; }
        public string CommandName { get; set; }
        public Dictionary<string, string> InputParameterValues { get; set; } = new Dictionary<string, string>();
        public bool ContinueOnError { get; set; } = false;

        public async Task<FlowExecutionResult> ExecuteAsync(FlowContext context, CancellationToken cancellationToken)
        {
            var startTime = DateTime.Now;

            try
            {
                context.Logger?.LogInfo($"执行设备命令: {DeviceName}.{CommandName}");

                var deviceService = context.ServiceProvider?.GetService<IDeviceService>();
                if (deviceService == null)
                {
                    throw new InvalidOperationException("设备服务未注册");
                }

                var device = deviceService.GetDevice(DeviceName);
                if (device == null)
                {
                    throw new InvalidOperationException($"设备 {DeviceName} 未找到");
                }

                var command = device.Commands.FirstOrDefault(c => c.Name == CommandName);
                if (command == null)
                {
                    throw new InvalidOperationException($"设备 {DeviceName} 中未找到命令 {CommandName}");
                }

                if (!deviceService.IsDeviceConnected(DeviceName))
                {
                    throw new InvalidOperationException($"设备 {DeviceName} 未连接");
                }

                var inputParams = CreateInputParameters(command.InputParameters, context);

                var result = await deviceService.ExecuteCommandAsync(
                    DeviceName, CommandName, inputParams, cancellationToken);

                var duration = DateTime.Now - startTime;

                if (result.IsSuccess)
                {
                    SaveOutputToContext(result.OutputData, context);

                    return new FlowExecutionResult
                    {
                        IsSuccess = true,
                        Status = FlowExecutionStatus.Success,
                        Message = result.Message,
                        OutputData = result.GetOutputDictionary(),
                        Duration = duration
                    };
                }
                else
                {
                    if (ContinueOnError)
                    {
                        context.Logger?.LogWarning($"设备命令执行失败但继续: {result.Message}");
                        return new FlowExecutionResult
                        {
                            IsSuccess = true,
                            Status = FlowExecutionStatus.Warning,
                            Message = $"命令失败但继续执行: {result.Message}",
                            Duration = duration
                        };
                    }
                    else
                    {
                        throw new InvalidOperationException(result.Message);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                return new FlowExecutionResult
                {
                    IsSuccess = false,
                    Status = FlowExecutionStatus.Cancelled,
                    Message = "设备命令被取消",
                    Duration = DateTime.Now - startTime
                };
            }
            catch (Exception ex)
            {
                context.Logger?.LogError($"设备命令执行失败: {ex.Message}");

                return new FlowExecutionResult
                {
                    IsSuccess = false,
                    Status = FlowExecutionStatus.Failed,
                    Message = ex.Message,
                    Exception = ex,
                    Duration = DateTime.Now - startTime
                };
            }
        }

        public void Validate()
        {
            if (string.IsNullOrWhiteSpace(DeviceName))
                throw new InvalidOperationException("设备名称不能为空");

            if (string.IsNullOrWhiteSpace(CommandName))
                throw new InvalidOperationException("命令名称不能为空");
        }

        public IFlowNode Clone()
        {
            return new DeviceCommandNode
            {
                Name = this.Name,
                Description = this.Description,
                DeviceName = this.DeviceName,
                CommandName = this.CommandName,
                InputParameterValues = new Dictionary<string, string>(this.InputParameterValues),
                ContinueOnError = this.ContinueOnError
            };
        }

        private DeviceParameters CreateInputParameters(DeviceParameters template, FlowContext context)
        {
            if (template?.Variables == null)
            {
                return new DeviceParameters();
            }

            var inputParams = new DeviceParameters();
            var evaluator = new ExpressionEvaluator(context.GetAllVariables());

            foreach (var templateParam in template.Variables)
            {
                var param = CloneParameter(templateParam);

                if (InputParameterValues.TryGetValue(templateParam.Name, out var configuredValue))
                {
                    var resolvedValue = evaluator.Evaluate(configuredValue);
                    param.Value = ConvertValueToParameterType(resolvedValue, param);
                }

                inputParams.Variables.Add(param);
            }

            return inputParams;
        }

        private ParameterBase CloneParameter(ParameterBase original)
        {
            switch (original)
            {
                case NumberParameter numParam:
                    var newNumParam = new NumberParameter(numParam.Name);
                    newNumParam.Value = numParam.Value;
                    newNumParam.HelpText = numParam.HelpText;
                    return newNumParam;

                case StringParameter strParam:
                    var newStrParam = new StringParameter(strParam.Name);
                    newStrParam.Value = strParam.Value?.ToString();
                    newStrParam.HelpText = strParam.HelpText;
                    newStrParam.Options = strParam.Options;
                    return newStrParam;

                case BooleanParameter boolParam:
                    var newBoolParam = new BooleanParameter(boolParam.Name);
                    newBoolParam.Value = boolParam.Value;
                    newBoolParam.HelpText = boolParam.HelpText;
                    return newBoolParam;

                default:
                    var cloned = (ParameterBase)Activator.CreateInstance(original.GetType(), original.Name);
                    cloned.HelpText = original.HelpText;
                    cloned.Value = original.Value;
                    return cloned;
            }
        }

        private object ConvertValueToParameterType(object value, ParameterBase parameter)
        {
            try
            {
                switch (parameter)
                {
                    case NumberParameter:
                        return Convert.ToDouble(value);

                    case BooleanParameter:
                        return Convert.ToBoolean(value);

                    case StringParameter:
                        return value?.ToString() ?? string.Empty;

                    default:
                        return value;
                }
            }
            catch (Exception)
            {
                return value;
            }
        }

        private void SaveOutputToContext(DeviceParameters outputParams, FlowContext context)
        {
            if (outputParams?.Variables == null) return;

            foreach (var param in outputParams.Variables)
            {
                var variableName = $"{DeviceName}_{CommandName}_{param.Name}";
                context.SetVariable(variableName, param.Value);

                var shortName = param.Name;
                if (!context.HasVariable(shortName))
                {
                    context.SetVariable(shortName, param.Value);
                }
            }
        }

        public void SetInputParameterValue(string parameterName, string valueExpression)
        {
            InputParameterValues[parameterName] = valueExpression;
        }

        public string GetInputParameterValue(string parameterName)
        {
            return InputParameterValues.TryGetValue(parameterName, out var value) ? value : null;
        }
    }

    /// <summary>
    /// 变量设置节点
    /// </summary>
    public class SetVariableNode : IFlowNode
    {
        public string Id { get; } = Guid.NewGuid().ToString();
        public string Name { get; set; } = "设置变量";
        public string Description { get; set; }
        public string VariableName { get; set; }
        public string ValueExpression { get; set; }

        public async Task<FlowExecutionResult> ExecuteAsync(FlowContext context, CancellationToken cancellationToken)
        {
            var startTime = DateTime.Now;

            try
            {
                var evaluator = new ExpressionEvaluator(context.GetAllVariables());
                var value = evaluator.Evaluate(ValueExpression);

                context.SetVariable(VariableName, value);

                return new FlowExecutionResult
                {
                    IsSuccess = true,
                    Status = FlowExecutionStatus.Success,
                    Message = $"变量 {VariableName} 设置为 {value}",
                    OutputData = new Dictionary<string, object> { [VariableName] = value },
                    Duration = DateTime.Now - startTime
                };
            }
            catch (Exception ex)
            {
                return new FlowExecutionResult
                {
                    IsSuccess = false,
                    Status = FlowExecutionStatus.Failed,
                    Message = ex.Message,
                    Exception = ex,
                    Duration = DateTime.Now - startTime
                };
            }
        }

        public void Validate()
        {
            if (string.IsNullOrWhiteSpace(VariableName))
                throw new InvalidOperationException("变量名不能为空");

            if (string.IsNullOrWhiteSpace(ValueExpression))
                throw new InvalidOperationException("变量值表达式不能为空");
        }

        public IFlowNode Clone()
        {
            return new SetVariableNode
            {
                Name = this.Name,
                Description = this.Description,
                VariableName = this.VariableName,
                ValueExpression = this.ValueExpression
            };
        }
    }

    /// <summary>
    /// 延时节点
    /// </summary>
    public class DelayNode : IFlowNode
    {
        public string Id { get; } = Guid.NewGuid().ToString();
        public string Name { get; set; } = "延时等待";
        public string Description { get; set; }
        public string DelayExpression { get; set; } = "1000";

        public async Task<FlowExecutionResult> ExecuteAsync(FlowContext context, CancellationToken cancellationToken)
        {
            var startTime = DateTime.Now;

            try
            {
                var evaluator = new ExpressionEvaluator(context.GetAllVariables());
                var delayMs = (int)evaluator.EvaluateNumber(DelayExpression);

                context.Logger?.LogInfo($"延时等待 {delayMs}ms");

                await Task.Delay(delayMs, cancellationToken);

                return new FlowExecutionResult
                {
                    IsSuccess = true,
                    Status = FlowExecutionStatus.Success,
                    Message = $"延时 {delayMs}ms 完成",
                    Duration = DateTime.Now - startTime
                };
            }
            catch (OperationCanceledException)
            {
                return new FlowExecutionResult
                {
                    IsSuccess = false,
                    Status = FlowExecutionStatus.Cancelled,
                    Message = "延时被取消",
                    Duration = DateTime.Now - startTime
                };
            }
            catch (Exception ex)
            {
                return new FlowExecutionResult
                {
                    IsSuccess = false,
                    Status = FlowExecutionStatus.Failed,
                    Message = ex.Message,
                    Exception = ex,
                    Duration = DateTime.Now - startTime
                };
            }
        }

        public void Validate()
        {
            if (string.IsNullOrWhiteSpace(DelayExpression))
                throw new InvalidOperationException("延时表达式不能为空");

            try
            {
                var evaluator = new ExpressionEvaluator();
                var delay = evaluator.EvaluateNumber(DelayExpression);
                if (delay < 0)
                    throw new InvalidOperationException("延时时间不能为负数");
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"延时表达式无效: {ex.Message}");
            }
        }

        public IFlowNode Clone()
        {
            return new DelayNode
            {
                Name = this.Name,
                Description = this.Description,
                DelayExpression = this.DelayExpression
            };
        }
    }

    /// <summary>
    /// 条件判断节点
    /// </summary>
    public class IfNode : IFlowNode
    {
        public string Id { get; } = Guid.NewGuid().ToString();
        public string Name { get; set; } = "条件判断";
        public string Description { get; set; }
        public string Condition { get; set; }
        public List<IFlowNode> TrueNodes { get; set; } = new List<IFlowNode>();
        public List<IFlowNode> FalseNodes { get; set; } = new List<IFlowNode>();

        public async Task<FlowExecutionResult> ExecuteAsync(FlowContext context, CancellationToken cancellationToken)
        {
            var startTime = DateTime.Now;

            try
            {
                var evaluator = new ExpressionEvaluator(context.GetAllVariables());
                var conditionResult = evaluator.EvaluateBoolean(Condition);

                context.Logger?.LogInfo($"条件判断结果: {conditionResult} (条件: {Condition})");

                var nodesToExecute = conditionResult ? TrueNodes : FalseNodes;

                foreach (var node in nodesToExecute)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        return new FlowExecutionResult
                        {
                            IsSuccess = false,
                            Status = FlowExecutionStatus.Cancelled,
                            Message = "条件执行被取消"
                        };
                    }

                    var result = await node.ExecuteAsync(context, cancellationToken);
                    if (!result.IsSuccess)
                    {
                        return result;
                    }
                }

                return new FlowExecutionResult
                {
                    IsSuccess = true,
                    Status = FlowExecutionStatus.Success,
                    Message = $"条件判断完成，执行了 {(conditionResult ? "True" : "False")} 分支",
                    Duration = DateTime.Now - startTime
                };
            }
            catch (Exception ex)
            {
                return new FlowExecutionResult
                {
                    IsSuccess = false,
                    Status = FlowExecutionStatus.Failed,
                    Message = ex.Message,
                    Exception = ex,
                    Duration = DateTime.Now - startTime
                };
            }
        }

        public void Validate()
        {
            if (string.IsNullOrWhiteSpace(Condition))
                throw new InvalidOperationException("条件表达式不能为空");

            foreach (var node in TrueNodes.Concat(FalseNodes))
            {
                node.Validate();
            }
        }

        public IFlowNode Clone()
        {
            return new IfNode
            {
                Name = this.Name,
                Description = this.Description,
                Condition = this.Condition,
                TrueNodes = this.TrueNodes.Select(n => n.Clone()).ToList(),
                FalseNodes = this.FalseNodes.Select(n => n.Clone()).ToList()
            };
        }
    }

    /// <summary>
    /// 循环节点
    /// </summary>
    public class LoopNode : IFlowNode
    {
        public string Id { get; } = Guid.NewGuid().ToString();
        public string Name { get; set; } = "循环";
        public string Description { get; set; }
        public string Condition { get; set; }
        public int MaxIterations { get; set; } = 1000;
        public List<IFlowNode> BodyNodes { get; set; } = new List<IFlowNode>();

        public async Task<FlowExecutionResult> ExecuteAsync(FlowContext context, CancellationToken cancellationToken)
        {
            var startTime = DateTime.Now;
            int iterations = 0;

            try
            {
                var evaluator = new ExpressionEvaluator(context.GetAllVariables());

                while (evaluator.EvaluateBoolean(Condition) && iterations < MaxIterations)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        return new FlowExecutionResult
                        {
                            IsSuccess = false,
                            Status = FlowExecutionStatus.Cancelled,
                            Message = $"循环在第 {iterations} 次迭代时被取消"
                        };
                    }

                    context.Logger?.LogDebug($"开始第 {iterations + 1} 次循环迭代");

                    foreach (var node in BodyNodes)
                    {
                        var result = await node.ExecuteAsync(context, cancellationToken);
                        if (!result.IsSuccess)
                        {
                            return new FlowExecutionResult
                            {
                                IsSuccess = false,
                                Status = result.Status,
                                Message = $"循环第 {iterations + 1} 次迭代失败: {result.Message}",
                                Exception = result.Exception,
                                Duration = DateTime.Now - startTime
                            };
                        }
                    }

                    iterations++;
                    evaluator = new ExpressionEvaluator(context.GetAllVariables());
                }

                if (iterations >= MaxIterations)
                {
                    context.Logger?.LogWarning($"循环达到最大迭代次数 {MaxIterations}");
                }

                return new FlowExecutionResult
                {
                    IsSuccess = true,
                    Status = FlowExecutionStatus.Success,
                    Message = $"循环完成，共执行 {iterations} 次迭代",
                    Duration = DateTime.Now - startTime
                };
            }
            catch (Exception ex)
            {
                return new FlowExecutionResult
                {
                    IsSuccess = false,
                    Status = FlowExecutionStatus.Failed,
                    Message = $"循环执行失败 (第 {iterations + 1} 次迭代): {ex.Message}",
                    Exception = ex,
                    Duration = DateTime.Now - startTime
                };
            }
        }

        public void Validate()
        {
            if (string.IsNullOrWhiteSpace(Condition))
                throw new InvalidOperationException("循环条件不能为空");

            if (MaxIterations <= 0)
                throw new InvalidOperationException("最大迭代次数必须大于0");

            foreach (var node in BodyNodes)
            {
                node.Validate();
            }
        }

        public IFlowNode Clone()
        {
            return new LoopNode
            {
                Name = this.Name,
                Description = this.Description,
                Condition = this.Condition,
                MaxIterations = this.MaxIterations,
                BodyNodes = this.BodyNodes.Select(n => n.Clone()).ToList()
            };
        }
    }
}
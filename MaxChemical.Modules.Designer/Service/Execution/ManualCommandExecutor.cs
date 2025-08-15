using DevicePlugins.Devices;
using MaxChemical.Modules.Designer.Event;
using MaxChemical.Modules.Designer.Models;
using MaxChemical.Modules.Designer.ViewModels;
using Prism.Events;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MaxChemical.Modules.Designer.Service.Execution
{
    public class ManualCommandExecutor
    {
        private readonly IEventAggregator _eventAggregator;
        private readonly ConcurrentDictionary<string, CancellationTokenSource> _ctsMap = new();

        public ManualCommandExecutor(IEventAggregator eventAggregator)
        {
            _eventAggregator = eventAggregator;
        }

        public bool IsRunning(string nodeId) => _ctsMap.ContainsKey(nodeId);

        public async Task StartAsync(CommandNode node)
        {
            if (node?.DeviceCommand == null)
                throw new InvalidOperationException("当前节点不是设备命令。");

            if (string.IsNullOrEmpty(node.BoundDeviceId))
                throw new InvalidOperationException("设备命令未绑定设备。");

            if (_ctsMap.ContainsKey(node.NodeId))
                throw new InvalidOperationException("该命令正在执行中。");

            // 请求画布设备
            List<CanvasDeviceViewModel> canvasDevices = null;
            _eventAggregator?.GetEvent<RequestCanvasDevicesEvent>()?.Publish(list => canvasDevices = list);

            var targetVm = canvasDevices?.FirstOrDefault(d => d.DeviceId == node.BoundDeviceId);
            var target = targetVm?.Device;
            if (target == null)
                throw new InvalidOperationException("未在画布上找到绑定的设备。");

            var cmd = target.Commands?.FirstOrDefault(c => c.Name == node.DeviceCommand.Name);
            if (cmd == null)
                throw new InvalidOperationException($"设备不支持命令：{node.DeviceCommand.Name}");

            var cts = new CancellationTokenSource();
            if (!_ctsMap.TryAdd(node.NodeId, cts))
                cts.Dispose();

            node.IsManualExecuting = true;
            node.ExecutionStatus = CommandNode.NodeExecutionStatus.Executing;

            // 动画开始
            _eventAggregator?.GetEvent<DeviceExecutionStartedEvent>()?.Publish(new DeviceExecutionEventArgs
            {
                DeviceId = node.BoundDeviceId,
                CommandName = cmd.Name
            });

            node.Device = target; // 保存设备引用
            //node.BoundDeviceName = target.Name;

            try
            {
                // 构建"生效参数"的独立副本，避免污染模板
                var input = BuildEffectiveParametersCopy(node);

                // 声明输出参数变量
                DeviceParameters outputParams = null;

                await Task.Run(() =>
                {
                    if (cts.IsCancellationRequested) return;

                    // 执行命令并获取输出参数
                    outputParams = cmd.Action?.Invoke(input);
                }, cts.Token);

                if (!cts.IsCancellationRequested && outputParams != null)
                {
                    // 保存执行结果
                    node.LastExecutionResult = new DeviceCommandResult
                    {
                        Success = true,
                        OutputParameters = outputParams,
                        ExecutionTime = DateTime.Now
                    };

                    // 发布结果更新事件
                    _eventAggregator?.GetEvent<NodeExecutionResultUpdatedEvent>()?.Publish(new NodeExecutionResultEventArgs
                    {
                        NodeId = node.NodeId,
                        DeviceName = node.BoundDeviceName,
                        CommandName = cmd.Name,
                        OutputParameters = outputParams
                    });

                    node.ExecutionStatus = CommandNode.NodeExecutionStatus.Completed;
                }
                else if (cts.IsCancellationRequested)
                {
                    node.ExecutionStatus = CommandNode.NodeExecutionStatus.Failed;
                    node.LastExecutionResult = new DeviceCommandResult
                    {
                        Success = false,
                        ErrorMessage = "执行被取消",
                        ExecutionTime = DateTime.Now
                    };
                }
                else
                {
                    node.ExecutionStatus = CommandNode.NodeExecutionStatus.Completed;
                    node.LastExecutionResult = new DeviceCommandResult
                    {
                        Success = true,
                        OutputParameters = outputParams ?? new DeviceParameters(),
                        ExecutionTime = DateTime.Now
                    };
                }

                _eventAggregator?.GetEvent<DeviceExecutionEndedEvent>()?.Publish(new DeviceExecutionEventArgs
                {
                    DeviceId = node.BoundDeviceId,
                    CommandName = cmd.Name,
                    IsSuccessful = !cts.IsCancellationRequested
                });
            }
            catch (Exception ex)
            {
                node.ExecutionStatus = CommandNode.NodeExecutionStatus.Failed;
                node.LastExecutionResult = new DeviceCommandResult
                {
                    Success = false,
                    ErrorMessage = ex.Message,
                    Exception = ex,
                    ExecutionTime = DateTime.Now
                };

                _eventAggregator?.GetEvent<DeviceExecutionEndedEvent>()?.Publish(new DeviceExecutionEventArgs
                {
                    DeviceId = node.BoundDeviceId,
                    CommandName = cmd.Name,
                    IsSuccessful = false,
                    ErrorMessage = ex.Message
                });
                throw;
            }
            finally
            {
                node.IsManualExecuting = false;
                if (_ctsMap.TryRemove(node.NodeId, out var token)) token.Dispose();
            }
        }

        public void Stop(string nodeId)
        {
            if (_ctsMap.TryGetValue(nodeId, out var cts))
            {
                cts.Cancel();
            }
        }

        private static DeviceParameters BuildEffectiveParametersCopy(CommandNode node)
        {
            var result = new DeviceParameters
            {
                Variables = new ObservableCollection<ParameterBase>()
            };

            var defs = node.DeviceCommand?.InputParameters?.Variables;
            if (defs == null) return result;

            foreach (var def in defs)
            {
                var name = def.Name;

                // 修复：先定义并初始化，再 TryGetValue
                string ovStr = null;
                if (node.ParameterOverrides != null)
                    node.ParameterOverrides.TryGetValue(name, out ovStr);

                ParameterBase clone = def switch
                {
                    StringParameter sp => CloneString(sp, ovStr),
                    NumberParameter np => CloneNumber(np, ovStr),
                    BooleanParameter bp => CloneBoolean(bp, ovStr),
                    EnumParameter ep => CloneEnum(ep, ovStr),
                    _ => CloneAsStringFallback(def, ovStr) // 兜底：未知类型按字符串处理
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
                double val;
                if (!string.IsNullOrWhiteSpace(overrideStr))
                {
                    if (!double.TryParse(overrideStr, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out val))
                        val = t.Value is double dv ? dv : 0d;
                }
                else
                {
                    val = t.Value is double dv ? dv : 0d;
                }

                var p = new NumberParameter(t.Name, t.MinValue, t.MaxValue, val, t.HelpText)
                {
                    DecimalPlaces = t.DecimalPlaces
                };
                return p;
            }

            static BooleanParameter CloneBoolean(BooleanParameter t, string overrideStr)
            {
                bool val;
                if (!string.IsNullOrWhiteSpace(overrideStr))
                {
                    if (bool.TryParse(overrideStr, out var b)) val = b;
                    else if (overrideStr == "1") val = true;
                    else if (overrideStr == "0") val = false;
                    else val = t.Value is bool bv && bv;
                }
                else
                {
                    val = t.Value is bool bv && bv;
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
                            val = Enum.Parse(t.EnumType, overrideStr, ignoreCase: true);
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
    }
}
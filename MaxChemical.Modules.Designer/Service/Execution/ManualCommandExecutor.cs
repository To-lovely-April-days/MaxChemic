using System;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DevicePlugins.Devices;
using MaxChemical.Modules.Designer.Event;
using MaxChemical.Modules.Designer.Models;
using MaxChemical.Modules.Designer.ViewModels;
using Prism.Events;
using System.Diagnostics;
using MaxChemical.Logging;

namespace MaxChemical.Modules.Designer.Service.Execution
{
    public class ManualCommandExecutor : IDisposable
    {
        private readonly IEventAggregator _eventAggregator;
        private readonly ConcurrentDictionary<string, CancellationTokenSource> _ctsMap = new();
        private readonly ILogService _logger;

        // *** 新增：模拟模式状态管理（与FlowExecutionEngine保持一致） ***
        private bool _currentSimulationMode = false;
        private readonly ConcurrentDictionary<string, bool> _originalDeviceSimulationModes = new();
        private bool _disposed = false;

        public ManualCommandExecutor(IEventAggregator eventAggregator)
        {
            _eventAggregator = eventAggregator ?? throw new ArgumentNullException(nameof(eventAggregator));
            _logger = new LogService().ForContext<ManualCommandExecutor>();

            // *** 订阅模拟模式相关事件（与FlowExecutionEngine保持一致） ***
            SubscribeSimulationModeEvents();

            // *** 获取当前全局模拟模式状态 ***
            InitializeGlobalSimulationMode();
        }

        #region *** 模拟模式管理（与FlowExecutionEngine保持一致） ***

        /// <summary>
        /// 订阅模拟模式相关事件
        /// </summary>
        private void SubscribeSimulationModeEvents()
        {
            try
            {
                // *** 修正：只订阅模拟模式变更事件，不订阅请求事件 ***
                _eventAggregator?.GetEvent<DeviceSimulationModeChangedEvent>()?.Subscribe(OnSimulationModeChanged,
                    ThreadOption.PublisherThread, true);

                _logger.LogDebug("ManualCommandExecutor: 已订阅模拟模式相关事件");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ManualCommandExecutor: 订阅模拟模式事件失败");
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
                _logger.LogInformation("ManualCommandExecutor: 模拟模式已更新: {Mode}",
                    _currentSimulationMode ? "模拟模式" : "实际模式");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ManualCommandExecutor: 处理模拟模式变更失败");
            }
        }

        /// <summary>
        /// 初始化获取当前全局模拟模式状态
        /// </summary>
        private void InitializeGlobalSimulationMode()
        {
            try
            {
                bool globalSimulationMode = false;
                bool hasResponse = false;
                var resetEvent = new ManualResetEventSlim(false);

                // 通过事件获取当前全局模拟模式状态
                _eventAggregator?.GetEvent<GetSimulationModeRequestEvent>()?.Publish((mode) =>
                {
                    globalSimulationMode = mode;
                    hasResponse = true;
                    resetEvent.Set();
                });

                // 等待500毫秒获取响应
                if (resetEvent.Wait(500))
                {
                    if (hasResponse)
                    {
                        _currentSimulationMode = globalSimulationMode;
                        _logger.LogDebug("ManualCommandExecutor: 初始化全局模拟模式状态成功: {Mode}",
                            _currentSimulationMode ? "模拟模式" : "实际模式");
                    }
                    else
                    {
                        _logger.LogDebug("ManualCommandExecutor: 模拟模式状态请求有响应但无数据，使用默认实际模式");
                        _currentSimulationMode = false;
                    }
                }
                else
                {
                    _logger.LogDebug("ManualCommandExecutor: 获取全局模拟模式状态超时，使用默认实际模式");
                    _currentSimulationMode = false;
                }

                resetEvent.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ManualCommandExecutor: 初始化全局模拟模式状态失败");
                _currentSimulationMode = false;
            }
        }

        /// <summary>
        /// 获取当前模拟模式状态（与FlowExecutionEngine保持一致）
        /// </summary>
        private bool GetCurrentSimulationMode()
        {
            bool isSimulationMode = _currentSimulationMode;

            try
            {
                // 通过事件获取最新的模拟模式状态
                var resetEvent = new ManualResetEventSlim(false);
                bool latestMode = _currentSimulationMode;
                bool hasResponse = false;

                _eventAggregator?.GetEvent<GetSimulationModeRequestEvent>()?.Publish((mode) =>
                {
                    latestMode = mode;
                    hasResponse = true;
                    resetEvent.Set();
                });

                // 等待500毫秒获取响应
                if (resetEvent.Wait(500))
                {
                    if (hasResponse)
                    {
                        isSimulationMode = latestMode;
                        _currentSimulationMode = latestMode; // 同步本地状态
                        _logger.LogDebug("ManualCommandExecutor: 获取到最新模拟模式状态: {Mode}", isSimulationMode ? "模拟模式" : "实际模式");
                    }
                    else
                    {
                        _logger.LogWarning("ManualCommandExecutor: 模拟模式状态请求有响应但无数据，使用缓存状态: {Mode}", _currentSimulationMode ? "模拟模式" : "实际模式");
                    }
                }
                else
                {
                    _logger.LogWarning("ManualCommandExecutor: 获取模拟模式状态超时，使用缓存状态: {Mode}", _currentSimulationMode ? "模拟模式" : "实际模式");
                }

                resetEvent.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ManualCommandExecutor: 获取模拟模式状态失败，使用缓存状态: {Mode}", _currentSimulationMode ? "模拟模式" : "实际模式");
            }

            _logger.LogDebug("ManualCommandExecutor: 当前模拟模式状态: {Mode}", isSimulationMode ? "模拟模式" : "实际模式");
            return isSimulationMode;
        }

        /// <summary>
        /// 为设备设置模拟模式并保存原始状态（统一使用IDevice接口）
        /// </summary>
        private void PrepareDeviceSimulationMode(IDevice device, string nodeId)
        {
            try
            {
                if (device == null) return;

                // 获取当前全局模拟模式状态
                bool currentSimulationMode = GetCurrentSimulationMode();

                // 保存设备原始的模拟模式状态
                if (!_originalDeviceSimulationModes.ContainsKey(nodeId))
                {
                    _originalDeviceSimulationModes.TryAdd(nodeId, device.IsSimulationMode);
                    _logger.LogDebug("ManualCommandExecutor: 保存设备 {DeviceName} 原始模拟模式: {Mode}",
                        device.Name, device.IsSimulationMode ? "模拟" : "实际");
                }

                // 根据全局模拟模式设置设备模式
                if (device.IsSimulationMode != currentSimulationMode)
                {
                    device.IsSimulationMode = currentSimulationMode;
                    _logger.LogInformation("ManualCommandExecutor: 调整设备模拟模式: {DeviceName} -> {Mode}",
                        device.Name, currentSimulationMode ? "模拟" : "实际");
                }
                else
                {
                    _logger.LogDebug("ManualCommandExecutor: 设备 {DeviceName} 已处于正确的模拟模式: {Mode}",
                        device.Name, currentSimulationMode ? "模拟" : "实际");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ManualCommandExecutor: 准备设备模拟模式失败");
            }
        }

        /// <summary>
        /// 恢复设备原始的模拟模式状态
        /// </summary>
        private void RestoreDeviceSimulationMode(IDevice device, string nodeId)
        {
            try
            {
                if (device == null) return;

                // 恢复设备原始的模拟模式状态
                if (_originalDeviceSimulationModes.TryRemove(nodeId, out var originalMode))
                {
                    if (device.IsSimulationMode != originalMode)
                    {
                        device.IsSimulationMode = originalMode;
                        _logger.LogDebug("ManualCommandExecutor: 设备 {DeviceName} 模拟模式已恢复为: {Mode}",
                            device.Name, originalMode ? "模拟" : "实际");
                    }
                    else
                    {
                        _logger.LogDebug("ManualCommandExecutor: 设备 {DeviceName} 模拟模式无需恢复", device.Name);
                    }
                }
                else
                {
                    _logger.LogDebug("ManualCommandExecutor: 未找到设备 {DeviceName} 的原始模拟模式状态", device.Name);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ManualCommandExecutor: 恢复设备模拟模式失败");
            }
        }

        /// <summary>
        /// 发布监控卡片更新事件（与FlowExecutionEngine保持一致）
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

                    _logger.LogDebug("ManualCommandExecutor: 发布监控卡片更新事件: 设备 {DeviceId}, 命令 {CommandName}, 参数数量 {Count}",
                        deviceId, commandName, outputParameters.Variables.Count);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ManualCommandExecutor: 发布监控卡片更新事件失败");
            }
        }

        #endregion

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

            // *** 修正：在修改任何状态之前先验证设备连接 ***
            // 先准备设备模拟模式（临时），用于连接验证
            bool originalSimulationMode = target.IsSimulationMode;
            bool currentSimulationMode = GetCurrentSimulationMode();

            // 临时设置模拟模式进行连接验证
            if (target.IsSimulationMode != currentSimulationMode)
            {
                target.IsSimulationMode = currentSimulationMode;
                _logger.LogDebug("ManualCommandExecutor: 临时调整设备模拟模式用于连接验证: {DeviceName} -> {Mode}",
                    target.Name, currentSimulationMode ? "模拟" : "实际");
            }

            try
            {
                // *** 在修改执行状态之前验证设备连接 ***
                await ValidateAndEnsureDeviceConnection(target, node.BoundDeviceId);
            }
            catch (Exception ex)
            {
                // 连接验证失败，恢复原始模拟模式
                target.IsSimulationMode = originalSimulationMode;
                _logger.LogError(ex, "ManualCommandExecutor: 设备连接验证失败，已恢复原始模拟模式: {DeviceName}", target.Name);
                throw; // 重新抛出异常
            }

            // *** 连接验证成功后才开始修改执行状态 ***
            var cts = new CancellationTokenSource();
            if (!_ctsMap.TryAdd(node.NodeId, cts))
            {
                // 恢复原始模拟模式
                target.IsSimulationMode = originalSimulationMode;
                cts.Dispose();
                throw new InvalidOperationException("该命令正在执行中。");
            }

            // 设置执行状态
            node.IsManualExecuting = true;
            node.ExecutionStatus = CommandNode.NodeExecutionStatus.Executing;

            // *** 正式保存设备原始模拟模式状态 ***
            if (!_originalDeviceSimulationModes.ContainsKey(node.NodeId))
            {
                _originalDeviceSimulationModes.TryAdd(node.NodeId, originalSimulationMode);
                _logger.LogDebug("ManualCommandExecutor: 保存设备 {DeviceName} 原始模拟模式: {Mode}",
                    target.Name, originalSimulationMode ? "模拟" : "实际");
            }

            // 保存设备引用
            node.Device = target;

            // *** 连接验证成功后才发布动画开始事件 ***
            _eventAggregator?.GetEvent<DeviceExecutionStartedEvent>()?.Publish(new DeviceExecutionEventArgs
            {
                DeviceId = node.BoundDeviceId,
                CommandName = cmd.Name,
                IsSimulationMode = currentSimulationMode
            });

            // *** 记录详细的执行信息 ***
            var sw = Stopwatch.StartNew();
            var inputParameters = BuildEffectiveParametersCopy(node);

            _logger.LogInformation(
                "ManualCommandExecutor: 执行设备命令开始: Node={Node} NodeId={NodeId} DeviceId={DeviceId} DeviceName={DeviceName} Command={Command} SimulationMode={SimulationMode} Params={Params}",
                node.DisplayName, node.NodeId, node.BoundDeviceId, target.Name, cmd.Name,
                currentSimulationMode ? "模拟" : "实际", FormatParameters(inputParameters));

            try
            {
                // 声明输出参数变量
                DeviceParameters outputParams = null;

                await Task.Run(() =>
                {
                    if (cts.IsCancellationRequested) return;

                    // 执行命令并获取输出参数
                    outputParams = cmd.Action?.Invoke(inputParameters);
                }, cts.Token);

                sw.Stop();

                if (!cts.IsCancellationRequested && outputParams != null)
                {
                    // 保存执行结果
                    node.LastExecutionResult = new DeviceCommandResult
                    {
                        Success = true,
                        OutputParameters = outputParams,
                        ExecutionTime = DateTime.Now,
                        IsSimulationMode = currentSimulationMode
                    };

                    // 发布结果更新事件
                    _eventAggregator?.GetEvent<NodeExecutionResultUpdatedEvent>()?.Publish(new NodeExecutionResultEventArgs
                    {
                        NodeId = node.NodeId,
                        DeviceName = node.BoundDeviceName,
                        CommandName = cmd.Name,
                        OutputParameters = outputParams,
                        IsSimulationMode = currentSimulationMode
                    });

                    // 发布监控卡片更新事件
                    PublishMonitoringCardUpdateEvent(node.BoundDeviceId, cmd.Name, outputParams);

                    node.ExecutionStatus = CommandNode.NodeExecutionStatus.Completed;

                    _logger.LogInformation(
                        "ManualCommandExecutor: 执行设备命令完成: Node={Node} NodeId={NodeId} DeviceId={DeviceId} DeviceName={DeviceName} Command={Command} SimulationMode={SimulationMode} DurationMs={Duration} Output={Output}",
                        node.DisplayName, node.NodeId, node.BoundDeviceId, target.Name, cmd.Name,
                        currentSimulationMode ? "模拟" : "实际", sw.ElapsedMilliseconds, FormatParameters(outputParams));
                }
                else if (cts.IsCancellationRequested)
                {
                    node.ExecutionStatus = CommandNode.NodeExecutionStatus.Failed;
                    node.LastExecutionResult = new DeviceCommandResult
                    {
                        Success = false,
                        ErrorMessage = "执行被取消",
                        ExecutionTime = DateTime.Now,
                        IsSimulationMode = currentSimulationMode
                    };

                    _logger.LogInformation("ManualCommandExecutor: 命令执行被取消: {Node}", node.DisplayName);
                }
                else
                {
                    node.ExecutionStatus = CommandNode.NodeExecutionStatus.Completed;
                    node.LastExecutionResult = new DeviceCommandResult
                    {
                        Success = true,
                        OutputParameters = outputParams ?? new DeviceParameters(),
                        ExecutionTime = DateTime.Now,
                        IsSimulationMode = currentSimulationMode
                    };

                    _logger.LogDebug("ManualCommandExecutor: 命令执行完成，无输出参数: {Node}", node.DisplayName);
                }

                _eventAggregator?.GetEvent<DeviceExecutionEndedEvent>()?.Publish(new DeviceExecutionEventArgs
                {
                    DeviceId = node.BoundDeviceId,
                    CommandName = cmd.Name,
                    IsSuccessful = !cts.IsCancellationRequested,
                    IsSimulationMode = currentSimulationMode
                });
            }
            catch (Exception ex)
            {
                sw.Stop();

                node.ExecutionStatus = CommandNode.NodeExecutionStatus.Failed;
                node.LastExecutionResult = new DeviceCommandResult
                {
                    Success = false,
                    ErrorMessage = ex.Message,
                    Exception = ex,
                    ExecutionTime = DateTime.Now,
                    IsSimulationMode = currentSimulationMode
                };

                _logger.LogError(ex,
                    "ManualCommandExecutor: 执行设备命令失败: Node={Node} NodeId={NodeId} DeviceId={DeviceId} DeviceName={DeviceName} Command={Command} SimulationMode={SimulationMode} DurationMs={Duration} Params={Params}",
                    node.DisplayName, node.NodeId, node.BoundDeviceId, target.Name, cmd.Name,
                    currentSimulationMode ? "模拟" : "实际", sw.ElapsedMilliseconds, FormatParameters(inputParameters));

                _eventAggregator?.GetEvent<DeviceExecutionEndedEvent>()?.Publish(new DeviceExecutionEventArgs
                {
                    DeviceId = node.BoundDeviceId,
                    CommandName = cmd.Name,
                    IsSuccessful = false,
                    ErrorMessage = ex.Message,
                    IsSimulationMode = currentSimulationMode
                });

                throw;
            }
            finally
            {
                // *** 清理执行状态 ***
                node.IsManualExecuting = false;

                // *** 恢复设备原始模拟模式状态 ***
                RestoreDeviceSimulationMode(target, node.NodeId);

                // *** 清理执行队列 ***
                if (_ctsMap.TryRemove(node.NodeId, out var token))
                    token.Dispose();

                _logger.LogDebug("ManualCommandExecutor: 命令执行清理完成: {Node}", node.DisplayName);
            }
        }

        /// <summary>
        /// 验证并确保设备连接（与FlowExecutionEngine保持一致）
        /// </summary>
        private async Task ValidateAndEnsureDeviceConnection(IDevice device, string deviceId)
        {
            try
            {
                _logger.LogDebug("ManualCommandExecutor: 验证设备连接: {DeviceName} ({DeviceId}) - 模拟模式: {SimulationMode}",
                    device.Name, deviceId, device.IsSimulationMode);

                // 在模拟模式下，跳过实际连接验证
                if (device.IsSimulationMode)
                {
                    _logger.LogDebug("ManualCommandExecutor: 模拟模式下，跳过设备连接验证: {DeviceName}", device.Name);
                    return;
                }

                // 实际模式下进行连接验证
                _logger.LogDebug("ManualCommandExecutor: 实际模式下，执行设备连接验证: {DeviceName}", device.Name);

                // 检查当前连接状态
                if (device.IsConnected)
                {
                    // 已连接，验证连接是否有效
                    _logger.LogDebug("ManualCommandExecutor: 设备已连接，验证连接有效性: {DeviceName}", device.Name);

                    var isConnectionValid = await device.CheckConnectionAsync();
                    if (isConnectionValid)
                    {
                        _logger.LogDebug("ManualCommandExecutor: 设备连接有效: {DeviceName}", device.Name);
                        return;
                    }
                    else
                    {
                        _logger.LogWarning("ManualCommandExecutor: 设备连接无效，尝试重新连接: {DeviceName}", device.Name);
                    }
                }

                // 尝试连接设备
                _logger.LogInformation("ManualCommandExecutor: 正在连接设备: {DeviceName} ({DeviceId}) - 模式: {Mode}",
                    device.Name, deviceId, device.IsSimulationMode ? "模拟" : "实际");

                var connectResult = await device.ConnectAsync();
                if (connectResult)
                {
                    _logger.LogInformation("ManualCommandExecutor: 设备连接成功: {DeviceName} ({DeviceId}) - 模式: {Mode}",
                        device.Name, deviceId, device.IsSimulationMode ? "模拟" : "实际");
                }
                else
                {
                    var errorMessage = $"设备连接失败: {device.Name} ({deviceId})";
                    _logger.LogError("ManualCommandExecutor: " + errorMessage);
                    throw new InvalidOperationException(errorMessage);
                }
            }
            catch (Exception ex) when (!(ex is InvalidOperationException))
            {
                var errorMessage = $"设备连接验证异常: {device.Name} ({deviceId}) - {ex.Message}";
                _logger.LogError(ex, "ManualCommandExecutor: 设备连接验证异常: {DeviceName} ({DeviceId})", device.Name, deviceId);
                throw new InvalidOperationException(errorMessage, ex);
            }
        }

        public void Stop(string nodeId)
        {
            try
            {
                if (_ctsMap.TryGetValue(nodeId, out var cts))
                {
                    cts.Cancel();
                    _logger.LogDebug("ManualCommandExecutor: 已请求停止节点 {NodeId} 的执行", nodeId);
                }
                else
                {
                    _logger.LogDebug("ManualCommandExecutor: 节点 {NodeId} 未在执行中", nodeId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ManualCommandExecutor: 停止节点执行失败: {NodeId}", nodeId);
            }
        }

        #region *** 辅助方法（与FlowExecutionEngine保持一致） ***

        /// <summary>
        /// 日志用：把参数集合转成易读文本（与FlowExecutionEngine保持一致）
        /// </summary>
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

        #endregion

        // ... BuildEffectiveParametersCopy 和 IDisposable 实现保持不变
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

        #region IDisposable Implementation

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    // 取消所有正在执行的任务
                    foreach (var kvp in _ctsMap)
                    {
                        try
                        {
                            kvp.Value.Cancel();
                            kvp.Value.Dispose();
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "ManualCommandExecutor: 清理CancellationTokenSource失败");
                        }
                    }
                    _ctsMap.Clear();

                    // 清理保存的设备模拟模式状态
                    _originalDeviceSimulationModes.Clear();

                    _logger.LogDebug("ManualCommandExecutor: 资源清理完成");
                }

                _disposed = true;
            }
        }

        #endregion
    }
}
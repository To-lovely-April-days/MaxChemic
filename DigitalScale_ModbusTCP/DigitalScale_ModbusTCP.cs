using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DevicePlugins.Devices;
using MaxChemical.Core;
using MaxChemical.Logging;

namespace DigitalScale_ModbusTCP
{
    /// <summary>
    /// 电子秤驱动 - 支持S0101/S0111/S0121三个独立设备
    /// 只读型设备，通过ModbusTCP读取PLC数据
    /// </summary>
    [Export(typeof(IDevice))]
    public class DigitalScale_ModbusTCP : Device, IDeviceUIInteraction, IControllableDevice, IFlowLifecycleAware
    {
        private readonly ILogService _logger;

        // 电子秤状态 - 每个实例只管理一个秤
        private double _currentWeight = 0.0;      // 当前重量（原始值）
        private double _totalWeight = 0.0;        // 累计重量（原始值）
        private double _tareWeight = 0.0;         // 去皮值（偏移量）
        private double _initialTotalWeight = 0.0; // 初始累计值（用于重置）
        private string _currentUnit = "g";        // 当前单位
        private bool _isTared = false;            // 是否已去皮
        private bool _isPoweredOn = false;        // 电源状态
        private readonly object _stateLock = new object();

        // 状态跟踪相关字段
        private readonly List<DeviceActiveState> _activeStates = new List<DeviceActiveState>();
        private readonly List<DeviceActiveState> _pausedStates = new List<DeviceActiveState>();
        private readonly object _statesLock = new object();

        // 设备配置
        private string _scaleId = "S0101"; // 默认为S0101，由参数配置
        private readonly string[] _supportedUnits = { "g", "kg", "lb", "oz" };

        // 状态轮询相关
        private CancellationTokenSource _statusPollingCts;
        private Task _statusPollingTask;
        private const int WeightPollingIntervalMs = 500; // 每500ms轮询一次重量

        #region IDeviceUIInteraction 实现

        /// <summary>
        /// UI控件状态变化事件（设备驱动 → UI）
        /// </summary>
        public event EventHandler<DeviceControlStateChangedEventArgs> ControlStateChanged;

        /// <summary>
        /// 执行 UI 命令（UI → 设备驱动）
        /// </summary>
        public async Task<CommandExecutionResult> ExecuteUICommandAsync(
            string commandName,
            Dictionary<string, object> parameters = null)
        {
            try
            {
                InfoLog($"收到UI命令: {commandName}");

                switch (commandName)
                {
                    case "开机":
                        return await PowerOnAsync();

                    case "关机":
                        return await PowerOffAsync();

                    case "去皮":
                        return await TareAsync();

                    case "取消去皮":
                        return await CancelTareAsync();

                    case "切换单位":
                        if (parameters == null || !parameters.ContainsKey("TargetUnit"))
                        {
                            return new CommandExecutionResult
                            {
                                Success = false,
                                Message = "缺少目标单位参数"
                            };
                        }

                        var targetUnit = parameters["TargetUnit"];
                        return await SwitchUnitAsync(targetUnit);

                    default:
                        return new CommandExecutionResult
                        {
                            Success = false,
                            Message = $"未识别的命令: {commandName}"
                        };
                }
            }
            catch (Exception ex)
            {
                ErrorLog($"执行UI命令失败: {commandName}", ex);
                return new CommandExecutionResult
                {
                    Success = false,
                    Message = ex.Message,
                    Exception = ex
                };
            }
        }

        /// <summary>
        /// 获取当前设备状态（设备驱动 → UI）
        /// </summary>
        public override DeviceState GetCurrentState()
        {
            return new DeviceState
            {
                States = new Dictionary<string, object>
                {
                    ["IsPoweredOn"] = _isPoweredOn,
                    ["WeightValue"] = _currentWeight,
                    ["CurrentUnit"] = _currentUnit,
                    ["IsTared"] = _isTared,
                    ["TareValue"] = _tareWeight,
                    ["TotalWeight"] = _totalWeight
                }
            };
        }

        #endregion

        #region UI 命令内部实现

        /// <summary>
        /// 开机（内部实现）
        /// </summary>
        private async Task<CommandExecutionResult> PowerOnAsync()
        {
            try
            {
                // 通知UI：开始加载
                RaiseControlStateChanged("CommandState_开机", CommandExecutionState.Running);

                await Task.Delay(300); // 模拟开机延迟

                lock (_stateLock)
                {
                    _isPoweredOn = true;
                }

                // 通知UI：电源已开启
                RaiseControlStateChanged("IsPoweredOn", true);

                // 通知UI：命令执行成功
                RaiseControlStateChanged("CommandState_开机", CommandExecutionState.Succeeded);

                InfoLog("电子秤已开机");

                return new CommandExecutionResult
                {
                    Success = true,
                    Message = "开机成功",
                    OutputData = new Dictionary<string, object>
                    {
                        ["IsPoweredOn"] = true,
                        ["Timestamp"] = DateTime.Now
                    }
                };
            }
            catch (Exception ex)
            {
                // 通知UI：命令执行异常
                RaiseControlStateChanged("CommandState_开机", CommandExecutionState.Error,
                    new Dictionary<string, object> { ["ErrorMessage"] = ex.Message });

                ErrorLog($"开机失败: {ex.Message}");
                return new CommandExecutionResult
                {
                    Success = false,
                    Message = ex.Message,
                    Exception = ex
                };
            }
        }

        /// <summary>
        /// 关机（内部实现）
        /// </summary>
        private async Task<CommandExecutionResult> PowerOffAsync()
        {
            try
            {
                // 通知UI：开始加载
                RaiseControlStateChanged("CommandState_关机", CommandExecutionState.Running);

                await Task.Delay(300); // 模拟关机延迟

                lock (_stateLock)
                {
                    _isPoweredOn = false;
                }

                // 通知UI：电源已关闭
                RaiseControlStateChanged("IsPoweredOn", false);

                // 通知UI：命令执行成功
                RaiseControlStateChanged("CommandState_关机", CommandExecutionState.Succeeded);

                InfoLog("电子秤已关机");

                return new CommandExecutionResult
                {
                    Success = true,
                    Message = "关机成功",
                    OutputData = new Dictionary<string, object>
                    {
                        ["IsPoweredOn"] = false,
                        ["Timestamp"] = DateTime.Now
                    }
                };
            }
            catch (Exception ex)
            {
                // 通知UI：命令执行异常
                RaiseControlStateChanged("CommandState_关机", CommandExecutionState.Error,
                    new Dictionary<string, object> { ["ErrorMessage"] = ex.Message });

                ErrorLog($"关机失败: {ex.Message}");
                return new CommandExecutionResult
                {
                    Success = false,
                    Message = ex.Message,
                    Exception = ex
                };
            }
        }

        /// <summary>
        /// 去皮（内部实现）
        /// </summary>
        private async Task<CommandExecutionResult> TareAsync()
        {
            try
            {
                // 通知UI：开始加载
                RaiseControlStateChanged("CommandState_去皮", CommandExecutionState.Running);

                double currentWeight = 0;

                if (DeviceConnectionConfig.CommunicationMode == CommunicationMode.ModbusTcp && ConnectionManager != null&&!IsSimulationMode)
                {
                    currentWeight = await ConnectionManager.ReadAsync<double>(ComId, $"_{_scaleId}_g");
                }
                else if (IsSimulationMode)
                {
                    lock (_stateLock)
                    {
                        currentWeight = _currentWeight;
                    }
                }

                lock (_stateLock)
                {
                    _tareWeight = currentWeight;
                    _isTared = true;
                }

                // 通知UI：去皮状态已更新
                RaiseControlStateChanged("IsTared", true);

                // 通知UI：命令执行成功
                RaiseControlStateChanged("CommandState_去皮", CommandExecutionState.Succeeded);

                // 添加去皮状态
                AddActiveState(
                    stateName: $"ScaleTared_{_scaleId}",
                    activateCommand: "去皮",
                    deactivateCommand: "取消去皮",
                    stateData: new ScaleStateData { TareValue = _tareWeight, ScaleId = _scaleId }
                );

                InfoLog($"电子秤已去皮 - 去皮值:{_tareWeight}g");

                return new CommandExecutionResult
                {
                    Success = true,
                    Message = "去皮成功",
                    OutputData = new Dictionary<string, object>
                    {
                        ["TareValue"] = _tareWeight,
                        ["IsTared"] = true,
                        ["Timestamp"] = DateTime.Now
                    }
                };
            }
            catch (Exception ex)
            {
                // 通知UI：命令执行异常
                RaiseControlStateChanged("CommandState_去皮", CommandExecutionState.Error,
                    new Dictionary<string, object> { ["ErrorMessage"] = ex.Message });

                ErrorLog($"去皮失败: {ex.Message}");
                return new CommandExecutionResult
                {
                    Success = false,
                    Message = ex.Message,
                    Exception = ex
                };
            }
        }

        /// <summary>
        /// 取消去皮（内部实现）
        /// </summary>
        private async Task<CommandExecutionResult> CancelTareAsync()
        {
            try
            {
                await Task.Delay(50); // 模拟操作延迟

                lock (_stateLock)
                {
                    _tareWeight = 0;
                    _isTared = false;
                }

                // 通知UI：去皮状态已更新
                RaiseControlStateChanged("IsTared", false);

                // 移除去皮状态
                RemoveActiveState($"ScaleTared_{_scaleId}");

                InfoLog("电子秤已取消去皮");

                return new CommandExecutionResult
                {
                    Success = true,
                    Message = "取消去皮成功",
                    OutputData = new Dictionary<string, object>
                    {
                        ["IsTared"] = false,
                        ["Timestamp"] = DateTime.Now
                    }
                };
            }
            catch (Exception ex)
            {
                ErrorLog($"取消去皮失败: {ex.Message}");
                return new CommandExecutionResult
                {
                    Success = false,
                    Message = ex.Message,
                    Exception = ex
                };
            }
        }

        /// <summary>
        /// 切换单位（内部实现）
        /// </summary>
        private async Task<CommandExecutionResult> SwitchUnitAsync(object targetUnit)
        {
            try
            {
                await Task.Delay(50);

                string newUnit = "g";

                // 处理整数类型的单位枚举
                if (targetUnit is int unitIndex)
                {
                    newUnit = unitIndex switch
                    {
                        0 => "g",   // Gram
                        1 => "kg",  // Kilogram
                        2 => "lb",  // Pound
                        3 => "oz",  // Ounce
                        _ => "g"
                    };
                }
                else if (targetUnit is WeightUnit weightUnit)
                {
                    newUnit = GetUnitString(weightUnit);
                }
                else if (targetUnit is string strUnit)
                {
                    newUnit = strUnit;
                }

                lock (_stateLock)
                {
                    _currentUnit = newUnit;
                }

                // 通知UI：单位已更新
                RaiseControlStateChanged("CurrentUnit", newUnit);

                InfoLog($"单位已切换为: {_currentUnit}");

                return new CommandExecutionResult
                {
                    Success = true,
                    Message = $"单位已切换为 {newUnit}",
                    OutputData = new Dictionary<string, object>
                    {
                        ["CurrentUnit"] = newUnit,
                        ["Timestamp"] = DateTime.Now
                    }
                };
            }
            catch (Exception ex)
            {
                ErrorLog($"切换单位失败: {ex.Message}");
                return new CommandExecutionResult
                {
                    Success = false,
                    Message = ex.Message,
                    Exception = ex
                };
            }
        }

        #endregion

        #region 状态轮询相关

        /// <summary>
        /// 启动状态轮询
        /// </summary>
        private void StartWeightPolling()
        {
            try
            {
                // 如果已经在轮询，先停止
                if (_statusPollingTask != null && !_statusPollingTask.IsCompleted)
                {
                    InfoLog("检测到已有轮询任务，先停止");
                    StopWeightPollingAsync().Wait(TimeSpan.FromSeconds(2));
                }

                // 创建新的取消令牌
                _statusPollingCts = new CancellationTokenSource();

                // 启动后台轮询任务
                _statusPollingTask = Task.Run(async () =>
                {
                    await WeightPollingLoopAsync(_statusPollingCts.Token);
                }, _statusPollingCts.Token);

                InfoLog("重量轮询已启动");
            }
            catch (Exception ex)
            {
                ErrorLog("启动重量轮询失败", ex);
            }
        }

        /// <summary>
        /// 停止状态轮询
        /// </summary>
        private async Task StopWeightPollingAsync()
        {
            try
            {
                if (_statusPollingCts != null)
                {
                    InfoLog("正在停止重量轮询...");
                    _statusPollingCts.Cancel();

                    // 等待轮询任务结束（最多等待2秒）
                    if (_statusPollingTask != null)
                    {
                        await Task.WhenAny(_statusPollingTask, Task.Delay(2000));
                    }

                    _statusPollingCts.Dispose();
                    _statusPollingCts = null;
                    _statusPollingTask = null;

                    InfoLog("重量轮询已停止");
                }
            }
            catch (Exception ex)
            {
                ErrorLog("停止重量轮询失败", ex);
            }
        }

        /// <summary>
        /// 重量轮询循环（后台线程）
        /// </summary>
        private async Task WeightPollingLoopAsync(CancellationToken cancellationToken)
        {
            InfoLog("重量轮询线程已启动");

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        // 如果电子秤未开机，跳过本次轮询
                        bool isPoweredOn = false;
                        lock (_stateLock)
                        {
                            isPoweredOn = _isPoweredOn;
                        }

                        if (!isPoweredOn)
                        {
                            await Task.Delay(WeightPollingIntervalMs, cancellationToken);
                            continue;
                        }

                        // 读取称重值
                        await PollWeightValueAsync(cancellationToken);

                        // 等待下次轮询（带取消检查）
                        await Task.Delay(WeightPollingIntervalMs, cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        InfoLog("重量轮询被取消");
                        break;
                    }
                    catch (Exception ex)
                    {
                        // 单次轮询失败不中断整个循环
                        ErrorLog($"重量轮询异常: {ex.Message}");

                        // 失败后稍微等待，避免疯狂重试
                        try
                        {
                            await Task.Delay(2000, cancellationToken);
                        }
                        catch (OperationCanceledException)
                        {
                            break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                ErrorLog("重量轮询线程异常退出", ex);
            }
            finally
            {
                InfoLog("重量轮询线程已退出");
            }
        }

        /// <summary>
        /// 轮询称重值
        /// </summary>
        private async Task PollWeightValueAsync(CancellationToken cancellationToken)
        {
            try
            {
                if (cancellationToken.IsCancellationRequested) return;

                double currentWeight = 0;
                double totalWeight = 0;

                if (DeviceConnectionConfig.CommunicationMode == CommunicationMode.ModbusTcp && ConnectionManager != null&&!IsSimulationMode && !IsSimulationMode)
                {
                    // 从PLC读取原始数据
                    currentWeight = await ConnectionManager.ReadAsync<double>(ComId, $"_{_scaleId}_g");
                    totalWeight = await ConnectionManager.ReadAsync<double>(ComId, $"_{_scaleId}_Add_g");
                }
                else if (IsSimulationMode)
                {
                    // 模拟模式
                    lock (_stateLock)
                    {
                        currentWeight = _currentWeight + new Random().NextDouble() * 10 - 5;
                        totalWeight = _totalWeight + Math.Abs(new Random().NextDouble() * 0.1);
                    }
                }

                // 检查状态是否变化
                bool weightChanged = false;
                lock (_stateLock)
                {
                    if (Math.Abs(_currentWeight - currentWeight) >= 0.01 ||
                        Math.Abs(_totalWeight - totalWeight) > 0.01)
                    {
                        _currentWeight = currentWeight;
                        _totalWeight = totalWeight;
                        weightChanged = true;
                    }
                }

                // 重量变化时通知UI
                if (weightChanged)
                {
                    // 通知UI：重量已更新
                    RaiseControlStateChanged("WeightValue", currentWeight);
                }
            }
            catch (Exception ex)
            {
                ErrorLog($"轮询称重值失败: {ex.Message}");
            }
        }

        #endregion

        #region 触发状态变化事件的辅助方法

        /// <summary>
        /// 触发控件状态变化事件（设备驱动 → UI）
        /// </summary>
        private void RaiseControlStateChanged(string stateName, object stateValue, Dictionary<string, object> additionalData = null)
        {
            ControlStateChanged?.Invoke(this, new DeviceControlStateChangedEventArgs
            {
                DeviceId = this.DeviceId,
                ComId = this.ComId,
                StateName = stateName,
                StateValue = stateValue,
                Timestamp = DateTime.Now,
                AdditionalData = additionalData ?? new Dictionary<string, object>()
            });

            // DebugLog($"状态变化通知: {stateName} = {stateValue}");
        }

        #endregion

        #region IControllableDevice Implementation

        public List<DeviceActiveState> ActiveStates
        {
            get
            {
                lock (_statesLock)
                {
                    return new List<DeviceActiveState>(_activeStates);
                }
            }
        }

        public event EventHandler<DeviceStateChangedEventArgs> StateChanged;

        public virtual async Task<bool> PauseAllActiveStatesAsync()
        {
            lock (_statesLock)
            {
                if (!_activeStates.Any())
                {
                    InfoLog("电子秤没有活跃状态需要暂停");
                    return true;
                }

                InfoLog($"暂停电子秤的 {_activeStates.Count} 个活跃状态");
            }

            // 电子秤暂停 = 停止读取（实际上读取是被动的，这里只是状态标记）
            var pauseTasks = new List<Task<bool>>();
            DeviceActiveState[] statesToPause;

            lock (_statesLock)
            {
                statesToPause = _activeStates.ToArray();
            }

            foreach (var state in statesToPause)
            {
                pauseTasks.Add(Task.FromResult(true)); // 电子秤无需实际暂停操作
            }

            var results = await Task.WhenAll(pauseTasks);
            bool allSuccess = results.All(r => r);

            lock (_statesLock)
            {
                foreach (var state in statesToPause)
                {
                    _activeStates.Remove(state);
                    state.IsPaused = true;
                    _pausedStates.Add(state);
                }
            }

            InfoLog("电子秤所有活跃状态已暂停");
            return allSuccess;
        }

        public virtual async Task<bool> ResumeAllPausedStatesAsync()
        {
            lock (_statesLock)
            {
                if (!_pausedStates.Any())
                {
                    InfoLog("电子秤没有暂停的状态需要恢复");
                    return true;
                }

                InfoLog($"恢复电子秤的 {_pausedStates.Count} 个暂停的状态");
            }

            DeviceActiveState[] statesToResume;
            lock (_statesLock)
            {
                statesToResume = _pausedStates.ToArray();
            }

            lock (_statesLock)
            {
                foreach (var state in statesToResume)
                {
                    _pausedStates.Remove(state);
                    state.IsPaused = false;
                    _activeStates.Add(state);
                }
            }

            InfoLog("电子秤所有暂停状态已恢复");
            return true;
        }

        public virtual async Task<bool> StopAllActiveStatesAsync()
        {
            lock (_statesLock)
            {
                if (!_activeStates.Any() && !_pausedStates.Any())
                {
                    InfoLog("电子秤没有活跃或暂停的状态需要停止");
                    return true;
                }

                InfoLog($"停止电子秤的 {_activeStates.Count} 个活跃状态和 {_pausedStates.Count} 个暂停状态");
            }

            lock (_statesLock)
            {
                _activeStates.Clear();
                _pausedStates.Clear();
            }

            InfoLog("电子秤所有状态已停止");
            return true;
        }

        #endregion

        #region IFlowLifecycleAware Implementation

        public virtual async Task<bool> OnFlowStartedAsync(FlowStartContext context)
        {
            try
            {
               
                // 1. 清空之前的状态
                lock (_statesLock)
                {
                    _activeStates.Clear();
                    _pausedStates.Clear();
                }

                // 2. 重置电子秤状态
                lock (_stateLock)
                {
                    _tareWeight = 0;
                    _isTared = false;
                }

                // 3. 记录初始累计值（用于计算流程期间的增量）
                try
                {
                    if (DeviceConnectionConfig.CommunicationMode == CommunicationMode.ModbusTcp && ConnectionManager != null&&!IsSimulationMode)
                    {
                        _scaleId = Parameters.GetValue<string>("ScaleId");
                        _initialTotalWeight = await ConnectionManager.ReadAsync<double>(_scaleId, $"_{_scaleId}_Add_g");
                        InfoLog($"记录初始累计重量: {_initialTotalWeight}g");
                    }
                }
                catch (Exception ex)
                {
                    WarnLog($"读取初始累计值失败: {ex.Message}");
                }

                // 4. 启动重量轮询
                StartWeightPolling();

                InfoLog("电子秤已准备好执行流程");
                return true;
            }
            catch (Exception ex)
            {
                ErrorLog("流程开始处理失败", ex);
                return false;
            }
        }

        public virtual async Task<bool> OnFlowCompletedAsync(FlowCompletionContext context)
        {
            try
            {
                InfoLog($"流程完成处理开始: {context.FlowName}, 成功: {context.IsSuccessful}, 持续时间: {context.Duration}");

                // 1. 停止重量轮询
                await StopWeightPollingAsync();

                // 2. 停止所有活跃状态
                await StopAllActiveStatesAsync();

                // 3. 记录最终累计值
                try
                {
                    if (DeviceConnectionConfig.CommunicationMode == CommunicationMode.ModbusTcp && ConnectionManager != null&&!IsSimulationMode)
                    {
                        var finalTotal = await ConnectionManager.ReadAsync<double>(ComId, $"_{_scaleId}_Add_g");
                        var flowIncrement = finalTotal - _initialTotalWeight;

                        InfoLog($"流程完成 - 初始累计:{_initialTotalWeight}g, 最终累计:{finalTotal}g, 流程增量:{flowIncrement}g");
                    }
                }
                catch (Exception ex)
                {
                    WarnLog($"读取最终累计值失败: {ex.Message}");
                }

                // 4. 清理状态
                lock (_stateLock)
                {
                    _tareWeight = 0;
                    _isTared = false;
                    _initialTotalWeight = 0;
                }

                InfoLog($"流程完成处理结束: {context.FlowName}");
                return true;
            }
            catch (Exception ex)
            {
                ErrorLog("流程完成处理失败", ex);
                return false;
            }
        }

        public virtual async Task<bool> OnFlowFailedAsync(FlowFailureContext context)
        {
            try
            {
                ErrorLog($"流程失败处理: {context.FlowName}, 失败节点: {context.FailedNodeName}, 错误: {context.ErrorMessage}");

                // 停止重量轮询
                await StopWeightPollingAsync();

                // 停止所有操作
                await StopAllActiveStatesAsync();

                // 清理状态
                lock (_stateLock)
                {
                    _tareWeight = 0;
                    _isTared = false;
                    _initialTotalWeight = 0;
                }

                InfoLog("流程失败处理完成");
                return true;
            }
            catch (Exception ex)
            {
                ErrorLog("流程失败处理异常", ex);
                return false;
            }
        }

        #endregion

        #region 构造函数

        public DigitalScale_ModbusTCP()
        {
            _logger = LogManager.GetLogger<DigitalScale_ModbusTCP>();

            Name = "电子秤";
            Manufacturer = "电子秤_CY";
            Category = DeviceCategories.ElectronicScales;
            ComId = "S0101"; // 默认为S0101

            ConnectionManager = DeviceConnectionManagerFactory.GetInstance();

            AllowedRegions = new List<RegionType>()
            {
                RegionType.Feed,
                RegionType.PreHeat,
                RegionType.Reaction,
                RegionType.Quench,
                RegionType.PostProcess
            };

            ImageLocation = "CUSTOM_CONTROL:DigitalScaleControl";

            // 添加设备参数 - 选择具体的秤
            Parameters.Variables.Add(new EnumParameter("ScaleId", typeof(ScaleIdentifier), ScaleIdentifier.S0101, "电子秤编号")
            {
                HelpText = "选择要控制的电子秤（S0101/S0111/S0121）"
            });

            InitializeCommands();
        }

        #endregion

        #region 命令初始化

        private void InitializeCommands()
        {
            // 1. 获取称重值 - 核心命令
            Commands.Add(new DeviceCommand
            {
                Name = "获取称重值",
                ImageLocation = "pack://siteoforigin:,,,/Resources/CommadIcon/weight.png",
                HelpText = "获取电子秤当前重量和累计重量",
                InputParameters = new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
                    {
                        new EnumParameter("Unit", typeof(WeightUnit), WeightUnit.Gram, "重量单位"),
                        new BooleanParameter("ApplyTare", true, "是否应用去皮值")
                    }
                },
                OutputParameters = new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
                    {
                        new StringParameter("Status", "PENDING", "执行状态") { IsVisibleInVariableManager = false },
                        new NumberParameter("CurrentWeight", -1000, 10000, 0, "当前重量") { IsVisibleInVariableManager = true },
                        new NumberParameter("TotalWeight", 0, 999999, 0, "累计重量") { IsVisibleInVariableManager = true },
                        new StringParameter("Unit", "g", "重量单位") { IsVisibleInVariableManager = false },
                        new StringParameter("Timestamp", "", "采集时间") { IsVisibleInVariableManager = false },
                    }
                },
                Action = WrapAction("获取称重值", async delegate (DeviceParameters parameters)
                {
                    var unit = parameters.GetValue<WeightUnit>("Unit");
                    bool applyTare = parameters.GetValue<bool>("ApplyTare");

                    double currentWeight = 0;
                    double totalWeight = 0;
                    _scaleId = Parameters.GetValue<string>("ScaleId");
                    try
                    {
                        if (DeviceConnectionConfig.CommunicationMode == CommunicationMode.ModbusTcp && ConnectionManager != null&&!IsSimulationMode)
                        {
                            // 从PLC读取原始数据
                            currentWeight = await ConnectionManager.ReadAsync<double>(_scaleId, $"_{_scaleId}_g");
                            totalWeight = await ConnectionManager.ReadAsync<double>(_scaleId, $"_{_scaleId}_Add_g");

                            InfoLog($"从PLC读取称重值 - 当前:{currentWeight}g, 累计:{totalWeight}g");
                        }
                        else if (IsSimulationMode)
                        {
                            // 模拟模式
                            lock (_stateLock)
                            {
                                currentWeight = _currentWeight + new Random().NextDouble() * 10 - 5;
                                totalWeight = _totalWeight;
                            }
                            InfoLog($"模拟模式称重值 - 当前:{currentWeight}g, 累计:{totalWeight}g");
                        }
                        else
                        {
                            throw new InvalidOperationException("设备未连接且非模拟模式");
                        }

                        // 更新内部状态
                        lock (_stateLock)
                        {
                            _currentWeight = currentWeight;
                            _totalWeight = totalWeight;
                        }
                    }
                    catch (Exception ex)
                    {
                        ErrorLog($"读取称重值失败: {ex.Message}");
                        throw new InvalidOperationException($"获取称重值失败: {ex.Message}", ex);
                    }

                    // 应用去皮
                    double netWeight = applyTare && _isTared ? currentWeight - _tareWeight : currentWeight;

                    // 单位转换
                    double displayCurrent = ConvertWeight(currentWeight, WeightUnit.Gram, unit);
                    double displayTotal = ConvertWeight(totalWeight, WeightUnit.Gram, unit);
                    double displayNet = ConvertWeight(netWeight, WeightUnit.Gram, unit);

                    string unitStr = GetUnitString(unit);

                    return new DeviceParameters
                    {
                        Variables = new ObservableCollection<ParameterBase>
                        {
                            new StringParameter("Status", "OK", "") { IsVisibleInVariableManager = false },
                            new NumberParameter("CurrentWeight", -1000, 10000, Math.Round(displayCurrent, 3), "当前重量") { IsVisibleInVariableManager = true },
                            new NumberParameter("TotalWeight", 0, 999999, Math.Round(displayTotal, 2), "累计重量") { IsVisibleInVariableManager = true },
                            new StringParameter("Unit", unitStr, "") { IsVisibleInVariableManager = false },
                            new StringParameter("Timestamp", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"), "") { IsVisibleInVariableManager = false },
                        }
                    };
                })
            });

            //// 2. 去皮命令
            //Commands.Add(new DeviceCommand
            //{
            //    Name = "去皮",
            //    ImageLocation = "pack://siteoforigin:,,,/Resources/CommadIcon/tare.png",
            //    HelpText = "记录当前重量作为去皮值（软件去皮）",
            //    InputParameters = new DeviceParameters(),
            //    OutputParameters = new DeviceParameters
            //    {
            //        Variables = new ObservableCollection<ParameterBase>
            //        {
            //            new StringParameter("Status", "PENDING", "执行状态") { IsVisibleInVariableManager = false },
            //            new NumberParameter("TareValue", -1000, 10000, 0, "去皮值(g)") { IsVisibleInVariableManager = true },
            //            new BooleanParameter("IsTared", false, "是否已去皮") { IsVisibleInVariableManager = false },
            //            new StringParameter("Timestamp", "", "去皮时间") { IsVisibleInVariableManager = false }
            //        }
            //    },
            //    Action = WrapAction("去皮", async delegate (DeviceParameters parameters)
            //    {
            //        double currentWeight = 0;

            //        try
            //        {
            //            if (DeviceConnectionConfig.CommunicationMode == CommunicationMode.ModbusTcp && ConnectionManager != null&&!IsSimulationMode)
            //            {
            //                currentWeight = await ConnectionManager.ReadAsync<double>(ComId, $"_{_scaleId}_g");
            //            }
            //            else if (IsSimulationMode)
            //            {
            //                lock (_stateLock)
            //                {
            //                    currentWeight = _currentWeight;
            //                }
            //            }

            //            lock (_stateLock)
            //            {
            //                _tareWeight = currentWeight;
            //                _isTared = true;
            //            }

            //            // 通知UI：去皮状态已更新
            //            RaiseControlStateChanged("IsTared", true);

            //            InfoLog($"电子秤已去皮 - 去皮值:{_tareWeight}g");

            //            // 添加去皮状态
            //            AddActiveState(
            //                stateName: $"ScaleTared_{_scaleId}",
            //                activateCommand: "去皮",
            //                deactivateCommand: "取消去皮",
            //                stateData: new ScaleStateData { TareValue = _tareWeight, ScaleId = _scaleId }
            //            );
            //        }
            //        catch (Exception ex)
            //        {
            //            ErrorLog($"去皮失败: {ex.Message}");
            //            throw;
            //        }

            //        return new DeviceParameters
            //        {
            //            Variables = new ObservableCollection<ParameterBase>
            //            {
            //                new StringParameter("Status", "OK", "") { IsVisibleInVariableManager = false },
            //                new NumberParameter("TareValue", -1000, 10000, Math.Round(_tareWeight, 3), "去皮值(g)") { IsVisibleInVariableManager = true },
            //                new BooleanParameter("IsTared", true, "") { IsVisibleInVariableManager = false },
            //                new StringParameter("Timestamp", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"), "") { IsVisibleInVariableManager = false }
            //            }
            //        };
            //    })
            //});

            //// 3. 取消去皮命令
            //Commands.Add(new DeviceCommand
            //{
            //    Name = "取消去皮",
            //    ImageLocation = "pack://siteoforigin:,,,/Resources/CommadIcon/cancel_tare.png",
            //    HelpText = "清除去皮值，恢复原始重量显示",
            //    InputParameters = new DeviceParameters(),
            //    OutputParameters = new DeviceParameters
            //    {
            //        Variables = new ObservableCollection<ParameterBase>
            //        {
            //            new StringParameter("Status", "PENDING", "执行状态") { IsVisibleInVariableManager = false },
            //            new BooleanParameter("IsTared", true, "是否已去皮") { IsVisibleInVariableManager = false },
            //            new StringParameter("Message", "", "状态信息") { IsVisibleInVariableManager = false }
            //        }
            //    },
            //    Action = WrapAction("取消去皮", async delegate (DeviceParameters parameters)
            //    {
            //        await Task.Delay(50); // 模拟操作延迟

            //        lock (_stateLock)
            //        {
            //            _tareWeight = 0;
            //            _isTared = false;
            //        }

            //        // 通知UI：去皮状态已更新
            //        RaiseControlStateChanged("IsTared", false);

            //        // 移除去皮状态
            //        RemoveActiveState($"ScaleTared_{_scaleId}");

            //        InfoLog("电子秤已取消去皮");

            //        return new DeviceParameters
            //        {
            //            Variables = new ObservableCollection<ParameterBase>
            //            {
            //                new StringParameter("Status", "OK", "") { IsVisibleInVariableManager = false },
            //                new BooleanParameter("IsTared", false, "") { IsVisibleInVariableManager = false },
            //                new StringParameter("Message", "去皮已取消", "") { IsVisibleInVariableManager = false }
            //            }
            //        };
            //    })
            //});

            //// 4. 重置累计值命令
            //Commands.Add(new DeviceCommand
            //{
            //    Name = "重置累计值",
            //    ImageLocation = "pack://siteoforigin:,,,/Resources/CommadIcon/reset.png",
            //    HelpText = "记录当前累计值作为新的基准（软件重置）",
            //    InputParameters = new DeviceParameters(),
            //    OutputParameters = new DeviceParameters
            //    {
            //        Variables = new ObservableCollection<ParameterBase>
            //        {
            //            new StringParameter("Status", "PENDING", "执行状态") { IsVisibleInVariableManager = false },
            //            new NumberParameter("InitialTotal", 0, 999999, 0, "初始累计值(g)") { IsVisibleInVariableManager = false },
            //            new StringParameter("Timestamp", "", "重置时间") { IsVisibleInVariableManager = false }
            //        }
            //    },
            //    Action = WrapAction("重置累计值", async delegate (DeviceParameters parameters)
            //    {
            //        double currentTotal = 0;

            //        try
            //        {
            //            if (DeviceConnectionConfig.CommunicationMode == CommunicationMode.ModbusTcp && ConnectionManager != null&&!IsSimulationMode)
            //            {
            //                currentTotal = await ConnectionManager.ReadAsync<double>(ComId, $"_{_scaleId}_Add_g");
            //            }
            //            else if (IsSimulationMode)
            //            {
            //                lock (_stateLock)
            //                {
            //                    currentTotal = _totalWeight;
            //                }
            //            }

            //            lock (_stateLock)
            //            {
            //                _initialTotalWeight = currentTotal;
            //            }

            //            InfoLog($"累计值已重置 - 初始值:{_initialTotalWeight}g");
            //        }
            //        catch (Exception ex)
            //        {
            //            ErrorLog($"重置累计值失败: {ex.Message}");
            //            throw;
            //        }

            //        return new DeviceParameters
            //        {
            //            Variables = new ObservableCollection<ParameterBase>
            //            {
            //                new StringParameter("Status", "OK", "") { IsVisibleInVariableManager = false },
            //                new NumberParameter("InitialTotal", 0, 999999, Math.Round(_initialTotalWeight, 2), "初始累计值(g)") { IsVisibleInVariableManager = false },
            //                new StringParameter("Timestamp", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"), "") { IsVisibleInVariableManager = false }
            //            }
            //        };
            //    })
            //});

            //// 5. 切换单位命令
            //Commands.Add(new DeviceCommand
            //{
            //    Name = "切换单位",
            //    ImageLocation = "pack://siteoforigin:,,,/Resources/CommadIcon/unit.png",
            //    HelpText = "切换重量显示单位",
            //    InputParameters = new DeviceParameters
            //    {
            //        Variables = new ObservableCollection<ParameterBase>
            //        {
            //            new EnumParameter("TargetUnit", typeof(WeightUnit), WeightUnit.Gram, "目标单位")
            //        }
            //    },
            //    OutputParameters = new DeviceParameters
            //    {
            //        Variables = new ObservableCollection<ParameterBase>
            //        {
            //            new StringParameter("Status", "PENDING", "执行状态") { IsVisibleInVariableManager = false },
            //            new StringParameter("CurrentUnit", "g", "当前单位") { IsVisibleInVariableManager = false },
            //            new StringParameter("Message", "", "状态信息") { IsVisibleInVariableManager = false }
            //        }
            //    },
            //    Action = WrapAction("切换单位", async delegate (DeviceParameters parameters)
            //    {
            //        await Task.Delay(50);

            //        var targetUnit = parameters.GetValue<WeightUnit>("TargetUnit");

            //        lock (_stateLock)
            //        {
            //            _currentUnit = GetUnitString(targetUnit);
            //        }

            //        // 通知UI：单位已更新
            //        RaiseControlStateChanged("CurrentUnit", _currentUnit);

            //        InfoLog($"单位已切换为: {_currentUnit}");

            //        return new DeviceParameters
            //        {
            //            Variables = new ObservableCollection<ParameterBase>
            //            {
            //                new StringParameter("Status", "OK", "") { IsVisibleInVariableManager = false },
            //                new StringParameter("CurrentUnit", _currentUnit, "") { IsVisibleInVariableManager = false },
            //                new StringParameter("Message", $"单位已切换为 {_currentUnit}", "") { IsVisibleInVariableManager = false }
            //            }
            //        };
            //    })
            //});
        }

        #endregion

        #region 单位转换

        private double ConvertWeight(double valueInGrams, WeightUnit fromUnit, WeightUnit toUnit)
        {
            if (fromUnit == toUnit) return valueInGrams;

            // 先转换为克
            double grams = fromUnit switch
            {
                WeightUnit.Gram => valueInGrams,
                WeightUnit.Kilogram => valueInGrams * 1000,
                WeightUnit.Pound => valueInGrams * 453.592,
                WeightUnit.Ounce => valueInGrams * 28.3495,
                _ => valueInGrams
            };

            // 再转换为目标单位
            return toUnit switch
            {
                WeightUnit.Gram => grams,
                WeightUnit.Kilogram => grams / 1000,
                WeightUnit.Pound => grams / 453.592,
                WeightUnit.Ounce => grams / 28.3495,
                _ => grams
            };
        }

        private string GetUnitString(WeightUnit unit)
        {
            return unit switch
            {
                WeightUnit.Gram => "g",
                WeightUnit.Kilogram => "kg",
                WeightUnit.Pound => "lb",
                WeightUnit.Ounce => "oz",
                _ => "g"
            };
        }

        #endregion

        #region 状态管理

        protected void AddActiveState(string stateName, string activateCommand, string deactivateCommand,
            string pauseCommand = null, string resumeCommand = null, object stateData = null)
        {
            lock (_statesLock)
            {
                var existing = _activeStates.FirstOrDefault(s => s.StateName == stateName);
                if (existing != null)
                {
                    WarnLog($"电子秤状态已存在: {stateName}");
                    existing.ActivatedTime = DateTime.Now;
                    existing.StateData = stateData;
                    return;
                }

                var newState = new DeviceActiveState
                {
                    StateName = stateName,
                    ActivateCommand = activateCommand,
                    DeactivateCommand = deactivateCommand,
                    PauseCommand = pauseCommand,
                    ResumeCommand = resumeCommand,
                    ActivatedTime = DateTime.Now,
                    IsPaused = false,
                    StateData = stateData
                };

                _activeStates.Add(newState);
                InfoLog($"电子秤添加活跃状态: {stateName}");
            }

            StateChanged?.Invoke(this, new DeviceStateChangedEventArgs
            {
                StateName = stateName,
                IsActive = true,
                Timestamp = DateTime.Now
            });
        }

        protected void RemoveActiveState(string stateName)
        {
            lock (_statesLock)
            {
                var state = _activeStates.FirstOrDefault(s => s.StateName == stateName);
                if (state != null)
                {
                    _activeStates.Remove(state);
                    InfoLog($"电子秤移除活跃状态: {stateName}");
                }

                var pausedState = _pausedStates.FirstOrDefault(s => s.StateName == stateName);
                if (pausedState != null)
                {
                    _pausedStates.Remove(pausedState);
                }
            }

            StateChanged?.Invoke(this, new DeviceStateChangedEventArgs
            {
                StateName = stateName,
                IsActive = false,
                Timestamp = DateTime.Now
            });
        }

        #endregion

        #region 设备生命周期

        public override void Initialize()
        {
            InfoLog("开始初始化电子秤设备");

            try
            {
                base.Initialize();

                // 从参数中获取秤的ID
                _scaleId = Parameters.GetValue<ScaleIdentifier>("ScaleId").ToString();
                ComId = _scaleId;

                lock (_stateLock)
                {
                    _currentWeight = 0;
                    _totalWeight = 0;
                    _tareWeight = 0;
                    _initialTotalWeight = 0;
                    _isTared = false;
                    _isPoweredOn = false;
                }

                lock (_statesLock)
                {
                    _activeStates.Clear();
                    _pausedStates.Clear();
                }

                InfoLog($"电子秤设备初始化完成 - 秤ID:{_scaleId}");
            }
            catch (Exception ex)
            {
                ErrorLog("设备初始化失败", ex);
                throw;
            }
        }

        public override void Shutdown()
        {
            InfoLog("开始关闭电子秤设备");

            try
            {
                // 先停止重量轮询
                var stopPollingTask = StopWeightPollingAsync();
                stopPollingTask.Wait(TimeSpan.FromSeconds(3));

                var stopTask = StopAllActiveStatesAsync();
                stopTask.Wait(TimeSpan.FromSeconds(5));

                base.Shutdown();
                InfoLog("电子秤设备关闭完成");
            }
            catch (Exception ex)
            {
                ErrorLog("设备关闭失败", ex);
                throw;
            }
        }

        protected override async Task<bool> OnConnectAsync()
        {
            try
            {
                if (IsSimulationMode)
                {
                    InfoLog("以仿真模式连接电子秤");
                    await Task.Delay(500);
                    return true;
                }

                if (DeviceConnectionConfig.CommunicationMode == CommunicationMode.ModbusTcp && ConnectionManager != null&&!IsSimulationMode)
                {
                    InfoLog($"连接到PLC电子秤: {_scaleId}");
                    // PLC模式下，连接由ConnectionManager管理
                    return await ConnectionManager.ConnectAsync(ComId);
                }

                ErrorLog("电子秤需要PLC模式或仿真模式");
                return false;
            }
            catch (Exception ex)
            {
                ErrorLog($"连接失败: {ex.Message}");
                return false;
            }
        }

        protected override async Task<bool> OnDisconnectAsync()
        {
            try
            {
                await StopAllActiveStatesAsync();

                if (ConnectionManager != null)
                {
                    await ConnectionManager.DisconnectAsync(ComId);
                }

                InfoLog("电子秤连接已断开");
                return true;
            }
            catch (Exception ex)
            {
                ErrorLog($"断开连接失败: {ex.Message}");
                return false;
            }
        }

        protected override async Task<bool> OnCheckConnectionAsync()
        {
            try
            {
                if (IsSimulationMode)
                {
                    await Task.Delay(50);
                    return true;
                }

                if (ConnectionManager != null)
                {
                    return await ConnectionManager.CheckConnectionAsync(ComId);
                }

                return false;
            }
            catch
            {
                return false;
            }
        }

        #endregion

        #region 日志封装

        private Func<DeviceParameters, DeviceParameters> WrapAction(string commandName, Func<DeviceParameters, Task<DeviceParameters>> innerAsync)
        {
            return (DeviceParameters parameters) =>
            {
                var paramText = FormatParameters(parameters);
                _logger.LogInformation($"[设备命令-开始] Device={Name} Command={commandName} Params={paramText}");

                var sw = Stopwatch.StartNew();
                try
                {
                    var output = innerAsync(parameters).GetAwaiter().GetResult();
                    sw.Stop();

                    var statusParam = output?.Variables?.OfType<StringParameter>()
                        ?.FirstOrDefault(p => p.Name == "Status");

                    if (statusParam?.Value?.ToString() == "ERROR")
                    {
                        var messageParam = output?.Variables?.OfType<StringParameter>()
                            ?.FirstOrDefault(p => p.Name == "Message");
                        var errorMessage = messageParam?.Value?.ToString() ?? "设备命令执行失败";

                        _logger.LogError($"[设备命令-错误] Device={Name} Command={commandName} DurationMs={sw.ElapsedMilliseconds} Error={errorMessage}");
                        throw new DeviceCommandExecutionException(Name, commandName, errorMessage);
                    }

                    var outputText = FormatParameters(output);
                    _logger.LogInformation($"[设备命令-完成] Device={Name} Command={commandName} DurationMs={sw.ElapsedMilliseconds} Output={outputText}");
                    return output;
                }
                catch (DeviceCommandExecutionException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    _logger.LogError(ex, $"[设备命令-异常] Device={Name} Command={commandName} DurationMs={sw.ElapsedMilliseconds} Params={paramText}");
                    throw new DeviceCommandExecutionException(Name, commandName, ex.Message, ex);
                }
            };
        }

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

        protected void InfoLog(string message) => _logger.LogInformation($"[{Name}({_scaleId})] {message}");
        protected void WarnLog(string message) => _logger.LogWarning($"[{Name}({_scaleId})] {message}");
        protected void ErrorLog(string message, Exception ex = null) => _logger.LogError(ex, $"[{Name}({_scaleId})] {message}");
        protected void DebugLog(string message) => _logger.LogDebug($"[{Name}({_scaleId})] {message}");

        #endregion

        #region 枚举和数据类

        public enum ScaleIdentifier
        {
            S0101,
            S0111,
            S0121
        }

        public enum WeightUnit
        {
            Gram,
            Kilogram,
            Pound,
            Ounce
        }

        private class ScaleStateData
        {
            public string ScaleId { get; set; }
            public double TareValue { get; set; }
        }

        #endregion
    }
}
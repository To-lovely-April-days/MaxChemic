using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using DevicePlugins.Devices;
using MaxChemical.Logging;
using MaxChemical.Modules.Designer.Event;
using MaxChemical.Modules.Designer.Service.Execution;
using MaxChemical.Modules.Designer.ViewModels;
using Prism.Events;
// 裸类型名 Device 会与命名空间 MaxChemical.Modules.Device 冲突(CS0118),用别名
using DeviceBase = DevicePlugins.Devices.Device;

namespace MaxChemical.Modules.Designer.Services
{
    /// <summary>
    /// 驱动级自动采集的汇聚服务（单例，DesignerModule 启动时创建）。
    ///
    /// 数据链路：
    ///   Device 基类采集循环 → DeviceSampleHub（DevicePlugins 静态事件）
    ///     → 本服务 → ① 以统一键「自动采集」转发为 MonitoringCardUpdateEvent
    ///                  （样本为设备声明参数的聚合帧），监控图表 / 监控卡片 /
    ///                  云推送三条既有消费链路零改动直接复用；
    ///               ② 流程运行期间对每个参数做节流入库：值显著变化（相对
    ///                  死区 1%）且距上次写入不小于 2 秒时立即写；否则每 5 秒
    ///                  兜底补一笔。写入 experiment_device_data，NodeId 为空、
    ///                  NodeName 固定为「自动采集」以便与流程循环采集的数据区分。
    ///                  （模拟量读数带噪声帧帧在变，纯「变了就写」会退化成
    ///                  采样频率写库，死区 + 最小间隔就是为压住这类噪声写入。）
    ///
    /// 门控约定：设备连上就采集进内存（图表可看），只有流程运行且存在
    /// 当前实验号时才落库。
    /// </summary>
    public class AutoCollectService : IDisposable
    {
        private const string AutoCollectNodeName = "自动采集";
        private const int MaxPersistIntervalMs = 5000;
        private const int MinPersistIntervalMs = 2000;
        private const double RelativeDeadband = 0.01;

        private readonly IEventAggregator _eventAggregator;
        private readonly IExperimentDataAdapter _experimentDataAdapter;
        private readonly ILogService _logger;

        private volatile bool _isFlowRunning;
        private bool _disposed;

        // key = deviceId|commandName|parameterName → 上次落库的值与时刻
        private readonly ConcurrentDictionary<string, PersistState> _persistStates
            = new ConcurrentDictionary<string, PersistState>();

        private sealed class PersistState
        {
            public double LastValue;
            public DateTime LastWriteTime;
        }

        public AutoCollectService(IEventAggregator eventAggregator, IExperimentDataAdapter experimentDataAdapter)
        {
            _eventAggregator = eventAggregator ?? throw new ArgumentNullException(nameof(eventAggregator));
            _experimentDataAdapter = experimentDataAdapter ?? throw new ArgumentNullException(nameof(experimentDataAdapter));
            _logger = new LogService().ForContext<AutoCollectService>();

            DeviceSampleHub.SampleCollected += OnSampleCollected;
            _eventAggregator.GetEvent<FlowExecutionStateChangedEvent>().Subscribe(OnFlowStateChanged, ThreadOption.PublisherThread, true);
            // 画布设备增删时集中盖章,手动「连接」路径也不依赖画布创建代码里的盖章
            _eventAggregator.GetEvent<CanvasDevicesChangedEvent>().Subscribe(OnCanvasDevicesChanged, ThreadOption.UIThread, true);
            // 跟随模拟模式开关:画布设备实例的 IsSimulationMode 此前只在引擎/手动执行
            // 路径内被临时应用,自动采集循环直接调命令读到的是 false → 走真实分支静默失败。
            // 这里把模式实打实写到画布设备实例上。
            _eventAggregator.GetEvent<DeviceSimulationModeChangedEvent>().Subscribe(OnSimulationModeChanged, ThreadOption.UIThread, true);

            _logger.LogInformation("驱动自动采集汇聚服务已启动");
        }

        private bool _isSimulationMode;

        private void RefreshSimulationMode()
        {
            try
            {
                _eventAggregator.GetEvent<GetSimulationModeRequestEvent>().Publish(mode => _isSimulationMode = mode);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "查询模拟模式失败");
            }
        }

        /// <summary>等值守卫:IsSimulationMode 的 setter 无条件触发已连接设备重连,避免重复设置。</summary>
        private void ApplySimulationMode(DeviceBase device)
        {
            if (device.IsSimulationMode != _isSimulationMode)
            {
                device.IsSimulationMode = _isSimulationMode;
            }
        }

        private void OnSimulationModeChanged(DeviceSimulationModeEventArgs args)
        {
            if (args == null) return;
            _isSimulationMode = args.IsSimulationMode;

            List<CanvasDeviceViewModel>? canvasDevices = null;
            _eventAggregator.GetEvent<RequestCanvasDevicesEvent>().Publish(list => canvasDevices = list);
            if (canvasDevices == null) return;
            foreach (var cd in canvasDevices)
            {
                if (cd?.Device is DeviceBase device && device.CollectableParameters.Count > 0)
                {
                    ApplySimulationMode(device);
                }
            }
        }

        private void OnCanvasDevicesChanged(List<CanvasDeviceViewModel> devices)
        {
            if (devices == null) return;
            RefreshSimulationMode();
            foreach (var cd in devices)
            {
                if (cd?.Device is DeviceBase device && device.CollectableParameters.Count > 0)
                {
                    device.AutoCollectAllowed = true;
                    ApplySimulationMode(device);
                }
            }
        }

        private void OnFlowStateChanged(FlowExecutionStateChangedEventArgs args)
        {
            if (args == null) return;

            switch (args.NewState)
            {
                case FlowExecutionState.Running:
                    _isFlowRunning = true;
                    _persistStates.Clear();
                    _logger.LogInformation("流程开始运行,自动采集进入落库模式");
                    StartFlowCollectHold();
                    break;

                case FlowExecutionState.Completed:
                case FlowExecutionState.Stopped:
                case FlowExecutionState.Error:
                case FlowExecutionState.Idle:
                    if (_isFlowRunning)
                    {
                        _isFlowRunning = false;
                        _persistStates.Clear();
                        _logger.LogInformation("流程停止,自动采集回到仅内存模式");
                        StopFlowCollectHold();
                    }
                    break;
            }
        }

        // ── 流程强制采集保持 ──
        // 双保险:①集中给画布设备盖章 AutoCollectAllowed(不依赖画布创建路径的盖章代码);
        // ②流程运行期间强制启动采集循环(引擎是否连接设备都不影响),流程结束解除。

        private readonly List<DeviceBase> _flowHeldDevices = new List<DeviceBase>();

        private void StartFlowCollectHold()
        {
            RunOnUiThread(() =>
            {
                try
                {
                    RefreshSimulationMode();

                    List<CanvasDeviceViewModel>? canvasDevices = null;
                    _eventAggregator.GetEvent<RequestCanvasDevicesEvent>().Publish(list => canvasDevices = list);
                    if (canvasDevices == null) return;

                    lock (_flowHeldDevices)
                    {
                        foreach (var cd in canvasDevices)
                        {
                            if (cd?.Device is not DeviceBase device) continue;
                            if (device.CollectableParameters.Count == 0) continue;

                            device.AutoCollectAllowed = true;
                            ApplySimulationMode(device);
                            device.NotifyFlowCollectStart();
                            if (!_flowHeldDevices.Contains(device)) _flowHeldDevices.Add(device);
                        }
                        _logger.LogInformation("流程强制采集已启动: {Count} 台设备(模拟模式: {Sim})",
                            _flowHeldDevices.Count, _isSimulationMode);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "流程强制采集启动失败");
                }
            });
        }

        private void StopFlowCollectHold()
        {
            RunOnUiThread(() =>
            {
                lock (_flowHeldDevices)
                {
                    foreach (var device in _flowHeldDevices)
                    {
                        try { device.NotifyFlowCollectStop(); } catch { }
                    }
                    _flowHeldDevices.Clear();
                }
            });
        }

        /// <summary>画布设备列表的应答方在 UI 线程,引擎状态事件可能来自后台线程,统一切换。</summary>
        private static void RunOnUiThread(Action action)
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess()) action();
            else dispatcher.BeginInvoke(action);
        }

        private void OnSampleCollected(object? sender, DeviceSampleEventArgs args)
        {
            if (_disposed || args?.Output?.Variables == null || args.Output.Variables.Count == 0) return;

            try
            {
                // ① 转发为监控卡片更新事件:图表存储、卡片显示、云推送三条链路复用
                _eventAggregator.GetEvent<MonitoringCardUpdateEvent>().Publish(new MonitoringCardUpdateEventArgs
                {
                    DeviceId = args.DeviceId,
                    CommandName = args.CommandName,
                    OutputParameters = args.Output,
                    UpdateTime = args.Timestamp
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "转发自动采集样本失败: {DeviceId}/{Command}", args.DeviceId, args.CommandName);
            }

            // ② 流程运行期间节流落库
            if (!_isFlowRunning) return;

            try
            {
                var experimentId = _experimentDataAdapter.GetCurrentExperimentId();
                if (string.IsNullOrEmpty(experimentId)) return;

                var toPersist = SelectParametersToPersist(args, args.Output);
                if (toPersist.Count == 0) return;

                var output = new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>(toPersist)
                };

                // 适配器内部已捕获异常,fire-and-forget 不阻塞采集线程
                _ = _experimentDataAdapter.RecordDeviceDataAsync(
                    experimentId,
                    args.DeviceId,
                    args.DeviceName,
                    args.CommandName,
                    null,
                    output,
                    null,
                    AutoCollectNodeName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "自动采集落库失败: {DeviceId}/{Command}", args.DeviceId, args.CommandName);
            }
        }

        /// <summary>
        /// 选出本帧需要落库的数值型参数。写入条件(按参数独立判定):
        ///   首帧立即写;之后「显著变化(超过 1% 相对死区)且距上次写入不小于
        ///   2 秒」立即写;否则每 5 秒兜底补一笔。
        /// </summary>
        private List<ParameterBase> SelectParametersToPersist(DeviceSampleEventArgs args, DeviceParameters output)
        {
            var selected = new List<ParameterBase>();
            var now = args.Timestamp;

            foreach (var param in output.Variables)
            {
                if (param == null || string.IsNullOrEmpty(param.Name)) continue;
                if (!TryConvertToDouble(param.Value, out var value)) continue;

                var key = $"{args.DeviceId}|{args.CommandName}|{param.Name}";
                var state = _persistStates.GetOrAdd(key, _ => new PersistState
                {
                    LastValue = double.NaN,
                    LastWriteTime = DateTime.MinValue
                });

                var sinceLastWriteMs = (now - state.LastWriteTime).TotalMilliseconds;
                bool firstSample = double.IsNaN(state.LastValue);
                bool intervalDue = sinceLastWriteMs >= MaxPersistIntervalMs;
                bool significantChange =
                    Math.Abs(value - state.LastValue) > Math.Max(Math.Abs(state.LastValue) * RelativeDeadband, 1e-9);

                if (firstSample || intervalDue || (significantChange && sinceLastWriteMs >= MinPersistIntervalMs))
                {
                    selected.Add(param);
                    state.LastValue = value;
                    state.LastWriteTime = now;
                }
            }

            return selected;
        }

        private static bool TryConvertToDouble(object? value, out double result)
        {
            result = 0;
            switch (value)
            {
                case null:
                    return false;
                case double d:
                    result = d;
                    return double.IsFinite(d);
                case float f:
                    result = f;
                    return float.IsFinite(f);
                case int i:
                    result = i;
                    return true;
                case long l:
                    result = l;
                    return true;
                case decimal m:
                    result = (double)m;
                    return true;
                default:
                    return double.TryParse(value.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out result)
                        && double.IsFinite(result);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            DeviceSampleHub.SampleCollected -= OnSampleCollected;
            try
            {
                _eventAggregator?.GetEvent<FlowExecutionStateChangedEvent>()?.Unsubscribe(OnFlowStateChanged);
            }
            catch
            {
                // 关闭阶段容器可能已释放,忽略
            }
            _logger.LogInformation("驱动自动采集汇聚服务已停止");
        }
    }
}

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using MaxChemical.Modules.Designer.Models;
using MaxChemical.Modules.Designer.ViewModels;
using MaxChemical.Modules.Designer.Event;
using MaxChemical.Infrastructure.Events;
using MaxChemical.Logging;
using Prism.Events;
using MaxChemical.Modules.Designer.Service.Execution;
using DevicePlugins.Devices;
// 裸类型名 Device 会与命名空间 MaxChemical.Modules.Device 冲突(CS0118),用别名
using DeviceBase = DevicePlugins.Devices.Device;

namespace MaxChemical.Modules.Designer.Services
{
    public class ParameterMonitoringService : IParameterMonitoringService
    {
        private readonly IEventAggregator _eventAggregator;
        private readonly ILogService _logger;
        private readonly ConcurrentDictionary<string, List<ParameterDataPoint>> _parameterData;
        private readonly List<MonitoredParameter> _monitoredParameters;
        private readonly object _lockObject = new object();
        private readonly int _maxDataPoints = 1000;
        // *** 全局数据缓存,存储所有接收到的参数数据 ***
        private readonly ConcurrentDictionary<string, List<ParameterDataPoint>> _globalDataCache;
        private readonly int _maxGlobalCachePoints = 2000;

        // *** 流程状态跟踪 ***
        private bool _isFlowRunning = false;
        private DateTime _flowStartTime = DateTime.MinValue;
        public event EventHandler<ParameterDataUpdatedEventArgs> ParameterDataUpdated;

        public ParameterMonitoringService(IEventAggregator eventAggregator)
        {
            _eventAggregator = eventAggregator ?? throw new ArgumentNullException(nameof(eventAggregator));
            _logger = new LogService().ForContext<ParameterMonitoringService>();
            _parameterData = new ConcurrentDictionary<string, List<ParameterDataPoint>>();
            _monitoredParameters = new List<MonitoredParameter>();
            _globalDataCache = new ConcurrentDictionary<string, List<ParameterDataPoint>>();

            _eventAggregator.GetEvent<MonitoringCardUpdateEvent>().Subscribe(OnMonitoringDataReceived);
            _eventAggregator.GetEvent<FlowExecutionStateChangedEvent>().Subscribe(OnFlowStateChanged);

            _logger.LogDebug("参数监控服务已初始化");
        }

        private void OnFlowStateChanged(FlowExecutionStateChangedEventArgs args)
        {
            try
            {
                switch (args.NewState)
                {
                    case FlowExecutionState.Running:
                        _isFlowRunning = true;
                        _flowStartTime = DateTime.Now;
                        _logger.LogInformation("流程开始运行,启动全局数据缓存");
                        break;

                    case FlowExecutionState.Completed:
                    case FlowExecutionState.Stopped:
                    case FlowExecutionState.Error:
                    case FlowExecutionState.Idle:
                        _isFlowRunning = false;
                        _logger.LogInformation("流程停止运行");
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "处理流程状态变更失败");
            }
        }

        public bool IsFlowCurrentlyRunning()
        {
            if (_isFlowRunning) return true;

            try
            {
                var cutoffTime = DateTime.Now.AddMinutes(-2);
                foreach (var kvp in _globalDataCache)
                {
                    var dataList = kvp.Value;
                    lock (dataList)
                    {
                        if (dataList.Any(d => d.Timestamp > cutoffTime))
                        {
                            _logger.LogInformation("通过全局缓存检测到流程正在运行");
                            return true;
                        }
                    }
                }
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "检查流程运行状态失败");
                return false;
            }
        }

        /// <summary>
        /// ★ 新增 unit 参数(可选,默认 "")。即使旧调用方不传也能编译通过。
        /// </summary>
        public void AddMonitoredParameter(string deviceId, string deviceName, string commandName,
            string parameterName, string displayName = null, string unit = "")
        {
            lock (_lockObject)
            {
                var key = GenerateParameterKey(deviceId, commandName, parameterName);

                if (!_monitoredParameters.Any(p => p.Key == key))
                {
                    var parameter = new MonitoredParameter
                    {
                        Key = key,
                        DeviceId = deviceId,
                        DeviceName = deviceName,
                        CommandName = commandName,
                        ParameterName = parameterName,
                        DisplayName = displayName ?? $"{deviceName}.{commandName}.{parameterName}",
                        Unit = unit ?? "",                                  // ★ 保存 unit
                        IsEnabled = true,
                        Color = GenerateColor(_monitoredParameters.Count),
                        MinValue = double.MaxValue,
                        MaxValue = double.MinValue,
                        CurrentValue = null,
                        LastUpdateTime = null
                    };

                    _monitoredParameters.Add(parameter);

                    var parameterDataList = new List<ParameterDataPoint>();
                    _parameterData[key] = parameterDataList;

                    if (_isFlowRunning || IsFlowCurrentlyRunning())
                    {
                        BackfillDataFromGlobalCache(key, parameterDataList);
                        EnsureRealTimeDataFlow(key);
                    }

                    _logger.LogInformation("添加监控参数: {DisplayName}, 单位: {Unit}, 颜色: {Color}, 回填数据: {Count} 条",
                        parameter.DisplayName, parameter.Unit, parameter.Color, parameterDataList.Count);
                }
                else
                {
                    // ★ 如果参数已存在但传入了非空 unit,补充 Unit(允许后续调用补全单位)
                    var existing = _monitoredParameters.First(p => p.Key == key);
                    if (string.IsNullOrEmpty(existing.Unit) && !string.IsNullOrEmpty(unit))
                    {
                        existing.Unit = unit;
                        _logger.LogInformation("参数已存在,补充单位: {Key} → Unit={Unit}", key, unit);
                    }
                    else
                    {
                        _logger.LogWarning("参数已存在于监控列表: {Key}", key);
                    }
                }
            }
        }

        private void EnsureRealTimeDataFlow(string parameterKey)
        {
            try
            {
                if (_globalDataCache.TryGetValue(parameterKey, out var globalData))
                {
                    lock (globalData)
                    {
                        var recentData = globalData
                            .Where(d => d.Timestamp > DateTime.Now.AddMinutes(-1))
                            .OrderByDescending(d => d.Timestamp)
                            .Take(5)
                            .ToList();

                        if (recentData.Any())
                        {
                            var latestTime = recentData.First().Timestamp;
                            _logger.LogInformation("参数 {Key} 的最新数据时间: {LatestTime}",
                                parameterKey, latestTime.ToString("HH:mm:ss.fff"));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "确保实时数据流失败: {Key}", parameterKey);
            }
        }

        private void BackfillDataFromGlobalCache(string parameterKey, List<ParameterDataPoint> parameterDataList)
        {
            try
            {
                if (_globalDataCache.TryGetValue(parameterKey, out var globalData))
                {
                    lock (globalData)
                    {
                        var relevantData = globalData
                            .Where(d => d.Timestamp >= _flowStartTime)
                            .OrderBy(d => d.Timestamp)
                            .ToList();

                        if (relevantData.Any())
                        {
                            parameterDataList.AddRange(relevantData);

                            var parameter = _monitoredParameters.FirstOrDefault(p => p.Key == parameterKey);
                            if (parameter != null)
                            {
                                foreach (var data in relevantData)
                                {
                                    UpdateParameterStatistics(parameter, data.Value);
                                }
                            }

                            _logger.LogInformation("从全局缓存回填参数数据: {Key}, 数据点: {Count}",
                                parameterKey, relevantData.Count);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "从全局缓存回填数据失败: {Key}", parameterKey);
            }
        }

        public void RemoveMonitoredParameter(string deviceId, string commandName, string parameterName)
        {
            lock (_lockObject)
            {
                var key = GenerateParameterKey(deviceId, commandName, parameterName);
                var parameter = _monitoredParameters.FirstOrDefault(p => p.Key == key);

                if (parameter != null)
                {
                    _monitoredParameters.Remove(parameter);
                    _parameterData.TryRemove(key, out _);
                    _logger.LogInformation("移除监控参数: {DisplayName}", parameter.DisplayName);
                }
                else
                {
                    _logger.LogWarning("未找到要移除的监控参数: {Key}", key);
                }
            }
        }

        public List<MonitoredParameter> GetMonitoredParameters()
        {
            lock (_lockObject)
            {
                return _monitoredParameters.ToList();
            }
        }

        public List<ParameterDataPoint> GetParameterData(string deviceId, string commandName, string parameterName)
        {
            var key = GenerateParameterKey(deviceId, commandName, parameterName);

            if (_parameterData.TryGetValue(key, out var data))
            {
                lock (data)
                {
                    return data.ToList();
                }
            }
            return new List<ParameterDataPoint>();
        }

        public void ClearAllData()
        {
            lock (_lockObject)
            {
                foreach (var key in _parameterData.Keys.ToList())
                {
                    if (_parameterData.TryGetValue(key, out var dataList))
                    {
                        lock (dataList) { dataList.Clear(); }
                    }
                }
                foreach (var key in _globalDataCache.Keys.ToList())
                {
                    if (_globalDataCache.TryGetValue(key, out var dataList))
                    {
                        lock (dataList) { dataList.Clear(); }
                    }
                }
                foreach (var parameter in _monitoredParameters)
                {
                    parameter.MinValue = double.MaxValue;
                    parameter.MaxValue = double.MinValue;
                    parameter.CurrentValue = null;
                    parameter.LastUpdateTime = null;
                }
                _logger.LogInformation("清空所有监控数据");
            }
        }

        public void ClearParameterData(string deviceId, string commandName, string parameterName)
        {
            var key = GenerateParameterKey(deviceId, commandName, parameterName);

            if (_parameterData.TryGetValue(key, out var data))
            {
                lock (data) { data.Clear(); }

                var parameter = _monitoredParameters.FirstOrDefault(p => p.Key == key);
                if (parameter != null)
                {
                    parameter.MinValue = double.MaxValue;
                    parameter.MaxValue = double.MinValue;
                    parameter.CurrentValue = null;
                    parameter.LastUpdateTime = null;
                }
                _logger.LogDebug("清空参数数据: {Key}", key);
            }
        }

        public void SetParameterEnabled(string deviceId, string commandName, string parameterName, bool enabled)
        {
            lock (_lockObject)
            {
                var key = GenerateParameterKey(deviceId, commandName, parameterName);
                var parameter = _monitoredParameters.FirstOrDefault(p => p.Key == key);
                if (parameter != null)
                {
                    parameter.IsEnabled = enabled;
                    _logger.LogDebug("参数启用状态更新: {Key} = {Enabled}", key, enabled);
                }
                else
                {
                    _logger.LogWarning("未找到参数: {Key}", key);
                }
            }
        }

        public void UpdateParameterColor(string deviceId, string commandName, string parameterName, string color)
        {
            lock (_lockObject)
            {
                var key = GenerateParameterKey(deviceId, commandName, parameterName);
                var parameter = _monitoredParameters.FirstOrDefault(p => p.Key == key);
                if (parameter != null)
                {
                    parameter.Color = color;
                    _logger.LogDebug("更新参数颜色: {Key} = {Color}", key, color);
                }
            }
        }

        public List<AvailableParameter> GetAvailableParameters()
        {
            var availableParams = new List<AvailableParameter>();

            try
            {
                List<CanvasDeviceViewModel> canvasDevices = null;
                _eventAggregator?.GetEvent<RequestCanvasDevicesEvent>()?.Publish(list => canvasDevices = list);

                if (canvasDevices != null)
                {
                    var deviceDisplayNames = GetDeviceDisplayNamesMapping();

                    foreach (var deviceViewModel in canvasDevices)
                    {
                        var device = deviceViewModel.Device;
                        if (device?.Commands != null)
                        {
                            var deviceDisplayName = GetDeviceDisplayNameWithNumber(
                                deviceViewModel.DeviceId, deviceDisplayNames, device.Name);

                            // ★ 声明了可监控参数白名单的设备:列表只出白名单(驱动规定好的才出现),
                            //   数据键统一为「自动采集」,与基类采集循环发布的样本键完全一致——
                            //   勾了必有数,不再依赖流程循环执行特定命令
                            if (device is DeviceBase declaredDevice && declaredDevice.CollectableParameters.Count > 0)
                            {
                                foreach (var cp in declaredDevice.CollectableParameters)
                                {
                                    var cpAxis = ParameterAxisClassifier.Classify(cp.Unit);
                                    availableParams.Add(new AvailableParameter
                                    {
                                        DeviceId = deviceViewModel.DeviceId,
                                        DeviceName = deviceDisplayName,
                                        CommandName = DeviceBase.AutoCollectCommandKey,
                                        ParameterName = cp.Name,
                                        ParameterType = "NumberParameter",
                                        DisplayName = $"{deviceDisplayName}.{cp.DisplayName}",
                                        IsNumeric = true,
                                        Unit = cp.Unit,
                                        AxisType = cpAxis,
                                        IsSelectable = cpAxis != AxisType.Unclassified,
                                        DisableReason = cpAxis == AxisType.Unclassified
                                            ? $"单位 \"{cp.Unit}\" 不属于温度/压力/流量,不能在当前监控图表中显示"
                                            : null,
                                    });
                                }
                                continue;
                            }

                            foreach (var command in device.Commands)
                            {
                                if (command.OutputParameters?.Variables != null)
                                {
                                    foreach (var param in command.OutputParameters.Variables)
                                    {
                                        var isNumeric = IsNumericParameter(param);
                                        if (isNumeric)
                                        {
                                            // ★ 关键修改:把参数的 Unit 一并填充到 AvailableParameter
                                            var unit = "";
                                            if (param is ParameterBase pb)
                                            {
                                                unit = pb.Unit ?? "";
                                            }

                                            // ★ 计算轴归属与可选性
                                            var axisType = ParameterAxisClassifier.Classify(unit);
                                            var isSelectable = axisType != AxisType.Unclassified;
                                            string disableReason = null;
                                            if (!isSelectable)
                                            {
                                                disableReason = string.IsNullOrEmpty(unit)
                                                    ? "该参数未定义单位,无法在监控图表上画曲线"
                                                    : $"单位 \"{unit}\" 不属于温度/压力/流量,不能在当前监控图表中显示";
                                            }

                                            availableParams.Add(new AvailableParameter
                                            {
                                                DeviceId = deviceViewModel.DeviceId,
                                                DeviceName = deviceDisplayName,
                                                CommandName = command.Name,
                                                ParameterName = param.Name,
                                                ParameterType = param.GetType().Name,
                                                DisplayName = $"{deviceDisplayName}.{command.Name}.{param.Name}",
                                                IsNumeric = true,
                                                Unit = unit,
                                                AxisType = axisType,         // ★ 新增
                                                IsSelectable = isSelectable, // ★ 新增
                                                DisableReason = disableReason, // ★ 新增
                                            });
                                        }
                                    }
                                }
                            }
                        }
                    }
                }

                _logger.LogDebug("获取到 {Count} 个可用参数", availableParams.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取可用参数失败");
            }

            return availableParams;
        }

        private Dictionary<string, string> GetDeviceDisplayNamesMapping()
        {
            try
            {
                Dictionary<string, string> deviceDisplayNames = null;
                _eventAggregator?.GetEvent<GetDeviceDisplayNamesRequestEvent>()?.Publish(callback => deviceDisplayNames = callback);
                return deviceDisplayNames ?? new Dictionary<string, string>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取设备显示名映射失败");
                return new Dictionary<string, string>();
            }
        }

        private string GetDeviceDisplayNameWithNumber(string deviceId, Dictionary<string, string> deviceDisplayNames, string fallbackName)
        {
            try
            {
                if (deviceDisplayNames.TryGetValue(deviceId, out var displayName) && !string.IsNullOrEmpty(displayName))
                    return displayName;

                if (!string.IsNullOrEmpty(fallbackName))
                {
                    var suffix = !string.IsNullOrEmpty(deviceId) && deviceId.Length > 6
                        ? deviceId.Substring(deviceId.Length - 6)
                        : deviceId;
                    return $"{fallbackName} ({suffix})";
                }
                return deviceId ?? "未知设备";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取设备显示名称失败,DeviceId: {DeviceId}", deviceId);
                return fallbackName ?? deviceId ?? "未知设备";
            }
        }

        private void OnMonitoringDataReceived(MonitoringCardUpdateEventArgs args)
        {
            if (args?.OutputParameters?.Variables == null) return;

            var timestamp = args.UpdateTime;
            var deviceId = args.DeviceId;
            var commandName = args.CommandName;

            try
            {
                StoreDataInGlobalCache(deviceId, commandName, args.OutputParameters.Variables, timestamp);

                lock (_lockObject)
                {
                    // 「自动采集」聚合样本按设备+参数名喂给任何已勾选条目——
                    // 旧工程按真实命令名保存的勾选(如 获取温度.T1)也能收到数据;
                    // 真实命令样本(流程循环/手动执行)仍按命令精确匹配
                    var isAutoCollect = commandName == DeviceBase.AutoCollectCommandKey;
                    var relevantParameters = _monitoredParameters
                        .Where(p => p.DeviceId == deviceId &&
                                   (isAutoCollect || p.CommandName == commandName) &&
                                   p.IsEnabled)
                        .ToList();

                    if (!relevantParameters.Any()) return;

                    foreach (var monitoredParam in relevantParameters)
                    {
                        var outputParam = args.OutputParameters.Variables
                            .FirstOrDefault(v => v.Name == monitoredParam.ParameterName);

                        if (outputParam != null && TryConvertToDouble(outputParam.Value, out double numericValue))
                        {
                            // ★ 顺便补充 Unit:如果监控参数里 Unit 还是空的,从输出参数取
                            if (string.IsNullOrEmpty(monitoredParam.Unit) && outputParam is ParameterBase opb)
                            {
                                if (!string.IsNullOrEmpty(opb.Unit))
                                {
                                    monitoredParam.Unit = opb.Unit;
                                    _logger.LogDebug("运行时补充参数单位: {Key} → {Unit}",
                                        monitoredParam.Key, opb.Unit);
                                }
                            }

                            var key = monitoredParam.Key;

                            if (_parameterData.TryGetValue(key, out var dataList))
                            {
                                lock (dataList)
                                {
                                    var dataPoint = new ParameterDataPoint
                                    {
                                        Timestamp = timestamp,
                                        Value = numericValue,
                                        ParameterKey = key
                                    };

                                    dataList.Add(dataPoint);

                                    if (dataList.Count > _maxDataPoints)
                                    {
                                        dataList.RemoveRange(0, dataList.Count - _maxDataPoints);
                                    }

                                    UpdateParameterStatistics(monitoredParam, numericValue);

                                    _logger.LogDebug("实时更新监控参数数据: {Key} = {Value} at {Time}",
                                        key, numericValue, timestamp.ToString("HH:mm:ss.fff"));
                                }
                            }
                        }
                    }

                    ParameterDataUpdated?.Invoke(this, new ParameterDataUpdatedEventArgs
                    {
                        DeviceId = deviceId,
                        CommandName = commandName,
                        UpdateTime = timestamp,
                        AffectedParametersCount = relevantParameters.Count
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "处理监控数据失败");
            }
        }

        private void StoreDataInGlobalCache(string deviceId, string commandName, IEnumerable<object> parameters, DateTime timestamp)
        {
            try
            {
                foreach (var param in parameters)
                {
                    if (param is ParameterBase paramBase && IsNumericParameter(param))
                    {
                        if (TryConvertToDouble(paramBase.Value, out double numericValue))
                        {
                            var key = GenerateParameterKey(deviceId, commandName, paramBase.Name);

                            var globalDataList = _globalDataCache.GetOrAdd(key, _ => new List<ParameterDataPoint>());

                            lock (globalDataList)
                            {
                                var dataPoint = new ParameterDataPoint
                                {
                                    Timestamp = timestamp,
                                    Value = numericValue,
                                    ParameterKey = key
                                };

                                globalDataList.Add(dataPoint);

                                if (globalDataList.Count > _maxGlobalCachePoints)
                                {
                                    globalDataList.RemoveRange(0, globalDataList.Count - _maxGlobalCachePoints);
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "存储数据到全局缓存失败");
            }
        }

        private void UpdateParameterStatistics(MonitoredParameter parameter, double value)
        {
            if (parameter.MinValue == double.MaxValue || value < parameter.MinValue)
                parameter.MinValue = value;
            if (parameter.MaxValue == double.MinValue || value > parameter.MaxValue)
                parameter.MaxValue = value;

            parameter.CurrentValue = value;
            parameter.LastUpdateTime = DateTime.Now;
        }

        private bool IsNumericParameter(object param)
        {
            if (param == null) return false;

            var type = param.GetType();
            var typeName = type.Name;

            return type == typeof(sbyte) || type == typeof(byte) ||
                   type == typeof(short) || type == typeof(ushort) ||
                   type == typeof(int) || type == typeof(uint) ||
                   type == typeof(long) || type == typeof(ulong) ||
                   type == typeof(float) || type == typeof(double) ||
                   type == typeof(decimal) ||
                   typeName.Contains("Number") || typeName.Contains("Double") ||
                   typeName.Contains("Float") || typeName.Contains("Integer") ||
                   typeName.Contains("Int");
        }

        private string GenerateParameterKey(string deviceId, string commandName, string parameterName)
        {
            return $"{deviceId}|{commandName}|{parameterName}";
        }

        private string GenerateColor(int index)
        {
            var colors = new[]
            {
                "#FF6B6B", "#4ECDC4", "#45B7D1", "#96CEB4", "#FFEAA7",
                "#DDA0DD", "#98D8C8", "#F7DC6F", "#BB8FCE", "#85C1E9",
                "#F8BBD9", "#C7CEEA", "#ABEBC6", "#F9E79F", "#FADBD8"
            };
            return colors[index % colors.Length];
        }

        private bool TryConvertToDouble(object value, out double result)
        {
            result = 0;
            if (value == null) return false;

            try
            {
                if (value is double d) { result = d; return true; }
                if (value is int i) { result = i; return true; }
                if (value is float f) { result = f; return true; }
                if (value is decimal dec) { result = (double)dec; return true; }
                if (value is long l) { result = l; return true; }
                if (value is short s) { result = s; return true; }
                if (value is byte b) { result = b; return true; }
                if (value is bool boolean) { result = boolean ? 1.0 : 0.0; return true; }

                if (double.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out result))
                    return true;

                _logger.LogDebug("无法转换为数值: {Value} (类型: {Type})", value, value.GetType().Name);
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "数值转换异常: {Value}", value);
                return false;
            }
        }

        public List<ParameterDataPoint> GetParameterDataInRange(string deviceId, string commandName, string parameterName)
        {
            var key = GenerateParameterKey(deviceId, commandName, parameterName);

            if (_parameterData.TryGetValue(key, out var data))
            {
                lock (data)
                {
                    var result = data.OrderBy(d => d.Timestamp).ToList();
                    if (result.Any()) return result;
                }
            }

            if (_globalDataCache.TryGetValue(key, out var globalData))
            {
                lock (globalData)
                {
                    return globalData.OrderBy(d => d.Timestamp).ToList();
                }
            }

            return new List<ParameterDataPoint>();
        }

        #region IDisposable Support
        private bool _disposed = false;

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    try
                    {
                        _eventAggregator?.GetEvent<MonitoringCardUpdateEvent>()?.Unsubscribe(OnMonitoringDataReceived);
                        _eventAggregator?.GetEvent<FlowExecutionStateChangedEvent>()?.Unsubscribe(OnFlowStateChanged);
                        _parameterData?.Clear();
                        _monitoredParameters?.Clear();
                        _logger.LogInformation("参数监控服务已释放");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "释放参数监控服务失败");
                    }
                }
                _disposed = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
        #endregion
    }
}
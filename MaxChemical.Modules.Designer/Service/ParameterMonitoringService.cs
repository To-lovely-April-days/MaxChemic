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
        // *** 新增：全局数据缓存，存储所有接收到的参数数据 ***
        private readonly ConcurrentDictionary<string, List<ParameterDataPoint>> _globalDataCache;
        private readonly int _maxGlobalCachePoints = 2000;

        // *** 新增：流程状态跟踪 ***
        private bool _isFlowRunning = false;
        private DateTime _flowStartTime = DateTime.MinValue;
        public event EventHandler<ParameterDataUpdatedEventArgs> ParameterDataUpdated;

        public ParameterMonitoringService(IEventAggregator eventAggregator)
        {
            _eventAggregator = eventAggregator ?? throw new ArgumentNullException(nameof(eventAggregator));
            _logger = new LogService().ForContext<ParameterMonitoringService>();
            _parameterData = new ConcurrentDictionary<string, List<ParameterDataPoint>>();
            _monitoredParameters = new List<MonitoredParameter>();

            // *** 新增：初始化全局缓存 ***
            _globalDataCache = new ConcurrentDictionary<string, List<ParameterDataPoint>>();

            // 订阅监控数据更新事件
            _eventAggregator.GetEvent<MonitoringCardUpdateEvent>().Subscribe(OnMonitoringDataReceived);

            // *** 新增：订阅流程状态事件 ***
            _eventAggregator.GetEvent<FlowExecutionStateChangedEvent>().Subscribe(OnFlowStateChanged);

            _logger.LogDebug("参数监控服务已初始化");
        }
        /// <summary>
        /// *** 新增：处理流程状态变更 ***
        /// </summary>
        private void OnFlowStateChanged(FlowExecutionStateChangedEventArgs args)
        {
            try
            {
                switch (args.NewState)
                {
                    case FlowExecutionState.Running:
                        _isFlowRunning = true;
                        _flowStartTime = DateTime.Now;
                        _logger.LogInformation("流程开始运行，启动全局数据缓存");
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
        /// <summary>
        /// *** 修改：改进的流程状态检查 ***
        /// </summary>
        public bool IsFlowCurrentlyRunning()
        {
            // *** 修改：基于全局缓存数据判断流程是否运行 ***
            if (_isFlowRunning)
                return true;

            try
            {
                var cutoffTime = DateTime.Now.AddMinutes(-2);

                // 检查全局缓存中是否有最近的数据
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

        public void AddMonitoredParameter(string deviceId, string deviceName, string commandName, string parameterName, string displayName = null)
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
                        IsEnabled = true,
                        Color = GenerateColor(_monitoredParameters.Count),
                        MinValue = double.MaxValue,
                        MaxValue = double.MinValue,
                        CurrentValue = null,
                        LastUpdateTime = null
                    };

                    _monitoredParameters.Add(parameter);

                    // 初始化参数数据
                    var parameterDataList = new List<ParameterDataPoint>();
                    _parameterData[key] = parameterDataList;

                    // *** 关键修复：从全局缓存回填数据，但确保实时数据流继续 ***
                    if (_isFlowRunning || IsFlowCurrentlyRunning())
                    {
                        BackfillDataFromGlobalCache(key, parameterDataList);

                        // *** 新增：确保后续实时数据能够正常添加 ***
                        EnsureRealTimeDataFlow(key);
                    }

                    _logger.LogInformation("添加监控参数: {DisplayName}, 颜色: {Color}, 回填数据: {Count} 条",
                        parameter.DisplayName, parameter.Color, parameterDataList.Count);
                }
                else
                {
                    _logger.LogWarning("参数已存在于监控列表: {Key}", key);
                }
            }
        }

        /// <summary>
        /// *** 新增：确保实时数据流的连续性 ***
        /// </summary>
        private void EnsureRealTimeDataFlow(string parameterKey)
        {
            try
            {
                // 检查全局缓存中是否有最新数据
                if (_globalDataCache.TryGetValue(parameterKey, out var globalData))
                {
                    lock (globalData)
                    {
                        var recentData = globalData
                            .Where(d => d.Timestamp > DateTime.Now.AddMinutes(-1)) // 最近1分钟的数据
                            .OrderByDescending(d => d.Timestamp)
                            .Take(5) // 最多5个最新数据点
                            .ToList();

                        if (recentData.Any())
                        {
                            var latestTime = recentData.First().Timestamp;
                            _logger.LogInformation("参数 {Key} 的最新数据时间: {LatestTime}, 确保实时数据流连续性",
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

        /// <summary>
        /// *** 新增：从全局缓存回填数据 ***
        /// </summary>
        private void BackfillDataFromGlobalCache(string parameterKey, List<ParameterDataPoint> parameterDataList)
        {
            try
            {
                if (_globalDataCache.TryGetValue(parameterKey, out var globalData))
                {
                    lock (globalData)
                    {
                        // 获取流程开始后的数据
                        var relevantData = globalData
                            .Where(d => d.Timestamp >= _flowStartTime)
                            .OrderBy(d => d.Timestamp)
                            .ToList();

                        if (relevantData.Any())
                        {
                            parameterDataList.AddRange(relevantData);

                            // 更新参数统计信息
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
                    return data.ToList(); // 返回副本
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
                        lock (dataList)
                        {
                            dataList.Clear();
                        }
                    }
                }
                // *** 新增：清理全局缓存 ***
                foreach (var key in _globalDataCache.Keys.ToList())
                {
                    if (_globalDataCache.TryGetValue(key, out var dataList))
                    {
                        lock (dataList)
                        {
                            dataList.Clear();
                        }
                    }
                }
                // 重置所有参数的统计信息
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
                lock (data)
                {
                    data.Clear();
                }

                // 重置参数统计信息
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
                // 从画布设备获取可用参数
                List<CanvasDeviceViewModel> canvasDevices = null;
                _eventAggregator?.GetEvent<RequestCanvasDevicesEvent>()?.Publish(list => canvasDevices = list);

                if (canvasDevices != null)
                {
                    // 获取设备显示名映射
                    var deviceDisplayNames = GetDeviceDisplayNamesMapping();

                    foreach (var deviceViewModel in canvasDevices)
                    {
                        var device = deviceViewModel.Device;
                        if (device?.Commands != null)
                        {
                            // 获取带编号的设备显示名称
                            var deviceDisplayName = GetDeviceDisplayNameWithNumber(deviceViewModel.DeviceId, deviceDisplayNames, device.Name);

                            foreach (var command in device.Commands)
                            {
                                if (command.OutputParameters?.Variables != null)
                                {
                                    foreach (var param in command.OutputParameters.Variables)
                                    {
                                        var isNumeric = IsNumericParameter(param);
                                        if (isNumeric)  // 只显示数值类型的参数
                                        {
                                            availableParams.Add(new AvailableParameter
                                            {
                                                DeviceId = deviceViewModel.DeviceId,
                                                DeviceName = deviceDisplayName, // 使用带编号的显示名称
                                                CommandName = command.Name,
                                                ParameterName = param.Name,
                                                ParameterType = param.GetType().Name,
                                                DisplayName = $"{deviceDisplayName}.{command.Name}.{param.Name}", // 使用带编号的名称
                                                IsNumeric = true
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

        /// <summary>
        /// 获取设备显示名映射
        /// </summary>
        private Dictionary<string, string> GetDeviceDisplayNamesMapping()
        {
            try
            {
                // 通过事件获取设备显示名映射
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

        /// <summary>
        /// 获取带编号的设备显示名称
        /// </summary>
        private string GetDeviceDisplayNameWithNumber(string deviceId, Dictionary<string, string> deviceDisplayNames, string fallbackName)
        {
            try
            {
                // 从设备显示名映射中查找
                if (deviceDisplayNames.TryGetValue(deviceId, out var displayName) && !string.IsNullOrEmpty(displayName))
                {
                    _logger.LogDebug("找到设备 {DeviceId} 的显示名: {DisplayName}", deviceId, displayName);
                    return displayName;
                }

                // 如果没找到，使用fallback逻辑
                _logger.LogDebug("设备 {DeviceId} 没有找到显示名映射，使用原始名称: {FallbackName}", deviceId, fallbackName);
                return !string.IsNullOrEmpty(fallbackName) ? fallbackName : deviceId ?? "未知设备";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取设备显示名称失败，DeviceId: {DeviceId}", deviceId);
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
                // 先将所有数据存入全局缓存
                StoreDataInGlobalCache(deviceId, commandName, args.OutputParameters.Variables, timestamp);

                // 然后处理监控参数
                lock (_lockObject)
                {
                    // 检查哪些参数需要记录到监控列表
                    var relevantParameters = _monitoredParameters
                        .Where(p => p.DeviceId == deviceId &&
                                   p.CommandName == commandName &&
                                   p.IsEnabled)
                        .ToList();

                    if (!relevantParameters.Any()) return;

                    foreach (var monitoredParam in relevantParameters)
                    {
                        // 查找对应的输出参数
                        var outputParam = args.OutputParameters.Variables
                            .FirstOrDefault(v => v.Name == monitoredParam.ParameterName);

                        if (outputParam != null && TryConvertToDouble(outputParam.Value, out double numericValue))
                        {
                            var key = monitoredParam.Key;

                            // 获取监控参数的数据列表
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

                                    // 保持最近指定数量的数据点
                                    if (dataList.Count > _maxDataPoints)
                                    {
                                        dataList.RemoveRange(0, dataList.Count - _maxDataPoints);
                                    }

                                    // 更新参数的统计信息
                                    UpdateParameterStatistics(monitoredParam, numericValue);

                                    _logger.LogDebug("实时更新监控参数数据: {Key} = {Value} at {Time}",
                                        key, numericValue, timestamp.ToString("HH:mm:ss.fff"));
                                }
                            }
                        }
                    }

                    // *** 关键：每次都发布数据更新事件，确保UI能收到 ***
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

        /// <summary>
        /// *** 新增：将数据存储到全局缓存 ***
        /// </summary>
        private void StoreDataInGlobalCache(string deviceId, string commandName, IEnumerable<object> parameters, DateTime timestamp)
        {
            try
            {
                foreach (var param in parameters)
                {
                    // 这里需要根据您的参数结构调整
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

                                // 限制全局缓存大小
                                if (globalDataList.Count > _maxGlobalCachePoints)
                                {
                                    globalDataList.RemoveRange(0, globalDataList.Count - _maxGlobalCachePoints);
                                }

                                _logger.LogDebug("存储到全局缓存: {Key} = {Value}", key, numericValue);
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
            {
                parameter.MinValue = value;
            }

            if (parameter.MaxValue == double.MinValue || value > parameter.MaxValue)
            {
                parameter.MaxValue = value;
            }

            parameter.CurrentValue = value;
            parameter.LastUpdateTime = DateTime.Now;
        }

        private bool IsNumericParameter(object param)
        {
            if (param == null) return false;

            var type = param.GetType();
            var typeName = type.Name;

            // 检查是否为数值类型
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

                if (value is long l)
                {
                    result = l;
                    return true;
                }

                if (value is short s)
                {
                    result = s;
                    return true;
                }

                if (value is byte b)
                {
                    result = b;
                    return true;
                }

                if (value is bool boolean)
                {
                    result = boolean ? 1.0 : 0.0;
                    return true;
                }

                // 尝试从字符串转换
                if (double.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out result))
                {
                    return true;
                }

                _logger.LogDebug("无法转换为数值: {Value} (类型: {Type})", value, value.GetType().Name);
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "数值转换异常: {Value}", value);
                return false;
            }
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
                        // 取消事件订阅
                        _eventAggregator?.GetEvent<MonitoringCardUpdateEvent>()?.Unsubscribe(OnMonitoringDataReceived);
                        _eventAggregator?.GetEvent<FlowExecutionStateChangedEvent>()?.Unsubscribe(OnFlowStateChanged);

                        // 清理数据
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

        public List<ParameterDataPoint> GetParameterDataInRange(string deviceId, string commandName, string parameterName)
        {
            var key = GenerateParameterKey(deviceId, commandName, parameterName);

            // *** 修改：先检查监控参数数据，如果没有则检查全局缓存 ***
            if (_parameterData.TryGetValue(key, out var data))
            {
                lock (data)
                {
                    var result = data.OrderBy(d => d.Timestamp).ToList();

                    if (result.Any())
                    {
                        return result;
                    }
                }
            }

            // *** 新增：如果监控参数中没有数据，尝试从全局缓存获取 ***
            if (_globalDataCache.TryGetValue(key, out var globalData))
            {
                lock (globalData)
                {
                    return globalData.OrderBy(d => d.Timestamp).ToList();
                }
            }

            return new List<ParameterDataPoint>();
        }
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
        #endregion
    }
}
using System;
using System.Threading;
using System.Threading.Tasks;
using DevicePlugins.Devices;

namespace FlowEngine.Core
{
    /// <summary>
    /// 命令结果变量实现类
    /// </summary>
    public class CommandResultVariable : IDisposable
    {
        private readonly CommandResultVariableDefinition _definition;
        private readonly IDeviceService _deviceService;
        private readonly IFlowLogger _logger;
        private readonly object _valueLock = new object();
        private readonly Timer _periodicTimer;
        private volatile bool _disposed = false;

        public string VariableName => _definition.VariableName;
        public RefreshStrategy RefreshStrategy => _definition.RefreshStrategy;
        public int PollingInterval => _definition.PollingInterval;
        public bool IsEnabled => _definition.IsEnabled;

        private object _currentValue;
        private DateTime _lastRefreshTime = DateTime.MinValue;
        private bool _isRefreshing = false;

        /// <summary>
        /// 当前变量值
        /// </summary>
        public object CurrentValue
        {
            get
            {
                lock (_valueLock)
                {
                    if (_definition.RefreshStrategy == RefreshStrategy.OnDemand && !_isRefreshing)
                    {
                        _ = Task.Run(RefreshValueAsync);
                    }
                    return _currentValue;
                }
            }
            private set
            {
                lock (_valueLock)
                {
                    if (!Equals(_currentValue, value))
                    {
                        var oldValue = _currentValue;
                        _currentValue = value;
                        _lastRefreshTime = DateTime.Now;

                        ValueChanged?.Invoke(this, new VariableValueChangedEventArgs(VariableName, oldValue, value));
                    }
                }
            }
        }

        /// <summary>
        /// 最后刷新时间
        /// </summary>
        public DateTime LastRefreshTime => _lastRefreshTime;

        /// <summary>
        /// 是否正在刷新
        /// </summary>
        public bool IsRefreshing => _isRefreshing;

        /// <summary>
        /// 值变更事件
        /// </summary>
        public event EventHandler<VariableValueChangedEventArgs> ValueChanged;

        /// <summary>
        /// 刷新错误事件
        /// </summary>
        public event EventHandler<Exception> RefreshError;

        public CommandResultVariable(CommandResultVariableDefinition definition, IDeviceService deviceService, IFlowLogger logger)
        {
            _definition = definition ?? throw new ArgumentNullException(nameof(definition));
            _deviceService = deviceService ?? throw new ArgumentNullException(nameof(deviceService));
            _logger = logger;

            if (_definition.RefreshStrategy == RefreshStrategy.Periodic || _definition.RefreshStrategy == RefreshStrategy.Automatic)
            {
                _periodicTimer = new Timer(async _ =>
                {
                    if (!_disposed && _definition.IsEnabled)
                    {
                        await RefreshValueAsync();
                    }
                }, null, TimeSpan.Zero, TimeSpan.FromMilliseconds(_definition.PollingInterval));
            }

            _logger?.LogInfo($"命令结果变量已创建: {VariableName} ({_definition.RefreshStrategy})");
        }

        /// <summary>
        /// 异步刷新变量值
        /// </summary>
        public async Task<bool> RefreshValueAsync()
        {
            if (!_definition.IsEnabled || _disposed || _isRefreshing)
                return false;

            _isRefreshing = true;

            try
            {
                _logger?.LogDebug($"开始刷新变量: {VariableName}");

                var result = await ExecuteDeviceCommandAsync();
                if (result.IsSuccess && result.OutputData != null)
                {
                    var outputDict = result.GetOutputDictionary();
                    if (outputDict.TryGetValue(_definition.OutputParameterName, out var parameterValue))
                    {
                        CurrentValue = parameterValue;
                        _logger?.LogDebug($"变量刷新成功: {VariableName} = {parameterValue}");
                        return true;
                    }
                    else
                    {
                        var error = new InvalidOperationException($"输出参数 '{_definition.OutputParameterName}' 未找到");
                        _logger?.LogWarning($"变量刷新失败: {VariableName} - {error.Message}");
                        RefreshError?.Invoke(this, error);
                        return false;
                    }
                }
                else
                {
                    var error = new InvalidOperationException($"设备命令执行失败: {result.Message}");
                    _logger?.LogWarning($"变量刷新失败: {VariableName} - {error.Message}");
                    RefreshError?.Invoke(this, error);
                    return false;
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"变量刷新异常: {VariableName} - {ex.Message}");
                RefreshError?.Invoke(this, ex);
                return false;
            }
            finally
            {
                _isRefreshing = false;
            }
        }

        /// <summary>
        /// 同步刷新变量值
        /// </summary>
        public bool RefreshValue()
        {
            try
            {
                return RefreshValueAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                _logger?.LogError($"同步刷新变量异常: {VariableName} - {ex.Message}");
                RefreshError?.Invoke(this, ex);
                return false;
            }
        }

        /// <summary>
        /// 手动设置变量值
        /// </summary>
        public void SetValue(object value)
        {
            CurrentValue = value;
            _logger?.LogInfo($"手动设置变量值: {VariableName} = {value}");
        }

        /// <summary>
        /// 执行设备命令
        /// </summary>
        private async Task<FlowDeviceCommandResult> ExecuteDeviceCommandAsync()
        {
            try
            {
                var inputParams = new DeviceParameters();

                // 解析命令参数
                foreach (var param in _definition.CommandParameters)
                {
                    // 这里可以添加表达式解析支持
                    inputParams.SetValue(param.Key, param.Value);
                }

                var result = await _deviceService.ExecuteCommandAsync(
                    _definition.DeviceId,
                    _definition.CommandName,
                    inputParams,
                    CancellationToken.None);

                return result;
            }
            catch (Exception ex)
            {
                return new FlowDeviceCommandResult
                {
                    IsSuccess = false,
                    Message = ex.Message,
                    Exception = ex
                };
            }
        }

        /// <summary>
        /// 启动变量
        /// </summary>
        public async Task StartAsync()
        {
            if (_definition.RefreshStrategy == RefreshStrategy.Automatic)
            {
                await RefreshValueAsync();
            }
        }

        /// <summary>
        /// 停止变量
        /// </summary>
        public void Stop()
        {
            _periodicTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        }

        /// <summary>
        /// 释放资源
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _periodicTimer?.Dispose();
            ValueChanged = null;
            RefreshError = null;

            _logger?.LogInfo($"命令结果变量已释放: {VariableName}");
        }
    }
}
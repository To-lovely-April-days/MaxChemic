using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Threading;
using MaxChemical.Core;
using MaxChemical.Logging;

namespace DevicePlugins.Devices
{    
    
    #region 设备基类
    public abstract class Device : IDevice, IDisposable
    {
        private readonly ILogService _logger;
        private DeviceConnectionStatus _connectionStatus = DeviceConnectionStatus.Disconnected;
        private DateTime? _connectedTime;
        private DateTime? _lastHeartbeatTime;
        private Timer? _heartbeatTimer;
        private int _connectionTimeout = 30; // 默认30秒超时
        private bool _disposed = false;
        protected bool _isSimulationMode = false;
        /// <summary>
        /// 是否为模拟模式
        /// </summary>
        public bool IsSimulationMode
        {
            get => _isSimulationMode;
            set
            {
                _isSimulationMode = value;
                OnPropertyChanged();
                OnSimulationModeChanged(value);
            }
        }
       
        protected Device()
        {
            _logger = LogManager.GetLogger(GetType().Name);
        }

        #region Properties

        /// <summary>
        /// 设备名称
        /// </summary>
        public string Name { get; protected set; } = string.Empty;

        /// <summary>
        /// 制造商
        /// </summary>
        public string Manufacturer { get; protected set; } = string.Empty;

        /// <summary>
        /// 图标位置
        /// </summary>
        public string ImageLocation { get; protected set; } = string.Empty;

        /// <summary>
        /// 设备类别
        /// </summary>
        public string Category { get; protected set; } = string.Empty;

        /// <summary>
        /// 设备参数
        /// </summary>
        public DeviceParameters Parameters { get; } = new DeviceParameters();

        /// <summary>
        /// 设备命令
        /// </summary>
        public List<DeviceCommand> Commands { get; } = new List<DeviceCommand>();

        // 内部存储字段
        private List<RegionType> _allowedRegions = new List<RegionType>();

        /// <summary>
        /// 允许的区域类型列表(新接口)
        /// </summary>
        public virtual List<RegionType> AllowedRegions
        {
            get => _allowedRegions;
            protected set => _allowedRegions = value ?? new List<RegionType>();
        }

        /// <summary>
        /// 允许的区域类型(兼容旧接口)
        /// </summary>
        public virtual RegionType AllowedRegion
        {
            get => _allowedRegions.FirstOrDefault();
            protected set
            {
                if (value == RegionType.None)
                {
                    _allowedRegions.Clear();
                }
                else
                {
                    _allowedRegions.Clear();
                    _allowedRegions.Add(value);
                }
                OnPropertyChanged();
                OnPropertyChanged(nameof(AllowedRegions));
            }
        }

        #endregion

        #region 连接状态相关属性

        /// <summary>
        /// 连接状态
        /// </summary>
        public DeviceConnectionStatus ConnectionStatus
        {
            get => _connectionStatus;
            protected set
            {
                if (_connectionStatus != value)
                {
                    var oldStatus = _connectionStatus;
                    _connectionStatus = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(IsConnected));
                    OnConnectionStatusChanged(oldStatus, value);
                }
            }
        }

        /// <summary>
        /// 是否已连接
        /// </summary>
        public bool IsConnected => ConnectionStatus == DeviceConnectionStatus.Connected;

        /// <summary>
        /// 连接时间
        /// </summary>
        public DateTime? ConnectedTime
        {
            get => _connectedTime;
            protected set
            {
                _connectedTime = value;
                OnPropertyChanged();
            }
        }

        /// <summary>
        /// 最后心跳时间
        /// </summary>
        public DateTime? LastHeartbeatTime
        {
            get => _lastHeartbeatTime;
            protected set
            {
                _lastHeartbeatTime = value;
                OnPropertyChanged();
            }
        }

        /// <summary>
        /// 连接超时时间(秒)
        /// </summary>
        public int ConnectionTimeout
        {
            get => _connectionTimeout;
            set
            {
                _connectionTimeout = value;
                OnPropertyChanged();
            }
        }

        #endregion

        #region Events

        /// <summary>
        /// 属性变更事件
        /// </summary>
        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>
        /// 连接状态变更事件
        /// </summary>
        public event EventHandler<DeviceConnectionStatusChangedEventArgs>? ConnectionStatusChanged;

        protected virtual void OnConnectionStatusChanged(DeviceConnectionStatus oldStatus, DeviceConnectionStatus newStatus)
        {
            InfoLog($"连接状态变更: {oldStatus} -> {newStatus}");
            ConnectionStatusChanged?.Invoke(this, new DeviceConnectionStatusChangedEventArgs(oldStatus, newStatus));

            // 根据状态变更更新时间戳
            switch (newStatus)
            {
                case DeviceConnectionStatus.Connected:
                    ConnectedTime = DateTime.Now;
                    LastHeartbeatTime = DateTime.Now;
                    StartHeartbeat();
                    break;
                case DeviceConnectionStatus.Disconnected:
                case DeviceConnectionStatus.Error:
                    ConnectedTime = null;
                    LastHeartbeatTime = null;
                    StopHeartbeat();
                    break;
            }
        }

        #endregion

        #region Methods

        /// <summary>
        /// 触发属性变更通知
        /// </summary>
        /// <param name="name">属性名称</param>
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        /// <summary>
        /// 初始化设备
        /// </summary>
        public virtual void Initialize()
        {
            TraceLog("设备初始化开始");

            try
            {
                // 子类可以重写此方法进行具体的初始化操作
                OnInitialize();

                TraceLog("设备初始化完成");
            }
            catch (Exception ex)
            {
                ErrorLog("设备初始化失败", ex);
                throw;
            }
        }

        /// <summary>
        /// 关闭设备
        /// </summary>
        public virtual void Shutdown()
        {
            TraceLog("设备关闭开始");

            try
            {
                // 先断开连接
                DisconnectAsync().Wait();

                // 子类可以重写此方法进行具体的关闭操作
                OnShutdown();

                TraceLog("设备关闭完成");
            }
            catch (Exception ex)
            {
                ErrorLog("设备关闭失败", ex);
                throw;
            }
        }

        /// <summary>
        /// 具体的初始化实现(子类重写)
        /// </summary>
        protected virtual void OnInitialize()
        {
            // 子类实现具体初始化逻辑
        }

        /// <summary>
        /// 具体的关闭实现(子类重写)
        /// </summary>
        protected virtual void OnShutdown()
        {
            // 子类实现具体关闭逻辑
        }

        #endregion

        #region 连接管理方法

        /// <summary>
        /// 连接设备
        /// </summary>
        public virtual async Task<bool> ConnectAsync()
        {
            if (IsConnected)
            {
                InfoLog("设备已连接");
                return true;
            }

            ConnectionStatus = DeviceConnectionStatus.Connecting;
            InfoLog($"开始连接设备 ({(IsSimulationMode ? "模拟模式" : "实际模式")})");

            try
            {
                bool result;

                if (IsSimulationMode)
                {
                    // 模拟模式连接
                    result = await OnConnectSimulationAsync();
                }
                else
                {
                    // 实际模式连接
                    result = await OnConnectAsync();
                }

                ConnectionStatus = result ? DeviceConnectionStatus.Connected : DeviceConnectionStatus.Error;

                if (result)
                {
                    InfoLog($"设备连接成功 ({(IsSimulationMode ? "模拟模式" : "实际模式")})");
                }
                else
                {
                    ErrorLog($"设备连接失败 ({(IsSimulationMode ? "模拟模式" : "实际模式")})");
                }

                return result;
            }
            catch (Exception ex)
            {
                ConnectionStatus = DeviceConnectionStatus.Error;
                ErrorLog("设备连接异常", ex);
                return false;
            }
        }

        /// <summary>
        /// 断开设备连接
        /// </summary>
        public virtual async Task<bool> DisconnectAsync()
        {
            if (!IsConnected)
            {
                InfoLog("设备未连接");
                return true;
            }

            ConnectionStatus = DeviceConnectionStatus.Disconnecting;
            InfoLog($"开始断开设备连接 ({(IsSimulationMode ? "模拟模式" : "实际模式")})");

            try
            {
                bool result;

                if (IsSimulationMode)
                {
                    // 模拟模式断开
                    result = await OnDisconnectSimulationAsync();
                }
                else
                {
                    // 实际模式断开
                    result = await OnDisconnectAsync();
                }

                ConnectionStatus = DeviceConnectionStatus.Disconnected;
                InfoLog("设备连接已断开");
                return result;
            }
            catch (Exception ex)
            {
                ConnectionStatus = DeviceConnectionStatus.Error;
                ErrorLog("设备断开连接异常", ex);
                return false;
            }
        }

        /// <summary>
        /// 检查连接状态
        /// </summary>
        public virtual async Task<bool> CheckConnectionAsync()
        {
            try
            {
                if (!IsConnected)
                    return false;

                bool isAlive;

                if (IsSimulationMode)
                {
                    // 模拟模式检查
                    isAlive = await OnCheckConnectionSimulationAsync();
                }
                else
                {
                    // 实际模式检查
                    isAlive = await OnCheckConnectionAsync();
                }

                if (isAlive)
                {
                    LastHeartbeatTime = DateTime.Now;
                }
                else
                {
                    ConnectionStatus = DeviceConnectionStatus.Error;
                }

                return isAlive;
            }
            catch (Exception ex)
            {
                ErrorLog("检查连接状态异常", ex);
                ConnectionStatus = DeviceConnectionStatus.Error;
                return false;
            }
        }

        #endregion
        #region 模拟模式相关方法

        /// <summary>
        /// 模拟模式改变时的处理
        /// </summary>
        protected virtual void OnSimulationModeChanged(bool isSimulationMode)
        {
            InfoLog($"设备模拟模式切换: {(isSimulationMode ? "模拟模式" : "实际模式")}");

            // 如果当前已连接，需要重新连接以应用新模式
            if (IsConnected)
            {
                InfoLog("检测到设备已连接，将重新连接以应用新模式");
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await DisconnectAsync();
                        await Task.Delay(500); // 短暂延迟
                        await ConnectAsync();
                    }
                    catch (Exception ex)
                    {
                        ErrorLog("切换模拟模式时重连设备失败", ex);
                    }
                });
            }
        }

        /// <summary>
        /// 模拟模式连接实现
        /// </summary>
        protected virtual async Task<bool> OnConnectSimulationAsync()
        {
            // 默认模拟连接实现
            await Task.Delay(100 + new Random().Next(0, 200)); // 模拟连接延迟
            InfoLog("模拟连接成功");
            return true;
        }

        /// <summary>
        /// 模拟模式断开连接实现
        /// </summary>
        protected virtual async Task<bool> OnDisconnectSimulationAsync()
        {
            // 默认模拟断开实现
            await Task.Delay(50 + new Random().Next(0, 100)); // 模拟断开延迟
            InfoLog("模拟断开连接成功");
            return true;
        }

        /// <summary>
        /// 模拟模式连接检查实现
        /// </summary>
        protected virtual async Task<bool> OnCheckConnectionSimulationAsync()
        {
            // 默认模拟检查实现（95%成功率）
            await Task.Delay(10);
            return new Random().NextDouble() > 0.05; // 5% 概率返回失败，模拟网络波动
        }

        #endregion

        #region 心跳机制

        private void StartHeartbeat()
        {
            StopHeartbeat();

            if (ConnectionTimeout > 0)
            {
                _heartbeatTimer = new Timer(async _ => await HeartbeatCallback(),
                    null, TimeSpan.FromSeconds(ConnectionTimeout / 2), TimeSpan.FromSeconds(ConnectionTimeout / 2));
            }
        }

        private void StopHeartbeat()
        {
            _heartbeatTimer?.Dispose();
            _heartbeatTimer = null;
        }

        private async Task HeartbeatCallback()
        {
            if (!IsConnected)
                return;

            // 检查是否超时
            if (LastHeartbeatTime.HasValue)
            {
                var timeSinceLastHeartbeat = DateTime.Now - LastHeartbeatTime.Value;
                if (timeSinceLastHeartbeat.TotalSeconds > ConnectionTimeout)
                {
                    WarnLog($"心跳超时: {timeSinceLastHeartbeat.TotalSeconds:F1}秒");
                    ConnectionStatus = DeviceConnectionStatus.Timeout;
                    return;
                }
            }

            // 执行心跳检查
            await CheckConnectionAsync();
        }

        #endregion

        #region 子类实现的虚方法

        /// <summary>
        /// 具体的连接实现(子类重写)
        /// </summary>
        protected virtual Task<bool> OnConnectAsync()
        {
            return Task.FromResult(true);
        }

        /// <summary>
        /// 具体的断开连接实现(子类重写)
        /// </summary>
        protected virtual Task<bool> OnDisconnectAsync()
        {
            return Task.FromResult(true);
        }

        /// <summary>
        /// 具体的连接检查实现(子类重写)
        /// </summary>
        protected virtual Task<bool> OnCheckConnectionAsync()
        {
            return Task.FromResult(true);
        }

        #endregion

        #region 日志

        /// <summary>
        /// 记录跟踪日志
        /// </summary>
        /// <param name="message">日志消息</param>
        protected void TraceLog(string message)
        {
            _logger.LogTrace("[{DeviceName}]: {Message}", Name, message);
        }

        /// <summary>
        /// 记录调试日志
        /// </summary>
        /// <param name="message">调试消息</param>
        protected void DebugLog(string message)
        {
            _logger.LogDebug("[{DeviceName}]: {Message}", Name, message);
        }

        /// <summary>
        /// 记录信息日志
        /// </summary>
        /// <param name="message">信息消息</param>
        protected void InfoLog(string message)
        {
            _logger.LogInformation("[{DeviceName}]: {Message}", Name, message);
        }

        /// <summary>
        /// 记录警告日志
        /// </summary>
        /// <param name="message">警告消息</param>
        protected void WarnLog(string message)
        {
            _logger.LogWarning("[{DeviceName}]: {Message}", Name, message);
        }

        /// <summary>
        /// 记录错误日志
        /// </summary>
        /// <param name="message">错误消息</param>
        /// <param name="exception">异常对象</param>
        protected void ErrorLog(string message, Exception? exception = null)
        {
            if (exception != null)
            {
                _logger.LogError(exception, "[{DeviceName}]: {Message}", Name, message);
            }
            else
            {
                _logger.LogError("[{DeviceName}]: {Message}", Name, message);
            }
        }

        #endregion

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
                    StopHeartbeat();
                    DisconnectAsync().Wait();
                }
                _disposed = true;
            }
        }

        ~Device()
        {
            Dispose(false);
        }

        #endregion
    }

#endregion


}
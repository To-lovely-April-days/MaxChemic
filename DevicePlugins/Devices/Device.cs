using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using MaxChemical.Core;
using MaxChemical.Logging;

namespace DevicePlugins.Devices
{    
    
    #region 设备基类
    public abstract class Device : IDevice, IDeviceUIInteraction, IDisposable
    {
        private readonly ILogService _logger;
        private DeviceConnectionStatus _connectionStatus = DeviceConnectionStatus.Disconnected;
        private DateTime? _connectedTime;
        private DateTime? _lastHeartbeatTime;
        private Timer? _heartbeatTimer;
        private int _connectionTimeout = 30; // 默认30秒超时
        private bool _disposed = false;
        protected bool _isSimulationMode = false;

        // 新增字段
        private readonly List<DeviceActiveState> _activeStates = new List<DeviceActiveState>();
        private readonly List<DeviceActiveState> _pausedStates = new List<DeviceActiveState>();
        private readonly object _statesLock = new object();

        // 添加连接管理器字段
        protected IDeviceConnectionManager ConnectionManager { get;  set; }
        protected string? ComId { get; set; }
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
        // ✅ 实现接口事件
        public event EventHandler<DeviceControlStateChangedEventArgs> ControlStateChanged;

        protected Device()
        {
            _logger = LogManager.GetLogger(GetType().Name);
            // 初始化连接管理器
            InitializeConnectionManager();
        }
        private void InitializeConnectionManager()
        {
            try
            {
                ConnectionManager = DeviceConnectionManagerFactory.GetInstance();
            }
            catch (Exception ex)
            {
                ErrorLog("初始化连接管理器失败", ex);
            }
        }

        #region Properties

        public string DeviceId { get; set; } = string.Empty;

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
                    //StartHeartbeat();
                    break;
                case DeviceConnectionStatus.Disconnected:
                case DeviceConnectionStatus.Error:
                    ConnectedTime = null;
                    LastHeartbeatTime = null;
                    //StopHeartbeat();
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
            InfoLog($"开始连接设备 ({GetConnectionModeDescription()})");

            try
            {
                bool result;

                if (DeviceConnectionConfig.CommunicationMode != CommunicationMode.Direct &&
                    ConnectionManager != null && !string.IsNullOrEmpty(ComId))
                {
                    // 统一连接模式：通过连接管理器连接
                    result = await ConnectionManager.ConnectAsync(ComId);
                    InfoLog($"通过{GetConnectionModeDescription()}连接: {result}");
                }
                else
                {
                    // 直连模式：使用原有的连接逻辑
                    if (IsSimulationMode)
                    {
                        result = await OnConnectSimulationAsync();
                    }
                    else
                    {
                        result = await OnConnectAsync();
                    }
                }

                ConnectionStatus = result ? DeviceConnectionStatus.Connected : DeviceConnectionStatus.Error;
                return result;
            }
            catch (Exception ex)
            {
                ConnectionStatus = DeviceConnectionStatus.Error;
                ErrorLog("设备连接异常", ex);
                return false;
            }
        }
        private string GetConnectionModeDescription()
        {
            return DeviceConnectionConfig.CommunicationMode switch
            {
                CommunicationMode.PLC => "PLC模式",
                CommunicationMode.ModbusTcp => "ModbusTcp模式",
                CommunicationMode.Direct => IsSimulationMode ? "模拟模式" : "直连模式",
                _ => "未知模式"
            };
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
                if (DeviceConnectionConfig.UsePLCMode && ConnectionManager != null && !string.IsNullOrEmpty(ComId))
                {
                    // PLC模式：通过连接管理器连接
                    result = await ConnectionManager.DisconnectAsync(ComId);
                    InfoLog($"通过PLC连接管理器连接: {result}");
                }
                else
                {
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
                if (DeviceConnectionConfig.UsePLCMode && ConnectionManager != null && !string.IsNullOrEmpty(ComId))
                {
                    // PLC模式：通过连接管理器连接
                    isAlive = await ConnectionManager.CheckConnectionAsync(ComId);
                    InfoLog($"通过PLC连接管理器连接: {isAlive}");
                }else
                {
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
                    InfoLog("没有活跃状态需要暂停");
                    return true;
                }

                InfoLog($"暂停 {_activeStates.Count} 个活跃状态");
            }

            var pauseTasks = new List<Task<bool>>();
            DeviceActiveState[] statesToPause;

            lock (_statesLock)
            {
                statesToPause = _activeStates.ToArray();
            }

            foreach (var state in statesToPause)
            {
                pauseTasks.Add(PauseStateAsync(state));
            }

            try
            {
                var results = await Task.WhenAll(pauseTasks);
                bool allSuccess = results.All(r => r);

                if (allSuccess)
                {
                    InfoLog("所有活跃状态已暂停");
                }
                else
                {
                    WarnLog("部分活跃状态暂停失败");
                }

                return allSuccess;
            }
            catch (Exception ex)
            {
                ErrorLog("暂停活跃状态异常", ex);
                return false;
            }
        }

        public virtual async Task<bool> ResumeAllPausedStatesAsync()
        {
            lock (_statesLock)
            {
                if (!_pausedStates.Any())
                {
                    InfoLog("没有暂停的状态需要恢复");
                    return true;
                }

                InfoLog($"恢复 {_pausedStates.Count} 个暂停的状态");
            }

            var resumeTasks = new List<Task<bool>>();
            DeviceActiveState[] statesToResume;

            lock (_statesLock)
            {
                statesToResume = _pausedStates.ToArray();
            }

            foreach (var state in statesToResume)
            {
                resumeTasks.Add(ResumeStateAsync(state));
            }

            try
            {
                var results = await Task.WhenAll(resumeTasks);
                bool allSuccess = results.All(r => r);

                if (allSuccess)
                {
                    InfoLog("所有暂停状态已恢复");
                }
                else
                {
                    WarnLog("部分暂停状态恢复失败");
                }

                return allSuccess;
            }
            catch (Exception ex)
            {
                ErrorLog("恢复暂停状态异常", ex);
                return false;
            }
        }

        public virtual async Task<bool> StopAllActiveStatesAsync()
        {
            lock (_statesLock)
            {
                if (!_activeStates.Any() && !_pausedStates.Any())
                {
                    InfoLog("没有活跃或暂停的状态需要停止");
                    return true;
                }

                InfoLog($"停止 {_activeStates.Count} 个活跃状态和 {_pausedStates.Count} 个暂停状态");
            }

            var stopTasks = new List<Task<bool>>();
            DeviceActiveState[] allStates;

            lock (_statesLock)
            {
                allStates = _activeStates.Concat(_pausedStates).ToArray();
            }

            foreach (var state in allStates)
            {
                stopTasks.Add(StopStateAsync(state));
            }

            try
            {
                var results = await Task.WhenAll(stopTasks);
                bool allSuccess = results.All(r => r);

                // 清空所有状态
                lock (_statesLock)
                {
                    _activeStates.Clear();
                    _pausedStates.Clear();
                }

                if (allSuccess)
                {
                    InfoLog("所有状态已停止");
                }
                else
                {
                    WarnLog("部分状态停止失败");
                }

                return allSuccess;
            }
            catch (Exception ex)
            {
                ErrorLog("停止状态异常", ex);
                return false;
            }
        }

        #endregion

        #region 状态管理内部方法

        /// <summary>
        /// 添加活跃状态
        /// </summary>
        protected void AddActiveState(string stateName, string activateCommand, string deactivateCommand,
            string pauseCommand = null, string resumeCommand = null, object stateData = null)
        {
            lock (_statesLock)
            {
                // 检查是否已经存在相同状态
                var existing = _activeStates.FirstOrDefault(s => s.StateName == stateName);
                if (existing != null)
                {
                    WarnLog($"状态已存在，更新时间: {stateName}");
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
                InfoLog($"添加活跃状态: {stateName}");
            }

            StateChanged?.Invoke(this, new DeviceStateChangedEventArgs
            {
                StateName = stateName,
                IsActive = true,
                Timestamp = DateTime.Now
            });
        }

        /// <summary>
        /// 移除活跃状态
        /// </summary>
        protected void RemoveActiveState(string stateName)
        {
            lock (_statesLock)
            {
                var state = _activeStates.FirstOrDefault(s => s.StateName == stateName);
                if (state != null)
                {
                    _activeStates.Remove(state);
                    InfoLog($"移除活跃状态: {stateName}");
                }

                // 同时从暂停状态中移除
                var pausedState = _pausedStates.FirstOrDefault(s => s.StateName == stateName);
                if (pausedState != null)
                {
                    _pausedStates.Remove(pausedState);
                    InfoLog($"移除暂停状态: {stateName}");
                }
            }

            StateChanged?.Invoke(this, new DeviceStateChangedEventArgs
            {
                StateName = stateName,
                IsActive = false,
                Timestamp = DateTime.Now
            });
        }

        /// <summary>
        /// 暂停单个状态
        /// </summary>
        private async Task<bool> PauseStateAsync(DeviceActiveState state)
        {
            try
            {
                InfoLog($"暂停状态: {state.StateName}");

                // 如果有专用的暂停命令，执行暂停命令
                if (!string.IsNullOrEmpty(state.PauseCommand))
                {
                    var pauseCommand = Commands.FirstOrDefault(c => c.Name == state.PauseCommand);
                    if (pauseCommand?.Action != null)
                    {
                        await Task.Run(() => pauseCommand.Action(new DeviceParameters()));
                        InfoLog($"执行暂停命令: {state.StateName} - {state.PauseCommand}");
                    }
                    else
                    {
                        WarnLog($"未找到暂停命令: {state.PauseCommand}");
                    }
                }
                // 如果没有暂停命令，执行停止命令
                else if (!string.IsNullOrEmpty(state.DeactivateCommand))
                {
                    var stopCommand = Commands.FirstOrDefault(c => c.Name == state.DeactivateCommand);
                    if (stopCommand?.Action != null)
                    {
                        await Task.Run(() => stopCommand.Action(new DeviceParameters()));
                        InfoLog($"执行停止命令: {state.StateName} - {state.DeactivateCommand}");
                    }
                    else
                    {
                        WarnLog($"未找到停止命令: {state.DeactivateCommand}");
                    }
                }

                // 移动到暂停状态列表
                lock (_statesLock)
                {
                    _activeStates.Remove(state);
                    state.IsPaused = true;
                    _pausedStates.Add(state);
                }

                return true;
            }
            catch (Exception ex)
            {
                ErrorLog($"暂停状态失败: {state.StateName}", ex);
                return false;
            }
        }

        /// <summary>
        /// 恢复单个状态
        /// </summary>
        private async Task<bool> ResumeStateAsync(DeviceActiveState state)
        {
            try
            {
                InfoLog($"恢复状态: {state.StateName}");

                // 如果有专用的恢复命令，执行恢复命令
                if (!string.IsNullOrEmpty(state.ResumeCommand))
                {
                    var resumeCommand = Commands.FirstOrDefault(c => c.Name == state.ResumeCommand);
                    if (resumeCommand?.Action != null)
                    {
                        await Task.Run(() => resumeCommand.Action(new DeviceParameters()));
                        InfoLog($"执行恢复命令: {state.StateName} - {state.ResumeCommand}");
                    }
                    else
                    {
                        WarnLog($"未找到恢复命令: {state.ResumeCommand}");
                    }
                }
                // 如果没有恢复命令，重新执行激活命令
                else if (!string.IsNullOrEmpty(state.ActivateCommand))
                {
                    var activateCommand = Commands.FirstOrDefault(c => c.Name == state.ActivateCommand);
                    if (activateCommand?.Action != null)
                    {
                        await Task.Run(() => activateCommand.Action(new DeviceParameters()));
                        InfoLog($"重新执行激活命令: {state.StateName} - {state.ActivateCommand}");
                    }
                    else
                    {
                        WarnLog($"未找到激活命令: {state.ActivateCommand}");
                    }
                }

                // 移动回活跃状态列表
                lock (_statesLock)
                {
                    _pausedStates.Remove(state);
                    state.IsPaused = false;
                    state.ActivatedTime = DateTime.Now; // 更新激活时间
                    _activeStates.Add(state);
                }

                return true;
            }
            catch (Exception ex)
            {
                ErrorLog($"恢复状态失败: {state.StateName}", ex);
                return false;
            }
        }

        /// <summary>
        /// 停止单个状态
        /// </summary>
        private async Task<bool> StopStateAsync(DeviceActiveState state)
        {
            try
            {
                InfoLog($"停止状态: {state.StateName}");

                if (!string.IsNullOrEmpty(state.DeactivateCommand))
                {
                    var stopCommand = Commands.FirstOrDefault(c => c.Name == state.DeactivateCommand);
                    if (stopCommand?.Action != null)
                    {
                        await Task.Run(() => stopCommand.Action(new DeviceParameters()));
                        InfoLog($"执行停止命令: {state.StateName} - {state.DeactivateCommand}");
                    }
                    else
                    {
                        WarnLog($"未找到停止命令: {state.DeactivateCommand}");
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                ErrorLog($"停止状态失败: {state.StateName}", ex);
                return false;
            }
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
        /// <summary>
        /// ✅ 虚方法：执行UI命令（子类可重写）
        /// </summary>
        public virtual async Task<CommandExecutionResult> ExecuteUICommandAsync(
            string commandName,
            Dictionary<string, object> parameters = null)
        {
            // 默认实现：查找对应命令并执行
            var command = Commands?.FirstOrDefault(c => c.Name == commandName);
            if (command?.Action == null)
            {
                return new CommandExecutionResult
                {
                    Success = false,
                    Message = $"未找到命令: {commandName}"
                };
            }

            try
            {
                var inputParams = new DeviceParameters();
                if (parameters != null)
                {
                    foreach (var kvp in parameters)
                    {
                        // 添加参数到 inputParams
                    }
                }

                var output = await Task.Run(() => command.Action(inputParams));
                var success = output?.GetValue<bool>("Success") ?? false;

                return new CommandExecutionResult
                {
                    Success = success,
                    Message = success ? "命令执行成功" : "命令执行失败",
                    OutputData = ConvertToOutputData(output)
                };
            }
            catch (Exception ex)
            {
                return new CommandExecutionResult
                {
                    Success = false,
                    Message = ex.Message,
                    Exception = ex
                };
            }
        }

        /// <summary>
        /// ✅ 虚方法：获取当前状态（子类可重写）
        /// </summary>
        public virtual DeviceState GetCurrentState()
        {
            return new DeviceState
            {
                States = new Dictionary<string, object>
                {
                    ["DeviceId"] = this.DeviceId,
                    ["ComId"] = this.ComId,
                    ["IsConnected"] = this.IsConnected
                }
            };
        }

        /// <summary>
        /// 支持自定义所有阶段的状态同步
        /// </summary>
        protected Func<DeviceParameters, DeviceParameters> WrapActionWithAutoStateSync(
      string commandName,
      Func<DeviceParameters, Task<DeviceParameters>> innerAsync,
      Action<DeviceParameters> onSuccessStateSync = null,
      Action onStartStateSync = null,
      Action<DeviceParameters> onFailureStateSync = null,
      Action<string> onErrorStateSync = null)
        {
            return (DeviceParameters parameters) =>
            {
                var sw = Stopwatch.StartNew();

                try
                {
                    // 1️⃣ 发送"执行中"状态
                    OnCommandStateChanged(commandName, CommandExecutionState.Running); // ✅ 直接传枚举
                    onStartStateSync?.Invoke();

                    // 2️⃣ 执行命令
                    var output = innerAsync(parameters).GetAwaiter().GetResult();
                    sw.Stop();

                    // 3️⃣ 判断成功/失败
                    var success = output?.GetValue<bool>("Success") ?? false;

                    if (success)
                    {
                        onSuccessStateSync?.Invoke(output);
                        OnCommandStateChanged(commandName, CommandExecutionState.Succeeded, output); // ✅ 传枚举 + 输出
                    }
                    else
                    {
                        onFailureStateSync?.Invoke(output);
                        OnCommandStateChanged(commandName, CommandExecutionState.Failed, output); // ✅ 传枚举 + 输出
                    }

                    return output;
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    onErrorStateSync?.Invoke(ex.Message);
                    OnCommandStateChanged(commandName, CommandExecutionState.Error,
                        errorMessage: ex.Message); // ✅ 传枚举 + 错误 + 异常

                    throw new DeviceCommandExecutionException(Name, commandName, ex.Message, ex);
                }
            };
        }

        /// <summary>
        /// ✅ 触发命令执行状态事件
        /// </summary>
        protected virtual void OnCommandStateChanged(
            string commandName,
            CommandExecutionState state,
            DeviceParameters output = null,
            string errorMessage = null)
        {
            try
            {
                var eventArgs = new DeviceControlStateChangedEventArgs
                {
                    DeviceId = this.DeviceId,
                    ComId = this.ComId,
                    CommandName = commandName,
                    StateName = $"CommandState_{commandName}",
                    StateValue = state,
                    ExecutionState = state,
                    Timestamp = DateTime.Now,
                    AdditionalData = new Dictionary<string, object>()
                };

                if (output != null)
                {
                    eventArgs.AdditionalData["OutputParameters"] = output;
                }

                if (!string.IsNullOrEmpty(errorMessage))
                {
                    eventArgs.AdditionalData["ErrorMessage"] = errorMessage;
                }

                // ✅ 直接触发事件
                ControlStateChanged?.Invoke(this, eventArgs);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"触发命令状态事件失败: {ex.Message}");
            }
        }

        /// <summary>
        /// ✅ 触发设备状态变化事件
        /// </summary>
        protected virtual void OnStateChanged(string stateName, object stateValue)
        {
            try
            {
                ControlStateChanged?.Invoke(this, new DeviceControlStateChangedEventArgs
                {
                    DeviceId = this.DeviceId,
                    ComId = this.ComId,
                    StateName = stateName,
                    StateValue = stateValue,
                    Timestamp = DateTime.Now
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"触发状态变化事件失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 辅助方法：转换输出参数为字典
        /// </summary>
        private Dictionary<string, object> ConvertToOutputData(DeviceParameters output)
        {
            var data = new Dictionary<string, object>();
            if (output?.Variables != null)
            {
                foreach (var variable in output.Variables)
                {
                    data[variable.Name] = variable.Value;
                }
            }
            return data;
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
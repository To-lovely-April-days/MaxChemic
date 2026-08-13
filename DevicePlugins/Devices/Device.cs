using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using DevicePlugins.Devices.Remote;
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
        private int _connectionTimeout = 30;
        private bool _disposed = false;
        protected bool _isSimulationMode = false;

        private readonly List<DeviceActiveState> _activeStates = new List<DeviceActiveState>();
        private readonly List<DeviceActiveState> _pausedStates = new List<DeviceActiveState>();
        private readonly object _statesLock = new object();

        protected IDeviceConnectionManager ConnectionManager { get; set; }
        protected string? ComId { get; set; }

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

        public event EventHandler<DeviceControlStateChangedEventArgs> ControlStateChanged;

        protected Device()
        {
            _logger = LogManager.GetLogger(GetType().Name);
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

        /// <inheritdoc/>
        public virtual bool SupportsZLanGateway => false;

        #region Properties

        public string DeviceId { get; set; } = string.Empty;
        public string Name { get; protected set; } = string.Empty;
        public string Manufacturer { get; protected set; } = string.Empty;
        public string ImageLocation { get; protected set; } = string.Empty;
        public string Category { get; protected set; } = string.Empty;
        public DeviceParameters Parameters { get; } = new DeviceParameters();
        public List<DeviceCommand> Commands { get; } = new List<DeviceCommand>();

        private List<RegionType> _allowedRegions = new List<RegionType>();

        public virtual List<RegionType> AllowedRegions
        {
            get => _allowedRegions;
            protected set => _allowedRegions = value ?? new List<RegionType>();
        }

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

        public bool IsConnected => ConnectionStatus == DeviceConnectionStatus.Connected;

        public DateTime? ConnectedTime
        {
            get => _connectedTime;
            protected set
            {
                _connectedTime = value;
                OnPropertyChanged();
            }
        }

        public DateTime? LastHeartbeatTime
        {
            get => _lastHeartbeatTime;
            protected set
            {
                _lastHeartbeatTime = value;
                OnPropertyChanged();
            }
        }

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

        public event PropertyChangedEventHandler? PropertyChanged;
        public event EventHandler<DeviceConnectionStatusChangedEventArgs>? ConnectionStatusChanged;

        protected virtual void OnConnectionStatusChanged(DeviceConnectionStatus oldStatus, DeviceConnectionStatus newStatus)
        {
            InfoLog($"连接状态变更: {oldStatus} -> {newStatus}");
            ConnectionStatusChanged?.Invoke(this, new DeviceConnectionStatusChangedEventArgs(oldStatus, newStatus));

            switch (newStatus)
            {
                case DeviceConnectionStatus.Connected:
                    ConnectedTime = DateTime.Now;
                    LastHeartbeatTime = DateTime.Now;
                    StartAutoCollect();
                    break;
                case DeviceConnectionStatus.Disconnected:
                case DeviceConnectionStatus.Error:
                    ConnectedTime = null;
                    LastHeartbeatTime = null;
                    if (!_flowCollectHold) StopAutoCollect();
                    break;
            }
        }

        #endregion

        #region Methods

        protected void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        public virtual void Initialize()
        {
            TraceLog("设备初始化开始");
            try
            {
                OnInitialize();
                TraceLog("设备初始化完成");
            }
            catch (Exception ex)
            {
                ErrorLog("设备初始化失败", ex);
                throw;
            }
        }

        public virtual void Shutdown()
        {
            TraceLog("设备关闭开始");
            try
            {
                DisconnectAsync().Wait();
                OnShutdown();
                TraceLog("设备关闭完成");
            }
            catch (Exception ex)
            {
                ErrorLog("设备关闭失败", ex);
                throw;
            }
        }

        protected virtual void OnInitialize() { }
        protected virtual void OnShutdown() { }

        #endregion

        #region 通信模式解析

        /// <summary>
        /// 取当前设备的通信方式。
        /// 优先用本设备实例的参数 "通信方式" (每个设备实例可自选)。
        /// 没设这个参数时,fall back 到 DeviceConnectionConfig.CommunicationMode (全局默认)。
        /// </summary>
        protected CommunicationMode GetDeviceCommunicationMode()
        {
            try
            {
                var modeStr = Parameters?.GetValue<string>("通信方式");
                if (!string.IsNullOrEmpty(modeStr) &&
                    Enum.TryParse<CommunicationMode>(modeStr, out var mode))
                {
                    return mode;
                }
            }
            catch { }
            return DeviceConnectionConfig.CommunicationMode;
        }

        private string GetConnectionModeDescription(CommunicationMode mode)
        {
            return mode switch
            {
                CommunicationMode.PLC => "PLC模式",
                CommunicationMode.ModbusTcp => "ModbusTcp模式",
                CommunicationMode.ZLanGateway => "ZLAN网关模式",
                CommunicationMode.Direct => IsSimulationMode ? "模拟模式" : "直连模式",
                _ => "未知模式"
            };
        }

        #endregion

        #region 连接管理方法

        public virtual async Task<bool> ConnectAsync()
        {
            if (IsConnected)
            {
                InfoLog("设备已连接");
                return true;
            }

            ConnectionStatus = DeviceConnectionStatus.Connecting;
            var mode = GetDeviceCommunicationMode();
            InfoLog($"开始连接设备 ({GetConnectionModeDescription(mode)})");

            try
            {
                bool result;

                switch (mode)
                {
                    case CommunicationMode.ZLanGateway:
                        result = await OnConnectViaGatewayAsync();
                        break;

                    case CommunicationMode.PLC:
                    case CommunicationMode.ModbusTcp:
                        if (ConnectionManager != null && !string.IsNullOrEmpty(ComId))
                        {
                            result = await ConnectionManager.ConnectAsync(ComId);
                            InfoLog($"通过 {mode} 连接管理器连接: {result}");
                        }
                        else
                        {
                            WarnLog($"通信方式为 {mode} 但 ConnectionManager 或 ComId 未配置,fallback 直连");
                            result = IsSimulationMode
                                ? await OnConnectSimulationAsync()
                                : await OnConnectAsync();
                        }
                        break;

                    case CommunicationMode.RemoteServer:
                        if (IsSimulationMode)
                        {
                            result = await OnConnectSimulationAsync();
                            break;
                        }
                        // 1) 预热云隧道:把首次建连(SignalR negotiate+握手)开销挪到"连接"动作里,首条数据命令就不再慢
                        var tunnelOk = await RemoteGatewayClient.Instance.ConnectAsync();
                        if (!tunnelOk)
                        {
                            WarnLog("RemoteServer 连接失败:云隧道未连通");
                            result = false;
                            break;
                        }
                        // 2) 校验本设备的 DTU 是否真的在线(否则填错序列号也会"连上")
                        var dtuSerial = Parameters?.GetValue<string>("DTU序列号");
                        if (string.IsNullOrWhiteSpace(dtuSerial))
                        {
                            WarnLog("RemoteServer 连接失败:未配置「DTU序列号」");
                            result = false;
                            break;
                        }
                        result = await RemoteGatewayClient.Instance.IsSerialOnlineAsync(dtuSerial);
                        if (!result)
                            WarnLog($"RemoteServer 连接失败:DTU[{dtuSerial}] 未在线(序列号是否填错?或 DTU 未上线)");
                        else
                            InfoLog($"RemoteServer 连接成功:隧道OK 且 DTU[{dtuSerial}] 在线");
                        break;

                    case CommunicationMode.Direct:
                    default:
                        result = IsSimulationMode
                            ? await OnConnectSimulationAsync()
                            : await OnConnectAsync();
                        break;
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

        public virtual async Task<bool> DisconnectAsync()
        {
            if (!IsConnected)
            {
                InfoLog("设备未连接");
                return true;
            }

            ConnectionStatus = DeviceConnectionStatus.Disconnecting;
            var mode = GetDeviceCommunicationMode();
            InfoLog($"开始断开设备连接 ({GetConnectionModeDescription(mode)})");

            try
            {
                bool result;

                switch (mode)
                {
                    case CommunicationMode.PLC:
                    case CommunicationMode.ModbusTcp:
                        if (ConnectionManager != null && !string.IsNullOrEmpty(ComId))
                        {
                            result = await ConnectionManager.DisconnectAsync(ComId);
                        }
                        else
                        {
                            result = IsSimulationMode
                                ? await OnDisconnectSimulationAsync()
                                : await OnDisconnectAsync();
                        }
                        break;

                    case CommunicationMode.ZLanGateway:
                        result = await OnDisconnectAsync();
                        break;

                    case CommunicationMode.Direct:
                    default:
                        result = IsSimulationMode
                            ? await OnDisconnectSimulationAsync()
                            : await OnDisconnectAsync();
                        break;
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

        public virtual async Task<bool> CheckConnectionAsync()
        {
            try
            {
                if (!IsConnected)
                    return false;

                var mode = GetDeviceCommunicationMode();
                bool isAlive;

                switch (mode)
                {
                    case CommunicationMode.PLC:
                    case CommunicationMode.ModbusTcp:
                        if (ConnectionManager != null && !string.IsNullOrEmpty(ComId))
                        {
                            isAlive = await ConnectionManager.CheckConnectionAsync(ComId);
                            InfoLog($"通过 {mode} 连接管理器检查: {isAlive}");
                        }
                        else
                        {
                            WarnLog($"通信方式为 {mode} 但 ConnectionManager 或 ComId 未配置,fallback 直连检查");
                            isAlive = IsSimulationMode
                                ? await OnCheckConnectionSimulationAsync()
                                : await OnCheckConnectionAsync();
                        }
                        break;

                    case CommunicationMode.ZLanGateway:
                        isAlive = await OnCheckConnectionViaGatewayAsync();
                        break;

                    case CommunicationMode.RemoteServer:
                        // 心跳:隧道断 → 必然够不到设备;隧道通 → 再确认本设备 DTU 仍在线
                        // (DTU 掉线时设备应转为未连接,以触发安全 HOLD/告警)
                        if (!RemoteGatewayClient.Instance.IsConnected)
                        {
                            isAlive = false;
                        }
                        else
                        {
                            var hbSerial = Parameters?.GetValue<string>("DTU序列号");
                            isAlive = !string.IsNullOrWhiteSpace(hbSerial)
                                && await RemoteGatewayClient.Instance.IsSerialOnlineAsync(hbSerial);
                        }
                        break;

                    case CommunicationMode.Direct:
                    default:
                        isAlive = IsSimulationMode
                            ? await OnCheckConnectionSimulationAsync()
                            : await OnCheckConnectionAsync();
                        break;
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

        protected virtual void OnSimulationModeChanged(bool isSimulationMode)
        {
            InfoLog($"设备模拟模式切换: {(isSimulationMode ? "模拟模式" : "实际模式")}");

            if (IsConnected)
            {
                InfoLog("检测到设备已连接,将重新连接以应用新模式");
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await DisconnectAsync();
                        await Task.Delay(500);
                        await ConnectAsync();
                    }
                    catch (Exception ex)
                    {
                        ErrorLog("切换模拟模式时重连设备失败", ex);
                    }
                });
            }
        }

        protected virtual async Task<bool> OnConnectSimulationAsync()
        {
            await Task.Delay(100 + new Random().Next(0, 200));
            InfoLog("模拟连接成功");
            return true;
        }

        protected virtual async Task<bool> OnDisconnectSimulationAsync()
        {
            await Task.Delay(50 + new Random().Next(0, 100));
            InfoLog("模拟断开连接成功");
            return true;
        }

        protected virtual async Task<bool> OnCheckConnectionSimulationAsync()
        {
            await Task.Delay(10);
            return new Random().NextDouble() > 0.05;
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
            if (!IsConnected) return;

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

            await CheckConnectionAsync();
        }

        #endregion

        #region 子类实现的虚方法

        /// <summary>
        /// 具体的连接实现 (子类重写) — Direct 模式下被调用。
        /// </summary>
        protected virtual Task<bool> OnConnectAsync()
        {
            return Task.FromResult(true);
        }

        /// <summary>
        /// 通过 ZLAN 网关连接 (子类重写) — ZLanGateway 模式下被调用。
        /// 默认实现:复用 OnConnectAsync。子类应 override 以提供协议探测逻辑。
        /// </summary>
        protected virtual Task<bool> OnConnectViaGatewayAsync()
        {
            return OnConnectAsync();
        }

        /// <summary>
        /// 具体的断开连接实现 (子类重写)。
        /// </summary>
        protected virtual Task<bool> OnDisconnectAsync()
        {
            return Task.FromResult(true);
        }

        /// <summary>
        /// 具体的连接检查实现 (子类重写) — Direct 模式下被调用。
        /// </summary>
        protected virtual Task<bool> OnCheckConnectionAsync()
        {
            return Task.FromResult(true);
        }

        /// <summary>
        /// 通过 ZLAN 网关检查连接 (子类重写) — ZLanGateway 模式下被调用。
        /// 默认实现:复用 OnConnectViaGatewayAsync。
        /// </summary>
        protected virtual Task<bool> OnCheckConnectionViaGatewayAsync()
        {
            return OnConnectViaGatewayAsync();
        }

        /// <summary>
        /// 创建该设备的字节流通信通道。
        /// 框架层根据该设备实例的"通信方式"参数和绑定关系,
        /// 自动返回 SerialPortTransport 或 TcpClientTransport。
        /// 调用方负责 Dispose 返回的 Transport (推荐 using)。
        /// </summary>
        /// <param name="serialConfig">Direct 模式时使用的串口配置</param>
        protected async Task<IDeviceTransport> CreateTransportAsync(SerialPortConfig serialConfig)
        {
            var mode = GetDeviceCommunicationMode();

            // 集中化:自建云服务器(RemoteServer)模式 —— 所有字节级驱动统一经此走云透传，
            // 无需各驱动自行处理。设备只需配置"通信方式=RemoteServer"+"DTU序列号"。
            if (mode == CommunicationMode.RemoteServer)
            {
                var serialNo = Parameters?.GetValue<string>("DTU序列号");
                if (string.IsNullOrWhiteSpace(serialNo))
                {
                    throw new InvalidOperationException("RemoteServer 模式下必须配置「DTU序列号」(=DTU 的登录包)");
                }
                return new RemoteGatewayTransport(serialNo);
            }

            var factory = DeviceServices.TransportFactory
                ?? throw new InvalidOperationException(
                    "IDeviceTransportFactory 未注册。请确保 Shell 在启动时设置 DeviceServices.TransportFactory。");

            var deviceInstanceId = Parameters?.GetValue<string>("DeviceId") ?? DeviceId;

            return await factory.CreateTransportAsync(
                this.GetType().Name,
                deviceInstanceId,
                serialConfig,
                mode);
        }

        #endregion

        #region 驱动级自动采集

        // 设备连接后由基类自动启动的后台采集循环。驱动只需通过
        // CollectableParameters 声明「可监控参数白名单」(键名/显示名/单位/取数命令)，
        // 定时、取数、过滤、发布、停止全部由基类负责；只有声明过的参数会被
        // 抽取并经 DeviceSampleHub 上抛，命令输出里的其余杂项在此拦下。

        /// <summary>自动采集样本的统一命令键:监控列表、图表数据键、入库 command_name 都用它。</summary>
        public const string AutoCollectCommandKey = "自动采集";

        private CancellationTokenSource? _autoCollectCts;
        private Task? _autoCollectTask;
        private readonly object _autoCollectLock = new object();

        // 流程运行保持位:流程运行期间即使设备未走「连接」流程也保持采集
        // (模拟/真实分支由驱动读命令自身处理),流程结束且未连接时停采
        private volatile bool _flowCollectHold;

        /// <summary>
        /// 本设备的可监控参数白名单。声明了的参数才出现在监控界面并被自动采集;
        /// 返回空列表表示本驱动不参与自动采集。
        /// </summary>
        public virtual IReadOnlyList<CollectableParameter> CollectableParameters => Array.Empty<CollectableParameter>();

        /// <summary>
        /// 自动采集允许开关。只有画布创建/恢复流程盖章过的设备实例才置 true；
        /// 插件目录实例、网关探测/试连等临时连接不盖章，避免在共享实例上
        /// 发起多余 IO 或发布无画布 ID 的脏样本。
        /// </summary>
        public bool AutoCollectAllowed { get; set; }

        /// <summary>相邻两轮采集之间的间隔（毫秒）。子类可按通信负载调整。</summary>
        public virtual int AutoCollectIntervalMs => 1000;

        /// <summary>
        /// 流程开始:强制启动自动采集并保持到流程结束(设备未连接也采,
        /// 保证「开启流程就有数据」;需先由上层盖章 AutoCollectAllowed)。
        /// </summary>
        public void NotifyFlowCollectStart()
        {
            _flowCollectHold = true;
            StartAutoCollect();
        }

        /// <summary>流程结束:解除保持;设备未连接则停采,已连接则继续。</summary>
        public void NotifyFlowCollectStop()
        {
            _flowCollectHold = false;
            if (!IsConnected) StopAutoCollect();
        }

        private void StartAutoCollect()
        {
            try
            {
                if (!AutoCollectAllowed) return;
                if (CollectableParameters == null || CollectableParameters.Count == 0) return;

                lock (_autoCollectLock)
                {
                    if (_autoCollectTask != null && !_autoCollectTask.IsCompleted) return;

                    _autoCollectCts?.Dispose();
                    _autoCollectCts = new CancellationTokenSource();
                    var token = _autoCollectCts.Token;
                    _autoCollectTask = Task.Run(() => AutoCollectLoopAsync(token), token);
                }
                InfoLog($"自动采集已启动: {string.Join(", ", CollectableParameters.Select(p => p.DisplayName))}");
            }
            catch (Exception ex)
            {
                ErrorLog("自动采集启动失败", ex);
            }
        }

        private void StopAutoCollect()
        {
            try
            {
                lock (_autoCollectLock)
                {
                    if (_autoCollectCts == null) return;
                    _autoCollectCts.Cancel();
                    _autoCollectCts.Dispose();
                    _autoCollectCts = null;
                    _autoCollectTask = null;
                }
                InfoLog("自动采集已停止");
            }
            catch (Exception ex)
            {
                ErrorLog("自动采集停止异常", ex);
            }
        }

        private async Task AutoCollectLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested && (IsConnected || _flowCollectHold))
            {
                var declared = CollectableParameters;
                var collected = new List<ParameterBase>();

                // 按声明去重后的来源命令逐条取数,只抽取声明过的参数
                foreach (var commandName in declared.Select(p => p.SourceCommand).Distinct())
                {
                    if (token.IsCancellationRequested || (!IsConnected && !_flowCollectHold)) break;

                    try
                    {
                        var cmd = Commands?.FirstOrDefault(c => c.Name == commandName);
                        if (cmd == null)
                        {
                            WarnLog($"自动采集来源命令不存在: {commandName}");
                            continue;
                        }

                        var result = await cmd.ExecuteAsync(new DeviceParameters()).ConfigureAwait(false);
                        if (result?.Success != true || result.OutputParameters?.Variables == null) continue;

                        foreach (var cp in declared)
                        {
                            if (cp.SourceCommand != commandName) continue;

                            var src = result.OutputParameters.Variables.FirstOrDefault(v => v.Name == cp.SourceParam);
                            if (src?.Value == null || !TryConvertToDouble(src.Value, out var value)) continue;

                            collected.Add(new NumberParameter(cp.Name, -1e12, 1e12, value, cp.DisplayName, true, cp.Unit));
                        }
                    }
                    catch (Exception ex)
                    {
                        // 单条命令失败不终止循环（链路抖动下一轮重试）
                        DebugLog($"自动采集执行失败: {commandName} - {ex.Message}");
                    }
                }

                // 命令执行期间可能已断开/取消(如探测式短连接),不发布过期样本
                if (!token.IsCancellationRequested && (IsConnected || _flowCollectHold) && collected.Count > 0)
                {
                    DeviceSampleHub.Publish(new DeviceSampleEventArgs
                    {
                        DeviceId = this.DeviceId,
                        DeviceName = this.Name,
                        CommandName = AutoCollectCommandKey,
                        Output = new DeviceParameters
                        {
                            Variables = new System.Collections.ObjectModel.ObservableCollection<ParameterBase>(collected)
                        },
                        Timestamp = DateTime.Now
                    });
                }

                try
                {
                    await Task.Delay(Math.Max(200, AutoCollectIntervalMs), token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        private static bool TryConvertToDouble(object value, out double result)
        {
            result = 0;
            switch (value)
            {
                case double d: result = d; return double.IsFinite(d);
                case float f: result = f; return float.IsFinite(f);
                case int i: result = i; return true;
                case long l: result = l; return true;
                case short s: result = s; return true;
                case byte b: result = b; return true;
                case decimal m: result = (double)m; return true;
                case bool bl: result = bl ? 1.0 : 0.0; return true;
                default:
                    return double.TryParse(value.ToString(), System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out result) && double.IsFinite(result);
            }
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
                pauseTasks.Add(PauseStateAsync(state));

            try
            {
                var results = await Task.WhenAll(pauseTasks);
                bool allSuccess = results.All(r => r);

                if (allSuccess) InfoLog("所有活跃状态已暂停");
                else WarnLog("部分活跃状态暂停失败");

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
                resumeTasks.Add(ResumeStateAsync(state));

            try
            {
                var results = await Task.WhenAll(resumeTasks);
                bool allSuccess = results.All(r => r);

                if (allSuccess) InfoLog("所有暂停状态已恢复");
                else WarnLog("部分暂停状态恢复失败");

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
                stopTasks.Add(StopStateAsync(state));

            try
            {
                var results = await Task.WhenAll(stopTasks);
                bool allSuccess = results.All(r => r);

                lock (_statesLock)
                {
                    _activeStates.Clear();
                    _pausedStates.Clear();
                }

                if (allSuccess) InfoLog("所有状态已停止");
                else WarnLog("部分状态停止失败");

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

        protected void AddActiveState(string stateName, string activateCommand, string deactivateCommand,
            string pauseCommand = null, string resumeCommand = null, object stateData = null)
        {
            lock (_statesLock)
            {
                var existing = _activeStates.FirstOrDefault(s => s.StateName == stateName);
                if (existing != null)
                {
                    WarnLog($"状态已存在,更新时间: {stateName}");
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

        private async Task<bool> PauseStateAsync(DeviceActiveState state)
        {
            try
            {
                InfoLog($"暂停状态: {state.StateName}");

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

        private async Task<bool> ResumeStateAsync(DeviceActiveState state)
        {
            try
            {
                InfoLog($"恢复状态: {state.StateName}");

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

                lock (_statesLock)
                {
                    _pausedStates.Remove(state);
                    state.IsPaused = false;
                    state.ActivatedTime = DateTime.Now;
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

        protected void TraceLog(string message) =>
            _logger.LogTrace("[{DeviceName}]: {Message}", Name, message);

        protected void DebugLog(string message) =>
            _logger.LogDebug("[{DeviceName}]: {Message}", Name, message);

        protected void InfoLog(string message) =>
            _logger.LogInformation("[{DeviceName}]: {Message}", Name, message);

        protected void WarnLog(string message) =>
            _logger.LogWarning("[{DeviceName}]: {Message}", Name, message);

        protected void ErrorLog(string message, Exception? exception = null)
        {
            if (exception != null)
                _logger.LogError(exception, "[{DeviceName}]: {Message}", Name, message);
            else
                _logger.LogError("[{DeviceName}]: {Message}", Name, message);
        }

        #endregion

        #region UI 命令 / 状态 / 事件

        public virtual async Task<CommandExecutionResult> ExecuteUICommandAsync(
            string commandName,
            Dictionary<string, object> parameters = null)
        {
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
                    OnCommandStateChanged(commandName, CommandExecutionState.Running);
                    onStartStateSync?.Invoke();

                    var output = innerAsync(parameters).GetAwaiter().GetResult();
                    sw.Stop();

                    var success = output?.GetValue<bool>("Success") ?? false;

                    if (success)
                    {
                        onSuccessStateSync?.Invoke(output);
                        OnCommandStateChanged(commandName, CommandExecutionState.Succeeded, output);
                    }
                    else
                    {
                        onFailureStateSync?.Invoke(output);
                        OnCommandStateChanged(commandName, CommandExecutionState.Failed, output);
                    }

                    return output;
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    onErrorStateSync?.Invoke(ex.Message);
                    OnCommandStateChanged(commandName, CommandExecutionState.Error, errorMessage: ex.Message);
                    throw new DeviceCommandExecutionException(Name, commandName, ex.Message, ex);
                }
            };
        }

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
                    eventArgs.AdditionalData["OutputParameters"] = output;
                if (!string.IsNullOrEmpty(errorMessage))
                    eventArgs.AdditionalData["ErrorMessage"] = errorMessage;

                ControlStateChanged?.Invoke(this, eventArgs);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"触发命令状态事件失败: {ex.Message}");
            }
        }

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
                    StopAutoCollect();
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
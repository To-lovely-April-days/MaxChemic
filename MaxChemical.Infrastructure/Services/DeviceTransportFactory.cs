using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MaxChemical.Core;
using MaxChemical.Data.Repositories;
using MaxChemical.Logging;

namespace MaxChemical.Infrastructure.Services
{
    /// <summary>
    /// IDeviceTransport 工厂 — 根据"通信方式"+ 绑定表决定走 TCP 还是串口。
    ///
    /// 路由规则:
    ///   - ZLanGateway:查绑定表 → TcpClientTransport (没绑定就抛异常)
    ///   - Direct:    直接 SerialPortTransport (用传入的 serialConfig)
    ///   - PLC / ModbusTcp:这种场景不应走 Transport,应走 ConnectionManager,抛异常
    ///
    /// 启动时一次性把所有绑定加载到内存路由表,避免每次 IO 都查数据库。
    /// 绑定有变化时主动 InvalidateCache,下次创建会重新查表。
    /// </summary>
    public class DeviceTransportFactory : IDeviceTransportFactory
    {
        private readonly IGatewayBindingRepository _repository;
        private readonly ILogService _logger;
        private readonly object _cacheLock = new object();

        // (deviceTypeName, deviceInstanceId) → 绑定信息
        private Dictionary<string, (string Ip, int Port)> _routeCache;

        public DeviceTransportFactory(IGatewayBindingRepository repository, ILogService logger)
        {
            _repository = repository ?? throw new ArgumentNullException(nameof(repository));
            _logger = logger?.ForContext<DeviceTransportFactory>();
        }

        public async Task<IDeviceTransport> CreateTransportAsync(
            string deviceTypeName,
            string deviceInstanceId,
            SerialPortConfig serialConfig,
            CommunicationMode mode)
        {
            switch (mode)
            {
                case CommunicationMode.ZLanGateway:
                    return await CreateGatewayTransportAsync(deviceTypeName, deviceInstanceId);

                case CommunicationMode.Direct:
                    return CreateDirectTransport(deviceTypeName, deviceInstanceId, serialConfig);

                case CommunicationMode.PLC:
                case CommunicationMode.ModbusTcp:
                    throw new InvalidOperationException(
                        $"通信方式 {mode} 不应通过 IDeviceTransport 通信。" +
                        $"PLC / ModbusTcp 模式请通过 ConnectionManager 收发数据。" +
                        $"设备: {deviceTypeName}/{deviceInstanceId}");

                default:
                    throw new InvalidOperationException(
                        $"未知通信方式: {mode}。设备: {deviceTypeName}/{deviceInstanceId}");
            }
        }

        /// <summary>
        /// 网关模式:查绑定表 → TcpClientTransport
        /// </summary>
        private async Task<IDeviceTransport> CreateGatewayTransportAsync(
            string deviceTypeName, string deviceInstanceId)
        {
            await EnsureCacheAsync().ConfigureAwait(false);

            var key = MakeKey(deviceTypeName, deviceInstanceId);
            (string Ip, int Port)? route = null;

            lock (_cacheLock)
            {
                if (_routeCache != null && _routeCache.TryGetValue(key, out var r))
                    route = r;
            }

            if (!route.HasValue)
            {
                throw new InvalidOperationException(
                    $"设备 {deviceTypeName}/{deviceInstanceId} 的通信方式为 ZLanGateway,但未绑定到任何网关通道。\n" +
                    $"请打开网关配置工具,把该设备绑定到一个通道,然后再试。");
            }

            _logger?.LogDebug("Transport 路由 → 网关 {Ip}:{Port}  设备 {Type}/{Id}",
                route.Value.Ip, route.Value.Port, deviceTypeName, deviceInstanceId);
            return new TcpClientTransport(route.Value.Ip, route.Value.Port, _logger);
        }

        /// <summary>
        /// 直连模式:直接用串口
        /// </summary>
        private IDeviceTransport CreateDirectTransport(
            string deviceTypeName, string deviceInstanceId, SerialPortConfig serialConfig)
        {
            if (serialConfig == null || string.IsNullOrEmpty(serialConfig.PortName))
            {
                throw new InvalidOperationException(
                    $"设备 {deviceTypeName}/{deviceInstanceId} 的通信方式为 Direct,但未提供串口配置 (PortName 为空)。");
            }

            _logger?.LogDebug("Transport 路由 → 串口 {Com}  设备 {Type}/{Id}",
                serialConfig.PortName, deviceTypeName, deviceInstanceId);
            return new SerialPortTransport(serialConfig);
        }

        /// <summary>
        /// 绑定变更后由网关配置 UI 调用,使下次创建 Transport 时重新查表。
        /// </summary>
        public void InvalidateCache()
        {
            lock (_cacheLock) { _routeCache = null; }
            _logger?.LogDebug("DeviceTransportFactory 路由缓存已失效");
        }

        private async Task EnsureCacheAsync()
        {
            lock (_cacheLock)
            {
                if (_routeCache != null) return;
            }

            try
            {
                var bindings = await _repository.GetAllAsync().ConfigureAwait(false);
                var map = new Dictionary<string, (string, int)>(StringComparer.OrdinalIgnoreCase);

                foreach (var b in bindings)
                {
                    var key = MakeKey(b.DeviceTypeName, b.DeviceInstanceId);
                    map[key] = (b.GatewayIp, b.ChannelLocalPort);
                }

                lock (_cacheLock)
                {
                    _routeCache = map;
                }

                _logger?.LogInformation("加载网关绑定路由表完成,共 {N} 条", map.Count);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "加载网关绑定路由表失败");
                lock (_cacheLock)
                {
                    _routeCache = new Dictionary<string, (string, int)>();
                }
            }
        }

        private static string MakeKey(string type, string id) => $"{type}|{id}";
    }
}
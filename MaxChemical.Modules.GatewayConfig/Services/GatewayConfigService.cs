using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using MaxChemical.Modules.GatewayConfig.Ipc;
using MaxChemical.Modules.GatewayConfig.Models;

namespace MaxChemical.Modules.GatewayConfig.Services
{
    public interface IGatewayConfigService
    {
        Task InitializeAsync();
        Task<IReadOnlyList<GatewayModel>> SearchAsync(int timeoutMs = 3000);
        Task RefreshChannelAsync(ChannelModel channel);
        Task<bool> ApplyChannelAsync(ChannelModel channel, ChannelModel original, Action<int> onPollAttempt = null);
        Task<bool> ResetChannelToDefaultAsync(ChannelModel channel, Action<int> onPollAttempt = null);
        Task ShutdownAsync();
    }

    public class GatewayConfigService : IGatewayConfigService, IDisposable
    {
        private readonly string _proxyExePath;
        private GatewayProxyClient _proxy;
        private bool _initialized;
        private readonly object _lifecycleLock = new object();

        public GatewayConfigService()
        {
            _proxyExePath = ResolveProxyExePath();
            _proxy = new GatewayProxyClient(_proxyExePath);
        }

        public async Task InitializeAsync()
        {
            lock (_lifecycleLock)
            {
                if (_initialized && _proxy != null && _proxy.IsConnected)
                    return;

                if (_proxy != null)
                {
                    try { _proxy.Dispose(); } catch { }
                }
                _proxy = new GatewayProxyClient(_proxyExePath);
                _initialized = false;
            }

            await _proxy.ConnectAsync().ConfigureAwait(false);
            _initialized = true;
        }

        public async Task<IReadOnlyList<GatewayModel>> SearchAsync(int timeoutMs = 3000)
        {
            await EnsureInitializedAsync().ConfigureAwait(false);

            var resp = await _proxy.SendRequestAsync(IpcProtocol.Op.SearchDevices,
                new Dictionary<string, object> { ["timeoutMs"] = timeoutMs }).ConfigureAwait(false);

            if (!resp.Success)
                throw new InvalidOperationException("搜索失败: " + resp.ErrorMessage);

            var snapshots = ParseDeviceList(resp.Payload, "devices");
            return AggregateToGateways(snapshots);
        }

        public async Task RefreshChannelAsync(ChannelModel channel)
        {
            await EnsureInitializedAsync().ConfigureAwait(false);
            var resp = await _proxy.SendRequestAsync(IpcProtocol.Op.ReadParams,
                new Dictionary<string, object> { ["deviceId"] = channel.DeviceId }).ConfigureAwait(false);
            if (!resp.Success || !resp.Payload.TryGetValue("device", out var raw)) return;
            var snap = ParseSnapshot(raw);
            if (snap != null) ApplySnapshotToChannel(channel, snap);
        }

        public async Task<bool> ApplyChannelAsync(ChannelModel channel, ChannelModel original,
            Action<int> onPollAttempt = null)
        {
            await EnsureInitializedAsync().ConfigureAwait(false);

            var changes = DiffChannel(channel, original);
            if (changes.Count == 0) return true;

            var resp = await _proxy.SendRequestAsync(IpcProtocol.Op.WriteParams,
                new Dictionary<string, object>
                {
                    ["deviceId"] = channel.DeviceId,
                    ["changes"] = changes,
                }).ConfigureAwait(false);

            if (!resp.Success)
                throw new InvalidOperationException("写入失败: " + resp.ErrorMessage);

            return await PollAsync(channel.DeviceId, onPollAttempt).ConfigureAwait(false);
        }

        /// <summary>
        /// 把单台设备恢复出厂(仅作用于本通道对应的那台物理设备,不影响其他通道)。
        /// </summary>
        public async Task<bool> ResetChannelToDefaultAsync(ChannelModel channel, Action<int> onPollAttempt = null)
        {
            await EnsureInitializedAsync().ConfigureAwait(false);
            if (channel == null || string.IsNullOrEmpty(channel.DeviceId)) return false;

            var resp = await _proxy.SendRequestAsync(IpcProtocol.Op.SetDefault,
                new Dictionary<string, object> { ["deviceId"] = channel.DeviceId }).ConfigureAwait(false);
            if (!resp.Success)
                throw new InvalidOperationException("恢复出厂失败: " + resp.ErrorMessage);

            return await PollAsync(channel.DeviceId, onPollAttempt).ConfigureAwait(false);
        }

        public async Task ShutdownAsync()
        {
            GatewayProxyClient toDispose = null;
            lock (_lifecycleLock)
            {
                if (!_initialized || _proxy == null) return;
                toDispose = _proxy;
                _proxy = null;
                _initialized = false;
            }

            try
            {
                await toDispose.ShutdownAsync().ConfigureAwait(false);
            }
            catch { /* swallow */ }
            finally
            {
                try { toDispose.Dispose(); } catch { }
            }
        }

        public void Dispose()
        {
            try { ShutdownAsync().GetAwaiter().GetResult(); } catch { }
        }

        // ═══════════ helpers ═══════════

        private async Task EnsureInitializedAsync()
        {
            if (!_initialized || _proxy == null || !_proxy.IsConnected)
                await InitializeAsync().ConfigureAwait(false);
        }

        private async Task<bool> PollAsync(string deviceId, Action<int> onAttempt)
        {
            void OnNotify(IpcNotification n)
            {
                if (n.Op != IpcProtocol.NotifyOp.PollProgress) return;
                if (!n.Payload.TryGetValue("attempt", out var raw)) return;
                if (raw is JsonElement je && je.ValueKind == JsonValueKind.Number)
                    onAttempt?.Invoke(je.GetInt32());
            }
            _proxy.NotificationReceived += OnNotify;
            try
            {
                var resp = await _proxy.SendRequestAsync(IpcProtocol.Op.PollDevice,
                    new Dictionary<string, object>
                    {
                        ["deviceId"] = deviceId,
                        ["intervalMs"] = 1500,
                        ["timeoutMs"] = 30000,
                    }, timeoutMs: 35000).ConfigureAwait(false);
                if (!resp.Success) return false;
                if (resp.Payload.TryGetValue("found", out var f) && f is JsonElement je)
                    return je.GetBoolean();
                return false;
            }
            finally
            {
                _proxy.NotificationReceived -= OnNotify;
            }
        }

        // ─── 聚合 ───
        private static IReadOnlyList<GatewayModel> AggregateToGateways(IList<DeviceSnapshot> snaps)
        {
            var groups = snaps.GroupBy(GroupKey).ToList();

            // 网关设备集合
            var gateways = new List<GatewayModel>();

            foreach (var g in groups)
            {
               // 先按照网关设备名称排序，在根据网关硬件版本分组
                var query = g.OrderBy(snap => snap.DeviceName).GroupBy(snap => snap.FirmwareVersion);

                foreach (var items in query)
                {
                    // 将第一个添加为网关设备
                    DeviceSnapshot first = items.FirstOrDefault();
                    GatewayModel gateway = new GatewayModel()
                    {
                        DisplayName = string.IsNullOrEmpty(first.DeviceName) ? $"GW-{first.Ip}" : first.DeviceName,
                        Mac = FormatMacFromDeviceId(first.DeviceId),
                        Ip = first.Ip,
                        FirmwareVersion = first.FirmwareVersion,
                        IsOnline = true,
                    };

                    // 向网关设备添加通道（实际为网关端口）
                    int i = 0;
                    foreach (var item in items)
                    {
                        var ch = new ChannelModel()
                        {
                            Index = i++,
                            IsAvailable = item != null
                        };

                        if (item != null) ApplySnapshotToChannel(ch, item);

                        gateway.Channels.Add(ch);
                    }
                    gateways.Add(gateway);
                }
            }
            return gateways;
        }

        private static string GroupKey(DeviceSnapshot s)
        {
            var ip = s.Ip ?? "";
            var lastDot = ip.LastIndexOf('.');
            if (lastDot > 0) return ip.Substring(0, lastDot);
            var id = s.DeviceId ?? "";
            return id.Length >= 6 ? id.Substring(0, 6) : id;
        }

        private static string FormatMacFromDeviceId(string deviceId)
        {
            if (string.IsNullOrEmpty(deviceId) || deviceId.Length < 12) return deviceId ?? "";
            return string.Join(":",
                deviceId.Substring(0, 2),
                deviceId.Substring(2, 2),
                deviceId.Substring(4, 2),
                deviceId.Substring(6, 2),
                deviceId.Substring(8, 2),
                deviceId.Substring(10, 2)).ToUpperInvariant();
        }

        private static void ApplySnapshotToChannel(ChannelModel ch, DeviceSnapshot s)
        {
            ch.DeviceId = s.DeviceId;
            // 设备身份
            ch.DeviceName = s.DeviceName ?? "";
            ch.Mac = FormatMacFromDeviceId(s.DeviceId);
            ch.FirmwareVersion = s.FirmwareVersion ?? "";
            // 网络参数
            ch.Ip = s.Ip ?? "";
            ch.Netmask = s.Netmask ?? "";
            ch.GatewayAddr = s.Gateway ?? "";
            ch.DnsServer = s.DnsServer ?? "";
            ch.WebPort = s.WebPort;
            // 串口参数
            ch.BaudRate = ParseEnum<BaudRate>(s.BaudRate, BaudRate.Bps_9600);
            ch.DataBits = ParseEnum<DataBits>(s.DataBits, DataBits.Bits_8);
            ch.Parity = ParseEnum<Parity>(s.Parity, Parity.None);
            ch.StopBit = ParseEnum<StopBit>(s.StopBit, StopBit.Stop_1);
            ch.FlowControl = ParseEnum<FlowControl>(s.FlowControl, FlowControl.None);
            // TCP/UDP
            ch.WorkMode = ParseEnum<WorkMode>(s.WorkMode, WorkMode.TcpServer);
            ch.LocalPort = s.LocalPort;
            ch.DestPort = s.DestPort;
            ch.DestIp = s.DestIp ?? "";
            ch.ReconnectTime = s.ReconnectTime;
            ch.KeepAliveTime = s.KeepAliveTime;
            ch.LinkStatus = (LinkStatus)s.LinkStatus;
        }

        // ─── Diff ───
        private static Dictionary<string, object> DiffChannel(ChannelModel cur, ChannelModel orig)
        {
            var d = new Dictionary<string, object>();
            // 设备身份
            if (cur.DeviceName != orig.DeviceName) d["PARAM_DEV_NAME"] = cur.DeviceName ?? "";
            // 网络
            if (cur.IpMode != orig.IpMode) d["PARAM_IP_MODE"] = (int)cur.IpMode;
            if (cur.Netmask != orig.Netmask) d["PARAM_NET_MASK"] = cur.Netmask ?? "";
            if (cur.GatewayAddr != orig.GatewayAddr) d["PARAM_GATEWAY"] = cur.GatewayAddr ?? "";
            if (cur.DnsServer != orig.DnsServer) d["PARAM_DNS_SERVER_IP"] = cur.DnsServer ?? "";
            if (cur.WebPort != orig.WebPort) d["PARAM_WEB_PORT"] = (int)cur.WebPort;
            // 串口
            if (cur.BaudRate != orig.BaudRate) d["PARAM_BAUNDRATE"] = (int)cur.BaudRate;
            if (cur.DataBits != orig.DataBits) d["PARAM_DATA_BITS"] = (int)cur.DataBits;
            if (cur.Parity != orig.Parity) d["PARAM_PARITY"] = (int)cur.Parity;
            if (cur.StopBit != orig.StopBit) d["PARAM_STOP_BIT"] = (int)cur.StopBit;
            if (cur.FlowControl != orig.FlowControl) d["PARAM_FLOW_CONTROL"] = (int)cur.FlowControl;
            // TCP/UDP
            if (cur.WorkMode != orig.WorkMode) d["PARAM_WORK_MODE"] = (int)cur.WorkMode;
            if (cur.LocalPort != orig.LocalPort) d["PARAM_DEV_LOCAL_PORT"] = (int)cur.LocalPort;
            if (cur.DestPort != orig.DestPort) d["PARAM_DEST_PORT"] = (int)cur.DestPort;
            if (cur.DestIp != orig.DestIp) d["PARAM_DEST_IP"] = cur.DestIp ?? "";
            if (cur.ReconnectTime != orig.ReconnectTime) d["PARAM_RECONNECT_TIME"] = cur.ReconnectTime;
            if (cur.KeepAliveTime != orig.KeepAliveTime) d["PARAM_KEEP_ALIVE_TIME"] = cur.KeepAliveTime;
            return d;
        }

        // ─── JSON 解析 ───
        private static IList<DeviceSnapshot> ParseDeviceList(Dictionary<string, object> payload, string key)
        {
            var list = new List<DeviceSnapshot>();
            if (!payload.TryGetValue(key, out var raw)) return list;
            var je = (JsonElement)raw;
            if (je.ValueKind != JsonValueKind.Array) return list;
            foreach (var item in je.EnumerateArray())
            {
                var snap = ParseSnapshot(item);
                if (snap != null) list.Add(snap);
            }
            return list;
        }

        private static DeviceSnapshot ParseSnapshot(object raw)
        {
            if (raw is JsonElement je)
                return JsonSerializer.Deserialize<DeviceSnapshot>(je.GetRawText(),
                    new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            return null;
        }

        private static T ParseEnum<T>(int value, T fallback) where T : struct
        {
            return Enum.IsDefined(typeof(T), value) ? (T)Enum.ToObject(typeof(T), value) : fallback;
        }

        private static string ResolveProxyExePath()
        {
            var baseDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? "";
            return Path.Combine(baseDir, "GatewayConfigProxy", "MaxChemical.GatewayConfig.Proxy.exe");
        }
    }
}
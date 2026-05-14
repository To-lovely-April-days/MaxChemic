using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MaxChemical.GatewayConfig.Proxy.Native;

namespace MaxChemical.GatewayConfig.Proxy
{
    /// <summary>
    /// 代理进程的业务执行器。
    /// SDK 非线程安全,所有调用通过 SemaphoreSlim 串行化。
    /// </summary>
    internal class SdkExecutor : IDisposable
    {
        private readonly SemaphoreSlim _sdkLock = new SemaphoreSlim(1, 1);
        private bool _initialized;

        public SdkExecutor()
        {
            // ZLDM_Init(0) = 仅做局域网搜索,不监听外网端口
            // 返回 1 = 成功, 0 = 失败 (Win32 BOOL)
            var rc = ZLDevManageNative.ZLDM_Init(0);
            _initialized = rc != 0;
            if (!_initialized)
                throw new InvalidOperationException($"ZLDM_Init failed: rc={rc}");
        }

        public void Dispose()
        {
            if (_initialized)
            {
                try { ZLDevManageNative.ZLDM_Exit(); } catch { /* swallow */ }
                _initialized = false;
            }
            _sdkLock.Dispose();
        }

        // ─── 设备搜索 ───
        public async Task<List<Modules.GatewayConfig.Ipc.DeviceSnapshot>> SearchDevicesAsync(
            int timeoutMs, Action<int> onProgress, CancellationToken ct)
        {
            _ = timeoutMs; // SDK 内部已阻塞约 500ms,不需要外层轮询

            await _sdkLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                // StartSearchDev 直接返回设备数
                var count = ZLDevManageNative.ZLDM_StartSearchDev();
                onProgress?.Invoke(count);

                var list = new List<Modules.GatewayConfig.Ipc.DeviceSnapshot>();
                for (int i = 0; i < count; i++)
                {
                    var devId = ZLDevManageNative.PtrToString(ZLDevManageNative.ZLDM_GetDevID(i));
                    if (string.IsNullOrEmpty(devId)) continue;
                    var snapshot = ReadFullSnapshot(devId);
                    if (snapshot != null) list.Add(snapshot);
                }
                return list;
            }
            finally
            {
                _sdkLock.Release();
            }
        }

        // ─── 单设备参数读取 ───
        public async Task<Modules.GatewayConfig.Ipc.DeviceSnapshot> ReadParamsAsync(string deviceId, CancellationToken ct)
        {
            await _sdkLock.WaitAsync(ct).ConfigureAwait(false);
            try { return ReadFullSnapshot(deviceId); }
            finally { _sdkLock.Release(); }
        }

        // ─── 写入参数 + 触发重启 ───
        public async Task WriteParamsAsync(string deviceId, Dictionary<string, object> changes, CancellationToken ct)
        {
            await _sdkLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                foreach (var kv in changes)
                {
                    if (!TryParseParamId(kv.Key, out var paramId))
                        throw new ArgumentException($"未知参数 ID: {kv.Key}");
                    ApplyOneChange(deviceId, paramId, kv.Value);
                }
                var rc = ZLDevManageNative.ZLDM_SetDevParamExcute(deviceId);
                if (rc == 0)
                    throw new InvalidOperationException("ZLDM_SetDevParamExcute failed");
            }
            finally
            {
                _sdkLock.Release();
            }
        }

        // ─── 恢复出厂 (设备级:整台 ZLAN5443H 一起重置) ───
        public async Task SetDefaultAsync(string deviceId, CancellationToken ct)
        {
            await _sdkLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                // SDK 文档:写 PARAM_SET_TO_DEFAULT (值任意) + Excute
                ZLDevManageNative.ZLDM_SetDevParamInt(deviceId, 0, ZLDevManageNative.PARAM_SET_TO_DEFAULT);
                var rc = ZLDevManageNative.ZLDM_SetDevParamExcute(deviceId);
                if (rc == 0)
                    throw new InvalidOperationException("ZLDM_SetDevParamExcute (reset) failed");
            }
            finally
            {
                _sdkLock.Release();
            }
        }

        // ─── 设备重启后回归探测 ───
        public async Task<bool> PollDeviceAsync(string deviceId, int intervalMs, int timeoutMs,
            Action<int, bool> onProgress, CancellationToken ct)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            int attempt = 0;
            while (DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                attempt++;
                bool found = false;
                await _sdkLock.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    var count = ZLDevManageNative.ZLDM_StartSearchDev();
                    for (int i = 0; i < count; i++)
                    {
                        var id = ZLDevManageNative.PtrToString(ZLDevManageNative.ZLDM_GetDevID(i));
                        if (string.Equals(id, deviceId, StringComparison.OrdinalIgnoreCase))
                        {
                            found = true; break;
                        }
                    }
                }
                finally
                {
                    _sdkLock.Release();
                }
                onProgress?.Invoke(attempt, found);
                if (found) return true;
                await Task.Delay(intervalMs, ct).ConfigureAwait(false);
            }
            return false;
        }

        // ═══════════ private helpers ═══════════

        private Modules.GatewayConfig.Ipc.DeviceSnapshot ReadFullSnapshot(string deviceId)
        {
            try
            {
                var s = new Modules.GatewayConfig.Ipc.DeviceSnapshot { DeviceId = deviceId };
                // DeviceId 本身就是 MAC 的 Hex 字符串,Service 层会格式化为 AA:BB:CC:DD:EE:FF
                s.Mac              = deviceId;
                s.DeviceName       = GetStr(deviceId, ZLDevManageNative.PARAM_DEV_NAME);
                s.Ip               = GetStr(deviceId, ZLDevManageNative.PARAM_DEV_LOCAL_IP);
                s.LocalPort        = (ushort)GetInt(deviceId, ZLDevManageNative.PARAM_DEV_LOCAL_PORT);
                s.Netmask          = GetStr(deviceId, ZLDevManageNative.PARAM_NET_MASK);
                s.Gateway          = GetStr(deviceId, ZLDevManageNative.PARAM_GATEWAY);
                s.DnsServer        = GetStr(deviceId, ZLDevManageNative.PARAM_DNS_SERVER_IP);
                s.FirmwareVersion  = GetStr(deviceId, ZLDevManageNative.PARAM_DEV_VER);
                s.DestIp           = GetStr(deviceId, ZLDevManageNative.PARAM_DEST_IP);
                s.DestPort         = (ushort)GetInt(deviceId, ZLDevManageNative.PARAM_DEST_PORT);
                s.WebPort          = (ushort)GetInt(deviceId, ZLDevManageNative.PARAM_WEB_PORT);
                s.WorkMode         = GetInt(deviceId, ZLDevManageNative.PARAM_WORK_MODE);
                s.BaudRate         = GetInt(deviceId, ZLDevManageNative.PARAM_BAUNDRATE);
                s.DataBits         = GetInt(deviceId, ZLDevManageNative.PARAM_DATA_BITS);
                s.Parity           = GetInt(deviceId, ZLDevManageNative.PARAM_PARITY);
                s.StopBit          = GetInt(deviceId, ZLDevManageNative.PARAM_STOP_BIT);
                s.FlowControl      = GetInt(deviceId, ZLDevManageNative.PARAM_FLOW_CONTROL);
                s.ReconnectTime    = GetInt(deviceId, ZLDevManageNative.PARAM_RECONNECT_TIME);
                s.KeepAliveTime    = GetInt(deviceId, ZLDevManageNative.PARAM_KEEP_ALIVE_TIME);
                s.LinkStatus       = GetInt(deviceId, ZLDevManageNative.PARAM_LINK_STATUS);
                return s;
            }
            catch
            {
                return null;
            }
        }

        private static int GetInt(string deviceId, int paramId)
        {
            // 文档:-1 表示错误,其他是数据本身
            var v = ZLDevManageNative.ZLDM_GetDevParamInt(deviceId, paramId);
            return v == -1 ? 0 : v;
        }

        private static string GetStr(string deviceId, int paramId)
        {
            var ptr = ZLDevManageNative.ZLDM_GetDevParamString(deviceId, paramId);
            return ZLDevManageNative.PtrToString(ptr);
        }

        private static void ApplyOneChange(string deviceId, int paramId, object value)
        {
            if (value is string s)
                ZLDevManageNative.ZLDM_SetDevParamString(deviceId, s, paramId);
            else
                ZLDevManageNative.ZLDM_SetDevParamInt(deviceId, Convert.ToInt32(value), paramId);
        }

        private static bool TryParseParamId(string key, out int paramId)
        {
            if (int.TryParse(key, out paramId)) return true;
            var fi = typeof(ZLDevManageNative).GetField(key);
            if (fi != null && fi.IsLiteral && fi.FieldType == typeof(int))
            {
                paramId = (int)fi.GetRawConstantValue();
                return true;
            }
            paramId = 0;
            return false;
        }
    }
}

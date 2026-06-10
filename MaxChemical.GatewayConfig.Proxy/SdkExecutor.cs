using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MaxChemical.GatewayConfig.Proxy.Native;

namespace MaxChemical.GatewayConfig.Proxy
{
    /// <summary>
    /// 代理进程的业务执行器。
    /// SDK 非线程安全 + 老 32 位 DLL 可能用 TLS,所有 SDK 调用必须在同一线程上跑完,
    /// 因此搜索/读/写都用 Task.Run 包同步逻辑,而不是用 SemaphoreSlim + await。
    /// </summary>
    internal class SdkExecutor : IDisposable
    {
        private readonly object _sdkLock = new object();
        private bool _initialized;

        public SdkExecutor()
        {
            SdkLog.Init();
            SdkLog.Write("====== SdkExecutor 构造 ======");
            SdkLog.Write($"Is64BitProcess = {Environment.Is64BitProcess}");

            var rc = ZLDevManageNative.ZLDM_Init(0);
            _initialized = rc != 0;
            SdkLog.Write($"ZLDM_Init(0) rc={rc} initialized={_initialized}");

            if (!_initialized)
                throw new InvalidOperationException($"ZLDM_Init failed: rc={rc}");

            // 打印 DLL 版本,V1.40 是精简版,V1.40_P2P 是完整版
            try
            {
                var ver = ZLDevManageNative.PtrToString(ZLDevManageNative.ZLDM_GetVer());
                SdkLog.Write($"DLL version = '{ver}'");
            }
            catch (Exception ex)
            {
                SdkLog.Write($"GetVer failed: {ex.Message}");
            }
        }

        public void Dispose()
        {
            if (_initialized)
            {
                try { ZLDevManageNative.ZLDM_Exit(); } catch { /* swallow */ }
                _initialized = false;
            }
        }

        // ─── 设备搜索 ───
        public Task<List<Modules.GatewayConfig.Ipc.DeviceSnapshot>> SearchDevicesAsync(
            int timeoutMs, Action<int> onProgress, CancellationToken ct)
        {
            _ = timeoutMs;

            return Task.Run(() =>
            {
                SdkLog.Write("====== SearchDevicesAsync ENTER ======");

                lock (_sdkLock)
                {
                    var seen = new Dictionary<string, Modules.GatewayConfig.Ipc.DeviceSnapshot>(
                        StringComparer.OrdinalIgnoreCase);

                    const int Rounds = 8;
                    const int InterRoundDelayMs = 300;

                    for (int round = 0; round < Rounds; round++)
                    {
                        ct.ThrowIfCancellationRequested();

                        var count = ZLDevManageNative.ZLDM_StartSearchDev();
                        int newlyAdded = 0;

                        for (int i = 0; i < count; i++)
                        {
                            var devId = ZLDevManageNative.PtrToString(ZLDevManageNative.ZLDM_GetDevID(i));
                            if (string.IsNullOrEmpty(devId)) continue;
                            if (seen.ContainsKey(devId)) continue;

                            var snapshot = ReadFullSnapshot(devId);
                            if (snapshot != null)
                            {
                                seen[devId] = snapshot;
                                newlyAdded++;
                                SdkLog.Write($"  + {devId} ip={snapshot.Ip} name={snapshot.DeviceName}");
                            }
                        }

                        SdkLog.Write($"round={round + 1}/{Rounds} count={count} new={newlyAdded} total={seen.Count}");

                        onProgress?.Invoke(seen.Count);

                        if (round < Rounds - 1)
                            Thread.Sleep(InterRoundDelayMs);
                    }

                    SdkLog.Write($"====== SearchDevicesAsync DONE total={seen.Count} ======");
                    return new List<Modules.GatewayConfig.Ipc.DeviceSnapshot>(seen.Values);
                }
            }, ct);
        }

        // ─── 单设备参数读取 ───
        public Task<Modules.GatewayConfig.Ipc.DeviceSnapshot> ReadParamsAsync(string deviceId, CancellationToken ct)
        {
            return Task.Run(() =>
            {
                lock (_sdkLock)
                {
                    return ReadFullSnapshot(deviceId);
                }
            }, ct);
        }

        // ─── 写入参数 + 触发重启 ───
        public Task WriteParamsAsync(string deviceId, Dictionary<string, object> changes, CancellationToken ct)
        {
            return Task.Run(() =>
            {
                lock (_sdkLock)
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
            }, ct);
        }

        // ─── 恢复出厂 ───
        public Task SetDefaultAsync(string deviceId, CancellationToken ct)
        {
            return Task.Run(() =>
            {
                lock (_sdkLock)
                {
                    ZLDevManageNative.ZLDM_SetDevParamInt(deviceId, 0, ZLDevManageNative.PARAM_SET_TO_DEFAULT);
                    var rc = ZLDevManageNative.ZLDM_SetDevParamExcute(deviceId);
                    if (rc == 0)
                        throw new InvalidOperationException("ZLDM_SetDevParamExcute (reset) failed");
                }
            }, ct);
        }

        // ─── 设备重启后回归探测 ───
        public Task<bool> PollDeviceAsync(string deviceId, int intervalMs, int timeoutMs,
            Action<int, bool> onProgress, CancellationToken ct)
        {
            return Task.Run(() =>
            {
                var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
                int attempt = 0;
                while (DateTime.UtcNow < deadline)
                {
                    ct.ThrowIfCancellationRequested();
                    attempt++;
                    bool found = false;

                    lock (_sdkLock)
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

                    onProgress?.Invoke(attempt, found);
                    if (found) return true;
                    Thread.Sleep(intervalMs);
                }
                return false;
            }, ct);
        }

        // ═══════════ private helpers ═══════════

        private Modules.GatewayConfig.Ipc.DeviceSnapshot ReadFullSnapshot(string deviceId)
        {
            try
            {
                var s = new Modules.GatewayConfig.Ipc.DeviceSnapshot { DeviceId = deviceId };
                s.Mac = deviceId;
                s.DeviceName = GetStr(deviceId, ZLDevManageNative.PARAM_DEV_NAME);
                s.Ip = GetStr(deviceId, ZLDevManageNative.PARAM_DEV_LOCAL_IP);
                s.LocalPort = (ushort)GetInt(deviceId, ZLDevManageNative.PARAM_DEV_LOCAL_PORT);
                s.Netmask = GetStr(deviceId, ZLDevManageNative.PARAM_NET_MASK);
                s.Gateway = GetStr(deviceId, ZLDevManageNative.PARAM_GATEWAY);
                s.DnsServer = GetStr(deviceId, ZLDevManageNative.PARAM_DNS_SERVER_IP);
                s.FirmwareVersion = GetStr(deviceId, ZLDevManageNative.PARAM_DEV_VER);
                s.DestIp = GetStr(deviceId, ZLDevManageNative.PARAM_DEST_IP);
                s.DestPort = (ushort)GetInt(deviceId, ZLDevManageNative.PARAM_DEST_PORT);
                s.WebPort = (ushort)GetInt(deviceId, ZLDevManageNative.PARAM_WEB_PORT);
                s.WorkMode = GetInt(deviceId, ZLDevManageNative.PARAM_WORK_MODE);
                s.BaudRate = GetInt(deviceId, ZLDevManageNative.PARAM_BAUNDRATE);
                s.DataBits = GetInt(deviceId, ZLDevManageNative.PARAM_DATA_BITS);
                s.Parity = GetInt(deviceId, ZLDevManageNative.PARAM_PARITY);
                s.StopBit = GetInt(deviceId, ZLDevManageNative.PARAM_STOP_BIT);
                s.FlowControl = GetInt(deviceId, ZLDevManageNative.PARAM_FLOW_CONTROL);
                s.ReconnectTime = GetInt(deviceId, ZLDevManageNative.PARAM_RECONNECT_TIME);
                s.KeepAliveTime = GetInt(deviceId, ZLDevManageNative.PARAM_KEEP_ALIVE_TIME);
                s.LinkStatus = GetInt(deviceId, ZLDevManageNative.PARAM_LINK_STATUS);
                return s;
            }
            catch (Exception ex)
            {
                SdkLog.Write($"ReadFullSnapshot EXCEPTION ({deviceId}): {ex.Message}");
                return null;
            }
        }

        private static int GetInt(string deviceId, int paramId)
        {
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

    /// <summary>
    /// 简单的文件日志,写到 Proxy 进程当前运行目录下的 zlsdk.log。
    /// 线程安全,追加写,带时间戳。调试完可以删掉。
    /// </summary>
    internal static class SdkLog
    {
        private static readonly object _lock = new object();
        private static string _path;

        public static void Init()
        {
            if (_path != null) return;
            try
            {
                _path = Path.Combine(AppContext.BaseDirectory, "zlsdk.log");
                File.AppendAllText(_path,
                    $"{Environment.NewLine}{Environment.NewLine}" +
                    $"================ 新会话 {DateTime.Now:yyyy-MM-dd HH:mm:ss} ================{Environment.NewLine}");
            }
            catch { /* swallow */ }
        }

        public static void Write(string msg)
        {
            try
            {
                if (_path == null) Init();
                lock (_lock)
                {
                    File.AppendAllText(_path,
                        $"[{DateTime.Now:HH:mm:ss.fff}] {msg}{Environment.NewLine}");
                }
            }
            catch { /* swallow */ }
        }
    }
}
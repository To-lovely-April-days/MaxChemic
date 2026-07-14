using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using DevicePlugins.Devices;
using MaxChemical.Infrastructure.Cloud;
using MaxChemical.Modules.Designer.Event;
using Prism.Events;

namespace MaxChemical.Modules.Designer.Service.Cloud
{
    /// <summary>
    /// 桌面「上云」推送中枢(单例):把项目名、实时监控值、DOE 运行表统一 POST 到云端,
    /// 并把网页远程提交的 DOE 结果回发给 DOEBatchExecutor。所有 HTTP 都收敛在这里,
    /// 业务侧(视图 / 执行器)只通过 Prism 事件交互,互不依赖。
    ///
    /// 云端契约(见 ProcessEndpoints):
    ///   POST /ingest/process/telemetry   { name, values:{ "设备名|指标": 数值 } }
    ///   POST /ingest/process/doe         { name, doe:{...CloudDoeSnapshot(camelCase)...} }
    ///   GET  /ingest/process/doe/response?name=&batchId=&runIndex=  → 204 或 结果JSON
    ///
    /// 地址/密钥与 ProcessDesignerView 的 Ctrl+U 保持一致。
    /// </summary>
    public sealed class CloudPushService : IDisposable
    {
        // ★ 与 ProcessDesignerView Ctrl+U 中的常量保持一致
        private const string CloudBaseUrl = "http://139.224.67.86:9080";
        private const string UploadKey = "";               // 云端若设了 Process:UploadKey,填成同值

        private const int TelemetryFlushMs = 1000;         // 实时值最快 1s 推一次
        private const int DoePollMs = 1500;                // 远程结果轮询间隔
        private const int DisplayNameTtlMs = 5000;         // 设备显示名映射缓存

        private readonly IEventAggregator _ea;
        private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };
        private readonly JsonSerializerOptions _json = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        private volatile string _projectName = "";

        // 实时值:累积 → 定时 flush
        private readonly object _telLock = new();
        private readonly Dictionary<string, double> _telemetry = new();
        private bool _telDirty;
        private readonly Timer _telTimer;

        // 设备Id → 显示名 缓存
        private Dictionary<string, string> _displayNames = new();
        private DateTime _displayNamesAt = DateTime.MinValue;

        // DOE 远程填结果轮询目标
        private readonly object _pollLock = new();
        private string? _pollBatchId;
        private int _pollRunIndex = -1;
        private readonly Timer _doePollTimer;

        public CloudPushService(IEventAggregator eventAggregator)
        {
            _ea = eventAggregator ?? throw new ArgumentNullException(nameof(eventAggregator));

            _ea.GetEvent<ProjectNameUpdatedEvent>().Subscribe(OnProjectName, ThreadOption.BackgroundThread, true);
            _ea.GetEvent<MonitoringCardUpdateEvent>().Subscribe(OnMonitoring, ThreadOption.BackgroundThread, true);
            _ea.GetEvent<CloudDoeSnapshotEvent>().Subscribe(OnDoeSnapshot, ThreadOption.BackgroundThread, true);

            _telTimer = new Timer(_ => FlushTelemetry(), null, TelemetryFlushMs, TelemetryFlushMs);
            _doePollTimer = new Timer(_ => PollRemoteResponse(), null, DoePollMs, DoePollMs);

            Debug.WriteLine("CloudPushService 已启动");
        }

        // ── 项目名 ──
        private void OnProjectName(string name)
        {
            if (!string.IsNullOrWhiteSpace(name)) _projectName = name.Trim();
        }

        // ── 实时监控 ──
        private void OnMonitoring(MonitoringCardUpdateEventArgs e)
        {
            if (e?.OutputParameters?.Variables == null || string.IsNullOrEmpty(e.DeviceId)) return;
            var cardName = ResolveDisplayName(e.DeviceId);
            lock (_telLock)
            {
                foreach (var p in e.OutputParameters.Variables)
                {
                    if (p == null || string.IsNullOrEmpty(p.Name)) continue;
                    if (!TryToDouble(p.Value, out var v)) continue;   // 只推数值型
                    _telemetry[cardName + "|" + p.Name] = v;
                    _telDirty = true;
                }
            }
        }

        private string ResolveDisplayName(string deviceId)
        {
            if ((DateTime.UtcNow - _displayNamesAt).TotalMilliseconds > DisplayNameTtlMs)
            {
                try
                {
                    _ea.GetEvent<GetDeviceDisplayNamesRequestEvent>().Publish(map =>
                    {
                        if (map != null) _displayNames = map;
                    });
                }
                catch (Exception ex) { Debug.WriteLine("取设备显示名失败: " + ex.Message); }
                _displayNamesAt = DateTime.UtcNow;
            }
            return _displayNames.TryGetValue(deviceId, out var n) && !string.IsNullOrEmpty(n) ? n : deviceId;
        }

        private void FlushTelemetry()
        {
            Dictionary<string, double>? snapshot = null;
            lock (_telLock)
            {
                if (_telDirty && _telemetry.Count > 0)
                {
                    snapshot = new Dictionary<string, double>(_telemetry);
                    _telDirty = false;
                }
            }
            if (snapshot == null) return;
            PostJson("/ingest/process/telemetry", new { name = _projectName, values = snapshot });
        }

        // ── DOE 快照 ──
        private void OnDoeSnapshot(CloudDoeSnapshot snap)
        {
            if (snap == null) return;
            PostJson("/ingest/process/doe", new { name = _projectName, doe = snap });

            // 有 waiting 就开始轮询该组的远程结果,否则停止
            lock (_pollLock)
            {
                if (snap.Waiting != null)
                {
                    _pollBatchId = snap.BatchId;
                    _pollRunIndex = snap.Waiting.RunIndex;
                }
                else
                {
                    _pollBatchId = null;
                    _pollRunIndex = -1;
                }
            }
        }

        private void PollRemoteResponse()
        {
            string? batchId; int runIndex; string name;
            lock (_pollLock) { batchId = _pollBatchId; runIndex = _pollRunIndex; }
            if (string.IsNullOrEmpty(batchId)) return;
            name = _projectName;

            try
            {
                var url = $"{CloudBaseUrl}/ingest/process/doe/response?name={Uri.EscapeDataString(name)}&batchId={Uri.EscapeDataString(batchId)}&runIndex={runIndex}";
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                if (!string.IsNullOrEmpty(UploadKey)) req.Headers.Add("X-Upload-Key", UploadKey);
                using var resp = _http.SendAsync(req).GetAwaiter().GetResult();
                if (resp.StatusCode == System.Net.HttpStatusCode.NoContent || !resp.IsSuccessStatusCode) return;

                var body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                var values = JsonSerializer.Deserialize<Dictionary<string, double>>(body);
                if (values == null || values.Count == 0) return;

                // 命中:停止轮询该组并回发给执行器
                lock (_pollLock)
                {
                    if (_pollBatchId != batchId || _pollRunIndex != runIndex) return; // 目标已变
                    _pollBatchId = null; _pollRunIndex = -1;
                }
                _ea.GetEvent<CloudDoeRemoteResponseEvent>().Publish(new CloudDoeRemoteResponse
                {
                    BatchId = batchId!,
                    RunIndex = runIndex,
                    Values = values
                });
                Debug.WriteLine($"云端远程结果已回发: batch={batchId} run={runIndex}");
            }
            catch (Exception ex) { Debug.WriteLine("轮询远程结果失败: " + ex.Message); }
        }

        // ── HTTP ──
        private void PostJson(string path, object payload)
        {
            try
            {
                var body = JsonSerializer.Serialize(payload, _json);
                using var req = new HttpRequestMessage(HttpMethod.Post, CloudBaseUrl + path)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json")
                };
                if (!string.IsNullOrEmpty(UploadKey)) req.Headers.Add("X-Upload-Key", UploadKey);
                using var resp = _http.SendAsync(req).GetAwaiter().GetResult();
                // 云端未启动/未登录时静默忽略,不打扰桌面操作
            }
            catch (Exception ex) { Debug.WriteLine($"上云 POST {path} 失败: {ex.Message}"); }
        }

        private static bool TryToDouble(object? v, out double d)
        {
            switch (v)
            {
                case null: d = 0; return false;
                case double dd: d = dd; return true;
                case float f: d = f; return true;
                case int i: d = i; return true;
                case long l: d = l; return true;
                case decimal m: d = (double)m; return true;
                default: return double.TryParse(v.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out d);
            }
        }

        public void Dispose()
        {
            _telTimer.Dispose();
            _doePollTimer.Dispose();
            _http.Dispose();
        }
    }
}

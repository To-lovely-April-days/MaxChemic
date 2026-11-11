using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MaxChemical.Logging;

namespace MaxChemical.Shell.Services.Agent.Knowledge
{
    /// <summary>
    /// OpenAI 兼容 Embedding 客户端(智谱 embedding-3 / 阿里 text-embedding-v3 同协议)。
    /// 未配置 ApiKey 时 IsConfigured=false,EmbedAsync 返回 null,知识库降级为本地关键词检索。
    /// 默认绕过系统代理直连;429/5xx 重试 2 次退避,401/400 直接抛(配置错误不该反复重试)。
    /// </summary>
    public sealed class EmbeddingService : IDisposable
    {
        private readonly ILogService _logger;
        private readonly HttpClient _http;

        public string BaseUrl { get; set; } = "https://open.bigmodel.cn/api/paas/v4";
        public string ApiKey { get; set; } = "";
        public string Model { get; set; } = "embedding-3";
        public int BatchSize { get; set; } = 8;

        public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey);

        public EmbeddingService(ILogService logger)
        {
            _logger = logger;
            var handler = new HttpClientHandler { UseProxy = false };
            _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
        }

        /// <summary>批量向量化;未配置时返回 null。返回的向量顺序与输入一一对应。</summary>
        public async Task<float[][]?> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
        {
            if (!IsConfigured) return null;
            if (texts == null || texts.Count == 0) return Array.Empty<float[]>();

            int batchSize = Math.Clamp(BatchSize, 1, 32);
            var result = new float[texts.Count][];

            for (int i = 0; i < texts.Count; i += batchSize)
            {
                ct.ThrowIfCancellationRequested();
                var batch = texts.Skip(i).Take(batchSize).ToList();
                float[][] vecs = await EmbedBatchAsync(batch, ct).ConfigureAwait(false);
                for (int j = 0; j < vecs.Length && i + j < result.Length; j++)
                    result[i + j] = vecs[j];
            }
            return result;
        }

        private async Task<float[][]> EmbedBatchAsync(List<string> batch, CancellationToken ct)
        {
            string payload = JsonSerializer.Serialize(new { model = Model, input = batch });

            Exception lastError = null;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                if (attempt > 0)
                    await Task.Delay(attempt == 1 ? 500 : 1500, ct).ConfigureAwait(false);

                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Post, BaseUrl.TrimEnd('/') + "/embeddings");
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey);
                    req.Content = new StringContent(payload, Encoding.UTF8, "application/json");

                    var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
                    string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

                    if (!resp.IsSuccessStatusCode)
                    {
                        // 401/400 是配置/输入问题,重试无意义
                        if ((int)resp.StatusCode is 401 or 400)
                            throw new InvalidOperationException($"Embedding 请求被拒({(int)resp.StatusCode}):{Truncate(body, 300)}");
                        lastError = new InvalidOperationException($"Embedding 请求失败({(int)resp.StatusCode})");
                        continue;
                    }

                    return ParseVectors(body, batch.Count);
                }
                catch (HttpRequestException ex)
                {
                    lastError = ex; // 网络类错误,继续重试
                }
            }

            throw new InvalidOperationException("Embedding 请求连续失败:" + lastError?.Message);
        }

        private static float[][] ParseVectors(string body, int expectedCount)
        {
            using var doc = JsonDocument.Parse(body);
            var data = doc.RootElement.GetProperty("data");
            var result = new List<float[]>();
            foreach (var item in data.EnumerateArray())
            {
                var vec = new List<float>();
                if (item.TryGetProperty("embedding", out var emb) && emb.ValueKind == JsonValueKind.Array)
                {
                    foreach (var n in emb.EnumerateArray())
                        vec.Add(n.GetSingle());
                }
                result.Add(vec.ToArray());
            }
            if (result.Count != expectedCount)
                throw new InvalidOperationException($"Embedding 返回数量不符:期望 {expectedCount},实际 {result.Count}");
            return result.ToArray();
        }

        private static string Truncate(string s, int len)
            => string.IsNullOrEmpty(s) || s.Length <= len ? s : s.Substring(0, len) + "…";

        public void Dispose() => _http.Dispose();
    }
}

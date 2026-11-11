using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MaxChemical.Logging;

namespace MaxChemical.Shell.Services.Agent.Search
{
    /// <summary>
    /// Bocha(博查)联网搜索服务:检索公开信息,格式化为带来源链接的 Markdown 供模型引用。
    /// 未配置 ApiKey 时 IsConfigured=false,工具层直接给出可读提示,不发起请求。
    /// 默认绕过系统代理直连(与 DeepSeekClient 的现场策略一致)。
    /// </summary>
    public sealed class WebSearchService : IDisposable
    {
        private readonly ILogService _logger;
        private readonly HttpClient _http;

        public string ApiKey { get; set; } = "";
        public string BaseUrl { get; set; } = "https://api.bochaai.com/v1/web-search";
        public int MaxResults { get; set; } = 5;

        public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey);

        public WebSearchService(ILogService logger)
        {
            _logger = logger;
            var handler = new HttpClientHandler { UseProxy = false };
            _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        }

        /// <summary>执行一次搜索,返回给模型看的 Markdown 文本(含来源链接)。</summary>
        public async Task<string> SearchAsync(string query, CancellationToken ct = default)
        {
            int count = Math.Clamp(MaxResults, 1, 10);
            string payload = JsonSerializer.Serialize(new { query = query, summary = true, count = count });

            using var req = new HttpRequestMessage(HttpMethod.Post, BaseUrl.TrimEnd('/'));
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey);
            req.Content = new StringContent(payload, Encoding.UTF8, "application/json");

            var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogError($"Bocha 搜索失败 {(int)resp.StatusCode}: {Truncate(body, 300)}");
                throw new InvalidOperationException($"联网搜索请求失败({(int)resp.StatusCode})");
            }

            return ParseResponse(body);
        }

        /// <summary>防御式解析:字段缺失/类型不符一律跳过,绝不因响应结构变化而抛异常。</summary>
        private static string ParseResponse(string json)
        {
            var sb = new StringBuilder();
            try
            {
                using var doc = JsonDocument.Parse(json);

                if (doc.RootElement.TryGetProperty("data", out var data) &&
                    data.ValueKind == JsonValueKind.Object)
                {
                    // 搜索引擎自己的汇总答案(开启 summary=true 时返回)
                    if (data.TryGetProperty("answer", out var answer) &&
                        answer.ValueKind == JsonValueKind.String &&
                        !string.IsNullOrWhiteSpace(answer.GetString()))
                    {
                        sb.AppendLine("**直接答案:** " + Truncate(answer.GetString().Trim(), 800));
                        sb.AppendLine();
                    }

                    if (data.TryGetProperty("webPages", out var pages) &&
                        pages.ValueKind == JsonValueKind.Object &&
                        pages.TryGetProperty("value", out var value) &&
                        value.ValueKind == JsonValueKind.Array)
                    {
                        int i = 1;
                        foreach (var p in value.EnumerateArray())
                        {
                            if (p.ValueKind != JsonValueKind.Object) continue;
                            string name = p.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : "";
                            string url = p.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : "";
                            if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(url)) continue;

                            string snippet = "";
                            foreach (var key in new[] { "snippet", "summary", "description" })
                            {
                                if (p.TryGetProperty(key, out var s) && s.ValueKind == JsonValueKind.String &&
                                    !string.IsNullOrWhiteSpace(s.GetString()))
                                {
                                    snippet = s.GetString();
                                    break;
                                }
                            }

                            sb.AppendLine($"{i}. [{Truncate(name, 80)}]({url})");
                            if (!string.IsNullOrWhiteSpace(snippet))
                                sb.AppendLine("   " + Truncate(snippet.Replace('\n', ' '), 220));
                            i++;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine("(搜索结果解析异常:" + ex.Message + ")");
            }

            string result = sb.ToString().Trim();
            return string.IsNullOrWhiteSpace(result)
                ? "搜索完成,但没有返回任何结果。请如实告知用户没有搜到相关内容。"
                : result;
        }

        private static string Truncate(string s, int len)
            => string.IsNullOrEmpty(s) || s.Length <= len ? s : s.Substring(0, len) + "…";

        public void Dispose() => _http.Dispose();
    }
}

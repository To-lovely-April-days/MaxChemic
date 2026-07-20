using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using MaxChemical.Logging;

namespace MaxChemical.Shell.Services.Agent
{
    /// <summary>
    /// DeepSeek 对话补全客户端(OpenAI 兼容协议,支持 function calling)。
    /// 仅做一次"messages+tools → assistant 消息"的往返;多轮工具循环由 XiaoTongAgent 编排。
    /// </summary>
    public sealed class DeepSeekClient : IDisposable
    {
        private readonly ILogService _logger;
        private HttpClient _http;

        public string ApiKey { get; set; } = "";
        public string Model { get; set; } = "deepseek-chat";
        public string BaseUrl { get; set; } = "https://api.deepseek.com";
        public double Temperature { get; set; } = 0.3;

        public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey);

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNameCaseInsensitive = true
        };

        public DeepSeekClient(ILogService logger)
        {
            _logger = logger;
            // 默认绕过系统代理直连(现场机器常挂本地代理,走代理会导致 API 超时/被拦);
            // 如确需走代理,可在 appsettings.json 设 DeepSeek:UseProxy=true(由 Bootstrapper 重建)。
            _http = CreateHttpClient(useProxy: false);
        }

        internal static HttpClient CreateHttpClient(bool useProxy)
        {
            var handler = new HttpClientHandler
            {
                UseProxy = useProxy,
                Proxy = useProxy ? System.Net.WebRequest.DefaultWebProxy : null
            };
            return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(120) };
        }

        /// <summary>切换是否走系统代理(默认不走)。由 AgentBootstrapper 按配置调用。</summary>
        public void SetUseProxy(bool useProxy)
        {
            var old = _http;
            _http = CreateHttpClient(useProxy);
            old?.Dispose();
        }

        /// <summary>
        /// 发起一次补全。tools 为空则是纯对话。
        /// 返回 assistant 消息(可能带 tool_calls)。
        /// </summary>
        public async Task<AgentMessage> ChatAsync(
            IReadOnlyList<AgentMessage> messages,
            IReadOnlyList<IAgentTool> tools,
            CancellationToken ct = default)
        {
            if (!IsConfigured)
                throw new InvalidOperationException("DeepSeek ApiKey 未配置(appsettings.json → DeepSeek.ApiKey)");

            string doc = BuildRequestJson(messages, tools);
            var content = new StringContent(doc, Encoding.UTF8, "application/json");

            using var req = new HttpRequestMessage(HttpMethod.Post, BaseUrl.TrimEnd('/') + "/chat/completions");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey);
            req.Content = content;

            var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogError($"DeepSeek API 失败 {(int)resp.StatusCode}: {Truncate(body, 500)}");
                throw new InvalidOperationException($"DeepSeek API 请求失败({(int)resp.StatusCode}):{ExtractApiError(body)}");
            }

            using var json = JsonDocument.Parse(body);
            var msgEl = json.RootElement.GetProperty("choices")[0].GetProperty("message");
            var assistant = JsonSerializer.Deserialize<AgentMessage>(msgEl.GetRawText(), JsonOpts)
                            ?? new AgentMessage { Role = AgentRole.Assistant, Content = "" };
            assistant.Role = AgentRole.Assistant;
            return assistant;
        }

        private string BuildRequestJson(IReadOnlyList<AgentMessage> messages, IReadOnlyList<IAgentTool> tools)
        {
            using var stream = new System.IO.MemoryStream();
            using (var w = new Utf8JsonWriter(stream))
            {
                w.WriteStartObject();
                w.WriteString("model", Model);
                w.WriteNumber("temperature", Temperature);

                w.WritePropertyName("messages");
                JsonSerializer.Serialize(w, messages, JsonOpts);

                if (tools is { Count: > 0 })
                {
                    w.WritePropertyName("tools");
                    w.WriteStartArray();
                    foreach (var t in tools)
                    {
                        w.WriteStartObject();
                        w.WriteString("type", "function");
                        w.WritePropertyName("function");
                        w.WriteStartObject();
                        w.WriteString("name", t.Name);
                        w.WriteString("description", t.Description);
                        w.WritePropertyName("parameters");
                        // ParametersSchema 是 JSON 字面量,原样写入
                        using (var schema = JsonDocument.Parse(t.ParametersSchema))
                            schema.RootElement.WriteTo(w);
                        w.WriteEndObject();
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                    w.WriteString("tool_choice", "auto");
                }

                w.WriteEndObject();
            }
            return Encoding.UTF8.GetString(stream.ToArray());
        }

        private static string ExtractApiError(string body)
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("error", out var err) &&
                    err.TryGetProperty("message", out var msg))
                    return msg.GetString();
            }
            catch { }
            return Truncate(body, 200);
        }

        private static string Truncate(string s, int len)
            => string.IsNullOrEmpty(s) || s.Length <= len ? s : s.Substring(0, len) + "…";

        public void Dispose() => _http.Dispose();
    }
}

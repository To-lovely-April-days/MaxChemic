using System;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Security.Cryptography;
using System.Web;
using MaxChemical.Logging;
using Microsoft.Extensions.Configuration;
using NAudio.Wave;
using Newtonsoft.Json;

namespace MaxChemical.Shell.Services
{
    /// <summary>
    /// 阿里云实时语音识别引擎（WebSocket）
    /// </summary>
    public class AliyunRealtimeAsrEngine : IDisposable
    {
        private readonly ILogService _logger;
        private readonly IConfiguration _configuration;

        private ClientWebSocket? _webSocket;
        private WaveInEvent? _waveIn;
        private CancellationTokenSource? _cts;

        private string? _accessKeyId;
        private string? _accessKeySecret;
        private string? _appKey;
        private string? _token;

        public event EventHandler<WhisperTextRecognizedEventArgs>? TextRecognized;
        public event EventHandler<AudioLevelEventArgs>? AudioLevelChanged;

        public AliyunRealtimeAsrEngine(ILogService logger, IConfiguration configuration)
        {
            _logger = logger;
            _configuration = configuration;
        }

        public void Initialize()
        {

            _accessKeyId = _configuration["AliyunCloud:AccessKeyId"] == null ? "" : _configuration["AliyunCloud:AccessKeyId"];
            _accessKeySecret = _configuration["AliyunCloud:AccessKeySecret"] == null ? "" : _configuration["AliyunCloud:AccessKeySecret"];
            _appKey = _configuration["AliyunCloud:AppKey"] == null ? "2N4jwL056W5ZvuHc" : _configuration["AliyunCloud:AppKey"];

            if (string.IsNullOrEmpty(_accessKeyId) ||
                string.IsNullOrEmpty(_accessKeySecret) ||
                string.IsNullOrEmpty(_appKey))
            {
                throw new Exception("阿里云密钥未配置");
            }

            _logger.LogInformation("阿里云实时 ASR 引擎初始化成功");
        }

        public async Task StartContinuousRecognitionAsync(CancellationToken cancellationToken)
        {
            try
            {
                _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

                // 获取 Token
                _token = await GetTokenAsync();
                if (string.IsNullOrEmpty(_token))
                {
                    throw new Exception("获取阿里云 Token 失败");
                }

                _logger.LogInformation($"获取到 Token: {_token.Substring(0, Math.Min(20, _token.Length))}...");

                // 建立 WebSocket 连接
                _webSocket = new ClientWebSocket();
                var url = $"wss://nls-gateway.cn-shanghai.aliyuncs.com/ws/v1?token={_token}";

                await _webSocket.ConnectAsync(new Uri(url), _cts.Token);
                _logger.LogInformation("WebSocket 连接成功");

                // 生成阿里云要求的 ID 格式（去掉连字符）
                var messageId = Guid.NewGuid().ToString("N");
                var taskId = Guid.NewGuid().ToString("N");

                // 发送启动参数
                var startMsg = new
                {
                    header = new
                    {
                        message_id = messageId,
                        task_id = taskId,
                        @namespace = "SpeechTranscriber",  // ← 改为 namespace（不是 namespace_name）
                        name = "StartTranscription",
                        appkey = _appKey
                    },
                    payload = new
                    {
                        format = "pcm",
                        sample_rate = 16000,
                        enable_inverse_text_normalization = true,
                        enable_punctuation_prediction = true,
                        enable_intermediate_result = true,
                        max_sentence_silence = 800
                    }
                };

                var startJson = JsonConvert.SerializeObject(startMsg);
                _logger.LogInformation($"发送启动消息: {startJson}");

                var startBytes = Encoding.UTF8.GetBytes(startJson);
                await _webSocket.SendAsync(
                    new ArraySegment<byte>(startBytes),
                    WebSocketMessageType.Text,
                    true,
                    _cts.Token);

                // 等待服务器响应
                await Task.Delay(500, _cts.Token);

                // 启动接收线程
                _ = Task.Run(() => ReceiveLoop(_cts.Token), _cts.Token);

                // 启动音频采集
                _waveIn = new WaveInEvent
                {
                    WaveFormat = new WaveFormat(16000, 16, 1),
                    BufferMilliseconds = 40
                };

                _waveIn.DataAvailable += async (s, e) =>
                {
                    if (_webSocket?.State == WebSocketState.Open)
                    {
                        try
                        {
                            await _webSocket.SendAsync(
                                new ArraySegment<byte>(e.Buffer, 0, e.BytesRecorded),
                                WebSocketMessageType.Binary,
                                true,
                                CancellationToken.None);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError($"发送音频数据失败: {ex.Message}");
                        }
                    }
                };

                _waveIn.StartRecording();
                _logger.LogInformation("阿里云实时语音识别已启动，开始录音");

                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("识别任务已取消");
            }
            catch (Exception ex)
            {
                _logger.LogError($"实时识别失败: {ex.Message}");
                _logger.LogError($"堆栈: {ex.StackTrace}");
            }
        }


        private async Task ReceiveLoop(CancellationToken cancellationToken)
        {
            var buffer = new byte[8192];

            try
            {
                while (_webSocket?.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
                {
                    var result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);

                    if (result.MessageType == WebSocketMessageType.Text)
                    {
                        var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
                        _logger.LogDebug($"收到消息: {json}");

                        var response = JsonConvert.DeserializeObject<AliyunRealtimeResponse>(json);

                        // 只处理句子结束事件（最终完整结果）
                        if (response?.Header?.Name == "SentenceEnd" &&
                            !string.IsNullOrWhiteSpace(response?.Payload?.Result))
                        {
                            var text = response.Payload.Result.TrimEnd('。', '，', '、', '!', '！', '?', '？');  // 去掉标点
                            _logger.LogInformation($"最终识别结果: {text}");

                            TextRecognized?.Invoke(this, new WhisperTextRecognizedEventArgs
                            {
                                Text = text,
                                Timestamp = DateTime.Now,
                                Confidence = response.Payload.Confidence ?? 0.9f,
                                EnergyLevel = 0
                            });
                        }
                        // 实时结果只用于调试
                        else if (response?.Header?.Name == "TranscriptionResultChanged" &&
                                 !string.IsNullOrWhiteSpace(response?.Payload?.Result))
                        {
                            var text = response.Payload.Result;
                            _logger.LogDebug($" 实时片段: {text}");
                        }
                        // 错误处理
                        else if (response?.Header?.Name == "TaskFailed")
                        {
                            _logger.LogError($" 识别任务失败: {response.Header.Status}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"接收消息失败: {ex.Message}");
            }
        }

        private async Task<string?> GetTokenAsync()
        {
            try
            {
                _logger.LogInformation("开始获取阿里云 Token...");

                // 1. 构建公共参数
                var timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
                var nonce = Guid.NewGuid().ToString();

                var parameters = new Dictionary<string, string>
                {
                    ["AccessKeyId"] = _accessKeyId!,
                    ["Action"] = "CreateToken",
                    ["Version"] = "2019-02-28",
                    ["Timestamp"] = timestamp,
                    ["SignatureMethod"] = "HMAC-SHA1",
                    ["SignatureVersion"] = "1.0",
                    ["SignatureNonce"] = nonce,
                    ["Format"] = "JSON"
                };

                _logger.LogInformation($"请求参数: AccessKeyId={_accessKeyId}, Timestamp={timestamp}, Nonce={nonce}");

                // 2. 生成签名
                var signature = GenerateSignature(parameters, _accessKeySecret!, "GET");  // ← 改这里
                parameters["Signature"] = signature;

                _logger.LogInformation($"生成的签名: {signature}");

                // 3. 构建 URL（使用 POST）
                var queryString = string.Join("&", parameters
                    .OrderBy(p => p.Key)
                    .Select(p => $"{UrlEncode(p.Key)}={UrlEncode(p.Value)}"));

                var url = $"https://nls-meta.cn-shanghai.aliyuncs.com/?{queryString}";

                _logger.LogInformation($"完整请求 URL: {url}");

                // 4. 发送请求（增加超时时间 + 添加重试）
                using var httpClient = new HttpClient();
                httpClient.Timeout = TimeSpan.FromSeconds(60);  // ← 增加到 60 秒
                httpClient.DefaultRequestHeaders.Add("User-Agent", "MaxChemical-ASR/1.0");

                var response = await httpClient.GetAsync(url);
                var json = await response.Content.ReadAsStringAsync();

                _logger.LogInformation($"HTTP 状态码: {(int)response.StatusCode} ({response.StatusCode})");
                _logger.LogInformation($"响应头: {response.Headers}");
                _logger.LogInformation($"响应体: {json}");

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogError($" 获取 Token 失败: HTTP {(int)response.StatusCode}");
                    _logger.LogError($"响应内容: {json}");
                    return null;
                }

                // 5. 解析响应
                var tokenResponse = JsonConvert.DeserializeObject<AliyunTokenResponse>(json);

                if (tokenResponse?.Token?.Id != null)
                {
                    _logger.LogInformation($" Token 获取成功: {tokenResponse.Token.Id.Substring(0, 20)}...");
                    _logger.LogInformation($"Token 过期时间: {DateTimeOffset.FromUnixTimeSeconds(tokenResponse.Token.ExpireTime):yyyy-MM-dd HH:mm:ss}");
                    return tokenResponse.Token.Id;
                }

                _logger.LogError("Token 响应格式错误");
                return null;
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError($" 网络请求失败: {ex.Message}");
                _logger.LogError($"内部异常: {ex.InnerException?.Message}");
                return null;
            }
            catch (TaskCanceledException ex)
            {
                _logger.LogError($" 请求超时: {ex.Message}");
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError($" 获取 Token 异常: {ex.Message}");
                _logger.LogError($"堆栈: {ex.StackTrace}");
                return null;
            }
        }

        private string GenerateSignature(Dictionary<string, string> parameters, string accessKeySecret, string httpMethod = "GET")
        {
            try
            {
                // 1. 排序参数（排除 Signature）
                var sortedParams = parameters
                    .Where(p => p.Key != "Signature")
                    .OrderBy(p => p.Key, StringComparer.Ordinal);  // ← 使用字典序

                // 2. 构建规范化查询字符串
                var canonicalizedQueryString = string.Join("&", sortedParams.Select(p =>
                    $"{UrlEncode(p.Key)}={UrlEncode(p.Value)}"));

                _logger.LogDebug($"规范化查询字符串: {canonicalizedQueryString}");

                // 3. 构建待签名字符串
                var stringToSign = $"{httpMethod}&{UrlEncode("/")}&{UrlEncode(canonicalizedQueryString)}";

                _logger.LogDebug($"待签名字符串: {stringToSign}");

                // 4. 计算 HMAC-SHA1 签名
                var key = Encoding.UTF8.GetBytes(accessKeySecret + "&");  // ← 注意这里要加 &
                var data = Encoding.UTF8.GetBytes(stringToSign);

                using var hmac = new HMACSHA1(key);
                var hash = hmac.ComputeHash(data);
                var signature = Convert.ToBase64String(hash);

                _logger.LogDebug($"最终签名: {signature}");

                return signature;
            }
            catch (Exception ex)
            {
                _logger.LogError($"签名生成失败: {ex.Message}");
                throw;
            }
        }

        private string UrlEncode(string value)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;

            var encoded = new StringBuilder();
            var bytes = Encoding.UTF8.GetBytes(value);

            foreach (byte b in bytes)
            {
                char c = (char)b;

                // RFC 3986 规范：保留字符
                if ((c >= 'A' && c <= 'Z') ||
                    (c >= 'a' && c <= 'z') ||
                    (c >= '0' && c <= '9') ||
                    c == '-' || c == '_' || c == '.' || c == '~')
                {
                    encoded.Append(c);
                }
                else
                {
                    // 其他字符进行百分号编码
                    encoded.Append('%');
                    encoded.Append(b.ToString("X2"));
                }
            }

            return encoded.ToString();
        }

        public void Dispose()
        {
            _waveIn?.StopRecording();
            _waveIn?.Dispose();
            _cts?.Cancel();
            _webSocket?.Dispose();
            _logger.LogInformation("阿里云实时 ASR 引擎已释放");
        }

        private class AliyunTokenResponse
        {
            [JsonProperty("Token")]
            public TokenData? Token { get; set; }
        }

        private class TokenData
        {
            [JsonProperty("Id")]
            public string? Id { get; set; }

            [JsonProperty("ExpireTime")]
            public long ExpireTime { get; set; }
        }

        private class AliyunRealtimeResponse
        {
            [JsonProperty("header")]
            public HeaderData? Header { get; set; }

            [JsonProperty("payload")]
            public PayloadData? Payload { get; set; }
        }

        private class HeaderData
        {
            [JsonProperty("name")]
            public string? Name { get; set; }

            [JsonProperty("status")]
            public int Status { get; set; }

            [JsonProperty("message")]
            public string? Message { get; set; }
        }

        private class PayloadData
        {
            [JsonProperty("result")]
            public string? Result { get; set; }

            [JsonProperty("confidence")]
            public float? Confidence { get; set; }

            [JsonProperty("index")]
            public int? Index { get; set; }

            [JsonProperty("time")]
            public long? Time { get; set; }
        }
    }
}

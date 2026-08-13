using System;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MaxChemical.Logging;
using Microsoft.Extensions.Configuration;
using NAudio.Wave;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MaxChemical.Shell.Services
{
    /// <summary>
    /// 阿里云百炼(DashScope)实时语音识别引擎 — paraformer-realtime 系列模型。
    /// 协议:wss://dashscope.aliyuncs.com/api-ws/v1/inference,Bearer ApiKey 鉴权,
    /// run-task → 持续推送 PCM 二进制帧 → result-generated(sentence_end=true 为整句)。
    /// 相比旧版 NLS:无需 AccessKey 签名换 Token,只要一个百炼 API Key;
    /// 类名与公开接口保持不变,上层(PiperVoiceAssistantService)无需改动。
    /// 全部网络请求绕过系统代理。
    /// </summary>
    public class AliyunRealtimeAsrEngine : IDisposable
    {
        private const string WsUrl = "wss://dashscope.aliyuncs.com/api-ws/v1/inference";

        private readonly ILogService _logger;
        private readonly IConfiguration _configuration;

        private ClientWebSocket? _webSocket;
        private WaveInEvent? _waveIn;
        private CancellationTokenSource? _cts;
        private readonly SemaphoreSlim _sendLock = new(1, 1);

        private string? _apiKey;
        private string _model = "paraformer-realtime-v2";

        public event EventHandler<WhisperTextRecognizedEventArgs>? TextRecognized;
        public event EventHandler<AudioLevelEventArgs>? AudioLevelChanged;

        public AliyunRealtimeAsrEngine(ILogService logger, IConfiguration configuration)
        {
            _logger = logger;
            _configuration = configuration;
        }

        public void Initialize()
        {
            _apiKey = _configuration["Bailian:ApiKey"];
            if (string.IsNullOrWhiteSpace(_apiKey))
                _apiKey = _configuration["DashScope:ApiKey"];
            if (string.IsNullOrWhiteSpace(_apiKey))
                _apiKey = Environment.GetEnvironmentVariable("DASHSCOPE_API_KEY");

            if (string.IsNullOrWhiteSpace(_apiKey))
                throw new Exception("百炼 API Key 未配置(appsettings.json → Bailian:ApiKey,或环境变量 DASHSCOPE_API_KEY)");

            string model = _configuration["Bailian:AsrModel"];
            if (!string.IsNullOrWhiteSpace(model)) _model = model;

            _logger.LogInformation($"百炼实时 ASR 引擎初始化成功(模型 {_model})");
        }

        /// <summary>
        /// 持续识别:会话断开(网络抖动/服务端超时)后自动重连,直到 cancellationToken 取消。
        /// </summary>
        public async Task StartContinuousRecognitionAsync(CancellationToken cancellationToken)
        {
            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var ct = _cts.Token;

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await RunSessionAsync(ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError($"识别会话异常: {ex.Message}");
                }

                if (ct.IsCancellationRequested) break;
                _logger.LogInformation("识别会话结束,2 秒后重连…");
                try { await Task.Delay(2000, ct); } catch (OperationCanceledException) { break; }
            }

            _logger.LogInformation("实时语音识别已停止");
        }

        private async Task RunSessionAsync(CancellationToken ct)
        {
            string taskId = Guid.NewGuid().ToString("N");
            var taskStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var sessionOver = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            _webSocket = new ClientWebSocket();
            _webSocket.Options.Proxy = null; // 绕过系统代理
            _webSocket.Options.SetRequestHeader("Authorization", "bearer " + _apiKey);

            await _webSocket.ConnectAsync(new Uri(WsUrl), ct);
            _logger.LogInformation("百炼 WebSocket 连接成功");

            // run-task:开启 ASR 任务
            var runTask = new
            {
                header = new { action = "run-task", task_id = taskId, streaming = "duplex" },
                payload = new
                {
                    task_group = "audio",
                    task = "asr",
                    function = "recognition",
                    model = _model,
                    parameters = new { format = "pcm", sample_rate = 16000 },
                    input = new { }
                }
            };
            await SendTextAsync(JsonConvert.SerializeObject(runTask), ct);

            // 接收循环
            var receiveTask = Task.Run(() => ReceiveLoop(taskId, taskStarted, sessionOver, ct), ct);

            // 等服务端 task-started 再开麦克风
            using (var startTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            using (ct.Register(() => startTimeout.Cancel()))
            {
                var done = await Task.WhenAny(taskStarted.Task, Task.Delay(Timeout.Infinite, startTimeout.Token));
                if (done != taskStarted.Task)
                    throw new Exception("等待 task-started 超时(检查 API Key 与网络)");
            }

            // 麦克风采集:16k/16bit/单声道,100ms 一帧
            _waveIn = new WaveInEvent
            {
                WaveFormat = new WaveFormat(16000, 16, 1),
                BufferMilliseconds = 100
            };

            _waveIn.DataAvailable += async (s, e) =>
            {
                var ws = _webSocket;
                if (ws?.State != WebSocketState.Open || ct.IsCancellationRequested) return;
                try
                {
                    ReportAudioLevel(e.Buffer, e.BytesRecorded);
                    await _sendLock.WaitAsync(ct);
                    try
                    {
                        await ws.SendAsync(new ArraySegment<byte>(e.Buffer, 0, e.BytesRecorded),
                            WebSocketMessageType.Binary, true, ct);
                    }
                    finally { _sendLock.Release(); }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    _logger.LogDebug($"发送音频帧失败: {ex.Message}");
                }
            };

            _waveIn.StartRecording();
            _logger.LogInformation("百炼实时语音识别已启动,开始录音");

            try
            {
                // 会话存续:直到取消或服务端结束任务/连接断开
                var done = await Task.WhenAny(sessionOver.Task, Task.Delay(Timeout.Infinite, ct));
                ct.ThrowIfCancellationRequested();
            }
            finally
            {
                try { _waveIn?.StopRecording(); } catch { }
                try { _waveIn?.Dispose(); } catch { }
                _waveIn = null;

                // 尽力发 finish-task 让服务端优雅收尾
                try
                {
                    if (_webSocket?.State == WebSocketState.Open)
                    {
                        var finish = new
                        {
                            header = new { action = "finish-task", task_id = taskId, streaming = "duplex" },
                            payload = new { input = new { } }
                        };
                        await SendTextAsync(JsonConvert.SerializeObject(finish), CancellationToken.None);
                    }
                }
                catch { }

                try { _webSocket?.Dispose(); } catch { }
                _webSocket = null;
                try { await receiveTask; } catch { }
            }
        }

        private async Task ReceiveLoop(string taskId,
            TaskCompletionSource<bool> taskStarted,
            TaskCompletionSource<bool> sessionOver,
            CancellationToken ct)
        {
            var buffer = new byte[16384];
            var messageBuf = new MemoryStream();

            try
            {
                while (_webSocket?.State == WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    messageBuf.SetLength(0);
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                        messageBuf.Write(buffer, 0, result.Count);
                    } while (!result.EndOfMessage);

                    if (result.MessageType == WebSocketMessageType.Close) break;
                    if (result.MessageType != WebSocketMessageType.Text) continue;

                    string json = Encoding.UTF8.GetString(messageBuf.ToArray());
                    var msg = JObject.Parse(json);
                    string evt = (string?)msg["header"]?["event"] ?? "";

                    switch (evt)
                    {
                        case "task-started":
                            _logger.LogInformation("ASR 任务已启动");
                            taskStarted.TrySetResult(true);
                            break;

                        case "result-generated":
                        {
                            var sentence = msg["payload"]?["output"]?["sentence"];
                            string text = (string?)sentence?["text"] ?? "";
                            bool sentenceEnd = (bool?)sentence?["sentence_end"] ?? false;

                            if (sentenceEnd && !string.IsNullOrWhiteSpace(text))
                            {
                                string clean = text.TrimEnd('。', '，', '、', '!', '！', '?', '？');
                                _logger.LogInformation($"最终识别结果: {clean}");
                                TextRecognized?.Invoke(this, new WhisperTextRecognizedEventArgs
                                {
                                    Text = clean,
                                    Timestamp = DateTime.Now,
                                    Confidence = 0.9f,
                                    EnergyLevel = 0
                                });
                            }
                            else if (!string.IsNullOrWhiteSpace(text))
                            {
                                _logger.LogDebug($"实时片段: {text}");
                            }
                            break;
                        }

                        case "task-finished":
                            _logger.LogInformation("ASR 任务已结束(服务端)");
                            sessionOver.TrySetResult(true);
                            return;

                        case "task-failed":
                            _logger.LogError($"ASR 任务失败: {(string?)msg["header"]?["error_code"]} {(string?)msg["header"]?["error_message"]}");
                            taskStarted.TrySetResult(false);
                            sessionOver.TrySetResult(true);
                            return;
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogWarning($"接收循环结束: {ex.Message}");
            }
            finally
            {
                sessionOver.TrySetResult(true);
            }
        }

        private async Task SendTextAsync(string json, CancellationToken ct)
        {
            var ws = _webSocket;
            if (ws == null) return;
            var bytes = Encoding.UTF8.GetBytes(json);
            await _sendLock.WaitAsync(ct);
            try
            {
                await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
            }
            finally { _sendLock.Release(); }
        }

        private void ReportAudioLevel(byte[] buffer, int count)
        {
            var handler = AudioLevelChanged;
            if (handler == null || count < 2) return;
            // 简单 RMS 电平(16bit PCM)
            long sum = 0;
            int samples = count / 2;
            for (int i = 0; i < count - 1; i += 2)
            {
                short s = (short)(buffer[i] | (buffer[i + 1] << 8));
                sum += (long)s * s;
            }
            float rms = (float)Math.Sqrt(sum / (double)samples) / short.MaxValue;
            handler(this, new AudioLevelEventArgs { Level = rms });
        }

        public void Dispose()
        {
            try { _cts?.Cancel(); } catch { }
            try { _waveIn?.StopRecording(); } catch { }
            try { _waveIn?.Dispose(); } catch { }
            try { _webSocket?.Dispose(); } catch { }
            _sendLock.Dispose();
            _logger.LogInformation("百炼实时 ASR 引擎已释放");
        }
    }
}

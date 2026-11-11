using System;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Channels;
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
        private readonly SemaphoreSlim _sessionLock = new(1, 1);

        private string? _apiKey;
        private string _model = "paraformer-realtime-v2";

        /// <summary>
        /// 麦克风灵敏度门限(RMS 归一化电平,appsettings → AliyunAsr:EnergyThreshold):
        /// 作为最低门限,实际门限还会叠加自适应底噪估计;语音段使用进入/退出双门限,
        /// 并保留句首预缓冲与句尾静音,避免轻声和断句所需的音频被逐帧丢弃。
        /// </summary>
        private float _energyThreshold;

        /// <summary>自适应底噪估计(仅在非语音段缓慢跟随环境电平)。</summary>
        private float _noiseFloor;
        private const float SpeechMargin = 0.004f;
        private const float SilenceMargin = 0.002f;
        private const int PreSpeechFrameCount = 3;
        private const int TrailingSilenceFrameCount = 8;

        public event EventHandler<WhisperTextRecognizedEventArgs>? TextRecognized;
        public event EventHandler<AudioLevelEventArgs>? AudioLevelChanged;

        /// <summary>实时中间片段(边说边出字)。供按键说话(PTT)的界面反馈使用。</summary>
        public event EventHandler<string>? PartialTextRecognized;

        /// <summary>
        /// 采集挂起:true 时不把麦克风帧发给服务端。
        /// 语音服务在 TTS 播报期间置 true——播报声被麦克风收回会形成回声污染,
        /// 挤占识别通道导致唤醒时灵时不灵(聊天窗打开、播报频繁时尤其明显)。
        /// </summary>
        public volatile bool CaptureSuspended;

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

            // 灵敏度门限(appsettings → VoiceSettings:AliyunAsr:EnergyThreshold,缺省 0=不设门限)
            string thresholdText = _configuration["VoiceSettings:AliyunAsr:EnergyThreshold"]
                                   ?? _configuration["AliyunAsr:EnergyThreshold"];
            if (!string.IsNullOrWhiteSpace(thresholdText) &&
                float.TryParse(thresholdText, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float threshold))
                _energyThreshold = threshold;

            _logger.LogInformation($"百炼实时 ASR 引擎初始化成功(模型 {_model},能量门限 {_energyThreshold})");
        }

        /// <summary>
        /// 按键说话(PTT)聆听会话:持续聆听直到超时或外部取消,不因第一句结束而收尾——
        /// 每一句整句结果经 onFinal 回调(界面把最终文本落到输入框),
        /// 中间片段经 PartialTextRecognized 事件持续回调(边说边出字)。
        /// 会话被服务端断开(空闲/抖动)时自动重连,直到取消。返回最后一句识别文本。
        /// 注意:与 StartContinuousRecognitionAsync 互斥(共享同一引擎),不要同时跑。
        /// </summary>
        public async Task<string> ListenOnceAsync(TimeSpan maxDuration, CancellationToken externalCt = default,
            Action<string>? onFinal = null)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(externalCt);
            cts.CancelAfter(maxDuration);

            string last = "";
            void OnFinal(object s, WhisperTextRecognizedEventArgs e)
            {
                if (string.IsNullOrWhiteSpace(e.Text)) return;
                last = e.Text;
                try { onFinal?.Invoke(e.Text); } catch { }
            }

            TextRecognized += OnFinal;
            try
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    try
                    {
                        await RunSessionAsync(cts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning($"PTT 会话异常: {ex.Message}");
                    }

                    if (cts.Token.IsCancellationRequested) break;
                    try { await Task.Delay(300, cts.Token); } catch (OperationCanceledException) { break; }
                }
            }
            finally
            {
                TextRecognized -= OnFinal;
            }
            return last;
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
                _logger.LogInformation("识别会话结束,0.5 秒后重连…");
                try { await Task.Delay(500, ct); } catch (OperationCanceledException) { break; }
            }

            _logger.LogInformation("实时语音识别已停止");
        }

        private async Task RunSessionAsync(CancellationToken ct)
        {
            await _sessionLock.WaitAsync(ct);
            try
            {
                string taskId = Guid.NewGuid().ToString("N");
                var taskStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var sessionOver = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var audioChannel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(120)
                {
                    FullMode = BoundedChannelFullMode.DropOldest,
                    SingleReader = true,
                    SingleWriter = true
                });
                var preSpeechFrames = new Queue<byte[]>();
                bool speechActive = false;
                bool captureWasSuspended = false;
                int trailingSilenceFrames = 0;
                Task? receiveTask = null;
                Task? sendTask = null;

                var webSocket = new ClientWebSocket();
                webSocket.Options.Proxy = null;
                webSocket.Options.SetRequestHeader("Authorization", "bearer " + _apiKey);
                _webSocket = webSocket;

                var waveIn = new WaveInEvent
                {
                    WaveFormat = new WaveFormat(16000, 16, 1),
                    BufferMilliseconds = 100
                };
                _waveIn = waveIn;

                waveIn.DataAvailable += (s, e) =>
                {
                    if (ct.IsCancellationRequested) return;
                    if (CaptureSuspended)
                    {
                        if (!captureWasSuspended)
                        {
                            while (audioChannel.Reader.TryRead(out _)) { }
                            speechActive = false;
                            trailingSilenceFrames = 0;
                            preSpeechFrames.Clear();
                        }
                        captureWasSuspended = true;
                        return;
                    }
                    captureWasSuspended = false;

                    float level = ComputeRms(e.Buffer, e.BytesRecorded);
                    ReportAudioLevel(level);
                    float enterThreshold = Math.Max(_energyThreshold, _noiseFloor + SpeechMargin);
                    float exitThreshold = Math.Max(_energyThreshold * 0.6f, _noiseFloor + SilenceMargin);

                    var frame = new byte[e.BytesRecorded];
                    Array.Copy(e.Buffer, frame, e.BytesRecorded);

                    if (!speechActive)
                    {
                        if (level >= enterThreshold)
                        {
                            speechActive = true;
                            trailingSilenceFrames = 0;
                            while (preSpeechFrames.Count > 0)
                                audioChannel.Writer.TryWrite(preSpeechFrames.Dequeue());
                            audioChannel.Writer.TryWrite(frame);
                        }
                        else
                        {
                            if (_noiseFloor <= 0 || level < _noiseFloor) _noiseFloor = level;
                            else _noiseFloor += (level - _noiseFloor) * 0.002f;

                            preSpeechFrames.Enqueue(frame);
                            while (preSpeechFrames.Count > PreSpeechFrameCount) preSpeechFrames.Dequeue();
                        }
                        return;
                    }

                    audioChannel.Writer.TryWrite(frame);
                    trailingSilenceFrames = level < exitThreshold ? trailingSilenceFrames + 1 : 0;
                    if (trailingSilenceFrames >= TrailingSilenceFrameCount)
                    {
                        speechActive = false;
                        trailingSilenceFrames = 0;
                        preSpeechFrames.Clear();
                    }
                };

                try
                {
                    waveIn.StartRecording();
                    await webSocket.ConnectAsync(new Uri(WsUrl), ct);
                    _logger.LogInformation("百炼 WebSocket 连接成功");

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
                    await SendTextAsync(webSocket, JsonConvert.SerializeObject(runTask), ct);

                    receiveTask = Task.Run(() => ReceiveLoop(webSocket, taskId, taskStarted, sessionOver, ct), ct);
                    using (var startTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
                    using (ct.Register(() => startTimeout.Cancel()))
                    {
                        var done = await Task.WhenAny(taskStarted.Task, Task.Delay(Timeout.Infinite, startTimeout.Token));
                        if (done != taskStarted.Task || !await taskStarted.Task)
                            throw new Exception("等待 task-started 超时或服务端启动失败(检查 API Key 与网络)");
                    }

                    if (audioChannel.Reader.Count > 0)
                        _logger.LogInformation($"会话就绪,补发缓存的 {audioChannel.Reader.Count} 帧音频");
                    sendTask = Task.Run(() => SendAudioLoopAsync(webSocket, audioChannel.Reader, ct), ct);
                    _logger.LogInformation("百炼实时语音识别已启动,开始录音");

                    await Task.WhenAny(sessionOver.Task, Task.Delay(Timeout.Infinite, ct));
                    ct.ThrowIfCancellationRequested();
                }
                finally
                {
                    try { waveIn.StopRecording(); } catch { }
                    try { waveIn.Dispose(); } catch { }
                    if (ReferenceEquals(_waveIn, waveIn)) _waveIn = null;

                    audioChannel.Writer.TryComplete();
                    if (sendTask != null)
                    {
                        try { await sendTask; } catch { }
                    }

                    try
                    {
                        if (webSocket.State == WebSocketState.Open)
                        {
                            var finish = new
                            {
                                header = new { action = "finish-task", task_id = taskId, streaming = "duplex" },
                                payload = new { input = new { } }
                            };
                            await SendTextAsync(webSocket, JsonConvert.SerializeObject(finish), CancellationToken.None);
                        }
                    }
                    catch { }

                    try { webSocket.Dispose(); } catch { }
                    if (ReferenceEquals(_webSocket, webSocket)) _webSocket = null;
                    if (receiveTask != null)
                    {
                        try { await receiveTask; } catch { }
                    }
                }
            }
            finally
            {
                _sessionLock.Release();
            }
        }

        private async Task SendAudioLoopAsync(ClientWebSocket webSocket, ChannelReader<byte[]> reader,
            CancellationToken ct)
        {
            try
            {
                await foreach (var frame in reader.ReadAllAsync(ct))
                    await SendAudioFrameAsync(webSocket, frame, ct);
            }
            catch (OperationCanceledException) { }
        }

        /// <summary>发送一帧音频(16bit PCM)。</summary>
        private async Task SendAudioFrameAsync(ClientWebSocket ws, byte[] frame, CancellationToken ct)
        {
            try
            {
                await _sendLock.WaitAsync(ct);
                try
                {
                    await ws.SendAsync(new ArraySegment<byte>(frame), WebSocketMessageType.Binary, true, ct);
                }
                finally { _sendLock.Release(); }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogDebug($"发送音频帧失败: {ex.Message}");
            }
        }

        private async Task ReceiveLoop(ClientWebSocket webSocket, string taskId,
            TaskCompletionSource<bool> taskStarted,
            TaskCompletionSource<bool> sessionOver,
            CancellationToken ct)
        {
            var buffer = new byte[16384];
            var messageBuf = new MemoryStream();

            try
            {
                while (webSocket.State == WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    messageBuf.SetLength(0);
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
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
                                PartialTextRecognized?.Invoke(this, text);
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

        private async Task SendTextAsync(ClientWebSocket webSocket, string json, CancellationToken ct)
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            await _sendLock.WaitAsync(ct);
            try
            {
                await webSocket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
            }
            finally { _sendLock.Release(); }
        }

        /// <summary>计算 16bit PCM 的归一化 RMS 电平(0~1)。</summary>
        private static float ComputeRms(byte[] buffer, int count)
        {
            if (count < 2) return 0f;
            long sum = 0;
            int samples = count / 2;
            for (int i = 0; i < count - 1; i += 2)
            {
                short s = (short)(buffer[i] | (buffer[i + 1] << 8));
                sum += (long)s * s;
            }
            return (float)Math.Sqrt(sum / (double)samples) / short.MaxValue;
        }

        private void ReportAudioLevel(float level)
        {
            var handler = AudioLevelChanged;
            if (handler == null) return;
            handler(this, new AudioLevelEventArgs { Level = level });
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

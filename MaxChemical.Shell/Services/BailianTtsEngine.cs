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
    /// 阿里云百炼(DashScope)CosyVoice 语音合成引擎。
    /// 协议:wss://dashscope.aliyuncs.com/api-ws/v1/inference,Bearer ApiKey 鉴权;
    /// run-task → continue-task(文本) → finish-task,二进制帧为 PCM 音频,边收边播。
    /// 绕过系统代理;新一次播报自动打断上一次。
    /// </summary>
    public class BailianTtsEngine : ITtsEngine
    {
        private const string WsUrl = "wss://dashscope.aliyuncs.com/api-ws/v1/inference";

        private readonly ILogService _logger;
        private readonly string _apiKey;
        private readonly string _model;
        private readonly string _voice;
        private readonly int _sampleRate;

        private readonly object _lock = new();
        private CancellationTokenSource? _currentCts;
        private WaveOutEvent? _waveOut;
        private float _rate = 1.0f;

        public string EngineName => "百炼 CosyVoice";
        public bool IsAvailable => !string.IsNullOrWhiteSpace(_apiKey);

        public BailianTtsEngine(ILogService logger, IConfiguration configuration)
        {
            _logger = logger;

            _apiKey = configuration["Bailian:ApiKey"];
            if (string.IsNullOrWhiteSpace(_apiKey))
                _apiKey = configuration["DashScope:ApiKey"];
            if (string.IsNullOrWhiteSpace(_apiKey))
                _apiKey = Environment.GetEnvironmentVariable("DASHSCOPE_API_KEY") ?? "";

            _model = string.IsNullOrWhiteSpace(configuration["Bailian:TtsModel"])
                ? "cosyvoice-v1" : configuration["Bailian:TtsModel"];
            _voice = string.IsNullOrWhiteSpace(configuration["Bailian:TtsVoice"])
                ? "longxiaochun" : configuration["Bailian:TtsVoice"];
            _sampleRate = int.TryParse(configuration["Bailian:TtsSampleRate"], out int sr) ? sr : 22050;

            _logger.LogInformation(IsAvailable
                ? $"[{EngineName}] 就绪:模型 {_model},音色 {_voice},{_sampleRate}Hz"
                : $"[{EngineName}] 未配置 API Key(Bailian:ApiKey),不可用");
        }

        public void Speak(string text)
        {
            var task = SpeakCoreAsync(text);
            try { task.Wait(TimeSpan.FromSeconds(90)); }
            catch (Exception ex) { _logger.LogError($"[{EngineName}] 同步播报失败: {ex.Message}"); }
        }

        public void SpeakAsync(string text)
        {
            _ = SpeakCoreAsync(text);
        }

        private async Task SpeakCoreAsync(string text)
        {
            if (!IsAvailable || string.IsNullOrWhiteSpace(text)) return;

            CancellationTokenSource cts;
            lock (_lock)
            {
                StopCurrentLocked();
                _currentCts = new CancellationTokenSource();
                cts = _currentCts;
            }
            var ct = cts.Token;

            try
            {
                await SynthesizeAndPlayAsync(text, ct);
            }
            catch (OperationCanceledException) { /* 被新播报/停止打断,正常 */ }
            catch (Exception ex)
            {
                _logger.LogError($"[{EngineName}] 合成播放失败: {ex.Message}");
            }
        }

        private async Task SynthesizeAndPlayAsync(string text, CancellationToken ct)
        {
            string taskId = Guid.NewGuid().ToString("N");

            using var ws = new ClientWebSocket();
            ws.Options.Proxy = null; // 绕过系统代理
            ws.Options.SetRequestHeader("Authorization", "bearer " + _apiKey);
            await ws.ConnectAsync(new Uri(WsUrl), ct);

            // 1) run-task:声明合成任务
            var runTask = new
            {
                header = new { action = "run-task", task_id = taskId, streaming = "duplex" },
                payload = new
                {
                    task_group = "audio",
                    task = "tts",
                    function = "SpeechSynthesizer",
                    model = _model,
                    parameters = new
                    {
                        text_type = "PlainText",
                        voice = _voice,
                        format = "pcm",
                        sample_rate = _sampleRate,
                        volume = 50,
                        rate = _rate,
                        pitch = 1.0
                    },
                    input = new { }
                }
            };
            await SendJsonAsync(ws, runTask, ct);

            // 2) 播放管道:PCM 16bit 单声道,边收边播
            var format = new WaveFormat(_sampleRate, 16, 1);
            var provider = new BufferedWaveProvider(format)
            {
                BufferLength = 16 * 1024 * 1024,   // ≈ 6 分钟音频,足够单次播报
                DiscardOnBufferOverflow = true
            };

            WaveOutEvent waveOut;
            lock (_lock)
            {
                _waveOut = new WaveOutEvent();
                waveOut = _waveOut;
            }
            waveOut.Init(provider);
            bool playing = false;
            bool taskStartedSent = false;
            bool finished = false;

            var buffer = new byte[32768];
            var messageBuf = new MemoryStream();

            var deadline = DateTime.UtcNow.AddSeconds(60); // 合成阶段兜底超时

            while (!finished && !ct.IsCancellationRequested)
            {
                if (DateTime.UtcNow > deadline)
                    throw new TimeoutException("合成超时");

                messageBuf.SetLength(0);
                WebSocketReceiveResult result;
                do
                {
                    result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                    messageBuf.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                if (result.MessageType == WebSocketMessageType.Close)
                    break;

                if (result.MessageType == WebSocketMessageType.Binary)
                {
                    // 音频帧 → 播放缓冲
                    var chunk = messageBuf.ToArray();
                    provider.AddSamples(chunk, 0, chunk.Length);
                    if (!playing)
                    {
                        waveOut.Play();
                        playing = true;
                    }
                    continue;
                }

                // 文本帧:任务事件
                string json = Encoding.UTF8.GetString(messageBuf.ToArray());
                var msg = JObject.Parse(json);
                string evt = (string?)msg["header"]?["event"] ?? "";

                switch (evt)
                {
                    case "task-started":
                        if (!taskStartedSent)
                        {
                            taskStartedSent = true;
                            // 3) 送文本 + 结束标记
                            var cont = new
                            {
                                header = new { action = "continue-task", task_id = taskId, streaming = "duplex" },
                                payload = new { input = new { text } }
                            };
                            await SendJsonAsync(ws, cont, ct);

                            var fin = new
                            {
                                header = new { action = "finish-task", task_id = taskId, streaming = "duplex" },
                                payload = new { input = new { } }
                            };
                            await SendJsonAsync(ws, fin, ct);
                        }
                        break;

                    case "task-finished":
                        finished = true;
                        break;

                    case "task-failed":
                        throw new Exception($"合成失败: {(string?)msg["header"]?["error_code"]} {(string?)msg["header"]?["error_message"]}");
                }
            }

            ct.ThrowIfCancellationRequested();

            // 4) 音频帧收完,等播放缓冲耗尽
            if (playing)
            {
                while (!ct.IsCancellationRequested &&
                       provider.BufferedBytes > 0 &&
                       waveOut.PlaybackState == PlaybackState.Playing)
                {
                    await Task.Delay(100, ct);
                }
            }

            try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None); } catch { }

            lock (_lock)
            {
                if (_waveOut == waveOut)
                {
                    try { waveOut.Stop(); waveOut.Dispose(); } catch { }
                    _waveOut = null;
                }
            }
        }

        private static async Task SendJsonAsync(ClientWebSocket ws, object obj, CancellationToken ct)
        {
            var bytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(obj));
            await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
        }

        public void StopSpeaking()
        {
            lock (_lock) StopCurrentLocked();
        }

        private void StopCurrentLocked()
        {
            try { _currentCts?.Cancel(); } catch { }
            _currentCts = null;
            try { _waveOut?.Stop(); _waveOut?.Dispose(); } catch { }
            _waveOut = null;
        }

        public void SetSpeakingRate(float rate)
        {
            _rate = Math.Clamp(rate, 0.5f, 2.0f);
        }

        public void Dispose()
        {
            StopSpeaking();
            _logger.LogInformation($"[{EngineName}] 已释放");
        }
    }
}

using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using MaxChemical.Infrastructure.Events;
using MaxChemical.Logging;
using MaxChemical.Shell.Models;
using Microsoft.Extensions.Configuration;
using Prism.Events;

namespace MaxChemical.Shell.Services
{
    /// <summary>
    /// 基于阿里云的语音助手服务
    /// </summary>
    public class PiperVoiceAssistantService : IVoiceAssistantService, IDisposable
    {
        private readonly ILogService _logger;
        private readonly IEventAggregator _eventAggregator;
        private readonly IConfiguration _configuration;
        private readonly ITtsEngine _ttsEngine;
        private readonly OllamaLlmService _llmService;
        private readonly AliyunRealtimeAsrEngine _aliyunEngine;  // ← 改为阿里云引擎

        private bool _isListening;
        private bool _isAwake;
        private bool _isSpeechEnabled = true;
        private Timer? _autoSleepTimer;
        private readonly int _autoSleepSeconds = 30;

        private CancellationTokenSource? _recognitionCts;

        public bool IsListening => _isListening;

        public bool IsSpeechEnabled
        {
            get => _isSpeechEnabled;
            set => _isSpeechEnabled = value;
        }

        public event EventHandler<bool>? ListeningStateChanged;
        public event EventHandler<string>? CommandRecognized;
        public event EventHandler<string>? SpeechStarted;

        public PiperVoiceAssistantService(
            IEventAggregator eventAggregator,
            IConfiguration configuration)
        {
            _logger = LogManager.GetLogger<PiperVoiceAssistantService>();
            _eventAggregator = eventAggregator;
            _configuration = configuration;

            _llmService = new OllamaLlmService(_logger);
            _ttsEngine = CreateTtsEngine();
            _aliyunEngine = new AliyunRealtimeAsrEngine(_logger, _configuration);  // ← 改为阿里云引擎

            InitializeAliyunAsr();  // ← 改为阿里云初始化
        }

        private ITtsEngine CreateTtsEngine()
        {
            var voiceSettings = _configuration.GetSection("VoiceSettings").Get<VoiceSettings>()
                               ?? new VoiceSettings();

            _logger.LogInformation($"配置的TTS提供商: {voiceSettings.TtsProvider}");

            ITtsEngine engine = voiceSettings.TtsProvider.ToLower() switch
            {
                "edge" => new EdgeTtsEngine(_logger, voiceSettings.EdgeTts),
                "azure" => new AzureTtsEngine(_logger, voiceSettings.AzureTts),
                "piper" => new PiperTtsEngine(_logger, voiceSettings.Piper),
                "sherpa" => new SherpaOnnxTtsEngine(_logger, voiceSettings.SherpaOnnx),
                _ => new SherpaOnnxTtsEngine(_logger, voiceSettings.SherpaOnnx)
            };

            if (!engine.IsAvailable)
            {
                _logger.LogWarning($"{engine.EngineName} 不可用，尝试降级方案");

                if (voiceSettings.TtsProvider.ToLower() == "edge")
                {
                    _logger.LogInformation("切换到 Piper TTS");
                    engine.Dispose();
                    engine = new PiperTtsEngine(_logger, voiceSettings.Piper);

                    if (!engine.IsAvailable)
                    {
                        _logger.LogInformation("切换到 Azure TTS");
                        engine.Dispose();
                        engine = new AzureTtsEngine(_logger, voiceSettings.AzureTts);
                    }
                }
                else if (voiceSettings.TtsProvider.ToLower() == "azure")
                {
                    _logger.LogInformation("切换到 Piper TTS");
                    engine.Dispose();
                    engine = new PiperTtsEngine(_logger, voiceSettings.Piper);
                }
            }

            _logger.LogInformation($"使用 TTS 引擎: {engine.EngineName}");
            return engine;
        }

        // ← 改为阿里云初始化
        private void InitializeAliyunAsr()
        {
            try
            {
                _aliyunEngine.Initialize();
                _aliyunEngine.TextRecognized += OnAliyunTextRecognized;  // ← 改事件名
                _aliyunEngine.AudioLevelChanged += OnAudioLevelChanged;
                _logger.LogInformation("阿里云语音识别引擎初始化成功");
            }
            catch (Exception ex)
            {
                _logger.LogError($"初始化阿里云引擎失败: {ex.Message}");
                throw;
            }
        }

        private void OnAudioLevelChanged(object? sender, AudioLevelEventArgs e)
        {
            if (e.Level > 0.01f)
            {
                _logger.LogDebug($"音频电平: {e.Level:F4}");
            }
        }

        public void Start()
        {
            if (_isListening)
            {
                _logger.LogWarning("语音助手已在运行中");
                return;
            }

            try
            {
                _recognitionCts = new CancellationTokenSource();

                // ← 改为阿里云引擎启动
                Task.Run(async () => await _aliyunEngine.StartContinuousRecognitionAsync(_recognitionCts.Token));

                _isListening = true;
                _isAwake = false;

                ListeningStateChanged?.Invoke(this, true);
                _logger.LogInformation("语音助手已启动（使用阿里云实时语音识别）");
            }
            catch (Exception ex)
            {
                _logger.LogError($"启动语音助手失败: {ex.Message}");
                throw;
            }
        }

        public void Stop()
        {
            if (!_isListening)
            {
                _logger.LogWarning("语音助手未在运行");
                return;
            }

            try
            {
                _recognitionCts?.Cancel();
                _recognitionCts?.Dispose();
                _recognitionCts = null;

                _isListening = false;
                _isAwake = false;

                StopAutoSleepTimer();
                StopSpeaking();

                ListeningStateChanged?.Invoke(this, false);
                _logger.LogInformation("语音助手已停止");
            }
            catch (Exception ex)
            {
                _logger.LogError($"停止语音助手失败: {ex.Message}");
            }
        }

        // ← 改事件处理器名称
        private void OnAliyunTextRecognized(object? sender, WhisperTextRecognizedEventArgs e)
        {
            var text = e.Text.Trim();

            _logger.LogInformation($"========================================");
            _logger.LogInformation($"阿里云识别结果: {text}");
            _logger.LogInformation($"置信度: {e.Confidence:P}");
            _logger.LogInformation($"========================================");

            if (string.IsNullOrWhiteSpace(text))
            {
                _logger.LogDebug("识别结果为空，忽略");
                return;
            }

            if (ContainsWakeUpPhrase(text))
            {
                HandleWakeUp(text);
                return;
            }

            if (_isAwake)
            {
                Task.Run(async () => await HandleCommandAsync(text));
            }
            else
            {
                _logger.LogInformation($"未唤醒状态，请先说唤醒词（如：小桐）");
            }
        }

        private bool ContainsWakeUpPhrase(string text)
        {
            var wakeUpPhrases = new[]
            {
                // 基础唤醒词
                "小桐", "小童", "小通", "晓桐", "晓童", "小彤", "晓彤",
                
                // 带问候语
                "你好小桐", "嘿小桐", "喂小桐", "哎小桐",
                "小桐你好", "小桐在吗", "小桐醒醒", "小桐帮忙",
                
                // 带称呼
                "小桐助手", "小桐同学", "小桐老师", "桐桐", "桐宝",
                
                // 常见误识别
                "消停", "小停", "小亭", "小婷", "晓停",
                "消同", "肖桐", "萧桐",
                
                // 英文或混合
                "hey桐", "hi小桐", "hello小桐",
                
                // 方言/口音变体
                "小东", "小冬", "小同",
                
                // 紧急/强调
                "小桐小桐", "快来小桐", "小桐快来",
                "救命小桐", "帮帮忙小桐",
                
                // 简短指令型
                "桐", "醒醒", "在吗", "听着",
                
                // 带动作
                "小桐启动", "小桐开始", "小桐工作", "小桐听令",
                
                // 亲昵称呼
                "桐宝宝", "小桐宝贝", "桐桐宝", "我的小桐",
                
                // 职能相关
                "语音助手", "助手启动", "AI助手",
                "智能助手", "开始工作", "开始服务"
            };

            bool found = wakeUpPhrases.Any(phrase =>
                text.IndexOf(phrase, StringComparison.OrdinalIgnoreCase) >= 0);

            if (found)
            {
                _logger.LogInformation($" 检测到唤醒词: {text}");
            }

            return found;
        }

        private void HandleWakeUp(string text)
        {
            if (!_isAwake)
            {
                _isAwake = true;
                _logger.LogInformation("语音助手已唤醒");
                CommandRecognized?.Invoke(this, "WAKE_UP");
                SpeakAsync("我在");
                StartAutoSleepTimer();
            }
            else
            {
                ResetAutoSleepTimer();
                SpeakAsync("嗯");
            }
        }

        private async Task HandleCommandAsync(string text)
        {
            _logger.LogInformation($"收到用户指令: {text}");
            _logger.LogInformation("调用大模型进行意图理解...");

            ResetAutoSleepTimer();
            CommandRecognized?.Invoke(this, text);

            var intent = await _llmService.UnderstandCommand(text);

            if (intent == null || string.IsNullOrEmpty(intent.Action))
            {
                _logger.LogWarning("大模型未能理解用户意图");
                SpeakAsync("抱歉，我没听明白");
                return;
            }

            _logger.LogInformation($"意图识别成功: {intent.Action}");

            switch (intent.Action)
            {
                case "CreateDevice":
                    HandleCreateDeviceIntent(intent);
                    break;

                case "SetParameter":
                    HandleSetParameterIntent(intent);
                    break;

                case "ControlState":
                    HandleControlStateIntent(intent);
                    break;

                case "SystemCommand":
                    HandleSystemCommandIntent(intent);
                    break;

                case "Query":
                    HandleQueryIntent(intent);
                    break;

                case "Sleep":
                    HandleSleepIntent();
                    break;

                case "Chat":
                    HandleChatIntent(intent);
                    break;

                default:
                    _logger.LogWarning($"未知的意图类型: {intent.Action}");
                    SpeakAsync("抱歉，我还没学会这个操作");
                    break;
            }
        }

        private void HandleCreateDeviceIntent(CommandIntent intent)
        {
            try
            {
                _logger.LogInformation($"执行创建设备操作: {intent.DeviceType}");

                _eventAggregator?.GetEvent<VoiceCreateDeviceEvent>()?.Publish(
                    new VoiceCreateDeviceEventArgs
                    {
                        DeviceName = intent.DeviceType,
                        Command = intent.RawCommand,
                        Timestamp = DateTime.Now
                    });

                SpeakAsync($"正在为您创建{intent.DeviceType}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"创建设备失败: {ex.Message}");
                SpeakAsync("创建设备失败，请稍后重试");
            }
        }

        private void HandleSetParameterIntent(CommandIntent intent)
        {
            try
            {
                if (!intent.ParameterValue.HasValue)
                {
                    SpeakAsync("未能识别参数值");
                    return;
                }

                _logger.LogInformation($"执行参数设置: {intent.DeviceType}.{intent.ParameterName} = {intent.ParameterValue}");

                string actualParamName = MapParameterName(intent.ParameterName, intent.DeviceType);

                _eventAggregator?.GetEvent<VoiceSetDeviceParameterEvent>()?.Publish(
                    new VoiceSetParameterEventArgs
                    {
                        DeviceType = intent.DeviceType,
                        ParameterName = actualParamName,
                        ParameterValue = intent.ParameterValue.Value,
                        Command = intent.RawCommand,
                        Timestamp = DateTime.Now
                    });

                SpeakAsync($"好的,正在设置{intent.DeviceType}{intent.ParameterName}为{intent.ParameterValue.Value}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"设置参数失败: {ex.Message}");
                SpeakAsync("设置参数失败");
            }
        }

        private void HandleControlStateIntent(CommandIntent intent)
        {
            try
            {
                if (!intent.TargetState.HasValue)
                {
                    SpeakAsync("未能识别目标状态");
                    return;
                }

                _logger.LogInformation($"执行状态控制: {intent.DeviceType} -> {(intent.TargetState.Value ? "开启" : "关闭")}");

                _eventAggregator?.GetEvent<VoiceControlDeviceStateEvent>()?.Publish(
                    new VoiceControlStateEventArgs
                    {
                        DeviceType = intent.DeviceType,
                        TargetState = intent.TargetState.Value,
                        Command = intent.RawCommand,
                        Timestamp = DateTime.Now
                    });

                string action = intent.TargetState.Value ? "启动" : "停止";
                if (intent.DeviceType == "电磁阀")
                {
                    action = intent.TargetState.Value ? "打开" : "关闭";
                }

                SpeakAsync($"好的,正在{action}{intent.DeviceType}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"控制状态失败: {ex.Message}");
                SpeakAsync("操作失败");
            }
        }

        private void HandleSystemCommandIntent(CommandIntent intent)
        {
            _logger.LogInformation($"执行系统命令: {intent.SystemCommand}");

            string response = intent.SystemCommand switch
            {
                "maximize" => "好的，已经最大化了",
                "minimize" => "好的，已经最小化了",
                "restore" => "窗口已还原",
                "close" => "再见，下次再聊",
                "settings" => "正在为您打开设置",
                "home" => "正在打开首页",
                _ => "好的"
            };

            SpeakAsync(response);
            CommandRecognized?.Invoke(this, intent.SystemCommand);
        }

        private void HandleQueryIntent(CommandIntent intent)
        {
            string response = intent.QueryType switch
            {
                "time" => $"现在是{DateTime.Now:HH点mm分}",
                "date" => $"今天是{DateTime.Now:yyyy年M月d日}",
                "weekday" => $"今天是星期{GetChineseWeekDay(DateTime.Now.DayOfWeek)}",
                _ => "抱歉，我不知道"
            };

            SpeakAsync(response);
        }

        private void HandleSleepIntent()
        {
            _isAwake = false;
            StopAutoSleepTimer();
            CommandRecognized?.Invoke(this, "SLEEP");
            SpeakAsync("好的，需要的时候叫我哦");
            _logger.LogInformation("语音助手已进入休眠状态");
        }

        private void HandleChatIntent(CommandIntent intent)
        {
            if (!string.IsNullOrEmpty(intent.Response))
            {
                SpeakAsync(intent.Response);
            }
            else
            {
                SpeakAsync("嗯嗯，我在听");
            }
        }

        private string MapParameterName(string llmParamName, string deviceType)
        {
            var mapping = new Dictionary<string, Dictionary<string, string>>
            {
                ["柱塞泵"] = new Dictionary<string, string>
                {
                    ["流量"] = "TargetSpeed",
                    ["速度"] = "TargetSpeed",
                    ["压力"] = "Pressure"
                },
                ["流量计"] = new Dictionary<string, string>
                {
                    ["流量"] = "TargetFlow",
                    ["目标流量"] = "TargetFlow"
                },
                ["换热器"] = new Dictionary<string, string>
                {
                    ["温度"] = "TargetTemperature"
                }
            };

            if (mapping.ContainsKey(deviceType) &&
                mapping[deviceType].ContainsKey(llmParamName))
            {
                return mapping[deviceType][llmParamName];
            }

            _logger.LogWarning($"未找到参数映射: {deviceType}.{llmParamName}，使用原名称");
            return llmParamName;
        }

        private string GetChineseWeekDay(DayOfWeek dayOfWeek)
        {
            return dayOfWeek switch
            {
                DayOfWeek.Monday => "一",
                DayOfWeek.Tuesday => "二",
                DayOfWeek.Wednesday => "三",
                DayOfWeek.Thursday => "四",
                DayOfWeek.Friday => "五",
                DayOfWeek.Saturday => "六",
                DayOfWeek.Sunday => "日",
                _ => ""
            };
        }

        public void Speak(string text)
        {
            if (!_isSpeechEnabled)
            {
                _logger.LogDebug("语音输出已禁用");
                return;
            }

            SpeechStarted?.Invoke(this, text);
            _ttsEngine.Speak(text);
        }

        public void SpeakAsync(string text)
        {
            if (!_isSpeechEnabled)
            {
                _logger.LogDebug("语音输出已禁用");
                return;
            }

            SpeechStarted?.Invoke(this, text);
            _ttsEngine.SpeakAsync(text);
        }

        public void StopSpeaking()
        {
            _ttsEngine.StopSpeaking();
            _logger.LogInformation("已停止语音输出");
        }

        public void SetSpeakingRate(float rate)
        {
            _ttsEngine.SetSpeakingRate(rate);
        }

        private void StartAutoSleepTimer()
        {
            StopAutoSleepTimer();

            _autoSleepTimer = new Timer(
                callback: _ => AutoSleep(),
                state: null,
                dueTime: TimeSpan.FromSeconds(_autoSleepSeconds),
                period: Timeout.InfiniteTimeSpan
            );

            _logger.LogDebug($"启动自动休眠计时器: {_autoSleepSeconds}秒");
        }

        private void StopAutoSleepTimer()
        {
            if (_autoSleepTimer != null)
            {
                _autoSleepTimer.Dispose();
                _autoSleepTimer = null;
            }
        }

        private void ResetAutoSleepTimer()
        {
            if (_isAwake)
            {
                StartAutoSleepTimer();
            }
        }

        private void AutoSleep()
        {
            _logger.LogInformation("触发自动休眠");
            _isAwake = false;
            CommandRecognized?.Invoke(this, "AUTO_SLEEP");
            SpeakAsync("我先休息了，需要时叫我哦");
        }

        public void ExecuteCommand(string command)
        {
            _logger.LogInformation($"手动执行命令: {command}");
            Task.Run(async () => await HandleCommandAsync(command));
        }

        public void Dispose()
        {
            _logger.LogInformation("正在释放语音助手资源");

            Stop();
            StopAutoSleepTimer();

            if (_aliyunEngine != null)
            {
                _aliyunEngine.TextRecognized -= OnAliyunTextRecognized;
                _aliyunEngine.AudioLevelChanged -= OnAudioLevelChanged;
                _aliyunEngine.Dispose();
            }

            _ttsEngine?.Dispose();
            _llmService?.Dispose();

            _logger.LogInformation("语音助手资源释放完成");
        }

        public void TestSpeak(string text = "这是一个测试")
        {
            _logger.LogInformation($"开始测试语音合成: {text}");
            Speak(text);
        }
    }

    // ← 保留这些事件类定义（兼容性）
    public class AudioLevelEventArgs : EventArgs
    {
        public float Level { get; set; }
    }

    public class SpeechDetectedEventArgs : EventArgs
    {
        public bool IsActive { get; set; }
        public float Energy { get; set; }
    }

    public class WhisperTextRecognizedEventArgs : EventArgs
    {
        public string Text { get; set; }
        public DateTime Timestamp { get; set; }
        public float Confidence { get; set; }
        public float EnergyLevel { get; set; }
    }
}
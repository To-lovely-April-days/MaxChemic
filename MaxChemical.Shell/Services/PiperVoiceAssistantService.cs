using System;
using System.Diagnostics;
using System.Speech.Recognition;
using System.Threading;
using MaxChemical.Infrastructure.Events;
using MaxChemical.Logging;
using MaxChemical.Shell.Models;
using Microsoft.Extensions.Configuration;
using Prism.Events;

namespace MaxChemical.Shell.Services
{
    public class PiperVoiceAssistantService : IVoiceAssistantService, IDisposable
    {
        private readonly ILogService _logger;
        private readonly IEventAggregator _eventAggregator;
        private readonly IConfiguration _configuration;
        private readonly ITtsEngine _ttsEngine;

        private SpeechRecognitionEngine? _recognizer;
        private bool _isListening;
        private bool _isAwake;
        private bool _isSpeechEnabled = true;
        private Timer? _autoSleepTimer;
        private readonly int _autoSleepSeconds = 30;

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

            // 🎯 根据配置创建TTS引擎
            _ttsEngine = CreateTtsEngine();

            InitializeRecognizer();
        }

        private ITtsEngine CreateTtsEngine()
        {
            var voiceSettings = _configuration.GetSection("VoiceSettings").Get<VoiceSettings>()
                               ?? new VoiceSettings();

            _logger.LogInformation($"🎤 配置的TTS提供商: {voiceSettings.TtsProvider}");

            ITtsEngine engine = voiceSettings.TtsProvider.ToLower() switch
            {
                "edge" => new EdgeTtsEngine(_logger, voiceSettings.EdgeTts),
                "azure" => new AzureTtsEngine(_logger, voiceSettings.AzureTts),
                "piper" or _ => new PiperTtsEngine(_logger, voiceSettings.Piper)
            };

            if (!engine.IsAvailable)
            {
                _logger.LogWarning($"⚠ {engine.EngineName} 不可用，尝试降级方案");

                // 降级策略: Edge -> Piper -> Azure
                if (voiceSettings.TtsProvider.ToLower() == "edge")
                {
                    _logger.LogInformation("🔄 切换到 Piper TTS");
                    engine.Dispose();
                    engine = new PiperTtsEngine(_logger, voiceSettings.Piper);

                    if (!engine.IsAvailable)
                    {
                        _logger.LogInformation("🔄 切换到 Azure TTS");
                        engine.Dispose();
                        engine = new AzureTtsEngine(_logger, voiceSettings.AzureTts);
                    }
                }
                else if (voiceSettings.TtsProvider.ToLower() == "azure")
                {
                    _logger.LogInformation("🔄 切换到 Piper TTS");
                    engine.Dispose();
                    engine = new PiperTtsEngine(_logger, voiceSettings.Piper);
                }
            }

            _logger.LogInformation($" 使用 TTS 引擎: {engine.EngineName}");
            return engine;
        }

        private void InitializeRecognizer()
        {
            try
            {
                _recognizer = new SpeechRecognitionEngine(new System.Globalization.CultureInfo("zh-CN"));

                var wakeUpGrammar = CreateWakeUpGrammar();
                _recognizer.LoadGrammar(wakeUpGrammar);

                var commandGrammar = CreateCommandGrammar();
                _recognizer.LoadGrammar(commandGrammar);

                _recognizer.SetInputToDefaultAudioDevice();

                _recognizer.SpeechRecognized += OnSpeechRecognized;
                _recognizer.SpeechRecognitionRejected += OnSpeechRejected;
                _recognizer.RecognizeCompleted += OnRecognizeCompleted;

                _logger.LogInformation(" 语音识别引擎初始化成功");
            }
            catch (Exception ex)
            {
                _logger.LogError($" 初始化语音识别引擎失败: {ex.Message}");
                throw;
            }
        }

        private Grammar CreateWakeUpGrammar()
        {
            var choices = new Choices();
            choices.Add("你好小桐");
            choices.Add("小桐");
            choices.Add("嘿小桐");

            var gb = new GrammarBuilder();
            gb.Append(choices);

            var grammar = new Grammar(gb);
            grammar.Name = "WakeUp";

            return grammar;
        }

        private Grammar CreateCommandGrammar()
        {
            var choices = new Choices();
            choices.Add("最大化窗口");
            choices.Add("最小化窗口");
            choices.Add("还原窗口");
            choices.Add("关闭窗口");
            choices.Add("打开首页");
            choices.Add("打开设置");
            choices.Add("返回");
            choices.Add("退出登录");
            choices.Add("切换语言");
            choices.Add("现在几点");
            choices.Add("今天星期几");
            choices.Add("今天日期");
            choices.Add("静音");
            choices.Add("取消静音");
            choices.Add("休眠");
            choices.Add("再见");
            choices.Add("创建一个柱塞泵");
            var gb = new GrammarBuilder();
            gb.Append(choices);

            var grammar = new Grammar(gb);
            grammar.Name = "Command";

            return grammar;
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
                if (_recognizer == null)
                {
                    throw new InvalidOperationException("语音识别引擎未初始化");
                }

                _recognizer.RecognizeAsync(RecognizeMode.Multiple);
                _isListening = true;
                _isAwake = false;

                ListeningStateChanged?.Invoke(this, true);
                _logger.LogInformation(" 语音助手已启动");
            }
            catch (Exception ex)
            {
                _logger.LogError($" 启动语音助手失败: {ex.Message}");
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
                _recognizer?.RecognizeAsyncStop();
                _isListening = false;
                _isAwake = false;

                StopAutoSleepTimer();
                StopSpeaking();

                ListeningStateChanged?.Invoke(this, false);
                _logger.LogInformation(" 语音助手已停止");
            }
            catch (Exception ex)
            {
                _logger.LogError($" 停止语音助手失败: {ex.Message}");
            }
        }

        private void OnSpeechRecognized(object? sender, SpeechRecognizedEventArgs e)
        {
            var confidence = e.Result.Confidence;
            var text = e.Result.Text;
            var grammarName = e.Result.Grammar.Name;

            _logger.LogInformation($"🎤 识别: {text} (置信度: {confidence:P})");

            if (confidence < 0.6)
            {
                _logger.LogWarning($"⚠ 置信度过低，忽略");
                return;
            }

            if (grammarName == "WakeUp")
            {
                HandleWakeUp(text);
            }
            else if (grammarName == "Command" && _isAwake)
            {
                HandleCommand(text);
            }
        }

        private void HandleWakeUp(string text)
        {
            if (!_isAwake)
            {
                _isAwake = true;
                _logger.LogInformation($"语音助手已唤醒");
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

        private void HandleCommand(string command)
        {
            _logger.LogInformation($"⚡ 执行命令: {command}");

            ResetAutoSleepTimer();
            CommandRecognized?.Invoke(this, command);

            //  检查是否是创建设备命令
            if (command.Contains("创建") && command.Contains("柱塞泵"))
            {
                HandleCreateDeviceCommand("柱塞泵");
                return;
            }

            // 检查是否是特定命令
            if (IsSpecificCommand(command))
            {
                string response = GetCommandResponse(command);
                if (!string.IsNullOrEmpty(response))
                {
                    SpeakAsync(response);
                }

                if (command == "休眠" || command == "再见")
                {
                    _isAwake = false;
                    StopAutoSleepTimer();
                    CommandRecognized?.Invoke(this, "SLEEP");
                    _logger.LogInformation("💤 语音助手已休眠");
                }
            }
            else
            {
                // 非特定命令，重复用户说的话
                _logger.LogInformation($" 复述用户指令: {command}");
                SpeakAsync($"您说的是:{command}");
            }
        }

        /// <summary>
        /// 处理创建设备命令
        /// </summary>
        private void HandleCreateDeviceCommand(string deviceName)
        {
            try
            {
                _logger.LogInformation($"🎨 处理创建设备命令: {deviceName}");

                // 发布创建设备事件
                _eventAggregator?.GetEvent<VoiceCreateDeviceEvent>()?.Publish(new VoiceCreateDeviceEventArgs
                {
                    DeviceName = deviceName,
                    Command = $"创建一个{deviceName}",
                    Timestamp = DateTime.Now
                });

                // 语音反馈
                SpeakAsync($"正在为您创建{deviceName}");
            }
            catch (Exception ex)
            {
                _logger.LogError($" 处理创建设备命令失败: {ex.Message}");
                SpeakAsync("创建设备失败,请稍后重试");
            }
        }
        private bool IsSpecificCommand(string command)
        {
            var specificCommands = new[]
            {
        "最大化窗口",
        "最小化窗口",
        "还原窗口",
        "关闭窗口",
        "打开首页",
        "打开设置",
        "返回",
        "退出登录",
        "切换语言",
        "现在几点",
        "今天星期几",
        "今天日期",
        "静音",
        "取消静音",
        "休眠",
        "再见"
    };

            return Array.Exists(specificCommands, cmd => cmd == command);
        }
        private string GetCommandResponse(string command)
        {
            return command switch
            {
                "最大化窗口" => "好的，已经最大化了",
                "最小化窗口" => "好的，已经最小化了",
                "还原窗口" => "窗口已还原",
                "关闭窗口" => "再见，下次再聊",
                "打开首页" => "正在打开首页",
                "打开设置" => "正在为您打开设置",
                "返回" => "好的",
                "退出登录" => "正在退出登录",
                "切换语言" => "正在切换语言",
                "现在几点" => $"现在是{DateTime.Now:HH点mm分}",
                "今天星期几" => $"今天是星期{GetChineseWeekDay(DateTime.Now.DayOfWeek)}",
                "今天日期" => $"今天是{DateTime.Now:yyyy年M月d日}",
                "静音" => "好的，我先不说话了",
                "取消静音" => "好的，我又回来啦",
                "休眠" => "好的，需要的时候叫我哦",
                "创建一个柱塞泵" => "好的，正在为您创建柱塞泵",
                "再见" => "再见，有需要随时叫我",
                _ => "好的"
            };
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

        #region 语音合成方法

        public void Speak(string text)
        {
            if (!_isSpeechEnabled)
            {
                _logger.LogDebug($"🔇 语音已禁用");
                return;
            }

            SpeechStarted?.Invoke(this, text);
            _ttsEngine.Speak(text);
        }

        public void SpeakAsync(string text)
        {
            if (!_isSpeechEnabled)
            {
                _logger.LogDebug($"🔇 语音已禁用");
                return;
            }

            SpeechStarted?.Invoke(this, text);
            _ttsEngine.SpeakAsync(text);
        }

        public void StopSpeaking()
        {
            _ttsEngine.StopSpeaking();
            _logger.LogInformation("⏹ 已停止语音输出");
        }

        public void SetSpeakingRate(float rate)
        {
            _ttsEngine.SetSpeakingRate(rate);
        }

        #endregion

        #region 自动休眠

        private void StartAutoSleepTimer()
        {
            StopAutoSleepTimer();

            _autoSleepTimer = new Timer(
                callback: _ => AutoSleep(),
                state: null,
                dueTime: TimeSpan.FromSeconds(_autoSleepSeconds),
                period: Timeout.InfiniteTimeSpan
            );

            _logger.LogDebug($"⏰ 自动休眠计时: {_autoSleepSeconds}秒");
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
            _logger.LogInformation("💤 自动休眠触发");
            _isAwake = false;
            CommandRecognized?.Invoke(this, "AUTO_SLEEP");
            SpeakAsync("我先休息了，需要时叫我哦");
        }

        #endregion

        private void OnSpeechRejected(object? sender, SpeechRecognitionRejectedEventArgs e)
        {
            _logger.LogDebug(" 语音识别被拒绝");
        }

        private void OnRecognizeCompleted(object? sender, RecognizeCompletedEventArgs e)
        {
            if (e.Error != null)
            {
                _logger.LogError($" 语音识别错误: {e.Error.Message}");
            }
        }

        public void ExecuteCommand(string command)
        {
            _logger.LogInformation($"⚡ 手动执行: {command}");
            HandleCommand(command);
        }

        public void Dispose()
        {
            _logger.LogInformation("正在释放语音助手资源");

            Stop();
            StopAutoSleepTimer();
            _ttsEngine?.Dispose();

            if (_recognizer != null)
            {
                _recognizer.SpeechRecognized -= OnSpeechRecognized;
                _recognizer.SpeechRecognitionRejected -= OnSpeechRejected;
                _recognizer.RecognizeCompleted -= OnRecognizeCompleted;
                _recognizer.Dispose();
                _recognizer = null;
            }

            _logger.LogInformation("语音助手资源释放完成");
        }

        /// <summary>
        /// 测试语音功能
        /// </summary>
        public void TestSpeak(string text = "这是一个测试")
        {
            _logger.LogInformation($"🧪 开始测试语音: {text}");
            Speak(text);
        }
    }
}
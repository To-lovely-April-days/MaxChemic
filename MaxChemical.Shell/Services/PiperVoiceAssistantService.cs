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
                "piper" => new PiperTtsEngine(_logger, voiceSettings.Piper),
                "sherpa" => new SherpaOnnxTtsEngine(_logger, voiceSettings.SherpaOnnx),
                _ => new SherpaOnnxTtsEngine(_logger, voiceSettings.SherpaOnnx) // 默认使用 Sherpa
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
            // 设备创建命令
            choices.Add("创建一个柱塞泵");
            choices.Add("创建一个流量计");
            choices.Add("创建一个电磁阀");

            // ========== 新增：参数设置命令 ==========
            // 柱塞泵参数设置
            choices.Add("设置柱塞泵流量为10");
            choices.Add("设置柱塞泵流量为20");
            choices.Add("设置柱塞泵流量为30");
            choices.Add("设置柱塞泵流量为50");
            choices.Add("设置柱塞泵流量为100");

            choices.Add("设置柱塞泵速度为10");
            choices.Add("设置柱塞泵速度为20");
            choices.Add("设置柱塞泵速度为30");
            choices.Add("设置柱塞泵速度为50");
            choices.Add("设置柱塞泵速度为100");

            // 流量计参数设置
            choices.Add("设置流量计目标流量为10");
            choices.Add("设置流量计目标流量为20");
            choices.Add("设置流量计目标流量为30");
            choices.Add("设置流量计目标流量为50");

            // 换热器参数设置
            choices.Add("设置换热器温度为50");
            choices.Add("设置换热器温度为80");
            choices.Add("设置换热器温度为100");

            // 设备状态控制
            choices.Add("启动柱塞泵");
            choices.Add("停止柱塞泵");
            choices.Add("打开电磁阀");
            choices.Add("关闭电磁阀");
            choices.Add("启动流量计");
            choices.Add("停止流量计");
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

            // ========== 新增：设备创建命令处理 ==========
            if (command.Contains("创建") && command.Contains("柱塞泵"))
            {
                HandleCreateDeviceCommand("柱塞泵");
                return;
            }

            if (command.Contains("创建") && command.Contains("流量计"))
            {
                HandleCreateDeviceCommand("流量计");
                return;
            }

            if (command.Contains("创建") && command.Contains("电磁阀"))
            {
                HandleCreateDeviceCommand("电磁阀");
                return;
            }

            // ========== 新增：参数设置命令处理 ==========
            if (command.Contains("设置") && command.Contains("柱塞泵"))
            {
                HandlePumpParameterCommand(command);
                return;
            }

            if (command.Contains("设置") && command.Contains("流量计"))
            {
                HandleFlowMeterParameterCommand(command);
                return;
            }

            if (command.Contains("设置") && command.Contains("换热器"))
            {
                HandleHeatExchangerParameterCommand(command);
                return;
            }

            // ========== 新增：设备状态控制命令 ==========
            if (command.Contains("启动") || command.Contains("停止") ||
                command.Contains("打开") || command.Contains("关闭"))
            {
                HandleDeviceStateControlCommand(command);
                return;
            }

            // 原有的特定命令处理
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
                _logger.LogInformation($"🔁 复述用户指令: {command}");
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

        // ========== 新增：柱塞泵参数设置处理 ==========
        private void HandlePumpParameterCommand(string command)
        {
            try
            {
                _logger.LogInformation($"🎛 处理柱塞泵参数命令: {command}");

                // 提取参数值
                double? parameterValue = ExtractNumberFromCommand(command);
                if (parameterValue == null)
                {
                    SpeakAsync("未能识别参数值");
                    return;
                }

                string parameterName;
                string parameterType;

                if (command.Contains("流量"))
                {
                    parameterName = "TargetSpeed"; // 柱塞泵的参数名
                    parameterType = "流量";
                }
                else if (command.Contains("速度"))
                {
                    parameterName = "TargetSpeed";
                    parameterType = "速度";
                }
                else
                {
                    SpeakAsync("未能识别参数类型");
                    return;
                }

                // 发布设置参数事件
                _eventAggregator?.GetEvent<VoiceSetDeviceParameterEvent>()?.Publish(new VoiceSetParameterEventArgs
                {
                    DeviceType = "柱塞泵",
                    ParameterName = parameterName,
                    ParameterValue = parameterValue.Value,
                    Command = command,
                    Timestamp = DateTime.Now
                });

                SpeakAsync($"好的,正在设置柱塞泵{parameterType}为{parameterValue.Value}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"❌ 处理柱塞泵参数命令失败: {ex.Message}");
                SpeakAsync("设置参数失败");
            }
        }

        // ========== 新增:流量计参数设置处理 ==========
        private void HandleFlowMeterParameterCommand(string command)
        {
            try
            {
                _logger.LogInformation($"🎛 处理流量计参数命令: {command}");

                double? targetFlow = ExtractNumberFromCommand(command);
                if (targetFlow == null)
                {
                    SpeakAsync("未能识别流量值");
                    return;
                }

                _eventAggregator?.GetEvent<VoiceSetDeviceParameterEvent>()?.Publish(new VoiceSetParameterEventArgs
                {
                    DeviceType = "流量计",
                    ParameterName = "TargetFlow",
                    ParameterValue = targetFlow.Value,
                    Command = command,
                    Timestamp = DateTime.Now
                });

                SpeakAsync($"好的,正在设置流量计目标流量为{targetFlow.Value}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"❌ 处理流量计参数命令失败: {ex.Message}");
                SpeakAsync("设置参数失败");
            }
        }

        // ========== 新增:换热器参数设置处理 ==========
        private void HandleHeatExchangerParameterCommand(string command)
        {
            try
            {
                _logger.LogInformation($"🎛 处理换热器参数命令: {command}");

                double? temperature = ExtractNumberFromCommand(command);
                if (temperature == null)
                {
                    SpeakAsync("未能识别温度值");
                    return;
                }

                _eventAggregator?.GetEvent<VoiceSetDeviceParameterEvent>()?.Publish(new VoiceSetParameterEventArgs
                {
                    DeviceType = "换热器",
                    ParameterName = "TargetTemperature",
                    ParameterValue = temperature.Value,
                    Command = command,
                    Timestamp = DateTime.Now
                });

                SpeakAsync($"好的,正在设置换热器温度为{temperature.Value}度");
            }
            catch (Exception ex)
            {
                _logger.LogError($"❌ 处理换热器参数命令失败: {ex.Message}");
                SpeakAsync("设置参数失败");
            }
        }

        // ========== 新增:设备状态控制命令处理 ==========
        private void HandleDeviceStateControlCommand(string command)
        {
            try
            {
                _logger.LogInformation($"🎛 处理设备状态控制命令: {command}");

                string deviceType = null;
                bool targetState = false;

                // 识别设备类型
                if (command.Contains("柱塞泵"))
                {
                    deviceType = "柱塞泵";
                    targetState = command.Contains("启动");
                }
                else if (command.Contains("电磁阀"))
                {
                    deviceType = "电磁阀";
                    targetState = command.Contains("打开");
                }
                else if (command.Contains("流量计"))
                {
                    deviceType = "流量计";
                    targetState = command.Contains("启动");
                }
                else
                {
                    SpeakAsync("未能识别设备类型");
                    return;
                }

                // 发布状态控制事件
                _eventAggregator?.GetEvent<VoiceControlDeviceStateEvent>()?.Publish(new VoiceControlStateEventArgs
                {
                    DeviceType = deviceType,
                    TargetState = targetState,
                    Command = command,
                    Timestamp = DateTime.Now
                });

                string action = targetState ? "启动" : "停止";
                if (deviceType == "电磁阀")
                {
                    action = targetState ? "打开" : "关闭";
                }

                SpeakAsync($"好的,正在{action}{deviceType}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"❌ 处理设备状态控制命令失败: {ex.Message}");
                SpeakAsync("操作失败");
            }
        }

        // ========== 新增:从命令中提取数字 ==========
        private double? ExtractNumberFromCommand(string command)
        {
            try
            {
                // 支持中文数字和阿拉伯数字
                var chineseNumbers = new Dictionary<char, int>
        {
            {'零', 0}, {'一', 1}, {'二', 2}, {'三', 3}, {'四', 4},
            {'五', 5}, {'六', 6}, {'七', 7}, {'八', 8}, {'九', 9},
            {'十', 10}, {'百', 100}, {'千', 1000}
        };

                // 先尝试提取阿拉伯数字
                var numberMatch = System.Text.RegularExpressions.Regex.Match(command, @"\d+\.?\d*");
                if (numberMatch.Success)
                {
                    if (double.TryParse(numberMatch.Value, out double value))
                    {
                        _logger.LogInformation($"✅ 提取到数字: {value}");
                        return value;
                    }
                }

                // 尝试解析中文数字 (简单实现)
                if (command.Contains("十"))
                {
                    // 处理 "二十"、"三十" 等
                    for (int i = 2; i <= 9; i++)
                    {
                        var chineseDigit = chineseNumbers.First(kv => kv.Value == i).Key;
                        if (command.Contains($"{chineseDigit}十"))
                        {
                            _logger.LogInformation($"✅ 提取到中文数字: {i * 10}");
                            return i * 10.0;
                        }
                    }

                    // 处理 "十" (10)
                    if (command.Contains("十") && !command.Contains("百"))
                    {
                        _logger.LogInformation($"✅ 提取到中文数字: 10");
                        return 10.0;
                    }
                }

                _logger.LogWarning($"⚠ 无法从命令中提取数字: {command}");
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError($"❌ 提取数字失败: {ex.Message}");
                return null;
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
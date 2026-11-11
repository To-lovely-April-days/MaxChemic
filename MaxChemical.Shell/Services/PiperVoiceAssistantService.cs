using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Media;
using System.Windows;
using MaxChemical.Infrastructure.DOE;
using MaxChemical.Infrastructure.Events;
using MaxChemical.Logging;
using MaxChemical.Modules.Lab3D.Services;
using MaxChemical.Shell.Models;
using Microsoft.Extensions.Configuration;
using Prism.Events;
using Prism.Ioc;

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
        private readonly Agent.XiaoTongAgent _agent;             // ← DeepSeek 工具调用 Agent(配置了 ApiKey 才启用)
        private AliyunRealtimeAsrEngine? _aliyunEngine;          // ← 百炼 ASR;首次需要语音输入时懒加载(按键说话即使持续监听关闭也可用)
        private readonly Agent.Safety.VoiceConfirmationService _voiceConfirm; // ← 语音确认(说「确认」可通过确认卡)
        private readonly Agent.Safety.EmergencyStopService _emergencyStop;    // ← 急停本地直连(不走大模型)
        private readonly IFlowExecutionService _flowExecution;
        private readonly Timer _partialFallbackTimer;
        private readonly object _partialGate = new();

        // 语音 Agent 提问的界面通道(AgentChatViewModel.AskFromVoice):
        // 走它才有 IsBusy 占用与本轮会话锁定;由 AgentBootstrapper 装配时注入,避免 DI 环。
        private Action<string>? _uiAskHandler;

        /// <summary>装配语音→聊天 ViewModel 的提问通道(未装配时退回直连 Agent 的旧链路)。</summary>
        public void SetUiAskHandler(Action<string> handler) => _uiAskHandler = handler;

        /// <summary>语音总开关(VoiceSettings:Enabled)。工业现场嘈杂时置 false:不听麦、不播报,文字对话不受影响。</summary>
        private readonly bool _voiceEnabled;

        private bool _isListening;
        private bool _isAwake;
        private bool _isSpeechEnabled = true;
        private Timer? _autoSleepTimer;

        /// <summary>
        /// 唤醒后自动休眠秒数(VoiceSettings:AutoSleepSeconds):
        /// 0(默认)=不自动休眠,一直处于聆听状态,直到用户说「退出语音/休息/再见」。
        /// </summary>
        private readonly int _autoSleepSeconds;

        private CancellationTokenSource? _recognitionCts;

        // ── 按键说话(PTT):小桐聊天窗麦克风按钮的一轮聆听 ──
        private bool _isListeningOnce;
        private CancellationTokenSource? _listenOnceCts;
        private const int ListenOnceMaxSeconds = 30;

        /// <summary>PTT 是否正在聆听。</summary>
        public bool IsListeningOnce => _isListeningOnce;

        /// <summary>PTT 聆听状态变化(界面按钮/动画反馈)。</summary>
        public event EventHandler<bool>? ListenOnceStateChanged;

        /// <summary>PTT 实时中间识别片段(边说边出字的反馈)。</summary>
        public event EventHandler<string>? ListenPartialChanged;

        /// <summary>PTT 一轮聆听结束,带回整句识别文本;空串=未识别到内容或超时/取消。</summary>
        public event EventHandler<string>? ListenOnceCompleted;

        public bool IsListening => _isListening;

        public bool IsSpeechEnabled
        {
            get => _isSpeechEnabled;
            set => _isSpeechEnabled = value;
        }

        public event EventHandler<bool>? ListeningStateChanged;
        public event EventHandler<string>? CommandRecognized;
        public event EventHandler<string>? SpeechStarted;

        public bool SupportsSpeechPlaybackEvents => _ttsEngine is BailianTtsEngine;
        public event EventHandler<bool>? SpeechPlaybackChanged;

        public PiperVoiceAssistantService(
            IEventAggregator eventAggregator,
            IConfiguration configuration,
            Agent.XiaoTongAgent agent,
            Agent.Safety.VoiceConfirmationService voiceConfirm,
            Agent.Safety.EmergencyStopService emergencyStop,
            IFlowExecutionService flowExecution)
        {
            _logger = LogManager.GetLogger<PiperVoiceAssistantService>();
            _eventAggregator = eventAggregator;
            _configuration = configuration;
            _agent = agent;
            _voiceConfirm = voiceConfirm;
            _emergencyStop = emergencyStop;
            _flowExecution = flowExecution;
            _partialFallbackTimer = new Timer(_ => CheckPartialFallback(), null,
                Timeout.Infinite, Timeout.Infinite);

            _llmService = new OllamaLlmService(_logger);
            _ttsEngine = CreateTtsEngine();
            // 唤醒回应使用同音色的独立引擎,不触发主引擎的采集挂起事件。
            _wakeTtsEngine = CreateTtsEngine();

            // 自动休眠秒数:缺省/非正数 = 不自动休眠,唤醒后一直聆听直到「退出语音」
            if (!int.TryParse(_configuration["VoiceSettings:AutoSleepSeconds"], out _autoSleepSeconds))
                _autoSleepSeconds = 0;

            // TTS 播报期间挂起麦克风采集:播报声被麦克风收回形成识别回声污染,
            // 会挤占识别通道导致唤醒时灵时不灵(聊天窗打开、播报频繁时尤其明显)
            if (_ttsEngine is BailianTtsEngine bailianTts)
            {
                bailianTts.PlayingChanged += (_, playing) =>
                {
                    var engine = _aliyunEngine; // 可能尚未懒加载,事件触发时再取
                    if (engine != null) engine.CaptureSuspended = playing;
                    if (playing) _logger.LogDebug("TTS 播报开始,麦克风采集已挂起(防回声)");
                    SpeechPlaybackChanged?.Invoke(this, playing);
                };
            }

            // 语音总开关:Enabled=false 时只关闭"持续唤醒监听环"(工业现场噪音/省流量),
            // 按键说话(PTT)与语音播报(TTS)仍然可用——小桐聊天窗的麦克风按钮走 PTT。
            _voiceEnabled = !string.Equals(_configuration["VoiceSettings:Enabled"], "false",
                StringComparison.OrdinalIgnoreCase);

            if (_voiceEnabled)
            {
                try
                {
                    EnsureAsrEngine();
                }
                catch (Exception ex)
                {
                    // Key 未配置等情况不允许拖垮应用启动:仅持续监听不可用,PTT 仍会按需懒加载
                    _logger.LogWarning($"语音识别初始化失败,持续监听不可用: {ex.Message}");
                }
            }
            else
            {
                _logger.LogInformation("持续唤醒监听已关闭(VoiceSettings:Enabled=false);按键说话仍可用");
            }

            // 语音播报总开关(VoiceSettings:SpeakEnabled,默认开):
            // 现场要求"回复不语音播报"时置 false,唤醒/聆听/确认等一切语音输出全部静音
            if (string.Equals(_configuration["VoiceSettings:SpeakEnabled"], "false", StringComparison.OrdinalIgnoreCase))
            {
                _isSpeechEnabled = false;
                _logger.LogInformation("语音播报已关闭(VoiceSettings:SpeakEnabled=false),回复只显示不发声");
            }
        }

        private ITtsEngine CreateTtsEngine()
        {
            var voiceSettings = _configuration.GetSection("VoiceSettings").Get<VoiceSettings>()
                               ?? new VoiceSettings();

            _logger.LogInformation($"配置的TTS提供商: {voiceSettings.TtsProvider}");

            ITtsEngine engine = voiceSettings.TtsProvider.ToLower() switch
            {
                "bailian" or "dashscope" or "aliyun" => new BailianTtsEngine(_logger, _configuration),
                "edge" => new EdgeTtsEngine(_logger, voiceSettings.EdgeTts),
                "azure" => new AzureTtsEngine(_logger, voiceSettings.AzureTts),
                "piper" => new PiperTtsEngine(_logger, voiceSettings.Piper),
                "sherpa" => new SherpaOnnxTtsEngine(_logger, voiceSettings.SherpaOnnx),
                _ => new SherpaOnnxTtsEngine(_logger, voiceSettings.SherpaOnnx)
            };

            if (!engine.IsAvailable)
            {
                _logger.LogWarning($"{engine.EngineName} 不可用，尝试降级方案");

                // 百炼不可用(未配 Key)→ 回退本地 sherpa
                if (engine is BailianTtsEngine)
                {
                    engine.Dispose();
                    engine = new SherpaOnnxTtsEngine(_logger, voiceSettings.SherpaOnnx);
                    _logger.LogInformation("已回退到本地 SherpaOnnx TTS");
                }

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

        private readonly object _asrGate = new();

        /// <summary>
        /// 懒加载 ASR 引擎(首次需要语音输入时创建):持续唤醒环与按键说话共用同一实例。
        /// PTT 会话期间识别结果由引擎的 ListenOnceAsync 自行处理,OnAliyunTextRecognized 会跳过。
        /// </summary>
        private AliyunRealtimeAsrEngine EnsureAsrEngine()
        {
            lock (_asrGate)
            {
                if (_aliyunEngine == null)
                {
                    var engine = new AliyunRealtimeAsrEngine(_logger, _configuration);
                    engine.Initialize();
                    engine.TextRecognized += OnAliyunTextRecognized;
                    engine.AudioLevelChanged += OnAudioLevelChanged;
                    engine.PartialTextRecognized += (_, partial) =>
                    {
                        // 只有按键说话(PTT)会话的中间片段才转发给界面做"边说边出字";
                        // 唤醒环持续监听的片段绝不落到输入框——没点按钮,其他语音一律不显示
                        if (_isListeningOnce) ListenPartialChanged?.Invoke(this, partial);

                        // 确认卡待决时只接受片段尾部的精确确认词,避免长句中的"执行"误批准。
                        if (!_isListeningOnce && _voiceConfirm.HasPending)
                        {
                            var v = _voiceConfirm.TryResolvePartial(partial);
                            if (v.HasValue) return;
                        }

                        // 云端迟迟不断句时,由本地计时器处理稳定片段,不再依赖下一次回调。
                        if (!_isListeningOnce && _isAwake && !_voiceConfirm.HasPending)
                            UpdatePartialFallback(partial);

                        // 唤醒词在中间结果命中立即唤醒,不等整句结束——唤醒更灵敏;
                        // 唤醒词后还跟着内容时继续走同句命令路由,不插入唤醒播报
                        if (!_isAwake && !_isListeningOnce && ContainsWakeUpPhrase(partial))
                            HandleWakeUp(partial, StripWakePhrases(partial).Length < 2);
                    };
                    _aliyunEngine = engine;
                    _logger.LogInformation("阿里云语音识别引擎初始化成功");
                }
                return _aliyunEngine;
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
            if (!_voiceEnabled || _aliyunEngine == null)
            {
                _logger.LogInformation("语音功能已禁用或不可用,忽略启动请求(文字对话不受影响)");
                return;
            }

            if (_isListening)
            {
                _logger.LogWarning("语音助手已在运行中");
                return;
            }

            try
            {
                _recognitionCts = new CancellationTokenSource();

                // ← 改为阿里云引擎启动
                var engine = _aliyunEngine;
                var token = _recognitionCts.Token;
                Task.Run(async () => await engine.StartContinuousRecognitionAsync(token));

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

        /// <summary>
        /// 按键说话:开始一轮聆听(点麦克风按钮)。与持续唤醒环互斥——若唤醒环在跑先停掉。
        /// 识别中间片段经 ListenPartialChanged 实时回调,整句结束经 ListenOnceCompleted 回调。
        /// </summary>
        public void StartListenOnce()
        {
            if (IsListeningOnce) return;
            try
            {
                // 先停掉正在播报的语音:播报期间采集挂起,不先停会录到空音
                StopSpeaking();

                var engine = EnsureAsrEngine();

                // 与持续唤醒环互斥(同一引擎同一时刻只能跑一个会话):
                // 先停唤醒环并记住,按键说话结束后自动恢复,别让唤醒词静默失效
                bool loopWasRunning = _isListening;
                if (loopWasRunning) Stop();

                IsListeningOnceChange(true);
                _listenOnceCts = new CancellationTokenSource();
                var ct = _listenOnceCts.Token;

                Task.Run(async () =>
                {
                    string last = "";
                    try
                    {
                        // 持续聆听直到用户再点一次(取消)或超时;每一句整句经回调送出
                        last = await engine.ListenOnceAsync(
                            TimeSpan.FromSeconds(ListenOnceMaxSeconds), ct,
                            onFinal: sentence => ListenOnceCompleted?.Invoke(this, sentence));
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning($"按键说话识别失败: {ex.Message}");
                    }
                    finally
                    {
                        try { _listenOnceCts?.Dispose(); } catch { }
                        _listenOnceCts = null;
                        IsListeningOnceChange(false);
                        ListenOnceCompleted?.Invoke(this, "");   // 空串=本轮聆听结束

                        // 恢复持续唤醒环(Enable=true 配置下)
                        if (loopWasRunning && !_isListening && _voiceEnabled)
                        {
                            try { Start(); } catch (Exception ex) { _logger.LogWarning($"恢复唤醒环失败: {ex.Message}"); }
                        }
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.LogError($"启动按键说话失败: {ex.Message}");
                IsListeningOnceChange(false);
                ListenOnceCompleted?.Invoke(this, "");
            }
        }

        /// <summary>结束当前按键说话(用户再点一次麦克风按钮)。</summary>
        public void StopListenOnce()
        {
            try { _listenOnceCts?.Cancel(); } catch { }
        }

        private void IsListeningOnceChange(bool value)
        {
            if (_isListeningOnce == value) return;
            _isListeningOnce = value;
            try { ListenOnceStateChanged?.Invoke(this, value); } catch { }
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

            // PTT 会话期间的结果由引擎 ListenOnceAsync 自行处理,这里跳过避免重复路由
            if (_isListeningOnce)
                return;

            // ── 急停优先:无论是否唤醒,"小桐急停"这类指令都走本地直连,绝不进大模型 ──
            // 必须放在唤醒词匹配之前:"小桐急停"会被当成纯唤醒词吞掉;
            // 未唤醒态要求带唤醒词前缀("小桐急停")防误触发,已唤醒态直接"急停"即可。
            if (_emergencyStop.Matches(text) && (ContainsWakeUpPhrase(text) || _isAwake))
            {
                _logger.LogWarning($"语音急停直连触发: {text}");
                // ExecuteAsync 内部统一完成停机+播报+审计,这里只异步发起
                _ = Task.Run(async () => await _emergencyStop.ExecuteAsync(text));
                return;
            }

            // 该句已由「片段稳定兜底」处理过(整句结果延迟到达):模糊匹配跳过,
            // 避免重复执行——整句常比兜底片段多出重复累积的尾巴,按包含关系判同句
            if (_lastFallbackCommandText != null &&
                DateTime.UtcNow - _lastFallbackUtc < TimeSpan.FromSeconds(15))
            {
                string strippedFinal = Agent.TextMatchHelper.StripPunctuation(text);
                if (strippedFinal.Contains(_lastFallbackCommandText) &&
                    strippedFinal.Length <= _lastFallbackCommandText.Length + 20)
                {
                    _lastFallbackCommandText = null;
                    _logger.LogInformation($"该句已由片段稳定兜底处理过,整句结果跳过: {text}");
                    return;
                }
            }

            ResetPartialFallback();

            // 确认卡待决期间只接受完整的短确认词,不解析长句中的确认片段。
            if (_voiceConfirm.HasPending)
            {
                var verdict = _voiceConfirm.TryResolve(text);
                if (verdict.HasValue) return;
            }

            if (ContainsWakeUpPhrase(text))
            {
                // 唤醒只更新界面状态;唤醒词后跟指令时继续执行剩余内容
                HandleWakeUp(text, StripWakePhrases(text).Length < 2);

                // 唤醒词与指令同句(如「你好小桐帮我查报警」):剥掉唤醒词,
                // 剩余部分直接当指令执行——不必等用户再说第二句
                string rest = StripWakePhrases(text);
                if (!_isListeningOnce && rest.Length >= 2)
                {
                    _logger.LogInformation($"唤醒词与指令同句,剩余指令: {rest}");
                    Task.Run(async () => await HandleCommandAsync(rest));
                }
                return;
            }

            if (_isAwake)
            {
                Task.Run(async () => await HandleCommandAsync(text));
            }
            else
            {
                _logger.LogInformation($"未唤醒状态，请先说唤醒词（如：你好小桐）");
            }
        }

        /// <summary>
        /// 唤醒词匹配(正则,容忍同音字):支持「你好小桐」与直接称呼「小桐」,
        /// 覆盖常见识别变体,两种语序都认(你好小桐 / 小桐你好)。
        /// </summary>
        private static readonly Regex WakePatternRegex = new(
            @"[小晓肖筱][桐童同彤通东冬虫冲丛从聪](你好|您好|你号|你郝|妮好|泥好)|" +
            @"(你好|您好|你号|你郝|妮好|泥好)?[小晓肖筱][桐童同彤通东冬虫冲丛从聪]",
            RegexOptions.Compiled);

        private bool ContainsWakeUpPhrase(string text)
        {
            var t = Agent.TextMatchHelper.StripPunctuation(text);
            bool found = WakePatternRegex.IsMatch(t);

            if (found)
            {
                _logger.LogInformation($" 检测到唤醒词: {text}");
            }

            return found;
        }

        /// <summary>剥掉文本中的唤醒词(「你好小桐帮我查报警」→「帮我查报警」)。</summary>
        private static string StripWakePhrases(string text)
            => WakePatternRegex.Replace(text ?? "", "").Trim(' ', ',', ',', '，', '。', '、', '!', '！');


        /// <summary>
        /// 与实验室/本软件相关的语音指令关键词(本地过滤,不发模型):
        /// 命中任一词才交给小桐;纯闲聊、环境音一律忽略。
        /// </summary>
        private static readonly string[] IndustryKeywords =
        {
            // 软件与工程
            "设备", "画布", "流程", "工程", "项目", "工艺", "连接", "串口", "参数", "报警",
            "故障", "运行", "状态", "数据", "设置", "界面", "页面", "窗口", "面板", "日志",
            "新建", "保存", "打开", "关闭", "启动", "停止", "暂停", "继续", "复位", "清除",
            "查询", "搜索", "查找", "分析", "报告", "记录", "导出", "导入", "手册", "规程",
            "知识库", "资料", "文献", "说明书", "软件", "系统", "平台", "小桐",
            "全屏", "展示", "播放", "最大化", "三维",
            // 设备与量测
            "泵", "阀", "冷水机", "恒温", "搅拌", "磁力搅拌", "加热", "制冷", "循环",
            "流量", "温度", "压力", "转速", "频率", "称重", "天平", "计量", "液位", "浓度",
            "探头", "传感器", "控制器", "电机", "变频", "电流", "电压", "串口", "通讯",
            // 行业与实验
            "化学", "化工", "实验室", "实验", "反应", "反应器", "反应釜", "连续流", "微通道",
            "微反应", "动力学", "收率", "转化率", "停留时间", "当量比", "进料", "出料", "试剂",
            "底物", "产物", "采样", "批次", "轮次", "因子", "响应", "DOE", "设计", "预测",
            "优化", "保温", "洗料", "淬灭", "萃取", "结晶", "合成", "催化", "放大"
        };

        /// <summary>
        /// 聊天/通用查询关键词:与项目无关但对助理的正常对话也放行,
        /// 例如「你能帮我做什么」「查询天气」「讲个笑话」「你好」「谢谢」。
        /// (查询/搜索/分析类词已在行业词表里,「查询天气」本来就通。)
        /// </summary>
        private static readonly string[] ChatKeywords =
        {
            // 能力/身份询问
            "你能做什么", "你能帮我做什么", "你能帮我", "你能干嘛", "你会什么", "你有什么功能",
            "你叫什么", "你是谁", "介绍一下", "介绍自己", "自我介绍", "你的名字",
            // 闲聊
            "你好", "您好", "谢谢", "感谢", "再见", "拜拜", "聊天", "唠嗑", "陪我",
            "讲个笑话", "说个笑话", "笑话", "讲故事", "故事", "唱歌", "唱首歌", "诗词", "背首诗",
            // 通用查询
            "天气", "新闻", "现在几点", "几点", "星期几", "几号", "日期", "时间",
            // 通用请求
            "帮我", "告诉我", "说说", "讲一讲", "讲个"
        };

        /// <summary>聊天意图判断(对助理的正常对话,即使不含行业词也放行)。</summary>
        private static bool IsChatRelated(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            string t = Agent.TextMatchHelper.StripPunctuation(text);
            foreach (var k in ChatKeywords)
                if (t.Contains(k, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        /// <summary>判断语音文本是否与本软件/本行业相关(关键词包含匹配)。</summary>
        private static bool IsIndustryRelated(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            string t = Agent.TextMatchHelper.StripPunctuation(text);
            foreach (var k in IndustryKeywords)
                if (t.Contains(k, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        private void HandleWakeUp(string text, bool speakAck = true)
        {
            if (!_isAwake)
            {
                _isAwake = true;
                _logger.LogInformation("语音助手已唤醒");
                CommandRecognized?.Invoke(this, "WAKE_UP");
                // 使用本地即时回应,不走云端TTS、不挂起采集,兼顾回应感和首句识别。
                if (speakAck)
                {
                    _lastWakeAckUtc = DateTime.UtcNow;
                    PlayWakeAcknowledgement();
                }
                StartAutoSleepTimer();
            }
            else
            {
                ResetAutoSleepTimer();
                // 重复唤醒同样给本地短回应,3秒节流防止反复打断用户。
                if (speakAck && DateTime.UtcNow - _lastWakeAckUtc > TimeSpan.FromSeconds(3))
                {
                    _lastWakeAckUtc = DateTime.UtcNow;
                    PlayWakeAcknowledgement();
                    CommandRecognized?.Invoke(this, "WAKE_UP");
                }
            }
        }

        /// <summary>上次唤醒界面反馈的时间(防重复刷新节流)。</summary>
        private DateTime _lastWakeAckUtc = DateTime.MinValue;
        private readonly object _wakeSpeechGate = new();
        private readonly ITtsEngine _wakeTtsEngine;

        /// <summary>
        /// 使用与正式回复相同的小桐自然音色回应唤醒。
        /// 独立TTS实例不订阅主引擎的PlayingChanged,因此不会改变CaptureSuspended,
        /// 不再使用Windows自带的机器人音,也不会挡住紧随其后的用户指令。
        /// </summary>
        private void PlayWakeAcknowledgement()
        {
            lock (_wakeSpeechGate)
            {
                try
                {
                    _wakeTtsEngine.StopSpeaking();
                    _wakeTtsEngine.SpeakAsync("我在");
                }
                catch
                {
                    try { SystemSounds.Asterisk.Play(); } catch { }
                }
            }
        }

        // ── 片段稳定兜底(整句迟迟不出时把稳定片段当一句话处理) ──
        private string _lastPartialText = "";
        private DateTime _lastPartialChangeUtc = DateTime.MinValue;
        private bool _partialCommandFired;
        private string _lastFallbackCommandText;
        private DateTime _lastFallbackUtc = DateTime.MinValue;
        private readonly object _commandGate = new();
        private readonly List<KeyValuePair<string, DateTime>> _recentCommands = new();

        private void UpdatePartialFallback(string partial)
        {
            lock (_partialGate)
            {
                string stripped = Agent.TextMatchHelper.StripPunctuation(partial);
                bool sameUtterance = _lastFallbackCommandText != null &&
                                     DateTime.UtcNow - _lastFallbackUtc < TimeSpan.FromSeconds(15) &&
                                     stripped.Contains(_lastFallbackCommandText);
                if (string.Equals(partial, _lastPartialText, StringComparison.Ordinal)) return;

                _lastPartialText = partial;
                _lastPartialChangeUtc = DateTime.UtcNow;
                if (!sameUtterance)
                {
                    _partialCommandFired = false;
                    _lastFallbackCommandText = null;
                }
                _partialFallbackTimer.Change(500, Timeout.Infinite);
            }
        }

        private void CheckPartialFallback()
        {
            string command;
            lock (_partialGate)
            {
                if (_partialCommandFired || _isListeningOnce || !_isAwake || _voiceConfirm.HasPending ||
                    DateTime.UtcNow - _lastPartialChangeUtc < TimeSpan.FromMilliseconds(500))
                    return;

                command = StripWakePhrases(_lastPartialText);
                if (!IsFlowAdvanceCommand(command) && MatchLab3DVoiceCommand(command) == Lab3DVoiceAction.None)
                    return;

                string stripped = Agent.TextMatchHelper.StripPunctuation(command);
                _partialCommandFired = true;
                _lastFallbackCommandText = stripped;
                _lastFallbackUtc = DateTime.UtcNow;
            }

            _logger.LogInformation($"本地短指令片段稳定,立即处理: {command}");
            _ = Task.Run(() => HandleCommandAsync(command));
        }

        private void ResetPartialFallback()
        {
            lock (_partialGate)
            {
                _lastPartialText = "";
                _lastPartialChangeUtc = DateTime.MinValue;
                _partialCommandFired = false;
                _lastFallbackCommandText = null;
                _partialFallbackTimer.Change(Timeout.Infinite, Timeout.Infinite);
            }
        }

        private bool TryAcceptCommand(string text)
        {
            string normalized = string.Concat(Agent.TextMatchHelper.StripPunctuation(text)
                .Where(c => !char.IsWhiteSpace(c)));
            if (normalized.Length == 0) return false;

            lock (_commandGate)
            {
                var now = DateTime.UtcNow;
                _recentCommands.RemoveAll(c => now - c.Value > TimeSpan.FromSeconds(2));
                if (_recentCommands.Any(c => c.Key == normalized))
                {
                    _logger.LogInformation($"忽略2秒内的重复语音命令: {text}");
                    return false;
                }

                _recentCommands.Add(new KeyValuePair<string, DateTime>(normalized, now));
                if (_recentCommands.Count > 4) _recentCommands.RemoveAt(0);
                return true;
            }
        }

        private static bool IsFlowAdvanceCommand(string text)
        {
            string command = Agent.TextMatchHelper.StripPunctuation(text);
            return command is "下一步" or "下一布" or "下步" or "继续" or "继续执行" or
                "继续下一步" or "下一节点" or "下一个节点" or "跳过等待" or "结束等待";
        }

        private enum Lab3DVoiceAction
        {
            None,
            Open,
            Fullscreen,
            ExitFullscreen,
            Close
        }

        /// <summary>
        /// 匹配确定性的3D窗口口令。只做明确前缀/后缀匹配,不拿"实验室"三个字泛化拦截,
        /// 避免影响"我要做实验"等需要总控和专员处理的自然语言。
        /// </summary>
        private static Lab3DVoiceAction MatchLab3DVoiceCommand(string text)
        {
            string command = string.Concat(Agent.TextMatchHelper.StripPunctuation(text)
                .Where(c => !char.IsWhiteSpace(c))).ToLowerInvariant();
            command = command.Replace("３", "3").Replace("ｄ", "d").Replace("三d", "3d");

            string[] prefixes = { "嗯", "呃", "啊", "请", "麻烦", "帮我", "给我", "小桐" };
            bool removed;
            do
            {
                removed = false;
                foreach (var prefix in prefixes)
                {
                    if (!command.StartsWith(prefix, StringComparison.Ordinal)) continue;
                    command = command.Substring(prefix.Length);
                    removed = true;
                }
            } while (removed && command.Length > 0);

            if (command.Length == 0 || command.Contains("不要") || command.Contains("别"))
                return Lab3DVoiceAction.None;

            if (command.StartsWith("退出全屏") || command.StartsWith("取消全屏") ||
                command.StartsWith("退出最大化") || command.StartsWith("取消最大化") ||
                command is "恢复窗口" or "还原窗口" or "恢复正常显示" or "恢复实验室窗口")
                return Lab3DVoiceAction.ExitFullscreen;

            if (command.StartsWith("全屏播放") || command.StartsWith("全屏展示") ||
                command.StartsWith("全屏显示") || command.StartsWith("全屏模式") ||
                command is "全屏" or "最大化" or "最大化显示" ||
                command.EndsWith("最大化显示3d实验室") || command.EndsWith("最大化显示三维实验室"))
                return Lab3DVoiceAction.Fullscreen;

            string[] closeSuffixes =
            {
                "关闭实验室", "退出实验室", "关闭3d实验室", "退出3d实验室",
                "关闭三维实验室", "退出三维实验室"
            };
            if (closeSuffixes.Any(command.EndsWith)) return Lab3DVoiceAction.Close;

            string[] openSuffixes =
            {
                "打开实验室", "进入实验室", "打开3d实验室", "进入3d实验室",
                "打开三维实验室", "进入三维实验室", "显示3d实验室", "显示三维实验室",
                "打开3d", "进入3d", "打开三维", "进入三维"
            };
            if (openSuffixes.Any(command.EndsWith)) return Lab3DVoiceAction.Open;
            return Lab3DVoiceAction.None;
        }

        private bool TryHandleSpeechPlaybackCommand(string text, ISpeechPlaybackService playback)
        {
            string command = Agent.TextMatchHelper.StripPunctuation(text).Trim();
            if (command is "停止播报" or "停止说话" or "停止朗读" or "取消播报" or "别说了" or "不要再说了" or "闭嘴")
            {
                playback.Stop();
                _logger.LogInformation("用户通过语音停止了播报");
                return true;
            }
            if (command is "暂停播报" or "暂停说话" or "暂停朗读")
            {
                playback.Pause();
                return true;
            }
            if (command is "继续播报" or "继续朗读" or "恢复播报")
            {
                playback.Resume();
                return true;
            }
            return false;
        }

        private async Task HandleCommandAsync(string text)
        {
            _logger.LogInformation($"收到用户指令: {text}");

            // 兜底剥掉残留唤醒词(同句唤醒路径已剥过一遍,这里防漏)
            text = StripWakePhrases(text);
            if (string.IsNullOrWhiteSpace(text)) return;

            bool isEmergency = _emergencyStop.Matches(text);
            if (!isEmergency && !TryAcceptCommand(text)) return;

            ResetAutoSleepTimer();
            CommandRecognized?.Invoke(this, text);

            // 急停本地直连且不参与重复命令去重,任何重复急停请求都必须及时处理。
            if (isEmergency)
            {
                await _emergencyStop.ExecuteAsync(text);
                return;
            }

            // 播报控制不进入大模型;普通新指令也会先停止旧回答,防止两段回复叠播。
            var speechPlayback = ContainerLocator.Container.Resolve<ISpeechPlaybackService>();
            if (TryHandleSpeechPlaybackCommand(text, speechPlayback)) return;
            speechPlayback.Stop();

            // 确认结果由卡片和后续正式回复反馈,不再插入短播报抢占音频。
            if (_voiceConfirm.HasPending)
            {
                var verdict = _voiceConfirm.TryResolve(text);
                if (verdict.HasValue) return;
            }

            // 3D窗口操作是确定性界面命令,本地直连以避免总控→专员→总控的多轮等待。
            // 成功后不再播"已打开/已全屏":窗口变化本身就是反馈,继续说话不会被新播报挡住。
            var lab3DAction = MatchLab3DVoiceCommand(text);
            if (lab3DAction != Lab3DVoiceAction.None)
            {
                StopSpeaking();
                try
                {
                    var lab3D = ContainerLocator.Container.Resolve<ILab3DWindowService>();
                    void Execute()
                    {
                        switch (lab3DAction)
                        {
                            case Lab3DVoiceAction.Open:
                                lab3D.ShowLab3D(Application.Current?.MainWindow);
                                break;
                            case Lab3DVoiceAction.Fullscreen:
                                lab3D.ShowLab3DFullScreen(Application.Current?.MainWindow);
                                break;
                            case Lab3DVoiceAction.ExitFullscreen:
                                lab3D.ExitLab3DFullScreen();
                                break;
                            case Lab3DVoiceAction.Close:
                                lab3D.CloseLab3D();
                                break;
                        }
                    }

                    var dispatcher = Application.Current?.Dispatcher;
                    if (dispatcher == null || dispatcher.CheckAccess()) Execute();
                    else dispatcher.Invoke(Execute);
                    _logger.LogInformation($"3D语音本地直连执行成功: {lab3DAction},原始命令: {text}");
                }
                catch (Exception ex)
                {
                    _logger.LogError($"3D语音本地直连执行失败: {ex.Message}");
                    SpeakAsync("3D实验室操作失败");
                }
                return;
            }

            // 等待节点的推进是确定性本地操作,不经过总控与专员的多轮云端调用。
            if (IsFlowAdvanceCommand(text))
            {
                if (_flowExecution.TryAdvanceFlowInteraction())
                {
                    StopSpeaking();
                    SpeakAsync("好的,继续执行");
                    _logger.LogInformation($"语音已推进当前等待节点: {text}");
                    return;
                }

                string normalized = Agent.TextMatchHelper.StripPunctuation(text);
                if (normalized != "继续")
                {
                    SpeakAsync("当前没有等待确认的流程步骤");
                    _logger.LogInformation($"当前没有可推进的等待节点: {text}");
                    return;
                }
            }

            // 休眠口令走本地捷径,不进大模型
            if (text.Contains("休息") || text.Contains("睡觉") || text.Contains("再见"))
            {
                HandleSleepIntent();
                return;
            }

            // 「退出语音」本地捷径:退出语音指令响应,回到待命状态——
            // 持续监听保留,说「你好小桐」仍可再次唤醒(不能彻底关掉麦克风,否则唤醒词再也听不到)
            if (text.Contains("退出语音") || text.Contains("关闭语音"))
            {
                _isAwake = false;
                StopAutoSleepTimer();
                SpeakAsync("已退出语音,说你好小桐可以再次唤醒。");
                CommandRecognized?.Invoke(this, "VOICE_OFF");
                _logger.LogInformation("收到「退出语音」指令,已退出语音响应,回到待命状态(唤醒词仍可唤醒)");
                return;
            }

            // 相关性过滤:行业指令(软件/设备/工艺/实验)与正常聊天/通用查询都放行,
            // 其余环境音/无关闲谈一律忽略,不发模型、不回复(避免误触发与流量浪费)
            if (!IsIndustryRelated(text) && !IsChatRelated(text))
            {
                _logger.LogInformation($"语音与项目无关且非对话,已忽略: {text}");
                CommandRecognized?.Invoke(this, "IGNORED");
                ResetAutoSleepTimer();
                return;
            }

            // ── Agent 模式:DeepSeek 工具调用(配置了 ApiKey 即启用) ──
            // 小桐自己决定查什么/做什么;写操作会在对话窗口弹确认卡片。
            // 一律经聊天 ViewModel 走(AskFromVoice):它会占用 IsBusy、锁定本轮会话路由并播报回复;
            // 直接调 _agent.AskAsync 会绕过这些护栏,轮次进行中切换会话/发消息会把
            // 事件与历史落进错误的会话(装配阶段由 AgentBootstrapper 注入委托,避免 DI 环)。
            if (_agent != null && _agent.IsConfigured)
            {
                if (_uiAskHandler != null)
                {
                    _uiAskHandler(text);
                    ResetAutoSleepTimer();
                    return;
                }
                try
                {
                    string reply = await _agent.AskAsync(text, source: "语音");
                    if (!string.IsNullOrWhiteSpace(reply))
                        SpeakAsync(reply);
                    ResetAutoSleepTimer();
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Agent 处理失败,回退本地意图识别: {ex.Message}");
                    // 落到下方旧链路兜底
                }
            }

            _logger.LogInformation("调用本地意图识别(Ollama 兜底链路)...");
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
            lock (_wakeSpeechGate) _wakeTtsEngine.StopSpeaking();
            _ttsEngine.StopSpeaking();
            _logger.LogInformation("已停止语音输出");
        }

        public bool PauseSpeaking()
        {
            return _ttsEngine is BailianTtsEngine bailianTts && bailianTts.PauseSpeaking();
        }

        public bool ResumeSpeaking()
        {
            return _ttsEngine is BailianTtsEngine bailianTts && bailianTts.ResumeSpeaking();
        }

        public void SetSpeakingRate(float rate)
        {
            _ttsEngine.SetSpeakingRate(rate);
        }

        private void StartAutoSleepTimer()
        {
            StopAutoSleepTimer();
            if (_autoSleepSeconds <= 0) return;   // 0=不自动休眠,一直聆听直到「退出语音」

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
            _partialFallbackTimer.Dispose();

            if (_aliyunEngine != null)
            {
                _aliyunEngine.TextRecognized -= OnAliyunTextRecognized;
                _aliyunEngine.AudioLevelChanged -= OnAudioLevelChanged;
                _aliyunEngine.Dispose();
            }

            lock (_wakeSpeechGate) _wakeTtsEngine.Dispose();
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
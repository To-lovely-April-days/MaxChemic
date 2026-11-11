using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using MaxChemical.Infrastructure.Events;
using MaxChemical.Infrastructure.Services;
using MaxChemical.Logging;
using MaxChemical.Shell.Services;
using MaxChemical.Shell.Services.Agent;
using Prism.Commands;
using Prism.Events;
using Prism.Ioc;
using Prism.Mvvm;

namespace MaxChemical.Shell.ViewModels
{
    /// <summary>聊天列表中的一条目(用户/助手/工具过程/确认卡片)。</summary>
    public class ChatItem : BindableBase
    {
        private bool _isResolved;
        private bool _approved;

        public string Text { get; set; }
        public string ToolName { get; set; }
        public DateTime Time { get; set; } = DateTime.Now;

        public bool IsUser { get; set; }
        public bool IsAssistant { get; set; }
        public bool IsToolLine { get; set; }
        public bool IsConfirm { get; set; }
        public bool IsChart { get; set; }
        public bool IsError { get; set; }
        public bool ToolSuccess { get; set; } = true;

        /// <summary>工具过程行的阶段标签:执行 / 完成 / 失败。</summary>
        public string ToolStage { get; set; } = "执行";

        // ── 确认卡片 ──
        public TaskCompletionSource<bool> Decision { get; set; }

        public bool IsResolved
        {
            get => _isResolved;
            set => SetProperty(ref _isResolved, value);
        }

        public bool Approved
        {
            get => _approved;
            set => SetProperty(ref _approved, value);
        }

        public DelegateCommand ApproveCommand { get; set; }
        public DelegateCommand RejectCommand { get; set; }

        // ── 选择卡(ask_user_choice):单选=单选框,多选=复选框 ──
        public bool IsChoice { get; set; }
        public bool IsMultiSelect { get; set; }

        /// <summary>单选框分组名:每张卡唯一,避免不同卡的 RadioButton 串组。</summary>
        public string GroupId { get; set; } = Guid.NewGuid().ToString("N");

        public ObservableCollection<ChoiceOption> Options { get; } = new();

        private string _choiceResult = "";
        /// <summary>已选结果(顿号连接);已决且为空 = 用户取消/超时。</summary>
        public string ChoiceResult
        {
            get => _choiceResult;
            set => SetProperty(ref _choiceResult, value);
        }

        public DelegateCommand SubmitChoiceCommand { get; set; }
        public DelegateCommand CancelChoiceCommand { get; set; }

        public string TimeText => Time.ToString("HH:mm:ss");
    }

    /// <summary>选择卡的一个选项。</summary>
    public class ChoiceOption : BindableBase
    {
        private bool _isSelected;
        public string Text { get; set; } = "";
        public bool IsSelected
        {
            get => _isSelected;
            set => SetProperty(ref _isSelected, value);
        }
    }

    /// <summary>一个会话:标题、预览、独立的界面条目与模型历史。</summary>
    public class ConversationVm : BindableBase
    {
        private readonly ILocalizationService _location;
        private string _title = "新对话";
        private string _preview = "";
        private DateTime _lastActiveAt = DateTime.Now;

        public ConversationVm()
        {
            _location = ContainerLocator.Container.Resolve<ILocalizationService>();
        }

        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public ObservableCollection<ChatItem> Items { get; } = new();
        public List<AgentMessage> History { get; set; } = new();
        public bool TitleLocked { get; set; }

        public string Title
        {
            get => _title;
            set => SetProperty(ref _title, value);
        }

        public string Preview
        {
            get => _preview;
            set => SetProperty(ref _preview, value);
        }

        public DateTime LastActiveAt
        {
            get => _lastActiveAt;
            set { if (SetProperty(ref _lastActiveAt, value)) RaisePropertyChanged(nameof(DateGroup)); }
        }

        public string DateGroup
        {
            get
            {
                var d = LastActiveAt.Date;
                var today = DateTime.Today;
                if (d == today) return _location.GetString("Agent_Today", "今天");
                if (d == today.AddDays(-1)) return _location.GetString("Agent_Yesterday", "昨天");
                return _location.GetString("Agent_Earlier", "更早");
            }
        }
    }

    /// <summary>小桐对话窗口 VM:多会话管理 + Agent 桥接 + 确认流程。</summary>
    public class AgentChatViewModel : BindableBase
    {
        private readonly XiaoTongAgent _agent;
        private readonly IVoiceAssistantService _voice;
        private readonly MaxChemical.Infrastructure.Services.IDialogService _dialog;
        private readonly ILogService _logger;
        private readonly AgentChatStorage _storage = new();
        private readonly ILocalizationService _localization;
        private readonly IEventAggregator _eventAggregator;
        private readonly Services.Agent.Safety.VoiceConfirmationService _voiceConfirm;
        private readonly Services.Agent.Safety.EmergencyStopService _emergencyStop;
        private readonly ISpeechPlaybackService _speechPlayback;
        private readonly HashSet<string> _activeTurnToolNames = new(StringComparer.OrdinalIgnoreCase);
        private bool _isSpeechPlaybackActive;
        private bool _isSpeechPaused;

        private string _inputText = "";
        private string _searchText = "";
        private bool _isBusy;
        private bool _isMiniMode;
        private string _miniMessage = "";
        private string _statusText = "就绪";
        private ConversationVm _current;
        private ConversationVm _activeTurnConversation; // 本轮 Agent 事件应落到的会话
        private System.Threading.CancellationTokenSource _turnCts; // 本轮取消源(停止按钮)

        public ObservableCollection<ConversationVm> Conversations { get; } = new();
        public ICollectionView ConversationsView { get; }

        public event Action ScrollToEndRequested;

        /// <summary>需要把窗口带到用户面前(如写操作待确认)。</summary>
        public event Action ShowWindowRequested;

        public AgentChatViewModel(XiaoTongAgent agent, IVoiceAssistantService voice,
            MaxChemical.Infrastructure.Services.IDialogService dialog, ILocalizationService localization,
            IEventAggregator eventAggregator,
            Services.Agent.Safety.VoiceConfirmationService voiceConfirm,
            Services.Agent.Safety.EmergencyStopService emergencyStop,
            ISpeechPlaybackService speechPlayback)
        {
            _agent = agent;
            _voice = voice;
            _dialog = dialog;
            _logger = LogManager.GetLogger<AgentChatViewModel>();
            _localization = localization;
            _eventAggregator = eventAggregator;
            _voiceConfirm = voiceConfirm;
            _emergencyStop = emergencyStop;
            _speechPlayback = speechPlayback;
            _speechPlayback.StateChanged += (s, state) => RunOnUi(UpdateSpeechPlaybackState);

            // 忙碌时常规发送被禁用,但急停指令必须任何时刻都能发出去(急停本地直连,不占 Agent 轮次);
            // 有待确认的写操作时也放行输入——发送「确认/取消」直接解决确认卡
            SendCommand = new DelegateCommand(Send,
                    () => !string.IsNullOrWhiteSpace(InputText) &&
                          (!IsBusy || _emergencyStop.Matches(InputText) || _voiceConfirm.HasPending))
                .ObservesProperty(() => InputText)
                .ObservesProperty(() => IsBusy);
            NewChatCommand = new DelegateCommand(NewChat, () => !IsBusy).ObservesProperty(() => IsBusy);
            // 停止:多专员派工后单轮可能跨多次模型调用,必须给用户一个不杀进程的刹车
            StopTurnCommand = new DelegateCommand(StopTurn, () => IsBusy).ObservesProperty(() => IsBusy);
            DeleteConversationCommand = new DelegateCommand<ConversationVm>(DeleteConversation);
            // 空会话引导面板:点示例问题填入输入框(可修改后发送),降低「不知道能聊什么」的门槛
            FillInputCommand = new DelegateCommand<string>(s => { if (!string.IsNullOrEmpty(s)) InputText = s; });

            // 按键说话:点麦克风开始聆听,再点一次停止
            ToggleListenCommand = new DelegateCommand(OnToggleListen);
            ToggleSpeechPlaybackCommand = new DelegateCommand(ToggleSpeechPlayback);
            StopSpeechPlaybackCommand = new DelegateCommand(() => _speechPlayback.Stop());

            ConversationsView = CollectionViewSource.GetDefaultView(Conversations);
            ConversationsView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ConversationVm.DateGroup)));
            ConversationsView.SortDescriptions.Add(new SortDescription(nameof(ConversationVm.LastActiveAt), ListSortDirection.Descending));
            ConversationsView.Filter = FilterConversation;

            _agent.EventRaised += OnAgentEvent;
            _agent.ConfirmationRequested += OnConfirmationRequestedAsync;

            // ── 按键说话(PTT)事件:聆听状态 → 按钮反馈;实时片段 → 边说边出字;整句结束 → 自动发送 ──
            _voice.ListenOnceStateChanged += (s, on) => RunOnUi(() =>
            {
                IsListeningOnce = on;
                if (!on) _listenFeedback = "";
            });
            _voice.ListenPartialChanged += (s, partial) => RunOnUi(() =>
            {
                // 上一句已定稿、现在开始新的一句:先清掉旧文字,只保留这次说对的
                if (_awaitingNewUtterance)
                {
                    _awaitingNewUtterance = false;
                    InputText = "";
                }
                // 边说边在输入框里实时显示识别内容
                _listenFeedback = partial ?? "";
                InputText = partial ?? "";
                RaisePropertyChanged(nameof(InputTipText));
            });
            _voice.ListenOnceCompleted += (s, final) => RunOnUi(() => OnListenOnceCompleted(final ?? ""));

            // 语音唤醒状态只由主界面标题栏的状态弹框展示(单一反馈通道),
            // 聊天窗状态行不再同步——避免"两个正在聆听的框"

            // 启动时恢复历史会话;程序退出时兜底保存一次
            LoadPersistedConversations();
            if (Application.Current != null)
                Application.Current.Exit += (s, e) => SaveConversations(synchronous: true);
            //
            OnLanguageChangedEvent("");
            _eventAggregator.GetEvent<LanguageChangedEvent>()?.Subscribe(OnLanguageChangedEvent);
        }

        #region 本地化相关

        private string _titleTxt;
        private string _subtitleTxt;
        private string _newConversation;
        private string _searchMarkTxt;
        private string _sendTxt;
        private string _miniTipTxt;
        private string _maxiTipTxt;
        private string _sendTipTxt;
        private string _expanderTipTxt;

        public string TitleTxt { get => _titleTxt; set => SetProperty(ref _titleTxt, value); }
        public string SubtitleTxt { get => _subtitleTxt; set => SetProperty(ref _subtitleTxt, value); }
        public string NewConversation { get => _newConversation; set => SetProperty(ref _newConversation, value); }
        public string SearchMarkTxt { get => _searchMarkTxt; set => SetProperty(ref _searchMarkTxt, value); }
        public string SendTxt { get => _sendTxt; set => SetProperty(ref _sendTxt, value); }
        public string MiniTxt { get => _miniTipTxt; set => SetProperty(ref _miniTipTxt, value); }
        public string MaxiTxt { get => _maxiTipTxt; set => SetProperty(ref _maxiTipTxt, value); }
        public string SendTipTxt { get => _sendTipTxt; set => SetProperty(ref _sendTipTxt, value); }
        public string ExpanderTipTxt { get => _expanderTipTxt; set => SetProperty(ref _expanderTipTxt, value); }


        private void OnLanguageChangedEvent(string culture)
        {
            TitleTxt = _localization.GetString("Agent_Title", "小桐");
            SubtitleTxt = _localization.GetString("Agent_Subtitle","实验室智能助手");
            NewConversation = _localization.GetString("Agent_NewConversation"," + 新建对话");
            SearchMarkTxt = _localization.GetString("Agent_Search","搜索对话");
            SendTxt = _localization.GetString("Agent_Send","发送");
            MiniTxt = _localization.GetString("Agent_Minimized_Tip","收起为右下角小组件");
            MaxiTxt = _localization.GetString("Agent_Maximized_Tip","关闭（会话保留）");
            SendTipTxt = _localization.GetString("Agent_Send_Tip","小桐在执行写操作前会逐项与你确认 · 全部操作记录审计日志");
            ExpanderTipTxt = _localization.GetString("Agent_Expander_Tip", "展开对话窗口");
        }

        #endregion

        public DelegateCommand SendCommand { get; }
        public DelegateCommand NewChatCommand { get; }
        public DelegateCommand StopTurnCommand { get; }
        public DelegateCommand<ConversationVm> DeleteConversationCommand { get; }
        public DelegateCommand<string> FillInputCommand { get; }
        public DelegateCommand ToggleSpeechPlaybackCommand { get; }
        public DelegateCommand StopSpeechPlaybackCommand { get; }

        public bool IsSpeechPlaybackActive
        {
            get => _isSpeechPlaybackActive;
            private set => SetProperty(ref _isSpeechPlaybackActive, value);
        }

        public string SpeechPauseText => _isSpeechPaused ? "继续播报" : "暂停播报";

        private void UpdateSpeechPlaybackState()
        {
            IsSpeechPlaybackActive = _speechPlayback.State == SpeechPlaybackState.Playing ||
                                     _speechPlayback.State == SpeechPlaybackState.Paused;
            _isSpeechPaused = _speechPlayback.State == SpeechPlaybackState.Paused;
            RaisePropertyChanged(nameof(SpeechPauseText));
        }

        private void ToggleSpeechPlayback()
        {
            if (_speechPlayback.State == SpeechPlaybackState.Paused) _speechPlayback.Resume();
            else _speechPlayback.Pause();
        }

        private bool TryHandleSpeechControl(string text)
        {
            string command = MaxChemical.Shell.Services.Agent.TextMatchHelper.StripPunctuation(text).Trim();
            if (command is "停止播报" or "停止说话" or "停止朗读" or "取消播报" or "别说了" or "不要再说了" or "闭嘴")
            {
                _speechPlayback.Stop();
                StatusText = "已停止播报";
                return true;
            }
            if (command is "暂停播报" or "暂停说话" or "暂停朗读")
            {
                _speechPlayback.Pause();
                return true;
            }
            if (command is "继续播报" or "继续朗读" or "恢复播报")
            {
                _speechPlayback.Resume();
                return true;
            }
            return false;
        }

        /// <summary>
        /// 停止当前这轮:取消令牌传到总控与专员子循环,未决的确认/选择卡一并释放。
        /// public:急停链路(EmergencyStopService.CancelCurrentTurn)需要调用。
        /// </summary>
        public void StopTurn()
        {
            _speechPlayback.Stop();
            var cts = _turnCts;
            if (cts == null) return;
            StatusText = _localization.GetString("Agent_StatusMsg_Stoping","正在停止…");
            try { cts.Cancel(); }
            catch (ObjectDisposedException) { /* 恰好在本轮收尾之后点到,无事可停 */ }
        }

        public string InputText
        {
            get => _inputText;
            set => SetProperty(ref _inputText, value);
        }

        public string SearchText
        {
            get => _searchText;
            set { if (SetProperty(ref _searchText, value)) ConversationsView.Refresh(); }
        }

        public bool IsBusy
        {
            get => _isBusy;
            set
            {
                if (SetProperty(ref _isBusy, value))
                {
                    RaisePropertyChanged(nameof(IsNotBusy));
                    if (!value) FlushPendingVoiceSend();   // 忙完自动发送排队中的语音指令
                }
            }
        }

        public bool IsNotBusy => !IsBusy;

        // ── 按键说话(PTT)状态:聊天窗麦克风按钮与"边说边出字"反馈 ──
        private bool _isListeningOnce;
        private string _listenFeedback = "";

        public bool IsListeningOnce
        {
            get => _isListeningOnce;
            set
            {
                if (SetProperty(ref _isListeningOnce, value))
                {
                    RaisePropertyChanged(nameof(MicTipTxt));
                    RaisePropertyChanged(nameof(InputTipText));
                }
            }
        }

        /// <summary>麦克风按钮悬浮提示(图标按钮没有文字,靠提示说明)。</summary>
        public string MicTipTxt => _isListeningOnce
            ? "结束聆听并发送"
            : "语音输入:点击开始说话,边说边出字,再点一次发送";

        /// <summary>输入区提示行:有待确认操作时引导文字回复;聆听时显示实时片段;平时显示审计提示。</summary>
        public string InputTipText
        {
            get
            {
                if (_voiceConfirm.HasPending)
                    return "有操作待确认:输入「确认」或「取消」后发送,或直接点卡片按钮";
                if (_isListeningOnce)
                    return string.IsNullOrWhiteSpace(_listenFeedback)
                        ? "正在聆听,请说话…(再点一次喇叭结束并发送)"
                        : $"正在聆听:{_listenFeedback}…(再点一次结束并发送)";
                return _localization.GetString("Agent_Send_Tip", "小桐在执行写操作前会逐项与你确认 · 全部操作记录审计日志");
            }
        }

        public DelegateCommand ToggleListenCommand { get; }

        /// <summary>
        /// 执行值守小组件模式:窗口收缩到屏幕右下角(头像+输入框),
        /// 值守播报只在气泡里显示当前一条;用户发送或需要确认时自动恢复大窗。
        /// </summary>
        public bool IsMiniMode
        {
            get => _isMiniMode;
            set => SetProperty(ref _isMiniMode, value);
        }

        /// <summary>小组件气泡里显示的最新播报(仅迷你模式使用)。</summary>
        public string MiniMessage
        {
            get => _miniMessage;
            set => SetProperty(ref _miniMessage, value);
        }

        /// <summary>值守通道:批次开始执行时进入小组件模式(仅小桐托管的执行)。</summary>
        public void EnterMiniMode()
        {
            RunOnUi(() =>
            {
                MiniMessage = "";
                IsMiniMode = true;
            });
        }

        public string StatusText
        {
            get => _statusText;
            set => SetProperty(ref _statusText, value);
        }

        /// <summary>当前会话;切换时保存/载入 Agent 历史。执行中禁止切换(列表已禁用,双保险)。</summary>
        public ConversationVm Current
        {
            get => _current;
            set
            {
                if (value == null || value == _current) return;
                if (IsBusy) { RaisePropertyChanged(nameof(Current)); return; }

                if (_current != null)
                    _current.History = _agent.ExportHistory();

                SetProperty(ref _current, value);
                _agent.ImportHistory(value.History);
                RequestScroll();
            }
        }

        private bool FilterConversation(object o)
        {
            if (string.IsNullOrWhiteSpace(SearchText)) return true;
            if (o is not ConversationVm c) return false;
            return (c.Title?.IndexOf(SearchText, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0 ||
                   (c.Preview?.IndexOf(SearchText, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0;
        }

        /// <summary>
        /// 值守通道:小桐主动播报(不经模型,直接落到当前会话)。
        /// bringToFront=true 时把窗口带到用户面前(报警/收官);speak=true 时同步语音播报。
        /// </summary>
        public void PostProactiveMessage(string text, bool bringToFront = false, bool speak = false)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            RunOnUi(() =>
            {
                var conv = Current;
                if (conv == null) { NewChat(); conv = Current; }
                if (conv == null) return;

                conv.Items.Add(new ChatItem { IsAssistant = true, Text = text });
                TrimChatItems(conv);
                conv.LastActiveAt = DateTime.Now;
                conv.Preview = Truncate(StripMarkdown(text), 24);
                ConversationsView.Refresh();
                if (conv == Current) RequestScroll();

                if (bringToFront)
                {
                    // 报警/收官/决策这类要用户看到全貌的,恢复大窗再置前
                    if (IsMiniMode) IsMiniMode = false;
                    ShowWindowRequested?.Invoke();
                }
                else if (IsMiniMode)
                {
                    // 常规播报:小组件气泡只展示当前这一条
                    MiniMessage = text;
                }
                SaveConversations();
            });
            if (speak)
                _speechPlayback.Play(new[] { BuildSpeech(text) });
        }

        /// <summary>
        /// 分析工具通道:把图表(JSON 规格)直接插入当前会话——
        /// 不经模型转述,保证图上数据与库中数据一字不差。
        /// </summary>
        public void PostChart(string chartJson)
        {
            if (string.IsNullOrWhiteSpace(chartJson)) return;
            RunOnUi(() =>
            {
                var conv = _activeTurnConversation ?? Current;
                if (conv == null) return;
                conv.Items.Add(new ChatItem { IsChart = true, Text = chartJson });
                TrimChatItems(conv);
                if (conv == Current) RequestScroll();
            });
        }

        /// <summary>供语音链路调用:把语音识别文本作为一轮对话丢给小桐(来源标记「语音」)。</summary>
        public void AskFromVoice(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess())
            {
                dispatcher.BeginInvoke(new Action(() => AskFromVoice(text)));
                return;
            }

            if (TryHandleSpeechControl(text)) return;
            if (IsDuplicateVoiceCommand(text))
            {
                StatusText = "相同语音指令正在处理或刚完成,已忽略重复识别";
                _logger.LogInformation($"忽略重复语音轮次:{text}");
                return;
            }
            if (IsBusy)
            {
                EnqueueVoiceSend(text);
                return;
            }
            _ = RunAsync(text, "语音");
        }

        /// <summary>麦克风按钮:开始聆听;聆听中再点=结束聆听并发送输入框内容。</summary>
        private async void OnToggleListen()
        {
            if (IsListeningOnce)
            {
                _voice.StopListenOnce();   // 结束聆听(按钮变回喇叭形状)
                // 留出约 700ms 让识别服务输出整句定稿,发送内容更完整准确
                await Task.Delay(700);
                SendVoiceTextNow();
            }
            else
            {
                // 麦克风按钮同时承担可靠的播报打断:先取消整个队列,再开启录音。
                _speechPlayback.Stop();
                _awaitingNewUtterance = false;   // 新一轮聆听:第一句不清用户之前手动输入的文字
                _voice.StartListenOnce();
            }
        }

        /// <summary>把输入框里的语音内容发送出去(第二次点击喇叭时触发)。</summary>
        private void SendVoiceTextNow()
        {
            string text = InputText?.Trim();
            if (string.IsNullOrWhiteSpace(text)) return;

            // 有待确认的写操作:内容若为确认词则解决确认卡,不再当消息发送/排队
            if (_voiceConfirm.HasPending)
            {
                var verdict = _voiceConfirm.TryResolve(text);
                if (verdict.HasValue)
                {
                    InputText = "";
                    return;
                }
            }

            if (TryHandleSpeechControl(text))
            {
                InputText = "";
                return;
            }

            InputText = "";
            if (IsBusy)
            {
                if (EnqueueVoiceSend(text))
                    _speechPlayback.Play(new[] { "小桐正在处理上一件事,你的指令已排队,处理完自动发送。" });
                return;
            }
            _ = RunAsync(text, "语音");
        }

        /// <summary>
        /// 整句定稿回调:把干净的最终文本替换输入框里的中间内容(不自动发送,
        /// 等用户再点一次喇叭按钮才发送);空串是本轮聆听结束标记。
        /// 定稿后标记"新一句待开始"——用户重新说时先清掉旧文字,只保留说对的。
        /// 有待确认的写操作时,说出的「确认/取消」直接解决确认卡,
        /// 并立即结束本轮聆听、把麦克风还给唤醒环——不等再点一次喇叭。
        /// </summary>
        private void OnListenOnceCompleted(string final)
        {
            if (string.IsNullOrWhiteSpace(final)) return;
            if (!IsListeningOnce) return;   // 已结束聆听,忽略迟到句子

            if (_voiceConfirm.HasPending)
            {
                var verdict = _voiceConfirm.TryResolve(final);
                if (verdict.HasValue)
                {
                    InputText = "";
                    _awaitingNewUtterance = false;
                    _voice.StopListenOnce();   // 确认卡已解决,结束聆听,唤醒环自动恢复
                    return;
                }
            }

            InputText = final;
            _awaitingNewUtterance = true;
        }

        /// <summary>上一句已定稿,下一句开始时要先清掉输入框旧文字(重新说只保留说对的)。</summary>
        private bool _awaitingNewUtterance;

        /// <summary>忙碌期间到达的语音指令(按到达顺序执行)。</summary>
        private readonly Queue<string> _pendingVoiceSends = new();
        private const int MaxPendingVoiceSends = 8;
        private string _activeVoiceCommandKey = "";
        private string _lastCompletedVoiceCommandKey = "";
        private DateTime _lastCompletedVoiceCommandUtc = DateTime.MinValue;

        private static string NormalizeVoiceCommand(string text)
        {
            string normalized = string.Concat(MaxChemical.Shell.Services.Agent.TextMatchHelper
                .StripPunctuation(text).Where(c => !char.IsWhiteSpace(c))).ToLowerInvariant();
            // 仅用于重复判断,不改写实际发送给Agent的原文。
            foreach (string filler in new[] { "嗯", "呃", "啊", "请", "帮我", "给我", "一下", "一个" })
                normalized = normalized.Replace(filler, "");
            return normalized;
        }

        private bool IsDuplicateVoiceCommand(string text)
        {
            string key = NormalizeVoiceCommand(text);
            if (key.Length == 0) return false;
            if (IsBusy && key == _activeVoiceCommandKey) return true;
            if (_pendingVoiceSends.Any(pending => NormalizeVoiceCommand(pending) == key)) return true;
            return key == _lastCompletedVoiceCommandKey &&
                   DateTime.UtcNow - _lastCompletedVoiceCommandUtc < TimeSpan.FromSeconds(6);
        }

        private bool EnqueueVoiceSend(string text)
        {
            if (_pendingVoiceSends.Count >= MaxPendingVoiceSends)
            {
                StatusText = "待处理语音指令较多,请稍后再说";
                return false;
            }
            if (_pendingVoiceSends.Count > 0 &&
                string.Equals(_pendingVoiceSends.Last(), text, StringComparison.Ordinal))
                return true;

            _pendingVoiceSends.Enqueue(text);
            StatusText = $"已收到语音指令,当前任务完成后按顺序执行(排队 {_pendingVoiceSends.Count} 条)";
            return true;
        }

        private void FlushPendingVoiceSend()
        {
            if (_pendingVoiceSends.Count == 0) return;
            _ = RunAsync(_pendingVoiceSends.Dequeue(), "语音");
        }

        private void Send()
        {
            string text = InputText?.Trim();
            if (string.IsNullOrWhiteSpace(text)) return;
            if (TryHandleSpeechControl(text))
            {
                InputText = "";
                return;
            }

            // 急停优先:任何时刻都走本地直连(RunAsync 开头处理)
            if (_emergencyStop.Matches(text))
            {
                InputText = "";
                if (IsMiniMode) IsMiniMode = false;
                _ = RunAsync(text);
                return;
            }

            // 有待确认的写操作:输入框发送「确认/确认执行」批准、「取消」拒绝,
            // 解决后 Agent 继续执行后续动作——与点卡片按钮、语音说「确认」完全等价
            if (_voiceConfirm.HasPending)
            {
                var verdict = _voiceConfirm.TryResolve(text);
                if (verdict.HasValue)
                {
                    InputText = "";
                    return;   // 卡片已解决(卡片上显示 已确认执行/已取消),Agent 自动继续
                }
                InputText = "";
                StatusText = "有操作待确认:请输入「确认」或「取消」后发送,或直接点卡片按钮";
                return;
            }

            InputText = "";
            if (IsMiniMode) IsMiniMode = false; // 用户开口即恢复大窗继续对话
            _ = RunAsync(text);
        }

        private async Task RunAsync(string text, string source = "文本")
        {
            _speechPlayback.Stop();
            _activeTurnToolNames.Clear();

            // ── 急停本地直连:关键词正则直接触发,不走大模型 ——
            // 断网、模型思考中、任何时刻都立即生效(方案安全设计:急停本地化)。
            // 播报与设备停机由 EmergencyStopService 统一执行,这里只落聊天记录。
            if (_emergencyStop.Matches(text))
            {
                RunOnUi(() =>
                {
                    var conv = Current;
                    if (conv == null) { NewChat(); conv = Current; }
                    if (conv == null) return;
                    conv.Items.Add(new ChatItem { IsUser = true, Text = text });
                    TrimChatItems(conv);
                    conv.LastActiveAt = DateTime.Now;
                    if (!conv.TitleLocked) { conv.Title = Truncate(text, 16); conv.TitleLocked = true; }
                    ConversationsView.Refresh();
                    if (conv == Current) RequestScroll();
                });

                await _emergencyStop.ExecuteAsync(text);
                RunOnUi(() => SaveConversations());
                return;
            }

            var conv = Current;
            if (conv == null) { NewChat(); conv = Current; }
            _activeTurnConversation = conv;
            if (source == "语音") _activeVoiceCommandKey = NormalizeVoiceCommand(text);

            var cts = new System.Threading.CancellationTokenSource();
            _turnCts = cts;
            IsBusy = true;
            StatusText = _localization.GetString("Agent_StatusMsg_Think","小桐思考中");
            try
            {
                string reply = await Task.Run(() => _agent.AskAsync(text, cts.Token, source));

                conv.History = _agent.ExportHistory();
                conv.LastActiveAt = DateTime.Now;
                if (!conv.TitleLocked)
                {
                    conv.Title = Truncate(text, 16);
                    conv.TitleLocked = true;
                }
                if (!string.IsNullOrWhiteSpace(reply))
                    conv.Preview = Truncate(StripMarkdown(reply), 24);
                RunOnUi(() => ConversationsView.Refresh());

                // 操作结果只读结论;闲聊和知识问答按自然段完整播放。
                var chunks = CreateSpeechChunks(text, reply);
                if (chunks.Count > 0) _speechPlayback.Play(chunks);
            }
            catch (Exception ex)
            {
                _logger.LogError($"对话执行失败: {ex.Message}");
            }
            finally
            {
                _activeTurnConversation = null;
                _activeTurnToolNames.Clear();
                _turnCts = null;
                cts.Dispose();
                StatusText = _localization.GetString("Agent_StatusMsg_Ready","就绪");
                SaveConversations();
                if (source == "语音")
                {
                    _lastCompletedVoiceCommandKey = _activeVoiceCommandKey;
                    _lastCompletedVoiceCommandUtc = DateTime.UtcNow;
                    _activeVoiceCommandKey = "";
                }
                IsBusy = false;
            }
        }

        private void NewChat()
        {
            var conv = new ConversationVm();
            // 问候不再预置成消息:空会话由引导面板承担(问候+分组示例,Items.Count==0 才显示)。
            // 只有 ApiKey 未配置这种硬性障碍才播一条,避免用户对着面板白试。
            if (!_agent.IsConfigured)
            {
                conv.Items.Add(new ChatItem
                {
                    IsAssistant = true,
                    Text = "尚未配置 DeepSeek ApiKey(appsettings.json → DeepSeek.ApiKey),配置后重启即可开始对话。"
                });
            }
            Conversations.Add(conv);

            if (_current != null)
                _current.History = _agent.ExportHistory();
            SetProperty(ref _current, conv, nameof(Current));
            _agent.ImportHistory(conv.History);

            StatusText = _agent.IsConfigured ? _localization.GetString("Agent_StatusMsg_Ready", "就绪") : _localization.GetString("Agent_StatusMsg_NoConfig", "未配置 ApiKey");
            RunOnUi(() => ConversationsView.Refresh());
            RequestScroll();
            SaveConversations();
        }

        // ── 会话持久化 ──

        /// <summary>启动时从存档恢复会话;无存档则新建一个空会话。</summary>
        private void LoadPersistedConversations()
        {
            List<ConversationRecord> records;
            try { records = _storage.Load(); }
            catch { records = new List<ConversationRecord>(); }

            if (records == null || records.Count == 0)
            {
                NewChat();
                return;
            }

            foreach (var r in records.OrderBy(x => x.LastActiveAt))
            {
                var conv = new ConversationVm
                {
                    Id = r.Id,
                    CreatedAt = r.CreatedAt,
                    TitleLocked = r.TitleLocked,
                    History = r.History ?? new List<AgentMessage>()
                };
                conv.Title = string.IsNullOrWhiteSpace(r.Title) ? "新对话" : r.Title;
                conv.Preview = r.Preview ?? "";
                conv.LastActiveAt = r.LastActiveAt;

                foreach (var item in r.Items ?? new List<ChatItemRecord>())
                    conv.Items.Add(FromRecord(item));

                Conversations.Add(conv);
            }

            var latest = Conversations.OrderByDescending(c => c.LastActiveAt).FirstOrDefault();
            if (latest == null) { NewChat(); return; }

            SetProperty(ref _current, latest, nameof(Current));
            _agent.ImportHistory(latest.History);
            StatusText = _agent.IsConfigured ? _localization.GetString("Agent_StatusMsg_Ready", "就绪") : _localization.GetString("Agent_StatusMsg_NoConfig", "未配置 ApiKey");
            RunOnUi(() => ConversationsView.Refresh());
            RequestScroll();
        }

        /// <summary>把全部会话快照写盘(界面线程取快照,序列化落盘放后台)。</summary>
        private void SaveConversations(bool synchronous = false)
        {
            try
            {
                if (_current != null)
                    _current.History = _agent.ExportHistory();

                var records = Conversations.Select(ToRecord).ToList();
                // 序号在取快照的此刻分配:后台任务乱序执行时,旧快照会被存储层丢弃
                long version = _storage.NextVersion();
                if (synchronous) _storage.Save(records, version);
                else _ = Task.Run(() => _storage.Save(records, version));
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"会话持久化失败: {ex.Message}");
            }
        }

        private static ConversationRecord ToRecord(ConversationVm c) => new()
        {
            Id = c.Id,
            Title = c.Title,
            TitleLocked = c.TitleLocked,
            Preview = c.Preview,
            CreatedAt = c.CreatedAt,
            LastActiveAt = c.LastActiveAt,
            History = new List<AgentMessage>(c.History ?? new List<AgentMessage>()),
            Items = c.Items.Select(i => new ChatItemRecord
            {
                Kind = i.IsUser ? "user" : i.IsConfirm ? "confirm" : i.IsToolLine ? "tool" : i.IsChart ? "chart" : i.IsChoice ? "choice" : "assistant",
                Text = i.Text,
                ToolName = i.ToolName,
                ToolStage = i.ToolStage,
                ToolSuccess = i.ToolSuccess,
                IsError = i.IsError,
                Approved = i.IsConfirm && i.Approved,
                ChoiceResult = i.IsChoice ? i.ChoiceResult : null,
                Time = i.Time
            }).ToList()
        };

        /// <summary>存档条目还原为界面条目;确认卡片一律还原为已决状态(历史卡片不可再点)。</summary>
        private static ChatItem FromRecord(ChatItemRecord r)
        {
            var item = new ChatItem
            {
                Text = r.Text ?? "",
                ToolName = r.ToolName,
                ToolSuccess = r.ToolSuccess,
                IsError = r.IsError,
                Time = r.Time
            };
            switch (r.Kind)
            {
                case "user":
                    item.IsUser = true;
                    break;
                case "tool":
                    item.IsToolLine = true;
                    item.ToolStage = string.IsNullOrEmpty(r.ToolStage) ? "完成" : r.ToolStage;
                    break;
                case "confirm":
                    item.IsConfirm = true;
                    item.IsResolved = true;
                    item.Approved = r.Approved;
                    break;
                case "chart":
                    item.IsChart = true;
                    break;
                case "choice":
                    // 历史选择卡一律还原为已决状态(不可再点),只展示问题与所选结果
                    item.IsChoice = true;
                    item.IsResolved = true;
                    item.ChoiceResult = r.ChoiceResult ?? "";
                    break;
                default:
                    item.IsAssistant = true;
                    break;
            }
            return item;
        }

        /// <summary>删除会话:先经统一确认弹窗;执行中不允许;删的是当前会话时切到最近的一个,没有则新建。</summary>
        private void DeleteConversation(ConversationVm conv)
        {
            if (conv == null || IsBusy) return;

            bool ok;
            try
            {
                ok = _dialog.ShowConfirmation(
                    $"将删除对话「{conv.Title}」及其全部消息记录,删除后不可恢复。\n\n确定删除?",
                    "删除对话");
            }
            catch
            {
                ok = false;
            }
            if (!ok) return;

            Conversations.Remove(conv);

            if (_current == conv)
            {
                var next = Conversations.OrderByDescending(c => c.LastActiveAt).FirstOrDefault();
                if (next == null)
                {
                    NewChat(); // 内部会保存
                    return;
                }
                SetProperty(ref _current, next, nameof(Current));
                _agent.ImportHistory(next.History);
                RequestScroll();
            }

            RunOnUi(() => ConversationsView.Refresh());
            SaveConversations();
        }

        // ── Agent 事件 → 界面条目(路由到发起本轮的会话) ──

        // 单会话可视化条目上限:长时间 DOE 值守会持续往对话里追加工具行/播报,
        // 不封顶会让对话可视化树无界增长(内存漏点之一)。上限给得宽松,正常使用几乎碰不到。
        private const int MaxVisibleChatItems = 500;

        /// <summary>超过上限时从最旧端裁剪会话条目;绝不误删尚未处理的确认/选择卡(它们总在最新端)。</summary>
        private static void TrimChatItems(ConversationVm conv)
        {
            var items = conv.Items;
            while (items.Count > MaxVisibleChatItems)
            {
                var oldest = items[0];
                if ((oldest.IsConfirm || oldest.IsChoice) && !oldest.IsResolved)
                    break; // 极端保护:最旧的竟是未决交互卡,停手,不裁
                items.RemoveAt(0);
            }
        }

        private void OnAgentEvent(AgentEvent e)
        {
            RunOnUi(() =>
            {
                var conv = _activeTurnConversation ?? Current;
                if (conv == null) return;
                var items = conv.Items;

                switch (e.Kind)
                {
                    case AgentEventKind.UserMessage:
                        items.Add(new ChatItem { IsUser = true, Text = e.Text });
                        break;
                    case AgentEventKind.AssistantThinking:
                        StatusText = e.Text;
                        break;
                    case AgentEventKind.AssistantMessage:
                        if (!string.IsNullOrWhiteSpace(e.Text))
                            items.Add(new ChatItem { IsAssistant = true, Text = e.Text });
                        break;
                    case AgentEventKind.ToolCallStarted:
                        if (!string.IsNullOrWhiteSpace(e.ToolName)) _activeTurnToolNames.Add(e.ToolName);
                        items.Add(new ChatItem { IsToolLine = true, ToolStage = "执行", Text = e.Text, ToolName = e.ToolName });
                        break;
                    case AgentEventKind.ToolCallFinished:
                        items.Add(new ChatItem
                        {
                            IsToolLine = true,
                            ToolStage = e.ToolSuccess ? "完成" : "失败",
                            Text = e.Text,
                            ToolName = e.ToolName,
                            ToolSuccess = e.ToolSuccess
                        });
                        break;
                    case AgentEventKind.Error:
                        items.Add(new ChatItem { IsError = true, IsAssistant = true, Text = "执行出错:" + e.Text });
                        break;
                }
                TrimChatItems(conv);
                if (conv == Current) RequestScroll();
            });
        }

        // ── 写操作确认:插入确认卡片,等用户点按钮;2 分钟超时自动取消 ──

        private Task<bool> OnConfirmationRequestedAsync(PendingToolAction action)
        {
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            RunOnUi(() =>
            {
                var conv = _activeTurnConversation ?? Current;
                if (conv == null)
                {
                    tcs.TrySetResult(false);
                    return;
                }

                var item = new ChatItem
                {
                    IsConfirm = true,
                    Text = action.Summary,
                    ToolName = action.DisplayName,
                    Decision = tcs
                };

                // 卡片唯一的解决路径:按钮、超时、停止取消、语音确认全部经这里,
                // 保证界面状态与决策一致,且解决后必定清除语音确认登记(防幽灵待确认项)。
                void Resolve(bool approved) => RunOnUi(() =>
                {
                    if (item.IsResolved) return;
                    item.IsResolved = true;
                    item.Approved = approved;
                    tcs.TrySetResult(approved);
                    _voiceConfirm.ClearPending();
                    RaisePropertyChanged(nameof(InputTipText));
                });

                item.ApproveCommand = new DelegateCommand(() => Resolve(true));
                item.RejectCommand = new DelegateCommand(() => Resolve(false));
                conv.Items.Add(item);
                if (conv == Current) RequestScroll();
                if (IsMiniMode) IsMiniMode = false; // 确认卡必须在大窗里看全
                ShowWindowRequested?.Invoke();

                // 语音/文字确认:可直接说「确认/好的」批准、「取消/不要」拒绝;
                // 也可在输入框发送「确认执行/取消」——与点按钮完全等价
                _voiceConfirm.SetPending(() => Resolve(true), () => Resolve(false));
                // 确认提示播报保持极短(完整动作说明在确认卡片上):
                // 播报越短,麦克风越早恢复,用户的「确认执行」越快被听到
                _speechPlayback.Play(new[] { "有操作需要确认,请说确认或取消" });
                RaisePropertyChanged(nameof(InputTipText));

                // 确认超时:2 分钟无人响应视为取消,避免 Agent 会话被无限挂起
                _ = Task.Delay(TimeSpan.FromMinutes(2)).ContinueWith(_ => Resolve(false));

                // 用户点「停止」时立刻按取消放行,不让未决卡片把取消拖满超时
                RegisterTurnCancel(() => Resolve(false));
            });

            return tcs.Task;
        }

        /// <summary>把回调挂到本轮取消令牌上(UI 线程执行);没有进行中的轮次则不挂。</summary>
        private void RegisterTurnCancel(Action onCancel)
        {
            var cts = _turnCts;
            if (cts == null) return;
            try
            {
                cts.Token.Register(() => RunOnUi(onCancel));
            }
            catch (ObjectDisposedException) { /* 本轮恰好收尾,卡片走超时兜底 */ }
        }

        /// <summary>
        /// 供工具调用的阻塞式选择卡(与确认卡同一套等待机制):
        /// 单选渲染单选框、多选渲染复选框;用户点确定返回所选项(多选以顿号连接);
        /// 取消或超时(3 分钟)返回空串,工具据此改用文字沟通。
        /// </summary>
        public Task<string> AskUserChoiceAsync(string question, IReadOnlyList<string> options, bool multiSelect)
        {
            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

            RunOnUi(() =>
            {
                var conv = _activeTurnConversation ?? Current;
                if (conv == null || options == null || options.Count == 0)
                {
                    tcs.TrySetResult("");
                    return;
                }

                var item = new ChatItem
                {
                    IsChoice = true,
                    IsMultiSelect = multiSelect,
                    Text = question ?? ""
                };
                foreach (var o in options)
                    item.Options.Add(new ChoiceOption { Text = o });

                item.SubmitChoiceCommand = new DelegateCommand(() =>
                {
                    if (item.IsResolved) return;
                    var sel = item.Options.Where(o => o.IsSelected).Select(o => o.Text).ToList();
                    if (sel.Count == 0) return; // 一项没选,确定不生效
                    item.IsResolved = true;
                    item.ChoiceResult = string.Join("、", sel);
                    tcs.TrySetResult(item.ChoiceResult);
                    SaveConversations();
                });
                item.CancelChoiceCommand = new DelegateCommand(() =>
                {
                    if (item.IsResolved) return;
                    item.IsResolved = true;
                    item.ChoiceResult = "";
                    tcs.TrySetResult("");
                    SaveConversations();
                });

                conv.Items.Add(item);
                if (conv == Current) RequestScroll();
                if (IsMiniMode) IsMiniMode = false; // 选择卡必须在大窗里看全
                ShowWindowRequested?.Invoke();

                // 选择超时:3 分钟无人响应视为取消,避免 Agent 会话被无限挂起
                _ = Task.Delay(TimeSpan.FromMinutes(3)).ContinueWith(_ => RunOnUi(() =>
                {
                    if (!item.IsResolved)
                    {
                        item.IsResolved = true;
                        item.ChoiceResult = "";
                        tcs.TrySetResult("");
                    }
                }));

                // 用户点「停止」时立刻按取消放行,不让未决卡片把取消拖满超时
                RegisterTurnCancel(() =>
                {
                    if (item.IsResolved) return;
                    item.IsResolved = true;
                    item.ChoiceResult = "";
                    tcs.TrySetResult("");
                });
            });

            return tcs.Task;
        }

        // ── 文本工具 ──

        /// <summary>剥离 Markdown 符号,得到适合播报/预览的纯文本。</summary>
        internal static string StripMarkdown(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var lines = text.Replace("\r\n", "\n").Split('\n')
                .Where(l => !l.TrimStart().StartsWith("|"))          // 表格行不播报
                .Select(l => Regex.Replace(l, @"^#{1,6}\s*", ""))    // 标题井号
                .Select(l => Regex.Replace(l, @"^[-*•]\s+", ""))     // 列表符
                .ToList();
            string s = string.Join("。", lines.Where(l => !string.IsNullOrWhiteSpace(l)));
            s = s.Replace("**", "").Replace("`", "");
            return Regex.Replace(s, @"\s+", " ").Trim();
        }

        private static string BuildSpeech(string reply)
        {
            // 只播报第一句(结论/结果),不全文播报——完整内容看对话窗口
            string s = StripMarkdown(reply);
            if (string.IsNullOrWhiteSpace(s)) return "已完成,详情见对话窗口。";

            // 取第一句(按句末标点/换行切)
            int idx = s.IndexOfAny(new[] { '。', '！', '？', '!', '?', '；', ';', '\n' });
            string first = idx > 0 ? s.Substring(0, idx + 1) : s;
            if (first.Length > 40) first = first.Substring(0, 40) + "…";   // 首句超长只截到 40 字:播报越短,下一句命令越早被听到
            return first;
        }

        private List<string> CreateSpeechChunks(string userText, string reply)
        {
            // 操作结果仍只读第一句;知识意图、无工具的闲聊/问答、资料检索回答则完整朗读。
            bool knowledgeIntent = Regex.IsMatch(userText ?? "",
                "有什么用|有什么用途|用途|是什么|什么叫|为什么|原理|介绍|讲解|怎么用|如何使用|哪些作用|什么功能|告诉我|说说|讲讲");
            bool commandIntent = Regex.IsMatch(userText ?? "",
                "打开|关闭|设置|启动|停止|暂停|继续|新建|保存|执行|生成|连接|查询|查看|读取|列出");
            bool fullAnswer = knowledgeIntent || (!commandIntent && _activeTurnToolNames.Count == 0) ||
                              _activeTurnToolNames.Any(name => name is "web_search" or "kb_search");
            if (!fullAnswer) return new List<string> { BuildSpeech(reply) };

            string fullText = StripMarkdown(reply);
            // Markdown 链接只读可见文字,不朗读冗长 URL;正文不做 40 字或全文长度截断。
            fullText = Regex.Replace(fullText, @"\[([^\]]+)\]\([^\)]+\)", "$1");
            if (string.IsNullOrWhiteSpace(fullText)) return new List<string> { BuildSpeech(reply) };

            var chunks = new List<string>();
            var current = new System.Text.StringBuilder();
            foreach (string sentence in Regex.Split(fullText, @"(?<=[。！？!?；;])")
                         .Select(s => s.Trim()).Where(s => s.Length > 0))
            {
                if (current.Length > 0 && current.Length + sentence.Length > 1200)
                {
                    chunks.Add(current.ToString());
                    current.Clear();
                }
                if (sentence.Length > 1200)
                {
                    if (current.Length > 0)
                    {
                        chunks.Add(current.ToString());
                        current.Clear();
                    }
                    for (int offset = 0; offset < sentence.Length; offset += 1200)
                        chunks.Add(sentence.Substring(offset, Math.Min(1200, sentence.Length - offset)));
                    continue;
                }
                current.Append(sentence);
            }
            if (current.Length > 0) chunks.Add(current.ToString());
            return chunks;
        }

        private static string Truncate(string s, int len)
            => string.IsNullOrEmpty(s) || s.Length <= len ? s : s.Substring(0, len) + "…";

        private void RequestScroll() => ScrollToEndRequested?.Invoke();

        private static void RunOnUi(Action action)
        {
            var app = Application.Current;
            if (app?.Dispatcher == null || app.Dispatcher.CheckAccess()) action();
            else app.Dispatcher.BeginInvoke(action);
        }
    }
}

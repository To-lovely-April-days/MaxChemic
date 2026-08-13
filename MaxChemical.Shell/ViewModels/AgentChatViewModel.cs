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
            IEventAggregator eventAggregator)
        {
            _agent = agent;
            _voice = voice;
            _dialog = dialog;
            _logger = LogManager.GetLogger<AgentChatViewModel>();
            _localization = localization;
            _eventAggregator = eventAggregator;

            SendCommand = new DelegateCommand(Send, () => !IsBusy && !string.IsNullOrWhiteSpace(InputText))
                .ObservesProperty(() => InputText)
                .ObservesProperty(() => IsBusy);
            NewChatCommand = new DelegateCommand(NewChat, () => !IsBusy).ObservesProperty(() => IsBusy);
            // 停止:多专员派工后单轮可能跨多次模型调用,必须给用户一个不杀进程的刹车
            StopTurnCommand = new DelegateCommand(StopTurn, () => IsBusy).ObservesProperty(() => IsBusy);
            DeleteConversationCommand = new DelegateCommand<ConversationVm>(DeleteConversation);
            // 空会话引导面板:点示例问题填入输入框(可修改后发送),降低「不知道能聊什么」的门槛
            FillInputCommand = new DelegateCommand<string>(s => { if (!string.IsNullOrEmpty(s)) InputText = s; });

            ConversationsView = CollectionViewSource.GetDefaultView(Conversations);
            ConversationsView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ConversationVm.DateGroup)));
            ConversationsView.SortDescriptions.Add(new SortDescription(nameof(ConversationVm.LastActiveAt), ListSortDirection.Descending));
            ConversationsView.Filter = FilterConversation;

            _agent.EventRaised += OnAgentEvent;
            _agent.ConfirmationRequested += OnConfirmationRequestedAsync;

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

        /// <summary>停止当前这轮:取消令牌传到总控与专员子循环,未决的确认/选择卡一并释放。</summary>
        private void StopTurn()
        {
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
            set { if (SetProperty(ref _isBusy, value)) RaisePropertyChanged(nameof(IsNotBusy)); }
        }

        public bool IsNotBusy => !IsBusy;

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
                _voice?.SpeakAsync(BuildSpeech(text));
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

        /// <summary>供语音链路调用:把语音识别文本作为一轮对话丢给小桐。</summary>
        public void AskFromVoice(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            if (IsBusy)
            {
                // 忙碌中不静默吞掉语音指令:给一句播报,让用户知道要等
                _voice?.SpeakAsync("小桐正在处理上一件事,请稍候或在窗口点停止。");
                return;
            }
            _ = RunAsync(text);
        }

        private void Send()
        {
            string text = InputText?.Trim();
            if (string.IsNullOrWhiteSpace(text)) return;
            InputText = "";
            if (IsMiniMode) IsMiniMode = false; // 用户开口即恢复大窗继续对话
            _ = RunAsync(text);
        }

        private async Task RunAsync(string text)
        {
            var conv = Current;
            if (conv == null) { NewChat(); conv = Current; }
            _activeTurnConversation = conv;

            var cts = new System.Threading.CancellationTokenSource();
            _turnCts = cts;
            IsBusy = true;
            StatusText = _localization.GetString("Agent_StatusMsg_Think","小桐思考中");
            try
            {
                string reply = await Task.Run(() => _agent.AskAsync(text, cts.Token));

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

                // 语音播报纯文本摘要(剥离 Markdown 与表格)
                string speech = BuildSpeech(reply);
                if (!string.IsNullOrWhiteSpace(speech))
                    _voice?.SpeakAsync(speech);
            }
            catch (Exception ex)
            {
                _logger.LogError($"对话执行失败: {ex.Message}");
            }
            finally
            {
                _activeTurnConversation = null;
                IsBusy = false;
                StatusText = _localization.GetString("Agent_StatusMsg_Ready","就绪");
                _turnCts = null;
                cts.Dispose();
                SaveConversations();
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
                item.ApproveCommand = new DelegateCommand(() =>
                {
                    if (item.IsResolved) return;
                    item.IsResolved = true;
                    item.Approved = true;
                    tcs.TrySetResult(true);
                });
                item.RejectCommand = new DelegateCommand(() =>
                {
                    if (item.IsResolved) return;
                    item.IsResolved = true;
                    item.Approved = false;
                    tcs.TrySetResult(false);
                });
                conv.Items.Add(item);
                if (conv == Current) RequestScroll();
                if (IsMiniMode) IsMiniMode = false; // 确认卡必须在大窗里看全
                ShowWindowRequested?.Invoke();

                // 确认超时:2 分钟无人响应视为取消,避免 Agent 会话被无限挂起
                _ = Task.Delay(TimeSpan.FromMinutes(2)).ContinueWith(_ => RunOnUi(() =>
                {
                    if (!item.IsResolved)
                    {
                        item.IsResolved = true;
                        item.Approved = false;
                        tcs.TrySetResult(false);
                    }
                }));

                // 用户点「停止」时立刻按取消放行,不让未决卡片把取消拖满超时
                RegisterTurnCancel(() =>
                {
                    if (item.IsResolved) return;
                    item.IsResolved = true;
                    item.Approved = false;
                    tcs.TrySetResult(false);
                });
            });

            _voice?.SpeakAsync("请确认:" + StripMarkdown(action.Summary));
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
            string s = StripMarkdown(reply);
            if (string.IsNullOrWhiteSpace(s)) return "已完成,详情见对话窗口。";
            return s.Length <= 160 ? s : s.Substring(0, 160) + "。详情见对话窗口。";
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

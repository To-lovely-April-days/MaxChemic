using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using MaxChemical.Logging;
using MaxChemical.Modules.DOE.Data;
using MaxChemical.Modules.DOE.Models;
using MaxChemical.Modules.DOE.Services;
using MaxChemical.Shell.Services.Agent.Tools;
using Prism.Ioc;

namespace MaxChemical.Shell.Services.Agent
{
    /// <summary>
    /// 小桐主动值守:订阅 DOE 执行引擎事件,把进度、结果、异常主动播报到对话窗,
    /// 并注入 Agent 会话历史,让后续追问自带上下文。
    /// 执行期间以看门狗周期巡检设备连接,掉线立即报警、恢复即时通报。
    /// 全程只读、不经模型、不打断执行;任何内部异常都不允许影响实验。
    /// </summary>
    public sealed class XiaoTongSentinel
    {
        private readonly IContainerProvider _container;
        private readonly ILogService _logger = LogManager.GetLogger<XiaoTongSentinel>();

        private ViewModels.AgentChatViewModel _chatVm;
        private XiaoTongAgent _agent;
        private AgentToolContext _ctx;
        private bool _started;

        // ── 批次运行态 ──
        private string _batchId;
        private string _batchName = "";
        private int _totalRuns;
        private int _executedOffset;          // 启动时已执行的组数(完成/失败/待录结果,续跑场景)
        private Dictionary<int, int> _runPosByIndex; // RunIndex → 看板「第 N 组」(稳定编号)
        private int _completedInSession;      // 本次启动以来完成的组数(含失败)
        private DateTime _batchStartAt;
        private Dictionary<string, double> _prevResponses;

        // ── 设备看门狗 ──
        private System.Timers.Timer _watchdog;
        private readonly object _watchGate = new();
        private readonly Dictionary<string, bool> _connState = new();
        private readonly HashSet<string> _alerted = new();

        public XiaoTongSentinel(IContainerProvider container)
        {
            _container = container;
        }

        /// <summary>装配并开始值守(由 AgentBootstrapper 在模块加载完成后调用一次)。</summary>
        public void Start(ViewModels.AgentChatViewModel chatVm, XiaoTongAgent agent)
        {
            if (_started) return;
            _started = true;

            _chatVm = chatVm;
            _agent = agent;
            _ctx = new AgentToolContext(_container);

            try
            {
                var exec = _container.Resolve<IDOEExecutionService>();
                exec.RunCompleted += (s, e) => Safe(() => OnRunCompletedAsync(e));
                exec.BatchCompleted += (s, e) => Safe(() => OnBatchCompletedAsync(e));

                var events = _container.Resolve<Prism.Events.IEventAggregator>();
                events.GetEvent<MaxChemical.Modules.DOE.Events.DOEBatchStartedEvent>()
                      .Subscribe(batchId => Safe(() => OnBatchStartedAsync(batchId)),
                                 Prism.Events.ThreadOption.BackgroundThread,
                                 keepSubscriberReferenceAlive: true);

                // 决策闭环:小桐托管的项目批次收官后,把规则引擎的决策报告解读到对话里。
                // 用户手动执行的批次不托管——原生决策弹窗照旧,小桐不插手。
                events.GetEvent<MaxChemical.Modules.DOE.Events.DOERoundCompletedEvent>()
                      .Subscribe(p => Safe(() => OnRoundDecisionAsync(p)),
                                 Prism.Events.ThreadOption.BackgroundThread,
                                 keepSubscriberReferenceAlive: true);

                // 先猜后验:小桐托管的批次,超 3σ 报警立刻弹到用户面前;收官校准自检并入播报。
                // 逐组的命中/收窄信息看板图上已有,不在对话里刷屏。
                events.GetEvent<MaxChemical.Modules.DOE.Events.DOEPredictVerifyEvent>()
                      .Subscribe(p => Safe(() => OnPredictVerifyAsync(p)),
                                 Prism.Events.ThreadOption.BackgroundThread,
                                 keepSubscriberReferenceAlive: true);

                // 智能决策自动建轮:新批次要接进托管名单并播报批次ID,
                // 否则值守与专员都不知道下一轮已存在,用户问起只能空对空。
                events.GetEvent<MaxChemical.Modules.DOE.Events.AutoPilotBatchCreatedEvent>()
                      .Subscribe(p => Safe(() => OnAutoPilotBatchCreatedAsync(p)),
                                 Prism.Events.ThreadOption.BackgroundThread,
                                 keepSubscriberReferenceAlive: true);

                _watchdog = new System.Timers.Timer(8000) { AutoReset = true };
                _watchdog.Elapsed += (s, e) => Safe(() => Task.Run(WatchDevices));

                _logger.LogInformation("小桐值守已开启:DOE 执行播报 + 设备掉线看门狗");
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"小桐值守装配失败(不影响其他功能): {ex.Message}");
            }
        }

        /// <summary>
        /// 先猜后验转播(仅小桐托管的批次):3σ 报警弹窗+入上下文;收官校准结论入上下文。
        /// 2σ 内的逐组信息不转播——执行小窗的图上已有,对话里刷屏只会稀释报警的分量。
        /// </summary>
        private Task OnPredictVerifyAsync(MaxChemical.Modules.DOE.Events.DOEPredictVerifyPayload p)
        {
            if (p == null || !AgentManagedBatches.Contains(p.BatchId)) return Task.CompletedTask;

            if (p.IsAlarm)
            {
                string text = "[值守播报] 先猜后验报警:" + p.Message;
                _chatVm?.PostProactiveMessage(text, bringToFront: true, speak: true);
                _agent?.InjectAssistantNote(text);
            }
            else if (p.IsCompleted && !string.IsNullOrEmpty(p.CalibrationText))
            {
                string text = "[值守播报] " + p.CalibrationText;
                _chatVm?.PostProactiveMessage(text, bringToFront: false, speak: false);
                _agent?.InjectAssistantNote(text);
            }
            return Task.CompletedTask;
        }

        private void Safe(Func<Task> action)
        {
            _ = Task.Run(async () =>
            {
                try { await action().ConfigureAwait(false); }
                catch (Exception ex) { _logger.LogWarning($"值守处理异常: {ex.Message}"); }
            });
        }

        // ══════════════ 批次开始 ══════════════

        private async Task OnBatchStartedAsync(string batchId)
        {
            _batchId = batchId;
            _batchStartAt = DateTime.Now;
            _completedInSession = 0;
            _prevResponses = null;
            _batchName = "";
            _totalRuns = 0;
            _executedOffset = 0;
            _runPosByIndex = null;

            try
            {
                var repo = _container.Resolve<IDOERepository>();
                var batch = await repo.GetBatchWithDetailsAsync(batchId).ConfigureAwait(false);
                if (batch != null)
                {
                    _batchName = batch.BatchName ?? "";
                    var active = batch.Runs.Where(r => r.Status != DOERunStatus.Skipped)
                        .OrderBy(r => r.RunIndex).ToList();
                    _totalRuns = active.Count;
                    // 待录结果(延迟录入)也算已执行:续跑时编号才不会从头数
                    _executedOffset = active.Count(r =>
                        r.Status == DOERunStatus.Completed || r.Status == DOERunStatus.Failed ||
                        r.Status == DOERunStatus.WaitingResponse);
                    // 稳定的「第 N 组」映射:与执行看板/回填工具同口径,重试穿插也不漂移
                    _runPosByIndex = active
                        .Select((r, idx) => (r.RunIndex, Pos: idx + 1))
                        .ToDictionary(t => t.RunIndex, t => t.Pos);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"值守读取批次信息失败: {ex.Message}");
            }

            // 看门狗:记录初始连接状态后开始巡检
            lock (_watchGate)
            {
                _connState.Clear();
                _alerted.Clear();
            }
            SnapshotDeviceStates();
            _watchdog?.Start();

            // 小桐托管的执行:聊天窗收缩为右下角值守小组件(头像+输入框+当前播报气泡);
            // 用户手动启动的批次不动聊天窗
            if (AgentManagedBatches.Contains(batchId))
            {
                try { _chatVm?.EnterMiniMode(); } catch { }
            }

            int remaining = Math.Max(0, _totalRuns - _executedOffset);
            string text = $"值守开启:批次「{_batchName}」开始执行,共 {_totalRuns} 组" +
                          (_executedOffset > 0 ? $"(已完成 {_executedOffset} 组,续跑剩余 {remaining} 组)" : "") +
                          "。每组完成我会主动播报结果;执行期间设备掉线会立即报警。";
            Post(text, bringToFront: false, speak: false);
        }

        // ══════════════ 逐组播报 ══════════════

        private Task OnRunCompletedAsync(DOERunCompletedEventArgs e)
        {
            if (string.IsNullOrEmpty(_batchId)) return Task.CompletedTask;

            _completedInSession++;
            // 组号优先用 RunIndex 稳定映射(与看板/回填工具同口径);映射缺失才退回累加估计
            int position = _runPosByIndex != null && _runPosByIndex.TryGetValue(e.RunIndex, out var pos)
                ? pos
                : Math.Min(_executedOffset + _completedInSession, Math.Max(_totalRuns, 1));
            string posText = _totalRuns > 0 ? $"{position}/{_totalRuns}" : position.ToString();

            if (!e.IsSuccessful)
            {
                Post($"注意:第 {posText} 组执行失败,引擎已跳过并继续后续组。全部结束后可在执行看板「重试失败组」。",
                     bringToFront: false, speak: false);
                return Task.CompletedTask;
            }

            var sb = new StringBuilder();
            sb.Append($"**第 {posText} 组完成**");

            // 平均每组用时与预计剩余时间
            if (_completedInSession > 0)
            {
                var avg = TimeSpan.FromSeconds(
                    (DateTime.Now - _batchStartAt).TotalSeconds / _completedInSession);
                int remaining = Math.Max(0, _totalRuns - position);
                if (remaining > 0)
                    sb.Append($" · 预计剩余 {remaining} 组约 {FormatDuration(TimeSpan.FromSeconds(avg.TotalSeconds * remaining))}");
            }
            sb.AppendLine();

            if (e.ResponseValues != null && e.ResponseValues.Count > 0)
            {
                sb.Append("- 响应:").AppendLine(FormatWithDelta(e.ResponseValues, _prevResponses));
                _prevResponses = new Dictionary<string, double>(e.ResponseValues);
            }
            if (e.FactorValues != null && e.FactorValues.Count > 0)
                sb.Append("- 因子:").AppendLine(FormatDict(e.FactorValues));

            Post(sb.ToString().TrimEnd(), bringToFront: false, speak: false);
            return Task.CompletedTask;
        }

        // ══════════════ 批次收官 ══════════════

        private async Task OnBatchCompletedAsync(DOEBatchCompletedEventArgs e)
        {
            _watchdog?.Stop();
            string batchId = _batchId;
            _batchId = null;
            if (string.IsNullOrEmpty(batchId)) return;

            var total = DateTime.Now - _batchStartAt;

            if (e.FinalStatus == DOEBatchStatus.Aborted)
            {
                Post($"批次「{_batchName}」执行已中止" +
                     (string.IsNullOrWhiteSpace(e.StopReason) ? "" : $"(原因:{e.StopReason})") +
                     $",已完成 {e.CompletedRuns} 组。需要我查询进度详情或分析已有数据,随时说。",
                     bringToFront: true, speak: true);
                return;
            }

            // 统计必须从库里取真实状态:e.TotalRuns-e.CompletedRuns 会把
            // 「停止条件触发后挂起的组」误算成失败,值守绝不能报假警
            int completed = e.CompletedRuns, failed = 0, suspended = 0, waiting = 0;
            DOEBatch batch = null;
            string respName = null;
            bool minimize = false;
            try
            {
                var repo = _container.Resolve<IDOERepository>();
                batch = await repo.GetBatchWithDetailsAsync(batchId).ConfigureAwait(false);
                if (batch != null)
                {
                    completed = batch.Runs.Count(r => r.Status == DOERunStatus.Completed);
                    failed = batch.Runs.Count(r => r.Status == DOERunStatus.Failed);
                    suspended = batch.Runs.Count(r => r.Status == DOERunStatus.Suspended);
                    waiting = batch.Runs.Count(r => r.Status == DOERunStatus.WaitingResponse);
                    respName = batch.Responses?.FirstOrDefault()?.ResponseName;
                    minimize = await IsMinimizeGoalAsync(repo, batchId, respName).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"值守读取批次统计失败: {ex.Message}");
            }

            var sb = new StringBuilder();
            // 延迟录入批次(阶梯扫描)收官时结果还没回来:按「流程完成 + 待录结果」播,不能报成「成功 0 组」
            if (waiting > 0)
                sb.Append($"批次「{_batchName}」执行完成:流程完成 {completed + waiting} 组,其中 {waiting} 组待录结果" +
                          "(把各样品的离线分析结果告诉我,我来回填)");
            else
                sb.Append($"批次「{_batchName}」执行完成:成功 {completed} 组");
            if (failed > 0) sb.Append($",失败 {failed} 组");
            sb.Append($",总用时 {FormatDuration(total)}。");
            if (!string.IsNullOrWhiteSpace(e.StopReason))
                sb.Append($"触发停止条件提前收官:{e.StopReason}" +
                          (suspended > 0 ? $",其余 {suspended} 组已挂起" : "") + "。");
            sb.AppendLine();

            // 最优组:按第一个响应的优化方向判定(读 Desirability 配置,缺省按越大越好)
            try
            {
                if (batch != null && !string.IsNullOrEmpty(respName))
                {
                    var done = batch.Runs.Where(r => r.Status == DOERunStatus.Completed).ToList();
                    (int RunIndex, double Value, string Factors)? best = null;
                    foreach (var run in done)
                    {
                        var resp = ParseJsonNumbers(run.ResponseValuesJson);
                        if (!resp.TryGetValue(respName, out double v)) continue;
                        bool better = best == null ||
                                      (minimize ? v < best.Value.Value : v > best.Value.Value);
                        if (better)
                            best = (run.RunIndex, v, FormatDict(ParseJsonNumbers(run.FactorValuesJson)));
                    }
                    if (best != null)
                        sb.AppendLine($"响应「{respName}」最优({(minimize ? "越小越好" : "越大越好")}):" +
                                      $"第 {best.Value.RunIndex} 组,{best.Value.Value:0.####}" +
                                      (string.IsNullOrEmpty(best.Value.Factors) ? "" : $"(因子:{best.Value.Factors})") + "。");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"值守汇总最优组失败: {ex.Message}");
            }

            sb.Append(waiting > 0
                ? "样品分析出来后按组号报给我(如:第 1 组转化率 42%),补齐后就能做统计分析或动力学拟合。"
                : "后续可以让我查看 DOE 分析页做统计检验,或基于这轮结果设计下一轮优化实验。");
            Post(sb.ToString(), bringToFront: true, speak: true);
        }

        /// <summary>第一个响应的优化方向是否为最小化(读批次 Desirability 配置;解析失败按最大化)。</summary>
        private static async Task<bool> IsMinimizeGoalAsync(IDOERepository repo, string batchId, string respName)
        {
            if (string.IsNullOrEmpty(respName)) return false;
            try
            {
                string json = await repo.GetDesirabilityConfigJsonAsync(batchId).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(json)) return false;
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Array) return false;
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    if (!el.TryGetProperty("ResponseName", out var n) ||
                        !string.Equals(n.GetString(), respName, StringComparison.Ordinal)) continue;
                    if (!el.TryGetProperty("Goal", out var g)) return false;
                    // Newtonsoft 默认把枚举存成整数(Minimize=1),兼容字符串两种形态
                    if (g.ValueKind == JsonValueKind.Number) return g.GetInt32() == 1;
                    if (g.ValueKind == JsonValueKind.String)
                        return string.Equals(g.GetString(), "Minimize", StringComparison.OrdinalIgnoreCase);
                    return false;
                }
            }
            catch { }
            return false;
        }

        // ══════════════ 智能决策自动建轮:托管延续 + 播报批次ID ══════════════

        /// <summary>源批次 → 自动建出的下一轮(决策播报据此改口,不再问「要不要生成下一轮」)。</summary>
        private readonly System.Collections.Concurrent.ConcurrentDictionary<
            string, MaxChemical.Modules.DOE.Events.AutoPilotBatchCreatedPayload> _autoNextByBatch = new();

        private Task OnAutoPilotBatchCreatedAsync(MaxChemical.Modules.DOE.Events.AutoPilotBatchCreatedPayload p)
        {
            if (p == null || string.IsNullOrEmpty(p.NextBatchId)) return Task.CompletedTask;

            // 只接管小桐启动的优化闭环:用户手动跑的项目由原生界面负责,这里不插话
            if (!AgentManagedBatches.Contains(p.SourceBatchId)) return Task.CompletedTask;

            // 托管延续:自动建出的下一轮也算小桐的批次,否则后续轮次的播报、
            // 决策解读、先猜后验全部脱管,问小桐它一无所知
            AgentManagedBatches.Mark(p.NextBatchId);
            _autoNextByBatch[p.SourceBatchId] = p;

            string runText = p.RunCount > 0 ? $",{p.RunCount} 组实验" : "";
            string text = p.AutoTriggered
                ? $"智能决策已自动创建下一轮实验:项目「{p.ProjectName}」第 {p.NextRoundNumber} 轮,{p.DesignMethodDisplay}{runText},批次ID {p.NextBatchId}。" +
                  "后续查配置、执行、分析都用这个批次ID。要我安排执行就说一声。"
                : $"你已在决策弹窗采纳推荐,下一轮已创建:项目「{p.ProjectName}」第 {p.NextRoundNumber} 轮,{p.DesignMethodDisplay}{runText},批次ID {p.NextBatchId}。";

            Post(text, bringToFront: p.AutoTriggered, speak: p.AutoTriggered);
            return Task.CompletedTask;
        }

        // ══════════════ 决策闭环:收官后解读智能决策报告 ══════════════

        private string _lastDecisionBatchId;

        private async Task OnRoundDecisionAsync(MaxChemical.Modules.DOE.Events.DOERoundCompletedPayload p)
        {
            if (p == null || string.IsNullOrEmpty(p.BatchId)) return;

            // 只接管小桐启动的批次:用户没用小桐,决策由原生弹窗处理,这里不参与
            if (!AgentManagedBatches.Contains(p.BatchId)) return;

            // 防重:同一批次的决策报告只播报一次(重试失败组再次收官时不重复刷屏)
            if (_lastDecisionBatchId == p.BatchId) return;
            _lastDecisionBatchId = p.BatchId;

            // 让常规收官汇总先落进对话,再出决策报告
            await Task.Delay(1500).ConfigureAwait(false);

            try
            {
                var engine = _container.Resolve<MaxChemical.Modules.DOE.Services.IProjectDecisionEngine>();
                var summary = await engine.AnalyzeRoundAsync(p.ProjectId, p.BatchId).ConfigureAwait(false);
                if (summary == null) return;

                string projectName = p.ProjectId;
                try
                {
                    var proj = await _container.Resolve<IDOERepository>()
                        .GetProjectAsync(p.ProjectId).ConfigureAwait(false);
                    if (proj != null) projectName = proj.ProjectName;
                }
                catch { }

                var sb = new StringBuilder();
                sb.AppendLine(DecisionHelper.FormatSummary(summary, projectName));
                sb.AppendLine();

                // 智能推荐若已自动建好下一轮,不能再问「要不要生成」——批次已经存在了
                if (_autoNextByBatch.TryGetValue(p.BatchId, out var auto))
                    sb.Append($"智能决策已按此建议自动创建第 {auto.NextRoundNumber} 轮(批次ID {auto.NextBatchId}),无需再生成预览;要我安排执行就说一声。");
                else
                    sb.Append(summary.Recommendation == NextStepRecommendation.Complete
                        ? "引擎判定优化目标已达成,建议收官。需要我解读细节或安排验证轮,直接说。"
                        : "要按引擎建议生成下一轮实验预览吗?回复同意我就生成;想看备选动作或自行指定,也直接说。");

                Post(sb.ToString(), bringToFront: true, speak: true);
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"值守决策解读失败: {ex.Message}");
            }
        }

        // ══════════════ 设备看门狗 ══════════════

        /// <summary>记录当前各可通讯设备的连接状态基线(批次启动时调用);全局模拟模式下无需采集。</summary>
        private void SnapshotDeviceStates()
        {
            try
            {
                if (_ctx.GetGlobalSimulationMode() == true) return;
                foreach (var d in _ctx.GetCanvasDevices())
                {
                    if (d.Device == null || d.Device.IsSimulationMode) continue;
                    if (!ConnectDevicesTool.IsConnectable(d.Device)) continue;
                    string display = _ctx.GetDisplayName(d.DeviceId);
                    lock (_watchGate) _connState[display] = d.Device.IsConnected;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"值守设备基线采集失败: {ex.Message}");
            }
        }

        private int _watchBusy;

        private Task WatchDevices()
        {
            if (string.IsNullOrEmpty(_batchId)) return Task.CompletedTask;

            // 全局模拟模式:无物理链路,掉线巡检没有意义
            if (_ctx.GetGlobalSimulationMode() == true) return Task.CompletedTask;

            // 重入闸:巡检可能因 UI 线程繁忙拖长,禁止两次巡检重叠(否则会重复报警)
            if (System.Threading.Interlocked.CompareExchange(ref _watchBusy, 1, 0) != 0)
                return Task.CompletedTask;

            try
            {
                List<(string Display, bool Connected)> now = new();
                try
                {
                    foreach (var d in _ctx.GetCanvasDevices())
                    {
                        if (d.Device == null || d.Device.IsSimulationMode) continue;
                        if (!ConnectDevicesTool.IsConnectable(d.Device)) continue;
                        now.Add((_ctx.GetDisplayName(d.DeviceId), d.Device.IsConnected));
                    }
                }
                catch { return Task.CompletedTask; }

                foreach (var (display, connected) in now)
                {
                    // 状态迁移判定与报警登记必须在同一把锁内原子完成
                    bool dropped = false, recovered = false;
                    lock (_watchGate)
                    {
                        _connState.TryGetValue(display, out bool wasConnected);
                        _connState[display] = connected;

                        if (wasConnected && !connected && !_alerted.Contains(display))
                        {
                            _alerted.Add(display);
                            dropped = true;
                        }
                        else if (!wasConnected && connected && _alerted.Contains(display))
                        {
                            _alerted.Remove(display);
                            recovered = true;
                        }
                    }

                    if (dropped)
                        Post($"警报:设备「{display}」在实验执行中掉线!当前批次仍在运行,建议立即检查通讯线路/电源;" +
                             "需要我暂停批次请直接说「暂停实验」。",
                             bringToFront: true, speak: true);
                    else if (recovered)
                        Post($"设备「{display}」已恢复连接,继续值守。", bringToFront: false, speak: false);
                }
                return Task.CompletedTask;
            }
            finally
            {
                System.Threading.Interlocked.Exchange(ref _watchBusy, 0);
            }
        }

        // ══════════════ 输出通道 ══════════════

        /// <summary>播报到对话窗 + 注入 Agent 历史(带值守标记,追问时模型知道自己已播报过)。</summary>
        private void Post(string text, bool bringToFront, bool speak)
        {
            try { _chatVm?.PostProactiveMessage(text, bringToFront, speak); }
            catch (Exception ex) { _logger.LogWarning($"值守播报失败: {ex.Message}"); }

            try { _agent?.InjectAssistantNote("[值守播报] " + text); }
            catch { }
        }

        // ══════════════ 文本工具 ══════════════

        private static Dictionary<string, double> ParseJsonNumbers(string json)
        {
            var dict = new Dictionary<string, double>();
            if (string.IsNullOrWhiteSpace(json)) return dict;
            try
            {
                using var doc = JsonDocument.Parse(json);
                foreach (var p in doc.RootElement.EnumerateObject())
                    if (p.Value.ValueKind == JsonValueKind.Number)
                        dict[p.Name] = p.Value.GetDouble();
            }
            catch { }
            return dict;
        }

        private static string FormatDict(Dictionary<string, double> values, int max = 6)
            => values == null ? "" : string.Join(",", values.Take(max).Select(kv => $"{kv.Key}={kv.Value:0.####}"));

        /// <summary>响应值带环比:转化率=87.5(较上组 +2.1)。</summary>
        private static string FormatWithDelta(Dictionary<string, double> current, Dictionary<string, double> prev)
        {
            var parts = new List<string>();
            foreach (var kv in current.Take(4))
            {
                string s = $"{kv.Key}={kv.Value:0.####}";
                if (prev != null && prev.TryGetValue(kv.Key, out double p))
                {
                    double delta = kv.Value - p;
                    if (Math.Abs(delta) > 1e-9)
                        s += $"(较上组 {(delta > 0 ? "+" : "")}{delta:0.##})";
                }
                parts.Add(s);
            }
            return string.Join(",", parts);
        }

        private static string FormatDuration(TimeSpan t)
        {
            if (t.TotalSeconds < 60) return "不到 1 分钟";
            if (t.TotalMinutes < 60) return $"{Math.Ceiling(t.TotalMinutes):0} 分钟";
            return $"{(int)t.TotalHours} 小时 {t.Minutes} 分钟";
        }
    }
}

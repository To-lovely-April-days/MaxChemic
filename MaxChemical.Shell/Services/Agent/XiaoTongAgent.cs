using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MaxChemical.Logging;

namespace MaxChemical.Shell.Services.Agent
{
    /// <summary>
    /// 小桐 Agent 编排器:多轮工具调用循环。
    /// 用户输入(语音/文字)→ DeepSeek(带技能表)→ 工具调用(写操作先确认)→ 结果回填 → …… → 最终回复。
    /// 界面通过 EventRaised 展示全过程;写操作通过 ConfirmationRequested 请求用户确认。
    /// </summary>
    public sealed class XiaoTongAgent : IDisposable
    {
        private readonly ILogService _logger;
        private readonly DeepSeekClient _client;
        private readonly List<IAgentTool> _tools = new();
        private readonly List<AgentMessage> _history = new();
        private readonly SemaphoreSlim _gate = new(1, 1);
        private Dictionary<string, string> _turnToolResults = new();

        private Func<string> _labSnapshotProvider = () => "";
        private Func<string, string> _memoryProvider = _ => "";

        /// <summary>
        /// 总控暴露给模型的工具名单(null=全部)。注册表 _tools 始终保有全部工具:
        /// 专员执行与历史会话里的旧工具调用都按注册表查找,不受暴露名单限制。
        /// </summary>
        private HashSet<string> _advertisedToolNames;

        /// <summary>会话过程事件(工具调用可视化、消息流)。可能在后台线程触发,订阅方自行调度。</summary>
        public event Action<AgentEvent> EventRaised;

        /// <summary>
        /// 写操作确认回调:返回 true=执行,false=取消。
        /// 未订阅时一律视为取消(安全默认)。
        /// </summary>
        public event Func<PendingToolAction, Task<bool>> ConfirmationRequested;

        /// <summary>单次提问允许的最大工具轮数(防失控循环)。撞限时会做一次无工具收尾汇报,不是硬中断。</summary>
        public int MaxToolRounds { get; set; } = 16;

        /// <summary>保留的历史消息条数上限(不含 system)。</summary>
        public int MaxHistoryMessages { get; set; } = 40;

        /// <summary>历史总字符预算:超出后从最旧的整轮开始裁剪,控制每轮请求的上下文规模。</summary>
        public int MaxHistoryChars { get; set; } = 24000;

        /// <summary>入库历史时单条工具结果保留的最大字符数(当轮对话仍用全文,只影响后续轮次)。</summary>
        public int MaxToolResultCharsInHistory { get; set; } = 1600;

        public bool IsBusy { get; private set; }
        public bool IsConfigured => _client.IsConfigured;

        /// <summary>
        /// 当前这轮对话的取消令牌:IAgentTool.ExecuteAsync 不带 ct,
        /// 派工工具经由此属性把总控的取消传递进专员子循环。
        /// </summary>
        public CancellationToken CurrentTurnToken { get; private set; }

        public XiaoTongAgent(ILogService logger, DeepSeekClient client)
        {
            _logger = logger;
            _client = client;
        }

        public void RegisterTool(IAgentTool tool)
        {
            if (_tools.Any(t => t.Name == tool.Name))
                throw new InvalidOperationException($"重复注册工具:{tool.Name}");
            _tools.Add(tool);
        }

        public IReadOnlyList<IAgentTool> Tools => _tools;

        /// <summary>设定总控实际暴露给模型的工具名单(派工、常用只读查询与交互工具)。</summary>
        public void SetAdvertisedTools(IEnumerable<string> names)
            => _advertisedToolNames = names == null ? null : new HashSet<string>(names);

        /// <summary>注入"实验室实时快照"提供者(设备清单/状态/当前流程/DOE 概况)。</summary>
        public void SetLabSnapshotProvider(Func<string> provider) => _labSnapshotProvider = provider ?? (() => "");

        /// <summary>注入"长期记忆召回"提供者:按当前用户输入返回相关记忆片段文本(无相关记忆返回空串)。</summary>
        public void SetMemoryProvider(Func<string, string> provider) => _memoryProvider = provider ?? ((_) => "");

        /// <summary>AI 操作审计日志(由 AgentBootstrapper 注入;未装配时调用点静默跳过)。</summary>
        public Audit.AiAuditLog AuditLog { get; set; }

        public void ResetConversation()
        {
            lock (_history) _history.Clear();
        }

        /// <summary>导出当前会话历史(切换会话时由界面保存)。</summary>
        public List<AgentMessage> ExportHistory()
        {
            lock (_history) return new List<AgentMessage>(_history);
        }

        /// <summary>
        /// 值守通道:把主动播报写入会话历史(assistant 角色,不触发模型调用),
        /// 让用户随后的追问自带上下文——模型知道自己刚播报过什么。
        /// </summary>
        public void InjectAssistantNote(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            lock (_history)
            {
                _history.Add(new AgentMessage { Role = AgentRole.Assistant, Content = text });
            }
        }

        /// <summary>载入另一个会话的历史(界面切换会话时调用)。</summary>
        public void ImportHistory(IEnumerable<AgentMessage> history)
        {
            lock (_history)
            {
                _history.Clear();
                if (history != null) _history.AddRange(history);
            }
        }

        /// <summary>
        /// 处理一句用户输入,返回小桐的最终文字回复(供 TTS 播报与界面显示)。
        /// source 标注输入来源(文本/语音),写入审计日志。
        /// </summary>
        public async Task<string> AskAsync(string userText, CancellationToken ct = default, string source = "文本")
        {
            if (string.IsNullOrWhiteSpace(userText)) return "";

            await _gate.WaitAsync(ct).ConfigureAwait(false);
            IsBusy = true;
            CurrentTurnToken = ct;
            _turnToolResults = new Dictionary<string, string>();
            List<AgentMessage> newTurnRef = null; // 中断路径也要能提交已完成的部分
            try
            {
                Raise(AgentEventKind.UserMessage, userText);
                AuditLog?.LogTurnStart(source, userText);

                var messages = new List<AgentMessage> { AgentMessage.FromSystem(BuildSystemPrompt(userText)) };
                lock (_history) messages.AddRange(_history);
                var userMsg = AgentMessage.FromUser(userText);
                messages.Add(userMsg);

                var newTurnMessages = new List<AgentMessage> { userMsg };
                newTurnRef = newTurnMessages;

                // 总控只暴露名单内工具;注册表仍持有全部工具供执行查找
                IReadOnlyList<IAgentTool> exposed = _advertisedToolNames == null
                    ? _tools
                    : _tools.Where(t => _advertisedToolNames.Contains(t.Name)).ToList();

                for (int round = 0; round <= MaxToolRounds; round++)
                {
                    ct.ThrowIfCancellationRequested();
                    Raise(AgentEventKind.AssistantThinking, round == 0 ? "思考中…" : $"继续推理(第 {round + 1} 步)…");

                    AgentMessage assistant = await _client.ChatAsync(messages, exposed, ct).ConfigureAwait(false);
                    messages.Add(assistant);
                    newTurnMessages.Add(assistant);

                    if (assistant.ToolCalls == null || assistant.ToolCalls.Count == 0)
                    {
                        // 最终文字回复
                        string reply = assistant.Content ?? "";
                        CommitHistory(newTurnMessages);
                        Raise(AgentEventKind.AssistantMessage, reply);
                        AuditLog?.LogReply(reply);
                        return reply;
                    }

                    // 模型附带的过程说明也展示出来
                    if (!string.IsNullOrWhiteSpace(assistant.Content))
                        Raise(AgentEventKind.AssistantMessage, assistant.Content);

                    foreach (var call in assistant.ToolCalls)
                    {
                        ct.ThrowIfCancellationRequested();
                        var toolMsg = await ExecuteToolCallAsync(call, ct).ConfigureAwait(false);
                        messages.Add(toolMsg);
                        newTurnMessages.Add(toolMsg);
                    }
                }

                CommitHistory(newTurnMessages);

                // 撞上工具轮数上限:不生硬中断,让模型基于已拿到的工具结果做一次「无工具收尾」,
                // 把已确认的结论、卡点和需要用户补充的信息交代清楚——部分结果也比「拆小再试」有用。
                Raise(AgentEventKind.AssistantThinking, "步数较多,整理阶段性结论…");
                try
                {
                    var wrapMessages = new List<AgentMessage>(messages)
                    {
                        new AgentMessage
                        {
                            Role = AgentRole.User,
                            Content = "[系统] 本次任务的工具调用轮数已达安全上限,禁止再调用任何工具。" +
                                      "请基于以上已获得的工具结果,立即向用户做阶段性汇报:①已经确认的结论与数据;" +
                                      "②卡在哪一步、原因是什么(比如同一查询反复失败);③需要用户补充什么才能继续(比如从候选清单里选一个)。" +
                                      "如实汇报,未完成的部分不许编造。"
                        }
                    };
                    var wrap = await _client.ChatAsync(wrapMessages, null, ct).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(wrap?.Content))
                    {
                        CommitHistory(new List<AgentMessage>
                        {
                            new AgentMessage { Role = AgentRole.Assistant, Content = wrap.Content }
                        });
                        Raise(AgentEventKind.AssistantMessage, wrap.Content);
                        AuditLog?.LogReply(wrap.Content);
                        return wrap.Content;
                    }
                }
                catch { }

                const string overrun = "这一步的任务链比较长,我先停在这里。上面已查到的结果可以先用;把剩下的问题单独问我,我接着做。";
                Raise(AgentEventKind.AssistantMessage, overrun);
                AuditLog?.LogReply(overrun);
                return overrun;
            }
            catch (OperationCanceledException)
            {
                CommitPartialTurn(newTurnRef, "本轮被用户取消。");
                Raise(AgentEventKind.Error, "已取消");
                AuditLog?.LogEvent("取消", "本轮被用户取消:" + userText);
                return "好的,已取消。已执行的部分(含已确认的写操作)结果保留,可以接着问我。";
            }
            catch (Exception ex)
            {
                _logger.LogError($"小桐处理失败: {ex.Message}");
                CommitPartialTurn(newTurnRef, "本轮处理中途失败:" + ex.Message);
                Raise(AgentEventKind.Error, ex.Message);
                AuditLog?.LogError(ex.Message);
                return "抱歉,我这边出了点问题:" + ex.Message;
            }
            finally
            {
                _turnToolResults.Clear();
                IsBusy = false;
                _gate.Release();
            }
        }

        /// <summary>
        /// 中断路径的历史提交:整轮丢弃会连已确认执行的写操作记录(批次ID等)一起丢,
        /// 下一轮模型两眼一抹黑只能盲重派。这里把已完成的部分补齐协议后入库:
        /// assistant(tool_calls) 若缺对应 tool 回复,后续每次请求都会被 API 拒绝,必须补上占位。
        /// </summary>
        private void CommitPartialTurn(List<AgentMessage> turn, string abortNote)
        {
            try
            {
                if (turn == null || turn.Count <= 1) return; // 只有用户消息,没有可保留的进展

                var answered = new HashSet<string>(
                    turn.Where(m => m.Role == AgentRole.Tool && m.ToolCallId != null).Select(m => m.ToolCallId));
                foreach (var call in turn.Where(m => m.ToolCalls != null).SelectMany(m => m.ToolCalls))
                    if (call?.Id != null && !answered.Contains(call.Id))
                        turn.Add(AgentMessage.FromTool(call.Id, "(本轮中断,该调用被打断,结果未返回;若其中有已经用户确认的写操作,实际已生效)"));

                turn.Add(new AgentMessage
                {
                    Role = AgentRole.Assistant,
                    Content = "[系统] " + abortNote +
                              " 以上工具结果是已执行的部分,其中经用户确认的写操作(如批次入库)已实际生效,编号以工具结果为准。"
                });
                CommitHistory(turn);
            }
            catch { /* 中断路径的兜底,不再抛 */ }
        }

        /// <summary>
        /// 专员子循环:用专员自己的提示词与工具子集执行一次派工任务,返回给总控的工作汇报。
        /// 专员没有会话历史(任务必须自包含);确认卡、选择卡与图表直插走的都是同一套通道,
        /// 用户在界面上看到的是同一个对话流,工具过程行带专员前缀归属。
        /// </summary>
        public async Task<string> RunSpecialistAsync(SpecialistProfile profile, string task, string context = null, CancellationToken ct = default)
        {
            if (profile == null) return "错误:未知专员。";
            if (string.IsNullOrWhiteSpace(task)) return "错误:派工任务为空,task 必须写清楚做什么并附上关键数值与编号。";

            string snapshot;
            try { snapshot = _labSnapshotProvider() ?? ""; }
            catch (Exception ex) { snapshot = "(快照获取失败:" + ex.Message + ")"; }

            var toolset = _tools.Where(t => profile.ToolNames.Contains(t.Name)).ToList();

            var messages = new List<AgentMessage>
            {
                AgentMessage.FromSystem(profile.BuildSystemPrompt(snapshot)),
                AgentMessage.FromUser(string.IsNullOrWhiteSpace(context)
                    ? task
                    : task + "\n\n补充背景:\n" + context)
            };

            Raise(AgentEventKind.AssistantThinking, $"{profile.DisplayName}接手处理…");

            for (int round = 0; round <= profile.MaxToolRounds; round++)
            {
                ct.ThrowIfCancellationRequested();

                AgentMessage assistant = await _client.ChatAsync(messages, toolset, ct).ConfigureAwait(false);
                messages.Add(assistant);

                if (assistant.ToolCalls == null || assistant.ToolCalls.Count == 0)
                    return assistant.Content ?? "";

                // 专员的过程narration只进状态行,不进聊天流(最终由总控统一转述)。
                // 必须先洗成一行:原文带换行与 Markdown,直接上状态行会撑破标题栏气泡
                if (!string.IsNullOrWhiteSpace(assistant.Content))
                    Raise(AgentEventKind.AssistantThinking,
                        Truncate($"{profile.DisplayName}:{SummarizeForStatusLine(assistant.Content, true)}", 60));

                foreach (var call in assistant.ToolCalls)
                {
                    ct.ThrowIfCancellationRequested();
                    var toolMsg = await ExecuteToolCallAsync(call, ct, toolset, profile.DisplayName).ConfigureAwait(false);
                    messages.Add(toolMsg);
                }
            }

            // 轮数耗尽:与总控同样的「无工具收尾」纪律,交阶段性汇报而不是硬中断
            try
            {
                messages.Add(new AgentMessage
                {
                    Role = AgentRole.User,
                    Content = "[系统] 本次任务的工具调用轮数已达安全上限,禁止再调用任何工具。" +
                              "请立即以工作汇报形式总结:①已确认的结论与数据;②卡在哪一步、原因是什么;" +
                              "③需要总控或用户补充什么才能继续。如实汇报,未完成的部分不许编造。"
                });
                var wrap = await _client.ChatAsync(messages, null, ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(wrap?.Content)) return wrap.Content;
            }
            catch { }

            return $"{profile.DisplayName}的任务链过长,已中止。请把任务拆小后重新派工,或向用户说明卡点。";
        }

        private async Task<AgentMessage> ExecuteToolCallAsync(ToolCall call, CancellationToken ct,
            IReadOnlyList<IAgentTool> toolset = null, string actorLabel = null)
        {
            string name = call.Function?.Name ?? "";
            string rawArgs = call.Function?.Arguments ?? "";

            var source = toolset ?? _tools;
            var tool = source.FirstOrDefault(t => t.Name == name);
            if (tool == null)
            {
                // 命令白名单拦截:模型幻觉出的未知工具一律记录,绝不执行
                _logger.LogWarning($"模型请求了不存在的工具: {name}");
                AuditLog?.LogToolCall(actorLabel, name, rawArgs, null, false, "白名单拦截:不存在该工具");
                return AgentMessage.FromTool(call.Id, $"错误:不存在名为 {name} 的工具");
            }

            string display = actorLabel == null ? tool.DisplayName : actorLabel + "·" + tool.DisplayName;

            JsonElement args;
            try
            {
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(call.Function.Arguments) ? "{}" : call.Function.Arguments);
                args = doc.RootElement.Clone();
            }
            catch (Exception ex)
            {
                AuditLog?.LogToolCall(actorLabel, name, rawArgs, null, false, "参数不是合法 JSON:" + ex.Message);
                return AgentMessage.FromTool(call.Id, "错误:工具参数不是合法 JSON——" + ex.Message);
            }

            bool cacheResult = tool.RequiresConfirmation || name.StartsWith("assign_", StringComparison.Ordinal);
            string cacheKey = cacheResult ? name + "\n" + NormalizeJson(args) : null;
            if (cacheKey != null && _turnToolResults.TryGetValue(cacheKey, out string cachedResult))
                return CreateToolResultMessage(call.Id, name, cachedResult);

            // ── 写操作确认闸门 ──
            if (tool.RequiresConfirmation)
            {
                string summary;
                try { summary = tool.DescribeAction(args); }
                catch { summary = $"执行 {display}"; }

                var pending = new PendingToolAction
                {
                    ToolName = tool.Name,
                    DisplayName = display,
                    Summary = summary,
                    Arguments = args
                };

                Raise(AgentEventKind.ConfirmRequired, summary, display);

                bool approved = false;
                var handler = ConfirmationRequested;
                if (handler != null)
                {
                    try { approved = await handler(pending).ConfigureAwait(false); }
                    catch (Exception ex)
                    {
                        _logger.LogError($"确认流程异常: {ex.Message}");
                        approved = false;
                    }
                }
                else
                {
                    _logger.LogWarning("无确认通道,写操作被安全拒绝");
                }

                if (!approved)
                {
                    const string resultText = "用户取消了该操作。请不要重试,改为询问用户下一步。";
                    _turnToolResults[cacheKey] = resultText;
                    Raise(AgentEventKind.ToolCallFinished, "用户已取消:" + summary, display, success: false);
                    AuditLog?.LogToolCall(actorLabel, name, rawArgs, false, false, "用户已取消:" + summary);
                    return CreateToolResultMessage(call.Id, name, resultText);
                }
            }

            Raise(AgentEventKind.ToolCallStarted, display, tool.Name);
            try
            {
                var result = await tool.ExecuteAsync(args).ConfigureAwait(false);
                Raise(AgentEventKind.ToolCallFinished,
                    SummarizeForStatusLine(result.Content, result.Success), display, result.Success);
                AuditLog?.LogToolCall(actorLabel, name, rawArgs,
                    tool.RequiresConfirmation ? true : (bool?)null, result.Success, result.Content);
                string resultText = result.ToLlmText();
                if (cacheKey != null) _turnToolResults[cacheKey] = resultText;
                return CreateToolResultMessage(call.Id, name, resultText);
            }
            catch (OperationCanceledException)
            {
                // 用户取消要终止整轮,不能降级成一条错误工具结果让模型继续跑
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError($"工具 {tool.Name} 执行异常: {ex.Message}");
                Raise(AgentEventKind.ToolCallFinished, "异常:" + ex.Message, display, success: false);
                AuditLog?.LogToolCall(actorLabel, name, rawArgs,
                    tool.RequiresConfirmation ? true : (bool?)null, false, "异常:" + ex.Message);
                string resultText = "错误:" + ex.Message;
                if (cacheKey != null) _turnToolResults[cacheKey] = resultText;
                return CreateToolResultMessage(call.Id, name, resultText);
            }
        }

        private static AgentMessage CreateToolResultMessage(string callId, string toolName, string resultText)
        {
            var msg = AgentMessage.FromTool(callId, resultText);
            // 专员汇报免历史截断(编号与待决事项散布全文,截哪头都丢信息)
            msg.PreserveFullInHistory = toolName.StartsWith("assign_", StringComparison.Ordinal);
            return msg;
        }

        private static string NormalizeJson(JsonElement element)
        {
            using var buffer = new System.IO.MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                WriteNormalizedJson(writer, element);
            }
            return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
        }

        private static void WriteNormalizedJson(Utf8JsonWriter writer, JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    writer.WriteStartObject();
                    foreach (var property in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                    {
                        writer.WritePropertyName(property.Name);
                        WriteNormalizedJson(writer, property.Value);
                    }
                    writer.WriteEndObject();
                    break;
                case JsonValueKind.Array:
                    writer.WriteStartArray();
                    foreach (var item in element.EnumerateArray()) WriteNormalizedJson(writer, item);
                    writer.WriteEndArray();
                    break;
                default:
                    element.WriteTo(writer);
                    break;
            }
        }

        private void CommitHistory(List<AgentMessage> turn)
        {
            lock (_history)
            {
                // 长工具结果只在"历史"里截断:当轮对话模型已看过全文,
                // 后续轮次无需重复携带大段原始输出,显著压缩上下文。
                // 截断做成换副本而不是原地改:turn 里的实例同时被本轮 messages 引用,
                // 原地改会让撞限后的「无工具收尾」拿到残缺的工具结果。
                // 专员汇报(派工结果)整体免截断:preview_id、批次ID、待用户决策都在正文里,
                // 掐头去尾都会丢关键信息;总量仍由 MaxHistoryChars 的整轮修剪兜底。
                for (int i = 0; i < turn.Count; i++)
                {
                    var m = turn[i];
                    if (m.Role == AgentRole.Tool && !m.PreserveFullInHistory && m.Content != null &&
                        m.Content.Length > MaxToolResultCharsInHistory)
                    {
                        turn[i] = new AgentMessage
                        {
                            Role = m.Role,
                            ToolCallId = m.ToolCallId,
                            Content = m.Content.Substring(0, MaxToolResultCharsInHistory) +
                                      "……(工具结果过长,历史中已截断;缺关键编号或数据时请重新派工获取)"
                        };
                    }
                }

                _history.AddRange(turn);

                // 修剪历史:条数与字符双预算,但不能把 tool 消息与其 assistant(tool_calls) 拆散,
                // 从最旧的消息开始整块移除;至少保留本轮消息。
                while (_history.Count > turn.Count &&
                       (_history.Count > MaxHistoryMessages || TotalChars() > MaxHistoryChars))
                {
                    _history.RemoveAt(0);
                    while (_history.Count > turn.Count && _history[0].Role == AgentRole.Tool)
                        _history.RemoveAt(0);
                }
            }
        }

        private int TotalChars()
        {
            int total = 0;
            foreach (var m in _history)
            {
                total += m.Content?.Length ?? 0;
                if (m.ToolCalls != null)
                    foreach (var c in m.ToolCalls)
                        total += c.Function?.Arguments?.Length ?? 0;
            }
            return total;
        }

        private string BuildSystemPrompt(string currentUserText)
        {
            string snapshot;
            try { snapshot = _labSnapshotProvider() ?? ""; }
            catch (Exception ex) { snapshot = "(快照获取失败:" + ex.Message + ")"; }

            // 长期记忆召回:按当前用户输入检索相关记忆片段注入(失败静默,不影响对话)
            string memory;
            try { memory = _memoryProvider?.Invoke(currentUserText ?? "") ?? ""; }
            catch { memory = ""; }

            return
$@"你是「小桐」,MaxChemic 智慧化学实验室平台的总控助手,由化学自动化团队打造。你带领一支五人专员团队:理解用户意图、拆解任务、派工给合适的专员、汇总专员汇报后答复用户。

## 表达规范
1. 回答一律用简体中文,专业、简洁。对话窗口支持 Markdown,但格式必须因内容而异,不要千篇一律套表格:
   - 一两句话能说清的结论,用自然段直接说;
   - 三项以内的少量数据,行内列举即可(如:温度 85.0℃,流量 6.0 mL/min);
   - 仅当呈现多行结构化数据(参数清单、实验矩阵、多设备对比)时才用表格;
   - 操作步骤、排查建议用列表;关键数值和结论加粗;阶段性小结可用三级标题。
2. 严禁使用任何 emoji、表情符号和装饰性符号,保持工业软件的专业语气。语音播报由系统自动提取纯文本摘要,排版不必迁就播报。

## 团队分工(用对应派工工具)
- 设计专员(assign_design_task):实验设计与建库——因子候选分析、DOE 矩阵预览与入库、化学家坐标系设计(停留时间/当量比)、阶梯扫描(动力学专用 τ 台阶)、开放实验接入(导入用户自己的历史数据或外部设计的方案,CSV 文件)、口述搭流程、按智能决策建议生成下一轮预览。
- 执行专员(assign_execution_task):设备与执行——设备清单/状态/连接实测、设参数、执行设备命令、流程启停、DOE 批次执行与进度查询、3D实验室展台状态与实时参数、化学家坐标系联动调参、离线结果回填。
- 分析专员(assign_analysis_task):数据分析与预测——批次/项目统计分析、实验历史、项目查找、智能决策报告解读、What-if 预测、反向寻优、实验室历史记忆检索(以前做过什么)、生成项目 PDF 报告。
- 机理专员(assign_mechanism_task):机理动力学——速率常数与活化能拟合、机理外推预测、取样浓度数据录入、多步反应网络拟合与浓度曲线仿真。
- 工程专员(assign_engineering_task):工程文档与界面操作——新建工程文档(自动保存到默认目录)、新建空白工艺流程、保存当前工程、把设备库设备添加到画布、打开设备连接界面、打开工艺设计器/流程设计器、打开、全屏展示或关闭3D实验室。

## 派工纪律
3. 设备清单、设备状态、流程状态、DOE 进度、3D实验室展台状态与参数等简单只读查询,优先直接使用总控已有的 list_devices、get_device_status、get_flow_status、get_doe_progress、list_lab3d_devices、get_lab3d_device_status 工具,不必派工;其余实质分析、设计与写操作仍派给专员,严禁凭记忆、快照或猜测作答。3D查询只读缓存,严格区分未绑定、未连接、等待数据与数据正常,页面关闭也可查,不得为查询主动打开窗口或连接设备。与实验无关的闲聊可以简短回应,并自然把话题带回实验室工作。

## 实验准备主流程(优先于通用派工)
- 用户说“我要做某实验”并希望系统推荐设备、摆放设备和生成流程时,第一步必须调用 create_experiment_preparation,不要自行拆成多轮流搜索、逐台添加和逐条建流程。experiment_description保留用户完整需求;public_search_query只能包含公开实验主题/反应名,严禁带本地设备参数、私有配方和实验数据。
- 工具返回 missing_information 时只向用户询问缺失项,不得执行;返回待确认方案时,完整展示设备、步骤、安全事项、来源与 preparation_id,等用户明确同意该方案后才可调用 execute_experiment_preparation。用户中途要求修改时,用补充后的完整描述重新生成方案,不得擅自改参数。
- execute_experiment_preparation 会按本地确定性顺序完成设备去重放置、逐台真实连接检查、命令参数校验、流程生成和保存,必需设备失败会自动停止。不要再对同一方案重复调用添加设备、连接或 build_flow 工具,也不要把进度状态当作完成结果。
- 本流程只生成并保存实验准备结果,绝不自动运行。即使用户已确认准备方案,启动真实实验仍是独立操作,必须之后再次明确请求并经 run_current_flow 的确认。查询进度或失败原因用 get_experiment_preparation_status。
4. task 必须自包含:专员看不到对话历史。用户原话里的数值、单位、设备名、项目/批次/预览编号必须原样写进 task;体积、浓度这类物理常数只能照抄用户原话,严禁自己发明、换算或沿用旧值;用户此前已确认过的事项(项目名、是否启用智能推荐等)要把结论一并写进去。
5. 专员汇报里的表格、编号(preview_id、批次ID、项目编号、文件路径)和「待用户决策/补充」事项必须原样转述给用户,不得压缩、改写或代替用户拍板;用户答复后再次派工时,把答复与相关编号一起写进 task。设计预览的矩阵表格、智能推荐的参数表格(参数/当前值/作用三列)都要完整展示,不许删列。
6. 跨领域任务按顺序分步派工(如先设计后执行、先录数据再拟合),一次派一个任务,上一步产出的编号传给下一步;不要把两个领域的活塞进同一个 task。
7. 同一任务同一专员最多派两次,第二次必须带上修正信息(新的编号、用户澄清);仍失败就如实向用户汇报卡点与原因,严禁死循环重派。
8. 会诊:分析专员给出最优条件或重要预测、且该体系有机理模型可用时,可追加派机理专员独立验证,两边结论并列呈现——一致就说一致,不一致要如实指出分歧与可能原因,严禁只挑一边或折中编数。
9. 让用户在有限候选里做选择时,一律用 ask_user_choice 弹选择卡,绝不在文字回复里出选择题。判定标准是句式:凡是你准备写出「是A还是B」「请问选哪一个」「以下哪台/哪组/哪种」,就必须改成选择卡;单选传 multi_select=false,允许多个答案才传 true;选项逐项完整可辨。一次只弹一张卡,次要确认并入 question 说明。自由文本问题(项目命名、数值输入)仍用普通文字提问。用户取消或超时后不要再弹同一张卡。
10. 汇总答复只基于实时工具结果、专员汇报与快照,数字不加不减不编;工具调用或专员汇报失败、数据不足时如实转达并给下一步建议。图表由系统直接插入对话窗,你不要复述图上逐点数据,也不要再画同内容的表格。
11. 进度、状态类问题(执行到第几组、设备在不在线)直接用对应只读工具实测回答,严禁用旧汇报或快照猜测;历史里带 [值守播报] 前缀的消息是系统值守的主动播报,用户追问时可直接引用,拿不准就用工具再查。值守播报里出现的批次ID、轮次、项目名(尤其是智能决策自动创建的下一轮批次ID)是专员看不到的关键上下文,后续派工时必须原样写进 task;用户说「执行下一轮」「继续」时,把播报里的新批次ID带给执行专员,不要让专员按名字重新搜。
12. 写操作(设参数、启停、创建项目、执行批次)由系统在专员执行时弹确认卡,用户取消后不要重派同一操作,改为询问用户下一步。

## 联网与知识库纪律
13. 联网搜索(web_search)只用于公开信息:行业动态、通用知识、外部资料。设备参数、实验数据、工艺条件属本实验室私有信息,严禁写进搜索词;涉及实验、设备、数据的判断一律靠专员本地工具,严禁依赖联网结果;引用联网结果必须标注来源链接。
14. 用户问「资料/手册/SOP/论文里怎么说」时,先用 kb_search 查知识库;查不到就如实说知识库没有该资料,严禁编造「文档里有」;作答标注出处文档名;kb_list_documents 可确认库里有哪些资料。知识库检索与实验室历史检索(search_lab_history)是两回事:前者查导入的文档,后者查做过的实验,不要混用。用户问怎么导入资料时,说「点主界面右上角小桐旁的『知识库』按钮即可导入文档」。

## 长期记忆
15. 对话中用户明确表达了长期有效的偏好、约定、别名、个人习惯时,在本轮收尾前用 save_memory 记下(一轮最多 2 条,只记真正值得长期记住的);严禁保存 API 密钥、密码、试剂配方等敏感信息。系统会在你作答前自动附带与本轮相关的记忆片段,引用记忆时不必向用户复述记忆机制。
16. 记忆只在本机生效;实验数据与项目信息不要写进记忆,那些由数据库与专员工具负责。

{(string.IsNullOrWhiteSpace(memory) ? "" : memory + "\n\n")}## 当前实验室快照
{snapshot}

严格基于以上快照与工具结果作答。";
        }

        /// <summary>
        /// 报告确定性长任务的阶段进度。
        /// 实验准备协调器在本地执行设备放置、连接检查等步骤时直接调用此方法,
        /// 不增加 DeepSeek 往返;界面仍复用现有状态行和 UI 调度,不会新开一套进度通道。
        /// </summary>
        public void ReportProgress(string text)
        {
            if (!string.IsNullOrWhiteSpace(text))
                Raise(AgentEventKind.AssistantThinking, text.Trim());
        }

        private void Raise(AgentEventKind kind, string text, string toolName = null, bool success = true)
        {
            try
            {
                EventRaised?.Invoke(new AgentEvent
                {
                    Kind = kind,
                    Text = text,
                    ToolName = toolName,
                    ToolSuccess = success
                });
            }
            catch { /* 界面订阅方异常不影响主循环 */ }
        }

        private static string Truncate(string s, int len)
            => string.IsNullOrEmpty(s) || s.Length <= len ? s : s.Substring(0, len) + "…";

        /// <summary>
        /// 把工具结果清洗成适合状态行的一行短摘要:
        /// 表格行/分隔线整行丢弃,Markdown 符号剥掉,换行合并为空格——
        /// 完整内容由最终回复渲染呈现,状态行只负责一句话交代。
        /// </summary>
        private static string SummarizeForStatusLine(string content, bool success)
        {
            if (string.IsNullOrWhiteSpace(content))
                return success ? "完成" : "失败";

            var parts = new List<string>();
            foreach (var raw in content.Replace("\r\n", "\n").Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;
                if (line.StartsWith("|")) continue;                       // 表格行
                if (line.StartsWith("---") || line.StartsWith("═")) continue; // 分隔线
                line = line.TrimStart('#', ' ').TrimStart('-', '·', ' ');
                line = line.Replace("**", "").Replace("`", "");
                if (line.Length > 0) parts.Add(line);
            }

            string s = string.Join(" ", parts);
            while (s.Contains("  ")) s = s.Replace("  ", " ");
            s = s.Trim();
            if (s.Length == 0) s = "结果已返回,详见下方回复";
            return Truncate(s, 160);
        }

        public void Dispose()
        {
            _gate.Dispose();
        }
    }
}

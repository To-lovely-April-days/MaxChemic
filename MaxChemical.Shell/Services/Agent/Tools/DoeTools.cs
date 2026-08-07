using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using MaxChemical.Infrastructure.DOE;
using MaxChemical.Logging;
using MaxChemical.Modules.DOE.Data;
using MaxChemical.Modules.DOE.Events;
using MaxChemical.Modules.DOE.Models;
using MaxChemical.Modules.DOE.Services;
using MaxChemical.Modules.DOE.Services.Designs;

namespace MaxChemical.Shell.Services.Agent.Tools
{
    // 注:原 list_doe_projects(仅活跃项目前10个、不可搜索)已被 find_doe_project 取代——
    // 单一入口全库检索,避免模型先用弱工具查不到再改口「扩大范围」的割裂体验。

    // ══════════════════════════════════════════════════════════════
    //  DOE 界面辅助:DOE 主界面是独立窗口(与工具栏"DOE"按钮同一打开方式)。
    //  执行需要看板 VM 在场(迷你监控、智能决策都由它驱动),但用户通过小桐执行时
    //  不希望大窗口在屏幕上闪现——stealth 模式下窗口在屏幕外隐身完成装配,
    //  批次启动切入迷你监控(灵动岛)时才现身。
    // ══════════════════════════════════════════════════════════════
    internal static class DoeUi
    {
        /// <summary>
        /// 确保 DOE 窗口已打开;返回 true 表示本次新开(需要等待其初始化)。
        /// stealth=true 时新开的窗口隐身装配(不闪现、不激活、不进任务栏),
        /// 首次切迷你监控才现身;窗口已存在时行为不变(置前激活)。
        /// </summary>
        public static bool EnsureWindowOpen(AgentToolContext ctx, bool stealth = false)
        {
            bool openedNew = false;
            AgentToolContext.RunOnUi(() =>
            {
                var app = System.Windows.Application.Current;
                var win = app.Windows.OfType<MaxChemical.Modules.DOE.Views.DOEMainView>().FirstOrDefault();
                if (win == null)
                {
                    win = ctx.Resolve<MaxChemical.Modules.DOE.Views.DOEMainView>();
                    win.Owner = app.MainWindow;
                    if (stealth) win.PrepareStealthOpen();
                    win.Show();
                    openedNew = true;
                }
                else
                {
                    if (win.WindowState == System.Windows.WindowState.Minimized)
                        win.WindowState = System.Windows.WindowState.Normal;
                    win.Activate();
                }
            });
            return openedNew;
        }

        /// <summary>批次没能启动时的兜底:隐身代开的窗口若仍未现身,关掉它,不留幽灵窗口。</summary>
        public static void CloseStealthRemnant()
        {
            try
            {
                AgentToolContext.RunOnUi(() =>
                {
                    foreach (var w in System.Windows.Application.Current.Windows
                                 .OfType<MaxChemical.Modules.DOE.Views.DOEMainView>().ToList())
                        w.CloseIfStillStealth();
                });
            }
            catch { }
        }
    }

    // ══════════════════════════════════════════════════════════════
    //  小桐托管批次登记:由 execute_doe_batch 标记,值守据此决定
    //  收官后是否在对话里接管智能决策(用户没用小桐时不参与)。
    // ══════════════════════════════════════════════════════════════
    internal static class AgentManagedBatches
    {
        private static readonly object Gate = new();
        private static readonly HashSet<string> Ids = new();

        public static void Mark(string batchId)
        {
            if (string.IsNullOrEmpty(batchId)) return;
            lock (Gate) Ids.Add(batchId);
        }

        public static bool Contains(string batchId)
        {
            if (string.IsNullOrEmpty(batchId)) return false;
            lock (Gate) return Ids.Contains(batchId);
        }
    }

    // ══════════════════════════════════════════════════════════════
    //  设计预览缓存:preview 生成的矩阵在此暂存,入库时取同一份,
    //  保证"用户看到的 = 入库的"。
    // ══════════════════════════════════════════════════════════════
    internal static class DoePreviewStore
    {
        internal sealed class Entry
        {
            public string BatchName;
            public DOEDesignMethod Method;
            public List<DOEFactor> Factors;
            public List<DOEResponse> Responses;
            public List<string> BindingReport;
            public int BoundCount;
            public DOEDesignMatrix Matrix;
            public DateTime CreatedAt;

            // ── 下一轮预览专用(决策闭环):挂到既有项目,而不是新建项目 ──
            public string ProjectId;
            public int RoundNumber;
            public DOEProjectPhase? NextPhase;

            // ── 化学家坐标系:派生因子(τ/eq)→泵速的转换配置(FactorTransform.ToJson) ──
            public string TransformConfigJson;

            // ── 阶梯扫描:延迟录入(执行时不弹结果录入窗,结果之后回填) ──
            public bool DeferResponses;

            // ── 开放实验接入:逐行响应值,与 Matrix.Rows 对齐 ──
            //   null 或对应元素为 null/空 = 该行待执行;有值 = 按「外部导入、已完成」入库
            public List<Dictionary<string, double>> RowResponses;
        }

        private static readonly object Gate = new();
        private static readonly Dictionary<string, Entry> Entries = new();

        public static string Put(Entry e)
        {
            lock (Gate)
            {
                // 只保留最近 5 份、30 分钟内的预览
                foreach (var k in Entries.Where(kv =>
                        (DateTime.UtcNow - kv.Value.CreatedAt).TotalMinutes > 30)
                    .Select(kv => kv.Key).ToList())
                    Entries.Remove(k);
                while (Entries.Count >= 5)
                    Entries.Remove(Entries.OrderBy(kv => kv.Value.CreatedAt).First().Key);

                string id = Guid.NewGuid().ToString("N").Substring(0, 8);
                e.CreatedAt = DateTime.UtcNow;
                Entries[id] = e;
                return id;
            }
        }

        public static Entry Take(string id)
        {
            lock (Gate)
            {
                if (id != null && Entries.TryGetValue(id, out var e))
                {
                    Entries.Remove(id);
                    return e;
                }
                return null;
            }
        }
    }

    /// <summary>因子/响应/设计选项的共用解析(preview 与 create 共用,保证行为一致)。</summary>
    internal static class DoeArgs
    {
        public static string ParseFactors(JsonElement args, List<FactorCandidate> candidates,
            out List<DOEFactor> factors, out List<string> bindingReport, out int boundCount)
        {
            factors = new List<DOEFactor>();
            bindingReport = new List<string>();
            boundCount = 0;

            if (!args.TryGetProperty("factors", out var factorsEl) || factorsEl.ValueKind != JsonValueKind.Array)
                return "缺少 factors";

            int order = 0;
            foreach (var f in factorsEl.EnumerateArray())
            {
                string fn = AgentToolContext.GetString(f, "name");
                double? low = AgentToolContext.GetNumber(f, "low");
                double? high = AgentToolContext.GetNumber(f, "high");
                if (string.IsNullOrWhiteSpace(fn) || !low.HasValue || !high.HasValue)
                    return "因子缺少 name/low/high";
                if (low.Value >= high.Value)
                    return $"因子「{fn}」低水平必须小于高水平";

                var factor = new DOEFactor
                {
                    FactorName = fn,
                    LowerBound = low.Value,
                    UpperBound = high.Value,
                    FactorType = DOEFactorType.Continuous,
                    SortOrder = order++
                };

                string bindId = AgentToolContext.GetString(f, "bind_parameter_id");
                if (!string.IsNullOrWhiteSpace(bindId))
                {
                    var cand = candidates.FirstOrDefault(c => c.ParameterId == bindId);
                    if (cand == null)
                        return $"因子「{fn}」的 bind_parameter_id({bindId})在当前流程中不存在。" +
                               "请先调用 list_factor_candidates 获取有效的参数ID。" +
                               (candidates.Count > 0
                                   ? "当前有效ID:" + string.Join(";", candidates.Take(10).Select(c => $"{c.DisplayName}={c.ParameterId}"))
                                   : "当前流程没有任何参数候选(可能未打开流程)。");

                    factor.FactorSource = FactorSourceType.ParameterOverride;
                    factor.SourceNodeId = cand.NodeId;
                    factor.SourceParamName = cand.ParameterName;
                    boundCount++;
                    bindingReport.Add($"{fn} → {cand.DisplayName}");
                }
                else
                {
                    bindingReport.Add($"{fn} →(未绑定,需在向导中手动绑定)");
                }

                factors.Add(factor);
            }
            return null;
        }

        /// <summary>
        /// 建库前敲定因子↔流程参数绑定,并如实核算「可执行」因子数:
        /// ①化学家坐标系的派生因子(停留时间/当量比)执行时由 transform 反算泵速注入,视为已落实——
        ///   不再误判成「未绑定」(泵参数在预览反算时就已确定);
        /// ②其余普通因子必须绑到流程参数,否则执行时不会被设定——未绑的先按名字在候选里唯一匹配自动补绑,
        ///   匹配不到或有歧义则记入 unresolved,由调用方拦下要求指定,绝不放过到入库。
        /// 会改写 bindingReport 与因子的绑定来源,返回可执行因子数与未决清单。
        /// </summary>
        public static void ReconcileFactorBindings(
            List<DOEFactor> factors, FactorTransformConfig cfg, List<FactorCandidate> candidates,
            out List<string> bindingReport, out int executableCount, out List<string> unresolved)
        {
            bindingReport = new List<string>();
            unresolved = new List<string>();
            executableCount = 0;

            var derived = new HashSet<string>(StringComparer.Ordinal);
            if (cfg != null)
            {
                if (!string.IsNullOrWhiteSpace(cfg.TauFactor)) derived.Add(cfg.TauFactor);
                if (!string.IsNullOrWhiteSpace(cfg.EqFactor)) derived.Add(cfg.EqFactor);
            }

            foreach (var f in factors)
            {
                if (derived.Contains(f.FactorName))
                {
                    executableCount++;
                    bindingReport.Add($"{f.FactorName} → 化学家坐标系执行时反算泵速注入");
                    continue;
                }
                if (f.FactorSource == FactorSourceType.ParameterOverride && !string.IsNullOrEmpty(f.SourceNodeId))
                {
                    executableCount++;
                    bindingReport.Add($"{f.FactorName} → {f.SourceParamName}");
                    continue;
                }

                var m = MatchCandidate(f.FactorName, candidates);
                if (m != null)
                {
                    f.FactorSource = FactorSourceType.ParameterOverride;
                    f.SourceNodeId = m.NodeId;
                    f.SourceParamName = m.ParameterName;
                    executableCount++;
                    bindingReport.Add($"{f.FactorName} → {m.DisplayName}(按名自动匹配)");
                }
                else
                {
                    unresolved.Add(f.FactorName);
                    bindingReport.Add($"{f.FactorName} →(未绑定)");
                }
            }
        }

        /// <summary>按名字在候选参数里找唯一匹配(去掉设定/目标等修饰后互相包含);歧义或无匹配返回 null,交回用户。</summary>
        private static FactorCandidate MatchCandidate(string factorName, List<FactorCandidate> candidates)
        {
            if (candidates == null || candidates.Count == 0) return null;
            string Norm(string s) => (s ?? "")
                .Replace("设定", "").Replace("设置", "").Replace("目标", "")
                .Replace("反应", "").Replace("进料", "").Replace("值", "").Replace(" ", "").Trim();
            string fn = Norm(factorName);
            if (fn.Length < 2) return null;
            var hits = candidates.Where(c =>
            {
                string cn = Norm(c.DisplayName);
                string pn = Norm(c.ParameterName);
                return (cn.Length >= 2 && (cn.Contains(fn) || fn.Contains(cn)))
                    || (pn.Length >= 2 && (pn.Contains(fn) || fn.Contains(pn)));
            }).ToList();
            return hits.Count == 1 ? hits[0] : null;
        }

        public static string ParseResponses(JsonElement args, out List<DOEResponse> responses)
        {
            responses = new List<DOEResponse>();
            if (!args.TryGetProperty("responses", out var respEl) || respEl.ValueKind != JsonValueKind.Array)
                return "缺少 responses";
            int ro = 0;
            foreach (var r in respEl.EnumerateArray())
            {
                string rn = AgentToolContext.GetString(r, "name");
                if (string.IsNullOrWhiteSpace(rn)) return "响应缺少 name";
                responses.Add(new DOEResponse
                {
                    ResponseName = rn,
                    Unit = AgentToolContext.GetString(r, "unit") ?? "",
                    CollectionMethod = DOECollectionMethod.Manual,
                    SortOrder = ro++
                });
            }
            return null;
        }

        public static string BuildOptions(DOEDesignMethod method, int k, int centerPoints, out DesignOptions options)
        {
            options = null;
            int pbRuns = new[] { 12, 20, 24 }.FirstOrDefault(n => n >= k + 1);
            if (method == DOEDesignMethod.PlackettBurman && pbRuns == 0)
                return $"Plackett-Burman 最多支持 23 个因子(当前 {k} 个)";
            if (method == DOEDesignMethod.BoxBehnken && k < 3)
                return "Box-Behnken 至少需要 3 个因子";

            int quadParams = 1 + 2 * k + k * (k - 1) / 2;
            options = method switch
            {
                DOEDesignMethod.FullFactorial => new FullFactorialOptions
                {
                    LevelCount = 2,
                    CenterPointCount = centerPoints > 0 ? centerPoints : 1
                },
                DOEDesignMethod.CCD => new CCDOptions(),
                DOEDesignMethod.BoxBehnken => new BoxBehnkenOptions
                {
                    BbdCenterCount = centerPoints > 0 ? centerPoints : -1
                },
                DOEDesignMethod.PlackettBurman => new PlackettBurmanOptions { RunCount = pbRuns },
                DOEDesignMethod.DOptimal => new DOptimalOptions
                {
                    RunCount = Math.Min(500, quadParams + 3),
                    ModelType = "quadratic"
                },
                _ => new FullFactorialOptions { LevelCount = 2 }
            };
            return null;
        }

        /// <summary>
        /// 解析化学家坐标系配置(transform 参数):校验因子名对应、泵参数ID存在、常数合法。
        /// 返回错误描述,null = 成功(cfg 为 null 表示未启用)。
        /// </summary>
        public static string ParseTransform(JsonElement args, List<FactorCandidate> candidates,
            List<DOEFactor> factors, out FactorTransformConfig cfg)
        {
            cfg = null;
            if (!args.TryGetProperty("transform", out var t) || t.ValueKind != JsonValueKind.Object)
                return null; // 未启用化学家坐标系

            double vol = AgentToolContext.GetNumber(t, "reactor_volume") ?? 0;
            string tauName = AgentToolContext.GetString(t, "tau_factor");
            string eqName = AgentToolContext.GetString(t, "eq_factor");

            string ferr = ParseFeed(t, "feed_a", candidates, out var feedA);
            if (ferr != null) return ferr;
            string ferr2 = ParseFeed(t, "feed_b", candidates, out var feedB);
            if (ferr2 != null) return ferr2;

            // 多泵:其他固定进料逐股解析(在跑但不参与变化的股,计入 τ 分母)
            List<FeedSpec> otherFeeds = null;
            if (t.TryGetProperty("other_feeds", out var ofArr) && ofArr.ValueKind == JsonValueKind.Array)
            {
                otherFeeds = new List<FeedSpec>();
                foreach (var oe in ofArr.EnumerateArray())
                {
                    string opid = AgentToolContext.GetString(oe, "parameter_id");
                    if (string.IsNullOrWhiteSpace(opid)) return "other_feeds 存在缺少 parameter_id 的条目";
                    var ocand = candidates.FirstOrDefault(x => x.ParameterId == opid);
                    if (ocand == null)
                        return $"other_feeds 的 parameter_id({opid})在当前流程中不存在,请先用 list_factor_candidates 核对。";
                    double? oflow = AgentToolContext.GetNumber(oe, "fixed_flow");
                    if (oflow == null || oflow.Value < 0)
                        return $"固定进料 {ocand.DisplayName} 必须给非负的 fixed_flow(mL/min);已停用的泵不要申报。";
                    otherFeeds.Add(new FeedSpec
                    {
                        ParameterId = opid,
                        DisplayName = ocand.DisplayName ?? opid,
                        FixedFlow = oflow.Value,
                        MaxFlow = AgentToolContext.GetNumber(oe, "max_flow") ?? 0
                    });
                }
            }

            var c = new FactorTransformConfig
            {
                ReactorVolume = vol,
                TauFactor = tauName ?? "",
                EqFactor = string.IsNullOrWhiteSpace(eqName) ? null : eqName,
                FeedA = feedA,
                FeedB = feedB,
                OtherFeeds = otherFeeds
            };

            // 按 τ 动态稳态等待(可选):dwell = turnovers × τ,注入指定的固定时长 Wait 节点
            if (t.TryGetProperty("steady_state", out var ssEl) && ssEl.ValueKind == JsonValueKind.Object)
            {
                var ss = new SteadyStateWaitConfig
                {
                    Enabled = AgentToolContext.GetBool(ssEl, "enabled") ?? true,
                    Turnovers = AgentToolContext.GetNumber(ssEl, "turnovers") ?? 3.0,
                    MinWaitMin = AgentToolContext.GetNumber(ssEl, "min_wait_min") ?? 0,
                    MaxWaitMin = AgentToolContext.GetNumber(ssEl, "max_wait_min") ?? 0,
                    WaitNodeId = AgentToolContext.GetString(ssEl, "wait_node_id") ?? "",
                    WaitNodeName = AgentToolContext.GetString(ssEl, "wait_node_name") ?? ""
                };
                string sserr = FactorTransform.ValidateSteadyState(ss);
                if (sserr != null) return "动态稳态等待配置不合法:" + sserr;
                if (ss.Enabled) c.SteadyState = ss;
            }

            string verr = FactorTransform.Validate(c);
            if (verr != null) return "化学家坐标系配置不合法:" + verr;

            // 因子名必须与 factors 对应,且派生因子不允许再绑定流程参数(泵由 transform 指定)
            foreach (var name in new[] { c.TauFactor, c.EqFactor })
            {
                if (string.IsNullOrEmpty(name)) continue;
                var f = factors.FirstOrDefault(x => string.Equals(x.FactorName, name, StringComparison.Ordinal));
                if (f == null)
                    return $"transform 里的因子「{name}」不在 factors 列表中(名字必须完全一致,注意全半角/空格)。" +
                           $"factors 现有:{string.Join("、", factors.Select(x => x.FactorName))}";
                if (!string.IsNullOrEmpty(f.SourceNodeId))
                    return $"派生因子「{name}」不要传 bind_parameter_id——泵由 transform 的 feed_a/feed_b 指定,因子本身不直接绑定。";
                if (f.LowerBound <= 0 && string.Equals(name, c.TauFactor, StringComparison.Ordinal))
                    return $"停留时间「{name}」的低水平必须大于 0。";
            }

            // 进料泵(含固定股)与普通绑定因子不得指向同一参数(否则该因子的设计值会被反算/固化值静默覆盖)
            var feedPids = new HashSet<string> { c.FeedA.ParameterId };
            if (c.FeedB != null) feedPids.Add(c.FeedB.ParameterId);
            if (c.OtherFeeds != null) foreach (var of in c.OtherFeeds) feedPids.Add(of.ParameterId);
            foreach (var f in factors.Where(x => !string.IsNullOrEmpty(x.SourceNodeId)))
            {
                string boundPid = $"{f.SourceNodeId}.{f.SourceParamName}";
                if (feedPids.Contains(boundPid))
                    return $"因子「{f.FactorName}」绑定的参数({boundPid})与 transform 申报的进料泵是同一个:" +
                           "同一个泵不能既是普通因子又是化学家坐标系的进料股,请去掉其中一处。";
            }

            cfg = c;
            return null;
        }

        /// <summary>
        /// 解析动态稳态等待的被驱动 Wait 节点(preview_doe_design 与 preview_staircase_sweep 共用)。
        /// 权威依据是节点上的「停留时间等待节点」标记(流程常有清洗/稳压/取样多个 Wait,靠清单挑会挑错):
        /// ①未传 wait_node_id → 恰有一个标记节点则自动采用,零个/多个则拒绝并给指引;
        /// ②传了 wait_node_id → 校验存在;若另有不同的标记节点,以标记为准纠正(标记是用户在设计器亲手勾的)。
        /// 返回错误描述(null = 成功);成功时回填 ss.WaitNodeId/WaitNodeName,locateNote 为需转告用户的定位说明(可空)。
        /// </summary>
        public static string ResolveSteadyStateNode(AgentToolContext ctx, SteadyStateWaitConfig ss, out string locateNote)
        {
            locateNote = null;
            if (ss == null || !ss.Enabled) return null;

            List<FlowWaitNodeInfo> waitNodes = null;
            try { waitNodes = ctx.Resolve<IFlowWaitNodeProvider>().GetFixedWaitNodes(); }
            catch { }

            var marked = (waitNodes ?? new List<FlowWaitNodeInfo>())
                .Where(w => w.IsResidenceTimeWait).ToList();

            if (string.IsNullOrEmpty(ss.WaitNodeId))
            {
                // 空列表 = 流程未打开或流程里没有固定时长 Wait 节点,与「有节点但没勾标记」是两码事,分开说
                if (waitNodes == null || waitNodes.Count == 0)
                    return "动态稳态等待:未找到任何固定时长的 Wait 节点(流程未打开,或流程中不含固定时长等待节点)。" +
                           "请先在工艺设计器打开目标流程、确认保温等待节点存在并勾选「停留时间等待节点」标记,再重试;可用 list_flow_wait_nodes 核对。";
                if (marked.Count == 1)
                {
                    ss.WaitNodeId = marked[0].NodeId;
                    ss.WaitNodeName = marked[0].DisplayName;
                    locateNote = $"保温节点按标记自动定位:「{marked[0].DisplayName}」(设计器里勾选了「停留时间等待节点」)。";
                    return null;
                }
                if (marked.Count == 0)
                    return "动态稳态等待:流程里没有勾选「停留时间等待节点」标记的 Wait 节点。" +
                           "最稳妥的做法是请用户在工艺设计器里打开保温等待节点的属性,勾选「停留时间等待节点(DOE 稳态保温)」后重试;" +
                           "或先用 list_flow_wait_nodes 列出候选,让用户选定后把 node_id 传入 wait_node_id。";
                return $"动态稳态等待:流程里有 {marked.Count} 个 Wait 节点都勾选了「停留时间等待节点」标记(应只勾一个):" +
                       string.Join("、", marked.Select(m => $"{m.DisplayName}({m.NodeId})")) +
                       "。请用户在设计器里只保留一个标记,或用选择卡让用户指定其一后把 node_id 传入 wait_node_id。";
            }

            if (waitNodes != null && waitNodes.Count > 0)
            {
                var hit = waitNodes.FirstOrDefault(w => w.NodeId == ss.WaitNodeId);
                if (hit == null)
                    return $"动态稳态等待指定的 Wait 节点(wait_node_id={ss.WaitNodeId})在当前流程里不存在。" +
                           "请用 list_flow_wait_nodes 核对,或让用户在设计器里给保温节点勾选「停留时间等待节点」标记后省略 wait_node_id 重试。";
                if (marked.Count == 1 && marked[0].NodeId != hit.NodeId)
                {
                    // 标记与指定不一致:标记是用户在设计器亲手勾的,以标记为准,防止清单挑错
                    ss.WaitNodeId = marked[0].NodeId;
                    ss.WaitNodeName = marked[0].DisplayName;
                    locateNote = $"注意:指定的节点「{hit.DisplayName}」与设计器里勾选「停留时间等待节点」标记的「{marked[0].DisplayName}」不一致,已按标记纠正为后者;若确应驱动前者,请用户先在设计器调整标记。";
                }
                else if (string.IsNullOrEmpty(ss.WaitNodeName))
                    ss.WaitNodeName = hit.DisplayName;
            }
            // waitNodes == null 且给了显式 id:提供者不可用时不阻断预览,执行时有标记自愈与固定等待兜底
            return null;
        }

        private static string ParseFeed(JsonElement t, string key, List<FactorCandidate> candidates, out FeedSpec feed)
        {
            feed = null;
            if (!t.TryGetProperty(key, out var fe) || fe.ValueKind != JsonValueKind.Object) return null;

            string pid = AgentToolContext.GetString(fe, "parameter_id");
            if (string.IsNullOrWhiteSpace(pid)) return $"{key} 缺少 parameter_id";
            var cand = candidates.FirstOrDefault(x => x.ParameterId == pid);
            if (cand == null)
                return $"{key} 的 parameter_id({pid})在当前流程中不存在,请先用 list_factor_candidates 获取有效ID。";

            feed = new FeedSpec
            {
                ParameterId = pid,
                DisplayName = cand.DisplayName ?? pid,
                Concentration = AgentToolContext.GetNumber(fe, "concentration") ?? 0,
                MinFlow = AgentToolContext.GetNumber(fe, "min_flow") ?? 0,
                MaxFlow = AgentToolContext.GetNumber(fe, "max_flow") ?? 0
            };
            return null;
        }

        /// <summary>
        /// 双空间矩阵表:化学家空间因子列 + 反算出的泵速列(核心体验:用户同时看到两个世界)。
        /// 越界行在末列标注;返回是否存在越界行。
        /// </summary>
        public static string DualSpaceMatrixToMarkdown(DOEDesignMatrix matrix, FactorTransformConfig cfg,
            out List<string> violations, int maxRows = 40)
        {
            violations = new List<string>();
            var sb = new StringBuilder();
            string feedAName = cfg.FeedA.DisplayName;
            // 泵B列只在当量比模式下才有反算值(单进料模式即使配置了 FeedB 也不输出)
            string feedBName = !string.IsNullOrEmpty(cfg.EqFactor) ? cfg.FeedB?.DisplayName : null;

            sb.Append("| 组号 |");
            foreach (var f in matrix.FactorNames) sb.Append($" {f} |");
            sb.Append($" {feedAName}(mL/min) |");
            if (feedBName != null) sb.Append($" {feedBName}(mL/min) |");
            sb.AppendLine(" 校验 |");
            sb.Append("|---|");
            for (int c2 = 0; c2 < matrix.FactorNames.Count + (feedBName != null ? 2 : 1) + 1; c2++) sb.Append("---|");
            sb.AppendLine();

            int shown = Math.Min(matrix.Rows.Count, maxRows);
            for (int i = 0; i < matrix.Rows.Count; i++)
            {
                var values = new Dictionary<string, double>();
                foreach (var f in matrix.FactorNames)
                {
                    if (!matrix.Rows[i].TryGetValue(f, out var v)) continue;
                    if (v is double d) values[f] = d;
                    else if (v is long l) values[f] = l;
                    else if (v is int n) values[f] = n;
                }

                string err = FactorTransform.SolveDeviceValues(cfg, values, out var device);
                if (err != null) violations.Add($"第 {i + 1} 组:{err}");
                if (i >= shown) continue;

                sb.Append($"| {i + 1} |");
                foreach (var f in matrix.FactorNames)
                {
                    string cell = matrix.Rows[i].TryGetValue(f, out var v)
                        ? (v is double d ? Math.Round(d, 2).ToString("0.##") : v?.ToString() ?? "?") : "?";
                    sb.Append($" {cell} |");
                }
                device.TryGetValue(cfg.FeedA.ParameterId, out double fa);
                sb.Append($" {fa:0.####} |");
                if (feedBName != null)
                {
                    device.TryGetValue(cfg.FeedB.ParameterId, out double fb);
                    sb.Append($" {fb:0.####} |");
                }
                sb.AppendLine(err == null ? " 可行 |" : " 超量程 |");
            }
            if (matrix.Rows.Count > maxRows)
                sb.AppendLine($"(共 {matrix.Rows.Count} 组,仅展示前 {maxRows} 组,校验已覆盖全部组)");
            return sb.ToString();
        }

        /// <summary>矩阵转 Markdown 表(逐组展示给用户)。</summary>
        public static string MatrixToMarkdown(DOEDesignMatrix matrix, int maxRows = 40)
        {
            var sb = new StringBuilder();
            sb.Append("| 组号 |");
            foreach (var f in matrix.FactorNames) sb.Append($" {f} |");
            sb.AppendLine();
            sb.Append("|---|");
            foreach (var _ in matrix.FactorNames) sb.Append("---|");
            sb.AppendLine();

            int shown = Math.Min(matrix.Rows.Count, maxRows);
            for (int i = 0; i < shown; i++)
            {
                sb.Append($"| {i + 1} |");
                foreach (var f in matrix.FactorNames)
                {
                    string cell = "?";
                    if (matrix.Rows[i].TryGetValue(f, out var v))
                        cell = v is double d ? Math.Round(d, 2).ToString("0.##") : v?.ToString() ?? "?";
                    sb.Append($" {cell} |");
                }
                sb.AppendLine();
            }
            if (matrix.Rows.Count > maxRows)
                sb.AppendLine($"(共 {matrix.Rows.Count} 组,仅展示前 {maxRows} 组)");
            return sb.ToString();
        }
    }

    /// <summary>预览 DOE 设计:生成矩阵并逐组展示,等用户点头后再入库(只读,不写库)。</summary>
    public sealed class PreviewDoeDesignTool : IAgentTool
    {
        private readonly AgentToolContext _ctx;
        public PreviewDoeDesignTool(AgentToolContext ctx) => _ctx = ctx;

        public string Name => "preview_doe_design";
        public string DisplayName => "预览实验设计";
        public string Description => @"按因子/响应/设计方法生成实验矩阵并返回逐组明细(Markdown 表格),不写数据库。
设计实验的标准流程:list_factor_candidates 取参数ID → 本工具生成预览 → 把矩阵表格完整展示给用户并询问是否入库 → 用户同意后用返回的 preview_id 调 create_doe_batch 入库(入库的就是预览的这一份矩阵)。
设计方法:筛选多因子 PlackettBurman;2~4 因子全面考察 FullFactorial;响应面寻优 CCD/BoxBehnken(≥3 因子);次数受限 DOptimal。
化学家坐标系(派生因子):用户用停留时间(τ,min)/当量比(eq)表达实验时,把它们作为普通因子写进 factors(不传 bind_parameter_id),另传 transform 配置(反应器有效体积、各进料泵的 parameter_id 与浓度、可选泵量程)。系统按 τ=V/(ΣF)、eq=(F2·C2)/(F1·C1) 闭式反算每组泵速,预览表并排展示两个空间,超泵量程在预览期即拒绝。V、C1、C2 等物理常数必须来自用户原话并经用户确认,严禁自行假设。
多泵流程(3台以上):τ 按进入反应器的全部流量算,必须先问清并申报每一股——参与变化的两股填 feed_a/feed_b,其余在跑但流速固定的股(溶剂泵、第三股料)逐个填进 other_feeds(带固定流速,执行时按该值强制注入),已停用的泵不申报(按 0 计)。漏报一股在跑的泵,整轮停留时间全错。";
        public string ParametersSchema => @"{
  ""type"":""object"",
  ""properties"":{
    ""batch_name"":{""type"":""string"",""description"":""项目/批次名称:必须是用户确认过(或用户口述)的名称,不要自拟;系统入库时会自动追加 [AI] 标签,不要自己加""},
    ""design_method"":{""type"":""string"",""enum"":[""FullFactorial"",""CCD"",""BoxBehnken"",""PlackettBurman"",""DOptimal""]},
    ""factors"":{
      ""type"":""array"",""minItems"":1,
      ""items"":{
        ""type"":""object"",
        ""properties"":{
          ""name"":{""type"":""string""},
          ""low"":{""type"":""number""},
          ""high"":{""type"":""number""},
          ""bind_parameter_id"":{""type"":""string"",""description"":""流程参数ID(来自 list_factor_candidates),强烈建议提供以自动绑定;派生因子(τ/eq)不要传""}
        },
        ""required"":[""name"",""low"",""high""]
      }
    },
    ""responses"":{
      ""type"":""array"",""minItems"":1,
      ""items"":{""type"":""object"",""properties"":{""name"":{""type"":""string""},""unit"":{""type"":""string""}},""required"":[""name""]}
    },
    ""center_points"":{""type"":""number""},
    ""transform"":{
      ""type"":""object"",
      ""description"":""化学家坐标系配置(因子含停留时间/当量比时必填);全部常数须用户确认"",
      ""properties"":{
        ""reactor_volume"":{""type"":""number"",""description"":""反应器有效体积(mL,应含混合点到出口死体积)""},
        ""tau_factor"":{""type"":""string"",""description"":""停留时间因子名(与 factors 中完全一致,单位 min)""},
        ""eq_factor"":{""type"":""string"",""description"":""当量比因子名(单进料可省)""},
        ""feed_a"":{""type"":""object"",""description"":""进料A(底物股)"",""properties"":{
          ""parameter_id"":{""type"":""string"",""description"":""泵流速参数ID(来自 list_factor_candidates)""},
          ""concentration"":{""type"":""number"",""description"":""摩尔浓度(与进料B同单位;当量比模式必填)""},
          ""min_flow"":{""type"":""number""},""max_flow"":{""type"":""number""}},
          ""required"":[""parameter_id""]},
        ""feed_b"":{""type"":""object"",""description"":""进料B(试剂股;当量比模式必填)"",""properties"":{
          ""parameter_id"":{""type"":""string""},""concentration"":{""type"":""number""},
          ""min_flow"":{""type"":""number""},""max_flow"":{""type"":""number""}},
          ""required"":[""parameter_id""]},
        ""other_feeds"":{""type"":""array"",""description"":""其他进入同一反应器且在跑的固定流速股(多泵流程必须逐股申报,计入停留时间;停用的泵不申报)"",""items"":{
          ""type"":""object"",""properties"":{
            ""parameter_id"":{""type"":""string""},
            ""fixed_flow"":{""type"":""number"",""description"":""固定流速(mL/min),执行时按该值强制注入""},
            ""max_flow"":{""type"":""number""}},
          ""required"":[""parameter_id"",""fixed_flow""]}},
        ""steady_state"":{""type"":""object"",""description"":""按 τ 动态稳态等待(可选):每组按停留时间自动折算保温时长,注入流程里的保温 Wait 节点,替代一刀切的固定等待。用户要求「按停留时间自动等待/别每组都等那么久」时启用。保温节点按设计器里勾选的「停留时间等待节点」标记自动定位,通常无需传 wait_node_id"",""properties"":{
          ""enabled"":{""type"":""boolean"",""description"":""是否启用,省略默认 true""},
          ""turnovers"":{""type"":""number"",""description"":""保温时长 = 该值 × 停留时间;经验 3(冲洗 3 个停留时间达稳态),默认 3""},
          ""min_wait_min"":{""type"":""number"",""description"":""保温时长下限(min,0=不设)""},
          ""max_wait_min"":{""type"":""number"",""description"":""保温时长上限(min,0=不设)""},
          ""wait_node_id"":{""type"":""string"",""description"":""可省略:流程里恰有一个勾选「停留时间等待节点」标记的 Wait 节点时自动采用;没有标记时才需要从 list_flow_wait_nodes 选定后传入""},
          ""wait_node_name"":{""type"":""string"",""description"":""该 Wait 节点显示名(回显用)""}}}
      },
      ""required"":[""reactor_volume"",""tau_factor"",""feed_a""]
    }
  },
  ""required"":[""batch_name"",""design_method"",""factors"",""responses""]
}";
        public bool RequiresConfirmation => false;
        public string DescribeAction(JsonElement args) => "预览实验设计";

        public async Task<AgentToolResult> ExecuteAsync(JsonElement args)
        {
            string batchName = AgentToolContext.GetString(args, "batch_name") ?? "小桐设计的实验";
            string methodStr = AgentToolContext.GetString(args, "design_method") ?? "FullFactorial";
            if (!Enum.TryParse<DOEDesignMethod>(methodStr, out var method))
                return AgentToolResult.Fail($"不支持的设计方法:{methodStr}");

            // 项目名查重:首轮预览入库时会以此名建项目,重名会让项目列表与分析聚合混乱,
            // 在预览阶段就拦下来让用户改名(查重失败不阻断,入库时还有最终校验)
            try
            {
                if (await _ctx.Resolve<IDOERepository>().ProjectNameExistsAsync(batchName).ConfigureAwait(false))
                    return AgentToolResult.Fail(
                        $"项目名「{batchName}」在数据库中已存在(项目名不允许重复,[AI] 标签差异也算重名)。" +
                        "请告知用户该名称已被占用,请用户换一个项目名后再重新生成预览;不要自作主张改名。");
            }
            catch { }

            List<FactorCandidate> candidates = new();
            try { candidates = _ctx.Resolve<IFlowParameterProvider>().GetFactorCandidates() ?? new(); }
            catch { }

            string err = DoeArgs.ParseFactors(args, candidates, out var factors, out var bindingReport, out int boundCount);
            if (err != null) return AgentToolResult.Fail(err);

            err = DoeArgs.ParseResponses(args, out var responses);
            if (err != null) return AgentToolResult.Fail(err);

            // 化学家坐标系(可选):派生因子 τ/eq → 泵速
            err = DoeArgs.ParseTransform(args, candidates, factors, out var transformCfg);
            if (err != null) return AgentToolResult.Fail(err);

            // 建库前敲定绑定:派生因子(τ/eq)算已落实,普通因子未绑就自动匹配或拦下要求指定。
            // 决不能建出「因子没绑参数」的批次——执行时那些因子不会被设定,事后也没有补绑工具。
            DoeArgs.ReconcileFactorBindings(factors, transformCfg, candidates,
                out bindingReport, out boundCount, out var unresolved);
            if (unresolved.Count > 0)
                return AgentToolResult.Fail(
                    $"以下因子还没确定要绑到哪个流程参数,不能建库:{string.Join("、", unresolved)}。" +
                    "(建库前必须把每个普通因子与流程参数的绑定敲定,否则执行时它们不会被设定,而且入库后没有补绑入口。)" +
                    "请在 factors 里为这些因子补上 bind_parameter_id 后重新 preview_doe_design。" +
                    (candidates.Count > 0
                        ? "当前可用参数:" + string.Join(";", candidates.Take(12).Select(c => $"{c.DisplayName}={c.ParameterId}"))
                        : "当前流程没有可用参数候选(可能未打开流程,请先在设计器打开对应流程)。"));

            // 动态稳态等待:解析被驱动的 Wait 节点(标记优先,详见 DoeArgs.ResolveSteadyStateNode)
            string ssLocateNote = null;
            if (transformCfg?.SteadyState != null && transformCfg.SteadyState.Enabled)
            {
                string ssResolveErr = DoeArgs.ResolveSteadyStateNode(_ctx, transformCfg.SteadyState, out ssLocateNote);
                if (ssResolveErr != null) return AgentToolResult.Fail(ssResolveErr);
            }

            int centerPoints = (int)(AgentToolContext.GetNumber(args, "center_points") ?? -1);
            err = DoeArgs.BuildOptions(method, factors.Count, centerPoints, out var options);
            if (err != null) return AgentToolResult.Fail(err);

            DOEDesignMatrix matrix;
            try
            {
                matrix = await _ctx.Resolve<IDOEDesignService>().GenerateAsync(factors, options).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return AgentToolResult.Fail($"生成设计矩阵失败:{ex.Message}");
            }
            if (matrix == null || matrix.Rows == null || matrix.Rows.Count == 0)
                return AgentToolResult.Fail("设计矩阵为空");

            // 化学家坐标系:先做全矩阵反算与泵量程校验,越界在预览期就拦下,不进入库/执行
            string dualMatrixMd = null;
            if (transformCfg != null)
            {
                dualMatrixMd = DoeArgs.DualSpaceMatrixToMarkdown(matrix, transformCfg, out var violations);
                if (violations.Count > 0)
                {
                    return AgentToolResult.Fail(
                        $"设计不可行:{violations.Count} 组反算出的泵速超出量程或无解,未生成预览。\n" +
                        string.Join("\n", violations.Take(6)) +
                        (violations.Count > 6 ? $"\n(另有 {violations.Count - 6} 组同类问题)" : "") +
                        "\n请向用户说明,并建议收缩因子范围(停留时间下界越小、总流速越大,最容易撞泵上限;当量比越偏离 1,单泵负荷越极端)后重新预览。不要为凑范围修改物理常数。");
                }
            }

            string previewId = DoePreviewStore.Put(new DoePreviewStore.Entry
            {
                BatchName = batchName,
                Method = method,
                Factors = factors,
                Responses = responses,
                BindingReport = bindingReport,
                BoundCount = boundCount,
                Matrix = matrix,
                TransformConfigJson = transformCfg != null ? FactorTransform.ToJson(transformCfg) : null
            });

            var sb = new StringBuilder();
            sb.AppendLine($"设计预览已生成(preview_id:{previewId},尚未入库):");
            sb.AppendLine($"批次「{batchName}」,{methodStr},{factors.Count} 因子 × {responses.Count} 响应,共 {matrix.Rows.Count} 组。");
            sb.AppendLine("绑定:" + string.Join(";", bindingReport));
            if (transformCfg != null)
            {
                sb.AppendLine($"化学家坐标系已启用:{FactorTransform.DescribeConstants(transformCfg)}。全部 {matrix.Rows.Count} 组反算泵速均在量程内。");

                // 动态稳态等待:算出本轮 τ 范围对应的保温时长区间,让用户预览到「每组等多久」
                var ss = transformCfg.SteadyState;
                if (ss != null && ss.Enabled)
                {
                    var taus = matrix.Rows
                        .Select(r => r.TryGetValue(transformCfg.TauFactor, out var tv) &&
                                     double.TryParse(tv?.ToString(), out var td) ? td : (double?)null)
                        .Where(x => x.HasValue && x.Value > 0).Select(x => x.Value).ToList();
                    string range = "";
                    if (taus.Count > 0)
                    {
                        double dLow = FactorTransform.ComputeDwellMinutes(ss, taus.Min()) ?? 0;
                        double dHigh = FactorTransform.ComputeDwellMinutes(ss, taus.Max()) ?? 0;
                        range = $";本轮停留时间 {taus.Min():0.###}–{taus.Max():0.###} min → 每组保温 {dLow:0.###}–{dHigh:0.###} min";
                    }
                    string clamp = (ss.MinWaitMin > 0 ? $",下限 {ss.MinWaitMin:0.###} min" : "") +
                                   (ss.MaxWaitMin > 0 ? $",上限 {ss.MaxWaitMin:0.###} min" : "");
                    sb.AppendLine($"按 τ 动态稳态等待已启用:保温时长 = {ss.Turnovers:0.###} × 停留时间,注入等待节点「{(string.IsNullOrEmpty(ss.WaitNodeName) ? ss.WaitNodeId : ss.WaitNodeName)}」{clamp}{range}。" +
                                  "执行时逐组自动覆盖该节点的等待时长,批次结束还原。");
                    if (ssLocateNote != null) sb.AppendLine(ssLocateNote);
                }
            }
            sb.AppendLine();
            sb.AppendLine("实验矩阵明细:");
            sb.AppendLine(dualMatrixMd ?? DoeArgs.MatrixToMarkdown(matrix));
            if (transformCfg != null)
            {
                sb.AppendLine("请把上面的双空间矩阵完整展示给用户,并逐项复述物理常数请用户确认:" +
                              $"反应器有效体积 {transformCfg.ReactorVolume:0.####} {transformCfg.VolumeUnit}(应含混合点到出口死体积)、" +
                              $"进料A[{transformCfg.FeedA.DisplayName}]浓度 {transformCfg.FeedA.Concentration:0.####}" +
                              (transformCfg.FeedB != null ? $"、进料B[{transformCfg.FeedB.DisplayName}]浓度 {transformCfg.FeedB.Concentration:0.####}" : "") +
                              "。常数错一个,整轮当量/停留时间全错。");
            }
            sb.AppendLine($"请确认两件事:①项目名称拟用「{batchName}」是否认可(入库时系统自动追加 [AI] 标签与溯源描述,名称本身须用户拍板,改名请重新生成预览);②是否入库。两项都确认后调用 create_doe_batch 并传 preview_id={previewId}(入库的就是这份矩阵,不会重新生成)。");
            return AgentToolResult.Ok(sb.ToString());
        }
    }

    /// <summary>执行 DOE 实验批次:逐组注入因子并运行流程,执行看板小窗口显示进度(写操作,需确认)。</summary>
    /// <summary>可执行批次查找(execute_doe_batch 与 get_doe_autopilot_config 共用同一套解析规则)。</summary>
    internal static class DoeBatchFinder
    {
        /// <summary>
        /// 候选范围:Ready/Paused/Aborted/Completed——手动中止(Aborted)的批次剩余组仍可续跑,
        /// Completed 但有失败组的批次可重试;只排除 Designing(未就绪)与 Running(已在跑)。
        /// 省略名字时取最近一个「还有待执行或失败组」的批次(逐个查详情确认)。
        /// </summary>
        public static async Task<(DOEBatch Batch, string Error)> FindRunnableAsync(IDOERepository repo, string name)
        {
            var recent = await repo.GetRecentBatchesAsync(200).ConfigureAwait(false) ?? new List<DOEBatch>();
            var candidates = recent
                .Where(b => b.Status != DOEBatchStatus.Designing && b.Status != DOEBatchStatus.Running)
                .ToList();

            if (!string.IsNullOrWhiteSpace(name))
            {
                string q = name.Trim();

                // ① 批次ID精确寻址(create_doe_batch 返回的ID;刚建的批次不该再按名字全库搜)
                var byId = candidates.FirstOrDefault(b => string.Equals(b.BatchId, q, StringComparison.OrdinalIgnoreCase));
                if (byId == null && LooksLikeId(q))
                {
                    try { byId = await repo.GetBatchWithDetailsAsync(q).ConfigureAwait(false); } catch { }
                    if (byId != null && (byId.Status == DOEBatchStatus.Designing || byId.Status == DOEBatchStatus.Running))
                        return (null, $"批次「{byId.BatchName}」当前状态为 {byId.Status},不可执行。");
                }
                if (byId != null) return (byId, null);

                // ② 精确同名 → ③ 忽略 [AI] 标签的精确同名(用户口述通常不带标签) → ④ 包含匹配
                var exact = candidates.Where(b => string.Equals(b.BatchName, q, StringComparison.OrdinalIgnoreCase)).ToList();
                if (exact.Count == 0)
                {
                    string qn = Normalize(q);
                    exact = candidates.Where(b => string.Equals(Normalize(b.BatchName), qn, StringComparison.OrdinalIgnoreCase)).ToList();
                }
                var fuzzy = exact.Count > 0 ? exact
                    : candidates.Where(b => NameContains(b.BatchName, q)).ToList();
                if (fuzzy.Count == 0)
                    return (null, $"没有名为「{q}」的可执行批次。候选批次:" +
                        (candidates.Count > 0 ? string.Join("、", candidates.Take(8).Select(b => b.BatchName)) : "(无)"));
                if (fuzzy.Count > 1)
                    return (null, "匹配到多个批次,不要用同一个名字原样重试。请改传批次ID精确选择(或让用户选):" +
                        string.Join("、", fuzzy.Take(8).Select(b => $"{b.BatchName}(ID:{b.BatchId})")));
                return (fuzzy[0], null);
            }

            foreach (var b in candidates)
            {
                try
                {
                    var detail = await repo.GetBatchWithDetailsAsync(b.BatchId).ConfigureAwait(false);
                    if (detail == null) continue;
                    bool hasWork = detail.Runs.Any(r =>
                        r.Status == DOERunStatus.Pending ||
                        r.Status == DOERunStatus.Suspended ||
                        r.Status == DOERunStatus.Failed);
                    if (hasWork) return (detail, null);
                }
                catch { }
            }
            return (null, "当前没有可继续执行的批次(既无待执行组也无失败组),请先创建 DOE 批次。");
        }

        /// <summary>批次ID形态判断(GUID 段/长无空格串),避免把普通中文批次名当ID去查库。</summary>
        internal static bool LooksLikeId(string s)
            => !string.IsNullOrEmpty(s) && s.Length >= 8 && !s.Contains(' ') &&
               s.All(c => char.IsLetterOrDigit(c) || c == '-' || c == '_');

        /// <summary>忽略 [AI] 标签与首尾空格的批次名归一。</summary>
        /// <summary>名称匹配归一化:去掉 [AI] 标签与全部半角/全角空白。
        /// 库里项目/批次名与用户口述常差一个空格(如「动力学验证[AI]」vs「动力学验证 [AI]」),
        /// 所有按名字的精确/包含匹配一律先经此归一化。</summary>
        internal static string Normalize(string s)
            => (s ?? "").Replace("[AI]", "").Replace(" ", "").Replace("\u3000", "").Replace("\t", "").Trim();

        /// <summary>归一化后的包含匹配(空白与 [AI] 标签不影响命中)。</summary>
        internal static bool NameContains(string candidate, string query)
        {
            var q = Normalize(query);
            if (q.Length == 0) return false;
            return Normalize(candidate).IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }

    /// <summary>查询批次的智能推荐(智能决策)配置(只读)。执行项目批次前的必经一步。</summary>
    public sealed class GetDoeAutopilotConfigTool : IAgentTool
    {
        private readonly AgentToolContext _ctx;
        public GetDoeAutopilotConfigTool(AgentToolContext ctx) => _ctx = ctx;

        public string Name => "get_doe_autopilot_config";
        public string DisplayName => "查询智能推荐配置";
        public string Description => @"查询 DOE 批次所属项目的「智能推荐(智能决策)」配置:是否启用,以及全部阈值参数的当前值。
智能推荐的作用:批次执行完成后自动做统计分析、筛选显著因子、推荐下一轮设计方案。
执行项目批次(execute_doe_batch)之前必须先调用本工具,把「是否启用智能推荐」和参数值展示给用户确认;非项目批次会直接告知无需询问。batch_name 省略时取最近一个可执行批次。";
        public string ParametersSchema => @"{
  ""type"":""object"",
  ""properties"":{
    ""batch_name"":{""type"":""string"",""description"":""批次名称(可模糊)或批次ID(create_doe_batch 返回,刚建的批次一律传ID);省略则取最近一个可执行批次""}
  }
}";
        public bool RequiresConfirmation => false;
        public string DescribeAction(JsonElement args) => "查询智能推荐配置";

        public async Task<AgentToolResult> ExecuteAsync(JsonElement args)
        {
            var repo = _ctx.Resolve<IDOERepository>();
            var (batch, err) = await DoeBatchFinder.FindRunnableAsync(
                repo, AgentToolContext.GetString(args, "batch_name")).ConfigureAwait(false);
            if (batch == null) return AgentToolResult.Fail(err);

            // 组状态摘要:失败组的处理方式要和智能推荐一起、一次性向用户问清
            var detail = batch.Runs.Count > 0
                ? batch
                : await repo.GetBatchWithDetailsAsync(batch.BatchId).ConfigureAwait(false);
            string stateLine = "";
            string retryAsk = "";
            if (detail != null)
            {
                var active = detail.Runs.Where(r => r.Status != DOERunStatus.Skipped).ToList();
                int pend = active.Count(r => r.Status == DOERunStatus.Pending || r.Status == DOERunStatus.Suspended);
                int fail = active.Count(r => r.Status == DOERunStatus.Failed);
                int comp = active.Count(r => r.Status == DOERunStatus.Completed);
                stateLine = $"批次组状态:待执行 {pend} 组、失败 {fail} 组、已完成 {comp} 组" +
                            (batch.Status == DOEBatchStatus.Aborted ? "(此前被手动中止,可继续执行剩余组)" : "") + "。";
                if (fail > 0)
                    retryAsk = $"注意:该批次有 {fail} 组失败,还需同时与用户确认「重试失败组」还是「跳过失败组只继续剩余组」,执行时把结论放进 retry_failed 参数。";
            }

            if (string.IsNullOrEmpty(batch.ProjectId))
                return AgentToolResult.Ok(
                    $"批次「{batch.BatchName}」不属于优化项目,没有智能推荐可配置,无需询问智能推荐,可直接 execute_doe_batch(传 autopilot=false)。" +
                    (stateLine.Length > 0 ? "\n" + stateLine : "") +
                    (retryAsk.Length > 0 ? "\n" + retryAsk : ""));

            var config = await repo.GetOrCreateAutoPilotConfigAsync(batch.ProjectId).ConfigureAwait(false);
            var sb = new StringBuilder();
            sb.AppendLine($"批次「{batch.BatchName}」属于优化项目,支持智能推荐:每轮执行完成后自动做统计分析、筛选显著因子、推荐下一轮设计方案,省去手动分析。");
            sb.AppendLine($"当前状态:{(config.Enabled ? "已启用" : "未启用")}。");
            if (stateLine.Length > 0) sb.AppendLine(stateLine);
            if (retryAsk.Length > 0) sb.AppendLine(retryAsk);
            sb.AppendLine();
            sb.AppendLine("参数一览(向用户展示时请原样使用下面的表格,不要改成列表):");
            sb.AppendLine("| 参数 | 当前值 | 作用 |");
            sb.AppendLine("| --- | --- | --- |");
            sb.AppendLine($"| 显著性筛选阈值 alpha_screen | {config.AlphaScreen} | 因子显著性检验门槛:p 值大于它的因子判为不显著,下一轮设计中被淘汰;调大保留更多因子,调小淘汰更严格 |");
            sb.AppendLine($"| 模型合格阈值 r2_min | {config.R2Min} | 模型解释力合格线:拟合 R 方低于它说明模型不可靠,系统会推荐补做实验点而不是贸然给优化建议 |");
            sb.AppendLine($"| 模型优秀阈值 r2_good | {config.R2Good} | 模型优秀线:R 方达到它且目标同时达成,才推荐进入最优点验证阶段 |");
            sb.AppendLine($"| 失拟检验阈值 alpha_lof | {config.AlphaLOF} | 失拟检验门槛:p 值小于它说明模型形式不对(有未捕获的高阶效应),会推荐升级模型或补中心点 |");
            sb.AppendLine($"| R 方最小改善 r2_improvement_min | {config.R2ImprovementMin} | 迭代收益下限:连续两轮 R 方提升低于它就暂停迭代,避免继续烧实验却没有收益 |");
            sb.AppendLine($"| 目标达成阈值 d_target | {config.DTarget} | 综合满意度达成线:多响应综合评分 D 达到它视为优化目标达成,推荐收尾验证 |");
            sb.AppendLine($"| 验证重复次数 confirmation_reps | {config.ConfirmationReps} | 验证阶段在预测最优点重复实验的次数,用统计检验确认模型预测可靠 |");
            sb.AppendLine($"| 最大迭代轮次 max_rounds | {config.MaxRounds} | 安全护栏:迭代超过该轮数强制暂停,防止无限循环 |");
            sb.AppendLine($"| 最大总实验组数 max_total_runs | {config.MaxTotalRuns} | 成本护栏:累计实验组数达到上限即暂停 |");
            sb.AppendLine($"| 边界扩展比例 range_expansion_ratio | {config.RangeExpansionRatio} | 最优点压在因子边界上时,把该因子范围向外扩展的比例(如 0.3 即扩 30%) |");
            sb.AppendLine($"| 同操作连续上限 max_consecutive_same_action | {config.MaxConsecutiveSameAction} | 防呆护栏:连续多轮给出同一建议时暂停,提示模型结构可能有问题 |");
            sb.AppendLine();
            sb.Append("请把上表连同「是否启用智能推荐」一起向用户确认(参数不必逐项复述,表格自会说明);" +
                      "用户拍板后调用 execute_doe_batch,传 autopilot=true/false,要改参数时把改动放进 autopilot_params。");
            return AgentToolResult.Ok(sb.ToString());
        }
    }

    public sealed class ExecuteDoeBatchTool : IAgentTool
    {
        private readonly AgentToolContext _ctx;
        private readonly ILogService _logger = LogManager.GetLogger<ExecuteDoeBatchTool>();
        public ExecuteDoeBatchTool(AgentToolContext ctx) => _ctx = ctx;

        public string Name => "execute_doe_batch";
        public string DisplayName => "执行 DOE 实验批次";
        public string Description => @"启动执行一个已创建的 DOE 实验批次:执行引擎按设计矩阵逐组把因子值下发到流程并自动运行,执行看板(小窗口)实时显示当前组号、参数与进度;需要人工录入的响应值每组结束会弹出录入窗。
这是执行 DOE 实验的唯一正确方式——run_current_flow 只把流程跑一遍,不会执行实验矩阵,严禁混用。
项目批次必须先用 get_doe_autopilot_config 查询智能推荐配置并向用户确认,再带 autopilot 参数调用本工具;未带 autopilot 会被拒绝。
手动中止(Aborted)的批次可以继续执行,剩余组接着跑;批次有失败组时,必须先与用户确认 retry_failed(true=失败组重置后一并重跑,false=跳过失败组只跑剩余组),未带会被拒绝。
前提:批次因子已绑定流程参数。batch_name 省略时取最近一个还有待执行/失败组的批次。";
        public string ParametersSchema => @"{
  ""type"":""object"",
  ""properties"":{
    ""batch_name"":{""type"":""string"",""description"":""批次名称(可模糊)或批次ID(create_doe_batch 返回,刚建的批次一律传ID);省略则取最近一个还有待执行/失败组的批次""},
    ""autopilot"":{""type"":""boolean"",""description"":""是否启用智能推荐(智能决策)。项目批次必填,且必须是用户确认过的决定""},
    ""autopilot_params"":{""type"":""object"",""description"":""启用时的参数调整(仅填用户要改的项,其余保持当前值):alpha_screen、r2_min、r2_good、alpha_lof、r2_improvement_min、d_target、confirmation_reps、max_rounds、max_total_runs、range_expansion_ratio、max_consecutive_same_action""},
    ""retry_failed"":{""type"":""boolean"",""description"":""批次有失败组时必填,且必须是用户确认过的决定:true=失败组重置为待执行后一并执行;false=跳过失败组只执行剩余组""}
  }
}";
        public bool RequiresConfirmation => true;

        public string DescribeAction(JsonElement args)
        {
            string n = AgentToolContext.GetString(args, "batch_name");
            bool? ap = AgentToolContext.GetBool(args, "autopilot");
            bool? rf = AgentToolContext.GetBool(args, "retry_failed");
            string apText = ap == null ? "" : ap.Value ? "(智能推荐:启用)" : "(智能推荐:不启用)";
            string rfText = rf == null ? "" : rf.Value ? "(重试失败组)" : "(跳过失败组)";
            return $"启动执行 DOE 批次「{(string.IsNullOrWhiteSpace(n) ? "最近一个可执行批次" : n)}」{apText}{rfText}:逐组下发因子参数并自动运行流程";
        }

        public async Task<AgentToolResult> ExecuteAsync(JsonElement args)
        {
            var exec = _ctx.Resolve<IDOEExecutionService>();
            if (exec.BatchState == DOEBatchStatus.Running)
                return AgentToolResult.Fail($"已有批次正在执行(当前第 {exec.CurrentRunIndex} 组),请先等待完成或让我停止它。");

            var repo = _ctx.Resolve<IDOERepository>();
            var (batch, err) = await DoeBatchFinder.FindRunnableAsync(
                repo, AgentToolContext.GetString(args, "batch_name")).ConfigureAwait(false);
            if (batch == null) return AgentToolResult.Fail(err);

            // ── 组状态判定:续跑(Aborted)/重试(有失败组)都要以真实明细为准 ──
            var detail = batch.Runs.Count > 0
                ? batch
                : await repo.GetBatchWithDetailsAsync(batch.BatchId).ConfigureAwait(false);
            if (detail == null) return AgentToolResult.Fail("批次数据读取失败。");

            var activeRuns = detail.Runs.Where(r => r.Status != DOERunStatus.Skipped).ToList();
            int pendingCount = activeRuns.Count(r => r.Status == DOERunStatus.Pending || r.Status == DOERunStatus.Suspended);
            int failedCount = activeRuns.Count(r => r.Status == DOERunStatus.Failed);
            if (pendingCount == 0 && failedCount == 0)
                return AgentToolResult.Fail(
                    $"批次「{batch.BatchName}」没有待执行或失败的组(已全部完成),无需执行。要复跑请另建批次。");

            // ── 失败组闸门:有失败组必须由用户决定「重试」还是「跳过」 ──
            bool? retryFailed = AgentToolContext.GetBool(args, "retry_failed");
            if (failedCount > 0 && retryFailed == null)
                return AgentToolResult.Fail(
                    $"批次「{batch.BatchName}」当前有 {failedCount} 组失败、{pendingCount} 组待执行。" +
                    "必须先与用户确认处理方式:「重试失败组」(retry_failed=true,失败组重置为待执行后一并执行)" +
                    "还是「跳过失败组只继续剩余组」(retry_failed=false)。用户拍板后再带 retry_failed 调用本工具。");
            if (failedCount > 0 && retryFailed == false && pendingCount == 0)
                return AgentToolResult.Fail(
                    $"批次「{batch.BatchName}」只剩 {failedCount} 组失败组、没有其他待执行组:跳过失败组就无事可做。" +
                    "请与用户确认改为重试失败组(retry_failed=true),或放弃执行。");

            string retryNote = "";
            if (failedCount > 0 && retryFailed == true)
            {
                foreach (var run in detail.Runs.Where(r => r.Status == DOERunStatus.Failed))
                {
                    run.Status = DOERunStatus.Pending;
                    run.ResponseValuesJson = null;
                    run.StartTime = null;
                    run.EndTime = null;
                    await repo.UpdateRunAsync(run).ConfigureAwait(false);
                }
                retryNote = $"已将 {failedCount} 组失败组重置为待执行。";
            }

            // ── 智能推荐闸门:项目批次必须带用户确认过的 autopilot 决定 ──
            bool? autopilot = AgentToolContext.GetBool(args, "autopilot");
            if (!string.IsNullOrEmpty(batch.ProjectId))
            {
                if (autopilot == null)
                    return AgentToolResult.Fail(
                        $"批次「{batch.BatchName}」属于优化项目,执行前必须先与用户确认是否启用智能推荐:" +
                        "请先调用 get_doe_autopilot_config 获取当前配置,向用户展示并征询,再带 autopilot 参数重试。");

                var config = await repo.GetOrCreateAutoPilotConfigAsync(batch.ProjectId).ConfigureAwait(false);
                if (autopilot.Value)
                {
                    string paramErr = ApplyAutopilotParams(config, args);
                    if (paramErr != null) return AgentToolResult.Fail(paramErr);
                    config.Enabled = true;
                }
                else
                {
                    config.Enabled = false;
                }
                await repo.SaveAutoPilotConfigAsync(config).ConfigureAwait(false);
            }

            // 执行前硬性检查:所有参与通讯的设备必须已连接或处于模拟模式,缺一不可;
            // 检查结论(含模拟模式跳过项)要如实回传给用户
            var readiness = _ctx.GetDeviceReadiness();
            if (!readiness.AllReady)
                return AgentToolResult.Fail(
                    $"执行前检查未通过:以下设备未连接且非模拟模式——{readiness.OfflineText}。" +
                    (readiness.Simulated.Count > 0 ? $"(另有 {readiness.Simulated.Count} 台模拟模式已跳过检查:{readiness.SimText})" : "") +
                    "请先用 connect_devices 完成真实连接(或把设备切换到模拟模式),确认全部就绪后再执行本批次。");
            string readinessNote = readiness.Summary();

            // 隐身装配 DOE 窗口(执行看板与智能决策的宿主):用户经小桐执行时不看 DOE 界面,
            // 大窗口不上屏,批次启动后只现身顶部迷你监控岛
            bool openedNew = false;
            try { openedNew = DoeUi.EnsureWindowOpen(_ctx, stealth: true); }
            catch (Exception ex) { _logger.LogWarning($"打开 DOE 窗口失败: {ex.Message}"); }

            // 新开窗口有异步初始化(加载 VM/数据),等它就绪再导航
            await Task.Delay(openedNew ? 4000 : 500).ConfigureAwait(false);

            // 登记为小桐托管批次:收官后的智能决策由小桐在对话里解读确认,
            // DOE 主界面据此跳过原生弹窗;用户手动执行的批次不受影响
            if (!string.IsNullOrEmpty(batch.ProjectId))
            {
                AgentManagedBatches.Mark(batch.BatchId);
                try
                {
                    AgentToolContext.RunOnUi(() =>
                        _ctx.Events.GetEvent<DOEAgentManagedBatchEvent>().Publish(batch.BatchId));
                }
                catch (Exception ex) { _logger.LogWarning($"托管登记事件发布失败: {ex.Message}"); }
            }

            // 发布执行请求:DOE 主界面收到后由执行看板串行完成「加载批次 → 程序化启动」。
            // 必须走看板自己的启动路径(迷你监控小窗才会自动弹出),不能在这里直接调 StartBatchAsync。
            try
            {
                AgentToolContext.RunOnUi(() =>
                    _ctx.Events.GetEvent<DOEExecuteBatchRequestEvent>().Publish(batch.BatchId));
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"执行看板启动请求失败: {ex.Message}");
            }

            // 等待确认真正进入执行状态(看板加载与启动需要时间,不能立即下结论)
            try
            {
                var deadline = DateTime.UtcNow.AddSeconds(20);
                while (DateTime.UtcNow < deadline)
                {
                    // BatchState 是执行引擎全局状态,必须核对批次 ID,防止把上一个批次残留的
                    // Completed/Aborted 误判成本批次的结果
                    bool isThisBatch = string.Equals(exec.CurrentBatchId, batch.BatchId, StringComparison.Ordinal);
                    if (isThisBatch && exec.BatchState == DOEBatchStatus.Running)
                        return AgentToolResult.Ok(
                            (string.IsNullOrEmpty(retryNote) ? "" : retryNote + "\n") +
                            (string.IsNullOrEmpty(readinessNote) ? "" : readinessNote + "\n") +
                            $"批次「{batch.BatchName}」已开始执行" +
                            (autopilot == true ? "(智能推荐已启用,执行完成后会自动分析并给出下一轮建议)" :
                             autopilot == false ? "(智能推荐未启用)" : "") +
                            ":迷你监控小窗已弹出,实时显示当前组与参数;" +
                            "进度可随时用 get_doe_progress 查询。需要人工录入的响应值,每组结束会弹出录入窗。");
                    if (isThisBatch && exec.BatchState == DOEBatchStatus.Completed)
                        return AgentToolResult.Ok($"批次「{batch.BatchName}」已执行完成(组数较少或执行极快)。");
                    if (isThisBatch && exec.BatchState == DOEBatchStatus.Aborted)
                        return AgentToolResult.Fail($"批次「{batch.BatchName}」启动后被中止,请检查因子绑定与设备状态。");
                    await Task.Delay(300).ConfigureAwait(false);
                }

                return AgentToolResult.Ok(
                    $"批次「{batch.BatchName}」启动指令已下发,但 20 秒内未观察到执行状态——" +
                    "可能 DOE 页面未打开或流程/绑定未就绪。请打开 DOE 模块的执行看板确认,不要重复启动。");
            }
            finally
            {
                // 无论哪条出口(含本轮被用户停止/任务取消):隐身代开的窗口只要还没现身
                // 就收掉,不留幽灵窗口;成功启动时迷你岛已现身,这里自然空转
                DoeUi.CloseStealthRemnant();
            }
        }

        /// <summary>
        /// 把 autopilot_params 里用户改动的项写入配置(带范围校验);
        /// 返回 null 表示成功,否则返回错误描述。未提供的项保持当前值。
        /// </summary>
        private static string ApplyAutopilotParams(AutoPilotConfig config, JsonElement args)
        {
            if (!args.TryGetProperty("autopilot_params", out var ps) || ps.ValueKind != JsonValueKind.Object)
                return null;

            foreach (var p in ps.EnumerateObject())
            {
                if (p.Value.ValueKind != JsonValueKind.Number)
                    return $"智能推荐参数 {p.Name} 必须是数值。";
                double v = p.Value.GetDouble();

                switch (p.Name)
                {
                    case "alpha_screen":
                        if (v <= 0 || v > 0.5) return "alpha_screen 应在 (0, 0.5] 之间。";
                        config.AlphaScreen = v; break;
                    case "r2_min":
                        if (v <= 0 || v >= 1) return "r2_min 应在 (0, 1) 之间。";
                        config.R2Min = v; break;
                    case "r2_good":
                        if (v <= 0 || v >= 1) return "r2_good 应在 (0, 1) 之间。";
                        config.R2Good = v; break;
                    case "alpha_lof":
                        if (v <= 0 || v > 0.5) return "alpha_lof 应在 (0, 0.5] 之间。";
                        config.AlphaLOF = v; break;
                    case "r2_improvement_min":
                        if (v < 0 || v > 0.5) return "r2_improvement_min 应在 [0, 0.5] 之间。";
                        config.R2ImprovementMin = v; break;
                    case "d_target":
                        if (v <= 0 || v > 1) return "d_target 应在 (0, 1] 之间。";
                        config.DTarget = v; break;
                    case "confirmation_reps":
                        if (v < 1 || v > 10) return "confirmation_reps 应在 1~10 之间。";
                        config.ConfirmationReps = (int)v; break;
                    case "max_rounds":
                        if (v < 1 || v > 100) return "max_rounds 应在 1~100 之间。";
                        config.MaxRounds = (int)v; break;
                    case "max_total_runs":
                        if (v < 1 || v > 10000) return "max_total_runs 应在 1~10000 之间。";
                        config.MaxTotalRuns = (int)v; break;
                    case "range_expansion_ratio":
                        if (v < 0 || v > 2) return "range_expansion_ratio 应在 [0, 2] 之间。";
                        config.RangeExpansionRatio = v; break;
                    case "max_consecutive_same_action":
                        if (v < 1 || v > 10) return "max_consecutive_same_action 应在 1~10 之间。";
                        config.MaxConsecutiveSameAction = (int)v; break;
                    default:
                        return $"未知的智能推荐参数 {p.Name}。可用:alpha_screen、r2_min、r2_good、alpha_lof、r2_improvement_min、d_target、confirmation_reps、max_rounds、max_total_runs、range_expansion_ratio、max_consecutive_same_action。";
                }
            }

            if (config.R2Good <= config.R2Min)
                return "r2_good 必须大于 r2_min。";
            return null;
        }
    }

    /// <summary>查询 DOE 批次执行进度(只读):当前第几组、该组因子值、完成/待执行统计。</summary>
    public sealed class GetDoeProgressTool : IAgentTool
    {
        private readonly AgentToolContext _ctx;
        public GetDoeProgressTool(AgentToolContext ctx) => _ctx = ctx;

        public string Name => "get_doe_progress";
        public string DisplayName => "查询 DOE 执行进度";
        public string Description => @"查询 DOE 批次的实时执行进度:执行状态、当前正在执行第几组、该组的因子设定值、已完成/待执行/失败组数。
用户询问「执行到第几组了」「正在跑哪组参数」「进度怎么样」时必须调用本工具——执行引擎才是进度的唯一权威来源,严禁通过读取设备当前参数值去反推是第几组。";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{}}";
        public bool RequiresConfirmation => false;
        public string DescribeAction(JsonElement args) => "查询 DOE 执行进度";

        public async Task<AgentToolResult> ExecuteAsync(JsonElement args)
        {
            var exec = _ctx.Resolve<IDOEExecutionService>();
            var state = exec.BatchState;
            string batchId = exec.CurrentBatchId;

            if (string.IsNullOrEmpty(batchId))
                return AgentToolResult.Ok("当前没有正在执行的 DOE 批次(本次启动以来尚未执行过)。");

            var repo = _ctx.Resolve<IDOERepository>();
            var batch = await repo.GetBatchWithDetailsAsync(batchId).ConfigureAwait(false);
            if (batch == null)
                return AgentToolResult.Ok($"执行引擎状态:{StateText(state)},但批次记录({batchId})已不存在。");

            var activeRuns = batch.Runs
                .Where(r => r.Status != DOERunStatus.Skipped)
                .OrderBy(r => r.RunIndex)
                .ToList();
            int total = activeRuns.Count;
            int completed = activeRuns.Count(r => r.Status == DOERunStatus.Completed);
            int failed = activeRuns.Count(r => r.Status == DOERunStatus.Failed);
            int pending = activeRuns.Count(r => r.Status == DOERunStatus.Pending || r.Status == DOERunStatus.Suspended);

            var sb = new StringBuilder();
            sb.AppendLine($"批次:{batch.BatchName}");
            sb.AppendLine($"执行状态:{StateText(state)}");

            if (state == DOEBatchStatus.Running || state == DOEBatchStatus.Paused)
            {
                var current = activeRuns.FirstOrDefault(r => r.RunIndex == exec.CurrentRunIndex);
                if (current != null)
                {
                    int position = activeRuns.IndexOf(current) + 1;
                    sb.AppendLine($"当前正在执行:第 {position}/{total} 组(RunIndex={current.RunIndex})");
                    sb.AppendLine($"该组因子设定值:{FormatFactors(current.FactorValuesJson)}");
                }
                else
                {
                    sb.AppendLine($"当前进度:已完成 {completed}/{total} 组(正在组间切换或等待响应录入)");
                }
            }

            int waiting = activeRuns.Count(r => r.Status == DOERunStatus.WaitingResponse);
            sb.AppendLine($"统计:已完成 {completed} 组,待执行 {pending} 组"
                + (waiting > 0 ? $",待录结果 {waiting} 组(流程已完成,等离线结果回填)" : "")
                + (failed > 0 ? $",失败 {failed} 组" : ""));
            if (state == DOEBatchStatus.Completed) sb.AppendLine("该批次已全部执行完成。");
            if (state == DOEBatchStatus.Aborted) sb.AppendLine("该批次执行已被中止。");
            return AgentToolResult.Ok(sb.ToString());
        }

        private static string StateText(DOEBatchStatus s) => s switch
        {
            DOEBatchStatus.Running => "执行中",
            DOEBatchStatus.Paused => "已暂停",
            DOEBatchStatus.Completed => "已完成",
            DOEBatchStatus.Aborted => "已中止",
            DOEBatchStatus.Ready => "就绪(未开始)",
            _ => s.ToString()
        };

        private static string FormatFactors(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
                var parts = new List<string>();
                foreach (var p in doc.RootElement.EnumerateObject())
                {
                    string v = p.Value.ValueKind == JsonValueKind.Number
                        ? p.Value.GetDouble().ToString("0.####")
                        : p.Value.ToString();
                    parts.Add($"{p.Name}={v}");
                }
                return parts.Count > 0 ? string.Join(",", parts) : "(无)";
            }
            catch
            {
                return json;
            }
        }
    }

    /// <summary>把预览过的 DOE 设计入库(写操作,需确认)。带 AI 标签,入库后 DOE 列表即时刷新可查。</summary>
    public sealed class CreateDoeBatchTool : IAgentTool
    {
        private readonly AgentToolContext _ctx;
        public CreateDoeBatchTool(AgentToolContext ctx) => _ctx = ctx;

        public string Name => "create_doe_batch";
        public string DisplayName => "实验设计入库";
        public string Description => @"把 preview_doe_design 生成并经用户确认的设计入库(创建 DOE 批次)。必须先预览、把矩阵展示给用户、征得同意后再调用本工具,传入 preview_id 即可——入库的就是预览那份矩阵。
入库的批次名会带 [AI] 标签以便在 DOE 列表中区分;入库后 DOE 模块列表立即刷新,用户可在历史/项目页查到。";
        public string ParametersSchema => @"{
  ""type"":""object"",
  ""properties"":{
    ""preview_id"":{""type"":""string"",""description"":""preview_doe_design 返回的预览ID""}
  },
  ""required"":[""preview_id""]
}";
        public bool RequiresConfirmation => true;

        public string DescribeAction(JsonElement args)
            => "将预览的实验设计入库为 DOE 批次(带 [AI] 标签,入库后可在 DOE 模块查看与执行)";

        public async Task<AgentToolResult> ExecuteAsync(JsonElement args)
        {
            string previewId = AgentToolContext.GetString(args, "preview_id");
            var entry = DoePreviewStore.Take(previewId);
            if (entry == null)
                return AgentToolResult.Fail("预览不存在或已过期(30 分钟有效)。请用原来生成这个预览的工具重新生成并展示给用户确认" +
                    "(常规设计 preview_doe_design;文件导入 create_import_preview,file_token 30 分钟内仍有效;" +
                    "下一轮 create_next_round_preview;阶梯扫描 preview_staircase_sweep)。");

            var repo = _ctx.Resolve<IDOERepository>();

            string flowId = "", flowName = "";
            try
            {
                var pp = _ctx.Resolve<IFlowParameterProvider>();
                flowId = pp.GetCurrentFlowId() ?? "";
                flowName = pp.GetCurrentFlowName() ?? "";
            }
            catch { }

            // AI 标签:批次名可见标记 + 配置元数据,双重可辨识
            string taggedName = entry.BatchName.Contains("[AI]") ? entry.BatchName : entry.BatchName + " [AI]";

            bool isNextRound = !string.IsNullOrEmpty(entry.ProjectId);

            // 首轮入库前最终查重(预览到入库之间可能有人建了同名项目);下一轮挂既有项目,不建新项目,无需查
            if (!isNextRound)
            {
                try
                {
                    if (await repo.ProjectNameExistsAsync(taggedName).ConfigureAwait(false))
                        return AgentToolResult.Fail(
                            $"项目名「{entry.BatchName}」在数据库中已存在,未入库(项目名不允许重复)。" +
                            "请告知用户该名称已被占用,请用户换一个项目名,然后用新名称重新调用 preview_doe_design 生成预览并确认后再入库。");
                }
                catch { }
            }

            // 历史/概览页按「项目」组织,游离批次(无 ProjectId)在项目树中不可见——
            // 首轮必须连项目一起建;下一轮(决策闭环)则挂到既有项目、轮次顺延。
            var phase = entry.NextPhase ??
                (entry.Method is DOEDesignMethod.CCD or DOEDesignMethod.BoxBehnken or DOEDesignMethod.DOptimal
                    ? DOEProjectPhase.RSM
                    : DOEProjectPhase.Screening);

            var batch = new DOEBatch
            {
                FlowId = flowId,
                FlowName = flowName,
                BatchName = taggedName,
                DesignMethod = entry.Method,
                Status = DOEBatchStatus.Ready,
                RoundNumber = isNextRound ? entry.RoundNumber : 1,
                ProjectPhase = phase,
                DesignConfigJson = JsonSerializer.Serialize(new
                {
                    method = entry.Method.ToString(),
                    createdBy = "小桐AI",
                    createdAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                })
            };

            // 化学家坐标系:转换配置(τ/eq→泵速的方程与常数快照)随批次固化,执行器据此反算
            if (!string.IsNullOrEmpty(entry.TransformConfigJson))
            {
                var tcfg = FactorTransform.FromJson(entry.TransformConfigJson);
                if (tcfg != null)
                    batch.DesignConfigJson = FactorTransform.MergeIntoDesignConfig(batch.DesignConfigJson, tcfg);
            }

            // 阶梯扫描:延迟录入模式随批次固化——执行时不弹结果录入窗,每阶完成直接进下一阶,
            // 结果之后经 record_run_response 回填(离线 HPLC 场景,录入窗会卡住整条阶梯)
            if (entry.DeferResponses)
            {
                try
                {
                    var root = Newtonsoft.Json.Linq.JObject.Parse(batch.DesignConfigJson ?? "{}");
                    root["deferResponses"] = true;
                    batch.DesignConfigJson = root.ToString(Newtonsoft.Json.Formatting.None);
                }
                catch { }
            }

            string createdProjectId = null;
            string createdBatchId = null;
            try
            {
                string projectId;
                if (isNextRound)
                {
                    // 决策闭环:项目已存在,批次顺延轮次(因子池由决策引擎维护,不重建);
                    // 阶段推进放在批次入库成功之后,避免入库失败留下「空推进」的阶段
                    projectId = entry.ProjectId;
                }
                else
                {
                    var project = new DOEProject
                    {
                        ProjectName = taggedName,
                        ObjectiveDescription = "由小桐AI根据用户口述创建:" +
                            string.Join("、", entry.Responses.Select(r => r.ResponseName)) + " 优化",
                        FlowId = string.IsNullOrEmpty(flowId) ? null : flowId,
                        FlowName = string.IsNullOrEmpty(flowName) ? null : flowName,
                        CurrentPhase = phase,
                        Status = DOEProjectStatus.Active
                    };
                    projectId = await repo.CreateProjectAsync(project).ConfigureAwait(false);
                    createdProjectId = projectId;

                    await repo.SaveProjectFactorsAsync(projectId, entry.Factors.Select(f => new DOEProjectFactor
                    {
                        ProjectId = projectId,
                        FactorName = f.FactorName,
                        FactorType = f.FactorType,
                        FactorStatus = ProjectFactorStatus.Active,
                        CurrentLowerBound = f.LowerBound,
                        CurrentUpperBound = f.UpperBound
                    }).ToList()).ConfigureAwait(false);
                }

                batch.ProjectId = projectId;
                string batchId = await repo.CreateBatchAsync(batch).ConfigureAwait(false);
                createdBatchId = batchId;
                await repo.SaveFactorsAsync(batchId, entry.Factors).ConfigureAwait(false);
                await repo.SaveResponsesAsync(batchId, entry.Responses).ConfigureAwait(false);

                var runs = new List<DOERunRecord>();
                int importedDone = 0;
                for (int i = 0; i < entry.Matrix.Rows.Count; i++)
                {
                    var run = new DOERunRecord
                    {
                        BatchId = batchId,
                        RunIndex = i + 1,
                        FactorValuesJson = JsonSerializer.Serialize(entry.Matrix.Rows[i]),
                        Status = DOERunStatus.Pending
                    };
                    // 开放实验接入:带结果的行按「外部导入、已完成」入库(喂记忆/分析/动力学),
                    // 无结果的行保持待执行(Measured),照常可跑——同一批次里两种行可共存(续命场景)
                    var rowResp = (entry.RowResponses != null && i < entry.RowResponses.Count)
                        ? entry.RowResponses[i] : null;
                    if (rowResp != null && rowResp.Count > 0)
                    {
                        run.ResponseValuesJson = JsonSerializer.Serialize(rowResp);
                        run.Status = DOERunStatus.Completed;
                        run.DataSource = DOEDataSource.Imported;
                        importedDone++;
                    }
                    runs.Add(run);
                }
                await repo.SaveRunsAsync(runs).ConfigureAwait(false);

                // 决策闭环:批次已落库,此时才推进项目阶段(失败不回滚批次,如实告知)
                string phaseNote = "";
                if (isNextRound && entry.NextPhase.HasValue)
                {
                    try
                    {
                        await _ctx.Resolve<IProjectDecisionEngine>()
                            .AdvancePhaseAsync(projectId, entry.NextPhase.Value).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        phaseNote = $"注意:批次已入库,但项目阶段推进失败({ex.Message}),可在 DOE 界面手动调整阶段。";
                    }
                }

                // 通知 DOE 界面刷新列表:入库即可见,不是空头支票
                try
                {
                    AgentToolContext.RunOnUi(() =>
                        _ctx.Events.GetEvent<DOEDataChangedEvent>().Publish());
                }
                catch { }

                var sb = new StringBuilder();
                sb.AppendLine(isNextRound
                    ? $"已入库:第 {entry.RoundNumber} 轮批次「{taggedName}」已挂到既有项目下" +
                      (entry.NextPhase.HasValue ? $",项目阶段已推进到 {entry.NextPhase.Value}" : "") +
                      $",{entry.Method},{entry.Factors.Count} 因子 × {entry.Responses.Count} 响应,共 {entry.Matrix.Rows.Count} 组(与预览一致)。"
                    : $"已入库:项目「{taggedName}」(第 1 轮批次同名),{entry.Method},{entry.Factors.Count} 因子 × {entry.Responses.Count} 响应,共 {entry.Matrix.Rows.Count} 组(与预览一致)。");
                sb.AppendLine("绑定:" + string.Join(";", entry.BindingReport));
                if (importedDone > 0)
                    sb.AppendLine($"其中 {importedDone} 组为外部导入的已完成数据(标记为导入来源,直接可用于分析/预测/动力学)" +
                                  (entry.Matrix.Rows.Count > importedDone
                                      ? $",另有 {entry.Matrix.Rows.Count - importedDone} 组待执行(可接着在本平台跑)。"
                                      : "。"));
                if (!string.IsNullOrEmpty(entry.TransformConfigJson))
                {
                    var tc = FactorTransform.FromJson(entry.TransformConfigJson);
                    if (tc != null)
                        sb.AppendLine($"化学家坐标系已随批次固化({FactorTransform.DescribeConstants(tc)}):执行时系统自动把每组的 τ/eq 反算成泵速注入,无需手动换算。");
                }
                sb.AppendLine(entry.BoundCount == entry.Factors.Count
                    ? "全部因子已落实(化学家坐标派生因子执行时反算泵速注入、其余已绑定流程参数),可直接让我执行(execute_doe_batch)。"
                    : $"注意:{entry.Factors.Count - entry.BoundCount} 个因子仍未落实绑定,执行时不会被设定;请重新设计并把因子绑到流程参数后再执行(入库后无补绑入口)。");
                sb.AppendLine($"批次ID:{batchId}。本会话后续对这个批次的一切操作(get_doe_autopilot_config、execute_doe_batch、analyze_doe_batch 等)一律把此ID当 batch_name 传入精确定位——刚建的批次再按名字全库搜,遇到近名批次会歧义。");
                sb.AppendLine("项目带 [AI] 标签,DOE 模块的概览与历史(项目树)中现在就能查到,展开项目即见本批次。");
                if (phaseNote.Length > 0) sb.AppendLine(phaseNote);
                return AgentToolResult.Ok(sb.ToString());
            }
            catch (Exception ex)
            {
                // 各持久化步骤独立提交(无跨步事务),中途失败会留下半成品:
                // 残留项目占用用户确认过的名字(名称查重挡住重试);残留批次一旦跟项目脱钩,
                // 在以项目为根的界面里既看不到也删不掉,还会让按名定位撞出多义。
                // 本次调用新建的对象逐一回滚(先批次后项目),删不掉的如实报出定位信息
                bool batchCleaned = true;
                if (createdBatchId != null)
                {
                    try { await repo.DeleteBatchWithChildrenAsync(createdBatchId).ConfigureAwait(false); }
                    catch { batchCleaned = false; }
                }
                bool projectCleaned = true;
                if (createdProjectId != null)
                {
                    try { await repo.DeleteProjectWithChildrenAsync(createdProjectId).ConfigureAwait(false); }
                    catch { projectCleaned = false; }
                }

                var fail = new StringBuilder($"入库失败:{ex.Message}。");
                if (batchCleaned && projectCleaned)
                {
                    fail.Append(createdProjectId != null
                        ? $"中途建立的项目与批次已回滚,项目名「{entry.BatchName}」可复用。"
                        : "本轮批次已回滚,既有项目未受影响。");
                    fail.Append("本次预览已被消耗,请用原来生成预览的工具重新生成并确认后再入库" +
                                "(文件导入的 file_token 30 分钟内仍有效,直接重调 create_import_preview 即可)。");
                }
                else
                {
                    if (!batchCleaned)
                        fail.Append($"批次「{taggedName}」(ID:{createdBatchId})清理失败,可能残留" +
                            (createdProjectId != null && projectCleaned
                                ? ",且其所属项目已删,它不挂在任何项目下(项目树里看不到),请在数据库中按此ID删除;"
                                : ";"));
                    if (createdProjectId != null && !projectCleaned)
                        fail.Append($"项目「{taggedName}」清理失败,可能残留,请在 DOE 概览或历史页删除它;");
                    fail.Append("清理残留(或换一个项目名)后再重试入库。");
                }
                return AgentToolResult.Fail(fail.ToString());
            }
        }
    }
}

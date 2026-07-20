using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using MaxChemical.Infrastructure.DOE;
using MaxChemical.Modules.DOE.Data;
using MaxChemical.Modules.DOE.Events;
using MaxChemical.Modules.DOE.Models;
using MaxChemical.Modules.DOE.Services;

namespace MaxChemical.Shell.Services.Agent.Tools
{
    /// <summary>
    /// 阶梯扫描(动力学专用):同一温度下 τ 按升序走台阶,一次执行扫出一条 τ-转化率曲线;
    /// 多温度时按温度分层,每层只换一次温。配合按 τ 动态稳态等待(每阶只等 倍数×τ)
    /// 与延迟录入(不弹录入窗,取样编号后事后回填),把传统「每组独立稳态」的等待和
    /// 反复换温的浪费挤掉。产出批次结构与普通 DOE 完全一致,fit_kinetics 直接可用。
    /// </summary>
    public sealed class PreviewStaircaseSweepTool : IAgentTool
    {
        private readonly AgentToolContext _ctx;
        public PreviewStaircaseSweepTool(AgentToolContext ctx) => _ctx = ctx;

        public string Name => "preview_staircase_sweep";
        public string DisplayName => "预览阶梯扫描";
        public string Description => @"生成动力学专用的阶梯扫描设计预览:同一温度下停留时间按升序走台阶(连续运行,不随机化),多温度按层分组、每层只换一次温。适用于用户想「测动力学/扫一条 τ-转化率曲线/一次实验多测几个停留时间」。
必须配合化学家坐标系(transform 必填)与「按 τ 动态稳态等待」(流程里须有勾选「停留时间等待节点」标记的 Wait 节点;steady_state 省略时按默认倍数 3 自动启用)。
批次自动启用延迟录入:执行时每阶完成不弹结果录入窗、直接进下一阶,提示按样品编号取样;结果之后用 record_run_response 回填。物理常数(体积、浓度)必须来自用户原话。
注意:阶梯扫描按温度分组、不随机化,是动力学测定的专用形态,不能替代常规 DOE 的因子筛选(那仍用 preview_doe_design)。预览确认后用 create_doe_batch(preview_id) 入库。";
        public string ParametersSchema => @"{
  ""type"":""object"",
  ""properties"":{
    ""batch_name"":{""type"":""string"",""description"":""项目/批次名称:必须是用户确认过的名称,系统入库时自动加 [AI] 标签""},
    ""tau_steps"":{""type"":""array"",""minItems"":2,""maxItems"":24,""items"":{""type"":""number""},""description"":""停留时间台阶(min),2~24 个,全部大于 0;会自动按升序执行""},
    ""temperatures"":{""type"":""array"",""maxItems"":6,""items"":{""type"":""number""},""description"":""温度分层(可省=不扫温度):每个温度跑完整条 τ 阶梯,层间只换一次温""},
    ""temperature_bind_parameter_id"":{""type"":""string"",""description"":""温度设定参数ID(来自 list_factor_candidates);给了 temperatures 时必填""},
    ""temperature_factor_name"":{""type"":""string"",""description"":""温度因子名,默认「反应温度」(动力学拟合按名称含「温」识别)""},
    ""eq_hold"":{""type"":""number"",""description"":""当量比固定值:transform 定义了 eq_factor 时必填(扫 τ 时当量比全程保持此值)""},
    ""responses"":{""type"":""array"",""minItems"":1,""items"":{""type"":""object"",""properties"":{""name"":{""type"":""string""},""unit"":{""type"":""string""}},""required"":[""name""]},""description"":""响应(动力学拟合建议用转化率)""},
    ""transform"":{""type"":""object"",""description"":""化学家坐标系配置,与 preview_doe_design 的 transform 完全同构(reactor_volume/tau_factor/eq_factor/feed_a/feed_b/other_feeds/steady_state);必填""}
  },
  ""required"":[""batch_name"",""tau_steps"",""responses"",""transform""]
}";
        public bool RequiresConfirmation => false;
        public string DescribeAction(JsonElement args) => "生成阶梯扫描设计预览";

        public async Task<AgentToolResult> ExecuteAsync(JsonElement args)
        {
            string batchName = AgentToolContext.GetString(args, "batch_name");
            if (string.IsNullOrWhiteSpace(batchName))
                return AgentToolResult.Fail("batch_name 必填,且必须是用户确认过的名称。");

            // 项目名查重(与 preview_doe_design 同口径)
            try
            {
                if (await _ctx.Resolve<IDOERepository>().ProjectNameExistsAsync(batchName).ConfigureAwait(false))
                    return AgentToolResult.Fail(
                        $"项目名「{batchName}」在数据库中已存在(项目名不允许重复)。请告知用户换一个名称后重试,不要自作主张改名。");
            }
            catch { }

            // ── τ 台阶(先去重再验数量,重复值不许蒙混过「至少 2 阶」) ──
            var taus = ReadNumberArray(args, "tau_steps");
            if (taus != null) taus = taus.Distinct().OrderBy(t => t).ToList(); // 升序:小 τ 起步,逐级放缓
            if (taus == null || taus.Count < 2)
                return AgentToolResult.Fail("tau_steps 去重后至少要 2 个不同的停留时间台阶(min)。");
            if (taus.Any(t => t <= 0))
                return AgentToolResult.Fail("tau_steps 里的停留时间必须全部大于 0。");
            if (taus.Count > 24)
                return AgentToolResult.Fail($"tau_steps 最多 24 个台阶(当前 {taus.Count})。");

            // ── 温度分层(可选) ──
            var temps = ReadNumberArray(args, "temperatures");
            string tempBindPid = AgentToolContext.GetString(args, "temperature_bind_parameter_id");
            string tempFactorName = AgentToolContext.GetString(args, "temperature_factor_name");
            if (string.IsNullOrWhiteSpace(tempFactorName)) tempFactorName = "反应温度";

            List<FactorCandidate> candidates = new();
            try { candidates = _ctx.Resolve<IFlowParameterProvider>().GetFactorCandidates() ?? new(); }
            catch { }

            FactorCandidate tempCand = null;
            if (temps != null && temps.Count > 0)
            {
                temps = temps.Distinct().ToList();
                if (temps.Count > 6)
                    return AgentToolResult.Fail($"temperatures 最多 6 层(当前 {temps.Count}),层数太多总时长会失控。");
                if (string.IsNullOrWhiteSpace(tempBindPid))
                    return AgentToolResult.Fail("给了 temperatures 就必须给 temperature_bind_parameter_id(温度设定参数,来自 list_factor_candidates)。");
                tempCand = candidates.FirstOrDefault(c => c.ParameterId == tempBindPid);
                if (tempCand == null)
                    return AgentToolResult.Fail($"温度参数({tempBindPid})在当前流程中不存在,请先用 list_factor_candidates 核对。");
            }

            // ── 因子表:τ(派生) + eq(派生、常数) + 温度(绑定) ──
            // 先从 transform 原始参数里读因子名,建好因子表再交给 ParseTransform 统一校验
            string tauName = null, eqName = null;
            if (args.TryGetProperty("transform", out var tEl) && tEl.ValueKind == JsonValueKind.Object)
            {
                tauName = AgentToolContext.GetString(tEl, "tau_factor");
                eqName = AgentToolContext.GetString(tEl, "eq_factor");
            }
            if (string.IsNullOrWhiteSpace(tauName))
                return AgentToolResult.Fail("transform.tau_factor 必填(阶梯扫描必须走化学家坐标系)。");

            double? eqHold = AgentToolContext.GetNumber(args, "eq_hold");
            if (!string.IsNullOrWhiteSpace(eqName) && eqHold == null)
                return AgentToolResult.Fail(
                    $"transform 定义了当量比因子「{eqName}」,必须给 eq_hold(扫 τ 期间当量比保持的固定值,须用户口述)。");
            if (eqHold != null && eqHold.Value < 0)
                return AgentToolResult.Fail("eq_hold 不能为负。");

            // 因子名必须两两不同:矩阵行按名字作键,重名会静默覆盖 τ 值,整批泵速全错
            if (!string.IsNullOrWhiteSpace(eqName) && string.Equals(eqName, tauName, StringComparison.Ordinal))
                return AgentToolResult.Fail($"transform.eq_factor 不能与 tau_factor 同名(「{tauName}」),请让两个因子名互不相同。");
            if (temps != null && temps.Count > 0 &&
                (string.Equals(tempFactorName, tauName, StringComparison.Ordinal) ||
                 (!string.IsNullOrWhiteSpace(eqName) && string.Equals(tempFactorName, eqName, StringComparison.Ordinal))))
                return AgentToolResult.Fail($"temperature_factor_name(「{tempFactorName}」)不能与 tau_factor/eq_factor 同名,请换一个温度因子名。");

            var factors = new List<DOEFactor>
            {
                new DOEFactor
                {
                    FactorName = tauName,
                    FactorType = DOEFactorType.Continuous,
                    LowerBound = taus.First(),
                    UpperBound = taus.Last(),
                    LevelCount = taus.Count,
                    SortOrder = 0
                }
            };
            if (!string.IsNullOrWhiteSpace(eqName))
            {
                factors.Add(new DOEFactor
                {
                    FactorName = eqName,
                    FactorType = DOEFactorType.Continuous,
                    LowerBound = eqHold!.Value,
                    UpperBound = eqHold.Value,
                    LevelCount = 1,
                    SortOrder = 1
                });
            }
            if (tempCand != null)
            {
                factors.Add(new DOEFactor
                {
                    FactorName = tempFactorName,
                    FactorType = DOEFactorType.Continuous,
                    FactorSource = FactorSourceType.ParameterOverride,
                    SourceNodeId = tempCand.NodeId,
                    SourceParamName = tempCand.ParameterName,
                    LowerBound = temps.Min(),
                    UpperBound = temps.Max(),
                    LevelCount = temps.Count,
                    SortOrder = 2
                });
            }

            string err = DoeArgs.ParseResponses(args, out var responses);
            if (err != null) return AgentToolResult.Fail(err);

            err = DoeArgs.ParseTransform(args, candidates, factors, out var transformCfg);
            if (err != null) return AgentToolResult.Fail(err);
            if (transformCfg == null)
                return AgentToolResult.Fail("阶梯扫描必须提供 transform(化学家坐标系配置):反应器体积与进料泵是 τ 反算的根基。");

            // 动态稳态等待:阶梯扫描的效率来源,未配置则按默认自动启用(倍数 3,靠标记定位节点)
            if (transformCfg.SteadyState == null || !transformCfg.SteadyState.Enabled)
                transformCfg.SteadyState = new SteadyStateWaitConfig { Enabled = true, Turnovers = 3.0 };
            string ssResolveErr = DoeArgs.ResolveSteadyStateNode(_ctx, transformCfg.SteadyState, out string ssLocateNote);
            if (ssResolveErr != null) return AgentToolResult.Fail(ssResolveErr);

            // ── 阶梯矩阵:按温度分层,层内 τ 升序,不随机化 ──
            var matrix = new DOEDesignMatrix { FactorNames = factors.Select(f => f.FactorName).ToList() };
            var tierList = (temps != null && temps.Count > 0) ? temps : new List<double> { double.NaN };
            foreach (var tier in tierList)
            {
                foreach (var tau in taus)
                {
                    var row = new Dictionary<string, object> { [tauName] = tau };
                    if (!string.IsNullOrWhiteSpace(eqName)) row[eqName] = eqHold!.Value;
                    if (tempCand != null) row[tempFactorName] = tier;
                    matrix.Rows.Add(row);
                }
            }
            if (matrix.Rows.Count > 60)
                return AgentToolResult.Fail($"阶梯总组数 {matrix.Rows.Count} 超过 60,请减少台阶或温度层。");

            // 全矩阵反算 + 泵量程校验(与常规预览同一道闸)
            string dualMd = DoeArgs.DualSpaceMatrixToMarkdown(matrix, transformCfg, out var violations, maxRows: 60);
            if (violations.Count > 0)
                return AgentToolResult.Fail(
                    $"阶梯不可行:{violations.Count} 个台阶反算出的泵速超出量程或无解,未生成预览。\n" +
                    string.Join("\n", violations.Take(6)) +
                    (violations.Count > 6 ? $"\n(另有 {violations.Count - 6} 个同类问题)" : "") +
                    "\n请向用户说明并调整台阶范围(τ 越小总流速越大,最易撞泵上限)。不要为凑范围修改物理常数。");

            // ── 保温时长预估(动态等待:每阶 倍数×τ) ──
            double tierDwell = taus.Sum(t => FactorTransform.ComputeDwellMinutes(transformCfg.SteadyState, t) ?? 0);
            double totalDwell = tierDwell * tierList.Count;

            // 与常规设计同一套绑定核算:τ/eq 派生因子算已落实、温度已绑,executable 应等于因子总数
            // (否则共用的入库消息会误报「已绑定 1/N,未绑定的需补充」)
            DoeArgs.ReconcileFactorBindings(factors, transformCfg, candidates,
                out var bindingReport, out int boundCount, out _);

            string previewId = DoePreviewStore.Put(new DoePreviewStore.Entry
            {
                BatchName = batchName,
                Method = DOEDesignMethod.Custom,
                Factors = factors,
                Responses = responses,
                BindingReport = bindingReport,
                BoundCount = boundCount,
                Matrix = matrix,
                TransformConfigJson = FactorTransform.ToJson(transformCfg),
                DeferResponses = true
            });

            var ss = transformCfg.SteadyState;
            var sb = new StringBuilder();
            sb.AppendLine($"阶梯扫描预览已生成(preview_id:{previewId},尚未入库):");
            sb.AppendLine($"批次「{batchName}」:{taus.Count} 个 τ 台阶 × {tierList.Count} 个温度层 = 共 {matrix.Rows.Count} 组,层内 τ 升序连续执行,不随机化(动力学专用形态)。");
            sb.AppendLine($"化学家坐标系:{FactorTransform.DescribeConstants(transformCfg)}。全部台阶反算泵速均在量程内。");
            sb.AppendLine($"动态稳态等待:每阶保温 = {ss.Turnovers:0.###} × τ,注入节点「{(string.IsNullOrEmpty(ss.WaitNodeName) ? ss.WaitNodeId : ss.WaitNodeName)}」;" +
                          $"单层保温合计约 {tierDwell:0.#} min,全部约 {totalDwell:0.#} min(不含换温平衡与流程其他步骤)。");
            if (ssLocateNote != null) sb.AppendLine(ssLocateNote);
            if (tempCand != null)
                sb.AppendLine("提示:层间换温后的首个台阶,建议保温节点勾选「等待设备稳定后再开始计时」,先等温度稳再计时,否则首阶数据偏早。");
            sb.AppendLine("延迟录入已启用:执行时每阶完成不弹录入窗,直接进下一阶;请按播报提示逐阶取样并标注编号(S1、S2……),离线分析出结果后告诉我,我用 record_run_response 回填,补齐后即可拟合动力学。");
            sb.AppendLine();
            sb.AppendLine("阶梯明细(双空间):");
            sb.AppendLine(dualMd);
            sb.AppendLine("请把上表完整展示给用户,并逐项复述物理常数请用户确认:" +
                          $"反应器有效体积 {transformCfg.ReactorVolume:0.####} {transformCfg.VolumeUnit}、" +
                          $"进料A[{transformCfg.FeedA.DisplayName}]浓度 {transformCfg.FeedA.Concentration:0.####}" +
                          (transformCfg.FeedB != null ? $"、进料B[{transformCfg.FeedB.DisplayName}]浓度 {transformCfg.FeedB.Concentration:0.####}" : "") +
                          (eqHold != null ? $"、当量比固定 {eqHold.Value:0.###}" : "") + "。");
            sb.Append($"请确认两件事:①项目名称拟用「{batchName}」是否认可;②是否入库。都确认后调用 create_doe_batch 并传 preview_id={previewId}。");
            return AgentToolResult.Ok(sb.ToString());
        }

        private static List<double> ReadNumberArray(JsonElement args, string name)
        {
            if (!args.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array) return null;
            var list = new List<double>();
            foreach (var el in arr.EnumerateArray())
            {
                if (el.ValueKind == JsonValueKind.Number) list.Add(el.GetDouble());
                else if (el.ValueKind == JsonValueKind.String && double.TryParse(el.GetString(), out var d)) list.Add(d);
            }
            return list;
        }
    }

    /// <summary>
    /// 回填实验结果:给停在「待录结果」的组补上响应值(阶梯扫描延迟录入的收口,
    /// 普通批次漏录/补录同样可用)。写响应、置完成、发结果事件,与执行时录入同一口径。
    /// </summary>
    public sealed class RecordRunResponseTool : IAgentTool
    {
        private readonly AgentToolContext _ctx;
        public RecordRunResponseTool(AgentToolContext ctx) => _ctx = ctx;

        public string Name => "record_run_response";
        public string DisplayName => "回填实验结果";
        public string Description => @"把离线分析得到的响应值回填给批次里的实验组:阶梯扫描(延迟录入)执行完后各组停在「待录结果」,用户报出各样品结果时用本工具逐组写入;普通批次补录/漏录同样适用。
entries 里的 run_no 按执行看板显示的「第 N 组」序号;responses 的键必须能对应批次定义的响应名。已完成组要改写必须传 overwrite=true 并向用户说明。回填前把整理好的「组号-结果」表展示给用户核对。";
        public string ParametersSchema => @"{
  ""type"":""object"",
  ""properties"":{
    ""batch_name"":{""type"":""string"",""description"":""批次名或批次ID(会话里有ID一律传ID)""},
    ""entries"":{""type"":""array"",""minItems"":1,""maxItems"":60,""items"":{
      ""type"":""object"",""properties"":{
        ""run_no"":{""type"":""integer"",""description"":""第 N 组(执行看板显示的序号,从 1 起)""},
        ""responses"":{""type"":""object"",""description"":""响应名→数值,如 {\""转化率\"":0.82}""}},
      ""required"":[""run_no"",""responses""]}},
    ""overwrite"":{""type"":""boolean"",""description"":""允许改写已完成组的已有结果,默认 false""}
  },
  ""required"":[""batch_name"",""entries""]
}";
        public bool RequiresConfirmation => true;
        public string DescribeAction(JsonElement args)
        {
            int n = 0;
            if (args.TryGetProperty("entries", out var arr) && arr.ValueKind == JsonValueKind.Array)
                n = arr.GetArrayLength();
            return $"回填批次「{AgentToolContext.GetString(args, "batch_name")}」的 {n} 组实验结果";
        }

        public async Task<AgentToolResult> ExecuteAsync(JsonElement args)
        {
            var repo = _ctx.Resolve<IDOERepository>();
            var (batch, err) = await DoeAnalysisHelper.FindBatchAsync(repo, AgentToolContext.GetString(args, "batch_name")).ConfigureAwait(false);
            if (batch == null) return AgentToolResult.Fail(err);

            var detail = batch.Runs != null && batch.Runs.Count > 0
                ? batch
                : await repo.GetBatchWithDetailsAsync(batch.BatchId).ConfigureAwait(false);
            if (detail == null) return AgentToolResult.Fail("批次数据读取失败。");

            var ordered = detail.Runs.Where(r => r.Status != DOERunStatus.Skipped)
                .OrderBy(r => r.RunIndex).ToList();
            if (ordered.Count == 0) return AgentToolResult.Fail("批次里没有可回填的实验组。");

            var respNames = (detail.Responses ?? new List<DOEResponse>()).Select(r => r.ResponseName).ToList();
            bool overwrite = AgentToolContext.GetBool(args, "overwrite") ?? false;

            if (!args.TryGetProperty("entries", out var entriesEl) || entriesEl.ValueKind != JsonValueKind.Array)
                return AgentToolResult.Fail("entries 必填:每项 {run_no, responses}。");

            var report = new StringBuilder();
            int okCount = 0;
            foreach (var e in entriesEl.EnumerateArray())
            {
                int runNo = (int)(AgentToolContext.GetNumber(e, "run_no") ?? 0);
                if (runNo < 1 || runNo > ordered.Count)
                    return AgentToolResult.Fail($"run_no={runNo} 超出范围(本批次共 {ordered.Count} 组,序号从 1 起)。已回填的 {okCount} 组保持生效。");
                var run = ordered[runNo - 1];

                if (run.Status == DOERunStatus.Pending || run.Status == DOERunStatus.Running || run.Status == DOERunStatus.Suspended)
                    return AgentToolResult.Fail($"第 {runNo} 组还没执行(状态 {run.Status}),不能回填结果。已回填的 {okCount} 组保持生效。");
                if (run.Status == DOERunStatus.Completed && !overwrite &&
                    !string.IsNullOrWhiteSpace(run.ResponseValuesJson) && run.ResponseValuesJson != "{}")
                    return AgentToolResult.Fail($"第 {runNo} 组已有结果({run.ResponseValuesJson})。要改写必须先向用户确认,再传 overwrite=true。已回填的 {okCount} 组保持生效。");
                if (run.Status == DOERunStatus.Failed && !overwrite)
                    return AgentToolResult.Fail(
                        $"第 {runNo} 组此前标记为失败(流程执行失败或未录结果),可能根本没有产出样品——" +
                        "给失败组回填结果极易是组号错位。请先与用户核对该组确实做出了样品且结果属于它," +
                        $"确认后传 overwrite=true 重试。已回填的 {okCount} 组保持生效。");

                if (!e.TryGetProperty("responses", out var respEl) || respEl.ValueKind != JsonValueKind.Object)
                    return AgentToolResult.Fail($"第 {runNo} 组缺少 responses 对象。已回填的 {okCount} 组保持生效。");

                var values = new Dictionary<string, double>();
                foreach (var p in respEl.EnumerateObject())
                {
                    if (p.Value.ValueKind != JsonValueKind.Number)
                        return AgentToolResult.Fail($"第 {runNo} 组响应「{p.Name}」的值必须是数字。已回填的 {okCount} 组保持生效。");

                    // 响应名对齐:精确 → 唯一包含;对不上就报出批次定义的名单
                    string match = respNames.FirstOrDefault(n => string.Equals(n, p.Name, StringComparison.Ordinal));
                    if (match == null)
                    {
                        var hits = respNames.Where(n =>
                            n.Contains(p.Name, StringComparison.OrdinalIgnoreCase) ||
                            p.Name.Contains(n, StringComparison.OrdinalIgnoreCase)).ToList();
                        if (hits.Count == 1) match = hits[0];
                        else
                            return AgentToolResult.Fail(
                                $"第 {runNo} 组响应名「{p.Name}」对不上批次定义的响应({string.Join("、", respNames)})。已回填的 {okCount} 组保持生效。");
                    }
                    values[match] = p.Value.GetDouble();
                }
                if (values.Count == 0)
                    return AgentToolResult.Fail($"第 {runNo} 组 responses 为空。已回填的 {okCount} 组保持生效。");

                run.ResponseValuesJson = JsonSerializer.Serialize(values);
                run.Status = DOERunStatus.Completed;
                run.EndTime ??= DateTime.Now;
                await repo.UpdateRunAsync(run).ConfigureAwait(false);

                // 与执行时录入同一口径:发结果事件,数据侧订阅方(模型/看板)同步感知
                try
                {
                    _ctx.Events.GetEvent<DOERunResultRecordedEvent>().Publish(new DOERunCompletedPayload
                    {
                        BatchId = detail.BatchId,
                        RunIndex = run.RunIndex,
                        FactorValuesJson = run.FactorValuesJson,
                        ResponseValuesJson = run.ResponseValuesJson
                    });
                }
                catch { }

                okCount++;
                report.AppendLine($"- 第 {runNo} 组:{string.Join(", ", values.Select(kv => $"{kv.Key}={kv.Value:0.####}"))}");
            }

            // 通知 DOE 界面刷新概览/历史(与 create_doe_batch 同一模式);
            // 注意:回填不喂 GPR 模型(那是执行时的在线学习通道),动力学与统计分析直接读库,不受影响
            if (okCount > 0)
            {
                try
                {
                    AgentToolContext.RunOnUi(() =>
                        _ctx.Events.GetEvent<DOEDataChangedEvent>().Publish());
                }
                catch { }
            }

            int remaining = ordered.Count(r => r.Status == DOERunStatus.WaitingResponse);

            // 延迟录入批次的轮次决策在收官时被推迟(当时无数据可评):
            // 最后一组结果补齐时在这里补发轮次完成事件,决策引擎拿真实数据做分析
            if (remaining == 0 && okCount > 0 &&
                !string.IsNullOrEmpty(detail.ProjectId) && detail.Status == DOEBatchStatus.Completed)
            {
                try
                {
                    _ctx.Events.GetEvent<DOERoundCompletedEvent>().Publish(new DOERoundCompletedPayload
                    {
                        ProjectId = detail.ProjectId,
                        BatchId = detail.BatchId,
                        RoundNumber = detail.RoundNumber ?? 0,
                        FinalStatus = DOEBatchStatus.Completed
                    });
                }
                catch { }
            }

            var sb = new StringBuilder();
            sb.AppendLine($"批次「{detail.BatchName}」已回填 {okCount} 组结果:");
            sb.Append(report);
            sb.AppendLine(remaining > 0
                ? $"还有 {remaining} 组待录结果,补齐后再做分析/动力学拟合更可靠。"
                : "全部组的结果已齐,可以直接 analyze_doe_batch 做统计分析,或 fit_kinetics 拟合动力学;若这是项目批次,轮次决策分析会随之触发。");
            return AgentToolResult.Ok(sb.ToString());
        }
    }
}

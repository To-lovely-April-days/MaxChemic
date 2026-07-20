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
using MaxChemical.Modules.DOE.Services.Designs;

namespace MaxChemical.Shell.Services.Agent.Tools
{
    /// <summary>
    /// 决策闭环共用:推荐动作的中文文案、阶段映射、决策报告排版。
    /// 决策永远由规则引擎(ProjectDecisionEngine)产出,这里只负责解读呈现。
    /// </summary>
    internal static class DecisionHelper
    {
        public static string RecText(NextStepRecommendation rec) => rec switch
        {
            NextStepRecommendation.ContinueScreening => "继续筛选(因子仍偏多,再做一轮筛选设计)",
            NextStepRecommendation.SteepestAscent => "最速上升/下降(沿梯度方向逼近最优区域)",
            NextStepRecommendation.StartRSM => "进入响应面优化(RSM 精细建模寻优)",
            NextStepRecommendation.AugmentDesign => "增强补点(模型质量不足,补做实验点)",
            NextStepRecommendation.ExpandRange => "扩展因子范围(最优点压在边界上)",
            NextStepRecommendation.ConfirmationRuns => "验证实验(在预测最优点重复实验确认)",
            NextStepRecommendation.Complete => "项目完成(优化目标已达成)",
            NextStepRecommendation.UserDecision => "需要用户决策(规则无法自动判断)",
            _ => rec.ToString()
        };

        public static DOEProjectPhase PhaseFor(NextStepRecommendation rec) => rec switch
        {
            NextStepRecommendation.ContinueScreening => DOEProjectPhase.Screening,
            NextStepRecommendation.SteepestAscent => DOEProjectPhase.PathSearch,
            NextStepRecommendation.StartRSM => DOEProjectPhase.RSM,
            NextStepRecommendation.AugmentDesign => DOEProjectPhase.Augmenting,
            NextStepRecommendation.ExpandRange => DOEProjectPhase.RSM,
            NextStepRecommendation.ConfirmationRuns => DOEProjectPhase.Confirmation,
            NextStepRecommendation.Complete => DOEProjectPhase.Completed,
            _ => DOEProjectPhase.RSM
        };

        /// <summary>把规则引擎的轮次决策报告排版成对话可读文本(数值全部来自引擎,不做二次计算)。</summary>
        public static string FormatSummary(DOERoundSummary s, string projectName)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"项目「{projectName}」第 {s.RoundNumber} 轮({s.DesignMethod},{s.RunCount} 组)决策报告:");

            var model = new List<string>();
            if (s.RSquared.HasValue) model.Add($"R 方 {s.RSquared.Value:0.###}");
            if (s.RSquaredAdj.HasValue) model.Add($"调整 R 方 {s.RSquaredAdj.Value:0.###}");
            if (s.LackOfFitP.HasValue) model.Add($"失拟 P {s.LackOfFitP.Value:0.####}");
            if (s.GprRSquared.HasValue) model.Add($"GPR R 方 {s.GprRSquared.Value:0.###}");
            if (model.Count > 0) sb.AppendLine("- 模型:" + string.Join(",", model));

            if (s.BestResponseValue.HasValue)
            {
                string factors = "";
                try
                {
                    var f = DoeAnalysisHelper.ParseNumbers(s.BestFactorsJson);
                    if (f.Count > 0)
                        factors = "(" + string.Join(",", f.Take(6).Select(kv => $"{kv.Key}={kv.Value:0.####}")) + ")";
                }
                catch { }
                sb.AppendLine($"- 本轮最优:{s.BestResponseValue.Value:0.####}{factors}");
            }

            // 边界状态:最优点压边界是「扩范围」建议的依据
            try
            {
                if (!string.IsNullOrWhiteSpace(s.OptimalBoundaryStatusJson))
                {
                    using var doc = JsonDocument.Parse(s.OptimalBoundaryStatusJson);
                    var onEdge = new List<string>();
                    foreach (var p in doc.RootElement.EnumerateObject())
                    {
                        string v = p.Value.GetString() ?? "";
                        if (v == "at_upper") onEdge.Add(p.Name + "(上边界)");
                        else if (v == "at_lower") onEdge.Add(p.Name + "(下边界)");
                    }
                    if (onEdge.Count > 0)
                        sb.AppendLine("- 压边界因子:" + string.Join("、", onEdge));
                }
            }
            catch { }

            if (!string.IsNullOrWhiteSpace(s.ScreenedOutFactors))
                sb.AppendLine($"- 本轮淘汰因子:{s.ScreenedOutFactors}");
            if (s.MaxEI.HasValue)
                sb.AppendLine($"- 剩余改进空间(最大 EI):{s.MaxEI.Value:0.####}");

            sb.AppendLine($"- 引擎推荐:{RecText(s.Recommendation)}");
            if (!string.IsNullOrWhiteSpace(s.RecommendationReason))
                sb.AppendLine($"- 推荐理由:{s.RecommendationReason}");
            return sb.ToString().TrimEnd();
        }
    }

    /// <summary>读取智能决策报告(只读):规则引擎对某一轮的分析结论与下一步推荐。</summary>
    public sealed class GetDecisionReportTool : IAgentTool
    {
        private readonly AgentToolContext _ctx;
        private readonly ILogService _logger = LogManager.GetLogger<GetDecisionReportTool>();
        public GetDecisionReportTool(AgentToolContext ctx) => _ctx = ctx;

        public string Name => "get_decision_report";
        public string DisplayName => "读取智能决策报告";
        public string Description => @"读取项目最近一个已完成轮次的智能决策报告(由规则引擎产出):模型质量、本轮最优、压边界因子、淘汰因子、推荐动作与理由。
用户问「这轮的决策建议是什么」「下一步该怎么做」时用本工具;解读时决策以引擎为准,你只做解释和倾向性建议,不得替引擎改判。
用户同意按建议走后,用 create_next_round_preview 生成下一轮预览。";
        public string ParametersSchema => @"{
  ""type"":""object"",
  ""properties"":{
    ""project_name"":{""type"":""string"",""description"":""项目名(可模糊);只有一个项目时可省略""}
  }
}";
        public bool RequiresConfirmation => false;
        public string DescribeAction(JsonElement args) => "读取智能决策报告";

        public async Task<AgentToolResult> ExecuteAsync(JsonElement args)
        {
            var repo = _ctx.Resolve<IDOERepository>();
            var (proj, err) = await DoeAnalysisHelper.FindProjectAsync(
                repo, AgentToolContext.GetString(args, "project_name")).ConfigureAwait(false);
            if (proj == null) return AgentToolResult.Fail(err);

            var (summary, sErr) = await DecisionReportLoader.LoadLatestAsync(_ctx, repo, proj.ProjectId).ConfigureAwait(false);
            if (summary == null) return AgentToolResult.Fail(sErr);

            var sb = new StringBuilder();
            sb.AppendLine(DecisionHelper.FormatSummary(summary, proj.ProjectName));
            sb.AppendLine();
            sb.Append("请向用户解读以上报告(通俗讲清推荐理由),并确认下一步:按推荐走则调用 create_next_round_preview;" +
                      "用户想换动作,把动作放进 create_next_round_preview 的 action 参数。决策以引擎为准,你不得自行改判。");
            return AgentToolResult.Ok(sb.ToString());
        }
    }

    /// <summary>决策报告定位:取项目最近一轮的报告,没有则现场让引擎分析。</summary>
    internal static class DecisionReportLoader
    {
        public static async Task<(DOERoundSummary Summary, string Error)> LoadLatestAsync(
            AgentToolContext ctx, IDOERepository repo, string projectId)
        {
            var batches = await repo.GetBatchesByProjectAsync(projectId).ConfigureAwait(false)
                          ?? new List<DOEBatch>();
            var completed = batches
                .Where(b => b.Status == DOEBatchStatus.Completed)
                .OrderByDescending(b => b.RoundNumber ?? 0)
                .ThenByDescending(b => b.CreatedTime)
                .ToList();
            if (completed.Count == 0)
                return (null, "该项目还没有已完成的轮次,没有决策报告可读。");

            var latest = completed[0];
            try
            {
                var saved = await repo.GetRoundSummaryByBatchAsync(latest.BatchId).ConfigureAwait(false);
                if (saved != null) return (saved, null);
            }
            catch { }

            try
            {
                var engine = ctx.Resolve<IProjectDecisionEngine>();
                var summary = await engine.AnalyzeRoundAsync(projectId, latest.BatchId).ConfigureAwait(false);
                return summary != null ? (summary, null) : (null, "决策引擎未返回分析结果。");
            }
            catch (Exception ex)
            {
                return (null, $"决策分析失败:{ex.Message}");
            }
        }
    }

    /// <summary>按决策建议生成下一轮实验预览(只读,不写库):因子/方法全部来自规则引擎。</summary>
    public sealed class CreateNextRoundPreviewTool : IAgentTool
    {
        private readonly AgentToolContext _ctx;
        private readonly ILogService _logger = LogManager.GetLogger<CreateNextRoundPreviewTool>();
        public CreateNextRoundPreviewTool(AgentToolContext ctx) => _ctx = ctx;

        public string Name => "create_next_round_preview";
        public string DisplayName => "生成下一轮实验预览";
        public string Description => @"按智能决策建议为项目生成下一轮实验矩阵预览(不写库):因子清单与范围取自决策引擎维护的项目因子池(含绑定与已淘汰因子处理),设计方法由引擎按推荐动作给出,你不得自拟因子或改范围。
前提:用户已看过决策报告(get_decision_report 或值守播报)并同意生成。action 省略时按引擎推荐执行;用户想换动作时传 action。
返回矩阵预览与 preview_id:把矩阵展示给用户并确认入库后,调 create_doe_batch(preview_id)——批次会挂到既有项目、轮次顺延、项目阶段随之推进;入库后可 execute_doe_batch 继续执行。";
        public string ParametersSchema => @"{
  ""type"":""object"",
  ""properties"":{
    ""project_name"":{""type"":""string"",""description"":""项目名(可模糊);只有一个项目时可省略""},
    ""action"":{""type"":""string"",""enum"":[""FollowRecommendation"",""ContinueScreening"",""StartRSM"",""AugmentDesign"",""ExpandRange"",""ConfirmationRuns""],""description"":""省略或 FollowRecommendation=按引擎推荐;其余为用户明确选择的备选动作""},
    ""design_method"":{""type"":""string"",""enum"":[""FullFactorial"",""CCD"",""BoxBehnken"",""PlackettBurman"",""DOptimal""],""description"":""设计方法覆盖(通常不填,由引擎推荐)""},
    ""center_points"":{""type"":""number""}
  }
}";
        public bool RequiresConfirmation => false;
        public string DescribeAction(JsonElement args) => "生成下一轮实验预览";

        public async Task<AgentToolResult> ExecuteAsync(JsonElement args)
        {
            var repo = _ctx.Resolve<IDOERepository>();
            var (proj, err) = await DoeAnalysisHelper.FindProjectAsync(
                repo, AgentToolContext.GetString(args, "project_name")).ConfigureAwait(false);
            if (proj == null) return AgentToolResult.Fail(err);

            var (summary, sErr) = await DecisionReportLoader.LoadLatestAsync(_ctx, repo, proj.ProjectId).ConfigureAwait(false);
            if (summary == null) return AgentToolResult.Fail(sErr);

            // 动作:默认跟随引擎推荐,用户可明确改选
            string actionStr = AgentToolContext.GetString(args, "action");
            NextStepRecommendation rec = summary.Recommendation;
            if (!string.IsNullOrWhiteSpace(actionStr) &&
                !actionStr.Equals("FollowRecommendation", StringComparison.OrdinalIgnoreCase))
            {
                if (!Enum.TryParse<NextStepRecommendation>(actionStr, out rec))
                    return AgentToolResult.Fail($"未知动作 {actionStr}。");
            }

            if (rec == NextStepRecommendation.Complete)
                return AgentToolResult.Ok(
                    $"引擎判定项目「{proj.ProjectName}」已达优化目标,无需再生成下一轮。" +
                    "建议与用户确认收官:在 DOE 界面将项目标记完成;若用户仍想加做验证,传 action=ConfirmationRuns 重新调用。");
            if (rec == NextStepRecommendation.UserDecision)
                return AgentToolResult.Ok(
                    "引擎无法自动判断下一步(已触发防护规则,理由见决策报告),必须由用户选择动作:" +
                    "ContinueScreening(继续筛选)、StartRSM(响应面)、AugmentDesign(补点)、ExpandRange(扩范围)、ConfirmationRuns(验证)。" +
                    "请把选项讲给用户,拿到选择后带 action 重新调用。");
            if (rec == NextStepRecommendation.SteepestAscent)
                return AgentToolResult.Ok(
                    "最速上升路径需要梯度方向计算,目前请在 DOE 设计向导中完成(向导会按项目预填);" +
                    "或与用户确认改用其他动作(如 StartRSM/AugmentDesign)后带 action 重新调用。");

            var project = await repo.GetProjectAsync(proj.ProjectId).ConfigureAwait(false);
            var engine = _ctx.Resolve<IProjectDecisionEngine>();

            // 因子池:引擎维护(活跃因子+当前范围+流程绑定),这是决策闭环的权威来源
            var factors = await engine.GetNextRoundFactorsAsync(proj.ProjectId).ConfigureAwait(false)
                          ?? new List<DOEFactor>();
            if (factors.Count == 0)
                return AgentToolResult.Fail("项目因子池中没有活跃因子(可能全部被淘汰或固定),无法生成下一轮。请在 DOE 界面检查项目因子。");
            for (int i = 0; i < factors.Count; i++) factors[i].SortOrder = i;

            // 响应:从最近一轮继承
            var batches = await repo.GetBatchesByProjectAsync(proj.ProjectId).ConfigureAwait(false) ?? new List<DOEBatch>();
            var latestBatch = batches.OrderByDescending(b => b.RoundNumber ?? 0).ThenByDescending(b => b.CreatedTime).FirstOrDefault();
            var latestDetail = latestBatch != null
                ? await repo.GetBatchWithDetailsAsync(latestBatch.BatchId).ConfigureAwait(false)
                : null;
            var responses = (latestDetail?.Responses ?? new List<DOEResponse>())
                .Select((r, i) => new DOEResponse
                {
                    ResponseName = r.ResponseName,
                    Unit = r.Unit,
                    CollectionMethod = r.CollectionMethod,
                    SortOrder = i
                }).ToList();
            if (responses.Count == 0)
                return AgentToolResult.Fail("未能从上一轮继承响应变量,无法生成下一轮。");

            int nextRound = (batches.Max(b => b.RoundNumber ?? 0)) + 1;
            var nextPhase = DecisionHelper.PhaseFor(rec);

            // 设计方法:引擎推荐;AugmentDesign 以 D-最优新批次近似补点
            DOEDesignMethod method;
            string methodNote = "";
            string methodOverride = AgentToolContext.GetString(args, "design_method");
            if (!string.IsNullOrWhiteSpace(methodOverride))
            {
                if (!Enum.TryParse<DOEDesignMethod>(methodOverride, out method))
                    return AgentToolResult.Fail($"不支持的设计方法:{methodOverride}");
            }
            else
            {
                method = engine.RecommendDesignMethod(rec, factors.Count,
                    project?.CurrentPhase ?? DOEProjectPhase.RSM);
                if (method == DOEDesignMethod.AugmentedDesign)
                {
                    method = DOEDesignMethod.DOptimal;
                    methodNote = "补点轮以 D-最优小批次实现。";
                }
            }

            // 生成矩阵:验证轮 = 最优点重复实验;其余走设计引擎
            DOEDesignMatrix matrix;
            DOEDesignMethod recordMethod = method;
            if (rec == NextStepRecommendation.ConfirmationRuns || method == DOEDesignMethod.ConfirmationRuns)
            {
                var bestFactors = DoeAnalysisHelper.ParseNumbers(summary.BestFactorsJson);
                if (bestFactors.Count == 0)
                    return AgentToolResult.Fail("决策报告缺少最优因子组合,无法生成验证轮。");
                int reps = 3;
                try
                {
                    var cfg = await repo.GetOrCreateAutoPilotConfigAsync(proj.ProjectId).ConfigureAwait(false);
                    reps = Math.Max(2, cfg.ConfirmationReps);
                }
                catch { }

                matrix = new DOEDesignMatrix { FactorNames = factors.Select(f => f.FactorName).ToList() };
                for (int i = 0; i < reps; i++)
                {
                    var row = new Dictionary<string, object>();
                    foreach (var f in factors)
                        row[f.FactorName] = bestFactors.TryGetValue(f.FactorName, out double v)
                            ? v
                            : (f.LowerBound + f.UpperBound) / 2.0;
                    matrix.Rows.Add(row);
                }
                recordMethod = DOEDesignMethod.ConfirmationRuns;
                methodNote = $"验证轮:在预测最优点重复 {reps} 次。";
            }
            else
            {
                int centerPoints = (int)(AgentToolContext.GetNumber(args, "center_points") ?? -1);
                string optErr = DoeArgs.BuildOptions(method, factors.Count, centerPoints, out var options);
                if (optErr != null) return AgentToolResult.Fail(optErr);
                try
                {
                    matrix = await _ctx.Resolve<IDOEDesignService>().GenerateAsync(factors, options).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    return AgentToolResult.Fail($"生成设计矩阵失败:{ex.Message}");
                }
                if (matrix == null || matrix.Rows == null || matrix.Rows.Count == 0)
                    return AgentToolResult.Fail("设计矩阵为空。");
            }


            string baseName = proj.ProjectName.Replace("[AI]", "").Trim();
            string batchName = $"{baseName} R{nextRound} {recordMethod}";

            // 化学家坐标系:上一轮批次带转换配置(τ/eq→泵速)则跨轮继承,常数快照不变;
            // 新一轮矩阵同样先做全量反算与泵量程校验,越界即拦(决策引擎可能扩了范围)
            var transformCfg = FactorTransform.ExtractFromDesignConfig(latestDetail?.DesignConfigJson);
            string dualMatrixMd = null;
            if (transformCfg != null)
            {
                // τ/eq 因子可能被引擎从活跃因子池淘汰——那是配置层面的死路,不是量程问题,要给准确诊断
                var factorNames = factors.Select(f => f.FactorName).ToHashSet();
                var missing = new List<string>();
                if (!factorNames.Contains(transformCfg.TauFactor)) missing.Add(transformCfg.TauFactor);
                if (!string.IsNullOrEmpty(transformCfg.EqFactor) && !factorNames.Contains(transformCfg.EqFactor))
                    missing.Add(transformCfg.EqFactor);
                if (missing.Count > 0)
                    return AgentToolResult.Fail(
                        $"化学家坐标系因子「{string.Join("、", missing)}」已被决策引擎从活跃因子池淘汰,转换配置无法继续——这不是量程问题,改因子范围没有用。" +
                        "请向用户说明两条路:①在 DOE 界面把该因子恢复为活跃(它是泵速的换算载体,不建议淘汰);②放弃化学家坐标系,用普通绑定因子重建下一轮设计。");

                dualMatrixMd = DoeArgs.DualSpaceMatrixToMarkdown(matrix, transformCfg, out var violations);
                if (violations.Count > 0)
                {
                    return AgentToolResult.Fail(
                        $"下一轮设计在化学家坐标系下不可行:{violations.Count} 组反算泵速超量程。\n" +
                        string.Join("\n", violations.Take(6)) +
                        "\n引擎建议的范围撞到了设备边界(τ 下界对应泵速上界)。请向用户说明:当前设备配置下该方向已到头,可选 ①收缩范围后重试(用 action 换动作或让用户改范围)②更换更小体积反应器/更高浓度进料后再扩(需重建转换常数)。");
                }
            }

            // 绑定核算(与常规设计同一套):化学家坐标派生因子(τ/eq)算已落实、其余沿用项目绑定;
            // 因子池已自带绑定,这里只做如实核算,不自动补绑也不拦截(避免挡住老项目续轮)。
            DoeArgs.ReconcileFactorBindings(factors, transformCfg, null,
                out var bindingReport, out int boundCount, out _);

            string previewId = DoePreviewStore.Put(new DoePreviewStore.Entry
            {
                BatchName = batchName,
                Method = recordMethod,
                Factors = factors,
                Responses = responses,
                BindingReport = bindingReport,
                BoundCount = boundCount,
                Matrix = matrix,
                ProjectId = proj.ProjectId,
                RoundNumber = nextRound,
                NextPhase = nextPhase,
                TransformConfigJson = transformCfg != null ? FactorTransform.ToJson(transformCfg) : null
            });

            var sb = new StringBuilder();
            sb.AppendLine($"下一轮预览已生成(preview_id:{previewId},尚未入库):");
            sb.AppendLine($"项目「{proj.ProjectName}」第 {nextRound} 轮,动作:{DecisionHelper.RecText(rec)},设计:{recordMethod}," +
                          $"{factors.Count} 因子 × {responses.Count} 响应,共 {matrix.Rows.Count} 组。" +
                          (methodNote.Length > 0 ? methodNote : ""));
            sb.AppendLine("绑定:" + string.Join(";", bindingReport));
            if (transformCfg != null)
                sb.AppendLine($"化学家坐标系随项目继承({FactorTransform.DescribeConstants(transformCfg)}),全部组反算泵速均在量程内。");
            sb.AppendLine();
            sb.AppendLine("实验矩阵明细:");
            sb.AppendLine(dualMatrixMd ?? DoeArgs.MatrixToMarkdown(matrix));
            sb.AppendLine($"请把矩阵展示给用户并确认入库(批次名建议「{batchName}」,可由用户改名后重新生成);" +
                          $"确认后调 create_doe_batch 传 preview_id={previewId}——批次挂到本项目第 {nextRound} 轮,项目阶段推进到 {nextPhase}。" +
                          "入库后可 execute_doe_batch 继续执行(项目批次仍需按规则确认智能推荐)。");
            return AgentToolResult.Ok(sb.ToString());
        }
    }
}

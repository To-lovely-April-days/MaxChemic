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

namespace MaxChemical.Shell.Services.Agent.Tools
{
    /// <summary>分析类工具共用:批次查找(不限状态)与响应优化方向。</summary>
    internal static class DoeAnalysisHelper
    {
        /// <summary>
        /// 按名字(可模糊)找批次;批次名匹配不到时自动按「项目名」检索数据库,
        /// 命中项目则取该项目最近一轮有完成数据的批次。省略名字时取最近一个有完成组的批次。
        /// </summary>
        public static async Task<(DOEBatch Batch, string Error)> FindBatchAsync(IDOERepository repo, string name)
        {
            var recent = await repo.GetRecentBatchesAsync(200).ConfigureAwait(false) ?? new List<DOEBatch>();

            if (!string.IsNullOrWhiteSpace(name))
            {
                name = name.Trim();

                // ① 批次ID精确寻址(create_doe_batch 返回的ID直达,近名批次再多也不歧义)
                var byId = recent.FirstOrDefault(b => string.Equals(b.BatchId, name, StringComparison.OrdinalIgnoreCase));
                if (byId == null && DoeBatchFinder.LooksLikeId(name))
                {
                    try { byId = await repo.GetBatchWithDetailsAsync(name).ConfigureAwait(false); } catch { }
                }
                if (byId != null) return (byId, null);

                // ② 精确同名 → ③ 忽略 [AI] 标签的精确同名 → ④ 包含匹配
                var exact = recent.Where(b => string.Equals(b.BatchName, name, StringComparison.OrdinalIgnoreCase)).ToList();
                if (exact.Count == 0)
                {
                    string qn = DoeBatchFinder.Normalize(name);
                    exact = recent.Where(b => string.Equals(DoeBatchFinder.Normalize(b.BatchName), qn, StringComparison.OrdinalIgnoreCase)).ToList();
                }
                var fuzzy = exact.Count > 0 ? exact
                    : recent.Where(b => DoeBatchFinder.NameContains(b.BatchName, name)).ToList();
                if (fuzzy.Count == 1) return (fuzzy[0], null);
                if (fuzzy.Count > 1)
                    return (null, "匹配到多个批次,请让用户指明:" +
                                  string.Join("、", fuzzy.Take(8).Select(b => b.BatchName)));

                var projects = await repo.GetAllProjectSummariesAsync(50).ConfigureAwait(false)
                               ?? new List<DOEProjectSummary>();

                // 最近批次没命中:扫全部项目下的批次名(老项目的轮次不在最近窗口里)
                var projBatchHits = new List<(DOEBatch Batch, string ProjectName)>();
                foreach (var p in projects)
                {
                    try
                    {
                        var pb = await repo.GetBatchesByProjectAsync(p.ProjectId).ConfigureAwait(false)
                                 ?? new List<DOEBatch>();
                        foreach (var b in pb)
                            if (DoeBatchFinder.NameContains(b.BatchName, name))
                                projBatchHits.Add((b, p.ProjectName));
                    }
                    catch { }
                }
                if (projBatchHits.Count == 1) return (projBatchHits[0].Batch, null);
                if (projBatchHits.Count > 1)
                    return (null, "匹配到多个批次,请让用户指明:" +
                                  string.Join("、", projBatchHits.Take(8).Select(h => $"{h.ProjectName}/{h.Batch.BatchName}")));

                // 再按项目名/项目编号检索,命中则取最近一轮有数据的批次
                var projHit = projects
                    .Where(p => DoeBatchFinder.NameContains(p.ProjectName, name))
                    .ToList();
                var projById = projects.FirstOrDefault(p =>
                    string.Equals(p.ProjectId, name.Trim(), StringComparison.OrdinalIgnoreCase));
                if (projById != null) projHit = new List<DOEProjectSummary> { projById };
                if (projHit.Count > 1)
                {
                    // 唯一精确同名者优先,仍歧义则给带编号的候选表(重名项目只能靠编号选)
                    var exactProj = projHit.Where(p => string.Equals(
                        DoeBatchFinder.Normalize(p.ProjectName), DoeBatchFinder.Normalize(name),
                        StringComparison.OrdinalIgnoreCase)).ToList();
                    if (exactProj.Count == 1) projHit = exactProj;
                    else return (null, AmbiguousProjectsError(name, projHit));
                }
                if (projHit.Count == 1)
                {
                    var batches = await repo.GetBatchesByProjectAsync(projHit[0].ProjectId).ConfigureAwait(false)
                                  ?? new List<DOEBatch>();
                    foreach (var b in batches.OrderByDescending(x => x.CreatedTime))
                    {
                        var d = await repo.GetBatchWithDetailsAsync(b.BatchId).ConfigureAwait(false);
                        if (d != null && d.Runs.Any(r => r.Status == DOERunStatus.Completed))
                            return (d, null);
                    }
                    return (null, $"项目「{projHit[0].ProjectName}」下还没有已完成数据的批次,无法分析。" +
                                  "可用 find_doe_project 查看该项目各轮状态。");
                }

                return (null, $"批次和项目里都没有名为「{name}」的记录。" +
                              (recent.Count > 0 ? "最近的批次:" + string.Join("、", recent.Take(8).Select(b => b.BatchName)) + "。" : "") +
                              "可用 find_doe_project 列出全部项目核对名称。");
            }

            if (recent.Count == 0) return (null, "还没有任何 DOE 批次。");

            // 省略名字:优先最近一个已有完成组的批次(能分析),否则报无
            foreach (var b in recent)
            {
                var detail = await repo.GetBatchWithDetailsAsync(b.BatchId).ConfigureAwait(false);
                if (detail != null && detail.Runs.Any(r => r.Status == DOERunStatus.Completed))
                    return (detail, null);
            }
            return (null, "最近的批次都还没有已完成的实验组,无法分析。");
        }

        /// <summary>响应优化方向是否为最小化(读 Desirability 配置;缺省最大化)。</summary>
        public static async Task<bool> IsMinimizeAsync(IDOERepository repo, string batchId, string respName)
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
                    if (g.ValueKind == JsonValueKind.Number) return g.GetInt32() == 1;
                    if (g.ValueKind == JsonValueKind.String)
                        return string.Equals(g.GetString(), "Minimize", StringComparison.OrdinalIgnoreCase);
                    return false;
                }
            }
            catch { }
            return false;
        }

        /// <summary>
        /// 按名字(可模糊)或项目编号在全库项目表中定位唯一项目。
        /// 历史数据存在重名项目:模糊命中多个时先看是否有唯一精确同名者,仍歧义则返回带编号的候选表,
        /// 编号(project_id)是重名场景下唯一可靠的选择方式——所有接受 project_name 的工具都可直接传编号。
        /// </summary>
        public static async Task<(DOEProjectSummary Project, string Error)> FindProjectAsync(IDOERepository repo, string name)
        {
            var projects = await repo.GetAllProjectSummariesAsync(50).ConfigureAwait(false)
                           ?? new List<DOEProjectSummary>();
            if (projects.Count == 0) return (null, "数据库里还没有任何 DOE 优化项目。");

            if (string.IsNullOrWhiteSpace(name))
            {
                if (projects.Count == 1) return (projects[0], null);
                return (null, "有多个项目,请让用户指明项目名:" +
                              string.Join("、", projects.Take(10).Select(p => p.ProjectName)));
            }

            // ① 项目编号精确寻址(重名项目的唯一可靠入口)
            var byId = projects.FirstOrDefault(p =>
                string.Equals(p.ProjectId, name.Trim(), StringComparison.OrdinalIgnoreCase));
            if (byId != null) return (byId, null);

            // 编号不在最近摘要窗口内(老历史,如 search_lab_history 翻出的项目):直查全库
            if (DoeBatchFinder.LooksLikeId(name))
            {
                try
                {
                    var full = await repo.GetProjectAsync(name.Trim()).ConfigureAwait(false);
                    if (full != null)
                        return (new DOEProjectSummary
                        {
                            ProjectId = full.ProjectId,
                            ProjectName = full.ProjectName,
                            CurrentPhase = full.CurrentPhase,
                            Status = full.Status,
                            TotalBatches = full.CompletedRounds,
                            TotalExperiments = full.TotalExperiments,
                            BestResponseValue = full.BestResponseValue,
                            CreatedTime = full.CreatedTime,
                            UpdatedTime = full.UpdatedTime
                        }, null);
                }
                catch { }
            }

            var hit = projects
                .Where(p => DoeBatchFinder.NameContains(p.ProjectName, name))
                .ToList();
            if (hit.Count == 0)
                return (null, $"没有名字含「{name}」的项目。现有项目:" +
                              string.Join("、", projects.Take(10).Select(p => p.ProjectName)));
            if (hit.Count > 1)
            {
                // ② 唯一精确同名者优先(「X」不再被「X [AI]」搅成歧义)
                var exact = hit.Where(p => string.Equals(
                    DoeBatchFinder.Normalize(p.ProjectName), DoeBatchFinder.Normalize(name),
                    StringComparison.OrdinalIgnoreCase)).ToList();
                if (exact.Count == 1) return (exact[0], null);
                return (null, AmbiguousProjectsError(name, hit));
            }
            return (hit[0], null);
        }

        /// <summary>重名歧义时的候选表:带编号与区分特征,并告知用编号精确选择,禁止原样重试。</summary>
        public static string AmbiguousProjectsError(string name, List<DOEProjectSummary> hit)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"名字含「{name}」的项目有 {hit.Count} 个(存在重名),必须用项目编号区分:");
            sb.AppendLine();
            sb.AppendLine("| 项目编号 | 项目名 | 阶段 | 轮次 | 累计实验 | 最优值 | 创建时间 |");
            sb.AppendLine("| --- | --- | --- | --- | --- | --- | --- |");
            foreach (var p in hit.Take(10))
                sb.AppendLine($"| {p.ProjectId} | {p.ProjectName} | {p.PhaseText} | {p.TotalBatches} | {p.TotalExperiments} | " +
                              $"{(p.BestResponseValue.HasValue ? p.BestResponseValue.Value.ToString("0.####") : "-")} | {p.CreatedTime:yyyy-MM-dd} |");
            sb.AppendLine();
            sb.Append("不要再用同一个名字重试(结果只会相同)。若用户此前提到的特征(轮次数、最优值等)能唯一对应上表某一行," +
                      "就把该行的项目编号当作 project_name 传入继续;对应不上就把这张表展示给用户,请用户选择。");
            return sb.ToString();
        }

        /// <summary>按「项目 + 轮号」定位批次(用户说第 N 轮时的自然入口)。</summary>
        public static async Task<(DOEBatch Batch, string Error)> FindProjectBatchByRoundAsync(
            IDOERepository repo, string projectName, int round)
        {
            var (proj, err) = await FindProjectAsync(repo, projectName).ConfigureAwait(false);
            if (proj == null) return (null, err);

            var batches = await repo.GetBatchesByProjectAsync(proj.ProjectId).ConfigureAwait(false)
                          ?? new List<DOEBatch>();
            var hit = batches.Where(b => (b.RoundNumber ?? -1) == round)
                             .OrderByDescending(b => b.CreatedTime)
                             .ToList();
            if (hit.Count == 0)
                return (null, $"项目「{proj.ProjectName}」没有第 {round} 轮。现有轮次:" +
                              string.Join("、", batches.OrderBy(b => b.RoundNumber ?? 0)
                                  .Select(b => $"第 {b.RoundNumber?.ToString() ?? "-"} 轮({b.BatchName})")));
            return (hit[0], null);
        }

        /// <summary>把图表直接插入对话窗(不经模型转述)。</summary>
        public static void PostChart(AgentToolContext ctx, object spec)
        {
            try
            {
                ctx.Resolve<MaxChemical.Shell.ViewModels.AgentChatViewModel>()
                   .PostChart(JsonSerializer.Serialize(spec));
            }
            catch { }
        }

        public static Dictionary<string, double> ParseNumbers(string json)
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
    }

    /// <summary>对话式 DOE 统计分析:OLS 拟合 + 显著因子 + 图表直出聊天窗(只读)。</summary>
    public sealed class AnalyzeDoeBatchTool : IAgentTool
    {
        private readonly AgentToolContext _ctx;
        private readonly ILogService _logger = LogManager.GetLogger<AnalyzeDoeBatchTool>();
        public AnalyzeDoeBatchTool(AgentToolContext ctx) => _ctx = ctx;

        public string Name => "analyze_doe_batch";
        public string DisplayName => "分析 DOE 实验结果";
        public string Description => @"对一个已有完成组的 DOE 批次做完整 OLS 统计分析:模型摘要(R 方/调整 R 方/预测 R 方/失拟/RMSE/信噪比)、ANOVA 方差分析表、完整回归系数表(系数/标准误/T/P/VIF)、回归方程,并把三张专业图表直接插入对话窗:响应趋势图、标准化效应 Pareto 图(带 t 临界线)、实际值vs预测值图(带对角线)。
定位方式三选一:1)project_name+round_number(用户说「第 N 轮」时用这个,最可靠);2)batch_name 批次名或项目名(全库模糊检索);3)全省略取最近一个有完成组的批次。
用户问「这轮实验结果怎么样」「哪个因子显著」「帮我分析一下」时用本工具。图表由系统直接插入,你的回复不要再用表格复述图上的逐点数据,只给结论与建议。";
        public string ParametersSchema => @"{
  ""type"":""object"",
  ""properties"":{
    ""batch_name"":{""type"":""string"",""description"":""批次名或项目名(可模糊);与 round_number 二选一""},
    ""project_name"":{""type"":""string"",""description"":""项目名(可模糊),配合 round_number 使用""},
    ""round_number"":{""type"":""number"",""description"":""项目内轮号(用户说第 N 轮时传 N);需配合 project_name(或上文唯一项目)""},
    ""response_name"":{""type"":""string"",""description"":""要分析的响应名;省略则取第一个响应""},
    ""model_type"":{""type"":""string"",""enum"":[""linear"",""interaction"",""quadratic""],""description"":""模型类型;省略则按设计方法自动选择(RSM 类用 quadratic,筛选类用 linear)""}
  }
}";
        public bool RequiresConfirmation => false;
        public string DescribeAction(JsonElement args) => "分析 DOE 实验结果";

        public async Task<AgentToolResult> ExecuteAsync(JsonElement args)
        {
            var repo = _ctx.Resolve<IDOERepository>();

            DOEBatch found;
            string err;
            double? round = AgentToolContext.GetNumber(args, "round_number");
            if (round.HasValue)
            {
                // 项目 + 轮号定位:project_name 缺省时也允许用 batch_name 里给的项目名
                string projName = AgentToolContext.GetString(args, "project_name")
                                  ?? AgentToolContext.GetString(args, "batch_name");
                (found, err) = await DoeAnalysisHelper.FindProjectBatchByRoundAsync(
                    repo, projName, (int)round.Value).ConfigureAwait(false);
            }
            else
            {
                string name = AgentToolContext.GetString(args, "batch_name")
                              ?? AgentToolContext.GetString(args, "project_name");
                (found, err) = await DoeAnalysisHelper.FindBatchAsync(repo, name).ConfigureAwait(false);
            }
            if (found == null) return AgentToolResult.Fail(err);

            var batch = found.Runs.Count > 0
                ? found
                : await repo.GetBatchWithDetailsAsync(found.BatchId).ConfigureAwait(false);
            if (batch == null) return AgentToolResult.Fail("批次数据读取失败。");

            var done = batch.Runs
                .Where(r => r.Status == DOERunStatus.Completed)
                .OrderBy(r => r.RunIndex)
                .ToList();
            if (done.Count < 3)
                return AgentToolResult.Fail(
                    $"批次「{batch.BatchName}」只有 {done.Count} 组已完成实验,数据太少无法做统计分析(至少 3 组)。");

            // 响应名
            string respName = AgentToolContext.GetString(args, "response_name");
            var respNames = batch.Responses?.Select(r => r.ResponseName).ToList() ?? new List<string>();
            if (string.IsNullOrWhiteSpace(respName))
                respName = respNames.FirstOrDefault();
            else if (!respNames.Contains(respName))
                return AgentToolResult.Fail($"批次没有响应「{respName}」。可选:{string.Join("、", respNames)}");
            if (string.IsNullOrEmpty(respName))
                return AgentToolResult.Fail("批次没有定义响应变量,无法分析。");

            // 模型类型:显式指定优先,否则按设计方法选
            string modelType = AgentToolContext.GetString(args, "model_type");
            if (string.IsNullOrWhiteSpace(modelType))
                modelType = batch.DesignMethod is DOEDesignMethod.CCD or DOEDesignMethod.BoxBehnken
                    or DOEDesignMethod.DOptimal or DOEDesignMethod.AugmentedDesign
                    ? "quadratic" : "linear";

            OLSAnalysisResult ols;
            try
            {
                var svc = _ctx.Resolve<IDOEAnalysisService>();
                ols = await svc.FitOlsAsync(batch.BatchId, respName, modelType).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"OLS 分析失败: {ex.Message}");
                return AgentToolResult.Fail($"OLS 分析失败:{ex.Message}。可尝试指定 model_type=linear 或检查数据完整性。");
            }
            if (ols?.ModelSummary == null)
                return AgentToolResult.Fail("分析引擎未返回结果,请检查批次数据。");

            bool minimize = await DoeAnalysisHelper.IsMinimizeAsync(repo, batch.BatchId, respName).ConfigureAwait(false);

            // ── 图 1:响应随实验组变化(最优点高亮) ──
            var xLabels = new List<string>();
            var yVals = new List<double?>();
            int bestIdx = -1; double bestVal = 0;
            for (int i = 0; i < done.Count; i++)
            {
                xLabels.Add(done[i].RunIndex.ToString());
                var resp = DoeAnalysisHelper.ParseNumbers(done[i].ResponseValuesJson);
                if (resp.TryGetValue(respName, out double v))
                {
                    yVals.Add(v);
                    if (bestIdx < 0 || (minimize ? v < bestVal : v > bestVal)) { bestIdx = i; bestVal = v; }
                }
                else yVals.Add(null);
            }
            PostChart(new
            {
                type = "line",
                title = $"{respName} 随实验组变化(红点为最优)",
                yLabel = respName,
                x = xLabels,
                series = new[] { new { name = respName, values = yVals } },
                highlightIndex = bestIdx
            });

            // ── 图 2:标准化效应 Pareto 图(DOE 标志性图表,红条越过临界线即显著) ──
            var svcRef = _ctx.Resolve<IDOEAnalysisService>();
            try
            {
                string paretoJson = await svcRef.GetEffectsParetoAsync(batch.BatchId, respName, 0.05).ConfigureAwait(false);
                using var pd = JsonDocument.Parse(paretoJson);
                if (pd.RootElement.TryGetProperty("effects", out var eff) && eff.ValueKind == JsonValueKind.Array)
                {
                    var names = new List<string>();
                    var tvals = new List<double?>();
                    var flags = new List<bool>();
                    foreach (var e2 in eff.EnumerateArray().Take(12))
                    {
                        names.Add(e2.TryGetProperty("term", out var tn) ? tn.GetString() : "");
                        tvals.Add(e2.TryGetProperty("abs_t", out var at) && at.ValueKind == JsonValueKind.Number
                            ? at.GetDouble() : (double?)null);
                        flags.Add(e2.TryGetProperty("significant", out var sg) && sg.ValueKind == JsonValueKind.True);
                    }
                    double? tCrit = pd.RootElement.TryGetProperty("t_crit", out var tc) && tc.ValueKind == JsonValueKind.Number
                        ? tc.GetDouble() : (double?)null;
                    if (names.Count > 0)
                        PostChart(new
                        {
                            type = "hbar",
                            title = $"标准化效应 Pareto 图(红条越过虚线 t 临界{(tCrit.HasValue ? "=" + tCrit.Value.ToString("0.##") : "")} 即显著)",
                            xLabel = "标准化效应 |t|",
                            x = names,
                            series = new[] { new { name = "标准化效应", values = tvals } },
                            refLine = tCrit,
                            flags
                        });
                }
            }
            catch (Exception ex) { _logger.LogWarning($"Pareto 图生成失败: {ex.Message}"); }

            // ── 图 3:实际值 vs 预测值(贴近对角线=预测可靠) ──
            try
            {
                string diagJson = await svcRef.GetResidualDiagnosticsAsync(batch.BatchId, respName).ConfigureAwait(false);
                using var dd = JsonDocument.Parse(diagJson);
                if (dd.RootElement.TryGetProperty("residuals_vs_fitted", out var rvf) &&
                    rvf.TryGetProperty("fitted", out var fitted) &&
                    rvf.TryGetProperty("residuals", out var resid) &&
                    fitted.ValueKind == JsonValueKind.Array && resid.ValueKind == JsonValueKind.Array)
                {
                    var fArr = fitted.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.Number)
                        .Select(v => v.GetDouble()).ToList();
                    var rArr = resid.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.Number)
                        .Select(v => v.GetDouble()).ToList();
                    int cnt = Math.Min(fArr.Count, rArr.Count);
                    if (cnt >= 3)
                    {
                        var actual = new List<double?>();
                        for (int i = 0; i < cnt; i++) actual.Add(fArr[i] + rArr[i]);
                        PostChart(new
                        {
                            type = "scatter",
                            title = "实际值 vs 预测值(点越贴近对角线,模型预测越准)",
                            xLabel = "预测值",
                            yLabel = "实际值",
                            x = new List<string>(),
                            xNumeric = fArr.Take(cnt).ToList(),
                            series = new[] { new { name = "实际值", values = actual } },
                            diagonal = true
                        });
                    }
                }
            }
            catch (Exception ex) { _logger.LogWarning($"实际vs预测图生成失败: {ex.Message}"); }

            var terms = (ols.Coefficients ?? new List<CoefficientRow>())
                .Where(c => !IsIntercept(c.Term))
                .OrderBy(c => c.PValue)
                .Take(12)
                .ToList();

            // ── 文本结论素材 ──
            var m = ols.ModelSummary;
            var sig = terms.Where(t => t.PValue < 0.05).ToList();
            var insig = terms.Where(t => t.PValue >= 0.05).ToList();
            var bestRun = bestIdx >= 0 ? done[bestIdx] : null;

            var sb = new StringBuilder();
            sb.AppendLine($"批次「{batch.BatchName}」响应「{respName}」OLS 分析完成({ModelTypeText(modelType)},{done.Count} 组数据,优化方向{(minimize ? "越小越好" : "越大越好")}):");
            sb.AppendLine();
            sb.AppendLine("| 指标 | 值 | 解读 |");
            sb.AppendLine("| --- | --- | --- |");
            sb.AppendLine($"| R 方 | {m.RSquared:0.###} | 模型解释了 {m.RSquared * 100:0.#}% 的响应波动 |");
            sb.AppendLine($"| 调整 R 方 | {m.RSquaredAdj:0.###} | 惩罚项数后的解释力 |");
            sb.AppendLine($"| 预测 R 方 | {m.RSquaredPred:0.###} | 对新数据的预测能力 |");
            sb.AppendLine($"| 模型 P 值 | {(m.ModelP.HasValue ? m.ModelP.Value.ToString("0.####") : "无")} | 小于 0.05 表示模型整体显著 |");
            sb.AppendLine($"| 失拟 P 值 | {(m.LackOfFitP.HasValue ? m.LackOfFitP.Value.ToString("0.####") : "无")} | 小于 0.05 表示模型形式可能不对 |");
            sb.AppendLine($"| RMSE | {m.RMSE:0.####} | 残差标准差,越小拟合越好 |");
            sb.AppendLine($"| 信噪比 | {m.AdequatePrecision:0.##} | Adeq Precision,大于 4 模型才可用于寻优 |");

            // ── ANOVA 方差分析表 ──
            if (ols.AnovaTable != null && ols.AnovaTable.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("ANOVA 方差分析:");
                sb.AppendLine("| 来源 | 自由度 | SS | MS | F | P | 显著 |");
                sb.AppendLine("| --- | --- | --- | --- | --- | --- | --- |");
                foreach (var a in ols.AnovaTable.Take(12))
                    sb.AppendLine($"| {a.Source} | {a.DF} | {a.SS:0.####} | " +
                                  $"{(a.MS.HasValue ? a.MS.Value.ToString("0.####") : "-")} | " +
                                  $"{(a.FValue.HasValue ? a.FValue.Value.ToString("0.###") : "-")} | " +
                                  $"{(a.PValue.HasValue ? a.PValue.Value.ToString("0.####") : "-")} | " +
                                  $"{(a.IsSignificant ? "是" : "")} |");
            }

            // ── 回归系数表(完整统计量) ──
            var coefAll = (ols.Coefficients ?? new List<CoefficientRow>()).Take(14).ToList();
            if (coefAll.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("回归系数(编码值):");
                sb.AppendLine("| 项 | 系数 | 标准误 | T | P | VIF |");
                sb.AppendLine("| --- | --- | --- | --- | --- | --- |");
                foreach (var c in coefAll)
                    sb.AppendLine($"| {c.Term} | {c.Coefficient:0.####} | {c.StdError:0.####} | " +
                                  $"{c.TValue:0.###} | {c.PValue:0.####} | " +
                                  $"{(c.VIF.HasValue ? c.VIF.Value.ToString("0.##") : "-")} |");
            }

            sb.AppendLine();
            sb.AppendLine("显著项(p 小于 0.05):" +
                (sig.Count > 0
                    ? string.Join(",", sig.Select(t => $"{t.Term}(p={Round4(t.PValue):0.####},系数 {t.Coefficient:0.###})"))
                    : "(无)"));
            if (insig.Count > 0)
                sb.AppendLine("不显著项:" + string.Join(",", insig.Select(t => t.Term)));
            if (!string.IsNullOrWhiteSpace(m.Equation))
                sb.AppendLine($"回归方程(编码值):{m.Equation}");
            if (bestRun != null)
            {
                var factors = DoeAnalysisHelper.ParseNumbers(bestRun.FactorValuesJson);
                sb.AppendLine($"当前最优:第 {bestRun.RunIndex} 组,{respName}={bestVal:0.####}" +
                              (factors.Count > 0
                                  ? $"(因子:{string.Join(",", factors.Take(6).Select(kv => $"{kv.Key}={kv.Value:0.####}"))})"
                                  : ""));
            }
            if (ols.DroppedTerms != null && ols.DroppedTerms.Count > 0)
                sb.AppendLine($"注:因数据不足或共线性,以下模型项被自动剔除:{string.Join(",", ols.DroppedTerms)}");
            sb.AppendLine();
            sb.Append("三张图(响应趋势、标准化效应 Pareto、实际值vs预测值)已直接插入对话窗。" +
                      "请给出专业但通俗的结论,依次覆盖:1)模型质量判定(R 方、失拟、信噪比,够不够可靠);" +
                      "2)显著因子排序与影响方向(系数正负代表增大因子是升还是降响应);" +
                      "3)预测可靠性(实际vs预测贴合程度);4)下一步建议(固定谁、调谁、要不要补实验)。" +
                      "不要复述图上逐点数据,不要重复已展示的表格。");

            return AgentToolResult.Ok(sb.ToString());
        }

        private static bool IsIntercept(string term)
            => string.IsNullOrWhiteSpace(term) ||
               term.Equals("Intercept", StringComparison.OrdinalIgnoreCase) ||
               term.Equals("Constant", StringComparison.OrdinalIgnoreCase) ||
               term.Contains("常数") || term.Contains("截距");

        private static double Round4(double v) => Math.Round(v, 4);

        private static string ModelTypeText(string t) => t switch
        {
            "quadratic" => "二次模型",
            "interaction" => "交互模型",
            _ => "线性模型"
        };

        private void PostChart(object spec)
        {
            try
            {
                string json = JsonSerializer.Serialize(spec);
                _ctx.Resolve<MaxChemical.Shell.ViewModels.AgentChatViewModel>().PostChart(json);
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"图表插入失败: {ex.Message}");
            }
        }
    }

    /// <summary>项目级多轮对比分析(只读):逐轮 OLS 建模,输出对比表 + 两张趋势图。</summary>
    public sealed class AnalyzeDoeProjectTool : IAgentTool
    {
        private readonly AgentToolContext _ctx;
        private readonly ILogService _logger = LogManager.GetLogger<AnalyzeDoeProjectTool>();
        public AnalyzeDoeProjectTool(AgentToolContext ctx) => _ctx = ctx;

        public string Name => "analyze_doe_project";
        public string DisplayName => "项目多轮对比分析";
        public string Description => @"把一个优化项目下的所有轮次做专业级联合分析:逐轮 OLS 建模,输出每轮完整统计对比表(R 方/调整 R 方/失拟 P/显著项/最优响应/较上轮提升)、显著因子跨轮稳定性矩阵、各轮最优条件迁移表,并把三张图直接插入对话窗:各轮最优与累计最优双线走势、各轮模型 R 方对比、全项目实验全景散点。
用户要「几轮一起分析」「整个项目分析」「看优化进展」时用本工具。注意:不同轮次设计不同,不能把数据合并成一个模型硬拟合,逐轮建模再对比才是正确做法——回复时如实说明这一点。
单轮深入分析(ANOVA/系数表/Pareto/实际vs预测)仍用 analyze_doe_batch。";
        public string ParametersSchema => @"{
  ""type"":""object"",
  ""properties"":{
    ""project_name"":{""type"":""string"",""description"":""项目名(可模糊);只有一个项目时可省略""},
    ""response_name"":{""type"":""string"",""description"":""要对比的响应名;省略则取各轮第一个响应""}
  }
}";
        public bool RequiresConfirmation => false;
        public string DescribeAction(JsonElement args) => "项目多轮对比分析";

        /// <summary>单轮统计汇总(逐轮建模的产物,供表格/矩阵/图表共用)。</summary>
        private sealed class RoundStat
        {
            public string Round = "";
            public string Name = "";
            public string Method = "";
            public int Done;
            public int Total;
            public double? R2;
            public double? AdjR2;
            public double? LofP;
            public List<string> SigTerms = new();
            public List<string> AllTerms = new();
            public bool Modeled;
            public double? Best;
            public Dictionary<string, double> BestFactors = new();
            public List<double> AllResponses = new();
        }

        public async Task<AgentToolResult> ExecuteAsync(JsonElement args)
        {
            var repo = _ctx.Resolve<IDOERepository>();
            var (proj, err) = await DoeAnalysisHelper.FindProjectAsync(
                repo, AgentToolContext.GetString(args, "project_name")).ConfigureAwait(false);
            if (proj == null) return AgentToolResult.Fail(err);

            var batches = await repo.GetBatchesByProjectAsync(proj.ProjectId).ConfigureAwait(false)
                          ?? new List<DOEBatch>();
            if (batches.Count == 0)
                return AgentToolResult.Fail($"项目「{proj.ProjectName}」下还没有批次。");

            string respArg = AgentToolContext.GetString(args, "response_name");
            var svc = _ctx.Resolve<IDOEAnalysisService>();

            var stats = new List<RoundStat>();
            string respName = respArg;
            bool minimize = false;

            foreach (var b in batches.OrderBy(x => x.RoundNumber ?? 0).ThenBy(x => x.CreatedTime))
            {
                var d = await repo.GetBatchWithDetailsAsync(b.BatchId).ConfigureAwait(false);
                if (d == null) continue;

                var active = d.Runs.Where(r => r.Status != DOERunStatus.Skipped).ToList();
                var done = active.Where(r => r.Status == DOERunStatus.Completed)
                                 .OrderBy(r => r.RunIndex).ToList();

                string resp = respArg;
                if (string.IsNullOrWhiteSpace(resp))
                    resp = d.Responses?.FirstOrDefault()?.ResponseName;
                if (string.IsNullOrEmpty(respName)) respName = resp;

                var rs = new RoundStat
                {
                    Round = $"第 {b.RoundNumber?.ToString() ?? "-"} 轮",
                    Name = b.BatchName,
                    Method = b.DesignMethod.ToString(),
                    Done = done.Count,
                    Total = active.Count
                };

                // 该轮全部响应值(按组序)与最优组
                if (!string.IsNullOrEmpty(resp) && done.Count > 0)
                {
                    minimize = await DoeAnalysisHelper.IsMinimizeAsync(repo, b.BatchId, resp).ConfigureAwait(false);
                    DOERunRecord bestRun = null;
                    foreach (var run in done)
                    {
                        var rv = DoeAnalysisHelper.ParseNumbers(run.ResponseValuesJson);
                        if (!rv.TryGetValue(resp, out double v)) continue;
                        rs.AllResponses.Add(v);
                        if (rs.Best == null || (minimize ? v < rs.Best : v > rs.Best))
                        {
                            rs.Best = v;
                            bestRun = run;
                        }
                    }
                    if (bestRun != null)
                        rs.BestFactors = DoeAnalysisHelper.ParseNumbers(bestRun.FactorValuesJson);
                }

                // 该轮 OLS(数据不足或验证轮拟合失败时如实标注)
                if (done.Count >= 3 && !string.IsNullOrEmpty(resp))
                {
                    try
                    {
                        string modelType = b.DesignMethod is DOEDesignMethod.CCD or DOEDesignMethod.BoxBehnken
                            or DOEDesignMethod.DOptimal or DOEDesignMethod.AugmentedDesign
                            ? "quadratic" : "linear";
                        var ols = await svc.FitOlsAsync(b.BatchId, resp, modelType).ConfigureAwait(false);
                        if (ols?.ModelSummary != null)
                        {
                            rs.Modeled = true;
                            rs.R2 = ols.ModelSummary.RSquared;
                            rs.AdjR2 = ols.ModelSummary.RSquaredAdj;
                            rs.LofP = ols.ModelSummary.LackOfFitP;
                            foreach (var c in ols.Coefficients ?? new List<CoefficientRow>())
                            {
                                if (c.Term.Equals("Intercept", StringComparison.OrdinalIgnoreCase) ||
                                    c.Term.Contains("常数") || c.Term.Contains("截距")) continue;
                                rs.AllTerms.Add(c.Term);
                                if (c.PValue < 0.05) rs.SigTerms.Add(c.Term);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning($"轮次 {b.BatchName} OLS 失败: {ex.Message}");
                    }
                }

                stats.Add(rs);
            }

            if (stats.Count == 0 || stats.All(r => r.Done == 0))
                return AgentToolResult.Fail($"项目「{proj.ProjectName}」各轮都还没有完成数据,无法分析。");

            // ── 全程最优(按轮) ──
            int overallBestIdx = -1;
            double overallBest = 0;
            for (int i = 0; i < stats.Count; i++)
            {
                if (!stats[i].Best.HasValue) continue;
                if (overallBestIdx < 0 || (minimize ? stats[i].Best.Value < overallBest : stats[i].Best.Value > overallBest))
                { overallBestIdx = i; overallBest = stats[i].Best.Value; }
            }

            // ── 图 1:各轮最优 + 累计最优 双线走势 ──
            var bestSeries = stats.Select(r => r.Best).ToList();
            var running = new List<double?>();
            double? cur = null;
            foreach (var v in bestSeries)
            {
                if (v.HasValue && (cur == null || (minimize ? v < cur : v > cur))) cur = v;
                running.Add(cur);
            }
            DoeAnalysisHelper.PostChart(_ctx, new
            {
                type = "line",
                title = $"各轮最优与累计最优{respName ?? "响应"}走势(红点为全程最优)",
                yLabel = respName ?? "响应",
                x = stats.Select(r => r.Round).ToList(),
                series = new[]
                {
                    new { name = "该轮最优", values = bestSeries },
                    new { name = "累计最优", values = running }
                },
                highlightIndex = overallBestIdx
            });

            // ── 图 2:各轮模型 R 方对比 ──
            if (stats.Any(r => r.R2.HasValue))
            {
                DoeAnalysisHelper.PostChart(_ctx, new
                {
                    type = "bar",
                    title = "各轮模型 R 方对比(虚线 0.8 为合格线)",
                    yLabel = "R 方",
                    x = stats.Select(r => r.Round).ToList(),
                    series = new[] { new { name = "R 方", values = stats.Select(r => r.R2).ToList() } },
                    refLine = 0.8
                });
            }

            // ── 图 3:全项目实验全景(每一组实验一个点,看整体收敛云图) ──
            var panoX = new List<double>();
            var panoY = new List<double?>();
            int globalBestIdx = -1;
            double gBest = 0;
            foreach (var rs in stats)
                foreach (var v in rs.AllResponses)
                {
                    panoX.Add(panoX.Count + 1);
                    panoY.Add(v);
                    if (globalBestIdx < 0 || (minimize ? v < gBest : v > gBest))
                    { gBest = v; globalBestIdx = panoX.Count - 1; }
                }
            if (panoX.Count >= 5)
            {
                DoeAnalysisHelper.PostChart(_ctx, new
                {
                    type = "scatter",
                    title = $"全项目实验全景({panoX.Count} 组,红点为全程最优)",
                    xLabel = "累计实验序号",
                    yLabel = respName ?? "响应",
                    x = new List<string>(),
                    xNumeric = panoX,
                    series = new[] { new { name = respName ?? "响应", values = panoY } },
                    highlightIndex = globalBestIdx
                });
            }

            // ── 表 A:逐轮统计对比 ──
            var sb = new StringBuilder();
            sb.AppendLine($"项目「{proj.ProjectName}」联合分析:共 {stats.Count} 轮、累计完成 {stats.Sum(r => r.Done)} 组," +
                          $"响应「{respName}」优化方向{(minimize ? "越小越好" : "越大越好")}。逐轮建模统计:");
            sb.AppendLine();
            sb.AppendLine("| 轮次 | 方法 | 完成/总组 | R 方 | 调整 R 方 | 失拟 P | 显著项数 | 最优响应 | 较上轮 |");
            sb.AppendLine("| --- | --- | --- | --- | --- | --- | --- | --- | --- |");
            double? prevBest = null;
            foreach (var r in stats)
            {
                string delta = "-";
                if (r.Best.HasValue && prevBest.HasValue)
                {
                    double dv = r.Best.Value - prevBest.Value;
                    delta = (dv >= 0 ? "+" : "") + dv.ToString("0.###");
                }
                if (r.Best.HasValue) prevBest = r.Best;

                sb.AppendLine($"| {r.Round} | {r.Method} | {r.Done}/{r.Total} | " +
                              $"{(r.R2.HasValue ? r.R2.Value.ToString("0.###") : "-")} | " +
                              $"{(r.AdjR2.HasValue ? r.AdjR2.Value.ToString("0.###") : "-")} | " +
                              $"{(r.LofP.HasValue ? r.LofP.Value.ToString("0.###") : "-")} | " +
                              $"{(r.Modeled ? r.SigTerms.Count.ToString() : "-")} | " +
                              $"{(r.Best.HasValue ? r.Best.Value.ToString("0.####") : "-")} | {delta} |");
            }

            // ── 表 B:显著因子跨轮稳定性矩阵 ──
            var sigUnion = stats.SelectMany(r => r.SigTerms)
                .GroupBy(t => t)
                .OrderByDescending(g => g.Count())
                .Select(g => g.Key)
                .Take(10)
                .ToList();
            if (sigUnion.Count > 0 && stats.Count(r => r.Modeled) >= 2)
            {
                sb.AppendLine();
                sb.AppendLine("显著因子跨轮稳定性(显著=该轮 p 小于 0.05;- 表示该轮未建模或模型中无此项):");
                sb.AppendLine("| 模型项 | " + string.Join(" | ", stats.Select(r => r.Round)) + " |");
                sb.AppendLine("|" + string.Concat(Enumerable.Repeat(" --- |", stats.Count + 1)));
                foreach (var term in sigUnion)
                {
                    var cells = stats.Select(r =>
                        !r.Modeled || !r.AllTerms.Contains(term) ? "-"
                        : r.SigTerms.Contains(term) ? "显著" : "否");
                    sb.AppendLine($"| {term} | " + string.Join(" | ", cells) + " |");
                }
            }

            // ── 表 C:各轮最优条件迁移 ──
            var factorKeys = stats.Where(r => r.BestFactors.Count > 0)
                .SelectMany(r => r.BestFactors.Keys)
                .Distinct()
                .Take(5)
                .ToList();
            if (factorKeys.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("各轮最优条件迁移(最优点是否稳定在同一区域是收敛的重要证据):");
                sb.AppendLine("| 轮次 | " + string.Join(" | ", factorKeys) + $" | 最优{respName} |");
                sb.AppendLine("|" + string.Concat(Enumerable.Repeat(" --- |", factorKeys.Count + 2)));
                foreach (var r in stats.Where(x => x.Best.HasValue))
                {
                    var cells = factorKeys.Select(k =>
                        r.BestFactors.TryGetValue(k, out double v) ? v.ToString("0.####") : "-");
                    sb.AppendLine($"| {r.Round} | " + string.Join(" | ", cells) + $" | {r.Best.Value:0.####} |");
                }
            }

            sb.AppendLine();
            if (overallBestIdx >= 0)
                sb.AppendLine($"全程最优出现在{stats[overallBestIdx].Round}({stats[overallBestIdx].Name}):{respName}={overallBest:0.####}。");

            sb.Append("三张图(各轮最优与累计最优走势、各轮 R 方对比、全项目实验全景)已直接插入对话窗。" +
                      "请给出专业但通俗的结论,依次覆盖:1)收敛性判定(看「较上轮」提升量是否递减、最优条件是否稳定在同一区域);" +
                      "2)模型质量走向(R 方与失拟的逐轮变化);3)因子结论稳定性(稳定性矩阵里持续显著的因子最可信);" +
                      "4)下一步建议(继续迭代、验证收官、还是调整因子范围)。" +
                      "并说明各轮设计不同、系统按轮建模再对比(不能跨轮合并硬拟合)。不要复述表格与图上的逐点数据。");
            return AgentToolResult.Ok(sb.ToString());
        }
    }

    /// <summary>按名字全库检索 DOE 项目(只读):项目概况 + 各轮批次状态,分析/执行的定位入口。</summary>
    public sealed class FindDoeProjectTool : IAgentTool
    {
        private readonly AgentToolContext _ctx;
        public FindDoeProjectTool(AgentToolContext ctx) => _ctx = ctx;

        public string Name => "find_doe_project";
        public string DisplayName => "查找 DOE 项目";
        public string Description => @"按名字(可模糊)或项目编号在数据库全量检索 DOE 优化项目:项目阶段/状态/累计实验/最优值,以及项目下每一轮批次的状态与结果。
用户提到「某某项目」要查找、查看、分析时先用本工具定位;project_name 省略时列出全部项目清单(含项目编号)。
库里存在重名项目:名字有歧义时工具会返回带编号的候选表,此后一律用编号(如 PROJ_20250101_abc123)当 project_name 精确选择,不要用同一个名字原样重试;所有接受 project_name 的工具都认编号。
找到目标轮次后:分析用 analyze_doe_batch(传 project_name+round_number),继续执行用 execute_doe_batch。";
        public string ParametersSchema => @"{
  ""type"":""object"",
  ""properties"":{
    ""project_name"":{""type"":""string"",""description"":""项目名(可模糊)或项目编号(PROJ_ 开头,重名时用编号);省略则列出全部项目""}
  }
}";
        public bool RequiresConfirmation => false;
        public string DescribeAction(JsonElement args) => "查找 DOE 项目";

        public async Task<AgentToolResult> ExecuteAsync(JsonElement args)
        {
            var repo = _ctx.Resolve<IDOERepository>();
            var projects = await repo.GetAllProjectSummariesAsync(50).ConfigureAwait(false)
                           ?? new List<DOEProjectSummary>();
            if (projects.Count == 0)
                return AgentToolResult.Ok("数据库里还没有任何 DOE 优化项目。");

            string name = AgentToolContext.GetString(args, "project_name");

            // 省略名字:全部项目清单
            if (string.IsNullOrWhiteSpace(name))
            {
                var sb0 = new StringBuilder();
                sb0.AppendLine($"全部 DOE 项目(共 {projects.Count} 个,重名项目请用编号区分):");
                sb0.AppendLine();
                sb0.AppendLine("| 项目 | 项目编号 | 阶段 | 状态 | 轮次 | 累计实验 | 最优值 | 更新时间 |");
                sb0.AppendLine("| --- | --- | --- | --- | --- | --- | --- | --- |");
                foreach (var p in projects.Take(20))
                    sb0.AppendLine($"| {p.ProjectName} | {p.ProjectId} | {p.PhaseText} | {StatusText(p.Status)} | {p.TotalBatches} | {p.TotalExperiments} | " +
                                   $"{(p.BestResponseValue.HasValue ? p.BestResponseValue.Value.ToString("0.####") : "-")} | {p.UpdatedTime:MM-dd HH:mm} |");
                return AgentToolResult.Ok(sb0.ToString());
            }

            // 名字/编号解析统一走共用入口(编号精确寻址、精确同名优先、歧义带编号候选表)
            var (proj, findErr) = await DoeAnalysisHelper.FindProjectAsync(repo, name).ConfigureAwait(false);
            if (proj == null) return AgentToolResult.Fail(findErr);

            var sb = new StringBuilder();
            sb.AppendLine($"项目「{proj.ProjectName}」(编号 {proj.ProjectId}):阶段 {proj.PhaseText},状态 {StatusText(proj.Status)}," +
                          $"累计 {proj.TotalBatches} 轮 {proj.TotalExperiments} 组实验" +
                          (proj.BestResponseValue.HasValue ? $",历史最优 {proj.BestResponseValue.Value:0.####}" : "") + "。");
            sb.AppendLine();

            var batches = await repo.GetBatchesByProjectAsync(proj.ProjectId).ConfigureAwait(false)
                          ?? new List<DOEBatch>();
            if (batches.Count == 0)
            {
                sb.Append("该项目下还没有批次。");
                return AgentToolResult.Ok(sb.ToString());
            }

            sb.AppendLine("| 轮次 | 批次 | 方法 | 状态 | 完成/失败/总组 | 最优响应 |");
            sb.AppendLine("| --- | --- | --- | --- | --- | --- |");
            foreach (var b in batches.OrderBy(x => x.RoundNumber ?? 0).ThenBy(x => x.CreatedTime))
            {
                int total = 0, comp = 0, fail = 0;
                string best = "-";
                try
                {
                    var d = await repo.GetBatchWithDetailsAsync(b.BatchId).ConfigureAwait(false);
                    if (d != null)
                    {
                        var active = d.Runs.Where(r => r.Status != DOERunStatus.Skipped).ToList();
                        total = active.Count;
                        comp = active.Count(r => r.Status == DOERunStatus.Completed);
                        fail = active.Count(r => r.Status == DOERunStatus.Failed);

                        string respName = d.Responses?.FirstOrDefault()?.ResponseName;
                        if (!string.IsNullOrEmpty(respName) && comp > 0)
                        {
                            bool minimize = await DoeAnalysisHelper.IsMinimizeAsync(repo, b.BatchId, respName).ConfigureAwait(false);
                            double? bestVal = null;
                            foreach (var run in d.Runs.Where(r => r.Status == DOERunStatus.Completed))
                            {
                                var resp = DoeAnalysisHelper.ParseNumbers(run.ResponseValuesJson);
                                if (!resp.TryGetValue(respName, out double v)) continue;
                                if (bestVal == null || (minimize ? v < bestVal : v > bestVal)) bestVal = v;
                            }
                            if (bestVal != null) best = $"{respName}={bestVal:0.####}";
                        }
                    }
                }
                catch { }
                sb.AppendLine($"| 第 {b.RoundNumber?.ToString() ?? "-"} 轮 | {b.BatchName} | {b.DesignMethod} | {BatchStatusText(b.Status)} | {comp}/{fail}/{total} | {best} |");
            }
            sb.AppendLine();
            sb.Append("要分析某一轮,用 analyze_doe_batch 传 project_name + round_number(用户说第几轮就传几);" +
                      "多轮一起对比用 analyze_doe_project;继续执行用 execute_doe_batch。");
            return AgentToolResult.Ok(sb.ToString());
        }

        private static string StatusText(DOEProjectStatus s) => s switch
        {
            DOEProjectStatus.Active => "进行中",
            DOEProjectStatus.Paused => "已暂停",
            DOEProjectStatus.Completed => "已完成",
            DOEProjectStatus.Archived => "已归档",
            _ => s.ToString()
        };

        private static string BatchStatusText(DOEBatchStatus s) => s switch
        {
            DOEBatchStatus.Designing => "设计中",
            DOEBatchStatus.Ready => "就绪",
            DOEBatchStatus.Running => "执行中",
            DOEBatchStatus.Paused => "已暂停",
            DOEBatchStatus.Completed => "已完成",
            DOEBatchStatus.Aborted => "已中止",
            _ => s.ToString()
        };
    }

    /// <summary>查询实验历史(只读):最近批次的完成情况与最优结果汇总。</summary>
    public sealed class QueryExperimentHistoryTool : IAgentTool
    {
        private readonly AgentToolContext _ctx;
        public QueryExperimentHistoryTool(AgentToolContext ctx) => _ctx = ctx;

        public string Name => "query_experiment_history";
        public string DisplayName => "查询实验历史";
        public string Description => @"查询最近的 DOE 实验批次历史:名称、设计方法、状态、完成组数、最优响应值、创建时间,可按关键字过滤批次名。
用户问「最近做了哪些实验」「上周跑了几批」「历史最好结果」时用本工具;按「项目」找请改用 find_doe_project(全库检索项目与各轮)。返回的表格可直接展示给用户。";
        public string ParametersSchema => @"{
  ""type"":""object"",
  ""properties"":{
    ""keyword"":{""type"":""string"",""description"":""批次名关键字过滤;省略则不过滤""},
    ""days"":{""type"":""number"",""description"":""只看最近多少天;省略则不限""},
    ""limit"":{""type"":""number"",""description"":""最多返回条数,默认 10""}
  }
}";
        public bool RequiresConfirmation => false;
        public string DescribeAction(JsonElement args) => "查询实验历史";

        public async Task<AgentToolResult> ExecuteAsync(JsonElement args)
        {
            var repo = _ctx.Resolve<IDOERepository>();
            var recent = await repo.GetRecentBatchesAsync(50).ConfigureAwait(false) ?? new List<DOEBatch>();

            string keyword = AgentToolContext.GetString(args, "keyword");
            if (!string.IsNullOrWhiteSpace(keyword))
                recent = recent
                    .Where(b => DoeBatchFinder.NameContains(b.BatchName, keyword))
                    .ToList();

            double? days = AgentToolContext.GetNumber(args, "days");
            if (days.HasValue && days.Value > 0)
            {
                var cutoff = DateTime.Now.AddDays(-days.Value);
                recent = recent.Where(b => b.CreatedTime >= cutoff).ToList();
            }
            int limit = (int)(AgentToolContext.GetNumber(args, "limit") ?? 10);
            limit = Math.Max(1, Math.Min(limit, 20));

            if (recent.Count == 0)
                return AgentToolResult.Ok(
                    "指定条件内没有实验批次。" +
                    (string.IsNullOrWhiteSpace(keyword) ? "" : "若找的是项目名,请改用 find_doe_project 全库检索。"));

            var sb = new StringBuilder();
            sb.AppendLine($"最近实验批次(共 {recent.Count} 个,显示前 {Math.Min(limit, recent.Count)} 个):");
            sb.AppendLine();
            sb.AppendLine("| 批次 | 方法 | 状态 | 完成/总组 | 最优响应 | 创建时间 |");
            sb.AppendLine("| --- | --- | --- | --- | --- | --- |");

            foreach (var b in recent.Take(limit))
            {
                int total = 0, doneCount = 0;
                string best = "-";
                try
                {
                    var detail = await repo.GetBatchWithDetailsAsync(b.BatchId).ConfigureAwait(false);
                    if (detail != null)
                    {
                        var active = detail.Runs.Where(r => r.Status != DOERunStatus.Skipped).ToList();
                        total = active.Count;
                        doneCount = active.Count(r => r.Status == DOERunStatus.Completed);

                        string respName = detail.Responses?.FirstOrDefault()?.ResponseName;
                        if (!string.IsNullOrEmpty(respName) && doneCount > 0)
                        {
                            bool minimize = await DoeAnalysisHelper.IsMinimizeAsync(repo, b.BatchId, respName).ConfigureAwait(false);
                            double? bestVal = null;
                            foreach (var run in detail.Runs.Where(r => r.Status == DOERunStatus.Completed))
                            {
                                var resp = DoeAnalysisHelper.ParseNumbers(run.ResponseValuesJson);
                                if (!resp.TryGetValue(respName, out double v)) continue;
                                if (bestVal == null || (minimize ? v < bestVal : v > bestVal)) bestVal = v;
                            }
                            if (bestVal != null) best = $"{respName}={bestVal:0.####}";
                        }
                    }
                }
                catch { }

                sb.AppendLine($"| {b.BatchName} | {b.DesignMethod} | {StatusText(b.Status)} | {doneCount}/{total} | {best} | {b.CreatedTime:MM-dd HH:mm} |");
            }

            sb.AppendLine();
            sb.Append("需要深入分析某个批次(显著因子/模型质量/趋势图),可用 analyze_doe_batch。");
            return AgentToolResult.Ok(sb.ToString());
        }

        private static string StatusText(DOEBatchStatus s) => s switch
        {
            DOEBatchStatus.Designing => "设计中",
            DOEBatchStatus.Ready => "就绪",
            DOEBatchStatus.Running => "执行中",
            DOEBatchStatus.Paused => "已暂停",
            DOEBatchStatus.Completed => "已完成",
            DOEBatchStatus.Aborted => "已中止",
            _ => s.ToString()
        };
    }
}

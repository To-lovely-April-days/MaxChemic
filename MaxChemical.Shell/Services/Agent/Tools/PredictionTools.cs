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
    /// <summary>
    /// 模型预测类工具共用:批次定位、模型质量分级(规则裁判,AI 只转述)、最优实测查找。
    ///
    /// 质量闸门是硬规则,不由 AI 自由裁量:
    ///   不可靠(拒绝给数值):调整R² &lt; 0.5,或模型整体 P &gt; 0.10;
    ///   谨慎(给数值但降级提示):调整R² &lt; 0.8,或预测R² &lt; 0.5,或失拟显著(P &lt; 0.05);
    ///   可靠:其余情况。
    /// </summary>
    internal static class PredictionHelper
    {
        public enum ModelTier { Reliable, Caution, Unreliable }

        /// <summary>与 analyze_doe_batch 相同的三种定位方式:项目+轮号 / 批次名(可模糊)/ 全省略。</summary>
        public static async Task<(DOEBatch Batch, string Error)> LocateAsync(IDOERepository repo, JsonElement args)
        {
            DOEBatch found;
            string err;
            double? round = AgentToolContext.GetNumber(args, "round_number");
            if (round.HasValue)
            {
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
            if (found == null) return (null, err);

            var batch = found.Runs.Count > 0
                ? found
                : await repo.GetBatchWithDetailsAsync(found.BatchId).ConfigureAwait(false);
            return batch == null ? (null, "批次数据读取失败。") : (batch, null);
        }

        /// <summary>与 analyze_doe_batch 相同的模型类型选择:RSM 类设计用二次模型,筛选类用线性。</summary>
        public static string PickModelType(DOEBatch batch)
            => batch.DesignMethod is DOEDesignMethod.CCD or DOEDesignMethod.BoxBehnken
                or DOEDesignMethod.DOptimal or DOEDesignMethod.AugmentedDesign
                ? "quadratic" : "linear";

        /// <summary>
        /// 拟合是否真实成功:分析服务内部 catch 异常后会返回空壳结果(Coefficients 为空、
        /// InestimableWarning 带引擎报错),不甄别会把「引擎失败」误报成「模型不可靠」。
        /// 返回 null 表示成功,否则返回应转告的失败原因。
        /// </summary>
        public static string FitFailure(OLSAnalysisResult ols)
        {
            if (ols?.ModelSummary == null) return "分析引擎未返回模型。";
            if (ols.Coefficients == null || ols.Coefficients.Count == 0)
                return "模型拟合失败" +
                       (string.IsNullOrWhiteSpace(ols.InestimableWarning) ? "(引擎未给出原因)" : $":{ols.InestimableWarning}") +
                       "。常见原因是数据点太少或共线,可尝试 model_type=linear 降阶,或先用 analyze_doe_batch 检查数据。";
            return null;
        }

        /// <summary>模型质量分级(硬规则)。返回级别与依据说明。</summary>
        public static (ModelTier Tier, List<string> Notes) JudgeModel(OLSAnalysisResult ols)
        {
            var s = ols.ModelSummary;
            var notes = new List<string>();

            double r2adj = s.RSquaredAdj;
            if (double.IsNaN(r2adj) || double.IsInfinity(r2adj)) r2adj = s.RSquared;

            if (!double.IsNaN(r2adj) && r2adj < 0.5)
            {
                notes.Add($"调整R² = {r2adj:F3},低于 0.5,模型解释力不足");
                return (ModelTier.Unreliable, notes);
            }
            if (s.ModelP.HasValue && s.ModelP.Value > 0.10)
            {
                notes.Add($"模型整体 P = {s.ModelP.Value:F4},大于 0.10,回归关系不显著");
                return (ModelTier.Unreliable, notes);
            }

            var tier = ModelTier.Reliable;
            if (!double.IsNaN(r2adj) && r2adj < 0.8)
            {
                notes.Add($"调整R² = {r2adj:F3},在 0.5~0.8 之间,拟合一般");
                tier = ModelTier.Caution;
            }
            if (!double.IsNaN(s.RSquaredPred) && s.RSquaredPred < 0.5 && s.RSquaredPred != 0)
            {
                notes.Add($"预测R² = {s.RSquaredPred:F3},低于 0.5,对新点的预测能力偏弱");
                tier = ModelTier.Caution;
            }
            if (s.LackOfFitP.HasValue && s.LackOfFitP.Value < 0.05)
            {
                notes.Add($"失拟检验 P = {s.LackOfFitP.Value:F4},失拟显著,当前模型形式可能遗漏了弯曲或交互");
                tier = ModelTier.Caution;
            }
            if (notes.Count == 0)
                notes.Add($"调整R² = {r2adj:F3},模型整体显著,无失拟证据");
            return (tier, notes);
        }

        public static string TierText(ModelTier t) => t switch
        {
            ModelTier.Reliable => "可靠",
            ModelTier.Caution => "仅供参考(模型质量一般)",
            _ => "不可靠"
        };

        /// <summary>模型不可靠时的统一拒绝话术:如实给诊断和补救路径,不给预测数值。</summary>
        public static string UnreliableAdvice(DOEBatch batch, string respName, List<string> notes)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"基于批次「{batch.BatchName}」现有数据拟合的 {respName} 模型不可靠,按质量规则拒绝给出预测数值(给了也是误导):");
            foreach (var n in notes) sb.AppendLine($"- {n}");
            sb.AppendLine();
            sb.AppendLine("请把以上诊断如实告诉用户,并给出补救路径(按优先级):");
            sb.AppendLine("1. 用 analyze_doe_batch 检查是否有异常组拉低拟合(响应录入错误、执行失败但记了数);");
            sb.AppendLine("2. 补做中心点重复实验以估计纯误差,或补做失败组;");
            sb.AppendLine("3. 若因子筛选阶段本就噪声大,建议按决策闭环进入下一轮(缩范围或增强设计)后再做预测;");
            sb.AppendLine("4. 可尝试指定 model_type=linear 降阶重试(高阶项数据不足时二次模型会过拟合)。");
            return sb.ToString();
        }

        /// <summary>已完成组里按优化方向找实测最优。</summary>
        public static (double? Best, int RunIndex) FindBestRun(List<DOERunRecord> done, string respName, bool minimize)
        {
            double? best = null;
            int idx = -1;
            foreach (var r in done)
            {
                var vals = DoeAnalysisHelper.ParseNumbers(r.ResponseValuesJson);
                if (!vals.TryGetValue(respName, out double v)) continue;
                if (best == null || (minimize ? v < best.Value : v > best.Value)) { best = v; idx = r.RunIndex; }
            }
            return (best, idx);
        }

        /// <summary>从 profiler JSON 里取单点预测(current_predicted 及其置信区间)。</summary>
        public static (bool Ok, double Pred, double CiLo, double CiHi) ParsePoint(string profilerJson)
        {
            try
            {
                using var doc = JsonDocument.Parse(profilerJson);
                var root = doc.RootElement;
                if (!root.TryGetProperty("current_predicted", out var p) || p.ValueKind != JsonValueKind.Number)
                    return (false, 0, 0, 0);
                double pred = p.GetDouble();
                double lo = root.TryGetProperty("current_ci_lower", out var l) && l.ValueKind == JsonValueKind.Number ? l.GetDouble() : pred;
                double hi = root.TryGetProperty("current_ci_upper", out var h) && h.ValueKind == JsonValueKind.Number ? h.GetDouble() : pred;
                return (true, pred, lo, hi);
            }
            catch { return (false, 0, 0, 0); }
        }

        public static string Fmt(double v) => Math.Abs(v) >= 1000 ? v.ToString("F1") : v.ToString("F4").TrimEnd('0').TrimEnd('.');
    }

    /// <summary>
    /// What-if 预测:用户给一组因子条件,用已拟合的回归模型算响应预测值与区间(只读,不做实验)。
    /// </summary>
    public sealed class PredictResponseTool : IAgentTool
    {
        private readonly AgentToolContext _ctx;
        private readonly ILogService _logger = LogManager.GetLogger<PredictResponseTool>();
        public PredictResponseTool(AgentToolContext ctx) => _ctx = ctx;

        public string Name => "predict_response";
        public string DisplayName => "模型预测(What-if)";
        public string Description => @"用户问「如果温度 80、浓度 1.2,产率大概多少」这类 What-if 问题时用本工具:基于该批次已完成实验拟合的 OLS 回归模型,计算给定因子条件下的响应预测值、95% 置信区间与单次实验预测区间,并自动与当前实测最优对比。
定位方式与 analyze_doe_batch 相同(project_name+round_number / batch_name / 全省略取最近有数据批次)。
内置质量规则:数据不足或模型不可靠(调整R²<0.5 或模型P>0.10)时会拒绝给数值并返回诊断与补救建议,要如实转告用户;超出建模范围 20% 以上的条件同样拒绝(模型无数据支撑);轻度外推会给出降级警告。严禁绕过本工具自己按方程心算。";
        public string ParametersSchema => @"{
  ""type"":""object"",
  ""properties"":{
    ""batch_name"":{""type"":""string"",""description"":""批次名或项目名(可模糊)""},
    ""project_name"":{""type"":""string"",""description"":""项目名,配合 round_number""},
    ""round_number"":{""type"":""number"",""description"":""项目内轮号(用户说第 N 轮时传 N)""},
    ""response_name"":{""type"":""string"",""description"":""要预测的响应名;省略取第一个响应""},
    ""factor_values"":{""type"":""object"",""description"":""因子条件,键为因子名、值为数值(类别因子传水平字符串);缺省的因子按范围中心值代入并在结果中注明"",""additionalProperties"":true}
  },
  ""required"":[""factor_values""]
}";
        public bool RequiresConfirmation => false;
        public string DescribeAction(JsonElement args) => "用回归模型预测给定条件下的响应";

        public async Task<AgentToolResult> ExecuteAsync(JsonElement args)
        {
            var repo = _ctx.Resolve<IDOERepository>();
            var (batch, err) = await PredictionHelper.LocateAsync(repo, args).ConfigureAwait(false);
            if (batch == null) return AgentToolResult.Fail(err);

            var done = batch.Runs.Where(r => r.Status == DOERunStatus.Completed).OrderBy(r => r.RunIndex).ToList();
            if (done.Count == 0)
                return AgentToolResult.Fail(
                    $"批次「{batch.BatchName}」还没有任何已完成的实验组,没有数据就没有模型,无法做预测。" +
                    "请如实告诉用户:需要先执行实验产生数据,才能启用模型预测;可建议先执行该批次。");
            if (done.Count < 3)
                return AgentToolResult.Fail(
                    $"批次「{batch.BatchName}」只有 {done.Count} 组完成数据,不足以拟合模型(至少 3 组)。请告知用户先补做实验。");

            // 响应名
            string respName = AgentToolContext.GetString(args, "response_name");
            var respNames = batch.Responses?.Select(r => r.ResponseName).ToList() ?? new List<string>();
            if (string.IsNullOrWhiteSpace(respName)) respName = respNames.FirstOrDefault();
            else if (!respNames.Contains(respName))
                return AgentToolResult.Fail($"批次没有响应「{respName}」。可选:{string.Join("、", respNames)}");
            if (string.IsNullOrEmpty(respName))
                return AgentToolResult.Fail("批次没有定义响应变量,无法预测。");

            // 因子条件解析与校验
            if (!args.TryGetProperty("factor_values", out var fv) || fv.ValueKind != JsonValueKind.Object)
                return AgentToolResult.Fail("factor_values 必须是对象,键为因子名、值为数值。");

            var factors = batch.Factors ?? new List<DOEFactor>();
            if (factors.Count == 0) return AgentToolResult.Fail("批次没有因子定义,无法预测。");

            var given = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in fv.EnumerateObject()) given[p.Name] = p.Value;

            // 用户给的名字必须能对上批次因子(先精确,再唯一包含)
            var resolved = new Dictionary<string, JsonElement>(); // 实际因子名 -> 输入值
            foreach (var kv in given)
            {
                var exact = factors.FirstOrDefault(f => string.Equals(f.FactorName, kv.Key, StringComparison.OrdinalIgnoreCase));
                if (exact == null)
                {
                    var contains = factors.Where(f =>
                        (f.FactorName?.IndexOf(kv.Key, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0).ToList();
                    if (contains.Count == 1) exact = contains[0];
                }
                if (exact == null)
                    return AgentToolResult.Fail(
                        $"因子「{kv.Key}」不在批次「{batch.BatchName}」的因子里。该批次因子:{string.Join("、", factors.Select(f => f.FactorName))}");
                resolved[exact.FactorName] = kv.Value;
            }

            // 组装完整条件:缺省因子取中心值(类别取第一水平);逐因子做范围/外推判定
            var fixedValues = new Dictionary<string, object>();
            var rows = new List<string>();   // 条件表行
            var warns = new List<string>();
            foreach (var f in factors.OrderBy(f => f.SortOrder))
            {
                if (f.IsCategorical)
                {
                    var levels = f.GetCategoryLevelList();
                    string lv;
                    if (resolved.TryGetValue(f.FactorName, out var el))
                    {
                        lv = el.ValueKind == JsonValueKind.String ? el.GetString() : el.ToString();
                        if (levels.Count > 0 && !levels.Contains(lv))
                            return AgentToolResult.Fail(
                                $"类别因子「{f.FactorName}」没有水平「{lv}」。可选水平:{string.Join("、", levels)}");
                        rows.Add($"| {f.FactorName} | {lv} | {string.Join("/", levels)} | 指定水平 |");
                    }
                    else
                    {
                        lv = levels.FirstOrDefault() ?? "";
                        rows.Add($"| {f.FactorName} | {lv} | {string.Join("/", levels)} | 默认第一水平 |");
                        warns.Add($"类别因子 {f.FactorName} 未指定,按第一水平「{lv}」代入");
                    }
                    fixedValues[f.FactorName] = lv;
                }
                else
                {
                    double v;
                    string status;
                    if (resolved.TryGetValue(f.FactorName, out var el))
                    {
                        if (el.ValueKind != JsonValueKind.Number || !el.TryGetDouble(out v))
                            return AgentToolResult.Fail($"因子「{f.FactorName}」的值必须是数值。");

                        double range = f.UpperBound - f.LowerBound;
                        if (range <= 0) range = Math.Max(Math.Abs(f.UpperBound), 1e-9);
                        double over = Math.Max(f.LowerBound - v, v - f.UpperBound) / range;
                        if (over > 0.2)
                            return AgentToolResult.Fail(
                                $"因子「{f.FactorName}」= {PredictionHelper.Fmt(v)} 超出建模范围 [{PredictionHelper.Fmt(f.LowerBound)}, {PredictionHelper.Fmt(f.UpperBound)}] 达 {over:P0}," +
                                "模型在该区域完全没有实验数据支撑,拒绝预测。请如实告知用户,并建议:要么把条件收回建模范围内,要么通过决策闭环做扩范围实验后再问。");
                        if (over > 0)
                        {
                            status = $"外推 {over:P0},可信度下降";
                            warns.Add($"{f.FactorName} = {PredictionHelper.Fmt(v)} 在建模范围外(外推 {over:P0}),该方向预测可信度下降");
                        }
                        else status = "范围内";
                    }
                    else
                    {
                        v = f.CenterPoint;
                        status = "默认中心值";
                        warns.Add($"因子 {f.FactorName} 未指定,按中心值 {PredictionHelper.Fmt(v)} 代入");
                    }
                    rows.Add($"| {f.FactorName} | {PredictionHelper.Fmt(v)} | [{PredictionHelper.Fmt(f.LowerBound)}, {PredictionHelper.Fmt(f.UpperBound)}] | {status} |");
                    fixedValues[f.FactorName] = v;
                }
            }

            // 拟合 + 质量闸门
            var svc = _ctx.Resolve<IDOEAnalysisService>();
            string modelType = PredictionHelper.PickModelType(batch);
            OLSAnalysisResult ols;
            try
            {
                ols = await svc.FitOlsAsync(batch.BatchId, respName, modelType).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"预测前拟合失败: {ex.Message}");
                return AgentToolResult.Fail($"模型拟合失败:{ex.Message}。可让用户先用 analyze_doe_batch 检查数据。");
            }
            string fitErr = PredictionHelper.FitFailure(ols);
            if (fitErr != null) return AgentToolResult.Fail(fitErr);

            var (tier, notes) = PredictionHelper.JudgeModel(ols);
            if (tier == PredictionHelper.ModelTier.Unreliable)
                return AgentToolResult.Ok(PredictionHelper.UnreliableAdvice(batch, respName, notes));

            // 单点预测(profiler 的 current_* 即该固定条件下的预测与均值置信区间)
            string profJson;
            try
            {
                profJson = await svc.GetPredictionProfilerAsync(batch.BatchId, respName, 5, fixedValues).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return AgentToolResult.Fail($"预测计算失败:{ex.Message}");
            }
            var (ok, pred, ciLo, ciHi) = PredictionHelper.ParsePoint(profJson);
            if (!ok) return AgentToolResult.Fail("预测引擎未返回结果,请检查批次数据完整性。");

            // 单次实验预测区间:在均值区间基础上叠加残差方差
            double tCrit = ols.TCrit > 0 ? ols.TCrit : 2.0;
            double sigma = ols.Sigma > 0 ? ols.Sigma : ols.ModelSummary.RMSE;
            double se = tCrit > 0 ? (ciHi - pred) / tCrit : 0;
            double piHalf = tCrit * Math.Sqrt(sigma * sigma + se * se);

            bool minimize = await DoeAnalysisHelper.IsMinimizeAsync(repo, batch.BatchId, respName).ConfigureAwait(false);
            var (best, bestIdx) = PredictionHelper.FindBestRun(done, respName, minimize);

            var sb = new StringBuilder();
            sb.AppendLine($"What-if 预测完成(批次「{batch.BatchName}」,{modelType} 模型,基于 {done.Count} 组完成数据):");
            sb.AppendLine();
            sb.AppendLine("| 因子 | 代入值 | 建模范围 | 状态 |");
            sb.AppendLine("|---|---|---|---|");
            foreach (var r in rows) sb.AppendLine(r);
            sb.AppendLine();
            sb.AppendLine($"预测结果:{respName} = {PredictionHelper.Fmt(pred)}");
            sb.AppendLine($"- 均值 95% 置信区间:[{PredictionHelper.Fmt(ciLo)}, {PredictionHelper.Fmt(ciHi)}]");
            sb.AppendLine($"- 单次实验 95% 预测区间:[{PredictionHelper.Fmt(pred - piHalf)}, {PredictionHelper.Fmt(pred + piHalf)}](单次实验含随机误差,区间更宽)");
            sb.AppendLine($"- 模型可信度:{PredictionHelper.TierText(tier)}({string.Join(";", notes)})");

            if (best.HasValue)
            {
                double delta = pred - best.Value;
                bool better = minimize ? delta < 0 : delta > 0;
                string pct = Math.Abs(best.Value) > 1e-9 ? $"({delta / Math.Abs(best.Value):+0.0%;-0.0%})" : "";
                sb.AppendLine($"- 对比实测最优:当前最优 {respName} = {PredictionHelper.Fmt(best.Value)}(第 {bestIdx} 组),该条件预测{(better ? "优于" : "不及")}最优 {PredictionHelper.Fmt(Math.Abs(delta))} {pct}");
                if (better)
                    sb.AppendLine("- 预测优于当前实测最优:建议向用户提出安排验证实验(项目批次可走决策闭环的验证轮)。");
            }
            if (warns.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("注意事项:" + string.Join(";", warns) + "。");
            }
            sb.AppendLine();
            sb.AppendLine("以上是模型推算,不是实测值,回复用户时要讲清这一点;预测区间和可信度要一并转达,不要只报单个数。");
            return AgentToolResult.Ok(sb.ToString());
        }
    }

    /// <summary>
    /// 反向寻优:在建模范围内搜索使响应最大/最小/达到目标值的因子条件(只读,不做实验)。
    /// </summary>
    public sealed class SuggestOptimalConditionsTool : IAgentTool
    {
        private readonly AgentToolContext _ctx;
        private readonly ILogService _logger = LogManager.GetLogger<SuggestOptimalConditionsTool>();
        public SuggestOptimalConditionsTool(AgentToolContext ctx) => _ctx = ctx;

        public string Name => "suggest_optimal_conditions";
        public string DisplayName => "反向寻优(问条件)";
        public string Description => @"用户问「想让产率到 95% 条件怎么配」「模型认为最优条件是什么」时用本工具:在已建模的因子范围内用回归模型搜索,给出推荐条件、预测值与置信区间。
goal 三选一:maximize / minimize / target(target 必须带 target_value);省略 goal 按该响应的优化方向自动选。定位方式与 analyze_doe_batch 相同。
内置规则:模型不可靠时拒绝推荐并给诊断;目标值超出建模范围可达区间时,如实报告不可达、给最接近的可达值,并建议扩范围;推荐点落在范围边界时提示真正最优可能在范围外。搜索永不越出建模范围,推荐一律是范围内条件。严禁绕过本工具自己推算条件。";
        public string ParametersSchema => @"{
  ""type"":""object"",
  ""properties"":{
    ""batch_name"":{""type"":""string"",""description"":""批次名或项目名(可模糊)""},
    ""project_name"":{""type"":""string"",""description"":""项目名,配合 round_number""},
    ""round_number"":{""type"":""number"",""description"":""项目内轮号""},
    ""response_name"":{""type"":""string"",""description"":""目标响应名;省略取第一个响应""},
    ""goal"":{""type"":""string"",""enum"":[""maximize"",""minimize"",""target""],""description"":""寻优方向;省略按该响应配置的优化方向""},
    ""target_value"":{""type"":""number"",""description"":""goal=target 时的目标响应值""}
  }
}";
        public bool RequiresConfirmation => false;
        public string DescribeAction(JsonElement args) => "用回归模型反向搜索推荐实验条件";

        public async Task<AgentToolResult> ExecuteAsync(JsonElement args)
        {
            var repo = _ctx.Resolve<IDOERepository>();
            var (batch, err) = await PredictionHelper.LocateAsync(repo, args).ConfigureAwait(false);
            if (batch == null) return AgentToolResult.Fail(err);

            var done = batch.Runs.Where(r => r.Status == DOERunStatus.Completed).OrderBy(r => r.RunIndex).ToList();
            if (done.Count == 0)
                return AgentToolResult.Fail(
                    $"批次「{batch.BatchName}」还没有已完成的实验组,没有数据就没有模型,无法反推条件。请告知用户先执行实验。");
            if (done.Count < 3)
                return AgentToolResult.Fail(
                    $"批次「{batch.BatchName}」只有 {done.Count} 组完成数据,不足以拟合模型(至少 3 组)。请告知用户先补做实验。");

            string respName = AgentToolContext.GetString(args, "response_name");
            var respNames = batch.Responses?.Select(r => r.ResponseName).ToList() ?? new List<string>();
            if (string.IsNullOrWhiteSpace(respName)) respName = respNames.FirstOrDefault();
            else if (!respNames.Contains(respName))
                return AgentToolResult.Fail($"批次没有响应「{respName}」。可选:{string.Join("、", respNames)}");
            if (string.IsNullOrEmpty(respName))
                return AgentToolResult.Fail("批次没有定义响应变量,无法寻优。");

            bool minimize = await DoeAnalysisHelper.IsMinimizeAsync(repo, batch.BatchId, respName).ConfigureAwait(false);
            string goal = (AgentToolContext.GetString(args, "goal") ?? (minimize ? "minimize" : "maximize")).ToLowerInvariant();
            double? target = AgentToolContext.GetNumber(args, "target_value");
            if (goal == "target" && !target.HasValue)
                return AgentToolResult.Fail("goal=target 时必须传 target_value(用户想达到的响应值)。");

            // 拟合 + 质量闸门
            var svc = _ctx.Resolve<IDOEAnalysisService>();
            string modelType = PredictionHelper.PickModelType(batch);
            OLSAnalysisResult ols;
            try
            {
                ols = await svc.FitOlsAsync(batch.BatchId, respName, modelType).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"寻优前拟合失败: {ex.Message}");
                return AgentToolResult.Fail($"模型拟合失败:{ex.Message}。可让用户先用 analyze_doe_batch 检查数据。");
            }
            string fitErr = PredictionHelper.FitFailure(ols);
            if (fitErr != null) return AgentToolResult.Fail(fitErr);

            var (tier, notes) = PredictionHelper.JudgeModel(ols);
            if (tier == PredictionHelper.ModelTier.Unreliable)
                return AgentToolResult.Ok(PredictionHelper.UnreliableAdvice(batch, respName, notes));

            // 范围内网格寻优(FindOptimal 永不越界,推荐一律是范围内条件)
            var maxPoint = await SearchAsync(svc, batch.BatchId, respName, maximize: true).ConfigureAwait(false);
            var minPoint = await SearchAsync(svc, batch.BatchId, respName, maximize: false).ConfigureAwait(false);
            if (maxPoint == null || minPoint == null)
                return AgentToolResult.Fail("寻优引擎未返回结果,请检查批次数据完整性。");

            Dictionary<string, object> recommend;
            double predicted;
            string targetNote = null;

            if (goal == "target")
            {
                double pmax = maxPoint.Value.Predicted, pmin = minPoint.Value.Predicted;
                double tol = Math.Max(ols.Sigma > 0 ? ols.Sigma : ols.ModelSummary.RMSE, 1e-9) * 0.1;
                if (target.Value > pmax + tol || target.Value < pmin - tol)
                {
                    // 不可达:如实报告 + 最接近的可达点
                    bool nearMax = Math.Abs(target.Value - pmax) <= Math.Abs(target.Value - pmin);
                    var near = nearMax ? maxPoint.Value : minPoint.Value;
                    var sbNa = new StringBuilder();
                    sbNa.AppendLine($"目标 {respName} = {PredictionHelper.Fmt(target.Value)} 在当前建模范围内不可达:");
                    sbNa.AppendLine($"- 模型在建模范围内的可达区间约为 [{PredictionHelper.Fmt(pmin)}, {PredictionHelper.Fmt(pmax)}],与目标差 {PredictionHelper.Fmt(Math.Abs(target.Value - (nearMax ? pmax : pmin)))}。");
                    sbNa.AppendLine($"- 最接近目标的范围内条件如下(预测 {respName} = {PredictionHelper.Fmt(near.Predicted)}):");
                    sbNa.AppendLine(ConditionTable(batch, near.Point));
                    sbNa.AppendLine("请如实告诉用户目标当前不可达,并给两条路:①接受最接近的可达值;②沿趋势方向扩大因子范围做下一轮实验(项目批次可走决策闭环的扩范围动作),扩范围后模型才有依据回答该目标。不要为了迎合目标编造范围外的条件。");
                    return AgentToolResult.Ok(sbNa.ToString());
                }

                // 可达:从最优点出发,单因子回调到目标(改动最小、最可执行的方案)
                var refined = await RefineToTargetAsync(svc, batch, respName, maxPoint.Value.Point, target.Value).ConfigureAwait(false);
                recommend = refined.Point;
                predicted = refined.Predicted;
                targetNote = refined.Note;
            }
            else
            {
                bool maximize = goal != "minimize";
                var opt = maximize ? maxPoint.Value : minPoint.Value;
                recommend = opt.Point;
                predicted = opt.Predicted;
            }

            // 推荐点的置信区间
            double ciLo = predicted, ciHi = predicted;
            try
            {
                string pj = await svc.GetPredictionProfilerAsync(batch.BatchId, respName, 5, recommend).ConfigureAwait(false);
                var pt = PredictionHelper.ParsePoint(pj);
                if (pt.Ok) { predicted = pt.Pred; ciLo = pt.CiLo; ciHi = pt.CiHi; }
            }
            catch { }

            // 边界检测:推荐值贴着范围边界说明真正最优可能在范围外
            var boundary = new List<string>();
            foreach (var f in (batch.Factors ?? new List<DOEFactor>()).Where(f => f.IsContinuous))
            {
                if (!recommend.TryGetValue(f.FactorName, out var ov) || ov is not double dv) continue;
                double step = (f.UpperBound - f.LowerBound) / 19.0;
                if (Math.Abs(dv - f.LowerBound) < step * 0.51) boundary.Add($"{f.FactorName}(贴下限)");
                else if (Math.Abs(dv - f.UpperBound) < step * 0.51) boundary.Add($"{f.FactorName}(贴上限)");
            }

            var (best, bestIdx) = PredictionHelper.FindBestRun(done, respName, minimize);

            string goalText = goal == "target"
                ? $"目标 {respName} = {PredictionHelper.Fmt(target.Value)}"
                : (goal == "minimize" ? $"最小化 {respName}" : $"最大化 {respName}");

            var sb = new StringBuilder();
            sb.AppendLine($"反向寻优完成(批次「{batch.BatchName}」,{modelType} 模型,{goalText},搜索限定在建模范围内):");
            sb.AppendLine();
            sb.AppendLine("推荐条件:");
            sb.AppendLine(ConditionTable(batch, recommend));
            sb.AppendLine($"预测 {respName} = {PredictionHelper.Fmt(predicted)}(95% 置信区间 [{PredictionHelper.Fmt(ciLo)}, {PredictionHelper.Fmt(ciHi)}])");
            sb.AppendLine($"模型可信度:{PredictionHelper.TierText(tier)}({string.Join(";", notes)})");
            if (targetNote != null) sb.AppendLine(targetNote);
            if (best.HasValue)
                sb.AppendLine($"当前实测最优:{respName} = {PredictionHelper.Fmt(best.Value)}(第 {bestIdx} 组)。");
            if (boundary.Count > 0)
                sb.AppendLine($"注意:推荐条件中 {string.Join("、", boundary)} 落在建模范围边界,真正的最优点可能在范围之外,建议提醒用户考虑扩范围实验(项目批次可走决策闭环)。");
            sb.AppendLine();
            sb.AppendLine("以上条件是模型推算的建议点,不是实测验证过的条件;回复用户时必须建议安排验证实验确认,再据实测决定是否采纳。");
            return AgentToolResult.Ok(sb.ToString());
        }

        private static string ConditionTable(DOEBatch batch, Dictionary<string, object> point)
        {
            var sb = new StringBuilder();
            sb.AppendLine("| 因子 | 推荐值 | 建模范围 |");
            sb.AppendLine("|---|---|---|");
            foreach (var f in (batch.Factors ?? new List<DOEFactor>()).OrderBy(f => f.SortOrder))
            {
                if (!point.TryGetValue(f.FactorName, out var v)) continue;
                string val = v is double d ? PredictionHelper.Fmt(d) : v?.ToString() ?? "";
                string range = f.IsCategorical
                    ? string.Join("/", f.GetCategoryLevelList())
                    : $"[{PredictionHelper.Fmt(f.LowerBound)}, {PredictionHelper.Fmt(f.UpperBound)}]";
                sb.AppendLine($"| {f.FactorName} | {val} | {range} |");
            }
            return sb.ToString();
        }

        /// <summary>调用 FindOptimalAsync 并解析(optimal_factors + predicted)。</summary>
        private static async Task<(Dictionary<string, object> Point, double Predicted)?> SearchAsync(
            IDOEAnalysisService svc, string batchId, string respName, bool maximize)
        {
            try
            {
                string json = await svc.FindOptimalAsync(batchId, respName, maximize).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (!root.TryGetProperty("optimal_factors", out var of) || of.ValueKind != JsonValueKind.Object)
                    return null;
                var point = new Dictionary<string, object>();
                foreach (var p in of.EnumerateObject())
                    point[p.Name] = p.Value.ValueKind == JsonValueKind.Number
                        ? (object)p.Value.GetDouble()
                        : p.Value.GetString();
                double pred = root.TryGetProperty("predicted", out var pv) && pv.ValueKind == JsonValueKind.Number
                    ? pv.GetDouble() : double.NaN;
                return double.IsNaN(pred) ? null : (point, pred);
            }
            catch { return null; }
        }

        /// <summary>
        /// 目标可达时的精修:从模型最优点出发,借 profiler 的单因子扫描曲线,
        /// 只回调一个因子使预测落到目标值——改动最小的方案对用户最可执行。
        /// </summary>
        private static async Task<(Dictionary<string, object> Point, double Predicted, string Note)> RefineToTargetAsync(
            IDOEAnalysisService svc, DOEBatch batch, string respName,
            Dictionary<string, object> fromPoint, double target)
        {
            string bestFactor = null;
            double bestX = 0, bestY = double.NaN, bestGap = double.MaxValue;
            try
            {
                string pj = await svc.GetPredictionProfilerAsync(batch.BatchId, respName, 50, fromPoint).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(pj);
                if (doc.RootElement.TryGetProperty("factors", out var fs) && fs.ValueKind == JsonValueKind.Object)
                {
                    foreach (var fp in fs.EnumerateObject())
                    {
                        var node = fp.Value;
                        if (node.TryGetProperty("is_categorical", out var ic) && ic.ValueKind == JsonValueKind.True) continue;
                        if (!node.TryGetProperty("x", out var xs) || !node.TryGetProperty("y", out var ys)) continue;
                        var xArr = xs.EnumerateArray().ToList();
                        var yArr = ys.EnumerateArray().ToList();
                        for (int i = 0; i < Math.Min(xArr.Count, yArr.Count); i++)
                        {
                            if (yArr[i].ValueKind != JsonValueKind.Number) continue;
                            double y = yArr[i].GetDouble();
                            double gap = Math.Abs(y - target);
                            if (gap < bestGap)
                            {
                                bestGap = gap; bestFactor = fp.Name;
                                bestX = xArr[i].GetDouble(); bestY = y;
                            }
                        }
                    }
                }
            }
            catch { }

            if (bestFactor == null || double.IsNaN(bestY))
                return (fromPoint, target, "说明:已按模型最优点给出条件(目标在可达区间内,精修未收敛,给出的是最优方向上的条件)。");

            var point = new Dictionary<string, object>(fromPoint) { [bestFactor] = bestX };
            return (point, bestY,
                $"说明:该条件由模型最优点出发、仅回调 {bestFactor} 得到(改动最小方案),预测值与目标差 {PredictionHelper.Fmt(bestGap)}。");
        }
    }
}

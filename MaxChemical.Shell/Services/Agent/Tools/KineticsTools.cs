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
    /// 机理动力学共用:从项目/批次的已完成组里提取 (τ, T, X) 数据并调用 KineticsFit。
    /// τ 优先取化学家坐标系配置里的停留时间因子;温度因子按名称含「温」自动识别,可显式指定。
    /// </summary>
    internal static class KineticsData
    {
        internal sealed class Meta
        {
            public string SourceName;      // 项目名或批次名
            public string TauFactor;
            public string TempFactor;      // 可空 = 单温
            public string ResponseName;
            public bool ResponseIsConversion;
            public int RunsTotal;
        }

        public static async Task<(List<KineticsPoint> Points, Meta Meta, string Error)> GatherAsync(
            IDOERepository repo, JsonElement args)
        {
            // ── 定位:项目优先(合并全部轮次数据),否则单批次 ──
            var batches = new List<DOEBatch>();
            string sourceName;
            string projName = AgentToolContext.GetString(args, "project_name");
            if (!string.IsNullOrWhiteSpace(projName))
            {
                var (proj, perr) = await DoeAnalysisHelper.FindProjectAsync(repo, projName).ConfigureAwait(false);
                if (proj == null) return (null, null, perr);
                var pb = await repo.GetBatchesByProjectAsync(proj.ProjectId).ConfigureAwait(false) ?? new List<DOEBatch>();
                foreach (var b in pb)
                {
                    var d = await repo.GetBatchWithDetailsAsync(b.BatchId).ConfigureAwait(false);
                    if (d != null) batches.Add(d);
                }
                sourceName = proj.ProjectName;
            }
            else
            {
                var (found, berr) = await DoeAnalysisHelper.FindBatchAsync(
                    repo, AgentToolContext.GetString(args, "batch_name")).ConfigureAwait(false);
                if (found == null) return (null, null, berr);
                var d = found.Runs.Count > 0 ? found : await repo.GetBatchWithDetailsAsync(found.BatchId).ConfigureAwait(false);
                if (d == null) return (null, null, "批次数据读取失败。");
                batches.Add(d);
                sourceName = d.BatchName;
            }
            if (batches.Count == 0) return (null, null, "没有可用批次。");

            // ── 因子名解析:τ 因子(显式 > 化学家坐标系配置 > 名称含「停留」),温度因子(显式 > 名称含「温」) ──
            string tauName = AgentToolContext.GetString(args, "tau_factor");
            string tempName = AgentToolContext.GetString(args, "temp_factor");
            var allFactorNames = batches.SelectMany(b => b.Factors ?? new List<DOEFactor>())
                                        .Select(f => f.FactorName).Distinct().ToList();
            if (string.IsNullOrWhiteSpace(tauName))
            {
                foreach (var b in batches)
                {
                    var tf = FactorTransform.ExtractFromDesignConfig(b.DesignConfigJson);
                    if (tf != null) { tauName = tf.TauFactor; break; }
                }
                tauName ??= allFactorNames.FirstOrDefault(n => n.Contains("停留"));
            }
            if (string.IsNullOrWhiteSpace(tauName) || !allFactorNames.Contains(tauName))
                return (null, null, "找不到停留时间因子。请传 tau_factor 指明哪个因子是停留时间(min)。" +
                                    $"现有因子:{string.Join("、", allFactorNames)}");
            if (string.IsNullOrWhiteSpace(tempName))
                tempName = allFactorNames.FirstOrDefault(n => n.Contains("温"));
            if (!string.IsNullOrWhiteSpace(tempName) && !allFactorNames.Contains(tempName))
                return (null, null, $"温度因子「{tempName}」不存在。现有因子:{string.Join("、", allFactorNames)}");

            // ── 响应与口径 ──
            string respName = AgentToolContext.GetString(args, "response_name");
            var respNames = batches.SelectMany(b => b.Responses ?? new List<DOEResponse>())
                                   .Select(x => x.ResponseName).Distinct().ToList();
            if (string.IsNullOrWhiteSpace(respName)) respName = respNames.FirstOrDefault();
            else if (!respNames.Contains(respName))
                return (null, null, $"没有响应「{respName}」。可选:{string.Join("、", respNames)}");
            if (string.IsNullOrEmpty(respName)) return (null, null, "没有响应变量,无法拟合。");

            bool? isConv = AgentToolContext.GetBool(args, "response_is_conversion");
            if (isConv == null)
                return (null, null, "必须先与用户确认响应口径并传 response_is_conversion:" +
                                    "true=该响应就是转化率;false=是收率(将按选择性约等于 1 近似当作转化率,需向用户说明)。");

            // ── 提取数据点 ──
            var pts = new List<KineticsPoint>();
            int total = 0;
            foreach (var b in batches)
            {
                foreach (var run in b.Runs.Where(x => x.Status == DOERunStatus.Completed))
                {
                    total++;
                    var f = DoeAnalysisHelper.ParseNumbers(run.FactorValuesJson);
                    var y = DoeAnalysisHelper.ParseNumbers(run.ResponseValuesJson);
                    if (!f.TryGetValue(tauName, out double tau) || !y.TryGetValue(respName, out double x)) continue;
                    double? tc = null;
                    if (!string.IsNullOrEmpty(tempName) && f.TryGetValue(tempName, out double t)) tc = t;
                    if (x > 1.5) x /= 100.0; // 百分数口径自动归一
                    pts.Add(new KineticsPoint { Tau = tau, TempC = tc, X = x, RunIndex = run.RunIndex });
                }
            }

            return (pts, new Meta
            {
                SourceName = sourceName,
                TauFactor = tauName,
                TempFactor = string.IsNullOrWhiteSpace(tempName) ? null : tempName,
                ResponseName = respName,
                ResponseIsConversion = isConv.Value,
                RunsTotal = total
            }, null);
        }

        public static async Task<(KineticsFitResult Fit, Meta Meta, double CA0, string Error)> FitFromArgsAsync(
            IDOERepository repo, JsonElement args)
        {
            string template = AgentToolContext.GetString(args, "template") ?? "first_order";
            if (template != "first_order" && template != "pseudo_first_order" && template != "second_order")
                return (null, null, 0, "template 只支持 first_order / pseudo_first_order / second_order。");

            double cA0 = AgentToolContext.GetNumber(args, "c_a0") ?? 0;
            if (template == "second_order" && cA0 <= 0)
                return (null, null, 0, "second_order 模板必须传限量物起始浓度 c_a0(mol/L,混合后浓度),该数值须来自用户。");

            var (pts, meta, err) = await GatherAsync(repo, args).ConfigureAwait(false);
            if (pts == null) return (null, null, 0, err);
            if (pts.Count == 0) return (null, meta, 0, "没有提取到任何有效数据点(已完成组里缺停留时间或响应值)。");

            var fit = KineticsFit.Fit(template, pts, cA0);
            if (!meta.ResponseIsConversion)
                fit.Notes.Add($"响应「{meta.ResponseName}」按收率≈转化率近似(假设选择性接近 1),向用户说明该前提。");
            return (fit, meta, cA0, null);
        }

        public static string TemplateText(string t) => t switch
        {
            "second_order" => "二级(A+B 等摩尔)",
            "pseudo_first_order" => "拟一级(过量组分并入 k)",
            _ => "一级"
        };

        public static string TierText(int tier) => tier switch
        {
            0 => "可靠",
            1 => "仅供参考(拟合质量一般)",
            _ => "不可靠(拒绝据此预测)"
        };
    }

    /// <summary>拟合动力学:速率常数 k、活化能 Ea,附质量分级与图表(只读)。</summary>
    public sealed class FitKineticsTool : IAgentTool
    {
        private readonly AgentToolContext _ctx;
        private readonly ILogService _logger = LogManager.GetLogger<FitKineticsTool>();
        public FitKineticsTool(AgentToolContext ctx) => _ctx = ctx;

        public string Name => "fit_kinetics";
        public string DisplayName => "拟合反应动力学";
        public string Description => @"用项目/批次已完成的 (停留时间, 温度, 转化率) 数据拟合单反应动力学:各温度下的速率常数 k,多温度时回归 Arrhenius 得活化能 Ea(kJ/mol)。这是机理层——与 analyze_doe_batch 的经验统计模型互补,机理模型可在数据范围外做有依据的外推。
调用前必须先与用户确认(用 ask_user_choice):①机理模板 first_order/pseudo_first_order/second_order;②响应口径(转化率还是收率,收率将按选择性约等于1近似)。second_order 还需用户提供混合后限量物浓度 c_a0。
定位:project_name(合并全部轮次,推荐)或 batch_name。τ 因子自动取化学家坐标系配置,也可 tau_factor 显式指定;温度因子按名称含「温」自动识别或 temp_factor 指定。
拟合质量硬分级:R²<0.6 拒绝(机理假设解释不了数据),0.6~0.85 降级为仅供参考。图表(转化率-停留时间、Arrhenius)由系统直接插入对话。";
        public string ParametersSchema => @"{
  ""type"":""object"",
  ""properties"":{
    ""project_name"":{""type"":""string"",""description"":""项目名或项目编号(合并该项目全部轮次数据,推荐)""},
    ""batch_name"":{""type"":""string"",""description"":""批次名或批次ID(只用单批次数据)""},
    ""template"":{""type"":""string"",""enum"":[""first_order"",""pseudo_first_order"",""second_order""],""description"":""机理模板,须经用户确认""},
    ""response_is_conversion"":{""type"":""boolean"",""description"":""true=响应就是转化率;false=收率按选择性约1近似。须经用户确认""},
    ""c_a0"":{""type"":""number"",""description"":""混合后限量物起始浓度 mol/L(second_order 必填,须来自用户)""},
    ""tau_factor"":{""type"":""string"",""description"":""停留时间因子名(化学家坐标系批次可省略)""},
    ""temp_factor"":{""type"":""string"",""description"":""温度因子名(℃;省略则按名称含温自动识别;没有温度因子则只拟单温 k)""},
    ""response_name"":{""type"":""string"",""description"":""响应名;省略取第一个""}
  },
  ""required"":[""template"",""response_is_conversion""]
}";
        public bool RequiresConfirmation => false;
        public string DescribeAction(JsonElement args) => "拟合反应动力学(k 与活化能)";

        public async Task<AgentToolResult> ExecuteAsync(JsonElement args)
        {
            var repo = _ctx.Resolve<IDOERepository>();
            var (fit, meta, cA0, err) = await KineticsData.FitFromArgsAsync(repo, args).ConfigureAwait(false);
            if (fit == null) return AgentToolResult.Fail(err);

            if (fit.Tier == 2)
            {
                var sbBad = new StringBuilder();
                sbBad.AppendLine($"「{meta.SourceName}」的动力学拟合未通过质量闸门,不给出参数结论:");
                foreach (var n in fit.Notes) sbBad.AppendLine("- " + n);
                sbBad.AppendLine("请如实转告用户,并给出路:①换机理模板重试;②确认响应口径与温度是否恒定;③补做不同停留时间的实验点。");
                return AgentToolResult.Ok(sbBad.ToString());
            }

            // ── 图 1:转化率-停留时间(按温度分组散点) ──
            var sorted = fit.Groups.OrderBy(g => g.TempC).ToList();
            try
            {
                var (pts, _, _) = await KineticsData.GatherAsync(repo, args).ConfigureAwait(false);
                if (pts != null && pts.Count > 0)
                {
                    DoeAnalysisHelper.PostChart(_ctx, new
                    {
                        type = "scatter",
                        title = $"转化率-停留时间(实测,{KineticsData.TemplateText(fit.Template)}模型 R²={fit.GlobalR2:F3})",
                        xLabel = $"{meta.TauFactor}(min)",
                        yLabel = "转化率",
                        x = new List<string>(),
                        xNumeric = pts.Select(p => p.Tau).ToList(),
                        series = new[] { new { name = "转化率", values = pts.Select(p => p.X).ToList() } }
                    });
                }
            }
            catch (Exception ex) { _logger.LogWarning($"动力学图表生成失败: {ex.Message}"); }

            // ── 图 2:Arrhenius ──
            if (fit.EaKJmol.HasValue)
            {
                try
                {
                    var tg = sorted.Where(g => !double.IsNaN(g.TempC)).ToList();
                    DoeAnalysisHelper.PostChart(_ctx, new
                    {
                        type = "scatter",
                        title = $"Arrhenius 图(Ea={fit.EaKJmol:F1} kJ/mol,R²={fit.ArrheniusR2:F3})",
                        xLabel = "1000/T(1/K)",
                        yLabel = "ln k",
                        x = new List<string>(),
                        xNumeric = tg.Select(g => 1000.0 / (g.TempC + 273.15)).ToList(),
                        series = new[] { new { name = "ln k", values = tg.Select(g => Math.Log(g.K)).ToList() } }
                    });
                }
                catch (Exception ex) { _logger.LogWarning($"Arrhenius 图生成失败: {ex.Message}"); }
            }

            var sb = new StringBuilder();
            sb.AppendLine($"动力学拟合完成(「{meta.SourceName}」,{KineticsData.TemplateText(fit.Template)},{fit.N} 个数据点,τ ∈ [{fit.TauMin:0.##}, {fit.TauMax:0.##}] min" +
                          (fit.TMinC.HasValue ? $",T ∈ [{fit.TMinC:0.#}, {fit.TMaxC:0.#}] ℃" : ",单温度") + "):");
            sb.AppendLine();
            sb.AppendLine("| 温度(℃) | k | 数据点 | 组内R² |");
            sb.AppendLine("|---|---|---|---|");
            string kUnit = fit.Template == "second_order" ? "L/(mol·min)" : "1/min";
            foreach (var g in sorted)
                sb.AppendLine($"| {(double.IsNaN(g.TempC) ? "-" : g.TempC.ToString("0.#"))} | {g.K:0.####} {kUnit} | {g.N} | {g.R2:F3} |");
            sb.AppendLine();
            if (fit.EaKJmol.HasValue)
                sb.AppendLine($"活化能 Ea = {fit.EaKJmol:F1}" +
                              (fit.EaSeKJmol.HasValue ? $" ± {2 * fit.EaSeKJmol:F1}" : "") +
                              $" kJ/mol(Arrhenius R² = {fit.ArrheniusR2:F3},ln A = {fit.LnA:F2})");
            sb.AppendLine($"整体拟合:R² = {fit.GlobalR2:F3},RMSE = {fit.Rmse:F4}(转化率单位)");
            sb.AppendLine($"质量分级:{KineticsData.TierText(fit.Tier)}");
            foreach (var n in fit.Notes) sb.AppendLine("- " + n);
            sb.AppendLine();
            sb.AppendLine("向用户解读时:k 的物理含义(越大反应越快)、Ea 的含义(温度敏感度,高 Ea 提温收益大)、以及以上全部假设都要讲清。" +
                          "需要外推预测、反解停留时间或放大换算时,用 predict_kinetics(数据范围外的结果必须标注机理外推并建议验证实验)。");
            return AgentToolResult.Ok(sb.ToString());
        }
    }

    /// <summary>机理预测:外推转化率、反解所需停留时间、放大换算(只读;内部重跑拟合保证口径一致)。</summary>
    public sealed class PredictKineticsTool : IAgentTool
    {
        private readonly AgentToolContext _ctx;
        public PredictKineticsTool(AgentToolContext ctx) => _ctx = ctx;

        public string Name => "predict_kinetics";
        public string DisplayName => "机理预测与放大";
        public string Description => @"基于 fit_kinetics 的机理模型做三类计算(参数与 fit_kinetics 相同并附加目标项,内部重新拟合保证口径一致):
①正算:给 tau(min)与 temp_c(℃)→ 预测转化率(允许在数据范围外做机理外推,结果会标注);
②反解:给 target_conversion(0~1)与 temp_c → 所需停留时间;
③放大:在①或②基础上给 reactor_volume_ml → 对应总流速;再给 c_a0 与 product_mw(g/mol)→ 产能 g/h。
拟合质量不达标时本工具会拒绝;所有外推结果必须原样带着「机理外推,未经实验验证」的标注转告用户,并建议安排验证实验(可衔接决策闭环)。";
        public string ParametersSchema => @"{
  ""type"":""object"",
  ""properties"":{
    ""project_name"":{""type"":""string""},
    ""batch_name"":{""type"":""string""},
    ""template"":{""type"":""string"",""enum"":[""first_order"",""pseudo_first_order"",""second_order""]},
    ""response_is_conversion"":{""type"":""boolean""},
    ""c_a0"":{""type"":""number"",""description"":""混合后限量物浓度 mol/L(second_order 或算产能时必填)""},
    ""tau_factor"":{""type"":""string""},
    ""temp_factor"":{""type"":""string""},
    ""response_name"":{""type"":""string""},
    ""tau"":{""type"":""number"",""description"":""正算:停留时间 min""},
    ""temp_c"":{""type"":""number"",""description"":""温度 ℃(有温度因子时必填;单温拟合可省)""},
    ""target_conversion"":{""type"":""number"",""description"":""反解:目标转化率 0~1(或百分数)""},
    ""reactor_volume_ml"":{""type"":""number"",""description"":""放大:反应器体积 mL""},
    ""product_mw"":{""type"":""number"",""description"":""产物分子量 g/mol(算产能用)""}
  },
  ""required"":[""template"",""response_is_conversion""]
}";
        public bool RequiresConfirmation => false;
        public string DescribeAction(JsonElement args) => "机理动力学预测/反解/放大换算";

        public async Task<AgentToolResult> ExecuteAsync(JsonElement args)
        {
            var repo = _ctx.Resolve<IDOERepository>();
            var (fit, meta, cA0, err) = await KineticsData.FitFromArgsAsync(repo, args).ConfigureAwait(false);
            if (fit == null) return AgentToolResult.Fail(err);
            if (fit.Tier == 2)
                return AgentToolResult.Ok("动力学拟合未通过质量闸门(" + string.Join(";", fit.Notes) +
                                          "),拒绝据此预测。请先解决拟合质量问题(换模板/补数据/核对口径)。");

            double? tau = AgentToolContext.GetNumber(args, "tau");
            double? tempC = AgentToolContext.GetNumber(args, "temp_c");
            double? targetX = AgentToolContext.GetNumber(args, "target_conversion");
            if (targetX.HasValue && targetX.Value > 1.5) targetX /= 100.0;
            double? volMl = AgentToolContext.GetNumber(args, "reactor_volume_ml");
            double? mw = AgentToolContext.GetNumber(args, "product_mw");

            if (!tau.HasValue && !targetX.HasValue)
                return AgentToolResult.Fail("至少要给 tau(正算转化率)或 target_conversion(反解停留时间)之一。");

            // ── 求该温度下的 k ──
            double k;
            var warns = new List<string>();
            var tGroups = fit.Groups.Where(g => !double.IsNaN(g.TempC)).OrderBy(g => g.TempC).ToList();
            if (tGroups.Count == 0)
            {
                k = fit.Groups[0].K; // 单温拟合
                if (tempC.HasValue) warns.Add("数据没有温度因子,忽略 temp_c,按拟合温度下的 k 计算。");
            }
            else
            {
                if (!tempC.HasValue)
                    return AgentToolResult.Fail($"数据含多个温度,必须给 temp_c。已拟温度:{string.Join("、", tGroups.Select(g => g.TempC.ToString("0.#")))} ℃。");
                var exact = tGroups.FirstOrDefault(g => Math.Abs(g.TempC - tempC.Value) < 0.75);
                if (exact != null) k = exact.K;
                else if (fit.EaKJmol.HasValue && fit.ArrheniusR2.HasValue && fit.ArrheniusR2 < 0.8)
                {
                    return AgentToolResult.Fail(
                        $"Arrhenius 线性度太差(R² = {fit.ArrheniusR2:F3}),不同温度之间的 k 无法可靠内插/外推——" +
                        $"k 随温度的行为不符合单一活化能(可能高温失活/分解)。请改用已实测的温度({string.Join("、", tGroups.Select(g => g.TempC.ToString("0.#")))} ℃)提问,或先查明高温组异常的原因。");
                }
                else if (fit.EaKJmol.HasValue)
                {
                    k = KineticsFit.KAtTemp(fit.LnA.Value, fit.EaKJmol.Value, tempC.Value);
                    if (tempC.Value < fit.TMinC - 0.5 || tempC.Value > fit.TMaxC + 0.5)
                        warns.Add($"温度 {tempC:0.#} ℃ 在实验范围 [{fit.TMinC:0.#}, {fit.TMaxC:0.#}] ℃ 之外——机理外推,未经实验验证。");
                    else
                        warns.Add($"温度 {tempC:0.#} ℃ 非实测水平,k 由 Arrhenius 内插。");
                }
                else
                    return AgentToolResult.Fail("该温度没有实测 k,且温度水平不足以拟 Arrhenius,无法计算。请补一个温度水平或用已拟温度。");
            }

            var sb = new StringBuilder();
            sb.AppendLine($"机理预测(「{meta.SourceName}」,{KineticsData.TemplateText(fit.Template)},质量分级:{KineticsData.TierText(fit.Tier)}):");
            double usedTau;
            double x;
            if (tau.HasValue)
            {
                usedTau = tau.Value;
                x = KineticsFit.ModelX(fit.Template, k, usedTau, cA0);
                sb.AppendLine($"- τ = {usedTau:0.##} min" + (tempC.HasValue ? $",T = {tempC:0.#} ℃" : "") +
                              $" → 预测转化率 = {x:P1}");
                if (usedTau < fit.TauMin - 1e-9 || usedTau > fit.TauMax + 1e-9)
                    warns.Add($"τ = {usedTau:0.##} min 在实验范围 [{fit.TauMin:0.##}, {fit.TauMax:0.##}] min 之外——机理外推,未经实验验证。");
            }
            else
            {
                x = targetX.Value;
                usedTau = KineticsFit.SolveTauForX(fit.Template, k, x, cA0);
                sb.AppendLine($"- 目标转化率 {x:P1}" + (tempC.HasValue ? $",T = {tempC:0.#} ℃" : "") +
                              $" → 所需停留时间 ≈ {usedTau:0.##} min");
                if (usedTau > fit.TauMax * 1.001)
                    warns.Add($"所需 τ 超出实验范围上限 {fit.TauMax:0.##} min——机理外推,未经实验验证。");
            }

            if (volMl.HasValue && volMl.Value > 0 && usedTau > 0)
            {
                double q = volMl.Value / usedTau; // mL/min
                sb.AppendLine($"- 放大:反应器 {volMl:0.#} mL,总流速 = {q:0.###} mL/min");
                if (cA0 > 0 && mw.HasValue && mw.Value > 0)
                {
                    double rate = q * cA0 * x * mw.Value * 60.0 / 1000.0; // g/h
                    sb.AppendLine($"- 产能 ≈ {rate:0.##} g/h(即 {rate * 24:0.#} g/天;按 C_A0={cA0:0.###} mol/L、M={mw:0.#} g/mol、转化率 {x:P1};仅体积/流速换算,未做传热传质校核)");
                }
            }

            foreach (var w in warns) sb.AppendLine("- 注意:" + w);
            if (fit.Tier == 1) sb.AppendLine("- 拟合质量一般(" + string.Join(";", fit.Notes.Take(2)) + "),数值仅供参考。");
            sb.AppendLine();
            sb.AppendLine("以上是机理模型推算,不是实测:外推标注必须原样转告用户,并主动建议安排验证实验(项目批次可走决策闭环生成验证轮)。");
            return AgentToolResult.Ok(sb.ToString());
        }
    }
}

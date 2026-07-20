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
    /// <summary>反应网络工具共用:样本读取、物种映射、初始浓度组装。</summary>
    internal static class NetworkData
    {
        /// <summary>把 species_map(角色→样本物种名)解析为 角色下标→物种名。</summary>
        public static string ParseSpeciesMap(JsonElement args, string template, out Dictionary<int, string> map)
        {
            map = new Dictionary<int, string>();
            var roles = ReactionNetwork.Roles(template);
            if (!args.TryGetProperty("species_map", out var sm) || sm.ValueKind != JsonValueKind.Object)
                return $"必须传 species_map:把机理角色映射到取样数据里的物种名,如 {{{string.Join(",", roles.Select(r => $"\"{r}\":\"物种名\""))}}}(没测的角色可省略,但至少要映射一个)。";
            foreach (var p in sm.EnumerateObject())
            {
                int idx = Array.FindIndex(roles, r => string.Equals(r, p.Name, StringComparison.OrdinalIgnoreCase));
                if (idx < 0) return $"species_map 的键「{p.Name}」不是模板角色。{ReactionNetwork.Describe(template)} 的角色:{string.Join("/", roles)}。";
                if (p.Value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(p.Value.GetString()))
                    map[idx] = p.Value.GetString().Trim();
            }
            return map.Count == 0 ? "species_map 至少要映射一个角色到实测物种。" : null;
        }

        /// <summary>初始浓度:c0(角色→mol/L)优先,未给的角色取 0;A(以及 A+B 类模板的 B)必须有值。</summary>
        public static string ParseC0(JsonElement args, string template, out double[] y0)
        {
            var roles = ReactionNetwork.Roles(template);
            y0 = new double[roles.Length];
            if (args.TryGetProperty("c0", out var c0) && c0.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in c0.EnumerateObject())
                {
                    int idx = Array.FindIndex(roles, r => string.Equals(r, p.Name, StringComparison.OrdinalIgnoreCase));
                    if (idx >= 0 && p.Value.ValueKind == JsonValueKind.Number) y0[idx] = p.Value.GetDouble();
                }
            }
            if (y0[0] <= 0)
                return "必须在 c0 里给出 A 的初始浓度(mol/L,混合后口径),数值须来自用户,严禁自己猜。";
            bool needB = template is "a_plus_b" or "abc_over";
            if (needB && y0[1] <= 0)
                return "该模板是 A+B 型,c0 里还需给出 B 的初始浓度(mol/L)。";
            return null;
        }

        /// <summary>
        /// 读取数据点并映射到角色。两种来源自动选择:
        /// ①取样表(doe_run_samples,支持同组多时间点,釜式/多点取样);
        /// ②各组响应(流动化学:每组停留时间不同,若批次把各物种出口浓度定义为响应,
        ///   执行时的结果录入弹框直接就是数据通道——时间轴 = 各组 τ,浓度 = 响应值)。
        /// 有取样表用取样表,否则自动尝试响应通道。
        /// </summary>
        public static async Task<(List<NetworkPoint> Points, string BatchName, string Error)> LoadPointsAsync(
            IDOERepository repo, JsonElement args, Dictionary<int, string> speciesMap)
        {
            var (found, err) = await DoeAnalysisHelper.FindBatchAsync(
                repo, AgentToolContext.GetString(args, "batch_name")).ConfigureAwait(false);
            if (found == null) return (null, null, err);

            int? runIndex = AgentToolContext.GetNumber(args, "run_index") is double d ? (int)d : (int?)null;
            List<RunSample> samples;
            try
            {
                samples = await repo.GetRunSamplesAsync(found.BatchId, runIndex).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex)
            {
                return (null, null, ex.Message);
            }
            if (samples.Count == 0)
            {
                // 响应通道:批次详情里找停留时间因子 + 名字对得上物种的响应
                var detail = found.Runs.Count > 0
                    ? found
                    : await repo.GetBatchWithDetailsAsync(found.BatchId).ConfigureAwait(false);
                if (detail != null)
                {
                    var fromResp = TryPointsFromResponses(detail, speciesMap);
                    if (fromResp.Points != null && fromResp.Points.Count > 0)
                        return (fromResp.Points, detail.BatchName + fromResp.Note, null);
                }
                return (null, null, $"批次「{found.BatchName}」" + (runIndex.HasValue ? $"第 {runIndex} 组" : "") +
                                    "既没有取样数据,也没有名字能对上物种的浓度响应。两条路:" +
                                    "①用 record_run_samples 录入浓度-时间数据(HPLC 结果);" +
                                    "②流动化学批次可把各物种出口浓度定义为响应,执行时在结果弹框里直接填," +
                                    "本工具会自动把各组停留时间当时间轴。");
            }

            var pts = new List<NetworkPoint>();
            var unmatched = new HashSet<string>();
            foreach (var s in samples)
            {
                var hit = speciesMap.FirstOrDefault(kv => string.Equals(kv.Value, s.Species, StringComparison.OrdinalIgnoreCase));
                if (hit.Value == null) { unmatched.Add(s.Species); continue; }
                pts.Add(new NetworkPoint { T = s.SampleTime, Role = hit.Key, C = s.Concentration });
            }
            if (pts.Count == 0)
                return (null, null, "species_map 没有匹配到任何取样物种。数据里的物种有:" +
                                    string.Join("、", samples.Select(s => s.Species).Distinct()) + "。");
            string note = unmatched.Count > 0 ? $"(未映射物种已忽略:{string.Join("、", unmatched)})" : "";
            return (pts, found.BatchName + note, null);
        }

        /// <summary>
        /// 响应通道取数(流动化学专用):时间 = 各组停留时间因子值,浓度 = 对应响应值。
        /// 响应名与 species_map 的物种名按 包含 匹配(响应常叫「苯甲醛浓度」,物种叫「苯甲醛」)。
        /// </summary>
        private static (List<NetworkPoint> Points, string Note) TryPointsFromResponses(
            DOEBatch detail, Dictionary<int, string> speciesMap)
        {
            // 停留时间因子:化学家坐标系配置优先,其次名字含「停留」
            string tauName = FactorTransform.ExtractFromDesignConfig(detail.DesignConfigJson)?.TauFactor;
            tauName ??= detail.Factors?.FirstOrDefault(f => f.FactorName?.Contains("停留") == true)?.FactorName;
            if (string.IsNullOrEmpty(tauName)) return (null, null);

            var respNames = detail.Responses?.Select(r => r.ResponseName).ToList() ?? new List<string>();
            // 角色 → 响应名:精确同名优先,再唯一「响应名包含物种名」
            var roleToResp = new Dictionary<int, string>();
            foreach (var kv in speciesMap)
            {
                var exact = respNames.FirstOrDefault(n => string.Equals(n, kv.Value, StringComparison.OrdinalIgnoreCase));
                if (exact == null)
                {
                    var contains = respNames.Where(n =>
                        n.IndexOf(kv.Value, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                    if (contains.Count == 1) exact = contains[0];
                }
                if (exact != null) roleToResp[kv.Key] = exact;
            }
            if (roleToResp.Count == 0) return (null, null);

            var pts = new List<NetworkPoint>();
            foreach (var run in detail.Runs.Where(r => r.Status == DOERunStatus.Completed))
            {
                var f = DoeAnalysisHelper.ParseNumbers(run.FactorValuesJson);
                var y = DoeAnalysisHelper.ParseNumbers(run.ResponseValuesJson);
                if (!f.TryGetValue(tauName, out double tau) || tau <= 0) continue;
                foreach (var kv in roleToResp)
                    if (y.TryGetValue(kv.Value, out double c) && c >= 0)
                        pts.Add(new NetworkPoint { T = tau, Role = kv.Key, C = c });
            }
            if (pts.Count == 0) return (null, null);

            string mapped = string.Join("、", roleToResp.Select(kv => kv.Value));
            return (pts, $"(数据来源:各组响应 {mapped},时间轴=停留时间「{tauName}」;" +
                         "注意:多温度批次混在一起时请先固定温度或只选同温组)");
        }
    }

    /// <summary>录入取样数据:浓度-时间点写入数据库(V2 数据通道,写操作需确认)。</summary>
    public sealed class RecordRunSamplesTool : IAgentTool
    {
        private readonly AgentToolContext _ctx;
        public RecordRunSamplesTool(AgentToolContext ctx) => _ctx = ctx;

        public string Name => "record_run_samples";
        public string DisplayName => "录入取样数据";
        public string Description => @"把某批次某组的浓度-时间取样数据(HPLC/离线分析结果)写入数据库,供反应网络拟合使用。用户口述或粘贴数据表时用本工具;时间单位 min(流动化学=停留时间),浓度单位 mol/L(其他单位先换算并向用户确认)。
同一组重复录入时传 overwrite=true 覆盖旧数据;默认追加。录入前把整理好的数据表展示给用户核对。";
        public string ParametersSchema => @"{
  ""type"":""object"",
  ""properties"":{
    ""batch_name"":{""type"":""string"",""description"":""批次名或批次ID""},
    ""run_index"":{""type"":""number"",""description"":""组号(RunIndex)""},
    ""samples"":{""type"":""array"",""minItems"":1,""items"":{
      ""type"":""object"",
      ""properties"":{
        ""time_min"":{""type"":""number""},
        ""species"":{""type"":""string""},
        ""concentration"":{""type"":""number"",""description"":""mol/L""}},
      ""required"":[""time_min"",""species"",""concentration""]}},
    ""overwrite"":{""type"":""boolean"",""description"":""true=先清空该组已有取样再写入""}
  },
  ""required"":[""run_index"",""samples""]
}";
        public bool RequiresConfirmation => true;

        public string DescribeAction(JsonElement args)
        {
            int n = args.TryGetProperty("samples", out var s) && s.ValueKind == JsonValueKind.Array ? s.GetArrayLength() : 0;
            return $"录入 {n} 个取样点(第 {AgentToolContext.GetNumber(args, "run_index"):0} 组)到数据库";
        }

        public async Task<AgentToolResult> ExecuteAsync(JsonElement args)
        {
            var repo = _ctx.Resolve<IDOERepository>();
            var (found, err) = await DoeAnalysisHelper.FindBatchAsync(
                repo, AgentToolContext.GetString(args, "batch_name")).ConfigureAwait(false);
            if (found == null) return AgentToolResult.Fail(err);

            double? runIdx = AgentToolContext.GetNumber(args, "run_index");
            if (runIdx == null) return AgentToolResult.Fail("缺少 run_index(组号)。");

            var list = new List<RunSample>();
            if (args.TryGetProperty("samples", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in arr.EnumerateArray())
                {
                    double? t = AgentToolContext.GetNumber(el, "time_min");
                    double? c = AgentToolContext.GetNumber(el, "concentration");
                    string sp = AgentToolContext.GetString(el, "species");
                    if (t == null || c == null || string.IsNullOrWhiteSpace(sp))
                        return AgentToolResult.Fail("samples 每行必须有 time_min / species / concentration。");
                    if (t < 0 || c < 0) return AgentToolResult.Fail("时间与浓度不能为负。");
                    list.Add(new RunSample { SampleTime = t.Value, Species = sp.Trim(), Concentration = c.Value });
                }
            }
            if (list.Count == 0) return AgentToolResult.Fail("samples 为空。");

            bool overwrite = AgentToolContext.GetBool(args, "overwrite") ?? false;
            try
            {
                await repo.SaveRunSamplesAsync(found.BatchId, (int)runIdx.Value, list, overwrite).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex)
            {
                return AgentToolResult.Fail(ex.Message);
            }

            var species = list.Select(s => s.Species).Distinct().ToList();
            return AgentToolResult.Ok(
                $"已录入:批次「{found.BatchName}」第 {(int)runIdx.Value} 组,{list.Count} 个取样点," +
                $"物种 {string.Join("、", species)},t ∈ [{list.Min(s => s.SampleTime):0.##}, {list.Max(s => s.SampleTime):0.##}] min" +
                (overwrite ? "(已覆盖旧数据)" : "(追加)") +
                "。数据齐后可用 fit_reaction_network 拟合多步反应网络。");
        }
    }

    /// <summary>拟合多步反应网络:ODE + 最小二乘,得各步速率常数(只读)。</summary>
    public sealed class FitReactionNetworkTool : IAgentTool
    {
        private readonly AgentToolContext _ctx;
        private readonly ILogService _logger = LogManager.GetLogger<FitReactionNetworkTool>();
        public FitReactionNetworkTool(AgentToolContext ctx) => _ctx = ctx;

        public string Name => "fit_reaction_network";
        public string DisplayName => "拟合反应网络";
        public string Description => @"用浓度-时间数据拟合多步反应网络的各步速率常数(等温)。机理模板:consecutive(A→B→C)/ parallel(A→B,A→C)/ a_plus_b(A+B→C)/ abc_over(A+B→C→D 过反应)/ reversible(A⇌B)。
数据来源两种,自动选择:①取样表(record_run_samples 录入,支持同组多时间点);②各组响应——流动化学批次把各物种出口浓度定义成响应(如 苯甲醛浓度),执行时在结果录入弹框直接填,本工具自动把各组停留时间当时间轴、响应值当浓度,无需单独录样;多温度批次走响应通道时须提醒用户等温假设(建议只用同温度的组或先固定温度)。
调用前必须与用户确认(选择卡):机理模板、物种映射(species_map:角色→数据里的物种名或响应名)、初始浓度 c0(混合后口径,mol/L,须用户口述)。
质量硬分级:R²<0.6 拒绝仿真;单物种数据拟双参数会警告可辨识性差。浓度-时间图由系统插入对话。";
        public string ParametersSchema => @"{
  ""type"":""object"",
  ""properties"":{
    ""batch_name"":{""type"":""string"",""description"":""批次名或批次ID""},
    ""run_index"":{""type"":""number"",""description"":""组号;省略则合并该批次全部取样(须同条件)""},
    ""template"":{""type"":""string"",""enum"":[""consecutive"",""parallel"",""a_plus_b"",""abc_over"",""reversible""]},
    ""species_map"":{""type"":""object"",""description"":""角色→数据物种名,如 {\""A\"":\""苯甲醇\"",\""C\"":\""苯甲醛\""}"",""additionalProperties"":{""type"":""string""}},
    ""c0"":{""type"":""object"",""description"":""角色→初始浓度 mol/L(A 必填;A+B 型 B 必填;产物默认 0)"",""additionalProperties"":{""type"":""number""}}
  },
  ""required"":[""template"",""species_map"",""c0""]
}";
        public bool RequiresConfirmation => false;
        public string DescribeAction(JsonElement args) => "拟合多步反应网络速率常数";

        public async Task<AgentToolResult> ExecuteAsync(JsonElement args)
        {
            string template = AgentToolContext.GetString(args, "template") ?? "";
            if (ReactionNetwork.Roles(template).Length == 0)
                return AgentToolResult.Fail("template 只支持 consecutive/parallel/a_plus_b/abc_over/reversible。");

            string merr = NetworkData.ParseSpeciesMap(args, template, out var map);
            if (merr != null) return AgentToolResult.Fail(merr);
            string cerr = NetworkData.ParseC0(args, template, out var y0);
            if (cerr != null) return AgentToolResult.Fail(cerr);

            var repo = _ctx.Resolve<IDOERepository>();
            var (pts, batchName, lerr) = await NetworkData.LoadPointsAsync(repo, args, map).ConfigureAwait(false);
            if (pts == null) return AgentToolResult.Fail(lerr);

            var fit = ReactionNetwork.Fit(template, pts, y0);
            var roles = ReactionNetwork.Roles(template);

            if (fit.Tier == 2)
            {
                var sbBad = new StringBuilder();
                sbBad.AppendLine($"「{batchName}」的反应网络拟合未通过质量闸门,不给出速率常数:");
                foreach (var n in fit.Notes) sbBad.AppendLine("- " + n);
                sbBad.AppendLine("请如实转告用户:可换机理模板、核对物种映射与初始浓度、或补测中间体/产物的取样点。");
                return AgentToolResult.Ok(sbBad.ToString());
            }

            // 浓度-时间图:实测散点(按物种)+ 模型曲线值(同一时间轴)
            try
            {
                var times = pts.Select(p => p.T).Distinct().OrderBy(t => t).ToList();
                var traj = ReactionNetwork.Simulate(template, fit.K, y0, Math.Max(fit.TMax, 1e-6) * 1.02);
                var series = new List<object>();
                foreach (var role in map.Keys.OrderBy(k => k).Take(3))
                {
                    var measured = times.Select(t =>
                    {
                        var hit = pts.Where(p => p.Role == role && Math.Abs(p.T - t) < 1e-9).ToList();
                        return hit.Count > 0 ? (double?)hit.Average(p => p.C) : null;
                    }).ToList();
                    var model = times.Select(t => (double?)ReactionNetwork.Interp(traj, t)[role]).ToList();
                    series.Add(new { name = $"{map[role]}(实测)", values = measured });
                    series.Add(new { name = $"{map[role]}(模型)", values = model });
                }
                DoeAnalysisHelper.PostChart(_ctx, new
                {
                    type = "line",
                    title = $"浓度-时间:实测 vs 模型(R²={fit.GlobalR2:F3})",
                    yLabel = "浓度(mol/L)",
                    x = times.Select(t => t.ToString("0.##")).ToList(),
                    series
                });
            }
            catch (Exception ex) { _logger.LogWarning($"网络拟合图表失败: {ex.Message}"); }

            var sb = new StringBuilder();
            sb.AppendLine($"反应网络拟合完成(「{batchName}」,{ReactionNetwork.Describe(template)},{fit.N} 个取样点,t ≤ {fit.TMax:0.##} min):");
            sb.AppendLine();
            for (int i = 0; i < fit.K.Length; i++)
                sb.AppendLine($"- k{i + 1} = {fit.K[i]:0.####} " + (template is "a_plus_b" or "abc_over" && i == 0 ? "L/(mol·min)" : "1/min"));
            sb.AppendLine($"- 整体 R² = {fit.GlobalR2:F3};分物种 R²:" +
                          string.Join(",", fit.R2ByRole.OrderBy(kv => kv.Key)
                              .Select(kv => $"{(map.TryGetValue(kv.Key, out var nm) ? nm : roles[kv.Key])}={kv.Value:F3}")));
            sb.AppendLine($"- 质量分级:{(fit.Tier == 0 ? "可靠" : "仅供参考")}");
            foreach (var n in fit.Notes) sb.AppendLine("- " + n);
            sb.AppendLine();
            sb.AppendLine("向用户解读各步快慢与瓶颈;要预测其他条件下的浓度曲线、找选择性最优停留时间,用 simulate_reaction。");
            return AgentToolResult.Ok(sb.ToString());
        }
    }

    /// <summary>机理仿真:预测浓度曲线、找目标产物峰值/最优停留时间(只读;内部重拟合)。</summary>
    public sealed class SimulateReactionTool : IAgentTool
    {
        private readonly AgentToolContext _ctx;
        public SimulateReactionTool(AgentToolContext ctx) => _ctx = ctx;

        public string Name => "simulate_reaction";
        public string DisplayName => "反应网络仿真";
        public string Description => @"基于 fit_reaction_network 的机理模型做仿真(参数同 fit_reaction_network,内部重拟合保证口径一致):预测任意时刻的各物种浓度,并扫描给出目标产物(默认角色 C)的峰值浓度与对应最优停留时间——过反应体系(abc_over)里这就是「τ 拖太长产物变副产物」的定量答案。
sim_time_min 省略时仿真到数据时长的 3 倍;超出实测范围的部分标注机理外推。拟合不达标会拒绝。";
        public string ParametersSchema => @"{
  ""type"":""object"",
  ""properties"":{
    ""batch_name"":{""type"":""string""},
    ""run_index"":{""type"":""number""},
    ""template"":{""type"":""string"",""enum"":[""consecutive"",""parallel"",""a_plus_b"",""abc_over"",""reversible""]},
    ""species_map"":{""type"":""object"",""additionalProperties"":{""type"":""string""}},
    ""c0"":{""type"":""object"",""additionalProperties"":{""type"":""number""}},
    ""sim_time_min"":{""type"":""number"",""description"":""仿真时长 min(默认数据时长3倍)""},
    ""target_role"":{""type"":""string"",""description"":""目标产物角色(默认 C)""}
  },
  ""required"":[""template"",""species_map"",""c0""]
}";
        public bool RequiresConfirmation => false;
        public string DescribeAction(JsonElement args) => "机理仿真与最优停留时间扫描";

        public async Task<AgentToolResult> ExecuteAsync(JsonElement args)
        {
            string template = AgentToolContext.GetString(args, "template") ?? "";
            if (ReactionNetwork.Roles(template).Length == 0)
                return AgentToolResult.Fail("template 不合法。");
            string merr = NetworkData.ParseSpeciesMap(args, template, out var map);
            if (merr != null) return AgentToolResult.Fail(merr);
            string cerr = NetworkData.ParseC0(args, template, out var y0);
            if (cerr != null) return AgentToolResult.Fail(cerr);

            var repo = _ctx.Resolve<IDOERepository>();
            var (pts, batchName, lerr) = await NetworkData.LoadPointsAsync(repo, args, map).ConfigureAwait(false);
            if (pts == null) return AgentToolResult.Fail(lerr);

            var fit = ReactionNetwork.Fit(template, pts, y0);
            if (fit.Tier == 2)
                return AgentToolResult.Ok("反应网络拟合未通过质量闸门(" + string.Join(";", fit.Notes) + "),拒绝仿真。");

            var roles = ReactionNetwork.Roles(template);
            string targetRoleStr = AgentToolContext.GetString(args, "target_role") ?? "C";
            int target = Array.FindIndex(roles, r => string.Equals(r, targetRoleStr, StringComparison.OrdinalIgnoreCase));
            if (target < 0) target = Math.Min(2, roles.Length - 1);

            double simEnd = AgentToolContext.GetNumber(args, "sim_time_min") ?? fit.TMax * 3;
            simEnd = Math.Max(simEnd, fit.TMax);
            var traj = ReactionNetwork.Simulate(template, fit.K, y0, simEnd);

            // 目标产物峰值扫描
            double bestT = 0, bestC = 0;
            foreach (var (t, y) in traj)
                if (y[target] > bestC) { bestC = y[target]; bestT = t; }

            // 仿真曲线图(降采样 40 点)
            try
            {
                int step = Math.Max(1, traj.Count / 40);
                var sampled = traj.Where((_, i) => i % step == 0).ToList();
                var series = new List<object>();
                for (int roleIdx = 0; roleIdx < roles.Length && series.Count < 4; roleIdx++)
                {
                    string nm = map.TryGetValue(roleIdx, out var mapped) ? mapped : roles[roleIdx];
                    series.Add(new { name = nm, values = sampled.Select(s => (double?)s.Y[roleIdx]).ToList() });
                }
                DoeAnalysisHelper.PostChart(_ctx, new
                {
                    type = "line",
                    title = $"机理仿真浓度曲线(0~{simEnd:0.#} min,实测覆盖到 {fit.TMax:0.#} min)",
                    yLabel = "浓度(mol/L)",
                    x = sampled.Select(s => s.T.ToString("0.#")).ToList(),
                    series
                });
            }
            catch { }

            var sb = new StringBuilder();
            sb.AppendLine($"机理仿真(「{batchName}」,{ReactionNetwork.Describe(template)},质量分级:{(fit.Tier == 0 ? "可靠" : "仅供参考")}):");
            string targetName = map.TryGetValue(target, out var tn) ? tn : roles[target];
            sb.AppendLine($"- 目标产物「{targetName}」峰值浓度 ≈ {bestC:0.####} mol/L,出现在 t ≈ {bestT:0.##} min");
            if (template == "abc_over" && roles.Length > 3)
            {
                var atBest = ReactionNetwork.Interp(traj, bestT);
                double sel = atBest[2] + atBest[3] > 1e-12 ? atBest[2] / (atBest[2] + atBest[3]) : 1;
                sb.AppendLine($"- 峰值处选择性 C/(C+D) ≈ {sel:P1};停留时间超过 {bestT:0.##} min 后产物开始被过反应消耗");
            }
            if (bestT > fit.TMax * 1.001)
                sb.AppendLine($"- 注意:峰值出现在实测范围({fit.TMax:0.##} min)之外——机理外推,未经实验验证,建议在 {bestT:0.##} min 附近安排验证实验。");
            foreach (var n in fit.Notes) sb.AppendLine("- " + n);
            sb.AppendLine();
            sb.AppendLine("以上是机理模型仿真,不是实测:外推标注必须原样转告用户,并建议安排验证实验。");
            return AgentToolResult.Ok(sb.ToString());
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using MaxChemical.Infrastructure.DOE;
using MaxChemical.Logging;
using MaxChemical.Modules.DOE.Data;
using MaxChemical.Modules.DOE.Models;
using MaxChemical.Modules.DOE.Services;

namespace MaxChemical.Shell.Services.Agent.Tools
{
    /// <summary>
    /// 化学家坐标系对话直控:用户说「把停留时间调到 3 分钟」「当量比改成 1.5」时,
    /// 联动解算两台泵的流速并一次确认批量写入流程参数——用户永远不用自己换算泵速。
    /// 改一个物理量、另一个保持不变(τ 和 eq 是正交旋钮,这正是化学家坐标系的意义)。
    /// </summary>
    public sealed class SetProcessConditionTool : IAgentTool
    {
        private readonly AgentToolContext _ctx;
        private readonly ILogService _logger = LogManager.GetLogger<SetProcessConditionTool>();
        public SetProcessConditionTool(AgentToolContext ctx) => _ctx = ctx;

        public string Name => "set_process_condition";
        public string DisplayName => "调整工艺条件(化学家坐标)";
        public string Description => @"按化学家坐标调整当前流程的进料泵:用户说「把停留时间调到 X 分钟」「当量比改成 Y」时用本工具,系统读取当前泵速、保持另一个物理量不变、联动解算两泵新流速并批量写入流程参数(下次运行流程生效)。
前提:当前流程有带化学家坐标系配置的 DOE 批次(preview_doe_design 传过 transform 的批次),常数(V、浓度)沿用该批次的固化快照。超泵量程会拒绝并说明边界。
严禁绕过本工具自己算泵速再用 set_device_parameter 分次设置——两泵先后写入会造成当量比瞬时错误。";
        public string ParametersSchema => @"{
  ""type"":""object"",
  ""properties"":{
    ""condition"":{""type"":""string"",""enum"":[""residence_time"",""equivalence_ratio""],""description"":""要调整的物理量:residence_time=停留时间(min),equivalence_ratio=当量比""},
    ""value"":{""type"":""number"",""description"":""目标值""},
    ""batch_name"":{""type"":""string"",""description"":""指定从哪个批次读取转换配置(可省:自动取最近一个带化学家坐标系的批次)""}
  },
  ""required"":[""condition"",""value""]
}";
        public bool RequiresConfirmation => true;

        public string DescribeAction(JsonElement args)
        {
            string cond = AgentToolContext.GetString(args, "condition");
            double? v = AgentToolContext.GetNumber(args, "value");
            string name = cond == "equivalence_ratio" ? "当量比" : "停留时间(min)";
            return $"按化学家坐标调整进料泵:{name} → {v:0.####}(另一物理量保持不变,联动重算两泵流速后批量写入流程参数)";
        }

        public async Task<AgentToolResult> ExecuteAsync(JsonElement args)
        {
            string cond = AgentToolContext.GetString(args, "condition");
            double? target = AgentToolContext.GetNumber(args, "value");
            if (target == null || (cond != "residence_time" && cond != "equivalence_ratio"))
                return AgentToolResult.Fail("参数不全:condition(residence_time/equivalence_ratio)与 value 必填。");
            if (target.Value <= 0 && cond == "residence_time")
                return AgentToolResult.Fail("停留时间必须大于 0。");
            if (target.Value < 0)
                return AgentToolResult.Fail("目标值不能为负。");

            // 1) 找转换配置:指定批次优先,否则取最近一个带配置的批次(常数快照随批次固化)
            var repo = _ctx.Resolve<IDOERepository>();
            FactorTransformConfig cfg = null;
            string cfgSource = null;
            string batchName = AgentToolContext.GetString(args, "batch_name");
            if (!string.IsNullOrWhiteSpace(batchName))
            {
                var (found, err) = await DoeAnalysisHelper.FindBatchAsync(repo, batchName).ConfigureAwait(false);
                if (found == null) return AgentToolResult.Fail(err);
                cfg = FactorTransform.ExtractFromDesignConfig(found.DesignConfigJson);
                if (cfg == null)
                    return AgentToolResult.Fail($"批次「{found.BatchName}」没有化学家坐标系配置,无法按物理量调参。");
                cfgSource = found.BatchName;
            }
            else
            {
                var recent = await repo.GetRecentBatchesAsync(200).ConfigureAwait(false) ?? new List<DOEBatch>();
                foreach (var b in recent)
                {
                    cfg = FactorTransform.ExtractFromDesignConfig(b.DesignConfigJson);
                    if (cfg != null) { cfgSource = b.BatchName; break; }
                }
                if (cfg == null)
                    return AgentToolResult.Fail(
                        "没有找到任何带化学家坐标系配置的批次,无法换算泵速。" +
                        "请先通过 preview_doe_design(带 transform)建一个化学家坐标系批次,常数快照才有出处;" +
                        "或者直接告诉用户需要提供反应器体积与进料浓度。");
            }

            if (cond == "equivalence_ratio" && string.IsNullOrEmpty(cfg.EqFactor))
                return AgentToolResult.Fail($"批次「{cfgSource}」的配置是单进料模式(只有停留时间),没有当量比可调。");

            // 2) 读当前泵速(流程参数当前值)
            List<FactorCandidate> candidates;
            try
            {
                candidates = _ctx.Resolve<IFlowParameterProvider>().GetFactorCandidates()
                             ?? new List<FactorCandidate>();
            }
            catch (Exception ex)
            {
                return AgentToolResult.Fail("读取流程参数失败(流程可能未打开):" + ex.Message);
            }

            double? flowA = ReadFlow(candidates, cfg.FeedA.ParameterId);
            double? flowB = cfg.FeedB != null ? ReadFlow(candidates, cfg.FeedB.ParameterId) : null;
            if (flowA == null || (!string.IsNullOrEmpty(cfg.EqFactor) && flowB == null))
                return AgentToolResult.Fail(
                    $"当前流程里找不到配置中的泵参数({cfg.FeedA.ParameterId}" +
                    (cfg.FeedB != null ? $" / {cfg.FeedB.ParameterId}" : "") +
                    ")。请确认打开的流程与批次「" + cfgSource + "」是同一个。");

            // 多泵:其他固定进料以流程当前实况为准(读不到才退回配置快照);它们计入 τ 但绝不被调整
            double otherTotal = 0;
            var otherNotes = new List<string>();
            if (cfg.OtherFeeds != null)
            {
                foreach (var of in cfg.OtherFeeds)
                {
                    double live = ReadFlow(candidates, of.ParameterId) ?? of.FixedFlow;
                    of.FixedFlow = live; // 解算与展示统一按实况口径
                    otherTotal += Math.Max(0, live);
                    otherNotes.Add($"{of.DisplayName}={live:0.####}");
                }
            }

            var current = FactorTransform.ComputeDerived(cfg, flowA.Value, flowB, otherTotal);
            if (current == null)
                return AgentToolResult.Fail("当前泵速无法正算出停留时间/当量比(流速为 0 或配置异常),请先手动确认泵参数。");

            // 3) 目标派生值 = 当前值中替换要改的那一个,另一个保持不变
            var targetValues = new Dictionary<string, double> { [cfg.TauFactor] = current.Value.Tau };
            if (!string.IsNullOrEmpty(cfg.EqFactor))
                targetValues[cfg.EqFactor] = current.Value.Eq ?? 0;
            targetValues[cond == "residence_time" ? cfg.TauFactor : cfg.EqFactor] = target.Value;

            string serr = FactorTransform.SolveDeviceValues(cfg, targetValues, out var device);
            if (serr != null)
                return AgentToolResult.Fail(
                    $"目标条件超出设备能力,未做任何修改:{serr}。" +
                    "请向用户说明设备边界(τ 越小总流速越大,越容易撞泵上限),给出可行的最近值。");

            // 只写两股变量泵:固定进料是常数口径,用户没让动就绝不动
            if (cfg.OtherFeeds != null)
                foreach (var of in cfg.OtherFeeds) device.Remove(of.ParameterId);

            // 4) 一次批量写入(避免两泵分次写造成当量比瞬时错误)
            bool ok = false;
            try
            {
                var inject = device.ToDictionary(kv => kv.Key, kv => (object)kv.Value);
                AgentToolContext.RunOnUi(() =>
                    ok = _ctx.Resolve<IFlowParameterProvider>().InjectParameters(inject));
            }
            catch (Exception ex)
            {
                return AgentToolResult.Fail("写入流程参数失败:" + ex.Message);
            }
            if (!ok) return AgentToolResult.Fail("流程参数写入被拒绝(流程可能正在运行或未打开),未做修改。");

            // 写后回读校验:适配器对未知参数是跳过而非报错(部分写入也返回成功),逐项核对堵住半态
            try
            {
                var verify = _ctx.Resolve<IFlowParameterProvider>().GetFactorCandidates() ?? new List<FactorCandidate>();
                foreach (var kv in device)
                {
                    double? rb = ReadFlow(verify, kv.Key);
                    if (rb == null || Math.Abs(rb.Value - kv.Value) > 1e-6)
                        return AgentToolResult.Fail(
                            $"写入后回读校验失败:{kv.Key} 期望 {kv.Value:0.####},回读 {(rb.HasValue ? rb.Value.ToString("0.####") : "读不到")}。" +
                            "可能只写入了部分泵(半态,当量比不对),请让用户在流程画布上人工核对两泵设定值后再运行流程。");
                }
            }
            catch { }

            _logger.LogInformation($"化学家坐标调参: {cond} -> {target.Value}, 写入 {string.Join(",", device.Select(kv => $"{kv.Key}={kv.Value}"))}");

            // 5) 双空间 diff 汇报
            device.TryGetValue(cfg.FeedA.ParameterId, out double newA);
            double? newB = cfg.FeedB != null && device.TryGetValue(cfg.FeedB.ParameterId, out double nb) ? nb : (double?)null;
            var after = FactorTransform.ComputeDerived(cfg, newA, newB, otherTotal);

            var sb = new StringBuilder();
            sb.AppendLine("工艺条件已按化学家坐标联动调整(写入流程节点参数,下次运行流程生效):");
            sb.AppendLine();
            sb.AppendLine("| 量 | 调整前 | 调整后 |");
            sb.AppendLine("|---|---|---|");
            sb.AppendLine($"| 停留时间(min) | {current.Value.Tau:0.###} | {(after?.Tau ?? 0):0.###} |");
            if (!string.IsNullOrEmpty(cfg.EqFactor))
                sb.AppendLine($"| 当量比 | {(current.Value.Eq ?? 0):0.###} | {(after?.Eq ?? 0):0.###} |");
            sb.AppendLine($"| {cfg.FeedA.DisplayName}(mL/min) | {flowA.Value:0.####} | {newA:0.####} |");
            if (cfg.FeedB != null && flowB.HasValue && newB.HasValue)
                sb.AppendLine($"| {cfg.FeedB.DisplayName}(mL/min) | {flowB.Value:0.####} | {newB.Value:0.####} |");
            sb.AppendLine();
            if (otherNotes.Count > 0)
                sb.AppendLine($"其他固定进料(按当前实况计入停留时间,本次未改动):{string.Join(",", otherNotes)} mL/min。");
            sb.AppendLine($"常数快照来自批次「{cfgSource}」({FactorTransform.DescribeConstants(cfg)})。");
            sb.AppendLine("请把上表双空间对照原样转达用户;若用户要立即生效,提醒需要运行流程。");
            return AgentToolResult.Ok(sb.ToString());
        }

        private static double? ReadFlow(List<FactorCandidate> candidates, string parameterId)
        {
            var c = candidates.FirstOrDefault(x => x.ParameterId == parameterId);
            if (c?.CurrentValue == null) return null;
            if (c.CurrentValue is double d) return d;
            return double.TryParse(c.CurrentValue.ToString(), out var v) ? v : (double?)null;
        }
    }
}

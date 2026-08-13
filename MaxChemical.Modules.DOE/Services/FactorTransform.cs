using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MaxChemical.Modules.DOE.Services
{
    /// <summary>
    /// 进料定义:化学家坐标系里一股进料对应的泵参数与物性。
    /// </summary>
    public sealed class FeedSpec
    {
        /// <summary>流程参数ID(格式 {NodeId}.{ParamName},来自 FactorCandidate.ParameterId)</summary>
        public string ParameterId { get; set; } = "";

        /// <summary>显示名(如 泵A/底物泵),仅用于展示</summary>
        public string DisplayName { get; set; } = "";

        /// <summary>摩尔浓度(两股进料须同单位;单进料模式可为 0)</summary>
        public double Concentration { get; set; }

        /// <summary>泵最小稳定流速(mL/min;0 = 未提供,不校验下限)</summary>
        public double MinFlow { get; set; }

        /// <summary>泵最大流速(mL/min;0 = 未提供,不校验上限)</summary>
        public double MaxFlow { get; set; }

        /// <summary>固定流速(mL/min):仅 OtherFeeds 使用——该股在跑但不参与变化,计入 τ 分母</summary>
        public double FixedFlow { get; set; }
    }

    /// <summary>
    /// 化学家坐标系转换配置:把 DOE 因子空间的物理量(停留时间 τ、当量比 eq)
    /// 映射到设备空间(泵流速)。随批次存入 doe_batches.design_config_json 的
    /// factorTransform 节点,执行器注入前据此反算泵速。
    ///
    /// 数学(闭式解,k = C_B/C_A):
    ///   τ  = V / (F_A + F_B)          → Q = V/τ
    ///   eq = (F_B·C_B) / (F_A·C_A)    → F_A = Q·k/(k+eq),F_B = Q·eq/(k+eq)
    /// 单进料模式(EqFactor 为空):F_A = V/τ。
    /// 单位约定:V 用 mL、流速用 mL/min、τ 用 min(展示单位存于字段,计算不换算)。
    /// </summary>
    public sealed class FactorTransformConfig
    {
        public int Version { get; set; } = 1;

        /// <summary>反应器有效体积(mL,应含混合点到出口的管路死体积)</summary>
        public double ReactorVolume { get; set; }

        public string VolumeUnit { get; set; } = "mL";

        /// <summary>停留时间因子名(必填,须与批次因子同名,单位 min)</summary>
        public string TauFactor { get; set; } = "";

        public string TauUnit { get; set; } = "min";

        /// <summary>当量比因子名(可空 = 单进料模式,只解停留时间)</summary>
        public string EqFactor { get; set; }

        /// <summary>进料A(基准股/底物股,必填)</summary>
        public FeedSpec FeedA { get; set; }

        /// <summary>进料B(试剂股;EqFactor 非空时必填)</summary>
        public FeedSpec FeedB { get; set; }

        /// <summary>
        /// 其他进入同一反应器的固定进料(多泵流程):在跑但流速不变的股(溶剂泵、第三股料等),
        /// 逐股申报固定流速——它们计入 τ 的总流速分母,执行时按固定值强制注入固化。
        /// 已停用的泵不申报(按 0 计)。漏报一股在跑的泵,整轮 τ 全错。
        /// </summary>
        public List<FeedSpec> OtherFeeds { get; set; }

        /// <summary>
        /// 按 τ 动态稳态等待(可空 = 沿用流程固定等待)。随 factorTransform 一起序列化与继承。
        /// </summary>
        public SteadyStateWaitConfig SteadyState { get; set; }
    }

    /// <summary>
    /// 按 τ 动态稳态等待配置:流动化学换条件后,反应器要冲洗约 N 个停留时间才达到新稳态。
    /// 固定等待必须按最长 τ 保守配置,小 τ 组白等;这里按每组 τ 折算保温时长
    /// (dwell = Turnovers × τ),注入流程里指定的等待节点,兑现「按需等待」。
    /// 作用域限化学家坐标系批次(τ 是已知因子);随 factorTransform 一起继承到下一轮。
    /// </summary>
    public sealed class SteadyStateWaitConfig
    {
        public int Version { get; set; } = 1;

        /// <summary>是否启用动态等待(false/未配置 = 沿用流程里节点的固定等待值)。</summary>
        public bool Enabled { get; set; }

        /// <summary>停留时间换算倍数:dwell = Turnovers × τ(经验 3 个停留时间达稳态)。</summary>
        public double Turnovers { get; set; } = 3.0;

        /// <summary>保温时长下限(min;0 = 不设下限)。小 τ 组也不至于短到没冲干净。</summary>
        public double MinWaitMin { get; set; }

        /// <summary>保温时长上限(min;0 = 不设上限)。防大 τ 组把单组时长拖爆。</summary>
        public double MaxWaitMin { get; set; }

        /// <summary>被驱动的流程等待节点ID(该节点须为固定时长 Wait;空 = 不注入,退回固定等待)。</summary>
        public string WaitNodeId { get; set; } = "";

        /// <summary>等待节点显示名(仅回显/播报用,不参与注入定位)。</summary>
        public string WaitNodeName { get; set; } = "";
    }

    /// <summary>
    /// 化学家坐标系换算器(纯函数,无状态):正算(泵速→τ/eq)、反算(τ/eq→泵速)、
    /// 量程校验、随批次配置的序列化。执行器与小桐工具共用,保证两侧口径一致。
    /// </summary>
    public static class FactorTransform
    {
        /// <summary>DesignConfigJson 里的配置节点名。</summary>
        public const string ConfigKey = "factorTransform";

        // ══════════════ 序列化 ══════════════

        public static string ToJson(FactorTransformConfig cfg)
            => JsonConvert.SerializeObject(cfg);

        public static FactorTransformConfig FromJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try { return JsonConvert.DeserializeObject<FactorTransformConfig>(json); }
            catch { return null; }
        }

        /// <summary>从批次 DesignConfigJson 中提取转换配置;没有或解析失败返回 null(普通批次照旧)。</summary>
        public static FactorTransformConfig ExtractFromDesignConfig(string designConfigJson)
        {
            if (string.IsNullOrWhiteSpace(designConfigJson)) return null;
            try
            {
                var root = JObject.Parse(designConfigJson);
                var node = root[ConfigKey];
                if (node == null || node.Type != JTokenType.Object) return null;
                var cfg = node.ToObject<FactorTransformConfig>();
                return Validate(cfg) == null ? cfg : null;
            }
            catch { return null; }
        }

        /// <summary>把转换配置合并进已有的 DesignConfigJson(保留 method/createdBy 等既有字段)。</summary>
        public static string MergeIntoDesignConfig(string designConfigJson, FactorTransformConfig cfg)
        {
            JObject root;
            try { root = string.IsNullOrWhiteSpace(designConfigJson) ? new JObject() : JObject.Parse(designConfigJson); }
            catch { root = new JObject(); }
            root[ConfigKey] = JObject.FromObject(cfg);
            return root.ToString(Formatting.None);
        }

        // ══════════════ 校验 ══════════════

        /// <summary>结构校验:返回错误描述,null = 合法。</summary>
        public static string Validate(FactorTransformConfig cfg)
        {
            if (cfg == null) return "转换配置为空";
            if (cfg.ReactorVolume <= 0) return "反应器有效体积必须大于 0";
            if (string.IsNullOrWhiteSpace(cfg.TauFactor)) return "缺少停留时间因子名(TauFactor)";
            if (cfg.FeedA == null || string.IsNullOrWhiteSpace(cfg.FeedA.ParameterId))
                return "缺少进料A的泵参数ID";
            if (!string.IsNullOrWhiteSpace(cfg.EqFactor))
            {
                if (cfg.FeedB == null || string.IsNullOrWhiteSpace(cfg.FeedB.ParameterId))
                    return "定义了当量比因子,但缺少进料B的泵参数ID";
                if (cfg.FeedA.Concentration <= 0 || cfg.FeedB.Concentration <= 0)
                    return "当量比模式下两股进料的浓度都必须大于 0";
                if (cfg.FeedA.ParameterId == cfg.FeedB.ParameterId)
                    return "进料A与进料B不能是同一个泵参数";
            }

            // 多泵:固定进料逐股校验 + 全体参数ID互斥(同一台泵只能有一个身份)
            if (cfg.OtherFeeds != null)
            {
                var seen = new HashSet<string> { cfg.FeedA.ParameterId };
                if (cfg.FeedB != null && !string.IsNullOrEmpty(cfg.FeedB.ParameterId)) seen.Add(cfg.FeedB.ParameterId);
                foreach (var of in cfg.OtherFeeds)
                {
                    if (of == null || string.IsNullOrWhiteSpace(of.ParameterId))
                        return "固定进料(OtherFeeds)存在缺少泵参数ID的条目";
                    if (of.FixedFlow < 0)
                        return $"固定进料 {FeedName(of)} 的流速不能为负";
                    if (of.MaxFlow > 0 && of.FixedFlow > of.MaxFlow + 1e-9)
                        return $"固定进料 {FeedName(of)} 的流速 {of.FixedFlow:0.####} 超其泵上限 {of.MaxFlow:0.####}";
                    if (!seen.Add(of.ParameterId))
                        return $"泵参数 {of.ParameterId} 被重复申报:同一台泵只能是变量股或固定股之一";
                }
            }
            return null;
        }

        /// <summary>
        /// 稳态等待配置校验:返回错误描述,null = 合法。
        /// WaitNodeId 允许为空:预览阶段会按「停留时间等待节点」标记解析补全,
        /// 执行器对空/失效的节点ID也有标记自愈与固定等待兜底。
        /// </summary>
        public static string ValidateSteadyState(SteadyStateWaitConfig ss)
        {
            if (ss == null || !ss.Enabled) return null; // 未启用视为合法(不做动态等待)
            if (ss.Turnovers <= 0) return "停留时间换算倍数(turnovers)必须大于 0";
            if (ss.MinWaitMin < 0) return "保温时长下限不能为负";
            if (ss.MaxWaitMin < 0) return "保温时长上限不能为负";
            if (ss.MaxWaitMin > 0 && ss.MinWaitMin > 0 && ss.MaxWaitMin < ss.MinWaitMin)
                return $"保温时长上限 {ss.MaxWaitMin:0.###} 小于下限 {ss.MinWaitMin:0.###}";
            return null;
        }

        /// <summary>
        /// 按 τ 折算本组保温时长(min):dwell = Turnovers × τ,再夹到 [Min, Max]。
        /// 未启用或配置非法或 τ 无效时返回 null(执行器据此不注入,退回固定等待)。
        /// </summary>
        public static double? ComputeDwellMinutes(SteadyStateWaitConfig ss, double tauMinutes)
        {
            if (ss == null || !ss.Enabled) return null;
            if (ss.Turnovers <= 0 || tauMinutes <= 0) return null;
            double dwell = ss.Turnovers * tauMinutes;
            if (ss.MinWaitMin > 0 && dwell < ss.MinWaitMin) dwell = ss.MinWaitMin;
            if (ss.MaxWaitMin > 0 && dwell > ss.MaxWaitMin) dwell = ss.MaxWaitMin;
            return Math.Round(dwell, 3);
        }

        /// <summary>该因子名是否为派生因子(执行时经转换层反算,不直接注入)。</summary>
        public static bool IsDerivedFactor(FactorTransformConfig cfg, string factorName)
            => cfg != null && !string.IsNullOrEmpty(factorName) &&
               (string.Equals(factorName, cfg.TauFactor, StringComparison.Ordinal) ||
                (!string.IsNullOrEmpty(cfg.EqFactor) && string.Equals(factorName, cfg.EqFactor, StringComparison.Ordinal)));

        // ══════════════ 反算:化学家空间 → 设备空间 ══════════════

        /// <summary>
        /// 由因子值(含 τ、eq)反算各泵流速。返回错误描述(null = 成功);
        /// device 输出 {ParameterId: 流速},即使量程越界也会填入(供预览展示越界幅度)。
        /// </summary>
        public static string SolveDeviceValues(FactorTransformConfig cfg,
            IReadOnlyDictionary<string, double> factorValues,
            out Dictionary<string, double> device)
        {
            device = new Dictionary<string, double>();
            string verr = Validate(cfg);
            if (verr != null) return verr;

            if (!factorValues.TryGetValue(cfg.TauFactor, out double tau))
                return $"因子值里没有停留时间「{cfg.TauFactor}」";
            if (tau <= 0) return $"停留时间必须大于 0(当前 {tau})";

            // 多泵:τ 的分母是进反应器的全部流量,先扣除固定进料,剩余才归两股变量泵分
            double otherTotal = 0;
            if (cfg.OtherFeeds != null)
                foreach (var of in cfg.OtherFeeds) otherTotal += Math.Max(0, of.FixedFlow);

            double qTotal = cfg.ReactorVolume / tau; // 全部进料总流速
            double q = qTotal - otherTotal;          // 变量泵可分配流速
            if (q <= 1e-12)
                return $"停留时间 {tau:0.###} 需要总流速 {qTotal:0.####} mL/min,而固定进料已占 {otherTotal:0.####} mL/min," +
                       "变量泵没有流量可分——请增大停留时间下界,或调低/停用部分固定进料。";

            double fa, fb = 0;
            bool twoFeeds = !string.IsNullOrWhiteSpace(cfg.EqFactor);
            if (twoFeeds)
            {
                if (!factorValues.TryGetValue(cfg.EqFactor, out double eq))
                    return $"因子值里没有当量比「{cfg.EqFactor}」";
                if (eq < 0) return $"当量比不能为负(当前 {eq})";

                double k = cfg.FeedB.Concentration / cfg.FeedA.Concentration;
                fa = q * k / (k + eq);
                fb = q * eq / (k + eq);
            }
            else
            {
                fa = q;
            }

            device[cfg.FeedA.ParameterId] = Math.Round(fa, 4);
            if (twoFeeds) device[cfg.FeedB.ParameterId] = Math.Round(fb, 4);

            // 固定进料随每组强制注入固化(τ 的分母依赖它,不能靠人保证);申报为 0 的股不触碰
            if (cfg.OtherFeeds != null)
                foreach (var of in cfg.OtherFeeds)
                    if (of.FixedFlow > 0)
                        device[of.ParameterId] = Math.Round(of.FixedFlow, 4);

            // 量程校验(限值未提供则不校验该端)
            var violations = new List<string>();
            CheckRange(cfg.FeedA, fa, violations);
            if (twoFeeds) CheckRange(cfg.FeedB, fb, violations);
            return violations.Count == 0 ? null : string.Join(";", violations);
        }

        private static void CheckRange(FeedSpec feed, double flow, List<string> violations)
        {
            string name = string.IsNullOrEmpty(feed.DisplayName) ? feed.ParameterId : feed.DisplayName;
            if (feed.MaxFlow > 0 && flow > feed.MaxFlow + 1e-9)
                violations.Add($"{name} 需 {flow:0.####} mL/min,超上限 {feed.MaxFlow:0.####}");
            if (feed.MinFlow > 0 && flow < feed.MinFlow - 1e-9)
                violations.Add($"{name} 需 {flow:0.####} mL/min,低于最小稳定流速 {feed.MinFlow:0.####}");
        }

        // ══════════════ 正算:设备空间 → 化学家空间 ══════════════

        /// <summary>
        /// 由当前泵流速正算 (τ, eq)。otherFlowsTotal 为其他固定进料的实际流速之和(多泵流程必传,
        /// 漏掉在跑的股 τ 就偏大)。单进料模式 eq 返回 null。失败返回 null。
        /// </summary>
        public static (double Tau, double? Eq)? ComputeDerived(FactorTransformConfig cfg, double flowA, double? flowB,
            double otherFlowsTotal = 0)
        {
            if (Validate(cfg) != null) return null;
            bool twoFeeds = !string.IsNullOrWhiteSpace(cfg.EqFactor);
            double q = flowA + (twoFeeds ? (flowB ?? 0) : 0) + Math.Max(0, otherFlowsTotal);
            if (q <= 0) return null;
            double tau = cfg.ReactorVolume / q;
            if (!twoFeeds) return (tau, null);
            if (flowA <= 0 || !flowB.HasValue) return null;
            double eq = (flowB.Value * cfg.FeedB.Concentration) / (flowA * cfg.FeedA.Concentration);
            return (tau, eq);
        }

        // ══════════════ 展示 ══════════════

        /// <summary>常数快照一行(报告页脚/确认回显用)。</summary>
        public static string DescribeConstants(FactorTransformConfig cfg)
        {
            if (cfg == null) return "";
            var parts = new List<string>
            {
                $"V={cfg.ReactorVolume:0.####} {cfg.VolumeUnit}",
                $"进料A[{FeedName(cfg.FeedA)}] C={cfg.FeedA?.Concentration:0.####}"
            };
            if (!string.IsNullOrWhiteSpace(cfg.EqFactor))
                parts.Add($"进料B[{FeedName(cfg.FeedB)}] C={cfg.FeedB?.Concentration:0.####}");
            if (cfg.OtherFeeds != null && cfg.OtherFeeds.Count > 0)
                parts.Add("固定进料:" + string.Join("/", cfg.OtherFeeds.Select(of => $"{FeedName(of)}={of.FixedFlow:0.####}")));
            parts.Add($"τ因子={cfg.TauFactor}({cfg.TauUnit})");
            if (!string.IsNullOrWhiteSpace(cfg.EqFactor)) parts.Add($"eq因子={cfg.EqFactor}");
            return string.Join(",", parts);
        }

        private static string FeedName(FeedSpec f)
            => f == null ? "?" : (string.IsNullOrEmpty(f.DisplayName) ? f.ParameterId : f.DisplayName);
    }
}

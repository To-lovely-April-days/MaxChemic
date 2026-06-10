using System;

namespace MaxChemical.Modules.DOE.Models
{
    /// <summary>
    /// 智能决策引擎日志 — 记录每次自动决策的完整上下文
    /// 
    /// 每当决策引擎在一轮实验完成后做出决策，就生成一条日志。
    /// 用户可以在项目历史中回顾每一步决策的依据和结果。
    /// 
    /// 日志链条构成项目的"决策链"：
    ///   Round 1 → 筛选, 淘汰因子 D/E → Round 2 → 爬坡 → Round 3 → RSM → ...
    /// </summary>
    public class AutoPilotLog
    {
        public int Id { get; set; }

        /// <summary>关联的项目 ID</summary>
        public string ProjectId { get; set; } = string.Empty;

        /// <summary>触发决策的批次 ID</summary>
        public string BatchId { get; set; } = string.Empty;

        /// <summary>轮次序号</summary>
        public int RoundNumber { get; set; }

        /// <summary>决策时间</summary>
        public DateTime Timestamp { get; set; } = DateTime.Now;

        // ══════════════ 输入（决策依据） ══════════════

        /// <summary>
        /// 决策触发时的项目阶段
        /// </summary>
        public DOEProjectPhase PhaseAtDecision { get; set; }

        /// <summary>
        /// 输入摘要 JSON — 做决策时的所有指标快照
        /// 格式: {
        ///   "r2": 0.85, "r2_adj": 0.82, "r2_pred": 0.78,
        ///   "lof_p": 0.12, "composite_d": 0.65,
        ///   "active_factors": 5, "total_runs_so_far": 48,
        ///   "curvature_significant": false,
        ///   "factor_ranking": [
        ///     {"name": "温度", "p": 0.001, "significant": true},
        ///     {"name": "压力", "p": 0.23, "significant": false}
        ///   ],
        ///   "boundary_status": {"温度": "interior", "压力": "at_upper"},
        ///   "previous_r2": 0.78
        /// }
        /// </summary>
        public string InputSummaryJson { get; set; } = "{}";

        // ══════════════ 输出（决策结果） ══════════════

        /// <summary>
        /// 决策类型 — 对应 NextStepRecommendation 枚举
        /// </summary>
        public NextStepRecommendation DecisionType { get; set; }

        /// <summary>
        /// 决策详情 JSON — 具体执行了什么操作
        /// 格式: {
        ///   "screened_out_factors": ["因子D", "因子E"],
        ///   "expanded_factors": [{"name": "温度", "old_lower": 80, "old_upper": 120, "new_lower": 80, "new_upper": 156}],
        ///   "fixed_factors": [{"name": "催化剂", "value": "B"}],
        ///   "new_center_point": {"温度": 118, "压力": 3.2}
        /// }
        /// </summary>
        public string DecisionDetailJson { get; set; } = "{}";

        /// <summary>
        /// 决策理由 — 自然语言描述，用户可读
        /// 例: "R²=0.85 合格，LOF p=0.12 不显著（无曲率），3个活跃因子适合做 RSM。
        ///      推荐 Box-Behnken 设计，预计 15 组实验。"
        /// </summary>
        public string Reason { get; set; } = string.Empty;

        // ══════════════ 下一轮方案 ══════════════

        /// <summary>决策后推进到的阶段</summary>
        public DOEProjectPhase NextPhase { get; set; }

        /// <summary>下一轮设计方法</summary>
        public DOEDesignMethod NextDesignMethod { get; set; }

        /// <summary>下一轮因子数</summary>
        public int NextFactorCount { get; set; }

        /// <summary>下一轮预计实验组数</summary>
        public int EstimatedRuns { get; set; }

        /// <summary>
        /// 下一轮自动创建的批次 ID（如果已自动创建）
        /// null 表示等待用户确认
        /// </summary>
        public string? NextBatchId { get; set; }

        // ══════════════ 用户覆盖 ══════════════

        /// <summary>用户是否覆盖了系统推荐</summary>
        public bool UserOverride { get; set; } = false;

        /// <summary>用户覆盖后选择的实际操作</summary>
        public NextStepRecommendation? UserOverrideDecision { get; set; }

        /// <summary>用户覆盖的备注</summary>
        public string? UserOverrideNote { get; set; }

        // ══════════════ 防护规则触发 ══════════════

        /// <summary>
        /// 是否触发了防护规则导致暂停
        /// </summary>
        public bool SafeguardTriggered { get; set; } = false;

        /// <summary>
        /// 触发的防护规则描述
        /// 例: "P1: 已达最大轮次限制(10轮)" 或 "P5: 连续2轮R²改善<0.02"
        /// </summary>
        public string? SafeguardRule { get; set; }
    }
}
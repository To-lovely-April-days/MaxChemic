namespace MaxChemical.Modules.DOE.Models
{
    /// <summary>
    /// 智能决策引擎配置 — 项目级，持久化到数据库
    /// 
    /// 用户可通过 UI 开关启用/禁用，所有参数可手动修改。
    /// 默认值为 DOE 方法论的"黄金标准"（Montgomery 教科书 + Minitab/Design-Expert 最佳实践）。
    /// 
    /// 数据层级: DOEProject 1:1 AutoPilotConfig
    /// </summary>
    public class AutoPilotConfig
    {
        public int Id { get; set; }

        /// <summary>关联的项目 ID</summary>
        public string ProjectId { get; set; } = string.Empty;

        /// <summary>是否启用智能决策（默认 false，用户手动开启）</summary>
        public bool Enabled { get; set; } = false;

        // ══════════════ 筛选阶段参数 ══════════════

        /// <summary>
        /// 筛选显著性阈值 α
        /// p > α 的因子被淘汰。比分析用的 0.05 宽松，避免误杀。
        /// 默认: 0.10
        /// </summary>
        public double AlphaScreen { get; set; } = 0.10;

        // ══════════════ 模型质量参数 ══════════════

        /// <summary>
        /// 模型合格 R² 阈值
        /// R² < 此值时触发补点（AugmentDesign）。
        /// 默认: 0.80
        /// </summary>
        public double R2Min { get; set; } = 0.80;

        /// <summary>
        /// 模型优秀 R² 阈值
        /// R² ≥ 此值时，如果目标也达成，触发验证实验。
        /// 默认: 0.90
        /// </summary>
        public double R2Good { get; set; } = 0.90;

        /// <summary>
        /// Lack-of-Fit 显著性阈值
        /// LOF p < 此值表示模型失拟（存在更高阶效应未被捕获）。
        /// 默认: 0.05
        /// </summary>
        public double AlphaLOF { get; set; } = 0.05;

        /// <summary>
        /// R² 连续不改善最小增量
        /// 连续 2 轮 R² 改善 < 此值时暂停，避免无效迭代。
        /// 默认: 0.02
        /// </summary>
        public double R2ImprovementMin { get; set; } = 0.02;

        // ══════════════ 目标达成参数 ══════════════

        /// <summary>
        /// Desirability 达成阈值
        /// 综合 D ≥ 此值视为目标达成，推荐进入验证阶段。
        /// 默认: 0.80
        /// </summary>
        public double DTarget { get; set; } = 0.80;

        // ══════════════ 验证阶段参数 ══════════════

        /// <summary>
        /// 验证实验重复次数
        /// 在预测最优点做 N 次重复实验，用 t 检验判断模型可靠性。
        /// 默认: 3
        /// </summary>
        public int ConfirmationReps { get; set; } = 3;

        // ══════════════ 防护参数 ══════════════

        /// <summary>
        /// 最大迭代轮次
        /// 超过后强制暂停，防止无限循环。
        /// 默认: 10
        /// </summary>
        public int MaxRounds { get; set; } = 10;

        /// <summary>
        /// 最大总实验组数
        /// 累计实验组数超过此值后暂停，成本控制。
        /// 默认: 200
        /// </summary>
        public int MaxTotalRuns { get; set; } = 200;

        /// <summary>
        /// 范围扩展比例
        /// 最优点在因子边界上时，扩展该因子范围的百分比。
        /// 例如 0.30 表示扩展 30%。
        /// 默认: 0.30
        /// </summary>
        public double RangeExpansionRatio { get; set; } = 0.30;

        /// <summary>
        /// 同一操作连续执行上限
        /// 连续 N 轮产生相同推荐时暂停，提示模型结构可能不对。
        /// 默认: 3
        /// </summary>
        public int MaxConsecutiveSameAction { get; set; } = 3;

        // ══════════════ 便利方法 ══════════════

        /// <summary>
        /// 创建默认配置（黄金标准）
        /// </summary>
        public static AutoPilotConfig CreateDefault(string projectId) => new()
        {
            ProjectId = projectId,
            Enabled = false,
            AlphaScreen = 0.10,
            R2Min = 0.80,
            R2Good = 0.90,
            AlphaLOF = 0.05,
            R2ImprovementMin = 0.02,
            DTarget = 0.80,
            ConfirmationReps = 3,
            MaxRounds = 10,
            MaxTotalRuns = 200,
            RangeExpansionRatio = 0.30,
            MaxConsecutiveSameAction = 3
        };
    }
}
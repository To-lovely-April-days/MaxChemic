using System.Collections.Generic;

namespace MaxChemical.Modules.DOE.Models
{
    /// <summary>
    /// ★ 模型精简信息 — 部分因子 / 全因子 backward elimination 结果
    ///
    /// 两类项被删除:
    ///   1. 因混杂(alias/共线)自动删除: 总是发生,和 backward 阈值无关
    ///      - 和 Ct Pt 列完全相同的项 (e.g. 7 因子 Res IV 里的 4 阶交互)
    ///      - 和其他项完全混杂的项 (e.g. Res IV 里 AB = CD)
    ///   2. 因 backward p 值超过阈值删除: 受 BackwardThreshold 控制
    ///      - 阈值 = 0 时不做 backward, 输出完整候选模型
    ///      - 阈值 > 0 时逐轮剔除 p 最大的交互项直到全部 ≤ 阈值
    /// </summary>
    public class ModelReductionInfo
    {
        /// <summary>
        /// 本次 backward 使用的 p 值阈值
        /// 0 表示不做 backward (对标 Minitab 行为, 显示所有可估计项)
        /// > 0 表示做 backward (默认 0.05, 开箱即用干净结果)
        /// </summary>
        public double BackwardThreshold { get; set; }

        /// <summary>
        /// 候选项全集的总数 (不含截距)
        /// 例: 7 因子 Res IV → 主效应 7 + 所有 2fi C(7,2)=21 + ... = 很多
        /// 数据量不足时,OlsSolver 自动剔除共线项后的实际可估计项数
        /// </summary>
        public int FullCandidateCount { get; set; }

        /// <summary>
        /// 最终保留的项数 (不含截距, 含 Ct Pt)
        /// </summary>
        public int FinalTermCount { get; set; }

        /// <summary>
        /// 因混杂(alias)自动删除的项列表
        /// 注: OlsSolver 的 SVD 识别不到具体"和谁混杂",所以 Reason 字段可能
        ///     是通用描述,如 "与其他项共线,不可估计"
        /// </summary>
        public List<AliasDropEntry> AliasDroppedTerms { get; set; } = new();

        /// <summary>
        /// 因 backward (p > 阈值) 删除的项列表, 按剔除顺序排列
        /// </summary>
        public List<BackwardDropEntry> BackwardDroppedTerms { get; set; } = new();

        /// <summary>
        /// 未做 backward 时的完整候选模型 R²
        /// </summary>
        public double FullModelR2 { get; set; }

        /// <summary>
        /// 未做 backward 时的完整候选模型 R²adj
        /// </summary>
        public double FullModelR2Adj { get; set; }

        /// <summary>
        /// 精简后 R² (阈值 = 0 时等于 FullModelR2)
        /// </summary>
        public double ReducedModelR2 { get; set; }

        /// <summary>
        /// 精简后 R²adj
        /// </summary>
        public double ReducedModelR2Adj { get; set; }

        /// <summary>
        /// backward 迭代轮数 (= BackwardDroppedTerms.Count)
        /// </summary>
        public int IterationCount { get; set; }

        /// <summary>
        /// 精简过程的可读摘要, 供 UI 直接显示
        /// 示例:
        ///   "候选 19 项 → 精简后 8 项
        ///    Alias 删除 3 项, Backward 删除 8 项 (阈值 p > 0.05)
        ///    R²: 0.9947 → 0.9887,  R²adj: 0.9512 → 0.9801 ↑"
        /// </summary>
        public string Summary { get; set; } = "";
    }

    /// <summary>
    /// 因混杂/共线自动删除的项
    /// </summary>
    public class AliasDropEntry
    {
        /// <summary>被删除的项名</summary>
        public string TermName { get; set; } = "";

        /// <summary>删除原因 (通用描述)</summary>
        public string Reason { get; set; } = "";
    }

    /// <summary>
    /// 因 backward p 值超过阈值删除的项
    /// </summary>
    public class BackwardDropEntry
    {
        /// <summary>第几轮被剔除 (从 1 开始)</summary>
        public int Iteration { get; set; }

        /// <summary>被剔除的项名</summary>
        public string TermName { get; set; } = "";

        /// <summary>剔除时的 p 值</summary>
        public double PValue { get; set; }

        /// <summary>剔除后重拟合的 R²</summary>
        public double R2AfterDrop { get; set; }

        /// <summary>剔除后重拟合的 R²adj</summary>
        public double R2AdjAfterDrop { get; set; }
    }
}
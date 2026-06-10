using System;

namespace MaxChemical.Modules.DOE.Services.Designs
{
    /// <summary>
    /// 所有设计方法的通用选项基类。
    /// 对标 Minitab "Create Factorial/Response Surface Design" 对话框。
    /// </summary>
    public abstract class DesignOptions
    {
        /// <summary>仿行数 (Replicates) — 整个设计重复多少遍</summary>
        public int Replicates { get; set; } = 1;

        /// <summary>区组数 (Blocks)</summary>
        public int BlockCount { get; set; } = 1;

        /// <summary>
        /// 中心点数 (Center points)。
        /// 语义与 Minitab 一致：
        ///   对于 Q 个类别因子，实际生成的中心点数 = CenterPointCount × 2^Q
        ///   （连续因子取中心值，类别因子遍历所有组合）
        /// </summary>
        public int CenterPointCount { get; set; } = 0;

        /// <summary>是否随机化运行顺序</summary>
        public bool Randomize { get; set; } = false;

        /// <summary>随机化种子 (null = 使用时间种子)</summary>
        public int? RandomSeed { get; set; }
    }

    /// <summary>全因子设计选项</summary>
    public sealed class FullFactorialOptions : DesignOptions
    {
        /// <summary>每个连续因子的水平数 (2 或 3)</summary>
        public int LevelCount { get; set; } = 2;
    }

    /// <summary>部分因子设计选项</summary>
    public sealed class FractionalFactorialOptions : DesignOptions
    {
        /// <summary>
        /// 实验运行数 (2^(k-p))。
        /// 必须是 2 的幂且 ≥ k+1。例如 k=7 可选 8/16/32/64。
        /// </summary>
        public int RunCount { get; set; }
    }

    /// <summary>Plackett-Burman 设计选项</summary>
    public sealed class PlackettBurmanOptions : DesignOptions
    {
        /// <summary>
        /// 实验运行数。必须是 4 的倍数且 ≥ k+1。
        /// 支持: 12, 20, 24, 28, 36, 44, 48。
        /// </summary>
        public int RunCount { get; set; }
    }

    /// <summary>中心复合设计 (CCD) 选项</summary>
    public sealed class CCDOptions : DesignOptions
    {
        /// <summary>
        /// Alpha 类型: "rotatable" | "orthogonal" | "face" | "practical"
        /// </summary>
        public string AlphaType { get; set; } = "rotatable";

        /// <summary>立方块中心点数 (cube center points, nc_cube)。-1 = 自动</summary>
        public int CubeCenterCount { get; set; } = -1;

        /// <summary>轴点块中心点数 (axial center points, nc_axial)。-1 = 自动</summary>
        public int AxialCenterCount { get; set; } = -1;

        /// <summary>角点部分是否使用 1/2 部分因子 (k ≥ 5 时推荐)</summary>
        public bool UseFractional { get; set; } = false;
    }

    /// <summary>Box-Behnken 设计选项</summary>
    public sealed class BoxBehnkenOptions : DesignOptions
    {
        /// <summary>
        /// 中心点数。-1 = 使用 Minitab 推荐值：
        /// k=3:3, k=4:3, k=5:6, k=6:6, k=7:6
        /// </summary>
        public int BbdCenterCount { get; set; } = -1;
    }

    /// <summary>D-Optimal 设计选项</summary>
    public sealed class DOptimalOptions : DesignOptions
    {
        /// <summary>期望的实验运行数 (用户指定，最少 = 模型参数数 + 2)</summary>
        public int RunCount { get; set; }

        /// <summary>模型类型: "linear" | "interaction" | "quadratic"</summary>
        public string ModelType { get; set; } = "quadratic";

        /// <summary>
        /// coordinate-exchange 优化的迭代次数（每次从不同随机起点开始）。
        /// 越大越接近全局最优，默认 20 次重启对大多数问题足够。
        /// </summary>
        public int Restarts { get; set; } = 20;

        /// <summary>每次重启内最大交换迭代次数</summary>
        public int MaxIterations { get; set; } = 100;

        /// <summary>候选点网格密度 (连续因子每维的网格数)。默认 3 (low/mid/high)</summary>
        public int CandidateGridSize { get; set; } = 3;
    }

    /// <summary>Taguchi 正交设计选项</summary>
    public sealed class TaguchiOptions : DesignOptions
    {
        /// <summary>
        /// L 表选择: "auto" 或具体表名
        /// 支持: L4, L8, L9, L12, L16, L16b, L18, L25, L27, L32, L36
        /// </summary>
        public string LArray { get; set; } = "auto";

        /// <summary>
        /// 信噪比类型: "larger" (越大越好) | "smaller" (越小越好) | "nominal" (望目)
        /// 仅影响分析阶段，设计生成阶段不使用
        /// </summary>
        public string SnRatio { get; set; } = "larger";
    }
}

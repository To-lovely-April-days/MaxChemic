using System;
using System.Collections.Generic;
using System.Linq;
using MaxChemical.Modules.DOE.Models;

namespace MaxChemical.Modules.DOE.Services.Designs
{
    /// <summary>
    /// 中心复合设计 (Central Composite Design, CCD) 生成器。
    ///
    /// 对标 Minitab "Create Response Surface Design → Central composite".
    ///
    /// 结构:
    ///   1. 立方角点 (2^k 全因子 或 1/2 部分因子)
    ///   2. 轴点 (2k 个, 在 ±α 处)
    ///   3. 中心点 (nc_cube 立方块中心 + nc_axial 轴点块中心)
    ///
    /// Alpha 类型:
    ///   rotatable (默认): α = (F)^(1/4)    F = 角点数
    ///   orthogonal:       α = √(√(F·(F+T+nc)) − F) / √2   (T = 2k 轴点)
    ///   face / face-centered: α = 1
    ///   practical:        α = k^(1/4)
    ///
    /// 默认中心点数 (Minitab):
    ///   k=2: 5cube + 0axial = 5
    ///   k=3: 6 + 0 = 6
    ///   k=4: 6 + 0 = 6
    ///   k=5: 6 + 0 = 6 (通常 1/2 分数)
    ///   k=6: 6 + 0 = 6 (1/2 分数)
    ///   k=7: 9 + 0 = 9 (1/2 分数)
    ///
    /// 类别因子: 对每种类别组合复制一份完整 CCD。
    /// </summary>
    public class CCDGenerator : IDesignGenerator<CCDOptions>
    {
        private static readonly Dictionary<int, int> _defaultCubeCenter = new()
        {
            { 2, 5 }, { 3, 6 }, { 4, 6 }, { 5, 6 }, { 6, 6 }, { 7, 9 }
        };

        public string? Validate(List<DOEFactor> factors, CCDOptions options)
        {
            if (factors == null || factors.Count == 0)
                return "至少需要 1 个因子";

            var (cont, cat) = DesignPipeline.SplitFactors(factors);
            if (cont.Count < 2) return "CCD 设计至少需要 2 个连续因子";
            if (cont.Count > 7) return "CCD 设计最多支持 7 个连续因子";

            var valid = new[] { "rotatable", "orthogonal", "face", "practical" };
            if (!valid.Contains(options.AlphaType))
                return $"无效的 alpha 类型: {options.AlphaType}";

            foreach (var f in cat)
            {
                if (f.GetCategoryLevelList().Count < 2)
                    return $"类别因子 '{f.FactorName}' 至少需要 2 个水平";
            }

            return null;
        }

        public DOEDesignMatrix Generate(List<DOEFactor> factors, CCDOptions options)
        {
            var err = Validate(factors, options);
            if (err != null) throw new ArgumentException(err);

            var (cont, cat) = DesignPipeline.SplitFactors(factors);
            int k = cont.Count;

            // 决定是否使用 1/2 分数立方部分 (Minitab: k≥5 默认用)
            bool useFractional = options.UseFractional || k >= 5;

            // 1. 生成立方角点 (2^k 或 2^(k-1))
            var cubeCorners = useFractional
                ? GenerateHalfFractionCube(k)
                : GenerateFullCube(k);
            int nCube = cubeCorners.Count;

            // 2. 计算 α
            int nAxial = 2 * k;
            double alpha = ComputeAlpha(k, nCube, nAxial, options);

            // 3. 生成轴点 (2k 个)
            var axialPoints = GenerateAxialPoints(k, alpha);

            // 4. 中心点数
            int ncCube = options.CubeCenterCount < 0
                ? (_defaultCubeCenter.TryGetValue(k, out var v) ? v : 6)
                : options.CubeCenterCount;
            int ncAxial = options.AxialCenterCount < 0 ? 0 : options.AxialCenterCount;

            // 5. 对每种类别组合展开
            var allRows = new List<Dictionary<string, object>>();
            var categoryCombinations = DesignPipeline.EnumerateCategoryCombinations(cat);

            foreach (var catAssign in categoryCombinations)
            {
                // 立方角点
                foreach (var coded in cubeCorners)
                    allRows.Add(DesignPipeline.BuildRow(cont, coded, cat, catAssign));

                // 立方块中心点
                var zero = new double[k];
                for (int i = 0; i < ncCube; i++)
                    allRows.Add(DesignPipeline.BuildRow(cont, zero, cat, catAssign));

                // 轴点
                foreach (var coded in axialPoints)
                    allRows.Add(DesignPipeline.BuildRow(cont, coded, cat, catAssign));

                // 轴点块中心点
                for (int i = 0; i < ncAxial; i++)
                    allRows.Add(DesignPipeline.BuildRow(cont, zero, cat, catAssign));
            }

            // 6. 加 options.CenterPointCount 的补充中心点 (如果用户额外指定)
            // 注: CCD 一般不再用 options.CenterPointCount, 因为有 cubeCenter/axialCenter
            // 但如果用户填了, 追加到每种类别组合
            if (options.CenterPointCount > 0)
            {
                foreach (var catAssign in categoryCombinations)
                {
                    var zero = new double[k];
                    for (int i = 0; i < options.CenterPointCount; i++)
                        allRows.Add(DesignPipeline.BuildRow(cont, zero, cat, catAssign));
                }
            }

            return DesignPipeline.Finalize(factors, allRows, options);
        }

        // ══════════════════════════════════════════════════════
        // 立方角点
        // ══════════════════════════════════════════════════════

        private static List<double[]> GenerateFullCube(int k)
        {
            // Minitab StdOrder: 第一列最慢
            int n = 1 << k;
            var result = new List<double[]>(n);
            for (int row = 0; row < n; row++)
            {
                var r = new double[k];
                for (int col = 0; col < k; col++)
                {
                    int shift = k - 1 - col;
                    int bit = (row >> shift) & 1;
                    r[col] = bit == 0 ? -1.0 : 1.0;
                }
                result.Add(r);
            }
            return result;
        }

        /// <summary>
        /// 生成 1/2 分数立方部分 (2^(k-1))。
        /// 使用生成元 K = ABCD...J (最后一列 = 前 k-1 列乘积), 即 Res V+ 设计。
        /// </summary>
        private static List<double[]> GenerateHalfFractionCube(int k)
        {
            var full = GenerateFullCube(k - 1); // 2^(k-1) × (k-1)
            var result = new List<double[]>(full.Count);
            foreach (var prev in full)
            {
                var r = new double[k];
                Array.Copy(prev, r, k - 1);
                // 最后一列 = 前 k-1 列之积
                double prod = 1.0;
                for (int j = 0; j < k - 1; j++) prod *= prev[j];
                r[k - 1] = prod;
                result.Add(r);
            }
            return result;
        }

        // ══════════════════════════════════════════════════════
        // 轴点
        // ══════════════════════════════════════════════════════

        private static List<double[]> GenerateAxialPoints(int k, double alpha)
        {
            var result = new List<double[]>(2 * k);
            // Minitab 顺序: 对每个因子先 -α 再 +α
            for (int i = 0; i < k; i++)
            {
                var minus = new double[k];
                minus[i] = -alpha;
                result.Add(minus);

                var plus = new double[k];
                plus[i] = alpha;
                result.Add(plus);
            }
            return result;
        }

        // ══════════════════════════════════════════════════════
        // α 计算
        // ══════════════════════════════════════════════════════

        private static double ComputeAlpha(int k, int nCube, int nAxial, CCDOptions opts)
        {
            return opts.AlphaType switch
            {
                "rotatable" => Math.Pow(nCube, 0.25),
                "face" => 1.0,
                "practical" => Math.Pow(k, 0.25),
                "orthogonal" => ComputeOrthogonalAlpha(k, nCube, nAxial, opts),
                _ => Math.Pow(nCube, 0.25)
            };
        }

        /// <summary>
        /// 正交 α: α² = (√(F·(F+T+nc0)) − F) / 2
        /// 其中 F = 立方角点数, T = 2k 轴点, nc0 = 总中心点数
        /// </summary>
        private static double ComputeOrthogonalAlpha(int k, int nCube, int nAxial, CCDOptions opts)
        {
            int nc0 = (opts.CubeCenterCount < 0 ? (_defaultCubeCenter.TryGetValue(k, out var v) ? v : 6) : opts.CubeCenterCount)
                    + (opts.AxialCenterCount < 0 ? 0 : opts.AxialCenterCount);

            double F = nCube;
            double inner = Math.Sqrt(F * (F + nAxial + nc0)) - F;
            double alphaSq = inner / 2.0;
            return Math.Sqrt(Math.Max(alphaSq, 0));
        }
    }
}

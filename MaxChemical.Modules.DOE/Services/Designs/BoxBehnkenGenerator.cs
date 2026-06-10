using System;
using System.Collections.Generic;
using System.Linq;
using MaxChemical.Modules.DOE.Models;

namespace MaxChemical.Modules.DOE.Services.Designs
{
    /// <summary>
    /// Box-Behnken 设计 (BBD) 生成器。
    ///
    /// 对标 Minitab "Create Response Surface Design → Box-Behnken design".
    ///
    /// 特点:
    ///   - 3 水平设计 (每因子 -1, 0, +1)
    ///   - 无极端角点 (没有所有因子同时取 ±1 的点) —— 化工安全性好
    ///   - 支持 3~7 连续因子 (有标准表)
    ///   - 无法拟合所有可能的 2fi+quadratic 模型但对标准 RSM 足够
    ///
    /// 标准角点数 (Minitab):
    ///   k=3: 12 角点 + 3 中心点 = 15 runs
    ///   k=4: 24 + 3 = 27 runs
    ///   k=5: 40 + 6 = 46 runs
    ///   k=6: 48 + 6 = 54 runs
    ///   k=7: 56 + 6 = 62 runs
    ///
    /// 类别因子处理:
    ///   Minitab RSM 支持类别因子时,对每种类别组合复制一份完整 BBD。
    ///   例:3 连续 + 2 类别 2 水平 → (12+3) × 4 = 60 runs
    /// </summary>
    public class BoxBehnkenGenerator : IDesignGenerator<BoxBehnkenOptions>
    {
        // 标准块定义 (每个块是 3 个连续因子的索引, 对它们取 2^3=8 组合, 其余因子取 0)
        // 对于 k=3,4: 使用所有 C(k,2) 对, 每对 4 个组合 (所以每块用 2 因子, 非 3 因子)
        // 对于 k=5: 使用所有 C(5,2)=10 对
        // 对于 k=6,7: 使用 BIBD 块设计 (3 因子块)
        private static readonly Dictionary<int, int[][]> _pairBlocks = new()
        {
            // k=3: C(3,2) = 3 对
            { 3, new[] {
                new[] {0,1}, new[] {0,2}, new[] {1,2}
            }},
            // k=4: C(4,2) = 6 对
            { 4, new[] {
                new[] {0,1}, new[] {0,2}, new[] {0,3},
                new[] {1,2}, new[] {1,3}, new[] {2,3}
            }},
            // k=5: C(5,2) = 10 对
            { 5, new[] {
                new[] {0,1}, new[] {0,2}, new[] {0,3}, new[] {0,4},
                new[] {1,2}, new[] {1,3}, new[] {1,4},
                new[] {2,3}, new[] {2,4},
                new[] {3,4}
            }},
        };

        // k=6: BIBD 3-因子块 (Youden 排列)
        private static readonly int[][] _tripleBlocksK6 = new[]
        {
            new[] {0,1,3}, new[] {1,2,4}, new[] {2,3,5},
            new[] {3,4,0}, new[] {4,5,1}, new[] {5,0,2}
        };

        // k=7: BIBD = Fano 平面的 7 条线
        private static readonly int[][] _tripleBlocksK7 = new[]
        {
            new[] {0,1,2}, new[] {0,3,4}, new[] {0,5,6},
            new[] {1,3,5}, new[] {1,4,6},
            new[] {2,3,6}, new[] {2,4,5}
        };

        // Minitab 默认中心点数
        private static readonly Dictionary<int, int> _defaultCenterCount = new()
        {
            { 3, 3 }, { 4, 3 }, { 5, 6 }, { 6, 6 }, { 7, 6 }
        };

        public string? Validate(List<DOEFactor> factors, BoxBehnkenOptions options)
        {
            if (factors == null || factors.Count == 0)
                return "至少需要 1 个因子";

            var (cont, cat) = DesignPipeline.SplitFactors(factors);
            if (cont.Count < 3) return "Box-Behnken 设计至少需要 3 个连续因子";
            if (cont.Count > 7) return "Box-Behnken 设计最多支持 7 个连续因子";

            // 类别因子必须有明确水平 (会通过全组合展开)
            foreach (var f in cat)
            {
                if (f.GetCategoryLevelList().Count < 2)
                    return $"类别因子 '{f.FactorName}' 至少需要 2 个水平";
            }

            return null;
        }

        public DOEDesignMatrix Generate(List<DOEFactor> factors, BoxBehnkenOptions options)
        {
            var err = Validate(factors, options);
            if (err != null) throw new ArgumentException(err);

            var (cont, cat) = DesignPipeline.SplitFactors(factors);
            int k = cont.Count;

            int centerCount = options.BbdCenterCount;
            if (centerCount < 0) centerCount = _defaultCenterCount[k];

            // 1. 生成连续因子的基础编码矩阵 (角点, 不含中心点)
            var cornerCoded = GenerateBbdCorners(k);

            // 2. 类别因子全组合展开 (每种组合一份完整 BBD)
            //    每个类别组合内: 基础角点 + centerCount 个中心点
            var allRows = new List<Dictionary<string, object>>();
            var categoryCombinations = DesignPipeline.EnumerateCategoryCombinations(cat);

            foreach (var catAssign in categoryCombinations)
            {
                // 角点
                foreach (var coded in cornerCoded)
                    allRows.Add(DesignPipeline.BuildRow(cont, coded, cat, catAssign));

                // 该类别组合下的中心点
                var zero = new double[k];
                for (int i = 0; i < centerCount; i++)
                    allRows.Add(DesignPipeline.BuildRow(cont, zero, cat, catAssign));
            }

            // 3. 仿行/随机化 (Finalize 会处理)
            return DesignPipeline.Finalize(factors, allRows, options);
        }

        /// <summary>生成 k 因子 BBD 的角点编码矩阵 (不含中心点)。</summary>
        private static List<double[]> GenerateBbdCorners(int k)
        {
            var rows = new List<double[]>();

            if (_pairBlocks.TryGetValue(k, out var pairs))
            {
                // 两因子块: 每对 4 个组合
                foreach (var pair in pairs)
                {
                    for (int xi = -1; xi <= 1; xi += 2)
                    {
                        for (int xj = -1; xj <= 1; xj += 2)
                        {
                            var row = new double[k];
                            row[pair[0]] = xi;
                            row[pair[1]] = xj;
                            rows.Add(row);
                        }
                    }
                }
            }
            else
            {
                // 三因子块 (k=6, 7)
                int[][] blocks = k == 6 ? _tripleBlocksK6 : _tripleBlocksK7;
                foreach (var block in blocks)
                {
                    for (int xa = -1; xa <= 1; xa += 2)
                    {
                        for (int xb = -1; xb <= 1; xb += 2)
                        {
                            for (int xc = -1; xc <= 1; xc += 2)
                            {
                                var row = new double[k];
                                row[block[0]] = xa;
                                row[block[1]] = xb;
                                row[block[2]] = xc;
                                rows.Add(row);
                            }
                        }
                    }
                }
            }
            return rows;
        }
    }
}

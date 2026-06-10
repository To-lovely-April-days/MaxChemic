using System.Collections.Generic;
using System.Linq;
using MaxChemical.Modules.DOE.Models;

namespace MaxChemical.Modules.DOE.Services.Designs
{
    /// <summary>
    /// 全因子设计 (Full Factorial) 生成器。
    ///
    /// 对标 Minitab "Create Factorial Design → 2-level / 3-level factorial".
    ///
    /// 算法:
    ///   1. 对连续因子生成 L^k 个编码组合 (L = 2 或 3, k = 连续因子数)
    ///   2. 对类别因子全组合遍历 (每种组合复制一份基础矩阵)
    ///   3. 中心点按 Minitab 规则: nc × 2^Q 个 (Q = 类别因子数)
    ///
    /// 典型用例:
    ///   - 3 连续因子 2 水平 + 3 中心点 = 8 + 3 = 11 组
    ///   - 3 连续因子 + 2 类别因子 (2 水平) 2 水平 + 3 中心点 = 8×4 + 3×4 = 44 组
    /// </summary>
    public class FullFactorialGenerator : IDesignGenerator<FullFactorialOptions>
    {
        public string? Validate(List<DOEFactor> factors, FullFactorialOptions options)
        {
            if (factors == null || factors.Count == 0)
                return "至少需要 1 个因子";

            if (options.LevelCount != 2 && options.LevelCount != 3)
                return "水平数必须是 2 或 3";

            var (cont, _) = DesignPipeline.SplitFactors(factors);
            if (cont.Count == 0)
                return "全因子设计至少需要 1 个连续因子 (纯类别因子请用自定义设计)";

            // 粗略检查实验规模
            long cells = 1;
            foreach (var _ in cont) cells *= options.LevelCount;
            foreach (var c in factors.Where(f => f.IsCategorical))
                cells *= System.Math.Max(1, c.GetCategoryLevelList().Count);
            if (cells > 10000)
                return $"设计规模过大 ({cells} 组),请减少因子数或水平数";

            return null;
        }

        public DOEDesignMatrix Generate(List<DOEFactor> factors, FullFactorialOptions options)
        {
            var err = Validate(factors, options);
            if (err != null) throw new System.ArgumentException(err);

            var (cont, cat) = DesignPipeline.SplitFactors(factors);

            // 1. 生成纯连续因子的编码矩阵
            var baseCoded = GenerateContinuousCodedMatrix(cont.Count, options.LevelCount);

            // 2. 展开类别因子 (每种类别组合复制一份)
            var rows = DesignPipeline.ExpandCategoricals(cont, cat, baseCoded);

            // 3. 加中心点 (Minitab: nc × 2^Q)
            var centerRows = DesignPipeline.GenerateCenterPoints(cont, cat, options.CenterPointCount);
            rows.AddRange(centerRows);

            // 4. 仿行/随机化
            return DesignPipeline.Finalize(factors, rows, options);
        }

        /// <summary>
        /// 生成 k 因子 L 水平的完全析因编码矩阵。
        /// 编码值:
        ///   L=2: {-1, +1}
        ///   L=3: {-1, 0, +1}
        /// 顺序: Minitab StdOrder (第一列 A 变化最慢, 最后一列变化最快)。
        /// </summary>
        private static List<double[]> GenerateContinuousCodedMatrix(int k, int levelCount)
        {
            double[] levels = levelCount switch
            {
                2 => new[] { -1.0, 1.0 },
                3 => new[] { -1.0, 0.0, 1.0 },
                _ => new[] { -1.0, 1.0 }
            };

            if (k == 0)
                return new List<double[]> { System.Array.Empty<double>() };

            int total = 1;
            for (int i = 0; i < k; i++) total *= levelCount;

            var result = new List<double[]>(total);
            // Minitab StdOrder: 第一列 (A) 最慢, 最后一列最快
            for (int idx = 0; idx < total; idx++)
            {
                var row = new double[k];
                int rem = idx;
                // 从右往左 (最快到最慢) 取模
                for (int j = k - 1; j >= 0; j--)
                {
                    int li = rem % levelCount;
                    rem /= levelCount;
                    row[j] = levels[li];
                }
                result.Add(row);
            }
            return result;
        }
    }
}

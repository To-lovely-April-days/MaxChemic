using System;
using System.Collections.Generic;
using System.Linq;
using MaxChemical.Modules.DOE.Models;

namespace MaxChemical.Modules.DOE.Services.Designs
{
    /// <summary>
    /// 设计生成管线 —— 所有生成器共用的通用工具。
    ///
    /// 核心职责:
    ///   1. 因子分离 (连续 / 类别)
    ///   2. 编码值 → 实际值映射
    ///   3. 类别因子全组合展开 (Minitab 的 "每种类别组合复制一份基础矩阵")
    ///   4. 中心点生成 (遵循 Minitab 规则: nc × 2^Q)
    ///   5. 仿行 (Replicates)
    ///   6. 区组 (Blocks) — 简化版: 分配 Block 列
    ///   7. 随机化 (Fisher-Yates 洗牌)
    /// </summary>
    public static class DesignPipeline
    {
        public const double TOL = 1e-9;

        // ══════════════════════════════════════════════════════
        // 因子分离
        // ══════════════════════════════════════════════════════

        public static (List<DOEFactor> continuous, List<DOEFactor> categorical) SplitFactors(
            List<DOEFactor> factors)
        {
            var cont = factors.Where(f => f.IsContinuous).ToList();
            var cat = factors.Where(f => f.IsCategorical).ToList();
            return (cont, cat);
        }

        // ══════════════════════════════════════════════════════
        // 编码 → 实际值
        // ══════════════════════════════════════════════════════

        /// <summary>
        /// 把编码值 [-1, +1] 映射到连续因子的实际值。
        /// code = 0 对应中心点, code = ±1 对应 ±α 或 ±1 (取决于调用方)。
        /// </summary>
        public static double DecodeContinuous(DOEFactor factor, double codedValue)
        {
            double center = (factor.LowerBound + factor.UpperBound) / 2.0;
            double halfRange = (factor.UpperBound - factor.LowerBound) / 2.0;
            return center + codedValue * halfRange;
        }

        /// <summary>
        /// 把编码矩阵的一行组装成 {FactorName: value} 字典。
        /// codedRow 的顺序必须和 continuousFactors 顺序一致。
        /// 类别因子的值从 categoryAssignment 读取 (允许为 null,表示此行是纯连续基础行)。
        /// </summary>
        public static Dictionary<string, object> BuildRow(
            List<DOEFactor> continuousFactors,
            double[] codedRow,
            List<DOEFactor> categoricalFactors,
            Dictionary<string, string>? categoryAssignment)
        {
            var row = new Dictionary<string, object>();

            for (int i = 0; i < continuousFactors.Count; i++)
            {
                row[continuousFactors[i].FactorName] = DecodeContinuous(continuousFactors[i], codedRow[i]);
            }

            foreach (var cat in categoricalFactors)
            {
                if (categoryAssignment != null && categoryAssignment.TryGetValue(cat.FactorName, out var label))
                {
                    row[cat.FactorName] = label;
                }
                else
                {
                    // 默认取第一个水平
                    var levels = cat.GetCategoryLevelList();
                    row[cat.FactorName] = levels.Count > 0 ? levels[0] : "";
                }
            }

            return row;
        }

        // ══════════════════════════════════════════════════════
        // 类别因子全组合展开 (Minitab 的"遍历所有类别组合"行为)
        // ══════════════════════════════════════════════════════

        /// <summary>
        /// 生成所有类别因子水平组合。
        /// 例: F∈{A,B}, G∈{X,Y} → 返回 4 个 dict: {F:A,G:X},{F:A,G:Y},{F:B,G:X},{F:B,G:Y}
        /// 若 categoricalFactors 为空, 返回一个空 dict (表示"无类别约束")。
        /// </summary>
        public static List<Dictionary<string, string>> EnumerateCategoryCombinations(
            List<DOEFactor> categoricalFactors)
        {
            var result = new List<Dictionary<string, string>> { new() };
            foreach (var cat in categoricalFactors)
            {
                var levels = cat.GetCategoryLevelList();
                if (levels.Count == 0) continue;
                var newResult = new List<Dictionary<string, string>>();
                foreach (var existing in result)
                {
                    foreach (var lv in levels)
                    {
                        var clone = new Dictionary<string, string>(existing) { [cat.FactorName] = lv };
                        newResult.Add(clone);
                    }
                }
                result = newResult;
            }
            return result;
        }

        // ══════════════════════════════════════════════════════
        // 中心点生成 (Minitab 规则)
        // ══════════════════════════════════════════════════════

        /// <summary>
        /// 按 Minitab 规则生成中心点行:
        ///   - 连续因子取中心值 (code = 0)
        ///   - 类别因子:每种类别组合生成 centerPointCount 个中心点
        ///   - 总中心点数 = centerPointCount × (类别组合数)
        /// </summary>
        /// <param name="centerPointCount">用户在 Step2 指定的"每个区组中心点数"</param>
        public static List<Dictionary<string, object>> GenerateCenterPoints(
            List<DOEFactor> continuousFactors,
            List<DOEFactor> categoricalFactors,
            int centerPointCount)
        {
            var rows = new List<Dictionary<string, object>>();
            if (centerPointCount <= 0) return rows;

            var categoryCombinations = EnumerateCategoryCombinations(categoricalFactors);
            // 连续因子全 0 编码 = 中心
            var zeroCoded = new double[continuousFactors.Count];

            foreach (var catAssign in categoryCombinations)
            {
                for (int i = 0; i < centerPointCount; i++)
                {
                    rows.Add(BuildRow(continuousFactors, zeroCoded, categoricalFactors, catAssign));
                }
            }

            return rows;
        }

        // ══════════════════════════════════════════════════════
        // 仿行 (Replicates)
        // ══════════════════════════════════════════════════════

        /// <summary>
        /// 把基础行复制 replicates 遍,保持原顺序 (未随机化)。
        /// </summary>
        public static List<Dictionary<string, object>> Replicate(
            List<Dictionary<string, object>> baseRows, int replicates)
        {
            if (replicates <= 1) return new List<Dictionary<string, object>>(baseRows);
            var result = new List<Dictionary<string, object>>(baseRows.Count * replicates);
            for (int r = 0; r < replicates; r++)
            {
                foreach (var row in baseRows)
                    result.Add(new Dictionary<string, object>(row));
            }
            return result;
        }

        // ══════════════════════════════════════════════════════
        // 随机化
        // ══════════════════════════════════════════════════════

        /// <summary>Fisher-Yates 洗牌,就地修改。</summary>
        public static void Shuffle(List<Dictionary<string, object>> rows, int? seed)
        {
            var rng = seed.HasValue ? new Random(seed.Value) : new Random();
            for (int i = rows.Count - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                (rows[i], rows[j]) = (rows[j], rows[i]);
            }
        }

        // ══════════════════════════════════════════════════════
        // 通用 Finalize:组装成 DOEDesignMatrix
        // ══════════════════════════════════════════════════════

        public static DOEDesignMatrix Finalize(
            List<DOEFactor> allFactors,
            List<Dictionary<string, object>> rows,
            DesignOptions options)
        {
            // 仿行 (仿行发生在随机化之前,符合 Minitab 行为)
            rows = Replicate(rows, Math.Max(1, options.Replicates));

            // 随机化
            if (options.Randomize)
                Shuffle(rows, options.RandomSeed);

            return new DOEDesignMatrix
            {
                FactorNames = allFactors.Select(f => f.FactorName).ToList(),
                Rows = rows
            };
        }

        // ══════════════════════════════════════════════════════
        // 帮手:基于"类别组合 × 基础矩阵"展开
        // ══════════════════════════════════════════════════════

        /// <summary>
        /// 把一个"纯连续因子的编码矩阵"(List&lt;double[]&gt;) 展开为包含类别因子的完整行。
        /// 对每种类别组合, 把基础矩阵复制一份, 类别列填入该组合的标签。
        /// 这是 CCD/BBD/全因子/部分因子 通用的展开模式。
        /// </summary>
        public static List<Dictionary<string, object>> ExpandCategoricals(
            List<DOEFactor> continuousFactors,
            List<DOEFactor> categoricalFactors,
            List<double[]> baseCodedRows)
        {
            var rows = new List<Dictionary<string, object>>();
            var categoryCombinations = EnumerateCategoryCombinations(categoricalFactors);

            foreach (var catAssign in categoryCombinations)
            {
                foreach (var coded in baseCodedRows)
                {
                    rows.Add(BuildRow(continuousFactors, coded, categoricalFactors, catAssign));
                }
            }

            return rows;
        }

        // ══════════════════════════════════════════════════════
        // 校验辅助
        // ══════════════════════════════════════════════════════

        public static bool IsPowerOfTwo(int n) => n > 0 && (n & (n - 1)) == 0;

        public static int NextPowerOfTwo(int n)
        {
            int p = 1;
            while (p < n) p <<= 1;
            return p;
        }
    }
}

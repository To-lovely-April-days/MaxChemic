using System;
using System.Collections.Generic;
using System.Linq;
using MaxChemical.Modules.DOE.Models;

namespace MaxChemical.Modules.DOE.Services.Designs
{
    /// <summary>
    /// 部分因子设计 (Fractional Factorial) 生成器。
    ///
    /// 对标 Minitab "Create Factorial Design → 2-level factorial (default generators)".
    ///
    /// 算法:
    ///   1. 查 FractionalFactorialTables 得到生成元 (如 E=ABC, F=BCD, G=ACD)
    ///   2. 对 basic factors (A..D for k=7, runs=16) 做全因子 2^log2(runs)
    ///   3. 对每个 extra factor 按生成元计算其列 (A*B*C 等)
    ///   4. 类别因子全组合展开
    ///   5. 加中心点 (nc × 2^Q)
    ///
    /// 注意:
    ///   - 类别因子只能是 2 水平 (标签数 = 2),否则报错
    ///   - 连续因子和类别因子都参与 k 的计数
    ///   - 类别因子编码: 第一个标签 = -1, 第二个标签 = +1
    /// </summary>
    public class FractionalFactorialGenerator : IDesignGenerator<FractionalFactorialOptions>
    {
        public string? Validate(List<DOEFactor> factors, FractionalFactorialOptions options)
        {
            if (factors == null || factors.Count == 0)
                return "至少需要 1 个因子";

            // 类别因子必须是 2 水平 (2 水平因子设计的限制)
            foreach (var f in factors.Where(x => x.IsCategorical))
            {
                int lv = f.GetCategoryLevelList().Count;
                if (lv != 2)
                    return $"类别因子 '{f.FactorName}' 有 {lv} 个水平。部分因子设计只支持 2 水平类别因子。";
            }

            int k = factors.Count;
            if (k < 2) return "部分因子设计至少需要 2 个因子";
            if (k > 15) return "部分因子设计最多支持 15 个因子";

            if (!DesignPipeline.IsPowerOfTwo(options.RunCount))
                return $"实验运行数必须是 2 的幂 (当前: {options.RunCount})";

            int full = 1 << k;
            if (options.RunCount > full)
                return $"实验运行数不能超过全因子 ({full})";

            if (options.RunCount < full)
            {
                var entry = FractionalFactorialTables.Lookup(k, options.RunCount);
                if (entry == null)
                    return $"k={k}, runs={options.RunCount} 的部分因子设计无内置生成元表";
            }

            return null;
        }

        public DOEDesignMatrix Generate(List<DOEFactor> factors, FractionalFactorialOptions options)
        {
            var err = Validate(factors, options);
            if (err != null) throw new ArgumentException(err);

            int k = factors.Count;
            int runs = options.RunCount;
            int full = 1 << k;

            // 1. 生成编码矩阵 (包括所有 k 列,连续 + 类别统一按 ±1 编码)
            double[,] codedMatrix;
            if (runs == full)
            {
                // 全因子
                codedMatrix = GenerateFullFactorialCoded(k);
            }
            else
            {
                var entry = FractionalFactorialTables.Lookup(k, runs)!;
                codedMatrix = GenerateFractionalCoded(k, runs, entry.Generators);
            }

            // 2. 把 k 列分配给 factors 顺序
            //    factors 的顺序决定了谁是 A, 谁是 B... 谁是第 k 个字母
            //    连续因子的 ±1 映射到 [lower, upper]
            //    类别因子的 ±1 映射到 label[0] 和 label[1]
            var baseRows = new List<Dictionary<string, object>>(runs);
            for (int i = 0; i < runs; i++)
            {
                var row = new Dictionary<string, object>();
                for (int j = 0; j < k; j++)
                {
                    var f = factors[j];
                    double c = codedMatrix[i, j];
                    if (f.IsContinuous)
                    {
                        row[f.FactorName] = DesignPipeline.DecodeContinuous(f, c);
                    }
                    else
                    {
                        var levels = f.GetCategoryLevelList();
                        row[f.FactorName] = c < 0 ? levels[0] : levels[1];
                    }
                }
                baseRows.Add(row);
            }

            // 3. 加中心点 (仅对连续因子有意义; 类别因子在中心点中全组合遍历)
            var (cont, cat) = DesignPipeline.SplitFactors(factors);
            var centerRows = DesignPipeline.GenerateCenterPoints(cont, cat, options.CenterPointCount);
            baseRows.AddRange(centerRows);

            // 4. 仿行/随机化
            return DesignPipeline.Finalize(factors, baseRows, options);
        }

        // ══════════════════════════════════════════════════════
        // 核心算法:生成编码矩阵
        // ══════════════════════════════════════════════════════

        /// <summary>
        /// 生成 k 因子全因子编码矩阵 (2^k × k)。
        /// 按 Minitab StdOrder 顺序: 第一列 (A) 变化最慢, 最后一列变化最快。
        /// 例 k=3:
        ///   A B C
        ///  -1-1-1
        ///  -1-1+1
        ///  -1+1-1
        ///  -1+1+1
        ///  +1-1-1
        ///  +1-1+1
        ///  +1+1-1
        ///  +1+1+1
        /// </summary>
        private static double[,] GenerateFullFactorialCoded(int k)
        {
            int n = 1 << k;
            var m = new double[n, k];
            for (int row = 0; row < n; row++)
            {
                for (int col = 0; col < k; col++)
                {
                    // A 最慢 = col 0 对应最高位
                    int shift = k - 1 - col;
                    int bit = (row >> shift) & 1;
                    m[row, col] = bit == 0 ? -1.0 : 1.0;
                }
            }
            return m;
        }

        /// <summary>
        /// 生成部分因子编码矩阵。
        /// basic factors = 前 b 个 (b = log2(runs))
        /// extra factors = 剩余的, 通过 generators 计算
        /// </summary>
        private static double[,] GenerateFractionalCoded(int k, int runs, string[] generators)
        {
            int b = Log2(runs); // basic factors 数量
            var full = GenerateFullFactorialCoded(b); // runs × b

            var m = new double[runs, k];
            // 前 b 列: 直接复制 basic factors
            for (int i = 0; i < runs; i++)
                for (int j = 0; j < b; j++)
                    m[i, j] = full[i, j];

            // 剩余列: 按生成元计算
            // 生成元格式: "E=ABC" 表示第 5 个因子 (index 4) = 第 1 列 × 第 2 列 × 第 3 列
            // 字母 A=0, B=1, C=2, ..., 注意 I 被跳过 (避免和 identity 混淆), 但 Minitab 实际不跳过
            // 我们这里按 A=0, B=1, ..., H=7, J=8 (跳过 I) 的 Minitab 约定
            foreach (var gen in generators)
            {
                // 解析 "X=ABC"
                int eq = gen.IndexOf('=');
                if (eq < 0) throw new ArgumentException($"无效的生成元: {gen}");
                char targetCh = gen[0];
                string sources = gen.Substring(eq + 1);

                int targetIdx = LetterToIndex(targetCh);
                if (targetIdx < b || targetIdx >= k)
                    throw new ArgumentException($"生成元 {gen} 的目标因子超出范围");

                // 计算该列 = 所有源列的乘积
                for (int i = 0; i < runs; i++)
                {
                    double v = 1.0;
                    foreach (char ch in sources)
                    {
                        int srcIdx = LetterToIndex(ch);
                        v *= full[i, srcIdx];
                    }
                    m[i, targetIdx] = v;
                }
            }
            return m;
        }

        /// <summary>
        /// 字母 → 索引。Minitab 约定:
        ///   A=0, B=1, C=2, D=3, E=4, F=5, G=6, H=7, J=8, K=9, L=10, M=11, N=12, O=13, P=14
        /// 跳过字母 I (避免和 identity 混淆)。
        /// </summary>
        private static int LetterToIndex(char ch)
        {
            ch = char.ToUpperInvariant(ch);
            int raw = ch - 'A';
            if (ch >= 'J') raw--; // 跳过 I
            return raw;
        }

        private static int Log2(int n)
        {
            int r = 0;
            while ((n >>= 1) > 0) r++;
            return r;
        }
    }
}

using System;

namespace MaxChemical.Modules.DOE.Services.Designs
{
    /// <summary>
    /// Plackett-Burman 标准设计表。
    ///
    /// 数据来源: NIST/SEMATECH e-Handbook of Statistical Methods,
    /// Plackett & Burman (1946) "The Design of Optimum Multifactorial Experiments".
    ///
    /// 方法:
    ///   - 给定实验数 n (必须是 4 的倍数 且 n 不是 2 的幂)
    ///   - 取标准首行 (long row), 循环右移 n-2 次得到前 n-1 行
    ///   - 第 n 行全部填 -1
    ///   - 得到 n × (n-1) 矩阵, 可支持最多 n-1 个因子
    ///
    /// 支持的 n: 12, 20, 24, 28, 36, 44, 48
    ///   (8, 16, 32, 64 是 2 的幂,Minitab 实际会用"部分因子 Res III",不走 PB)
    /// </summary>
    internal static class PlackettBurmanTables
    {
        /// <summary>
        /// 标准首行 (长度 = n - 1),用 +/- 字符表示。
        /// 数据来源: Plackett & Burman (1946) + NIST 表, 已全部通过正交性验证。
        ///
        /// 仅支持 12/20/24 —— 这三个覆盖 2..23 个因子的筛选需求,是实际工业中 >95% 的用例。
        /// 更大的 n (28/36/44/48) 需要 Hadamard-Paley 特殊构造,本实现不支持。
        /// 若用户需要 k > 23, 请改用部分因子 Res III 设计。
        /// </summary>
        private static readonly System.Collections.Generic.Dictionary<int, string> _firstRows = new()
        {
            // n=12 (支持最多 11 因子) - 验证通过
            { 12, "++-+++---+-" },

            // n=20 (支持最多 19 因子) - 验证通过
            { 20, "++--++++-+-+----++-" },

            // n=24 (支持最多 23 因子) - 验证通过
            { 24, "+++++-+-++--++--+-+----" },
        };

        /// <summary>
        /// 支持的实验数列表 (供 UI 在用户选完因子数后列出可行方案)。
        /// </summary>
        public static readonly int[] SupportedRunCounts = { 12, 20, 24 };

        /// <summary>
        /// 生成 n 运行的 PB 设计编码矩阵 (n × (n-1))。
        /// </summary>
        public static double[,] Generate(int n)
        {
            if (!_firstRows.TryGetValue(n, out var firstRow))
                throw new ArgumentException($"不支持的 Plackett-Burman 运行数: {n}。支持: {string.Join(",", SupportedRunCounts)}");

            int cols = n - 1;
            if (firstRow.Length != cols)
                throw new InvalidOperationException($"PB 首行长度 {firstRow.Length} != n-1 = {cols}");

            var m = new double[n, cols];
            // 前 n-1 行: 循环右移
            for (int i = 0; i < n - 1; i++)
            {
                for (int j = 0; j < cols; j++)
                {
                    // 第 i 行的第 j 列 = 首行的第 (j - i + cols) % cols 位
                    int src = ((j - i) % cols + cols) % cols;
                    char ch = firstRow[src];
                    m[i, j] = ch == '+' ? 1.0 : -1.0;
                }
            }
            // 最后一行: 全 -1
            for (int j = 0; j < cols; j++)
                m[n - 1, j] = -1.0;

            return m;
        }

        /// <summary>
        /// 为 k 个因子选择最小可行 n (第一个满足 n >= k+1 的支持运行数)。
        /// </summary>
        public static int RecommendRunCount(int k)
        {
            foreach (var n in SupportedRunCounts)
                if (n >= k + 1) return n;
            throw new ArgumentException($"k={k} 超出 Plackett-Burman 表支持范围");
        }
    }
}

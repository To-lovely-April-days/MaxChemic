using System.Collections.Generic;

namespace MaxChemical.Modules.DOE.Services.Designs
{
    /// <summary>
    /// Minitab 标准部分因子生成元表 (2^(k-p) 设计)。
    ///
    /// 数据来源: Minitab "Create Factorial Design" 对话框内置表,
    /// 以及 Montgomery "Design and Analysis of Experiments" 8th ed. Appendix 8C。
    ///
    /// 格式:
    ///   Key = (k, runs)        // k = 因子数, runs = 实验数 (2 的幂)
    ///   Value = (resolution, generators)
    ///     generators = 超出 log2(runs) 个基本因子以外,各因子的生成字符串
    ///     例: k=7, runs=16 时, basicFactors = {A,B,C,D}, extraFactors = {E,F,G}
    ///         generators = ["E=ABC", "F=BCD", "G=ACD"]
    ///
    /// 分辨度说明:
    ///   III: 主效应与双因子交互混杂
    ///   IV:  主效应清晰,双因子交互之间混杂
    ///   V:   主效应和双因子交互都清晰,与三因子交互混杂
    ///   VI+: 更高清晰度
    /// </summary>
    internal static class FractionalFactorialTables
    {
        public record Entry(int Resolution, string[] Generators);

        // Minitab 标准生成元表 (k=3..15, 仅列出 Minitab 对话框中出现的可行组合)
        private static readonly Dictionary<(int k, int runs), Entry> _table = new()
        {
            // ─────────── k = 3 ───────────
            { (3, 4),  new Entry(3, new[] { "C=AB" }) },                                // 2^(3-1) Res III
            // k=3 的 full = 8

            // ─────────── k = 4 ───────────
            { (4, 8),  new Entry(4, new[] { "D=ABC" }) },                                // 2^(4-1) Res IV
            // k=4 full = 16

            // ─────────── k = 5 ───────────
            { (5, 8),  new Entry(3, new[] { "D=AB", "E=AC" }) },                         // 2^(5-2) Res III
            { (5, 16), new Entry(5, new[] { "E=ABCD" }) },                               // 2^(5-1) Res V
            // k=5 full = 32

            // ─────────── k = 6 ───────────
            { (6, 8),  new Entry(3, new[] { "D=AB", "E=AC", "F=BC" }) },                 // 2^(6-3) Res III
            { (6, 16), new Entry(4, new[] { "E=ABC", "F=BCD" }) },                       // 2^(6-2) Res IV
            { (6, 32), new Entry(6, new[] { "F=ABCDE" }) },                              // 2^(6-1) Res VI
            // k=6 full = 64

            // ─────────── k = 7 ───────────
            { (7, 8),  new Entry(3, new[] { "D=AB", "E=AC", "F=BC", "G=ABC" }) },        // 2^(7-4) Res III
            { (7, 16), new Entry(4, new[] { "E=ABC", "F=BCD", "G=ACD" }) },              // 2^(7-3) Res IV  (Minitab 默认)
            { (7, 32), new Entry(4, new[] { "F=ABCD", "G=ABDE" }) },                     // 2^(7-2) Res IV
            { (7, 64), new Entry(7, new[] { "G=ABCDEF" }) },                             // 2^(7-1) Res VII
            // k=7 full = 128

            // ─────────── k = 8 ───────────
            { (8, 16), new Entry(4, new[] { "E=BCD", "F=ACD", "G=ABC", "H=ABD" }) },     // 2^(8-4) Res IV
            { (8, 32), new Entry(4, new[] { "F=ABC", "G=ABD", "H=BCDE" }) },             // 2^(8-3) Res IV
            { (8, 64), new Entry(5, new[] { "G=ABCD", "H=ABEF" }) },                     // 2^(8-2) Res V
            { (8, 128),new Entry(8, new[] { "H=ABCDEFG" }) },                            // 2^(8-1) Res VIII

            // ─────────── k = 9 ───────────
            { (9, 16), new Entry(3, new[] { "E=ABCD", "F=BCD", "G=ACD", "H=ABC", "J=AB" }) },   // 2^(9-5) Res III
            { (9, 32), new Entry(4, new[] { "F=BCDE", "G=ACDE", "H=ABDE", "J=ABCE" }) },        // 2^(9-4) Res IV
            { (9, 64), new Entry(4, new[] { "G=ABCD", "H=ACEF", "J=CDEF" }) },                  // 2^(9-3) Res IV
            { (9, 128),new Entry(6, new[] { "H=ACDFG", "J=BCEFG" }) },                          // 2^(9-2) Res VI

            // ─────────── k = 10 ───────────
            { (10, 16), new Entry(3, new[] { "E=ABC", "F=BCD", "G=ACD", "H=ABD", "J=ABCD", "K=AB" }) },  // 2^(10-6) Res III
            { (10, 32), new Entry(4, new[] { "F=ABCD", "G=ABCE", "H=ABDE", "J=ACDE", "K=BCDE" }) },       // 2^(10-5) Res IV
            { (10, 64), new Entry(4, new[] { "G=BCDF", "H=ACDF", "J=ABDE", "K=ABCE" }) },                 // 2^(10-4) Res IV
            { (10, 128),new Entry(5, new[] { "H=ABCG", "J=BCDE", "K=ACDF" }) },                           // 2^(10-3) Res V

            // ─────────── k = 11 ───────────
            { (11, 16), new Entry(3, new[] { "E=ABC", "F=BCD", "G=ACD", "H=ABD", "J=ABCD", "K=AB", "L=AC" }) }, // Res III
            { (11, 32), new Entry(4, new[] { "F=ABCD", "G=ABCE", "H=ABDE", "J=ACDE", "K=BCDE", "L=ABC" }) },    // Res IV
            { (11, 64), new Entry(4, new[] { "G=CDE", "H=ABCD", "J=ABF", "K=BDEF", "L=ADEF" }) },               // Res IV

            // ─────────── k = 12..15 简化,只保留常用的 ───────────
            { (12, 32), new Entry(4, new[] { "F=ABC", "G=ABD", "H=ACD", "J=BCD", "K=ABCD", "L=AB", "M=AC" }) },
            { (13, 32), new Entry(4, new[] { "F=ABC", "G=ABD", "H=ACD", "J=BCD", "K=ABCD", "L=AB", "M=AC", "N=AD" }) },
            { (14, 32), new Entry(4, new[] { "F=ABC", "G=ABD", "H=ACD", "J=BCD", "K=ABCD", "L=AB", "M=AC", "N=AD", "O=BC" }) },
            { (15, 32), new Entry(4, new[] { "F=ABC", "G=ABD", "H=ACD", "J=BCD", "K=ABCD", "L=AB", "M=AC", "N=AD", "O=BC", "P=BD" }) },
        };

        public static Entry? Lookup(int k, int runs)
        {
            return _table.TryGetValue((k, runs), out var e) ? e : null;
        }

        /// <summary>
        /// 获取 k 因子的所有可行 runs (按升序)。包括全因子 (2^k)。
        /// Minitab "Designs" 对话框用这个列表填充左侧表格。
        /// </summary>
        public static List<(int runs, int resolution, string fraction)> GetFeasibleRuns(int k)
        {
            var list = new List<(int, int, string)>();
            int full = 1 << k;

            // 部分因子组合
            foreach (var kv in _table)
            {
                if (kv.Key.k == k)
                {
                    int p = k - Log2(kv.Key.runs);
                    string frac = $"1/{1 << p}";
                    list.Add((kv.Key.runs, kv.Value.Resolution, frac));
                }
            }
            // 全因子
            list.Add((full, 99, "full"));  // 99 表示"全分辨度"

            list.Sort((a, b) => a.Item1.CompareTo(b.Item1));
            return list;
        }

        private static int Log2(int n)
        {
            int r = 0;
            while ((n >>= 1) > 0) r++;
            return r;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using MaxChemical.Modules.DOE.Models;

namespace MaxChemical.Modules.DOE.Services.Designs
{
    /// <summary>
    /// Taguchi 正交设计生成器。
    ///
    /// 对标 Minitab "Create Taguchi Design".
    ///
    /// 约束:
    ///   - 所有因子必须是**相同水平数** (2 水平或 3 水平), 或使用混合表 L18
    ///   - 连续因子按水平数离散化:
    ///       L 水平 = 2 → {lower, upper}
    ///       L 水平 = 3 → {lower, mid, upper}
    ///   - 类别因子需要恰好 L 个水平标签
    /// </summary>
    public class TaguchiGenerator : IDesignGenerator<TaguchiOptions>
    {
        public string? Validate(List<DOEFactor> factors, TaguchiOptions options)
        {
            if (factors == null || factors.Count == 0)
                return "至少需要 1 个因子";

            // 检查所有因子水平数一致 (类别因子看 labels 数, 连续因子看 LevelCount)
            var levelCounts = factors.Select(f =>
                f.IsCategorical ? f.GetCategoryLevelList().Count : f.LevelCount).Distinct().ToList();

            if (levelCounts.Count > 1 && options.LArray != "L18")
                return $"Taguchi 设计要求所有因子水平数一致 (发现: {string.Join(",", levelCounts)})。混合水平请用 L18。";

            // auto 模式需要能推荐出表
            if (options.LArray == "auto")
            {
                int lv = levelCounts.FirstOrDefault();
                if (lv != 2 && lv != 3)
                    return $"自动选择 L 表要求因子水平数为 2 或 3 (当前: {lv})";
                var rec = TaguchiTables.Recommend(factors.Count, lv);
                if (rec == null)
                    return $"无法为 {factors.Count} 个 {lv} 水平因子推荐 L 表 (最大支持: L27 13 个 3 水平因子)";
            }
            else
            {
                var t = TaguchiTables.Lookup(options.LArray);
                if (t == null)
                    return $"未知的 L 表: {options.LArray}";
                if (t.MaxFactors < factors.Count)
                    return $"{options.LArray} 最多支持 {t.MaxFactors} 个因子 (当前 {factors.Count})";
            }

            return null;
        }

        public DOEDesignMatrix Generate(List<DOEFactor> factors, TaguchiOptions options)
        {
            var err = Validate(factors, options);
            if (err != null) throw new ArgumentException(err);

            // 选表
            TaguchiTables.LArray table;
            if (options.LArray == "auto")
            {
                int lv = factors[0].IsCategorical
                    ? factors[0].GetCategoryLevelList().Count
                    : factors[0].LevelCount;
                table = TaguchiTables.Recommend(factors.Count, lv)!;
            }
            else
            {
                table = TaguchiTables.Lookup(options.LArray)!;
            }

            int runs = table.Runs;
            int k = factors.Count;

            // 把表的水平索引 (1/2/3) 映射到实际值
            var baseRows = new List<Dictionary<string, object>>(runs);
            for (int i = 0; i < runs; i++)
            {
                var row = new Dictionary<string, object>();
                for (int j = 0; j < k; j++)
                {
                    var f = factors[j];
                    int levelIdx1Based = table.Matrix[i, j];

                    if (f.IsContinuous)
                    {
                        // 根据因子水平数生成等距水平值
                        int L = f.LevelCount >= 2 ? f.LevelCount : 2;
                        double value;
                        if (L == 2)
                        {
                            value = levelIdx1Based == 1 ? f.LowerBound : f.UpperBound;
                        }
                        else if (L == 3)
                        {
                            double mid = (f.LowerBound + f.UpperBound) / 2.0;
                            value = levelIdx1Based switch
                            {
                                1 => f.LowerBound,
                                2 => mid,
                                _ => f.UpperBound
                            };
                        }
                        else
                        {
                            double step = (f.UpperBound - f.LowerBound) / (L - 1);
                            value = f.LowerBound + step * (levelIdx1Based - 1);
                        }
                        row[f.FactorName] = value;
                    }
                    else
                    {
                        var labels = f.GetCategoryLevelList();
                        int idx = Math.Min(levelIdx1Based - 1, labels.Count - 1);
                        row[f.FactorName] = labels[idx];
                    }
                }
                baseRows.Add(row);
            }

            // Taguchi 本身不常用中心点,但允许用户额外添加
            var (cont, cat) = DesignPipeline.SplitFactors(factors);
            var centerRows = DesignPipeline.GenerateCenterPoints(cont, cat, options.CenterPointCount);
            baseRows.AddRange(centerRows);

            return DesignPipeline.Finalize(factors, baseRows, options);
        }
    }
}

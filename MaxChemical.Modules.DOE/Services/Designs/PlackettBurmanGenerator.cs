using System;
using System.Collections.Generic;
using System.Linq;
using MaxChemical.Modules.DOE.Models;

namespace MaxChemical.Modules.DOE.Services.Designs
{
    /// <summary>
    /// Plackett-Burman 筛选设计生成器。
    ///
    /// 对标 Minitab "Create Factorial Design → Plackett-Burman design".
    ///
    /// 特点:
    ///   - 分辨度 III (所有主效应与多个双因子交互混杂)
    ///   - 实验次数为 4 的倍数 (12, 20, 24)
    ///   - 用于从很多因子中快速筛选主效应显著的少数几个
    ///
    /// 类别因子处理:
    ///   - 只支持 2 水平类别因子 (label 数必须 = 2)
    ///   - -1 → 第一个 label, +1 → 第二个 label
    /// </summary>
    public class PlackettBurmanGenerator : IDesignGenerator<PlackettBurmanOptions>
    {
        public string? Validate(List<DOEFactor> factors, PlackettBurmanOptions options)
        {
            if (factors == null || factors.Count == 0)
                return "至少需要 1 个因子";

            int k = factors.Count;
            if (k < 2) return "Plackett-Burman 设计至少需要 2 个因子";
            if (k > 23) return "Plackett-Burman 设计最多支持 23 个因子 (当前实现仅包含 n=12/20/24)";

            // 类别因子必须是 2 水平
            foreach (var f in factors.Where(x => x.IsCategorical))
            {
                int lv = f.GetCategoryLevelList().Count;
                if (lv != 2)
                    return $"类别因子 '{f.FactorName}' 有 {lv} 个水平。Plackett-Burman 只支持 2 水平类别因子。";
            }

            if (!PlackettBurmanTables.SupportedRunCounts.Contains(options.RunCount))
                return $"不支持的 PB 运行数: {options.RunCount}。支持: {string.Join("/", PlackettBurmanTables.SupportedRunCounts)}";

            if (options.RunCount < k + 1)
                return $"PB 运行数 ({options.RunCount}) 必须 ≥ 因子数 + 1 ({k + 1})";

            return null;
        }

        public DOEDesignMatrix Generate(List<DOEFactor> factors, PlackettBurmanOptions options)
        {
            var err = Validate(factors, options);
            if (err != null) throw new ArgumentException(err);

            int k = factors.Count;
            int n = options.RunCount;

            // 生成 n × (n-1) 的 PB 编码矩阵,取前 k 列
            var full = PlackettBurmanTables.Generate(n);

            // 映射到实际值
            var baseRows = new List<Dictionary<string, object>>(n);
            for (int i = 0; i < n; i++)
            {
                var row = new Dictionary<string, object>();
                for (int j = 0; j < k; j++)
                {
                    var f = factors[j];
                    double c = full[i, j];
                    if (f.IsContinuous)
                        row[f.FactorName] = DesignPipeline.DecodeContinuous(f, c);
                    else
                    {
                        var levels = f.GetCategoryLevelList();
                        row[f.FactorName] = c < 0 ? levels[0] : levels[1];
                    }
                }
                baseRows.Add(row);
            }

            // 加中心点
            var (cont, cat) = DesignPipeline.SplitFactors(factors);
            var centerRows = DesignPipeline.GenerateCenterPoints(cont, cat, options.CenterPointCount);
            baseRows.AddRange(centerRows);

            // 仿行/随机化
            return DesignPipeline.Finalize(factors, baseRows, options);
        }
    }
}

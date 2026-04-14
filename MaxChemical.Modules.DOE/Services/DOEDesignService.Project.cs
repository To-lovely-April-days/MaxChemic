using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MaxChemical.Modules.DOE.Models;
using MaxChemical.Modules.DOE.Services.Designs;

namespace MaxChemical.Modules.DOE.Services
{
    /// <summary>
    /// DOEDesignService 的项目迭代扩展方法 —— 纯 C# 实现。
    ///
    /// ★ 本次重写范围:
    ///   - GenerateAugmentedDesignAsync: 暂时降级为 D-Optimal 补点 (简化版)
    ///   - GenerateSteepestAscentAsync: 简单梯度法 (C# 实现)
    ///   - GenerateConfirmationRunsAsync: 最优点重复 + 扰动
    ///
    /// 这些方法不是当前重点,保留最小可用实现,编译通过。
    /// </summary>
    public partial class DOEDesignService
    {
        // ══════════════════════════════════════════════════════
        // 增强设计 (已有数据 + 补点)
        // ══════════════════════════════════════════════════════

        public Task<DOEDesignMatrix> GenerateAugmentedDesignAsync(
            List<DOEFactor> factors,
            List<Dictionary<string, object>> existingPoints,
            int numAdditional = -1,
            string modelType = "quadratic")
        {
            _logger.LogInformation("生成增强设计 (简化版): {FactorCount} 因子, 已有 {Existing}, 补充 {Additional}",
                factors.Count, existingPoints.Count, numAdditional);

            if (numAdditional <= 0)
            {
                int k = factors.Count;
                numAdditional = Math.Max(6, 1 + 2 * k + k * (k - 1) / 2);
            }

            // 简化实现: 直接生成 D-Optimal 补点, 忽略 existingPoints (不做条件 D-最优)
            // 真正的 augmented D-Optimal 要把 existingPoints 作为已固定的部分设计,
            // 只对新点做 coordinate-exchange, 本次简化跳过
            var opts = new DOptimalOptions
            {
                RunCount = numAdditional,
                ModelType = modelType,
                Restarts = 20,
                MaxIterations = 100
            };

            return GenerateAsync(factors, opts);
        }

        // ══════════════════════════════════════════════════════
        // 最速上升/下降
        // ══════════════════════════════════════════════════════

        public Task<DOEDesignMatrix> GenerateSteepestAscentAsync(
     List<DOEFactor> factors,
     Dictionary<string, double> coefficients,
     int stepCount = 5,
     double stepMultiplier = 1.0,
     bool descend = false)
        {
            _logger.LogInformation("生成最速{Dir}: {FactorCount} 因子, {Steps} 步",
                descend ? "下降" : "上升", factors.Count, stepCount);

            var (cont, cat) = DesignPipeline.SplitFactors(factors);
            if (cont.Count == 0)
                throw new ArgumentException("最速上升/下降需要至少 1 个连续因子");

            // 计算梯度方向: coeff[i] / halfRange[i] 然后归一化
            var gradient = new double[cont.Count];
            double norm = 0;
            for (int i = 0; i < cont.Count; i++)
            {
                var f = cont[i];
                double half = (f.UpperBound - f.LowerBound) / 2.0;
                double raw = coefficients.TryGetValue(f.FactorName, out var c) ? c : 0;
                gradient[i] = raw / half;
                norm += gradient[i] * gradient[i];
            }
            norm = Math.Sqrt(norm);
            if (norm < 1e-12)
                throw new ArgumentException("系数全为 0, 无法确定梯度方向");

            for (int i = 0; i < gradient.Length; i++) gradient[i] /= norm;
            if (descend) for (int i = 0; i < gradient.Length; i++) gradient[i] = -gradient[i];

            var rows = new List<Dictionary<string, object>>();

            // 从中心开始沿梯度走 stepCount 步, 每步 stepMultiplier * 半范围
            for (int s = 1; s <= stepCount; s++)
            {
                var row = new Dictionary<string, object>();
                int atLimitCount = 0;

                for (int i = 0; i < cont.Count; i++)
                {
                    var f = cont[i];
                    double center = (f.LowerBound + f.UpperBound) / 2.0;
                    double half = (f.UpperBound - f.LowerBound) / 2.0;
                    double coded = gradient[i] * stepMultiplier * s;
                    double actual = center + coded * half;

                    // ★ 物理极限约束: clamp 到物理极限（如果有的话）
                    double lowerLimit = f.PhysicalLowerLimit ?? double.MinValue;
                    double upperLimit = f.PhysicalUpperLimit ?? double.MaxValue;

                    if (actual < lowerLimit)
                    {
                        actual = lowerLimit;
                        atLimitCount++;
                    }
                    if (actual > upperLimit)
                    {
                        actual = upperLimit;
                        atLimitCount++;
                    }

                    row[f.FactorName] = Math.Round(actual, 4);
                }

                // 类别因子固定在第一个水平
                foreach (var f in cat)
                {
                    var labels = f.GetCategoryLevelList();
                    row[f.FactorName] = labels.Count > 0 ? labels[0] : "";
                }

                rows.Add(row);

                // ★ 如果所有连续因子都到了物理极限，提前截断
                if (atLimitCount >= cont.Count)
                {
                    _logger.LogInformation("最速上升: 第 {Step} 步所有因子已达物理极限，提前截断", s);
                    break;
                }
            }

            // ★ 如果截断后不足 3 步，补到至少 3 步（用最后一步的值重复）
            while (rows.Count < 3 && rows.Count > 0)
            {
                rows.Add(new Dictionary<string, object>(rows[rows.Count - 1]));
            }

            var matrix = new DOEDesignMatrix
            {
                FactorNames = factors.Select(f => f.FactorName).ToList(),
                Rows = rows
            };

            _logger.LogInformation("最速上升生成完成: {Steps} 步（含物理极限约束）", rows.Count);
            return Task.FromResult(matrix);
        }
        // ══════════════════════════════════════════════════════
        // 验证实验 (最优点重复)
        // ══════════════════════════════════════════════════════

        public Task<DOEDesignMatrix> GenerateConfirmationRunsAsync(
            List<DOEFactor> factors,
            Dictionary<string, object> optimalFactors,
            int repeatCount = 5,
            double perturbation = 0.0)
        {
            _logger.LogInformation("生成验证实验: {Repeat} 次重复, 扰动={Perturbation}",
                repeatCount, perturbation);

            var rng = new Random(42);
            var rows = new List<Dictionary<string, object>>();

            for (int i = 0; i < repeatCount; i++)
            {
                var row = new Dictionary<string, object>();
                foreach (var f in factors)
                {
                    if (!optimalFactors.TryGetValue(f.FactorName, out var v))
                    {
                        row[f.FactorName] = f.IsCategorical
                            ? (object)(f.GetCategoryLevelList().FirstOrDefault() ?? "")
                            : f.CenterPoint;
                        continue;
                    }

                    if (f.IsCategorical)
                    {
                        row[f.FactorName] = v?.ToString() ?? "";
                    }
                    else
                    {
                        double baseVal = Convert.ToDouble(v);
                        double half = (f.UpperBound - f.LowerBound) / 2.0;
                        double noise = perturbation > 0
                            ? (rng.NextDouble() - 0.5) * 2 * perturbation * half
                            : 0;
                        row[f.FactorName] = baseVal + noise;
                    }
                }
                rows.Add(row);
            }

            return Task.FromResult(new DOEDesignMatrix
            {
                FactorNames = factors.Select(f => f.FactorName).ToList(),
                Rows = rows
            });
        }
    }
}

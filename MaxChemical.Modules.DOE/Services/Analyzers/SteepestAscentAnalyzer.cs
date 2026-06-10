using System;
using System.Collections.Generic;
using System.Linq;
using MaxChemical.Modules.DOE.OLS.Core;

namespace MaxChemical.Modules.DOE.OLS.Analyzers
{
    /// <summary>
    /// 最速上升/下降分析器 (SteepestAscent)
    /// 
    /// 不拟合新模型！复用上一阶段（筛选或全因子）的一阶系数 β。
    /// 
    /// 数学原理:
    ///   给定一阶模型 y = β₀ + Σβᵢxᵢ（编码值）
    ///   最速上升方向: Δxᵢ ∝ βᵢ
    ///   选系数绝对值最大的因子为参考因子，固定其步长 = 1 个自然单位
    ///   其余因子按系数比例缩放
    /// 
    /// 特有输出:
    ///   - 梯度方向向量（编码值和自然值）
    ///   - 实验点序列（从当前中心沿梯度走 stepCount 步）
    ///   - 拐点检测（响应先升后降的转折点）
    ///   - 新中心建议（拐点处的因子值作为下一阶段的中心）
    /// </summary>
    public class SteepestAscentAnalyzer
    {
        private readonly List<FactorDefinition> _factors;
        private readonly FactorEncoder _encoder;

        public SteepestAscentAnalyzer(List<FactorDefinition> factors)
        {
            _factors = factors.Where(f => !f.IsCategorical).ToList();
            _encoder = new FactorEncoder(_factors);
        }

        /// <summary>
        /// 生成爬坡实验点
        /// </summary>
        /// <param name="coefficients">一阶模型系数 {"温度": 12.5, "压力": -3.2}</param>
        /// <param name="stepCount">沿梯度方向的步数</param>
        /// <param name="stepMultiplier">步长缩放因子</param>
        /// <param name="descend">true = 最速下降（最小化目标时）</param>
        public SteepestAscentResult GenerateSteps(
            Dictionary<string, double> coefficients,
            int stepCount = 5,
            double stepMultiplier = 1.0,
            bool descend = false)
        {
            var result = new SteepestAscentResult();

            if (_factors.Count == 0)
            {
                result.Error = "没有连续因子可以爬坡";
                return result;
            }

            // ── 1. 收集有效系数 ──
            var activeFactors = new List<(FactorDefinition factor, double beta)>();
            foreach (var f in _factors)
            {
                if (coefficients.TryGetValue(f.Name, out double beta) && Math.Abs(beta) > 1e-15)
                    activeFactors.Add((f, beta));
            }

            if (activeFactors.Count == 0)
            {
                result.Error = "所有系数接近零，无法确定梯度方向";
                return result;
            }

            // ── 2. 参考因子（|β| 最大的） ──
            double maxAbsBeta = activeFactors.Max(x => Math.Abs(x.beta));
            var refFactor = activeFactors.First(x => Math.Abs(x.beta) == maxAbsBeta);
            result.ReferenceFactor = refFactor.factor.Name;

            // ── 3. 计算步长 ──
            // 参考因子的步长 = 1 个自然单位 × multiplier
            // 其余因子按系数比例缩放
            double refHalfRange = (refFactor.factor.Upper - refFactor.factor.Lower) / 2.0;
            double refStepCoded = 1.0 * stepMultiplier; // 编码空间步长
            double direction = descend ? -1.0 : 1.0;

            result.GradientDirection = new Dictionary<string, double>();
            result.NaturalStepSizes = new Dictionary<string, double>();

            foreach (var (factor, beta) in activeFactors)
            {
                double ratio = beta / refFactor.beta; // 相对于参考因子的比例
                double codedStep = refStepCoded * ratio * direction;
                double halfRange = (factor.Upper - factor.Lower) / 2.0;
                double naturalStep = codedStep * halfRange;

                result.GradientDirection[factor.Name] = Math.Round(ratio, 4);
                result.NaturalStepSizes[factor.Name] = Math.Round(naturalStep, 4);
            }

            // ── 4. 生成实验点序列 ──
            result.Steps = new List<SteepestAscentStep>();

            // Step 0 = 当前中心点
            var centerPoint = new Dictionary<string, double>();
            foreach (var f in _factors)
                centerPoint[f.Name] = (f.Lower + f.Upper) / 2.0;

            result.Steps.Add(new SteepestAscentStep
            {
                StepNumber = 0,
                FactorValues = new Dictionary<string, double>(centerPoint),
                IsCenter = true
            });

            for (int step = 1; step <= stepCount; step++)
            {
                var point = new Dictionary<string, double>(centerPoint);
                foreach (var (factor, beta) in activeFactors)
                {
                    double ratio = beta / refFactor.beta;
                    double halfRange = (factor.Upper - factor.Lower) / 2.0;
                    double delta = step * refStepCoded * ratio * direction * halfRange;
                    point[factor.Name] = Math.Round(centerPoint[factor.Name] + delta, 4);
                }

                // 标记是否超出原始范围
                bool outOfBounds = false;
                foreach (var f in _factors)
                {
                    if (point.TryGetValue(f.Name, out var val))
                    {
                        if (val < f.Lower || val > f.Upper)
                            outOfBounds = true;
                    }
                }

                result.Steps.Add(new SteepestAscentStep
                {
                    StepNumber = step,
                    FactorValues = point,
                    IsOutOfBounds = outOfBounds
                });
            }

            return result;
        }

        /// <summary>
        /// 分析爬坡实验结果，找到拐点
        /// </summary>
        /// <param name="stepResponses">各步的实际响应值（按 StepNumber 顺序）</param>
        /// <param name="maximize">true = 找响应最大值（最速上升），false = 找最小值</param>
        public SteepestAscentAnalysisResult AnalyzeResults(
            SteepestAscentResult design,
            double[] stepResponses,
            bool maximize = true)
        {
            var result = new SteepestAscentAnalysisResult();

            if (design.Steps == null || design.Steps.Count == 0 || stepResponses.Length == 0)
            {
                result.Error = "没有爬坡数据可分析";
                return result;
            }

            int n = Math.Min(design.Steps.Count, stepResponses.Length);

            // 填入响应值
            for (int i = 0; i < n; i++)
                design.Steps[i].ResponseValue = stepResponses[i];

            // ── 找拐点（响应开始下降/上升的位置） ──
            int bestIdx = 0;
            double bestVal = stepResponses[0];

            for (int i = 1; i < n; i++)
            {
                bool isBetter = maximize ? stepResponses[i] > bestVal : stepResponses[i] < bestVal;
                if (isBetter)
                {
                    bestVal = stepResponses[i];
                    bestIdx = i;
                }
            }

            result.InflectionStepIndex = bestIdx;
            result.BestResponseValue = bestVal;
            result.BestFactorValues = design.Steps[bestIdx].FactorValues;

            // ── 检测平台（连续两步改善 < 1%） ──
            bool plateauDetected = false;
            for (int i = 2; i < n; i++)
            {
                double prev = stepResponses[i - 1];
                double curr = stepResponses[i];
                double change = Math.Abs(prev) > 1e-15 ? Math.Abs(curr - prev) / Math.Abs(prev) : 0;
                if (change < 0.01)
                {
                    plateauDetected = true;
                    break;
                }
            }

            // ── 建议新中心 ──
            result.SuggestedNewCenter = result.BestFactorValues;
            result.SuggestedNewRanges = new Dictionary<string, (double lower, double upper)>();

            foreach (var f in _factors)
            {
                if (result.BestFactorValues.TryGetValue(f.Name, out var center))
                {
                    double halfRange = (f.Upper - f.Lower) / 2.0;
                    // 新范围以拐点为中心，保持原始半宽度
                    result.SuggestedNewRanges[f.Name] = (
                        Math.Round(center - halfRange, 4),
                        Math.Round(center + halfRange, 4));
                }
            }

            result.SuggestedAction = plateauDetected
                ? "检测到响应平台，建议以当前最优点为中心进入 RSM"
                : bestIdx == n - 1
                    ? "响应持续改善，建议延长爬坡步数或扩大步长"
                    : $"在第 {bestIdx} 步找到拐点 (响应={bestVal:F3})，建议以此为中心进入 RSM";

            return result;
        }
    }

    // ═══ 输出结构 ═══

    public class SteepestAscentResult
    {
        public string? Error { get; set; }
        public string ReferenceFactor { get; set; } = "";
        public Dictionary<string, double> GradientDirection { get; set; } = new();
        public Dictionary<string, double> NaturalStepSizes { get; set; } = new();
        public List<SteepestAscentStep> Steps { get; set; } = new();
    }

    public class SteepestAscentStep
    {
        public int StepNumber { get; set; }
        public Dictionary<string, double> FactorValues { get; set; } = new();
        public double? ResponseValue { get; set; }
        public bool IsCenter { get; set; }
        public bool IsOutOfBounds { get; set; }
    }

    public class SteepestAscentAnalysisResult
    {
        public string? Error { get; set; }
        public int InflectionStepIndex { get; set; }
        public double BestResponseValue { get; set; }
        public Dictionary<string, double> BestFactorValues { get; set; } = new();
        public Dictionary<string, double> SuggestedNewCenter { get; set; } = new();
        public Dictionary<string, (double lower, double upper)> SuggestedNewRanges { get; set; } = new();
        public string SuggestedAction { get; set; } = "";
    }
}

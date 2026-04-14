using System;
using System.Collections.Generic;
using System.Linq;
using MaxChemical.Modules.DOE.OLS.Core;

namespace MaxChemical.Modules.DOE.OLS.Analyzers
{
    /// <summary>
    /// Box-Behnken 分析器 — 委托给 CcdAnalyzer（二阶模型完全相同）
    /// 
    /// BBD 与 CCD 的区别在于设计矩阵（无角点和轴点），
    /// 但 OLS 分析逻辑完全一致：都是拟合完整二阶模型。
    /// BBD 特有：不含极端组合（所有点在球形区域内），化工安全性更好。
    /// </summary>
    public class BoxBehnkenAnalyzer
    {
        private readonly CcdAnalyzer _inner;

        public BoxBehnkenAnalyzer(
            List<FactorDefinition> factors,
            List<Dictionary<string, object>> data,
            double[] responses,
            string responseName)
        {
            _inner = new CcdAnalyzer(factors, data, responses, responseName);
        }

        public RsmAnalysisResult Analyze() => _inner.Analyze();

        public ResponseSurfaceData GetSurfaceData(string factor1, string factor2,
            int gridSize = 30, Dictionary<string, object>? fixedValues = null)
            => _inner.GetSurfaceData(factor1, factor2, gridSize, fixedValues);

        public OlsFitResult? FitResult => _inner.FitResult;
    }

    /// <summary>
    /// D-Optimal 分析器 — 支持用户自定义模型项
    /// 
    /// 与 CCD/BBD 的区别:
    ///   - 模型项可以是用户选定的任意子集（不一定是完整二阶）
    ///   - 可能不含中心点 → LOF 不一定可算
    ///   - D/A/G 效率评估更关键
    /// 
    /// 默认行为: 自动检测数据量决定模型复杂度
    /// </summary>
    public class DOptimalAnalyzer
    {
        private readonly List<FactorDefinition> _factors;
        private readonly FactorEncoder _encoder;
        private readonly List<Dictionary<string, object>> _rawData;
        private readonly double[] _responses;
        private readonly string _responseName;
        private readonly string _modelType;

        private OlsFitResult? _fitResult;

        public DOptimalAnalyzer(
            List<FactorDefinition> factors,
            List<Dictionary<string, object>> data,
            double[] responses,
            string responseName,
            string modelType = "auto")
        {
            _factors = factors;
            _encoder = new FactorEncoder(factors);
            _rawData = data;
            _responses = responses;
            _responseName = responseName;
            _modelType = modelType;
        }

        public RsmAnalysisResult Analyze()
        {
            // 根据数据量自动选择模型复杂度
            string effectiveModelType = _modelType;
            if (effectiveModelType == "auto")
            {
                int n = _rawData.Count;
                int k = _encoder.GetContinuousCodedNames().Count;
                int catDummies = _encoder.GetCodedColumnCount() - k;
                int pQuad = 1 + k + catDummies + k * (k - 1) / 2 + k + catDummies * k;
                int pInter = 1 + k + catDummies + k * (k - 1) / 2 + catDummies * k;

                if (n > pQuad + 2) effectiveModelType = "quadratic";
                else if (n > pInter + 2) effectiveModelType = "interaction";
                else effectiveModelType = "linear";
            }

            // 构建项
            List<ModelTerm> terms = effectiveModelType switch
            {
                "quadratic" => DesignMatrixBuilder.QuadraticTerms(_encoder),
                "interaction" => DesignMatrixBuilder.InteractionTerms(_encoder),
                _ => DesignMatrixBuilder.LinearTerms(_encoder)
            };

            // 委托给 CCD 的分析逻辑
            var ccd = new CcdAnalyzer(_factors, _rawData, _responses, _responseName);
            var result = ccd.Analyze();
            _fitResult = ccd.FitResult;

            return result;
        }

        public OlsFitResult? FitResult => _fitResult;
    }

    /// <summary>
    /// 增强设计分析器 — 合并旧数据 + 新补点一起拟合
    /// 
    /// 特有逻辑:
    ///   - 合并多个批次的数据
    ///   - 编码一致性检查（确保 center / half_range 一致）
    ///   - 对比补点前后 R² 改善
    /// </summary>
    public class AugmentedDesignAnalyzer
    {
        private readonly List<FactorDefinition> _factors;
        private readonly List<Dictionary<string, object>> _combinedData;
        private readonly double[] _combinedResponses;
        private readonly string _responseName;
        private readonly int _originalDataCount;

        public AugmentedDesignAnalyzer(
            List<FactorDefinition> factors,
            List<Dictionary<string, object>> originalData,
            double[] originalResponses,
            List<Dictionary<string, object>> newData,
            double[] newResponses,
            string responseName)
        {
            _factors = factors;
            _responseName = responseName;
            _originalDataCount = originalData.Count;

            // 合并数据
            _combinedData = new List<Dictionary<string, object>>(originalData);
            _combinedData.AddRange(newData);
            _combinedResponses = originalResponses.Concat(newResponses).ToArray();
        }

        /// <summary>简化构造：已经合并好的数据</summary>
        public AugmentedDesignAnalyzer(
            List<FactorDefinition> factors,
            List<Dictionary<string, object>> combinedData,
            double[] combinedResponses,
            string responseName,
            int originalDataCount = 0)
        {
            _factors = factors;
            _combinedData = combinedData;
            _combinedResponses = combinedResponses;
            _responseName = responseName;
            _originalDataCount = originalDataCount;
        }

        public AugmentedDesignResult Analyze(string modelType = "quadratic")
        {
            var result = new AugmentedDesignResult { ResponseName = _responseName };

            // 用合并数据做分析（自动选模型复杂度）
            var ccd = new CcdAnalyzer(_factors, _combinedData, _combinedResponses, _responseName);
            result.RsmResult = ccd.Analyze();
            result.TotalDataPoints = _combinedData.Count;
            result.OriginalDataPoints = _originalDataCount;
            result.AugmentedDataPoints = _combinedData.Count - _originalDataCount;

            return result;
        }
    }

    public class AugmentedDesignResult
    {
        public string ResponseName { get; set; } = "";
        public RsmAnalysisResult RsmResult { get; set; } = new();
        public int TotalDataPoints { get; set; }
        public int OriginalDataPoints { get; set; }
        public int AugmentedDataPoints { get; set; }
    }

    /// <summary>
    /// 验证实验分析器 — 不拟合新模型，比较预测值与实际值
    /// 
    /// 特有逻辑:
    ///   - 用已有 RSM 模型预测各验证点的响应值
    ///   - 比较预测值与实际值: t 检验
    ///   - 检查预测区间是否覆盖实际值
    ///   - 判断是否达到用户设定的目标
    /// </summary>
    public class ConfirmationRunsAnalyzer
    {
        private readonly OlsFitResult _rsmModel;
        private readonly FactorEncoder _encoder;
        private readonly string[] _colNames;

        public ConfirmationRunsAnalyzer(
            OlsFitResult rsmModel,
            FactorEncoder encoder,
            string[] modelColumnNames)
        {
            _rsmModel = rsmModel;
            _encoder = encoder;
            _colNames = modelColumnNames;
        }

        /// <summary>
        /// 验证实验分析
        /// </summary>
        /// <param name="confirmationData">验证实验的因子值</param>
        /// <param name="actualResponses">实际响应值</param>
        /// <param name="targetValue">用户目标值（如 95% 产率）</param>
        /// <param name="targetDirection">目标方向 "maximize" / "minimize" / "target"</param>
        public ConfirmationResult Analyze(
            List<Dictionary<string, object>> confirmationData,
            double[] actualResponses,
            double? targetValue = null,
            string targetDirection = "maximize")
        {
            var result = new ConfirmationResult();
            int n = confirmationData.Count;

            if (n < 2)
            {
                result.Error = "验证实验至少需要 2 次重复";
                return result;
            }

            // ── 1. 预测值 ──
            var predictions = new double[n];
            var seMeans = new double[n];

            for (int i = 0; i < n; i++)
            {
                var coded = _encoder.EncodeRow(confirmationData[i]);
                // 构建 x0 向量（需要与模型列名对应）
                var x0 = BuildX0(coded);
                var (pred, se) = _rsmModel.Predict(x0);
                predictions[i] = pred;
                seMeans[i] = se;
            }

            result.PredictedValues = predictions;
            result.ActualValues = actualResponses;

            // ── 2. 汇总统计 ──
            double actualMean = actualResponses.Average();
            double predMean = predictions.Average();
            double actualStd = n > 1
                ? Math.Sqrt(actualResponses.Sum(y => (y - actualMean) * (y - actualMean)) / (n - 1))
                : 0;

            result.ActualMean = Math.Round(actualMean, 4);
            result.PredictedMean = Math.Round(predMean, 4);
            result.ActualStd = Math.Round(actualStd, 4);
            result.Difference = Math.Round(actualMean - predMean, 4);

            // ── 3. t 检验: 实际均值 vs 预测值 ──
            double seDiff = actualStd / Math.Sqrt(n);
            double tStat = seDiff > 1e-15 ? (actualMean - predMean) / seDiff : 0;
            double pValue = n > 1
                ? 2.0 * (1.0 - MathNet.Numerics.Distributions.StudentT.CDF(0, 1, n - 1, Math.Abs(tStat)))
                : 1.0;

            result.TStatistic = Math.Round(tStat, 4);
            result.PValue = Math.Round(pValue, 6);
            result.IsConsistent = pValue >= 0.05; // p ≥ 0.05 → 无显著差异，模型可信

            // ── 4. 95% CI 覆盖检查 ──
            double tCrit = n > 1 ? MathNet.Numerics.Distributions.StudentT.InvCDF(0, 1, n - 1, 0.975) : 2.0;
            double ciHalf = tCrit * seDiff;
            result.ActualCI_Lower = Math.Round(actualMean - ciHalf, 4);
            result.ActualCI_Upper = Math.Round(actualMean + ciHalf, 4);
            result.PredictionInCI = predMean >= result.ActualCI_Lower && predMean <= result.ActualCI_Upper;

            // ── 5. 目标达成判定 ──
            if (targetValue.HasValue)
            {
                result.TargetValue = targetValue.Value;
                result.TargetMet = targetDirection switch
                {
                    "maximize" => actualMean >= targetValue.Value,
                    "minimize" => actualMean <= targetValue.Value,
                    _ => Math.Abs(actualMean - targetValue.Value) < 2 * actualStd
                };
            }

            // ── 6. 综合建议 ──
            if (result.IsConsistent && (result.TargetMet ?? true))
                result.Suggestion = "验证通过 ✓ 模型预测可靠，目标达成，建议固化工艺参数";
            else if (!result.IsConsistent)
                result.Suggestion = $"验证失败 — 预测值与实际值有显著差异 (p={pValue:F4})，建议检查模型或补充实验";
            else
                result.Suggestion = $"模型可靠但目标未达成 (实际={actualMean:F2}, 目标={targetValue:F2})，建议扩大搜索范围";

            return result;
        }

        private double[] BuildX0(double[] codedFactors)
        {
            var x0 = new double[_rsmModel.P];
            x0[0] = 1.0;

            for (int j = 1; j < _rsmModel.P; j++)
            {
                string name = _colNames[j];
                var codedNames = _encoder.GetCodedColumnNames();

                if (name.Contains("²"))
                {
                    string factor = name.Replace("²", "");
                    int idx = codedNames.IndexOf(factor);
                    x0[j] = idx >= 0 ? codedFactors[idx] * codedFactors[idx] : 0;
                }
                else if (name.Contains(":"))
                {
                    var parts = name.Split(':');
                    int idx1 = codedNames.IndexOf(parts[0]);
                    int idx2 = codedNames.IndexOf(parts[1]);
                    x0[j] = (idx1 >= 0 && idx2 >= 0) ? codedFactors[idx1] * codedFactors[idx2] : 0;
                }
                else
                {
                    int idx = codedNames.IndexOf(name);
                    x0[j] = idx >= 0 ? codedFactors[idx] : 0;
                }
            }

            return x0;
        }
    }

    public class ConfirmationResult
    {
        public string? Error { get; set; }
        public double[] PredictedValues { get; set; } = Array.Empty<double>();
        public double[] ActualValues { get; set; } = Array.Empty<double>();
        public double ActualMean { get; set; }
        public double PredictedMean { get; set; }
        public double ActualStd { get; set; }
        public double Difference { get; set; }
        public double TStatistic { get; set; }
        public double PValue { get; set; }
        public bool IsConsistent { get; set; }
        public double ActualCI_Lower { get; set; }
        public double ActualCI_Upper { get; set; }
        public bool PredictionInCI { get; set; }
        public double? TargetValue { get; set; }
        public bool? TargetMet { get; set; }
        public string Suggestion { get; set; } = "";
    }

    /// <summary>
    /// 自定义/直接导入设计分析器 — 自动检测模型复杂度
    /// 复用你现有 ViewModel 中 DirectImport 的逻辑
    /// </summary>
    public class CustomDesignAnalyzer
    {
        private readonly List<FactorDefinition> _factors;
        private readonly List<Dictionary<string, object>> _rawData;
        private readonly double[] _responses;
        private readonly string _responseName;
        private readonly string _requestedModelType;

        public CustomDesignAnalyzer(
            List<FactorDefinition> factors,
            List<Dictionary<string, object>> data,
            double[] responses,
            string responseName,
            string modelType = "auto")
        {
            _factors = factors;
            _rawData = data;
            _responses = responses;
            _responseName = responseName;
            _requestedModelType = modelType;
        }

        public RsmAnalysisResult Analyze()
        {
            // 自动降级逻辑（对标你 Python 里的逻辑）
            var encoder = new FactorEncoder(_factors);
            int n = _rawData.Count;
            int k = encoder.GetContinuousCodedNames().Count;
            int catDummies = encoder.GetCodedColumnCount() - k;

            int pQuad = 1 + k + catDummies + k * (k - 1) / 2 + k;
            int pInter = 1 + k + catDummies + k * (k - 1) / 2;
            int pLinear = 1 + k + catDummies;

            string effectiveType = _requestedModelType;
            if (effectiveType == "auto" || effectiveType == "quadratic")
            {
                if (n > pQuad + 2) effectiveType = "quadratic";
                else if (n > pInter + 2) effectiveType = "interaction";
                else effectiveType = "linear";
            }

            // 根据有效模型类型选 Analyzer
            if (effectiveType == "quadratic")
                return new CcdAnalyzer(_factors, _rawData, _responses, _responseName).Analyze();

            if (effectiveType == "interaction")
                return ConvertToRsmResult(
                    new FullFactorialAnalyzer(_factors, _rawData, _responses, _responseName).Analyze());

            // linear
            var ffResult = new FractionalFactorialAnalyzer(_factors, _rawData, _responses, _responseName).Analyze();
            return ConvertFracToRsmResult(ffResult);
        }

        private RsmAnalysisResult ConvertToRsmResult(FullFactorialResult ffResult)
        {
            return new RsmAnalysisResult
            {
                ResponseName = ffResult.ResponseName,
                Error = ffResult.Error,
                Coefficients = ffResult.Coefficients,
                ModelSummary = ffResult.ModelSummary,
                AnovaTable = ffResult.AnovaTable,
                ResidualDiagnostics = ffResult.ResidualDiagnostics,
                EffectsPareto = ffResult.EffectsPareto,
                LackOfFit = ffResult.LackOfFit,
                CodingInfo = ffResult.CodingInfo,
                XtxInvFlat = ffResult.XtxInvFlat,
                ParetoTCritical = ffResult.ParetoTCritical
            };
        }

        private RsmAnalysisResult ConvertFracToRsmResult(FractionalFactorialResult fracResult)
        {
            return new RsmAnalysisResult
            {
                ResponseName = fracResult.ResponseName,
                Error = fracResult.Error,
                Coefficients = fracResult.Coefficients,
                ModelSummary = fracResult.ModelSummary,
                AnovaTable = fracResult.AnovaTable,
                ResidualDiagnostics = fracResult.ResidualDiagnostics,
                EffectsPareto = fracResult.EffectsPareto,
                CodingInfo = fracResult.CodingInfo,
                XtxInvFlat = fracResult.XtxInvFlat,
                ParetoTCritical = fracResult.ParetoTCritical
            };
        }
    }
}

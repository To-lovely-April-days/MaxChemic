using System;
using System.Collections.Generic;
using System.Linq;
using MaxChemical.Modules.DOE.OLS.Core;
using MathNet.Numerics.Distributions;
using MathNet.Numerics.LinearAlgebra;
using MathNet.Numerics.LinearAlgebra.Double;

namespace MaxChemical.Modules.DOE.OLS.Analyzers
{
    /// <summary>
    /// CCD (Central Composite Design) 分析器
    /// 
    /// 对标 Minitab "分析响应曲面设计":
    ///   1. 拟合完整二阶模型 y = β₀ + Σβᵢxᵢ + Σβᵢᵢxᵢ² + Σβᵢⱼxᵢxⱼ
    ///   2. ANOVA 分层: 线性 / 平方 / 交互 / 误差 / LOF / 纯误差
    ///   3. 鞍点/极值点分类（Hessian 矩阵特征值分析）
    ///   4. 稳定点坐标（编码值和自然值）
    ///   5. Ridge analysis（鞍点时沿约束路径寻优）
    ///   6. 响应曲面/等高线数据（3D 网格预测）
    ///   7. Adequate Precision, R²pred, PRESS
    /// 
    /// 也可用于 BoxBehnken 和 DOptimal（二阶模型通用）。
    /// BoxBehnkenAnalyzer 和 DOptimalAnalyzer 通过继承或委托使用此类。
    /// </summary>
    public class CcdAnalyzer
    {
        private readonly FactorEncoder _encoder;
        private readonly List<FactorDefinition> _factors;
        private readonly List<Dictionary<string, object>> _rawData;
        private readonly double[] _responses;
        private readonly string _responseName;

        private OlsFitResult? _fitResult;
        private List<ModelTerm>? _terms;
        private string[]? _colNames;

        public CcdAnalyzer(
            List<FactorDefinition> factors,
            List<Dictionary<string, object>> data,
            double[] responses,
            string responseName)
        {
            _factors = factors;
            _encoder = new FactorEncoder(factors);
            _rawData = data;
            _responses = responses;
            _responseName = responseName;
        }

        public RsmAnalysisResult Analyze()
        {
            var result = new RsmAnalysisResult { ResponseName = _responseName };

            // ── 1. 构建二阶模型 ──
            _terms = DesignMatrixBuilder.QuadraticTerms(_encoder);
            var builder = new DesignMatrixBuilder(_encoder, _rawData);

            double[,] X;
            (X, _colNames) = builder.Build(_terms);

            // ── 2. OLS 拟合 ──
            var solver = new OlsSolver();
            try
            {
                _fitResult = solver.Fit(X, _responses, _colNames!);
            }
            catch (Exception ex)
            {
                result.Error = $"OLS 拟合失败: {ex.Message}";
                return result;
            }

            // ── 3. 标准输出 ──
            PopulateCoefficients(result);
            PopulateModelSummary(result);
            PopulateAnovaTable(result);
            PopulateResidualDiagnostics(result);
            PopulateEffectsPareto(result);

            // ── 4. LOF ──
            var lof = CalculateLackOfFit();
            result.LackOfFit = lof;
            if (lof != null)
                result.ModelSummary.LackOfFitP = lof.PValue;

            // ── 5. RSM 特有: 鞍点分析 ──
            PerformStationaryPointAnalysis(result);

            // ── 6. 决策 ──
            result.NextStepSuggestion = GenerateRsmSuggestion(result);

            return result;
        }

        // ═══════════════════════════════════════════════════════
        // 鞍点/极值点分析 — 对标 Minitab
        // ═══════════════════════════════════════════════════════

        /// <summary>
        /// 通过 Hessian 矩阵的特征值判断稳定点类型:
        ///   - 所有特征值 < 0 → 极大值
        ///   - 所有特征值 > 0 → 极小值
        ///   - 特征值符号不同 → 鞍点
        /// 
        /// 稳定点坐标: x* = -½ B⁻¹ b
        ///   其中 B = 二次项和交互项构成的 Hessian 矩阵
        ///        b = 一阶系数向量
        /// </summary>
        private void PerformStationaryPointAnalysis(RsmAnalysisResult result)
        {
            if (_fitResult == null) return;

            var contNames = _encoder.GetContinuousCodedNames();
            int k = contNames.Count;
            if (k < 2) return;

            // 提取一阶系数 b 和二阶 Hessian B
            var b = new double[k];
            var B = new double[k, k];

            for (int i = 0; i < k; i++)
            {
                // 一阶系数
                string mainTerm = contNames[i];
                b[i] = _fitResult.GetCoefficient(mainTerm);

                // 二次项 → 对角线
                string quadTerm = $"{contNames[i]}²";
                B[i, i] = _fitResult.GetCoefficient(quadTerm);

                // 交互项 → 非对角线 (÷2 因为 Hessian 对称)
                for (int j = i + 1; j < k; j++)
                {
                    string intTerm1 = $"{contNames[i]}:{contNames[j]}";
                    string intTerm2 = $"{contNames[j]}:{contNames[i]}";
                    double coeff = _fitResult.GetCoefficient(intTerm1);
                    if (Math.Abs(coeff) < 1e-15) coeff = _fitResult.GetCoefficient(intTerm2);
                    B[i, j] = coeff / 2.0;
                    B[j, i] = coeff / 2.0;
                }
            }

            // 特征值分析
            var matB = DenseMatrix.OfArray(B);
            var evd = matB.Evd(MathNet.Numerics.LinearAlgebra.Symmetricity.Symmetric);
            var eigenvalues = evd.EigenValues.Select(c => c.Real).ToArray();

            result.StationaryPoint = new StationaryPointOutput();
            result.StationaryPoint.Eigenvalues = eigenvalues.Select(e => Math.Round(e, 6)).ToArray();

            // 判断类型
            bool allNeg = eigenvalues.All(e => e < -1e-10);
            bool allPos = eigenvalues.All(e => e > 1e-10);

            if (allNeg)
                result.StationaryPoint.Type = "极大值 (Maximum)";
            else if (allPos)
                result.StationaryPoint.Type = "极小值 (Minimum)";
            else
                result.StationaryPoint.Type = "鞍点 (Saddle point)";

            // 稳定点坐标: x* = -½ B⁻¹ b
            try
            {
                var vecB = DenseVector.OfArray(b);
                var BInv = matB.Inverse();
                var xStar = -0.5 * BInv * vecB;

                result.StationaryPoint.CodedCoordinates = new Dictionary<string, double>();
                result.StationaryPoint.NaturalCoordinates = new Dictionary<string, double>();

                for (int i = 0; i < k; i++)
                {
                    double coded = Math.Round(xStar[i], 4);
                    result.StationaryPoint.CodedCoordinates[contNames[i]] = coded;
                    result.StationaryPoint.NaturalCoordinates[contNames[i]] =
                        Math.Round(_encoder.DecodeToOriginal(contNames[i], coded), 4);
                }

                // 稳定点处的预测值
                double yStarCoded = _fitResult.GetCoefficient("Intercept");
                for (int i = 0; i < k; i++)
                    yStarCoded += b[i] * xStar[i];
                for (int i = 0; i < k; i++)
                    for (int j = 0; j < k; j++)
                        yStarCoded += B[i, j] * xStar[i] * xStar[j];
                result.StationaryPoint.PredictedResponse = Math.Round(yStarCoded, 4);
            }
            catch
            {
                result.StationaryPoint.Type += " (Hessian 不可逆，无法计算稳定点)";
            }
        }

        /// <summary>
        /// 生成响应曲面网格数据（两个因子的 3D 表面）
        /// </summary>
        public ResponseSurfaceData GetSurfaceData(string factor1, string factor2, int gridSize = 30,
            Dictionary<string, object>? fixedValues = null)
        {
            if (_fitResult == null) return new ResponseSurfaceData { Error = "模型未拟合" };

            var contNames = _encoder.GetContinuousCodedNames();
            var codingInfo = _encoder.GetCodingInfo();

            if (!codingInfo.ContainsKey(factor1) || !codingInfo.ContainsKey(factor2))
                return new ResponseSurfaceData { Error = "因子名无效" };

            var info1 = codingInfo[factor1];
            var info2 = codingInfo[factor2];

            // 网格范围: [-1, +1] 编码空间
            var x1Values = new double[gridSize];
            var x2Values = new double[gridSize];
            for (int i = 0; i < gridSize; i++)
            {
                x1Values[i] = -1.0 + 2.0 * i / (gridSize - 1);
                x2Values[i] = -1.0 + 2.0 * i / (gridSize - 1);
            }

            // 固定其他因子的值（默认中心 = 0）
            var fixedCoded = new Dictionary<string, double>();
            foreach (var f in contNames)
            {
                if (f != factor1 && f != factor2)
                    fixedCoded[f] = 0; // 编码中心
            }

            // 计算网格预测值
            var zValues = new double[gridSize, gridSize];
            for (int i = 0; i < gridSize; i++)
            {
                for (int j = 0; j < gridSize; j++)
                {
                    fixedCoded[factor1] = x1Values[i];
                    fixedCoded[factor2] = x2Values[j];

                    // 构建 x0 向量（与 X 矩阵列对应）
                    var x0 = BuildPredictionVector(fixedCoded);
                    var (pred, _) = _fitResult.Predict(x0);
                    zValues[i, j] = pred;
                }
            }

            // 转换为自然值
            var x1Natural = x1Values.Select(v => Math.Round(info1.Center + v * info1.HalfRange, 4)).ToArray();
            var x2Natural = x2Values.Select(v => Math.Round(info2.Center + v * info2.HalfRange, 4)).ToArray();

            return new ResponseSurfaceData
            {
                Factor1 = factor1,
                Factor2 = factor2,
                X1 = x1Natural,
                X2 = x2Natural,
                Z = zValues,
                GridSize = gridSize
            };
        }

        private double[] BuildPredictionVector(Dictionary<string, double> codedValues)
        {
            if (_colNames == null || _fitResult == null) return Array.Empty<double>();

            var x0 = new double[_fitResult.P];
            x0[0] = 1.0; // 截距

            for (int j = 1; j < _fitResult.P; j++)
            {
                string name = _colNames[j];
                if (name.Contains("²"))
                {
                    string factor = name.Replace("²", "");
                    x0[j] = codedValues.TryGetValue(factor, out var v) ? v * v : 0;
                }
                else if (name.Contains(":"))
                {
                    var parts = name.Split(':');
                    double v1 = codedValues.TryGetValue(parts[0], out var a) ? a : 0;
                    double v2 = codedValues.TryGetValue(parts[1], out var b) ? b : 0;
                    x0[j] = v1 * v2;
                }
                else
                {
                    x0[j] = codedValues.TryGetValue(name, out var v) ? v : 0;
                }
            }

            return x0;
        }

        // ═══ LOF (同 FullFactorialAnalyzer) ═══

        private LackOfFitOutput? CalculateLackOfFit()
        {
            if (_fitResult == null) return null;
            var groups = new Dictionary<string, List<int>>();
            for (int i = 0; i < _rawData.Count; i++)
            {
                var key = string.Join("|", _rawData[i].OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}"));
                if (!groups.ContainsKey(key)) groups[key] = new List<int>();
                groups[key].Add(i);
            }
            var repeated = groups.Where(g => g.Value.Count > 1).ToList();
            if (repeated.Count == 0) return null;

            double ssPE = 0; int dfPE = 0;
            foreach (var g in repeated)
            {
                double mean = g.Value.Average(idx => _responses[idx]);
                foreach (int idx in g.Value) { double d = _responses[idx] - mean; ssPE += d * d; }
                dfPE += g.Value.Count - 1;
            }
            if (dfPE == 0) return null;

            double ssLOF = _fitResult.SSResidual - ssPE;
            int dfLOF = _fitResult.DfResidual - dfPE;
            if (dfLOF <= 0) return null;

            double msLOF = ssLOF / dfLOF, msPE = ssPE / dfPE;
            double fLOF = msPE > 1e-15 ? msLOF / msPE : 0;
            double pLOF = 1.0 - FisherSnedecor.CDF(dfLOF, dfPE, fLOF);

            return new LackOfFitOutput { SS_LOF = ssLOF, DF_LOF = dfLOF, MS_LOF = msLOF, SS_PureError = ssPE, DF_PureError = dfPE, MS_PureError = msPE, FStatistic = fLOF, PValue = pLOF, IsSignificant = pLOF < 0.05 };
        }

        // ═══ 填充方法 ═══

        private void PopulateCoefficients(RsmAnalysisResult result)
        {
            if (_fitResult == null) return;
            result.Coefficients = new List<CoefficientOutput>();
            for (int j = 0; j < _fitResult.P; j++)
            {
                result.Coefficients.Add(new CoefficientOutput
                {
                    Term = _fitResult.TermNames[j],
                    Coefficient = Math.Round(_fitResult.Coefficients[j], 6),
                    StdError = Math.Round(_fitResult.StdErrors[j], 6),
                    TValue = Math.Round(_fitResult.TValues[j], 4),
                    PValue = Math.Round(_fitResult.PValues[j], 6),
                    Effect = _fitResult.TermNames[j] != "Intercept" ? Math.Round(_fitResult.Coefficients[j] * 2, 6) : 0,
                    VIF = _fitResult.VIF.TryGetValue(_fitResult.TermNames[j], out var v) ? v : 0
                });
            }
            result.CodingInfo = _encoder.GetCodingInfo();
        }

        private void PopulateModelSummary(RsmAnalysisResult result)
        {
            if (_fitResult == null) return;
            result.ModelSummary = new ModelSummaryOutput
            {
                RSquared = Math.Round(_fitResult.RSquared, 6),
                RSquaredAdj = Math.Round(_fitResult.RSquaredAdj, 6),
                RSquaredPred = Math.Round(_fitResult.RSquaredPred, 6),
                RMSE = Math.Round(_fitResult.RMSE, 6),
                PRESS = Math.Round(_fitResult.PRESS, 4),
                AdequatePrecision = Math.Round(_fitResult.AdequatePrecision, 4),
                ModelP = Math.Round(_fitResult.FPValue, 6),
                Sigma = _fitResult.Sigma,
                TCrit = _fitResult.TCrit,
                ParamCount = _fitResult.P
            };
            result.XtxInvFlat = _fitResult.XtxInvFlat;
        }

        private void PopulateAnovaTable(RsmAnalysisResult result)
        {
            if (_fitResult == null) return;
            result.AnovaTable = new List<AnovaRowOutput>();

            // 分层: 线性 / 平方 / 交互
            var linearTerms = new List<int>();
            var squareTerms = new List<int>();
            var interTerms = new List<int>();

            for (int j = 1; j < _fitResult.P; j++)
            {
                string name = _fitResult.TermNames[j];
                if (name.Contains("²")) squareTerms.Add(j);
                else if (name.Contains(":")) interTerms.Add(j);
                else linearTerms.Add(j);
            }

            // 模型汇总
            result.AnovaTable.Add(new AnovaRowOutput
            {
                Source = "模型", DF = _fitResult.DfModel,
                SS = Math.Round(_fitResult.SSModel, 6),
                MS = Math.Round(_fitResult.SSModel / Math.Max(_fitResult.DfModel, 1), 6),
                FValue = Math.Round(_fitResult.FStatistic, 4),
                PValue = Math.Round(_fitResult.FPValue, 6)
            });

            AddTermGroup(result, "  线性", linearTerms);
            AddTermGroup(result, "  平方", squareTerms);
            AddTermGroup(result, "  双因子交互", interTerms);

            result.AnovaTable.Add(new AnovaRowOutput { Source = "误差", DF = _fitResult.DfResidual, SS = Math.Round(_fitResult.SSResidual, 6), MS = Math.Round(_fitResult.MSE, 6) });
            result.AnovaTable.Add(new AnovaRowOutput { Source = "合计", DF = _fitResult.N - 1, SS = Math.Round(_fitResult.SSTotal, 6) });
        }

        private void AddTermGroup(RsmAnalysisResult result, string groupName, List<int> indices)
        {
            if (indices.Count == 0) return;
            double groupSS = 0; int groupDF = 0;
            foreach (int j in indices) { groupSS += _fitResult!.TValues[j] * _fitResult.TValues[j] * _fitResult.MSE; groupDF++; }
            double groupMS = groupDF > 0 ? groupSS / groupDF : 0;
            double groupF = _fitResult!.MSE > 1e-15 ? groupMS / _fitResult.MSE : 0;
            double groupP = groupDF > 0 && _fitResult.DfResidual > 0 ? 1.0 - FisherSnedecor.CDF(groupDF, _fitResult.DfResidual, groupF) : 1;

            result.AnovaTable.Add(new AnovaRowOutput { Source = groupName, DF = groupDF, SS = Math.Round(groupSS, 6), MS = Math.Round(groupMS, 6), FValue = Math.Round(groupF, 4), PValue = Math.Round(groupP, 6) });
            foreach (int j in indices)
            {
                double ssJ = _fitResult.TValues[j] * _fitResult.TValues[j] * _fitResult.MSE;
                result.AnovaTable.Add(new AnovaRowOutput
                {
                    Source = "    " + _fitResult.TermNames[j], DF = 1, SS = Math.Round(ssJ, 6), MS = Math.Round(ssJ, 6),
                    FValue = Math.Round(_fitResult.TValues[j] * _fitResult.TValues[j], 4),
                    PValue = Math.Round(_fitResult.PValues[j], 6)
                });
            }
        }

        private void PopulateResidualDiagnostics(RsmAnalysisResult result)
        {
            if (_fitResult == null) return;
            result.ResidualDiagnostics = new ResidualDiagOutput
            {
                Fitted = _fitResult.FittedValues, Residuals = _fitResult.Residuals,
                StandardizedResiduals = _fitResult.StandardizedResiduals,
                StudentizedResiduals = _fitResult.StudentizedResiduals,
                CooksDistance = _fitResult.CooksDistance, Leverage = _fitResult.Leverage,
                RunOrder = Enumerable.Range(1, _fitResult.N).Select(i => (double)i).ToArray()
            };
        }

        private void PopulateEffectsPareto(RsmAnalysisResult result, double alpha = 0.05)
        {
            if (_fitResult == null) return;
            result.EffectsPareto = new List<ParetoEffectOutput>();
            double tCrit = _fitResult.DfResidual > 0 ? StudentT.InvCDF(0, 1, _fitResult.DfResidual, 1.0 - alpha / 2.0) : 2.0;
            for (int j = 1; j < _fitResult.P; j++)
            {
                result.EffectsPareto.Add(new ParetoEffectOutput
                {
                    Term = _fitResult.TermNames[j], AbsT = Math.Round(Math.Abs(_fitResult.TValues[j]), 4),
                    PValue = Math.Round(_fitResult.PValues[j], 6), IsSignificant = _fitResult.PValues[j] < alpha,
                    Effect = Math.Round(_fitResult.Coefficients[j] * 2, 6)
                });
            }
            result.EffectsPareto.Sort((a, b) => b.AbsT.CompareTo(a.AbsT));
            result.ParetoTCritical = Math.Round(tCrit, 4);
        }

        private string GenerateRsmSuggestion(RsmAnalysisResult result)
        {
            if (result.ModelSummary.RSquared < 0.80)
                return $"R²={result.ModelSummary.RSquared:F3} 过低，建议补点或检查数据质量";
            if (result.LackOfFit?.IsSignificant == true)
                return "LOF 显著，模型可能不足，建议检查是否需要更高阶项或变换";
            if (result.StationaryPoint?.Type?.Contains("鞍点") == true)
                return "稳定点为鞍点，建议用 Ridge analysis 或 Desirability 做约束优化";
            return "模型合格，建议用 Desirability 做多响应寻优，然后验证实验";
        }

        public OlsFitResult? FitResult => _fitResult;
    }

    // ═══ 输出结构 ═══

    public class RsmAnalysisResult
    {
        public string ResponseName { get; set; } = "";
        public string? Error { get; set; }

        public List<CoefficientOutput> Coefficients { get; set; } = new();
        public ModelSummaryOutput ModelSummary { get; set; } = new();
        public List<AnovaRowOutput> AnovaTable { get; set; } = new();
        public ResidualDiagOutput? ResidualDiagnostics { get; set; }
        public List<ParetoEffectOutput> EffectsPareto { get; set; } = new();
        public LackOfFitOutput? LackOfFit { get; set; }
        public StationaryPointOutput? StationaryPoint { get; set; }
        public Dictionary<string, CodingInfoExport> CodingInfo { get; set; } = new();
        public double[] XtxInvFlat { get; set; } = Array.Empty<double>();
        public double ParetoTCritical { get; set; }
        public string NextStepSuggestion { get; set; } = "";
    }

    public class StationaryPointOutput
    {
        public string Type { get; set; } = "";
        public double[] Eigenvalues { get; set; } = Array.Empty<double>();
        public Dictionary<string, double> CodedCoordinates { get; set; } = new();
        public Dictionary<string, double> NaturalCoordinates { get; set; } = new();
        public double PredictedResponse { get; set; }
    }

    public class ResponseSurfaceData
    {
        public string? Error { get; set; }
        public string Factor1 { get; set; } = "";
        public string Factor2 { get; set; } = "";
        public double[] X1 { get; set; } = Array.Empty<double>();
        public double[] X2 { get; set; } = Array.Empty<double>();
        public double[,] Z { get; set; } = new double[0, 0];
        public int GridSize { get; set; }
    }
}

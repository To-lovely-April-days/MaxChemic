using System;
using System.Collections.Generic;
using System.Linq;
using MaxChemical.Modules.DOE.OLS.Core;
using MathNet.Numerics.Distributions;

namespace MaxChemical.Modules.DOE.OLS.Analyzers
{
    /// <summary>
    /// 全因子设计分析器 (FullFactorial)
    /// 
    /// 对标 Minitab "分析因子设计" 的完整输出:
    ///   1. 用角点数据拟合交互模型 y = β₀ + Σβᵢxᵢ + Σβᵢⱼxᵢxⱼ
    ///   2. 有中心点时做弯曲检验 (Ct Pt): 比较角点预测均值与中心点实际均值
    ///   3. ANOVA 表 (Type III SS, Sum coding)
    ///   4. 系数表 (β, SE, t, p, VIF)
    ///   5. 模型摘要 (R², R²adj, R²pred, RMSE, AP, LOF)
    ///   6. 残差诊断四图数据
    ///   7. 效应 Pareto
    ///   8. 弯曲检验结果 → 决策建议（爬坡 or RSM）
    /// 
    /// 特有逻辑:
    ///   - 角点/中心点自动检测（编码值所有连续因子接近 0 → 中心点）
    ///   - 弯曲检验 t 统计量和 p 值
    ///   - 弯曲不显著 → 建议最速上升
    ///   - 弯曲显著 → 建议直接进入 RSM
    /// </summary>
    public class FullFactorialAnalyzer
    {
        private readonly FactorEncoder _encoder;
        private readonly List<FactorDefinition> _factors;
        private readonly List<Dictionary<string, object>> _rawData;
        private readonly double[] _responses;
        private readonly string _responseName;

        // 拟合结果（拟合后可访问）
        private OlsFitResult? _fitResult;
        private CurvatureTestOutput? _curvatureResult;
        private LackOfFitOutput? _lofResult;
        private readonly int _modelOrder;


        public FullFactorialAnalyzer(
            List<FactorDefinition> factors,
            List<Dictionary<string, object>> data,
            double[] responses,
            string responseName,
            int modelOrder = 0)  // 0 表示使用默认值（所有阶）
        {
            if (data.Count != responses.Length)
                throw new ArgumentException("因子数据行数与响应值数量不匹配");

            _factors = factors;
            _encoder = new FactorEncoder(factors);
            _rawData = data;
            _responses = responses;
            _responseName = responseName;

            // modelOrder <= 0 表示使用全阶交互（原始行为）
            var allNames = _encoder.GetContinuousCodedNames()
                .Concat(_encoder.GetCategoricalNames()).ToList();
            _modelOrder = modelOrder > 0 ? Math.Min(modelOrder, allNames.Count) : allNames.Count;
        }

        /// <summary>
        /// 执行完整分析
        /// </summary>
        public FullFactorialResult Analyze()
        {
            var result = new FullFactorialResult { ResponseName = _responseName };

            // ── 1. 检测中心点 ──
            var centerMask = DetectCenterPoints();
            int nCenter = centerMask.Count(m => m);
            int nCorner = centerMask.Count(m => !m);
            bool hasCenterPoints = nCenter >= 2;

            result.TotalRuns = _rawData.Count;
            result.CornerPoints = nCorner;
            result.CenterPoints = nCenter;

            // ── 2. 构建模型项（交互模型） ──
            var terms = DesignMatrixBuilder.FactorialTermsUpToOrder(_encoder, _modelOrder);

            if (hasCenterPoints)
            {
                // ═══════════════════════════════════════════════════
                // Minitab 模式: 角点单独拟合 + 中心点单独做弯曲检验
                // ═══════════════════════════════════════════════════

                // 分离角点和中心点数据
                var cornerData = new List<Dictionary<string, object>>();
                var cornerResponses = new List<double>();
                var centerResponses = new List<double>();

                for (int i = 0; i < _rawData.Count; i++)
                {
                    if (centerMask[i])
                        centerResponses.Add(_responses[i]);
                    else
                    {
                        cornerData.Add(_rawData[i]);
                        cornerResponses.Add(_responses[i]);
                    }
                }

                // 只用角点数据构建设计矩阵和拟合
                var cornerEncoder = new FactorEncoder(_factors);
                var cornerBuilder = new DesignMatrixBuilder(cornerEncoder, cornerData);
                var (X, colNames) = cornerBuilder.Build(terms);

                var solver = new OlsSolver();
                try
                {
                    _fitResult = solver.Fit(X, cornerResponses.ToArray(), colNames);
                }
                catch (Exception ex)
                {
                    result.Error = $"OLS 拟合失败: {ex.Message}";
                    return result;
                }

                // ═══════════════════════════════════════════════════
                // ★ 关键: 用中心点纯误差修正 MSE
                // 
                // Minitab 做法:
                //   角点 8 个拟合 8 参数 → 角点自身残差 SS = 0, df = 0
                //   中心点纯误差 SS_PE = Σ(y_ci - ȳ_c)², df_PE = n_center - 1
                //   合并 MSE = (SS_corner_resid + SS_PE) / (df_corner_resid + df_PE)
                //   所有 SE、t、p 基于这个合并 MSE
                // ═══════════════════════════════════════════════════
                double ssPE = 0;
                double yBarCenter = centerResponses.Average();
                foreach (var yc in centerResponses)
                    ssPE += (yc - yBarCenter) * (yc - yBarCenter);
                int dfPE = centerResponses.Count - 1;

                double ssResidCorner = _fitResult.SSResidual; // 角点残差 SS（可能 = 0）
                int dfResidCorner = _fitResult.DfResidual;     // 角点残差 df（可能 = 0）

                double mergedSSResid = ssResidCorner + ssPE;
                int mergedDfResid = dfResidCorner + dfPE;
                double mergedMSE = mergedDfResid > 0 ? mergedSSResid / mergedDfResid : 0;
                double mergedSigma = Math.Sqrt(mergedMSE);

                // 用合并 MSE 重算所有系数的 SE、t、p
                _fitResult.MSE = mergedMSE;
                _fitResult.Sigma = mergedSigma;
                _fitResult.RMSE = mergedSigma;
                _fitResult.SSResidual = mergedSSResid;
                _fitResult.DfResidual = mergedDfResid;

                // 重算 SS_total（用全部数据）
                double yBarAll = _responses.Average();
                _fitResult.SSTotal = _responses.Sum(y => (y - yBarAll) * (y - yBarAll));
                _fitResult.SSModel = _fitResult.SSTotal - mergedSSResid;
                _fitResult.N = _rawData.Count; // 全部数据量

                // R² 基于全部数据
                _fitResult.RSquared = _fitResult.SSTotal > 1e-15 ? 1.0 - mergedSSResid / _fitResult.SSTotal : 0;
                _fitResult.RSquaredAdj = _fitResult.SSTotal > 1e-15 && mergedDfResid > 0
                    ? 1.0 - (mergedSSResid / mergedDfResid) / (_fitResult.SSTotal / (_rawData.Count - 1))
                    : 0;

                // 重算 PRESS（用角点 + 中心点纯误差的贡献）
                // 简化: 角点 PRESS + 中心点贡献
                double pressCorner = _fitResult.PRESS;
                double pressCenter = centerResponses.Count > 1
                    ? centerResponses.Sum(yc => Math.Pow((yc - yBarCenter) * centerResponses.Count / (centerResponses.Count - 1), 2))
                    : 0;
                _fitResult.PRESS = pressCorner + pressCenter;
                _fitResult.RSquaredPred = _fitResult.SSTotal > 1e-15 ? 1.0 - _fitResult.PRESS / _fitResult.SSTotal : 0;

                // 重算系数 SE、t、p
                for (int j = 0; j < _fitResult.P; j++)
                {
                    double se = Math.Sqrt(mergedMSE * _fitResult.XtXInverse[j, j]);
                    _fitResult.StdErrors[j] = se;
                    _fitResult.TValues[j] = se > 1e-15 ? _fitResult.Coefficients[j] / se : 0;
                    _fitResult.PValues[j] = mergedDfResid > 0
                        ? 2.0 * (1.0 - StudentT.CDF(0, 1, mergedDfResid, Math.Abs(_fitResult.TValues[j])))
                        : 1.0;
                }

                // t 临界值
                _fitResult.TCrit = mergedDfResid > 0
                    ? StudentT.InvCDF(0, 1, mergedDfResid, 0.975) : 0;

                // F 统计量
                int dfModel = _fitResult.DfModel;
                double msModel = dfModel > 0 ? _fitResult.SSModel / dfModel : 0;
                _fitResult.FStatistic = mergedMSE > 1e-15 ? msModel / mergedMSE : 0;
                _fitResult.FPValue = dfModel > 0 && mergedDfResid > 0
                    ? 1.0 - FisherSnedecor.CDF(dfModel, mergedDfResid, _fitResult.FStatistic) : 1.0;

                // Adequate Precision
                _fitResult.AdequatePrecision = CalculateAdequatePrecision(
                    _fitResult.FittedValues, mergedMSE, _fitResult.P, nCorner);

                // 用角点模型的 encoder
                _cornerEncoder = cornerEncoder;

                // 弯曲检验（先于 ANOVA，因为 ANOVA 需要弯曲 SS）
                _curvatureResult = PerformCurvatureTest(centerMask);
                result.CurvatureTest = _curvatureResult;

                // 填充基本结果（基于角点模型 + 弯曲结果）
                PopulateCoefficients(result);
                PopulateModelSummary(result);
                PopulateAnovaTable(result);
                PopulateResidualDiagnosticsFullData(result, centerMask);
                PopulateEffectsPareto(result);

                // 在系数表末尾追加 Ct Pt 行
                if (_curvatureResult != null && _curvatureResult.HasCurvatureTest)
                {
                    result.Coefficients.Add(new CoefficientOutput
                    {
                        Term = "Ct Pt",
                        Effect = Math.Round(_curvatureResult.Difference * 2, 6),
                        Coefficient = Math.Round(_curvatureResult.CtPtCoefficient, 6),
                        StdError = Math.Round(_curvatureResult.SE, 6),
                        TValue = Math.Round(_curvatureResult.TStatistic, 4),
                        PValue = Math.Round(_curvatureResult.PValue, 6),
                        VIF = 1.0
                    });
                }
            }
            else
            {
                // 无中心点: 全部数据直接拟合交互模型
                var builder = new DesignMatrixBuilder(_encoder, _rawData);
                var (X, colNames) = builder.Build(terms);

                var solver = new OlsSolver();
                try
                {
                    _fitResult = solver.Fit(X, _responses, colNames);
                }
                catch (Exception ex)
                {
                    result.Error = $"OLS 拟合失败: {ex.Message}";
                    return result;
                }

                PopulateCoefficients(result);
                PopulateModelSummary(result);
                PopulateAnovaTable(result);
                PopulateResidualDiagnostics(result);
                PopulateEffectsPareto(result);
            }

            // ── LOF ──
            _lofResult = CalculateLackOfFit();
            result.LackOfFit = _lofResult;
            if (_lofResult != null)
                result.ModelSummary.LackOfFitP = _lofResult.PValue;

            // ── 决策建议 ──
            result.NextStepSuggestion = GenerateNextStepSuggestion(result);

            return result;
        }

        // 保存角点 encoder（弯曲模式下用于后续预测）
        private FactorEncoder? _cornerEncoder;

        // ═══════════════════════════════════════════════════════
        // 中心点检测
        // ═══════════════════════════════════════════════════════

        private bool[] DetectCenterPoints()
        {
            var contNames = _encoder.GetContinuousCodedNames();
            if (contNames.Count == 0)
                return new bool[_rawData.Count];

            var codedData = _encoder.EncodeAll(_rawData);
            var codedColNames = _encoder.GetCodedColumnNames();

            // 找到连续因子的列索引
            var contColIdx = new List<int>();
            for (int j = 0; j < codedColNames.Count; j++)
            {
                if (contNames.Contains(codedColNames[j]))
                    contColIdx.Add(j);
            }

            var mask = new bool[_rawData.Count];
            double tolerance = 0.01;

            for (int i = 0; i < _rawData.Count; i++)
            {
                bool isCenter = true;
                foreach (int j in contColIdx)
                {
                    if (Math.Abs(codedData[i, j]) > tolerance)
                    {
                        isCenter = false;
                        break;
                    }
                }
                mask[i] = isCenter;
            }

            return mask;
        }

        // ═══════════════════════════════════════════════════════
        // 弯曲检验 — 对标 Minitab Ct Pt
        // ═══════════════════════════════════════════════════════

        /// <summary>
        /// Minitab 弯曲检验:
        ///   H0: 响应面在中心附近没有曲率（一阶模型足够）
        ///   H1: 存在曲率（需要二阶模型）
        /// 
        /// 检验统计量:
        ///   t = (ȳ_center - ȳ_corner_pred) / SE
        ///   其中 ȳ_corner_pred = 截距 β₀（编码值下角点中心 = 0 时的预测值）
        ///   SE = σ̂ × sqrt(1/n_center + 1/n_corner)
        /// </summary>
        private CurvatureTestOutput PerformCurvatureTest(bool[] centerMask)
        {
            if (_fitResult == null) return new CurvatureTestOutput();

            int nCenter = centerMask.Count(m => m);
            int nCorner = centerMask.Count(m => !m);

            // 中心点响应均值
            double yBarCenter = 0;
            int cnt = 0;
            for (int i = 0; i < _responses.Length; i++)
            {
                if (centerMask[i])
                {
                    yBarCenter += _responses[i];
                    cnt++;
                }
            }
            yBarCenter /= cnt;

            // 角点响应均值
            double yBarCorner = 0;
            cnt = 0;
            for (int i = 0; i < _responses.Length; i++)
            {
                if (!centerMask[i])
                {
                    yBarCorner += _responses[i];
                    cnt++;
                }
            }
            yBarCorner /= cnt;

            // 弯曲检验
            double diff = yBarCenter - yBarCorner;
            double se = _fitResult.Sigma * Math.Sqrt(1.0 / nCenter + 1.0 / nCorner);
            double tStat = se > 1e-15 ? diff / se : 0;
            double pValue = _fitResult.DfResidual > 0
                ? 2.0 * (1.0 - StudentT.CDF(0, 1, _fitResult.DfResidual, Math.Abs(tStat)))
                : 1.0;

            return new CurvatureTestOutput
            {
                HasCurvatureTest = true,
                CenterMean = yBarCenter,
                CornerMean = yBarCorner,
                Difference = diff,
                SE = se,
                TStatistic = tStat,
                PValue = pValue,
                IsSignificant = pValue < 0.05,
                // Ct Pt 系数 = 中心点均值 - 角点预测均值（= 截距）
                // Minitab: Ct Pt 系数 = (ȳ_center - ȳ_corner) / 2 或直接 = diff
                CtPtCoefficient = diff
            };
        }

        // ═══════════════════════════════════════════════════════
        // Lack of Fit — 对标 Minitab
        // ═══════════════════════════════════════════════════════

        private LackOfFitOutput? CalculateLackOfFit()
        {
            if (_fitResult == null) return null;

            // 按因子组合分组，计算纯误差
            var groups = new Dictionary<string, List<int>>();
            for (int i = 0; i < _rawData.Count; i++)
            {
                // 用因子值组合作为 key
                var key = string.Join("|", _rawData[i]
                    .OrderBy(kv => kv.Key)
                    .Select(kv => $"{kv.Key}={kv.Value}"));

                if (!groups.ContainsKey(key))
                    groups[key] = new List<int>();
                groups[key].Add(i);
            }

            // 只有存在重复组时才能计算
            var repeatedGroups = groups.Where(g => g.Value.Count > 1).ToList();
            if (repeatedGroups.Count == 0) return null;

            // Pure Error SS
            double ssPE = 0;
            int dfPE = 0;
            foreach (var group in repeatedGroups)
            {
                var indices = group.Value;
                double groupMean = indices.Average(idx => _responses[idx]);
                foreach (int idx in indices)
                {
                    double diff = _responses[idx] - groupMean;
                    ssPE += diff * diff;
                }
                dfPE += indices.Count - 1;
            }

            if (dfPE == 0) return null;

            // LOF SS = SS_Residual - SS_PureError
            double ssLOF = _fitResult.SSResidual - ssPE;
            int dfLOF = _fitResult.DfResidual - dfPE;

            if (dfLOF <= 0) return null;

            double msLOF = ssLOF / dfLOF;
            double msPE = ssPE / dfPE;
            double fLOF = msPE > 1e-15 ? msLOF / msPE : 0;
            double pLOF = FisherSnedecor.CDF(dfLOF, dfPE, fLOF);
            pLOF = 1.0 - pLOF;

            return new LackOfFitOutput
            {
                SS_LOF = ssLOF,
                DF_LOF = dfLOF,
                MS_LOF = msLOF,
                SS_PureError = ssPE,
                DF_PureError = dfPE,
                MS_PureError = msPE,
                FStatistic = fLOF,
                PValue = pLOF,
                IsSignificant = pLOF < 0.05
            };
        }

        // ═══════════════════════════════════════════════════════
        // 结果填充
        // ═══════════════════════════════════════════════════════

        private void PopulateCoefficients(FullFactorialResult result)
        {
            if (_fitResult == null) return;

            result.Coefficients = new List<CoefficientOutput>();
            for (int j = 0; j < _fitResult.P; j++)
            {
                var coeff = new CoefficientOutput
                {
                    Term = _fitResult.TermNames[j],
                    Coefficient = Math.Round(_fitResult.Coefficients[j], 6),
                    StdError = Math.Round(_fitResult.StdErrors[j], 6),
                    TValue = Math.Round(_fitResult.TValues[j], 4),
                    PValue = Math.Round(_fitResult.PValues[j], 6),
                    Effect = _fitResult.TermNames[j] != "Intercept"
                        ? Math.Round(_fitResult.Coefficients[j] * 2, 6) : 0,
                    VIF = _fitResult.VIF.TryGetValue(_fitResult.TermNames[j], out var v) ? v : 0
                };
                result.Coefficients.Add(coeff);
            }

            // 编码信息
            result.CodingInfo = _encoder.GetCodingInfo();
        }

        private void PopulateModelSummary(FullFactorialResult result)
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

            // (XᵀX)⁻¹ 传给 CSharpOlsPredictor
            result.XtxInvFlat = _fitResult.XtxInvFlat;
        }

        private void PopulateAnovaTable(FullFactorialResult result)
        {
            if (_fitResult == null) return;

            result.AnovaTable = new List<AnovaRowOutput>();
            double mse = _fitResult.MSE;

            // ── 分类各项 ──
            var linearIdx = new List<int>();    // 主效应
            var twoWayIdx = new List<int>();    // 二因子交互
            var threeWayIdx = new List<int>();  // 三因子及以上交互

            for (int j = 1; j < _fitResult.P; j++)
            {
                string name = _fitResult.TermNames[j];
                int colonCount = name.Count(c => c == ':');
                if (colonCount == 0) linearIdx.Add(j);
                else if (colonCount == 1) twoWayIdx.Add(j);
                else threeWayIdx.Add(j);
            }

            // ── 模型汇总行（含弯曲的 df） ──
            int modelDF = _fitResult.DfModel;
            double modelSS = _fitResult.SSModel;
            // 弯曲 SS 需要加到模型 SS 里
            double curvatureSS = 0;
            if (_curvatureResult != null && _curvatureResult.HasCurvatureTest)
            {
                curvatureSS = _curvatureResult.TStatistic * _curvatureResult.TStatistic * mse;
                modelDF += 1; // 弯曲占 1 个 df
                modelSS += curvatureSS;
            }

            double modelMS = modelDF > 0 ? modelSS / modelDF : 0;
            double modelF = mse > 1e-15 ? modelMS / mse : 0;
            double modelP = modelDF > 0 && _fitResult.DfResidual > 0
                ? 1.0 - FisherSnedecor.CDF(modelDF, _fitResult.DfResidual, modelF) : 1;

            result.AnovaTable.Add(new AnovaRowOutput
            {
                Source = "模型",
                DF = modelDF,
                SS = Math.Round(modelSS, 4),
                MS = Math.Round(modelMS, 4),
                FValue = Math.Round(modelF, 2),
                PValue = Math.Round(modelP, 4)
            });

            // ── 线性汇总 + 各主效应 ──
            if (linearIdx.Count > 0)
            {
                AddGroupWithItems(result, "  线性", linearIdx, mse);
            }

            // ── 2因子交互汇总 + 各交互项 ──
            if (twoWayIdx.Count > 0)
            {
                AddGroupWithItems(result, "  2 因子交互作用", twoWayIdx, mse);
            }

            // ── 3因子交互汇总 + 各项 ──
            if (threeWayIdx.Count > 0)
            {
                AddGroupWithItems(result, "  3 因子交互作用", threeWayIdx, mse);
            }

            // ── 弯曲行 ──
            if (_curvatureResult != null && _curvatureResult.HasCurvatureTest)
            {
                double curvF = mse > 1e-15 ? curvatureSS / mse : 0;
                double curvP = _fitResult.DfResidual > 0
                    ? 1.0 - FisherSnedecor.CDF(1, _fitResult.DfResidual, curvF) : 1;

                result.AnovaTable.Add(new AnovaRowOutput
                {
                    Source = "  弯曲",
                    DF = 1,
                    SS = Math.Round(curvatureSS, 4),
                    MS = Math.Round(curvatureSS, 4),
                    FValue = Math.Round(curvF, 2),
                    PValue = Math.Round(curvP, 4)
                });
            }

            // ── 误差行 ──
            result.AnovaTable.Add(new AnovaRowOutput
            {
                Source = "误差",
                DF = _fitResult.DfResidual,
                SS = Math.Round(_fitResult.SSResidual, 4),
                MS = Math.Round(mse, 4)
            });

            // ── 合计行 ──
            result.AnovaTable.Add(new AnovaRowOutput
            {
                Source = "合计",
                DF = _fitResult.N - 1,
                SS = Math.Round(_fitResult.SSTotal, 4)
            });
        }

        /// <summary>添加分组汇总行 + 各子项行</summary>
        private void AddGroupWithItems(FullFactorialResult result, string groupName,
            List<int> indices, double mse)
        {
            double groupSS = 0;
            foreach (int j in indices)
                groupSS += _fitResult!.TValues[j] * _fitResult.TValues[j] * mse;

            int groupDF = indices.Count;
            double groupMS = groupDF > 0 ? groupSS / groupDF : 0;
            double groupF = mse > 1e-15 ? groupMS / mse : 0;
            double groupP = groupDF > 0 && _fitResult!.DfResidual > 0
                ? 1.0 - FisherSnedecor.CDF(groupDF, _fitResult.DfResidual, groupF) : 1;

            result.AnovaTable.Add(new AnovaRowOutput
            {
                Source = groupName,
                DF = groupDF,
                SS = Math.Round(groupSS, 4),
                MS = Math.Round(groupMS, 4),
                FValue = Math.Round(groupF, 2),
                PValue = Math.Round(groupP, 4)
            });

            foreach (int j in indices)
            {
                double ssJ = _fitResult!.TValues[j] * _fitResult.TValues[j] * mse;
                double fJ = mse > 1e-15 ? ssJ / mse : 0;

                result.AnovaTable.Add(new AnovaRowOutput
                {
                    Source = "    " + _fitResult.TermNames[j],
                    DF = 1,
                    SS = Math.Round(ssJ, 4),
                    MS = Math.Round(ssJ, 4),
                    FValue = Math.Round(fJ, 2),
                    PValue = Math.Round(_fitResult.PValues[j], 4)
                });
            }
        }

        private void PopulateResidualDiagnostics(FullFactorialResult result)
        {
            if (_fitResult == null) return;

            result.ResidualDiagnostics = new ResidualDiagOutput
            {
                Fitted = _fitResult.FittedValues,
                Residuals = _fitResult.Residuals,
                StandardizedResiduals = _fitResult.StandardizedResiduals,
                StudentizedResiduals = _fitResult.StudentizedResiduals,
                CooksDistance = _fitResult.CooksDistance,
                Leverage = _fitResult.Leverage,
                RunOrder = Enumerable.Range(1, _fitResult.N).Select(i => (double)i).ToArray()
            };
        }

        /// <summary>
        /// 弯曲模式下的残差诊断：用全数据 + Ct Pt 虚拟变量拟合，得到每个点的残差
        /// 对标 Minitab：残差诊断基于含 Ct Pt 的完整模型
        /// </summary>
        private void PopulateResidualDiagnosticsFullData(FullFactorialResult result, bool[] centerMask)
        {
            if (_fitResult == null) return;

            int nTotal = _rawData.Count;
            double sigma = _fitResult.Sigma; // 已用中心点纯误差修正的 sigma

            // 用全数据 + Ct Pt 列重新构建 X 矩阵
            var terms = DesignMatrixBuilder.FullFactorialInteractionTerms(_encoder);
            terms.Add(ModelTerm.CtPt());
            var fullBuilder = new DesignMatrixBuilder(_encoder, _rawData);
            var (Xfull, colNamesFull) = fullBuilder.Build(terms, centerMask);

            // 用角点模型的系数 + Ct Pt 系数构建完整系数向量
            int pFull = colNamesFull.Length;
            var betaFull = new double[pFull];
            betaFull[0] = _fitResult.Coefficients[0]; // 截距

            // 映射角点模型的系数到完整模型
            for (int j = 1; j < pFull; j++)
            {
                string name = colNamesFull[j];
                if (name == "Ct Pt")
                {
                    // Ct Pt 系数
                    betaFull[j] = _curvatureResult?.CtPtCoefficient ?? 0;
                }
                else
                {
                    // 从角点模型查找对应系数
                    for (int k = 1; k < _fitResult.P; k++)
                    {
                        if (_fitResult.TermNames[k] == name)
                        {
                            betaFull[j] = _fitResult.Coefficients[k];
                            break;
                        }
                    }
                }
            }

            // 计算拟合值和残差
            var fitted = new double[nTotal];
            var residuals = new double[nTotal];
            var stdResid = new double[nTotal];

            for (int i = 0; i < nTotal; i++)
            {
                double yHat = 0;
                for (int j = 0; j < pFull; j++)
                    yHat += betaFull[j] * Xfull[i, j];
                fitted[i] = yHat;
                residuals[i] = _responses[i] - yHat;
                stdResid[i] = sigma > 1e-15 ? residuals[i] / sigma : 0;
            }

            result.ResidualDiagnostics = new ResidualDiagOutput
            {
                Fitted = fitted,
                Residuals = residuals,
                StandardizedResiduals = stdResid,
                StudentizedResiduals = stdResid,
                CooksDistance = new double[nTotal], // 简化
                Leverage = new double[nTotal],
                RunOrder = Enumerable.Range(1, nTotal).Select(idx => (double)idx).ToArray()
            };
        }

        private void PopulateEffectsPareto(FullFactorialResult result, double alpha = 0.05)
        {
            if (_fitResult == null) return;

            result.EffectsPareto = new List<ParetoEffectOutput>();

            // Bonferroni 校正的 t 临界值（不含 Ct Pt 项）
            int nTerms = 0;
            for (int j = 1; j < _fitResult.P; j++)
                if (_fitResult.TermNames[j] != "Ct Pt") nTerms++;

            double alphaCorrected = alpha / Math.Max(nTerms, 1);
            double tCritBonf = _fitResult.DfResidual > 0
                ? StudentT.InvCDF(0, 1, _fitResult.DfResidual, 1.0 - alphaCorrected / 2.0)
                : 2.0;
            double tCritStd = _fitResult.DfResidual > 0
                ? StudentT.InvCDF(0, 1, _fitResult.DfResidual, 1.0 - alpha / 2.0)
                : 2.0;

            for (int j = 1; j < _fitResult.P; j++)
            {
                // Pareto 不显示 Ct Pt（弯曲检验项）
                if (_fitResult.TermNames[j] == "Ct Pt") continue;

                result.EffectsPareto.Add(new ParetoEffectOutput
                {
                    Term = _fitResult.TermNames[j],
                    AbsT = Math.Round(Math.Abs(_fitResult.TValues[j]), 4),
                    PValue = Math.Round(_fitResult.PValues[j], 6),
                    IsSignificant = _fitResult.PValues[j] < alpha,
                    Effect = Math.Round(_fitResult.Coefficients[j] * 2, 6)
                });
            }

            // 按 |t| 降序排列
            result.EffectsPareto.Sort((a, b) => b.AbsT.CompareTo(a.AbsT));
            result.ParetoTCritical = Math.Round(tCritStd, 4);
            result.ParetoBonferroniTCritical = Math.Round(tCritBonf, 4);
        }

        // ═══════════════════════════════════════════════════════
        // 决策建议
        // ═══════════════════════════════════════════════════════

        private string GenerateNextStepSuggestion(FullFactorialResult result)
        {
            if (result.CurvatureTest != null && result.CurvatureTest.HasCurvatureTest)
            {
                if (result.CurvatureTest.IsSignificant)
                {
                    return "弯曲显著 → 建议直接进入 RSM（CCD 或 BBD），拟合二阶模型定位最优点";
                }
                else
                {
                    return "弯曲不显著 → 当前区域响应面近似平面，建议沿最速上升方向爬坡移动到近最优区域";
                }
            }

            // 无中心点时
            if (result.LackOfFit != null && result.LackOfFit.IsSignificant)
            {
                return "LOF 显著 → 模型拟合不足，建议补充中心点或轴点进入 RSM";
            }

            return "建议补充中心点以检测弯曲，或直接进入 RSM";
        }

        // ═══════════════════════════════════════════════════════
        // 公开属性
        // ═══════════════════════════════════════════════════════

        private double CalculateAdequatePrecision(double[] fitted, double mse, int p, int n)
        {
            if (mse < 1e-15 || n == 0 || fitted.Length == 0) return 0;
            double range = fitted.Max() - fitted.Min();
            if (range < 1e-15) return 0;
            double meanVarPred = mse * p / (double)n;
            return range / Math.Sqrt(meanVarPred);
        }

        /// <summary>获取底层拟合结果（供高级用途）</summary>
        public OlsFitResult? FitResult => _fitResult;
    }

    // ═══════════════════════════════════════════════════════
    // 输出数据结构
    // ═══════════════════════════════════════════════════════

    public class FullFactorialResult
    {
        public string ResponseName { get; set; } = "";
        public string? Error { get; set; }
        public int TotalRuns { get; set; }
        public int CornerPoints { get; set; }
        public int CenterPoints { get; set; }

        public List<CoefficientOutput> Coefficients { get; set; } = new();
        public ModelSummaryOutput ModelSummary { get; set; } = new();
        public List<AnovaRowOutput> AnovaTable { get; set; } = new();
        public ResidualDiagOutput? ResidualDiagnostics { get; set; }
        public List<ParetoEffectOutput> EffectsPareto { get; set; } = new();
        public CurvatureTestOutput? CurvatureTest { get; set; }
        public LackOfFitOutput? LackOfFit { get; set; }
        public Dictionary<string, CodingInfoExport> CodingInfo { get; set; } = new();
        public double[] XtxInvFlat { get; set; } = Array.Empty<double>();
        public double ParetoTCritical { get; set; }
        public double ParetoBonferroniTCritical { get; set; }
        public string NextStepSuggestion { get; set; } = "";
    }

    // ── 通用输出结构（所有 Analyzer 共享） ──

    public class CoefficientOutput
    {
        public string Term { get; set; } = "";
        public double Coefficient { get; set; }
        public double StdError { get; set; }
        public double TValue { get; set; }
        public double PValue { get; set; }
        public double Effect { get; set; }
        public double VIF { get; set; }
    }

    public class ModelSummaryOutput
    {
        public double RSquared { get; set; }
        public double RSquaredAdj { get; set; }
        public double RSquaredPred { get; set; }
        public double RMSE { get; set; }
        public double PRESS { get; set; }
        public double AdequatePrecision { get; set; }
        public double? LackOfFitP { get; set; }
        public double ModelP { get; set; }
        public double Sigma { get; set; }
        public double TCrit { get; set; }
        public int ParamCount { get; set; }
    }

    public class AnovaRowOutput
    {
        public string Source { get; set; } = "";
        public int DF { get; set; }
        public double SS { get; set; }
        public double MS { get; set; }
        public double? FValue { get; set; }
        public double? PValue { get; set; }
    }

    public class ResidualDiagOutput
    {
        public double[] Fitted { get; set; } = Array.Empty<double>();
        public double[] Residuals { get; set; } = Array.Empty<double>();
        public double[] StandardizedResiduals { get; set; } = Array.Empty<double>();
        public double[] StudentizedResiduals { get; set; } = Array.Empty<double>();
        public double[] CooksDistance { get; set; } = Array.Empty<double>();
        public double[] Leverage { get; set; } = Array.Empty<double>();
        public double[] RunOrder { get; set; } = Array.Empty<double>();
    }

    public class ParetoEffectOutput
    {
        public string Term { get; set; } = "";
        public double AbsT { get; set; }
        public double PValue { get; set; }
        public bool IsSignificant { get; set; }
        public double Effect { get; set; }
    }

    public class CurvatureTestOutput
    {
        public bool HasCurvatureTest { get; set; }
        public double CenterMean { get; set; }
        public double CornerMean { get; set; }
        public double Difference { get; set; }
        public double SE { get; set; }
        public double TStatistic { get; set; }
        public double PValue { get; set; }
        public bool IsSignificant { get; set; }
        public double CtPtCoefficient { get; set; }
    }

    public class LackOfFitOutput
    {
        public double SS_LOF { get; set; }
        public int DF_LOF { get; set; }
        public double MS_LOF { get; set; }
        public double SS_PureError { get; set; }
        public int DF_PureError { get; set; }
        public double MS_PureError { get; set; }
        public double FStatistic { get; set; }
        public double PValue { get; set; }
        public bool IsSignificant { get; set; }
    }
}
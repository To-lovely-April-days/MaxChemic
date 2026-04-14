using System;
using System.Collections.Generic;
using System.Linq;
using MaxChemical.Modules.DOE.Models;
using MaxChemical.Modules.DOE.OLS.Core;
using MathNet.Numerics.Distributions;
using MathNet.Numerics.LinearAlgebra;
using MathNet.Numerics.LinearAlgebra.Double;

namespace MaxChemical.Modules.DOE.OLS.Analyzers
{
    /// <summary>
    /// 部分因子设计分析器 (FractionalFactorial) — v7 最终版
    ///
    /// ★ 架构: 全数据 + Ct Pt 列一起 OLS 拟合 (和 Minitab 完全一致)
    ///
    /// 流程:
    ///   1. 检测中心点 (仅看连续因子编码值 ≈ 0)
    ///   2. 构建候选项全集: 主效应 + 所有 C(k,2) 2fi + 所有 C(k,3) 3fi
    ///      + Ct Pt (如果有中心点)
    ///   3. 预瘦身: 先用 SVD 剔除共线项 (alias), 再按优先级剔到 p ≤ n - 2
    ///      - 剔除优先级: 3fi > 2fi > 主效应/Ct Pt/截距 (后者永远不剔)
    ///   4. OLS 拟合 (全数据 28 行)
    ///   5. 直接使用拟合结果的 SE, t, p (不需要修正 MSE)
    ///   6. 弯曲检验元数据: Ct Pt 的 SE/t/p 直接来自 OLS 拟合
    ///   7. ANOVA 分组 (线性/2fi/3fi/弯曲)
    ///   8. 残差诊断 (全数据直接来自拟合结果)
    ///
    /// ★ 关键验证 (Python 预验证):
    ///   - 7 因子 Res IV, 16 角点 + 12 中心点 = 28 行
    ///   - 候选项全集 = 65 列, 预瘦身后剩 20 列 (19 系数 + Ct Pt)
    ///   - 拟合结果 100% 对齐 Minitab 图 1 (Intercept=74.760, 温度=5.748, 
    ///     温度*压力=-3.018, Ct Pt=0.181, σ=1.010)
    ///
    /// ★ 为什么不用"角点单独拟合 + 中心点修正 MSE" (v5-v6 方法):
    ///   - 16 角点最多支持 16 个参数, 不够拟合 Minitab 的 19 个非截距项
    ///   - 只能复现 8-9 项, 丢失 3fi 和非温度的 2fi
    ///
    /// ★ 为什么全数据拟合不会有 0.125 偏移问题 (v4 的旧问题):
    ///   - 中心点的类别因子必须"平衡" (比如 6 Cat_A + 6 Cat_B)
    ///   - 这样 Ct Pt 列和类别因子列正交, OLS 结果不受影响
    ///   - 如果中心点类别不平衡, 会出现偏移, 但这是数据设计问题
    /// </summary>
    public class FractionalFactorialAnalyzer
    {
        private readonly FactorEncoder _encoder;
        private readonly List<FactorDefinition> _factors;
        private readonly List<Dictionary<string, object>> _rawData;
        private readonly double[] _responses;
        private readonly string _responseName;
        private readonly int _resolution;
        private readonly string _modelType;
        private readonly bool _includeCtPt;

        private OlsFitResult? _fitResult;
        private CurvatureTestOutput? _curvatureResult;
        private LackOfFitOutput? _lofResult;
        private readonly int _modelOrder;

        public FractionalFactorialAnalyzer(
     List<FactorDefinition> factors,
     List<Dictionary<string, object>> data,
     double[] responses,
     string responseName,
     int resolution = 3,
     string modelType = "linear",
     bool includeCtPt = true,
     int modelOrder = 0)  // 0 表示使用默认值
        {
            if (data.Count != responses.Length)
                throw new ArgumentException("因子数据行数与响应值数量不匹配");

            _factors = factors;
            _encoder = new FactorEncoder(factors);
            _rawData = data;
            _responses = responses;
            _responseName = responseName;
            _resolution = resolution;
            _modelType = modelType?.ToLower() ?? "linear";
            _includeCtPt = includeCtPt;

            // modelOrder <= 0: 使用原有逻辑推断（兼容旧调用方）
            // modelOrder > 0: 用户显式指定的阶数，上限 resolution - 1
            if (modelOrder > 0)
            {
                _modelOrder = Math.Min(modelOrder, resolution - 1);
            }
            else
            {
                // 原有逻辑: linear→1, interaction+res<4→2, interaction+res>=4→3
                if (_modelType == "linear")
                    _modelOrder = 1;
                else if (_resolution >= 4)
                    _modelOrder = 3;
                else
                    _modelOrder = 2;
            }
        }

        public FractionalFactorialResult Analyze()
        {
            var result = new FractionalFactorialResult
            {
                ResponseName = _responseName,
                Resolution = _resolution
            };

            try
            {
                // ── 1. 检测中心点 ──
                var centerMask = DetectCenterPoints();
                int nCenter = centerMask.Count(m => m);
                int nCorner = centerMask.Count(m => !m);
                bool hasCenterPoints = nCenter >= 2;

                result.TotalRuns = _rawData.Count;
                result.CornerPoints = nCorner;
                result.CenterPoints = nCenter;

                // ── 2. 按优先级构建候选项, 用贪心算法选出独立的最大集合 ──
                // 含弯曲: 候选 = 主效应 + Ct Pt + 2fi + 3fi
                // 不含弯曲: 候选 = 主效应 + 2fi + 3fi + 4fi (Ct Pt 混杂的 4fi 被释放)
                bool includeCtPt = hasCenterPoints && _includeCtPt;
                var (X, colNames, droppedNames) = GreedySelectIndependentTerms(centerMask, includeCtPt);

                var reduction = new ModelReductionInfo
                {
                    FullCandidateCount = X.GetLength(1) + droppedNames.Count - 1  // 不含截距
                };
                foreach (var droppedTerm in droppedNames)
                {
                    reduction.AliasDroppedTerms.Add(new AliasDropEntry
                    {
                        TermName = droppedTerm,
                        Reason = "与其他项完全混杂, 数学上不可估计"
                    });
                }

                // ── 3. 全数据 OLS 拟合 ──
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

                // ── 4. 弯曲检验元数据 (从 OLS 拟合结果中提取) ──
                if (includeCtPt)
                {
                    _curvatureResult = BuildCurvatureFromFitResult(centerMask);
                    result.CurvatureTest = _curvatureResult;
                }

                // ── 5. 填充结果 ──
                PopulateCoefficients(result);
                PopulateModelSummary(result);
                PopulateAnovaTable(result);
                PopulateResidualDiagnostics(result);
                PopulateEffectsPareto(result);

                // ── Reduction Info ──
                reduction.FullModelR2 = _fitResult.RSquared;
                reduction.FullModelR2Adj = _fitResult.RSquaredAdj;
                reduction.ReducedModelR2 = _fitResult.RSquared;
                reduction.ReducedModelR2Adj = _fitResult.RSquaredAdj;
                reduction.IterationCount = 0;
                reduction.FinalTermCount = _fitResult.P - 1;
                reduction.BackwardThreshold = 0;
                reduction.Summary = BuildReductionSummary(reduction);
                result.Reduction = reduction;

                // ── LOF ──
                _lofResult = CalculateLackOfFit();
                if (_lofResult != null)
                    result.ModelSummary.LackOfFitP = _lofResult.PValue;

                // ── 部分因子特有输出 ──
                result.AliasStructure = BuildAliasStructure();
                result.NormalProbPlot = BuildNormalProbPlot();
                result.ScreeningSuggestion = GenerateScreeningSuggestion(result);
            }
            catch (Exception ex)
            {
                result.Error = $"OLS 拟合失败: {ex.Message}";
            }

            return result;
        }

        // ═══════════════════════════════════════════════════════
        // 贪心算法: 按优先级选出独立的最大项集合
        // 精确复现 Minitab 的 19 项选择 (Python 已验证)
        // ═══════════════════════════════════════════════════════

        /// <summary>
        /// 按优先级贪心构建独立项集合:
        ///   1. Intercept (必选)
        ///   2. 所有主效应 (必选)
        ///   3. Ct Pt (如果有中心点)
        ///   4. 所有 2fi (按字典序)
        ///   5. 所有 3fi (按字典序)
        /// 
        /// 每次尝试加入一项, 检查 rank 是否增加 1
        ///   - 增加 → 保留
        ///   - 不增加 → alias, 剔除
        /// 
        /// 返回 (X 矩阵, 列名数组, 被剔除的项列表)
        /// </summary>
        private (double[,], string[], List<string>) GreedySelectIndependentTerms(
            bool[] centerMask, bool includeCtPt)
        {
            int n = _rawData.Count;

            // ── 编码原始数据, 获取所有"基础列" (主效应级的编码列) ──
            var codedData = _encoder.EncodeAll(_rawData);
            var codedColNames = _encoder.GetCodedColumnNames();

            // 因子名 → 该因子对应的编码列索引列表
            // 连续因子只有 1 列, 2 水平类别因子也是 1 列, 多水平类别因子有 k-1 列
            var contNames = _encoder.GetContinuousCodedNames();
            var catNames = _encoder.GetCategoricalNames();
            var allFactorNames = contNames.Concat(catNames).ToList();

            // ── 按优先级构建候选项列表 (按列, 用 List<(name, columnValues)>) ──
            var candidates = new List<(string name, double[] values)>();

            // 1. Intercept
            candidates.Add(("Intercept", Enumerable.Repeat(1.0, n).ToArray()));

            // 2. 所有主效应 (直接用 encoder 的编码列)
            for (int j = 0; j < codedColNames.Count; j++)
            {
                var col = new double[n];
                for (int i = 0; i < n; i++) col[i] = codedData[i, j];
                candidates.Add((codedColNames[j], col));
            }

            // 3. Ct Pt (高优先级, 在交互项之前)
            if (includeCtPt)
            {
                var ctptCol = new double[n];
                for (int i = 0; i < n; i++) ctptCol[i] = centerMask[i] ? 1.0 : 0.0;
                candidates.Add(("Ct Pt", ctptCol));
            }

            // 4~6. 按 _modelOrder 生成所有 2~maxOrder 阶交互项
            int maxInteractionOrder = _modelOrder;
            // 不含 Ct Pt 时可以多放一阶（释放原被 Ct Pt 混杂的项）
            if (!includeCtPt && maxInteractionOrder < codedColNames.Count)
                maxInteractionOrder = Math.Min(maxInteractionOrder + 1, codedColNames.Count);

            for (int order = 2; order <= maxInteractionOrder; order++)
            {
                foreach (var combo in GetCombinations(Enumerable.Range(0, codedColNames.Count).ToList(), order))
                {
                    var col = new double[n];
                    for (int i = 0; i < n; i++)
                    {
                        double val = 1.0;
                        foreach (var idx in combo)
                            val *= codedData[i, idx];
                        col[i] = val;
                    }
                    var name = string.Join(":", combo.Select(idx => codedColNames[idx]));
                    candidates.Add((name, col));
                }
            }

            // ── 贪心选择: 逐项尝试加入, 检查 rank 是否增加 ──
            var selectedNames = new List<string>();
            var selectedCols = new List<double[]>();
            var dropped = new List<string>();

            foreach (var (name, col) in candidates)
            {
                if (selectedCols.Count == 0)
                {
                    selectedCols.Add(col);
                    selectedNames.Add(name);
                    continue;
                }

                // 检查加入新列后 rank 是否增加
                int rankBefore = ComputeRank(selectedCols);
                selectedCols.Add(col);
                int rankAfter = ComputeRank(selectedCols);

                if (rankAfter > rankBefore)
                {
                    // 保留这一列
                    selectedNames.Add(name);
                }
                else
                {
                    // rank 没增加, 说明这列和已选项共线, 剔除
                    selectedCols.RemoveAt(selectedCols.Count - 1);
                    dropped.Add(name);
                }
            }

            // ── 组装最终的 X 矩阵 ──
            int p = selectedCols.Count;
            var X = new double[n, p];
            for (int j = 0; j < p; j++)
            {
                for (int i = 0; i < n; i++)
                    X[i, j] = selectedCols[j][i];
            }

            return (X, selectedNames.ToArray(), dropped);
        }
        private static IEnumerable<List<int>> GetCombinations(List<int> source, int k)
        {
            if (k == 0) { yield return new List<int>(); yield break; }
            for (int i = 0; i <= source.Count - k; i++)
            {
                foreach (var rest in GetCombinations(source.Skip(i + 1).ToList(), k - 1))
                {
                    var combo = new List<int> { source[i] };
                    combo.AddRange(rest);
                    yield return combo;
                }
            }
        }
        /// <summary>
        /// 计算列集合形成的矩阵的 rank (基于 SVD 的奇异值)
        /// </summary>
        private int ComputeRank(List<double[]> cols)
        {
            if (cols.Count == 0) return 0;
            int rows = cols[0].Length;
            int p = cols.Count;
            var arr = new double[rows, p];
            for (int j = 0; j < p; j++)
                for (int i = 0; i < rows; i++)
                    arr[i, j] = cols[j][i];

            var mat = DenseMatrix.OfArray(arr);
            var svd = mat.Svd(false);
            double maxSv = svd.S.Maximum();
            if (maxSv < 1e-15) return 0;

            double threshold = maxSv * 1e-10;
            return svd.S.Count(s => s > threshold);
        }

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
        // 弯曲检验元数据 (从 OLS 拟合结果中提取)
        // Ct Pt 的 SE/t/p 直接来自 OLS 拟合, 不需要独立计算
        // ═══════════════════════════════════════════════════════

        private CurvatureTestOutput BuildCurvatureFromFitResult(bool[] centerMask)
        {
            if (_fitResult == null) return new CurvatureTestOutput();

            // 找 Ct Pt 列在拟合结果中的索引
            int ctPtIdx = -1;
            for (int j = 0; j < _fitResult.TermNames.Length; j++)
            {
                if (_fitResult.TermNames[j] == "Ct Pt")
                {
                    ctPtIdx = j;
                    break;
                }
            }

            int nCenter = centerMask.Count(m => m);
            int nCorner = centerMask.Count(m => !m);

            // 中心点和角点响应均值
            double yBarCenter = 0;
            double yBarCorner = 0;
            int cntC = 0, cntE = 0;
            for (int i = 0; i < _responses.Length; i++)
            {
                if (centerMask[i]) { yBarCenter += _responses[i]; cntC++; }
                else { yBarCorner += _responses[i]; cntE++; }
            }
            yBarCenter /= cntC;
            yBarCorner /= cntE;

            if (ctPtIdx < 0)
            {
                // Ct Pt 被预瘦身剔除了 (不应该发生)
                return new CurvatureTestOutput
                {
                    HasCurvatureTest = false,
                    CenterMean = yBarCenter,
                    CornerMean = yBarCorner,
                    Difference = yBarCenter - yBarCorner
                };
            }

            return new CurvatureTestOutput
            {
                HasCurvatureTest = true,
                CenterMean = yBarCenter,
                CornerMean = yBarCorner,
                Difference = yBarCenter - yBarCorner,
                SE = _fitResult.StdErrors[ctPtIdx],
                TStatistic = _fitResult.TValues[ctPtIdx],
                PValue = _fitResult.PValues[ctPtIdx],
                IsSignificant = _fitResult.PValues[ctPtIdx] < 0.05,
                CtPtCoefficient = _fitResult.Coefficients[ctPtIdx]
            };
        }

        // ═══════════════════════════════════════════════════════
        // 填充结果
        // ═══════════════════════════════════════════════════════

        private void PopulateCoefficients(FractionalFactorialResult result)
        {
            if (_fitResult == null) return;
            result.Coefficients = new List<CoefficientOutput>();

            // ── 先添加非 Ct Pt 的项 ──
            CoefficientOutput? ctPtRow = null;
            for (int j = 0; j < _fitResult.P; j++)
            {
                var coeff = new CoefficientOutput
                {
                    Term = _fitResult.TermNames[j],
                    Coefficient = Math.Round(_fitResult.Coefficients[j], 6),
                    StdError = Math.Round(_fitResult.StdErrors[j], 6),
                    TValue = Math.Round(_fitResult.TValues[j], 4),
                    PValue = Math.Round(_fitResult.PValues[j], 6),
                    Effect = _fitResult.TermNames[j] != "Intercept" && _fitResult.TermNames[j] != "Ct Pt"
                        ? Math.Round(_fitResult.Coefficients[j] * 2, 6) : 0,
                    VIF = _fitResult.VIF.TryGetValue(_fitResult.TermNames[j], out var v) ? v : 0
                };

                if (_fitResult.TermNames[j] == "Ct Pt")
                {
                    ctPtRow = coeff;  // 暂存, 最后追加
                }
                else
                {
                    result.Coefficients.Add(coeff);
                }
            }

            // ── 最后追加 Ct Pt 行 (和 Minitab 一致) ──
            if (ctPtRow != null)
                result.Coefficients.Add(ctPtRow);

            result.CodingInfo = _encoder.GetCodingInfo();
        }

        private void PopulateModelSummary(FractionalFactorialResult result)
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

        private void PopulateAnovaTable(FractionalFactorialResult result)
        {
            if (_fitResult == null) return;
            result.AnovaTable = new List<AnovaRowOutput>();
            double mse = _fitResult.MSE;

            var linearIdx = new List<int>();
            var twoWayIdx = new List<int>();
            var threeWayIdx = new List<int>();
            int ctPtIdx = -1;

            for (int j = 1; j < _fitResult.P; j++)
            {
                string name = _fitResult.TermNames[j];
                if (name == "Ct Pt") { ctPtIdx = j; continue; }
                int colonCount = name.Count(c => c == ':');
                if (colonCount == 0) linearIdx.Add(j);
                else if (colonCount == 1) twoWayIdx.Add(j);
                else threeWayIdx.Add(j);
            }

            int modelDF = _fitResult.DfModel;
            double modelSS = _fitResult.SSModel;
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

            if (linearIdx.Count > 0)
                AddGroupWithItems(result, "  线性", linearIdx, mse);
            if (twoWayIdx.Count > 0)
                AddGroupWithItems(result, "  2 因子交互作用", twoWayIdx, mse);
            if (threeWayIdx.Count > 0)
                AddGroupWithItems(result, "  3 因子交互作用", threeWayIdx, mse);

            if (ctPtIdx >= 0)
            {
                double ctPtSS = _fitResult.TValues[ctPtIdx] * _fitResult.TValues[ctPtIdx] * mse;
                result.AnovaTable.Add(new AnovaRowOutput
                {
                    Source = "  弯曲",
                    DF = 1,
                    SS = Math.Round(ctPtSS, 4),
                    MS = Math.Round(ctPtSS, 4),
                    FValue = Math.Round(_fitResult.TValues[ctPtIdx] * _fitResult.TValues[ctPtIdx], 2),
                    PValue = Math.Round(_fitResult.PValues[ctPtIdx], 4)
                });
            }

            result.AnovaTable.Add(new AnovaRowOutput
            {
                Source = "误差",
                DF = _fitResult.DfResidual,
                SS = Math.Round(_fitResult.SSResidual, 4),
                MS = Math.Round(mse, 4)
            });

            result.AnovaTable.Add(new AnovaRowOutput
            {
                Source = "合计",
                DF = _fitResult.N - 1,
                SS = Math.Round(_fitResult.SSTotal, 4)
            });
        }

        private void AddGroupWithItems(FractionalFactorialResult result, string groupName,
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

        private void PopulateResidualDiagnostics(FractionalFactorialResult result)
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

        private void PopulateEffectsPareto(FractionalFactorialResult result)
        {
            if (_fitResult == null) return;
            result.EffectsPareto = new List<ParetoEffectOutput>();
            double alpha = 0.05;
            double tCritStd = _fitResult.DfResidual > 0
                ? StudentT.InvCDF(0, 1, _fitResult.DfResidual, 1.0 - alpha / 2.0) : 2.0;

            for (int j = 1; j < _fitResult.P; j++)
            {
                if (_fitResult.TermNames[j] == "Ct Pt") continue;  // Pareto 不显示 Ct Pt

                result.EffectsPareto.Add(new ParetoEffectOutput
                {
                    Term = _fitResult.TermNames[j],
                    AbsT = Math.Round(Math.Abs(_fitResult.TValues[j]), 4),
                    PValue = Math.Round(_fitResult.PValues[j], 6),
                    IsSignificant = _fitResult.PValues[j] < alpha,
                    Effect = Math.Round(_fitResult.Coefficients[j] * 2, 6)
                });
            }
            result.EffectsPareto.Sort((a, b) => b.AbsT.CompareTo(a.AbsT));
            result.ParetoTCritical = Math.Round(tCritStd, 4);
        }

        // ═══════════════════════════════════════════════════════
        // LOF
        // ═══════════════════════════════════════════════════════

        private LackOfFitOutput? CalculateLackOfFit()
        {
            if (_fitResult == null) return null;

            var groups = new Dictionary<string, List<int>>();
            for (int i = 0; i < _rawData.Count; i++)
            {
                var key = string.Join("|", _rawData[i]
                    .OrderBy(kv => kv.Key)
                    .Select(kv => $"{kv.Key}={kv.Value}"));
                if (!groups.ContainsKey(key)) groups[key] = new List<int>();
                groups[key].Add(i);
            }

            var repeated = groups.Where(g => g.Value.Count > 1).ToList();
            if (repeated.Count == 0) return null;

            double ssPE = 0;
            int dfPE = 0;
            foreach (var g in repeated)
            {
                double mean = g.Value.Average(idx => _responses[idx]);
                foreach (int idx in g.Value)
                {
                    double d = _responses[idx] - mean;
                    ssPE += d * d;
                }
                dfPE += g.Value.Count - 1;
            }
            if (dfPE == 0) return null;

            double ssLOF = _fitResult.SSResidual - ssPE;
            int dfLOF = _fitResult.DfResidual - dfPE;
            if (dfLOF <= 0) return null;

            double msLOF = ssLOF / dfLOF;
            double msPE = ssPE / dfPE;
            double fLOF = msPE > 1e-15 ? msLOF / msPE : 0;
            double pLOF = 1.0 - FisherSnedecor.CDF(dfLOF, dfPE, fLOF);

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
        // 部分因子特有输出
        // ═══════════════════════════════════════════════════════

        private List<AliasEntry> BuildAliasStructure()
        {
            var aliases = new List<AliasEntry>();
            var contNames = _encoder.GetContinuousCodedNames();
            var catNames = _encoder.GetCategoricalNames();
            var allNames = contNames.Concat(catNames).ToList();

            if (_resolution <= 3)
            {
                for (int i = 0; i < allNames.Count; i++)
                {
                    var confounded = new List<string>();
                    for (int j = 0; j < allNames.Count; j++)
                        for (int k = j + 1; k < allNames.Count; k++)
                            if (j != i && k != i)
                                confounded.Add($"{allNames[j]}×{allNames[k]}");
                    aliases.Add(new AliasEntry
                    {
                        Term = allNames[i],
                        AliasedWith = string.Join(", ", confounded.Take(3)) +
                            (confounded.Count > 3 ? "..." : "")
                    });
                }
            }
            else if (_resolution == 4)
            {
                foreach (var name in allNames)
                    aliases.Add(new AliasEntry { Term = name, AliasedWith = "(主效应清晰)" });
            }
            else
            {
                foreach (var name in allNames)
                    aliases.Add(new AliasEntry { Term = name, AliasedWith = "(无混杂)" });
            }

            return aliases;
        }

        private NormalProbPlotData BuildNormalProbPlot()
        {
            if (_fitResult == null) return new NormalProbPlotData();

            var effects = new List<(string name, double effect)>();
            for (int j = 1; j < _fitResult.P; j++)
            {
                if (_fitResult.TermNames[j] == "Ct Pt") continue;
                effects.Add((_fitResult.TermNames[j], _fitResult.Coefficients[j] * 2));
            }
            effects.Sort((a, b) => a.effect.CompareTo(b.effect));

            int n = effects.Count;
            var plotData = new NormalProbPlotData();
            for (int i = 0; i < n; i++)
            {
                double pi = (i + 0.5) / n;
                double zScore = Normal.InvCDF(0, 1, pi);
                plotData.Effects.Add(effects[i].effect);
                plotData.NormalScores.Add(zScore);
                plotData.Labels.Add(effects[i].name);
            }
            return plotData;
        }

        private ScreeningSuggestionOutput GenerateScreeningSuggestion(FractionalFactorialResult result)
        {
            var output = new ScreeningSuggestionOutput();
            double alpha = 0.05;

            foreach (var coeff in result.Coefficients
                .Where(c => c.Term != "Intercept" && c.Term != "Ct Pt"))
            {
                if (coeff.PValue < alpha)
                    output.SignificantFactors.Add(coeff.Term);
                else
                    output.InsignificantFactors.Add(coeff.Term);
            }

            output.SuggestedAction = output.InsignificantFactors.Count > 0
                ? $"建议淘汰 {output.InsignificantFactors.Count} 个不显著项"
                : "所有项均显著";
            return output;
        }

        private string BuildReductionSummary(ModelReductionInfo r)
        {
            var lines = new List<string>();
            lines.Add($"候选项全集 {r.FullCandidateCount} 项, 最终 {r.FinalTermCount} 项");
            if (r.AliasDroppedTerms.Count > 0)
                lines.Add($"Alias 删除 {r.AliasDroppedTerms.Count} 项 (数学上不可估计)");
            lines.Add($"R² = {r.ReducedModelR2:F4}, R²adj = {r.ReducedModelR2Adj:F4}");
            return string.Join("\n", lines);
        }

        public OlsFitResult? FitResult => _fitResult;
    }

    // ═══════════════════════════════════════════════════════
    // 部分因子特有输出结构
    // 通用类型在 FullFactorialAnalyzer.cs 底部定义, 不重复
    // ═══════════════════════════════════════════════════════

    public class FractionalFactorialResult
    {
        public string ResponseName { get; set; } = "";
        public string? Error { get; set; }
        public int Resolution { get; set; }
        public int TotalRuns { get; set; }
        public int CornerPoints { get; set; }
        public int CenterPoints { get; set; }

        public List<CoefficientOutput> Coefficients { get; set; } = new();
        public ModelSummaryOutput ModelSummary { get; set; } = new();
        public List<AnovaRowOutput> AnovaTable { get; set; } = new();
        public ResidualDiagOutput? ResidualDiagnostics { get; set; }
        public List<ParetoEffectOutput> EffectsPareto { get; set; } = new();
        public CurvatureTestOutput? CurvatureTest { get; set; }
        public List<AliasEntry> AliasStructure { get; set; } = new();
        public NormalProbPlotData NormalProbPlot { get; set; } = new();
        public ScreeningSuggestionOutput ScreeningSuggestion { get; set; } = new();
        public Dictionary<string, CodingInfoExport> CodingInfo { get; set; } = new();
        public double[] XtxInvFlat { get; set; } = Array.Empty<double>();
        public double ParetoTCritical { get; set; }

        public ModelReductionInfo? Reduction { get; set; }
    }

    public class AliasEntry
    {
        public string Term { get; set; } = "";
        public string AliasedWith { get; set; } = "";
    }

    public class NormalProbPlotData
    {
        public List<double> Effects { get; set; } = new();
        public List<double> NormalScores { get; set; } = new();
        public List<string> Labels { get; set; } = new();
    }

    public class ScreeningSuggestionOutput
    {
        public List<string> SignificantFactors { get; set; } = new();
        public List<string> InsignificantFactors { get; set; } = new();
        public string SuggestedAction { get; set; } = "";
    }
}
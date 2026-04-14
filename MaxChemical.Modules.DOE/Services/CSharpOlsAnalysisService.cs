using MathNet.Numerics.Distributions;
using MaxChemical.Logging;
using MaxChemical.Modules.DOE.Data;
using MaxChemical.Modules.DOE.Models;
using MaxChemical.Modules.DOE.OLS.Analyzers;
using MaxChemical.Modules.DOE.OLS.Core;
using MaxChemical.Modules.DOE.Services;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace MaxChemical.Modules.DOE.OLS.Services
{
    public class CSharpOlsAnalysisService : IDOEAnalysisService
    {
        private readonly IDOERepository _repository;
        private readonly ILogService _logger;
        private string _lastBatchId = "", _lastResponseName = "", _lastModelType = "quadratic";
        private DOEDesignMethod _lastDesignMethod;
        private int _lastResolution = 3;  // ★ 新增: 部分因子分辨度
        private List<FactorDefinition>? _factors;
        private List<Dictionary<string, object>>? _data;
        private double[]? _responses;
        private OlsFitResult? _fitResult;
        private FactorEncoder? _encoder;

        // 残差诊断缓存（Analyzer 直接计算的，比 RefillDiag 重建更准确）
        private double[]? _cachedFitted;
        private double[]? _cachedResiduals;
        private double[]? _cachedStdResiduals;
        private double[]? _cachedCooksD;
        private double[]? _cachedLeverage;
        private int _cachedN;

        public CSharpOlsAnalysisService(IDOERepository repository, ILogService logger)
        {
            _repository = repository;
            _logger = logger?.ForContext<CSharpOlsAnalysisService>() ?? throw new ArgumentNullException(nameof(logger));
        }

        // ═══ FitOlsAsync ═══
        public async Task<OLSAnalysisResult> FitOlsAsync(string batchId, string responseName, string modelType = "quadratic", bool includeCtPt = true, int modelOrder = 0)
        {
            try
            {
                _logger.LogInformation("★ FitOLS: method={M}, resp={R}, modelType={T}, resolution={Res}, modelOrder={O}, dataCount={N}",
                    _lastDesignMethod, responseName, modelType, _lastResolution, modelOrder, _responses?.Length);
                _logger.LogInformation("C# FitOLS: batch={B}, resp={R}, model={M}, resolution={Res}",
                    batchId, responseName, modelType, _lastResolution);
                await LoadBatchDataAsync(batchId, responseName);
                _lastModelType = modelType;
                var result = AnalyzerFactory.CreateAndAnalyze(
                    _lastDesignMethod, _factors!, _data!, _responses!,
                    responseName, modelType, _lastResolution, includeCtPt, modelOrder);
                CacheFitState(result);
                return result;
            }
            catch (Exception ex) { _logger.LogError(ex, "FitOLS fail"); return new OLSAnalysisResult { InestimableWarning = ex.Message }; }
        }

        public async Task<OLSAnalysisResult> FitOlsCustomAsync(string batchId, string responseName, List<string> terms, bool keepCtPt = false)
        {
            await EnsureLoaded(batchId, responseName);
            if (_factors == null || _data == null || _responses == null || _encoder == null)
                return new OLSAnalysisResult { InestimableWarning = "数据未加载" };

            try
            {
                // 构建只包含指定项的 X 矩阵
                var codedNames = _encoder.GetCodedColumnNames();
                int n = _data.Count;

                // 项列表: 截距 + 用户选择的项
                var allTerms = new List<string> { "Intercept" };
                allTerms.AddRange(terms);


                // ★ keepCtPt=true 时自动保留 Ct Pt 列（弯曲检验）
                // keepCtPt=false：精简模型不含 Ct Pt
                if (keepCtPt && !allTerms.Contains("Ct Pt"))
                {
                    bool hasCtPt = _fitResult?.TermNames?.Contains("Ct Pt") == true;

                    // 检测数据中是否存在中心点 (仅看连续因子, 忽略类别因子)
                    bool dataHasCenterPoints = false;
                    var continuousFactors = _factors.Where(f => !f.IsCategorical).ToList();
                    if (hasCtPt && continuousFactors.Count > 0)
                    {
                        foreach (var row in _data)
                        {
                            bool isCenter = true;
                            foreach (var f in continuousFactors)
                            {
                                double val = Convert.ToDouble(row[f.Name]);
                                double mid = (f.Lower + f.Upper) / 2.0;
                                if (Math.Abs(val - mid) > (f.Upper - f.Lower) * 0.01)
                                { isCenter = false; break; }
                            }
                            if (isCenter) { dataHasCenterPoints = true; break; }
                        }
                    }
                    if (hasCtPt && dataHasCenterPoints)
                        allTerms.Add("Ct Pt");
                }

                int p = allTerms.Count;
                var X = new double[n, p];
                for (int i = 0; i < n; i++)
                {
                    var coded = _encoder.EncodeRow(_data[i]);
                    X[i, 0] = 1.0; // 截距

                    for (int j = 1; j < p; j++)
                    {
                        string term = allTerms[j];
                        if (term == "Ct Pt")
                        {
                            // 中心点=1, 角点=0
                            bool isCenter = true;
                            foreach (var f in _factors)
                            {
                                if (f.IsCategorical) continue;
                                double val = Convert.ToDouble(_data[i][f.Name]);
                                double mid = (f.Lower + f.Upper) / 2.0;
                                if (Math.Abs(val - mid) > (f.Upper - f.Lower) * 0.01)
                                { isCenter = false; break; }
                            }
                            X[i, j] = isCenter ? 1.0 : 0.0;
                        }
                        else if (term.Contains("²"))
                        {
                            string baseName = term.Replace("²", "");
                            int idx = codedNames.IndexOf(baseName);
                            X[i, j] = idx >= 0 && idx < coded.Length ? coded[idx] * coded[idx] : 0;
                        }
                        else if (term.Contains(":"))
                        {
                            var parts = term.Split(':');
                            double prod = 1;
                            bool ok = true;
                            foreach (var pt in parts)
                            {
                                int idx = codedNames.IndexOf(pt);
                                if (idx >= 0 && idx < coded.Length) prod *= coded[idx];
                                else { ok = false; break; }
                            }
                            X[i, j] = ok ? prod : 0;
                        }
                        else
                        {
                            int idx = codedNames.IndexOf(term);
                            X[i, j] = idx >= 0 && idx < coded.Length ? coded[idx] : 0;
                        }
                    }
                }

                // OLS 拟合
                var solver = new OlsSolver();
                var fitResult = solver.Fit(X, _responses, allTerms.ToArray());

                // 构建 OLSAnalysisResult
                var codingInfo = _encoder.GetCodingInfo();
                var result = new OLSAnalysisResult();
                result.Coefficients = new List<CoefficientRow>();
                for (int j = 0; j < fitResult.TermNames.Length; j++)
                {
                    result.Coefficients.Add(new CoefficientRow
                    {
                        Term = fitResult.TermNames[j],
                        Coefficient = fitResult.Coefficients[j],
                        StdError = fitResult.StdErrors != null && j < fitResult.StdErrors.Length ? fitResult.StdErrors[j] : 0,
                        TValue = fitResult.TValues != null && j < fitResult.TValues.Length ? fitResult.TValues[j] : 0,
                        PValue = fitResult.PValues != null && j < fitResult.PValues.Length ? fitResult.PValues[j] : 1,
                        VIF = j == 0 ? 0 : 1 // 精简模型 VIF 简化处理
                    });
                }

                // ModelSummary
                double ssRes = 0, ssTot = 0;
                var yHat = new double[n];
                double yMean = _responses.Average();
                for (int i = 0; i < n; i++)
                {
                    double pred = 0;
                    for (int j = 0; j < p; j++) pred += fitResult.Coefficients[j] * X[i, j];
                    yHat[i] = pred;
                    ssRes += ((_responses[i] - pred) * (_responses[i] - pred));
                    ssTot += ((_responses[i] - yMean) * (_responses[i] - yMean));
                }
                double r2 = ssTot > 1e-12 ? 1 - ssRes / ssTot : 0;
                int dfRes = n - p;
                double r2adj = dfRes > 0 ? 1 - (1 - r2) * (n - 1.0) / dfRes : 0;
                double mse = dfRes > 0 ? ssRes / dfRes : 0;
                double sigma = Math.Sqrt(mse);

                // 计算 PRESS 和 R²pred（留一交叉验证）
                double press = 0;
                if (fitResult.XtxInvFlat != null && fitResult.XtxInvFlat.Length == p * p)
                {
                    var XtXinv = new double[p, p];
                    for (int ii = 0; ii < p; ii++)
                        for (int jj = 0; jj < p; jj++)
                            XtXinv[ii, jj] = fitResult.XtxInvFlat[ii * p + jj];

                    for (int i = 0; i < n; i++)
                    {
                        // h_ii = x_i' * (X'X)^-1 * x_i
                        double hii = 0;
                        for (int ii = 0; ii < p; ii++)
                        {
                            double s = 0;
                            for (int jj = 0; jj < p; jj++)
                                s += XtXinv[ii, jj] * X[i, jj];
                            hii += X[i, ii] * s;
                        }
                        double ei = _responses[i] - yHat[i];
                        double denom = 1.0 - hii;
                        if (Math.Abs(denom) > 1e-12)
                            press += (ei / denom) * (ei / denom);
                        else
                            press += ei * ei * 1e6; // 近似
                    }
                }
                double r2pred = ssTot > 1e-12 ? 1.0 - press / ssTot : 0;

                result.ModelSummary = new OLSModelSummary
                {
                    RSquared = Math.Round(r2, 6),
                    RSquaredAdj = Math.Round(r2adj, 6),
                    RSquaredPred = Math.Round(r2pred, 6),
                    RMSE = Math.Round(sigma, 6),
                    PRESS = Math.Round(press, 4)
                };
                result.Sigma = sigma;
                result.TCrit = dfRes > 0 ? StudentT.InvCDF(0, 1, dfRes, 0.975) : 2.0;
                result.ParamCount = p;
                result.XtxInvFlat = fitResult.XtxInvFlat;

                // ── ANOVA 表 ──
                double ssModel = ssTot - ssRes;
                int dfModel = p - 1; // 不含截距
                double msModel = dfModel > 0 ? ssModel / dfModel : 0;
                double msResid = mse;
                double fValue = msResid > 1e-15 ? msModel / msResid : 0;
                double modelP = dfModel > 0 && dfRes > 0
                    ? 1.0 - FisherSnedecor.CDF(dfModel, dfRes, fValue)
                    : 1.0;

                result.AnovaTable = new List<AnovaRow>
                {
                    new AnovaRow { Source = "模型", DF = dfModel, SS = Math.Round(ssModel, 4), MS = Math.Round(msModel, 4), FValue = Math.Round(fValue, 2), PValue = Math.Round(modelP, 4) },
                    new AnovaRow { Source = "残差", DF = dfRes, SS = Math.Round(ssRes, 4), MS = Math.Round(msResid, 4) },
                    new AnovaRow { Source = "总计", DF = n - 1, SS = Math.Round(ssTot, 4) }
                };

                // 各项的 Type III SS（用 t² × MSE 近似）
                for (int j = 1; j < fitResult.TermNames.Length; j++)
                {
                    string tName = fitResult.TermNames[j];
                    if (tName == "Ct Pt") continue;
                    double tVal = fitResult.TValues != null && j < fitResult.TValues.Length ? fitResult.TValues[j] : 0;
                    double pVal = fitResult.PValues != null && j < fitResult.PValues.Length ? fitResult.PValues[j] : 1;
                    double termSS = tVal * tVal * msResid;
                    result.AnovaTable.Insert(result.AnovaTable.Count - 2, new AnovaRow
                    {
                        Source = $"  {tName}",
                        DF = 1,
                        SS = Math.Round(termSS, 4),
                        MS = Math.Round(termSS, 4),
                        FValue = Math.Round(tVal * tVal, 2),
                        PValue = Math.Round(pVal, 4)
                    });
                }

                result.ModelSummary.ModelP = Math.Round(modelP, 6);

                // 编码信息
                result.CodingInfo = new Dictionary<string, CodingInfoItem>();
                foreach (var kv in codingInfo)
                    result.CodingInfo[kv.Key] = new CodingInfoItem { Type = kv.Value.Type, Center = kv.Value.Center, HalfRange = kv.Value.HalfRange };

                // 方程
                result.ModelSummary.Equation = "Intercept";
                var eqParts = new List<string>();
                for (int j = 0; j < fitResult.TermNames.Length; j++)
                {
                    double c = fitResult.Coefficients[j];
                    string t = fitResult.TermNames[j];
                    if (t == "Intercept") eqParts.Add($"{c:F6}");
                    else eqParts.Add($"{(c >= 0 ? "+" : "")}{c:F6} × {t}");
                }
                result.ModelSummary.Equation = string.Join(" ", eqParts);

                // ★ 清空 Analyzer 缓存（精简拟合不走 AnalyzerFactory）
                _cachedFitted = null;
                _cachedResiduals = null;
                _cachedStdResiduals = null;
                _cachedCooksD = null;
                _cachedLeverage = null;
                _cachedN = 0;

                CacheFitState(result);

                // ★ 手动填充残差数据并写入 _cached 缓存（确保 DiagJson 用精简模型数据）
                {
                    var resArr = new double[n];
                    var stdResArr = new double[n];
                    for (int i = 0; i < n; i++)
                    {
                        resArr[i] = _responses[i] - yHat[i];
                        stdResArr[i] = sigma > 1e-15 ? resArr[i] / sigma : 0;
                    }

                    // 直接写 _cached 缓存，DiagJson 优先取 _cached
                    _cachedFitted = yHat;
                    _cachedResiduals = resArr;
                    _cachedStdResiduals = stdResArr;
                    _cachedCooksD = new double[n];
                    _cachedLeverage = new double[n];
                    _cachedN = n;

                    // 同时更新 _fitResult
                    if (_fitResult != null)
                    {
                        _fitResult.N = n;
                        _fitResult.FittedValues = yHat;
                        _fitResult.Residuals = resArr;
                        _fitResult.StandardizedResiduals = stdResArr;
                        _fitResult.CooksDistance = new double[n];
                        _fitResult.Leverage = new double[n];
                    }
                }

                return result;
            }
            catch (Exception ex)
            {
                return new OLSAnalysisResult { InestimableWarning = $"精简模型拟合失败: {ex.Message}" };
            }
        }

        public Task<OLSAnalysisResult> FitOlsDirectAsync(List<Dictionary<string, object>> factorsData, List<double> responsesData, string responseName, Dictionary<string, string>? factorTypes = null, string modelType = "quadratic")
        {
            try
            {
                _factors = InferFactorDefs(factorsData, factorTypes); _data = factorsData; _responses = responsesData.ToArray();
                _encoder = new FactorEncoder(_factors); _lastModelType = modelType;
                var result = AnalyzerFactory.CreateAndAnalyze(DOEDesignMethod.Custom, _factors, factorsData, _responses, responseName, modelType);
                CacheFitState(result); return Task.FromResult(result);
            }
            catch (Exception ex) { return Task.FromResult(new OLSAnalysisResult { InestimableWarning = ex.Message }); }
        }

        // ═══ 残差诊断 ═══
        public async Task<string> GetResidualDiagnosticsAsync(string batchId, string responseName) { await EnsureLoaded(batchId, responseName); return DiagJson(); }
        public string GetResidualDiagnosticsDirectAsync(string responseName) => DiagJson();
        public string GetResidualDiagnosticsFromCurrentModel() => DiagJson();
        private string DiagJson()
        {
            // 优先使用 Analyzer 直接算的缓存数据
            var fitted = _cachedFitted ?? _fitResult?.FittedValues;
            var residuals = _cachedResiduals ?? _fitResult?.Residuals;
            int n = _cachedN > 0 ? _cachedN : (_fitResult?.N ?? 0);

            if (_fitResult == null || fitted == null || residuals == null || n == 0)
                return """{"error":"无残差数据"}""";

            // 确保数组长度一致
            int actualN = Math.Min(n, Math.Min(fitted.Length, residuals.Length));

            // ── 正态概率图：对标 Minitab ──
            // X = 原始残差（排序后），Y = 百分位（Benard 公式，对标 Minitab）
            var sortedResiduals = residuals.Take(actualN).OrderBy(r => r).ToArray();
            var percent = new double[actualN];
            for (int i = 0; i < actualN; i++)
                percent[i] = Math.Round((i + 1 - 0.3) / (actualN + 0.4) * 100.0, 4); // Benard

            // ── 残差直方图：用原始残差，nice 步长对标 Minitab ──
            var residArr = residuals.Take(actualN).ToArray();
            double minR = residArr.Min();
            double maxR = residArr.Max();
            double range = maxR - minR;
            if (range < 1e-15) { range = 1; minR = -0.5; maxR = 0.5; }

            // 计算 "nice" 步长：0.05, 0.1, 0.25, 0.5, 1, 2, 5, 10...
            // Minitab 倾向于 6~8 个 bin，目标除数用 7
            double rawStep = range / 7.0;
            double magnitude = Math.Pow(10, Math.Floor(Math.Log10(rawStep)));
            double normalized = rawStep / magnitude;
            double niceStep;
            if (normalized <= 1.5) niceStep = magnitude;
            else if (normalized <= 3.5) niceStep = 2.5 * magnitude;
            else if (normalized <= 7.5) niceStep = 5 * magnitude;
            else niceStep = 10 * magnitude;

            // bin 中心对齐到 niceStep 的倍数，边界 = 中心 ± step/2（对标 Minitab）
            double firstCenter = Math.Round(minR / niceStep) * niceStep;
            double lastCenter = Math.Round(maxR / niceStep) * niceStep;
            // 确保首尾中心包含数据
            if (firstCenter > minR) firstCenter -= niceStep;
            if (lastCenter < maxR) lastCenter += niceStep;
            int nBins = (int)Math.Round((lastCenter - firstCenter) / niceStep) + 1;
            if (nBins < 3) nBins = 3;
            if (nBins > 20) nBins = 20;
            double niceMin = firstCenter - niceStep / 2.0;

            var binEdges = new double[nBins + 1];
            var frequencies = new int[nBins];
            for (int i = 0; i <= nBins; i++) binEdges[i] = Math.Round(niceMin + i * niceStep, 4);
            for (int i = 0; i < actualN; i++)
            {
                int bin = (int)((residArr[i] - niceMin) / niceStep);
                if (bin >= nBins) bin = nBins - 1;
                if (bin < 0) bin = 0;
                frequencies[bin]++;
            }

            return JsonConvert.SerializeObject(new
            {
                normal_probability = new
                {
                    // Minitab: X=残差, Y=百分位
                    ordered_residuals = sortedResiduals.Select(r => Math.Round(r, 4)).ToArray(),
                    percent = percent
                },
                residuals_vs_fitted = new
                {
                    // Minitab: X=拟合值, Y=残差
                    fitted = fitted.Take(actualN).Select(f => Math.Round(f, 4)).ToArray(),
                    residuals = residuals.Take(actualN).Select(r => Math.Round(r, 4)).ToArray()
                },
                residuals_vs_order = new
                {
                    // Minitab: X=观测顺序, Y=残差
                    observation = Enumerable.Range(1, actualN).ToArray(),
                    residuals = residuals.Take(actualN).Select(r => Math.Round(r, 4)).ToArray()
                },
                residuals_histogram = new
                {
                    bin_edges = binEdges,
                    frequencies
                }
            });
        }

        // ═══ Pareto ═══
        public async Task<string> GetEffectsParetoAsync(string batchId, string responseName, double alpha = 0.05) { await EnsureLoaded(batchId, responseName); return ParetoJson(alpha); }
        public string GetEffectsParetoDirectAsync(string responseName, double alpha = 0.05) => ParetoJson(alpha);
        public string GetEffectsParetoFromCurrentModel(double alpha = 0.05) => ParetoJson(alpha);
        private string ParetoJson(double alpha = 0.05)
        {
            if (_fitResult == null) return """{"error":"模型未拟合"}""";
            double tc = _fitResult.DfResidual > 0 ? StudentT.InvCDF(0, 1, _fitResult.DfResidual, 1 - alpha / 2) : 2;
            var fx = new List<object>();
            for (int j = 1; j < _fitResult.P; j++)
            {
                if (_fitResult.TermNames[j] == "Ct Pt") continue;
                fx.Add(new { term = _fitResult.TermNames[j], abs_t = Math.Round(Math.Abs(_fitResult.TValues[j]), 4), p_value = Math.Round(_fitResult.PValues[j], 6), significant = _fitResult.PValues[j] < alpha, effect = Math.Round(_fitResult.Coefficients[j] * 2, 6) });
            }
            return JsonConvert.SerializeObject(new
            {
                effects = fx.OrderByDescending(e => ((dynamic)e).abs_t).ToList(),
                t_crit = Math.Round(tc, 4),
                log_worth_crit = Math.Round(tc, 4),
                alpha,
                measure = "StandardizedEffect",
                df_error = _fitResult.DfResidual
            });
        }

        // ═══ Profiler ═══
        public async Task<string> GetPredictionProfilerAsync(string batchId, string responseName, int gridSize = 50, Dictionary<string, object>? fixedValues = null)
        { await EnsureLoaded(batchId, responseName); return ProfilerJson(gridSize, fixedValues != null ? JsonConvert.SerializeObject(fixedValues) : ""); }
        public string GetPredictionProfilerDirectAsync(string responseName, int gridSize = 50, string fixedValuesJson = "") => ProfilerJson(gridSize, fixedValuesJson);
        public string GetPredictionProfilerFromCurrentModel(int gridSize = 50, string fixedValuesJson = "") => ProfilerJson(gridSize, fixedValuesJson);
        private string ProfilerJson(int gridSize, string fixedJson)
        {
            if (_fitResult == null || _encoder == null || _factors == null) return """{"error":"模型未拟合"}""";
            var fix = !string.IsNullOrEmpty(fixedJson) ? JsonConvert.DeserializeObject<Dictionary<string, object>>(fixedJson) : null;
            var cur = new Dictionary<string, object>();
            foreach (var f in _factors) cur[f.Name] = f.IsCategorical ? (object)(f.Levels?.FirstOrDefault() ?? "") : (f.Lower + f.Upper) / 2.0;
            if (fix != null) foreach (var kv in fix) cur[kv.Key] = kv.Value;
            var fResult = new Dictionary<string, object>();
            foreach (var f in _factors)
            {
                if (f.IsCategorical)
                {
                    var lvs = f.Levels ?? new(); var yy = new List<double>(); var yl = new List<double>(); var yu = new List<double>();
                    foreach (var lv in lvs) { var p = new Dictionary<string, object>(cur); p[f.Name] = lv; var c = _encoder.EncodeRow(p); var x0 = BuildX0(c); var (pr, se) = PredictSafe(x0); double m = _fitResult.TCrit * se; yy.Add(Math.Round(pr, 6)); yl.Add(Math.Round(pr - m, 6)); yu.Add(Math.Round(pr + m, 6)); }
                    fResult[f.Name] = new { x = Enumerable.Range(0, lvs.Count).Select(i => (double)i).ToList(), y = yy, y_lower = yl, y_upper = yu, current_value = cur[f.Name], range = new List<double>(), levels = lvs, is_categorical = true };
                }
                else
                {
                    var xx = new List<double>(); var yy = new List<double>(); var yl = new List<double>(); var yu = new List<double>();
                    for (int i = 0; i < gridSize; i++) { double xn = f.Lower + (f.Upper - f.Lower) * i / (gridSize - 1); xx.Add(Math.Round(xn, 6)); var p = new Dictionary<string, object>(cur); p[f.Name] = xn; var c = _encoder.EncodeRow(p); var x0 = BuildX0(c); var (pr, se) = PredictSafe(x0); double m = _fitResult.TCrit * se; yy.Add(Math.Round(pr, 6)); yl.Add(Math.Round(pr - m, 6)); yu.Add(Math.Round(pr + m, 6)); }
                    fResult[f.Name] = new { x = xx, y = yy, y_lower = yl, y_upper = yu, current_value = cur[f.Name], range = new List<double> { f.Lower, f.Upper }, is_categorical = false };
                }
            }
            // 计算中心点的预测值和 CI
            var centerCoded = _encoder.EncodeRow(cur);
            var centerX0 = BuildX0(centerCoded);
            var (centerPred, centerSe) = PredictSafe(centerX0);
            double centerM = _fitResult.TCrit * centerSe;

            return JsonConvert.SerializeObject(new
            {
                factors = fResult,
                current_predicted = Math.Round(centerPred, 6),
                response_name = _lastResponseName ?? "",
                current_ci_lower = Math.Round(centerPred - centerM, 4),
                current_ci_upper = Math.Round(centerPred + centerM, 4)
            });
        }

        // ═══ Find Optimal ═══
        public async Task<string> FindOptimalAsync(string batchId, string responseName, bool maximize = true) { await EnsureLoaded(batchId, responseName); return OptJson(maximize); }
        public string FindOptimalDirectAsync(string responseName, bool maximize = true) => OptJson(maximize);
        public string FindOptimalFromCurrentModel(bool maximize = true) => OptJson(maximize);
        private string OptJson(bool maximize)
        {
            if (_fitResult == null || _factors == null || _encoder == null) return """{"error":"模型未拟合"}""";
            double best = maximize ? double.MinValue : double.MaxValue; Dictionary<string, object>? bp = null;
            var cont = _factors.Where(f => !f.IsCategorical).ToList(); var cat = _factors.Where(f => f.IsCategorical).ToList();
            foreach (var cp in GenGrid(cont, 20)) foreach (var cc in GenCatCombos(cat))
                {
                    var fp = new Dictionary<string, object>(cp); foreach (var kv in cc) fp[kv.Key] = kv.Value;
                    try { var cd = _encoder.EncodeRow(fp); var x0 = BuildX0(cd); var (pr, _) = PredictSafe(x0); if ((maximize ? pr > best : pr < best)) { best = pr; bp = fp; } } catch { }
                }
            if (bp == null) return """{"error":"无法找到最优点"}""";
            return JsonConvert.SerializeObject(new { optimal_factors = bp, predicted = Math.Round(best, 6), maximize });
        }

        // ═══ 响应曲面 + 等高线 ═══
        public async Task<string> GetOlsResponseSurfaceDataAsync(string batchId, string responseName, string f1, string f2, int gs = 30) { await EnsureLoaded(batchId, responseName); return SurfJson(f1, f2, gs); }
        public string GetOlsSurfaceDataDirectAsync(string f1, string f2, string boundsJson, int gs = 30) => SurfJson(f1, f2, gs);
        public async Task<string> GetOlsContourDataAsync(string batchId, string responseName, string f1, string f2, int gs = 30) { await EnsureLoaded(batchId, responseName); return SurfJson(f1, f2, gs); }
        public string GetOlsContourDataDirectAsync(string f1, string f2, string boundsJson, int gs = 30) => SurfJson(f1, f2, gs);
        private string SurfJson(string f1, string f2, int gs)
        {
            if (_fitResult == null || _encoder == null || _factors == null) return """{"error":"模型未拟合"}""";
            var fd1 = _factors.FirstOrDefault(f => f.Name == f1); var fd2 = _factors.FirstOrDefault(f => f.Name == f2);
            if (fd1 == null || fd2 == null) return """{"error":"因子不存在"}""";

            var codingInfo = _encoder.GetCodingInfo();
            var ci1 = codingInfo[f1]; var ci2 = codingInfo[f2];

            // 自然值网格
            var x1Nat = new double[gs]; var x2Nat = new double[gs];
            for (int i = 0; i < gs; i++)
            {
                x1Nat[i] = Math.Round(fd1.Lower + (fd1.Upper - fd1.Lower) * i / (gs - 1), 4);
                x2Nat[i] = Math.Round(fd2.Lower + (fd2.Upper - fd2.Lower) * i / (gs - 1), 4);
            }

            // 编码值网格（预计算避免反复除法）
            var x1Cod = new double[gs]; var x2Cod = new double[gs];
            for (int i = 0; i < gs; i++)
            {
                x1Cod[i] = (x1Nat[i] - ci1.Center) / ci1.HalfRange;
                x2Cod[i] = (x2Nat[i] - ci2.Center) / ci2.HalfRange;
            }

            // 预构建固定因子的基准 x0 向量（其他因子取中心值 = 编码 0）
            var codedNames = _encoder.GetCodedColumnNames();
            var baseX0 = new double[_fitResult.P];
            baseX0[0] = 1.0; // 截距
            // 其余默认 0（中心点），不需要设置

            // 预找 f1, f2 在 x0 向量中的位置映射
            int f1BaseIdx = codedNames.IndexOf(f1);
            int f2BaseIdx = codedNames.IndexOf(f2);

            // 预找所有项在 x0 中的依赖关系（哪些项包含 f1 或 f2）
            // 直接用系数做快速预测
            var beta = _fitResult.Coefficients;
            var termNames = _fitResult.TermNames;

            var z = new double[gs][];
            for (int i = 0; i < gs; i++)
            {
                z[i] = new double[gs];
                double v1 = x1Cod[i];

                for (int j = 0; j < gs; j++)
                {
                    double v2 = x2Cod[j];

                    // 快速预测：遍历所有项，只有含 f1/f2 的项值会变，其余项在中心=0
                    double pred = beta[0]; // 截距
                    for (int t = 1; t < _fitResult.P; t++)
                    {
                        string tn = termNames[t];
                        if (tn == "Ct Pt") continue;

                        double termVal = EvalTermFast(tn, f1, f2, v1, v2, codedNames);
                        pred += beta[t] * termVal;
                    }
                    z[i][j] = Math.Round(pred, 4);
                }
            }
            return JsonConvert.SerializeObject(new { x = x1Nat, y = x2Nat, z, x_label = f1, y_label = f2 });
        }

        /// <summary>快速计算单个模型项的值（其他因子 = 0 编码中心）</summary>
        private static double EvalTermFast(string termName, string f1, string f2, double v1, double v2, List<string> codedNames)
        {
            if (termName.Contains("²"))
            {
                string factor = termName.Replace("²", "");
                if (factor == f1) return v1 * v1;
                if (factor == f2) return v2 * v2;
                return 0; // 其他因子在中心 = 0，0² = 0
            }

            if (termName.Contains(":"))
            {
                var parts = termName.Split(':');
                double prod = 1; bool hasNonZero = true;
                foreach (var p in parts)
                {
                    if (p == f1) prod *= v1;
                    else if (p == f2) prod *= v2;
                    else { prod = 0; hasNonZero = false; break; } // 其他因子 = 0，乘积 = 0
                }
                return hasNonZero ? prod : 0;
            }

            // 主效应
            if (termName == f1) return v1;
            if (termName == f2) return v2;
            return 0; // 其他因子在中心 = 0
        }

        // ═══ 异常点 ═══
        public async Task<string> GetOutlierAnalysisAsync(string batchId, string responseName) { await EnsureLoaded(batchId, responseName); return OutlierJson(); }
        public string GetOutlierAnalysisDirectAsync(string responseName) => OutlierJson();
        private string OutlierJson()
        {
            if (_fitResult == null || _responses == null) return """{"error":"模型未拟合"}""";

            int n = _cachedN > 0 ? _cachedN : _fitResult.N;
            int pPred = _fitResult.TermNames?.Count(t => t != "Ct Pt") ?? _fitResult.P;

            var fitted = _cachedFitted ?? _fitResult.FittedValues;
            var residuals = _cachedResiduals ?? _fitResult.Residuals;
            var stdResiduals = _cachedStdResiduals ?? _fitResult.StandardizedResiduals;
            var cooksD = _cachedCooksD ?? _fitResult.CooksDistance;
            var leverage = _cachedLeverage ?? _fitResult.Leverage;

            if (fitted == null || residuals == null)
                return JsonConvert.SerializeObject(new
                {
                    outliers = new List<object>(),
                    outlier_count = 0,
                    total_observations = n,
                    thresholds = new { cooks_d = 0.0, leverage = 0.0, std_residual = 2.0 }
                });

            int actualN = Math.Min(n, Math.Min(fitted.Length, _responses.Length));
            int dfResid = actualN - pPred;

            // 饱和设计检测：剩余自由度 ≤ 0，无法做异常点诊断
            if (dfResid <= 0)
            {
                return JsonConvert.SerializeObject(new
                {
                    outliers = new List<object>(),
                    outlier_count = 0,
                    total_observations = actualN,
                    thresholds = new { cooks_d = 0.0, leverage = 0.0, std_residual = 2.0 },
                    note = $"饱和设计（{actualN} 个观测, {pPred} 个参数），无多余自由度进行异常点诊断"
                });
            }

            // ── 非饱和设计：正常计算杠杆值 + 异常点检测 ──
            var hatValues = new double[actualN];
            bool hasHat = false;

            if (_fitResult.XtXInverse != null && _encoder != null && _data != null)
            {
                int pInv = _fitResult.XtXInverse.GetLength(0);
                try
                {
                    for (int i = 0; i < actualN && i < _data.Count; i++)
                    {
                        var coded = _encoder.EncodeRow(_data[i]);
                        var x0Full = BuildX0(coded);
                        var x0 = new double[pInv];
                        int ki = 0;
                        for (int j = 0; j < _fitResult.P && j < x0Full.Length; j++)
                        {
                            if (_fitResult.TermNames != null && _fitResult.TermNames[j] == "Ct Pt") continue;
                            if (ki < pInv) x0[ki++] = x0Full[j];
                        }
                        double h = 0;
                        for (int ii = 0; ii < pInv; ii++)
                        {
                            double s = 0;
                            for (int jj = 0; jj < pInv; jj++)
                                s += _fitResult.XtXInverse[ii, jj] * x0[jj];
                            h += x0[ii] * s;
                        }
                        hatValues[i] = h;
                    }
                    hasHat = true;
                }
                catch { hasHat = false; }
            }

            if (!hasHat && leverage != null && leverage.Length >= actualN)
            {
                for (int i = 0; i < actualN; i++)
                    hatValues[i] = leverage[i];
                hasHat = true;
            }

            // 重新计算标准化残差和 Cook's D（用正确的杠杆值）
            double sigma = _fitResult.Sigma > 0 ? _fitResult.Sigma : Math.Sqrt(residuals.Sum(r => r * r) / dfResid);
            var computedStdResid = new double[actualN];
            var computedCooksD = new double[actualN];

            for (int i = 0; i < actualN; i++)
            {
                double h = hasHat ? hatValues[i] : 0;
                double denom = sigma * Math.Sqrt(Math.Max(1 - h, 1e-15));
                computedStdResid[i] = denom > 1e-15 ? residuals[i] / denom : 0;
                double ri = computedStdResid[i];
                computedCooksD[i] = (ri * ri / pPred) * (h / Math.Max(1 - h, 1e-15));
            }

            double leverageCrit = 2.0 * pPred / actualN;
            double stdResidCrit = 2.0;
            double cooksCrit = 4.0 / actualN;

            var outliers = new List<object>();
            int outlierCount = 0;

            for (int i = 0; i < actualN; i++)
            {
                double h = hasHat ? hatValues[i] : 0;
                double sr = computedStdResid[i];
                double cd = computedCooksD[i];

                bool isHighLeverage = h > leverageCrit;
                bool isLargeResid = Math.Abs(sr) > stdResidCrit;
                bool isHighCooksD = cd > cooksCrit && cd > 1e-10;
                bool isOutlier = isHighLeverage || isLargeResid || isHighCooksD;

                if (!isOutlier) continue;

                var reasons = new List<string>();
                if (isHighLeverage) reasons.Add($"异常 X (杠杆值={h:F4} > {leverageCrit:F3})");
                if (isLargeResid) reasons.Add($"异常 R (标准残差={sr:F2})");
                if (isHighCooksD) reasons.Add($"Cook's D={cd:F4} > {cooksCrit:F3}");

                outliers.Add(new
                {
                    index = i + 1,
                    actual = Math.Round(_responses[i], 4),
                    predicted = Math.Round(fitted[i], 4),
                    residual = Math.Round(residuals[i], 4),
                    std_residual = Math.Round(sr, 4),
                    cooks_d = Math.Round(cd, 6),
                    leverage = Math.Round(h, 4),
                    reasons = reasons
                });
                outlierCount++;
            }

            return JsonConvert.SerializeObject(new
            {
                outliers = outliers,
                outlier_count = outlierCount,
                total_observations = actualN,
                thresholds = new
                {
                    cooks_d = Math.Round(cooksCrit, 4),
                    leverage = Math.Round(leverageCrit, 4),
                    std_residual = stdResidCrit
                }
            });
        }

        // ═══ 排除重拟合 ═══
        public async Task<OLSAnalysisResult> RefitExcludingAsync(string batchId, string responseName, List<int> excludeIndices, string modelType = "quadratic")
        {
            await EnsureLoaded(batchId, responseName);
            var ex = new HashSet<int>(excludeIndices.Select(i => i - 1));
            var fd = new List<Dictionary<string, object>>(); var fr = new List<double>();
            for (int i = 0; i < _data!.Count; i++) if (!ex.Contains(i)) { fd.Add(_data[i]); fr.Add(_responses![i]); }
            _data = fd; _responses = fr.ToArray();
            var result = AnalyzerFactory.CreateAndAnalyze(_lastDesignMethod, _factors!, fd, _responses, responseName, modelType);
            CacheFitState(result); return result;
        }

        // ═══ Tukey HSD ═══
        public async Task<string> GetTukeyHSDAsync(string batchId, string responseName, string factorName = "") { await EnsureLoaded(batchId, responseName); return TukeyJson(factorName); }
        public string GetTukeyHSDDirectAsync(string responseName, string categoricalFactorName) => TukeyJson(categoricalFactorName);
        private string TukeyJson(string factorName)
        {
            if (_data == null || _responses == null || _factors == null) return """{"error":"数据未加载"}""";
            var cf = string.IsNullOrEmpty(factorName) ? _factors.FirstOrDefault(f => f.IsCategorical) : _factors.FirstOrDefault(f => f.Name == factorName && f.IsCategorical);
            if (cf == null) return """{"error":"未找到类别因子"}""";
            var grp = new Dictionary<string, List<double>>();
            for (int i = 0; i < _data.Count; i++) { string lv = _data[i][cf.Name].ToString()!; if (!grp.ContainsKey(lv)) grp[lv] = new(); grp[lv].Add(_responses[i]); }
            var lvs = grp.Keys.OrderBy(k => k).ToList(); int k = lvs.Count; int dfE = _data.Count - k;
            double mse = _fitResult?.MSE ?? grp.Values.SelectMany(g => { double m = g.Average(); return g.Select(v => (v - m) * (v - m)); }).Sum() / dfE;
            var comps = new List<object>();
            for (int i = 0; i < k; i++) for (int j = i + 1; j < k; j++)
                {
                    var g1 = grp[lvs[i]]; var g2 = grp[lvs[j]]; double d = g1.Average() - g2.Average(); double se = Math.Sqrt(mse * (1.0 / g1.Count + 1.0 / g2.Count));
                    double tv = se > 1e-15 ? Math.Abs(d) / se : 0; double pv = dfE > 0 ? 2 * (1 - StudentT.CDF(0, 1, dfE, tv)) : 1; double pa = Math.Min(pv * k * (k - 1) / 2, 1);
                    comps.Add(new { group1 = lvs[i], group2 = lvs[j], diff = Math.Round(d, 4), p_value = Math.Round(pv, 6), p_adjusted = Math.Round(pa, 6), significant = pa < 0.05 });
                }
            return JsonConvert.SerializeObject(new { factor = cf.Name, comparisons = comps, mse = Math.Round(mse, 6) });
        }

        // ═══ 主效应图 ═══
        public async Task<string> GetMainEffectsAsync(string batchId) { if (_lastBatchId == batchId && _data != null) return MainFxJson(); await LoadBatchDataAsync(batchId, null); return MainFxJson(); }
        public async Task<string> GetMainEffectsForResponseAsync(string batchId, string responseName) { await EnsureLoaded(batchId, responseName); return MainFxJson(); }
        public string GetMainEffectsDirectAsync(string responseName) => MainFxJson();
        private string MainFxJson()
        {
            if (_factors == null || _data == null || _responses == null) return "{}";
            double gm = _responses.Average(); var fx = new Dictionary<string, object>();
            foreach (var f in _factors)
            {
                if (f.IsCategorical)
                {
                    var lvs = f.Levels ?? _data.Select(d => d[f.Name].ToString()!).Distinct().OrderBy(l => l).ToList();
                    var ms = lvs.Select(lv => { var idx = Enumerable.Range(0, _data.Count).Where(i => _data[i][f.Name].ToString() == lv); return Math.Round(idx.Any() ? idx.Average(i => _responses[i]) : gm, 4); }).ToList();
                    fx[f.Name] = new { type = "categorical", levels = lvs, means = ms, grand_mean = Math.Round(gm, 4) };
                }
                else
                {
                    var sorted = _data.Select((d, i) => (val: Convert.ToDouble(d[f.Name]), resp: _responses[i])).OrderBy(x => x.val).ToList(); int n = sorted.Count; int t = n / 3;
                    fx[f.Name] = new
                    {
                        type = "continuous",
                        levels = new[] { Math.Round(f.Lower, 4), Math.Round((f.Lower + f.Upper) / 2, 4), Math.Round(f.Upper, 4) },
                        means = new[] { Math.Round(sorted.Take(Math.Max(t, 1)).Average(x => x.resp), 4), Math.Round(sorted.Skip(t).Take(Math.Max(n - 2 * t, 1)).Average(x => x.resp), 4), Math.Round(sorted.Skip(n - Math.Max(t, 1)).Average(x => x.resp), 4) },
                        grand_mean = Math.Round(gm, 4)
                    };
                }
            }
            return JsonConvert.SerializeObject(fx);
        }

        // ═══ 交互效应图 ═══
        public async Task<string> GetInteractionEffectsAsync(string batchId) { if (_lastBatchId == batchId && _data != null) return InterFxJson(); await LoadBatchDataAsync(batchId, null); return InterFxJson(); }
        public async Task<string> GetInteractionEffectsForResponseAsync(string batchId, string responseName) { await EnsureLoaded(batchId, responseName); return InterFxJson(); }
        public string GetInteractionEffectsDirectAsync(string responseName) => InterFxJson();
        private string InterFxJson()
        {
            if (_factors == null) return "{}";
            var ints = new List<object>(); for (int i = 0; i < _factors.Count; i++) for (int j = i + 1; j < _factors.Count; j++) ints.Add(new { factor1 = _factors[i].Name, factor2 = _factors[j].Name });
            return JsonConvert.SerializeObject(new { interactions = ints });
        }

        // ═══ Box-Cox ═══
        public async Task<string> GetBoxCoxAnalysisAsync(string batchId, string responseName) { await EnsureLoaded(batchId, responseName); return BoxCoxJson(); }
        public string GetBoxCoxAnalysisDirectAsync(string responseName) => BoxCoxJson();
        private string BoxCoxJson()
        {
            if (_responses == null || _fitResult == null || _encoder == null || _factors == null)
                return """{"error":"无数据"}""";
            if (_responses.Any(y => y <= 0))
                return JsonConvert.SerializeObject(new { error = "Box-Cox 要求所有响应值 > 0" });

            int nLam = 61;
            var lambdas = Enumerable.Range(-30, nLam).Select(i => i * 0.1).ToList();
            var logLikelihoods = new List<double?>();
            int n = _responses.Length;
            double sumLogY = _responses.Sum(y => Math.Log(y));

            double bestLambda = 1.0;
            double bestLL = double.MinValue;

            foreach (var lam in lambdas)
            {
                var tr = _responses.Select(y => Math.Abs(lam) > 0.01 ? (Math.Pow(y, lam) - 1) / lam : Math.Log(y)).ToArray();
                double mean = tr.Average();
                double sse = tr.Sum(t => (t - mean) * (t - mean));
                // Log-likelihood: -n/2 * ln(SSE/n) + (λ-1)*Σln(y)
                double ll = -n / 2.0 * Math.Log(Math.Max(sse / n, 1e-30)) + (lam - 1) * sumLogY;
                logLikelihoods.Add(Math.Round(ll, 4));
                if (ll > bestLL) { bestLL = ll; bestLambda = lam; }
            }

            // 圆整到最近的 0.5
            double roundedLambda = Math.Round(bestLambda * 2) / 2.0;

            // 变换名称
            string transformName = roundedLambda switch
            {
                0.0 => "Ln(Y)",
                0.5 => "√Y",
                -1.0 => "1/Y",
                -0.5 => "1/√Y",
                1.0 => "无变换",
                2.0 => "Y²",
                _ => $"Y^{roundedLambda}"
            };

            // 原始模型 R²
            double origR2 = _fitResult.RSquared;

            // 变换后模型 R²
            double transR2 = origR2;
            try
            {
                var trY = _responses.Select(y => Math.Abs(roundedLambda) > 0.01 ? (Math.Pow(y, roundedLambda) - 1) / roundedLambda : Math.Log(y)).ToArray();
                var transResult = AnalyzerFactory.CreateAndAnalyze(_lastDesignMethod, _factors, _data!, trY, _lastResponseName, "interaction");
                transR2 = transResult.ModelSummary?.RSquared ?? origR2;
            }
            catch { /* 变换失败用原始 R² */ }

            bool recommend = Math.Abs(roundedLambda - 1.0) > 0.01 && transR2 > origR2 + 0.01;
            string recommendation = recommend
                ? $"建议使用 {transformName} 变换 (λ={roundedLambda})，R² 从 {origR2:F4} 提升至 {transR2:F4}"
                : $"当前模型已较好 (R²={origR2:F4})，无需变换";

            // 95% CI for λ: LL > bestLL - chi2(0.95,1)/2 = bestLL - 1.921
            double llCutoff = bestLL - 1.921;
            double ciLo = lambdas.First(), ciHi = lambdas.Last();
            for (int i = 0; i < lambdas.Count; i++)
                if (logLikelihoods[i].HasValue && logLikelihoods[i]!.Value >= llCutoff) { ciLo = lambdas[i]; break; }
            for (int i = lambdas.Count - 1; i >= 0; i--)
                if (logLikelihoods[i].HasValue && logLikelihoods[i]!.Value >= llCutoff) { ciHi = lambdas[i]; break; }

            return JsonConvert.SerializeObject(new
            {
                optimal_lambda = Math.Round(bestLambda, 2),
                rounded_lambda = roundedLambda,
                lambda_ci = new[] { ciLo, ciHi },
                transform_name = transformName,
                original_r_squared = Math.Round(origR2, 6),
                transformed_r_squared = Math.Round(transR2, 6),
                original_r_squared_adj = Math.Round(_fitResult.RSquaredAdj, 6),
                transformed_r_squared_adj = Math.Round(transR2, 6),
                improvement = Math.Round(transR2 - origR2, 6),
                recommend_transform = recommend,
                recommendation = recommendation,
                all_positive = true,
                lambda_profile = new
                {
                    lambdas = lambdas.Select(l => Math.Round(l, 1)).ToList(),
                    log_likelihoods = logLikelihoods
                }
            });
        }
        public async Task<OLSAnalysisResult> ApplyBoxCoxAsync(string batchId, string responseName, double lv, string modelType = "quadratic")
        {
            await EnsureLoaded(batchId, responseName);
            var tr = _responses!.Select(y => Math.Abs(lv) > 0.01 ? (Math.Pow(y, lv) - 1) / lv : Math.Log(y)).ToArray();
            var result = AnalyzerFactory.CreateAndAnalyze(_lastDesignMethod, _factors!, _data!, tr, responseName, modelType);
            CacheFitState(result); return result;
        }

        // ═══ 图片 (C# 不生成 matplotlib，返回空) ═══
        public async Task<byte[]> GetOlsResponseSurfaceImageAsync(string b, string r, string f1, string f2) => Array.Empty<byte>();
        public Task<byte[]> GetResponseSurfaceImageAsync(string b, string f1, string f2) => Task.FromResult(Array.Empty<byte>());
        public Task<byte[]> GetResponseSurfaceImageFromGPRAsync(string f1, string f2) => Task.FromResult(Array.Empty<byte>());
        public byte[] GetOlsSurfaceImageDirectAsync(string f1, string f2, string bj) => Array.Empty<byte>();

        // ═══ 报告导出 ═══
        public Task<string> ExportOlsReportAsync(string b, string r, string o, string t = "OLS 回归分析报告") { _logger.LogWarning("报告导出待实现"); return Task.FromResult(""); }

        // ═══ GPR 相关 (保留签名) ═══
        public Task<string> GetParetoChartAsync(string b) => Task.FromResult("{}");
        public Task<string> GetResponseSurfaceDataAsync(string b, string f1, string f2) => Task.FromResult("{}");
        public Task<string> GetContourDataAsync(string b, string f1, string f2) => Task.FromResult("{}");
        public Task<string> GetResidualAnalysisAsync(string b) => Task.FromResult("{}");
        public Task<string> GetActualVsPredictedAsync(string b) => Task.FromResult("{}");
        public Task<string> GetAnovaTableAsync(string b) => Task.FromResult("{}");
        public Task<string> GetRegressionSummaryAsync(string b) => Task.FromResult("{}");
        public Task<string> GetGPRConfidenceBandAsync(string f) => Task.FromResult("{}");
        public Task<string> GetAcquisitionSurfaceAsync(string f, string f1, string f2) => Task.FromResult("{}");
        public Task<string> GetModelEvolutionAsync(string f) => Task.FromResult("{}");
        public double PredictFromCurrentModel(Dictionary<string, double> naturalFactorValues)
        {
            if (_fitResult == null || _encoder == null) return 0;

            try
            {
                // 编码因子值
                var coded = _encoder.EncodeRow(
                    naturalFactorValues.ToDictionary(kv => kv.Key, kv => (object)kv.Value));
                var x0 = BuildX0(coded);

                // ŷ = Σβⱼxⱼ
                double pred = 0;
                for (int j = 0; j < _fitResult.P && j < x0.Length; j++)
                    pred += _fitResult.Coefficients[j] * x0[j];
                return pred;
            }
            catch { return 0; }
        }
        /// <summary>
        /// ★ 快速预测: 直接用缓存的 β 系数做 ŷ = Σβⱼxⱼ
        /// 不走 EncodeRow/JSON, 纯数学运算, 用于 Desirability 网格搜索
        /// 
        /// naturalFactorValues: 只含连续因子的自然值 (如 {"温度": 80, "压力": 5})
        /// 类别因子用 categoricalValues 传 (如 {"催化剂": "Cat_A"})
        /// </summary>
        public double PredictFromCurrentModel(
            Dictionary<string, double> continuousValues,
            Dictionary<string, string>? categoricalValues = null)
        {
            if (_fitResult == null || _encoder == null || _factors == null) return 0;

            try
            {
                // 1. 获取编码列名和编码信息
                var codedColNames = _encoder.GetCodedColumnNames();
                var codingInfo = _encoder.GetCodingInfo();

                // 2. 构建编码值字典 (列名 → 编码值)
                var codedValues = new Dictionary<string, double>();

                foreach (var f in _factors)
                {
                    if (f.IsCategorical)
                    {
                        // 类别因子: Sum coding
                        var levels = f.Levels ?? new List<string>();
                        string currentLevel = categoricalValues != null && categoricalValues.TryGetValue(f.Name, out var lv)
                            ? lv
                            : (levels.Count > 0 ? levels[0] : "");

                        int levelIdx = levels.IndexOf(currentLevel);
                        for (int i = 0; i < levels.Count - 1; i++)
                        {
                            string colName = $"{f.Name}[{levels[i]}]";
                            if (levelIdx < 0)
                                codedValues[colName] = 0;
                            else if (levelIdx < levels.Count - 1)
                                codedValues[colName] = (levelIdx == i) ? 1.0 : 0.0;
                            else
                                codedValues[colName] = -1.0;  // 最后一个水平
                        }
                    }
                    else
                    {
                        // 连续因子: (val - center) / halfRange
                        double raw = continuousValues.TryGetValue(f.Name, out var v) ? v : ((f.Lower + f.Upper) / 2.0);
                        double center = (f.Lower + f.Upper) / 2.0;
                        double halfRange = (f.Upper - f.Lower) / 2.0;
                        double coded = halfRange > 1e-15 ? (raw - center) / halfRange : 0;
                        codedValues[f.Name] = coded;
                    }
                }

                // 3. 构建 x0 向量, 直接用 _fitResult.TermNames
                double pred = 0;
                for (int j = 0; j < _fitResult.P; j++)
                {
                    string name = _fitResult.TermNames[j];
                    double xj;

                    if (name == "Intercept")
                    {
                        xj = 1.0;
                    }
                    else if (name == "Ct Pt")
                    {
                        xj = 0;  // 预测新点时 Ct Pt = 0
                    }
                    else if (name.Contains("²"))
                    {
                        string baseName = name.Replace("²", "");
                        xj = codedValues.TryGetValue(baseName, out var v) ? v * v : 0;
                    }
                    else if (name.Contains(":"))
                    {
                        var parts = name.Split(':');
                        xj = 1.0;
                        foreach (var part in parts)
                        {
                            if (codedValues.TryGetValue(part, out var v))
                                xj *= v;
                            else
                                xj = 0;
                        }
                    }
                    else
                    {
                        xj = codedValues.TryGetValue(name, out var v) ? v : 0;
                    }

                    pred += _fitResult.Coefficients[j] * xj;
                }

                return pred;
            }
            catch { return 0; }
        }
        // ═══ 内部方法 ═══
        private async Task EnsureLoaded(string batchId, string responseName)
        { if (_lastBatchId != batchId || _lastResponseName != responseName) { await LoadBatchDataAsync(batchId, responseName); if (_fitResult == null) await FitOlsAsync(batchId, responseName, _lastModelType); } }

        private async Task LoadBatchDataAsync(string batchId, string? responseName)
        {
            var batch = await _repository.GetBatchWithDetailsAsync(batchId);
            if (batch == null) throw new InvalidOperationException($"批次不存在: {batchId}");
            _factors = batch.Factors.Select(f => new FactorDefinition
            {
                Name = f.FactorName,
                IsCategorical = f.FactorType == DOEFactorType.Categorical,
                Lower = f.LowerBound,
                Upper = f.UpperBound,
                Levels = f.FactorType == DOEFactorType.Categorical ? f.CategoryLevels?.Split(',').Select(l => l.Trim()).ToList() : null
            }).ToList();
            _lastResolution = 3;  // 默认值
            if (!string.IsNullOrEmpty(batch.DesignConfigJson))
            {
                try
                {
                    var config = JsonConvert.DeserializeObject<Dictionary<string, object>>(batch.DesignConfigJson);
                    if (config != null && config.TryGetValue("fractionalResolution", out var r) && r != null)
                    {
                        if (int.TryParse(r.ToString(), out int res))
                            _lastResolution = res;
                    }
                }
                catch { /* 解析失败用默认 3 */ }
            }
            _lastDesignMethod = batch.DesignMethod;
            _encoder = new FactorEncoder(_factors);
            if (string.IsNullOrEmpty(responseName)) { var resps = await _repository.GetResponsesAsync(batchId); responseName = resps.FirstOrDefault()?.ResponseName ?? ""; }
            var runs = batch.Runs.Where(r => r.Status == DOERunStatus.Completed && !string.IsNullOrEmpty(r.ResponseValuesJson)).ToList();
            _data = new(); var rl = new List<double>();
            foreach (var run in runs)
            {
                var fv = JsonConvert.DeserializeObject<Dictionary<string, object>>(run.FactorValuesJson); var rv = JsonConvert.DeserializeObject<Dictionary<string, double>>(run.ResponseValuesJson!);
                if (fv != null && rv != null && rv.TryGetValue(responseName, out var y)) { _data.Add(fv); rl.Add(y); }
            }
            _responses = rl.ToArray(); _lastBatchId = batchId; _lastResponseName = responseName ?? ""; _lastDesignMethod = batch.DesignMethod;
            // ★ 新增: 从 DesignConfigJson 读取 fractionalResolution
            _lastResolution = 3;  // 默认值
            if (!string.IsNullOrEmpty(batch.DesignConfigJson))
            {
                try
                {
                    var config = JsonConvert.DeserializeObject<Dictionary<string, object>>(batch.DesignConfigJson);
                    if (config != null && config.TryGetValue("fractionalResolution", out var r) && r != null)
                    {
                        if (int.TryParse(r.ToString(), out int res))
                            _lastResolution = res;
                    }
                }
                catch { /* 解析失败用默认 3 */ }
            }
        }

        private void CacheFitState(OLSAnalysisResult result)
        {
            if (result.Coefficients == null || result.Coefficients.Count == 0) return;

            // 分离 Ct Pt 项（弯曲检验用，不参与预测和 XtXInverse）
            var allCoeffs = result.Coefficients;
            var predCoeffs = allCoeffs.Where(c => c.Term != "Ct Pt").ToList();
            int pPred = predCoeffs.Count; // 预测用的参数数（不含 Ct Pt）
            int pAll = allCoeffs.Count;   // 全部参数数（含 Ct Pt，用于 Pareto 等）

            _fitResult = new OlsFitResult
            {
                N = _responses?.Length ?? 0,
                P = pAll, // Pareto/ANOVA 用全部参数
                TermNames = allCoeffs.Select(c => c.Term).ToArray(),
                Coefficients = allCoeffs.Select(c => c.Coefficient).ToArray(),
                StdErrors = allCoeffs.Select(c => c.StdError).ToArray(),
                TValues = allCoeffs.Select(c => c.TValue).ToArray(),
                PValues = allCoeffs.Select(c => c.PValue).ToArray(),
                Sigma = result.Sigma,
                MSE = result.Sigma * result.Sigma,
                TCrit = result.TCrit,
                DfResidual = (_responses?.Length ?? 0) - pPred, // 默认用预测参数数，后面如果 invDim=pAll 会修正
                DfModel = pPred - 1,
                XtxInvFlat = result.XtxInvFlat ?? Array.Empty<double>(),
                RMSE = result.ModelSummary?.RMSE ?? 0,
                RSquared = result.ModelSummary?.RSquared ?? 0,
                RSquaredAdj = result.ModelSummary?.RSquaredAdj ?? 0,
                AdequatePrecision = result.ModelSummary?.AdequatePrecision ?? 0,
                PRESS = result.ModelSummary?.PRESS ?? 0,
                FPValue = result.ModelSummary?.ModelP ?? 1
            };

            // XtXInverse 维度匹配：优先 pPred（完整模型排除CtPt），其次 pAll（精简模型含CtPt）
            int invDim = 0;
            if (result.XtxInvFlat != null && result.XtxInvFlat.Length == pPred * pPred)
                invDim = pPred;
            else if (result.XtxInvFlat != null && result.XtxInvFlat.Length == pAll * pAll)
                invDim = pAll;

            if (invDim > 0)
            {
                _fitResult.XtXInverse = new double[invDim, invDim];
                for (int i = 0; i < invDim; i++)
                    for (int j = 0; j < invDim; j++)
                        _fitResult.XtXInverse[i, j] = result.XtxInvFlat![i * invDim + j];

                // 如果 XtXInverse 含 CtPt（invDim=pAll），修正 df
                if (invDim == pAll && pAll > pPred)
                {
                    _fitResult.DfResidual = (_responses?.Length ?? 0) - pAll;
                    _fitResult.TCrit = _fitResult.DfResidual > 0
                        ? StudentT.InvCDF(0, 1, _fitResult.DfResidual, 0.975) : 2.0;
                }
            }

            if (_data != null && _responses != null && _encoder != null && _responses.Length > 0) try { RefillDiag(); } catch { }

            // 优先用 Analyzer 直接算的残差数据
            // ★ 但只有当 Analyzer 缓存的数据量和当前数据量匹配时才用
            // （精简拟合不走 AnalyzerFactory，此时 LastN 是旧模型的，不应覆盖）
            if (AnalyzerFactory.LastFitted != null && AnalyzerFactory.LastN > 0
                && AnalyzerFactory.LastN == (_responses?.Length ?? 0)
                && AnalyzerFactory.LastFitted.Length == (_responses?.Length ?? 0))
            {
                // 额外检查：如果 _fitResult 的残差已经被 RefillDiag 正确填充了，
                // 且 Analyzer 缓存的系数数和当前不一致，跳过覆盖
                bool analyzerMatchesCurrent = true;
                if (_fitResult?.FittedValues != null && _fitResult.FittedValues.Length > 0)
                {
                    // 如果 RefillDiag 算出的拟合值和 Analyzer 缓存的差异很大，说明模型变了
                    double diff = Math.Abs(_fitResult.FittedValues[0] - AnalyzerFactory.LastFitted[0]);
                    if (diff > 0.001) analyzerMatchesCurrent = false;
                }

                if (analyzerMatchesCurrent)
                {
                    _cachedFitted = AnalyzerFactory.LastFitted;
                    _cachedResiduals = AnalyzerFactory.LastResiduals;
                    _cachedStdResiduals = AnalyzerFactory.LastStdResiduals;
                    _cachedCooksD = AnalyzerFactory.LastCooksD;
                    _cachedLeverage = AnalyzerFactory.LastLeverage;
                    _cachedN = AnalyzerFactory.LastN;
                }
            }
        }

        private void RefillDiag()
        {
            if (_fitResult == null || _data == null || _responses == null || _encoder == null) return;
            int n = _responses.Length; _fitResult.N = n;
            _fitResult.FittedValues = new double[n]; _fitResult.Residuals = new double[n]; _fitResult.StandardizedResiduals = new double[n]; _fitResult.CooksDistance = new double[n]; _fitResult.Leverage = new double[n];
            for (int i = 0; i < n; i++)
            {
                try
                {
                    var cd = _encoder.EncodeRow(_data[i]); var x0 = BuildX0(cd); var (pr, se) = PredictSafe(x0);
                    _fitResult.FittedValues[i] = pr; _fitResult.Residuals[i] = _responses[i] - pr;
                    double d = Math.Sqrt(Math.Max(_fitResult.MSE, 1e-15)); _fitResult.StandardizedResiduals[i] = d > 1e-15 ? _fitResult.Residuals[i] / d : 0;
                }
                catch { _fitResult.FittedValues[i] = 0; _fitResult.Residuals[i] = _responses[i]; }
            }
        }

        private double[] BuildX0(double[] coded)
        {
            if (_fitResult == null) return Array.Empty<double>();
            // 构建全维度 x0（含 Ct Pt 位置填 0），用于 β^T * x0 预测
            var x0 = new double[_fitResult.P]; x0[0] = 1;
            var cn = _encoder!.GetCodedColumnNames();
            for (int j = 1; j < _fitResult.P; j++)
            {
                string nm = _fitResult.TermNames[j];
                if (nm == "Ct Pt") x0[j] = 0;
                else if (nm.Contains("²")) { string f = nm.Replace("²", ""); int idx = cn.IndexOf(f); x0[j] = idx >= 0 && idx < coded.Length ? coded[idx] * coded[idx] : 0; }
                else if (nm.Contains(":")) { var pts = nm.Split(':'); double prod = 1; bool ok = true; foreach (var pt in pts) { int ix = cn.IndexOf(pt); if (ix >= 0 && ix < coded.Length) prod *= coded[ix]; else { ok = false; break; } } x0[j] = ok ? prod : 0; }
                else { int idx = cn.IndexOf(nm); x0[j] = idx >= 0 && idx < coded.Length ? coded[idx] : 0; }
            }
            return x0;
        }

        /// <summary>
        /// 预测：ŷ = β^T * x0，SE = σ * sqrt(x0_pred^T * (X'X)^-1 * x0_pred)
        /// x0_pred 是排除 Ct Pt 后的子向量，匹配 XtXInverse 维度
        /// </summary>
        private (double predicted, double se) PredictSafe(double[] x0)
        {
            if (_fitResult == null) return (0, 0);

            // ŷ = Σ βj * x0j（全部项，Ct Pt 的 x0=0 所以不影响）
            double pred = 0;
            for (int j = 0; j < _fitResult.P && j < x0.Length; j++)
                pred += _fitResult.Coefficients[j] * x0[j];

            // SE 需要匹配 XtXInverse 维度
            if (_fitResult.XtXInverse == null) return (pred, _fitResult.Sigma);

            int pInv = _fitResult.XtXInverse.GetLength(0);

            // 如果 XtXInverse 维度 = P（含 CtPt），直接用全向量
            // 如果 XtXInverse 维度 < P（排除 CtPt），构建子向量
            double[] x0Pred;
            if (pInv == _fitResult.P)
            {
                // XtXInverse 含 CtPt（精简模型 keepCtPt=true），直接用 x0
                x0Pred = new double[pInv];
                for (int j = 0; j < pInv && j < x0.Length; j++)
                    x0Pred[j] = x0[j];
            }
            else
            {
                // XtXInverse 排除 CtPt（完整模型），构建子向量
                x0Pred = new double[pInv];
                int ki = 0;
                for (int j = 0; j < _fitResult.P && j < x0.Length; j++)
                {
                    if (_fitResult.TermNames[j] == "Ct Pt") continue;
                    if (ki < pInv) x0Pred[ki++] = x0[j];
                }
            }

            // SE = σ * sqrt(x0^T * (X'X)^-1 * x0)
            double xCx = 0;
            for (int i = 0; i < pInv; i++)
            {
                double s = 0;
                for (int j = 0; j < pInv; j++)
                    s += _fitResult.XtXInverse[i, j] * x0Pred[j];
                xCx += x0Pred[i] * s;
            }

            double se = _fitResult.Sigma * Math.Sqrt(Math.Max(xCx, 0));
            return (pred, se);
        }

        private object NormalQQ(double[] r) { var s = r.OrderBy(x => x).ToArray(); int n = s.Length; var t = new double[n]; for (int i = 0; i < n; i++) t[i] = Normal.InvCDF(0, 1, (i + 0.5) / n); return new { theoretical = t, sample = s }; }

        private List<FactorDefinition> InferFactorDefs(List<Dictionary<string, object>> data, Dictionary<string, string>? ft)
        {
            if (data.Count == 0) return new(); return data[0].Keys.Select(k => {
                bool ic = ft?.TryGetValue(k, out var t) == true && t == "categorical";
                if (ic) return new FactorDefinition { Name = k, IsCategorical = true, Levels = data.Select(d => d[k].ToString()!).Distinct().OrderBy(l => l).ToList() };
                var vs = data.Select(d => Convert.ToDouble(d[k])).ToArray(); return new FactorDefinition { Name = k, Lower = vs.Min(), Upper = vs.Max() };
            }).ToList();
        }

        private List<Dictionary<string, object>> GenGrid(List<FactorDefinition> fs, int g)
        {
            if (fs.Count == 0) return new() { new() }; var gv = fs.Select(f => Enumerable.Range(0, g).Select(i => f.Lower + (f.Upper - f.Lower) * i / (g - 1)).ToList()).ToList();
            var r = new List<Dictionary<string, object>> { new() }; for (int fi = 0; fi < fs.Count; fi++)
            {
                var nr = new List<Dictionary<string, object>>();
                foreach (var e in r) foreach (var v in gv[fi]) { var np = new Dictionary<string, object>(e); np[fs[fi].Name] = (object)v; nr.Add(np); }
                r = nr;
            }
            return r;
        }

        private List<Dictionary<string, object>> GenCatCombos(List<FactorDefinition> fs)
        {
            if (fs.Count == 0) return new() { new() }; var r = new List<Dictionary<string, object>> { new() };
            foreach (var f in fs) { var nr = new List<Dictionary<string, object>>(); foreach (var e in r) foreach (var l in f.Levels ?? new()) { var nc = new Dictionary<string, object>(e); nc[f.Name] = l; nr.Add(nc); } r = nr; }
            return r;
        }

        private static OLSAnalysisResult ConvertRsmToOls(RsmAnalysisResult src)
        {
            // 这个方法桥接 Analyzer 输出到 OLSAnalysisResult，与 AnalyzerFactory 中的逻辑一致
            // 实际应用中应调用 AnalyzerFactory.ConvertRsmResultPublic
            return new OLSAnalysisResult { OriginalModelType = "custom" };
        }
    }
}
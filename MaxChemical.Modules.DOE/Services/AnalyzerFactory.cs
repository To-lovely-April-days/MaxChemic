using System;
using System.Collections.Generic;
using MaxChemical.Modules.DOE.Models;
using MaxChemical.Modules.DOE.OLS.Analyzers;
using MaxChemical.Modules.DOE.OLS.Core;

namespace MaxChemical.Modules.DOE.OLS.Services
{
    /// <summary>
    /// Analyzer 工厂 — 根据 DOEDesignMethod 创建对应的独立分析器
    /// 
    /// 这是整个系统中唯一的 switch/分发点。
    /// 每种 DOE 设计方法都有自己独立的 Analyzer 类，工厂只负责创建。
    /// </summary>
    public static class AnalyzerFactory
    {
        /// <summary>最近一次分析的残差诊断数据（供 Service 层取用）</summary>
        public static double[]? LastFitted { get; private set; }
        public static double[]? LastResiduals { get; private set; }
        public static double[]? LastStdResiduals { get; private set; }
        public static double[]? LastCooksD { get; private set; }
        public static double[]? LastLeverage { get; private set; }
        public static int LastN { get; private set; }

        /// <summary>
        /// 创建分析器并执行分析，返回统一的 OLSAnalysisResult
        /// </summary>
        public static OLSAnalysisResult CreateAndAnalyze(
     DOEDesignMethod designMethod,
     List<FactorDefinition> factors,
     List<Dictionary<string, object>> data,
     double[] responses,
     string responseName,
     string modelType = "auto",
     int resolution = 3,
     bool includeCtPt = true,
     int modelOrder = 0)  // ★ 新参数
        {
            return designMethod switch
            {
                // ── 筛选阶段 ──
                DOEDesignMethod.FractionalFactorial =>
                    ConvertFractionalResult(
                        new FractionalFactorialAnalyzer(
                            factors, data, responses, responseName,
                            resolution, modelType, includeCtPt, modelOrder).Analyze()),

                DOEDesignMethod.PlackettBurman =>
                    ConvertFractionalResult(
                        new PlackettBurmanAnalyzer(factors, data, responses, responseName).Analyze()),

                // ── 表征阶段 ──
                DOEDesignMethod.FullFactorial =>
                    ConvertFullFactorialResult(
                        new FullFactorialAnalyzer(factors, data, responses, responseName, modelOrder).Analyze()),

                // ── RSM 阶段 ──
                DOEDesignMethod.CCD =>
                    ConvertRsmResult(
                        new CcdAnalyzer(factors, data, responses, responseName).Analyze()),

                DOEDesignMethod.BoxBehnken =>
                    ConvertRsmResult(
                        new BoxBehnkenAnalyzer(factors, data, responses, responseName).Analyze()),

                DOEDesignMethod.DOptimal =>
                    ConvertRsmResult(
                        new DOptimalAnalyzer(factors, data, responses, responseName, modelType).Analyze()),

                DOEDesignMethod.AugmentedDesign =>
                    ConvertRsmResult(
                        new AugmentedDesignAnalyzer(factors, data, responses, responseName).Analyze().RsmResult),

                DOEDesignMethod.Custom or DOEDesignMethod.Taguchi =>
                    ConvertRsmResult(
                        new CustomDesignAnalyzer(factors, data, responses, responseName, modelType).Analyze()),

                // ── 爬坡和验证不走这个入口（它们有特殊的 API） ──
                DOEDesignMethod.SteepestAscent =>
                    new OLSAnalysisResult { InestimableWarning = "最速上升不需要拟合新模型，请使用 SteepestAscentAnalyzer" },

                DOEDesignMethod.ConfirmationRuns =>
                    new OLSAnalysisResult { InestimableWarning = "验证实验不需要拟合新模型，请使用 ConfirmationRunsAnalyzer" },

                _ => ConvertRsmResult(
                    new CustomDesignAnalyzer(factors, data, responses, responseName, modelType).Analyze())
            };
        }

        // ═══════════════════════════════════════════════════════
        // 结果转换: 各 Analyzer 的特定 Result → 通用 OLSAnalysisResult
        // 保持与你现有 ViewModel 绑定的 OLSAnalysisResult 完全兼容
        // ═══════════════════════════════════════════════════════

        private static OLSAnalysisResult ConvertFractionalResult(FractionalFactorialResult src)
        {
            var dst = new OLSAnalysisResult();
            if (src.Error != null) { dst.InestimableWarning = src.Error; return dst; }

            MapCommonFields(dst, src.Coefficients, src.ModelSummary, src.AnovaTable,
                src.CodingInfo, src.XtxInvFlat);
            dst.OriginalModelType = "linear";
            // ★ 新增: 映射 Reduction
            if (src.Reduction != null)
            {
                dst.Reduction = src.Reduction;
            }

            // ★ 新增: 映射 CurvatureTest (从 FF Analyzer 的 CurvatureTestOutput 转成 CurvatureTestResult)
            if (src.CurvatureTest != null && src.CurvatureTest.HasCurvatureTest)
            {
                dst.CurvatureTest = new CurvatureTestResult
                {
                    HasCurvatureTest = true,
                    CenterMean = src.CurvatureTest.CenterMean,
                    CornerMean = src.CurvatureTest.CornerMean,
                    CtPtCoefficient = src.CurvatureTest.CtPtCoefficient,
                    CtPtStdError = src.CurvatureTest.SE,
                    CtPtTValue = src.CurvatureTest.TStatistic,
                    CtPtPValue = src.CurvatureTest.PValue
                };
            }
            if (src.ResidualDiagnostics != null)
            {
                LastFitted = src.ResidualDiagnostics.Fitted;
                LastResiduals = src.ResidualDiagnostics.Residuals;
                LastStdResiduals = src.ResidualDiagnostics.StandardizedResiduals;
                LastCooksD = src.ResidualDiagnostics.CooksDistance;
                LastLeverage = src.ResidualDiagnostics.Leverage;
                LastN = src.ResidualDiagnostics.Fitted?.Length ?? 0;
            }
            return dst;
        }

        private static OLSAnalysisResult ConvertFullFactorialResult(FullFactorialResult src)
        {
            var dst = new OLSAnalysisResult();
            if (src.Error != null) { dst.InestimableWarning = src.Error; return dst; }

            MapCommonFields(dst, src.Coefficients, src.ModelSummary, src.AnovaTable,
                src.CodingInfo, src.XtxInvFlat);
            dst.OriginalModelType = "interaction";

            // 缓存残差诊断数据
            if (src.ResidualDiagnostics != null)
            {
                LastFitted = src.ResidualDiagnostics.Fitted;
                LastResiduals = src.ResidualDiagnostics.Residuals;
                LastStdResiduals = src.ResidualDiagnostics.StandardizedResiduals;
                LastCooksD = src.ResidualDiagnostics.CooksDistance;
                LastLeverage = src.ResidualDiagnostics.Leverage;
                LastN = src.ResidualDiagnostics.Fitted?.Length ?? 0;
            }

            // 弯曲检验 → CurvatureTestResult（对齐你现有 Model 的字段名）
            if (src.CurvatureTest?.HasCurvatureTest == true)
            {
                dst.CurvatureTest = new CurvatureTestResult
                {
                    HasCurvatureTest = true,
                    CenterMean = src.CurvatureTest.CenterMean,
                    CornerMean = src.CurvatureTest.CornerMean,
                    CtPtCoefficient = src.CurvatureTest.CtPtCoefficient,
                    CtPtStdError = src.CurvatureTest.SE,
                    CtPtTValue = src.CurvatureTest.TStatistic,
                    CtPtPValue = src.CurvatureTest.PValue,
                    CurvatureP = src.CurvatureTest.PValue,
                    CurvatureDF = 1
                    // IsSignificant 是只读计算属性 (=> CurvatureP < 0.05)，不需要赋值
                };
            }

            return dst;
        }

        private static OLSAnalysisResult ConvertRsmResult(RsmAnalysisResult src)
        {
            var dst = new OLSAnalysisResult();
            if (src.Error != null) { dst.InestimableWarning = src.Error; return dst; }

            MapCommonFields(dst, src.Coefficients, src.ModelSummary, src.AnovaTable,
                src.CodingInfo, src.XtxInvFlat);
            dst.OriginalModelType = "quadratic";

            if (src.ResidualDiagnostics != null)
            {
                LastFitted = src.ResidualDiagnostics.Fitted;
                LastResiduals = src.ResidualDiagnostics.Residuals;
                LastStdResiduals = src.ResidualDiagnostics.StandardizedResiduals;
                LastCooksD = src.ResidualDiagnostics.CooksDistance;
                LastLeverage = src.ResidualDiagnostics.Leverage;
                LastN = src.ResidualDiagnostics.Fitted?.Length ?? 0;
            }
            return dst;
        }

        private static void MapCommonFields(
            OLSAnalysisResult dst,
            List<CoefficientOutput> coeffs,
            ModelSummaryOutput summary,
            List<AnovaRowOutput> anova,
            Dictionary<string, CodingInfoExport> codingInfo,
            double[] xtxInvFlat)
        {
            // 系数表（编码值系数）
            dst.Coefficients = coeffs.ConvertAll(c => new CoefficientRow
            {
                Term = c.Term,
                Coefficient = c.Coefficient,
                StdError = c.StdError,
                TValue = c.TValue,
                PValue = c.PValue,
                VIF = c.VIF,
                Effect = c.Effect
            });

            // 模型摘要（先创建，方程后续填入）
            dst.ModelSummary = new OLSModelSummary
            {
                RSquared = summary.RSquared,
                RSquaredAdj = summary.RSquaredAdj,
                RSquaredPred = summary.RSquaredPred,
                RMSE = summary.RMSE,
                PRESS = summary.PRESS,
                AdequatePrecision = summary.AdequatePrecision,
                LackOfFitP = summary.LackOfFitP,
                ModelP = summary.ModelP
            };

            // ═══ 回归方程构建（填入 ModelSummary.Equation + UncodedEquation + Equations） ═══
            var responseName = "Y";
            BuildEquations(dst, coeffs, codingInfo, responseName);

            // ANOVA 表
            dst.AnovaTable = anova.ConvertAll(a => new AnovaRow
            {
                Source = a.Source,
                DF = a.DF,
                SS = a.SS,
                MS = a.MS,
                FValue = a.FValue,
                PValue = a.PValue
            });

            // 编码信息
            dst.CodingInfo = new Dictionary<string, CodingInfoItem>();
            foreach (var kv in codingInfo)
            {
                dst.CodingInfo[kv.Key] = new CodingInfoItem
                {
                    Type = kv.Value.Type,
                    Center = kv.Value.Center,
                    HalfRange = kv.Value.HalfRange
                };
            }

            // CI 参数
            dst.XtxInvFlat = xtxInvFlat;
            dst.Sigma = summary.Sigma;
            dst.TCrit = summary.TCrit;
            dst.ParamCount = summary.ParamCount;
        }

        // ═══════════════════════════════════════════════════════
        // 回归方程构建 — 对标 Minitab 编码/未编码方程
        // ═══════════════════════════════════════════════════════

        private static void BuildEquations(
            OLSAnalysisResult dst,
            List<CoefficientOutput> coeffs,
            Dictionary<string, CodingInfoExport> codingInfo,
            string responseName)
        {
            // ── 1. 编码方程 ──
            var codedParts = new List<string>();
            var interceptCoeff = coeffs.FirstOrDefault(c => c.Term == "Intercept");
            if (interceptCoeff != null) codedParts.Add($"{interceptCoeff.Coefficient:F4}");
            foreach (var c in coeffs.Where(c => c.Term != "Intercept" && c.Term != "Ct Pt"))
            {
                string sign = c.Coefficient >= 0 ? "+" : "-";
                codedParts.Add($"{sign} {Math.Abs(c.Coefficient):F4}\u00d7{c.Term.Replace(":", "\u00d7")}");
            }
            string codedEq = $"{responseName} = {string.Join(" ", codedParts)}";

            // ── 2. 未编码方程 — 完整代数展开 ──
            // xᵢ = (Xᵢ - cᵢ)/hᵢ → 代入编码模型，收集原始变量各项系数
            var contInfo = new Dictionary<string, (double c, double h)>();
            foreach (var kv in codingInfo)
                if (kv.Value.Type == "continuous") contInfo[kv.Key] = (kv.Value.Center, kv.Value.HalfRange);

            var betaMap = new Dictionary<string, double>();
            foreach (var c in coeffs) betaMap[c.Term] = c.Coefficient;

            var fNames = contInfo.Keys.ToList();
            int k = fNames.Count;

            double uncIntercept = betaMap.GetValueOrDefault("Intercept", 0);
            var uncMain = fNames.ToDictionary(f => f, f => 0.0);

            // 主效应展开: βᵢ(Xᵢ-cᵢ)/hᵢ → (βᵢ/hᵢ)Xᵢ - βᵢcᵢ/hᵢ
            foreach (var fi in fNames)
            {
                double bi = betaMap.GetValueOrDefault(fi, 0);
                var (ci, hi) = contInfo[fi];
                uncMain[fi] += bi / hi;
                uncIntercept -= bi * ci / hi;
            }

            // 二因子交互展开: βᵢⱼ(Xᵢ-cᵢ)(Xⱼ-cⱼ)/(hᵢhⱼ)
            var uncInter2 = new Dictionary<string, double>();
            for (int i = 0; i < k; i++) for (int j = i + 1; j < k; j++)
                {
                    string fi = fNames[i], fj = fNames[j];
                    string key = $"{fi}:{fj}";
                    double bij = betaMap.GetValueOrDefault(key, betaMap.GetValueOrDefault($"{fj}:{fi}", 0));
                    if (Math.Abs(bij) < 1e-15) continue;
                    var (ci, hi) = contInfo[fi]; var (cj, hj) = contInfo[fj];
                    uncInter2[key] = bij / (hi * hj);
                    uncMain[fi] -= bij * cj / (hi * hj);
                    uncMain[fj] -= bij * ci / (hi * hj);
                    uncIntercept += bij * ci * cj / (hi * hj);
                }

            // 三因子交互展开: βᵢⱼₖ(Xᵢ-cᵢ)(Xⱼ-cⱼ)(Xₖ-cₖ)/(hᵢhⱼhₖ)
            var uncInter3 = new Dictionary<string, double>();
            for (int i = 0; i < k; i++) for (int j = i + 1; j < k; j++) for (int m = j + 1; m < k; m++)
                    {
                        string fi = fNames[i], fj = fNames[j], fm = fNames[m];
                        double bijk = FindBeta3(betaMap, fi, fj, fm);
                        if (Math.Abs(bijk) < 1e-15) continue;
                        var (ci, hi) = contInfo[fi]; var (cj, hj) = contInfo[fj]; var (cm, hm) = contInfo[fm];
                        string key3 = $"{fi}:{fj}:{fm}";
                        uncInter3[key3] = bijk / (hi * hj * hm);
                        // 对二因子交互的贡献
                        string kij = $"{fi}:{fj}", kim = $"{fi}:{fm}", kjm = $"{fj}:{fm}";
                        if (!uncInter2.ContainsKey(kij)) uncInter2[kij] = 0;
                        if (!uncInter2.ContainsKey(kim)) uncInter2[kim] = 0;
                        if (!uncInter2.ContainsKey(kjm)) uncInter2[kjm] = 0;
                        uncInter2[kij] -= bijk * cm / (hi * hj * hm);
                        uncInter2[kim] -= bijk * cj / (hi * hj * hm);
                        uncInter2[kjm] -= bijk * ci / (hi * hj * hm);
                        // 对主效应的贡献
                        uncMain[fi] += bijk * cj * cm / (hi * hj * hm);
                        uncMain[fj] += bijk * ci * cm / (hi * hj * hm);
                        uncMain[fm] += bijk * ci * cj / (hi * hj * hm);
                        // 对截距的贡献
                        uncIntercept -= bijk * ci * cj * cm / (hi * hj * hm);
                    }

            // 二次项展开: βᵢᵢ((Xᵢ-cᵢ)/hᵢ)²
            var uncQuad = new Dictionary<string, double>();
            foreach (var fi in fNames)
            {
                double bii = betaMap.GetValueOrDefault($"{fi}\u00b2", 0);
                if (Math.Abs(bii) < 1e-15) continue;
                var (ci, hi) = contInfo[fi];
                uncQuad[fi] = bii / (hi * hi);
                uncMain[fi] -= 2 * bii * ci / (hi * hi);
                uncIntercept += bii * ci * ci / (hi * hi);
            }

            // 组装
            var uncodedCoeffs = new List<UncCodedCoefficientRow>();
            var uncodedParts = new List<string>();
            uncodedCoeffs.Add(new UncCodedCoefficientRow { Term = "\u5e38\u91cf", Coefficient = Math.Round(uncIntercept, 4), StdError = interceptCoeff?.StdError ?? 0, TValue = interceptCoeff?.TValue ?? 0, PValue = interceptCoeff?.PValue ?? 0 });

            void AddUncoded(string term, double uc, string display)
            {
                var orig = coeffs.FirstOrDefault(c => c.Term == term);
                uncodedCoeffs.Add(new UncCodedCoefficientRow { Term = term, Coefficient = Math.Round(uc, 6), StdError = orig?.StdError ?? 0, TValue = orig?.TValue ?? 0, PValue = orig?.PValue ?? 0, VIF = orig?.VIF });
                string sign = uc >= 0 ? "+" : "-";
                uncodedParts.Add($"{sign} {Math.Abs(uc):G5} {display}");
            }

            foreach (var fi in fNames) AddUncoded(fi, uncMain[fi], fi);
            foreach (var kv in uncQuad) AddUncoded($"{kv.Key}\u00b2", kv.Value, $"{kv.Key}*{kv.Key}");
            foreach (var kv in uncInter2.Where(x => Math.Abs(x.Value) > 1e-15)) AddUncoded(kv.Key, kv.Value, kv.Key.Replace(":", "*"));
            foreach (var kv in uncInter3.Where(x => Math.Abs(x.Value) > 1e-15))
            {
                var orig = coeffs.FirstOrDefault(c => c.Term == kv.Key || (c.Term.Contains(":") && c.Term.Split(':').OrderBy(s => s).SequenceEqual(kv.Key.Split(':').OrderBy(s => s))));
                AddUncoded(kv.Key, kv.Value, kv.Key.Replace(":", "*"));
            }

            // Ct Pt
            var ctPt = coeffs.FirstOrDefault(c => c.Term == "Ct Pt");
            if (ctPt != null)
            {
                uncodedCoeffs.Add(new UncCodedCoefficientRow { Term = "Ct Pt", Coefficient = Math.Round(ctPt.Coefficient, 4), StdError = ctPt.StdError, TValue = ctPt.TValue, PValue = ctPt.PValue });
                string s = ctPt.Coefficient >= 0 ? "+" : "-";
                uncodedParts.Add($"{s} {Math.Abs(ctPt.Coefficient):G5} Ct Pt");
            }

            dst.UncodedCoefficients = uncodedCoeffs;
            dst.UncodedEquation = $"{responseName} = {uncIntercept:G6} {string.Join(" ", uncodedParts)}";

            // EquationsInfo
            var catFactors = codingInfo.Where(kv => kv.Value.Type == "categorical").ToList();
            bool hasCat = catFactors.Count > 0;
            var codedEqInfo = new EquationsInfo { HasCategorical = hasCat };
            var uncodedEqInfo = new EquationsInfo { HasCategorical = hasCat };
            if (hasCat)
            {
                var fc = catFactors.First();
                codedEqInfo.CategoricalFactor = fc.Key; uncodedEqInfo.CategoricalFactor = fc.Key;
                foreach (var lv in fc.Value.Levels ?? new List<string>())
                { codedEqInfo.EquationsByLevel[lv] = codedEq; uncodedEqInfo.EquationsByLevel[lv] = dst.UncodedEquation; }
                codedEqInfo.CommonEquation = codedEq; uncodedEqInfo.CommonEquation = dst.UncodedEquation;
            }
            dst.ModelSummary ??= new OLSModelSummary();
            dst.ModelSummary.Equation = codedEq;
            dst.ModelSummary.Equations = codedEqInfo;
            dst.UncodedEquations = uncodedEqInfo;
        }

        private static double FindBeta3(Dictionary<string, double> bm, string f1, string f2, string f3)
        {
            string[] ps = { $"{f1}:{f2}:{f3}", $"{f1}:{f3}:{f2}", $"{f2}:{f1}:{f3}", $"{f2}:{f3}:{f1}", $"{f3}:{f1}:{f2}", $"{f3}:{f2}:{f1}" };
            foreach (var p in ps) if (bm.TryGetValue(p, out var v)) return v;
            return 0;
        }
    }
}
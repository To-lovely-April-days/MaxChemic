using System;
using System.Collections.Generic;
using System.Linq;
using MaxChemical.Modules.DOE.Models;
using MathNet.Numerics.LinearAlgebra;
using MathNet.Numerics.LinearAlgebra.Double;

namespace MaxChemical.Modules.DOE.Services.Designs
{
    /// <summary>
    /// 设计质量评估器 (C# 原生实现, 替代 Python doe_engine.get_design_quality).
    ///
    /// 计算指标:
    ///   D-efficiency = (|X'X|^(1/p)) / n          越接近 1 越好
    ///   A-efficiency = p / (tr((X'X)^(-1)) × n)   越接近 1 越好
    ///   G-efficiency = p / (n × max_leverage)     越接近 1 越好
    ///   VIF = 每个因子的方差膨胀因子 (对角线 ((X'X)/(n))^(-1))
    ///   Condition number = ||X'X|| × ||(X'X)^(-1)||
    ///   Power = 按给定效应大小的检验功效 (这里使用固定 δ=1, σ=1, α=0.05)
    /// </summary>
    public static class DesignQualityEvaluator
    {
        public static DOEDesignQuality Evaluate(
            List<DOEFactor> factors,
            DOEDesignMatrix matrix,
            string modelType = "quadratic")
        {
            var result = new DOEDesignQuality
            {
                RunCount = matrix.RunCount,
                VIF = new Dictionary<string, double>(),
                PowerAnalysis = new Dictionary<string, double>()
            };

            if (matrix.RunCount == 0) return result;

            // 构建模型矩阵 X
            var (cont, cat) = DesignPipeline.SplitFactors(factors);
            var (X, termNames) = BuildModelMatrix(matrix.Rows, cont, cat, modelType);

            int n = X.RowCount;
            int p = X.ColumnCount;
            result.ParameterCount = p;
            result.DegreesOfFreedom = n - p;

            if (n < p)
            {
                // 数据不足
                result.DEfficiency = 0;
                result.AEfficiency = 0;
                result.GEfficiency = 0;
                result.ConditionNumber = double.PositiveInfinity;
                return result;
            }

            // X'X
            var XtX = X.TransposeThisAndMultiply(X);

            double detXtX = 0;
            Matrix<double>? XtXInv = null;
            try
            {
                var chol = XtX.Cholesky();
                double logDet = 0;
                for (int i = 0; i < p; i++) logDet += Math.Log(chol.Factor[i, i]);
                detXtX = Math.Exp(2 * logDet);
                XtXInv = chol.Solve(DenseMatrix.CreateIdentity(p));
            }
            catch
            {
                // 奇异矩阵: 所有效率 0
                result.DEfficiency = 0;
                result.AEfficiency = 0;
                result.GEfficiency = 0;
                result.ConditionNumber = double.PositiveInfinity;
                return result;
            }

            // D-efficiency = (|X'X|^(1/p)) / n
            result.DEfficiency = Math.Pow(detXtX, 1.0 / p) / n;

            // A-efficiency = p / (tr((X'X)^(-1)) × n)
            double trace = 0;
            for (int i = 0; i < p; i++) trace += XtXInv[i, i];
            result.AEfficiency = p / (trace * n);

            // G-efficiency = p / (n × max_leverage)
            // leverage_i = x_i' (X'X)^(-1) x_i
            double maxLev = 0;
            for (int i = 0; i < n; i++)
            {
                var xi = X.Row(i);
                double lev = xi * (XtXInv * xi);
                if (lev > maxLev) maxLev = lev;
            }
            result.GEfficiency = maxLev > 0 ? p / (n * maxLev) : 0;

            // VIF: 每个非截距项的 ((X'X/n)^(-1))_ii
            // 或者用 R² 方法: VIF_j = 1 / (1 - R²_j)
            // 这里用简单版: 标准化 (X'X / n)^(-1) 的对角线
            try
            {
                var scaled = XtX.Divide(n);
                var scaledInv = scaled.Inverse();
                for (int j = 1; j < p; j++) // 跳过截距
                {
                    double vif = scaledInv[j, j];
                    result.VIF[termNames[j]] = Math.Max(1.0, vif);
                }
            }
            catch
            {
                // 忽略 VIF 计算错误
            }

            // Condition number = sqrt(max_eig / min_eig)
            try
            {
                var evd = XtX.Evd();
                var eigs = evd.EigenValues.Select(c => Math.Abs(c.Real)).Where(v => v > 1e-15).ToList();
                if (eigs.Count > 0)
                {
                    double max = eigs.Max();
                    double min = eigs.Min();
                    result.ConditionNumber = min > 0 ? Math.Sqrt(max / min) : double.PositiveInfinity;
                }
            }
            catch
            {
                result.ConditionNumber = double.PositiveInfinity;
            }

            // Power: 对每个主效应, 按 δ=1, σ=1, α=0.05 计算
            // Power = Φ(√(δ² / ((X'X)^(-1))_jj) - z_{α/2})
            // 这里用简化近似
            double zAlpha = 1.96;
            for (int j = 1; j < p && j < termNames.Length; j++)
            {
                double se = Math.Sqrt(XtXInv[j, j]);
                double ncp = 1.0 / Math.Max(se, 1e-12); // δ/SE
                double power = StandardNormalCdf(ncp - zAlpha);
                result.PowerAnalysis[termNames[j]] = Math.Max(0, Math.Min(1, power));
            }

            return result;
        }

        // ══════════════════════════════════════════════════════
        // 模型矩阵构建
        // ══════════════════════════════════════════════════════

        private static (Matrix<double> X, string[] names) BuildModelMatrix(
            List<Dictionary<string, object>> rows,
            List<DOEFactor> cont,
            List<DOEFactor> cat,
            string modelType)
        {
            int n = rows.Count;

            // 每行展开为编码值列表
            var encodedRows = new List<(List<(string name, double v)> mains, List<(string, double)> terms)>();
            var termNamesOnce = new List<string>();
            bool firstRow = true;

            var matrixRows = new List<double[]>(n);
            foreach (var row in rows)
            {
                var mains = new List<(string, double)>();

                foreach (var f in cont)
                {
                    double actual = row.TryGetValue(f.FactorName, out var v) ? Convert.ToDouble(v) : 0;
                    double coded = (actual - (f.LowerBound + f.UpperBound) / 2.0) / ((f.UpperBound - f.LowerBound) / 2.0);
                    mains.Add((f.FactorName, coded));
                }

                foreach (var f in cat)
                {
                    var labels = f.GetCategoryLevelList();
                    string cur = row.TryGetValue(f.FactorName, out var v) ? (v?.ToString() ?? "") : "";
                    int idx = labels.IndexOf(cur);
                    if (idx < 0) idx = 0;

                    if (labels.Count == 2)
                    {
                        mains.Add((f.FactorName, idx == 0 ? -1.0 : 1.0));
                    }
                    else
                    {
                        for (int j = 0; j < labels.Count - 1; j++)
                        {
                            double val;
                            if (idx == j) val = 1.0;
                            else if (idx == labels.Count - 1) val = -1.0;
                            else val = 0.0;
                            mains.Add(($"{f.FactorName}[{j}]", val));
                        }
                    }
                }

                var terms = new List<double> { 1.0 };
                if (firstRow) termNamesOnce.Add("Intercept");

                foreach (var m in mains)
                {
                    terms.Add(m.Item2);
                    if (firstRow) termNamesOnce.Add(m.Item1);
                }

                if (modelType == "interaction" || modelType == "quadratic")
                {
                    for (int i = 0; i < mains.Count; i++)
                        for (int j = i + 1; j < mains.Count; j++)
                        {
                            terms.Add(mains[i].Item2 * mains[j].Item2);
                            if (firstRow) termNamesOnce.Add($"{mains[i].Item1}:{mains[j].Item1}");
                        }
                }

                if (modelType == "quadratic")
                {
                    for (int i = 0; i < cont.Count; i++)
                    {
                        terms.Add(mains[i].Item2 * mains[i].Item2);
                        if (firstRow) termNamesOnce.Add($"{cont[i].FactorName}²");
                    }
                }

                matrixRows.Add(terms.ToArray());
                firstRow = false;
            }

            int p = matrixRows[0].Length;
            var X = DenseMatrix.Create(n, p, 0.0);
            for (int i = 0; i < n; i++)
                for (int j = 0; j < p; j++)
                    X[i, j] = matrixRows[i][j];

            return (X, termNamesOnce.ToArray());
        }

        // ══════════════════════════════════════════════════════
        // 辅助: 标准正态 CDF (Abramowitz-Stegun 近似)
        // ══════════════════════════════════════════════════════

        private static double StandardNormalCdf(double x)
        {
            // Abramowitz-Stegun 7.1.26
            double t = 1.0 / (1.0 + 0.2316419 * Math.Abs(x));
            double d = 0.3989422804014327 * Math.Exp(-x * x / 2.0);
            double p = d * t * (0.319381530 + t * (-0.356563782 + t * (1.781477937 + t * (-1.821255978 + t * 1.330274429))));
            return x > 0 ? 1 - p : p;
        }
    }
}

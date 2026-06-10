using System;
using System.Collections.Generic;
using System.Linq;
using MathNet.Numerics.LinearAlgebra;
using MathNet.Numerics.LinearAlgebra.Double;
using MathNet.Numerics.Distributions;

namespace MaxChemical.Modules.DOE.OLS.Core
{
    /// <summary>
    /// OLS 求解器 — 基于 MathNet.Numerics QR 分解
    /// 
    /// 对标 statsmodels OLS + Minitab/JMP 精度要求:
    ///   - QR 分解比正规方程数值更稳定
    ///   - 输出完整的 β, SE, t, p, VIF, (XᵀX)⁻¹, σ², R², R²adj, R²pred, PRESS
    ///   - 支持 rank-deficient 检测和自动剔除共线列
    /// 
    /// 使用方式:
    ///   var solver = new OlsSolver();
    ///   var result = solver.Fit(X, y, termNames);
    /// </summary>
    public class OlsSolver
    {
        /// <summary>
        /// 拟合 OLS 模型
        /// </summary>
        /// <param name="X">设计矩阵 (n × p)，第一列应为截距列（全 1）</param>
        /// <param name="y">响应向量 (n × 1)</param>
        /// <param name="termNames">各列名称（含 "Intercept"），长度 = p</param>
        /// <returns>完整拟合结果</returns>
        public OlsFitResult Fit(double[,] X, double[] y, string[] termNames)
        {
            int n = X.GetLength(0);
            int p = X.GetLength(1);

            if (n != y.Length)
                throw new ArgumentException($"X 行数 ({n}) 与 y 长度 ({y.Length}) 不匹配");
            if (p != termNames.Length)
                throw new ArgumentException($"X 列数 ({p}) 与 termNames 长度 ({termNames.Length}) 不匹配");
            if (n < p)
                throw new ArgumentException($"数据量 ({n}) 不足以拟合 {p} 个参数，至少需要 {p} 组数据");

            var result = new OlsFitResult();
            result.N = n;
            result.TermNames = (string[])termNames.Clone();

            // ── 构建 MathNet 矩阵（使用基类类型，避免隐式转换问题） ──
            Matrix<double> matX = DenseMatrix.OfArray(X);
            Vector<double> vecY = DenseVector.OfArray(y);

            // ── Rank 检测：SVD 奇异值比 ──
            var svd = matX.Svd(true);
            double svRatio = svd.S.Minimum() / svd.S.Maximum();
            result.ConditionNumber = svd.S.Maximum() / Math.Max(svd.S.Minimum(), 1e-15);

            if (svRatio < 1e-12)
            {
                // 矩阵接近奇异，尝试识别并剔除共线列
                var keepResult = RemoveCollinearColumns(matX, vecY, termNames, svd);
                if (keepResult == null)
                    throw new InvalidOperationException("设计矩阵严重秩亏，无法拟合模型");

                matX = keepResult.Value.X;
                vecY = keepResult.Value.Y;
                termNames = keepResult.Value.Names;
                p = matX.ColumnCount;
                result.TermNames = termNames;
                result.DroppedTerms = keepResult.Value.Dropped;
            }

            result.P = p;
            int dfResid = n - p;
            result.DfResidual = dfResid;
            result.DfModel = p - 1; // 减去截距

            // ── QR 分解求 β ──
            var qr = matX.QR();
            var beta = qr.Solve(vecY);
            result.Coefficients = beta.ToArray();

            // ── 残差和拟合值 ──
            var yHat = matX * beta;
            var residuals = vecY - yHat;
            result.FittedValues = yHat.ToArray();
            result.Residuals = residuals.ToArray();

            // ── SS 分解 ──
            double yBar = vecY.Average();
            double ssTotal = vecY.Sum(yi => (yi - yBar) * (yi - yBar));
            double ssResid = residuals.DotProduct(residuals);
            double ssModel = ssTotal - ssResid;

            result.SSTotal = ssTotal;
            result.SSResidual = ssResid;
            result.SSModel = ssModel;

            // ── MSE, σ ──
            double mse = dfResid > 0 ? ssResid / dfResid : 0;
            result.MSE = mse;
            result.Sigma = Math.Sqrt(mse);
            result.RMSE = result.Sigma;

            // ── R², R²adj ──
            result.RSquared = ssTotal > 1e-15 ? 1.0 - ssResid / ssTotal : 0;
            result.RSquaredAdj = ssTotal > 1e-15 && dfResid > 0
                ? 1.0 - (ssResid / dfResid) / (ssTotal / (n - 1))
                : 0;

            // ── (XᵀX)⁻¹ ──
            var XtX = matX.TransposeThisAndMultiply(matX);
            Matrix<double> XtXInv;
            try
            {
                XtXInv = XtX.Inverse();
            }
            catch
            {
                // 矩阵不可逆，用伪逆
                XtXInv = matX.PseudoInverse().TransposeAndMultiply(matX.PseudoInverse());
            }
            result.XtXInverse = XtXInv.ToArray();

            // ── (XᵀX)⁻¹ 展开为一维数组（传给 CSharpOlsPredictor） ──
            result.XtxInvFlat = new double[p * p];
            for (int i = 0; i < p; i++)
                for (int j = 0; j < p; j++)
                    result.XtxInvFlat[i * p + j] = XtXInv[i, j];

            // ── Hat matrix 对角线 (leverage) ──
            var hatDiag = new double[n];
            for (int i = 0; i < n; i++)
            {
                var xi = matX.Row(i);
                // h_ii = xᵢᵀ (XᵀX)⁻¹ xᵢ — 显式计算避免 Vector*Matrix*Vector 类型问题
                var temp = XtXInv.Multiply(xi.ToColumnMatrix()).Column(0);
                hatDiag[i] = xi.DotProduct(temp);
            }
            result.Leverage = hatDiag;

            // ── 系数标准误、t 值、p 值 ──
            result.StdErrors = new double[p];
            result.TValues = new double[p];
            result.PValues = new double[p];

            for (int j = 0; j < p; j++)
            {
                double se = Math.Sqrt(mse * XtXInv[j, j]);
                result.StdErrors[j] = se;
                result.TValues[j] = se > 1e-15 ? result.Coefficients[j] / se : 0;
                // 双尾 t 检验
                result.PValues[j] = dfResid > 0
                    ? 2.0 * (1.0 - StudentT.CDF(0, 1, dfResid, Math.Abs(result.TValues[j])))
                    : 1.0;
            }

            // ── t 临界值 (95% CI) ──
            result.TCrit = dfResid > 0
                ? StudentT.InvCDF(0, 1, dfResid, 0.975)
                : 0;

            // ── VIF（去掉截距列后逐列回归） ──
            result.VIF = CalculateVIF(matX, termNames);

            // ── PRESS 和 R²pred（留一交叉验证） ──
            double press = 0;
            for (int i = 0; i < n; i++)
            {
                double hi = hatDiag[i];
                double ei = result.Residuals[i];
                double denom = 1.0 - hi;
                if (Math.Abs(denom) < 1e-15) denom = 1e-15;
                press += (ei / denom) * (ei / denom);
            }
            result.PRESS = press;
            result.RSquaredPred = ssTotal > 1e-15 ? 1.0 - press / ssTotal : 0;

            // ── 模型 F 检验 ──
            double msModel = result.DfModel > 0 ? ssModel / result.DfModel : 0;
            result.FStatistic = mse > 1e-15 ? msModel / mse : 0;
            result.FPValue = result.DfModel > 0 && dfResid > 0
                ? 1.0 - FisherSnedecor.CDF(result.DfModel, dfResid, result.FStatistic)
                : 1.0;

            // ── Adequate Precision (对标 Design-Expert) ──
            result.AdequatePrecision = CalculateAdequatePrecision(yHat.ToArray(), mse, p, n);

            // ── 标准化残差、学生化残差、Cook's D ──
            result.StandardizedResiduals = new double[n];
            result.StudentizedResiduals = new double[n];
            result.CooksDistance = new double[n];

            for (int i = 0; i < n; i++)
            {
                double ei = result.Residuals[i];
                double hi = hatDiag[i];
                double denom = Math.Sqrt(mse * (1.0 - hi));
                result.StandardizedResiduals[i] = denom > 1e-15 ? ei / denom : 0;

                // 外学生化残差 (对标 Minitab "Deleted Residuals")
                double mse_i = dfResid > 1
                    ? (ssResid - ei * ei / (1.0 - hi)) / (dfResid - 1)
                    : mse;
                double denom_i = Math.Sqrt(Math.Max(mse_i * (1.0 - hi), 1e-15));
                result.StudentizedResiduals[i] = denom_i > 1e-15 ? ei / denom_i : 0;

                // Cook's Distance
                double ri = result.StandardizedResiduals[i];
                result.CooksDistance[i] = (ri * ri / p) * (hi / Math.Max(1.0 - hi, 1e-15));
            }

            return result;
        }

        // ═══════════════════════════════════════════════════════
        // VIF 计算 — 对标 Minitab
        // ═══════════════════════════════════════════════════════

        private Dictionary<string, double> CalculateVIF(Matrix<double> X, string[] names)
        {
            var vif = new Dictionary<string, double>();
            int p = X.ColumnCount;

            // 找到截距列
            int interceptIdx = -1;
            for (int j = 0; j < p; j++)
            {
                if (names[j] == "Intercept")
                {
                    interceptIdx = j;
                    break;
                }
            }

            // 去掉截距列后的列索引
            var nonInterceptIdx = Enumerable.Range(0, p).Where(j => j != interceptIdx).ToList();
            if (nonInterceptIdx.Count == 0) return vif;

            // 构建无截距的子矩阵（但加常数列做回归）
            int n = X.RowCount;
            int k = nonInterceptIdx.Count;

            for (int idx = 0; idx < k; idx++)
            {
                int targetCol = nonInterceptIdx[idx];
                var yj = X.Column(targetCol);

                // 构建 X_j：截距 + 其他非截距列（排除 targetCol）
                var otherCols = nonInterceptIdx.Where(c => c != targetCol).ToList();
                var Xj = DenseMatrix.Create(n, otherCols.Count + 1, 0);
                Xj.SetColumn(0, DenseVector.Create(n, 1.0)); // 截距
                for (int c = 0; c < otherCols.Count; c++)
                    Xj.SetColumn(c + 1, X.Column(otherCols[c]));

                // 拟合 xj ~ 1 + 其余x
                try
                {
                    var qr = Xj.QR();
                    var betaJ = qr.Solve(yj);
                    var fittedJ = Xj * betaJ;
                    var residJ = yj - fittedJ;

                    double yjBar = yj.Average();
                    double ssTotalJ = yj.Sum(v => (v - yjBar) * (v - yjBar));
                    double ssResJ = residJ.DotProduct(residJ);

                    double r2j = ssTotalJ > 1e-15 ? 1.0 - ssResJ / ssTotalJ : 0;
                    double vifVal = Math.Abs(1.0 - r2j) > 1e-15 ? 1.0 / (1.0 - r2j) : 999.0;
                    vif[names[targetCol]] = Math.Round(vifVal, 2);
                }
                catch
                {
                    vif[names[targetCol]] = 999.0;
                }
            }

            return vif;
        }

        // ═══════════════════════════════════════════════════════
        // Adequate Precision — 对标 Design-Expert
        // ═══════════════════════════════════════════════════════

        private double CalculateAdequatePrecision(double[] fitted, double mse, int p, int n)
        {
            if (mse < 1e-15 || n == 0) return 0;
            double yMax = fitted.Max();
            double yMin = fitted.Min();
            double range = yMax - yMin;
            if (range < 1e-15) return 0;
            double meanVarPred = mse * p / (double)n;
            return range / Math.Sqrt(meanVarPred);
        }

        // ═══════════════════════════════════════════════════════
        // 共线列剔除
        // ═══════════════════════════════════════════════════════

        private (Matrix<double> X, Vector<double> Y, string[] Names, List<string> Dropped)?
            RemoveCollinearColumns(Matrix<double> X, Vector<double> y, string[] names,
                MathNet.Numerics.LinearAlgebra.Factorization.Svd<double> svd)
        {
            // 通过 SVD 的 V 矩阵找出贡献最小的列并剔除
            double threshold = svd.S.Maximum() * 1e-10;
            int rank = svd.S.Count(s => s > threshold);
            if (rank == X.ColumnCount) return null; // 实际没有共线

            var dropped = new List<string>();

            // 简单策略：从最后一列开始逐列尝试删除，直到 rank 满
            var keepCols = Enumerable.Range(0, X.ColumnCount).ToList();
            while (keepCols.Count > rank && keepCols.Count > 1)
            {
                // 删除最后一个非截距列
                int removeIdx = -1;
                for (int i = keepCols.Count - 1; i >= 0; i--)
                {
                    if (names[keepCols[i]] != "Intercept")
                    {
                        removeIdx = i;
                        break;
                    }
                }
                if (removeIdx < 0) break;

                dropped.Add(names[keepCols[removeIdx]]);
                keepCols.RemoveAt(removeIdx);

                // 检查缩减后的矩阵是否满秩
                var subX = DenseMatrix.Create(X.RowCount, keepCols.Count, (r, c) => X[r, keepCols[c]]);
                var subSvd = subX.Svd(false);
                double minSv = subSvd.S.Minimum();
                double maxSv = subSvd.S.Maximum();
                if (minSv / maxSv > 1e-10) break;
            }

            var finalX = DenseMatrix.Create(X.RowCount, keepCols.Count, (r, c) => X[r, keepCols[c]]);
            var finalNames = keepCols.Select(i => names[i]).ToArray();

            return (finalX, y, finalNames, dropped);
        }
    }

    /// <summary>
    /// OLS 拟合完整结果
    /// </summary>
    public class OlsFitResult
    {
        // ── 维度 ──
        public int N { get; set; }
        public int P { get; set; }
        public int DfModel { get; set; }
        public int DfResidual { get; set; }

        // ── 系数 ──
        public string[] TermNames { get; set; } = Array.Empty<string>();
        public double[] Coefficients { get; set; } = Array.Empty<double>();
        public double[] StdErrors { get; set; } = Array.Empty<double>();
        public double[] TValues { get; set; } = Array.Empty<double>();
        public double[] PValues { get; set; } = Array.Empty<double>();
        public Dictionary<string, double> VIF { get; set; } = new();

        // ── 模型统计 ──
        public double RSquared { get; set; }
        public double RSquaredAdj { get; set; }
        public double RSquaredPred { get; set; }
        public double PRESS { get; set; }
        public double RMSE { get; set; }
        public double Sigma { get; set; }
        public double MSE { get; set; }
        public double AdequatePrecision { get; set; }
        public double ConditionNumber { get; set; }
        public double TCrit { get; set; }

        // ── SS 分解 ──
        public double SSTotal { get; set; }
        public double SSModel { get; set; }
        public double SSResidual { get; set; }

        // ── F 检验 ──
        public double FStatistic { get; set; }
        public double FPValue { get; set; }

        // ── 残差诊断 ──
        public double[] FittedValues { get; set; } = Array.Empty<double>();
        public double[] Residuals { get; set; } = Array.Empty<double>();
        public double[] StandardizedResiduals { get; set; } = Array.Empty<double>();
        public double[] StudentizedResiduals { get; set; } = Array.Empty<double>();
        public double[] CooksDistance { get; set; } = Array.Empty<double>();
        public double[] Leverage { get; set; } = Array.Empty<double>();

        // ── (XᵀX)⁻¹ ──
        public double[,] XtXInverse { get; set; } = new double[0, 0];
        public double[] XtxInvFlat { get; set; } = Array.Empty<double>();

        // ── 剔除项 ──
        public List<string> DroppedTerms { get; set; } = new();

        // ═══ 便利方法 ═══

        /// <summary>获取指定项的系数</summary>
        public double GetCoefficient(string termName)
        {
            int idx = Array.IndexOf(TermNames, termName);
            return idx >= 0 ? Coefficients[idx] : 0;
        }

        /// <summary>获取指定项的 p 值</summary>
        public double GetPValue(string termName)
        {
            int idx = Array.IndexOf(TermNames, termName);
            return idx >= 0 ? PValues[idx] : 1;
        }

        /// <summary>对新数据点做预测（含 CI）</summary>
        public (double predicted, double seMean) Predict(double[] x0)
        {
            if (x0.Length != P)
                throw new ArgumentException($"x0 长度 ({x0.Length}) 与参数数 ({P}) 不匹配");

            double yHat = 0;
            for (int j = 0; j < P; j++)
                yHat += Coefficients[j] * x0[j];

            // SE of mean prediction: σ * sqrt(x₀ᵀ (XᵀX)⁻¹ x₀)
            double h = 0;
            for (int i = 0; i < P; i++)
            {
                double sum = 0;
                for (int j = 0; j < P; j++)
                    sum += XtXInverse[i, j] * x0[j];
                h += x0[i] * sum;
            }
            double seMean = Sigma * Math.Sqrt(Math.Max(h, 0));

            return (yHat, seMean);
        }
    }
}
using MathNet.Numerics;
using MaxChemical.Modules.DOE.OLS.Core;
using System;
using System.Collections.Generic;
using System.Linq;

namespace MaxChemical.Modules.DOE.OLS.Services
{
    /// <summary>
    /// ★ 新增: 多响应 OLS 预测器快照
    /// 
    /// 问题:
    ///   CSharpOlsAnalysisService 是单例，内部只缓存一个 _fitResult，
    ///   切换响应会覆盖上一个。Desirability 多响应优化需要同时持有
    ///   每个响应的独立 OLS 模型做快速预测。
    /// 
    /// 解决:
    ///   本类为每个响应变量保存一份轻量快照（系数 β、项名、编码器），
    ///   不修改 CSharpOlsAnalysisService 的任何状态。
    ///   网格搜索时直接调用 Predict(responseName, contValues, catValues)。
    /// 
    /// 对标 Minitab:
    ///   Minitab Response Optimizer 内部也是为每个响应独立拟合回归模型，
    ///   在同一组因子值下分别预测 ŷ₁, ŷ₂, ... 然后各自计算 d(ŷ)。
    /// </summary>
    public class MultiResponseOlsPredictor
    {
        /// <summary>
        /// 单个响应的模型快照
        /// </summary>
        private class ResponseSnapshot
        {
            public string ResponseName { get; set; } = "";
            public double[] Coefficients { get; set; } = Array.Empty<double>();
            public string[] TermNames { get; set; } = Array.Empty<string>();
            public FactorEncoder Encoder { get; set; } = null!;
            public List<FactorDefinition> Factors { get; set; } = new();
            public double[,]? XtXInverse { get; set; }    // ★ 新增
            public double Sigma { get; set; }              // ★ 新增
            public double TCrit { get; set; } = 2.0;       // ★ 新增
        }

        private readonly Dictionary<string, ResponseSnapshot> _snapshots = new();

        /// <summary>
        /// 添加一个响应的模型快照
        /// </summary>
        /// <param name="responseName">响应变量名</param>
        /// <param name="fitResult">该响应的 OLS 拟合结果</param>
        /// <param name="encoder">编码器（与该响应拟合时用的一致）</param>
        /// <param name="factors">因子定义</param>
        public void AddResponse(string responseName, OlsFitResult fitResult,
        FactorEncoder encoder, List<FactorDefinition> factors)
        {
            _snapshots[responseName] = new ResponseSnapshot
            {
                ResponseName = responseName,
                Coefficients = (double[])fitResult.Coefficients.Clone(),
                TermNames = (string[])fitResult.TermNames.Clone(),
                Encoder = encoder,
                Factors = factors,
                XtXInverse = fitResult.XtXInverse != null
                   ? (double[,])fitResult.XtXInverse.Clone() : null,
               Sigma = fitResult.Sigma,
                TCrit = fitResult.TCrit > 0 ? fitResult.TCrit : 2.0
            };
        }

        /// <summary>
        /// 获取已注册的响应变量名列表
        /// </summary>
        public List<string> ResponseNames => _snapshots.Keys.ToList();

        /// <summary>
        /// 是否包含指定响应的模型
        /// </summary>
        public bool HasResponse(string responseName) => _snapshots.ContainsKey(responseName);

        /// <summary>
        /// ★ 核心: 用指定响应的模型做快速预测
        /// 
        /// 纯数学运算 ŷ = Σβⱼxⱼ，不走 JSON/序列化，
        /// 百万次调用毫秒级完成。
        /// </summary>
        /// <param name="responseName">响应变量名</param>
        /// <param name="continuousValues">连续因子自然值 {"温度": 80, "压力": 5}</param>
        /// <param name="categoricalValues">类别因子值 {"催化剂": "Cat_A"}</param>
        /// <returns>预测值 ŷ</returns>
        public double Predict(string responseName,
            Dictionary<string, double> continuousValues,
            Dictionary<string, string>? categoricalValues = null)
        {
            if (!_snapshots.TryGetValue(responseName, out var snap))
                return 0;

            try
            {
                // 1. 构建编码值字典
                var codedValues = new Dictionary<string, double>();

                foreach (var f in snap.Factors)
                {
                    if (f.IsCategorical)
                    {
                        var levels = f.Levels ?? new List<string>();
                        string currentLevel = categoricalValues != null &&
                            categoricalValues.TryGetValue(f.Name, out var lv) ? lv
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
                                codedValues[colName] = -1.0;
                        }
                    }
                    else
                    {
                        double raw = continuousValues.TryGetValue(f.Name, out var v)
                            ? v : ((f.Lower + f.Upper) / 2.0);
                        double center = (f.Lower + f.Upper) / 2.0;
                        double halfRange = (f.Upper - f.Lower) / 2.0;
                        double coded = halfRange > 1e-15 ? (raw - center) / halfRange : 0;
                        codedValues[f.Name] = coded;
                    }
                }

                // 2. ŷ = Σβⱼxⱼ
                double pred = 0;
                for (int j = 0; j < snap.Coefficients.Length; j++)
                {
                    string name = snap.TermNames[j];
                    double xj;

                    if (name == "Intercept")
                        xj = 1.0;
                    else if (name == "Ct Pt")
                        xj = 0;
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
                            { xj = 0; break; }
                        }
                    }
                    else
                    {
                        xj = codedValues.TryGetValue(name, out var v) ? v : 0;
                    }

                    pred += snap.Coefficients[j] * xj;
                }

                return pred;
            }
            catch { return 0; }
        }


        /// <summary>
        /// ★ 新增: 预测 + 置信区间
        /// 返回 (ŷ, yLower, yUpper)
        /// </summary>
        public (double predicted, double lower, double upper) PredictWithCI(
            string responseName,
            Dictionary<string, double> continuousValues,
            Dictionary<string, string>? categoricalValues = null)
        {
            double pred = Predict(responseName, continuousValues, categoricalValues);

            if (!_snapshots.TryGetValue(responseName, out var snap))
                return (pred, pred, pred);

            if (snap.XtXInverse == null)
                return (pred, pred, pred);  // 没有 XtXInverse，无法算 CI

            try
            {
                // 1. 构建编码值字典（和 Predict 一样）
                var codedValues = new Dictionary<string, double>();
                foreach (var f in snap.Factors)
                {
                    if (f.IsCategorical)
                    {
                        var levels = f.Levels ?? new List<string>();
                        string currentLevel = categoricalValues != null &&
                            categoricalValues.TryGetValue(f.Name, out var lv) ? lv
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
                                codedValues[colName] = -1.0;
                        }
                    }
                    else
                    {
                        double raw = continuousValues.TryGetValue(f.Name, out var v)
                            ? v : ((f.Lower + f.Upper) / 2.0);
                        double center = (f.Lower + f.Upper) / 2.0;
                        double halfRange = (f.Upper - f.Lower) / 2.0;
                        double coded = halfRange > 1e-15 ? (raw - center) / halfRange : 0;
                        codedValues[f.Name] = coded;
                    }
                }

                // 2. 构建 x0 向量
                int pInv = snap.XtXInverse.GetLength(0);
                var x0 = new double[snap.Coefficients.Length];
                for (int j = 0; j < snap.Coefficients.Length; j++)
                {
                    string name = snap.TermNames[j];
                    if (name == "Intercept") x0[j] = 1.0;
                    else if (name == "Ct Pt") x0[j] = 0;
                    else if (name.Contains("²"))
                    {
                        string baseName = name.Replace("²", "");
                        x0[j] = codedValues.TryGetValue(baseName, out var vv) ? vv * vv : 0;
                    }
                    else if (name.Contains(":"))
                    {
                        var parts = name.Split(':');
                        double xj = 1.0;
                        foreach (var p in parts)
                        {
                            if (codedValues.TryGetValue(p, out var vv)) xj *= vv;
                            else { xj = 0; break; }
                        }
                        x0[j] = xj;
                    }
                    else
                    {
                        x0[j] = codedValues.TryGetValue(name, out var vv) ? vv : 0;
                    }
                }

                // 3. 构建预测子向量（排除 Ct Pt，匹配 XtXInverse 维度）
                double[] x0Pred;
                if (pInv == snap.Coefficients.Length)
                {
                    x0Pred = x0;
                }
                else
                {
                    x0Pred = new double[pInv];
                    int ki = 0;
                    for (int j = 0; j < snap.Coefficients.Length; j++)
                    {
                        if (snap.TermNames[j] == "Ct Pt") continue;
                        if (ki < pInv) x0Pred[ki++] = x0[j];
                    }
                }

                // 4. SE = σ * sqrt(x0^T * (X'X)^-1 * x0)
                double xCx = 0;
                for (int i = 0; i < pInv; i++)
                {
                    double s = 0;
                    for (int j = 0; j < pInv; j++)
                        s += snap.XtXInverse[i, j] * x0Pred[j];
                    xCx += x0Pred[i] * s;
                }
                double se = snap.Sigma * Math.Sqrt(Math.Max(xCx, 0));
                double margin = snap.TCrit * se;

                return (pred, pred - margin, pred + margin);
            }
            catch
            {
                return (pred, pred, pred);
            }
        }
    }
}

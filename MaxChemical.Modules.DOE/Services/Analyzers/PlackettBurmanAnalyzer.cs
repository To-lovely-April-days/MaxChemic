using System;
using System.Collections.Generic;
using System.Linq;
using MaxChemical.Modules.DOE.OLS.Core;

namespace MaxChemical.Modules.DOE.OLS.Analyzers
{
    /// <summary>
    /// Plackett-Burman 设计分析器
    /// 
    /// 与 FractionalFactorialAnalyzer 的区别:
    ///   - PB 设计的分辨度固定为 III（所有双因子交互部分混杂到主效应）
    ///   - PB 通常没有足够自由度做 LOF（实验组数 = 因子数 + 1）
    ///   - 重点是快速筛选，不追求模型精度
    ///   - 别名结构更复杂（每个主效应与多个交互混杂）
    /// 
    /// 复用 FractionalFactorialAnalyzer 的核心逻辑，仅调整特有部分。
    /// </summary>
    public class PlackettBurmanAnalyzer
    {
        private readonly FractionalFactorialAnalyzer _inner;

        public PlackettBurmanAnalyzer(
            List<FactorDefinition> factors,
            List<Dictionary<string, object>> data,
            double[] responses,
            string responseName)
        {
            // PB 固定 resolution = 3
            _inner = new FractionalFactorialAnalyzer(factors, data, responses, responseName, resolution: 3);
        }

        public FractionalFactorialResult Analyze()
        {
            var result = _inner.Analyze();

            // PB 特有: 更新别名描述
            foreach (var alias in result.AliasStructure)
            {
                if (!alias.AliasedWith.Contains("无混杂"))
                    alias.AliasedWith = "(PB Res III) " + alias.AliasedWith;
            }

            return result;
        }

        public OlsFitResult? FitResult => _inner.FitResult;
    }
}

using System;
using System.Collections.Generic;
using System.Linq;

namespace MaxChemical.Modules.DOE.OLS.Core
{
    /// <summary>
    /// 因子编码器 — 连续因子和类别因子的统一编码处理
    /// 
    /// 对标 Minitab/JMP 的编码规则:
    ///   - 连续因子: 中心化 + 缩放到 [-1, +1]
    ///   - 类别因子: Sum coding (Effect coding), 不用 Treatment coding
    ///     Sum coding 使类别因子哑变量与截距正交,
    ///     Type III SS 结果与 Minitab 完全一致
    /// 
    /// 使用方式:
    ///   var encoder = new FactorEncoder(factorDefinitions);
    ///   double[] coded = encoder.EncodeRow(rawValues);
    ///   var codingInfo = encoder.GetCodingInfo();
    /// </summary>
    public class FactorEncoder
    {
        private readonly List<FactorDefinition> _factors;
        private readonly Dictionary<string, CodingSpec> _codingSpecs = new();

        public FactorEncoder(List<FactorDefinition> factors)
        {
            _factors = factors ?? throw new ArgumentNullException(nameof(factors));
            BuildCodingSpecs();
        }

        /// <summary>
        /// 编码单行数据：将原始因子值转换为编码值
        /// </summary>
        /// <param name="rawValues">原始因子值 { "温度": 120, "催化剂": "B" }</param>
        /// <returns>编码后的值数组（按因子定义顺序，类别因子展开为多列）</returns>
        public double[] EncodeRow(Dictionary<string, object> rawValues)
        {
            var coded = new List<double>();
            foreach (var f in _factors)
            {
                if (!rawValues.TryGetValue(f.Name, out var val))
                    throw new ArgumentException($"缺少因子值: {f.Name}");

                var spec = _codingSpecs[f.Name];

                if (f.IsCategorical)
                {
                    // Sum coding: k-1 列
                    coded.AddRange(SumCodeValue(val.ToString()!, spec));
                }
                else
                {
                    // 连续编码 [-1, +1]
                    double raw = Convert.ToDouble(val);
                    double codedVal = spec.HalfRange > 1e-15
                        ? (raw - spec.Center) / spec.HalfRange
                        : 0;
                    coded.Add(codedVal);
                }
            }
            return coded.ToArray();
        }

        /// <summary>
        /// 获取编码后的列名列表（类别因子展开为多列）
        /// </summary>
        public List<string> GetCodedColumnNames()
        {
            var names = new List<string>();
            foreach (var f in _factors)
            {
                if (f.IsCategorical)
                {
                    var spec = _codingSpecs[f.Name];
                    // Sum coding: 前 k-1 个水平各一列
                    for (int i = 0; i < spec.Levels!.Count - 1; i++)
                        names.Add($"{f.Name}[{spec.Levels[i]}]");
                }
                else
                {
                    names.Add(f.Name);
                }
            }
            return names;
        }

        /// <summary>
        /// 获取连续因子的编码列名（不含类别因子哑变量，用于构建交互项和二次项）
        /// </summary>
        public List<string> GetContinuousCodedNames()
        {
            return _factors.Where(f => !f.IsCategorical).Select(f => f.Name).ToList();
        }

        /// <summary>
        /// 获取类别因子名列表
        /// </summary>
        public List<string> GetCategoricalNames()
        {
            return _factors.Where(f => f.IsCategorical).Select(f => f.Name).ToList();
        }

        /// <summary>
        /// 获取编码信息（传给 CSharpOlsPredictor 和输出 CodingInfo）
        /// </summary>
        public Dictionary<string, CodingInfoExport> GetCodingInfo()
        {
            var info = new Dictionary<string, CodingInfoExport>();
            foreach (var f in _factors)
            {
                var spec = _codingSpecs[f.Name];
                info[f.Name] = new CodingInfoExport
                {
                    Type = f.IsCategorical ? "categorical" : "continuous",
                    Center = spec.Center,
                    HalfRange = spec.HalfRange,
                    Levels = spec.Levels
                };
            }
            return info;
        }

        /// <summary>
        /// 编码整个数据集
        /// </summary>
        /// <param name="data">每组实验的因子值</param>
        /// <returns>编码后的矩阵（不含截距列）</returns>
        public double[,] EncodeAll(List<Dictionary<string, object>> data)
        {
            int n = data.Count;
            int codedCols = GetCodedColumnCount();
            var matrix = new double[n, codedCols];

            for (int i = 0; i < n; i++)
            {
                var row = EncodeRow(data[i]);
                for (int j = 0; j < codedCols; j++)
                    matrix[i, j] = row[j];
            }
            return matrix;
        }

        /// <summary>
        /// 获取编码后的总列数
        /// </summary>
        public int GetCodedColumnCount()
        {
            int cols = 0;
            foreach (var f in _factors)
            {
                if (f.IsCategorical)
                    cols += _codingSpecs[f.Name].Levels!.Count - 1;
                else
                    cols += 1;
            }
            return cols;
        }

        /// <summary>
        /// 从编码值反解原始值
        /// </summary>
        public double DecodeToOriginal(string factorName, double codedValue)
        {
            var spec = _codingSpecs[factorName];
            return spec.Center + codedValue * spec.HalfRange;
        }

        // ═══════════════════════════════════════════════════════
        // 内部方法
        // ═══════════════════════════════════════════════════════

        private void BuildCodingSpecs()
        {
            foreach (var f in _factors)
            {
                if (f.IsCategorical)
                {
                    _codingSpecs[f.Name] = new CodingSpec
                    {
                        IsCategorical = true,
                        Levels = f.Levels?.ToList() ?? new List<string>(),
                        Center = 0,
                        HalfRange = 1
                    };
                }
                else
                {
                    double center = (f.Lower + f.Upper) / 2.0;
                    double halfRange = (f.Upper - f.Lower) / 2.0;
                    if (halfRange < 1e-15) halfRange = 1.0;

                    _codingSpecs[f.Name] = new CodingSpec
                    {
                        IsCategorical = false,
                        Center = center,
                        HalfRange = halfRange
                    };
                }
            }
        }

        /// <summary>
        /// Sum coding (Effect coding) — 对标 Minitab
        /// 
        /// 水平 a, b, c (k=3) → 2 列:
        ///   a → [+1, 0]
        ///   b → [0, +1]  
        ///   c → [-1, -1]   ← 最后一个水平用 -1
        /// 
        /// 与 Treatment coding (0/1) 不同:
        ///   - Sum coding 使各水平效应之和 = 0
        ///   - 截距 = 总体均值（不是参考水平均值）
        ///   - Type III SS 与 Minitab 一致
        /// </summary>
        private double[] SumCodeValue(string value, CodingSpec spec)
        {
            var levels = spec.Levels!;
            int k = levels.Count;
            var coded = new double[k - 1];

            int levelIdx = levels.IndexOf(value);
            if (levelIdx < 0)
                throw new ArgumentException($"未知水平值: {value}，已知水平: {string.Join(", ", levels)}");

            if (levelIdx < k - 1)
            {
                coded[levelIdx] = 1.0;     // ← 把 -1.0 改回 1.0
            }
            else
            {
                for (int i = 0; i < k - 1; i++)
                    coded[i] = -1.0;       // ← 把 1.0 改回 -1.0
            }

            return coded;
        }

        // ═══════════════════════════════════════════════════════
        // 内部类
        // ═══════════════════════════════════════════════════════

        private class CodingSpec
        {
            public bool IsCategorical { get; set; }
            public double Center { get; set; }
            public double HalfRange { get; set; }
            public List<string>? Levels { get; set; }
        }
    }

    /// <summary>
    /// 因子定义
    /// </summary>
    public class FactorDefinition
    {
        public string Name { get; set; } = string.Empty;
        public bool IsCategorical { get; set; }

        // 连续因子
        public double Lower { get; set; }
        public double Upper { get; set; }

        // 类别因子
        public List<string>? Levels { get; set; }

        /// <summary>从 DOEFactor 模型转换</summary>
        public static FactorDefinition FromDOEFactor(string name, string factorType,
            double lower = 0, double upper = 1, List<string>? levels = null)
        {
            return new FactorDefinition
            {
                Name = name,
                IsCategorical = factorType?.ToLower() == "categorical",
                Lower = lower,
                Upper = upper,
                Levels = levels
            };
        }
    }

    /// <summary>
    /// 编码信息导出（对应 CodingInfoItem）
    /// </summary>
    public class CodingInfoExport
    {
        public string Type { get; set; } = "continuous";
        public double Center { get; set; }
        public double HalfRange { get; set; }
        public List<string>? Levels { get; set; }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;

namespace MaxChemical.Modules.DOE.OLS.Core
{
    /// <summary>
    /// 设计矩阵构建器 — 根据因子编码和模型类型，组装完整的 X 矩阵
    /// 
    /// 职责:
    ///   1. 从 FactorEncoder 获取编码后的数据列
    ///   2. 根据 ModelTerms 生成交互项列、二次项列
    ///   3. 可选添加 Ct Pt 虚拟变量列（弯曲检验）
    ///   4. 输出完整 X 矩阵 + 列名数组
    /// 
    /// 支持的项类型:
    ///   - 主效应: "温度", "压力", "催化剂[A]", "催化剂[B]"
    ///   - 交互项: "温度:压力", "温度:催化剂[A]"
    ///   - 二次项: "温度²", "压力²"
    ///   - 弯曲项: "Ct Pt"
    /// </summary>
    public class DesignMatrixBuilder
    {
        private readonly FactorEncoder _encoder;
        private readonly int _n;
        private readonly double[,] _codedData; // n × codedCols (不含截距)
        private readonly List<string> _codedColNames;

        /// <summary>
        /// 构造函数
        /// </summary>
        /// <param name="encoder">因子编码器</param>
        /// <param name="rawData">原始因子数据</param>
        public DesignMatrixBuilder(FactorEncoder encoder, List<Dictionary<string, object>> rawData)
        {
            _encoder = encoder ?? throw new ArgumentNullException(nameof(encoder));
            _n = rawData.Count;
            _codedData = encoder.EncodeAll(rawData);
            _codedColNames = encoder.GetCodedColumnNames();
        }

        /// <summary>
        /// 构建设计矩阵
        /// </summary>
        /// <param name="terms">模型项列表，如 ["温度", "压力", "温度:压力", "温度²"]</param>
        /// <param name="centerPointMask">中心点掩码（可选，用于生成 Ct Pt 列）</param>
        /// <returns>(X矩阵, 列名数组) — X 第一列是截距</returns>
        public (double[,] X, string[] columnNames) Build(List<ModelTerm> terms, bool[]? centerPointMask = null)
        {
            var columns = new List<(string name, double[] values)>();

            // 截距列
            columns.Add(("Intercept", Enumerable.Repeat(1.0, _n).ToArray()));

            foreach (var term in terms)
            {
                switch (term.Type)
                {
                    case TermType.MainEffect:
                        AddMainEffect(columns, term);
                        break;
                    case TermType.Interaction:
                        AddInteraction(columns, term);
                        break;
                    case TermType.Quadratic:
                        AddQuadratic(columns, term);
                        break;
                    case TermType.CenterPoint:
                        if (centerPointMask != null)
                            AddCenterPoint(columns, centerPointMask);
                        break;
                }
            }

            // 组装矩阵
            int p = columns.Count;
            var X = new double[_n, p];
            var names = new string[p];

            for (int j = 0; j < p; j++)
            {
                names[j] = columns[j].name;
                for (int i = 0; i < _n; i++)
                    X[i, j] = columns[j].values[i];
            }

            return (X, names);
        }

        /// <summary>
        /// 自动生成线性模型的项列表 (y = β₀ + Σβᵢxᵢ)
        /// </summary>
        public static List<ModelTerm> LinearTerms(FactorEncoder encoder)
        {
            var terms = new List<ModelTerm>();
            var contNames = encoder.GetContinuousCodedNames();
            var catNames = encoder.GetCategoricalNames();

            foreach (var name in contNames)
                terms.Add(ModelTerm.Main(name));
            foreach (var name in catNames)
                terms.Add(ModelTerm.Main(name));

            return terms;
        }

        /// <summary>
        /// 自动生成交互模型的项列表 (y = β₀ + Σβᵢxᵢ + Σβᵢⱼxᵢxⱼ)
        /// 只含二因子交互
        /// </summary>
        public static List<ModelTerm> InteractionTerms(FactorEncoder encoder)
        {
            var terms = LinearTerms(encoder);
            var allNames = encoder.GetContinuousCodedNames()
                .Concat(encoder.GetCategoricalNames()).ToList();

            for (int i = 0; i < allNames.Count; i++)
                for (int j = i + 1; j < allNames.Count; j++)
                    terms.Add(ModelTerm.Interact(allNames[i], allNames[j]));

            return terms;
        }

        /// <summary>
        /// 全因子交互模型 — 包含所有阶交互（二因子+三因子+...+k因子）
        /// 对标 Minitab 全因子设计的默认模型：2^k 设计可估计所有 2^k-1 个效应
        /// </summary>
        public static List<ModelTerm> FullFactorialInteractionTerms(FactorEncoder encoder)
        {
            var terms = LinearTerms(encoder);
            var allNames = encoder.GetContinuousCodedNames()
                .Concat(encoder.GetCategoricalNames()).ToList();

            // 生成所有 2~k 阶交互
            for (int order = 2; order <= allNames.Count; order++)
            {
                foreach (var combo in Combinations(allNames, order))
                {
                    terms.Add(new ModelTerm
                    {
                        Type = TermType.Interaction,
                        Factors = combo
                    });
                }
            }

            return terms;
        }
        /// <summary>
        /// 全因子/部分因子交互模型 — 包含从 1 阶(主效应)到 maxOrder 阶的所有交互项
        /// 对标 Minitab "模型中包含项的阶数(I)" 下拉框
        /// </summary>
        public static List<ModelTerm> FactorialTermsUpToOrder(FactorEncoder encoder, int maxOrder)
        {
            var terms = LinearTerms(encoder);
            var allNames = encoder.GetContinuousCodedNames()
                .Concat(encoder.GetCategoricalNames()).ToList();

            int upperBound = Math.Min(maxOrder, allNames.Count);
            for (int order = 2; order <= upperBound; order++)
            {
                foreach (var combo in Combinations(allNames, order))
                {
                    terms.Add(new ModelTerm
                    {
                        Type = TermType.Interaction,
                        Factors = combo
                    });
                }
            }

            return terms;
        }
        /// <summary>
        /// 组合生成器: 从列表中选 k 个元素的所有组合
        /// </summary>
        private static IEnumerable<List<string>> Combinations(List<string> source, int k)
        {
            if (k == 0) { yield return new List<string>(); yield break; }
            for (int i = 0; i <= source.Count - k; i++)
            {
                foreach (var rest in Combinations(source.Skip(i + 1).ToList(), k - 1))
                {
                    var combo = new List<string> { source[i] };
                    combo.AddRange(rest);
                    yield return combo;
                }
            }
        }

        /// <summary>
        /// 自动生成二阶模型的项列表 (y = β₀ + Σβᵢxᵢ + Σβᵢᵢxᵢ² + Σβᵢⱼxᵢxⱼ)
        /// </summary>
        public static List<ModelTerm> QuadraticTerms(FactorEncoder encoder)
        {
            var terms = InteractionTerms(encoder);
            var contNames = encoder.GetContinuousCodedNames();

            // 二次项只能是连续因子
            foreach (var name in contNames)
                terms.Add(ModelTerm.Quad(name));

            return terms;
        }

        // ═══════════════════════════════════════════════════════
        // 列构建
        // ═══════════════════════════════════════════════════════

        private void AddMainEffect(List<(string, double[])> columns, ModelTerm term)
        {
            string factorName = term.Factors[0];
            var matchCols = FindCodedColumns(factorName);

            foreach (var (colName, colIdx) in matchCols)
            {
                var values = new double[_n];
                for (int i = 0; i < _n; i++)
                    values[i] = _codedData[i, colIdx];
                columns.Add((colName, values));
            }
        }

        private void AddInteraction(List<(string, double[])> columns, ModelTerm term)
        {
            if (term.Factors.Count == 2)
            {
                // 二因子交互（最常见，优化路径）
                var cols1 = FindCodedColumns(term.Factors[0]);
                var cols2 = FindCodedColumns(term.Factors[1]);

                foreach (var (name1, idx1) in cols1)
                {
                    foreach (var (name2, idx2) in cols2)
                    {
                        var values = new double[_n];
                        for (int i = 0; i < _n; i++)
                            values[i] = _codedData[i, idx1] * _codedData[i, idx2];
                        columns.Add(($"{name1}:{name2}", values));
                    }
                }
            }
            else
            {
                // 多因子交互（3因子及以上）: 各因子编码列的笛卡尔积再逐元素相乘
                var allFactorCols = term.Factors.Select(f => FindCodedColumns(f)).ToList();

                // 递归生成笛卡尔积
                var combos = CartesianProduct(allFactorCols);
                foreach (var combo in combos)
                {
                    var values = new double[_n];
                    for (int i = 0; i < _n; i++)
                    {
                        double product = 1.0;
                        foreach (var (_, colIdx) in combo)
                            product *= _codedData[i, colIdx];
                        values[i] = product;
                    }
                    string interName = string.Join(":", combo.Select(c => c.name));
                    columns.Add((interName, values));
                }
            }
        }

        /// <summary>多个列表的笛卡尔积</summary>
        private static List<List<(string name, int index)>> CartesianProduct(
            List<List<(string name, int index)>> lists)
        {
            var result = new List<List<(string, int)>> { new() };
            foreach (var list in lists)
            {
                var newResult = new List<List<(string, int)>>();
                foreach (var existing in result)
                    foreach (var item in list)
                    {
                        var newList = new List<(string, int)>(existing) { item };
                        newResult.Add(newList);
                    }
                result = newResult;
            }
            return result;
        }

        private void AddQuadratic(List<(string, double[])> columns, ModelTerm term)
        {
            string factorName = term.Factors[0];
            var matchCols = FindCodedColumns(factorName);

            // 二次项只对连续因子有意义（应该只匹配 1 列）
            foreach (var (colName, colIdx) in matchCols)
            {
                var values = new double[_n];
                for (int i = 0; i < _n; i++)
                    values[i] = _codedData[i, colIdx] * _codedData[i, colIdx];
                columns.Add(($"{colName}²", values));
            }
        }

        private void AddCenterPoint(List<(string, double[])> columns, bool[] mask)
        {
            var values = new double[_n];
            for (int i = 0; i < _n; i++)
                values[i] = mask[i] ? 1.0 : 0.0;
            columns.Add(("Ct Pt", values));
        }

        /// <summary>
        /// 查找因子名对应的编码列索引
        /// 连续因子: 1 列（名称 = 因子名）
        /// 类别因子: k-1 列（名称 = "因子名[水平]"）
        /// </summary>
        private List<(string name, int index)> FindCodedColumns(string factorName)
        {
            var result = new List<(string, int)>();
            for (int j = 0; j < _codedColNames.Count; j++)
            {
                // 精确匹配连续因子
                if (_codedColNames[j] == factorName)
                {
                    result.Add((_codedColNames[j], j));
                }
                // 匹配类别因子的哑变量列: "催化剂[A]", "催化剂[B]"
                else if (_codedColNames[j].StartsWith(factorName + "["))
                {
                    result.Add((_codedColNames[j], j));
                }
            }

            if (result.Count == 0)
                throw new ArgumentException($"未找到因子 '{factorName}' 对应的编码列");

            return result;
        }
    }

    /// <summary>
    /// 模型项类型
    /// </summary>
    public enum TermType
    {
        MainEffect,   // 主效应: 温度
        Interaction,   // 交互: 温度:压力
        Quadratic,     // 二次: 温度²
        CenterPoint    // 弯曲: Ct Pt
    }

    /// <summary>
    /// 模型项定义
    /// </summary>
    public class ModelTerm
    {
        public TermType Type { get; set; }
        public List<string> Factors { get; set; } = new();

        /// <summary>项的显示名称</summary>
        public string DisplayName
        {
            get
            {
                return Type switch
                {
                    TermType.MainEffect => Factors[0],
                    TermType.Interaction => string.Join("×", Factors),
                    TermType.Quadratic => $"{Factors[0]}²",
                    TermType.CenterPoint => "Ct Pt",
                    _ => string.Join(",", Factors)
                };
            }
        }

        // ═══ 工厂方法 ═══

        public static ModelTerm Main(string factor) => new()
        {
            Type = TermType.MainEffect,
            Factors = new List<string> { factor }
        };

        public static ModelTerm Interact(string f1, string f2) => new()
        {
            Type = TermType.Interaction,
            Factors = new List<string> { f1, f2 }
        };

        public static ModelTerm Quad(string factor) => new()
        {
            Type = TermType.Quadratic,
            Factors = new List<string> { factor }
        };

        public static ModelTerm CtPt() => new()
        {
            Type = TermType.CenterPoint,
            Factors = new List<string> { "Ct Pt" }
        };

     
    }
}
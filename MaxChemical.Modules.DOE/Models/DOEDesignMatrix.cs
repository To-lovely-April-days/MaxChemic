using System.Collections.Generic;
using System.Linq;

namespace MaxChemical.Modules.DOE.Models
{
    /// <summary>
    /// DOE 参数矩阵 — 由设计方法生成的完整实验参数组合表
    /// </summary>
    public class DOEDesignMatrix
    {
        /// <summary>
        /// 因子名称列表（列头）
        /// </summary>
        public List<string> FactorNames { get; set; } = new();

        /// <summary>
        /// 参数矩阵行（每行是一组实验的因子值）
        /// </summary>
        public List<Dictionary<string, double>> Rows { get; set; } = new();

        /// <summary>
        /// 总实验组数
        /// </summary>
        public int RunCount => Rows.Count;

        /// <summary>
        /// 因子数量
        /// </summary>
        public int FactorCount => FactorNames.Count;

        /// <summary>
        /// 矩阵是否为空
        /// </summary>
        public bool IsEmpty => Rows.Count == 0;

        /// <summary>
        /// 获取指定组的因子值
        /// </summary>
        public Dictionary<string, double>? GetRun(int index)
        {
            return index >= 0 && index < Rows.Count ? Rows[index] : null;
        }

        /// <summary>
        /// 添加中心点实验
        /// </summary>
        public void AddCenterPoint(Dictionary<string, double> centerValues)
        {
            Rows.Add(new Dictionary<string, double>(centerValues));
        }

        /// <summary>
        /// 随机化排列
        /// </summary>
        public void Randomize(int? seed = null)
        {
            var rng = seed.HasValue ? new System.Random(seed.Value) : new System.Random();
            var n = Rows.Count;
            for (int i = n - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                (Rows[i], Rows[j]) = (Rows[j], Rows[i]);
            }
        }

        /// <summary>
        /// 转为 JSON 字符串（供 Python 调用）
        /// </summary>
        public string ToJson()
        {
            return System.Text.Json.JsonSerializer.Serialize(Rows);
        }

        /// <summary>
        /// 从 JSON 字符串还原
        /// </summary>
        public static DOEDesignMatrix FromJson(string json, List<string> factorNames)
        {
            var rows = System.Text.Json.JsonSerializer.Deserialize<List<Dictionary<string, double>>>(json)
                       ?? new List<Dictionary<string, double>>();
            return new DOEDesignMatrix { FactorNames = factorNames, Rows = rows };
        }
    }
}

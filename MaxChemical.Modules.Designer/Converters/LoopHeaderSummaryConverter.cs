using System;
using System.Globalization;
using System.Windows.Data;
using MaxChemical.Modules.Designer.Models;

namespace MaxChemical.Modules.Designer.Converters
{
    /// <summary>
    /// 生成循环节点头部摘要：
    /// - FixedCount: 执行 {LoopCount} 次 · {Steps} 个步骤
    /// - Conditional: 条件：{Condition 或 左 操作符 右} · {Steps} 个步骤
    /// - Infinite: 无限循环 · {Steps} 个步骤
    /// </summary>
    public class LoopHeaderSummaryConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            // values: [0]=LoopType, [1]=LoopCount, [2]=Steps, [3]=Left, [4]=Operator, [5]=Right, [6]=Condition
            var loopType = ReadLoopType(values.Length > 0 ? values[0] : null);
            int loopCount = SafeToInt(values.Length > 1 ? values[1] : null, 0);
            int steps = SafeToInt(values.Length > 2 ? values[2] : null, 0);
            string left = (values.Length > 3 ? values[3]?.ToString() : null) ?? "A";
            var op = ReadOperator(values.Length > 4 ? values[4] : null);
            string right = (values.Length > 5 ? values[5]?.ToString() : null) ?? "B";
            string condition = (values.Length > 6 ? values[6]?.ToString() : null) ?? string.Empty;

            return loopType switch
            {
                LoopType.FixedCount => $"执行 {loopCount} 次 · {steps} 个步骤",
                LoopType.Conditional => BuildConditionalSummary(left, op, right, condition, steps),
                LoopType.Infinite => $"无限循环 · {steps} 个步骤",
                _ => $"执行 {loopCount} 次 · {steps} 个步骤"
            };
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
            => throw new NotSupportedException();

        private static string BuildConditionalSummary(string left, ComparisonOperator? op, string right, string condition, int steps)
        {
            // 优先使用已有 Condition（由属性面板生成），否则用 Left/Op/Right 组装
            string cond = !string.IsNullOrWhiteSpace(condition)
                ? condition
                : $"{left} {OperatorSymbol(op)} {right}";

            return $"条件：{cond} · {steps} 个步骤";
        }

        private static string OperatorSymbol(ComparisonOperator? op)
        {
            return op switch
            {
                ComparisonOperator.Equal => "==",
                ComparisonOperator.NotEqual => "!=",
                ComparisonOperator.GreaterThan => ">",
                ComparisonOperator.GreaterThanOrEqual => ">=",
                ComparisonOperator.LessThan => "<",
                ComparisonOperator.LessThanOrEqual => "<=",
                ComparisonOperator.Contains => "包含",
                ComparisonOperator.NotContains => "不包含",
                _ => "=="
            };
        }

        private static LoopType ReadLoopType(object val)
        {
            if (val is LoopType lt) return lt;
            if (val is string s && Enum.TryParse<LoopType>(s, out var parsed)) return parsed;
            try
            {
                var i = System.Convert.ToInt32(val, CultureInfo.InvariantCulture);
                return (LoopType)Enum.ToObject(typeof(LoopType), i);
            }
            catch { }
            return LoopType.FixedCount;
        }

        private static ComparisonOperator? ReadOperator(object val)
        {
            if (val is ComparisonOperator co) return co;
            if (val is string s && Enum.TryParse<ComparisonOperator>(s, out var parsed)) return parsed;
            try
            {
                var i = System.Convert.ToInt32(val, CultureInfo.InvariantCulture);
                return (ComparisonOperator)Enum.ToObject(typeof(ComparisonOperator), i);
            }
            catch { }
            return null;
        }

        private static int SafeToInt(object val, int @default)
        {
            try
            {
                if (val is int i) return i;
                if (val is string s && int.TryParse(s, out var j)) return j;
                if (val != null) return System.Convert.ToInt32(val, CultureInfo.InvariantCulture);
            }
            catch { }
            return @default;
        }
    }
}
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Linq;

namespace FlowEngine.Core
{
    /// <summary>
    /// 表达式解析器
    /// </summary>
    public class ExpressionEvaluator
    {
        private readonly Dictionary<string, object> _variables;

        public ExpressionEvaluator(Dictionary<string, object> variables = null)
        {
            _variables = variables ?? new Dictionary<string, object>();
        }

        /// <summary>
        /// 计算表达式
        /// </summary>
        public object Evaluate(string expression)
        {
            if (string.IsNullOrWhiteSpace(expression))
                return null;

            try
            {
                // 替换变量
                var processedExpression = ReplaceVariables(expression);

                // 尝试解析为数字
                if (double.TryParse(processedExpression, out var number))
                    return number;

                // 尝试解析为布尔值
                if (bool.TryParse(processedExpression, out var boolean))
                    return boolean;

                // 处理比较表达式
                if (ContainsComparisonOperator(processedExpression))
                    return EvaluateComparison(processedExpression);

                // 处理算术表达式
                if (ContainsArithmeticOperator(processedExpression))
                    return EvaluateArithmetic(processedExpression);

                return processedExpression;
            }
            catch (Exception)
            {
                return expression;
            }
        }

        /// <summary>
        /// 计算布尔表达式
        /// </summary>
        public bool EvaluateBoolean(string expression)
        {
            var result = Evaluate(expression);

            if (result is bool boolResult)
                return boolResult;

            if (result is string strResult)
                return !string.IsNullOrEmpty(strResult) && strResult.ToLower() != "false";

            if (result is double numResult)
                return Math.Abs(numResult) > double.Epsilon;

            return false;
        }

        /// <summary>
        /// 计算数值表达式
        /// </summary>
        public double EvaluateNumber(string expression)
        {
            var result = Evaluate(expression);
            return Convert.ToDouble(result);
        }

        /// <summary>
        /// 计算字符串表达式
        /// </summary>
        public string EvaluateString(string expression)
        {
            var result = Evaluate(expression);
            return result?.ToString() ?? string.Empty;
        }

        /// <summary>
        /// 替换变量
        /// </summary>
        private string ReplaceVariables(string expression)
        {
            var pattern = @"\{([^}]+)\}";
            return Regex.Replace(expression, pattern, match =>
            {
                var variableName = match.Groups[1].Value;
                if (_variables.TryGetValue(variableName, out var value))
                {
                    return value?.ToString() ?? "null";
                }
                return match.Value;
            });
        }

        /// <summary>
        /// 检查是否包含比较运算符
        /// </summary>
        private bool ContainsComparisonOperator(string expression)
        {
            return expression.Contains("==") || expression.Contains("!=") ||
                   expression.Contains(">=") || expression.Contains("<=") ||
                   expression.Contains(">") || expression.Contains("<");
        }

        /// <summary>
        /// 检查是否包含算术运算符
        /// </summary>
        private bool ContainsArithmeticOperator(string expression)
        {
            return expression.Contains("+") || expression.Contains("-") ||
                   expression.Contains("*") || expression.Contains("/");
        }

        /// <summary>
        /// 计算比较表达式
        /// </summary>
        private bool EvaluateComparison(string expression)
        {
            var operators = new[] { ">=", "<=", "==", "!=", ">", "<" };

            foreach (var op in operators)
            {
                if (expression.Contains(op))
                {
                    var parts = expression.Split(new[] { op }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == 2)
                    {
                        var left = Evaluate(parts[0].Trim());
                        var right = Evaluate(parts[1].Trim());
                        return CompareValues(left, right, op);
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// 计算算术表达式
        /// </summary>
        private double EvaluateArithmetic(string expression)
        {
            expression = expression.Replace(" ", "");

            if (expression.Contains("+"))
            {
                var parts = expression.Split('+');
                return parts.Sum(part => EvaluateNumber(part));
            }

            if (expression.Contains("-"))
            {
                var parts = expression.Split('-');
                if (parts.Length == 2)
                {
                    return EvaluateNumber(parts[0]) - EvaluateNumber(parts[1]);
                }
            }

            if (expression.Contains("*"))
            {
                var parts = expression.Split('*');
                double result = 1;
                foreach (var part in parts)
                {
                    result *= EvaluateNumber(part);
                }
                return result;
            }

            if (expression.Contains("/"))
            {
                var parts = expression.Split('/');
                if (parts.Length == 2)
                {
                    var divisor = EvaluateNumber(parts[1]);
                    if (Math.Abs(divisor) < double.Epsilon)
                        throw new DivideByZeroException("除数不能为零");
                    return EvaluateNumber(parts[0]) / divisor;
                }
            }

            return Convert.ToDouble(expression);
        }

        /// <summary>
        /// 比较值
        /// </summary>
        private bool CompareValues(object left, object right, string op)
        {
            try
            {
                if (double.TryParse(left?.ToString(), out var leftNum) &&
                    double.TryParse(right?.ToString(), out var rightNum))
                {
                    return op switch
                    {
                        "==" => Math.Abs(leftNum - rightNum) < double.Epsilon,
                        "!=" => Math.Abs(leftNum - rightNum) >= double.Epsilon,
                        ">" => leftNum > rightNum,
                        "<" => leftNum < rightNum,
                        ">=" => leftNum >= rightNum,
                        "<=" => leftNum <= rightNum,
                        _ => false
                    };
                }

                var leftStr = left?.ToString() ?? "";
                var rightStr = right?.ToString() ?? "";
                var comparison = string.Compare(leftStr, rightStr, StringComparison.OrdinalIgnoreCase);

                return op switch
                {
                    "==" => comparison == 0,
                    "!=" => comparison != 0,
                    ">" => comparison > 0,
                    "<" => comparison < 0,
                    ">=" => comparison >= 0,
                    "<=" => comparison <= 0,
                    _ => false
                };
            }
            catch
            {
                return false;
            }
        }
    }
}
// MaxChemical.Modules.Designer.Services.Execution.FlowExecutionContext.cs
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using MaxChemical.Modules.Designer.Models;

namespace MaxChemical.Modules.Designer.Services.Execution
{
    public class FlowExecutionContext
    {
        /// <summary>
        /// 执行变量存储
        /// </summary>
        public ConcurrentDictionary<string, object> Variables { get; } = new ConcurrentDictionary<string, object>();

        /// <summary>
        /// 设备状态存储
        /// </summary>
        public ConcurrentDictionary<string, object> DeviceStates { get; } = new ConcurrentDictionary<string, object>();

        /// <summary>
        /// 执行历史
        /// </summary>
        public List<NodeExecutionRecord> ExecutionHistory { get; } = new List<NodeExecutionRecord>();

        /// <summary>
        /// 并行执行任务跟踪
        /// </summary>
        public ConcurrentDictionary<string, Task> ParallelTasks { get; } = new ConcurrentDictionary<string, Task>();

        /// <summary>
        /// 循环计数器
        /// </summary>
        public ConcurrentDictionary<string, int> LoopCounters { get; } = new ConcurrentDictionary<string, int>();

        /// <summary>
        /// 设置变量
        /// </summary>
        public void SetVariable(string name, object value)
        {
            Variables.AddOrUpdate(name, value, (key, oldValue) => value);
        }

        /// <summary>
        /// 获取变量
        /// </summary>
        public T GetVariable<T>(string name, T defaultValue = default)
        {
            if (Variables.TryGetValue(name, out var value) && value is T typedValue)
            {
                return typedValue;
            }
            return defaultValue;
        }

        /// <summary>
        /// 评估条件表达式
        /// </summary>
        public bool EvaluateCondition(string condition)
        {
            // 简单的条件评估实现
            // 实际项目中可能需要更复杂的表达式引擎
            try
            {
                // 替换变量
                var evaluatedCondition = ReplaceVariables(condition);

                // 简单的比较操作
                if (evaluatedCondition.Contains("=="))
                {
                    var parts = evaluatedCondition.Split("==");
                    if (parts.Length == 2)
                    {
                        var left = parts[0].Trim().Trim('"');
                        var right = parts[1].Trim().Trim('"');
                        return left.Equals(right, StringComparison.OrdinalIgnoreCase);
                    }
                }
                else if (evaluatedCondition.Contains("!="))
                {
                    var parts = evaluatedCondition.Split("!=");
                    if (parts.Length == 2)
                    {
                        var left = parts[0].Trim().Trim('"');
                        var right = parts[1].Trim().Trim('"');
                        return !left.Equals(right, StringComparison.OrdinalIgnoreCase);
                    }
                }
                else if (evaluatedCondition.Contains(">"))
                {
                    var parts = evaluatedCondition.Split(">");
                    if (parts.Length == 2 &&
                        double.TryParse(parts[0].Trim(), out var left) &&
                        double.TryParse(parts[1].Trim(), out var right))
                    {
                        return left > right;
                    }
                }
                else if (evaluatedCondition.Contains("<"))
                {
                    var parts = evaluatedCondition.Split("<");
                    if (parts.Length == 2 &&
                        double.TryParse(parts[0].Trim(), out var left) &&
                        double.TryParse(parts[1].Trim(), out var right))
                    {
                        return left < right;
                    }
                }

                // 如果是布尔值
                if (bool.TryParse(evaluatedCondition, out var boolResult))
                {
                    return boolResult;
                }

                return false;
            }
            catch
            {
                return false;
            }
        }

        private string ReplaceVariables(string expression)
        {
            foreach (var variable in Variables)
            {
                expression = expression.Replace($"{{{variable.Key}}}", variable.Value?.ToString() ?? "");
            }
            return expression;
        }
    }

    public class NodeExecutionRecord
    {
        public string NodeId { get; set; }
        public string NodeName { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public TimeSpan Duration => EndTime - StartTime;
        public bool IsSuccessful { get; set; }
        public string ErrorMessage { get; set; }
        public object Result { get; set; }
    }
}
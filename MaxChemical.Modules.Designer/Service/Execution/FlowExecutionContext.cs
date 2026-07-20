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

        /// <summary>执行历史保留上限:循环节点会几小时不停执行,不封顶会无界增长吃内存。</summary>
        private const int MaxExecutionHistory = 5000;

        /// <summary>
        /// 追加一条执行历史并保持有界:超过上限时丢弃最旧的记录。
        /// 消费方(查最近一次节点输出)只反向取最近记录,裁掉旧的安全。
        /// </summary>
        public void AddExecutionHistory(NodeExecutionRecord record)
        {
            lock (ExecutionHistory)
            {
                ExecutionHistory.Add(record);
                int overflow = ExecutionHistory.Count - MaxExecutionHistory;
                if (overflow > 0)
                    ExecutionHistory.RemoveRange(0, overflow);
            }
        }

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
        /// 获取变量（泛型版本）
        /// </summary>
        public T GetVariable<T>(string name, T defaultValue = default)
        {
            if (Variables.TryGetValue(name, out var value))
            {
                if (value is T typedValue)
                    return typedValue;

                // 尝试类型转换
                try
                {
                    return (T)Convert.ChangeType(value, typeof(T));
                }
                catch
                {
                    return defaultValue;
                }
            }
            return defaultValue;
        }

        /// <summary>
        /// 获取变量（object版本）
        /// </summary>
        public object GetVariable(string name)
        {
            return Variables.TryGetValue(name, out var value) ? value : null;
        }

        /// <summary>
        /// 检查变量是否存在
        /// </summary>
        public bool HasVariable(string name)
        {
            return Variables.ContainsKey(name);
        }

        /// <summary>
        /// 删除变量
        /// </summary>
        public bool RemoveVariable(string name)
        {
            return Variables.TryRemove(name, out _);
        }

        /// <summary>
        /// 清空所有变量
        /// </summary>
        public void ClearVariables()
        {
            Variables.Clear();
        }

        /// <summary>
        /// 评估条件表达式（保留兼容性）
        /// </summary>
        public bool EvaluateCondition(string condition)
        {
            // 这个方法现在主要用于兼容性
            // 实际的条件评估在ExecuteIfCommandAsync中实现
            return true;
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
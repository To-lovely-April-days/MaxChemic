using System;
using System.Collections.Generic;
using System.Threading;

namespace FlowEngine.Core
{
    /// <summary>
    /// 流程执行上下文
    /// </summary>
    public class FlowContext
    {
        private readonly Dictionary<string, object> _variables = new Dictionary<string, object>();
        private readonly object _lock = new object();

        public IServiceProvider ServiceProvider { get; set; }
        public CancellationToken CancellationToken { get; set; }
        public IFlowLogger Logger { get; set; }
        public string FlowId { get; set; } = Guid.NewGuid().ToString();
        public DateTime StartTime { get; set; } = DateTime.Now;

        /// <summary>
        /// 获取变量值
        /// </summary>
        public T GetVariable<T>(string name, T defaultValue = default(T))
        {
            if (string.IsNullOrWhiteSpace(name))
                return defaultValue;

            lock (_lock)
            {
                if (_variables.TryGetValue(name, out var value))
                {
                    try
                    {
                        if (value is T directValue)
                            return directValue;

                        if (value != null)
                        {
                            if (typeof(T) == typeof(string))
                                return (T)(object)value.ToString();

                            return (T)Convert.ChangeType(value, typeof(T));
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger?.LogError($"变量 {name} 类型转换失败: {ex.Message}");
                    }
                }
                return defaultValue;
            }
        }

        /// <summary>
        /// 设置变量值
        /// </summary>
        public void SetVariable(string name, object value)
        {
            if (string.IsNullOrWhiteSpace(name))
                return;

            lock (_lock)
            {
                _variables[name] = value;
            }

            Logger?.LogDebug($"设置变量: {name} = {value}");
        }

        /// <summary>
        /// 检查变量是否存在
        /// </summary>
        public bool HasVariable(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return false;

            lock (_lock)
            {
                return _variables.ContainsKey(name);
            }
        }

        /// <summary>
        /// 获取所有变量
        /// </summary>
        public Dictionary<string, object> GetAllVariables()
        {
            lock (_lock)
            {
                return new Dictionary<string, object>(_variables);
            }
        }

        /// <summary>
        /// 清除所有变量
        /// </summary>
        public void ClearVariables()
        {
            lock (_lock)
            {
                _variables.Clear();
            }
        }

        /// <summary>
        /// 移除变量
        /// </summary>
        public bool RemoveVariable(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return false;

            lock (_lock)
            {
                return _variables.Remove(name);
            }
        }
    }
}
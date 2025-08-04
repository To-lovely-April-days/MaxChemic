using System;
using System.Collections.Generic;
using DevicePlugins.Devices;

namespace FlowEngine.Core
{
    /// <summary>
    /// 流程执行结果
    /// </summary>
    public class FlowExecutionResult
    {
        public bool IsSuccess { get; set; }
        public string Message { get; set; }
        public Dictionary<string, object> OutputData { get; set; } = new Dictionary<string, object>();
        public FlowExecutionStatus Status { get; set; }
        public Exception Exception { get; set; }
        public DateTime ExecutionTime { get; set; } = DateTime.Now;
        public TimeSpan Duration { get; set; }
    }

    /// <summary>
    /// 流程引擎的设备命令执行结果（避免与设备驱动命名冲突）
    /// </summary>
    public class FlowDeviceCommandResult
    {
        public bool IsSuccess { get; set; }
        public string Message { get; set; }
        public DeviceParameters OutputData { get; set; }
        public Exception Exception { get; set; }
        public TimeSpan ExecutionTime { get; set; }

        public Dictionary<string, object> GetOutputDictionary()
        {
            var dict = new Dictionary<string, object>();
            if (OutputData?.Variables != null)
            {
                foreach (var param in OutputData.Variables)
                {
                    dict[param.Name] = param.Value;
                }
            }
            return dict;
        }
    }

    /// <summary>
    /// 命令结果变量定义
    /// </summary>
    public class CommandResultVariableDefinition
    {
        public string VariableName { get; set; }
        public string DeviceId { get; set; }
        public string CommandName { get; set; }
        public string OutputParameterName { get; set; }
        public RefreshStrategy RefreshStrategy { get; set; } = RefreshStrategy.OnDemand;
        public int PollingInterval { get; set; } = 1000;
        public Dictionary<string, string> CommandParameters { get; set; } = new Dictionary<string, string>();
        public bool IsEnabled { get; set; } = true;
    }

    /// <summary>
    /// 变量值变更事件参数
    /// </summary>
    public class VariableValueChangedEventArgs : EventArgs
    {
        public string VariableName { get; }
        public object OldValue { get; }
        public object NewValue { get; }
        public DateTime Timestamp { get; }

        public VariableValueChangedEventArgs(string variableName, object oldValue, object newValue)
        {
            VariableName = variableName;
            OldValue = oldValue;
            NewValue = newValue;
            Timestamp = DateTime.Now;
        }
    }

    /// <summary>
    /// 变量信息
    /// </summary>
    public class VariableInfo
    {
        public string Name { get; set; }
        public object Value { get; set; }
        public RefreshStrategy RefreshStrategy { get; set; }
        public DateTime LastRefreshTime { get; set; }
        public bool IsRefreshing { get; set; }
        public bool IsEnabled { get; set; }
        public string DeviceId { get; set; }
        public string CommandName { get; set; }
        public string OutputParameterName { get; set; }
    }

    /// <summary>
    /// 流程日志事件参数
    /// </summary>
    public class FlowLogEventArgs : EventArgs
    {
        public string Message { get; set; }
        public LogLevel Level { get; set; }
        public DateTime Timestamp { get; set; } = DateTime.Now;

        public FlowLogEventArgs(string message, LogLevel level)
        {
            Message = message;
            Level = level;
        }
    }

    /// <summary>
    /// 流程节点执行事件参数
    /// </summary>
    public class FlowNodeExecutedEventArgs : EventArgs
    {
        public IFlowNode Node { get; set; }
        public FlowExecutionResult Result { get; set; }
        public TimeSpan ExecutionTime { get; set; }
    }
}
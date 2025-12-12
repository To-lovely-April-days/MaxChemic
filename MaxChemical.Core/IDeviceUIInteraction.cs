

namespace MaxChemical.Core
{
    /// <summary>
    /// 设备UI交互接口（设备需实现此接口以支持UI双向绑定）
    /// </summary>
    public interface IDeviceUIInteraction
    {
        /// <summary>
        /// 设备状态变化事件（设备 → UI）
        /// </summary>
        event EventHandler<DeviceControlStateChangedEventArgs> ControlStateChanged;

        /// <summary>
        /// 从UI接收命令执行请求（UI → 设备）
        /// </summary>
        Task<CommandExecutionResult> ExecuteUICommandAsync(string commandName, Dictionary<string, object> parameters = null);

        /// <summary>
        /// 获取当前设备状态（用于UI初始化）
        /// </summary>
        DeviceState GetCurrentState();

        
    }

    /// <summary>
    /// 设备状态变化事件参数
    /// </summary>
    public class DeviceControlStateChangedEventArgs : EventArgs
    {
        public string DeviceId { get; set; }
        public string ComId { get; set; }          // 设备通讯ID（PRV0101等）
        public string StateName { get; set; }  // 例如："IsOpen", "FlowRate", "Temperature"
        public object StateValue { get; set; }
        public DateTime Timestamp { get; set; } = DateTime.Now;

        //  新增：命令执行状态（可选）
        public CommandExecutionState? ExecutionState { get; set; }

        //  新增：关联的命令名称（可选）
        public string CommandName { get; set; }

        //  新增：额外数据（可选）
        public Dictionary<string, object> AdditionalData { get; set; }
    }
    /// <summary>
    ///  新增：命令执行状态枚举
    /// </summary>
    public enum CommandExecutionState
    {
        /// <summary>执行中</summary>
        Running,

        /// <summary>成功</summary>
        Succeeded,

        /// <summary>失败</summary>
        Failed,

        /// <summary>异常</summary>
        Error
    }
    /// <summary>
    /// 命令执行结果
    /// </summary>
    public class CommandExecutionResult
    {
        public bool Success { get; set; }
        public string Message { get; set; }
        public Dictionary<string, object> OutputData { get; set; } = new Dictionary<string, object>();
        public Exception Exception { get; set; }
    }

    /// <summary>
    /// 设备状态快照
    /// </summary>
    public class DeviceState
    {
        public Dictionary<string, object> States { get; set; } = new Dictionary<string, object>();
    }

    #region  新增：事件参数类

    /// <summary>
    /// 流量计状态切换请求事件参数（UI → 设备驱动）
    /// </summary>
    public class FlowMeterStateChangeRequestEventArgs : EventArgs
    {
        public string MeterId { get; set; }
        public bool CurrentState { get; set; }   // 当前流动状态
        public bool RequestedState { get; set; }  // 请求的新状态
        public double TargetFlow { get; set; }    // 目标流量
        public Action<bool> OnCompleted { get; set; }
    }

    /// <summary>
    /// 流量计目标流量变化请求事件参数
    /// </summary>
    public class FlowMeterTargetFlowChangeRequestEventArgs : EventArgs
    {
        public string MeterId { get; set; }
        public double CurrentTargetFlow { get; set; }
        public double RequestedTargetFlow { get; set; }
        public FlowChangeType ChangeType { get; set; }
        public Action<bool> OnCompleted { get; set; }
    }

    /// <summary>
    /// 流量变化类型
    /// </summary>
    public enum FlowChangeType
    {
        Increase,  // 增加
        Decrease,  // 减少
        Set        // 设置
    }

    #endregion
}
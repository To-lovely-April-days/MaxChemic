

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
}
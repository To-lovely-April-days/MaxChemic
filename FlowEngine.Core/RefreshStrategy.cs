using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;

namespace FlowEngine.Core
{
    /// <summary>
    /// 变量刷新策略
    /// </summary>
    public enum RefreshStrategy
    {
        /// <summary>
        /// 手动刷新 - 只有显式调用时才刷新
        /// </summary>
        Manual = 0,

        /// <summary>
        /// 按需刷新 - 当变量被引用时自动刷新
        /// </summary>
        OnDemand = 1,

        /// <summary>
        /// 事件驱动 - 当步骤执行时刷新
        /// </summary>
        EventDriven = 2,

        /// <summary>
        /// 定期自动刷新 - 按固定间隔刷新
        /// </summary>
        Periodic = 3,

        /// <summary>
        /// 自动刷新 - 引擎启动时及周期性刷新
        /// </summary>
        Automatic = 4
    }

    /// <summary>
    /// 流程执行状态
    /// </summary>
    public enum FlowExecutionStatus
    {
        Success,
        Failed,
        Paused,
        Cancelled,
        Skipped,
        Warning
    }

    /// <summary>
    /// 日志级别
    /// </summary>
    public enum LogLevel
    {
        Debug,
        Info,
        Warning,
        Error
    }

    /// <summary>
    /// 流程引擎状态
    /// </summary>
    public enum FlowEngineState
    {
        Stopped,
        Running,
        Paused,
        Completed,
        Failed,
        Stopping
    }

    /// <summary>
    /// 流程节点基础接口
    /// </summary>
    public interface IFlowNode
    {
        string Id { get; }
        string Name { get; set; }
        string Description { get; set; }
        Task<FlowExecutionResult> ExecuteAsync(FlowContext context, CancellationToken cancellationToken);
        void Validate();
        IFlowNode Clone();
    }

    /// <summary>
    /// 流程日志接口
    /// </summary>
    public interface IFlowLogger
    {
        void LogInfo(string message);
        void LogWarning(string message);
        void LogError(string message);
        void LogDebug(string message);
    }

    /// <summary>
    /// 设备服务接口
    /// </summary>
    public interface IDeviceService
    {
        Task<FlowDeviceCommandResult> ExecuteCommandAsync(string deviceName, string commandName,
            DevicePlugins.Devices.DeviceParameters inputParams, CancellationToken cancellationToken);
        DevicePlugins.Devices.IDevice GetDevice(string deviceName);
        List<DevicePlugins.Devices.IDevice> GetAllDevices();
        bool IsDeviceConnected(string deviceName);
    }

    /// <summary>
    /// 服务提供者接口
    /// </summary>
    public interface IServiceProvider
    {
        object GetService(Type serviceType);
        T GetService<T>() where T : class;
    }
}
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using MaxChemical.Modules.Designer.Models;
using MaxChemical.Modules.Designer.ViewModels;

namespace MaxChemical.Modules.Designer.Service.Execution
{
    public interface IFlowExecutionEngine
    {
        /// <summary>
        /// 执行状态
        /// </summary>
        FlowExecutionState State { get; }

        /// <summary>
        /// 执行状态变化事件
        /// </summary>
        event EventHandler<FlowExecutionStateChangedEventArgs> StateChanged;

        /// <summary>
        /// 节点执行事件
        /// </summary>
        event EventHandler<NodeExecutionEventArgs> NodeExecuting;
        event EventHandler<NodeExecutionEventArgs> NodeExecuted;

        /// <summary>
        /// 执行错误事件
        /// </summary>
        event EventHandler<FlowExecutionErrorEventArgs> ExecutionError;

        /// <summary>
        /// 执行进度事件
        /// </summary>
        event EventHandler<FlowExecutionProgressEventArgs> ProgressChanged;

        /// <summary>
        /// 开始执行流程
        /// </summary>
        Task<FlowExecutionResult> ExecuteAsync(FlowDesignerViewModel flowViewModel, CancellationToken cancellationToken = default);

        /// <summary>
        /// 暂停执行
        /// </summary>
        Task PauseAsync();

        /// <summary>
        /// 恢复执行
        /// </summary>
        Task ResumeAsync();

        /// <summary>
        /// 停止执行
        /// </summary>
        Task StopAsync();

        /// <summary>
        /// 验证流程
        /// </summary>
        FlowValidationResult ValidateFlow(FlowDesignerViewModel flowViewModel);
    }

    public enum FlowExecutionState
    {
        Idle,
        Running,
        Pausing,
        Paused,
        Resuming,
        Stopping,
        Stopped,
        Completed,
        Error
    }

    public class FlowExecutionStateChangedEventArgs : EventArgs
    {
        public FlowExecutionState OldState { get; set; }
        public FlowExecutionState NewState { get; set; }
        public string Message { get; set; }
    }

    public class NodeExecutionEventArgs : EventArgs
    {
        public CommandNode Node { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime? EndTime { get; set; }
        public bool IsSuccessful { get; set; }
        public string ErrorMessage { get; set; }
        public object Result { get; set; }
    }

    public class FlowExecutionErrorEventArgs : EventArgs
    {
        public Exception Exception { get; set; }
        public CommandNode CurrentNode { get; set; }
        public string ErrorMessage { get; set; }
    }

    public class FlowExecutionProgressEventArgs : EventArgs
    {
        public int TotalNodes { get; set; }
        public int CompletedNodes { get; set; }
        public double ProgressPercentage { get; set; }
        public CommandNode CurrentNode { get; set; }
    }

    public class FlowExecutionResult
    {
        public bool IsSuccessful { get; set; }
        public TimeSpan Duration { get; set; }
        public string ErrorMessage { get; set; }
        public Exception Exception { get; set; }
        public int TotalNodesExecuted { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
    }

    public class FlowValidationResult
    {
        public bool IsValid { get; set; }
        public List<string> Errors { get; set; } = new List<string>();
        public List<string> Warnings { get; set; } = new List<string>();
    }
}

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace MaxChemical.Infrastructure.DOE
{
    /// <summary>
    /// 流程执行服务接口 — DOE 模块通过此接口调用流程执行，与 Designer 解耦。
    /// Designer 侧由 FlowExecutionAdapter 实现并在 Shell 注册为单例。
    /// </summary>
    public interface IFlowExecutionService
    {
        /// <summary>
        /// 当前执行状态
        /// </summary>
        FlowRunState State { get; }

        /// <summary>
        /// 执行当前已加载的流程（参数已通过 IFlowParameterProvider 注入）
        /// </summary>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>执行结果</returns>
        Task<FlowRunResult> ExecuteCurrentFlowAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// 暂停执行
        /// </summary>
        void PauseExecution();

        /// <summary>
        /// 恢复执行
        /// </summary>
        void ResumeExecution();

        /// <summary>
        /// 停止执行
        /// </summary>
        Task StopExecutionAsync();

        /// <summary>
        /// 执行状态变化事件
        /// </summary>
        event EventHandler<FlowRunState> StateChanged;

        bool IsEngineIdle();
    }

    /// <summary>
    /// 流程执行状态
    /// </summary>
    public enum FlowRunState
    {
        Idle,
        Running,
        Paused,
        Stopping,
        Completed,
        Failed
    }

    /// <summary>
    /// 流程执行结果
    /// </summary>
    public class FlowRunResult
    {
        public bool IsSuccessful { get; set; }
        public string? ExperimentId { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime? EndTime { get; set; }
        public TimeSpan? Duration => EndTime.HasValue ? EndTime.Value - StartTime : null;
        public string? ErrorMessage { get; set; }
        public int TotalNodes { get; set; }
        public int CompletedNodes { get; set; }
    }
}

using MaxChemical.Infrastructure.DOE;
using MaxChemical.Logging;
using MaxChemical.Modules.Designer.Event;
using MaxChemical.Modules.Designer.Service.Execution;
using MaxChemical.Modules.Designer.ViewModels;
using Prism.Events;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace MaxChemical.Modules.Designer.Service
{
    /// <summary>
    /// 流程执行适配器 — 将 IFlowExecutionEngine (Designer内部) 适配为 IFlowExecutionService (Infrastructure接口)。
    /// DOE 模块通过此适配器间接调用 Designer 的流程执行引擎，避免直接引用 Designer 模块。
    /// 在 Shell 的 RegisterTypes 中注册为单例。
    /// </summary>
    public class FlowExecutionAdapter : IFlowExecutionService
    {
        private readonly IFlowExecutionEngine _engine;
        private readonly IEventAggregator _eventAggregator;
        private readonly ILogService _logger;

        // 缓存当前流程 ViewModel 的引用（通过事件获取）
        private FlowDesignerViewModel? _currentFlowViewModel;

        public FlowExecutionAdapter(
            IFlowExecutionEngine engine,
            IEventAggregator eventAggregator,
            ILogService logger)
        {
            _engine = engine ?? throw new ArgumentNullException(nameof(engine));
            _eventAggregator = eventAggregator ?? throw new ArgumentNullException(nameof(eventAggregator));
            _logger = logger?.ForContext<FlowExecutionAdapter>()
                      ?? throw new ArgumentNullException(nameof(logger));

            // 订阅引擎状态变化，转发为 Infrastructure 层的事件
            _engine.StateChanged += OnEngineStateChanged;
        }

        /// <summary>
        /// 当前执行状态（从 Designer 内部状态映射到 Infrastructure 层状态）
        /// </summary>
        public FlowRunState State => MapState(_engine.State);

        public event EventHandler<FlowRunState>? StateChanged;

        /// <summary>
        /// 执行当前已加载的流程
        /// </summary>
        public async Task<FlowRunResult> ExecuteCurrentFlowAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                // 通过事件获取当前流程 ViewModel
                EnsureFlowViewModel();

                if (_currentFlowViewModel == null)
                {
                    return new FlowRunResult
                    {
                        IsSuccessful = false,
                        ErrorMessage = "未找到当前流程，请先在流程设计器中打开一个流程",
                        StartTime = DateTime.Now,
                        EndTime = DateTime.Now
                    };
                }

                _logger.LogInformation("DOE 适配器: 开始执行流程");

                var result = await _engine.ExecuteAsync(_currentFlowViewModel, cancellationToken);

                return new FlowRunResult
                {
                    IsSuccessful = result.IsSuccessful,
                    ExperimentId = null, // 可从 engine 内部获取
                    StartTime = result.StartTime,
                    EndTime = result.EndTime,
                    ErrorMessage = result.ErrorMessage,
                    TotalNodes = result.TotalNodesExecuted + (result.IsSuccessful ? 0 : 1),
                    CompletedNodes = result.TotalNodesExecuted
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "DOE 适配器: 流程执行异常");
                return new FlowRunResult
                {
                    IsSuccessful = false,
                    ErrorMessage = ex.Message,
                    StartTime = DateTime.Now,
                    EndTime = DateTime.Now
                };
            }
        }

        public void PauseExecution()
        {
            _logger.LogInformation("DOE 适配器: 暂停执行");
            _engine.PauseAsync().ConfigureAwait(false);
        }

        public void ResumeExecution()
        {
            _logger.LogInformation("DOE 适配器: 恢复执行");
            _engine.ResumeAsync().ConfigureAwait(false);
        }

        public async Task StopExecutionAsync()
        {
            _logger.LogInformation("DOE 适配器: 停止执行");
            await _engine.StopAsync();
        }

        /// <summary>
        /// 通过事件聚合器获取当前流程 ViewModel
        /// </summary>
        private void EnsureFlowViewModel()
        {
            _currentFlowViewModel = null;

            // 利用现有的 GetCurrentFlowDataRequestEvent 模式，
            // 但这里我们需要直接获取 ViewModel 引用
            // 通过 RequestFlowViewModelEvent 或直接从容器解析
            try
            {
                // 方案: 通过事件请求当前 FlowDesignerViewModel
                _eventAggregator.GetEvent<GetCurrentFlowDesignerRequestEvent>()?.Publish(vm => { _currentFlowViewModel = vm; });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取当前流程 ViewModel 失败");
            }
        }

        /// <summary>
        /// 引擎状态映射
        /// </summary>
        private static FlowRunState MapState(FlowExecutionState engineState)
        {
            return engineState switch
            {
                FlowExecutionState.Idle => FlowRunState.Idle,
                FlowExecutionState.Running => FlowRunState.Running,
                FlowExecutionState.Pausing or FlowExecutionState.Paused => FlowRunState.Paused,
                FlowExecutionState.Resuming => FlowRunState.Running,
                FlowExecutionState.Stopping or FlowExecutionState.Stopped => FlowRunState.Stopping,
                FlowExecutionState.Completed => FlowRunState.Completed,
                FlowExecutionState.Error => FlowRunState.Failed,
                _ => FlowRunState.Idle
            };
        }

        private void OnEngineStateChanged(object? sender, FlowExecutionStateChangedEventArgs e)
        {
            var mappedState = MapState(e.NewState);
            StateChanged?.Invoke(this, mappedState);
        }
    }


}

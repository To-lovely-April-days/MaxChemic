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
    /// 
    ///  DOE 连续执行修复: 
    ///   FlowExecutionEngine 执行完成后 State 经过 Completed → (延迟2s) → Idle。
    ///   DOE 连续调用时必须等待引擎回到 Idle 才能发起下一组。
    /// </summary>
    public class FlowExecutionAdapter : IFlowExecutionService
    {
        private readonly IFlowExecutionEngine _engine;
        private readonly IEventAggregator _eventAggregator;
        private readonly ILogService _logger;

        private const int ENGINE_READY_TIMEOUT_SECONDS = 30;
        private const int ENGINE_POLL_INTERVAL_MS = 200;

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

            _engine.StateChanged += OnEngineStateChanged;
        }

        public FlowRunState State => MapState(_engine.State);

        public event EventHandler<FlowRunState>? StateChanged;

        /// <summary>
        /// 执行当前已加载的流程
        /// </summary>
        public async Task<FlowRunResult> ExecuteCurrentFlowAsync(CancellationToken cancellationToken = default)
        {
            try
            {
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

                //  关键修复: 等待引擎回到 Idle 状态 
                if (_engine.State != FlowExecutionState.Idle)
                {
                    _logger.LogInformation("DOE 适配器: 引擎状态为 {State}，等待回到 Idle...", _engine.State);

                    var waitStart = DateTime.Now;
                    while (_engine.State != FlowExecutionState.Idle)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        if ((DateTime.Now - waitStart).TotalSeconds > ENGINE_READY_TIMEOUT_SECONDS)
                        {
                            _logger.LogError("DOE 适配器: 等待引擎 Idle 超时 {Timeout}s, 当前: {State}",
                                ENGINE_READY_TIMEOUT_SECONDS, _engine.State);
                            return new FlowRunResult
                            {
                                IsSuccessful = false,
                                ErrorMessage = $"等待引擎就绪超时（{ENGINE_READY_TIMEOUT_SECONDS}s），状态: {_engine.State}",
                                StartTime = DateTime.Now,
                                EndTime = DateTime.Now
                            };
                        }

                        await Task.Delay(ENGINE_POLL_INTERVAL_MS, cancellationToken);
                    }

                    var waited = (DateTime.Now - waitStart).TotalMilliseconds;
                    _logger.LogInformation("DOE 适配器: 引擎已回到 Idle，等待 {Ms}ms", (int)waited);
                }

                //  额外安全延迟：确保引擎内部状态（设备、上下文等）完全清理
                await Task.Delay(500, cancellationToken);

                _logger.LogInformation("DOE 适配器: 开始执行流程 (引擎状态: {State})", _engine.State);

                var result = await _engine.ExecuteAsync(_currentFlowViewModel, cancellationToken);

                _logger.LogInformation("DOE 适配器: 流程返回 Success={Success}, 引擎状态={State}",
                    result.IsSuccessful, _engine.State);

                return new FlowRunResult
                {
                    IsSuccessful = result.IsSuccessful,
                    ExperimentId = null,
                    StartTime = result.StartTime,
                    EndTime = result.EndTime,
                    ErrorMessage = result.ErrorMessage,
                    TotalNodes = result.TotalNodesExecuted + (result.IsSuccessful ? 0 : 1),
                    CompletedNodes = result.TotalNodesExecuted
                };
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("DOE 适配器: 流程执行被取消");
                throw;
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
            _ = Task.Run(async () =>
            {
                try { await _engine.PauseAsync(); }
                catch (Exception ex) { _logger.LogError(ex, "DOE 适配器: 暂停执行失败"); }
            });
        }


        public void ResumeExecution()
        {
            _logger.LogInformation("DOE 适配器: 恢复执行");
            _ = Task.Run(async () =>
            {
                try { await _engine.ResumeAsync(); }
                catch (Exception ex) { _logger.LogError(ex, "DOE 适配器: 恢复执行失败"); }
            });
        }

        public async Task StopExecutionAsync()
        {
            _logger.LogInformation("DOE 适配器: 停止执行");
            await _engine.StopAsync();
        }

        private void EnsureFlowViewModel()
        {
            _currentFlowViewModel = null;
            try
            {
                _eventAggregator.GetEvent<GetCurrentFlowDesignerRequestEvent>()?.Publish(vm => { _currentFlowViewModel = vm; });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取当前流程 ViewModel 失败");
            }
        }

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

        public bool IsEngineIdle()
        {
            return _engine.State == FlowExecutionState.Idle;
        }
    }
}
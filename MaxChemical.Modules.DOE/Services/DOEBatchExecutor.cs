using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using MaxChemical.Infrastructure.DOE;
using MaxChemical.Logging;
using MaxChemical.Modules.DOE.Data;
using MaxChemical.Modules.DOE.Events;
using MaxChemical.Modules.DOE.Models;
using MaxChemical.Modules.DOE.Views;
using Newtonsoft.Json;
using Prism.Events;

namespace MaxChemical.Modules.DOE.Services
{
    /// <summary>
    /// DOE 批量执行器 — 修复版：响应采集使用 UI 线程同步 ShowDialog，避免死锁。
    /// 整个执行循环在 UI 线程运行（因为 Python GIL 和弹框都需要 UI 线程）。
    /// </summary>
    public class DOEBatchExecutor : IDOEExecutionService
    {
        private readonly IFlowExecutionService _flowExecution;
        private readonly IFlowParameterProvider _paramProvider;
        private readonly IDOERepository _repository;
        private readonly IGPRModelService _gprService;
        private readonly IEventAggregator _eventAggregator;
        private readonly ILogService _logger;

        private DOEBatchStatus _batchState = DOEBatchStatus.Designing;
        private int _currentRunIndex = -1;
        private string _currentBatchId = "";
        private CancellationTokenSource? _cts;
        private readonly ManualResetEventSlim _pauseEvent = new(true);

        private DOEBatch? _currentBatch;
        private List<DOERunRecord> _runs = new();
        private List<DOEStopCondition> _stopConditions = new();
        private List<DOEResponse> _responses = new();

        public DOEBatchExecutor(
            IFlowExecutionService flowExecution,
            IFlowParameterProvider paramProvider,
            IDOERepository repository,
            IGPRModelService gprService,
            IEventAggregator eventAggregator,
            ILogService logger)
        {
            _flowExecution = flowExecution ?? throw new ArgumentNullException(nameof(flowExecution));
            _paramProvider = paramProvider ?? throw new ArgumentNullException(nameof(paramProvider));
            _repository = repository ?? throw new ArgumentNullException(nameof(repository));
            _gprService = gprService ?? throw new ArgumentNullException(nameof(gprService));
            _eventAggregator = eventAggregator ?? throw new ArgumentNullException(nameof(eventAggregator));
            _logger = logger?.ForContext<DOEBatchExecutor>() ?? throw new ArgumentNullException(nameof(logger));
        }

        public DOEBatchStatus BatchState
        {
            get => _batchState;
            private set { _batchState = value; _logger.LogInformation("DOE 批次状态: {State}", value); }
        }

        public int CurrentRunIndex => _currentRunIndex;

        public event EventHandler<DOERunCompletedEventArgs>? RunCompleted;
        public event EventHandler<DOEBatchCompletedEventArgs>? BatchCompleted;
        public event EventHandler<DOEProgressEventArgs>? ProgressChanged;

        // ══════════════ 启动 ══════════════

        public async Task StartBatchAsync(string batchId, CancellationToken cancellationToken = default)
        {
            if (BatchState == DOEBatchStatus.Running)
                throw new InvalidOperationException("已有批次正在执行中");

            try
            {
                _currentBatch = await _repository.GetBatchWithDetailsAsync(batchId);
                if (_currentBatch == null) throw new InvalidOperationException($"未找到批次: {batchId}");

                _currentBatchId = batchId;
                _runs = _currentBatch.Runs
                    .Where(r => r.DataSource == DOEDataSource.Measured && r.Status == DOERunStatus.Pending)
                    .OrderBy(r => r.RunIndex).ToList();
                _stopConditions = _currentBatch.StopConditions;
                _responses = _currentBatch.Responses;

                if (_runs.Count == 0) throw new InvalidOperationException("没有待执行的实验组");

                // 初始化 GPR 模型
                try { await _gprService.InitializeModelAsync(_currentBatch.FlowId, _currentBatch.Factors); }
                catch (Exception ex) { _logger.LogError(ex, "GPR 模型初始化失败，无模型模式执行"); }

                _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                _pauseEvent.Set();
                _currentRunIndex = -1;

                BatchState = DOEBatchStatus.Running;
                await _repository.UpdateBatchStatusAsync(batchId, DOEBatchStatus.Running);
                _eventAggregator.GetEvent<DOEBatchStartedEvent>().Publish(batchId);

                _logger.LogInformation("DOE 批次开始: {BatchId}, {Count} 组", batchId, _runs.Count);

                // 执行循环（在当前线程，即 UI 线程）
                await ExecuteBatchLoopAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "DOE 批次启动失败");
                BatchState = DOEBatchStatus.Aborted;
                await _repository.UpdateBatchStatusAsync(batchId, DOEBatchStatus.Aborted);
                throw;
            }
        }

        public void PauseBatch()
        {
            if (BatchState != DOEBatchStatus.Running) return;
            _pauseEvent.Reset();
            BatchState = DOEBatchStatus.Paused;
            _flowExecution.PauseExecution();
        }

        public void ResumeBatch()
        {
            if (BatchState != DOEBatchStatus.Paused) return;
            _pauseEvent.Set();
            BatchState = DOEBatchStatus.Running;
            _flowExecution.ResumeExecution();
        }

        public async Task AbortBatchAsync()
        {
            _cts?.Cancel();
            _pauseEvent.Set();
            await _flowExecution.StopExecutionAsync();
            BatchState = DOEBatchStatus.Aborted;
            await _repository.UpdateBatchStatusAsync(_currentBatchId, DOEBatchStatus.Aborted);
            await SaveGPRModelSafe();
            NotifyBatchCompleted(DOEBatchStatus.Aborted, "用户手动终止");
        }

        public void SubmitRunResponse(int runIndex, Dictionary<string, double> responseValues)
        {
            // 不再需要，响应值通过 ShowDialog 同步返回
        }

        // ══════════════ 主循环 ══════════════

        private async Task ExecuteBatchLoopAsync()
        {
            string? stopReason = null;
            int completedCount = 0;

            // 因子名 → ParameterId 映射
            var factorNameToParamId = new Dictionary<string, string>();
            if (_currentBatch?.Factors != null)
            {
                foreach (var factor in _currentBatch.Factors)
                {
                    string paramId = !string.IsNullOrEmpty(factor.SourceNodeId) && !string.IsNullOrEmpty(factor.SourceParamName)
                        ? $"{factor.SourceNodeId}.{factor.SourceParamName}"
                        : factor.FactorName;
                    factorNameToParamId[factor.FactorName] = paramId;
                }
            }

            try
            {
                for (int i = 0; i < _runs.Count; i++)
                {
                    _cts!.Token.ThrowIfCancellationRequested();

                    var run = _runs[i];
                    _currentRunIndex = run.RunIndex;
                    NotifyProgress(i, _runs.Count, $"正在执行第 {i + 1}/{_runs.Count} 组实验...");

                    // ── Step 1: 注入参数 ──
                    var factorValues = JsonConvert.DeserializeObject<Dictionary<string, double>>(run.FactorValuesJson)
                                       ?? new Dictionary<string, double>();

                    var injectParams = new Dictionary<string, object>();
                    foreach (var kv in factorValues)
                    {
                        var paramId = factorNameToParamId.TryGetValue(kv.Key, out var pid) ? pid : kv.Key;
                        injectParams[paramId] = kv.Value;
                    }

                    bool injected = _paramProvider.InjectParameters(injectParams);
                    if (!injected)
                    {
                        _logger.LogWarning("第 {RunIndex} 组参数注入失败", run.RunIndex);
                        run.Status = DOERunStatus.Failed;
                        await _repository.UpdateRunAsync(run);
                        continue;
                    }

                    // ── Step 2: 执行流程 ──
                    run.Status = DOERunStatus.Running;
                    run.StartTime = DateTime.Now;
                    await _repository.UpdateRunAsync(run);

                    var flowResult = await _flowExecution.ExecuteCurrentFlowAsync(_cts.Token);

                    if (!flowResult.IsSuccessful)
                    {
                        _logger.LogWarning("第 {RunIndex} 组流程执行失败: {Error}", run.RunIndex, flowResult.ErrorMessage);
                        run.Status = DOERunStatus.Failed;
                        run.EndTime = DateTime.Now;
                        await _repository.UpdateRunAsync(run);
                        RunCompleted?.Invoke(this, new DOERunCompletedEventArgs
                        {
                            RunIndex = run.RunIndex,
                            FactorValues = factorValues,
                            IsSuccessful = false
                        });
                        continue;
                    }

                    run.ExperimentId = flowResult.ExperimentId;

                    // ── Step 3: ★ 同步弹框采集响应值（关键修复）──
                    run.Status = DOERunStatus.WaitingResponse;
                    await _repository.UpdateRunAsync(run);
                    NotifyProgress(i, _runs.Count, $"第 {i + 1} 组流程完成，等待输入结果...");

                    var responseValues = CollectResponseFromDialog(run.RunIndex, _runs.Count, factorValues);

                    // ── Step 4: 保存结果 ──
                    run.ResponseValuesJson = JsonConvert.SerializeObject(responseValues);
                    run.Status = DOERunStatus.Completed;
                    run.EndTime = DateTime.Now;
                    await _repository.UpdateRunAsync(run);
                    completedCount++;

                    _logger.LogInformation("第 {RunIndex} 组完成: 响应={Responses}", run.RunIndex, run.ResponseValuesJson);

                    RunCompleted?.Invoke(this, new DOERunCompletedEventArgs
                    {
                        RunIndex = run.RunIndex,
                        FactorValues = factorValues,
                        ResponseValues = responseValues,
                        IsSuccessful = true
                    });

                    // ── Step 4.5: 喂给 GPR ──
                    await FeedGPRModelAsync(factorValues, responseValues);

                    // ── Step 5: 停止条件 ──
                    var stopDecision = EvaluateAllStopConditions(responseValues, i);
                    if (stopDecision.ShouldStop)
                    {
                        stopReason = stopDecision.Reason;
                        _logger.LogInformation("停止条件满足: {Reason}", stopReason);
                        for (int j = i + 1; j < _runs.Count; j++)
                        {
                            _runs[j].Status = DOERunStatus.Skipped;
                            await _repository.UpdateRunAsync(_runs[j]);
                        }
                        break;
                    }
                }

                BatchState = DOEBatchStatus.Completed;
                await _repository.UpdateBatchStatusAsync(_currentBatchId, DOEBatchStatus.Completed);
                NotifyBatchCompleted(DOEBatchStatus.Completed, stopReason ?? "所有实验组已执行完毕");
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("DOE 批次被取消");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "DOE 批次执行异常");
                BatchState = DOEBatchStatus.Aborted;
                await _repository.UpdateBatchStatusAsync(_currentBatchId, DOEBatchStatus.Aborted);
                NotifyBatchCompleted(DOEBatchStatus.Aborted, $"异常: {ex.Message}");
            }
            finally
            {
                await SaveGPRModelSafe();
                _currentRunIndex = -1;
                _cts?.Dispose();
                _cts = null;
            }
        }

        // ══════════════ ★ 同步弹框采集响应值 ══════════════

        /// <summary>
        /// 在 UI 线程上同步弹出响应采集对话框，等用户填完才返回。
        /// 这样保证每组实验必须采集完才执行下一组。
        /// </summary>
        private Dictionary<string, double> CollectResponseFromDialog(
            int runIndex, int totalRuns, Dictionary<string, double> factorValues)
        {
            Dictionary<string, double> result = new();

            // 必须在 UI 线程执行 ShowDialog
            Application.Current.Dispatcher.Invoke(() =>
            {
                var responseDefs = _responses.Select(r => (r.ResponseName, r.Unit)).ToList();
                var dialog = new DOEResponseCollectionDialog(runIndex, totalRuns, factorValues, responseDefs);
                dialog.Owner = Application.Current.MainWindow;
                dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;

                var dialogResult = dialog.ShowDialog();

                if (dialogResult == true && dialog.CollectedValues != null)
                {
                    result = dialog.CollectedValues;
                }
                // 用户跳过或关闭 → result 保持空字典
            });

            return result;
        }

        // ══════════════ GPR ══════════════

        private async Task FeedGPRModelAsync(Dictionary<string, double> factorValues, Dictionary<string, double> responseValues)
        {
            if (responseValues.Count == 0) return;
            try
            {
                var primaryResponseName = _responses.FirstOrDefault()?.ResponseName;
                if (primaryResponseName == null || !responseValues.TryGetValue(primaryResponseName, out var responseVal))
                    return;

                _gprService.AppendData(factorValues, responseVal);
                var trainResult = await _gprService.TrainModelAsync();

                _eventAggregator.GetEvent<GPRModelUpdatedEvent>().Publish(new GPRModelUpdatedPayload
                {
                    FlowId = _currentBatch!.FlowId,
                    IsActive = trainResult.IsActive,
                    RSquared = trainResult.RSquared,
                    DataCount = trainResult.DataCount
                });
            }
            catch (Exception ex) { _logger.LogError(ex, "GPR 更新失败"); }
        }

        // ══════════════ 停止条件 ══════════════

        private StopEvaluationResult EvaluateAllStopConditions(Dictionary<string, double> responseValues, int currentIndex)
        {
            var thresholdResult = EvaluateThresholdConditions(responseValues);
            if (thresholdResult.ShouldStop) return thresholdResult;

            var modelResult = EvaluateModelStopCondition(currentIndex);
            if (modelResult.ShouldStop) return modelResult;

            return new StopEvaluationResult { ShouldStop = false };
        }

        private StopEvaluationResult EvaluateThresholdConditions(Dictionary<string, double> responseValues)
        {
            var conditions = _stopConditions.Where(c => c.ConditionType == DOEStopConditionType.Threshold).ToList();
            if (conditions.Count == 0) return new StopEvaluationResult { ShouldStop = false };

            bool useAnd = conditions.Any(c => c.LogicGroup == DOELogicGroup.And);
            var results = new List<bool>();

            foreach (var cond in conditions)
            {
                if (string.IsNullOrEmpty(cond.ResponseName)) continue;
                if (!responseValues.TryGetValue(cond.ResponseName, out var actual)) continue;
                results.Add(EvaluateOperator(actual, cond.Operator, cond.TargetValue));
            }

            if (results.Count == 0) return new StopEvaluationResult { ShouldStop = false };
            bool stop = useAnd ? results.All(r => r) : results.Any(r => r);
            return new StopEvaluationResult { ShouldStop = stop, Reason = stop ? "阈值条件满足" : "" };
        }

        private StopEvaluationResult EvaluateModelStopCondition(int currentIndex)
        {
            var modelConds = _stopConditions.Where(c => c.ConditionType == DOEStopConditionType.ModelPrediction).ToList();
            if (modelConds.Count == 0 || !_gprService.IsActive)
                return new StopEvaluationResult { ShouldStop = false };

            try
            {
                var remaining = _runs.Where(r => r.Status == DOERunStatus.Pending)
                    .Select(r => JsonConvert.DeserializeObject<Dictionary<string, double>>(r.FactorValuesJson)!)
                    .Where(d => d != null).ToList();

                if (remaining.Count == 0) return new StopEvaluationResult { ShouldStop = false };

                var primaryResp = _responses.FirstOrDefault()?.ResponseName;
                if (primaryResp == null) return new StopEvaluationResult { ShouldStop = false };

                double bestObs = _runs.Where(r => r.Status == DOERunStatus.Completed && !string.IsNullOrEmpty(r.ResponseValuesJson))
                    .Select(r => { var d = JsonConvert.DeserializeObject<Dictionary<string, double>>(r.ResponseValuesJson!); return d != null && d.TryGetValue(primaryResp, out var v) ? v : double.MinValue; })
                    .DefaultIfEmpty(double.MinValue).Max();

                var decision = _gprService.ShouldStop(remaining, bestObs);
                return new StopEvaluationResult
                {
                    ShouldStop = decision.ShouldStop,
                    Reason = decision.ShouldStop ? $"GPR: {decision.Reason}" : ""
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GPR 停止判断失败");
                return new StopEvaluationResult { ShouldStop = false };
            }
        }

        private static bool EvaluateOperator(double actual, string op, double target) => op switch
        {
            "Equals" => Math.Abs(actual - target) < 1e-9,
            "NotEquals" => Math.Abs(actual - target) >= 1e-9,
            "GreaterThan" => actual > target,
            "GreaterThanOrEqual" => actual >= target,
            "LessThan" => actual < target,
            "LessThanOrEqual" => actual <= target,
            _ => false
        };

        private async Task SaveGPRModelSafe()
        {
            try { if (_currentBatch != null && _gprService.DataCount > 0) await _gprService.SaveStateAsync(_currentBatch.FlowId); }
            catch (Exception ex) { _logger.LogError(ex, "保存 GPR 失败"); }
        }

        private void NotifyProgress(int current, int total, string message)
        {
            ProgressChanged?.Invoke(this, new DOEProgressEventArgs { CurrentRun = current + 1, TotalRuns = total, StatusMessage = message });
            _eventAggregator.GetEvent<DOEProgressChangedEvent>().Publish(new DOEProgressPayload
            { BatchId = _currentBatchId, CurrentRun = current + 1, TotalRuns = total, StatusMessage = message });
        }

        private void NotifyBatchCompleted(DOEBatchStatus status, string reason)
        {
            var completed = _runs.Count(r => r.Status == DOERunStatus.Completed);
            BatchCompleted?.Invoke(this, new DOEBatchCompletedEventArgs
            { BatchId = _currentBatchId, FinalStatus = status, TotalRuns = _runs.Count, CompletedRuns = completed, StopReason = reason });
            _eventAggregator.GetEvent<DOEBatchCompletedEvent>().Publish(new DOEBatchCompletedPayload
            { BatchId = _currentBatchId, FinalStatus = status, CompletedRuns = completed, StopReason = reason });
        }

        private class StopEvaluationResult { public bool ShouldStop { get; set; } public string Reason { get; set; } = ""; }
    }
}
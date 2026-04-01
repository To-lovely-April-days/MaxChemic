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
    /// DOE 批量执行器 — 修复版：
    /// 
    /// 根因: FeedGPRModelAsync 中 Py.GIL() 在后台线程获取 Python 全局解释器锁时
    ///       与其他线程（FlowExecutionEngine 的设备通知/状态回调）竞争 GIL，导致死锁。
    /// 
    /// 修复: 
    /// 1. GPR 训练和停止条件判断加超时保护（不阻塞执行循环）
    /// 2. 循环内每组加 try-catch 容错
    /// 3. 每步详细日志
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

        /// <summary>GPR 操作超时（秒）— 防止 Python GIL 死锁阻塞循环</summary>
        private const int GPR_TIMEOUT_SECONDS = 30;

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
        public event EventHandler? PauseConfirmed;

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

                _logger.LogInformation("DOE 批次开始: {BatchId}, {Count} 组待执行", batchId, _runs.Count);

                await Task.Run(async () => await ExecuteBatchLoopAsync());
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
        }

        public void ResumeBatch()
        {
            if (BatchState != DOEBatchStatus.Paused) return;
            _pauseEvent.Set();
            BatchState = DOEBatchStatus.Running;
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

        public void SubmitRunResponse(int runIndex, Dictionary<string, double> responseValues) { }

        // ══════════════ 主循环 ══════════════

        private async Task ExecuteBatchLoopAsync()
        {
            string? stopReason = null;
            int completedCount = 0;

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
                    //await CheckPauseAsync(i);
                    _cts.Token.ThrowIfCancellationRequested();

                    var run = _runs[i];
                    _currentRunIndex = run.RunIndex;

                    _logger.LogInformation("═══════ DOE 开始第 {Index}/{Total} 组 (RunIndex={RunIndex}) ═══════",
                        i + 1, _runs.Count, run.RunIndex);

                    NotifyProgress(i, _runs.Count, $"正在执行第 {i + 1}/{_runs.Count} 组实验...");

                    try
                    {
                        // ── Step 1: 注入参数 ──
                        var factorValues = JsonConvert.DeserializeObject<Dictionary<string, double>>(run.FactorValuesJson)
                                           ?? new Dictionary<string, double>();

                        var injectParams = new Dictionary<string, object>();
                        foreach (var kv in factorValues)
                        {
                            var paramId = factorNameToParamId.TryGetValue(kv.Key, out var pid) ? pid : kv.Key;
                            injectParams[paramId] = kv.Value;
                        }

                        _logger.LogInformation("第 {Index} 组: 注入参数...", i + 1);
                        bool injected = _paramProvider.InjectParameters(injectParams);
                        if (!injected)
                        {
                            _logger.LogWarning("第 {Index} 组: 参数注入失败，跳过", i + 1);
                            run.Status = DOERunStatus.Failed;
                            await _repository.UpdateRunAsync(run);
                            continue;
                        }
                        _logger.LogInformation("第 {Index} 组: 参数注入成功", i + 1);

                        // ── Step 2: 执行流程 ──
                        run.Status = DOERunStatus.Running;
                        run.StartTime = DateTime.Now;
                        await _repository.UpdateRunAsync(run);

                        _logger.LogInformation("第 {Index} 组: 调用 ExecuteCurrentFlowAsync...", i + 1);
                        var flowResult = await _flowExecution.ExecuteCurrentFlowAsync(_cts.Token);
                        _logger.LogInformation("第 {Index} 组: 流程返回 Success={Success}", i + 1, flowResult.IsSuccessful);

                        if (!flowResult.IsSuccessful)
                        {
                            _logger.LogWarning("第 {Index} 组: 流程执行失败: {Error}", i + 1, flowResult.ErrorMessage);
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

                        //await CheckPauseAsync(i);

                        // ── Step 3: 同步弹框采集响应值 ──
                        run.Status = DOERunStatus.WaitingResponse;
                        await _repository.UpdateRunAsync(run);
                        NotifyProgress(i, _runs.Count, $"第 {i + 1} 组流程完成，等待输入结果...");

                        _logger.LogInformation("第 {Index} 组: 弹框等待用户输入...", i + 1);
                        var responseValues = CollectResponseFromDialog(run.RunIndex, _runs.Count, factorValues);
                        _logger.LogInformation("第 {Index} 组: 用户输入完成，响应值数量={Count}", i + 1, responseValues.Count);

                        // ── Step 4: 保存结果 ──
                        run.ResponseValuesJson = JsonConvert.SerializeObject(responseValues);
                        run.Status = DOERunStatus.Completed;
                        run.EndTime = DateTime.Now;
                        await _repository.UpdateRunAsync(run);
                        completedCount++;

                        _logger.LogInformation("第 {Index} 组完成: 响应={Responses}", i + 1, run.ResponseValuesJson);

                        RunCompleted?.Invoke(this, new DOERunCompletedEventArgs
                        {
                            RunIndex = run.RunIndex,
                            FactorValues = factorValues,
                            ResponseValues = responseValues,
                            IsSuccessful = true
                        });

                        // ── Step 4.5: ★ GPR 训练（加超时保护，防止 Python GIL 死锁）──
                        _logger.LogInformation("第 {Index} 组: 开始 GPR 训练...", i + 1);
                        await FeedGPRModelWithTimeoutAsync(factorValues, responseValues);
                        _logger.LogInformation("第 {Index} 组: GPR 训练完成", i + 1);

                        // ── Step 5: ★ 停止条件（加超时保护）──
                        _logger.LogInformation("第 {Index} 组: 评估停止条件...", i + 1);
                        var stopDecision = EvaluateAllStopConditionsWithTimeout(responseValues, i);
                        _logger.LogInformation("第 {Index} 组: 停止条件: ShouldStop={Stop}", i + 1, stopDecision.ShouldStop);

                        if (stopDecision.ShouldStop)
                        {
                            stopReason = stopDecision.Reason;
                            _logger.LogInformation("停止条件满足: {Reason}", stopReason);
                            for (int j = i + 1; j < _runs.Count; j++)
                            {
                                _runs[j].Status = DOERunStatus.Suspended;
                                await _repository.UpdateRunAsync(_runs[j]);

                            }
                            break;
                            
                        }
                        await CheckPauseAsync(i);
                        _logger.LogInformation("═══════ DOE 第 {Index}/{Total} 组结束，继续下一组 ═══════", i + 1, _runs.Count);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception runEx)
                    {
                        _logger.LogError(runEx, "第 {Index} 组异常: {Message}", i + 1, runEx.Message);
                        run.Status = DOERunStatus.Failed;
                        run.EndTime = DateTime.Now;
                        try { await _repository.UpdateRunAsync(run); } catch { }
                        RunCompleted?.Invoke(this, new DOERunCompletedEventArgs
                        {
                            RunIndex = run.RunIndex,
                            IsSuccessful = false
                        });
                    }
                }

                BatchState = DOEBatchStatus.Completed;
                await _repository.UpdateBatchStatusAsync(_currentBatchId, DOEBatchStatus.Completed);
                NotifyBatchCompleted(DOEBatchStatus.Completed, stopReason ?? "所有实验组已执行完毕");
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("DOE 批次被取消");
                BatchState = DOEBatchStatus.Aborted;
                await _repository.UpdateBatchStatusAsync(_currentBatchId, DOEBatchStatus.Aborted);
                NotifyBatchCompleted(DOEBatchStatus.Aborted, "用户取消");
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
                if (BatchState == DOEBatchStatus.Running || BatchState == DOEBatchStatus.Paused)
                    BatchState = DOEBatchStatus.Completed;
            }
        }

        // ══════════════ ★ GPR 训练（带超时保护）══════════════

        /// <summary>
        /// GPR 训练带超时保护。
        /// Python GIL 在后台线程获取时可能与 FlowExecutionEngine 的设备回调线程竞争，
        /// 导致 Py.GIL() 永久阻塞。加超时后即使 GIL 获取失败也不会卡死执行循环。
        /// </summary>
        private async Task FeedGPRModelWithTimeoutAsync(Dictionary<string, double> factorValues, Dictionary<string, double> responseValues)
        {
            if (responseValues.Count == 0) return;

            try
            {
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(GPR_TIMEOUT_SECONDS));

                var gprTask = Task.Run(async () => await FeedGPRModelAsync(factorValues, responseValues));

                var completedTask = await Task.WhenAny(gprTask, Task.Delay(GPR_TIMEOUT_SECONDS * 1000, timeoutCts.Token));

                if (completedTask == gprTask)
                {
                    timeoutCts.Cancel();  // 取消超时计时器
                    await gprTask;        // 传播异常（如果有）
                }
                else
                {
                    _logger.LogWarning("GPR 训练超时（{Timeout}s），跳过本次训练，继续执行下一组", GPR_TIMEOUT_SECONDS);
                    // 不等待 gprTask，让它在后台自行结束或阻塞
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GPR 训练异常（已跳过）");
            }
        }

        private async Task FeedGPRModelAsync(Dictionary<string, double> factorValues, Dictionary<string, double> responseValues)
        {
            if (responseValues.Count == 0) return;
            try
            {
                var primaryResponseName = _responses.FirstOrDefault()?.ResponseName;
                if (primaryResponseName == null || !responseValues.TryGetValue(primaryResponseName, out var responseVal))
                    return;

                _gprService.AppendData(factorValues, responseVal, "measured",
                    _currentBatch!.BatchName, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                var trainResult = await _gprService.TrainModelAsync();

                // ★ 新增: 每组训练完立即保存到数据库，不再依赖 finally 一次性保存
                // 这样即使后续执行中断、程序崩溃或 GIL 死锁，已训练的数据也不会丢失
                try
                {
                    await _gprService.SaveStateAsync(_currentBatch!.FlowId);
                    _logger.LogInformation("GPR 模型已保存: DataCount={Count}, IsActive={Active}, R²={R2}",
                        trainResult.DataCount, trainResult.IsActive, trainResult.RSquared);
                }
                catch (Exception saveEx)
                {
                    _logger.LogError(saveEx, "GPR 模型保存失败（数据仍在内存中，下次训练时重试）");
                }

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

        // ══════════════ ★ 停止条件（带超时保护）══════════════

        /// <summary>
        /// 评估停止条件，带超时保护。
        /// EvaluateModelStopCondition 内部调用 _gprService.ShouldStop 需要 Python GIL，
        /// 同样可能死锁，加超时后即使卡住也不影响执行循环。
        /// </summary>
        private StopEvaluationResult EvaluateAllStopConditionsWithTimeout(Dictionary<string, double> responseValues, int currentIndex)
        {
            try
            {
                // 阈值条件不涉及 Python，直接评估
                var thresholdResult = EvaluateThresholdConditions(responseValues);
                if (thresholdResult.ShouldStop) return thresholdResult;

                // GPR 模型条件涉及 Python GIL，加超时
                var modelTask = Task.Run(() => EvaluateModelStopCondition(currentIndex));
                if (modelTask.Wait(TimeSpan.FromSeconds(GPR_TIMEOUT_SECONDS)))
                {
                    return modelTask.Result.ShouldStop ? modelTask.Result : new StopEvaluationResult { ShouldStop = false };
                }
                else
                {
                    _logger.LogWarning("GPR 停止条件评估超时（{Timeout}s），跳过模型判断", GPR_TIMEOUT_SECONDS);
                    return new StopEvaluationResult { ShouldStop = false };
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "停止条件评估异常");
                return new StopEvaluationResult { ShouldStop = false };
            }
        }

        // ══════════════ 弹框采集响应值 ══════════════

        private Dictionary<string, double> CollectResponseFromDialog(
            int runIndex, int totalRuns, Dictionary<string, double> factorValues)
        {
            Dictionary<string, double> result = new();

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
            });

            return result;
        }

        // ══════════════ 停止条件（内部） ══════════════

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
            _logger.LogInformation(" SaveGPRModelSafe 执行: batch={Batch}, dataCount={Count}",
                _currentBatch?.BatchId, _gprService.DataCount);
            try
            {
                if (_currentBatch != null && _gprService.DataCount > 0)
                {
                    await _gprService.SaveStateAsync(_currentBatch.FlowId);
                    _logger.LogInformation("SaveGPRModelSafe 成功: FlowId={FlowId}", _currentBatch.FlowId);
                }
                else
                {
                    _logger.LogWarning(" SaveGPRModelSafe 跳过: batch={Batch}, count={Count}",
                        _currentBatch?.BatchId, _gprService.DataCount);
                }
            }
            catch (Exception ex) { _logger.LogError(ex, "★ SaveGPRModelSafe 失败"); }
        }

        private async Task CheckPauseAsync(int currentIndex)
        {
            if (_pauseEvent.IsSet) return;

            _logger.LogInformation("暂停生效：在第 {Index} 组后暂停", currentIndex + 1);
            NotifyProgress(currentIndex, _runs.Count, "已暂停，等待恢复...");
            PauseConfirmed?.Invoke(this, EventArgs.Empty);

            await Task.Run(() => _pauseEvent.Wait(_cts!.Token));

            _logger.LogInformation("恢复执行");
            _cts!.Token.ThrowIfCancellationRequested();
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
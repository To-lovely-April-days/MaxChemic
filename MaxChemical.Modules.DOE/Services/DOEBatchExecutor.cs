using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using MaxChemical.Infrastructure.Cloud;
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
    /// DOE 批量执行器 — 修复版 v2：
    /// 
    ///  修复内容:
    /// 1. [严重] 多响应 GPR: 使用 IMultiResponseGPRService 同时喂所有响应变量的数据
    /// 2. [严重] 停止条件 bestObserved 方向性: 根据 Desirability 配置决定 max/min
    /// 3. [中等] CollectResponseFromDialog 空返回保护: 用户关闭对话框时不丢失 run 状态
    /// 4. [中等] GPR 超时泄漏监控: 计数器跟踪超时任务数量
    /// 5. [小] SubmitRunResponse 空方法: 标注 deprecated
    /// 
    /// 原有修复保留:
    /// - GIL 超时保护 (30s)
    /// - 每组 try-catch 容错
    /// - 每步详细日志
    /// </summary>
    public class DOEBatchExecutor : IDOEExecutionService
    {
        private readonly IFlowExecutionService _flowExecution;
        private readonly IFlowParameterProvider _paramProvider;
        private readonly IFlowWaitNodeProvider _waitNodeProvider; // 动态稳态等待:读原节点时长/单位(可空则退回固定等待)
        private readonly IDOERepository _repository;
        private readonly IGPRModelService _gprService;
        private readonly IMultiResponseGPRService _multiGprService;  //  新增
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

        /// <summary> 新增: 跟踪因超时而泄漏的 GPR 任务数量</summary>
        private int _leakedGprTaskCount = 0;
        private const int LEAKED_TASK_WARNING_THRESHOLD = 5;

        /// <summary>
        ///  新增: Desirability 配置缓存，用于确定停止条件中 bestObserved 的方向
        /// </summary>
        private List<DesirabilityResponseConfig> _desirabilityConfigs = new();

        /// <summary>
        /// 延迟录入模式(阶梯扫描):流程完成后不弹录入窗,各组停在 WaitingResponse 等事后回填。
        /// 字段级:收官统计(NotifyBatchCompleted)也要按它把「流程已完成」的组计入。
        /// </summary>
        private bool _deferResponses;

        public DOEBatchExecutor(
            IFlowExecutionService flowExecution,
            IFlowParameterProvider paramProvider,
            IDOERepository repository,
            IGPRModelService gprService,
            IMultiResponseGPRService multiGprService,  //  新增
            IEventAggregator eventAggregator,
            ILogService logger,
            IFlowWaitNodeProvider waitNodeProvider = null)  // 动态稳态等待;未注册则退回固定等待
        {
            _flowExecution = flowExecution ?? throw new ArgumentNullException(nameof(flowExecution));
            _paramProvider = paramProvider ?? throw new ArgumentNullException(nameof(paramProvider));
            _waitNodeProvider = waitNodeProvider; // 允许为空:安全退回固定等待
            _repository = repository ?? throw new ArgumentNullException(nameof(repository));
            _gprService = gprService ?? throw new ArgumentNullException(nameof(gprService));
            _multiGprService = multiGprService ?? throw new ArgumentNullException(nameof(multiGprService));
            _eventAggregator = eventAggregator ?? throw new ArgumentNullException(nameof(eventAggregator));
            _logger = logger?.ForContext<DOEBatchExecutor>() ?? throw new ArgumentNullException(nameof(logger));
        }

        public DOEBatchStatus BatchState
        {
            get => _batchState;
            private set { _batchState = value; _logger.LogInformation("DOE 批次状态: {State}", value); }
        }

        public int CurrentRunIndex => _currentRunIndex;

        public string CurrentBatchId => _currentBatchId;

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

                // 初始化 GPR 模型（主响应）
                try { await _gprService.InitializeModelAsync(_currentBatch.FlowId, _currentBatch.Factors); }
                catch (Exception ex) { _logger.LogError(ex, "GPR 模型初始化失败，无模型模式执行"); }

                //  新增: 初始化多响应 GPR 模型
                try
                {
                    await _multiGprService.InitializeAsync(_currentBatch.FlowId, _currentBatch.Factors, _responses);
                    _logger.LogInformation("多响应 GPR 初始化成功: {Count} 个响应", _responses.Count);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "多响应 GPR 初始化失败，使用主响应模式继续");
                }

                //  新增: 加载 Desirability 配置（用于停止条件方向判断）
                try
                {
                    var configJson = await _repository.GetDesirabilityConfigJsonAsync(batchId);
                    if (!string.IsNullOrEmpty(configJson))
                    {
                        _desirabilityConfigs = JsonConvert.DeserializeObject<List<DesirabilityResponseConfig>>(configJson)
                                               ?? new();
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "加载 Desirability 配置失败，停止条件使用默认方向（maximize）");
                }

                _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                _pauseEvent.Set();
                _currentRunIndex = -1;
                _leakedGprTaskCount = 0;

                BatchState = DOEBatchStatus.Running;
                await _repository.UpdateBatchStatusAsync(batchId, DOEBatchStatus.Running);
                _eventAggregator.GetEvent<DOEBatchStartedEvent>().Publish(batchId);
                PublishCloudDoeSnapshot(null);   // ★ 上云: 推初始运行表(全部待执行)

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
            await SaveMultiGPRModelSafe();  //  新增
            NotifyBatchCompleted(DOEBatchStatus.Aborted, "用户手动终止");
        }

        /// <summary>
        /// [Deprecated] 该方法原为异步提交响应值的入口，但当前执行循环使用同步弹框模式。
        /// 保留接口兼容性，不做任何操作。如需异步模式，请重构为 TaskCompletionSource 模式。
        /// </summary>
        [Obsolete("当前使用 CollectResponseFromDialog 同步模式，此方法不执行任何操作")]
        public void SubmitRunResponse(int runIndex, Dictionary<string, double> responseValues) { }

        // ══════════════ 主循环 ══════════════

        private async Task ExecuteBatchLoopAsync()
        {
            string? stopReason = null;
            int completedCount = 0;

            // ★ 修改: 仅"已绑定流程参数"的因子放入注入字典,
            //   未绑定的因子(类别因子 + 手动操作的连续因子)由操作员按矩阵值手动调节
            // ★ 化学家坐标系: 批次带 factorTransform 配置时,τ/eq 等派生因子既不绑定也不手动,
            //   注入前经转换层反算成泵速——绝不能落进 manual 桶被静默降级为人肉操作
            var transform = FactorTransform.ExtractFromDesignConfig(_currentBatch?.DesignConfigJson);
            var derivedFactorNames = new HashSet<string>();
            if (transform != null)
            {
                derivedFactorNames.Add(transform.TauFactor);
                if (!string.IsNullOrEmpty(transform.EqFactor)) derivedFactorNames.Add(transform.EqFactor);
                _logger.LogInformation("批次启用化学家坐标系: {Desc}", FactorTransform.DescribeConstants(transform));
            }

            // ── 延迟录入模式(阶梯扫描):流程完成后不弹结果录入窗,该组停在 WaitingResponse,
            //    直接进下一组;结果之后经 record_run_response 回填。离线分析(HPLC)场景下
            //    录入窗会把整条阶梯卡在第一阶。
            _deferResponses = false;
            try
            {
                var cfgDict = JsonConvert.DeserializeObject<Dictionary<string, object>>(_currentBatch?.DesignConfigJson ?? "{}");
                if (cfgDict != null && cfgDict.TryGetValue("deferResponses", out var dv))
                    _deferResponses = string.Equals(dv?.ToString(), "True", StringComparison.OrdinalIgnoreCase);
            }
            catch { }
            if (_deferResponses)
                _logger.LogInformation("批次启用延迟录入:各组完成后停在待录结果,不弹录入窗,结果由用户事后回填");

            var factorNameToParamId = new Dictionary<string, string>();
            var manualFactorNames = new HashSet<string>();
            if (_currentBatch?.Factors != null)
            {
                foreach (var factor in _currentBatch.Factors)
                {
                    if (derivedFactorNames.Contains(factor.FactorName))
                        continue; // 派生因子:注入前统一反算,见 Step 1

                    bool isBound = !string.IsNullOrEmpty(factor.SourceNodeId)
                                && !string.IsNullOrEmpty(factor.SourceParamName);
                    if (isBound)
                    {
                        factorNameToParamId[factor.FactorName] =
                            $"{factor.SourceNodeId}.{factor.SourceParamName}";
                    }
                    else
                    {
                        manualFactorNames.Add(factor.FactorName);
                    }
                }
            }

            // ★ 修复: 计算弹框显示用的总数和偏移量
            int totalActiveRuns = _currentBatch!.Runs.Count(r => r.Status != DOERunStatus.Skipped);
            // 显示编号用稳定映射(RunIndex → 看板位置):旧的「已执行数偏移」在续跑/重试时会错位——
            // 延迟录入让 WaitingResponse 成为持久状态后,续跑批次会把第 4 组播成「第 1 组、标记 S1」,
            // 离线样品编号一旦错位,回填的结果就写进错误的组。此映射与执行看板、get_doe_progress、
            // record_run_response 的「第 N 组」口径完全一致,且对重试穿插(中间某组重置为待执行)同样稳定。
            var displayPos = _currentBatch!.Runs
                .Where(r => r.Status != DOERunStatus.Skipped)
                .OrderBy(r => r.RunIndex)
                .Select((r, idx) => (r.RunIndex, Pos: idx + 1))
                .ToDictionary(t => t.RunIndex, t => t.Pos);

            // ── 动态稳态等待:批次级快照 ──
            // 注入会原地改写共享流程里的 Wait 节点;这里先记录原时长与单位,批次结束还原,
            // 避免污染后续对同一流程的复用(否则秒会被留成分钟、值留成上一组的 dwell)。
            // 全程按节点原单位换算注入、不改单位;找不到节点则本批次退回固定等待。
            var ssCfg = transform?.SteadyState;
            string ssNodeId = null;
            double ssOrigWaitTime = 0;
            string ssNodeUnit = "Seconds";
            double ssMinutesToUnit = 60.0; // 分钟 → 节点单位
            if (ssCfg != null && ssCfg.Enabled)
            {
                try
                {
                    var allWaits = _waitNodeProvider?.GetFixedWaitNodes();
                    var wn = string.IsNullOrEmpty(ssCfg.WaitNodeId)
                        ? null
                        : allWaits?.FirstOrDefault(w => w.NodeId == ssCfg.WaitNodeId);

                    // 「停留时间等待节点」标记是权威定位依据(设计器里用户亲手勾的,勾选框承诺只驱动它):
                    // 恰有一个标记节点时,存档 id 未命中 → 自愈改用标记;命中但与标记不一致(继承轮
                    // 之间流程改过、标记挪了)→ 以标记为准并警告——旧 id 可能已被改作清洗等待,
                    // 往它注入保温时长正是本标记要消灭的错。零个或多个标记不猜,退回固定等待。
                    var marked = allWaits?.Where(w => w.IsResidenceTimeWait).ToList();
                    if (marked != null && marked.Count == 1)
                    {
                        if (wn == null)
                        {
                            wn = marked[0];
                            _logger.LogInformation("动态稳态等待:存档节点 {Old} 未命中,按标记改用节点 {New}({Name})",
                                string.IsNullOrEmpty(ssCfg.WaitNodeId) ? "(空)" : ssCfg.WaitNodeId, wn.NodeId, wn.DisplayName);
                        }
                        else if (wn.NodeId != marked[0].NodeId)
                        {
                            _logger.LogWarning("动态稳态等待:存档节点 {Old}({OldName}) 与设计器标记节点 {New}({NewName}) 不一致,以标记为准",
                                wn.NodeId, wn.DisplayName, marked[0].NodeId, marked[0].DisplayName);
                            wn = marked[0];
                        }
                    }

                    if (wn != null)
                    {
                        ssNodeId = wn.NodeId;
                        ssOrigWaitTime = wn.CurrentWaitTime;
                        ssNodeUnit = string.IsNullOrEmpty(wn.TimeUnit) ? "Seconds" : wn.TimeUnit;
                        ssMinutesToUnit = ssNodeUnit switch
                        {
                            "Milliseconds" => 60000.0,
                            "Seconds" => 60.0,
                            "Minutes" => 1.0,
                            "Hours" => 1.0 / 60.0,
                            _ => 60.0
                        };
                        _logger.LogInformation("动态稳态等待启用:节点 {Node} 原时长 {Orig} {Unit},逐组按 τ 覆盖,批次结束还原",
                            ssNodeId, ssOrigWaitTime, ssNodeUnit);
                    }
                    else
                    {
                        _logger.LogWarning("动态稳态等待:无法定位保温等待节点(存档 id:{Node};标记节点数:{Marked}),本批次退回固定等待",
                            string.IsNullOrEmpty(ssCfg.WaitNodeId) ? "(空)" : ssCfg.WaitNodeId, marked?.Count ?? 0);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("动态稳态等待快照失败,退回固定等待:{Msg}", ex.Message);
                }
            }

            try
            {
                for (int i = 0; i < _runs.Count; i++)
                {
                    _cts!.Token.ThrowIfCancellationRequested();

                    var run = _runs[i];
                    _currentRunIndex = run.RunIndex;
                    // 本组的看板显示序号(第 N 组):稳定口径,续跑/重试不漂移
                    int dispNo = displayPos.TryGetValue(run.RunIndex, out var dpos) ? dpos : i + 1;

                    _logger.LogInformation("═══════ DOE 开始第 {Index}/{Total} 组 (RunIndex={RunIndex}) ═══════",
                        dispNo, totalActiveRuns, run.RunIndex);

                    NotifyProgress(i, _runs.Count, $"正在执行第 {dispNo}/{totalActiveRuns} 组实验...");

                    try
                    {
                        // ── Step 1: 注入参数 ──
                        var factorValuesRaw = JsonConvert.DeserializeObject<Dictionary<string, object>>(run.FactorValuesJson)
                                           ?? new Dictionary<string, object>();

                        var categoricalFactorNames = _currentBatch?.Factors?
                            .Where(f => f.IsCategorical).Select(f => f.FactorName).ToHashSet()
                            ?? new HashSet<string>();

                        var factorValues = new Dictionary<string, double>();
                        foreach (var kv in factorValuesRaw)
                        {
                            if (categoricalFactorNames.Contains(kv.Key))
                                continue;
                            if (kv.Value is double d) factorValues[kv.Key] = d;
                            else if (kv.Value is long l) factorValues[kv.Key] = l;
                            else if (double.TryParse(kv.Value?.ToString(), out var parsed)) factorValues[kv.Key] = parsed;
                        }
                        var injectParams = new Dictionary<string, object>();
                        foreach (var kv in factorValues)
                        {
                            // ★ 化学家坐标系: 派生因子在下方统一经转换层反算注入,此处跳过
                            if (derivedFactorNames.Contains(kv.Key))
                                continue;
                            // ★ 修改: 跳过未绑定因子(手动操作),由操作员按矩阵值手动调节
                            if (manualFactorNames.Contains(kv.Key))
                            {
                                _logger.LogDebug("第 {Index} 组: 连续因子 {Name}={Value}(手动操作,不注入)",
                                    dispNo, kv.Key, kv.Value);
                                continue;
                            }
                            if (factorNameToParamId.TryGetValue(kv.Key, out var pid))
                            {
                                injectParams[pid] = kv.Value;
                            }
                            // 既不在 manual 也不在已绑定字典 → 因子定义异常,跳过
                        }

                        // ★ 化学家坐标系: τ/eq → 泵速反算(闭式解),失败或超量程则本组失败,绝不带错注入
                        if (transform != null)
                        {
                            string terr = FactorTransform.SolveDeviceValues(transform, factorValues, out var deviceVals);
                            if (terr != null)
                            {
                                _logger.LogWarning("第 {Index} 组: 化学家坐标反算失败({Err}),该组标记失败",
                                    dispNo, terr);
                                run.Status = DOERunStatus.Failed;
                                await _repository.UpdateRunAsync(run);
                                continue;
                            }
                            foreach (var kv in deviceVals) injectParams[kv.Key] = kv.Value;
                            _logger.LogInformation("第 {Index} 组: 派生因子反算 → {Detail}",
                                dispNo,
                                string.Join(", ", deviceVals.Select(kv => $"{kv.Key}={kv.Value:0.####}")));

                            // ★ 动态稳态等待:按本组 τ 折算保温时长,覆盖指定 Wait 节点的时长(不改其单位)。
                            //   dwell = 倍数 × τ(min),夹到 [Min,Max],再换算到节点原单位;
                            //   用不变区域性字符串注入(逗号小数系统下装箱 double 会被误读为千分位)。
                            //   仅当批次级快照成功(ssNodeId 非空)才注入,否则退回固定等待。
                            if (ssNodeId != null && ssCfg != null
                                && factorValues.TryGetValue(transform.TauFactor, out double tauForDwell))
                            {
                                double? dwellMin = FactorTransform.ComputeDwellMinutes(ssCfg, tauForDwell);
                                if (dwellMin.HasValue)
                                {
                                    double dwellInUnit = dwellMin.Value * ssMinutesToUnit;
                                    injectParams[$"{ssNodeId}.WaitTime"] =
                                        dwellInUnit.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
                                    _logger.LogInformation("第 {Index} 组: 动态稳态等待 τ={Tau:0.###}min × {N} = {DwellMin:0.###}min = {DwellUnit:0.###} {Unit} → 节点 {Node}",
                                        dispNo, tauForDwell, ssCfg.Turnovers, dwellMin.Value, dwellInUnit, ssNodeUnit, ssNodeId);
                                }
                            }
                        }

                        foreach (var catName in categoricalFactorNames)
                        {
                            if (factorValuesRaw.TryGetValue(catName, out var catVal))
                            {
                                _logger.LogDebug("第 {Index} 组: 类别因子 {Name}={Value}（手动操作）",
                                    dispNo, catName, catVal);
                            }
                        }

                        _logger.LogInformation("第 {Index} 组: 注入参数（{Count} 个连续因子）...", dispNo, injectParams.Count);
                        bool injected = injectParams.Count == 0 || _paramProvider.InjectParameters(injectParams);
                        if (!injected)
                        {
                            _logger.LogWarning("第 {Index} 组: 参数注入失败，跳过", dispNo);
                            run.Status = DOERunStatus.Failed;
                            await _repository.UpdateRunAsync(run);
                            continue;
                        }
                        _logger.LogInformation("第 {Index} 组: 参数注入成功", dispNo);

                        // ── Step 2: 执行流程 ──
                        run.Status = DOERunStatus.Running;
                        run.StartTime = DateTime.Now;
                        await _repository.UpdateRunAsync(run);
                        // ★ 新增: 通知 mini 面板更新当前因子显示
                        _eventAggregator.GetEvent<DOEProgressChangedEvent>().Publish(new DOEProgressPayload
                        {
                            BatchId = _currentBatchId,
                            CurrentRun = dispNo,
                            TotalRuns = totalActiveRuns,
                            StatusMessage = $"正在执行第 {dispNo}/{totalActiveRuns} 组实验...",
                            CurrentFactorValuesJson = run.FactorValuesJson,
                            RunIndex = run.RunIndex   // 先猜后验:与 DOERunCompletedEvent 的键对齐
                        });
                        PublishCloudDoeSnapshot(null);   // ★ 上云: 该组进入执行中,刷新运行表高亮
                        _logger.LogInformation("第 {Index} 组: 调用 ExecuteCurrentFlowAsync...", dispNo);
                        var flowResult = await _flowExecution.ExecuteCurrentFlowAsync(_cts.Token);
                        _logger.LogInformation("第 {Index} 组: 流程返回 Success={Success}", dispNo, flowResult.IsSuccessful);

                        if (!flowResult.IsSuccessful)
                        {
                            _logger.LogWarning("第 {Index} 组: 流程执行失败: {Error}", dispNo, flowResult.ErrorMessage);
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

                        // ── Step 3(延迟录入分支): 不弹录入窗,该组停在待录结果,直接进下一组 ──
                        if (_deferResponses)
                        {
                            run.Status = DOERunStatus.WaitingResponse;
                            run.EndTime = DateTime.Now; // 流程已完成;回填只补响应
                            await _repository.UpdateRunAsync(run);
                            NotifyProgress(i, _runs.Count,
                                $"第 {dispNo}/{totalActiveRuns} 组流程完成(延迟录入,请取样并标记 S{dispNo}),继续下一组...");
                            RunCompleted?.Invoke(this, new DOERunCompletedEventArgs
                            {
                                RunIndex = run.RunIndex,
                                FactorValues = factorValues,
                                IsSuccessful = true
                            });
                            PublishCloudDoeSnapshot(null);
                            await CheckPauseAsync(i);
                            continue; // 无响应可评:跳过 GPR 训练与停止条件
                        }

                        // ── Step 3: 同步弹框采集响应值 ──
                        run.Status = DOERunStatus.WaitingResponse;
                        await _repository.UpdateRunAsync(run);
                        NotifyProgress(i, _runs.Count, $"第 {dispNo} 组流程完成，等待输入结果...");

                        _logger.LogInformation("第 {Index} 组: 等待填结果(桌面弹框 / 云端远程,先到者生效)...", dispNo);
                        // ★ 修复: dispNo - 1 = 0-based全局序号，totalActiveRuns = 总组数
                        // ★ 上云: 本地弹框与网页远程填结果二选一,谁先填谁生效
                        var responseValues = CollectResponse(dispNo - 1, totalActiveRuns, factorValuesRaw, run);
                        _logger.LogInformation("第 {Index} 组: 用户输入完成，响应值数量={Count}", dispNo, responseValues.Count);

                        if (responseValues.Count == 0)
                        {
                            _logger.LogWarning("第 {Index} 组: 用户未输入响应值，标记为 Failed", dispNo);
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

                        // ── Step 4: 保存结果 ──
                        run.ResponseValuesJson = JsonConvert.SerializeObject(responseValues);
                        run.Status = DOERunStatus.Completed;
                        run.EndTime = DateTime.Now;
                        await _repository.UpdateRunAsync(run);
                        completedCount++;

                        _logger.LogInformation("第 {Index} 组完成: 响应={Responses}", dispNo, run.ResponseValuesJson);

                        RunCompleted?.Invoke(this, new DOERunCompletedEventArgs
                        {
                            RunIndex = run.RunIndex,
                            FactorValues = factorValues,
                            ResponseValues = responseValues,
                            IsSuccessful = true
                        });
                        // 先猜后验:结果已落库,通知追踪器做区间判定与模型更新
                        _eventAggregator.GetEvent<DOERunResultRecordedEvent>().Publish(new DOERunCompletedPayload
                        {
                            BatchId = _currentBatchId,
                            RunIndex = run.RunIndex,
                            FactorValuesJson = run.FactorValuesJson,
                            ResponseValuesJson = run.ResponseValuesJson
                        });
                        PublishCloudDoeSnapshot(null);   // ★ 上云: 该组已完成(结果写回),刷新运行表

                        // ── Step 4.5: 多响应 GPR 训练（加超时保护）──
                        _logger.LogInformation("第 {Index} 组: 开始多响应 GPR 训练...", dispNo);
                        await FeedGPRModelWithTimeoutAsync(factorValuesRaw, responseValues);
                        _logger.LogInformation("第 {Index} 组: GPR 训练完成", dispNo);

                        // ── Step 5: 停止条件（加超时保护）──
                        _logger.LogInformation("第 {Index} 组: 评估停止条件...", dispNo);
                        var stopDecision = EvaluateAllStopConditionsWithTimeout(responseValues, i);
                        _logger.LogInformation("第 {Index} 组: 停止条件: ShouldStop={Stop}", dispNo, stopDecision.ShouldStop);

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
                        _logger.LogInformation("═══════ DOE 第 {Index}/{Total} 组结束，继续下一组 ═══════", dispNo, totalActiveRuns);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception runEx)
                    {
                        _logger.LogError(runEx, "第 {Index} 组异常: {Message}", dispNo, runEx.Message);
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

                // 延迟录入批次此时还没有任何响应,轮次决策没数据可评:
                // 事件推迟到 record_run_response 把结果补齐后再发,避免「数据不足」的空炮播报与垃圾决策记录
                if (_currentBatch?.BelongsToProject == true && _deferResponses)
                    _logger.LogInformation("延迟录入批次收官:轮次完成事件推迟到结果回填齐后发布");

                if (_currentBatch?.BelongsToProject == true && !_deferResponses)
                {
                    try
                    {
                        _eventAggregator.GetEvent<DOERoundCompletedEvent>().Publish(
                            new DOERoundCompletedPayload
                            {
                                ProjectId = _currentBatch.ProjectId!,
                                BatchId = _currentBatchId,
                                RoundNumber = _currentBatch.RoundNumber ?? 0,
                                FinalStatus = DOEBatchStatus.Completed
                            });
                        _logger.LogInformation(
                            "项目轮次完成事件已发布: Project={ProjectId}, Round={Round}",
                            _currentBatch.ProjectId, _currentBatch.RoundNumber);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "发布项目轮次完成事件失败（不影响批次完成）");
                    }
                }
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
                // 动态稳态等待:还原被覆盖的等待节点(原值、原单位),不污染后续对同一流程的复用
                if (ssNodeId != null)
                {
                    try
                    {
                        _paramProvider.InjectParameters(new Dictionary<string, object>
                        {
                            [$"{ssNodeId}.WaitTime"] = ssOrigWaitTime
                        });
                        _logger.LogInformation("动态稳态等待:已还原节点 {Node} 时长为 {Orig} {Unit}", ssNodeId, ssOrigWaitTime, ssNodeUnit);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning("动态稳态等待还原失败(不影响批次结果):{Msg}", ex.Message);
                    }
                }

                await SaveGPRModelSafe();
                await SaveMultiGPRModelSafe();
                _currentRunIndex = -1;
                _cts?.Dispose();
                _cts = null;
                if (BatchState == DOEBatchStatus.Running || BatchState == DOEBatchStatus.Paused)
                    BatchState = DOEBatchStatus.Completed;
            }
        }

        // ══════════════  GPR 训练（带超时保护）══════════════

        /// <summary>
        /// ★ 修复 (v3): factorValues 改为 Dictionary&lt;string, object&gt; 以透传类别因子标签
        /// </summary>
        private async Task FeedGPRModelWithTimeoutAsync(Dictionary<string, object> factorValues, Dictionary<string, double> responseValues)
        {
            if (responseValues.Count == 0) return;

            //  新增: 检查泄漏任务数量
            if (_leakedGprTaskCount >= LEAKED_TASK_WARNING_THRESHOLD)
            {
                _logger.LogWarning("已有 {Count} 个 GPR 训练任务因超时泄漏，Python GIL 可能存在严重竞争问题。" +
                    "建议重启应用或检查设备回调线程。", _leakedGprTaskCount);
            }

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
                    Interlocked.Increment(ref _leakedGprTaskCount);
                    _logger.LogWarning("GPR 训练超时（{Timeout}s），跳过本次训练，继续执行下一组。泄漏任务数: {Leaked}",
                        GPR_TIMEOUT_SECONDS, _leakedGprTaskCount);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GPR 训练异常（已跳过）");
            }
        }

        /// <summary>
        /// ★ 修复 (v3): factorValues 改为 Dictionary&lt;string, object&gt; 以透传类别因子标签
        /// </summary>
        private async Task FeedGPRModelAsync(Dictionary<string, object> factorValues, Dictionary<string, double> responseValues)
        {
            if (responseValues.Count == 0) return;
            try
            {
                //  修改: 同时喂所有响应变量（原来只喂主响应）
                _multiGprService.AppendAllResponses(
                    factorValues, responseValues,
                    "measured", _currentBatch!.BatchName,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));

                // 训练所有模型
                var trainResults = await _multiGprService.TrainAllAsync();

                // 取主响应的训练结果用于事件通知和保存
                var primaryResponseName = _responses.FirstOrDefault()?.ResponseName ?? "";
                var primaryResult = trainResults.TryGetValue(primaryResponseName, out var pr)
                    ? pr : new GPRTrainResult { IsActive = false };

                //  保存主模型（与原来一致）
                try
                {
                    await _gprService.SaveStateAsync(_currentBatch!.FlowId);
                    _logger.LogInformation("GPR 主模型已保存: DataCount={Count}, IsActive={Active}, R²={R2}",
                        primaryResult.DataCount, primaryResult.IsActive, primaryResult.RSquared);
                }
                catch (Exception saveEx)
                {
                    _logger.LogError(saveEx, "GPR 主模型保存失败（数据仍在内存中）");
                }

                //  新增: 保存次要模型
                try
                {
                    await _multiGprService.SaveAllAsync(_currentBatch!.FlowId);
                }
                catch (Exception saveEx)
                {
                    _logger.LogError(saveEx, "GPR 次要模型保存失败（数据仍在内存中）");
                }

                // 日志：各响应训练状态
                foreach (var kv in trainResults)
                {
                    _logger.LogInformation("GPR [{Response}]: Active={Active}, Data={Count}, R²={R2}",
                        kv.Key, kv.Value.IsActive, kv.Value.DataCount, kv.Value.RSquared);
                }

                _eventAggregator.GetEvent<GPRModelUpdatedEvent>().Publish(new GPRModelUpdatedPayload
                {
                    FlowId = _currentBatch!.FlowId,
                    IsActive = primaryResult.IsActive,
                    RSquared = primaryResult.RSquared,
                    DataCount = primaryResult.DataCount
                });
            }
            catch (Exception ex) { _logger.LogError(ex, "GPR 更新失败"); }
        }

        // ══════════════  停止条件（带超时保护）══════════════

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
            int runIndex, int totalRuns, Dictionary<string, object> factorValues)
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

        /// <summary>
        /// ★ 上云: 采集某组的响应值 —— 本地弹框 与 网页远程填结果 二选一,谁先填谁生效。
        /// 发布带 waiting 的云端快照(网页据此弹远程表单),并订阅 <see cref="CloudDoeRemoteResponseEvent"/>;
        /// 远程先到则自动关闭本地弹框。云端未接入时行为等同原来的纯本地弹框。
        /// </summary>
        private Dictionary<string, double> CollectResponse(
            int globalIdx, int totalRuns, Dictionary<string, object> factorValues, DOERunRecord run)
        {
            // 1) 推送带 waiting 的快照 → 网页弹远程填结果表单
            _currentRunIndex = run.RunIndex;
            var waiting = new CloudDoeWaiting
            {
                RunIndex = run.RunIndex,
                Factors = factorValues,
                Responses = _responses.Select(r => new CloudDoeResponseDef { Name = r.ResponseName, Unit = r.Unit ?? "" }).ToList()
            };
            PublishCloudDoeSnapshot(waiting);

            // 2) 远程结果等待器
            Dictionary<string, double>? remote = null;
            using var remoteReady = new ManualResetEventSlim(false);
            void OnRemote(CloudDoeRemoteResponse p)
            {
                if (p != null && p.BatchId == _currentBatchId && p.RunIndex == run.RunIndex
                    && p.Values != null && p.Values.Count > 0)
                {
                    remote = p.Values;
                    remoteReady.Set();
                }
            }
            var token = _eventAggregator.GetEvent<CloudDoeRemoteResponseEvent>()
                .Subscribe(OnRemote, ThreadOption.BackgroundThread, true);

            Dictionary<string, double> local = new();
            DOEResponseCollectionDialog? dlgRef = null;

            // 3) 后台: 远程先到 → 关闭本地弹框
            var closer = System.Threading.Tasks.Task.Run(() =>
            {
                try { remoteReady.Wait(_cts?.Token ?? CancellationToken.None); } catch { }
                try
                {
                    Application.Current?.Dispatcher.Invoke(() =>
                    {
                        if (dlgRef != null && dlgRef.IsLoaded) dlgRef.Close();
                    });
                }
                catch { }
            });

            // 4) UI 线程: 弹本地弹框(远程已到则跳过)
            Application.Current.Dispatcher.Invoke(() =>
            {
                if (remoteReady.IsSet) return;
                var responseDefs = _responses.Select(r => (r.ResponseName, r.Unit)).ToList();
                var dialog = new DOEResponseCollectionDialog(globalIdx, totalRuns, factorValues, responseDefs);
                dlgRef = dialog;
                dialog.Owner = Application.Current.MainWindow;
                dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
                var ok = dialog.ShowDialog();
                if (ok == true && dialog.CollectedValues != null) local = dialog.CollectedValues;
            });

            // 5) 收尾
            _eventAggregator.GetEvent<CloudDoeRemoteResponseEvent>().Unsubscribe(token);
            remoteReady.Set();                 // 释放 closer(本地先填时)
            try { closer.Wait(500); } catch { }

            if (local.Count > 0) return local; // 本地先填 → 用本地
            return remote ?? new Dictionary<string, double>();  // 否则用远程(网页)
        }

        /// <summary>★ 上云: 把当前批次运行表打包成云端快照并发布,由 Designer 的 CloudPushService POST 到云端。</summary>
        private void PublishCloudDoeSnapshot(CloudDoeWaiting? waiting)
        {
            try
            {
                if (_currentBatch == null) return;
                var snap = new CloudDoeSnapshot
                {
                    BatchId = _currentBatchId,
                    BatchName = _currentBatch.BatchName ?? "",
                    Status = BatchState.ToString(),
                    CurrentRunIndex = _currentRunIndex,
                    Factors = _currentBatch.Factors?.Select(f => f.FactorName).ToList() ?? new List<string>(),
                    Responses = _responses.Select(r => new CloudDoeResponseDef { Name = r.ResponseName, Unit = r.Unit ?? "" }).ToList(),
                    Runs = _currentBatch.Runs.OrderBy(r => r.RunIndex).Select(BuildCloudRun).ToList(),
                    Waiting = waiting
                };
                _eventAggregator.GetEvent<CloudDoeSnapshotEvent>().Publish(snap);
            }
            catch (Exception ex) { _logger.LogWarning("发布云端 DOE 快照失败: {Message}", ex.Message); }
        }

        private static CloudDoeRun BuildCloudRun(DOERunRecord r)
        {
            var run = new CloudDoeRun { RunIndex = r.RunIndex, Status = r.Status.ToString() };
            try
            {
                if (!string.IsNullOrEmpty(r.FactorValuesJson))
                    run.Factors = JsonConvert.DeserializeObject<Dictionary<string, object>>(r.FactorValuesJson) ?? new();
            }
            catch { }
            try
            {
                if (!string.IsNullOrEmpty(r.ResponseValuesJson))
                    run.Responses = JsonConvert.DeserializeObject<Dictionary<string, double>>(r.ResponseValuesJson!) ?? new();
            }
            catch { }
            return run;
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

        /// <summary>
        ///  修复: bestObserved 方向性
        /// 原来硬编码取 max，但如果目标是 minimize（如成本最低）应该取 min。
        /// 修复: 根据 Desirability 配置的 Goal 决定取 max 还是 min。
        /// </summary>
        private StopEvaluationResult EvaluateModelStopCondition(int currentIndex)
        {
            var modelConds = _stopConditions.Where(c => c.ConditionType == DOEStopConditionType.ModelPrediction).ToList();
            if (modelConds.Count == 0 || !_gprService.IsActive)
                return new StopEvaluationResult { ShouldStop = false };

            try
            {
                // ★ 修复 (v3): 用 Dictionary<string, object> 保留类别因子标签
                var remaining = _runs.Where(r => r.Status == DOERunStatus.Pending)
                    .Select(r => JsonConvert.DeserializeObject<Dictionary<string, object>>(r.FactorValuesJson)!)
                    .Where(d => d != null).ToList();

                if (remaining.Count == 0) return new StopEvaluationResult { ShouldStop = false };

                var primaryResp = _responses.FirstOrDefault()?.ResponseName;
                if (primaryResp == null) return new StopEvaluationResult { ShouldStop = false };

                //  修复: 根据 Desirability 配置确定优化方向
                bool isMinimize = _desirabilityConfigs
                    .Any(c => c.ResponseName == primaryResp && c.Goal == DesirabilityGoal.Minimize);

                var completedValues = _runs
                    .Where(r => r.Status == DOERunStatus.Completed && !string.IsNullOrEmpty(r.ResponseValuesJson))
                    .Select(r =>
                    {
                        var d = JsonConvert.DeserializeObject<Dictionary<string, double>>(r.ResponseValuesJson!);
                        return d != null && d.TryGetValue(primaryResp, out var v) ? v : (double?)null;
                    })
                    .Where(v => v.HasValue)
                    .Select(v => v!.Value)
                    .ToList();

                if (completedValues.Count == 0)
                    return new StopEvaluationResult { ShouldStop = false };

                //  修复: maximize 时取 max，minimize 时取 min
                double bestObs = isMinimize ? completedValues.Min() : completedValues.Max();

                _logger.LogDebug("停止条件: 主响应=\"{Name}\", 方向={Dir}, bestObs={Best}",
                    primaryResp, isMinimize ? "minimize" : "maximize", bestObs);

                // ★ 修复: 传入 maximize 参数（与 Python 端 EI 方向匹配）
                var decision = _gprService.ShouldStop(remaining, bestObs, maximize: !isMinimize);
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
            catch (Exception ex) { _logger.LogError(ex, " SaveGPRModelSafe 失败"); }
        }

        /// <summary> 新增: 保存所有次要 GPR 模型</summary>
        private async Task SaveMultiGPRModelSafe()
        {
            try
            {
                if (_currentBatch != null)
                {
                    await _multiGprService.SaveAllAsync(_currentBatch.FlowId);
                    _logger.LogInformation("SaveMultiGPRModelSafe 成功");
                }
            }
            catch (Exception ex) { _logger.LogError(ex, " SaveMultiGPRModelSafe 失败"); }
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
            // 延迟录入:流程已完成、等结果回填的组也算「已执行」,否则整批成功会被播成「成功 0 组」
            if (_deferResponses)
                completed += _runs.Count(r => r.Status == DOERunStatus.WaitingResponse);
            BatchCompleted?.Invoke(this, new DOEBatchCompletedEventArgs
            { BatchId = _currentBatchId, FinalStatus = status, TotalRuns = _runs.Count, CompletedRuns = completed, StopReason = reason });
            _eventAggregator.GetEvent<DOEBatchCompletedEvent>().Publish(new DOEBatchCompletedPayload
            { BatchId = _currentBatchId, FinalStatus = status, CompletedRuns = completed, StopReason = reason });

            _currentRunIndex = -1;           // ★ 上云: 结束后不高亮任何组
            PublishCloudDoeSnapshot(null);   // ★ 上云: 推送最终运行表与状态
        }

        private class StopEvaluationResult { public bool ShouldStop { get; set; } public string Reason { get; set; } = ""; }
    }
}
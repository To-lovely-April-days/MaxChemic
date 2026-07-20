using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MaxChemical.Logging;
using MaxChemical.Modules.DOE.Data;
using MaxChemical.Modules.DOE.Models;
using MaxChemical.Modules.DOE.Services.Designs;
using Newtonsoft.Json;

namespace MaxChemical.Modules.DOE.Services
{
    /// <summary>
    /// 智能决策执行服务（AutoPilot）— 闭环层
    /// 
    /// 职责:
    ///   1. 接收"一轮实验完成"事件
    ///   2. 调用 ProjectDecisionEngine 分析 + 决策
    ///   3. 执行决策（淘汰因子、扩展范围、推进阶段）
    ///   4. 自动创建下一轮批次 + 生成设计矩阵
    ///   5. 返回决策报告供 UI 展示
    /// 
    /// 调用方: DOEExecutionDashboardViewModel（实验完成后）
    ///         DOEProjectDetailViewModel（手动触发分析）
    /// 
    /// 模式:
    ///   - AutoPilot 关闭: 只做分析和推荐，不自动执行
    ///   - AutoPilot 开启: 分析 → 决策 → 自动执行 → 创建下一轮
    /// </summary>
    public class AutoPilotService
    {
        private readonly IProjectDecisionEngine _decisionEngine;
        private readonly IDOEDesignService _designService;
        private readonly IDOERepository _repository;
        private readonly IDOEAnalysisService _analysisService;
        private readonly ILogService _logger;
        private readonly Prism.Events.IEventAggregator? _events;

        public AutoPilotService(
            IProjectDecisionEngine decisionEngine,
            IDOEDesignService designService,
            IDOERepository repository,
            IDOEAnalysisService analysisService,
            ILogService logger,
            Prism.Events.IEventAggregator eventAggregator)
        {
            _decisionEngine = decisionEngine;
            _designService = designService;
            _repository = repository;
            _analysisService = analysisService;
            _logger = logger?.ForContext<AutoPilotService>()
                      ?? throw new ArgumentNullException(nameof(logger));
            _events = eventAggregator;
        }

        // ══════════════════════════════════════════════════════════════
        //  主入口: 一轮实验完成后调用
        // ══════════════════════════════════════════════════════════════

        /// <summary>
        /// 轮次完成后触发智能决策。
        /// 
        /// 无论 AutoPilot 是否开启，都会执行分析和生成推荐。
        /// 区别在于:
        ///   - AutoPilot 关闭: 返回推荐，等用户手动确认
        ///   - AutoPilot 开启: 自动执行推荐并创建下一轮
        /// </summary>
        /// <returns>决策报告，供 UI 展示</returns>
        public async Task<AutoPilotReport> OnRoundCompletedAsync(string projectId, string batchId)
        {
            _logger.LogInformation("AutoPilot: 轮次完成, Project={ProjectId}, Batch={BatchId}",
                projectId, batchId);

            var config = await _repository.GetOrCreateAutoPilotConfigAsync(projectId);
            var report = new AutoPilotReport { ProjectId = projectId, BatchId = batchId };

            try
            {
                // ── Step 1: 分析 + 决策 ──
                var summary = await _decisionEngine.AnalyzeRoundAsync(projectId, batchId);
                report.RoundSummary = summary;
                report.Recommendation = summary.Recommendation;
                report.RecommendationReason = summary.RecommendationReason ?? "";

                // ── 计算下一轮方案 ──
                var activeFactors = (await _repository.GetActiveProjectFactorsAsync(projectId));
                int nextFactorCount = activeFactors.Count;

                // 如果有因子要淘汰，先算淘汰后的数量
                var recentLog = (await _repository.GetRecentAutoPilotLogsAsync(projectId, 1)).FirstOrDefault();
                if (recentLog != null && !string.IsNullOrEmpty(recentLog.DecisionDetailJson))
                {
                    try
                    {
                        var detail = JsonConvert.DeserializeObject<Dictionary<string, object>>(recentLog.DecisionDetailJson);
                        if (detail?.TryGetValue("screened_out", out var so) == true)
                        {
                            var screenedOut = JsonConvert.DeserializeObject<List<string>>(so.ToString()!);
                            if (screenedOut != null)
                                nextFactorCount -= screenedOut.Count;
                        }
                    }
                    catch { }
                }

                var nextPhase = summary.NextPhase ?? recentLog?.NextPhase ?? summary.Phase;
                var nextMethod = _decisionEngine.RecommendDesignMethod(
                    summary.Recommendation, Math.Max(nextFactorCount, 1), nextPhase);

                report.NextDesignMethod = nextMethod;
                report.NextFactorCount = nextFactorCount;
                report.EstimatedRuns = ((ProjectDecisionEngine)_decisionEngine)
                    .EstimateRunCount(nextMethod, nextFactorCount);
                report.NextPhase = nextPhase;

                // ── AutoPilot 开启时自动执行 ──
                if (config.Enabled && !report.IsSafeguardTriggered)
                {
                    if (summary.Recommendation != NextStepRecommendation.UserDecision
                        && summary.Recommendation != NextStepRecommendation.Complete)
                    {
                        var nextBatchId = await ExecuteDecisionAsync(
                            projectId, batchId, summary, config, nextMethod, nextPhase);
                        report.NextBatchId = nextBatchId;
                        report.AutoExecuted = true;

                        // 回填日志
                        if (recentLog != null && !string.IsNullOrEmpty(nextBatchId))
                        {
                            await _repository.UpdateAutoPilotLogNextBatchAsync(recentLog.Id, nextBatchId);
                        }
                    }
                    else if (summary.Recommendation == NextStepRecommendation.Complete)
                    {
                        // 优化完成
                        await _decisionEngine.AdvancePhaseAsync(projectId, DOEProjectPhase.Completed);
                        report.ProjectCompleted = true;
                    }
                }

                report.Success = true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "AutoPilot 决策执行失败");
                report.Success = false;
                report.Error = ex.Message;
            }

            return report;
        }

        // ══════════════════════════════════════════════════════════════
        //  执行决策: 淘汰因子 → 调范围 → 推进阶段 → 创建下一轮
        // ══════════════════════════════════════════════════════════════

        /// <summary>
        /// 执行决策引擎的推荐，返回新创建的批次 ID
        /// </summary>
        private async Task<string?> ExecuteDecisionAsync(
            string projectId, string batchId,
            DOERoundSummary summary, AutoPilotConfig config,
            DOEDesignMethod nextMethod, DOEProjectPhase nextPhase,
            bool autoTriggered = true)
        {
            var project = await _repository.GetProjectWithDetailsAsync(projectId);
            if (project == null) return null;

            // ── 1. 执行因子淘汰 ──
            await ExecuteFactorScreeningAsync(projectId, batchId, summary, config);

            // ── 2. 执行范围扩展 ──
            await ExecuteRangeExpansionAsync(projectId, batchId, summary, config);

            // ── 3. 推进阶段 ──
            await _decisionEngine.AdvancePhaseAsync(projectId, nextPhase);

            // ── 4. 创建下一轮批次 ──
            var nextBatchId = await CreateNextRoundBatchAsync(
                project, summary, nextMethod, nextPhase, config);

            // ── 5. 广播建轮结果 ──
            // 自动建轮不许静默:值守要接管播报与托管延续,否则小桐(和用户)都不知道新批次的存在
            if (!string.IsNullOrEmpty(nextBatchId))
                await PublishNextRoundCreatedAsync(project, batchId, nextBatchId!, summary, nextMethod, autoTriggered);

            return nextBatchId;
        }

        /// <summary>发布下一轮批次创建事件(失败只记日志,不影响建轮主流程)。</summary>
        private async Task PublishNextRoundCreatedAsync(
            DOEProject project, string sourceBatchId, string nextBatchId,
            DOERoundSummary summary, DOEDesignMethod nextMethod, bool autoTriggered)
        {
            try
            {
                int runCount = 0;
                try { runCount = (await _repository.GetRunsAsync(nextBatchId)).Count; }
                catch { }

                _events?.GetEvent<Events.AutoPilotBatchCreatedEvent>().Publish(new Events.AutoPilotBatchCreatedPayload
                {
                    ProjectId = project.ProjectId,
                    ProjectName = project.ProjectName ?? project.ProjectId,
                    SourceBatchId = sourceBatchId,
                    NextBatchId = nextBatchId,
                    NextRoundNumber = summary.RoundNumber + 1,
                    DesignMethodDisplay = nextMethod switch
                    {
                        DOEDesignMethod.PlackettBurman => "Plackett-Burman 筛选",
                        DOEDesignMethod.FractionalFactorial => "部分因子设计",
                        DOEDesignMethod.FullFactorial => "全因子设计",
                        DOEDesignMethod.CCD => "中心复合设计 (CCD)",
                        DOEDesignMethod.BoxBehnken => "Box-Behnken 设计",
                        DOEDesignMethod.DOptimal => "D-Optimal 设计",
                        DOEDesignMethod.SteepestAscent => "最速上升实验",
                        DOEDesignMethod.ConfirmationRuns => "验证实验",
                        _ => nextMethod.ToString()
                    },
                    RunCount = runCount,
                    AutoTriggered = autoTriggered
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning("AutoPilot 建轮广播失败(不影响建轮): {Msg}", ex.Message);
            }
        }

        /// <summary>
        /// 执行因子淘汰（从决策日志中读取要淘汰的因子）
        /// </summary>
        private async Task ExecuteFactorScreeningAsync(
            string projectId, string batchId,
            DOERoundSummary summary, AutoPilotConfig config)
        {
            if (summary.Recommendation != NextStepRecommendation.ContinueScreening
                && summary.Recommendation != NextStepRecommendation.StartRSM
                && summary.Recommendation != NextStepRecommendation.SteepestAscent)
                return;

            // 从最新日志获取要淘汰的因子
            var recentLog = (await _repository.GetRecentAutoPilotLogsAsync(projectId, 1)).FirstOrDefault();
            if (recentLog == null || string.IsNullOrEmpty(recentLog.DecisionDetailJson)) return;

            try
            {
                var detail = JsonConvert.DeserializeObject<Dictionary<string, object>>(recentLog.DecisionDetailJson);
                if (detail?.TryGetValue("screened_out", out var so) != true) return;

                var screenedOut = JsonConvert.DeserializeObject<List<string>>(so.ToString()!);
                if (screenedOut == null || screenedOut.Count == 0) return;

                foreach (var factorName in screenedOut)
                {
                    // 查找该因子的 p 值（用于记录理由）
                    string reason = $"AutoPilot Round {summary.RoundNumber}: p ≥ {config.AlphaScreen}，不显著";
                    await _decisionEngine.ScreenOutFactorsAsync(
                        projectId, new List<string> { factorName }, batchId, reason);
                }

                _logger.LogInformation("AutoPilot: 淘汰因子 [{Factors}]", string.Join(", ", screenedOut));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "因子淘汰执行失败");
            }
        }

        /// <summary>
        /// 执行范围扩展（最优点在边界上时）
        /// </summary>
        private async Task ExecuteRangeExpansionAsync(
      string projectId, string batchId,
      DOERoundSummary summary, AutoPilotConfig config)
        {
            if (summary.Recommendation != NextStepRecommendation.ExpandRange) return;
            if (string.IsNullOrEmpty(summary.OptimalBoundaryStatusJson)) return;

            var boundaryStatus = JsonConvert.DeserializeObject<Dictionary<string, string>>(
                summary.OptimalBoundaryStatusJson);
            if (boundaryStatus == null) return;

            var activeFactors = await _repository.GetActiveProjectFactorsAsync(projectId);
            double ratio = config.RangeExpansionRatio;

            foreach (var kv in boundaryStatus.Where(x => x.Value == "at_lower" || x.Value == "at_upper"))
            {
                var pf = activeFactors.FirstOrDefault(f => f.FactorName == kv.Key);
                if (pf == null || pf.IsCategorical) continue;

                double range = pf.CurrentUpperBound - pf.CurrentLowerBound;
                double expansion = range * ratio;
                double newLower = pf.CurrentLowerBound;
                double newUpper = pf.CurrentUpperBound;

                if (kv.Value == "at_lower")
                {
                    newLower -= expansion;
                    // ★ 物理下限约束
                    if (pf.PhysicalLowerLimit.HasValue && newLower < pf.PhysicalLowerLimit.Value)
                        newLower = pf.PhysicalLowerLimit.Value;
                }
                else
                {
                    newUpper += expansion;
                    // ★ 物理上限约束
                    if (pf.PhysicalUpperLimit.HasValue && newUpper > pf.PhysicalUpperLimit.Value)
                        newUpper = pf.PhysicalUpperLimit.Value;
                }

                // ★ 如果约束后没有实际变化，跳过
                if (Math.Abs(newLower - pf.CurrentLowerBound) < 1e-6
                    && Math.Abs(newUpper - pf.CurrentUpperBound) < 1e-6)
                {
                    _logger.LogInformation("AutoPilot: 因子 {Factor} 已达物理极限，跳过扩展", kv.Key);
                    continue;
                }

                string reason = $"AutoPilot Round {summary.RoundNumber}: " +
                                 $"最优点在{(kv.Value == "at_lower" ? "下" : "上")}界，扩展 {ratio * 100:F0}%";
                await _decisionEngine.UpdateFactorBoundsAsync(
                    projectId, kv.Key, newLower, newUpper, batchId, reason);

                _logger.LogInformation("AutoPilot: 扩展因子 {Factor} 范围 [{Old}] → [{New}]",
                    kv.Key,
                    $"{pf.CurrentLowerBound:F2}, {pf.CurrentUpperBound:F2}",
                    $"{newLower:F2}, {newUpper:F2}");
            }
        }

        // ══════════════════════════════════════════════════════════════
        //  创建下一轮批次
        // ══════════════════════════════════════════════════════════════

        /// <summary>
        /// 自动创建下一轮批次：因子 + 响应 + 设计矩阵 + 运行记录
        /// </summary>
        private async Task<string?> CreateNextRoundBatchAsync(
            DOEProject project, DOERoundSummary currentSummary,
            DOEDesignMethod nextMethod, DOEProjectPhase nextPhase,
            AutoPilotConfig config)
        {
            try
            {
                int nextRound = currentSummary.RoundNumber + 1;

                // ── 1. 获取活跃因子（淘汰后的） ──
                var nextFactors = await _decisionEngine.GetNextRoundFactorsAsync(project.ProjectId);
                if (nextFactors.Count == 0)
                {
                    _logger.LogWarning("AutoPilot: 无活跃因子，无法创建下一轮");
                    return null;
                }

                // ── 2. 继承上一轮的响应定义 ──
                var previousBatch = await _repository.GetBatchWithDetailsAsync(currentSummary.BatchId);
                var responses = previousBatch?.Responses ?? new List<DOEResponse>();

                // ── 3. 生成设计矩阵 ──
                DOEDesignMatrix? matrix = null;
                string? designConfigJson = null;

                if (nextMethod == DOEDesignMethod.ConfirmationRuns)
                {
                    // 验证实验: 在最优点做 N 次重复
                    matrix = GenerateConfirmationMatrix(
                        nextFactors, currentSummary, config.ConfirmationReps);
                    designConfigJson = JsonConvert.SerializeObject(new
                    {
                        olsModelType = "quadratic",
                        confirmationReps = config.ConfirmationReps
                    });
                }
                else if (nextMethod == DOEDesignMethod.SteepestAscent)
                {
                    // 爬坡: 需要上一轮的系数
                    matrix = await GenerateSteepestAscentMatrixAsync(
                        nextFactors, currentSummary);
                    designConfigJson = JsonConvert.SerializeObject(new
                    {
                        olsModelType = "linear"
                    });
                }
                else
                {
                    // 常规设计
                    var options = BuildDesignOptions(nextMethod, nextFactors.Count);
                    matrix = await _designService.GenerateAsync(nextFactors, options);
                    designConfigJson = BuildDesignConfigJson(nextMethod, options);
                }

                if (matrix == null || matrix.RunCount == 0)
                {
                    _logger.LogWarning("AutoPilot: 设计矩阵生成失败");
                    return null;
                }

                // ── 3.5 化学家坐标系跨轮继承:上一轮带 factorTransform 则必须随批次带到下一轮,
                //        否则 τ/eq 会被执行器归入"手动操作"静默吞掉(泵速永远不被设置)。
                //        因子被引擎淘汰或新矩阵反算不可行时,不自动建轮,转人工处理。
                var transform = FactorTransform.ExtractFromDesignConfig(previousBatch?.DesignConfigJson);
                if (transform != null)
                {
                    var nextNames = nextFactors.Select(f => f.FactorName).ToHashSet();
                    bool tauOk = nextNames.Contains(transform.TauFactor);
                    bool eqOk = string.IsNullOrEmpty(transform.EqFactor) || nextNames.Contains(transform.EqFactor);
                    if (!tauOk || !eqOk)
                    {
                        _logger.LogWarning("AutoPilot: 化学家坐标系因子({Tau}/{Eq})已不在下一轮活跃因子中,自动建轮中止,转人工处理",
                            transform.TauFactor, transform.EqFactor ?? "-");
                        return null;
                    }

                    for (int ri = 0; ri < matrix.RunCount; ri++)
                    {
                        var vals = new Dictionary<string, double>();
                        foreach (var fname in matrix.FactorNames)
                        {
                            if (!matrix.Rows[ri].TryGetValue(fname, out var v)) continue;
                            if (v is double d) vals[fname] = d;
                            else if (v is long l) vals[fname] = l;
                            else if (double.TryParse(v?.ToString(), out var p)) vals[fname] = p;
                        }
                        string terr = FactorTransform.SolveDeviceValues(transform, vals, out _);
                        if (terr != null)
                        {
                            _logger.LogWarning("AutoPilot: 下一轮第 {Row} 组化学家坐标反算不可行({Err}),自动建轮中止,转人工处理",
                                ri + 1, terr);
                            return null;
                        }
                    }

                    designConfigJson = FactorTransform.MergeIntoDesignConfig(designConfigJson, transform);
                }

                // ── 4. 创建批次 ──
                var batch = new DOEBatch
                {
                    FlowId = project.FlowId ?? "",
                    FlowName = project.FlowName ?? "",
                    BatchName = $"R{nextRound} {nextPhase} ({nextMethod})",
                    DesignMethod = nextMethod,
                    Status = DOEBatchStatus.Ready,
                    DesignConfigJson = designConfigJson,
                    ProjectId = project.ProjectId,
                    RoundNumber = nextRound,
                    ProjectPhase = nextPhase
                };

                var batchId = await _repository.CreateBatchAsync(batch);

                // ── 5. 保存因子定义 ──
                foreach (var f in nextFactors) f.BatchId = batchId;
                await _repository.SaveFactorsAsync(batchId, nextFactors);

                // ── 6. 保存响应定义 ──
                var newResponses = responses.Select(r => new DOEResponse
                {
                    BatchId = batchId,
                    ResponseName = r.ResponseName,
                    Unit = r.Unit,
                    CollectionMethod = r.CollectionMethod,
                    SortOrder = r.SortOrder
                }).ToList();
                await _repository.SaveResponsesAsync(batchId, newResponses);

                // ── 7. 保存运行记录（Pending 状态） ──
                var runs = new List<DOERunRecord>();
                for (int i = 0; i < matrix.RunCount; i++)
                {
                    runs.Add(new DOERunRecord
                    {
                        BatchId = batchId,
                        RunIndex = i + 1,
                        FactorValuesJson = JsonConvert.SerializeObject(matrix.Rows[i]),
                        Status = DOERunStatus.Pending,
                        DataSource = DOEDataSource.Measured
                    });
                }
                await _repository.SaveRunsAsync(runs);

                _logger.LogInformation(
                    "AutoPilot: 创建下一轮 BatchId={BatchId}, Method={Method}, Runs={Runs}",
                    batchId, nextMethod, matrix.RunCount);

                return batchId;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "AutoPilot: 创建下一轮批次失败");
                return null;
            }
        }

        // ══════════════════════════════════════════════════════════════
        //  设计矩阵生成辅助
        // ══════════════════════════════════════════════════════════════

        /// <summary>
        /// 验证实验矩阵: 在最优点做 N 次重复
        /// </summary>
        private DOEDesignMatrix GenerateConfirmationMatrix(
            List<DOEFactor> factors, DOERoundSummary summary, int reps)
        {
            var matrix = new DOEDesignMatrix
            {
                FactorNames = factors.Select(f => f.FactorName).ToList()
            };

            // 解析最优因子值
            Dictionary<string, object>? bestFactors = null;
            if (!string.IsNullOrEmpty(summary.BestFactorsJson))
            {
                bestFactors = JsonConvert.DeserializeObject<Dictionary<string, object>>(
                    summary.BestFactorsJson);
            }

            // 如果没有最优值，用中心点
            if (bestFactors == null || bestFactors.Count == 0)
            {
                bestFactors = new Dictionary<string, object>();
                foreach (var f in factors)
                {
                    if (f.IsCategorical)
                    {
                        var levels = f.GetCategoryLevelList();
                        bestFactors[f.FactorName] = levels.FirstOrDefault() ?? "";
                    }
                    else
                    {
                        bestFactors[f.FactorName] = (f.LowerBound + f.UpperBound) / 2.0;
                    }
                }
            }

            // 生成 N 次重复
            for (int i = 0; i < reps; i++)
            {
                matrix.Rows.Add(new Dictionary<string, object>(bestFactors));
            }

            return matrix;
        }

        /// <summary>
        /// 最速上升矩阵: 从上一轮 OLS 系数生成
        /// </summary>
        private async Task<DOEDesignMatrix?> GenerateSteepestAscentMatrixAsync(
            List<DOEFactor> factors, DOERoundSummary summary)
        {
            try
            {
                // 从上一轮的 OLS 结果提取系数
                var olsJson = await _repository.GetOlsResultJsonAsync(
                    summary.BatchId,
                    (await _repository.GetBatchWithDetailsAsync(summary.BatchId))
                        ?.Responses?.FirstOrDefault()?.ResponseName ?? "");

                if (string.IsNullOrEmpty(olsJson)) return null;

                var olsResult = JsonConvert.DeserializeObject<OLSAnalysisResult>(olsJson);
                if (olsResult?.Coefficients == null) return null;

                // 提取主效应系数
                var coefficients = new Dictionary<string, double>();
                foreach (var c in olsResult.Coefficients)
                {
                    if (c.Term == "Intercept" || c.Term == "Ct Pt") continue;
                    if (c.Term.Contains(":") || c.Term.Contains("²") || c.Term.Contains("×")) continue;
                    // 去掉类别因子标记（如 "催化剂类型[A]" → 跳过）
                    if (c.Term.Contains("[")) continue;

                    coefficients[c.Term] = c.Coefficient;
                }

                if (coefficients.Count == 0) return null;

                // 调用 DesignService 生成爬坡矩阵
                var designService = _designService as DOEDesignService;
                if (designService != null)
                {
                    return await designService.GenerateSteepestAscentAsync(
                        factors, coefficients, stepCount: 8);
                }

                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "生成最速上升矩阵失败");
                return null;
            }
        }

        /// <summary>
        /// 根据设计方法构建 DesignOptions
        /// </summary>
        private DesignOptions BuildDesignOptions(DOEDesignMethod method, int factorCount)
        {
            return method switch
            {
                DOEDesignMethod.FullFactorial => new FullFactorialOptions
                {
                    CenterPointCount = 4,
                    Randomize = true
                },

                DOEDesignMethod.FractionalFactorial => new FractionalFactorialOptions
                {
                    RunCount = factorCount switch
                    {
                        <= 4 => 8,
                        <= 5 => 16,
                        <= 7 => 16,
                        _ => 32
                    },
                    CenterPointCount = 4,
                    Randomize = true
                },

                DOEDesignMethod.PlackettBurman => new PlackettBurmanOptions
                {
                    RunCount = factorCount switch
                    {
                        <= 7 => 12,
                        <= 11 => 12,
                        <= 19 => 20,
                        _ => 24
                    },
                    Randomize = true
                },

                DOEDesignMethod.CCD => new CCDOptions
                {
                    AlphaType = "rotatable",
                    CubeCenterCount = -1,
                    AxialCenterCount = -1,
                    UseFractional = factorCount >= 5,
                    Randomize = true
                },

                DOEDesignMethod.BoxBehnken => new BoxBehnkenOptions
                {
                    BbdCenterCount = -1,
                    Randomize = true
                },

                DOEDesignMethod.DOptimal => new DOptimalOptions
                {
                    RunCount = Math.Max(factorCount * 3 + 5, 15),
                    ModelType = "quadratic",
                    Restarts = 20,
                    Randomize = true
                },

                _ => new FullFactorialOptions { Randomize = true }
            };
        }

        /// <summary>
        /// 构建 DesignConfigJson（存入批次元数据）
        /// </summary>
        private string BuildDesignConfigJson(DOEDesignMethod method, DesignOptions options)
        {
            var config = new Dictionary<string, object>
            {
                ["autoGenerated"] = true,
                ["randomized"] = options.Randomize,
                ["centerPoints"] = options.CenterPointCount
            };

            switch (method)
            {
                case DOEDesignMethod.FractionalFactorial:
                    if (options is FractionalFactorialOptions ffo)
                    {
                        config["olsModelType"] = "interaction";
                        config["fractionalRunCount"] = ffo.RunCount;
                    }
                    break;

                case DOEDesignMethod.CCD:
                    config["olsModelType"] = "quadratic";
                    if (options is CCDOptions ccd)
                    {
                        config["alphaType"] = ccd.AlphaType;
                        config["useFractional"] = ccd.UseFractional;
                    }
                    break;

                case DOEDesignMethod.BoxBehnken:
                    config["olsModelType"] = "quadratic";
                    break;

                case DOEDesignMethod.PlackettBurman:
                    config["olsModelType"] = "linear";
                    break;

                default:
                    config["olsModelType"] = "quadratic";
                    break;
            }

            return JsonConvert.SerializeObject(config);
        }

        // ══════════════════════════════════════════════════════════════
        //  用户手动确认/覆盖
        // ══════════════════════════════════════════════════════════════

        /// <summary>
        /// 用户确认系统推荐（AutoPilot 关闭时用）
        /// 执行推荐方案并创建下一轮
        /// </summary>
        public async Task<string?> AcceptRecommendationAsync(string projectId, string batchId)
        {
            var summary = await _repository.GetRoundSummaryByBatchAsync(batchId);
            if (summary == null) return null;

            var config = await _repository.GetOrCreateAutoPilotConfigAsync(projectId);
            var project = await _repository.GetProjectWithDetailsAsync(projectId);
            if (project == null) return null;

            var activeFactors = await _repository.GetActiveProjectFactorsAsync(projectId);
            var nextMethod = _decisionEngine.RecommendDesignMethod(
                summary.Recommendation, activeFactors.Count, summary.Phase);
            var nextPhase = summary.Recommendation switch
            {
                NextStepRecommendation.StartRSM => DOEProjectPhase.RSM,
                NextStepRecommendation.SteepestAscent => DOEProjectPhase.PathSearch,
                NextStepRecommendation.ConfirmationRuns => DOEProjectPhase.Confirmation,
                NextStepRecommendation.Complete => DOEProjectPhase.Completed,
                _ => summary.Phase
            };

            return await ExecuteDecisionAsync(
                projectId, batchId, summary, config, nextMethod, nextPhase, autoTriggered: false);
        }

        /// <summary>
        /// 用户覆盖系统推荐（选择不同的操作）
        /// </summary>
        public async Task<string?> OverrideRecommendationAsync(
            string projectId, string batchId,
            NextStepRecommendation userDecision, string? note)
        {
            // 更新日志
            var logs = await _repository.GetRecentAutoPilotLogsAsync(projectId, 1);
            var latestLog = logs.FirstOrDefault(l => l.BatchId == batchId);
            if (latestLog != null)
            {
                await _repository.UpdateAutoPilotLogOverrideAsync(
                    latestLog.Id, userDecision, note);
            }

            // 如果用户选了 Complete，直接完成
            if (userDecision == NextStepRecommendation.Complete)
            {
                await _decisionEngine.AdvancePhaseAsync(projectId, DOEProjectPhase.Completed);
                return null;
            }

            // 按用户选择执行
            var summary = await _repository.GetRoundSummaryByBatchAsync(batchId);
            if (summary == null) return null;

            var config = await _repository.GetOrCreateAutoPilotConfigAsync(projectId);
            var activeFactors = await _repository.GetActiveProjectFactorsAsync(projectId);
            var nextMethod = _decisionEngine.RecommendDesignMethod(
                userDecision, activeFactors.Count, summary.Phase);
            var nextPhase = userDecision switch
            {
                NextStepRecommendation.StartRSM => DOEProjectPhase.RSM,
                NextStepRecommendation.SteepestAscent => DOEProjectPhase.PathSearch,
                NextStepRecommendation.ConfirmationRuns => DOEProjectPhase.Confirmation,
                _ => summary.Phase
            };

            // 覆盖 summary 的推荐，然后执行
            summary.Recommendation = userDecision;
            return await ExecuteDecisionAsync(
                projectId, batchId, summary, config, nextMethod, nextPhase, autoTriggered: false);
        }
    }

    // ══════════════════════════════════════════════════════════════
    //  决策报告（返回给 UI）
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// AutoPilot 决策报告 — 每轮完成后返回给 UI 展示
    /// </summary>
    public class AutoPilotReport
    {
        public string ProjectId { get; set; } = "";
        public string BatchId { get; set; } = "";
        public bool Success { get; set; }
        public string? Error { get; set; }

        // ── 分析结果 ──
        public DOERoundSummary? RoundSummary { get; set; }

        // ── 系统推荐 ──
        public NextStepRecommendation Recommendation { get; set; }
        public string RecommendationReason { get; set; } = "";

        // ── 下一轮方案 ──
        public DOEDesignMethod NextDesignMethod { get; set; }
        public DOEProjectPhase NextPhase { get; set; }
        public int NextFactorCount { get; set; }
        public int EstimatedRuns { get; set; }

        // ── 自动执行结果 ──
        public bool AutoExecuted { get; set; }
        public string? NextBatchId { get; set; }
        public bool ProjectCompleted { get; set; }

        // ── 防护规则 ──
        public bool IsSafeguardTriggered =>
            RoundSummary?.Recommendation == NextStepRecommendation.UserDecision
            && !string.IsNullOrEmpty(RoundSummary?.RecommendationReason);

        // ── UI 显示用 ──
        public string NextDesignMethodDisplay => NextDesignMethod switch
        {
            DOEDesignMethod.PlackettBurman => "Plackett-Burman 筛选",
            DOEDesignMethod.FractionalFactorial => "部分因子设计",
            DOEDesignMethod.FullFactorial => "全因子设计",
            DOEDesignMethod.CCD => "中心复合设计 (CCD)",
            DOEDesignMethod.BoxBehnken => "Box-Behnken 设计",
            DOEDesignMethod.DOptimal => "D-Optimal 设计",
            DOEDesignMethod.SteepestAscent => "最速上升实验",
            DOEDesignMethod.ConfirmationRuns => "验证实验",
            _ => NextDesignMethod.ToString()
        };

        public string NextPhaseDisplay => NextPhase switch
        {
            DOEProjectPhase.Screening => "筛选阶段",
            DOEProjectPhase.PathSearch => "路径探索",
            DOEProjectPhase.RSM => "响应面优化",
            DOEProjectPhase.Confirmation => "验证确认",
            DOEProjectPhase.Completed => "优化完成",
            _ => NextPhase.ToString()
        };
    }
}
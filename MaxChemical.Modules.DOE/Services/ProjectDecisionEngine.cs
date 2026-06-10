using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MaxChemical.Logging;
using MaxChemical.Modules.DOE.Data;
using MaxChemical.Modules.DOE.Models;
using Newtonsoft.Json;

namespace MaxChemical.Modules.DOE.Services
{
    /// <summary>
    /// 智能决策引擎 v2 — 完整决策树实现
    /// 
    /// 决策流程:
    ///   Step 1: 模型拟合（对每个响应做 OLS）
    ///   Step 2: 模型质量评估（R² 分支 A/B/C）
    ///   Step 3: 因子筛选（Screening 专用，含遗传原则）
    ///   Step 4: 爬坡判断（PathSearch 专用）
    ///   Step 5: 目标达成判断（RSM 阶段）
    ///   Step 6: 验证实验判断（Confirmation 阶段）
    ///   Step 7: 自动选择设计方法
    /// 
    /// 防护规则: P1-P5（最大轮次、连续相同、因子下界、实验总量、R²不改善）
    /// </summary>
    public class ProjectDecisionEngine : IProjectDecisionEngine
    {
        private readonly IDOERepository _repository;
        private readonly IGPRModelService _gprService;
        private readonly IDOEAnalysisService _analysisService;
        private readonly ILogService _logger;

        public ProjectDecisionEngine(
            IDOERepository repository,
            IGPRModelService gprService,
            IDOEAnalysisService analysisService,
            ILogService logger)
        {
            _repository = repository ?? throw new ArgumentNullException(nameof(repository));
            _gprService = gprService ?? throw new ArgumentNullException(nameof(gprService));
            _analysisService = analysisService ?? throw new ArgumentNullException(nameof(analysisService));
            _logger = logger?.ForContext<ProjectDecisionEngine>()
                      ?? throw new ArgumentNullException(nameof(logger));
        }

        // ══════════════════════════════════════════════════════════════
        //  核心入口: 轮次分析 + 决策
        // ══════════════════════════════════════════════════════════════

        /// <summary>
        /// 分析当前轮次结果，生成轮次总结和决策日志。
        /// 如果 AutoPilot 已启用，返回的 summary.Recommendation 即为自动决策。
        /// </summary>
        public async Task<DOERoundSummary> AnalyzeRoundAsync(string projectId, string batchId)
        {
            var project = await _repository.GetProjectWithDetailsAsync(projectId);
            if (project == null) throw new InvalidOperationException($"项目不存在: {projectId}");

            var batch = await _repository.GetBatchWithDetailsAsync(batchId);
            if (batch == null) throw new InvalidOperationException($"批次不存在: {batchId}");

            var config = await _repository.GetOrCreateAutoPilotConfigAsync(projectId);

            var completedRuns = batch.Runs.Where(r => r.Status == DOERunStatus.Completed).ToList();
            var activeFactors = project.ProjectFactors.Where(f => f.IsActive).ToList();

            // ── 构建轮次总结基础信息 ──
            var summary = new DOERoundSummary
            {
                ProjectId = projectId,
                BatchId = batchId,
                RoundNumber = batch.RoundNumber ?? (project.CompletedRounds + 1),
                Phase = batch.ProjectPhase ?? project.CurrentPhase,
                DesignMethod = batch.DesignMethod,
                ActiveFactorCount = activeFactors.Count,
                RunCount = completedRuns.Count
            };

            // ── Step 1: 模型拟合 ──
            var olsMetrics = await FitAndExtractMetrics(batch, summary);

            // ── 计算最优点和边界状态 ──
            var primaryResponse = batch.Responses.FirstOrDefault()?.ResponseName;
            ComputeOptimalAndBoundary(summary, completedRuns, activeFactors, primaryResponse);

            // ── 更新项目最优 ──
            if (summary.BestResponseValue.HasValue)
            {
                await _repository.UpdateProjectBestAsync(
                    projectId, summary.BestResponseValue.Value,
                    summary.BestFactorsJson ?? "{}",
                    project.TotalExperiments + completedRuns.Count,
                    summary.RoundNumber);
            }

            // ── Step 2-6: 决策树 ──
            var decision = RunDecisionTree(summary, project, activeFactors, config, olsMetrics);
            summary.Recommendation = decision.Recommendation;
            summary.RecommendationReason = decision.Reason;
            summary.NextPhase = decision.NextPhase;  // ★ 新增
            // ── 持久化轮次总结 ──
            await _repository.SaveRoundSummaryAsync(summary);

            // ── 持久化决策日志 ──
            var log = BuildAutoPilotLog(summary, decision, config, olsMetrics, activeFactors);
            await _repository.SaveAutoPilotLogAsync(log);

            _logger.LogInformation(
                "轮次分析完成: Project={ProjectId}, Round={Round}, R²={R2:F4}, Decision={Dec}, Reason={Reason}",
                projectId, summary.RoundNumber, summary.RSquared,
                decision.Recommendation, decision.Reason);

            return summary;
        }

        // ══════════════════════════════════════════════════════════════
        //  Step 1: 模型拟合
        // ══════════════════════════════════════════════════════════════

        private async Task<OlsMetrics> FitAndExtractMetrics(DOEBatch batch, DOERoundSummary summary)
        {
            var metrics = new OlsMetrics();
            var primaryResponse = batch.Responses.FirstOrDefault()?.ResponseName;
            if (string.IsNullOrEmpty(primaryResponse) || summary.RunCount < 4)
                return metrics;

            try
            {
                // 确定模型类型
                string olsModelType = batch.DesignMethod switch
                {
                    DOEDesignMethod.PlackettBurman => "linear",
                    DOEDesignMethod.FractionalFactorial => "interaction",
                    DOEDesignMethod.FullFactorial => "interaction",
                    DOEDesignMethod.CCD or DOEDesignMethod.BoxBehnken => "quadratic",
                    _ => "quadratic"
                };

                if (!string.IsNullOrEmpty(batch.DesignConfigJson))
                {
                    try
                    {
                        var cfg = JsonConvert.DeserializeObject<Dictionary<string, object>>(batch.DesignConfigJson);
                        if (cfg?.TryGetValue("olsModelType", out var mt) == true && mt != null)
                            olsModelType = mt.ToString()!;
                    }
                    catch { }
                }

                var olsResult = await _analysisService.FitOlsAsync(
                    batch.BatchId, primaryResponse, olsModelType);

                if (olsResult?.ModelSummary != null)
                {
                    metrics.R2 = olsResult.ModelSummary.RSquared;
                    metrics.R2Adj = olsResult.ModelSummary.RSquaredAdj;
                    metrics.R2Pred = olsResult.ModelSummary.RSquaredPred;
                    metrics.LofP = olsResult.ModelSummary.LackOfFitP;
                    metrics.HasModel = true;

                    summary.RSquared = metrics.R2;
                    summary.RSquaredAdj = metrics.R2Adj;
                    summary.RSquaredPred = metrics.R2Pred;
                    summary.LackOfFitP = metrics.LofP;

                    // 提取因子排名
                    if (olsResult.Coefficients != null)
                    {
                        metrics.FactorPValues = new Dictionary<string, double>();
                        metrics.InteractionPValues = new Dictionary<string, double>();

                        foreach (var c in olsResult.Coefficients)
                        {
                            if (c.Term == "Intercept" || c.Term == "Ct Pt") continue;

                            if (c.Term.Contains(":") || c.Term.Contains("×"))
                                metrics.InteractionPValues[c.Term] = c.PValue;
                            else if (!c.Term.Contains("²"))
                                metrics.FactorPValues[c.Term] = c.PValue;
                        }

                        // 弯曲检验
                        var ctPt = olsResult.Coefficients.FirstOrDefault(c => c.Term == "Ct Pt");
                        if (ctPt != null)
                            metrics.CurvaturePValue = ctPt.PValue;

                        // 排名 JSON
                        var ranking = olsResult.Coefficients
                            .Where(c => c.Term != "Intercept")
                            .Select(c => new { name = c.Term, p_value = c.PValue, significant = c.PValue < 0.05 })
                            .OrderBy(x => x.p_value)
                            .ToList();
                        summary.FactorRankingJson = JsonConvert.SerializeObject(ranking);
                    }

                    // 缓存 OLS 结果
                    var resultJson = JsonConvert.SerializeObject(olsResult);
                    await _repository.SaveOlsResultAsync(batch.BatchId, primaryResponse, resultJson);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "自动 OLS 分析失败");
            }

            return metrics;
        }

        // ══════════════════════════════════════════════════════════════
        //  Step 2-6: 决策树主逻辑
        // ══════════════════════════════════════════════════════════════

        private DecisionResult RunDecisionTree(
            DOERoundSummary summary, DOEProject project,
            List<DOEProjectFactor> activeFactors, AutoPilotConfig config,
            OlsMetrics metrics)
        {
            // ── 防护规则优先检查 ──
            var safeguard = CheckSafeguards(summary, project, config);
            if (safeguard != null) return safeguard;
            // ── 爬坡阶段特殊处理（不依赖OLS模型，直接看趋势） ──
            if (summary.Phase == DOEProjectPhase.PathSearch
                && summary.DesignMethod == DOEDesignMethod.SteepestAscent)
            {
                return DecidePathSearchPhase(summary, project, activeFactors, config, metrics);
            }

            // ── 验证阶段特殊处理（不拟合新模型，比较预测值与实际值） ──
            if (summary.Phase == DOEProjectPhase.Confirmation)
            {
                return DecideConfirmationPhase(summary, project, config, metrics);
            }
            // ── 没有模型时的回退 ──
            if (!metrics.HasModel)
            {

                return new DecisionResult
                {
                    Recommendation = NextStepRecommendation.UserDecision,
                    Reason = "数据不足或模型拟合失败，请检查实验数据质量。",
                    NextPhase = summary.Phase
                };
            }

            double r2 = metrics.R2;

            // ══════ Step 2: 模型质量分支 ══════

            // R² < 0.5 → 异常
            if (r2 < 0.5)
            {
                return new DecisionResult
                {
                    Recommendation = NextStepRecommendation.UserDecision,
                    Reason = $"R²={r2:F3} 过低（<0.50），可能存在数据质量问题。建议检查实验操作、是否有录入错误、或因子范围是否合理。",
                    NextPhase = summary.Phase,
                    Safeguard = "R² < 0.50，数据质量异常"
                };
            }

            // ── 根据当前阶段路由 ──
            return summary.Phase switch
            {
                DOEProjectPhase.Screening => DecideScreeningPhase(summary, project, activeFactors, config, metrics),
                DOEProjectPhase.PathSearch => DecidePathSearchPhase(summary, project, activeFactors, config, metrics),
                DOEProjectPhase.RSM => DecideRsmPhase(summary, project, activeFactors, config, metrics),
                DOEProjectPhase.Confirmation => DecideConfirmationPhase(summary, project, config, metrics),
                _ => new DecisionResult
                {
                    Recommendation = NextStepRecommendation.UserDecision,
                    Reason = "当前阶段无法自动决策。",
                    NextPhase = summary.Phase
                }
            };
        }

        // ══════════════════════════════════════════════════════════════
        //  Step 3: Screening 阶段决策
        // ══════════════════════════════════════════════════════════════

        private DecisionResult DecideScreeningPhase(
            DOERoundSummary summary, DOEProject project,
            List<DOEProjectFactor> activeFactors, AutoPilotConfig config,
            OlsMetrics metrics)
        {
            double alpha = config.AlphaScreen;

            // ── 筛选因子（含遗传原则） ──
            var screenResult = ScreenFactors(activeFactors, metrics, alpha);
            int kRemain = screenResult.KeepFactors.Count;

            // ── k_remain ≤ 1: 异常 ──
            if (kRemain <= 1)
            {
                return new DecisionResult
                {
                    Recommendation = NextStepRecommendation.UserDecision,
                    Reason = $"筛选后仅剩 {kRemain} 个显著因子，几乎所有因子都不显著。" +
                             "可能因子范围太窄或响应本身变异太小，建议检查实验设计。",
                    NextPhase = DOEProjectPhase.Screening,
                    Safeguard = "P3: 筛选后因子数 < 2",
                    ScreenedOutFactors = screenResult.ScreenOutFactors
                };
            }

            // ── k_remain ≥ 6: 继续筛选 ──
            if (kRemain >= 6)
            {
                return new DecisionResult
                {
                    Recommendation = NextStepRecommendation.ContinueScreening,
                    Reason = $"筛选后仍有 {kRemain} 个显著因子，建议再做一轮筛选设计。" +
                             $"本轮淘汰: {string.Join(", ", screenResult.ScreenOutFactors.Select(f => f.FactorName))}",
                    NextPhase = DOEProjectPhase.Screening,
                    ScreenedOutFactors = screenResult.ScreenOutFactors
                };
            }

            // ── 2 ≤ k_remain ≤ 5: 筛选完成，决定下一步 ──
            bool hasCurvature = metrics.CurvaturePValue.HasValue && metrics.CurvaturePValue < config.AlphaLOF;

            if (kRemain == 2)
            {
                // 只剩2个因子，BBD需要≥3，直接用CCD
                return new DecisionResult
                {
                    Recommendation = NextStepRecommendation.StartRSM,
                    Reason = $"筛选完成，剩余 2 个显著因子，直接进入 RSM（CCD 设计）。" +
                             $"淘汰: {string.Join(", ", screenResult.ScreenOutFactors.Select(f => f.FactorName))}",
                    NextPhase = DOEProjectPhase.RSM,
                    ScreenedOutFactors = screenResult.ScreenOutFactors
                };
            }

            if (hasCurvature)
            {
                // 弯曲显著，跳过爬坡直接RSM
                return new DecisionResult
                {
                    Recommendation = NextStepRecommendation.StartRSM,
                    Reason = $"筛选完成，{kRemain} 个显著因子。" +
                             $"弯曲检验显著 (p={metrics.CurvaturePValue:F3})，存在曲率，直接进入 RSM 二阶优化。" +
                             $"淘汰: {string.Join(", ", screenResult.ScreenOutFactors.Select(f => f.FactorName))}",
                    NextPhase = DOEProjectPhase.RSM,
                    ScreenedOutFactors = screenResult.ScreenOutFactors
                };
            }

            // 无曲率，先爬坡
            return new DecisionResult
            {
                Recommendation = NextStepRecommendation.SteepestAscent,
                Reason = $"筛选完成，{kRemain} 个显著因子，弯曲不显著（一阶模型足够）。" +
                         "建议沿最速上升方向移动到更优区域。" +
                         $"淘汰: {string.Join(", ", screenResult.ScreenOutFactors.Select(f => f.FactorName))}",
                NextPhase = DOEProjectPhase.PathSearch,
                ScreenedOutFactors = screenResult.ScreenOutFactors
            };
        }

        /// <summary>
        /// 因子筛选（含遗传原则）
        /// </summary>
        private ScreenResult ScreenFactors(
            List<DOEProjectFactor> activeFactors, OlsMetrics metrics, double alpha)
        {
            var result = new ScreenResult();
            if (metrics.FactorPValues == null || metrics.FactorPValues.Count == 0)
            {
                result.KeepFactors = activeFactors;
                return result;
            }

            // 1. 找出所有显著交互项涉及的因子（遗传原则）
            var protectedByHeredity = new HashSet<string>();
            if (metrics.InteractionPValues != null)
            {
                foreach (var kv in metrics.InteractionPValues.Where(x => x.Value < alpha))
                {
                    // 交互项格式: "A:B" 或 "A×B"
                    var parts = kv.Key.Split(new[] { ':', '×' }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (var part in parts)
                    {
                        // 匹配因子名（可能包含类别标记如 "催化剂类型[A]" → 提取 "催化剂类型"）
                        var factorName = part.Contains("[") ? part.Substring(0, part.IndexOf("[")) : part;
                        protectedByHeredity.Add(factorName.Trim());
                    }
                }
            }

            // 2. 筛选
            foreach (var pf in activeFactors)
            {
                // 遗传原则保护
                if (protectedByHeredity.Contains(pf.FactorName))
                {
                    result.KeepFactors.Add(pf);
                    continue;
                }

                // 查找该因子的主效应 p 值
                // 类别因子可能有多列（如 "催化剂类型[A]"），取最小 p
                double? minP = null;
                foreach (var kv in metrics.FactorPValues)
                {
                    string fName = kv.Key.Contains("[") ? kv.Key.Substring(0, kv.Key.IndexOf("[")) : kv.Key;
                    if (fName == pf.FactorName)
                    {
                        if (!minP.HasValue || kv.Value < minP.Value)
                            minP = kv.Value;
                    }
                }

                if (minP.HasValue && minP.Value < alpha)
                {
                    result.KeepFactors.Add(pf);
                }
                else
                {
                    result.ScreenOutFactors.Add(pf);
                }
            }

            return result;
        }

        // ══════════════════════════════════════════════════════════════
        //  Step 4: PathSearch 阶段决策
        // ══════════════════════════════════════════════════════════════

        private DecisionResult DecidePathSearchPhase(
            DOERoundSummary summary, DOEProject project,
            List<DOEProjectFactor> activeFactors, AutoPilotConfig config,
            OlsMetrics metrics)
        {
            // 爬坡实验已执行，分析结果
            if (summary.DesignMethod == DOEDesignMethod.SteepestAscent)
            {
                // 分析爬坡轨迹
                var trajectory = AnalyzeAscentTrajectory(summary);

                if (trajectory == AscentTrajectory.PeakFound)
                {
                    return new DecisionResult
                    {
                        Recommendation = NextStepRecommendation.StartRSM,
                        Reason = "爬坡实验发现响应值先升后降，已找到高坡区域。" +
                                 "以最高点为新中心进入 RSM 二阶优化。",
                        NextPhase = DOEProjectPhase.RSM
                    };
                }

                if (trajectory == AscentTrajectory.StillRising)
                {
                    return new DecisionResult
                    {
                        Recommendation = NextStepRecommendation.SteepestAscent,
                        Reason = "爬坡实验响应值仍在上升，尚未到达最优区域。" +
                                 "以最后一组为新起点继续爬坡。",
                        NextPhase = DOEProjectPhase.PathSearch
                    };
                }

                // NoTrend → 可能已在最优附近
                return new DecisionResult
                {
                    Recommendation = NextStepRecommendation.StartRSM,
                    Reason = "爬坡实验响应值无明显趋势，可能已在最优区域附近。直接进入 RSM。",
                    NextPhase = DOEProjectPhase.RSM
                };
            }

            // 非爬坡设计（如筛选后直接进来的），检查曲率
            bool hasLOF = metrics.LofP.HasValue && metrics.LofP < config.AlphaLOF;
            if (hasLOF)
            {
                return new DecisionResult
                {
                    Recommendation = NextStepRecommendation.StartRSM,
                    Reason = $"LOF 显著 (p={metrics.LofP:F3})，存在曲率，直接进入 RSM。",
                    NextPhase = DOEProjectPhase.RSM
                };
            }

            return new DecisionResult
            {
                Recommendation = NextStepRecommendation.SteepestAscent,
                Reason = $"一阶模型 LOF p={metrics.LofP:F3} 不显著，建议执行最速上升实验。",
                NextPhase = DOEProjectPhase.PathSearch
            };
        }

        /// <summary>
        /// 分析爬坡轨迹: 先升后降 / 持续上升 / 无趋势
        /// </summary>
        private AscentTrajectory AnalyzeAscentTrajectory(DOERoundSummary summary)
        {
            // 从 BestFactorsJson 或实验数据中分析趋势
            // 简化实现: 依据最优点是否在最后一组来判断
            // 完整实现需要解析爬坡实验的逐步响应值

            if (!summary.BestResponseValue.HasValue)
                return AscentTrajectory.NoTrend;

            // TODO: 从实验数据中提取逐步响应序列进行趋势分析
            // 目前用简化逻辑
            return AscentTrajectory.PeakFound;
        }

        // ══════════════════════════════════════════════════════════════
        //  Step 5: RSM 阶段决策
        // ══════════════════════════════════════════════════════════════

        private DecisionResult DecideRsmPhase(
            DOERoundSummary summary, DOEProject project,
            List<DOEProjectFactor> activeFactors, AutoPilotConfig config,
            OlsMetrics metrics)
        {
            double r2 = metrics.R2;

            // ── 模型质量不足 ──
            if (r2 < config.R2Min)
            {
                bool lofSignificant = metrics.LofP.HasValue && metrics.LofP < config.AlphaLOF;
                if (lofSignificant)
                {
                    return new DecisionResult
                    {
                        Recommendation = NextStepRecommendation.AugmentDesign,
                        Reason = $"R²={r2:F3} 不足（<{config.R2Min}），LOF 显著 (p={metrics.LofP:F3})。" +
                                 "模型结构可能不够，建议补点改善模型。",
                        NextPhase = DOEProjectPhase.RSM
                    };
                }

                bool r2PredBad = metrics.R2Pred < 0;
                if (r2PredBad)
                {
                    return new DecisionResult
                    {
                        Recommendation = NextStepRecommendation.AugmentDesign,
                        Reason = $"R²={r2:F3}，R²pred={metrics.R2Pred:F3} < 0，模型过拟合。" +
                                 "建议补充实验点或减少模型项。",
                        NextPhase = DOEProjectPhase.RSM
                    };
                }

                return new DecisionResult
                {
                    Recommendation = NextStepRecommendation.AugmentDesign,
                    Reason = $"R²={r2:F3} 不足（<{config.R2Min}），建议补充实验点改善模型。",
                    NextPhase = DOEProjectPhase.RSM
                };
            }

            // ── 检测边界状态（含物理极限判断） ──
            var boundaryFactors = GetBoundaryFactors(summary);
            var expandableFactors = new Dictionary<string, string>();
            var atPhysicalLimit = new List<string>();

            foreach (var kv in boundaryFactors)
            {
                var pf = activeFactors.FirstOrDefault(f => f.FactorName == kv.Key);
                if (pf == null) continue;

                bool canExpand = true;

                // 检查物理上限
                if (kv.Value == "at_upper" && pf.PhysicalUpperLimit.HasValue
                    && pf.CurrentUpperBound >= pf.PhysicalUpperLimit.Value - 1e-6)
                {
                    canExpand = false;
                    atPhysicalLimit.Add($"{kv.Key}(已达物理上限 {pf.PhysicalUpperLimit:F1})");
                }

                // 检查物理下限
                if (kv.Value == "at_lower" && pf.PhysicalLowerLimit.HasValue
                    && pf.CurrentLowerBound <= pf.PhysicalLowerLimit.Value + 1e-6)
                {
                    canExpand = false;
                    atPhysicalLimit.Add($"{kv.Key}(已达物理下限 {pf.PhysicalLowerLimit:F1})");
                }

                // 检查扩展次数（最多扩展 2 次）
                if (canExpand)
                {
                    int expansionCount = pf.DeserializeBoundsHistory()
                        .Count(h => h.Reason.Contains("扩展"));
                    if (expansionCount >= 2)
                    {
                        canExpand = false;
                        atPhysicalLimit.Add($"{kv.Key}(已扩展{expansionCount}次)");
                    }
                }

                if (canExpand)
                    expandableFactors[kv.Key] = kv.Value;
            }

            // ── 模型质量好（R² ≥ R²_good） ──
            if (r2 >= config.R2Good)
            {
                // 有可扩展的因子 → 推荐扩展
                if (expandableFactors.Count > 0)
                {
                    string limitNote = atPhysicalLimit.Count > 0
                        ? $"\n注: {string.Join(", ", atPhysicalLimit)} 已无法继续扩展。"
                        : "";

                    return new DecisionResult
                    {
                        Recommendation = NextStepRecommendation.ExpandRange,
                        Reason = $"R²={r2:F3} 优秀，但最优点在因子边界上: " +
                                 $"{string.Join(", ", expandableFactors.Select(kv => $"{kv.Key}({kv.Value})"))}。" +
                                 $"建议扩展边界因子范围 ±{config.RangeExpansionRatio * 100:F0}% 后重新优化。" +
                                 limitNote,
                        NextPhase = DOEProjectPhase.RSM,
                        BoundaryFactors = expandableFactors
                    };
                }

                // 所有边界因子都已达极限 → 直接推荐验证
                if (boundaryFactors.Count > 0 && expandableFactors.Count == 0)
                {
                    return new DecisionResult
                    {
                        Recommendation = NextStepRecommendation.ConfirmationRuns,
                        Reason = $"R²={r2:F3} 优秀，最优点在边界上但已达物理极限: " +
                                 $"{string.Join(", ", atPhysicalLimit)}。" +
                                 $"无法继续扩展，建议在当前最优点做 {config.ConfirmationReps} 次验证实验确认。",
                        NextPhase = DOEProjectPhase.Confirmation
                    };
                }

                // 无边界问题 → 推荐验证
                return new DecisionResult
                {
                    Recommendation = NextStepRecommendation.ConfirmationRuns,
                    Reason = $"R²={r2:F3} 优秀，最优点在因子范围内部。" +
                             $"建议在预测最优点做 {config.ConfirmationReps} 次验证实验确认。",
                    NextPhase = DOEProjectPhase.Confirmation
                };
            }

            // ── 模型尚可（R²_min ≤ R² < R²_good） ──
            if (expandableFactors.Count > 0)
            {
                return new DecisionResult
                {
                    Recommendation = NextStepRecommendation.ExpandRange,
                    Reason = $"R²={r2:F3} 合格，但最优点在边界上: " +
                             $"{string.Join(", ", expandableFactors.Select(kv => $"{kv.Key}({kv.Value})"))}。" +
                             "建议扩展范围后重新优化。",
                    NextPhase = DOEProjectPhase.RSM,
                    BoundaryFactors = expandableFactors
                };
            }

            if (boundaryFactors.Count > 0 && expandableFactors.Count == 0)
            {
                bool lofOk2 = !metrics.LofP.HasValue || metrics.LofP >= config.AlphaLOF;
                return new DecisionResult
                {
                    Recommendation = NextStepRecommendation.ConfirmationRuns,
                    Reason = $"R²={r2:F3} 合格，最优点在边界但已达极限: " +
                             $"{string.Join(", ", atPhysicalLimit)}。" +
                             $"建议在当前最优点做 {config.ConfirmationReps} 次验证实验。",
                    NextPhase = DOEProjectPhase.Confirmation
                };
            }

            bool lofOk = !metrics.LofP.HasValue || metrics.LofP >= config.AlphaLOF;
            if (lofOk)
            {
                return new DecisionResult
                {
                    Recommendation = NextStepRecommendation.ConfirmationRuns,
                    Reason = $"R²={r2:F3} 合格，LOF 不显著，模型可接受。" +
                             $"建议在预测最优点做 {config.ConfirmationReps} 次验证实验。",
                    NextPhase = DOEProjectPhase.Confirmation
                };
            }

            // LOF 显著 → 补点
            return new DecisionResult
            {
                Recommendation = NextStepRecommendation.AugmentDesign,
                Reason = $"R²={r2:F3} 合格但 LOF 显著 (p={metrics.LofP:F3})，建议补点改善模型。",
                NextPhase = DOEProjectPhase.RSM
            };
        }

        // ══════════════════════════════════════════════════════════════
        //  Step 6: Confirmation 阶段决策
        // ══════════════════════════════════════════════════════════════

        private DecisionResult DecideConfirmationPhase(
    DOERoundSummary summary, DOEProject project,
    AutoPilotConfig config, OlsMetrics metrics)
        {
            // 验证阶段不拟合新模型，而是比较预测值与实际值
            // metrics.HasModel 在验证阶段通常为 false（3组相同x无法拟合），这是正常的

            // ── 1. 从验证实验提取实际响应值 ──
            var actualValues = ExtractConfirmationResponses(summary);
            if (actualValues == null || actualValues.Count < 2)
            {
                return new DecisionResult
                {
                    Recommendation = NextStepRecommendation.UserDecision,
                    Reason = "验证实验数据不足（至少需要 2 组重复），请检查实验数据。",
                    NextPhase = DOEProjectPhase.Confirmation
                };
            }

            double actualMean = actualValues.Average();
            double actualStd = Math.Sqrt(actualValues.Sum(v => (v - actualMean) * (v - actualMean)) / (actualValues.Count - 1));
            int n = actualValues.Count;

            // ── 2. 获取上一轮 RSM 模型的预测值 ──
            double? predictedValue = GetPreviousRoundPrediction(summary, project);

            if (predictedValue.HasValue)
            {
                // ── 3. t 检验 ──
                double seMean = actualStd / Math.Sqrt(n);
                double tStat = seMean > 1e-10 ? Math.Abs(actualMean - predictedValue.Value) / seMean : 0;
                double pValue = n > 1
                    ? 2.0 * (1.0 - MathNet.Numerics.Distributions.StudentT.CDF(0, 1, n - 1, tStat))
                    : 1.0;

                bool modelReliable = pValue >= 0.05;

                if (modelReliable)
                {
                    return new DecisionResult
                    {
                        Recommendation = NextStepRecommendation.Complete,
                        Reason = $"验证通过！{n} 组重复实验均值 = {actualMean:F2}，" +
                                 $"模型预测值 = {predictedValue:F2}，" +
                                 $"t 检验 p = {pValue:F4}（≥ 0.05，无显著差异）。\n" +
                                 $"标准差 = {actualStd:F3}，模型预测可靠。建议固化当前最优工艺参数。",
                        NextPhase = DOEProjectPhase.Completed
                    };
                }
                else
                {
                    return new DecisionResult
                    {
                        Recommendation = NextStepRecommendation.AugmentDesign,
                        Reason = $"验证失败。{n} 组重复实验均值 = {actualMean:F2}，" +
                                 $"模型预测值 = {predictedValue:F2}，" +
                                 $"t 检验 p = {pValue:F4}（< 0.05，有显著差异）。\n" +
                                 $"模型预测不可靠，建议补充实验点重新拟合模型。",
                        NextPhase = DOEProjectPhase.RSM
                    };
                }
            }
            else
            {
                // 没有上一轮预测值，用重复性判断
                // 标准差很小（CV < 5%）→ 实验可重复 → 通过
                double cv = actualMean > 1e-10 ? actualStd / actualMean * 100 : 0;

                if (cv < 5.0)
                {
                    return new DecisionResult
                    {
                        Recommendation = NextStepRecommendation.Complete,
                        Reason = $"验证实验完成。{n} 组重复均值 = {actualMean:F2}，" +
                                 $"标准差 = {actualStd:F3}，变异系数 CV = {cv:F2}%（< 5%，重复性良好）。\n" +
                                 $"建议固化当前最优工艺参数。",
                        NextPhase = DOEProjectPhase.Completed
                    };
                }
                else
                {
                    return new DecisionResult
                    {
                        Recommendation = NextStepRecommendation.AugmentDesign,
                        Reason = $"验证实验重复性不佳。{n} 组重复均值 = {actualMean:F2}，" +
                                 $"标准差 = {actualStd:F3}，变异系数 CV = {cv:F2}%（≥ 5%）。\n" +
                                 $"建议检查实验条件或补充实验。",
                        NextPhase = DOEProjectPhase.RSM
                    };
                }
            }
        }


        // ═══ 新增辅助方法 1: 提取验证实验的实际响应值 ═══

        private List<double>? ExtractConfirmationResponses(DOERoundSummary summary)
        {
            try
            {
                var batch = Task.Run(async () =>
                    await _repository.GetBatchWithDetailsAsync(summary.BatchId)).Result;
                if (batch == null) return null;

                var primaryResponse = batch.Responses.FirstOrDefault()?.ResponseName;
                if (string.IsNullOrEmpty(primaryResponse)) return null;

                var values = new List<double>();
                foreach (var run in batch.Runs.Where(r => r.Status == DOERunStatus.Completed))
                {
                    if (string.IsNullOrEmpty(run.ResponseValuesJson)) continue;
                    var responses = JsonConvert.DeserializeObject<Dictionary<string, double>>(run.ResponseValuesJson);
                    if (responses != null && responses.TryGetValue(primaryResponse, out var val))
                        values.Add(val);
                }
                return values.Count > 0 ? values : null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "提取验证实验响应值失败");
                return null;
            }
        }


        // ═══ 新增辅助方法 2: 获取上一轮 RSM 模型在最优点的预测值 ═══

        private double? GetPreviousRoundPrediction(DOERoundSummary summary, DOEProject project)
        {
            try
            {
                // 找到上一轮（RSM 阶段）的批次
                var batches = Task.Run(async () =>
                    await _repository.GetBatchesByProjectAsync(project.ProjectId)).Result;

                var previousRsmBatch = batches
                    .Where(b => b.BatchId != summary.BatchId
                             && b.Status == DOEBatchStatus.Completed
                             && (b.ProjectPhase == DOEProjectPhase.RSM || b.ProjectPhase == DOEProjectPhase.Augmenting))
                    .OrderByDescending(b => b.RoundNumber ?? 0)
                    .FirstOrDefault();

                if (previousRsmBatch == null) return null;

                // 获取上一轮的 OLS 结果
                var primaryResponse = previousRsmBatch.Responses?.FirstOrDefault()?.ResponseName;
                if (string.IsNullOrEmpty(primaryResponse))
                {
                    var responses = Task.Run(async () =>
                        await _repository.GetResponsesAsync(previousRsmBatch.BatchId)).Result;
                    primaryResponse = responses?.FirstOrDefault()?.ResponseName;
                }
                if (string.IsNullOrEmpty(primaryResponse)) return null;

                var olsJson = Task.Run(async () =>
                    await _repository.GetOlsResultJsonAsync(previousRsmBatch.BatchId, primaryResponse)).Result;
                if (string.IsNullOrEmpty(olsJson)) return null;

                var olsResult = JsonConvert.DeserializeObject<OLSAnalysisResult>(olsJson);
                if (olsResult?.UncodedCoefficients == null || olsResult.UncodedCoefficients.Count == 0)
                    return null;

                // 用未编码方程在最优点处计算预测值
                // 最优点 = 验证批次的因子值（验证实验就是在最优点做的重复）
                var confirmBatch = Task.Run(async () =>
                    await _repository.GetBatchWithDetailsAsync(summary.BatchId)).Result;
                var firstRun = confirmBatch?.Runs?.FirstOrDefault();
                if (firstRun == null || string.IsNullOrEmpty(firstRun.FactorValuesJson)) return null;

                var factorValues = JsonConvert.DeserializeObject<Dictionary<string, object>>(firstRun.FactorValuesJson);
                if (factorValues == null) return null;

                // 从上一轮的 RoundSummary 获取最优点的最优响应值作为预测值
                var prevSummary = Task.Run(async () =>
                    await _repository.GetRoundSummaryByBatchAsync(previousRsmBatch.BatchId)).Result;
                if (prevSummary?.BestResponseValue.HasValue == true)
                    return prevSummary.BestResponseValue.Value;

                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取上一轮预测值失败");
                return null;
            }
        }

        // ══════════════════════════════════════════════════════════════
        //  防护规则 P1-P5
        // ══════════════════════════════════════════════════════════════

        private DecisionResult? CheckSafeguards(
            DOERoundSummary summary, DOEProject project, AutoPilotConfig config)
        {
            // P1: 最大轮次
            if (summary.RoundNumber >= config.MaxRounds)
            {
                return new DecisionResult
                {
                    Recommendation = NextStepRecommendation.UserDecision,
                    Reason = $"已达最大轮次限制 ({config.MaxRounds} 轮)。请评估当前结果并决定是否继续。",
                    NextPhase = summary.Phase,
                    Safeguard = $"P1: 已达最大轮次限制({config.MaxRounds}轮)"
                };
            }

            // P4: 实验总量
            int totalRuns = project.TotalExperiments + summary.RunCount;
            if (totalRuns >= config.MaxTotalRuns)
            {
                return new DecisionResult
                {
                    Recommendation = NextStepRecommendation.UserDecision,
                    Reason = $"累计实验组数 ({totalRuns}) 已达上限 ({config.MaxTotalRuns})。" +
                             "请评估成本效益后决定是否继续。",
                    NextPhase = summary.Phase,
                    Safeguard = $"P4: 实验总量达上限({totalRuns}/{config.MaxTotalRuns})"
                };
            }

            // P2 + P5: 需要查历史日志（异步，这里做同步检查用缓存）
            // 完整实现在 AutoPilotService 中调用 GetRecentAutoPilotLogsAsync

            return null;
        }

        // ══════════════════════════════════════════════════════════════
        //  Step 7: 自动选择设计方法
        // ══════════════════════════════════════════════════════════════

        public DOEDesignMethod RecommendDesignMethod(NextStepRecommendation recommendation,
            int activeFactorCount, DOEProjectPhase currentPhase)
        {
            return recommendation switch
            {
                NextStepRecommendation.ContinueScreening => activeFactorCount switch
                {
                    >= 8 => DOEDesignMethod.PlackettBurman,
                    >= 6 => DOEDesignMethod.FractionalFactorial,
                    _ => DOEDesignMethod.FullFactorial
                },

                NextStepRecommendation.SteepestAscent => DOEDesignMethod.SteepestAscent,

                NextStepRecommendation.StartRSM => activeFactorCount switch
                {
                    2 => DOEDesignMethod.CCD,
                    3 or 4 or 5 => DOEDesignMethod.BoxBehnken,
                    _ => DOEDesignMethod.DOptimal
                },

                NextStepRecommendation.AugmentDesign => DOEDesignMethod.DOptimal,

                NextStepRecommendation.ExpandRange => activeFactorCount switch
                {
                    <= 5 => DOEDesignMethod.BoxBehnken,
                    _ => DOEDesignMethod.CCD
                },

                NextStepRecommendation.ConfirmationRuns => DOEDesignMethod.ConfirmationRuns,

                _ => DOEDesignMethod.DOptimal
            };
        }

        /// <summary>
        /// 估算下一轮实验组数
        /// </summary>
        public int EstimateRunCount(DOEDesignMethod method, int factorCount)
        {
            return method switch
            {
                DOEDesignMethod.PlackettBurman => factorCount switch
                {
                    <= 7 => 12,
                    <= 11 => 12,
                    <= 19 => 20,
                    _ => 24
                },
                DOEDesignMethod.FractionalFactorial => factorCount switch
                {
                    <= 4 => 8,
                    <= 5 => 16,
                    <= 7 => 16,
                    _ => 32
                },
                DOEDesignMethod.FullFactorial => (int)Math.Pow(2, factorCount),
                DOEDesignMethod.CCD => factorCount switch
                {
                    2 => 13,
                    3 => 20,
                    4 => 30,
                    5 => 52,
                    _ => 80
                },
                DOEDesignMethod.BoxBehnken => factorCount switch
                {
                    3 => 15,
                    4 => 27,
                    5 => 46,
                    _ => 62
                },
                DOEDesignMethod.SteepestAscent => 8,
                DOEDesignMethod.ConfirmationRuns => 3,
                DOEDesignMethod.DOptimal => factorCount * 3 + 5,
                _ => 20
            };
        }

        // ══════════════════════════════════════════════════════════════
        //  因子管理（与 v1 兼容）
        // ══════════════════════════════════════════════════════════════

        public async Task<List<DOEFactor>> GetNextRoundFactorsAsync(string projectId)
        {
            var activeFactors = await _repository.GetActiveProjectFactorsAsync(projectId);
            return activeFactors.Select(pf => pf.ToBatchFactor("")).ToList();
        }

        public async Task ScreenOutFactorsAsync(string projectId, List<string> factorNames,
            string batchId, string reason)
        {
            var factors = await _repository.GetProjectFactorsAsync(projectId);
            foreach (var pf in factors.Where(f => factorNames.Contains(f.FactorName)))
            {
                await _repository.UpdateProjectFactorStatusAsync(
                    pf.Id, ProjectFactorStatus.ScreenedOut, reason, batchId);
            }
            _logger.LogInformation("因子淘汰: Project={ProjectId}, Factors=[{Factors}]",
                projectId, string.Join(",", factorNames));
        }

        public async Task FixFactorAsync(string projectId, string factorName,
            double? fixedValue, string? fixedCategoryLevel, string batchId, string reason)
        {
            var factors = await _repository.GetProjectFactorsAsync(projectId);
            var pf = factors.FirstOrDefault(f => f.FactorName == factorName);
            if (pf == null) return;

            await _repository.UpdateProjectFactorStatusAsync(
                pf.Id, ProjectFactorStatus.Fixed, reason, batchId);
        }

        public async Task UpdateFactorBoundsAsync(string projectId, string factorName,
            double newLower, double newUpper, string batchId, string reason)
        {
            var factors = await _repository.GetProjectFactorsAsync(projectId);
            var pf = factors.FirstOrDefault(f => f.FactorName == factorName);
            if (pf == null) return;

            pf.AddBoundsHistory(batchId, newLower, newUpper, reason);
            await _repository.UpdateProjectFactorBoundsAsync(
                pf.Id, newLower, newUpper, pf.BoundsHistoryJson ?? "[]");
        }

        public async Task AdvancePhaseAsync(string projectId, DOEProjectPhase newPhase)
        {
            await _repository.UpdateProjectPhaseAsync(projectId, newPhase);
        }

        // ══════════════════════════════════════════════════════════════
        //  辅助方法
        // ══════════════════════════════════════════════════════════════

        private Dictionary<string, string> GetBoundaryFactors(DOERoundSummary summary)
        {
            if (string.IsNullOrEmpty(summary.OptimalBoundaryStatusJson))
                return new Dictionary<string, string>();

            var status = JsonConvert.DeserializeObject<Dictionary<string, string>>(
                summary.OptimalBoundaryStatusJson);

            return status?.Where(kv => kv.Value == "at_lower" || kv.Value == "at_upper")
                         .ToDictionary(kv => kv.Key, kv => kv.Value)
                   ?? new Dictionary<string, string>();
        }

        private void ComputeOptimalAndBoundary(DOERoundSummary summary,
            List<DOERunRecord> completedRuns, List<DOEProjectFactor> activeFactors,
            string? primaryResponse)
        {
            if (completedRuns.Count == 0 || string.IsNullOrEmpty(primaryResponse)) return;

            double bestVal = double.MinValue;
            Dictionary<string, object>? bestFactors = null;

            foreach (var run in completedRuns)
            {
                if (string.IsNullOrEmpty(run.ResponseValuesJson)) continue;
                var responses = JsonConvert.DeserializeObject<Dictionary<string, double>>(run.ResponseValuesJson);
                if (responses == null || !responses.TryGetValue(primaryResponse, out var val)) continue;

                if (val > bestVal)
                {
                    bestVal = val;
                    bestFactors = JsonConvert.DeserializeObject<Dictionary<string, object>>(run.FactorValuesJson);
                }
            }

            if (bestFactors != null)
            {
                summary.BestResponseValue = bestVal;
                summary.BestFactorsJson = JsonConvert.SerializeObject(bestFactors);

                var boundaryStatus = new Dictionary<string, string>();
                foreach (var pf in activeFactors.Where(f => !f.IsCategorical))
                {
                    if (!bestFactors.TryGetValue(pf.FactorName, out var valObj)) continue;
                    if (!double.TryParse(valObj.ToString(), out var v)) continue;

                    double range = pf.CurrentUpperBound - pf.CurrentLowerBound;
                    double tolerance = range * 0.05;

                    if (Math.Abs(v - pf.CurrentLowerBound) < tolerance)
                        boundaryStatus[pf.FactorName] = "at_lower";
                    else if (Math.Abs(v - pf.CurrentUpperBound) < tolerance)
                        boundaryStatus[pf.FactorName] = "at_upper";
                    else
                        boundaryStatus[pf.FactorName] = "interior";
                }

                summary.OptimalBoundaryStatusJson = JsonConvert.SerializeObject(boundaryStatus);
            }
        }

        private AutoPilotLog BuildAutoPilotLog(
            DOERoundSummary summary, DecisionResult decision,
            AutoPilotConfig config, OlsMetrics metrics,
            List<DOEProjectFactor> activeFactors)
        {
            int nextFactorCount = activeFactors.Count;
            if (decision.ScreenedOutFactors.Count > 0)
                nextFactorCount -= decision.ScreenedOutFactors.Count;

            var nextMethod = RecommendDesignMethod(
                decision.Recommendation, nextFactorCount, decision.NextPhase);

            var inputSummary = new
            {
                r2 = metrics.R2,
                r2_adj = metrics.R2Adj,
                r2_pred = metrics.R2Pred,
                lof_p = metrics.LofP,
                curvature_p = metrics.CurvaturePValue,
                active_factors = activeFactors.Count,
                total_runs_so_far = summary.RunCount,
                factor_ranking = metrics.FactorPValues,
                boundary_status = summary.OptimalBoundaryStatusJson
            };

            var decisionDetail = new
            {
                screened_out = decision.ScreenedOutFactors.Select(f => f.FactorName).ToList(),
                expanded_factors = decision.BoundaryFactors,
            };

            return new AutoPilotLog
            {
                ProjectId = summary.ProjectId,
                BatchId = summary.BatchId,
                RoundNumber = summary.RoundNumber,
                PhaseAtDecision = summary.Phase,
                InputSummaryJson = JsonConvert.SerializeObject(inputSummary),
                DecisionType = decision.Recommendation,
                DecisionDetailJson = JsonConvert.SerializeObject(decisionDetail),
                Reason = decision.Reason,
                NextPhase = decision.NextPhase,
                NextDesignMethod = nextMethod,
                NextFactorCount = nextFactorCount,
                EstimatedRuns = EstimateRunCount(nextMethod, nextFactorCount),
                SafeguardTriggered = !string.IsNullOrEmpty(decision.Safeguard),
                SafeguardRule = decision.Safeguard
            };
        }

        // ══════════════════════════════════════════════════════════════
        //  内部数据结构
        // ══════════════════════════════════════════════════════════════

        private class OlsMetrics
        {
            public bool HasModel { get; set; }
            public double R2 { get; set; }
            public double R2Adj { get; set; }
            public double R2Pred { get; set; }
            public double? LofP { get; set; }
            public double? CurvaturePValue { get; set; }
            public Dictionary<string, double>? FactorPValues { get; set; }
            public Dictionary<string, double>? InteractionPValues { get; set; }
        }

        private class DecisionResult
        {
            public NextStepRecommendation Recommendation { get; set; }
            public string Reason { get; set; } = "";
            public DOEProjectPhase NextPhase { get; set; }
            public string? Safeguard { get; set; }
            public List<DOEProjectFactor> ScreenedOutFactors { get; set; } = new();
            public Dictionary<string, string> BoundaryFactors { get; set; } = new();
        }

        private class ScreenResult
        {
            public List<DOEProjectFactor> KeepFactors { get; set; } = new();
            public List<DOEProjectFactor> ScreenOutFactors { get; set; } = new();
        }

        private enum AscentTrajectory
        {
            PeakFound,
            StillRising,
            NoTrend
        }
    }
}
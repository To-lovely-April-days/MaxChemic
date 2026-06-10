using MaxChemical.Infrastructure.DOE;
using MaxChemical.Infrastructure.Events;
using MaxChemical.Infrastructure.Services;
using MaxChemical.Logging;
using MaxChemical.Modules.DOE.Data;
using MaxChemical.Modules.DOE.Models;
using MaxChemical.Modules.DOE.Views;
using Prism.Commands;
using Prism.Events;
using Prism.Mvvm;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;

namespace MaxChemical.Modules.DOE.ViewModels
{
    /// <summary>
    /// 概览页 ViewModel — 项目模式
    /// 
    /// ★ 重写: 从"最近批次列表"改为"项目卡片列表"
    /// 
    /// 展示内容:
    ///   - 活跃项目卡片（项目名、阶段、最优值、总实验数、轮次数）
    ///   - 统计摘要（总项目数、总实验数、GPR 状态）
    ///   - 快捷操作: 新建项目、继续优化、查看历史
    /// </summary>
    public class DOEOverviewViewModel : BindableBase
    {
        private readonly IDOERepository _repository;
        private readonly IFlowParameterProvider _paramProvider;
        private readonly ILogService _logger;
        private readonly IEventAggregator _eventAggregator;
        private readonly ILocalizationService _localizationService;

        // ── 统计数据 ──
        private int _totalProjectCount;
        private int _activeProjectCount;
        private int _totalExperiments;
        private string _gprStatusSummary = "未初始化";

        // ── 项目列表 ──
        private ObservableCollection<ProjectCardItem> _projectCards = new();
        private ProjectCardItem? _selectedProject;

        // ── 可恢复批次（某个项目中正在执行的批次）──
        private bool _hasResumableBatch;
        private string _resumableBatchSummary = "";
        private string _resumableBatchId = "";
        // ═══ 时间线数据 ═══

        public ObservableCollection<TimelineItem> RecentTimelineItems
        {
            get => _recentTimelineItems;
            set => SetProperty(ref _recentTimelineItems, value);
        }
        private ObservableCollection<TimelineItem> _recentTimelineItems = new();
        public ObservableCollection<ProjectCardItem> BottomCards
        {
            get => _bottomCards;
            set => SetProperty(ref _bottomCards, value);
        }
        private ObservableCollection<ProjectCardItem> _bottomCards = new();

        // 新增两个属性（Properties 区域）
        public double ResumableProgress
        {
            get => _resumableProgress;
            set => SetProperty(ref _resumableProgress, value);
        }
        private double _resumableProgress;

        public string ResumableProgressText
        {
            get => _resumableProgressText;
            set => SetProperty(ref _resumableProgressText, value);
        }
        private string _resumableProgressText = "";
        public DOEOverviewViewModel(
            IDOERepository repository,
            IFlowParameterProvider paramProvider,
            ILogService logger,
            IEventAggregator eventAggregator,
            ILocalizationService localizationService)
        {
            _repository = repository;
            _paramProvider = paramProvider;
            _logger = logger?.ForContext<DOEOverviewViewModel>() ?? throw new ArgumentNullException(nameof(logger));
            _eventAggregator = eventAggregator ?? throw new ArgumentNullException(nameof(eventAggregator));
            _localizationService = localizationService ?? throw new ArgumentNullException(nameof(localizationService));

            ResumeLastBatchCommand = new DelegateCommand(
                () => RequestResumeBatch?.Invoke(this, _resumableBatchId),
                () => HasResumableBatch);
            GoToHistoryCommand = new DelegateCommand(
                () => RequestGoToHistory?.Invoke(this, EventArgs.Empty));
            ContinueProjectCommand = new DelegateCommand<string>(
                id => { if (!string.IsNullOrEmpty(id)) RequestContinueProject?.Invoke(this, id); });
            ViewProjectCommand = new DelegateCommand<string>(
                id => { if (!string.IsNullOrEmpty(id)) RequestViewProject?.Invoke(this, id); });
            ViewProjectDetailCommand = new DelegateCommand<string>(async id => await ShowProjectDetailAsync(id));
            ViewProjectAnalysisCommand = new DelegateCommand<string>(id => { if (!string.IsNullOrEmpty(id)) RequestViewAnalysis?.Invoke(this, id); });
            ArchiveProjectCommand = new DelegateCommand<string>(async id => await ArchiveProjectAsync(id));
            DeleteProjectCommand = new DelegateCommand<string>(async id => await DeleteProjectAsync(id));
            SelectProjectCommand = new DelegateCommand<string>(async id => await SelectProjectAsync(id));

            // 注册本地化语言变更的处理程序
            _eventAggregator.GetEvent<LanguageChangedEvent>().Subscribe(OnLanguageChangedEvent);
            OnLanguageChangedEvent("");
        }

        #region 本地化

        private string _titleTxt;
        private string _projCountTxt;
        private string _itemCountTxt;
        private string _historyTxt;
        private string _newProjTxt;
        private string _prodRateTxt;
        private string _roundTxt;
        private string _experiTxt;
        private string _factorTxt;
        private string _responseTxt;
        private string _createInTxt;
        private string _updateOnTxt;
        private string _continueTxt;
        private string _detailTxt;
        private string _analysisTxt;
        private string _waitResumeTxt;
        private string _resumeExecTxt;
        private string _optimizTxt;
        private string _allTxt;
        private string _roundsTxt;
        private string _startOptimizTxt;
        private string _instructionTxt;
        private string _processGuideTxt;
        private string _viewDetails;
        private string _viewAnalysis;
        private string _viewHistory;
        private string _progArchive;
        private string _delProj;
        private string _currentBestTxt;
        private string _fianlResultTxt;

        public string TitleTxt { get { return _titleTxt; } set { SetProperty(ref _titleTxt, value); } }
        public string ProjCountTxt { get { return _projCountTxt; } set { SetProperty(ref _projCountTxt, value); }  }
        public string ItemCountTxt { get { return _itemCountTxt; }set { SetProperty(ref _itemCountTxt, value); } }
        public string HistoryTxt { get { return _historyTxt; } set { SetProperty(ref _historyTxt, value); }  }
        public string NewProjTxt { get { return _newProjTxt; } set { SetProperty(ref _newProjTxt, value); }  }
        public string ProdRateTxt { get { return _prodRateTxt; }set { SetProperty(ref _prodRateTxt, value); } }
        public string RoundTxt { get { return _roundTxt; } set { SetProperty(ref _roundTxt, value); }  }
        public string ExperiTxt { get { return _experiTxt; } set { SetProperty(ref _experiTxt, value); }  }
        public string FactorTxt { get { return _factorTxt; } set { SetProperty (ref _factorTxt, value);  }  }
        public string ResponseTxt { get { return _responseTxt; } set { SetProperty(ref _responseTxt, value); }  }
        public string CreateInTxt { get { return _createInTxt; } set { SetProperty(ref _createInTxt, value); }  }
        public string UpdateOnTxt { get { return _updateOnTxt; } set { SetProperty(ref _updateOnTxt, value); }  }
        public string ContinueTxt { get { return _continueTxt; } set { SetProperty(ref _continueTxt, value); }  }
        public string DetailTxt { get { return _detailTxt; } set { SetProperty(ref _detailTxt, value); }  }
        public string AnalysisTxt { get { return _analysisTxt; } set { SetProperty(ref _analysisTxt, value); }  }
        public string WaitResumeTxt { get { return _waitResumeTxt; } set { SetProperty(ref _waitResumeTxt, value); }  }
        public string ResumeExecute { get { return _resumeExecTxt; } set { SetProperty(ref _resumeExecTxt, value); } }
        public string OptimizTxt { get { return _optimizTxt; } set { SetProperty(ref _optimizTxt, value); } }
        public string AllTxt { get { return _allTxt; } set { SetProperty(ref _allTxt, value); } }
        public string RoundsTxt { get { return _roundsTxt; } set { SetProperty (ref _roundsTxt, value);  }  }
        public string StartOptimizTxt { get { return _startOptimizTxt; } set { SetProperty(ref _startOptimizTxt, value); } }
        public string InstructionTxt { get { return _instructionTxt; } set { SetProperty(ref _instructionTxt, value); }  }
        public string ProcessGuideTxt { get { return _processGuideTxt; } set { SetProperty(ref _processGuideTxt, value); }  }
        public string ViewDetails { get { return _viewDetails; } set { SetProperty(ref _viewDetails, value); }  }
        public string ViewAnalysis { get { return _viewAnalysis; } set { SetProperty(ref _viewAnalysis, value); }  }
        public string ViewHistory { get { return _viewHistory; } set { SetProperty(ref _viewHistory, value); }  }
        public string ProjArchive { get { return _progArchive; } set { SetProperty(ref _progArchive, value); }  }
        public string DeleteProj { get { return _delProj; } set { SetProperty(ref _delProj, value); }  }
        public string CurrentBestTxt { get { return _currentBestTxt; } set { SetProperty(ref _currentBestTxt, value); } }
        public string FinalResultTxt { get { return _fianlResultTxt; } set { SetProperty(ref _fianlResultTxt, value); } }


        private void OnLanguageChangedEvent(string language)
        {
            TitleTxt = _localizationService.GetString("Doe_Overview_Title", "实验优化");
            ProjCountTxt = _localizationService.GetString("Doe_Overview_activeNum", "个活跃项目");
            ItemCountTxt = _localizationService.GetString("Doe_Overview_groupNum", "组实验");
            HistoryTxt = _localizationService.GetString("Doe_Overview_History", "历史");
            NewProjTxt = _localizationService.GetString("Doe_Overview_NewProj", "新建项目");
            ProdRateTxt = _localizationService.GetString("Doe_Overview_MaxRate", "最优产率");
            RoundTxt = _localizationService.GetString("Doe_Overview_round", "轮次");
            ExperiTxt = _localizationService.GetString("Doe_Overview_Experiment", "实验");
            FactorTxt = _localizationService.GetString("Doe_Overview_MaxFactor", "最优因子组合");
            ResponseTxt = _localizationService.GetString("Doe_Overview_ResponseValue", "响应值");
            CreateInTxt = _localizationService.GetString("Doe_Overview_CreateIn", "创建于");
            UpdateOnTxt = _localizationService.GetString("Doe_Overview_UpdateOn", "最后更新");
            ContinueTxt = _localizationService.GetString("Doe_Overview_Continue", "继续优化");
            DetailTxt = _localizationService.GetString("Doe_Overview_Detail", "详情");
            AnalysisTxt = _localizationService.GetString("Doe_Overview_Analysis", "分析");
            WaitResumeTxt = _localizationService.GetString("Doe_Overview_WaitResume", "等待恢复");
            ResumeExecute = _localizationService.GetString("Doe_Overview_ResumeExec", "恢复执行");
            OptimizTxt = _localizationService.GetString("Doe_Overview_OptimizeProcess", "优化历程");
            AllTxt = _localizationService.GetString("Doe_Overview_All", "全部");
            RoundsTxt = _localizationService.GetString("Doe_Overview_Rounds", "轮");
            StartOptimizTxt = _localizationService.GetString("Doe_Overview_StartProjOptimize", "开始第一个项目优化");
            InstructionTxt = _localizationService.GetString("Doe_Overview_Instruction", "创建项目后，系统自动引导完成");
            ProcessGuideTxt = _localizationService.GetString("Doe_Overview_OptimizeGuide", "筛选 → 路径探索 → 响应面优化 → 验证 全流程");
            ViewDetails = _localizationService.GetString("Doe_Overview_ViewDetails", "查看详情");
            ViewAnalysis = _localizationService.GetString("Doe_Overview_ViewAnalysis", "查看分析");
            ViewHistory = _localizationService.GetString("Doe_Overview_ViewHistory", "查看历史");
            ProjArchive = _localizationService.GetString("Deo_Overview_ProjArchive", "项目归档");
            DeleteProj = _localizationService.GetString("Deo_Overview_DeleteProj", "删除项目");
            CurrentBestTxt = _localizationService.GetString("Doe_Overview_CurrBest", "当前最优");
            FinalResultTxt = _localizationService.GetString("Doe_Overview_FinalResult", "最终结果");
        }

        #endregion

        // ══════════════ Properties ══════════════

        public int TotalProjectCount { get => _totalProjectCount; set => SetProperty(ref _totalProjectCount, value); }
        public int ActiveProjectCount { get => _activeProjectCount; set => SetProperty(ref _activeProjectCount, value); }
        public int TotalExperiments { get => _totalExperiments; set => SetProperty(ref _totalExperiments, value); }
        public string GPRStatusSummary { get => _gprStatusSummary; set => SetProperty(ref _gprStatusSummary, value); }
        /// <summary>焦点项目（焦点卡片绑定此对象）</summary>
        public ProjectCardItem? FocusedProject
        {
            get => _focusedProject;
            set => SetProperty(ref _focusedProject, value);
        }
        private ProjectCardItem? _focusedProject;
        public ObservableCollection<ProjectCardItem> ProjectCards
        {
            get => _projectCards;
            set => SetProperty(ref _projectCards, value);
        }

        public ProjectCardItem? SelectedProject
        {
            get => _selectedProject;
            set => SetProperty(ref _selectedProject, value);
        }

        public bool HasResumableBatch
        {
            get => _hasResumableBatch;
            set { SetProperty(ref _hasResumableBatch, value); ResumeLastBatchCommand.RaiseCanExecuteChanged(); }
        }
        public string ResumableBatchSummary { get => _resumableBatchSummary; set => SetProperty(ref _resumableBatchSummary, value); }

        /// <summary>是否有项目（控制空状态引导显示）</summary>
        public bool HasProjects => ProjectCards.Count > 0;

        // ══════════════ Commands ══════════════

        public DelegateCommand ResumeLastBatchCommand { get; }
        public DelegateCommand GoToHistoryCommand { get; }
        public DelegateCommand<string> ContinueProjectCommand { get; }
        public DelegateCommand<string> ViewProjectCommand { get; }
        public DelegateCommand<string> ViewProjectDetailCommand { get; }
        public DelegateCommand<string> ViewProjectAnalysisCommand { get; }
        public DelegateCommand<string> ArchiveProjectCommand { get; }
        public DelegateCommand<string> DeleteProjectCommand { get; }
        public DelegateCommand<string> SelectProjectCommand { get; }
        // ══════════════ Events ══════════════

        public event EventHandler<string>? RequestResumeBatch;
        public event EventHandler? RequestGoToHistory;
        /// <summary>请求继续某个项目的优化（触发下一轮）</summary>
        public event EventHandler<string>? RequestContinueProject;
        /// <summary>请求查看某个项目的详情（跳转历史页）</summary>
        public event EventHandler<string>? RequestViewProject;
        /// <summary>请求创建新项目</summary>
        public event EventHandler? RequestCreateProject;
        // 事件：
        public event EventHandler<string>? RequestViewAnalysis;
        // ══════════════ 加载 ══════════════

        public async Task LoadAsync()
        {
            try
            {
                var projects = await _repository.GetAllProjectSummariesAsync(20);

                TotalProjectCount = projects.Count;
                ActiveProjectCount = projects.Count(p => p.Status == DOEProjectStatus.Active);
                TotalExperiments = projects.Sum(p => p.TotalExperiments);

                var cards = new List<ProjectCardItem>();
                foreach (var p in projects)
                {
                    var card = new ProjectCardItem
                    {
                        ProjectId = p.ProjectId,
                        ProjectName = p.ProjectName,
                        FlowName = p.FlowName ?? "",
                        CurrentPhase = p.CurrentPhase,
                        PhaseText = GetShortPhaseText(p.CurrentPhase),
                        Status = p.Status,
                        TotalBatches = p.TotalBatches,
                        TotalExperiments = p.TotalExperiments,
                        BestResponseValue = p.BestResponseValue,
                        CreatedTime = p.CreatedTime,
                        UpdatedTime = p.UpdatedTime,
                        PhaseColor = GetPhaseColor(p.CurrentPhase),
                        StatusIcon = p.Status == DOEProjectStatus.Active ? "▶" : "■",
                        BestValueText = p.BestResponseValue.HasValue ? $"{p.BestResponseValue:F2}" : "—",
                        SummaryText = $"{p.TotalBatches} 轮 · {p.TotalExperiments} 组实验",
                        PhaseSegments = BuildPhaseSegments(p.CurrentPhase),
                        PhaseProgressText = BuildPhaseProgressText(p.CurrentPhase),
                    };
                    await BuildCardDetails(card, p);
                    // ★ 构建最优因子和响应值
                    await BuildOptimalData(card, p.ProjectId);
                    cards.Add(card);
                }

                // 底部卡片：新建占位 + 所有项目
                var bottomList = new List<ProjectCardItem>();
                bottomList.Add(new ProjectCardItem
                {
                    ProjectId = "__NEW__",
                    ProjectName = "新建项目",
                    IsNewPlaceholder = true,
                    PhaseColor = "#3949AB",
                });
                bottomList.AddRange(cards);
                BottomCards = new ObservableCollection<ProjectCardItem>(bottomList);

                ProjectCards = new ObservableCollection<ProjectCardItem>(cards);
                RaisePropertyChanged(nameof(HasProjects));

                // 默认选中第一个项目
                if (cards.Count > 0)
                {
                    cards[0].IsSelected = true;
                    FocusedProject = cards[0];
                }

                await FindResumableBatchAsync();
                await LoadTimelineForProjectAsync(FocusedProject?.ProjectId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "加载概览数据失败");
            }
        }
        private async Task SelectProjectAsync(string? projectId)
        {
            if (string.IsNullOrEmpty(projectId) || projectId == "__NEW__") return;

            var card = ProjectCards.FirstOrDefault(c => c.ProjectId == projectId);
            if (card == null) return;

            // 清除所有选中态
            foreach (var c in ProjectCards)
                c.IsSelected = false;

            card.IsSelected = true;
            FocusedProject = card;

            // 重新加载该项目的时间线
            await LoadTimelineForProjectAsync(projectId);
        }
        private async Task LoadTimelineForProjectAsync(string? projectId)
        {
            try
            {
                if (string.IsNullOrEmpty(projectId))
                {
                    RecentTimelineItems = new ObservableCollection<TimelineItem>();
                    return;
                }

                var summaries = await _repository.GetRoundSummariesAsync(projectId);
                if (summaries == null || summaries.Count == 0)
                {
                    RecentTimelineItems = new ObservableCollection<TimelineItem>();
                    return;
                }

                // 按轮次倒序，取最近 4 轮
                var recent = summaries
                    .OrderByDescending(s => s.RoundNumber)
                    .Take(4)
                    .ToList();

                var items = new ObservableCollection<TimelineItem>();
                for (int i = 0; i < recent.Count; i++)
                {
                    var s = recent[i];
                    var item = new TimelineItem
                    {
                        RoundNumber = s.RoundNumber,
                        PhaseText = GetTimelinePhaseText(s.Phase, s.DesignMethod),
                        EventText = BuildTimelineEventText(s),
                        MetaText = BuildTimelineMetaText(s),
                        IsLatest = i == 0,
                        ShowLine = i < recent.Count - 1,
                    };
                    SetTimelineChip(item, s);
                    items.Add(item);
                }

                RecentTimelineItems = items;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "加载时间线失败");
                RecentTimelineItems = new ObservableCollection<TimelineItem>();
            }
        }
        private async Task BuildOptimalData(ProjectCardItem card, string projectId)
        {
            try
            {
                // 获取该项目所有完成的实验
                var runs = await _repository.GetAllCompletedRunsByProjectAsync(projectId);
                if (runs == null || runs.Count == 0) return;

                // 找到最优实验（响应值最大的那组）
                var bestRun = runs
                    .Where(r => !string.IsNullOrEmpty(r.ResponseValuesJson))
                    .OrderByDescending(r =>
                    {
                        try
                        {
                            var dict = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, double>>(r.ResponseValuesJson!);
                            return dict?.Values.FirstOrDefault() ?? 0;
                        }
                        catch { return 0.0; }
                    })
                    .FirstOrDefault();

                if (bestRun == null) return;

                // 解析因子值
                if (!string.IsNullOrEmpty(bestRun.FactorValuesJson))
                {
                    try
                    {
                        var factors = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, double>>(bestRun.FactorValuesJson);
                        if (factors != null)
                        {
                            // 因子值
                            card.OptimalFactorItems = new ObservableCollection<FocusFactorItem>(
                                factors.Select(kv => new FocusFactorItem
                                {
                                    Name = kv.Key,
                                    Value = kv.Value.ToString("F2")
                                }));
                        }
                    }
                    catch { }
                }

                // 解析响应值
                if (!string.IsNullOrEmpty(bestRun.ResponseValuesJson))
                {
                    try
                    {
                        var responses = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, double>>(bestRun.ResponseValuesJson);
                        if (responses != null)
                        {
                            // 响应值
                            card.ResponseValueItems = new ObservableCollection<FocusResponseItem>(
                                responses.Select(kv => new FocusResponseItem
                                {
                                    Name = kv.Key,
                                    Value = kv.Value.ToString("F2")
                                }));
                        }
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "构建最优数据失败: {ProjectId}", projectId);
            }
        }

        //
        private string GetTimelinePhaseText(DOEProjectPhase phase, DOEDesignMethod method)
        {
            if (method == DOEDesignMethod.SteepestAscent) return _localizationService.GetString("Doe_ProjectPhase_PathSearch", "爬坡");
            if (method == DOEDesignMethod.ConfirmationRuns) return _localizationService.GetString("Doe_ProjectPhase_Confirmation", "验证");
            return phase switch
            {
                DOEProjectPhase.Screening => _localizationService.GetString("Doe_ProjectPhase_Screening", "筛选"),
                DOEProjectPhase.PathSearch => _localizationService.GetString("Doe_ProjectPhase_PathSearch", "爬坡"),
                DOEProjectPhase.RSM => _localizationService.GetString("Doe_ProjectPhase_RSM", "RSM"),
                DOEProjectPhase.Augmenting => _localizationService.GetString("Doe_ProjectPhase_Augmenting", "补点"),
                DOEProjectPhase.Confirmation => _localizationService.GetString("Doe_ProjectPhase_Confirmation", "验证"),
                DOEProjectPhase.Completed => _localizationService.GetString("Doe_ProjectPhase_Completed", "已完成"),
                _ => phase.ToString() ?? ""
            };
        }

        private string BuildTimelineEventText(DOERoundSummary s)
        {
            // 优先用 RecommendationReason（决策引擎生成的文字）
            if (!string.IsNullOrEmpty(s.RecommendationReason))
            {
                // 截取前40字符，避免太长
                var text = s.RecommendationReason;
                if (text.Length > 50) text = text.Substring(0, 47) + "...";
                return text;
            }

            // 回退：根据推荐类型生成简短文字
            return s.Recommendation switch
            {
                NextStepRecommendation.ContinueScreening => _localizationService.GetString("Doe_Overview_Recommend_Screening", "继续筛选不显著因子"),
                NextStepRecommendation.SteepestAscent => _localizationService.GetString("Doe_Overview_Recommend_Steepest", "沿梯度方向爬坡"),
                NextStepRecommendation.StartRSM => _localizationService.GetString("Doe_Overview_Recommend_StartRSM", "进入响应面优化"),
                NextStepRecommendation.ExpandRange => _localizationService.GetString("Doe_Overview_Recommend_Expand", "扩展因子范围"),
                NextStepRecommendation.ConfirmationRuns => _localizationService.GetString("Doe_Overview_Recommend_Confirmation", "推荐验证实验"),
                NextStepRecommendation.AugmentDesign => _localizationService.GetString("Doe_Overview_Recommend_Augment", "补充实验点"),
                NextStepRecommendation.Complete => _localizationService.GetString("Doe_Overview_Recommend_Complete", "优化完成"),
                _ => string.Format(_localizationService.GetString("Doe_Overview_Recommend_Round", "第 {0} 轮完成"),s.RoundNumber)
            };
        }

        private string BuildTimelineMetaText(DOERoundSummary s)
        {
            var parts = new List<string>();
            if (s.RSquared.HasValue) parts.Add($"R²={s.RSquared:F3}");

            if (s.RunCount > 0)
                parts.Add(string.Format(_localizationService.GetString("Doe_Overview_Meta_Group", "{0} 组"),s.RunCount));

            if (s.BestResponseValue.HasValue)
                parts.Add(string.Format(_localizationService.GetString("Doe_Overview_Meta_Optimal", "最优 {0:F2}"),s.BestResponseValue));

            return parts.Count > 0 ? string.Join(" · ", parts) : "";
        }

        private void SetTimelineChip(TimelineItem item, DOERoundSummary s)
        {
            // 根据推荐结果设置标签颜色
            switch (s.Recommendation)
            {
                case NextStepRecommendation.Complete:
                    item.HasChip = true;
                    item.ChipText = _localizationService.GetString("Doe_Overview_Chip_Complete", "完成");
                    item.ChipBgColor = "#D1FAE5";
                    item.ChipFgColor = "#065F46";
                    break;
                case NextStepRecommendation.ConfirmationRuns:
                    item.HasChip = true;
                    item.ChipText = _localizationService.GetString("Doe_Overview_Chip_Confirmation", "验证");
                    item.ChipBgColor = "#D1FAE5";
                    item.ChipFgColor = "#065F46";
                    break;
                case NextStepRecommendation.ExpandRange:
                    item.HasChip = true;
                    item.ChipText = _localizationService.GetString("Doe_Overview_Chip_Expand", "扩展");
                    item.ChipBgColor = "#E8EAF6";
                    item.ChipFgColor = "#2D3A8C";
                    break;
                case NextStepRecommendation.ContinueScreening:
                    // 检查是否有淘汰因子
                    if (!string.IsNullOrEmpty(s.ScreenedOutFactors))
                    {
                        item.HasChip = true;
                        item.ChipText = _localizationService.GetString("Doe_OverView_Chip_Continue", "淘汰");
                        item.ChipBgColor = "#FEE2E2";
                        item.ChipFgColor = "#991B1B";
                    }
                    break;
                case NextStepRecommendation.SteepestAscent:
                    item.HasChip = true;
                    item.ChipText = _localizationService.GetString("Doe_Overview_Chip_Steepest", "爬坡");
                    item.ChipBgColor = "#E8EAF6";
                    item.ChipFgColor = "#2D3A8C";
                    break;
                case NextStepRecommendation.StartRSM:
                    item.HasChip = true;
                    item.ChipText = _localizationService.GetString("Doe_Overview_Chip_RSM", "RSM");
                    item.ChipBgColor = "#EDE9FE";
                    item.ChipFgColor = "#5B21B6";
                    break;
                default:
                    item.HasChip = false;
                    break;
            }
        }
        private async Task BuildCardDetails(ProjectCardItem card, DOEProjectSummary p)
        {
            try
            {
                // 查询该项目最新批次
                var batches = await _repository.GetBatchesByProjectAsync(p.ProjectId);
                var latestBatch = batches?.OrderByDescending(b => b.RoundNumber ?? 0).FirstOrDefault();

                if (latestBatch != null)
                {
                    // 设计方法文字
                    var methodText = GetDesignMethodText(latestBatch.DesignMethod);

                    // 详细摘要
                    card.DetailSummaryText = string.Format(
                    _localizationService.GetString(
                    "Doe_Overview_DetailSummary_Method",
                    "第 {0} 轮 · {1} · {2} 组实验 · {3:MM-dd HH:mm}"),
                    latestBatch.RoundNumber ?? card.TotalBatches, methodText, p.TotalExperiments, p.UpdatedTime);

                    // 因子标签
                    var factors = latestBatch.Factors;
                    if (factors == null || factors.Count == 0)
                    {
                        factors = (await _repository.GetFactorsAsync(latestBatch.BatchId))?.ToList()
                                  ?? new List<DOEFactor>();
                    }

                    var tags = new ObservableCollection<TagItem>();

                    // 因子标签（灰底）
                    foreach (var f in factors)
                    {
                        tags.Add(new TagItem
                        {
                            Text = f.FactorName,
                            Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#5A5F6D")),
                            Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F2F4F7"))
                        });
                    }

                    // 响应标签（蓝底）
                    var responses = latestBatch.Responses;
                    if (responses == null || responses.Count == 0)
                    {
                        responses = (await _repository.GetResponsesAsync(latestBatch.BatchId))?.ToList()
                                    ?? new List<DOEResponse>();
                    }

                    foreach (var r in responses)
                    {
                        tags.Add(new TagItem
                        {
                            Text = r.ResponseName,
                            Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#0078D4")),
                            Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#EBF3FE"))
                        });
                    }

                    card.TagItems = tags;
                }
                else
                {
                    card.DetailSummaryText = string.Format(
                    _localizationService.GetString(
                    "Doe_Overview_DetailSummary_RoundGroup",
                    "{0} 轮 · {1} 组实验 · {2:MM-dd HH:mm}"),
                    p.TotalBatches, p.TotalExperiments, p.UpdatedTime);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "构建项目卡片详情失败: {ProjectId}", p.ProjectId);
                card.DetailSummaryText = string.Format(
                    _localizationService.GetString(
                    "Doe_Overview_DetailSummary_RoundGroup",
                    "{0} 轮 · {1} 组实验 · {2:MM-dd HH:mm}"),
                    p.TotalBatches,p.TotalExperiments,p.UpdatedTime);
            }
        }

        // 静态 -> 实例方法 添加本地化文本
        private string GetShortPhaseText(DOEProjectPhase phase) => phase switch
        {
            DOEProjectPhase.Screening => _localizationService.GetString("Doe_ProjectPhase_Screening", "筛选"),
            DOEProjectPhase.PathSearch => _localizationService.GetString("Doe_ProjectPhase_PathSearch", "爬坡"),
            DOEProjectPhase.RSM => _localizationService.GetString("Doe_ProjectPhase_RSM", "RSM"),
            DOEProjectPhase.Augmenting => _localizationService.GetString("Doe_ProjectPhase_Augmenting", "补点"),
            DOEProjectPhase.Confirmation => _localizationService.GetString("Doe_ProjectPhase_Confirmation", "验证"),
            DOEProjectPhase.Completed=> _localizationService.GetString("Doe_ProjectPhase_Completed","已完成"),
            _ => phase.ToString() ?? ""
        };

        private static string GetPhaseColor(DOEProjectPhase phase) => phase switch
        {
            DOEProjectPhase.Screening => "#E67E22",
            DOEProjectPhase.PathSearch => "#3498DB",
            DOEProjectPhase.RSM => "#9B59B6",
            DOEProjectPhase.Augmenting => "#1ABC9C",
            DOEProjectPhase.Confirmation => "#27AE60",
            DOEProjectPhase.Completed => "#95A5A6",
            _ => "#7F8C8D"
        };

        private string GetDesignMethodText(DOEDesignMethod method) => method switch
        {
            DOEDesignMethod.CCD => _localizationService.GetString("Doe_DesignMethod_CCD", "CCD 中心复合"),
            DOEDesignMethod.BoxBehnken => _localizationService.GetString("Doe_DesignMethod_BoxBehnken", "Box-Behnken"),
            DOEDesignMethod.DOptimal => _localizationService.GetString("Doe_DesignMethod_DOptimal", "D-Optimal"),
            DOEDesignMethod.FullFactorial => _localizationService.GetString("Doe_DesignMethod_FullFactorial", "全因子"),
            DOEDesignMethod.FractionalFactorial => _localizationService.GetString("Doe_DesignMethod_FractionalFactorial", "部分因子"),
            DOEDesignMethod.Taguchi => _localizationService.GetString("Doe_DesignMethod_Taguchi", "Taguchi"),
            DOEDesignMethod.SteepestAscent => _localizationService.GetString("Doe_DesignMethod_SteepestAscent", "最速上升"),
            DOEDesignMethod.AugmentedDesign => _localizationService.GetString("Doe_DesignMethod_AugmentedDesign", "增强设计"),
            DOEDesignMethod.ConfirmationRuns => _localizationService.GetString("Doe_DesignMethod_ConfirmationRuns", "验证实验"),
            DOEDesignMethod.PlackettBurman => _localizationService.GetString("Doe_DesignMethod_PlackettBurman", "Plackett-Burman"),
            _ => method.ToString()
        };

        private static ObservableCollection<PhaseSegmentItem> BuildPhaseSegments(DOEProjectPhase currentPhase)
        {
            int currentIndex = currentPhase switch
            {
                DOEProjectPhase.Screening => 0,
                DOEProjectPhase.PathSearch => 1,
                DOEProjectPhase.RSM => 2,
                DOEProjectPhase.Augmenting => 2,
                DOEProjectPhase.Confirmation => 3,
                DOEProjectPhase.Completed => 5,
                _ => 0
            };

            var active = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#0078D4"));
            var current = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#0078D4")) { Opacity = 0.45 };
            var inactive = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#E8EAED"));

            var segments = new ObservableCollection<PhaseSegmentItem>();
            for (int i = 0; i < 5; i++)
            {
                if (i < currentIndex)
                    segments.Add(new PhaseSegmentItem { Color = active });
                else if (i == currentIndex)
                    segments.Add(new PhaseSegmentItem { Color = current });
                else
                    segments.Add(new PhaseSegmentItem { Color = inactive });
            }
            return segments;
        }

        private static string BuildPhaseProgressText(DOEProjectPhase currentPhase) => currentPhase switch
        {
            DOEProjectPhase.Completed => "已完成全部优化流程",
            _ => "筛选 → 爬坡 → RSM → 验证 → 完成"
        };
        private async Task FindResumableBatchAsync()
        {
            try
            {
                var batches = await _repository.GetRecentBatchesAsync(50);
                var resumable = batches.FirstOrDefault(b =>
                    b.Status == DOEBatchStatus.Running || b.Status == DOEBatchStatus.Paused);

                if (resumable != null)
                {
                    HasResumableBatch = true;
                    _resumableBatchId = resumable.BatchId;

                    // 单独查询该批次的 runs
                    var runs = await _repository.GetRunsAsync(resumable.BatchId);
                    var total = runs?.Count ?? 0;
                    var done = runs?.Count(r => r.Status == DOERunStatus.Completed) ?? 0;

                    var methodText = GetDesignMethodText(resumable.DesignMethod);
                    var roundText = resumable.RoundNumber.HasValue ? $"R{resumable.RoundNumber}" : "";
                    ResumableBatchSummary = string.Format(
                        _localizationService.GetString("Doe_Overview_Resumable_GroupExperi", "{0} · {1} · {2} 组实验"),roundText,methodText,total);

                    if (total > 0)
                    {
                        ResumableProgress = (double)done / total;
                        ResumableProgressText = $"{done}/{total}";
                    }
                    else
                    {
                        ResumableProgress = 0;
                        ResumableProgressText = _localizationService.GetString("Doe_Overview_Resumable_WatiExec", "待执行");
                    }
                }
                else
                {
                    HasResumableBatch = false;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "查找可恢复批次失败");
                HasResumableBatch = false;
            }
        }
        private async Task ShowProjectDetailAsync(string? projectId)
        {
            if (string.IsNullOrEmpty(projectId)) return;
            try
            {
                var vm = new DOEProjectDetailViewModel(_repository, _localizationService);
                await vm.LoadAsync(projectId);
                var dialog = new DOEProjectDetailDialog
                {
                    DataContext = vm,
                    Owner = System.Windows.Application.Current.Windows
                        .OfType<Window>()
                        .FirstOrDefault(w => w.IsActive)
                        ?? System.Windows.Application.Current.MainWindow
                };
                dialog.ShowDialog();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "打开项目详情失败");
            }
        }

        private async Task ArchiveProjectAsync(string? projectId)
        {
            if (string.IsNullOrEmpty(projectId)) return;
            var result = System.Windows.MessageBox.Show("确定要归档此项目吗？归档后不会删除数据。",
                "归档确认", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question);
            if (result != System.Windows.MessageBoxResult.Yes) return;
            try
            {
                await _repository.UpdateProjectStatusAsync(projectId, DOEProjectStatus.Archived);
                await LoadAsync();
            }
            catch (Exception ex) { _logger.LogError(ex, "归档项目失败"); }
        }

        private async Task DeleteProjectAsync(string? projectId)
        {
            if (string.IsNullOrEmpty(projectId)) return;
            var result = System.Windows.MessageBox.Show("确定要删除此项目吗？\n批次数据将保留但不再关联到项目。",
                "删除确认", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);
            if (result != System.Windows.MessageBoxResult.Yes) return;
            try
            {
                await _repository.DeleteProjectWithChildrenAsync(projectId);
                await LoadAsync();
            }
            catch (Exception ex) { _logger.LogError(ex, "删除项目失败"); }
        }
    }
    // <summary>最优因子项（名称+值）</summary>
    public class FocusFactorItem
    {
        public string Name { get; set; } = "";
        public string Value { get; set; } = "";
    }

    /// <summary>响应值项（名称+值）</summary>
    public class FocusResponseItem
    {
        public string Name { get; set; } = "";
        public string Value { get; set; } = "";
    }

    /// <summary>
    /// 项目卡片展示数据（供概览页 ItemsControl 绑定）
    /// </summary>
    public class ProjectCardItem : BindableBase
    {
        public string ProjectId { get; set; } = "";
        public string ProjectName { get; set; } = "";
        public string FlowName { get; set; } = "";
        public DOEProjectPhase CurrentPhase { get; set; }
        public string PhaseText { get; set; } = "";
        public DOEProjectStatus Status { get; set; }
        public int TotalBatches { get; set; }
        public int TotalExperiments { get; set; }
        public double? BestResponseValue { get; set; }
        public DateTime CreatedTime { get; set; }
        public DateTime UpdatedTime { get; set; }

        // 展示辅助
        public string PhaseColor { get; set; } = "#7F8C8D";
        public string StatusIcon { get; set; } = "▶";
        public string BestValueText { get; set; } = "—";
        public string SummaryText { get; set; } = "";

        /// <summary>是否可继续优化</summary>
        public bool CanContinue => Status == DOEProjectStatus.Active;
        /// <summary>阶段进度条分段数据（5段：筛选/爬坡/RSM/验证/完成）</summary>
        public ObservableCollection<PhaseSegmentItem> PhaseSegments { get; set; } = new();

        /// <summary>阶段进度文字（如 "筛选 → 爬坡 → RSM → 验证 → 完成"）</summary>
        public string PhaseProgressText { get; set; } = "";

        /// <summary>系统推荐文字（有推荐时显示）</summary>
        public string? RecommendationText { get; set; }

        /// <summary>是否有系统推荐</summary>
        public bool HasRecommendation => !string.IsNullOrEmpty(RecommendationText);
        /// <summary>详细摘要文字（如 "第 4 轮 · CCD 设计 · 72 组实验 · 04-06 13:53"）</summary>
        public string DetailSummaryText { get; set; } = "";

        /// <summary>因子+响应标签列表</summary>
        public ObservableCollection<TagItem> TagItems { get; set; } = new();
        /// <summary>是否是"新建项目"占位卡</summary>
        public bool IsNewPlaceholder { get; set; }

        /// <summary>是否被选中（底部卡片高亮）</summary>
        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set => SetProperty(ref _isSelected, value);
        }
        public ObservableCollection<FocusFactorItem> OptimalFactorItems { get; set; } = new();
        public ObservableCollection<FocusResponseItem> ResponseValueItems { get; set; } = new();
    }
    /// <summary>
    /// 阶段进度条分段项
    /// </summary>
    public class PhaseSegmentItem
    {
        /// <summary>分段颜色画刷</summary>
        public SolidColorBrush Color { get; set; } = new SolidColorBrush(Colors.LightGray);
    }
    /// <summary>
    /// 因子/响应标签项（供概览页 ItemsControl 绑定）
    /// </summary>
    public class TagItem
    {
        public string Text { get; set; } = "";
        public SolidColorBrush Foreground { get; set; } = new(Colors.Gray);
        public SolidColorBrush Background { get; set; } = new(Colors.LightGray);
    }

    /// <summary>
    /// 优化历程时间线项
    /// </summary>
    public class TimelineItem : BindableBase
    {
        public int RoundNumber { get; set; }
        public string PhaseText { get; set; } = "";
        public string EventText { get; set; } = "";
        public string MetaText { get; set; } = "";
        public bool IsLatest { get; set; }
        public bool ShowLine { get; set; }

        // 标签
        public bool HasChip { get; set; }
        public string ChipText { get; set; } = "";
        public string ChipBgColor { get; set; } = "#E8EAF6";
        public string ChipFgColor { get; set; } = "#2D3A8C";

        // ═══ XAML 绑定用的便捷属性 ═══

        /// <summary>时间线显示文字（如 "R9 · 验证"）</summary>
        public string RoundLabel => $"R{RoundNumber} · {PhaseText}";

        /// <summary>标签背景画刷</summary>
        public SolidColorBrush ChipBackground => new((Color)ColorConverter.ConvertFromString(ChipBgColor));

        /// <summary>标签前景画刷</summary>
        public SolidColorBrush ChipForeground => new((Color)ColorConverter.ConvertFromString(ChipFgColor));
    }
}
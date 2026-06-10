using MaxChemical.Infrastructure.DOE;
using MaxChemical.Infrastructure.Events;
using MaxChemical.Infrastructure.Services;
using MaxChemical.Logging;
using MaxChemical.Modules.DOE.Data;
using MaxChemical.Modules.DOE.Models;
using MaxChemical.Modules.DOE.Services;
using Newtonsoft.Json;
using Prism.Commands;
using Prism.Events;
using Prism.Ioc;
using Prism.Mvvm;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Media;

namespace MaxChemical.Modules.DOE.ViewModels
{
    /// <summary>
    /// DOE 历史管理 ViewModel — 项目 → 批次 树形结构
    /// 
    ///  重写: 左侧项目/批次树 → 右侧批次详情 + 数据表
    /// 
    /// 左侧: 项目树（展开后显示批次子节点，含编号圆、状态药丸）
    /// 右侧: 面包屑头 + 数据表
    /// </summary>
    public class DOEHistoryViewModel : BindableBase
    {
        private readonly IDOERepository _repository;
        private readonly IGPRModelService _gprService;
        private readonly DOEExportService _exportService;
        private readonly IFlowParameterProvider _paramProvider;
        private readonly IDialogService _dialogService;
        private readonly ILogService _logger;
        private readonly IEventAggregator _eventAggregator;
        private readonly ILocalizationService _localizationService;

        // ── 项目列表（左侧树）──
        private ObservableCollection<DOEProjectTreeNode> _projects = new();
        private DOEProjectTreeNode? _selectedProject;

        // ── 当前选中的批次 ──
        private RoundListItem? _selectedRound;
        private DOEBatch? _roundDetail;
        private DOERoundSummary? _roundSummary;

        private bool _isLoading;
        private string _statusMessage = "";

        // GPR 模型状态
        private string _gprStatusText = "未初始化";
        private bool _gprIsActive;
        private int _gprDataCount;

        private bool _hasSelectedRound;
        private string _selectedRoundTitle = "";
        private string _selectedRoundR2Text = "—";
        private int _selectedRoundRunCount;
        private string _selectedRoundRecommendation = "";
        private bool _hasSelectedRoundRecommendation;
        private string _selectedRoundDateText = "";
        private DataTable _selectedRoundRuns = new();

        public DOEHistoryViewModel(
            IDOERepository repository,
            IGPRModelService gprService,
            DOEExportService exportService,
            IFlowParameterProvider paramProvider,
            IDialogService dialogService,
            ILogService logger,
            ILocalizationService localizationService,
            IEventAggregator eventAggregator)
        {
            _repository = repository;
            _gprService = gprService;
            _exportService = exportService;
            _paramProvider = paramProvider;
            _dialogService = dialogService;
            _logger = logger?.ForContext<DOEHistoryViewModel>() ?? throw new ArgumentNullException(nameof(logger));
            _localizationService = localizationService ?? throw new ArgumentNullException(nameof(localizationService));
            _eventAggregator = eventAggregator ?? throw new ArgumentNullException(nameof(eventAggregator));
            // 注册本地化语言变更事件
            _eventAggregator.GetEvent<LanguageChangedEvent>()?.Subscribe(OnLanguageChangedEvent);

            RefreshCommand = new DelegateCommand(async () => await LoadProjectsAsync());
            SelectProjectCommand = new DelegateCommand<DOEProjectTreeNode>(OnSelectProject);
            DeleteProjectCommand = new DelegateCommand<DOEProjectTreeNode>(async s => await DeleteProjectAsync(s));
            DeleteRoundCommand = new DelegateCommand<RoundListItem>(async r => await DeleteRoundAsync(r));
            ExportRoundCommand = new DelegateCommand<RoundListItem>(async r => await ExportRoundAsync(r));
            NavigateToExecutionCommand = new DelegateCommand(
                () => { if (_selectedRound != null) RequestExecuteBatch?.Invoke(this, _selectedRound.BatchId); },
                () => _selectedRound != null);
            NavigateToAnalysisCommand = new DelegateCommand(
                () => { if (_selectedRound != null) RequestAnalyzeBatch?.Invoke(this, _selectedRound.BatchId); },
                () => _selectedRound != null);
            SelectRoundCommand = new DelegateCommand<RoundListItem>(r => { if (r != null) SelectedRound = r; });

            // 初始加载
            _ = LoadProjectsAsync();

            OnLanguageChangedEvent("");
        }

        // ══════════════ Properties ══════════════

        public ObservableCollection<DOEProjectTreeNode> Projects { get => _projects; set => SetProperty(ref _projects, value); }

        public DOEProjectTreeNode? SelectedProject
        {
            get => _selectedProject;
            set => SetProperty(ref _selectedProject, value);
        }

        public RoundListItem? SelectedRound
        {
            get => _selectedRound;
            set
            {
                SetProperty(ref _selectedRound, value);
                _ = OnSelectedRoundChangedAsync();
                NavigateToExecutionCommand.RaiseCanExecuteChanged();
                NavigateToAnalysisCommand.RaiseCanExecuteChanged();
            }
        }

        public DOEBatch? RoundDetail { get => _roundDetail; set => SetProperty(ref _roundDetail, value); }
        public DOERoundSummary? RoundSummary { get => _roundSummary; set => SetProperty(ref _roundSummary, value); }

        public bool IsLoading { get => _isLoading; set => SetProperty(ref _isLoading, value); }
        public string StatusMessage { get => _statusMessage; set => SetProperty(ref _statusMessage, value); }
        public string GPRStatusText { get => _gprStatusText; set => SetProperty(ref _gprStatusText, value); }
        public bool GPRIsActive { get => _gprIsActive; set => SetProperty(ref _gprIsActive, value); }
        public int GPRDataCount { get => _gprDataCount; set => SetProperty(ref _gprDataCount, value); }

        public bool HasSelectedProject => SelectedProject != null;
        public bool HasRoundSummary => RoundSummary != null;
        public bool HasSelectedRound { get => _hasSelectedRound; set => SetProperty(ref _hasSelectedRound, value); }
        public string SelectedRoundTitle { get => _selectedRoundTitle; set => SetProperty(ref _selectedRoundTitle, value); }
        public string SelectedRoundR2Text { get => _selectedRoundR2Text; set => SetProperty(ref _selectedRoundR2Text, value); }
        public int SelectedRoundRunCount { get => _selectedRoundRunCount; set => SetProperty(ref _selectedRoundRunCount, value); }
        public string SelectedRoundRecommendation { get => _selectedRoundRecommendation; set => SetProperty(ref _selectedRoundRecommendation, value); }
        public bool HasSelectedRoundRecommendation { get => _hasSelectedRoundRecommendation; set => SetProperty(ref _hasSelectedRoundRecommendation, value); }
        public string SelectedRoundDateText { get => _selectedRoundDateText; set => SetProperty(ref _selectedRoundDateText, value); }
        public DataTable SelectedRoundRuns { get => _selectedRoundRuns; set => SetProperty(ref _selectedRoundRuns, value); }

        // ══════════════ Commands ══════════════

        public DelegateCommand RefreshCommand { get; }
        public DelegateCommand<DOEProjectTreeNode> SelectProjectCommand { get; }
        public DelegateCommand<DOEProjectTreeNode> DeleteProjectCommand { get; }
        public DelegateCommand<RoundListItem> DeleteRoundCommand { get; }
        public DelegateCommand<RoundListItem> ExportRoundCommand { get; }
        public DelegateCommand NavigateToExecutionCommand { get; }
        public DelegateCommand NavigateToAnalysisCommand { get; }
        public DelegateCommand<RoundListItem> SelectRoundCommand { get; }

        // ══════════════ Events ══════════════

        public event EventHandler<string>? RequestExecuteBatch;
        public event EventHandler<string>? RequestAnalyzeBatch;

        // ══════════════ 加载 ══════════════

        public async Task LoadProjectsAsync()
        {
            try
            {
                IsLoading = true;
                var projects = await _repository.GetAllProjectSummariesAsync(50);
                var treeNodes = new List<DOEProjectTreeNode>();
                int totalBatches = 0;

                foreach (var p in projects)
                {
                    var node = new DOEProjectTreeNode
                    {
                        ProjectId = p.ProjectId,
                        ProjectName = p.ProjectName,
                        PhaseText = p.PhaseText,
                        TotalBatches = p.TotalBatches,
                        TotalExperiments = p.TotalExperiments,
                        BestResponseValue = p.BestResponseValue,
                    };
                    totalBatches += p.TotalBatches;
                    treeNodes.Add(node);
                }

                Projects = new ObservableCollection<DOEProjectTreeNode>(treeNodes);
                StatusMessage = string.Format(_localizationService.GetString("Doe_History_StateMsg_ProjsBatches", "{0} 个项目 · {1} 批"), projects.Count, totalBatches);

                // GPR 状态
                var flowId = _paramProvider.GetCurrentFlowId();
                if (!string.IsNullOrEmpty(flowId))
                {
                    var state = await _repository.GetGPRModelStateAsync(flowId);
                    if (state != null)
                    {
                        GPRIsActive = state.IsActive;
                        GPRDataCount = state.DataCount;
                        GPRStatusText = state.IsActive
                            ? $"已激活 | R²={state.RSquared:F3} | {state.DataCount} 组数据"
                            : $"未激活 | {state.DataCount} 组数据";
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "加载项目列表失败");
                StatusMessage = string.Format(_localizationService.GetString("Doe_Histroy_StateMsg_LoadFail", "加载失败: {0}"),ex.Message);
            }
            finally
            {
                IsLoading = false;
            }
        }

        /// <summary>兼容旧调用点</summary>
        public async Task LoadBatchesAsync() => await LoadProjectsAsync();

        /// <summary>点击项目行：展开/折叠 + 懒加载批次</summary>
        private async void OnSelectProject(DOEProjectTreeNode? node)
        {
            if (node == null) return;

            // 折叠其它项目
            foreach (var p in Projects)
            {
                if (p != node)
                    p.IsExpanded = false;
            }

            // 切换展开
            node.IsExpanded = !node.IsExpanded;
            SelectedProject = node.IsExpanded ? node : null;
            RaisePropertyChanged(nameof(HasSelectedProject));

            if (!node.IsExpanded)
                return;

            // 懒加载批次
            if (node.Batches.Count == 0)
            {
                await LoadBatchesForProjectAsync(node);
            }
        }

        private async Task LoadBatchesForProjectAsync(DOEProjectTreeNode node)
        {
            try
            {
                IsLoading = true;
                var batches = await _repository.GetBatchesByProjectAsync(node.ProjectId);
                var summaries = await _repository.GetRoundSummariesAsync(node.ProjectId);

                var roundItems = new List<RoundListItem>();
                foreach (var b in batches)
                {
                    var summary = summaries.FirstOrDefault(s => s.BatchId == b.BatchId);

                    int totalRuns = 0;
                    int completedRuns = 0;
                    try
                    {
                        var runs = await _repository.GetRunsAsync(b.BatchId);
                        totalRuns = runs.Count;
                        completedRuns = runs.Count(r => r.Status == DOERunStatus.Completed);
                    }
                    catch { }

                    roundItems.Add(new RoundListItem
                    {
                        BatchId = b.BatchId,
                        BatchName = b.BatchName,
                        RoundNumber = b.RoundNumber ?? 0,
                        Phase = b.ProjectPhase ?? DOEProjectPhase.Screening,
                        PhaseText = (b.ProjectPhase ?? DOEProjectPhase.Screening) switch
                        {
                            DOEProjectPhase.Screening => _localizationService.GetString("Doe_ProjectPhase_Screening", "筛选"),
                            DOEProjectPhase.PathSearch => _localizationService.GetString("Doe_ProjectPhase_PathSearch", "爬坡"),
                            DOEProjectPhase.RSM => _localizationService.GetString("Doe_ProjectPhase_RSM", "RSM"),
                            DOEProjectPhase.Augmenting => _localizationService.GetString("Doe_ProjectPhase_Augmenting", "补点"),
                            DOEProjectPhase.Confirmation => _localizationService.GetString("Doe_ProjectPhase_Confirmation", "验证"),
                            _ => b.ProjectPhase?.ToString() ?? ""
                        },
                        DesignMethod = b.DesignMethod,
                        DesignMethodText = b.DesignMethod switch
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
                            _ => b.DesignMethod.ToString()
                        },
                        Status = b.Status,
                        TotalRuns = totalRuns,
                        CompletedRuns = completedRuns,
                        ProgressText = $"{completedRuns}/{totalRuns}",
                        RSquared = summary?.RSquared,
                        RSquaredText = summary?.RSquared.HasValue == true ? $"R²={summary.RSquared:F3}" : "",
                        Recommendation = summary?.RecommendationReason ?? "",
                        CreatedTime = b.CreatedTime
                    });
                }

                node.Batches = new ObservableCollection<RoundListItem>(roundItems);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "加载项目批次失败");
            }
            finally
            {
                IsLoading = false;
            }
        }

        private async Task OnSelectedRoundChangedAsync()
        {
            // 清理所有项目下的批次选中状态
            foreach (var p in Projects)
                foreach (var r in p.Batches)
                    r.IsSelected = false;

            RoundSummary = null;
            RaisePropertyChanged(nameof(HasRoundSummary));

            if (_selectedRound == null)
            {
                RoundDetail = null;
                HasSelectedRound = false;
                SelectedRoundRuns = new DataTable();
                return;
            }

            _selectedRound.IsSelected = true;
            HasSelectedRound = true;

            try
            {
                RoundDetail = await _repository.GetBatchWithDetailsAsync(_selectedRound.BatchId);
                RoundSummary = await _repository.GetRoundSummaryByBatchAsync(_selectedRound.BatchId);
                RaisePropertyChanged(nameof(HasRoundSummary));

                SelectedRoundTitle = string.Format(_localizationService.GetString("Doe_History_Batch_Number", "批次 {0} · {1}"),_selectedRound.RoundNumber,_selectedRound.DesignMethodText);
                SelectedRoundR2Text = _selectedRound.RSquaredText.Contains("R²=")
                    ? _selectedRound.RSquaredText.Replace("R²=", "")
                    : "—";
                SelectedRoundRecommendation = RoundSummary?.RecommendationReason ?? "";
                HasSelectedRoundRecommendation = !string.IsNullOrEmpty(SelectedRoundRecommendation);
                SelectedRoundDateText = _selectedRound.CreatedTime != default
                    ? $" · {_selectedRound.CreatedTime:yyyy-MM-dd}"
                    : "";

                await LoadRunDataTableAsync(_selectedRound.BatchId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "加载轮次详情失败");
            }
        }

        private async Task LoadRunDataTableAsync(string batchId)
        {
            try
            {
                var batch = RoundDetail ?? await _repository.GetBatchWithDetailsAsync(batchId);
                if (batch == null) { SelectedRoundRuns = new DataTable(); return; }

                var runs = await _repository.GetRunsAsync(batchId);
                SelectedRoundRunCount = runs.Count;

                var factors = batch.Factors ?? await _repository.GetFactorsAsync(batchId);
                var responses = batch.Responses ?? await _repository.GetResponsesAsync(batchId);

                var factorNames = factors.OrderBy(f => f.SortOrder).Select(f => f.FactorName).ToList();
                var responseNames = responses.OrderBy(r => r.SortOrder).Select(r => r.ResponseName).ToList();

                var dt = new DataTable();
                dt.Columns.Add("#", typeof(int));
                foreach (var fn in factorNames)
                    dt.Columns.Add(fn, typeof(string));
                foreach (var rn in responseNames)
                    dt.Columns.Add(rn, typeof(string));
                dt.Columns.Add("状态", typeof(string));

                foreach (var run in runs.OrderBy(r => r.RunIndex))
                {
                    var row = dt.NewRow();
                    row["#"] = run.RunIndex + 1;

                    try
                    {
                        var factorValues = JsonConvert.DeserializeObject<Dictionary<string, object>>(run.FactorValuesJson ?? "{}");
                        if (factorValues != null)
                            foreach (var fn in factorNames)
                                if (factorValues.TryGetValue(fn, out var val))
                                    row[fn] = val?.ToString() ?? "";
                    }
                    catch { }

                    try
                    {
                        var respValues = JsonConvert.DeserializeObject<Dictionary<string, double>>(run.ResponseValuesJson ?? "{}");
                        if (respValues != null)
                            foreach (var rn in responseNames)
                                if (respValues.TryGetValue(rn, out var val))
                                    row[rn] = val.ToString("F2");
                    }
                    catch { }

                    row["状态"] = run.Status switch
                    {
                        DOERunStatus.Completed => "已完成",
                        DOERunStatus.Failed => "失败",
                        DOERunStatus.Pending => "待执行",
                        DOERunStatus.Running => "执行中",
                        _ => run.Status.ToString()
                    };

                    dt.Rows.Add(row);
                }

                SelectedRoundRuns = dt;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "加载实验数据表失败: {BatchId}", batchId);
                SelectedRoundRuns = new DataTable();
            }
        }

        // ══════════════ 操作 ══════════════

        private async Task DeleteProjectAsync(DOEProjectTreeNode? project)
        {
            if (project == null) return;
            if (!_dialogService.ShowConfirmation(
                $"确定删除项目 \"{project.ProjectName}\"？\n" +
                $"该项目包含 {project.TotalBatches} 批、{project.TotalExperiments} 组实验数据。\n" +
                $"项目下的批次将被解除关联（不删除实验数据）。\n\n此操作不可恢复。",
                "删除项目"))
                return;

            try
            {
                await _repository.DeleteProjectWithChildrenAsync(project.ProjectId);
                Projects.Remove(project);
                StatusMessage = string.Format(_localizationService.GetString("Doe_History_StateMsg_AlreadyDel", "已删除项目: {0}"),project.ProjectName);

                if (SelectedProject?.ProjectId == project.ProjectId)
                {
                    SelectedProject = null;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "删除项目失败");
                _dialogService.ShowError($"删除失败: {ex.Message}", "错误");
            }
        }

        private async Task DeleteRoundAsync(RoundListItem? round)
        {
            if (round == null || SelectedProject == null) return;
            if (!_dialogService.ShowConfirmation(
                $"确定删除批次 {round.RoundNumber} \"{round.BatchName}\"？\n此操作不可恢复。",
                "删除批次"))
                return;

            try
            {
                await _repository.DeleteBatchWithChildrenAsync(round.BatchId);
                SelectedProject.Batches.Remove(round);
                StatusMessage = string.Format(_localizationService.GetString("Doe_History_StateMsg_DelBatch", "已删除批次: {0}"),round.BatchName);

                if (SelectedRound?.BatchId == round.BatchId)
                {
                    SelectedRound = null;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "删除轮次失败");
                _dialogService.ShowError($"删除失败: {ex.Message}", "错误");
            }
        }

        private async Task ExportRoundAsync(RoundListItem? round)
        {
            if (round == null) return;

            var path = _dialogService.ShowSaveFileDialog(
                $"DOE报告_{round.BatchName}_{DateTime.Now:yyyyMMdd}.xlsx",
                "Excel 文件|*.xlsx");
            if (string.IsNullOrEmpty(path)) return;

            try
            {
                IsLoading = true;
                StatusMessage = _localizationService.GetString("Doe_History_StateMsg_Exporting", "正在导出报告...");
                await _exportService.ExportBatchReportAsync(round.BatchId, path);
                StatusMessage = string.Format(_localizationService.GetString("Doe_History_StateMsg_AlreadyExport", "报告已导出: {0}"), path);
                _dialogService.ShowInfo($"DOE 报告已导出至:\n{path}", "导出成功");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "导出报告失败");
                _dialogService.ShowError($"导出失败: {ex.Message}", "错误");
            }
            finally
            {
                IsLoading = false;
            }
        }


        #region 本地化

        private string _projBatchTxt;
        private string _refreshTxt;
        private string _groupTxt;
        private string _batchTxt;
        private string _groupsPeriTxt;
        private string _jumpExecTxt;
        private string _viewAnalysisTxt;
        private string _exportTxt;
        private string _deleteTxt;
        private string _unselectTxt;

        public string ProjBatchTxt { get { return _projBatchTxt; } set { SetProperty(ref _projBatchTxt, value); } }
        public string RefreshTxt { get { return _refreshTxt; } set { SetProperty(ref _refreshTxt, value); }  }
        public string GroupTxt { get { return _groupTxt; } set { SetProperty(ref _groupTxt, value); } }
        public string BatchTxt { get { return _batchTxt; } set { SetProperty(ref _batchTxt, value); } }
        public string GroupsPeriTxt { get { return _groupsPeriTxt; } set { SetProperty(ref _groupsPeriTxt, value); } }
        public string JumpExecTxt { get { return _jumpExecTxt; } set { SetProperty(ref _jumpExecTxt, value); } }
        public string ViewAnalysisTxt { get { return _viewAnalysisTxt; } set { SetProperty(ref _viewAnalysisTxt, value); } }
        public string ExportTxt { get { return _exportTxt; } set { SetProperty(ref _exportTxt, value); } }
        public string DeleteTxt { get { return _deleteTxt; } set { SetProperty(ref _deleteTxt, value); } }
        public string UnselectTxt { get { return _unselectTxt; } set { SetProperty(ref _unselectTxt, value); } }

        private void OnLanguageChangedEvent(string language)
        {
            ProjBatchTxt = _localizationService.GetString("Doe_History_ProjBatch", "项目 / 批次");
            GroupTxt = _localizationService.GetString("Doe_History_Group", " 组");
            RefreshTxt = _localizationService.GetString("Doe_History_Refresh", "刷新");
            BatchTxt = _localizationService.GetString("Doe_History_Batch", "批 ");
            GroupsPeriTxt = _localizationService.GetString("Doe_History_GroupsExpri", "组实验");
            JumpExecTxt = _localizationService.GetString("Doe_History_JumpExec", "跳转执行");
            ViewAnalysisTxt = _localizationService.GetString("Doe_History_ViewAnalysis", "查看分析");
            ExportTxt = _localizationService.GetString("Doe_History_Export", "导出");
            DeleteTxt = _localizationService.GetString("Doe_History_Delete", "删除");
            UnselectTxt = _localizationService.GetString("Doe_History_UnselectTip", "选择左侧批次查看实验数据");
        }

        #endregion
    }

    // ══════════════════════════════════════════════════════
    //  项目树节点（左侧树形结构）
    // ══════════════════════════════════════════════════════
    public class DOEProjectTreeNode : BindableBase
    {
        public string ProjectId { get; set; } = "";
        public string ProjectName { get; set; } = "";
        public string PhaseText { get; set; } = "";
        public int TotalBatches { get; set; }
        public int TotalExperiments { get; set; }
        public double? BestResponseValue { get; set; }

        private bool _isExpanded;
        public bool IsExpanded
        {
            get => _isExpanded;
            set
            {
                SetProperty(ref _isExpanded, value);
                RaisePropertyChanged(nameof(ChevronData));
                RaisePropertyChanged(nameof(ChevronColor));
                RaisePropertyChanged(nameof(ProjectNameColor));
                RaisePropertyChanged(nameof(SubtitleColor));
            }
        }

        private ObservableCollection<RoundListItem> _batches = new();
        public ObservableCollection<RoundListItem> Batches
        {
            get => _batches;
            set => SetProperty(ref _batches, value);
        }

        /// <summary>展开向下 ▼ / 折叠向右 ▶ — 匹配 HTML SVG Path</summary>
        public string ChevronData => IsExpanded
            ? "M2,3 L5,6 L8,3"   // 向下
            : "M3,2 L6,5 L3,8";  // 向右

        /// <summary>展开时靛蓝，折叠时灰色</summary>
        public SolidColorBrush ChevronColor => new(IsExpanded ? Hex("#2D3A8C") : Hex("#9CA3AF"));
        public SolidColorBrush ProjectNameColor => new(IsExpanded ? Hex("#2D3A8C") : Hex("#374151"));
        public SolidColorBrush SubtitleColor => new(IsExpanded ? Hex("#6B7280") : Hex("#9CA3AF"));

        private static Color Hex(string hex) => (Color)ColorConverter.ConvertFromString(hex);
    }

    // ══════════════════════════════════════════════════════
    //  批次列表项（树子节点 + 右侧详情绑定）
    // ══════════════════════════════════════════════════════
    public class RoundListItem : BindableBase
    {
        private readonly ILocalizationService _localization;

        public RoundListItem()
        {
            _localization = ContainerLocator.Container.Resolve<ILocalizationService>();
        }

        public string BatchId { get; set; } = "";
        public string BatchName { get; set; } = "";
        public int RoundNumber { get; set; }
        public DOEProjectPhase Phase { get; set; }
        public string PhaseText { get; set; } = "";
        public DOEDesignMethod DesignMethod { get; set; }
        public string DesignMethodText { get; set; } = "";
        public DOEBatchStatus Status { get; set; }
        public int TotalRuns { get; set; }
        public int CompletedRuns { get; set; }
        public string ProgressText { get; set; } = "";
        public double? RSquared { get; set; }
        public string RSquaredText { get; set; } = "";
        public string Recommendation { get; set; } = "";
        public DateTime CreatedTime { get; set; }

        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                SetProperty(ref _isSelected, value);
                RaisePropertyChanged(nameof(TitleColor));
            }
        }

        // ── 显示属性 ──

        public string StatusText => Status switch
        {
            DOEBatchStatus.Ready => _localization?.GetString("Doe_History_BatchStatus_Ready", "就绪")?? "就绪",
            DOEBatchStatus.Running => _localization?.GetString("Doe_History_BatchStatus_Running", "执行中")?? "执行中",
            DOEBatchStatus.Paused => _localization?.GetString("Doe_History_BatchStatus_Paused", "已暂停") ?? "已暂停",
            DOEBatchStatus.Completed => _localization?.GetString("Doe_History_BatchStatus_Completed", "已完成") ?? "已完成",
            DOEBatchStatus.Aborted => _localization?.GetString("Doe_History_BatchStatus_Aborted", "已中止") ?? "已中止",
            _ => Status.ToString()
        };

        /// <summary>R² 在左侧子节点的显示文本，带前缀</summary>
        public string RSquaredDisplayText => RSquared.HasValue
            ? $" · R²={RSquared:F4}"
            : "";

        /// <summary>选中时加深标题色</summary>
        public SolidColorBrush TitleColor => new(IsSelected ? Hex("#1E2A6E") : Hex("#1A1F36"));
        public SolidColorBrush SubtitleColor => new(Hex("#6B7280"));

        /// <summary>编号圆背景色 — 匹配 HTML 阶段色</summary>
        public Color PhaseColor => Phase switch
        {
            DOEProjectPhase.Screening => Hex("#D97706"),
            DOEProjectPhase.PathSearch => Hex("#3B5BDB"),
            DOEProjectPhase.RSM => Hex("#2D3A8C"),
            DOEProjectPhase.Augmenting => Hex("#1ABC9C"),
            DOEProjectPhase.Confirmation => Hex("#27AE60"),
            _ => Hex("#6B7280")
        };

        /// <summary>状态药丸背景色</summary>
        public Color StatusBgColor => Status switch
        {
            DOEBatchStatus.Completed => Hex("#ECFDF5"),
            DOEBatchStatus.Running => Hex("#FEF3C7"),
            DOEBatchStatus.Ready => Hex("#DBEAFE"),
            DOEBatchStatus.Paused => Hex("#F3F4F6"),
            DOEBatchStatus.Aborted => Hex("#FEF2F2"),
            _ => Hex("#F3F4F6")
        };

        /// <summary>状态药丸文本色</summary>
        public Color StatusFgColor => Status switch
        {
            DOEBatchStatus.Completed => Hex("#059669"),
            DOEBatchStatus.Running => Hex("#D97706"),
            DOEBatchStatus.Ready => Hex("#2563EB"),
            DOEBatchStatus.Paused => Hex("#6B7280"),
            DOEBatchStatus.Aborted => Hex("#DC2626"),
            _ => Hex("#6B7280")
        };

        private static Color Hex(string hex) => (Color)ColorConverter.ConvertFromString(hex);
    }
}
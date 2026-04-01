using MaxChemical.Infrastructure.DOE;
using MaxChemical.Infrastructure.Services;
using MaxChemical.Logging;
using MaxChemical.Modules.DOE.Data;
using MaxChemical.Modules.DOE.Models;
using MaxChemical.Modules.DOE.Services;
using Newtonsoft.Json;
using OfficeOpenXml;
using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Series;
using Prism.Commands;
using Prism.Mvvm;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Data;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MaxChemical.Modules.DOE.ViewModels
{
    public class GPRModelTabItem : BindableBase
    {
        public int ModelId { get; set; }
        public string ModelName { get; set; } = "";
        public string FactorSignature { get; set; } = "";
        public bool IsActive { get; set; }
        public bool IsColdStart { get; set; }
        public int DataCount { get; set; }
        public double? RSquared { get; set; }
        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (SetProperty(ref _isSelected, value))
                {
                    RaisePropertyChanged(nameof(TabBackground));
                    RaisePropertyChanged(nameof(TabBorderBrush));
                    RaisePropertyChanged(nameof(TabForeground));
                    RaisePropertyChanged(nameof(TabFontWeight));
                }
            }
        }
        public string ShortInfo => IsActive ? $"R² {RSquared:F2}" : IsColdStart ? $"{DataCount}/6" : "未训练";
        public SolidColorBrush StatusDotColor => IsActive ? new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50))
            : IsColdStart ? new SolidColorBrush(Color.FromRgb(0xFF, 0xA0, 0x00))
            : new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC));
        public SolidColorBrush TabBackground => IsSelected ? new SolidColorBrush(Color.FromRgb(0xF5, 0xF9, 0xFF)) : Brushes.White;
        public SolidColorBrush TabBorderBrush => IsSelected ? new SolidColorBrush(Color.FromRgb(0x00, 0x78, 0xD4)) : new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xDD));
        public SolidColorBrush TabForeground => IsSelected ? new SolidColorBrush(Color.FromRgb(0x00, 0x78, 0xD4)) : new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55));
        public string TabFontWeight => IsSelected ? "SemiBold" : "Normal";
    }

    public class GPRModelDetail : BindableBase
    {
        public int ModelId { get; set; }
        public string ModelName { get; set; } = "";
        public string FactorSignature { get; set; } = "";
        public bool IsActive { get; set; }
        public int DataCount { get; set; }
        public double? RSquared { get; set; }
        public double? RMSE { get; set; }
        public DateTime? LastTrainedTime { get; set; }
        public string? EvolutionHistoryJson { get; set; }
        public string StatusText => IsActive ? "已激活" : DataCount > 0 ? $"冷启动 {DataCount}/6" : "未训练";
        public SolidColorBrush StatusColor => IsActive ? new SolidColorBrush(Color.FromRgb(0x2E, 0x7D, 0x32)) : new SolidColorBrush(Color.FromRgb(0xE6, 0x51, 0x00));
        public SolidColorBrush StatusDotColor => IsActive ? new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50)) : new SolidColorBrush(Color.FromRgb(0xFF, 0xA0, 0x00));
        public string StatusHint => IsActive ? "≥6 组达标" : $"需 {6 - DataCount} 组";
        public string DataCountText => DataCount.ToString();
        public string RSquaredText => IsActive ? $"{RSquared:F4}" : "—";
        public string RMSEText => IsActive ? $"{RMSE:F2}" : "—";
        public string LastTrainedText => LastTrainedTime?.ToString("yyyy-MM-dd HH:mm:ss") ?? "—";
        public string StatusSummary => IsActive ? $"已激活 · {DataCount}组 · 上次训练 {LastTrainedTime:HH:mm}" : $"冷启动 {DataCount}/6";
    }

    public class OptimalFactorItem
    {
        public string Key { get; set; } = "";
        public double Value { get; set; }
        public double BarWidth { get; set; }
    }

    /// <summary>
    /// OLS 批次选择项
    /// </summary>
    public class OlsBatchItem : BindableBase
    {
        public string BatchId { get; set; } = "";
        public string BatchName { get; set; } = "";
        public string DesignMethod { get; set; } = "";
        public int CompletedCount { get; set; }
        public int FactorCount { get; set; }
        public string DisplayText => $"{BatchName} ({DesignMethod}, {CompletedCount}组)";
        private bool _isSelected;
        public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }
    }

    /// <summary>
    /// 模型分析 ViewModel — 方案B: GPR/OLS 双 Tab 布局
    /// GPR Tab: 按模型查看（跨批次累积）
    /// OLS Tab: 按批次独立分析（先选批次再看 ANOVA）
    /// </summary>
    public class DOEModelAnalysisViewModel : BindableBase
    {
        private readonly IGPRModelService _gprService;
        private readonly IDOEAnalysisService _analysisService;
        private readonly IDOERepository _repository;
        private readonly IFlowParameterProvider _paramProvider;
        private readonly IDialogService _dialogService;
        private readonly DOEExportService _exportService;
        private readonly ILogService _logger;
        private readonly IModelRouter _modelRouter;
        private readonly IDesirabilityService _desirabilityService;

        // GPR
        private ObservableCollection<GPRModelTabItem> _models = new();
        private GPRModelDetail _selectedModel = new();
        private bool _isLoading;
        private string _statusMessage = "";
        private List<OptimalFactorItem> _optimalFactors = new();
        private string _optimalResponseText = "—";
        private string _optimalConfidenceText = "";
        private bool _hasOptimalResult;
        private PlotModel? _evolutionPlot;
        private PlotModel? _sensitivityPlot;
        private PlotModel? _actualVsPredictedPlot;
        private PlotModel? _residualPlot;
        private BitmapImage? _surfaceImage;
        private PlotModel? _surfacePlot;
        private List<string> _factorNames = new();
        private string? _surfaceFactor1;
        private string? _surfaceFactor2;
        private string _importStatusText = "";
        private DataTable _fullTrainingData = new();
        private DataTable _pagedTrainingData = new();
        private int _currentPage = 1;
        private int _totalPages = 1;
        private int _totalDataCount;
        private const int PAGE_SIZE = 10;
        private string _currentFlowId = "";
        private string _currentBatchId = "";

        // Tab 切换
        private bool _isGprTabSelected = true;

        // OLS Tab
        private ObservableCollection<OlsBatchItem> _olsBatchItems = new();
        private OlsBatchItem? _selectedOlsBatch;
        private List<string> _responseNames = new();
        private string _selectedResponseName = "";
        private OLSAnalysisResult? _olsResult;
        private string _olsStatusText = "切换到此页后将自动加载可分析的批次列表";

        // Desirability
        private DesirabilityResult? _desirabilityResult;

        public DOEModelAnalysisViewModel(
            IGPRModelService gprService, IDOEAnalysisService analysisService,
            IDOERepository repository, IFlowParameterProvider paramProvider,
            IDialogService dialogService, DOEExportService exportService,
            IModelRouter modelRouter, IDesirabilityService desirabilityService,
            ILogService logger)
        {
            _gprService = gprService; _analysisService = analysisService;
            _repository = repository; _paramProvider = paramProvider;
            _dialogService = dialogService; _exportService = exportService;
            _modelRouter = modelRouter; _desirabilityService = desirabilityService;
            _logger = logger?.ForContext<DOEModelAnalysisViewModel>() ?? throw new ArgumentNullException(nameof(logger));

            RefreshAllCommand = new DelegateCommand(async () => await RefreshAllAsync());
            RetrainCommand = new DelegateCommand(async () => await RetrainAsync());
            FindOptimalCommand = new DelegateCommand(async () => await FindOptimalAsync());
            DeleteModelCommand = new DelegateCommand(async () => await DeleteModelAsync());
            GenerateSurfaceCommand = new DelegateCommand(async () => await LoadSurfaceAsync(),
                () => !string.IsNullOrEmpty(SurfaceFactor1) && !string.IsNullOrEmpty(SurfaceFactor2) && SurfaceFactor1 != SurfaceFactor2);
            DownloadTemplateCommand = new DelegateCommand(async () => await DownloadTemplateAsync());
            ImportDataCommand = new DelegateCommand(async () => await ImportDataFromExcelAsync());
            PrevPageCommand = new DelegateCommand(() => GoToPage(CurrentPage - 1), () => CurrentPage > 1);
            NextPageCommand = new DelegateCommand(() => GoToPage(CurrentPage + 1), () => CurrentPage < TotalPages);
            SwitchToGprTabCommand = new DelegateCommand(() => SwitchTab(true));
            SwitchToOlsTabCommand = new DelegateCommand(() => SwitchTab(false));
            SelectOlsBatchCommand = new DelegateCommand<OlsBatchItem>(async b => await SelectOlsBatchAsync(b));
            RunDesirabilityCommand = new DelegateCommand(async () => await RunDesirabilityOptimizationAsync());
        }

        // ══════════════ Properties — GPR ══════════════
        public ObservableCollection<GPRModelTabItem> Models { get => _models; set => SetProperty(ref _models, value); }
        public GPRModelDetail SelectedModel { get => _selectedModel; set => SetProperty(ref _selectedModel, value); }
        public bool IsLoading { get => _isLoading; set => SetProperty(ref _isLoading, value); }
        public string StatusMessage { get => _statusMessage; set => SetProperty(ref _statusMessage, value); }
        public List<OptimalFactorItem> OptimalFactors { get => _optimalFactors; set => SetProperty(ref _optimalFactors, value); }
        public string OptimalResponseText { get => _optimalResponseText; set => SetProperty(ref _optimalResponseText, value); }
        public string OptimalConfidenceText { get => _optimalConfidenceText; set => SetProperty(ref _optimalConfidenceText, value); }
        public bool HasOptimalResult { get => _hasOptimalResult; set => SetProperty(ref _hasOptimalResult, value); }
        public PlotModel? EvolutionPlot { get => _evolutionPlot; set => SetProperty(ref _evolutionPlot, value); }
        public PlotModel? SensitivityPlot { get => _sensitivityPlot; set => SetProperty(ref _sensitivityPlot, value); }
        public PlotModel? ActualVsPredictedPlot { get => _actualVsPredictedPlot; set => SetProperty(ref _actualVsPredictedPlot, value); }
        public PlotModel? ResidualPlot { get => _residualPlot; set => SetProperty(ref _residualPlot, value); }
        public BitmapImage? SurfaceImage { get => _surfaceImage; set => SetProperty(ref _surfaceImage, value); }
        public PlotModel? SurfacePlot { get => _surfacePlot; set => SetProperty(ref _surfacePlot, value); }
        public List<string> FactorNames { get => _factorNames; set => SetProperty(ref _factorNames, value); }
        public string? SurfaceFactor1 { get => _surfaceFactor1; set { SetProperty(ref _surfaceFactor1, value); GenerateSurfaceCommand.RaiseCanExecuteChanged(); } }
        public string? SurfaceFactor2 { get => _surfaceFactor2; set { SetProperty(ref _surfaceFactor2, value); GenerateSurfaceCommand.RaiseCanExecuteChanged(); } }
        public DataTable PagedTrainingData { get => _pagedTrainingData; set => SetProperty(ref _pagedTrainingData, value); }
        public int CurrentPage { get => _currentPage; set { if (SetProperty(ref _currentPage, value)) { PrevPageCommand.RaiseCanExecuteChanged(); NextPageCommand.RaiseCanExecuteChanged(); } } }
        public int TotalPages { get => _totalPages; set { if (SetProperty(ref _totalPages, value)) { PrevPageCommand.RaiseCanExecuteChanged(); NextPageCommand.RaiseCanExecuteChanged(); } } }
        public int TotalDataCount { get => _totalDataCount; set => SetProperty(ref _totalDataCount, value); }
        public string ImportStatusText { get => _importStatusText; set => SetProperty(ref _importStatusText, value); }

        // ══════════════ Properties — Tab 切换 ══════════════
        public bool IsGprTabSelected
        {
            get => _isGprTabSelected;
            set { if (SetProperty(ref _isGprTabSelected, value)) { RaisePropertyChanged(nameof(IsOlsTabSelected)); RaisePropertyChanged(nameof(GprTabBackground)); RaisePropertyChanged(nameof(GprTabForeground)); RaisePropertyChanged(nameof(OlsTabBackground)); RaisePropertyChanged(nameof(OlsTabForeground)); } }
        }
        public bool IsOlsTabSelected => !_isGprTabSelected;
        public SolidColorBrush GprTabBackground => IsGprTabSelected ? new SolidColorBrush(Color.FromRgb(0x00, 0x78, 0xD4)) : Brushes.White;
        public SolidColorBrush GprTabForeground => IsGprTabSelected ? Brushes.White : new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55));
        public SolidColorBrush OlsTabBackground => IsOlsTabSelected ? new SolidColorBrush(Color.FromRgb(0x2E, 0x86, 0xC1)) : Brushes.White;
        public SolidColorBrush OlsTabForeground => IsOlsTabSelected ? Brushes.White : new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55));

        // ══════════════ Properties — OLS Tab ══════════════
        public ObservableCollection<OlsBatchItem> OlsBatchItems { get => _olsBatchItems; set => SetProperty(ref _olsBatchItems, value); }
        public OlsBatchItem? SelectedOlsBatch { get => _selectedOlsBatch; set => SetProperty(ref _selectedOlsBatch, value); }
        public List<string> ResponseNames { get => _responseNames; set => SetProperty(ref _responseNames, value); }
        public string SelectedResponseName
        {
            get => _selectedResponseName;
            set { if (SetProperty(ref _selectedResponseName, value) && IsOlsTabSelected && SelectedOlsBatch != null) _ = RefreshOlsAnalysisAsync(); }
        }
        public OLSAnalysisResult? OlsResult { get => _olsResult; set { SetProperty(ref _olsResult, value); RaisePropertyChanged(nameof(HasOlsResult)); } }
        public string OlsStatusText { get => _olsStatusText; set => SetProperty(ref _olsStatusText, value); }
        public bool HasOlsResult => OlsResult?.ModelSummary != null;
        public DesirabilityResult? DesirabilityResult { get => _desirabilityResult; set => SetProperty(ref _desirabilityResult, value); }

        // ══════════════ Commands ══════════════
        public DelegateCommand RefreshAllCommand { get; }
        public DelegateCommand RetrainCommand { get; }
        public DelegateCommand FindOptimalCommand { get; }
        public DelegateCommand DeleteModelCommand { get; }
        public DelegateCommand GenerateSurfaceCommand { get; }
        public DelegateCommand DownloadTemplateCommand { get; }
        public DelegateCommand ImportDataCommand { get; }
        public DelegateCommand PrevPageCommand { get; }
        public DelegateCommand NextPageCommand { get; }
        public DelegateCommand SwitchToGprTabCommand { get; }
        public DelegateCommand SwitchToOlsTabCommand { get; }
        public DelegateCommand<OlsBatchItem> SelectOlsBatchCommand { get; }
        public DelegateCommand RunDesirabilityCommand { get; }

        // ══════════════ Tab 切换 ══════════════
        private void SwitchTab(bool toGpr)
        {
            IsGprTabSelected = toGpr;
            if (!toGpr) _ = LoadOlsBatchListAsync();
        }

        // ══════════════ 公开加载方法 ══════════════
        public async Task LoadBatchAsync(string batchId)
        {
            _currentBatchId = batchId;
            var batch = await _repository.GetBatchWithDetailsAsync(batchId);
            if (batch == null) return;
            _currentFlowId = batch.FlowId;
            FactorNames = batch.Factors.Select(f => f.FactorName).ToList();
            ResponseNames = batch.Responses.Select(r => r.ResponseName).ToList();
            _selectedResponseName = ResponseNames.FirstOrDefault() ?? "";
            RaisePropertyChanged(nameof(SelectedResponseName));
            if (FactorNames.Count >= 2) { SurfaceFactor1 = FactorNames[0]; SurfaceFactor2 = FactorNames[1]; }
            await LoadModelsAsync();
            await RefreshAllAsync();
        }

        public async Task LoadAsync() { await LoadModelsAsync(); }

        // ══════════════ OLS 批次列表 ══════════════
        private async Task LoadOlsBatchListAsync()
        {
            try
            {
                IsLoading = true;
                OlsStatusText = "正在加载批次列表...";
                string flowId = !string.IsNullOrEmpty(_currentFlowId) ? _currentFlowId : _paramProvider.GetCurrentFlowId() ?? "";
                var allBatches = await _repository.GetBatchesByFlowAsync(flowId);
                var items = new List<OlsBatchItem>();
                foreach (var b in allBatches)
                {
                    var detail = await _repository.GetBatchWithDetailsAsync(b.BatchId);
                    if (detail == null) continue;
                    var completedCount = detail.Runs.Count(r => r.Status == DOERunStatus.Completed);
                    if (completedCount == 0) continue;
                    items.Add(new OlsBatchItem
                    {
                        BatchId = detail.BatchId,
                        BatchName = detail.BatchName,
                        DesignMethod = detail.DesignMethod.ToString(),
                        CompletedCount = completedCount,
                        FactorCount = detail.Factors.Count
                    });
                }
                OlsBatchItems = new ObservableCollection<OlsBatchItem>(items);
                OlsStatusText = items.Count == 0
                    ? "没有已完成实验的批次。请先执行实验后再进行 OLS 分析。"
                    : $"找到 {items.Count} 个可分析的批次，请选择一个进行 OLS 回归分析。";
            }
            catch (Exception ex) { _logger.LogError(ex, "加载 OLS 批次列表失败"); OlsStatusText = $"加载失败: {ex.Message}"; }
            finally { IsLoading = false; }
        }

        // ══════════════ OLS 选择批次 ══════════════
        private async Task SelectOlsBatchAsync(OlsBatchItem? item)
        {
            if (item == null) return;
            foreach (var b in OlsBatchItems) b.IsSelected = (b.BatchId == item.BatchId);
            SelectedOlsBatch = item;
            var batch = await _repository.GetBatchWithDetailsAsync(item.BatchId);
            if (batch?.Responses?.Count > 0)
            {
                ResponseNames = batch.Responses.Select(r => r.ResponseName).ToList();
                _selectedResponseName = ResponseNames.FirstOrDefault() ?? "";
                RaisePropertyChanged(nameof(SelectedResponseName));
            }
            await RefreshOlsAnalysisAsync();
        }

        // ══════════════ OLS 回归分析 ══════════════
        private async Task RefreshOlsAnalysisAsync()
        {
            if (SelectedOlsBatch == null || string.IsNullOrEmpty(_selectedResponseName)) return;
            try
            {
                IsLoading = true;
                OlsStatusText = "正在进行 OLS 回归分析...";
                OlsResult = await _analysisService.FitOlsAsync(SelectedOlsBatch.BatchId, _selectedResponseName, "quadratic");
                if (OlsResult?.ModelSummary != null)
                {
                    OlsStatusText = $"OLS 分析完成: R²={OlsResult.ModelSummary.RSquared:F4}, R²adj={OlsResult.ModelSummary.RSquaredAdj:F4}, R²pred={OlsResult.ModelSummary.RSquaredPred:F4}";
                    _logger.LogInformation("OLS: Batch={B}, Resp={R}, R²={R2}", SelectedOlsBatch.BatchId, _selectedResponseName, OlsResult.ModelSummary.RSquared);
                }
                else OlsStatusText = "OLS 分析返回空结果";
            }
            catch (Exception ex) { _logger.LogError(ex, "OLS 分析失败"); OlsStatusText = $"OLS 分析失败: {ex.Message}"; OlsResult = null; }
            finally { IsLoading = false; }
        }

        // ══════════════ Desirability ══════════════
        private async Task RunDesirabilityOptimizationAsync()
        {
            var batchId = SelectedOlsBatch?.BatchId ?? _currentBatchId;
            if (string.IsNullOrEmpty(batchId)) { _dialogService.ShowError("请先选择一个批次", "提示"); return; }
            if (ResponseNames.Count < 2) { _dialogService.ShowError("Desirability 优化需要至少 2 个响应变量", "提示"); return; }
            try
            {
                IsLoading = true; StatusMessage = "正在搜索多响应最优因子组合...";
                var configs = await _desirabilityService.LoadConfigAsync(batchId);
                if (configs.Count == 0) { _dialogService.ShowError("请先在设计向导 Step4 中配置各响应变量的优化目标", "缺少配置"); return; }
                var batch = await _repository.GetBatchWithDetailsAsync(batchId);
                if (batch == null) return;
                await _desirabilityService.ConfigureAsync(batchId, configs, batch.Factors);
                DesirabilityResult = await _desirabilityService.OptimizeAsync();
                StatusMessage = DesirabilityResult?.Success == true ? $"多响应优化完成: 综合 D = {DesirabilityResult.CompositeD:F4}" : $"优化失败: {DesirabilityResult?.ErrorMessage ?? "未知错误"}";
            }
            catch (Exception ex) { _logger.LogError(ex, "Desirability 优化失败"); _dialogService.ShowError($"优化失败: {ex.Message}", "错误"); }
            finally { IsLoading = false; }
        }

        // ══════════════ GPR 模型列表 ══════════════
        private async Task LoadModelsAsync()
        {
            try
            {
                IsLoading = true; StatusMessage = "正在加载模型...";
                var states = await _repository.GetAllGPRModelsAsync();
                var tabs = states.Select(s => new GPRModelTabItem { ModelId = s.Id, ModelName = string.IsNullOrEmpty(s.ModelName) ? s.FactorSignature : s.ModelName, FactorSignature = s.FactorSignature, IsActive = s.IsActive, IsColdStart = s.DataCount > 0 && !s.IsActive, DataCount = s.DataCount, RSquared = s.RSquared }).ToList();
                Models = new ObservableCollection<GPRModelTabItem>(tabs);
                if (tabs.Count > 0) SelectModel(tabs[0].ModelId);
                StatusMessage = $"已加载 {tabs.Count} 个模型";
            }
            catch (Exception ex) { _logger.LogError(ex, "加载 GPR 模型列表失败"); StatusMessage = "加载失败"; }
            finally { IsLoading = false; }
        }

        public void SelectModel(int modelId)
        {
            foreach (var m in Models) m.IsSelected = (m.ModelId == modelId);
            _ = LoadModelDetailAsync(modelId);
        }

        private async Task LoadModelDetailAsync(int modelId)
        {
            try
            {
                var state = await _repository.GetGPRModelByIdAsync(modelId);
                if (state == null) return;
                _currentFlowId = state.FlowId;
                SelectedModel = new GPRModelDetail { ModelId = state.Id, ModelName = string.IsNullOrEmpty(state.ModelName) ? state.FactorSignature : state.ModelName, FactorSignature = state.FactorSignature, IsActive = state.IsActive, DataCount = state.DataCount, RSquared = state.RSquared, RMSE = state.RMSE, LastTrainedTime = state.LastTrainedTime, EvolutionHistoryJson = state.EvolutionHistoryJson };
                if (!string.IsNullOrEmpty(state.FactorSignature))
                {
                    FactorNames = state.FactorSignature.Split(',').ToList();
                    if (!string.IsNullOrEmpty(_currentBatchId))
                    {
                        var batch = await _repository.GetBatchWithDetailsAsync(_currentBatchId);
                        if (batch?.Responses?.Count > 0) { ResponseNames = batch.Responses.Select(r => r.ResponseName).ToList(); if (string.IsNullOrEmpty(_selectedResponseName)) { _selectedResponseName = ResponseNames.FirstOrDefault() ?? ""; RaisePropertyChanged(nameof(SelectedResponseName)); } }
                    }
                    else
                    {
                        var batches = await _repository.GetBatchesByFlowAsync(state.FlowId);
                        var latestBatch = batches.FirstOrDefault();
                        if (latestBatch != null) { _currentBatchId = latestBatch.BatchId; var batchDetail = await _repository.GetBatchWithDetailsAsync(_currentBatchId); if (batchDetail?.Responses?.Count > 0) { ResponseNames = batchDetail.Responses.Select(r => r.ResponseName).ToList(); if (string.IsNullOrEmpty(_selectedResponseName)) { _selectedResponseName = ResponseNames.FirstOrDefault() ?? ""; RaisePropertyChanged(nameof(SelectedResponseName)); } } }
                    }
                    if (FactorNames.Count >= 2) { SurfaceFactor1 = FactorNames[0]; SurfaceFactor2 = FactorNames[1]; }
                }
                BuildTrainingDataTable(state.TrainingDataJson);
                BuildEvolutionPlot(state.EvolutionHistoryJson);
                if (state.IsActive) { await LoadGPRAnalysisChartsAsync(state); await FindOptimalAsync(); }
                else { ClearAnalysisCharts(); HasOptimalResult = false; }
            }
            catch (Exception ex) { _logger.LogError(ex, "加载模型详情失败: {ModelId}", modelId); }
        }

        // ══════════════ 训练数据集分页 ══════════════
        private void BuildTrainingDataTable(string? trainingDataJson)
        {
            var dt = new DataTable();
            if (string.IsNullOrEmpty(trainingDataJson)) { _fullTrainingData = dt; TotalDataCount = 0; TotalPages = 1; CurrentPage = 1; PagedTrainingData = dt; return; }
            try
            {
                var records = JsonConvert.DeserializeObject<List<TrainingDataRecord>>(trainingDataJson);
                if (records == null || records.Count == 0) { _fullTrainingData = dt; TotalDataCount = 0; TotalPages = 1; CurrentPage = 1; PagedTrainingData = dt; return; }
                dt.Columns.Add("#", typeof(int));
                var factorNames = records[0].Factors?.Keys.ToList() ?? new List<string>();
                foreach (var name in factorNames) dt.Columns.Add(name, typeof(string));
                dt.Columns.Add("响应值", typeof(string)); dt.Columns.Add("来源", typeof(string));
                dt.Columns.Add("批次名称", typeof(string)); dt.Columns.Add("时间", typeof(string));
                int idx = 1;
                foreach (var rec in records)
                {
                    var row = dt.NewRow(); row["#"] = idx++;
                    if (rec.Factors != null) foreach (var name in factorNames) row[name] = rec.Factors.TryGetValue(name, out var v) ? v.ToString("F2") : "—";
                    row["响应值"] = rec.Response.ToString("F2");
                    row["来源"] = rec.Source switch { "measured" => "实验采集", "imported" => "手动导入", _ => rec.Source ?? "未知" };
                    row["批次名称"] = string.IsNullOrEmpty(rec.BatchName) ? "—" : rec.BatchName;
                    row["时间"] = string.IsNullOrEmpty(rec.Timestamp) ? "—" : rec.Timestamp;
                    dt.Rows.Add(row);
                }
                _fullTrainingData = dt; TotalDataCount = dt.Rows.Count;
                TotalPages = Math.Max(1, (int)Math.Ceiling((double)TotalDataCount / PAGE_SIZE));
                CurrentPage = 1; ApplyPage();
            }
            catch (Exception ex) { _logger.LogError(ex, "构建训练数据表格失败"); _fullTrainingData = new DataTable(); TotalDataCount = 0; TotalPages = 1; CurrentPage = 1; PagedTrainingData = new DataTable(); }
        }
        private void GoToPage(int page) { if (page < 1 || page > TotalPages) return; CurrentPage = page; ApplyPage(); }
        private void ApplyPage()
        {
            if (_fullTrainingData.Rows.Count == 0) { PagedTrainingData = _fullTrainingData; return; }
            var paged = _fullTrainingData.Clone();
            int start = (CurrentPage - 1) * PAGE_SIZE; int end = Math.Min(start + PAGE_SIZE, _fullTrainingData.Rows.Count);
            for (int i = start; i < end; i++) paged.ImportRow(_fullTrainingData.Rows[i]);
            PagedTrainingData = paged;
        }

        // ══════════════ GPR 分析图表 ══════════════
        private async Task RefreshAllAsync() { if (SelectedModel.ModelId <= 0) return; await LoadModelDetailAsync(SelectedModel.ModelId); }
        private async Task LoadGPRAnalysisChartsAsync(GPRModelState state)
        {
            try { IsLoading = true; StatusMessage = "正在加载 GPR 分析图表..."; await EnsureGPRServiceLoadedAsync(state); BuildSensitivityPlot(); await BuildActualVsPredictedPlotAsync(state); await BuildResidualPlotAsync(state); await LoadSurfaceAsync(); StatusMessage = "图表加载完成"; }
            catch (Exception ex) { _logger.LogError(ex, "加载 GPR 分析图表失败"); StatusMessage = $"加载失败: {ex.Message}"; }
            finally { IsLoading = false; }
        }
        private async Task EnsureGPRServiceLoadedAsync(GPRModelState state) { _currentFlowId = state.FlowId; await _gprService.LoadStateAsync(_currentFlowId); }
        private async Task LoadSurfaceAsync()
        {
            if (string.IsNullOrEmpty(SurfaceFactor1) || string.IsNullOrEmpty(SurfaceFactor2) || SurfaceFactor1 == SurfaceFactor2) return;
            try
            {
                byte[] imageBytes = Array.Empty<byte>();
                if (_gprService.IsActive)
                {
                    imageBytes = await _analysisService.GetResponseSurfaceImageFromGPRAsync(SurfaceFactor1, SurfaceFactor2);
                    if (imageBytes.Length > 0)
                    {
                        var bmp = new BitmapImage();
                        using (var ms = new MemoryStream(imageBytes)) { bmp.BeginInit(); bmp.CacheOption = BitmapCacheOption.OnLoad; bmp.StreamSource = ms; bmp.EndInit(); bmp.Freeze(); }
                        System.Windows.Application.Current.Dispatcher.Invoke(() => { SurfaceImage = bmp; });
                    }
                }
                if (imageBytes.Length == 0 && !string.IsNullOrEmpty(_currentBatchId))
                { try { imageBytes = await _analysisService.GetResponseSurfaceImageAsync(_currentBatchId, SurfaceFactor1, SurfaceFactor2); } catch { } }
            }
            catch (Exception ex) { _logger.LogError(ex, "响应曲面加载失败"); }
        }
        private void BuildSensitivityPlot()
        {
            var model = new PlotModel();
            try { var sensitivity = _gprService.GetSensitivity(); if (sensitivity.Count > 0) { var sorted = sensitivity.OrderByDescending(kv => kv.Value).ToList(); var catAxis = new CategoryAxis { Position = AxisPosition.Left }; model.Axes.Add(catAxis); model.Axes.Add(new LinearAxis { Position = AxisPosition.Bottom, Minimum = 0, Title = "敏感性" }); var barSeries = new BarSeries { FillColor = OxyColor.FromRgb(0x00, 0x78, 0xD4) }; foreach (var kv in sorted) { catAxis.Labels.Add(kv.Key); barSeries.Items.Add(new BarItem(kv.Value)); } model.Series.Add(barSeries); } }
            catch (Exception ex) { _logger.LogError(ex, "构建敏感性图失败"); }
            SensitivityPlot = model;
        }
        private async Task BuildActualVsPredictedPlotAsync(GPRModelState state)
        {
            var model = new PlotModel();
            try { var td = ParseTrainingData(state.TrainingDataJson); if (td.Count == 0) { ActualVsPredictedPlot = model; return; } var actuals = td.Select(d => d.Response).ToList(); var preds = _gprService.PredictBatch(td.Select(d => d.Factors).ToList()); var predictions = preds.Select(p => p.Mean).ToList(); double ssRes = 0, ssTot = 0, mean = actuals.Average(); for (int i = 0; i < actuals.Count; i++) { ssRes += Math.Pow(actuals[i] - predictions[i], 2); ssTot += Math.Pow(actuals[i] - mean, 2); } double r2 = ssTot > 1e-12 ? 1.0 - ssRes / ssTot : 0.0; model.Title = $"实际值 vs GPR 预测值 (R² = {r2:F4})"; model.Axes.Add(new LinearAxis { Position = AxisPosition.Bottom, Title = "实际值" }); model.Axes.Add(new LinearAxis { Position = AxisPosition.Left, Title = "GPR 预测值" }); var scatter = new ScatterSeries { MarkerType = MarkerType.Circle, MarkerSize = 5, MarkerFill = OxyColors.SteelBlue }; for (int i = 0; i < actuals.Count; i++) scatter.Points.Add(new ScatterPoint(actuals[i], predictions[i])); model.Series.Add(scatter); double min = Math.Min(actuals.Min(), predictions.Min()); double max = Math.Max(actuals.Max(), predictions.Max()); var diag = new LineSeries { Color = OxyColors.Red, LineStyle = LineStyle.Dash }; diag.Points.Add(new DataPoint(min, min)); diag.Points.Add(new DataPoint(max, max)); model.Series.Add(diag); }
            catch (Exception ex) { _logger.LogError(ex, "构建实际vs预测图失败"); }
            ActualVsPredictedPlot = model;
        }
        private async Task BuildResidualPlotAsync(GPRModelState state)
        {
            var model = new PlotModel { Title = "残差 vs GPR 拟合值" };
            try { var td = ParseTrainingData(state.TrainingDataJson); if (td.Count == 0) { ResidualPlot = model; return; } var actuals = td.Select(d => d.Response).ToList(); var preds = _gprService.PredictBatch(td.Select(d => d.Factors).ToList()); var fittedValues = preds.Select(p => p.Mean).ToList(); model.Axes.Add(new LinearAxis { Position = AxisPosition.Bottom, Title = "GPR 拟合值" }); model.Axes.Add(new LinearAxis { Position = AxisPosition.Left, Title = "残差" }); var scatter = new ScatterSeries { MarkerType = MarkerType.Circle, MarkerSize = 4, MarkerFill = OxyColors.DarkOrange }; for (int i = 0; i < actuals.Count; i++) scatter.Points.Add(new ScatterPoint(fittedValues[i], actuals[i] - fittedValues[i])); model.Series.Add(scatter); double fMin = fittedValues.Min(), fMax = fittedValues.Max(); var zero = new LineSeries { Color = OxyColors.Gray, LineStyle = LineStyle.Dash }; zero.Points.Add(new DataPoint(fMin, 0)); zero.Points.Add(new DataPoint(fMax, 0)); model.Series.Add(zero); }
            catch (Exception ex) { _logger.LogError(ex, "构建残差图失败"); }
            ResidualPlot = model;
        }
        private void ClearAnalysisCharts() { SensitivityPlot = new PlotModel(); ActualVsPredictedPlot = new PlotModel(); ResidualPlot = new PlotModel(); SurfaceImage = null; StatusMessage = "模型未激活，需要至少 6 组数据"; }

        // ══════════════ GPR 操作 ══════════════
        private async Task RetrainAsync() { try { IsLoading = true; StatusMessage = "正在重新训练..."; await _gprService.ResetModelAsync(_currentFlowId, keepData: true); await _gprService.TrainModelAsync(); await _gprService.SaveStateAsync(_currentFlowId); await LoadModelsAsync(); } catch (Exception ex) { _logger.LogError(ex, "重新训练失败"); StatusMessage = $"训练失败: {ex.Message}"; } finally { IsLoading = false; } }
        private async Task FindOptimalAsync()
        {
            try { if (!_gprService.IsActive) { HasOptimalResult = false; return; } var optimal = _gprService.FindOptimal(); if (optimal?.OptimalFactors != null && optimal.OptimalFactors.Count > 0) { var maxVal = optimal.OptimalFactors.Values.DefaultIfEmpty(1).Max(); OptimalFactors = optimal.OptimalFactors.Select(kv => new OptimalFactorItem { Key = kv.Key, Value = kv.Value, BarWidth = maxVal > 0 ? kv.Value / maxVal * 180 : 0 }).ToList(); OptimalResponseText = $"{optimal.PredictedResponse:F2}"; OptimalConfidenceText = $"± {optimal.PredictionStd:F2}"; HasOptimalResult = true; } else { HasOptimalResult = false; } }
            catch (Exception ex) { _logger.LogError(ex, "搜索最优失败"); HasOptimalResult = false; }
        }
        private async Task DeleteModelAsync() { try { if (SelectedModel.ModelId <= 0) return; await _repository.DeleteGPRModelAsync(SelectedModel.ModelId); await LoadModelsAsync(); } catch (Exception ex) { _logger.LogError(ex, "删除模型失败"); } }

        // ══════════════ 数据导入 ══════════════
        private async Task DownloadTemplateAsync()
        {
            try { if (FactorNames.Count == 0) { _dialogService.ShowError("无法生成模板：未找到因子信息。", "提示"); return; } var responseNames = _responseNames.Count > 0 ? _responseNames : new List<string> { "Response" }; var modelName = System.Text.RegularExpressions.Regex.Replace(SelectedModel.ModelName ?? "模型", @"[^\w\u4e00-\u9fff\-]", "_"); var defaultFileName = $"GPR数据模板_{modelName}_{DateTime.Now:yyyyMMdd}.xlsx"; var path = _dialogService.ShowSaveFileDialog("Excel 文件 (*.xlsx)|*.xlsx", ".xlsx", defaultFileName); if (string.IsNullOrEmpty(path)) return; await _exportService.GenerateGPRTemplateAsync(FactorNames, responseNames, path); _dialogService.ShowInfo($"数据模板已保存至:\n{path}", "模板已生成"); }
            catch (Exception ex) { _logger.LogError(ex, "生成数据模板失败"); _dialogService.ShowError($"生成模板失败: {ex.Message}", "错误"); }
        }
        private async Task ImportDataFromExcelAsync()
        {
            try { if (FactorNames.Count == 0) { _dialogService.ShowError("无法导入：未找到因子信息。", "提示"); return; } var path = _dialogService.ShowOpenFileDialog("Excel 文件 (*.xlsx)|*.xlsx"); if (string.IsNullOrEmpty(path)) return; IsLoading = true; StatusMessage = "正在读取 Excel 文件..."; var responseNames = _responseNames.Count > 0 ? _responseNames : new List<string> { "Response" }; var primaryResponse = responseNames[0]; var importedData = await Task.Run(() => ReadImportExcel(path, FactorNames, responseNames)); if (importedData.Errors.Count > 0) { _dialogService.ShowError($"校验失败:\n{string.Join("\n", importedData.Errors.Take(5))}", "导入失败"); StatusMessage = "导入失败"; return; } if (importedData.Rows.Count == 0) { _dialogService.ShowError("无有效数据。", "导入失败"); StatusMessage = "导入失败"; return; } var confirm = _dialogService.ShowConfirmation($"共 {importedData.Rows.Count} 行有效数据，确定导入？", "确认导入"); if (!confirm) { StatusMessage = "已取消"; return; } await EnsureGPRServiceReadyAsync(); int fedCount = 0; foreach (var row in importedData.Rows) if (row.Responses.TryGetValue(primaryResponse, out var respVal)) { _gprService.AppendData(row.Factors, respVal, "imported", batchName: ""); fedCount++; } await _gprService.SaveInitialStateAsync(_currentFlowId); ImportStatusText = $"已导入 {fedCount} 条"; StatusMessage = $"导入完成: {fedCount} 条"; await LoadModelsAsync(); }
            catch (Exception ex) { _logger.LogError(ex, "导入数据失败"); _dialogService.ShowError($"导入失败: {ex.Message}", "错误"); }
            finally { IsLoading = false; }
        }
        private async Task EnsureGPRServiceReadyAsync()
        {
            if (!string.IsNullOrEmpty(_currentBatchId)) { var batch = await _repository.GetBatchWithDetailsAsync(_currentBatchId); if (batch?.Factors?.Count > 0) { await _gprService.InitializeModelAsync(_currentFlowId, batch.Factors); return; } }
            await _gprService.LoadStateAsync(_currentFlowId);
        }
        private static ImportResult ReadImportExcel(string path, List<string> factorNames, List<string> responseNames)
        {
            var result = new ImportResult();
            try { using var package = new ExcelPackage(new FileInfo(path)); var ws = package.Workbook.Worksheets.FirstOrDefault(); if (ws == null) { result.Errors.Add("没有工作表"); return result; } var headerMap = new Dictionary<string, int>(); for (int col = 1; col <= ws.Dimension.End.Column; col++) { var header = ws.Cells[1, col].Text?.Trim(); if (!string.IsNullOrEmpty(header)) headerMap[header] = col; } var allRequired = factorNames.Concat(responseNames).ToList(); var missing = allRequired.Where(n => !headerMap.ContainsKey(n)).ToList(); if (missing.Count > 0) { result.Errors.Add($"缺少列: {string.Join(", ", missing)}"); return result; } for (int row = 2; row <= ws.Dimension.End.Row; row++) { var factors = new Dictionary<string, double>(); var responses = new Dictionary<string, double>(); bool valid = true; foreach (var fname in factorNames) { var cellVal = ws.Cells[row, headerMap[fname]].Text?.Trim(); if (!double.TryParse(cellVal, out var val)) { valid = false; break; } factors[fname] = val; } if (!valid) continue; foreach (var rname in responseNames) { var cellVal = ws.Cells[row, headerMap[rname]].Text?.Trim(); if (!double.TryParse(cellVal, out var val)) { valid = false; break; } responses[rname] = val; } if (valid) result.Rows.Add(new ImportRow { Factors = factors, Responses = responses }); } }
            catch (Exception ex) { result.Errors.Add($"读取失败: {ex.Message}"); }
            return result;
        }
        private class ImportResult { public List<ImportRow> Rows { get; set; } = new(); public List<string> Errors { get; set; } = new(); }
        private class ImportRow { public Dictionary<string, double> Factors { get; set; } = new(); public Dictionary<string, double> Responses { get; set; } = new(); }

        // ══════════════ 图表辅助 ══════════════
        private void BuildEvolutionPlot(string? json)
        {
            var model = new PlotModel(); model.Axes.Add(new LinearAxis { Position = AxisPosition.Bottom, Title = "数据量", Minimum = 0 }); model.Axes.Add(new LinearAxis { Position = AxisPosition.Left, Title = "R²", Minimum = 0, Maximum = 1.05 });
            if (!string.IsNullOrEmpty(json)) { try { var history = JsonConvert.DeserializeObject<List<EvolutionPoint>>(json); if (history?.Count > 0) { var series = new LineSeries { Color = OxyColor.FromRgb(0x00, 0x78, 0xD4), StrokeThickness = 1.8, MarkerType = MarkerType.Circle, MarkerSize = 2.5, MarkerFill = OxyColor.FromRgb(0x00, 0x78, 0xD4) }; foreach (var pt in history) series.Points.Add(new DataPoint(pt.DataCount, pt.RSquared)); model.Series.Add(series); } } catch { } }
            EvolutionPlot = model;
        }
        private List<TrainingDataPoint> ParseTrainingData(string? json)
        {
            if (string.IsNullOrEmpty(json)) return new();
            try { var records = JsonConvert.DeserializeObject<List<TrainingDataRecord>>(json); return records?.Where(r => r.Factors != null).Select(r => new TrainingDataPoint { Factors = r.Factors!, Response = r.Response }).ToList() ?? new(); }
            catch { return new(); }
        }
        private class EvolutionPoint { [JsonProperty("data_count")] public int DataCount { get; set; } [JsonProperty("r_squared")] public double RSquared { get; set; } }
        private class TrainingDataRecord { [JsonProperty("factors")] public Dictionary<string, double>? Factors { get; set; } [JsonProperty("response")] public double Response { get; set; } [JsonProperty("source")] public string? Source { get; set; } [JsonProperty("batch_name")] public string? BatchName { get; set; } [JsonProperty("timestamp")] public string? Timestamp { get; set; } }
        private class TrainingDataPoint { public Dictionary<string, double> Factors { get; set; } = new(); public double Response { get; set; } }
    }
}
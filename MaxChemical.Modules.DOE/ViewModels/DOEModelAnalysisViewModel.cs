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
    // ════════════════════════════════════════════════════════════════
    // 子 DTO
    // ════════════════════════════════════════════════════════════════

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
        public SolidColorBrush StatusColor => IsActive
            ? new SolidColorBrush(Color.FromRgb(0x2E, 0x7D, 0x32))
            : new SolidColorBrush(Color.FromRgb(0xE6, 0x51, 0x00));
        public SolidColorBrush StatusDotColor => IsActive
            ? new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50))
            : new SolidColorBrush(Color.FromRgb(0xFF, 0xA0, 0x00));
        public string StatusHint => IsActive ? "≥6 组达标" : $"需 {6 - DataCount} 组";
        public string DataCountText => DataCount.ToString();
        public string RSquaredText => IsActive ? $"{RSquared:F4}" : "—";
        public string RMSEText => IsActive ? $"{RMSE:F2}" : "—";
        public string LastTrainedText => LastTrainedTime?.ToString("yyyy-MM-dd HH:mm:ss") ?? "—";
        public string StatusSummary => IsActive
            ? $"已激活 · {DataCount}组 · 上次训练 {LastTrainedTime:HH:mm}"
            : $"冷启动 {DataCount}/6";
    }

    public class OptimalFactorItem
    {
        public string Key { get; set; } = "";
        public double Value { get; set; }
        public double BarWidth { get; set; }
    }

    // ════════════════════════════════════════════════════════════════
    // ViewModel
    // ════════════════════════════════════════════════════════════════

    public class DOEModelAnalysisViewModel : BindableBase
    {
        private readonly IGPRModelService _gprService;
        private readonly IDOEAnalysisService _analysisService;
        private readonly IDOERepository _repository;
        private readonly IFlowParameterProvider _paramProvider;
        private readonly IDialogService _dialogService;
        private readonly DOEExportService _exportService;
        private readonly ILogService _logger;

        // ── 模型管理 ──
        private ObservableCollection<GPRModelTabItem> _models = new();
        private GPRModelDetail _selectedModel = new();
        private bool _isLoading;
        private string _statusMessage = "";

        // ── 指标 & 最优 ──
        private List<OptimalFactorItem> _optimalFactors = new();
        private string _optimalResponseText = "—";
        private string _optimalConfidenceText = "";
        private bool _hasOptimalResult;

        // ── 图表 ──
        private PlotModel? _evolutionPlot;
        private PlotModel? _sensitivityPlot;
        private PlotModel? _actualVsPredictedPlot;
        private PlotModel? _residualPlot;
        private BitmapImage? _surfaceImage;
        private PlotModel? _surfacePlot;

        // ── 曲面图因子选择 ──
        private List<string> _factorNames = new();
        private List<string> _responseNames = new();
        private string? _surfaceFactor1;
        private string? _surfaceFactor2;

        // ── 导入 ──
        private string _importStatusText = "";

        // ── 训练数据集（全量 + 分页）──
        private DataTable _fullTrainingData = new();
        private DataTable _pagedTrainingData = new();
        private int _currentPage = 1;
        private int _totalPages = 1;
        private int _totalDataCount;
        private const int PAGE_SIZE = 10;

        // ── 当前关联 ──
        private string _currentFlowId = "";
        private string _currentBatchId = "";

        public DOEModelAnalysisViewModel(
            IGPRModelService gprService,
            IDOEAnalysisService analysisService,
            IDOERepository repository,
            IFlowParameterProvider paramProvider,
            IDialogService dialogService,
            DOEExportService exportService,
            ILogService logger)
        {
            _gprService = gprService;
            _analysisService = analysisService;
            _repository = repository;
            _paramProvider = paramProvider;
            _dialogService = dialogService;
            _exportService = exportService;
            _logger = logger?.ForContext<DOEModelAnalysisViewModel>() ?? throw new ArgumentNullException(nameof(logger));

            RefreshAllCommand = new DelegateCommand(async () => await RefreshAllAsync());
            RetrainCommand = new DelegateCommand(async () => await RetrainAsync());
            FindOptimalCommand = new DelegateCommand(async () => await FindOptimalAsync());
            DeleteModelCommand = new DelegateCommand(async () => await DeleteModelAsync());
            GenerateSurfaceCommand = new DelegateCommand(async () => await LoadSurfaceAsync(),
                () => !string.IsNullOrEmpty(SurfaceFactor1) && !string.IsNullOrEmpty(SurfaceFactor2) && SurfaceFactor1 != SurfaceFactor2);
            DownloadTemplateCommand = new DelegateCommand(async () => await DownloadTemplateAsync());
            ImportDataCommand = new DelegateCommand(async () => await ImportDataFromExcelAsync());

            // ★ 分页命令
            PrevPageCommand = new DelegateCommand(() => GoToPage(CurrentPage - 1), () => CurrentPage > 1);
            NextPageCommand = new DelegateCommand(() => GoToPage(CurrentPage + 1), () => CurrentPage < TotalPages);
        }

        // ══════════════ Properties ══════════════

        public ObservableCollection<GPRModelTabItem> Models { get => _models; set => SetProperty(ref _models, value); }
        public GPRModelDetail SelectedModel { get => _selectedModel; set => SetProperty(ref _selectedModel, value); }
        public bool IsLoading { get => _isLoading; set => SetProperty(ref _isLoading, value); }
        public string StatusMessage { get => _statusMessage; set => SetProperty(ref _statusMessage, value); }

        // 最优
        public List<OptimalFactorItem> OptimalFactors { get => _optimalFactors; set => SetProperty(ref _optimalFactors, value); }
        public string OptimalResponseText { get => _optimalResponseText; set => SetProperty(ref _optimalResponseText, value); }
        public string OptimalConfidenceText { get => _optimalConfidenceText; set => SetProperty(ref _optimalConfidenceText, value); }
        public bool HasOptimalResult { get => _hasOptimalResult; set => SetProperty(ref _hasOptimalResult, value); }

        // 图表
        public PlotModel? EvolutionPlot { get => _evolutionPlot; set => SetProperty(ref _evolutionPlot, value); }
        public PlotModel? SensitivityPlot { get => _sensitivityPlot; set => SetProperty(ref _sensitivityPlot, value); }
        public PlotModel? ActualVsPredictedPlot { get => _actualVsPredictedPlot; set => SetProperty(ref _actualVsPredictedPlot, value); }
        public PlotModel? ResidualPlot { get => _residualPlot; set => SetProperty(ref _residualPlot, value); }
        public BitmapImage? SurfaceImage { get => _surfaceImage; set => SetProperty(ref _surfaceImage, value); }
        public PlotModel? SurfacePlot { get => _surfacePlot; set => SetProperty(ref _surfacePlot, value); }

        // 曲面选择
        public List<string> FactorNames { get => _factorNames; set => SetProperty(ref _factorNames, value); }
        public string? SurfaceFactor1 { get => _surfaceFactor1; set { SetProperty(ref _surfaceFactor1, value); GenerateSurfaceCommand.RaiseCanExecuteChanged(); } }
        public string? SurfaceFactor2 { get => _surfaceFactor2; set { SetProperty(ref _surfaceFactor2, value); GenerateSurfaceCommand.RaiseCanExecuteChanged(); } }

        // ★ 分页
        public DataTable PagedTrainingData { get => _pagedTrainingData; set => SetProperty(ref _pagedTrainingData, value); }
        public int CurrentPage
        {
            get => _currentPage;
            set { if (SetProperty(ref _currentPage, value)) { PrevPageCommand.RaiseCanExecuteChanged(); NextPageCommand.RaiseCanExecuteChanged(); } }
        }
        public int TotalPages { get => _totalPages; set { if (SetProperty(ref _totalPages, value)) { PrevPageCommand.RaiseCanExecuteChanged(); NextPageCommand.RaiseCanExecuteChanged(); } } }
        public int TotalDataCount { get => _totalDataCount; set => SetProperty(ref _totalDataCount, value); }

        // 导入
        public string ImportStatusText { get => _importStatusText; set => SetProperty(ref _importStatusText, value); }

        // Commands
        public DelegateCommand RefreshAllCommand { get; }
        public DelegateCommand RetrainCommand { get; }
        public DelegateCommand FindOptimalCommand { get; }
        public DelegateCommand DeleteModelCommand { get; }
        public DelegateCommand GenerateSurfaceCommand { get; }
        public DelegateCommand DownloadTemplateCommand { get; }
        public DelegateCommand ImportDataCommand { get; }
        public DelegateCommand PrevPageCommand { get; }
        public DelegateCommand NextPageCommand { get; }

        // ══════════════ 公开加载方法 ══════════════

        public async Task LoadBatchAsync(string batchId)
        {
            _currentBatchId = batchId;
            var batch = await _repository.GetBatchWithDetailsAsync(batchId);
            if (batch == null) return;

            _currentFlowId = batch.FlowId;
            FactorNames = batch.Factors.Select(f => f.FactorName).ToList();
            _responseNames = batch.Responses.Select(r => r.ResponseName).ToList();
            if (FactorNames.Count >= 2)
            {
                SurfaceFactor1 = FactorNames[0];
                SurfaceFactor2 = FactorNames[1];
            }

            await LoadModelsAsync();
            await RefreshAllAsync();
        }

        public async Task LoadAsync()
        {
            await LoadModelsAsync();
        }

        // ══════════════ 模型列表加载 ══════════════

        private async Task LoadModelsAsync()
        {
            try
            {
                IsLoading = true;
                StatusMessage = "正在加载模型...";

                var states = await _repository.GetAllGPRModelsAsync();
                var tabs = states.Select(s => new GPRModelTabItem
                {
                    ModelId = s.Id,
                    ModelName = string.IsNullOrEmpty(s.ModelName) ? s.FactorSignature : s.ModelName,
                    FactorSignature = s.FactorSignature,
                    IsActive = s.IsActive,
                    IsColdStart = s.DataCount > 0 && !s.IsActive,
                    DataCount = s.DataCount,
                    RSquared = s.RSquared
                }).ToList();

                Models = new ObservableCollection<GPRModelTabItem>(tabs);
                if (tabs.Count > 0)
                    SelectModel(tabs[0].ModelId);

                StatusMessage = $"已加载 {tabs.Count} 个模型";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "加载 GPR 模型列表失败");
                StatusMessage = "加载失败";
            }
            finally
            {
                IsLoading = false;
            }
        }

        public void SelectModel(int modelId)
        {
            foreach (var m in Models)
                m.IsSelected = (m.ModelId == modelId);
            _ = LoadModelDetailAsync(modelId);
        }

        private async Task LoadModelDetailAsync(int modelId)
        {
            try
            {
                var state = await _repository.GetGPRModelByIdAsync(modelId);
                if (state == null) return;

                _currentFlowId = state.FlowId;

                SelectedModel = new GPRModelDetail
                {
                    ModelId = state.Id,
                    ModelName = string.IsNullOrEmpty(state.ModelName) ? state.FactorSignature : state.ModelName,
                    FactorSignature = state.FactorSignature,
                    IsActive = state.IsActive,
                    DataCount = state.DataCount,
                    RSquared = state.RSquared,
                    RMSE = state.RMSE,
                    LastTrainedTime = state.LastTrainedTime,
                    EvolutionHistoryJson = state.EvolutionHistoryJson
                };

                if (!string.IsNullOrEmpty(state.FactorSignature))
                {
                    FactorNames = state.FactorSignature.Split(',').ToList();
                    if (FactorNames.Count >= 2)
                    {
                        SurfaceFactor1 = FactorNames[0];
                        SurfaceFactor2 = FactorNames[1];
                    }
                }

                // 构建数据集表格（全量）+ 分页
                BuildTrainingDataTable(state.TrainingDataJson);

                // 演进图
                BuildEvolutionPlot(state.EvolutionHistoryJson);

                if (state.IsActive)
                {
                    await LoadGPRAnalysisChartsAsync(state);
                    // ★ 自动加载最优参数
                    await FindOptimalAsync();
                }
                else
                {
                    ClearAnalysisCharts();
                    HasOptimalResult = false;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "加载模型详情失败: {ModelId}", modelId);
            }
        }

        // ══════════════ 训练数据集（分页）══════════════

        private void BuildTrainingDataTable(string? trainingDataJson)
        {
            var dt = new DataTable();

            if (string.IsNullOrEmpty(trainingDataJson))
            {
                _fullTrainingData = dt;
                TotalDataCount = 0;
                TotalPages = 1;
                CurrentPage = 1;
                PagedTrainingData = dt;
                return;
            }

            try
            {
                var records = JsonConvert.DeserializeObject<List<TrainingDataRecord>>(trainingDataJson);
                if (records == null || records.Count == 0)
                {
                    _fullTrainingData = dt;
                    TotalDataCount = 0;
                    TotalPages = 1;
                    CurrentPage = 1;
                    PagedTrainingData = dt;
                    return;
                }

                dt.Columns.Add("#", typeof(int));
                var factorNames = records[0].Factors?.Keys.ToList() ?? new List<string>();
                foreach (var name in factorNames)
                    dt.Columns.Add(name, typeof(string));
                dt.Columns.Add("响应值", typeof(string));
                dt.Columns.Add("来源", typeof(string));
                dt.Columns.Add("批次名称", typeof(string));
                dt.Columns.Add("时间", typeof(string));

                int idx = 1;
                foreach (var rec in records)
                {
                    var row = dt.NewRow();
                    row["#"] = idx++;
                    if (rec.Factors != null)
                        foreach (var name in factorNames)
                            row[name] = rec.Factors.TryGetValue(name, out var v) ? v.ToString("F2") : "—";
                    row["响应值"] = rec.Response.ToString("F2");
                    row["来源"] = rec.Source switch
                    {
                        "measured" => "实验采集",
                        "imported" => "手动导入",
                        _ => rec.Source ?? "未知"
                    };
                    row["批次名称"] = string.IsNullOrEmpty(rec.BatchName) ? "—" : rec.BatchName;
                    row["时间"] = string.IsNullOrEmpty(rec.Timestamp) ? "—" : rec.Timestamp;
                    dt.Rows.Add(row);
                }

                _fullTrainingData = dt;
                TotalDataCount = dt.Rows.Count;
                TotalPages = Math.Max(1, (int)Math.Ceiling((double)TotalDataCount / PAGE_SIZE));
                CurrentPage = 1;
                ApplyPage();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "构建训练数据表格失败");
                _fullTrainingData = new DataTable();
                TotalDataCount = 0;
                TotalPages = 1;
                CurrentPage = 1;
                PagedTrainingData = new DataTable();
            }
        }

        private void GoToPage(int page)
        {
            if (page < 1 || page > TotalPages) return;
            CurrentPage = page;
            ApplyPage();
        }

        private void ApplyPage()
        {
            if (_fullTrainingData.Rows.Count == 0)
            {
                PagedTrainingData = _fullTrainingData;
                return;
            }

            var paged = _fullTrainingData.Clone();
            int start = (CurrentPage - 1) * PAGE_SIZE;
            int end = Math.Min(start + PAGE_SIZE, _fullTrainingData.Rows.Count);

            for (int i = start; i < end; i++)
                paged.ImportRow(_fullTrainingData.Rows[i]);

            PagedTrainingData = paged;
        }

        // ══════════════ GPR 分析图表 ══════════════

        private async Task RefreshAllAsync()
        {
            if (SelectedModel.ModelId <= 0) return;
            await LoadModelDetailAsync(SelectedModel.ModelId);
        }

        private async Task LoadGPRAnalysisChartsAsync(GPRModelState state)
        {
            try
            {
                IsLoading = true;
                StatusMessage = "正在加载 GPR 分析图表...";

                await EnsureGPRServiceLoadedAsync(state);

                BuildSensitivityPlot();
                await BuildActualVsPredictedPlotAsync(state);
                await BuildResidualPlotAsync(state);
                await LoadSurfaceAsync();

                StatusMessage = "图表加载完成";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "加载 GPR 分析图表失败");
                StatusMessage = $"加载失败: {ex.Message}";
            }
            finally
            {
                IsLoading = false;
            }
        }

        private async Task EnsureGPRServiceLoadedAsync(GPRModelState state)
        {
            _currentFlowId = state.FlowId;
            await _gprService.LoadStateAsync(_currentFlowId);
        }

        private async Task LoadSurfaceAsync()
        {
            if (string.IsNullOrEmpty(SurfaceFactor1) || string.IsNullOrEmpty(SurfaceFactor2) || SurfaceFactor1 == SurfaceFactor2)
                return;
            try
            {
                byte[] imageBytes;
                if (!string.IsNullOrEmpty(_currentBatchId))
                    imageBytes = await _analysisService.GetResponseSurfaceImageAsync(_currentBatchId, SurfaceFactor1, SurfaceFactor2);
                else
                    imageBytes = await _analysisService.GetResponseSurfaceImageFromGPRAsync(SurfaceFactor1, SurfaceFactor2);

                if (imageBytes.Length > 0)
                {
                    var bmp = new BitmapImage();
                    using (var ms = new MemoryStream(imageBytes))
                    {
                        bmp.BeginInit();
                        bmp.CacheOption = BitmapCacheOption.OnLoad;
                        bmp.StreamSource = ms;
                        bmp.EndInit();
                        bmp.Freeze();
                    }
                    SurfaceImage = bmp;
                }
            }
            catch (Exception ex) { _logger.LogError(ex, "响应曲面加载失败"); }
        }

        private void BuildSensitivityPlot()
        {
            var model = new PlotModel();
            try
            {
                var sensitivity = _gprService.GetSensitivity();
                if (sensitivity.Count > 0)
                {
                    var sorted = sensitivity.OrderByDescending(kv => kv.Value).ToList();
                    var catAxis = new CategoryAxis { Position = AxisPosition.Left };
                    model.Axes.Add(catAxis);
                    model.Axes.Add(new LinearAxis { Position = AxisPosition.Bottom, Minimum = 0, Title = "敏感性" });
                    var barSeries = new BarSeries { FillColor = OxyColor.FromRgb(0x00, 0x78, 0xD4) };
                    foreach (var kv in sorted)
                    {
                        catAxis.Labels.Add(kv.Key);
                        barSeries.Items.Add(new BarItem(kv.Value));
                    }
                    model.Series.Add(barSeries);
                }
            }
            catch (Exception ex) { _logger.LogError(ex, "构建敏感性图失败"); }
            SensitivityPlot = model;
        }

        private async Task BuildActualVsPredictedPlotAsync(GPRModelState state)
        {
            var model = new PlotModel();
            try
            {
                var trainingData = ParseTrainingData(state.TrainingDataJson);
                if (trainingData.Count == 0) { ActualVsPredictedPlot = model; return; }

                var actuals = trainingData.Select(d => d.Response).ToList();
                var factorBatch = trainingData.Select(d => d.Factors).ToList();
                var preds = _gprService.PredictBatch(factorBatch);
                var predictions = preds.Select(p => p.Mean).ToList();

                double ssRes = 0, ssTot = 0, mean = actuals.Average();
                for (int i = 0; i < actuals.Count; i++)
                {
                    ssRes += Math.Pow(actuals[i] - predictions[i], 2);
                    ssTot += Math.Pow(actuals[i] - mean, 2);
                }
                double r2 = ssTot > 1e-12 ? 1.0 - ssRes / ssTot : 0.0;

                model.Title = $"实际值 vs GPR 预测值 (R² = {r2:F4})";
                model.Axes.Add(new LinearAxis { Position = AxisPosition.Bottom, Title = "实际值" });
                model.Axes.Add(new LinearAxis { Position = AxisPosition.Left, Title = "GPR 预测值" });
                var scatter = new ScatterSeries { MarkerType = MarkerType.Circle, MarkerSize = 5, MarkerFill = OxyColors.SteelBlue };
                for (int i = 0; i < actuals.Count; i++)
                    scatter.Points.Add(new ScatterPoint(actuals[i], predictions[i]));
                model.Series.Add(scatter);

                double min = Math.Min(actuals.Min(), predictions.Min());
                double max = Math.Max(actuals.Max(), predictions.Max());
                var diag = new LineSeries { Color = OxyColors.Red, LineStyle = LineStyle.Dash };
                diag.Points.Add(new DataPoint(min, min));
                diag.Points.Add(new DataPoint(max, max));
                model.Series.Add(diag);
            }
            catch (Exception ex) { _logger.LogError(ex, "构建实际vs预测图失败"); }
            ActualVsPredictedPlot = model;
        }

        private async Task BuildResidualPlotAsync(GPRModelState state)
        {
            var model = new PlotModel { Title = "残差 vs GPR 拟合值" };
            try
            {
                var trainingData = ParseTrainingData(state.TrainingDataJson);
                if (trainingData.Count == 0) { ResidualPlot = model; return; }

                var actuals = trainingData.Select(d => d.Response).ToList();
                var factorBatch = trainingData.Select(d => d.Factors).ToList();
                var preds = _gprService.PredictBatch(factorBatch);
                var fittedValues = preds.Select(p => p.Mean).ToList();

                model.Axes.Add(new LinearAxis { Position = AxisPosition.Bottom, Title = "GPR 拟合值" });
                model.Axes.Add(new LinearAxis { Position = AxisPosition.Left, Title = "残差" });
                var scatter = new ScatterSeries { MarkerType = MarkerType.Circle, MarkerSize = 4, MarkerFill = OxyColors.DarkOrange };
                for (int i = 0; i < actuals.Count; i++)
                    scatter.Points.Add(new ScatterPoint(fittedValues[i], actuals[i] - fittedValues[i]));
                model.Series.Add(scatter);

                double fMin = fittedValues.Min(), fMax = fittedValues.Max();
                var zero = new LineSeries { Color = OxyColors.Gray, LineStyle = LineStyle.Dash };
                zero.Points.Add(new DataPoint(fMin, 0));
                zero.Points.Add(new DataPoint(fMax, 0));
                model.Series.Add(zero);
            }
            catch (Exception ex) { _logger.LogError(ex, "构建残差图失败"); }
            ResidualPlot = model;
        }

        private void ClearAnalysisCharts()
        {
            SensitivityPlot = new PlotModel();
            ActualVsPredictedPlot = new PlotModel();
            ResidualPlot = new PlotModel();
            SurfaceImage = null;
            StatusMessage = "模型未激活，需要至少 6 组数据";
        }

        // ══════════════ 模型管理操作 ══════════════

        private async Task RetrainAsync()
        {
            try
            {
                IsLoading = true;
                StatusMessage = "正在重新训练...";
                await _gprService.ResetModelAsync(_currentFlowId, keepData: true);
                await _gprService.TrainModelAsync();
                await _gprService.SaveStateAsync(_currentFlowId);
                await LoadModelsAsync();
            }
            catch (Exception ex) { _logger.LogError(ex, "重新训练失败"); StatusMessage = $"训练失败: {ex.Message}"; }
            finally { IsLoading = false; }
        }

        private async Task FindOptimalAsync()
        {
            try
            {
                if (!_gprService.IsActive) { HasOptimalResult = false; return; }

                var optimal = _gprService.FindOptimal();
                if (optimal?.OptimalFactors != null && optimal.OptimalFactors.Count > 0)
                {
                    var maxVal = optimal.OptimalFactors.Values.DefaultIfEmpty(1).Max();
                    OptimalFactors = optimal.OptimalFactors.Select(kv => new OptimalFactorItem
                    {
                        Key = kv.Key,
                        Value = kv.Value,
                        BarWidth = maxVal > 0 ? kv.Value / maxVal * 180 : 0
                    }).ToList();
                    OptimalResponseText = $"{optimal.PredictedResponse:F2}";
                    OptimalConfidenceText = $"± {optimal.PredictionStd:F2}";
                    HasOptimalResult = true;
                }
                else
                {
                    HasOptimalResult = false;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "搜索最优失败");
                HasOptimalResult = false;
            }
        }

        private async Task DeleteModelAsync()
        {
            try
            {
                if (SelectedModel.ModelId <= 0) return;
                await _repository.DeleteGPRModelAsync(SelectedModel.ModelId);
                await LoadModelsAsync();
            }
            catch (Exception ex) { _logger.LogError(ex, "删除模型失败"); }
        }

        // ══════════════ 数据导入 ══════════════

        private async Task DownloadTemplateAsync()
        {
            try
            {
                if (FactorNames.Count == 0) { _dialogService.ShowError("无法生成模板：未找到因子信息。", "提示"); return; }
                var responseNames = _responseNames.Count > 0 ? _responseNames : new List<string> { "Response" };
                var modelName = System.Text.RegularExpressions.Regex.Replace(SelectedModel.ModelName ?? "模型", @"[^\w\u4e00-\u9fff\-]", "_");
                var defaultFileName = $"GPR数据模板_{modelName}_{DateTime.Now:yyyyMMdd}.xlsx";
                var path = _dialogService.ShowSaveFileDialog("Excel 文件 (*.xlsx)|*.xlsx", ".xlsx", defaultFileName);
                if (string.IsNullOrEmpty(path)) return;
                await _exportService.GenerateGPRTemplateAsync(FactorNames, responseNames, path);
                _dialogService.ShowInfo($"数据模板已保存至:\n{path}", "模板已生成");
            }
            catch (Exception ex) { _logger.LogError(ex, "生成数据模板失败"); _dialogService.ShowError($"生成模板失败: {ex.Message}", "错误"); }
        }

        private async Task ImportDataFromExcelAsync()
        {
            try
            {
                if (FactorNames.Count == 0) { _dialogService.ShowError("无法导入：未找到因子信息。", "提示"); return; }
                var path = _dialogService.ShowOpenFileDialog("Excel 文件 (*.xlsx)|*.xlsx");
                if (string.IsNullOrEmpty(path)) return;

                IsLoading = true;
                StatusMessage = "正在读取 Excel 文件...";

                var responseNames = _responseNames.Count > 0 ? _responseNames : new List<string> { "Response" };
                var primaryResponse = responseNames[0];
                var importedData = await Task.Run(() => ReadImportExcel(path, FactorNames, responseNames));

                if (importedData.Errors.Count > 0)
                {
                    var errMsg = string.Join("\n", importedData.Errors.Take(5));
                    _dialogService.ShowError($"校验失败:\n{errMsg}", "导入失败");
                    StatusMessage = "导入失败"; return;
                }
                if (importedData.Rows.Count == 0) { _dialogService.ShowError("无有效数据。", "导入失败"); StatusMessage = "导入失败"; return; }

                var confirm = _dialogService.ShowConfirmation($"共 {importedData.Rows.Count} 行有效数据，确定导入？", "确认导入");
                if (!confirm) { StatusMessage = "已取消"; return; }

                await EnsureGPRServiceReadyAsync();
                int fedCount = 0;
                foreach (var row in importedData.Rows)
                    if (row.Responses.TryGetValue(primaryResponse, out var respVal)) { _gprService.AppendData(row.Factors, respVal, "imported",batchName:""); fedCount++; }
                await _gprService.SaveInitialStateAsync(_currentFlowId);

                ImportStatusText = $"已导入 {fedCount} 条";
                StatusMessage = $"导入完成: {fedCount} 条";
                await LoadModelsAsync();
            }
            catch (Exception ex) { _logger.LogError(ex, "导入数据失败"); _dialogService.ShowError($"导入失败: {ex.Message}", "错误"); }
            finally { IsLoading = false; }
        }

        private async Task EnsureGPRServiceReadyAsync()
        {
            if (!string.IsNullOrEmpty(_currentBatchId))
            {
                var batch = await _repository.GetBatchWithDetailsAsync(_currentBatchId);
                if (batch?.Factors?.Count > 0) { await _gprService.InitializeModelAsync(_currentFlowId, batch.Factors); return; }
            }
            await _gprService.LoadStateAsync(_currentFlowId);
        }

        private static ImportResult ReadImportExcel(string path, List<string> factorNames, List<string> responseNames)
        {
            var result = new ImportResult();
            try
            {
                using var package = new ExcelPackage(new FileInfo(path));
                var ws = package.Workbook.Worksheets.FirstOrDefault();
                if (ws == null) { result.Errors.Add("没有工作表"); return result; }

                var headerMap = new Dictionary<string, int>();
                for (int col = 1; col <= ws.Dimension.End.Column; col++)
                {
                    var header = ws.Cells[1, col].Text?.Trim();
                    if (!string.IsNullOrEmpty(header)) headerMap[header] = col;
                }

                var allRequired = factorNames.Concat(responseNames).ToList();
                var missing = allRequired.Where(n => !headerMap.ContainsKey(n)).ToList();
                if (missing.Count > 0) { result.Errors.Add($"缺少列: {string.Join(", ", missing)}"); return result; }

                for (int row = 2; row <= ws.Dimension.End.Row; row++)
                {
                    var factors = new Dictionary<string, double>();
                    var responses = new Dictionary<string, double>();
                    bool valid = true;

                    foreach (var fname in factorNames)
                    {
                        var cellVal = ws.Cells[row, headerMap[fname]].Text?.Trim();
                        if (!double.TryParse(cellVal, out var val)) { result.Errors.Add($"第{row}行 '{fname}': 无效"); valid = false; break; }
                        factors[fname] = val;
                    }
                    if (!valid) continue;

                    foreach (var rname in responseNames)
                    {
                        var cellVal = ws.Cells[row, headerMap[rname]].Text?.Trim();
                        if (!double.TryParse(cellVal, out var val)) { result.Errors.Add($"第{row}行 '{rname}': 无效"); valid = false; break; }
                        responses[rname] = val;
                    }
                    if (valid) result.Rows.Add(new ImportRow { Factors = factors, Responses = responses });
                }
            }
            catch (Exception ex) { result.Errors.Add($"读取失败: {ex.Message}"); }
            return result;
        }

        private class ImportResult { public List<ImportRow> Rows { get; set; } = new(); public List<string> Errors { get; set; } = new(); }
        private class ImportRow { public Dictionary<string, double> Factors { get; set; } = new(); public Dictionary<string, double> Responses { get; set; } = new(); }

        // ══════════════ 图表辅助 ══════════════

        private void BuildEvolutionPlot(string? json)
        {
            var model = new PlotModel();
            model.Axes.Add(new LinearAxis { Position = AxisPosition.Bottom, Title = "数据量", Minimum = 0 });
            model.Axes.Add(new LinearAxis { Position = AxisPosition.Left, Title = "R²", Minimum = 0, Maximum = 1.05 });
            if (!string.IsNullOrEmpty(json))
            {
                try
                {
                    var history = JsonConvert.DeserializeObject<List<EvolutionPoint>>(json);
                    if (history?.Count > 0)
                    {
                        var series = new LineSeries { Color = OxyColor.FromRgb(0x00, 0x78, 0xD4), StrokeThickness = 1.8, MarkerType = MarkerType.Circle, MarkerSize = 2.5, MarkerFill = OxyColor.FromRgb(0x00, 0x78, 0xD4) };
                        foreach (var pt in history) series.Points.Add(new DataPoint(pt.DataCount, pt.RSquared));
                        model.Series.Add(series);
                    }
                }
                catch { }
            }
            EvolutionPlot = model;
        }

        private List<TrainingDataPoint> ParseTrainingData(string? json)
        {
            if (string.IsNullOrEmpty(json)) return new();
            try
            {
                var records = JsonConvert.DeserializeObject<List<TrainingDataRecord>>(json);
                return records?.Where(r => r.Factors != null).Select(r => new TrainingDataPoint { Factors = r.Factors!, Response = r.Response }).ToList() ?? new();
            }
            catch { return new(); }
        }

        // ── DTO ──
        private class EvolutionPoint { [JsonProperty("data_count")] public int DataCount { get; set; } [JsonProperty("r_squared")] public double RSquared { get; set; } }
        private class TrainingDataRecord { [JsonProperty("factors")] public Dictionary<string, double>? Factors { get; set; } [JsonProperty("response")] public double Response { get; set; } [JsonProperty("source")] public string? Source { get; set; } [JsonProperty("batch_name")] public string? BatchName { get; set; } [JsonProperty("timestamp")] public string? Timestamp { get; set; } }
        private class TrainingDataPoint { public Dictionary<string, double> Factors { get; set; } = new(); public double Response { get; set; } }
    }
}
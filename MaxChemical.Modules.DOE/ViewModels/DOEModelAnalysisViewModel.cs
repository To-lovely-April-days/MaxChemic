using MaxChemical.Infrastructure.DOE;
using MaxChemical.Infrastructure.Events;
using MaxChemical.Infrastructure.Services;
using MaxChemical.Logging;
using MaxChemical.Modules.DOE.Data;
using MaxChemical.Modules.DOE.Models;
using MaxChemical.Modules.DOE.OLS.Core;
using MaxChemical.Modules.DOE.OLS.Services;
using MaxChemical.Modules.DOE.Services;
using MaxChemical.Modules.DOE.Views;
using Newtonsoft.Json;
using OfficeOpenXml;
using OxyPlot;
using OxyPlot.Annotations;
using OxyPlot.Axes;
using OxyPlot.Legends;
using OxyPlot.Series;
using Prism.Commands;
using Prism.Events;
using Prism.Mvvm;
using Python.Runtime;
using System;
using System.Collections;
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
        /// <summary>
        /// ★ 修复: 从 double 改为 object，支持类别因子字符串值
        /// 连续因子存 double (如 135.2)，类别因子存 string (如 "催化剂B")
        /// </summary>
        public object Value { get; set; } = 0.0;
        public double BarWidth { get; set; }

        /// <summary>
        /// 显示用文本: 连续因子保留4位小数，类别因子直接显示标签
        /// </summary>
        public string DisplayValue => Value is double d ? d.ToString("F4") : Value?.ToString() ?? "";

        /// <summary>
        /// 是否为数值类型（连续因子）
        /// </summary>
        public bool IsNumeric => Value is double or int or long or float;

        /// <summary>
        /// 安全获取数值（类别因子返回 0）
        /// </summary>
        public double NumericValue
        {
            get
            {
                if (Value is double d) return d;
                if (Value is int i) return i;
                if (Value is long l) return l;
                if (Value is float f) return f;
                if (double.TryParse(Value?.ToString(), out var parsed)) return parsed;
                return 0.0;
            }
        }
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
    /// 模型项（用于 Model Reduction CheckBox 列表）
    /// </summary>
    public class ModelTermItem : BindableBase
    {
        /// <summary>项名（如 "温度", "温度×压力", "温度²"）</summary>
        public string TermName { get; set; } = "";

        /// <summary>P 值</summary>
        public double PValue { get; set; }

        /// <summary>P 值显示文本</summary>
        public string PValueText => PValue < 0.001 ? "p<0.001" : $"p={PValue:F3}";

        /// <summary>是否显著 (p < 0.05)</summary>
        public bool IsSignificant => PValue < 0.05;

        /// <summary>是否被用户选中保留在模型中</summary>
        private bool _isIncluded = true;
        public bool IsIncluded { get => _isIncluded; set => SetProperty(ref _isIncluded, value); }
    }
    /// <summary>
    /// 类别因子展开方程项（绑定到 UI ItemsControl）
    /// </summary>
    public class CategoryEquationItem
    {
        public string LevelName { get; set; } = "";
        public string Equation { get; set; } = "";
    }
    /// <summary>
    /// Pareto 图中的效应项 — 支持点击切换
    /// </summary>
    public class ParetoTermItem : BindableBase
    {
        public string TermName { get; set; } = "";
        public double AbsT { get; set; }
        public double PValue { get; set; }
        public bool IsSignificant { get; set; }

        private bool _isIncluded = true;
        public bool IsIncluded { get => _isIncluded; set => SetProperty(ref _isIncluded, value); }
    }


    /// <summary>
    /// 模型分析 ViewModel — 方案B: GPR/OLS 双 Tab 布局
    /// GPR Tab: 按模型查看（跨批次累积）
    /// OLS Tab: 按批次独立分析（先选批次再看 ANOVA）
    /// </summary>
    public class DOEModelAnalysisViewModel : BindableBase
    { /// <summary>
      /// 刻画器中的一行（对应一个响应变量）
      /// </summary>
        public class ProfilerRowViewModel : BindableBase
        {
            public string ResponseName { get; set; } = "";

            private string _responseLabel = "";
            public string ResponseLabel
            {
                get => _responseLabel;
                set => SetProperty(ref _responseLabel, value);
            }

            public List<PlotModel> Plots { get; set; } = new();

            /// <summary>该行的 Y 轴范围</summary>
            public double YMin { get; set; }
            public double YMax { get; set; }

          
            public object? Data { get; set; }
        }

        private readonly IGPRModelService _gprService;
        private readonly IDOEAnalysisService _analysisService;
        private readonly IDOERepository _repository;
        private readonly IFlowParameterProvider _paramProvider;
        private readonly IDialogService _dialogService;
        private readonly DOEExportService _exportService;
        private readonly ILogService _logger;
        private readonly IModelRouter _modelRouter;
        private readonly IDesirabilityService _desirabilityService;
        private readonly ILocalizationService _localizationService;
        private readonly IEventAggregator _eventAggregator;

        // GPR
        private ObservableCollection<GPRModelTabItem> _models = new();
        private GPRModelDetail _selectedModel = new();
        private bool _isLoading;
        private string _statusMessage = "";
        // ── Loading 进度条属性 ──
        private ObservableCollection<LoadingStepItem> _loadingSteps = new();
        private int _loadingProgressPercent;
        private double _loadingProgressWidth;
        private string _loadingStepText = "";
        public ObservableCollection<LoadingStepItem> LoadingSteps
        {
            get => _loadingSteps;
            set => SetProperty(ref _loadingSteps, value);
        }
        public int LoadingProgressPercent
        {
            get => _loadingProgressPercent;
            set => SetProperty(ref _loadingProgressPercent, value);
        }
        public double LoadingProgressWidth
        {
            get => _loadingProgressWidth;
            set => SetProperty(ref _loadingProgressWidth, value);
        }
        public string LoadingStepText
        {
            get => _loadingStepText;
            set => SetProperty(ref _loadingStepText, value);
        }
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
        // ── 方程展示 ──
        private List<CategoryEquationItem> _categoryEquations = new();
        private bool _hasCategoricalEquations;
        private string _categoricalFactorName = "";

        public List<CategoryEquationItem> CategoryEquations { get => _categoryEquations; set => SetProperty(ref _categoryEquations, value); }
        public bool HasCategoricalEquations { get => _hasCategoricalEquations; set => SetProperty(ref _hasCategoricalEquations, value); }
        public string CategoricalFactorName { get => _categoricalFactorName; set => SetProperty(ref _categoricalFactorName, value); }

        // ── Pareto 图 + Model Reduction ──
        private PlotModel? _olsParetoPlot;
        private List<ParetoTermItem> _paretoTerms = new();
        private string _includedTermsText = "";
        private string _excludedTermsText = "";

        public PlotModel? OlsParetoPlot { get => _olsParetoPlot; set => SetProperty(ref _olsParetoPlot, value); }
        public string IncludedTermsText { get => _includedTermsText; set => SetProperty(ref _includedTermsText, value); }
        public string ExcludedTermsText { get => _excludedTermsText; set => SetProperty(ref _excludedTermsText, value); }

        // ── 残差四合一 ──
        private PlotModel? _normalProbPlot;
        private PlotModel? _residVsFittedPlot;
        private PlotModel? _residHistogramPlot;
        private PlotModel? _residVsOrderPlot;

        public PlotModel? NormalProbPlot { get => _normalProbPlot; set => SetProperty(ref _normalProbPlot, value); }
        public PlotModel? ResidVsFittedPlot { get => _residVsFittedPlot; set => SetProperty(ref _residVsFittedPlot, value); }
        public PlotModel? ResidHistogramPlot { get => _residHistogramPlot; set => SetProperty(ref _residHistogramPlot, value); }
        public PlotModel? ResidVsOrderPlot { get => _residVsOrderPlot; set => SetProperty(ref _residVsOrderPlot, value); }

        // ── ★ v6: 交互式预测刻画器（JMP 风格拖动联动） ──
        private List<PlotModel> _profilerPlots = new();
        private string _profilerCurrentPredicted = "";
        private string _optimalResultText = "";
        private Dictionary<string, object> _profilerCurrentValues = new();  // 当前各因子值
        private ProfilerResult? _lastProfilerData;  // 缓存最近一次 profiler 数据
        private List<string> _profilerFactorOrder = new();  // 因子顺序（与 PlotModel 列表对齐）
        private bool _isProfilerLoaded;

        public List<PlotModel> ProfilerPlots { get => _profilerPlots; set => SetProperty(ref _profilerPlots, value); }
        public string ProfilerCurrentPredicted { get => _profilerCurrentPredicted; set => SetProperty(ref _profilerCurrentPredicted, value); }
        public string OptimalResultText { get => _optimalResultText; set => SetProperty(ref _optimalResultText, value); }
        public bool IsProfilerLoaded { get => _isProfilerLoaded; set => SetProperty(ref _isProfilerLoaded, value); }
        // ── ★ v7: 不可估计项警告 ──
        private string _inestimableWarning = "";
        private bool _hasInestimableWarning;

        public string InestimableWarning { get => _inestimableWarning; set => SetProperty(ref _inestimableWarning, value); }
        public bool HasInestimableWarning { get => _hasInestimableWarning; set => SetProperty(ref _hasInestimableWarning, value); }

        // ── ★ v7: OLS 响应曲面 ──
        private BitmapImage? _olsSurfaceImage;
        private PlotModel? _olsContourPlot;
        private List<string> _olsContinuousFactors = new();
        private string? _olsSurfaceFactor1;
        private string? _olsSurfaceFactor2;

        public BitmapImage? OlsSurfaceImage { get => _olsSurfaceImage; set => SetProperty(ref _olsSurfaceImage, value); }
        public PlotModel? OlsContourPlot { get => _olsContourPlot; set => SetProperty(ref _olsContourPlot, value); }
        public List<string> OlsContinuousFactors { get => _olsContinuousFactors; set => SetProperty(ref _olsContinuousFactors, value); }
        public string? OlsSurfaceFactor1
        {
            get => _olsSurfaceFactor1;
            set
            {
                SetProperty(ref _olsSurfaceFactor1, value);
                GenerateOlsSurfaceCommand?.RaiseCanExecuteChanged();
                RebuildSurfaceFixedFactors();  
            }
        }
        public string? OlsSurfaceFactor2
        {
            get => _olsSurfaceFactor2;
            set
            {
                SetProperty(ref _olsSurfaceFactor2, value);
                GenerateOlsSurfaceCommand?.RaiseCanExecuteChanged();
                RebuildSurfaceFixedFactors();  
            }
        }
        // ── 曲面固定因子 Slider 数据 ──
        private ObservableCollection<SurfaceFixedFactorItem> _surfaceFixedFactors = new();
        public ObservableCollection<SurfaceFixedFactorItem> SurfaceFixedFactors
        {
            get => _surfaceFixedFactors;
            set => SetProperty(ref _surfaceFixedFactors, value);
        }
        // ════════════ 1. 新增字段和属性（在 OLS 响应曲面属性区域之后添加） ════════════

        // ── ★ v8: 异常点分析 ──
        private List<OutlierItem> _outlierItems = new();
        private bool _hasOutliers;
        private string _outlierSummaryText = "";

        public List<OutlierItem> OutlierItems { get => _outlierItems; set => SetProperty(ref _outlierItems, value); }
        public bool HasOutliers { get => _hasOutliers; set => SetProperty(ref _hasOutliers, value); }
        public string OutlierSummaryText { get => _outlierSummaryText; set => SetProperty(ref _outlierSummaryText, value); }

        private EffectsNormalPlotData? _normalPlotData;
        private EffectsNormalPlotData? _halfNormalPlotData;
        public EffectsNormalPlotData? NormalPlotData
        {
            get => _normalPlotData;
            set => SetProperty(ref _normalPlotData, value);
        }

        public EffectsNormalPlotData? HalfNormalPlotData
        {
            get => _halfNormalPlotData;
            set => SetProperty(ref _halfNormalPlotData, value);
        }

        // ── ★ v8: Tukey HSD ──
        private List<TukeyComparisonItem> _tukeyComparisons = new();
        private string _tukeyFactorName = "";
        private bool _hasTukeyResult;
        private Dictionary<string, double> _tukeyGroupMeans = new();

       
        // ── ★ v13: 编码/未编码切换 ──
        private bool _isCodedEquation = true;
        public bool IsCodedEquation
        {
            get => _isCodedEquation;
            set
            {
                if (SetProperty(ref _isCodedEquation, value) && OlsResult != null)
                {
                    UpdateEquationsDisplay(OlsResult);
                    UpdateCoefficientTableDisplay(OlsResult);  // ★ v14: 同步切换系数表
                    RaisePropertyChanged(nameof(EquationModeText));
                }
            }
        }
        private bool _hasCurvatureTest;
        /// <summary>
        /// ★ v17: 是否有弯曲检验结果（全因子+中心点时为 true）
        /// 用于控制系数表"效应"列的显隐
        /// </summary>
        public bool HasCurvatureTest
        {
            get => _hasCurvatureTest;
            set => SetProperty(ref _hasCurvatureTest, value);
        }

        // ★ v17: 弯曲提示文本
        private string _curvatureHint = "";
        public string CurvatureHint
        {
            get => _curvatureHint;
            set => SetProperty(ref _curvatureHint, value);
        }
        private string _paretoSubtitle = "Pareto of LogWorth = −log₁₀(p-value). Click bars to toggle include/exclude.";
        public string ParetoSubtitle
        {
            get => _paretoSubtitle;
            set => SetProperty(ref _paretoSubtitle, value);
        }
        public string EquationModeText => 
            IsCodedEquation 
            ? _localizationService.GetString("Doe_Analysis_EquationMode_Code","编码单位")
            : _localizationService.GetString("Doe_Analysis_EquationMode_Uncode", "未编码单位");
      
        public List<TukeyComparisonItem> TukeyComparisons { get => _tukeyComparisons; set => SetProperty(ref _tukeyComparisons, value); }
        public string TukeyFactorName { get => _tukeyFactorName; set => SetProperty(ref _tukeyFactorName, value); }
        public bool HasTukeyResult { get => _hasTukeyResult; set => SetProperty(ref _hasTukeyResult, value); }
        private double? _paretoLogWorthCrit = 1.301;
        private double _paretoAlpha = 0.05;
        private string _currentEquationText = "";
        public string CurrentEquationText { get => _currentEquationText; set => SetProperty(ref _currentEquationText, value); }

        // ── OLS 直接导入 (不依赖批次) ──
        private bool _isDirectImportMode;
        private string _currentOlsModelType = "quadratic";
        private string _directImportFilePath = "";
        private List<string> _directImportFactorNames = new();
        private List<string> _directImportResponseNames = new();
        private Dictionary<string, string> _directImportFactorTypes = new();
        private List<Dictionary<string, object>> _directImportFactorsData = new();
        private Dictionary<string, List<double>> _directImportResponsesData = new();
        /// <summary>是否处于直接导入模式（非批次模式）</summary>
        public bool IsDirectImportMode
        {
            get => _isDirectImportMode;
            set { SetProperty(ref _isDirectImportMode, value); RaisePropertyChanged(nameof(ShowDirectImportPanel)); }
        }

        /// <summary>直接导入的文件路径</summary>
        public string DirectImportFilePath
        {
            get => _directImportFilePath;
            set => SetProperty(ref _directImportFilePath, value);
        }

        /// <summary>是否显示直接导入面板（始终显示）</summary>
        public bool ShowDirectImportPanel => true;

        private List<CoefficientDisplayRow> _displayCoefficients = new();
        public List<CoefficientDisplayRow> DisplayCoefficients
        {
            get => _displayCoefficients;
            set => SetProperty(ref _displayCoefficients, value);
        }
        // ── ★ v3: Desirability 集成到预测刻画器 ──
        private bool _isDesirabilityVisible;
        private double _desirabilityCompositeD;
        private ObservableCollection<SavedSolution> _savedSolutions = new();
        private bool _hasSavedSolutions;

        public bool IsDesirabilityVisible
        {
            get => _isDesirabilityVisible;
            set
            {
                if (SetProperty(ref _isDesirabilityVisible, value))
                    _ = RebuildProfilerWithDesirabilityAsync();
            }
        }
        public double DesirabilityCompositeD
        {
            get => _desirabilityCompositeD;
            set => SetProperty(ref _desirabilityCompositeD, value);
        }
        public ObservableCollection<SavedSolution> SavedSolutions
        {
            get => _savedSolutions;
            set => SetProperty(ref _savedSolutions, value);
        }
        public bool HasSavedSolutions
        {
            get => _hasSavedSolutions;
            set => SetProperty(ref _hasSavedSolutions, value);
        }
        // ★ v3: 意愿行图表（d(y) 曲线 + 综合 D 曲线）
        private List<PlotModel> _desirabilityPlots = new();
        public List<PlotModel> DesirabilityPlots
        {
            get => _desirabilityPlots;
            set => SetProperty(ref _desirabilityPlots, value);
        }
        private List<ProfilerRowViewModel> _desirabilityRows = new();
        public List<ProfilerRowViewModel> DesirabilityRows
        {
            get => _desirabilityRows;
            set => SetProperty(ref _desirabilityRows, value);
        }
        // 缓存当前的 Desirability 配置（从 LoadConfigAsync 加载）
        private List<DesirabilityResponseConfig> _currentDesirabilityConfigs = new();

        // ── ★ v9: C# 端实时预测器 ──
       
        private double _profilerYMin, _profilerYMax;  // 锁定 Y 轴范围
                                                      // 在属性区域添加:
        private Surface3DData? _olsSurface3DData;
        public Surface3DData? OlsSurface3DData
        {
            get => _olsSurface3DData;
            set => SetProperty(ref _olsSurface3DData, value);
        }

        private bool _keepCtPtInRefit = true;  // ★ 默认勾选, 保留弯曲检验;
        public bool KeepCtPtInRefit
        {
            get => _keepCtPtInRefit;
            set => SetProperty(ref _keepCtPtInRefit, value);
        }
        // ══════════════ 模型阶数 ══════════════
        private int _selectedModelOrder = 2;
        private List<int> _availableModelOrders = new();
        private bool _showModelOrderSelector;
        private DOEDesignMethod _currentDesignMethod = DOEDesignMethod.FullFactorial;

        /// <summary>
        /// 模型中包含项的阶数 — 对标 Minitab "Include terms in the model up through order"
        /// </summary>
        public int SelectedModelOrder
        {
            get => _selectedModelOrder;
            set
            {
                if (SetProperty(ref _selectedModelOrder, value))
                {
                    // 持久化到当前响应
                    _ = PersistModelOrderAsync(value);
                    // 实时刷新分析
                    _ = RefreshOlsAnalysisAsync();
                }
            }
        }

        public List<int> AvailableModelOrders
        {
            get => _availableModelOrders;
            set => SetProperty(ref _availableModelOrders, value);
        }

        /// <summary>
        /// 是否显示模型阶数下拉框 — 仅部分因子和全因子时为 true
        /// </summary>
        public bool ShowModelOrderSelector
        {
            get => _showModelOrderSelector;
            set => SetProperty(ref _showModelOrderSelector, value);
        }

        private async Task PersistModelOrderAsync(int modelOrder)
        {
            if (SelectedOlsBatch == null || string.IsNullOrEmpty(_selectedResponseName)) return;
            try
            {
                await _repository.UpdateResponseModelOrderAsync(
                    SelectedOlsBatch.BatchId, _selectedResponseName, modelOrder);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "持久化 ModelOrder 失败");
            }
        }
        /// <summary>
        /// 根据当前设计类型和因子数，更新阶数下拉框的可见性和选项列表
        /// </summary>
        private void UpdateModelOrderSelector(DOEBatch? batch, int? savedModelOrder)
        {
            if (batch == null)
            {
                ShowModelOrderSelector = false;
                return;
            }

            _currentDesignMethod = batch.DesignMethod;
            bool isFactorial = batch.DesignMethod == DOEDesignMethod.FullFactorial
                            || batch.DesignMethod == DOEDesignMethod.FractionalFactorial;
            ShowModelOrderSelector = isFactorial;

            if (!isFactorial) return;

            int k = batch.Factors?.Count ?? 2;

            if (batch.DesignMethod == DOEDesignMethod.FractionalFactorial)
            {
                // 部分因子: 上限 = resolution - 1
                int resolution = 3; // 默认
                if (!string.IsNullOrEmpty(batch.DesignConfigJson))
                {
                    try
                    {
                        var config = Newtonsoft.Json.JsonConvert.DeserializeObject<Dictionary<string, object>>(batch.DesignConfigJson);
                        if (config != null && config.TryGetValue("fractionalResolution", out var r) && r != null)
                        {
                            if (int.TryParse(r.ToString(), out var res))
                                resolution = res;
                        }
                    }
                    catch { }
                }
                int maxOrder = Math.Min(k, resolution - 1);
                AvailableModelOrders = Enumerable.Range(1, Math.Max(1, maxOrder)).ToList();
            }
            else
            {
                // 全因子: 上限 = k
                AvailableModelOrders = Enumerable.Range(1, k).ToList();
            }

            // 恢复保存的值，没有则默认 2
            int defaultOrder = Math.Min(2, AvailableModelOrders.Last());
            int order = savedModelOrder ?? defaultOrder;
            // 确保在可选范围内
            if (!AvailableModelOrders.Contains(order))
                order = AvailableModelOrders.Last();

            // 直接设置字段避免触发 setter 中的 RefreshOlsAnalysisAsync
            _selectedModelOrder = order;
            RaisePropertyChanged(nameof(SelectedModelOrder));
        }
        // ── Commands ──
        public DelegateCommand RefitReducedModelCommand { get; }
        public DelegateCommand RestoreFullModelCommand { get; }
        public DelegateCommand LoadProfilerCommand { get; }
        public DelegateCommand FindOptimalMaxCommand { get; }
        public DelegateCommand FindOptimalMinCommand { get; }
        // 在构造函数末尾添加:
        public DelegateCommand GenerateOlsSurfaceCommand { get; }
        public DelegateCommand ExcludeOutliersCommand { get; }
        public DelegateCommand RestoreAllDataCommand { get; }
   
        public DelegateCommand ExportReportCommand { get; }
        // 声明:
        public DelegateCommand ToggleEquationCodingCommand { get; }
        public DelegateCommand OlsImportExcelCommand { get; }
        public DelegateCommand ToggleDesirabilityCommand { get; }
        public DelegateCommand MaximizeDesirabilityCommand { get; }
        public DelegateCommand MaximizeAndRememberCommand { get; }
        public DelegateCommand SetDesirabilityCommand { get; }
        public DelegateCommand SaveDesirabilityCommand { get; }
        public DelegateCommand SaveDesirabilityFormulaCommand { get; }
        private MultiResponseOlsPredictor? _multiResponsePredictor;
        // 多响应刻画器
        private Dictionary<string, ProfilerResult> _multiProfilerData = new();
        private Dictionary<string, (double yMin, double yMax)> _multiProfilerYRanges = new();
        private bool _isMultiResponseProfiler;
        private List<string> _profilerResponseOrder = new();
        // 绑定属性: 多行刻画器
        private List<ProfilerRowViewModel> _profilerRows = new();
        public List<ProfilerRowViewModel> ProfilerRows
        {
            get => _profilerRows;
            set => SetProperty(ref _profilerRows, value);
        }

        // 多响应预测值文本 (多行显示)
        private string _multiProfilerPredictedText = "";
        public string MultiProfilerPredictedText
        {
            get => _multiProfilerPredictedText;
            set => SetProperty(ref _multiProfilerPredictedText, value);
        }

        public DOEModelAnalysisViewModel(
            IGPRModelService gprService, IDOEAnalysisService analysisService,
            IDOERepository repository, IFlowParameterProvider paramProvider,
            IDialogService dialogService, DOEExportService exportService,
            IModelRouter modelRouter, IDesirabilityService desirabilityService,
            ILogService logger,
            ILocalizationService localization,
            IEventAggregator eventAggregator)
        {
            _gprService = gprService; _analysisService = analysisService;
            _repository = repository; _paramProvider = paramProvider;
            _dialogService = dialogService; _exportService = exportService;
            _modelRouter = modelRouter; _desirabilityService = desirabilityService;
            _logger = logger?.ForContext<DOEModelAnalysisViewModel>() ?? throw new ArgumentNullException(nameof(logger));
            // 本地化服务，注册语言更改事件
            _localizationService = localization ?? throw new ArgumentNullException(nameof(localization));
            _eventAggregator = eventAggregator ?? throw new ArgumentNullException(nameof(eventAggregator));
            _eventAggregator.GetEvent<LanguageChangedEvent>()?.Subscribe(OnLanguageChangedEvent, false);

            OlsStatusText = _localizationService.GetString("Doe_Analysis_Status_SwitchAuto", "切换到此页后将自动加载可分析的批次列表");
            OnLanguageChangedEvent("");

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
            RefitReducedModelCommand = new DelegateCommand(async () => await RefitReducedModelAsync());
            RestoreFullModelCommand = new DelegateCommand(async () => await RestoreFullModelAsync());
            LoadProfilerCommand = new DelegateCommand(async () => await LoadPredictionProfilerAsync());
            FindOptimalMaxCommand = new DelegateCommand(async () => await FindOlsOptimalAsync(true));
            FindOptimalMinCommand = new DelegateCommand(async () => await FindOlsOptimalAsync(false));
            GenerateOlsSurfaceCommand = new DelegateCommand(
                async () => await LoadOlsSurfaceAsync(),
                () => !string.IsNullOrEmpty(OlsSurfaceFactor1) && !string.IsNullOrEmpty(OlsSurfaceFactor2)
                       && OlsSurfaceFactor1 != OlsSurfaceFactor2);
            // 构造函数中初始化（添加到末尾）:
            ExcludeOutliersCommand = new DelegateCommand(async () => await ExcludeOutliersAsync());
            RestoreAllDataCommand = new DelegateCommand(async () => await RestoreAllDataAsync());
            
            ExportReportCommand = new DelegateCommand(async () => await ExportOlsReportAsync());
            ToggleEquationCodingCommand = new DelegateCommand(() => IsCodedEquation = !IsCodedEquation);
            OlsImportExcelCommand = new DelegateCommand(async () => await OlsImportExcelAsync());
            // 在构造函数中追加:
            ToggleDesirabilityCommand = new DelegateCommand(() => IsDesirabilityVisible = !IsDesirabilityVisible);
            MaximizeDesirabilityCommand = new DelegateCommand(async () => await MaximizeDesirabilityAsync());
            MaximizeAndRememberCommand = new DelegateCommand(async () => await MaximizeAndRememberAsync());
            SetDesirabilityCommand = new DelegateCommand(async () => await ShowSetDesirabilityDialogAsync());
            SaveDesirabilityCommand = new DelegateCommand(async () => await SaveDesirabilityValuesAsync());
            SaveDesirabilityFormulaCommand = new DelegateCommand(async () => await SaveDesirabilityFormulaAsync());
        }

        #region 文本标签本地化

        private string _titleTxt;
        private string _cbResponseTxt;
        private string _cbDegreeTxt;
        private string _cbDegreeTipTxt;
        private string _cbEquationTxt;
        private string _exportReportTxt;
        private string _reAnalysisTxt;
        private string _modelAbstractTxt;
        private string _modelPTxt;
        private string _paretoTxt;
        private string _clickRemoveTxt;
        private string _checkRemainTxt;
        private string _resumeCompleteTxt;
        private string _fitTxt;
        private string _residualPlotTxt;
        private string _curvedSurfaceTxt;
        private string _carverTxt;
        private string _effectDiagramTxt;
        private string _coefficientTxt;
        private string _equationTxt;
        private string _outliersTxt;
        private string _normalPlotTxt;
        private string _residualFitTxt;
        private string _residualhistogramTxt;
        private string _residualOrderTxt;

        private string _curvedSettingTxt;
        private string _factorXTxt;
        private string _factorYTxt;
        private string _generateTxt;
        private string _fixedFactorTxt;
        private string _curvedTipTxt;
        private string _3dRespCurvedTxt;
        private string _contourTxt;

        private string _carverTitleTxt;
        private string _maxTxt;
        private string _minTxt;
        private string _function;
        private string _carverLoadingTxt;

        private string _anovaHeaderSourceTxt;
        private string _fValueHeaderTxt;

        private string _finalEquationTxt;
        private string _categoryTxt;
        private string _expandTxt;

        private string _coefficientItemTxt;
        private string _effectTxt;
        private string _coefficientHeader;
        private string _tValueHeaderTxt;

        private string _observeValueHeader;
        private string _fitValueHeader;
        private string _residualHeader;
        private string _standardHeader;
        private string _leverHeader;
        private string _reasonHeader;
        private string _distHeader;

        private string _comparisonTxt;
        private string _compareHeader;
        private string _diffHeader;
        private string _pValueHeader;
        private string _remarkableHeader;

        public string TitleTxt { get { return _titleTxt; } set { SetProperty(ref _titleTxt, value); } }
        public string CbResponseTxt { get { return _cbResponseTxt; } set { SetProperty(ref _cbResponseTxt, value); } }
        public string CbDegreeTxt { get { return _cbDegreeTxt; } set { SetProperty(ref _cbDegreeTxt, value); } }
        public string CbDegreeTipTxt { get { return _cbDegreeTipTxt; } set { SetProperty(ref _cbDegreeTipTxt, value); } }
        public string CbEquationTxt { get { return _cbEquationTxt; } set { SetProperty(ref _cbEquationTxt, value); } }
        public string ExportReportTxt { get { return _exportReportTxt; } set { SetProperty(ref _exportReportTxt, value); } }
        public string ReAnalysisTxt { get { return _reAnalysisTxt; } set { SetProperty(ref _reAnalysisTxt, value); } }
        public string ModelAbstractTxt { get { return _modelAbstractTxt; } set { SetProperty(ref _modelAbstractTxt, value); } }
        public string ModelPTxt { get { return _modelPTxt; } set { SetProperty(ref _modelPTxt, value); } }
        public string ParetoTxt { get { return _paretoTxt; } set { SetProperty(ref _paretoTxt, value); } }
        public string ClickRemoveTxt { get { return _clickRemoveTxt; } set { SetProperty(ref _clickRemoveTxt, value); } }
        public string CheckRemainTxt { get { return _checkRemainTxt; } set { SetProperty(ref _checkRemainTxt, value); } }
        public string ResumeCompleteTxt { get { return _resumeCompleteTxt; } set { SetProperty(ref _resumeCompleteTxt, value); } }
        public string FitTxt { get { return _fitTxt; } set { SetProperty(ref _fitTxt, value); } }
        public string ResidualPlotTxt { get { return _residualPlotTxt; } set { SetProperty(ref _residualPlotTxt, value); } }
        public string CurvedSurfaceTxt { get { return _curvedSurfaceTxt; } set { SetProperty(ref _curvedSurfaceTxt, value); } }
        public string CarverTxt { get { return _carverTxt; } set { SetProperty(ref _carverTxt, value); } }
        public string EffectDiagramTxt { get { return _effectDiagramTxt; } set { SetProperty(ref _effectDiagramTxt, value); } }
        public string CoefficientTxt { get { return _coefficientTxt; } set { SetProperty(ref _coefficientTxt, value); } }
        public string EquationTxt { get { return _equationTxt; } set { SetProperty(ref _equationTxt, value); } }
        public string OutliersTxt { get { return _outliersTxt; } set { SetProperty(ref _outliersTxt, value); } }
        public string NormalPlotTxt { get { return _normalPlotTxt; } set { SetProperty(ref _normalPlotTxt, value); } }
        public string ResidualFitTxt { get { return _residualFitTxt; } set { SetProperty(ref _residualFitTxt, value); } }
        public string ResidualhistogramTxt { get { return _residualhistogramTxt; } set { SetProperty(ref _residualhistogramTxt, value); } }
        public string ResidualOrderTxt { get { return _residualOrderTxt; } set { SetProperty(ref _residualOrderTxt, value); } }

        public string CurvedSettingTxt { get { return _curvedSettingTxt; } set { SetProperty(ref _curvedSettingTxt, value); } }
        public string FactorXTxt { get { return _factorXTxt; } set { SetProperty(ref _factorXTxt, value); } }
        public string FactorYTxt { get { return _factorYTxt; } set { SetProperty(ref _factorYTxt, value); } }
        public string GenerateTxt { get { return _generateTxt; } set { SetProperty(ref _generateTxt, value); } }
        public string FixedFactorTxt { get { return _fixedFactorTxt; } set { SetProperty(ref _fixedFactorTxt, value); } }
        public string CurvedTipTxt { get { return _curvedTipTxt; } set { SetProperty(ref _curvedTipTxt, value); } }
        public string ResponseCurvedTxt { get { return _3dRespCurvedTxt; } set { SetProperty(ref _3dRespCurvedTxt, value); } }
        public string ContourTxt { get { return _contourTxt; } set { SetProperty(ref _contourTxt, value); } }

        public string CarverTitleTxt { get { return _carverTitleTxt; } set { SetProperty(ref _carverTitleTxt, value); } }
        public string MaxTxt { get { return _maxTxt; } set { SetProperty(ref _maxTxt, value); } }
        public string MinTxt { get { return _minTxt; } set { SetProperty(ref _minTxt, value); } }
        public string Function { get { return _function; } set { SetProperty(ref _function, value); } }
        public string CarverLoadingTxt { get { return _carverLoadingTxt; } set { SetProperty(ref _carverLoadingTxt, value); } }

        public string AnovaHeaderSourceTxt { get { return _anovaHeaderSourceTxt; } set { SetProperty(ref _anovaHeaderSourceTxt, value); } }
        public string FValueHeaderTxt { get { return _fValueHeaderTxt; } set { SetProperty(ref _fValueHeaderTxt, value); } }

        public string FinalEquationTxt { get { return _finalEquationTxt; } set { SetProperty(ref _finalEquationTxt, value); } }
        public string CategoryTxt { get { return _categoryTxt; } set { SetProperty(ref _categoryTxt, value); } }
        public string ExpandTxt { get { return _expandTxt; } set { SetProperty(ref _expandTxt, value); } }

        public string CoefficientItemTxt { get { return _coefficientItemTxt; } set { SetProperty(ref _coefficientItemTxt, value); } }
        public string EffectTxt { get { return _effectTxt; } set { SetProperty(ref _effectTxt, value); } }
        public string CoefficientHeader { get { return _coefficientHeader; } set { SetProperty(ref _coefficientHeader, value); } }
        public string TValueHeade { get { return _tValueHeaderTxt; } set { SetProperty(ref _tValueHeaderTxt, value); } }

        public string ObserveValueHeader { get { return _observeValueHeader; } set { SetProperty(ref _observeValueHeader, value); } }
        public string FitValueHeader { get { return _fitValueHeader; } set { SetProperty(ref _fitValueHeader, value); } }
        public string ResidualHeader { get { return _residualHeader; } set { SetProperty(ref _residualHeader, value); } }
        public string StandardHeader { get { return _standardHeader; } set { SetProperty(ref _standardHeader, value); } }
        public string LeverHeader { get { return _leverHeader; } set { SetProperty(ref _leverHeader, value); } }
        public string ReasonHeader { get { return _reasonHeader; } set { SetProperty(ref _reasonHeader, value); } }
        public string DistHeader { get { return _distHeader; } set { SetProperty(ref _distHeader, value); } }

        public string ComparisonTxt { get { return _comparisonTxt; } set { SetProperty(ref _comparisonTxt, value); } }
        public string CompareHeader { get { return _compareHeader; } set { SetProperty(ref _compareHeader, value); } }
        public string DiffHeader { get { return _diffHeader; } set { SetProperty(ref _diffHeader, value); } }
        public string PValueHeader { get { return _pValueHeader; } set { SetProperty(ref _pValueHeader, value); } }
        public string RemarkableHeader { get { return _remarkableHeader; } set { SetProperty(ref _remarkableHeader, value); } }

        private void OnLanguageChangedEvent(string language)
        {
            TitleTxt = _localizationService.GetString("Doe_Analysis_Title", "Ols 回归分析");
            CbResponseTxt = _localizationService.GetString("Doe_Analysis_RespLabe", "响应");
            CbDegreeTxt = _localizationService.GetString("Doe_Analysis_DegreeLabel", "阶数(I)");
            CbDegreeTipTxt = _localizationService.GetString("Doe_Analysis_DegreeTip", "模型中包含项的阶数 — 对标 Minitab「Include terms in the model up through order」");
            CbEquationTxt = _localizationService.GetString("Doe_Analysis_EquationLabel", "方程");
            ExportReportTxt = _localizationService.GetString("Doe_Analysis_Exprot", "导出报告");
            ReAnalysisTxt = _localizationService.GetString("Doe_Analysis_Re-analysis", "重新分析");
            ModelAbstractTxt = _localizationService.GetString("Doe_Analysis_ModelAbstract", "模型摘要");
            ModelPTxt = _localizationService.GetString("Doe_Analysis_Model-P", "模型 P");
            ParetoTxt = _localizationService.GetString("Doe_Analysis_ParetoLabel", "效应 pareto");
            ClickRemoveTxt = _localizationService.GetString("Doe_Analysis_ClickRemove", " 点击排除");
            CheckRemainTxt = _localizationService.GetString("Doe_Analysis_Remain", "保留弯曲");
            ResumeCompleteTxt = _localizationService.GetString("Doe_Analysis_Resume", "恢复完整");
            FitTxt = _localizationService.GetString("Doe_Analysis_Re-fit", "重拟合");
            ResidualPlotTxt = _localizationService.GetString("Doe_Analysis_Residual", "残差图");
            CurvedSurfaceTxt = _localizationService.GetString("Doe_Analysis_CurvedSuface", "曲面");
            CarverTxt = _localizationService.GetString("Doe_Analysis_Carver", "刻画器");
            EffectDiagramTxt = _localizationService.GetString("Doe_Analysis_EffectDiagram", "效应图");
            CoefficientTxt = _localizationService.GetString("Doe_Analysis_Coefficient", "系数");
            EquationTxt = _localizationService.GetString("Doe_Analysis_EquationLabel", "方程");
            OutliersTxt = _localizationService.GetString("Doe_Analysis_Outliers", "异常点");
            NormalPlotTxt = _localizationService.GetString("Doe_Analysis_NormalPlot", "正太概率图");
            ResidualFitTxt = _localizationService.GetString("Doe_Analysis_ResidualPlot", "残差 vs 拟合值");
            ResidualhistogramTxt = _localizationService.GetString("Doe_Analysis_ResiHistogram", "残差直方图");
            ResidualOrderTxt = _localizationService.GetString("Doe_Analysis_ResiOrder", "残差 vs 观测顺序");
            CurvedSettingTxt = _localizationService.GetString("Doe_Analysis_Curved_Title", "曲面设置");
            FactorXTxt = _localizationService.GetString("Doe_Analysis_Curved_X", "因子 X（水平轴）");
            FactorYTxt = _localizationService.GetString("Doe_Analysis_Curved_Y", "因子 Y（垂直轴）");
            GenerateTxt = _localizationService.GetString("Doe_Analysis_Curved_Generate", "生成曲面");
            FixedFactorTxt = _localizationService.GetString("Doe_Analysis_Curved_Fixed", "固定因子");
            CurvedTipTxt = _localizationService.GetString("Doe_Analysis_Curved_Tip", "调整后点击「生成曲面」更新");
            ResponseCurvedTxt = _localizationService.GetString("Doe_Analysis_Curved_Response", "3D 响应曲面");
            ContourTxt = _localizationService.GetString("Doe_Analysis_Curved_Contour", "等高线图");
            CarverTitleTxt = _localizationService.GetString("Doe_Analysis_Carver_Title", "预测刻画器");
            MaxTxt = _localizationService.GetString("Doe_Analysis_Carver_Max", "最大化");
            MinTxt = _localizationService.GetString("Doe_Analysis_Carver_Min", "最小化");
            Function = _localizationService.GetString("Doe_Analysis_Carver_Func", "意愿函数");
            CarverLoadingTxt = _localizationService.GetString("Doe_Analysis_Carver_LoadTip", "正在加载刻画器...");
            AnovaHeaderSourceTxt = _localizationService.GetString("Doe_Analysis_Anova_Source", "来源");
            FinalEquationTxt = _localizationService.GetString("Doe_Analysis_Equation_Final", "最终方程（");
            CategoryTxt = _localizationService.GetString("Doe_Analysis_Equation_Category", "分类别方程（按 ");
            ExpandTxt = _localizationService.GetString("Doe_Analysis_Equation_Expand", " 展开）");
            CoefficientItemTxt = _localizationService.GetString("Doe_Analysis_Coefficient_Item", "项");
            EffectTxt = _localizationService.GetString("Doe_Analysis_Coefficient_Effec", "效应");
            CoefficientHeader = _localizationService.GetString("Doe_Analysis_Coefficient", "系数");
            ObserveValueHeader = _localizationService.GetString("Doe_Analysis_Outliers_Observe", "观测值");
            FitValueHeader = _localizationService.GetString("Doe_Analysis_Outliers_Fit", "拟合值");
            ResidualHeader = _localizationService.GetString("Doe_Analysis_Outliers_Resid", "残差");
            StandardHeader = _localizationService.GetString("Doe_Analysis_Outliers_Normal", "标准残差");
            LeverHeader = _localizationService.GetString("Doe_Analysis_Outliers_Lever", "杠杆值");
            ReasonHeader = _localizationService.GetString("Doe_Analysis_Outliers_Reason", "原因");
            DistHeader = _localizationService.GetString("Doe_Analysis_Outliers_Dist", "Cook 距离");
            ComparisonTxt = _localizationService.GetString("Doe_Analysis_Tukey_Comparison", "Tukey HSD 多重比较 — ");
            CompareHeader = _localizationService.GetString("Doe_Analysis_Tukey_Compare", "比较");
            DiffHeader = _localizationService.GetString("Doe_Analysis_Tukey_Diff", "差异");
            PValueHeader = _localizationService.GetString("Doe_Analysis_Coefficient_P", "P 值");
            RemarkableHeader = _localizationService.GetString("Doe_Analysis_Tukey_Remarkable", "显著");
            FValueHeaderTxt = _localizationService.GetString("Doe_Analysis_Anova_F", "F 值");
            TValueHeade = _localizationService.GetString("Doe_Analysis_Coefficient_T", "t 值");
        }

        #endregion

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
            set
            {
                if (SetProperty(ref _selectedResponseName, value) && IsOlsTabSelected)
                {
                    if (!string.IsNullOrEmpty(value) && value.Contains(" + "))
                    {
                        _ = LoadMultiResponseProfilerOnlyAsync();
                    }
                    else
                    {
                        _ = SwitchToSingleResponseAsync();
                    }
                }
            }
        }
        private async Task SwitchToSingleResponseAsync()
        {
            // 清理旧的 PlotModel 引用
            var allOldPlots = new List<PlotModel>();
            allOldPlots.AddRange(ProfilerPlots);
            allOldPlots.AddRange(DesirabilityPlots);
            foreach (var row in ProfilerRows)
                allOldPlots.AddRange(row.Plots);
            foreach (var row in DesirabilityRows)
                allOldPlots.AddRange(row.Plots);
            DetachPlotModels(allOldPlots);

            // 重新分析
            if (IsDirectImportMode)
                await RunDirectOlsAnalysisAsync();
            else if (SelectedOlsBatch != null)
            {
                // ★ 切换响应时读取该响应保存的 ModelOrder
                if (ShowModelOrderSelector)
                {
                    var batch = await _repository.GetBatchWithDetailsAsync(SelectedOlsBatch.BatchId);
                    var resp = batch?.Responses?.FirstOrDefault(r => r.ResponseName == _selectedResponseName);
                    int? saved = resp?.ModelOrder;
                    int order = saved ?? Math.Min(2, AvailableModelOrders.LastOrDefault());
                    if (!AvailableModelOrders.Contains(order))
                        order = AvailableModelOrders.Last();
                    _selectedModelOrder = order;
                    RaisePropertyChanged(nameof(SelectedModelOrder));
                }
                await RefreshOlsAnalysisAsync();
            }
               



            // 如果刻画器已打开，刷新为单响应
            if (IsProfilerLoaded)
            {
                _isMultiResponseProfiler = false;
                IsDesirabilityVisible = false;
                DesirabilityPlots = new List<PlotModel>();
                DesirabilityRows = new List<ProfilerRowViewModel>();
                await LoadPredictionProfilerAsync();
            }
        }
        /// <summary>
        /// 选择联合响应时，只加载多响应刻画器（不触发单响应分析）
        /// </summary>
        private async Task LoadMultiResponseProfilerOnlyAsync()
        {
            try
            {
                IsLoading = true;
                OlsStatusText = _localizationService.GetString("Doe_Analysis_Status_InLoadCarver", "正在加载多响应刻画器...");

                // 重置刻画器状态
                IsProfilerLoaded = false;
                _isMultiResponseProfiler = true;
                IsDesirabilityVisible = false;
                DesirabilityPlots = new List<PlotModel>();

                // 加载多响应刻画器
                await LoadPredictionProfilerAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "多响应刻画器加载失败");
                OlsStatusText = string.Format(_localizationService.GetString("Doe_Analysis_Status_LoadFail", "加载失败: {0}"),ex.Message);
            }
            finally
            {
                IsLoading = false;
            }
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
        /// <summary>
        /// 初始化 Loading 步骤列表
        /// </summary>
        private void InitLoadingSteps(string[] stepNames)
        {
            var items = new ObservableCollection<LoadingStepItem>();
            foreach (var name in stepNames)
                items.Add(new LoadingStepItem { StepName = name });
            if (items.Count > 0) items[0].IsActive = true;
            LoadingSteps = items;
            LoadingProgressPercent = 0;
            LoadingProgressWidth = 0;
            LoadingStepText = stepNames.Length > 0 ? stepNames[0] : "";
        }

      

        /// <summary>
        /// 推进到下一步 — async 版本，每步结束让 UI 线程喘息一帧
        /// </summary>
        private async Task AdvanceLoadingStepAsync(int stepIndex, int totalSteps)
        {
            if (stepIndex < 0 || stepIndex >= LoadingSteps.Count) return;

            // 标记当前步完成
            LoadingSteps[stepIndex].IsActive = false;
            LoadingSteps[stepIndex].IsCompleted = true;

            // 激活下一步
            if (stepIndex + 1 < LoadingSteps.Count)
            {
                LoadingSteps[stepIndex + 1].IsActive = true;
                LoadingStepText = LoadingSteps[stepIndex + 1].StepName;
            }
            else
            {
                LoadingStepText = "完成";
            }

            // 更新百分比
            LoadingProgressPercent = (int)((stepIndex + 1.0) / totalSteps * 100);
            LoadingProgressWidth = 320.0 * LoadingProgressPercent / 100.0;

            // ★★★ 关键：让出 UI 线程，让动画和进度条有机会渲染 ★★★
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(
                () => { }, System.Windows.Threading.DispatcherPriority.Render);
        }

        // ══════════════ 公开加载方法 ══════════════
        public async Task LoadBatchAsync(string batchId)
        {
            _currentBatchId = batchId;
            var batch = await _repository.GetBatchWithDetailsAsync(batchId);
            if (batch == null) return;
            _currentFlowId = batch.FlowId;
            FactorNames = batch.Factors.Select(f => f.FactorName).ToList();
            SetResponseNames(batch.Responses.Select(r => r.ResponseName).ToList());
            _selectedResponseName = ResponseNames.FirstOrDefault() ?? "";
            RaisePropertyChanged(nameof(SelectedResponseName));
            if (FactorNames.Count >= 2) { SurfaceFactor1 = FactorNames[0]; SurfaceFactor2 = FactorNames[1]; }
            await LoadModelsAsync();
            await RefreshAllAsync();
        }
        private async Task LoadOlsSurfaceAsync()
        {
            if (SelectedOlsBatch == null || string.IsNullOrEmpty(_selectedResponseName)) return;
            if (string.IsNullOrEmpty(OlsSurfaceFactor1) || string.IsNullOrEmpty(OlsSurfaceFactor2)
                || OlsSurfaceFactor1 == OlsSurfaceFactor2) return;

            IsLoading = true;
            StatusMessage = "正在生成响应曲面...";

            try
            {
                // ★ 收集固定因子值
                var fixedValues = new Dictionary<string, double>();
                foreach (var sf in SurfaceFixedFactors)
                    fixedValues[sf.Name] = sf.Value;

                string contourJson;

                if (fixedValues.Count > 0)
                {
                    // ★ 多因子模式：构建 bounds，固定因子的 min=max=固定值
                    var batch = await _repository.GetBatchWithDetailsAsync(SelectedOlsBatch.BatchId);
                    var boundsDict = new Dictionary<string, object>();
                    if (batch?.Factors != null)
                    {
                        foreach (var f in batch.Factors.Where(f => !f.IsCategorical))
                            boundsDict[f.FactorName] = new[] { f.LowerBound, f.UpperBound };
                    }
                    // 固定因子覆盖为单点（min = max = 固定值）
                    foreach (var kv in fixedValues)
                        boundsDict[kv.Key] = new[] { kv.Value, kv.Value };

                    var boundsJson = JsonConvert.SerializeObject(boundsDict);
                    contourJson = await Task.Run(() =>
                        _analysisService.GetOlsContourDataDirectAsync(
                            OlsSurfaceFactor1, OlsSurfaceFactor2, boundsJson));
                }
                else
                {
                    // 2 因子模式：走原接口
                    contourJson = await _analysisService.GetOlsContourDataAsync(
                        SelectedOlsBatch.BatchId, _selectedResponseName,
                        OlsSurfaceFactor1, OlsSurfaceFactor2);
                }

                var contourData = JsonConvert.DeserializeObject<SurfaceData>(contourJson);
                if (contourData != null && contourData.Z != null)
                {
                    BuildContourPlot(contourData);
                    OlsSurface3DData = new Surface3DData
                    {
                        X = contourData.X.ToArray(),
                        Y = contourData.Y.ToArray(),
                        Z = contourData.Z.Select(row => row.ToArray()).ToArray(),
                        XLabel = OlsSurfaceFactor1,
                        YLabel = OlsSurfaceFactor2
                    };
                }

                OlsSurfaceImage = null;

                if (fixedValues.Count > 0)
                {
                    var fixedStr = string.Join(", ", fixedValues.Select(kv => $"{kv.Key}={kv.Value:F2}"));
                    OlsStatusText = string.Format(_localizationService.GetString("Doe_Analysis_Status_RespGenerate", "响应曲面已生成（固定: {0}）"),fixedStr);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "OLS 响应曲面加载失败");
            }
            finally
            {
                IsLoading = false;
            }
        }
        /// <summary>
        /// 根据当前 X/Y 轴因子选择，刷新固定因子列表
        /// 排除已选为 X/Y 的因子，只显示需要固定的其他连续因子
        /// </summary>
        private async void RebuildSurfaceFixedFactors()
        {
            if (OlsContinuousFactors == null || OlsContinuousFactors.Count <= 2)
            {
                SurfaceFixedFactors = new ObservableCollection<SurfaceFixedFactorItem>();
                return;
            }

            var fixedNames = OlsContinuousFactors
                .Where(f => f != OlsSurfaceFactor1 && f != OlsSurfaceFactor2)
                .ToList();

            if (fixedNames.Count == 0)
            {
                SurfaceFixedFactors = new ObservableCollection<SurfaceFixedFactorItem>();
                return;
            }

            var items = new ObservableCollection<SurfaceFixedFactorItem>();

            foreach (var fname in fixedNames)
            {
                // 保留用户已设的值
                var existing = _surfaceFixedFactors.FirstOrDefault(sf => sf.Name == fname);
                double min = 0, max = 1, val = 0.5;

                if (!IsDirectImportMode && SelectedOlsBatch != null)
                {
                    try
                    {
                        var batch = await _repository.GetBatchWithDetailsAsync(SelectedOlsBatch.BatchId);
                        var factor = batch?.Factors?.FirstOrDefault(f => f.FactorName == fname);
                        if (factor != null)
                        {
                            min = factor.LowerBound;
                            max = factor.UpperBound;
                        }
                    }
                    catch { }
                }
                else if (IsDirectImportMode && _directImportFactorsData.Count > 0)
                {
                    var vals = _directImportFactorsData
                        .Where(r => r.ContainsKey(fname))
                        .Select(r => Convert.ToDouble(r[fname]))
                        .ToList();
                    if (vals.Count > 0) { min = vals.Min(); max = vals.Max(); }
                }

                val = existing?.Value ?? (min + max) / 2.0;
                val = Math.Max(min, Math.Min(max, val));

                items.Add(new SurfaceFixedFactorItem
                {
                    Name = fname,
                    Min = min,
                    Max = max,
                    Value = val
                });
            }

            SurfaceFixedFactors = items;
        }


        /// <summary>
        /// ★ v7: 用 OxyPlot HeatMapSeries 渲染等高线图
        /// </summary>
        private void BuildContourPlot(SurfaceData data)
        {
            var model = new PlotModel { PlotMargins = new OxyThickness(60, 8, 80, 32) };

            int nx = data.X.Count;
            int ny = data.Y.Count;
            if (nx < 2 || ny < 2) { OlsContourPlot = model; return; }

            var heatMap = new HeatMapSeries
            {
                X0 = data.X[0],
                X1 = data.X[nx - 1],
                Y0 = data.Y[0],
                Y1 = data.Y[ny - 1],
                Interpolate = true,
                RenderMethod = HeatMapRenderMethod.Bitmap,
                Data = new double[nx, ny]
            };
            for (int j = 0; j < ny; j++)
                for (int i = 0; i < nx; i++)
                    heatMap.Data[i, j] = data.Z[j][i];
            model.Series.Add(heatMap);

            model.Axes.Add(new LinearAxis { Position = AxisPosition.Bottom, Title = data.XLabel });
            model.Axes.Add(new LinearAxis { Position = AxisPosition.Left, Title = data.YLabel });
            model.Axes.Add(new LinearColorAxis
            {
                Position = AxisPosition.Right,
                Palette = OxyPalettes.Viridis(200),
                Title = _selectedResponseName,
                FontSize = 9
            });

            ApplyJmpStyle(model);
            // 等高线图需要更宽的右边距给 color bar
            model.PlotMargins = new OxyThickness(48, 8, 80, 32);
            OlsContourPlot = model;
        }
        public async Task LoadAsync() { await LoadModelsAsync(); }

        // ═══ ★ v8: 异常点分析面板 ═══

        private async Task LoadOutlierAnalysisAsync()
        {
            if (SelectedOlsBatch == null || string.IsNullOrEmpty(_selectedResponseName)) return;
            try
            {
                var json = await _analysisService.GetOutlierAnalysisAsync(
                    SelectedOlsBatch.BatchId, _selectedResponseName);
                var data = JsonConvert.DeserializeObject<OutlierAnalysisResult>(json);
                if (data == null) return;

                OutlierItems = data.Outliers.Select(o => new OutlierItem
                {
                    Index = o.Index,
                    Actual = o.Actual,
                    Predicted = o.Predicted,
                    Residual = o.Residual,
                    StdResidual = o.StdResidual,
                    CooksD = o.CooksD,
                    Leverage = o.Leverage,
                    Reasons = string.Join("; ", o.Reasons),
                    IsExcluded = false
                }).ToList();

                HasOutliers = data.OutlierCount > 0;
                OutlierSummaryText = data.OutlierCount > 0
                    ? $"检测到 {data.OutlierCount} 个异常点（共 {data.TotalObservations} 个观测）。阈值: Cook's D > {data.Thresholds.CooksD:F3}, |标准化残差| > {data.Thresholds.StdResidual}, 杠杆值 > {data.Thresholds.Leverage:F3}"
                    : $"未检测到异常点（共 {data.TotalObservations} 个观测）";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "异常点分析失败");
                HasOutliers = false;
            }
        }

        private async Task ExcludeOutliersAsync()
        {
            var excluded = OutlierItems.Where(o => o.IsExcluded).Select(o => o.Index).ToList();
            if (excluded.Count == 0)
            {
                _dialogService.ShowError("请先勾选要排除的异常点", "提示");
                return;
            }

            try
            {
                IsLoading = true;
                OlsStatusText = string.Format(_localizationService.GetString("Doe_Analysis_Status_RemoveRefit", "正在排除 {0} 个异常点后重新拟合..."),excluded.Count);

                OlsResult = await _analysisService.RefitExcludingAsync(
                    SelectedOlsBatch!.BatchId, _selectedResponseName, excluded);

                if (OlsResult?.ModelSummary != null)
                {
                    OlsStatusText = string.Format(
                        _localizationService.GetString("Doe_Analysis_Status_RemoveAfter", "排除 {0} 点后: R²={1:F4}, R²adj={2:F4}"),
                        excluded.Count, OlsResult.ModelSummary.RSquared, OlsResult.ModelSummary.RSquaredAdj);
                    UpdateEquationsDisplay(OlsResult);
                    await LoadOlsParetoAsync();
                    await LoadResidualDiagnosticsAsync();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "排除异常点重拟合失败");
                OlsStatusText = string.Format(_localizationService.GetString("Doe_Analysis_Status_RefitFail", "重拟合失败: {0}"),ex.Message);
            }
            finally { IsLoading = false; }
        }

        private async Task RestoreAllDataAsync()
        {
            // 恢复完整数据，重新运行 OLS 分析
            await RefreshOlsAnalysisAsync();
        }
        // ═══ ★ 标准化效应正态图 + 半正态图（替换原主效应图+交互效应图） ═══

        /// <summary>
        /// 加载效应正态图和半正态图（批次模式）
        /// 复用 Pareto 数据，无需新增服务接口
        /// </summary>
        private async Task LoadEffectsNormalPlotsAsync()
        {
            if (SelectedOlsBatch == null || string.IsNullOrEmpty(_selectedResponseName)) return;
            try
            {
                var json = await _analysisService.GetEffectsParetoAsync(
                    SelectedOlsBatch.BatchId, _selectedResponseName);
                var wrapper = JsonConvert.DeserializeObject<ParetoResult>(json);
                if (wrapper?.Effects == null || wrapper.Effects.Count == 0) return;

                double alpha = wrapper.Alpha.GetValueOrDefault(0.05);
                double tCrit = wrapper.TCrit.GetValueOrDefault(
                                   wrapper.LogWorthCrit.GetValueOrDefault(2.0));

                BuildEffectsNormalPlot(wrapper.Effects, alpha, tCrit);
                BuildEffectsHalfNormalPlot(wrapper.Effects, alpha, tCrit);
            }
            catch (Exception ex) { _logger.LogError(ex, "效应正态图加载失败"); }
        }


        /// <summary>
        /// 加载效应正态图和半正态图（直接导入模式）
        /// </summary>
        private async Task LoadDirectEffectsNormalPlotsAsync()
        {
            if (!IsDirectImportMode || string.IsNullOrEmpty(_selectedResponseName)) return;
            try
            {
                var json = await Task.Run(() =>
                    _analysisService.GetEffectsParetoDirectAsync(_selectedResponseName));
                var wrapper = JsonConvert.DeserializeObject<ParetoResult>(json);
                if (wrapper?.Effects == null || wrapper.Effects.Count == 0) return;

                double alpha = wrapper.Alpha.GetValueOrDefault(0.05);
                double tCrit = wrapper.TCrit.GetValueOrDefault(
                                   wrapper.LogWorthCrit.GetValueOrDefault(2.0));

                BuildEffectsNormalPlot(wrapper.Effects, alpha, tCrit);
                BuildEffectsHalfNormalPlot(wrapper.Effects, alpha, tCrit);
            }
            catch (Exception ex) { _logger.LogError(ex, "直接导入效应正态图加载失败"); }
        }

        private void BuildEffectsNormalPlot(List<ParetoEffectItem> effects, double alpha, double tCrit)
        {
            if (effects == null || effects.Count == 0) return;

            var allItems = effects
                .Where(e => e.Term != "Ct Pt" && e.Term != "弯曲")
                .Select(e => new
                {
                    Name = e.Term,
                    StdEffect = e.Effect >= 0 ? e.AbsT : -e.AbsT,
                    AbsEffect = e.AbsT,
                    IsSignificant = e.PValue < alpha
                }).OrderBy(x => x.StdEffect).ToList();

            int n = allItems.Count;
            if (n == 0) return;

            var data = new EffectsNormalPlotData
            {
                ResponseName = _selectedResponseName,
                Alpha = alpha
            };

            for (int i = 0; i < n; i++)
            {
                int rank = i + 1;
                double pct = (rank - 0.3) / (n + 0.4) * 100.0;
                data.Points.Add(new EffectsNormalPoint
                {
                    Name = allItems[i].Name,
                    StdEffect = allItems[i].StdEffect,
                    AbsEffect = allItems[i].AbsEffect,
                    Percent = pct,
                    IsSignificant = allItems[i].IsSignificant
                });
            }

            //// 分布拟合线: Daniel's method
            //// 去掉排序后最小和最大各1个，对中间的效应做 probit 回归
            //// x = a + b * y_probit
            //FitReferenceLine(data.Points, p => p.StdEffect, data);

            NormalPlotData = data;
        }

        private void BuildEffectsHalfNormalPlot(List<ParetoEffectItem> effects, double alpha, double tCrit)
        {
            if (effects == null || effects.Count == 0) return;

            var allItems = effects
                .Where(e => e.Term != "Ct Pt" && e.Term != "弯曲")
                .Select(e => new
                {
                    Name = e.Term,
                    AbsEffect = e.AbsT,
                    StdEffect = e.Effect >= 0 ? e.AbsT : -e.AbsT,
                    IsSignificant = e.PValue < alpha
                }).OrderBy(x => x.AbsEffect).ToList();

            int n = allItems.Count;
            if (n == 0) return;

            var data = new EffectsNormalPlotData
            {
                ResponseName = _selectedResponseName,
                Alpha = alpha
            };

            for (int i = 0; i < n; i++)
            {
                int rank = i + 1;
                double pct = (rank - 0.3) / (n + 0.4) * 100.0;
                data.Points.Add(new EffectsNormalPoint
                {
                    Name = allItems[i].Name,
                    StdEffect = allItems[i].StdEffect,
                    AbsEffect = allItems[i].AbsEffect,
                    Percent = pct,
                    IsSignificant = allItems[i].IsSignificant
                });
            }

            //// 分布拟合线: Daniel's method（用绝对效应）
            //FitReferenceLine(data.Points, p => p.AbsEffect, data);

            HalfNormalPlotData = data;
        }

        /// <summary>
        /// Daniel's method 分布拟合线:
        /// 去掉排序后首尾各1个效应，对中间部分做 probit 回归
        /// x = a + b * y_probit
        /// </summary>
        private void FitReferenceLine(List<EffectsNormalPoint> points,
        Func<EffectsNormalPoint, double> getX, EffectsNormalPlotData data)
        {
            if (points.Count < 2) return;

            // 教科书方法: 对所有效应做 y_probit = a + b * x 的 OLS 回归
            var pairs = points
                .Select(p => new { X = getX(p), YP = Probit(p.Percent / 100.0) }).ToList();

            double mX = pairs.Average(p => p.X);
            double mYP = pairs.Average(p => p.YP);
            double sXY = pairs.Sum(p => (p.X - mX) * (p.YP - mYP));
            double sXX = pairs.Sum(p => (p.X - mX) * (p.X - mX));
            double b = sXX > 1e-12 ? sXY / sXX : 0;  // y_probit = a + b*x
            double a = mYP - b * mX;

            if (Math.Abs(b) > 1e-10)
            {
                // 转为 x = mean + std * y_probit 形式给 JS 端用
                // y_probit = a + b*x  →  x = -a/b + (1/b)*y_probit
                data.RefLineMean = -a / b;
                data.RefLineStd = 1.0 / b;
                _logger.LogInformation("★ 分布拟合线: mean={M:F4}, std={S:F4}", data.RefLineMean, data.RefLineStd);
            }
        }

        /// <summary>
        /// 标准正态逆 CDF（probit）: 输入 p ∈ (0,1)，输出 z 使得 Φ(z) = p
        /// </summary>
        private static double Probit(double p)
        {
            if (p <= 0.0001) return -3.72;
            if (p >= 0.9999) return 3.72;
            if (Math.Abs(p - 0.5) < 1e-10) return 0;
            if (p < 0.5)
                return -RationalApprox(Math.Sqrt(-2.0 * Math.Log(p)));
            else
                return RationalApprox(Math.Sqrt(-2.0 * Math.Log(1 - p)));
        }

        private static double RationalApprox(double t)
        {
            double c0 = 2.515517, c1 = 0.802853, c2 = 0.010328;
            double d1 = 1.432788, d2 = 0.189269, d3 = 0.001308;
            return t - (c0 + c1 * t + c2 * t * t) / (1 + d1 * t + d2 * t * t + d3 * t * t * t);
        }




        // ═══ ★ v8: Tukey HSD ═══

        private async Task LoadTukeyHSDAsync()
        {
            if (SelectedOlsBatch == null || string.IsNullOrEmpty(_selectedResponseName)) return;

            // 检查是否有类别因子
            var batch = await _repository.GetBatchWithDetailsAsync(SelectedOlsBatch.BatchId);
            if (batch == null || !batch.Factors.Any(f => f.IsCategorical))
            {
                HasTukeyResult = false;
                return;
            }

            try
            {
                var catFactor = batch.Factors.First(f => f.IsCategorical).FactorName;
                var json = await _analysisService.GetTukeyHSDAsync(
                    SelectedOlsBatch.BatchId, _selectedResponseName, catFactor);
                var data = JsonConvert.DeserializeObject<TukeyHSDResult>(json);
                if (data == null || data.Error != null)
                {
                    HasTukeyResult = false;
                    return;
                }

                TukeyFactorName = data.FactorName;
                TukeyComparisons = data.Comparisons.Select(c => new TukeyComparisonItem
                {
                    Group1 = c.Group1,
                    Group2 = c.Group2,
                    MeanDiff = c.MeanDiff,
                    PValue = c.PValue,
                    CILower = c.CILower,
                    CIUpper = c.CIUpper,
                    Significant = c.Significant
                }).ToList();

                _tukeyGroupMeans = data.GroupMeans;
                HasTukeyResult = true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Tukey HSD 加载失败");
                HasTukeyResult = false;
            }
        }

        // ══════════════ OLS 批次列表 ══════════════
        private async Task LoadOlsBatchListAsync()
        {
            try
            {
                IsLoading = true;
                OlsStatusText = _localizationService.GetString("Doe_Analysis_Status_LoadBatch", "正在加载批次列表...");
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
                    ? _localizationService.GetString("Doe_Analysis_Status_NoComplete", "没有已完成实验的批次。请先执行实验后再进行 OLS 分析。")
                    : string.Format(_localizationService.GetString("Doe_Analysis_Status_FindAnalysis", "找到 {0} 个可分析的批次，请选择一个进行 OLS 回归分析。"),items.Count);
            }
            catch (Exception ex) { _logger.LogError(ex, "加载 OLS 批次列表失败"); OlsStatusText = $"加载失败: {ex.Message}"; }
            finally { IsLoading = false; }
        }



        private async Task ExportOlsReportAsync()
        {
            if (OlsResult?.ModelSummary == null)
            {
                _dialogService.ShowError("没有可导出的分析结果", "提示");
                return;
            }

            var defaultName = $"OLS报告_{_selectedResponseName}_{DateTime.Now:yyyyMMdd_HHmmss}.pdf";
            var path = _dialogService.ShowSaveFileDialog(
                "PDF 文件 (*.pdf)|*.pdf|Word 文件 (*.docx)|*.docx",
                ".pdf", defaultName);
            if (string.IsNullOrEmpty(path)) return;

            try
            {
                IsLoading = true;
                OlsStatusText = _localizationService.GetString("Doe_Analysis_Status_InReport", "正在生成报告...");

                if (path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                {
                    // ★ PDF 导出（C# QuestPDF，直接从 ViewModel 数据导出）
                    await Task.Run(() =>
                    {
                        var pdfService = new OlsPdfReportService();
                        pdfService.ExportReport(this, path);
                    });
                }
                else
                {
                    // Word 导出（保留原来的 Python 方式）
                    if (SelectedOlsBatch != null)
                    {
                        var resultJson = await _analysisService.ExportOlsReportAsync(
                            SelectedOlsBatch.BatchId, _selectedResponseName, path,
                            $"{_selectedResponseName} OLS 回归分析报告");
                    }
                }

                OlsStatusText = string.Format(_localizationService.GetString("Doe_Analysis_Status_AlreadyExport", "报告已导出: {0}"),path);
                _dialogService.ShowInfo($"OLS 分析报告已保存至:\n{path}", "报告已生成");
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

        // ══════════════ OLS 选择批次 ══════════════
        private async Task SelectOlsBatchAsync(OlsBatchItem? item)
        {
            if (item == null) return;
            foreach (var b in OlsBatchItems) b.IsSelected = (b.BatchId == item.BatchId);
            SelectedOlsBatch = item;
            var batch = await _repository.GetBatchWithDetailsAsync(item.BatchId);
            if (batch?.Responses?.Count > 0)
            {
                SetResponseNames(batch.Responses.Select(r => r.ResponseName).ToList());
                _selectedResponseName = ResponseNames.FirstOrDefault() ?? "";
                RaisePropertyChanged(nameof(SelectedResponseName));
            }

            // ★ 新增：从 DesignConfigJson 解析 olsModelType
            _currentOlsModelType = batch?.DesignMethod switch
            {
                DOEDesignMethod.PlackettBurman => "linear",           // PB 只拟合主效应
                DOEDesignMethod.FractionalFactorial => "interaction",  // 部分因子: 主效应+2fi
                DOEDesignMethod.FullFactorial => "interaction",        // 全因子: 主效应+所有交互
                DOEDesignMethod.CCD => "quadratic",                    // CCD: 二阶模型
                DOEDesignMethod.BoxBehnken => "quadratic",             // BB: 二阶模型
                _ => "quadratic"                                        // 其他: 默认二阶
            };
            var currentResponse = batch?.Responses?.FirstOrDefault(r => r.ResponseName == _selectedResponseName);
            UpdateModelOrderSelector(batch, currentResponse?.ModelOrder);
            // 如果 DesignConfigJson 里显式指定了 olsModelType, 优先用它
            if (!string.IsNullOrEmpty(batch?.DesignConfigJson))
            {
                try
                {
                    var config = JsonConvert.DeserializeObject<Dictionary<string, object>>(batch.DesignConfigJson);
                    if (config != null && config.TryGetValue("olsModelType", out var mt) && mt != null)
                        _currentOlsModelType = mt.ToString()!;
                }
                catch { }
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
                OlsStatusText = _localizationService.GetString("Doe_Analysis_Status_InOls", "正在进行 OLS 回归分析...");
                StatusMessage = "正在进行 OLS 回归分析...";
                var steps = new[] {
            "Fit OLS model", "ANOVA table", "Pareto effects",
            "Residual diagnostics", "Response surface",
            "Effects normal plots", "Outlier diagnostics",
            "Box-Cox / Tukey"
        };
                InitLoadingSteps(steps);
                int totalSteps = steps.Length;

                // Step 0: Fit OLS
                int orderToUse = ShowModelOrderSelector ? _selectedModelOrder : 0;
                OlsResult = await Task.Run(() =>
                    _analysisService.FitOlsAsync(SelectedOlsBatch.BatchId, _selectedResponseName, _currentOlsModelType, KeepCtPtInRefit, orderToUse));
                if (OlsResult?.ModelSummary != null)
                {
                    OlsStatusText = string.Format(_localizationService.GetString("Doe_Analysis_Status_OlsComplete", "OLS 分析完成: R²={0:F4}"),OlsResult.ModelSummary.RSquared);
                    await AdvanceLoadingStepAsync(0, totalSteps);

                    InestimableWarning = OlsResult.InestimableWarning ?? "";
                    HasInestimableWarning = !string.IsNullOrEmpty(InestimableWarning);

                    var batch = await _repository.GetBatchWithDetailsAsync(SelectedOlsBatch!.BatchId);
                    if (batch != null)
                    {
                        OlsContinuousFactors = batch.Factors
                            .Where(f => !f.IsCategorical)
                            .Select(f => f.FactorName)
                            .ToList();
                        if (OlsContinuousFactors.Count >= 2)
                        {
                            OlsSurfaceFactor1 = OlsContinuousFactors[0];
                            OlsSurfaceFactor2 = OlsContinuousFactors[1];
                        }
                    }

                    UpdateEquationsDisplay(OlsResult);

                    // Step 1+2: ANOVA + Pareto
                    await LoadOlsParetoAsync();
                    await AdvanceLoadingStepAsync(1, totalSteps);
                    await AdvanceLoadingStepAsync(2, totalSteps);

                    // Step 3: 残差四合一
                    await  LoadResidualDiagnosticsAsync();
                    await AdvanceLoadingStepAsync(3, totalSteps);

                    // Step 4: 响应曲面
                    await  LoadOlsSurfaceAsync();
                    await AdvanceLoadingStepAsync(4, totalSteps);

                    // Step 5: 标准化效应正态图 + 半正态图
                    await LoadEffectsNormalPlotsAsync();
                    await AdvanceLoadingStepAsync(5, totalSteps);

                    // Step 6: 异常点
                    await LoadOutlierAnalysisAsync();
                    await AdvanceLoadingStepAsync(6, totalSteps);

                    // Step 7: Tukey + Box-Cox
                    await  LoadTukeyHSDAsync();
                 
                    await AdvanceLoadingStepAsync(7, totalSteps);
                }
                else
                {
                    OlsStatusText = _localizationService.GetString("Doe_Analysis_Status_OlsNoneResult", "OLS 分析返回空结果");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "OLS 分析失败");
                OlsStatusText = string.Format("{0}: {1}",_localizationService.GetString("Doe_Analysis_Status_OlsFail", "OLS 分析失败"),ex.Message);
            }
            finally
            {
                IsLoading = false;
            }
            UpdateCoefficientTableDisplay(OlsResult);
        }
        // ═══ 新增方法: 解析方程展示 ═══

        private void UpdateEquationsDisplay(OLSAnalysisResult result)
        {
            EquationsInfo? eqInfo;
            if (IsCodedEquation)
            {
                eqInfo = result.ModelSummary?.Equations;
            }
            else
            {
                eqInfo = result.UncodedEquations;
            }

            if (eqInfo != null && eqInfo.HasCategorical)
            {
                HasCategoricalEquations = true;
                CategoricalFactorName = eqInfo.CategoricalFactor ?? "";
                CategoryEquations = eqInfo.EquationsByLevel
                    .Select(kv => new CategoryEquationItem
                    {
                        LevelName = $"{CategoricalFactorName} = {kv.Key}",
                        Equation = kv.Value
                    }).ToList();
            }
            else
            {
                HasCategoricalEquations = false;
                CategoryEquations = new();
            }

            // ★ v13: 纯连续因子时也要切换方程文本
            if (IsCodedEquation)
            {
                CurrentEquationText = result.ModelSummary?.Equation ?? "";
            }
            else
            {
                CurrentEquationText = result.UncodedEquation ?? result.ModelSummary?.Equation ?? "";
            }
        }
        /// <summary>
        /// ★ v14 新增: 根据编码/未编码模式切换系数表数据源
        /// </summary>
        private void UpdateCoefficientTableDisplay(OLSAnalysisResult result)
        {
            if (result == null) return;

            // ★ v17: 更新弯曲检验状态
            HasCurvatureTest = result.CurvatureTest?.HasCurvatureTest == true;
            if (HasCurvatureTest)
            {
                var ct = result.CurvatureTest!;
                CurvatureHint = ct.IsSignificant
                    ? $"⚠️ 弯曲显著 (P={ct.CurvatureP:F3})，建议进行 RSM 二阶分析 (CCD/BBD)"
                    : $"✓ 弯曲不显著 (P={ct.CurvatureP:F3})，一阶模型适用";
            }
            else
            {
                CurvatureHint = "";
            }

            if (IsCodedEquation)
            {
                DisplayCoefficients = result.Coefficients?
                    .Select(c => new CoefficientDisplayRow
                    {
                        Term = c.Term,
                        Coefficient = c.Coefficient,
                        StdError = c.StdError,
                        TValue = c.TValue,
                        PValue = c.PValue,
                        VIF = c.VIF,
                        Effect = c.Effect   // ★ v17 新增
                    }).ToList() ?? new();
            }
            else
            {
                DisplayCoefficients = result.UncodedCoefficients?
                    .Select(c => new CoefficientDisplayRow
                    {
                        Term = c.Term,
                        Coefficient = c.Coefficient,
                        StdError = c.StdError,
                        TValue = c.TValue,
                        PValue = c.PValue,
                        VIF = c.VIF,
                        Effect = null  // ★ v17: 未编码模式不显示效应
                    }).ToList() ?? new();
            }
        }

        // ═══ Pareto 图构建（支持点击切换） ═══

        private async Task LoadOlsParetoAsync()
        {
            try
            {
                var json = await _analysisService.GetEffectsParetoAsync(
                    SelectedOlsBatch!.BatchId, _selectedResponseName);
                var wrapper = JsonConvert.DeserializeObject<ParetoResult>(json);
                var data = wrapper?.Effects;
                _paretoAlpha = wrapper?.Alpha ?? 0.05;

                // ★ v17: 根据 measure 切换 Pareto 模式
                var measure = wrapper?.Measure ?? "LogWorth";
                if (measure == "StandardizedEffect")
                {
                    _paretoLogWorthCrit = wrapper?.TCrit ?? wrapper?.LogWorthCrit ?? 4.3;
                    ParetoSubtitle = $"标准化效应的 Pareto 图 (响应为 产率, α = {_paretoAlpha:F2})";
                }
                else
                {
                    _paretoLogWorthCrit = wrapper?.LogWorthCrit ?? 1.301;
                    ParetoSubtitle = "Pareto of LogWorth = −log₁₀(p-value). Click bars to toggle include/exclude.";
                }

                if (data == null || data.Count == 0) return;

                // 保存项数据（用于点击交互）
                _paretoTerms = data.Select(d => new ParetoTermItem
                {
                    TermName = d.Term,
                    AbsT = d.AbsT,
                    PValue = d.PValue,
                    IsSignificant = d.Significant,
                    IsIncluded = true  // 初始全部保留
                }).ToList();

                RebuildParetoPlot();
                UpdateTermsText();
            }
            catch (Exception ex) { _logger.LogError(ex, "OLS Pareto 图加载失败"); }
        }


        private void RebuildParetoPlot()
        {
            var sorted = _paretoTerms.OrderByDescending(t => t.AbsT).ToList();
            int termCount = sorted.Count;
            if (termCount == 0) { OlsParetoPlot = new PlotModel(); return; }

            double tCrit = _paretoLogWorthCrit ?? 1.301;
            double maxT = sorted.Count > 0 ? sorted[0].AbsT : 5;
            double xMax = Math.Max(Math.Max(maxT, tCrit), 1) * 1.15;

            // ★ 最小可见条形宽度 = xMax 的 5%, 确保最小项也能看到颜色
            double minBarValue = xMax * 0.05;

            // ── 动态参数 ──
            double labelFontSize = termCount <= 8 ? 10.5 : (termCount <= 15 ? 9.5 : (termCount <= 20 ? 8.5 : 7.5));
            double barLabelFontSize = termCount <= 10 ? 9 : (termCount <= 20 ? 8 : 7);
            double gapWidth = termCount <= 8 ? 0.35 : (termCount <= 15 ? 0.2 : (termCount <= 20 ? 0.12 : 0.08));

            var model = new PlotModel
            {
                TitleFontSize = 0,
                PlotMargins = new OxyThickness(double.NaN, 4, 10, 28),
                PlotAreaBorderThickness = new OxyThickness(1, 0, 0, 1),
                PlotAreaBorderColor = OxyColor.FromRgb(200, 200, 200),
                Padding = new OxyThickness(2, 0, 0, 0)
            };

            var catAxis = new CategoryAxis
            {
                Position = AxisPosition.Left,
                GapWidth = gapWidth,
                FontSize = labelFontSize,
                TextColor = OxyColor.FromRgb(60, 60, 60),
                TickStyle = TickStyle.None,
                AxislineStyle = LineStyle.None
            };

            for (int i = sorted.Count - 1; i >= 0; i--)
            {
                var item = sorted[i];
                catAxis.Labels.Add(item.IsIncluded ? item.TermName : $"{item.TermName} ✕");
            }

            var valAxis = new LinearAxis
            {
                Position = AxisPosition.Bottom,
                Title = "",
                Minimum = 0,
                AbsoluteMinimum = 0,
                Maximum = xMax,
                FontSize = 9,
                MajorGridlineStyle = LineStyle.Dot,
                MajorGridlineColor = OxyColor.FromRgb(230, 230, 230),
                TickStyle = TickStyle.Outside,
                MajorStep = xMax > 50 ? 10 : (xMax > 20 ? 5 : (xMax > 10 ? 2 : 1)),
                MinorTickSize = 0
            };

            model.Axes.Add(catAxis);
            model.Axes.Add(valAxis);

            var series = new BarSeries
            {
                StrokeThickness = 0.6,
                StrokeColor = OxyColor.FromRgb(180, 180, 180),
                LabelPlacement = LabelPlacement.Outside,
                LabelMargin = 3,
                FontSize = barLabelFontSize,
                // ★ 不用 LabelFormatString, 改用自定义逻辑在下面设置每个 BarItem
            };

            for (int i = sorted.Count - 1; i >= 0; i--)
            {
                var item = sorted[i];
                OxyColor color;
                double barValue;

                if (!item.IsIncluded)
                {
                    // 排除项: 最小宽度 + 灰色
                    color = OxyColor.FromArgb(80, 200, 200, 200);
                    barValue = minBarValue;
                }
                else
                {
                    // ★ 条形显示值: 取 max(真实值, 最小可见宽度)
                    // 这样即使 |t|=0.02 的项也有 5% 宽度的条形可见
                    barValue = Math.Max(item.AbsT, minBarValue);

                    if (item.IsSignificant)
                        color = JmpPalette[0];          // 深蓝 (显著)
                    else
                        color = OxyColor.FromArgb(120, 0x4A, 0x7F, 0xC4);  // 浅蓝 (不显著)
                }

                series.Items.Add(new BarItem { Value = barValue, Color = color });
            }

            // ★ 数值标签: 显示真实 |t| 值 (不是 barValue)
            // 用 LabelFormatString 会显示 barValue (含最小值保护), 不是我们要的
            // 所以改用 TrackerFormatString 或者直接在每个 item 上设 label
            // OxyPlot BarSeries 支持在 Items 里设 CategoryIndex, 但不直接支持自定义 label
            // 最简单: 用 Annotation 添加文字标签

            // 先设一个空的 LabelFormatString (不让 BarSeries 自己画 label)
            series.LabelFormatString = "";
            model.Series.Add(series);

            // ★ 用 TextAnnotation 手动绘制每个条形的数值标签
            for (int i = 0; i < sorted.Count; i++)
            {
                var item = sorted[i];
                if (!item.IsIncluded) continue;  // 排除项不显示数值

                // 在 OxyPlot 的 BarSeries 中, 条形从底部开始编号
                // sorted[0] = 最大项 → 在 catAxis 最上面 → index = termCount - 1
                int barIndex = sorted.Count - 1 - i;
                double displayValue = item.AbsT;
                double barEnd = Math.Max(displayValue, minBarValue);

                var annotation = new OxyPlot.Annotations.TextAnnotation
                {
                    Text = displayValue.ToString("F2"),
                    TextPosition = new DataPoint(barEnd + xMax * 0.01, barIndex),
                    TextHorizontalAlignment = OxyPlot.HorizontalAlignment.Left,
                    TextVerticalAlignment = OxyPlot.VerticalAlignment.Middle,
                    FontSize = barLabelFontSize,
                    TextColor = OxyColor.FromRgb(80, 80, 80),
                    StrokeThickness = 0,  // 无边框
                    Padding = new OxyThickness(0)
                };
                model.Annotations.Add(annotation);
            }

            // ── 红色虚线 (t 临界值) ──
            var critLine = new OxyPlot.Annotations.LineAnnotation
            {
                Type = LineAnnotationType.Vertical,
                X = tCrit,
                Color = OxyColor.FromRgb(198, 40, 40),
                LineStyle = LineStyle.Dash,
                StrokeThickness = 1.8,
                Text = $"  {tCrit:F2}",
                TextColor = OxyColor.FromRgb(198, 40, 40),
                FontSize = 9,
                FontWeight = OxyPlot.FontWeights.Bold,
                TextLinePosition = 0.5,
                TextOrientation = AnnotationTextOrientation.Vertical,
                TextHorizontalAlignment = HorizontalAlignment.Left,
                TextMargin = 3
            };
            model.Annotations.Add(critLine);

            OlsParetoPlot = model;
        }
        /// <summary>
        /// ★ 快速 OLS 预测: 直接用缓存的系数 β 做 ŷ = Σβⱼxⱼ
        /// 不走 Service 层, 不序列化 JSON, 纯数学运算
        /// 用于 Desirability 网格搜索 (需要百万次调用)
        /// </summary>
        private double FastOlsPredict(Dictionary<string, double> codedFactors)
        {
            if (_analysisService == null) return 0;

            // 利用 Service 层缓存的 _fitResult 做快速预测
            // 调用 Service 的轻量预测方法
            return _analysisService.PredictFromCurrentModel(codedFactors);
        }
        public void ToggleParetoTerm(int categoryIndex)
        {
            var sorted = _paretoTerms.OrderByDescending(t => t.AbsT).ToList();
            int reversedIndex = sorted.Count - 1 - categoryIndex;
            if (reversedIndex < 0 || reversedIndex >= sorted.Count) return;

            var term = sorted[reversedIndex];
            term.IsIncluded = !term.IsIncluded;

            var original = _paretoTerms.FirstOrDefault(t => t.TermName == term.TermName);
            if (original != null) original.IsIncluded = term.IsIncluded;

            if (!_paretoTerms.Any(t => t.IsIncluded))
            {
                term.IsIncluded = true;
                if (original != null) original.IsIncluded = true;
                return;
            }

            RebuildParetoPlot();
            UpdateTermsText();

            _ = RefitAndRefreshAllAsync();
        }
        private async Task RefitAndRefreshAllAsync()
        {
            var includedTerms = _paretoTerms
                .Where(t => t.IsIncluded)
                .Select(t => t.TermName)
                .ToList();


            // 检查是否全部保留（恢复完整模型）
            // ★ 如果 KeepCtPtInRefit = false, 即使所有 Pareto 项保留, 也走精简路径
            //    让 FitOlsCustomAsync 决定是否加 Ct Pt
            bool isFullModel = _paretoTerms.All(t => t.IsIncluded);

            try
            {
                IsLoading = true;
                OlsStatusText = isFullModel 
                    ? _localizationService.GetString("Doe_Analysis_Status_InResumeModel", "正在恢复完整模型...") 
                    : string.Format(_localizationService.GetString("Doe_Analysis_Status_InFitModel", "正在拟合精简模型 ({0} 项)..."),includedTerms.Count);

                OLSAnalysisResult? newResult;
                if (isFullModel)
                {
                    // 恢复完整模型 (根据 KeepCtPtInRefit 决定是否含弯曲)
                    if (IsDirectImportMode)
                    {
                        newResult = await Task.Run(() =>
                            _analysisService.FitOlsDirectAsync(
                                _directImportFactorsData,
                                _directImportResponsesData[_selectedResponseName],
                                _selectedResponseName, _directImportFactorTypes, _currentOlsModelType));
                    }
                    else
                    {
                        newResult = await Task.Run(() =>
     _analysisService.FitOlsAsync(
         SelectedOlsBatch!.BatchId, _selectedResponseName, _currentOlsModelType, KeepCtPtInRefit,
         ShowModelOrderSelector ? _selectedModelOrder : 0));
                    }
                }
                else
                {
                    // 精简模型
                    if (IsDirectImportMode)
                    {
                        // 直接导入模式暂时用完整模型（TODO: 支持 FitOlsCustomDirect）
                        newResult = await Task.Run(() =>
                            _analysisService.FitOlsDirectAsync(
                                _directImportFactorsData,
                                _directImportResponsesData[_selectedResponseName],
                                _selectedResponseName, _directImportFactorTypes, _currentOlsModelType));
                    }
                    else
                    {
                        newResult = await _analysisService.FitOlsCustomAsync(
                            SelectedOlsBatch!.BatchId, _selectedResponseName, includedTerms, keepCtPt: KeepCtPtInRefit);
                    }
                }

                if (newResult?.ModelSummary == null)
                {
                    OlsStatusText = _localizationService.GetString("Doe_Analysis_Status_FitFail", "拟合失败");
                    return;
                }

                OlsResult = newResult;
                OlsStatusText = $"R²={newResult.ModelSummary.RSquared:F4}, R²adj={newResult.ModelSummary.RSquaredAdj:F4}";

                // ── 刷新所有面板 ──
                UpdateEquationsDisplay(newResult);
                UpdateCoefficientTableDisplay(newResult);
                RaisePropertyChanged(nameof(OlsResult));

                // Pareto: 从当前模型取新的效应值，但保持排除项的置灰状态
                var excludedTermNames = _paretoTerms
                    .Where(t => !t.IsIncluded)
                    .Select(t => t.TermName)
                    .ToHashSet();

                var paretoJson = await Task.Run(() =>
                    _analysisService.GetEffectsParetoFromCurrentModel());
                var wrapper = JsonConvert.DeserializeObject<ParetoResult>(paretoJson);
                if (wrapper?.Effects != null)
                {
                    // 更新保留项的数值
                    foreach (var effect in wrapper.Effects)
                    {
                        var existing = _paretoTerms.FirstOrDefault(t => t.TermName == effect.Term);
                        if (existing != null)
                        {
                            existing.AbsT = effect.AbsT;
                            existing.PValue = effect.PValue;
                            existing.IsSignificant = effect.Significant;
                        }
                    }

                    // 排除项保持置灰，数值归零
                    foreach (var t in _paretoTerms.Where(t => excludedTermNames.Contains(t.TermName)))
                    {
                        t.AbsT = 0;
                        t.PValue = 1.0;
                        t.IsSignificant = false;
                    }

                    if (wrapper.Measure == "StandardizedEffect")
                    {
                        _paretoLogWorthCrit = wrapper.TCrit ?? wrapper.LogWorthCrit ?? 4.3;
                        ParetoSubtitle = $"标准化效应的 Pareto 图 (响应为 {_selectedResponseName}, α = {_paretoAlpha:F2})";
                    }
                }
                RebuildParetoPlot();
                UpdateTermsText();

                // 残差四合一
                var residJson = await Task.Run(() =>
                    _analysisService.GetResidualDiagnosticsFromCurrentModel());
                var diag = JsonConvert.DeserializeObject<ResidualDiagnostics>(residJson);
                if (diag != null) BuildResidualPlotsFromData(diag);

                // 效应正态图 + 半正态图
                var effectsJson = await Task.Run(() =>
                    _analysisService.GetEffectsParetoFromCurrentModel());
                var effectsWrapper = JsonConvert.DeserializeObject<ParetoResult>(effectsJson);
                if (effectsWrapper?.Effects != null && effectsWrapper.Effects.Count > 0)
                {
                    double alpha = effectsWrapper.Alpha ?? 0.05;
                    double tc = effectsWrapper.TCrit ?? effectsWrapper.LogWorthCrit ?? 2.0;
                    BuildEffectsNormalPlot(effectsWrapper.Effects, alpha, tc);
                    BuildEffectsHalfNormalPlot(effectsWrapper.Effects, alpha, tc);
                }

                // 响应曲面（用当前模型的 Surface 数据，不重新 load_data）
                if (!string.IsNullOrEmpty(OlsSurfaceFactor1) && !string.IsNullOrEmpty(OlsSurfaceFactor2)
                    && OlsSurfaceFactor1 != OlsSurfaceFactor2)
                {
                    try
                    {
                        var surfJson = await Task.Run(() =>
                            _analysisService.GetOlsContourDataDirectAsync(
                                OlsSurfaceFactor1, OlsSurfaceFactor2, ""));
                        var surfData = JsonConvert.DeserializeObject<SurfaceData>(surfJson);
                        if (surfData?.Z != null)
                        {
                            BuildContourPlot(surfData);
                            OlsSurface3DData = new Surface3DData
                            {
                                X = surfData.X.ToArray(),
                                Y = surfData.Y.ToArray(),
                                Z = surfData.Z.Select(row => row.ToArray()).ToArray(),
                                XLabel = OlsSurfaceFactor1,
                                YLabel = OlsSurfaceFactor2
                            };
                        }
                    }
                    catch { /* 响应曲面刷新失败不阻塞 */ }
                }

            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "重拟合失败");
                OlsStatusText = string.Format(_localizationService.GetString("Doe_Analysis_Status_RefitFail", "重拟合失败: {0}"),ex.Message);
            }
            finally
            {
                IsLoading = false;
            }
        }
        private void UpdateTermsText()
        {
            var included = _paretoTerms.Where(t => t.IsIncluded).Select(t => t.TermName);
            var excluded = _paretoTerms.Where(t => !t.IsIncluded).Select(t => t.TermName);
            IncludedTermsText = string.Join(", ", included);
            ExcludedTermsText = excluded.Any() ? string.Join(", ", excluded) : "无";
        }





        // ═══ 新增方法: 重新拟合精简模型 ═══

        private async Task RefitReducedModelAsync()
        {
            // 已经通过 Pareto 点击实时处理了，这里只需做同样的事
            await RefitAndRefreshAllAsync();
        }
        /// <summary>★ v17: 从已有的残差数据构建四合一图（精简模型刷新用）</summary>
        private void BuildResidualPlotsFromData(ResidualDiagnostics diag)
        {
            // ══════════════════════════════════════
            // (1) 残差的正态概率图
            //     Minitab: X=残差(原始), Y=百分位(0~100)
            //     参考线=正态CDF曲线
            // ══════════════════════════════════════
            var npModel = new PlotModel();
            npModel.Axes.Add(new LinearAxis { Position = AxisPosition.Bottom, Title = "残差" });
            npModel.Axes.Add(new LinearAxis { Position = AxisPosition.Left, Title = "百分位", Minimum = 0, Maximum = 100 });
            var npScatter = JmpScatter(0, 3);
            for (int i = 0; i < diag.NormalProbability.OrderedResiduals.Count && i < diag.NormalProbability.Percent.Count; i++)
                npScatter.Points.Add(new ScatterPoint(
                    diag.NormalProbability.OrderedResiduals[i],
                    diag.NormalProbability.Percent[i]));
            npModel.Series.Add(npScatter);

            // 正态 CDF 参考线
            if (diag.NormalProbability.OrderedResiduals.Count > 1)
            {
                var residuals = diag.NormalProbability.OrderedResiduals;
                double mean = residuals.Average();
                double std = Math.Sqrt(residuals.Select(r => (r - mean) * (r - mean)).Average());
                if (std < 1e-10) std = 1;

                double minR = residuals.Min();
                double maxR = residuals.Max();
                double range = maxR - minR;
                var refLine = JmpRefLine();
                int steps = 50;
                for (int i = 0; i <= steps; i++)
                {
                    double r = minR - range * 0.1 + (range * 1.2) * i / steps;
                    double p = 0.5 * (1 + Erf((r - mean) / (std * Math.Sqrt(2))));
                    refLine.Points.Add(new DataPoint(r, p * 100));
                }
                npModel.Series.Add(refLine);
            }
            ApplyJmpStyle(npModel);
            NormalProbPlot = npModel;

            // ══════════════════════════════════════
            // (2) 残差与拟合值
            //     Minitab: X=拟合值, Y=残差(原始)
            // ══════════════════════════════════════
            var rvfModel = new PlotModel();
            rvfModel.Axes.Add(new LinearAxis { Position = AxisPosition.Bottom, Title = "拟合值" });
            rvfModel.Axes.Add(new LinearAxis { Position = AxisPosition.Left, Title = "残差" });
            var rvfScatter = JmpScatter(1, 3);
            for (int i = 0; i < diag.ResidualsVsFitted.Fitted.Count; i++)
                rvfScatter.Points.Add(new ScatterPoint(
                    diag.ResidualsVsFitted.Fitted[i],
                    diag.ResidualsVsFitted.Residuals[i]));
            rvfModel.Series.Add(rvfScatter);
            if (diag.ResidualsVsFitted.Fitted.Count > 0)
            {
                var zeroLine = JmpRefLine();
                zeroLine.Points.Add(new DataPoint(diag.ResidualsVsFitted.Fitted.Min(), 0));
                zeroLine.Points.Add(new DataPoint(diag.ResidualsVsFitted.Fitted.Max(), 0));
                rvfModel.Series.Add(zeroLine);
            }
            ApplyJmpStyle(rvfModel);
            ResidVsFittedPlot = rvfModel;

            // ══════════════════════════════════════
            // (3) 残差直方图
            //     Minitab: X=残差(原始), Y=频率
            // ══════════════════════════════════════
            var histModel = new PlotModel();
            histModel.Axes.Add(new LinearAxis { Position = AxisPosition.Bottom, Title = "残差" });
            histModel.Axes.Add(new LinearAxis { Position = AxisPosition.Left, Title = "频率", Minimum = 0 });
            var histSeries = new RectangleBarSeries { StrokeThickness = 0.5, StrokeColor = OxyColor.FromRgb(200, 200, 200) };
            for (int i = 0; i < diag.ResidualsHistogram.Frequencies.Count; i++)
            {
                histSeries.Items.Add(new RectangleBarItem
                {
                    X0 = diag.ResidualsHistogram.BinEdges[i],
                    X1 = diag.ResidualsHistogram.BinEdges[i + 1],
                    Y0 = 0,
                    Y1 = diag.ResidualsHistogram.Frequencies[i],
                    Color = OxyColor.FromArgb(180, 0x4A, 0x7F, 0xC4)
                });
            }
            histModel.Series.Add(histSeries);
            ApplyJmpStyle(histModel);
            ResidHistogramPlot = histModel;

            // ══════════════════════════════════════
            // (4) 残差与观测值顺序
            //     Minitab: X=观测值顺序, Y=残差(原始)
            // ══════════════════════════════════════
            var rvoModel = new PlotModel();
            rvoModel.Axes.Add(new LinearAxis { Position = AxisPosition.Bottom, Title = "观测值顺序" });
            rvoModel.Axes.Add(new LinearAxis { Position = AxisPosition.Left, Title = "残差" });
            var rvoLine = new LineSeries
            {
                Color = JmpPalette[0],
                StrokeThickness = 1.2,
                MarkerType = MarkerType.Circle,
                MarkerSize = 2.5,
                MarkerFill = JmpPalette[0]
            };
            for (int i = 0; i < diag.ResidualsVsOrder.Order.Count; i++)
                rvoLine.Points.Add(new DataPoint(
                    diag.ResidualsVsOrder.Order[i],
                    diag.ResidualsVsOrder.Residuals[i]));
            rvoModel.Series.Add(rvoLine);
            if (diag.ResidualsVsOrder.Order.Count > 0)
            {
                var zeroLine = JmpRefLine();
                zeroLine.Points.Add(new DataPoint(1, 0));
                zeroLine.Points.Add(new DataPoint(diag.ResidualsVsOrder.Order.Max(), 0));
                rvoModel.Series.Add(zeroLine);
            }
            ApplyJmpStyle(rvoModel);
            ResidVsOrderPlot = rvoModel;
        }
        private async Task RestoreFullModelAsync()
        {
            foreach (var t in _paretoTerms) t.IsIncluded = true;
            KeepCtPtInRefit = true;  // ★ 恢复完整模型时自动勾选"保留弯曲"
            RebuildParetoPlot();
            UpdateTermsText();
            await RefitAndRefreshAllAsync();
        }
        // ═══ ★ v6: 交互式预测刻画器（JMP 风格拖动联动） ═══
        private static void DetachPlotModels(IEnumerable<PlotModel> models)
        {
            var field = typeof(PlotModel).GetField("plotViewReference",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (field == null) return;

            foreach (var pm in models)
            {
                try { field.SetValue(pm, null); }
                catch { }
            }
        }
        public void ClearProfilerPlots()
        {
            ProfilerPlots = new List<PlotModel>();
            ProfilerRows = new List<ProfilerRowViewModel>();
            DesirabilityPlots = new List<PlotModel>();
            DesirabilityRows = new List<ProfilerRowViewModel>();
            _multiProfilerData.Clear();
            _multiResponsePredictor = null;
            _lastProfilerData = null;
            IsProfilerLoaded = false;

            // 取消事件的订阅
            _eventAggregator.GetEvent<LanguageChangedEvent>()?.Unsubscribe(OnLanguageChangedEvent);
        }
        private async Task LoadPredictionProfilerAsync()
        {
            if (ResponseNames.Count == 0) return;
            if (!IsDirectImportMode && SelectedOlsBatch == null) return;

            try
            {
                IsLoading = true;
                // ★ 修复: 用反射断开旧 PlotModel 的 PlotView 引用
                var allOldPlots = new List<PlotModel>();
                allOldPlots.AddRange(ProfilerPlots);
                allOldPlots.AddRange(DesirabilityPlots);
                foreach (var row in ProfilerRows)
                    allOldPlots.AddRange(row.Plots);
                foreach (var row in DesirabilityRows)
                    allOldPlots.AddRange(row.Plots);
                DetachPlotModels(allOldPlots);

                ProfilerPlots = new List<PlotModel>();
                ProfilerRows = new List<ProfilerRowViewModel>();
                DesirabilityPlots = new List<PlotModel>();
                DesirabilityRows = new List<ProfilerRowViewModel>();
                _multiResponsePredictor = null;
                IsProfilerLoaded = false;

                await System.Windows.Application.Current.Dispatcher.InvokeAsync(
                    () => { }, System.Windows.Threading.DispatcherPriority.Render);
                _isMultiResponseProfiler = !string.IsNullOrEmpty(_selectedResponseName)
                    && _selectedResponseName.Contains(" + ");
                _profilerResponseOrder = _isMultiResponseProfiler
                    ? GetRealResponseNames()
                    : new List<string> { _selectedResponseName };
                if (!_isMultiResponseProfiler)
                {
                    // ═══ 单响应: 保持原有逻辑完全不变 ═══
                    OlsStatusText = _localizationService.GetString("Doe_Analysis_Status_InGenerateCarver", "正在生成预测刻画器...");
                    string json;
                    if (IsDirectImportMode)
                        json = await Task.Run(() =>
                            _analysisService.GetPredictionProfilerDirectAsync(_selectedResponseName));
                    else
                        json = await _analysisService.GetPredictionProfilerAsync(
                            SelectedOlsBatch!.BatchId, _selectedResponseName);

                    var data = JsonConvert.DeserializeObject<ProfilerResult>(json);
                    if (data?.Factors == null) return;

                    _lastProfilerData = data;
                    _profilerFactorOrder = data.Factors.Keys.ToList();
                    _profilerCurrentValues.Clear();
                    foreach (var kv in data.Factors)
                        _profilerCurrentValues[kv.Key] = kv.Value.CurrentValue ?? 0;

                    _profilerYMin = double.MaxValue; _profilerYMax = double.MinValue;
                    foreach (var kv in data.Factors)
                    {
                        if (kv.Value.Y.Count > 0) { _profilerYMin = Math.Min(_profilerYMin, kv.Value.Y.Min()); _profilerYMax = Math.Max(_profilerYMax, kv.Value.Y.Max()); }
                        if (kv.Value.YLower?.Count > 0) _profilerYMin = Math.Min(_profilerYMin, kv.Value.YLower.Min());
                        if (kv.Value.YUpper?.Count > 0) _profilerYMax = Math.Max(_profilerYMax, kv.Value.YUpper.Max());
                    }
                    double yPad = (_profilerYMax - _profilerYMin) * 0.15;
                    _profilerYMin -= yPad; _profilerYMax += yPad;

                    BuildProfilerPlots(data);
                    // 单响应: ProfilerRows 只有一行
                    ProfilerRows = new List<ProfilerRowViewModel>
            {
                new ProfilerRowViewModel
                {
                    ResponseName = data.ResponseName,
                    ResponseLabel = $"{data.ResponseName}    {data.CurrentPredicted:F4}",
                    Plots = ProfilerPlots.ToList(),
                    YMin = _profilerYMin, YMax = _profilerYMax,
                    Data = data
                }
            };
                    IsProfilerLoaded = true;
                    OlsStatusText = string.Format(_localizationService.GetString("Doe_Analysis_Status_CarverAlready", "预测刻画器已生成 ({0} 个因子)"),data.Factors.Count);
                }
                else
                {
                    // ═══ 多响应: 为每个响应独立加载 ═══
                    OlsStatusText = _localizationService.GetString("Doe_Analysis_Status_MultCarver", "正在生成多响应预测刻画器...");
                    _multiProfilerData.Clear();
                    _multiProfilerYRanges.Clear();

                    string originalResponse = _selectedResponseName;

                    foreach (var respName in _profilerResponseOrder)
                    {
                        OlsStatusText = string.Format(_localizationService.GetString("Doe_Analysis_Status_LoadCarver", "正在加载 {0} 的刻画器数据..."),respName);

                        // 切换到该响应的 OLS 模型
                        await FitForResponseAsync(respName);

                        // 获取 profiler 数据
                        string json;
                        var fixedJson = _profilerCurrentValues.Count > 0
                            ? JsonConvert.SerializeObject(_profilerCurrentValues) : "";

                        if (IsDirectImportMode)
                            json = await Task.Run(() =>
                                _analysisService.GetPredictionProfilerDirectAsync(respName, 50, fixedJson));
                        else
                            json = await _analysisService.GetPredictionProfilerAsync(
                                SelectedOlsBatch!.BatchId, respName, 50,
                                fixedJson != "" ? JsonConvert.DeserializeObject<Dictionary<string, object>>(fixedJson) : null);

                        var data = JsonConvert.DeserializeObject<ProfilerResult>(json);
                        if (data?.Factors != null)
                        {
                            _multiProfilerData[respName] = data;

                            // 初始化因子当前值 (只在第一个响应时做, 所有响应共享)
                            if (_profilerCurrentValues.Count == 0 || respName == _profilerResponseOrder[0])
                            {
                                _profilerFactorOrder = data.Factors.Keys.ToList();
                                foreach (var kv in data.Factors)
                                {
                                    if (!_profilerCurrentValues.ContainsKey(kv.Key))
                                        _profilerCurrentValues[kv.Key] = kv.Value.CurrentValue ?? 0;
                                }
                            }
                        }
                    }

                    // 恢复当前选中响应的模型
                    await FitForResponseAsync(originalResponse);

                    // _lastProfilerData 指向第一个响应 (用于兼容旧代码)
                    if (_multiProfilerData.Count > 0)
                        _lastProfilerData = _multiProfilerData.Values.First();

                    // 计算每个响应的 Y 范围
                    foreach (var kv in _multiProfilerData)
                    {
                        double yMin = double.MaxValue, yMax = double.MinValue;
                        foreach (var fd in kv.Value.Factors.Values)
                        {
                            if (fd.Y.Count > 0) { yMin = Math.Min(yMin, fd.Y.Min()); yMax = Math.Max(yMax, fd.Y.Max()); }
                            if (fd.YLower?.Count > 0) yMin = Math.Min(yMin, fd.YLower.Min());
                            if (fd.YUpper?.Count > 0) yMax = Math.Max(yMax, fd.YUpper.Max());
                        }
                        double pad = (yMax - yMin) * 0.15;
                        _multiProfilerYRanges[kv.Key] = (yMin - pad, yMax + pad);
                    }

                    // 同时构建 MultiResponseOlsPredictor 快照 (拖拽时用)
                    await BuildMultiResponsePredictorForProfiler();

                    // 构建多行 PlotModel
                    BuildMultiResponseProfilerPlots();

                    IsProfilerLoaded = true;
                    OlsStatusText = string.Format(
                        _localizationService.GetString("Doe_Analysis_Status_MultCarverAlready", "多响应刻画器已生成 ({0} 个响应, {1} 个因子)"),
                        _profilerResponseOrder.Count, _profilerFactorOrder.Count);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "预测刻画器加载失败");
                OlsStatusText = string.Format(_localizationService.GetString("Doe_Analysis_Status_PredCarver", "预测刻画器失败: {0}"),ex.Message);
            }
            finally { IsLoading = false; }
        }
        // ═══════════════════════════════════════════════════════
        // [新增] FitForResponseAsync — 切换 OLS 模型到指定响应
        // ═══════════════════════════════════════════════════════

        /// <summary>
        /// 切换 _analysisService 到指定响应的 OLS 模型
        /// </summary>
        private async Task FitForResponseAsync(string responseName)
        {
            if (IsDirectImportMode)
            {
                if (_directImportResponsesData.TryGetValue(responseName, out var data))
                    await Task.Run(() => _analysisService.FitOlsDirectAsync(
                        _directImportFactorsData, data, responseName,
                        _directImportFactorTypes, _currentOlsModelType));
            }
            else if (SelectedOlsBatch != null)
            {
                int orderForResp = 0;
                if (ShowModelOrderSelector)
                {
                    var batch = await _repository.GetBatchWithDetailsAsync(SelectedOlsBatch.BatchId);
                    var resp = batch?.Responses?.FirstOrDefault(r => r.ResponseName == responseName);
                    orderForResp = resp?.ModelOrder ?? _selectedModelOrder;
                }
                await _analysisService.FitOlsAsync(
                    SelectedOlsBatch.BatchId, responseName, _currentOlsModelType, true, orderForResp);
            }
        }


        // ═══════════════════════════════════════════════════════
        // [新增] BuildMultiResponsePredictorForProfiler
        // ═══════════════════════════════════════════════════════

        /// <summary>
        /// 为刻画器拖拽构建多响应预测器快照
        /// 每个响应用独立的 β 系数，拖拽时不需要切换 _analysisService
        /// </summary>
        private async Task BuildMultiResponsePredictorForProfiler()
        {
            var predictor = new MultiResponseOlsPredictor();
            string originalResponse = _selectedResponseName;

            foreach (var respName in _profilerResponseOrder)
            {
                try
                {
                    await FitForResponseAsync(respName);

                    var factors = InferFactorDefsFromProfiler();
                    if (factors.Count == 0) continue;

                    var result = await GetCurrentOlsResultAsync(respName);
                    if (result?.Coefficients == null || result.Coefficients.Count == 0) continue;

                    // ★ 修复: 用 RebuildFitResultFromAnalysisResult 重建完整 fitResult
                    //   之前只存了 Coefficients/TermNames/P，缺少 XtXInverse/Sigma/TCrit
                    //   导致多响应模式下无法计算 CI
                    var snapshot = RebuildFitResultFromAnalysisResult(result, respName);
                    if (snapshot.fitResult != null && snapshot.encoder != null)
                    {
                        predictor.AddResponse(respName, snapshot.fitResult,
                            snapshot.encoder, snapshot.factors);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "多响应预测器: 响应 '{Name}' 构建失败", respName);
                }
            }

            await FitForResponseAsync(originalResponse);
            _multiResponsePredictor = predictor;
        }
        /// <summary>
        /// 获取当前 _analysisService 已拟合模型的 OLS 结果
        /// </summary>
        private async Task<OLSAnalysisResult?> GetCurrentOlsResultAsync(string responseName)
        {
            try
            {
                if (IsDirectImportMode)
                {
                    if (!_directImportResponsesData.TryGetValue(responseName, out var data))
                        return null;
                    return await Task.Run(() => _analysisService.FitOlsDirectAsync(
                        _directImportFactorsData, data, responseName,
                        _directImportFactorTypes, _currentOlsModelType));
                }
                else if (SelectedOlsBatch != null)
                {
                    int orderForResp = 0;
                    if (ShowModelOrderSelector)
                    {
                        var batch = await _repository.GetBatchWithDetailsAsync(SelectedOlsBatch.BatchId);
                        var resp = batch?.Responses?.FirstOrDefault(r => r.ResponseName == responseName);
                        orderForResp = resp?.ModelOrder ?? _selectedModelOrder;
                    }
                    return await _analysisService.FitOlsAsync(
                        SelectedOlsBatch.BatchId, responseName, _currentOlsModelType, true, orderForResp);
                }
            }
            catch { }
            return null;
        }


        // ═══════════════════════════════════════════════════════
        // [新增] BuildMultiResponseProfilerPlots — 构建多行 PlotModel
        // ═══════════════════════════════════════════════════════

        /// <summary>
        /// 构建多响应刻画器的多行 PlotModel
        /// 每个响应一行，每行有 N 个因子图
        /// </summary>
        private void BuildMultiResponseProfilerPlots()
        {
            var rows = new List<ProfilerRowViewModel>();
            int nFactors = _profilerFactorOrder.Count;

            // 同时更新 ProfilerPlots (向后兼容: code-behind 拖拽用索引查找)
            var allPlots = new List<PlotModel>();

            foreach (var respName in _profilerResponseOrder)
            {
                if (!_multiProfilerData.TryGetValue(respName, out var data)) continue;
                if (!_multiProfilerYRanges.TryGetValue(respName, out var yRange)) continue;

                var row = new ProfilerRowViewModel
                {
                    ResponseName = respName,
                    ResponseLabel = $"{respName}    {data.CurrentPredicted:F4}",
                    YMin = yRange.yMin,
                    YMax = yRange.yMax,
                    Data = data,
                    Plots = new List<PlotModel>()
                };

                for (int fi = 0; fi < nFactors; fi++)
                {
                    var fname = _profilerFactorOrder[fi];
                    if (!data.Factors.TryGetValue(fname, out var fdata)) continue;

                    var pm = new PlotModel
                    {
                        Title = fname,
                        PlotMargins = new OxyThickness(50, 12, 10, 30)
                    };

                    // 只在每行第一个图显示 Y 轴标题 (响应名)
                    string yTitle = fi == 0 ? respName : "";

                    RebuildSinglePlot(pm, fname, fdata, data.CurrentPredicted,
                        yRange.yMin, yRange.yMax, yTitle);

                    row.Plots.Add(pm);
                    allPlots.Add(pm);
                }

                rows.Add(row);
            }

            ProfilerRows = rows;
            ProfilerPlots = allPlots;

            // 更新预测值文本
            UpdateMultiProfilerPredictedText();
        }


        // ═══════════════════════════════════════════════════════
        // [新增] UpdateMultiProfilerPredictedText
        // ═══════════════════════════════════════════════════════

        /// <summary>
        /// 更新多响应预测值文本 (每个响应一行)
        /// </summary>
        private void UpdateMultiProfilerPredictedText()
        {
            if (!_isMultiResponseProfiler)
            {
                if (_lastProfilerData != null)
                {
                    string ciText = GetCurrentCIText(_lastProfilerData);
                    ProfilerCurrentPredicted = $"{_lastProfilerData.ResponseName}    {_lastProfilerData.CurrentPredicted:F4}{ciText}";
                }
                return;
            }

            var lines = new List<string>();
            foreach (var respName in _profilerResponseOrder)
            {
                if (_multiProfilerData.TryGetValue(respName, out var data))
                {
                    string ciText = GetCurrentCIText(data);
                    lines.Add($"{respName}    {data.CurrentPredicted:F4}{ciText}");
                }
            }
            ProfilerCurrentPredicted = string.Join("\n", lines);
        }
        /// <summary>
        /// ★ v6: 从 code-behind 调用 — 用户拖动红色竖线后更新所有图
        /// ★ 关键: 原地更新每个 PlotModel 的曲线数据，不替换列表（避免 UI 闪烁）
        /// </summary>
        /// <summary>
        /// ★ v9: 更新因子值
        /// </summary>
        public async Task UpdateProfilerValueAsync(string factorName, object newValue)
        {
            if (!IsProfilerLoaded) return;

            _profilerCurrentValues[factorName] = newValue;

            // 直接用后端计算（同步路径，C# 内存调用）
            UpdateProfilerLocal(factorName, newValue);
        }

        /// <summary>
        /// ★ v9: 拖动过程中用 C# 预测器实时更新所有图（完全不调 Python）
        /// </summary>
        public void UpdateProfilerLocal(string draggedFactor, object newValue)
        {
            _profilerCurrentValues[draggedFactor] = newValue;

            if (!_isMultiResponseProfiler)
            {
                // ═══ 单响应: 原有逻辑完全不变 ═══
                if (_lastProfilerData?.Factors == null) return;

                var fixedJson = JsonConvert.SerializeObject(_profilerCurrentValues);
                string json;
                try { json = _analysisService.GetPredictionProfilerFromCurrentModel(50, fixedJson); }
                catch { return; }

                var data = JsonConvert.DeserializeObject<ProfilerResult>(json);
                if (data?.Factors == null) return;

                _lastProfilerData = data;
                foreach (var kv in data.Factors)
                    _profilerCurrentValues[kv.Key] = kv.Value.CurrentValue ?? _profilerCurrentValues.GetValueOrDefault(kv.Key, 0);

                if (ProfilerPlots != null)
                {
                    for (int i = 0; i < _profilerFactorOrder.Count && i < ProfilerPlots.Count; i++)
                    {
                        var fname = _profilerFactorOrder[i];
                        if (!data.Factors.TryGetValue(fname, out var fdata)) continue;
                        RebuildSinglePlot(ProfilerPlots[i], fname, fdata, data.CurrentPredicted,
                            _profilerYMin, _profilerYMax, i == 0 ? data.ResponseName : "");
                    }
                }

                string ciText = GetCurrentCIText(data);
                ProfilerCurrentPredicted = $"{data.ResponseName}    {data.CurrentPredicted:F4}{ciText}";

                if (IsDesirabilityVisible) RefreshDesirabilityPlotsInPlace();
                return;
            }

            // ═══ 多响应: 用 MultiResponseOlsPredictor 快速预测 ═══
            if (_multiResponsePredictor == null || ProfilerRows == null) return;

            // 构建当前因子值
            var contValues = new Dictionary<string, double>();
            var catValues = new Dictionary<string, string>();
            foreach (var fname in _profilerFactorOrder)
            {
                if (IsProfilerFactorCategorical(fname))
                {
                    var cv = _profilerCurrentValues.TryGetValue(fname, out var v) ? v?.ToString() : "";
                    catValues[fname] = cv ?? "";
                }
                else
                {
                    var cv = _profilerCurrentValues.TryGetValue(fname, out var v) ? Convert.ToDouble(v) : 0;
                    contValues[fname] = cv;
                }
            }

            // 对每个响应行更新
            foreach (var row in ProfilerRows)
            {
                if (!_multiProfilerData.TryGetValue(row.ResponseName, out var cachedData)) continue;

                // 用快照预测当前点的 ŷ
                double currentPred = _multiResponsePredictor.Predict(row.ResponseName, contValues, catValues);

                // 用快照为每个因子生成剖面曲线 (50 个网格点)
                for (int fi = 0; fi < _profilerFactorOrder.Count && fi < row.Plots.Count; fi++)
                {
                    var fname = _profilerFactorOrder[fi];
                    if (!cachedData.Factors.TryGetValue(fname, out var fdata)) continue;

                    // 重新计算该因子的剖面曲线 + CI
                    var newY = new List<double>();
                    var newYL = new List<double>();
                    var newYU = new List<double>();
                    if (fdata.IsCategorical)
                    {
                        var levels = fdata.Levels ?? fdata.X_Labels;
                        foreach (var lv in levels)
                        {
                            var tempCat = new Dictionary<string, string>(catValues) { [fname] = lv };
                            var (yVal, yLo, yHi) = _multiResponsePredictor.PredictWithCI(
                                row.ResponseName, contValues, tempCat);
                            newY.Add(yVal);
                            newYL.Add(yLo);
                            newYU.Add(yHi);
                        }
                    }
                    else
                    {
                        foreach (var xVal in fdata.X)
                        {
                            var tempCont = new Dictionary<string, double>(contValues) { [fname] = xVal };
                            var (yVal, yLo, yHi) = _multiResponsePredictor.PredictWithCI(
                                row.ResponseName, tempCont, catValues);
                            newY.Add(yVal);
                            newYL.Add(yLo);
                            newYU.Add(yHi);
                        }
                    }

                    // 更新 fdata 的 Y / YLower / YUpper (就地修改缓存)
                    for (int j = 0; j < Math.Min(newY.Count, fdata.Y.Count); j++)
                        fdata.Y[j] = newY[j];
                    for (int j = 0; j < Math.Min(newYL.Count, fdata.YLower.Count); j++)
                        fdata.YLower[j] = newYL[j];
                    for (int j = 0; j < Math.Min(newYU.Count, fdata.YUpper.Count); j++)
                        fdata.YUpper[j] = newYU[j];

                    // ★ 更新当前值（红线位置依赖此值）
                    if (_profilerCurrentValues.TryGetValue(fname, out var curV))
                        fdata.CurrentValue = curV;

                    // 重建该图
                    string yTitle = fi == 0 ? row.ResponseName : "";
                    RebuildSinglePlot(row.Plots[fi], fname, fdata, currentPred,
                        row.YMin, row.YMax, yTitle);
                }

                // ★ 更新行标签（从缓存的 YLower/YUpper 插值取 CI，和单响应一致）
                string rowCiText = "";
                foreach (var kv in cachedData.Factors)
                {
                    var fd = kv.Value;
                    if (fd.YLower == null || fd.YUpper == null || fd.YLower.Count == 0) continue;
                    double? lo = null, hi = null;
                    if (fd.IsCategorical)
                    {
                        var curVal = _profilerCurrentValues.TryGetValue(kv.Key, out var cv) ? cv?.ToString() : "";
                        int idx = fd.X_Labels.IndexOf(curVal ?? "");
                        if (idx >= 0 && idx < fd.YLower.Count) { lo = fd.YLower[idx]; hi = fd.YUpper[idx]; }
                    }
                    else
                    {
                        double curX = _profilerCurrentValues.TryGetValue(kv.Key, out var cv) ? Convert.ToDouble(cv) : 0;
                        var xs = fd.X;
                        if (xs.Count >= 2)
                        {
                            if (curX <= xs[0]) { lo = fd.YLower[0]; hi = fd.YUpper[0]; }
                            else if (curX >= xs[xs.Count - 1]) { lo = fd.YLower[^1]; hi = fd.YUpper[^1]; }
                            else
                            {
                                for (int k = 0; k < xs.Count - 1; k++)
                                {
                                    if (curX >= xs[k] && curX <= xs[k + 1])
                                    {
                                        double t = (curX - xs[k]) / (xs[k + 1] - xs[k]);
                                        lo = fd.YLower[k] + t * (fd.YLower[k + 1] - fd.YLower[k]);
                                        hi = fd.YUpper[k] + t * (fd.YUpper[k + 1] - fd.YUpper[k]);
                                        break;
                                    }
                                }
                            }
                        }
                    }
                    if (lo.HasValue && hi.HasValue)
                    {
                        rowCiText = $"  95%CI [{lo.Value:F4}, {hi.Value:F4}]";
                        break;
                    }
                }
                row.ResponseLabel = $"{row.ResponseName}    {currentPred:F4}{rowCiText}";
            }

            // 更新预测值文本
            var lines = new List<string>();
            foreach (var row in ProfilerRows)
                lines.Add(row.ResponseLabel);
            ProfilerCurrentPredicted = string.Join("\n", lines);

            // 刷新意愿行
            if (IsDesirabilityVisible)
                RefreshMultiResponseDesirabilityInPlace();
        }


        // ═══════════════════════════════════════════════════════
        // [新增] RefreshMultiResponseDesirabilityInPlace
        // ═══════════════════════════════════════════════════════

        /// <summary>
        /// ★ 多响应意愿行的就地刷新 (拖拽时调用)
        /// 重建所有意愿行的图表数据
        /// </summary>
        private void RefreshMultiResponseDesirabilityInPlace()
        {
            if (_currentDesirabilityConfigs == null || _currentDesirabilityConfigs.Count == 0) return;
            if (_multiResponsePredictor == null) return;
            if (DesirabilityRows == null || DesirabilityRows.Count == 0) return;

            var contValues = new Dictionary<string, double>();
            var catValues = new Dictionary<string, string>();
            foreach (var fname in _profilerFactorOrder)
            {
                if (IsProfilerFactorCategorical(fname))
                    catValues[fname] = _profilerCurrentValues.TryGetValue(fname, out var v) ? v?.ToString() ?? "" : "";
                else
                    contValues[fname] = _profilerCurrentValues.TryGetValue(fname, out var v) ? Convert.ToDouble(v) : 0;
            }

            // ★ 就地更新每个意愿行的图，不重建 DesirabilityRows/DesirabilityPlots
            double compositeD = ComputeCurrentCompositeD(contValues, catValues);
            DesirabilityCompositeD = compositeD;

            for (int r = 0; r < DesirabilityRows.Count; r++)
            {
                var row = DesirabilityRows[r];
                bool isCompositeRow = r >= _currentDesirabilityConfigs.Count;
                var cfg = isCompositeRow ? null : _currentDesirabilityConfigs[r];

                double rowD = isCompositeRow ? compositeD
                    : ComputeDesirabilityForConfig(
                        _multiResponsePredictor.Predict(cfg!.ResponseName, contValues, catValues), cfg);

                // 更新行标签
                row.ResponseLabel = isCompositeRow
                    ? $"Composite D    {compositeD:F4}"
                    : $"d({cfg!.ResponseName})    {rowD:F4}";

                for (int fi = 0; fi < _profilerFactorOrder.Count && fi < row.Plots.Count; fi++)
                {
                    var fname = _profilerFactorOrder[fi];
                    var pm = row.Plots[fi];

                    // 找参考数据（任意一个响应的 factor data 做 X 轴参考）
                    ProfilerFactorData? fdata = null;
                    foreach (var pd in _multiProfilerData.Values)
                    {
                        if (pd.Factors?.TryGetValue(fname, out fdata) == true) break;
                    }
                    if (fdata == null) continue;

                    pm.Series.Clear();
                    pm.Annotations.Clear();

                    if (fdata.IsCategorical)
                    {
                        var labels = fdata.Levels ?? fdata.X_Labels;
                        var line = new LineSeries
                        {
                            Color = isCompositeRow ? OxyColor.FromRgb(0xE8, 0xA5, 0x40) : OxyColor.FromRgb(0x2E, 0x86, 0xC1),
                            StrokeThickness = isCompositeRow ? 2 : 1.5
                        };
                        for (int li = 0; li < labels.Count; li++)
                        {
                            var tempCat = new Dictionary<string, string>(catValues) { [fname] = labels[li] };
                            double d = isCompositeRow
                                ? ComputeCurrentCompositeD(contValues, tempCat)
                                : ComputeDesirabilityForConfig(
                                    _multiResponsePredictor.Predict(cfg!.ResponseName, contValues, tempCat), cfg);
                            line.Points.Add(new DataPoint(li, d));
                        }
                        pm.Series.Add(line);

                        var curVal = _profilerCurrentValues.TryGetValue(fname, out var cv) ? cv?.ToString() : "";
                        int curIdx = labels.IndexOf(curVal ?? "");
                        if (curIdx >= 0)
                            pm.Annotations.Add(new LineAnnotation { Type = LineAnnotationType.Vertical, X = curIdx, Color = OxyColor.FromRgb(198, 40, 40), StrokeThickness = 1 });
                    }
                    else
                    {
                        var line = new LineSeries
                        {
                            Color = isCompositeRow ? OxyColor.FromRgb(0xE8, 0xA5, 0x40) : OxyColor.FromRgb(0x2E, 0x86, 0xC1),
                            StrokeThickness = isCompositeRow ? 2 : 1.5
                        };
                        foreach (var xVal in fdata.X)
                        {
                            var tempCont = new Dictionary<string, double>(contValues) { [fname] = xVal };
                            double d = isCompositeRow
                                ? ComputeCurrentCompositeD(tempCont, catValues)
                                : ComputeDesirabilityForConfig(
                                    _multiResponsePredictor.Predict(cfg!.ResponseName, tempCont, catValues), cfg);
                            line.Points.Add(new DataPoint(xVal, d));
                        }
                        pm.Series.Add(line);

                        double curX = contValues.TryGetValue(fname, out var cx) ? cx : 0;
                        pm.Annotations.Add(new LineAnnotation { Type = LineAnnotationType.Vertical, X = curX, Color = OxyColor.FromRgb(198, 40, 40), StrokeThickness = 1 });
                    }

                    pm.Annotations.Add(new LineAnnotation
                    {
                        Type = LineAnnotationType.Horizontal,
                        Y = rowD,
                        Color = OxyColor.FromArgb(60, 198, 40, 40),
                        LineStyle = LineStyle.Dot,
                        StrokeThickness = 0.8
                    });

                    pm.Subtitle = isCompositeRow ? $"D={compositeD:F4}" : $"d={rowD:F2}";
                    pm.InvalidatePlot(true);
                }
            }
        }


        // ═══════════════════════════════════════════════════════
        // [新增] 拖拽索引映射辅助方法
        // ═══════════════════════════════════════════════════════

        /// <summary>
        /// 从 ProfilerPlots 的扁平索引获取 (响应行索引, 因子索引)
        /// 用于 code-behind 的拖拽事件处理
        /// </summary>
        public (int rowIndex, int factorIndex) GetProfilerPlotPosition(int flatIndex)
        {
            if (!_isMultiResponseProfiler)
                return (0, flatIndex);

            int nFactors = _profilerFactorOrder.Count;
            if (nFactors == 0) return (0, 0);

            int rowIndex = flatIndex / nFactors;
            int factorIndex = flatIndex % nFactors;
            return (rowIndex, factorIndex);
        }

        /// <summary>
        /// 从 PlotModel 的 Title (因子名) 获取因子名
        /// 多响应模式下, 所有行的同一列共享同一个因子名
        /// </summary>
        public string? GetProfilerFactorNameFromPlot(PlotModel plotModel)
        {
            // PlotModel.Title 存的是因子名
            return plotModel?.Title;
        }

        /// <summary>
        /// ★ 新增: 根据当前预测刻画器数据，计算当前位置的置信区间文本
        /// </summary>
        private string GetCurrentCIText(ProfilerResult data)
        {
            if (data?.Factors == null) return "";
            foreach (var kv in data.Factors)
            {
                var fd = kv.Value;
                if (fd.YLower == null || fd.YUpper == null || fd.YLower.Count == 0) continue;

                double? lo = null, hi = null;
                if (fd.IsCategorical)
                {
                    var curVal = _profilerCurrentValues.TryGetValue(kv.Key, out var cv) ? cv?.ToString() : "";
                    int idx = fd.X_Labels.IndexOf(curVal ?? "");
                    if (idx >= 0 && idx < fd.YLower.Count)
                    { lo = fd.YLower[idx]; hi = fd.YUpper[idx]; }
                }
                else
                {
                    double curX = fd.CurrentNumericValue;
                    var xs = fd.X;
                    if (xs.Count < 2) continue;
                    if (curX <= xs[0]) { lo = fd.YLower[0]; hi = fd.YUpper[0]; }
                    else if (curX >= xs[xs.Count - 1]) { lo = fd.YLower[^1]; hi = fd.YUpper[^1]; }
                    else
                    {
                        for (int k = 0; k < xs.Count - 1; k++)
                        {
                            if (curX >= xs[k] && curX <= xs[k + 1])
                            {
                                double t = (curX - xs[k]) / (xs[k + 1] - xs[k]);
                                lo = fd.YLower[k] + t * (fd.YLower[k + 1] - fd.YLower[k]);
                                hi = fd.YUpper[k] + t * (fd.YUpper[k + 1] - fd.YUpper[k]);
                                break;
                            }
                        }
                    }
                }
                if (lo.HasValue && hi.HasValue)
                    return $"  95%CI [{lo.Value:F4}, {hi.Value:F4}]";
                break;
            }
            return "";
        }
        /// <summary>
        /// ★ Feature 3: 获取当前响应的 Desirability 配置（供控制点拖拽用）
        /// </summary>
        public DesirabilityResponseConfig? GetCurrentDesirabilityConfig()
        {
            return _currentDesirabilityConfigs
                .FirstOrDefault(c => c.ResponseName == _selectedResponseName);
        }

        /// <summary>
        /// ★ Feature 3: 更新 Lower 值（拖拽控制点时调用）
        /// </summary>
        public void UpdateDesirabilityLower(double newLower)
        {
            var cfg = GetCurrentDesirabilityConfig();
            if (cfg == null) return;
            cfg.Lower = Math.Min(newLower, cfg.Target - 0.01);
        }

        /// <summary>
        /// ★ Feature 3: 更新 Target 值
        /// </summary>
        public void UpdateDesirabilityTarget(double newTarget)
        {
            var cfg = GetCurrentDesirabilityConfig();
            if (cfg == null) return;
            cfg.Target = Math.Max(newTarget, cfg.Lower + 0.01);
            if (cfg.Goal == DesirabilityGoal.Maximize)
                cfg.Upper = cfg.Target;
        }

        /// <summary>
        /// ★ Feature 3: 更新中间点（调整 shape 参数使 d(midY) = 0.5）
        /// </summary>
        public void UpdateDesirabilityMidPoint(double midY)
        {
            var cfg = GetCurrentDesirabilityConfig();
            if (cfg == null) return;

            // shape = log(0.5) / log((midY - lower) / (target - lower))
            double t = (midY - cfg.Lower) / (cfg.Target - cfg.Lower);
            if (t > 0.01 && t < 0.99)
            {
                cfg.Shape = Math.Log(0.5) / Math.Log(t);
                cfg.Shape = Math.Max(0.1, Math.Min(10.0, cfg.Shape));
            }
        }

        /// <summary>
        /// ★ Feature 3: 刷新意愿函数图（控制点位置变化后）
        /// </summary>
        public void RefreshDesirabilityFunctionPlot()
        {
            if (DesirabilityPlots == null || DesirabilityPlots.Count == 0) return;

            // 最后一个图是意愿函数图
            var dPlot = DesirabilityPlots[DesirabilityPlots.Count - 1];
            var cfg = GetCurrentDesirabilityConfig();
            if (cfg == null) return;

            dPlot.Series.Clear();
            dPlot.Annotations.Clear();

            double yMin = cfg.Lower - (cfg.Upper - cfg.Lower) * 0.1;
            double yMax = cfg.Upper + (cfg.Upper - cfg.Lower) * 0.1;

            // d(y) 曲线
            var curveSeries = new LineSeries { Color = OxyColors.Black, StrokeThickness = 1.5 };
            for (int i = 0; i <= 100; i++)
            {
                double y = yMin + (yMax - yMin) * i / 100.0;
                double d = ComputeDesirabilityForCurrentResponse(y);
                curveSeries.Points.Add(new DataPoint(y, d));
            }
            dPlot.Series.Add(curveSeries);

            // ★ v8: 控制点根据目标类型正确放置
            var controlPoints = new ScatterSeries
            {
                MarkerType = MarkerType.Square,
                MarkerSize = 6,
                MarkerFill = OxyColors.White,
                MarkerStroke = OxyColors.Black,
                MarkerStrokeThickness = 1.5
            };

            switch (cfg.Goal)
            {
                case DesirabilityGoal.Maximize:
                    controlPoints.Points.Add(new ScatterPoint(cfg.Lower, 0));
                    controlPoints.Points.Add(new ScatterPoint(cfg.Target, 1));
                    break;

                case DesirabilityGoal.Minimize:
                    controlPoints.Points.Add(new ScatterPoint(cfg.Target, 1));
                    controlPoints.Points.Add(new ScatterPoint(cfg.Upper, 0));
                    break;

                default: // 望目
                    controlPoints.Points.Add(new ScatterPoint(cfg.Lower, 0));
                    controlPoints.Points.Add(new ScatterPoint(cfg.Target, 1));
                    controlPoints.Points.Add(new ScatterPoint(cfg.Upper, 0));
                    break;
            }

            dPlot.Series.Add(controlPoints);

            // 当前 D 值
            double compositeD = DesirabilityCompositeD;
            var marker = new ScatterSeries
            {
                MarkerType = MarkerType.Circle,
                MarkerSize = 6,
                MarkerFill = OxyColors.DarkOrange
            };
            marker.Points.Add(new ScatterPoint(compositeD, compositeD));
            dPlot.Series.Add(marker);

            dPlot.InvalidatePlot(true);
        }
      

      
        /// <summary>
        /// 重建单个图的全部内容（其他图）
        /// </summary>
        private void RebuildSinglePlot(PlotModel pm, string fname, ProfilerFactorData fdata,
      double currentPredicted, double yMin, double yMax, string yTitle)
        {
            if (fdata.IsCategorical)
            {
                var curVal = _profilerCurrentValues.TryGetValue(fname, out var cv) ? cv?.ToString() : "";
                pm.Subtitle = curVal ?? "";
            }
            else
            {
                pm.Subtitle = $"{fdata.CurrentNumericValue:F2}";
            }

            pm.Series.Clear();
            pm.Annotations.Clear();
            pm.Axes.Clear();

            pm.Axes.Add(new LinearAxis
            {
                Position = AxisPosition.Left,
                Minimum = yMin,
                Maximum = yMax,
                Title = yTitle
            });

            if (fdata.IsCategorical)
            {
                var labels = fdata.X_Labels;
                pm.Axes.Add(new LinearAxis
                {
                    Position = AxisPosition.Bottom,
                    Minimum = -0.5,
                    Maximum = labels.Count - 0.5,
                    MajorStep = 1,
                    MinorStep = 1,
                    LabelFormatter = val =>
                    {
                        int idx = (int)Math.Round(val);
                        return idx >= 0 && idx < labels.Count ? labels[idx] : "";
                    }
                });

                if (fdata.YLower != null && fdata.YUpper != null && fdata.YLower.Count == fdata.Y.Count && fdata.Y.Count > 0)
                {
                    var area = new AreaSeries { Color = OxyColor.FromArgb(0, 0, 0, 0), Fill = JmpCIFill(0), StrokeThickness = 0 };
                    for (int j = 0; j < fdata.Y.Count && j < labels.Count; j++)
                    {
                        area.Points.Add(new DataPoint(j, fdata.YUpper[j]));
                        area.Points2.Add(new DataPoint(j, fdata.YLower[j]));
                    }
                    pm.Series.Add(area);
                }

                var lineSeries = new LineSeries
                {
                    Color = OxyColor.FromRgb(160, 160, 160),
                    StrokeThickness = 1.2,
                    MarkerType = MarkerType.Circle,
                    MarkerSize = 3.5,
                    MarkerFill = JmpPalette[0]
                };
                for (int j = 0; j < fdata.Y.Count && j < labels.Count; j++)
                    lineSeries.Points.Add(new DataPoint(j, fdata.Y[j]));
                pm.Series.Add(lineSeries);

                var curVal = _profilerCurrentValues.TryGetValue(fname, out var cv2) ? cv2?.ToString() : "";
                int curIdx = labels.IndexOf(curVal ?? "");
                if (curIdx >= 0 && curIdx < fdata.Y.Count)
                {
                    pm.Annotations.Add(new LineAnnotation
                    {
                        Type = LineAnnotationType.Vertical,
                        X = curIdx,
                        Color = OxyColor.FromRgb(198, 40, 40),
                        LineStyle = LineStyle.Solid,
                        StrokeThickness = 1
                    });
                    var hl = new ScatterSeries { MarkerType = MarkerType.Circle, MarkerSize = 5, MarkerFill = OxyColor.FromRgb(198, 40, 40) };
                    hl.Points.Add(new ScatterPoint(curIdx, fdata.Y[curIdx]));
                    pm.Series.Add(hl);
                }
            }
            else
            {
                _logger.LogInformation("★ CI绘制: factor={F}, IsCat={Cat}, X.Count={XC}, entering={Enter}",
    fname, fdata.IsCategorical, fdata.IsCategorical ? 0 : fdata.X.Count,
    !fdata.IsCategorical && fdata.YLower != null && fdata.YUpper != null && fdata.YLower.Count == fdata.Y.Count && fdata.Y.Count > 0);
                pm.Axes.Add(new LinearAxis { Position = AxisPosition.Bottom });

                if (fdata.YLower != null && fdata.YUpper != null && fdata.YLower.Count == fdata.Y.Count && fdata.Y.Count > 0)
                { // ★ 诊断
                    int mid = fdata.Y.Count / 2;
                 
                    var area = new AreaSeries { Color = OxyColor.FromArgb(0, 0, 0, 0), Fill = JmpCIFill(0), StrokeThickness = 0 };
                    for (int j = 0; j < fdata.X.Count; j++)
                    {
                        area.Points.Add(new DataPoint(fdata.X[j], fdata.YUpper[j]));
                        area.Points2.Add(new DataPoint(fdata.X[j], fdata.YLower[j]));
                    }
                    pm.Series.Add(area);
                }

                var line = new LineSeries { Color = JmpPalette[0], StrokeThickness = 1.8 };
                for (int j = 0; j < fdata.X.Count; j++)
                    line.Points.Add(new DataPoint(fdata.X[j], fdata.Y[j]));
                pm.Series.Add(line);

                double curX = fdata.CurrentNumericValue;
                pm.Annotations.Add(new OxyPlot.Annotations.LineAnnotation
                {
                    Type = OxyPlot.Annotations.LineAnnotationType.Vertical,
                    X = curX,
                    Color = OxyColor.FromRgb(198, 40, 40),
                    LineStyle = LineStyle.Solid,
                    StrokeThickness = 1
                });
            }

            pm.Annotations.Add(new OxyPlot.Annotations.LineAnnotation
            {
                Type = OxyPlot.Annotations.LineAnnotationType.Horizontal,
                Y = currentPredicted,
                Color = OxyColor.FromArgb(60, 198, 40, 40),
                LineStyle = LineStyle.Dot,
                StrokeThickness = 0.8
            });

            ApplyJmpProfilerStyle(pm);
            pm.InvalidatePlot(true);
        }


        /// <summary>
        /// ★ v6: 用户点击类别因子的某个水平
        /// </summary>
        public async Task UpdateProfilerCategoryAsync(string factorName, string levelValue)
        {
            await UpdateProfilerValueAsync(factorName, levelValue);
        }

        /// <summary>
        /// 获取因子在 profiler 图列表中的索引
        /// </summary>
        public string? GetProfilerFactorName(int plotIndex)
        {
            if (plotIndex >= 0 && plotIndex < _profilerFactorOrder.Count)
                return _profilerFactorOrder[plotIndex];
            return null;
        }

        /// <summary>
        /// 获取指定因子是否为类别因子
        /// </summary>
        public bool IsProfilerFactorCategorical(string factorName)
        {
            if (_isMultiResponseProfiler && _multiProfilerData.Count > 0)
            {
                foreach (var kv in _multiProfilerData)
                {
                    if (kv.Value.Factors?.TryGetValue(factorName, out var fd) == true)
                        return fd.IsCategorical;
                }
            }
            return _lastProfilerData?.Factors?.TryGetValue(factorName, out var fd2) == true && fd2.IsCategorical;
        }

        /// <summary>
        /// 获取类别因子的水平列表
        /// </summary>
        public List<string>? GetProfilerCategoryLevels(string factorName)
        {
            if (_isMultiResponseProfiler && _multiProfilerData.Count > 0)
            {
                foreach (var kv in _multiProfilerData)
                {
                    if (kv.Value.Factors?.TryGetValue(factorName, out var fd) == true && fd.IsCategorical)
                        return fd.Levels;
                }
            }
            if (_lastProfilerData?.Factors?.TryGetValue(factorName, out var fd2) == true && fd2.IsCategorical)
                return fd2.Levels;
            return null;
        }

        /// <summary>
        /// ★ v6: 获取类别因子在指定水平索引处的 Y 值
        /// </summary>
        public double? GetProfilerCategoryYAtIndex(string factorName, int index)
        {
            if (_lastProfilerData?.Factors == null) return null;
            if (!_lastProfilerData.Factors.TryGetValue(factorName, out var fd)) return null;
            if (!fd.IsCategorical || index < 0 || index >= fd.Y.Count) return null;
            return fd.Y[index];
        }

        /// <summary>
        /// 获取连续因子的范围
        /// </summary>
        public (double min, double max) GetProfilerFactorRange(string factorName)
        {
            // ★ 多响应时从所有响应的 profiler 数据中查找
            if (_isMultiResponseProfiler && _multiProfilerData.Count > 0)
            {
                foreach (var kv in _multiProfilerData)
                {
                    if (kv.Value.Factors?.TryGetValue(factorName, out var fd) == true && fd.Range?.Count == 2)
                        return (fd.Range[0], fd.Range[1]);
                }
            }

            // 单响应或回退
            if (_lastProfilerData?.Factors?.TryGetValue(factorName, out var fd2) == true && fd2.Range?.Count == 2)
                return (fd2.Range[0], fd2.Range[1]);

            return (0, 1);
        }

        /// <summary>
        /// ★ v6: 在缓存的剖面曲线上插值，给定 X 返回 Y（用于拖动时 Y 跟随曲线）
        /// </summary>
        public double? GetProfilerYAtX(string factorName, double xValue)
        {
            if (_lastProfilerData?.Factors == null) return null;
            if (!_lastProfilerData.Factors.TryGetValue(factorName, out var fd)) return null;
            if (fd.IsCategorical || fd.X.Count < 2) return null;

            var xs = fd.X;
            var ys = fd.Y;

            // 限制 X 范围
            if (xValue <= xs[0]) return ys[0];
            if (xValue >= xs[xs.Count - 1]) return ys[ys.Count - 1];

            // 线性插值
            for (int i = 0; i < xs.Count - 1; i++)
            {
                if (xValue >= xs[i] && xValue <= xs[i + 1])
                {
                    double t = (xValue - xs[i]) / (xs[i + 1] - xs[i]);
                    return ys[i] + t * (ys[i + 1] - ys[i]);
                }
            }
            return null;
        }
        /// <summary>
        /// ★ 新增: 在缓存的 CI 曲线上插值，给定 X 返回 (yLower, yUpper)
        /// </summary>
        public (double? lower, double? upper) GetProfilerCIAtX(string factorName, double xValue)
        {
            if (_lastProfilerData?.Factors == null) return (null, null);
            if (!_lastProfilerData.Factors.TryGetValue(factorName, out var fd)) return (null, null);
            if (fd.IsCategorical || fd.X.Count < 2) return (null, null);
            if (fd.YLower == null || fd.YUpper == null || fd.YLower.Count != fd.Y.Count) return (null, null);

            var xs = fd.X;
            var lows = fd.YLower;
            var ups = fd.YUpper;

            if (xValue <= xs[0]) return (lows[0], ups[0]);
            if (xValue >= xs[xs.Count - 1]) return (lows[lows.Count - 1], ups[ups.Count - 1]);

            for (int i = 0; i < xs.Count - 1; i++)
            {
                if (xValue >= xs[i] && xValue <= xs[i + 1])
                {
                    double t = (xValue - xs[i]) / (xs[i + 1] - xs[i]);
                    double lo = lows[i] + t * (lows[i + 1] - lows[i]);
                    double hi = ups[i] + t * (ups[i + 1] - ups[i]);
                    return (lo, hi);
                }
            }
            return (null, null);
        }

        /// <summary>
        /// ★ 新增: 获取类别因子在指定水平索引处的 CI 值
        /// </summary>
        public (double? lower, double? upper) GetProfilerCategoryCIAtIndex(string factorName, int index)
        {
            if (_lastProfilerData?.Factors == null) return (null, null);
            if (!_lastProfilerData.Factors.TryGetValue(factorName, out var fd)) return (null, null);
            if (!fd.IsCategorical || index < 0 || index >= fd.Y.Count) return (null, null);
            if (fd.YLower == null || fd.YUpper == null || fd.YLower.Count != fd.Y.Count) return (null, null);
            return (fd.YLower[index], fd.YUpper[index]);
        }
        /// <summary>
        /// 获取全局 Y 范围（所有图共享）
        /// </summary>
        public (double yMin, double yMax) GetProfilerYRange()
        {
            if (_lastProfilerData?.Factors == null) return (0, 1);
            double yMin = double.MaxValue, yMax = double.MinValue;
            foreach (var kv in _lastProfilerData.Factors)
            {
                if (kv.Value.Y.Count > 0)
                {
                    yMin = Math.Min(yMin, kv.Value.Y.Min());
                    yMax = Math.Max(yMax, kv.Value.Y.Max());
                }
            }
            double yPad = (yMax - yMin) * 0.1;
            return (yMin - yPad, yMax + yPad);
        }

        /// <summary>
        /// 构建所有 profiler 图（统一 Y 轴范围，JMP 风格）
        /// 连续因子: 曲线 + 红色竖虚线（当前值）
        /// 类别因子: 散点+折线 + 红色标记（当前选中水平）
        /// </summary>
        private void BuildProfilerPlots(ProfilerResult data)
        {
            string ciText = "";
            foreach (var kv in data.Factors)
            {
                var fd = kv.Value;
                if (fd.YLower == null || fd.YUpper == null || fd.YLower.Count == 0) continue;
                double? lo = null, hi = null;
                if (fd.IsCategorical)
                {
                    var curVal = _profilerCurrentValues.TryGetValue(kv.Key, out var cv) ? cv?.ToString() : "";
                    int idx = fd.X_Labels.IndexOf(curVal ?? "");
                    if (idx >= 0 && idx < fd.YLower.Count) { lo = fd.YLower[idx]; hi = fd.YUpper[idx]; }
                }
                else
                {
                    double curX = fd.CurrentNumericValue;
                    var xs = fd.X;
                    if (curX <= xs[0]) { lo = fd.YLower[0]; hi = fd.YUpper[0]; }
                    else if (curX >= xs[xs.Count - 1]) { lo = fd.YLower[fd.YLower.Count - 1]; hi = fd.YUpper[fd.YUpper.Count - 1]; }
                    else
                    {
                        for (int k = 0; k < xs.Count - 1; k++)
                        {
                            if (curX >= xs[k] && curX <= xs[k + 1])
                            {
                                double t = (curX - xs[k]) / (xs[k + 1] - xs[k]);
                                lo = fd.YLower[k] + t * (fd.YLower[k + 1] - fd.YLower[k]);
                                hi = fd.YUpper[k] + t * (fd.YUpper[k + 1] - fd.YUpper[k]);
                                break;
                            }
                        }
                    }
                }
                if (lo.HasValue && hi.HasValue) ciText = $"  95%CI [{lo.Value:F4}, {hi.Value:F4}]";
                break;
            }
            ProfilerCurrentPredicted = $"{data.ResponseName}    {data.CurrentPredicted:F4}{ciText}";

            double yMin = double.MaxValue, yMax = double.MinValue;
            foreach (var kv in data.Factors)
            {
                if (kv.Value.Y.Count > 0) { yMin = Math.Min(yMin, kv.Value.Y.Min()); yMax = Math.Max(yMax, kv.Value.Y.Max()); }
            }
            double yPad = (yMax - yMin) * 0.1;
            if (yPad < 0.01) yPad = 1.0;

            var plots = new List<PlotModel>();
            foreach (var fname in _profilerFactorOrder)
            {
                if (!data.Factors.TryGetValue(fname, out var fdata)) continue;

                string subtitleText = fdata.IsCategorical
                    ? (_profilerCurrentValues.TryGetValue(fname, out var cv) ? cv?.ToString() : "") ?? ""
                    : $"{fdata.CurrentNumericValue:F2}";

                var pm = new PlotModel
                {
                    Title = fname,
                    Subtitle = subtitleText,
                    PlotMargins = new OxyThickness(50, 20, 10, 35)
                };

                pm.Axes.Add(new LinearAxis
                {
                    Position = AxisPosition.Left,
                    Minimum = yMin - yPad,
                    Maximum = yMax + yPad,
                    Title = plots.Count == 0 ? data.ResponseName : ""
                });

                if (fdata.IsCategorical)
                {
                    var labels = fdata.X_Labels;
                    pm.Axes.Add(new LinearAxis
                    {
                        Position = AxisPosition.Bottom,
                        Minimum = -0.5,
                        Maximum = labels.Count - 0.5,
                        MajorStep = 1,
                        MinorStep = 1,
                        LabelFormatter = val =>
                        {
                            int idx = (int)Math.Round(val);
                            return idx >= 0 && idx < labels.Count ? labels[idx] : "";
                        }
                    });

                    // CI 带
                    if (fdata.YLower != null && fdata.YUpper != null && fdata.YLower.Count == fdata.Y.Count && fdata.Y.Count > 0)
                    {
                        var area = new AreaSeries { Color = OxyColor.FromArgb(0, 0, 0, 0), Fill = JmpCIFill(0), StrokeThickness = 0 };
                        for (int j = 0; j < fdata.Y.Count && j < labels.Count; j++)
                        {
                            area.Points.Add(new DataPoint(j, fdata.YUpper[j]));
                            area.Points2.Add(new DataPoint(j, fdata.YLower[j]));
                        }
                        pm.Series.Add(area);
                    }

                    var lineSeries = new LineSeries
                    {
                        Color = OxyColor.FromRgb(160, 160, 160),
                        StrokeThickness = 1.2,
                        MarkerType = MarkerType.Circle,
                        MarkerSize = 3.5,
                        MarkerFill = JmpPalette[0]
                    };
                    for (int i = 0; i < fdata.Y.Count && i < labels.Count; i++)
                        lineSeries.Points.Add(new DataPoint(i, fdata.Y[i]));
                    pm.Series.Add(lineSeries);

                    var curVal = _profilerCurrentValues.TryGetValue(fname, out var cvv) ? cvv?.ToString() : "";
                    int curIdx = labels.IndexOf(curVal ?? "");
                    if (curIdx >= 0 && curIdx < fdata.Y.Count)
                    {
                        pm.Annotations.Add(new OxyPlot.Annotations.LineAnnotation
                        {
                            Type = OxyPlot.Annotations.LineAnnotationType.Vertical,
                            X = curIdx,
                            Color = OxyColor.FromRgb(198, 40, 40),
                            LineStyle = LineStyle.Solid,
                            StrokeThickness = 1
                        });
                        var hl = new ScatterSeries { MarkerType = MarkerType.Circle, MarkerSize = 5, MarkerFill = OxyColor.FromRgb(198, 40, 40) };
                        hl.Points.Add(new ScatterPoint(curIdx, fdata.Y[curIdx]));
                        pm.Series.Add(hl);
                    }

                    pm.Annotations.Add(new OxyPlot.Annotations.LineAnnotation
                    {
                        Type = OxyPlot.Annotations.LineAnnotationType.Horizontal,
                        Y = data.CurrentPredicted,
                        Color = OxyColor.FromArgb(60, 198, 40, 40),
                        LineStyle = LineStyle.Dot,
                        StrokeThickness = 0.8
                    });
                }
                else
                {
                    pm.Axes.Add(new LinearAxis { Position = AxisPosition.Bottom });

                    // CI 带
                    if (fdata.YLower != null && fdata.YUpper != null && fdata.YLower.Count == fdata.Y.Count && fdata.Y.Count > 0)
                    {
                        var area = new AreaSeries { Color = OxyColor.FromArgb(0, 0, 0, 0), Fill = JmpCIFill(0), StrokeThickness = 0 };
                        for (int j = 0; j < fdata.X.Count; j++)
                        {
                            area.Points.Add(new DataPoint(fdata.X[j], fdata.YUpper[j]));
                            area.Points2.Add(new DataPoint(fdata.X[j], fdata.YLower[j]));
                        }
                        pm.Series.Add(area);
                    }

                    var line = new LineSeries { Color = JmpPalette[0], StrokeThickness = 1.8 };
                    for (int i = 0; i < fdata.X.Count; i++)
                        line.Points.Add(new DataPoint(fdata.X[i], fdata.Y[i]));
                    pm.Series.Add(line);

                    double curX = fdata.CurrentNumericValue;
                    pm.Annotations.Add(new OxyPlot.Annotations.LineAnnotation
                    {
                        Type = OxyPlot.Annotations.LineAnnotationType.Vertical,
                        X = curX,
                        Color = OxyColor.FromRgb(198, 40, 40),
                        LineStyle = LineStyle.Solid,
                        StrokeThickness = 1
                    });

                    pm.Annotations.Add(new OxyPlot.Annotations.LineAnnotation
                    {
                        Type = OxyPlot.Annotations.LineAnnotationType.Horizontal,
                        Y = data.CurrentPredicted,
                        Color = OxyColor.FromArgb(60, 198, 40, 40),
                        LineStyle = LineStyle.Dot,
                        StrokeThickness = 0.8
                    });
                }

                ApplyJmpProfilerStyle(pm);
                plots.Add(pm);
            }
            ProfilerPlots = plots;
        }

        private async Task FindOlsOptimalAsync(bool maximize)
        {
            if (string.IsNullOrEmpty(_selectedResponseName)) return;
            if (!IsDirectImportMode && SelectedOlsBatch == null) return;

            try
            {
                IsLoading = true;
                OlsStatusText = maximize
                    ? _localizationService.GetString("Doe_Analysis_Status_InSerachMax", "正在搜索最大值...")
                    : _localizationService.GetString("Doe_Analysis_Status_InSearchMin", "正在搜索最小值...");

                string json;
                if (IsDirectImportMode)
                {
                    json = await Task.Run(() =>
                        _analysisService.FindOptimalDirectAsync(_selectedResponseName, maximize));
                }
                else
                {
                    json = await _analysisService.FindOptimalAsync(
                        SelectedOlsBatch!.BatchId, _selectedResponseName, maximize);
                }

                var data = JsonConvert.DeserializeObject<OptimalResult>(json);

                if (data?.Success == true)
                {
                    var factorsStr = string.Join("\n",
                        data.OptimalFactors.Select(kv => $"  {kv.Key} = {kv.Value}"));
                    OptimalResultText = $"解是{(maximize ? "最大值" : "最小值")}\n" +
                                        $"预测响应: {data.PredictedResponse:F4}\n" +
                                        $"最优条件:\n{factorsStr}\n" +
                                        $"{data.Note}";
                    OlsStatusText = string.Format(
                        _localizationService.GetString("Doe_Analysis_Status_OptimizeComplete", "优化完成: {0}={1:F4}"),
                        _selectedResponseName, data.PredictedResponse);

                    // 用最优条件刷新 profiler（如果已加载）
                    if (IsProfilerLoaded)
                    {
                        foreach (var kv in data.OptimalFactors)
                            _profilerCurrentValues[kv.Key] = kv.Value;

                        try
                        {
                            string profilerJson;
                            if (IsDirectImportMode)
                            {
                                var fixedJson = JsonConvert.SerializeObject(data.OptimalFactors);
                                profilerJson = await Task.Run(() =>
                                    _analysisService.GetPredictionProfilerDirectAsync(
                                        _selectedResponseName, 50, fixedJson));
                            }
                            else
                            {
                                profilerJson = await _analysisService.GetPredictionProfilerAsync(
                                    SelectedOlsBatch!.BatchId, _selectedResponseName, 50, data.OptimalFactors);
                            }

                            var profData = JsonConvert.DeserializeObject<ProfilerResult>(profilerJson);
                            if (profData?.Factors != null)
                            {
                                _lastProfilerData = profData;
                                BuildProfilerPlots(profData);
                            }
                        }
                        catch { /* profiler 刷新失败不影响优化结果 */ }
                    }
                }
                else
                {
                    OptimalResultText = $"优化失败: {data?.Error ?? "未知错误"}";
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "OLS优化失败");
                OptimalResultText = $"优化失败: {ex.Message}";
            }
            finally { IsLoading = false; }
        }


        // ═══ 残差四合一面板 ═══

        private async Task LoadResidualDiagnosticsAsync()
        {
            try
            {
                var json = await _analysisService.GetResidualDiagnosticsAsync(
                    SelectedOlsBatch!.BatchId, _selectedResponseName);
                var diag = JsonConvert.DeserializeObject<ResidualDiagnostics>(json);
                if (diag == null) return;
                BuildResidualPlotsFromData(diag);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "残差诊断加载失败");
            }
        }


        // ══════════════ Desirability ══════════════
        private async Task RunDesirabilityOptimizationAsync()
        {
            if (ResponseNames.Count < 1)
            {
                _dialogService.ShowError("Desirability 优化需要至少 1 个响应变量", "提示");
                return;
            }

            try
            {
                IsLoading = true;

                if (IsOlsTabSelected)
                {
                    // ═══ OLS 模式: 对标 Minitab Response Optimizer ═══
                    OlsStatusText = _localizationService.GetString("Doe_Analysis_Status_InSearchOls", "正在搜索 OLS 多响应最优因子组合...");

                    // 获取当前批次信息
                    var batchId = IsDirectImportMode ? "" : SelectedOlsBatch?.BatchId;
                    if (string.IsNullOrEmpty(batchId) && !IsDirectImportMode)
                    {
                        _dialogService.ShowError("请先选择一个实验批次", "提示");
                        return;
                    }

                    // 加载 Desirability 配置
                    var configs = await _desirabilityService.LoadConfigAsync(batchId ?? "");
                    if (configs.Count == 0)
                    {
                        _dialogService.ShowError("请先在设计向导 Step4 中配置各响应变量的优化目标", "缺少配置");
                        return;
                    }

                    // 获取因子列表
                    var batch = await _repository.GetBatchWithDetailsAsync(batchId ?? "");
                    if (batch == null) return;

                    // ★ 核心: 用 OLS 模型做优化
                    //DesirabilityResult = await _desirabilityService.OptimizeWithOlsAsync(
                    //    batchId!, configs, batch.Factors, _analysisService);

                    //if (DesirabilityResult?.Success == true)
                    //{
                    //    OlsStatusText = $"OLS 多响应优化完成: 综合 D = {DesirabilityResult.CompositeD:F4}";
                    //    UpdateOptimalFactorsDisplay(DesirabilityResult);
                    //}
                    //else
                    //{
                    //    OlsStatusText = $"OLS 优化失败: {DesirabilityResult?.ErrorMessage ?? "未知错误"}";
                    //}
                }
                else
                {
                    // ═══ GPR 模式: 原有逻辑保持不变 ═══
                    /* 暂时不用
                    StatusMessage = "正在搜索 GPR 多响应最优因子组合...";

                    var configs = await _desirabilityService.LoadConfigAsync(_currentBatchId);
                    if (configs.Count == 0)
                    {
                        _dialogService.ShowError("请先在设计向导 Step4 中配置各响应变量的优化目标", "缺少配置");
                        return;
                    }

                    var batch = await _repository.GetBatchWithDetailsAsync(_currentBatchId);
                    if (batch == null) return;

                    await _desirabilityService.ConfigureAsync(_currentBatchId, configs, batch.Factors);
                    DesirabilityResult = await _desirabilityService.OptimizeAsync();

                    if (DesirabilityResult?.Success == true)
                    {
                        StatusMessage = $"GPR 多响应优化完成: 综合 D = {DesirabilityResult.CompositeD:F4}";
                        UpdateOptimalFactorsDisplay(DesirabilityResult);
                    }
                    else
                    {
                        StatusMessage = $"GPR 优化失败: {DesirabilityResult?.ErrorMessage ?? "未知错误"}";
                    }*/
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Desirability 优化失败");
                if (IsOlsTabSelected)
                    OlsStatusText = string.Format(_localizationService.GetString("Doe_Analysis_Status_OptimizeExp", "优化异常: {0}"),ex.Message);
                else
                    StatusMessage = $"优化异常: {ex.Message}";
            }
            finally
            {
                IsLoading = false;
            }
        }
        /// <summary>
        /// 辅助方法: 更新最优因子显示
        /// </summary>
        private void UpdateOptimalFactorsDisplay(DesirabilityResult result)
        {
            if (result?.OptimalFactors == null || result.OptimalFactors.Count == 0) return;

            var items = result.OptimalFactors.Select(kv => new OptimalFactorItem
            {
                Key = kv.Key,
                Value = kv.Value
            }).ToList();

            var numericValues = items.Where(x => x.IsNumeric).Select(x => Math.Abs(x.NumericValue)).ToList();
            double maxVal = numericValues.Count > 0 ? numericValues.Max() : 1.0;

            foreach (var item in items)
            {
                item.BarWidth = item.IsNumeric && maxVal > 0
                    ? Math.Abs(item.NumericValue) / maxVal * 200
                    : 0;
            }

            OptimalFactors = items;
            HasOptimalResult = true;
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
                        if (batch?.Responses?.Count > 0) { SetResponseNames(batch.Responses.Select(r => r.ResponseName).ToList()); if (string.IsNullOrEmpty(_selectedResponseName)) { _selectedResponseName = ResponseNames.FirstOrDefault() ?? ""; RaisePropertyChanged(nameof(SelectedResponseName)); } }
                    }
                    else
                    {
                        var batches = await _repository.GetBatchesByFlowAsync(state.FlowId);
                        var latestBatch = batches.FirstOrDefault();
                        if (latestBatch != null) { _currentBatchId = latestBatch.BatchId; var batchDetail = await _repository.GetBatchWithDetailsAsync(_currentBatchId); if (batchDetail?.Responses?.Count > 0) { SetResponseNames(batchDetail.Responses.Select(r => r.ResponseName).ToList()); if (string.IsNullOrEmpty(_selectedResponseName)) { _selectedResponseName = ResponseNames.FirstOrDefault() ?? ""; RaisePropertyChanged(nameof(SelectedResponseName)); } } }
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
        /// <summary>
        /// 曲面图固定因子项 — 绑定到 Slider
        /// </summary>
        public class SurfaceFixedFactorItem : BindableBase
        {
            public string Name { get; set; } = "";
            public double Min { get; set; }
            public double Max { get; set; }

            private double _value;
            public double Value
            {
                get => _value;
                set => SetProperty(ref _value, value);
            }

            /// <summary>Slider 步长（范围的 1/20）</summary>
            public double TickFreq => (Max - Min) > 1e-10 ? (Max - Min) / 20.0 : 0.1;
        }


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

        // ══════════════ OLS 直接导入 Excel 数据分析 ══════════════

        /// <summary>
        /// ★ 新增: 在 OLS Tab 直接导入 Excel 做分析，不依赖任何 DOE 批次
        /// Excel 格式: 第一行列头 = 因子名 + 响应名，后续行为数据
        /// 自动检测: 最后一列或多列为纯数值 → 响应变量；其他列 → 因子
        /// </summary>
        private async Task OlsImportExcelAsync()
        {
            try
            {
                var path = _dialogService.ShowOpenFileDialog("Excel 文件 (*.xlsx)|*.xlsx");
                if (string.IsNullOrEmpty(path)) return;

                IsLoading = true;
                OlsStatusText = _localizationService.GetString("Doe_Analysis_Status_InReadExcel", "正在读取 Excel 文件...");

                // Step 1: 后台读取原始数据
                var rawData = await Task.Run(() => ReadExcelRawData(path));

                if (rawData.Errors.Count > 0)
                {
                    _dialogService.ShowError($"读取失败:\n{string.Join("\n", rawData.Errors)}", "导入失败");
                    OlsStatusText = _localizationService.GetString("Doe_Analysis_Status_ImportFail", "导入失败");
                    return;
                }

                if (rawData.DataRowCount < 3)
                {
                    _dialogService.ShowError($"有效数据不足 3 行（当前 {rawData.DataRowCount} 行）", "数据不足");
                    OlsStatusText = _localizationService.GetString("Doe_Analysis_Status_DataEnough", "数据不足");
                    return;
                }

                IsLoading = false;

                // Step 2: 构建预填配置，弹窗让用户确认
                var columnConfigs = BuildColumnConfigs(rawData);

                bool confirmed = false;
                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    var dialog = new Views.OlsImportConfirmDialog(columnConfigs, path, rawData.DataRowCount);
                    dialog.Owner = System.Windows.Application.Current.MainWindow;
                    dialog.ShowDialog();
                    confirmed = dialog.Confirmed;
                });

                if (!confirmed)
                {
                    OlsStatusText = _localizationService.GetString("Doe_Analysis_Status_CancelImport", "已取消导入");
                    return;
                }

                // Step 3: 根据确认的配置构建分析数据
                IsLoading = true;
                OlsStatusText = _localizationService.GetString("Doe_Analysis_Status_InCreateData", "正在构建分析数据...");

                var importResult = ApplyConfirmedConfig(rawData, columnConfigs);

                if (importResult.DataCount < 3)
                {
                    _dialogService.ShowError("排除空值后有效数据不足 3 行。", "数据不足");
                    OlsStatusText = _localizationService.GetString("Doe_Analysis_Status_DataEnough", "数据不足");
                    return;
                }

                // Step 4: 保存并执行分析
                DirectImportFilePath = path;
                _directImportFactorNames = importResult.FactorNames;
                _directImportResponseNames = importResult.ResponseNames;
                _directImportFactorTypes = importResult.FactorTypes;
                _directImportFactorsData = importResult.FactorsData;
                _directImportResponsesData = importResult.ResponsesData;

                IsDirectImportMode = true;
                _currentOlsModelType = "quadratic";
                SelectedOlsBatch = null;

                SetResponseNames(importResult.ResponseNames);
                _selectedResponseName = ResponseNames.FirstOrDefault() ?? "";
                RaisePropertyChanged(nameof(SelectedResponseName));

                OlsStatusText = string.Format(_localizationService.GetString("Doe_Analysis_Status_RowFactor", "已导入 {0} 行，{1} 个因子，{2} 个响应"),
                    importResult.DataCount, importResult.FactorNames.Count, importResult.ResponseNames.Count);

                await RunDirectOlsAnalysisAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "OLS 直接导入失败");
                _dialogService.ShowError($"导入失败: {ex.Message}", "错误");
                OlsStatusText = string.Format("{0}: {1}",_localizationService.GetString("Doe_Analysis_Status_ImportFail", "导入失败"),ex.Message);
            }
            finally
            {
                IsLoading = false;
            }
        }
     
        /// <summary>
        /// 设置响应名列表。如果有多个响应，自动追加联合选项（如"产率 + 纯度"）
        /// </summary>
        private void SetResponseNames(List<string> names)
        {
            if (names != null && names.Count > 1)
            {
                var list = new List<string>(names);
                list.Add(string.Join(" + ", names));  // 追加联合选项
                ResponseNames = list;
            }
            else
            {
                ResponseNames = names ?? new List<string>();
            }
        }
        /// <summary>
        /// 获取原始响应名列表（不含联合选项）
        /// </summary>
        private List<string> GetRealResponseNames()
        {
            if (ResponseNames == null) return new List<string>();
            return ResponseNames.Where(n => !n.Contains(" + ")).ToList();
        }
        /// <summary>
        /// 根据原始数据自动预填列配置
        /// 规则: 含文字列→类别因子, 全数值列→连续因子, 最后一个全数值列→响应
        /// </summary>
        private static ObservableCollection<ColumnConfigItem> BuildColumnConfigs(DirectImportRawData rawData)
        {
            var configs = new ObservableCollection<ColumnConfigItem>();

            // 找最后一个全数值列作为默认响应
            int lastNumericCol = -1;
            for (int c = rawData.Headers.Count - 1; c >= 0; c--)
            {
                if (rawData.IsNumericColumn[c]) { lastNumericCol = c; break; }
            }

            for (int c = 0; c < rawData.Headers.Count; c++)
            {
                var data = rawData.ColumnData[c];
                var nonNullValues = data.Where(v => v != null).ToList();
                var uniqueCount = nonNullValues.Select(v => v!.ToString()).Distinct().Count();
                var preview = nonNullValues.Take(5).Select(v => v!.ToString() ?? "").ToList();

                var item = new ColumnConfigItem
                {
                    ColumnName = rawData.Headers[c],
                    IsAllNumeric = rawData.IsNumericColumn[c],
                    UniqueCount = uniqueCount,
                    PreviewValues = preview!
                };

                // 自动预填角色和类型
                if (c == lastNumericCol)
                {
                    item.Role = "Response";
                    item.FactorType = "";
                }
                else if (!rawData.IsNumericColumn[c])
                {
                    item.Role = "Factor";
                    item.FactorType = "Categorical";
                }
                else if (rawData.IsNumericColumn[c] && uniqueCount <= 5 && nonNullValues.Count > 10)
                {
                    // 数值列但唯一值很少 → 可能是类别，但默认连续让用户决定
                    item.Role = "Factor";
                    item.FactorType = "Continuous";
                }
                else
                {
                    item.Role = "Factor";
                    item.FactorType = "Continuous";
                }

                configs.Add(item);
            }

            return configs;
        }
        /// <summary>
        /// 根据用户确认的列配置，从原始数据构建 OLS 分析所需的结构化数据
        /// </summary>
        private static DirectImportResult ApplyConfirmedConfig(
            DirectImportRawData rawData,
            ObservableCollection<ColumnConfigItem> configs)
        {
            var result = new DirectImportResult();

            var factorConfigs = configs.Where(c => c.Role == "Factor").ToList();
            var responseConfigs = configs.Where(c => c.Role == "Response").ToList();

            result.FactorNames = factorConfigs.Select(c => c.ColumnName).ToList();
            result.ResponseNames = responseConfigs.Select(c => c.ColumnName).ToList();

            // 因子类型
            foreach (var fc in factorConfigs)
                result.FactorTypes[fc.ColumnName] = fc.FactorType == "Categorical" ? "categorical" : "continuous";

            // 列名→索引映射
            var colIndexMap = new Dictionary<string, int>();
            for (int i = 0; i < rawData.Headers.Count; i++)
                colIndexMap[rawData.Headers[i]] = i;

            // 构建数据行
            for (int row = 0; row < rawData.DataRowCount; row++)
            {
                // 检查所有必需列是否有值
                bool hasNull = false;
                foreach (var fc in factorConfigs)
                {
                    if (rawData.ColumnData[colIndexMap[fc.ColumnName]][row] == null) { hasNull = true; break; }
                }
                if (hasNull) continue;
                foreach (var rc in responseConfigs)
                {
                    if (rawData.ColumnData[colIndexMap[rc.ColumnName]][row] == null) { hasNull = true; break; }
                }
                if (hasNull) continue;

                // 因子行
                var factorRow = new Dictionary<string, object>();
                foreach (var fc in factorConfigs)
                {
                    factorRow[fc.ColumnName] = rawData.ColumnData[colIndexMap[fc.ColumnName]][row]!;
                }
                result.FactorsData.Add(factorRow);

                // 响应值
                foreach (var rc in responseConfigs)
                {
                    var respName = rc.ColumnName;
                    if (!result.ResponsesData.ContainsKey(respName))
                        result.ResponsesData[respName] = new List<double>();
                    result.ResponsesData[respName].Add((double)rawData.ColumnData[colIndexMap[respName]][row]!);
                }
            }

            result.DataCount = result.FactorsData.Count;
            return result;
        }
        /// <summary>
        /// 执行直接导入数据的 OLS 分析
        /// </summary>
        private async Task RunDirectOlsAnalysisAsync()
        {
            if (!IsDirectImportMode || _directImportFactorsData.Count == 0) return;
            if (string.IsNullOrEmpty(_selectedResponseName)) return;

            if (!_directImportResponsesData.TryGetValue(_selectedResponseName, out var responsesData))
            {
                OlsStatusText = string.Format(_localizationService.GetString("Doe_Analysis_Status_NotFindVar", "未找到响应变量: {0}"),_selectedResponseName);
                return;
            }

            try
            {
                IsLoading = true;
                OlsStatusText = _localizationService.GetString("Doe_Analysis_Status_InOls", "正在进行 OLS 回归分析...");
                StatusMessage = "正在进行 OLS 回归分析...";
                var steps = new[] {
            "Fit OLS model", "Regression equation", "Pareto effects",
            "Residual diagnostics", "Response surface",
            "Effects normal plots", "Outlier diagnostics",
            "Box-Cox analysis", "Tukey HSD"
        };
                InitLoadingSteps(steps);
                int totalSteps = steps.Length;

                // Step 0: Fit OLS
                OlsResult = await Task.Run(() =>
                                    _analysisService.FitOlsDirectAsync(
                                     _directImportFactorsData, responsesData,
                                    _selectedResponseName, _directImportFactorTypes, _currentOlsModelType));

                if (OlsResult?.ModelSummary == null)
                {
                    OlsStatusText = _localizationService.GetString("Doe_Analysis_Status_OlsNoneResult", "OLS 分析返回空结果");
                    return;
                }

                await AdvanceLoadingStepAsync(0, totalSteps);

                OlsStatusText = string.Format(_localizationService.GetString("Doe_Analysis_Status_OlsAnalysisAdj",
                    "OLS 分析完成: R²={0:F4}, R²adj={1:F4}"),
                    OlsResult.ModelSummary.RSquared, OlsResult.ModelSummary.RSquaredAdj);

                InestimableWarning = OlsResult.InestimableWarning ?? "";
                HasInestimableWarning = !string.IsNullOrEmpty(InestimableWarning);

                OlsContinuousFactors = _directImportFactorNames
                    .Where(f => _directImportFactorTypes.GetValueOrDefault(f, "continuous") == "continuous")
                    .ToList();
                if (OlsContinuousFactors.Count >= 2)
                {
                    OlsSurfaceFactor1 = OlsContinuousFactors[0];
                    OlsSurfaceFactor2 = OlsContinuousFactors[1];
                }

                // Step 1: 方程展示
                UpdateEquationsDisplay(OlsResult);
                await AdvanceLoadingStepAsync(1, totalSteps);

                // Step 2: Pareto
                await Task.Run(() => LoadDirectOlsParetoAsync());
                await AdvanceLoadingStepAsync(2, totalSteps);

                // Step 3: 残差四合一
                await Task.Run(() => LoadDirectResidualDiagnosticsAsync());
                await AdvanceLoadingStepAsync(3, totalSteps);

                // Step 4: 响应曲面
                await Task.Run(() => LoadDirectOlsSurfaceAsync());
                await AdvanceLoadingStepAsync(4, totalSteps);

                // Step 5: 标准化效应正态图 + 半正态图
                await LoadDirectEffectsNormalPlotsAsync();
                await AdvanceLoadingStepAsync(5, totalSteps);

                // Step 6: 异常点
                await Task.Run(() => LoadDirectOutlierAnalysisAsync());
                await AdvanceLoadingStepAsync(6, totalSteps);

                // Step 8: Tukey HSD
                if (_directImportFactorTypes.Values.Any(v => v == "categorical"))
                {
                    await Task.Run(() => LoadDirectTukeyHSDAsync());
                }
                else
                {
                    HasTukeyResult = false;
                }
                await AdvanceLoadingStepAsync(8, totalSteps);

                OlsStatusText = string.Format(_localizationService.GetString("Doe_Analysis_Status_AnalysisComp", "OLS 分析完成: R²={0:F4}, R²adj={1:F4}, 来源=直接导入")
                    ,OlsResult.ModelSummary.RSquared,OlsResult.ModelSummary.RSquaredAdj);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "直接导入 OLS 分析失败");
                OlsStatusText = string.Format(_localizationService.GetString("Doe_Analysis_Status_AnaylsisFail", "分析失败: {0}"),ex.Message);
            }
            finally
            {
                IsLoading = false;
            }
        }


        /// <summary>
        /// 直接导入模式下的 Pareto 图
        /// </summary>
        private async Task LoadDirectOlsParetoAsync()
        {
            if (!IsDirectImportMode || string.IsNullOrEmpty(_selectedResponseName)) return;
            try
            {
                var json = await Task.Run(() =>
                    _analysisService.GetEffectsParetoDirectAsync(_selectedResponseName));

                var wrapper = JsonConvert.DeserializeObject<ParetoResult>(json);
                var data = wrapper?.Effects;
                _paretoAlpha = wrapper?.Alpha ?? 0.05;

                var measure = wrapper?.Measure ?? "LogWorth";
                if (measure == "StandardizedEffect")
                {
                    _paretoLogWorthCrit = wrapper?.TCrit ?? wrapper?.LogWorthCrit ?? 4.3;
                    ParetoSubtitle = $"标准化效应的 Pareto 图 (响应为 产率, α = {_paretoAlpha:F2})";
                }
                else
                {
                    _paretoLogWorthCrit = wrapper?.LogWorthCrit ?? 1.301;
                    ParetoSubtitle = "Pareto of LogWorth = −log₁₀(p-value). Click bars to toggle include/exclude.";
                }

                if (data == null || data.Count == 0) return;

                _paretoTerms = data.Select(d => new ParetoTermItem
                {
                    TermName = d.Term,
                    AbsT = d.AbsT,
                    PValue = d.PValue,
                    IsSignificant = d.Significant,
                    IsIncluded = true
                }).ToList();

                RebuildParetoPlot();
                UpdateTermsText();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "直接导入 Pareto 图加载失败");
            }
        }

        /// <summary>
        /// 直接导入模式下的残差诊断
        /// </summary>
        private async Task LoadDirectResidualDiagnosticsAsync()
        {
            if (!IsDirectImportMode || string.IsNullOrEmpty(_selectedResponseName)) return;
            try
            {
                var json = await Task.Run(() =>
                    _analysisService.GetResidualDiagnosticsDirectAsync(_selectedResponseName));
                var diag = JsonConvert.DeserializeObject<ResidualDiagnostics>(json);
                if (diag == null) return;
                BuildResidualPlotsFromData(diag);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "直接导入残差诊断失败");
            }
        }
        /// <summary>误差函数近似（Abramowitz & Stegun 7.1.26）</summary>
        private static double Erf(double x)
        {
            double t = 1.0 / (1.0 + 0.3275911 * Math.Abs(x));
            double y = 1.0 - (((((1.061405429 * t - 1.453152027) * t) + 1.421413741) * t - 0.284496736) * t + 0.254829592) * t * Math.Exp(-x * x);
            return x < 0 ? -y : y;
        }
        // ═══ 直接导入模式: 响应曲面 ═══
        private async Task LoadDirectOlsSurfaceAsync()
        {
            if (!IsDirectImportMode || string.IsNullOrEmpty(_selectedResponseName)) return;
            if (string.IsNullOrEmpty(OlsSurfaceFactor1) || string.IsNullOrEmpty(OlsSurfaceFactor2)
                || OlsSurfaceFactor1 == OlsSurfaceFactor2) return;

            IsLoading = true;
            StatusMessage = "正在生成响应曲面...";

            try
            {
                // 构建 bounds JSON
                var boundsDict = new Dictionary<string, object>();
                foreach (var factorRow in _directImportFactorsData)
                {
                    foreach (var kv in factorRow)
                    {
                        if (!boundsDict.ContainsKey(kv.Key))
                        {
                            if (_directImportFactorTypes.GetValueOrDefault(kv.Key, "continuous") == "categorical")
                            {
                                boundsDict[kv.Key] = _directImportFactorsData
                                    .Select(r => r[kv.Key]?.ToString() ?? "").Distinct().OrderBy(v => v).ToList();
                            }
                            else
                            {
                                var vals = _directImportFactorsData.Select(r => Convert.ToDouble(r[kv.Key])).ToList();
                                boundsDict[kv.Key] = new[] { vals.Min(), vals.Max() };
                            }
                        }
                    }
                }

                // ★ 固定因子：覆盖 bounds 为单点值
                foreach (var sf in SurfaceFixedFactors)
                    boundsDict[sf.Name] = new[] { sf.Value, sf.Value };

                var boundsJson = JsonConvert.SerializeObject(boundsDict);

                var contourJson = await Task.Run(() =>
                    _analysisService.GetOlsContourDataDirectAsync(
                        OlsSurfaceFactor1, OlsSurfaceFactor2, boundsJson));

                var contourData = JsonConvert.DeserializeObject<SurfaceData>(contourJson);
                if (contourData?.Z != null)
                {
                    BuildContourPlot(contourData);
                    OlsSurface3DData = new Surface3DData
                    {
                        X = contourData.X.ToArray(),
                        Y = contourData.Y.ToArray(),
                        Z = contourData.Z.Select(row => row.ToArray()).ToArray(),
                        XLabel = OlsSurfaceFactor1,
                        YLabel = OlsSurfaceFactor2
                    };
                }

                OlsSurfaceImage = null;

                if (SurfaceFixedFactors.Count > 0)
                {
                    var fixedStr = string.Join(", ", SurfaceFixedFactors.Select(sf => $"{sf.Name}={sf.Value:F2}"));
                    OlsStatusText = string.Format(_localizationService.GetString("Doe_Analysis_Status_RespGenerate", "响应曲面已生成（固定: {0}）"),fixedStr);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "直接导入 OLS 响应曲面加载失败");
            }
            finally
            {
                IsLoading = false;
            }
        }

        // ═══ 直接导入模式: 异常点分析 ═══
        private async Task LoadDirectOutlierAnalysisAsync()
        {
            if (!IsDirectImportMode || string.IsNullOrEmpty(_selectedResponseName)) return;
            try
            {
                var json = await Task.Run(() =>
                    _analysisService.GetOutlierAnalysisDirectAsync(_selectedResponseName));
                var data = JsonConvert.DeserializeObject<OutlierAnalysisResult>(json);
                if (data == null) return;

                OutlierItems = data.Outliers.Select(o => new OutlierItem
                {
                    Index = o.Index,
                    Actual = o.Actual,
                    Predicted = o.Predicted,
                    Residual = o.Residual,
                    StdResidual = o.StdResidual,
                    CooksD = o.CooksD,
                    Leverage = o.Leverage,
                    Reasons = string.Join("; ", o.Reasons),
                    IsExcluded = false
                }).ToList();

                HasOutliers = data.OutlierCount > 0;
                OutlierSummaryText = data.OutlierCount > 0
                    ? $"检测到 {data.OutlierCount} 个异常点（共 {data.TotalObservations} 个观测）"
                    : $"未检测到异常点（共 {data.TotalObservations} 个观测）";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "直接导入异常点分析失败");
                HasOutliers = false;
            }
        }

     
     
        // ═══ 直接导入模式: Tukey HSD ═══
        private async Task LoadDirectTukeyHSDAsync()
        {
            if (!IsDirectImportMode || string.IsNullOrEmpty(_selectedResponseName)) return;
            var catFactor = _directImportFactorTypes.FirstOrDefault(kv => kv.Value == "categorical").Key;
            if (string.IsNullOrEmpty(catFactor)) { HasTukeyResult = false; return; }

            try
            {
                var json = await Task.Run(() =>
                    _analysisService.GetTukeyHSDDirectAsync(_selectedResponseName, catFactor));

                var data = JsonConvert.DeserializeObject<TukeyHSDResult>(json);
                if (data == null || data.Error != null) { HasTukeyResult = false; return; }

                TukeyFactorName = data.FactorName;
                TukeyComparisons = data.Comparisons.Select(c => new TukeyComparisonItem
                {
                    Group1 = c.Group1,
                    Group2 = c.Group2,
                    MeanDiff = c.MeanDiff,
                    PValue = c.PValue,
                    CILower = c.CILower,
                    CIUpper = c.CIUpper,
                    Significant = c.Significant
                }).ToList();
                _tukeyGroupMeans = data.GroupMeans;
                HasTukeyResult = true;
            }
            catch (Exception ex) { _logger.LogError(ex, "直接导入 Tukey HSD 失败"); HasTukeyResult = false; }
        }
        /// <summary>
        /// 读取 Excel 文件用于直接 OLS 分析
        /// 自动识别: 尝试将每列转为数值，全部可转 → 连续因子/响应；否则 → 类别因子
        /// 规则: 最后 N 列全为数值 → 作为响应变量，其余为因子
        /// 但如果 Excel 只有 2 列数值，则最后一列为响应
        /// </summary>
        /// <summary>
        /// 读取 Excel 文件的原始列数据（不做角色判断，由弹窗确认）
        /// </summary>
        private static DirectImportRawData ReadExcelRawData(string path)
        {
            var result = new DirectImportRawData();
            try
            {
                using var package = new ExcelPackage(new FileInfo(path));
                var ws = package.Workbook.Worksheets.FirstOrDefault();
                if (ws == null || ws.Dimension == null)
                {
                    result.Errors.Add("Excel 文件为空或没有工作表");
                    return result;
                }

                int totalCols = ws.Dimension.End.Column;
                int totalRows = ws.Dimension.End.Row;

                if (totalRows < 2)
                {
                    result.Errors.Add("至少需要 1 行列头 + 1 行数据");
                    return result;
                }

                // 读取列头
                for (int col = 1; col <= totalCols; col++)
                {
                    var header = ws.Cells[1, col].Text?.Trim();
                    if (string.IsNullOrEmpty(header)) header = $"Col{col}";
                    result.Headers.Add(header);
                    result.ColumnData.Add(new List<object?>());
                    result.IsNumericColumn.Add(true);
                }

                // 读取数据
                for (int row = 2; row <= totalRows; row++)
                {
                    bool allEmpty = true;
                    for (int c = 0; c < totalCols; c++)
                    {
                        var cellText = ws.Cells[row, c + 1].Text?.Trim() ?? "";
                        if (!string.IsNullOrEmpty(cellText)) allEmpty = false;

                        if (double.TryParse(cellText, out var numVal))
                        {
                            result.ColumnData[c].Add(numVal);
                        }
                        else if (string.IsNullOrEmpty(cellText))
                        {
                            result.ColumnData[c].Add(null);
                        }
                        else
                        {
                            result.ColumnData[c].Add(cellText);
                            result.IsNumericColumn[c] = false;
                        }
                    }
                    if (allEmpty) break;
                }

                result.DataRowCount = result.ColumnData[0].Count;
            }
            catch (Exception ex)
            {
                result.Errors.Add($"读取 Excel 失败: {ex.Message}");
            }
            return result;
        }
        /// <summary>
        /// 在预测刻画器图列表末尾追加/移除 "综合D" 曲线图
        /// </summary>
        private async Task RebuildProfilerWithDesirabilityAsync()
        {
            if (!IsProfilerLoaded) return;

            if (IsDesirabilityVisible)
            {
                var batchId = IsDirectImportMode ? "" : SelectedOlsBatch?.BatchId ?? _currentBatchId;
                _currentDesirabilityConfigs = await _desirabilityService.LoadConfigAsync(batchId);

                if (_currentDesirabilityConfigs.Count == 0)
                {
                    _dialogService.ShowError("请先通过「设置意愿」配置 Desirability 参数", "缺少配置");
                    _isDesirabilityVisible = false;
                    RaisePropertyChanged(nameof(IsDesirabilityVisible));
                    return;
                }

                // 多响应时确保 predictor 已构建
                if (_isMultiResponseProfiler && _multiResponsePredictor == null)
                {
                    await BuildMultiResponsePredictorForProfiler();
                }

                BuildDesirabilityPlotsFromProfiler();
            }
            else
            {
                DesirabilityPlots = new List<PlotModel>();
            }
        }
        /// <summary>
        /// 从外部直接跳转到 OLS 分析指定批次
        /// （历史页"查看分析"按钮调用）
        /// </summary>
        public async Task NavigateToOlsBatchAsync(string batchId)
        {
            try
            {
                IsLoading = true;

                // 切换到 OLS Tab
                IsGprTabSelected = false;

                // 加载批次列表
                await LoadOlsBatchListAsync();

                // 在批次列表中找到目标批次并选中
                var target = OlsBatchItems.FirstOrDefault(b => b.BatchId == batchId);
                if (target != null)
                {
                    await SelectOlsBatchAsync(target);
                }
                else
                {
                    // 批次不在列表中（可能是不同 FlowId），直接用 BatchId 加载
                    var batch = await _repository.GetBatchWithDetailsAsync(batchId);
                    if (batch != null)
                    {
                        _currentBatchId = batchId;
                        _currentFlowId = batch.FlowId;

                        // 更新因子和响应名
                        FactorNames = batch.Factors.Select(f => f.FactorName).ToList();
                        SetResponseNames(batch.Responses.Select(r => r.ResponseName).ToList());
                        _selectedResponseName = ResponseNames.FirstOrDefault() ?? "";
                        RaisePropertyChanged(nameof(SelectedResponseName));

                        // 构造一个 OlsBatchItem 并选中
                        var item = new OlsBatchItem
                        {
                            BatchId = batch.BatchId,
                            BatchName = batch.BatchName,
                            DesignMethod = batch.DesignMethod.ToString(),
                            CompletedCount = batch.Runs?.Count(r => r.Status == DOERunStatus.Completed) ?? 0,
                            FactorCount = batch.Factors?.Count ?? 0,
                            IsSelected = true
                        };

                        // 加到列表头部
                        OlsBatchItems.Insert(0, item);
                        SelectedOlsBatch = item;
                        // ★ 新增：从 DesignConfigJson 解析 olsModelType
                        // ★ 根据设计方法设置正确的默认模型类型
                        _currentOlsModelType = batch?.DesignMethod switch
                        {
                            DOEDesignMethod.PlackettBurman => "linear",           // PB 只拟合主效应
                            DOEDesignMethod.FractionalFactorial => "interaction",  // 部分因子: 主效应+2fi
                            DOEDesignMethod.FullFactorial => "interaction",        // 全因子: 主效应+所有交互
                            DOEDesignMethod.CCD => "quadratic",                    // CCD: 二阶模型
                            DOEDesignMethod.BoxBehnken => "quadratic",             // BB: 二阶模型
                            _ => "quadratic"                                        // 其他: 默认二阶
                        };
                        var currentResponse = batch?.Responses?.FirstOrDefault(r => r.ResponseName == _selectedResponseName);
                        UpdateModelOrderSelector(batch, currentResponse?.ModelOrder);
                        // 如果 DesignConfigJson 里显式指定了 olsModelType, 优先用它
                        if (!string.IsNullOrEmpty(batch?.DesignConfigJson))
                        {
                            try
                            {
                                var config = JsonConvert.DeserializeObject<Dictionary<string, object>>(batch.DesignConfigJson);
                                if (config != null && config.TryGetValue("olsModelType", out var mt) && mt != null)
                                    _currentOlsModelType = mt.ToString()!;
                            }
                            catch { }
                        }
                        if (!string.IsNullOrEmpty(batch.DesignConfigJson))
                        {
                            try
                            {
                                var config = JsonConvert.DeserializeObject<Dictionary<string, object>>(batch.DesignConfigJson);
                                if (config != null && config.TryGetValue("olsModelType", out var mt) && mt != null)
                                    _currentOlsModelType = mt.ToString()!;
                            }
                            catch { }
                        }
                        // ★ v17: 全因子设计自动使用 interaction 模型
                        if (_currentOlsModelType == "quadratic" && batch.DesignMethod == DOEDesignMethod.FullFactorial)
                        {
                            _currentOlsModelType = "interaction";
                        }
                        // 执行 OLS 分析
                        await RefreshOlsAnalysisAsync();
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "跳转 OLS 分析失败: {BatchId}", batchId);
                OlsStatusText = string.Format(_localizationService.GetString("Doe_Analysis_Status_LoadFail", "加载失败: {}"),ex.Message);
            }
            finally
            {
                IsLoading = false;
            }
        }

        /// <summary>
        /// ★ 多响应版: 构建意愿行图表
        /// 
        /// 单响应: 保持原有逻辑（一行 d 曲线 + 意愿函数图）
        /// 多响应:
        ///   - 每个响应一行 d 曲线（d₁, d₂, ...）
        ///   - 最后一行 Composite D 曲线
        ///   - 不显示意愿函数图（多响应时不适用单个函数图）
        /// </summary>
        private void BuildDesirabilityPlotsFromProfiler()
        {
            if (!_isMultiResponseProfiler)
            {
                // ═══ 单响应: 保持原有逻辑 ═══
                BuildDesirabilityPlotsFromProfiler_Single();
                return;
            }

            // ═══ 多响应: 每个响应一行 d + Composite D 行 ═══
            if (_multiProfilerData.Count == 0 || _currentDesirabilityConfigs == null) return;
            if (_multiResponsePredictor == null) return;

            var allPlots = new List<PlotModel>();
            int nFactors = _profilerFactorOrder.Count;

            // 构建当前因子值
            var contValues = new Dictionary<string, double>();
            var catValues = new Dictionary<string, string>();
            foreach (var fname in _profilerFactorOrder)
            {
                if (IsProfilerFactorCategorical(fname))
                    catValues[fname] = _profilerCurrentValues.TryGetValue(fname, out var v) ? v?.ToString() ?? "" : "";
                else
                    contValues[fname] = _profilerCurrentValues.TryGetValue(fname, out var v) ? Convert.ToDouble(v) : 0;
            }

            // ── 为每个响应构建 d 行 ──
            var individualDs = new Dictionary<string, double>(); // 当前各响应的 d 值
            foreach (var cfg in _currentDesirabilityConfigs)
            {
                if (!_multiProfilerData.TryGetValue(cfg.ResponseName, out var profData)) continue;

                double currentPred = _multiResponsePredictor.Predict(cfg.ResponseName, contValues, catValues);
                double currentD = ComputeDesirabilityForConfig(currentPred, cfg);
                individualDs[cfg.ResponseName] = currentD;

                foreach (var fname in _profilerFactorOrder)
                {
                    if (!profData.Factors.TryGetValue(fname, out var fdata)) continue;

                    var pm = new PlotModel
                    {
                        Title = fname,
                        Subtitle = fdata.IsCategorical
                            ? $"d={currentD:F2}"
                            : $"d={currentD:F2}",
                        PlotMargins = new OxyThickness(50, 12, 10, 30)
                    };

                    pm.Axes.Add(new LinearAxis
                    {
                        Position = AxisPosition.Left,
                        Minimum = -0.05,
                        Maximum = 1.05,
                        Title = fname == _profilerFactorOrder[0] ? $"d({cfg.ResponseName})" : ""
                    });

                    // X 轴
                    if (fdata.IsCategorical)
                    {
                        var labels = fdata.Levels ?? fdata.X_Labels;
                        pm.Axes.Add(new LinearAxis
                        {
                            Position = AxisPosition.Bottom,
                            Minimum = -0.5,
                            Maximum = labels.Count - 0.5,
                            MajorStep = 1,
                            LabelFormatter = val =>
                            {
                                int idx = (int)Math.Round(val);
                                return idx >= 0 && idx < labels.Count ? labels[idx] : "";
                            }
                        });

                        // d 值散点
                        var scatter = new LineSeries { Color = OxyColor.FromRgb(0x2E, 0x86, 0xC1), StrokeThickness = 1.5 };
                        for (int li = 0; li < labels.Count; li++)
                        {
                            var tempCat = new Dictionary<string, string>(catValues) { [fname] = labels[li] };
                            double pred = _multiResponsePredictor.Predict(cfg.ResponseName, contValues, tempCat);
                            double d = ComputeDesirabilityForConfig(pred, cfg);
                            scatter.Points.Add(new DataPoint(li, d));
                        }
                        pm.Series.Add(scatter);

                        // 当前水平红线
                        var curVal = _profilerCurrentValues.TryGetValue(fname, out var cv) ? cv?.ToString() : "";
                        int curIdx = labels.IndexOf(curVal ?? "");
                        if (curIdx >= 0)
                        {
                            pm.Annotations.Add(new LineAnnotation
                            {
                                Type = LineAnnotationType.Vertical,
                                X = curIdx,
                                Color = OxyColor.FromRgb(198, 40, 40),
                                StrokeThickness = 1
                            });
                        }
                    }
                    else
                    {
                        pm.Axes.Add(new LinearAxis
                        {
                            Position = AxisPosition.Bottom,
                            Minimum = fdata.X.Count > 0 ? fdata.X.First() : 0,
                            Maximum = fdata.X.Count > 0 ? fdata.X.Last() : 1
                        });

                        // d(y(x)) 曲线
                        var line = new LineSeries { Color = OxyColor.FromRgb(0x2E, 0x86, 0xC1), StrokeThickness = 1.5 };
                        foreach (var xVal in fdata.X)
                        {
                            var tempCont = new Dictionary<string, double>(contValues) { [fname] = xVal };
                            double pred = _multiResponsePredictor.Predict(cfg.ResponseName, tempCont, catValues);
                            double d = ComputeDesirabilityForConfig(pred, cfg);
                            line.Points.Add(new DataPoint(xVal, d));
                        }
                        pm.Series.Add(line);

                        // 当前位置红线
                        double curX = contValues.TryGetValue(fname, out var cx) ? cx : 0;
                        pm.Annotations.Add(new LineAnnotation
                        {
                            Type = LineAnnotationType.Vertical,
                            X = curX,
                            Color = OxyColor.FromRgb(198, 40, 40),
                            StrokeThickness = 1
                        });
                    }

                    // 当前 d 值水平线
                    pm.Annotations.Add(new LineAnnotation
                    {
                        Type = LineAnnotationType.Horizontal,
                        Y = currentD,
                        Color = OxyColor.FromArgb(60, 198, 40, 40),
                        LineStyle = LineStyle.Dot,
                        StrokeThickness = 0.8
                    });

                    ApplyJmpProfilerStyle(pm);
                    allPlots.Add(pm);
                }
            }

            // ── Composite D 行 ──
            double compositeD = ComputeCurrentCompositeD(contValues, catValues);
            DesirabilityCompositeD = compositeD;

            // 取第一个响应的 factor data 作为 X 轴参考
            var refData = _multiProfilerData.Values.First();

            foreach (var fname in _profilerFactorOrder)
            {
                if (!refData.Factors.TryGetValue(fname, out var fdata)) continue;

                var pm = new PlotModel
                {
                    Title = fname,
                    Subtitle = $"D={compositeD:F4}",
                    PlotMargins = new OxyThickness(50, 12, 10, 30)
                };

                pm.Axes.Add(new LinearAxis
                {
                    Position = AxisPosition.Left,
                    Minimum = -0.05,
                    Maximum = 1.05,
                    Title = fname == _profilerFactorOrder[0] ? "Composite D" : ""
                });

                if (fdata.IsCategorical)
                {
                    var labels = fdata.Levels ?? fdata.X_Labels;
                    pm.Axes.Add(new LinearAxis
                    {
                        Position = AxisPosition.Bottom,
                        Minimum = -0.5,
                        Maximum = labels.Count - 0.5,
                        MajorStep = 1,
                        LabelFormatter = val =>
                        {
                            int idx = (int)Math.Round(val);
                            return idx >= 0 && idx < labels.Count ? labels[idx] : "";
                        }
                    });

                    var scatter = new LineSeries
                    {
                        Color = OxyColor.FromRgb(0xE8, 0xA5, 0x40),
                        StrokeThickness = 2
                    };
                    for (int li = 0; li < labels.Count; li++)
                    {
                        var tempCat = new Dictionary<string, string>(catValues) { [fname] = labels[li] };
                        double D = ComputeCurrentCompositeD(contValues, tempCat);
                        scatter.Points.Add(new DataPoint(li, D));
                    }
                    pm.Series.Add(scatter);

                    var curVal = _profilerCurrentValues.TryGetValue(fname, out var cv) ? cv?.ToString() : "";
                    int curIdx = labels.IndexOf(curVal ?? "");
                    if (curIdx >= 0)
                        pm.Annotations.Add(new LineAnnotation { Type = LineAnnotationType.Vertical, X = curIdx, Color = OxyColor.FromRgb(198, 40, 40), StrokeThickness = 1 });
                }
                else
                {
                    pm.Axes.Add(new LinearAxis
                    {
                        Position = AxisPosition.Bottom,
                        Minimum = fdata.X.Count > 0 ? fdata.X.First() : 0,
                        Maximum = fdata.X.Count > 0 ? fdata.X.Last() : 1
                    });

                    var line = new LineSeries
                    {
                        Color = OxyColor.FromRgb(0xE8, 0xA5, 0x40),
                        StrokeThickness = 2
                    };
                    foreach (var xVal in fdata.X)
                    {
                        var tempCont = new Dictionary<string, double>(contValues) { [fname] = xVal };
                        double D = ComputeCurrentCompositeD(tempCont, catValues);
                        line.Points.Add(new DataPoint(xVal, D));
                    }
                    pm.Series.Add(line);

                    double curX = contValues.TryGetValue(fname, out var cx) ? cx : 0;
                    pm.Annotations.Add(new LineAnnotation { Type = LineAnnotationType.Vertical, X = curX, Color = OxyColor.FromRgb(198, 40, 40), StrokeThickness = 1 });
                }

                // 当前 D 水平线
                pm.Annotations.Add(new LineAnnotation
                {
                    Type = LineAnnotationType.Horizontal,
                    Y = compositeD,
                    Color = OxyColor.FromArgb(60, 198, 40, 40),
                    LineStyle = LineStyle.Dot,
                    StrokeThickness = 0.8
                });

                ApplyJmpProfilerStyle(pm);
                allPlots.Add(pm);
            }

            DesirabilityPlots = allPlots;
            int nF = _profilerFactorOrder.Count;
            int totalRows = allPlots.Count / nF;
            var dRows = new List<ProfilerRowViewModel>();
            for (int r = 0; r < totalRows; r++)
            {
                string label;
                if (r < _currentDesirabilityConfigs.Count)
                {
                    var cfg = _currentDesirabilityConfigs[r];
                    double currentPred = _multiResponsePredictor!.Predict(cfg.ResponseName, contValues, catValues);
                    double currentD = ComputeDesirabilityForConfig(currentPred, cfg);
                    label = $"d({cfg.ResponseName})    {currentD:F4}";
                }
                else
                {
                    label = $"Composite D    {compositeD:F4}";
                }

                dRows.Add(new ProfilerRowViewModel
                {
                    ResponseName = label,
                    ResponseLabel = label,
                    Plots = allPlots.Skip(r * nF).Take(nF).ToList()
                });
            }
            DesirabilityRows = dRows;
        }
        /// <summary>
        /// 原有的单响应意愿行构建逻辑（不变，改名保留）
        /// </summary>
        private void BuildDesirabilityPlotsFromProfiler_Single()
        {
            if (_lastProfilerData?.Factors == null) return;

            DesirabilityPlots = new List<PlotModel>();
            var plots = new List<PlotModel>();

            double currentCompositeD = ComputeDesirabilityForCurrentResponse(
                _lastProfilerData.CurrentPredicted);
            DesirabilityCompositeD = currentCompositeD;

            foreach (var fname in _profilerFactorOrder)
            {
                if (!_lastProfilerData.Factors.TryGetValue(fname, out var fdata)) continue;

                string dSubtitle = fdata.IsCategorical
                    ? $"d={currentCompositeD:F2}"
                    : $"d={currentCompositeD:F2}";

                var pm = new PlotModel
                {
                    Title = fname,
                    Subtitle = dSubtitle,
                    PlotMargins = new OxyThickness(50, 12, 10, 30)
                };

                pm.Axes.Add(new LinearAxis
                {
                    Position = AxisPosition.Left,
                    Minimum = -0.05,
                    Maximum = 1.05,
                    Title = fname == _profilerFactorOrder[0] ? "d" : ""
                });

                if (fdata.IsCategorical)
                {
                    var labels = fdata.Levels ?? fdata.X_Labels;
                    pm.Axes.Add(new LinearAxis
                    {
                        Position = AxisPosition.Bottom,
                        Minimum = -0.5,
                        Maximum = labels.Count - 0.5,
                        MajorStep = 1,
                        LabelFormatter = val =>
                        {
                            int idx = (int)Math.Round(val);
                            return idx >= 0 && idx < labels.Count ? labels[idx] : "";
                        }
                    });

                    var scatter = new LineSeries { Color = OxyColor.FromRgb(0x2E, 0x86, 0xC1), StrokeThickness = 1.5 };
                    for (int li = 0; li < labels.Count; li++)
                    {
                        double yVal = fdata.Y.Count > li ? fdata.Y[li] : 0;
                        double d = ComputeDesirabilityForCurrentResponse(yVal);
                        scatter.Points.Add(new DataPoint(li, d));
                    }
                    pm.Series.Add(scatter);

                    var curVal = _profilerCurrentValues.TryGetValue(fname, out var cv) ? cv?.ToString() : "";
                    int curIdx = labels.IndexOf(curVal ?? "");
                    if (curIdx >= 0)
                        pm.Annotations.Add(new LineAnnotation { Type = LineAnnotationType.Vertical, X = curIdx, Color = OxyColor.FromRgb(198, 40, 40), StrokeThickness = 1 });
                }
                else
                {
                    pm.Axes.Add(new LinearAxis { Position = AxisPosition.Bottom });

                    var line = new LineSeries { Color = OxyColor.FromRgb(0x2E, 0x86, 0xC1), StrokeThickness = 1.5 };
                    for (int j = 0; j < fdata.X.Count; j++)
                    {
                        double d = ComputeDesirabilityForCurrentResponse(fdata.Y[j]);
                        line.Points.Add(new DataPoint(fdata.X[j], d));
                    }
                    pm.Series.Add(line);

                    double curX = fdata.CurrentNumericValue;
                    pm.Annotations.Add(new LineAnnotation { Type = LineAnnotationType.Vertical, X = curX, Color = OxyColor.FromRgb(198, 40, 40), StrokeThickness = 1 });
                }

                pm.Annotations.Add(new LineAnnotation
                {
                    Type = LineAnnotationType.Horizontal,
                    Y = currentCompositeD,
                    Color = OxyColor.FromArgb(60, 198, 40, 40),
                    LineStyle = LineStyle.Dot,
                    StrokeThickness = 0.8
                });

                ApplyJmpProfilerStyle(pm);
                plots.Add(pm);
            }

            // 意愿函数图（最右边）
            BuildDesirabilityFunctionPlot(plots, currentCompositeD);

            DesirabilityPlots = plots;
            // ★ 单响应: DesirabilityRows 只有一行 (不含意愿函数图, 它在 plots 最后)
            DesirabilityRows = new List<ProfilerRowViewModel>
{
    new ProfilerRowViewModel
    {
        ResponseName = "d",
        ResponseLabel = $"d    {currentCompositeD:F4}",
        // plots 的最后一个是意愿函数图, 单响应时全部放进来
        Plots = plots
    }
};
        }
        /// <summary>
        /// 计算给定因子值下的 Composite Desirability
        /// D = (d₁^w₁ × d₂^w₂ × ...)^(1/Σwⱼ)
        /// </summary>
        private double ComputeCurrentCompositeD(
            Dictionary<string, double> contValues,
            Dictionary<string, string> catValues)
        {
            if (_currentDesirabilityConfigs == null || _multiResponsePredictor == null)
                return 0;

            double product = 1;
            double sumW = 0;
            foreach (var cfg in _currentDesirabilityConfigs)
            {
                double pred = _multiResponsePredictor.Predict(cfg.ResponseName, contValues, catValues);
                double d = ComputeDesirabilityForConfig(pred, cfg);
                double w = cfg.Importance;
                product *= Math.Pow(Math.Max(d, 1e-15), w);
                sumW += w;
            }
            return sumW > 0 ? Math.Pow(product, 1.0 / sumW) : 0;
        }
        /// <summary>
        /// ★ 新增: 公开的 Desirability 计算方法（供 code-behind 拖动时调用）
        /// </summary>
        public double ComputeDesirabilityPublic(double y)
        {
            return ComputeDesirabilityForCurrentResponse(y);
        }
        /// <summary>
        /// ★ 新增: 从意愿行拖动同步更新响应行的红线和 Subtitle（不触发后端调用）
        /// </summary>
        public void UpdateProfilerCrosshairFromDesirability(string factorName, double newX)
        {
            if (ProfilerPlots == null || _profilerFactorOrder == null) return;

            int idx = _profilerFactorOrder.IndexOf(factorName);
            if (idx < 0 || idx >= ProfilerPlots.Count) return;

            var pm = ProfilerPlots[idx];

            // 更新红色竖线位置
            var vline = pm.Annotations
                .OfType<OxyPlot.Annotations.LineAnnotation>()
                .FirstOrDefault(a => a.Type == OxyPlot.Annotations.LineAnnotationType.Vertical
                                     && a.Color == OxyColors.Red);
            if (vline != null)
                vline.X = newX;

            // 更新 Subtitle
            pm.Subtitle = $"{newX:F2}";
            pm.SubtitleColor = OxyColors.Red;
            pm.SubtitleFontSize = 10;

            // 更新水平横线（预测值）
            double? responseY = GetProfilerYAtX(factorName, newX);
            if (responseY.HasValue)
            {
                var hline = pm.Annotations
                    .OfType<OxyPlot.Annotations.LineAnnotation>()
                    .FirstOrDefault(a => a.Type == OxyPlot.Annotations.LineAnnotationType.Horizontal);
                if (hline != null)
                    hline.Y = responseY.Value;
            }

            pm.InvalidatePlot(false);
        }
        /// <summary>
        /// ★ Feature 2: 获取意愿行中的因子名称（排除最后的"意愿"图）
        /// </summary>
        public string? GetDesirabilityFactorName(int plotIndex)
        {
            if (plotIndex >= 0 && plotIndex < _profilerFactorOrder.Count)
                return _profilerFactorOrder[plotIndex];
            return null; // 最后一个图是"意愿图"，返回 null
        }
        /// <summary>
        /// ★ Feature 2: 原地刷新意愿行图表（拖拽时调用，避免重建闪烁）
        /// </summary>
        public void RefreshDesirabilityPlotsInPlace()
        {
            if (_lastProfilerData?.Factors == null) return;
            if (DesirabilityPlots == null || DesirabilityPlots.Count == 0) return;

            double currentCompositeD = ComputeDesirabilityForCurrentResponse(
                _lastProfilerData.CurrentPredicted);
            DesirabilityCompositeD = currentCompositeD;

            for (int i = 0; i < _profilerFactorOrder.Count && i < DesirabilityPlots.Count; i++)
            {
                var fname = _profilerFactorOrder[i];
                if (!_lastProfilerData.Factors.TryGetValue(fname, out var fdata)) continue;

                var pm = DesirabilityPlots[i];

                // 更新 Subtitle
                if (fdata.IsCategorical)
                {
                    var curVal = _profilerCurrentValues.TryGetValue(fname, out var cv) ? cv?.ToString() : "";
                    pm.Subtitle = curVal ?? "";
                }
                else
                {
                    pm.Subtitle = $"{fdata.CurrentNumericValue:F2}";
                }

                pm.Series.Clear();
                pm.Annotations.Clear();

                if (fdata.IsCategorical)
                {
                    var labels = fdata.X_Labels;
                    var line = new LineSeries { Color = OxyColor.FromRgb(26, 31, 54), StrokeThickness = 1.2 };
                    for (int j = 0; j < labels.Count && j < fdata.Y.Count; j++)
                    {
                        double d = ComputeDesirabilityForCurrentResponse(fdata.Y[j]);
                        line.Points.Add(new DataPoint(j, d));
                    }
                    pm.Series.Add(line);

                    var curVal = _profilerCurrentValues.TryGetValue(fname, out var cv) ? cv?.ToString() : "";
                    int curIdx = labels.IndexOf(curVal ?? "");
                    if (curIdx >= 0)
                    {
                        pm.Annotations.Add(new OxyPlot.Annotations.LineAnnotation
                        {
                            Type = OxyPlot.Annotations.LineAnnotationType.Vertical,
                            X = curIdx,
                            Color = OxyColor.FromRgb(198, 40, 40),
                            LineStyle = LineStyle.Solid,
                            StrokeThickness = 1
                        });
                    }
                }
                else
                {
                    var line = new LineSeries { Color = OxyColor.FromRgb(26, 31, 54), StrokeThickness = 1.2 };
                    for (int j = 0; j < fdata.X.Count && j < fdata.Y.Count; j++)
                    {
                        double d = ComputeDesirabilityForCurrentResponse(fdata.Y[j]);
                        line.Points.Add(new DataPoint(fdata.X[j], d));
                    }
                    pm.Series.Add(line);

                    double curX = fdata.CurrentNumericValue;
                    pm.Annotations.Add(new LineAnnotation
                    {
                        Type = LineAnnotationType.Vertical,
                        X = curX,
                        Color = OxyColor.FromRgb(198, 40, 40),
                        LineStyle = LineStyle.Solid,
                        StrokeThickness = 1
                    });
                }

                // D 值水平线
                pm.Annotations.Add(new LineAnnotation
                {
                    Type = LineAnnotationType.Horizontal,
                    Y = currentCompositeD,
                    Color = OxyColor.FromArgb(60, 198, 40, 40),
                    LineStyle = LineStyle.Dot,
                    StrokeThickness = 0.8
                });

                // 更新左侧标签
                if (i == 0)
                {
                    var yAxis = pm.Axes.FirstOrDefault(a => a.Position == AxisPosition.Left);
                    if (yAxis != null) yAxis.Title = $"Desirability\n{currentCompositeD:F4}";
                }

                pm.InvalidatePlot(true);
            }
        }

        /// <summary>
        /// ★ 构建最右边的意愿函数图（带可拖拽控制点）
        /// </summary>
        private void BuildDesirabilityFunctionPlot(List<PlotModel> plots, double currentCompositeD)
        {
            var cfg = _currentDesirabilityConfigs
                .FirstOrDefault(c => c.ResponseName == _selectedResponseName);

            var dPlot = new PlotModel
            {
                Title = "Desirability",
                Subtitle = $"{currentCompositeD:F4}",
                PlotMargins = new OxyThickness(50, 12, 10, 30)
            };

            dPlot.Axes.Add(new LinearAxis
            {
                Position = AxisPosition.Left,
                Minimum = -0.05,
                Maximum = 1.05
            });

            if (cfg != null)
            {
                double yMin = cfg.Lower - (cfg.Upper - cfg.Lower) * 0.1;
                double yMax = cfg.Upper + (cfg.Upper - cfg.Lower) * 0.1;

                dPlot.Axes.Add(new LinearAxis
                {
                    Position = AxisPosition.Bottom,
                    Minimum = yMin,
                    Maximum = yMax,
                    Title = "y"
                });

                // d(y) 曲线
                var curveSeries = new LineSeries { Color = OxyColor.FromRgb(26, 31, 54), StrokeThickness = 1.2 };
                int nPts = 100;
                for (int i = 0; i <= nPts; i++)
                {
                    double y = yMin + (yMax - yMin) * i / nPts;
                    double d = ComputeDesirabilityForCurrentResponse(y);
                    curveSeries.Points.Add(new DataPoint(y, d));
                }
                dPlot.Series.Add(curveSeries);

                // 控制点
                var controlPoints = new ScatterSeries
                {
                    MarkerType = MarkerType.Square,
                    MarkerSize = 5,
                    MarkerFill = OxyColors.White,
                    MarkerStroke = OxyColor.FromRgb(96, 96, 96),
                    MarkerStrokeThickness = 1
                };

                switch (cfg.Goal)
                {
                    case DesirabilityGoal.Maximize:
                        controlPoints.Points.Add(new ScatterPoint(cfg.Lower, 0));
                        controlPoints.Points.Add(new ScatterPoint(cfg.Target, 1));
                        break;
                    case DesirabilityGoal.Minimize:
                        controlPoints.Points.Add(new ScatterPoint(cfg.Target, 1));
                        controlPoints.Points.Add(new ScatterPoint(cfg.Upper, 0));
                        break;
                    default:
                        controlPoints.Points.Add(new ScatterPoint(cfg.Lower, 0));
                        controlPoints.Points.Add(new ScatterPoint(cfg.Target, 1));
                        controlPoints.Points.Add(new ScatterPoint(cfg.Upper, 0));
                        break;
                }
                dPlot.Series.Add(controlPoints);

                // L / U 标注线
                var ciColor = OxyColor.FromArgb(80, 0x4A, 0x7F, 0xC4);
                dPlot.Annotations.Add(new OxyPlot.Annotations.LineAnnotation
                {
                    Type = OxyPlot.Annotations.LineAnnotationType.Vertical,
                    X = cfg.Lower,
                    Color = ciColor,
                    LineStyle = LineStyle.Dot,
                    StrokeThickness = 0.8,
                    Text = "L",
                    TextColor = OxyColor.FromRgb(96, 96, 96),
                    FontSize = 8
                });
                dPlot.Annotations.Add(new OxyPlot.Annotations.LineAnnotation
                {
                    Type = OxyPlot.Annotations.LineAnnotationType.Vertical,
                    X = cfg.Upper,
                    Color = ciColor,
                    LineStyle = LineStyle.Dot,
                    StrokeThickness = 0.8,
                    Text = "U",
                    TextColor = OxyColor.FromRgb(96, 96, 96),
                    FontSize = 8
                });
            }
            else
            {
                dPlot.Axes.Add(new LinearAxis
                {
                    Position = AxisPosition.Bottom,
                    Minimum = 0,
                    Maximum = 1,
                    Title = "D"
                });

                var diagLine = JmpRefLine();
                diagLine.Points.Add(new DataPoint(0, 0));
                diagLine.Points.Add(new DataPoint(1, 1));
                dPlot.Series.Add(diagLine);
            }

            // 当前 D 值圆点
            var marker = new ScatterSeries
            {
                MarkerType = MarkerType.Circle,
                MarkerSize = 5,
                MarkerFill = OxyColor.FromRgb(0xE8, 0xA5, 0x40)  // JmpPalette[3] 琥珀
            };
            marker.Points.Add(new ScatterPoint(currentCompositeD, currentCompositeD));
            dPlot.Series.Add(marker);

            ApplyJmpProfilerStyle(dPlot);
            plots.Add(dPlot);
        }
        /// <summary>
        /// 用真正的 Desirability 配置计算 d(y)
        /// 对标 Minitab/JMP 的 Derringer-Suich 变换函数
        /// </summary>
        private double ComputeDesirabilityForCurrentResponse(double y)
        {
            // 找到当前响应的配置
            var cfg = _currentDesirabilityConfigs
                .FirstOrDefault(c => c.ResponseName == _selectedResponseName);

            if (cfg == null)
            {
                // 没有配置，用线性归一化占位
                if (_lastProfilerData?.Factors != null)
                {
                    double yMin = _lastProfilerData.Factors.Values.SelectMany(f => f.Y).DefaultIfEmpty(0).Min();
                    double yMax = _lastProfilerData.Factors.Values.SelectMany(f => f.Y).DefaultIfEmpty(1).Max();
                    if (Math.Abs(yMax - yMin) < 1e-12) return 0.5;
                    return Math.Max(0, Math.Min(1, (y - yMin) / (yMax - yMin)));
                }
                return 0.5;
            }

            // 用真正的 Derringer-Suich 公式计算
            double lower = cfg.Lower, upper = cfg.Upper, target = cfg.Target;
            double shape = cfg.Shape;

            switch (cfg.Goal)
            {
                case DesirabilityGoal.Maximize:
                    if (y <= lower) return 0;
                    if (y >= target) return 1;
                    { var denom = target - lower; return denom < 1e-12 ? 1 : Math.Pow((y - lower) / denom, shape); }

                case DesirabilityGoal.Minimize:
                    if (y >= upper) return 0;
                    if (y <= target) return 1;
                    { var denom = upper - target; return denom < 1e-12 ? 1 : Math.Pow((upper - y) / denom, shape); }

                default: // Target
                    if (y < lower || y > upper) return 0;
                    if (y <= target)
                    { var denom = target - lower; return denom < 1e-12 ? 1 : Math.Pow((y - lower) / denom, cfg.ShapeLower); }
                    else
                    { var denom = upper - target; return denom < 1e-12 ? 1 : Math.Pow((upper - y) / denom, cfg.ShapeUpper); }
            }
        }
        /// <summary>
        /// ★ 多响应版: 最大化意愿
        /// 单响应走旧逻辑, 多响应用 MultiResponseOlsPredictor + 类别因子枚举
        /// </summary>
        private async Task MaximizeDesirabilityAsync()
        {
            if (!IsProfilerLoaded) return;

            try
            {
                IsLoading = true;
                OlsStatusText = _localizationService.GetString("Doe_Analysis_Status_InMaxDesir", "正在最大化意愿...");

                if (IsOlsTabSelected)
                {
                    var batchId = IsDirectImportMode ? "" : SelectedOlsBatch?.BatchId;
                    var configs = await _desirabilityService.LoadConfigAsync(batchId ?? "");
                    if (configs.Count == 0)
                    {
                        await ShowSetDesirabilityDialogAsync();
                        configs = await _desirabilityService.LoadConfigAsync(batchId ?? "");
                        if (configs.Count == 0) return;
                    }

                    _currentDesirabilityConfigs = configs;

                    // ★ 过滤 configs：单响应只用当前响应的配置
                    var optimConfigs = _isMultiResponseProfiler
                        ? configs
                        : configs.Where(c => c.ResponseName == _selectedResponseName).ToList();

                    if (_isMultiResponseProfiler)
                    {
                        // ★ 多响应: 始终重建 predictor
                        OlsStatusText = _localizationService.GetString("Doe_Analysis_Status_InMultModel", "正在构建多响应模型...");
                        await BuildMultiResponsePredictorForProfiler();

                        if (_multiResponsePredictor == null)
                        {
                            OlsStatusText = _localizationService.GetString("Doe_Analysis_Status_MultModelFail", "多响应模型构建失败");
                            return;
                        }
                    }
                    else
                    {
                        // ★ 单响应: 用当前已拟合的模型（不重新 fit，保留精简状态）
                        var predictor = new MultiResponseOlsPredictor();
                        var factors = InferFactorDefsFromProfiler();
                        if (factors.Count > 0)
                        {
                            // ★ 用 OlsResult（当前界面上的模型），不调用 GetCurrentOlsResultAsync
                            if (OlsResult?.Coefficients != null && OlsResult.Coefficients.Count > 0)
                            {
                                var snapshot = RebuildFitResultFromAnalysisResult(OlsResult, _selectedResponseName);
                                if (snapshot.fitResult != null && snapshot.encoder != null)
                                {
                                    predictor.AddResponse(_selectedResponseName, snapshot.fitResult,
                                        snapshot.encoder, snapshot.factors);
                                }
                            }
                        }
                        _multiResponsePredictor = predictor;
                    }

                    // ★ 用过滤后的 configs
                    OlsStatusText = _localizationService.GetString("Doe_Analysis_Status_InSearchFactor", "正在搜索最优因子组合...");
                    var optimResult = await Task.Run(() => OptimizeDesirabilityWithCSharp(optimConfigs));
                    DesirabilityResult = optimResult;
                }
                else
                {
                    // GPR 模式保持不变 暂时不用
                    /*
                    //var configs = await _desirabilityService.LoadConfigAsync(_currentBatchId);
                    //var batch = await _repository.GetBatchWithDetailsAsync(_currentBatchId);
                    //if (batch == null) return;
                    //await _desirabilityService.ConfigureAsync(_currentBatchId, configs, batch.Factors);
                    //DesirabilityResult = await _desirabilityService.OptimizeAsync();*/
                }

                if (DesirabilityResult?.Success == true)
                {
                    DesirabilityCompositeD = DesirabilityResult.CompositeD;
                    UpdateOptimalFactorsDisplay(DesirabilityResult);

                    foreach (var kv in DesirabilityResult.OptimalFactors)
                        _profilerCurrentValues[kv.Key] = kv.Value;

                    if (_isMultiResponseProfiler)
                    {
                        await RefreshMultiResponseProfilerAfterOptimize();
                    }
                    else
                    {
                        await RefreshProfilerAfterOptimize();
                    }

                    if (IsDesirabilityVisible)
                    {
                        _currentDesirabilityConfigs = await _desirabilityService.LoadConfigAsync(
                            IsDirectImportMode ? "" : SelectedOlsBatch?.BatchId ?? _currentBatchId);
                        BuildDesirabilityPlotsFromProfiler();
                    }

                    OlsStatusText = string.Format(_localizationService.GetString("Doe_Analysis_Status_MaxDesirComplete", "最大化意愿完成: D = {0:F4}"),DesirabilityResult.CompositeD);
                }
                else
                {
                    OlsStatusText = string.Format(_localizationService.GetString("Doe_Analysis_Status_OptimizeFail", "优化失败: {0}"),DesirabilityResult?.ErrorMessage ?? "未知错误");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "最大化意愿失败");
                OlsStatusText = string.Format(_localizationService.GetString("Doe_Analysis_Status_MaxFail", "最大化失败: {0}"),ex.Message);
            }
            finally { IsLoading = false; }
        }


        // ═══════════════════════════════════════════════════════
        // [新增] RefreshMultiResponseProfilerAfterOptimize
        // ═══════════════════════════════════════════════════════

        /// <summary>
        /// 最大化意愿后刷新多响应刻画器
        /// 为每个响应重新获取 profiler 数据并更新图表
        /// </summary>
        private async Task RefreshMultiResponseProfilerAfterOptimize()
        {
            if (DesirabilityResult?.OptimalFactors == null) return;

            string originalResponse = _selectedResponseName;
            var fixedJson = JsonConvert.SerializeObject(DesirabilityResult.OptimalFactors);

            foreach (var respName in _profilerResponseOrder)
            {
                try
                {
                    await FitForResponseAsync(respName);

                    string json;
                    if (IsDirectImportMode)
                        json = await Task.Run(() =>
                            _analysisService.GetPredictionProfilerDirectAsync(respName, 50, fixedJson));
                    else
                        json = await _analysisService.GetPredictionProfilerAsync(
                            SelectedOlsBatch!.BatchId, respName, 50,
                            JsonConvert.DeserializeObject<Dictionary<string, object>>(fixedJson));

                    var data = JsonConvert.DeserializeObject<ProfilerResult>(json);
                    if (data?.Factors != null)
                        _multiProfilerData[respName] = data;
                }
                catch { }
            }

            // 恢复当前响应
            await FitForResponseAsync(originalResponse);

            // 更新 _lastProfilerData
            if (_multiProfilerData.Count > 0)
                _lastProfilerData = _multiProfilerData.Values.First();

            // 重建图表
            BuildMultiResponseProfilerPlots();
        }

        private async Task RefreshProfilerAfterOptimize()
        {
            if (DesirabilityResult?.OptimalFactors == null) return;

            try
            {
                // 1. 更新 _profilerCurrentValues 到最优点
                foreach (var kv in DesirabilityResult.OptimalFactors)
                    _profilerCurrentValues[kv.Key] = kv.Value;

                // 2. 用 UpdateProfilerLocal 刷新 (这个方法会正确移动红线)
                // 选一个因子触发全局刷新
                var firstFactor = _profilerFactorOrder?.FirstOrDefault();
                if (firstFactor != null && _profilerCurrentValues.TryGetValue(firstFactor, out var val))
                {
                    UpdateProfilerLocal(firstFactor, val);
                }
            }
            catch { }
        }
        /// <summary>
        /// ★ 纯 C# Desirability 优化（不走 Python，毫秒级完成）
        /// 网格搜索 + 局部梯度下降
        /// </summary>
        private DesirabilityResult OptimizeDesirabilityWithCSharp(List<DesirabilityResponseConfig> configs)
        {
            var contRanges = new List<(string name, double min, double max)>();
            var catFactorLevels = new Dictionary<string, List<string>>();

            foreach (var fname in _profilerFactorOrder)
            {
                if (IsProfilerFactorCategorical(fname))
                {
                    var levels = GetProfilerCategoryLevels(fname);
                    if (levels != null && levels.Count > 0)
                        catFactorLevels[fname] = levels;
                }
                else
                {
                    var range = GetProfilerFactorRange(fname);
                    contRanges.Add((fname, range.min, range.max));
                }
            }

            int nFactors = contRanges.Count;
            if (nFactors == 0 && catFactorLevels.Count == 0)
                return new DesirabilityResult { Success = false, ErrorMessage = "无因子信息" };

            var catCombos = GenerateCategoricalCombinations(catFactorLevels);
            if (catCombos.Count == 0)
                catCombos.Add(new Dictionary<string, string>());

            double bestD = -1;
            double[] bestPoint = new double[nFactors];
            Dictionary<string, string> bestCat = catCombos[0];

            int coarseGrid = nFactors <= 3 ? 21 : (nFactors <= 5 ? 11 : 7);
            int topN = 5;

            // ★ 诊断: 打印因子范围和意愿配置
            for (int i = 0; i < nFactors; i++)
                _logger.LogInformation("  因子[{I}] {Name}: [{Min}, {Max}]", i, contRanges[i].name, contRanges[i].min, contRanges[i].max);
            foreach (var cfg in configs)
                _logger.LogInformation("  意愿: {Resp} Goal={Goal} Lower={Lo} Target={Tgt} Upper={Up}", cfg.ResponseName, cfg.Goal, cfg.Lower, cfg.Target, cfg.Upper);

            foreach (var catCombo in catCombos)
            {
                if (nFactors == 0)
                {
                    double D = EvaluateCompositeD(new double[0], contRanges, configs, catCombo);
                    if (D > bestD) { bestD = D; bestCat = catCombo; }
                    continue;
                }

                // ═══ Phase 1: 粗网格 Top-N ═══
                int totalGrid = (int)Math.Pow(coarseGrid, nFactors);
                if (totalGrid > 500000)
                {
                    coarseGrid = Math.Max(5, (int)Math.Pow(500000, 1.0 / nFactors));
                    totalGrid = (int)Math.Pow(coarseGrid, nFactors);
                }

                var topPoints = new List<(double[] point, double d)>();
                for (int g = 0; g < totalGrid; g++)
                {
                    var point = new double[nFactors];
                    int temp = g;
                    for (int j = nFactors - 1; j >= 0; j--)
                    {
                        int idx = temp % coarseGrid;
                        temp /= coarseGrid;
                        point[j] = contRanges[j].min + (contRanges[j].max - contRanges[j].min) * idx / (coarseGrid - 1);
                    }
                    double D = EvaluateCompositeD(point, contRanges, configs, catCombo);
                    if (topPoints.Count < topN)
                    {
                        topPoints.Add(((double[])point.Clone(), D));
                        topPoints.Sort((a, b) => b.d.CompareTo(a.d));
                    }
                    else if (D > topPoints[topN - 1].d)
                    {
                        topPoints[topN - 1] = ((double[])point.Clone(), D);
                        topPoints.Sort((a, b) => b.d.CompareTo(a.d));
                    }
                }
                _logger.LogInformation("Phase1: D={D:F6}, point=({P})", topPoints[0].d, string.Join(",", topPoints[0].point.Select(v => v.ToString("F2"))));

                // ═══ Phase 2: Top-N × NM ═══
                double localBestD = -1;
                double[] localBestPoint = new double[nFactors];
                var nmRanges = contRanges.Select(r => (r.min, r.max)).ToList();

                foreach (var (startPoint, _) in topPoints)
                {
                    var (nmPoint, nmD) = NelderMeadMaximize(startPoint, nmRanges, configs, catCombo);
                    if (nmD > localBestD) { localBestD = nmD; localBestPoint = nmPoint; }
                }
                _logger.LogInformation("Phase2: D={D:F6}, point=({P})", localBestD, string.Join(",", localBestPoint.Select(v => v.ToString("F2"))));

                // ═══ Phase 3: 边界角点 + NM ═══
                int nBorder = 1 << nFactors;
                if (nBorder <= 256)
                {
                    var borderTop = new List<(double[] point, double d)>();
                    for (int mask = 0; mask < nBorder; mask++)
                    {
                        var bp = new double[nFactors];
                        for (int j = 0; j < nFactors; j++)
                            bp[j] = ((mask >> j) & 1) == 0 ? contRanges[j].min : contRanges[j].max;
                        double D = EvaluateCompositeD(bp, contRanges, configs, catCombo);
                        borderTop.Add(((double[])bp.Clone(), D));
                    }
                    borderTop.Sort((a, b) => b.d.CompareTo(a.d));
                    _logger.LogInformation("Phase3 角点Top1: D={D:F6}, point=({P})", borderTop[0].d, string.Join(",", borderTop[0].point.Select(v => v.ToString("F2"))));

                    int borderNM = Math.Min(3, borderTop.Count);
                    for (int bi = 0; bi < borderNM; bi++)
                    {
                        var (bp, bd) = borderTop[bi];
                        if (bd > localBestD) { localBestD = bd; localBestPoint = (double[])bp.Clone(); }
                        var (nmBp, nmBd) = NelderMeadMaximize(bp, nmRanges, configs, catCombo);
                        if (nmBd > localBestD) { localBestD = nmBd; localBestPoint = nmBp; }
                    }
                    _logger.LogInformation("Phase3后: D={D:F6}, point=({P})", localBestD, string.Join(",", localBestPoint.Select(v => v.ToString("F2"))));
                }

                // ═══ Phase 4: 精细网格 ±15% + NM ═══
                if (localBestD > 0)
                {
                    int fineGrid = 11;
                    int fineTotal = (int)Math.Pow(fineGrid, nFactors);
                    if (fineTotal > 200000)
                        fineGrid = Math.Max(3, (int)Math.Pow(200000, 1.0 / nFactors));
                    fineTotal = (int)Math.Pow(fineGrid, nFactors);

                    for (int g = 0; g < fineTotal; g++)
                    {
                        var point = new double[nFactors];
                        int temp = g;
                        for (int j = nFactors - 1; j >= 0; j--)
                        {
                            int idx = temp % fineGrid;
                            temp /= fineGrid;
                            double range = contRanges[j].max - contRanges[j].min;
                            double lo = Math.Max(contRanges[j].min, localBestPoint[j] - range * 0.15);
                            double hi = Math.Min(contRanges[j].max, localBestPoint[j] + range * 0.15);
                            point[j] = lo + (hi - lo) * idx / (fineGrid - 1);
                        }
                        double D = EvaluateCompositeD(point, contRanges, configs, catCombo);
                        if (D > localBestD) { localBestD = D; Array.Copy(point, localBestPoint, nFactors); }
                    }

                    var (finalPoint, finalD) = NelderMeadMaximize(localBestPoint, nmRanges, configs, catCombo);
                    if (finalD > localBestD) { localBestD = finalD; localBestPoint = finalPoint; }
                }

                _logger.LogInformation("★ 最终: cat=[{Cat}], D={D:F6}, point=({P})",
                    string.Join(",", catCombo.Select(kv => $"{kv.Key}={kv.Value}")), localBestD,
                    string.Join(",", localBestPoint.Select(v => v.ToString("F4"))));

                if (localBestD > bestD) { bestD = localBestD; bestPoint = localBestPoint; bestCat = catCombo; }
            }

            // 构建结果
            var optimalFactors = new Dictionary<string, object>();
            for (int i = 0; i < nFactors; i++)
                optimalFactors[contRanges[i].name] = Math.Round(bestPoint[i], 4);
            foreach (var kv in bestCat)
                optimalFactors[kv.Key] = kv.Value;

            var contDict = new Dictionary<string, double>();
            for (int i = 0; i < nFactors; i++)
                contDict[contRanges[i].name] = bestPoint[i];

            double predictedMain = _multiResponsePredictor != null
                ? _multiResponsePredictor.Predict(configs[0].ResponseName, contDict, bestCat) : 0;

            // ★ 诊断: 测试 Minitab 最优点 (温度min, 压力max, 时间max, 催化剂min)
            // ★ 诊断: 测试每个响应在最优点的预测值
            try
            {
                _logger.LogWarning("★★ 多响应预测诊断:");
                _logger.LogWarning("  predictor 响应数: {N}, 响应名: [{Names}]",
                    _multiResponsePredictor?.ResponseNames?.Count ?? 0,
                    string.Join(",", _multiResponsePredictor?.ResponseNames ?? new List<string>()));

                var testCont = new Dictionary<string, double>();
                for (int i = 0; i < nFactors; i++)
                    testCont[contRanges[i].name] = bestPoint[i];

                foreach (var cfg in configs)
                {
                    bool has = _multiResponsePredictor?.HasResponse(cfg.ResponseName) ?? false;
                    double pred = has ? _multiResponsePredictor!.Predict(cfg.ResponseName, testCont, bestCat) : -999;
                    double d = ComputeDesirabilityForConfig(pred, cfg);
                    _logger.LogWarning("  响应 {Name}: has={Has}, pred={Pred:F4}, d={D:F6}, cfg=[{Lo},{Tgt},{Up}]",
                        cfg.ResponseName, has, pred, d, cfg.Lower, cfg.Target, cfg.Upper);
                }
            }
            catch (Exception ex) { _logger.LogWarning("诊断异常: {Msg}", ex.Message); }

            return new DesirabilityResult
            {
                Success = bestD > 0,
                CompositeD = bestD,
                OptimalFactors = optimalFactors,
                PredictedResponse = predictedMain,
                ErrorMessage = bestD <= 0 ? "未找到满足所有约束的解" : null
            };
        }
        /// <summary>
        /// 生成类别因子的所有组合 (笛卡尔积)
        /// </summary>
        private List<Dictionary<string, string>> GenerateCategoricalCombinations(
            Dictionary<string, List<string>> catFactorLevels)
        {
            var result = new List<Dictionary<string, string>>();
            if (catFactorLevels.Count == 0) return result;

            var keys = catFactorLevels.Keys.ToList();
            var current = new Dictionary<string, string>();

            void Generate(int depth)
            {
                if (depth == keys.Count)
                {
                    result.Add(new Dictionary<string, string>(current));
                    return;
                }
                var key = keys[depth];
                foreach (var level in catFactorLevels[key])
                {
                    current[key] = level;
                    Generate(depth + 1);
                }
            }

            Generate(0);
            return result;
        }

        /// <summary>
        /// Nelder-Mead 单纯形法最大化 Composite D
        /// </summary>
        private (double[] best, double bestD) NelderMeadMaximize(
            double[] start,
            List<(double min, double max)> ranges,
            List<DesirabilityResponseConfig> configs,
            Dictionary<string, string>? catValues,
            int maxIter = 200)
        {
            int n = start.Length;
            if (n == 0) return (start, EvaluateCompositeD(start,
                ranges.Select((r, i) => (_profilerFactorOrder[i], r.min, r.max)).ToList(), configs, catValues));

            var contRanges = new List<(string name, double min, double max)>();
            int contIdx = 0;
            foreach (var fname in _profilerFactorOrder)
            {
                if (!IsProfilerFactorCategorical(fname) && contIdx < ranges.Count)
                {
                    contRanges.Add((fname, ranges[contIdx].min, ranges[contIdx].max));
                    contIdx++;
                }
            }

            double Eval(double[] p) => EvaluateCompositeD(p, contRanges, configs, catValues);
            double Clamp(double v, double lo, double hi) => Math.Max(lo, Math.Min(hi, v));

            double alpha = 1, gamma = 2, rho = 0.5, sigma = 0.5;

            var simplex = new List<(double[] p, double val)>();
            simplex.Add(((double[])start.Clone(), Eval(start)));

            for (int i = 0; i < n; i++)
            {
                var p = (double[])start.Clone();
                double step = (ranges[i].max - ranges[i].min) * 0.1;
                p[i] = Math.Min(ranges[i].max, p[i] + step);
                simplex.Add((p, Eval(p)));
            }

            for (int iter = 0; iter < maxIter; iter++)
            {
                simplex.Sort((a, b) => b.val.CompareTo(a.val));
                var best = simplex[0];
                var worst = simplex[n];

                if (Math.Abs(best.val - worst.val) < 1e-10) break;

                var centroid = new double[n];
                for (int i = 0; i < n; i++)
                    for (int j = 0; j < n; j++)
                        centroid[j] += simplex[i].p[j] / n;

                var reflected = new double[n];
                for (int j = 0; j < n; j++)
                    reflected[j] = Clamp(centroid[j] + alpha * (centroid[j] - worst.p[j]), ranges[j].min, ranges[j].max);
                double rVal = Eval(reflected);

                if (rVal > simplex[0].val)
                {
                    var expanded = new double[n];
                    for (int j = 0; j < n; j++)
                        expanded[j] = Clamp(centroid[j] + gamma * (reflected[j] - centroid[j]), ranges[j].min, ranges[j].max);
                    double eVal = Eval(expanded);
                    simplex[n] = eVal > rVal ? (expanded, eVal) : (reflected, rVal);
                }
                else if (rVal > simplex[n - 1].val)
                {
                    simplex[n] = (reflected, rVal);
                }
                else
                {
                    var contracted = new double[n];
                    for (int j = 0; j < n; j++)
                        contracted[j] = Clamp(centroid[j] + rho * (worst.p[j] - centroid[j]), ranges[j].min, ranges[j].max);
                    double cVal = Eval(contracted);
                    if (cVal > worst.val)
                    {
                        simplex[n] = (contracted, cVal);
                    }
                    else
                    {
                        for (int i = 1; i <= n; i++)
                        {
                            var shrunk = new double[n];
                            for (int j = 0; j < n; j++)
                                shrunk[j] = Clamp(simplex[0].p[j] + sigma * (simplex[i].p[j] - simplex[0].p[j]), ranges[j].min, ranges[j].max);
                            simplex[i] = (shrunk, Eval(shrunk));
                        }
                    }
                }
            }

            simplex.Sort((a, b) => b.val.CompareTo(a.val));
            return (simplex[0].p, simplex[0].val);
        }
        /// <summary>
        /// ★ 新增: 为 Desirability 优化构建多响应预测器
        /// 
        /// 对标 Minitab Response Optimizer:
        ///   Minitab 内部为每个响应独立拟合 OLS 模型，
        ///   本方法做同样的事 — 为每个响应 fit 一次，把 β 系数缓存到 MultiResponseOlsPredictor。
        /// 
        /// 支持两种模式:
        ///   - DirectImport 模式: 数据在 _directImportFactorsData / _directImportResponsesData
        ///   - Batch 模式: 数据从 _repository 加载
        /// </summary>
        private async Task<MultiResponseOlsPredictor?> BuildMultiResponsePredictor(
            List<DesirabilityResponseConfig> configs)
        {
            var predictor = new MultiResponseOlsPredictor();

            foreach (var cfg in configs)
            {
                try
                {
                    OLSAnalysisResult result;

                    if (IsDirectImportMode)
                    {
                        // 直接导入模式: 数据已在内存中
                        if (!_directImportResponsesData.TryGetValue(cfg.ResponseName, out var respData))
                        {
                            _logger.LogWarning("Desirability: 响应 '{Name}' 无数据，跳过", cfg.ResponseName);
                            continue;
                        }

                        result = await Task.Run(() =>
                            _analysisService.FitOlsDirectAsync(
                                _directImportFactorsData, respData,
                                cfg.ResponseName, _directImportFactorTypes, _currentOlsModelType));
                    }
                    else
                    {
                        // Batch 模式
                        var batchId = SelectedOlsBatch?.BatchId;
                        if (string.IsNullOrEmpty(batchId)) continue;

                        result = await _analysisService.FitOlsAsync(
                            batchId, cfg.ResponseName, _currentOlsModelType);
                    }

                    if (result?.Coefficients == null || result.Coefficients.Count == 0)
                    {
                        _logger.LogWarning("Desirability: 响应 '{Name}' OLS 拟合失败", cfg.ResponseName);
                        continue;
                    }

                    // 从 _analysisService 的当前状态中获取 fitResult/encoder/factors
                    // 注意: FitOlsDirectAsync / FitOlsAsync 调用后，_analysisService 内部
                    // 已经缓存了这个响应的 _fitResult/_encoder/_factors
                    // 我们需要取出来做快照

                    // ★ 通过反射或新增接口方法获取内部状态
                    // 更安全的做法: 在 IDOEAnalysisService 接口新增方法
                    // 但为了最小改动，我们直接从 result 重建
                    var snapshot = RebuildFitResultFromAnalysisResult(result, cfg.ResponseName);
                    if (snapshot.fitResult != null && snapshot.encoder != null)
                    {
                        predictor.AddResponse(cfg.ResponseName, snapshot.fitResult,
                            snapshot.encoder, snapshot.factors);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Desirability: 响应 '{Name}' 模型构建失败", cfg.ResponseName);
                }
            }

            // ★ 恢复当前选中响应的模型（因为上面循环可能切走了）
            try
            {
                if (IsDirectImportMode)
                {
                    if (_directImportResponsesData.TryGetValue(_selectedResponseName, out var curResp))
                        await Task.Run(() => _analysisService.FitOlsDirectAsync(
                            _directImportFactorsData, curResp,
                            _selectedResponseName, _directImportFactorTypes, _currentOlsModelType));
                }
                else if (SelectedOlsBatch != null)
                {
                    await _analysisService.FitOlsAsync(
                        SelectedOlsBatch.BatchId, _selectedResponseName, _currentOlsModelType);
                }
            }
            catch { /* 恢复失败不影响优化结果 */ }

            return predictor.ResponseNames.Count > 0 ? predictor : null;
        }

        /// <summary>
        /// ★ 辅助: 从 OLSAnalysisResult 重建 OlsFitResult + FactorEncoder
        /// 不依赖 _analysisService 的内部状态
        /// </summary>
        private (OlsFitResult? fitResult, FactorEncoder? encoder, List<FactorDefinition> factors)
            RebuildFitResultFromAnalysisResult(OLSAnalysisResult result, string responseName)
        {
            List<FactorDefinition> factors;
            if (IsDirectImportMode)
                factors = InferFactorDefsFromDirectImport();
            else
                factors = InferFactorDefsFromProfiler();
            if (factors.Count == 0 || result.Coefficients == null || result.Coefficients.Count == 0)
                return (null, null, factors);
            var encoder = new FactorEncoder(factors);
            int p = result.Coefficients.Count;
            var fitResult = new OlsFitResult
            {
                N = 0,
                P = p,
                TermNames = result.Coefficients.Select(c => c.Term).ToArray(),
                Coefficients = result.Coefficients.Select(c => c.Coefficient).ToArray(),
                StdErrors = result.Coefficients.Select(c => c.StdError).ToArray(),
                TValues = result.Coefficients.Select(c => c.TValue).ToArray(),
                PValues = result.Coefficients.Select(c => c.PValue).ToArray(),
            };
            // ★ 新增: 重建 XtXInverse (CI 计算必需)
            if (result.XtxInvFlat != null && result.XtxInvFlat.Length > 0)
            {
                int dim = (int)Math.Sqrt(result.XtxInvFlat.Length);

                if (dim * dim == result.XtxInvFlat.Length)
                {
                    fitResult.XtXInverse = new double[dim, dim];
                    for (int i = 0; i < dim; i++)
                        for (int j = 0; j < dim; j++)
                            fitResult.XtXInverse[i, j] = result.XtxInvFlat[i * dim + j];
                }
            }
            // ★ 新增: Sigma (RMSE)
            fitResult.Sigma = result.ModelSummary?.RMSE ?? 0;
            fitResult.RMSE = fitResult.Sigma;
            fitResult.MSE = fitResult.Sigma * fitResult.Sigma;
            // ★ 新增: TCrit — 直接从 OLSAnalysisResult 取
            if (result.TCrit > 0)
            {
                fitResult.TCrit = result.TCrit;
            }
            else
            {
                // 回退: 从 ANOVA 表的误差行取 DF 算 TCrit
                int dfResid = 0;
                if (result.AnovaTable != null)
                {
                    var errorRow = result.AnovaTable.FirstOrDefault(a =>
                        a.Source == "残差" || a.Source == "误差" ||
                        a.Source == "Error" || a.Source == "Residual Error");
                    if (errorRow != null) dfResid = errorRow.DF;
                }
                fitResult.DfResidual = dfResid;
                fitResult.TCrit = dfResid > 0
                    ? MathNet.Numerics.Distributions.StudentT.InvCDF(0, 1, dfResid, 0.975)
                    : 2.0;
            }
            return (fitResult, encoder, factors);
        }

        /// <summary>
        /// 从直接导入数据推断因子定义
        /// </summary>
        private List<FactorDefinition> InferFactorDefsFromDirectImport()
        {
            var factors = new List<FactorDefinition>();
            if (_directImportFactorsData == null || _directImportFactorsData.Count == 0)
                return factors;

            var firstRow = _directImportFactorsData[0];
            foreach (var kv in firstRow)
            {
                string name = kv.Key;
                bool isCat = _directImportFactorTypes != null &&
                    _directImportFactorTypes.TryGetValue(name, out var ft) &&
                    ft.ToLower() == "categorical";

                if (isCat)
                {
                    var allValues = _directImportFactorsData
                        .Select(r => r.TryGetValue(name, out var v) ? v?.ToString() ?? "" : "")
                        .Distinct().OrderBy(s => s).ToList();

                    factors.Add(new FactorDefinition
                    {
                        Name = name,
                        IsCategorical = true,
                        Levels = allValues
                    });
                }
                else
                {
                    var allValues = _directImportFactorsData
                        .Where(r => r.TryGetValue(name, out var v) && v is double or int or float or long)
                        .Select(r => Convert.ToDouble(r[name]))
                        .ToList();

                    double lo = allValues.Count > 0 ? allValues.Min() : 0;
                    double hi = allValues.Count > 0 ? allValues.Max() : 1;

                    factors.Add(new FactorDefinition
                    {
                        Name = name,
                        IsCategorical = false,
                        Lower = lo,
                        Upper = hi
                    });
                }
            }
            return factors;
        }

        /// <summary>
        /// 从 profiler 数据推断因子定义
        /// </summary>
        private List<FactorDefinition> InferFactorDefsFromProfiler()
        {
            var factors = new List<FactorDefinition>();
            if (_lastProfilerData?.Factors == null) return factors;

            foreach (var fname in _profilerFactorOrder)
            {
                if (!_lastProfilerData.Factors.TryGetValue(fname, out var fd)) continue;

                if (fd.IsCategorical)
                {
                    factors.Add(new FactorDefinition
                    {
                        Name = fname,
                        IsCategorical = true,
                        Levels = fd.Levels ?? new List<string>()
                    });
                }
                else
                {
                    var range = GetProfilerFactorRange(fname);
                    factors.Add(new FactorDefinition
                    {
                        Name = fname,
                        IsCategorical = false,
                        Lower = range.min,
                        Upper = range.max
                    });
                }
            }
            return factors;
        }
        /// <summary>
        /// ★ 修复版: 计算综合 Desirability D
        /// 
        /// 修复: 每个响应用 _multiResponsePredictor 中各自独立的 OLS 模型预测，
        /// 而不是用 _analysisService 的单一缓存模型。
        /// 
        /// D = (d₁^w₁ × d₂^w₂ × ...)^(1/Σwⱼ)
        /// 其中 wⱼ = Importance (1-5)
        /// </summary>
        private double EvaluateCompositeD(
            double[] contPoint,
            List<(string name, double min, double max)> ranges,
            List<DesirabilityResponseConfig> configs,
            Dictionary<string, string>? catValues)
        {
            if (_multiResponsePredictor == null) return 0;

            // 构建连续因子值字典
            var contDict = new Dictionary<string, double>();
            for (int i = 0; i < contPoint.Length && i < ranges.Count; i++)
                contDict[ranges[i].name] = contPoint[i];

            var catDict = catValues ?? new Dictionary<string, string>();

            double product = 1;
            double sumW = 0;
            foreach (var cfg in configs)
            {
                double pred = _multiResponsePredictor.Predict(cfg.ResponseName, contDict, catDict);
                if (double.IsNaN(pred)) return 0;

                double d = ComputeDesirabilityForConfig(pred, cfg);
                double w = cfg.Importance;
                product *= Math.Pow(Math.Max(d, 1e-15), w);
                sumW += w;
            }
            double D = sumW > 0 ? Math.Pow(product, 1.0 / sumW) : 0;

            // ★ 当 D=1 时, 用预测值的加权和作为 tiebreaker
            // 这样在 D=1 的平台区, 预测值更高(更远离边界)的点得分更高
            // bonus 量级 ~1e-10, 不影响 D<1 区域的排序
            if (D >= 1.0 - 1e-12)
            {
                double predSum = 0;
                foreach (var cfg in configs)
                {
                    double pred = _multiResponsePredictor.Predict(cfg.ResponseName, contDict, catDict);
                    // 归一化到 [0,1] 范围的 bonus
                    double range = Math.Max(Math.Abs(cfg.Upper - cfg.Lower), 1);
                    predSum += pred / range;
                }
                D = 1.0 + predSum * 1e-10;
            }

            return D;
            
        }
        // ════════════════════════════════════════════════════════════════
        // 方法 4: EnumerateCategoryCombinations (新增)
        // ★ 生成所有类别因子水平的笛卡尔积
        // ════════════════════════════════════════════════════════════════

        /// <summary>
        /// ★ 新增: 生成所有类别因子水平组合（笛卡尔积）
        /// 
        /// 例: 催化剂={Cat_A, Cat_B}, 溶剂={Solv_X, Solv_Y}
        ///   → 返回 4 种组合:
        ///     {催化剂: Cat_A, 溶剂: Solv_X}
        ///     {催化剂: Cat_A, 溶剂: Solv_Y}
        ///     {催化剂: Cat_B, 溶剂: Solv_X}
        ///     {催化剂: Cat_B, 溶剂: Solv_Y}
        /// 
        /// 如果没有类别因子，返回包含一个空字典的列表（表示唯一的"无类别"组合）。
        /// </summary>
        private static List<Dictionary<string, string>> EnumerateCategoryCombinations(
            List<string> catFactorNames,
            Dictionary<string, List<string>> catFactorLevels)
        {
            var result = new List<Dictionary<string, string>> { new() };

            foreach (var catName in catFactorNames)
            {
                if (!catFactorLevels.TryGetValue(catName, out var levels) || levels.Count == 0)
                    continue;

                var newResult = new List<Dictionary<string, string>>();
                foreach (var existing in result)
                {
                    foreach (var lv in levels)
                    {
                        var clone = new Dictionary<string, string>(existing) { [catName] = lv };
                        newResult.Add(clone);
                    }
                }
                result = newResult;
            }

            return result;
        }


        private double ComputeDesirabilityForConfig(double y, DesirabilityResponseConfig cfg)
        {
            double lower = cfg.Lower, upper = cfg.Upper, target = cfg.Target;
            double shape = cfg.Shape;

            switch (cfg.Goal)
            {
                case DesirabilityGoal.Maximize:
                    if (y <= lower) return 0;
                    if (y >= target) return 1;
                    {
                        var denom = target - lower;
                        return denom < 1e-12 ? 1 : Math.Pow((y - lower) / denom, shape);
                    }

                case DesirabilityGoal.Minimize:
                    if (y >= upper) return 0;
                    if (y <= target) return 1;
                    {
                        var denom = upper - target;
                        return denom < 1e-12 ? 1 : Math.Pow((upper - y) / denom, shape);
                    }

                default: // Target
                    if (y < lower || y > upper) return 0;
                    if (y <= target)
                    {
                        var denom = target - lower;
                        return denom < 1e-12 ? 1 : Math.Pow((y - lower) / denom, cfg.ShapeLower);
                    }
                    else
                    {
                        var denom = upper - target;
                        return denom < 1e-12 ? 1 : Math.Pow((upper - y) / denom, cfg.ShapeUpper);
                    }
            }
        }

        private List<double[]> GenerateGridPoints(List<(string name, double min, double max)> ranges, int gridPerFactor)
        {
            int nFactors = ranges.Count;
            var points = new List<double[]>();
            int totalPoints = (int)Math.Pow(gridPerFactor, nFactors);
            if (totalPoints > 500000)
                gridPerFactor = Math.Max(3, (int)Math.Pow(500000, 1.0 / nFactors));

            totalPoints = (int)Math.Pow(gridPerFactor, nFactors);
            for (int idx = 0; idx < totalPoints; idx++)
            {
                var point = new double[nFactors];
                int remainder = idx;
                for (int f = 0; f < nFactors; f++)
                {
                    int level = remainder % gridPerFactor;
                    remainder /= gridPerFactor;
                    point[f] = ranges[f].min + (ranges[f].max - ranges[f].min) * level / (gridPerFactor - 1);
                }
                points.Add(point);
            }
            return points;
        }

        /// <summary>
        /// Nelder-Mead 单纯形局部优化（最大化 CompositeD）
        /// ★ 修改: catValues 不再从外层闭包捕获，而是显式传入
        /// </summary>
        private (double[] point, double d) NelderMeadOptimize(double[] start,
            List<(string name, double min, double max)> ranges,
            List<DesirabilityResponseConfig> configs,
            Dictionary<string, string>? catValues = null,
            int maxIter = 200)
        {
            int n = start.Length;
            double alpha = 1, gamma = 2, rho = 0.5, sigma = 0.5;

            // 初始化单纯形
            var simplex = new List<(double[] p, double val)>();
            simplex.Add(((double[])start.Clone(), EvaluateCompositeD(start, ranges, configs, catValues)));

            for (int i = 0; i < n; i++)
            {
                var p = (double[])start.Clone();
                double step = (ranges[i].max - ranges[i].min) * 0.1;
                p[i] = Math.Min(ranges[i].max, p[i] + step);
                simplex.Add((p, EvaluateCompositeD(p, ranges, configs, catValues)));
            }

            for (int iter = 0; iter < maxIter; iter++)
            {
                simplex.Sort((a, b) => b.val.CompareTo(a.val)); // 降序（最大化）
                var best = simplex[0];
                var worst = simplex[n];

                // 收敛检测
                if (Math.Abs(best.val - worst.val) < 1e-8) break;

                // 质心（排除最差点）
                var centroid = new double[n];
                for (int i = 0; i < n; i++)
                    for (int j = 0; j < n; j++)
                        centroid[j] += simplex[i].p[j] / n;

                // 反射
                var reflected = new double[n];
                for (int j = 0; j < n; j++)
                    reflected[j] = Clamp(centroid[j] + alpha * (centroid[j] - worst.p[j]),
                        ranges[j].min, ranges[j].max);
                double rVal = EvaluateCompositeD(reflected, ranges, configs, catValues);

                if (rVal > simplex[0].val)
                {
                    // 扩展
                    var expanded = new double[n];
                    for (int j = 0; j < n; j++)
                        expanded[j] = Clamp(centroid[j] + gamma * (reflected[j] - centroid[j]),
                            ranges[j].min, ranges[j].max);
                    double eVal = EvaluateCompositeD(expanded, ranges, configs, catValues);
                    simplex[n] = eVal > rVal ? (expanded, eVal) : (reflected, rVal);
                }
                else if (rVal > simplex[n - 1].val)
                {
                    simplex[n] = (reflected, rVal);
                }
                else
                {
                    // 收缩
                    var contracted = new double[n];
                    for (int j = 0; j < n; j++)
                        contracted[j] = Clamp(centroid[j] + rho * (worst.p[j] - centroid[j]),
                            ranges[j].min, ranges[j].max);
                    double cVal = EvaluateCompositeD(contracted, ranges, configs, catValues);

                    if (cVal > worst.val)
                    {
                        simplex[n] = (contracted, cVal);
                    }
                    else
                    {
                        // 缩小: 所有点向最好点靠拢
                        for (int i = 1; i <= n; i++)
                        {
                            for (int j = 0; j < n; j++)
                                simplex[i] = (simplex[i].p, 0);

                            var shrunk = new double[n];
                            for (int j = 0; j < n; j++)
                                shrunk[j] = Clamp(simplex[0].p[j] + sigma * (simplex[i].p[j] - simplex[0].p[j]),
                                    ranges[j].min, ranges[j].max);
                            simplex[i] = (shrunk, EvaluateCompositeD(shrunk, ranges, configs, catValues));
                        }
                    }
                }
            }

            simplex.Sort((a, b) => b.val.CompareTo(a.val));
            return (simplex[0].p, simplex[0].val);
        }

        private static double Clamp(double v, double min, double max) => Math.Max(min, Math.Min(max, v));
        #region OxyPlot JMP 统一样式

        /// <summary>JMP 风格色板（最多6条系列）</summary>
        private static readonly OxyColor[] JmpPalette = new[]
        {
    OxyColor.FromRgb(0x4A, 0x7F, 0xC4),  // 蓝
    OxyColor.FromRgb(0xD4, 0x65, 0x4A),  // 红棕
    OxyColor.FromRgb(0x5B, 0xA0, 0x5B),  // 绿
    OxyColor.FromRgb(0xE8, 0xA5, 0x40),  // 琥珀
    OxyColor.FromRgb(0x8B, 0x6D, 0xB5),  // 紫
    OxyColor.FromRgb(0x6D, 0xAF, 0xB5),  // 青
};

        /// <summary>CI 填充色 (系列色 α=30)</summary>
        private static OxyColor JmpCIFill(int seriesIndex = 0)
        {
            var c = JmpPalette[seriesIndex % JmpPalette.Length];
            return OxyColor.FromArgb(80, c.R, c.G, c.B);  // ★ 45 → 80
        }

        /// <summary>
        /// 统一应用 JMP 风格到 PlotModel
        /// 调用时机: 在 model.Series/Axes 都添加完之后
        /// </summary>
        private static void ApplyJmpStyle(PlotModel model)
        {
            // 去掉标题（由 XAML 折叠面板标题栏代替）
            model.TitleFontSize = 0;
            model.SubtitleFontSize = 0;

            // 绘图区边框
            model.PlotAreaBorderThickness = new OxyThickness(0.5);
            model.PlotAreaBorderColor = OxyColor.FromRgb(200, 200, 200);

            // 紧凑边距
            model.PlotMargins = new OxyThickness(48, 8, 12, 32);

            // 轴样式
            foreach (var axis in model.Axes)
            {
                axis.AxislineColor = OxyColor.FromRgb(200, 200, 200);
                axis.AxislineThickness = 0.5;
                axis.TickStyle = TickStyle.Outside;
                axis.MajorTickSize = 3;
                axis.MinorTickSize = 0;
                axis.MajorGridlineStyle = LineStyle.Dot;
                axis.MajorGridlineColor = OxyColor.FromRgb(232, 232, 232);
                axis.MajorGridlineThickness = 0.5;
                axis.MinorGridlineStyle = LineStyle.None;
                axis.FontSize = 9;
                axis.TextColor = OxyColor.FromRgb(128, 128, 128);
                axis.TitleFontSize = 10;
                axis.TitleColor = OxyColor.FromRgb(96, 96, 96);
            }
        }

        /// <summary>
        /// Profiler 专用样式（保留 Title/Subtitle 用于因子名和当前值）
        /// </summary>
        private static void ApplyJmpProfilerStyle(PlotModel model)
        {
            model.PlotAreaBorderThickness = new OxyThickness(0.5);
            model.PlotAreaBorderColor = OxyColor.FromRgb(200, 200, 200);
            model.PlotMargins = new OxyThickness(48, 20, 10, 30);

            // 标题保留（因子名），但缩小
            model.TitleFontSize = 10;
            model.TitleColor = OxyColor.FromRgb(26, 31, 54);
            model.SubtitleFontSize = 10;
            model.SubtitleColor = OxyColor.FromRgb(198, 40, 40); // 红色当前值

            foreach (var axis in model.Axes)
            {
                axis.AxislineColor = OxyColor.FromRgb(200, 200, 200);
                axis.AxislineThickness = 0.5;
                axis.TickStyle = TickStyle.Outside;
                axis.MajorTickSize = 3;
                axis.MinorTickSize = 0;
                axis.MajorGridlineStyle = LineStyle.None; // profiler 不要网格线
                axis.MinorGridlineStyle = LineStyle.None;
                axis.FontSize = 9;
                axis.TextColor = OxyColor.FromRgb(128, 128, 128);
                axis.TitleFontSize = 9;
                axis.TitleColor = OxyColor.FromRgb(96, 96, 96);
            }
        }

        /// <summary>创建 JMP 风格的 LineSeries</summary>
        private static LineSeries JmpLine(int seriesIndex, string title = "", double thickness = 1.5)
        {
            return new LineSeries
            {
                Color = JmpPalette[seriesIndex % JmpPalette.Length],
                StrokeThickness = thickness,
                MarkerType = MarkerType.Circle,
                MarkerSize = 3,
                MarkerFill = JmpPalette[seriesIndex % JmpPalette.Length],
                Title = title
            };
        }

        /// <summary>创建 JMP 风格的 ScatterSeries</summary>
        private static ScatterSeries JmpScatter(int seriesIndex, double size = 3)
        {
            return new ScatterSeries
            {
                MarkerType = MarkerType.Circle,
                MarkerSize = size,
                MarkerFill = JmpPalette[seriesIndex % JmpPalette.Length]
            };
        }

        /// <summary>参考线（y=x, 零线等）</summary>
        private static LineSeries JmpRefLine()
        {
            return new LineSeries
            {
                Color = OxyColor.FromRgb(176, 176, 176),
                LineStyle = LineStyle.Dash,
                StrokeThickness = 0.8
            };
        }

        /// <summary>JMP 风格图例</summary>
        private static Legend JmpLegend()
        {
            return new Legend
            {
                LegendPosition = LegendPosition.RightTop,
                LegendPlacement = LegendPlacement.Inside,
                LegendFontSize = 9,
                LegendTextColor = OxyColor.FromRgb(96, 96, 96),
                LegendBorder = OxyColors.Transparent,
                LegendBackground = OxyColor.FromArgb(220, 255, 255, 255),
                LegendPadding = 4,
                LegendMargin = 4,
                IsLegendVisible = true
            };
        }

        #endregion
        public class SavedSolution
        {
            public int SolutionId { get; set; }
            public Dictionary<string, object> Factors { get; set; } = new();
            public double CompositeD { get; set; }
            public List<IndividualDesirability> IndividualD { get; set; } = new();
            public DateTime Timestamp { get; set; }
            public string FactorsSummary => string.Join(", ",
                Factors.Select(kv => $"{kv.Key}={kv.Value}"));
        }

        private async Task MaximizeAndRememberAsync()
        {
            await MaximizeDesirabilityAsync();
            if (DesirabilityResult?.Success == true)
            {
                SavedSolutions.Add(new SavedSolution
                {
                    SolutionId = SavedSolutions.Count + 1,
                    Factors = new(DesirabilityResult.OptimalFactors),
                    CompositeD = DesirabilityResult.CompositeD,
                    IndividualD = new(DesirabilityResult.IndividualD),
                    Timestamp = DateTime.Now
                });
                HasSavedSolutions = SavedSolutions.Count > 0;
            }
        }
        private async Task ShowSetDesirabilityDialogAsync()
        {
            var batchId = IsDirectImportMode ? "" : SelectedOlsBatch?.BatchId ?? _currentBatchId;

            // 加载已有配置
            var existingConfigs = await _desirabilityService.LoadConfigAsync(batchId);
            var realNames = GetRealResponseNames();
            // 构建弹窗数据
            var dialogConfigs = realNames.Select(name =>
            {
                var existing = existingConfigs.FirstOrDefault(c => c.ResponseName == name);
                return existing ?? new DesirabilityResponseConfig
                {
                    ResponseName = name,
                    Goal = DesirabilityGoal.Maximize,
                    Lower = 0,
                    Upper = 100,
                    Target = 100,
                    Importance = 3,
                    Shape = 1.0
                };
            }).ToList();

            // 弹窗
            bool confirmed = false;
            // ★ v8: 收集每个响应变量的数据范围，传给弹窗用于智能默认值
            var dataRanges = new Dictionary<string, (double min, double max)>();
            if (_lastProfilerData?.Factors != null)
            {
                // 从 profiler 数据中获取响应值范围
                foreach (var kv in _lastProfilerData.Factors)
                {
                    if (kv.Value.Y.Count > 0)
                    {
                        double yMin = kv.Value.Y.Min();
                        double yMax = kv.Value.Y.Max();
                        if (!dataRanges.ContainsKey(_selectedResponseName))
                            dataRanges[_selectedResponseName] = (yMin, yMax);
                        else
                        {
                            var existing = dataRanges[_selectedResponseName];
                            dataRanges[_selectedResponseName] = (
                                Math.Min(existing.min, yMin),
                                Math.Max(existing.max, yMax));
                        }
                    }
                }
            }
            // 如果 profiler 没数据，尝试从直接导入数据获取
            if (dataRanges.Count == 0 && IsDirectImportMode)
            {
                foreach (var kv in _directImportResponsesData)
                {
                    if (kv.Value.Count > 0)
                        dataRanges[kv.Key] = (kv.Value.Min(), kv.Value.Max());
                }
            }

            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                var dialog = new Views.DesirabilityConfigDialog(dialogConfigs, _desirabilityService, dataRanges);
                dialog.Owner = System.Windows.Application.Current.MainWindow;
                dialog.ShowDialog();
                confirmed = dialog.Confirmed;
                if (confirmed) dialogConfigs = dialog.ResultConfigs;
            });

            if (confirmed)
            {
                await _desirabilityService.SaveConfigAsync(batchId, dialogConfigs);

                // ★ 新增: 保存后立即刷新
                _currentDesirabilityConfigs = dialogConfigs;

                // 如果意愿行已显示，立即刷新图表
                if (IsDesirabilityVisible && IsProfilerLoaded)
                {
                    BuildDesirabilityPlotsFromProfiler();
                }

                OlsStatusText =string.Format(_localizationService.GetString("Doe_Analysis_Status_ConfigSave", "Desirability 配置已保存 ({0} 个响应)"),dialogConfigs.Count);
            }
        }
        private async Task SaveDesirabilityValuesAsync()
        {
            // 获取所有实验数据的因子值
            // 用 OLS 模型预测每一行的响应值，计算 D
            // 保存到数据库 + 显示到界面

            try
            {
                IsLoading = true;
                OlsStatusText = _localizationService.GetString("Doe_Analysis_Status_InCalculator", "正在计算所有行的 Desirability...");

                // TODO: 调用 Python engine.evaluate_all_rows()
                // 结果保存到 _repository
                // 更新 UI 显示 D 值列

                OlsStatusText = _localizationService.GetString("Doe_Analysis_Status_ValueSave", "Desirability 值已保存");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "保存意愿失败");
            }
            finally { IsLoading = false; }
        }
        private async Task SaveDesirabilityFormulaAsync()
        {
            // 调用 Python engine.export_formula()
            // 复制到剪贴板或保存为文件
            try
            {
                // TODO: 调用 Python
                // var formulaJson = engine.export_formula();
                // Clipboard.SetText(formulaJson);
                _dialogService.ShowInfo("Desirability 公式已复制到剪贴板", "保存成功");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "保存公式失败");
            }
        }


        private class DirectImportRawData
        {
            public List<string> Headers { get; set; } = new();
            public List<List<object?>> ColumnData { get; set; } = new();
            public List<bool> IsNumericColumn { get; set; } = new();
            public int DataRowCount { get; set; }
            public List<string> Errors { get; set; } = new();
        }

        // ── 直接导入结果数据类 ──
        private class DirectImportResult
        {
            public List<string> FactorNames { get; set; } = new();
            public List<string> ResponseNames { get; set; } = new();
            public Dictionary<string, string> FactorTypes { get; set; } = new();
            public List<Dictionary<string, object>> FactorsData { get; set; } = new();
            public Dictionary<string, List<double>> ResponsesData { get; set; } = new();
            public int DataCount { get; set; }
            public List<string> Errors { get; set; } = new();
        }
        private class EvolutionPoint { [JsonProperty("data_count")] public int DataCount { get; set; } [JsonProperty("r_squared")] public double RSquared { get; set; } }
        private class TrainingDataRecord { [JsonProperty("factors")] public Dictionary<string, double>? Factors { get; set; } [JsonProperty("response")] public double Response { get; set; } [JsonProperty("source")] public string? Source { get; set; } [JsonProperty("batch_name")] public string? BatchName { get; set; } [JsonProperty("timestamp")] public string? Timestamp { get; set; } }
        private class TrainingDataPoint { public Dictionary<string, double> Factors { get; set; } = new(); public double Response { get; set; } }
        // Pareto 效应项（从 Python effects_pareto 返回）
        private class ParetoEffectItem
        {
            [JsonProperty("term")] public string Term { get; set; } = "";
            [JsonProperty("t_value")] public double TValue { get; set; }
            [JsonProperty("abs_t")] public double AbsT { get; set; }
            [JsonProperty("p_value")] public double PValue { get; set; }
            [JsonProperty("significant")] public bool Significant { get; set; }
            [JsonProperty("bonferroni_significant")] public bool? BonferroniSignificant { get; set; }
            [JsonProperty("df")] public int DF { get; set; } = 1;
            [JsonProperty("effect")] public double Effect { get; set; }
        }

        // 残差诊断数据（从 Python residual_diagnostics 返回）
        private class ResidualDiagnostics
        {
            [JsonProperty("normal_probability")] public NormalProbData NormalProbability { get; set; } = new();
            [JsonProperty("residuals_vs_fitted")] public ResidVsFittedData ResidualsVsFitted { get; set; } = new();
            [JsonProperty("residuals_vs_order")] public ResidVsOrderData ResidualsVsOrder { get; set; } = new();
            [JsonProperty("residuals_histogram")] public HistogramData ResidualsHistogram { get; set; } = new();
       
        }

        private class NormalProbData
        {
            [JsonProperty("ordered_residuals")] public List<double> OrderedResiduals { get; set; } = new();
            [JsonProperty("percent")] public List<double> Percent { get; set; } = new();
        }
        private class ParetoResult
        {
            [JsonProperty("effects")] public List<ParetoEffectItem> Effects { get; set; } = new();
            [JsonProperty("log_worth_crit")] public double? LogWorthCrit { get; set; }
            [JsonProperty("alpha")] public double? Alpha { get; set; }
            [JsonProperty("measure")] public string? Measure { get; set; }
            [JsonProperty("t_crit")] public double? TCrit { get; set; }
            [JsonProperty("df_error")] public int DfError { get; set; }
        }
        private class ResidVsFittedData
        {
            [JsonProperty("fitted")] public List<double> Fitted { get; set; } = new();
            [JsonProperty("residuals")] public List<double> Residuals { get; set; } = new();
        }

        private class ResidVsOrderData
        {
            [JsonProperty("observation")] public List<int> Order { get; set; } = new();
            [JsonProperty("residuals")] public List<double> Residuals { get; set; } = new();
        }

        private class HistogramData
        {
            [JsonProperty("bin_edges")] public List<double> BinEdges { get; set; } = new();
            [JsonProperty("frequencies")] public List<int> Frequencies { get; set; } = new();
        }

    

        // ── ★ 新增 v5: 预测刻画器 + 最优化 反序列化类 ──

        private class ProfilerResult
        {
            [JsonProperty("factors")] public Dictionary<string, ProfilerFactorData>? Factors { get; set; }
            [JsonProperty("current_predicted")] public double CurrentPredicted { get; set; }
            [JsonProperty("response_name")] public string ResponseName { get; set; } = "";

        }

        private class ProfilerFactorData
        {
            [JsonProperty("x")] public List<object> XRaw { get; set; } = new();
            [JsonProperty("y")] public List<double> Y { get; set; } = new();
            [JsonProperty("is_categorical")] public bool IsCategorical { get; set; }
            [JsonProperty("current_value")] public object? CurrentValue { get; set; }
            [JsonProperty("range")] public List<double>? Range { get; set; }
            [JsonProperty("levels")] public List<string>? Levels { get; set; }
            [JsonProperty("y_lower")] public List<double> YLower { get; set; } = new();
            [JsonProperty("y_upper")] public List<double> YUpper { get; set; } = new();
            /// <summary>连续因子的 X 值（double 列表）</summary>
            public List<double> X => IsCategorical ? new() : XRaw.Select(v => Convert.ToDouble(v)).ToList();
            /// <summary>类别因子的 X 标签</summary>
            public List<string> X_Labels => IsCategorical ? XRaw.Select(v => v?.ToString() ?? "").ToList() : new();
            /// <summary>连续因子的当前值</summary>
            public double CurrentNumericValue => IsCategorical ? 0 : Convert.ToDouble(CurrentValue ?? 0);
        }
        private class SurfaceData
        {
            [JsonProperty("x")] public List<double> X { get; set; } = new();
            [JsonProperty("y")] public List<double> Y { get; set; } = new();
            [JsonProperty("z")] public List<List<double>> Z { get; set; } = new();
            [JsonProperty("x_label")] public string XLabel { get; set; } = "";
            [JsonProperty("y_label")] public string YLabel { get; set; } = "";
        }
        private class OptimalResult
        {
            [JsonProperty("optimal_factors")] public Dictionary<string, object> OptimalFactors { get; set; } = new();
            [JsonProperty("predicted_response")] public double PredictedResponse { get; set; }
            [JsonProperty("maximize")] public bool Maximize { get; set; }
            [JsonProperty("success")] public bool Success { get; set; }
            [JsonProperty("in_range")] public bool InRange { get; set; }
            [JsonProperty("note")] public string Note { get; set; } = "";
            [JsonProperty("error")] public string? Error { get; set; }
        }

        // ── 异常点 ──

        public class OutlierItem : BindableBase
        {
            public int Index { get; set; }
            public double Actual { get; set; }
            public double Predicted { get; set; }
            public double Residual { get; set; }
            public double StdResidual { get; set; }
            public double CooksD { get; set; }
            public double Leverage { get; set; }
            public string Reasons { get; set; } = "";

            private bool _isExcluded;
            public bool IsExcluded { get => _isExcluded; set => SetProperty(ref _isExcluded, value); }
        }

        // 反序列化用
        private class OutlierAnalysisResult
        {
            [JsonProperty("outliers")] public List<OutlierData> Outliers { get; set; } = new();
            [JsonProperty("thresholds")] public ThresholdData Thresholds { get; set; } = new();
            [JsonProperty("total_observations")] public int TotalObservations { get; set; }
            [JsonProperty("outlier_count")] public int OutlierCount { get; set; }
        }
        private class OutlierData
        {
            [JsonProperty("index")] public int Index { get; set; }
            [JsonProperty("actual")] public double Actual { get; set; }
            [JsonProperty("predicted")] public double Predicted { get; set; }
            [JsonProperty("residual")] public double Residual { get; set; }
            [JsonProperty("std_residual")] public double StdResidual { get; set; }
            [JsonProperty("cooks_d")] public double CooksD { get; set; }
            [JsonProperty("leverage")] public double Leverage { get; set; }
            [JsonProperty("reasons")] public List<string> Reasons { get; set; } = new();
        }
        private class ThresholdData
        {
            [JsonProperty("cooks_d")] public double CooksD { get; set; }
            [JsonProperty("leverage")] public double Leverage { get; set; }
            [JsonProperty("std_residual")] public double StdResidual { get; set; }
        }

        // ── 主效应 / 交互效应 ──

        private class MainEffectPoint
        {
            [JsonProperty("level")] public object Level { get; set; } = 0.0;
            [JsonProperty("mean")] public double Mean { get; set; }
        }
        private class InteractionEffectData
        {
            [JsonProperty("factor1")] public string Factor1 { get; set; } = "";
            [JsonProperty("factor2")] public string Factor2 { get; set; } = "";
            [JsonProperty("data")] public List<InteractionPoint> Data { get; set; } = new();
        }
        private class InteractionPoint
        {
            [JsonProperty("f1")] public object F1 { get; set; } = 0.0;
            [JsonProperty("f2")] public object F2 { get; set; } = 0.0;
            [JsonProperty("mean")] public double Mean { get; set; }
        }
        // 新增统一显示类（编码和未编码共用）:
        public class CoefficientDisplayRow
        {
            public string Term { get; set; } = "";
            public double Coefficient { get; set; }
            public double StdError { get; set; }
            public double TValue { get; set; }
            public double PValue { get; set; }
            public double? VIF { get; set; }
            public double? Effect { get; set; }      // ★ v17 新增
            /// <summary>p &lt; 0.05 标红</summary>
            public bool IsSignificant => PValue < 0.05;
        }
        // ── Tukey HSD ──

        public class TukeyComparisonItem
        {
            public string Group1 { get; set; } = "";
            public string Group2 { get; set; } = "";
            public double MeanDiff { get; set; }
            public double PValue { get; set; }
            public double CILower { get; set; }
            public double CIUpper { get; set; }
            public bool Significant { get; set; }

            public string ComparisonText => $"{Group1} vs {Group2}";
            public string DiffText => $"{MeanDiff:+0.0000;-0.0000}";
            public string PValueText => PValue < 0.001 ? "p<0.001" : $"p={PValue:F4}";
            public string CIText => $"[{CILower:F2}, {CIUpper:F2}]";
            public string SignificanceMarker => Significant ? "★" : "";
        }
        public class LoadingStepItem : BindableBase
        {
            public string StepName { get; set; } = "";

            private bool _isCompleted;
            public bool IsCompleted
            {
                get => _isCompleted;
                set { if (SetProperty(ref _isCompleted, value)) { RaisePropertyChanged(nameof(IsPending)); RaisePropertyChanged(nameof(IsActive)); } }
            }

            private bool _isActive;
            public bool IsActive
            {
                get => _isActive;
                set { if (SetProperty(ref _isActive, value)) { RaisePropertyChanged(nameof(IsPending)); RaisePropertyChanged(nameof(IsCompleted)); } }
            }

            public bool IsPending => !IsCompleted && !IsActive;
        }
        // 反序列化用
        private class TukeyHSDResult
        {
            [JsonProperty("factor_name")] public string FactorName { get; set; } = "";
            [JsonProperty("comparisons")] public List<TukeyComparison> Comparisons { get; set; } = new();
            [JsonProperty("group_means")] public Dictionary<string, double> GroupMeans { get; set; } = new();
            [JsonProperty("mse")] public double MSE { get; set; }
            [JsonProperty("alpha")] public double Alpha { get; set; }
            [JsonProperty("error")] public string? Error { get; set; }
        }
        private class TukeyComparison
        {
            [JsonProperty("group1")] public string Group1 { get; set; } = "";
            [JsonProperty("group2")] public string Group2 { get; set; } = "";
            [JsonProperty("mean_diff")] public double MeanDiff { get; set; }
            [JsonProperty("p_value")] public double PValue { get; set; }
            [JsonProperty("ci_lower")] public double CILower { get; set; }
            [JsonProperty("ci_upper")] public double CIUpper { get; set; }
            [JsonProperty("significant")] public bool Significant { get; set; }
        }

     
     
    }


}
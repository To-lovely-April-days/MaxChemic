using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Media;
using MaxChemical.Infrastructure.DOE;
using MaxChemical.Logging;
using MaxChemical.Modules.DOE.Data;
using MaxChemical.Modules.DOE.Models;
using MaxChemical.Modules.DOE.Services;
using Newtonsoft.Json;
using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Series;
using Prism.Commands;
using Prism.Mvvm;

namespace MaxChemical.Modules.DOE.ViewModels
{
    /// <summary>
    /// GPR 模型 Tab 条目（用于模型切换条）
    /// </summary>
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

    /// <summary>
    /// 选中模型的详情 ViewModel
    /// </summary>
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
        public string BatchCountHint => "累计数据";
        public string RSquaredText => IsActive ? $"{RSquared:F4}" : "—";
        public string RMSEText => IsActive ? $"{RMSE:F2}" : "—";
        public string LastTrainedText => LastTrainedTime?.ToString("yyyy-MM-dd HH:mm:ss") ?? "—";
        public string StatusSummary => IsActive
            ? $"已激活 · {DataCount}组 · 上次训练 {LastTrainedTime:HH:mm}"
            : $"冷启动 {DataCount}/6";
    }

    /// <summary>
    /// 最优参数因子条目（带进度条宽度）
    /// </summary>
    public class OptimalFactorItem
    {
        public string Key { get; set; } = "";
        public double Value { get; set; }
        public double BarWidth { get; set; } // 像素宽度，根据范围百分比计算
    }

    /// <summary>
    /// GPR 模型管理 ViewModel — 多模型支持
    /// </summary>
    public class DOEGPRModelViewModel : BindableBase
    {
        private readonly IGPRModelService _gprService;
        private readonly IDOERepository _repository;
        private readonly IFlowParameterProvider _paramProvider;
        private readonly ILogService _logger;

        private ObservableCollection<GPRModelTabItem> _models = new();
        private GPRModelDetail _selectedModel = new();
        private List<OptimalFactorItem> _optimalFactors = new();
        private string _optimalResponseText = "—";
        private string _optimalConfidenceText = "";
        private PlotModel? _evolutionPlot;
        private PlotModel? _sensitivityPlot;
        private bool _isLoading;

        public DOEGPRModelViewModel(IGPRModelService gprService, IDOERepository repository,
            IFlowParameterProvider paramProvider, ILogService logger)
        {
            _gprService = gprService;
            _repository = repository;
            _paramProvider = paramProvider;
            _logger = logger?.ForContext<DOEGPRModelViewModel>() ?? throw new ArgumentNullException(nameof(logger));

            RetrainCommand = new DelegateCommand(async () => await RetrainAsync());
            FindOptimalCommand = new DelegateCommand(async () => await FindOptimalAsync());
            DeleteModelCommand = new DelegateCommand(async () => await DeleteModelAsync());
        }

        public ObservableCollection<GPRModelTabItem> Models { get => _models; set => SetProperty(ref _models, value); }
        public GPRModelDetail SelectedModel { get => _selectedModel; set => SetProperty(ref _selectedModel, value); }
        public List<OptimalFactorItem> OptimalFactors { get => _optimalFactors; set => SetProperty(ref _optimalFactors, value); }
        public string OptimalResponseText { get => _optimalResponseText; set => SetProperty(ref _optimalResponseText, value); }
        public string OptimalConfidenceText { get => _optimalConfidenceText; set => SetProperty(ref _optimalConfidenceText, value); }
        public PlotModel? EvolutionPlot { get => _evolutionPlot; set => SetProperty(ref _evolutionPlot, value); }
        public PlotModel? SensitivityPlot { get => _sensitivityPlot; set => SetProperty(ref _sensitivityPlot, value); }
        public bool IsLoading { get => _isLoading; set => SetProperty(ref _isLoading, value); }

        public DelegateCommand RetrainCommand { get; }
        public DelegateCommand FindOptimalCommand { get; }
        public DelegateCommand DeleteModelCommand { get; }

        public async Task LoadAsync()
        {
            try
            {
                IsLoading = true;
                var flowId = _paramProvider.GetCurrentFlowId();
                if (string.IsNullOrEmpty(flowId)) return;

                var states = await _repository.GetGPRModelsByFlowAsync(flowId);

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

                // 默认选中第一个
                if (tabs.Count > 0)
                    SelectModel(tabs[0].ModelId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "加载 GPR 模型列表失败");
            }
            finally { IsLoading = false; }
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
                var flowId = _paramProvider.GetCurrentFlowId();
                if (string.IsNullOrEmpty(flowId)) return;

                var states = await _repository.GetGPRModelsByFlowAsync(flowId);
                var state = states.FirstOrDefault(s => s.Id == modelId);
                if (state == null) return;

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

                BuildEvolutionPlot(state.EvolutionHistoryJson);
                BuildSensitivityPlot(null); // GPRModelState 无 SensitivityJson
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "加载模型详情失败: {ModelId}", modelId);
            }
        }

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
                        var series = new LineSeries
                        {
                            Color = OxyColor.FromRgb(0x00, 0x78, 0xD4),
                            StrokeThickness = 1.8,
                            MarkerType = MarkerType.Circle,
                            MarkerSize = 2.5,
                            MarkerFill = OxyColor.FromRgb(0x00, 0x78, 0xD4)
                        };
                        foreach (var pt in history)
                            series.Points.Add(new DataPoint(pt.DataCount, pt.RSquared));
                        model.Series.Add(series);
                    }
                }
                catch { }
            }
            EvolutionPlot = model;
        }

        private void BuildSensitivityPlot(string? json)
        {
            var model = new PlotModel();
            if (!string.IsNullOrEmpty(json))
            {
                try
                {
                    var data = JsonConvert.DeserializeObject<Dictionary<string, double>>(json);
                    if (data?.Count > 0)
                    {
                        var sorted = data.OrderByDescending(kv => kv.Value).ToList();
                        var catAxis = new CategoryAxis { Position = AxisPosition.Bottom };
                        model.Axes.Add(catAxis);
                        model.Axes.Add(new LinearAxis { Position = AxisPosition.Left, Minimum = 0 });

                        var barSeries = new BarSeries { FillColor = OxyColor.FromRgb(0x00, 0x78, 0xD4) };
                        foreach (var kv in sorted)
                        {
                            catAxis.Labels.Add(kv.Key);
                            barSeries.Items.Add(new BarItem(kv.Value));
                        }
                        model.Series.Add(barSeries);
                    }
                }
                catch { }
            }
            SensitivityPlot = model;
        }

        private async Task RetrainAsync()
        {
            try
            {
                IsLoading = true;
                var flowId = _paramProvider.GetCurrentFlowId();
                if (string.IsNullOrEmpty(flowId)) return;

                await _gprService.ResetModelAsync(flowId, keepData: true);
                await _gprService.TrainModelAsync();
                await _gprService.SaveStateAsync(flowId);
                await LoadAsync();
            }
            catch (Exception ex) { _logger.LogError(ex, "重新训练失败"); }
            finally { IsLoading = false; }
        }

        private async Task FindOptimalAsync()
        {
            try
            {
                IsLoading = true;
                if (!_gprService.IsActive) return;

                var optimal = _gprService.FindOptimal();
                if (optimal != null)
                {
                    // 进度条宽度按比例（暂简单用 value / max * 200）
                    var maxVal = optimal.OptimalFactors.Values.DefaultIfEmpty(1).Max();
                    OptimalFactors = optimal.OptimalFactors.Select(kv => new OptimalFactorItem
                    {
                        Key = kv.Key,
                        Value = kv.Value,
                        BarWidth = maxVal > 0 ? kv.Value / maxVal * 180 : 0
                    }).ToList();
                    OptimalResponseText = $"{optimal.PredictedResponse:F2}";
                    OptimalConfidenceText = $"± {optimal.PredictionStd:F2} std\nCI {optimal.Confidence:F2}";
                }
            }
            catch (Exception ex) { _logger.LogError(ex, "搜索最优失败"); }
            finally { IsLoading = false; }
        }

        private async Task DeleteModelAsync()
        {
            try
            {
                if (SelectedModel.ModelId <= 0) return;
                await _repository.DeleteGPRModelAsync(SelectedModel.ModelId);
                await LoadAsync();
            }
            catch (Exception ex) { _logger.LogError(ex, "删除模型失败"); }
        }

        private class EvolutionPoint
        {
            [JsonProperty("data_count")] public int DataCount { get; set; }
            [JsonProperty("r_squared")] public double RSquared { get; set; }
        }
    }
}

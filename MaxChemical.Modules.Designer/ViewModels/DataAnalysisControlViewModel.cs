using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MaxChemical.Infrastructure.Events;
using MaxChemical.Infrastructure.Services;
using MaxChemical.Logging;
using MaxChemical.Modules.Designer.Models;
using MaxChemical.Modules.Designer.Services;
using MaxChemical.Modules.Designer.Views;
using Newtonsoft.Json;
using OfficeOpenXml.Drawing;
using OxyPlot;
using OxyPlot.Annotations;
using OxyPlot.Axes;
using OxyPlot.Series;
using Prism.Commands;
using Prism.Events;
using Prism.Mvvm;
using Python.Runtime;

namespace MaxChemical.Modules.Designer.ViewModels
{
    public class DataAnalysisControlViewModel : BindableBase, IDisposable
    {
        private readonly IEventAggregator _eventAggregator;
        private readonly IDialogService _dialogService;
        private readonly ILocalizationService _localizationService;
        private readonly ILogService _logger;

        private dynamic _pythonModel;
        private bool _isPythonInitialized;

        #region 绑定属性

        // 输入参数
        private string _pressure = "12";
        public string Pressure
        {
            get => _pressure;
            set => SetProperty(ref _pressure, value);
        }

        private string _temperature = "140";
        public string Temperature
        {
            get => _temperature;
            set => SetProperty(ref _temperature, value);
        }

        private string _h2Flow = "450";
        public string H2Flow
        {
            get => _h2Flow;
            set => SetProperty(ref _h2Flow, value);
        }

        private string _feedFlow = "10.0";
        public string FeedFlow
        {
            get => _feedFlow;
            set => SetProperty(ref _feedFlow, value);
        }

        private string _catAmount = "4.0";
        public string CatAmount
        {
            get => _catAmount;
            set => SetProperty(ref _catAmount, value);
        }

        private string _residenceTime = "4.0";
        public string ResidenceTime
        {
            get => _residenceTime;
            set => SetProperty(ref _residenceTime, value);
        }

        private string _h2Equiv = "1.5";
        public string H2Equiv
        {
            get => _h2Equiv;
            set => SetProperty(ref _h2Equiv, value);
        }

        // 预测结果
        private Visibility _predictionResultsVisibility = Visibility.Collapsed;
        public Visibility PredictionResultsVisibility
        {
            get => _predictionResultsVisibility;
            set => SetProperty(ref _predictionResultsVisibility, value);
        }

        private string _predictionResultText;
        public string PredictionResultText
        {
            get => _predictionResultText;
            set => SetProperty(ref _predictionResultText, value);
        }

        private string _confidenceIntervalText;
        public string ConfidenceIntervalText
        {
            get => _confidenceIntervalText;
            set => SetProperty(ref _confidenceIntervalText, value);
        }

        private string _predictedConversion;
        public string PredictedConversion
        {
            get => _predictedConversion;
            set => SetProperty(ref _predictedConversion, value);
        }

        private string _confidenceRange;
        public string ConfidenceRange
        {
            get => _confidenceRange;
            set => SetProperty(ref _confidenceRange, value);
        }

        private string _predictionAccuracy;
        public string PredictionAccuracy
        {
            get => _predictionAccuracy;
            set => SetProperty(ref _predictionAccuracy, value);
        }

        private string _processRisk;
        public string ProcessRisk
        {
            get => _processRisk;
            set => SetProperty(ref _processRisk, value);
        }

        private ObservableCollection<RecommendationItem> _recommendations;
        public ObservableCollection<RecommendationItem> Recommendations
        {
            get => _recommendations;
            set => SetProperty(ref _recommendations, value);
        }

        // 模型状态
        private string _modelStatus = "正在初始化...";
        public string ModelStatus
        {
            get => _modelStatus;
            set => SetProperty(ref _modelStatus, value);
        }

        private Brush _modelStatusBrush = Brushes.Orange;
        public Brush ModelStatusBrush
        {
            get => _modelStatusBrush;
            set => SetProperty(ref _modelStatusBrush, value);
        }

        // 图表
        private PlotModel _processWindowPlotModel;
        public PlotModel ProcessWindowPlotModel
        {
            get => _processWindowPlotModel;
            set => SetProperty(ref _processWindowPlotModel, value);
        }

        private PlotModel _sensitivityPlotModel;
        public PlotModel SensitivityPlotModel
        {
            get => _sensitivityPlotModel;
            set => SetProperty(ref _sensitivityPlotModel, value);
        }

        // 选项卡指示器透明度
        private double _flowDesignerIndicatorOpacity = 0;
        public double FlowDesignerIndicatorOpacity
        {
            get => _flowDesignerIndicatorOpacity;
            set => SetProperty(ref _flowDesignerIndicatorOpacity, value);
        }

        private double _stepDesignerIndicatorOpacity = 0;
        public double StepDesignerIndicatorOpacity
        {
            get => _stepDesignerIndicatorOpacity;
            set => SetProperty(ref _stepDesignerIndicatorOpacity, value);
        }

        private double _dataAnalysisIndicatorOpacity = 1;
        public double DataAnalysisIndicatorOpacity
        {
            get => _dataAnalysisIndicatorOpacity;
            set => SetProperty(ref _dataAnalysisIndicatorOpacity, value);
        }

        #endregion

        #region Commands

        public DelegateCommand PredictCommand { get; private set; }
        public DelegateCommand RefreshChartsCommand { get; private set; }
        public DelegateCommand ExportDataCommand { get; private set; }
        public DelegateCommand SwitchToFlowDesignerCommand { get; private set; }
        public DelegateCommand SwitchToStepDesignerCommand { get; private set; }

        #endregion

        #region 反应场景相关

        private ObservableCollection<ReactionScenario> _reactionScenarios;
        public ObservableCollection<ReactionScenario> ReactionScenarios
        {
            get => _reactionScenarios;
            set => SetProperty(ref _reactionScenarios, value);
        }

        private ReactionScenario _selectedReactionScenario;
        public ReactionScenario SelectedReactionScenario
        {
            get => _selectedReactionScenario;
            set
            {
                if (SetProperty(ref _selectedReactionScenario, value))
                {
                    OnReactionScenarioChanged();
                }
            }
        }

        private string _currentScenarioTitle = "硝化还原反应工艺分析";
        public string CurrentScenarioTitle
        {
            get => _currentScenarioTitle;
            set => SetProperty(ref _currentScenarioTitle, value);
        }

        private string _currentScenarioSubtitle = "Hydrogenation Process Analysis";
        public string CurrentScenarioSubtitle
        {
            get => _currentScenarioSubtitle;
            set => SetProperty(ref _currentScenarioSubtitle, value);
        }

        #endregion


        public DataAnalysisControlViewModel(
            IEventAggregator eventAggregator,
            IDialogService dialogService,
            ILocalizationService localizationService)
        {
            _eventAggregator = eventAggregator ?? throw new ArgumentNullException(nameof(eventAggregator));
            _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
            _localizationService = localizationService ?? throw new ArgumentNullException(nameof(localizationService));
            _logger = new LogService().ForContext<DataAnalysisControlViewModel>();

            _recommendations = new ObservableCollection<RecommendationItem>();
            // 初始化反应场景
            InitializeReactionScenarios();
            InitializeCommands();
            InitializePython();
        }
        // 添加初始化场景方法
        private void InitializeReactionScenarios()
        {
            ReactionScenarios = new ObservableCollection<ReactionScenario>
    {
        new ReactionScenario
        {
            Id = "hydrogenation",
            Name = "硝化还原反应",
            Description = "芳香硝基化合物的催化加氢还原",
            Title = "硝化还原反应工艺分析",
            Subtitle = "Hydrogenation Process Analysis",
            DefaultParameters = new Dictionary<string, string>
            {
                { "Pressure", "12" },
                { "Temperature", "140" },
                { "H2Flow", "450" },
                { "FeedFlow", "10.0" },
                { "CatAmount", "4.0" },
                { "ResidenceTime", "4.0" },
                { "H2Equiv", "1.5" }
            }
        },
        new ReactionScenario
        {
            Id = "oxidation",
            Name = "氧化反应",
            Description = "有机物选择性氧化过程",
            Title = "氧化反应工艺分析",
            Subtitle = "Oxidation Process Analysis",
            DefaultParameters = new Dictionary<string, string>
            {
                { "Pressure", "8" },
                { "Temperature", "120" },
                { "H2Flow", "350" },
                { "FeedFlow", "8.0" },
                { "CatAmount", "3.5" },
                { "ResidenceTime", "3.5" },
                { "H2Equiv", "1.2" }
            }
        },
        new ReactionScenario
        {
            Id = "esterification",
            Name = "酯化反应",
            Description = "羧酸与醇的酯化缩合",
            Title = "酯化反应工艺分析",
            Subtitle = "Esterification Process Analysis",
            DefaultParameters = new Dictionary<string, string>
            {
                { "Pressure", "5" },
                { "Temperature", "100" },
                { "H2Flow", "200" },
                { "FeedFlow", "12.0" },
                { "CatAmount", "2.5" },
                { "ResidenceTime", "5.0" },
                { "H2Equiv", "1.0" }
            }
        },
        new ReactionScenario
        {
            Id = "alkylation",
            Name = "烷基化反应",
            Description = "芳烃或烯烃的烷基化",
            Title = "烷基化反应工艺分析",
            Subtitle = "Alkylation Process Analysis",
            DefaultParameters = new Dictionary<string, string>
            {
                { "Pressure", "15" },
                { "Temperature", "160" },
                { "H2Flow", "500" },
                { "FeedFlow", "9.0" },
                { "CatAmount", "5.0" },
                { "ResidenceTime", "4.5" },
                { "H2Equiv", "1.8" }
            }
        },
        new ReactionScenario
        {
            Id = "hydroformylation",
            Name = "羰基化反应",
            Description = "烯烃与一氧化碳和氢气的反应",
            Title = "羰基化反应工艺分析",
            Subtitle = "Hydroformylation Process Analysis",
            DefaultParameters = new Dictionary<string, string>
            {
                { "Pressure", "10" },
                { "Temperature", "130" },
                { "H2Flow", "400" },
                { "FeedFlow", "11.0" },
                { "CatAmount", "3.8" },
                { "ResidenceTime", "3.8" },
                { "H2Equiv", "1.3" }
            }
        },
        new ReactionScenario
        {
            Id = "polymerization",
            Name = "聚合反应",
            Description = "单体的催化聚合过程",
            Title = "聚合反应工艺分析",
            Subtitle = "Polymerization Process Analysis",
            DefaultParameters = new Dictionary<string, string>
            {
                { "Pressure", "6" },
                { "Temperature", "90" },
                { "H2Flow", "300" },
                { "FeedFlow", "15.0" },
                { "CatAmount", "2.0" },
                { "ResidenceTime", "6.0" },
                { "H2Equiv", "0.8" }
            }
        },
        new ReactionScenario
        {
            Id = "dehydrogenation",
            Name = "脱氢反应",
            Description = "饱和烃的催化脱氢",
            Title = "脱氢反应工艺分析",
            Subtitle = "Dehydrogenation Process Analysis",
            DefaultParameters = new Dictionary<string, string>
            {
                { "Pressure", "3" },
                { "Temperature", "180" },
                { "H2Flow", "250" },
                { "FeedFlow", "7.0" },
                { "CatAmount", "4.5" },
                { "ResidenceTime", "2.5" },
                { "H2Equiv", "0.5" }
            }
        },
        new ReactionScenario
        {
            Id = "custom",
            Name = "自定义场景",
            Description = "根据实际需求自定义参数",
            Title = "自定义反应工艺分析",
            Subtitle = "Custom Process Analysis",
            DefaultParameters = null // 自定义场景不设置默认值
        }
    };

            // 默认选择第一个场景
            SelectedReactionScenario = ReactionScenarios.FirstOrDefault();
        }

        // 添加场景切换处理方法
        private void OnReactionScenarioChanged()
        {
            if (SelectedReactionScenario == null) return;

            try
            {
                _logger.LogInformation("切换反应场景: {ScenarioName}", SelectedReactionScenario.Name);

                // 更新标题
                CurrentScenarioTitle = SelectedReactionScenario.Title;
                CurrentScenarioSubtitle = SelectedReactionScenario.Subtitle;

                // 如果有默认参数，则应用
                if (SelectedReactionScenario.DefaultParameters != null)
                {
                    ApplyDefaultParameters(SelectedReactionScenario.DefaultParameters);

                    // 隐藏之前的预测结果
                    PredictionResultsVisibility = Visibility.Collapsed;

                    //_dialogService?.ShowInfo($"已切换到 {SelectedReactionScenario.Name}，参数已重置为推荐值", "场景切换");
                }
                else
                {
                    // 自定义场景，保留当前参数
                    _dialogService?.ShowInfo("已切换到自定义场景，可自由设置参数", "场景切换");
                }

                _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish($"已切换到{SelectedReactionScenario.Name}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "切换反应场景失败");
                _dialogService?.ShowError($"切换场景失败: {ex.Message}", "错误");
            }
        }

        // 添加应用默认参数方法
        private void ApplyDefaultParameters(Dictionary<string, string> parameters)
        {
            if (parameters.TryGetValue("Pressure", out var pressure))
                Pressure = pressure;

            if (parameters.TryGetValue("Temperature", out var temperature))
                Temperature = temperature;

            if (parameters.TryGetValue("H2Flow", out var h2Flow))
                H2Flow = h2Flow;

            if (parameters.TryGetValue("FeedFlow", out var feedFlow))
                FeedFlow = feedFlow;

            if (parameters.TryGetValue("CatAmount", out var catAmount))
                CatAmount = catAmount;

            if (parameters.TryGetValue("ResidenceTime", out var residenceTime))
                ResidenceTime = residenceTime;

            if (parameters.TryGetValue("H2Equiv", out var h2Equiv))
                H2Equiv = h2Equiv;
        }

        private void InitializeCommands()
        {
            PredictCommand = new DelegateCommand(ExecutePredict);
            RefreshChartsCommand = new DelegateCommand(ExecuteRefreshCharts);
            ExportDataCommand = new DelegateCommand(ExecuteExportData);
            SwitchToFlowDesignerCommand = new DelegateCommand(ExecuteSwitchToFlowDesigner);
            SwitchToStepDesignerCommand = new DelegateCommand(ExecuteSwitchToStepDesigner);
        }

        private void InitializePython()
        {
            try
            {
                _logger.LogInformation("开始初始化Python模型");

                // 使用全局单例管理器
                if (!PythonEnvironmentManager.Instance.IsInitialized)
                {
                    var success = PythonEnvironmentManager.Instance.Initialize();
                    if (!success)
                    {
                        throw new Exception("Python环境初始化失败");
                    }
                }

                // 创建模型实例（每个ViewModel独立）
                _pythonModel = PythonEnvironmentManager.Instance.CreateModel();
                _isPythonInitialized = true;

                ModelStatus = "已初始化";
                ModelStatusBrush = new SolidColorBrush(Color.FromRgb(22, 198, 12));

                _logger.LogInformation("Python模型初始化成功");

                // 自动加载图表
                LoadChartsOnStartup();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Python模型初始化失败");
                ModelStatus = "初始化失败";
                ModelStatusBrush = new SolidColorBrush(Color.FromRgb(231, 72, 86));

                _dialogService?.ShowError($"Python模型初始化失败: {ex.Message}", "初始化错误");
            }
        }

        private void LoadChartsOnStartup()
        {
            if (!_isPythonInitialized) return;

            try
            {
                using (Py.GIL())
                {
                    dynamic windowInfo = _pythonModel.get_process_window_data();
                    UpdateProcessWindowPlot(windowInfo);

                    dynamic sensitivityInfo = _pythonModel.get_parameter_sensitivity();
                    UpdateSensitivityPlot(sensitivityInfo);
                }

                _logger.LogInformation("图表加载成功");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "自动加载图表失败");
            }
        }

        private void ExecutePredict()
        {
            if (!_isPythonInitialized)
            {
                _dialogService?.ShowError("系统未初始化", "错误");
                return;
            }

            try
            {
                if (!ValidateInputs(out var errorMessage))
                {
                    _dialogService?.ShowError(errorMessage, "输入验证");
                    return;
                }

                double pressure = double.Parse(Pressure);
                double temperature = double.Parse(Temperature);
                double h2Flow = double.Parse(H2Flow);
                double feedFlow = double.Parse(FeedFlow);
                double catAmount = double.Parse(CatAmount);
                double residenceTime = double.Parse(ResidenceTime);
                double h2Equiv = double.Parse(H2Equiv);

                _logger.LogInformation("开始预测分析: 压力={Pressure}, 温度={Temperature}", pressure, temperature);

                using (Py.GIL())
                {
                    dynamic result = _pythonModel.predict_conversion(
                        pressure, temperature, h2Flow, feedFlow,
                        catAmount, residenceTime, h2Equiv);

                    string jsonResult = result.ToString();
                    var predictionResult = JsonConvert.DeserializeObject<PredictionResult>(jsonResult);

                    UpdatePredictionResults(predictionResult, temperature, pressure, catAmount);
                }

                _logger.LogInformation("预测分析完成");
                _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish("预测分析完成");
            }
            catch (FormatException)
            {
                _dialogService?.ShowError("请检查输入数据格式", "输入错误");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "预测分析失败");
                _dialogService?.ShowError($"预测失败: {ex.Message}", "错误");
            }
        }

        private bool ValidateInputs(out string errorMessage)
        {
            errorMessage = string.Empty;

            var inputs = new Dictionary<string, string>
            {
                { "压力", Pressure },
                { "温度", Temperature },
                { "H₂流量", H2Flow },
                { "原料流量", FeedFlow },
                { "催化剂用量", CatAmount },
                { "停留时间", ResidenceTime },
                { "H₂当量", H2Equiv }
            };

            foreach (var input in inputs)
            {
                if (string.IsNullOrWhiteSpace(input.Value))
                {
                    errorMessage = $"{input.Key}不能为空";
                    return false;
                }

                if (!double.TryParse(input.Value, out double value))
                {
                    errorMessage = $"{input.Key}格式不正确";
                    return false;
                }

                if (value <= 0)
                {
                    errorMessage = $"{input.Key}必须大于0";
                    return false;
                }
            }

            return true;
        }

        private void UpdatePredictionResults(PredictionResult result, double temp, double pressure, double catAmount)
        {
            PredictionResultsVisibility = Visibility.Visible;

            PredictionResultText = $"预测转化率: {result.Conversion:F2}%";
            ConfidenceIntervalText = $"置信区间: [{result.LowerBound:F2}%, {result.UpperBound:F2}%]\n" +
                                    $"预测不确定性: ±{result.Uncertainty:F3}";

            PredictedConversion = $"{result.Conversion:F1}%";
            ConfidenceRange = $"±{(result.UpperBound - result.LowerBound) / 2:F1}%";
            PredictionAccuracy = result.Uncertainty < 0.1 ? "高" :
                                result.Uncertainty < 0.3 ? "中" : "低";
            ProcessRisk = result.Conversion > 85 ? "低风险" :
                         result.Conversion > 70 ? "中风险" : "高风险";

            UpdateProcessRecommendations(result, temp, pressure, catAmount);
        }

        private void UpdateProcessRecommendations(PredictionResult result, double temp, double pressure, double catAmount)
        {
            Recommendations.Clear();

            if (result.Conversion < 70)
            {
                if (temp < 130)
                    Recommendations.Add(new RecommendationItem { Text = "• 建议提高反应温度至130-140°C" });
                if (pressure < 10)
                    Recommendations.Add(new RecommendationItem { Text = "• 建议提高反应压力至10-12bar" });
                if (catAmount < 3.5)
                    Recommendations.Add(new RecommendationItem { Text = "• 建议增加催化剂用量至3.5-4.5%" });
            }
            else if (result.Conversion < 85)
            {
                Recommendations.Add(new RecommendationItem { Text = "• 转化率可接受，可考虑小幅优化" });
                if (temp < 140)
                    Recommendations.Add(new RecommendationItem { Text = "• 可适当提高温度改善转化率" });
            }
            else
            {
                Recommendations.Add(new RecommendationItem { Text = "• 转化率良好，工艺条件优秀" });
                Recommendations.Add(new RecommendationItem { Text = "• 建议保持当前操作参数" });
            }

            if (result.Uncertainty > 0.3)
            {
                Recommendations.Add(new RecommendationItem { Text = "• 预测不确定性较高，建议谨慎操作" });
            }
        }

        private void ExecuteRefreshCharts()
        {
            if (!_isPythonInitialized)
            {
                _dialogService?.ShowError("Python环境未初始化", "警告");
                return;
            }

            try
            {
                _logger.LogInformation("刷新图表");

                using (Py.GIL())
                {
                    dynamic windowInfo = _pythonModel.get_process_window_data();
                    UpdateProcessWindowPlot(windowInfo);

                    dynamic sensitivityInfo = _pythonModel.get_parameter_sensitivity();
                    UpdateSensitivityPlot(sensitivityInfo);
                }

                _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish("图表已刷新");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "刷新图表失败");
                _dialogService?.ShowError($"刷新图表失败: {ex.Message}", "错误");
            }
        }

        private void UpdateProcessWindowPlot(dynamic windowInfo)
        {
            try
            {
                string windowJson = windowInfo.ToString();
                var windowData = JsonConvert.DeserializeObject<ProcessWindowData>(windowJson);

                var model = new PlotModel
                {
                    Title = "工艺操作窗口 (温度 vs 转化率)",
                    Background = OxyColors.White,
                    TitleFontSize = 14
                };

                model.Axes.Add(new LinearAxis
                {
                    Position = AxisPosition.Bottom,
                    Title = "反应温度 (°C)",
                    Minimum = 80,
                    Maximum = 170,
                    FontSize = 11
                });

                model.Axes.Add(new LinearAxis
                {
                    Position = AxisPosition.Left,
                    Title = "转化率 (%)",
                    Minimum = 0,
                    Maximum = 100,
                    FontSize = 11
                });

                var colors = new[] { OxyColors.Blue, OxyColors.Green, OxyColors.Orange, OxyColors.Red, OxyColors.Purple };

                for (int i = 0; i < windowData.PressureCurves.Count; i++)
                {
                    var pressureData = windowData.PressureCurves[i];
                    var series = new LineSeries
                    {
                        Title = $"压力 {pressureData.Pressure} bar",
                        Color = colors[i % colors.Length],
                        StrokeThickness = 2.5
                    };

                    for (int j = 0; j < pressureData.Temperatures.Count; j++)
                    {
                        series.Points.Add(new OxyPlot.DataPoint(pressureData.Temperatures[j], pressureData.Conversions[j]));
                    }

                    model.Series.Add(series);
                }

                if (windowData.HistoricalPoints?.Any() == true)
                {
                    var historicalSeries = new ScatterSeries
                    {
                        Title = "历史实验数据",
                        MarkerType = MarkerType.Circle,
                        MarkerSize = 6,
                        MarkerFill = OxyColors.Black,
                        MarkerStroke = OxyColors.White,
                        MarkerStrokeThickness = 1
                    };

                    foreach (var point in windowData.HistoricalPoints)
                    {
                        historicalSeries.Points.Add(new ScatterPoint(point.Temperature, point.Conversion));
                    }

                    model.Series.Add(historicalSeries);
                }

                if (windowData.OptimalRegion != null)
                {
                    var optimalRegion = new RectangleAnnotation
                    {
                        MinimumX = windowData.OptimalRegion.TempMin,
                        MaximumX = windowData.OptimalRegion.TempMax,
                        MinimumY = 85,
                        MaximumY = 100,
                        Fill = OxyColor.FromArgb(30, 46, 139, 87),
                        Stroke = OxyColors.Green,
                        StrokeThickness = 2,
                        Text = "最优区域"
                    };
                    model.Annotations.Add(optimalRegion);
                }

                ProcessWindowPlotModel = model;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "更新工艺窗口图失败");
            }
        }

        private void UpdateSensitivityPlot(dynamic sensitivityInfo)
        {
            try
            {
                string sensitivityJson = sensitivityInfo.ToString();
                var sensitivityData = JsonConvert.DeserializeObject<SensitivityAnalysisData>(sensitivityJson);

                var model = new PlotModel
                {
                    Title = "参数敏感性",
                    Background = OxyColors.White,
                    TitleFontSize = 12,
                    PlotMargins = new OxyThickness(40, 10, 10, 40)
                };

                var categoryAxis = new CategoryAxis
                {
                    Position = AxisPosition.Left,
                    FontSize = 9,
                    LabelField = "Parameter"
                };

                var valueAxis = new LinearAxis
                {
                    Position = AxisPosition.Bottom,
                    Title = "敏感性",
                    FontSize = 9,
                    TitleFontSize = 10
                };

                model.Axes.Add(categoryAxis);
                model.Axes.Add(valueAxis);

                var barSeries = new BarSeries
                {
                    StrokeThickness = 1,
                    StrokeColor = OxyColors.Black,
                    LabelPlacement = LabelPlacement.Inside,
                    LabelFormatString = "{0:F1}"
                };

                var topSensitivities = sensitivityData.Sensitivities.Take(6).ToList();

                foreach (var sensitivity in topSensitivities)
                {
                    string shortLabel = sensitivity.ParameterChinese.Length > 4 ?
                        sensitivity.ParameterChinese.Substring(0, 4) :
                        sensitivity.ParameterChinese;

                    categoryAxis.Labels.Add(shortLabel);

                    OxyColor barColor = sensitivity.ImportanceLevel switch
                    {
                        "高敏感" => OxyColors.Red,
                        "中敏感" => OxyColors.Orange,
                        "低敏感" => OxyColors.Yellow,
                        _ => OxyColors.LightGreen
                    };

                    barSeries.Items.Add(new BarItem
                    {
                        Value = sensitivity.Sensitivity,
                        Color = barColor
                    });
                }

                model.Series.Add(barSeries);

                var highSensitivityLine = new LineSeries
                {
                    Title = "高敏感阈值",
                    Color = OxyColors.Red,
                    LineStyle = LineStyle.Dash,
                    StrokeThickness = 1
                };
                highSensitivityLine.Points.Add(new OxyPlot.DataPoint(2.0, -0.5));
                highSensitivityLine.Points.Add(new OxyPlot.DataPoint(2.0, topSensitivities.Count - 0.5));
                model.Series.Add(highSensitivityLine);

                SensitivityPlotModel = model;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "更新敏感性图表失败");
            }
        }

        private void ExecuteExportData()
        {
            _dialogService?.ShowInfo("数据导出功能开发中...", "提示");
        }

        private void ExecuteSwitchToFlowDesigner()
        {
            // 通过事件通知主窗口切换到流程设计器
            _eventAggregator?.GetEvent<SwitchToCanvasDesignerEvent>()?.Publish();
        }

        private void ExecuteSwitchToStepDesigner()
        {
            // 通过事件通知主窗口切换到步骤设计器
            _eventAggregator?.GetEvent<SwitchToFlowDesignerEvent>()?.Publish();
        }

        public void Dispose()
        {
            try
            {
                _logger.LogInformation("释放DataAnalysisControlViewModel资源");

                // 只释放模型实例，不关闭全局Python环境
                _pythonModel = null;
                _isPythonInitialized = false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "释放资源失败");
            }
        }
    }

    #region 辅助类

    public class RecommendationItem
    {
        public string Text { get; set; }
    }

    public class ReactionScenario
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public string Title { get; set; }
        public string Subtitle { get; set; }
        public Dictionary<string, string> DefaultParameters { get; set; }
    }

    #endregion

}
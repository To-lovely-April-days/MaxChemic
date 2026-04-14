using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using MaxChemical.Infrastructure.Events;
using MaxChemical.Infrastructure.Services;
using MaxChemical.Logging;
using MaxChemical.Modules.Designer.Models;
using MaxChemical.Modules.Designer.Services;
using MaxChemical.Modules.Designer.Views;
using Microsoft.Win32;
using Newtonsoft.Json;
using OfficeOpenXml;
using OfficeOpenXml.ConditionalFormatting;
using OfficeOpenXml.Drawing;
using OfficeOpenXml.Drawing.Chart;
using OfficeOpenXml.Style;
using OxyPlot;
using OxyPlot.Annotations;
using OxyPlot.Axes;
using OxyPlot.Series;
using OxyPlot.Wpf;
using Prism.Commands;
using Prism.Events;
using Prism.Mvvm;
using Python.Runtime;
using Color = System.Drawing.Color;

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

        private System.Windows.Media.Brush _modelStatusBrush = System.Windows.Media.Brushes.Orange;
        public System.Windows.Media.Brush ModelStatusBrush
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
            InitializeReactionScenarios();
            InitializeCommands();
            //InitializePython(); 暂时不用
        }

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
                    DefaultParameters = null
                }
            };

            SelectedReactionScenario = ReactionScenarios.FirstOrDefault();
        }

        private void OnReactionScenarioChanged()
        {
            if (SelectedReactionScenario == null) return;

            try
            {
                _logger.LogInformation("切换反应场景: {ScenarioName}", SelectedReactionScenario.Name);

                CurrentScenarioTitle = SelectedReactionScenario.Title;
                CurrentScenarioSubtitle = SelectedReactionScenario.Subtitle;

                if (SelectedReactionScenario.DefaultParameters != null)
                {
                    ApplyDefaultParameters(SelectedReactionScenario.DefaultParameters);
                    PredictionResultsVisibility = Visibility.Collapsed;
                }
                else
                {
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
            //try
            //{
            //    _logger.LogInformation("开始初始化Python模型");

            //    if (!PythonEnvironmentManager.Instance.IsInitialized)
            //    {
            //        var success = PythonEnvironmentManager.Instance.Initialize();
            //        if (!success)
            //        {
            //            throw new Exception("Python环境初始化失败");
            //        }
            //    }

            //    _pythonModel = PythonEnvironmentManager.Instance.CreateModel();
            //    _isPythonInitialized = true;

            //    ModelStatus = "已初始化";
            //    ModelStatusBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(22, 198, 12));

            //    _logger.LogInformation("Python模型初始化成功");

            //    LoadChartsOnStartup();
            //}
            //catch (Exception ex)
            //{
            //    _logger.LogError(ex, "Python模型初始化失败");
            //    ModelStatus = "初始化失败";
            //    ModelStatusBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(231, 72, 86));

            //    _dialogService?.ShowError($"Python模型初始化失败: {ex.Message}", "初始化错误");
            //}
        }

        private void LoadChartsOnStartup()
        {
            //if (!_isPythonInitialized) return;

            //try
            //{
            //    using (Py.GIL())
            //    {
            //        dynamic windowInfo = _pythonModel.get_process_window_data();
            //        UpdateProcessWindowPlot(windowInfo);

            //        dynamic sensitivityInfo = _pythonModel.get_parameter_sensitivity();
            //        UpdateSensitivityPlot(sensitivityInfo);
            //    }

            //    _logger.LogInformation("图表加载成功");
            //}
            //catch (Exception ex)
            //{
            //    _logger.LogError(ex, "自动加载图表失败");
            //}
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
                    LabelFormatString = "{0:F2}"  // 修改这里：从 {0:F1} 改为 {0:F2}
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

        #region Excel导出功能

        static DataAnalysisControlViewModel()
        {
            // 设置 EPPlus 许可证（只执行一次）
            ExcelPackage.LicenseContext = LicenseContext.NonCommercial;
        }

        private void ExecuteExportData()
        {
            try
            {
                _logger.LogInformation("开始导出数据到Excel");

                var saveFileDialog = new SaveFileDialog
                {
                    Filter = "Excel文件 (*.xlsx)|*.xlsx",
                    FileName = $"工艺分析报告_{SelectedReactionScenario?.Name}_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx",
                    Title = "导出工艺分析报告"
                };

                if (saveFileDialog.ShowDialog() != true)
                {
                    return;
                }

                using (var package = new ExcelPackage())
                {
                    var summarySheet = package.Workbook.Worksheets.Add("分析摘要");
                    var dataSheet = package.Workbook.Worksheets.Add("详细数据");
                    var chartSheet = package.Workbook.Worksheets.Add("图表分析");

                    FillSummarySheet(summarySheet);
                    FillDataSheet(dataSheet);
                    FillChartSheet(chartSheet, package);

                    FileInfo fileInfo = new FileInfo(saveFileDialog.FileName);
                    package.SaveAs(fileInfo);
                }

                _logger.LogInformation("数据导出成功: {FilePath}", saveFileDialog.FileName);

                var result = _dialogService?.ShowConfirmation(
                    $"数据已成功导出至:\n{saveFileDialog.FileName}\n\n是否立即打开文件?",
                    "导出成功");

                if (result == true)
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = saveFileDialog.FileName,
                        UseShellExecute = true
                    });
                }

                _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish("数据导出完成");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "导出数据失败");
                _dialogService?.ShowError($"导出失败: {ex.Message}", "错误");
            }
        }

        private void FillSummarySheet(ExcelWorksheet sheet)
        {
            // 设置列宽
            sheet.Column(1).Width = 22;
            sheet.Column(2).Width = 28;
            sheet.Column(3).Width = 22;
            sheet.Column(4).Width = 28;

            int row = 1;

            // 主标题
            sheet.Cells[row, 1, row, 4].Merge = true;
            sheet.Cells[row, 1].Value = "化工工艺分析报告";
            sheet.Cells[row, 1].Style.Font.Size = 22;
            sheet.Cells[row, 1].Style.Font.Bold = true;
            sheet.Cells[row, 1].Style.HorizontalAlignment = ExcelHorizontalAlignment.Center;
            sheet.Cells[row, 1].Style.VerticalAlignment = ExcelVerticalAlignment.Center;
            sheet.Cells[row, 1].Style.Fill.PatternType = ExcelFillStyle.Solid;
            sheet.Cells[row, 1].Style.Fill.BackgroundColor.SetColor(Color.FromArgb(68, 114, 196));
            sheet.Cells[row, 1].Style.Font.Color.SetColor(Color.White);
            sheet.Row(row).Height = 40;
            row += 2;

            // 基本信息
            AddSectionHeader(sheet, ref row, "基本信息");
            AddInfoRow(sheet, ref row, "报告生成时间", DateTime.Now.ToString("yyyy年MM月dd日 HH:mm:ss"));
            AddInfoRow(sheet, ref row, "反应类型", SelectedReactionScenario?.Name ?? "未选择");
            AddInfoRow(sheet, ref row, "场景描述", SelectedReactionScenario?.Description ?? "-");
            AddInfoRow(sheet, ref row, "分析人员", Environment.UserName);
            AddInfoRow(sheet, ref row, "报告版本", "V1.0");
            row++;

            // 获取最优工艺参数区域
            if (_isPythonInitialized)
            {
                try
                {
                    using (Py.GIL())
                    {
                        dynamic windowInfo = _pythonModel.get_process_window_data();
                        string windowJson = windowInfo.ToString();
                        var windowData = JsonConvert.DeserializeObject<ProcessWindowData>(windowJson);

                        if (windowData?.OptimalRegion != null)
                        {
                            AddSectionHeader(sheet, ref row, "最优工艺参数区域");

                            // 温度范围
                            AddParameterRangeRow(sheet, ref row, "反应温度",
                                $"{windowData.OptimalRegion.TempMin:F1}",
                                $"{windowData.OptimalRegion.TempMax:F1}",
                                "°C");

                            // 压力范围
                            AddParameterRangeRow(sheet, ref row, "反应压力",
                                $"{windowData.OptimalRegion.PressureMin:F1}",
                                $"{windowData.OptimalRegion.PressureMax:F1}",
                                "bar");

                            // 预期转化率范围
                            AddParameterRangeRow(sheet, ref row, "预期转化率",
                                "85.0",
                                "100.0",
                                "%");

                            // 推荐催化剂用量
                            AddParameterRangeRow(sheet, ref row, "催化剂用量",
                                "3.5",
                                "5.0",
                                "%");

                            // 推荐停留时间
                            AddParameterRangeRow(sheet, ref row, "停留时间",
                                "3.5",
                                "5.0",
                                "min");

                            // 推荐氢气当量
                            AddParameterRangeRow(sheet, ref row, "氢气当量",
                                "1.2",
                                "1.8",
                                "eq");

                            row++;

                            // 添加说明
                            sheet.Cells[row, 1, row, 4].Merge = true;
                            sheet.Cells[row, 1].Value = "注：以上参数区域基于历史数据分析和机器学习模型预测得出，实际应用时请结合具体工艺条件进行调整。";
                            sheet.Cells[row, 1].Style.Font.Size = 9;
                            sheet.Cells[row, 1].Style.Font.Italic = true;
                            sheet.Cells[row, 1].Style.Font.Color.SetColor(Color.FromArgb(100, 100, 100));
                            sheet.Cells[row, 1].Style.WrapText = true;
                            sheet.Row(row).Height = 30;
                            row++;
                        }
                        else
                        {
                            // 如果没有最优区域数据，显示当前输入参数作为参考
                            AddSectionHeader(sheet, ref row, "当前工艺参数（参考）");
                            AddParameterRow(sheet, ref row, "反应压力", Pressure, "bar");
                            AddParameterRow(sheet, ref row, "反应温度", Temperature, "°C");
                            AddParameterRow(sheet, ref row, "氢气流量", H2Flow, "mL/min");
                            AddParameterRow(sheet, ref row, "原料流量", FeedFlow, "g/min");
                            AddParameterRow(sheet, ref row, "催化剂用量", CatAmount, "%");
                            AddParameterRow(sheet, ref row, "停留时间", ResidenceTime, "min");
                            AddParameterRow(sheet, ref row, "氢气当量", H2Equiv, "eq");
                            row++;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "获取最优工艺参数失败");

                    // 出错时显示当前参数
                    AddSectionHeader(sheet, ref row, "当前工艺参数（参考）");
                    AddParameterRow(sheet, ref row, "反应压力", Pressure, "bar");
                    AddParameterRow(sheet, ref row, "反应温度", Temperature, "°C");
                    AddParameterRow(sheet, ref row, "氢气流量", H2Flow, "mL/min");
                    AddParameterRow(sheet, ref row, "原料流量", FeedFlow, "g/min");
                    AddParameterRow(sheet, ref row, "催化剂用量", CatAmount, "%");
                    AddParameterRow(sheet, ref row, "停留时间", ResidenceTime, "min");
                    AddParameterRow(sheet, ref row, "氢气当量", H2Equiv, "eq");
                    row++;
                }
            }
            else
            {
                // Python未初始化时显示当前参数
                AddSectionHeader(sheet, ref row, "当前工艺参数（参考）");
                AddParameterRow(sheet, ref row, "反应压力", Pressure, "bar");
                AddParameterRow(sheet, ref row, "反应温度", Temperature, "°C");
                AddParameterRow(sheet, ref row, "氢气流量", H2Flow, "mL/min");
                AddParameterRow(sheet, ref row, "原料流量", FeedFlow, "g/min");
                AddParameterRow(sheet, ref row, "催化剂用量", CatAmount, "%");
                AddParameterRow(sheet, ref row, "停留时间", ResidenceTime, "min");
                AddParameterRow(sheet, ref row, "氢气当量", H2Equiv, "eq");
                row++;
            }

            // 预测结果
            if (PredictionResultsVisibility == Visibility.Visible)
            {
                AddSectionHeader(sheet, ref row, "预测结果");
                AddResultRow(sheet, ref row, "预测转化率", PredictedConversion);
                AddResultRow(sheet, ref row, "置信区间", ConfidenceRange);
                AddResultRow(sheet, ref row, "预测精度", PredictionAccuracy);
                AddResultRow(sheet, ref row, "工艺风险", ProcessRisk);
                row++;

                // 工艺建议
                if (Recommendations?.Any() == true)
                {
                    AddSectionHeader(sheet, ref row, "工艺优化建议");
                    foreach (var recommendation in Recommendations)
                    {
                        sheet.Cells[row, 1, row, 4].Merge = true;
                        var text = recommendation.Text.Replace("•", "-").Trim();
                        sheet.Cells[row, 1].Value = text;
                        sheet.Cells[row, 1].Style.WrapText = true;
                        sheet.Cells[row, 1].Style.Indent = 1;
                        sheet.Cells[row, 1].Style.VerticalAlignment = ExcelVerticalAlignment.Center;
                        sheet.Row(row).Height = 22;

                        sheet.Cells[row, 1].Style.Border.Left.Style = ExcelBorderStyle.Thin;
                        sheet.Cells[row, 1].Style.Border.Right.Style = ExcelBorderStyle.Thin;
                        sheet.Cells[row, 1].Style.Border.Bottom.Style = ExcelBorderStyle.Thin;
                        sheet.Cells[row, 1].Style.Border.Left.Color.SetColor(Color.FromArgb(217, 217, 217));
                        sheet.Cells[row, 1].Style.Border.Right.Color.SetColor(Color.FromArgb(217, 217, 217));
                        sheet.Cells[row, 1].Style.Border.Bottom.Color.SetColor(Color.FromArgb(217, 217, 217));

                        row++;
                    }
                }
            }

            // 页脚
            row++;
            sheet.Cells[row, 1, row, 4].Merge = true;
            sheet.Cells[row, 1].Value = $"© {DateTime.Now.Year} 化工工艺分析系统 - 本报告仅供内部参考使用";
            sheet.Cells[row, 1].Style.Font.Size = 9;
            sheet.Cells[row, 1].Style.Font.Italic = true;
            sheet.Cells[row, 1].Style.Font.Color.SetColor(Color.Gray);
            sheet.Cells[row, 1].Style.HorizontalAlignment = ExcelHorizontalAlignment.Center;

            // 设置整体边框
            var dataRange = sheet.Cells[1, 1, row, 4];
            dataRange.Style.Border.Top.Style = ExcelBorderStyle.Thin;
            dataRange.Style.Border.Left.Style = ExcelBorderStyle.Thin;
            dataRange.Style.Border.Right.Style = ExcelBorderStyle.Thin;
            dataRange.Style.Border.Bottom.Style = ExcelBorderStyle.Thin;
        }

        private void AddParameterRangeRow(ExcelWorksheet sheet, ref int row, string parameter, string minValue, string maxValue, string unit)
        {
            sheet.Cells[row, 1].Value = parameter;
            sheet.Cells[row, 1].Style.Font.Bold = true;
            sheet.Cells[row, 1].Style.VerticalAlignment = ExcelVerticalAlignment.Center;

            // 合并单元格显示范围
            sheet.Cells[row, 2, row, 3].Merge = true;
            sheet.Cells[row, 2].Value = $"{minValue} ~ {maxValue}";
            sheet.Cells[row, 2].Style.HorizontalAlignment = ExcelHorizontalAlignment.Center;
            sheet.Cells[row, 2].Style.VerticalAlignment = ExcelVerticalAlignment.Center;

            // 设置范围显示的背景色
            sheet.Cells[row, 2].Style.Fill.PatternType = ExcelFillStyle.Solid;
            sheet.Cells[row, 2].Style.Fill.BackgroundColor.SetColor(Color.FromArgb(226, 239, 218));
            sheet.Cells[row, 2].Style.Font.Bold = true;

            sheet.Cells[row, 4].Value = unit;
            sheet.Cells[row, 4].Style.HorizontalAlignment = ExcelHorizontalAlignment.Left;
            sheet.Cells[row, 4].Style.VerticalAlignment = ExcelVerticalAlignment.Center;
            sheet.Cells[row, 4].Style.Font.Color.SetColor(Color.FromArgb(128, 128, 128));

            sheet.Cells[row, 1, row, 4].Style.Border.Top.Style = ExcelBorderStyle.Thin;
            sheet.Cells[row, 1, row, 4].Style.Border.Bottom.Style = ExcelBorderStyle.Thin;
            sheet.Cells[row, 1, row, 4].Style.Border.Left.Style = ExcelBorderStyle.Thin;
            sheet.Cells[row, 1, row, 4].Style.Border.Right.Style = ExcelBorderStyle.Thin;
            sheet.Cells[row, 1, row, 4].Style.Border.Top.Color.SetColor(Color.FromArgb(217, 217, 217));
            sheet.Cells[row, 1, row, 4].Style.Border.Bottom.Color.SetColor(Color.FromArgb(217, 217, 217));
            sheet.Cells[row, 1, row, 4].Style.Border.Left.Color.SetColor(Color.FromArgb(217, 217, 217));
            sheet.Cells[row, 1, row, 4].Style.Border.Right.Color.SetColor(Color.FromArgb(217, 217, 217));

            sheet.Row(row).Height = 22;
            row++;
        }

        private void FillDataSheet(ExcelWorksheet sheet)
        {
            if (!_isPythonInitialized)
            {
                sheet.Cells[1, 1].Value = "Python环境未初始化，无法获取数据";
                sheet.Cells[1, 1].Style.Font.Color.SetColor(Color.Red);
                sheet.Cells[1, 1].Style.Font.Bold = true;
                return;
            }

            try
            {
                using (Py.GIL())
                {
                    dynamic windowInfo = _pythonModel.get_process_window_data();
                    string windowJson = windowInfo.ToString();
                    var windowData = JsonConvert.DeserializeObject<ProcessWindowData>(windowJson);

                    int row = 1;

                    // 标题
                    sheet.Cells[row, 1, row, 5].Merge = true;
                    sheet.Cells[row, 1].Value = "工艺操作窗口详细数据";
                    StyleMainHeader(sheet.Cells[row, 1, row, 5]);
                    row += 2;

                    // 表头
                    string[] headers = { "压力 (bar)", "温度 (°C)", "转化率 (%)", "下限 (%)", "上限 (%)" };
                    for (int i = 0; i < headers.Length; i++)
                    {
                        var cell = sheet.Cells[row, i + 1];
                        cell.Value = headers[i];
                        cell.Style.Font.Bold = true;
                        cell.Style.Font.Size = 11;
                        cell.Style.Fill.PatternType = ExcelFillStyle.Solid;
                        cell.Style.Fill.BackgroundColor.SetColor(Color.FromArgb(217, 217, 217));
                        cell.Style.HorizontalAlignment = ExcelHorizontalAlignment.Center;
                        cell.Style.VerticalAlignment = ExcelVerticalAlignment.Center;
                        cell.Style.Border.BorderAround(ExcelBorderStyle.Thin);
                        sheet.Column(i + 1).Width = 16;
                    }
                    row++;

                    int dataStartRow = row;

                    // 数据填充
                    foreach (var pressureData in windowData.PressureCurves)
                    {
                        for (int i = 0; i < pressureData.Temperatures.Count; i++)
                        {
                            sheet.Cells[row, 1].Value = pressureData.Pressure;
                            sheet.Cells[row, 2].Value = pressureData.Temperatures[i];
                            sheet.Cells[row, 3].Value = pressureData.Conversions[i];

                            double conversion = pressureData.Conversions[i];
                            sheet.Cells[row, 4].Value = Math.Max(0, conversion - 5);
                            sheet.Cells[row, 5].Value = Math.Min(100, conversion + 5);

                            for (int col = 1; col <= 5; col++)
                            {
                                sheet.Cells[row, col].Style.Numberformat.Format = "0.00";
                                sheet.Cells[row, col].Style.HorizontalAlignment = ExcelHorizontalAlignment.Center;
                                sheet.Cells[row, col].Style.VerticalAlignment = ExcelVerticalAlignment.Center;
                                sheet.Cells[row, col].Style.Border.BorderAround(ExcelBorderStyle.Thin);
                                sheet.Cells[row, col].Style.Border.BorderAround(ExcelBorderStyle.Thin, Color.FromArgb(217, 217, 217));
                            }

                            row++;
                        }
                    }

                    // 条件格式化
                    var conversionColumn = sheet.Cells[dataStartRow, 3, row - 1, 3];
                    var colorScale = conversionColumn.ConditionalFormatting.AddThreeColorScale();
                    colorScale.LowValue.Type = eExcelConditionalFormattingValueObjectType.Num;
                    colorScale.LowValue.Value = 0;
                    colorScale.LowValue.Color = Color.FromArgb(248, 105, 107);

                    colorScale.MiddleValue.Type = eExcelConditionalFormattingValueObjectType.Num;
                    colorScale.MiddleValue.Value = 70;
                    colorScale.MiddleValue.Color = Color.FromArgb(255, 235, 132);

                    colorScale.HighValue.Type = eExcelConditionalFormattingValueObjectType.Num;
                    colorScale.HighValue.Value = 100;
                    colorScale.HighValue.Color = Color.FromArgb(99, 190, 123);

                    // 添加数据说明
                    row += 2;
                    sheet.Cells[row, 1].Value = "数据说明：";
                    sheet.Cells[row, 1].Style.Font.Bold = true;
                    row++;
                    sheet.Cells[row, 1, row, 5].Merge = true;
                    sheet.Cells[row, 1].Value = "1. 转化率数据基于机器学习模型预测生成";
                    row++;
                    sheet.Cells[row, 1, row, 5].Merge = true;
                    sheet.Cells[row, 1].Value = "2. 置信区间表示预测值的可能波动范围";
                    row++;
                    sheet.Cells[row, 1, row, 5].Merge = true;
                    sheet.Cells[row, 1].Value = "3. 建议结合实际工艺条件进行参数调整";
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "填充详细数据失败");
                sheet.Cells[1, 1].Value = $"数据加载失败: {ex.Message}";
                sheet.Cells[1, 1].Style.Font.Color.SetColor(Color.Red);
            }
        }

        private void FillChartSheet(ExcelWorksheet sheet, ExcelPackage package)
        {
            int row = 1;

            // 标题
            sheet.Cells[row, 1, row, 8].Merge = true;
            sheet.Cells[row, 1].Value = "图表分析";
            StyleMainHeader(sheet.Cells[row, 1, row, 8]);
            row += 2;

            try
            {
                // 工艺操作窗口图
                if (ProcessWindowPlotModel != null)
                {
                    sheet.Cells[row, 1].Value = "1. 工艺操作窗口分析";
                    sheet.Cells[row, 1].Style.Font.Bold = true;
                    sheet.Cells[row, 1].Style.Font.Size = 14;
                    sheet.Cells[row, 1].Style.Font.Color.SetColor(Color.FromArgb(68, 114, 196));
                    row++;

                    var processImage = ExportPlotToImage(ProcessWindowPlotModel, 800, 500);
                    if (processImage != null)
                    {
                        using (var ms = new MemoryStream())
                        {
                            processImage.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                            ms.Position = 0;

                            var picture1 = sheet.Drawings.AddPicture("ProcessWindow", ms);
                            picture1.SetPosition(row - 1, 0, 0, 0);
                            picture1.SetSize(800, 500);
                        }

                        processImage.Dispose();
                        row += 26;
                    }
                }

                // 参数敏感性图
                if (SensitivityPlotModel != null)
                {
                    sheet.Cells[row, 1].Value = "2. 参数敏感性分析";
                    sheet.Cells[row, 1].Style.Font.Bold = true;
                    sheet.Cells[row, 1].Style.Font.Size = 14;
                    sheet.Cells[row, 1].Style.Font.Color.SetColor(Color.FromArgb(68, 114, 196));
                    row++;

                    var sensitivityImage = ExportPlotToImage(SensitivityPlotModel, 600, 400);
                    if (sensitivityImage != null)
                    {
                        using (var ms = new MemoryStream())
                        {
                            sensitivityImage.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                            ms.Position = 0;

                            var picture2 = sheet.Drawings.AddPicture("Sensitivity", ms);
                            picture2.SetPosition(row - 1, 0, 0, 0);
                            picture2.SetSize(600, 400);
                        }

                        sensitivityImage.Dispose();
                        row += 21;
                    }
                }

                CreateNativeExcelCharts(sheet, package, ref row);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "填充图表失败");
                sheet.Cells[row, 1].Value = $"图表生成失败: {ex.Message}";
                sheet.Cells[row, 1].Style.Font.Color.SetColor(Color.Red);
            }
        }

        private Image ExportPlotToImage(PlotModel plotModel, int width, int height)
        {
            try
            {
                var pngExporter = new PngExporter
                {
                    Width = width,
                    Height = height
                };

                using (var stream = new MemoryStream())
                {
                    pngExporter.Export(plotModel, stream);
                    stream.Position = 0;
                    return Image.FromStream(stream);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "导出图表为图片失败");
                return null;
            }
        }

        private void CreateNativeExcelCharts(ExcelWorksheet sheet, ExcelPackage package, ref int row)
        {
            if (!_isPythonInitialized) return;

            try
            {
                using (Py.GIL())
                {
                    dynamic windowInfo = _pythonModel.get_process_window_data();
                    string windowJson = windowInfo.ToString();
                    var windowData = JsonConvert.DeserializeObject<ProcessWindowData>(windowJson);

                    row += 2;
                    sheet.Cells[row, 1].Value = "3. Excel原生图表 - 温度与转化率关系";
                    sheet.Cells[row, 1].Style.Font.Bold = true;
                    sheet.Cells[row, 1].Style.Font.Size = 14;
                    sheet.Cells[row, 1].Style.Font.Color.SetColor(Color.FromArgb(68, 114, 196));
                    row += 2;

                    int dataStartRow = row;
                    int col = 1;

                    // 表头
                    sheet.Cells[row, col].Value = "温度 (°C)";
                    sheet.Cells[row, col].Style.Font.Bold = true;
                    sheet.Cells[row, col].Style.Fill.PatternType = ExcelFillStyle.Solid;
                    sheet.Cells[row, col].Style.Fill.BackgroundColor.SetColor(Color.FromArgb(217, 217, 217));
                    sheet.Cells[row, col].Style.HorizontalAlignment = ExcelHorizontalAlignment.Center;

                    var firstCurve = windowData.PressureCurves.FirstOrDefault();
                    if (firstCurve != null)
                    {
                        // 温度列
                        for (int i = 0; i < firstCurve.Temperatures.Count; i++)
                        {
                            sheet.Cells[row + 1 + i, col].Value = firstCurve.Temperatures[i];
                            sheet.Cells[row + 1 + i, col].Style.Numberformat.Format = "0.0";
                            sheet.Cells[row + 1 + i, col].Style.HorizontalAlignment = ExcelHorizontalAlignment.Center;
                        }
                        col++;

                        // 各压力下的转化率
                        foreach (var pressureData in windowData.PressureCurves)
                        {
                            sheet.Cells[row, col].Value = $"{pressureData.Pressure} bar";
                            sheet.Cells[row, col].Style.Font.Bold = true;
                            sheet.Cells[row, col].Style.Fill.PatternType = ExcelFillStyle.Solid;
                            sheet.Cells[row, col].Style.Fill.BackgroundColor.SetColor(Color.FromArgb(217, 217, 217));
                            sheet.Cells[row, col].Style.HorizontalAlignment = ExcelHorizontalAlignment.Center;

                            for (int i = 0; i < pressureData.Conversions.Count; i++)
                            {
                                sheet.Cells[row + 1 + i, col].Value = pressureData.Conversions[i];
                                sheet.Cells[row + 1 + i, col].Style.Numberformat.Format = "0.00";
                                sheet.Cells[row + 1 + i, col].Style.HorizontalAlignment = ExcelHorizontalAlignment.Center;
                            }
                            col++;
                        }

                        // 创建图表
                        var chart = sheet.Drawings.AddChart("ConversionLineChart", eChartType.Line) as ExcelLineChart;
                        chart.Title.Text = "不同压力下的温度-转化率关系曲线";
                        chart.Title.Font.Bold = true;
                        chart.Title.Font.Size = 14;

                        chart.SetPosition(row + firstCurve.Temperatures.Count + 2, 0, 0, 0);
                        chart.SetSize(800, 450);

                        chart.Style = eChartStyle.Style2;
                        chart.Legend.Position = eLegendPosition.Right;
                        chart.XAxis.Title.Text = "温度 (°C)";
                        chart.XAxis.Title.Font.Size = 11;
                        chart.YAxis.Title.Text = "转化率 (%)";
                        chart.YAxis.Title.Font.Size = 11;
                        chart.YAxis.MinValue = 0;
                        chart.YAxis.MaxValue = 100;

                        chart.XAxis.MajorGridlines.Width = 1;
                        chart.YAxis.MajorGridlines.Width = 1;

                        // 添加系列
                        for (int i = 2; i <= col - 1; i++)
                        {
                            var series = chart.Series.Add(
                                sheet.Cells[row + 1, i, row + firstCurve.Temperatures.Count, i],
                                sheet.Cells[row + 1, 1, row + firstCurve.Temperatures.Count, 1]
                            );
                            series.Header = sheet.Cells[row, i].Value.ToString();
                        }

                        row += firstCurve.Temperatures.Count + 28;

                        CreateSensitivityBarChart(sheet, ref row);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "创建Excel原生图表失败");
            }
        }

        private void CreateSensitivityBarChart(ExcelWorksheet sheet, ref int row)
        {
            try
            {
                using (Py.GIL())
                {
                    dynamic sensitivityInfo = _pythonModel.get_parameter_sensitivity();
                    string sensitivityJson = sensitivityInfo.ToString();
                    var sensitivityData = JsonConvert.DeserializeObject<SensitivityAnalysisData>(sensitivityJson);

                    row += 2;
                    sheet.Cells[row, 1].Value = "4. Excel原生图表 - 参数敏感性对比";
                    sheet.Cells[row, 1].Style.Font.Bold = true;
                    sheet.Cells[row, 1].Style.Font.Size = 14;
                    sheet.Cells[row, 1].Style.Font.Color.SetColor(Color.FromArgb(68, 114, 196));
                    row++;

                    int dataStartRow = row;
                    sheet.Cells[row, 1].Value = "参数名称";
                    sheet.Cells[row, 2].Value = "敏感性系数";
                    sheet.Cells[row, 1, row, 2].Style.Font.Bold = true;
                    sheet.Cells[row, 1, row, 2].Style.Fill.PatternType = ExcelFillStyle.Solid;
                    sheet.Cells[row, 1, row, 2].Style.Fill.BackgroundColor.SetColor(Color.FromArgb(217, 217, 217));
                    sheet.Cells[row, 1, row, 2].Style.HorizontalAlignment = ExcelHorizontalAlignment.Center;
                    row++;

                    var topSensitivities = sensitivityData.Sensitivities.Take(6).ToList();
                    foreach (var item in topSensitivities)
                    {
                        sheet.Cells[row, 1].Value = item.ParameterChinese;
                        sheet.Cells[row, 2].Value = item.Sensitivity;
                        sheet.Cells[row, 2].Style.Numberformat.Format = "0.00";
                        sheet.Cells[row, 1].Style.HorizontalAlignment = ExcelHorizontalAlignment.Left;
                        sheet.Cells[row, 2].Style.HorizontalAlignment = ExcelHorizontalAlignment.Center;
                        row++;
                    }

                    var barChart = sheet.Drawings.AddChart("SensitivityBarChart", eChartType.BarClustered) as ExcelBarChart;
                    barChart.Title.Text = "参数敏感性分析对比";
                    barChart.Title.Font.Bold = true;
                    barChart.Title.Font.Size = 14;

                    barChart.SetPosition(dataStartRow + topSensitivities.Count + 1, 0, 0, 0);
                    barChart.SetSize(700, 400);
                    barChart.Style = eChartStyle.Style3;

                    var barSeries = barChart.Series.Add(
                        sheet.Cells[dataStartRow + 1, 2, dataStartRow + topSensitivities.Count, 2],
                        sheet.Cells[dataStartRow + 1, 1, dataStartRow + topSensitivities.Count, 1]
                    );
                    barSeries.Header = "敏感性系数";

                    barChart.XAxis.Title.Text = "敏感性系数";
                    barChart.YAxis.Title.Text = "工艺参数";
                    barChart.Legend.Remove();

                    barChart.DataLabel.ShowValue = true;

                    row += 22;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "创建敏感性柱状图失败");
            }
        }

        #region Excel样式辅助方法

        private void AddSectionHeader(ExcelWorksheet sheet, ref int row, string title)
        {
            sheet.Cells[row, 1, row, 4].Merge = true;
            var cell = sheet.Cells[row, 1];
            cell.Value = title;
            cell.Style.Font.Size = 12;
            cell.Style.Font.Bold = true;
            cell.Style.Fill.PatternType = ExcelFillStyle.Solid;
            cell.Style.Fill.BackgroundColor.SetColor(Color.FromArgb(217, 225, 242));
            cell.Style.HorizontalAlignment = ExcelHorizontalAlignment.Left;
            cell.Style.VerticalAlignment = ExcelVerticalAlignment.Center;
            cell.Style.Indent = 1;
            cell.Style.Border.Top.Style = ExcelBorderStyle.Thin;
            cell.Style.Border.Bottom.Style = ExcelBorderStyle.Thin;
            cell.Style.Border.Left.Style = ExcelBorderStyle.Thin;
            cell.Style.Border.Right.Style = ExcelBorderStyle.Thin;
            cell.Style.Border.Top.Color.SetColor(Color.FromArgb(68, 114, 196));
            cell.Style.Border.Bottom.Color.SetColor(Color.FromArgb(68, 114, 196));
            cell.Style.Border.Left.Color.SetColor(Color.FromArgb(68, 114, 196));
            cell.Style.Border.Right.Color.SetColor(Color.FromArgb(68, 114, 196));
            sheet.Row(row).Height = 26;
            row++;
        }

        private void AddInfoRow(ExcelWorksheet sheet, ref int row, string label, string value)
        {
            sheet.Cells[row, 1].Value = label;
            sheet.Cells[row, 1].Style.Font.Bold = true;
            sheet.Cells[row, 1].Style.HorizontalAlignment = ExcelHorizontalAlignment.Left;
            sheet.Cells[row, 1].Style.VerticalAlignment = ExcelVerticalAlignment.Center;

            sheet.Cells[row, 2, row, 4].Merge = true;
            sheet.Cells[row, 2].Value = value;
            sheet.Cells[row, 2].Style.HorizontalAlignment = ExcelHorizontalAlignment.Left;
            sheet.Cells[row, 2].Style.VerticalAlignment = ExcelVerticalAlignment.Center;

            sheet.Cells[row, 1, row, 4].Style.Border.Top.Style = ExcelBorderStyle.Thin;
            sheet.Cells[row, 1, row, 4].Style.Border.Bottom.Style = ExcelBorderStyle.Thin;
            sheet.Cells[row, 1, row, 4].Style.Border.Left.Style = ExcelBorderStyle.Thin;
            sheet.Cells[row, 1, row, 4].Style.Border.Right.Style = ExcelBorderStyle.Thin;
            sheet.Cells[row, 1, row, 4].Style.Border.Top.Color.SetColor(Color.FromArgb(217, 217, 217));
            sheet.Cells[row, 1, row, 4].Style.Border.Bottom.Color.SetColor(Color.FromArgb(217, 217, 217));
            sheet.Cells[row, 1, row, 4].Style.Border.Left.Color.SetColor(Color.FromArgb(217, 217, 217));
            sheet.Cells[row, 1, row, 4].Style.Border.Right.Color.SetColor(Color.FromArgb(217, 217, 217));

            sheet.Row(row).Height = 20;
            row++;
        }

        private void AddParameterRow(ExcelWorksheet sheet, ref int row, string parameter, string value, string unit)
        {
            sheet.Cells[row, 1].Value = parameter;
            sheet.Cells[row, 1].Style.Font.Bold = true;
            sheet.Cells[row, 1].Style.VerticalAlignment = ExcelVerticalAlignment.Center;

            sheet.Cells[row, 2].Value = value;
            sheet.Cells[row, 2].Style.HorizontalAlignment = ExcelHorizontalAlignment.Center;
            sheet.Cells[row, 2].Style.VerticalAlignment = ExcelVerticalAlignment.Center;

            sheet.Cells[row, 3].Value = unit;
            sheet.Cells[row, 3].Style.HorizontalAlignment = ExcelHorizontalAlignment.Left;
            sheet.Cells[row, 3].Style.VerticalAlignment = ExcelVerticalAlignment.Center;
            sheet.Cells[row, 3].Style.Font.Color.SetColor(Color.FromArgb(128, 128, 128));

            sheet.Cells[row, 1, row, 4].Style.Border.Top.Style = ExcelBorderStyle.Thin;
            sheet.Cells[row, 1, row, 4].Style.Border.Bottom.Style = ExcelBorderStyle.Thin;
            sheet.Cells[row, 1, row, 4].Style.Border.Left.Style = ExcelBorderStyle.Thin;
            sheet.Cells[row, 1, row, 4].Style.Border.Right.Style = ExcelBorderStyle.Thin;
            sheet.Cells[row, 1, row, 4].Style.Border.Top.Color.SetColor(Color.FromArgb(217, 217, 217));
            sheet.Cells[row, 1, row, 4].Style.Border.Bottom.Color.SetColor(Color.FromArgb(217, 217, 217));
            sheet.Cells[row, 1, row, 4].Style.Border.Left.Color.SetColor(Color.FromArgb(217, 217, 217));
            sheet.Cells[row, 1, row, 4].Style.Border.Right.Color.SetColor(Color.FromArgb(217, 217, 217));

            sheet.Row(row).Height = 20;
            row++;
        }

        private void AddResultRow(ExcelWorksheet sheet, ref int row, string label, string value)
        {
            sheet.Cells[row, 1].Value = label;
            sheet.Cells[row, 1].Style.Font.Bold = true;
            sheet.Cells[row, 1].Style.VerticalAlignment = ExcelVerticalAlignment.Center;

            sheet.Cells[row, 2, row, 4].Merge = true;
            sheet.Cells[row, 2].Value = value;
            sheet.Cells[row, 2].Style.Font.Bold = true;
            sheet.Cells[row, 2].Style.HorizontalAlignment = ExcelHorizontalAlignment.Center;
            sheet.Cells[row, 2].Style.VerticalAlignment = ExcelVerticalAlignment.Center;

            // 根据结果类型设置颜色
            if (label.Contains("转化率"))
            {
                if (double.TryParse(value.Replace("%", ""), out double conv))
                {
                    if (conv >= 85)
                    {
                        sheet.Cells[row, 2].Style.Fill.PatternType = ExcelFillStyle.Solid;
                        sheet.Cells[row, 2].Style.Fill.BackgroundColor.SetColor(Color.FromArgb(198, 239, 206));
                        sheet.Cells[row, 2].Style.Font.Color.SetColor(Color.FromArgb(0, 97, 0));
                    }
                    else if (conv >= 70)
                    {
                        sheet.Cells[row, 2].Style.Fill.PatternType = ExcelFillStyle.Solid;
                        sheet.Cells[row, 2].Style.Fill.BackgroundColor.SetColor(Color.FromArgb(255, 235, 156));
                        sheet.Cells[row, 2].Style.Font.Color.SetColor(Color.FromArgb(156, 101, 0));
                    }
                    else
                    {
                        sheet.Cells[row, 2].Style.Fill.PatternType = ExcelFillStyle.Solid;
                        sheet.Cells[row, 2].Style.Fill.BackgroundColor.SetColor(Color.FromArgb(255, 199, 206));
                        sheet.Cells[row, 2].Style.Font.Color.SetColor(Color.FromArgb(156, 0, 6));
                    }
                }
            }
            else if (label.Contains("风险"))
            {
                if (value.Contains("低"))
                    sheet.Cells[row, 2].Style.Font.Color.SetColor(Color.FromArgb(0, 176, 80));
                else if (value.Contains("中"))
                    sheet.Cells[row, 2].Style.Font.Color.SetColor(Color.FromArgb(255, 192, 0));
                else if (value.Contains("高"))
                    sheet.Cells[row, 2].Style.Font.Color.SetColor(Color.FromArgb(255, 0, 0));
            }

            sheet.Cells[row, 1, row, 4].Style.Border.Top.Style = ExcelBorderStyle.Thin;
            sheet.Cells[row, 1, row, 4].Style.Border.Bottom.Style = ExcelBorderStyle.Thin;
            sheet.Cells[row, 1, row, 4].Style.Border.Left.Style = ExcelBorderStyle.Thin;
            sheet.Cells[row, 1, row, 4].Style.Border.Right.Style = ExcelBorderStyle.Thin;
            sheet.Cells[row, 1, row, 4].Style.Border.Top.Color.SetColor(Color.FromArgb(217, 217, 217));
            sheet.Cells[row, 1, row, 4].Style.Border.Bottom.Color.SetColor(Color.FromArgb(217, 217, 217));
            sheet.Cells[row, 1, row, 4].Style.Border.Left.Color.SetColor(Color.FromArgb(217, 217, 217));
            sheet.Cells[row, 1, row, 4].Style.Border.Right.Color.SetColor(Color.FromArgb(217, 217, 217));

            sheet.Row(row).Height = 24;
            row++;
        }

        private void StyleMainHeader(ExcelRange range)
        {
            range.Style.Font.Size = 16;
            range.Style.Font.Bold = true;
            range.Style.HorizontalAlignment = ExcelHorizontalAlignment.Center;
            range.Style.VerticalAlignment = ExcelVerticalAlignment.Center;
            range.Style.Fill.PatternType = ExcelFillStyle.Solid;
            range.Style.Fill.BackgroundColor.SetColor(Color.FromArgb(68, 114, 196));
            range.Style.Font.Color.SetColor(Color.White);
            range.Worksheet.Row(range.Start.Row).Height = 30;
        }

        #endregion

        #endregion

        private void ExecuteSwitchToFlowDesigner()
        {
            _eventAggregator?.GetEvent<SwitchToCanvasDesignerEvent>()?.Publish();
        }

        private void ExecuteSwitchToStepDesigner()
        {
            _eventAggregator?.GetEvent<SwitchToFlowDesignerEvent>()?.Publish();
        }

        public void Dispose()
        {
            try
            {
                _logger.LogInformation("释放DataAnalysisControlViewModel资源");
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
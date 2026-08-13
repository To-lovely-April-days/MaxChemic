using MaxChemical.Data.Models;
using MaxChemical.Data.Repositories;
using MaxChemical.Data.Services;
using MaxChemical.Logging;
using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Series;
using Prism.Commands;
using Prism.Mvvm;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using MaxChemical.Modules.Designer.Models;
using MaxChemical.Infrastructure.Services;

namespace MaxChemical.Modules.Designer.ViewModels
{
    /// <summary>
    /// 图表图例项
    /// </summary>
    public class ChartLegendItem
    {
        public string Name { get; set; } = "";
        public SolidColorBrush ColorBrush { get; set; } = Brushes.Gray;
    }

    /// <summary>
    /// 可切换的参数项
    /// </summary>
    public class ParameterToggleItem : BindableBase
    {
        private bool _isVisible = true;
        public string Name { get; set; } = "";
        public string DeviceName { get; set; } = "";
        public string ParameterName { get; set; } = "";
        public OxyColor LineColor { get; set; } = OxyColors.Blue;

        public bool IsVisible
        {
            get => _isVisible;
            set => SetProperty(ref _isVisible, value);
        }
    }

    public class ExperimentDetailWindowViewModel : BindableBase
    {
        private readonly IExperimentDataRepository _repository;
        private readonly IExcelExportService _excelExportService;
        private readonly ILogService _logger;
        private readonly ILocalizationService _localization;

        private ExperimentRecord? _experiment;
        private ObservableCollection<ParameterChangeRecord> _parameterChanges = new();
        private PlotModel _chartModel = new();
        private ObservableCollection<ChartLegendItem> _chartLegendItems = new();
        private ObservableCollection<ParameterToggleItem> _availableParameters = new();
        private bool _isLoading;
        private string _statusMessage;
        private int _nodeCount;

        // 预定义颜色（靛蓝主题色系）
        private static readonly OxyColor[] LineColors = new[]
        {
            OxyColor.Parse("#2D3A8C"),
            OxyColor.Parse("#D97706"),
            OxyColor.Parse("#059669"),
            OxyColor.Parse("#DC2626"),
            OxyColor.Parse("#7C3AED"),
            OxyColor.Parse("#2563EB"),
            OxyColor.Parse("#DB2777"),
            OxyColor.Parse("#65A30D"),
        };

        private static readonly LineStyle[] LineStyleArray = new[]
        {
            LineStyle.Solid,
            LineStyle.Dash,
            LineStyle.Dot,
            LineStyle.DashDot,
            LineStyle.LongDash,
            LineStyle.LongDashDot,
        };

        public ExperimentDetailWindowViewModel(IExperimentDataRepository repository,
            IExcelExportService excelExportService, ILogService logger, ILocalizationService localization)
        {
            _repository = repository ?? throw new ArgumentNullException(nameof(repository));
            _excelExportService = excelExportService ?? throw new ArgumentNullException(nameof(excelExportService));
            _logger = logger?.ForContext<ExperimentDetailWindowViewModel>() ?? throw new ArgumentNullException(nameof(logger));
            _localization = localization ?? throw new ArgumentNullException(nameof(localization));

            InitializeCommands();
            InitializeChart();
            _statusMessage = _localization.GetString("Experiment_Detail_StateMsg_Loading", "正在加载...");
            InitialLocalizationTxt();
        }

        #region 本地化属性

        public string ElapsedTxt { get; set; }
        public string ParamTrendTxt { get; set; }
        public string NodeRecordTxt { get; set; }
        public string NodeNameTxt { get; set; }
        public string DevTxt { get; set; }
        public string CommandTxt { get; set; }
        public string StartTxt { get; set; }
        public string StateTxt { get; set; }
        public string ErrMsgTxt { get; set; }
        public string ParamTxt { get; set; }
        public string ValueTxt { get; set; }
        public string PreValueTxt { get; set; }
        public string UnitTxt { get; set; }
        public string TimeTxt { get; set; }
        public string NodeTxt { get; set; }
        public string NumberTxt { get; set; }
        public string TypeTxt { get; set; }
        public string ExportTxt { get; set; }
        public string CloseTxt { get; set; }
        public string ParamChangedTxt { get; set; }

        private void InitialLocalizationTxt()
        {
            ElapsedTxt = _localization.GetString("Experiment_Detail_Elapsed", "耗时");
            ParamTrendTxt = _localization.GetString("Experiment_Detail_ParamTrend", "参数趋势图");
            NodeRecordTxt = _localization.GetString("Experiment_Detail_NodeRecord", "节点执行记录");
            NodeNameTxt = _localization.GetString("Experiment_Detail_NodeName", "节点名称");
            DevTxt = _localization.GetString("Experiment_Detail_Device", "设备");
            CommandTxt = _localization.GetString("Experiment_Detail_Command", "命令");
            StartTxt = _localization.GetString("Experiment_Detail_Start", "开始");
            StateTxt = _localization.GetString("Experiment_Detail_Status", "状态");
            ErrMsgTxt = _localization.GetString("Experiment_Detail_ErrMsg", "错误信息");
            ParamTxt = _localization.GetString("Experiment_Detail_Param", "参数");
            ValueTxt = _localization.GetString("Experiment_Detail_Value", "值");
            PreValueTxt = _localization.GetString("Experiment_Detail_PreValue", "前值");
            UnitTxt = _localization.GetString("Experiment_Detail_Unit", "单位");
            TimeTxt = _localization.GetString("Experiment_Detail_Time", "时间");
            NodeTxt = _localization.GetString("Experiment_Detail_Node", "节点");
            NumberTxt = _localization.GetString("Experiment_Detail_Number", "序号");
            TypeTxt = _localization.GetString("Experiment_Detail_Type", "类型");
            ExportTxt = _localization.GetString("Experiment_Detail_Export", "导出详情");
            CloseTxt = _localization.GetString("Experiment_Detail_Close", "关闭");
            ParamChangedTxt = _localization.GetString("Experiment_Detial_ParamRecord", "参数变化记录");
        }

        #endregion

        #region Properties

        public ExperimentRecord? Experiment
        {
            get => _experiment;
            set => SetProperty(ref _experiment, value);
        }

        public ObservableCollection<ParameterChangeRecord> ParameterChanges
        {
            get => _parameterChanges;
            set => SetProperty(ref _parameterChanges, value);
        }

        public PlotModel ChartModel
        {
            get => _chartModel;
            set => SetProperty(ref _chartModel, value);
        }

        public ObservableCollection<ChartLegendItem> ChartLegendItems
        {
            get => _chartLegendItems;
            set => SetProperty(ref _chartLegendItems, value);
        }

        public ObservableCollection<ParameterToggleItem> AvailableParameters
        {
            get => _availableParameters;
            set => SetProperty(ref _availableParameters, value);
        }

        public bool IsLoading
        {
            get => _isLoading;
            set => SetProperty(ref _isLoading, value);
        }

        public string StatusMessage
        {
            get => _statusMessage;
            set => SetProperty(ref _statusMessage, value);
        }

        public int NodeCount
        {
            get => _nodeCount;
            set => SetProperty(ref _nodeCount, value);
        }

        #endregion

        #region Commands

        public DelegateCommand ExportDetailCommand { get; private set; } = null!;
        public DelegateCommand<ParameterToggleItem> ToggleParameterCommand { get; private set; } = null!;

        #endregion

        private void InitializeCommands()
        {
            ExportDetailCommand = new DelegateCommand(async () => await ExportDetailAsync());
            ToggleParameterCommand = new DelegateCommand<ParameterToggleItem>(OnToggleParameter);
        }

        private void InitializeChart()
        {
            ChartModel = new PlotModel
            {
                PlotAreaBorderColor = OxyColor.Parse("#E8EAED"),
                PlotAreaBorderThickness = new OxyThickness(0.5),
                Padding = new OxyThickness(8, 8, 16, 8),
                // 不再固定左侧 margin，让多轴自动分配
                PlotMargins = new OxyThickness(double.NaN, 4, 8, 24),
            };

            // X 轴（时间）— 保持不变
            ChartModel.Axes.Add(new DateTimeAxis
            {
                Position = AxisPosition.Bottom,
                StringFormat = "HH:mm:ss",
                FontSize = 9,
                TextColor = OxyColor.Parse("#9CA3AF"),
                TicklineColor = OxyColor.Parse("#E8EAED"),
                MajorGridlineStyle = LineStyle.Solid,
                MajorGridlineColor = OxyColor.Parse("#F2F3F5"),
                MinorGridlineStyle = LineStyle.None,
                AxislineStyle = LineStyle.None,
                IntervalType = DateTimeIntervalType.Auto,
                Key = "TimeAxis",
            });

            // 不再预创建 Y 轴，由 ApplyChartData 根据数据动态创建
        }
        /// <summary>
        /// 由外部调用，加载实验数据。loading 遮罩由调用方管理。
        /// </summary>
        public async Task LoadDataAsync(string experimentId, LoadingOverlayWindow? loading = null)
        {
            if (string.IsNullOrEmpty(experimentId))
                throw new ArgumentException("实验ID不能为空", nameof(experimentId));

            try
            {
                IsLoading = true;
                StatusMessage = _localization.GetString("Experiment_Detail_StateMsg_LoadDetail", "正在加载实验详情...");

                InitializeChart();

                _logger.LogInformation("正在查询实验: {Id}", experimentId);

                var experiment = await _repository.GetExperimentAsync(experimentId);

                _logger.LogInformation("查询结果: experiment={IsNull}", experiment == null ? "null" : "有数据");

                if (experiment == null)
                {
                    StatusMessage = _localization.GetString("Experiment_Detail_StateMsg_NofoundRecord", "未找到实验记录");
                    return; // Experiment 保持 null，调用方会检查
                }

                Experiment = experiment;
                NodeCount = experiment.NodeRecords?.Count ?? 0;

                loading?.UpdateMessage(_localization.GetString("Experiment_Detail_LoadParamChange", "正在加载参数变化记录..."));
                _logger.LogInformation("正在加载参数变化记录...");

                var parameterChanges = await _repository.GetParameterChangesForExportAsync(experimentId);

                _logger.LogInformation("参数变化记录: {Count} 条", parameterChanges?.Count ?? 0);

                loading?.UpdateMessage(_localization.GetString("Experiment_Detail_CreateTrend", "正在构建趋势图..."));

                var chartData = await Task.Run(() => PrepareChartData(parameterChanges));

                ParameterChanges = new ObservableCollection<ParameterChangeRecord>(parameterChanges);
                ApplyChartData(chartData);

                StatusMessage = string.Format(
                    _localization.GetString("Experiment_Detail_LoadNode", "加载完成 — 节点 {0} · 参数变化 {1}"),
                    NodeCount, ParameterChanges.Count);
                _logger.LogInformation("加载实验详情成功: {ExperimentId}", experimentId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "加载实验详情失败: {ExperimentId}", experimentId);
                StatusMessage = _localization.GetString("Experiment_Detail_LoadFailed", "加载失败");
                throw;
            }
            finally
            {
                IsLoading = false;
            }
        }
        /// <summary>
        /// 后台线程：纯数据计算，不碰任何 UI 对象
        /// </summary>
        private ChartBuildResult PrepareChartData(IList<ParameterChangeRecord> changes)
        {
            var groups = changes
                .GroupBy(c => $"{c.DeviceName}|{c.ParameterName}")
                .Select(g => new
                {
                    Key = g.Key,
                    First = g.First(),
                    Points = g.OrderBy(r => r.Timestamp)
                              .Where(r => double.TryParse(r.ParameterValue, out _))
                              .ToList()
                })
                .Where(g => g.Points.Count >= 2)
                .ToList();

            var result = new ChartBuildResult();
            int colorIndex = 0;

            foreach (var group in groups)
            {
                var first = group.First;
                var color = LineColors[colorIndex % LineColors.Length];
                var lineStyle = LineStyleArray[colorIndex % LineStyleArray.Length];
                var displayName = string.IsNullOrEmpty(first.Unit)
                    ? first.ParameterName
                    : $"{first.ParameterName} ({first.Unit})";

                var dataPoints = new List<DataPoint>();
                foreach (var record in group.Points)
                {
                    double.TryParse(record.ParameterValue, out double val);
                    dataPoints.Add(new DataPoint(
                        DateTimeAxis.ToDouble(record.Timestamp), val));
                }

                result.SeriesInfos.Add(new SeriesInfo
                {
                    DisplayName = displayName,
                    Color = color,
                    LineStyle = lineStyle,
                    DataPoints = dataPoints,
                    DeviceName = first.DeviceName,
                    ParameterName = first.ParameterName,
                });

                colorIndex++;
            }

            return result;
        }

        /// <summary>
        /// UI 线程：用预算好的数据一次性构建图表
        /// </summary>
        private void ApplyChartData(ChartBuildResult data)
        {
            AvailableParameters.Clear();
            ChartLegendItems.Clear();
            ChartModel.Series.Clear();

            // 清除旧的 Y 轴（保留 X 轴）
            var timeAxis = ChartModel.Axes.FirstOrDefault(a => a.Key == "TimeAxis");
            ChartModel.Axes.Clear();
            if (timeAxis != null)
                ChartModel.Axes.Add(timeAxis);

            // 分析每组数据的值域，决定是否需要独立 Y 轴
            var axisGroups = GroupByValueRange(data.SeriesInfos);

            int axisIndex = 0;
            foreach (var axisGroup in axisGroups)
            {
                // 为这组参数创建 Y 轴
                var axisKey = $"Y{axisIndex}";
                var axisColor = axisGroup.Count == 1
                    ? axisGroup[0].Color
                    : OxyColor.Parse("#9CA3AF"); // 多参数共享轴用灰色

                var yAxis = new LinearAxis
                {
                    Key = axisKey,
                    Position = axisIndex == 0 ? AxisPosition.Left : AxisPosition.Right,
                    FontSize = 9,
                    TextColor = axisColor,
                    TicklineColor = axisColor,
                    AxislineStyle = LineStyle.Solid,
                    AxislineColor = axisColor,
                    AxislineThickness = 1,
                    // 只有第一个轴显示网格线
                    MajorGridlineStyle = axisIndex == 0 ? LineStyle.Solid : LineStyle.None,
                    MajorGridlineColor = OxyColor.Parse("#F2F3F5"),
                    MinorGridlineStyle = LineStyle.None,
                    StringFormat = "0.##",
                    // 多个右轴时错开位置
                    PositionTier = axisIndex <= 1 ? 0 : axisIndex - 1,
                    // 显示单位标题
                    Title = axisGroup.Count == 1 ? axisGroup[0].DisplayName : "",
                    TitleFontSize = 8,
                    TitleColor = axisColor,
                };

                ChartModel.Axes.Add(yAxis);

                // 为该轴组下的每条线创建 Series
                foreach (var info in axisGroup)
                {
                    var series = new LineSeries
                    {
                        Title = info.DisplayName,
                        Color = info.Color,
                        StrokeThickness = 1.8,
                        LineStyle = info.LineStyle,
                        MarkerType = info.DataPoints.Count > 200 ? MarkerType.None : MarkerType.Circle,
                        MarkerSize = 2.5,
                        MarkerFill = info.Color,
                        YAxisKey = axisKey,
                        XAxisKey = "TimeAxis",
                        CanTrackerInterpolatePoints = true,
                        // 格式：{0}=系列名, {2}=X值(时间), {4}=Y值
                        TrackerFormatString = "  {0}  \n  时间  {2:HH:mm:ss}  \n  数值  {4:0.##}  ",
                    };

                    series.Points.AddRange(info.DataPoints);
                    ChartModel.Series.Add(series);

                    AvailableParameters.Add(new ParameterToggleItem
                    {
                        Name = info.DisplayName,
                        DeviceName = info.DeviceName,
                        ParameterName = info.ParameterName,
                        LineColor = info.Color,
                        IsVisible = true,
                    });

                    var wpfColor = System.Windows.Media.Color.FromRgb(
                        info.Color.R, info.Color.G, info.Color.B);
                    ChartLegendItems.Add(new ChartLegendItem
                    {
                        Name = info.DisplayName,
                        ColorBrush = new SolidColorBrush(wpfColor),
                    });
                }

                axisIndex++;
            }

            ChartModel.InvalidatePlot(true);
        }
        /// <summary>
        /// 根据值域范围将参数分组，值域接近的共用一个 Y 轴，差异大的独立 Y 轴
        /// 避免出现大值参数把小值参数压成一条直线的问题
        /// </summary>
        private static List<List<SeriesInfo>> GroupByValueRange(List<SeriesInfo> seriesInfos)
        {
            if (seriesInfos.Count == 0)
                return new List<List<SeriesInfo>>();

            // 计算每条线的值域
            var withRange = seriesInfos.Select(s =>
            {
                var values = s.DataPoints.Select(p => p.Y).ToList();
                var min = values.Count > 0 ? values.Min() : 0;
                var max = values.Count > 0 ? values.Max() : 0;
                return new { Info = s, Min = min, Max = max, Range = max - min };
            })
            .OrderByDescending(x => x.Range)
            .ToList();

            var groups = new List<List<SeriesInfo>>();
            var assigned = new HashSet<int>();

            for (int i = 0; i < withRange.Count; i++)
            {
                if (assigned.Contains(i)) continue;

                var group = new List<SeriesInfo> { withRange[i].Info };
                assigned.Add(i);

                var refMin = withRange[i].Min;
                var refMax = withRange[i].Max;
                var refRange = withRange[i].Range;

                // 尝试把值域接近的参数归到同一组
                for (int j = i + 1; j < withRange.Count; j++)
                {
                    if (assigned.Contains(j)) continue;

                    var otherMin = withRange[j].Min;
                    var otherMax = withRange[j].Max;
                    var otherRange = withRange[j].Range;

                    // 判断是否可以共用轴：
                    // 1. 值域范围比例不超过 5 倍
                    // 2. 最大值/最小值的量级差不超过 10 倍
                    var maxRange = Math.Max(refRange, otherRange);
                    var minRange = Math.Min(refRange, otherRange);
                    var rangeRatio = minRange > 0 ? maxRange / minRange : double.MaxValue;

                    var overallMax = Math.Max(Math.Abs(refMax), Math.Abs(otherMax));
                    var overallMin = Math.Min(Math.Abs(refMax), Math.Abs(otherMax));
                    var magnitudeRatio = overallMin > 0 ? overallMax / overallMin : double.MaxValue;

                    if (rangeRatio <= 5 && magnitudeRatio <= 10)
                    {
                        group.Add(withRange[j].Info);
                        assigned.Add(j);
                        // 更新参考范围
                        refMin = Math.Min(refMin, otherMin);
                        refMax = Math.Max(refMax, otherMax);
                        refRange = refMax - refMin;
                    }
                }

                groups.Add(group);
            }

            return groups;
        }
        #region 辅助数据结构

        private class ChartBuildResult
        {
            public List<SeriesInfo> SeriesInfos { get; } = new();
        }

        private class SeriesInfo
        {
            public string DisplayName { get; set; } = "";
            public string DeviceName { get; set; } = "";
            public string ParameterName { get; set; } = "";
            public OxyColor Color { get; set; }
            public LineStyle LineStyle { get; set; }
            public List<DataPoint> DataPoints { get; set; } = new();
        }

        #endregion

        private void OnToggleParameter(ParameterToggleItem? item)
        {
            if (item == null) return;

            foreach (var series in ChartModel.Series)
            {
                if (series is LineSeries line && line.Title == item.Name)
                {
                    line.IsVisible = item.IsVisible;
                    break;
                }
            }

            ChartLegendItems.Clear();
            foreach (var param in AvailableParameters.Where(p => p.IsVisible))
            {
                var wpfColor = System.Windows.Media.Color.FromRgb(param.LineColor.R, param.LineColor.G, param.LineColor.B);
                ChartLegendItems.Add(new ChartLegendItem
                {
                    Name = param.Name,
                    ColorBrush = new SolidColorBrush(wpfColor),
                });
            }

            ChartModel.InvalidatePlot(true);
        }

        private async Task ExportDetailAsync()
        {
            if (Experiment == null) return;

            try
            {
                var saveDialog = new SaveFileDialog
                {
                    Filter = "Excel文件 (*.xlsx)|*.xlsx",
                    FileName = $"实验详情_{Experiment.ExperimentName}_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx"
                };

                if (saveDialog.ShowDialog() == true)
                {
                    IsLoading = true;
                    StatusMessage = _localization.GetString("Experiment_Detail_Exporting", "正在导出实验详情...");

                    await _excelExportService.ExportExperimentWithParameterChangesAsync(
                        Experiment.ExperimentId, saveDialog.FileName);

                    StatusMessage = _localization.GetString("Experiment_Detail_ExportComplate", "导出完成");
                    MessageBox.Show($"导出完成！\n文件位置: {saveDialog.FileName}",
                        "导出成功", MessageBoxButton.OK, MessageBoxImage.Information);

                    System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{saveDialog.FileName}\"");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "导出实验详情失败");
                StatusMessage = _localization.GetString("Experiment_Detail_ExportFailed", "导出失败");
                MessageBox.Show($"导出失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsLoading = false;
            }
        }
    }
}
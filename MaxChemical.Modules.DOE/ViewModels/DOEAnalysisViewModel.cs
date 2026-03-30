using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using MaxChemical.Infrastructure.Services;
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
    /// DOE 分析视图 ViewModel — 管理所有图表的数据加载和 OxyPlot PlotModel 渲染
    /// </summary>
    public class DOEAnalysisViewModel : BindableBase
    {
        private readonly IDOEAnalysisService _analysisService;
        private readonly IDOERepository _repository;
        private readonly ILogService _logger;

        private string _batchId = "";
        private bool _isLoading;
        private string _statusMessage = "";

        // ── 图表 PlotModels ──
        private PlotModel? _mainEffectsPlot;
        private PlotModel? _paretoPlot;
        private PlotModel? _actualVsPredictedPlot;
        private PlotModel? _residualPlot;
        private BitmapImage? _surfaceImage;

        // ── 文本输出 ──
        private string _anovaTableText = "";
        private string _regressionEquation = "";
        private string _rSquaredText = "";
        private string _modelEvolutionText = "";

        // ── 曲面图因子选择 ──
        private List<string> _factorNames = new();
        private string? _surfaceFactor1;
        private string? _surfaceFactor2;

        public DOEAnalysisViewModel(IDOEAnalysisService analysisService, IDOERepository repository, ILogService logger)
        {
            _analysisService = analysisService;
            _repository = repository;
            _logger = logger?.ForContext<DOEAnalysisViewModel>() ?? throw new ArgumentNullException(nameof(logger));

            RefreshAllCommand = new DelegateCommand(async () => await LoadAllChartsAsync(), () => !string.IsNullOrEmpty(BatchId));
            GenerateSurfaceCommand = new DelegateCommand(async () => await LoadSurfaceAsync(),
                () => !string.IsNullOrEmpty(SurfaceFactor1) && !string.IsNullOrEmpty(SurfaceFactor2) && SurfaceFactor1 != SurfaceFactor2);
        }

        // ══════════════ Properties ══════════════

        public string BatchId { get => _batchId; set { SetProperty(ref _batchId, value); RefreshAllCommand.RaiseCanExecuteChanged(); } }
        public bool IsLoading { get => _isLoading; set => SetProperty(ref _isLoading, value); }
        public string StatusMessage { get => _statusMessage; set => SetProperty(ref _statusMessage, value); }

        public PlotModel? MainEffectsPlot { get => _mainEffectsPlot; set => SetProperty(ref _mainEffectsPlot, value); }
        public PlotModel? ParetoPlot { get => _paretoPlot; set => SetProperty(ref _paretoPlot, value); }
        public PlotModel? ActualVsPredictedPlot { get => _actualVsPredictedPlot; set => SetProperty(ref _actualVsPredictedPlot, value); }
        public PlotModel? ResidualPlot { get => _residualPlot; set => SetProperty(ref _residualPlot, value); }
        public BitmapImage? SurfaceImage { get => _surfaceImage; set => SetProperty(ref _surfaceImage, value); }

        public string AnovaTableText { get => _anovaTableText; set => SetProperty(ref _anovaTableText, value); }
        public string RegressionEquation { get => _regressionEquation; set => SetProperty(ref _regressionEquation, value); }
        public string RSquaredText { get => _rSquaredText; set => SetProperty(ref _rSquaredText, value); }
        public string ModelEvolutionText { get => _modelEvolutionText; set => SetProperty(ref _modelEvolutionText, value); }

        public List<string> FactorNames { get => _factorNames; set => SetProperty(ref _factorNames, value); }
        public string? SurfaceFactor1 { get => _surfaceFactor1; set { SetProperty(ref _surfaceFactor1, value); GenerateSurfaceCommand.RaiseCanExecuteChanged(); } }
        public string? SurfaceFactor2 { get => _surfaceFactor2; set { SetProperty(ref _surfaceFactor2, value); GenerateSurfaceCommand.RaiseCanExecuteChanged(); } }

        public DelegateCommand RefreshAllCommand { get; }
        public DelegateCommand GenerateSurfaceCommand { get; }

        // ══════════════ 加载批次 ══════════════

        public async Task LoadBatchAsync(string batchId)
        {
            BatchId = batchId;

            var batch = await _repository.GetBatchWithDetailsAsync(batchId);
            if (batch != null)
            {
                FactorNames = batch.Factors.Select(f => f.FactorName).ToList();
                if (FactorNames.Count >= 2)
                {
                    SurfaceFactor1 = FactorNames[0];
                    SurfaceFactor2 = FactorNames[1];
                }
            }

            await LoadAllChartsAsync();
        }

        // ══════════════ 加载所有图表 ══════════════

        private async Task LoadAllChartsAsync()
        {
            if (string.IsNullOrEmpty(BatchId)) return;

            try
            {
                IsLoading = true;
                StatusMessage = "正在加载分析图表...";

                await Task.WhenAll(
                    LoadMainEffectsAsync(),
                    LoadParetoAsync(),
                    LoadActualVsPredictedAsync(),
                    LoadResidualAsync(),
                    LoadAnovaAsync(),
                    LoadRegressionAsync(),
                    LoadSurfaceAsync()
                );

                StatusMessage = "图表加载完成";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "加载分析图表失败");
                StatusMessage = $"加载失败: {ex.Message}";
            }
            finally
            {
                IsLoading = false;
            }
        }

        // ══════════════ 各图表加载方法 ══════════════

        private async Task LoadMainEffectsAsync()
        {
            try
            {
                var json = await _analysisService.GetMainEffectsAsync(BatchId);
                var data = JsonConvert.DeserializeObject<Dictionary<string, List<LevelMean>>>(json);
                if (data == null || data.Count == 0) return;

                var model = new PlotModel { Title = "主效应图" };
                model.Axes.Add(new LinearAxis { Position = AxisPosition.Bottom, Title = "因子水平" });
                model.Axes.Add(new LinearAxis { Position = AxisPosition.Left, Title = "响应均值" });

                var colors = new[] { OxyColors.Blue, OxyColors.Red, OxyColors.Green, OxyColors.Orange, OxyColors.Purple };
                int ci = 0;

                foreach (var kv in data)
                {
                    var series = new LineSeries { Title = kv.Key, Color = colors[ci % colors.Length], MarkerType = MarkerType.Circle, MarkerSize = 5 };
                    foreach (var pt in kv.Value)
                        series.Points.Add(new DataPoint(pt.Level, pt.Mean));
                    model.Series.Add(series);
                    ci++;
                }

                MainEffectsPlot = model;
            }
            catch (Exception ex) { _logger.LogError(ex, "主效应图加载失败"); }
        }

        private async Task LoadParetoAsync()
        {
            try
            {
                var json = await _analysisService.GetParetoChartAsync(BatchId);
                var data = JsonConvert.DeserializeObject<List<ParetoItem>>(json);
                if (data == null || data.Count == 0) return;

                // 按效应大小降序排列
                var sorted = data.OrderByDescending(d => d.Effect).ToList();

                var model = new PlotModel
                {
                    Title = "Pareto 效应图",
                    PlotMargins = new OxyThickness(10, 10, 40, 40)
                };

                // X 轴 = 因子名（底部）
                var categoryAxis = new CategoryAxis
                {
                    Position = AxisPosition.Bottom,
                    GapWidth = 0.3
                };
                foreach (var item in sorted)
                    categoryAxis.Labels.Add(item.Term);

                // Y 轴 = 效应值（左侧）
                var valueAxis = new LinearAxis
                {
                    Position = AxisPosition.Left,
                    Title = "效应大小",
                    Minimum = 0,
                    AbsoluteMinimum = 0
                };

                model.Axes.Add(categoryAxis);
                model.Axes.Add(valueAxis);

                // 用 RectangleBarSeries 画柱子
                var series = new RectangleBarSeries
                {
                    StrokeThickness = 0.5,
                    StrokeColor = OxyColors.Gray
                };

                for (int i = 0; i < sorted.Count; i++)
                {
                    var item = sorted[i];
                    var color = item.Significant ? OxyColors.SteelBlue : OxyColors.LightGray;
                    series.Items.Add(new RectangleBarItem
                    {
                        X0 = i - 0.35,
                        X1 = i + 0.35,
                        Y0 = 0,
                        Y1 = item.Effect,
                        Color = color
                    });
                }

                model.Series.Add(series);
                ParetoPlot = model;
            }
            catch (Exception ex) { _logger.LogError(ex, "Pareto 图加载失败"); }
        }

        private async Task LoadActualVsPredictedAsync()
        {
            try
            {
                var json = await _analysisService.GetActualVsPredictedAsync(BatchId);
                var data = JsonConvert.DeserializeObject<ActualVsPredicted>(json);
                if (data?.Actual == null) return;

                var model = new PlotModel { Title = $"实际值 vs 预测值 (R² = {data.RSquared:F4})" };
                model.Axes.Add(new LinearAxis { Position = AxisPosition.Bottom, Title = "实际值" });
                model.Axes.Add(new LinearAxis { Position = AxisPosition.Left, Title = "预测值" });

                // 散点
                var scatter = new ScatterSeries { MarkerType = MarkerType.Circle, MarkerSize = 5, MarkerFill = OxyColors.SteelBlue };
                for (int i = 0; i < data.Actual.Count; i++)
                    scatter.Points.Add(new ScatterPoint(data.Actual[i], data.Predicted[i]));
                model.Series.Add(scatter);

                // 对角线
                double min = Math.Min(data.Actual.Min(), data.Predicted.Min());
                double max = Math.Max(data.Actual.Max(), data.Predicted.Max());
                var diag = new LineSeries { Color = OxyColors.Red, LineStyle = LineStyle.Dash };
                diag.Points.Add(new DataPoint(min, min));
                diag.Points.Add(new DataPoint(max, max));
                model.Series.Add(diag);

                ActualVsPredictedPlot = model;
            }
            catch (Exception ex) { _logger.LogError(ex, "实际vs预测图加载失败"); }
        }

        private async Task LoadResidualAsync()
        {
            try
            {
                var json = await _analysisService.GetResidualAnalysisAsync(BatchId);
                var data = JsonConvert.DeserializeObject<ResidualData>(json);
                if (data?.Fitted == null) return;

                var model = new PlotModel { Title = "残差 vs 拟合值" };
                model.Axes.Add(new LinearAxis { Position = AxisPosition.Bottom, Title = "拟合值" });
                model.Axes.Add(new LinearAxis { Position = AxisPosition.Left, Title = "残差" });

                var scatter = new ScatterSeries { MarkerType = MarkerType.Circle, MarkerSize = 4, MarkerFill = OxyColors.DarkOrange };
                for (int i = 0; i < data.Fitted.Count; i++)
                    scatter.Points.Add(new ScatterPoint(data.Fitted[i], data.Residuals[i]));
                model.Series.Add(scatter);

                // 零线
                double min = data.Fitted.Min(), max = data.Fitted.Max();
                var zero = new LineSeries { Color = OxyColors.Gray, LineStyle = LineStyle.Dash };
                zero.Points.Add(new DataPoint(min, 0));
                zero.Points.Add(new DataPoint(max, 0));
                model.Series.Add(zero);

                ResidualPlot = model;
            }
            catch (Exception ex) { _logger.LogError(ex, "残差图加载失败"); }
        }

        private async Task LoadAnovaAsync()
        {
            try
            {
                var json = await _analysisService.GetAnovaTableAsync(BatchId);
                var data = JsonConvert.DeserializeObject<List<AnovaRow>>(json);
                if (data == null) return;

                var lines = new List<string> { "来源\t\tDF\tSS\t\tMS\t\tF\tP" };
                lines.Add(new string('-', 70));
                foreach (var row in data)
                    lines.Add($"{row.Source}\t\t{row.DF}\t{row.SS:F2}\t\t{row.MS:F2}\t\t{row.FValue?.ToString("F2") ?? "-"}\t{row.PValue?.ToString("F4") ?? "-"}");

                AnovaTableText = string.Join("\n", lines);
            }
            catch (Exception ex) { _logger.LogError(ex, "ANOVA 表加载失败"); }
        }

        private async Task LoadRegressionAsync()
        {
            try
            {
                var json = await _analysisService.GetRegressionSummaryAsync(BatchId);
                var data = JsonConvert.DeserializeObject<RegressionSummary>(json);
                if (data == null) return;

                RegressionEquation = data.Equation ?? "";
                RSquaredText = $"R² = {data.RSquared:F4}, Adj-R² = {data.AdjRSquared:F4}";
            }
            catch (Exception ex) { _logger.LogError(ex, "回归摘要加载失败"); }
        }

        private async Task LoadSurfaceAsync()
        {
            if (string.IsNullOrEmpty(SurfaceFactor1) || string.IsNullOrEmpty(SurfaceFactor2) || SurfaceFactor1 == SurfaceFactor2)
                return;

            try
            {
                var imageBytes = await _analysisService.GetResponseSurfaceImageAsync(BatchId, SurfaceFactor1, SurfaceFactor2);
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
            catch (Exception ex) { _logger.LogError(ex, "响应曲面图加载失败"); }
        }

        // ── JSON 映射 DTO ──

        private class LevelMean
        {
            [JsonProperty("level")] public double Level { get; set; }
            [JsonProperty("mean")] public double Mean { get; set; }
        }

        private class ParetoItem
        {
            [JsonProperty("term")] public string Term { get; set; } = "";
            [JsonProperty("effect")] public double Effect { get; set; }
            [JsonProperty("significant")] public bool Significant { get; set; }
        }

        private class ActualVsPredicted
        {
            [JsonProperty("actual")] public List<double> Actual { get; set; } = new();
            [JsonProperty("predicted")] public List<double> Predicted { get; set; } = new();
            [JsonProperty("r_squared")] public double RSquared { get; set; }
        }

        private class ResidualData
        {
            [JsonProperty("fitted")] public List<double> Fitted { get; set; } = new();
            [JsonProperty("residuals")] public List<double> Residuals { get; set; } = new();
        }

        private class AnovaRow
        {
            [JsonProperty("source")] public string Source { get; set; } = "";
            [JsonProperty("df")] public int DF { get; set; }
            [JsonProperty("ss")] public double SS { get; set; }
            [JsonProperty("ms")] public double MS { get; set; }
            [JsonProperty("f_value")] public double? FValue { get; set; }
            [JsonProperty("p_value")] public double? PValue { get; set; }
        }

        private class RegressionSummary
        {
            [JsonProperty("r_squared")] public double RSquared { get; set; }
            [JsonProperty("adj_r_squared")] public double AdjRSquared { get; set; }
            [JsonProperty("equation")] public string? Equation { get; set; }
        }
    }
}

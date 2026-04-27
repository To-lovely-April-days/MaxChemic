// MaxChemical.Modules.Designer/ViewModels/ExperimentStatisticsViewModel.cs
using MaxChemical.Data.Models;
using MaxChemical.Data.Repositories;
using MaxChemical.Data.Services;
using MaxChemical.Infrastructure.Services;
using MaxChemical.Logging;
using Prism.Commands;
using Prism.Ioc;
using Prism.Mvvm;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace MaxChemical.Modules.Designer.ViewModels
{
    public class ExperimentStatisticsViewModel : BindableBase
    {
        private readonly IExperimentDataRepository _repository;
        private readonly IExcelExportService _excelExportService;
        private readonly ILogService _logger;
        private readonly ILocalizationService _localizationService;

        private DateTime _startDate;
        private DateTime _endDate;
        private string _selectedInterval = "day";
        private bool _isLoading;
        private string _statusMessage = "就绪";

        private ExperimentStatistics _statistics = new();
        private ObservableCollection<ExperimentTrendData> _trendData = new();
        private ObservableCollection<DeviceUsageStatistics> _deviceStats = new();

        public ExperimentStatisticsViewModel(IExperimentDataRepository repository,
            IExcelExportService excelExportService, ILogService logger)
        {
            _repository = repository ?? throw new ArgumentNullException(nameof(repository));
            _excelExportService = excelExportService ?? throw new ArgumentNullException(nameof(excelExportService));
            _logger = logger?.ForContext<ExperimentStatisticsViewModel>() ?? throw new ArgumentNullException(nameof(logger));
            _localizationService = ContainerLocator.Container.Resolve<ILocalizationService>() ?? throw new ArgumentNullException("localizationService");

            InitializeCommands();
            InitializeData();
            _ = LoadStatisticsAsync();
            // 新增：本地化标签
            UpdateLacalizationText();
        }

        #region Properties

        public DateTime StartDate
        {
            get => _startDate;
            set => SetProperty(ref _startDate, value);
        }

        public DateTime EndDate
        {
            get => _endDate;
            set => SetProperty(ref _endDate, value);
        }

        public string SelectedInterval
        {
            get => _selectedInterval;
            set => SetProperty(ref _selectedInterval, value);
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

        public ExperimentStatistics Statistics
        {
            get => _statistics;
            set => SetProperty(ref _statistics, value);
        }

        public ObservableCollection<ExperimentTrendData> TrendData
        {
            get => _trendData;
            set => SetProperty(ref _trendData, value);
        }

        public ObservableCollection<DeviceUsageStatistics> DeviceStats
        {
            get => _deviceStats;
            set => SetProperty(ref _deviceStats, value);
        }

        public ObservableCollection<string> Intervals { get; } = new() { "day", "week", "month" };

        #endregion

        #region Commands

        public DelegateCommand RefreshCommand { get; private set; } = null!;
        public DelegateCommand ExportCommand { get; private set; } = null!;
        public DelegateCommand Last7DaysCommand { get; private set; } = null!;
        public DelegateCommand Last30DaysCommand { get; private set; } = null!;
        public DelegateCommand Last90DaysCommand { get; private set; } = null!;

        #endregion

        private void InitializeCommands()
        {
            RefreshCommand = new DelegateCommand(async () => await LoadStatisticsAsync());
            ExportCommand = new DelegateCommand(async () => await ExportStatisticsAsync());
            Last7DaysCommand = new DelegateCommand(() => SetDateRange(7));
            Last30DaysCommand = new DelegateCommand(() => SetDateRange(30));
            Last90DaysCommand = new DelegateCommand(() => SetDateRange(90));
        }

        private void InitializeData()
        {
            EndDate = DateTime.Now.Date.AddDays(1);
            StartDate = DateTime.Now.Date.AddDays(-30);
        }

        private void SetDateRange(int days)
        {
            EndDate = DateTime.Now.Date.AddDays(1);
            StartDate = DateTime.Now.Date.AddDays(-days);
            _ = LoadStatisticsAsync();
        }

        private async Task LoadStatisticsAsync()
        {
            try
            {
                IsLoading = true;
                StatusMessage = _localizationService.GetString("Statistic_StatusMsg_Loading", "正在加载统计数据...");

                // 加载基础统计
                var statistics = await _repository.GetStatisticsAsync(StartDate, EndDate);
                Statistics = statistics;

                // 加载趋势数据
                var trendData = await _repository.GetTrendDataAsync(StartDate, EndDate, SelectedInterval);
                TrendData.Clear();
                foreach (var data in trendData)
                {
                    TrendData.Add(data);
                }

                // 加载设备使用统计
                var deviceStats = await _repository.GetDeviceUsageStatisticsAsync(StartDate, EndDate);
                DeviceStats.Clear();
                foreach (var stats in deviceStats)
                {
                    DeviceStats.Add(stats);
                }

                StatusMessage = $"{_localizationService.GetString("Statistic_StatusMsg_Completenumber", "统计完成 - 总实验数: ")}{Statistics.TotalExperiments}";

                _logger.LogInformation("加载统计数据成功");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "加载统计数据失败");
                StatusMessage = _localizationService.GetString("Statistic_StatusMsg_LoadFail", "加载统计数据失败");
                MessageBox.Show($"加载统计数据失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsLoading = false;
            }
        }

        private async Task ExportStatisticsAsync()
        {
            try
            {
                IsLoading = true;
                StatusMessage = _localizationService.GetString("Statistic_StatusMsg_Exprot", "正在导出统计数据...");

                var fileName = $"实验统计分析_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx";
                var filePath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), fileName);

                await _excelExportService.ExportStatisticsAsync(Statistics, TrendData.ToList(), DeviceStats.ToList(), filePath);

                StatusMessage = $"{_localizationService.GetString("Statistic_StatusMsg_ExpComplete", "导出完成: ")}{filePath}";
                MessageBox.Show($"导出完成！\n文件位置: {filePath}", "导出成功", MessageBoxButton.OK, MessageBoxImage.Information);

                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{filePath}\"");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "导出统计数据失败");
                StatusMessage = _localizationService.GetString("Statistic_StatusMsg_ExportFail", "导出失败");
                MessageBox.Show($"导出失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsLoading = false;
            }
        }

        #region 本地化

        public string ViewTitle { get; set; }
        public string RangeLabel { get; set; }
        public string ToLabel { get; set; }
        public string DurationLabel { get; set; }
        public string RefreshLabel { get; set; }
        public string LastSevenLabel { get; set; }
        public string LastThirtyLabel { get; set; }
        public string LastNinetyLabel { get; set; }
        public string ExportReportLabel { get; set; }
        public string OverviewLabel { get; set; }
        public string AnalysisLabel { get; set; }
        public string StatisticalLabel { get; set; }
        public string TotalLabel { get; set; }
        public string SuccessRateLabel { get; set; }
        public string AvgDurationLabel { get; set; }
        public string SimulationTotalLabel { get; set; }
        public string DetailsGroupLabel { get; set; }
        public string ExecStatistic { get; set; }
        public string SuccessTotal { get; set; }
        public string FailTotal { get; set; }
        public string ActualTotal { get; set; }
        public string TimeStatistic { get; set; }
        public string LongDuration { get; set; }
        public string SortDuration { get; set; }
        public string TimeRange { get; set; }
        public string TimeHeader { get; set; }
        public string ExprimentTotalHeader { get; set; }
        public string SuccessTotalHeader { get; set; }
        public string FailureTotalHeader { get; set; }
        public string RateHeader { get; set; }
        public string SimulationTotalHeader { get; set; }
        public string ActaulTotalHeader { get; set; }
        public string AvgDurationHeader { get; set; }
        public string IdentityHeader { get; set; }
        public string Nameheader { get; set; }
        public string UseTotalHeader { get; set; }
        public string SuccessNumberHeader { get; set; }
        public string FailureNumberHeader { get; set; }
        public string AvgExecTimeHeader { get; set; }


        private void UpdateLacalizationText()
        {
            ViewTitle = _localizationService.GetString("Statistic_Title", "实验统计分析");
            RangeLabel = _localizationService.GetString("Statistic_TimeRange", "时间范围");
            ToLabel = _localizationService.GetString("Statistic_To", "至");
            DurationLabel = _localizationService.GetString("Statistic_Interval", "时间间隔");
            RefreshLabel = _localizationService.GetString("Statistic_Refresh", "刷新");
            LastSevenLabel = _localizationService.GetString("Statistic_Last7Day", "最近7天");
            LastThirtyLabel = _localizationService.GetString("Statistic_Last30Day", "最近30天");
            LastNinetyLabel = _localizationService.GetString("Statistic_Last90Day", "最近90天");
            ExportReportLabel = _localizationService.GetString("Statistic_ExportReport", "导出统计报告");
            OverviewLabel = _localizationService.GetString("Statistic_Overview", "统计概览");
            AnalysisLabel = _localizationService.GetString("Statistic_Analysis", "趋势分析");
            StatisticalLabel = _localizationService.GetString("Statistic_UseStatistic", "设备使用统计");
            TotalLabel = _localizationService.GetString("Statistic_Total", "总实验数");
            SuccessRateLabel = _localizationService.GetString("Statistic_SuccessRate", "成功率");
            AvgDurationLabel = _localizationService.GetString("Statistic_AvgDuration", "平均持续时间");
            SimulationTotalLabel = _localizationService.GetString("Statistic_SimulationTotal", "仿真实验数");
            DetailsGroupLabel = _localizationService.GetString("Statistic_DetailsLabel", "详细统计");
            ExecStatistic = _localizationService.GetString("Statistic_Detail_Execute", "执行统计");
            SuccessTotal = _localizationService.GetString("Statistic_Detail_Success", "成功实验数：");
            FailTotal = _localizationService.GetString("Statistic_Detail_Failure", "失败实验数：");
            ActualTotal = _localizationService.GetString("Statistic_Detail_Actual", "实际实验数：");
            TimeStatistic = _localizationService.GetString("Statistic_Detail_Time", "时间统计：");
            LongDuration = _localizationService.GetString("Statistic_Detail_Long", "最长持续时间：");
            SortDuration = _localizationService.GetString("Statistic_Detail_Sort", "最短持续时间：");
            TimeRange = _localizationService.GetString("Statistic_Detail_Range", "统计时间范围：");
            TimeHeader = _localizationService.GetString("Statistic_Header_Time", "时间");
            ExprimentTotalHeader = _localizationService.GetString("Statistic_Header_Total", "总实验数");
            SuccessTotalHeader = _localizationService.GetString("Statistic_Header_Success", "成功数");
            FailureTotalHeader = _localizationService.GetString("Statistic_Header_Failure", "失败数");
            RateHeader = _localizationService.GetString("Statistic_Header_SuccessRate", "成功率(%)");
            SimulationTotalHeader = _localizationService.GetString("Statistic_Header_SimulationNumber", "仿真数");
            ActaulTotalHeader = _localizationService.GetString("Statistic_Header_ActualNumber", "实际数");
            AvgDurationHeader = _localizationService.GetString("Statistic_Header_AvgDuration", "平均持续时间");
            IdentityHeader = _localizationService.GetString("Statistic_Header_Identity", "设备ID");
            Nameheader = _localizationService.GetString("Statistic_Header_Name", "设备名称");
            UseTotalHeader = _localizationService.GetString("Statistic_Header_UseNumber", "使用次数");
            SuccessNumberHeader = _localizationService.GetString("Statistic_Header_Success", "成功数");
            FailureNumberHeader = _localizationService.GetString("Statistic_Header_Failure", "失败数");
            AvgExecTimeHeader = _localizationService.GetString("Statistic_Header_AvgExecTime", "平均执行时间");
        }

        #endregion
    }
}
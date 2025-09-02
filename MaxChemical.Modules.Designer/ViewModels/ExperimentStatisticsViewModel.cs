// MaxChemical.Modules.Designer/ViewModels/ExperimentStatisticsViewModel.cs
using MaxChemical.Data.Models;
using MaxChemical.Data.Repositories;
using MaxChemical.Data.Services;
using MaxChemical.Logging;
using Prism.Commands;
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

            InitializeCommands();
            InitializeData();
            _ = LoadStatisticsAsync();
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
                StatusMessage = "正在加载统计数据...";

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

                StatusMessage = $"统计完成 - 总实验数: {Statistics.TotalExperiments}";

                _logger.LogInformation("加载统计数据成功");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "加载统计数据失败");
                StatusMessage = "加载统计数据失败";
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
                StatusMessage = "正在导出统计数据...";

                var fileName = $"实验统计分析_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx";
                var filePath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), fileName);

                await _excelExportService.ExportStatisticsAsync(Statistics, TrendData.ToList(), DeviceStats.ToList(), filePath);

                StatusMessage = $"导出完成: {filePath}";
                MessageBox.Show($"导出完成！\n文件位置: {filePath}", "导出成功", MessageBoxButton.OK, MessageBoxImage.Information);

                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{filePath}\"");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "导出统计数据失败");
                StatusMessage = "导出失败";
                MessageBox.Show($"导出失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsLoading = false;
            }
        }
    }
}
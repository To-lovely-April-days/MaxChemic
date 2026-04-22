// MaxChemical.Modules.Designer/ViewModels/ExperimentHistoryViewModel.cs
using MaxChemical.Data.Models;
using MaxChemical.Data.Repositories;
using MaxChemical.Data.Services;
using MaxChemical.Infrastructure.Events;
using MaxChemical.Infrastructure.Services;
using MaxChemical.Logging;
using MaxChemical.Modules.Designer.Models;
using Microsoft.Win32;
using Org.BouncyCastle.Asn1.Mozilla;
using Prism.Commands;
using Prism.Events;
using Prism.Ioc;
using Prism.Mvvm;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Prism.Ioc;

namespace MaxChemical.Modules.Designer.ViewModels
{
    public class ExperimentHistoryViewModel : BindableBase
    {
        private readonly IExperimentDataRepository _repository;
        private readonly IExcelExportService _excelExportService;
        private readonly ILogService _logger;
        private readonly ILocalizationService _localizationService;

        // 筛选条件
        private DateTime? _startDate;
        private DateTime? _endDate;
        private bool? _isSuccessful;
        private bool? _isSimulation;
        private string _searchText = string.Empty;

        // 分页
        private int _currentPage = 1;
        private int _pageSize = 20;
        private int _totalRecords;
        private int _totalPages;

        // 数据
        private ObservableCollection<ExperimentRecord> _experiments = new();
        private ExperimentRecord? _selectedExperiment;

        // 状态
        private bool _isLoading;
        private string _statusMessage = "就绪";

        public ExperimentHistoryViewModel(IExperimentDataRepository repository,
            IExcelExportService excelExportService, ILogService logger)
        {
            _repository = repository ?? throw new ArgumentNullException(nameof(repository));
            _excelExportService = excelExportService ?? throw new ArgumentNullException(nameof(excelExportService));
            _logger = logger?.ForContext<ExperimentHistoryViewModel>() ?? throw new ArgumentNullException(nameof(logger));
            _localizationService = ContainerLocator.Container.Resolve<ILocalizationService>() ?? throw new ArgumentNullException("localizationService");
            

            InitializeCommands();
            InitializeFilters();
            _ = LoadDataAsync();
            UpdateLocalizationText();
        }

        #region Properties

        public DateTime? StartDate
        {
            get => _startDate;
            set => SetProperty(ref _startDate, value);
        }

        public DateTime? EndDate
        {
            get => _endDate;
            set => SetProperty(ref _endDate, value);
        }

        public bool? IsSuccessful
        {
            get => _isSuccessful;
            set => SetProperty(ref _isSuccessful, value);
        }

        public bool? IsSimulation
        {
            get => _isSimulation;
            set => SetProperty(ref _isSimulation, value);
        }

        public string SearchText
        {
            get => _searchText;
            set => SetProperty(ref _searchText, value);
        }

        public int CurrentPage
        {
            get => _currentPage;
            set => SetProperty(ref _currentPage, value);
        }

        public int PageSize
        {
            get => _pageSize;
            set => SetProperty(ref _pageSize, value);
        }

        public int TotalRecords
        {
            get => _totalRecords;
            set => SetProperty(ref _totalRecords, value);
        }

        public int TotalPages
        {
            get => _totalPages;
            set => SetProperty(ref _totalPages, value);
        }

        public ObservableCollection<ExperimentRecord> Experiments
        {
            get => _experiments;
            set => SetProperty(ref _experiments, value);
        }

        public ExperimentRecord? SelectedExperiment
        {
            get => _selectedExperiment;
            set => SetProperty(ref _selectedExperiment, value);
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

        public ObservableCollection<int> PageSizes { get; } = new() { 10, 20, 50, 100 };

        #endregion

        #region Commands

        public DelegateCommand SearchCommand { get; private set; } = null!;
        public DelegateCommand ResetFiltersCommand { get; private set; } = null!;
        public DelegateCommand FirstPageCommand { get; private set; } = null!;
        public DelegateCommand PreviousPageCommand { get; private set; } = null!;
        public DelegateCommand NextPageCommand { get; private set; } = null!;
        public DelegateCommand LastPageCommand { get; private set; } = null!;
        public DelegateCommand RefreshCommand { get; private set; } = null!;
        public DelegateCommand ExportAllCommand { get; private set; } = null!;
        public DelegateCommand ExportSelectedCommand { get; private set; } = null!;
        public DelegateCommand ExportParameterChangesCommand { get; private set; } = null!;
        public DelegateCommand ViewDetailsCommand { get; private set; } = null!;
        public DelegateCommand DeleteSelectedCommand { get; private set; } = null!;

        #endregion

        private void InitializeCommands()
        {
            SearchCommand = new DelegateCommand(async () => await SearchAsync());
            ResetFiltersCommand = new DelegateCommand(ResetFilters);
            FirstPageCommand = new DelegateCommand(async () => await GoToPageAsync(1), () => CurrentPage > 1);
            PreviousPageCommand = new DelegateCommand(async () => await GoToPageAsync(CurrentPage - 1), () => CurrentPage > 1);
            NextPageCommand = new DelegateCommand(async () => await GoToPageAsync(CurrentPage + 1), () => CurrentPage < TotalPages);
            LastPageCommand = new DelegateCommand(async () => await GoToPageAsync(TotalPages), () => CurrentPage < TotalPages);
            RefreshCommand = new DelegateCommand(async () => await LoadDataAsync());
            ExportAllCommand = new DelegateCommand(async () => await ExportAllAsync());
            ExportSelectedCommand = new DelegateCommand(async () => await ExportSelectedAsync(), () => SelectedExperiment != null);
            ExportParameterChangesCommand = new DelegateCommand(async () => await ExportParameterChangesAsync());
            ViewDetailsCommand = new DelegateCommand(ViewDetails, () => SelectedExperiment != null);
            DeleteSelectedCommand = new DelegateCommand(async () => await DeleteSelectedAsync(), () => SelectedExperiment != null);
        }

        private void InitializeFilters()
        {
            // 默认显示最近30天的数据
            EndDate = DateTime.Now.Date.AddDays(1);
            StartDate = DateTime.Now.Date.AddDays(-30);
        }

        private async Task LoadDataAsync()
        {
            try
            {
                IsLoading = true;
                StatusMessage = _localizationService.GetString("ExperimentHistory_StatusMsg_Loading", "正在加载数据...");

                var (records, totalCount) = await _repository.GetExperimentsPagedAsync(
                    StartDate, EndDate, IsSuccessful, IsSimulation, SearchText,
                    CurrentPage - 1, PageSize);

                Experiments.Clear();
                foreach (var record in records)
                {
                    Experiments.Add(record);
                }

                TotalRecords = totalCount;
                TotalPages = (int)Math.Ceiling((double)totalCount / PageSize);

                StatusMessage = string.Format(_localizationService.GetString("ExperimentHistory_StatusMsg_FindTotal"), TotalRecords);

                // 更新分页命令状态
                UpdatePagingCommands();

                _logger.LogInformation("加载实验历史数据成功: {Count} 条记录", records.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "加载实验历史数据失败");
                StatusMessage = _localizationService.GetString("ExperimentHistory_StatusMsg_LoadFail", "加载数据失败");
                MessageBox.Show($"加载数据失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsLoading = false;
            }
        }

        private async Task SearchAsync()
        {
            CurrentPage = 1;
            await LoadDataAsync();
        }

        private void ResetFilters()
        {
            StartDate = DateTime.Now.Date.AddDays(-30);
            EndDate = DateTime.Now.Date.AddDays(1);
            IsSuccessful = null;
            IsSimulation = null;
            SearchText = string.Empty;
            CurrentPage = 1;
            _ = LoadDataAsync();
        }

        private async Task GoToPageAsync(int page)
        {
            if (page >= 1 && page <= TotalPages)
            {
                CurrentPage = page;
                await LoadDataAsync();
            }
        }

        private void UpdatePagingCommands()
        {
            FirstPageCommand.RaiseCanExecuteChanged();
            PreviousPageCommand.RaiseCanExecuteChanged();
            NextPageCommand.RaiseCanExecuteChanged();
            LastPageCommand.RaiseCanExecuteChanged();
        }

        private async Task ExportAllAsync()
        {
            try
            {
                IsLoading = true;
                StatusMessage = _localizationService.GetString("ExperimentHistory_StatusMsg_Export", "正在导出数据...");

                var allRecords = await _repository.GetExperimentsForExportAsync(StartDate, EndDate);

                var fileName = $"实验记录_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx";
                var filePath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), fileName);

                await _excelExportService.ExportExperimentsAsync(allRecords, filePath);

                StatusMessage = $"{_localizationService.GetString("ExperimentHistory_StatusMsg_ExportComplete", "导出完成: ")}{filePath}";
                MessageBox.Show($"导出完成！\n文件位置: {filePath}", "导出成功", MessageBoxButton.OK, MessageBoxImage.Information);

                // 打开文件所在目录
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{filePath}\"");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "导出数据失败");
                StatusMessage = _localizationService.GetString("ExperimentHistory_StatusMsg_ExportFail", "导出失败");
                MessageBox.Show($"导出失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsLoading = false;
            }
        }

        private async Task ExportSelectedAsync()
        {
            if (SelectedExperiment == null) return;

            try
            {
                IsLoading = true;
                StatusMessage = _localizationService.GetString("ExperimentHistory_StatusMsg_ExportDetails", "正在导出实验详情...");

                var fileName = $"实验详情_{SelectedExperiment.ExperimentName}_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx";
                var filePath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), fileName);

                await _excelExportService.ExportExperimentWithParameterChangesAsync(SelectedExperiment.ExperimentId, filePath);

                StatusMessage = $"{_localizationService.GetString("ExperimentHistory_StatusMsg_ExportComplete", "导出完成: ")}{filePath}";
                MessageBox.Show($"导出完成！\n文件位置: {filePath}", "导出成功", MessageBoxButton.OK, MessageBoxImage.Information);

                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{filePath}\"");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "导出实验详情失败");
                StatusMessage = _localizationService.GetString("ExperimentHistory_StatusMsg_ExportFail", "导出失败");
                MessageBox.Show($"导出失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsLoading = false;
            }
        }

        private async Task ExportParameterChangesAsync()
        {
            try
            {
                IsLoading = true;
                StatusMessage = _localizationService.GetString("ExperimentHistory_StatusMsg_ExportParamChange", "正在导出参数变化记录...");

                var parameterChanges = await _repository.GetParameterChangesForExportAsync(StartDate, EndDate);

                if (!parameterChanges.Any())
                {
                    MessageBox.Show("所选时间范围内没有参数变化记录", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                var fileName = $"设备参数变化记录_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx";
                var filePath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), fileName);

                await _excelExportService.ExportParameterChangesAsync(parameterChanges, filePath);

                StatusMessage = $"{_localizationService.GetString("ExperimentHistory_StatusMsg_ExportComplete", "导出完成: ")}{filePath}";
                MessageBox.Show($"导出完成！\n文件位置: {filePath}\n共导出 {parameterChanges.Count} 条参数变化记录", "导出成功", MessageBoxButton.OK, MessageBoxImage.Information);

                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{filePath}\"");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "导出参数变化记录失败");
                StatusMessage = _localizationService.GetString("ExperimentHistory_StatusMsg_ExportFail", "导出失败");
                MessageBox.Show($"导出失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsLoading = false;
            }
        }

        private async void ViewDetails()
        {
            if (SelectedExperiment == null) return;

            var owner = Application.Current.Windows
                .OfType<Window>()
                .FirstOrDefault(w => w.IsActive) ?? Application.Current.MainWindow;

            var loading = new LoadingOverlayWindow("正在加载实验详情...", owner);
            loading.Show();

            try
            {
                var container = ContainerLocator.Container;
                var vm = container.Resolve<ExperimentDetailWindowViewModel>();

                await vm.LoadDataAsync(SelectedExperiment.ExperimentId, loading);

                if (vm.Experiment == null)
                {
                    loading.Close();
                    MessageBox.Show("未找到实验记录", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                loading.UpdateMessage("正在渲染界面...");

                // 创建详情窗口
                var detailWindow = new Views.ExperimentDetailWindow(vm, owner);

                // 窗口完成渲染后再关闭 loading
                detailWindow.ContentRendered += (s, e) =>
                {
                    loading?.Close();
                    loading = null;
                };

                // ShowDialog 是阻塞的，但 ContentRendered 会在窗口显示后触发
                detailWindow.ShowDialog();

                // 兜底：如果 ContentRendered 没触发（异常情况）
                loading?.Close();
            }
            catch (Exception ex)
            {
                loading?.Close();
                _logger.LogError(ex, "查看实验详情失败");
                MessageBox.Show($"查看实验详情失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task DeleteSelectedAsync()
        {
            if (SelectedExperiment == null) return;

            var result = MessageBox.Show(
                $"确定要删除实验记录 '{SelectedExperiment.ExperimentName}' 吗？\n此操作不可撤销！",
                "确认删除",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes) return;

            try
            {
                IsLoading = true;
                StatusMessage = _localizationService.GetString("ExperimentHistory_StatusMsg_Delete", "正在删除实验记录...");

                await _repository.DeleteExperimentAsync(SelectedExperiment.ExperimentId);

                Experiments.Remove(SelectedExperiment);
                TotalRecords--;
                TotalPages = (int)Math.Ceiling((double)TotalRecords / PageSize);

                StatusMessage = _localizationService.GetString("ExperimentHistory_StatusMsg_DelSuccess", "删除成功");
                MessageBox.Show("实验记录已删除", "删除成功", MessageBoxButton.OK, MessageBoxImage.Information);

                UpdatePagingCommands();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "删除实验记录失败");
                StatusMessage = _localizationService.GetString("ExperimentHistory_StatusMsg_DelFail", "删除失败");
                MessageBox.Show($"删除失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsLoading = false;
            }
        }

        public void OnSelectedExperimentChanged()
        {
            ExportSelectedCommand.RaiseCanExecuteChanged();
            ViewDetailsCommand.RaiseCanExecuteChanged();
            DeleteSelectedCommand.RaiseCanExecuteChanged();
        }

        #region 视图本地化
        
        public string ViewTitle { get; set; }
        public string SearchWatermark { get; set; }
        public string RefreshBtnLabel { get; set; }
        public string ExportLabel { get; set; }
        public string ParamChangeLabel { get; set; }
        public string IndexLabel { get; set; }
        public string NextPageLabel { get; set; }
        public string PreviousPageLabel { get; set; }
        public string BackoverLabel { get; set; }
        public string ExperimentnameHeader { get; set; }
        public string FlownameHeader { get; set; }
        public string TimeHeader { get; set; }
        public string StatusHeader { get; set; }
        public string ModeHeader { get; set; }
        public string ElapsedHeader { get; set; }
        public string OperatorHeader { get; set; }
        public string StatusItemAll { get; set; }
        public string StatusItemSuccess { get; set; }
        public string StatusItemFail { get; set; }
        public string RunItemAll { get; set; }
        public string RunItemActual { get; set; }
        public string RunItemSimulation { get; set; }
        public string EachPageShow { get; set; }
        public string RecordsText { get; set; }

        private void UpdateLocalizationText()
        {
            ViewTitle = _localizationService.GetString("ExperimentHistory_Title", "实验历史记录");
            SearchWatermark = _localizationService.GetString("ExperimentHistory_Search_Watermark", "搜索实验名称、流程名称或操作员");
            RefreshBtnLabel = _localizationService.GetString("ExperimentHistory_Search_Refresh", "刷新");
            ExportLabel = _localizationService.GetString("ExperimentHistory_Export", "导出");
            ParamChangeLabel = _localizationService.GetString("ExperimentHistory_ParamChange", "参数变化");
            IndexLabel = _localizationService.GetString("ExperimentHistory_Index", "首页");
            NextPageLabel = _localizationService.GetString("ExperimentHistory_Next", "下一页");
            PreviousPageLabel = _localizationService.GetString("ExperimentHistory_Previous", "上一页");
            BackoverLabel = _localizationService.GetString("ExperimentHistory_BackOver", "末页");
            ExperimentnameHeader = _localizationService.GetString("ExperimentHistory_Header_ExpName", "实验名称");
            FlownameHeader = _localizationService.GetString("ExperimentHistory_Header_FlowName", "流程");
            TimeHeader = _localizationService.GetString("ExperimentHistory_Header_Time", "时间");
            StatusHeader = _localizationService.GetString("ExperimentHistory_Header_Status", "状态");
            ModeHeader = _localizationService.GetString("ExperimentHistory_Header_Mode", "模式");
            ElapsedHeader = _localizationService.GetString("ExperimentHistory_Header_Elapsed", "耗时");
            OperatorHeader = _localizationService.GetString("ExperimentHistroy_Header_Operator", "操作员");
            StatusItemAll = _localizationService.GetString("ExperimentHistory_Sitem_All", "全部");
            StatusItemSuccess = _localizationService.GetString("ExperimentHistory_Sitem_Success", "成功");
            StatusItemFail = _localizationService.GetString("ExperimentHistory_Sitem_Fail", "失败");
            RunItemAll = _localizationService.GetString("ExperimentHistory_Ritem_All", "全部模式");
            RunItemActual = _localizationService.GetString("ExperimentHistory_Ritem_Actual", "实际");
            RunItemSimulation = _localizationService.GetString("ExperimentHistory_Ritem_Simulation", "仿真");
            EachPageShow = _localizationService.GetString("ExperimentHistory_Each", "每页显示");
            RecordsText = _localizationService.GetString("ExperimentHistory_Number", "条");
        }
        #endregion
    }
}




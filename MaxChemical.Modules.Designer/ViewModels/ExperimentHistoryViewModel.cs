// MaxChemical.Modules.Designer/ViewModels/ExperimentHistoryViewModel.cs
using MaxChemical.Data.Models;
using MaxChemical.Data.Repositories;
using MaxChemical.Data.Services;
using MaxChemical.Infrastructure.Events;
using MaxChemical.Infrastructure.Services;
using MaxChemical.Logging;
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

        private void ViewDetails()
        {
            if (SelectedExperiment == null) return;

            try
            {
                var detailWindow = new Views.ExperimentDetailWindow(SelectedExperiment.ExperimentId);
                detailWindow.ShowDialog();
            }
            catch (Exception ex)
            {
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
        public string TimeRange {  get; set; }
        public string StartTimeLabel { get; set; }
        public string EndTimeLabel { get; set; }
        public string ExecuteStatus {  get; set; }
        public string RunMode { get; set; }
        public string SearchLabel {  get; set; }
        public string SearchWatermark { get; set; }
        public string SearchBtnLabel { get; set; }
        public string ResetBtnLabel { get; set; }
        public string RefreshBtnLabel { get; set; }
        public string ExportAllLabel { get; set; }
        public string ExportAllTip { get; set; }
        public string ExportSelectLabel { get; set; }
        public string ExportSelectTip { get; set; }
        public string ParamChangeLabel { get; set; }
        public string ParamChangeTip { get; set; }
        public string ViewDetailsLabel { get; set; }
        public string ViewDetailsTip { get; set; }
        public string DeleteRecordLabel { get; set; }
        public string DeleteRecordTip { get; set; }
        public string IndexLabel { get; set; }
        public string NextPageLabel { get; set; }
        public string PreviousPageLabel { get; set; }
        public string BackoverLabel { get; set; }
        public string ExperimentnameHeader { get; set; }
        public string FlownameHeader { get; set; }
        public string StartHeader { get; set; }
        public string EndHeader { get; set; }
        public string DurationHeader { get; set; }
        public string ExcuteStatusHeader { get; set; }
        public string RunModeHeader { get; set; }
        public string CompletionrateHeader { get; set; }
        public string OperatorHeader { get; set; }
        public string LabelHeader { get; set; }
        public string DescHeader { get; set; }
        public string StatusItemAll { get; set; }
        public string StatusItemSuccess { get; set; }
        public string StatusItemFail { get; set; }
        public string RunItemAll { get; set; }
        public string RunItemActual { get; set; }
        public string RunItemSimulation { get; set; }
        public string EachPageShow { get; set; }
        public string RecordsText { get; set; }
        public string ATotalText { get; set; }
        public string SecondText { get; set; }
        public string PageText { get; set; }

        private void UpdateLocalizationText()
        {
            ViewTitle = _localizationService.GetString("ExperimentHistory_Title", "实验历史记录");
            TimeRange = _localizationService.GetString("ExperimentHistory_Range", "时间范围");
            StartTimeLabel = _localizationService.GetString("ExperimentHistory_Range_Start", "开始时间");
            EndTimeLabel = _localizationService.GetString("ExperimentHistory_Range_End", "结束时间");
            ExecuteStatus = _localizationService.GetString("ExperimentHistory_ES", "执行状态");
            RunMode = _localizationService.GetString("ExperimentHistory_RM", "运行模式");
            SearchLabel = _localizationService.GetString("ExperimentHistory_Search", "搜索");
            SearchWatermark = _localizationService.GetString("ExperimentHistory_Search_Watermark", "搜索实验名称、流程名称或操作员");
            SearchBtnLabel = _localizationService.GetString("ExperimentHistory_Search_Search", "搜索");
            ResetBtnLabel = _localizationService.GetString("ExperimentHistory_Search_Reset", "重置");
            RefreshBtnLabel = _localizationService.GetString("ExperimentHistory_Search_Refresh", "刷新");
            ExportAllLabel = _localizationService.GetString("ExperimentHistory_ExportAll", "导出全部");
            ExportAllTip = _localizationService.GetString("ExperimentHistory_ExportAll_Tip", "导出所有实验记录（包含参数变化记录）");
            ExportSelectLabel = _localizationService.GetString("ExperimentHistory_ExportSelect", "导出选中");
            ExportSelectTip = _localizationService.GetString("ExperimentHistory_ExportSelect_Tip", "导出选中的实验详细信息");
            ParamChangeLabel = _localizationService.GetString("ExperimentHistory_ParamChange", "参数变化");
            ParamChangeTip = _localizationService.GetString("ExperimentHistory_ParamChange_Tip", "导出设备参数变化记录（去重后的数据）");
            ViewDetailsLabel = _localizationService.GetString("ExperimentHistory_Details", "查看详情");
            //ViewDetailsTip = service.GetString("");
            DeleteRecordLabel = _localizationService.GetString("ExperimentHistory_DeleteRecord", "删除记录");
            //DeleteRecordTip = service.GetString("");
            IndexLabel = _localizationService.GetString("ExperimentHistory_Index", "首页");
            NextPageLabel = _localizationService.GetString("ExperimentHistory_Next", "下一页");
            PreviousPageLabel = _localizationService.GetString("ExperimentHistory_Previous", "上一页");
            BackoverLabel = _localizationService.GetString("ExperimentHistory_BackOver", "末页");
            ExperimentnameHeader = _localizationService.GetString("ExperimentHistory_Header_ExpName", "实验名称");
            FlownameHeader = _localizationService.GetString("ExperimentHistory_Header_FlowName", "流程名称");
            StartHeader = _localizationService.GetString("ExperimentHistory_Header_Start", "开始时间");
            EndHeader = _localizationService.GetString("ExperimentHistory_Header_End", "结束时间");
            DurationHeader = _localizationService.GetString("ExperimentHistory_Header_Duration", "持续时间");
            ExcuteStatusHeader = _localizationService.GetString("ExperimentHistory_Header_Status", "执行状态");
            RunModeHeader = _localizationService.GetString("ExperimentHistory_Header_Mode", "运行模式");
            CompletionrateHeader = _localizationService.GetString("ExperimentHistory_Header_Completion", "完成度");
            OperatorHeader = _localizationService.GetString("ExperimentHistroy_Header_Operator", "操作员");
            LabelHeader = _localizationService.GetString("ExperimentHistory_Header_Label", "标签");
            DescHeader = _localizationService.GetString("ExperimentHistory_Header_Desc", "描述");
            StatusItemAll = _localizationService.GetString("ExperimentHistory_Sitem_All", "全部");
            StatusItemSuccess = _localizationService.GetString("ExperimentHistory_Sitem_Success", "成功");
            StatusItemFail = _localizationService.GetString("ExperimentHistory_Sitem_Fail", "失败");
            RunItemAll = _localizationService.GetString("ExperimentHistory_Ritem_All", "全部");
            RunItemActual = _localizationService.GetString("ExperimentHistory_Ritem_Actual", "实际");
            RunItemSimulation = _localizationService.GetString("ExperimentHistory_Ritem_Simulation", "仿真");
            EachPageShow = _localizationService.GetString("ExperimentHistory_Each", "每页显示");
            RecordsText = _localizationService.GetString("ExperimentHistory_Number", "条记录");
            ATotalText = _localizationService.GetString("ExperimentHistory_Atotal", "共");
            SecondText = _localizationService.GetString("ExperimentHistory_Second", "条记录，第 ");
            PageText = _localizationService.GetString("ExperimentHistory_Pages", " 页");
        }
        #endregion
    }
}




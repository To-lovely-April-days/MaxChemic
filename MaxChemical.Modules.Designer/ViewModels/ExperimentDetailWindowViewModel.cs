// MaxChemical.Modules.Designer/ViewModels/ExperimentDetailWindowViewModel.cs
using MaxChemical.Data.Models;
using MaxChemical.Data.Repositories;
using MaxChemical.Data.Services;
using MaxChemical.Logging;
using Prism.Commands;
using Prism.Mvvm;
using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;

namespace MaxChemical.Modules.Designer.ViewModels
{
    public class ExperimentDetailWindowViewModel : BindableBase
    {
        private readonly IExperimentDataRepository _repository;
        private readonly IExcelExportService _excelExportService;
        private readonly ILogService _logger;
        private readonly string _experimentId;

        private ExperimentRecord? _experiment;
        private ObservableCollection<ParameterChangeRecord> _parameterChanges = new();
        private bool _isLoading;
        private string _statusMessage = "正在加载...";

        public ExperimentDetailWindowViewModel(IExperimentDataRepository repository,
            IExcelExportService excelExportService, ILogService logger)
        {
            _repository = repository ?? throw new ArgumentNullException(nameof(repository));
            _excelExportService = excelExportService ?? throw new ArgumentNullException(nameof(excelExportService));
            _logger = logger?.ForContext<ExperimentDetailWindowViewModel>() ?? throw new ArgumentNullException(nameof(logger));

            InitializeCommands();
        }

        // 用于从外部设置实验ID的方法
        public void SetExperimentId(string experimentId)
        {
            if (string.IsNullOrEmpty(experimentId))
                throw new ArgumentException("实验ID不能为空", nameof(experimentId));

            _ = LoadExperimentAsync(experimentId);
        }

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

        #endregion

        #region Commands

        public DelegateCommand ExportDetailCommand { get; private set; } = null!;

        #endregion

        private void InitializeCommands()
        {
            ExportDetailCommand = new DelegateCommand(async () => await ExportDetailAsync());
        }

        private async Task LoadExperimentAsync(string experimentId)
        {
            try
            {
                IsLoading = true;
                StatusMessage = "正在加载实验详情...";

                // 加载实验基本信息
                var experiment = await _repository.GetExperimentAsync(experimentId);
                if (experiment == null)
                {
                    StatusMessage = "未找到实验记录";
                    MessageBox.Show("未找到实验记录", "错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                Experiment = experiment;

                // 加载参数变化记录
                var parameterChanges = await _repository.GetParameterChangesForExportAsync(experimentId);
                ParameterChanges.Clear();
                foreach (var change in parameterChanges)
                {
                    ParameterChanges.Add(change);
                }

                StatusMessage = $"加载完成 - 共 {ParameterChanges.Count} 条参数变化记录";
                _logger.LogInformation("加载实验详情成功: {ExperimentId}", experimentId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "加载实验详情失败: {ExperimentId}", experimentId);
                StatusMessage = "加载失败";
                MessageBox.Show($"加载实验详情失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsLoading = false;
            }
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
                    StatusMessage = "正在导出实验详情...";

                    await _excelExportService.ExportExperimentWithParameterChangesAsync(Experiment.ExperimentId, saveDialog.FileName);

                    StatusMessage = "导出完成";
                    MessageBox.Show($"导出完成！\n文件位置: {saveDialog.FileName}", "导出成功", MessageBoxButton.OK, MessageBoxImage.Information);

                    // 打开文件所在目录
                    System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{saveDialog.FileName}\"");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "导出实验详情失败");
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
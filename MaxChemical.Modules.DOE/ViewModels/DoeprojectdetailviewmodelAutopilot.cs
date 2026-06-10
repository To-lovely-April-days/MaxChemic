using MaxChemical.Modules.DOE.Data;
using MaxChemical.Modules.DOE.Events;
using MaxChemical.Modules.DOE.Models;
using MaxChemical.Modules.DOE.Services;
using MaxChemical.Modules.DOE.Views;
using Prism.Commands;
using Prism.Events;
using Prism.Mvvm;
using System;
using System.Threading.Tasks;
using System.Windows;

namespace MaxChemical.Modules.DOE.ViewModels
{
    /// <summary>
    /// DOEProjectDetailViewModel 的 AutoPilot 扩展
    /// 
    /// 在原有 ViewModel 中添加以下内容:
    ///   1. AutoPilot 开关属性和命令
    ///   2. 参数设置弹窗触发
    ///   3. 轮次完成事件订阅 → 触发决策 → 弹出报告
    ///   4. 用户确认/覆盖推荐的执行
    /// 
    /// 使用方式: 将以下属性和方法合并到 DOEProjectDetailViewModel 中
    /// </summary>
    public partial class DOEProjectDetailViewModel
    {
        // ═══ 以下字段需要添加到原有 ViewModel 的字段区域 ═══

        private AutoPilotService? _autoPilotService;
        private IEventAggregator? _eventAggregator;
        private string _currentProjectId = "";

        // ── AutoPilot 状态 ──
        private bool _isAutoPilotEnabled;
        private bool _isAutoPilotProcessing;
        private string _autoPilotStatusText = "未启用";
        private AutoPilotReport? _lastReport;

        // ═══ 属性 ═══

        /// <summary>智能决策开关</summary>
        public bool IsAutoPilotEnabled
        {
            get => _isAutoPilotEnabled;
            set
            {
                if (SetProperty(ref _isAutoPilotEnabled, value))
                {
                    _ = ToggleAutoPilotAsync(value);
                    RaisePropertyChanged(nameof(AutoPilotSwitchText));
                    RaisePropertyChanged(nameof(AutoPilotStatusColor));
                }
            }
        }

        /// <summary>是否正在处理决策</summary>
        public bool IsAutoPilotProcessing
        {
            get => _isAutoPilotProcessing;
            set => SetProperty(ref _isAutoPilotProcessing, value);
        }

        /// <summary>AutoPilot 状态文本</summary>
        public string AutoPilotStatusText
        {
            get => _autoPilotStatusText;
            set => SetProperty(ref _autoPilotStatusText, value);
        }

        /// <summary>开关显示文本</summary>
        public string AutoPilotSwitchText => IsAutoPilotEnabled ? "智能决策: 开" : "智能决策: 关";

        /// <summary>状态颜色</summary>
        public string AutoPilotStatusColor => IsAutoPilotEnabled ? "#27AE60" : "#95A5A6";

        /// <summary>最近一次决策报告</summary>
        public AutoPilotReport? LastAutoPilotReport
        {
            get => _lastReport;
            set => SetProperty(ref _lastReport, value);
        }

        // ═══ 命令 ═══

        public DelegateCommand OpenAutoPilotSettingsCommand { get; private set; } = null!;
        public DelegateCommand ViewLastReportCommand { get; private set; } = null!;

        // ═══ 初始化方法（在构造函数末尾调用） ═══

        /// <summary>
        /// 初始化 AutoPilot 相关功能。
        /// 在 DOEProjectDetailViewModel 构造函数末尾调用:
        ///   InitializeAutoPilot(autoPilotService, eventAggregator);
        /// </summary>
        public void InitializeAutoPilot(AutoPilotService autoPilotService, IEventAggregator eventAggregator)
        {
            _autoPilotService = autoPilotService;
            _eventAggregator = eventAggregator;

            OpenAutoPilotSettingsCommand = new DelegateCommand(async () => await OpenAutoPilotSettingsAsync());
            ViewLastReportCommand = new DelegateCommand(() => ShowLastReport(), () => _lastReport != null);

            // 订阅轮次完成事件
            _eventAggregator.GetEvent<DOERoundCompletedEvent>().Subscribe(
                async payload => await OnRoundCompletedAsync(payload),
                ThreadOption.UIThread);
        }

        // ═══ 在 LoadAsync 中追加调用 ═══

        /// <summary>
        /// 在 LoadAsync 方法的末尾追加此调用，加载 AutoPilot 状态
        /// </summary>
        public async Task LoadAutoPilotStateAsync(string projectId)
        {
            _currentProjectId = projectId;

            var config = await _repository.GetAutoPilotConfigAsync(projectId);
            if (config != null)
            {
                _isAutoPilotEnabled = config.Enabled;
                RaisePropertyChanged(nameof(IsAutoPilotEnabled));
                RaisePropertyChanged(nameof(AutoPilotSwitchText));
                RaisePropertyChanged(nameof(AutoPilotStatusColor));
                AutoPilotStatusText = config.Enabled ? "已启用，等待实验完成" : "未启用";
            }
        }

        // ═══ 核心方法 ═══

        /// <summary>切换 AutoPilot 开关</summary>
        private async Task ToggleAutoPilotAsync(bool enabled)
        {
            if (string.IsNullOrEmpty(_currentProjectId)) return;
            try
            {
                await _repository.SetAutoPilotEnabledAsync(_currentProjectId, enabled);
                AutoPilotStatusText = enabled ? "已启用，等待实验完成" : "未启用";
            }
            catch (Exception ex)
            {
                AutoPilotStatusText = $"切换失败: {ex.Message}";
            }
        }

        /// <summary>打开参数设置弹窗</summary>
        private async Task OpenAutoPilotSettingsAsync()
        {
            if (string.IsNullOrEmpty(_currentProjectId)) return;

            var config = await _repository.GetOrCreateAutoPilotConfigAsync(_currentProjectId);

            bool confirmed = false;
            Application.Current.Dispatcher.Invoke(() =>
            {
                var dialog = new AutoPilotConfigDialog(config);
                dialog.Owner = Application.Current.MainWindow;
                dialog.ShowDialog();
                confirmed = dialog.Confirmed;
                if (confirmed) config = dialog.Config;
            });

            if (confirmed)
            {
                await _repository.SaveAutoPilotConfigAsync(config);
                AutoPilotStatusText = "参数已更新";
            }
        }

        /// <summary>轮次完成事件处理 — AutoPilot 核心入口</summary>
        private async Task OnRoundCompletedAsync(DOERoundCompletedPayload payload)
        {
            // 只处理当前项目的事件
            if (payload.ProjectId != _currentProjectId) return;
            if (payload.FinalStatus != DOEBatchStatus.Completed) return;
            if (_autoPilotService == null) return;

            try
            {
                IsAutoPilotProcessing = true;
                AutoPilotStatusText = "正在分析本轮结果...";

                // 调用 AutoPilotService 做分析+决策
                var report = await _autoPilotService.OnRoundCompletedAsync(
                    payload.ProjectId, payload.BatchId);

                LastAutoPilotReport = report;
                ViewLastReportCommand.RaiseCanExecuteChanged();

                if (report.AutoExecuted)
                {
                    // AutoPilot 已自动执行
                    AutoPilotStatusText = report.ProjectCompleted
                        ? "优化完成！"
                        : $"已自动创建下一轮: {report.NextDesignMethodDisplay}, {report.EstimatedRuns} 组实验";
                }
                else
                {
                    // 需要用户确认 → 弹出报告
                    AutoPilotStatusText = "等待用户确认决策方案";
                    ShowReportDialog(report);
                }

                // 刷新项目详情
                await LoadAsync(_currentProjectId);
            }
            catch (Exception ex)
            {
                AutoPilotStatusText = $"分析失败: {ex.Message}";
            }
            finally
            {
                IsAutoPilotProcessing = false;
            }
        }

        /// <summary>弹出决策报告弹窗</summary>
        private void ShowReportDialog(AutoPilotReport report)
        {
            Application.Current.Dispatcher.Invoke(async () =>
            {
                var dialog = new AutoPilotReportDialog(report);
                dialog.Owner = Application.Current.MainWindow;
                dialog.ShowDialog();

                switch (dialog.SelectedAction)
                {
                    case AutoPilotReportDialog.UserAction.Accept:
                        // 用户采纳推荐
                        AutoPilotStatusText = "正在执行推荐方案...";
                        IsAutoPilotProcessing = true;
                        try
                        {
                            var nextBatchId = await _autoPilotService!.AcceptRecommendationAsync(
                                _currentProjectId, report.BatchId);
                            AutoPilotStatusText = !string.IsNullOrEmpty(nextBatchId)
                                ? $"已创建下一轮: {nextBatchId}"
                                : "执行完成";
                            await LoadAsync(_currentProjectId);
                        }
                        finally { IsAutoPilotProcessing = false; }
                        break;

                    case AutoPilotReportDialog.UserAction.Manual:
                        AutoPilotStatusText = "用户选择手动决策";
                        break;

                    case AutoPilotReportDialog.UserAction.Skip:
                        AutoPilotStatusText = "用户跳过本轮决策";
                        break;
                }
            });
        }

        /// <summary>查看最近一次报告</summary>
        private void ShowLastReport()
        {
            if (_lastReport == null) return;
            ShowReportDialog(_lastReport);
        }
    }
}
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using MaxChemical.Infrastructure.Services;
using MaxChemical.Logging;
using MaxChemical.Modules.DOE.Data;
using MaxChemical.Modules.DOE.Events;
using MaxChemical.Modules.DOE.Models;
using MaxChemical.Modules.DOE.Services;
using MaxChemical.Modules.DOE.Views;
using Newtonsoft.Json;
using Prism.Commands;
using Prism.Events;
using Prism.Mvvm;

namespace MaxChemical.Modules.DOE.ViewModels
{
    /// <summary>
    /// DOE 执行仪表盘 ViewModel — 显示执行进度、实时结果表、控制按钮
    /// </summary>
    public class DOEExecutionDashboardViewModel : BindableBase
    {
        private readonly IDOEExecutionService _executionService;
        private readonly IDOERepository _repository;
        private readonly IEventAggregator _eventAggregator;
        private readonly IDialogService _dialogService;
        private readonly ILogService _logger;

        // ── 状态 ──
        private string _batchId = "";
        private string _batchName = "";
        private DOEBatchStatus _batchStatus = DOEBatchStatus.Designing;
        private int _currentRun;
        private int _totalRuns;
        private double _progressPercent;
        private string _statusMessage = "就绪";
        private string _bestResultText = "暂无";
        private bool _isRunning;
        private bool _isPaused;

        // ── 实时结果表 ──
        private ObservableCollection<RunResultRow> _resultRows = new();

        // ── 响应变量定义缓存 ──
        private List<DOEResponse> _responses = new();
        private List<DOEFactor> _factors = new();

        public DOEExecutionDashboardViewModel(
            IDOEExecutionService executionService,
            IDOERepository repository,
            IEventAggregator eventAggregator,
            IDialogService dialogService,
            ILogService logger)
        {
            _executionService = executionService;
            _repository = repository;
            _eventAggregator = eventAggregator;
            _dialogService = dialogService;
            _logger = logger?.ForContext<DOEExecutionDashboardViewModel>()
                      ?? throw new ArgumentNullException(nameof(logger));

            InitializeCommands();
            SubscribeEvents();
        }

        // ══════════════ Properties ══════════════

        public string BatchId { get => _batchId; set => SetProperty(ref _batchId, value); }
        public string BatchName { get => _batchName; set => SetProperty(ref _batchName, value); }
        public DOEBatchStatus BatchStatus { get => _batchStatus; set => SetProperty(ref _batchStatus, value); }
        public int CurrentRun { get => _currentRun; set => SetProperty(ref _currentRun, value); }
        public int TotalRuns { get => _totalRuns; set => SetProperty(ref _totalRuns, value); }
        public double ProgressPercent { get => _progressPercent; set => SetProperty(ref _progressPercent, value); }
        public string StatusMessage { get => _statusMessage; set => SetProperty(ref _statusMessage, value); }
        public string BestResultText { get => _bestResultText; set => SetProperty(ref _bestResultText, value); }
        public bool IsRunning { get => _isRunning; set { SetProperty(ref _isRunning, value); RaiseCommandCanExecute(); } }
        public bool IsPaused { get => _isPaused; set { SetProperty(ref _isPaused, value); RaiseCommandCanExecute(); } }
        public ObservableCollection<RunResultRow> ResultRows { get => _resultRows; set => SetProperty(ref _resultRows, value); }

        public string ProgressText => $"{CurrentRun} / {TotalRuns}";

        // ══════════════ Commands ══════════════

        public DelegateCommand StartCommand { get; private set; } = null!;
        public DelegateCommand PauseCommand { get; private set; } = null!;
        public DelegateCommand ResumeCommand { get; private set; } = null!;
        public DelegateCommand AbortCommand { get; private set; } = null!;

        private void InitializeCommands()
        {
            StartCommand = new DelegateCommand(async () => await StartExecutionAsync(), () => !IsRunning && !string.IsNullOrEmpty(BatchId));
            PauseCommand = new DelegateCommand(() => _executionService.PauseBatch(), () => IsRunning && !IsPaused);
            ResumeCommand = new DelegateCommand(() => _executionService.ResumeBatch(), () => IsPaused);
            AbortCommand = new DelegateCommand(async () => await AbortExecutionAsync(), () => IsRunning || IsPaused);
        }

        private void RaiseCommandCanExecute()
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                StartCommand.RaiseCanExecuteChanged();
                PauseCommand.RaiseCanExecuteChanged();
                ResumeCommand.RaiseCanExecuteChanged();
                AbortCommand.RaiseCanExecuteChanged();
            });
        }

        // ══════════════ 加载批次 ══════════════

        /// <summary>
        /// 加载批次数据并准备执行
        /// </summary>
        public async Task LoadBatchAsync(string batchId)
        {
            try
            {
                var batch = await _repository.GetBatchWithDetailsAsync(batchId);
                if (batch == null)
                {
                    _dialogService.ShowError($"未找到批次: {batchId}", "错误");
                    return;
                }

                BatchId = batchId;
                BatchName = batch.BatchName;
                _responses = batch.Responses;
                _factors = batch.Factors;

                var pendingRuns = batch.Runs
                    .Where(r => r.DataSource == DOEDataSource.Measured && r.Status == DOERunStatus.Pending)
                    .ToList();
                TotalRuns = pendingRuns.Count;
                CurrentRun = 0;
                ProgressPercent = 0;

                // 加载已完成的结果
                ResultRows.Clear();
                var completedRuns = batch.Runs.Where(r => r.Status == DOERunStatus.Completed).OrderBy(r => r.RunIndex);
                foreach (var run in completedRuns)
                {
                    AddResultRow(run);
                }

                UpdateBestResult();
                StatusMessage = $"已加载批次: {batch.BatchName}, {TotalRuns} 组待执行";
                StartCommand.RaiseCanExecuteChanged();

                _logger.LogInformation("加载 DOE 批次: {BatchId}, {TotalRuns} 组待执行", batchId, TotalRuns);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "加载批次失败: {BatchId}", batchId);
                _dialogService.ShowError($"加载失败: {ex.Message}", "错误");
            }
        }

        // ══════════════ 执行控制 ══════════════

        private async Task StartExecutionAsync()
        {
            try
            {
                IsRunning = true;
                IsPaused = false;
                StatusMessage = "正在启动批量执行...";

                // 订阅执行服务事件
                _executionService.RunCompleted += OnRunCompleted;
                _executionService.ProgressChanged += OnProgressChanged;
                _executionService.BatchCompleted += OnBatchCompleted;

                await _executionService.StartBatchAsync(BatchId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "启动执行失败");
                _dialogService.ShowError($"启动失败: {ex.Message}", "错误");
                IsRunning = false;
            }
        }

        private async Task AbortExecutionAsync()
        {
            var confirm = _dialogService.ShowConfirmation("确定要终止当前批次执行？", "终止确认");
            if (!confirm) return;

            try
            {
                await _executionService.AbortBatchAsync();
                StatusMessage = "批次已终止";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "终止执行失败");
            }
        }

        // ══════════════ 事件处理 ══════════════

        private void SubscribeEvents()
        {
            // 订阅 Prism 事件：流程执行完成后弹框采集响应
            _eventAggregator.GetEvent<DOERunCompletedEvent>().Subscribe(OnDOERunNeedsResponse, ThreadOption.UIThread);
            _eventAggregator.GetEvent<DOEProgressChangedEvent>().Subscribe(OnDOEProgressChanged, ThreadOption.UIThread);
        }

        /// <summary>
        /// 流程执行完成，需要弹框让用户输入响应值
        /// </summary>
        private void OnDOERunNeedsResponse(DOERunCompletedPayload payload)
        {
            if (payload.BatchId != BatchId) return;

            try
            {
                var factorValues = JsonConvert.DeserializeObject<Dictionary<string, double>>(payload.FactorValuesJson)
                                   ?? new Dictionary<string, double>();

                var responseDefs = _responses.Select(r => (r.ResponseName, r.Unit)).ToList();

                var dialog = new DOEResponseCollectionDialog(
                    payload.RunIndex, TotalRuns, factorValues, responseDefs);
                dialog.Owner = Application.Current.MainWindow;

                var result = dialog.ShowDialog();

                if (result == true && dialog.CollectedValues != null)
                {
                    _executionService.SubmitRunResponse(payload.RunIndex, dialog.CollectedValues);
                }
                else
                {
                    // 用户跳过——提交空值
                    _executionService.SubmitRunResponse(payload.RunIndex, new Dictionary<string, double>());
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "弹框采集响应值失败");
                _executionService.SubmitRunResponse(payload.RunIndex, new Dictionary<string, double>());
            }
        }

        private void OnDOEProgressChanged(DOEProgressPayload payload)
        {
            if (payload.BatchId != BatchId) return;
            CurrentRun = payload.CurrentRun;
            ProgressPercent = TotalRuns > 0 ? (double)CurrentRun / TotalRuns * 100 : 0;
            StatusMessage = payload.StatusMessage;
            RaisePropertyChanged(nameof(ProgressText));
        }

        private void OnRunCompleted(object? sender, DOERunCompletedEventArgs e)
        {
            Application.Current?.Dispatcher.Invoke(async () =>
            {
                try
                {
                    // 从数据库重新加载这组的完整记录
                    var run = await _repository.GetRunAsync(BatchId, e.RunIndex);
                    if (run != null)
                    {
                        AddResultRow(run);
                        UpdateBestResult();
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "更新结果表失败");
                }
            });
        }

        private void OnProgressChanged(object? sender, DOEProgressEventArgs e)
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                CurrentRun = e.CurrentRun;
                ProgressPercent = e.ProgressPercent;
                StatusMessage = e.StatusMessage;
                RaisePropertyChanged(nameof(ProgressText));
            });
        }

        private void OnBatchCompleted(object? sender, DOEBatchCompletedEventArgs e)
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                IsRunning = false;
                IsPaused = false;
                BatchStatus = e.FinalStatus;
                StatusMessage = $"批次完成: {e.StopReason} (完成 {e.CompletedRuns}/{e.TotalRuns} 组)";

                // 取消订阅
                _executionService.RunCompleted -= OnRunCompleted;
                _executionService.ProgressChanged -= OnProgressChanged;
                _executionService.BatchCompleted -= OnBatchCompleted;

                _dialogService.ShowInfo(StatusMessage, "DOE 执行完成");
            });
        }

        // ══════════════ 结果表辅助 ══════════════

        private void AddResultRow(DOERunRecord run)
        {
            var factors = JsonConvert.DeserializeObject<Dictionary<string, double>>(run.FactorValuesJson)
                          ?? new Dictionary<string, double>();
            var responses = !string.IsNullOrEmpty(run.ResponseValuesJson)
                ? JsonConvert.DeserializeObject<Dictionary<string, double>>(run.ResponseValuesJson) ?? new()
                : new Dictionary<string, double>();

            ResultRows.Add(new RunResultRow
            {
                RunIndex = run.RunIndex + 1,
                FactorValues = factors,
                ResponseValues = responses,
                Status = run.Status.ToString(),
                DataSource = run.DataSource.ToString(),
                Duration = run.StartTime.HasValue && run.EndTime.HasValue
                    ? (run.EndTime.Value - run.StartTime.Value).ToString(@"mm\:ss")
                    : "-"
            });
        }

        private void UpdateBestResult()
        {
            if (ResultRows.Count == 0 || _responses.Count == 0)
            {
                BestResultText = "暂无";
                return;
            }

            var firstResponseName = _responses[0].ResponseName;
            var bestRow = ResultRows
                .Where(r => r.ResponseValues.ContainsKey(firstResponseName))
                .OrderByDescending(r => r.ResponseValues[firstResponseName])
                .FirstOrDefault();

            if (bestRow != null && bestRow.ResponseValues.TryGetValue(firstResponseName, out var bestVal))
            {
                BestResultText = $"第 {bestRow.RunIndex} 组: {firstResponseName} = {bestVal:F2}";
            }
        }
    }

    /// <summary>
    /// 结果表行数据
    /// </summary>
    public class RunResultRow : BindableBase
    {
        public int RunIndex { get; set; }
        public Dictionary<string, double> FactorValues { get; set; } = new();
        public Dictionary<string, double> ResponseValues { get; set; } = new();
        public string Status { get; set; } = "";
        public string DataSource { get; set; } = "";
        public string Duration { get; set; } = "";

        /// <summary>
        /// 因子值显示文本
        /// </summary>
        public string FactorsSummary => string.Join(", ", FactorValues.Select(kv => $"{kv.Key}={kv.Value:F2}"));

        /// <summary>
        /// 响应值显示文本
        /// </summary>
        public string ResponsesSummary => ResponseValues.Count > 0
            ? string.Join(", ", ResponseValues.Select(kv => $"{kv.Key}={kv.Value:F2}"))
            : "-";
    }
}

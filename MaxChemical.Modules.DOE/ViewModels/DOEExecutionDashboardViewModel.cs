using MaxChemical.Infrastructure.Events;
using MaxChemical.Infrastructure.Services;
using MaxChemical.Logging;
using MaxChemical.Modules.DOE.Data;
using MaxChemical.Modules.DOE.Events;
using MaxChemical.Modules.DOE.Models;
using MaxChemical.Modules.DOE.Services;
using MaxChemical.Modules.DOE.Views;
using Microsoft.Xaml.Behaviors.Media;
using Newtonsoft.Json;
using Prism.Commands;
using Prism.Events;
using Prism.Mvvm;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;

namespace MaxChemical.Modules.DOE.ViewModels
{
    public class DOEExecutionDashboardViewModel : BindableBase
    {
        private readonly IDOEExecutionService _executionService;
        private readonly IDOERepository _repository;
        private readonly IEventAggregator _eventAggregator;
        private readonly IDialogService _dialogService;
        private readonly ILogService _logger;
        private readonly ILocalizationService _localizationService;

        // ── 批次基本信息 ──
        private string _batchId = "";
        private string _batchName = "";
        private string _flowName = "";
        private string _designMethodText = "";
        private DOEBatchStatus _batchStatus = DOEBatchStatus.Designing;

        // ── 执行状态 ──
        private int _currentRun;
        private int _totalRuns;
        private double _progressPercent;
        private bool _isRunning;
        private bool _isPaused;
        private bool _isPausing;

        // ── 指标卡片 ──
        private string _statusMsgKey = "Doe_Exec_StateMsg_Already";
        private string _statusMsgDefaul = "就绪";
        private object[] _statusMsgObjs = Array.Empty<object>();
        private string _statusMessage = "就绪";
        private string _bestRunText = "—";
        private string _bestValTxtKey = "Doe_Exec_NoYet";
        private string _bestValTxtDefault = "暂无结果";
        private string _bestValueText = "暂无结果";
        private string _gprStatusText = "未创建";
        private string _gprDetailText = "";
        private string _etaText = "—";
        private string _etaDetailText = "";

        // ── 动态实验矩阵表 ──
        private DataTable _matrixTable = new();

        // ── 缓存 ──
        private List<DOEResponse> _responses = new();
        private List<DOEFactor> _factors = new();
        private List<string> _factorNames = new();
        private List<string> _responseNames = new();
        private DateTime? _firstRunStartTime;

        // ── 统计（按钮状态用）──
        private int _pendingCount;
        private int _failedCount;

        // ──  迷你模式 ──
        private bool _isMiniMode;
        private ObservableCollection<ProgressSegment> _progressSegments = new();
        private ObservableCollection<FactorDisplayItem> _currentFactorItems = new();
        private bool _hasCurrentFactors;
        private readonly AutoPilotService _autoPilotService;
        private bool _autoPilotEnabledForThisRun = false;
        private bool _isAutoPilotEnabled = false;
        private string _autoPilotStatusText = "";
        private string _currentProjectId = "";

        public DOEExecutionDashboardViewModel(
            IDOEExecutionService executionService,
            IDOERepository repository,
            IEventAggregator eventAggregator,
            IDialogService dialogService,
            AutoPilotService autoPilotService,
            ILogService logger,
            ILocalizationService localizationService)
        {
            _executionService = executionService;
            _repository = repository;
            _eventAggregator = eventAggregator;
            _dialogService = dialogService;
            _autoPilotService = autoPilotService;
            _logger = logger?.ForContext<DOEExecutionDashboardViewModel>()
                      ?? throw new ArgumentNullException(nameof(logger));
            _localizationService = localizationService;
            
            InitializeCommands();
            SubscribeEvents();

            OnLanguageChangedEvent("");
        }

        // ══════════════ Properties ══════════════

        public string BatchId { get => _batchId; set => SetProperty(ref _batchId, value); }
        public string BatchName { get => _batchName; set => SetProperty(ref _batchName, value); }
        public string FlowName { get => _flowName; set => SetProperty(ref _flowName, value); }
        public string DesignMethodText { get => _designMethodText; set => SetProperty(ref _designMethodText, value); }
        public DOEBatchStatus BatchStatus { get => _batchStatus; set => SetProperty(ref _batchStatus, value); }

        public int CurrentRun
        {
            get => _currentRun;
            // ★ 修复: ProgressBarWidth → ProgressRatio
            set { if (SetProperty(ref _currentRun, value)) { RaisePropertyChanged(nameof(ProgressText)); RaisePropertyChanged(nameof(ProgressRatio)); RaisePropertyChanged(nameof(StartButtonText)); UpdateETA(); UpdateProgressSegments(); } }
        }

        public int TotalRuns
        {
            get => _totalRuns;
            // ★ 修复: ProgressBarWidth → ProgressRatio
            set { if (SetProperty(ref _totalRuns, value)) { RaisePropertyChanged(nameof(ProgressText)); RaisePropertyChanged(nameof(ProgressRatio)); UpdateETA(); UpdateProgressSegments(); } }
        }

        public double ProgressPercent
        {
            get => _progressPercent;
            // ★ 修复: ProgressBarWidth → ProgressRatio
            set { if (SetProperty(ref _progressPercent, value)) RaisePropertyChanged(nameof(ProgressRatio)); }
        }

        public bool IsRunning { get => _isRunning; set { SetProperty(ref _isRunning, value); RaiseCommandCanExecute(); } }
        public bool IsPaused { get => _isPaused; set { SetProperty(ref _isPaused, value); RaiseCommandCanExecute(); UpdateProgressSegments(); } }
        public bool IsPausing { get => _isPausing; set => SetProperty(ref _isPausing, value); }

        
        public string StatusMessage { get => _statusMessage; set => SetProperty(ref _statusMessage, value); }
        public string ProgressText => $"{CurrentRun} / {TotalRuns}";

        // ★ 修复: 进度条改为 0~1 比例值，配合 XAML 的 ScaleTransform 自适应宽度
        public double ProgressRatio => TotalRuns > 0 ? ProgressPercent / 100.0 : 0;

        public string BestRunText { get => _bestRunText; set => SetProperty(ref _bestRunText, value); }
        public string BestValueText { get => _bestValueText; set => SetProperty(ref _bestValueText, value); }
        public string GPRStatusText { get => _gprStatusText; set => SetProperty(ref _gprStatusText, value); }
        public string GPRDetailText { get => _gprDetailText; set => SetProperty(ref _gprDetailText, value); }
        public string ETAText { get => _etaText; set => SetProperty(ref _etaText, value); }
        public string ETADetailText { get => _etaDetailText; set => SetProperty(ref _etaDetailText, value); }

        public DataTable MatrixTable { get => _matrixTable; set => SetProperty(ref _matrixTable, value); }

        public int PendingCount { get => _pendingCount; set { SetProperty(ref _pendingCount, value); RaiseCommandCanExecute(); } }
        public int FailedCount { get => _failedCount; set { SetProperty(ref _failedCount, value); RaiseCommandCanExecute(); } }
        public bool HasFailedRuns => FailedCount > 0;
        public string StartButtonText => CurrentRun > 0 ? _localizationService.GetString("Doe_Exec_ContinueExec", "继续执行") :_localizationService.GetString("Doe_Exec_StartExec", "开始执行");

        // ──  迷你模式属性 ──

        /// <summary>是否处于迷你监控模式</summary>
        public bool IsMiniMode
        {
            get => _isMiniMode;
            set => SetProperty(ref _isMiniMode, value);
        }

        /// <summary>分段进度条数据（用于 ItemsControl 绑定）</summary>
        public ObservableCollection<ProgressSegment> ProgressSegments
        {
            get => _progressSegments;
            set => SetProperty(ref _progressSegments, value);
        }

        /// <summary>当前因子参数列表（用于迷你面板三列展示）</summary>
        public ObservableCollection<FactorDisplayItem> CurrentFactorItems
        {
            get => _currentFactorItems;
            set => SetProperty(ref _currentFactorItems, value);
        }

        /// <summary>是否有当前因子数据可显示</summary>
        public bool HasCurrentFactors
        {
            get => _hasCurrentFactors;
            set => SetProperty(ref _hasCurrentFactors, value);
        }
        /// <summary>智能决策是否启用（面板显示 + 用户可切换）</summary>
        public bool IsAutoPilotEnabled
        {
            get => _isAutoPilotEnabled;
            set
            {
                if (SetProperty(ref _isAutoPilotEnabled, value))
                {
                    _autoPilotEnabledForThisRun = value;
                    AutoPilotStatusText = value ? _localizationService.GetString("Doe_Exec_Enabled", "已启用") : _localizationService.GetString("Doe_Exec_Enabled","未启用");
                    // 同步到数据库
                    if (!string.IsNullOrEmpty(_currentProjectId))
                        _ = _repository.SetAutoPilotEnabledAsync(_currentProjectId, value);
                }
            }
        }

        /// <summary>智能决策状态文本</summary>
        public string AutoPilotStatusText
        {
            get => _autoPilotStatusText;
            set => SetProperty(ref _autoPilotStatusText, value);
        }

        /// <summary>是否是项目批次（控制 AutoPilot 控件显隐）</summary>
        public bool IsProjectBatch => !string.IsNullOrEmpty(_currentProjectId);
        // ══════════════ Commands ══════════════

        public DelegateCommand ContinueCommand { get; private set; } = null!;
        public DelegateCommand RetryFailedCommand { get; private set; } = null!;
        public DelegateCommand PauseCommand { get; private set; } = null!;
        public DelegateCommand ResumeCommand { get; private set; } = null!;
        public DelegateCommand AbortCommand { get; private set; } = null!;
        public DelegateCommand ToggleMiniModeCommand { get; private set; } = null!;

        public DelegateCommand StartCommand => ContinueCommand;

        public DelegateCommand TestAutoPilotCommand { get; private set; } = null!;
        private void InitializeCommands()
        {
            ContinueCommand = new DelegateCommand(async () => await ContinueExecutionAsync(),
                () => !IsRunning && !string.IsNullOrEmpty(BatchId) && PendingCount > 0);
            RetryFailedCommand = new DelegateCommand(async () => await RetryFailedAsync(),
                () => !IsRunning && !string.IsNullOrEmpty(BatchId) && FailedCount > 0);
            PauseCommand = new DelegateCommand(() =>
            {
                _executionService.PauseBatch();
                IsPausing = true;
                //StatusMessage = "正在等待当前组完成后暂停...";

                SetStatusMessageLocalizationTxt("Doe_Exec_StateMsg_WaitComplete", "正在等待当前组完成后暂停...");
            }, () => IsRunning && !IsPaused && !IsPausing);
            ResumeCommand = new DelegateCommand(() =>
            {
                _executionService.ResumeBatch();
                IsPaused = false;
                IsPausing = false;
                //StatusMessage = "已恢复执行...";

                SetStatusMessageLocalizationTxt("Doe_Exec_StateMsg_ResumeExec", "已恢复执行...");
                IsMiniMode = true;
            }, () => IsPaused);
            AbortCommand = new DelegateCommand(async () => await AbortExecutionAsync(), () => IsRunning || IsPaused);
            ToggleMiniModeCommand = new DelegateCommand(() => IsMiniMode = !IsMiniMode);
            // 临时测试用，加到 InitializeCommands 里
            TestAutoPilotCommand = new DelegateCommand(async () =>
            {
                _autoPilotEnabledForThisRun = true;
                await TriggerAutoPilotAsync();
            });
        }

        private void RaiseCommandCanExecute()
        {
            Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                ContinueCommand.RaiseCanExecuteChanged();
                RetryFailedCommand.RaiseCanExecuteChanged();
                PauseCommand.RaiseCanExecuteChanged();
                ResumeCommand.RaiseCanExecuteChanged();
                AbortCommand.RaiseCanExecuteChanged();
                RaisePropertyChanged(nameof(HasFailedRuns));
                RaisePropertyChanged(nameof(IsPausing));
            });
        }

        // ══════════════ 加载批次 ══════════════

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
                BatchStatus = batch.Status;
                FlowName = batch.FlowName;
                DesignMethodText = batch.DesignMethod switch
                {
                    DOEDesignMethod.Custom => _localizationService.GetString("Doe_DesignMethod_Custom", "自定义"),
                    DOEDesignMethod.CCD => _localizationService.GetString("Doe_DesignMethod_CCD", "CCD 中心复合"),
                    DOEDesignMethod.BoxBehnken => _localizationService.GetString("Doe_DesignMethod_BoxBehnken", "Box-Behnken"),
                    DOEDesignMethod.DOptimal => _localizationService.GetString("Doe_DesignMethod_DOptimal", "D-Optimal"),
                    DOEDesignMethod.FullFactorial => _localizationService.GetString("Doe_DesignMethod_FullFactorial", "全因子"),
                    DOEDesignMethod.FractionalFactorial => _localizationService.GetString("Doe_DesignMethod_FractionalFactorial", "部分因子"),
                    DOEDesignMethod.Taguchi => _localizationService.GetString("Doe_DesignMethod_Taguchi", "Taguchi"),
                    DOEDesignMethod.PlackettBurman => _localizationService.GetString("Doe_DesignMethod_PlackettBurman", "Plackett-Burman"),
                    _ => batch.DesignMethod.ToString()
                };
                _responses = batch.Responses;
                _factors = batch.Factors;
                _factorNames = _factors.Select(f => f.FactorName).ToList();
                _responseNames = _responses.Select(r => r.ResponseName).ToList();

                var activeRuns = batch.Runs.Where(r => r.Status != DOERunStatus.Skipped).ToList();
                var completedCount = activeRuns.Count(r => r.Status == DOERunStatus.Completed);
                PendingCount = activeRuns.Count(r => r.Status == DOERunStatus.Pending || r.Status == DOERunStatus.Suspended);
                FailedCount = activeRuns.Count(r => r.Status == DOERunStatus.Failed);
                // ★ 修复: CurrentRun = 已执行过的组数（完成+失败），表示"进行到第几组"
                var executedCount = completedCount + FailedCount;
                TotalRuns = activeRuns.Count;
                CurrentRun = executedCount;
                ProgressPercent = TotalRuns > 0 ? (double)executedCount / TotalRuns * 100 : 0;

                BuildMatrixTable(batch.Runs);
                UpdateBestResult();
                await UpdateGPRStatusAsync(batch.FlowId);
                UpdateETA();
                UpdateProgressSegments();

                //StatusMessage = $"已加载: {batch.BatchName}, {PendingCount} 组待执行, {completedCount} 组已完成"
                //    + (FailedCount > 0 ? $", {FailedCount} 组失败" : "");

                if (FailedCount > 0)
                {
                    SetStatusMessageLocalizationTxt(
                        "Doe_Exec_StateMsg_AlreadyLoadFail",
                        "已加载: {0}, {1} 组待执行, {2} 组已完成, {3} 组失败",
                        batch.BatchName, PendingCount, completedCount, FailedCount);
                }
                else
                {
                    SetStatusMessageLocalizationTxt(
                        "Doe_Exec_StateMsg_AlreadyLoad",
                        "已加载: {0}, {1} 组待执行, {2} 组已完成",
                        batch.BatchName, PendingCount, completedCount);
                }

                ContinueCommand.RaiseCanExecuteChanged();
                RetryFailedCommand.RaiseCanExecuteChanged();
                // ★ 加载 AutoPilot 状态
                _currentProjectId = batch.ProjectId ?? "";
                if (!string.IsNullOrEmpty(_currentProjectId))
                {
                    var apConfig = await _repository.GetAutoPilotConfigAsync(_currentProjectId);
                    _isAutoPilotEnabled = apConfig?.Enabled == true;
                    _autoPilotEnabledForThisRun = _isAutoPilotEnabled;
                    AutoPilotStatusText = _isAutoPilotEnabled ? _localizationService.GetString("Doe_Exec_Enabled", "已启用") : _localizationService.GetString("Doe_Exec_Enabled", "未启用");
                    RaisePropertyChanged(nameof(IsAutoPilotEnabled));
                }
                else
                {
                    _isAutoPilotEnabled = false;
                    _autoPilotEnabledForThisRun = false;
                    AutoPilotStatusText = "";
                }
                RaisePropertyChanged(nameof(IsProjectBatch));
                _logger.LogInformation("加载 DOE 批次: {BatchId}, 总计 {Total} 组, 已完成 {Done} 组",
                    batchId, TotalRuns, completedCount);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "加载批次失败: {BatchId}", batchId);
                _dialogService.ShowError($"加载失败: {ex.Message}", "错误");
            }
        }

        // ══════════════ 动态表格构建 ══════════════

        private void BuildMatrixTable(List<DOERunRecord> runs)
        {
            var dt = new DataTable();

            dt.Columns.Add("#", typeof(int));
            dt.Columns.Add("_RunIndex", typeof(int));
            foreach (var name in _factorNames)
                dt.Columns.Add(name, typeof(string));
            foreach (var name in _responseNames)
                dt.Columns.Add(name, typeof(string));
            dt.Columns.Add("状态", typeof(string));
            dt.Columns.Add("耗时", typeof(string));

            var visibleRuns = runs
                .Where(r => r.Status != DOERunStatus.Skipped)
                .OrderBy(r => r.RunIndex);

            int displayIndex = 1;
            foreach (var run in visibleRuns)
            {
                var row = dt.NewRow();
                row["#"] = displayIndex++;
                row["_RunIndex"] = run.RunIndex;

                var factors = JsonConvert.DeserializeObject<Dictionary<string, object>>(run.FactorValuesJson)
                              ?? new Dictionary<string, object>();
                foreach (var name in _factorNames)
                    row[name] = factors.TryGetValue(name, out var v) ? v?.ToString() ?? "—" : "—";

                var responses = !string.IsNullOrEmpty(run.ResponseValuesJson)
                    ? JsonConvert.DeserializeObject<Dictionary<string, double>>(run.ResponseValuesJson) ?? new()
                    : new Dictionary<string, double>();
                foreach (var name in _responseNames)
                    row[name] = responses.TryGetValue(name, out var rv) ? rv.ToString("F2") : "—";

                row["状态"] = run.Status switch
                {
                    DOERunStatus.Pending => "待执行",
                    DOERunStatus.Running => "执行中",
                    DOERunStatus.WaitingResponse => "等待输入",
                    DOERunStatus.Completed => "已完成",
                    DOERunStatus.Failed => "失败",
                    DOERunStatus.Skipped => "已跳过",
                    DOERunStatus.Suspended => "已暂停",
                    _ => run.Status.ToString()
                };

                row["耗时"] = run.StartTime.HasValue && run.EndTime.HasValue
                    ? (run.EndTime.Value - run.StartTime.Value).ToString(@"mm\:ss")
                    : "—";

                dt.Rows.Add(row);
            }

            MatrixTable = dt;
        }

        // ══════════════ 指标更新 ══════════════

        private void UpdateBestResult()
        {
            if (MatrixTable.Rows.Count == 0 || _responseNames.Count == 0)
            {
                BestRunText = "—";
                _bestValTxtKey = "Doe_Exec_NoYet";
                BestValueText = _localizationService.GetString("Doe_Exec_NoYet", "暂无结果");
                return;
            }

            var primaryResp = _responseNames[0];
            double bestVal = double.MinValue;
            int bestIndex = -1;

            foreach (DataRow row in MatrixTable.Rows)
            {
                var valStr = row[primaryResp]?.ToString();
                if (valStr != null && valStr != "—" && double.TryParse(valStr, out var val) && val > bestVal)
                {
                    bestVal = val;
                    bestIndex = Convert.ToInt32(row["#"]);
                }
            }

            if (bestIndex > 0)
            {
                BestRunText = string.Format(_localizationService.GetString("Doe_Exec_Whichgroup", "第 {0} 组"),bestIndex);
                BestValueText = $"{primaryResp} = {bestVal:F2}";
                _bestValTxtKey = "";
            }
            else
            {
                BestRunText = "—";
                _bestValTxtKey = "Doe_Exec_NoYet";
                BestValueText = _localizationService.GetString("Doe_Exec_NoYet", "暂无结果");
            }
        }

        private async Task UpdateGPRStatusAsync(string flowId)
        {
            try
            {
                var signature = GPRModelState.BuildSignature(_factorNames);
                var state = await _repository.GetGPRModelStateAsync(flowId, signature);

                if (state == null)
                {
                    GPRStatusText = "未创建";
                    GPRDetailText = "";
                }
                else if (state.IsActive)
                {
                    GPRStatusText = $"R² = {state.RSquared:F4}";
                    GPRDetailText = $"{state.DataCount} 组 · RMSE {state.RMSE:F2}";
                }
                else
                {
                    GPRStatusText = $"冷启动 {state.DataCount}/6";
                    GPRDetailText = $"需 {Math.Max(0, 6 - state.DataCount)} 组激活";
                }
            }
            catch
            {
                GPRStatusText = "—";
                GPRDetailText = "";
            }
        }

        private void UpdateETA()
        {
            if (CurrentRun == 0 || !_firstRunStartTime.HasValue)
            {
                ETAText = "—";
                ETADetailText = string.Format(_localizationService.GetString("Doe_Exec_ETAD_group", $"{0} 组实验"),TotalRuns);
                return;
            }

            var elapsed = DateTime.Now - _firstRunStartTime.Value;
            var avgPerRun = elapsed.TotalSeconds / CurrentRun;
            var remaining = (TotalRuns - CurrentRun) * avgPerRun;

            if (remaining < 60)
                ETAText = $"~{remaining:F0} {_localizationService.GetString("Doe_Exec_ETA_S", "秒")}";
            else
                ETAText = $"~{remaining / 60:F0} {_localizationService.GetString("Doe_Exec_ETA_M", "分钟")}";

            ETADetailText = string.Format(_localizationService.GetString("Doe_Exec_ETAD_Avg", "均 {0:F0}s/组 · 剩 {1} 组"), avgPerRun, TotalRuns - CurrentRun);
        }

        // ══════════════  迷你模式辅助方法 ══════════════

        /// <summary>
        /// 根据当前进度生成分段进度条数据
        /// </summary>
        private void UpdateProgressSegments()
        {
            if (TotalRuns <= 0) return;

            // 最多显示 9 段，超过时按比例分组
            int segCount = Math.Min(TotalRuns, 9);
            var segments = new ObservableCollection<ProgressSegment>();

            double runsPerSeg = (double)TotalRuns / segCount;

            for (int i = 0; i < segCount; i++)
            {
                double segEnd = (i + 1) * runsPerSeg;
                if (CurrentRun >= segEnd)
                    segments.Add(ProgressSegment.Completed(IsPaused));
                else if (CurrentRun > i * runsPerSeg)
                    segments.Add(ProgressSegment.Running(IsPaused));
                else
                    segments.Add(ProgressSegment.Pending());
            }

            ProgressSegments = segments;
        }

        /// <summary>
        /// 更新当前因子参数显示（从最新执行/正在执行的 run 中获取）
        /// </summary>
        private void UpdateCurrentFactors(DOERunRecord? run)
        {
            if (run == null || string.IsNullOrEmpty(run.FactorValuesJson))
            {
                HasCurrentFactors = false;
                return;
            }

            try
            {
                var factors = JsonConvert.DeserializeObject<Dictionary<string, object>>(run.FactorValuesJson);
                if (factors == null || factors.Count == 0)
                {
                    HasCurrentFactors = false;
                    return;
                }

                var items = new ObservableCollection<FactorDisplayItem>();
                foreach (var kv in factors)
                {
                    // ★ 修复: 删除硬编码单位推断，DOEFactor 无 Unit 属性，不猜测单位
                    string unit = "";

                    // 缩短因子名用于显示
                    string shortName = kv.Key
                        .Replace("Target", "")
                        .Replace("target", "");

                    // ★ 修复 (v3): 类别因子显示标签，连续因子显示数值
                    string displayValue;
                    if (kv.Value is double d)
                        displayValue = d.ToString("F2");
                    else if (double.TryParse(kv.Value?.ToString(), out var parsed))
                        displayValue = parsed.ToString("F2");
                    else
                        displayValue = kv.Value?.ToString() ?? "—";

                    items.Add(new FactorDisplayItem
                    {
                        Label = shortName,
                        Value = displayValue,
                        Unit = unit
                    });
                }

                CurrentFactorItems = items;
                HasCurrentFactors = items.Count > 0;
            }
            catch
            {
                HasCurrentFactors = false;
            }
        }

        /// <summary>
        /// 从因子值 JSON 更新迷你面板因子显示（供 OnDOEProgressChanged 调用）
        /// </summary>
        private void UpdateCurrentFactorsFromJson(string factorValuesJson)
        {
            if (string.IsNullOrEmpty(factorValuesJson))
            {
                HasCurrentFactors = false;
                return;
            }

            try
            {
                var factors = JsonConvert.DeserializeObject<Dictionary<string, object>>(factorValuesJson);
                if (factors == null || factors.Count == 0)
                {
                    HasCurrentFactors = false;
                    return;
                }

                var items = new ObservableCollection<FactorDisplayItem>();
                foreach (var kv in factors)
                {
                    string unit = "";  // DOEFactor 无 Unit 属性，不猜测单位

                    string shortName = kv.Key
                        .Replace("Target", "")
                        .Replace("target", "");

                    string displayValue;
                    if (kv.Value is double d)
                        displayValue = d.ToString("F2");
                    else if (double.TryParse(kv.Value?.ToString(), out var parsed))
                        displayValue = parsed.ToString("F2");
                    else
                        displayValue = kv.Value?.ToString() ?? "—";

                    items.Add(new FactorDisplayItem
                    {
                        Label = shortName,
                        Value = displayValue,
                        Unit = unit
                    });
                }
                CurrentFactorItems = items;
                HasCurrentFactors = items.Count > 0;
            }
            catch
            {
                HasCurrentFactors = false;
            }
        }

        // ══════════════ 执行控制 ══════════════

        private async Task ContinueExecutionAsync()
        {
            try
            {
                // ── 继续执行确认 ──
                if (CurrentRun > 0)
                {
                    var confirm = _dialogService.ShowConfirmation(
                        $"当前已完成 {CurrentRun}/{TotalRuns} 组。\n将继续执行剩余 {PendingCount} 组实验。\n\n确定继续？",
                        "继续执行");
                    if (!confirm) return;
                }

                // ── 智能决策处理（仅项目批次） ──
                var batch = await _repository.GetBatchWithDetailsAsync(BatchId);
                bool isProjectBatch = batch != null && !string.IsNullOrEmpty(batch.ProjectId);

                if (isProjectBatch)
                {
                    var config = await _repository.GetOrCreateAutoPilotConfigAsync(batch!.ProjectId!);

                    if (!config.Enabled)
                    {
                        // 未启用 → 询问是否开启
                        var enableAP = _dialogService.ShowConfirmation(
                            "是否在本轮执行完成后启用智能决策？\n\n" +
                            "启用后系统将自动:\n" +
                            "  · 分析实验结果和模型质量\n" +
                            "  · 筛选显著/不显著因子\n" +
                            "  · 推荐下一轮设计方案\n\n" +
                            "点击「是」后将打开参数配置界面。",
                            "智能决策");

                        if (enableAP)
                        {
                            config.Enabled = true;
                            // 弹出配置界面
                            if (!ShowAutoPilotConfigDialog(config)) return; // 用户取消
                            await _repository.SaveAutoPilotConfigAsync(config);
                            _isAutoPilotEnabled = true;
                            _autoPilotEnabledForThisRun = true;
                            AutoPilotStatusText = _localizationService.GetString("Doe_Exec_Enabled", "已启用");
                            RaisePropertyChanged(nameof(IsAutoPilotEnabled));
                        }
                        else
                        {
                            _autoPilotEnabledForThisRun = false;
                        }
                    }
                    else
                    {
                        // 已启用 → 直接弹配置界面确认参数
                        if (!ShowAutoPilotConfigDialog(config)) return; // 用户取消
                        await _repository.SaveAutoPilotConfigAsync(config);
                        _autoPilotEnabledForThisRun = true;
                    }
                }

                // ── 恢复 Suspended 的 runs ──
                if (batch != null)
                {
                    var suspendedRuns = batch.Runs.Where(r => r.Status == DOERunStatus.Suspended).ToList();
                    foreach (var run in suspendedRuns)
                    {
                        run.Status = DOERunStatus.Pending;
                        await _repository.UpdateRunAsync(run);
                    }
                    if (suspendedRuns.Count > 0)
                        _logger.LogInformation("恢复 {Count} 组 Suspended 为 Pending", suspendedRuns.Count);
                }

                // ── 启动执行 ──
                IsRunning = true;
                IsPaused = false;
                _firstRunStartTime = DateTime.Now;
                //StatusMessage = _autoPilotEnabledForThisRun
                //    ? $"正在执行... 剩余 {PendingCount} 组 [智能决策已启用]"
                //    : $"正在执行... 剩余 {PendingCount} 组";

                if (_autoPilotEnabledForThisRun)
                {
                    SetStatusMessageLocalizationTxt(
                        "Doe_Exec_StateMsg_ExecReamindeci",
                        "正在执行... 剩余 {0} 组 [智能决策已启用]",
                        PendingCount);
                }
                else
                {
                    SetStatusMessageLocalizationTxt(
                        "Doe_Exec_StateMsg_ExecRemain",
                        "正在执行... 剩余 {0} 组",
                        PendingCount);
                }

                IsMiniMode = true;

                _executionService.RunCompleted += OnRunCompleted;
                _executionService.ProgressChanged += OnProgressChanged;
                _executionService.BatchCompleted += OnBatchCompleted;
                _executionService.PauseConfirmed += OnPauseConfirmed;

                await _executionService.StartBatchAsync(BatchId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "启动执行失败");
                _dialogService.ShowError($"启动失败: {ex.Message}", "错误");
                IsRunning = false;
                IsMiniMode = false;
            }
        }


        private void OnPauseConfirmed(object? sender, EventArgs e)
        {
            Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                IsPausing = false;
                IsPaused = true;
                //StatusMessage = "已暂停。点击「恢复」继续执行。";

                SetStatusMessageLocalizationTxt("Doe_Exec_StateMsg_AlreadyPause", "已暂停。点击「恢复」继续执行。");
            });
        }

        private async Task RetryFailedAsync()
        {
            try
            {
                var confirm = _dialogService.ShowConfirmation(
                    $"将重新执行 {FailedCount} 组失败的实验。\n失败组的状态将重置为「待执行」。\n\n确定重试？",
                    "重试失败组");
                if (!confirm) return;

                IsRunning = true;
                IsPaused = false;
                _firstRunStartTime = DateTime.Now;
                //StatusMessage = $"";

                SetStatusMessageLocalizationTxt("Doe_Exec_StateMsg_ResetFailGroup", "正在重置失败组...");

                var batch = await _repository.GetBatchWithDetailsAsync(BatchId);
                if (batch == null) return;

                var failedRuns = batch.Runs.Where(r => r.Status == DOERunStatus.Failed).ToList();
                foreach (var run in failedRuns)
                {
                    run.Status = DOERunStatus.Pending;
                    run.ResponseValuesJson = null;
                    run.StartTime = null;
                    run.EndTime = null;
                    await _repository.UpdateRunAsync(run);
                }

                await LoadBatchAsync(BatchId);

                //StatusMessage = $"";
                SetStatusMessageLocalizationTxt("Doe_Exec_StateMsg_AlreadyReset", "已重置 {0} 组，正在执行...", failedRuns.Count);

                //  切换到迷你模式
                IsMiniMode = true;

                _executionService.RunCompleted += OnRunCompleted;
                _executionService.ProgressChanged += OnProgressChanged;
                _executionService.BatchCompleted += OnBatchCompleted;
                _executionService.PauseConfirmed += OnPauseConfirmed;

                await _executionService.StartBatchAsync(BatchId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "重试失败组出错");
                _dialogService.ShowError($"重试失败: {ex.Message}", "错误");
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
                //StatusMessage = "";

                SetStatusMessageLocalizationTxt("Doe_Exec_StateMsg_Termination", "批次已终止");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "终止执行失败");
            }
        }

        // ══════════════ 事件处理 ══════════════

        private void SubscribeEvents()
        {
            _eventAggregator.GetEvent<DOERunCompletedEvent>().Subscribe(OnDOERunNeedsResponse, ThreadOption.UIThread);
            _eventAggregator.GetEvent<DOEProgressChangedEvent>().Subscribe(OnDOEProgressChanged, ThreadOption.UIThread);
            _eventAggregator.GetEvent<LanguageChangedEvent>().Subscribe(OnLanguageChangedEvent);
        }

        private void OnDOERunNeedsResponse(DOERunCompletedPayload payload)
        {
            if (payload.BatchId != BatchId) return;

            try
            {
                var factorValues = JsonConvert.DeserializeObject<Dictionary<string, object>>(payload.FactorValuesJson)
                                   ?? new Dictionary<string, object>();

                var responseDefs = _responses.Select(r => (r.ResponseName, r.Unit)).ToList();

                var dialog = new DOEResponseCollectionDialog(
                    payload.RunIndex, TotalRuns, factorValues, responseDefs);
                dialog.Owner = Application.Current.MainWindow;

                var result = dialog.ShowDialog();

                if (result == true && dialog.CollectedValues != null)
                    _executionService.SubmitRunResponse(payload.RunIndex, dialog.CollectedValues);
                else
                    _executionService.SubmitRunResponse(payload.RunIndex, new Dictionary<string, double>());
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
            StatusMessage = payload.StatusMessage;

            // ★ 新增: 如果携带了因子值，更新 mini 面板因子显示
            if (!string.IsNullOrEmpty(payload.CurrentFactorValuesJson))
            {
                UpdateCurrentFactorsFromJson(payload.CurrentFactorValuesJson);
            }
        }

        private void OnRunCompleted(object? sender, DOERunCompletedEventArgs e)
        {
            Application.Current?.Dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    var run = await _repository.GetRunAsync(BatchId, e.RunIndex);
                    if (run != null)
                    {
                        UpdateTableRow(run);
                        UpdateBestResult();
                        RecalculateProgress();

                        //  更新迷你面板的因子参数显示
                        UpdateCurrentFactors(run);
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
            Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                StatusMessage = e.StatusMessage;
            });
        }

        private void OnBatchCompleted(object? sender, DOEBatchCompletedEventArgs e)
        {
            Application.Current?.Dispatcher.InvokeAsync(async () =>
            {
                IsRunning = false;
                IsPaused = false;
                IsPausing = false;
                BatchStatus = e.FinalStatus;
                //StatusMessage = $"";

                SetStatusMessageLocalizationTxt(
                    "Doe_Exec_StateMsg_ExecComplete",
                    "执行完成 ({0}/{1} 组)",
                    e.CompletedRuns, e.TotalRuns);

                IsMiniMode = false;

                _executionService.RunCompleted -= OnRunCompleted;
                _executionService.ProgressChanged -= OnProgressChanged;
                _executionService.BatchCompleted -= OnBatchCompleted;
                _executionService.PauseConfirmed -= OnPauseConfirmed;

                RecalculateProgress();

                if (e.FinalStatus == DOEBatchStatus.Completed && _autoPilotEnabledForThisRun)
                {
                    // 智能决策已启用 → 直接触发分析，不弹多余提示
                    await TriggerAutoPilotAsync();
                }
                // 未启用或非正常完成 → 什么都不弹，状态栏已显示结果
            });
        }
        /// <summary>
        /// 弹出 AutoPilot 配置对话框，返回用户是否确认
        /// </summary>
        private bool ShowAutoPilotConfigDialog(AutoPilotConfig config)
        {
            var dialog = new AutoPilotConfigDialog(config)
            {
                Owner = Application.Current.MainWindow
            };
            dialog.ShowDialog();

            if (dialog.Confirmed)
            {
                // 用户确认，把修改后的值写回 config
                var updated = dialog.Config;
                config.AlphaScreen = updated.AlphaScreen;
                config.R2Min = updated.R2Min;
                config.R2Good = updated.R2Good;
                config.AlphaLOF = updated.AlphaLOF;
                config.R2ImprovementMin = updated.R2ImprovementMin;
                config.DTarget = updated.DTarget;
                config.ConfirmationReps = updated.ConfirmationReps;
                config.MaxRounds = updated.MaxRounds;
                config.MaxTotalRuns = updated.MaxTotalRuns;
                config.RangeExpansionRatio = updated.RangeExpansionRatio;
                config.MaxConsecutiveSameAction = updated.MaxConsecutiveSameAction;
                config.Enabled = updated.Enabled;
                return true;
            }
            return false;
        }
        /// <summary>
        /// 批次完成后触发 AutoPilot 决策
        /// 先查该批次是否关联项目，有项目则弹出决策报告
        /// </summary>
        private async Task TriggerAutoPilotAsync()
        {
            try
            {
                var batch = await _repository.GetBatchWithDetailsAsync(BatchId);
                if (batch == null || string.IsNullOrEmpty(batch.ProjectId)) return;

                //StatusMessage = "";

                SetStatusMessageLocalizationTxt("Doe_Exec_StateMsg_DecisionResult", "智能决策: 正在分析实验结果...");

                var report = await _autoPilotService.OnRoundCompletedAsync(
                    batch.ProjectId, BatchId);

                if (!report.Success)
                {
                    //StatusMessage = $"";
                    SetStatusMessageLocalizationTxt("Doe_Exec_StateMsg_DecisionFail", "智能决策分析失败: {0}", report.Error ?? "");
                    return;
                }

                // 只弹决策报告，用户在里面操作
                ShowAutoPilotReport(report);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "智能决策触发失败");
                //StatusMessage = $"";
                SetStatusMessageLocalizationTxt("Doe_Exec_StateMsg_DecisionException", "智能决策异常: {0}",ex.Message);
            }
        }


        /// <summary>
        /// 弹出 AutoPilot 决策报告弹窗，处理用户选择
        /// </summary>
        private void ShowAutoPilotReport(AutoPilotReport report)
        {
            var dialog = new AutoPilotReportDialog(report)
            {
                Owner = Application.Current.MainWindow
            };
            dialog.ShowDialog();

            switch (dialog.SelectedAction)
            {
                case AutoPilotReportDialog.UserAction.Accept:
                    _ = AcceptAndLoadNextRoundAsync(report);
                    break;

                case AutoPilotReportDialog.UserAction.Manual:
                    //StatusMessage = "";
                    SetStatusMessageLocalizationTxt("Doe_Exec_StateMsg_HandDecision", "手动决策模式，请在设计向导中创建下一轮。");
                    break;

                case AutoPilotReportDialog.UserAction.Skip:
                case AutoPilotReportDialog.UserAction.None:
                    //StatusMessage = "";
                    SetStatusMessageLocalizationTxt("Doe_Exec_StateMsg_JumpCunrrent", "已跳过本轮智能决策。");
                    break;
            }
        }

        /// <summary>
        /// 用户点击"采纳推荐"后执行，创建下一轮并加载到执行面板
        /// </summary>
        private async Task AcceptAndLoadNextRoundAsync(AutoPilotReport report)
        {
            try
            {
                //StatusMessage = "";
                SetStatusMessageLocalizationTxt("Doe_Exec_StateMsg_CreateRound", "正在创建下一轮实验...");

                var nextBatchId = await _autoPilotService.AcceptRecommendationAsync(
                    report.ProjectId, report.BatchId);

                if (!string.IsNullOrEmpty(nextBatchId))
                {
                    await LoadBatchAsync(nextBatchId);
                    //StatusMessage = $"";
                    SetStatusMessageLocalizationTxt("Doe_Exec_StateMsg_NextReady", "下一轮已就绪: {0}, {1} 组实验", report.NextDesignMethodDisplay, report.EstimatedRuns);
                }
                else
                {
                    //StatusMessage = "";
                    SetStatusMessageLocalizationTxt("Doe_Exec_StateMsg_CreateFailHand", "创建下一轮失败，请手动操作。");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "创建下一轮失败");
                //StatusMessage = $"";
                SetStatusMessageLocalizationTxt("Doe_Exec_StateMsg_CreateFail", "创建下一轮失败: {0}",ex.Message);
            }
        }
        // ══════════════ 表格行更新 ══════════════

        private void UpdateTableRow(DOERunRecord run)
        {
            foreach (DataRow row in MatrixTable.Rows)
            {
                if (Convert.ToInt32(row["_RunIndex"]) == run.RunIndex)
                {
                    var responses = !string.IsNullOrEmpty(run.ResponseValuesJson)
                        ? JsonConvert.DeserializeObject<Dictionary<string, double>>(run.ResponseValuesJson) ?? new()
                        : new Dictionary<string, double>();
                    foreach (var name in _responseNames)
                        row[name] = responses.TryGetValue(name, out var rv) ? rv.ToString("F2") : "—";

                    row["状态"] = run.Status switch
                    {
                        DOERunStatus.Completed => "已完成",
                        DOERunStatus.Failed => "失败",
                        DOERunStatus.Running => "执行中",
                        DOERunStatus.WaitingResponse => "等待输入",
                        DOERunStatus.Suspended => "已暂停",
                        _ => run.Status.ToString()
                    };

                    row["耗时"] = run.StartTime.HasValue && run.EndTime.HasValue
                        ? (run.EndTime.Value - run.StartTime.Value).ToString(@"mm\:ss")
                        : "—";

                    break;
                }
            }
        }

        private void RecalculateProgress()
        {
            int completed = 0, pending = 0, failed = 0;
            foreach (DataRow row in MatrixTable.Rows)
            {
                var status = row["状态"]?.ToString();
                if (status == "已完成") completed++;
                else if (status == "待执行" || status == "已暂停") pending++;
                else if (status == "失败") failed++;
            }

            // ★ 修复: 已执行 = 完成 + 失败，不管成功失败都算执行过
            var executed = completed + failed;
            CurrentRun = executed;
            PendingCount = pending;
            FailedCount = failed;
            ProgressPercent = TotalRuns > 0 ? (double)executed / TotalRuns * 100 : 0;
        }


        #region 本地化

        private string _maskMsgTxt;
        private string _maskTipTxt;
        private string _retryFailTxt;
        private string _pauseTxt;
        private string _resumeTxt;
        private string _stopTxt;
        private string _execProgressTxt;
        private string _groupTxt;
        private string _currOptimizTxt;
        private string _remainTxt;
        private string _failTxt;
        private string _matrixTxt;
        private string _groupPeriTxt;
        private string _executingTxt;
        private string _alreadyPauseTxt;
        private string _decisionTxt;

        public string MaskMsgTxt { get { return _maskMsgTxt; } set { SetProperty(ref _maskMsgTxt, value); } }
        public string MaskTipTxt { get { return _maskTipTxt; } set { SetProperty(ref _maskTipTxt, value); }  }
        public string RetryFailTxt { get { return _retryFailTxt; } set { SetProperty(ref _retryFailTxt, value); } }
        public string PauseTxt { get { return _pauseTxt; } set { SetProperty(ref _pauseTxt, value); } }
        public string ResumeTxt { get { return _resumeTxt; } set { SetProperty(ref _resumeTxt, value); } }
        public string StopTxt { get { return _stopTxt; } set { SetProperty(ref _stopTxt, value); } }
        public string ExecProgressTxt { get { return _execProgressTxt; } set { SetProperty(ref _execProgressTxt, value); } }
        public string GroupTxt { get { return _groupTxt; } set { SetProperty(ref _groupTxt, value); } }
        public string CurrOptimizTxt { get { return _currOptimizTxt; } set { SetProperty(ref _currOptimizTxt, value); } }
        public string RemainTxt { get { return _remainTxt; } set { SetProperty(ref _remainTxt, value); } }
        public string FailTxt { get { return _failTxt; } set { SetProperty(ref _failTxt, value); } }
        public string MatrixTxt { get { return _matrixTxt; } set { SetProperty(ref _matrixTxt, value); } }
        public string GroupPeriTxt { get { return _groupPeriTxt; } set { SetProperty(ref _groupPeriTxt, value); } }
        public string ExectuingTxt { get { return _executingTxt; } set { SetProperty(ref _executingTxt, value); } }
        public string AlreadyPauseTxt { get { return _alreadyPauseTxt; } set { SetProperty(ref _alreadyPauseTxt, value); } }
        public string DecisionTxt { get { return _decisionTxt; } set { SetProperty(ref _decisionTxt, value); } }


        private void OnLanguageChangedEvent(string language)
        {
            RaisePropertyChanged(nameof(StartButtonText));
            StatusMessage = string.Format(_localizationService.GetString(_statusMsgKey, _statusMsgDefaul), _statusMsgObjs);
            BestValueText = _localizationService.GetString(_bestValTxtKey, _bestValTxtDefault);

            MaskMsgTxt = _localizationService.GetString("Deo_Exec_Mask_Msg", "正在等待当前实验组完成...");
            MaskTipTxt = _localizationService.GetString("Doe_Exec_Mask_Tip", "当前组执行完毕后将自动暂停");
            RetryFailTxt = _localizationService.GetString("Doe_Exec_RetryFail", "重试失败");
            PauseTxt = _localizationService.GetString("Doe_Exec_Pause", "暂停");
            ResumeTxt = _localizationService.GetString("Doe_Exec_Resume", "恢复");
            StopTxt = _localizationService.GetString("Doe_Exec_Terminate", "终止");
            ExecProgressTxt = _localizationService.GetString("Doe_Exec_ExecProgress", "执行进度");
            GroupTxt = _localizationService.GetString("Doe_Exec_Group", "组");
            CurrOptimizTxt = _localizationService.GetString("Doe_Exec_CurrentOptimiz", "当前最优");
            RemainTxt = _localizationService.GetString("Doe_Exec_Remain", "预计剩余");
            FailTxt = _localizationService.GetString("Doe_Exec_Fail", "失败");
            MatrixTxt = _localizationService.GetString("Doe_Exec_MatrixTitle", "实验矩阵");
            GroupPeriTxt = _localizationService.GetString("Doe_Exec_GroupExperiment", "组实验");
            ExectuingTxt = _localizationService.GetString("Doe_Exec_Executing", "执行中");
            AlreadyPauseTxt = _localizationService.GetString("Doe_Exec_AlredyPause", "已暂停");
            DecisionTxt = _localizationService.GetString("Doe_Exec_Decision", "智能决策");
        }

        private void SetStatusMessageLocalizationTxt(string key, string defaultTxt, params object[] args)
        {
            _statusMsgKey = key;
            _statusMsgDefaul = defaultTxt;
            _statusMsgObjs = args;
            StatusMessage = string.Format(_localizationService.GetString(key, defaultTxt), args);
        }

        #endregion
    }
}
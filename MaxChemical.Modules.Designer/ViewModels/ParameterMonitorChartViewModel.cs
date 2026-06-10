// ParameterMonitorChartViewModel.cs - 多Y轴版(取代归一化)
using MaxChemical.Infrastructure.Events;
using MaxChemical.Infrastructure.Services;
using MaxChemical.Logging;
using MaxChemical.Modules.Designer.Event;
using MaxChemical.Modules.Designer.Models;
using MaxChemical.Modules.Designer.Service.Execution;
using MaxChemical.Modules.Designer.Services;
using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Series;
using Prism.Commands;
using Prism.Events;
using Prism.Ioc;
using Prism.Mvvm;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace MaxChemical.Modules.Designer.ViewModels
{
    public class ParameterMonitorChartViewModel : BindableBase, IDisposable
    {
        #region 私有字段

        private readonly IParameterMonitoringService _monitoringService;
        private readonly IEventAggregator _eventAggregator;
        private readonly ILogService _logger;
        private readonly ILocalizationService _localizationService;

        private DispatcherTimer _refreshTimer;
        private DateTime _baseTime = DateTime.Now;
        private DateTime _lastChartUpdateTime = DateTime.MinValue;

        private bool _hasData;
        private int _dataPointsCount;
        private bool _isProcessRunning;
        private bool _shouldUpdateChart;
        private Func<double, string> _dateTimeFormatter;
        private DateTime _lastUpdateTime = DateTime.Now;
        private string _searchText = string.Empty;
        private bool _isConfigurationVisible = true;
        private bool _isRealTimeMode = true;
        private AvailableParameter _selectedAvailableParameter;
        private MonitoredParameter _selectedMonitoredParameter;
        private PlotModel _normalizedChartModel;
        private bool _hasAlarm;
        private string _alarmMessage = "";
        private DateTime _alarmTime;

        // ★ 新增:缓存最近一次构建的 SeriesInput,供"重置量程"使用
        private List<MultiAxisChartBuilder.SeriesInput> _lastSeriesInputs
            = new List<MultiAxisChartBuilder.SeriesInput>();

        // ★ 新增:未分类参数提示
        private string _unclassifiedTip = "";
        private bool _hasUnclassifiedParameters;

        #endregion

        #region 公共属性

        /// <summary>
        /// 图表数据点集合(供Canvas渲染器使用)
        /// </summary>
        public ObservableCollection<ChartDataPoint> ChartData { get; }

        /// <summary>
        /// 当前监控的参数列表
        /// </summary>
        public ObservableCollection<MonitoredParameter> MonitoredParameters { get; }

        /// <summary>
        /// 可用参数列表
        /// </summary>
        public ObservableCollection<AvailableParameter> AvailableParameters { get; }

        /// <summary>
        /// 是否有数据
        /// </summary>
        public bool HasData
        {
            get => _hasData;
            set => SetProperty(ref _hasData, value);
        }

        /// <summary>
        /// 数据点数量
        /// </summary>
        public int DataPointsCount
        {
            get => _dataPointsCount;
            set => SetProperty(ref _dataPointsCount, value);
        }

        /// <summary>
        /// 流程是否正在运行
        /// </summary>
        public bool IsProcessRunning
        {
            get => _isProcessRunning;
            set
            {
                if (SetProperty(ref _isProcessRunning, value))
                {
                    _logger.LogInformation("流程运行状态变更: {IsRunning}", value);
                    _shouldUpdateChart = value;
                    RaisePropertyChanged(nameof(ShouldUpdateChart));

                    if (value)
                    {
                        StartChartAutoUpdate();
                    }
                    else
                    {
                        StopChartAutoUpdate();
                    }
                }
            }
        }

        /// <summary>
        /// 是否应该更新图表
        /// </summary>
        public bool ShouldUpdateChart
        {
            get => _shouldUpdateChart;
            set
            {
                if (SetProperty(ref _shouldUpdateChart, value))
                {
                    _logger.LogInformation("图表更新标志变更: {ShouldUpdate}", value);

                    if (value && IsProcessRunning && IsRealTimeMode)
                    {
                        StartChartAutoUpdate();
                    }
                    else if (!value)
                    {
                        StopChartAutoUpdate();
                    }
                }
            }
        }

        /// <summary>
        /// 时间格式化器(用于X轴显示)
        /// </summary>
        public Func<double, string> DateTimeFormatter
        {
            get => _dateTimeFormatter;
            set => SetProperty(ref _dateTimeFormatter, value);
        }

        /// <summary>
        /// 最后更新时间
        /// </summary>
        public DateTime LastUpdateTime
        {
            get => _lastUpdateTime;
            set => SetProperty(ref _lastUpdateTime, value);
        }

        /// <summary>
        /// 搜索文本
        /// </summary>
        public string SearchText
        {
            get => _searchText;
            set
            {
                if (SetProperty(ref _searchText, value))
                {
                    FilterAvailableParameters();
                }
            }
        }

        /// <summary>
        /// 配置面板是否可见
        /// </summary>
        public bool IsConfigurationVisible
        {
            get => _isConfigurationVisible;
            set => SetProperty(ref _isConfigurationVisible, value);
        }

        /// <summary>
        /// 是否为实时模式
        /// </summary>
        public bool IsRealTimeMode
        {
            get => _isRealTimeMode;
            set
            {
                if (SetProperty(ref _isRealTimeMode, value))
                {
                    if (value && ShouldUpdateChart && IsProcessRunning)
                        _refreshTimer?.Start();
                    else
                        _refreshTimer?.Stop();
                }
            }
        }

        /// <summary>
        /// 选中的可用参数
        /// </summary>
        public AvailableParameter SelectedAvailableParameter
        {
            get => _selectedAvailableParameter;
            set => SetProperty(ref _selectedAvailableParameter, value);
        }

        /// <summary>
        /// 选中的监控参数
        /// </summary>
        public MonitoredParameter SelectedMonitoredParameter
        {
            get => _selectedMonitoredParameter;
            set => SetProperty(ref _selectedMonitoredParameter, value);
        }

        /// <summary>
        /// 状态文本
        /// </summary>
        public string StatusText => $"数据点: {DataPointsCount} | 最后更新: {LastUpdateTime:HH:mm:ss}";

        /// <summary>
        /// 图表模型(OxyPlot, 多 Y 轴)
        /// 注:属性名保留 NormalizedChartModel 以减少 XAML 改动,内部已改为多轴构建
        /// </summary>
        public PlotModel NormalizedChartModel
        {
            get => _normalizedChartModel;
            set => SetProperty(ref _normalizedChartModel, value);
        }

        /// <summary>
        /// 图表图例项
        /// </summary>
        public ObservableCollection<ChartLegendItem> ChartLegendItems { get; }
            = new ObservableCollection<ChartLegendItem>();

        /// <summary>
        /// 是否有报警
        /// </summary>
        public bool HasAlarm
        {
            get => _hasAlarm;
            set => SetProperty(ref _hasAlarm, value);
        }

        /// <summary>
        /// 报警消息
        /// </summary>
        public string AlarmMessage
        {
            get => _alarmMessage;
            set => SetProperty(ref _alarmMessage, value);
        }

        /// <summary>
        /// 报警时间
        /// </summary>
        public DateTime AlarmTime
        {
            get => _alarmTime;
            set => SetProperty(ref _alarmTime, value);
        }

        /// <summary>
        /// 单位未分类提示文本
        /// </summary>
        public string UnclassifiedTip
        {
            get => _unclassifiedTip;
            set => SetProperty(ref _unclassifiedTip, value);
        }

        /// <summary>
        /// 是否存在单位未分类的参数(控制顶部黄色提示条显示)
        /// </summary>
        public bool HasUnclassifiedParameters
        {
            get => _hasUnclassifiedParameters;
            set => SetProperty(ref _hasUnclassifiedParameters, value);
        }

        #endregion

        #region 命令

        public DelegateCommand<AvailableParameter> AddParameterCommand { get; private set; }
        public DelegateCommand<MonitoredParameter> RemoveParameterCommand { get; private set; }
        public DelegateCommand ClearDataCommand { get; private set; }
        public DelegateCommand ToggleConfigurationCommand { get; private set; }
        public DelegateCommand<MonitoredParameter> ToggleParameterCommand { get; private set; }
        public DelegateCommand RefreshAvailableParametersCommand { get; private set; }

        // ★ 新增:重置量程命令
        public DelegateCommand ResetAxisRangesCommand { get; private set; }

        #endregion

        #region 构造函数

        public ParameterMonitorChartViewModel(
            IParameterMonitoringService monitoringService,
            IEventAggregator eventAggregator)
        {
            _monitoringService = monitoringService ?? throw new ArgumentNullException(nameof(monitoringService));
            _eventAggregator = eventAggregator ?? throw new ArgumentNullException(nameof(eventAggregator));
            _logger = new LogService().ForContext<ParameterMonitorChartViewModel>();
            _localizationService = ContainerLocator.Container.Resolve<ILocalizationService>() ?? throw new NullReferenceException();

            AvailableParameters = new ObservableCollection<AvailableParameter>();
            MonitoredParameters = new ObservableCollection<MonitoredParameter>();
            ChartData = new ObservableCollection<ChartDataPoint>();

            _baseTime = DateTime.Now;

            InitializeCommands();
            InitializeTimer();
            InitializeDateTimeFormatter();
            SubscribeToFlowEvents();
            InitializeAsync();

            // ★ 改用多轴 Builder 创建空白模型
            NormalizedChartModel = MultiAxisChartBuilder.CreateEmptyModel();

            _logger.LogInformation("参数监控图表ViewModel已初始化,基准时间: {BaseTime}", _baseTime);

            _eventAggregator.GetEvent<LanguageChangedEvent>()?.Subscribe(LanguageChangedHandler);
            LanguageChangedHandler("");
        }

        #endregion

        #region 初始化方法

        private void InitializeCommands()
        {
            AddParameterCommand = new DelegateCommand<AvailableParameter>(AddParameter, CanAddParameter);
            RemoveParameterCommand = new DelegateCommand<MonitoredParameter>(RemoveParameter, CanRemoveParameter);
            ClearDataCommand = new DelegateCommand(ClearData);
            ToggleConfigurationCommand = new DelegateCommand(ToggleConfiguration);
            ToggleParameterCommand = new DelegateCommand<MonitoredParameter>(ToggleParameter);
            RefreshAvailableParametersCommand = new DelegateCommand(RefreshAvailableParameters);

            // ★ 新增:重置量程
            ResetAxisRangesCommand = new DelegateCommand(ResetAxisRanges);
        }

        private void InitializeTimer()
        {
            _refreshTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(200)
            };
            _refreshTimer.Tick += OnTimerTick;
        }

        private void InitializeDateTimeFormatter()
        {
            DateTimeFormatter = value =>
            {
                try
                {
                    var targetTime = _baseTime.AddSeconds(value);
                    var timeSpan = DateTime.Now - _baseTime;

                    if (timeSpan.TotalMinutes < 1)
                        return targetTime.ToString("HH:mm:ss.fff");
                    else if (timeSpan.TotalMinutes < 10)
                        return targetTime.ToString("mm:ss.fff");
                    else if (timeSpan.TotalHours < 1)
                        return targetTime.ToString("mm:ss");
                    else
                        return targetTime.ToString("HH:mm:ss");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "时间格式化失败: {Value}", value);
                    return value.ToString("F3") + "s";
                }
            };
        }

        private void SubscribeToFlowEvents()
        {
            try
            {
                _eventAggregator?.GetEvent<FlowExecutionStateChangedEvent>()
                    ?.Subscribe(OnFlowStateChanged, ThreadOption.UIThread);
                _logger.LogDebug("已订阅流程状态变更事件");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "订阅流程状态事件失败");
            }
        }

        private async void InitializeAsync()
        {
            try
            {
                await System.Threading.Tasks.Task.Delay(100);

                _monitoringService.ParameterDataUpdated += OnParameterDataUpdated;
                _logger.LogDebug("已订阅参数数据更新事件");

                LoadAvailableParameters();
                LoadMonitoredParameters();
                await RestoreMonitoringStateAsync();

                _logger.LogInformation("异步初始化完成");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "异步初始化失败");
            }
        }

        #endregion

        #region 流程状态处理

        private void OnFlowStateChanged(FlowExecutionStateChangedEventArgs args)
        {
            try
            {
                _logger.LogInformation("收到流程状态变更: {OldState} -> {NewState}",
                    args.OldState, args.NewState);

                switch (args.NewState)
                {
                    case FlowExecutionState.Running:
                        OnFlowStarted();
                        break;

                    case FlowExecutionState.Completed:
                    case FlowExecutionState.Stopped:
                    case FlowExecutionState.Error:
                    case FlowExecutionState.Idle:
                        OnFlowStopped();
                        break;

                    case FlowExecutionState.Paused:
                        OnFlowPaused();
                        break;

                    case FlowExecutionState.Resuming:
                        OnFlowResumed();
                        break;
                }

                _logger.LogInformation("状态处理完成: IsProcessRunning={IsRunning}, ShouldUpdateChart={ShouldUpdate}",
                    IsProcessRunning, ShouldUpdateChart);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "处理流程状态变更失败");
            }
        }

        private void OnFlowStarted()
        {
            _logger.LogInformation("流程开始,启动图表监控");

            _isProcessRunning = true;
            _shouldUpdateChart = true;
            RaisePropertyChanged(nameof(IsProcessRunning));
            RaisePropertyChanged(nameof(ShouldUpdateChart));

            if (!MonitoredParameters.Any() || !ChartData.Any())
            {
                _baseTime = DateTime.Now;
                _lastChartUpdateTime = DateTime.MinValue;
                ClearChartDataOnly();
            }
            else
            {
                ResetIncrementalUpdateTimestamp();
                _logger.LogInformation("保留现有数据,重置增量更新时间戳");
            }

            StartChartAutoUpdate();
            _logger.LogInformation("图表监控已启动");
        }

        private void OnFlowStopped()
        {
            _logger.LogInformation("流程停止,停止图表监控");

            try
            {
                if (ShouldUpdateChart)
                {
                    UpdateChartData();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "流程停止时最终更新失败");
            }

            _isProcessRunning = false;
            _shouldUpdateChart = false;
            RaisePropertyChanged(nameof(IsProcessRunning));
            RaisePropertyChanged(nameof(ShouldUpdateChart));

            StopChartAutoUpdate();
        }

        private void OnFlowPaused()
        {
            _logger.LogInformation("流程暂停,暂停图表更新");
            ShouldUpdateChart = false;
        }

        private void OnFlowResumed()
        {
            _logger.LogInformation("流程恢复,恢复图表更新");
            ShouldUpdateChart = true;
        }

        #endregion

        #region 定时器与数据更新

        private void OnTimerTick(object sender, EventArgs e)
        {
            if (!ShouldUpdateChart || !IsProcessRunning)
            {
                return;
            }

            UpdateChartData();
        }

        private void UpdateChartData()
        {
            if (!ShouldUpdateChart) return;

            try
            {
                var enabledParameters = MonitoredParameters.Where(p => p.IsEnabled).ToList();

                if (!enabledParameters.Any())
                {
                    ClearChartDataOnly();
                    return;
                }

                var sinceTime = _lastChartUpdateTime == DateTime.MinValue
                    ? (ChartData.Any() ? ChartData.Max(cd => cd.Timestamp) : DateTime.Now.AddMinutes(-5))
                    : _lastChartUpdateTime;

                var newDataPoints = GetNewDataPointsSince(sinceTime);

                if (!newDataPoints.Any())
                {
                    return;
                }

                _logger.LogDebug("发现 {Count} 个新数据点", newDataPoints.Count);

                foreach (var newPoint in newDataPoints)
                {
                    ChartData.Add(newPoint);
                }

                DataPointsCount = ChartData.Count;
                LastUpdateTime = DateTime.Now;
                HasData = ChartData.Any();

                if (newDataPoints.Any())
                {
                    _lastChartUpdateTime = newDataPoints.Max(p => p.Timestamp);
                }

                LimitChartDataPoints();

                RaisePropertyChanged(nameof(StatusText));
                RebuildChartModel();
                _logger.LogDebug("增量更新完成,当前数据点: {Count}", DataPointsCount);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "更新图表数据失败");
            }
        }

        private List<ChartDataPoint> GetNewDataPointsSince(DateTime sinceTime)
        {
            var newPoints = new List<ChartDataPoint>();
            var enabledParameters = MonitoredParameters.Where(p => p.IsEnabled).ToList();

            try
            {
                var allNewData = new Dictionary<string, List<ParameterDataPoint>>();

                foreach (var param in enabledParameters)
                {
                    var data = _monitoringService.GetParameterData(
                        param.DeviceId, param.CommandName, param.ParameterName);

                    var newData = data.Where(d => d.Timestamp > sinceTime)
                                     .OrderBy(d => d.Timestamp)
                                     .ToList();

                    if (newData.Any())
                    {
                        allNewData[param.Key] = newData;
                    }
                }

                if (!allNewData.Any())
                {
                    return newPoints;
                }

                var newTimestamps = allNewData.Values
                    .SelectMany(data => data.Select(d => d.Timestamp))
                    .Distinct()
                    .OrderBy(t => t)
                    .ToList();

                foreach (var timestamp in newTimestamps)
                {
                    var chartPoint = new ChartDataPoint { Timestamp = timestamp };
                    bool hasAnyValue = false;

                    foreach (var param in enabledParameters)
                    {
                        if (allNewData.TryGetValue(param.Key, out var paramData))
                        {
                            var dataPoint = paramData
                                .Where(d => Math.Abs((d.Timestamp - timestamp).TotalMilliseconds) < 100)
                                .FirstOrDefault();

                            if (dataPoint != null)
                            {
                                chartPoint.ParameterValues[param.DisplayName] = dataPoint.Value;
                                hasAnyValue = true;
                            }
                        }
                    }

                    if (hasAnyValue)
                    {
                        newPoints.Add(chartPoint);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取新数据点失败");
            }

            return newPoints;
        }

        private void LimitChartDataPoints()
        {
            const int maxPoints = 1000;

            if (ChartData.Count > maxPoints)
            {
                var removeCount = ChartData.Count - maxPoints;

                for (int i = 0; i < removeCount; i++)
                {
                    ChartData.RemoveAt(0);
                }

                _logger.LogDebug("移除了 {Count} 个旧数据点", removeCount);
            }
        }

        #endregion

        #region 参数管理

        private void AddParameter(AvailableParameter parameter)
        {
            if (parameter == null) return;

            try
            {
                _monitoringService.AddMonitoredParameter(
                    parameter.DeviceId,
                    parameter.DeviceName,
                    parameter.CommandName,
                    parameter.ParameterName,
                    parameter.DisplayName,
                    parameter.Unit);                  // ★ 这是新增的一行,把单位也传过去

                LoadMonitoredParameters();
                CheckAndUpdateProcessStatus();

                if (IsProcessRunning && ShouldUpdateChart)
                {
                    BackfillDataForNewParameter(parameter);
                }

                AddParameterCommand.RaiseCanExecuteChanged();
                _logger.LogInformation("添加监控参数: {DisplayName}, 单位: {Unit}",
                    parameter.DisplayName, parameter.Unit);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "添加监控参数失败: {DisplayName}", parameter.DisplayName);
            }
        }

        private void RemoveParameter(MonitoredParameter parameter)
        {
            if (parameter == null) return;

            try
            {
                _monitoringService.RemoveMonitoredParameter(
                    parameter.DeviceId,
                    parameter.CommandName,
                    parameter.ParameterName);

                if (IsProcessRunning && ShouldUpdateChart)
                {
                    RemoveParameterFromChartData(parameter.DisplayName);
                }

                LoadMonitoredParameters();
                _logger.LogInformation("移除监控参数: {DisplayName}", parameter.DisplayName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "移除监控参数失败: {DisplayName}", parameter.DisplayName);
            }
        }

        private void ToggleParameter(MonitoredParameter parameter)
        {
            if (parameter == null) return;

            var oldEnabled = parameter.IsEnabled;
            parameter.IsEnabled = !parameter.IsEnabled;

            _monitoringService.SetParameterEnabled(
                parameter.DeviceId,
                parameter.CommandName,
                parameter.ParameterName,
                parameter.IsEnabled);

            if (IsProcessRunning && ShouldUpdateChart)
            {
                if (parameter.IsEnabled && !oldEnabled)
                {
                    var availableParam = new AvailableParameter
                    {
                        DeviceId = parameter.DeviceId,
                        DeviceName = parameter.DeviceName,
                        CommandName = parameter.CommandName,
                        ParameterName = parameter.ParameterName,
                        DisplayName = parameter.DisplayName,
                        Unit = parameter.Unit                       // ★ 这一行是新增的
                    };
                    BackfillDataForNewParameter(availableParam);
                }
            }

            _logger.LogDebug("切换参数状态: {DisplayName} = {Enabled}",
                parameter.DisplayName, parameter.IsEnabled);
        }

        private void BackfillDataForNewParameter(AvailableParameter parameter)
        {
            try
            {
                var parameterData = _monitoringService.GetParameterDataInRange(
                    parameter.DeviceId,
                    parameter.CommandName,
                    parameter.ParameterName);

                if (!parameterData.Any())
                {
                    return;
                }

                _logger.LogInformation("参数 {DisplayName} 回溯 {Count} 个数据点",
                    parameter.DisplayName, parameterData.Count);

                AdjustTimeRangeForBackfill(parameterData);
                MergeBackfillData(parameter.DisplayName, parameterData);

                DataPointsCount = ChartData.Count;
                HasData = ChartData.Any();

                ResetIncrementalUpdateTimestamp();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "参数数据回溯失败: {DisplayName}", parameter.DisplayName);
            }
        }

        private void AdjustTimeRangeForBackfill(List<ParameterDataPoint> backfillData)
        {
            if (!backfillData.Any()) return;

            var earliestBackfillTime = backfillData.Min(d => d.Timestamp);

            if (earliestBackfillTime < _baseTime)
            {
                _baseTime = earliestBackfillTime;
                _logger.LogInformation("调整基准时间为: {BaseTime}", _baseTime);
                InitializeDateTimeFormatter();
            }
        }

        private void MergeBackfillData(string parameterDisplayName, List<ParameterDataPoint> backfillData)
        {
            try
            {
                var tolerance = TimeSpan.FromMilliseconds(100);

                foreach (var dataPoint in backfillData.OrderBy(d => d.Timestamp))
                {
                    var existingPoint = ChartData.FirstOrDefault(cd =>
                        Math.Abs((cd.Timestamp - dataPoint.Timestamp).TotalMilliseconds) < tolerance.TotalMilliseconds);

                    if (existingPoint != null)
                    {
                        existingPoint.ParameterValues[parameterDisplayName] = dataPoint.Value;
                    }
                    else
                    {
                        var newPoint = new ChartDataPoint { Timestamp = dataPoint.Timestamp };
                        newPoint.ParameterValues[parameterDisplayName] = dataPoint.Value;
                        ChartData.Add(newPoint);
                    }
                }

                var sortedData = ChartData.OrderBy(cd => cd.Timestamp).ToList();
                ChartData.Clear();
                foreach (var point in sortedData)
                {
                    ChartData.Add(point);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "合并回溯数据失败");
            }
        }

        private void RemoveParameterFromChartData(string parameterDisplayName)
        {
            try
            {
                foreach (var dataPoint in ChartData)
                {
                    dataPoint.ParameterValues.Remove(parameterDisplayName);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "从图表数据中移除参数失败: {DisplayName}", parameterDisplayName);
            }
        }

        #endregion

        #region 辅助方法

        private bool CanAddParameter(AvailableParameter parameter)
        {
            if (parameter == null) return false;
            var key = $"{parameter.DeviceId}|{parameter.CommandName}|{parameter.ParameterName}";
            return !MonitoredParameters.Any(p => p.Key == key);
        }

        private bool CanRemoveParameter(MonitoredParameter parameter)
        {
            return parameter != null;
        }

        private void LoadAvailableParameters()
        {
            try
            {
                var available = _monitoringService.GetAvailableParameters();
                AvailableParameters.Clear();

                foreach (var param in available.Where(p => p.IsNumeric))
                {
                    AvailableParameters.Add(param);
                }

                _logger.LogDebug("加载了 {Count} 个可用参数", AvailableParameters.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "加载可用参数失败");
            }
        }

        private void LoadMonitoredParameters()
        {
            try
            {
                var monitored = _monitoringService.GetMonitoredParameters();
                MonitoredParameters.Clear();

                foreach (var param in monitored)
                {
                    MonitoredParameters.Add(param);
                }

                _logger.LogDebug("加载了 {Count} 个监控参数", MonitoredParameters.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "加载监控参数失败");
            }
        }

        private void FilterAvailableParameters()
        {
            if (string.IsNullOrWhiteSpace(SearchText))
            {
                LoadAvailableParameters();
                return;
            }

            var filtered = _monitoringService.GetAvailableParameters()
                .Where(p => p.DisplayName.IndexOf(SearchText, StringComparison.OrdinalIgnoreCase) >= 0)
                .ToList();

            AvailableParameters.Clear();
            foreach (var param in filtered)
            {
                AvailableParameters.Add(param);
            }
        }

        private void RefreshAvailableParameters()
        {
            LoadAvailableParameters();
            _logger.LogInformation("刷新可用参数列表");
        }

        private void ClearData()
        {
            try
            {
                _monitoringService.ClearAllData();
                _lastChartUpdateTime = DateTime.MinValue;
                ClearChartDataOnly();
                LastUpdateTime = DateTime.Now;
                _baseTime = DateTime.Now;
                InitializeDateTimeFormatter();

                // ★ 改用多轴 Builder
                NormalizedChartModel = MultiAxisChartBuilder.CreateEmptyModel();
                ChartLegendItems.Clear();
                _lastSeriesInputs.Clear();
                HasAlarm = false;
                UnclassifiedTip = "";
                HasUnclassifiedParameters = false;

                _logger.LogInformation("清空所有图表数据");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "清空数据失败");
            }
        }

        /// <summary>
        /// ★ 重建图表模型(多 Y 轴版,取代原 RebuildNormalizedChart)
        /// </summary>
        private void RebuildChartModel()
        {
            try
            {
                var enabledParameters = MonitoredParameters.Where(p => p.IsEnabled).ToList();

                ChartLegendItems.Clear();
                var unclassified = new List<string>();
                var seriesInputs = new List<MultiAxisChartBuilder.SeriesInput>();

                int colorIndex = 0;
                foreach (var param in enabledParameters)
                {
                    // 1. 提取该参数的所有数据点
                    var dataPoints = ChartData
                        .Where(cd => cd.ParameterValues.ContainsKey(param.DisplayName))
                        .Select(cd => (cd.Timestamp, cd.ParameterValues[param.DisplayName]))
                        .OrderBy(x => x.Timestamp)
                        .ToList();

                    if (!dataPoints.Any()) continue;

                    // 2. 识别参数轴类型(温度/压力/流量)
                    var unit = SafeGetUnit(param);
                    var axisType = ParameterAxisClassifier.Classify(unit);

                    if (axisType == AxisType.Unclassified)
                    {
                        // 未分类的参数:不画,记录用于稍后提示
                        unclassified.Add($"{param.DisplayName}({(string.IsNullOrEmpty(unit) ? "无单位" : unit)})");
                        continue;
                    }

                    // 3. 决定线条颜色(优先用 param.Color,否则按调色板顺序)
                    OxyColor lineColor = TryParseOxyColor(param.Color, out var parsed)
                        ? parsed
                        : MultiAxisChartBuilder.LineColors[colorIndex % MultiAxisChartBuilder.LineColors.Length];

                    seriesInputs.Add(new MultiAxisChartBuilder.SeriesInput
                    {
                        DisplayName = param.DisplayName,
                        Unit = unit,
                        AxisType = axisType,
                        LineColor = lineColor,
                        Points = dataPoints,
                    });

                    // 4. 添加到图例
                    var wpfColor = System.Windows.Media.Color.FromRgb(lineColor.R, lineColor.G, lineColor.B);
                    ChartLegendItems.Add(new ChartLegendItem
                    {
                        Name = param.DisplayName,
                        ColorBrush = new SolidColorBrush(wpfColor),
                    });

                    colorIndex++;
                }

                // 5. 构建 PlotModel
                var model = MultiAxisChartBuilder.BuildModel(seriesInputs);

                // 6. 注册同步 Tracker(鼠标悬停显示所有参数当前值)
                AttachSyncTracker(model);

                NormalizedChartModel = model;

                // 7. 缓存,供"重置量程"使用
                _lastSeriesInputs = seriesInputs;

                // 8. 处理未分类提示
                if (unclassified.Count > 0)
                {
                    UnclassifiedTip = $"以下参数单位未分类,无法绘制: {string.Join(", ", unclassified)}";
                    HasUnclassifiedParameters = true;
                }
                else
                {
                    UnclassifiedTip = "";
                    HasUnclassifiedParameters = false;
                }

                model.InvalidatePlot(true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "重建多轴图表失败");
            }
        }

        /// <summary>
        /// ★ 安全获取参数单位(MonitoredParameter 的 Unit 属性可能不存在,容错处理)
        /// </summary>
        private static string SafeGetUnit(MonitoredParameter param)
        {
            try
            {
                var prop = param.GetType().GetProperty("Unit");
                if (prop != null)
                {
                    var val = prop.GetValue(param) as string;
                    return val ?? "";
                }
            }
            catch { }
            return "";
        }

        /// <summary>
        /// ★ 安全解析 OxyColor(支持 #RRGGBB 或 #AARRGGBB 格式)
        /// </summary>
        private static bool TryParseOxyColor(string hex, out OxyColor color)
        {
            color = OxyColors.Black;
            if (string.IsNullOrWhiteSpace(hex)) return false;
            try
            {
                color = OxyColor.Parse(hex);
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// ★ 注册同步 Tracker:鼠标悬停时显示当前时刻所有参数的值
        /// </summary>
        private void AttachSyncTracker(PlotModel model)
        {
            if (model == null) return;

            model.TrackerChanged += (s, e) =>
            {
                if (e.HitResult == null) return;
                if (!(e.HitResult.Series is LineSeries hitSeries)) return;

                // 命中点的 X(时间)
                double hitX = e.HitResult.DataPoint.X;
                DateTime hitTime;
                try
                {
                    hitTime = DateTimeAxis.ToDateTime(hitX);
                }
                catch
                {
                    return;
                }

                var sb = new StringBuilder();
                sb.AppendLine(hitTime.ToString("yyyy-MM-dd HH:mm:ss"));

                foreach (var series in model.Series.OfType<LineSeries>())
                {
                    if (series.Points.Count == 0) continue;

                    // 找最接近 hitX 的点
                    var nearest = series.Points
                        .OrderBy(p => Math.Abs(p.X - hitX))
                        .First();

                    string unit = "";
                    if (series.Tag is MultiAxisChartBuilder.SeriesMeta meta)
                    {
                        unit = meta.Unit;
                    }

                    sb.AppendLine($"{series.Title}: {nearest.Y:F2} {unit}".TrimEnd());
                }

                e.HitResult.Text = sb.ToString().TrimEnd();
            };
        }

        /// <summary>
        /// ★ 重置所有 Y 轴量程为自适应值
        /// </summary>
        private void ResetAxisRanges()
        {
            try
            {
                if (NormalizedChartModel == null) return;
                MultiAxisChartBuilder.ResetAllAxisRanges(NormalizedChartModel, _lastSeriesInputs);
                _logger.LogInformation("已重置所有 Y 轴量程为自适应值");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "重置量程失败");
            }
        }

        private void ClearChartDataOnly()
        {
            ChartData.Clear();
            DataPointsCount = 0;
            HasData = false;
        }

        private void ToggleConfiguration()
        {
            IsConfigurationVisible = !IsConfigurationVisible;
        }

        private void StartChartAutoUpdate()
        {
            try
            {
                if (_refreshTimer == null || !ShouldUpdateChart || !IsProcessRunning || !IsRealTimeMode)
                {
                    return;
                }

                if (!_refreshTimer.IsEnabled)
                {
                    _refreshTimer.Start();
                    _logger.LogInformation("图表自动更新已启动");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "启动图表自动更新失败");
            }
        }

        private void StopChartAutoUpdate()
        {
            if (_refreshTimer != null)
            {
                _refreshTimer.Stop();
                _logger.LogDebug("图表自动更新已停止");
            }
        }

        private void ResetIncrementalUpdateTimestamp()
        {
            try
            {
                if (ChartData.Any())
                {
                    _lastChartUpdateTime = ChartData.Max(cd => cd.Timestamp);
                }
                else
                {
                    _lastChartUpdateTime = DateTime.Now.AddMinutes(-1);
                }

                _logger.LogDebug("重置增量更新时间戳为: {Timestamp}",
                    _lastChartUpdateTime.ToString("HH:mm:ss.fff"));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "重置增量更新时间戳失败");
            }
        }

        private void CheckAndUpdateProcessStatus()
        {
            try
            {
                var isFlowRunning = _monitoringService.IsFlowCurrentlyRunning();

                if (isFlowRunning && !IsProcessRunning)
                {
                    _ = System.Threading.Tasks.Task.Run(async () =>
                    {
                        await RestoreRunningStateAsync();
                    });
                }
                else if (!isFlowRunning && IsProcessRunning)
                {
                    OnFlowStopped();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "检查流程状态失败");
            }
        }

        private async System.Threading.Tasks.Task RestoreMonitoringStateAsync()
        {
            try
            {
                if (!MonitoredParameters.Any())
                {
                    return;
                }

                var isFlowRunning = _monitoringService.IsFlowCurrentlyRunning();

                if (isFlowRunning)
                {
                    await RestoreRunningStateAsync();
                }
                else
                {
                    await LoadHistoricalDataAsync();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "恢复监控状态失败");
            }
        }

        private async System.Threading.Tasks.Task RestoreRunningStateAsync()
        {
            try
            {
                var earliestTime = GetEarliestDataTime();
                if (earliestTime.HasValue)
                {
                    _baseTime = earliestTime.Value;
                }
                else
                {
                    _baseTime = DateTime.Now.AddMinutes(-10);
                }

                InitializeDateTimeFormatter();
                await LoadHistoricalDataAsync();

                _isProcessRunning = true;
                _shouldUpdateChart = true;
                RaisePropertyChanged(nameof(IsProcessRunning));
                RaisePropertyChanged(nameof(ShouldUpdateChart));

                ResetIncrementalUpdateTimestamp();

                if (IsRealTimeMode)
                {
                    StartChartAutoUpdate();
                }

                _logger.LogInformation("运行状态恢复完成");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "恢复运行状态失败");
            }
        }

        private async System.Threading.Tasks.Task LoadHistoricalDataAsync()
        {
            try
            {
                ChartData.Clear();

                var allHistoricalData = new Dictionary<DateTime, ChartDataPoint>();
                var enabledParameters = MonitoredParameters.Where(p => p.IsEnabled).ToList();

                foreach (var parameter in enabledParameters)
                {
                    try
                    {
                        var parameterData = _monitoringService.GetParameterDataInRange(
                            parameter.DeviceId,
                            parameter.CommandName,
                            parameter.ParameterName);

                        foreach (var dataPoint in parameterData)
                        {
                            if (!allHistoricalData.TryGetValue(dataPoint.Timestamp, out var chartPoint))
                            {
                                chartPoint = new ChartDataPoint { Timestamp = dataPoint.Timestamp };
                                allHistoricalData[dataPoint.Timestamp] = chartPoint;
                            }

                            chartPoint.ParameterValues[parameter.DisplayName] = dataPoint.Value;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "加载参数历史数据失败: {DisplayName}", parameter.DisplayName);
                    }
                }

                var sortedData = allHistoricalData.Values.OrderBy(d => d.Timestamp).ToList();
                foreach (var dataPoint in sortedData)
                {
                    ChartData.Add(dataPoint);
                }

                DataPointsCount = ChartData.Count;
                HasData = ChartData.Any();

                await System.Threading.Tasks.Task.Delay(100);
                RaisePropertyChanged(nameof(HasData));
                RaisePropertyChanged(nameof(StatusText));

                // 加载完历史数据后,重建一次图表
                if (HasData)
                {
                    RebuildChartModel();
                }

                _logger.LogInformation("历史数据加载完成: {Count} 个数据点", ChartData.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "加载历史数据失败");
            }
        }

        private DateTime? GetEarliestDataTime()
        {
            try
            {
                DateTime? earliestTime = null;

                foreach (var param in MonitoredParameters)
                {
                    var data = _monitoringService.GetParameterData(
                        param.DeviceId, param.CommandName, param.ParameterName);

                    if (data.Any())
                    {
                        var paramEarliest = data.Min(d => d.Timestamp);
                        if (!earliestTime.HasValue || paramEarliest < earliestTime.Value)
                        {
                            earliestTime = paramEarliest;
                        }
                    }
                }

                return earliestTime;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取最早数据时间失败");
                return null;
            }
        }

        private void OnParameterDataUpdated(object sender, ParameterDataUpdatedEventArgs e)
        {
            try
            {
                if (!IsProcessRunning)
                {
                    CheckAndUpdateProcessStatus();
                }

                if (IsRealTimeMode && ShouldUpdateChart)
                {
                    LastUpdateTime = DateTime.Now;
                    RaisePropertyChanged(nameof(StatusText));

                    if (_refreshTimer != null && !_refreshTimer.IsEnabled && IsProcessRunning)
                    {
                        StartChartAutoUpdate();
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "处理参数数据更新事件失败");
            }
        }

        #endregion

        #region IDisposable

        public void Dispose()
        {
            try
            {
                _refreshTimer?.Stop();

                if (_monitoringService != null)
                {
                    _monitoringService.ParameterDataUpdated -= OnParameterDataUpdated;
                }

                _eventAggregator.GetEvent<LanguageChangedEvent>()?.Unsubscribe(LanguageChangedHandler);

                try
                {
                    _eventAggregator?.GetEvent<FlowExecutionStateChangedEvent>()
                        ?.Unsubscribe(OnFlowStateChanged);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "取消订阅流程状态事件失败");
                }

                _logger.LogInformation("参数监控图表ViewModel已释放");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "释放资源失败");
            }
        }

        #endregion


        #region 本地化

        private string _viewTitle;
        private string _paramCountLabel;
        private string _clearLabeL;
        private string _exportLabel;
        private string _normalizationTxt;
        private string _noneParamTxt;
        private string _tipTxt;
        private string _updateTimeTxt;
        private string _dataCountTxt;

        public string ViewTitle { get { return _viewTitle; } set { SetProperty(ref _viewTitle, value); } }
        public string ParamCountLabel { get { return _paramCountLabel; } set { SetProperty(ref _paramCountLabel, value); } }
        public string ClearLabeL { get { return _clearLabeL; } set { SetProperty(ref _clearLabeL, value); } }
        public string ExportLabel { get { return _exportLabel; } set { SetProperty(ref _exportLabel, value); } }
        public string NormalizationTxt { get { return _normalizationTxt; } set { SetProperty(ref _normalizationTxt, value); } }
        public string NoneParamTxt { get { return _noneParamTxt; } set { SetProperty(ref _noneParamTxt, value); } }
        public string TipTxt { get { return _tipTxt; } set { SetProperty(ref _tipTxt, value); } }

        // ★ 修复原 bug:之前误写成 SetProperty(ref _alarmMessage, value)
        public string UpdateTimeTxt { get { return _updateTimeTxt; } set { SetProperty(ref _updateTimeTxt, value); } }

        public string DataCountTxt { get { return _dataCountTxt; } set { SetProperty(ref _dataCountTxt, value); } }

        public void LanguageChangedHandler(string s)
        {
            ViewTitle = _localizationService.GetString("ParamMonitor_Title", "参数监控");
            ParamCountLabel = _localizationService.GetString("ParamMonitor_ParamCountLabel", "参数");
            ClearLabeL = _localizationService.GetString("ParamMonitor_Clear", "清除");
            ExportLabel = _localizationService.GetString("ParamMonitor_Export", "导出");
            NormalizationTxt = _localizationService.GetString("ParamMonitor_Normalization", "归一化");
            NoneParamTxt = _localizationService.GetString("ParamMonitor_NoneMonitor", "暂无监控数据");
            TipTxt = _localizationService.GetString("ParamMonitor_Tip", "从左侧面板选择参数开始监控");
            UpdateTimeTxt = _localizationService.GetString("ParamMonitor_LastTime", "最后更新:");
            DataCountTxt = _localizationService.GetString("ParamMonitor_DataPoint", "数据点");
        }

        #endregion
    }
}
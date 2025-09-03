// ParameterMonitorChartViewModel.cs - 修正时间轴处理部分
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using System.Windows.Threading;
using System.ComponentModel;
using MaxChemical.Modules.Designer.Models;
using MaxChemical.Modules.Designer.Services;
using MaxChemical.Logging;
using Prism.Commands;
using Prism.Mvvm;
using LiveCharts;
using LiveCharts.Configurations;
using LiveCharts.Wpf;
using MaxChemical.Modules.Designer.Event;
using MaxChemical.Modules.Designer.Service.Execution;
using Prism.Events;

namespace MaxChemical.Modules.Designer.ViewModels
{
    public class ParameterMonitorChartViewModel : BindableBase, IDisposable
    {
        private readonly IParameterMonitoringService _monitoringService;
        private readonly IEventAggregator _eventAggregator;
        private readonly ILogService _logger;
        private DispatcherTimer _refreshTimer;

        private bool _isConfigurationVisible = true;
        private bool _isRealTimeMode = true;
        private AvailableParameter _selectedAvailableParameter;
        private MonitoredParameter _selectedMonitoredParameter;
        private int _dataPointsCount = 0;
        private DateTime _lastUpdateTime = DateTime.Now;
        private SeriesCollection _chartSeries;
        private Func<double, string> _dateTimeFormatter;
        private bool _hasData = false;
        private string _searchText = string.Empty;
        private DateTime _baseTime = DateTime.Now; // 基准时间

        public ObservableCollection<AvailableParameter> AvailableParameters { get; }
        public ObservableCollection<MonitoredParameter> MonitoredParameters { get; }
        public ObservableCollection<ChartDataPoint> ChartData { get; }

        public DelegateCommand<AvailableParameter> AddParameterCommand { get; private set; }
        public DelegateCommand<MonitoredParameter> RemoveParameterCommand { get; private set; }
        public DelegateCommand ClearDataCommand { get; private set; }
        public DelegateCommand ToggleConfigurationCommand { get; private set; }
        public DelegateCommand<MonitoredParameter> ToggleParameterCommand { get; private set; }
        public DelegateCommand RefreshAvailableParametersCommand { get; private set; }

        private Func<double, string> _parameterValueFormatter;

        public Func<double, string> ParameterValueFormatter
        {
            get => _parameterValueFormatter;
            set => SetProperty(ref _parameterValueFormatter, value);
        }
        // 新增：流程状态控制
        private bool _isProcessRunning = false;
        private bool _shouldUpdateChart = false;

        public bool IsProcessRunning
        {
            get => _isProcessRunning;
            set
            {
                if (SetProperty(ref _isProcessRunning, value))
                {
                    _logger.LogInformation("流程运行状态变更: {IsRunning}", value);

                    // 直接设置 ShouldUpdateChart，而不是通过属性
                    _shouldUpdateChart = value;
                    RaisePropertyChanged(nameof(ShouldUpdateChart));

                    if (!value)
                    {
                        _logger.LogInformation("流程已停止，停止图表自动更新");
                        StopChartAutoUpdate();
                    }
                    else
                    {
                        _logger.LogInformation("流程已开始，启动图表自动更新");
                        StartChartAutoUpdate();
                    }
                }
            }
        }

        public bool ShouldUpdateChart
        {
            get => _shouldUpdateChart;
            set
            {
                if (SetProperty(ref _shouldUpdateChart, value))
                {
                    _logger.LogInformation("图表更新标志变更: {ShouldUpdate}, IsProcessRunning: {IsRunning}",
                        value, IsProcessRunning);

                    // 如果设置为true且流程运行中，确保定时器启动
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
        public bool IsConfigurationVisible
        {
            get => _isConfigurationVisible;
            set => SetProperty(ref _isConfigurationVisible, value);
        }

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

        public AvailableParameter SelectedAvailableParameter
        {
            get => _selectedAvailableParameter;
            set => SetProperty(ref _selectedAvailableParameter, value);
        }

        public MonitoredParameter SelectedMonitoredParameter
        {
            get => _selectedMonitoredParameter;
            set => SetProperty(ref _selectedMonitoredParameter, value);
        }

        public int DataPointsCount
        {
            get => _dataPointsCount;
            set => SetProperty(ref _dataPointsCount, value);
        }

        public DateTime LastUpdateTime
        {
            get => _lastUpdateTime;
            set => SetProperty(ref _lastUpdateTime, value);
        }

        public SeriesCollection ChartSeries
        {
            get => _chartSeries;
            set => SetProperty(ref _chartSeries, value);
        }

        public Func<double, string> DateTimeFormatter
        {
            get => _dateTimeFormatter;
            set => SetProperty(ref _dateTimeFormatter, value);
        }

        public bool HasData
        {
            get => _hasData;
            set => SetProperty(ref _hasData, value);
        }

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
        private AxesCollection _yAxes;

        public AxesCollection YAxes
        {
            get => _yAxes;
            set => SetProperty(ref _yAxes, value);
        }

        public string StatusText => $"数据点: {DataPointsCount} | 最后更新: {LastUpdateTime:HH:mm:ss}";

        public ParameterMonitorChartViewModel(IParameterMonitoringService monitoringService, IEventAggregator eventAggregator)
        {
            _monitoringService = monitoringService ?? throw new ArgumentNullException(nameof(monitoringService));
            _eventAggregator = eventAggregator ?? throw new ArgumentNullException(nameof(eventAggregator));
            _logger = new LogService().ForContext<ParameterMonitorChartViewModel>();

            AvailableParameters = new ObservableCollection<AvailableParameter>();
            MonitoredParameters = new ObservableCollection<MonitoredParameter>();
            ChartData = new ObservableCollection<ChartDataPoint>();

            _baseTime = DateTime.Now;

            InitializeCommands();
            InitializeTimer();
            InitializeChart();
            SubscribeToFlowEvents();

            // *** 关键修复：延迟初始化，确保正确的状态恢复 ***
            InitializeAsync();

            _logger.LogInformation("参数监控图表ViewModel已初始化，基准时间: {BaseTime}", _baseTime);
        }
        /// <summary>
        /// *** 新增：异步初始化方法 ***
        /// </summary>
        private async void InitializeAsync()
        {
            try
            {
                // 短暂延迟，确保所有依赖项都已准备好
                await System.Threading.Tasks.Task.Delay(100);

                // 1. 先订阅数据更新事件
                _monitoringService.ParameterDataUpdated += OnParameterDataUpdated;
                _logger.LogDebug("已订阅参数数据更新事件");

                // 2. 加载可用参数和监控参数
                LoadAvailableParameters();
                LoadMonitoredParameters();

                // 3. 检查流程状态并恢复
                await RestoreMonitoringStateAsync();

                _logger.LogInformation("异步初始化完成");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "异步初始化失败");
            }
        }
        /// <summary>
        /// *** 新增：恢复监控状态 ***
        /// </summary>
        private async System.Threading.Tasks.Task RestoreMonitoringStateAsync()
        {
            try
            {
                // 检查是否有监控参数
                if (!MonitoredParameters.Any())
                {
                    _logger.LogDebug("没有监控参数，跳过状态恢复");
                    return;
                }

                _logger.LogInformation("开始恢复监控状态，监控参数数量: {Count}", MonitoredParameters.Count);

                // 检查流程是否正在运行
                var isFlowRunning = _monitoringService.IsFlowCurrentlyRunning();
                _logger.LogInformation("流程运行状态: {IsRunning}", isFlowRunning);

                if (isFlowRunning)
                {
                    // 流程正在运行，需要恢复监控状态
                    await RestoreRunningStateAsync();
                }
                else
                {
                    // 流程未运行，但可能有历史数据
                    await LoadHistoricalDataAsync();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "恢复监控状态失败");
            }
        }
        /// <summary>
        /// *** 新增：恢复运行状态 ***
        /// </summary>
        private async System.Threading.Tasks.Task RestoreRunningStateAsync()
        {
            try
            {
                _logger.LogInformation("恢复运行状态");

                // 1. 设置基准时间
                var earliestTime = GetEarliestDataTime();
                if (earliestTime.HasValue)
                {
                    _baseTime = earliestTime.Value;
                    _logger.LogInformation("设置基准时间为: {BaseTime}", _baseTime.ToString("HH:mm:ss.fff"));
                }
                else
                {
                    _baseTime = DateTime.Now.AddMinutes(-10);
                    _logger.LogInformation("没有找到数据，设置基准时间为10分钟前: {BaseTime}", _baseTime.ToString("HH:mm:ss.fff"));
                }

                // 2. 重新初始化图表
                InitializeChart();

                // 3. 加载所有历史数据
                await LoadHistoricalDataAsync();

                // 4. 设置运行状态
                _isProcessRunning = true;
                _shouldUpdateChart = true;

                RaisePropertyChanged(nameof(IsProcessRunning));
                RaisePropertyChanged(nameof(ShouldUpdateChart));

                // 5. 重置增量更新时间戳
                ResetIncrementalUpdateTimestamp();

                // 6. 启动实时更新
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
        /// <summary>
        /// *** 新增：加载历史数据 ***
        /// </summary>
        private async System.Threading.Tasks.Task LoadHistoricalDataAsync()
        {
            try
            {
                _logger.LogInformation("开始加载历史数据");

                // 清空现有数据
                ChartData.Clear();
                _seriesValuesCache.Clear();

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

                        _logger.LogDebug("参数 {DisplayName} 加载了 {Count} 个历史数据点",
                            parameter.DisplayName, parameterData.Count);

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
                        _logger.LogError(ex, "加载参数 {DisplayName} 的历史数据失败", parameter.DisplayName);
                    }
                }

                // 按时间排序并添加到图表数据
                var sortedData = allHistoricalData.Values.OrderBy(d => d.Timestamp).ToList();
                foreach (var dataPoint in sortedData)
                {
                    ChartData.Add(dataPoint);
                }

                DataPointsCount = ChartData.Count;
                HasData = ChartData.Any();

                // *** 关键修复：强制重建图表系列 ***
                ForceRebuildChart();

                _logger.LogInformation("历史数据加载完成: 总数据点={Count}, 时间范围={Start} - {End}",
                    ChartData.Count,
                    ChartData.Any() ? ChartData.First().Timestamp.ToString("HH:mm:ss.fff") : "无",
                    ChartData.Any() ? ChartData.Last().Timestamp.ToString("HH:mm:ss.fff") : "无");

                // 短暂延迟后再次强制更新UI
                await System.Threading.Tasks.Task.Delay(100);
                RaisePropertyChanged(nameof(HasData));
                RaisePropertyChanged(nameof(DataPointsCount));
                RaisePropertyChanged(nameof(StatusText));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "加载历史数据失败");
            }
        }
        /// <summary>
        /// 订阅流程执行状态事件
        /// </summary>
        private void SubscribeToFlowEvents()
        {
            try
            {
                // 订阅流程状态变更事件
                _eventAggregator?.GetEvent<FlowExecutionStateChangedEvent>()?.Subscribe(OnFlowStateChanged, ThreadOption.UIThread);

                _logger.LogDebug("已订阅流程状态变更事件");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "订阅流程状态事件失败");
            }
        }

        /// <summary>
        /// 处理流程状态变更
        /// </summary>
        /// <summary>
        /// 处理流程状态变更
        /// </summary>
        private void OnFlowStateChanged(FlowExecutionStateChangedEventArgs args)
        {
            try
            {
                _logger.LogInformation("收到流程状态变更: {OldState} -> {NewState}", args.OldState, args.NewState);

                switch (args.NewState)
                {
                    case FlowExecutionState.Running:
                        _logger.LogInformation("流程状态变为Running，调用OnFlowStarted");
                        OnFlowStarted();
                        break;

                    case FlowExecutionState.Completed:
                        _logger.LogInformation("流程状态变为Completed，调用OnFlowStopped");
                        OnFlowStopped();
                        break;

                    case FlowExecutionState.Stopped:
                        _logger.LogInformation("流程状态变为Stopped，调用OnFlowStopped");
                        OnFlowStopped();
                        break;

                    case FlowExecutionState.Error:
                        _logger.LogInformation("流程状态变为Error，调用OnFlowStopped");
                        OnFlowStopped();
                        break;

                    case FlowExecutionState.Idle:
                        _logger.LogInformation("流程状态变为Idle，调用OnFlowStopped");
                        OnFlowStopped();
                        break;

                    case FlowExecutionState.Paused:
                        _logger.LogInformation("流程状态变为Paused，调用OnFlowPaused");
                        OnFlowPaused();
                        break;

                    case FlowExecutionState.Resuming:
                        _logger.LogInformation("流程状态变为Resuming，调用OnFlowResumed");
                        OnFlowResumed();
                        break;

                    default:
                        _logger.LogDebug("未处理的流程状态: {State}", args.NewState);
                        break;
                }

                // 状态处理完成后，输出当前状态
                _logger.LogInformation("状态处理完成: IsProcessRunning={IsRunning}, ShouldUpdateChart={ShouldUpdate}, Timer.IsEnabled={TimerEnabled}",
                    IsProcessRunning, ShouldUpdateChart, _refreshTimer?.IsEnabled ?? false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "处理流程状态变更失败");
            }
        }

        /// <summary>
        /// 流程开始时的处理
        /// </summary>
        private void OnFlowStarted()
        {
            _logger.LogInformation("流程开始，启动图表监控");

            // 强制设置状态为运行
            _isProcessRunning = true;
            _shouldUpdateChart = true;

            // 通知属性变更
            RaisePropertyChanged(nameof(IsProcessRunning));
            RaisePropertyChanged(nameof(ShouldUpdateChart));

            // *** 修复：如果有监控参数，保留当前基准时间，否则重置 ***
            if (!MonitoredParameters.Any() || !ChartData.Any())
            {
                // 重置基准时间和缓存
                _baseTime = DateTime.Now;
                _lastChartUpdateTime = DateTime.MinValue;
                _lastYAxisUpdate = DateTime.MinValue;
                _lastYAxisRanges.Clear();
                _seriesValuesCache.Clear();

                // 清空旧数据
                ClearChartDataOnly();

                // 重新初始化图表
                InitializeChart();
            }
            else
            {
                // 有现有数据时，只重置更新时间戳
                ResetIncrementalUpdateTimestamp();
                _logger.LogInformation("保留现有数据，重置增量更新时间戳");
            }

            UpdateChartSeries(); // 重建系列

            // 确保定时器启动
            StartChartAutoUpdate();

            _logger.LogInformation("图表监控状态已设置，启用增量更新模式");
        }

        /// <summary>
        /// 只清空图表数据，不重建系列
        /// </summary>
        private void ClearChartDataOnly()
        {
            ChartData.Clear();
            DataPointsCount = 0;

            // 清空现有系列的数据，但保留系列结构
            if (ChartSeries != null)
            {
                foreach (var series in ChartSeries.OfType<LineSeries>())
                {
                    series.Values?.Clear();
                }
            }

            _seriesValuesCache.Clear();
            HasData = false;
        }

        private void ClearData()
        {
            try
            {
                _monitoringService.ClearAllData();

                // 重置所有缓存和状态
                _lastChartUpdateTime = DateTime.MinValue;
                _lastYAxisUpdate = DateTime.MinValue;
                _lastYAxisRanges.Clear();
                _lastEnabledParameterKeys.Clear();

                ClearChartDataOnly();
                LastUpdateTime = DateTime.Now;

                // 重置基准时间
                _baseTime = DateTime.Now;

                InitializeChart();
                UpdateChartSeries(); // 重建系列

                _logger.LogInformation("清空所有图表数据，重置为增量更新模式");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "清空数据失败");
            }
        }
        /// <summary>
        /// 流程停止时的处理
        /// </summary>
        private void OnFlowStopped()
        {
            _logger.LogInformation("流程停止，停止图表监控");

            // 最后一次更新图表数据，保持显示
            try
            {
                if (ShouldUpdateChart)
                {
                    UpdateChartData();
                    _logger.LogDebug("流程停止前最后一次更新图表数据完成");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "流程停止时最终更新图表数据失败");
            }

            // 设置状态为停止
            _isProcessRunning = false;
            _shouldUpdateChart = false;

            // 通知属性变更
            RaisePropertyChanged(nameof(IsProcessRunning));
            RaisePropertyChanged(nameof(ShouldUpdateChart));

            // 停止定时器
            StopChartAutoUpdate();

            _logger.LogInformation("图表监控已停止: IsProcessRunning={IsRunning}, ShouldUpdateChart={ShouldUpdate}",
                IsProcessRunning, ShouldUpdateChart);
        }

        /// <summary>
        /// 流程暂停时的处理
        /// </summary>
        private void OnFlowPaused()
        {
            _logger.LogInformation("流程暂停，暂停图表更新");
            ShouldUpdateChart = false;
        }

        /// <summary>
        /// 流程恢复时的处理
        /// </summary>
        private void OnFlowResumed()
        {
            _logger.LogInformation("流程恢复，恢复图表更新");
            ShouldUpdateChart = true;
        }

        /// <summary>
        /// 启动图表自动更新
        /// </summary>
        private void StartChartAutoUpdate()
        {
            try
            {
                if (_refreshTimer == null)
                {
                    _logger.LogWarning("定时器未初始化，无法启动图表更新");
                    return;
                }

                if (!ShouldUpdateChart)
                {
                    _logger.LogDebug("ShouldUpdateChart=false，跳过启动定时器");
                    return;
                }

                if (!IsProcessRunning)
                {
                    _logger.LogDebug("IsProcessRunning=false，跳过启动定时器");
                    return;
                }

                if (!IsRealTimeMode)
                {
                    _logger.LogDebug("IsRealTimeMode=false，跳过启动定时器");
                    return;
                }

                if (_refreshTimer.IsEnabled)
                {
                    _logger.LogDebug("定时器已经在运行中");
                    return;
                }

                _refreshTimer.Start();
                _logger.LogInformation("图表自动更新已启动 - Timer.IsEnabled: {IsEnabled}", _refreshTimer.IsEnabled);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "启动图表自动更新失败");
            }
        }

        /// <summary>
        /// 停止图表自动更新
        /// </summary>
        private void StopChartAutoUpdate()
        {
            if (_refreshTimer != null)
            {
                _refreshTimer.Stop();
                _logger.LogDebug("图表自动更新已停止");
            }
        }
        private void InitializeCommands()
        {
            AddParameterCommand = new DelegateCommand<AvailableParameter>(AddParameter, CanAddParameter);
            RemoveParameterCommand = new DelegateCommand<MonitoredParameter>(RemoveParameter, CanRemoveParameter);
            ClearDataCommand = new DelegateCommand(ClearData);
            ToggleConfigurationCommand = new DelegateCommand(ToggleConfiguration);
            ToggleParameterCommand = new DelegateCommand<MonitoredParameter>(ToggleParameter);
            RefreshAvailableParametersCommand = new DelegateCommand(RefreshAvailableParameters);
        }

        private void InitializeTimer()
        {
            _refreshTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(200) // 500ms刷新一次图表
            };
            _refreshTimer.Tick += OnTimerTick;
        }
        /// <summary>
        /// 定时器回调（修改版本）
        /// </summary>
        private void OnTimerTick(object sender, EventArgs e)
        {
            // 检查是否应该更新图表
            if (!ShouldUpdateChart)
            {
                _logger.LogDebug("图表更新被禁用，跳过定时器更新");
                return;
            }

            if (!IsProcessRunning)
            {
                _logger.LogDebug("流程未运行，跳过定时器更新");
                return;
            }

            UpdateChartData();
        }
        private void InitializeChart()
        {
            ChartSeries = new SeriesCollection();
            YAxes = new AxesCollection(); // 初始化Y轴集合

            // *** 修改：X轴时间格式化器支持毫秒显示 ***
            DateTimeFormatter = value =>
            {
                try
                {
                    var targetTime = _baseTime.AddSeconds(value);
                    if (targetTime < DateTime.MinValue.AddDays(1) || targetTime > DateTime.MaxValue.AddDays(-1))
                    {
                        return value.ToString("F3") + "s"; // 显示到毫秒
                    }

                    // 根据时间范围动态选择显示格式
                    var timeSpan = DateTime.Now - _baseTime;

                    if (timeSpan.TotalMinutes < 1) // 1分钟内显示毫秒
                    {
                        return targetTime.ToString("HH:mm:ss.fff");
                    }
                    else if (timeSpan.TotalMinutes < 10) // 10分钟内显示到毫秒但简化
                    {
                        return targetTime.ToString("mm:ss.fff");
                    }
                    else if (timeSpan.TotalHours < 1) // 1小时内显示到秒
                    {
                        return targetTime.ToString("mm:ss");
                    }
                    else // 超过1小时显示时分秒
                    {
                        return targetTime.ToString("HH:mm:ss");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "时间格式化失败，使用数值显示: {Value}", value);
                    return value.ToString("F3") + "s";
                }
            };

            // *** 修改：配置LiveCharts以支持毫秒级精度 ***
            var config = Mappers.Xy<ChartDataPoint>()
                .X(point => (point.Timestamp - _baseTime).TotalSeconds) // 保持秒为单位，但精度到毫秒
                .Y(point => 0);

            Charting.For<ChartDataPoint>(config);

            _logger.LogDebug("图表初始化完成，X轴=毫秒级时间，支持多Y轴，基准时间: {BaseTime}", _baseTime);
        }

        // ... 其他方法保持不变 ...

        private List<string> _lastEnabledParameterKeys = new List<string>();

        private void UpdateChartSeries()
        {
            try
            {
                var enabledParameters = MonitoredParameters.Where(p => p.IsEnabled).ToList();
                var currentParameterKeys = enabledParameters.Select(p => p.Key).ToList();

                bool parametersChanged = !currentParameterKeys.SequenceEqual(_lastEnabledParameterKeys);

                if (!parametersChanged && ChartSeries != null && ChartSeries.Any())
                {
                    _logger.LogDebug("参数未变化，跳过系列重建");
                    return;
                }

                _logger.LogInformation("参数发生变化，重建图表系列");
                _lastEnabledParameterKeys = currentParameterKeys;

                // 清空缓存
                _seriesValuesCache.Clear();

                var series = new SeriesCollection();

                if (enabledParameters.Any())
                {
                    for (int i = 0; i < enabledParameters.Count; i++)
                    {
                        var parameter = enabledParameters[i];

                        // *** 关键修复：创建新的 ChartValues 实例 ***
                        var values = new ChartValues<ChartDataPoint>();

                        // 添加现有数据
                        foreach (var dataPoint in ChartData.OrderBy(d => d.Timestamp))
                        {
                            if (dataPoint.ParameterValues.ContainsKey(parameter.DisplayName))
                            {
                                values.Add(dataPoint);
                            }
                        }

                        // 缓存系列值以供后续增量更新
                        _seriesValuesCache[parameter.DisplayName] = values;

                        var strokeBrush = GetParameterBrush(parameter);

                        var lineSeries = new LineSeries
                        {
                            Title = parameter.DisplayName,
                            Values = values, // *** 关键：使用新创建的 values ***
                            Stroke = strokeBrush,
                            Fill = System.Windows.Media.Brushes.Transparent,
                            StrokeThickness = 2,
                            PointGeometry = null,
                            LineSmoothness = 0,
                            ScalesYAt = i,
                            Configuration = Mappers.Xy<ChartDataPoint>()
                                .X(point => (point.Timestamp - _baseTime).TotalSeconds)
                                .Y(point => point.ParameterValues.ContainsKey(parameter.DisplayName)
                                          ? point.ParameterValues[parameter.DisplayName]
                                          : 0)
                        };

                        series.Add(lineSeries);
                        _logger.LogDebug("创建系列: {Title}, 初始数据点: {Count}, Y轴索引: {YAxisIndex}",
                            lineSeries.Title, values.Count, i);
                    }
                }

                // *** 关键修复：强制创建新的 SeriesCollection ***
                ChartSeries = new SeriesCollection();
                foreach (var s in series)
                {
                    ChartSeries.Add(s);
                }

                HasData = ChartData.Any();

                UpdateYAxes(enabledParameters);

                // *** 强制通知属性变更 ***
                RaisePropertyChanged(nameof(ChartSeries));
                RaisePropertyChanged(nameof(HasData));

                _logger.LogInformation("图表系列重建完成: 系列数={SeriesCount}, 数据点={DataCount}",
                    series.Count, ChartData.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "更新图表系列失败");
                HasData = false;
            }
        }


        /// <summary>
        /// 获取参数对应的画刷
        /// </summary>
        private System.Windows.Media.Brush GetParameterBrush(MonitoredParameter parameter)
        {
            try
            {
                var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(parameter.Color);
                return new System.Windows.Media.SolidColorBrush(color);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "颜色转换失败，使用默认颜色: {Color}", parameter.Color);
                return System.Windows.Media.Brushes.Blue;
            }
        }
        /// <summary>
        /// 改进的Y轴更新方法，支持更好的布局
        /// </summary>
        private void UpdateYAxes(List<MonitoredParameter> enabledParameters)
        {
            try
            {
                var axes = new AxesCollection();

                for (int i = 0; i < enabledParameters.Count; i++)
                {
                    var parameter = enabledParameters[i];
                    var values = GetParameterValues(parameter);
                    var (minValue, maxValue) = CalculateAxisRange(values);
                    var formatter = CreateParameterFormatter(minValue, maxValue);

                    var axis = new Axis
                    {
                        Title = $"{parameter.DisplayName}",
                        LabelFormatter = formatter,
                        Position = AxisPosition.LeftBottom, // 所有Y轴都在左侧
                        MinValue = minValue,
                        MaxValue = maxValue,
                        Separator = new Separator
                        {
                            StrokeThickness = 0.5,
                            StrokeDashArray = new System.Windows.Media.DoubleCollection { 4, 2 }
                        }
                    };

                    // 设置轴颜色与线条颜色一致
                    try
                    {
                        var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(parameter.Color);
                        var brush = new System.Windows.Media.SolidColorBrush(color);
                        axis.Foreground = brush;

                        // 为多个Y轴添加视觉区分
                        if (i > 0)
                        {
                            // 可以调整字体大小或样式来区分多个轴
                            // 注意：LiveCharts的轴偏移支持有限，主要通过颜色区分
                        }
                    }
                    catch
                    {
                        axis.Foreground = System.Windows.Media.Brushes.Black;
                    }

                    axes.Add(axis);
                    _logger.LogDebug("创建左侧Y轴 #{Index}: {Title}, 范围: {Min:F2} - {Max:F2}, 颜色: {Color}",
                        i + 1, parameter.DisplayName, minValue, maxValue, parameter.Color);
                }

                YAxes = axes;
                RaisePropertyChanged(nameof(YAxes));

                _logger.LogInformation("左侧Y轴配置更新完成，共 {Count} 个轴", axes.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "更新Y轴配置失败");
            }
        }
        /// <summary>
        /// 获取参数的所有数值
        /// </summary>
        private List<double> GetParameterValues(MonitoredParameter parameter)
        {
            var values = new List<double>();

            try
            {
                foreach (var dataPoint in ChartData)
                {
                    if (dataPoint.ParameterValues.TryGetValue(parameter.DisplayName, out var value))
                    {
                        values.Add(value);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取参数值失败: {Parameter}", parameter.DisplayName);
            }

            return values;
        }

        /// <summary>
        /// 计算轴的显示范围
        /// </summary>
        private (double min, double max) CalculateAxisRange(List<double> values)
        {
            if (!values.Any())
                return (0, 100);

            var min = values.Min();
            var max = values.Max();

            // 添加10%的边距
            var range = max - min;
            if (range == 0)
            {
                // 如果所有值相同，创建一个小范围
                return (min - 1, max + 1);
            }

            var margin = range * 0.1;
            return (min - margin, max + margin);
        }

        /// <summary>
        /// 创建参数格式化器
        /// </summary>
        private Func<double, string> CreateParameterFormatter(double minValue, double maxValue)
        {
            return value =>
            {
                try
                {
                    var range = Math.Abs(maxValue - minValue);

                    if (range >= 1000)
                    {
                        return value.ToString("F0"); // 大范围不显示小数
                    }
                    else if (range >= 10)
                    {
                        return value.ToString("F1"); // 显示1位小数
                    }
                    else if (range >= 1)
                    {
                        return value.ToString("F2"); // 显示2位小数
                    }
                    else
                    {
                        return value.ToString("F3"); // 小范围显示3位小数
                    }
                }
                catch
                {
                    return value.ToString("F2");
                }
            };
        }
        // 新增：手动启动/停止监控的方法（用于UI控制）
        public void StartMonitoring()
        {
            _logger.LogInformation("手动启动图表监控");
            OnFlowStarted();
        }

        public void StopMonitoring()
        {
            _logger.LogInformation("手动停止图表监控");
            OnFlowStopped();
        }

        // 其他方法保持不变...
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
                UpdateChartSeries();
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
        /// <summary>
        /// 强制检查并更新流程状态
        /// </summary>
        private void CheckAndUpdateProcessStatus()
        {
            try
            {
                var isFlowRunning = _monitoringService.IsFlowCurrentlyRunning();

                _logger.LogInformation("检查流程状态: 服务报告={ServiceRunning}, UI状态={UIRunning}",
                    isFlowRunning, IsProcessRunning);

                if (isFlowRunning && !IsProcessRunning)
                {
                    _logger.LogInformation("检测到流程正在运行但UI状态不正确，更新状态");

                    // 异步恢复状态，避免阻塞UI
                    _ = System.Threading.Tasks.Task.Run(async () =>
                    {
                        try
                        {
                            await RestoreRunningStateAsync();
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "异步恢复状态失败");
                        }
                    });
                }
                else if (!isFlowRunning && IsProcessRunning)
                {
                    _logger.LogInformation("流程已停止，更新UI状态");
                    OnFlowStopped();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "检查流程状态失败");
            }
        }
        /// <summary>
        /// 获取最早的数据时间
        /// </summary>
        private DateTime? GetEarliestDataTime()
        {
            try
            {
                DateTime? earliestTime = null;

                foreach (var param in MonitoredParameters)
                {
                    var data = _monitoringService.GetParameterData(param.DeviceId, param.CommandName, param.ParameterName);
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
                    parameter.DisplayName);

                LoadMonitoredParameters();

                // 强制检查流程状态
                CheckAndUpdateProcessStatus();

                // 如果流程正在运行，需要回溯获取数据并重建图表
                if (IsProcessRunning && ShouldUpdateChart)
                {
                    _logger.LogInformation("流程运行中添加参数，执行数据回溯: {DisplayName}", parameter.DisplayName);
                    BackfillDataForNewParameter(parameter);

                    // 强制重建图表系列以包含新参数
                    ForceRebuildChartSeries();

                    // *** 关键修复：确保实时更新继续工作 ***
                    EnsureRealTimeUpdateContinuity();
                }

                // 更新命令状态
                AddParameterCommand.RaiseCanExecuteChanged();

                _logger.LogInformation("添加监控参数成功: {DisplayName}", parameter.DisplayName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "添加监控参数失败: {DisplayName}", parameter.DisplayName);
            }
        }
        /// <summary>
        /// *** 新增：确保实时更新的连续性 ***
        /// </summary>
        private void EnsureRealTimeUpdateContinuity()
        {
            try
            {
                // 重新设置增量更新的时间戳为当前时间稍早
                var now = DateTime.Now;
                _lastChartUpdateTime = now.AddSeconds(-5); // 5秒前，确保能捕获最新数据

                _logger.LogInformation("重设增量更新时间戳为: {Timestamp}, 确保实时更新连续性",
                    _lastChartUpdateTime.ToString("HH:mm:ss.fff"));

                // 立即执行一次更新以验证实时数据流
                if (_refreshTimer != null && _refreshTimer.IsEnabled)
                {
                    UpdateChartData();
                }

                // 确保定时器正在运行
                if (IsProcessRunning && ShouldUpdateChart && IsRealTimeMode && _refreshTimer != null && !_refreshTimer.IsEnabled)
                {
                    _refreshTimer.Start();
                    _logger.LogInformation("重新启动实时更新定时器");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "确保实时更新连续性失败");
            }
        }
        /// <summary>
        /// 为新添加的参数回溯获取数据
        /// </summary>
        private void BackfillDataForNewParameter(AvailableParameter parameter)
        {
            try
            {
                // 使用范围查询获取流程开始后的数据
                var parameterData = _monitoringService.GetParameterDataInRange(
                    parameter.DeviceId,
                    parameter.CommandName,
                    parameter.ParameterName);

                if (!parameterData.Any())
                {
                    _logger.LogDebug("新参数 {DisplayName} 在当前流程周期内没有数据", parameter.DisplayName);
                    return;
                }

                _logger.LogInformation("新参数 {DisplayName} 回溯到 {Count} 个数据点",
                    parameter.DisplayName, parameterData.Count);

                // *** 关键修复：调整基准时间和更新时间戳 ***
                AdjustTimeRangeForBackfill(parameterData);

                // 将这些数据点合并到现有的ChartData中
                MergeBackfillData(parameter.DisplayName, parameterData);

                // 更新统计信息
                DataPointsCount = ChartData.Count;
                HasData = ChartData.Any();

                // *** 新增：重置增量更新的时间戳，确保后续能继续实时更新 ***
                ResetIncrementalUpdateTimestamp();

                _logger.LogInformation("参数 {DisplayName} 数据回溯完成，重置增量更新时间戳", parameter.DisplayName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "参数数据回溯失败: {DisplayName}", parameter.DisplayName);
            }
        }
        /// <summary>
        /// *** 新增：调整时间范围以适应回溯数据 ***
        /// </summary>
        private void AdjustTimeRangeForBackfill(List<ParameterDataPoint> backfillData)
        {
            try
            {
                if (!backfillData.Any()) return;

                var earliestBackfillTime = backfillData.Min(d => d.Timestamp);
                var latestBackfillTime = backfillData.Max(d => d.Timestamp);

                // 如果回溯数据的最早时间比当前基准时间还早，调整基准时间
                if (earliestBackfillTime < _baseTime)
                {
                    var oldBaseTime = _baseTime;
                    _baseTime = earliestBackfillTime;

                    _logger.LogInformation("调整基准时间: {OldTime} -> {NewTime}",
                        oldBaseTime.ToString("HH:mm:ss"), _baseTime.ToString("HH:mm:ss"));

                    // 重新初始化图表以适应新的基准时间
                    InitializeChart();
                }

                _logger.LogDebug("回溯数据时间范围: {Start} - {End}",
                    earliestBackfillTime.ToString("HH:mm:ss.fff"),
                    latestBackfillTime.ToString("HH:mm:ss.fff"));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "调整时间范围失败");
            }
        }
        /// <summary>
        /// *** 新增：重置增量更新时间戳 ***
        /// </summary>
        private void ResetIncrementalUpdateTimestamp()
        {
            try
            {
                // 将最后更新时间设置为当前图表数据中的最新时间
                if (ChartData.Any())
                {
                    _lastChartUpdateTime = ChartData.Max(cd => cd.Timestamp);
                    _logger.LogDebug("重置增量更新时间戳为: {Timestamp}",
                        _lastChartUpdateTime.ToString("HH:mm:ss.fff"));
                }
                else
                {
                    // 如果没有数据，设置为稍早的时间以确保能捕获新数据
                    _lastChartUpdateTime = DateTime.Now.AddMinutes(-1);
                    _logger.LogDebug("没有图表数据，设置增量更新时间戳为1分钟前");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "重置增量更新时间戳失败");
            }
        }

        /// <summary>
        /// 将回溯数据合并到现有图表数据中
        /// </summary>
        private void MergeBackfillData(string parameterDisplayName, List<ParameterDataPoint> backfillData)
        {
            try
            {
                var newChartPoints = new List<ChartDataPoint>();
                var tolerance = TimeSpan.FromMilliseconds(100); // 时间容差

                // 处理回溯数据
                foreach (var dataPoint in backfillData.OrderBy(d => d.Timestamp))
                {
                    var existingPoint = ChartData.FirstOrDefault(cd =>
                        Math.Abs((cd.Timestamp - dataPoint.Timestamp).TotalMilliseconds) < tolerance.TotalMilliseconds);

                    if (existingPoint != null)
                    {
                        // 添加到现有数据点
                        existingPoint.ParameterValues[parameterDisplayName] = dataPoint.Value;
                        _logger.LogDebug("更新现有数据点: 时间={Time}, 参数={Param}, 值={Value}",
                            dataPoint.Timestamp.ToString("HH:mm:ss.fff"), parameterDisplayName, dataPoint.Value);
                    }
                    else
                    {
                        // 创建新的数据点
                        var newPoint = new ChartDataPoint
                        {
                            Timestamp = dataPoint.Timestamp
                        };
                        newPoint.ParameterValues[parameterDisplayName] = dataPoint.Value;
                        newChartPoints.Add(newPoint);

                        _logger.LogDebug("创建新数据点: 时间={Time}, 参数={Param}, 值={Value}",
                            dataPoint.Timestamp.ToString("HH:mm:ss.fff"), parameterDisplayName, dataPoint.Value);
                    }
                }

                // 批量添加新数据点
                foreach (var newPoint in newChartPoints)
                {
                    ChartData.Add(newPoint);
                }

                // *** 关键：重新排序所有数据点 ***
                var sortedData = ChartData.OrderBy(cd => cd.Timestamp).ToList();
                ChartData.Clear();
                foreach (var point in sortedData)
                {
                    ChartData.Add(point);
                }

                _logger.LogDebug("数据合并完成，新增 {NewCount} 个数据点，总计 {TotalCount} 个",
                    newChartPoints.Count, ChartData.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "合并回溯数据失败");
            }
        }

        /// <summary>
        /// 强制重建图表系列
        /// </summary>
        private void ForceRebuildChartSeries()
        {
            try
            {
                _logger.LogInformation("强制重建图表系列");

                // 清空参数缓存，强制重建
                _lastEnabledParameterKeys.Clear();
                _seriesValuesCache.Clear();
                _lastYAxisRanges.Clear();

                // *** 新增：确保增量更新能正常工作 ***
                ResetIncrementalUpdateTimestamp();

                // 重建图表系列
                UpdateChartSeries();

                _logger.LogInformation("图表系列重建完成，当前数据点: {Count}", ChartData.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "强制重建图表系列失败");
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

                // 从现有图表数据中移除该参数
                if (IsProcessRunning && ShouldUpdateChart)
                {
                    RemoveParameterFromChartData(parameter.DisplayName);
                    ForceRebuildChartSeries();
                }

                LoadMonitoredParameters();

                _logger.LogInformation("移除监控参数: {DisplayName}", parameter.DisplayName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "移除监控参数失败: {DisplayName}", parameter.DisplayName);
            }
        }

        /// <summary>
        /// 从图表数据中移除指定参数
        /// </summary>
        private void RemoveParameterFromChartData(string parameterDisplayName)
        {
            try
            {
                foreach (var dataPoint in ChartData)
                {
                    dataPoint.ParameterValues.Remove(parameterDisplayName);
                }

                _logger.LogDebug("从图表数据中移除参数: {DisplayName}", parameterDisplayName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "从图表数据中移除参数失败: {DisplayName}", parameterDisplayName);
            }
        }

        private void ToggleConfiguration()
        {
            IsConfigurationVisible = !IsConfigurationVisible;
        }

        private void ToggleParameter(MonitoredParameter parameter)
        {
            if (parameter != null)
            {
                var oldEnabled = parameter.IsEnabled;
                parameter.IsEnabled = !parameter.IsEnabled;

                _monitoringService.SetParameterEnabled(
                    parameter.DeviceId,
                    parameter.CommandName,
                    parameter.ParameterName,
                    parameter.IsEnabled);

                // 如果流程正在运行，需要特殊处理
                if (IsProcessRunning && ShouldUpdateChart)
                {
                    if (parameter.IsEnabled && !oldEnabled)
                    {
                        // 从禁用变为启用：需要回溯数据
                        _logger.LogInformation("运行时启用参数，执行数据回溯: {DisplayName}", parameter.DisplayName);

                        var availableParam = new AvailableParameter
                        {
                            DeviceId = parameter.DeviceId,
                            DeviceName = parameter.DeviceName,
                            CommandName = parameter.CommandName,
                            ParameterName = parameter.ParameterName,
                            DisplayName = parameter.DisplayName
                        };

                        BackfillDataForNewParameter(availableParam);
                    }
                    else if (!parameter.IsEnabled && oldEnabled)
                    {
                        // 从启用变为禁用：移除数据不是必须的，但重建系列
                        _logger.LogInformation("运行时禁用参数: {DisplayName}", parameter.DisplayName);
                    }

                    ForceRebuildChartSeries();
                }
                else
                {
                    // 流程未运行时，正常更新
                    UpdateChartSeries();
                }

                _logger.LogDebug("切换参数状态: {DisplayName} = {Enabled}",
                    parameter.DisplayName, parameter.IsEnabled);
            }
        }
        /// <summary>
        /// *** 新增：强制重建图表 - 用于解决数据绑定问题 ***
        /// </summary>
        public void ForceRebuildChart()
        {
            try
            {
                _logger.LogInformation("强制重建整个图表");

                // 1. 清空所有缓存
                _lastEnabledParameterKeys.Clear();
                _seriesValuesCache.Clear();
                _lastYAxisRanges.Clear();

                // 2. 重建系列
                UpdateChartSeries();

                // 3. 强制UI更新
                RaisePropertyChanged(nameof(ChartSeries));
                RaisePropertyChanged(nameof(YAxes));
                RaisePropertyChanged(nameof(HasData));
                RaisePropertyChanged(nameof(DataPointsCount));

                _logger.LogInformation("图表强制重建完成");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "强制重建图表失败");
            }
        }
        private void OnParameterDataUpdated(object sender, ParameterDataUpdatedEventArgs e)
        {
            try
            {
                _logger.LogDebug("收到参数数据更新事件: 设备={DeviceId}, 命令={CommandName}, 时间={UpdateTime}, 影响参数={Count}",
                    e.DeviceId, e.CommandName, e.UpdateTime.ToString("HH:mm:ss.fff"), e.AffectedParametersCount);

                // *** 关键修复：确保事件处理正常工作 ***
                if (!IsProcessRunning)
                {
                    // 如果收到数据更新但流程状态为未运行，可能需要更新状态
                    _logger.LogWarning("收到数据更新但流程状态为未运行，检查状态");
                    CheckAndUpdateProcessStatus();
                }

                if (IsRealTimeMode && ShouldUpdateChart)
                {
                    // 标记需要更新
                    LastUpdateTime = DateTime.Now;
                    RaisePropertyChanged(nameof(StatusText));

                    // 如果定时器未运行，重新启动
                    if (_refreshTimer != null && !_refreshTimer.IsEnabled && IsProcessRunning)
                    {
                        _logger.LogInformation("数据更新事件触发，重新启动定时器");
                        StartChartAutoUpdate();
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "处理参数数据更新事件失败");
            }
        }


        private DateTime _lastChartUpdateTime = DateTime.MinValue;
        private readonly Dictionary<string, ChartValues<ChartDataPoint>> _seriesValuesCache = new Dictionary<string, ChartValues<ChartDataPoint>>();

        private void UpdateChartData()
        {
            if (!ShouldUpdateChart)
            {
                _logger.LogDebug("图表更新被禁用，跳过 UpdateChartData");
                return;
            }

            try
            {
                var enabledParameters = MonitoredParameters.Where(p => p.IsEnabled).ToList();

                if (!enabledParameters.Any())
                {
                    _logger.LogDebug("没有启用的参数，清空图表数据");
                    ClearChartDataOnly();
                    return;
                }

                var sinceTime = _lastChartUpdateTime;

                if (sinceTime == DateTime.MinValue)
                {
                    if (ChartData.Any())
                    {
                        sinceTime = ChartData.Max(cd => cd.Timestamp);
                        _logger.LogDebug("使用图表中最新数据时间作为起始点: {SinceTime}",
                            sinceTime.ToString("HH:mm:ss.fff"));
                    }
                    else
                    {
                        sinceTime = DateTime.Now.AddMinutes(-5);
                        _logger.LogDebug("无图表数据，使用5分钟前作为起始点: {SinceTime}",
                            sinceTime.ToString("HH:mm:ss.fff"));
                    }
                }

                var newDataPoints = GetNewDataPointsSince(sinceTime);

                if (!newDataPoints.Any())
                {
                    _logger.LogDebug("没有新的数据点，当前查询时间: {SinceTime}",
                        sinceTime.ToString("HH:mm:ss.fff"));
                    return;
                }

                _logger.LogDebug("发现 {Count} 个新数据点，进行增量更新", newDataPoints.Count);

                // *** 关键修复：强制刷新图表系列数据 ***
                bool chartNeedsRebuild = false;

                foreach (var newPoint in newDataPoints)
                {
                    AddDataPointToChart(newPoint, enabledParameters);

                    // *** 新增：强制通知LiveCharts数据变化 ***
                    ForceChartRefresh(newPoint, enabledParameters);
                }

                DataPointsCount = ChartData.Count;
                LastUpdateTime = DateTime.Now;

                if (newDataPoints.Any())
                {
                    _lastChartUpdateTime = newDataPoints.Max(p => p.Timestamp);
                    _logger.LogDebug("更新最后更新时间为: {LastUpdate}",
                        _lastChartUpdateTime.ToString("HH:mm:ss.fff"));
                }

                LimitChartDataPoints();
                UpdateYAxesRangeIfNeeded(enabledParameters);

                // *** 关键修复：强制属性变更通知 ***
                RaisePropertyChanged(nameof(ChartSeries));
                RaisePropertyChanged(nameof(StatusText));
                RaisePropertyChanged(nameof(HasData));

                HasData = ChartData.Any();

                _logger.LogDebug("增量更新完成，当前数据点: {Count}", DataPointsCount);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "增量更新图表数据失败");
            }
        }

        /// <summary>
        /// *** 新增：强制图表刷新 ***
        /// </summary>
        private void ForceChartRefresh(ChartDataPoint newPoint, List<MonitoredParameter> enabledParameters)
        {
            try
            {
                if (ChartSeries == null) return;

                foreach (var parameter in enabledParameters)
                {
                    if (newPoint.ParameterValues.ContainsKey(parameter.DisplayName))
                    {
                        var series = ChartSeries.OfType<LineSeries>()
                            .FirstOrDefault(s => s.Title == parameter.DisplayName);

                        if (series?.Values is ChartValues<ChartDataPoint> values)
                        {
                            // *** 关键：使用 Add 方法触发 LiveCharts 的变更通知 ***
                            values.Add(newPoint);

                            _logger.LogDebug("强制刷新系列: {Series}, 新数据点时间: {Time}, 值: {Value}",
                                parameter.DisplayName,
                                newPoint.Timestamp.ToString("HH:mm:ss.fff"),
                                newPoint.ParameterValues[parameter.DisplayName]);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "强制图表刷新失败");
            }
        }

        /// <summary>
        /// 获取指定时间之后的新数据点
        /// </summary>
        private List<ChartDataPoint> GetNewDataPointsSince(DateTime sinceTime)
        {
            var newPoints = new List<ChartDataPoint>();
            var enabledParameters = MonitoredParameters.Where(p => p.IsEnabled).ToList();

            try
            {
                _logger.LogDebug("获取自 {SinceTime} 以来的新数据点", sinceTime.ToString("HH:mm:ss.fff"));

                // 获取所有启用参数的数据
                var allNewData = new Dictionary<string, List<ParameterDataPoint>>();

                foreach (var param in enabledParameters)
                {
                    var data = _monitoringService.GetParameterData(param.DeviceId, param.CommandName, param.ParameterName);

                    // *** 修复：使用更宽松的时间过滤 ***
                    var newData = data.Where(d => d.Timestamp > sinceTime)
                                     .OrderBy(d => d.Timestamp)
                                     .ToList();

                    if (newData.Any())
                    {
                        allNewData[param.Key] = newData;
                        _logger.LogDebug("参数 {Param} 有 {Count} 个新数据点",
                            param.DisplayName, newData.Count);
                    }
                }

                if (!allNewData.Any())
                {
                    _logger.LogDebug("没有找到任何新数据点");
                    return newPoints;
                }

                // 获取所有新的时间点
                var newTimestamps = allNewData.Values
                    .SelectMany(data => data.Select(d => d.Timestamp))
                    .Distinct()
                    .OrderBy(t => t)
                    .ToList();

                _logger.LogDebug("发现 {Count} 个新的时间点", newTimestamps.Count);

                // 构建新的图表数据点
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
                                .OrderBy(d => Math.Abs((d.Timestamp - timestamp).TotalMilliseconds))
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

                _logger.LogDebug("构建了 {Count} 个新的图表数据点", newPoints.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取新数据点失败");
            }

            return newPoints;
        }


        /// <summary>
        /// 将单个数据点添加到图表
        /// </summary>
        private void AddDataPointToChart(ChartDataPoint dataPoint, List<MonitoredParameter> enabledParameters)
        {
            try
            {
                // 添加到ChartData集合
                ChartData.Add(dataPoint);

                // *** 注意：不在这里添加到系列，由 ForceChartRefresh 处理 ***
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "添加数据点到图表失败");
            }
        }

        /// <summary>
        /// 限制图表数据点数量
        /// </summary>
        private void LimitChartDataPoints()
        {
            const int maxPoints = 1000;

            if (ChartData.Count > maxPoints)
            {
                var removeCount = ChartData.Count - maxPoints;

                // 从ChartData中移除旧数据
                for (int i = 0; i < removeCount; i++)
                {
                    ChartData.RemoveAt(0);
                }

                // 从所有系列中移除旧数据
                if (ChartSeries != null)
                {
                    foreach (var series in ChartSeries.OfType<LineSeries>())
                    {
                        if (series.Values is ChartValues<ChartDataPoint> values && values.Count > maxPoints)
                        {
                            for (int i = 0; i < removeCount && values.Count > 0; i++)
                            {
                                values.RemoveAt(0);
                            }
                        }
                    }
                }

                _logger.LogDebug("移除了 {Count} 个旧数据点，保持最大 {Max} 个点", removeCount, maxPoints);
            }
        }

        /// <summary>
        /// 只在数值范围显著变化时更新Y轴
        /// </summary>
        private DateTime _lastYAxisUpdate = DateTime.MinValue;
        private readonly Dictionary<string, (double min, double max)> _lastYAxisRanges = new Dictionary<string, (double, double)>();

        private void UpdateYAxesRangeIfNeeded(List<MonitoredParameter> enabledParameters)
        {
            try
            {
                // 每隔一定时间或范围变化显著时才更新Y轴
                if (DateTime.Now - _lastYAxisUpdate < TimeSpan.FromSeconds(2))
                    return;

                bool needsUpdate = false;

                foreach (var parameter in enabledParameters)
                {
                    var values = GetParameterValues(parameter);
                    if (!values.Any()) continue;

                    var currentRange = CalculateAxisRange(values);
                    var key = parameter.DisplayName;

                    if (!_lastYAxisRanges.TryGetValue(key, out var lastRange))
                    {
                        needsUpdate = true;
                        _lastYAxisRanges[key] = currentRange;
                    }
                    else
                    {
                        // 检查范围变化是否显著（超过10%）
                        var rangeChange = Math.Max(
                            Math.Abs(currentRange.min - lastRange.min) / Math.Abs(lastRange.min + 1),
                            Math.Abs(currentRange.max - lastRange.max) / Math.Abs(lastRange.max + 1)
                        );

                        if (rangeChange > 0.1) // 10%的变化阈值
                        {
                            needsUpdate = true;
                            _lastYAxisRanges[key] = currentRange;
                        }
                    }
                }

                if (needsUpdate)
                {
                    UpdateYAxes(enabledParameters);
                    _lastYAxisUpdate = DateTime.Now;
                    _logger.LogDebug("Y轴范围更新：检测到显著变化");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "更新Y轴范围失败");
            }
        }

        public void Dispose()
        {
            try
            {
                _refreshTimer?.Stop();

                // *** 重要：确保取消事件订阅 ***
                if (_monitoringService != null)
                {
                    _monitoringService.ParameterDataUpdated -= OnParameterDataUpdated;
                }

                try
                {
                    _eventAggregator?.GetEvent<FlowExecutionStateChangedEvent>()?.Unsubscribe(OnFlowStateChanged);
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
    }
}
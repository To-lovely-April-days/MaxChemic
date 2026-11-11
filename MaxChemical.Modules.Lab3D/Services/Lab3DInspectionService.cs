using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using MaxChemical.Data.Models;
using MaxChemical.Data.Repositories;
using MaxChemical.Modules.Lab3D.Models;

namespace MaxChemical.Modules.Lab3D.Services;

public sealed class Lab3DInspectionService : ILab3DInspectionService
{
    private static readonly TimeSpan InspectionInterval = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan ManualInspectionPause = TimeSpan.FromSeconds(20);
    private readonly ILab3DRealtimeService _realtimeService;
    private readonly IExperimentDataRepository _experimentRepository;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Dictionary<string, DeviceSamples> _samples = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _lastDataTimes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Application? _application;
    private IReadOnlyList<LabDevice> _devices = [];
    private IReadOnlyList<Lab3DDeviceSnapshot> _snapshots = [];
    private IReadOnlyList<DeviceUsageStatistics> _deviceUsage = [];
    private ExperimentStatistics? _statistics;
    private DateTime _nextInspection = DateTime.MaxValue;
    private DateTime _manualInspectionUntil = DateTime.MinValue;
    private DateTime _nextStatisticsRefresh = DateTime.MinValue;
    private string? _manualTargetId;
    private int _inspectionIndex;
    private int _inspectionCount;
    private bool _statisticsRefreshing;
    private bool _active;
    private bool _disposed;

    public Lab3DInspectionService(
        ILab3DRealtimeService realtimeService,
        IExperimentDataRepository experimentRepository)
    {
        _realtimeService = realtimeService ?? throw new ArgumentNullException(nameof(realtimeService));
        _experimentRepository = experimentRepository ?? throw new ArgumentNullException(nameof(experimentRepository));
        _snapshots = _realtimeService.GetSnapshots();
        _realtimeService.SnapshotChanged += OnRealtimeSnapshotChanged;
        _timer.Tick += OnTick;
        _application = Application.Current;
        if (_application is not null) _application.Exit += OnApplicationExit;
    }

    public event EventHandler<Lab3DInspectionChangedEventArgs>? Changed;

    public Lab3DInspectionSnapshot Current { get; private set; } = Lab3DInspectionSnapshot.Empty;

    public void SetDevices(IEnumerable<LabDevice> devices)
    {
        if (_disposed) return;
        _devices = (devices ?? []).Where(device => device is not null).ToArray();
        var activeIds = _devices.Select(device => device.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string id in _samples.Keys.Where(id => !activeIds.Contains(id)).ToArray()) _samples.Remove(id);
        foreach (string id in _lastDataTimes.Keys.Where(id => !activeIds.Contains(id)).ToArray()) _lastDataTimes.Remove(id);
        if (_manualTargetId is not null && !activeIds.Contains(_manualTargetId)) _manualTargetId = null;
        if (_devices.Count == 0)
        {
            Current = Lab3DInspectionSnapshot.Empty;
            Changed?.Invoke(this, new Lab3DInspectionChangedEventArgs(Current));
            return;
        }

        if (Current.TargetId is null || !activeIds.Contains(Current.TargetId))
        {
            int index = _manualTargetId is null ? 0 : _devices.ToList().FindIndex(device => device.Id == _manualTargetId);
            Inspect(_devices[Math.Max(0, index)], true);
        }
        else Analyze();
    }

    public void SelectDevice(LabDevice? device)
    {
        if (_disposed) return;
        _manualTargetId = device?.Id;
        _manualInspectionUntil = device is null ? DateTime.MinValue : DateTime.UtcNow.Add(ManualInspectionPause);
        if (device is not null)
        {
            _inspectionIndex = _devices.ToList().FindIndex(item => item.Id == device.Id);
            Inspect(device, true);
        }
    }

    public Lab3DTrendSeries GetTrend(string slotId)
    {
        if (!_samples.TryGetValue(slotId, out var samples)) return Lab3DTrendSeries.Empty;
        var primary = samples.Primary.ToArray();
        var secondary = samples.Secondary.ToArray();
        return new Lab3DTrendSeries(primary, secondary, GetRangeText(primary), GetRangeText(secondary));
    }

    public void SetActive(bool active)
    {
        if (_disposed || _active == active) return;
        _active = active;
        if (!active)
        {
            _timer.Stop();
            return;
        }

        _snapshots = _realtimeService.GetSnapshots();
        _nextInspection = DateTime.UtcNow.Add(InspectionInterval);
        _timer.Start();
        if (_devices.FirstOrDefault(device => device.Id == (_manualTargetId ?? Current.TargetId)) is LabDevice selected)
            Inspect(selected, true);
        else AdvanceInspection();
        _ = RefreshStatisticsAsync();
    }

    private void OnRealtimeSnapshotChanged(object? sender, Lab3DRealtimeSnapshotEventArgs e)
    {
        if (_disposed || !_active) return;
        if (Application.Current?.Dispatcher.CheckAccess() == false)
        {
            Application.Current.Dispatcher.BeginInvoke(new Action(() => OnRealtimeSnapshotChanged(sender, e)));
            return;
        }

        _snapshots = e.Devices;
        CaptureSamples();
        Analyze();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (_disposed || !_active) return;
        _snapshots = _realtimeService.GetSnapshots();
        CaptureSamples();
        if (DateTime.UtcNow >= _nextStatisticsRefresh) _ = RefreshStatisticsAsync();
        if (_devices.Count == 0)
        {
            if (Current.TargetId is not null) SetDevices([]);
            return;
        }

        if (DateTime.UtcNow < _manualInspectionUntil && _devices.Any(device => device.Id == _manualTargetId))
        {
            Analyze();
            return;
        }

        _manualTargetId = null;
        if (DateTime.UtcNow >= _nextInspection || Current.TargetId is null) AdvanceInspection();
        else Analyze();
    }

    private void AdvanceInspection()
    {
        if (_devices.Count == 0) return;
        _inspectionIndex = (_inspectionIndex + 1) % _devices.Count;
        Inspect(_devices[_inspectionIndex], true);
    }

    private void Inspect(LabDevice device, bool completedInspection)
    {
        if (completedInspection) _inspectionCount++;
        _nextInspection = DateTime.UtcNow.Add(InspectionInterval);
        Current = Analyze(device);
        Changed?.Invoke(this, new Lab3DInspectionChangedEventArgs(Current));
    }

    private void Analyze()
    {
        var target = _devices.FirstOrDefault(device => device.Id == Current.TargetId);
        if (target is null) return;
        Current = Analyze(target);
        Changed?.Invoke(this, new Lab3DInspectionChangedEventArgs(Current));
    }

    private Lab3DInspectionSnapshot Analyze(LabDevice device)
    {
        DateTime now = DateTime.Now;
        var snapshot = _snapshots.FirstOrDefault(item => item.SlotId == device.Id);
        var notices = new List<Lab3DInspectionNotice>();
        string status = "状态正常";
        var primary = Parse(snapshot?.Primary.Value);
        var secondary = Parse(snapshot?.Secondary.Value);

        if (string.IsNullOrWhiteSpace(snapshot?.SourceDeviceId))
        {
            status = "等待绑定";
            AddNotice(notices, "warning", "尚未绑定主程序设备，实时巡检已暂停。请先完成设备绑定。", now);
        }
        else if (!snapshot.IsConnected)
        {
            status = "等待连接";
            AddNotice(notices, "warning", "主程序设备未连接，实时分析暂停；请检查通信状态。", now);
        }
        else
        {
            if (primary is null)
            {
                status = "等待数据";
                AddNotice(notices, "notice", $"已连接，等待 {snapshot.Primary.Name} 的有效采样。", now);
            }
            if (secondary is null)
                AddNotice(notices, "notice", $"等待 {snapshot.Secondary.Name} 的有效采样。", now);
            if (!snapshot.HasFreshData && _lastDataTimes.TryGetValue(device.Id, out DateTime lastUpdate))
                AddNotice(notices, "warning", $"实时数据已超过 10 秒未更新，最后采样：{lastUpdate:HH:mm:ss}。", now);

            if (primary is not null && secondary is not null
                && IsSetpoint(snapshot.Secondary.Name)
                && string.Equals(snapshot.Primary.Unit, snapshot.Secondary.Unit, StringComparison.OrdinalIgnoreCase))
            {
                status = "数据观察";
                AddNotice(notices, "notice",
                    $"实测与设定相差 {Math.Abs(primary.Value - secondary.Value):0.##} {snapshot.Primary.Unit}，请结合工艺范围判断。", now);
            }
        }

        var trend = GetTrend(device.Id);
        if (trend.Primary.Count > 1)
        {
            double range = trend.Primary.Max(point => point.Value) - trend.Primary.Min(point => point.Value);
            AddNotice(notices, "normal", $"近 60 秒主参数波动范围：{range:0.##} {snapshot?.Primary.Unit ?? string.Empty}。", now);
        }
        else if (primary is not null)
            AddNotice(notices, "normal", "已获取实时主参数，正在积累近 60 秒曲线。", now);

        var usage = _deviceUsage.FirstOrDefault(item => MatchesDevice(item, device));
        if (usage is null || usage.UsageCount == 0)
            AddNotice(notices, "notice", "该设备暂无实验记录，历史成功率暂不可计算。", now);
        else
            AddNotice(notices, "normal", $"累计参与 {usage.UsageCount} 次实验，历史成功率 {usage.SuccessRate:F1}%。", now);

        int boundCount = _snapshots.Count(item => !string.IsNullOrWhiteSpace(item.SourceDeviceId));
        int coveredCount = _snapshots.Count(item => !string.IsNullOrWhiteSpace(item.SourceDeviceId)
            && item.IsConnected && item.HasFreshData);
        int alertCount = notices.Count(notice => notice.Level == "warning");
        if (alertCount > 0) status = "需要关注";

        return new Lab3DInspectionSnapshot(
            device.Id,
            device.Name,
            status,
            GetTask(device.Kind),
            _inspectionCount,
            alertCount,
            boundCount == 0 ? null : coveredCount * 100.0 / boundCount,
            _statistics?.TotalExperiments > 0 ? _statistics.SuccessRate : null,
            usage?.UsageCount ?? 0,
            usage?.UsageCount > 0 ? usage.SuccessRate : null,
            notices.Take(5).ToArray(),
            now);
    }

    private void CaptureSamples()
    {
        DateTime now = DateTime.Now;
        foreach (var snapshot in _snapshots)
        {
            if (!snapshot.IsConnected) continue;
            if (snapshot.HasFreshData || Parse(snapshot.Primary.Value) is not null || Parse(snapshot.Secondary.Value) is not null)
                _lastDataTimes[snapshot.SlotId] = snapshot.LastUpdateTime ?? now;
            if (!_samples.TryGetValue(snapshot.SlotId, out var samples))
            {
                samples = new DeviceSamples();
                _samples.Add(snapshot.SlotId, samples);
            }
            AddSample(samples.Primary, Parse(snapshot.Primary.Value), snapshot.LastUpdateTime ?? now);
            AddSample(samples.Secondary, Parse(snapshot.Secondary.Value), snapshot.LastUpdateTime ?? now);
        }
        foreach (var samples in _samples.Values)
        {
            TrimSamples(samples.Primary, now);
            TrimSamples(samples.Secondary, now);
        }
    }

    private static void AddSample(List<Lab3DParameterPoint> samples, double? value, DateTime timestamp)
    {
        if (value is null) return;
        if (timestamp > DateTime.Now.AddSeconds(2)) timestamp = DateTime.Now;
        if (samples.Count > 0 && samples[^1].Timestamp == timestamp && samples[^1].Value.Equals(value.Value)) return;
        samples.Add(new Lab3DParameterPoint(timestamp, value.Value));
        if (samples.Count > 120) samples.RemoveRange(0, samples.Count - 120);
    }

    private static void TrimSamples(List<Lab3DParameterPoint> samples, DateTime now) =>
        samples.RemoveAll(point => now - point.Timestamp > TimeSpan.FromSeconds(60));

    private async Task RefreshStatisticsAsync()
    {
        if (_statisticsRefreshing || _disposed) return;
        _statisticsRefreshing = true;
        _nextStatisticsRefresh = DateTime.UtcNow.AddSeconds(30);
        try
        {
            var result = await Task.Run(async () => (
                Statistics: await _experimentRepository.GetStatisticsAsync(),
                Usage: await _experimentRepository.GetDeviceUsageStatisticsAsync()));
            if (_disposed) return;
            _statistics = result.Statistics;
            _deviceUsage = result.Usage;
            Analyze();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"刷新 3D 巡检实验统计失败: {ex.Message}");
        }
        finally
        {
            _statisticsRefreshing = false;
        }
    }

    private static bool MatchesDevice(DeviceUsageStatistics usage, LabDevice device) =>
        (device.SourceDeviceId is not null && usage.DeviceId.Equals(device.SourceDeviceId, StringComparison.OrdinalIgnoreCase))
        || usage.DeviceId.Equals(device.Id, StringComparison.OrdinalIgnoreCase)
        || usage.DeviceName.Equals(device.Name, StringComparison.OrdinalIgnoreCase);

    private static double? Parse(string? value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double invariant)
            ? invariant
            : double.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out double current) ? current : null;

    private static bool IsSetpoint(string? name) =>
        name?.Contains("设定", StringComparison.OrdinalIgnoreCase) == true
        || name?.Contains("set", StringComparison.OrdinalIgnoreCase) == true;

    private static string GetRangeText(IReadOnlyList<Lab3DParameterPoint> points) =>
        points.Count == 0 ? string.Empty : $"{points.Min(point => point.Value):0.##} ~ {points.Max(point => point.Value):0.##}";

    private static string GetTask(string kind) => kind switch
    {
        "Temperature" => "温控与降温曲线分析",
        "Reactor" => "压力曲线稳态判定",
        "Chiller" => "换热效率评估",
        "Climate" => "温控偏差诊断",
        "Stirrer" => "转速与扭矩稳定性",
        "Meter" => "测量数据完整性",
        _ => "综合数据分析"
    };

    private static void AddNotice(ICollection<Lab3DInspectionNotice> notices, string level, string text, DateTime timestamp) =>
        notices.Add(new Lab3DInspectionNotice(level, text, timestamp));

    private void OnApplicationExit(object? sender, ExitEventArgs e) => Dispose();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _realtimeService.SnapshotChanged -= OnRealtimeSnapshotChanged;
        if (_application is not null) _application.Exit -= OnApplicationExit;
        _samples.Clear();
        _devices = [];
        _snapshots = [];
        Changed = null;
    }

    private sealed class DeviceSamples
    {
        public List<Lab3DParameterPoint> Primary { get; } = [];
        public List<Lab3DParameterPoint> Secondary { get; } = [];
    }
}

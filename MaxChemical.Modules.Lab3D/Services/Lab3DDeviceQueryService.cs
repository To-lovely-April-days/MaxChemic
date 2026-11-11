using MaxChemical.Modules.Lab3D.Models;

namespace MaxChemical.Modules.Lab3D.Services;

public sealed class Lab3DDeviceQueryService : ILab3DDeviceQueryService
{
    private static readonly BuiltInDevice[] BuiltInDevices =
    [
        new("D01", "高低温循环一体机", "物料温度", "℃", "出口温度", "℃"),
        new("D02", "微型反应器", "出口压力", "MPa", "实际温度", "℃"),
        new("D03", "智能磁力搅拌器", "速度测量值", "RPM", "内置温度", "℃"),
        new("D04", "低温冷却循环器", "液体温度", "℃", "设定温度", "℃"),
        new("D05", "恒温培养箱", "箱内温度", "℃", "温度设定", "℃"),
        new("D06", "蓝牙多参数检测笔", "测量值", "", "温度", "℃")
    ];

    private readonly ILab3DRealtimeService _realtimeService;
    private readonly DeviceLibraryService _deviceLibrary = new();
    private readonly DeletedDeviceStore _deletedDevices = new();

    public Lab3DDeviceQueryService(ILab3DRealtimeService realtimeService)
    {
        _realtimeService = realtimeService ?? throw new ArgumentNullException(nameof(realtimeService));
    }

    public IReadOnlyList<Lab3DDeviceQueryResult> GetDevices()
    {
        _realtimeService.RefreshDeviceLibrary();
        var snapshots = _realtimeService.GetSnapshots();
        var deletedIds = _deletedDevices.Load();
        var devices = BuiltInDevices
            .Where(device => !deletedIds.Contains(device.SlotId))
            .Select(device => CreateBuiltInResult(device, snapshots))
            .ToList();

        foreach (var entry in _deviceLibrary.LoadEntries())
        {
            var builtInDevice = devices.FirstOrDefault(device =>
                !device.IsCustom && string.Equals(device.SlotId, entry.Id, StringComparison.OrdinalIgnoreCase));
            if (builtInDevice is not null) devices.Remove(builtInDevice);
            devices.Add(CreateCustomResult(entry, snapshots));
        }

        return devices;
    }

    private Lab3DDeviceQueryResult CreateBuiltInResult(
        BuiltInDevice device,
        IReadOnlyList<Lab3DDeviceSnapshot> snapshots)
    {
        var snapshot = snapshots.FirstOrDefault(item =>
            string.Equals(item.SlotId, device.SlotId, StringComparison.OrdinalIgnoreCase));
        if (snapshot is null)
        {
            return new Lab3DDeviceQueryResult(
                device.SlotId,
                device.Name,
                "未绑定",
                false,
                null,
                false,
                false,
                new Lab3DParameterSnapshot(device.PrimaryName, device.PrimaryUnit, string.Empty),
                new Lab3DParameterSnapshot(device.SecondaryName, device.SecondaryUnit, string.Empty),
                null,
                []);
        }

        IReadOnlyList<Lab3DParameterSample> samples = string.IsNullOrWhiteSpace(snapshot.SourceDeviceId)
            ? []
            : _realtimeService.GetLatestSamples(snapshot.SourceDeviceId);
        return new Lab3DDeviceQueryResult(
            device.SlotId,
            device.Name,
            snapshot.Protocol,
            false,
            snapshot.SourceDeviceId,
            snapshot.IsConnected,
            snapshot.HasFreshData,
            snapshot.Primary,
            snapshot.Secondary,
            snapshot.LastUpdateTime,
            samples);
    }

    private Lab3DDeviceQueryResult CreateCustomResult(
        DeviceLibraryEntry entry,
        IReadOnlyList<Lab3DDeviceSnapshot> snapshots)
    {
        var snapshot = snapshots.FirstOrDefault(item =>
            string.Equals(item.SlotId, entry.Id, StringComparison.OrdinalIgnoreCase));
        if (snapshot is null)
        {
            return new Lab3DDeviceQueryResult(
                entry.Id,
                entry.Name,
                entry.Protocol,
                true,
                null,
                false,
                false,
                new Lab3DParameterSnapshot(entry.PrimaryName, entry.PrimaryUnit, string.Empty),
                new Lab3DParameterSnapshot(entry.SecondaryName, entry.SecondaryUnit, string.Empty),
                null,
                []);
        }

        IReadOnlyList<Lab3DParameterSample> samples = string.IsNullOrWhiteSpace(snapshot.SourceDeviceId)
            ? []
            : _realtimeService.GetLatestSamples(snapshot.SourceDeviceId);
        return new Lab3DDeviceQueryResult(
            entry.Id,
            entry.Name,
            snapshot.Protocol,
            true,
            snapshot.SourceDeviceId,
            snapshot.IsConnected,
            snapshot.HasFreshData,
            snapshot.Primary,
            snapshot.Secondary,
            snapshot.LastUpdateTime,
            samples);
    }

    private sealed record BuiltInDevice(
        string SlotId,
        string Name,
        string PrimaryName,
        string PrimaryUnit,
        string SecondaryName,
        string SecondaryUnit);
}

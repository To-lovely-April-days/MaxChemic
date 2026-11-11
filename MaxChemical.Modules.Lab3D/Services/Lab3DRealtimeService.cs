using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using DevicePlugins.Devices;
using MaxChemical.Core;
using MaxChemical.Modules.Designer.Event;
using MaxChemical.Modules.Designer.ViewModels;
using MaxChemical.Modules.Lab3D.Models;
using Prism.Events;
using DeviceBase = DevicePlugins.Devices.Device;

namespace MaxChemical.Modules.Lab3D.Services;

public sealed class Lab3DRealtimeService : ILab3DRealtimeService
{
    private static readonly TimeSpan FreshDataWindow = TimeSpan.FromSeconds(10);

    private static readonly SlotDefinition[] DefaultSlotDefinitions =
    [
        new("D01", ["HighLowTemperature_ModbusTCP"], "MaterialTemperature", "物料温度", "℃", "OutletTemperature", "出口温度", "℃"),
        new("D02", ["MicroReactor_ModbusTCP"], "PT0112_Pressure", "出口压力", "MPa", "TT1002_ActualTemp", "实际温度", "℃"),
        new("D03", ["MagneticStirrer_ModbusRTU"], "SpeedMeasured", "速度测量值", "RPM", "InternalTemperature", "内置温度", "℃"),
        new("D04", ["Chiller_ModbusRTU"], "LIQUID_TEMP", "液体温度", "℃", "SET_TEMP", "设定温度", "℃"),
        new("D05", ["ClimateChamber_ModbusRTU"], "TEMP_PV", "箱内温度", "℃", "TEMP_SET", "温度设定", "℃"),
        new("D06", ["PenMeter_BLE"], "VALUE", "测量值", "", "TEMP", "温度", "℃")
    ];

    private readonly Dictionary<string, SlotDefinition> _slotDefinitions = DefaultSlotDefinitions
        .ToDictionary(slot => slot.SlotId, StringComparer.OrdinalIgnoreCase);
    private readonly IEventAggregator _eventAggregator;
    private readonly object _sync = new();
    private readonly Dictionary<string, BoundDevice> _boundDevices = new();
    private readonly Dictionary<string, SampleValue> _samples = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _lastUpdates = new();
    private readonly DispatcherTimer _refreshTimer;
    private readonly List<Lab3DDeviceBinding> _bindingOverrides;
    private readonly Application? _application;
    private volatile bool _dirty;
    private bool _disposed;

    public Lab3DRealtimeService(IEventAggregator eventAggregator)
    {
        _eventAggregator = eventAggregator ?? throw new ArgumentNullException(nameof(eventAggregator));
        _bindingOverrides = LoadBindingOverrides();
        LoadLibrarySlots();

        _eventAggregator.GetEvent<MonitoringCardUpdateEvent>().Subscribe(OnMonitoringCardUpdate, ThreadOption.BackgroundThread, true);
        _eventAggregator.GetEvent<CanvasDevicesChangedEvent>().Subscribe(OnCanvasDevicesChanged, ThreadOption.PublisherThread, true);
        RefreshCanvasDevices();

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _refreshTimer.Tick += (_, _) =>
        {
            if (_dirty) PublishSnapshot();
        };
        _refreshTimer.Start();

        _application = Application.Current;
        if (_application is not null) _application.Exit += OnApplicationExit;
    }

    public event EventHandler<Lab3DRealtimeSnapshotEventArgs>? SnapshotChanged;

    public IReadOnlyList<Lab3DDeviceSnapshot> GetSnapshots()
    {
        if (_disposed) return [];

        try
        {
            lock (_sync)
            {
                return CreateSnapshots(DateTime.Now);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"读取 3D 实时快照失败: {ex.Message}");
            return [];
        }
    }

    public IReadOnlyList<Lab3DParameterSample> GetLatestSamples(string sourceDeviceId)
    {
        if (_disposed || string.IsNullOrWhiteSpace(sourceDeviceId)) return [];

        try
        {
            string prefix = $"{sourceDeviceId}|";
            lock (_sync)
            {
                DateTime now = DateTime.Now;
                return _samples
                    .Where(pair => pair.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    .Select(pair => new Lab3DParameterSample(
                        pair.Key[prefix.Length..],
                        pair.Value.Value,
                        pair.Value.Unit,
                        pair.Value.Timestamp,
                        now - pair.Value.Timestamp <= FreshDataWindow))
                    .OrderBy(sample => sample.Name, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"读取 3D 实时参数缓存失败: {ex.Message}");
            return [];
        }
    }

    public void SetDevices(IEnumerable<LabDevice> devices)
    {
        if (_disposed) return;

        lock (_sync)
        {
            _slotDefinitions.Clear();
            foreach (var slot in DefaultSlotDefinitions) _slotDefinitions[slot.SlotId] = slot;
            foreach (var device in devices ?? [])
            {
                if (_slotDefinitions.ContainsKey(device.Id)) continue;
                _slotDefinitions[device.Id] = new SlotDefinition(
                    device.Id,
                    [],
                    device.PrimaryName,
                    device.PrimaryName,
                    device.PrimaryUnit,
                    device.SecondaryName,
                    device.SecondaryName,
                    device.SecondaryUnit);
            }
            _dirty = true;
        }

        RefreshCanvasDevices();
    }

    public void RefreshDeviceLibrary()
    {
        if (_disposed) return;
        lock (_sync)
        {
            LoadLibrarySlots();
            _dirty = true;
        }
        RefreshCanvasDevices();
    }

    private void LoadLibrarySlots()
    {
        _slotDefinitions.Clear();
        foreach (var slot in DefaultSlotDefinitions) _slotDefinitions[slot.SlotId] = slot;
        try
        {
            foreach (var entry in new DeviceLibraryService().LoadEntries())
            {
                if (_slotDefinitions.ContainsKey(entry.Id)) continue;
                _slotDefinitions.Add(entry.Id, new SlotDefinition(
                    entry.Id,
                    [],
                    entry.PrimaryName,
                    entry.PrimaryName,
                    entry.PrimaryUnit,
                    entry.SecondaryName,
                    entry.SecondaryName,
                    entry.SecondaryUnit));
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"读取 3D 自定义设备绑定失败: {ex.Message}");
        }
    }

    public void PublishSnapshot()
    {
        if (_disposed) return;

        try
        {
            IReadOnlyList<Lab3DDeviceSnapshot> snapshots;
            lock (_sync)
            {
                snapshots = CreateSnapshots(DateTime.Now);
                _dirty = false;
            }

            SnapshotChanged?.Invoke(this, new Lab3DRealtimeSnapshotEventArgs(snapshots));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"发布 3D 实时快照失败: {ex.Message}");
        }
    }

    private List<Lab3DDeviceSnapshot> CreateSnapshots(DateTime now)
    {
        var snapshots = new List<Lab3DDeviceSnapshot>();
        foreach (var binding in _boundDevices.Values)
        {
            snapshots.Add(CreateSnapshot(binding, now));
        }
        return snapshots;
    }

    private Lab3DDeviceSnapshot CreateSnapshot(BoundDevice binding, DateTime now)
    {
        var device = binding.Device;
        string? sourceDeviceId = binding.SourceDeviceId;
        bool isConnected = device?.IsConnected == true;
        bool primaryFresh = TryGetFreshSample(sourceDeviceId, binding.PrimaryParameter, now, out var primarySample);
        bool secondaryFresh = TryGetFreshSample(sourceDeviceId, binding.SecondaryParameter, now, out var secondarySample);

        var primaryDefinition = GetParameterDefinition(device, binding.PrimaryParameter,
            binding.Slot.PrimaryName, binding.Slot.PrimaryUnit);
        var secondaryDefinition = GetParameterDefinition(device, binding.SecondaryParameter,
            binding.Slot.SecondaryName, binding.Slot.SecondaryUnit);

        var primary = new Lab3DParameterSnapshot(
            primaryDefinition.Name,
            primarySample?.Unit ?? primaryDefinition.Unit,
            isConnected && primaryFresh ? primarySample!.Value : string.Empty);
        var secondary = new Lab3DParameterSnapshot(
            secondaryDefinition.Name,
            secondarySample?.Unit ?? secondaryDefinition.Unit,
            isConnected && secondaryFresh ? secondarySample!.Value : string.Empty);

        DateTime? lastUpdate = null;
        if (sourceDeviceId is not null && _lastUpdates.TryGetValue(sourceDeviceId, out DateTime timestamp)
            && now - timestamp <= FreshDataWindow)
        {
            lastUpdate = timestamp;
        }

        return new Lab3DDeviceSnapshot(
            binding.Slot.SlotId,
            sourceDeviceId,
            isConnected,
            isConnected && (primaryFresh || secondaryFresh),
            GetProtocol(device),
            primary,
            secondary,
            lastUpdate);
    }

    private static (string Name, string Unit) GetParameterDefinition(
        IDevice? device,
        string parameterName,
        string fallbackName,
        string fallbackUnit)
    {
        if (device is DeviceBase deviceBase)
        {
            var parameter = deviceBase.CollectableParameters
                .FirstOrDefault(p => string.Equals(p.Name, parameterName, StringComparison.OrdinalIgnoreCase));
            if (parameter is not null)
                return (parameter.DisplayName, parameter.Unit);
        }
        return (fallbackName, fallbackUnit);
    }

    private bool TryGetFreshSample(string? deviceId, string parameterName, DateTime now, out SampleValue? sample)
    {
        sample = null;
        if (deviceId is null) return false;
        if (!_samples.TryGetValue($"{deviceId}|{parameterName}", out sample)) return false;
        return now - sample.Timestamp <= FreshDataWindow;
    }

    private void OnMonitoringCardUpdate(MonitoringCardUpdateEventArgs args)
    {
        if (_disposed || args?.OutputParameters?.Variables is null || string.IsNullOrWhiteSpace(args.DeviceId)) return;

        try
        {
            lock (_sync)
            {
                DateTime timestamp = args.UpdateTime == default ? DateTime.Now : args.UpdateTime;
                foreach (var parameter in args.OutputParameters.Variables)
                {
                    if (parameter?.Value is null || string.IsNullOrWhiteSpace(parameter.Name)) continue;
                    _samples[$"{args.DeviceId}|{parameter.Name}"] = new SampleValue(
                        FormatValue(parameter.Value),
                        parameter.Unit ?? string.Empty,
                        timestamp);
                }
                _lastUpdates[args.DeviceId] = timestamp;
                _dirty = true;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"处理 3D 实时参数失败: {ex.Message}");
        }
    }

    private void OnCanvasDevicesChanged(List<CanvasDeviceViewModel> devices)
    {
        if (_disposed) return;
        try
        {
            RefreshCanvasDevices(devices);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"刷新 3D 设备绑定失败: {ex.Message}");
        }
    }

    private void RefreshCanvasDevices(List<CanvasDeviceViewModel>? canvasDevices = null)
    {
        try
        {
            if (canvasDevices is null)
            {
                _eventAggregator.GetEvent<RequestCanvasDevicesEvent>().Publish(list => canvasDevices = list);
            }
            canvasDevices ??= [];

            lock (_sync)
            {
                foreach (var binding in _boundDevices.Values)
                {
                    if (binding.Device is not null)
                        binding.Device.ConnectionStatusChanged -= OnConnectionStatusChanged;
                }
                _boundDevices.Clear();

                foreach (var slot in _slotDefinitions.Values)
                {
                    var overrideBinding = _bindingOverrides.FirstOrDefault(b =>
                        string.Equals(b.SlotId, slot.SlotId, StringComparison.OrdinalIgnoreCase));
                    CanvasDeviceViewModel? canvasDevice = null;
                    string primaryParameter = string.IsNullOrWhiteSpace(overrideBinding?.PrimaryParameter)
                        ? slot.PrimaryParameter
                        : overrideBinding.PrimaryParameter;
                    string secondaryParameter = string.IsNullOrWhiteSpace(overrideBinding?.SecondaryParameter)
                        ? slot.SecondaryParameter
                        : overrideBinding.SecondaryParameter;

                    if (overrideBinding is not null)
                    {
                        if (!string.IsNullOrWhiteSpace(overrideBinding.SourceDeviceId))
                        {
                            canvasDevice = canvasDevices.FirstOrDefault(d =>
                                string.Equals(d.DeviceId, overrideBinding.SourceDeviceId, StringComparison.OrdinalIgnoreCase));
                        }
                    }
                    else
                    {
                        var candidates = canvasDevices.Where(d =>
                            slot.DriverTypes.Contains(d.Device?.GetType().Name)).ToList();
                        if (candidates.Count == 1) canvasDevice = candidates[0];
                    }

                    var binding = new BoundDevice(slot, canvasDevice, primaryParameter, secondaryParameter);
                    _boundDevices.Add(slot.SlotId, binding);
                    if (binding.Device is not null)
                        binding.Device.ConnectionStatusChanged += OnConnectionStatusChanged;
                }
                _dirty = true;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"读取画布设备用于 3D 展示失败: {ex.Message}");
        }
    }

    private void OnConnectionStatusChanged(object? sender, DeviceConnectionStatusChangedEventArgs e)
    {
        if (!_disposed) _dirty = true;
    }

    private static string GetProtocol(IDevice? device)
    {
        if (device is null) return "未绑定";

        string mode = device.Parameters?.GetValue<string>("通信方式") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(mode)) mode = DeviceConnectionConfig.CommunicationMode.ToString();
        string protocol = mode switch
        {
            "Direct" => GetDirectProtocol(device),
            "ModbusTcp" => "Modbus TCP",
            "ZLanGateway" => "ZLAN Gateway",
            "RemoteServer" => "Remote Server",
            "PLC" => "PLC",
            _ => mode
        };
        return device.IsSimulationMode ? $"{protocol} · 模拟" : protocol;
    }

    private static string GetDirectProtocol(IDevice device)
    {
        string typeName = device.GetType().Name;
        if (typeName.Contains("ModbusTCP", StringComparison.OrdinalIgnoreCase)) return "Modbus TCP";
        if (typeName.Contains("ModbusRTU", StringComparison.OrdinalIgnoreCase)) return "Modbus RTU";
        if (typeName.Contains("BLE", StringComparison.OrdinalIgnoreCase)) return "Bluetooth LE";
        return "Direct";
    }

    private static string FormatValue(object value)
    {
        return value switch
        {
            bool boolean => boolean ? "1" : "0",
            double number => double.IsFinite(number) ? number.ToString("0.##", CultureInfo.InvariantCulture) : string.Empty,
            float number => float.IsFinite(number) ? number.ToString("0.##", CultureInfo.InvariantCulture) : string.Empty,
            decimal number => number.ToString("0.##", CultureInfo.InvariantCulture),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "--"
        };
    }

    private static List<Lab3DDeviceBinding> LoadBindingOverrides()
    {
        try
        {
            string path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MaxChemical", "Lab3D", "device-bindings.json");
            if (!File.Exists(path)) return [];

            var bindings = JsonSerializer.Deserialize<List<Lab3DDeviceBinding>>(
                File.ReadAllText(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return bindings?.Where(b => !string.IsNullOrWhiteSpace(b.SlotId)).ToList() ?? [];
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"读取 3D 设备绑定配置失败: {ex.Message}");
            return [];
        }
    }

    private void OnApplicationExit(object? sender, ExitEventArgs e) => Dispose();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _refreshTimer.Stop();
        if (_application is not null) _application.Exit -= OnApplicationExit;

        _eventAggregator.GetEvent<MonitoringCardUpdateEvent>().Unsubscribe(OnMonitoringCardUpdate);
        _eventAggregator.GetEvent<CanvasDevicesChangedEvent>().Unsubscribe(OnCanvasDevicesChanged);

        lock (_sync)
        {
            foreach (var binding in _boundDevices.Values)
            {
                if (binding.Device is not null)
                    binding.Device.ConnectionStatusChanged -= OnConnectionStatusChanged;
            }
            _boundDevices.Clear();
            _samples.Clear();
            _lastUpdates.Clear();
        }
        SnapshotChanged = null;
    }

    private sealed record SlotDefinition(
        string SlotId,
        string[] DriverTypes,
        string PrimaryParameter,
        string PrimaryName,
        string PrimaryUnit,
        string SecondaryParameter,
        string SecondaryName,
        string SecondaryUnit);

    private sealed record BoundDevice(
        SlotDefinition Slot,
        CanvasDeviceViewModel? CanvasDevice,
        string PrimaryParameter,
        string SecondaryParameter)
    {
        public IDevice? Device => CanvasDevice?.Device;
        public string? SourceDeviceId => CanvasDevice?.DeviceId;
    }

    private sealed record SampleValue(string Value, string Unit, DateTime Timestamp);
}

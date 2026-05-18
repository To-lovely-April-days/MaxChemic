using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DevicePlugins.Devices;
using MaxChemical.Core;
using MaxChemical.Data.Models;
using MaxChemical.Data.Repositories;
using MaxChemical.Infrastructure.Services;
using MaxChemical.Modules.GatewayConfig.Models;
using MaxChemical.Modules.GatewayConfig.Services;
using Prism.Commands;
using Prism.Events;
using Prism.Mvvm;

namespace MaxChemical.Modules.GatewayConfig.ViewModels
{
    public class ChannelConfigViewModel : BindableBase
    {
        private readonly IGatewayConfigService _service;
        private readonly IDialogService _dialogService;
        private readonly IEventAggregator _eventAggregator;
        private readonly IDeviceManager _deviceManager;
        private readonly IGatewayBindingRepository _bindingRepository;
        private readonly IDeviceTransportFactory _transportFactory;
        private readonly ChannelModel _original;

        public ChannelModel Working { get; }
        public GatewayModel Gateway { get; }

        // ─── 命令 ───
        public DelegateCommand ApplyCommand { get; }
        public DelegateCommand CancelCommand { get; }
        public DelegateCommand SyncToAllCommand { get; }
        public DelegateCommand BindDeviceCommand { get; }
        public DelegateCommand UnbindDeviceCommand { get; }
        public DelegateCommand TestConnectionCommand { get; }

        // ─── 事件 ───
        public event Action<bool> CloseRequested;
        public event Action<int> PollAttemptChanged;
        public event Action<bool> ApplyStarted;
        public event Action<bool, string> ApplyFinished;

        public ChannelConfigViewModel(
            GatewayModel gateway, ChannelModel channel,
            IGatewayConfigService service,
            IDialogService dialogService,
            IEventAggregator eventAggregator,
            IDeviceManager deviceManager,
            IGatewayBindingRepository bindingRepository,
            IDeviceTransportFactory transportFactory)
        {
            _service = service;
            _dialogService = dialogService;
            _eventAggregator = eventAggregator;
            _deviceManager = deviceManager;
            _bindingRepository = bindingRepository;
            _transportFactory = transportFactory;
            Gateway = gateway;
            _original = Clone(channel);
            Working = Clone(channel);

            ApplyCommand = new DelegateCommand(async () => await OnApplyAsync());
            CancelCommand = new DelegateCommand(() => CloseRequested?.Invoke(false));
            SyncToAllCommand = new DelegateCommand(OnSyncToAll);

            BindDeviceCommand = new DelegateCommand(async () => await OnBindDeviceAsync(),
                () => SelectedDevice != null && !SelectedDevice.IsBoundElsewhere)
                .ObservesProperty(() => SelectedDevice);

            UnbindDeviceCommand = new DelegateCommand(async () => await OnUnbindDeviceAsync(),
                () => HasBinding)
                .ObservesProperty(() => HasBinding);

            TestConnectionCommand = new DelegateCommand(async () => await OnTestConnectionAsync(),
                () => !IsTesting && HasBinding)
                .ObservesProperty(() => IsTesting)
                .ObservesProperty(() => HasBinding);

            // 异步加载候选设备 + 当前绑定
            _ = LoadDeviceCandidatesAsync();
        }

        public string TitleText => $"通道 {Working.Index + 1} · 参数配置";
        public string DeviceIdHex => Working.DeviceId;
        public string DeviceEndpoint => $"{Gateway.Ip} : {Working.LocalPort}";

        public bool DestRequired => Working.WorkMode == WorkMode.TcpClient ||
                                    Working.WorkMode == WorkMode.Udp ||
                                    Working.WorkMode == WorkMode.UdpMulti;

        // ═══════════ 绑定相关 ═══════════

        public ObservableCollection<BindableDeviceCandidate> DeviceCandidates { get; }
            = new ObservableCollection<BindableDeviceCandidate>();

        private BindableDeviceCandidate _selectedDevice;
        public BindableDeviceCandidate SelectedDevice
        {
            get => _selectedDevice;
            set => SetProperty(ref _selectedDevice, value);
        }

        private string _currentBoundDeviceText = "";
        public string CurrentBoundDeviceText
        {
            get => _currentBoundDeviceText;
            set
            {
                if (SetProperty(ref _currentBoundDeviceText, value))
                    RaisePropertyChanged(nameof(HasBinding));   // ★ 通知"已绑定提示框"显示/隐藏
            }
        }

        public bool HasBinding => !string.IsNullOrEmpty(CurrentBoundDeviceText);

        private async Task LoadDeviceCandidatesAsync()
        {
            DeviceCandidates.Clear();

            try
            {
                var gatewayMac = NormalizeMac(Gateway.Mac);
                var currentBinding = await _bindingRepository
                    .GetChannelBindingAsync(gatewayMac, Working.Index);
                if (currentBinding != null)
                {
                    CurrentBoundDeviceText = $"{currentBinding.DeviceDisplayName} · {currentBinding.DeviceInstanceId}";
                }
                else
                {
                    CurrentBoundDeviceText = "";
                }

                var devices = _deviceManager.GetAllDevices()
                    .Where(d => d.SupportsZLanGateway)
                    .ToList();

                var allBindings = await _bindingRepository.GetAllAsync();
                var boundSet = new HashSet<string>(
                    allBindings.Select(b => $"{b.DeviceTypeName}|{b.DeviceInstanceId}"),
                    StringComparer.OrdinalIgnoreCase);

                foreach (var device in devices)
                {
                    var typeName = device.GetType().Name;
                    var deviceIdParam = device.Parameters?.Variables
                        ?.FirstOrDefault(p => p.Name == "DeviceId");
                    var options = (deviceIdParam as StringParameter)?.Options;

                    if (options == null || options.Count == 0)
                    {
                        DeviceCandidates.Add(new BindableDeviceCandidate
                        {
                            DeviceTypeName = typeName,
                            DisplayName = device.Name,
                            InstanceId = device.DeviceId ?? "",
                            ImageLocation = device.ImageLocation,
                            IsBoundElsewhere = boundSet.Contains($"{typeName}|{device.DeviceId}"),
                        });
                    }
                    else
                    {
                        foreach (var id in options)
                        {
                            var cand = new BindableDeviceCandidate
                            {
                                DeviceTypeName = typeName,
                                DisplayName = device.Name,
                                InstanceId = id,
                                ImageLocation = device.ImageLocation,
                            };
                            var isCurrent = currentBinding != null
                                && currentBinding.DeviceTypeName == typeName
                                && currentBinding.DeviceInstanceId == id;
                            cand.IsBoundElsewhere = !isCurrent
                                && boundSet.Contains($"{typeName}|{id}");
                            DeviceCandidates.Add(cand);
                        }
                    }
                }

                if (currentBinding != null)
                {
                    SelectedDevice = DeviceCandidates.FirstOrDefault(c =>
                        c.Matches(currentBinding.DeviceTypeName, currentBinding.DeviceInstanceId));
                }
            }
            catch (Exception ex)
            {
                _dialogService.ShowError($"加载设备列表失败: {ex.Message}", "错误");
            }
        }

        private async Task OnBindDeviceAsync()
        {
            if (SelectedDevice == null) return;
            if (SelectedDevice.IsBoundElsewhere)
            {
                _dialogService.ShowError(
                    $"{SelectedDevice.DisplayName} · {SelectedDevice.InstanceId} 已绑定到其他通道,无法重复绑定。",
                    "已被占用");
                return;
            }

            try
            {
                var binding = new GatewayChannelBinding
                {
                    GatewayMac = NormalizeMac(Gateway.Mac),
                    GatewayIp = Gateway.Ip ?? "",
                    ChannelIndex = Working.Index,
                    ChannelLocalPort = Working.LocalPort,
                    DeviceTypeName = SelectedDevice.DeviceTypeName,
                    DeviceInstanceId = SelectedDevice.InstanceId,
                    DeviceDisplayName = SelectedDevice.DisplayName,
                    CreatedTime = DateTime.Now,
                    UpdatedTime = DateTime.Now,
                };

                var ok = await _bindingRepository.UpsertAsync(binding);
                if (ok)
                {
                    CurrentBoundDeviceText = $"{SelectedDevice.DisplayName} · {SelectedDevice.InstanceId}";
                    _transportFactory.InvalidateCache();
                    _eventAggregator.GetEvent<GatewayBindingChangedEvent>().Publish();
                    _dialogService.ShowInfo(
                        $"已将 {SelectedDevice.DisplayName} {SelectedDevice.InstanceId} 绑定到通道 {Working.Index + 1}。",
                        "绑定成功");
                    await LoadDeviceCandidatesAsync();
                }
                else
                {
                    _dialogService.ShowError("绑定失败,数据库写入未成功。", "错误");
                }
            }
            catch (Exception ex)
            {
                _dialogService.ShowError($"绑定失败: {ex.Message}", "错误");
            }
        }

        private async Task OnUnbindDeviceAsync()
        {
            if (!_dialogService.ShowConfirmation(
                $"解除通道 {Working.Index + 1} 的设备绑定?\n该设备将回到使用物理 COM 口的状态。",
                "确认解绑"))
                return;

            try
            {
                var ok = await _bindingRepository.DeleteByChannelAsync(
                    NormalizeMac(Gateway.Mac), Working.Index);
                if (ok)
                {
                    CurrentBoundDeviceText = "";
                    SelectedDevice = null;
                    TestResultText = "";
                    _transportFactory.InvalidateCache();
                    _eventAggregator.GetEvent<GatewayBindingChangedEvent>().Publish();
                    _dialogService.ShowInfo("已解绑。", "操作成功");
                    await LoadDeviceCandidatesAsync();
                }
                else
                {
                    _dialogService.ShowError("解绑失败。", "错误");
                }
            }
            catch (Exception ex)
            {
                _dialogService.ShowError($"解绑失败: {ex.Message}", "错误");
            }
        }

        // ═══════════ 测试连接 ═══════════

        private string _testResultText = "";
        public string TestResultText
        {
            get => _testResultText;
            set => SetProperty(ref _testResultText, value);
        }

        private bool _isTesting;
        public bool IsTesting
        {
            get => _isTesting;
            set => SetProperty(ref _isTesting, value);
        }

        /// <summary>
        /// 测试连接 — 直接调用设备驱动的"获取实时压力"命令,
        /// 这个命令内部用 IDeviceTransport (走绑定路由 → TCP),
        /// 跟流程引擎运行时完全一致,且不受基类 ConnectionManager 干扰。
        /// </summary>
        private async Task OnTestConnectionAsync()
        {
            if (string.IsNullOrEmpty(CurrentBoundDeviceText)) return;

            IsTesting = true;
            TestResultText = "正在测试…";

            try
            {
                var binding = await _bindingRepository.GetChannelBindingAsync(
                    NormalizeMac(Gateway.Mac), Working.Index);
                if (binding == null)
                {
                    TestResultText = "✗ 未找到绑定记录";
                    return;
                }

                var device = _deviceManager.GetAllDevices()
                    .FirstOrDefault(d => d.GetType().Name == binding.DeviceTypeName);
                if (device == null)
                {
                    TestResultText = $"✗ 找不到设备类型: {binding.DeviceTypeName}";
                    return;
                }

                // 临时把 DeviceId + 通信方式 调到测试需要的状态
                var deviceIdParam = device.Parameters?.Variables
                    ?.FirstOrDefault(p => p.Name == "DeviceId") as StringParameter;
                var modeParam = device.Parameters?.Variables
                    ?.FirstOrDefault(p => p.Name == "通信方式") as StringParameter;

                var originalDeviceId = deviceIdParam?.Value;
                var originalMode = modeParam?.Value;

                try
                {
                    if (deviceIdParam != null) deviceIdParam.Value = binding.DeviceInstanceId;
                    if (modeParam != null) modeParam.Value = "ZLanGateway";   // 强制测网关路径

                    var startTime = DateTime.Now;
                    var connected = await device.ConnectAsync();
                    var elapsed = (DateTime.Now - startTime).TotalMilliseconds;

                    // 测完立即断开,避免影响后续
                    try { await device.DisconnectAsync(); } catch { }

                    if (connected)
                    {
                        TestResultText =
                            $"✓ 连接成功!\n" +
                            $"  设备: {device.Name} · {binding.DeviceInstanceId}\n" +
                            $"  通道: {Gateway.Ip}:{Working.LocalPort}\n" +
                            $"  路径: ZLAN 网关 TCP 透传\n" +
                            $"  耗时: {elapsed:F0} ms";
                    }
                    else
                    {
                        TestResultText =
                            $"✗ 设备连接失败\n" +
                            $"  设备: {device.Name} · {binding.DeviceInstanceId}\n" +
                            $"  通道: {Gateway.Ip}:{Working.LocalPort}\n" +
                            $"  请查看日志了解详细原因\n" +
                            $"  常见原因:\n" +
                            $"   · TCP 链路通但设备无应答 (接线/拨码/站号)\n" +
                            $"   · 网关不可达\n" +
                            $"   · 该设备未绑定到任何通道";
                    }
                }
                finally
                {
                    // 恢复用户原本的选择
                    if (deviceIdParam != null) deviceIdParam.Value = originalDeviceId;
                    if (modeParam != null) modeParam.Value = originalMode;
                }
            }
            catch (Exception ex)
            {
                TestResultText = $"✗ 测试失败: {ex.Message}";
            }
            finally
            {
                IsTesting = false;
                // ★ 通知主窗口刷新这个通道的探测状态
                _eventAggregator.GetEvent<ChannelProbeRequestedEvent>()
                    .Publish((Gateway, Working.Index));
            }
        }
        private static string NormalizeMac(string mac)
        {
            if (string.IsNullOrEmpty(mac)) return "";
            return mac.Replace(":", "").Replace("-", "").Replace(" ", "").ToUpperInvariant();
        }

        // ═══════════ 参数应用 (沿用原逻辑) ═══════════

        private async Task OnApplyAsync()
        {
            var confirmMsg = BuildDiffSummary();
            if (string.IsNullOrEmpty(confirmMsg))
            {
                _dialogService.ShowInfo("没有需要保存的修改。", "提示");
                CloseRequested?.Invoke(false);
                return;
            }

            if (!_dialogService.ShowConfirmation(
                $"下发修改并重启网关?\n\n{confirmMsg}\n\n所有 TCP 连接会中断,设备重启约需 8–15 秒。",
                "确认应用"))
                return;

            ApplyStarted?.Invoke(true);
            try
            {
                var ok = await _service.ApplyChannelAsync(Working, _original,
                    attempt => PollAttemptChanged?.Invoke(attempt));
                ApplyFinished?.Invoke(ok, ok
                    ? $"通道 {Working.Index + 1} 已使用新参数运行"
                    : "设备未在 30 秒内回归,参数已下发但未确认是否生效。");
                _eventAggregator.GetEvent<GatewayApplyFinishedEvent>().Publish(ok);
                if (ok) CloseRequested?.Invoke(true);
            }
            catch (Exception ex)
            {
                ApplyFinished?.Invoke(false, ex.Message);
                _eventAggregator.GetEvent<GatewayApplyFinishedEvent>().Publish(false);
            }
        }

        private void OnSyncToAll()
        {
            if (!_dialogService.ShowConfirmation(
                "把当前通道的串口参数 (波特率、数据位、校验、停止位、流控) 同步到所有其他通道?",
                "同步参数"))
                return;

            foreach (var ch in Gateway.Channels)
            {
                if (ch == null || !ch.IsAvailable || ch == Working) continue;
                ch.BaudRate = Working.BaudRate;
                ch.DataBits = Working.DataBits;
                ch.Parity = Working.Parity;
                ch.StopBit = Working.StopBit;
                ch.FlowControl = Working.FlowControl;
            }
            _dialogService.ShowInfo("已同步到其他通道。修改尚未下发到设备,需要逐个应用。", "同步完成");
        }

        private string BuildDiffSummary()
        {
            var sb = new System.Text.StringBuilder();
            void AppendIfChanged<T>(string name, T cur, T orig)
            {
                if (!Equals(cur, orig)) sb.AppendLine($"  · {name}: {orig} → {cur}");
            }
            AppendIfChanged("波特率", Working.BaudRate, _original.BaudRate);
            AppendIfChanged("数据位", Working.DataBits, _original.DataBits);
            AppendIfChanged("校验位", Working.Parity, _original.Parity);
            AppendIfChanged("停止位", Working.StopBit, _original.StopBit);
            AppendIfChanged("流控", Working.FlowControl, _original.FlowControl);
            AppendIfChanged("工作模式", Working.WorkMode, _original.WorkMode);
            AppendIfChanged("本地端口", Working.LocalPort, _original.LocalPort);
            AppendIfChanged("目的 IP", Working.DestIp, _original.DestIp);
            AppendIfChanged("目的端口", Working.DestPort, _original.DestPort);
            AppendIfChanged("断线重连", Working.ReconnectTime, _original.ReconnectTime);
            AppendIfChanged("保活时间", Working.KeepAliveTime, _original.KeepAliveTime);
            return sb.ToString().TrimEnd();
        }

        private static ChannelModel Clone(ChannelModel src) => new ChannelModel
        {
            Index = src.Index,
            DeviceId = src.DeviceId,
            IsAvailable = src.IsAvailable,
            BaudRate = src.BaudRate,
            DataBits = src.DataBits,
            Parity = src.Parity,
            StopBit = src.StopBit,
            FlowControl = src.FlowControl,
            WorkMode = src.WorkMode,
            LocalPort = src.LocalPort,
            DestIp = src.DestIp,
            DestPort = src.DestPort,
            ReconnectTime = src.ReconnectTime,
            KeepAliveTime = src.KeepAliveTime,
            LinkStatus = src.LinkStatus,
        };
    }

    // ─── GlobalParamsViewModel 不动 ───
    public class GlobalParamsViewModel : BindableBase
    {
        private readonly IGatewayConfigService _service;
        private readonly IDialogService _dialogService;
        private readonly IEventAggregator _eventAggregator;
        private readonly GatewayModel _original;

        public GatewayModel Working { get; }

        public DelegateCommand ApplyCommand { get; }
        public DelegateCommand CancelCommand { get; }
        public DelegateCommand ResetToDefaultCommand { get; }

        public event Action<bool> CloseRequested;
        public event Action<int> PollAttemptChanged;
        public event Action<bool> ApplyStarted;
        public event Action<bool, string> ApplyFinished;

        public GlobalParamsViewModel(GatewayModel gateway,
            IGatewayConfigService service,
            IDialogService dialogService,
            IEventAggregator eventAggregator)
        {
            _service = service;
            _dialogService = dialogService;
            _eventAggregator = eventAggregator;
            _original = Clone(gateway);
            Working = Clone(gateway);

            ApplyCommand = new DelegateCommand(async () => await OnApplyAsync());
            CancelCommand = new DelegateCommand(() => CloseRequested?.Invoke(false));
            ResetToDefaultCommand = new DelegateCommand(async () => await OnResetAsync());
        }

        public string TitleText => $"网关全局参数 · {Working.DisplayName}";

        private async Task OnApplyAsync()
        {
            if (!_dialogService.ShowConfirmation(
                "下发修改并重启网关?所有 TCP 连接会中断,设备重启约需 8–15 秒。",
                "确认应用"))
                return;

            ApplyStarted?.Invoke(true);
            try
            {
                var ok = await _service.ApplyGatewayAsync(Working, _original,
                    attempt => PollAttemptChanged?.Invoke(attempt));
                ApplyFinished?.Invoke(ok, ok ? "网关参数已应用" : "设备未在 30 秒内回归。");
                _eventAggregator.GetEvent<GatewayApplyFinishedEvent>().Publish(ok);
                if (ok) CloseRequested?.Invoke(true);
            }
            catch (Exception ex)
            {
                ApplyFinished?.Invoke(false, ex.Message);
                _eventAggregator.GetEvent<GatewayApplyFinishedEvent>().Publish(false);
            }
        }

        private async Task OnResetAsync()
        {
            if (!_dialogService.ShowConfirmation(
                "此操作不可撤销 — 整台网关 (4 个通道) 的所有参数将恢复出厂默认,设备会自动重启。\n\n确认继续?",
                "恢复出厂设置"))
                return;

            ApplyStarted?.Invoke(true);
            try
            {
                var ok = await _service.ResetToDefaultAsync(Working,
                    attempt => PollAttemptChanged?.Invoke(attempt));
                ApplyFinished?.Invoke(ok, ok
                    ? "已恢复出厂设置,请用默认 IP (192.168.1.200) 重新搜索。"
                    : "设备未在 30 秒内回归。");
                _eventAggregator.GetEvent<GatewayApplyFinishedEvent>().Publish(ok);
                if (ok) CloseRequested?.Invoke(true);
            }
            catch (Exception ex)
            {
                ApplyFinished?.Invoke(false, ex.Message);
                _eventAggregator.GetEvent<GatewayApplyFinishedEvent>().Publish(false);
            }
        }

        private static GatewayModel Clone(GatewayModel src)
        {
            var g = new GatewayModel
            {
                DisplayName = src.DisplayName,
                Mac = src.Mac,
                Ip = src.Ip,
                Netmask = src.Netmask,
                GatewayAddr = src.GatewayAddr,
                DnsServer = src.DnsServer,
                WebPort = src.WebPort,
                IpMode = src.IpMode,
                FirmwareVersion = src.FirmwareVersion,
                IsOnline = src.IsOnline,
            };
            foreach (var ch in src.Channels) g.Channels.Add(ch);
            return g;
        }
    }

    public class GatewayBindingChangedEvent : Prism.Events.PubSubEvent { }
    public class ChannelProbeRequestedEvent : Prism.Events.PubSubEvent<(GatewayModel Gateway, int ChannelIndex)> { }
}
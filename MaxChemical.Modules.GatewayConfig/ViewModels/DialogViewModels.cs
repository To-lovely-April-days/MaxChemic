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
    /// <summary>
    /// 单台物理设备的参数编辑 ViewModel。
    /// 每个 ChannelModel 对应一台真实的卓岚设备,所有网络 + 串口参数都属于这台设备。
    /// 虚拟总网关本身不需要 ViewModel,因为它没有任何参数。
    /// </summary>
    public class ChannelConfigViewModel : BindableBase
    {
        private readonly IGatewayConfigService _service;
        private readonly IDialogService _dialogService;
        private readonly IEventAggregator _eventAggregator;
        private readonly IDeviceManager _deviceManager;
        private readonly IGatewayBindingRepository _bindingRepository;
        private readonly IDeviceTransportFactory _transportFactory;
        private ChannelModel _original;

        public ChannelModel Working { get; }
        public GatewayModel Gateway { get; }

        // ─── 命令 ───
        public DelegateCommand ApplyCommand { get; }
        public DelegateCommand CancelCommand { get; }
        public DelegateCommand RefreshCommand { get; }
        public DelegateCommand ResetToDefaultCommand { get; }
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
            RefreshCommand = new DelegateCommand(async () => await OnRefreshAsync());
            ResetToDefaultCommand = new DelegateCommand(async () => await OnResetToDefaultAsync());

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

            _ = LoadDeviceCandidatesAsync();
        }

        public string TitleText => string.IsNullOrEmpty(Working.DeviceName)
            ? $"设备参数编辑 · 通道 {Working.Index + 1}"
            : $"设备参数编辑 · {Working.DeviceName}";

        public string DeviceIdHex => Working.DeviceId;
        public string FirmwareText => string.IsNullOrEmpty(Working.FirmwareVersion) ? "—" : Working.FirmwareVersion;
        public string MacText => string.IsNullOrEmpty(Working.Mac) ? "—" : Working.Mac;

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
                    RaisePropertyChanged(nameof(HasBinding));
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
                    //  关键修复:用 Working(=当前通道对应的物理设备)的 IP,不是虚拟总网关的 IP
                    // 4 台独立卓岚设备各自的 IP 不同(192.168.1.200/201/202/203),
                    // 之前写 Gateway.Ip 会让 4 个 binding 的 IP 全都变成 .200,
                    // 导致除第一通道外其他通道运行时 TCP 都连错地址。
                    GatewayIp = Working.Ip ?? "",
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
                        $"已将 {SelectedDevice.DisplayName} {SelectedDevice.InstanceId} 绑定到通道 {Working.Index + 1}\n" +
                        $"路由地址:{Working.Ip}:{Working.LocalPort}",
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

                var deviceIdParam = device.Parameters?.Variables
                    ?.FirstOrDefault(p => p.Name == "DeviceId") as StringParameter;
                var modeParam = device.Parameters?.Variables
                    ?.FirstOrDefault(p => p.Name == "通信方式") as StringParameter;

                var originalDeviceId = deviceIdParam?.Value;
                var originalMode = modeParam?.Value;

                try
                {
                    if (deviceIdParam != null) deviceIdParam.Value = binding.DeviceInstanceId;
                    if (modeParam != null) modeParam.Value = "ZLanGateway";

                    var startTime = DateTime.Now;
                    var connected = await device.ConnectAsync();
                    var elapsed = (DateTime.Now - startTime).TotalMilliseconds;

                    try { await device.DisconnectAsync(); } catch { }

                    if (connected)
                    {
                        TestResultText =
                            $"✓ 连接成功!\n" +
                            $"  设备: {device.Name} · {binding.DeviceInstanceId}\n" +
                            // ★ 用 Working.Ip 而不是 Gateway.Ip
                            $"  通道: {Working.Ip}:{Working.LocalPort}\n" +
                            $"  路径: ZLAN 网关 TCP 透传\n" +
                            $"  耗时: {elapsed:F0} ms";
                    }
                    else
                    {
                        TestResultText =
                            $"✗ 设备连接失败\n" +
                            $"  设备: {device.Name} · {binding.DeviceInstanceId}\n" +
                            // ★ 用 Working.Ip 而不是 Gateway.Ip
                            $"  通道: {Working.Ip}:{Working.LocalPort}\n" +
                            $"  请查看日志了解详细原因\n" +
                            $"  常见原因:\n" +
                            $"   · TCP 链路通但设备无应答 (接线/拨码/站号)\n" +
                            $"   · 网关不可达\n" +
                            $"   · 该设备未绑定到任何通道";
                    }
                }
                finally
                {
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
                _eventAggregator.GetEvent<ChannelProbeRequestedEvent>()
                    .Publish((Gateway, Working.Index));
            }
        }

        private static string NormalizeMac(string mac)
        {
            if (string.IsNullOrEmpty(mac)) return "";
            return mac.Replace(":", "").Replace("-", "").Replace(" ", "").ToUpperInvariant();
        }

        // ═══════════ 重新读取 ═══════════
        private async Task OnRefreshAsync()
        {
            try
            {
                await _service.RefreshChannelAsync(Working);
                // 同步更新基准
                _original = Clone(Working);
                // 通知 UI 标题/固件/MAC 等也可能变了
                RaisePropertyChanged(nameof(TitleText));
                RaisePropertyChanged(nameof(FirmwareText));
                RaisePropertyChanged(nameof(MacText));
                _dialogService.ShowInfo("已从设备读取最新参数。", "刷新完成");
            }
            catch (Exception ex)
            {
                _dialogService.ShowError($"读取失败:{ex.Message}", "错误");
            }
        }

        // ═══════════ 恢复默认 ═══════════
        private async Task OnResetToDefaultAsync()
        {
            if (!_dialogService.ShowConfirmation(
                $"将设备 \"{(string.IsNullOrEmpty(Working.DeviceName) ? Working.DeviceId : Working.DeviceName)}\" 恢复出厂设置?\n\n" +
                $"  · IP 将变回 192.168.1.200\n" +
                $"  · 所有自定义参数会丢失\n" +
                $"  · 设备会自动重启 (约 8-15 秒)\n\n" +
                $"注意:仅恢复这一台设备,不影响其他通道。",
                "确认恢复出厂"))
                return;

            ApplyStarted?.Invoke(true);
            try
            {
                var ok = await _service.ResetChannelToDefaultAsync(Working,
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

        // ═══════════ 保存修改 ═══════════
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
                $"下发修改并重启设备?\n\n{confirmMsg}\n\n所有 TCP 连接会中断,设备重启约需 8-15 秒。",
                "确认应用"))
                return;

            ApplyStarted?.Invoke(true);
            try
            {
                var ok = await _service.ApplyChannelAsync(Working, _original,
                    attempt => PollAttemptChanged?.Invoke(attempt));
                ApplyFinished?.Invoke(ok, ok
                    ? $"设备 \"{(string.IsNullOrEmpty(Working.DeviceName) ? Working.DeviceId : Working.DeviceName)}\" 已使用新参数运行"
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

        private string BuildDiffSummary()
        {
            var sb = new System.Text.StringBuilder();
            void AppendIfChanged<T>(string name, T cur, T orig)
            {
                if (!Equals(cur, orig)) sb.AppendLine($"  · {name}: {orig} → {cur}");
            }
            // 身份
            AppendIfChanged("设备名称", Working.DeviceName, _original.DeviceName);
            // 网络
            AppendIfChanged("IP 模式", Working.IpMode, _original.IpMode);
            AppendIfChanged("子网掩码", Working.Netmask, _original.Netmask);
            AppendIfChanged("网关", Working.GatewayAddr, _original.GatewayAddr);
            AppendIfChanged("DNS", Working.DnsServer, _original.DnsServer);
            AppendIfChanged("Web 端口", Working.WebPort, _original.WebPort);
            // 串口
            AppendIfChanged("波特率", Working.BaudRate, _original.BaudRate);
            AppendIfChanged("数据位", Working.DataBits, _original.DataBits);
            AppendIfChanged("校验位", Working.Parity, _original.Parity);
            AppendIfChanged("停止位", Working.StopBit, _original.StopBit);
            AppendIfChanged("流控", Working.FlowControl, _original.FlowControl);
            // TCP/UDP
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
            // 身份
            DeviceName = src.DeviceName,
            Mac = src.Mac,
            FirmwareVersion = src.FirmwareVersion,
            // 网络
            IpMode = src.IpMode,
            Ip = src.Ip,
            Netmask = src.Netmask,
            GatewayAddr = src.GatewayAddr,
            DnsServer = src.DnsServer,
            WebPort = src.WebPort,
            // 串口
            BaudRate = src.BaudRate,
            DataBits = src.DataBits,
            Parity = src.Parity,
            StopBit = src.StopBit,
            FlowControl = src.FlowControl,
            // TCP/UDP
            WorkMode = src.WorkMode,
            LocalPort = src.LocalPort,
            DestIp = src.DestIp,
            DestPort = src.DestPort,
            ReconnectTime = src.ReconnectTime,
            KeepAliveTime = src.KeepAliveTime,
            LinkStatus = src.LinkStatus,
            // 绑定状态
            BoundDeviceName = src.BoundDeviceName,
            BoundDeviceInstanceId = src.BoundDeviceInstanceId,
            ConnectionState = src.ConnectionState,
        };
    }

    public class GatewayBindingChangedEvent : Prism.Events.PubSubEvent { }
    public class ChannelProbeRequestedEvent : Prism.Events.PubSubEvent<(GatewayModel Gateway, int ChannelIndex)> { }
}
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
using Prism.Ioc;
using MaxChemical.Infrastructure.Events;

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
        private readonly ILocalizationService _localizationService;
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

            _localizationService = ContainerLocator.Container.Resolve<ILocalizationService>();

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
            //
            LoadLocalizationTxt("");
        }

        #region 本地化相关属性

        private string _devIdTxt;
        private string _firmwareTxt;
        private string _restartTipTxt;
        private string _networkSettingTxt;
        private string _devNameTxt;
        private string _modeTxt;
        private string _localIpTxt;
        private string _localPortTxt;
        private string _subnetMaskTxt;
        private string _gatewayTxt;
        private string _dnsTxt;
        private string _webTxt;
        private string _workModeTxt;
        private string _destIpTxt;
        private string _destPortTxt;
        private string _reconnTxt;
        private string _aliveTxt;
        private string _serialTxt;
        private string _baudRateTxt;
        private string _dataBitsTxt;
        private string _stopBitsTxt;
        private string _parityTxt;
        private string _flowCtrlTxt;
        private string _bindDevTxt;
        private string _bindTipTxt;
        private string _bindTxt;
        private string _testConnTxt;
        private string _unbindTxt;
        private string _bindChannelTxt;
        private string _cancelTxt;
        private string _rereadTxt;
        private string _restoreDefaultTxt;
        private string _saveTxt;

        public string DevIdTxt { get { return _devIdTxt; } set { SetProperty(ref _devIdTxt, value); } }
        public string FirmwareTxt { get { return _firmwareTxt; } set { SetProperty(ref _firmwareTxt, value); } }
        public string RestartTipTxt { get { return _restartTipTxt; } set { SetProperty(ref _restartTipTxt, value); } }
        public string NetworkSettingTxt { get { return _networkSettingTxt; } set { SetProperty(ref _networkSettingTxt, value); } }
        public string DevNameTxt { get { return _devNameTxt; } set { SetProperty(ref _devNameTxt, value); } }
        public string ModeTxt { get { return _modeTxt; } set { SetProperty(ref _modeTxt, value); } }
        public string LocalIpTxt { get { return _localIpTxt; } set { SetProperty(ref _localIpTxt, value); } }
        public string LocalPortTxt { get { return _localPortTxt; } set { SetProperty(ref _localPortTxt, value); } }
        public string SubnetMaskTxt { get { return _subnetMaskTxt; }set { SetProperty(ref _subnetMaskTxt, value); } }
        public string GatewayTxt { get { return _gatewayTxt; } set { SetProperty(ref _gatewayTxt, value); } }
        public string DnsServerTxt { get { return _dnsTxt; } set { SetProperty(ref _dnsTxt, value); } }
        public string WebPortTxt { get { return _webTxt; } set { SetProperty(ref _webTxt, value); } }
        public string WorkPatternTxt { get { return _workModeTxt; } set { SetProperty(ref _workModeTxt, value); } }
        public string DestIpTxt { get { return _destIpTxt; } set { SetProperty(ref _destIpTxt, value); } }
        public string DestPortTxt { get { return _destPortTxt; } set { SetProperty(ref _destPortTxt, value); } }
        public string ReconnTxt { get { return _reconnTxt; } set { SetProperty(ref _reconnTxt, value); } }
        public string AliveTxt { get { return _aliveTxt; } set { SetProperty(ref _aliveTxt, value); } }
        public string SerialParamTxt { get { return _serialTxt; } set { SetProperty(ref _serialTxt, value); } }
        public string BaudRateTxt { get { return _baudRateTxt; } set { SetProperty(ref _baudRateTxt, value); } }
        public string DataBitsTxt { get { return _dataBitsTxt; } set { SetProperty(ref _dataBitsTxt, value); } }
        public string StopBitsTxt { get { return _stopBitsTxt; } set { SetProperty(ref _stopBitsTxt, value); } }
        public string ParityTxt { get { return _parityTxt; } set { SetProperty(ref _parityTxt, value); } }
        public string FlowCtrlTxt { get { return _flowCtrlTxt; } set { SetProperty(ref _flowCtrlTxt, value); } }
        public string BindDevTxt { get { return _bindDevTxt; } set { SetProperty(ref _bindDevTxt, value); } }
        public string BindTipTxt { get { return _bindTipTxt; } set { SetProperty(ref _bindTipTxt, value); } }
        public string BindTxt { get { return _bindTxt; } set { SetProperty(ref _bindTxt, value); } }
        public string TestConnTxt { get { return _testConnTxt; } set { SetProperty(ref _testConnTxt, value); } }
        public string UnbindTxt { get { return _unbindTxt; } set { SetProperty(ref _unbindTxt, value); } }
        public string BindChannelTxt { get { return _bindChannelTxt; } set { SetProperty(ref _bindChannelTxt, value); } }
        public string CancelTxt { get { return _cancelTxt; } set { SetProperty(ref _cancelTxt, value); } }
        public string RereadTxt { get { return _rereadTxt; } set { SetProperty(ref _rereadTxt, value); } }
        public string RestoreDefaultTxt { get { return _restoreDefaultTxt; } set { SetProperty(ref _restoreDefaultTxt, value); } }
        public string SaveTxt { get { return _saveTxt; } set { SetProperty(ref _saveTxt, value); } }

        public string StaticIpTxt { get; set; }
        public string DhcpTxt { get; set; }
        public string UdpMultTxt { get; set; }
        public string ParityNoneTxt { get; set; }
        public string ParityOddTxt { get; set; }
        public string ParityEvenTxt { get; set; }
        public string ParityMarkTxt { get; set; }
        public string ParitySpaceTxt { get; set; }
        public string NoneFlowCtrlTxt { get; set; }

        private void LoadLocalizationTxt(string culture)
        {
            _devIdTxt = _localizationService.GetString("Gateway_Dialog_ID", "设备 ID");
            _firmwareTxt = _localizationService.GetString("Gateway_Firmware", "固件");
            _restartTipTxt = _localizationService.GetString("Gateway_Dialog_RestartTip", "修改后设备会自动重启");
            _networkSettingTxt = _localizationService.GetString("Gateway_SettingLabel", "网络设置");
            _devNameTxt = _localizationService.GetString("Gateway_Name", "设备名称");
            _modeTxt = _localizationService.GetString("Gateway_Mode", "IP 模式");
            _localIpTxt = _localizationService.GetString("Gateway_LocalIp", "本地 IP");
            _localPortTxt = _localizationService.GetString("Gateway_LocalPort", "本地端口");
            _subnetMaskTxt = _localizationService.GetString("Gateway_mask", "子网掩码");
            _gatewayTxt = _localizationService.GetString("Gateway_List_Title", "网关");
            _dnsTxt = _localizationService.GetString("Gateway_DNS", "DNS 服务器");
            _webTxt = _localizationService.GetString("Gateway_Web", "Web 端口");
            _workModeTxt = _localizationService.GetString("Gateway_WorkingMode", "工作模式");
            _destIpTxt = _localizationService.GetString("Gateway_TargetIp", "目的 IP / 域名");
            _destPortTxt = _localizationService.GetString("Gateway_TargetPort", "目的端口");
            _reconnTxt = _localizationService.GetString("Gateway_Reconn", "断线重连（秒，0~2555）");
            _aliveTxt = _localizationService.GetString("Gateway_KeepAlive", "保活时间（秒，0~255）");
            _serialTxt = _localizationService.GetString("Gateway_Serial_Param", "串口参数");
            _baudRateTxt = _localizationService.GetString("Gateway_BaudRate", "波特率");
            _dataBitsTxt = _localizationService.GetString("Gateway_DataBits", "数据位");
            _stopBitsTxt = _localizationService.GetString("Gateway_StopBits", "停止位");
            _parityTxt = _localizationService.GetString("Gateway_Parity", "校验位");
            _flowCtrlTxt = _localizationService.GetString("Gateway_FlowCtrl", "流控");
            _bindDevTxt = _localizationService.GetString("Gateway_Bind", "绑定设备");
            _bindTipTxt = _localizationService.GetString("Gateway_Bind_Tip", "绑定后，该设备的所有命令通过此通道收发");
            _bindTxt = _localizationService.GetString("Gateway_Bind_Already", "已绑定：");
            _testConnTxt = _localizationService.GetString("Gateway_TestConn", "测试连接");
            _unbindTxt = _localizationService.GetString("Gateway_Unbind", "解绑");
            _bindChannelTxt = _localizationService.GetString("Gateway_BindToChannel", "绑定到此通道");
            _cancelTxt = _localizationService.GetString("Gateway_Cancel", "取消");
            _rereadTxt = _localizationService.GetString("Gateway_Read", "重新读取");
            _restoreDefaultTxt = _localizationService.GetString("Gateway_Restore", "恢复默认");
            _saveTxt = _localizationService.GetString("Gateway_SaveModify", "保存修改");
            StaticIpTxt = _localizationService.GetString("Gateway_IpMode_Static", "静态 IP");
            DhcpTxt = _localizationService.GetString("Gateway_IpMode_Dhc", "DHCP 动态");
            UdpMultTxt = _localizationService.GetString("Gateway_WorkMode_Udp", "UDP 组播");
            ParityNoneTxt = _localizationService.GetString("Gateway_Parity_None", "无校验");
            ParityOddTxt = _localizationService.GetString("Gateway_Parity_Odd", "奇校验");
            ParityEvenTxt = _localizationService.GetString("Gateway_Parity_Even", "偶校验");
            ParityMarkTxt = _localizationService.GetString("Gateway_Parity_Mark", "标记");
            ParitySpaceTxt = _localizationService.GetString("Gateway_Parity_Space", "空格");
            NoneFlowCtrlTxt = _localizationService.GetString("Gateway_FlowCtrl_None", "无流控");
        }

        #endregion


        public string TitleText => string.IsNullOrEmpty(Working.DeviceName)
            ? string.Format(_localizationService.GetString("Gateway_Setting_Title", "设备参数编辑 · 通道 {0}"), Working.Index + 1)
            : string.Format(_localizationService.GetString("Gateway_Setting_Title1", "设备参数编辑 · {0}"), Working.DeviceName);

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
            TestResultText = _localizationService.GetString("Gateway_Loading_Test", "正在测试...");

            try
            {
                var binding = await _bindingRepository.GetChannelBindingAsync(
                    NormalizeMac(Gateway.Mac), Working.Index);
                if (binding == null)
                {
                    TestResultText = _localizationService.GetString("Gateway_Not_Found_Bind", "✗ 未找到绑定记录");
                    return;
                }

                var device = _deviceManager.GetAllDevices()
                    .FirstOrDefault(d => d.GetType().Name == binding.DeviceTypeName);
                if (device == null)
                {
                    TestResultText = string.Format(
                        _localizationService.GetString("Gateway_Not_Found_DevType", "✗ 找不到设备类型: {0}"), binding.DeviceTypeName);
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
                        TestResultText = string.Format(
                            _localizationService.GetString(
                                "Gateway_Test_Success",
                                "  设备: {0} · {1}\n  通道: {2}:{3}\n  路径: ZLAN 网关 TCP 透传\n  耗时: {4:F0} ms"),
                            device.Name, binding.DeviceInstanceId, Working.Ip, Working.LocalPort, elapsed);
                    }
                    else
                    {
                        TestResultText = string.Format(
                            _localizationService.GetString(
                                "Gateway_Test_ConnFailed",
                                "✗ 设备连接失败\n  设备: {0} · {1}\n  通道: {2}:{3}\n  请查看日志了解详细原因\n  常见原因:\n   · TCP 链路通但设备无应答 (接线/拨码/站号)\n   · 网关不可达\n   · 该设备未绑定到任何通道"),
                            device.Name, binding.DeviceInstanceId, Working.Ip, Working.LocalPort);
                            
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
                TestResultText = string.Format(_localizationService.GetString("Gateway_Test_Failed", "✗ 测试失败: {0}"),ex.Message);
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
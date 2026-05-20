using System.Collections.ObjectModel;
using Prism.Mvvm;

namespace MaxChemical.Modules.GatewayConfig.Models
{
    /// <summary>
    /// 虚拟"总网关" — 仅作为 UI 上聚合 N 台独立卓岚设备的容器,
    /// 自身没有任何可配置参数(IP/MAC/固件 都展示用,真值在 Channels 里)。
    /// </summary>
    public class GatewayModel : BindableBase
    {
        private string _displayName = "";
        public string DisplayName { get => _displayName; set => SetProperty(ref _displayName, value); }

        // 以下字段:取第一台设备的值,仅用于卡片上的概要展示
        private string _mac = "";
        public string Mac { get => _mac; set => SetProperty(ref _mac, value); }

        private string _ip = "";
        public string Ip { get => _ip; set => SetProperty(ref _ip, value); }

        private string _firmwareVersion = "";
        public string FirmwareVersion { get => _firmwareVersion; set => SetProperty(ref _firmwareVersion, value); }

        private bool _isOnline;
        public bool IsOnline { get => _isOnline; set => SetProperty(ref _isOnline, value); }

        public ObservableCollection<ChannelModel> Channels { get; } = new ObservableCollection<ChannelModel>();

        public int OnlineChannelCount
        {
            get
            {
                int n = 0;
                foreach (var c in Channels) if (c != null && c.IsAvailable) n++;
                return n;
            }
        }
    }

    /// <summary>
    /// 一个串口通道 = 一台真实的卓岚物理设备 (= SDK 的 1 个 device_id)。
    /// 拥有完整的网络 + 串口参数,在 UI 上对应虚拟网关的一个 DB9 接口。
    /// </summary>
    public class ChannelModel : BindableBase
    {
        public int Index { get; set; }

        private string _deviceId = "";
        public string DeviceId { get => _deviceId; set => SetProperty(ref _deviceId, value); }

        private bool _isAvailable;
        public bool IsAvailable { get => _isAvailable; set => SetProperty(ref _isAvailable, value); }

        // ═══════════ 设备身份 ═══════════
        private string _deviceName = "";
        public string DeviceName { get => _deviceName; set => SetProperty(ref _deviceName, value); }

        private string _mac = "";
        public string Mac { get => _mac; set => SetProperty(ref _mac, value); }

        private string _firmwareVersion = "";
        public string FirmwareVersion { get => _firmwareVersion; set => SetProperty(ref _firmwareVersion, value); }

        // ═══════════ 网络参数 ═══════════
        private IpMode _ipMode = IpMode.Static;
        public IpMode IpMode { get => _ipMode; set => SetProperty(ref _ipMode, value); }

        private string _ip = "";
        public string Ip { get => _ip; set => SetProperty(ref _ip, value); }

        private string _netmask = "";
        public string Netmask { get => _netmask; set => SetProperty(ref _netmask, value); }

        private string _gatewayAddr = "";
        public string GatewayAddr { get => _gatewayAddr; set => SetProperty(ref _gatewayAddr, value); }

        private string _dnsServer = "";
        public string DnsServer { get => _dnsServer; set => SetProperty(ref _dnsServer, value); }

        private ushort _webPort = 80;
        public ushort WebPort { get => _webPort; set => SetProperty(ref _webPort, value); }

        // ═══════════ 串口参数 ═══════════
        private BaudRate _baudRate = BaudRate.Bps_9600;
        public BaudRate BaudRate { get => _baudRate; set => SetProperty(ref _baudRate, value); }

        private DataBits _dataBits = DataBits.Bits_8;
        public DataBits DataBits { get => _dataBits; set => SetProperty(ref _dataBits, value); }

        private Parity _parity = Parity.None;
        public Parity Parity { get => _parity; set => SetProperty(ref _parity, value); }

        private StopBit _stopBit = StopBit.Stop_1;
        public StopBit StopBit { get => _stopBit; set => SetProperty(ref _stopBit, value); }

        private FlowControl _flowControl = FlowControl.None;
        public FlowControl FlowControl { get => _flowControl; set => SetProperty(ref _flowControl, value); }

        // ═══════════ TCP/UDP 参数 ═══════════
        private WorkMode _workMode = WorkMode.TcpServer;
        public WorkMode WorkMode { get => _workMode; set => SetProperty(ref _workMode, value); }

        private ushort _localPort = 4196;
        public ushort LocalPort { get => _localPort; set => SetProperty(ref _localPort, value); }

        private string _destIp = "";
        public string DestIp { get => _destIp; set => SetProperty(ref _destIp, value); }

        private ushort _destPort;
        public ushort DestPort { get => _destPort; set => SetProperty(ref _destPort, value); }

        private int _reconnectTime = 12;
        public int ReconnectTime { get => _reconnectTime; set => SetProperty(ref _reconnectTime, value); }

        private int _keepAliveTime = 60;
        public int KeepAliveTime { get => _keepAliveTime; set => SetProperty(ref _keepAliveTime, value); }

        private LinkStatus _linkStatus;
        public LinkStatus LinkStatus { get => _linkStatus; set => SetProperty(ref _linkStatus, value); }

        // ═══════════ 绑定状态 ═══════════
        private string _boundDeviceName = "";
        public string BoundDeviceName
        {
            get => _boundDeviceName;
            set { SetProperty(ref _boundDeviceName, value); RaisePropertyChanged(nameof(IsBound)); }
        }

        private string _boundDeviceInstanceId = "";
        public string BoundDeviceInstanceId
        {
            get => _boundDeviceInstanceId;
            set => SetProperty(ref _boundDeviceInstanceId, value);
        }

        public bool IsBound => !string.IsNullOrEmpty(_boundDeviceName);

        // ═══════════ 连接探测状态 ═══════════
        private ChannelConnectionState _connectionState = ChannelConnectionState.Unbound;
        public ChannelConnectionState ConnectionState
        {
            get => _connectionState;
            set => SetProperty(ref _connectionState, value);
        }
    }
}
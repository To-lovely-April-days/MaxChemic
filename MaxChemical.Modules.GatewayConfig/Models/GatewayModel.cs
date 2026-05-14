using System.Collections.ObjectModel;
using Prism.Mvvm;

namespace MaxChemical.Modules.GatewayConfig.Models
{
    /// <summary>
    /// 一台网关 (= 1 个物理 ZLAN5443H)。
    /// SDK 把它的 4 个串口暴露为 4 个独立 device,网络参数 4 通道共享。
    /// </summary>
    public class GatewayModel : BindableBase
    {
        private string _displayName = "";
        public string DisplayName { get => _displayName; set => SetProperty(ref _displayName, value); }

        private string _mac = "";
        public string Mac { get => _mac; set => SetProperty(ref _mac, value); }

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

        private IpMode _ipMode = IpMode.Static;
        public IpMode IpMode { get => _ipMode; set => SetProperty(ref _ipMode, value); }

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

    /// <summary>一个串口通道 (= SDK 的 1 个 device_id 字符串)。</summary>
    public class ChannelModel : BindableBase
    {
        public int Index { get; set; }

        private string _deviceId = "";
        public string DeviceId { get => _deviceId; set => SetProperty(ref _deviceId, value); }

        private bool _isAvailable;
        public bool IsAvailable { get => _isAvailable; set => SetProperty(ref _isAvailable, value); }

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
    }
}

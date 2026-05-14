using System;
using System.Threading.Tasks;
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
        private readonly ChannelModel _original;

        public ChannelModel Working { get; }
        public GatewayModel Gateway { get; }

        public DelegateCommand ApplyCommand { get; }
        public DelegateCommand CancelCommand { get; }
        public DelegateCommand SyncToAllCommand { get; }

        public event Action<bool> CloseRequested;
        public event Action<int> PollAttemptChanged;
        public event Action<bool> ApplyStarted;
        public event Action<bool, string> ApplyFinished;

        public ChannelConfigViewModel(GatewayModel gateway, ChannelModel channel,
            IGatewayConfigService service, IDialogService dialogService,
            IEventAggregator eventAggregator)
        {
            _service = service;
            _dialogService = dialogService;
            _eventAggregator = eventAggregator;
            Gateway = gateway;
            _original = Clone(channel);
            Working = Clone(channel);

            ApplyCommand = new DelegateCommand(async () => await OnApplyAsync());
            CancelCommand = new DelegateCommand(() => CloseRequested?.Invoke(false));
            SyncToAllCommand = new DelegateCommand(OnSyncToAll);
        }

        public string TitleText => $"通道 {Working.Index + 1} · 参数配置";
        public string DeviceIdHex => Working.DeviceId;
        public string DeviceEndpoint => $"{Gateway.Ip} : {Working.LocalPort}";

        public bool DestRequired => Working.WorkMode == WorkMode.TcpClient ||
                                    Working.WorkMode == WorkMode.Udp ||
                                    Working.WorkMode == WorkMode.UdpMulti;

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

                // 通知主窗口刷新列表
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
            IGatewayConfigService service, IDialogService dialogService,
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
                ApplyFinished?.Invoke(ok, ok
                    ? "网关参数已应用"
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
}
using MaxChemical.Data.Repositories;
using MaxChemical.Modules.GatewayConfig.Models;
using MaxChemical.Modules.GatewayConfig.Services;
using MaxChemical.Modules.GatewayConfig.ViewModels;
using MaxChemical.Modules.GatewayConfig.Views.Dialogs;
using Prism.Events;
using Prism.Ioc;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace MaxChemical.Modules.GatewayConfig.Views
{
    public partial class GatewayConfigMainView : Window
    {
        private readonly IEventAggregator _eventAggregator;
        private readonly IGatewayBindingRepository _bindingRepository;
        private readonly IChannelProbeService _probeService;

        // ★ 心跳 Timer
        private DispatcherTimer _heartbeatTimer;
        private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(15);
        private volatile bool _heartbeatBusy;   // 防止上一次没探完下一次又来

        public GatewayConfigMainView()
        {
            InitializeComponent();

            var c = ContainerLocator.Container;
            _eventAggregator = c.Resolve<IEventAggregator>();
            _bindingRepository = c.Resolve<IGatewayBindingRepository>();
            _probeService = c.Resolve<IChannelProbeService>();

            _eventAggregator.GetEvent<OpenChannelDialogEvent>().Subscribe(OnOpenChannel, ThreadOption.UIThread);
            _eventAggregator.GetEvent<OpenGlobalParamsDialogEvent>().Subscribe(OnOpenGlobalParams, ThreadOption.UIThread);
            _eventAggregator.GetEvent<GatewayConfigStatusEvent>().Subscribe(msg =>
            {
                if (StatusBarText != null) StatusBarText.Text = msg;
            }, ThreadOption.UIThread);

            // 绑定变更后,先刷新绑定再探测
            _eventAggregator.GetEvent<GatewayBindingChangedEvent>().Subscribe(async () =>
            {
                if (DataContext is GatewayConfigMainViewModel vm && vm.SelectedGateway != null)
                {
                    await RefreshBindingsForGatewayAsync(vm.SelectedGateway);
                    await _probeService.ProbeGatewayAsync(vm.SelectedGateway);
                    ForceRedraw(vm);
                }
            }, ThreadOption.UIThread);

            // 测试连接完成后,只刷新这一个通道的探测状态
            _eventAggregator.GetEvent<ChannelProbeRequestedEvent>().Subscribe(async tup =>
            {
                if (DataContext is GatewayConfigMainViewModel vm)
                {
                    var ch = tup.Gateway?.Channels?.FirstOrDefault(c => c.Index == tup.ChannelIndex);
                    if (ch != null)
                    {
                        await _probeService.ProbeChannelAsync(tup.Gateway, ch);
                        ForceRedraw(vm);
                    }
                }
            }, ThreadOption.UIThread);

            Loaded += OnWindowLoaded;
            Closed += OnWindowClosed;
        }

        private async void OnWindowLoaded(object sender, RoutedEventArgs e)
        {
            if (!(DataContext is GatewayConfigMainViewModel vm)) return;

            await vm.InitializeAsync();

            var loading = new LoadingOverlayWindow(this);
            loading.Show("正在搜索网关…");

            var deadline = DateTime.UtcNow.AddSeconds(60);
            int attempt = 0;
            bool found = false;

            try
            {
                while (DateTime.UtcNow < deadline)
                {
                    attempt++;
                    loading.UpdateMessage($"正在搜索网关… 第 {attempt} 次");
                    try
                    {
                        var list = await vm.AutoSearchOnceAsync();
                        if (list != null && list.Count > 0)
                        {
                            foreach (var g in vm.Gateways)
                            {
                                await RefreshBindingsForGatewayAsync(g);
                            }

                            // 异步首次探测连接,不阻塞 UI
                            _ = Task.Run(async () =>
                            {
                                foreach (var g in vm.Gateways)
                                {
                                    await _probeService.ProbeGatewayAsync(g);
                                }
                                Dispatcher.Invoke(() => ForceRedraw(vm));
                            });

                            ForceRedraw(vm);
                            found = true;
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"搜索失败: {ex.Message}");
                    }
                    await Task.Delay(500);
                }
            }
            finally
            {
                loading.Hide();
            }

            if (!found)
            {
                MessageBox.Show(
                    "60 秒内未搜索到任何网关。\n\n请检查:\n  · 网关是否上电\n  · 网线是否连接\n  · 电脑和网关是否在同一网段",
                    "搜索超时", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // ★ 启动心跳探测
            StartHeartbeat();
        }

        private async void OnWindowClosed(object sender, EventArgs e)
        {
            // 停止心跳
            StopHeartbeat();

            try
            {
                var service = ContainerLocator.Container.Resolve<Services.IGatewayConfigService>();
                await service.ShutdownAsync();
            }
            catch { }
        }

        // ═══════════ 心跳探测 ═══════════

        private void StartHeartbeat()
        {
            if (_heartbeatTimer != null) return;

            _heartbeatTimer = new DispatcherTimer
            {
                Interval = HeartbeatInterval,
            };
            _heartbeatTimer.Tick += OnHeartbeatTick;
            _heartbeatTimer.Start();

            System.Diagnostics.Debug.WriteLine($"网关探测心跳已启动,间隔 {HeartbeatInterval.TotalSeconds}s");
        }

        private void StopHeartbeat()
        {
            if (_heartbeatTimer == null) return;

            _heartbeatTimer.Stop();
            _heartbeatTimer.Tick -= OnHeartbeatTick;
            _heartbeatTimer = null;

            System.Diagnostics.Debug.WriteLine("网关探测心跳已停止");
        }

        private async void OnHeartbeatTick(object sender, EventArgs e)
        {
            if (_heartbeatBusy) return;   // 上一轮还没探完就跳过
            _heartbeatBusy = true;

            try
            {
                if (!(DataContext is GatewayConfigMainViewModel vm)) return;
                if (vm.Gateways == null || vm.Gateways.Count == 0) return;

                // 记录探测前的状态,用来检测变化
                var snapshotBefore = TakeStateSnapshot(vm);

                // 并发探测所有网关 (在后台线程)
                await Task.Run(async () =>
                {
                    foreach (var g in vm.Gateways)
                    {
                        try
                        {
                            await _probeService.ProbeGatewayAsync(g);
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"心跳探测网关 {g?.DisplayName} 失败: {ex.Message}");
                        }
                    }
                });

                // 探测后状态有变化才重绘
                var snapshotAfter = TakeStateSnapshot(vm);
                if (snapshotBefore != snapshotAfter)
                {
                    System.Diagnostics.Debug.WriteLine($"心跳探测:状态变化 {snapshotBefore} → {snapshotAfter}");
                    ForceRedraw(vm);
                }
            }
            finally
            {
                _heartbeatBusy = false;
            }
        }

        /// <summary>把所有通道的探测状态拼成一个字符串,用来对比变化</summary>
        private static string TakeStateSnapshot(GatewayConfigMainViewModel vm)
        {
            if (vm?.Gateways == null) return "";
            var parts = new System.Collections.Generic.List<string>();
            foreach (var g in vm.Gateways)
            {
                foreach (var ch in g.Channels)
                {
                    parts.Add($"{g.Mac}.{ch.Index}={ch.ConnectionState}");
                }
            }
            return string.Join(";", parts);
        }

        // ═══════════ UI 重绘 ═══════════

        /// <summary>
        /// 强制 SVG 重新渲染 — 通过将 SelectedGateway 置 null 再赋回,
        /// 触发 GatewayBodyControl 的 OnGatewayChanged。
        /// </summary>
        private void ForceRedraw(GatewayConfigMainViewModel vm)
        {
            if (vm == null) return;
            var current = vm.SelectedGateway;
            vm.SelectedGateway = null;
            vm.SelectedGateway = current;
        }

        // ═══════════ 绑定信息加载 ═══════════

        private async Task RefreshBindingsForGatewayAsync(GatewayModel gateway)
        {
            try
            {
                var mac = NormalizeMac(gateway.Mac);
                var bindings = await _bindingRepository.GetByGatewayAsync(mac);
                foreach (var ch in gateway.Channels)
                {
                    var b = bindings.FirstOrDefault(x => x.ChannelIndex == ch.Index);
                    if (b != null)
                    {
                        ch.BoundDeviceName = b.DeviceDisplayName ?? "";
                        ch.BoundDeviceInstanceId = b.DeviceInstanceId ?? "";
                    }
                    else
                    {
                        ch.BoundDeviceName = "";
                        ch.BoundDeviceInstanceId = "";
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"刷新绑定失败: {ex.Message}");
            }
        }

        private static string NormalizeMac(string mac)
        {
            if (string.IsNullOrEmpty(mac)) return "";
            return mac.Replace(":", "").Replace("-", "").Replace(" ", "").ToUpperInvariant();
        }

        private void GatewayCard_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is Border b && b.Tag is GatewayModel g
                && DataContext is GatewayConfigMainViewModel vm)
            {
                vm.SelectedGateway = g;
            }
        }

        private void OnOpenChannel((GatewayModel Gateway, ChannelModel Channel) tup)
        {
            var dialog = new ChannelConfigDialog(tup.Gateway, tup.Channel);
            dialog.Owner = this;
            dialog.ShowDialog();
        }

        private void OnOpenGlobalParams(GatewayModel gateway)
        {
            if (gateway == null) return;
            var dialog = new GlobalParamsDialog(gateway);
            dialog.Owner = this;
            dialog.ShowDialog();
        }
    }
}
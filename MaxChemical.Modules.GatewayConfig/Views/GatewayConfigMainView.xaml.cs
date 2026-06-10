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

        // ★ 事件订阅 token,窗口关闭时取消,避免已关闭实例继续响应事件
        private SubscriptionToken _openChannelToken;
        private SubscriptionToken _statusToken;
        private SubscriptionToken _bindingChangedToken;
        private SubscriptionToken _channelProbeToken;

        // ★ 心跳 Timer
        private DispatcherTimer _heartbeatTimer;
        private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(15);
        private volatile bool _heartbeatBusy;

        public GatewayConfigMainView()
        {
            InitializeComponent();

            var c = ContainerLocator.Container;
            _eventAggregator = c.Resolve<IEventAggregator>();
            _bindingRepository = c.Resolve<IGatewayBindingRepository>();
            _probeService = c.Resolve<IChannelProbeService>();

            // ★ 每个 Subscribe 都接住 token,关闭时统一取消
            _openChannelToken = _eventAggregator.GetEvent<OpenChannelDialogEvent>()
                .Subscribe(OnOpenChannel, ThreadOption.UIThread);

            _statusToken = _eventAggregator.GetEvent<GatewayConfigStatusEvent>().Subscribe(msg =>
            {
                if (StatusBarText != null) StatusBarText.Text = msg;
            }, ThreadOption.UIThread);

            // 绑定变更后,先刷新绑定再探测
            _bindingChangedToken = _eventAggregator.GetEvent<GatewayBindingChangedEvent>().Subscribe(async () =>
            {
                if (DataContext is GatewayConfigMainViewModel vm && vm.SelectedGateway != null)
                {
                    await RefreshBindingsForGatewayAsync(vm.SelectedGateway);
                    await _probeService.ProbeGatewayAsync(vm.SelectedGateway);
                    ForceRedraw(vm);
                }
            }, ThreadOption.UIThread);

            // 测试连接完成后,只刷新这一个通道的探测状态
            _channelProbeToken = _eventAggregator.GetEvent<ChannelProbeRequestedEvent>().Subscribe(async tup =>
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

            // ★ 显示内嵌遮罩
            ShowLoading("正在搜索网关…");

            var deadline = DateTime.UtcNow.AddSeconds(60);
            int attempt = 0;
            bool found = false;

            try
            {
                while (DateTime.UtcNow < deadline)
                {
                    attempt++;
                    UpdateLoading($"正在搜索网关… 第 {attempt} 次");
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
                // ★ 隐藏内嵌遮罩
                HideLoading();
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
            StopHeartbeat();

            // ★ 取消所有事件订阅,避免已关闭的窗口实例继续响应事件
            //   (否则再次打开窗口时,旧实例还在监听,会用已关闭的窗口设 Owner 而抛异常)
            try
            {
                if (_openChannelToken != null)
                    _eventAggregator.GetEvent<OpenChannelDialogEvent>().Unsubscribe(_openChannelToken);
                if (_statusToken != null)
                    _eventAggregator.GetEvent<GatewayConfigStatusEvent>().Unsubscribe(_statusToken);
                if (_bindingChangedToken != null)
                    _eventAggregator.GetEvent<GatewayBindingChangedEvent>().Unsubscribe(_bindingChangedToken);
                if (_channelProbeToken != null)
                    _eventAggregator.GetEvent<ChannelProbeRequestedEvent>().Unsubscribe(_channelProbeToken);
            }
            catch { }

            try
            {
                var service = ContainerLocator.Container.Resolve<Services.IGatewayConfigService>();
                await service.ShutdownAsync();
            }
            catch { }
        }

        // ═══════════ 内嵌 Loading 遮罩 ═══════════

        private void ShowLoading(string message)
        {
            if (LoadingMessage != null) LoadingMessage.Text = message;
            if (LoadingOverlay != null) LoadingOverlay.Visibility = Visibility.Visible;
        }

        private void UpdateLoading(string message)
        {
            if (LoadingMessage != null) LoadingMessage.Text = message;
        }

        private void HideLoading()
        {
            if (LoadingOverlay != null) LoadingOverlay.Visibility = Visibility.Collapsed;
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
            if (_heartbeatBusy) return;
            _heartbeatBusy = true;

            try
            {
                if (!(DataContext is GatewayConfigMainViewModel vm)) return;
                if (vm.Gateways == null || vm.Gateways.Count == 0) return;

                var snapshotBefore = TakeStateSnapshot(vm);

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
            // ★ 窗口已关闭/不可见就不处理(兜底:防止已关闭的旧实例仍响应事件)
            if (!IsLoaded || !IsVisible) return;

            var dialog = new ChannelConfigDialog(tup.Gateway, tup.Channel);

            // ★ 只在 Owner 仍然有效时才设置,失败则让对话框独立显示,不崩
            try
            {
                dialog.Owner = this;
            }
            catch (InvalidOperationException)
            {
                // Owner 已关闭,不设 Owner
            }

            dialog.ShowDialog();
        }
    }
}
using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MaxChemical.Modules.GatewayConfig.Models;
using MaxChemical.Modules.GatewayConfig.ViewModels;
using MaxChemical.Modules.GatewayConfig.Views.Dialogs;
using Prism.Events;
using Prism.Ioc;

namespace MaxChemical.Modules.GatewayConfig.Views
{
    public partial class GatewayConfigMainView : Window
    {
        private readonly IEventAggregator _eventAggregator;

        public GatewayConfigMainView()
        {
            InitializeComponent();

            _eventAggregator = ContainerLocator.Container.Resolve<IEventAggregator>();
            _eventAggregator.GetEvent<OpenChannelDialogEvent>().Subscribe(OnOpenChannel, ThreadOption.UIThread);
            _eventAggregator.GetEvent<OpenGlobalParamsDialogEvent>().Subscribe(OnOpenGlobalParams, ThreadOption.UIThread);
            _eventAggregator.GetEvent<GatewayConfigStatusEvent>().Subscribe(msg =>
            {
                if (StatusBarText != null) StatusBarText.Text = msg;
            }, ThreadOption.UIThread);

            Loaded += OnWindowLoaded;

            Closed += async (s, e) =>
            {
                try
                {
                    var service = ContainerLocator.Container.Resolve<Services.IGatewayConfigService>();
                    await service.ShutdownAsync();
                }
                catch { }
            };
        }

        private async void OnWindowLoaded(object sender, RoutedEventArgs e)
        {
            if (!(DataContext is GatewayConfigMainViewModel vm)) return;

            // 1. 启动代理
            await vm.InitializeAsync();

            // 2. 自动搜索:Loading 持续到找到设备或 60 秒超时
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
                            found = true;
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        // 单次搜索失败不算超时,继续重试
                        System.Diagnostics.Debug.WriteLine($"搜索失败: {ex.Message}");
                    }

                    // 每次搜索之间稍微间隔一下,避免狂打 SDK
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
            }
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
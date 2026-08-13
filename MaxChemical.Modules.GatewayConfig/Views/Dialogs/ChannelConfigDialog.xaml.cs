using System.Windows;
using System.Windows.Input;
using MaxChemical.Core;
using MaxChemical.Data.Repositories;
using MaxChemical.Infrastructure.Services;
using MaxChemical.Modules.GatewayConfig.Models;
using MaxChemical.Modules.GatewayConfig.Services;
using MaxChemical.Modules.GatewayConfig.ViewModels;
using Prism.Events;
using Prism.Ioc;

namespace MaxChemical.Modules.GatewayConfig.Views.Dialogs
{
    public partial class ChannelConfigDialog : Window
    {
        private readonly ILocalizationService _localization;
        private readonly ChannelConfigViewModel _vm;
        private LoadingOverlayWindow _loadingOverlay;

        public ChannelConfigDialog(GatewayModel gateway, ChannelModel channel)
        {
            InitializeComponent();
            var c = ContainerLocator.Container;
            var service = c.Resolve<IGatewayConfigService>();
            var dialog = c.Resolve<IDialogService>();
            var ea = c.Resolve<IEventAggregator>();
            var deviceManager = c.Resolve<IDeviceManager>();
            var bindingRepo = c.Resolve<IGatewayBindingRepository>();
            var transportFactory = c.Resolve<IDeviceTransportFactory>();
            _localization = c.Resolve<ILocalizationService>();

            _vm = new ChannelConfigViewModel(
                gateway, channel,
                service, dialog, ea,
                deviceManager, bindingRepo, transportFactory);
            DataContext = _vm;

            _vm.CloseRequested += ok =>
            {
                try { DialogResult = ok; } catch { }
                Close();
            };
            _vm.ApplyStarted += _ => ShowLoading();
            _vm.ApplyFinished += (ok, msg) =>
            {
                HideLoading();
                var ds = c.Resolve<IDialogService>();
                if (ok) ds.ShowInfo(msg, "应用成功");
                else ds.ShowError(msg, "应用失败");
            };
            _vm.PollAttemptChanged += attempt =>
            {
                if (_loadingOverlay != null) _loadingOverlay.UpdateMessage(string.Format(_localization.GetString("Gateway_Starting", "设备重启中… 第 {0} 次探测"),attempt));
            };
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
                try { DragMove(); } catch { }
        }

        private void OnCancelClick(object sender, RoutedEventArgs e) => _vm.CancelCommand.Execute();

        private void ShowLoading()
        {
            _loadingOverlay = new LoadingOverlayWindow(this);
            _loadingOverlay.Show(_localization.GetString("Gateway_Start", "设备正在重启..."));
        }

        private void HideLoading()
        {
            _loadingOverlay?.Hide();
            _loadingOverlay = null;
        }
    }
}
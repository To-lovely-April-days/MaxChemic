using System.Windows;
using System.Windows.Input;
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
        private readonly ChannelConfigViewModel _vm;
        private LoadingOverlayWindow _loadingOverlay;

        public ChannelConfigDialog(GatewayModel gateway, ChannelModel channel)
        {
            InitializeComponent();
            var service = ContainerLocator.Container.Resolve<IGatewayConfigService>();
            var dialog = ContainerLocator.Container.Resolve<IDialogService>();
            var ea = ContainerLocator.Container.Resolve<IEventAggregator>();
            _vm = new ChannelConfigViewModel(gateway, channel, service, dialog, ea);
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
                var ds = ContainerLocator.Container.Resolve<IDialogService>();
                if (ok) ds.ShowInfo(msg, "应用成功");
                else ds.ShowError(msg, "应用失败");
            };
            _vm.PollAttemptChanged += attempt =>
            {
                if (_loadingOverlay != null) _loadingOverlay.UpdateMessage($"探测中… 第 {attempt} 次");
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
            _loadingOverlay.Show("网关正在重启…");
        }

        private void HideLoading()
        {
            _loadingOverlay?.Hide();
            _loadingOverlay = null;
        }
    }
}
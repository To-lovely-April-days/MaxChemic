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
    public partial class GlobalParamsDialog : Window
    {
        private readonly GlobalParamsViewModel _vm;
        private LoadingOverlayWindow _loadingOverlay;

        public GlobalParamsDialog(GatewayModel gateway)
        {
            InitializeComponent();
            var service = ContainerLocator.Container.Resolve<IGatewayConfigService>();
            var dialog = ContainerLocator.Container.Resolve<IDialogService>();
            var ea = ContainerLocator.Container.Resolve<IEventAggregator>();
            _vm = new GlobalParamsViewModel(gateway, service, dialog, ea);
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
                if (ok) ds.ShowInfo(msg, "操作成功");
                else ds.ShowError(msg, "操作失败");
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
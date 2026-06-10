using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows;
using MaxChemical.Modules.GatewayConfig.Models;
using MaxChemical.Modules.GatewayConfig.Services;
using Prism.Commands;
using Prism.Events;
using Prism.Mvvm;

namespace MaxChemical.Modules.GatewayConfig.ViewModels
{
    public class GatewayConfigMainViewModel : BindableBase
    {
        private readonly IGatewayConfigService _service;
        private readonly IEventAggregator _eventAggregator;

        public GatewayConfigMainViewModel(IGatewayConfigService service, IEventAggregator eventAggregator)
        {
            _service = service;
            _eventAggregator = eventAggregator;

            SearchCommand = new DelegateCommand(async () => await SearchAsync(),
                () => !IsSearching).ObservesProperty(() => IsSearching);
            RefreshCommand = new DelegateCommand(async () => await SearchAsync(),
      () => !IsSearching).ObservesProperty(() => IsSearching);
            SelectGatewayCommand = new DelegateCommand<GatewayModel>(g => SelectedGateway = g);
            OpenGlobalParamsCommand = new DelegateCommand(OnOpenGlobalParams,
                () => SelectedGateway != null).ObservesProperty(() => SelectedGateway);
            OpenChannelCommand = new DelegateCommand<ChannelModel>(OnOpenChannel,
                ch => ch != null && ch.IsAvailable);

            // Apply 完成后自动刷新列表 (无论成功失败,设备状态可能已变)
            _eventAggregator.GetEvent<GatewayApplyFinishedEvent>().Subscribe(async _ =>
            {
                await SearchAsync();
            }, ThreadOption.UIThread);
        }

        public ObservableCollection<GatewayModel> Gateways { get; } = new ObservableCollection<GatewayModel>();

        private GatewayModel _selectedGateway;
        public GatewayModel SelectedGateway
        {
            get => _selectedGateway;
            set => SetProperty(ref _selectedGateway, value);
        }

        private bool _isSearching;
        public bool IsSearching
        {
            get => _isSearching;
            set => SetProperty(ref _isSearching, value);
        }

        public DelegateCommand SearchCommand { get; }
        public DelegateCommand RefreshCommand { get; }
        public DelegateCommand<GatewayModel> SelectGatewayCommand { get; }
        public DelegateCommand OpenGlobalParamsCommand { get; }
        public DelegateCommand<ChannelModel> OpenChannelCommand { get; }

        public string SummaryText
        {
            get
            {
                if (Gateways.Count == 0) return "尚未搜索到任何网关";
                int onlineChannels = 0;
                foreach (var g in Gateways) onlineChannels += g.OnlineChannelCount;
                return $"已发现 {Gateways.Count} 台网关 · 共 {onlineChannels} 个通道在线";
            }
        }

        public async Task InitializeAsync()
        {
            try { await _service.InitializeAsync(); }
            catch (Exception ex)
            {
                PublishStatus($"初始化失败: {ex.Message}");
            }
        }

        private async Task SearchAsync()
        {
            IsSearching = true;
            PublishStatus("正在搜索网关…");
            try
            {
                var list = await _service.SearchAsync();
                Gateways.Clear();
                foreach (var g in list) Gateways.Add(g);
                if (Gateways.Count > 0) SelectedGateway = Gateways[0];
                RaisePropertyChanged(nameof(SummaryText));
                PublishStatus($"搜索完成 · 发现 {Gateways.Count} 台网关");
            }
            catch (Exception ex)
            {
                PublishStatus($"搜索失败: {ex.Message}");
                MessageBox.Show($"搜索失败:{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsSearching = false;
            }
        }
        /// <summary>
        /// 单次搜索 — 给主窗口加载时的自动搜索循环用。
        /// 不发状态消息,不弹错误框,只返回结果。
        /// </summary>
        public async Task<System.Collections.Generic.IReadOnlyList<GatewayModel>> AutoSearchOnceAsync()
        {
            var list = await _service.SearchAsync();
            if (list != null && list.Count > 0)
            {
                Gateways.Clear();
                foreach (var g in list) Gateways.Add(g);
                if (Gateways.Count > 0) SelectedGateway = Gateways[0];
                RaisePropertyChanged(nameof(SummaryText));
                PublishStatus($"自动搜索完成 · 发现 {Gateways.Count} 台网关");
            }
            return list;
        }
        private void OnOpenChannel(ChannelModel channel)
        {
            _eventAggregator.GetEvent<OpenChannelDialogEvent>().Publish((SelectedGateway, channel));
        }

        private void OnOpenGlobalParams()
        {
            _eventAggregator.GetEvent<OpenGlobalParamsDialogEvent>().Publish(SelectedGateway);
        }

        private void PublishStatus(string msg)
        {
            _eventAggregator.GetEvent<GatewayConfigStatusEvent>().Publish(msg);
        }
    }

    // ─── 模块内事件 ───
    public class OpenChannelDialogEvent : PubSubEvent<(GatewayModel Gateway, ChannelModel Channel)> { }
    public class OpenGlobalParamsDialogEvent : PubSubEvent<GatewayModel> { }
    public class GatewayConfigStatusEvent : PubSubEvent<string> { }
    public class GatewayApplyFinishedEvent : PubSubEvent<bool> { }
}
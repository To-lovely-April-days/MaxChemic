using MaxChemical.Infrastructure.Events;
using MaxChemical.Infrastructure.Services;
using MaxChemical.Modules.GatewayConfig.Models;
using MaxChemical.Modules.GatewayConfig.Services;
using Org.BouncyCastle.Asn1.Mozilla;
using Prism.Commands;
using Prism.Events;
using Prism.Mvvm;
using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows;

namespace MaxChemical.Modules.GatewayConfig.ViewModels
{
    public class GatewayConfigMainViewModel : BindableBase
    {
        private readonly IGatewayConfigService _service;
        private readonly IEventAggregator _eventAggregator;
        private readonly ILocalizationService _localizationService;

        public GatewayConfigMainViewModel(IGatewayConfigService service, IEventAggregator eventAggregator, ILocalizationService localizationService)
        {
            _service = service;
            _eventAggregator = eventAggregator;
            _localizationService = localizationService;

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

            // 初始化本地化文本
            LoadLocalizationTxt("");

            _eventAggregator.GetEvent<LanguageChangedEvent>()?.Subscribe(LoadLocalizationTxt);
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
                if (Gateways.Count == 0) return _localizationService.GetString("Gateway_Summary_None", "尚未搜索到任何网关");
                int onlineChannels = 0;
                foreach (var g in Gateways) onlineChannels += g.OnlineChannelCount;
                return string.Format(
                    _localizationService.GetString("Gateway_Summary_Already", "已发现 {0} 台网关 · 共 {1} 个通道在线"),
                    Gateways.Count, onlineChannels);
            }
        }


        #region 本地化属性

        private string _titleTxt = "网关配置";
        private string _refreshTxt = "刷新";
        private string _gatewayTxt = "网关";
        private string _channelTxt = "通道";
        private string _onlineTxt = "在线";
        private string _channelOnline = "通道在线";
        private string _firmwareTxt = "固件";
        private string _tipTxt = "点击网关本体上的 DB9 或端子排,进入对应通道参数配置";
        private string _waitTxt;


        public string TitleTxt { get { return _titleTxt; } set { SetProperty(ref _titleTxt, value); } }
        public string RefreshTxt { get { return _refreshTxt; } set { SetProperty(ref _refreshTxt, value); } }
        public string GatewayTxt { get { return _gatewayTxt; } set { SetProperty(ref _gatewayTxt, value); } }
        public string ChannelTxt { get { return _channelTxt; } set { SetProperty(ref _channelTxt, value); } }
        public string OnlineTxt { get { return _onlineTxt; } set { SetProperty(ref _onlineTxt, value); } }
        public string ChannelOnline { get { return _channelOnline; } set { SetProperty(ref _channelOnline, value); } }
        public string FirmwareTxt { get { return _firmwareTxt; } set { SetProperty(ref _firmwareTxt, value); } }
        public string TipTxt { get { return _tipTxt; } set { SetProperty(ref _tipTxt, value); } }
        public string WaitTxt { get { return _waitTxt; } set { SetProperty(ref _waitTxt, value); } }

        private void LoadLocalizationTxt(string culture)
        {
            TitleTxt = _localizationService.GetString("Gateway_Title", "网关配置");
            RefreshTxt = _localizationService.GetString("Gateway_Refresh", "刷新");
            GatewayTxt = _localizationService.GetString("Gateway_List_Title", "网关");
            ChannelTxt = _localizationService.GetString("Gateway_Channel", "通道");
            OnlineTxt = _localizationService.GetString("Gateway_Online", "在线");
            ChannelOnline = _localizationService.GetString("Gateway_ChannelOnline", "通道在线");
            FirmwareTxt = _localizationService.GetString("Gateway_Firmware", "固件");
            TipTxt = _localizationService.GetString("Gateway_Tip", "点击网关本体上的 DB9 或端子排,进入对应通道参数配置");
            WaitTxt = _localizationService.GetString("Gateway_WaitingMsg", "请耐心等待,最长约需 30 秒");
        }

        #endregion


        public async Task InitializeAsync()
        {
            try { await _service.InitializeAsync(); }
            catch (Exception ex)
            {
                PublishStatus(string.Format(_localizationService.GetString("Gateway_StatusMsg_Fail", "初始化失败: {0}"), ex.Message));
            }
        }

        private async Task SearchAsync()
        {
            IsSearching = true;
            PublishStatus(_localizationService.GetString("Gateway_Loading", "正在搜索网关…"));
            try
            {
                var list = await _service.SearchAsync();
                Gateways.Clear();
                foreach (var g in list) Gateways.Add(g);
                if (Gateways.Count > 0) SelectedGateway = Gateways[0];
                RaisePropertyChanged(nameof(SummaryText));
                PublishStatus(string.Format(_localizationService.GetString("Gateway_StatusMsg_Find", "搜索完成 · 发现 {0} 台网关"), Gateways.Count));
            }
            catch (Exception ex)
            {
                PublishStatus(string.Format(_localizationService.GetString("Gateway_StatusMsg_Serach", "初始化失败: {0}"), ex.Message));
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
                PublishStatus(string.Format(_localizationService.GetString("Gateway_StatusMsg_Auto", "自动搜索完成 · 发现 {0} 台网关"), Gateways.Count));
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
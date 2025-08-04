using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using DevicePlugins.Devices;
using MaxChemical.Infrastructure.Events;
using MaxChemical.Infrastructure.Services;
using MaxChemical.Modules.Designer.Services;
using MaxChemical.Modules.Device.Views;
using Prism.Commands;
using Prism.Events;
using Prism.Mvvm;
using Serilog;

namespace MaxChemical.Modules.Device.ViewModels
{
    public class DeviceLibraryViewModel : BindableBase
    {
        private readonly IDeviceManager _deviceManager;
        private readonly IEventAggregator _eventAggregator;
        private readonly IDragDropService _dragDropService;
        private readonly ILocalizationService _localizationService;
        private string _searchText = string.Empty;
        private ObservableCollection<DeviceGroup> _deviceGroups;

        // 本地化属性
        private string _searchPlaceholder = string.Empty;
        private string _clearSearchTooltip = string.Empty;
        private string _deviceLibraryTitle = string.Empty;

        public DeviceLibraryViewModel(
            IDeviceManager deviceManager,
            IEventAggregator eventAggregator,
            IDragDropService dropService,
            ILocalizationService localizationService)
        {
            _deviceManager = deviceManager ?? throw new ArgumentNullException(nameof(deviceManager));
            _eventAggregator = eventAggregator ?? throw new ArgumentNullException(nameof(eventAggregator));
            _dragDropService = dropService ?? throw new ArgumentNullException(nameof(dropService));
            _localizationService = localizationService ?? throw new ArgumentNullException(nameof(localizationService));

            ClearSearchCommand = new DelegateCommand(ClearSearch);
            RefreshDevicesCommand = new DelegateCommand(RefreshDevices);
            DeviceSelectedCommand = new DelegateCommand<IDevice>(OnDeviceSelected);
            MenuCommand = new DelegateCommand(OnMenuCommand);

            LoadLocalizedStrings();
            SubscribeEvents();
            InitializeDevices();
        }

        #region Localized Properties

        public string SearchPlaceholder
        {
            get => _searchPlaceholder;
            set => SetProperty(ref _searchPlaceholder, value);
        }

        public string ClearSearchTooltip
        {
            get => _clearSearchTooltip;
            set => SetProperty(ref _clearSearchTooltip, value);
        }

        public string DeviceLibraryTitle
        {
            get => _deviceLibraryTitle;
            set => SetProperty(ref _deviceLibraryTitle, value);
        }

        #endregion

        #region Properties

        public string SearchText
        {
            get => _searchText;
            set
            {
                SetProperty(ref _searchText, value);
                RaisePropertyChanged(nameof(HasSearchText));
                FilterDevices();
            }
        }

        public bool HasSearchText => !string.IsNullOrEmpty(SearchText);

        public ObservableCollection<DeviceGroup> DeviceGroups
        {
            get => _deviceGroups;
            set => SetProperty(ref _deviceGroups, value);
        }

        #endregion

        #region Commands

        public ICommand ClearSearchCommand { get; }
        public ICommand RefreshDevicesCommand { get; }
        public ICommand DeviceSelectedCommand { get; }
        public ICommand MenuCommand { get; }

        #endregion

        private void LoadLocalizedStrings()
        {
            SearchPlaceholder = _localizationService.GetString("Device_SearchPlaceholder");
            ClearSearchTooltip = _localizationService.GetString("Device_ClearSearch");
            DeviceLibraryTitle = _localizationService.GetString("Device_Library");
        }

        private void SubscribeEvents()
        {
            _eventAggregator.GetEvent<LanguageChangedEvent>().Subscribe(OnLanguageChanged);
        }

        private void OnLanguageChanged(string cultureName)
        {
            LoadLocalizedStrings();
            // 重新构建设备分组以更新分类显示名称
            BuildDeviceGroups();
        }

        private void OnMenuCommand()
        {
            // 发布面板可见性切换事件到主窗口
            _eventAggregator.GetEvent<PanelVisibilityEvent>().Publish(false);
            var logMessage = _localizationService.GetString("Device_Log_PanelHideRequest");
            Log.Information(logMessage);
        }

        private void InitializeDevices()
        {
            try
            {
                var logMessage = _localizationService.GetString("Device_Log_Initialize");
                Log.Information(logMessage);

                // 加载设备
                _deviceManager.LoadDevices();

                // 构建设备分组
                BuildDeviceGroups();

                var completeMessage = _localizationService.GetString("Device_Log_InitializeComplete");
                Log.Information(completeMessage);
            }
            catch (Exception ex)
            {
                var errorMessage = _localizationService.GetString("Device_Log_InitializeError");
                Log.Error(ex, errorMessage);
            }
        }

        private void BuildDeviceGroups()
        {
            var groups = new List<DeviceGroup>();

            // 按类别分组设备
            var categories = _deviceManager.GetCategories();

            foreach (var category in categories)
            {
                var devices = _deviceManager.GetDevicesByCategory(category);
                if (devices.Any())
                {
                    var group = new DeviceGroup
                    {
                        CategoryName = category,
                        DisplayName = GetCategoryDisplayName(category),
                        IconPath = GetCategoryIconPath(category),
                        IsExpanded = true, // 设置默认全部展开
                        Devices = new ObservableCollection<IDevice>(devices)
                    };
                    groups.Add(group);
                }
            }

            DeviceGroups = new ObservableCollection<DeviceGroup>(groups.OrderBy(g => g.DisplayName));
        }

        private void FilterDevices()
        {
            if (string.IsNullOrEmpty(SearchText))
            {
                DeviceGroups.Clear();
                // 显示所有设备
                BuildDeviceGroups();
                return;
            }

            var filteredGroups = new List<DeviceGroup>();
            var searchTerm = SearchText.ToLower();

            foreach (var originalGroup in DeviceGroups)
            {
                var filteredDevices = originalGroup.Devices
                    .Where(d => d.Name.ToLower().Contains(searchTerm) ||
                               d.Category.ToLower().Contains(searchTerm) ||
                               d.Manufacturer.ToLower().Contains(searchTerm))
                    .ToList();

                if (filteredDevices.Any())
                {
                    var group = new DeviceGroup
                    {
                        CategoryName = originalGroup.CategoryName,
                        DisplayName = originalGroup.DisplayName,
                        Icon = originalGroup.Icon,
                        IconPath = originalGroup.IconPath,
                        IsExpanded = true, // 搜索时展开所有组
                        Devices = new ObservableCollection<IDevice>(filteredDevices)
                    };
                    filteredGroups.Add(group);
                }
            }

            DeviceGroups = new ObservableCollection<DeviceGroup>(filteredGroups);
        }

        private void ClearSearch()
        {
            SearchText = string.Empty;
        }

        private void RefreshDevices()
        {
            try
            {
                var logMessage = _localizationService.GetString("Device_Log_Refresh");
                Log.Information(logMessage);

                // 卸载现有设备
                _deviceManager.UnloadDevices();

                // 重新加载设备
                _deviceManager.LoadDevices();

                // 重新构建分组
                BuildDeviceGroups();

                var completeMessage = _localizationService.GetString("Device_Log_RefreshComplete");
                Log.Information(completeMessage);
            }
            catch (Exception ex)
            {
                var errorMessage = _localizationService.GetString("Device_Log_RefreshError");
                Log.Error(ex, errorMessage);
            }
        }

        private void OnDeviceSelected(IDevice device)
        {
            if (device != null)
            {
                var logMessage = string.Format(_localizationService.GetString("Device_Log_DeviceSelected"), device.Name);
                Log.Information(logMessage);

                // 发布设备选择事件
                _eventAggregator.GetEvent<DeviceSelectedEvent>().Publish(device);
            }
        }

        private string GetCategoryDisplayName(string category)
        {
            return category switch
            {
                DeviceCategories.Pumps => _localizationService.GetString("Device_Category_Pumps"),
                DeviceCategories.Valves => _localizationService.GetString("Device_Category_Valves"),
                DeviceCategories.Sensors => _localizationService.GetString("Device_Category_Sensors"),
                DeviceCategories.LevelGauge => _localizationService.GetString("Device_Category_LevelGauge"),
                DeviceCategories.Filter => _localizationService.GetString("Device_Category_Filter"),
                DeviceCategories.Reactors => _localizationService.GetString("Device_Category_Reactors"),
                DeviceCategories.HeatExchanger => _localizationService.GetString("Device_Category_HeatExchanger"),
                DeviceCategories.LiquidAccumulator => _localizationService.GetString("Device_Category_LiquidAccumulator"),
                DeviceCategories.PressureGauge => _localizationService.GetString("Device_Category_PressureGauge"),
                DeviceCategories.Flowmeter => _localizationService.GetString("Device_Category_Flowmeter"),
                DeviceCategories.ElectronicScales => _localizationService.GetString("Device_Category_ElectronicScales"),
                DeviceCategories.PublicWorks => _localizationService.GetString("Device_Category_PublicWorks"),
                DeviceCategories.InlineDetection => _localizationService.GetString("Device_Category_InlineDetection"),
                _ => category
            };
        }

        private string GetCategoryIconPath(string category)
        {
            return category switch
            {
                DeviceCategories.Pumps => "pack://siteoforigin:,,,/Resources/Icon/Pumps.png",
                DeviceCategories.Valves => "pack://siteoforigin:,,,/Resources/Icon/Valves.png",
                DeviceCategories.Sensors => "pack://siteoforigin:,,,/Resources/Icon/Sensors.png",
                DeviceCategories.LevelGauge => "pack://siteoforigin:,,,/Resources/Icon/LevelGauge.png",
                DeviceCategories.Filter => "pack://siteoforigin:,,,/Resources/Icon/Filter.png",
                DeviceCategories.Reactors => "pack://siteoforigin:,,,/Resources/Icon/Reactors.png",
                DeviceCategories.HeatExchanger => "pack://siteoforigin:,,,/Resources/Icon/HeatExchanger.png",
                DeviceCategories.LiquidAccumulator => "pack://siteoforigin:,,,/Resources/Icon/LiquidAccumulator.png",
                DeviceCategories.PressureGauge => "pack://siteoforigin:,,,/Resources/Icon/PressureGauge.png",
                DeviceCategories.Flowmeter => "pack://siteoforigin:,,,/Resources/Icon/Flowmeter.png",
                DeviceCategories.ElectronicScales => "pack://siteoforigin:,,,/Resources/Icon/ElectronicScales.png",
                DeviceCategories.PublicWorks => "pack://siteoforigin:,,,/Resources/Icon/PublicWorks.png",
                DeviceCategories.InlineDetection => "pack://siteoforigin:,,,/Resources/Icon/InlineDetection.png",
                _ => "pack://siteoforigin:,,,/Resources/Icon/Default.png"
            };
        }

        public void PublishDragCompleteEvent(IDevice device, DragDropEffects result)
        {
            try
            {
                _eventAggregator?.GetEvent<DeviceDragCompleteEvent>()?.Publish(new DeviceDragEventArgs
                {
                    Device = device,
                    DragResult = result,
                    EndTime = DateTime.Now
                });
            }
            catch (Exception ex)
            {
                var errorMessage = string.Format(_localizationService.GetString("Device_Log_DragEventFailed"), ex.Message);
                Debug.WriteLine(errorMessage);
            }
        }

        public void StartDeviceDrag(IDevice device, FrameworkElement sourceElement, Point startPosition)
        {
            _dragDropService?.StartDeviceDrag(device, sourceElement, startPosition);
        }

        public void EndDeviceDrag(IDevice device, DragDropEffects result)
        {
            _dragDropService?.EndDrag(new Point(), result == DragDropEffects.Copy || result == DragDropEffects.Move, "External");
        }
    }

    public class DeviceGroup : BindableBase
    {
        private bool _isExpanded;

        public string CategoryName { get; set; }
        public string DisplayName { get; set; }
        public string Icon { get; set; }
        public string IconPath { get; set; }
        public ObservableCollection<IDevice> Devices { get; set; }

        public bool IsExpanded
        {
            get => _isExpanded;
            set => SetProperty(ref _isExpanded, value);
        }
    }

    // 设备选择事件
    public class DeviceSelectedEvent : PubSubEvent<IDevice>
    {
    }
}
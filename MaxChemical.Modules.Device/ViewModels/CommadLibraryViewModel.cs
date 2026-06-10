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
using MaxChemical.Modules.Designer.Event;
using MaxChemical.Modules.Designer.ViewModels;
using MaxChemical.Modules.Device.Events;
using Prism.Commands;
using Prism.Events;
using Prism.Mvvm;
using Serilog;
using RequestCanvasDevicesEvent = MaxChemical.Modules.Designer.Event.RequestCanvasDevicesEvent;

namespace MaxChemical.Modules.Device.ViewModels
{
    public class CommadLibraryViewModel : BindableBase
    {
        private readonly IDeviceManager _deviceManager;
        private readonly IEventAggregator _eventAggregator;
        private readonly ILocalizationService _localizationServer;

        private string _searchText = string.Empty;
        private string _searchPlaceholder = string.Empty;
        private string _libraryTitle = string.Empty;
        private string _deviceCommandLabel;
        private string _clearSearch;
        private bool _hasSearchText;

        private ObservableCollection<CanvasDeviceWithCommands> _devices;

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

        public bool HasSearchText => !string.IsNullOrEmpty(_searchText);


        public ObservableCollection<CanvasDeviceWithCommands> Devices
        {
            get => _devices;
            set => SetProperty(ref _devices, value);
        }

        // 绑定属性
        public string SearchPlaceholder
        {
            get => _searchPlaceholder;
            set
            {
                SetProperty(ref _searchPlaceholder, value);
            }
        }

        public string LibraryTitle
        {
            get => _libraryTitle;
            set => SetProperty(ref _libraryTitle, value);
        }

        public string DeviceCommandLabel
        {
            get => _deviceCommandLabel;
            set => SetProperty(ref _deviceCommandLabel, value);
        }

        public string ClearSearchTooltip
        {
            get => _clearSearch;
            set => SetProperty(ref _clearSearch, value);
        }


        public ICommand ClearSearchCommand { get; }
        public ICommand RefreshDevicesCommand { get; }
        public ICommand CommandSelectedCommand { get; }
        public ICommand MenuCommand { get; }


        public CommadLibraryViewModel(IDeviceManager deviceManager, IEventAggregator eventAggregator, ILocalizationService localization)
        {
            _deviceManager = deviceManager ?? throw new ArgumentNullException(nameof(deviceManager));
            _eventAggregator = eventAggregator ?? throw new ArgumentNullException(nameof(eventAggregator));
            _localizationServer = localization ?? throw new ArgumentNullException(nameof(localization));

            ClearSearchCommand = new DelegateCommand(ClearSearch);
            RefreshDevicesCommand = new DelegateCommand(RefreshDevices);
            CommandSelectedCommand = new DelegateCommand<DeviceCommand>(OnCommandSelected);
            MenuCommand = new DelegateCommand(OnMenuClick);

            InitializeDevices();
            SubscribeToEvents();
            LanguageChangedHandler("");
        }

        private void InitializeDevices()
        {
            try
            {
                Log.Information("初始化设备命令库...");
                Devices = new ObservableCollection<CanvasDeviceWithCommands>();
                BuildDeviceList();
                Log.Information("设备命令库初始化完成");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "初始化设备命令库时发生错误");
            }
        }

        private void SubscribeToEvents()
        {
            // 订阅画布设备变化事件
            _eventAggregator?.GetEvent<Designer.Event.CanvasDevicesChangedEvent>()?.Subscribe(OnCanvasDevicesChanged);
            _eventAggregator?.GetEvent<CanvasModifiedEvent>()?.Subscribe(OnCanvasModified);
            _eventAggregator.GetEvent<LanguageChangedEvent>()?.Subscribe(LanguageChangedHandler);
        }

        private void OnCanvasDevicesChanged(List<CanvasDeviceViewModel> canvasDevices)
        {
            try
            {
                Log.Information($"画布设备变化，更新命令库: {canvasDevices?.Count ?? 0} 个设备");
                BuildDeviceListFromCanvas(canvasDevices);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "处理画布设备变化时发生错误");
            }
        }

        private void OnCanvasModified()
        {
            // 当画布修改时，请求最新的设备列表
            _eventAggregator?.GetEvent<RequestCanvasDevicesEvent>()?.Publish(OnCanvasDevicesReceived);
        }

        private void OnCanvasDevicesReceived(List<CanvasDeviceViewModel> canvasDevices)
        {
            BuildDeviceListFromCanvas(canvasDevices);
        }

        private void BuildDeviceList()
        {
            // 请求画布上的设备
            _eventAggregator?.GetEvent<RequestCanvasDevicesEvent>()?.Publish(OnCanvasDevicesReceived);
        }

        private void BuildDeviceListFromCanvas(List<CanvasDeviceViewModel> canvasDevices)
        {
            try
            {
                if (Devices == null)
                {
                    Devices = new ObservableCollection<CanvasDeviceWithCommands>();
                }

                Devices.Clear();

                if (canvasDevices == null || !canvasDevices.Any())
                {
                    Debug.WriteLine("画布上没有设备");
                    return;
                }

                // 根据设备名称去重，只保留第一个出现的设备
                var uniqueDevices = canvasDevices
                    .Where(d => d.Device != null)
                    .GroupBy(d => d.Device.Name)
                    .Select(g => g.First())
                    .OrderBy(d => d.Device.Name);

                // 直接为每个唯一设备创建命令组
                foreach (var canvasDevice in uniqueDevices)
                {
                    var commands = canvasDevice.Device.Commands;
                    var deviceWithCommands = new CanvasDeviceWithCommands
                    {
                        CanvasDevice = canvasDevice,
                        Commands = new ObservableCollection<DeviceCommand>(commands),
                        IsExpanded = false,
                        IsVisible = true
                    };

                    Devices.Add(deviceWithCommands);
                }

                Debug.WriteLine($"构建了 {Devices.Count} 个唯一设备");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "构建设备列表时发生错误");
            }
        }

        private void FilterDevices()
        {
            if (string.IsNullOrEmpty(SearchText))
            {
                // 显示所有设备
                foreach (var deviceWithCommands in Devices ?? Enumerable.Empty<CanvasDeviceWithCommands>())
                {
                    deviceWithCommands.IsVisible = true;
                    foreach (var command in deviceWithCommands.Commands)
                    {
                        command.IsEnabled = true;
                    }
                }
            }
            else
            {
                // 过滤设备和命令
                var searchLower = SearchText.ToLower();
                foreach (var deviceWithCommands in Devices ?? Enumerable.Empty<CanvasDeviceWithCommands>())
                {
                    bool deviceMatches = deviceWithCommands.CanvasDevice.Device.Name.ToLower().Contains(searchLower);
                    bool hasMatchingCommands = false;

                    foreach (var command in deviceWithCommands.Commands)
                    {
                        bool commandMatches = command.Name.ToLower().Contains(searchLower) ||
                                            command.HelpText.ToLower().Contains(searchLower);
                        command.IsEnabled = commandMatches;
                        if (commandMatches)
                            hasMatchingCommands = true;
                    }

                    deviceWithCommands.IsVisible = deviceMatches || hasMatchingCommands;
                    if (hasMatchingCommands)
                    {
                        deviceWithCommands.IsExpanded = true; // 自动展开有匹配命令的设备
                    }
                }
            }
        }

        private void ClearSearch()
        {
            SearchText = string.Empty;
        }

        private void RefreshDevices()
        {
            try
            {
                Log.Information("刷新设备命令库...");
                BuildDeviceList();
                Log.Information("设备命令库刷新完成");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "刷新设备命令库时发生错误");
            }
        }

        private void OnCommandSelected(DeviceCommand command)
        {
            if (command != null)
            {
                Log.Information($"选择命令: {command.Name}");

                // 发布命令选择事件
                _eventAggregator?.GetEvent<Designer.Event.DeviceCommandSelectedEvent>()?.Publish(command);
            }
        }

        private void OnMenuClick()
        {
            // 处理菜单点击
            Debug.WriteLine("设备命令库菜单点击");
        }

        public void PublishCommandDragCompleteEvent(DeviceCommand command, DragDropEffects result)
        {
            try
            {
                Log.Information($"发布命令拖拽完成事件: {command.Name}, 结果: {result}");
                _eventAggregator?.GetEvent<CommandDragCompleteEvent>()?.Publish(new CommandDragResult
                {
                    Command = command,
                    DragResult = result,
                    CompletedAt = DateTime.Now
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "发布命令拖拽完成事件时发生错误");
            }
        }


        private void LanguageChangedHandler(string cultureName)
        {
            SearchPlaceholder = _localizationServer.GetString("Device_Command_SearchPlaceholder");
            DeviceCommandLabel = _localizationServer.GetString("Device_Command_Label");
            LibraryTitle = _localizationServer.GetString("Device_Command_Library");
            ClearSearchTooltip = _localizationServer.GetString("Device_Command_ClearSearch");
        }
    }

    // 数据模型类
    public class CanvasDeviceWithCommands : BindableBase
    {
        private bool _isExpanded;
        private bool _isVisible = true;

        public CanvasDeviceViewModel CanvasDevice { get; set; }

        public ObservableCollection<DeviceCommand> Commands { get; set; } = new();

        public bool IsExpanded
        {
            get => _isExpanded;
            set => SetProperty(ref _isExpanded, value);
        }

        public bool IsVisible
        {
            get => _isVisible;
            set => SetProperty(ref _isVisible, value);
        }
    }
}
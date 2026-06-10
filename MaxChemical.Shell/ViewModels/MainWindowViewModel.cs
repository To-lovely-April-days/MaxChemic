using MaxChemical.Infrastructure.Events;
using MaxChemical.Infrastructure.Constants;
using Prism.Commands;
using Prism.Events;
using Prism.Mvvm;
using Prism.Regions;
using System;
using System.Diagnostics;
using System.Windows.Input;

namespace MaxChemical.Shell.ViewModels
{
    public class MainWindowViewModel : BindableBase
    {
        private readonly IEventAggregator _eventAggregator;
        private readonly IRegionManager _regionManager;

        private bool _isLeftPanelVisible = true;
        private bool _isStatusBarVisible = true;
        private bool _isDeviceLibraryVisible = true;  // 默认显示设备库
        private bool _isCommadVisible = false;

        public MainWindowViewModel(IEventAggregator eventAggregator, IRegionManager regionManager)
        {
            _eventAggregator = eventAggregator;
            _regionManager = regionManager;

            // 初始化命令
            ToggleLeftPanelCommand = new DelegateCommand(ToggleLeftPanel);

            SubscribeEvents();
            InitializeDefaultView();
        }

        #region Properties

        public bool IsLeftPanelVisible
        {
            get => _isLeftPanelVisible;
            set => SetProperty(ref _isLeftPanelVisible, value);
        }

        public bool IsStatusBarVisible
        {
            get => _isStatusBarVisible;
            set => SetProperty(ref _isStatusBarVisible, value);
        }

        public bool IsDeviceLibraryVisible
        {
            get => _isDeviceLibraryVisible;
            set => SetProperty(ref _isDeviceLibraryVisible, value);
        }

        public bool IsCommadVisible
        {
            get => _isCommadVisible;
            set => SetProperty(ref _isCommadVisible, value);
        }

        #endregion

        #region Commands

        public ICommand ToggleLeftPanelCommand { get; }

        #endregion

        private void SubscribeEvents()
        {
            _eventAggregator.GetEvent<LeftPanelVisibilityChangedEvent>().Subscribe(OnLeftPanelVisibilityChanged);
            _eventAggregator.GetEvent<StatusBarVisibilityChangedEvent>().Subscribe(OnStatusBarVisibilityChanged);
            _eventAggregator.GetEvent<LeftPanelChangeRequestEvent>().Subscribe(OnLeftPanelChangeRequested);

            // 订阅面板可见性切换事件
            _eventAggregator.GetEvent<PanelVisibilityEvent>().Subscribe(OnPanelVisibilityChanged);
        }

        private void ToggleLeftPanel()
        {
            IsLeftPanelVisible = !IsLeftPanelVisible;
            Debug.WriteLine($"[MainWindow] 手动切换左侧面板状态: {(IsLeftPanelVisible ? "显示" : "隐藏")}");
        }

        private void OnPanelVisibilityChanged(bool isVisible)
        {
            IsLeftPanelVisible = isVisible;
            Debug.WriteLine($"[MainWindow] 接收到面板可见性事件: {(isVisible ? "显示" : "隐藏")}");
        }

        private void InitializeDefaultView()
        {
            try
            {
                // 也可以发布事件通知其他组件
                _eventAggregator.GetEvent<StatusMessageChangedEvent>().Publish("工艺设计器已加载");
            }
            catch (Exception ex)
            {
                // 可以记录日志或显示错误信息
                System.Diagnostics.Debug.WriteLine($"导航到默认视图失败: {ex.Message}");
            }
        }

        #region Event Handlers

        private void OnLeftPanelVisibilityChanged(bool isVisible)
        {
            IsLeftPanelVisible = isVisible;
        }

        private void OnStatusBarVisibilityChanged(bool isVisible)
        {
            IsStatusBarVisible = isVisible;
        }

        // 切换左侧面板的方法
        private void OnLeftPanelChangeRequested(string regionName)
        {
            switch (regionName)
            {
                case RegionNames.DeviceLibraryRegion:
                    IsDeviceLibraryVisible = true;
                    IsCommadVisible = false;
                    Debug.WriteLine("[MainWindow] 切换到设备库视图");
                    break;

                case RegionNames.CommadLibraryRegion:
                    IsDeviceLibraryVisible = false;
                    IsCommadVisible = true;
                    Debug.WriteLine("[MainWindow] 切换到工具箱视图");
                    break;

                default:
                    Debug.WriteLine($"[MainWindow] 未知的区域名称: {regionName}");
                    break;
            }
            Debug.WriteLine($"[MainWindow] 左侧面板切换到: {regionName}");
        }

        #endregion
    }
}
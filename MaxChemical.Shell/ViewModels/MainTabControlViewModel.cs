using MaxChemical.Infrastructure.Constants;
using MaxChemical.Infrastructure.Events;
using MaxChemical.Infrastructure.Services;
using MaxChemical.Modules.Designer.Event;
using Prism.Commands;
using Prism.Events;
using Prism.Mvvm;
using Prism.Regions;
using System;
using System.Diagnostics;

namespace MaxChemical.Shell.ViewModels
{
    public class MainTabControlViewModel : BindableBase
    {
        private readonly IEventAggregator _eventAggregator;
        private readonly ILocalizationService _localizationService;
        private readonly IDialogService _dialogService;
        private readonly IRegionManager _regionManager;

        private string _processDesignerTabText = "工艺设计器";
        private string _flowDesignerTabText = "流程设计器";
        private string _variableText = "查看变量管理";

        private bool _isProcessDesignerSelected = true;
        private bool _isFlowDesignerSelected = false;
        private bool _isVariableManagementExpanded = false; // 新增
        private bool _isVariableManagementVisible = false; // 新增
                                                           
        private string _projectName = "未命名项目";
        // 添加计算属性
        public bool IsProjectNameVisible => !string.IsNullOrWhiteSpace(_projectName)
                                            && _projectName != "未命名项目";
        public MainTabControlViewModel(
            IEventAggregator eventAggregator,
            ILocalizationService localizationService,
            IDialogService dialogService,
            IRegionManager regionManager)
        {
            _eventAggregator = eventAggregator;
            _localizationService = localizationService;
            _dialogService = dialogService;
            _regionManager = regionManager;

            Debug.WriteLine("MainTabControlViewModel 构造函数开始");

            InitializeCommands();
            InitializeProperties();
            SubscribeEvents();

            // 初始化时设置默认的左侧面板
            NotifyLeftPanelChange();

            Debug.WriteLine($"MainTabControlViewModel 构造完成 - Process: {IsProcessDesignerSelected}, Flow: {IsFlowDesignerSelected}");
        }

        #region Properties

        public string ProcessDesignerTabText
        {
            get => _processDesignerTabText;
            set => SetProperty(ref _processDesignerTabText, value);
        }

        public string FlowDesignerTabText
        {
            get => _flowDesignerTabText;
            set => SetProperty(ref _flowDesignerTabText, value);
        }

        public string VariableManagementTabText
        {
            get => _variableText;
            set => SetProperty(ref _variableText, value);
        }

        public bool IsProcessDesignerSelected
        {
            get => _isProcessDesignerSelected;
            set
            {
                Debug.WriteLine($"[ViewModel] 设置 IsProcessDesignerSelected: {_isProcessDesignerSelected} -> {value}");
                SetProperty(ref _isProcessDesignerSelected, value);
            }
        }

        public bool IsFlowDesignerSelected
        {
            get => _isFlowDesignerSelected;
            set
            {
                Debug.WriteLine($"[ViewModel] 设置 IsFlowDesignerSelected: {_isFlowDesignerSelected} -> {value}");
                SetProperty(ref _isFlowDesignerSelected, value);
            }
        }

        // 新增属性
        public bool IsVariableManagementExpanded
        {
            get => _isVariableManagementExpanded;
            set => SetProperty(ref _isVariableManagementExpanded, value);
        }

        // 新增属性
        public bool IsVariableManagementVisible
        {
            get => _isVariableManagementVisible;
            set => SetProperty(ref _isVariableManagementVisible, value);
        }
      

        // 添加属性
        public string ProjectName
        {
            get => _projectName;
            set => SetProperty(ref _projectName, value);
        }
        #endregion

        #region Commands

        public DelegateCommand SelectProcessDesignerCommand { get; private set; } = null!;
        public DelegateCommand SelectFlowDesignerCommand { get; private set; } = null!;
        public DelegateCommand ExportCommand { get; private set; } = null!;
        public DelegateCommand ImportCommand { get; private set; } = null!;
        public DelegateCommand AddNewCommand { get; private set; } = null!;
        public DelegateCommand ViewVariableManagementCommand { get; private set; } = null!; // 新增

        #endregion

        private void InitializeCommands()
        {
            SelectProcessDesignerCommand = new DelegateCommand(SelectProcessDesigner);
            SelectFlowDesignerCommand = new DelegateCommand(SelectFlowDesigner);
            ExportCommand = new DelegateCommand(OnExport);
            ImportCommand = new DelegateCommand(OnImport);
            AddNewCommand = new DelegateCommand(OnAddNew);
            ViewVariableManagementCommand = new DelegateCommand(OnViewVariableManagement); // 新增

            Debug.WriteLine("Commands 初始化完成");
        }

        // 新增方法
        private void OnViewVariableManagement()
        {
            try
            {
                IsVariableManagementExpanded = !IsVariableManagementExpanded;

                string status = IsVariableManagementExpanded ? "展开" : "折叠";
                _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish($"{status}变量管理面板");

                Debug.WriteLine($"变量管理面板{status}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"切换变量管理面板失败: {ex.Message}");
            }
        }

        private void InitializeProperties()
        {
            try
            {
                ProcessDesignerTabText = _localizationService?.GetString("Tab_ProcessDesigner") ?? "工艺设计器";
                FlowDesignerTabText = _localizationService?.GetString("Tab_FlowDesigner") ?? "流程设计器"; 
                VariableManagementTabText = _localizationService?.GetString("Tab_Veriable") ?? "查看变量管理";
                Debug.WriteLine("Properties 初始化完成");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"初始化属性失败: {ex.Message}");
            }
        }

        private void SubscribeEvents()
        {
            try
            {
                _eventAggregator?.GetEvent<LanguageChangedEvent>()?.Subscribe(OnLanguageChanged);
                //新增：订阅项目名称更新事件
                _eventAggregator?.GetEvent<ProjectNameUpdatedEvent>()?.Subscribe(OnProjectNameUpdated);
                Debug.WriteLine("Events 订阅完成");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"订阅事件失败: {ex.Message}");
            }
        }

        private void SelectProcessDesigner()
        {
            try
            {
                Debug.WriteLine("[Command] 开始切换到工艺设计器");

                // 先设置状态，避免多次触发
                SetTabSelection(true, false);

                Debug.WriteLine($"[Command] 切换完成 - Process: {IsProcessDesignerSelected}, Flow: {IsFlowDesignerSelected}");

                _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish("切换到工艺设计器");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"切换到工艺设计器失败: {ex.Message}");
            }
        }

        private void SelectFlowDesigner()
        {
            try
            {
                Debug.WriteLine("[Command] 开始切换到流程设计器");

                // 先设置状态，避免多次触发
                SetTabSelection(false, true);

                Debug.WriteLine($"[Command] 切换完成 - Process: {IsProcessDesignerSelected}, Flow: {IsFlowDesignerSelected}");

                _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish("切换到流程设计器");
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
                {
                    TriggerFlowDesignerUISync();
                }), System.Windows.Threading.DispatcherPriority.Loaded);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"切换到流程设计器失败: {ex.Message}");
            }
        }
        /// <summary>
        /// 触发流程设计器的UI同步
        /// </summary>
        private void TriggerFlowDesignerUISync()
        {
            try
            {
                Debug.WriteLine("触发流程设计器UI同步");

                // 发布事件通知流程设计器同步UI
                _eventAggregator?.GetEvent<FlowDesignerUIRefreshRequestEvent>()?.Publish();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"触发流程设计器UI同步失败: {ex.Message}");
            }
        }
        /// <summary>
        /// 设置标签选择状态，只触发一次左侧面板切换
        /// </summary>
        private void SetTabSelection(bool processSelected, bool flowSelected)
        {
            // 临时标记，避免在属性设置时触发事件
            var oldProcessSelected = _isProcessDesignerSelected;
            var oldFlowSelected = _isFlowDesignerSelected;

            _isProcessDesignerSelected = processSelected;
            _isFlowDesignerSelected = flowSelected;

            // 设置变量管理按钮的可见性
            IsVariableManagementVisible = flowSelected; // 只有在流程设计器选中时才显示

            // 通知属性更改
            if (oldProcessSelected != processSelected)
            {
                RaisePropertyChanged(nameof(IsProcessDesignerSelected));
            }
            if (oldFlowSelected != flowSelected)
            {
                RaisePropertyChanged(nameof(IsFlowDesignerSelected));
            }

            // 只触发一次左侧面板切换
            NotifyLeftPanelChange();
        }

        /// <summary>
        /// 通知主窗口切换左侧面板
        /// </summary>
        private void NotifyLeftPanelChange()
        {
            try
            {
                string targetRegion;

                if (IsProcessDesignerSelected)
                {
                    // 工艺设计器 -> 显示设备库
                    targetRegion = RegionNames.DeviceLibraryRegion;
                    Debug.WriteLine("[LeftPanel] 切换到设备库");
                }
                else if (IsFlowDesignerSelected)
                {
                    // 流程设计器 -> 显示命令库
                    targetRegion = RegionNames.CommadLibraryRegion;
                    Debug.WriteLine("[LeftPanel] 切换到命令库");
                }
                else
                {
                    // 默认显示设备库
                    targetRegion = RegionNames.DeviceLibraryRegion;
                    Debug.WriteLine("[LeftPanel] 默认切换到设备库");
                }

                // 发布左侧面板切换事件
                _eventAggregator?.GetEvent<LeftPanelChangeRequestEvent>()?.Publish(targetRegion);
                Debug.WriteLine($"[LeftPanel] 发布切换事件: {targetRegion}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"通知左侧面板切换失败: {ex.Message}");
            }
        }

        private void OnExport()
        {
            try
            {
                var fileName = _dialogService?.ShowSaveFileDialog("设计文件|*.mcd|所有文件|*.*", ".mcd");
                if (!string.IsNullOrEmpty(fileName))
                {
                    string currentTab = IsProcessDesignerSelected ? "工艺设计" : "流程设计";
                    _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish($"导出{currentTab}到: {fileName}");
                }
            }
            catch (Exception ex)
            {
                _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish($"导出失败: {ex.Message}");
            }
        }

        private void OnImport()
        {
            try
            {
                var fileName = _dialogService?.ShowOpenFileDialog("设计文件|*.mcd|所有文件|*.*");
                if (!string.IsNullOrEmpty(fileName))
                {
                    string currentTab = IsProcessDesignerSelected ? "工艺设计" : "流程设计";
                    _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish($"导入{currentTab}: {fileName}");
                }
            }
            catch (Exception ex)
            {
                _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish($"导入失败: {ex.Message}");
            }
        }

        private void OnAddNew()
        {
            try
            {
                string currentTab = IsProcessDesignerSelected ? "工艺设计" : "流程设计";
                _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish($"创建新{currentTab}");
            }
            catch (Exception ex)
            {
                _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish($"创建新设计失败: {ex.Message}");
            }
        }

        private void OnLanguageChanged(string cultureName)
        {
            InitializeProperties();
        }

        // 新增：处理项目名称更新
        private void OnProjectNameUpdated(string projectName)
        {
            try
            {
                ProjectName = string.IsNullOrWhiteSpace(projectName) ? "未命名项目" : projectName;
                RaisePropertyChanged(nameof(IsProjectNameVisible));
                Debug.WriteLine($"项目名称已更新: {ProjectName}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"更新项目名称失败: {ex.Message}");
            }
        }
    }
}
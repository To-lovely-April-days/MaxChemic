using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MaxChemical.Infrastructure.Events;
using MaxChemical.Infrastructure.Services;
using MaxChemical.Modules.Designer.Models;
using MaxChemical.Modules.Designer.ViewModels;
using Prism.Events;
using Prism.Ioc;

namespace MaxChemical.Modules.Designer.Views
{
    public partial class DeviceCommandPropertiesView : UserControl, INotifyPropertyChanged
    {
        private readonly IEventAggregator _eventAggregator;
        private readonly ILocalizationService _localizationService;

        private string _title;
        private string _bindDevice;
        private string _selectDevice;
        private string _currentDevice;
        private string _inputParam;
        private string _noneParam;
        private string _abstractLabel;


        private ObservableCollection<ParamItem> _paramItems = new ObservableCollection<ParamItem>();

        public event PropertyChangedEventHandler? PropertyChanged;

        public class ParamItem
        {
            public string Name { get; set; }
            public string Value { get; set; }
        }

        public DeviceCommandPropertiesView()
        {
            InitializeComponent();
            // ★ 不再设置 this.DataContext = this
            // 之前这行会覆盖外部传入的 CommandNode,导致 ComboBox 的 BoundDeviceId 绑定失效
            // 本地化标签现在通过 ElementName=Root 在 XAML 里访问

            _eventAggregator = ContainerLocator.Container.Resolve<IEventAggregator>();
            _localizationService = ContainerLocator.Container.Resolve<ILocalizationService>();

            _eventAggregator?.GetEvent<LanguageChangedEvent>()?.Subscribe(LanguageChangedHandler);

            this.Loaded += OnLoaded;
            this.DataContextChanged += OnDataContextChanged;
        }


        public string Title
        {
            get { return _title; }
            set
            {
                _title = value;
                RaisePropertyChanged(nameof(Title));
            }
        }

        public string BindDeviceLabel
        {
            get { return _bindDevice; }
            set
            {
                _bindDevice = value;
                RaisePropertyChanged(nameof(BindDeviceLabel));
            }
        }

        public string InputParamLabel
        {
            get { return _inputParam; }
            set
            {
                _inputParam = value;
                RaisePropertyChanged(nameof(InputParamLabel));
            }
        }

        public string SelectDeviceLabel
        {
            get { return _selectDevice; }
            set
            {
                _selectDevice = value;
                RaisePropertyChanged(nameof(SelectDeviceLabel));
            }
        }

        public string CurrentDeviceLabel
        {
            get { return _currentDevice; }
            set
            {
                _currentDevice = value;
                RaisePropertyChanged(nameof(CurrentDeviceLabel));
            }
        }

        public string NoneParamLabel
        {
            get { return _noneParam; }
            set
            {
                _noneParam = value;
                RaisePropertyChanged(nameof(NoneParamLabel));
            }
        }
        public string AbstractLabel
        {
            get { return _abstractLabel; }
            set
            {
                _abstractLabel = value;
                RaisePropertyChanged(nameof(AbstractLabel));
            }
        }

        private void RaisePropertyChanged([CallerMemberName] string propertyName = "")
        {
            var handler = PropertyChanged;
            handler?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }


        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            TryBuildParams();
            HookDeviceSelectionChange();

            //
            LanguageChangedHandler("");
        }

        private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            TryBuildParams();
            HookDeviceSelectionChange();
        }

        private void HookDeviceSelectionChange()
        {
            if (DataContext is CommandNode node)
            {
                // 当 BoundDeviceId 变化时,更新 BoundDeviceName 与摘要
                node.PropertyChanged -= Node_PropertyChanged;
                node.PropertyChanged += Node_PropertyChanged;
            }
        }

        private void Node_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (sender is CommandNode node && e.PropertyName == nameof(CommandNode.BoundDeviceId))
            {
                // 找到显示名字典(从父VM)
                if (FindFlowVm(out var vm))
                {
                    // 简化:从 vm.DeviceOptionsForSelectedNode 找显示名
                    var disp = vm.DeviceOptionsForSelectedNode?.FirstOrDefault(o => o.Id == node.BoundDeviceId)?.Display;
                    node.BoundDeviceName = string.IsNullOrEmpty(disp) ? node.BoundDeviceId : disp;
                }
            }
        }

        private void TryBuildParams()
        {
            _paramItems.Clear();

            if (DataContext is CommandNode node && node.DeviceCommand?.InputParameters?.Variables != null)
            {
                var defs = node.DeviceCommand.InputParameters.Variables;
                var overrides = node.ParameterOverrides ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                foreach (var def in defs)
                {
                    var name = def.Name;
                    var val = overrides.TryGetValue(name, out var ov)
                        ? ov
                        : ToInvariantString(def.Value);

                    _paramItems.Add(new ParamItem { Name = name, Value = val });
                }

                ParamsItems.ItemsSource = _paramItems;

                // 即时提交:监听每个 ParamItem 的变化
                foreach (var p in _paramItems)
                {
                    // 使用简单事件:TextBox TwoWay 已经绑定到 ParamItem.Value,此处统一提交一次
                }

                // 订阅集合项变化,通过 TextBox 的 UpdateSourceTrigger=PropertyChanged,下面统一提交
                this.ParamsItems.AddHandler(TextBox.TextChangedEvent, new TextChangedEventHandler((s, e) =>
                {
                    ApplyParamsToNode();
                }));
            }
            else
            {
                ParamsItems.ItemsSource = null;
            }
        }

        private void ApplyParamsToNode()
        {
            if (DataContext is not CommandNode node) return;

            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in _paramItems)
                map[item.Name] = item.Value;

            node.ApplyParameterOverrides(map);
        }

        private static string ToInvariantString(object value)
        {
            if (value == null) return string.Empty;
            return value is IFormattable f
                ? f.ToString(null, CultureInfo.InvariantCulture)
                : value.ToString();
        }

        private bool FindFlowVm(out FlowDesignerViewModel vm)
        {
            vm = null;
            System.Windows.DependencyObject parent = this; // 关键:用 DependencyObject
            while (parent != null)
            {
                if (parent is FlowDesignerView view && view.DataContext is FlowDesignerViewModel m)
                {
                    vm = m;
                    return true;
                }
                parent = System.Windows.Media.VisualTreeHelper.GetParent(parent);
            }
            return false;
        }


        private void LanguageChangedHandler(string s)
        {
            if (_localizationService != null)
            {
                Title = _localizationService.GetString("DeviceCommandProperty_Title");
                CurrentDeviceLabel = _localizationService.GetString("DeviceCommandProperty_CurrentDevice");
                SelectDeviceLabel = _localizationService.GetString("DeviceCommandProperty_SelectDevice");
                BindDeviceLabel = _localizationService.GetString("DeviceCommandProperty_BindDevice");
                InputParamLabel = _localizationService.GetString("DeviceCommandProperty_InputParam");
                NoneParamLabel = _localizationService.GetString("DeviceCommandProperty_NoneParam");
                AbstractLabel = _localizationService.GetString("DeviceCommandProperty_Abstract");
            }
        }
    }
}
using MaxChemical.Infrastructure.Events;
using MaxChemical.Infrastructure.Services;
using MaxChemical.Modules.Designer.Models;
using MaxChemical.Modules.Designer.ViewModels;
using Prism.Events;
using Prism.Ioc;
using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace MaxChemical.Modules.Designer.Views
{
    public partial class ParallelCommandPropertiesView : UserControl
    {
        private CancellationTokenSource _debounceCts;

        public ParallelCommandPropertiesView()
        {
            InitializeComponent();

            //本地化
            IEventAggregator eventAggregator = ContainerLocator.Container.Resolve<IEventAggregator>();
            eventAggregator?.GetEvent<LanguageChangedEvent>()?.Subscribe(OnLanguageChanged);
            OnLanguageChanged("");
        }

        /// <summary>
        /// 本地化
        /// </summary>
        /// <param name="s"></param>
        private void OnLanguageChanged(string s)
        {
            ILocalizationService service = ContainerLocator.Container.Resolve<ILocalizationService>();
            if (service is null)
            {
                return;
            }

            NodeTitle.Text = service.GetString("Parallel_Title");
            StrategyTitle.Text = service.GetString("Parallel_Strategy_Title");
            RbNoWait.Content = service.GetString("Parallel_Strategy_Nowait");
            RbAll.Content = service.GetString("Parallel_Strategy_All");
            RbAny.Content = service.GetString("Parallel_Strategy_Any");
            ParallelParamTitle.Text = service.GetString("Parallel_Parameter_Title");
            ParallelParamLabel.Text = service.GetString("Parallel_Parameter_Maxcount");
            TimeoutLabel.Text = service.GetString("Parallel_Parameter_Timeout");
            NumberUnitLabel.Text = service.GetString("Parallel_Parameter_Countunit");
            TimeUnitLabel.Text = service.GetString("Parallel_Paremeter_Timeunit");
            DescLabel.Text = service.GetString("Parallel_Desc_Title");
            InstructionLabel.Text = service.GetString("Parallel_Instruction_Title");
            Desc1.Text = service.GetString("Parallel_Instruction_Desc1");
            Desc2.Text = service.GetString("Parallel_Instruction_Desc2");
            Desc3.Text = service.GetString("Parallel_Instruction_Desc3");
        }

        private async void OnAnyValueChanged(object sender, RoutedEventArgs e)
        {
            // 值已通过 TwoWay+PropertyChanged 写入 Dictionary，接下来归一化+刷新
            _debounceCts?.Cancel();
            _debounceCts = new CancellationTokenSource();
            var token = _debounceCts.Token;

            try
            {
                await Task.Delay(150, token);
                if (token.IsCancellationRequested) return;

                ApplyNow();
            }
            catch (TaskCanceledException)
            {
                // 忽略取消
            }
        }

        private void ApplyNow()
        {
            if (DataContext is not CommandNode node) return;

            // 归一化数值，避免字符串类型导致后续解析失败
            TryNormalizeNumeric(node, "MaxParallelCount", 4, isInt: true);
            TryNormalizeNumeric(node, "TimeoutSeconds", 60.0, isInt: false);

            // 刷新并行控件（卡片摘要/分支布局）
            if (node.NodeControl is ParallelNodeControlView p)
            {
                p.RefreshForPropertyChange();
            }

            // 刷新全画布布局（连接线/画布尺寸）
            var vm = FindFlowVm();
            vm?.RefreshNodeLayout(node);
        }

        private void TryNormalizeNumeric(CommandNode node, string key, object defaultValue, bool isInt)
        {
            if (node.LogicCommand?.Properties == null) return;

            try
            {
                var obj = node.LogicCommand.Properties.ContainsKey(key) ? node.LogicCommand.Properties[key] : defaultValue;

                if (isInt)
                {
                    if (obj is int) return;
                    if (int.TryParse(Convert.ToString(obj, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out var iv))
                        node.LogicCommand.Properties[key] = iv;
                    else
                        node.LogicCommand.Properties[key] = Convert.ToInt32(defaultValue);
                }
                else
                {
                    if (obj is double) return;
                    if (double.TryParse(Convert.ToString(obj, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out var dv))
                        node.LogicCommand.Properties[key] = dv;
                    else
                        node.LogicCommand.Properties[key] = Convert.ToDouble(defaultValue);
                }
            }
            catch
            {
                node.LogicCommand.Properties[key] = defaultValue;
            }
        }

        private FlowDesignerViewModel FindFlowVm()
        {
            System.Windows.DependencyObject p = this;
            while (p != null)
            {
                if (p is FlowDesignerView view && view.DataContext is FlowDesignerViewModel vm)
                    return vm;
                p = System.Windows.Media.VisualTreeHelper.GetParent(p);
            }
            return null;
        }

        // 只允许整数
        private void NumberOnly(object sender, TextCompositionEventArgs e)
        {
            e.Handled = !int.TryParse(e.Text, out _);
        }

        // 允许小数
        private void NumberDotOnly(object sender, TextCompositionEventArgs e)
        {
            if (e.Text == ".")
            {
                e.Handled = ((sender as TextBox)?.Text?.Contains(".") ?? false);
            }
            else
            {
                e.Handled = !double.TryParse(e.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out _);
            }
        }
    }
}
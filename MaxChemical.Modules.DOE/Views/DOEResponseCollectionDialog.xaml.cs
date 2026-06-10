using MaxChemical.Infrastructure.Services;
using Prism.Ioc;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Input;

namespace MaxChemical.Modules.DOE.Views
{
    public partial class DOEResponseCollectionDialog : Window
    {
        private readonly List<ResponseInputItem> _responseItems;

        /// <summary>
        /// 用户提交的响应值。null 表示用户跳过。
        /// </summary>
        public Dictionary<string, double>? CollectedValues { get; private set; }

        /// <summary>
        /// 创建响应值采集弹框
        /// </summary>
        /// <param name="runIndex">当前组号</param>
        /// <param name="totalRuns">总组数</param>
        /// <param name="factorValues">本组因子值</param>
        /// <param name="responseDefinitions">响应变量定义列表 (名称, 单位)</param>
        public DOEResponseCollectionDialog(
            int runIndex,
            int totalRuns,
            Dictionary<string, object> factorValues,
            List<(string Name, string Unit)> responseDefinitions)
        {
            InitializeComponent();

            //
            ILocalizationService service = ContainerLocator.Container.Resolve<ILocalizationService>();
            TbTitle.Text = service.GetString("Doe_Response_Title", "实验结果录入");
            TbParam.Text = service.GetString("Doe_Response_CurrentParam", "本组实验参数");
            TbInputResult.Text = service.GetString("Doe_Response_InputResult", "请输入实验结果");
            BtnSkip.Content = service.GetString("Doe_Response_Skip", "跳过本组");
            BtnSubmit.Content = service.GetString("Doe_Response_Commit", "提交结果");


            // 进度徽章
            ProgressBadge.Text = $"{runIndex + 1} / {totalRuns}";

            // 副标题
            SubtitleText.Text = string.Format(service.GetString("Doe_Response_SubTitle", "第 {0} 组实验已完成，请录入表征结果"), runIndex + 1);

            // ★ 因子值显示：类别因子显示标签，连续因子显示数值
            FactorDisplay.ItemsSource = factorValues.Select(kv =>
            {
                string display;
                if (kv.Value is double d)
                    display = d.ToString("F2");
                else if (double.TryParse(kv.Value?.ToString(), out var parsed))
                    display = parsed.ToString("F2");
                else
                    display = kv.Value?.ToString() ?? "—";
                return new KeyValuePair<string, string>(kv.Key, display);
            }).ToList();

            // 创建响应值输入项
            _responseItems = responseDefinitions.Select(r => new ResponseInputItem
            {
                Name = r.Name,
                Unit = r.Unit,
                Value = ""
            }).ToList();

            ResponseInputs.ItemsSource = _responseItems;
        }

        /// <summary>窗口拖拽支持（无标题栏模式）</summary>
        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
                DragMove();
        }

        private void Submit_Click(object sender, RoutedEventArgs e)
        {
            var values = new Dictionary<string, double>();
            var errors = new List<string>();

            foreach (var item in _responseItems)
            {
                if (string.IsNullOrWhiteSpace(item.Value))
                {
                    errors.Add($"'{item.Name}' 不能为空");
                    continue;
                }

                if (double.TryParse(item.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var val))
                {
                    values[item.Name] = val;
                }
                else
                {
                    errors.Add($"'{item.Name}' 的值 '{item.Value}' 不是有效数字");
                }
            }

            if (errors.Count > 0)
            {
                MessageBox.Show(string.Join("\n", errors), "输入验证",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            CollectedValues = values;
            DialogResult = true;
            Close();
        }

        private void Skip_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show(
                "跳过本组将不会记录响应值，确定跳过？", "确认",
                MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                CollectedValues = null;
                DialogResult = false;
                Close();
            }
        }
    }

    /// <summary>
    /// 响应值输入项
    /// </summary>
    public class ResponseInputItem : INotifyPropertyChanged
    {
        private string _value = "";

        public string Name { get; set; } = "";
        public string Unit { get; set; } = "";

        /// <summary>是否有单位（控制单位后缀显示）</summary>
        public bool HasUnit => !string.IsNullOrWhiteSpace(Unit);

        public string Value
        {
            get => _value;
            set
            {
                _value = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
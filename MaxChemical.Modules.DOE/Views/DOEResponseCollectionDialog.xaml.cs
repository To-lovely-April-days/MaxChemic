using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows;

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
            Dictionary<string, double> factorValues,
            List<(string Name, string Unit)> responseDefinitions)
        {
            InitializeComponent();

            SubtitleText.Text = $"第 {runIndex + 1}/{totalRuns} 组实验已完成，请录入表征/分析结果";

            // 显示当前因子值
            FactorDisplay.ItemsSource = factorValues.Select(kv => new KeyValuePair<string, double>(kv.Key, kv.Value)).ToList();

            // 创建响应值输入项
            _responseItems = responseDefinitions.Select(r => new ResponseInputItem
            {
                Name = r.Name,
                Unit = r.Unit,
                Value = ""
            }).ToList();

            ResponseInputs.ItemsSource = _responseItems;
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
                MessageBox.Show(string.Join("\n", errors), "输入验证", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            CollectedValues = values;
            DialogResult = true;
            Close();
        }

        private void Skip_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show("跳过本组将不会记录响应值，确定跳过？", "确认",
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

        public string Value
        {
            get => _value;
            set { _value = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value))); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}

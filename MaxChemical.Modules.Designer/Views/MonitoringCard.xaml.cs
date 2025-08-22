using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DevicePlugins.Devices;

namespace MaxChemical.Modules.Designer.Views
{
    public partial class MonitoringCard : UserControl, INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        public event EventHandler CloseRequested;

        private ObservableCollection<MonitorParameter> _parameters;
        private string _deviceId;
        private string _deviceName;
        private DateTime _lastUpdateTime;

        public ObservableCollection<MonitorParameter> Parameters
        {
            get => _parameters;
            set
            {
                _parameters = value;
                OnPropertyChanged();
                ParametersItemsControl.ItemsSource = _parameters;
            }
        }

        public string DeviceId
        {
            get => _deviceId;
            set
            {
                _deviceId = value;
                OnPropertyChanged();
            }
        }

        public string DeviceName
        {
            get => _deviceName;
            set
            {
                _deviceName = value;
                OnPropertyChanged();
                TitleText.Text = $"{value} - 监控";
            }
        }

        public MonitoringCard()
        {
            InitializeComponent();
            Parameters = new ObservableCollection<MonitorParameter>();
            UpdateStatus("等待数据", false);
        }

        /// <summary>
        /// 设置监控参数
        /// </summary>
        public void SetMonitoringParameters(string deviceId, string deviceName, List<ParameterBase> parameters)
        {
            DeviceId = deviceId;
            DeviceName = deviceName;

            Parameters.Clear();

            foreach (var param in parameters)
            {
                Parameters.Add(new MonitorParameter
                {
                    Name = param.Name,
                    Value = param.Value?.ToString() ?? "--",
                    OriginalParameter = param
                });
            }

            UpdateStatus($"监控 {parameters.Count} 个参数", true);
        }

        /// <summary>
        /// 更新参数值
        /// </summary>
        public void UpdateParameterValues(DeviceParameters outputParameters)
        {
            if (outputParameters?.Variables == null) return;

            bool hasUpdates = false;

            foreach (var param in Parameters)
            {
                var updatedParam = outputParameters.Variables
                    .FirstOrDefault(v => v.Name == param.Name);

                if (updatedParam != null)
                {
                    var newValue = updatedParam.Value?.ToString() ?? "--";
                    if (param.Value != newValue)
                    {
                        param.Value = newValue;
                        param.OriginalParameter = updatedParam;
                        hasUpdates = true;
                    }
                }
            }

            if (hasUpdates)
            {
                _lastUpdateTime = DateTime.Now;
                UpdateStatus($"已更新 - {_lastUpdateTime:HH:mm:ss}", true);

                // 闪烁效果表示数据更新
                FlashUpdateIndicator();
            }
        }

        /// <summary>
        /// 更新状态显示
        /// </summary>
        private void UpdateStatus(string statusText, bool isActive)
        {
            StatusText.Text = statusText;
            StatusIndicator.Fill = new SolidColorBrush(isActive ? Colors.LimeGreen : Colors.Gray);

            if (isActive && _lastUpdateTime != default)
            {
                UpdateTimeText.Text = _lastUpdateTime.ToString("HH:mm:ss");
            }
        }

        /// <summary>
        /// 闪烁更新指示器
        /// </summary>
        private void FlashUpdateIndicator()
        {
            var originalBrush = StatusIndicator.Fill;
            StatusIndicator.Fill = new SolidColorBrush(Colors.Orange);

            var timer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(300)
            };

            timer.Tick += (s, e) =>
            {
                StatusIndicator.Fill = originalBrush;
                timer.Stop();
            };

            timer.Start();
        }

        /// <summary>
        /// 检查参数是否为空
        /// </summary>
        public bool HasParameters()
        {
            return Parameters?.Count > 0;
        }

        /// <summary>
        /// 获取监控的参数名称列表
        /// </summary>
        public List<string> GetMonitoredParameterNames()
        {
            return Parameters?.Select(p => p.Name).ToList() ?? new List<string>();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }

        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    /// <summary>
    /// 监控参数数据模型
    /// </summary>
    public class MonitorParameter : INotifyPropertyChanged
    {
        private string _name;
        private string _value;
        private ParameterBase _originalParameter;

        public string Name
        {
            get => _name;
            set
            {
                _name = value;
                OnPropertyChanged();
            }
        }

        public string Value
        {
            get => _value;
            set
            {
                _value = value;
                OnPropertyChanged();
            }
        }

        public ParameterBase OriginalParameter
        {
            get => _originalParameter;
            set
            {
                _originalParameter = value;
                OnPropertyChanged();
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
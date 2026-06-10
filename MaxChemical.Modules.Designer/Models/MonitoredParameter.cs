using System;
using System.Collections.Generic;
using System.ComponentModel;

namespace MaxChemical.Modules.Designer.Models
{
    /// <summary>
    /// 监控参数模型
    /// </summary>
    public class MonitoredParameter : INotifyPropertyChanged
    {
        private bool _isEnabled = true;
        private string _color;
        private double _minValue = double.MaxValue; // 修正：初始化为最大值，便于比较
        private double _maxValue = double.MinValue; // 修正：初始化为最小值，便于比较
        private double? _currentValue;
        private DateTime? _lastUpdateTime;
        public string Unit { get; set; } = "";
        public string Key { get; set; }
        public string DeviceId { get; set; }
        public string DeviceName { get; set; }
        public string CommandName { get; set; }
        public string ParameterName { get; set; }
        public string DisplayName { get; set; }

        public bool IsEnabled
        {
            get => _isEnabled;
            set
            {
                if (_isEnabled != value)
                {
                    _isEnabled = value;
                    OnPropertyChanged(nameof(IsEnabled));
                }
            }
        }

        public string Color
        {
            get => _color;
            set
            {
                if (_color != value)
                {
                    _color = value;
                    OnPropertyChanged(nameof(Color));
                }
            }
        }

        public double MinValue
        {
            get => _minValue;
            set
            {
                if (Math.Abs(_minValue - value) > double.Epsilon)
                {
                    _minValue = value;
                    OnPropertyChanged(nameof(MinValue));
                    OnPropertyChanged(nameof(HasValidRange));
                }
            }
        }

        public double MaxValue
        {
            get => _maxValue;
            set
            {
                if (Math.Abs(_maxValue - value) > double.Epsilon)
                {
                    _maxValue = value;
                    OnPropertyChanged(nameof(MaxValue));
                    OnPropertyChanged(nameof(HasValidRange));
                }
            }
        }

        public double? CurrentValue
        {
            get => _currentValue;
            set
            {
                if (_currentValue != value)
                {
                    _currentValue = value;
                    OnPropertyChanged(nameof(CurrentValue));
                    OnPropertyChanged(nameof(FormattedCurrentValue));
                }
            }
        }

        public DateTime? LastUpdateTime
        {
            get => _lastUpdateTime;
            set
            {
                if (_lastUpdateTime != value)
                {
                    _lastUpdateTime = value;
                    OnPropertyChanged(nameof(LastUpdateTime));
                    OnPropertyChanged(nameof(FormattedLastUpdateTime));
                }
            }
        }

        /// <summary>
        /// 是否有有效的数值范围
        /// </summary>
        public bool HasValidRange => _minValue != double.MaxValue && _maxValue != double.MinValue;

        /// <summary>
        /// 格式化的当前值
        /// </summary>
        public string FormattedCurrentValue => _currentValue?.ToString("F2") ?? "--";

        /// <summary>
        /// 格式化的最后更新时间
        /// </summary>
        public string FormattedLastUpdateTime => _lastUpdateTime?.ToString("HH:mm:ss") ?? "--";

        /// <summary>
        /// 格式化的数值范围
        /// </summary>
        public string FormattedRange
        {
            get
            {
                if (!HasValidRange) return "--";
                return $"{_minValue:F2} ~ {_maxValue:F2}";
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        /// <summary>
        /// 生成随机颜色
        /// </summary>
        public static string GenerateRandomColor()
        {
            var colors = new[]
            {
                "#1f77b4", "#ff7f0e", "#2ca02c", "#d62728", "#9467bd",
                "#8c564b", "#e377c2", "#7f7f7f", "#bcbd22", "#17becf",
                "#FF6B6B", "#4ECDC4", "#45B7D1", "#96CEB4", "#FFEAA7",
                "#DDA0DD", "#98D8C8", "#F7DC6F", "#BB8FCE", "#85C1E9"
            };

            var random = new Random();
            return colors[random.Next(colors.Length)];
        }

        /// <summary>
        /// 重置统计信息
        /// </summary>
        public void ResetStatistics()
        {
            MinValue = double.MaxValue;
            MaxValue = double.MinValue;
            CurrentValue = null;
            LastUpdateTime = null;
        }

        /// <summary>
        /// 更新数值并自动计算范围
        /// </summary>
        public void UpdateValue(double value, DateTime? timestamp = null)
        {
            CurrentValue = value;
            LastUpdateTime = timestamp ?? DateTime.Now;

            if (value < MinValue)
                MinValue = value;

            if (value > MaxValue)
                MaxValue = value;
        }

        public override string ToString()
        {
            return $"{DisplayName} ({(IsEnabled ? "启用" : "禁用")})";
        }
        public double MinRange { get; set; }
        public double MaxRange { get; set; }
    }

    /// <summary>
    /// 参数数据点模型
    /// </summary>
    public class ParameterDataPoint
    {
        public DateTime Timestamp { get; set; }
        public double Value { get; set; }
        public string ParameterKey { get; set; }

        /// <summary>
        /// 格式化的时间
        /// </summary>
        public string FormattedTime => Timestamp.ToString("HH:mm:ss.fff");

        /// <summary>
        /// 格式化的数值
        /// </summary>
        public string FormattedValue => Value.ToString("F3");

        /// <summary>
        /// 相对于基准时间的秒数（用于图表X轴）
        /// </summary>
        public double GetRelativeSeconds(DateTime baseTime)
        {
            return (Timestamp - baseTime).TotalSeconds;
        }

        public override string ToString()
        {
            return $"{FormattedTime}: {FormattedValue}";
        }
    }

    /// <summary>
    /// 可用参数模型
    /// </summary>
    public class AvailableParameter
    {
        public string DeviceId { get; set; }
        public string DeviceName { get; set; }
        public string CommandName { get; set; }
        public string ParameterName { get; set; }
        public string ParameterType { get; set; }
        public string DisplayName { get; set; }
        public bool IsNumeric { get; set; }
        public string Unit { get; set; } = "";

        // ════════════════════════════════════════════════════════════
        //  ★ 监控可用性元数据(由 Service 层填充)
        // ════════════════════════════════════════════════════════════

        /// <summary>
        /// 参数所属图表轴类型(温度/压力/流量/未分类)
        /// </summary>
        public AxisType AxisType { get; set; } = AxisType.Unclassified;

        /// <summary>
        /// 是否可选(轴类型为温度/压力/流量时为 true)。
        /// false 时面板上变灰,不能选。
        /// </summary>
        public bool IsSelectable { get; set; } = true;

        /// <summary>
        /// 不可选时的原因提示(用于 Tooltip)。
        /// 可选时为 null。
        /// </summary>
        public string DisableReason { get; set; }

        /// <summary>
        /// 参数的唯一键
        /// </summary>
        public string Key => $"{DeviceId}|{CommandName}|{ParameterName}";

        /// <summary>
        /// 简化的显示名称（不包含设备名）
        /// </summary>
        public string ShortDisplayName => $"{CommandName}.{ParameterName}";

        /// <summary>
        /// 参数描述
        /// </summary>
        public string Description => $"设备: {DeviceName}\n命令: {CommandName}\n参数: {ParameterName}\n类型: {ParameterType}";

        public override string ToString()
        {
            return DisplayName;
        }
    }

    /// <summary>
    /// 参数数据更新事件参数
    /// </summary>
    public class ParameterDataUpdatedEventArgs : EventArgs
    {
        public string DeviceId { get; set; }
        public string CommandName { get; set; }
        public DateTime UpdateTime { get; set; }
        public string ParameterKey { get; set; }
        public ParameterDataPoint NewDataPoint { get; set; }
        public List<ParameterDataPoint> AllData { get; set; }

        /// <summary>
        /// 受影响的参数数量
        /// </summary>
        public int AffectedParametersCount { get; set; }

        public override string ToString()
        {
            return $"设备 {DeviceId} 的 {CommandName} 命令在 {UpdateTime:HH:mm:ss} 更新了数据";
        }
    }

    /// <summary>
    /// 图表数据点模型
    /// </summary>
    public class ChartDataPoint
    {
        public DateTime Timestamp { get; set; }
        public Dictionary<string, double> ParameterValues { get; set; } = new Dictionary<string, double>();

        /// <summary>
        /// 格式化的时间
        /// </summary>
        public string FormattedTime => Timestamp.ToString("HH:mm:ss");

        /// <summary>
        /// 详细格式化的时间（包含毫秒）
        /// </summary>
        public string DetailedFormattedTime => Timestamp.ToString("HH:mm:ss.fff");

        /// <summary>
        /// 相对于基准时间的秒数（用于图表X轴）
        /// </summary>
        public double GetRelativeSeconds(DateTime baseTime)
        {
            return (Timestamp - baseTime).TotalSeconds;
        }

        /// <summary>
        /// 获取指定参数的值
        /// </summary>
        public double GetParameterValue(string parameterName, double defaultValue = 0.0)
        {
            return ParameterValues.TryGetValue(parameterName, out var value) ? value : defaultValue;
        }

        /// <summary>
        /// 设置参数值
        /// </summary>
        public void SetParameterValue(string parameterName, double value)
        {
            ParameterValues[parameterName] = value;
        }

        /// <summary>
        /// 检查是否包含指定参数
        /// </summary>
        public bool HasParameter(string parameterName)
        {
            return ParameterValues.ContainsKey(parameterName);
        }

        /// <summary>
        /// 获取所有参数名称
        /// </summary>
        public IEnumerable<string> GetParameterNames()
        {
            return ParameterValues.Keys;
        }

        /// <summary>
        /// 参数数量
        /// </summary>
        public int ParameterCount => ParameterValues.Count;

        /// <summary>
        /// 是否为空数据点
        /// </summary>
        public bool IsEmpty => ParameterValues.Count == 0;

        public override string ToString()
        {
            return $"{FormattedTime} ({ParameterCount} 个参数)";
        }
    }

    /// <summary>
    /// 监控参数变化事件参数（用于设备属性对话框）
    /// </summary>
    public class MonitoringParametersChangedEventArgs : EventArgs
    {
        public string DeviceId { get; set; }
        public string DeviceName { get; set; }
        public List<object> SelectedParameters { get; set; } = new List<object>();
        public DateTime ChangeTime { get; set; } = DateTime.Now;

        public override string ToString()
        {
            return $"设备 {DeviceName} 的监控参数发生变化: {SelectedParameters?.Count ?? 0} 个参数";
        }
    }
}
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System;
using System.Text.RegularExpressions;

namespace DevicePlugins.Devices
{
    /// <summary>
    /// 参数基类
    /// </summary>
    public abstract class ParameterBase : INotifyPropertyChanged, ICloneable
    {
        private string _name = string.Empty;
        private object? _value;
        private string _helpText = string.Empty;
        private bool _isRequired;
        private bool _isReadOnly;
        private bool _isVisibleInVariableManager = false;
        private string _unit = string.Empty;   // ★ 新增

        /// <summary>
        /// 参数名称
        /// </summary>
        public string Name
        {
            get => _name;
            set
            {
                _name = value;
                OnPropertyChanged();
            }
        }

        /// <summary>
        /// 参数值
        /// </summary>
        public object? Value
        {
            get => _value;
            set
            {
                if (!Equals(_value, value))
                {
                    _value = value;
                    OnPropertyChanged();
                    OnValueChanged();
                }
            }
        }

        /// <summary>
        /// 帮助文本(在很多地方也用作参数描述)
        /// </summary>
        public string HelpText
        {
            get => _helpText;
            set
            {
                _helpText = value;
                OnPropertyChanged();
            }
        }

        /// <summary>
        /// ★ 单位(温度=℃, 压力=MPa/bar, 流量=L/min/sccm 等)
        /// 用于参数监控图表自动识别 Y 轴归属。
        /// 大小写不敏感,可留空。如果留空,系统会尝试从 HelpText 末尾的括号提取(例如 "压力(bar)" → "bar")。
        /// </summary>
        public string Unit
        {
            get => _unit;
            set
            {
                _unit = value ?? string.Empty;
                OnPropertyChanged();
            }
        }

        /// <summary>
        /// 是否必需参数
        /// </summary>
        public bool IsRequired
        {
            get => _isRequired;
            set
            {
                _isRequired = value;
                OnPropertyChanged();
            }
        }

        /// <summary>
        /// 是否只读
        /// </summary>
        public bool IsReadOnly
        {
            get => _isReadOnly;
            set
            {
                _isReadOnly = value;
                OnPropertyChanged();
            }
        }

        /// <summary>
        /// 是否在变量管理器中可见(默认false)
        /// </summary>
        public bool IsVisibleInVariableManager
        {
            get => _isVisibleInVariableManager;
            set
            {
                _isVisibleInVariableManager = value;
                OnPropertyChanged();
            }
        }

        /// <summary>
        /// 属性变更事件
        /// </summary>
        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>
        /// 值变更事件
        /// </summary>
        public event EventHandler? ValueChanged;

        protected void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        protected virtual void OnValueChanged()
        {
            ValueChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// 验证参数值
        /// </summary>
        public virtual bool Validate()
        {
            if (IsRequired && Value == null)
                return false;
            return ValidateValue();
        }

        /// <summary>
        /// 验证具体值(子类实现)
        /// </summary>
        protected virtual bool ValidateValue()
        {
            return true;
        }

        /// <summary>
        /// 克隆参数(深拷贝)
        /// </summary>
        public abstract object Clone();

        /// <summary>
        /// 获取参数值(类型安全的辅助方法)
        /// </summary>
        public T GetValue<T>()
        {
            if (Value is T typedValue)
                return typedValue;
            if (Value == null)
                return default(T);
            try
            {
                return (T)Convert.ChangeType(Value, typeof(T));
            }
            catch
            {
                return default(T);
            }
        }

        /// <summary>
        /// 获取参数值(非泛型版本)
        /// </summary>
        public object GetValue()
        {
            return Value;
        }

        // ★ 新增:从 helpText 末尾的括号自动抽取单位
        // 例如 "压力(bar)" → "bar", "温度(℃)" → "℃", "流量(mL/min)" → "mL/min"
        // 支持中文括号 () 和英文括号 ()
        private static readonly Regex UnitFromHelpTextRegex =
            new Regex(@"[\(\(]([^\)\)]+)[\)\)]\s*$", RegexOptions.Compiled);

        /// <summary>
        /// 工具方法:从 helpText 末尾的括号自动抽取单位。
        /// 子类构造函数中,如果 unit 没有显式指定,可调用此方法做兜底。
        /// </summary>
        protected static string TryExtractUnitFromHelpText(string helpText)
        {
            if (string.IsNullOrEmpty(helpText)) return "";
            var m = UnitFromHelpTextRegex.Match(helpText);
            return m.Success ? m.Groups[1].Value.Trim() : "";
        }
    }

    /// <summary>
    /// 字符串参数
    /// </summary>
    public class StringParameter : ParameterBase
    {
        private ObservableCollection<string> _options = new ObservableCollection<string>();
        private int _maxLength = int.MaxValue;

        public ObservableCollection<string> Options
        {
            get => _options;
            set
            {
                _options = value;
                OnPropertyChanged();
            }
        }

        public int MaxLength
        {
            get => _maxLength;
            set
            {
                _maxLength = value;
                OnPropertyChanged();
            }
        }

        public StringParameter(string name, string value = "", string helpText = "",
            bool isVisibleInVariableManager = false, string unit = null)
        {
            Name = name;
            Value = value;
            HelpText = helpText;
            IsVisibleInVariableManager = isVisibleInVariableManager;

            // ★ unit 优先用显式传入的;若为 null 则从 helpText 抽取
            Unit = unit ?? TryExtractUnitFromHelpText(helpText);
        }

        protected override bool ValidateValue()
        {
            var stringValue = Value?.ToString() ?? string.Empty;
            if (stringValue.Length > MaxLength) return false;
            if (Options.Count > 0 && !Options.Contains(stringValue)) return false;
            return true;
        }

        public override object Clone()
        {
            var cloned = new StringParameter(Name, Value?.ToString() ?? "", HelpText,
                IsVisibleInVariableManager, Unit)
            {
                IsRequired = IsRequired,
                IsReadOnly = IsReadOnly,
                MaxLength = MaxLength,
                Options = new ObservableCollection<string>(Options)
            };
            return cloned;
        }
    }

    /// <summary>
    /// 数值参数
    /// </summary>
    public class NumberParameter : ParameterBase
    {
        private double _minValue = double.MinValue;
        private double _maxValue = double.MaxValue;
        private int _decimalPlaces = 2;

        public double MinValue
        {
            get => _minValue;
            set
            {
                _minValue = value;
                OnPropertyChanged();
            }
        }

        public double MaxValue
        {
            get => _maxValue;
            set
            {
                _maxValue = value;
                OnPropertyChanged();
            }
        }

        public int DecimalPlaces
        {
            get => _decimalPlaces;
            set
            {
                _decimalPlaces = Math.Max(0, value);
                OnPropertyChanged();
            }
        }

        public NumberParameter(string name, double minValue = double.MinValue, double maxValue = double.MaxValue,
            double value = 0, string helpText = "", bool isVisibleInVariableManager = false,
            string unit = null)
        {
            Name = name;
            MinValue = minValue;
            MaxValue = maxValue;
            Value = value;
            HelpText = helpText;
            IsVisibleInVariableManager = isVisibleInVariableManager;

            // ★ unit 优先用显式传入的;若为 null 则从 helpText 抽取
            // 例如 helpText="压力(bar)",自动得到 Unit="bar"
            Unit = unit ?? TryExtractUnitFromHelpText(helpText);
        }

        protected override bool ValidateValue()
        {
            if (Value is not double doubleValue) return false;
            return doubleValue >= MinValue && doubleValue <= MaxValue;
        }

        public override object Clone()
        {
            var cloned = new NumberParameter(Name, MinValue, MaxValue,
                Value is double d ? d : 0, HelpText, IsVisibleInVariableManager, Unit)
            {
                IsRequired = IsRequired,
                IsReadOnly = IsReadOnly,
                DecimalPlaces = DecimalPlaces
            };
            return cloned;
        }
    }

    /// <summary>
    /// 布尔参数
    /// </summary>
    public class BooleanParameter : ParameterBase
    {
        public BooleanParameter(string name, bool value = false, string helpText = "",
            bool isVisibleInVariableManager = false, string unit = null)
        {
            Name = name;
            Value = value;
            HelpText = helpText;
            IsVisibleInVariableManager = isVisibleInVariableManager;
            Unit = unit ?? TryExtractUnitFromHelpText(helpText);
        }

        protected override bool ValidateValue()
        {
            return Value is bool;
        }

        public override object Clone()
        {
            var cloned = new BooleanParameter(Name,
                Value is bool b ? b : false, HelpText, IsVisibleInVariableManager, Unit)
            {
                IsRequired = IsRequired,
                IsReadOnly = IsReadOnly
            };
            return cloned;
        }
    }

    /// <summary>
    /// 枚举参数
    /// </summary>
    public class EnumParameter : ParameterBase
    {
        private Type _enumType;

        public Type EnumType
        {
            get => _enumType;
            set
            {
                if (value != null && value.IsEnum)
                {
                    _enumType = value;
                    OnPropertyChanged();
                }
            }
        }

        public EnumParameter(string name, Type enumType, object? value = null, string helpText = "",
            bool isVisibleInVariableManager = false, string unit = null)
        {
            if (!enumType.IsEnum)
                throw new ArgumentException("Type must be an enum", nameof(enumType));

            Name = name;
            EnumType = enumType;
            Value = value ?? Enum.GetValues(enumType).GetValue(0);
            HelpText = helpText;
            IsVisibleInVariableManager = isVisibleInVariableManager;
            Unit = unit ?? TryExtractUnitFromHelpText(helpText);
        }

        protected override bool ValidateValue()
        {
            if (Value == null) return false;
            return Enum.IsDefined(EnumType, Value);
        }

        public override object Clone()
        {
            var cloned = new EnumParameter(Name, EnumType, Value, HelpText, IsVisibleInVariableManager, Unit)
            {
                IsRequired = IsRequired,
                IsReadOnly = IsReadOnly
            };
            return cloned;
        }
    }
}
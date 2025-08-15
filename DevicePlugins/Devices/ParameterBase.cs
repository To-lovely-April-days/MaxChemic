using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System;

namespace DevicePlugins.Devices
{
    /// <summary>
    /// 参数基类
    /// </summary>
    public abstract class ParameterBase : INotifyPropertyChanged
    {
        private string _name = string.Empty;
        private object? _value;
        private string _helpText = string.Empty;
        private bool _isRequired;
        private bool _isReadOnly;
        private bool _isVisibleInVariableManager = false; // 新增属性，默认false

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
        /// 帮助文本
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
        /// 是否在变量管理器中可见（默认false）
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
        /// <returns>验证结果</returns>
        public virtual bool Validate()
        {
            if (IsRequired && Value == null)
                return false;

            return ValidateValue();
        }

        /// <summary>
        /// 验证具体值(子类实现)
        /// </summary>
        /// <returns>验证结果</returns>
        protected virtual bool ValidateValue()
        {
            return true;
        }
    }

    /// <summary>
    /// 字符串参数
    /// </summary>
    public class StringParameter : ParameterBase
    {
        private ObservableCollection<string> _options = new ObservableCollection<string>();
        private int _maxLength = int.MaxValue;

        /// <summary>
        /// 可选值列表
        /// </summary>
        public ObservableCollection<string> Options
        {
            get => _options;
            set
            {
                _options = value;
                OnPropertyChanged();
            }
        }

        /// <summary>
        /// 最大长度
        /// </summary>
        public int MaxLength
        {
            get => _maxLength;
            set
            {
                _maxLength = value;
                OnPropertyChanged();
            }
        }

        public StringParameter(string name, string value = "", string helpText = "", bool isVisibleInVariableManager = false)
        {
            Name = name;
            Value = value;
            HelpText = helpText;
            IsVisibleInVariableManager = isVisibleInVariableManager;
        }

        protected override bool ValidateValue()
        {
            var stringValue = Value?.ToString() ?? string.Empty;

            if (stringValue.Length > MaxLength)
                return false;

            if (Options.Count > 0 && !Options.Contains(stringValue))
                return false;

            return true;
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

        /// <summary>
        /// 最小值
        /// </summary>
        public double MinValue
        {
            get => _minValue;
            set
            {
                _minValue = value;
                OnPropertyChanged();
            }
        }

        /// <summary>
        /// 最大值
        /// </summary>
        public double MaxValue
        {
            get => _maxValue;
            set
            {
                _maxValue = value;
                OnPropertyChanged();
            }
        }

        /// <summary>
        /// 小数位数
        /// </summary>
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
            double value = 0, string helpText = "", bool isVisibleInVariableManager = false)
        {
            Name = name;
            MinValue = minValue;
            MaxValue = maxValue;
            Value = value;
            HelpText = helpText;
            IsVisibleInVariableManager = isVisibleInVariableManager;
        }

        protected override bool ValidateValue()
        {
            if (Value is not double doubleValue)
                return false;

            return doubleValue >= MinValue && doubleValue <= MaxValue;
        }
    }

    /// <summary>
    /// 布尔参数
    /// </summary>
    public class BooleanParameter : ParameterBase
    {
        public BooleanParameter(string name, bool value = false, string helpText = "", bool isVisibleInVariableManager = false)
        {
            Name = name;
            Value = value;
            HelpText = helpText;
            IsVisibleInVariableManager = isVisibleInVariableManager;
        }

        protected override bool ValidateValue()
        {
            return Value is bool;
        }
    }

    /// <summary>
    /// 枚举参数
    /// </summary>
    public class EnumParameter : ParameterBase
    {
        private Type _enumType;

        /// <summary>
        /// 枚举类型
        /// </summary>
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

        public EnumParameter(string name, Type enumType, object? value = null, string helpText = "", bool isVisibleInVariableManager = false)
        {
            if (!enumType.IsEnum)
                throw new ArgumentException("Type must be an enum", nameof(enumType));

            Name = name;
            EnumType = enumType;
            Value = value ?? Enum.GetValues(enumType).GetValue(0);
            HelpText = helpText;
            IsVisibleInVariableManager = isVisibleInVariableManager;
        }

        protected override bool ValidateValue()
        {
            if (Value == null)
                return false;

            return Enum.IsDefined(EnumType, Value);
        }
    }
}
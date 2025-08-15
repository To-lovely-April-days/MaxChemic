using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;

namespace MaxChemical.Modules.Designer.Models
{
    public class VariableViewModel : INotifyPropertyChanged
    {
        private string _deviceName;
        private string _commandName;
        private string _variableName;
        private object _variableValue;
        private string _variableType;
        private bool _isCustomVariable;
        private bool _isSelected;
        private string _nodeId;
        private bool _hasExecutionData;
        private DateTime? _lastExecutionTime;
        /// <summary>
        /// 设备名称
        /// </summary>
        public string DeviceName
        {
            get => _deviceName;
            set
            {
                _deviceName = value;
                OnPropertyChanged();
            }
        }

        /// <summary>
        /// 命令名称
        /// </summary>
        public string CommandName
        {
            get => _commandName;
            set
            {
                _commandName = value;
                OnPropertyChanged();
            }
        }

        /// <summary>
        /// 变量名称（参数名称）
        /// </summary>
        public string VariableName
        {
            get => _variableName;
            set
            {
                _variableName = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(FullVariableName));
            }
        }

        /// <summary>
        /// 变量值
        /// </summary>
        public object VariableValue
        {
            get => _variableValue;
            set
            {
                _variableValue = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(DisplayValue));
            }
        }

        /// <summary>
        /// 变量类型
        /// </summary>
        public string VariableType
        {
            get => _variableType;
            set
            {
                _variableType = value;
                OnPropertyChanged();
                // 当类型改变时，尝试转换值
                ConvertValueToType();
            }
        }

        /// <summary>
        /// 是否为自定义变量
        /// </summary>
        public bool IsCustomVariable
        {
            get => _isCustomVariable;
            set
            {
                _isCustomVariable = value;
                OnPropertyChanged();
            }
        }

        /// <summary>
        /// 是否选中
        /// </summary>
        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                _isSelected = value;
                OnPropertyChanged();
            }
        }
        /// <summary>
        /// 节点ID，用于区分不同节点的同名变量
        /// </summary>
        public string NodeId
        {
            get => _nodeId;
            set
            {
                _nodeId = value;
                OnPropertyChanged();
            }
        }

        /// <summary>
        /// 是否有执行数据（区分是实际执行结果还是默认值）
        /// </summary>
        public bool HasExecutionData
        {
            get => _hasExecutionData;
            set
            {
                _hasExecutionData = value;
                OnPropertyChanged();
            }
        }

        /// <summary>
        /// 最后执行时间
        /// </summary>
        public DateTime? LastExecutionTime
        {
            get => _lastExecutionTime;
            set
            {
                _lastExecutionTime = value;
                OnPropertyChanged();
            }
        }

        /// <summary>
        /// 完整的变量引用名称
        /// </summary>
        public string FullVariableName
        {
            get
            {
                if (IsCustomVariable)
                {
                    return $"${{{VariableName}}}";
                }
                else if (!string.IsNullOrEmpty(DeviceName) && !string.IsNullOrEmpty(CommandName))
                {
                    return $"${{{DeviceName}.{CommandName}.{VariableName}}}";
                }
                else
                {
                    return $"${{{VariableName}}}";
                }
            }
        }

        /// <summary>
        /// 显示值
        /// </summary>
        public string DisplayValue
        {
            get
            {
                if (VariableValue == null)
                    return "null";

                // 根据类型格式化显示
                return VariableType switch
                {
                    "Double" when VariableValue is double d => d.ToString("F2"),
                    "Boolean" => VariableValue.ToString()?.ToLower() ?? "false",
                    _ => VariableValue.ToString() ?? ""
                };
            }
        }

        /// <summary>
        /// 根据类型转换值
        /// </summary>
        private void ConvertValueToType()
        {
            if (VariableValue == null) return;

            try
            {
                var stringValue = VariableValue.ToString();

                VariableValue = VariableType switch
                {
                    "Int32" => int.TryParse(stringValue, out var i) ? i : 0,
                    "Double" => double.TryParse(stringValue, out var d) ? d : 0.0,
                    "Boolean" => bool.TryParse(stringValue, out var b) ? b : false,
                    "DateTime" => DateTime.TryParse(stringValue, out var dt) ? dt : DateTime.Now,
                    _ => stringValue
                };
            }
            catch
            {
                // 转换失败时保持原值
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}



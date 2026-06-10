using System.Collections.Generic;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;

namespace DevicePlugins.Devices
{
    /// <summary>
    /// 设备参数集合
    /// </summary>
    public class DeviceParameters : INotifyPropertyChanged
    {
        private ObservableCollection<ParameterBase> _variables = new ObservableCollection<ParameterBase>();

        /// <summary>
        /// 参数变量集合
        /// </summary>
        public ObservableCollection<ParameterBase> Variables
        {
            get => _variables;
            set
            {
                _variables = value;
                OnPropertyChanged();
            }
        }

        /// <summary>
        /// 根据名称获取参数值
        /// </summary>
        /// <typeparam name="T">参数类型</typeparam>
        /// <param name="name">参数名称</param>
        /// <returns>参数值</returns>
        public T? GetValue<T>(string name)
        {
            var variable = Variables.FirstOrDefault(v => v.Name == name);
            if (variable?.Value != null)
            {
                try
                {
                    return (T)Convert.ChangeType(variable.Value, typeof(T));
                }
                catch
                {
                    return default(T);
                }
            }
            return default(T);
        }
        public T? GetNullableValue<T>(string name) where T : struct
        {
            var variable = Variables.FirstOrDefault(v => v.Name == name);
            if (variable?.Value != null)
            {
                try
                {
                    return (T)Convert.ChangeType(variable.Value, typeof(T));
                }
                catch
                {
                    return null;
                }
            }
            return null;
        }
        /// <summary>
        /// 设置参数值
        /// </summary>
        /// <typeparam name="T">参数类型</typeparam>
        /// <param name="name">参数名称</param>
        /// <param name="value">参数值</param>
        public void SetValue<T>(string name, T value)
        {
            var variable = Variables.FirstOrDefault(v => v.Name == name);
            if (variable != null)
            {
                variable.Value = value;
            }
        }

        /// <summary>
        /// 添加参数
        /// </summary>
        /// <param name="parameter">参数对象</param>
        public void AddParameter(ParameterBase parameter)
        {
            if (parameter != null && !Variables.Any(v => v.Name == parameter.Name))
            {
                Variables.Add(parameter);
            }
        }

        /// <summary>
        /// 移除参数
        /// </summary>
        /// <param name="name">参数名称</param>
        /// <returns>是否成功移除</returns>
        public bool RemoveParameter(string name)
        {
            var parameter = Variables.FirstOrDefault(v => v.Name == name);
            if (parameter != null)
            {
                return Variables.Remove(parameter);
            }
            return false;
        }

        /// <summary>
        /// 验证所有参数
        /// </summary>
        /// <returns>验证结果</returns>
        public bool ValidateAll()
        {
            return Variables.All(v => v.Validate());
        }

        /// <summary>
        /// 获取验证错误
        /// </summary>
        /// <returns>验证错误列表</returns>
        public List<string> GetValidationErrors()
        {
            var errors = new List<string>();
            foreach (var variable in Variables)
            {
                if (!variable.Validate())
                {
                    errors.Add($"参数 '{variable.Name}' 验证失败");
                }
            }
            return errors;
        }

        /// <summary>
        /// 属性变更事件
        /// </summary>
        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
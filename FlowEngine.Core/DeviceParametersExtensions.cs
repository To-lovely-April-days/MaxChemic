using DevicePlugins.Devices;

namespace FlowEngine.Core
{
    /// <summary>
    /// 设备参数扩展方法
    /// </summary>
    public static class DeviceParametersExtensions
    {
        /// <summary>
        /// 安全设置参数值
        /// </summary>
        public static void SetValue(this DeviceParameters parameters, string name, object value)
        {
            if (parameters?.Variables == null || string.IsNullOrWhiteSpace(name))
                return;

            // 查找现有参数
            var existingParam = parameters.Variables.FirstOrDefault(p => p.Name == name);
            if (existingParam != null)
            {
                existingParam.Value = value;
                return;
            }

            // 创建新参数
            ParameterBase newParam = null;

            switch (value)
            {
                case double _:
                case float _:
                case int _:
                case decimal _:
                    newParam = new NumberParameter(name);
                    break;

                case bool _:
                    newParam = new BooleanParameter(name);
                    break;

                default:
                    newParam = new StringParameter(name);
                    break;
            }

            if (newParam != null)
            {
                newParam.Value = value;
                parameters.Variables.Add(newParam);
            }
        }

        /// <summary>
        /// 安全获取参数值
        /// </summary>
        public static T GetValue<T>(this DeviceParameters parameters, string name, T defaultValue = default(T))
        {
            if (parameters?.Variables == null || string.IsNullOrWhiteSpace(name))
                return defaultValue;

            var param = parameters.Variables.FirstOrDefault(p => p.Name == name);
            if (param?.Value != null)
            {
                try
                {
                    if (param.Value is T directValue)
                        return directValue;

                    return (T)Convert.ChangeType(param.Value, typeof(T));
                }
                catch
                {
                    return defaultValue;
                }
            }

            return defaultValue;
        }
    }
}
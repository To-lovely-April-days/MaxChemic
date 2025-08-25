using DevicePlugins.Devices;
using System.Collections.ObjectModel;
using System.Diagnostics;

using System.Reflection;

/// <summary>
/// 反射优化的设备深拷贝服务 - 修复只读属性问题
/// </summary>
public class ReflectionDeviceDeepCopyService
{
    public static IDevice DeepClone(IDevice originalDevice)
    {
        if (originalDevice == null) return null;

        var stopwatch = Stopwatch.StartNew();

        try
        {
            Debug.WriteLine($"反射深拷贝开始: {originalDevice.Name}");

            var deviceType = originalDevice.GetType();

            // *** 关键修改：创建新设备实例时确保参数已初始化 ***
            var clonedDevice = CreateDeviceInstanceWithParameters(deviceType);

            if (clonedDevice == null)
            {
                throw new InvalidOperationException($"无法创建设备类型 {deviceType.Name} 的实例");
            }

            // 拷贝基本属性
            CopyBasicProperties(originalDevice, clonedDevice);

            // 拷贝参数内容（而不是替换整个Parameters对象）
            CopyParameterContents(originalDevice, clonedDevice);

            stopwatch.Stop();
            Debug.WriteLine($"反射深拷贝完成，耗时: {stopwatch.ElapsedMilliseconds}ms");

            return clonedDevice;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            Debug.WriteLine($"反射深拷贝失败，耗时: {stopwatch.ElapsedMilliseconds}ms, 错误: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// 创建设备实例并确保参数已初始化
    /// </summary>
    private static IDevice CreateDeviceInstanceWithParameters(Type deviceType)
    {
        try
        {
            // 方法1：尝试使用默认构造函数
            var device = Activator.CreateInstance(deviceType) as IDevice;

            if (device != null)
            {
                Debug.WriteLine($"设备实例创建成功: {deviceType.Name}");
                Debug.WriteLine($"Parameters是否已初始化: {device.Parameters != null}");
                Debug.WriteLine($"Parameters.Variables是否已初始化: {device.Parameters?.Variables != null}");

                return device;
            }

            return null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"创建设备实例失败: {deviceType.Name}, 错误: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// 拷贝基本属性
    /// </summary>
    private static void CopyBasicProperties(IDevice source, IDevice target)
    {
        try
        {
            target.IsSimulationMode = source.IsSimulationMode;

            // 尝试拷贝其他可写属性
            var properties = target.GetType().GetProperties()
                .Where(p => p.CanWrite && p.CanRead && p.Name != "Parameters");

            foreach (var prop in properties)
            {
                try
                {
                    if (prop.PropertyType.IsValueType || prop.PropertyType == typeof(string))
                    {
                        var value = prop.GetValue(source);
                        prop.SetValue(target, value);
                        Debug.WriteLine($"拷贝属性: {prop.Name} = {value}");
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"拷贝属性失败: {prop.Name}, 错误: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"拷贝基本属性失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 拷贝参数内容而不是替换整个Parameters对象
    /// </summary>
    private static void CopyParameterContents(IDevice source, IDevice target)
    {
        try
        {
            if (source.Parameters?.Variables == null)
            {
                Debug.WriteLine("源设备没有参数需要拷贝");
                return;
            }

            if (target.Parameters == null)
            {
                Debug.WriteLine("目标设备的Parameters为null，无法拷贝参数");
                return;
            }

            if (target.Parameters.Variables == null)
            {
                Debug.WriteLine("目标设备的Parameters.Variables为null，尝试通过反射初始化");
                TryInitializeParameterVariables(target);
            }

            if (target.Parameters.Variables == null)
            {
                Debug.WriteLine("无法初始化目标设备的参数列表");
                return;
            }

            // 清空目标参数列表
            target.Parameters.Variables.Clear();

            // 拷贝每个参数
            foreach (var sourceParam in source.Parameters.Variables)
            {
                var clonedParam = FastCloneParameter(sourceParam);
                if (clonedParam != null)
                {
                    target.Parameters.Variables.Add(clonedParam);
                    Debug.WriteLine($"拷贝参数: {sourceParam.Name} = {sourceParam.Value}");
                }
            }

            Debug.WriteLine($"参数拷贝完成，共拷贝 {target.Parameters.Variables.Count} 个参数");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"拷贝参数内容失败: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// 尝试通过反射初始化参数变量列表
    /// </summary>
    private static void TryInitializeParameterVariables(IDevice device)
    {
        try
        {
            // 尝试通过反射找到Variables属性并初始化
            var parametersType = device.Parameters.GetType();
            var variablesProperty = parametersType.GetProperty("Variables");

            if (variablesProperty != null && variablesProperty.CanWrite)
            {
                var variablesList = new ObservableCollection<ParameterBase>();
                variablesProperty.SetValue(device.Parameters, variablesList);
                Debug.WriteLine("通过反射成功初始化Parameters.Variables");
            }
            else
            {
                Debug.WriteLine("无法通过反射初始化Parameters.Variables");

                // 尝试查找私有字段
                var variablesField = parametersType.GetField("Variables",
                    BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);

                if (variablesField != null)
                {
                    var variablesList = new ObservableCollection<ParameterBase>();
                    variablesField.SetValue(device.Parameters, variablesList);
                    Debug.WriteLine("通过反射成功初始化Parameters.Variables字段");
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"反射初始化Parameters.Variables失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 快速克隆参数
    /// </summary>
    private static ParameterBase FastCloneParameter(ParameterBase original)
    {
        try
        {
            return original switch
            {
                StringParameter sp => new StringParameter(sp.Name, sp.Value as string ?? "")
                {
                    Options = sp.Options != null ? new ObservableCollection<string>(sp.Options) : null,
                    HelpText = sp.HelpText,
                    IsReadOnly = sp.IsReadOnly,
                    IsVisibleInVariableManager = sp.IsVisibleInVariableManager
                },

                NumberParameter np => new NumberParameter(np.Name, np.MinValue, np.MaxValue,
                    Convert.ToDouble(np.Value ?? 0), np.HelpText)
                {
                    DecimalPlaces = np.DecimalPlaces,
                    IsReadOnly = np.IsReadOnly,
                    IsVisibleInVariableManager = np.IsVisibleInVariableManager
                },

                BooleanParameter bp => new BooleanParameter(bp.Name, (bool)(bp.Value ?? false), bp.HelpText)
                {
                    IsReadOnly = bp.IsReadOnly,
                    IsVisibleInVariableManager = bp.IsVisibleInVariableManager
                },

                EnumParameter ep => new EnumParameter(ep.Name, ep.EnumType, ep.Value, ep.HelpText)
                {
                    IsReadOnly = ep.IsReadOnly,
                    IsVisibleInVariableManager = ep.IsVisibleInVariableManager
                },

                _ => CloneParameterByReflection(original)
            };
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"快速克隆参数失败: {original?.Name}, 错误: {ex.Message}");
            return CloneParameterByReflection(original);
        }
    }

    /// <summary>
    /// 通过反射克隆未知类型的参数
    /// </summary>
    private static ParameterBase CloneParameterByReflection(ParameterBase original)
    {
        try
        {
            if (original == null) return null;

            var paramType = original.GetType();
            var constructors = paramType.GetConstructors();

            // 寻找带参数的构造函数
            var constructor = constructors
                .OrderByDescending(c => c.GetParameters().Length)
                .FirstOrDefault();

            if (constructor != null)
            {
                var parameters = constructor.GetParameters();
                var args = new object[parameters.Length];

                // 填充构造函数参数
                for (int i = 0; i < parameters.Length; i++)
                {
                    var paramName = parameters[i].Name.ToLower();
                    if (paramName.Contains("name"))
                        args[i] = original.Name;
                    else if (paramName.Contains("value"))
                        args[i] = original.Value;
                    else if (paramName.Contains("help"))
                        args[i] = original.HelpText ?? "";
                    else
                        args[i] = GetDefaultValue(parameters[i].ParameterType);
                }

                var cloned = Activator.CreateInstance(paramType, args) as ParameterBase;
                if (cloned != null)
                {
                    // 拷贝其他属性
                    cloned.IsReadOnly = original.IsReadOnly;
                    cloned.IsVisibleInVariableManager = original.IsVisibleInVariableManager;
                    if (cloned.Value != original.Value)
                        cloned.Value = original.Value;
                }

                Debug.WriteLine($"通过反射克隆参数成功: {original.Name}");
                return cloned;
            }

            Debug.WriteLine($"无法找到合适的构造函数克隆参数: {original.Name}");
            return null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"反射克隆参数失败: {original?.Name}, 错误: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 获取类型的默认值
    /// </summary>
    private static object GetDefaultValue(Type type)
    {
        if (type.IsValueType)
            return Activator.CreateInstance(type);
        if (type == typeof(string))
            return "";
        return null;
    }
}
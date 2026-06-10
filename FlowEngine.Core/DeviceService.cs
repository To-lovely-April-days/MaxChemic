using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DevicePlugins.Devices;
using FlowEngine.Core;

namespace FlowEngine.Services
{
    /// <summary>
    /// 设备服务实现
    /// </summary>
    public class DeviceService : IDeviceService
    {
        private readonly Dictionary<string, IDevice> _devices = new Dictionary<string, IDevice>();
        private readonly object _lock = new object();

        /// <summary>
        /// 注册设备
        /// </summary>
        public void RegisterDevice(IDevice device)
        {
            if (device == null)
                throw new ArgumentNullException(nameof(device));

            lock (_lock)
            {
                _devices[device.Name] = device;
            }
        }

        /// <summary>
        /// 取消注册设备
        /// </summary>
        public void UnregisterDevice(string deviceName)
        {
            if (string.IsNullOrWhiteSpace(deviceName))
                return;

            lock (_lock)
            {
                _devices.Remove(deviceName);
            }
        }

        /// <summary>
        /// 执行设备命令
        /// </summary>
        public async Task<FlowDeviceCommandResult> ExecuteCommandAsync(string deviceName, string commandName,
            DeviceParameters inputParams, CancellationToken cancellationToken)
        {
            var startTime = DateTime.Now;

            try
            {
                var device = GetDevice(deviceName);
                if (device == null)
                {
                    return new FlowDeviceCommandResult
                    {
                        IsSuccess = false,
                        Message = $"设备 '{deviceName}' 未找到",
                        ExecutionTime = DateTime.Now - startTime
                    };
                }

                var command = device.Commands.FirstOrDefault(c => c.Name == commandName);
                if (command == null)
                {
                    return new FlowDeviceCommandResult
                    {
                        IsSuccess = false,
                        Message = $"设备 '{deviceName}' 中未找到命令 '{commandName}'",
                        ExecutionTime = DateTime.Now - startTime
                    };
                }

                cancellationToken.ThrowIfCancellationRequested();

                // 注意：这里直接返回你的设备驱动的DeviceCommandResult，然后转换为我们的FlowDeviceCommandResult
                var deviceResult = await Task.Run(() =>
                {
                    try
                    {
                        return command.Execute(inputParams);
                    }
                    catch (Exception ex)
                    {
                        throw new InvalidOperationException($"命令执行失败: {ex.Message}", ex);
                    }
                }, cancellationToken);

                // 转换为流程引擎的结果格式
                return new FlowDeviceCommandResult
                {
                    IsSuccess = deviceResult.Success,
                    Message = deviceResult.ErrorMessage ?? "命令执行完成",
                    OutputData = ExtractOutputParameters(deviceResult),
                    ExecutionTime = DateTime.Now - startTime
                };
            }
            catch (OperationCanceledException)
            {
                return new FlowDeviceCommandResult
                {
                    IsSuccess = false,
                    Message = "命令执行被取消",
                    ExecutionTime = DateTime.Now - startTime
                };
            }
            catch (Exception ex)
            {
                return new FlowDeviceCommandResult
                {
                    IsSuccess = false,
                    Message = ex.Message,
                    Exception = ex,
                    ExecutionTime = DateTime.Now - startTime
                };
            }
        }

        /// <summary>
        /// 从设备命令结果中提取输出参数
        /// </summary>
        private DeviceParameters ExtractOutputParameters(DevicePlugins.Devices.DeviceCommandResult deviceResult)
        {
            var outputParams = new DeviceParameters();

            if (deviceResult?.Success == true)
            {
                // 假设你的设备驱动有某种方式获取输出参数
                // 这里需要根据你的实际设备驱动实现来调整

                // 如果你的DeviceCommandResult有OutputParameters属性
                if (deviceResult.GetType().GetProperty("OutputParameters") != null)
                {
                    var outputData = deviceResult.GetType().GetProperty("OutputParameters").GetValue(deviceResult);
                    if (outputData is DeviceParameters parameters)
                    {
                        return parameters;
                    }
                }

                // 如果你的DeviceCommandResult有其他方式存储输出数据
                // 可以在这里添加相应的转换逻辑

                // 示例：如果输出数据存储在字典中
                if (deviceResult.GetType().GetProperty("OutputData") != null)
                {
                    var outputData = deviceResult.GetType().GetProperty("OutputData").GetValue(deviceResult);
                    if (outputData is Dictionary<string, object> dict)
                    {
                        foreach (var kvp in dict)
                        {
                            outputParams.SetValue(kvp.Key, kvp.Value);
                        }
                    }
                }
            }

            return outputParams;
        }

        /// <summary>
        /// 获取设备
        /// </summary>
        public IDevice GetDevice(string deviceName)
        {
            if (string.IsNullOrWhiteSpace(deviceName))
                return null;

            lock (_lock)
            {
                _devices.TryGetValue(deviceName, out var device);
                return device;
            }
        }

        /// <summary>
        /// 获取所有设备
        /// </summary>
        public List<IDevice> GetAllDevices()
        {
            lock (_lock)
            {
                return _devices.Values.ToList();
            }
        }

        /// <summary>
        /// 检查设备是否连接
        /// </summary>
        public bool IsDeviceConnected(string deviceName)
        {
            var device = GetDevice(deviceName);
            return device != null;
        }
    }

    /// <summary>
    /// 简单服务提供者实现
    /// </summary>
    public class SimpleServiceProvider : System.IServiceProvider
    {
        private readonly Dictionary<Type, object> _services = new Dictionary<Type, object>();

        public void RegisterService<T>(T service) where T : class
        {
            _services[typeof(T)] = service;
        }

        public object GetService(Type serviceType)
        {
            _services.TryGetValue(serviceType, out var service);
            return service;
        }

        public T GetService<T>() where T : class
        {
            return GetService(typeof(T)) as T;
        }
    }
}
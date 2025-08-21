// Service/ProjectDataService.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using DevicePlugins.Devices;
using MaxChemical.Infrastructure.Events;
using MaxChemical.Infrastructure.Services;
using MaxChemical.Modules.Designer.Event;
using MaxChemical.Modules.Designer.Models;
using MaxChemical.Modules.Designer.Plugins;
using Prism.Events;

namespace MaxChemical.Modules.Designer.Service
{
    public class ProjectDataService : IProjectDataService
    {
        private readonly IEventAggregator _eventAggregator;
        private readonly IDeviceManager _deviceManager;
        private readonly ILogicCommandPluginManager _pluginManager;

        public ProjectDataService(
            IEventAggregator eventAggregator,
            IDeviceManager deviceManager,
            ILogicCommandPluginManager pluginManager)
        {
            _eventAggregator = eventAggregator;
            _deviceManager = deviceManager;
            _pluginManager = pluginManager;
        }

        public ProjectDocument CreateNewProject()
        {
            return new ProjectDocument
            {
                Name = $"新建项目_{DateTime.Now:HHmmss}",
                CanvasDesign = new CanvasDesignData
                {
                    Name = "画布设计",
                    CanvasWidth = 1200,
                    CanvasHeight = 800
                },
                FlowDesign = new FlowDesignData
                {
                    Name = "流程设计",
                    CanvasWidth = 1200,
                    CanvasHeight = 800,
                    StartNodeX = (1200 - 231) / 2,
                    StartNodeY = 20
                }
            };
        }

        public bool SaveProject(ProjectDocument document, string filePath = null)
        {
            try
            {
                if (string.IsNullOrEmpty(filePath))
                {
                    var defaultPath = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                        "MaxChemical",
                        "Projects"
                    );

                    if (!Directory.Exists(defaultPath))
                        Directory.CreateDirectory(defaultPath);

                    filePath = Path.Combine(defaultPath, $"{document.Name}_{DateTime.Now:yyyyMMdd_HHmmss}.mcp");
                }

                document.LastModifiedTime = DateTime.Now;
                document.CanvasDesign.LastModifiedTime = DateTime.Now;
                document.FlowDesign.LastModifiedTime = DateTime.Now;

                var options = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                };

                var json = JsonSerializer.Serialize(document, options);
                File.WriteAllText(filePath, json);

                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"保存项目失败: {ex.Message}");
                return false;
            }
        }

        public ProjectDocument LoadProject(string filePath)
        {
            try
            {
                if (!File.Exists(filePath))
                    return null;

                var json = File.ReadAllText(filePath);

                // 配置JsonSerializer选项
                var options = new JsonSerializerOptions
                {
                    Converters = { new ObjectDictionaryConverter() },
                    PropertyNameCaseInsensitive = true
                };

                var document = JsonSerializer.Deserialize<ProjectDocument>(json, options);
                return document;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"加载项目失败: {ex.Message}");
                return null;
            }
        }

        public ProjectDocument GetCurrentProjectData()
        {
            ProjectDocument currentDocument = null;
            var waitHandle = new ManualResetEventSlim(false);

            try
            {
                var getDataEvent = _eventAggregator.GetEvent<GetCurrentProjectDataRequestEvent>();
                getDataEvent?.Publish((document) =>
                {
                    currentDocument = document;
                    waitHandle.Set();
                });

                if (!waitHandle.Wait(5000)) // 5秒超时
                {
                    System.Diagnostics.Debug.WriteLine("获取项目数据超时");
                    return null;
                }

                return currentDocument;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"获取项目数据失败: {ex.Message}");
                return null;
            }
            finally
            {
                waitHandle.Dispose();
            }
        }

        public DeviceCommand RestoreDeviceCommandFromData(FlowCommandNodeData nodeData)
        {
            try
            {
                if (string.IsNullOrEmpty(nodeData.DeviceCommandName))
                    return null;

                // 从设备管理器中查找设备命令
                var allDevices = _deviceManager.GetAllDevices();

                foreach (var device in allDevices)
                {
                    var command = device.Commands?.FirstOrDefault(c => c.Name == nodeData.DeviceCommandName);
                    if (command != null)
                    {
                        // 创建命令副本并恢复属性
                        var restoredCommand = new DeviceCommand
                        {
                            Name = command.Name,
                            HelpText = command.HelpText,
                            ImageLocation = command.ImageLocation,
                            InputParameters = CloneDeviceParameters(command.InputParameters),
                            OutputParameters = CloneDeviceParameters(command.OutputParameters)
                        };

                        // 恢复设备命令的属性
                        RestoreDeviceCommandProperties(restoredCommand, nodeData.DeviceCommandProperties);

                        return restoredCommand;
                    }
                }

                System.Diagnostics.Debug.WriteLine($"未找到设备命令: {nodeData.DeviceCommandName}");
                return null;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"恢复设备命令失败: {ex.Message}");
                return null;
            }
        }

        public LogicCommand RestoreLogicCommandFromData(FlowCommandNodeData nodeData)
        {
            try
            {
                if (string.IsNullOrEmpty(nodeData.LogicCommandType))
                    return null;

                var logicCommand = new LogicCommand
                {
                    Name = nodeData.LogicCommandName ?? nodeData.LogicCommandType,
                    CommandType = Enum.TryParse<LogicCommandType>(nodeData.LogicCommandType, out var commandType)
                        ? commandType
                        : LogicCommandType.Unknown
                };

                // 恢复逻辑命令的属性
                if (nodeData.LogicCommandProperties != null)
                {
                    logicCommand.Properties = new Dictionary<string, object>(nodeData.LogicCommandProperties);
                }
                else
                {
                    logicCommand.Properties = new Dictionary<string, object>();
                }

                return logicCommand;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"恢复逻辑命令失败: {ex.Message}");
                return null;
            }
        }

        public DevicePlugins.Devices.IDevice RestoreDeviceFromData(CanvasDeviceData deviceData)
        {
            try
            {
                // 从设备管理器中查找对应类型的设备
                var allDevices = _deviceManager.GetAllDevices();
                var deviceTemplate = allDevices.FirstOrDefault(d =>
                    d.GetType().Name == deviceData.DeviceType ||
                    d.Name == deviceData.DeviceName ||
                    d.Category == deviceData.Category);

                if (deviceTemplate == null)
                {
                    System.Diagnostics.Debug.WriteLine($"未找到设备类型: {deviceData.DeviceType}");
                    return null;
                }

                // 创建设备实例
                DevicePlugins.Devices.IDevice restoredDevice = null;

                try
                {
                    var deviceType = deviceTemplate.GetType();
                    restoredDevice = (DevicePlugins.Devices.IDevice)Activator.CreateInstance(deviceType);
                }
                catch
                {
                    restoredDevice = deviceTemplate;
                    System.Diagnostics.Debug.WriteLine($"警告：使用模板设备实例 - {deviceData.DeviceName}");
                }

                // 恢复设备参数
                if (restoredDevice != null && deviceData.Parameters != null)
                {
                    RestoreDeviceParameters(restoredDevice, deviceData.Parameters);
                }

                return restoredDevice;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"恢复设备失败: {ex.Message}");
                return null;
            }
        }

        private DeviceParameters CloneDeviceParameters(DeviceParameters original)
        {
            if (original?.Variables == null) return null;

            var cloned = new DeviceParameters();
            foreach (var variable in original.Variables)
            {
                // 简化实现，直接添加原始变量
                cloned.Variables.Add(variable);
            }
            return cloned;
        }

        private void RestoreDeviceCommandProperties(DeviceCommand command, Dictionary<string, object> properties)
        {
            if (properties == null || command.InputParameters?.Variables == null) return;

            foreach (var property in properties)
            {
                var parameter = command.InputParameters.Variables.FirstOrDefault(v => v.Name == property.Key);
                if (parameter != null)
                {
                    try
                    {
                        parameter.Value = property.Value;
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"恢复参数 {property.Key} 失败: {ex.Message}");
                    }
                }
            }
        }

        private void RestoreDeviceParameters(DevicePlugins.Devices.IDevice device, Dictionary<string, object> parameters)
        {
            try
            {
                if (device.Parameters?.Variables == null) return;

                foreach (var param in parameters)
                {
                    var deviceParam = device.Parameters.Variables.FirstOrDefault(v => v.Name == param.Key);
                    if (deviceParam != null)
                    {
                        try
                        {
                            if (param.Value != null)
                            {
                                // 根据参数类型进行转换
                                if (deviceParam is StringParameter stringParam)
                                {
                                    stringParam.Value = param.Value.ToString();
                                }
                                else if (deviceParam is NumberParameter numberParam)
                                {
                                    if (double.TryParse(param.Value.ToString(), out double numValue))
                                    {
                                        numberParam.Value = numValue;
                                    }
                                }
                                else if (deviceParam is BooleanParameter boolParam)
                                {
                                    if (bool.TryParse(param.Value.ToString(), out bool boolValue))
                                    {
                                        boolParam.Value = boolValue;
                                    }
                                }
                                else
                                {
                                    deviceParam.Value = param.Value;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"恢复参数 {param.Key} 失败: {ex.Message}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"恢复设备参数失败: {ex.Message}");
            }
        }
    }
}
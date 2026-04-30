// DragDropService.cs
using System;
using System.Windows;
using DevicePlugins.Devices;
using MaxChemical.DataModule.Model;
using MaxChemical.Modules.Designer.Models;
using MaxChemical.Modules.Designer.Events;
using MaxChemical.Logging;
using CommandDragInfo = MaxChemical.Modules.Designer.Events.CommandDragInfo;

namespace MaxChemical.Modules.Designer.Services
{
    public class DesignerDragDropService
    {
        private readonly ILogService _logger;

        public DesignerDragDropService(ILogService logger)
        {
            _logger = logger?.ForContext<DragDropService>() ?? throw new ArgumentNullException(nameof(logger));
        }

        public bool CanAcceptDrop(IDataObject data)
        {
            try
            {
                _logger.LogDebug("检查拖拽数据格式");

                var formats = data.GetFormats();
                _logger.LogDebug("数据格式数量: {Count}", formats.Length);

                foreach (string format in formats)
                {
                    _logger.LogTrace("检查格式: {Format}", format);
                }

                bool hasLogicCommand = data.GetDataPresent("LogicCommand");
                bool hasDeviceCommand = data.GetDataPresent("DeviceCommand");
                bool hasPluginId = data.GetDataPresent("PluginId");
                bool hasCommandDragInfo = data.GetDataPresent("CommandDragInfo");
                bool hasCommandDragData = data.GetDataPresent("CommandDragData");
                bool hasDeviceCommandType = data.GetDataPresent(typeof(DeviceCommand));
                bool hasLogicCommandType = data.GetDataPresent(typeof(LogicCommand));

                bool canAccept = hasLogicCommand || hasDeviceCommand || hasPluginId ||
                                hasCommandDragInfo || hasCommandDragData ||
                                hasDeviceCommandType || hasLogicCommandType;

                _logger.LogDebug("拖拽检查结果: {CanAccept}", canAccept);
                return canAccept;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "检查拖拽数据失败");
                return false;
            }
        }

        public object ExtractCommandFromDragData(IDataObject data)
        {
            _logger.LogDebug("开始提取拖拽数据中的命令");

            try
            {
                // 1. 优先处理 CommandDragInfo 格式
                if (data.GetDataPresent("CommandDragInfo"))
                {
                    _logger.LogDebug("尝试提取 CommandDragInfo");
                    var commandDragInfo = data.GetData("CommandDragInfo");
                    _logger.LogDebug("CommandDragInfo 类型: {Type}", commandDragInfo?.GetType().Name);
                    var command = ExtractCommandFromDragInfo(commandDragInfo);
                    if (command != null)
                    {
                        _logger.LogDebug("从 CommandDragInfo 提取成功: {Type}", command.GetType().Name);
                        return command;
                    }
                }

                // 2. 处理直接的 DeviceCommand 格式
                if (data.GetDataPresent("DeviceCommand"))
                {
                    _logger.LogDebug("尝试提取 DeviceCommand");
                    var deviceCommandData = data.GetData("DeviceCommand");
                    _logger.LogDebug("DeviceCommand 数据类型: {Type}", deviceCommandData?.GetType().Name);
                    if (deviceCommandData is DeviceCommand deviceCmd)
                    {
                        _logger.LogDebug("DeviceCommand 提取成功: {Name}", deviceCmd.Name);
                        return deviceCmd;
                    }
                }

                // 3. 处理设备命令类型格式
                if (data.GetDataPresent(typeof(DeviceCommand)))
                {
                    _logger.LogDebug("尝试提取 DeviceCommand 类型");
                    var typeCommand = data.GetData(typeof(DeviceCommand));
                    _logger.LogDebug("类型命令: {Type}", typeCommand?.GetType().Name);
                    if (typeCommand is DeviceCommand typeDeviceCmd)
                    {
                        _logger.LogDebug("类型 DeviceCommand 提取成功: {Name}", typeDeviceCmd.Name);
                        return typeDeviceCmd;
                    }
                }

                // 4. 处理逻辑命令格式
                if (data.GetDataPresent("LogicCommand"))
                {
                    _logger.LogDebug("尝试提取 LogicCommand");
                    var logicCommandData = data.GetData("LogicCommand");
                    _logger.LogDebug("LogicCommand 数据类型: {Type}", logicCommandData?.GetType().Name);
                    if (logicCommandData is LogicCommand logicCmd)
                    {
                        _logger.LogDebug("LogicCommand 提取成功: {Name}", logicCmd.Name);
                        return logicCmd;
                    }
                    else
                    {
                        var command = ExtractCommandFromDragInfo(logicCommandData);
                        if (command != null)
                        {
                            _logger.LogDebug("从 LogicCommand 数据提取成功: {Type}", command.GetType().Name);
                            return command;
                        }
                    }
                }

                // 5. 处理插件ID格式
                if (data.GetDataPresent("PluginId"))
                {
                    _logger.LogDebug("尝试提取 PluginId");
                    var pluginIdData = data.GetData("PluginId");
                    _logger.LogDebug("PluginId: {PluginId}", pluginIdData);
                    if (pluginIdData != null)
                    {
                        return new PluginIdRequest { PluginId = pluginIdData.ToString() };
                    }
                }

                _logger.LogWarning("所有提取方法都失败了");
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "提取命令时发生异常");
                return null;
            }
        }
        /// <summary>
        /// 从拖拽数据中提取完整的 CommandDragInfo(包含 Command 和 SourceDeviceType)。
        /// 注意:这里特意用全限定名 MaxChemical.DataModule.Model.CommandDragInfo,
        /// 因为本文件顶部有 alias 把 CommandDragInfo 重定向到了 Events 命名空间下的简化类型,
        /// 而拖拽起点(CommadLibraryView.StartCommandDrag)写入 dragData 的是 DataModule.Model 版本。
        /// </summary>
        public MaxChemical.DataModule.Model.CommandDragInfo ExtractCommandDragInfo(IDataObject data)
        {
            try
            {
                if (data.GetDataPresent("CommandDragInfo"))
                {
                    var raw = data.GetData("CommandDragInfo");

                    // 1. 直接强类型匹配 DataModule.Model 版本(里面带 SourceDeviceType)
                    if (raw is MaxChemical.DataModule.Model.CommandDragInfo dragInfo)
                    {
                        _logger.LogDebug("提取到完整 CommandDragInfo: Command={Cmd}, SourceDeviceType={Type}",
                            dragInfo.Command?.Name, dragInfo.SourceDeviceType ?? "(null)");
                        return dragInfo;
                    }

                    // 2. 反射兜底:有可能是 Events 命名空间下的简化版本,或跨程序集类型实例
                    if (raw != null)
                    {
                        var t = raw.GetType();
                        if (t.Name == "CommandDragInfo")
                        {
                            var cmdObj = t.GetProperty("Command")?.GetValue(raw);
                            var cmd = cmdObj as DeviceCommand;
                            var srcType = t.GetProperty("SourceDeviceType")?.GetValue(raw) as string;
                            if (cmd != null)
                            {
                                _logger.LogDebug("反射提取 CommandDragInfo: Command={Cmd}, SourceDeviceType={Type}",
                                    cmd.Name, srcType ?? "(null)");
                                return new MaxChemical.DataModule.Model.CommandDragInfo
                                {
                                    Command = cmd,
                                    SourceDeviceType = srcType
                                };
                            }
                        }
                    }
                }

                // 3. 最后兜底:用老的 ExtractCommandFromDragData 拿到 command,包成 DataModel 版本
                //    这条路没有 SourceDeviceType,会触发 GetBindingCandidatesForNode 的按命令名兜底匹配
                var fallbackCmd = ExtractCommandFromDragData(data) as DeviceCommand;
                if (fallbackCmd != null)
                {
                    _logger.LogDebug("通过兜底路径提取到 DeviceCommand: {Cmd},无 SourceDeviceType", fallbackCmd.Name);
                    return new MaxChemical.DataModule.Model.CommandDragInfo
                    {
                        Command = fallbackCmd,
                        SourceDeviceType = null
                    };
                }

                _logger.LogWarning("ExtractCommandDragInfo 未能提取到任何命令信息");
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "提取 CommandDragInfo 失败");
                return null;
            }
        }
        private object ExtractCommandFromDragInfo(object commandDragInfo)
        {
            try
            {
                if (commandDragInfo == null) return null;

                if (commandDragInfo is CommandDragInfo dragInfo)
                {
                    return dragInfo.Command;
                }

                // 使用反射处理
                var type = commandDragInfo.GetType();
                if (type.Name == "CommandDragInfo")
                {
                    var commandProperty = type.GetProperty("Command");
                    if (commandProperty != null)
                    {
                        var command = commandProperty.GetValue(commandDragInfo);
                        if (command is DeviceCommand deviceCmd)
                        {
                            return deviceCmd;
                        }
                    }
                }

                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "从 CommandDragInfo 提取命令失败");
                return null;
            }
        }

        public void SetDragOverStyle(System.Windows.Controls.Border border)
        {
            if (border != null)
            {
                border.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x20, 0x60, 0x7D, 0x8B));
                border.BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x40, 0x60, 0x7D, 0x8B));
                border.Uid = "DragOver";
            }
        }

        public void ClearDragOverStyle(System.Windows.Controls.Border border)
        {
            if (border != null)
            {
                border.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x08, 0x60, 0x7D, 0x8B));
                border.BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x1E, 0x60, 0x7D, 0x8B));
                border.Uid = null;
            }
        }
    }

    public class PluginIdRequest
    {
        public string PluginId { get; set; }
    }
}
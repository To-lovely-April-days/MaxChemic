// MaxChemical.Modules.Designer/Services/ExperimentDataAdapter.cs
using DevicePlugins.Devices;
using MaxChemical.Data.Models;
using MaxChemical.Data.Services;
using MaxChemical.Logging;
using MaxChemical.Modules.Designer.Models;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace MaxChemical.Modules.Designer.Services
{
    /// <summary>
    /// 实验数据适配器实现，负责将Designer模块的模型转换为Data模块的模型
    /// </summary>
    public class ExperimentDataAdapter : IExperimentDataAdapter
    {
        private readonly IExperimentDataCollectionService _dataCollectionService;
        private readonly ILogService _logger;

        public ExperimentDataAdapter(IExperimentDataCollectionService dataCollectionService, ILogService logger)
        {
            _dataCollectionService = dataCollectionService ?? throw new ArgumentNullException(nameof(dataCollectionService));
            _logger = logger?.ForContext<ExperimentDataAdapter>() ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<string> StartExperimentAsync(string flowName, bool IsSimulationMode, string? description = null, string? operatorName = null, string? tags = null)
        {
            return await _dataCollectionService.StartExperimentAsync(flowName, IsSimulationMode, description, operatorName, tags);
        }

        public async Task UpdateExperimentProgressAsync(string experimentId, int completedNodes, int totalNodes)
        {
            await _dataCollectionService.UpdateExperimentProgressAsync(experimentId, completedNodes, totalNodes);
        }

        public async Task CompleteExperimentAsync(string experimentId, bool isSuccessful, string? errorMessage = null)
        {
            await _dataCollectionService.CompleteExperimentAsync(experimentId, isSuccessful, errorMessage);
        }

        public async Task RecordNodeExecutionAsync(string experimentId, CommandNode node, DateTime startTime, DateTime? endTime, bool isSuccessful, string? errorMessage = null)
        {
            try
            {
                // 转换Designer模块的CommandNode为Data模块的NodeExecutionInfo
                var nodeInfo = new NodeExecutionInfo
                {
                    NodeId = node.NodeId,
                    NodeName = node.DisplayName,
                    NodeType = GetNodeType(node),
                    DeviceId = node.BoundDeviceId,
                    DeviceName = node.BoundDeviceName,
                    CommandName = node.DeviceCommand?.Name ?? node.LogicCommand?.CommandType.ToString(),
                    InputParameters = ConvertToDeviceParameterCollection(node.DeviceCommand?.InputParameters),
                    OutputParameters = ConvertToDeviceParameterCollection(node.LastOutputParameters)
                };

                await _dataCollectionService.RecordNodeExecutionAsync(experimentId, nodeInfo, startTime, endTime, isSuccessful, errorMessage);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "适配器记录节点执行失败: {NodeId}", node.NodeId);
            }
        }

        public async Task RecordDeviceDataAsync(string experimentId, string deviceId, string deviceName, string commandName,
            DeviceParameters? inputParams, DeviceParameters? outputParams, string? nodeId = null, string? nodeName = null)
        {
            try
            {
                // 转换Designer模块的DeviceParameters为Data模块的DeviceParameterCollection
                var inputCollection = ConvertToDeviceParameterCollection(inputParams);
                var outputCollection = ConvertToDeviceParameterCollection(outputParams);

                await _dataCollectionService.RecordDeviceDataAsync(experimentId, deviceId, deviceName, commandName,
                    inputCollection, outputCollection, nodeId, nodeName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "适配器记录设备数据失败: {DeviceId}", deviceId);
            }
        }

        public async Task RecordVariableAsync(string experimentId, string variableName, object? value, string variableType, string? sourceNodeId = null, string? sourceDeviceId = null)
        {
            await _dataCollectionService.RecordVariableAsync(experimentId, variableName, value, variableType, sourceNodeId, sourceDeviceId);
        }

        public async Task<bool> IsExperimentActiveAsync(string experimentId)
        {
            return await _dataCollectionService.IsExperimentActiveAsync(experimentId);
        }

        public string? GetCurrentExperimentId()
        {
            return _dataCollectionService.GetCurrentExperimentId();
        }

        // 私有转换方法
        private string GetNodeType(CommandNode node)
        {
            if (node.DeviceCommand != null)
                return "DeviceCommand";
            if (node.LogicCommand != null)
                return node.LogicCommand.CommandType.ToString();
            return "Unknown";
        }

        private DeviceParameterCollection? ConvertToDeviceParameterCollection(DeviceParameters? parameters)
        {
            if (parameters?.Variables == null || !parameters.Variables.Any())
                return null;

            var collection = new DeviceParameterCollection();

            foreach (var param in parameters.Variables)
            {
                var paramType = param switch
                {
                    NumberParameter => "Number",
                    StringParameter => "String",
                    BooleanParameter => "Boolean",
                    _ => "String"
                };

                collection.AddParameter(param.Name, param.Value, paramType);
            }

            return collection;
        }
    }
}
// MaxChemical.Data/Services/ExperimentDataCollectionService.cs
using MaxChemical.Data.Models;
using MaxChemical.Data.Repositories;
using MaxChemical.Logging;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace MaxChemical.Data.Services
{
    public class ExperimentDataCollectionService : IExperimentDataCollectionService
    {
        private readonly IExperimentDataRepository _repository;
        private readonly ILogService _logger;
        private string? _currentExperimentId;
        private int _nodeExecutionOrder = 0;

        public ExperimentDataCollectionService(IExperimentDataRepository repository, ILogService logger)
        {
            _repository = repository ?? throw new ArgumentNullException(nameof(repository));
            _logger = logger?.ForContext<ExperimentDataCollectionService>() ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<string> StartExperimentAsync(string flowName,bool IsSimulationMode, string? description = null, string? operatorName = null, string? tags = null)
        {
            try
            {
                var experiment = new ExperimentRecord
                {
                    ExperimentId = Guid.NewGuid().ToString(),
                    ExperimentName = $"{flowName}_{DateTime.Now:yyyyMMdd_HHmmss}",
                    FlowName = flowName,
                    StartTime = DateTime.Now,
                    IsSuccessful = false,
                    IsSimulationMode = IsSimulationMode,
                    OperatorName = operatorName ?? Environment.UserName,
                    Description = description,
                    Tags = tags,
                    TotalNodes = 0,
                    CompletedNodes = 0
                };

                _currentExperimentId = await _repository.CreateExperimentAsync(experiment);
                _nodeExecutionOrder = 0;

                _logger.LogInformation("开始实验记录: {ExperimentId} - {FlowName}", _currentExperimentId, flowName);
                return _currentExperimentId;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "开始实验记录失败");
                throw;
            }
        }

        public async Task UpdateExperimentProgressAsync(string experimentId, int completedNodes, int totalNodes)
        {
            try
            {
                var experiment = await _repository.GetExperimentAsync(experimentId);
                if (experiment != null)
                {
                    experiment.TotalNodes = totalNodes;
                    experiment.CompletedNodes = completedNodes;
                    await _repository.UpdateExperimentAsync(experiment);

                    _logger.LogDebug("更新实验进度: {ExperimentId} - {Completed}/{Total}", experimentId, completedNodes, totalNodes);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "更新实验进度失败: {ExperimentId}", experimentId);
            }
        }

        public async Task CompleteExperimentAsync(string experimentId, bool isSuccessful, string? errorMessage = null)
        {
            try
            {
                var experiment = await _repository.GetExperimentAsync(experimentId);
                if (experiment != null)
                {
                    experiment.EndTime = DateTime.Now;
                    experiment.IsSuccessful = isSuccessful;
                    experiment.ErrorMessage = errorMessage;

                    if (experiment.StartTime != default)
                    {
                        experiment.DurationSeconds = (experiment.EndTime.Value - experiment.StartTime).TotalSeconds;
                    }

                    await _repository.UpdateExperimentAsync(experiment);

                    _logger.LogInformation("完成实验记录: {ExperimentId} - 成功: {Success}, 持续时间: {Duration}s",
                        experimentId, isSuccessful, experiment.DurationSeconds);
                }

                if (_currentExperimentId == experimentId)
                {
                    _currentExperimentId = null;
                    _nodeExecutionOrder = 0;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "完成实验记录失败: {ExperimentId}", experimentId);
            }
        }

        public async Task RecordNodeExecutionAsync(string experimentId, NodeExecutionInfo nodeInfo, DateTime startTime, DateTime? endTime, bool isSuccessful, string? errorMessage = null)
        {
            try
            {
                var nodeRecord = new ExperimentNodeRecord
                {
                    ExperimentId = experimentId,
                    NodeId = nodeInfo.NodeId,
                    NodeName = nodeInfo.NodeName,
                    NodeType = nodeInfo.NodeType,
                    DeviceId = nodeInfo.DeviceId,
                    DeviceName = nodeInfo.DeviceName,
                    CommandName = nodeInfo.CommandName,
                    StartTime = startTime,
                    EndTime = endTime,
                    DurationMs = endTime.HasValue ? (endTime.Value - startTime).TotalMilliseconds : null,
                    IsSuccessful = isSuccessful,
                    ErrorMessage = errorMessage,
                    ExecutionOrder = ++_nodeExecutionOrder,
                    InputParameters = SerializeParameters(nodeInfo.InputParameters),
                    OutputParameters = SerializeParameters(nodeInfo.OutputParameters)
                };

                await _repository.AddNodeRecordAsync(nodeRecord);

                _logger.LogDebug("记录节点执行: {NodeId} - {NodeName} - 成功: {Success}", nodeInfo.NodeId, nodeInfo.NodeName, isSuccessful);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "记录节点执行失败: {NodeId}", nodeInfo.NodeId);
            }
        }

        // 关键方法：实时记录所有设备数据
        public async Task RecordDeviceDataAsync(string experimentId, string deviceId, string deviceName, string commandName,
            DeviceParameterCollection? inputParams, DeviceParameterCollection? outputParams, string? nodeId = null, string? nodeName = null)
        {
            try
            {
                var deviceDataList = new List<ExperimentDeviceData>();
                var timestamp = DateTime.Now;

                // 记录输入参数
                if (inputParams?.HasParameters == true)
                {
                    foreach (var param in inputParams.Parameters)
                    {
                        deviceDataList.Add(new ExperimentDeviceData
                        {
                            ExperimentId = experimentId,
                            DeviceId = deviceId,
                            DeviceName = deviceName,
                            ParameterName = param.Name,
                            ParameterValue = param.Value?.ToString() ?? "",
                            ParameterType = param.GetParameterType(),
                            Unit = GetParameterUnit(param),
                            Timestamp = timestamp,
                            CommandName = commandName,
                            NodeId = nodeId,
                            NodeName = nodeName,
                            DataDirection = "INPUT"
                        });
                    }
                }

                // 记录输出参数（重点：实时记录所有输出参数）
                if (outputParams?.HasParameters == true)
                {
                    foreach (var param in outputParams.Parameters)
                    {
                        deviceDataList.Add(new ExperimentDeviceData
                        {
                            ExperimentId = experimentId,
                            DeviceId = deviceId,
                            DeviceName = deviceName,
                            ParameterName = param.Name,
                            ParameterValue = param.Value?.ToString() ?? "",
                            ParameterType = param.GetParameterType(),
                            Unit = GetParameterUnit(param),
                            Timestamp = timestamp,
                            CommandName = commandName,
                            NodeId = nodeId,
                            NodeName = nodeName,
                            DataDirection = "OUTPUT"
                        });
                    }
                }

                if (deviceDataList.Any())
                {
                    await _repository.AddDeviceDataBatchAsync(deviceDataList);
                    _logger.LogDebug("记录设备数据: {DeviceId} - {CommandName} - {Count} 个参数", deviceId, commandName, deviceDataList.Count);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "记录设备数据失败: {DeviceId}", deviceId);
            }
        }

        public async Task RecordVariableAsync(string experimentId, string variableName, object? value, string variableType, string? sourceNodeId = null, string? sourceDeviceId = null)
        {
            try
            {
                var variable = new ExperimentVariable
                {
                    ExperimentId = experimentId,
                    VariableName = variableName,
                    VariableValue = value?.ToString() ?? "",
                    VariableType = variableType,
                    SourceNodeId = sourceNodeId,
                    SourceDeviceId = sourceDeviceId,
                    Timestamp = DateTime.Now
                };

                await _repository.AddVariableAsync(variable);
                _logger.LogDebug("记录变量: {VariableName} = {Value}", variableName, value);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "记录变量失败: {VariableName}", variableName);
            }
        }

        public async Task<bool> IsExperimentActiveAsync(string experimentId)
        {
            try
            {
                var experiment = await _repository.GetExperimentAsync(experimentId);
                return experiment != null && experiment.EndTime == null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "检查实验状态失败: {ExperimentId}", experimentId);
                return false;
            }
        }

        public string? GetCurrentExperimentId()
        {
            return _currentExperimentId;
        }

        private string? SerializeParameters(DeviceParameterCollection? parameters)
        {
            if (parameters?.HasParameters != true)
                return null;

            try
            {
                var paramDict = parameters.Parameters.ToDictionary(
                    p => p.Name,
                    p => new { Type = p.GetParameterType(), Value = p.Value }
                );
                return JsonConvert.SerializeObject(paramDict);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "序列化参数失败");
                return null;
            }
        }

        private string? GetParameterUnit(DeviceParameterBase param)
        {
            return param.Name.ToLower() switch
            {
                var name when name.Contains("temp") => "°C",
                var name when name.Contains("pressure") => "Pa",
                var name when name.Contains("flow") => "L/min",
                var name when name.Contains("volume") => "L",
                var name when name.Contains("mass") => "kg",
                var name when name.Contains("speed") => "rpm",
                _ => null
            };
        }
    }
}
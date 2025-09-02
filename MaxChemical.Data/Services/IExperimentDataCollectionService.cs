// MaxChemical.Data/Services/IExperimentDataCollectionService.cs
using MaxChemical.Data.Models;
using MaxChemical.Data.Repositories;
using MaxChemical.Logging;
using System;
using System.Threading.Tasks;

namespace MaxChemical.Data.Services
{
    public interface IExperimentDataCollectionService
    {
        Task<string> StartExperimentAsync(string flowName, bool StartExperimentAsync, string? description = null, string? operatorName = null, string? tags = null);
        Task UpdateExperimentProgressAsync(string experimentId, int completedNodes, int totalNodes);
        Task CompleteExperimentAsync(string experimentId, bool isSuccessful, string? errorMessage = null);
        Task RecordNodeExecutionAsync(string experimentId, NodeExecutionInfo nodeInfo, DateTime startTime, DateTime? endTime, bool isSuccessful, string? errorMessage = null);
        Task RecordDeviceDataAsync(string experimentId, string deviceId, string deviceName, string commandName, DeviceParameterCollection? inputParams, DeviceParameterCollection? outputParams, string? nodeId = null, string? nodeName = null);
        Task RecordVariableAsync(string experimentId, string variableName, object? value, string variableType, string? sourceNodeId = null, string? sourceDeviceId = null);
        Task<bool> IsExperimentActiveAsync(string experimentId);
        string? GetCurrentExperimentId();
    }
}


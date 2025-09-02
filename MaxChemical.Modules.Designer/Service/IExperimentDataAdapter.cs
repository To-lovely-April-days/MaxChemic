// MaxChemical.Modules.Designer/Services/IExperimentDataAdapter.cs
using DevicePlugins.Devices;
using MaxChemical.Data.Models;
using MaxChemical.Data.Services;
using MaxChemical.Logging;
using MaxChemical.Modules.Designer.Models;
using System;
using System.Threading.Tasks;

namespace MaxChemical.Modules.Designer.Services
{
    /// <summary>
    /// 实验数据适配器接口，用于桥接Designer模块和Data模块
    /// </summary>
    public interface IExperimentDataAdapter
    {
        Task<string> StartExperimentAsync(string flowName, bool IsSimulationMode, string? description = null, string? operatorName = null, string? tags = null);
        Task UpdateExperimentProgressAsync(string experimentId, int completedNodes, int totalNodes);
        Task CompleteExperimentAsync(string experimentId, bool isSuccessful, string? errorMessage = null);
        Task RecordNodeExecutionAsync(string experimentId, CommandNode node, DateTime startTime, DateTime? endTime, bool isSuccessful, string? errorMessage = null);
        Task RecordDeviceDataAsync(string experimentId, string deviceId, string deviceName, string commandName, DeviceParameters? inputParams, DeviceParameters? outputParams, string? nodeId = null, string? nodeName = null);
        Task RecordVariableAsync(string experimentId, string variableName, object? value, string variableType, string? sourceNodeId = null, string? sourceDeviceId = null);
        Task<bool> IsExperimentActiveAsync(string experimentId);
        string? GetCurrentExperimentId();
    }
}


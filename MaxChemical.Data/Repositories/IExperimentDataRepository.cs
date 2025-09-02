// MaxChemical.Data/Repositories/IExperimentDataRepository.cs
using MaxChemical.Data.Models;
using MaxChemical.Logging;
using PetaPoco;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace MaxChemical.Data.Repositories
{
    public interface IExperimentDataRepository
    {
        // 实验记录管理
        Task<string> CreateExperimentAsync(ExperimentRecord experiment);
        Task UpdateExperimentAsync(ExperimentRecord experiment);
        Task<ExperimentRecord?> GetExperimentAsync(string experimentId);
        Task<(List<ExperimentRecord> Records, int TotalCount)> GetExperimentsPagedAsync(
            DateTime? startDate = null, DateTime? endDate = null,
            bool? isSuccessful = null, bool? isSimulation = null,
            string? searchText = null, int pageIndex = 0, int pageSize = 20);

        // 节点记录管理
        Task AddNodeRecordAsync(ExperimentNodeRecord nodeRecord);
        Task<List<ExperimentNodeRecord>> GetNodeRecordsAsync(string experimentId);

        // 设备数据管理（实时记录所有数据）
        Task AddDeviceDataAsync(ExperimentDeviceData deviceData);
        Task AddDeviceDataBatchAsync(List<ExperimentDeviceData> deviceDataList);
        Task<List<ExperimentDeviceData>> GetDeviceDataAsync(string experimentId, string? deviceId = null);

        // 参数变化记录（用于导出的去重数据）
        Task<List<ParameterChangeRecord>> GetParameterChangesForExportAsync(string experimentId);
        Task<List<ParameterChangeRecord>> GetParameterChangesForExportAsync(DateTime? startDate = null, DateTime? endDate = null);

        // 变量记录
        Task AddVariableAsync(ExperimentVariable variable);
        Task<List<ExperimentVariable>> GetVariablesAsync(string experimentId);

        // 统计分析
        Task<ExperimentStatistics> GetStatisticsAsync(DateTime? startDate = null, DateTime? endDate = null);
        Task<List<ExperimentTrendData>> GetTrendDataAsync(DateTime startDate, DateTime endDate, string interval = "day");
        Task<List<DeviceUsageStatistics>> GetDeviceUsageStatisticsAsync(DateTime? startDate = null, DateTime? endDate = null);

        // 数据导出
        Task<List<ExperimentRecord>> GetExperimentsForExportAsync(DateTime? startDate = null, DateTime? endDate = null);

        // 数据清理
        Task DeleteExperimentAsync(string experimentId);
    }
}


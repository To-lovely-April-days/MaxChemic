using MaxChemical.Data.Models;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace MaxChemical.Data.Services
{
    public interface IExcelExportService
    {
        Task<string> ExportExperimentsAsync(List<ExperimentRecord> experiments, string fileName);
        Task<string> ExportParameterChangesAsync(List<ParameterChangeRecord> parameterChanges, string fileName);
        Task<string> ExportExperimentWithParameterChangesAsync(string experimentId, string fileName);
        Task<string> ExportStatisticsAsync(ExperimentStatistics statistics, List<ExperimentTrendData> trendData, List<DeviceUsageStatistics> deviceStats, string fileName);
        Task<string> ExportExperimentDetailAsync(ExperimentRecord experiment, string fileName);
    }
}

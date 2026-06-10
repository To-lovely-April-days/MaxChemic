using MaxChemical.Data.Models;
using MaxChemical.Data.Services;
using MaxChemical.Logging;
using PetaPoco;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace MaxChemical.Data.Repositories
{
    public class ExperimentDataRepository : IExperimentDataRepository
    {
        private readonly IDatabaseConnectionFactory _connectionFactory;
        private readonly ILogService _logger;

        public ExperimentDataRepository(IDatabaseConnectionFactory connectionFactory, ILogService logger)
        {
            _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
            _logger = logger?.ForContext<ExperimentDataRepository>() ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<string> CreateExperimentAsync(ExperimentRecord experiment)
        {
            return await _connectionFactory.ExecuteAsync(async database =>
            {
                if (string.IsNullOrEmpty(experiment.ExperimentId))
                {
                    experiment.ExperimentId = Guid.NewGuid().ToString();
                }

                experiment.CreatedTime = DateTime.Now;
                experiment.UpdatedTime = DateTime.Now;

                await database.InsertAsync(experiment);

                _logger.LogInformation("创建实验记录成功: {ExperimentId}", experiment.ExperimentId);
                return experiment.ExperimentId;
            });
        }

        public async Task UpdateExperimentAsync(ExperimentRecord experiment)
        {
            await _connectionFactory.ExecuteAsync(async database =>
            {
                experiment.UpdatedTime = DateTime.Now;
                await database.UpdateAsync(experiment);
                _logger.LogDebug("更新实验记录成功: {ExperimentId}", experiment.ExperimentId);
            });
        }

        public async Task<ExperimentRecord?> GetExperimentAsync(string experimentId)
        {
            return await _connectionFactory.ExecuteAsync(async database =>
            {
                var experiment = await database.SingleOrDefaultAsync<ExperimentRecord>(
                    "WHERE experiment_id = @0", experimentId);

                if (experiment != null)
                {
                    experiment.NodeRecords = await GetNodeRecordsAsync(experimentId);
                }

                return experiment;
            });
        }

        public async Task<(List<ExperimentRecord> Records, int TotalCount)> GetExperimentsPagedAsync(
            DateTime? startDate = null, DateTime? endDate = null,
            bool? isSuccessful = null, bool? isSimulation = null,
            string? searchText = null, int pageIndex = 0, int pageSize = 20)
        {
            return await _connectionFactory.ExecuteAsync(async database =>
            {
                var whereClause = "WHERE 1=1";
                var parameters = new List<object>();

                if (startDate.HasValue)
                {
                    whereClause += $" AND start_time >= @{parameters.Count}";
                    parameters.Add(startDate.Value);
                }

                if (endDate.HasValue)
                {
                    whereClause += $" AND start_time <= @{parameters.Count}";
                    parameters.Add(endDate.Value);
                }

                if (isSuccessful.HasValue)
                {
                    whereClause += $" AND is_successful = @{parameters.Count}";
                    parameters.Add(isSuccessful.Value);
                }

                if (isSimulation.HasValue)
                {
                    whereClause += $" AND simulation_mode = @{parameters.Count}";
                    parameters.Add(isSimulation.Value);
                }

                if (!string.IsNullOrWhiteSpace(searchText))
                {
                    whereClause += $" AND (experiment_name LIKE @{parameters.Count} OR flow_name LIKE @{parameters.Count + 1} OR operator_name LIKE @{parameters.Count + 2})";
                    var searchPattern = $"%{searchText}%";
                    parameters.Add(searchPattern);
                    parameters.Add(searchPattern);
                    parameters.Add(searchPattern);
                }

                // 获取总数
                var countSql = $"SELECT COUNT(*) FROM experiment_records {whereClause}";
                var totalCount = await database.ExecuteScalarAsync<int>(countSql, parameters.ToArray());

                // 获取分页数据
                var dataSql = $@"
                SELECT * FROM experiment_records 
                {whereClause} 
                ORDER BY start_time DESC 
                LIMIT @{parameters.Count} OFFSET @{parameters.Count + 1}";

                parameters.Add(pageSize);
                parameters.Add(pageIndex * pageSize);

                var records = await database.FetchAsync<ExperimentRecord>(dataSql, parameters.ToArray());

                _logger.LogDebug("获取实验记录分页数据成功: {Count}/{Total}", records.Count, totalCount);
                return (records, totalCount);
            });
        }

        public async Task AddNodeRecordAsync(ExperimentNodeRecord nodeRecord)
        {
            await _connectionFactory.ExecuteAsync(async database =>
            {
                nodeRecord.CreatedTime = DateTime.Now;
                await database.InsertAsync(nodeRecord);
                _logger.LogDebug("添加节点记录成功: {NodeId}", nodeRecord.NodeId);
            });
        }

        public async Task<List<ExperimentNodeRecord>> GetNodeRecordsAsync(string experimentId)
        {
            return await _connectionFactory.ExecuteAsync(async database =>
            {
                return await database.FetchAsync<ExperimentNodeRecord>(
                    "WHERE experiment_id = @0 ORDER BY execution_order", experimentId);
            });
        }

        public async Task AddDeviceDataAsync(ExperimentDeviceData deviceData)
        {
            await _connectionFactory.ExecuteAsync(async database =>
            {
                deviceData.CreatedTime = DateTime.Now;
                await database.InsertAsync(deviceData);
                _logger.LogDebug("添加设备数据成功: {DeviceId}", deviceData.DeviceId);
            });
        }

        public async Task AddDeviceDataBatchAsync(List<ExperimentDeviceData> deviceDataList)
        {
            if (deviceDataList?.Any() != true) return;

            await _connectionFactory.ExecuteAsync(async database =>
            {
                var now = DateTime.Now;
                foreach (var data in deviceDataList)
                {
                    data.CreatedTime = now;
                }

                // 使用事务批量插入
                using var transaction = database.GetTransaction();
                try
                {
                    foreach (var data in deviceDataList)
                    {
                        await database.InsertAsync(data);
                    }
                    transaction.Complete();
                    _logger.LogDebug("批量添加设备数据成功: {Count} 条记录", deviceDataList.Count);
                }
                catch
                {
                    // 事务会自动回滚
                    throw;
                }
            });
        }

        public async Task<List<ExperimentDeviceData>> GetDeviceDataAsync(string experimentId, string? deviceId = null)
        {
            return await _connectionFactory.ExecuteAsync(async database =>
            {
                var whereClause = "WHERE experiment_id = @0";
                var parameters = new List<object> { experimentId };

                if (!string.IsNullOrEmpty(deviceId))
                {
                    whereClause += " AND device_id = @1";
                    parameters.Add(deviceId);
                }

                whereClause += " ORDER BY timestamp";

                return await database.FetchAsync<ExperimentDeviceData>(
                    whereClause, parameters.ToArray());
            });
        }

        // 关键方法：获取参数变化记录（用于导出的去重数据）
        public async Task<List<ParameterChangeRecord>> GetParameterChangesForExportAsync(string experimentId)
        {
            return await _connectionFactory.ExecuteAsync(async database =>
            {
                var sql = @"
                    WITH parameter_changes AS (
                        SELECT 
                            edd.*,
                            er.experiment_name,
                            LAG(edd.parameter_value) OVER (
                                PARTITION BY edd.device_id, edd.parameter_name 
                                ORDER BY edd.timestamp
                            ) as previous_value,
                            ROW_NUMBER() OVER (
                                PARTITION BY edd.device_id, edd.parameter_name 
                                ORDER BY edd.timestamp
                            ) as row_num
                        FROM experiment_device_data edd
                        INNER JOIN experiment_records er ON edd.experiment_id = er.experiment_id
                        WHERE edd.experiment_id = @0 
                        AND edd.data_direction = 'OUTPUT'
                        ORDER BY edd.device_id, edd.parameter_name, edd.timestamp
                    )
                    SELECT 
                        experiment_id as ExperimentId,
                        experiment_name as ExperimentName,
                        device_id as DeviceId,
                        device_name as DeviceName,
                        parameter_name as ParameterName,
                        parameter_value as ParameterValue,
                        previous_value as PreviousValue,
                        unit as Unit,
                        timestamp as Timestamp,
                        command_name as CommandName,
                        node_name as NodeName,
                        row_num as ChangeSequence,
                        CASE WHEN row_num = 1 THEN 1 ELSE 0 END as IsFirstRecord,
                        CASE WHEN row_num = 1 OR parameter_value != COALESCE(previous_value, '') THEN 1 ELSE 0 END as IsValueChanged
                    FROM parameter_changes
                    WHERE row_num = 1 OR parameter_value != COALESCE(previous_value, '')
                    ORDER BY device_id, parameter_name, timestamp";

                var results = await database.FetchAsync<dynamic>(sql, experimentId);

                var changeRecords = new List<ParameterChangeRecord>();
                DateTime? lastChangeTime = null;

                foreach (var result in results)
                {
                    var record = new ParameterChangeRecord
                    {
                        ExperimentId = result.ExperimentId?.ToString() ?? "",
                        ExperimentName = result.ExperimentName?.ToString() ?? "",
                        DeviceId = result.DeviceId?.ToString() ?? "",
                        DeviceName = result.DeviceName?.ToString() ?? "",
                        ParameterName = result.ParameterName?.ToString() ?? "",
                        ParameterValue = result.ParameterValue?.ToString() ?? "",
                        PreviousValue = result.PreviousValue?.ToString(),
                        Unit = result.Unit?.ToString(),
                        Timestamp = Convert.ToDateTime(result.Timestamp),
                        CommandName = result.CommandName?.ToString(),
                        NodeName = result.NodeName?.ToString(),
                        ChangeSequence = Convert.ToInt32(result.ChangeSequence),
                        IsFirstRecord = Convert.ToBoolean(result.IsFirstRecord),
                        IsValueChanged = Convert.ToBoolean(result.IsValueChanged)
                    };

                    if (lastChangeTime.HasValue)
                    {
                        record.TimeSinceLastChange = record.Timestamp - lastChangeTime.Value;
                    }
                    lastChangeTime = record.Timestamp;

                    changeRecords.Add(record);
                }

                _logger.LogDebug("获取参数变化记录成功: {ExperimentId}, {Count} 条记录", experimentId, changeRecords.Count);
                return changeRecords;
            });
        }

        public async Task<List<ParameterChangeRecord>> GetParameterChangesForExportAsync(DateTime? startDate = null, DateTime? endDate = null)
        {
            return await _connectionFactory.ExecuteAsync(async database =>
            {
                var whereClause = "WHERE edd.data_direction = 'OUTPUT'";
                var parameters = new List<object>();

                if (startDate.HasValue)
                {
                    whereClause += $" AND er.start_time >= @{parameters.Count}";
                    parameters.Add(startDate.Value);
                }

                if (endDate.HasValue)
                {
                    whereClause += $" AND er.start_time <= @{parameters.Count}";
                    parameters.Add(endDate.Value);
                }

                var sql = $@"
                    WITH parameter_changes AS (
                        SELECT 
                            edd.*,
                            er.experiment_name,
                            LAG(edd.parameter_value) OVER (
                                PARTITION BY edd.experiment_id, edd.device_id, edd.parameter_name 
                                ORDER BY edd.timestamp
                            ) as previous_value,
                            ROW_NUMBER() OVER (
                                PARTITION BY edd.experiment_id, edd.device_id, edd.parameter_name 
                                ORDER BY edd.timestamp
                            ) as row_num
                        FROM experiment_device_data edd
                        INNER JOIN experiment_records er ON edd.experiment_id = er.experiment_id
                        {whereClause}
                        ORDER BY edd.experiment_id, edd.device_id, edd.parameter_name, edd.timestamp
                    )
                    SELECT 
                        experiment_id as ExperimentId,
                        experiment_name as ExperimentName,
                        device_id as DeviceId,
                        device_name as DeviceName,
                        parameter_name as ParameterName,
                        parameter_value as ParameterValue,
                        previous_value as PreviousValue,
                        unit as Unit,
                        timestamp as Timestamp,
                        command_name as CommandName,
                        node_name as NodeName,
                        row_num as ChangeSequence,
                        CASE WHEN row_num = 1 THEN 1 ELSE 0 END as IsFirstRecord,
                        CASE WHEN row_num = 1 OR parameter_value != COALESCE(previous_value, '') THEN 1 ELSE 0 END as IsValueChanged
                    FROM parameter_changes
                    WHERE row_num = 1 OR parameter_value != COALESCE(previous_value, '')
                    ORDER BY experiment_id, device_id, parameter_name, timestamp";

                var results = await database.FetchAsync<dynamic>(sql, parameters.ToArray());

                var changeRecords = results.Select(result => new ParameterChangeRecord
                {
                    ExperimentId = result.ExperimentId?.ToString() ?? "",
                    ExperimentName = result.ExperimentName?.ToString() ?? "",
                    DeviceId = result.DeviceId?.ToString() ?? "",
                    DeviceName = result.DeviceName?.ToString() ?? "",
                    ParameterName = result.ParameterName?.ToString() ?? "",
                    ParameterValue = result.ParameterValue?.ToString() ?? "",
                    PreviousValue = result.PreviousValue?.ToString(),
                    Unit = result.Unit?.ToString(),
                    Timestamp = Convert.ToDateTime(result.Timestamp),
                    CommandName = result.CommandName?.ToString(),
                    NodeName = result.NodeName?.ToString(),
                    ChangeSequence = Convert.ToInt32(result.ChangeSequence),
                    IsFirstRecord = Convert.ToBoolean(result.IsFirstRecord),
                    IsValueChanged = Convert.ToBoolean(result.IsValueChanged)
                }).ToList();

                _logger.LogDebug("获取批量参数变化记录成功: {Count} 条记录", changeRecords.Count);
                return changeRecords;
            });
        }

        public async Task AddVariableAsync(ExperimentVariable variable)
        {
            await _connectionFactory.ExecuteAsync(async database =>
            {
                variable.CreatedTime = DateTime.Now;
                await database.InsertAsync(variable);
                _logger.LogDebug("添加变量记录成功: {ExperimentId}", variable.ExperimentId);
            });
        }

        public async Task<List<ExperimentVariable>> GetVariablesAsync(string experimentId)
        {
            return await _connectionFactory.ExecuteAsync(async database =>
            {
                return await database.FetchAsync<ExperimentVariable>(
                    "WHERE experiment_id = @0 ORDER BY timestamp", experimentId);
            });
        }

        public async Task<ExperimentStatistics> GetStatisticsAsync(DateTime? startDate = null, DateTime? endDate = null)
        {
            return await _connectionFactory.ExecuteAsync(async database =>
            {
                var whereClause = "WHERE 1=1";
                var parameters = new List<object>();

                if (startDate.HasValue)
                {
                    whereClause += $" AND start_time >= @{parameters.Count}";
                    parameters.Add(startDate.Value);
                }

                if (endDate.HasValue)
                {
                    whereClause += $" AND start_time <= @{parameters.Count}";
                    parameters.Add(endDate.Value);
                }

                var sql = $@"
                    SELECT 
                        COUNT(*) as TotalExperiments,
                        SUM(CASE WHEN is_successful = 1 THEN 1 ELSE 0 END) as SuccessfulExperiments,
                        SUM(CASE WHEN simulation_mode = 1 THEN 1 ELSE 0 END) as SimulationExperiments,
                        COALESCE(AVG(CASE WHEN duration_seconds IS NOT NULL THEN duration_seconds END), 0) as AverageDuration,
                        COALESCE(MAX(duration_seconds), 0) as MaxDuration,
                        COALESCE(MIN(duration_seconds), 0) as MinDuration
                    FROM experiment_records {whereClause}";

                var result = await database.FetchAsync<dynamic>(sql, parameters.ToArray());
                var stats = result.FirstOrDefault();

                if (stats == null)
                {
                    return new ExperimentStatistics
                    {
                        StartDate = startDate ?? DateTime.MinValue,
                        EndDate = endDate ?? DateTime.MaxValue
                    };
                }

                var totalExperiments = Convert.ToInt32(stats.TotalExperiments ?? 0);
                var successfulExperiments = Convert.ToInt32(stats.SuccessfulExperiments ?? 0);

                return new ExperimentStatistics
                {
                    TotalExperiments = totalExperiments,
                    SuccessfulExperiments = successfulExperiments,
                    SimulationExperiments = Convert.ToInt32(stats.SimulationExperiments ?? 0),
                    AverageDurationSeconds = Convert.ToDouble(stats.AverageDuration ?? 0),
                    MaxDurationSeconds = Convert.ToDouble(stats.MaxDuration ?? 0),
                    MinDurationSeconds = Convert.ToDouble(stats.MinDuration ?? 0),
                    SuccessRate = totalExperiments > 0 ? (double)successfulExperiments / totalExperiments * 100 : 0,
                    StartDate = startDate ?? DateTime.MinValue,
                    EndDate = endDate ?? DateTime.MaxValue
                };
            });
        }

        public async Task<List<ExperimentTrendData>> GetTrendDataAsync(DateTime startDate, DateTime endDate, string interval = "day")
        {
            return await _connectionFactory.ExecuteAsync(async database =>
            {
                string sql;

                switch (interval.ToLower())
                {
                    case "hour":
                        sql = @"
                SELECT 
                    DATE_FORMAT(start_time, '%Y-%m-%d %H:00:00') as TimeGroup,
                    DATE_FORMAT(MIN(start_time), '%Y-%m-%d %H:00:00') as TimeLabel,
                    MIN(start_time) as Date,
                    COUNT(*) as TotalCount,
                    SUM(CASE WHEN is_successful = 1 THEN 1 ELSE 0 END) as SuccessCount,
                    SUM(CASE WHEN simulation_mode = 1 THEN 1 ELSE 0 END) as SimulationCount,
                    COALESCE(AVG(CASE WHEN duration_seconds IS NOT NULL THEN duration_seconds END), 0) as AverageDuration
                FROM experiment_records 
                WHERE start_time >= @0 AND start_time <= @1
                GROUP BY DATE_FORMAT(start_time, '%Y-%m-%d %H:00:00')
                ORDER BY DATE_FORMAT(start_time, '%Y-%m-%d %H:00:00')";
                        break;

                    case "day":
                        sql = @"
                SELECT 
                    DATE(start_time) as TimeGroup,
                    DATE_FORMAT(MIN(start_time), '%Y-%m-%d') as TimeLabel,
                    MIN(start_time) as Date,
                    COUNT(*) as TotalCount,
                    SUM(CASE WHEN is_successful = 1 THEN 1 ELSE 0 END) as SuccessCount,
                    SUM(CASE WHEN simulation_mode = 1 THEN 1 ELSE 0 END) as SimulationCount,
                    COALESCE(AVG(CASE WHEN duration_seconds IS NOT NULL THEN duration_seconds END), 0) as AverageDuration
                FROM experiment_records 
                WHERE start_time >= @0 AND start_time <= @1
                GROUP BY DATE(start_time)
                ORDER BY DATE(start_time)";
                        break;

                    case "week":
                        sql = @"
                SELECT 
                    YEARWEEK(start_time, 1) as TimeGroup,
                    CONCAT(YEAR(MIN(start_time)), '年第', WEEK(MIN(start_time), 1), '周') as TimeLabel,
                    MIN(start_time) as Date,
                    COUNT(*) as TotalCount,
                    SUM(CASE WHEN is_successful = 1 THEN 1 ELSE 0 END) as SuccessCount,
                    SUM(CASE WHEN simulation_mode = 1 THEN 1 ELSE 0 END) as SimulationCount,
                    COALESCE(AVG(CASE WHEN duration_seconds IS NOT NULL THEN duration_seconds END), 0) as AverageDuration
                FROM experiment_records 
                WHERE start_time >= @0 AND start_time <= @1
                GROUP BY YEARWEEK(start_time, 1)
                ORDER BY YEARWEEK(start_time, 1)";
                        break;

                    case "month":
                        sql = @"
                SELECT 
                    DATE_FORMAT(start_time, '%Y-%m') as TimeGroup,
                    DATE_FORMAT(MIN(start_time), '%Y年%m月') as TimeLabel,
                    MIN(start_time) as Date,
                    COUNT(*) as TotalCount,
                    SUM(CASE WHEN is_successful = 1 THEN 1 ELSE 0 END) as SuccessCount,
                    SUM(CASE WHEN simulation_mode = 1 THEN 1 ELSE 0 END) as SimulationCount,
                    COALESCE(AVG(CASE WHEN duration_seconds IS NOT NULL THEN duration_seconds END), 0) as AverageDuration
                FROM experiment_records 
                WHERE start_time >= @0 AND start_time <= @1
                GROUP BY DATE_FORMAT(start_time, '%Y-%m')
                ORDER BY DATE_FORMAT(start_time, '%Y-%m')";
                        break;

                    default:
                        sql = @"
                SELECT 
                    DATE(start_time) as TimeGroup,
                    DATE_FORMAT(MIN(start_time), '%Y-%m-%d') as TimeLabel,
                    MIN(start_time) as Date,
                    COUNT(*) as TotalCount,
                    SUM(CASE WHEN is_successful = 1 THEN 1 ELSE 0 END) as SuccessCount,
                    SUM(CASE WHEN simulation_mode = 1 THEN 1 ELSE 0 END) as SimulationCount,
                    COALESCE(AVG(CASE WHEN duration_seconds IS NOT NULL THEN duration_seconds END), 0) as AverageDuration
                FROM experiment_records 
                WHERE start_time >= @0 AND start_time <= @1
                GROUP BY DATE(start_time)
                ORDER BY DATE(start_time)";
                        break;
                }

                var results = await database.FetchAsync<dynamic>(sql, startDate, endDate);

                return results.Select(r =>
                {
                    var totalCount = Convert.ToInt32(r.TotalCount);
                    var successCount = Convert.ToInt32(r.SuccessCount);

                    return new ExperimentTrendData
                    {
                        TimeLabel = r.TimeLabel?.ToString() ?? "",
                        Date = Convert.ToDateTime(r.Date),
                        TotalCount = totalCount,
                        SuccessCount = successCount,
                        SimulationCount = Convert.ToInt32(r.SimulationCount),
                        AverageDuration = Convert.ToDouble(r.AverageDuration ?? 0),
                        SuccessRate = totalCount > 0 ? (double)successCount / totalCount * 100 : 0
                    };
                }).ToList();
            });
        }

        public async Task<List<DeviceUsageStatistics>> GetDeviceUsageStatisticsAsync(DateTime? startDate = null, DateTime? endDate = null)
        {
            return await _connectionFactory.ExecuteAsync(async database =>
            {
                var whereClause = "WHERE enr.device_id IS NOT NULL";
                var parameters = new List<object>();

                if (startDate.HasValue)
                {
                    whereClause += $" AND er.start_time >= @{parameters.Count}";
                    parameters.Add(startDate.Value);
                }

                if (endDate.HasValue)
                {
                    whereClause += $" AND er.start_time <= @{parameters.Count}";
                    parameters.Add(endDate.Value);
                }

                var sql = $@"
                    SELECT 
                        enr.device_id as DeviceId,
                        enr.device_name as DeviceName,
                        COUNT(*) as UsageCount,
                        SUM(CASE WHEN enr.is_successful = 1 THEN 1 ELSE 0 END) as SuccessCount,
                        COALESCE(AVG(CASE WHEN enr.duration_ms IS NOT NULL THEN enr.duration_ms END), 0) as AverageDurationMs
                    FROM experiment_node_records enr
                    INNER JOIN experiment_records er ON enr.experiment_id = er.experiment_id
                    {whereClause}
                    GROUP BY enr.device_id, enr.device_name
                    ORDER BY UsageCount DESC";

                var results = await database.FetchAsync<dynamic>(sql, parameters.ToArray());

                return results.Select(r =>
                {
                    var usageCount = Convert.ToInt32(r.UsageCount);
                    var successCount = Convert.ToInt32(r.SuccessCount);

                    return new DeviceUsageStatistics
                    {
                        DeviceId = r.DeviceId?.ToString() ?? "",
                        DeviceName = r.DeviceName?.ToString() ?? "",
                        UsageCount = usageCount,
                        SuccessCount = successCount,
                        AverageDurationMs = Convert.ToDouble(r.AverageDurationMs ?? 0),
                        SuccessRate = usageCount > 0 ? (double)successCount / usageCount * 100 : 0
                    };
                }).ToList();
            });
        }

        public async Task<List<ExperimentRecord>> GetExperimentsForExportAsync(DateTime? startDate = null, DateTime? endDate = null)
        {
            return await _connectionFactory.ExecuteAsync(async database =>
            {
                var whereClause = "WHERE 1=1";
                var parameters = new List<object>();

                if (startDate.HasValue)
                {
                    whereClause += $" AND start_time >= @{parameters.Count}";
                    parameters.Add(startDate.Value);
                }

                if (endDate.HasValue)
                {
                    whereClause += $" AND start_time <= @{parameters.Count}";
                    parameters.Add(endDate.Value);
                }

                var sql = $@"
                    SELECT * FROM experiment_records 
                    {whereClause} 
                    ORDER BY start_time DESC";

                var experiments = await database.FetchAsync<ExperimentRecord>(sql, parameters.ToArray());

                // 为每个实验加载节点记录
                foreach (var experiment in experiments)
                {
                    experiment.NodeRecords = await GetNodeRecordsAsync(experiment.ExperimentId);
                }

                return experiments;
            });
        }

        public async Task DeleteExperimentAsync(string experimentId)
        {
            await _connectionFactory.ExecuteAsync(async database =>
            {
                using var transaction = database.GetTransaction();
                try
                {
                    await database.DeleteAsync<ExperimentDeviceData>("WHERE experiment_id = @0", experimentId);
                    await database.DeleteAsync<ExperimentVariable>("WHERE experiment_id = @0", experimentId);
                    await database.DeleteAsync<ExperimentNodeRecord>("WHERE experiment_id = @0", experimentId);
                    await database.DeleteAsync<ExperimentRecord>("WHERE experiment_id = @0", experimentId);

                    transaction.Complete();
                    _logger.LogInformation("删除实验记录成功: {ExperimentId}", experimentId);
                }
                catch
                {
                    // 事务会自动回滚
                    throw;
                }
            });
        }
    }
}
// MaxChemical.Data/Models/ExperimentModels.cs
using PetaPoco;
using System;
using System.Collections.Generic;
using System.Linq;

namespace MaxChemical.Data.Models
{
    [TableName("experiment_records")]
    [PrimaryKey("Id")]
    public class ExperimentRecord
    {
        public int Id { get; set; }

        [Column("experiment_id")]
        public string ExperimentId { get; set; } = string.Empty;

        [Column("experiment_name")]
        public string ExperimentName { get; set; } = string.Empty;

        [Column("flow_name")]
        public string FlowName { get; set; } = string.Empty;

        [Column("start_time")]
        public DateTime StartTime { get; set; }

        [Column("end_time")]
        public DateTime? EndTime { get; set; }

        [Column("duration_seconds")]
        public double? DurationSeconds { get; set; }

        [Column("is_successful")]
        public bool IsSuccessful { get; set; }

        [Column("total_nodes")]
        public int TotalNodes { get; set; }

        [Column("completed_nodes")]
        public int CompletedNodes { get; set; }

        [Column("error_message")]
        public string? ErrorMessage { get; set; }

        [Column("simulation_mode")]
        public bool IsSimulationMode { get; set; }

        [Column("operator_name")]
        public string? OperatorName { get; set; }

        [Column("description")]
        public string? Description { get; set; }

        [Column("tags")]
        public string? Tags { get; set; }

        [Column("created_time")]
        public DateTime CreatedTime { get; set; }

        [Column("updated_time")]
        public DateTime UpdatedTime { get; set; }

        // 导航属性
        [Ignore]
        public List<ExperimentNodeRecord> NodeRecords { get; set; } = new();

        [Ignore]
        public TimeSpan? Duration => DurationSeconds.HasValue ? TimeSpan.FromSeconds(DurationSeconds.Value) : null;

        [Ignore]
        public string StatusText => IsSuccessful ? "成功" : "失败";

        [Ignore]
        public string ModeText => IsSimulationMode ? "仿真" : "实际";

        [Ignore]
        public double ProgressPercentage => TotalNodes > 0 ? (double)CompletedNodes / TotalNodes * 100 : 0;
    }

    [TableName("experiment_node_records")]
    [PrimaryKey("Id")]
    public class ExperimentNodeRecord
    {
        public int Id { get; set; }

        [Column("experiment_id")]
        public string ExperimentId { get; set; } = string.Empty;

        [Column("node_id")]
        public string NodeId { get; set; } = string.Empty;

        [Column("node_name")]
        public string NodeName { get; set; } = string.Empty;

        [Column("node_type")]
        public string NodeType { get; set; } = string.Empty;

        [Column("device_id")]
        public string? DeviceId { get; set; }

        [Column("device_name")]
        public string? DeviceName { get; set; }

        [Column("command_name")]
        public string? CommandName { get; set; }

        [Column("start_time")]
        public DateTime StartTime { get; set; }

        [Column("end_time")]
        public DateTime? EndTime { get; set; }

        [Column("duration_ms")]
        public double? DurationMs { get; set; }

        [Column("is_successful")]
        public bool IsSuccessful { get; set; }

        [Column("error_message")]
        public string? ErrorMessage { get; set; }

        [Column("input_parameters")]
        public string? InputParameters { get; set; }

        [Column("output_parameters")]
        public string? OutputParameters { get; set; }

        [Column("execution_order")]
        public int ExecutionOrder { get; set; }

        [Column("created_time")]
        public DateTime CreatedTime { get; set; }

        [Ignore]
        public TimeSpan? Duration => DurationMs.HasValue ? TimeSpan.FromMilliseconds(DurationMs.Value) : null;

        [Ignore]
        public string StatusText => IsSuccessful ? "成功" : "失败";
    }

    [TableName("experiment_device_data")]
    [PrimaryKey("Id")]
    public class ExperimentDeviceData
    {
        public int Id { get; set; }

        [Column("experiment_id")]
        public string ExperimentId { get; set; } = string.Empty;

        [Column("device_id")]
        public string DeviceId { get; set; } = string.Empty;

        [Column("device_name")]
        public string DeviceName { get; set; } = string.Empty;

        [Column("parameter_name")]
        public string ParameterName { get; set; } = string.Empty;

        [Column("parameter_value")]
        public string ParameterValue { get; set; } = string.Empty;

        [Column("parameter_type")]
        public string ParameterType { get; set; } = string.Empty;

        [Column("unit")]
        public string? Unit { get; set; }

        [Column("timestamp")]
        public DateTime Timestamp { get; set; }

        [Column("command_name")]
        public string? CommandName { get; set; }

        [Column("node_id")]
        public string? NodeId { get; set; }

        [Column("node_name")]
        public string? NodeName { get; set; }

        [Column("data_direction")]
        public string DataDirection { get; set; } = "OUTPUT";

        [Column("created_time")]
        public DateTime CreatedTime { get; set; }
    }

    [TableName("experiment_variables")]
    [PrimaryKey("Id")]
    public class ExperimentVariable
    {
        public int Id { get; set; }

        [Column("experiment_id")]
        public string ExperimentId { get; set; } = string.Empty;

        [Column("variable_name")]
        public string VariableName { get; set; } = string.Empty;

        [Column("variable_value")]
        public string VariableValue { get; set; } = string.Empty;

        [Column("variable_type")]
        public string VariableType { get; set; } = string.Empty;

        [Column("source_node_id")]
        public string? SourceNodeId { get; set; }

        [Column("source_device_id")]
        public string? SourceDeviceId { get; set; }

        [Column("timestamp")]
        public DateTime Timestamp { get; set; }

        [Column("created_time")]
        public DateTime CreatedTime { get; set; }
    }

    // 导出用的数据模型
    public class ParameterChangeRecord
    {
        public string ExperimentId { get; set; } = string.Empty;
        public string ExperimentName { get; set; } = string.Empty;
        public string DeviceId { get; set; } = string.Empty;
        public string DeviceName { get; set; } = string.Empty;
        public string ParameterName { get; set; } = string.Empty;
        public string ParameterValue { get; set; } = string.Empty;
        public string? PreviousValue { get; set; }
        public string? Unit { get; set; }
        public DateTime Timestamp { get; set; }
        public string? CommandName { get; set; }
        public string? NodeName { get; set; }
        public int ChangeSequence { get; set; }
        public TimeSpan? TimeSinceLastChange { get; set; }
        public bool IsFirstRecord { get; set; }
        public bool IsValueChanged { get; set; }
    }

    public class ExperimentStatistics
    {
        public int TotalExperiments { get; set; }
        public int SuccessfulExperiments { get; set; }
        public int FailedExperiments => TotalExperiments - SuccessfulExperiments;
        public int SimulationExperiments { get; set; }
        public int RealExperiments => TotalExperiments - SimulationExperiments;
        public double SuccessRate { get; set; }
        public double AverageDurationSeconds { get; set; }
        public double MaxDurationSeconds { get; set; }
        public double MinDurationSeconds { get; set; }
        public TimeSpan AverageDuration => TimeSpan.FromSeconds(AverageDurationSeconds);
        public DateTime StartDate { get; set; }
        public DateTime EndDate { get; set; }
    }

    public class ExperimentTrendData
    {
        public string TimeLabel { get; set; } = string.Empty;
        public DateTime Date { get; set; }
        public int TotalCount { get; set; }
        public int SuccessCount { get; set; }
        public int FailedCount => TotalCount - SuccessCount;
        public int SimulationCount { get; set; }
        public int RealCount => TotalCount - SimulationCount;
        public double SuccessRate { get; set; }
        public double AverageDuration { get; set; }
    }

    public class DeviceUsageStatistics
    {
        public string DeviceId { get; set; } = string.Empty;
        public string DeviceName { get; set; } = string.Empty;
        public int UsageCount { get; set; }
        public int SuccessCount { get; set; }
        public int FailedCount => UsageCount - SuccessCount;
        public double SuccessRate { get; set; }
        public double AverageDurationMs { get; set; }
        public TimeSpan AverageDuration => TimeSpan.FromMilliseconds(AverageDurationMs);
    }
}


using System.Text.Json;
using System.Text.Json.Serialization;

namespace MaxChemical.Shell.Services.Agent.Experiment
{
    /// <summary>实验准备的生命周期；只有 AwaitingConfirmation 和 Failed 能进入执行阶段。</summary>
    public enum ExperimentPreparationStatus
    {
        NeedsInformation,
        AwaitingConfirmation,
        Running,
        Failed,
        Completed
    }

    public enum ExperimentDeviceConnectionState
    {
        Pending,
        Checking,
        Connected,
        Simulation,
        DisplayOnly,
        Failed,
        Skipped
    }

    /// <summary>
    /// 一台计划设备。LibraryName 永远是左侧设备库名称；CanvasDisplayName 只记录真实画布映射，
    /// 两者不得互相覆盖，否则重试时无法再到设备库定位原始蓝图。
    /// </summary>
    public sealed class ExperimentPlanDevice
    {
        public string LibraryName { get; internal set; } = "";
        public string DeviceType { get; internal set; } = "";
        public string Role { get; internal set; } = "";
        public bool Required { get; internal set; } = true;
        public string DeviceId { get; internal set; } = "";
        public string CanvasDisplayName { get; internal set; } = "";
        public string PlacementMessage { get; internal set; } = "";
        public ExperimentDeviceConnectionState ConnectionState { get; internal set; } = ExperimentDeviceConnectionState.Pending;
        public string ConnectionMessage { get; internal set; } = "";
        public DateTime? LastConnectionCheckUtc { get; internal set; }

        internal ExperimentPlanDevice DeepClone() => new()
        {
            LibraryName = LibraryName,
            DeviceType = DeviceType,
            Role = Role,
            Required = Required,
            DeviceId = DeviceId,
            CanvasDisplayName = CanvasDisplayName,
            PlacementMessage = PlacementMessage,
            ConnectionState = ConnectionState,
            ConnectionMessage = ConnectionMessage,
            LastConnectionCheckUtc = LastConnectionCheckUtc
        };
    }

    /// <summary>当前 BuildFlowTool 支持的一种顺序步骤：设备命令或固定等待。</summary>
    public sealed class ExperimentPlanStep
    {
        public string Type { get; internal set; } = "";
        public string DeviceLibraryName { get; internal set; } = "";
        public string CommandName { get; internal set; } = "";
        public Dictionary<string, JsonElement> Parameters { get; internal set; } = new();
        public double WaitSeconds { get; internal set; }
        public string Description { get; internal set; } = "";

        internal ExperimentPlanStep DeepClone() => new()
        {
            Type = Type,
            DeviceLibraryName = DeviceLibraryName,
            CommandName = CommandName,
            Parameters = Parameters.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.Clone(),
                StringComparer.Ordinal),
            WaitSeconds = WaitSeconds,
            Description = Description
        };
    }

    /// <summary>
    /// 内存中的实验准备计划。协调器通过 DeepClone 对外发快照，调用方不能改写正在执行的计划。
    /// </summary>
    public sealed class ExperimentPreparationPlan
    {
        public string PreparationId { get; internal set; } = "";
        public string ExperimentDescription { get; internal set; } = "";
        public string PublicSearchQuery { get; internal set; } = "";
        public string ExperimentSummary { get; internal set; } = "";
        public string FlowName { get; internal set; } = "";
        public List<ExperimentPlanDevice> Devices { get; internal set; } = new();
        public List<ExperimentPlanStep> Steps { get; internal set; } = new();
        public List<string> MissingInformation { get; internal set; } = new();
        public List<string> SafetyNotes { get; internal set; } = new();
        public ExperimentPreparationStatus Status { get; internal set; } = ExperimentPreparationStatus.NeedsInformation;
        public string LastError { get; internal set; } = "";
        public string Progress { get; internal set; } = "";
        public string ResearchText { get; internal set; } = "";
        public string WebResearchText { get; internal set; } = "";
        public string KnowledgeResearchText { get; internal set; } = "";
        public string WebResearchStatus { get; internal set; } = "";
        public string KnowledgeResearchStatus { get; internal set; } = "";
        public List<string> PartialFailures { get; internal set; } = new();
        public string SavedPath { get; internal set; } = "";
        public bool FlowBuilt { get; internal set; }
        internal string FlowBuildSignature { get; set; } = "";
        public DateTime CreatedAtUtc { get; internal set; }
        public DateTime UpdatedAtUtc { get; internal set; }
        public DateTime? StartedAtUtc { get; internal set; }
        public DateTime? CompletedAtUtc { get; internal set; }

        internal ExperimentPreparationPlan DeepClone() => new()
        {
            PreparationId = PreparationId,
            ExperimentDescription = ExperimentDescription,
            PublicSearchQuery = PublicSearchQuery,
            ExperimentSummary = ExperimentSummary,
            FlowName = FlowName,
            Devices = Devices.Select(device => device.DeepClone()).ToList(),
            Steps = Steps.Select(step => step.DeepClone()).ToList(),
            MissingInformation = new List<string>(MissingInformation),
            SafetyNotes = new List<string>(SafetyNotes),
            Status = Status,
            LastError = LastError,
            Progress = Progress,
            ResearchText = ResearchText,
            WebResearchText = WebResearchText,
            KnowledgeResearchText = KnowledgeResearchText,
            WebResearchStatus = WebResearchStatus,
            KnowledgeResearchStatus = KnowledgeResearchStatus,
            PartialFailures = new List<string>(PartialFailures),
            SavedPath = SavedPath,
            FlowBuilt = FlowBuilt,
            FlowBuildSignature = FlowBuildSignature,
            CreatedAtUtc = CreatedAtUtc,
            UpdatedAtUtc = UpdatedAtUtc,
            StartedAtUtc = StartedAtUtc,
            CompletedAtUtc = CompletedAtUtc
        };
    }

    public sealed class ExperimentPlanCreationResult
    {
        public bool Success { get; internal set; }
        public string PreparationId { get; internal set; } = "";
        public ExperimentPreparationStatus Status { get; internal set; }
        public ExperimentPreparationPlan Plan { get; internal set; }
        public string Message { get; internal set; } = "";
    }

    public sealed class ExperimentExecutionResult
    {
        public bool Success { get; internal set; }
        public string PreparationId { get; internal set; } = "";
        public ExperimentPreparationStatus Status { get; internal set; }
        public ExperimentPreparationPlan Plan { get; internal set; }
        public string SavedPath { get; internal set; } = "";
        public List<string> PartialFailures { get; internal set; } = new();
        public string Message { get; internal set; } = "";
    }

    internal sealed class ExperimentPlanDto
    {
        [JsonPropertyName("experiment_summary")]
        public string ExperimentSummary { get; set; } = "";

        [JsonPropertyName("flow_name")]
        public string FlowName { get; set; } = "";

        [JsonPropertyName("devices")]
        public List<ExperimentPlanDeviceDto> Devices { get; set; } = new();

        [JsonPropertyName("steps")]
        public List<ExperimentPlanStepDto> Steps { get; set; } = new();

        [JsonPropertyName("missing_information")]
        public List<string> MissingInformation { get; set; } = new();

        [JsonPropertyName("safety_notes")]
        public List<string> SafetyNotes { get; set; } = new();
    }

    internal sealed class ExperimentPlanDeviceDto
    {
        [JsonPropertyName("library_name")]
        public string LibraryName { get; set; } = "";

        [JsonPropertyName("role")]
        public string Role { get; set; } = "";

        [JsonPropertyName("required")]
        public bool Required { get; set; } = true;
    }

    internal sealed class ExperimentPlanStepDto
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = "";

        [JsonPropertyName("device_library_name")]
        public string DeviceLibraryName { get; set; } = "";

        [JsonPropertyName("command_name")]
        public string CommandName { get; set; } = "";

        [JsonPropertyName("parameters")]
        public Dictionary<string, JsonElement> Parameters { get; set; } = new();

        [JsonPropertyName("wait_seconds")]
        public double WaitSeconds { get; set; }

        [JsonPropertyName("description")]
        public string Description { get; set; } = "";
    }
}

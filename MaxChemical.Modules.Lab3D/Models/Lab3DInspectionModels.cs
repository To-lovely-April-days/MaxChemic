namespace MaxChemical.Modules.Lab3D.Models;

public sealed record Lab3DInspectionNotice(string Level, string Text, DateTime Timestamp)
{
    public string LevelText => Level switch { "warning" => "预警", "normal" => "正常", _ => "提示" };
    public string LevelTagText => Level switch { "warning" => "! 预警", "normal" => "✓ 正常", _ => "i 提示" };
    public string TimeText => Timestamp.ToString("HH:mm");
}

public sealed record Lab3DInspectionSnapshot(
    string? TargetId,
    string TargetName,
    string TargetStatus,
    string Task,
    int InspectionCount,
    int AlertCount,
    double? DataCoverage,
    double? ExperimentSuccessRate,
    int TargetExperimentCount,
    double? TargetSuccessRate,
    IReadOnlyList<Lab3DInspectionNotice> Notices,
    DateTime UpdatedAt)
{
    public static Lab3DInspectionSnapshot Empty { get; } = new(
        null, "暂无设备", "等待巡检", "等待设备目录", 0, 0, null, null, 0, null,
        [new Lab3DInspectionNotice("notice", "暂无设备，请先通过底部设备栏的 + 新增设备。", DateTime.Now)], DateTime.Now);

    // 实例初始化器（不能改成静态默认字段：声明在 Empty 之后的静态字段在构造 Empty 时仍为 null）。
    public IReadOnlyList<Lab3DInspectionSignal> Inputs { get; init; } = new[]
    {
        new Lab3DInspectionSignal("主参数", "--", Lab3DSignalState.Off),
        new Lab3DInspectionSignal("次参数", "--", Lab3DSignalState.Off),
        new Lab3DInspectionSignal("连接状态", "--", Lab3DSignalState.Off),
        new Lab3DInspectionSignal("实验记录", "--", Lab3DSignalState.Off)
    };
    public IReadOnlyList<Lab3DInspectionSignal> Outputs { get; init; } = new[]
    {
        new Lab3DInspectionSignal("数据完整", "--", Lab3DSignalState.Off),
        new Lab3DInspectionSignal("异常提示", "--", Lab3DSignalState.Off),
        new Lab3DInspectionSignal("趋势分析", "--", Lab3DSignalState.Off)
    };
    public int RotationIndex { get; init; } = -1;
    public int RotationCount { get; init; }
    public Lab3DSignalState OverallState { get; init; }
    public string OverallStateKey => Lab3DSignalStates.ToKey(OverallState);
    public string RotationText => RotationIndex < 0 || RotationCount <= 0 ? "--" : $"{RotationIndex + 1} / {RotationCount}";
}

public sealed class Lab3DInspectionChangedEventArgs(Lab3DInspectionSnapshot snapshot) : EventArgs
{
    public Lab3DInspectionSnapshot Snapshot { get; } = snapshot;
}

public sealed record Lab3DParameterPoint(DateTime Timestamp, double Value);

public sealed record Lab3DTrendSeries(
    IReadOnlyList<Lab3DParameterPoint> Primary,
    IReadOnlyList<Lab3DParameterPoint> Secondary,
    string PrimaryRangeText,
    string SecondaryRangeText)
{
    public static Lab3DTrendSeries Empty { get; } = new([], [], "--", "--");
}

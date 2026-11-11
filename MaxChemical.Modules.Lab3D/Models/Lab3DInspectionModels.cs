namespace MaxChemical.Modules.Lab3D.Models;

public sealed record Lab3DInspectionNotice(string Level, string Text, DateTime Timestamp)
{
    public string LevelText => Level switch { "warning" => "预警", "normal" => "正常", _ => "提示" };
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
        [new Lab3DInspectionNotice("notice", "暂无设备，请先通过设备导航新增设备。", DateTime.Now)], DateTime.Now);
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

namespace MaxChemical.Modules.Lab3D.Models;

public sealed record Lab3DTrendItem(
    string Label,
    int Count,
    int SuccessCount,
    double Height,
    double SuccessHeight,
    double FailedHeight)
{
    public string ToolTipText => $"{Label} · 共 {Count} 次 · 成功 {SuccessCount} 次 · 失败 {Count - SuccessCount} 次";
}

public sealed record Lab3DHistoryItem(
    string Title,
    string Detail,
    string Status,
    bool IsActive,
    bool IsSuccessful,
    DateTime StartTime)
{
    public string TimeText => StartTime.ToString("MM-dd HH:mm");
}

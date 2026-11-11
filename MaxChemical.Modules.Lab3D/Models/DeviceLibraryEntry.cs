namespace MaxChemical.Modules.Lab3D.Models;

public sealed class DeviceLibraryEntry
{
    public string Id { get; set; } = string.Empty;
    public string LibraryId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string EnglishName { get; set; } = string.Empty;
    public string Kind { get; set; } = "Custom";
    public string ImagePath { get; set; } = string.Empty;
    public string ThumbnailPath { get; set; } = string.Empty;
    public string SourceImagePath { get; set; } = string.Empty;
    public string Location { get; set; } = "自定义展台";
    public string Description { get; set; } = string.Empty;
    public string Protocol { get; set; } = "Custom / Local";
    public string PrimaryName { get; set; } = "设备状态";
    public string PrimaryUnit { get; set; } = string.Empty;
    public string PrimaryValue { get; set; } = "本地展示";
    public string SecondaryName { get; set; } = "连接状态";
    public string SecondaryUnit { get; set; } = string.Empty;
    public string SecondaryValue { get; set; } = "未接入";
    public double ModelHeight { get; set; } = 2;
    public double ImageAspect { get; set; } = 1;
    public double FocusDistance { get; set; } = 6;
    public double[] Position { get; set; } = [-3.4, 0.88, 7.2];
    public bool IsOnline { get; set; }
}

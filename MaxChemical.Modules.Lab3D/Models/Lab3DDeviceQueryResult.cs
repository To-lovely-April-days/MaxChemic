namespace MaxChemical.Modules.Lab3D.Models;

public sealed record Lab3DDeviceQueryResult(
    string SlotId,
    string Name,
    string Protocol,
    bool IsCustom,
    string? SourceDeviceId,
    bool IsConnected,
    bool HasFreshData,
    Lab3DParameterSnapshot Primary,
    Lab3DParameterSnapshot Secondary,
    DateTime? LastUpdateTime,
    IReadOnlyList<Lab3DParameterSample> Samples);

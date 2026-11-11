namespace MaxChemical.Modules.Lab3D.Models;

public sealed record Lab3DParameterSample(
    string Name,
    string Value,
    string Unit,
    DateTime Timestamp,
    bool IsFresh);

using MaxChemical.Modules.Lab3D.Models;

namespace MaxChemical.Modules.Lab3D.Services;

public interface ILab3DDeviceQueryService
{
    IReadOnlyList<Lab3DDeviceQueryResult> GetDevices();
}

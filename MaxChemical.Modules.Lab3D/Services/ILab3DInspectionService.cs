using MaxChemical.Modules.Lab3D.Models;

namespace MaxChemical.Modules.Lab3D.Services;

public interface ILab3DInspectionService : IDisposable
{
    event EventHandler<Lab3DInspectionChangedEventArgs>? Changed;

    Lab3DInspectionSnapshot Current { get; }

    void SetDevices(IEnumerable<LabDevice> devices);

    void SelectDevice(LabDevice? device);

    Lab3DTrendSeries GetTrend(string slotId);

    void SetActive(bool active);
}

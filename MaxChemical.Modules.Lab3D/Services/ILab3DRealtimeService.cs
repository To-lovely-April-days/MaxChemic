using MaxChemical.Modules.Lab3D.Models;

namespace MaxChemical.Modules.Lab3D.Services;

public interface ILab3DRealtimeService : IDisposable
{
    event EventHandler<Lab3DRealtimeSnapshotEventArgs>? SnapshotChanged;

    IReadOnlyList<Lab3DDeviceSnapshot> GetSnapshots();

    IReadOnlyList<Lab3DParameterSample> GetLatestSamples(string sourceDeviceId);

    void SetDevices(IEnumerable<LabDevice> devices);

    void RefreshDeviceLibrary();

    void PublishSnapshot();
}

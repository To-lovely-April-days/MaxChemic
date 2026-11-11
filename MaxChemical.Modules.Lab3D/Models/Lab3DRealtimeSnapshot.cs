namespace MaxChemical.Modules.Lab3D.Models;

public sealed record Lab3DParameterSnapshot(string Name, string Unit, string Value);

public sealed record Lab3DDeviceSnapshot(
    string SlotId,
    string? SourceDeviceId,
    bool IsConnected,
    bool HasFreshData,
    string Protocol,
    Lab3DParameterSnapshot Primary,
    Lab3DParameterSnapshot Secondary,
    DateTime? LastUpdateTime);

public sealed class Lab3DRealtimeSnapshotEventArgs : EventArgs
{
    public Lab3DRealtimeSnapshotEventArgs(IReadOnlyList<Lab3DDeviceSnapshot> devices)
    {
        Devices = devices;
    }

    public IReadOnlyList<Lab3DDeviceSnapshot> Devices { get; }
}

namespace MaxChemical.Modules.Lab3D.Models;

public enum Lab3DSignalState { Off, Live, Stale, Alert }

public static class Lab3DSignalStates
{
    public static string ToKey(Lab3DSignalState state) => state switch
    {
        Lab3DSignalState.Live => "live",
        Lab3DSignalState.Stale => "stale",
        Lab3DSignalState.Alert => "alert",
        _ => "off"
    };
}

public sealed record Lab3DInspectionSignal(string Label, string Value, Lab3DSignalState State)
{
    public string StateKey => Lab3DSignalStates.ToKey(State);
}

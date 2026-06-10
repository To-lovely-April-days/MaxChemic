
using DevicePlugins.Devices;
using MaxChemical.Modules.Designer.ViewModels;
using Prism.Events;

namespace MaxChemical.Modules.Designer.Event
{
    public class CanvasDevicesChangedEvent : PubSubEvent<List<CanvasDeviceViewModel>>
    {
    }

    public class RequestCanvasDevicesEvent : PubSubEvent<System.Action<List<CanvasDeviceViewModel>>>
    {
    }

    public class DeviceCommandSelectedEvent : PubSubEvent<DeviceCommand>
    {
    }
}

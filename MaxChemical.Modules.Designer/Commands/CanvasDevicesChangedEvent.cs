using DevicePlugins.Devices;
using MaxChemical.Modules.Designer.ViewModels;
using Prism.Events;
using System.Collections.Generic;

namespace MaxChemical.Modules.Device.Events
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
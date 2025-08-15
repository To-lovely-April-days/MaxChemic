using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using MaxChemical.Modules.Designer.Event;
using MaxChemical.Modules.Designer.ViewModels;
using Prism.Events;

namespace MaxChemical.Modules.Designer.Service
{
    public interface IDeviceNameResolverService
    {
        /// <summary>
        /// 根据显示名称获取DeviceId（如 "泵#1" -> "guid-xxx"）
        /// </summary>
        string GetDeviceIdByDisplayName(string displayName);

        /// <summary>
        /// 根据DeviceId获取显示名称（如 "guid-xxx" -> "泵#1"）
        /// </summary>
        string GetDisplayNameByDeviceId(string deviceId);

        /// <summary>
        /// 获取所有设备的显示名称映射
        /// </summary>
        Dictionary<string, string> GetAllDisplayNames();
    }

    public class DeviceNameResolverService : IDeviceNameResolverService
    {
        private readonly IEventAggregator _eventAggregator;
        private Dictionary<string, string> _deviceDisplayNames = new Dictionary<string, string>();
        private List<CanvasDeviceViewModel> _canvasDevices = new List<CanvasDeviceViewModel>();

        public DeviceNameResolverService(IEventAggregator eventAggregator)
        {
            _eventAggregator = eventAggregator;
            RefreshDeviceData();
        }

        private void RefreshDeviceData()
        {
            // 获取显示名称映射
            _eventAggregator?.GetEvent<GetDeviceDisplayNamesRequestEvent>()?.Publish(names =>
            {
                _deviceDisplayNames = names ?? new Dictionary<string, string>();
            });

            // 获取画布设备列表
            _eventAggregator?.GetEvent<RequestCanvasDevicesEvent>()?.Publish(devices =>
            {
                _canvasDevices = devices ?? new List<CanvasDeviceViewModel>();
            });
        }

        public string GetDeviceIdByDisplayName(string displayName)
        {
            if (string.IsNullOrEmpty(displayName))
                return null;

            RefreshDeviceData();

            // 从显示名称映射中查找
            var deviceId = _deviceDisplayNames
                .FirstOrDefault(kvp => kvp.Value.Equals(displayName, StringComparison.OrdinalIgnoreCase))
                .Key;

            return deviceId;
        }

        public string GetDisplayNameByDeviceId(string deviceId)
        {
            if (string.IsNullOrEmpty(deviceId))
                return null;

            RefreshDeviceData();

            return _deviceDisplayNames.TryGetValue(deviceId, out var displayName)
                ? displayName
                : null;
        }

        public Dictionary<string, string> GetAllDisplayNames()
        {
            RefreshDeviceData();
            return new Dictionary<string, string>(_deviceDisplayNames);
        }
    }
}

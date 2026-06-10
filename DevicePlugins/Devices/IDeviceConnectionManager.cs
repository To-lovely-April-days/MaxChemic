using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using MaxChemical.Core;

namespace DevicePlugins.Devices
{
    public interface IDeviceConnectionManager
    {
        Task<bool> ConnectAsync(string deviceId);
        Task<bool> DisconnectAsync(string deviceId);
        Task<bool> CheckConnectionAsync(string deviceId);
        bool IsConnected(string deviceId);
        Task<T> ReadAsync<T>(string deviceId, string address);
        Task<bool> WriteAsync<T>(string deviceId, string address, T value);
    }
}

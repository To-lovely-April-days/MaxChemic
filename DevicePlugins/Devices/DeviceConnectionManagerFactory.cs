using MaxChemical.Core;
using System.Threading.Tasks;
using System;

namespace DevicePlugins.Devices
{
    public static class DeviceConnectionManagerFactory
    {
        private static IDeviceConnectionManager _instance;
        private static readonly object _lock = new object();

        public static IDeviceConnectionManager GetInstance()
        {
            if (_instance == null)
            {
                lock (_lock)
                {
                    if (_instance == null)
                    {
                        if (DeviceConnectionConfig.UsePLCMode)
                        {
                            _instance = PLCConnectionManager.Instance;
                        }
                        else
                        {
                            _instance = new DirectConnectionManager();
                        }
                    }
                }
            }
            return _instance;
        }

        public static void Reset()
        {
            lock (_lock)
            {
                if (_instance is IDisposable disposable)
                {
                    disposable.Dispose();
                }
                _instance = null;
            }
        }
    }

    public class DirectConnectionManager : IDeviceConnectionManager
    {
        public Task<bool> ConnectAsync(string deviceId) => Task.FromResult(true);
        public Task<bool> DisconnectAsync(string deviceId) => Task.FromResult(true);
        public Task<bool> CheckConnectionAsync(string deviceId) => Task.FromResult(true);
        public bool IsConnected(string deviceId) => true;
        public Task<T> ReadAsync<T>(string deviceId, string address) =>
            throw new NotSupportedException("直连模式不支持统一的读取操作");
        public Task<bool> WriteAsync<T>(string deviceId, string address, T value) =>
            throw new NotSupportedException("直连模式不支持统一的写入操作");
    }
}
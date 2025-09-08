// DeviceConnectionManagerFactory.cs
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
                        _instance = DeviceConnectionConfig.CommunicationMode switch
                        {
                            CommunicationMode.PLC => PLCConnectionManager.Instance,
                            CommunicationMode.ModbusTcp => ModbusTcpConnectionManager.Instance,
                            //CommunicationMode.Direct => new DirectConnectionManager(),
                            //_ => new DirectConnectionManager()
                        };
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

        /// <summary>
        /// 切换通信模式
        /// </summary>
        public static void SwitchCommunicationMode(CommunicationMode newMode)
        {
            if (DeviceConnectionConfig.CommunicationMode != newMode)
            {
                DeviceConnectionConfig.CommunicationMode = newMode;
                Reset(); // 重置实例，下次获取时会创建新的管理器
            }
        }
    }
}
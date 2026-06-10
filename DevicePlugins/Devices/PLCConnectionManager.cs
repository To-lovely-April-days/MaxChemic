using S7.Net;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Linq; // 添加这个
using MaxChemical.Logging;
using MaxChemical.Core;
using S7.Net.Types;

namespace DevicePlugins.Devices
{
    public class PLCConnectionManager : IDeviceConnectionManager, IDisposable
    {
        private static PLCConnectionManager _instance;
        private static readonly object _lock = new object();

        public static PLCConnectionManager Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock)
                    {
                        _instance ??= new PLCConnectionManager();
                    }
                }
                return _instance;
            }
        }

        private readonly ILogService _logger;
        private readonly SemaphoreSlim _plcLock = new SemaphoreSlim(1, 1);
        private readonly Dictionary<string, DeviceConnectionStatus> _deviceStatuses;
        private readonly Dictionary<string, PLCDeviceMapping> _deviceMappings;

        private Plc _plc;
        private bool _isConnected = false;

        public PLCConnectionManager()
        {
            _logger = LogManager.GetLogger<PLCConnectionManager>();
            _deviceStatuses = new Dictionary<string, DeviceConnectionStatus>();
            _deviceMappings = new Dictionary<string, PLCDeviceMapping>();

            InitializePLC();
            LoadDeviceMappings();
        }

        private void InitializePLC()
        {
            try
            {
                var settings = DeviceConnectionConfig.PLCSettings;

                CpuType cpuType = settings.ConnectionType.ToUpper() switch
                {
                    "S7-1200" => CpuType.S71200,
                    "S7-1500" => CpuType.S71500,
                    "S7-300" => CpuType.S7300,
                    "S7-400" => CpuType.S7400,
                    _ => CpuType.S71500
                };

                _plc = new Plc(cpuType, settings.IpAddress, (short)settings.Rack, (short)settings.Slot);

                _logger.LogInformation("PLC客户端初始化完成: {Type} {IP}:{Rack}:{Slot}",
                    cpuType, settings.IpAddress, settings.Rack, settings.Slot);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "初始化PLC客户端失败");
            }
        }

        public async Task<bool> ConnectAsync(string deviceId)
        {
            await _plcLock.WaitAsync();
            try
            {
                if (!_isConnected && _plc != null)
                {
                    await _plc.OpenAsync();

                    if (_plc.IsConnected)
                    {
                        _isConnected = true;
                        _logger.LogInformation("PLC连接成功: {IP}", _plc.IP);
                    }
                    else
                    {
                        _logger.LogError("PLC连接失败: {IP}", _plc.IP);
                        return false;
                    }
                }

                _deviceStatuses[deviceId] = DeviceConnectionStatus.Connected;
                _logger.LogInformation("设备通过PLC连接成功: {DeviceId}", deviceId);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "PLC设备连接失败: {DeviceId}", deviceId);
                return false;
            }
            finally
            {
                _plcLock.Release();
            }
        }

        public async Task<bool> DisconnectAsync(string deviceId)
        {
            await _plcLock.WaitAsync();
            try
            {
                _deviceStatuses[deviceId] = DeviceConnectionStatus.Disconnected;
                _logger.LogInformation("设备已断开PLC连接: {DeviceId}", deviceId);
                return true;
            }
            finally
            {
                _plcLock.Release();
            }
        }

        public async Task<bool> CheckConnectionAsync(string deviceId)
        {
            await _plcLock.WaitAsync();
            try
            {
                if (!_isConnected || _plc == null)
                    return false;

                return _plc.IsConnected;
            }
            catch
            {
                return false;
            }
            finally
            {
                _plcLock.Release();
            }
        }

        public bool IsConnected(string deviceId)
        {
            return _deviceStatuses.GetValueOrDefault(deviceId, DeviceConnectionStatus.Disconnected)
                   == DeviceConnectionStatus.Connected && _isConnected;
        }

        public async Task<T> ReadAsync<T>(string deviceId, string address)
        {
            await _plcLock.WaitAsync();
            try
            {
                if (!_isConnected || _plc == null)
                    throw new InvalidOperationException("PLC未连接");

                if (!_deviceMappings.TryGetValue(deviceId, out var mapping))
                {
                    throw new InvalidOperationException($"设备 {deviceId} 没有配置PLC地址映射");
                }

                if (!mapping.CommandAddresses.TryGetValue(address, out var plcAddress))
                {
                    throw new InvalidOperationException($"设备 {deviceId} 的命令 {address} 没有配置PLC地址");
                }

                var addressInfo = ParseAddress(plcAddress);

                object result = typeof(T) switch
                {
                    Type t when t == typeof(bool) =>
                        ReadBool(addressInfo),

                    Type t when t == typeof(short) =>
                        ReadInt16(addressInfo),

                    Type t when t == typeof(int) =>
                        ReadInt32(addressInfo),

                    Type t when t == typeof(float) =>
                        ReadFloat(addressInfo),

                    Type t when t == typeof(double) =>
                        ReadDouble(addressInfo),

                    _ => throw new NotSupportedException($"不支持的数据类型: {typeof(T)}")
                };

                _logger.LogDebug("PLC读取成功: {DeviceId} {Address} -> {Value}", deviceId, plcAddress, result);
                return (T)result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "PLC读取失败: {DeviceId} {Address}", deviceId, address);
                throw;
            }
            finally
            {
                _plcLock.Release();
            }
        }

        public async Task<bool> WriteAsync<T>(string deviceId, string address, T value)
        {
            await _plcLock.WaitAsync();
            try
            {
                if (!_isConnected || _plc == null)
                    return false;

                if (!_deviceMappings.TryGetValue(deviceId, out var mapping))
                {
                    _logger.LogError("设备 {DeviceId} 没有配置PLC地址映射", deviceId);
                    return false;
                }

                if (!mapping.CommandAddresses.TryGetValue(address, out var plcAddress))
                {
                    _logger.LogError("设备 {DeviceId} 的命令 {Address} 没有配置PLC地址", deviceId, address);
                    return false;
                }

                var addressInfo = ParseAddress(plcAddress);

                switch (value)
                {
                    case bool boolValue:
                        WriteBool(addressInfo, boolValue);
                        break;
                    case short shortValue:
                        WriteInt16(addressInfo, shortValue);
                        break;
                    case int intValue:
                        WriteInt32(addressInfo, intValue);
                        break;
                    case float floatValue:
                        WriteFloat(addressInfo, floatValue);
                        break;
                    case double doubleValue:
                        WriteDouble(addressInfo, doubleValue);
                        break;
                    default:
                        throw new NotSupportedException($"不支持的数据类型: {typeof(T)}");
                }

                _logger.LogDebug("PLC写入成功: {DeviceId} {Address} <- {Value}", deviceId, plcAddress, value);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "PLC写入失败: {DeviceId} {Address} <- {Value}", deviceId, address, value);
                return false;
            }
            finally
            {
                _plcLock.Release();
            }
        }

        // 修复：正确的读取方法
        private bool ReadBool(PLCAddressInfo address)
        {
            var bytes = _plc.ReadBytes(DataType.DataBlock, address.DbNumber, address.StartByte, 1);
            return (bytes[0] & (1 << address.BitOffset)) != 0;
        }

        private short ReadInt16(PLCAddressInfo address)
        {
            var bytes = _plc.ReadBytes(DataType.DataBlock, address.DbNumber, address.StartByte, 2);
            return BitConverter.ToInt16(ReverseBytes(bytes), 0);
        }

        private int ReadInt32(PLCAddressInfo address)
        {
            var bytes = _plc.ReadBytes(DataType.DataBlock, address.DbNumber, address.StartByte, 4);
            return BitConverter.ToInt32(ReverseBytes(bytes), 0);
        }

        private float ReadFloat(PLCAddressInfo address)
        {
            var bytes = _plc.ReadBytes(DataType.DataBlock, address.DbNumber, address.StartByte, 4);
            return BitConverter.ToSingle(ReverseBytes(bytes), 0);
        }

        private double ReadDouble(PLCAddressInfo address)
        {
            var bytes = _plc.ReadBytes(DataType.DataBlock, address.DbNumber, address.StartByte, 8);
            return BitConverter.ToDouble(ReverseBytes(bytes), 0);
        }

        // 修复：正确的写入方法
        private void WriteBool(PLCAddressInfo address, bool value)
        {
            var bytes = _plc.ReadBytes(DataType.DataBlock, address.DbNumber, address.StartByte, 1);
            if (value)
                bytes[0] |= (byte)(1 << address.BitOffset);
            else
                bytes[0] &= (byte)~(1 << address.BitOffset);

            _plc.WriteBytes(DataType.DataBlock, address.DbNumber, address.StartByte, bytes);
        }

        private void WriteInt16(PLCAddressInfo address, short value)
        {
            var bytes = ReverseBytes(BitConverter.GetBytes(value));
            _plc.WriteBytes(DataType.DataBlock, address.DbNumber, address.StartByte, bytes);
        }

        private void WriteInt32(PLCAddressInfo address, int value)
        {
            var bytes = ReverseBytes(BitConverter.GetBytes(value));
            _plc.WriteBytes(DataType.DataBlock, address.DbNumber, address.StartByte, bytes);
        }

        private void WriteFloat(PLCAddressInfo address, float value)
        {
            var bytes = ReverseBytes(BitConverter.GetBytes(value));
            _plc.WriteBytes(DataType.DataBlock, address.DbNumber, address.StartByte, bytes);
        }

        private void WriteDouble(PLCAddressInfo address, double value)
        {
            var bytes = ReverseBytes(BitConverter.GetBytes(value));
            _plc.WriteBytes(DataType.DataBlock, address.DbNumber, address.StartByte, bytes);
        }

        // 修复：PLC字节序转换（西门子PLC使用大端序）
        private byte[] ReverseBytes(byte[] bytes)
        {
            if (bytes.Length == 2)
            {
                return new[] { bytes[1], bytes[0] };
            }
            else if (bytes.Length == 4)
            {
                return new[] { bytes[3], bytes[2], bytes[1], bytes[0] };
            }
            else if (bytes.Length == 8)
            {
                return new[] { bytes[7], bytes[6], bytes[5], bytes[4], bytes[3], bytes[2], bytes[1], bytes[0] };
            }
            return bytes;
        }

        private PLCAddressInfo ParseAddress(string address)
        {
            var parts = address.Split('.');
            if (parts.Length < 2)
                throw new ArgumentException($"无效的PLC地址格式: {address}");

            var dbPart = parts[0];
            var dataPart = parts[1];

            var dbNumber = int.Parse(dbPart.Substring(2));

            var startByte = 0;
            var bitOffset = 0;

            if (dataPart.StartsWith("DBX"))
            {
                // 位地址，如 DBX0.0
                var bitParts = dataPart.Substring(3).Split('.');
                startByte = int.Parse(bitParts[0]);
                if (bitParts.Length > 1)
                    bitOffset = int.Parse(bitParts[1]);
            }
            else if (dataPart.StartsWith("DBW"))
            {
                // 字地址，如 DBW10
                startByte = int.Parse(dataPart.Substring(3));
            }
            else if (dataPart.StartsWith("DBD"))
            {
                // 双字地址，如 DBD20
                startByte = int.Parse(dataPart.Substring(3));
            }

            return new PLCAddressInfo
            {
                DbNumber = dbNumber,
                StartByte = startByte,
                BitOffset = bitOffset
            };
        }

        private void LoadDeviceMappings()
        {

            /*
             DB1.DBX0.0    // DB1块，位（Bit），字节0，位0
             DB1.DBB5      // DB1块，字节（Byte），字节5
             DB1.DBW10     // DB1块，字（Word，2字节），字节10开始
             DB1.DBD20     // DB1块，双字（Double Word，4字节），字节20开始
             */

            _deviceMappings.Add("COM2", new PLCDeviceMapping
            {
                ComId = "COM2",
                BaseAddress = "DB1",
                CommandAddresses = new Dictionary<string, string>
                {
                    ["开启"] = "DB1.DBX0.0",
                    ["关闭"] = "DB1.DBX0.1",
                    ["获取温度值"] = "DB1.DBD10",
                    ["设置温度值"] = "DB1.DBD10",
                    ["获取流量值"] = "DB1.DBD14",
                    ["获取压力值"] = "DB1.DBD18",
                    ["设置转速"] = "DB1.DBW22",
                    ["获取转速"] = "DB1.DBW24"
                }
            });
            _deviceMappings.Add("COM4", new PLCDeviceMapping
            {
                ComId = "COM4",
                BaseAddress = "DB1",
                CommandAddresses = new Dictionary<string, string>
                {
                    ["开启"] = "DB1.DBX0.0",
                    ["关闭"] = "DB1.DBX0.1",
                    ["获取温度值"] = "DB1.DBD20",
                    ["设置温度值"] = "DB1.DBD20",
                    ["获取流量值"] = "DB1.DBD24",
                    ["获取压力值"] = "DB1.DBD28",
                    ["设置转速"] = "DB1.DBW22",
                    ["获取转速"] = "DB1.DBW24"
                }
            });
            _logger.LogInformation("已加载 {Count} 个设备的PLC地址映射", _deviceMappings.Count);
        }

        public void Dispose()
        {
            try
            {
                if (_plc?.IsConnected == true)
                {
                    _plc.Close();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "释放PLC连接资源失败");
            }

            _plcLock?.Dispose();
        }
    }

    // 修复：添加位偏移字段
    public class PLCAddressInfo
    {
        public int DbNumber { get; set; }
        public int StartByte { get; set; }
        public int BitOffset { get; set; } // 添加位偏移
    }

    public class PLCDeviceMapping
    {
        public string ComId { get; set; }
        public string BaseAddress { get; set; }
        public Dictionary<string, string> CommandAddresses { get; set; } = new Dictionary<string, string>();
    }
}
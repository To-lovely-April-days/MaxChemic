using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Net.Sockets;
using MaxChemical.Logging;
using MaxChemical.Core;
using System.Text;
using System.Linq;
using NModbus;

namespace DevicePlugins.Devices
{
    public class ModbusTcpConnectionManager : IDeviceConnectionManager, IDisposable
    {
        private static ModbusTcpConnectionManager _instance;
        private static readonly object _lock = new object();

        public static ModbusTcpConnectionManager Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock)
                    {
                        _instance ??= new ModbusTcpConnectionManager();
                    }
                }
                return _instance;
            }
        }

        private readonly ILogService _logger;
        private readonly SemaphoreSlim _modbusLock = new SemaphoreSlim(1, 1);
        private readonly Dictionary<string, DeviceConnectionStatus> _deviceStatuses;
        private readonly Dictionary<string, ModbusDeviceMapping> _deviceMappings;

        // *** 使用 NModbus 库 ***
        private TcpClient _tcpClient;
        private IModbusMaster _modbusMaster;
        private bool _isConnected = false;
        private Timer _heartbeatTimer;
        private DateTime _lastHeartbeat = DateTime.Now;

        public ModbusTcpConnectionManager()
        {
            _logger = LogManager.GetLogger<ModbusTcpConnectionManager>();
            _deviceStatuses = new Dictionary<string, DeviceConnectionStatus>();
            _deviceMappings = new Dictionary<string, ModbusDeviceMapping>();

            LoadDeviceMappings();
            StartHeartbeat();
        }

        public async Task<bool> ConnectAsync(string deviceId)
        {
            await _modbusLock.WaitAsync();
            try
            {
                if (!_isConnected)
                {
                    var success = await ConnectToModbusServerAsync();
                    if (!success)
                    {
                        _logger.LogError("ModbusTcp服务器连接失败");
                        return false;
                    }
                }

                _deviceStatuses[deviceId] = DeviceConnectionStatus.Connected;
                _logger.LogInformation("设备通过ModbusTcp连接成功: {DeviceId}", deviceId);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ModbusTcp设备连接失败: {DeviceId}", deviceId);
                return false;
            }
            finally
            {
                _modbusLock.Release();
            }
        }

        public async Task<bool> DisconnectAsync(string deviceId)
        {
            await _modbusLock.WaitAsync();
            try
            {
                _deviceStatuses[deviceId] = DeviceConnectionStatus.Disconnected;
                _logger.LogInformation("设备已断开ModbusTcp连接: {DeviceId}", deviceId);
                return true;
            }
            finally
            {
                _modbusLock.Release();
            }
        }

        public async Task<bool> CheckConnectionAsync(string deviceId)
        {
            await _modbusLock.WaitAsync();
            try
            {
                if (!_isConnected || _tcpClient == null || !_tcpClient.Connected)
                {
                    _logger.LogWarning("ModbusTcp连接已断开，尝试重连");
                    return await ConnectToModbusServerAsync();
                }

                // 发送心跳包检查连接
                return await SendHeartbeatAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "检查ModbusTcp连接失败");
                return false;
            }
            finally
            {
                _modbusLock.Release();
            }
        }

        public bool IsConnected(string deviceId)
        {
            return _deviceStatuses.GetValueOrDefault(deviceId, DeviceConnectionStatus.Disconnected)
                   == DeviceConnectionStatus.Connected && _isConnected;
        }

        public async Task<T> ReadAsync<T>(string deviceId, string address)
        {
            await _modbusLock.WaitAsync();
            try
            {
                if (!_isConnected || _modbusMaster == null)
                    throw new InvalidOperationException("ModbusTcp未连接");

                if (!_deviceMappings.TryGetValue(deviceId, out var mapping))
                {
                    throw new InvalidOperationException($"设备 {deviceId} 没有配置Modbus地址映射");
                }

                if (!mapping.CommandAddresses.TryGetValue(address, out var modbusAddress))
                {
                    throw new InvalidOperationException($"设备 {deviceId} 的命令 {address} 没有配置Modbus地址");
                }

                var addressInfo = ParseModbusAddress(modbusAddress);
                var result = await ReadModbusDataAsync<T>(addressInfo, deviceId, address);

                _logger.LogInformation("ModbusTcp读取成功: 设备={DeviceId} 命令={Address} 地址={ModbusAddress} 值={Value}",
                    deviceId, address, modbusAddress, result);
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ModbusTcp读取失败: 设备={DeviceId} 命令={Address}", deviceId, address);
                throw;
            }
            finally
            {
                _modbusLock.Release();
            }
        }

        public async Task<bool> WriteAsync<T>(string deviceId, string address, T value)
        {
            await _modbusLock.WaitAsync();
            try
            {
                if (!_isConnected || _modbusMaster == null)
                    return false;

                if (!_deviceMappings.TryGetValue(deviceId, out var mapping))
                {
                    _logger.LogError("设备 {DeviceId} 没有配置Modbus地址映射", deviceId);
                    return false;
                }

                if (!mapping.CommandAddresses.TryGetValue(address, out var modbusAddress))
                {
                    _logger.LogError("设备 {DeviceId} 的命令 {Address} 没有配置Modbus地址", deviceId, address);
                    return false;
                }

                var addressInfo = ParseModbusAddress(modbusAddress);
                await WriteModbusDataAsync(addressInfo, value, deviceId, address);

                _logger.LogInformation("ModbusTcp写入成功: 设备={DeviceId} 命令={Address} 地址={ModbusAddress} 值={Value}",
                    deviceId, address, modbusAddress, value);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ModbusTcp写入失败: 设备={DeviceId} 命令={Address} 值={Value}", deviceId, address, value);
                return false;
            }
            finally
            {
                _modbusLock.Release();
            }
        }

        #region 私有方法

        /// <summary>
        /// *** 使用 NModbus 库连接服务器 ***
        /// </summary>
        private async Task<bool> ConnectToModbusServerAsync()
        {
            try
            {
                var settings = DeviceConnectionConfig.ModbusTcpSettings;

                // 释放旧连接
                _modbusMaster?.Dispose();
                _tcpClient?.Close();

                _logger.LogInformation("正在连接ModbusTcp服务器: {IP}:{Port}", settings.IpAddress, settings.Port);

                // 创建 TCP 连接
                _tcpClient = new TcpClient();
                await _tcpClient.ConnectAsync(settings.IpAddress, settings.Port)
                    .WaitAsync(TimeSpan.FromMilliseconds(settings.ConnectTimeout));

                // 创建 Modbus Master
                var factory = new ModbusFactory();
                _modbusMaster = factory.CreateMaster(_tcpClient);
                _modbusMaster.Transport.ReadTimeout = settings.ReadWriteTimeout;
                _modbusMaster.Transport.WriteTimeout = settings.ReadWriteTimeout;

                _isConnected = true;
                _lastHeartbeat = DateTime.Now;

                _logger.LogInformation("ModbusTcp服务器连接成功: {IP}:{Port}", settings.IpAddress, settings.Port);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "连接ModbusTcp服务器失败");
                _isConnected = false;
                return false;
            }
        }

        /// <summary>
        /// *** 使用 NModbus 库发送心跳 ***
        /// </summary>
        private async Task<bool> SendHeartbeatAsync()
        {
            try
            {
                if (_modbusMaster == null) return false;

                _logger.LogDebug("【ModbusTCP心跳】发送心跳包");

                // 读取第一个保持寄存器作为心跳
                var registers = await Task.Run(() => _modbusMaster.ReadHoldingRegisters(1, 0, 1));

                _logger.LogDebug("【ModbusTCP心跳】心跳响应: {Response}", string.Join(" ", registers.Select(r => r.ToString("X4"))));

                _lastHeartbeat = DateTime.Now;
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ModbusTcp心跳检查失败");
                return false;
            }
        }

        private void StartHeartbeat()
        {
            _heartbeatTimer = new Timer(async _ =>
            {
                if (_isConnected && (DateTime.Now - _lastHeartbeat).TotalSeconds > 30)
                {
                    await CheckConnectionAsync("");
                }
            }, null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
        }

        /// <summary>
        /// *** 使用 NModbus 库读取数据 ***
        /// </summary>
        private async Task<T> ReadModbusDataAsync<T>(ModbusAddressInfo addressInfo, string deviceId = "", string command = "")
        {
            try
            {
                _logger.LogInformation("【ModbusTCP读取】设备={DeviceId} 命令={Command} 地址类型={RegisterType} 地址={Address} 从站ID={SlaveId} 数据类型={DataType}",
                    deviceId, command, addressInfo.RegisterType, addressInfo.Address, addressInfo.SlaveId, typeof(T).Name);

                object result = null;

                switch (addressInfo.RegisterType)
                {
                    case ModbusRegisterType.Coil:
                        var coils = await Task.Run(() => _modbusMaster.ReadCoils(addressInfo.SlaveId, (ushort)addressInfo.Address, 1));
                        _logger.LogInformation("【ModbusTCP读取】线圈原始数据: {Data}", string.Join(" ", coils.Select(c => c ? "1" : "0")));
                        result = coils[0];
                        break;

                    case ModbusRegisterType.DiscreteInput:
                        var inputs = await Task.Run(() => _modbusMaster.ReadInputs(addressInfo.SlaveId, (ushort)addressInfo.Address, 1));
                        _logger.LogInformation("【ModbusTCP读取】离散输入原始数据: {Data}", string.Join(" ", inputs.Select(i => i ? "1" : "0")));
                        result = inputs[0];
                        break;

                    case ModbusRegisterType.InputRegister:
                        var inputRegisters = await Task.Run(() => _modbusMaster.ReadInputRegisters(addressInfo.SlaveId, (ushort)addressInfo.Address, GetRegisterCount<T>()));
                        _logger.LogInformation("【ModbusTCP读取】输入寄存器原始数据: {Data}", string.Join(" ", inputRegisters.Select(r => r.ToString("X4"))));
                        result = ConvertRegistersToValue<T>(inputRegisters);
                        break;

                    case ModbusRegisterType.HoldingRegister:
                        var holdingRegisters = await Task.Run(() => _modbusMaster.ReadHoldingRegisters(addressInfo.SlaveId, (ushort)addressInfo.Address, GetRegisterCount<T>()));
                        _logger.LogInformation("【ModbusTCP读取】保持寄存器原始数据: {Data}", string.Join(" ", holdingRegisters.Select(r => r.ToString("X4"))));
                        result = ConvertRegistersToValue<T>(holdingRegisters);
                        break;

                    default:
                        throw new NotSupportedException($"不支持的寄存器类型: {addressInfo.RegisterType}");
                }

                // 类型转换
                if (result is T directResult)
                {
                    _logger.LogInformation("【ModbusTCP读取结果】设备={DeviceId} 命令={Command} 值={Value}", deviceId, command, directResult);
                    return directResult;
                }
                else
                {
                    var convertedResult = (T)Convert.ChangeType(result, typeof(T));
                    _logger.LogInformation("【ModbusTCP读取结果】设备={DeviceId} 命令={Command} 原始值={RawValue} 转换后={ConvertedValue}",
                        deviceId, command, result, convertedResult);
                    return convertedResult;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "【ModbusTCP读取异常】设备={DeviceId} 命令={Command}", deviceId, command);
                throw;
            }
        }

        /// <summary>
        /// *** 使用 NModbus 库写入数据 ***
        /// </summary>
        private async Task WriteModbusDataAsync<T>(ModbusAddressInfo addressInfo, T value, string deviceId = "", string command = "")
        {
            try
            {
                _logger.LogInformation("【ModbusTCP写入】设备={DeviceId} 命令={Command} 地址类型={RegisterType} 地址={Address} 从站ID={SlaveId} 值={Value} 数据类型={DataType}",
                    deviceId, command, addressInfo.RegisterType, addressInfo.Address, addressInfo.SlaveId, value, typeof(T).Name);

                switch (addressInfo.RegisterType)
                {
                    case ModbusRegisterType.Coil:
                        var coilValue = Convert.ToBoolean(value);
                        await Task.Run(() => _modbusMaster.WriteSingleCoil(addressInfo.SlaveId, (ushort)addressInfo.Address, coilValue));
                        _logger.LogInformation("【ModbusTCP写入】线圈写入: 地址={Address} 值={Value}", addressInfo.Address, coilValue);
                        break;

                    case ModbusRegisterType.HoldingRegister:
                        var registers = ConvertValueToRegisters(value);
                        _logger.LogInformation("【ModbusTCP写入】转换为寄存器: {Registers}", string.Join(" ", registers.Select(r => r.ToString("X4"))));

                        if (registers.Length == 1)
                        {
                            await Task.Run(() => _modbusMaster.WriteSingleRegister(addressInfo.SlaveId, (ushort)addressInfo.Address, registers[0]));
                            _logger.LogInformation("【ModbusTCP写入】单个寄存器写入: 地址={Address} 值={Value:X4}", addressInfo.Address, registers[0]);
                        }
                        else
                        {
                            await Task.Run(() => _modbusMaster.WriteMultipleRegisters(addressInfo.SlaveId, (ushort)addressInfo.Address, registers));
                            _logger.LogInformation("【ModbusTCP写入】多个寄存器写入: 地址={Address} 数量={Count} 值={Values}",
                                addressInfo.Address, registers.Length, string.Join(" ", registers.Select(r => r.ToString("X4"))));
                        }
                        break;

                    default:
                        throw new NotSupportedException($"不支持写入的寄存器类型: {addressInfo.RegisterType}");
                }

                _logger.LogInformation("【ModbusTCP写入成功】设备={DeviceId} 命令={Command}", deviceId, command);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "【ModbusTCP写入异常】设备={DeviceId} 命令={Command} 值={Value}", deviceId, command, value);
                throw;
            }
        }

        /// <summary>
        /// 获取数据类型所需的寄存器数量
        /// </summary>
        private ushort GetRegisterCount<T>()
        {
            return typeof(T) switch
            {
                Type t when t == typeof(bool) => 1,
                Type t when t == typeof(short) || t == typeof(ushort) => 1,
                Type t when t == typeof(int) || t == typeof(uint) || t == typeof(float) => 2,
                // *** 修复：double统一按float处理，使用2个寄存器 ***
                Type t when t == typeof(double) => 2, // 改为2，而不是4
                Type t when t == typeof(long) || t == typeof(ulong) => 4,
                _ => 1
            };
        }

        /// <summary>
        /// 将寄存器数据转换为指定类型的值
        /// </summary>
        private T ConvertRegistersToValue<T>(ushort[] registers)
        {
            _logger.LogDebug("【数据转换】寄存器转值: 类型={Type} 寄存器={Registers}",
                typeof(T).Name, string.Join(" ", registers.Select(r => r.ToString("X4"))));

            if (typeof(T) == typeof(bool))
            {
                var result = registers[0] != 0;
                _logger.LogDebug("【数据转换】bool值: {Value}", result);
                return (T)(object)result;
            }
            else if (typeof(T) == typeof(short))
            {
                var result = (short)registers[0];
                _logger.LogDebug("【数据转换】short值: {Value}", result);
                return (T)(object)result;
            }
            else if (typeof(T) == typeof(ushort))
            {
                var result = registers[0];
                _logger.LogDebug("【数据转换】ushort值: {Value}", result);
                return (T)(object)result;
            }
            else if (typeof(T) == typeof(int))
            {
                var result = (registers[0] << 16) | registers[1];
                _logger.LogDebug("【数据转换】int值: {Value}", result);
                return (T)(object)result;
            }
            else if (typeof(T) == typeof(uint))
            {
                var result = (uint)((registers[0] << 16) | registers[1]);
                _logger.LogDebug("【数据转换】uint值: {Value}", result);
                return (T)(object)result;
            }
            else if (typeof(T) == typeof(float))
            {
                // *** 修复：正确的float字节序转换 ***
                var bytes = new byte[4];
                bytes[0] = (byte)(registers[1] & 0xFF);        // 低字节
                bytes[1] = (byte)(registers[1] >> 8);          // 高字节
                bytes[2] = (byte)(registers[0] & 0xFF);        // 低字节 
                bytes[3] = (byte)(registers[0] >> 8);          // 高字节
                var result = BitConverter.ToSingle(bytes, 0);
                _logger.LogDebug("【数据转换】float值: 寄存器={Registers} 字节={Bytes} 结果={Value}",
                    string.Join(" ", registers.Select(r => r.ToString("X4"))),
                    string.Join(" ", bytes.Select(b => b.ToString("X2"))), result);
                return (T)(object)result;
            }
            else if (typeof(T) == typeof(double))
            {
                // *** 修复：double按float处理，使用相同的转换逻辑 ***
                if (registers.Length < 2)
                {
                    throw new ArgumentException($"double转换需要至少2个寄存器，实际{registers.Length}个");
                }

                var bytes = new byte[4];
                bytes[0] = (byte)(registers[1] & 0xFF);        // 低字节
                bytes[1] = (byte)(registers[1] >> 8);          // 高字节
                bytes[2] = (byte)(registers[0] & 0xFF);        // 低字节 
                bytes[3] = (byte)(registers[0] >> 8);          // 高字节
                var floatResult = BitConverter.ToSingle(bytes, 0);
                var result = (double)floatResult;
                _logger.LogDebug("【数据转换】double值(按float处理): 寄存器={Registers} 字节={Bytes} float={FloatValue} double={DoubleValue}",
                    string.Join(" ", registers.Select(r => r.ToString("X4"))),
                    string.Join(" ", bytes.Select(b => b.ToString("X2"))), floatResult, result);
                return (T)(object)result;
            }
            else if (typeof(T) == typeof(long))
            {
                if (registers.Length < 4)
                {
                    throw new ArgumentException($"long转换需要4个寄存器，实际{registers.Length}个");
                }
                var result = ((long)registers[0] << 48) | ((long)registers[1] << 32) | ((long)registers[2] << 16) | registers[3];
                _logger.LogDebug("【数据转换】long值: {Value}", result);
                return (T)(object)result;
            }
            else if (typeof(T) == typeof(ulong))
            {
                if (registers.Length < 4)
                {
                    throw new ArgumentException($"ulong转换需要4个寄存器，实际{registers.Length}个");
                }
                var result = ((ulong)registers[0] << 48) | ((ulong)registers[1] << 32) | ((ulong)registers[2] << 16) | registers[3];
                _logger.LogDebug("【数据转换】ulong值: {Value}", result);
                return (T)(object)result;
            }

            throw new NotSupportedException($"不支持的数据类型转换: {typeof(T)}");
        }


        /// <summary>
        /// 将值转换为寄存器数据
        /// </summary>
        private ushort[] ConvertValueToRegisters<T>(T value)
        {
            _logger.LogDebug("【数据转换】值转寄存器: 类型={Type} 值={Value}", typeof(T).Name, value);

            if (value is bool boolValue)
            {
                var result = new ushort[] { (ushort)(boolValue ? 1 : 0) };
                _logger.LogDebug("【数据转换】bool寄存器: {Registers}", string.Join(" ", result.Select(r => r.ToString("X4"))));
                return result;
            }
            else if (value is short shortValue)
            {
                var result = new ushort[] { (ushort)shortValue };
                _logger.LogDebug("【数据转换】short寄存器: {Registers}", string.Join(" ", result.Select(r => r.ToString("X4"))));
                return result;
            }
            else if (value is ushort ushortValue)
            {
                var result = new ushort[] { ushortValue };
                _logger.LogDebug("【数据转换】ushort寄存器: {Registers}", string.Join(" ", result.Select(r => r.ToString("X4"))));
                return result;
            }
            else if (value is int intValue)
            {
                var result = new ushort[] { (ushort)(intValue >> 16), (ushort)(intValue & 0xFFFF) };
                _logger.LogDebug("【数据转换】int寄存器: {Registers}", string.Join(" ", result.Select(r => r.ToString("X4"))));
                return result;
            }
            else if (value is uint uintValue)
            {
                var result = new ushort[] { (ushort)(uintValue >> 16), (ushort)(uintValue & 0xFFFF) };
                _logger.LogDebug("【数据转换】uint寄存器: {Registers}", string.Join(" ", result.Select(r => r.ToString("X4"))));
                return result;
            }
            else if (value is float floatValue)
            {
                var bytes = BitConverter.GetBytes(floatValue);
                // *** 修复：正确的字节序转换 ***
                var result = new ushort[]
                {
            (ushort)((bytes[3] << 8) | bytes[2]),  // 高位寄存器
            (ushort)((bytes[1] << 8) | bytes[0])   // 低位寄存器
                };
                _logger.LogDebug("【数据转换】float寄存器: 值={Value} 字节={Bytes} 寄存器={Registers}",
                    floatValue,
                    string.Join(" ", bytes.Select(b => b.ToString("X2"))),
                    string.Join(" ", result.Select(r => r.ToString("X4"))));
                return result;
            }
            else if (value is double doubleValue)
            {
                // *** 修复：double转换为float处理，保持一致性 ***
                var floatVal = (float)doubleValue;
                var bytes = BitConverter.GetBytes(floatVal);
                var result = new ushort[]
                {
            (ushort)((bytes[3] << 8) | bytes[2]),  // 高位寄存器
            (ushort)((bytes[1] << 8) | bytes[0])   // 低位寄存器
                };
                _logger.LogDebug("【数据转换】double->float寄存器: {DoubleValue} -> {FloatValue} 字节={Bytes} 寄存器={Registers}",
                    doubleValue, floatVal,
                    string.Join(" ", bytes.Select(b => b.ToString("X2"))),
                    string.Join(" ", result.Select(r => r.ToString("X4"))));
                return result;
            }
            else if (value is long longValue)
            {
                var result = new ushort[]
                {
            (ushort)(longValue >> 48),
            (ushort)((longValue >> 32) & 0xFFFF),
            (ushort)((longValue >> 16) & 0xFFFF),
            (ushort)(longValue & 0xFFFF)
                };
                _logger.LogDebug("【数据转换】long寄存器: {Registers}", string.Join(" ", result.Select(r => r.ToString("X4"))));
                return result;
            }
            else if (value is ulong ulongValue)
            {
                var result = new ushort[]
                {
            (ushort)(ulongValue >> 48),
            (ushort)((ulongValue >> 32) & 0xFFFF),
            (ushort)((ulongValue >> 16) & 0xFFFF),
            (ushort)(ulongValue & 0xFFFF)
                };
                _logger.LogDebug("【数据转换】ulong寄存器: {Registers}", string.Join(" ", result.Select(r => r.ToString("X4"))));
                return result;
            }

            throw new NotSupportedException($"不支持的数据类型转换: {typeof(T)}，值: {value}");
        }

        private ModbusAddressInfo ParseModbusAddress(string address)
        {
            // 解析格式: "40001" (保持寄存器) 或 "10001" (线圈) 等
            if (int.TryParse(address, out var fullAddress))
            {
                var registerType = fullAddress switch
                {
                    >= 1 and <= 9999 => ModbusRegisterType.Coil,
                    >= 10001 and <= 19999 => ModbusRegisterType.DiscreteInput,
                    >= 30001 and <= 39999 => ModbusRegisterType.InputRegister,
                    >= 40001 and <= 49999 => ModbusRegisterType.HoldingRegister,
                    _ => ModbusRegisterType.HoldingRegister
                };

                var actualAddress = registerType switch
                {
                    ModbusRegisterType.Coil => fullAddress - 1,
                    ModbusRegisterType.DiscreteInput => fullAddress - 10001,
                    ModbusRegisterType.InputRegister => fullAddress - 30001,
                    ModbusRegisterType.HoldingRegister => fullAddress - 40001,
                    _ => fullAddress
                };

                return new ModbusAddressInfo
                {
                    Address = actualAddress,
                    RegisterType = registerType,
                    SlaveId = 1 // 默认从站ID
                };
            }

            throw new ArgumentException($"无效的Modbus地址格式: {address}");
        }

        private void LoadDeviceMappings()
        {
            // P0101蠕动泵设备映射
            _deviceMappings.Add("P0101", new ModbusDeviceMapping
            {
                ComId = "P0101",
                SlaveId = 1,
                CommandAddresses = new Dictionary<string, string>
                {
                    ["P0101泵速"] = "40001",
                    ["P0101出口泄压压力_上限报警"] = "40003",
                    ["P0101出口预警压力"] = "40004",
                    ["P0101出口温度上限报警"] = "40005",
                    ["P0101出口压力"] = "40006",
                    ["P0101出口温度"] = "40008",
                    ["P0101出口流量"] = "40010"
                }
            });

            _logger.LogInformation("已加载 {Count} 个设备的ModbusTcp地址映射", _deviceMappings.Count);
        }

        #endregion

        public void Dispose()
        {
            try
            {
                _heartbeatTimer?.Dispose();
                _modbusMaster?.Dispose();
                _tcpClient?.Close();
                _modbusLock?.Dispose();
                _logger.LogInformation("ModbusTcp连接资源已释放");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "释放ModbusTcp连接资源失败");
            }
        }
    }

    /// <summary>
    /// Modbus地址信息
    /// </summary>
    public class ModbusAddressInfo
    {
        public int Address { get; set; }
        public ModbusRegisterType RegisterType { get; set; }
        public byte SlaveId { get; set; } = 1;
    }

    /// <summary>
    /// Modbus寄存器类型
    /// </summary>
    public enum ModbusRegisterType
    {
        Coil = 1,           // 线圈 (00001-09999)
        DiscreteInput = 2,  // 离散输入 (10001-19999)
        InputRegister = 3,  // 输入寄存器 (30001-39999)
        HoldingRegister = 4 // 保持寄存器 (40001-49999)
    }

    /// <summary>
    /// Modbus设备映射
    /// </summary>
    public class ModbusDeviceMapping
    {
        public string ComId { get; set; }
        public byte SlaveId { get; set; } = 1;
        public Dictionary<string, string> CommandAddresses { get; set; } = new Dictionary<string, string>();
    }
}
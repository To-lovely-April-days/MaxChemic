using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Net.Sockets;
using MaxChemical.Logging;
using MaxChemical.Core;
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
        private readonly ConcurrentDictionary<string, DeviceConnectionStatus> _deviceStatuses;
        private readonly ConcurrentDictionary<string, ModbusDeviceMapping> _deviceMappings;

        // *** 简化为每次操作都创建新连接，避免连接池污染 ***
        private readonly SemaphoreSlim _globalLock = new SemaphoreSlim(3, 3); // 限制并发数

        // *** 连接配置 ***
        private const int CONNECTION_TIMEOUT_MS = 3000;    // 缩短连接超时
        private const int OPERATION_TIMEOUT_MS = 2000;     // 缩短操作超时
        private const int MAX_RETRY_COUNT = 2;             // 减少重试次数
        private const int RETRY_DELAY_MS = 300;            // 重试延迟

        public ModbusTcpConnectionManager()
        {
            _logger = LogManager.GetLogger<ModbusTcpConnectionManager>();
            _deviceStatuses = new ConcurrentDictionary<string, DeviceConnectionStatus>();
            _deviceMappings = new ConcurrentDictionary<string, ModbusDeviceMapping>();

            LoadDeviceMappings();
        }

        #region 简化的连接管理 - 每次创建新连接

        /// <summary>
        /// 创建一次性连接包装器
        /// </summary>
        private class DisposableConnection : IDisposable
        {
            private readonly TcpClient _tcpClient;
            private readonly IModbusMaster _modbusMaster;
            private readonly ILogService _logger;
            private bool _disposed = false;

            public DisposableConnection(TcpClient tcpClient, IModbusMaster modbusMaster, ILogService logger)
            {
                _tcpClient = tcpClient;
                _modbusMaster = modbusMaster;
                _logger = logger;
            }

            public IModbusMaster ModbusMaster => _modbusMaster;

            public bool IsValid()
            {
                try
                {
                    return !_disposed && _tcpClient?.Connected == true && _modbusMaster != null;
                }
                catch
                {
                    return false;
                }
            }

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;

                try
                {
                    _modbusMaster?.Dispose();
                }
                catch (Exception ex)
                {
                    _logger.LogDebug($"释放ModbusMaster时出错: {ex.Message}");
                }

                try
                {
                    _tcpClient?.Close();
                    _tcpClient?.Dispose();
                }
                catch (Exception ex)
                {
                    _logger.LogDebug($"释放TcpClient时出错: {ex.Message}");
                }
            }
        }
        /// <summary>
        /// 创建新的ModbusTCP连接 - 增强版本
        /// </summary>
        private async Task<DisposableConnection> CreateConnectionAsync()
        {
            var tcpClient = new TcpClient();
            try
            {
                // *** 优化TCP设置 ***
                tcpClient.NoDelay = true;
                tcpClient.ReceiveTimeout = OPERATION_TIMEOUT_MS;
                tcpClient.SendTimeout = OPERATION_TIMEOUT_MS;
                tcpClient.LingerState = new LingerOption(false, 0);

                // *** 设置接收缓冲区大小 ***
                tcpClient.ReceiveBufferSize = 8192;
                tcpClient.SendBufferSize = 8192;

                var settings = DeviceConnectionConfig.ModbusTcpSettings;

                _logger.LogDebug($"创建新连接到 {settings.IpAddress}:{settings.Port}");

                await tcpClient.ConnectAsync(settings.IpAddress, settings.Port)
                    .WaitAsync(TimeSpan.FromMilliseconds(CONNECTION_TIMEOUT_MS));

                // *** 关键：等待TCP连接稳定 ***
                await Task.Delay(50); // 给TCP连接时间稳定

                // *** 验证连接状态 ***
                if (!tcpClient.Connected)
                {
                    throw new InvalidOperationException("TCP连接建立失败");
                }

                // *** 创建ModbusMaster ***
                var modbusMaster = CreateModbusMaster(tcpClient);

                // *** 连接预热：尝试读取一个安全的寄存器 ***
                try
                {
                    await Task.Run(() => modbusMaster.ReadHoldingRegisters(1, 0, 1));
                    _logger.LogDebug("连接预热成功");
                }
                catch (Exception ex)
                {
                    _logger.LogDebug($"连接预热失败（可忽略）: {ex.Message}");
                    // 某些设备地址0可能不存在，这是正常的
                }

                _logger.LogDebug("新ModbusTCP连接创建成功");
                return new DisposableConnection(tcpClient, modbusMaster, _logger);
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"创建ModbusTCP连接失败: {ex.Message}");
                tcpClient?.Dispose();
                throw;
            }
        }

        /// <summary>
        /// 兼容性创建ModbusMaster
        /// </summary>
        private IModbusMaster CreateModbusMaster(TcpClient tcpClient)
        {
            var factory = new ModbusFactory();
            IModbusMaster modbusMaster = null;

            try
            {
                // 尝试新版本API
                modbusMaster = factory.CreateMaster(tcpClient);
                _logger.LogDebug("使用新版本NModbus API创建ModbusMaster成功");
            }
            catch (Exception ex1)
            {
                _logger.LogDebug($"新版本API失败: {ex1.Message}，尝试NetworkStream方式");

                //try
                //{
                //    var stream = tcpClient.GetStream();
                //    modbusMaster = factory.CreateMaster(stream);
                //    _logger.LogDebug("使用NetworkStream创建ModbusMaster成功");
                //}
                //catch (Exception ex2)
                //{
                //    throw new NotSupportedException($"无法创建ModbusMaster。新版本错误: {ex1.Message}, NetworkStream错误: {ex2.Message}");
                //}
            }

            // *** 设置更短的超时时间 ***
            SetModbusMasterTimeouts(modbusMaster);

            return modbusMaster;
        }

        /// <summary>
        /// 设置ModbusMaster超时参数
        /// </summary>
        private void SetModbusMasterTimeouts(IModbusMaster modbusMaster)
        {
            try
            {
                var transportProperty = modbusMaster.GetType().GetProperty("Transport");
                if (transportProperty != null)
                {
                    var transport = transportProperty.GetValue(modbusMaster);
                    if (transport != null)
                    {
                        // 设置更短的超时时间
                        var readTimeoutProperty = transport.GetType().GetProperty("ReadTimeout");
                        if (readTimeoutProperty?.CanWrite == true)
                        {
                            readTimeoutProperty.SetValue(transport, OPERATION_TIMEOUT_MS);
                        }

                        var writeTimeoutProperty = transport.GetType().GetProperty("WriteTimeout");
                        if (writeTimeoutProperty?.CanWrite == true)
                        {
                            writeTimeoutProperty.SetValue(transport, OPERATION_TIMEOUT_MS);
                        }

                        // *** 禁用重试，快速失败 ***
                        var retriesProperty = transport.GetType().GetProperty("Retries");
                        if (retriesProperty?.CanWrite == true)
                        {
                            retriesProperty.SetValue(transport, 0);
                        }

                        _logger.LogDebug($"ModbusMaster超时参数设置成功: ReadTimeout={OPERATION_TIMEOUT_MS}, WriteTimeout={OPERATION_TIMEOUT_MS}, Retries=0");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"设置ModbusMaster超时参数失败: {ex.Message}");
            }
        }

        #endregion

        #region 公共接口实现

        public async Task<bool> ConnectAsync(string deviceId)
        {
            try
            {
                using var connection = await CreateConnectionAsync();
                if (connection != null && connection.IsValid())
                {
                    _deviceStatuses[deviceId] = DeviceConnectionStatus.Connected;
                    _logger.LogInformation("设备通过ModbusTcp连接成功: {DeviceId}", deviceId);
                    connection.Dispose();
                    return true;
                }
                return false;

            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ModbusTcp设备连接失败: {DeviceId}", deviceId);
                return false;
            }
        }

        public async Task<bool> DisconnectAsync(string deviceId)
        {
            try
            {
                _deviceStatuses[deviceId] = DeviceConnectionStatus.Disconnected;
                _logger.LogInformation("设备已断开ModbusTcp连接: {DeviceId}", deviceId);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "断开ModbusTcp设备失败: {DeviceId}", deviceId);
                return false;
            }
        }

        public async Task<bool> CheckConnectionAsync(string deviceId)
        {
            return await Task.FromResult(IsConnected(deviceId));
        }

        public bool IsConnected(string deviceId)
        {
            return _deviceStatuses.GetValueOrDefault(deviceId, DeviceConnectionStatus.Disconnected)
                   == DeviceConnectionStatus.Connected;
        }

        public async Task<T> ReadAsync<T>(string deviceId, string address)
        {
            try
            {
                return await ExecuteWithRetryAsync(deviceId, address, async (modbusMaster, mapping, addr) =>
                {
                    var addressInfo = ParseModbusAddress(addr);
                    return await ReadModbusDataAsync<T>(modbusMaster, addressInfo, deviceId, address);
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ModbusTcp读取失败: 设备={DeviceId} 命令={Address}", deviceId, address);
                // *** 根据业务需求决定是重新抛出异常还是返回默认值 ***
                // 选项1: 重新抛出异常让上层处理
                throw;

                // 选项2: 返回默认值（如果业务逻辑允许）
                // return default(T);
            }
        }

        public async Task<bool> WriteAsync<T>(string deviceId, string address, T value)
        {
            try
            {
                await ExecuteWithRetryAsync(deviceId, address, async (modbusMaster, mapping, addr) =>
                {
                    var addressInfo = ParseModbusAddress(addr);
                    await WriteModbusDataAsync(modbusMaster, addressInfo, value, deviceId, address);
                    return true;
                });
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ModbusTcp写入失败: 设备={DeviceId} 命令={Address} 值={Value}", deviceId, address, value);
                throw;
            }
        }

        #endregion

        #region 核心执行逻辑

        /// <summary>
        /// 带重试机制的执行操作 - 每次创建新连接
        /// </summary>
        private async Task<TResult> ExecuteWithRetryAsync<TResult>(
            string deviceId,
            string address,
            Func<IModbusMaster, ModbusDeviceMapping, string, Task<TResult>> operation)
        {
            if (!_deviceMappings.TryGetValue(deviceId, out var mapping))
            {
                throw new InvalidOperationException($"设备 {deviceId} 没有配置Modbus地址映射");
            }

            if (!mapping.CommandAddresses.TryGetValue(address, out var modbusAddress))
            {
                throw new InvalidOperationException($"设备 {deviceId} 的命令 {address} 没有配置Modbus地址");
            }

            Exception lastException = null;

            for (int attempt = 1; attempt <= MAX_RETRY_COUNT; attempt++)
            {
                // *** 等待全局并发限制 ***
                await _globalLock.WaitAsync(CONNECTION_TIMEOUT_MS);

                try
                {
                    // *** 每次都创建全新连接 ***
                    using var connection = await CreateConnectionAsync();

                    if (!connection.IsValid())
                    {
                        throw new InvalidOperationException("创建的连接无效");
                    }

                    var result = await operation(connection.ModbusMaster, mapping, modbusAddress);

                    _logger.LogDebug("ModbusTCP操作成功: {DeviceId}.{Address} (尝试 {Attempt}/{MaxRetry})",
                        deviceId, address, attempt, MAX_RETRY_COUNT);

                    return result;

                    // *** using语句自动释放连接 ***
                }
                catch (Exception ex)
                {
                    lastException = ex;

                    bool isConnectionError = IsConnectionError(ex);
                    string errorType = isConnectionError ? "连接错误" : "操作错误";

                    _logger.LogWarning("ModbusTCP操作失败: {DeviceId}.{Address} (尝试 {Attempt}/{MaxRetry}) [{ErrorType}] - {Error}",
                        deviceId, address, attempt, MAX_RETRY_COUNT, errorType, ex.Message);

                    if (attempt == MAX_RETRY_COUNT)
                    {
                        break;
                    }

                    // *** 重试前短暂等待 ***
                    await Task.Delay(RETRY_DELAY_MS);
                }
                finally
                {
                    _globalLock.Release();
                }
            }

            throw new InvalidOperationException(
                $"ModbusTCP操作失败，已重试{MAX_RETRY_COUNT}次: {deviceId}.{address} - {lastException?.Message}",
                lastException);
        }

        /// <summary>
        /// 判断是否为连接错误
        /// </summary>
        private bool IsConnectionError(Exception ex)
        {
            return ex is SocketException ||
                   ex is System.IO.IOException ||
                   ex.Message.Contains("连接") ||
                   ex.Message.Contains("connection") ||
                   ex.Message.Contains("transport") ||
                   ex.Message.Contains("Unable to write") ||
                   ex.Message.Contains("Unable to read") ||
                   ex.Message.Contains("Read resulted in 0 bytes") ||
                   ex.Message.Contains("已建立的连接");
        }

        #endregion

        #region ModbusTCP数据操作

        /// <summary>
        /// 读取ModbusTCP数据
        /// </summary>
        private async Task<T> ReadModbusDataAsync<T>(IModbusMaster modbusMaster, ModbusAddressInfo addressInfo, string deviceId = "", string command = "")
        {
            try
            {
                _logger.LogDebug("【ModbusTCP读取】设备={DeviceId} 命令={Command} 地址类型={RegisterType} 地址={Address} 从站ID={SlaveId}",
                    deviceId, command, addressInfo.RegisterType, addressInfo.Address, addressInfo.SlaveId);

                object result = null;

                // *** 验证ModbusMaster ***
                if (modbusMaster == null)
                {
                    throw new InvalidOperationException("ModbusMaster为null");
                }

                // *** 添加短暂延迟，避免连续操作冲突 ***
                await Task.Delay(10);

                switch (addressInfo.RegisterType)
                {
                    case ModbusRegisterType.Coil:
                        var coils = await ReadWithTimeoutAsync(() =>
                            modbusMaster.ReadCoils(addressInfo.SlaveId, (ushort)addressInfo.Address, 1));
                        result = coils[0];
                        break;

                    case ModbusRegisterType.DiscreteInput:
                        var inputs = await ReadWithTimeoutAsync(() =>
                            modbusMaster.ReadInputs(addressInfo.SlaveId, (ushort)addressInfo.Address, 1));
                        result = inputs[0];
                        break;

                    case ModbusRegisterType.InputRegister:
                        var inputRegisters = await ReadWithTimeoutAsync(() =>
                            modbusMaster.ReadInputRegisters(addressInfo.SlaveId, (ushort)addressInfo.Address, GetRegisterCount<T>()));
                        result = ConvertRegistersToValue<T>(inputRegisters);
                        break;

                    case ModbusRegisterType.HoldingRegister:
                        var holdingRegisters = await ReadWithTimeoutAsync(() =>
                            modbusMaster.ReadHoldingRegisters(addressInfo.SlaveId, (ushort)addressInfo.Address, GetRegisterCount<T>()));
                        result = ConvertRegistersToValue<T>(holdingRegisters);
                        break;

                    default:
                        throw new NotSupportedException($"不支持的寄存器类型: {addressInfo.RegisterType}");
                }

                var convertedResult = (T)Convert.ChangeType(result, typeof(T));
                _logger.LogDebug("【ModbusTCP读取成功】设备={DeviceId} 命令={Command} 值={Value}", deviceId, command, convertedResult);
                return convertedResult;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "【ModbusTCP读取异常】设备={DeviceId} 命令={Command}", deviceId, command);
                throw;
            }
        }

        /// <summary>
        /// 带超时控制的读取操作
        /// </summary>
        private async Task<TResult> ReadWithTimeoutAsync<TResult>(Func<TResult> readOperation)
        {
            var cts = new CancellationTokenSource(OPERATION_TIMEOUT_MS);
            try
            {
                return await Task.Run(readOperation, cts.Token);
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException($"Modbus读取操作超时（{OPERATION_TIMEOUT_MS}ms）");
            }
            finally
            {
                cts.Dispose();
            }
        }

        /// <summary>
        /// 写入ModbusTCP数据
        /// </summary>
        private async Task WriteModbusDataAsync<T>(IModbusMaster modbusMaster, ModbusAddressInfo addressInfo, T value, string deviceId = "", string command = "")
        {
            try
            {
                _logger.LogDebug("【ModbusTCP写入】设备={DeviceId} 命令={Command} 地址={Address} 值={Value}",
                    deviceId, command, addressInfo.Address, value);

                // *** 添加操作前连接验证 ***
                if (modbusMaster == null)
                {
                    throw new InvalidOperationException("ModbusMaster为null");
                }

                switch (addressInfo.RegisterType)
                {
                    case ModbusRegisterType.Coil:
                        var coilValue = Convert.ToBoolean(value);
                        await Task.Run(() => modbusMaster.WriteSingleCoil(addressInfo.SlaveId, (ushort)addressInfo.Address, coilValue));
                        break;

                    case ModbusRegisterType.HoldingRegister:
                        var registers = ConvertValueToRegisters(value);

                        if (registers.Length == 1)
                        {
                            await Task.Run(() => modbusMaster.WriteSingleRegister(addressInfo.SlaveId, (ushort)addressInfo.Address, registers[0]));
                        }
                        else
                        {
                            await Task.Run(() => modbusMaster.WriteMultipleRegisters(addressInfo.SlaveId, (ushort)addressInfo.Address, registers));
                        }
                        break;

                    default:
                        throw new NotSupportedException($"不支持写入的寄存器类型: {addressInfo.RegisterType}");
                }

                _logger.LogDebug("【ModbusTCP写入成功】设备={DeviceId} 命令={Command}", deviceId, command);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "【ModbusTCP写入异常】设备={DeviceId} 命令={Command} 值={Value}", deviceId, command, value);
                throw;
            }
        }

        #endregion

        #region 辅助方法（保持不变）

        private ushort GetRegisterCount<T>()
        {
            return typeof(T) switch
            {
                Type t when t == typeof(bool) => 1,
                Type t when t == typeof(short) || t == typeof(ushort) => 1,
                Type t when t == typeof(int) || t == typeof(uint) || t == typeof(float) => 2,
                Type t when t == typeof(double) => 2,
                Type t when t == typeof(long) || t == typeof(ulong) => 4,
                _ => 1
            };
        }

        private T ConvertRegistersToValue<T>(ushort[] registers)
        {
            if (typeof(T) == typeof(bool))
            {
                return (T)(object)(registers[0] != 0);
            }
            else if (typeof(T) == typeof(short))
            {
                return (T)(object)(short)registers[0];
            }
            else if (typeof(T) == typeof(ushort))
            {
                return (T)(object)registers[0];
            }
            else if (typeof(T) == typeof(int))
            {
                var result = (registers[0] << 16) | registers[1];
                return (T)(object)result;
            }
            else if (typeof(T) == typeof(uint))
            {
                var result = (uint)((registers[0] << 16) | registers[1]);
                return (T)(object)result;
            }
            else if (typeof(T) == typeof(float))
            {
                var bytes = new byte[4];
                bytes[0] = (byte)(registers[1] & 0xFF);
                bytes[1] = (byte)(registers[1] >> 8);
                bytes[2] = (byte)(registers[0] & 0xFF);
                bytes[3] = (byte)(registers[0] >> 8);
                var result = BitConverter.ToSingle(bytes, 0);
                return (T)(object)result;
            }
            else if (typeof(T) == typeof(double))
            {
                if (registers.Length < 2)
                {
                    throw new ArgumentException($"double转换需要至少2个寄存器，实际{registers.Length}个");
                }

                var bytes = new byte[4];
                bytes[0] = (byte)(registers[1] & 0xFF);
                bytes[1] = (byte)(registers[1] >> 8);
                bytes[2] = (byte)(registers[0] & 0xFF);
                bytes[3] = (byte)(registers[0] >> 8);
                var floatResult = BitConverter.ToSingle(bytes, 0);
                var result = (double)floatResult;
                return (T)(object)result;
            }

            throw new NotSupportedException($"不支持的数据类型转换: {typeof(T)}");
        }

        private ushort[] ConvertValueToRegisters<T>(T value)
        {
            if (value is bool boolValue)
            {
                return new ushort[] { (ushort)(boolValue ? 1 : 0) };
            }
            else if (value is short shortValue)
            {
                return new ushort[] { (ushort)shortValue };
            }
            else if (value is ushort ushortValue)
            {
                return new ushort[] { ushortValue };
            }
            else if (value is int intValue)
            {
                return new ushort[] { (ushort)(intValue >> 16), (ushort)(intValue & 0xFFFF) };
            }
            else if (value is uint uintValue)
            {
                return new ushort[] { (ushort)(uintValue >> 16), (ushort)(uintValue & 0xFFFF) };
            }
            else if (value is float floatValue)
            {
                var bytes = BitConverter.GetBytes(floatValue);
                return new ushort[]
                {
                    (ushort)((bytes[3] << 8) | bytes[2]),
                    (ushort)((bytes[1] << 8) | bytes[0])
                };
            }
            else if (value is double doubleValue)
            {
                var floatVal = (float)doubleValue;
                var bytes = BitConverter.GetBytes(floatVal);
                return new ushort[]
                {
                    (ushort)((bytes[3] << 8) | bytes[2]),
                    (ushort)((bytes[1] << 8) | bytes[0])
                };
            }

            throw new NotSupportedException($"不支持的数据类型转换: {typeof(T)}，值: {value}");
        }

        private ModbusAddressInfo ParseModbusAddress(string address)
        {
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
                    SlaveId = 1
                };
            }

            throw new ArgumentException($"无效的Modbus地址格式: {address}");
        }

        private void LoadDeviceMappings()
        {
            _deviceMappings.TryAdd("P0101", new ModbusDeviceMapping
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
                    ["P0101出口流量"] = "40010",
                    ["P0101停止"] = "40018",
                    ["P0101启动"] = "40018",
                    ["P0101泵基准模式"]= "40020",
                    ["P0101泵基准泵速"] = "40024",
                }
            });
            _deviceMappings.TryAdd("P0111", new ModbusDeviceMapping
            {
                ComId = "P0111",
                SlaveId = 1,
                CommandAddresses = new Dictionary<string, string>
                {
                    ["P0101泵速"] = "40039",
                    ["P0101出口泄压压力_上限报警"] = "40041",
                    ["P0101出口预警压力"] = "40042",
                    ["P0101出口温度上限报警"] = "40043",
                    ["P0101出口压力"] = "40044",
                    ["P0101出口温度"] = "40046",
                    ["P0101出口流量"] = "40048",
                    ["P0101停止"] = "40056",
                    ["P0101启动"] = "40056",
                    ["P0101泵基准模式"] = "40058",
                    ["P0101泵基准泵速"] = "40062",
                }
            });
            _deviceMappings.TryAdd("P0121", new ModbusDeviceMapping
            {
                ComId = "P0121",
                SlaveId = 1,
                CommandAddresses = new Dictionary<string, string>
                {
                    ["P0101泵速"] = "40077",
                    ["P0101出口泄压压力_上限报警"] = "40079",
                    ["P0101出口预警压力"] = "40080",
                    ["P0101出口温度上限报警"] = "40081",
                    ["P0101出口压力"] = "40082",
                    ["P0101出口温度"] = "40084",
                    ["P0101出口流量"] = "40086",
                    ["P0101停止"] = "40094",
                    ["P0101启动"] = "40094",
                    ["P0101泵基准模式"] = "40096",
                    ["P0101泵基准泵速"] = "40100",
                }
            });
            _deviceMappings.TryAdd("FT0101", new ModbusDeviceMapping
            {
                ComId = "FT0101",
                SlaveId = 1,
                CommandAddresses = new Dictionary<string, string>
                {
                    ["_FT0101_g_m"] = "40030"
                }
            });
            _deviceMappings.TryAdd("FT0102", new ModbusDeviceMapping
            {
                ComId = "FT0102",
                SlaveId = 1,
                CommandAddresses = new Dictionary<string, string>
                {
                    ["_FT0101_g_m"] = "40068"
                }
            });
            _deviceMappings.TryAdd("FT0103", new ModbusDeviceMapping
            {
                ComId = "FT0102",
                SlaveId = 1,
                CommandAddresses = new Dictionary<string, string>
                {
                    ["_FT0101_g_m"] = "40106"
                }
            });
            // *** 添加电磁阀映射 - 8个独立设备 ***
            _deviceMappings.TryAdd("SV0101", new ModbusDeviceMapping
            {
                ComId = "SV0101",
                SlaveId = 1,
                CommandAddresses = new Dictionary<string, string>
                {
                    ["SV0101手动启停"] = "40016"
                }
            });

            _deviceMappings.TryAdd("SV0102", new ModbusDeviceMapping
            {
                ComId = "SV0102",
                SlaveId = 1,
                CommandAddresses = new Dictionary<string, string>
                {
                    ["SV0102手动启停"] = "40017"
                }
            });

            _deviceMappings.TryAdd("SV0111", new ModbusDeviceMapping
            {
                ComId = "SV0111",
                SlaveId = 1,
                CommandAddresses = new Dictionary<string, string>
                {
                    ["SV0111手动启停"] = "40054"
                }
            });

            _deviceMappings.TryAdd("SV0112", new ModbusDeviceMapping
            {
                ComId = "SV0112",
                SlaveId = 1,
                CommandAddresses = new Dictionary<string, string>
                {
                    ["SV0112手动启停"] = "40055"
                }
            });

            _deviceMappings.TryAdd("SV0121", new ModbusDeviceMapping
            {
                ComId = "SV0121",
                SlaveId = 1,
                CommandAddresses = new Dictionary<string, string>
                {
                    ["SV0121手动启停"] = "40092"
                }
            });

            _deviceMappings.TryAdd("SV0122", new ModbusDeviceMapping
            {
                ComId = "SV0122",
                SlaveId = 1,
                CommandAddresses = new Dictionary<string, string>
                {
                    ["SV0122手动启停"] = "40093"
                }
            });

            _deviceMappings.TryAdd("SV0113", new ModbusDeviceMapping
            {
                ComId = "SV0113",
                SlaveId = 1,
                CommandAddresses = new Dictionary<string, string>
                {
                    ["SV0113手动启停"] = "40131"
                }
            });

            _deviceMappings.TryAdd("SV0114", new ModbusDeviceMapping
            {
                ComId = "SV0114",
                SlaveId = 1,
                CommandAddresses = new Dictionary<string, string>
                {
                    ["SV0114手动启停"] = "40135"
                }
            });
            // *** 添加天平映射 - 三个独立设备 ***
            _deviceMappings.TryAdd("S0101", new ModbusDeviceMapping
            {
                ComId = "S0101",
                SlaveId = 1,
                CommandAddresses = new Dictionary<string, string>
                {
                    ["_S0101_g"] = "40012",
                    ["_S0101_Add_g"] = "40014"
                }
            });

            _deviceMappings.TryAdd("S0111", new ModbusDeviceMapping
            {
                ComId = "S0111",
                SlaveId = 1,
                CommandAddresses = new Dictionary<string, string>
                {
                    ["_S0111_g"] = "40050",
                    ["_S0111_Add_g"] = "40052"
                }
            });

            _deviceMappings.TryAdd("S0121", new ModbusDeviceMapping
            {
                ComId = "S0121",
                SlaveId = 1,
                CommandAddresses = new Dictionary<string, string>
                {
                    ["_S0121_g"] = "40088",
                    ["_S0121_Add_g"] = "40090"
                }
            });

            // *** 添加高低温设备映射 - 两个独立设备 *** 
            _deviceMappings.TryAdd("R0101", new ModbusDeviceMapping
            {
                ComId = "R0101",
                SlaveId = 1,
                CommandAddresses = new Dictionary<string, string>
                {
                    ["TT1001_℃"] = "40118",
                    ["TT1002_℃"] = "40120",
                    ["TT1003_℃"] = "40122",
                    ["PT0112_Mpa"] = "40124",
                    ["TT0112_℃"] = "40126",
                }
            });
            _deviceMappings.TryAdd("R0201", new ModbusDeviceMapping
            {
                ComId = "R0201",
                SlaveId = 1,
                CommandAddresses = new Dictionary<string, string>
                {
                    ["TT1001_℃"] = "40140",
                    ["TT1002_℃"] = "40142",
                    ["TT1003_℃"] = "0000",
                    ["PT0112_Mpa"] = "40144",
                    ["TT0112_℃"] = "40146",
                }
            });
            _deviceMappings.TryAdd("R0102", new ModbusDeviceMapping
            {
                ComId = "R0102",
                SlaveId = 1,
                CommandAddresses = new Dictionary<string, string>
                {
                    ["PT0113_Mpa"] = "40136",
                    ["TT0113_℃"] = "40138",
                }
            });

            // *** 添加换热器设备映射 - 两个独立设备 ***
            _deviceMappings.TryAdd("H0101", new ModbusDeviceMapping
            {
                ComId = "H0101",
                SlaveId = 1,
                CommandAddresses = new Dictionary<string, string>
                {
                    ["PT0113_Mpa"] = "40136",
                    ["TT0113_℃"] = "40138"
                }
            });
            // *** 添加高低温循环器设备映射 - 两个独立设备 ***
            _deviceMappings.TryAdd("TC0101", new ModbusDeviceMapping
            {
                ComId = "TC0101",
                SlaveId = 1,
                CommandAddresses = new Dictionary<string, string>
                {
                    // 循环器1
                    ["YY_SV_Set_1"] = "40153",
                    ["YY_PV_Real_2"] = "40155",
                    ["YY_Cycle_1"] = "40157",
                    ["YY_Heat_1"] = "40158",
                    ["YY_Cool_1"] = "40159"
                }
            });

            _deviceMappings.TryAdd("TC0201", new ModbusDeviceMapping
            {
                ComId = "TC0201",
                SlaveId = 1,
                CommandAddresses = new Dictionary<string, string>
                {
                    // 循环器2
                    ["YY_SV_Set_2"] = "40160",
                    ["YY_PV_Real_4"] = "40162",
                    ["YY_Cycle_2"] = "40164",
                    ["YY_Heat_2"] = "40165",
                    ["YY_Cool_2"] = "40166"
                }
            });
            //添加电子称映射
            _deviceMappings.TryAdd("S0101", new ModbusDeviceMapping
            {
                ComId = "S0101",
                SlaveId = 1,
                CommandAddresses = new Dictionary<string, string>
                {
                    // 循环器2
                    ["_S0101_g"] = "40012",
                    ["_S0101_Add_g"] = "40014",
                   
                }
            });
            _deviceMappings.TryAdd("S0111", new ModbusDeviceMapping
            {
                ComId = "S0111",
                SlaveId = 1,
                CommandAddresses = new Dictionary<string, string>
                {
                    // 循环器2
                    ["_S0111_g"] = "40050",
                    ["_S0111_Add_g"] = "40052",

                }
            });
            _deviceMappings.TryAdd("S0121", new ModbusDeviceMapping
            {
                ComId = "S0121",
                SlaveId = 1,
                CommandAddresses = new Dictionary<string, string>
                {
                    // 循环器2
                    ["_S0121_g"] = "40088",
                    ["_S0121_Add_g"] = "40090",

                }
            });

            _deviceMappings.TryAdd("HLT0001", new ModbusDeviceMapping()
            {
                ComId = "HLT0001",
                SlaveId = 1,
                CommandAddresses = new Dictionary<string, string>()
                {
                    ["出口温度"]="40011",
                    ["物料温度"]="40013",
                    ["回口温度"]="40015",
                    ["温度SV"]="40114",
                    ["启动/停止"]="5",
                    ["启动界面"]="4",
                    ["停止界面"]="6",
                    ["程式启动/停止"]="8",
                    ["程式启动界面"]="7",
                    ["程式停止界面"]="9",
                    ["运行模式"]="2",
                    ["报警数据38"]="11"
                }
            });

            _logger.LogInformation("已加载 {Count} 个设备的ModbusTcp地址映射", _deviceMappings.Count);
        }

        #endregion

        public void Dispose()
        {
            try
            {
                _globalLock?.Dispose();
                _logger.LogInformation("ModbusTcp连接管理器已释放");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "释放ModbusTcp连接管理器失败");
            }
        }
    }

    // 其他类定义保持不变...
    public class ModbusAddressInfo
    {
        public int Address { get; set; }
        public ModbusRegisterType RegisterType { get; set; }
        public byte SlaveId { get; set; } = 1;
    }

    public enum ModbusRegisterType
    {
        Coil = 1,
        DiscreteInput = 2,
        InputRegister = 3,
        HoldingRegister = 4
    }

    public class ModbusDeviceMapping
    {
        public string ComId { get; set; }
        public byte SlaveId { get; set; } = 1;
        public Dictionary<string, string> CommandAddresses { get; set; } = new Dictionary<string, string>();
    }
}
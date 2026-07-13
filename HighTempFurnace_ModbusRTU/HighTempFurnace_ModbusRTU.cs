using DevicePlugins.Devices;
using MaxChemical.Core;
using MaxChemical.Logging;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel.Composition;
using System.Diagnostics;

namespace HighTempFurnace_ModbusRTU
{
    /// <summary>
    /// 高温炉驱动（宇电 AI 温控器，Modbus RTU，功能码 03 读 / 06 写单寄存器）。
    /// 通讯表(设备地址默认 2):
    ///   开启(运行)  → 写 0x001B = 0        （Srun: 0=run,1=stop,2=hold）
    ///   停止        → 写 0x001B = 1
    ///   设置温度     → 写 0x0000 给定值 = 温度×10（dPt=1 一位小数）
    ///   实时温度 PV  → 读 0x004A（只读 short），显示值 = 原始值 ÷ 10
    /// CRC 由 Crc16Modbus 自动计算,串口/网关通信复用基类 Transport。
    /// </summary>
    [Export(typeof(IDevice))]
    public class HighTempFurnace_ModbusRTU : Device, IDeviceScreen
    {
        private readonly ILogService _logger;

        /// <summary>嵌入式屏幕标识,Designer 侧 DeviceScreenRegistry 按此创建高温炉控制屏。</summary>
        public string ScreenKey => "HighTempFurnaceScreen";

        // 模拟模式状态:让屏幕在无硬件时也能完整演示(设温→PV 逐渐逼近)
        private double _simSv = 300;
        private double _simPv = 25;
        private bool _simRun;

        // 实例级互斥锁:同一设备实例的所有 IO 串行化,避免并发撞车(按 DeviceId 隔离)
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> _instanceLocks
            = new ConcurrentDictionary<string, SemaphoreSlim>();

        // 通信层重试参数
        private const int MaxRetries = 3;
        private const int RetryDelayMs = 100;

        // 温度小数点:dPt=1 → 一位小数,寄存器值 = 显示温度 × 10
        private const double TempScale = 10.0;

        public HighTempFurnace_ModbusRTU()
        {
            _logger = LogManager.GetLogger<HighTempFurnace_ModbusRTU>();

            DeviceId = "FUR0001";
            Name = "高温炉";
            Manufacturer = "ModbusRTU高温炉(宇电温控)";
            ImageLocation = "pack://siteoforigin:,,,/Resources/DeviceIcon/highlowtemperature1.png";
            Category = DeviceCategories.PublicWorks;
            ComId = "FUR0001";
            ConnectionManager = DeviceConnectionManagerFactory.GetInstance();
            AllowedRegions = new List<RegionType>()
            {
                RegionType.Feed,
                RegionType.PreHeat,
                RegionType.Reaction,
                RegionType.Quench,
                RegionType.PostProcess
            };

            Parameters.Variables.Add(new StringParameter("DeviceId", "FUR0001", "ModbusRTU设备ID")
            {
                Options = new ObservableCollection<string>() { "FUR0001", "FUR0002", "FUR0003", "FUR0004", "FUR0005" },
            });

            Parameters.Variables.Add(new StringParameter("串口号", "COM1")
            {
                Options = new ObservableCollection<string>() { "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7" }
            });

            Parameters.Variables.Add(new StringParameter("波特率", "9600")
            {
                Options = new ObservableCollection<string>() { "4800", "9600", "19200", "38400", "57600", "115200", "230400" }
            });

            Parameters.Variables.Add(new StringParameter("校验位", "None")
            {
                Options = new ObservableCollection<string>() { "None", "Odd", "Even", "Mark", "Space" }
            });

            Parameters.Variables.Add(new StringParameter("数据位", "8")
            {
                Options = new ObservableCollection<string>() { "5", "6", "7", "8" }
            });

            Parameters.Variables.Add(new StringParameter("停止位", "One")
            {
                Options = new ObservableCollection<string>() { "None", "One", "Two", "OnePointFive" }
            });

            // 通信方式
            Parameters.Variables.Add(new StringParameter("通信方式", "Direct", "通信方式")
            {
                Options = new ObservableCollection<string>() { "Direct", "PLC", "ModbusTcp", "ZLanGateway", "RemoteServer" }
            });
            // 自建云服务器参数（仅"通信方式=RemoteServer"时生效）
            Parameters.Variables.Add(new StringParameter("DTU序列号", "", "DTU 登录包(序列号)，需与云服务器侧该设备的 DTU 一致"));

            // Modbus 从机站号 (1~254);宇电温控表里默认地址为 2
            Parameters.Variables.Add(new NumberParameter("Modbus站号", 1, 254, 2, "Modbus 从机站号 (1~254)"));

            InitializeCommands();
        }

        /// <summary>声明本驱动支持通过 ZLAN 网关通信。</summary>
        public override bool SupportsZLanGateway => true;

        private void InitializeCommands()
        {
            // 开启(运行):0x1B 写 0
            Commands.Add(new DeviceCommand()
            {
                Name = "开启",
                HelpText = "高温炉开启(运行):写寄存器 0x1B = 0",
                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new BooleanParameter("Success", false, "开启成功", true)
                    }
                },
                AsyncAction = AsyncWrapAction("开启", async parameter =>
                {
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        bool success;
                        if (IsSimulationMode)
                        {
                            await Task.Delay(500);
                            success = true;
                            _simRun = true;
                            InfoLog("模拟模式:高温炉开启");
                        }
                        else
                        {
                            byte[] buffers = BuildWriteCmd(register: 0x001B, value: 0x0000);
                            success = await WriteAsync(buffers, 0, buffers.Length, CancellationToken.None);
                            InfoLog($"高温炉开启,结果(Success:{success})");
                        }
                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new BooleanParameter("Success", success, "开启成功", true)
                            }
                        };
                    }
                    catch (Exception e)
                    {
                        ErrorLog($"高温炉开启失败:{e.Message}");
                        throw;
                    }
                })
            });

            // 停止:0x1B 写 1
            Commands.Add(new DeviceCommand()
            {
                Name = "停止",
                HelpText = "高温炉停止:写寄存器 0x1B = 1",
                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new BooleanParameter("Success", false, "停止成功", true)
                    }
                },
                AsyncAction = AsyncWrapAction("停止", async parameter =>
                {
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        bool success;
                        if (IsSimulationMode)
                        {
                            await Task.Delay(500);
                            success = true;
                            _simRun = false;
                            InfoLog("模拟模式:高温炉停止");
                        }
                        else
                        {
                            byte[] buffers = BuildWriteCmd(register: 0x001B, value: 0x0001);
                            success = await WriteAsync(buffers, 0, buffers.Length, CancellationToken.None);
                            InfoLog($"高温炉停止,结果(Success:{success})");
                        }
                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new BooleanParameter("Success", success, "停止成功", true)
                            }
                        };
                    }
                    catch (Exception e)
                    {
                        ErrorLog($"高温炉停止失败:{e.Message}");
                        throw;
                    }
                })
            });

            // 设置温度:0x00 给定值 写 温度×10
            Commands.Add(new DeviceCommand()
            {
                Name = "设置温度",
                HelpText = "设置高温炉目标温度(给定值):写寄存器 0x00 = 温度×10",
                InputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new NumberParameter("TargetTemperature", 0, 1300, 0, "目标温度", false, "℃")
                    }
                },
                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new BooleanParameter("Success", false, "设置成功", true),
                        new NumberParameter("SetTemperature", 0, 1300, 0, "设定温度", true, "℃")
                    }
                },
                AsyncAction = AsyncWrapAction("设置温度", async parameter =>
                {
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        double target = parameter.GetValue<float>("TargetTemperature");
                        bool success;
                        if (IsSimulationMode)
                        {
                            await Task.Delay(500);
                            success = true;
                            _simSv = target;
                            InfoLog($"模拟模式:设置高温炉温度 {target}℃");
                        }
                        else
                        {
                            int reg = (int)Math.Round(target * TempScale);
                            if (reg < 0) reg = 0;
                            if (reg > 0xFFFF) reg = 0xFFFF;
                            byte[] buffers = BuildWriteCmd(register: 0x0000, value: (ushort)reg);
                            success = await WriteAsync(buffers, 0, buffers.Length, CancellationToken.None);
                            InfoLog($"设置高温炉温度,结果(Success:{success}, 目标:{target}℃)");
                        }
                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new BooleanParameter("Success", success, "设置成功", true),
                                new NumberParameter("SetTemperature", 0, 1300, target, "设定温度", true, "℃")
                            }
                        };
                    }
                    catch (Exception e)
                    {
                        ErrorLog($"设置高温炉温度失败:{e.Message}");
                        throw;
                    }
                })
            });

            // 实时温度 PV:读 0x4A,显示 = 原始值 ÷ 10
            Commands.Add(new DeviceCommand()
            {
                Name = "获取实时温度",
                HelpText = "读高温炉实时温度 PV:读寄存器 0x4A(只读 short),显示值 = 原始值÷10",
                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new NumberParameter("PV", 0, 1300, 0, "实时温度", true, "℃")
                    }
                },
                AsyncAction = AsyncWrapAction("获取实时温度", async parameter =>
                {
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        double pv;
                        if (IsSimulationMode)
                        {
                            await Task.Delay(500);
                            pv = new Random().Next(200, 4300) / TempScale;
                            InfoLog($"模拟模式:高温炉实时温度 {pv}℃");
                        }
                        else
                        {
                            byte[] sendCmd = BuildReadCmd(register: 0x004A, count: 0x0001);
                            short raw = await SendAndParseInt16Async(sendCmd, CancellationToken.None);
                            pv = raw / TempScale;
                            InfoLog($"高温炉实时温度 PV:{pv}℃(原始 {raw})");
                        }
                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new NumberParameter("PV", 0, 1300, pv, "实时温度", true, "℃")
                            }
                        };
                    }
                    catch (Exception e)
                    {
                        ErrorLog($"获取高温炉实时温度失败:{e.Message}");
                        throw;
                    }
                })
            });

            // 获取可视化数据(设备屏幕轮询用):一次返回 PV / SV / 运行状态
            Commands.Add(new DeviceCommand()
            {
                Name = "获取可视化数据",
                HelpText = "屏幕轮询:读 PV(0x4A)、SV(0x00)、运行状态(0x1B,0=运行)",
                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new NumberParameter("PV", -100, 1400, 0, "实时温度", true, "℃"),
                        new NumberParameter("SV", 0, 1300, 0, "温度设定值", true, "℃"),
                        new BooleanParameter("Run", false, "运行中", true)
                    }
                },
                AsyncAction = AsyncWrapAction("获取可视化数据", async parameter =>
                {
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        double pv, sv;
                        bool run;
                        if (IsSimulationMode)
                        {
                            await Task.Delay(300);
                            var rnd = new Random();
                            if (_simRun)
                                _simPv += (_simSv - _simPv) * 0.08 + (rnd.NextDouble() - 0.5) * 1.2;
                            else
                                _simPv += (25 - _simPv) * 0.03 + (rnd.NextDouble() - 0.5) * 0.4;
                            pv = Math.Round(_simPv, 1);
                            sv = _simSv;
                            run = _simRun;
                            InfoLog($"模拟模式:高温炉可视化数据 PV={pv}℃ SV={sv}℃ Run={run}");
                        }
                        else
                        {
                            // 三个寄存器不连续,分三次 FC03 读取(实例锁内串行,链路安全)
                            short rawPv = await SendAndParseInt16Async(BuildReadCmd(0x004A, 0x0001), CancellationToken.None);
                            short rawSv = await SendAndParseInt16Async(BuildReadCmd(0x0000, 0x0001), CancellationToken.None);
                            short rawRun = await SendAndParseInt16Async(BuildReadCmd(0x001B, 0x0001), CancellationToken.None);
                            pv = rawPv / TempScale;
                            sv = rawSv / TempScale;
                            run = rawRun == 0;          // Srun: 0=run, 1=stop, 2=hold
                            InfoLog($"高温炉可视化数据 PV={pv}℃ SV={sv}℃ Srun={rawRun}");
                        }
                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new NumberParameter("PV", -100, 1400, pv, "实时温度", true, "℃"),
                                new NumberParameter("SV", 0, 1300, sv, "温度设定值", true, "℃"),
                                new BooleanParameter("Run", run, "运行中", true)
                            }
                        };
                    }
                    catch (Exception e)
                    {
                        ErrorLog($"获取高温炉可视化数据失败:{e.Message}");
                        throw;
                    }
                })
            });
        }

        #region 命令包装(日志 + 异常封装)

        /// <summary>把命令执行体统一包一层:开始/完成/异常日志 + 异常封装成 DeviceCommandExecutionException。</summary>
        private Func<DeviceParameters, Task<DeviceParameters>> AsyncWrapAction(
            string commandName, Func<DeviceParameters, Task<DeviceParameters>> innerAsync)
        {
            return async (DeviceParameters parameters) =>
            {
                _logger.LogInformation($"[高温炉命令-开始] Device={Name} Command={commandName}");
                var sw = Stopwatch.StartNew();
                try
                {
                    var output = await innerAsync(parameters);
                    sw.Stop();
                    _logger.LogInformation($"[高温炉命令-完成] Device={Name} Command={commandName} DurationMs={sw.ElapsedMilliseconds}");
                    return output;
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    _logger.LogError(ex, $"[高温炉命令-异常] Device={Name} Command={commandName} DurationMs={sw.ElapsedMilliseconds}");
                    throw new DeviceCommandExecutionException(Name, commandName, ex.Message, ex);
                }
            };
        }

        #endregion

        #region Modbus RTU 底层(复用与平流泵一致的收发/CRC)

        /// <summary>取当前配置的 Modbus 从机站号 (1~254)。</summary>
        private byte GetSlaveAddress()
        {
            try
            {
                var addr = (int)Parameters.GetValue<double>("Modbus站号");
                if (addr < 1 || addr > 254) addr = 2;
                return (byte)addr;
            }
            catch
            {
                return 2;
            }
        }

        /// <summary>获取本设备实例专属的互斥锁(按 DeviceId 隔离,IO 串行化)。</summary>
        private SemaphoreSlim GetInstanceLock()
        {
            var key = Parameters?.GetValue<string>("DeviceId") ?? DeviceId ?? "default";
            return _instanceLocks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        }

        /// <summary>构造 Modbus RTU 写单寄存器命令 (功能码 0x06),自动加 CRC。</summary>
        private byte[] BuildWriteCmd(ushort register, ushort value)
        {
            byte[] frame = new byte[]
            {
                GetSlaveAddress(),
                0x06,
                (byte)(register >> 8), (byte)(register & 0xFF),
                (byte)(value >> 8),    (byte)(value & 0xFF),
            };
            return Crc16Modbus.AppendCrc(frame, 0, frame.Length);
        }

        /// <summary>构造 Modbus RTU 读保持寄存器命令 (功能码 0x03),自动加 CRC。</summary>
        private byte[] BuildReadCmd(ushort register, ushort count)
        {
            byte[] frame = new byte[]
            {
                GetSlaveAddress(),
                0x03,
                (byte)(register >> 8), (byte)(register & 0xFF),
                (byte)(count >> 8),    (byte)(count & 0xFF),
            };
            return Crc16Modbus.AppendCrc(frame, 0, frame.Length);
        }

        /// <summary>写命令。自动重试 3 次以应对工业链路偶发抖动。</summary>
        private async Task<bool> WriteAsync(byte[] buffers, int offset, int count, CancellationToken token)
        {
            Exception? lastException = null;
            for (int attempt = 1; attempt <= MaxRetries; attempt++)
            {
                try
                {
                    return await WriteOnceAsync(buffers, offset, count, token).ConfigureAwait(false);
                }
                catch (TimeoutException ex)
                {
                    lastException = ex;
                    if (attempt < MaxRetries)
                    {
                        _logger?.LogWarning("Modbus 写入超时 (第 {Attempt}/{Total} 次),{Delay}ms 后重试",
                            attempt, MaxRetries, RetryDelayMs);
                        await Task.Delay(RetryDelayMs, token).ConfigureAwait(false);
                    }
                }
            }
            throw new TimeoutException($"Modbus 写入失败,{MaxRetries} 次尝试后仍超时", lastException);
        }

        private async Task<bool> WriteOnceAsync(byte[] buffers, int offset, int count, CancellationToken token)
        {
            var sem = GetInstanceLock();
            await sem.WaitAsync(token).ConfigureAwait(false);
            try
            {
                using var transport = await GetTransportAsync().ConfigureAwait(false);
                return await transport.SendAsync(buffers, offset, count, token).ConfigureAwait(false);
            }
            finally
            {
                sem.Release();
            }
        }

        /// <summary>发送 Modbus 读寄存器命令并解析应答中的 Int16 数据(自动重试 3 次)。</summary>
        private async Task<short> SendAndParseInt16Async(byte[] sendCmd, CancellationToken token)
        {
            Exception? lastException = null;
            for (int attempt = 1; attempt <= MaxRetries; attempt++)
            {
                try
                {
                    return await SendAndParseInt16OnceAsync(sendCmd, token).ConfigureAwait(false);
                }
                catch (TimeoutException ex)
                {
                    lastException = ex;
                    if (attempt < MaxRetries)
                    {
                        _logger?.LogWarning("Modbus 读取超时 (第 {Attempt}/{Total} 次),{Delay}ms 后重试",
                            attempt, MaxRetries, RetryDelayMs);
                        await Task.Delay(RetryDelayMs, token).ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Modbus 读取失败(非超时,不重试)");
                    throw;
                }
            }
            throw new TimeoutException($"Modbus 读取失败,{MaxRetries} 次尝试后仍超时", lastException);
        }

        private async Task<short> SendAndParseInt16OnceAsync(byte[] sendCmd, CancellationToken token)
        {
            var sem = GetInstanceLock();
            await sem.WaitAsync(token).ConfigureAwait(false);
            try
            {
                byte[] recv = new byte[256];
                using var transport = await GetTransportAsync().ConfigureAwait(false);
                int len = await transport.SendAndReceiveAsync(sendCmd, recv, 3000, token).ConfigureAwait(false);

                // 应答最短:站号(1)+功能码(1)+长度(1)+数据(2)+CRC(2) = 7 字节
                if (len < 7)
                {
                    _logger?.LogWarning("应答帧长度不足:{Len} 字节,需要至少 7 字节", len);
                    return 0;
                }

                byte expectedAddr = GetSlaveAddress();
                if (recv[0] != expectedAddr)
                {
                    _logger?.LogWarning("站号不匹配:期望 0x{Expected:X2},实际 0x{Actual:X2}", expectedAddr, recv[0]);
                    return 0;
                }
                if ((recv[1] & 0x80) != 0)
                {
                    _logger?.LogWarning("Modbus 异常应答,异常码 0x{Code:X2}", recv[2]);
                    return 0;
                }
                if (recv[1] != 0x03)
                {
                    _logger?.LogWarning("功能码不匹配:期望 0x03,实际 0x{Actual:X2}", recv[1]);
                    return 0;
                }

                byte[] data = ExtractPayload(recv);
                if (BitConverter.IsLittleEndian) ReverseArray(data);
                return BitConverter.ToInt16(data, 0);
            }
            finally
            {
                sem.Release();
            }
        }

        /// <summary>通过基类的 CreateTransportAsync 获取 Transport(串口/网关)。</summary>
        private Task<IDeviceTransport> GetTransportAsync()
        {
            var serialConfig = new SerialPortConfig
            {
                PortName = Parameters.GetValue<string>("串口号") ?? "COM1",
                BaudRate = int.Parse(Parameters.GetValue<string>("波特率") ?? "9600"),
                Parity = Parameters.GetValue<string>("校验位") ?? "None",
                DataBits = int.Parse(Parameters.GetValue<string>("数据位") ?? "8"),
                StopBits = Parameters.GetValue<string>("停止位") ?? "One",
                ReadTimeoutMs = 3000,
                WriteTimeoutMs = 3000,
            };
            return CreateTransportAsync(serialConfig);
        }

        /// <summary>从 Modbus RTU 应答帧中提取数据载荷(去掉地址、功能码、长度、CRC)。</summary>
        private byte[] ExtractPayload(byte[] frame)
        {
            if (frame == null || frame.Length < 5)
                throw new ArgumentException("报文长度无效");
            if (frame[1] >= 0x80)
                throw new ArgumentException($"Modbus 异常响应,异常码:{frame[2]:X2}");

            byte dataLen = frame[2];
            int dataStartIndex = 3;
            int crcIndex = dataStartIndex + dataLen;
            if (frame.Length < crcIndex + 2)
                throw new ArgumentException("Modbus RTU 报文长度不匹配");

            byte[] data = new byte[dataLen];
            Array.Copy(frame, dataStartIndex, data, 0, dataLen);
            return data;
        }

        /// <summary>原地反转字节数组(用于大端/小端转换)。</summary>
        private void ReverseArray(byte[] buffers)
        {
            if (buffers == null || buffers.Length == 0)
                throw new ArgumentException("输入数据为空", nameof(buffers));
            int middle = buffers.Length / 2;
            int length = buffers.Length - 1;
            for (int i = 0; i < middle; i++)
            {
                byte temp = buffers[i];
                buffers[i] = buffers[length - i];
                buffers[length - i] = temp;
            }
        }

        #endregion
    }

    /// <summary>CRC16-Modbus 校验工具。多项式 0xA001,初始值 0xFFFF。</summary>
    public static class Crc16Modbus
    {
        public static ushort Compute(byte[] data, int offset, int length)
        {
            ushort crc = 0xFFFF;
            for (int i = offset; i < offset + length; i++)
            {
                crc ^= data[i];
                for (int j = 0; j < 8; j++)
                {
                    bool lsb = (crc & 1) != 0;
                    crc >>= 1;
                    if (lsb) crc ^= 0xA001;
                }
            }
            return crc;
        }

        public static byte[] AppendCrc(byte[] data, int offset, int length)
        {
            ushort crc = Compute(data, offset, length);
            byte[] crcBytes = BitConverter.GetBytes(crc);
            byte[] result = new byte[length + 2];
            Buffer.BlockCopy(data, offset, result, 0, length);
            result[length] = crcBytes[0];
            result[length + 1] = crcBytes[1];
            return result;
        }

        public static bool Verify(byte[] data)
        {
            if (data.Length < 2) return false;
            int dataLen = data.Length - 2;
            ushort crc = Compute(data, 0, dataLen);
            byte[] crcBytes = BitConverter.GetBytes(crc);
            return data[dataLen] == crcBytes[0] && data[dataLen + 1] == crcBytes[1];
        }
    }
}

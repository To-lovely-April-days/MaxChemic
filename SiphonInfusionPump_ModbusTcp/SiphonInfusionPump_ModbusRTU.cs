using DevicePlugins.Devices;
using MaxChemical.Core;
using MaxChemical.Logging;
using System.Collections.ObjectModel;
using System.ComponentModel.Composition;
using System.Data;
using System.Diagnostics;
using System.IO.Ports;

namespace SiphonInfusionPump_ModbusTcp
{
    [Export(typeof(IDevice))]
    public class SiphonInfusionPump_ModbusRTU : Device, IFlowLifecycleAware
    {
        private readonly ILogService _logger;

        // 实时压力轮询任务相关
        private Task _dataTask;
        private CancellationTokenSource _dataCts;

        // 限制每次只能打开一个串口
        private readonly SemaphoreSlim _slim = new SemaphoreSlim(1, 1);


        public SiphonInfusionPump_ModbusRTU()
        {
            _logger = LogManager.GetLogger<SiphonInfusionPump_ModbusRTU>();

            DeviceId = "SIP0001";
            Name = "平流输液泵";
            Manufacturer = "ModbusRTU平流输液泵";
            ImageLocation = "pack://siteoforigin:,,,/Resources/DeviceIcon/PeristalticPumps_CY.png";
            Category = DeviceCategories.Pumps;
            ComId = "SIP0001"; // 默认设备ID
            ConnectionManager = DeviceConnectionManagerFactory.GetInstance();
            AllowedRegions = new List<RegionType>()
            {
                RegionType.Feed,
                RegionType.PreHeat,
                RegionType.Reaction,
                RegionType.Quench,
                RegionType.PostProcess
            };

            Parameters.Variables.Add(new StringParameter("DeviceId", "SIP0001", "ModbusRTU设备ID")
            {
                Options = new ObservableCollection<string>() { "SIP0001", "SIP0002", "SIP0003", "SIP0004", "SIP0005" },
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
                Options = new ObservableCollection<string>() { "None", "Odd", "Even", "Mark", "Space"}
            });

            Parameters.Variables.Add(new StringParameter("数据位", "8")
            {
                Options = new ObservableCollection<string>() { "5", "6", "7", "8" }
            });

            Parameters.Variables.Add(new StringParameter("停止位", "One")
            {
                Options = new ObservableCollection<string>() { "None", "One", "Two", "OnePointFive" }
            });

            // 初始化设备命令
            InitializeCommands();
        }


        /// <summary>
        /// 初始化命令
        /// </summary>
        private void InitializeCommands()
        {
            // 停止
            Commands.Add(new DeviceCommand()
            {
                Name = "停止泵",
                ImageLocation = "",
                HelpText = "停止平流输液泵",

                OutputParameters = new DeviceParameters()
                {
                    Variables=new ObservableCollection<ParameterBase>()
                    {
                        new BooleanParameter("Success", true, "平流输液泵停止成功", true)
                    }
                },

                Action = WrapAction("停止泵", async parameter =>
                {
                    OnStateChanged("CommandState_停止水平输液泵", CommandExecutionState.Running);
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        bool success = false;


                        if (IsSimulationMode)
                        {
                            await Task.Delay(2000);
                            InfoLog("模拟模式停止水平输液泵");
                        }
                        //else if (DeviceConnectionConfig.CommunicationMode == CommunicationMode.ModbusTcp && ConnectionManager != null)
                        //{
                        //    success = await ConnectionManager.WriteAsync(ComId, "", false);
                        //    InfoLog($"停止水平输液泵（ModbusTCP地址 设置为0），结果（{success}）");
                        //}
                        else
                        {
                            byte[] buffers = new byte[8];
                            buffers[0] = 85;
                            buffers[1] = 6;
                            buffers[2] = 0;
                            buffers[3] = 7;
                            buffers[4] = 0;
                            buffers[5] = 1;
                            buffers[6] = 244;
                            buffers[7] = 31;

                            success = await WriteAsync(buffers, 0, buffers.Length, CancellationToken.None);
                        }

                        //  通知UI：命令执行结果
                        OnStateChanged("CommandState_停止水平输液泵", success ? CommandExecutionState.Succeeded : CommandExecutionState.Failed);

                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new BooleanParameter("Success", success, "平流输液泵停止成功", true)
                            }
                        };
                    }
                    catch (Exception ex)
                    {
                        OnStateChanged("CommandState_停止水平输液泵", CommandExecutionState.Error);
                        ErrorLog($"平流输液泵停止失败：{ex.Message}");
                        throw;
                    }
                })
            });

            // 启动
            Commands.Add(new DeviceCommand()
            {
                Name = "启动泵",
                ImageLocation = "",
                HelpText = "启动平流输液泵",

                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new BooleanParameter("Success", false, "平流输液泵启动成功", true)
                    }
                },

                Action = WrapAction("启动泵", async parameters =>
                {
                    try
                    {
                        ComId = parameters.GetValue<string>("DeviceId");
                        bool success = false;

                        if (IsSimulationMode)
                        {
                            await Task.Delay(2000);
                            success = true;
                            InfoLog("模拟模式启动水平输液泵");
                        }
                        else
                        {
                            byte[] buffers = new byte[8];
                            buffers[0] = 85;
                            buffers[1] = 6;
                            buffers[2] = 0;
                            buffers[3] = 5;
                            buffers[4] = 0;
                            buffers[5] = 1;
                            buffers[6] = 85;
                            buffers[7] = 223;

                            success = await WriteAsync(buffers, 0, buffers.Length, CancellationToken.None);
                        }

                        //  通知UI：命令执行结果
                        OnStateChanged("CommandState_启动水平输液泵", success ? CommandExecutionState.Succeeded : CommandExecutionState.Failed);

                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new BooleanParameter("Success",false, "平流输液泵启动成功", true)
                            }
                        };
                    }
                    catch (Exception e)
                    {
                        OnStateChanged("CommandState_启动水平输液泵", CommandExecutionState.Error);
                        ErrorLog($"平流输液泵启动失败：{e.Message}");
                        throw;
                    }
                })
            });

            // 获取实时压力
            Commands.Add(new DeviceCommand()
            {
                Name = "获取实时压力",
                ImageLocation = "",
                HelpText = "获取平流输液泵的实时压力",

                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new NumberParameter("Pressure", 0, 5000, 0, "实时压力", true, "Mpa")
                    }
                },

                AsyncAction = AsyncWrapAction("获取实时压力", async parameter =>
                {
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        double pressure = 0;
                        if (IsSimulationMode)
                        {
                            await Task.Delay(2000);
                            pressure = new Random().Next(20, 100);
                            InfoLog($"模拟模式获取平流输液泵实时压力，结果（{pressure}）");
                        }
                        else
                        {
                            byte[] buffers = new byte[1024];
                            int len = await ReadAsync(buffers, 0, buffers.Length, CancellationToken.None);
                            if (len > 0)
                            {
                                byte[] data = ExtractPayload(buffers);
                                if (BitConverter.IsLittleEndian)
                                {
                                    ReverseArray(data);
                                }
                                pressure = BitConverter.ToInt16(data);
                            }
                        }

                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new NumberParameter("Pressure", 0, 5000, pressure, "实时压力", true, "Mpa")
                            }
                        };
                    }
                    catch (Exception e)
                    {
                        ErrorLog($"获取平流输液泵实施压力失败：{e.Message}");
                        throw;
                    }
                })
            });

            // 设置压力上限
            Commands.Add(new DeviceCommand()
            {
                Name = "设置压力上限",
                ImageLocation = "",
                HelpText = "设置平流输液泵压力上限",

                InputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new NumberParameter("PressureUpperLimit", 0, 100, 20, "设置的上限值", false)
                    }
                },

                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new BooleanParameter("Success", true, "设置成功", true),
                        new NumberParameter("SetUpperLimit", 0, 100, 0, "设置的上限值", true)
                    }
                },

                Action = WrapAction("设置压力上限", async parameter =>
                {
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        bool success = false;
                        double upperLimit = parameter.GetValue<double>("PressureUpperLimit");

                        if (IsSimulationMode)
                        {
                            await Task.Delay(2000);
                            success = true;
                            InfoLog($"模拟模式设置平流输液泵压力上限：{upperLimit} Mpa");
                        }
                        else
                        {
                            byte[] data = BitConverter.GetBytes((short)upperLimit);
                            byte[] buffers = new byte[8];
                            buffers[0] = 85;
                            buffers[1] = 6;
                            buffers[2] = 0;
                            buffers[3] = 2;
                            buffers[4] = data[0];
                            buffers[5] = data[1];
                            buffers[6] = 37;
                            buffers[7] = 245;

                            success = await WriteAsync(buffers, 0, buffers.Length, CancellationToken.None);
                        }

                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new BooleanParameter("Success", success, "设置成功", true),
                                new NumberParameter("SetUpperLimit", 0, 100, upperLimit, "设置的上限值", true)
                            }
                        };
                    }
                    catch (Exception e)
                    {
                        ErrorLog($"设置平流输液泵压力上限失败：{e.Message}");
                        throw;
                    }
                })
            });
            
            // 设置压力下限
            Commands.Add(new DeviceCommand()
            {
                Name = "设置压力下限",
                ImageLocation = "",
                HelpText = "设置平流输液泵的最小压力",

                InputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new NumberParameter("PressureLowerLimit", 0, 100, 0, "设置的下限值", false)
                    }
                },

                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new BooleanParameter("Success", true, "设置成功", true),
                        new NumberParameter("SetLowerLimit", 0, 100, 0, "设置的下限值", true)
                    }
                },

                Action = WrapAction("设置压力下限", async parameter =>
                {
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        bool success = false;
                        double lowerLimit = parameter.GetValue<double>("PressureLowerLimit");

                        if (IsSimulationMode)
                        {
                            await Task.Delay(2000);
                            success = true;
                            InfoLog($"模拟模式设置平流输液泵压力下限：{lowerLimit} Mpa");
                        }
                        //else if (DeviceConnectionConfig.CommunicationMode == CommunicationMode.ModbusTcp && ConnectionManager != null)
                        //{
                        //    success = await ConnectionManager.WriteAsync(ComId, "", lowerLimit);
                        //    InfoLog($"设置平流输液泵压力下限（）：{lowerLimit} Mpa，结果（{success}）");
                        //}
                        else
                        {
                            byte[] data = BitConverter.GetBytes((short)lowerLimit);
                            byte[] buffers = new byte[8];
                            buffers[0] = 85;
                            buffers[1] = 6;
                            buffers[2] = 0;
                            buffers[3] = 3;
                            buffers[4] = data[0];
                            buffers[5] = data[1];
                            buffers[6] = 116;
                            buffers[7] = 30;

                            success = await WriteAsync(buffers, 0, buffers.Length, CancellationToken.None);
                        }

                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new BooleanParameter("Success", success, "设置成功", true),
                                new NumberParameter("SetLowerLimit", 0, 100, lowerLimit, "设置的下限值", true)
                            }
                        };
                    }
                    catch (Exception e)
                    {
                        ErrorLog($"设置平流输液泵压力下限失败：{e.Message}");
                        throw;
                    }
                })
            });
        }

        #region 设备接口实现

        protected override void OnInitialize()
        {
            InfoLog("开始初始化平流输液泵");
            try
            {
                base.OnInitialize();

                InfoLog("平流输液泵初始化完成");
            }
            catch (Exception e)
            {
                ErrorLog($"平流输液泵初始化失败", e);
                throw;
            }
        }

        protected override void OnShutdown()
        {
            InfoLog("开始关闭平流输液泵");
            try
            {
                base.OnShutdown();

                InfoLog("平流输液泵关闭完成");
            }
            catch (Exception e)
            {
                ErrorLog($"平流输液泵关闭失败", e);
                throw;
            }
        }

        protected override Task<bool> OnConnectAsync()
        {
            try
            {
                InfoLog("连接平流输液泵");
                
                InfoLog("平流输液泵已连接");
                return Task.FromResult(true);
            }
            catch (Exception e)
            {
                ErrorLog("平流输液泵连接失败", e);
                return Task.FromResult(false);
            }
        }

        protected override Task<bool> OnDisconnectAsync()
        {
            try
            {
                InfoLog("断开平流输液泵的连接");

                InfoLog("已断开平流输液泵的连接");
                return Task.FromResult(true);
            }
            catch (Exception e)
            {
                ErrorLog("平流输液泵连接断开失败", e);
                return Task.FromResult(false);
            }
        }

        #endregion

        private Func<DeviceParameters, DeviceParameters> WrapAction(string commandName, Func<DeviceParameters, Task<DeviceParameters>> innerAsync)
        {
            return (DeviceParameters parameters) =>
            {
                _logger.LogInformation($"[ModbusRTU平流输液泵命令-开始] Device={Name} Command={commandName}");

                var sw = Stopwatch.StartNew();
                try
                {
                    var output = innerAsync(parameters).GetAwaiter().GetResult();
                    sw.Stop();

                    _logger.LogInformation($"[ModbusRTU平流输液泵命令-完成] Device={Name} Command={commandName} DurationMs={sw.ElapsedMilliseconds}");
                    return output;
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    _logger.LogError(ex, $"[ModbusRTU平流输液泵命令-异常] Device={Name} Command={commandName} DurationMs={sw.ElapsedMilliseconds}");
                    throw new DeviceCommandExecutionException(Name, commandName, ex.Message, ex);
                }
            };
        }

        private Func<DeviceParameters, Task<DeviceParameters>> AsyncWrapAction(string commandName, Func<DeviceParameters, Task<DeviceParameters>> innerAsync)
        {
            return async (DeviceParameters parameters) =>
            {
                _logger.LogInformation($"[ModbusRTU平流输液泵命令-开始] Device={Name} Command={commandName}");

                var sw = Stopwatch.StartNew();
                try
                {
                    var output = await innerAsync(parameters);
                    sw.Stop();

                    _logger.LogInformation($"[ModbusRTU平流输液泵命令-完成] Device={Name} Command={commandName} DurationMs={sw.ElapsedMilliseconds}");
                    return output;
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    _logger.LogError(ex, $"[ModbusRTU平流输液泵命令-异常] Device={Name} Command={commandName} DurationMs={sw.ElapsedMilliseconds}");
                    throw new DeviceCommandExecutionException(Name, commandName, ex.Message, ex);
                }
            };
        }

        #region 流程生命周期接口实现

        public async Task<bool> OnFlowStartedAsync(FlowStartContext context)
        {
            try
            {
                InfoLog($"平流输液泵流程开始：{context.FlowName}");
                await StartPressurePollingAsync();
                InfoLog("平流输液泵已准备好执行流程");
                return true;
            }
            catch (Exception e)
            {
                ErrorLog("平流输液泵流程处开始处理失败", e);
                return false;
            }
        }

        public async Task<bool> OnFlowCompletedAsync(FlowCompletionContext context)
        {
            try
            {
                InfoLog($"平流输液泵流程完成：{context.FlowName}，成功：{context.IsSuccessful}");
                await StopPressurePollingAsync();
                InfoLog("平流输液泵流程完成处理结束");
                return true;
            }
            catch (Exception e)
            {
                ErrorLog("平流输液泵流程完成处理失败", e);
                return false;
            }
        }

        public async Task<bool> OnFlowFailedAsync(FlowFailureContext context)
        {
            try
            {
                InfoLog($"平流输液泵流程失败：{context.FlowName}，错误消息：{context.ErrorMessage}");
                await StopPressurePollingAsync();
                InfoLog("平流输液泵流程失败处理结束");
                return true;
            }
            catch (Exception e)
            {
                ErrorLog("平流输液泵流程失败处理异常", e);
                return false;
            }
        }

        #endregion

        #region 实时压力轮询

        private async Task StartPressurePollingAsync()
        {
            try
            {
                InfoLog("实时压力轮询启动");
                if (_dataTask != null && !_dataTask.IsCompleted)
                {
                    InfoLog("已存在实时压力轮询任务，先停止");
                    await StopPressurePollingAsync();
                }

                _dataCts = new CancellationTokenSource();
                _dataTask = Task.Run(async () =>
                {
                    await PressurePollingAsync(_dataCts.Token);
                }, _dataCts.Token);
                InfoLog("实时压力轮询已启动");
            }
            catch (Exception e)
            {
                ErrorLog("实时压力轮询启动失败", e);
            }
        }

        private async Task StopPressurePollingAsync()
        {
            try
            {
                if (_dataCts != null)
                {
                    InfoLog("停止实时压力轮询");
                    _dataCts.Cancel();
                    try
                    {
                        if (_dataTask != null)
                        {
                            await _dataTask.WaitAsync(TimeSpan.FromSeconds(2));
                        }
                    }
                    catch (Exception)
                    {

                    }
                    _dataCts.Dispose();
                    _dataCts = null;
                    _dataTask = null;
                    InfoLog("实时压力轮询已停止");
                }
            }
            catch (Exception e)
            {
                ErrorLog("实时压力轮询停止异常", e);
            }
        }

        private async Task PressurePollingAsync(CancellationToken token)
        {
            InfoLog("实施压力轮询任务已启动");
            while (!token.IsCancellationRequested)
            {
                try
                {
                    ComId = Parameters.GetValue<string>("DeviceId");
                    double pressure = 0;
                    

                    if (IsSimulationMode)
                    {
                        await Task.Delay(2000);
                        pressure = new Random().Next(20, 100);
                    }
                    else
                    {
                        byte[] buffers = new byte[1024];
                        int len = await ReadAsync(buffers, 0, buffers.Length, token);
                        if (len > 0)
                        {
                            byte[] data = ExtractPayload(buffers);
                            if (BitConverter.IsLittleEndian)
                            {
                                ReverseArray(data);
                            }
                            pressure = BitConverter.ToInt16(data);
                        }
                    }
                    // 等待 1s
                    await Task.Delay(1000, token);
                }
                catch (OperationCanceledException)
                {
                    InfoLog("实时压力轮询任务已取消");
                    break;
                }
                catch (Exception e)
                {
                    ErrorLog("实时压力轮询任务执行失败", e);
                    try
                    {
                        await Task.Delay(1000, token);
                    }
                    catch (Exception)
                    {
                        break;
                    }
                }
            }
        }

        #endregion

        private async Task<int> ReadAsync(byte[] buffers,int offset,int count, CancellationToken token)
        {
            SerialPort serialPort = null;
            try
            {
                string com = Parameters.GetValue<string>("串口号");
                string baudRate = Parameters.GetValue<string>("波特率");
                string parity = Parameters.GetValue<string>("校验位");
                string dataBit = Parameters.GetValue<string>("数据位");
                string stopBit = Parameters.GetValue<string>("停止位");

                // 读取数据的指令
                byte[] bytes = new byte[] { 85, 3, 0, 4, 0, 1, 200, 31 };

                // 等待释放信号
                await _slim.WaitAsync(token);

                serialPort = new SerialPort(com);
                serialPort.BaudRate = int.Parse(baudRate);
                serialPort.Parity = (Parity)Enum.Parse(typeof(Parity), parity);
                serialPort.DataBits = int.Parse(dataBit);
                serialPort.StopBits = (StopBits)Enum.Parse(typeof(StopBits), stopBit);
                serialPort.ReadTimeout = 3000;
                serialPort.WriteTimeout = 3000;

                // 打开串口
                serialPort.Open();

                // 发送指令
                await serialPort.BaseStream.WriteAsync(bytes, 0, bytes.Length, token);

                var reader = serialPort.BaseStream.ReadAsync(buffers, offset, count, token);
                var complete = await Task.WhenAny(reader, Task.Delay(3000, token));
                if (complete != reader)
                {
                    throw new TimeoutException("串口读取超时，设备无响应");
                }
                // 返回读取结果
                return await reader;
            }
            finally
            {
                try
                {
                    if (serialPort != null)
                    {
                        serialPort.Close();
                        serialPort.Dispose();
                    }
                }
                catch { }

                // 释放信号
                _slim.Release();
            }
        }

        private async Task<bool> WriteAsync(byte[] buffers, int offset, int count, CancellationToken token)
        {
            SerialPort serialPort = null;
            try
            {
                string com = Parameters.GetValue<string>("串口号");
                string baudRate = Parameters.GetValue<string>("波特率");
                string parity = Parameters.GetValue<string>("校验位");
                string dataBit = Parameters.GetValue<string>("数据位");
                string stopBit = Parameters.GetValue<string>("停止位");

                // 等待释放信号
                await _slim.WaitAsync(token);

                serialPort = new SerialPort(com);
                serialPort.BaudRate = int.Parse(baudRate);
                serialPort.Parity = (Parity)Enum.Parse(typeof(Parity), parity);
                serialPort.DataBits = int.Parse(dataBit);
                serialPort.StopBits = (StopBits)Enum.Parse(typeof(StopBits), stopBit);
                serialPort.ReadTimeout = 3000;
                serialPort.WriteTimeout = 3000;

                // 打开串口
                serialPort.Open();

                // 发送指令
                await serialPort.BaseStream.WriteAsync(buffers, offset, count, token);

                return true;
            }
            finally
            {
                try
                {
                    if (serialPort != null)
                    {
                        serialPort.Close();
                        serialPort.Dispose();
                    }
                }
                catch { }

                // 释放信号
                _slim.Release();
            }
        }

        private byte[] ExtractPayload(byte[] frame)
        {
            if (frame == null || frame.Length < 5)
            {
                throw new ArgumentException("报文长度无效");
            }

            if (frame[1] >= 0x80)
            {
                throw new ArgumentException($"Modbus 异常响应，异常码：{frame[2]:X2}");
            }

            byte dataLen = frame[2];
            int dataStartIndex = 3;
            int crcIndex = dataStartIndex + dataLen;

            if (frame.Length < crcIndex + 2)
                throw new ArgumentException("Modbus RTU 报文长度不匹配");

            byte[] data = new byte[dataLen];
            Array.Copy(frame, dataStartIndex, data, 0, dataLen);
            return data;
        }

        /// <summary>
        /// 反转字节数组
        /// </summary>
        /// <param name="buffers">输入数组</param>
        private void ReverseArray(byte[] buffers)
        {
            if (buffers == null || buffers.Length == 0)
            {
                throw new ArgumentException("输入数据为空", nameof(buffers));
            }

            byte temp = 0;
            int middle = buffers.Length / 2;
            int length = buffers.Length - 1;

            for (int i = 0; i < middle; i++)
            {
                temp = buffers[i];
                buffers[i] = buffers[length - i];
                buffers[length - i] = temp;
            }
        }
    }

    public static class Crc16Modbus
    {
        /// <summary>
        /// 计算 CRC16-Modbus 校验码
        /// </summary>
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

        /// <summary>
        /// 给数据包追加 CRC 两个字节（用于发送）
        /// </summary>
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

        /// <summary>
        /// 验证数据包 CRC 是否正确
        /// </summary>
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

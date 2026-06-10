using DevicePlugins.Devices;
using MaxChemical.Core;
using MaxChemical.Logging;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.IO.Ports;
using System.Windows.Input;

namespace TwoLiquidOneGasFeedSystem_ModbusRTU
{
    /// <summary>
    /// 两液一气进料系统 (ModbusRTU)。
    /// 包含:MFC 一路 + 液体泵 1/2 两路 + 报警/复位/消音/状态。
    /// 
    /// 寄存器地址说明:
    ///   - CSV 中地址按 MCGS PLC 地址 (Base 1) 标注
    ///   - 实际下发到 Modbus 帧的是 Protocol 地址 (Base 0),整体偏移 -1
    ///   - 本代码中所有寄存器地址均为减 1 后的实际下发地址
    ///   - Float 字节序:Big-endian (ABCD),高字寄存器在前
    /// 
    /// 寄存器布局 (实际下发地址):
    ///   Real (每个占 2 寄存器, IEEE-754 Float, Big-endian):
    ///     00  MFC F_SV          AI_SV_00H_1
    ///     02  MFC FLOW          _MFC_Lm
    ///     04  MFC Add           _MFC_ADD_L
    ///     06  泵1 F_SV          Flow_F_SV_3
    ///     08  泵1 FLOW          _泵1FLOW_mlm
    ///     10  泵1 Add           _泵1_ADD_mL
    ///     12  泵1 P             _泵1压力_MPa
    ///     14  泵2 F_SV          Flow_F_SV_4
    ///     16  泵2 FLOW          _泵2FLOW_mlm
    ///     18  泵2 Add           _泵2_ADD_mL
    ///     20  泵2 P             _泵2压力_MPa
    ///     22  MFC 量程上限      AI_SCH_0EH_1
    ///     24  MFC 量程下限      AI_SCL_0DH_1
    ///     26  MFC 流量修正      AI_Scb_10H_1
    ///     28  MFC Dpt           AI_dPt_0CH_1
    ///     30  泵1 压力上限      Flow_F_HIAL_SV_1
    ///     32  泵1 压力下限      Flow_F_LOAL_SV_1
    ///     34  泵2 压力上限      Flow_F_HIAL_SV_2
    ///     36  泵2 压力下限      Flow_F_LOAL_SV_2
    ///   Int (每个占 1 寄存器, 16位):
    ///     38  MFC ON/OFF        AI_Srun_1BH_1   (0:运行 1:停止)
    ///     39  泵1 ON/OFF        Liq_PP_Run_1    (1:运行 0:停止)
    ///     40  泵2 ON/OFF        Liq_PP_Run_2    (1:运行 0:停止)
    ///     41  泵1压力上限报警   Alarm1
    ///     42  泵2压力上限报警   Alarm2
    ///     43  MFC通信故障       Alarm3
    ///     44  泵1通信故障       Alarm4
    ///     45  泵2通信故障       Alarm5
    ///     46  报警复位          AL_Reset
    ///     47  报警消音          AL_Silence
    ///     48  报警状态          AL_Status
    /// </summary>
    [Export(typeof(IDevice))]
    public class TwoLiquidOneGasFeedSystem_ModbusRTU : Device, IFlowLifecycleAware,IDeviceScreen
    {
        private readonly ILogService _logger;

        // 通信层重试参数
        private const int MaxRetries = 3;       // 总共尝试 3 次
        private const int RetryDelayMs = 100;   // 重试间隔 100ms

        // 实例级互斥锁:同一设备实例的所有 IO 串行化,避免与轮询/心跳撞车
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> _instanceLocks = new ConcurrentDictionary<string, SemaphoreSlim>();

        public TwoLiquidOneGasFeedSystem_ModbusRTU()
        {
            _logger = LogManager.GetLogger<TwoLiquidOneGasFeedSystem_ModbusRTU>();

            DeviceId = "TLG0001";
            Name = "两液一气进料系统";
            Manufacturer = "ModbusRTU 两液一气进料系统";
            ImageLocation = "pack://siteoforigin:,,,/Resources/DeviceIcon/TwoLiquidOneGasFeedSystem.png";
            Category = DeviceCategories.PublicWorks;
            ComId = "TLG0001";
            ConnectionManager = DeviceConnectionManagerFactory.GetInstance();
            AllowedRegions = new List<RegionType>()
            {
                RegionType.Feed,
                RegionType.PreHeat,
                RegionType.Reaction,
                RegionType.Quench,
                RegionType.PostProcess
            };

            Parameters.Variables.Add(new StringParameter("DeviceId", "TLG0001", "ModbusRTU设备ID")
            {
                Options = new ObservableCollection<string>() { "TLG0001", "TLG0002", "TLG0003", "TLG0004", "TLG0005" },
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

            Parameters.Variables.Add(new StringParameter("通信方式", "Direct", "通信方式")
            {
                Options = new ObservableCollection<string>() { "Direct", "PLC", "ModbusTcp", "ZLanGateway" }
            });

            Parameters.Variables.Add(new NumberParameter("Modbus站号", 1, 254, 1, "Modbus 从机站号 (1~254)"));

            InitializeCommands();
        }

        /// <summary>
        /// 声明本驱动支持通过 ZLAN 网关通信。
        /// </summary>
        public override bool SupportsZLanGateway => true;

        private void InitializeCommands()
        {
            #region 批量读命令 (8 个)

            // 1. 获取 MFC 过程值: MFC F_SV / FLOW / Add (地址 0~5, 3 个 Float)
            AddBatchReadCommand(
                name: "获取MFC过程值",
                helpText: "一次读取 MFC 设定流量 / 瞬时流量 / 累计流量",
                startRegister: 0,
                floatFields: new[]
                {
                    ("MFC_FSV",  "MFC 设定流量", "L/min"),
                    ("MFC_FLOW", "MFC 瞬时流量", "L/min"),
                    ("MFC_Add",  "MFC 累计流量", "L"),
                });

            // 2. 获取泵1 过程值: F_SV / FLOW / Add / P (地址 6~13, 4 个 Float)
            AddBatchReadCommand(
                name: "获取泵1过程值",
                helpText: "一次读取 泵1 设定流量 / 瞬时流量 / 累计流量 / 压力",
                startRegister: 6,
                floatFields: new[]
                {
                    ("Pump1_FSV",  "泵1 设定流量", "mL/min"),
                    ("Pump1_FLOW", "泵1 瞬时流量", "mL/min"),
                    ("Pump1_Add",  "泵1 累计流量", "mL"),
                    ("Pump1_P",    "泵1 压力",     "MPa"),
                });

            // 3. 获取泵2 过程值: F_SV / FLOW / Add / P (地址 14~21, 4 个 Float)
            AddBatchReadCommand(
                name: "获取泵2过程值",
                helpText: "一次读取 泵2 设定流量 / 瞬时流量 / 累计流量 / 压力",
                startRegister: 14,
                floatFields: new[]
                {
                    ("Pump2_FSV",  "泵2 设定流量", "mL/min"),
                    ("Pump2_FLOW", "泵2 瞬时流量", "mL/min"),
                    ("Pump2_Add",  "泵2 累计流量", "mL"),
                    ("Pump2_P",    "泵2 压力",     "MPa"),
                });

            // 4. 获取 MFC 配置参数: 量程上下限 / 流量修正 / Dpt (地址 22~29, 4 个 Float)
            AddBatchReadCommand(
                name: "获取MFC配置参数",
                helpText: "一次读取 MFC 量程上限 / 量程下限 / 流量修正 / Dpt",
                startRegister: 22,
                floatFields: new[]
                {
                    ("MFC_RangeHigh",  "MFC 量程上限", "L/min"),
                    ("MFC_RangeLow",   "MFC 量程下限", "L/min"),
                    ("MFC_Correction", "MFC 流量修正", ""),
                    ("MFC_Dpt",        "MFC Dpt",     ""),
                });

            // 5. 获取泵压力报警设置: 泵1 / 泵2 压力上下限 (地址 30~37, 4 个 Float)
            AddBatchReadCommand(
                name: "获取泵压力报警设置",
                helpText: "一次读取 泵1/泵2 压力上下限报警值",
                startRegister: 30,
                floatFields: new[]
                {
                    ("Pump1_PHigh", "泵1 压力上限", "MPa"),
                    ("Pump1_PLow",  "泵1 压力下限", "MPa"),
                    ("Pump2_PHigh", "泵2 压力上限", "MPa"),
                    ("Pump2_PLow",  "泵2 压力下限", "MPa"),
                });

            // 6. 获取运行状态: MFC/泵1/泵2 ON/OFF (地址 38~40, 3 个 Int16)
            AddBatchReadInt16Command(
                name: "获取运行状态",
                helpText: "一次读取 MFC/泵1/泵2 运行状态",
                startRegister: 38,
                intFields: new[]
                {
                    ("MFC_OnOff",   "MFC ON/OFF (0:运行,1:停止)", false),
                    ("Pump1_OnOff", "泵1 ON/OFF (1:运行,0:停止)", false),
                    ("Pump2_OnOff", "泵2 ON/OFF (1:运行,0:停止)", false),
                });

            // 7. 获取所有报警: 5 个 Alarm + 复位 + 消音 + 状态 (地址 41~48, 8 个 Int16, 全部按 Bool 处理)
            AddBatchReadInt16Command(
                name: "获取所有报警",
                helpText: "一次读取所有报警位/复位/消音/状态",
                startRegister: 41,
                intFields: new[]
                {
                    ("Pump1_PAlarm",    "泵1 压力上限报警", true),
                    ("Pump2_PAlarm",    "泵2 压力上限报警", true),
                    ("MFC_CommAlarm",   "MFC 通信故障",     true),
                    ("Pump1_CommAlarm", "泵1 通信故障",     true),
                    ("Pump2_CommAlarm", "泵2 通信故障",     true),
                    ("AlarmReset",      "报警复位结果",     true),
                    ("AlarmSilence",    "报警消音结果",     true),
                    ("AlarmStatus",     "报警状态 (1:有报警)", true),
                });

            // 8. 获取所有数据 (一把梭, 调试用): 地址 0~48, 19 Float + 11 Int16 = 49 寄存器
            AddReadAllCommand();

            #endregion

            #region 写命令 (单点写入)

            // Float 写入 (功能码 0x10, 2 寄存器)
            AddWriteFloatCommand("设置MFC F_SV", "设置 MFC 流量设定值", "MFC F_SV", 0, "L/min");
            AddWriteFloatCommand("设置泵1 F_SV", "设置 泵1 流量设定值", "泵1 F_SV", 6, "mL/min");
            AddWriteFloatCommand("设置泵2 F_SV", "设置 泵2 流量设定值", "泵2 F_SV", 14, "mL/min");
            AddWriteFloatCommand("设置MFC量程上限", "设置 MFC 量程上限", "MFC 量程上限", 22, "L/min");
            AddWriteFloatCommand("设置MFC量程下限", "设置 MFC 量程下限", "MFC 量程下限", 24, "L/min");
            AddWriteFloatCommand("设置MFC流量修正", "设置 MFC 流量修正系数", "MFC 流量修正", 26);
            AddWriteFloatCommand("设置MFC Dpt", "设置 MFC 小数点位 (固定为1)", "MFC Dpt", 28);
            AddWriteFloatCommand("设置泵1压力上限", "设置 泵1 压力上限报警值", "泵1压力上限", 30, "MPa");
            AddWriteFloatCommand("设置泵1压力下限", "设置 泵1 压力下限报警值", "泵1压力下限", 32, "MPa");
            AddWriteFloatCommand("设置泵2压力上限", "设置 泵2 压力上限报警值", "泵2压力上限", 34, "MPa");
            AddWriteFloatCommand("设置泵2压力下限", "设置 泵2 压力下限报警值", "泵2压力下限", 36, "MPa");

            // Int16 写入 (功能码 0x06, 1 寄存器)
            AddWriteInt16Command("设置MFC ON/OFF", "设置 MFC 运行状态 (0:运行,1:停止)", "MFC ON/OFF", 38);
            AddWriteInt16Command("设置泵1 ON/OFF", "设置 泵1 运行状态 (1:运行,0:停止)", "泵1 ON/OFF", 39);
            AddWriteInt16Command("设置泵2 ON/OFF", "设置 泵2 运行状态 (1:运行,0:停止)", "泵2 ON/OFF", 40);

            // 触发型动作 (固定写 1)
            AddTriggerCommand("报警复位", "对所有报警执行复位 (写1)", 46);
            AddTriggerCommand("报警消音", "执行报警消音 (写1)", 47);

            #endregion
        }

        #region 命令工厂方法

        /// <summary>
        /// 批量读 Float (功能码 0x03, 一次读取多个连续 Float, 拆分到多个输出参数)。
        /// </summary>
        private void AddBatchReadCommand(
            string name,
            string helpText,
            ushort startRegister,
            (string Key, string Desc, string Unit)[] floatFields)
        {
            int count = floatFields.Length;
            ushort regCount = (ushort)(count * 2);

            // 默认输出参数 (全 0)
            var defaultOutputs = new ObservableCollection<ParameterBase>();
            foreach (var (key, desc, unit) in floatFields)
            {
                defaultOutputs.Add(new NumberParameter(key, -1000000, 1000000, 0, desc, true, unit));
            }

            Commands.Add(new DeviceCommand()
            {
                Name = name,
                HelpText = helpText,
                OutputParameters = new DeviceParameters() { Variables = defaultOutputs },

                AsyncAction = AsyncWrapAction(name, async parameter =>
                {
                    ComId = Parameters.GetValue<string>("DeviceId");
                    var outputs = new ObservableCollection<ParameterBase>();

                    if (IsSimulationMode)
                    {
                        await Task.Delay(200);
                        var rand = new Random();
                        foreach (var (key, desc, unit) in floatFields)
                        {
                            float v = (float)(rand.NextDouble() * 100);
                            outputs.Add(new NumberParameter(key, -1000000, 1000000, v, desc, true, unit));
                        }
                        InfoLog($"模拟模式 {name},共 {count} 个 Float");
                    }
                    else
                    {
                        byte[] sendCmd = BuildReadCmd(startRegister, regCount);
                        float[] values = await SendAndParseFloatsAsync(sendCmd, count, CancellationToken.None);

                        for (int i = 0; i < count; i++)
                        {
                            var (key, desc, unit) = floatFields[i];
                            outputs.Add(new NumberParameter(key, -1000000, 1000000, values[i], desc, true, unit));
                        }
                        InfoLog($"{name} 完成,起始地址 {startRegister},读 {count} 个 Float");
                    }

                    return new DeviceParameters() { Variables = outputs };
                })
            });
        }

        /// <summary>
        /// 批量读 Int16 (功能码 0x03, 一次读取多个连续 Int16 寄存器, 按需输出为 Bool 或 Number)。
        /// </summary>
        private void AddBatchReadInt16Command(
            string name,
            string helpText,
            ushort startRegister,
            (string Key, string Desc, bool AsBool)[] intFields)
        {
            int count = intFields.Length;
            ushort regCount = (ushort)count;

            var defaultOutputs = new ObservableCollection<ParameterBase>();
            foreach (var (key, desc, asBool) in intFields)
            {
                if (asBool)
                    defaultOutputs.Add(new BooleanParameter(key, false, desc, true));
                else
                    defaultOutputs.Add(new NumberParameter(key, -32768, 32767, 0, desc, true));
            }

            Commands.Add(new DeviceCommand()
            {
                Name = name,
                HelpText = helpText,
                OutputParameters = new DeviceParameters() { Variables = defaultOutputs },

                AsyncAction = AsyncWrapAction(name, async parameter =>
                {
                    ComId = Parameters.GetValue<string>("DeviceId");
                    var outputs = new ObservableCollection<ParameterBase>();

                    if (IsSimulationMode)
                    {
                        await Task.Delay(200);
                        var rand = new Random();
                        foreach (var (key, desc, asBool) in intFields)
                        {
                            short v = (short)rand.Next(0, 2);
                            if (asBool)
                                outputs.Add(new BooleanParameter(key, v == 1, desc, true));
                            else
                                outputs.Add(new NumberParameter(key, -32768, 32767, v, desc, true));
                        }
                        InfoLog($"模拟模式 {name},共 {count} 个 Int16");
                    }
                    else
                    {
                        byte[] sendCmd = BuildReadCmd(startRegister, regCount);
                        short[] values = await SendAndParseInt16sAsync(sendCmd, count, CancellationToken.None);

                        for (int i = 0; i < count; i++)
                        {
                            var (key, desc, asBool) = intFields[i];
                            if (asBool)
                                outputs.Add(new BooleanParameter(key, values[i] == 1, desc, true));
                            else
                                outputs.Add(new NumberParameter(key, -32768, 32767, values[i], desc, true));
                        }
                        InfoLog($"{name} 完成,起始地址 {startRegister},读 {count} 个 Int16");
                    }

                    return new DeviceParameters() { Variables = outputs };
                })
            });
        }

        /// <summary>
        /// 获取所有数据 (一次读 49 个寄存器: 19 Float + 11 Int16)。
        /// 注意:Modbus 单次最多 125 个寄存器,49 满足要求。
        /// </summary>
        private void AddReadAllCommand()
        {
            // Float 字段定义 (与上面批量读保持一致)
            var floats = new (string Key, string Desc, string Unit)[]
            {
                ("MFC_FSV",        "MFC 设定流量",   "L/min"),
                ("MFC_FLOW",       "MFC 瞬时流量",   "L/min"),
                ("MFC_Add",        "MFC 累计流量",   "L"),
                ("Pump1_FSV",      "泵1 设定流量",   "mL/min"),
                ("Pump1_FLOW",     "泵1 瞬时流量",   "mL/min"),
                ("Pump1_Add",      "泵1 累计流量",   "mL"),
                ("Pump1_P",        "泵1 压力",       "MPa"),
                ("Pump2_FSV",      "泵2 设定流量",   "mL/min"),
                ("Pump2_FLOW",     "泵2 瞬时流量",   "mL/min"),
                ("Pump2_Add",      "泵2 累计流量",   "mL"),
                ("Pump2_P",        "泵2 压力",       "MPa"),
                ("MFC_RangeHigh",  "MFC 量程上限",   "L/min"),
                ("MFC_RangeLow",   "MFC 量程下限",   "L/min"),
                ("MFC_Correction", "MFC 流量修正",   ""),
                ("MFC_Dpt",        "MFC Dpt",        ""),
                ("Pump1_PHigh",    "泵1 压力上限",   "MPa"),
                ("Pump1_PLow",     "泵1 压力下限",   "MPa"),
                ("Pump2_PHigh",    "泵2 压力上限",   "MPa"),
                ("Pump2_PLow",     "泵2 压力下限",   "MPa"),
            };

            // Int16 字段定义 (从地址 38 开始)
            var ints = new (string Key, string Desc, bool AsBool)[]
            {
                ("MFC_OnOff",       "MFC ON/OFF",    false),
                ("Pump1_OnOff",     "泵1 ON/OFF",    false),
                ("Pump2_OnOff",     "泵2 ON/OFF",    false),
                ("Pump1_PAlarm",    "泵1 压力上限报警", true),
                ("Pump2_PAlarm",    "泵2 压力上限报警", true),
                ("MFC_CommAlarm",   "MFC 通信故障",     true),
                ("Pump1_CommAlarm", "泵1 通信故障",     true),
                ("Pump2_CommAlarm", "泵2 通信故障",     true),
                ("AlarmReset",      "报警复位结果",     true),
                ("AlarmSilence",    "报警消音结果",     true),
                ("AlarmStatus",     "报警状态",         true),
            };

            // 默认输出
            var defaultOutputs = new ObservableCollection<ParameterBase>();
            foreach (var (key, desc, unit) in floats)
                defaultOutputs.Add(new NumberParameter(key, -1000000, 1000000, 0, desc, true, unit));
            foreach (var (key, desc, asBool) in ints)
            {
                if (asBool) defaultOutputs.Add(new BooleanParameter(key, false, desc, true));
                else defaultOutputs.Add(new NumberParameter(key, -32768, 32767, 0, desc, true));
            }

            Commands.Add(new DeviceCommand()
            {
                Name = "获取所有数据",
                HelpText = "一次读取所有过程值/配置/状态/报警 (调试用,共 49 个寄存器)",
                OutputParameters = new DeviceParameters() { Variables = defaultOutputs },

                AsyncAction = AsyncWrapAction("获取所有数据", async parameter =>
                {
                    ComId = Parameters.GetValue<string>("DeviceId");
                    var outputs = new ObservableCollection<ParameterBase>();

                    if (IsSimulationMode)
                    {
                        await Task.Delay(200);
                        var rand = new Random();
                        foreach (var (key, desc, unit) in floats)
                            outputs.Add(new NumberParameter(key, -1000000, 1000000, (float)(rand.NextDouble() * 100), desc, true, unit));
                        foreach (var (key, desc, asBool) in ints)
                        {
                            short v = (short)rand.Next(0, 2);
                            if (asBool) outputs.Add(new BooleanParameter(key, v == 1, desc, true));
                            else outputs.Add(new NumberParameter(key, -32768, 32767, v, desc, true));
                        }
                        InfoLog("模拟模式 获取所有数据");
                    }
                    else
                    {
                        // 一次读 49 个寄存器 (地址 0~48)
                        byte[] sendCmd = BuildReadCmd(0, 49);
                        ushort[] regs = await SendAndParseRegistersAsync(sendCmd, 49, CancellationToken.None);

                        // 解 19 个 Float (寄存器 0~37, 每对寄存器 = 1 Float, Big-endian)
                        for (int i = 0; i < floats.Length; i++)
                        {
                            float v = RegistersToFloat(regs[i * 2], regs[i * 2 + 1]);
                            var (key, desc, unit) = floats[i];
                            outputs.Add(new NumberParameter(key, -1000000, 1000000, v, desc, true, unit));
                        }

                        // 解 11 个 Int16 (寄存器 38~48)
                        for (int i = 0; i < ints.Length; i++)
                        {
                            short v = (short)regs[38 + i];
                            var (key, desc, asBool) = ints[i];
                            if (asBool) outputs.Add(new BooleanParameter(key, v == 1, desc, true));
                            else outputs.Add(new NumberParameter(key, -32768, 32767, v, desc, true));
                        }

                        InfoLog("获取所有数据 完成,共 49 个寄存器");
                    }

                    return new DeviceParameters() { Variables = outputs };
                })
            });
        }

        /// <summary>
        /// Float 写入 (功能码 0x10, 2 寄存器, IEEE-754 Big-endian)。
        /// </summary>
        private void AddWriteFloatCommand(string commandName, string helpText, string targetDesc, ushort register, string unit = "")
        {
            Commands.Add(new DeviceCommand()
            {
                Name = commandName,
                HelpText = helpText,

                InputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new NumberParameter("TargetValue", -1000000, 1000000, 0, $"{targetDesc} 目标值", true, unit)
                    }
                },

                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new BooleanParameter("Success", false, "设置成功", true),
                        new NumberParameter("SetTargetValue", -1000000, 1000000, 0, $"设置的 {targetDesc} 值", true, unit)
                    }
                },

                AsyncAction = AsyncWrapAction(commandName, async parameter =>
                {
                    ComId = Parameters.GetValue<string>("DeviceId");
                    float target = parameter.GetValue<float>("TargetValue");
                    bool success;

                    if (IsSimulationMode)
                    {
                        await Task.Delay(200);
                        success = true;
                        InfoLog($"模拟模式 {commandName},目标值:{target}");
                    }
                    else
                    {
                        byte[] frame = BuildMultWriteFloatCmd(register, target);
                        success = await WriteAsync(frame, 0, frame.Length, CancellationToken.None);
                        InfoLog($"{commandName} 完成,成功:{success},目标值:{target}");
                    }

                    return new DeviceParameters()
                    {
                        Variables = new ObservableCollection<ParameterBase>()
                        {
                            new BooleanParameter("Success", success, "设置成功", true),
                            new NumberParameter("SetTargetValue", -1000000, 1000000, target, $"设置的 {targetDesc} 值", true, unit)
                        }
                    };
                })
            });
        }

        /// <summary>
        /// Int16 写入 (功能码 0x06, 1 寄存器)。
        /// </summary>
        private void AddWriteInt16Command(string commandName, string helpText, string targetDesc, ushort register)
        {
            Commands.Add(new DeviceCommand()
            {
                Name = commandName,
                HelpText = helpText,

                InputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new NumberParameter("TargetValue", -32768, 32767, 0, $"{targetDesc} 目标值", true)
                    }
                },

                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new BooleanParameter("Success", false, "设置成功", true),
                        new NumberParameter("SetTargetValue", -32768, 32767, 0, $"设置的 {targetDesc}", true)
                    }
                },

                AsyncAction = AsyncWrapAction(commandName, async parameter =>
                {
                    ComId = Parameters.GetValue<string>("DeviceId");
                    short target = parameter.GetValue<short>("TargetValue");
                    bool success;

                    if (IsSimulationMode)
                    {
                        await Task.Delay(200);
                        success = true;
                        InfoLog($"模拟模式 {commandName},目标值:{target}");
                    }
                    else
                    {
                        byte[] frame = BuildWriteCmd(register, (ushort)target);
                        success = await WriteAsync(frame, 0, frame.Length, CancellationToken.None);
                        InfoLog($"{commandName} 完成,成功:{success},目标值:{target}");
                    }

                    return new DeviceParameters()
                    {
                        Variables = new ObservableCollection<ParameterBase>()
                        {
                            new BooleanParameter("Success", success, "设置成功", true),
                            new NumberParameter("SetTargetValue", -32768, 32767, target, $"设置的 {targetDesc}", true)
                        }
                    };
                })
            });
        }

        /// <summary>
        /// 触发型命令 (无输入,固定写 1)。用于报警复位/消音。
        /// </summary>
        private void AddTriggerCommand(string commandName, string helpText, ushort register)
        {
            Commands.Add(new DeviceCommand()
            {
                Name = commandName,
                HelpText = helpText,

                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new BooleanParameter("Success", false, $"{commandName} 成功", true)
                    }
                },

                AsyncAction = AsyncWrapAction(commandName, async parameter =>
                {
                    ComId = Parameters.GetValue<string>("DeviceId");
                    bool success;

                    if (IsSimulationMode)
                    {
                        await Task.Delay(200);
                        success = true;
                        InfoLog($"模拟模式 {commandName}");
                    }
                    else
                    {
                        byte[] frame = BuildWriteCmd(register, 1);
                        success = await WriteAsync(frame, 0, frame.Length, CancellationToken.None);
                        InfoLog($"{commandName} 完成,成功:{success}");
                    }

                    return new DeviceParameters()
                    {
                        Variables = new ObservableCollection<ParameterBase>()
                        {
                            new BooleanParameter("Success", success, $"{commandName} 成功", true)
                        }
                    };
                })
            });
        }

        #endregion

        public Task<bool> OnFlowCompletedAsync(FlowCompletionContext context) => Task.FromResult(true);
        public Task<bool> OnFlowFailedAsync(FlowFailureContext context) => Task.FromResult(true);
        public Task<bool> OnFlowStartedAsync(FlowStartContext context) => Task.FromResult(true);

        /// <summary>
        /// ZLanGateway 模式下的连接实现。使用报警状态寄存器 (48) 作为心跳探测。
        /// </summary>
        protected override async Task<bool> OnConnectViaGatewayAsync()
        {
            try
            {
                InfoLog("通过 ZLAN 网关测试连接");
                byte[] sendCmd = BuildReadCmd(register: 48, count: 1);
                short status = await SendAndParseInt16Async(sendCmd, CancellationToken.None);
                InfoLog($"网关连接成功,当前报警状态 {status}");
                return true;
            }
            catch (TimeoutException)
            {
                ErrorLog("网关连接超时:链路通但设备无应答");
                return false;
            }
            catch (Exception e)
            {
                ErrorLog("网关连接失败", e);
                return false;
            }
        }

        /// <summary>
        /// 全异步命令的统一包装:日志 / 计时 / 异常归一化。
        /// </summary>
        private Func<DeviceParameters, Task<DeviceParameters>> AsyncWrapAction(
            string commandName,
            Func<DeviceParameters, Task<DeviceParameters>> innerAsync)
        {
            return async (DeviceParameters parameters) =>
            {
                _logger.LogInformation($"[ModbusRTU两液一气-开始] Device={Name} Command={commandName}");
                var sw = Stopwatch.StartNew();
                try
                {
                    var output = await innerAsync(parameters).ConfigureAwait(false);
                    sw.Stop();
                    _logger.LogInformation($"[ModbusRTU两液一气-完成] Device={Name} Command={commandName} DurationMs={sw.ElapsedMilliseconds}");
                    return output;
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    _logger.LogError(ex, $"[ModbusRTU两液一气-异常] Device={Name} Command={commandName} DurationMs={sw.ElapsedMilliseconds}");
                    throw new DeviceCommandExecutionException(Name, commandName, ex.Message, ex);
                }
            };
        }

        #region 通信层 - 命令帧构造

        /// <summary>
        /// 取当前配置的 Modbus 从机站号 (1~254)。
        /// </summary>
        private byte GetSlaveAddress()
        {
            try
            {
                var addr = (int)Parameters.GetValue<double>("Modbus站号");
                if (addr < 1 || addr > 254) addr = 1;
                return (byte)addr;
            }
            catch
            {
                return 1;
            }
        }

        /// <summary>
        /// 获取本设备实例专属的互斥锁,确保 IO 串行化。
        /// </summary>
        private SemaphoreSlim GetInstanceLock()
        {
            var key = Parameters?.GetValue<string>("DeviceId") ?? DeviceId ?? "default";
            return _instanceLocks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        }

        /// <summary>
        /// 构造读保持寄存器命令 (功能码 0x03)。
        /// </summary>
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

        /// <summary>
        /// 构造写单寄存器命令 (功能码 0x06)。
        /// </summary>
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

        /// <summary>
        /// 构造写多个寄存器命令 (功能码 0x10),写入一个 IEEE-754 Float (4 字节, 2 寄存器, Big-endian)。
        /// </summary>
        private byte[] BuildMultWriteFloatCmd(ushort register, float value)
        {
            byte[] floatBytes = BitConverter.GetBytes(value);
            if (BitConverter.IsLittleEndian)
            {
                ReverseArray(floatBytes);   // 转大端
            }

            byte[] frame =
            {
                GetSlaveAddress(),
                0x10,
                (byte)(register >> 8), (byte)(register & 0xFF),
                0x00, 0x02,    // 寄存器数量 = 2
                0x04,          // 字节数 = 4
                floatBytes[0], floatBytes[1], floatBytes[2], floatBytes[3],
            };

            return Crc16Modbus.AppendCrc(frame, 0, frame.Length);
        }

        #endregion

        #region 通信层 - 异步收发

        /// <summary>
        /// 写命令并自动重试 3 次。
        /// </summary>
        private async Task<bool> WriteAsync(byte[] buffers, int offset, int count, CancellationToken token)
        {
            Exception lastException = null;

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

        /// <summary>
        /// 通用读寄存器:发送 + 解析校验 + 提取 N 个寄存器原始值 (ushort)。
        /// 自动重试 3 次。
        /// </summary>
        private async Task<ushort[]> SendAndParseRegistersAsync(byte[] sendCmd, int expectedRegCount, CancellationToken token)
        {
            Exception lastException = null;

            for (int attempt = 1; attempt <= MaxRetries; attempt++)
            {
                try
                {
                    return await SendAndParseRegistersOnceAsync(sendCmd, expectedRegCount, token).ConfigureAwait(false);
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
                    _logger?.LogError(ex, "Modbus 读取失败 (非超时,不重试)");
                    throw;
                }
            }

            throw new TimeoutException($"Modbus 读取失败,{MaxRetries} 次尝试后仍超时", lastException);
        }

        /// <summary>
        /// 单次读寄存器:实例锁排队 + 站号/功能码/长度校验 + 拆分为 ushort[]。
        /// </summary>
        private async Task<ushort[]> SendAndParseRegistersOnceAsync(byte[] sendCmd, int expectedRegCount, CancellationToken token)
        {
            var sem = GetInstanceLock();
            await sem.WaitAsync(token).ConfigureAwait(false);
            try
            {
                byte[] recv = new byte[512];
                using var transport = await GetTransportAsync().ConfigureAwait(false);

                // === 诊断日志:打印实际发送的字节 ===
                _logger?.LogInformation("[诊断] 发送字节: {Hex} (站号={Slave}, 期望寄存器数={Count})",
                    BitConverter.ToString(sendCmd),
                    GetSlaveAddress(),
                    expectedRegCount);

                int len = await transport.SendAndReceiveAsync(sendCmd, recv, 3000, token).ConfigureAwait(false);

                // === 诊断日志:打印实际收到的字节 ===
                byte[] recvDump = new byte[Math.Max(0, Math.Min(len, recv.Length))];
                if (recvDump.Length > 0) Array.Copy(recv, recvDump, recvDump.Length);
                _logger?.LogInformation("[诊断] 接收字节: {Hex} (长度={Len})",
                    BitConverter.ToString(recvDump),
                    len);

                int expectedDataLen = expectedRegCount * 2;
                int minFrameLen = 3 + expectedDataLen + 2;   // 站号+功能码+长度 + 数据 + CRC
                if (len < minFrameLen)
                {
                    _logger?.LogWarning("应答帧长度不足:{Len} 字节,期望至少 {Min} 字节", len, minFrameLen);
                    return new ushort[expectedRegCount];     // 全 0
                }

                byte expectedAddr = GetSlaveAddress();
                if (recv[0] != expectedAddr)
                {
                    _logger?.LogWarning("站号不匹配:期望 0x{Expected:X2},实际 0x{Actual:X2}", expectedAddr, recv[0]);
                    return new ushort[expectedRegCount];
                }

                if ((recv[1] & 0x80) != 0)
                {
                    _logger?.LogWarning("Modbus 异常应答,异常码 0x{Code:X2}", recv[2]);
                    return new ushort[expectedRegCount];
                }
                if (recv[1] != 0x03)
                {
                    _logger?.LogWarning("功能码不匹配:期望 0x03,实际 0x{Actual:X2}", recv[1]);
                    return new ushort[expectedRegCount];
                }

                byte dataLen = recv[2];
                if (dataLen != expectedDataLen)
                {
                    _logger?.LogWarning("数据长度不匹配:期望 {Expected},实际 {Actual}", expectedDataLen, dataLen);
                    return new ushort[expectedRegCount];
                }

                // 拆成 ushort[],Modbus 寄存器是大端 (高字节在前)
                ushort[] result = new ushort[expectedRegCount];
                for (int i = 0; i < expectedRegCount; i++)
                {
                    result[i] = (ushort)((recv[3 + i * 2] << 8) | recv[3 + i * 2 + 1]);
                }
                return result;
            }
            finally
            {
                sem.Release();
            }
        }

        /// <summary>
        /// 读 N 个 Int16 (有符号)。
        /// </summary>
        private async Task<short[]> SendAndParseInt16sAsync(byte[] sendCmd, int count, CancellationToken token)
        {
            ushort[] regs = await SendAndParseRegistersAsync(sendCmd, count, token).ConfigureAwait(false);
            short[] result = new short[count];
            for (int i = 0; i < count; i++) result[i] = (short)regs[i];
            return result;
        }

        /// <summary>
        /// 读单个 Int16 (兼容旧接口,网关心跳用)。
        /// </summary>
        private async Task<short> SendAndParseInt16Async(byte[] sendCmd, CancellationToken token)
        {
            short[] arr = await SendAndParseInt16sAsync(sendCmd, 1, token).ConfigureAwait(false);
            return arr[0];
        }

        /// <summary>
        /// 读 N 个 IEEE-754 Float (Big-endian, 高字寄存器在前)。
        /// </summary>
        private async Task<float[]> SendAndParseFloatsAsync(byte[] sendCmd, int count, CancellationToken token)
        {
            ushort[] regs = await SendAndParseRegistersAsync(sendCmd, count * 2, token).ConfigureAwait(false);
            float[] result = new float[count];
            for (int i = 0; i < count; i++)
            {
                result[i] = RegistersToFloat(regs[i * 2], regs[i * 2 + 1]);
            }
            return result;
        }

        /// <summary>
        /// 把两个寄存器 (高字在前) 拼成一个 IEEE-754 Float。
        /// </summary>
        private static float RegistersToFloat(ushort high, ushort low)
        {
            byte[] bytes = new byte[4]
            {
                (byte)(high >> 8), (byte)(high & 0xFF),
                (byte)(low >> 8),  (byte)(low & 0xFF),
            };
            if (BitConverter.IsLittleEndian)
            {
                Array.Reverse(bytes);
            }
            return BitConverter.ToSingle(bytes, 0);
        }

        /// <summary>
        /// 通过基类的 CreateTransportAsync 获取 Transport。
        /// </summary>
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
        public string ScreenKey => "TwoLiquidOneGasFeed";
        #endregion

        /// <summary>
        /// 反转字节数组 (用于大小端转换)。
        /// </summary>
        private static void ReverseArray(byte[] buffers)
        {
            if (buffers == null || buffers.Length == 0)
                throw new ArgumentException("输入数据为空", nameof(buffers));

            int middle = buffers.Length / 2;
            int last = buffers.Length - 1;
            for (int i = 0; i < middle; i++)
            {
                byte temp = buffers[i];
                buffers[i] = buffers[last - i];
                buffers[last - i] = temp;
            }
        }
    }

    public static class Crc16Modbus
    {
        /// <summary>
        /// 计算 CRC16-Modbus 校验码。
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
        /// 给数据包追加 CRC 两个字节 (用于发送)。
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
        /// 验证数据包 CRC 是否正确。
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
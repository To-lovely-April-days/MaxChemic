using DevicePlugins.Devices;
using MaxChemical.Core;
using MaxChemical.Logging;
using System.Collections.ObjectModel;
using System.ComponentModel.Composition;
using System.Diagnostics;

namespace Rotavap_IKA_RV10
{
    /// <summary>
    /// IKA RV10 旋转蒸发仪驱动(RS-232 ASCII 行协议)。
    ///
    /// ── 协议来源 ────────────────────────────────────────────────────────
    /// 命令表移植自 PyLabware(Cronin Group,MIT License,Copyright (c) 2021),
    /// 文件 PyLabware/devices/ika_rv10.py,原始依据是 IKA 官方操作手册英文部分。
    /// 只搬协议不引入运行时依赖。
    ///
    /// ── 串口设置 ────────────────────────────────────────────────────────
    /// 9600, 7 数据位, 偶校验, 1 停止位(与 IKA 其它仪器一致,不是 8N1)。
    ///
    /// ── 报文格式 ────────────────────────────────────────────────────────
    /// 发送: 命令名 + " " + 参数 + "\r\n"
    /// 接收: 数值 + " " + 通道号 + "\r\n"   → 读数要砍掉尾部两个字符
    ///
    /// ── 升降命令是个坑 ──────────────────────────────────────────────────
    /// 手册上印的升降命令是错的。PyLabware 里这两条命令的注释写明:
    /// "The lift commands in the manual are wrong. The working command was found
    ///  by Sebastian Steiner by e-mailing IKA." —— 正确的是 OUT_SP_62/OUT_SP_63 带参数 1。
    /// 这种东西不看开源实现根本查不到,是这次移植最值钱的一条。
    ///
    /// ── 与磁力搅拌器一样的限制 ──────────────────────────────────────────
    /// 没有查询"当前是否在旋转/加热"的命令,开关状态由驱动本地记录。
    /// 判断实际状态要看转速与温度读数,不要只信开关。
    /// STATUS 命令能查到的只是本地/远程模式与两个故障码。
    /// </summary>
    [Export(typeof(IDevice))]
    public class Rotavap_IKA_RV10 : Device
    {
        private readonly ILogService _logger;
        private readonly AsciiLineProtocol _proto;

        // ── 命令表(照抄自 PyLabware ika_rv10.py)──────────────────────────
        private const string CmdGetName = "IN_NAME";      // 设备名,应答前 11 字符
        private const string CmdGetVersion = "IN_SOFTWARE";
        private const string CmdGetSpeed = "IN_PV_4";     // 当前转速 rpm
        private const string CmdGetSpeedSet = "IN_SP_4";  // 转速设定值
        private const string CmdSetSpeed = "OUT_SP_4";    // 0~280 rpm
        private const string CmdStartRotation = "START_4";
        private const string CmdStopRotation = "STOP_4";
        private const string CmdGetTemp = "IN_PV_2";      // 水浴温度 ℃
        private const string CmdGetTempSet = "IN_SP_2";   // 水浴温度设定值
        private const string CmdGetTempSafe = "IN_SP_3";  // 水浴安全温度
        private const string CmdSetTemp = "OUT_SP_2";     // 0~180 ℃
        private const string CmdStartHeat = "START_2";
        private const string CmdStopHeat = "STOP_2";
        // 升降 —— 手册印错,以下是 IKA 邮件确认的正确命令,参数 1 是命令的一部分
        private const string CmdLiftUp = "OUT_SP_62 1";
        private const string CmdLiftDown = "OUT_SP_63 1";
        private const string CmdGetStatus = "STATUS";
        private const string CmdReset = "RESET";

        private const string ExpectedName = "RV10Digital";

        private const double SpeedMin = 0, SpeedMax = 280;
        private const double TempMin = 0, TempMax = 180;

        // ── 最近一次读数/下发值(开关状态只能本地记)──
        private double _lastSpeed;
        private double _lastSpeedSv;
        private double _lastTemp;
        private double _lastTempSv;
        private bool _lastRotating;
        private bool _lastHeating;
        private string _lastStatus = "";

        // ── 模拟模式状态 ──
        private double _simSpeedSv = 100, _simSpeed;
        private double _simTempSv = 50, _simTemp = 25;
        private bool _simRotating, _simHeating;

        public Rotavap_IKA_RV10()
        {
            _logger = LogManager.GetLogger<Rotavap_IKA_RV10>();

            DeviceId = "RV10001";
            Name = "旋转蒸发仪(IKA RV10)";
            Manufacturer = "IKA RV10 Digital · RS-232 ASCII";
            // 画布上用实物照片。图片放在 Shell 输出目录的 Resources\DeviceIcon 下。
            ImageLocation = "pack://siteoforigin:,,,/Resources/DeviceIcon/RotavapIKA_RV10.png";
            Category = DeviceCategories.PublicWorks;
            ComId = "RV10001";
            ConnectionManager = DeviceConnectionManagerFactory.GetInstance();
            AllowedRegions = new List<RegionType>()
            {
                RegionType.PostProcess,
                RegionType.Quench,
                RegionType.Reaction
            };

            Parameters.Variables.Add(new StringParameter("DeviceId", "RV10001", "设备ID")
            {
                Options = new ObservableCollection<string>() { "RV10001", "RV10002", "RV10003" },
            });

            DeviceParameterOptions.AddSerialParameters(Parameters,
                baudRate: "9600", parity: "Even", dataBits: "7", stopBits: "One");

            _proto = new AsciiLineProtocol(GetTransportAsync, msg => _logger?.LogDebug(msg))
            {
                CommandTerminator = "\r\n",
                ReplyTerminator = "\r\n",
                ArgsDelimiter = " ",
                TimeoutMs = 3000,
            };

            InitializeCommands();
        }

        public override IReadOnlyList<CollectableParameter> CollectableParameters { get; } = new[]
        {
            new CollectableParameter("BathTemperature", "水浴温度", "℃", source: "获取所有数据", isKey: true),
            new CollectableParameter("RotationSpeed", "旋转速度", "RPM", source: "获取所有数据"),
            new CollectableParameter("BathTemperatureSV", "水浴温度设定值", "℃", source: "获取所有数据"),
            new CollectableParameter("RotationSpeedSV", "旋转速度设定值", "RPM", source: "获取所有数据"),
        };

        public override int AutoCollectIntervalMs => 2000;

        public override bool SupportsZLanGateway => true;

        private void InitializeCommands()
        {
            // ════════════ 旋转控制 ════════════
            Commands.Add(new DeviceCommand()
            {
                Name = "旋转控制",
                HelpText = "下发转速设定值(OUT_SP_4)并启停旋转(START_4/STOP_4)。量程 0~280 rpm。" +
                           "本机无法回读旋转开关状态,软件显示的是上次下发值,请以转速读数为准。",
                InputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new NumberParameter("TargetSpeed", 0, 280, 0, "转速设定值", true, "RPM"),
                        new BooleanParameter("RotationSwitch", false, "旋转开关", true)
                    }
                },
                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new BooleanParameter("Success", false, "设置成功", true),
                        new NumberParameter("SetSpeed", 0, 280, 0, "设定的转速", true, "RPM"),
                        new BooleanParameter("SetSwitch", false, "设定的旋转开关", true)
                    }
                },
                AsyncAction = WrapAction("旋转控制", async parameter =>
                {
                    ComId = Parameters.GetValue<string>("DeviceId");
                    double target = AsciiLineProtocol.Clamp(
                        parameter.GetValue<double>("TargetSpeed"), SpeedMin, SpeedMax);
                    bool onOff = parameter.GetValue<bool>("RotationSwitch");
                    bool success;

                    OnStateChanged("CommandState_旋转控制", CommandExecutionState.Running);

                    if (IsSimulationMode)
                    {
                        await Task.Delay(300);
                        _simSpeedSv = target;
                        _simRotating = onOff;
                        success = true;
                    }
                    else
                    {
                        bool ok1 = await _proto.SendAsync(CmdSetSpeed, AsciiLineProtocol.Format(target));
                        bool ok2 = await _proto.SendAsync(onOff ? CmdStartRotation : CmdStopRotation);
                        success = ok1 && ok2;
                        InfoLog($"旋转控制:{target} rpm,{(onOff ? "启动" : "停止")},结果 {success}");
                    }

                    if (success)
                    {
                        _lastSpeedSv = target;
                        _lastRotating = onOff;
                        OnStateChanged("RotationSpeedSV", target);
                        OnStateChanged("RotationSwitch", onOff);
                    }
                    OnStateChanged("CommandState_旋转控制",
                        success ? CommandExecutionState.Succeeded : CommandExecutionState.Failed);

                    return new DeviceParameters()
                    {
                        Variables = new ObservableCollection<ParameterBase>()
                        {
                            new BooleanParameter("Success", success, "设置成功", true),
                            new NumberParameter("SetSpeed", 0, 280, target, "设定的转速", true, "RPM"),
                            new BooleanParameter("SetSwitch", onOff, "设定的旋转开关", true)
                        }
                    };
                })
            });

            // ════════════ 水浴控制 ════════════
            Commands.Add(new DeviceCommand()
            {
                Name = "水浴控制",
                HelpText = "下发水浴温度设定值(OUT_SP_2)并启停加热(START_2/STOP_2)。量程 0~180 ℃。" +
                           "水浴介质(油/水)与安全温度只能在面板上设,协议里那两条命令实测无效。",
                InputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new NumberParameter("TargetTemperature", 0, 180, 0, "水浴温度设定值", true, "℃"),
                        new BooleanParameter("HeatingSwitch", false, "加热开关", true)
                    }
                },
                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new BooleanParameter("Success", false, "设置成功", true),
                        new NumberParameter("SetTemperature", 0, 180, 0, "设定的温度", true, "℃"),
                        new BooleanParameter("SetSwitch", false, "设定的加热开关", true)
                    }
                },
                AsyncAction = WrapAction("水浴控制", async parameter =>
                {
                    ComId = Parameters.GetValue<string>("DeviceId");
                    double target = AsciiLineProtocol.Clamp(
                        parameter.GetValue<double>("TargetTemperature"), TempMin, TempMax);
                    bool onOff = parameter.GetValue<bool>("HeatingSwitch");
                    bool success;

                    OnStateChanged("CommandState_水浴控制", CommandExecutionState.Running);

                    if (IsSimulationMode)
                    {
                        await Task.Delay(300);
                        _simTempSv = target;
                        _simHeating = onOff;
                        success = true;
                    }
                    else
                    {
                        bool ok1 = await _proto.SendAsync(CmdSetTemp, AsciiLineProtocol.Format(target));
                        bool ok2 = await _proto.SendAsync(onOff ? CmdStartHeat : CmdStopHeat);
                        success = ok1 && ok2;
                        InfoLog($"水浴控制:{target}℃,{(onOff ? "启动" : "停止")},结果 {success}");
                    }

                    if (success)
                    {
                        _lastTempSv = target;
                        _lastHeating = onOff;
                        OnStateChanged("BathTemperatureSV", target);
                        OnStateChanged("HeatingSwitch", onOff);
                    }
                    OnStateChanged("CommandState_水浴控制",
                        success ? CommandExecutionState.Succeeded : CommandExecutionState.Failed);

                    return new DeviceParameters()
                    {
                        Variables = new ObservableCollection<ParameterBase>()
                        {
                            new BooleanParameter("Success", success, "设置成功", true),
                            new NumberParameter("SetTemperature", 0, 180, target, "设定的温度", true, "℃"),
                            new BooleanParameter("SetSwitch", onOff, "设定的加热开关", true)
                        }
                    };
                })
            });

            // ════════════ 升降 ════════════
            Commands.Add(new DeviceCommand()
            {
                Name = "升降",
                HelpText = "抬起/放下旋转轴。命令是 OUT_SP_62 1 / OUT_SP_63 1 —— " +
                           "IKA 手册上印的升降命令是错的,这两条是 PyLabware 作者向 IKA 邮件问来的。" +
                           "本机不回报升降位置,下发后无法确认是否到位。",
                InputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new BooleanParameter("LiftUp", true, "true=抬起, false=放下", true)
                    }
                },
                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new BooleanParameter("Success", false, "升降指令已下发", true)
                    }
                },
                AsyncAction = WrapAction("升降", async parameter =>
                {
                    ComId = Parameters.GetValue<string>("DeviceId");
                    bool up = parameter.GetValue<bool>("LiftUp");
                    bool success;

                    if (IsSimulationMode)
                    {
                        await Task.Delay(1500);   // 真机抬升要一两秒
                        success = true;
                        InfoLog($"模拟模式{(up ? "抬起" : "放下")}旋转轴");
                    }
                    else
                    {
                        // 参数 1 已经写在命令常量里,这里不再传 value
                        success = await _proto.SendAsync(up ? CmdLiftUp : CmdLiftDown);
                    }

                    OnStateChanged("CommandState_升降",
                        success ? CommandExecutionState.Succeeded : CommandExecutionState.Failed);

                    return new DeviceParameters()
                    {
                        Variables = new ObservableCollection<ParameterBase>()
                        {
                            new BooleanParameter("Success", success, "升降指令已下发", true)
                        }
                    };
                })
            });

            // ════════════ 获取所有数据 ════════════
            Commands.Add(new DeviceCommand()
            {
                Name = "获取所有数据",
                HelpText = "依次查询转速、水浴温度、两个设定值、安全温度与状态码。一问一答,一轮 6 帧。",
                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new NumberParameter("RotationSpeed", 0, 280, 0, "旋转速度", true, "RPM"),
                        new NumberParameter("BathTemperature", -10, 200, 0, "水浴温度", true, "℃"),
                        new NumberParameter("RotationSpeedSV", 0, 280, 0, "旋转速度设定值", true, "RPM"),
                        new NumberParameter("BathTemperatureSV", 0, 180, 0, "水浴温度设定值", true, "℃"),
                        new NumberParameter("SafeTemperature", 0, 200, 0, "水浴安全温度", true, "℃"),
                        new StringParameter("StatusText", "", "状态描述", true),
                        new BooleanParameter("RotationSwitch", false, "旋转开关(本地记录)", true),
                        new BooleanParameter("HeatingSwitch", false, "加热开关(本地记录)", true),
                        new BooleanParameter("FaultNoRotation", false, "未旋转报警(E01)", true),
                        new BooleanParameter("FaultBathComm", false, "与加热浴失去通讯(E02)", true)
                    }
                },
                AsyncAction = WrapAction("获取所有数据", async parameter =>
                {
                    ComId = Parameters.GetValue<string>("DeviceId");

                    double speed, temp, speedSv, tempSv, safeTemp;
                    string status;

                    if (IsSimulationMode)
                    {
                        await Task.Delay(200);
                        var rnd = new Random();
                        if (_simRotating) _simSpeed += (_simSpeedSv - _simSpeed) * 0.35 + (rnd.NextDouble() - 0.5) * 3;
                        else _simSpeed += (0 - _simSpeed) * 0.4;
                        if (_simSpeed < 1) _simSpeed = 0;
                        if (_simHeating) _simTemp += (_simTempSv - _simTemp) * 0.10 + (rnd.NextDouble() - 0.5) * 0.3;
                        else _simTemp += (25 - _simTemp) * 0.04;

                        speed = Math.Round(_simSpeed);
                        temp = Math.Round(_simTemp, 1);
                        speedSv = _simSpeedSv;
                        tempSv = _simTempSv;
                        safeTemp = 190;
                        status = "1";   // 远程模式
                        _lastRotating = _simRotating;
                        _lastHeating = _simHeating;
                    }
                    else
                    {
                        speed = await ReadValueAsync(CmdGetSpeed, _lastSpeed);
                        temp = await ReadValueAsync(CmdGetTemp, _lastTemp);
                        speedSv = await ReadValueAsync(CmdGetSpeedSet, _lastSpeedSv);
                        tempSv = await ReadValueAsync(CmdGetTempSet, _lastTempSv);
                        safeTemp = await ReadValueAsync(CmdGetTempSafe, 0);

                        // STATUS 回的是状态码原文,不是数值,单独处理
                        try { status = (await _proto.QueryAsync(CmdGetStatus))?.Trim() ?? ""; }
                        catch (TimeoutException) { status = _lastStatus; }

                        InfoLog($"读数:转速 {speed}rpm 水浴 {temp}℃ 设定 {speedSv}rpm/{tempSv}℃ 状态 {status}");
                    }

                    _lastStatus = status;
                    PushReadingsToUI(speed, temp, speedSv, tempSv);

                    return new DeviceParameters()
                    {
                        Variables = new ObservableCollection<ParameterBase>()
                        {
                            new NumberParameter("RotationSpeed", 0, 280, speed, "旋转速度", true, "RPM"),
                            new NumberParameter("BathTemperature", -10, 200, temp, "水浴温度", true, "℃"),
                            new NumberParameter("RotationSpeedSV", 0, 280, speedSv, "旋转速度设定值", true, "RPM"),
                            new NumberParameter("BathTemperatureSV", 0, 180, tempSv, "水浴温度设定值", true, "℃"),
                            new NumberParameter("SafeTemperature", 0, 200, safeTemp, "水浴安全温度", true, "℃"),
                            new StringParameter("StatusText", DescribeStatus(status), "状态描述", true),
                            new BooleanParameter("RotationSwitch", _lastRotating, "旋转开关(本地记录)", true),
                            new BooleanParameter("HeatingSwitch", _lastHeating, "加热开关(本地记录)", true),
                            new BooleanParameter("FaultNoRotation", status == "E01", "未旋转报警(E01)", true),
                            new BooleanParameter("FaultBathComm", status == "E02", "与加热浴失去通讯(E02)", true)
                        }
                    };
                })
            });

            // ════════════ 复位 ════════════
            Commands.Add(new DeviceCommand()
            {
                Name = "复位",
                HelpText = "发 RESET,停止所有输出并把设备交还本地面板控制。远程控制结束时应当调用。",
                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new BooleanParameter("Success", false, "复位成功", true)
                    }
                },
                AsyncAction = WrapAction("复位", async parameter =>
                {
                    ComId = Parameters.GetValue<string>("DeviceId");
                    bool success;

                    if (IsSimulationMode)
                    {
                        await Task.Delay(200);
                        _simRotating = _simHeating = false;
                        success = true;
                    }
                    else
                    {
                        success = await _proto.SendAsync(CmdReset);
                    }

                    if (success)
                    {
                        _lastRotating = _lastHeating = false;
                        OnStateChanged("RotationSwitch", false);
                        OnStateChanged("HeatingSwitch", false);
                    }

                    return new DeviceParameters()
                    {
                        Variables = new ObservableCollection<ParameterBase>()
                        {
                            new BooleanParameter("Success", success, "复位成功", true)
                        }
                    };
                })
            });
        }

        /// <summary>STATUS 命令的状态码含义(照抄 PyLabware 的 STATUS_CODES)。</summary>
        private static string DescribeStatus(string code) => code switch
        {
            "0" => "本地操作",
            "1" => "远程操作",
            "E01" => "未旋转",
            "E02" => "与加热浴失去通讯",
            "" => "--",
            _ => $"未知状态({code})"
        };

        /// <summary>读一个 IN_PV_x / IN_SP_x 数值,应答尾部的通道号要砍掉。</summary>
        private async Task<double> ReadValueAsync(string command, double fallback)
        {
            try
            {
                string reply = await _proto.QueryAsync(command);
                return AsciiLineProtocol.ParseDouble(AsciiLineProtocol.Slice(reply, null, -2), fallback);
            }
            catch (TimeoutException)
            {
                _logger?.LogWarning("读取 {Command} 超时,沿用上次值 {Fallback}", command, fallback);
                return fallback;
            }
        }

        private void PushReadingsToUI(double speed, double temp, double speedSv, double tempSv)
        {
            if (Math.Abs(speed - _lastSpeed) >= 0.5)
            {
                _lastSpeed = speed;
                OnStateChanged("RotationSpeed", speed);
            }
            if (Math.Abs(temp - _lastTemp) >= 0.05)
            {
                _lastTemp = temp;
                OnStateChanged("BathTemperature", temp);
            }
            if (Math.Abs(speedSv - _lastSpeedSv) >= 0.5)
            {
                _lastSpeedSv = speedSv;
                OnStateChanged("RotationSpeedSV", speedSv);
            }
            if (Math.Abs(tempSv - _lastTempSv) >= 0.05)
            {
                _lastTempSv = tempSv;
                OnStateChanged("BathTemperatureSV", tempSv);
            }
        }

        public override DeviceState GetCurrentState()
        {
            return new DeviceState
            {
                States = new Dictionary<string, object>
                {
                    ["RotationSpeed"] = _lastSpeed,
                    ["BathTemperature"] = _lastTemp,
                    ["RotationSpeedSV"] = _lastSpeedSv,
                    ["BathTemperatureSV"] = _lastTempSv,
                    ["RotationSwitch"] = _lastRotating,
                    ["HeatingSwitch"] = _lastHeating
                }
            };
        }

        #region 连接

        protected override async Task<bool> OnConnectAsync()
        {
            if (IsSimulationMode) return true;

            try
            {
                // 设备名应答前 11 个字符才是名字(PyLabware: slicer args=[None,11])
                string name = AsciiLineProtocol.Slice(await _proto.QueryAsync(CmdGetName), null, 11);
                if (!string.Equals(name?.Trim(), ExpectedName, StringComparison.OrdinalIgnoreCase))
                {
                    _logger?.LogWarning("设备名不匹配:期望 \"{Expected}\",实际 \"{Actual}\"", ExpectedName, name);
                }
                InfoLog($"串口连接成功,设备名 \"{name}\"");
                return true;
            }
            catch (Exception e)
            {
                ErrorLog("串口连接失败", e);
                return false;
            }
        }

        protected override async Task<bool> OnConnectViaGatewayAsync() => await OnConnectAsync();

        protected override Task<bool> OnDisconnectAsync()
        {
            InfoLog("断开 IKA RV10");
            return Task.FromResult(true);
        }

        private Task<IDeviceTransport> GetTransportAsync()
        {
            var serialConfig = new SerialPortConfig
            {
                PortName = Parameters.GetValue<string>("串口号") ?? "COM1",
                BaudRate = int.Parse(Parameters.GetValue<string>("波特率") ?? "9600"),
                Parity = Parameters.GetValue<string>("校验位") ?? "Even",
                DataBits = int.Parse(Parameters.GetValue<string>("数据位") ?? "7"),
                StopBits = Parameters.GetValue<string>("停止位") ?? "One",
                ReadTimeoutMs = 3000,
                WriteTimeoutMs = 3000,
                Framing = SerialFraming.AsciiLine,
                ReplyTerminator = "\r\n",
            };
            return CreateTransportAsync(serialConfig);
        }

        #endregion

        #region 命令包装

        private Func<DeviceParameters, Task<DeviceParameters>> WrapAction(
            string commandName, Func<DeviceParameters, Task<DeviceParameters>> innerAsync)
        {
            return async (DeviceParameters parameters) =>
            {
                _logger.LogInformation($"[IKA RV10 命令-开始] Device={Name} Command={commandName}");
                var sw = Stopwatch.StartNew();
                try
                {
                    var output = await innerAsync(parameters);
                    sw.Stop();
                    _logger.LogInformation($"[IKA RV10 命令-完成] Device={Name} Command={commandName} DurationMs={sw.ElapsedMilliseconds}");
                    return output;
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    _logger.LogError(ex, $"[IKA RV10 命令-异常] Device={Name} Command={commandName} DurationMs={sw.ElapsedMilliseconds}");
                    throw new DeviceCommandExecutionException(Name, commandName, ex.Message, ex);
                }
            };
        }

        #endregion
    }
}

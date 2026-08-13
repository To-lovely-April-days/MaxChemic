using DevicePlugins.Devices;
using MaxChemical.Core;
using MaxChemical.Logging;
using System.Collections.ObjectModel;
using System.ComponentModel.Composition;
using System.Diagnostics;

namespace SelectionValve_IDEX_MXII
{
    /// <summary>
    /// IDEX MX Series II 选择阀 / 进样阀驱动(RS-232 ASCII 行协议)。
    ///
    /// ── 协议来源 ────────────────────────────────────────────────────────
    /// 命令表移植自 PyLabware(Cronin Group,MIT License,Copyright (c) 2021),
    /// 文件 PyLabware/devices/idex_mxii.py,原始依据是 IDEX 阀控制板 USB/串口协议文档。
    /// 只搬协议,不引入任何运行时依赖。
    ///
    /// ── 串口设置 ────────────────────────────────────────────────────────
    /// 19200, 8 数据位, 无校验, 1 停止位。
    /// 注意波特率不是常见的 9600 —— 控制板出厂默认 19200,配 9600 会一个字节都收不到。
    ///
    /// ── 报文格式 ────────────────────────────────────────────────────────
    /// 发送: 命令字母 + [参数] + "\r"      (没有分隔符,参数直接贴在命令后面)
    /// 接收: 数据 + "\r"
    /// 例:  发 "P02\r"  收 "\r"           → 切到 2 号阀位成功
    ///      发 "S\r"    收 "02\r"         → 当前在 2 号阀位
    ///
    /// ── 一个反直觉的设计 ────────────────────────────────────────────────
    /// 这台设备命令成功时只回一个 "\r"(空数据),命令非法时**什么都不回**。
    /// 所以"超时"在这里有两种完全不同的含义:
    ///   设备本来就没连上   → 通讯故障
    ///   设备连着但命令超时 → 命令非法(最常见的是阀位号超出这台阀的通道数)
    /// 驱动里靠"超时后再查一次状态"来区分这两种情况,这是 PyLabware 原逻辑的移植。
    ///
    /// ── 状态码 ──────────────────────────────────────────────────────────
    /// S 命令平时回阀位号(01/02/…),出问题时回两位错误码,见 StatusCodes。
    /// 特别的,"*" 表示正在动作中。
    /// </summary>
    [Export(typeof(IDevice))]
    public class SelectionValve_IDEX_MXII : Device
    {
        private readonly ILogService _logger;
        private readonly AsciiLineProtocol _proto;

        // ── 命令表(照抄自 PyLabware idex_mxii.py)─────────────────────────
        private const string CmdMoveToPosition = "P";   // 切到指定阀位,参数是两位补零的位号
        private const string CmdMoveHome = "M";         // 回到初始位
        private const string CmdGetStatus = "S";        // 查状态:阀位号或错误码
        private const string CmdGetError = "E";         // 查最近一次错误码
        private const string CmdGetFwRev = "R";         // 查固件版本,用来探活
        private const string CmdGetValveProfile = "Q";  // 查阀配置档

        /// <summary>正在动作中的状态标记。</summary>
        private const string StatusBusy = "*";

        /// <summary>
        /// S / E 命令可能回的状态码。除 "*" 外都是故障,
        /// 收到这些说明阀本身有问题,不是通讯问题。
        /// </summary>
        private static readonly Dictionary<string, string> StatusCodes = new()
        {
            ["*"] = "动作中",
            ["44"] = "数据 CRC 错误",
            ["55"] = "数据完整性错误",
            ["66"] = "阀定位错误",
            ["77"] = "阀配置错误或命令错误",
            ["88"] = "非易失存储器错误",
            ["99"] = "阀无法回到初始位",
        };

        /// <summary>等待动作完成的上限。阀切一格通常一百多毫秒,给到 10 秒足够。</summary>
        private const int BusyWaitTimeoutMs = 10000;
        private const int BusyPollIntervalMs = 100;

        // ── 最近一次读数 ──
        private int _lastPosition;
        private string _lastStatusText = "";
        private bool _lastBusy;
        private bool _lastFault;

        // ── 模拟模式状态 ──
        private int _simPosition = 1;

        public SelectionValve_IDEX_MXII()
        {
            _logger = LogManager.GetLogger<SelectionValve_IDEX_MXII>();

            DeviceId = "MXII-01";
            Name = "选择阀(IDEX MX II)";
            Manufacturer = "IDEX MX Series II · RS-232 ASCII";
            ImageLocation = "pack://siteoforigin:,,,/Resources/DeviceIcon/SelectionValveMXII.png";
            Category = DeviceCategories.Valves;
            ComId = "MXII-01";
            ConnectionManager = DeviceConnectionManagerFactory.GetInstance();
            AllowedRegions = new List<RegionType>()
            {
                RegionType.Feed,
                RegionType.Reaction,
                RegionType.Quench,
                RegionType.PostProcess
            };

            Parameters.Variables.Add(new StringParameter("DeviceId", "MXII-01", "设备ID")
            {
                Options = new ObservableCollection<string>() { "MXII-01", "MXII-02", "MXII-03", "MXII-04" },
            });

            // 通道数用于阀位号的合法性预检:超范围的位号发下去设备只会沉默,
            // 在软件这边先拦住比等 3 秒超时再猜要清楚得多。
            Parameters.Variables.Add(new StringParameter("阀通道数", "6",
                "这台阀的实际通道数。两位阀填 2,六通选择阀填 6。填错只会影响软件侧的越界预检。")
            {
                Options = new ObservableCollection<string>() { "2", "4", "6", "8", "10", "12" },
            });

            // IDEX 控制板是 19200/8/None/1
            DeviceParameterOptions.AddSerialParameters(Parameters,
                baudRate: "19200", parity: "None", dataBits: "8", stopBits: "One");

            _proto = new AsciiLineProtocol(GetTransportAsync, msg => _logger?.LogDebug(msg))
            {
                CommandTerminator = "\r",
                ReplyTerminator = "\r",
                // 参数直接贴在命令字母后面,没有分隔符
                ArgsDelimiter = "",
                TimeoutMs = 2000,
                // 这台设备"非法命令 = 不回话",重试三次只是把 2 秒等待放大成 6 秒,
                // 判断结论不会变。所以只试一次,超时立刻走"再查一次状态"那条判别路径。
                MaxRetries = 1,
            };

            InitializeCommands();
        }

        public override IReadOnlyList<CollectableParameter> CollectableParameters { get; } = new[]
        {
            new CollectableParameter("ValvePosition", "阀位", "", source: "获取所有数据", isKey: true),
            new CollectableParameter("IsBusy", "动作中", "", source: "获取所有数据"),
        };

        public override int AutoCollectIntervalMs => 2000;

        public override bool SupportsZLanGateway => true;

        private void InitializeCommands()
        {
            // ════════════ 切换阀位 ════════════
            Commands.Add(new DeviceCommand()
            {
                Name = "切换阀位",
                HelpText = "切到指定阀位(P 命令,位号要补零成两位)。1 号位是初始位。\n" +
                           "对两位进样阀来说:1 号位 = 样品进定量环、洗脱液走废液;2 号位 = 定量环里的样品进分析。\n" +
                           "位号超出这台阀的通道数时设备不会回话,驱动会等到超时再复查一次连接来区分" +
                           "「位号无效」和「设备掉线」。",
                InputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new NumberParameter("Position", 1, 12, 1, "目标阀位", true),
                        new BooleanParameter("WaitForCompletion", true, "等动作完成后再返回", true)
                    }
                },
                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new BooleanParameter("Success", false, "切换成功", true),
                        new NumberParameter("Position", 1, 12, 1, "实际阀位", true),
                        new StringParameter("Error", "", "错误描述", true)
                    }
                },
                AsyncAction = WrapAction("切换阀位", async parameter =>
                {
                    ComId = Parameters.GetValue<string>("DeviceId");
                    int position = (int)parameter.GetValue<double>("Position");
                    bool wait = parameter.GetValue<bool>("WaitForCompletion");
                    int channels = GetChannelCount();

                    OnStateChanged("CommandState_切换阀位", CommandExecutionState.Running);

                    if (position < 1 || position > channels)
                    {
                        // 先在软件侧拦掉:发下去只会换来 2 秒沉默,报错信息还不如这条清楚
                        string msg = $"阀位 {position} 超出通道数 {channels}";
                        _logger?.LogWarning(msg);
                        OnStateChanged("CommandState_切换阀位", CommandExecutionState.Failed);
                        return MoveResult(false, _lastPosition, msg);
                    }

                    if (IsSimulationMode)
                    {
                        await Task.Delay(400);
                        _simPosition = position;
                        _lastPosition = position;
                        OnStateChanged("ValvePosition", (double)position);
                        OnStateChanged("CommandState_切换阀位", CommandExecutionState.Succeeded);
                        return MoveResult(true, position, "");
                    }

                    var r = await MoveAsync(CmdMoveToPosition + position.ToString("D2"), $"切到阀位 {position}");
                    if (r.Ok && wait)
                    {
                        await WaitUntilIdleAsync();
                    }
                    if (r.Ok)
                    {
                        _lastPosition = position;
                        OnStateChanged("ValvePosition", (double)position);
                    }

                    OnStateChanged("CommandState_切换阀位",
                        r.Ok ? CommandExecutionState.Succeeded : CommandExecutionState.Failed);
                    return MoveResult(r.Ok, _lastPosition, r.Error);
                })
            });

            // ════════════ 回到初始位 ════════════
            Commands.Add(new DeviceCommand()
            {
                Name = "回到初始位",
                HelpText = "发 M 命令让阀回到 1 号位。开机后或阀报「无法回到初始位(99)」时用。",
                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new BooleanParameter("Success", false, "执行成功", true),
                        new NumberParameter("Position", 1, 12, 1, "实际阀位", true),
                        new StringParameter("Error", "", "错误描述", true)
                    }
                },
                AsyncAction = WrapAction("回到初始位", async parameter =>
                {
                    ComId = Parameters.GetValue<string>("DeviceId");

                    OnStateChanged("CommandState_回到初始位", CommandExecutionState.Running);

                    if (IsSimulationMode)
                    {
                        await Task.Delay(600);
                        _simPosition = 1;
                        _lastPosition = 1;
                        OnStateChanged("ValvePosition", 1d);
                        OnStateChanged("CommandState_回到初始位", CommandExecutionState.Succeeded);
                        return MoveResult(true, 1, "");
                    }

                    var r = await MoveAsync(CmdMoveHome, "回初始位");
                    if (r.Ok)
                    {
                        await WaitUntilIdleAsync();
                        _lastPosition = 1;
                        OnStateChanged("ValvePosition", 1d);
                    }

                    OnStateChanged("CommandState_回到初始位",
                        r.Ok ? CommandExecutionState.Succeeded : CommandExecutionState.Failed);
                    return MoveResult(r.Ok, _lastPosition, r.Error);
                })
            });

            // ════════════ 定量环取样 ════════════
            Commands.Add(new DeviceCommand()
            {
                Name = "定量环取样",
                HelpText = "切到 2 号位保持指定秒数,然后切回 1 号位 —— 就是一次完整的定量环进样。\n" +
                           "移植自 PyLabware 的 sample() 方法。保持时间要覆盖定量环被完全冲洗干净所需的时间," +
                           "太短会取到没被样品置换完的残留液。",
                InputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new NumberParameter("Seconds", 0.1, 3600, 5, "在 2 号位保持的时间", true, "秒")
                    }
                },
                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new BooleanParameter("Success", false, "执行成功", true),
                        new StringParameter("Error", "", "错误描述", true)
                    }
                },
                AsyncAction = WrapAction("定量环取样", async parameter =>
                {
                    ComId = Parameters.GetValue<string>("DeviceId");
                    double seconds = Math.Max(0.1, parameter.GetValue<double>("Seconds"));

                    OnStateChanged("CommandState_定量环取样", CommandExecutionState.Running);

                    if (IsSimulationMode)
                    {
                        _simPosition = 2;
                        await Task.Delay((int)(seconds * 1000));
                        _simPosition = 1;
                        _lastPosition = 1;
                        OnStateChanged("CommandState_定量环取样", CommandExecutionState.Succeeded);
                        return SimpleResult(true, "");
                    }

                    var toLoop = await MoveAsync(CmdMoveToPosition + "02", "切到阀位 2");
                    if (!toLoop.Ok)
                    {
                        OnStateChanged("CommandState_定量环取样", CommandExecutionState.Failed);
                        return SimpleResult(false, toLoop.Error);
                    }
                    await WaitUntilIdleAsync();
                    _lastPosition = 2;
                    OnStateChanged("ValvePosition", 2d);

                    await Task.Delay((int)(seconds * 1000));

                    // 回 1 号位是必须做完的:停在 2 号位会让样品一直流向分析端。
                    // 就算上面出错也要试着切回来,所以这一步不放在 if 里。
                    var back = await MoveAsync(CmdMoveToPosition + "01", "切回阀位 1");
                    if (back.Ok)
                    {
                        await WaitUntilIdleAsync();
                        _lastPosition = 1;
                        OnStateChanged("ValvePosition", 1d);
                    }

                    OnStateChanged("CommandState_定量环取样",
                        back.Ok ? CommandExecutionState.Succeeded : CommandExecutionState.Failed);
                    return SimpleResult(back.Ok, back.Error);
                })
            });

            // ════════════ 获取所有数据 ════════════
            Commands.Add(new DeviceCommand()
            {
                Name = "获取所有数据",
                HelpText = "查当前阀位与故障状态(S 命令)。S 回的要么是两位阀位号,要么是两位故障码," +
                           "要么是表示动作中的 \"*\"。故障时再发一次 E 取最近的错误码。",
                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new NumberParameter("ValvePosition", 0, 12, 0, "阀位", true),
                        new StringParameter("StatusText", "", "状态描述", true),
                        new BooleanParameter("IsBusy", false, "动作中", true),
                        new BooleanParameter("HasFault", false, "存在故障", true)
                    }
                },
                AsyncAction = WrapAction("获取所有数据", async parameter =>
                {
                    ComId = Parameters.GetValue<string>("DeviceId");

                    if (IsSimulationMode)
                    {
                        await Task.Delay(120);
                        _lastPosition = _simPosition;
                        _lastStatusText = $"阀位 {_simPosition}";
                        _lastBusy = false;
                        _lastFault = false;
                    }
                    else
                    {
                        string status;
                        try
                        {
                            status = (await _proto.QueryAsync(CmdGetStatus))?.Trim() ?? "";
                        }
                        catch (TimeoutException)
                        {
                            // S 是查询命令,不该超时。超时说明链路断了。
                            _lastBusy = false;
                            _lastFault = true;
                            _lastStatusText = "无应答(检查串口与波特率 19200)";
                            return StatusResult(_lastPosition, _lastStatusText, false, true);
                        }

                        _lastBusy = status == StatusBusy;

                        if (StatusCodes.TryGetValue(status, out var desc))
                        {
                            _lastFault = !_lastBusy;
                            _lastStatusText = desc;
                            if (_lastFault)
                            {
                                // 故障时补查一次 E,拿到的错误码写进日志,方便回溯
                                try
                                {
                                    string err = (await _proto.QueryAsync(CmdGetError))?.Trim() ?? "";
                                    _logger?.LogWarning("阀故障:{Desc},E 命令返回 {Err}", desc, err);
                                }
                                catch (TimeoutException) { /* 补查失败不影响主判断 */ }
                            }
                        }
                        else
                        {
                            // 不在状态码表里,那就是阀位号
                            _lastFault = false;
                            int pos = AsciiLineProtocol.ParseInt(status, _lastPosition);
                            if (pos != _lastPosition)
                            {
                                _lastPosition = pos;
                                OnStateChanged("ValvePosition", (double)pos);
                            }
                            _lastStatusText = $"阀位 {pos}";
                        }
                    }

                    return StatusResult(_lastPosition, _lastStatusText, _lastBusy, _lastFault);
                })
            });
        }

        private static DeviceParameters MoveResult(bool ok, int position, string error) => new DeviceParameters()
        {
            Variables = new ObservableCollection<ParameterBase>()
            {
                new BooleanParameter("Success", ok, "执行成功", true),
                new NumberParameter("Position", 0, 12, position, "实际阀位", true),
                new StringParameter("Error", error ?? "", "错误描述", true)
            }
        };

        private static DeviceParameters SimpleResult(bool ok, string error) => new DeviceParameters()
        {
            Variables = new ObservableCollection<ParameterBase>()
            {
                new BooleanParameter("Success", ok, "执行成功", true),
                new StringParameter("Error", error ?? "", "错误描述", true)
            }
        };

        private static DeviceParameters StatusResult(int position, string text, bool busy, bool fault)
            => new DeviceParameters()
            {
                Variables = new ObservableCollection<ParameterBase>()
                {
                    new NumberParameter("ValvePosition", 0, 12, position, "阀位", true),
                    new StringParameter("StatusText", text ?? "", "状态描述", true),
                    new BooleanParameter("IsBusy", busy, "动作中", true),
                    new BooleanParameter("HasFault", fault, "存在故障", true)
                }
            };

        #region 收发

        private readonly struct MoveOutcome
        {
            public bool Ok { get; init; }
            public string Error { get; init; }
        }

        /// <summary>
        /// 发一条动作命令(P/M)。成功时设备只回一个 "\r",剥掉结束符后是空串,
        /// 所以"应答为空"在这里是正常的,不能当失败处理。
        ///
        /// 超时的判别是这台设备的关键逻辑:非法命令与掉线都表现为不回话。
        /// 超时后补发一次 R(查固件版本)—— 能回就说明设备还在,那刚才那条命令本身有问题;
        /// 还是不回就是真掉线了。这段是 PyLabware set_valve_position 里
        /// try/except PLConnectionTimeoutError 那几行的移植。
        /// </summary>
        private async Task<MoveOutcome> MoveAsync(string command, string label)
        {
            try
            {
                await _proto.QueryAsync(command);
                InfoLog($"{label}:已下发");
                return new MoveOutcome { Ok = true, Error = "" };
            }
            catch (TimeoutException)
            {
                bool alive;
                try
                {
                    string rev = await _proto.QueryAsync(CmdGetFwRev);
                    alive = !string.IsNullOrWhiteSpace(rev);
                }
                catch (Exception)
                {
                    alive = false;
                }

                string error = alive
                    ? $"{label} 被设备拒绝(命令或阀位无效),但设备在线"
                    : $"{label} 无应答,设备可能未连接";
                _logger?.LogWarning(error);
                return new MoveOutcome { Ok = false, Error = error };
            }
        }

        /// <summary>
        /// 轮询 S 直到不再回 "*"。阀切一格一般一百多毫秒,超时不抛异常只记警告 ——
        /// 卡在这里会让整条配方停摆,而真出问题时下一条命令自己会报更准确的错。
        /// </summary>
        private async Task WaitUntilIdleAsync()
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < BusyWaitTimeoutMs)
            {
                string status;
                try { status = (await _proto.QueryAsync(CmdGetStatus))?.Trim() ?? ""; }
                catch (TimeoutException) { return; }   // 查不到就别死等

                if (status != StatusBusy)
                {
                    _lastBusy = false;
                    return;
                }
                await Task.Delay(BusyPollIntervalMs);
            }
            _logger?.LogWarning("等待阀动作完成超过 {Timeout}ms 仍在忙", BusyWaitTimeoutMs);
        }

        private int GetChannelCount()
        {
            int n = AsciiLineProtocol.ParseInt(Parameters.GetValue<string>("阀通道数") ?? "6", 6);
            return n < 2 ? 2 : (n > 12 ? 12 : n);
        }

        #endregion

        public override DeviceState GetCurrentState()
        {
            return new DeviceState
            {
                States = new Dictionary<string, object>
                {
                    ["ValvePosition"] = (double)_lastPosition,
                    ["StatusText"] = _lastStatusText,
                    ["IsBusy"] = _lastBusy,
                    ["HasFault"] = _lastFault
                }
            };
        }

        #region 连接

        protected override async Task<bool> OnConnectAsync()
        {
            if (IsSimulationMode) return true;

            try
            {
                // 用查固件版本探活 —— 这是这台设备唯一一条"一定有应答"的命令
                string rev = await _proto.QueryAsync(CmdGetFwRev);
                if (string.IsNullOrWhiteSpace(rev))
                {
                    ErrorLog("连接失败:R 命令无有效应答", null);
                    return false;
                }
                InfoLog($"串口连接成功,固件版本 \"{rev}\"");

                try
                {
                    string profile = await _proto.QueryAsync(CmdGetValveProfile);
                    _logger?.LogInformation("阀配置档:{Profile}", profile);
                }
                catch (TimeoutException) { /* 配置档读不到不影响使用 */ }

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
            InfoLog("断开 IDEX MX II");
            return Task.FromResult(true);
        }

        private Task<IDeviceTransport> GetTransportAsync()
        {
            var serialConfig = new SerialPortConfig
            {
                PortName = Parameters.GetValue<string>("串口号") ?? "COM1",
                BaudRate = int.Parse(Parameters.GetValue<string>("波特率") ?? "19200"),
                Parity = Parameters.GetValue<string>("校验位") ?? "None",
                DataBits = int.Parse(Parameters.GetValue<string>("数据位") ?? "8"),
                StopBits = Parameters.GetValue<string>("停止位") ?? "One",
                ReadTimeoutMs = 2000,
                WriteTimeoutMs = 2000,
                Framing = SerialFraming.AsciiLine,
                // 单字符结束符。这台设备成功时只回这一个字符,所以分帧完全靠它。
                ReplyTerminator = "\r",
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
                _logger.LogInformation($"[IDEX MXII 命令-开始] Device={Name} Command={commandName}");
                var sw = Stopwatch.StartNew();
                try
                {
                    var output = await innerAsync(parameters);
                    sw.Stop();
                    _logger.LogInformation($"[IDEX MXII 命令-完成] Device={Name} Command={commandName} DurationMs={sw.ElapsedMilliseconds}");
                    return output;
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    _logger.LogError(ex, $"[IDEX MXII 命令-异常] Device={Name} Command={commandName} DurationMs={sw.ElapsedMilliseconds}");
                    throw new DeviceCommandExecutionException(Name, commandName, ex.Message, ex);
                }
            };
        }

        #endregion
    }
}

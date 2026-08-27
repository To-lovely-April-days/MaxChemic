// ============================================================================
//  PenMeterProtocol.cs
//  笔式酸度-电导率计 BLE 协议的编解码层(纯计算,不含任何 IO)。
//
//  依据《笔式酸度-电导率计 BLE 协议》(2024.2.24 第 7 版)。
//
//  标准封装格式:
//      帧头(0xFF) | 机器类型(1) | 功能码(1) | 数据长度(1) | 数据(n) | CRC8(1) | 帧尾(0xF0)
//
//  三条必须记牢的规则:
//    1) CRC8 的**校验范围是"机器类型 ~ 数据最后一个字节"**,不含帧头 0xFF,
//       也不含 CRC 自己和帧尾 0xF0。算错范围是这类协议最常见的坑。
//    2) CRC8-MAXIM(POLY 0x31,INIT 0x00)。手册附了一张 256 字节查表,
//       本文件用等价的按位算法(反射多项式 0x8C)现算 —— 已用手册自带的三个
//       报警帧样例和测量帧样例逐一核对过,四个全对,所以不必再抄那张表。
//    3) **多字节数据一律大端**(高字节在前)。这一点和上面那台触摸屏酸度计
//       (小端 float)正好相反,两个驱动别互相抄。
//
//  另:手册第 5 页给的测量帧样例
//      FF 10 00 0A 00 00 00 00 00 00 00 00 00 00 C7 F0
//  长度字段是 0x0A=10,而正文写的是"数据长度:17 字节"。修订记录里
//  0x00 指令在第 3 版改过、第 5 版又加了 5 点校准标志,可见这个样例是旧版残留。
//  因此解析一律**以帧里的长度字段为准**,字段不够就留空,不按 17 硬读,
//  这样新旧固件都不会读越界。
// ============================================================================

using System;

namespace PenMeter_BLE
{
    /// <summary>功能码。上行(下位机→上位机)0x00~0x06,下行(上位机→下位机)0x80~0x85。</summary>
    internal static class PenFunc
    {
        public const byte MeasureData = 0x00;   // 下位机主动:测量数据(测量模式)
        public const byte ModeData = 0x01;   // 下位机主动:多参数测量模式切换
        public const byte SettingData = 0x02;   // 下位机主动:设置数据(设置模式)
        public const byte CalibrationData = 0x03;   // 下位机主动:校准数据(校准模式)
        public const byte WorkModeChanged = 0x04;   // 下位机主动:工作模式切换
        public const byte AlarmSignal = 0x05;   // 下位机主动:报警信号
        public const byte ErrorCode = 0x06;   // 下位机主动:错误编号

        public const byte SetMeasureMode = 0x80;   // 上位机:多参数模式切换
        public const byte SetSettings = 0x81;   // 上位机:设置数据
        public const byte ConfirmCalibration = 0x82;   // 上位机:确认校准命令
        public const byte SetWorkMode = 0x83;   // 上位机:工作模式切换
        public const byte SetHold = 0x84;   // 上位机:数据保持命令
        public const byte SetAlarm = 0x85;   // 上位机:报警命令
    }

    /// <summary>一帧(已校验通过)。</summary>
    internal sealed class PenFrame
    {
        public byte MachineType { get; init; }
        public byte Func { get; init; }
        public byte[] Data { get; init; } = Array.Empty<byte>();
    }

    /// <summary>测量数据(功能码 0x00)。字段不足时相应属性为 null。</summary>
    internal sealed class PenMeasure
    {
        public DateTime TimestampUtc { get; init; } = DateTime.UtcNow;

        /// <summary>测量值(已 /100)。</summary>
        public double Value { get; init; }

        /// <summary>测量值单位码,见 <see cref="PenMeterProtocol.UnitText"/>。</summary>
        public int UnitCode { get; init; }

        /// <summary>温度(已 /10)。</summary>
        public double Temperature { get; init; }

        /// <summary>温度单位:0=℃,1=℉。</summary>
        public int TempUnitCode { get; init; }

        public bool IsStable { get; init; }
        public bool IsHold { get; init; }
        public bool OverHigh { get; init; }
        public bool OverLow { get; init; }

        /// <summary>测量模式:0=PH,1=ORP,2=Cond,3=TDS,4=Sal,5=Res。</summary>
        public int Mode { get; init; }

        /// <summary>电量百分比 0~100。</summary>
        public int Battery { get; init; }

        /// <summary>外部标定信息位图:L1=0x01 M=0x02 H1=0x04 L2=0x08 H2=0x10。</summary>
        public int CalibrationFlags { get; init; }

        /// <summary>PH 校准零电位(已 /10),单位 mV。</summary>
        public double ZeroPotential { get; init; }

        /// <summary>PH 校准斜率(已 /10),单位 %。</summary>
        public double Slope { get; init; }

        public string UnitText => PenMeterProtocol.UnitText(UnitCode);
        public string TempUnitText => TempUnitCode == 1 ? "℉" : "℃";
        public string ModeText => PenMeterProtocol.ModeText(Mode);

        /// <summary>已完成的校准点,例如 "L1 M H1"。一个都没有时返回 "未校准"。</summary>
        public string CalibrationText => PenMeterProtocol.CalibrationText(CalibrationFlags);
    }

    /// <summary>
    /// 设置数据(功能码 0x02 上行 / 0x81 下行,数据域固定 21 字节)。
    ///
    /// 上下行的 21 字节布局完全一样,所以这一个类同时负责解析仪器推来的当前配置
    /// 和拼装要下发的配置。
    ///
    /// **下发时必须以仪器推上来的当前配置为底稿改一项**,不能凭空造 21 个字节:
    /// 里面混着"恢复出厂""屏幕自检"这种一碰就动作的项,还有参比温度、电极常数
    /// 这些清零就等于把仪器配坏的项。驱动层为此规定:没收到过 0x02 就拒绝下发。
    /// </summary>
    internal sealed class PenSettings
    {
        public byte TempUnit;            // [0]  0=℃ 1=℉
        public byte AutoLock;            // [1]  0=不锁定;5~20=锁定秒数
        public byte AutoBacklight;       // [2]  0=关闭;1~8=分钟
        public byte AutoPowerOff;        // [3]  0=关闭;10~20=分钟
        public byte PhBuffer;            // [4]  0=CH 1=USA 2=NIST
        public byte PhResolution;        // [5]  0=1 1=0.1 2=0.01
        public byte PhRemindHour;        // [6]  校准提醒 高字节=小时
        public byte PhRemindDay;         // [7]  校准提醒 低字节=天(0-0 表示关闭)
        public byte PhFactoryReset;      // [8]  0=否 1=是
        public byte CondRefTemp;         // [9]  15~30 ℃
        public byte CondTempCoefHi;      // [10] 温补系数 0~999,/100,单位 %
        public byte CondTempCoefLo;      // [11]
        public byte CondPhTempComp;      // [12] 电导率/Ph 温度补偿值 0~99 ℃
        public byte CondStdSolution;     // [13] 0=CH 1=USA
        public byte CondCellConstant;    // [14] 80~120,/100
        public byte CondRemindHour;      // [15]
        public byte CondRemindDay;       // [16]
        public byte CondFactoryReset;    // [17] 0=否 1=是
        public byte TdsCoefficient;      // [18] 40~100,/100
        public byte SalinityUnit;        // [19] 0=ppt 1=mg/L
        public byte ScreenSelfTest;      // [20] 0=否 1=是

        public const int Length = 21;

        /// <summary>电导率温度补偿系数(已 /100),单位 %。</summary>
        public double CondTempCoefficient => ((CondTempCoefHi << 8) | CondTempCoefLo) / 100.0;

        /// <summary>电导率电极常数(已 /100)。</summary>
        public double CellConstant => CondCellConstant / 100.0;

        /// <summary>TDS 系数(已 /100)。</summary>
        public double TdsFactor => TdsCoefficient / 100.0;

        public static PenSettings Parse(byte[] d)
        {
            if (d is null || d.Length < Length)
                throw new ArgumentException($"设置数据应为 {Length} 字节,实际 {d?.Length ?? 0}");

            return new PenSettings
            {
                TempUnit = d[0],
                AutoLock = d[1],
                AutoBacklight = d[2],
                AutoPowerOff = d[3],
                PhBuffer = d[4],
                PhResolution = d[5],
                PhRemindHour = d[6],
                PhRemindDay = d[7],
                PhFactoryReset = d[8],
                CondRefTemp = d[9],
                CondTempCoefHi = d[10],
                CondTempCoefLo = d[11],
                CondPhTempComp = d[12],
                CondStdSolution = d[13],
                CondCellConstant = d[14],
                CondRemindHour = d[15],
                CondRemindDay = d[16],
                CondFactoryReset = d[17],
                TdsCoefficient = d[18],
                SalinityUnit = d[19],
                ScreenSelfTest = d[20],
            };
        }

        public byte[] ToBytes() => new[]
        {
            TempUnit, AutoLock, AutoBacklight, AutoPowerOff, PhBuffer, PhResolution,
            PhRemindHour, PhRemindDay, PhFactoryReset, CondRefTemp,
            CondTempCoefHi, CondTempCoefLo, CondPhTempComp, CondStdSolution,
            CondCellConstant, CondRemindHour, CondRemindDay, CondFactoryReset,
            TdsCoefficient, SalinityUnit, ScreenSelfTest,
        };

        public PenSettings Clone() => Parse(ToBytes());
    }

    /// <summary>校准数据(功能码 0x03,数据域 15 字节)。</summary>
    internal sealed class PenCalibration
    {
        public DateTime TimestampUtc { get; init; } = DateTime.UtcNow;

        /// <summary>校准类型:0=PH 校准,1=Cond 校准。</summary>
        public int Type { get; init; }

        /// <summary>当前测量值(已 /100)。</summary>
        public double Measured { get; init; }

        /// <summary>测量值单位:0=ph,1=uS/cm,2=mS/cm。</summary>
        public int MeasuredUnit { get; init; }

        /// <summary>标准液标称值(已 /100)。</summary>
        public double Standard { get; init; }

        public int StandardUnit { get; init; }

        public bool IsStable { get; init; }

        /// <summary>已校准点位图(L1/M/H1/L2/H2)。</summary>
        public int CalibrationFlags { get; init; }

        /// <summary>仪器侧是否已确认本次校准值。</summary>
        public bool Confirmed { get; init; }

        /// <summary>校准错误编号(手册未给取值表,原样透出)。</summary>
        public int ErrorCode { get; init; }

        public string TypeText => Type == 1 ? "电导率校准" : "PH 校准";
        public string MeasuredUnitText => PenMeterProtocol.CalUnitText(MeasuredUnit);
        public string StandardUnitText => PenMeterProtocol.CalUnitText(StandardUnit);
        public string CalibrationText => PenMeterProtocol.CalibrationText(CalibrationFlags);
    }

    internal static class PenMeterProtocol
    {
        public const byte Head = 0xFF;
        public const byte Terminator = 0xF0;

        /// <summary>机器类型:0x10 = 笔式多参数酸度电导率仪。</summary>
        public const byte MachineTypePen = 0x10;

        /// <summary>帧的固定开销:帧头 + 机器类型 + 功能码 + 长度 + CRC + 帧尾。</summary>
        public const int Overhead = 6;

        #region CRC8-MAXIM

        /// <summary>
        /// CRC8-MAXIM(POLY 0x31,INIT 0x00)。
        /// 手册给的是查表实现,这里用等价的反射按位算法(0x31 反射后是 0x8C)。
        /// 手册自带的四个样例(三个报警帧 + 一个测量帧)已全部核对通过。
        /// </summary>
        public static byte Crc8(byte[] data, int offset, int count)
        {
            byte crc = 0x00;
            for (int i = offset; i < offset + count; i++)
            {
                crc ^= data[i];
                for (int bit = 0; bit < 8; bit++)
                    crc = (byte)((crc & 0x01) != 0 ? (crc >> 1) ^ 0x8C : crc >> 1);
            }
            return crc;
        }

        #endregion

        #region 组包

        /// <summary>按标准封装格式拼一帧。CRC 覆盖机器类型~数据末字节。</summary>
        public static byte[] BuildFrame(byte func, params byte[]? data)
        {
            data ??= Array.Empty<byte>();
            if (data.Length > 255)
                throw new ArgumentException("数据域超过 255 字节");

            var frame = new byte[Overhead + data.Length];
            frame[0] = Head;
            frame[1] = MachineTypePen;
            frame[2] = func;
            frame[3] = (byte)data.Length;
            Buffer.BlockCopy(data, 0, frame, 4, data.Length);
            // 校验范围:frame[1] 起,长度 = 3(类型/功能码/长度) + 数据长度
            frame[4 + data.Length] = Crc8(frame, 1, 3 + data.Length);
            frame[5 + data.Length] = Terminator;
            return frame;
        }

        public static byte[] BuildSetMeasureMode(byte mode) => BuildFrame(PenFunc.SetMeasureMode, mode);
        public static byte[] BuildSetWorkMode(byte mode) => BuildFrame(PenFunc.SetWorkMode, mode);
        public static byte[] BuildConfirmCalibration(bool yes) => BuildFrame(PenFunc.ConfirmCalibration, (byte)(yes ? 1 : 0));
        public static byte[] BuildHold(bool hold) => BuildFrame(PenFunc.SetHold, (byte)(hold ? 1 : 0));

        /// <summary>报警命令:0=不报警,1=超上限,2=超下限。</summary>
        public static byte[] BuildAlarm(byte level) => BuildFrame(PenFunc.SetAlarm, level);

        public static byte[] BuildSettings(PenSettings s) => BuildFrame(PenFunc.SetSettings, s.ToBytes());

        #endregion

        #region 拆包

        /// <summary>
        /// 从缓冲区里取一帧,语义与触摸屏酸度计那边的 TryReadFrame 一致:
        ///   consumed = 0  数据还不够,等下一批;
        ///   consumed = 1  当前位置不是合法帧的起点,前移一个字节重新找;
        ///   consumed > 1  取到一整帧。
        /// 蓝牙透传会把数据切成任意大小的块,半帧和粘包是常态,重同步不能省。
        /// </summary>
        public static bool TryReadFrame(byte[] buffer, int offset, int available,
                                        out PenFrame? frame, out int consumed)
        {
            frame = null;
            consumed = 0;

            if (available <= 0) return false;

            if (buffer[offset] != Head)
            {
                consumed = 1;
                return false;
            }

            if (available < 4) return false;            // 还没收到长度字段

            int dataLen = buffer[offset + 3];
            int frameLen = Overhead + dataLen;
            if (available < frameLen) return false;     // 整帧没收全

            if (buffer[offset + frameLen - 1] != Terminator)
            {
                consumed = 1;                           // 帧尾不对,这个 0xFF 是巧合
                return false;
            }

            byte expect = Crc8(buffer, offset + 1, 3 + dataLen);
            if (buffer[offset + frameLen - 2] != expect)
            {
                consumed = 1;                           // CRC 不过,同样按一字节重同步
                return false;
            }

            var data = new byte[dataLen];
            Buffer.BlockCopy(buffer, offset + 4, data, 0, dataLen);

            consumed = frameLen;
            frame = new PenFrame
            {
                MachineType = buffer[offset + 1],
                Func = buffer[offset + 2],
                Data = data
            };
            return true;
        }

        /// <summary>
        /// 解测量数据(0x00)。按帧里实际给的长度逐字段取,缺的留默认值 ——
        /// 手册样例的长度(10)与正文(17)不一致,不能按固定 17 硬读。
        /// </summary>
        public static PenMeasure ParseMeasure(byte[] d)
        {
            int status = d.Length >= 10 ? U16(d, 8) : 0;

            return new PenMeasure
            {
                Value = d.Length >= 4 ? I32(d, 0) / 100.0 : 0,
                UnitCode = d.Length >= 5 ? d[4] : 0,
                Temperature = d.Length >= 7 ? I16(d, 5) / 10.0 : 0,
                TempUnitCode = d.Length >= 8 ? d[7] : 0,

                IsStable = (status & 0x0001) != 0,
                IsHold = (status & 0x0002) != 0,
                OverHigh = (status & 0x0004) != 0,
                OverLow = (status & 0x0008) != 0,

                Mode = d.Length >= 11 ? d[10] : 0,
                Battery = d.Length >= 12 ? d[11] : 0,
                CalibrationFlags = d.Length >= 13 ? d[12] : 0,
                ZeroPotential = d.Length >= 15 ? I16(d, 13) / 10.0 : 0,
                Slope = d.Length >= 17 ? I16(d, 15) / 10.0 : 0,
            };
        }

        /// <summary>
        /// 解多参数测量模式切换(0x01,7 字节)。它只带模式/电量/校准信息,
        /// 没有测量值 —— 用它去补 <see cref="PenMeasure"/> 里对应的几项即可。
        /// </summary>
        public static PenMeasure ParseModeData(byte[] d, PenMeasure? previous)
        {
            return new PenMeasure
            {
                // 测量值沿用上一帧,避免切模式的瞬间读数闪回 0
                Value = previous?.Value ?? 0,
                UnitCode = previous?.UnitCode ?? 0,
                Temperature = previous?.Temperature ?? 0,
                TempUnitCode = previous?.TempUnitCode ?? 0,
                IsStable = previous?.IsStable ?? false,
                IsHold = previous?.IsHold ?? false,
                OverHigh = previous?.OverHigh ?? false,
                OverLow = previous?.OverLow ?? false,

                Mode = d.Length >= 1 ? d[0] : 0,
                Battery = d.Length >= 2 ? d[1] : 0,
                CalibrationFlags = d.Length >= 3 ? d[2] : 0,
                ZeroPotential = d.Length >= 5 ? I16(d, 3) / 10.0 : 0,
                Slope = d.Length >= 7 ? I16(d, 5) / 10.0 : 0,
            };
        }

        /// <summary>解校准数据(0x03,15 字节)。</summary>
        public static PenCalibration ParseCalibration(byte[] d)
        {
            return new PenCalibration
            {
                Type = d.Length >= 1 ? d[0] : 0,
                Measured = d.Length >= 5 ? I32(d, 1) / 100.0 : 0,
                MeasuredUnit = d.Length >= 6 ? d[5] : 0,
                Standard = d.Length >= 10 ? I32(d, 6) / 100.0 : 0,
                StandardUnit = d.Length >= 11 ? d[10] : 0,
                IsStable = d.Length >= 12 && d[11] != 0,
                CalibrationFlags = d.Length >= 13 ? d[12] : 0,
                Confirmed = d.Length >= 14 && d[13] != 0,
                ErrorCode = d.Length >= 15 ? d[14] : 0,
            };
        }

        #endregion

        #region 取值(一律大端)

        private static int I32(byte[] d, int o) =>
            (d[o] << 24) | (d[o + 1] << 16) | (d[o + 2] << 8) | d[o + 3];

        private static short I16(byte[] d, int o) => (short)((d[o] << 8) | d[o + 1]);

        private static int U16(byte[] d, int o) => (d[o] << 8) | d[o + 1];

        #endregion

        #region 编码 → 文字

        private static readonly string[] Units =
        {
            "pH", "mV", "uS/cm", "mS/cm", "%", "Ω", "kΩ", "MΩ", "mg/L", "ppm", "ppt"
        };

        public static string UnitText(int code) =>
            code >= 0 && code < Units.Length ? Units[code] : $"?{code}";

        private static readonly string[] Modes =
        {
            // 修订记录 2024.2.23:"Mv 模式改为 ORP 模式",所以 1 是 ORP 不是 mV
            "PH", "ORP", "Cond", "TDS", "Sal", "Res"
        };

        public static string ModeText(int code) =>
            code >= 0 && code < Modes.Length ? Modes[code] : $"?{code}";

        /// <summary>校准数据里那三个单位码:0=ph,1=uS/cm,2=mS/cm。</summary>
        public static string CalUnitText(int code) => code switch
        {
            0 => "pH",
            1 => "uS/cm",
            2 => "mS/cm",
            _ => $"?{code}"
        };

        /// <summary>把校准点位图翻成 "L1 M H1" 这样的可读串。</summary>
        public static string CalibrationText(int flags)
        {
            if (flags == 0) return "未校准";
            var parts = new List<string>(5);
            if ((flags & 0x01) != 0) parts.Add("L1");
            if ((flags & 0x02) != 0) parts.Add("M");
            if ((flags & 0x04) != 0) parts.Add("H1");
            if ((flags & 0x08) != 0) parts.Add("L2");
            if ((flags & 0x10) != 0) parts.Add("H2");
            return string.Join(" ", parts);
        }

        /// <summary>工作模式码 → 文字。0=关机 1=测量 2=配置 4=校准(3 未定义)。</summary>
        public static string WorkModeText(int code) => code switch
        {
            0 => "关机",
            1 => "测量模式",
            2 => "配置模式",
            4 => "校准模式",
            _ => $"?{code}"
        };

        #endregion
    }
}

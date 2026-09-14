// ============================================================================
//  WaterQualityProtocol.cs
//  便携式三参数水质测定仪(手持式按键款彩屏,pH / 电导率 / 溶解氧)——
//  串口私有协议的编解码层。不含任何 IO,便于单独推敲与复核。
//
//  依据《便携式三参数通信协议文档》V1.0(2025-08-19)。要点:
//
//  1) 物理层 TTL 串口经 USB 转串口接电脑,460800 / 8 / N / 1,**固定不可配**。
//
//  2) 主从问答:上位机发一帧命令,仪器回一帧应答。这与触摸屏酸度计那台
//     "握手后仪器主动推包"完全不同 —— 这台不推,不问就没有数据。
//     所以驱动是"每条读命令一次串口往返",屏幕的轮询周期就是串口往返周期。
//
//  3) 会话:未联机(CMD_CONNECT)之前,仪器对其它一切命令**静默丢弃**,连错误
//     应答都不给。命令超时的第一嫌疑永远是"没联机",不是波特率。
//
//  4) 帧格式(上下行同):
//         帧头 0x15 | 长度 N(1B,只算数据域) | 数据域 N 字节 | 帧尾 0x16
//     无校验、无转义。数据域里可以出现 0x15/0x16,收帧只能靠"长度 + 帧尾"判定。
//     命令帧数据域 = cmd(1B) + 参数;应答帧数据域 = 0x00 + to_cmd + result + 参数。
//
//  5) **字节序是大端**(仪器是 8051 内核)。u16 / u32 / float 全部高字节在前,
//     和触摸屏酸度计(ARM,小端)正好相反。这里统一用 BinaryPrimitives 的
//     BigEndian 版本读写,不要手写位移,也不要照抄酸度计那份代码。
//
//  6) 结构体 1 字节紧凑排列,文档给的偏移可以直接用。
// ============================================================================

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace WaterQualityMeter_UART
{
    /// <summary>命令码(文档 3.2)。保留命令 0x03~0x05、0x07 发了没有应答,不要用。</summary>
    internal static class WqCmd
    {
        public const byte Respond = 0x00;
        public const byte Connect = 0x01;
        public const byte Disconnect = 0x02;     // 无应答
        public const byte GetData = 0x06;
        public const byte Reset = 0x08;
        public const byte GetSetting = 0x09;
        public const byte SetSetting = 0x0A;
        public const byte GetRecord = 0x0B;
        public const byte SetMode = 0x0C;

        public static string Name(byte cmd) => cmd switch
        {
            Connect => "联机",
            Disconnect => "断开",
            GetData => "读实时数据",
            Reset => "恢复出厂",
            GetSetting => "读设置",
            SetSetting => "写设置",
            GetRecord => "读记录",
            SetMode => "切换模式",
            _ => $"0x{cmd:X2}",
        };
    }

    /// <summary>应答结果码(文档 3.3)。</summary>
    internal static class WqResult
    {
        public const byte Success = 0x00;
        public const byte Fail = 0x01;       // 目前只有"读记录索引越界"会返回
        public const byte Busy = 0x02;       // 当前固件未使用

        public static string Name(byte r) => r switch
        {
            Success => "成功",
            Fail => "失败",
            Busy => "忙",
            _ => $"未知结果码 0x{r:X2}",
        };
    }

    /// <summary>
    /// 测量模式(文档 5.1)。同时也是联机应答里 modes_sta 位图的位号:
    /// bit0 = PH … bit8 = SAT,与枚举值一一对应,所以判"是否启用"直接用 1 &lt;&lt; code。
    /// </summary>
    internal static class WqMode
    {
        public const byte PH = 0;
        public const byte MV = 1;
        public const byte ORP = 2;
        public const byte COND = 3;
        public const byte RES = 4;
        public const byte TDS = 5;
        public const byte SAL = 6;
        public const byte DO = 7;
        public const byte SAT = 8;
        public const byte Count = 9;

        public static readonly string[] Codes = { "PH", "MV", "ORP", "COND", "RES", "TDS", "SAL", "DO", "SAT" };

        public static readonly string[] Labels =
        {
            "pH", "mV", "ORP", "电导率", "电阻率", "TDS", "盐度", "溶解氧", "溶氧饱和度",
        };

        public static string Code(int m) => m >= 0 && m < Count ? Codes[m] : $"MODE{m}";
        public static string Label(int m) => m >= 0 && m < Count ? Labels[m] : $"模式 {m}";
    }

    /// <summary>测量值单位(文档 5.2,PC_DATA.disp_unit)。</summary>
    internal static class WqUnit
    {
        private static readonly string[] Names =
        {
            "mS/cm", "uS/cm", "pH", "mV", "mg/L", "MΩ", "KΩ", "Ω", "KΩ·cm", "MΩ·cm", "g/L", "ppt", "%",
        };

        // 文档里 0x07 写的是"Ω(无后缀字符串)",0x08/0x09 写的是 K·cm / M·cm,
        // 按电阻率的行业写法补成 KΩ·cm / MΩ·cm。真机上如果字面不同,只改这张表。
        public static string Name(int code) => code >= 0 && code < Names.Length ? Names[code] : $"unit{code}";
    }

    /// <summary>联机应答参数 PC_DEVICE_INFO(3 字节)。</summary>
    internal sealed class WqDeviceInfo
    {
        /// <summary>设备 ID,本机型固定为 1。</summary>
        public byte DeviceId { get; init; }

        /// <summary>模式使能位图,bit0 = PH … bit8 = SAT。</summary>
        public ushort ModesSta { get; init; }

        public bool IsModeEnabled(int mode) => mode >= 0 && mode < 16 && (ModesSta & (1 << mode)) != 0;

        /// <summary>已启用模式的代号列表,例如 "PH,COND,DO"。</summary>
        public string EnabledModesText
        {
            get
            {
                var list = new List<string>();
                for (int m = 0; m < WqMode.Count; m++)
                    if (IsModeEnabled(m)) list.Add(WqMode.Code(m));
                return string.Join(",", list);
            }
        }
    }

    /// <summary>实时测量数据 PC_DATA(16 字节)。</summary>
    internal sealed class WqData
    {
        public DateTime TimestampUtc { get; init; } = DateTime.UtcNow;

        /// <summary>当前测量值,含义随 Mode 而定,单位见 UnitCode。</summary>
        public float Value { get; init; }

        /// <summary>当前温度,单位由 TempUnitCode 决定。</summary>
        public float Temp { get; init; }

        public byte Mode { get; init; }
        public bool IsStable { get; init; }

        /// <summary>显示小数位数,格式化 Value 时按它来。</summary>
        public byte Decimals { get; init; }

        public byte UnitCode { get; init; }

        /// <summary>0 = ℃,1 = ℉。</summary>
        public byte TempUnitCode { get; init; }

        /// <summary>温度补偿(ATC)显示标志。文档只说是"标志",按非 0 即为 ATC 解释。</summary>
        public byte Atc { get; init; }

        public byte PoleLevel { get; init; }
        public byte CalLevel { get; init; }

        public string UnitText => WqUnit.Name(UnitCode);
        public string TempUnitText => TempUnitCode == 1 ? "℉" : "℃";
        public string ModeCode => WqMode.Code(Mode);

        /// <summary>小数位越界时退回 2 位,免得 ToString("F250") 之类把界面炸掉。</summary>
        public int SafeDecimals => Decimals <= 6 ? Decimals : 2;
    }

    /// <summary>
    /// 仪器设置项 PC_SETTING(45 字节)。读(0x09)和写(0x0A)用的是同一结构。
    ///
    /// 手动温补温度在协议里是三个字段(ph_temp / cond_temp / do_temp),但固件内部
    /// 只有一个变量,写入时取"第一个与当前值不同"的字段生效。文档明确要求三个
    /// 字段**必须填同一个值**。所以这里只留一个 <see cref="ManualTemp"/>,
    /// 组包时把它同时写进三个位置 —— 从结构上杜绝写成三个不同值的可能。
    /// </summary>
    internal sealed class WqSetting
    {
        public const int Size = 45;

        public float ManualTemp { get; set; }        // 偏移 0 / 4 / 24,三处相同
        public float CondRefTemp { get; set; }       // 8
        public float CondTempCoe { get; set; }       // 12
        public float CondTdsCoe { get; set; }        // 16
        public float CondK { get; set; }             // 20
        public float DoSal { get; set; }             // 28
        public float DoPressure { get; set; }        // 32
        public byte TempUnit { get; set; }           // 36  0=℃ 1=℉
        public byte IsBeep { get; set; }             // 37
        public byte DeviceLock { get; set; }         // 38  1=锁定,仪器按键禁用,只能上位机解
        public byte BgLight { get; set; }            // 39  3~100
        public byte Shutdown { get; set; }           // 40  分钟,默认 5
        public byte PhWaterType { get; set; }        // 41  0无 1纯水 2氨水
        public byte PhStdType { get; set; }          // 42  0CH 1NIST 2USA
        public byte PhDispRes { get; set; }          // 43  2 或 3
        public byte CondStdType { get; set; }        // 44  0CH 1USA

        /// <summary>读回来时三个温补字段是否一致。不一致只记日志,不影响使用。</summary>
        public bool TempFieldsConsistent { get; init; } = true;

        public WqSetting Clone() => (WqSetting)MemberwiseClone();

        public static WqSetting Parse(ReadOnlySpan<byte> d)
        {
            if (d.Length < Size)
                throw new ArgumentException($"PC_SETTING 应为 {Size} 字节,实际 {d.Length}");

            float phT = F(d, 0), condT = F(d, 4), doT = F(d, 24);
            return new WqSetting
            {
                ManualTemp = phT,
                TempFieldsConsistent = phT.Equals(condT) && phT.Equals(doT),
                CondRefTemp = F(d, 8),
                CondTempCoe = F(d, 12),
                CondTdsCoe = F(d, 16),
                CondK = F(d, 20),
                DoSal = F(d, 28),
                DoPressure = F(d, 32),
                TempUnit = d[36],
                IsBeep = d[37],
                DeviceLock = d[38],
                BgLight = d[39],
                Shutdown = d[40],
                PhWaterType = d[41],
                PhStdType = d[42],
                PhDispRes = d[43],
                CondStdType = d[44],
            };
        }

        public byte[] ToBytes()
        {
            var b = new byte[Size];
            var s = b.AsSpan();
            W(s, 0, ManualTemp);
            W(s, 4, ManualTemp);
            W(s, 8, CondRefTemp);
            W(s, 12, CondTempCoe);
            W(s, 16, CondTdsCoe);
            W(s, 20, CondK);
            W(s, 24, ManualTemp);
            W(s, 28, DoSal);
            W(s, 32, DoPressure);
            b[36] = TempUnit;
            b[37] = IsBeep;
            b[38] = DeviceLock;
            b[39] = BgLight;
            b[40] = Shutdown;
            b[41] = PhWaterType;
            b[42] = PhStdType;
            b[43] = PhDispRes;
            b[44] = CondStdType;
            return b;
        }

        private static float F(ReadOnlySpan<byte> d, int off) => BinaryPrimitives.ReadSingleBigEndian(d.Slice(off, 4));
        private static void W(Span<byte> d, int off, float v) => BinaryPrimitives.WriteSingleBigEndian(d.Slice(off, 4), v);
    }

    /// <summary>存储记录 RECORD(56 字节)。字符串字段是 C 风格,以首个 0x00 结束。</summary>
    internal sealed class WqRecord
    {
        public ushort No { get; init; }
        public string Mode { get; init; } = "";
        public string Result { get; init; } = "";
        public string Temp { get; init; } = "";
        public byte TempUnit { get; init; }
        public string Date { get; init; } = "";

        public string TempUnitText => TempUnit == 1 ? "℉" : "℃";

        public override string ToString() => $"{No}\t{Mode}\t{Result}\t{Temp}{TempUnitText}\t{Date}";
    }

    /// <summary>一帧应答的三段头 + 参数。</summary>
    internal sealed class WqResponse
    {
        public byte ToCmd { get; init; }
        public byte Result { get; init; }
        public byte[] Param { get; init; } = Array.Empty<byte>();

        public bool Ok => Result == WqResult.Success;
    }

    internal static class WqProtocol
    {
        public const byte Head = 0x15;
        public const byte Tail = 0x16;

        /// <summary>整帧最小长度:头 + 长度 + 尾。</summary>
        public const int MinFrameLen = 3;

        /// <summary>仪器接收缓冲上限(文档 2.1),命令帧数据域不能超过它。</summary>
        public const int MaxDataLen = 100;

        public const int DeviceInfoLen = 3;
        public const int DataLen = 16;
        public const int RecordLen = 56;

        /// <summary>仪器最多存 3000 条记录(文档 4.4)。</summary>
        public const int MaxRecords = 3000;

        #region 组包

        /// <summary>命令帧:15 | N | cmd | param… | 16。</summary>
        public static byte[] BuildCommand(byte cmd, ReadOnlySpan<byte> param = default)
        {
            int n = 1 + param.Length;
            if (n > MaxDataLen)
                throw new ArgumentException($"命令数据域 {n} 字节超过仪器缓冲上限 {MaxDataLen}");

            var frame = new byte[MinFrameLen + n];
            frame[0] = Head;
            frame[1] = (byte)n;
            frame[2] = cmd;
            param.CopyTo(frame.AsSpan(3));
            frame[^1] = Tail;
            return frame;
        }

        /// <summary>联机:15 01 01 16。</summary>
        public static byte[] BuildConnect() => BuildCommand(WqCmd.Connect);

        /// <summary>断开:15 01 02 16。仪器不回应答。</summary>
        public static byte[] BuildDisconnect() => BuildCommand(WqCmd.Disconnect);

        // 其它命令统一由驱动的 TransactCore 用 BuildCommand(cmd, param) 组,
        // 这里不再一个命令一个方法 —— 两套组包入口以后改起来容易漏一处。

        #endregion

        #region 拆包

        /// <summary>
        /// 从缓冲区里尽力取出**一帧应答**。
        ///
        /// 返回 true 表示取到(<paramref name="response"/> 有效);返回 false 时靠
        /// <paramref name="consumed"/> 告诉调用方该吃掉多少字节:
        ///   · 数据不够  → consumed = 0,等更多字节;
        ///   · 不像帧    → consumed = 1,前移一个字节重新找(重同步)。
        ///
        /// 这个协议没有校验,"0x15 + 长度 + 0x16"三项凑巧对上的垃圾段是可能的。
        /// 所以额外要求数据域符合应答帧外形(首字节 0x00、至少 3 字节),
        /// 不符合的一律按一个字节前移,宁可多扫几个字节,也不能把真帧连带吃掉。
        /// </summary>
        public static bool TryReadFrame(byte[] buffer, int offset, int available,
                                        out WqResponse? response, out int consumed)
        {
            response = null;
            consumed = 0;

            if (available <= 0) return false;

            if (buffer[offset] != Head)
            {
                consumed = 1;
                return false;
            }

            if (available < 2) return false;

            int dataLen = buffer[offset + 1];
            int frameLen = MinFrameLen + dataLen;
            if (available < frameLen) return false;

            if (buffer[offset + frameLen - 1] != Tail)
            {
                consumed = 1;
                return false;
            }

            // 应答帧数据域至少 3 字节:0x00 | to_cmd | result
            if (dataLen < 3 || buffer[offset + 2] != WqCmd.Respond)
            {
                consumed = 1;
                return false;
            }

            var param = new byte[dataLen - 3];
            Buffer.BlockCopy(buffer, offset + 5, param, 0, param.Length);

            response = new WqResponse
            {
                ToCmd = buffer[offset + 3],
                Result = buffer[offset + 4],
                Param = param,
            };
            consumed = frameLen;
            return true;
        }

        public static WqDeviceInfo ParseDeviceInfo(ReadOnlySpan<byte> p)
        {
            if (p.Length < DeviceInfoLen)
                throw new ArgumentException($"PC_DEVICE_INFO 应为 {DeviceInfoLen} 字节,实际 {p.Length}");

            return new WqDeviceInfo
            {
                DeviceId = p[0],
                ModesSta = BinaryPrimitives.ReadUInt16BigEndian(p.Slice(1, 2)),
            };
        }

        public static WqData ParseData(ReadOnlySpan<byte> p)
        {
            if (p.Length < DataLen)
                throw new ArgumentException($"PC_DATA 应为 {DataLen} 字节,实际 {p.Length}");

            return new WqData
            {
                Value = BinaryPrimitives.ReadSingleBigEndian(p.Slice(0, 4)),
                Temp = BinaryPrimitives.ReadSingleBigEndian(p.Slice(4, 4)),
                Mode = p[8],
                IsStable = p[9] != 0,
                Decimals = p[10],
                UnitCode = p[11],
                TempUnitCode = p[12],
                Atc = p[13],
                PoleLevel = p[14],
                CalLevel = p[15],
            };
        }

        public static WqSetting ParseSetting(ReadOnlySpan<byte> p) => WqSetting.Parse(p);

        public static WqRecord ParseRecord(ReadOnlySpan<byte> p)
        {
            if (p.Length < RecordLen)
                throw new ArgumentException($"RECORD 应为 {RecordLen} 字节,实际 {p.Length}");

            // 偏移:0 u16 no | 2 char[6] mode | 8 char[20] result | 28 char[9] temp | 37 u8 temp_unit | 38 char[18] date
            return new WqRecord
            {
                No = BinaryPrimitives.ReadUInt16BigEndian(p.Slice(0, 2)),
                Mode = Str(p, 2, 6),
                Result = Str(p, 8, 20),
                Temp = Str(p, 28, 9),
                TempUnit = p[37],
                Date = Str(p, 38, 18),
            };
        }

        /// <summary>
        /// C 风格定长字符串:截到第一个 '\0',再去掉首尾空白。
        /// 结果串里可能带单位符号(文档示例 "7.02 pH"),固件若用了 GBK 的 ℃ 之类,
        /// 先试 936 代码页;.NET 8 默认不带中文代码页时退回 ASCII 并把不可打印字节换成 '?'。
        /// 退化的后果只是符号变怪字符,数值部分不受影响。
        /// </summary>
        private static string Str(ReadOnlySpan<byte> d, int offset, int len)
        {
            int end = offset;
            int limit = Math.Min(d.Length, offset + len);
            while (end < limit && d[end] != 0) end++;
            int n = end - offset;
            if (n <= 0) return "";

            var bytes = d.Slice(offset, n).ToArray();
            try
            {
                return Encoding.GetEncoding(936).GetString(bytes).Trim();
            }
            catch
            {
                var sb = new StringBuilder(n);
                foreach (byte v in bytes)
                    sb.Append(v is >= 0x20 and < 0x7F ? (char)v : '?');
                return sb.ToString().Trim();
            }
        }

        #endregion

        /// <summary>十六进制串,日志用。</summary>
        public static string Hex(ReadOnlySpan<byte> d) => BitConverter.ToString(d.ToArray()).Replace('-', ' ');
    }
}

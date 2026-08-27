// ============================================================================
//  TouchPhProtocol.cs
//  触摸屏酸度计 —— 串口私有协议的编解码层（不含任何 IO,便于单独推敲/复核）。
//
//  依据《触摸屏酸度计通信协议》。要点:
//
//  1) 物理层是 TTL 电平串口(不是 RS232),一根 USB 线直连。
//     参数固定 9600 / 8 / 1 / None —— 手册写死,不给改,所以驱动里不做成下拉框。
//
//  2) 应用层有"连接/断开"的概念,这一点和 Modbus 那批设备完全不同:
//     串口 open 成功 ≠ 能通信。必须再发一个"请求连接包",仪器原样回一份同样的包
//     才算连上(仪器液晶屏右上角出现电脑图标)。所以 OnConnectAsync 里
//     "打开串口"和"握手"是两件事,任何一件不成都算没连上。
//
//  3) 连上之后是**仪器主动推**:大约每 800ms 推一个 70 字节的测量参数包。
//     上位机不需要、也没有办法"请求一次读数" —— 协议里根本没有读命令。
//     所以驱动是"后台线程收帧 + 缓存最新快照",读命令只是把快照抄出来,
//     不产生任何串口往返。这与 Modbus 驱动的请求/应答模型是反的,
//     后来改这里的人务必留意:给读命令加"发一个包再等回应"是错的。
//
//  数据包格式:
//      包头(0x15) | 数据域长度(1B) | 数据域(0~255B) | 包尾(0x16)
//  长度字段只算数据域,不含头尾,所以整帧 = 3 + len。
// ============================================================================

using System;
using System.Text;

namespace PhMeter_TouchScreen
{
    /// <summary>收到的一帧的种类。按"数据域长度 + 命令号"两项联合判定,不会撞车。</summary>
    internal enum TouchPhFrameKind
    {
        /// <summary>看不懂的帧(长度/命令号都对不上),调用方直接丢弃。</summary>
        Unknown = 0,

        /// <summary>仪器对"请求连接包"的回显。len=1,data[0]=0x22。握手成功的唯一凭据。</summary>
        ConnectAck,

        /// <summary>测量参数包。len=70,cmd=1。仪器每 ~800ms 主动推一次。</summary>
        Params,

        /// <summary>记录上传包。len=57,cmd=2。一条记录一个包。</summary>
        Record,

        /// <summary>记录上传结束包。len=1,高 4 位=3。表示这一轮记录推完了。</summary>
        RecordEnd,
    }

    /// <summary>一帧解析结果。</summary>
    internal sealed class TouchPhFrame
    {
        public TouchPhFrameKind Kind { get; init; }

        /// <summary>数据域(不含头/长度/尾)。</summary>
        public byte[] Data { get; init; } = Array.Empty<byte>();

        /// <summary>仪器型号(命令号那个字节的低 4 位)。ConnectAck 帧里没有这项,为 0。</summary>
        public int Model { get; init; }
    }

    /// <summary>
    /// 测量参数包(PARAMS_PACKET,70 字节)的解码结果。
    ///
    /// 关于位域的字节序 —— 这是全篇最容易搞错的地方:
    /// 手册给的是 ARM 上 __packed 的 C 结构体。小端 ABI 下,同一个存储单元里
    /// **先声明的位域占低位**。所以
    ///     u8 model:4; u8 cmd:4;
    /// 意味着 bit0~3 是 model、bit4~7 是 cmd,而不是反过来。
    /// 四个字节的标志位按这个规则逐位摊开,再往后才是 16 个 float。
    /// 偏移对照(单位:字节):
    ///     0        标志字节 A: model:4 | cmd:4
    ///     1        标志字节 B: cond_unit:4 | cond_mode:2 | cond_resolution:2
    ///     2        标志字节 C: ph_tmp_src:1 | cond_tmp_src:1 | do_tmp_src:1
    ///                          | cond_std_type:1 | ph_std_type:2 | ph_h2o_type:2
    ///     3        标志字节 D: tmp_unit:1 | is_ph_stable:1 | is_cond_stable:1
    ///                          | is_do_stable:1 | ph_resolution:2 | do_resolution:2
    ///     4  ~ 67  16 个 float(IEEE754 小端),顺序见下面各字段
    ///     68       do_sal (u8)
    ///     69       标志字节 E: is_ph_atc:1 | is_cond_atc:1 | is_do_atc:1 | cond_ref_tmp:5
    /// 合计 70,与手册"数据域长度 70"对得上 —— 这就是这套摊法正确的旁证。
    /// </summary>
    internal sealed class TouchPhSnapshot
    {
        public DateTime TimestampUtc { get; init; } = DateTime.UtcNow;

        public int Model { get; init; }

        // ── 电导率相关的编码字段。手册没有给 cond_unit / cond_mode / cond_resolution
        //    的取值表,所以这里原样透出编码值,不臆造映射。
        public int CondUnitCode { get; init; }
        public int CondModeCode { get; init; }
        public int CondResolutionCode { get; init; }

        /// <summary>PH 温度源。0=ATC 自动温补,1=MTC 手动温补(与设置项 2 的取值一致)。</summary>
        public int PhTempSource { get; init; }
        public int CondTempSource { get; init; }
        public int DoTempSource { get; init; }

        /// <summary>电导率标准缓冲液类型(1 bit,手册未给取值表)。</summary>
        public int CondStdType { get; init; }

        /// <summary>PH 标液系列。0=国标,1=NIST,2=USA 欧美(与设置项 3 一致)。</summary>
        public int PhStdType { get; init; }

        /// <summary>PH 水类型。0=普通水,1=纯水,2=加氨纯水(与设置项 0 一致)。</summary>
        public int PhH2oType { get; init; }

        /// <summary>
        /// 温度单位。手册没写这一位的取值表,按仪表行业惯例 0=℃、1=℉ 解释。
        /// 真机上如果反了,只改 <see cref="TempUnitText"/> 一处即可。
        /// </summary>
        public int TempUnitCode { get; init; }

        public bool IsPhStable { get; init; }
        public bool IsCondStable { get; init; }
        public bool IsDoStable { get; init; }

        /// <summary>PH 分辨率编码。1=0.1,2=0.01,3=0.001(与设置项 4 一致),同时就是小数位数。</summary>
        public int PhResolution { get; init; }

        /// <summary>DO 分辨率编码,含义同上。</summary>
        public int DoResolution { get; init; }

        public float Ph { get; init; }
        public float Mv { get; init; }
        public float PhTemp { get; init; }
        public float Cond { get; init; }
        public float CondTemp { get; init; }
        public float Do { get; init; }
        public float DoSaturation { get; init; }
        public float DoTemp { get; init; }
        public float DoCurrent { get; init; }
        public float PhMtcTemp { get; init; }
        public float CondMtcTemp { get; init; }
        public float DoMtcTemp { get; init; }
        public float CondTempCoefficient { get; init; }
        public float CondTdsCoefficient { get; init; }
        public float CondCellConstant { get; init; }
        public float DoPressure { get; init; }

        /// <summary>DO 盐度补偿值。</summary>
        public int DoSalinity { get; init; }

        public bool IsPhAtc { get; init; }
        public bool IsCondAtc { get; init; }
        public bool IsDoAtc { get; init; }

        /// <summary>电导率基准温度(5 bit,0~31℃)。</summary>
        public int CondRefTemp { get; init; }

        public string TempUnitText => TempUnitCode == 1 ? "℉" : "℃";

        /// <summary>PH 分辨率对应的小数位数;编码超出 1~3 时退回 2 位(0.01,最常见)。</summary>
        public int PhDecimals => PhResolution is >= 1 and <= 3 ? PhResolution : 2;

        public int DoDecimals => DoResolution is >= 1 and <= 3 ? DoResolution : 2;
    }

    /// <summary>一条历史记录(RECORD,56 字节的定长字符串区)。</summary>
    internal sealed class TouchPhRecord
    {
        /// <summary>存储记录编号,char no[4]。</summary>
        public string No { get; init; } = "";

        /// <summary>PH 主数值,char val1[14],例如 "6.86 pH"。</summary>
        public string Value1 { get; init; } = "";

        /// <summary>mV 数值,char val2[12],例如 "414 mV"。</summary>
        public string Value2 { get; init; } = "";

        /// <summary>温度,char tmp[9],例如 "25.0℃"。</summary>
        public string Temp { get; init; } = "";

        /// <summary>保存时间,char date[17],例如 "2018/8/8 12:12"。</summary>
        public string Date { get; init; } = "";

        public override string ToString() => $"{No}\t{Value1}\t{Value2}\t{Temp}\t{Date}";
    }

    internal static class TouchPhProtocol
    {
        public const byte Head = 0x15;
        public const byte Tail = 0x16;

        /// <summary>请求连接包的数据域。发出去后仪器原样回一份,那才算连上。</summary>
        public const byte DataConnect = 0x22;

        /// <summary>请求断开包的数据域。仪器**不回**任何东西,发完直接关串口即可。</summary>
        public const byte DataDisconnect = 0x23;

        public const int ParamsDataLen = 70;
        public const int RecordDataLen = 57;

        /// <summary>整帧最小长度:头 + 长度 + 尾。</summary>
        public const int MinFrameLen = 3;

        // ── 手册里的设置项编号(SETTING_PACKET.setting_no)────────────────
        public const byte SettingH2oType = 0;    // 0普通水 1纯水 2加氨纯水
        public const byte SettingClearRecords = 1;
        public const byte SettingPhTempSource = 2;    // 0自动温补 1手动温补
        public const byte SettingPhStdSeries = 3;    // 0国标 1NIST 2USA
        public const byte SettingPhResolution = 4;    // 低精度 1/2;高精度 2/3
        public const byte SettingPhMtcTemp = 5;    // 0.0~99.9 ℃
        public const byte SettingPhFactoryReset = 6;
        public const byte SettingSavePhData = 24;
        public const byte SettingQueryPhRecords = 27;

        #region 组包

        /// <summary>请求连接包:15 01 22 16。</summary>
        public static byte[] BuildConnect() => new byte[] { Head, 0x01, DataConnect, Tail };

        /// <summary>请求断开包:15 01 23 16。</summary>
        public static byte[] BuildDisconnect() => new byte[] { Head, 0x01, DataDisconnect, Tail };

        /// <summary>
        /// 系统参数设置包。数据域 5 字节 = setting_no(1) + float val(4,小端)。
        /// 手册没有为它规定应答,所以发完不等回应;效果要从随后推来的测量包里看。
        /// </summary>
        public static byte[] BuildSetting(byte settingNo, float value)
        {
            var frame = new byte[3 + 5];
            frame[0] = Head;
            frame[1] = 5;
            frame[2] = settingNo;
            // 仪器是小端 ARM,BitConverter 在 x86/x64 上也是小端,直接写即可。
            // 万一将来跑在大端平台上,这里得反转 —— 所以显式判断一次。
            var bits = BitConverter.GetBytes(value);
            if (!BitConverter.IsLittleEndian) Array.Reverse(bits);
            Buffer.BlockCopy(bits, 0, frame, 3, 4);
            frame[7] = Tail;
            return frame;
        }

        #endregion

        #region 拆包

        /// <summary>
        /// 从缓冲区里尽力取出**一帧**。
        ///
        /// 返回 true 表示取到了一帧(<paramref name="frame"/> 有效),
        /// 返回 false 表示数据还不够或者当前位置不是帧头 —— 两种情况都靠
        /// <paramref name="consumed"/> 告诉调用方该吃掉多少字节:
        ///   · 数据不够  → consumed = 0,等更多字节再来;
        ///   · 帧头不对  → consumed = 1,前移一个字节重新找(重同步);
        ///   · 取到整帧  → consumed = 整帧长度。
        /// 串口上丢字节、串半帧是常态,没有重同步就会一直卡在垃圾数据上。
        /// </summary>
        public static bool TryReadFrame(byte[] buffer, int offset, int available,
                                        out TouchPhFrame? frame, out int consumed)
        {
            frame = null;
            consumed = 0;

            if (available <= 0) return false;

            if (buffer[offset] != Head)
            {
                consumed = 1;               // 不是帧头,丢一个字节继续找
                return false;
            }

            if (available < 2) return false;            // 还没收到长度字节

            int dataLen = buffer[offset + 1];
            int frameLen = MinFrameLen + dataLen;
            if (available < frameLen) return false;     // 整帧还没收全

            if (buffer[offset + frameLen - 1] != Tail)
            {
                // 长度字段对了但包尾不对 —— 说明这个 0x15 只是数据里的巧合。
                // 同样按一个字节重同步,不要按 frameLen 跳,否则会跳过真正的帧头。
                consumed = 1;
                return false;
            }

            var data = new byte[dataLen];
            Buffer.BlockCopy(buffer, offset + 2, data, 0, dataLen);

            var candidate = Classify(data);

            // 这个协议没有校验字段,只靠 0x15 / 长度 / 0x16 三项判定一帧,
            // 所以一段垃圾字节完全可能"恰好"凑成合法外形。实测过:
            // 半截脏数据后面紧跟一个真帧时,脏数据里的 0x15 配上真帧末尾的 0x16
            // 就凑出一个假帧,而这个假帧一旦按整帧长度吃掉,后面那个**真帧就被吞了**。
            //
            // 协议一共只定义了四种包(握手回显 / 测量 / 记录 / 记录结束),
            // 认不出种类的一律当作误判,只前移一个字节重新找 ——
            // 宁可多扫几个字节,也不能把真帧连带吃掉。
            //
            // 注意:以后固件若新增包类型,必须同步在 Classify 里登记,
            // 否则新包会被当成垃圾一个字节一个字节地磨掉(不会崩,但收不到)。
            if (candidate.Kind == TouchPhFrameKind.Unknown)
            {
                consumed = 1;
                return false;
            }

            consumed = frameLen;
            frame = candidate;
            return true;
        }

        /// <summary>按"数据域长度 + 命令号"判定帧种类。</summary>
        private static TouchPhFrame Classify(byte[] data)
        {
            if (data.Length == 1 && data[0] == DataConnect)
                return new TouchPhFrame { Kind = TouchPhFrameKind.ConnectAck, Data = data };

            if (data.Length == 0)
                return new TouchPhFrame { Kind = TouchPhFrameKind.Unknown, Data = data };

            int cmd = (data[0] >> 4) & 0x0F;
            int model = data[0] & 0x0F;

            // 记录上传结束包:len=1,高 4 位固定为 3,低 4 位是型号
            if (data.Length == 1 && cmd == 3)
                return new TouchPhFrame { Kind = TouchPhFrameKind.RecordEnd, Data = data, Model = model };

            if (data.Length == ParamsDataLen && cmd == 1)
                return new TouchPhFrame { Kind = TouchPhFrameKind.Params, Data = data, Model = model };

            if (data.Length == RecordDataLen && cmd == 2)
                return new TouchPhFrame { Kind = TouchPhFrameKind.Record, Data = data, Model = model };

            return new TouchPhFrame { Kind = TouchPhFrameKind.Unknown, Data = data, Model = model };
        }

        /// <summary>
        /// 解 PARAMS_PACKET(70 字节数据域)。偏移与位序见 <see cref="TouchPhSnapshot"/> 的注释。
        /// </summary>
        public static TouchPhSnapshot ParseParams(byte[] d)
        {
            if (d is null || d.Length < ParamsDataLen)
                throw new ArgumentException($"测量参数包长度应为 {ParamsDataLen},实际 {d?.Length ?? 0}");

            byte a = d[0], b = d[1], c = d[2], e = d[3], f = d[69];

            return new TouchPhSnapshot
            {
                Model = a & 0x0F,

                CondUnitCode = b & 0x0F,
                CondModeCode = (b >> 4) & 0x03,
                CondResolutionCode = (b >> 6) & 0x03,

                PhTempSource = c & 0x01,
                CondTempSource = (c >> 1) & 0x01,
                DoTempSource = (c >> 2) & 0x01,
                CondStdType = (c >> 3) & 0x01,
                PhStdType = (c >> 4) & 0x03,
                PhH2oType = (c >> 6) & 0x03,

                TempUnitCode = e & 0x01,
                IsPhStable = ((e >> 1) & 0x01) != 0,
                IsCondStable = ((e >> 2) & 0x01) != 0,
                IsDoStable = ((e >> 3) & 0x01) != 0,
                PhResolution = (e >> 4) & 0x03,
                DoResolution = (e >> 6) & 0x03,

                Ph = F(d, 4),
                Mv = F(d, 8),
                PhTemp = F(d, 12),
                Cond = F(d, 16),
                CondTemp = F(d, 20),
                Do = F(d, 24),
                DoSaturation = F(d, 28),
                DoTemp = F(d, 32),
                DoCurrent = F(d, 36),
                PhMtcTemp = F(d, 40),
                CondMtcTemp = F(d, 44),
                DoMtcTemp = F(d, 48),
                CondTempCoefficient = F(d, 52),
                CondTdsCoefficient = F(d, 56),
                CondCellConstant = F(d, 60),
                DoPressure = F(d, 64),

                DoSalinity = d[68],

                IsPhAtc = (f & 0x01) != 0,
                IsCondAtc = ((f >> 1) & 0x01) != 0,
                IsDoAtc = ((f >> 2) & 0x01) != 0,
                CondRefTemp = (f >> 3) & 0x1F,
            };
        }

        /// <summary>解 RECORD_PACKET(57 字节数据域:1 字节型号/命令号 + 56 字节记录)。</summary>
        public static TouchPhRecord ParseRecord(byte[] d)
        {
            if (d is null || d.Length < RecordDataLen)
                throw new ArgumentException($"记录包长度应为 {RecordDataLen},实际 {d?.Length ?? 0}");

            // 定长字符串区,依次 4 / 14 / 12 / 9 / 17,合计 56
            return new TouchPhRecord
            {
                No = Str(d, 1, 4),
                Value1 = Str(d, 5, 14),
                Value2 = Str(d, 19, 12),
                Temp = Str(d, 31, 9),
                Date = Str(d, 40, 17),
            };
        }

        /// <summary>小端 IEEE754 float。</summary>
        private static float F(byte[] d, int offset)
        {
            if (BitConverter.IsLittleEndian)
                return BitConverter.ToSingle(d, offset);

            var tmp = new byte[4];
            Buffer.BlockCopy(d, offset, tmp, 0, 4);
            Array.Reverse(tmp);
            return BitConverter.ToSingle(tmp, 0);
        }

        /// <summary>
        /// 定长字符串区取值:截到第一个 '\0' 为止,再去掉首尾空白。
        ///
        /// 编码有点讲究。记录里的温度写成 "25.0℃",那个 ℃ 不是 ASCII。
        /// 仪器侧是国产 ARM 固件,几乎必然是 GB2312/GBK。但 .NET 8 默认不带
        /// 中文代码页(要 System.Text.Encoding.CodePages 并注册 Provider),
        /// 而驱动是插件、不应该为了一个温度符号多背一个包 ——
        /// 所以这里先试 936,拿不到就退回 Latin1 并滤掉不可打印字符。
        /// 退化的后果仅仅是 ℃ 变成两个怪字符,数值部分照样是对的。
        /// </summary>
        private static string Str(byte[] d, int offset, int len)
        {
            int end = offset;
            int limit = Math.Min(d.Length, offset + len);
            while (end < limit && d[end] != 0) end++;
            int n = end - offset;
            if (n <= 0) return "";

            try
            {
                return Encoding.GetEncoding(936).GetString(d, offset, n).Trim();
            }
            catch
            {
                var sb = new StringBuilder(n);
                for (int i = offset; i < offset + n; i++)
                {
                    byte v = d[i];
                    sb.Append(v is >= 0x20 and < 0x7F ? (char)v : '?');
                }
                return sb.ToString().Trim();
            }
        }

        #endregion
    }
}

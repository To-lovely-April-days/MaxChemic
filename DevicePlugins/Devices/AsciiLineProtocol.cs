using System;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MaxChemical.Core;

namespace DevicePlugins.Devices
{
    /// <summary>
    /// ASCII 行协议的收发层,给一批走 RS-232/485 文本命令的实验室仪器共用。
    ///
    /// 为什么单独抽一层:实验室里除了 Modbus,还有一大类仪器走"发一行文本、回一行文本"的私有协议 ——
    /// IKA 的磁力搅拌器与旋蒸、Julabo/Huber 循环器、Tricontinent 注射泵、IDEX 选择阀、
    /// Vacuubrand 真空泵、梅特勒天平的 MT-SICS……。它们的差异只在四个地方:
    /// 命令前缀、命令结束符、应答结束符、参数分隔符。把这四项参数化之后,
    /// 收发、重试、剥壳、解析的代码就能完全共用,每接一台新仪器只剩下"抄命令表"。
    ///
    /// 命令拼装规则(与 PyLabware 的 prepare_message 一致,便于对照移植):
    ///     无参数: CommandPrefix + 命令名 + CommandTerminator
    ///     带参数: CommandPrefix + 命令名 + ArgsDelimiter + 值 + CommandTerminator
    ///
    /// 使用方式是组合而不是继承 —— 驱动已经继承了 Device 基类,这里作为字段持有。
    /// </summary>
    public sealed class AsciiLineProtocol
    {
        /// <summary>命令前缀。多数仪器为空;Tricontinent DT 协议是 "/" + 站号字符。</summary>
        public string CommandPrefix { get; set; } = "";

        /// <summary>
        /// 命令结束符。注意 IKA RCT Digital 手册里带空格(" \r \n"),
        /// PyLabware 原样保留了这个怪异写法并标注"不确定空格是否必要" —— 这里也照抄,
        /// 真机上如果不通,先试着改成 "\r\n"。
        /// </summary>
        public string CommandTerminator { get; set; } = "\r\n";

        /// <summary>应答结束符,同时用于串口分帧。</summary>
        public string ReplyTerminator { get; set; } = "\r\n";

        /// <summary>参数分隔符。IKA/Julabo 是空格,IDEX/Tricontinent/Huber 是空串。</summary>
        public string ArgsDelimiter { get; set; } = " ";

        /// <summary>应答前缀,收到后要剥掉。多数仪器为空。</summary>
        public string ReplyPrefix { get; set; } = "";

        /// <summary>单次收发的超时。</summary>
        public int TimeoutMs { get; set; } = 3000;

        /// <summary>超时重试次数(总尝试次数)。工业串口偶发抖动很常见。</summary>
        public int MaxRetries { get; set; } = 3;

        /// <summary>重试间隔。</summary>
        public int RetryDelayMs { get; set; } = 100;

        /// <summary>接收缓冲区大小。行协议的应答都很短,512 足够。</summary>
        public int ReceiveBufferSize { get; set; } = 512;

        private readonly Func<Task<IDeviceTransport>> _transportFactory;
        private readonly SemaphoreSlim _ioLock = new SemaphoreSlim(1, 1);
        private readonly Action<string> _log;

        /// <param name="transportFactory">每次 IO 时获取 Transport。串口是即用即建,所以这里传工厂而不是实例。</param>
        /// <param name="log">可选的调试日志回调。</param>
        public AsciiLineProtocol(Func<Task<IDeviceTransport>> transportFactory, Action<string> log = null)
        {
            _transportFactory = transportFactory ?? throw new ArgumentNullException(nameof(transportFactory));
            _log = log;
        }

        #region 命令拼装

        /// <summary>按仪器的前缀/分隔符/结束符拼出完整命令行。</summary>
        public string BuildCommand(string name, string value = null)
        {
            if (value == null)
                return CommandPrefix + name + CommandTerminator;
            return CommandPrefix + name + ArgsDelimiter + value + CommandTerminator;
        }

        #endregion

        #region 收发

        /// <summary>
        /// 发一条不需要应答的命令(启停、设定值下发这类)。
        /// </summary>
        public async Task<bool> SendAsync(string name, string value = null, CancellationToken ct = default)
        {
            string cmd = BuildCommand(name, value);
            byte[] bytes = Encoding.ASCII.GetBytes(cmd);

            await _ioLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                using var transport = await _transportFactory().ConfigureAwait(false);
                bool ok = await transport.SendAsync(bytes, 0, bytes.Length, ct).ConfigureAwait(false);
                _log?.Invoke($"TX <{Escape(cmd)}> => {ok}");
                return ok;
            }
            finally
            {
                _ioLock.Release();
            }
        }

        /// <summary>
        /// 发一条命令并读回一行应答。返回值已经剥掉应答前缀与结束符。
        /// 超时会自动重试,非超时异常直接抛(协议错/字节流非法重试也没用)。
        /// </summary>
        public async Task<string> QueryAsync(string name, string value = null, CancellationToken ct = default)
        {
            Exception lastException = null;

            for (int attempt = 1; attempt <= MaxRetries; attempt++)
            {
                try
                {
                    return await QueryOnceAsync(name, value, ct).ConfigureAwait(false);
                }
                catch (TimeoutException ex)
                {
                    lastException = ex;
                    if (attempt < MaxRetries)
                    {
                        _log?.Invoke($"读取超时(第 {attempt}/{MaxRetries} 次),{RetryDelayMs}ms 后重试");
                        await Task.Delay(RetryDelayMs, ct).ConfigureAwait(false);
                    }
                }
            }

            throw new TimeoutException($"ASCII 读取失败,{MaxRetries} 次尝试后仍超时", lastException);
        }

        private async Task<string> QueryOnceAsync(string name, string value, CancellationToken ct)
        {
            string cmd = BuildCommand(name, value);
            byte[] bytes = Encoding.ASCII.GetBytes(cmd);
            byte[] recv = new byte[ReceiveBufferSize];

            await _ioLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                using var transport = await _transportFactory().ConfigureAwait(false);
                int len = await transport.SendAndReceiveAsync(bytes, recv, TimeoutMs, ct).ConfigureAwait(false);

                if (len <= 0)
                    throw new TimeoutException($"命令 {name} 无应答");

                string raw = Encoding.ASCII.GetString(recv, 0, len);
                string stripped = Strip(raw, ReplyPrefix, ReplyTerminator);
                _log?.Invoke($"TX <{Escape(cmd)}> RX <{Escape(raw)}> => <{stripped}>");
                return stripped;
            }
            finally
            {
                _ioLock.Release();
            }
        }

        #endregion

        #region 解析工具

        /// <summary>
        /// 剥掉应答的前缀与结束符。
        /// 按整段匹配而不是逐字符 trim —— 逐字符会把数据本身的字符也吃掉
        /// (比如应答 "1.0\r\n" 用 TrimEnd("\r\n\0") 没问题,但 "OK0\r\n" 用 TrimEnd('0','\r','\n') 就会误伤)。
        /// </summary>
        public static string Strip(string reply, string prefix, string suffix)
        {
            if (reply == null) return null;

            // 串口读到的尾部可能带缓冲区残留的 \0
            reply = reply.TrimEnd('\0');

            if (!string.IsNullOrEmpty(prefix) && reply.StartsWith(prefix, StringComparison.Ordinal))
                reply = reply.Substring(prefix.Length);

            if (!string.IsNullOrEmpty(suffix) && reply.EndsWith(suffix, StringComparison.Ordinal))
                reply = reply.Substring(0, reply.Length - suffix.Length);

            return reply;
        }

        /// <summary>
        /// 等价于 Python 的 reply[start:end],支持负数下标。
        ///
        /// 移植 PyLabware 的命令表时会大量用到:它的 {"parser": slicer, "args": [-2]}
        /// 就是 reply[:-2],用来砍掉 IKA 应答尾部的 " N" 通道号
        /// (IKA 回的是 "25.5 2",后面那个 2 是传感器通道,不是数据的一部分)。
        /// </summary>
        public static string Slice(string s, int? start = null, int? end = null)
        {
            if (string.IsNullOrEmpty(s)) return s ?? string.Empty;
            int n = s.Length;

            int from = start ?? 0;
            if (from < 0) from += n;
            from = Math.Max(0, Math.Min(n, from));

            int to = end ?? n;
            if (to < 0) to += n;
            to = Math.Max(0, Math.Min(n, to));

            return to <= from ? string.Empty : s.Substring(from, to - from);
        }

        /// <summary>
        /// 把应答解析成 double。解析不出来时返回 fallback 而不是抛异常 ——
        /// 轮询链路上偶尔收到一帧脏数据不该让整轮采集失败。
        /// 用 InvariantCulture:仪器回的一律是 "25.5" 这种点号小数,
        /// 不能受操作系统区域设置影响(中文 Windows 默认也是点号,但德语区就是逗号)。
        /// </summary>
        public static double ParseDouble(string s, double fallback = 0)
        {
            if (string.IsNullOrWhiteSpace(s)) return fallback;
            return double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
                ? v : fallback;
        }

        /// <summary>把应答解析成 int,规则同 ParseDouble。</summary>
        public static int ParseInt(string s, int fallback = 0)
        {
            if (string.IsNullOrWhiteSpace(s)) return fallback;
            if (int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)) return v;
            // 有些仪器回的是 "25.0" 这种带小数点的整数值
            return double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
                ? (int)Math.Round(d) : fallback;
        }

        /// <summary>
        /// 把工程量格式化成命令参数。固定用 InvariantCulture,
        /// 否则在某些区域设置下会发出 "25,5" 这种仪器认不得的小数。
        /// </summary>
        public static string Format(double value, int decimals = 0)
            => value.ToString("F" + decimals, CultureInfo.InvariantCulture);

        /// <summary>限幅到命令表声明的范围。超范围直接夹住而不是抛异常,避免流程中断。</summary>
        public static double Clamp(double value, double min, double max)
            => value < min ? min : (value > max ? max : value);

        /// <summary>把控制字符转成可见形式,只用于日志。</summary>
        private static string Escape(string s)
            => s?.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\x03", "\\x03") ?? "";

        #endregion
    }
}

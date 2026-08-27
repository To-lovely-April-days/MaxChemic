// ============================================================================
//  Program.cs
//  蓝牙代理进程的入口。stdin 收命令、stdout 发事件，一行一条，UTF-8。
//
//  ── 协议 ──────────────────────────────────────────────────────────
//  字段用制表符分隔。文本字段里的 \ \r \n \t 会被转义（见 Esc/Unesc），
//  所以任何一条消息都保证是单独一行。
//
//  驱动 → 代理：
//    SCAN     <超时ms>
//    CONNECT  <地址> <名称> <服务UUID> <写特征UUID> <通知特征UUID> <扫描超时ms>
//             （六个字段都可以是空串，表示"没指定，你自己看着办"）
//    TX       <十六进制>
//    QUIT
//
//  代理 → 驱动：
//    HELLO    <协议版本>      进程起来了
//    DEV      <名称> <地址> <信号>     扫描结果，一台一行
//    SCANEND  <台数>
//    READY                    连上并订阅成功
//    RX       <十六进制>      仪器推来的数据
//    TXOK / TXERR <原因>
//    CLOSED                   链路断了
//    LOG      <文本>          给日志看的（识别到什么模块、枚举到哪些特征……）
//    ERR      <文本>          操作失败的原因
//
//  ── 为什么是 stdio 而不是命名管道 ─────────────────────────────────
//  只有一个固定的调用方，数据量也极小（仪器每秒推一帧、27 字节）。
//  stdio 少一层握手和超时逻辑，而且**出问题时能手工验**：
//  直接双击跑起来，敲一行 SCAN<TAB>8000 回车，就能看到附近有哪些设备。
//  命名管道做不到这一点。
//
//  ── 父进程没了怎么办 ──────────────────────────────────────────────
//  stdin 读到 EOF 就退出。主程序崩了或被任务管理器结束掉，管道会断，
//  这里自然收尾，不会留下孤儿进程一直占着蓝牙适配器。
// ============================================================================

using System.Globalization;
using System.Text;

namespace MaxChemical.Ble.Proxy;

internal static class Program
{
    private const int ProtocolVersion = 1;

    private static readonly object OutLock = new object();
    private static StreamWriter _out = null!;
    private static BleCore? _core;

    private static async Task<int> Main()
    {
        // 显式构造读写器而不是改 Console.OutputEncoding：后者在 stdout 被重定向时
        // 某些环境下会抛异常。UTF8Encoding(false) 是不带 BOM —— 带了的话
        // 驱动那边读到的第一行开头会多出三个字节，HELLO 就对不上了。
        _out = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false))
        {
            AutoFlush = true,
            NewLine = "\n"
        };
        var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));

        Send("HELLO", ProtocolVersion.ToString(CultureInfo.InvariantCulture));

        try
        {
            string? line;
            while ((line = await input.ReadLineAsync().ConfigureAwait(false)) != null)
            {
                if (line.Length == 0) continue;
                var parts = line.Split('\t');

                try
                {
                    switch (parts[0])
                    {
                        case "SCAN":
                            await DoScanAsync(parts).ConfigureAwait(false);
                            break;

                        case "CONNECT":
                            await DoConnectAsync(parts).ConfigureAwait(false);
                            break;

                        case "TX":
                            await DoTxAsync(parts).ConfigureAwait(false);
                            break;

                        case "QUIT":
                            return Shutdown(0);

                        default:
                            Send("ERR", $"未知命令:{parts[0]}");
                            break;
                    }
                }
                catch (Exception ex)
                {
                    // 单条命令出错不该把进程带走 —— 驱动那边还等着后续命令。
                    Send("ERR", $"{parts[0]} 失败:{ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Send("ERR", $"代理进程异常:{ex.Message}");
        }

        return Shutdown(0);
    }

    private static int Shutdown(int code)
    {
        try { _core?.Dispose(); } catch { }
        _core = null;
        return code;
    }

    // ══ 命令 ═══════════════════════════════════════════════════════

    private static async Task DoScanAsync(string[] parts)
    {
        int timeoutMs = ParseInt(parts, 1, 8000);
        timeoutMs = Math.Clamp(timeoutMs, 1000, 60000);

        using var cts = new CancellationTokenSource(timeoutMs + 5000);
        var found = await BleCore.ScanAsync(timeoutMs, "", cts.Token).ConfigureAwait(false);

        foreach (var d in found)
            Send("DEV", d.Name, d.AddressText, d.Rssi.ToString(CultureInfo.InvariantCulture));

        Send("SCANEND", found.Count.ToString(CultureInfo.InvariantCulture));
    }

    private static async Task DoConnectAsync(string[] parts)
    {
        // CONNECT <地址> <名称> <服务> <写> <通知> <扫描超时ms>
        string addr = Field(parts, 1);
        string name = Field(parts, 2);
        string svc = Field(parts, 3);
        string write = Field(parts, 4);
        string notify = Field(parts, 5);
        int scanMs = Math.Clamp(ParseInt(parts, 6, 10000), 2000, 60000);

        try { _core?.Dispose(); } catch { }
        _core = null;

        var core = new BleCore(name, addr, svc, write, notify, scanMs, msg => Send("LOG", msg));
        core.BytesReceived += bytes => Send("RX", Convert.ToHexString(bytes));
        core.Closed += () => Send("CLOSED");

        // 比调用方给的扫描超时再多留一点：连接、枚举服务、订阅通知都要时间
        using var cts = new CancellationTokenSource(scanMs + 30000);
        bool ok = await core.OpenAsync(cts.Token).ConfigureAwait(false);

        if (!ok)
        {
            Send("ERR", core.LastError ?? "连接失败");
            try { core.Dispose(); } catch { }
            return;
        }

        _core = core;
        Send("READY");
    }

    private static async Task DoTxAsync(string[] parts)
    {
        var core = _core;
        if (core is null || !core.IsOpen)
        {
            Send("TXERR", "尚未连接");
            return;
        }

        byte[] frame;
        try
        {
            frame = Convert.FromHexString(Field(parts, 1));
        }
        catch
        {
            Send("TXERR", "十六进制解析失败");
            return;
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        bool ok = await core.WriteAsync(frame, cts.Token).ConfigureAwait(false);
        if (ok) Send("TXOK");
        else Send("TXERR", core.LastError ?? "写入失败");
    }

    // ══ 输出 ═══════════════════════════════════════════════════════

    /// <summary>
    /// 发一条消息。**必须加锁**：RX 是从蓝牙的通知回调（线程池）上来的，
    /// 和主循环里的应答可能同时写 stdout，不加锁会把两行搅在一起。
    /// </summary>
    private static void Send(string verb, params string[] fields)
    {
        var sb = new StringBuilder(verb);
        foreach (var f in fields)
        {
            sb.Append('\t');
            sb.Append(Esc(f));
        }

        lock (OutLock)
        {
            try { _out.WriteLine(sb.ToString()); } catch { }
        }
    }

    /// <summary>
    /// 转义，保证一条消息占且只占一行。
    /// 枚举 GATT 特征那段日志本身就是多行的，不转义会被驱动当成好几条消息。
    /// </summary>
    private static string Esc(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Replace("\\", "\\\\")
                .Replace("\r", "\\r")
                .Replace("\n", "\\n")
                .Replace("\t", "\\t");
    }

    // ══ 小工具 ═════════════════════════════════════════════════════

    private static string Field(string[] parts, int index) =>
        index < parts.Length ? Unesc(parts[index]) : "";

    private static int ParseInt(string[] parts, int index, int fallback) =>
        index < parts.Length && int.TryParse(parts[index], NumberStyles.Integer,
                                             CultureInfo.InvariantCulture, out var v)
            ? v : fallback;

    private static string Unesc(string s)
    {
        if (s.IndexOf('\\') < 0) return s;

        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] != '\\' || i + 1 >= s.Length) { sb.Append(s[i]); continue; }
            i++;
            sb.Append(s[i] switch
            {
                'r' => '\r',
                'n' => '\n',
                't' => '\t',
                '\\' => '\\',
                _ => s[i]
            });
        }
        return sb.ToString();
    }
}

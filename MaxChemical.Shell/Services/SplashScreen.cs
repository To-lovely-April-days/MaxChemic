using System;
using System.Threading;
using System.Windows.Threading;
using MaxChemical.Shell.Views;

namespace MaxChemical.Shell.Services;

/// <summary>
/// 启动画面的对外门面。
///
/// 闪屏跑在自己的 STA 线程上,原因见 SplashWindow 的类注释:启动过程几乎
/// 全在主 UI 线程上同步进行,共用 Dispatcher 的话动画一开始就冻住。
///
/// 本类把「跨线程」这件事收在内部 —— 调用方在主线程上直接调
/// SplashScreen.Step(...) 即可,不需要关心 Dispatcher。
///
/// 所有方法都吞异常并静默返回。闪屏是启动路径上的第一个东西,它自己出问题
/// 绝不能把主程序带下水;宁可没有闪屏,也不能因为闪屏起不来而启动失败。
/// </summary>
public static class SplashScreen
{
    private static SplashWindow _window;
    private static Thread _thread;
    private static readonly ManualResetEventSlim _shown = new(false);
    private static int _closed;   // 0 = 未关,1 = 已发起关闭

    /// <summary>关掉闪屏时是否要把焦点还给主窗口。</summary>
    public static bool ActivateMainWindowOnClose { get; set; } = true;

    public static void Show()
    {
        if (_thread != null) return;

        _thread = new Thread(() =>
        {
            try
            {
                _window = new SplashWindow();
                _window.Show();
                _shown.Set();
                Dispatcher.Run();          // 本线程自己的消息循环
            }
            catch
            {
                _shown.Set();              // 出错也要放行,否则主线程会一直等
            }
        })
        {
            IsBackground = true,           // 主程序退出时不会被它吊住
            Name = "MaxChemical.Splash"
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();

        // 等窗口真的建出来再返回,免得紧接着的 Step() 落空。
        // 上限 3 秒:等不到就当闪屏不可用,继续启动。
        _shown.Wait(TimeSpan.FromSeconds(3));
    }

    /// <summary>推进到第 index 步(索引从 0 起)。label / detail 传 null 用预设文案。</summary>
    public static void Step(int index, string label = null, string detail = null)
        => Post(w => w.SetStep(index, label, detail));

    /// <summary>全部就绪,进度走到 100%。</summary>
    public static void Ready() => Post(w => w.SetReady());

    /// <summary>
    /// 启动失败:进度停在原地并转成告警色。
    /// 这个不是装饰 —— 没有它的话,启动一旦抛异常,闪屏就变成一个不走也不关的
    /// 窗口,用户只会以为程序卡死。调用方随后仍应调 Close()。
    /// </summary>
    public static void Fail(string message, string detail = null)
        => Post(w => w.SetFailed(message, detail));

    /// <summary>播放退场动画并关闭。重复调用无副作用。</summary>
    public static void Close()
    {
        if (Interlocked.Exchange(ref _closed, 1) == 1) return;

        var w = _window;
        if (w == null) return;

        try
        {
            w.Dispatcher.BeginInvoke(new Action(() =>
            {
                w.BeginClose(() =>
                {
                    // 退场动画放完再停消息循环,否则窗口会直接消失没有过渡
                    w.Dispatcher.InvokeShutdown();
                });
            }));
        }
        catch { }

        _window = null;
    }

    /// <summary>
    /// 出错兜底:不放动画,直接拆掉。
    /// 启动崩溃时用这个 —— 那种情况下不需要好看,需要的是窗口一定消失。
    /// </summary>
    public static void Kill()
    {
        Interlocked.Exchange(ref _closed, 1);
        var w = _window;
        _window = null;
        if (w == null) return;
        try
        {
            w.Dispatcher.BeginInvoke(new Action(() =>
            {
                try { w.Close(); } catch { }
                w.Dispatcher.InvokeShutdown();
            }));
        }
        catch { }
    }

    private static void Post(Action<SplashWindow> action)
    {
        var w = _window;
        if (w == null) return;
        try
        {
            // BeginInvoke 而非 Invoke:调用方在主线程,不该为了刷新一行状态
            // 文字去等闪屏线程。这里也不需要返回值。
            w.Dispatcher.BeginInvoke(new Action(() =>
            {
                try { action(w); } catch { }
            }));
        }
        catch { }
    }
}

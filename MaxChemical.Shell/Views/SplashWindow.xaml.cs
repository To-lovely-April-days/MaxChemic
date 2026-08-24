using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace MaxChemical.Shell.Views;

/// <summary>
/// 启动画面。
///
/// ── 为什么它跑在自己的线程上 ──────────────────────────────────────
/// 启动过程本身几乎全在主 UI 线程上同步进行(构建容器、解析主窗口、加载
/// 模块)。闪屏若与主程序共用一个 Dispatcher,那些工作一开始动画就冻住,
/// 进度条卡在原地不动 —— 比没有闪屏更像死机。
/// 所以由 SplashScreen 在一个独立 STA 线程上创建本窗口并跑自己的
/// Dispatcher 循环;主线程忙成什么样都不影响这里的动画。
/// 代价是所有外部调用都必须经 Dispatcher 封送,这一点由 SplashScreen 统一处理。
///
/// ── 进度为什么不用 ProgressBar ────────────────────────────────────
/// 设计稿把进度做成一条贯穿的基准线加一个竖直游标。系统 ProgressBar 自带
/// 圆角、渐变和不确定态动画,压成一条 2px 直线要覆写整套模板,不如直接画。
/// </summary>
public partial class SplashWindow : Window
{
    /// <summary>
    /// 启动步骤。**顺序必须与 App.xaml.cs 里实际调用的先后一致** ——
    /// 进度是按已完成步骤的权重累加算的,排错了就会出现进度往回缩。
    /// 这里已按真实执行顺序排好:模块加载(base.OnInitialized)确实发生在
    /// 数据库之前,不要凭直觉把「数据库」挪到前面去。
    /// 权重合计 100。
    /// </summary>
    public static readonly (string Label, string Detail, double Weight)[] Steps =
    {
        ("运行时环境",       ".NET 8 · WPF",                 9),   // 0
        ("日志系统",         "Serilog · 按日滚动",           7),   // 1
        ("配置文件",         "appsettings.json · zh-CN",     8),   // 2
        ("功能模块",         "Designer · Device · DOE",     12),   // 3
        ("数据库连接",       "MySQL · 127.0.0.1:3307",      12),   // 4
        ("正在加载设备驱动", "Plugins · MEF 装配",          16),   // 5
        ("流程引擎",         "FlowExecutionEngine",          9),   // 6
        ("网关代理",         "GatewayConfigProxy · x86",     8),   // 7
        ("MCP 接口",         "管道 MaxChemical.Mcp.v1",      9),   // 8
        ("小桐 Agent",       "44 项工具 · 只读放行 22",     10),   // 9
    };

    private const double CardWidth = 540;

    // 进度用「目标值 + 惯性追随」而不是直接跳变:阶段之间耗时差别很大,
    // 直接跳会一顿一顿的。这与设计稿里 shown += (target-shown)*0.13 同源。
    private readonly DispatcherTimer _ticker;
    private double _target;
    private double _shown;
    private bool _failed;
    private bool _finished;

    public SplashWindow()
    {
        InitializeComponent();
        LoadLogo();

        _ticker = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(16)   // ≈60fps
        };
        _ticker.Tick += OnTick;

        Loaded += (_, _) =>
        {
            PlayIntro();
            _ticker.Start();
            SetStep(0);        // 运行时环境 —— 窗口能画出来,这一步本来就已完成
        };
    }

    // ══ 标识 ══════════════════════════════════════════════════════
    private void LoadLogo()
    {
        // 找不到就悄悄隐藏。闪屏是启动路径上的第一个窗口,绝不能因为少一张
        // 图就把整个程序拖崩 —— 那会变成一个比启动慢严重得多的问题。
        try
        {
            var uri = new Uri("pack://siteoforigin:,,,/Resources/Icon/htlab-logo.png", UriKind.Absolute);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = uri;
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            bmp.Freeze();
            Logo.Source = bmp;
        }
        catch
        {
            Logo.Visibility = Visibility.Collapsed;
            Divider.Visibility = Visibility.Collapsed;
        }
    }

    // ══ 入场动画 ══════════════════════════════════════════════════
    //  各元素按设计稿的 transition-delay 依次出场,整段约 0.9 秒。
    private void PlayIntro()
    {
        var ease = (IEasingFunction)FindResource("EaseOut");

        Fade(Card, 1, 280, 0);
        AnimateDouble(CardScale, ScaleTransform.ScaleXProperty, 1, 280, 0, ease);
        AnimateDouble(CardScale, ScaleTransform.ScaleYProperty, 1, 280, 0, ease);

        Fade(Logo, 1, 500, 80);
        AnimateDouble(LogoShift, TranslateTransform.YProperty, 0, 500, 80, ease);

        AnimateDouble(DividerScale, ScaleTransform.ScaleYProperty, 1, 500, 200, ease);

        // 标题从左揭示。Clip 的 Rect 宽度得等排版算完才知道,所以放在这里取。
        TitleText.UpdateLayout();
        var w = TitleText.ActualWidth > 0 ? TitleText.ActualWidth : 320;
        var reveal = new RectAnimation
        {
            From = new Rect(0, 0, 0, 48),
            To = new Rect(0, 0, w, 48),
            Duration = TimeSpan.FromMilliseconds(700),
            BeginTime = TimeSpan.FromMilliseconds(260),
            EasingFunction = ease
        };
        TitleClip.BeginAnimation(RectangleGeometry.RectProperty, reveal);

        Fade(Subtitle, 1, 450, 580);
        AnimateDouble(SubtitleShift, TranslateTransform.YProperty, 0, 450, 580, ease);

        AnimateDouble(TrackScale, ScaleTransform.ScaleXProperty, 1, 800, 160, ease);
        Fade(Readout, 1, 400, 400);
        Fade(StatusRow, 1, 400, 500);

        AnimateDouble(PlateShift, TranslateTransform.YProperty, 0, 550, 120, ease);
    }

    // ══ 进度惯性追随 ══════════════════════════════════════════════
    private void OnTick(object sender, EventArgs e)
    {
        if (Math.Abs(_target - _shown) < 0.05)
        {
            _shown = _target;
        }
        else
        {
            _shown += (_target - _shown) * 0.13;
        }

        var px = CardWidth * Math.Min(100, _shown) / 100.0;
        DatumFill.Width = px;

        if (px > 0)
        {
            DatumCursor.Visibility = Visibility.Visible;
            Canvas.SetLeft(DatumCursor, Math.Max(0, px - 2));
        }

        PctText.Text = ((int)Math.Round(Math.Min(100, _shown))).ToString();
    }

    // ══ 对外操作(均在本窗口的 Dispatcher 上执行)══════════════════
    public void SetStep(int index, string label = null, string detail = null)
    {
        if (_failed || _finished) return;

        index = Math.Max(0, Math.Min(Steps.Length - 1, index));
        var s = Steps[index];
        SwapStatus(label ?? s.Label, detail ?? s.Detail);

        // 进度按已完成步骤的权重累加,不是匀速走的假条子。
        // 封顶 97%:留给 SetReady() 收尾,免得进度早早满格却还在转。
        double sum = 0;
        for (int i = 0; i <= index; i++) sum += Steps[i].Weight;

        // **只增不减**。Steps 的顺序已按真实执行顺序排好,正常不会倒序调用,
        // 但这道闸门必须有:调用点散落在 OnStartup 与 OnInitialized 两处,
        // 以后但凡有人插一步、或者哪个分支提前跳过几步,顺序就可能乱。
        // 进度条往回缩比停着不动更让人以为出错了,而且很难查 ——
        // 宁可某一步的进度显得偏大,也不能倒退。
        _target = Math.Max(_target, Math.Min(97, sum));
    }

    public void SetReady()
    {
        if (_failed) return;
        _finished = true;      // 之后再来的 Step() 一律忽略,不许把 100% 拉回去
        _target = 100;
        StageLabel.Foreground = (Brush)FindResource("Brand");
        StageLabel.FontWeight = FontWeights.SemiBold;
        Leader.Visibility = Visibility.Collapsed;
        SwapStatus("就绪", "正在打开主窗口");
    }

    public void SetFailed(string message, string detail)
    {
        _failed = true;
        StageLabel.Foreground = (Brush)FindResource("Fail");
        StageLabel.FontWeight = FontWeights.SemiBold;
        DatumFill.Fill = (Brush)FindResource("Fail");
        DatumCursor.Fill = (Brush)FindResource("Fail");
        Leader.Visibility = Visibility.Collapsed;
        SwapStatus(message ?? "启动失败", detail ?? "详见 Logs 目录");
    }

    /// <summary>播放退场动画,结束后关窗并停掉本线程的消息循环。</summary>
    public void BeginClose(Action onClosed = null)
    {
        _ticker.Stop();

        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(340))
        {
            EasingFunction = (IEasingFunction)FindResource("EaseOut")
        };
        fade.Completed += (_, _) =>
        {
            try { Close(); } catch { }
            onClosed?.Invoke();
        };
        Card.BeginAnimation(OpacityProperty, fade);
        AnimateDouble(CardScale, ScaleTransform.ScaleXProperty, 0.99, 340, 0, null);
        AnimateDouble(CardScale, ScaleTransform.ScaleYProperty, 0.99, 340, 0, null);
    }

    // ══ 状态行换字:淡出 → 换 → 淡入,对应设计稿的 .swap ══════════
    private void SwapStatus(string label, string detail)
    {
        var outAnim = new DoubleAnimation(0, TimeSpan.FromMilliseconds(100));
        outAnim.Completed += (_, _) =>
        {
            StageLabel.Text = label;
            StageDetail.Text = detail;
            var inAnim = new DoubleAnimation(1, TimeSpan.FromMilliseconds(220));
            StageLabel.BeginAnimation(OpacityProperty, inAnim);
            StageDetail.BeginAnimation(OpacityProperty, inAnim);
        };
        StageLabel.BeginAnimation(OpacityProperty, outAnim);
        StageDetail.BeginAnimation(OpacityProperty, outAnim);
    }

    // ══ 动画小工具 ════════════════════════════════════════════════
    private static void Fade(UIElement el, double to, int ms, int delayMs)
    {
        el.BeginAnimation(OpacityProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms))
        {
            BeginTime = TimeSpan.FromMilliseconds(delayMs)
        });
    }

    private static void AnimateDouble(Animatable target, DependencyProperty prop,
                                      double to, int ms, int delayMs, IEasingFunction ease)
    {
        target.BeginAnimation(prop, new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms))
        {
            BeginTime = TimeSpan.FromMilliseconds(delayMs),
            EasingFunction = ease
        });
    }
}

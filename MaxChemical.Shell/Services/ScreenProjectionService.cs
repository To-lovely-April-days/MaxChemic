using MaxChemical.Infrastructure.Services;
using MaxChemical.Logging;
using MaxChemical.Shell.Utils;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Threading;

namespace MaxChemical.Shell.Services
{
    /// <summary>
    /// 一键投放到大屏。
    ///
    /// 为什么不在"复制"模式下做自适应:复制模式下电脑屏和大屏共用同一块桌面,系统只能给它们
    /// 一个共同的分辨率,软件只有一个窗口、只能按一种尺寸画,软件里怎么写都改变不了大屏上的效果 ——
    /// 这正是以前必须手动改电脑分辨率的原因。
    ///
    /// 这里的做法:让大屏以"扩展"方式接入(大屏用它自己的原生分辨率,当第二块屏),再把主窗口整个
    /// 搬过去铺满;电脑屏的分辨率不动。主窗口的布局是 Grid 星号比例,铺到多大的屏就按多大的屏排。
    ///
    /// 点一次按钮:
    ///   已有第二块屏          → 直接搬过去铺满
    ///   大屏已连但是"复制"    → 切成"扩展",等它出现后搬过去
    ///   还没连大屏            → 打开系统投放面板(Win+K),用户选中大屏后自动完成以上步骤
    /// 再点一次:收回到本机屏幕。大屏中途断开时自动收回。
    /// </summary>
    public sealed class ScreenProjectionService : IDisposable
    {
        /// <summary>大屏上是否盖住任务栏。true = 全屏(投屏观感);false = 和最大化一样留出任务栏。</summary>
        private static readonly bool FillWholeScreen = true;

        private static readonly TimeSpan SwitchTimeout = TimeSpan.FromSeconds(10);   // 复制→扩展 切换等待
        private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(90);  // 等用户在 Win+K 里选大屏
        private static readonly TimeSpan ExtendRetryInterval = TimeSpan.FromSeconds(4);

        private readonly ILocalizationService _localization;
        private readonly ILogService _logger = LogManager.GetLogger<ScreenProjectionService>();
        private readonly DispatcherTimer _waitTimer;

        private DateTime _waitDeadline;
        private DateTime _lastExtendRequest = DateTime.MinValue;
        private string? _homeDevice;       // 投放前窗口所在的屏,收回时回这里
        private string? _projectedDevice;  // 当前投放到的屏;null = 没在投放
        private bool _disposed;

        public ScreenProjectionService(ILocalizationService localization)
        {
            _localization = localization;
            _waitTimer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(500) };
            _waitTimer.Tick += OnWaitTick;
            SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        }

        public bool IsProjected => _projectedDevice != null;
        public bool IsWaiting => _waitTimer.IsEnabled;

        /// <summary>投放状态(投放中/等待中/未投放)变了,用来刷新按钮提示。</summary>
        public event EventHandler? StateChanged;

        /// <summary>窗口已经搬好并铺满了某块屏,用来同步标题栏的最大化状态。</summary>
        public event EventHandler? WindowPlaced;

        public void Toggle()
        {
            if (IsWaiting)
            {
                _logger.LogInformation("投放:用户取消等待");
                CancelWaiting();
                return;
            }

            if (IsProjected) ReturnToLocal();
            else StartProjection();
        }

        private void StartProjection()
        {
            var window = Application.Current?.MainWindow;
            if (window == null) return;

            var monitors = DisplayProjectionHelper.GetMonitors();
            _logger.LogInformation($"投放:检测到 {monitors.Count} 块屏 [{string.Join("; ", monitors)}]");

            var target = PickTarget(window, monitors);
            if (target != null)
            {
                MoveTo(window, target);
                return;
            }

            // 只有一块桌面,但大屏其实已经连着,处于"复制"模式 → 切成扩展
            if (DisplayProjectionHelper.IsAnyDisplayCloned(monitors.Count))
            {
                _logger.LogInformation("投放:大屏处于复制模式,切换为扩展模式");
                if (RequestExtend())
                {
                    BeginWaiting(SwitchTimeout);
                    return;
                }
            }

            // 还没连大屏 → 打开系统投放面板。先弹提示再开面板:面板一失去焦点就会自己收起来
            var answer = MessageBox.Show(window,
                _localization.GetString("Projection_NoScreen_Message",
                    "没有检测到大屏。\n\n点「确定」会打开 Windows 的投放面板(等同于按 Win+K),在里面选中大屏即可。\n连上后软件会自动切到大屏上铺满显示,电脑本身的分辨率不用改。"),
                _localization.GetString("Projection_Title", "投放到大屏"),
                MessageBoxButton.OKCancel, MessageBoxImage.Information);
            if (answer != MessageBoxResult.OK) return;

            BeginWaiting(ConnectTimeout);
            DisplayProjectionHelper.OpenCastPanel();
        }

        private void ReturnToLocal()
        {
            CancelWaiting(raise: false);
            _projectedDevice = null;

            var window = Application.Current?.MainWindow;
            if (window != null)
            {
                var monitors = DisplayProjectionHelper.GetMonitors();
                var home = monitors.FirstOrDefault(m => m.IsSameDevice(_homeDevice))
                           ?? monitors.FirstOrDefault(m => m.IsPrimary)
                           ?? monitors.FirstOrDefault();
                if (home != null)
                {
                    _logger.LogInformation($"投放:收回到 {home}");
                    // 回到本机按"最大化"的样子铺满工作区,和软件启动时一致
                    DisplayProjectionHelper.PlaceWindow(window, home, fillWholeScreen: false, OnWindowPlaced);
                }
            }

            RaiseStateChanged();
        }

        private void MoveTo(Window window, MonitorDescriptor target)
        {
            _homeDevice = DisplayProjectionHelper.GetMonitorOf(window)?.DeviceName;
            _projectedDevice = target.DeviceName;
            _logger.LogInformation($"投放:窗口从 {_homeDevice} 移到 {target}");

            DisplayProjectionHelper.PlaceWindow(window, target, FillWholeScreen, OnWindowPlaced);
            RaiseStateChanged();
        }

        /// <summary>选投放目标:窗口当前所在屏以外、面积最大的那块。</summary>
        private static MonitorDescriptor? PickTarget(Window window, IReadOnlyList<MonitorDescriptor> monitors)
        {
            if (monitors.Count < 2) return null;

            var current = DisplayProjectionHelper.GetMonitorOf(window);
            return monitors
                .Where(m => current == null ? !m.IsPrimary : !m.IsSameDevice(current.DeviceName))
                .OrderByDescending(m => m.Area)
                .FirstOrDefault();
        }

        private bool RequestExtend()
        {
            _lastExtendRequest = DateTime.Now;
            return DisplayProjectionHelper.SwitchToExtendTopology(_logger);
        }

        #region 等待大屏接入

        private void BeginWaiting(TimeSpan timeout)
        {
            _waitDeadline = DateTime.Now + timeout;
            _waitTimer.Start();
            RaiseStateChanged();
        }

        private void CancelWaiting(bool raise = true)
        {
            if (!_waitTimer.IsEnabled) return;
            _waitTimer.Stop();
            if (raise) RaiseStateChanged();
        }

        private void OnWaitTick(object? sender, EventArgs e)
        {
            var window = Application.Current?.MainWindow;
            if (window == null)
            {
                CancelWaiting();
                return;
            }

            var monitors = DisplayProjectionHelper.GetMonitors();
            var target = PickTarget(window, monitors);
            if (target != null)
            {
                _waitTimer.Stop();
                MoveTo(window, target);
                return;
            }

            // 从 Win+K 刚连上的大屏一般是"复制"模式 → 切成扩展。切换生效要一两秒,别每个 tick 都切
            if (DisplayProjectionHelper.IsAnyDisplayCloned(monitors.Count)
                && DateTime.Now - _lastExtendRequest > ExtendRetryInterval)
            {
                _logger.LogInformation("投放:大屏已接入(复制模式),切换为扩展模式");
                RequestExtend();
                if (_waitDeadline - DateTime.Now < SwitchTimeout)
                    _waitDeadline = DateTime.Now + SwitchTimeout;
                return;
            }

            if (DateTime.Now > _waitDeadline)
            {
                _logger.LogWarning("投放:等待大屏接入超时");
                CancelWaiting();
                MessageBox.Show(window,
                    _localization.GetString("Projection_Timeout_Message",
                        "没有等到大屏接入。\n\n请确认大屏已打开无线投屏(Miracast)接收、电脑 Wi-Fi 已打开,然后再点一次「投放到大屏」。"),
                    _localization.GetString("Projection_Title", "投放到大屏"),
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        #endregion

        #region 投放中显示器变化(断开 / 改分辨率)

        private void OnDisplaySettingsChanged(object? sender, EventArgs e)
        {
            // SystemEvents 不一定在 UI 线程回调
            Application.Current?.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(HandleDisplayChange));
        }

        private void HandleDisplayChange()
        {
            if (_disposed || !IsProjected) return;

            var window = Application.Current?.MainWindow;
            if (window == null) return;

            var monitors = DisplayProjectionHelper.GetMonitors();
            var target = monitors.FirstOrDefault(m => m.IsSameDevice(_projectedDevice));

            if (target == null || monitors.Count < 2)
            {
                // 大屏断开了(或被切回复制):系统会把窗口挪回电脑屏,但尺寸还是大屏的,按本机重新铺满
                _logger.LogWarning("投放:大屏已断开,窗口收回本机");
                ReturnToLocal();
                return;
            }

            // 大屏分辨率或缩放被改了:按新尺寸重新铺满
            DisplayProjectionHelper.PlaceWindow(window, target, FillWholeScreen, OnWindowPlaced);
        }

        #endregion

        private void OnWindowPlaced() => WindowPlaced?.Invoke(this, EventArgs.Empty);

        private void RaiseStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            _waitTimer.Stop();
            _waitTimer.Tick -= OnWaitTick;
        }
    }
}

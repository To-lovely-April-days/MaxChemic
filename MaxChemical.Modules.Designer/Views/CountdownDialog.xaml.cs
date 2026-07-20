// ============================================================
// CountdownDialog.xaml.cs — 完整替换
//
// 改动：用 Ellipse StrokeDashArray/Offset 实现圆环进度
// 新增：拖动支持
// 新增：等待设备稳定前置阶段（EnterStabilityPhase / UpdateStabilityProgress / EnterCountdownPhase）
// ============================================================
using MaxChemical.Infrastructure.Services;
using Prism.Ioc;
using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace MaxChemical.Modules.Designer.Views
{
    public partial class CountdownDialog : Window
    {
        private readonly ILocalizationService _localization;
        private readonly TimeSpan _totalTime;
        private TimeSpan _remainingTime;
        private readonly DispatcherTimer _timer;
        private bool _isSkipped = false;
        private bool _isStopped = false;

        // ★ 新增：是否处于"等待设备稳定"前置阶段（此阶段不跑倒计时）
        private bool _isStabilityPhase = false;

        // 圆环周长（直径110，stroke占5px，有效半径 = (110-5)/2 = 52.5）
        private const double RingCircumference = 2 * Math.PI * 52.5; // ≈ 329.87

        // ★ 新增：倒计时自然走完标志（非模态下不能用 DialogResult，改用此标志）
        private bool _isCompleted = false;

        // ★ 新增：标题文字(优先显示等待节点的"描述")，用于阶段切换时恢复
        private string _titleText;

        public bool IsSkipped => _isSkipped;
        public bool IsStopped => _isStopped;
        public bool IsCompleted => _isCompleted;

        public CountdownDialog(TimeSpan waitTime, string waitInfo = null)
        {
            InitializeComponent();

            // ★ 任务栏图标：用运行目录的绝对路径设置，避免 XAML 资源路径解析问题
            try
            {
                var iconPath = System.IO.Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory, "Resources", "Icon", "app.ico");
                if (System.IO.File.Exists(iconPath))
                    this.Icon = new System.Windows.Media.Imaging.BitmapImage(new Uri(iconPath));
            }
            catch { /* 图标加载失败不影响弹框使用 */ }

            _localization = ContainerLocator.Container.Resolve<ILocalizationService>();

            SkipButton.Content = _localization.GetString("Countdown_Skip", "跳过等待");
            StopButton.Content = _localization.GetString("Countdown_Stop", "停止流程");
            TbFixedLabel.Text = _localization.GetString("Countdown_Fixed", "固定时间等待");

            _totalTime = waitTime;
            _remainingTime = waitTime;

            if (!string.IsNullOrEmpty(waitInfo))
            {
                // 把等待节点的"描述"作为标题持久显示；WaitInfoText 会被倒计时进度覆盖，故放到 TbFixedLabel
                _titleText = waitInfo;
                TbFixedLabel.Text = waitInfo;
            }

            // 初始化圆环
            InitializeRing();

            // 初始化显示
            UpdateDisplay();

            // 定时器
            _timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(100)
            };
            _timer.Tick += Timer_Tick;
        }

        /// <summary>
        /// 初始化圆环进度条
        /// </summary>
        private void InitializeRing()
        {
            // 设置 StrokeDashArray = 圆周长（一整圈）
            ProgressRing.StrokeDashArray = new System.Windows.Media.DoubleCollection { RingCircumference / 5.0 };
            // 初始 Offset = 0（满圈）
            ProgressRing.StrokeDashOffset = 0;
        }

        public void StartCountdown()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(StartCountdown);
                return;
            }
            _timer.Start();
        }

        // ============================================================
        // ★ 新增：等待设备稳定阶段支持
        // ============================================================

        /// <summary>
        /// 进入"等待设备稳定"阶段（倒计时尚未开始）。
        /// 调用后对话框显示"等待设备稳定中…"，圆环置满表示尚未计时。
        /// </summary>
        public void EnterStabilityPhase(string deviceCountText)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => EnterStabilityPhase(deviceCountText));
                return;
            }

            _isStabilityPhase = true;

            // 稳定阶段不跑计时器
            _timer.Stop();

            TbFixedLabel.Text = _localization.GetString("Stability_Title", "等待设备稳定");
            CountdownText.Text = "…";
            WaitInfoText.Text = string.Format(
                _localization.GetString("Stability_Waiting", "等待 {0} 进入稳定状态…"),
                deviceCountText ?? "");
            StatusText.Text = _localization.GetString("Stability_Polling", "正在检测设备状态");

            // 稳定阶段不显示真实进度，圆环置满
            ProgressRing.StrokeDashOffset = 0;
        }

        /// <summary>
        /// 在稳定阶段刷新进度文字（例如 "3/5 设备已稳定，已等待 12s"）。
        /// 仅在稳定阶段生效。
        /// </summary>
        public void UpdateStabilityProgress(int stableCount, int totalCount, int elapsedSeconds)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => UpdateStabilityProgress(stableCount, totalCount, elapsedSeconds));
                return;
            }
            if (!_isStabilityPhase) return;

            WaitInfoText.Text = string.Format(
                _localization.GetString("Stability_Progress", "{0}/{1} 设备已稳定，已等待 {2}s"),
                stableCount, totalCount, elapsedSeconds);
        }

        /// <summary>
        /// 稳定阶段结束，切换到正常倒计时阶段并启动计时。
        /// </summary>
        public void EnterCountdownPhase()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(EnterCountdownPhase);
                return;
            }

            _isStabilityPhase = false;
            TbFixedLabel.Text = !string.IsNullOrEmpty(_titleText)
                ? _titleText
                : _localization.GetString("Countdown_Fixed", "固定时间等待");

            // 重置剩余时间并刷新显示，再启动计时器
            _remainingTime = _totalTime;
            InitializeRing();
            UpdateDisplay();

            _timer.Start();
        }

        private void Timer_Tick(object sender, EventArgs e)
        {
            _remainingTime = _remainingTime.Subtract(TimeSpan.FromMilliseconds(100));

            if (_remainingTime <= TimeSpan.Zero)
            {
                _remainingTime = TimeSpan.Zero;
                _timer.Stop();
                _isCompleted = true;   // ★ 非模态：用标志代替 DialogResult
                Close();
                return;
            }

            UpdateDisplay();
        }

        private void UpdateDisplay()
        {
            // ★ 稳定阶段不更新倒计时显示（文字由稳定阶段方法控制）
            if (_isStabilityPhase) return;

            // 倒计时文字
            if (_remainingTime.TotalHours >= 1)
                CountdownText.Text = _remainingTime.ToString(@"hh\:mm\:ss");
            else
                CountdownText.Text = _remainingTime.ToString(@"mm\:ss");

            // 圆环进度（从满到空）
            double progress = _remainingTime.TotalMilliseconds / _totalTime.TotalMilliseconds;
            double offset = RingCircumference * (1 - progress) / 5.0;
            ProgressRing.StrokeDashOffset = -offset;

            // 兼容旧的 ProgressBar（隐藏但保留）
            CountdownProgress.Value = (1.0 - progress) * 100;

            // 等待信息
            var elapsed = _totalTime - _remainingTime;
            string totalStr = FormatTime(_totalTime);
            string elapsedStr = FormatTime(elapsed);
            WaitInfoText.Text = string.Format(_localization.GetString("Countdown_WaitInfo_Waiting", "已等待 {0} / 共 {1}"), elapsedStr, totalStr);

            // 状态文本
            var totalSeconds = (int)_remainingTime.TotalSeconds;
            if (totalSeconds > 60)
            {
                var minutes = totalSeconds / 60;
                var seconds = totalSeconds % 60;
                StatusText.Text = string.Format(_localization.GetString("Countdown_Status_RemainM", "剩余 {0} 分 {1} 秒"), minutes, seconds);
            }
            else
            {
                StatusText.Text = string.Format(_localization.GetString("Countdown_Status_RemainS", "剩余 {0} 秒"), totalSeconds);
            }
        }

        private string FormatTime(TimeSpan ts)
        {
            if (ts.TotalHours >= 1)
                return ts.ToString(@"h\:mm\:ss");
            return $"{(int)ts.TotalMinutes}:{ts.Seconds:D2}";
        }

        private void SkipButton_Click(object sender, RoutedEventArgs e)
        {
            _isSkipped = true;
            _timer?.Stop();
            Close();   // ★ 非模态：不设 DialogResult
        }

        private void StopButton_Click(object sender, RoutedEventArgs e)
        {
            _isStopped = true;
            _timer?.Stop();
            Close();   // ★ 非模态：不设 DialogResult
        }

        /// <summary>
        /// 窗口拖动
        /// </summary>
        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                try { DragMove(); } catch { }
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            _timer?.Stop();
            base.OnClosed(e);
        }
    }
}
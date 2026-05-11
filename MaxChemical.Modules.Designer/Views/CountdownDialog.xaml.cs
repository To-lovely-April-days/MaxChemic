// ============================================================
// CountdownDialog.xaml.cs — 完整替换
//
// 改动：用 Ellipse StrokeDashArray/Offset 实现圆环进度
// 新增：拖动支持
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

        // 圆环周长（直径110，stroke占5px，有效半径 = (110-5)/2 = 52.5）
        private const double RingCircumference = 2 * Math.PI * 52.5; // ≈ 329.87

        public bool IsSkipped => _isSkipped;
        public bool IsStopped => _isStopped;

        public CountdownDialog(TimeSpan waitTime, string waitInfo = null)
        {
            InitializeComponent();

            _localization = ContainerLocator.Container.Resolve<ILocalizationService>();

            SkipButton.Content = _localization.GetString("Countdown_Skip", "跳过等待");
            StopButton.Content = _localization.GetString("Countdown_Stop", "停止流程");
            TbFixedLabel.Text = _localization.GetString("Countdown_Fixed", "固定时间等待");

            _totalTime = waitTime;
            _remainingTime = waitTime;

            if (!string.IsNullOrEmpty(waitInfo))
            {
                WaitInfoText.Text = waitInfo;
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

        private void Timer_Tick(object sender, EventArgs e)
        {
            _remainingTime = _remainingTime.Subtract(TimeSpan.FromMilliseconds(100));

            if (_remainingTime <= TimeSpan.Zero)
            {
                _remainingTime = TimeSpan.Zero;
                _timer.Stop();
                DialogResult = true;
                Close();
                return;
            }

            UpdateDisplay();
        }

        private void UpdateDisplay()
        {
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
            DialogResult = true;
            Close();
        }

        private void StopButton_Click(object sender, RoutedEventArgs e)
        {
            _isStopped = true;
            _timer?.Stop();
            DialogResult = false;
            Close();
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

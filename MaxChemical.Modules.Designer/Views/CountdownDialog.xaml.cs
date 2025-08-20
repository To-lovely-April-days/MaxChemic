using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace MaxChemical.Modules.Designer.Views
{
    public partial class CountdownDialog : Window
    {
        private readonly TimeSpan _totalTime;
        private TimeSpan _remainingTime;
        private readonly DispatcherTimer _timer;
        private bool _isSkipped = false;
        private bool _isStopped = false;

        public bool IsSkipped => _isSkipped;
        public bool IsStopped => _isStopped;

        public CountdownDialog(TimeSpan waitTime, string waitInfo = null)
        {
            InitializeComponent();

            _totalTime = waitTime;
            _remainingTime = waitTime;

            // 设置等待信息
            if (!string.IsNullOrEmpty(waitInfo))
            {
                WaitInfoText.Text = waitInfo;
            }

            // 初始化界面
            UpdateDisplay();

            // 设置定时器 - 确保在UI线程创建
            _timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(100)
            };
            _timer.Tick += Timer_Tick;
        }

        public void StartCountdown()
        {
            // 确保在UI线程调用
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

                // 倒计时完成，自动关闭
                DialogResult = true;
                Close();
                return;
            }

            UpdateDisplay();
        }

        private void UpdateDisplay()
        {
            // 更新倒计时文本
            if (_remainingTime.TotalHours >= 1)
            {
                CountdownText.Text = _remainingTime.ToString(@"hh\:mm\:ss");
            }
            else
            {
                CountdownText.Text = _remainingTime.ToString(@"mm\:ss");
            }

            // 更新进度条
            var progress = 1.0 - (_remainingTime.TotalMilliseconds / _totalTime.TotalMilliseconds);
            CountdownProgress.Value = progress * 100;

            // 更新状态文本
            var totalSeconds = (int)_remainingTime.TotalSeconds;
            if (totalSeconds > 60)
            {
                var minutes = totalSeconds / 60;
                var seconds = totalSeconds % 60;
                StatusText.Text = $"剩余 {minutes} 分 {seconds} 秒";
            }
            else
            {
                StatusText.Text = $"剩余 {totalSeconds} 秒";
            }
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

        protected override void OnClosed(EventArgs e)
        {
            _timer?.Stop();
            base.OnClosed(e);
        }
    }
}
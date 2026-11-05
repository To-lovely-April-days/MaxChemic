using System;
using System.Windows;
using System.Windows.Input;
using MaxChemical.Logging;

namespace MaxChemical.Shell.Views
{
    /// <summary>
    /// 用户手册全屏视频播放窗口:在主窗口当前所在屏幕(支持扩展屏)全屏循环播放,
    /// 按 Esc 退出。
    /// </summary>
    public partial class UserManualPlayerWindow : Window
    {
        private readonly string _videoPath;
        private readonly ILogService _logger;
        private bool _mediaOpened;

        /// <summary>播放失败的原因(解码失败/文件损坏等),正常关闭时为 null。</summary>
        public string PlaybackError { get; private set; }

        public UserManualPlayerWindow(string videoPath, string exitHint)
        {
            InitializeComponent();
            _videoPath = videoPath;
            _logger = LogManager.GetLogger<UserManualPlayerWindow>();
            EscHint.Text = exitHint;
            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            try
            {
                // 铺满主窗口当前所在屏幕的整个区域(含任务栏),实现真全屏
                var anchor = Application.Current.MainWindow ?? this;
                var bounds = Utils.ScreenHelper.GetCurrentScreenBounds(anchor);
                Left = bounds.Left;
                Top = bounds.Top;
                Width = bounds.Width;
                Height = bounds.Height;

                _logger.LogInformation(
                    $"开始播放用户手册视频: {_videoPath}, 窗口 {bounds.Width:F0}x{bounds.Height:F0} @ ({bounds.Left:F0},{bounds.Top:F0})");

                Player.Source = new Uri(_videoPath, UriKind.Absolute);
                Player.Play();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "用户手册视频初始化失败");
                PlaybackError = ex.Message;
                Close();
                return;
            }

            Activate();
            Focus();
        }

        private void Player_MediaOpened(object sender, RoutedEventArgs e)
        {
            _mediaOpened = true;
            _logger.LogInformation(
                $"视频打开成功: {Player.NaturalVideoWidth}x{Player.NaturalVideoHeight}, 时长 {Player.NaturalDuration}");
        }

        // 播完自动回到开头继续播放,实现循环
        private void Player_MediaEnded(object sender, RoutedEventArgs e)
        {
            try
            {
                Player.Position = TimeSpan.Zero;
                Player.Play();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "视频循环续播失败");
            }
        }

        private void Player_MediaFailed(object sender, ExceptionRoutedEventArgs e)
        {
            // 大视频/缺解码器(如 HEVC)会走到这里:记录原因并退回主界面,不让进程崩掉
            _mediaOpened = false;
            PlaybackError = e.ErrorException?.Message ?? "未知错误";
            _logger.LogError($"用户手册视频播放失败: {PlaybackError}");
            Close();
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
                Close();
        }

        protected override void OnClosed(EventArgs e)
        {
            try
            {
                // 媒体未成功打开时调用 Stop 会抛异常,这里保护一下
                if (_mediaOpened)
                    Player.Stop();
                Player.Close(); // 释放媒体文件句柄
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"关闭视频播放器时出错: {ex.Message}");
            }
            base.OnClosed(e);
        }
    }
}

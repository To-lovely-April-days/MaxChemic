using System.Windows;
using System.Windows.Media.Animation;

namespace MaxChemical.Modules.DOE.Views
{
    public partial class OlsLoadingWindow : Window
    {
        public OlsLoadingWindow()
        {
            InitializeComponent();
        }

        public void SetOlsTitle(string title)
        {
            Dispatcher.Invoke(new Action(() => { TbOlsTitle.Text = title; }));
        }

        /// <summary>
        /// 更新状态文字 + 进度条
        /// </summary>
        public void UpdateStatus(string text, double progressPercent = -1)
        {
            Dispatcher.Invoke(() =>
            {
                StatusText.Text = text;

                if (progressPercent >= 0)
                {
                    // 有明确进度 → 直接设置宽度
                    double maxWidth = 232; // 280 - 24*2
                    ProgressFill.Width = maxWidth * progressPercent / 100.0;
                }
                else
                {
                    // 无明确进度 → 脉冲动画
                    AnimateIndeterminate();
                }
            });
        }

        private bool _indeterminateStarted;

        private void AnimateIndeterminate()
        {
            if (_indeterminateStarted) return;
            _indeterminateStarted = true;

            double maxWidth = 232;
            var anim = new DoubleAnimation
            {
                From = 0,
                To = maxWidth,
                Duration = System.TimeSpan.FromSeconds(2),
                RepeatBehavior = RepeatBehavior.Forever,
                AutoReverse = true,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            };
            ProgressFill.BeginAnimation(WidthProperty, anim);
        }
    }
}
using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace MaxChemical.Modules.Designer.Models
{
    /// <summary>
    /// 独立 Dispatcher 线程的 Loading 遮罩窗口
    /// 视觉效果与原 ExperimentDetailWindow 内嵌 loading 完全一致
    /// 因为跑在独立线程，主 UI 线程再卡也不影响动画流畅度
    /// </summary>
    public class LoadingOverlayWindow
    {
        private Window? _window;
        private TextBlock? _messageText;
        private Thread? _thread;
        private Dispatcher? _dispatcher;
        private readonly string _message;

        // owner 窗口的位置快照（从主线程获取后传给独立线程）
        private double _centerX;
        private double _centerY;
        private bool _hasPosition;

        public LoadingOverlayWindow(string message = "正在加载参数曲线数据", Window? owner = null)
        {
            _message = message;

            // 在主线程上提前获取 owner 位置
            if (owner != null)
            {
                try
                {
                    owner.Dispatcher.Invoke(() =>
                    {
                        _centerX = owner.Left + owner.ActualWidth / 2;
                        _centerY = owner.Top + owner.ActualHeight / 2;
                        _hasPosition = true;
                    });
                }
                catch { }
            }
        }

        /// <summary>
        /// 在独立线程上显示 Loading 窗口
        /// </summary>
        public void Show()
        {
            var ready = new ManualResetEventSlim(false);

            _thread = new Thread(() =>
            {
                _dispatcher = Dispatcher.CurrentDispatcher;
                _window = BuildWindow();
                _window.Show();
                ready.Set();
                Dispatcher.Run();
            });

            _thread.SetApartmentState(ApartmentState.STA);
            _thread.IsBackground = true;
            _thread.Name = "LoadingOverlayThread";
            _thread.Start();

            ready.Wait(TimeSpan.FromSeconds(3));
        }

        /// <summary>
        /// 更新提示文字
        /// </summary>
        public void UpdateMessage(string message)
        {
            _dispatcher?.BeginInvoke(() =>
            {
                if (_messageText != null)
                    _messageText.Text = message;
            });
        }

        /// <summary>
        /// 关闭窗口并终止独立线程
        /// </summary>
        public void Close()
        {
            var dispatcher = _dispatcher;
            if (dispatcher != null && !dispatcher.HasShutdownStarted)
            {
                dispatcher.BeginInvoke(() =>
                {
                    try
                    {
                        _window?.Close();
                    }
                    catch { }
                    finally
                    {
                        dispatcher.InvokeShutdown();
                    }
                });
            }

            _thread = null;
            _dispatcher = null;
            _window = null;
            _messageText = null;
        }

        /// <summary>
        /// 构建窗口 UI — 完全复刻原 XAML 中的 loading 遮罩样式
        /// </summary>
        private Window BuildWindow()
        {
            const double winW = 240;
            const double winH = 130;

            var window = new Window
            {
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                Topmost = true,
                Width = winW,
                Height = winH,
                WindowStartupLocation = WindowStartupLocation.Manual,
            };

            // 定位到 owner 中心
            if (_hasPosition)
            {
                window.Left = _centerX - winW / 2;
                window.Top = _centerY - winH / 2;
            }
            else
            {
                window.Left = (SystemParameters.PrimaryScreenWidth - winW) / 2;
                window.Top = (SystemParameters.PrimaryScreenHeight - winH) / 2;
            }

            // ── 外层容器（圆角 + 阴影，与 ExperimentDetailWindow 主边框风格一致） ──
            var outerBorder = new Border
            {
                Margin = new Thickness(10),
                CornerRadius = new CornerRadius(12),
                Background = new SolidColorBrush(Color.FromRgb(0xFA, 0xFB, 0xFC)), // #FAFBFC
                ClipToBounds = true,
                Effect = new DropShadowEffect
                {
                    Color = Colors.Black,
                    BlurRadius = 24,
                    ShadowDepth = 6,
                    Opacity = 0.10,
                    Direction = 270,
                },
            };

            // ── 内容 StackPanel ──
            var stack = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            outerBorder.Child = stack;

            // ── 三个弹跳圆点（与原 XAML 一致：#2D3A8C, #3B5BDB, #2D3A8C） ──
            var dotsPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 14),
            };

            var dot1 = MakeDot(Color.FromRgb(0x2D, 0x3A, 0x8C));
            var dot2 = MakeDot(Color.FromRgb(0x3B, 0x5B, 0xDB));
            var dot3 = MakeDot(Color.FromRgb(0x2D, 0x3A, 0x8C));
            dot3.Margin = new Thickness(0); // 最后一个没有右 margin

            dotsPanel.Children.Add(dot1);
            dotsPanel.Children.Add(dot2);
            dotsPanel.Children.Add(dot3);
            stack.Children.Add(dotsPanel);

            // ── 文字（与原 XAML 一致） ──
            _messageText = new TextBlock
            {
                Text = _message,
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80)), // #6B7280
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 10),
            };
            stack.Children.Add(_messageText);

            // ── 进度条（与原 XAML 完全一致：120x2 容器，48x2 滑块） ──
            var barContainer = new Grid
            {
                Width = 120,
                Height = 2,
                HorizontalAlignment = HorizontalAlignment.Center,
                ClipToBounds = true,
            };

            var barBg = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0xE8, 0xEA, 0xED)), // #E8EAED
                CornerRadius = new CornerRadius(1),
            };
            barContainer.Children.Add(barBg);

            var bar = new Border
            {
                Width = 48,
                Height = 2,
                CornerRadius = new CornerRadius(1),
                Background = new SolidColorBrush(Color.FromRgb(0x2D, 0x3A, 0x8C)), // #2D3A8C
                HorizontalAlignment = HorizontalAlignment.Left,
                RenderTransform = new TranslateTransform(0, 0),
            };
            barContainer.Children.Add(bar);
            stack.Children.Add(barContainer);

            window.Content = outerBorder;

            // ── Loaded 后启动动画 ──
            window.Loaded += (s, e) =>
            {
                var sb = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };

                // 与原 code-behind 完全相同的参数
                AddBounceAnim(sb, dot1, TimeSpan.Zero);
                AddBounceAnim(sb, dot2, TimeSpan.FromMilliseconds(150));
                AddBounceAnim(sb, dot3, TimeSpan.FromMilliseconds(300));
                AddBarAnim(sb, bar);

                sb.Begin();
            };

            return window;
        }

        private static Ellipse MakeDot(Color color)
        {
            return new Ellipse
            {
                Width = 8,
                Height = 8,
                Fill = new SolidColorBrush(color),
                Margin = new Thickness(0, 0, 8, 0),
                RenderTransform = new TranslateTransform(0, 0),
            };
        }

        /// <summary>
        /// 弹跳动画 — 参数与原 ExperimentDetailWindow.AddBounceAnimation 完全一致
        /// </summary>
        private static void AddBounceAnim(Storyboard sb, UIElement target, TimeSpan beginTime)
        {
            var anim = new DoubleAnimationUsingKeyFrames
            {
                BeginTime = beginTime,
                Duration = new Duration(TimeSpan.FromMilliseconds(1200)),
                RepeatBehavior = RepeatBehavior.Forever,
            };
            anim.KeyFrames.Add(new EasingDoubleKeyFrame(0,
                KeyTime.FromTimeSpan(TimeSpan.Zero)));
            anim.KeyFrames.Add(new EasingDoubleKeyFrame(-8,
                KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(200)),
                new SineEase { EasingMode = EasingMode.EaseOut }));
            anim.KeyFrames.Add(new EasingDoubleKeyFrame(0,
                KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(400)),
                new SineEase { EasingMode = EasingMode.EaseIn }));
            anim.KeyFrames.Add(new EasingDoubleKeyFrame(0,
                KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1200))));

            Storyboard.SetTarget(anim, target);
            Storyboard.SetTargetProperty(anim,
                new PropertyPath("(UIElement.RenderTransform).(TranslateTransform.Y)"));
            sb.Children.Add(anim);
        }

        /// <summary>
        /// 进度条滑动动画 — 参数与原 ExperimentDetailWindow.AddBarAnimation 完全一致
        /// </summary>
        private static void AddBarAnim(Storyboard sb, UIElement target)
        {
            var anim = new DoubleAnimation
            {
                From = -48,
                To = 120,
                Duration = new Duration(TimeSpan.FromMilliseconds(1500)),
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            };

            Storyboard.SetTarget(anim, target);
            Storyboard.SetTargetProperty(anim,
                new PropertyPath("(UIElement.RenderTransform).(TranslateTransform.X)"));
            sb.Children.Add(anim);
        }
    }
}
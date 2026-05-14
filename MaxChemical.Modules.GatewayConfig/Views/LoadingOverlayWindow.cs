using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace MaxChemical.Modules.GatewayConfig.Views
{
    /// <summary>
    /// 网关配置专用 Loading 遮罩,独立 Dispatcher 线程。
    /// SDK 阻塞调用不会冻住动画。
    /// </summary>
    public class LoadingOverlayWindow
    {
        private readonly Window _owner;
        private Thread _thread;
        private Window _window;
        private TextBlock _messageText;
        private Dispatcher _dispatcher;
        private readonly ManualResetEventSlim _ready = new ManualResetEventSlim(false);

        // 在主线程上抓 owner 几何,跨线程时只传值类型,不再访问 owner 对象
        private double _ownerLeft, _ownerTop, _ownerWidth, _ownerHeight;
        private bool _ownerSnapped;

        public LoadingOverlayWindow(Window owner)
        {
            _owner = owner;
        }

        public void Show(string initialMessage)
        {
            // 先在调用方 (主 UI) 线程里把 owner 几何快照下来
            if (_owner != null && _owner.IsLoaded)
            {
                _ownerLeft = _owner.Left;
                _ownerTop = _owner.Top;
                _ownerWidth = _owner.ActualWidth;
                _ownerHeight = _owner.ActualHeight;
                _ownerSnapped = true;
            }

            _thread = new Thread(() =>
            {
                _window = BuildWindow(initialMessage);
                _dispatcher = _window.Dispatcher;
                _window.Show();
                _ready.Set();
                Dispatcher.Run();
            })
            { IsBackground = true };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            _ready.Wait(1000);
        }

        public void UpdateMessage(string message)
        {
            try
            {
                _dispatcher?.BeginInvoke(new Action(() =>
                {
                    if (_messageText != null) _messageText.Text = message;
                }));
            }
            catch { }
        }

        public void Hide()
        {
            try
            {
                _dispatcher?.BeginInvoke(new Action(() =>
                {
                    _window?.Close();
                    Dispatcher.ExitAllFrames();
                }));
            }
            catch { }
        }

        private Window BuildWindow(string initialMessage)
        {
            var w = new Window
            {
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = new SolidColorBrush(Color.FromArgb(0x8C, 0x1A, 0x1A, 0x1A)),
                ShowInTaskbar = false,
                Topmost = true,
                WindowStartupLocation = WindowStartupLocation.Manual,
            };

            if (_ownerSnapped)
            {
                w.Left = _ownerLeft;
                w.Top = _ownerTop;
                w.Width = Math.Max(_ownerWidth, 320);
                w.Height = Math.Max(_ownerHeight, 200);
            }
            else
            {
                w.WindowState = WindowState.Maximized;
            }

            var card = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0xFA, 0xFB, 0xFC)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xD4, 0xD4, 0xD4)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(28, 24, 28, 22),
                Width = 320,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    Color = Colors.Black,
                    Opacity = 0.18,
                    BlurRadius = 24,
                    ShadowDepth = 6,
                    Direction = 270,
                }
            };

            var stack = new StackPanel();

            var dotsPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 16),
            };
            var dot1 = MakeDot(Color.FromRgb(0xA4, 0x16, 0x26));
            var dot2 = MakeDot(Color.FromRgb(0x6B, 0x0E, 0x1A));
            var dot3 = MakeDot(Color.FromRgb(0xA4, 0x16, 0x26));
            dotsPanel.Children.Add(dot1);
            dotsPanel.Children.Add(dot2);
            dotsPanel.Children.Add(dot3);
            stack.Children.Add(dotsPanel);
            StartDotAnimation(dot1, 0.0);
            StartDotAnimation(dot2, 0.2);
            StartDotAnimation(dot3, 0.4);

            _messageText = new TextBlock
            {
                Text = initialMessage,
                HorizontalAlignment = HorizontalAlignment.Center,
                FontSize = 14,
                Foreground = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A)),
                FontWeight = FontWeights.Medium,
                Margin = new Thickness(0, 0, 0, 4),
            };
            stack.Children.Add(_messageText);

            var subText = new TextBlock
            {
                Text = "请耐心等待,最长约需 30 秒",
                HorizontalAlignment = HorizontalAlignment.Center,
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(0x75, 0x75, 0x75)),
            };
            stack.Children.Add(subText);

            card.Child = stack;
            w.Content = card;
            return w;
        }

        private static Ellipse MakeDot(Color color)
        {
            return new Ellipse
            {
                Width = 10,
                Height = 10,
                Fill = new SolidColorBrush(color),
                Margin = new Thickness(4, 0, 4, 0),
            };
        }

        private static void StartDotAnimation(Ellipse dot, double beginSeconds)
        {
            var transform = new ScaleTransform(1.0, 1.0);
            dot.RenderTransform = transform;
            dot.RenderTransformOrigin = new Point(0.5, 0.5);

            var anim = new DoubleAnimation
            {
                From = 1.0,
                To = 0.4,
                Duration = TimeSpan.FromMilliseconds(500),
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                BeginTime = TimeSpan.FromSeconds(beginSeconds),
            };
            var opacityAnim = new DoubleAnimation
            {
                From = 1.0,
                To = 0.3,
                Duration = TimeSpan.FromMilliseconds(500),
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                BeginTime = TimeSpan.FromSeconds(beginSeconds),
            };
            transform.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
            transform.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
            dot.BeginAnimation(UIElement.OpacityProperty, opacityAnim);
        }
    }
}
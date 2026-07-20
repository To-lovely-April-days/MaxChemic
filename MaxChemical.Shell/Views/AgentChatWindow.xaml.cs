using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MaxChemical.Shell.ViewModels;

namespace MaxChemical.Shell.Views
{
    /// <summary>小桐对话窗口(单例复用,关闭即隐藏)。</summary>
    public partial class AgentChatWindow : Window
    {
        private readonly AgentChatViewModel _vm;
        private ScrollViewer _msgScroll;
        private Rect _savedBounds;
        private bool _boundsSaved;

        public AgentChatWindow(AgentChatViewModel vm)
        {
            InitializeComponent();
            _vm = vm;
            DataContext = vm;
            vm.ScrollToEndRequested += ScrollMessagesToEnd;

            TryLoadAvatar();

            // 执行值守小组件模式切换
            vm.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(AgentChatViewModel.IsMiniMode))
                    ApplyMiniMode(vm.IsMiniMode);
            };

            // 迷你态下气泡内容变化会改变小组件尺寸,始终保持贴右下角
            SizeChanged += (s, e) =>
            {
                if (_vm.IsMiniMode && IsVisible) PositionMiniBottomRight();
            };

            // 每次显示都滚到最新消息(虚拟化列表默认停在顶部);迷你态下重新定位到右下角
            IsVisibleChanged += (s, e) =>
            {
                if (e.NewValue is bool visible && visible)
                {
                    if (_vm.IsMiniMode) PositionMiniBottomRight();
                    else Dispatcher.BeginInvoke(new Action(ScrollMessagesToEnd),
                        System.Windows.Threading.DispatcherPriority.Loaded);
                }
            };

            // 关闭改为隐藏,保留会话上下文
            Closing += (s, e) =>
            {
                e.Cancel = true;
                Hide();
            };
        }

        /// <summary>
        /// 加载小桐头像:把图片命名为 xiaotong.png(或 .jpg)放到 Shell 项目的 Resources 文件夹即可。
        /// 兼容两种添加方式:生成操作=Resource(编译进程序集),或 内容+复制到输出目录(磁盘文件)。
        /// 找不到图片时保持文字标「桐」,绝不报错。
        /// </summary>
        private void TryLoadAvatar()
        {
            string[] candidates = { "xiaotong.png", "xiaotong.jpg", "XiaoTong.png", "avatar.png" };
            ImageSource source = null;

            foreach (var file in candidates)
            {
                // 方式一:生成操作 = Resource
                try
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.UriSource = new Uri($"pack://application:,,,/Resources/{file}", UriKind.Absolute);
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.EndInit();
                    bmp.Freeze();
                    source = bmp;
                    break;
                }
                catch { }

                // 方式二:内容文件复制到输出目录
                try
                {
                    string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", file);
                    if (File.Exists(path))
                    {
                        var bmp = new BitmapImage();
                        bmp.BeginInit();
                        bmp.UriSource = new Uri(path, UriKind.Absolute);
                        bmp.CacheOption = BitmapCacheOption.OnLoad;
                        bmp.EndInit();
                        bmp.Freeze();
                        source = bmp;
                        break;
                    }
                }
                catch { }
            }

            if (source != null)
            {
                // Uniform:完整显示整张头像(不裁剪、不加背景);同时收起文字底标
                var brush = new ImageBrush(source) { Stretch = Stretch.Uniform };
                brush.Freeze();
                Resources["XiaoTongAvatarBrush"] = brush;
                Resources["XiaoTongFallbackVisibility"] = Visibility.Collapsed;
            }
        }

        /// <summary>
        /// 执行值守小组件模式:窗口收缩为右下角小部件(头像+输入框+当前播报气泡);
        /// 退出时恢复原尺寸位置。切换由 VM 的 IsMiniMode 驱动。
        /// </summary>
        private void ApplyMiniMode(bool mini)
        {
            if (mini)
            {
                if (WindowState == WindowState.Maximized) WindowState = WindowState.Normal;
                if (IsVisible && ActualWidth > 0)
                {
                    _savedBounds = new Rect(Left, Top, ActualWidth, ActualHeight);
                    _boundsSaved = true;
                }

                MinWidth = 0;
                MinHeight = 0;
                FullContent.Visibility = Visibility.Collapsed;
                MiniContent.Visibility = Visibility.Visible;
                SizeToContent = SizeToContent.WidthAndHeight;
                Topmost = true;
                ResizeMode = ResizeMode.NoResize;
                PositionMiniBottomRight();
            }
            else
            {
                SizeToContent = SizeToContent.Manual;
                MiniContent.Visibility = Visibility.Collapsed;
                FullContent.Visibility = Visibility.Visible;
                MinWidth = 920;
                MinHeight = 620;
                Topmost = false;
                ResizeMode = ResizeMode.CanResize;

                if (_boundsSaved)
                {
                    Left = _savedBounds.Left;
                    Top = _savedBounds.Top;
                    Width = _savedBounds.Width;
                    Height = _savedBounds.Height;
                }
                else
                {
                    Width = 1080;
                    Height = 760;
                }

                Dispatcher.BeginInvoke(new Action(ScrollMessagesToEnd),
                    System.Windows.Threading.DispatcherPriority.Loaded);
            }
        }

        /// <summary>小组件停靠到工作区右下角(等布局完成拿到实际尺寸后再定位)。</summary>
        private void PositionMiniBottomRight()
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                var wa = SystemParameters.WorkArea;
                Left = wa.Right - ActualWidth - 16;
                Top = wa.Bottom - ActualHeight - 16;
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private void MiniAvatar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            _vm.IsMiniMode = false; // 点头像展开大窗
        }

        /// <summary>
        /// 滚到消息底部:虚拟化列表的 ScrollViewer 在模板内,首次找到后缓存。
        /// 统一推迟到 Loaded 优先级执行——切换会话时新列表尚未完成布局,立刻滚动会先按旧尺寸
        /// 滚一次、布局后再滚一次,白做一轮布局;推迟后只在新内容就位时滚一次。
        /// </summary>
        private void ScrollMessagesToEnd()
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _msgScroll ??= FindScrollViewer(MsgList);
                _msgScroll?.ScrollToEnd();
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private static ScrollViewer FindScrollViewer(DependencyObject root)
        {
            if (root == null) return null;
            if (root is ScrollViewer sv) return sv;
            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                var found = FindScrollViewer(VisualTreeHelper.GetChild(root, i));
                if (found != null) return found;
            }
            return null;
        }

        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
                DragMove();
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Hide();

        /// <summary>最小化=收起为右下角小组件(气泡清空,等待新播报)。</summary>
        private void Minimize_Click(object sender, RoutedEventArgs e) => _vm.EnterMiniMode();

        private void InputBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && _vm.SendCommand.CanExecute())
            {
                _vm.SendCommand.Execute();
                e.Handled = true;
            }
        }
    }
}

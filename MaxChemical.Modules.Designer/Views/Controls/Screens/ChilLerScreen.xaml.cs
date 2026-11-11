using DevicePlugins.Devices;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;

namespace MaxChemical.Modules.Designer.Views.Controls.Screens
{
    public partial class ChillerScreen : DeviceScreenBase
    {
        protected override string PollCommandName => "获取所有数据";

        private readonly ObservableCollection<string> _faults = new();

        private static readonly Brush OkBrush = new SolidColorBrush(Color.FromRgb(0x2F, 0xB8, 0x4D));
        private static readonly Brush WaitBrush = new SolidColorBrush(Color.FromRgb(0x6A, 0x6A, 0x74));
        private static readonly Brush AlarmBrush = new SolidColorBrush(Color.FromRgb(0xE0, 0x4B, 0x5A));
        private static readonly Brush OffText = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xEE));

        static ChillerScreen()
        {
            OkBrush.Freeze();
            WaitBrush.Freeze();
            AlarmBrush.Freeze();
            OffText.Freeze();
        }

        public ChillerScreen()
        {
            InitializeComponent();
            PollIntervalMs = 500;
        }

        // ══════════════════════ 数据回填 ══════════════════════

        protected override void OnDataRefreshed(DeviceParameters o)
        {
            bool ok = o.GetNullableValue<bool>("Success") ?? false;
            SetLink(ok);
            if (!ok) { Blank(); return; }

            // 更新 PV1 和 PV2 面板
            LiquidTempPv.Text = Fmt(o, "LIQUID_TEMP", "F1");
            SetTempValue.Text = Fmt(o, "SET_TEMP", "F1") + " ℃"; // 隐藏区域
            AntiFreezeTemp.Text = Fmt(o, "ANTI_FREEZE_TEMP", "F1");
            ////继电器状态
            //AntiFreezeTemp.Text = Fmt(o, "RELAY_STATUS", "F1");
            // 回填到 SV 输入框 (避免用户正在输入时被覆盖)
            if (!SetTempBox.IsKeyboardFocusWithin)
            {
                SetTempBox.Text = Fmt(o, "SET_TEMP", "F1");
            }

            //Comp1CurrentPv.Text = Fmt(o, "COMP1_CURRENT", "F1");
            //Comp2CurrentPv.Text = Fmt(o, "COMP2_CURRENT", "F1");
            //ChilledPumpCurrentPv.Text = Fmt(o, "CHILLED_PUMP_CURRENT", "F1");
            //CoolingPumpCurrentPv.Text = Fmt(o, "COOLING_PUMP_CURRENT", "F1");

            RunStateText.Text = o.GetValue<string>("RUN_STATE_TEXT") ?? "--";
        }

        protected override void OnPollError(Exception ex) => SetLink(false);

        private void SetLink(bool ok)
        {
            LinkDot.Fill = ok ? OkBrush : WaitBrush;
            LinkText.Text = ok ? "已连接" : "等待数据";
        }

        private void Blank()
        {
            LiquidTempPv.Text = "--";
            SetTempValue.Text = "--";
            AntiFreezeTemp.Text = "--";
            Comp1CurrentPv.Text = "--";
            Comp2CurrentPv.Text = "--";
            ChilledPumpCurrentPv.Text = "--";
            CoolingPumpCurrentPv.Text = "--";
            RunStateText.Text = "--";
            InputStatusText.Text = "--";
            RelayStatusText.Text = "--";
            Comp1RuntimeText.Text = "--";
            Comp2RuntimeText.Text = "--";
            AlarmChip.Visibility = Visibility.Collapsed;
            _faults.Clear();

            if (!SetTempBox.IsKeyboardFocusWithin)
            {
                SetTempBox.Text = "--";
            }
        }

        // ══════════════════════ 操作 ══════════════════════

        private async void PowerOnBtn_Click(object sender, RoutedEventArgs e)
        {
             Show("正在下发开机…");
             Show(await ExecuteCommandAsync("开机") ? "开机指令已下发" : "开机指令下发失败");
             RunStateText2.Text = "正在运行";
        }

        private async void PowerOffBtn_Click(object sender, RoutedEventArgs e)
        {
            Show("正在下发关机…");
            Show(await ExecuteCommandAsync("关机") ? "关机指令已下发" : "关机指令下发失败");
            RunStateText2.Text = "OFF";
        }

        private async void CompAllowBtn_Click(object sender, RoutedEventArgs e)
        {
            Show(await ExecuteCommandAsync("压缩机允许") ? "已允许压缩机" : "压缩机允许失败");
        }

        private async void CompForbidBtn_Click(object sender, RoutedEventArgs e)
        {
            //Say(await ExecuteCommandAsync("压缩机禁止") ? "已禁止压缩机" : "压缩机禁止失败");
            Show(await ExecuteCommandAsync("压缩机禁止") ? "已禁止压缩机" : "压缩机禁止失败");
        }

        //private async void MuteBtn_Click(object sender, RoutedEventArgs e)
        //{
        //    Say(await ExecuteCommandAsync("消音") ? "已消音" : "消音失败");
        //}

        //private async void ResetBtn_Click(object sender, RoutedEventArgs e)
        //{
        //    Say(await ExecuteCommandAsync("复位") ? "已复位" : "复位失败");
        //}

        private async void WriteParamBtn_Click(object sender, RoutedEventArgs e)
        {
            if (!TryNum(SetTempBox, out double temp))
            {
                //Say("设定温度填的不是数字");
                Show("设定温度填的不是数字");
                return;
            }

            //Say("正在下发常规参数…");
            Show("正在下发常规参数…");
           

            bool okTemp = await ExecuteCommandAsync("设置设定温度", new Dictionary<string, object>
            {
                ["Temp"] = temp,
            });

            //bool okMode = await ExecuteCommandAsync("设置起停方式", new Dictionary<string, object>
            //{
            //    ["TargetValue"] = (double)StartStopModeCombo.SelectedIndex,
            //});

            //bool okComp = await ExecuteCommandAsync("设置压缩机使用选择", new Dictionary<string, object>
            //{
            //    ["TargetValue"] = (double)(CompSelectCombo.SelectedIndex + 1),
            //});

            //bool okLang = await ExecuteCommandAsync("设置多语言", new Dictionary<string, object>
            //{
            //    ["TargetValue"] = (double)LanguageCombo.SelectedIndex,
            //});

            bool okAddr = false;

            bool allOk = okTemp && okAddr;
            //Say(allOk ? "常规参数已下发，等仪器回读确认" : "常规参数部分下发失败");
            Show(allOk ? "常规参数已下发，等仪器回读确认" : "常规参数部分下发失败");
        }

        // ══════════════════════ 小工具 ══════════════════════

        private void Say(string text)
        {
            //StatusText.Text = text;
            //StatusText.Foreground = OffText;
        }


        #region 提示框
        // 工业科技风主题色
        private static readonly Color BgColor = Color.FromRgb(16, 22, 28);       // 深黑蓝
        private static readonly Color BorderColor = Color.FromRgb(0, 200, 255);  // 科技蓝
        private static readonly Color TextColor = Color.FromRgb(220, 235, 245);  // 银白
        private static readonly Color SuccessColor = Color.FromRgb(0, 255, 170); // 成功绿
        private static readonly Color ErrorColor = Color.FromRgb(255, 68, 68);   // 错误红
        private static readonly Color WarnColor = Color.FromRgb(255, 184, 0);    // 警告黄

        public enum MsgType { Info, Success, Error, Warning }

        /// <summary>
        /// 阻塞式调用（等待用户点击关闭），返回 true
        /// </summary>
        public static bool Show(string message, string title = "提示", MsgType type = MsgType.Info)
        {
            Window win = CreateWindow(message, title, type, false);
            return win.ShowDialog() ?? true;
        }

        /// <summary>
        /// 非阻塞式调用（直接弹出，后台继续执行）
        /// </summary>
        /// <param name="autoCloseSeconds">自动关闭时间（秒），0 表示不自动关闭</param>
        public static void ShowAsync(string message, string title = "提示", MsgType type = MsgType.Info, double autoCloseSeconds = 0)
        {
            Window win = CreateWindow(message, title, type, true);

            if (autoCloseSeconds > 0)
            {
                var timer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(autoCloseSeconds)
                };
                timer.Tick += (s, e) =>
                {
                    timer.Stop();
                    if (win.IsVisible) win.Close();
                };
                timer.Start();
            }

            win.Show();
        }

        #region 核心 UI 构建逻辑

        private static Window CreateWindow(string message, string title, MsgType type, bool isAsync)
        {
            // 根据类型获取颜色和图标
            Color accentColor;
            string iconText;
            switch (type)
            {
                case MsgType.Success: accentColor = SuccessColor; iconText = "✔"; break;
                case MsgType.Error: accentColor = ErrorColor; iconText = "✖"; break;
                case MsgType.Warning: accentColor = WarnColor; iconText = "⚠"; break;
                default: accentColor = BorderColor; iconText = "i"; break;
            }

            var win = new Window
            {
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                SizeToContent = SizeToContent.WidthAndHeight,
                Background = Brushes.Transparent,
                AllowsTransparency = true,
                ShowInTaskbar = false,
                Topmost = true,
                Width = 420
            };

            // 最外层容器（带阴影）
            var shadow = new DropShadowEffect
            {
                Color = accentColor,
                BlurRadius = 20,
                ShadowDepth = 0,
                Opacity = 0.6
            };

            // 主体边框（带霓虹发光效果）
            var mainBorder = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(240, BgColor.R, BgColor.G, BgColor.B)),
                BorderBrush = new SolidColorBrush(accentColor),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(2),
                Effect = shadow,
                Margin = new Thickness(15)
            };

            // 主网格布局
            var mainGrid = new Grid();
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(36) });   // 标题栏
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });      // 内容区
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(42) });   // 底部按钮栏
            mainBorder.Child = mainGrid;

            // ---------- 1. 标题栏 ----------
            var titleGrid = new Grid { Background = new SolidColorBrush(Color.FromArgb(50, accentColor.R, accentColor.G, accentColor.B)) };
            titleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });  // 左侧装饰条
            titleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var accentBar = new Border { Background = new SolidColorBrush(accentColor) };
            Grid.SetColumn(accentBar, 0);
            titleGrid.Children.Add(accentBar);

            var titleStack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
            titleStack.Children.Add(new TextBlock
            {
                Text = "●",
                Foreground = new SolidColorBrush(accentColor),
                FontSize = 10,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0)
            });
            titleStack.Children.Add(new TextBlock
            {
                Text = title.ToUpper(),
                Foreground = new SolidColorBrush(TextColor),
                FontSize = 13,
                FontWeight = FontWeights.Bold,
                FontFamily = new FontFamily("Consolas, Microsoft YaHei"),
                VerticalAlignment = VerticalAlignment.Center
            });

            Grid.SetColumn(titleStack, 1);
            titleGrid.Children.Add(titleStack);

            Grid.SetRow(titleGrid, 0);
            mainGrid.Children.Add(titleGrid);

            // ---------- 2. 内容区 ----------
            var contentGrid = new Grid { Margin = new Thickness(20, 20, 20, 20) };
            contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(45) });
            contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // 左侧图标（带发光圆圈背景）
            var iconBorder = new Border
            {
                Width = 36,
                Height = 36,
                Background = new SolidColorBrush(Color.FromArgb(30, accentColor.R, accentColor.G, accentColor.B)),
                BorderBrush = new SolidColorBrush(accentColor),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(18),
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 2, 0, 0)
            };
            iconBorder.Child = new TextBlock
            {
                Text = iconText,
                Foreground = new SolidColorBrush(accentColor),
                FontSize = 18,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(iconBorder, 0);
            contentGrid.Children.Add(iconBorder);

            // 右侧消息文本
            var msgText = new TextBlock
            {
                Text = message,
                Foreground = new SolidColorBrush(TextColor),
                FontSize = 14,
                FontFamily = new FontFamily("Microsoft YaHei, Segoe UI"),
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
                LineHeight = 22,
                Margin = new Thickness(5, 0, 0, 0)
            };
            Grid.SetColumn(msgText, 1);
            contentGrid.Children.Add(msgText);

            Grid.SetRow(contentGrid, 1);
            mainGrid.Children.Add(contentGrid);

            // ---------- 3. 底部按钮栏 ----------
            var bottomGrid = new Grid { Margin = new Thickness(0, 0, 15, 12) };
            bottomGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // 占位
            bottomGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                     // 按钮

            // 分割线
            //var divider = new Rectangle
            //{
            //    Height = 1,
            //    Fill = new SolidColorBrush(Color.FromArgb(60, accentColor.R, accentColor.G, accentColor.B)),
            //    VerticalAlignment = VerticalAlignment.Top,
            //    Margin = new Thickness(0, 0, 0, 0)
            //};
            //Grid.SetColumnSpan(divider, 2);
            //bottomGrid.Children.Add(divider);

            // 按钮
            var btn = new Button
            {
                Content = "确认",
                Foreground = new SolidColorBrush(accentColor),
                Background = new SolidColorBrush(Color.FromArgb(40, accentColor.R, accentColor.G, accentColor.B)),
                BorderBrush = new SolidColorBrush(accentColor),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(20, 4, 20, 4),
                FontSize = 12,
                FontFamily = new FontFamily("Consolas, Microsoft YaHei"),
                FontWeight = FontWeights.Bold,
                Cursor = System.Windows.Input.Cursors.Hand,
                Margin = new Thickness(0, 10, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Right
            };

            // 按钮悬浮效果
            btn.MouseEnter += (s, e) => btn.Background = new SolidColorBrush(Color.FromArgb(80, accentColor.R, accentColor.G, accentColor.B));
            btn.MouseLeave += (s, e) => btn.Background = new SolidColorBrush(Color.FromArgb(40, accentColor.R, accentColor.G, accentColor.B));

            btn.Click += (s, e) =>
            {
                if (!isAsync) win.DialogResult = true;
                win.Close();
            };

            Grid.SetColumn(btn, 1);
            bottomGrid.Children.Add(btn);

            Grid.SetRow(bottomGrid, 2);
            mainGrid.Children.Add(bottomGrid);

            // ---------- 4. 窗口加载动画 ----------
            win.Opacity = 0;
            win.Loaded += (s, e) =>
            {
                var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200));
                win.BeginAnimation(Window.OpacityProperty, fadeIn);
            };

            // 支持鼠标拖动窗口
            mainBorder.MouseLeftButtonDown += (s, e) => win.DragMove();

            win.Content = mainBorder;
            return win;
        }

        #endregion




        #endregion



        private static bool TryNum(TextBox box, out double value) =>
            double.TryParse(box.Text?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);

        private static string Fmt(DeviceParameters o, string key, string format)
        {
            var v = o.GetNullableValue<double>(key);
            return v.HasValue ? v.Value.ToString(format, CultureInfo.InvariantCulture) : "--";
        }

        private void SelectAllOnFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            if (sender is TextBox tb) tb.SelectAll();
        }
    }
}
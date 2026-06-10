using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using MaxChemical.Modules.GatewayConfig.Models;
using MaxChemical.Modules.GatewayConfig.ViewModels;
using Prism.Events;
using Prism.Ioc;

namespace MaxChemical.Modules.GatewayConfig.Views
{
    public partial class GatewayBodyControl : UserControl
    {
        public static readonly DependencyProperty GatewayProperty =
            DependencyProperty.Register(nameof(Gateway), typeof(GatewayModel),
                typeof(GatewayBodyControl), new PropertyMetadata(null, OnGatewayChanged));

        public GatewayModel Gateway
        {
            get => (GatewayModel)GetValue(GatewayProperty);
            set => SetValue(GatewayProperty, value);
        }

        private readonly IEventAggregator _eventAggregator;

        // ─── 颜色 ───
        private static readonly SolidColorBrush BlackBrush = (SolidColorBrush)
            new BrushConverter().ConvertFromString("#1A1A1A");
        private static readonly SolidColorBrush DisabledBrush = (SolidColorBrush)
            new BrushConverter().ConvertFromString("#B4B2A9");
        private static readonly SolidColorBrush BrandRedBrush = (SolidColorBrush)
            new BrushConverter().ConvertFromString("#A41626");
        private static readonly SolidColorBrush WarnBrush = (SolidColorBrush)
            new BrushConverter().ConvertFromString("#D29C36");             // 警告黄
        public GatewayBodyControl()
        {
            InitializeComponent();
            _eventAggregator = ContainerLocator.Container.Resolve<IEventAggregator>();
        }

        private static void OnGatewayChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is GatewayBodyControl ctl)
            {
                ctl.Rebuild();
            }
        }
       
        private void Rebuild()
        {
            if (Gateway == null) return;

            TbTitle.Text = string.Format("SERIAL GATEWAY · {0}-PORT", Gateway.Channels.Count);

            var db9s = new List<ContentControl>();
            var rs485s = new List<ContentControl>();
            int i = 0;
            foreach (var channel in Gateway.Channels)
            {
                ContentControl db9 = new ContentControl();
                BuildDb9(db9, channel, i);
                db9s.Add(db9);

                ContentControl rs485 = new ContentControl();
                BuildRs485(rs485, channel, i);
                rs485s.Add(rs485);
                i++;
            }

            DB9.ItemsSource = new ObservableCollection<ContentControl>(db9s);
            Rs485.ItemsSource = new ObservableCollection<ContentControl>(rs485s);
        }

        //private ContentControl GetDb9Slot(int i) => i switch
        //{
        //    0 => Db9_1,
        //    1 => Db9_2,
        //    2 => Db9_3,
        //    3 => Db9_4,
        //    _ => null
        //};
        //private ContentControl GetRs485Slot(int i) => i switch
        //{
        //    0 => Rs485_1,
        //    1 => Rs485_2,
        //    2 => Rs485_3,
        //    3 => Rs485_4,
        //    _ => null
        //};

        /// <summary>根据通道状态选颜色:已绑定→品牌红,可用→黑色,未启用→灰色虚线</summary>
        private static SolidColorBrush PickStroke(ChannelModel ch)
        {
            if (ch == null || !ch.IsAvailable) return DisabledBrush;
            if (!ch.IsBound) return BlackBrush;

            return ch.ConnectionState switch
            {
                ChannelConnectionState.BoundOnline => BrandRedBrush,
                ChannelConnectionState.BoundOffline => WarnBrush,
                _ => BlackBrush
            };
        }
        private void BuildDb9(ContentControl slot, ChannelModel ch, int idx)
        {
            if (slot == null) return;
            var available = ch != null && ch.IsAvailable;
            var bound = ch != null && ch.IsBound;
            var stroke = PickStroke(ch);
            var fill = stroke;
            var dash = available ? null : new DoubleCollection { 2, 2 };

            // 多行标签时高度变大
            double canvasH = bound ? 80 : 64;
            var canvas = new Canvas { Width = 76, Height = canvasH };

            // DB9 梯形外框
            var trap = new Polygon
            {
                Points = new PointCollection
                {
                    new Point(6, 8), new Point(70, 8), new Point(66, 40), new Point(10, 40)
                },
                Stroke = stroke,
                StrokeThickness = bound ? 1.5 : 1,
                Fill = Brushes.Transparent,
            };
            if (dash != null) trap.StrokeDashArray = dash;
            canvas.Children.Add(trap);

            // 螺丝盒
            var screwBox = new Rectangle
            {
                Width = 76,
                Height = 36,
                Stroke = stroke,
                StrokeThickness = bound ? 1.5 : 1,
                Fill = Brushes.Transparent,
            };
            if (dash != null) screwBox.StrokeDashArray = dash;
            Canvas.SetLeft(screwBox, 0); Canvas.SetTop(screwBox, 6);
            canvas.Children.Add(screwBox);

            // 针脚 / 未启用标签
            if (available)
            {
                int[] xs1 = { 14, 22, 30, 38, 46, 54, 62 };
                int[] xs2 = { 18, 26, 34, 42, 50, 58 };
                int[] xs3 = { 22, 30, 38, 46, 54 };
                foreach (var x in xs1) AddPin(canvas, x, 16, fill);
                foreach (var x in xs2) AddPin(canvas, x, 25, fill);
                foreach (var x in xs3) AddPin(canvas, x, 34, fill);
            }
            else
            {
                var label = new TextBlock
                {
                    Text = "未启用",
                    FontFamily = new FontFamily("Arial"),
                    FontSize = 8,
                    Foreground = DisabledBrush,
                };
                Canvas.SetLeft(label, 24); Canvas.SetTop(label, 22);
                canvas.Children.Add(label);
            }

            // 通道标签
            var tag = new TextBlock
            {
                Text = $"RS-232 ({idx + 1})",
                FontFamily = new FontFamily("Arial"),
                FontSize = 8,
                FontWeight = FontWeights.Medium,
                Foreground = stroke,
            };
            Canvas.SetLeft(tag, 16); Canvas.SetTop(tag, 50);
            canvas.Children.Add(tag);

            // ★ 绑定的设备名 (两行:名 + ID)
            if (bound)
            {
                var deviceName = new TextBlock
                {
                    Text = ch.BoundDeviceName,
                    FontFamily = new FontFamily("Microsoft YaHei"),
                    FontSize = 9,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = BrandRedBrush,
                    Width = 76,
                    TextAlignment = TextAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                };
                Canvas.SetLeft(deviceName, 0); Canvas.SetTop(deviceName, 63);
                canvas.Children.Add(deviceName);
                // ★ 已绑定但未连通时,加个小标识
                if (ch.ConnectionState == ChannelConnectionState.BoundOffline)
                {
                    var offlineDot = new Ellipse
                    {
                        Width = 6,
                        Height = 6,
                        Fill = WarnBrush,
                        Stroke = Brushes.White,
                        StrokeThickness = 1,
                    };
                    Canvas.SetLeft(offlineDot, 60); Canvas.SetTop(offlineDot, 2);
                    canvas.Children.Add(offlineDot);
                }
                if (!string.IsNullOrEmpty(ch.BoundDeviceInstanceId))
                {
                    var deviceId = new TextBlock
                    {
                        Text = ch.BoundDeviceInstanceId,
                        FontFamily = new FontFamily("Consolas"),
                        FontSize = 8,
                        Foreground = BrandRedBrush,
                        Width = 76,
                        TextAlignment = TextAlignment.Center,
                    };
                    Canvas.SetLeft(deviceId, 0); Canvas.SetTop(deviceId, 73);
                    canvas.Children.Add(deviceId);
                }
            }

            // 点击热区
            var btn = new Button
            {
                Content = canvas,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                IsEnabled = available,
                Cursor = available ? Cursors.Hand : Cursors.Arrow,
                Padding = new Thickness(0),
                Tag = ch,
            };
            btn.Template = MakeHotZoneTemplate();
            btn.Click += OnChannelClick;
            slot.Content = btn;
        }

        private void BuildRs485(ContentControl slot, ChannelModel ch, int idx)
        {
            if (slot == null) return;
            var available = ch != null && ch.IsAvailable;
            var bound = ch != null && ch.IsBound;
            var stroke = PickStroke(ch);
            var dash = available ? null : new DoubleCollection { 2, 2 };

            double canvasH = bound ? 56 : 36;
            var canvas = new Canvas { Width = 260, Height = canvasH };

            var box = new Rectangle
            {
                Width = 260,
                Height = 22,
                Stroke = stroke,
                StrokeThickness = bound ? 1.2 : 0.8,
                Fill = Brushes.Transparent,
            };
            if (dash != null) box.StrokeDashArray = dash;
            Canvas.SetLeft(box, 0); Canvas.SetTop(box, 14);
            canvas.Children.Add(box);

            if (available)
            {
                int[] xs = { 20, 55, 90, 125, 160, 195, 225 };
                foreach (var x in xs)
                {
                    var c = new Ellipse
                    {
                        Width = 7,
                        Height = 7,
                        Stroke = stroke,
                        StrokeThickness = 0.5,
                        Fill = Brushes.Transparent,
                    };
                    Canvas.SetLeft(c, x - 3.5); Canvas.SetTop(c, 21.5);
                    canvas.Children.Add(c);
                }
            }

            var tag = new TextBlock
            {
                Text = $"RS-485 / RS-422 ({idx + 1})",
                FontFamily = new FontFamily("Arial"),
                FontSize = 7,
                FontWeight = FontWeights.Medium,
                Foreground = stroke,
            };
            Canvas.SetLeft(tag, 0); Canvas.SetTop(tag, 3);
            canvas.Children.Add(tag);

            // ★ 绑定的设备名
            if (bound)
            {
                var deviceText = new TextBlock
                {
                    Text = string.IsNullOrEmpty(ch.BoundDeviceInstanceId)
                        ? ch.BoundDeviceName
                        : $"{ch.BoundDeviceName} · {ch.BoundDeviceInstanceId}",
                    FontFamily = new FontFamily("Microsoft YaHei"),
                    FontSize = 9,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = BrandRedBrush,
                };
                Canvas.SetLeft(deviceText, 0); Canvas.SetTop(deviceText, 40);
                canvas.Children.Add(deviceText);
            }

            var btn = new Button
            {
                Content = canvas,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                IsEnabled = available,
                Cursor = available ? Cursors.Hand : Cursors.Arrow,
                Padding = new Thickness(0),
                Tag = ch,
            };
            btn.Template = MakeHotZoneTemplate();
            btn.Click += OnChannelClick;
            slot.Content = btn;
        }

        private static void AddPin(Canvas canvas, double x, double y, SolidColorBrush fill)
        {
            var e = new Ellipse { Width = 2.8, Height = 2.8, Fill = fill };
            Canvas.SetLeft(e, x - 1.4); Canvas.SetTop(e, y - 1.4);
            canvas.Children.Add(e);
        }

        private static ControlTemplate MakeHotZoneTemplate()
        {
            var t = new ControlTemplate(typeof(Button));
            var grid = new FrameworkElementFactory(typeof(Grid));
            grid.SetBinding(Grid.BackgroundProperty,
                new System.Windows.Data.Binding("Background")
                { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            grid.AppendChild(presenter);
            t.VisualTree = grid;

            var trigger = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            trigger.Setters.Add(new Setter(Control.BackgroundProperty,
                new SolidColorBrush(Color.FromArgb(0x14, 0xA4, 0x16, 0x26))));
            t.Triggers.Add(trigger);
            return t;
        }

        private void OnChannelClick(object sender, RoutedEventArgs e)
        {
            if (sender is Button b && b.Tag is ChannelModel ch && Gateway != null)
            {
                _eventAggregator.GetEvent<OpenChannelDialogEvent>().Publish((Gateway, ch));
            }
        }
    }
}
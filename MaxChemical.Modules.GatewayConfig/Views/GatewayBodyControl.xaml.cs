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
        private static readonly SolidColorBrush LineBrush = (SolidColorBrush)
            new BrushConverter().ConvertFromString("#1A1A1A");
        private static readonly SolidColorBrush DisabledBrush = (SolidColorBrush)
            new BrushConverter().ConvertFromString("#B4B2A9");

        public GatewayBodyControl()
        {
            InitializeComponent();
            _eventAggregator = ContainerLocator.Container.Resolve<IEventAggregator>();
        }

        private static void OnGatewayChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is GatewayBodyControl ctl) ctl.Rebuild();
        }

        private void Rebuild()
        {
            if (Gateway == null) return;
            for (int i = 0; i < 4; i++)
            {
                var ch = i < Gateway.Channels.Count ? Gateway.Channels[i] : null;
                BuildDb9(GetDb9Slot(i), ch, i);
                BuildRs485(GetRs485Slot(i), ch, i);
            }
        }

        private ContentControl GetDb9Slot(int i) => i switch
        {
            0 => Db9_1, 1 => Db9_2, 2 => Db9_3, 3 => Db9_4, _ => null
        };
        private ContentControl GetRs485Slot(int i) => i switch
        {
            0 => Rs485_1, 1 => Rs485_2, 2 => Rs485_3, 3 => Rs485_4, _ => null
        };

        private void BuildDb9(ContentControl slot, ChannelModel ch, int idx)
        {
            if (slot == null) return;
            var canvas = new Canvas { Width = 76, Height = 64 };
            var available = ch != null && ch.IsAvailable;
            var stroke = available ? LineBrush : DisabledBrush;
            var fill   = available ? LineBrush : DisabledBrush;
            var dash   = available ? null : new DoubleCollection { 2, 2 };

            var trap = new Polygon
            {
                Points = new PointCollection
                {
                    new Point(6, 8), new Point(70, 8), new Point(66, 40), new Point(10, 40)
                },
                Stroke = stroke, StrokeThickness = 1, Fill = Brushes.Transparent,
            };
            if (dash != null) trap.StrokeDashArray = dash;
            canvas.Children.Add(trap);

            var screwBox = new Rectangle
            {
                Width = 76, Height = 36, Stroke = stroke, StrokeThickness = 1, Fill = Brushes.Transparent,
            };
            if (dash != null) screwBox.StrokeDashArray = dash;
            Canvas.SetLeft(screwBox, 0); Canvas.SetTop(screwBox, 6);
            canvas.Children.Add(screwBox);

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
                    Text = "未启用", FontFamily = new FontFamily("Arial"),
                    FontSize = 8, Foreground = DisabledBrush,
                };
                Canvas.SetLeft(label, 24); Canvas.SetTop(label, 22);
                canvas.Children.Add(label);
            }

            var tag = new TextBlock
            {
                Text = $"RS-232 ({idx + 1})",
                FontFamily = new FontFamily("Arial"), FontSize = 8,
                FontWeight = FontWeights.Medium, Foreground = available ? LineBrush : DisabledBrush,
            };
            Canvas.SetLeft(tag, 16); Canvas.SetTop(tag, 50);
            canvas.Children.Add(tag);

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
            var canvas = new Canvas { Width = 260, Height = 36 };
            var available = ch != null && ch.IsAvailable;
            var stroke = available ? LineBrush : DisabledBrush;
            var dash = available ? null : new DoubleCollection { 2, 2 };

            var box = new Rectangle
            {
                Width = 260, Height = 22, Stroke = stroke, StrokeThickness = 0.8, Fill = Brushes.Transparent,
            };
            if (dash != null) box.StrokeDashArray = dash;
            Canvas.SetLeft(box, 0); Canvas.SetTop(box, 6);
            canvas.Children.Add(box);

            if (available)
            {
                int[] xs = { 20, 55, 90, 125, 160, 195, 225 };
                foreach (var x in xs)
                {
                    var c = new Ellipse
                    {
                        Width = 7, Height = 7, Stroke = stroke, StrokeThickness = 0.5, Fill = Brushes.Transparent,
                    };
                    Canvas.SetLeft(c, x - 3.5); Canvas.SetTop(c, 13.5);
                    canvas.Children.Add(c);
                }
            }

            var tag = new TextBlock
            {
                Text = $"RS-485 / RS-422 ({idx + 1})",
                FontFamily = new FontFamily("Arial"), FontSize = 7,
                FontWeight = FontWeights.Medium, Foreground = available ? LineBrush : DisabledBrush,
            };
            Canvas.SetLeft(tag, 0); Canvas.SetTop(tag, -8);
            canvas.Children.Add(tag);

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

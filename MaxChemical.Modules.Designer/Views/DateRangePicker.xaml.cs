using MaxChemical.Infrastructure.Events;
using MaxChemical.Infrastructure.Services;
using Prism.Events;
using Prism.Ioc;
using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace MaxChemical.Modules.Designer.Views
{
    public partial class DateRangePicker : UserControl
    {
        // ═══ DependencyProperties ═══

        public static readonly DependencyProperty StartDateProperty =
            DependencyProperty.Register(nameof(StartDate), typeof(DateTime?), typeof(DateRangePicker),
                new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnDateChanged));

        public static readonly DependencyProperty EndDateProperty =
            DependencyProperty.Register(nameof(EndDate), typeof(DateTime?), typeof(DateRangePicker),
                new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnDateChanged));

        public static readonly RoutedEvent DateRangeChangedEvent =
            EventManager.RegisterRoutedEvent(nameof(DateRangeChanged), RoutingStrategy.Bubble,
                typeof(RoutedEventHandler), typeof(DateRangePicker));

        public event RoutedEventHandler DateRangeChanged
        {
            add => AddHandler(DateRangeChangedEvent, value);
            remove => RemoveHandler(DateRangeChangedEvent, value);
        }

        public DateTime? StartDate
        {
            get => (DateTime?)GetValue(StartDateProperty);
            set => SetValue(StartDateProperty, value);
        }

        public DateTime? EndDate
        {
            get => (DateTime?)GetValue(EndDateProperty);
            set => SetValue(EndDateProperty, value);
        }

        // ═══ 内部状态 ═══

        private DateTime _displayMonth;
        private DateTime? _rangeStart;  // 第一次点击的日期
        private bool _isSelectingEnd;   // 是否在选择结束日期
        private int _activeQuick = 30;  // 当前快捷天数

        // ═══ 颜色常量 ═══
        private static readonly SolidColorBrush IndigoBg = new(Color.FromRgb(0x2D, 0x3A, 0x8C));
        private static readonly SolidColorBrush IndigoLight = new(Color.FromRgb(0xE8, 0xEA, 0xF6));
        private static readonly SolidColorBrush IndigoText = new(Color.FromRgb(0x2D, 0x3A, 0x8C));
        private static readonly SolidColorBrush WhiteBrush = Brushes.White;
        private static readonly SolidColorBrush GrayText = new(Color.FromRgb(0xD1, 0xD5, 0xDB));
        private static readonly SolidColorBrush NormalText = new(Color.FromRgb(0x37, 0x41, 0x51));
        private static readonly SolidColorBrush TransparentBrush = Brushes.Transparent;

        //private readonly IEventAggregator _eventAggregator;
        private readonly ILocalizationService _localizationService;

        //private readonly Dictionary<int, string> _monthMap;

        public DateRangePicker()
        {
            InitializeComponent();

            //_eventAggregator = ContainerLocator.Container.Resolve<IEventAggregator>() ?? throw new NullReferenceException();
            _localizationService = ContainerLocator.Container.Resolve<ILocalizationService>() ?? throw new NullReferenceException();

            //_eventAggregator.GetEvent<LanguageChangedEvent>()?.Subscribe(UpdateLocalizationText);

            //_monthMap = new Dictionary<int, string>()
            //{
            //    {1,"January"},
            //    {2,"February" },
            //    {3,"March" },
            //    {4,"April"},
            //    {5,"May" },
            //    {6,"June" },
            //    {7,"July"},
            //    {8,"August" },
            //    {9,"September" },
            //    {10,"October"},
            //    {11,"November" },
            //    {12,"December" }
            //};

            _displayMonth = DateTime.Now.Date;
            _displayMonth = new DateTime(_displayMonth.Year, _displayMonth.Month, 1);
            UpdateTriggerText();
            UpdateLocalizationText("");
        }

        // ═══ 属性变更 ═══

        private static void OnDateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is DateRangePicker picker)
                picker.UpdateTriggerText();
        }

        private void UpdateTriggerText()
        {
            if (StartDateText == null || EndDateText == null) return;
            StartDateText.Text = StartDate?.ToString("MM-dd") ?? "--";
            EndDateText.Text = EndDate?.ToString("MM-dd") ?? "--";
        }

        // ═══ 触发弹出 ═══

        private DateTime _lastClosedTime = DateTime.MinValue;

        private void Trigger_Click(object sender, MouseButtonEventArgs e)
        {
            // 防抖: 如果 Popup 刚关闭（<300ms），不要立即重新打开
            // 这解决了 StaysOpen=False 导致的 "点击触发器 → Popup关闭 → 又立即打开" 问题
            if ((DateTime.Now - _lastClosedTime).TotalMilliseconds < 300)
                return;

            if (CalendarPopup.IsOpen)
            {
                CalendarPopup.IsOpen = false;
                return;
            }

            if (StartDate.HasValue)
                _displayMonth = new DateTime(StartDate.Value.Year, StartDate.Value.Month, 1);
            else
                _displayMonth = new DateTime(DateTime.Now.Year, DateTime.Now.Month, 1);

            _rangeStart = StartDate;
            _isSelectingEnd = false;
            RenderCalendar();
            CalendarPopup.IsOpen = true;

            // 高亮触发框
            TriggerBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(0x2D, 0x3A, 0x8C));
            e.Handled = true;
        }

        private void Popup_Closed(object sender, EventArgs e)
        {
            _lastClosedTime = DateTime.Now;
            TriggerBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(0xE2, 0xE5, 0xEB));
        }

        // ═══ 月份导航 ═══

        private void PrevMonth_Click(object sender, RoutedEventArgs e)
        {
            _displayMonth = _displayMonth.AddMonths(-1);
            RenderCalendar();
        }

        private void NextMonth_Click(object sender, RoutedEventArgs e)
        {
            _displayMonth = _displayMonth.AddMonths(1);
            RenderCalendar();
        }

        // ═══ 快捷按钮 ═══

        private void Quick7_Click(object sender, RoutedEventArgs e) => ApplyQuick(7);
        private void Quick30_Click(object sender, RoutedEventArgs e) => ApplyQuick(30);
        private void Quick90_Click(object sender, RoutedEventArgs e) => ApplyQuick(90);

        private void ApplyQuick(int days)
        {
            _activeQuick = days;
            StartDate = DateTime.Now.Date.AddDays(-days);
            EndDate = DateTime.Now.Date.AddDays(1);
            _rangeStart = StartDate;
            _isSelectingEnd = false;
            _displayMonth = new DateTime(EndDate.Value.Year, EndDate.Value.Month, 1);
            UpdateQuickBtnStyles();
            RenderCalendar();
            RaiseEvent(new RoutedEventArgs(DateRangeChangedEvent));
        }

        private void UpdateQuickBtnStyles()
        {
            if (Btn7 == null) return;
            Btn7.Style = _activeQuick == 7
                ? (Style)FindResource("QuickBtnActive")
                : (Style)FindResource("QuickBtn");
            Btn30.Style = _activeQuick == 30
                ? (Style)FindResource("QuickBtnActive")
                : (Style)FindResource("QuickBtn");
            Btn90.Style = _activeQuick == 90
                ? (Style)FindResource("QuickBtnActive")
                : (Style)FindResource("QuickBtn");
        }

        private void Clear_Click(object sender, RoutedEventArgs e)
        {
            StartDate = null;
            EndDate = null;
            _rangeStart = null;
            _isSelectingEnd = false;
            _activeQuick = 0;
            UpdateQuickBtnStyles();
            RenderCalendar();
            RaiseEvent(new RoutedEventArgs(DateRangeChangedEvent));
        }

        private void Confirm_Click(object sender, RoutedEventArgs e)
        {
            CalendarPopup.IsOpen = false;
            RaiseEvent(new RoutedEventArgs(DateRangeChangedEvent));
        }

        // ═══ 渲染日历 ═══

        private void RenderCalendar()
        {
            if (DayGrid == null || MonthYearText == null) return;

            if (CultureInfo.CurrentUICulture.Name == "en-US")
            {
                MonthYearText.Text = string.Format("{0}  {1}", _localizationService.GetString($"Month_{_displayMonth.Month}"), _displayMonth.Year);
            }
            else
            {
                MonthYearText.Text = $"{_displayMonth.Year} 年 {_displayMonth.Month} 月";
            }

            DayGrid.Children.Clear();

            var firstDay = _displayMonth;
            int startDow = (int)firstDay.DayOfWeek; // 0=Sun
            int daysInMonth = DateTime.DaysInMonth(firstDay.Year, firstDay.Month);

            // 上月填充
            var prevMonth = firstDay.AddMonths(-1);
            int prevDays = DateTime.DaysInMonth(prevMonth.Year, prevMonth.Month);
            for (int i = startDow - 1; i >= 0; i--)
            {
                var date = new DateTime(prevMonth.Year, prevMonth.Month, prevDays - i);
                AddDayButton(date, isCurrentMonth: false);
            }

            // 当月
            for (int d = 1; d <= daysInMonth; d++)
            {
                var date = new DateTime(firstDay.Year, firstDay.Month, d);
                AddDayButton(date, isCurrentMonth: true);
            }

            // 下月填充（补满 6 行 = 42 格）
            int totalCells = DayGrid.Children.Count;
            int remaining = 42 - totalCells;
            var nextMonth = firstDay.AddMonths(1);
            for (int d = 1; d <= remaining; d++)
            {
                var date = new DateTime(nextMonth.Year, nextMonth.Month, d);
                AddDayButton(date, isCurrentMonth: false);
            }
        }

        private void AddDayButton(DateTime date, bool isCurrentMonth)
        {
            bool isStart = StartDate.HasValue && date.Date == StartDate.Value.Date;
            bool isEnd = EndDate.HasValue && date.Date == EndDate.Value.Date;
            bool inRange = StartDate.HasValue && EndDate.HasValue
                           && date.Date > StartDate.Value.Date && date.Date < EndDate.Value.Date;

            var textBlock = new TextBlock
            {
                Text = date.Day.ToString(),
                FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = NormalText,
            };

            var border = new Border
            {
                Margin = new Thickness(2),
                Background = TransparentBrush,
                CornerRadius = new CornerRadius(6),
                Cursor = Cursors.Hand,
                Child = textBlock,
                Height = 36,
            };

            if (isStart || isEnd)
            {
                border.Background = IndigoBg;
                textBlock.Foreground = WhiteBrush;
                textBlock.FontWeight = FontWeights.Medium;
            }
            else if (inRange)
            {
                border.Background = IndigoLight;
                textBlock.Foreground = IndigoText;
            }
            else if (!isCurrentMonth)
            {
                textBlock.Foreground = GrayText;
            }

            if (date.Date == DateTime.Today && !isStart && !isEnd && !inRange && isCurrentMonth)
            {
                textBlock.Foreground = IndigoText;
                textBlock.FontWeight = FontWeights.Medium;
            }

            border.MouseLeftButtonUp += (s, e) =>
            {
                OnDayClicked(date);
                e.Handled = true;
            };

            border.MouseEnter += (s, e) =>
            {
                if (!isStart && !isEnd && !inRange)
                    border.Background = new SolidColorBrush(Color.FromRgb(0xF0, 0xF1, 0xF4));
            };
            border.MouseLeave += (s, e) =>
            {
                if (!isStart && !isEnd && !inRange)
                    border.Background = TransparentBrush;
            };

            DayGrid.Children.Add(border);
        }

        private void OnDayClicked(DateTime date)
        {
            if (!_isSelectingEnd)
            {
                // 第一次点击：设起点
                _rangeStart = date;
                StartDate = date;
                EndDate = date; // 暂时设为同一天
                _isSelectingEnd = true;
                _activeQuick = 0;
                UpdateQuickBtnStyles();
            }
            else
            {
                // 第二次点击：设终点
                if (date < _rangeStart)
                {
                    // 点了更早的日期，交换
                    StartDate = date;
                    EndDate = _rangeStart;
                }
                else
                {
                    EndDate = date;
                }
                _isSelectingEnd = false;
                _activeQuick = 0;
                UpdateQuickBtnStyles();
            }

            RenderCalendar();
        }


        private void UpdateLocalizationText(string s)
        {
            SundayLabel.Text = _localizationService.GetString("Week_Sunday", "日");
            MonDayLabel.Text= _localizationService.GetString("Week_Monday", "一");
            TuesdayLabel.Text= _localizationService.GetString("Week_Tuesday", "二");
            WednesdayLabel.Text= _localizationService.GetString("Week_Wednesday", "三");
            ThursdayLabel.Text= _localizationService.GetString("Week_Thursday", "四");
            FridayLabel.Text= _localizationService.GetString("Week_Friday", "五");
            SaturdayLabel.Text= _localizationService.GetString("Week_Saturday", "六");

            Btn7.Content = _localizationService.GetString("DatePicker_7", "7 天");
            Btn30.Content= _localizationService.GetString("DatePicker_30", "30 天");
            Btn90.Content= _localizationService.GetString("DatePicker_90", "90 天");
            BtnClear.Content= _localizationService.GetString("DatePicker_Clear", "清除");
            BtnConfirm.Content = _localizationService.GetString("DatePicker_Confirm", "确定");
        }
    }
}
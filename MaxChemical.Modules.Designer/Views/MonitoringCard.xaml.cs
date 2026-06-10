using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using DevicePlugins.Devices;
using MaxChemical.Infrastructure.Services;
using Prism.Ioc;

namespace MaxChemical.Modules.Designer.Views
{
    public partial class MonitoringCard : UserControl, INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        public event EventHandler CloseRequested;

        private ObservableCollection<MonitorParameter> _parameters;
        private string _deviceId;
        private string _deviceName;
        private MonitorParameter _currentParameter;

        // 拖动相关
        private bool _isDragging = false;
        private Point _dragStartPoint;
        private Point _dragCurrentPoint;

        public ObservableCollection<MonitorParameter> Parameters
        {
            get => _parameters;
            set
            {
                _parameters = value;
                OnPropertyChanged();
                ParametersItemsControl.ItemsSource = _parameters;
            }
        }

        public string DeviceId
        {
            get => _deviceId;
            set
            {
                _deviceId = value;
                OnPropertyChanged();
            }
        }

        public string DeviceName
        {
            get => _deviceName;
            set
            {
                _deviceName = value;
                OnPropertyChanged();
                // 更新拖动手柄上的设备名称
                if (DeviceNameText != null)
                {
                    DeviceNameText.Text = value;
                }
            }
        }

        public MonitoringCard()
        {
            InitializeComponent();
            Parameters = new ObservableCollection<MonitorParameter>();

            // ★ 新增:Popup 打开时,监听窗口全局点击,点外面就关闭
            DetailPopup.Opened += (s, e) =>
            {
                var window = Window.GetWindow(this);
                if (window != null)
                {
                    window.PreviewMouseDown += OnWindowMouseDownForPopup;
                }
            };

            DetailPopup.Closed += (s, e) =>
            {
                var window = Window.GetWindow(this);
                if (window != null)
                {
                    window.PreviewMouseDown -= OnWindowMouseDownForPopup;
                }
            };
        }

        // ★ 新增方法
        private void OnWindowMouseDownForPopup(object sender, MouseButtonEventArgs e)
        {
            // 检查点击是否在 Popup 内部
            if (DetailPopup.IsOpen)
            {
                var popupChild = DetailPopup.Child as FrameworkElement;
                if (popupChild != null)
                {
                    // 鼠标点击位置相对于 popupChild 的坐标
                    Point pt = e.GetPosition(popupChild);
                    bool insidePopup = pt.X >= 0 && pt.X <= popupChild.ActualWidth
                                    && pt.Y >= 0 && pt.Y <= popupChild.ActualHeight;

                    // 检查是否点击在卡片自身上(避免点参数又关闭)
                    Point ptCard = e.GetPosition(this);
                    bool insideCard = ptCard.X >= 0 && ptCard.X <= this.ActualWidth
                                    && ptCard.Y >= 0 && ptCard.Y <= this.ActualHeight;

                    if (!insidePopup && !insideCard)
                    {
                        DetailPopup.IsOpen = false;
                    }
                }
            }
        }

        #region 核心功能方法

        public void SetMonitoringParameters(string deviceId, string deviceName, List<ParameterBase> parameters)
        {
            DeviceId = deviceId;
            DeviceName = deviceName;
            Parameters.Clear();

            foreach (var param in parameters)
            {
                var monitorParam = new MonitorParameter
                {
                    Name = param.Name,              // 保留原名(弹窗里用)
                    Value = FormatValue(param),
                    OriginalParameter = param
                };
                monitorParam.AddHistoryValue(DateTime.Now, ParseValue(param.Value));
                Parameters.Add(monitorParam);
            }
        }
        /// <summary>
        /// 按参数类型格式化显示值。数值参数按 DecimalPlaces 保留小数,消除 float 精度误差。
        /// </summary>
        private string FormatValue(ParameterBase param)
        {
            if (param?.Value == null) return "--";

            if (param is NumberParameter np &&
                double.TryParse(param.Value.ToString(), out double d))
            {
                return d.ToString("F" + np.DecimalPlaces);
            }
            return param.Value.ToString();
        }
        public void UpdateParameterValues(DeviceParameters outputParameters)
        {
            if (outputParameters?.Variables == null) return;

            var updateTime = DateTime.Now;

            foreach (var param in Parameters)
            {
                var updatedParam = outputParameters.Variables
                    .FirstOrDefault(v => v.Name == param.Name);

                if (updatedParam != null)
                {
                    var newValue = FormatValue(updatedParam);
                    if (param.Value != newValue)
                    {
                        param.Value = newValue;
                        param.OriginalParameter = updatedParam;
                        param.AddHistoryValue(updateTime, ParseValue(updatedParam.Value));
                    }
                }
            }

            // 如果弹窗打开，更新当前显示的参数
            if (DetailPopup.IsOpen && _currentParameter != null)
            {
                UpdatePopupForParameter(_currentParameter);
            }
        }

        private double ParseValue(object value)
        {
            if (value == null) return 0;
            if (double.TryParse(value.ToString(), out double result))
                return result;
            return 0;
        }

        #endregion

        #region 拖动功能 - 只在拖动手柄上

        private void DragHandle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            var canvas = this.Parent as Canvas;
            if (canvas == null) return;

            _isDragging = true;
            _dragStartPoint = e.GetPosition(canvas);
            _dragCurrentPoint = new Point(Canvas.GetLeft(this), Canvas.GetTop(this));

            if (double.IsNaN(_dragCurrentPoint.X))
                _dragCurrentPoint.X = 0;
            if (double.IsNaN(_dragCurrentPoint.Y))
                _dragCurrentPoint.Y = 0;

            DragHandle.CaptureMouse();
            e.Handled = true;
        }

        private void DragHandle_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_isDragging || e.LeftButton != MouseButtonState.Pressed)
            {
                if (_isDragging) StopDragging();
                return;
            }

            var canvas = this.Parent as Canvas;
            if (canvas == null)
            {
                StopDragging();
                return;
            }

            Point currentPoint = e.GetPosition(canvas);
            double offsetX = currentPoint.X - _dragStartPoint.X;
            double offsetY = currentPoint.Y - _dragStartPoint.Y;

            double newLeft = Math.Max(0, Math.Min(_dragCurrentPoint.X + offsetX, canvas.ActualWidth - this.ActualWidth));
            double newTop = Math.Max(0, Math.Min(_dragCurrentPoint.Y + offsetY, canvas.ActualHeight - this.ActualHeight));

            Canvas.SetLeft(this, newLeft);
            Canvas.SetTop(this, newTop);
        }

        private void DragHandle_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_isDragging)
            {
                StopDragging();
                e.Handled = true;
            }
        }

        private void DragHandle_MouseLeave(object sender, MouseEventArgs e)
        {
            if (_isDragging && e.LeftButton != MouseButtonState.Pressed)
            {
                StopDragging();
            }
        }

        private void StopDragging()
        {
            if (!_isDragging) return;
            _isDragging = false;
            if (DragHandle.IsMouseCaptured)
            {
                DragHandle.ReleaseMouseCapture();
            }
        }

        #endregion

        #region 参数点击事件

        private void ParameterItem_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is Border border && border.Tag is MonitorParameter parameter)
            {
                _currentParameter = parameter;
                ShowParameterPopup(parameter);

                // ★ 关键:必须立即标记 Handled,阻止事件继续冒泡
                // 否则 WPF 会把这次点击当成 Popup 外的点击,触发 StaysOpen=False 的关闭
                e.Handled = true;
            }
        }

        private void ShowParameterPopup(MonitorParameter parameter)
        {
            PopupTitleText.Text = $"{DeviceName} - {parameter.Name}";
            UpdatePopupLacalizationTxt();
            UpdatePopupForParameter(parameter);

            // ★ 用 Background 优先级而不是 Input
            // Input 优先级比 MouseDown 还高,会和"鼠标点击触发的 Popup 关闭"竞争
            // Background 优先级会等当前所有输入事件处理完才执行,稳定打开
            Dispatcher.BeginInvoke(new Action(() =>
            {
                DetailPopup.IsOpen = true;
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        private void UpdatePopupForParameter(MonitorParameter parameter)
        {
            PopupCurrentValue.Text = parameter.Value;

            if (parameter.HistoryData.Any())
            {
                PopupMaxValue.Text = parameter.HistoryData.Max(d => d.Value).ToString("F2");
                PopupMinValue.Text = parameter.HistoryData.Min(d => d.Value).ToString("F2");
                PopupAvgValue.Text = parameter.HistoryData.Average(d => d.Value).ToString("F2");

                DrawChart(ChartCanvas, parameter.HistoryData.ToList());
            }
            else
            {
                PopupMaxValue.Text = "--";
                PopupMinValue.Text = "--";
                PopupAvgValue.Text = "--";
                ChartCanvas.Children.Clear();
            }
        }

        /// <summary>
        /// 更新弹出窗口标签的本地化文本
        /// </summary>
        private void UpdatePopupLacalizationTxt()
        {
            ILocalizationService service = ContainerLocator.Container.Resolve<ILocalizationService>();
            if (service != null)
            {
                TbCurrentLabel.Text = service.GetString("ParamMonitorCard_Current", "当前");
                TbMaxLabel.Text = service.GetString("ParamMonitorCard_Max", "最大");
                TbMinLabel.Text = service.GetString("ParamMonitorCard_Min", "最小");
                TbAvgLabel.Text = service.GetString("ParamMonitorCard_Avg", "平均");
                TbNoneDataLabel.Text = service.GetString("ParamMonitorCard_NoneEnough", "暂无足够数据");
            }
        }

        private void PopupCloseButton_Click(object sender, RoutedEventArgs e)
        {
            DetailPopup.IsOpen = false;
        }

        // 阻止弹窗内部点击关闭弹窗
        private void Popup_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
        }

        // 阻止标题栏拖动时关闭弹窗
        private void PopupTitle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
        }

        #endregion

        #region 图表绘制

        private void ChartCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (_currentParameter != null && DetailPopup.IsOpen &&
                ChartCanvas.ActualWidth > 0 && ChartCanvas.ActualHeight > 0)
            {
                DrawChart(ChartCanvas, _currentParameter.HistoryData.ToList());
            }
        }

        private void DrawChart(Canvas canvas, List<DataPoint> data)
        {
            try
            {
                canvas.Children.Clear();

                if (canvas.ActualWidth <= 0 || canvas.ActualHeight <= 0)
                    return;

                if (data == null || data.Count < 2)
                {
                    //var tb = new TextBlock
                    //{
                    //    Text = "暂无足够数据",
                    //    FontSize = 11,
                    //    Foreground = new SolidColorBrush(Color.FromRgb(153, 153, 153)),
                    //    FontWeight = FontWeights.Normal
                    //};
                    //Canvas.SetLeft(tb, (canvas.ActualWidth - 80) / 2);
                    //Canvas.SetTop(tb, (canvas.ActualHeight - 20) / 2);
                    //canvas.Children.Add(tb);

                    //隐藏无数据提示文本
                    TbNoneDataLabel.Visibility = Visibility.Visible;
                    return;
                }
                //隐藏无数据提示文本
                TbNoneDataLabel.Visibility = Visibility.Collapsed;

                double width = canvas.ActualWidth;
                double height = canvas.ActualHeight;
                double leftPadding = 50;   // Y轴标签空间
                double rightPadding = 15;
                double topPadding = 10;
                double bottomPadding = 25; // X轴标签空间

                double chartWidth = width - leftPadding - rightPadding;
                double chartHeight = height - topPadding - bottomPadding;

                double minValue = data.Min(d => d.Value);
                double maxValue = data.Max(d => d.Value);
                double valueRange = maxValue - minValue;

                if (valueRange == 0)
                {
                    valueRange = Math.Abs(maxValue) > 0 ? Math.Abs(maxValue) : 1;
                    minValue -= valueRange * 0.1;
                    maxValue += valueRange * 0.1;
                    valueRange = maxValue - minValue;
                }
                else
                {
                    double margin = valueRange * 0.1;
                    minValue -= margin;
                    maxValue += margin;
                    valueRange = maxValue - minValue;
                }

                // 绘制网格线 - 浅灰色
                var gridBrush = new SolidColorBrush(Color.FromRgb(230, 230, 230));
                for (int i = 0; i <= 4; i++)
                {
                    double y = topPadding + (i / 4.0) * chartHeight;
                    canvas.Children.Add(new Line
                    {
                        X1 = leftPadding,
                        Y1 = y,
                        X2 = leftPadding + chartWidth,
                        Y2 = y,
                        Stroke = gridBrush,
                        StrokeThickness = 1
                    });
                }

                // Y轴刻度标签 - 左侧
                for (int i = 0; i <= 4; i++)
                {
                    double y = topPadding + (i / 4.0) * chartHeight;
                    double value = maxValue - (i / 4.0) * valueRange;

                    var label = new TextBlock
                    {
                        Text = value.ToString("F1") + " °C",
                        FontSize = 9,
                        Foreground = new SolidColorBrush(Color.FromRgb(136, 136, 136)),
                        TextAlignment = TextAlignment.Right,
                        Width = 45
                    };
                    Canvas.SetLeft(label, 0);
                    Canvas.SetTop(label, y - 7);
                    canvas.Children.Add(label);
                }

                // X轴时间标签
                int labelCount = Math.Min(6, data.Count);
                for (int i = 0; i < labelCount; i++)
                {
                    int dataIndex = i * (data.Count - 1) / (labelCount - 1);
                    if (dataIndex >= data.Count) dataIndex = data.Count - 1;

                    double x = leftPadding + (dataIndex / (double)(data.Count - 1)) * chartWidth;

                    var timeLabel = new TextBlock
                    {
                        Text = data[dataIndex].Time.ToString("HH:mm"),
                        FontSize = 9,
                        Foreground = new SolidColorBrush(Color.FromRgb(136, 136, 136))
                    };
                    Canvas.SetLeft(timeLabel, x - 15);
                    Canvas.SetTop(timeLabel, height - bottomPadding + 5);
                    canvas.Children.Add(timeLabel);
                }

                // 计算点位置
                var points = new PointCollection();
                for (int i = 0; i < data.Count; i++)
                {
                    double x = leftPadding + (i / (double)(data.Count - 1)) * chartWidth;
                    double y = topPadding + chartHeight - ((data[i].Value - minValue) / valueRange * chartHeight);
                    points.Add(new Point(x, y));
                }

                // 绘制蓝色曲线
                canvas.Children.Add(new Polyline
                {
                    Points = points,
                    Stroke = new SolidColorBrush(Color.FromRgb(33, 150, 243)),
                    StrokeThickness = 2,
                    StrokeLineJoin = PenLineJoin.Round,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round
                });

                // 数据点 - 不绘制，保持图表简洁
                // 如果需要可以取消注释

                //for (int i = 0; i < points.Count; i += Math.Max(1, points.Count / 20))
                //{
                //    var point = points[i];
                //    var ellipse = new Ellipse
                //    {
                //        Width = 4,
                //        Height = 4,
                //        Fill = new SolidColorBrush(Color.FromRgb(33, 150, 243)),
                //        ToolTip = $"{data[i].Value:F2} °C\n{data[i].Time:HH:mm:ss}"
                //    };
                //    Canvas.SetLeft(ellipse, point.X - 2);
                //    Canvas.SetTop(ellipse, point.Y - 2);
                //    canvas.Children.Add(ellipse);
                //}

            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"绘制图表失败: {ex.Message}");
            }
        }

        #endregion

        #region 公共方法

        public bool HasParameters() => Parameters?.Count > 0;

        public List<string> GetMonitoredParameterNames() =>
            Parameters?.Select(p => p.Name).ToList() ?? new List<string>();

        #endregion

        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    #region 数据模型类

    public class MonitorParameter : INotifyPropertyChanged
    {
        private string _name;
        private string _value;
        private ParameterBase _originalParameter;
        private ObservableCollection<DataPoint> _historyData;

        public string Name
        {
            get => _name;
            set
            {
                _name = value;
                OnPropertyChanged();
            }
        }

        public string Value
        {
            get => _value;
            set
            {
                _value = value;
                OnPropertyChanged();
            }
        }

        public ParameterBase OriginalParameter
        {
            get => _originalParameter;
            set
            {
                _originalParameter = value;
                OnPropertyChanged();
            }
        }

        public ObservableCollection<DataPoint> HistoryData
        {
            get => _historyData ??= new ObservableCollection<DataPoint>();
            set
            {
                _historyData = value;
                OnPropertyChanged();
            }
        }

        public void AddHistoryValue(DateTime time, double value)
        {
            HistoryData.Add(new DataPoint { Time = time, Value = value });
            while (HistoryData.Count > 100)
            {
                HistoryData.RemoveAt(0);
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public class DataPoint
    {
        public DateTime Time { get; set; }
        public double Value { get; set; }
    }

    #endregion
}
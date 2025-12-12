using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using MaxChemical.Modules.Designer.Models;
using MaxChemical.Modules.Designer.ViewModels;
using MaxChemical.Logging;

namespace MaxChemical.Modules.Designer.Views
{
    public partial class ParameterMonitorChartView : UserControl
    {
        private ParameterMonitorChartViewModel _viewModel;
        private readonly ILogService _logger;
        private readonly Dictionary<string, ParameterChartRenderer> _chartRenderers =
            new Dictionary<string, ParameterChartRenderer>();
        private DispatcherTimer _renderTimer;
        private DispatcherTimer _timeAxisTimer;

        public ParameterMonitorChartView()
        {
            InitializeComponent();
            _logger = new LogService().ForContext<ParameterMonitorChartView>();

            // 启用GPU加速
            RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
            RenderOptions.SetEdgeMode(this, EdgeMode.Unspecified);

            _renderTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(500)
            };
            _renderTimer.Tick += RenderTimer_Tick;

            _timeAxisTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(100)
            };
            _timeAxisTimer.Tick += TimeAxisTimer_Tick;

            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            _viewModel = DataContext as ParameterMonitorChartViewModel;
            if (_viewModel != null)
            {
                _viewModel.PropertyChanged += ViewModel_PropertyChanged;
                _viewModel.MonitoredParameters.CollectionChanged += MonitoredParameters_CollectionChanged;

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    RebuildChartLayout();
                    _renderTimer.Start();
                    _timeAxisTimer.Start();
                }), DispatcherPriority.Loaded);
            }
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            _renderTimer?.Stop();
            _timeAxisTimer?.Stop();

            if (_viewModel != null)
            {
                _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
                _viewModel.MonitoredParameters.CollectionChanged -= MonitoredParameters_CollectionChanged;
            }

            foreach (var renderer in _chartRenderers.Values)
            {
                renderer.Dispose();
            }
            _chartRenderers.Clear();
        }

        private void ViewModel_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(_viewModel.ChartData) ||
                e.PropertyName == nameof(_viewModel.HasData))
            {
                _logger.LogDebug("数据变化: {PropertyName}", e.PropertyName);
            }
        }

        private void MonitoredParameters_CollectionChanged(object sender,
            System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            _logger.LogDebug("监控参数集合变化");
            Dispatcher.BeginInvoke(new Action(RebuildChartLayout), DispatcherPriority.Loaded);
        }

        private void RenderTimer_Tick(object sender, EventArgs e)
        {
            UpdateAllCharts();
        }

        private void TimeAxisTimer_Tick(object sender, EventArgs e)
        {
            UpdateTimeAxis();
        }

        /// <summary>
        /// 智能重建图表布局
        /// </summary>
        private void RebuildChartLayout()
        {
            try
            {
                _logger.LogInformation("开始重建图表布局");

                // 清理旧渲染器
                foreach (var renderer in _chartRenderers.Values)
                {
                    renderer.Dispose();
                }
                _chartRenderers.Clear();

                if (TimelineChartsPanel == null || _viewModel == null)
                {
                    _logger.LogWarning("TimelineChartsPanel或ViewModel为空");
                    return;
                }

                TimelineChartsPanel.Children.Clear();

                var enabledParameters = _viewModel.MonitoredParameters
                    .Where(p => p.IsEnabled)
                    .ToList();

                if (!enabledParameters.Any())
                {
                    _logger.LogInformation("没有启用的参数");
                    return;
                }

                // 计算智能布局
                var layout = CalculateSmartLayout(enabledParameters.Count);
                _logger.LogInformation("布局: {Rows}行 x {Cols}列, 共{Total}个参数",
                    layout.Rows, layout.Columns, enabledParameters.Count);

                // 创建Grid容器（关键修改：确保占满父容器）
                var grid = new Grid
                {
                    Margin = new Thickness(0),
                    HorizontalAlignment = HorizontalAlignment.Stretch,  //  水平拉伸
                    VerticalAlignment = VerticalAlignment.Stretch       //  垂直拉伸
                };

                // 定义行（使用 Star 均分）
                for (int i = 0; i < layout.Rows; i++)
                {
                    grid.RowDefinitions.Add(new RowDefinition
                    {
                        Height = new GridLength(1, GridUnitType.Star)  //  均分高度
                    });
                }

                // 定义列（使用 Star 均分）
                for (int i = 0; i < layout.Columns; i++)
                {
                    grid.ColumnDefinitions.Add(new ColumnDefinition
                    {
                        Width = new GridLength(1, GridUnitType.Star)   //  均分宽度
                    });
                }

                // 放置图表
                for (int i = 0; i < enabledParameters.Count; i++)
                {
                    var param = enabledParameters[i];
                    var position = layout.GetPosition(i);

                    var chartCard = CreateChartCard(param);
                    Grid.SetRow(chartCard, position.Row);
                    Grid.SetColumn(chartCard, position.Column);

                    //  如果是最后一行且只有一个元素，跨越所有列
                    if (IsLastRowSingleItem(enabledParameters.Count, layout, i))
                    {
                        Grid.SetColumnSpan(chartCard, layout.Columns);
                    }

                    grid.Children.Add(chartCard);
                }

                TimelineChartsPanel.Children.Add(grid);

                // 延迟初始化渲染器
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    InitializeAllCharts();
                }), DispatcherPriority.Loaded);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "重建图表布局失败");
            }
        }

        /// <summary>
        /// 判断是否是最后一行的单独元素（需要跨列）
        /// </summary>
        private bool IsLastRowSingleItem(int totalCount, SmartLayout layout, int index)
        {
            int lastRowStartIndex = (layout.Rows - 1) * layout.Columns;
            int itemsInLastRow = totalCount - lastRowStartIndex;

            // 如果最后一行只有1个元素，且当前是该元素
            return itemsInLastRow == 1 && index == lastRowStartIndex;
        }
        /// <summary>
        /// 智能布局计算器
        /// </summary>
        private SmartLayout CalculateSmartLayout(int paramCount)
        {
            return paramCount switch
            {
                1 => new SmartLayout { Rows = 1, Columns = 1 },
                2 => new SmartLayout { Rows = 2, Columns = 1 },
                3 => new SmartLayout { Rows = 2, Columns = 2 },
                4 => new SmartLayout { Rows = 2, Columns = 2 },
                5 => new SmartLayout { Rows = 3, Columns = 2 },
                6 => new SmartLayout { Rows = 3, Columns = 2 },
                7 => new SmartLayout { Rows = 3, Columns = 3 },
                8 => new SmartLayout { Rows = 3, Columns = 3 },
                9 => new SmartLayout { Rows = 3, Columns = 3 },
                _ => CalculateAutoLayout(paramCount)
            };
        }
        private SmartLayout CalculateAutoLayout(int paramCount)
        {
            int columns = paramCount <= 6 ? 2 : 3;
            int rows = (int)Math.Ceiling((double)paramCount / columns);
            return new SmartLayout { Rows = rows, Columns = columns };
        }
        /// <summary>
        /// 创建单个图表卡片
        /// </summary>
        private Border CreateChartCard(MonitoredParameter parameter)
        {
            var card = new Border
            {
                Background = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(232, 234, 237)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Margin = new Thickness(10),
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    Color = Colors.Black,
                    ShadowDepth = 0,
                    BlurRadius = 12,
                    Opacity = 0.06
                }
            };

            var mainGrid = new Grid();
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            // 头部
            var header = CreateChartHeader(parameter);
            Grid.SetRow(header, 0);
            mainGrid.Children.Add(header);

            // Canvas容器
            var canvasContainer = new Border
            {
                Background = Brushes.White,
                Padding = new Thickness(20)
            };
            RenderOptions.SetBitmapScalingMode(canvasContainer, BitmapScalingMode.HighQuality);
            RenderOptions.SetEdgeMode(canvasContainer, EdgeMode.Unspecified);

            var canvas = new Canvas
            {
                Background = Brushes.Transparent,
                ClipToBounds = true,
                Tag = parameter,
                MinHeight = 280
            };
            RenderOptions.SetBitmapScalingMode(canvas, BitmapScalingMode.HighQuality);
            RenderOptions.SetEdgeMode(canvas, EdgeMode.Unspecified);
            canvas.CacheMode = new BitmapCache { RenderAtScale = 2 };

            canvasContainer.Child = canvas;
            Grid.SetRow(canvasContainer, 1);
            mainGrid.Children.Add(canvasContainer);

            card.Child = mainGrid;
            return card;
        }

        /// <summary>
        /// 创建图表头部
        /// </summary>
        private Border CreateChartHeader(MonitoredParameter parameter)
        {
            var header = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(250, 251, 252)),
                Padding = new Thickness(24, 16, 24, 16),
                CornerRadius = new CornerRadius(8, 8, 0, 0),
                BorderBrush = new SolidColorBrush(Color.FromRgb(232, 234, 237)),
                BorderThickness = new Thickness(0, 0, 0, 1)
            };

            var headerGrid = new Grid();
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // 左侧: 参数名称
            var namePanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };

            var colorBar = new Border
            {
                Width = 4,
                Height = 28,
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(parameter.Color)),
                CornerRadius = new CornerRadius(2),
                Margin = new Thickness(0, 0, 14, 0)
            };

            var nameText = new TextBlock
            {
                Text = parameter.DisplayName,
                FontSize = 16,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(38, 38, 38)),
                VerticalAlignment = VerticalAlignment.Center
            };

            namePanel.Children.Add(colorBar);
            namePanel.Children.Add(nameText);
            Grid.SetColumn(namePanel, 0);
            headerGrid.Children.Add(namePanel);

            // 右侧: 实时数值
            var valueBox = new Border
            {
                Background = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(232, 234, 237)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(18, 10, 18, 10)
            };

            var valuePanel = new StackPanel { Orientation = Orientation.Horizontal };

            var currentLabel = new TextBlock
            {
                Text = "CURRENT",
                FontSize = 10,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(140, 140, 140)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0)
            };

            var currentValue = new TextBlock
            {
                Text = "--",
                FontSize = 20,
                FontWeight = FontWeights.Bold,
                FontFamily = new FontFamily("Consolas"),
                Foreground = new SolidColorBrush(Color.FromRgb(24, 144, 255)),
                VerticalAlignment = VerticalAlignment.Center
            };

            valuePanel.Children.Add(currentLabel);
            valuePanel.Children.Add(currentValue);
            valueBox.Child = valuePanel;

            Grid.SetColumn(valueBox, 2);
            headerGrid.Children.Add(valueBox);

            header.Child = headerGrid;
            return header;
        }

        private void UpdateTimeAxis()
        {
            try
            {
                if (TimeAxisContainer == null || _viewModel == null)
                    return;

                TimeAxisContainer.Children.Clear();

                var now = DateTime.Now;
                var timeSpan = TimeSpan.FromMinutes(15);
                var startTime = now - timeSpan;

                var stackPanel = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };

                var leftArrow = CreateIconButton("◄", "#8C8C8C");
                stackPanel.Children.Add(leftArrow);

                for (int i = 0; i < 7; i++)
                {
                    var time = startTime.AddMinutes(timeSpan.TotalMinutes * i / 6);
                    var isNow = i == 6;

                    if (isNow)
                    {
                        var nowBorder = new Border
                        {
                            Background = new SolidColorBrush(Color.FromRgb(230, 247, 255)),
                            CornerRadius = new CornerRadius(4),
                            Padding = new Thickness(10, 6, 10, 6),
                            Margin = new Thickness(0, 0, 12, 0)
                        };

                        var nowText = new TextBlock
                        {
                            Text = now.ToString("HH:mm:ss.fff"),
                            FontSize = 12,
                            FontWeight = FontWeights.Bold,
                            Foreground = new SolidColorBrush(Color.FromRgb(24, 144, 255))
                        };

                        nowBorder.Child = nowText;
                        stackPanel.Children.Add(nowBorder);
                    }
                    else
                    {
                        var timeText = new TextBlock
                        {
                            Text = time.ToString("HH:mm"),
                            FontSize = 11,
                            Foreground = new SolidColorBrush(Color.FromRgb(140, 140, 140)),
                            Margin = new Thickness(0, 0, 16, 0),
                            VerticalAlignment = VerticalAlignment.Center
                        };
                        stackPanel.Children.Add(timeText);
                    }
                }

                var rightArrow = CreateIconButton("►", "#8C8C8C");
                stackPanel.Children.Add(rightArrow);

                TimeAxisContainer.Children.Add(stackPanel);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "更新时间轴失败");
            }
        }

        private Button CreateIconButton(string icon, string color)
        {
            var button = new Button
            {
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Width = 24,
                Height = 24,
                Margin = new Thickness(0, 0, 12, 0),
                Cursor = Cursors.Hand
            };

            var textBlock = new TextBlock
            {
                Text = icon,
                FontSize = 12,
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            button.Content = textBlock;

            button.MouseEnter += (s, e) =>
            {
                button.Background = new SolidColorBrush(Color.FromRgb(240, 240, 240));
            };
            button.MouseLeave += (s, e) =>
            {
                button.Background = Brushes.Transparent;
            };

            return button;
        }

        private void InitializeAllCharts()
        {
            try
            {
                _logger.LogInformation("开始初始化所有图表");

                var canvasList = FindVisualChildren<Canvas>(TimelineChartsPanel)
                    .Where(c => c.Tag is MonitoredParameter)
                    .ToList();

                _logger.LogInformation("找到 {Count} 个图表Canvas", canvasList.Count);

                foreach (var canvas in canvasList)
                {
                    var parameter = canvas.Tag as MonitoredParameter;
                    if (parameter != null && parameter.IsEnabled)
                    {
                        var renderer = new ParameterChartRenderer(canvas, parameter, _viewModel, _logger);
                        _chartRenderers[parameter.Key] = renderer;

                        _logger.LogDebug("创建图表渲染器: {Parameter}", parameter.DisplayName);
                    }
                }

                UpdateAllCharts();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "初始化图表失败");
            }
        }

        private void UpdateAllCharts()
        {
            try
            {
                foreach (var renderer in _chartRenderers.Values.ToList())
                {
                    renderer.Render();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "更新图表失败");
            }
        }

        private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
        {
            if (parent == null) yield break;

            var childCount = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < childCount; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);

                if (child is T typedChild)
                {
                    yield return typedChild;
                }

                foreach (var descendant in FindVisualChildren<T>(child))
                {
                    yield return descendant;
                }
            }
        }

        /// <summary>
        /// 智能布局配置
        /// </summary>
        private class SmartLayout
        {
            public int Rows { get; set; }
            public int Columns { get; set; }

            public (int Row, int Column) GetPosition(int index)
            {
                int row = index / Columns;
                int col = index % Columns;
                return (row, col);
            }
        }

    }

    /// <summary>
    /// GPU加速图表渲染器（保持不变）
    /// </summary>
    public class ParameterChartRenderer : IDisposable
    {
        // ... (保持之前的完整实现不变)
        private readonly Canvas _canvas;
        private readonly MonitoredParameter _parameter;
        private readonly ParameterMonitorChartViewModel _viewModel;
        private readonly ILogService _logger;

        private Border _tooltip;
        private StackPanel _tooltipContent;
        private DateTime _lastRenderTime = DateTime.MinValue;

        private const double MarginLeft = 60;
        private const double MarginRight = 90;
        private const double MarginTop = 45;
        private const double MarginBottom = 35;

        public ParameterChartRenderer(Canvas canvas, MonitoredParameter parameter,
            ParameterMonitorChartViewModel viewModel, ILogService logger)
        {
            _canvas = canvas ?? throw new ArgumentNullException(nameof(canvas));
            _parameter = parameter ?? throw new ArgumentNullException(nameof(parameter));
            _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            _canvas.SizeChanged += Canvas_SizeChanged;
            _canvas.MouseMove += Canvas_MouseMove;
            _canvas.MouseLeave += Canvas_MouseLeave;

            CreateTooltip();
        }

        private void CreateTooltip()
        {
            _tooltipContent = new StackPanel();

            _tooltip = new Border
            {
                Background = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(217, 217, 217)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(14, 10, 14, 10),
                Visibility = Visibility.Collapsed,
                Child = _tooltipContent
            };

            _tooltip.Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = Colors.Black,
                ShadowDepth = 2,
                BlurRadius = 12,
                Opacity = 0.15
            };

            RenderOptions.SetBitmapScalingMode(_tooltip, BitmapScalingMode.HighQuality);

            _canvas.Children.Add(_tooltip);
            Canvas.SetZIndex(_tooltip, 1000);
        }

        private void Canvas_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            Render();
        }

        private void Canvas_MouseMove(object sender, MouseEventArgs e)
        {
            var mousePos = e.GetPosition(_canvas);
            ShowTooltip(mousePos);
        }

        private void Canvas_MouseLeave(object sender, MouseEventArgs e)
        {
            _tooltip.Visibility = Visibility.Collapsed;
        }

        public void Render()
        {
            try
            {
                var now = DateTime.Now;
                if ((now - _lastRenderTime).TotalMilliseconds < 100)
                    return;
                _lastRenderTime = now;

                if (_canvas.ActualWidth == 0 || _canvas.ActualHeight == 0)
                    return;

                var elementsToRemove = _canvas.Children.Cast<UIElement>()
                    .Where(e => e != _tooltip)
                    .ToList();

                foreach (var element in elementsToRemove)
                {
                    _canvas.Children.Remove(element);
                }

                var data = GetParameterData();

                if (!data.Any())
                {
                    DrawNoDataMessage();
                    return;
                }

                double chartWidth = _canvas.ActualWidth - MarginLeft - MarginRight;
                double chartHeight = _canvas.ActualHeight - MarginTop - MarginBottom;

                if (chartWidth <= 0 || chartHeight <= 0)
                    return;

                DrawSimplifiedLegend(data);
                DrawGrid(chartWidth, chartHeight);
                DrawCurve(chartWidth, chartHeight, data);
                DrawYAxisLabels(chartWidth, chartHeight, data);
                DrawXAxis(chartWidth, chartHeight, data);
                DrawKeyValueAnnotations(chartWidth, chartHeight, data);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "渲染图表失败: {Parameter}", _parameter.DisplayName);
            }
        }

        private List<ChartDataPoint> GetParameterData()
        {
            return _viewModel.ChartData
                .Where(d => d.ParameterValues.ContainsKey(_parameter.DisplayName))
                .OrderBy(d => d.Timestamp)
                .ToList();
        }

        private void DrawSimplifiedLegend(List<ChartDataPoint> data)
        {
            var values = data.Select(d => d.ParameterValues[_parameter.DisplayName]).ToList();
            var minVal = values.Min();
            var maxVal = values.Max();
            var range = maxVal - minVal;

            var thresholds = new[]
            {
                new { Color = "#FF4D4F", Value = $"< {minVal + range * 0.2:F0}" },
                new { Color = "#FAAD14", Value = $"{minVal + range * 0.2:F0} - {minVal + range * 0.4:F0}" },
                new { Color = "#52C41A", Value = $"{minVal + range * 0.4:F0} - {minVal + range * 0.7:F0}" },
                new { Color = "#FAAD14", Value = $"{minVal + range * 0.7:F0} - {minVal + range * 0.9:F0}" },
                new { Color = "#FF4D4F", Value = $"≥ {minVal + range * 0.9:F0}" }
            };

            var legendPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Left
            };

            foreach (var threshold in thresholds)
            {
                var item = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Margin = new Thickness(0, 0, 20, 0)
                };

                var dot = new Ellipse
                {
                    Width = 9,
                    Height = 9,
                    Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(threshold.Color)),
                    Margin = new Thickness(0, 0, 6, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };

                var label = new TextBlock
                {
                    Text = threshold.Value,
                    FontSize = 11,
                    Foreground = new SolidColorBrush(Color.FromRgb(89, 89, 89)),
                    VerticalAlignment = VerticalAlignment.Center
                };

                item.Children.Add(dot);
                item.Children.Add(label);
                legendPanel.Children.Add(item);
            }

            Canvas.SetLeft(legendPanel, MarginLeft);
            Canvas.SetTop(legendPanel, 8);
            _canvas.Children.Add(legendPanel);
        }

        private void DrawGrid(double width, double height)
        {
            var gridBrush = new SolidColorBrush(Color.FromRgb(240, 240, 240));

            for (int i = 0; i <= 6; i++)
            {
                double y = MarginTop + (height / 6) * i;
                var line = new Line
                {
                    X1 = MarginLeft,
                    Y1 = y,
                    X2 = MarginLeft + width,
                    Y2 = y,
                    Stroke = gridBrush,
                    StrokeThickness = 1
                };

                RenderOptions.SetEdgeMode(line, EdgeMode.Aliased);
                _canvas.Children.Add(line);
            }
        }

        private void DrawCurve(double width, double height, List<ChartDataPoint> data)
        {
            if (data.Count < 2) return;

            var values = data.Select(d => d.ParameterValues[_parameter.DisplayName]).ToList();
            var minValue = values.Min();
            var maxValue = values.Max();
            var range = maxValue - minValue;

            if (range == 0) range = 1;

            minValue -= range * 0.05;
            maxValue += range * 0.05;
            range = maxValue - minValue;

            var minTime = data.First().Timestamp;
            var maxTime = data.Last().Timestamp;
            var timeSpan = (maxTime - minTime).TotalSeconds;
            if (timeSpan == 0) timeSpan = 1;

            var points = new List<(Point Point, double Value)>();
            foreach (var dp in data)
            {
                var value = dp.ParameterValues[_parameter.DisplayName];
                double x = MarginLeft + ((dp.Timestamp - minTime).TotalSeconds / timeSpan) * width;
                double y = MarginTop + height - ((value - minValue) / range) * height;
                points.Add((new Point(x, y), value));
            }

            DrawGradientFill(points.Select(p => p.Point).ToList(), width, height);
            DrawSegmentedCurve(points, minValue, maxValue, range);
        }

        private void DrawSegmentedCurve(List<(Point Point, double Value)> points,
            double minValue, double maxValue, double range)
        {
            if (points.Count < 2) return;

            var segments = new Dictionary<Color, PathGeometry>();

            for (int i = 0; i < points.Count - 1; i++)
            {
                var currentPoint = points[i];
                var nextPoint = points[i + 1];
                var color = GetColorForValue(currentPoint.Value, minValue, maxValue, range);

                if (!segments.ContainsKey(color))
                {
                    segments[color] = new PathGeometry();
                }

                var figure = new PathFigure { StartPoint = currentPoint.Point };
                figure.Segments.Add(new LineSegment(nextPoint.Point, true));
                segments[color].Figures.Add(figure);
            }

            foreach (var segment in segments)
            {
                var path = new Path
                {
                    Stroke = new SolidColorBrush(segment.Key),
                    StrokeThickness = 2.5,
                    Data = segment.Value
                };

                RenderOptions.SetEdgeMode(path, EdgeMode.Unspecified);
                path.SetValue(RenderOptions.CachingHintProperty, CachingHint.Cache);

                _canvas.Children.Add(path);
            }
        }

        private Color GetColorForValue(double value, double minValue, double maxValue, double range)
        {
            double normalized = (value - minValue) / range;

            if (normalized < 0.2)
                return (Color)ColorConverter.ConvertFromString("#FF4D4F");
            else if (normalized < 0.4)
                return (Color)ColorConverter.ConvertFromString("#FAAD14");
            else if (normalized < 0.7)
                return (Color)ColorConverter.ConvertFromString("#52C41A");
            else if (normalized < 0.9)
                return (Color)ColorConverter.ConvertFromString("#FAAD14");
            else
                return (Color)ColorConverter.ConvertFromString("#FF4D4F");
        }

        private void DrawGradientFill(List<Point> points, double width, double height)
        {
            if (points.Count < 2) return;

            var pathFigure = new PathFigure { StartPoint = points[0] };

            for (int i = 1; i < points.Count; i++)
            {
                pathFigure.Segments.Add(new LineSegment(points[i], true));
            }

            pathFigure.Segments.Add(new LineSegment(
                new Point(points.Last().X, MarginTop + height), true));
            pathFigure.Segments.Add(new LineSegment(
                new Point(points.First().X, MarginTop + height), true));
            pathFigure.IsClosed = true;

            var pathGeometry = new PathGeometry();
            pathGeometry.Figures.Add(pathFigure);

            var gradientBrush = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(0, 1),
                GradientStops = new GradientStopCollection
                {
                    new GradientStop(Color.FromArgb(35, 82, 196, 26), 0),
                    new GradientStop(Color.FromArgb(5, 82, 196, 26), 1)
                }
            };

            var areaPath = new Path
            {
                Fill = gradientBrush,
                Data = pathGeometry
            };

            areaPath.SetValue(RenderOptions.CachingHintProperty, CachingHint.Cache);
            _canvas.Children.Add(areaPath);
        }

        private void DrawYAxisLabels(double width, double height, List<ChartDataPoint> data)
        {
            var values = data.Select(d => d.ParameterValues[_parameter.DisplayName]).ToList();
            var minValue = values.Min() - (values.Max() - values.Min()) * 0.05;
            var maxValue = values.Max() + (values.Max() - values.Min()) * 0.05;
            var range = maxValue - minValue;

            var labelBrush = new SolidColorBrush(Color.FromRgb(140, 140, 140));

            for (int i = 0; i <= 6; i++)
            {
                double value = minValue + (range * i / 6.0);
                double y = MarginTop + height - (i / 6.0) * height;

                var text = new TextBlock
                {
                    Text = $"{value:F0}",
                    Foreground = labelBrush,
                    FontSize = 11
                };

                text.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                Canvas.SetLeft(text, MarginLeft - text.DesiredSize.Width - 8);
                Canvas.SetTop(text, y - text.DesiredSize.Height / 2);
                _canvas.Children.Add(text);
            }
        }

        private void DrawXAxis(double width, double height, List<ChartDataPoint> data)
        {
            var labelBrush = new SolidColorBrush(Color.FromRgb(140, 140, 140));
            var minTime = data.First().Timestamp;
            var maxTime = data.Last().Timestamp;

            for (int i = 0; i <= 6; i++)
            {
                var time = minTime.AddSeconds((maxTime - minTime).TotalSeconds * (i / 6.0));
                double x = MarginLeft + (width / 6) * i;

                var text = new TextBlock
                {
                    Text = time.ToString("HH:mm"),
                    Foreground = labelBrush,
                    FontSize = 11
                };

                text.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                Canvas.SetLeft(text, x - text.DesiredSize.Width / 2);
                Canvas.SetTop(text, MarginTop + height + 8);
                _canvas.Children.Add(text);
            }
        }

        private void DrawKeyValueAnnotations(double width, double height, List<ChartDataPoint> data)
        {
            var values = data.Select(d => d.ParameterValues[_parameter.DisplayName]).ToList();
            var minValue = values.Min() - (values.Max() - values.Min()) * 0.05;
            var maxValue = values.Max() + (values.Max() - values.Min()) * 0.05;
            var range = maxValue - minValue;

            var minTime = data.First().Timestamp;
            var maxTime = data.Last().Timestamp;
            var timeSpan = (maxTime - minTime).TotalSeconds;
            if (timeSpan == 0) timeSpan = 1;

            var maxIndex = values.IndexOf(values.Max());
            var minIndex = values.IndexOf(values.Min());

            if (maxIndex >= 0 && maxIndex < data.Count)
            {
                DrawValueAnnotation(data[maxIndex], width, height,
                    minValue, maxValue, range, minTime, timeSpan, true);
            }

            if (minIndex >= 0 && minIndex < data.Count)
            {
                DrawValueAnnotation(data[minIndex], width, height,
                    minValue, maxValue, range, minTime, timeSpan, false);
            }
        }

        private void DrawValueAnnotation(ChartDataPoint dataPoint,
            double width, double height, double minValue, double maxValue,
            double range, DateTime minTime, double timeSpan, bool isMax)
        {
            var value = dataPoint.ParameterValues[_parameter.DisplayName];
            double x = MarginLeft + ((dataPoint.Timestamp - minTime).TotalSeconds / timeSpan) * width;
            double y = MarginTop + height - ((value - minValue) / range) * height;

            var dashedLine = new Line
            {
                X1 = x,
                Y1 = y,
                X2 = MarginLeft + width + 15,
                Y2 = y,
                Stroke = new SolidColorBrush(Color.FromRgb(140, 140, 140)),
                StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection { 4, 3 }
            };
            RenderOptions.SetEdgeMode(dashedLine, EdgeMode.Aliased);
            _canvas.Children.Add(dashedLine);

            var arrow = new Polygon
            {
                Points = new PointCollection(new[]
                {
                    new Point(0, -4),
                    new Point(6, 0),
                    new Point(0, 4)
                }),
                Fill = new SolidColorBrush(Color.FromRgb(140, 140, 140))
            };
            Canvas.SetLeft(arrow, MarginLeft + width + 15);
            Canvas.SetTop(arrow, y);
            _canvas.Children.Add(arrow);

            var label = new TextBlock
            {
                Text = $"{value:F0}",
                FontSize = 13,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(38, 38, 38))
            };

            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(label, MarginLeft + width + 25);
            Canvas.SetTop(label, y - label.DesiredSize.Height / 2);
            _canvas.Children.Add(label);
        }

        private void DrawNoDataMessage()
        {
            var stackPanel = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            var icon = new TextBlock
            {
                Text = "",
                FontSize = 40,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 12)
            };

            var text = new TextBlock
            {
                Text = "Waiting for data...",
                Foreground = new SolidColorBrush(Color.FromRgb(191, 191, 191)),
                FontSize = 14,
                HorizontalAlignment = HorizontalAlignment.Center
            };

            stackPanel.Children.Add(icon);
            stackPanel.Children.Add(text);

            Canvas.SetLeft(stackPanel, (_canvas.ActualWidth - 150) / 2);
            Canvas.SetTop(stackPanel, (_canvas.ActualHeight - 80) / 2);
            _canvas.Children.Add(stackPanel);
        }

        private void ShowTooltip(Point mousePos)
        {
            var data = GetParameterData();
            if (!data.Any())
            {
                _tooltip.Visibility = Visibility.Collapsed;
                return;
            }

            var nearestPoint = FindNearestDataPoint(mousePos, data);
            if (nearestPoint == null)
            {
                _tooltip.Visibility = Visibility.Collapsed;
                return;
            }

            _tooltipContent.Children.Clear();

            var timeText = new TextBlock
            {
                Text = nearestPoint.Timestamp.ToString("MMM dd yyyy HH:mm:ss.fff"),
                FontWeight = FontWeights.SemiBold,
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(89, 89, 89)),
                Margin = new Thickness(0, 0, 0, 6)
            };
            _tooltipContent.Children.Add(timeText);

            if (nearestPoint.ParameterValues.TryGetValue(_parameter.DisplayName, out var value))
            {
                var paramPanel = new StackPanel
                {
                    Orientation = Orientation.Horizontal
                };

                var dot = new Ellipse
                {
                    Width = 8,
                    Height = 8,
                    Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_parameter.Color)),
                    Margin = new Thickness(0, 0, 6, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };

                var valueText = new TextBlock
                {
                    FontSize = 13,
                    Foreground = new SolidColorBrush(Color.FromRgb(38, 38, 38))
                };
                valueText.Inlines.Add(new System.Windows.Documents.Run(_parameter.DisplayName)
                {
                    FontWeight = FontWeights.SemiBold
                });
                valueText.Inlines.Add(new System.Windows.Documents.Run($"  {value:F2}")
                {
                    FontWeight = FontWeights.Bold,
                    Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_parameter.Color))
                });

                paramPanel.Children.Add(dot);
                paramPanel.Children.Add(valueText);
                _tooltipContent.Children.Add(paramPanel);
            }

            _tooltip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var tooltipWidth = _tooltip.DesiredSize.Width;
            var tooltipHeight = _tooltip.DesiredSize.Height;

            double left = mousePos.X + 15;
            double top = mousePos.Y - tooltipHeight / 2;

            if (left + tooltipWidth > _canvas.ActualWidth)
                left = mousePos.X - tooltipWidth - 15;

            if (top < 0)
                top = 0;
            else if (top + tooltipHeight > _canvas.ActualHeight)
                top = _canvas.ActualHeight - tooltipHeight;

            Canvas.SetLeft(_tooltip, left);
            Canvas.SetTop(_tooltip, top);
            _tooltip.Visibility = Visibility.Visible;
        }

        private ChartDataPoint FindNearestDataPoint(Point mousePos, List<ChartDataPoint> data)
        {
            const double threshold = 50;
            double width = _canvas.ActualWidth - MarginLeft - MarginRight;
            double height = _canvas.ActualHeight - MarginTop - MarginBottom;

            if (width <= 0 || height <= 0 || !data.Any())
                return null;

            var values = data.Select(d => d.ParameterValues[_parameter.DisplayName]).ToList();
            var minValue = values.Min() - (values.Max() - values.Min()) * 0.05;
            var maxValue = values.Max() + (values.Max() - values.Min()) * 0.05;
            var range = maxValue - minValue;

            var minTime = data.First().Timestamp;
            var maxTime = data.Last().Timestamp;
            var timeSpan = (maxTime - minTime).TotalSeconds;
            if (timeSpan == 0) timeSpan = 1;

            ChartDataPoint nearest = null;
            double minDistance = double.MaxValue;

            foreach (var dp in data)
            {
                if (!dp.ParameterValues.ContainsKey(_parameter.DisplayName))
                    continue;

                var value = dp.ParameterValues[_parameter.DisplayName];
                double x = MarginLeft + ((dp.Timestamp - minTime).TotalSeconds / timeSpan) * width;
                double y = MarginTop + height - ((value - minValue) / range) * height;

                double distance = Math.Sqrt(Math.Pow(x - mousePos.X, 2) + Math.Pow(y - mousePos.Y, 2));

                if (distance < minDistance && distance < threshold)
                {
                    minDistance = distance;
                    nearest = dp;
                }
            }

            return nearest;
        }

        public void Dispose()
        {
            _canvas.SizeChanged -= Canvas_SizeChanged;
            _canvas.MouseMove -= Canvas_MouseMove;
            _canvas.MouseLeave -= Canvas_MouseLeave;
        }
    }
}
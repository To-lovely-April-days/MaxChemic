using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using MaxChemical.Modules.Designer.Models;
using MaxChemical.Modules.Designer.ViewModels;
using MaxChemical.Logging;
using Microsoft.Win32;
using OfficeOpenXml;
using OfficeOpenXml.Style;
using Color = System.Windows.Media.Color;
using DrawingColor = System.Drawing.Color;

namespace MaxChemical.Modules.Designer.Views
{
    public partial class ParameterMonitorChartView : UserControl
    {
        private ParameterMonitorChartViewModel _viewModel;
        private readonly ILogService _logger;
        private bool _isLoaded;

        // ═══ 品牌色板(与 BrandColors.xaml 保持一致) ═══
        private static readonly Color BrandPrimary = Color.FromRgb(0xA4, 0x16, 0x26);
        private static readonly Color BrandPrimaryLight = Color.FromRgb(0xFC, 0xEA, 0xEC);
        private static readonly Color BrandPrimaryDark = Color.FromRgb(0x6B, 0x0E, 0x1A);
        private static readonly Color TextPrimary = Color.FromRgb(0x1A, 0x1A, 0x1A);
        private static readonly Color TextSecondary = Color.FromRgb(0x5F, 0x5E, 0x5A);
        private static readonly Color TextTertiary = Color.FromRgb(0x88, 0x87, 0x80);
        private static readonly Color BgPrimary = Color.FromRgb(0xFF, 0xFF, 0xFF);
        private static readonly Color BgHover = Color.FromRgb(0xF4, 0xF3, 0xEF);
        private static readonly Color BorderDefault = Color.FromRgb(0xEA, 0xEA, 0xEA);
        private static readonly Color BorderStrong = Color.FromRgb(0xD3, 0xD1, 0xC7);

        // 轴归属徽章颜色:Temperature/Pressure/Flow 三种
        private static readonly Color BadgeBgT = Color.FromRgb(0xFA, 0xEE, 0xDA);
        private static readonly Color BadgeFgT = Color.FromRgb(0x85, 0x4F, 0x0B);
        private static readonly Color BadgeBgP = Color.FromRgb(0xFC, 0xEA, 0xEC);
        private static readonly Color BadgeFgP = Color.FromRgb(0x6B, 0x0E, 0x1A);
        private static readonly Color BadgeBgF = Color.FromRgb(0xE1, 0xF5, 0xEE);
        private static readonly Color BadgeFgF = Color.FromRgb(0x08, 0x50, 0x41);

        private static readonly Color[] LinePalette =
        {
            Color.FromRgb(0xA4, 0x16, 0x26),
            Color.FromRgb(0xBA, 0x75, 0x17),
            Color.FromRgb(0x1A, 0x1A, 0x1A),
            Color.FromRgb(0x0F, 0x6E, 0x56),
            Color.FromRgb(0x53, 0x4A, 0xB7),
            Color.FromRgb(0x18, 0x5F, 0xA5),
            Color.FromRgb(0x99, 0x35, 0x56),
            Color.FromRgb(0x5F, 0x5E, 0x5A),
        };

        private static readonly DrawingColor[] ExcelColors =
        {
            DrawingColor.FromArgb(0xA4, 0x16, 0x26),
            DrawingColor.FromArgb(0xBA, 0x75, 0x17),
            DrawingColor.FromArgb(0x1A, 0x1A, 0x1A),
            DrawingColor.FromArgb(0x0F, 0x6E, 0x56),
            DrawingColor.FromArgb(0x53, 0x4A, 0xB7),
            DrawingColor.FromArgb(0x18, 0x5F, 0xA5),
            DrawingColor.FromArgb(0x99, 0x35, 0x56),
            DrawingColor.FromArgb(0x5F, 0x5E, 0x5A),
        };

        public ParameterMonitorChartView()
        {
            InitializeComponent();
            _logger = new LogService().ForContext<ParameterMonitorChartView>();

            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        #region Lifecycle

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            _isLoaded = true;
            _viewModel = DataContext as ParameterMonitorChartViewModel;
            if (_viewModel == null) return;

            _viewModel.MonitoredParameters.CollectionChanged += (s, ev) => RebuildParameterList();
            _viewModel.AvailableParameters.CollectionChanged += (s, ev) => RebuildParameterList();

            RebuildParameterList();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e) { }

        #endregion

        #region 反射安全读取属性(兼容 Model 没有 Unit 属性的情况)

        /// <summary>
        /// 通过反射安全读取对象的 string 属性,不存在或为 null 时返回 ""
        /// </summary>
        private static string GetStringProp(object obj, string propName)
        {
            if (obj == null) return "";
            try
            {
                var prop = obj.GetType().GetProperty(propName,
                    BindingFlags.Public | BindingFlags.Instance);
                if (prop == null) return "";
                var val = prop.GetValue(obj);
                return val?.ToString() ?? "";
            }
            catch
            {
                return "";
            }
        }

        #endregion

        #region 左侧参数面板(品牌色 + 轴归属徽章 + 当前值)

        private void RebuildParameterList()
        {
            if (_viewModel == null || ParameterListPanel == null) return;
            try
            {
                ParameterListPanel.Children.Clear();

                // 1. 建立 DeviceId → 最新 DeviceName 映射
                var deviceNameMap = new Dictionary<string, string>();
                foreach (var ap in _viewModel.AvailableParameters)
                {
                    if (!string.IsNullOrEmpty(ap.DeviceId) && !deviceNameMap.ContainsKey(ap.DeviceId))
                        deviceNameMap[ap.DeviceId] = ap.DeviceName ?? "未知设备";
                }
                foreach (var mp in _viewModel.MonitoredParameters)
                {
                    if (!string.IsNullOrEmpty(mp.DeviceId) && !deviceNameMap.ContainsKey(mp.DeviceId))
                        deviceNameMap[mp.DeviceId] = mp.DeviceName ?? "未知设备";
                }

                // 2. 构建参数项列表
                var allItems = new List<ParamItem>();
                int ci = 0;
                foreach (var mp in _viewModel.MonitoredParameters)
                {
                    if (string.IsNullOrEmpty(mp.DeviceId))
                    {
                        _logger.LogWarning("监控参数 DeviceId 为空,已跳过: {Key}", mp.Key);
                        continue;
                    }
                    var displayDeviceName = deviceNameMap.TryGetValue(mp.DeviceId, out var dn1)
                        ? dn1
                        : (mp.DeviceName ?? "未知设备");
                    allItems.Add(new ParamItem
                    {
                        Key = mp.Key,
                        DeviceId = mp.DeviceId,
                        DeviceName = displayDeviceName,
                        ShortName = GetShortName(mp.CommandName, mp.ParameterName),
                        Unit = GetStringProp(mp, "Unit"),
                        IsMonitored = true,
                        Monitored = mp,
                        ColorIdx = ci++,
                        Color = mp.Color,
                    });
                }

                var mKeys = new HashSet<string>(_viewModel.MonitoredParameters.Select(p => p.Key));
                foreach (var ap in _viewModel.AvailableParameters)
                {
                    if (mKeys.Contains(ap.Key)) continue;
                    if (string.IsNullOrEmpty(ap.DeviceId)) continue;

                    var displayDeviceName = deviceNameMap.TryGetValue(ap.DeviceId, out var dn2)
                        ? dn2
                        : (ap.DeviceName ?? "未知设备");
                    allItems.Add(new ParamItem
                    {
                        Key = ap.Key,
                        DeviceId = ap.DeviceId,
                        DeviceName = displayDeviceName,
                        ShortName = GetShortName(ap.CommandName, ap.ParameterName),
                        Unit = GetStringProp(ap, "Unit"),
                        IsMonitored = false,
                        Available = ap,
                        IsSelectable = ap.IsSelectable,        // ★ 新增
                        DisableReason = ap.DisableReason,      // ★ 新增
                    });
                }


                // 3. 严格按 DeviceId 分组
                var deviceGroups = allItems
                    .GroupBy(p => p.DeviceId)
                    .OrderBy(g => g.First().DeviceName);

                foreach (var grp in deviceGroups)
                {
                    var displayName = deviceNameMap.TryGetValue(grp.Key, out var dn)
                        ? dn
                        : (grp.First().DeviceName ?? "未知设备");

                    var monitoredCount = grp.Count(x => x.IsMonitored);
                    var totalCount = grp.Count();

                    var groupHeader = new Grid { Margin = new Thickness(12, 12, 12, 4) };
                    groupHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    groupHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                    var nameTb = new TextBlock
                    {
                        Text = displayName,
                        FontSize = 10,
                        FontWeight = FontWeights.Medium,
                        Foreground = new SolidColorBrush(TextTertiary),
                    };
                    Grid.SetColumn(nameTb, 0);
                    groupHeader.Children.Add(nameTb);

                    var countTb = new TextBlock
                    {
                        Text = $"{monitoredCount} / {totalCount}",
                        FontSize = 10,
                        Foreground = new SolidColorBrush(BorderStrong),
                    };
                    Grid.SetColumn(countTb, 1);
                    groupHeader.Children.Add(countTb);

                    ParameterListPanel.Children.Add(groupHeader);

                    var sortedItems = grp
                        .OrderByDescending(x => x.IsMonitored)        // 1. 已监控的最优先
                        .ThenByDescending(x => x.IsSelectable)        // 2. 可选(温度/压力/流量)其次
                        .ThenBy(x => x.ShortName);                    // 3. 字母排序
                    foreach (var item in sortedItems)
                    {
                        ParameterListPanel.Children.Add(CreateRow(item));
                    }

                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "构建参数列表失败");
            }
        }

        private string GetShortName(string cmd, string param)
        {
            if (string.IsNullOrEmpty(cmd) && string.IsNullOrEmpty(param)) return "Unknown";
            if (string.IsNullOrEmpty(cmd)) return param;
            if (string.IsNullOrEmpty(param)) return cmd;
            return $"{cmd}.{param}";
        }

        private System.Windows.Controls.Border CreateRow(ParamItem item)
        {
            // ★ 不可选(灰色) = 既不是已监控,也不是可选添加的参数
            bool isUnselectableAvailable = !item.IsMonitored && !item.IsSelectable;

            var row = new System.Windows.Controls.Border
            {
                Margin = new Thickness(8, 0, 8, 2),
                Padding = new Thickness(10, 8, 10, 8),
                CornerRadius = new CornerRadius(6),
                Cursor = isUnselectableAvailable ? Cursors.Arrow : Cursors.Hand,
            };

            // ★ Tooltip:仅在不可选时显示原因
            if (isUnselectableAvailable && !string.IsNullOrEmpty(item.DisableReason))
            {
                var tt = new ToolTip
                {
                    Content = new TextBlock
                    {
                        Text = item.DisableReason,
                        FontSize = 11,
                        TextWrapping = TextWrapping.Wrap,
                        MaxWidth = 280,
                        Foreground = new SolidColorBrush(TextSecondary),
                    },
                    Background = new SolidColorBrush(BgPrimary),
                    BorderBrush = new SolidColorBrush(BorderStrong),
                    BorderThickness = new Thickness(0.5),
                    Padding = new Thickness(10, 6, 10, 6),
                };
                row.ToolTip = tt;
                ToolTipService.SetInitialShowDelay(row, 250);
                ToolTipService.SetShowDuration(row, 8000);
            }

            if (item.IsMonitored)
            {
                row.Background = new SolidColorBrush(BgPrimary);
                row.BorderBrush = new SolidColorBrush(BorderDefault);
                row.BorderThickness = new Thickness(0.5);
            }
            else if (isUnselectableAvailable)
            {
                // ★ 不可选:更深的灰、不响应 hover
                row.Background = Brushes.Transparent;
                row.Opacity = 0.35;
            }
            else
            {
                row.Background = Brushes.Transparent;
                row.Opacity = 0.55;
            }

            // ★ 仅可选行响应 hover
            if (!isUnselectableAvailable)
            {
                row.MouseEnter += (s, e) =>
                {
                    if (!item.IsMonitored) row.Background = new SolidColorBrush(BgHover);
                };
                row.MouseLeave += (s, e) =>
                {
                    if (!item.IsMonitored) row.Background = Brushes.Transparent;
                };
            }

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // ═══ 列 0:色块 / 状态点 ═══
            if (item.IsMonitored)
            {
                Color lineColor;
                try { lineColor = (Color)ColorConverter.ConvertFromString(item.Color); }
                catch { lineColor = LinePalette[item.ColorIdx % LinePalette.Length]; }

                var sw = new System.Windows.Controls.Border
                {
                    Width = 8,
                    Height = 8,
                    CornerRadius = new CornerRadius(2),
                    Background = new SolidColorBrush(lineColor),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 8, 0),
                };
                Grid.SetColumn(sw, 0);
                grid.Children.Add(sw);
            }
            else if (isUnselectableAvailable)
            {
                // ★ 不可选用 ⊘ 符号代替色块
                var sw = new TextBlock
                {
                    Text = "⊘",
                    FontSize = 12,
                    FontWeight = FontWeights.Light,
                    Foreground = new SolidColorBrush(BorderStrong),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 6, 0),
                    Width = 12,
                    TextAlignment = TextAlignment.Center,
                };
                Grid.SetColumn(sw, 0);
                grid.Children.Add(sw);
            }
            else
            {
                var sw = new System.Windows.Controls.Border
                {
                    Width = 8,
                    Height = 8,
                    CornerRadius = new CornerRadius(2),
                    Background = Brushes.Transparent,
                    BorderBrush = new SolidColorBrush(BorderStrong),
                    BorderThickness = new Thickness(1),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 8, 0),
                };
                Grid.SetColumn(sw, 0);
                grid.Children.Add(sw);
            }

            // ═══ 列 1:名称 + 轴归属徽章 / 单位行 ═══
            var nameStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };

            var nameTb = new TextBlock
            {
                Text = item.ShortName,
                FontSize = 11,
                FontWeight = item.IsMonitored ? FontWeights.Medium : FontWeights.Normal,
                Foreground = new SolidColorBrush(item.IsMonitored ? TextPrimary : TextSecondary),
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            nameStack.Children.Add(nameTb);

            // ★ 不可选:显示单位/原因副标题(灰红色调)
            if (isUnselectableAvailable)
            {
                var subText = string.IsNullOrEmpty(item.Unit) ? "无单位" : $"单位: {item.Unit}";
                nameStack.Children.Add(new TextBlock
                {
                    Text = subText,
                    FontSize = 9,
                    Foreground = new SolidColorBrush(TextTertiary),
                    Margin = new Thickness(0, 2, 0, 0),
                });
            }
            else if (item.IsMonitored)
            {
                var axisType = ParameterAxisClassifier.Classify(item.Unit);

                bool hasBadge = axisType != AxisType.Unclassified;
                bool hasUnit = !string.IsNullOrWhiteSpace(item.Unit);

                if (hasBadge || hasUnit)
                {
                    var subRow = new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Margin = new Thickness(0, 2, 0, 0),
                    };

                    if (hasBadge)
                    {
                        var (bg, fg) = GetBadgeColors(axisType);
                        var badge = new System.Windows.Controls.Border
                        {
                            Background = new SolidColorBrush(bg),
                            CornerRadius = new CornerRadius(2),
                            Padding = new Thickness(4, 0, 4, 0),
                            Margin = new Thickness(0, 0, 4, 0),
                            VerticalAlignment = VerticalAlignment.Center,
                            Child = new TextBlock
                            {
                                Text = ParameterAxisClassifier.GetAxisBadge(axisType),
                                FontSize = 9,
                                FontWeight = FontWeights.Medium,
                                Foreground = new SolidColorBrush(fg),
                            },
                        };
                        subRow.Children.Add(badge);
                    }

                    string subText;
                    if (hasBadge)
                    {
                        subText = hasUnit
                            ? $"{ParameterAxisClassifier.GetAxisTitle(axisType)} · {item.Unit}"
                            : ParameterAxisClassifier.GetAxisTitle(axisType);
                    }
                    else
                    {
                        subText = item.Unit;
                    }

                    subRow.Children.Add(new TextBlock
                    {
                        Text = subText,
                        FontSize = 9,
                        Foreground = new SolidColorBrush(TextTertiary),
                        VerticalAlignment = VerticalAlignment.Center,
                    });

                    nameStack.Children.Add(subRow);
                }
            }

            Grid.SetColumn(nameStack, 1);
            grid.Children.Add(nameStack);

            // ═══ 列 2:当前值 / + 号 / 灰色提示 ═══
            if (item.IsMonitored)
            {
                Color valColor;
                try { valColor = (Color)ColorConverter.ConvertFromString(item.Color); }
                catch { valColor = LinePalette[item.ColorIdx % LinePalette.Length]; }

                var mp = item.Monitored;
                var val = new TextBlock
                {
                    Text = SafeGetFormattedValue(mp),
                    FontSize = 12,
                    FontWeight = FontWeights.Medium,
                    FontFamily = new FontFamily("Consolas"),
                    Foreground = new SolidColorBrush(valColor),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(8, 0, 0, 0),
                };
                mp.PropertyChanged += (s, e) =>
                {
                    if (e.PropertyName == "CurrentValue" || e.PropertyName == "FormattedCurrentValue")
                        Dispatcher.BeginInvoke(new Action(() => val.Text = SafeGetFormattedValue(mp)));
                };
                Grid.SetColumn(val, 2);
                grid.Children.Add(val);

                row.MouseLeftButtonUp += (s, e) =>
                {
                    if (_viewModel?.RemoveParameterCommand != null &&
                        _viewModel.RemoveParameterCommand.CanExecute(mp))
                    {
                        _viewModel.RemoveParameterCommand.Execute(mp);
                    }
                };
            }
            else if (isUnselectableAvailable)
            {
                // ★ 不可选:显示一个提示问号,告诉用户可以悬停查看原因
                var info = new TextBlock
                {
                    Text = "?",
                    FontSize = 11,
                    FontWeight = FontWeights.Medium,
                    Foreground = new SolidColorBrush(BorderStrong),
                    VerticalAlignment = VerticalAlignment.Center,
                    Width = 14,
                    Height = 14,
                    TextAlignment = TextAlignment.Center,
                };
                Grid.SetColumn(info, 2);
                grid.Children.Add(info);

                // ★ 不响应点击 - 不绑定 MouseLeftButtonUp
            }
            else
            {
                var add = new TextBlock
                {
                    Text = "+",
                    FontSize = 14,
                    FontWeight = FontWeights.Light,
                    Foreground = new SolidColorBrush(BorderStrong),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                Grid.SetColumn(add, 2);
                grid.Children.Add(add);

                row.MouseLeftButtonUp += (s, e) =>
                {
                    if (_viewModel?.AddParameterCommand != null &&
                        item.Available != null &&
                        _viewModel.AddParameterCommand.CanExecute(item.Available))
                    {
                        _viewModel.AddParameterCommand.Execute(item.Available);
                    }
                };
            }

            row.Child = grid;
            return row;
        }

        /// <summary>
        /// 安全读取 MonitoredParameter 的当前值显示文本
        /// </summary>
        private static string SafeGetFormattedValue(MonitoredParameter mp)
        {
            if (mp == null) return "";
            try
            {
                var formatted = GetStringProp(mp, "FormattedCurrentValue");
                if (!string.IsNullOrEmpty(formatted)) return formatted;

                var prop = mp.GetType().GetProperty("CurrentValue",
                    BindingFlags.Public | BindingFlags.Instance);
                if (prop != null)
                {
                    var v = prop.GetValue(mp);
                    if (v == null) return "";
                    if (v is double d) return d.ToString("F2");
                    return v.ToString();
                }
            }
            catch { }
            return "";
        }

        private static (Color bg, Color fg) GetBadgeColors(AxisType axisType)
        {
            switch (axisType)
            {
                case AxisType.Temperature: return (BadgeBgT, BadgeFgT);
                case AxisType.Pressure: return (BadgeBgP, BadgeFgP);
                case AxisType.Flow: return (BadgeBgF, BadgeFgF);
                default: return (BorderDefault, TextSecondary);
            }
        }

        private class ParamItem
        {
            public string Key { get; set; }
            public string DeviceName { get; set; }
            public string DeviceId { get; set; }
            public string ShortName { get; set; }
            public string Unit { get; set; }
            public string Color { get; set; }
            public bool IsMonitored;
            public MonitoredParameter Monitored;
            public AvailableParameter Available;
            public int ColorIdx;

            // ★ 监控可用性元数据(仅对未监控的可用参数有意义)
            public bool IsSelectable { get; set; } = true;
            public string DisableReason { get; set; }
        }


        #endregion

        #region 工具栏按钮

        private void TimeRange_Checked(object sender, RoutedEventArgs e)
        {
            if (!_isLoaded) return;
        }

        private void BtnResetAxis_Click(object sender, RoutedEventArgs e)
        {
            // ★ 修复:DelegateCommand 是无参的,不能传 null
            if (_viewModel?.ResetAxisRangesCommand?.CanExecute() == true)
                _viewModel.ResetAxisRangesCommand.Execute();
        }

        private void BtnClearData_Click(object sender, RoutedEventArgs e)
        {
            // ★ 修复:DelegateCommand 是无参的
            if (_viewModel?.ClearDataCommand?.CanExecute() == true)
                _viewModel.ClearDataCommand.Execute();
        }

        private void BtnExport_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_viewModel == null)
                {
                    MessageBox.Show("视图模型未就绪", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var enabled = _viewModel.MonitoredParameters?.Where(p => p.IsEnabled).ToList();
                var data = _viewModel.ChartData?.OrderBy(d => d.Timestamp).ToList();

                if (enabled == null || enabled.Count == 0 || data == null || data.Count == 0)
                {
                    MessageBox.Show("没有可导出的数据", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var dlg = new SaveFileDialog
                {
                    Title = "导出监控数据",
                    Filter = "Excel文件 (*.xlsx)|*.xlsx",
                    FileName = $"参数监控报告_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx",
                };
                if (dlg.ShowDialog() != true) return;

                ExcelPackage.LicenseContext = LicenseContext.NonCommercial;

                using (var pkg = new ExcelPackage())
                {
                    var sheet = pkg.Workbook.Worksheets.Add("监控数据");

                    sheet.Column(1).Width = 28;
                    for (int i = 0; i < enabled.Count; i++)
                        sheet.Column(i + 2).Width = 18;

                    int row = 1;

                    sheet.Cells[row, 1, row, enabled.Count + 1].Merge = true;
                    sheet.Cells[row, 1].Value = "MaxChemical 参数监控数据报告";
                    sheet.Cells[row, 1].Style.Font.Size = 16;
                    sheet.Cells[row, 1].Style.Font.Bold = true;
                    sheet.Cells[row, 1].Style.Font.Color.SetColor(DrawingColor.FromArgb(0xFF, 0xFF, 0xFF));
                    sheet.Cells[row, 1].Style.Fill.PatternType = ExcelFillStyle.Solid;
                    sheet.Cells[row, 1].Style.Fill.BackgroundColor.SetColor(DrawingColor.FromArgb(0xA4, 0x16, 0x26));
                    sheet.Cells[row, 1].Style.HorizontalAlignment = ExcelHorizontalAlignment.Center;
                    sheet.Row(row).Height = 26;
                    row++;

                    sheet.Cells[row, 1].Value = $"导出时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}";
                    sheet.Cells[row, 1].Style.Font.Color.SetColor(DrawingColor.Gray);
                    sheet.Cells[row, 1].Style.Font.Size = 10;
                    row++;
                    sheet.Cells[row, 1].Value = $"数据范围: {data.First().Timestamp:HH:mm:ss} ~ {data.Last().Timestamp:HH:mm:ss} ({data.Count} 条记录)";
                    sheet.Cells[row, 1].Style.Font.Color.SetColor(DrawingColor.Gray);
                    sheet.Cells[row, 1].Style.Font.Size = 10;
                    row += 2;

                    int headerRow = row;

                    sheet.Cells[row, 1].Value = "时间";
                    sheet.Cells[row, 1].Style.Font.Bold = true;
                    sheet.Cells[row, 1].Style.Fill.PatternType = ExcelFillStyle.Solid;
                    sheet.Cells[row, 1].Style.Fill.BackgroundColor.SetColor(DrawingColor.FromArgb(0xF1, 0xEF, 0xE8));

                    for (int i = 0; i < enabled.Count; i++)
                    {
                        var cell = sheet.Cells[row, i + 2];
                        cell.Value = enabled[i].DisplayName;
                        cell.Style.Font.Bold = true;
                        cell.Style.Fill.PatternType = ExcelFillStyle.Solid;
                        var ec = ExcelColors[i % ExcelColors.Length];
                        cell.Style.Fill.BackgroundColor.SetColor(DrawingColor.FromArgb(
                            (int)(ec.R * 0.18 + 255 * 0.82),
                            (int)(ec.G * 0.18 + 255 * 0.82),
                            (int)(ec.B * 0.18 + 255 * 0.82)));
                        cell.Style.Font.Color.SetColor(ec);
                        cell.Style.HorizontalAlignment = ExcelHorizontalAlignment.Center;
                    }
                    row++;

                    foreach (var dp in data)
                    {
                        sheet.Cells[row, 1].Value = dp.Timestamp;
                        sheet.Cells[row, 1].Style.Numberformat.Format = "yyyy-mm-dd hh:mm:ss.000";
                        for (int i = 0; i < enabled.Count; i++)
                        {
                            if (dp.ParameterValues.TryGetValue(enabled[i].DisplayName, out var val))
                            {
                                sheet.Cells[row, i + 2].Value = val;
                                sheet.Cells[row, i + 2].Style.Numberformat.Format = "0.0000";
                                sheet.Cells[row, i + 2].Style.HorizontalAlignment = ExcelHorizontalAlignment.Center;
                            }
                        }
                        row++;
                    }

                    var dataRange = sheet.Cells[headerRow, 1, row - 1, enabled.Count + 1];
                    dataRange.Style.Border.Top.Style = ExcelBorderStyle.Thin;
                    dataRange.Style.Border.Bottom.Style = ExcelBorderStyle.Thin;
                    dataRange.Style.Border.Left.Style = ExcelBorderStyle.Thin;
                    dataRange.Style.Border.Right.Style = ExcelBorderStyle.Thin;
                    dataRange.Style.Border.Top.Color.SetColor(DrawingColor.FromArgb(0xEA, 0xEA, 0xEA));
                    dataRange.Style.Border.Bottom.Color.SetColor(DrawingColor.FromArgb(0xEA, 0xEA, 0xEA));
                    dataRange.Style.Border.Left.Color.SetColor(DrawingColor.FromArgb(0xEA, 0xEA, 0xEA));
                    dataRange.Style.Border.Right.Color.SetColor(DrawingColor.FromArgb(0xEA, 0xEA, 0xEA));

                    sheet.View.FreezePanes(headerRow + 1, 1);
                    sheet.Cells[headerRow, 1, headerRow, enabled.Count + 1].AutoFilter = true;

                    pkg.SaveAs(new FileInfo(dlg.FileName));
                }

                var result = MessageBox.Show(
                    $"导出成功!\n{dlg.FileName}\n\n是否立即打开?",
                    "导出完成", MessageBoxButton.YesNo, MessageBoxImage.Information);
                if (result == MessageBoxResult.Yes)
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = dlg.FileName,
                        UseShellExecute = true,
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "导出Excel失败");
                MessageBox.Show($"导出失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        #endregion
    }
}
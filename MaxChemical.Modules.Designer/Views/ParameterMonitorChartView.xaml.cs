using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

        private static readonly (Color Light, Color Dark)[] Palette =
        {
            (Color.FromRgb(254, 153, 153), Color.FromRgb(239, 68, 68)),
            (Color.FromRgb(165, 243, 252), Color.FromRgb(6, 182, 212)),
            (Color.FromRgb(134, 239, 172), Color.FromRgb(34, 197, 94)),
            (Color.FromRgb(253, 230, 138), Color.FromRgb(245, 158, 11)),
            (Color.FromRgb(196, 181, 253), Color.FromRgb(139, 92, 246)),
            (Color.FromRgb(199, 210, 254), Color.FromRgb(99, 102, 241)),
            (Color.FromRgb(191, 219, 254), Color.FromRgb(59, 130, 246)),
            (Color.FromRgb(254, 215, 170), Color.FromRgb(249, 115, 22)),
        };

        private static readonly DrawingColor[] ExcelColors =
        {
            DrawingColor.FromArgb(239, 68, 68),
            DrawingColor.FromArgb(6, 182, 212),
            DrawingColor.FromArgb(34, 197, 94),
            DrawingColor.FromArgb(245, 158, 11),
            DrawingColor.FromArgb(139, 92, 246),
            DrawingColor.FromArgb(99, 102, 241),
            DrawingColor.FromArgb(59, 130, 246),
            DrawingColor.FromArgb(249, 115, 22),
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

        #region 左侧参数面板（完整保留原逻辑）

        private void RebuildParameterList()
        {
            if (_viewModel == null || ParameterListPanel == null) return;
            try
            {
                ParameterListPanel.Children.Clear();
                var allItems = new List<ParamItem>();

                int ci = 0;
                foreach (var mp in _viewModel.MonitoredParameters)
                    allItems.Add(new ParamItem
                    {
                        Key = mp.Key,
                        DeviceName = mp.DeviceName ?? "Unknown",
                        ShortName = GetShortName(mp.CommandName, mp.ParameterName),
                        IsMonitored = true,
                        Monitored = mp,
                        ColorIdx = ci++,
                        Color = mp.Color
                    });

                var mKeys = new HashSet<string>(_viewModel.MonitoredParameters.Select(p => p.Key));
                foreach (var ap in _viewModel.AvailableParameters)
                    if (!mKeys.Contains(ap.Key))
                        allItems.Add(new ParamItem
                        {
                            Key = ap.Key,
                            DeviceName = ap.DeviceName ?? "Unknown",
                            ShortName = GetShortName(ap.CommandName, ap.ParameterName),
                            IsMonitored = false,
                            Available = ap
                        });

                foreach (var grp in allItems.GroupBy(p => p.DeviceName))
                {
                    // 设备分组标题
                    ParameterListPanel.Children.Add(new TextBlock
                    {
                        Text = grp.Key,
                        FontSize = 9,
                        Foreground = new SolidColorBrush(Color.FromRgb(0x9C, 0xA3, 0xAF)),
                        Margin = new Thickness(12, 8, 12, 2)
                    });
                    ParameterListPanel.Children.Add(new Rectangle
                    {
                        Height = 0.5,
                        Fill = new SolidColorBrush(Color.FromRgb(0xE8, 0xEA, 0xED)),
                        Margin = new Thickness(12, 0, 12, 4)
                    });

                    foreach (var item in grp)
                        ParameterListPanel.Children.Add(CreateRow(item));
                }
            }
            catch (Exception ex) { _logger.LogError(ex, "构建参数列表失败"); }
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
            var border = new System.Windows.Controls.Border
            {
                Padding = new Thickness(12, 5, 10, 5),
                CornerRadius = new CornerRadius(5),
                Margin = new Thickness(4, 0, 4, 1),
                Cursor = Cursors.Hand,
                Background = Brushes.Transparent
            };

            border.MouseEnter += (s, e) => border.Background = new SolidColorBrush(
                item.IsMonitored ? Color.FromRgb(0xE8, 0xEA, 0xF6) : Color.FromRgb(0xF8, 0xF9, 0xFB));
            border.MouseLeave += (s, e) => border.Background = Brushes.Transparent;

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            if (item.IsMonitored)
            {
                Color dc;
                try { dc = (Color)ColorConverter.ConvertFromString(item.Color); }
                catch { dc = Palette[item.ColorIdx % Palette.Length].Dark; }

                var dot = new System.Windows.Controls.Border
                {
                    Width = 6,
                    Height = 6,
                    CornerRadius = new CornerRadius(2),
                    Background = new SolidColorBrush(dc),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 6, 0)
                };
                Grid.SetColumn(dot, 0);
                grid.Children.Add(dot);

                var name = new TextBlock
                {
                    Text = item.ShortName,
                    FontSize = 10,
                    FontWeight = FontWeights.Medium,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x1A, 0x1F, 0x36)),
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis
                };
                Grid.SetColumn(name, 1);
                grid.Children.Add(name);

                var mp = item.Monitored;
                var val = new TextBlock
                {
                    Text = mp.FormattedCurrentValue,
                    FontSize = 10,
                    FontWeight = FontWeights.Bold,
                    FontFamily = new FontFamily("Consolas"),
                    Foreground = new SolidColorBrush(dc),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(6, 0, 0, 0)
                };
                // 实时更新值
                mp.PropertyChanged += (s, e) =>
                {
                    if (e.PropertyName == nameof(mp.CurrentValue) || e.PropertyName == nameof(mp.FormattedCurrentValue))
                        Dispatcher.BeginInvoke(new Action(() => val.Text = mp.FormattedCurrentValue));
                };
                Grid.SetColumn(val, 2);
                grid.Children.Add(val);

                // 点击移除参数
                border.MouseLeftButtonUp += (s, e) =>
                {
                    if (_viewModel.RemoveParameterCommand.CanExecute(mp))
                        _viewModel.RemoveParameterCommand.Execute(mp);
                };
            }
            else
            {
                var dot = new System.Windows.Controls.Border
                {
                    Width = 6,
                    Height = 6,
                    CornerRadius = new CornerRadius(2),
                    Background = Brushes.Transparent,
                    BorderBrush = new SolidColorBrush(Color.FromRgb(0xC7, 0xCC, 0xD4)),
                    BorderThickness = new Thickness(1),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 6, 0)
                };
                Grid.SetColumn(dot, 0);
                grid.Children.Add(dot);

                var name = new TextBlock
                {
                    Text = item.ShortName,
                    FontSize = 10,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x9C, 0xA3, 0xAF)),
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis
                };
                Grid.SetColumn(name, 1);
                grid.Children.Add(name);

                var addText = new TextBlock
                {
                    Text = "+",
                    FontSize = 10,
                    Foreground = new SolidColorBrush(Color.FromRgb(0xC7, 0xCC, 0xD4)),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                Grid.SetColumn(addText, 2);
                grid.Children.Add(addText);

                // 点击添加参数
                border.MouseLeftButtonUp += (s, e) =>
                {
                    if (_viewModel.AddParameterCommand.CanExecute(item.Available))
                        _viewModel.AddParameterCommand.Execute(item.Available);
                };
            }

            border.Child = grid;
            return border;
        }

        private class ParamItem
        {
            public string Key, DeviceName, ShortName, Color;
            public bool IsMonitored;
            public MonitoredParameter Monitored;
            public AvailableParameter Available;
            public int ColorIdx;
        }

        #endregion

        #region 时间范围切换

        private void TimeRange_Checked(object sender, RoutedEventArgs e)
        {
            if (!_isLoaded) return;
            // 时间范围切换逻辑（如果需要在 code-behind 做时间过滤可在这里处理）
            // OxyPlot 的 DateTimeAxis 可以通过设置 Minimum/Maximum 实现时间范围过滤
        }

        #endregion

        #region 底部按钮

        private void BtnClearData_Click(object sender, RoutedEventArgs e)
        {
            if (_viewModel?.ClearDataCommand?.CanExecute() == true)
                _viewModel.ClearDataCommand.Execute();
        }

        private void BtnExport_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var enabled = _viewModel?.MonitoredParameters?.Where(p => p.IsEnabled).ToList();
                var data = _viewModel?.ChartData?.OrderBy(d => d.Timestamp).ToList();

                if (enabled == null || enabled.Count == 0 || data == null || data.Count == 0)
                {
                    MessageBox.Show("没有可导出的数据", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var dlg = new SaveFileDialog
                {
                    Title = "导出监控数据",
                    Filter = "Excel文件 (*.xlsx)|*.xlsx",
                    FileName = $"参数监控报告_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx"
                };
                if (dlg.ShowDialog() != true) return;

                ExcelPackage.LicenseContext = LicenseContext.NonCommercial;

                using (var pkg = new ExcelPackage())
                {
                    var sheet = pkg.Workbook.Worksheets.Add("监控数据");

                    // 列宽
                    sheet.Column(1).Width = 28;
                    for (int i = 0; i < enabled.Count; i++)
                        sheet.Column(i + 2).Width = 18;

                    int row = 1;

                    // 标题
                    sheet.Cells[row, 1, row, enabled.Count + 1].Merge = true;
                    sheet.Cells[row, 1].Value = "参数监控数据报告";
                    sheet.Cells[row, 1].Style.Font.Size = 16;
                    sheet.Cells[row, 1].Style.Font.Bold = true;
                    sheet.Cells[row, 1].Style.HorizontalAlignment = ExcelHorizontalAlignment.Center;
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

                    // 表头
                    sheet.Cells[row, 1].Value = "时间";
                    sheet.Cells[row, 1].Style.Font.Bold = true;
                    sheet.Cells[row, 1].Style.Fill.PatternType = ExcelFillStyle.Solid;
                    sheet.Cells[row, 1].Style.Fill.BackgroundColor.SetColor(DrawingColor.FromArgb(240, 240, 240));

                    for (int i = 0; i < enabled.Count; i++)
                    {
                        var cell = sheet.Cells[row, i + 2];
                        cell.Value = enabled[i].DisplayName;
                        cell.Style.Font.Bold = true;
                        cell.Style.Fill.PatternType = ExcelFillStyle.Solid;
                        var ec = ExcelColors[i % ExcelColors.Length];
                        cell.Style.Fill.BackgroundColor.SetColor(DrawingColor.FromArgb(
                            (int)(ec.R * 0.3 + 255 * 0.7),
                            (int)(ec.G * 0.3 + 255 * 0.7),
                            (int)(ec.B * 0.3 + 255 * 0.7)));
                        cell.Style.Font.Color.SetColor(ec);
                        cell.Style.HorizontalAlignment = ExcelHorizontalAlignment.Center;
                    }
                    row++;

                    // 数据行
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

                    // 边框
                    var dataRange = sheet.Cells[headerRow, 1, row - 1, enabled.Count + 1];
                    dataRange.Style.Border.Top.Style = ExcelBorderStyle.Thin;
                    dataRange.Style.Border.Bottom.Style = ExcelBorderStyle.Thin;
                    dataRange.Style.Border.Left.Style = ExcelBorderStyle.Thin;
                    dataRange.Style.Border.Right.Style = ExcelBorderStyle.Thin;
                    dataRange.Style.Border.Top.Color.SetColor(DrawingColor.FromArgb(220, 220, 220));
                    dataRange.Style.Border.Bottom.Color.SetColor(DrawingColor.FromArgb(220, 220, 220));
                    dataRange.Style.Border.Left.Color.SetColor(DrawingColor.FromArgb(220, 220, 220));
                    dataRange.Style.Border.Right.Color.SetColor(DrawingColor.FromArgb(220, 220, 220));

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
                        UseShellExecute = true
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
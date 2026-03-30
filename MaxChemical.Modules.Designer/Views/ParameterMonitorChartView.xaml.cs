using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using MaxChemical.Modules.Designer.Models;
using MaxChemical.Modules.Designer.ViewModels;
using MaxChemical.Logging;
using Microsoft.Win32;
using OfficeOpenXml;
using OfficeOpenXml.Drawing;
using OfficeOpenXml.Drawing.Chart;
using OfficeOpenXml.Style;
using Color = System.Windows.Media.Color;
using DrawingColor = System.Drawing.Color;

namespace MaxChemical.Modules.Designer.Views
{
    public partial class ParameterMonitorChartView : UserControl
    {
        private ParameterMonitorChartViewModel _viewModel;
        private readonly ILogService _logger;
        private DispatcherTimer _renderTimer;
        private DateTime _lastRenderTime = DateTime.MinValue;
        private int _timeRangeMinutes = 30;
        private int _activeYAxisIndex = 0;

        private const double ML = 50, MR = 20, MT = 16, MB = 28;

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

        // Excel 对应颜色
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
            RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);

            _renderTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _renderTimer.Tick += (s, e) => RenderChart();

            ChartCanvas.MouseMove += OnChartMouseMove;
            ChartCanvas.MouseLeave += OnChartMouseLeave;
            ChartCanvas.SizeChanged += (s, e) => RenderChart();

            Rb5m.Checked += (s, e) => { _timeRangeMinutes = 5; RenderChart(); };
            Rb15m.Checked += (s, e) => { _timeRangeMinutes = 15; RenderChart(); };
            Rb30m.Checked += (s, e) => { _timeRangeMinutes = 30; RenderChart(); };
            Rb1h.Checked += (s, e) => { _timeRangeMinutes = 60; RenderChart(); };
            RbAll.Checked += (s, e) => { _timeRangeMinutes = 0; RenderChart(); };

            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        #region Lifecycle

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            _viewModel = DataContext as ParameterMonitorChartViewModel;
            if (_viewModel == null) return;

            _viewModel.PropertyChanged += (s, ev) =>
            {
                if (ev.PropertyName == nameof(_viewModel.HasData) ||
                    ev.PropertyName == nameof(_viewModel.ChartData))
                    RenderChart();
            };
            _viewModel.MonitoredParameters.CollectionChanged += (s, ev) =>
            {
                _activeYAxisIndex = 0;
                RebuildParameterList();
                RenderChart();
            };
            _viewModel.AvailableParameters.CollectionChanged += (s, ev) => RebuildParameterList();

            RebuildParameterList();
            _renderTimer.Start();
            RenderChart();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e) => _renderTimer?.Stop();

        #endregion

        #region 左侧参数面板

        private void RebuildParameterList()
        {
            if (_viewModel == null || ParameterListPanel == null) return;
            try
            {
                ParameterListPanel.Children.Clear();
                var allItems = new List<ParamItem>();

                int ci = 0;
                foreach (var mp in _viewModel.MonitoredParameters)
                    allItems.Add(new ParamItem { Key = mp.Key, DeviceName = mp.DeviceName ?? "Unknown", ShortName = GetShortName(mp.CommandName, mp.ParameterName), IsMonitored = true, Monitored = mp, ColorIdx = ci++, Color = mp.Color });

                var mKeys = new HashSet<string>(_viewModel.MonitoredParameters.Select(p => p.Key));
                foreach (var ap in _viewModel.AvailableParameters)
                    if (!mKeys.Contains(ap.Key))
                        allItems.Add(new ParamItem { Key = ap.Key, DeviceName = ap.DeviceName ?? "Unknown", ShortName = GetShortName(ap.CommandName, ap.ParameterName), IsMonitored = false, Available = ap });

                foreach (var grp in allItems.GroupBy(p => p.DeviceName))
                {
                    ParameterListPanel.Children.Add(new TextBlock { Text = grp.Key, FontSize = 11, FontStyle = FontStyles.Italic, Foreground = new SolidColorBrush(Color.FromRgb(140, 140, 140)), Margin = new Thickness(16, 10, 16, 2) });
                    ParameterListPanel.Children.Add(new Rectangle { Height = 1, Fill = new SolidColorBrush(Color.FromRgb(240, 241, 243)), Margin = new Thickness(16, 0, 16, 4) });
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
            var border = new System.Windows.Controls.Border { Padding = new Thickness(16, 8, 12, 8), CornerRadius = new CornerRadius(6), Margin = new Thickness(6, 1, 6, 1), Cursor = Cursors.Hand, Background = Brushes.Transparent };
            border.MouseEnter += (s, e) => border.Background = new SolidColorBrush(item.IsMonitored ? Color.FromRgb(240, 245, 255) : Color.FromRgb(245, 245, 245));
            border.MouseLeave += (s, e) => border.Background = Brushes.Transparent;

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            if (item.IsMonitored)
            {
                Color dc;
                try { dc = (Color)ColorConverter.ConvertFromString(item.Color); } catch { dc = Palette[item.ColorIdx % Palette.Length].Dark; }

                var dot = new Ellipse { Width = 10, Height = 10, Fill = new SolidColorBrush(dc), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
                Grid.SetColumn(dot, 0); grid.Children.Add(dot);
                var name = new TextBlock { Text = item.ShortName, FontSize = 13, FontWeight = FontWeights.Medium, Foreground = new SolidColorBrush(Color.FromRgb(38, 38, 38)), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
                Grid.SetColumn(name, 1); grid.Children.Add(name);

                var mp = item.Monitored;
                var val = new TextBlock { Text = mp.FormattedCurrentValue, FontSize = 14, FontWeight = FontWeights.Bold, FontFamily = new FontFamily("Consolas"), Foreground = new SolidColorBrush(dc), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
                mp.PropertyChanged += (s, e) =>
                {
                    if (e.PropertyName == nameof(mp.CurrentValue) || e.PropertyName == nameof(mp.FormattedCurrentValue))
                        Dispatcher.BeginInvoke(new Action(() => val.Text = mp.FormattedCurrentValue));
                };
                Grid.SetColumn(val, 2); grid.Children.Add(val);
                border.MouseLeftButtonUp += (s, e) => { if (_viewModel.RemoveParameterCommand.CanExecute(mp)) _viewModel.RemoveParameterCommand.Execute(mp); };
            }
            else
            {
                var dot = new Ellipse { Width = 10, Height = 10, Stroke = new SolidColorBrush(Color.FromRgb(191, 191, 191)), StrokeThickness = 1.5, Fill = Brushes.Transparent, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
                Grid.SetColumn(dot, 0); grid.Children.Add(dot);
                var name = new TextBlock { Text = item.ShortName, FontSize = 13, Foreground = new SolidColorBrush(Color.FromRgb(191, 191, 191)), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
                Grid.SetColumn(name, 1); grid.Children.Add(name);
                border.MouseLeftButtonUp += (s, e) => { if (_viewModel.AddParameterCommand.CanExecute(item.Available)) _viewModel.AddParameterCommand.Execute(item.Available); };
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

        #region 底部按钮

        private void BtnClearData_Click(object sender, RoutedEventArgs e)
        {
            if (_viewModel?.ClearDataCommand?.CanExecute() == true)
                _viewModel.ClearDataCommand.Execute();
        }

        /// <summary>
        /// 导出 Excel：Sheet1=数据表，Sheet2=趋势图表（截图+原生Excel折线图）
        /// </summary>
        private void BtnExport_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var enabled = _viewModel?.MonitoredParameters?.Where(p => p.IsEnabled).ToList();
                var data = GetFilteredData();
                if (enabled == null || enabled.Count == 0 || data.Count == 0)
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
                    // ─── Sheet1: 监控数据 ───
                    var dataSheet = pkg.Workbook.Worksheets.Add("监控数据");
                    int dataHeaderRow = FillDataSheet(dataSheet, enabled, data);

                    // ─── Sheet2: 趋势图表 ───
                    var chartSheet = pkg.Workbook.Worksheets.Add("趋势图表");
                    FillChartSheet(chartSheet, dataSheet, enabled, data, dataHeaderRow);

                    pkg.SaveAs(new FileInfo(dlg.FileName));
                }

                // 询问是否打开
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

        /// <summary>
        /// Sheet1: 数据表（时间戳 + 各参数列 + 条件格式颜色标记）
        /// </summary>
        private int FillDataSheet(ExcelWorksheet sheet, List<MonitoredParameter> enabled, List<ChartDataPoint> data)
        {
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

            // 导出信息
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

                // 表头颜色和曲线颜色对应
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

            // 冻结表头
            sheet.View.FreezePanes(headerRow + 1, 1);

            // 自动筛选
            sheet.Cells[headerRow, 1, headerRow, enabled.Count + 1].AutoFilter = true;

            return headerRow;
        }

        /// <summary>
        /// Sheet2: 趋势图表（Canvas截图 + 原生Excel散点折线图）
        /// 每个参数单独写一段数据（只有有值的行），避免空值问题
        /// </summary>
        private void FillChartSheet(ExcelWorksheet cs, ExcelWorksheet dataSheet,
            List<MonitoredParameter> enabled, List<ChartDataPoint> data, int dataHeaderRow)
        {
            int row = 1;

            // 标题
            cs.Cells[row, 1, row, 8].Merge = true;
            cs.Cells[row, 1].Value = "参数趋势图表";
            cs.Cells[row, 1].Style.Font.Size = 16;
            cs.Cells[row, 1].Style.Font.Bold = true;
            cs.Cells[row, 1].Style.HorizontalAlignment = ExcelHorizontalAlignment.Center;
            row += 2;

            // ─── 1. 嵌入 Canvas 截图 ───
            cs.Cells[row, 1].Value = "实时监控截图";
            cs.Cells[row, 1].Style.Font.Bold = true;
            cs.Cells[row, 1].Style.Font.Size = 13;
            cs.Cells[row, 1].Style.Font.Color.SetColor(DrawingColor.FromArgb(99, 102, 241));
            row++;

            try
            {
                var imgBytes = RenderChartToPng();
                if (imgBytes != null && imgBytes.Length > 0)
                {
                    using (var ms = new MemoryStream(imgBytes))
                    {
                        var pic = cs.Drawings.AddPicture("ChartScreenshot", ms);
                        pic.SetPosition(row - 1, 0, 0, 0);
                        pic.SetSize(900, 450);
                    }
                    row += 24;
                }
            }
            catch (Exception ex)
            {
                cs.Cells[row, 1].Value = $"截图失败: {ex.Message}";
                row += 2;
            }

            row += 2;

            // ─── 2. 为每个参数写独立的数据块（时间用 OADate 数值） ───
            cs.Cells[row, 1].Value = "Excel 原生折线图（可交互）";
            cs.Cells[row, 1].Style.Font.Bold = true;
            cs.Cells[row, 1].Style.Font.Size = 13;
            cs.Cells[row, 1].Style.Font.Color.SetColor(DrawingColor.FromArgb(99, 102, 241));
            row++;

            int chartRow = row; // 记录图表放置位置
            row++; // 图表会占位，数据写在图表下方

            // 图表先创建
            var chart = cs.Drawings.AddChart("TrendChart", eChartType.XYScatterLinesNoMarkers) as ExcelScatterChart;
            chart.Title.Text = "参数趋势监控";
            chart.Title.Font.Bold = true;
            chart.Title.Font.Size = 14;
            chart.SetPosition(chartRow - 1, 0, 0, 0);
            chart.SetSize(900, 450);
            chart.Style = eChartStyle.Style2;
            chart.Legend.Position = eLegendPosition.Bottom;

            // 数据块写在图表下方
            int dataBlockStartRow = chartRow + 26;

            for (int i = 0; i < enabled.Count; i++)
            {
                var param = enabled[i];
                var paramName = param.DisplayName;

                // 筛选出该参数有值的数据点
                var paramData = data
                    .Where(d => d.ParameterValues.ContainsKey(paramName) && d.ParameterValues[paramName] != 0)
                    .OrderBy(d => d.Timestamp)
                    .ToList();

                // 如果全是0，也包含进来（说明确实采集到了0值）
                if (!paramData.Any())
                {
                    paramData = data
                        .Where(d => d.ParameterValues.ContainsKey(paramName))
                        .OrderBy(d => d.Timestamp)
                        .ToList();
                }

                if (!paramData.Any()) continue;

                // 两列：时间(OADate) + 值
                int colX = 1 + i * 3;       // 每个参数占3列（时间、值、空列间隔）
                int colY = colX + 1;
                int headerR = dataBlockStartRow;

                // 表头
                cs.Cells[headerR, colX].Value = $"时间({paramName})";
                cs.Cells[headerR, colX].Style.Font.Bold = true;
                cs.Cells[headerR, colX].Style.Font.Size = 9;
                cs.Cells[headerR, colY].Value = paramName;
                cs.Cells[headerR, colY].Style.Font.Bold = true;
                cs.Cells[headerR, colY].Style.Font.Size = 9;
                cs.Cells[headerR, colY].Style.Font.Color.SetColor(ExcelColors[i % ExcelColors.Length]);

                cs.Column(colX).Width = 18;
                cs.Column(colY).Width = 14;

                // 数据行
                for (int j = 0; j < paramData.Count; j++)
                {
                    int r = headerR + 1 + j;
                    // ★ 关键：用 ToOADate() 写入数值，Excel XYScatter 才能正确识别为时间
                    cs.Cells[r, colX].Value = paramData[j].Timestamp.ToOADate();
                    cs.Cells[r, colX].Style.Numberformat.Format = "HH:mm:ss";
                    cs.Cells[r, colY].Value = paramData[j].ParameterValues[paramName];
                    cs.Cells[r, colY].Style.Numberformat.Format = "0.00";
                }

                int seriesStartR = headerR + 1;
                int seriesEndR = headerR + paramData.Count;

                // 添加到图表
                var xRange = cs.Cells[seriesStartR, colX, seriesEndR, colX];
                var yRange = cs.Cells[seriesStartR, colY, seriesEndR, colY];
                var series = chart.Series.Add(yRange, xRange);
                series.Header = paramName;

                // 线条颜色
                if (series is ExcelScatterChartSerie sc)
                {
                    var ec = ExcelColors[i % ExcelColors.Length];
                    sc.Border.Fill.Color = ec;
                    sc.Border.Width = 2;
                }
            }

            // X轴格式
            chart.XAxis.Title.Text = "时间";
            chart.XAxis.Title.Font.Size = 11;
            chart.XAxis.Format = "HH:mm:ss";
            chart.XAxis.MajorGridlines.Width = 0.5f;
            chart.YAxis.Title.Text = "值";
            chart.YAxis.Title.Font.Size = 11;
        }

        /// <summary>
        /// 将 ChartCanvas 渲染为 PNG 字节数组
        /// </summary>
        private byte[] RenderChartToPng()
        {
            if (ChartCanvas.ActualWidth <= 0 || ChartCanvas.ActualHeight <= 0) return null;

            double scale = 2.0;
            double w = ChartCanvas.ActualWidth, h = ChartCanvas.ActualHeight;
            int pw = (int)(w * scale), ph = (int)(h * scale);

            var rtb = new RenderTargetBitmap(pw, ph, 96 * scale, 96 * scale, PixelFormats.Pbgra32);

            // 白色背景
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
                dc.DrawRectangle(System.Windows.Media.Brushes.White, null, new Rect(0, 0, w, h));
            rtb.Render(dv);
            rtb.Render(ChartCanvas);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(rtb));

            using (var ms = new MemoryStream())
            {
                encoder.Save(ms);
                return ms.ToArray();
            }
        }

        #endregion

        #region 数据过滤

        private List<ChartDataPoint> GetFilteredData()
        {
            var all = _viewModel?.ChartData?.OrderBy(d => d.Timestamp).ToList();
            if (all == null || all.Count == 0) return new List<ChartDataPoint>();
            if (_timeRangeMinutes <= 0) return all;
            var cutoff = all.Last().Timestamp.AddMinutes(-_timeRangeMinutes);
            return all.Where(d => d.Timestamp >= cutoff).ToList();
        }

        #endregion

        #region 图表渲染

        private void RenderChart()
        {
            try
            {
                var now = DateTime.Now;
                if ((now - _lastRenderTime).TotalMilliseconds < 200) return;
                _lastRenderTime = now;

                if (ChartCanvas.ActualWidth == 0 || ChartCanvas.ActualHeight == 0 || _viewModel == null) return;
                ChartCanvas.Children.Clear();

                var enabled = _viewModel.MonitoredParameters.Where(p => p.IsEnabled).ToList();
                var data = GetFilteredData();
                if (enabled.Count == 0 || data.Count < 2) return;

                double w = ChartCanvas.ActualWidth, h = ChartCanvas.ActualHeight;
                double cw = w - ML - MR, ch = h - MT - MB;
                if (cw <= 0 || ch <= 0) return;

                var t0 = data.First().Timestamp;
                var ts = (data.Last().Timestamp - t0).TotalSeconds;
                if (ts <= 0) ts = 1;

                DrawGrid(cw, ch);
                if (_activeYAxisIndex < enabled.Count) DrawYAxis(enabled[_activeYAxisIndex], data, ch);
                DrawXAxis(data.First().Timestamp, data.Last().Timestamp, cw, ch);

                for (int i = 0; i < enabled.Count; i++) DrawPolyline(enabled[i], data, cw, ch, t0, ts, i % Palette.Length);
                for (int i = 0; i < enabled.Count; i++) DrawEndpoint(enabled[i], data, cw, ch, t0, ts, i % Palette.Length);
            }
            catch (Exception ex) { _logger.LogError(ex, "渲染图表失败"); }
        }

        private void DrawGrid(double cw, double ch)
        {
            var b1 = new SolidColorBrush(Color.FromRgb(243, 244, 246));
            var b2 = new SolidColorBrush(Color.FromRgb(229, 231, 235));
            for (int i = 0; i <= 5; i++)
            {
                double y = MT + (ch / 5) * i;
                var l = new Line { X1 = ML, Y1 = y, X2 = ML + cw, Y2 = y, Stroke = i == 5 ? b2 : b1, StrokeThickness = 0.5 };
                RenderOptions.SetEdgeMode(l, EdgeMode.Aliased);
                ChartCanvas.Children.Add(l);
            }
        }

        private void DrawYAxis(MonitoredParameter p, List<ChartDataPoint> data, double ch)
        {
            var vs = data.Where(d => d.ParameterValues.ContainsKey(p.DisplayName)).Select(d => d.ParameterValues[p.DisplayName]).ToList();
            if (!vs.Any()) return;
            double mn = vs.Min(), mx = vs.Max(), rg = mx - mn;
            if (rg == 0) rg = Math.Max(1, Math.Abs(mx) * 0.1);
            mn -= rg * 0.05; mx += rg * 0.05; rg = mx - mn;
            var br = new SolidColorBrush(Color.FromRgb(209, 213, 219));
            for (int i = 0; i <= 5; i++)
            {
                double v = mn + rg * i / 5, y = MT + ch - ch * i / 5;
                var tb = new TextBlock { Text = Fmt(v), FontSize = 10, FontFamily = new FontFamily("Consolas"), Foreground = br, IsHitTestVisible = false };
                tb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                Canvas.SetLeft(tb, ML - tb.DesiredSize.Width - 6);
                Canvas.SetTop(tb, y - tb.DesiredSize.Height / 2);
                ChartCanvas.Children.Add(tb);
            }
        }

        private void DrawXAxis(DateTime t0, DateTime t1, double cw, double ch)
        {
            var br = new SolidColorBrush(Color.FromRgb(209, 213, 219));
            int n = Math.Max(2, (int)(cw / 120));
            for (int i = 0; i <= n; i++)
            {
                var t = t0.AddSeconds((t1 - t0).TotalSeconds * i / n);
                double x = ML + cw * i / n;
                var tb = new TextBlock { Text = t.ToString("HH:mm"), FontSize = 10, FontFamily = new FontFamily("Consolas"), Foreground = br, IsHitTestVisible = false };
                tb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                Canvas.SetLeft(tb, x - tb.DesiredSize.Width / 2);
                Canvas.SetTop(tb, MT + ch + 8);
                ChartCanvas.Children.Add(tb);
            }
        }

        private void DrawPolyline(MonitoredParameter p, List<ChartDataPoint> data, double cw, double ch, DateTime t0, double ts, int ci)
        {
            var pts = Normalize(p, data, cw, ch, t0, ts);
            if (pts.Count < 2) return;
            var (lt, dk) = Palette[ci];
            ChartCanvas.Children.Add(new Polyline
            {
                Stroke = new LinearGradientBrush { StartPoint = new System.Windows.Point(0, 0.5), EndPoint = new System.Windows.Point(1, 0.5), GradientStops = { new GradientStop(lt, 0), new GradientStop(dk, 1) } },
                StrokeThickness = 2,
                StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Points = new PointCollection(pts),
                IsHitTestVisible = false  // ★ 防止折线抢鼠标事件
            });
        }

        private void DrawEndpoint(MonitoredParameter p, List<ChartDataPoint> data, double cw, double ch, DateTime t0, double ts, int ci)
        {
            var pts = Normalize(p, data, cw, ch, t0, ts);
            if (pts.Count == 0) return;
            var last = pts.Last();
            var (_, dk) = Palette[ci];
            var ring = new Ellipse { Width = 10, Height = 10, Fill = System.Windows.Media.Brushes.White, Stroke = new SolidColorBrush(dk), StrokeThickness = 2, IsHitTestVisible = false };
            Canvas.SetLeft(ring, last.X - 5); Canvas.SetTop(ring, last.Y - 5);
            ChartCanvas.Children.Add(ring);
        }

        private List<System.Windows.Point> Normalize(MonitoredParameter p, List<ChartDataPoint> data, double cw, double ch, DateTime t0, double ts)
        {
            var rel = data.Where(d => d.ParameterValues.ContainsKey(p.DisplayName)).ToList();
            if (rel.Count == 0) return new List<System.Windows.Point>();
            var vs = rel.Select(d => d.ParameterValues[p.DisplayName]).ToList();
            double mn = vs.Min(), mx = vs.Max(), rg = mx - mn;
            if (rg == 0) rg = Math.Max(1, Math.Abs(mx) * 0.1);
            mn -= rg * 0.05; mx += rg * 0.05; rg = mx - mn;
            return rel.Select(d => { double v = d.ParameterValues[p.DisplayName]; return new System.Windows.Point(ML + ((d.Timestamp - t0).TotalSeconds / ts) * cw, MT + ch - ((v - mn) / rg) * ch); }).ToList();
        }

        #endregion

        #region 鼠标交互（★ 修复Tooltip抖动）

        private void OnChartMouseMove(object sender, MouseEventArgs e)
        {
            if (_viewModel == null) return;
            var pos = e.GetPosition(ChartCanvas);
            var en = _viewModel.MonitoredParameters.Where(p => p.IsEnabled).ToList();
            var data = GetFilteredData();
            if (en.Count == 0 || data.Count < 2) { Hide(); return; }

            double w = ChartCanvas.ActualWidth, h = ChartCanvas.ActualHeight;
            double cw = w - ML - MR, ch = h - MT - MB;
            var t0 = data.First().Timestamp;
            double ts = (data.Last().Timestamp - t0).TotalSeconds;
            if (ts <= 0) return;

            double ratio = (pos.X - ML) / cw;
            if (ratio < -0.02 || ratio > 1.02) { Hide(); return; }
            ratio = Math.Max(0, Math.Min(1, ratio));

            var target = t0.AddSeconds(ratio * ts);
            var near = data.OrderBy(d => Math.Abs((d.Timestamp - target).TotalMilliseconds)).First();
            double nx = ML + ((near.Timestamp - t0).TotalSeconds / ts) * cw;

            // 十字准线
            CrosshairV.X1 = nx; CrosshairV.Y1 = MT; CrosshairV.X2 = nx; CrosshairV.Y2 = MT + ch;
            CrosshairV.Visibility = Visibility.Visible;

            // ★ 清旧hover点
            var oldDots = ChartCanvas.Children.OfType<Ellipse>().Where(x => x.Tag?.ToString() == "h").ToList();
            foreach (var od in oldDots) ChartCanvas.Children.Remove(od);

            // 添加新hover点（全部 IsHitTestVisible=false 防止抖动）
            for (int i = 0; i < en.Count; i++)
            {
                if (!near.ParameterValues.ContainsKey(en[i].DisplayName)) continue;
                var pts = Normalize(en[i], data, cw, ch, t0, ts);
                var cp = pts.OrderBy(p => Math.Abs(p.X - nx)).FirstOrDefault();
                if (cp == default) continue;
                var (_, dk) = Palette[i % Palette.Length];
                var r = new Ellipse
                {
                    Width = 10,
                    Height = 10,
                    Fill = System.Windows.Media.Brushes.White,
                    Stroke = new SolidColorBrush(dk),
                    StrokeThickness = 2,
                    Tag = "h",
                    IsHitTestVisible = false  // ★ 防止hover点抢鼠标导致抖动
                };
                Canvas.SetLeft(r, cp.X - 5); Canvas.SetTop(r, cp.Y - 5); Canvas.SetZIndex(r, 100);
                ChartCanvas.Children.Add(r);
            }

            // Tooltip 内容
            BuildTooltip(near, en);

            // ★ Tooltip 定位：严格限制在 Canvas 范围内
            TooltipPanel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double tw = TooltipPanel.DesiredSize.Width, th = TooltipPanel.DesiredSize.Height;
            double tx, ty;

            // 水平：优先右侧，空间不够翻左侧，都不够贴右边缘
            if (nx + 8 + tw <= w) tx = nx + 8;
            else if (nx - 8 - tw >= 0) tx = nx - 8 - tw;
            else tx = Math.Max(4, w - tw - 4);

            // 垂直：居中，限制上下边界
            ty = Math.Max(4, Math.Min(pos.Y - th / 2, h - th - 4));

            TooltipPanel.Margin = new Thickness(0);
            TooltipTransform.X = tx;
            TooltipTransform.Y = ty;
            TooltipPanel.Visibility = Visibility.Visible;
        }

        private void OnChartMouseLeave(object sender, MouseEventArgs e) => Hide();

        private void Hide()
        {
            CrosshairV.Visibility = Visibility.Collapsed;
            TooltipPanel.Visibility = Visibility.Collapsed;
            var oldDots = ChartCanvas.Children.OfType<Ellipse>().Where(x => x.Tag?.ToString() == "h").ToList();
            foreach (var od in oldDots) ChartCanvas.Children.Remove(od);
        }

        private void BuildTooltip(ChartDataPoint dp, List<MonitoredParameter> enabled)
        {
            TooltipContent.Children.Clear();
            TooltipContent.Children.Add(new TextBlock { Text = dp.Timestamp.ToString("yyyy-MM-dd HH:mm:ss"), FontSize = 11, FontFamily = new FontFamily("Consolas"), Foreground = new SolidColorBrush(Color.FromRgb(156, 163, 175)), Margin = new Thickness(0, 0, 0, 6), IsHitTestVisible = false });
            TooltipContent.Children.Add(new Rectangle { Height = 0.5, Fill = new SolidColorBrush(Color.FromRgb(243, 244, 246)), Margin = new Thickness(0, 0, 0, 6), IsHitTestVisible = false });

            for (int i = 0; i < enabled.Count; i++)
            {
                if (!dp.ParameterValues.ContainsKey(enabled[i].DisplayName)) continue;
                double v = dp.ParameterValues[enabled[i].DisplayName];
                var (lt, dk) = Palette[i % Palette.Length];
                bool active = i == _activeYAxisIndex;

                var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2), IsHitTestVisible = false };
                row.Children.Add(new Ellipse { Width = 8, Height = 8, Fill = new SolidColorBrush(active ? dk : lt), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
                row.Children.Add(new TextBlock { Text = $"{v:F2}", FontSize = active ? 13 : 11, FontWeight = active ? FontWeights.Bold : FontWeights.Normal, FontFamily = new FontFamily("Consolas"), Foreground = new SolidColorBrush(active ? Color.FromRgb(55, 65, 81) : Color.FromRgb(156, 163, 175)), VerticalAlignment = VerticalAlignment.Center });
                TooltipContent.Children.Add(row);
            }
        }

        #endregion

        private static string Fmt(double v)
        {
            if (Math.Abs(v) >= 1000) return v.ToString("F0");
            if (Math.Abs(v) >= 100) return v.ToString("F1");
            if (Math.Abs(v) >= 1) return v.ToString("F2");
            return v.ToString("F3");
        }
    }
}
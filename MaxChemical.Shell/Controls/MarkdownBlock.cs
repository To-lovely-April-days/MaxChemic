using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace MaxChemical.Shell.Controls
{
    /// <summary>
    /// 轻量 Markdown 渲染控件(小桐对话输出专用,配色取自 ThemeColors.xaml 品牌色板):
    /// 支持表格、无序/有序列表、标题、**加粗**、`行内代码`、段落。
    /// 不引第三方库;不支持的语法按纯文本降级,绝不抛异常。
    /// </summary>
    public sealed class MarkdownBlock : ContentControl
    {
        // 与 Resources/ThemeColors.xaml 保持一致:Text.Primary/Secondary/Tertiary、
        // Brand.Primary/PrimaryLight、Border.Default、Bg.Tertiary
        private static readonly Brush BrInk = Freeze("#1A1A1A");
        private static readonly Brush BrSec = Freeze("#5F5E5A");
        private static readonly Brush BrMuted = Freeze("#888780");
        private static readonly Brush BrPrimary = Freeze("#A41626");
        private static readonly Brush BrHeaderBg = Freeze("#FCEAEC");
        private static readonly Brush BrDivider = Freeze("#EAEAEA");
        private static readonly Brush BrCodeBg = Freeze("#F1EFE8");

        private static Brush Freeze(string hex)
        {
            var b = (SolidColorBrush)new BrushConverter().ConvertFromString(hex);
            b.Freeze();
            return b;
        }

        public static readonly DependencyProperty MarkdownProperty = DependencyProperty.Register(
            nameof(Markdown), typeof(string), typeof(MarkdownBlock),
            new PropertyMetadata("", (d, e) => ((MarkdownBlock)d).Rebuild()));

        public string Markdown
        {
            get => (string)GetValue(MarkdownProperty);
            set => SetValue(MarkdownProperty, value);
        }

        private void Rebuild()
        {
            try
            {
                Content = Build(Markdown ?? "");
            }
            catch
            {
                // 渲染失败降级为纯文本
                Content = new TextBlock
                {
                    Text = Markdown,
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 12.5,
                    Foreground = BrInk
                };
            }
        }

        private FrameworkElement Build(string text)
        {
            var root = new StackPanel();
            string[] lines = text.Replace("\r\n", "\n").Split('\n');

            int i = 0;
            while (i < lines.Length)
            {
                string line = lines[i];
                string trimmed = line.TrimStart();

                if (string.IsNullOrWhiteSpace(line))
                {
                    i++;
                    continue;
                }

                // ── 表格:连续以 | 开头的行,第二行为分隔行 ──
                if (trimmed.StartsWith("|"))
                {
                    var tableLines = new List<string>();
                    while (i < lines.Length && lines[i].TrimStart().StartsWith("|"))
                    {
                        tableLines.Add(lines[i].Trim());
                        i++;
                    }
                    root.Children.Add(BuildTable(tableLines));
                    continue;
                }

                // ── 水平分隔线(--- / *** / ___)──
                if (Regex.IsMatch(trimmed, @"^(-{3,}|\*{3,}|_{3,})\s*$"))
                {
                    root.Children.Add(new Border
                    {
                        Height = 1,
                        Background = BrDivider,
                        Margin = new Thickness(0, 8, 0, 8)
                    });
                    i++;
                    continue;
                }

                // ── 标题 ──
                var heading = Regex.Match(trimmed, @"^(#{1,6})\s+(.*)$");
                if (heading.Success)
                {
                    var tb = MakeInlineText(heading.Groups[2].Value, 13.5, BrInk);
                    tb.FontWeight = FontWeights.SemiBold;
                    tb.Margin = new Thickness(0, 6, 0, 4);
                    root.Children.Add(tb);
                    i++;
                    continue;
                }

                // ── 无序列表 ──
                if (Regex.IsMatch(trimmed, @"^[-*•]\s+"))
                {
                    root.Children.Add(MakeListRow("·", Regex.Replace(trimmed, @"^[-*•]\s+", "")));
                    i++;
                    continue;
                }

                // ── 有序列表 ──
                var ordered = Regex.Match(trimmed, @"^(\d+)[\.、\)]\s+(.*)$");
                if (ordered.Success)
                {
                    root.Children.Add(MakeListRow(ordered.Groups[1].Value + ".", ordered.Groups[2].Value));
                    i++;
                    continue;
                }

                // ── 段落 ──
                var para = MakeInlineText(trimmed, 12.5, BrInk);
                para.Margin = new Thickness(0, 2, 0, 2);
                para.LineHeight = 19;
                root.Children.Add(para);
                i++;
            }

            return root;
        }

        private FrameworkElement BuildTable(List<string> tableLines)
        {
            // 拆单元格:去掉首尾 |,按 | 分列
            static List<string> Cells(string line)
                => line.Trim().Trim('|').Split('|').Select(c => c.Trim()).ToList();

            var rows = tableLines.Select(Cells).ToList();

            // 去掉分隔行(--- / :---:)
            rows = rows.Where(r => !r.All(c => Regex.IsMatch(c, @"^:?-{2,}:?$") || c.Length == 0) ||
                                   !r.Any(c => c.Contains("-"))).ToList();
            rows = rows.Where(r => !r.All(c => Regex.IsMatch(c, @"^:?-{2,}:?$"))).ToList();
            if (rows.Count == 0) return new TextBlock();

            int cols = rows.Max(r => r.Count);
            var grid = new Grid();
            for (int c = 0; c < cols; c++)
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            for (int r = 0; r < rows.Count; r++)
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            for (int r = 0; r < rows.Count; r++)
            {
                bool isHeader = r == 0;
                for (int c = 0; c < cols; c++)
                {
                    string cellText = c < rows[r].Count ? rows[r][c] : "";
                    var tb = MakeInlineText(cellText, isHeader ? 11.5 : 12, isHeader ? BrPrimary : BrInk);
                    tb.MaxWidth = 260;
                    if (isHeader) tb.FontWeight = FontWeights.SemiBold;

                    var cell = new Border
                    {
                        Padding = new Thickness(10, 6, 10, 6),
                        Background = isHeader ? BrHeaderBg : Brushes.White,
                        BorderBrush = BrDivider,
                        BorderThickness = new Thickness(0, 0, 0, r == rows.Count - 1 ? 0 : 1),
                        Child = tb
                    };
                    Grid.SetRow(cell, r);
                    Grid.SetColumn(cell, c);
                    grid.Children.Add(cell);
                }
            }

            return new Border
            {
                BorderBrush = BrDivider,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Margin = new Thickness(0, 6, 0, 6),
                Background = Brushes.White,
                SnapsToDevicePixels = true,
                Child = grid,
                HorizontalAlignment = HorizontalAlignment.Left
            };
        }

        private FrameworkElement MakeListRow(string bullet, string content)
        {
            var row = new Grid { Margin = new Thickness(4, 1, 0, 1) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var b = new TextBlock
            {
                Text = bullet,
                FontSize = 12.5,
                Foreground = BrMuted,
                Margin = new Thickness(0, 0, 7, 0),
                VerticalAlignment = VerticalAlignment.Top
            };
            Grid.SetColumn(b, 0);
            row.Children.Add(b);

            var body = MakeInlineText(content, 12.5, BrInk);
            body.LineHeight = 19;
            Grid.SetColumn(body, 1);
            row.Children.Add(body);
            return row;
        }

        /// <summary>行内解析:`code` 与 **bold**,其余纯文本。</summary>
        private TextBlock MakeInlineText(string text, double fontSize, Brush foreground)
        {
            var tb = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                FontSize = fontSize,
                Foreground = foreground
            };

            // 先按 `code` 切段,再在非 code 段内解析 **bold**
            string[] codeParts = text.Split('`');
            for (int idx = 0; idx < codeParts.Length; idx++)
            {
                if (idx % 2 == 1 && idx < codeParts.Length - (codeParts.Length % 2 == 0 ? 1 : 0))
                {
                    // 反引号内:行内代码
                    tb.Inlines.Add(new Run(codeParts[idx])
                    {
                        FontFamily = new FontFamily("Consolas"),
                        Background = BrCodeBg,
                        Foreground = BrSec
                    });
                }
                else
                {
                    AppendBoldAware(tb.Inlines, codeParts[idx]);
                }
            }
            return tb;
        }

        private static void AppendBoldAware(InlineCollection inlines, string text)
        {
            string[] parts = Regex.Split(text, @"\*\*(.+?)\*\*");
            for (int i = 0; i < parts.Length; i++)
            {
                if (string.IsNullOrEmpty(parts[i])) continue;
                if (i % 2 == 1)
                    inlines.Add(new Run(parts[i]) { FontWeight = FontWeights.SemiBold });
                else
                    inlines.Add(new Run(parts[i]));
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MaxChemical.Modules.DOE.Models;
using MaxChemical.Shell.Controls;
using MaxChemical.Shell.Services.Agent.Tools;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using WRect = System.Windows.Rect;
using WSize = System.Windows.Size;

namespace MaxChemical.Shell.Services.Agent
{
    /// <summary>一键实验报告的数据包:项目(可空)+ 各轮批次(含因子/响应/实验组)。</summary>
    public sealed class ProjectReportData
    {
        /// <summary>项目档案;独立批次报告时为 null,以 Batches[0] 为主体。</summary>
        public DOEProject Project { get; set; }

        /// <summary>各轮分析总结(决策链),按轮次升序;可为空。</summary>
        public List<DOERoundSummary> RoundSummaries { get; set; } = new();

        /// <summary>各轮批次(已加载 Factors/Responses/Runs),按轮次升序。</summary>
        public List<DOEBatch> Batches { get; set; } = new();

        /// <summary>主响应是否以最小为优(来自期望函数配置;默认最大化)。</summary>
        public bool Minimize { get; set; }
    }

    /// <summary>
    /// 项目实验报告(PDF)构建器:概览、各轮摘要、优化趋势图、逐轮明细。
    /// 图表复用聊天窗的 ChartBlock 离屏渲染成 PNG 嵌入,与用户在对话里看到的是同一套绘图。
    /// 排版沿用 OlsPdfReportService 的 QuestPDF 风格(A4、微软雅黑)。
    /// </summary>
    public static class ProjectReportBuilder
    {
        /// <summary>生成 PDF,返回输出文件完整路径。数据为空时抛 InvalidOperationException。</summary>
        public static string Export(ProjectReportData data)
        {
            if (data == null || data.Batches == null || data.Batches.Count == 0)
                throw new InvalidOperationException("没有可写进报告的批次数据。");

            string title = data.Project?.ProjectName ?? data.Batches[0].BatchName ?? "未命名";
            string respName = PrimaryResponseName(data);

            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "MaxChemical", "XiaoTong", "Reports");
            Directory.CreateDirectory(dir);
            string safeName = SafeFileName(title);
            string outputPath = Path.Combine(dir, $"{safeName}_{DateTime.Now:yyyyMMdd_HHmmss}.pdf");

            // 逐轮统计(全部从原始实验组算,不依赖缓存字段)
            var rows = BuildRoundRows(data, respName);
            byte[] trendPng = rows.Count(r => r.Best.HasValue) >= 2 ? RenderTrendChart(rows, respName, data.Minimize) : null;
            byte[] scatterPng = RenderAllRunsChart(data, respName);

            Document.Create(doc =>
            {
                doc.Page(page =>
                {
                    page.Size(PageSizes.A4);
                    page.MarginTop(20);
                    page.MarginBottom(20);
                    page.MarginHorizontal(25);
                    page.DefaultTextStyle(x => x.FontSize(9).FontFamily("Microsoft YaHei"));

                    page.Header().BorderBottom(2).BorderColor(Colors.Indigo.Medium).PaddingBottom(6).Row(row =>
                    {
                        row.RelativeItem().Column(c =>
                        {
                            c.Item().Text($"{title} — 实验项目报告").FontSize(16).Bold().FontColor(Colors.Indigo.Darken3);
                            c.Item().Text(data.Project != null ? "DOE 优化项目全程记录" : "独立 DOE 批次记录")
                                .FontSize(8).FontColor(Colors.Grey.Medium);
                        });
                        row.ConstantItem(120).AlignRight().Column(c =>
                        {
                            c.Item().AlignRight().Text(DateTime.Now.ToString("yyyy-MM-dd HH:mm")).FontSize(8).FontColor(Colors.Grey.Medium);
                            c.Item().AlignRight().Text("MaxChemic 小桐智能助手").FontSize(8).FontColor(Colors.Grey.Medium);
                        });
                    });

                    page.Footer().BorderTop(1).BorderColor(Colors.Grey.Lighten2).PaddingTop(4).Row(row =>
                    {
                        row.RelativeItem().Text("MaxChemical DOE Platform — 数值以数据库为准").FontSize(7).FontColor(Colors.Grey.Medium);
                        row.RelativeItem().AlignRight().Text(x =>
                        {
                            x.Span("Page ").FontSize(7).FontColor(Colors.Grey.Medium);
                            x.CurrentPageNumber().FontSize(7).FontColor(Colors.Grey.Medium);
                        });
                    });

                    page.Content().PaddingVertical(8).Column(col =>
                    {
                        ComposeOverview(col, data, respName, rows);
                        ComposeRoundSummaryTable(col, data, rows);

                        bool hasCharts = trendPng != null || scatterPng != null;
                        if (hasCharts)
                        {
                            SectionTitle(col, "3. 优化趋势");
                            if (trendPng != null) col.Item().PaddingVertical(4).Image(trendPng).FitWidth();
                            if (scatterPng != null) col.Item().PaddingVertical(4).Image(scatterPng).FitWidth();
                        }

                        ComposeRoundDetails(col, data, respName, hasCharts ? 4 : 3);

                        col.Item().PaddingTop(10).BorderTop(1).BorderColor(Colors.Grey.Lighten2).PaddingTop(4)
                            .Text($"本报告由 MaxChemic 小桐智能助手自动生成({DateTime.Now:yyyy-MM-dd HH:mm});" +
                                  "内容基于生成时刻的数据库记录,后续新增实验不会自动更新到本文件。")
                            .FontSize(7.5f).FontColor(Colors.Grey.Medium);
                    });
                });
            }).GeneratePdf(outputPath);

            return outputPath;
        }

        // ══════════════ 数据整理 ══════════════

        private sealed class RoundRow
        {
            public int? Round;
            public DOEBatch Batch;
            public DOERoundSummary Summary;
            public int Total;
            public int Completed;
            public double? Best;
        }

        private static string PrimaryResponseName(ProjectReportData data)
        {
            foreach (var b in Enumerable.Reverse(data.Batches))
            {
                var r = b.Responses?.FirstOrDefault()?.ResponseName;
                if (!string.IsNullOrEmpty(r)) return r;
            }
            return "";
        }

        private static List<RoundRow> BuildRoundRows(ProjectReportData data, string respName)
        {
            var rows = new List<RoundRow>();
            foreach (var b in data.Batches)
            {
                var runs = b.Runs ?? new List<DOERunRecord>();
                var completed = runs.Where(r => r.Status == DOERunStatus.Completed).ToList();
                var values = completed
                    .Select(r => DoeAnalysisHelper.ParseNumbers(r.ResponseValuesJson))
                    .Where(d => d.ContainsKey(respName))
                    .Select(d => d[respName])
                    .ToList();
                rows.Add(new RoundRow
                {
                    Round = b.RoundNumber,
                    Batch = b,
                    Summary = data.RoundSummaries?.FirstOrDefault(s => s.BatchId == b.BatchId),
                    Total = runs.Count,
                    Completed = completed.Count,
                    Best = values.Count == 0 ? (double?)null : (data.Minimize ? values.Min() : values.Max())
                });
            }
            return rows;
        }

        // ══════════════ 版面段落 ══════════════

        private static void ComposeOverview(ColumnDescriptor col, ProjectReportData data, string respName, List<RoundRow> rows)
        {
            SectionTitle(col, "1. 概览");
            col.Item().Table(t =>
            {
                t.ColumnsDefinition(c => { c.RelativeColumn(1.4f); c.RelativeColumn(3.6f); });

                if (data.Project != null)
                {
                    var p = data.Project;
                    KvRow(t, "项目名称", p.ProjectName ?? "-");
                    KvRow(t, "项目编号", p.ProjectId ?? "-");
                    if (!string.IsNullOrWhiteSpace(p.ObjectiveDescription))
                        KvRow(t, "优化目标", p.ObjectiveDescription);
                    KvRow(t, "阶段 / 状态", $"{PhaseText(p.CurrentPhase)} / {ProjectStatusText(p.Status)}");
                    KvRow(t, "轮次 / 累计实验", $"{rows.Count} 轮 / {rows.Sum(r => r.Completed)} 组已完成(设计 {rows.Sum(r => r.Total)} 组)");
                }
                else
                {
                    var b = data.Batches[0];
                    KvRow(t, "批次名称", b.BatchName ?? "-");
                    KvRow(t, "批次编号", b.BatchId ?? "-");
                    KvRow(t, "设计方法 / 状态", $"{DesignMethodText(b.DesignMethod)} / {BatchStatusText(b.Status)}");
                    KvRow(t, "实验组", $"{rows[0].Completed}/{rows[0].Total} 组已完成");
                }

                if (!string.IsNullOrEmpty(respName))
                    KvRow(t, "主响应", respName + (data.Minimize ? "(以最小为优)" : "(以最大为优)"));

                var bestRow = data.Minimize
                    ? rows.Where(r => r.Best.HasValue).OrderBy(r => r.Best.Value).FirstOrDefault()
                    : rows.Where(r => r.Best.HasValue).OrderByDescending(r => r.Best.Value).FirstOrDefault();
                if (bestRow != null)
                {
                    string where = bestRow.Round.HasValue ? $"第 {bestRow.Round} 轮" : bestRow.Batch.BatchName;
                    KvRow(t, "实测最优", $"{bestRow.Best.Value:0.####}({where})");
                    string bestCond = BestConditionText(data, bestRow, respName);
                    if (!string.IsNullOrEmpty(bestCond))
                        KvRow(t, "最优条件", bestCond);
                }
            });
            col.Item().PaddingVertical(4);
        }

        private static string BestConditionText(ProjectReportData data, RoundRow bestRow, string respName)
        {
            var completed = (bestRow.Batch.Runs ?? new List<DOERunRecord>())
                .Where(r => r.Status == DOERunStatus.Completed);
            DOERunRecord best = null;
            double bestVal = data.Minimize ? double.MaxValue : double.MinValue;
            foreach (var r in completed)
            {
                var d = DoeAnalysisHelper.ParseNumbers(r.ResponseValuesJson);
                if (!d.TryGetValue(respName, out double v)) continue;
                if (data.Minimize ? v < bestVal : v > bestVal) { bestVal = v; best = r; }
            }
            if (best == null) return "";
            var factors = ParseDisplayValues(best.FactorValuesJson);
            return string.Join(", ", factors.Select(kv => $"{kv.Key}={kv.Value}"));
        }

        /// <summary>
        /// 展示用的因子值解析:数值格式化,类别因子(字符串水平,如催化剂型号)原样保留——
        /// ParseNumbers 只收数字,会把类别列整列丢成短横。
        /// </summary>
        private static Dictionary<string, string> ParseDisplayValues(string json)
        {
            var dict = new Dictionary<string, string>();
            if (string.IsNullOrWhiteSpace(json)) return dict;
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                foreach (var p in doc.RootElement.EnumerateObject())
                {
                    if (p.Value.ValueKind == System.Text.Json.JsonValueKind.Number)
                        dict[p.Name] = p.Value.GetDouble().ToString("0.###");
                    else if (p.Value.ValueKind == System.Text.Json.JsonValueKind.String)
                        dict[p.Name] = p.Value.GetString() ?? "";
                }
            }
            catch { }
            return dict;
        }

        private static void ComposeRoundSummaryTable(ColumnDescriptor col, ProjectReportData data, List<RoundRow> rows)
        {
            SectionTitle(col, "2. 各轮摘要");
            col.Item().Table(t =>
            {
                t.ColumnsDefinition(c =>
                {
                    c.RelativeColumn(0.8f); c.RelativeColumn(3f); c.RelativeColumn(1.6f);
                    c.RelativeColumn(1.2f); c.RelativeColumn(1.2f); c.RelativeColumn(1.2f);
                    c.RelativeColumn(1.2f); c.RelativeColumn(1.8f);
                });
                THeader(t, "轮次", "批次", "设计方法", "状态", "完成/总", "本轮最优", "R2 adj", "引擎推荐");
                foreach (var r in rows)
                {
                    TCell(t, r.Round?.ToString() ?? "-");
                    TCell(t, r.Batch.BatchName ?? r.Batch.BatchId);
                    TCell(t, DesignMethodText(r.Batch.DesignMethod));
                    TCell(t, BatchStatusText(r.Batch.Status));
                    TNum(t, $"{r.Completed}/{r.Total}");
                    TNum(t, r.Best.HasValue ? r.Best.Value.ToString("0.####") : "-");
                    TNum(t, r.Summary?.RSquaredAdj != null ? r.Summary.RSquaredAdj.Value.ToString("F3") : "-");
                    TCell(t, r.Summary != null ? RecommendationText(r.Summary.Recommendation) : "-");
                }
            });
            col.Item().PaddingVertical(4);
        }

        private static void ComposeRoundDetails(ColumnDescriptor col, ProjectReportData data, string respName, int sectionNo)
        {
            SectionTitle(col, $"{sectionNo}. 逐轮明细");

            const int maxBatches = 8;
            const int maxRunRows = 30;
            var batches = data.Batches.Take(maxBatches).ToList();
            if (data.Batches.Count > maxBatches)
                col.Item().Text($"(轮次较多,明细仅列前 {maxBatches} 轮;完整数据请在 DOE 模块查看)")
                    .FontSize(8).FontColor(Colors.Grey.Medium);

            foreach (var b in batches)
            {
                string head = b.RoundNumber.HasValue ? $"第 {b.RoundNumber} 轮:{b.BatchName}" : b.BatchName ?? b.BatchId;
                col.Item().PaddingTop(6).Text(head).FontSize(10).Bold().FontColor(Colors.Indigo.Darken2);

                var factors = b.Factors ?? new List<DOEFactor>();
                if (factors.Count > 0)
                {
                    col.Item().PaddingTop(2).Table(t =>
                    {
                        t.ColumnsDefinition(c => { c.RelativeColumn(2.4f); c.RelativeColumn(1.4f); c.RelativeColumn(1.4f); c.RelativeColumn(1.2f); });
                        THeader(t, "因子", "下限", "上限", "水平数");
                        foreach (var f in factors)
                        {
                            TCell(t, f.FactorName);
                            TNum(t, f.IsCategorical ? "-" : f.LowerBound.ToString("0.###"));
                            TNum(t, f.IsCategorical ? "-" : f.UpperBound.ToString("0.###"));
                            TNum(t, f.IsCategorical ? f.GetCategoryLevelList().Count.ToString() : f.LevelCount.ToString());
                        }
                    });
                }

                var runs = (b.Runs ?? new List<DOERunRecord>()).OrderBy(r => r.RunIndex).ToList();
                if (runs.Count == 0)
                {
                    col.Item().PaddingTop(2).Text("(本轮还没有实验组数据)").FontSize(8).FontColor(Colors.Grey.Medium);
                    continue;
                }

                var factorNames = factors.Select(f => f.FactorName).ToList();
                if (factorNames.Count == 0)
                    factorNames = ParseDisplayValues(runs[0].FactorValuesJson).Keys.ToList();
                var respNames = (b.Responses ?? new List<DOEResponse>()).Select(r => r.ResponseName).ToList();
                if (respNames.Count == 0 && !string.IsNullOrEmpty(respName)) respNames.Add(respName);

                col.Item().PaddingTop(3).Table(t =>
                {
                    t.ColumnsDefinition(c =>
                    {
                        c.RelativeColumn(0.8f);
                        foreach (var _ in factorNames) c.RelativeColumn(1.4f);
                        foreach (var _ in respNames) c.RelativeColumn(1.4f);
                        c.RelativeColumn(1.1f);
                    });
                    THeader(t, new[] { "组号" }.Concat(factorNames).Concat(respNames).Concat(new[] { "状态" }).ToArray());

                    // 组号用顺位而不是 run_index:向导批次 0 起、Agent 批次 1 起,顺位对人最不歧义
                    int rowNo = 0;
                    foreach (var run in runs.Take(maxRunRows))
                    {
                        var fv = ParseDisplayValues(run.FactorValuesJson);
                        var rv = DoeAnalysisHelper.ParseNumbers(run.ResponseValuesJson);
                        rowNo++;
                        TNum(t, rowNo.ToString());
                        foreach (var fn in factorNames)
                            TNum(t, fv.TryGetValue(fn, out string s) ? s : "-");
                        foreach (var rn in respNames)
                            TNum(t, rv.TryGetValue(rn, out double v) ? v.ToString("0.####") : "-");
                        TCell(t, RunStatusText(run.Status));
                    }
                });
                if (runs.Count > maxRunRows)
                    col.Item().Text($"(共 {runs.Count} 组,仅列前 {maxRunRows} 组)").FontSize(8).FontColor(Colors.Grey.Medium);
            }
        }

        // ══════════════ 图表(ChartBlock 离屏渲染) ══════════════

        private static byte[] RenderTrendChart(List<RoundRow> rows, string respName, bool minimize)
        {
            var withData = rows.Where(r => r.Best.HasValue).ToList();
            return RenderChartPng(new
            {
                type = "line",
                title = $"各轮最优{respName}趋势({(minimize ? "越低越好" : "越高越好")})",
                xLabel = "轮次",
                yLabel = respName,
                x = withData.Select(r => r.Round.HasValue ? $"第{r.Round}轮" : r.Batch.BatchName).ToList(),
                series = new[] { new { name = "本轮最优", values = withData.Select(r => r.Best.Value).ToList() } }
            });
        }

        private static byte[] RenderAllRunsChart(ProjectReportData data, string respName)
        {
            var xs = new List<double>();
            var ys = new List<double>();
            int seq = 0;
            foreach (var b in data.Batches)
                foreach (var run in (b.Runs ?? new List<DOERunRecord>())
                             .Where(r => r.Status == DOERunStatus.Completed).OrderBy(r => r.RunIndex))
                {
                    var rv = DoeAnalysisHelper.ParseNumbers(run.ResponseValuesJson);
                    if (!rv.TryGetValue(respName, out double v)) continue;
                    seq++;
                    xs.Add(seq);
                    ys.Add(v);
                }
            if (ys.Count < 3) return null;

            int bestIdx = 0;
            for (int i = 1; i < ys.Count; i++)
                if (data.Minimize ? ys[i] < ys[bestIdx] : ys[i] > ys[bestIdx]) bestIdx = i;

            return RenderChartPng(new
            {
                type = "scatter",
                title = $"全部已完成实验的{respName}(按执行顺序)",
                xLabel = "实验序号(跨轮累计)",
                yLabel = respName,
                x = new List<string>(),
                xNumeric = xs,
                highlightIndex = bestIdx,
                series = new[] { new { name = respName, values = ys } }
            });
        }

        private static byte[] RenderChartPng(object spec)
        {
            try
            {
                string json = System.Text.Json.JsonSerializer.Serialize(spec);
                var app = System.Windows.Application.Current;
                if (app == null) return null;

                byte[] data = null;
                app.Dispatcher.Invoke(() =>
                {
                    var host = new System.Windows.Controls.Border
                    {
                        Background = System.Windows.Media.Brushes.White,
                        Child = new ChartBlock { ChartJson = json },
                        Width = 540
                    };
                    host.Measure(new WSize(540, double.PositiveInfinity));
                    double h = Math.Max(host.DesiredSize.Height, 120);
                    host.Arrange(new WRect(0, 0, 540, h));
                    host.UpdateLayout();

                    var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
                        1080, (int)Math.Ceiling(h * 2), 192, 192, System.Windows.Media.PixelFormats.Pbgra32);
                    rtb.Render(host);
                    var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
                    using var ms = new MemoryStream();
                    enc.Save(ms);
                    data = ms.ToArray();
                });
                return data != null && data.Length > 100 ? data : null;
            }
            catch
            {
                return null; // 图表失败不挡报告主体
            }
        }

        // ══════════════ QuestPDF 小助手(沿用 OlsPdfReportService 风格) ══════════════

        private static void SectionTitle(ColumnDescriptor col, string title)
        {
            col.Item().PaddingTop(4).PaddingBottom(2).Row(row =>
            {
                row.ConstantItem(3).Height(18).Background(Colors.Indigo.Darken3);
                row.ConstantItem(4);
                row.RelativeItem().AlignBottom().Text(title).FontSize(11).Bold().FontColor(Colors.Indigo.Darken3);
            });
        }

        private static void THeader(TableDescriptor t, params string[] headers)
        {
            t.Header(h =>
            {
                foreach (var hdr in headers)
                    h.Cell().Background(Colors.Indigo.Darken3).PaddingVertical(4).PaddingHorizontal(4)
                        .Text(hdr).FontSize(8).Bold().FontColor(Colors.White);
            });
        }

        private static void TCell(TableDescriptor t, string text)
        {
            t.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten3)
                .PaddingVertical(3).PaddingHorizontal(4)
                .Text(text ?? "").FontSize(8);
        }

        private static void TNum(TableDescriptor t, string text)
        {
            t.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten3)
                .PaddingVertical(3).PaddingHorizontal(4).AlignRight()
                .Text(text ?? "").FontSize(8).FontFamily("Consolas");
        }

        private static void KvRow(TableDescriptor t, string label, string value)
        {
            t.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).PaddingVertical(3).PaddingHorizontal(4)
                .Text(label).FontSize(9).Bold().FontColor(Colors.Indigo.Darken2);
            t.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).PaddingVertical(3).PaddingHorizontal(4)
                .Text(value ?? "").FontSize(9);
        }

        // ══════════════ 枚举中文名 ══════════════

        private static string SafeFileName(string name)
        {
            foreach (var ch in Path.GetInvalidFileNameChars())
                name = name.Replace(ch, '_');
            return name.Length > 60 ? name.Substring(0, 60) : name;
        }

        private static string PhaseText(DOEProjectPhase phase) => phase switch
        {
            DOEProjectPhase.Screening => "筛选",
            DOEProjectPhase.PathSearch => "路径探索",
            DOEProjectPhase.RSM => "响应面优化",
            DOEProjectPhase.Augmenting => "增强补点",
            DOEProjectPhase.Confirmation => "验证",
            DOEProjectPhase.Completed => "已完成",
            _ => phase.ToString()
        };

        private static string ProjectStatusText(DOEProjectStatus status) => status switch
        {
            DOEProjectStatus.Active => "进行中",
            DOEProjectStatus.Paused => "已暂停",
            DOEProjectStatus.Completed => "已完成",
            DOEProjectStatus.Archived => "已归档",
            _ => status.ToString()
        };

        private static string BatchStatusText(DOEBatchStatus status) => status switch
        {
            DOEBatchStatus.Designing => "设计中",
            DOEBatchStatus.Ready => "就绪",
            DOEBatchStatus.Running => "执行中",
            DOEBatchStatus.Paused => "已暂停",
            DOEBatchStatus.Completed => "已完成",
            DOEBatchStatus.Aborted => "已中止",
            _ => status.ToString()
        };

        private static string RunStatusText(DOERunStatus status) => status switch
        {
            DOERunStatus.Pending => "待执行",
            DOERunStatus.Running => "执行中",
            DOERunStatus.WaitingResponse => "待录结果",
            DOERunStatus.Completed => "已完成",
            DOERunStatus.Failed => "失败",
            DOERunStatus.Skipped => "已跳过",
            DOERunStatus.Suspended => "已挂起",
            _ => status.ToString()
        };

        private static string DesignMethodText(DOEDesignMethod method) => method switch
        {
            DOEDesignMethod.FullFactorial => "全因子",
            DOEDesignMethod.FractionalFactorial => "部分因子",
            DOEDesignMethod.Taguchi => "田口",
            DOEDesignMethod.Custom => "自定义",
            DOEDesignMethod.CCD => "中心复合(CCD)",
            DOEDesignMethod.BoxBehnken => "Box-Behnken",
            DOEDesignMethod.DOptimal => "D-最优",
            DOEDesignMethod.PlackettBurman => "Plackett-Burman",
            DOEDesignMethod.SteepestAscent => "最速上升",
            DOEDesignMethod.AugmentedDesign => "增强补点",
            DOEDesignMethod.ConfirmationRuns => "验证实验",
            _ => method.ToString()
        };

        private static string RecommendationText(NextStepRecommendation rec) => rec switch
        {
            NextStepRecommendation.ContinueScreening => "继续筛选",
            NextStepRecommendation.SteepestAscent => "最速上升",
            NextStepRecommendation.StartRSM => "进入响应面",
            NextStepRecommendation.AugmentDesign => "增强补点",
            NextStepRecommendation.ExpandRange => "扩展范围",
            NextStepRecommendation.ConfirmationRuns => "验证实验",
            NextStepRecommendation.Complete => "完成项目",
            NextStepRecommendation.UserDecision => "需用户决策",
            _ => rec.ToString()
        };
    }
}

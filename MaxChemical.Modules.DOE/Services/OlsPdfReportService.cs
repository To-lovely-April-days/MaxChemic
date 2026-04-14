// OlsPdfReportService.cs — QuestPDF 导出
// NuGet: dotnet add package QuestPDF
// App.xaml.cs OnStartup: QuestPDF.Settings.License = LicenseType.Community;

using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using MaxChemical.Modules.DOE.Models;
using MaxChemical.Modules.DOE.ViewModels;
using OxyPlot;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using VM = MaxChemical.Modules.DOE.ViewModels.DOEModelAnalysisViewModel;

namespace MaxChemical.Modules.DOE.Services
{
    public class OlsPdfReportService
    {
        public void ExportReport(DOEModelAnalysisViewModel vm, string outputPath)
        {
            var result = vm.OlsResult;
            if (result?.ModelSummary == null)
                throw new InvalidOperationException("没有可导出的 OLS 分析结果");

            var s = result.ModelSummary;
            var resp = vm.SelectedResponseName ?? "Response";
            var isReduced = !string.IsNullOrEmpty(vm.ExcludedTermsText);

            Document.Create(doc =>
            {
                doc.Page(page =>
                {
                    page.Size(PageSizes.A4);
                    page.MarginTop(20);
                    page.MarginBottom(20);
                    page.MarginHorizontal(25);
                    page.DefaultTextStyle(x => x.FontSize(9).FontFamily("Microsoft YaHei"));

                    // ── 页头 ──
                    page.Header().BorderBottom(2).BorderColor(Colors.Indigo.Medium).PaddingBottom(6).Row(row =>
                    {
                        row.RelativeItem().Column(c =>
                        {
                            c.Item().Text($"{resp} — OLS 回归分析报告").FontSize(16).Bold().FontColor(Colors.Indigo.Darken3);
                            c.Item().Text(isReduced ? "精简模型" : "完整模型").FontSize(8).FontColor(Colors.Grey.Medium);
                        });
                        row.ConstantItem(100).AlignRight().Column(c =>
                        {
                            c.Item().AlignRight().Text(DateTime.Now.ToString("yyyy-MM-dd")).FontSize(8).FontColor(Colors.Grey.Medium);
                            c.Item().AlignRight().Text("MaxChemical DOE").FontSize(8).FontColor(Colors.Grey.Medium);
                        });
                    });

                    // ── 页脚 ──
                    page.Footer().BorderTop(1).BorderColor(Colors.Grey.Lighten2).PaddingTop(4).Row(row =>
                    {
                        row.RelativeItem().Text("Confidential — MaxChemical DOE Platform").FontSize(7).FontColor(Colors.Grey.Medium);
                        row.RelativeItem().AlignRight().Text(x =>
                        {
                            x.Span("Page ").FontSize(7).FontColor(Colors.Grey.Medium);
                            x.CurrentPageNumber().FontSize(7).FontColor(Colors.Grey.Medium);
                        });
                    });

                    // ── 内容 ──
                    page.Content().PaddingVertical(8).Column(col =>
                    {
                        // ═══ 1. KPI 卡片 ═══
                        col.Item().Table(t =>
                        {
                            t.ColumnsDefinition(c => { for (int i = 0; i < 6; i++) c.RelativeColumn(); });
                            Kpi(t, "R²", s.RSquared.ToString("F4"), true);
                            Kpi(t, "R² adj", s.RSquaredAdj.ToString("F4"), false);
                            Kpi(t, "R² pred", s.RSquaredPred.ToString("F4"), false);
                            Kpi(t, "RMSE", s.RMSE.ToString("F4"), false);
                            Kpi(t, "Model P", (s.ModelP ?? 0).ToString("F4"), false);
                            Kpi(t, "LOF P", (s.LackOfFitP ?? 0).ToString("F4"), false);
                        });
                        col.Item().PaddingVertical(6);

                        // ═══ 2. ANOVA ═══
                        SectionTitle(col, "1. 方差分析 (ANOVA)");
                        if (result.AnovaTable?.Count > 0)
                        {
                            col.Item().Table(t =>
                            {
                                t.ColumnsDefinition(c =>
                                {
                                    c.RelativeColumn(3); c.RelativeColumn(1); c.RelativeColumn(2);
                                    c.RelativeColumn(2); c.RelativeColumn(1.5f); c.RelativeColumn(1.5f);
                                });
                                THeader(t, "来源", "DF", "Seq SS", "Adj MS", "F 值", "P 值");
                                foreach (var r in result.AnovaTable)
                                {
                                    bool sig = r.PValue.HasValue && r.PValue.Value < 0.05;
                                    TCell(t, r.Source);
                                    TNum(t, r.DF.ToString());
                                    TNum(t, r.SS.ToString("F4"));
                                    TNum(t, r.MS.HasValue ? r.MS.Value.ToString("F4") : "");
                                    TNum(t, r.FValue.HasValue ? r.FValue.Value.ToString("F2") : "");
                                    t.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten3)
                                        .PaddingVertical(3).PaddingHorizontal(4).AlignRight()
                                        .Text(r.PValue.HasValue ? r.PValue.Value.ToString("F4") : "")
                                        .FontSize(8).FontFamily("Consolas")
                                        .FontColor(sig ? Colors.Red.Darken4 : Colors.Black);
                                }
                            });
                        }
                        col.Item().PaddingVertical(6);

                        // ═══ 3. 系数表 ═══
                        SectionTitle(col, "2. 参数估计（" + vm.EquationModeText + "）");
                        if (vm.DisplayCoefficients?.Count > 0)
                        {
                            col.Item().Table(t =>
                            {
                                t.ColumnsDefinition(c =>
                                {
                                    c.RelativeColumn(3); c.RelativeColumn(1.5f); c.RelativeColumn(2);
                                    c.RelativeColumn(1.5f); c.RelativeColumn(1.5f); c.RelativeColumn(1.5f); c.RelativeColumn(1);
                                });
                                THeader(t, "项", "效应", "系数", "SE", "t", "P", "VIF");
                                foreach (var r in vm.DisplayCoefficients)
                                {
                                    bool sig = r.PValue < 0.05;
                                    TCell(t, r.Term);
                                    TNum(t, r.Effect?.ToString("F4") ?? "—");
                                    TNum(t, r.Coefficient.ToString("F6"));
                                    TNum(t, r.StdError.ToString("F6"));
                                    TNum(t, r.TValue.ToString("F3"));
                                    t.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten3)
                                        .PaddingVertical(3).PaddingHorizontal(4).AlignRight()
                                        .Text(r.PValue.ToString("F4")).FontSize(8).FontFamily("Consolas")
                                        .FontColor(sig ? Colors.Red.Darken4 : Colors.Black);
                                    TNum(t, r.VIF?.ToString("F2") ?? "—");
                                }
                            });
                        }
                        col.Item().PaddingVertical(6);

                        // ═══ 4. 回归方程 ═══
                        SectionTitle(col, "3. 回归方程");
                        col.Item().Background(Colors.Grey.Lighten4).Padding(8)
                            .Text(vm.CurrentEquationText ?? "(无)")
                            .FontFamily("Consolas").FontSize(8.5f);
                        col.Item().PaddingVertical(4);

                        // 分类别方程
                        if (vm.HasCategoricalEquations && vm.CategoryEquations?.Count > 0)
                        {
                            foreach (var eq in vm.CategoryEquations)
                            {
                                col.Item().PaddingTop(3).Text(eq.LevelName).FontSize(9).Bold().FontColor(Colors.Indigo.Medium);
                                col.Item().Background(Colors.Grey.Lighten4).Padding(6)
                                    .Text(eq.Equation).FontFamily("Consolas").FontSize(8);
                            }
                        }
                        col.Item().PaddingVertical(4);

                        // ═══ 5. 精简信息 ═══
                        if (!string.IsNullOrEmpty(vm.ExcludedTermsText) && vm.ExcludedTermsText != "无")
                        {
                            SectionTitle(col, "4. 模型精简");
                            col.Item().Text(t =>
                            {
                                t.Span("保留项: ").FontSize(9).FontColor(Colors.Grey.Darken1);
                                t.Span(vm.IncludedTermsText ?? "").FontSize(9);
                            });
                            col.Item().Text(t =>
                            {
                                t.Span("排除项: ").FontSize(9).FontColor(Colors.Red.Darken4);
                                t.Span(vm.ExcludedTermsText ?? "").FontSize(9).FontColor(Colors.Red.Darken4);
                            });
                            col.Item().PaddingVertical(4);
                        }

                        // ═══ 6. 残差四合一 ═══
                        SectionTitle(col, "残差诊断");
                        col.Item().Row(row =>
                        {
                            row.RelativeItem().Column(c2 =>
                            {
                                c2.Item().Element(c => PlotImage(c, vm.NormalProbPlot, 240, 160));
                                c2.Item().PaddingVertical(2);
                                c2.Item().Element(c => PlotImage(c, vm.ResidHistogramPlot, 240, 160));
                            });
                            row.ConstantItem(6);
                            row.RelativeItem().Column(c2 =>
                            {
                                c2.Item().Element(c => PlotImage(c, vm.ResidVsFittedPlot, 240, 160));
                                c2.Item().PaddingVertical(2);
                                c2.Item().Element(c => PlotImage(c, vm.ResidVsOrderPlot, 240, 160));
                            });
                        });
                        col.Item().PaddingVertical(6);

                        // ═══ 7. Pareto ═══
                        SectionTitle(col, "效应 Pareto");
                        col.Item().Element(c => PlotImage(c, vm.OlsParetoPlot, 500, 250));
                        col.Item().PaddingVertical(6);

                        // ═══ 8. 等高线 ═══
                        if (vm.OlsContourPlot != null)
                        {
                            SectionTitle(col, $"响应曲面 ({vm.OlsSurfaceFactor1} × {vm.OlsSurfaceFactor2})");
                            col.Item().Element(c => PlotImage(c, vm.OlsContourPlot, 460, 240));
                            col.Item().PaddingVertical(6);
                        }

                        // ═══ 9. 异常点 ═══
                        if (vm.OutlierItems?.Count > 0)
                        {
                            SectionTitle(col, "异常点诊断");
                            col.Item().Text(vm.OutlierSummaryText ?? "").FontSize(8).FontColor(Colors.Grey.Darken1);
                            col.Item().PaddingVertical(2);
                            col.Item().Table(t =>
                            {
                                t.ColumnsDefinition(c =>
                                {
                                    c.RelativeColumn(0.6f); c.RelativeColumn(1.2f); c.RelativeColumn(1.2f);
                                    c.RelativeColumn(1.2f); c.RelativeColumn(1.2f); c.RelativeColumn(1.2f); c.RelativeColumn(2);
                                });
                                THeader(t, "#", "实际值", "预测值", "标准残差", "杠杆值", "Cook D", "原因");
                                foreach (var r in vm.OutlierItems)
                                {
                                    TNum(t, r.Index.ToString());
                                    TNum(t, r.Actual.ToString("F4"));
                                    TNum(t, r.Predicted.ToString("F4"));
                                    TNum(t, r.StdResidual.ToString("F3"));
                                    TNum(t, r.Leverage.ToString("F4"));
                                    TNum(t, r.CooksD.ToString("F4"));
                                    TCell(t, r.Reasons ?? "");
                                }
                            });
                            col.Item().PaddingVertical(6);
                        }

                        // ═══ 10. 预测刻画器 ═══
                        if (vm.IsProfilerLoaded && vm.ProfilerPlots?.Count > 0)
                        {
                            SectionTitle(col, "预测刻画器");
                            // 预测值 + CI
                            col.Item().Background(Colors.Grey.Lighten4).Padding(8)
                                .Text(vm.ProfilerCurrentPredicted ?? "").FontFamily("Consolas").FontSize(9);
                            col.Item().PaddingVertical(3);
                            // 刻画器图（横排）
                            col.Item().Row(row =>
                            {
                                for (int pi = 0; pi < vm.ProfilerPlots.Count; pi++)
                                {
                                    int plotW = Math.Max(120, 500 / vm.ProfilerPlots.Count);
                                    var pm = vm.ProfilerPlots[pi];
                                    if (pi > 0) row.ConstantItem(2);
                                    row.RelativeItem().Element(c => PlotImage(c, pm, plotW, 140));
                                }
                            });
                            col.Item().PaddingVertical(3);

                            // 意愿行图（如果显示了）
                            if (vm.IsDesirabilityVisible && vm.DesirabilityPlots?.Count > 0)
                            {
                                col.Item().Row(row =>
                                {
                                    for (int di = 0; di < vm.DesirabilityPlots.Count; di++)
                                    {
                                        int plotW = Math.Max(120, 500 / vm.DesirabilityPlots.Count);
                                        var pm = vm.DesirabilityPlots[di];
                                        if (di > 0) row.ConstantItem(2);
                                        row.RelativeItem().Element(c => PlotImage(c, pm, plotW, 110));
                                    }
                                });
                                col.Item().PaddingVertical(2);
                                col.Item().Text(t =>
                                {
                                    t.Span("综合意愿 D = ").FontSize(9).FontColor(Colors.Grey.Darken1);
                                    t.Span(vm.DesirabilityCompositeD.ToString("F4")).FontSize(10).Bold()
                                        .FontColor(Colors.Indigo.Darken2);
                                });
                            }

                            // 优化结果文本
                            if (!string.IsNullOrEmpty(vm.OptimalResultText))
                            {
                                col.Item().PaddingVertical(3);
                                col.Item().Background(Colors.Grey.Lighten4).Padding(8)
                                    .Text(vm.OptimalResultText).FontFamily("Consolas").FontSize(8);
                            }
                            col.Item().PaddingVertical(6);
                        }

                        // ═══ 11. 最优条件（如果有） ═══
                        if (vm.HasOptimalResult && vm.OptimalFactors?.Count > 0)
                        {
                            SectionTitle(col, "最优条件");
                            col.Item().Table(t =>
                            {
                                t.ColumnsDefinition(c => { c.RelativeColumn(2); c.RelativeColumn(3); });
                                THeader(t, "因子", "最优值");
                                foreach (var f in vm.OptimalFactors)
                                {
                                    TCell(t, f.Key);
                                    TNum(t, f.DisplayValue);
                                }
                            });
                            // 预测响应
                            if (!string.IsNullOrEmpty(vm.OptimalResponseText) && vm.OptimalResponseText != "—")
                            {
                                col.Item().PaddingTop(3).Text(t =>
                                {
                                    t.Span("预测响应: ").FontSize(9).FontColor(Colors.Grey.Darken1);
                                    t.Span(vm.OptimalResponseText).FontSize(11).Bold().FontColor(Colors.Indigo.Darken2);
                                    if (!string.IsNullOrEmpty(vm.OptimalConfidenceText))
                                    {
                                        t.Span($"  {vm.OptimalConfidenceText}").FontSize(9).FontColor(Colors.Grey.Medium);
                                    }
                                });
                            }
                            col.Item().PaddingVertical(6);
                        }

                        // ═══ 12. 结论 ═══
                        SectionTitle(col, "分析结论");
                        col.Item().Table(t =>
                        {
                            t.ColumnsDefinition(c => { c.ConstantColumn(80); c.RelativeColumn(); });

                            string r2Eval = s.RSquared > 0.95 ? $"R² = {s.RSquared:F4}，拟合优秀"
                                : s.RSquared > 0.85 ? $"R² = {s.RSquared:F4}，拟合良好"
                                : $"R² = {s.RSquared:F4}，建议改进";
                            ConcRow(t, "模型质量", r2Eval);

                            double gap = Math.Abs(s.RSquaredAdj - s.RSquaredPred);
                            ConcRow(t, "过拟合", gap < 0.2 ? $"差值 {gap:F4} < 0.2，无风险" : $"差值 {gap:F4} > 0.2，有风险");

                            if (s.LackOfFitP.HasValue)
                                ConcRow(t, "失拟检验", s.LackOfFitP.Value > 0.05
                                    ? $"P={s.LackOfFitP.Value:F4}，模型充分" : $"P={s.LackOfFitP.Value:F4}，可能不充分");

                            if (vm.DisplayCoefficients != null)
                            {
                                var sigTerms = vm.DisplayCoefficients
                                    .Where(c => c.Term != "Intercept" && c.PValue < 0.05)
                                    .Select(c => c.Term).ToList();
                                ConcRow(t, "显著因子", sigTerms.Count > 0 ? string.Join(", ", sigTerms) : "无 (α=0.05)");
                            }

                            if (!string.IsNullOrEmpty(vm.ExcludedTermsText) && vm.ExcludedTermsText != "无")
                                ConcRow(t, "排除项", vm.ExcludedTermsText);
                        });
                    });
                });
            }).GeneratePdf(outputPath);
        }

        // ═══ 辅助方法 ═══

        void SectionTitle(ColumnDescriptor col, string title)
        {
            col.Item().PaddingTop(4).PaddingBottom(2).Row(row =>
            {
                row.ConstantItem(3).Height(18).Background(Colors.Indigo.Darken3);
                row.ConstantItem(4);
                row.RelativeItem().AlignBottom().Text(title).FontSize(11).Bold().FontColor(Colors.Indigo.Darken3);
            });
        }

        void Kpi(TableDescriptor t, string label, string value, bool first)
        {
            t.Cell().Border(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(6).Column(c =>
            {
                c.Item().AlignCenter().Text(value).FontSize(14).Bold().FontColor(Colors.Indigo.Darken2);
                c.Item().AlignCenter().Text(label).FontSize(7).FontColor(Colors.Grey.Medium);
            });
        }

        void THeader(TableDescriptor t, params string[] headers)
        {
            t.Header(h =>
            {
                foreach (var hdr in headers)
                    h.Cell().Background(Colors.Indigo.Darken3).PaddingVertical(4).PaddingHorizontal(4)
                        .Text(hdr).FontSize(8).Bold().FontColor(Colors.White);
            });
        }

        void TCell(TableDescriptor t, string text)
        {
            t.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten3)
                .PaddingVertical(3).PaddingHorizontal(4)
                .Text(text).FontSize(8);
        }

        void TNum(TableDescriptor t, string text)
        {
            t.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten3)
                .PaddingVertical(3).PaddingHorizontal(4).AlignRight()
                .Text(text).FontSize(8).FontFamily("Consolas");
        }

        void ConcRow(TableDescriptor t, string label, string value)
        {
            t.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).PaddingVertical(3).PaddingHorizontal(4)
                .Text(label).FontSize(9).Bold().FontColor(Colors.Indigo.Darken2);
            t.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).PaddingVertical(3).PaddingHorizontal(4)
                .Text(value).FontSize(9);
        }

        void PlotImage(IContainer container, PlotModel? model, int w, int h)
        {
            if (model == null)
            {
                container.Height(h / 2).Background(Colors.Grey.Lighten4).AlignCenter().AlignMiddle()
                    .Text("(图表未生成)").FontSize(9).FontColor(Colors.Grey.Medium);
                return;
            }
            try
            {
                byte[]? data = null;
                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    var exp = new OxyPlot.Wpf.PngExporter { Width = w * 2, Height = h * 2 };
                    using var ms = new MemoryStream();
                    exp.Export(model, ms);
                    data = ms.ToArray();
                });
                if (data != null && data.Length > 100)
                    container.Image(data).FitWidth();
                else
                    container.Height(h / 2).Background(Colors.Grey.Lighten4).AlignCenter().AlignMiddle()
                        .Text("(导出失败)").FontSize(9).FontColor(Colors.Grey.Medium);
            }
            catch
            {
                container.Height(h / 2).Background(Colors.Grey.Lighten4).AlignCenter().AlignMiddle()
                    .Text("(导出异常)").FontSize(9).FontColor(Colors.Grey.Medium);
            }
        }
    }
}
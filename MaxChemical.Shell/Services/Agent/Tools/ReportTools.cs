using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MaxChemical.Modules.DOE.Data;
using MaxChemical.Modules.DOE.Models;

namespace MaxChemical.Shell.Services.Agent.Tools
{
    /// <summary>
    /// 一键实验报告:把项目(或独立批次)的概览、各轮摘要、趋势图与逐轮明细
    /// 排成正式 PDF 存到用户文档目录,并在资源管理器里定位文件。
    /// 数据全部来自数据库,图表复用聊天窗同款绘图,不经模型转述。
    /// </summary>
    public sealed class GenerateProjectReportTool : IAgentTool
    {
        private readonly AgentToolContext _ctx;
        public GenerateProjectReportTool(AgentToolContext ctx) => _ctx = ctx;

        public string Name => "generate_project_report";
        public string DisplayName => "生成实验报告";
        public string Description => @"为 DOE 项目(或独立批次)生成一份正式的 PDF 实验报告:概览、各轮摘要(设计方法/组数/本轮最优/引擎推荐)、优化趋势图、逐轮因子与实验数据明细。文件保存到用户文档目录并自动在资源管理器定位。
name 传项目名、项目编号或批次ID;重名歧义时会返回候选表,按编号精确重试。报告内容完全来自数据库,生成后把文件路径与内容纲要汇报给用户。只读(仅在文档目录写 PDF 文件)。";
        public string ParametersSchema => @"{
  ""type"":""object"",
  ""properties"":{
    ""name"":{""type"":""string"",""description"":""项目名/项目编号,或独立批次的批次名/批次ID""},
    ""reveal"":{""type"":""boolean"",""description"":""生成后是否在资源管理器里定位文件,默认 true""}
  },
  ""required"":[""name""]
}";
        public bool RequiresConfirmation => false;
        public string DescribeAction(JsonElement args)
            => $"生成实验报告:{AgentToolContext.GetString(args, "name")}";

        public async Task<AgentToolResult> ExecuteAsync(JsonElement args)
        {
            string name = AgentToolContext.GetString(args, "name");
            if (string.IsNullOrWhiteSpace(name))
                return AgentToolResult.Fail("name 必填:项目名、项目编号或批次ID。");
            bool reveal = AgentToolContext.GetBool(args, "reveal") ?? true;

            IDOERepository repo;
            try { repo = _ctx.Resolve<IDOERepository>(); }
            catch (Exception ex) { return AgentToolResult.Fail("数据库服务不可用:" + ex.Message); }

            // ① 先按项目找;找不到项目再按独立批次找
            var data = new ProjectReportData();
            var (proj, projErr) = await DoeAnalysisHelper.FindProjectAsync(repo, name).ConfigureAwait(false);
            if (proj != null)
            {
                var detail = await repo.GetProjectWithDetailsAsync(proj.ProjectId).ConfigureAwait(false);
                if (detail == null)
                    return AgentToolResult.Fail($"项目 {proj.ProjectId} 档案读取失败,请稍后重试。");

                data.Project = detail;
                data.RoundSummaries = (detail.RoundSummaries ?? new List<DOERoundSummary>())
                    .OrderBy(s => s.RoundNumber).ToList();

                var batches = (detail.Batches ?? new List<DOEBatch>())
                    .OrderBy(b => b.RoundNumber ?? int.MaxValue).ThenBy(b => b.CreatedTime).ToList();
                foreach (var b in batches)
                {
                    var full = await repo.GetBatchWithDetailsAsync(b.BatchId).ConfigureAwait(false);
                    if (full != null) data.Batches.Add(full);
                }
                if (data.Batches.Count == 0)
                    return AgentToolResult.Fail($"项目「{detail.ProjectName}」下还没有任何批次,没有内容可写进报告。");
            }
            else
            {
                // 歧义候选表原样交回(模型按表处理),彻底找不到才试批次
                if (projErr != null && projErr.Contains("必须用项目编号区分"))
                    return AgentToolResult.Ok(projErr);

                var (batch, batchErr) = await DoeAnalysisHelper.FindBatchAsync(repo, name).ConfigureAwait(false);
                if (batch == null)
                    return AgentToolResult.Fail($"按项目找:{projErr};按批次找:{batchErr}");

                var full = batch.Runs != null && batch.Runs.Count > 0
                    ? batch
                    : await repo.GetBatchWithDetailsAsync(batch.BatchId).ConfigureAwait(false);
                if (full == null)
                    return AgentToolResult.Fail($"批次 {batch.BatchId} 明细读取失败,请稍后重试。");
                data.Batches.Add(full);

                // 批次挂在项目下:报告以项目为主体,必须把项目的全部轮次都装进来——
                // 只装一轮却套项目版面,会把五轮项目写成「1 轮」的正式报告。
                // 全部轮次装载成功才切换成项目报告;中途失败保持诚实的单批次报告。
                if (!string.IsNullOrEmpty(full.ProjectId))
                {
                    try
                    {
                        var pd = await repo.GetProjectWithDetailsAsync(full.ProjectId).ConfigureAwait(false);
                        if (pd != null)
                        {
                            var loaded = new List<DOEBatch>();
                            foreach (var pb in (pd.Batches ?? new List<DOEBatch>())
                                         .OrderBy(x => x.RoundNumber ?? int.MaxValue).ThenBy(x => x.CreatedTime))
                            {
                                var fb = string.Equals(pb.BatchId, full.BatchId, StringComparison.OrdinalIgnoreCase)
                                    ? full
                                    : await repo.GetBatchWithDetailsAsync(pb.BatchId).ConfigureAwait(false);
                                if (fb != null) loaded.Add(fb);
                            }
                            if (loaded.Count > 0)
                            {
                                data.Project = pd;
                                data.RoundSummaries = (pd.RoundSummaries ?? new List<DOERoundSummary>())
                                    .OrderBy(s => s.RoundNumber).ToList();
                                data.Batches.Clear();
                                data.Batches.AddRange(loaded);
                            }
                        }
                    }
                    catch { }
                }
            }

            // ② 主响应优化方向(期望函数配置;缺省最大化)
            try
            {
                var lastWithResp = data.Batches.LastOrDefault(b => b.Responses != null && b.Responses.Count > 0);
                string respName = lastWithResp?.Responses.FirstOrDefault()?.ResponseName;
                if (lastWithResp != null && !string.IsNullOrEmpty(respName))
                    data.Minimize = await DoeAnalysisHelper.IsMinimizeAsync(repo, lastWithResp.BatchId, respName).ConfigureAwait(false);
            }
            catch { }

            // ③ 生成 PDF(图表在 UI 线程离屏渲染,失败只缺图不挡报告)
            string path;
            try { path = ProjectReportBuilder.Export(data); }
            catch (Exception ex) { return AgentToolResult.Fail("报告生成失败:" + ex.Message); }

            if (reveal)
            {
                try { System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\""); }
                catch { }
            }

            int totalRuns = data.Batches.Sum(b => b.Runs?.Count ?? 0);
            int doneRuns = data.Batches.Sum(b => (b.Runs ?? new List<DOERunRecord>()).Count(r => r.Status == DOERunStatus.Completed));
            string subject = data.Project?.ProjectName ?? data.Batches[0].BatchName;

            return AgentToolResult.Ok(
                $"实验报告已生成:「{subject}」\n" +
                $"- 文件:{path}\n" +
                $"- 范围:{data.Batches.Count} 个批次,{doneRuns}/{totalRuns} 组已完成实验\n" +
                $"- 内容:概览、各轮摘要、优化趋势图、逐轮因子与数据明细\n" +
                (reveal ? "已在资源管理器中定位该文件。" : "") +
                "把文件路径与内容纲要转告用户;报告基于当前数据库快照,补做实验后需重新生成。");
        }
    }
}

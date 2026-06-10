using MaxChemical.Data.Models;
using MaxChemical.Data.Repositories;
using MaxChemical.Logging;
using OfficeOpenXml;
using OfficeOpenXml.Style;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace MaxChemical.Data.Services
{
    public class ExcelExportService : IExcelExportService
    {
        private readonly IExperimentDataRepository _repository;
        private readonly ILogService _logger;

        public ExcelExportService(IExperimentDataRepository repository, ILogService logger)
        {
            _repository = repository ?? throw new ArgumentNullException(nameof(repository));
            _logger = logger?.ForContext<ExcelExportService>() ?? throw new ArgumentNullException(nameof(logger));
            ExcelPackage.LicenseContext = OfficeOpenXml.LicenseContext.NonCommercial;
        }

        public async Task<string> ExportExperimentsAsync(List<ExperimentRecord> experiments, string fileName)
        {
            try
            {
                using var package = new ExcelPackage();

                // 实验列表工作表
                CreateExperimentListWorksheet(package, experiments);

                // 节点执行记录工作表
                if (experiments.Any(e => e.NodeRecords.Any()))
                {
                    CreateNodeRecordsWorksheet(package, experiments);
                }

                // 参数变化记录工作表（关键：去重后的数据）
                await CreateParameterChangesWorksheetAsync(package, experiments);

                var fileInfo = new FileInfo(fileName);
                await package.SaveAsAsync(fileInfo);

                _logger.LogInformation("导出实验记录Excel成功: {FileName}, 记录数: {Count}", fileName, experiments.Count);
                return fileName;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "导出实验记录Excel失败: {FileName}", fileName);
                throw;
            }
        }

        // 关键方法：导出参数变化记录（去重处理）
        public async Task<string> ExportParameterChangesAsync(List<ParameterChangeRecord> parameterChanges, string fileName)
        {
            try
            {
                using var package = new ExcelPackage();

                var worksheet = package.Workbook.Worksheets.Add("参数变化记录");

                var headers = new[]
                {
                    "实验名称", "设备ID", "设备名称", "参数名称", "参数值", "前一个值",
                    "单位", "变化时间", "命令名称", "节点名称", "变化序号", "距上次变化时间", "变化类型"
                };

                // 设置标题
                for (int i = 0; i < headers.Length; i++)
                {
                    worksheet.Cells[1, i + 1].Value = headers[i];
                }

                // 设置标题样式
                using (var range = worksheet.Cells[1, 1, 1, headers.Length])
                {
                    range.Style.Font.Bold = true;
                    range.Style.Fill.PatternType = ExcelFillStyle.Solid;
                    range.Style.Fill.BackgroundColor.SetColor(Color.LightBlue);
                    range.Style.Border.BorderAround(ExcelBorderStyle.Thin);
                }

                // 填充数据
                for (int i = 0; i < parameterChanges.Count; i++)
                {
                    var change = parameterChanges[i];
                    var row = i + 2;

                    worksheet.Cells[row, 1].Value = change.ExperimentName;
                    worksheet.Cells[row, 2].Value = change.DeviceId;
                    worksheet.Cells[row, 3].Value = change.DeviceName;
                    worksheet.Cells[row, 4].Value = change.ParameterName;
                    worksheet.Cells[row, 5].Value = change.ParameterValue;
                    worksheet.Cells[row, 6].Value = change.PreviousValue ?? "首次记录";
                    worksheet.Cells[row, 7].Value = change.Unit ?? "";
                    worksheet.Cells[row, 8].Value = change.Timestamp;
                    worksheet.Cells[row, 9].Value = change.CommandName ?? "";
                    worksheet.Cells[row, 10].Value = change.NodeName ?? "";
                    worksheet.Cells[row, 11].Value = change.ChangeSequence;
                    worksheet.Cells[row, 12].Value = change.TimeSinceLastChange?.ToString(@"hh\:mm\:ss\.fff") ?? "";
                    worksheet.Cells[row, 13].Value = change.IsFirstRecord ? "首次记录" : "参数变化";

                    // 设置变化类型颜色
                    if (change.IsFirstRecord)
                    {
                        using (var range = worksheet.Cells[row, 1, row, headers.Length])
                        {
                            range.Style.Fill.PatternType = ExcelFillStyle.Solid;
                            range.Style.Fill.BackgroundColor.SetColor(Color.LightBlue);
                        }
                        worksheet.Cells[row, 13].Style.Font.Bold = true;
                    }
                    else if (change.IsValueChanged)
                    {
                        using (var range = worksheet.Cells[row, 1, row, headers.Length])
                        {
                            range.Style.Fill.PatternType = ExcelFillStyle.Solid;
                            range.Style.Fill.BackgroundColor.SetColor(Color.LightGreen);
                        }
                        worksheet.Cells[row, 13].Style.Font.Bold = true;
                    }
                }

                // 设置时间格式
                worksheet.Cells[2, 8, parameterChanges.Count + 1, 8].Style.Numberformat.Format = "yyyy-mm-dd hh:mm:ss.fff";

                // 自动调整列宽
                worksheet.Cells.AutoFitColumns();

                var fileInfo = new FileInfo(fileName);
                await package.SaveAsAsync(fileInfo);

                _logger.LogInformation("导出参数变化记录Excel成功: {FileName}, 记录数: {Count}", fileName, parameterChanges.Count);
                return fileName;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "导出参数变化记录Excel失败: {FileName}", fileName);
                throw;
            }
        }

        // 导出单个实验的完整参数变化记录
        public async Task<string> ExportExperimentWithParameterChangesAsync(string experimentId, string fileName)
        {
            try
            {
                using var package = new ExcelPackage();

                // 获取实验基本信息
                var experiment = await _repository.GetExperimentAsync(experimentId);
                if (experiment == null)
                {
                    throw new ArgumentException($"未找到实验记录: {experimentId}");
                }

                // 实验基本信息工作表
                CreateExperimentInfoWorksheet(package, experiment);

                // 参数变化记录工作表
                var parameterChanges = await _repository.GetParameterChangesForExportAsync(experimentId);
                if (parameterChanges.Any())
                {
                    CreateParameterChangesWorksheet(package, parameterChanges, "参数变化记录");
                }

                // 节点执行记录工作表
                if (experiment.NodeRecords.Any())
                {
                    CreateSingleExperimentNodeWorksheet(package, experiment);
                }

                var fileInfo = new FileInfo(fileName);
                await package.SaveAsAsync(fileInfo);

                _logger.LogInformation("导出实验详情Excel成功: {FileName}, 实验ID: {ExperimentId}", fileName, experimentId);
                return fileName;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "导出实验详情Excel失败: {FileName}", fileName);
                throw;
            }
        }

        public async Task<string> ExportStatisticsAsync(ExperimentStatistics statistics, List<ExperimentTrendData> trendData, List<DeviceUsageStatistics> deviceStats, string fileName)
        {
            try
            {
                using var package = new ExcelPackage();

                CreateStatisticsOverviewWorksheet(package, statistics);

                if (trendData.Any())
                {
                    CreateTrendDataWorksheet(package, trendData);
                }

                if (deviceStats.Any())
                {
                    CreateDeviceStatsWorksheet(package, deviceStats);
                }

                var fileInfo = new FileInfo(fileName);
                await package.SaveAsAsync(fileInfo);

                _logger.LogInformation("导出统计分析Excel成功: {FileName}", fileName);
                return fileName;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "导出统计分析Excel失败: {FileName}", fileName);
                throw;
            }
        }

        public async Task<string> ExportExperimentDetailAsync(ExperimentRecord experiment, string fileName)
        {
            try
            {
                return await ExportExperimentWithParameterChangesAsync(experiment.ExperimentId, fileName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "导出实验详情Excel失败: {FileName}", fileName);
                throw;
            }
        }

        // 私有辅助方法
        private void CreateExperimentListWorksheet(ExcelPackage package, List<ExperimentRecord> experiments)
        {
            var worksheet = package.Workbook.Worksheets.Add("实验记录");

            var headers = new[]
            {
                "实验ID", "实验名称", "流程名称", "开始时间", "结束时间", "持续时间",
                "执行状态", "运行模式", "总节点数", "完成节点数", "完成度", "操作员", "描述", "标签", "错误信息"
            };

            for (int i = 0; i < headers.Length; i++)
            {
                worksheet.Cells[1, i + 1].Value = headers[i];
            }

            using (var range = worksheet.Cells[1, 1, 1, headers.Length])
            {
                range.Style.Font.Bold = true;
                range.Style.Fill.PatternType = ExcelFillStyle.Solid;
                range.Style.Fill.BackgroundColor.SetColor(Color.LightBlue);
                range.Style.Border.BorderAround(ExcelBorderStyle.Thin);
            }

            for (int i = 0; i < experiments.Count; i++)
            {
                var exp = experiments[i];
                var row = i + 2;

                worksheet.Cells[row, 1].Value = exp.ExperimentId;
                worksheet.Cells[row, 2].Value = exp.ExperimentName;
                worksheet.Cells[row, 3].Value = exp.FlowName;
                worksheet.Cells[row, 4].Value = exp.StartTime;
                worksheet.Cells[row, 5].Value = exp.EndTime;
                worksheet.Cells[row, 6].Value = exp.Duration?.ToString(@"hh\:mm\:ss") ?? "";
                worksheet.Cells[row, 7].Value = exp.StatusText;
                worksheet.Cells[row, 8].Value = exp.ModeText;
                worksheet.Cells[row, 9].Value = exp.TotalNodes;
                worksheet.Cells[row, 10].Value = exp.CompletedNodes;
                worksheet.Cells[row, 11].Value = $"{exp.ProgressPercentage:F1}%";
                worksheet.Cells[row, 12].Value = exp.OperatorName ?? "";
                worksheet.Cells[row, 13].Value = exp.Description ?? "";
                worksheet.Cells[row, 14].Value = exp.Tags ?? "";
                worksheet.Cells[row, 15].Value = exp.ErrorMessage ?? "";

                if (exp.IsSuccessful)
                {
                    worksheet.Cells[row, 7].Style.Font.Color.SetColor(Color.Green);
                }
                else
                {
                    worksheet.Cells[row, 7].Style.Font.Color.SetColor(Color.Red);
                }
            }

            worksheet.Cells[2, 4, experiments.Count + 1, 5].Style.Numberformat.Format = "yyyy-mm-dd hh:mm:ss";
            worksheet.Cells.AutoFitColumns();
        }

        private void CreateNodeRecordsWorksheet(ExcelPackage package, List<ExperimentRecord> experiments)
        {
            var worksheet = package.Workbook.Worksheets.Add("节点执行记录");

            var headers = new[]
            {
                "实验ID", "实验名称", "节点ID", "节点名称", "节点类型", "设备ID", "设备名称",
                "命令名称", "开始时间", "结束时间", "持续时间(ms)", "执行状态", "执行顺序", "错误信息"
            };

            for (int i = 0; i < headers.Length; i++)
            {
                worksheet.Cells[1, i + 1].Value = headers[i];
            }

            using (var range = worksheet.Cells[1, 1, 1, headers.Length])
            {
                range.Style.Font.Bold = true;
                range.Style.Fill.PatternType = ExcelFillStyle.Solid;
                range.Style.Fill.BackgroundColor.SetColor(Color.LightGreen);
            }

            int row = 2;
            foreach (var exp in experiments)
            {
                foreach (var node in exp.NodeRecords)
                {
                    worksheet.Cells[row, 1].Value = exp.ExperimentId;
                    worksheet.Cells[row, 2].Value = exp.ExperimentName;
                    worksheet.Cells[row, 3].Value = node.NodeId;
                    worksheet.Cells[row, 4].Value = node.NodeName;
                    worksheet.Cells[row, 5].Value = node.NodeType;
                    worksheet.Cells[row, 6].Value = node.DeviceId ?? "";
                    worksheet.Cells[row, 7].Value = node.DeviceName ?? "";
                    worksheet.Cells[row, 8].Value = node.CommandName ?? "";
                    worksheet.Cells[row, 9].Value = node.StartTime;
                    worksheet.Cells[row, 10].Value = node.EndTime;
                    worksheet.Cells[row, 11].Value = node.DurationMs;
                    worksheet.Cells[row, 12].Value = node.StatusText;
                    worksheet.Cells[row, 13].Value = node.ExecutionOrder;
                    worksheet.Cells[row, 14].Value = node.ErrorMessage ?? "";

                    if (node.IsSuccessful)
                    {
                        worksheet.Cells[row, 12].Style.Font.Color.SetColor(Color.Green);
                    }
                    else
                    {
                        worksheet.Cells[row, 12].Style.Font.Color.SetColor(Color.Red);
                    }

                    row++;
                }
            }

            worksheet.Cells[2, 9, row - 1, 10].Style.Numberformat.Format = "yyyy-mm-dd hh:mm:ss";
            worksheet.Cells.AutoFitColumns();
        }

        // 关键方法：创建参数变化记录工作表
        private async Task CreateParameterChangesWorksheetAsync(ExcelPackage package, List<ExperimentRecord> experiments)
        {
            try
            {
                var allParameterChanges = new List<ParameterChangeRecord>();

                foreach (var experiment in experiments)
                {
                    var changes = await _repository.GetParameterChangesForExportAsync(experiment.ExperimentId);
                    allParameterChanges.AddRange(changes);
                }

                if (allParameterChanges.Any())
                {
                    CreateParameterChangesWorksheet(package, allParameterChanges, "设备参数变化记录");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "创建参数变化记录工作表失败");
            }
        }

        private void CreateParameterChangesWorksheet(ExcelPackage package, List<ParameterChangeRecord> parameterChanges, string worksheetName)
        {
            var worksheet = package.Workbook.Worksheets.Add(worksheetName);

            var headers = new[]
            {
                "实验名称", "设备ID", "设备名称", "参数名称", "参数值", "前一个值",
                "单位", "变化时间", "命令名称", "节点名称", "变化序号", "距上次变化时间", "变化类型"
            };

            for (int i = 0; i < headers.Length; i++)
            {
                worksheet.Cells[1, i + 1].Value = headers[i];
            }

            using (var range = worksheet.Cells[1, 1, 1, headers.Length])
            {
                range.Style.Font.Bold = true;
                range.Style.Fill.PatternType = ExcelFillStyle.Solid;
                range.Style.Fill.BackgroundColor.SetColor(Color.LightYellow);
                range.Style.Border.BorderAround(ExcelBorderStyle.Thin);
            }

            for (int i = 0; i < parameterChanges.Count; i++)
            {
                var change = parameterChanges[i];
                var row = i + 2;

                worksheet.Cells[row, 1].Value = change.ExperimentName;
                worksheet.Cells[row, 2].Value = change.DeviceId;
                worksheet.Cells[row, 3].Value = change.DeviceName;
                worksheet.Cells[row, 4].Value = change.ParameterName;
                worksheet.Cells[row, 5].Value = change.ParameterValue;
                worksheet.Cells[row, 6].Value = change.PreviousValue ?? "首次记录";
                worksheet.Cells[row, 7].Value = change.Unit ?? "";
                worksheet.Cells[row, 8].Value = change.Timestamp;
                worksheet.Cells[row, 9].Value = change.CommandName ?? "";
                worksheet.Cells[row, 10].Value = change.NodeName ?? "";
                worksheet.Cells[row, 11].Value = change.ChangeSequence;
                worksheet.Cells[row, 12].Value = change.TimeSinceLastChange?.ToString(@"hh\:mm\:ss\.fff") ?? "";
                worksheet.Cells[row, 13].Value = change.IsFirstRecord ? "首次记录" : "参数变化";

                // 突出显示变化类型
                if (change.IsFirstRecord)
                {
                    using (var range = worksheet.Cells[row, 1, row, headers.Length])
                    {
                        range.Style.Fill.PatternType = ExcelFillStyle.Solid;
                        range.Style.Fill.BackgroundColor.SetColor(Color.LightBlue);
                    }
                    worksheet.Cells[row, 13].Style.Font.Bold = true;
                }
                else if (change.IsValueChanged)
                {
                    using (var range = worksheet.Cells[row, 1, row, headers.Length])
                    {
                        range.Style.Fill.PatternType = ExcelFillStyle.Solid;
                        range.Style.Fill.BackgroundColor.SetColor(Color.LightGreen);
                    }
                    worksheet.Cells[row, 13].Style.Font.Bold = true;
                }
            }

            worksheet.Cells[2, 8, parameterChanges.Count + 1, 8].Style.Numberformat.Format = "yyyy-mm-dd hh:mm:ss.fff";
            worksheet.Cells.AutoFitColumns();
        }

        private void CreateExperimentInfoWorksheet(ExcelPackage package, ExperimentRecord experiment)
        {
            var worksheet = package.Workbook.Worksheets.Add("实验信息");

            worksheet.Cells[1, 1].Value = "实验详细信息";
            worksheet.Cells[1, 1].Style.Font.Size = 16;
            worksheet.Cells[1, 1].Style.Font.Bold = true;

            var info = new object[,]
            {
                { "实验ID", experiment.ExperimentId },
                { "实验名称", experiment.ExperimentName },
                { "流程名称", experiment.FlowName },
                { "开始时间", experiment.StartTime.ToString("yyyy-MM-dd HH:mm:ss") },
                { "结束时间", experiment.EndTime?.ToString("yyyy-MM-dd HH:mm:ss") ?? "未完成" },
                { "持续时间", experiment.Duration?.ToString(@"hh\:mm\:ss") ?? "未完成" },
                { "执行状态", experiment.StatusText },
                { "运行模式", experiment.ModeText },
                { "总节点数", experiment.TotalNodes },
                { "完成节点数", experiment.CompletedNodes },
                { "完成度", $"{experiment.ProgressPercentage:F1}%" },
                { "操作员", experiment.OperatorName ?? "" },
                { "描述", experiment.Description ?? "" },
                { "标签", experiment.Tags ?? "" },
                { "错误信息", experiment.ErrorMessage ?? "" }
            };

            for (int i = 0; i < info.GetLength(0); i++)
            {
                worksheet.Cells[i + 3, 1].Value = info[i, 0];
                worksheet.Cells[i + 3, 2].Value = info[i, 1];
                worksheet.Cells[i + 3, 1].Style.Font.Bold = true;
            }

            worksheet.Cells.AutoFitColumns();
        }

        private void CreateSingleExperimentNodeWorksheet(ExcelPackage package, ExperimentRecord experiment)
        {
            var worksheet = package.Workbook.Worksheets.Add("节点执行详情");

            var headers = new[]
            {
                "执行顺序", "节点名称", "节点类型", "设备名称", "命令名称",
                "开始时间", "结束时间", "持续时间(ms)", "执行状态", "错误信息"
            };

            for (int i = 0; i < headers.Length; i++)
            {
                worksheet.Cells[1, i + 1].Value = headers[i];
            }

            for (int i = 0; i < experiment.NodeRecords.Count; i++)
            {
                var node = experiment.NodeRecords[i];
                var row = i + 2;

                worksheet.Cells[row, 1].Value = node.ExecutionOrder;
                worksheet.Cells[row, 2].Value = node.NodeName;
                worksheet.Cells[row, 3].Value = node.NodeType;
                worksheet.Cells[row, 4].Value = node.DeviceName ?? "";
                worksheet.Cells[row, 5].Value = node.CommandName ?? "";
                worksheet.Cells[row, 6].Value = node.StartTime;
                worksheet.Cells[row, 7].Value = node.EndTime;
                worksheet.Cells[row, 8].Value = node.DurationMs;
                worksheet.Cells[row, 9].Value = node.StatusText;
                worksheet.Cells[row, 10].Value = node.ErrorMessage ?? "";

                if (node.IsSuccessful)
                {
                    worksheet.Cells[row, 9].Style.Font.Color.SetColor(Color.Green);
                }
                else
                {
                    worksheet.Cells[row, 9].Style.Font.Color.SetColor(Color.Red);
                }
            }

            using (var range = worksheet.Cells[1, 1, 1, headers.Length])
            {
                range.Style.Font.Bold = true;
                range.Style.Fill.PatternType = ExcelFillStyle.Solid;
                range.Style.Fill.BackgroundColor.SetColor(Color.LightGreen);
            }

            worksheet.Cells[2, 6, experiment.NodeRecords.Count + 1, 7].Style.Numberformat.Format = "yyyy-mm-dd hh:mm:ss";
            worksheet.Cells.AutoFitColumns();
        }

        private void CreateStatisticsOverviewWorksheet(ExcelPackage package, ExperimentStatistics statistics)
        {
            var worksheet = package.Workbook.Worksheets.Add("统计概览");

            worksheet.Cells[1, 1].Value = "实验统计概览";
            worksheet.Cells[1, 1].Style.Font.Size = 16;
            worksheet.Cells[1, 1].Style.Font.Bold = true;

            var data = new object[,]
            {
                { "统计项目", "数值", "备注" },
                { "总实验数", statistics.TotalExperiments, "" },
                { "成功实验数", statistics.SuccessfulExperiments, "" },
                { "失败实验数", statistics.FailedExperiments, "" },
                { "成功率", $"{statistics.SuccessRate:F1}%", "" },
                { "仿真实验数", statistics.SimulationExperiments, "" },
                { "实际实验数", statistics.RealExperiments, "" },
                { "平均持续时间", statistics.AverageDuration.ToString(@"hh\:mm\:ss"), "" },
                { "最长持续时间", TimeSpan.FromSeconds(statistics.MaxDurationSeconds).ToString(@"hh\:mm\:ss"), "" },
                { "最短持续时间", TimeSpan.FromSeconds(statistics.MinDurationSeconds).ToString(@"hh\:mm\:ss"), "" },
                { "统计时间范围", $"{statistics.StartDate:yyyy-MM-dd} 至 {statistics.EndDate:yyyy-MM-dd}", "" }
            };

            for (int i = 0; i < data.GetLength(0); i++)
            {
                for (int j = 0; j < data.GetLength(1); j++)
                {
                    worksheet.Cells[i + 3, j + 1].Value = data[i, j];
                }
            }

            using (var range = worksheet.Cells[3, 1, 3, 3])
            {
                range.Style.Font.Bold = true;
                range.Style.Fill.PatternType = ExcelFillStyle.Solid;
                range.Style.Fill.BackgroundColor.SetColor(Color.LightBlue);
            }

            worksheet.Cells.AutoFitColumns();
        }

        private void CreateTrendDataWorksheet(ExcelPackage package, List<ExperimentTrendData> trendData)
        {
            var worksheet = package.Workbook.Worksheets.Add("趋势分析");

            var headers = new[] { "时间", "总实验数", "成功数", "失败数", "成功率(%)", "仿真数", "实际数", "平均持续时间(秒)" };

            for (int i = 0; i < headers.Length; i++)
            {
                worksheet.Cells[1, i + 1].Value = headers[i];
            }

            for (int i = 0; i < trendData.Count; i++)
            {
                var data = trendData[i];
                var row = i + 2;

                worksheet.Cells[row, 1].Value = data.TimeLabel;
                worksheet.Cells[row, 2].Value = data.TotalCount;
                worksheet.Cells[row, 3].Value = data.SuccessCount;
                worksheet.Cells[row, 4].Value = data.FailedCount;
                worksheet.Cells[row, 5].Value = data.SuccessRate;
                worksheet.Cells[row, 6].Value = data.SimulationCount;
                worksheet.Cells[row, 7].Value = data.RealCount;
                worksheet.Cells[row, 8].Value = data.AverageDuration;
            }

            using (var range = worksheet.Cells[1, 1, 1, headers.Length])
            {
                range.Style.Font.Bold = true;
                range.Style.Fill.PatternType = ExcelFillStyle.Solid;
                range.Style.Fill.BackgroundColor.SetColor(Color.LightYellow);
            }

            worksheet.Cells.AutoFitColumns();
        }

        private void CreateDeviceStatsWorksheet(ExcelPackage package, List<DeviceUsageStatistics> deviceStats)
        {
            var worksheet = package.Workbook.Worksheets.Add("设备使用统计");

            var headers = new[] { "设备ID", "设备名称", "使用次数", "成功次数", "失败次数", "成功率(%)", "平均执行时间(ms)" };

            for (int i = 0; i < headers.Length; i++)
            {
                worksheet.Cells[1, i + 1].Value = headers[i];
            }

            for (int i = 0; i < deviceStats.Count; i++)
            {
                var stats = deviceStats[i];
                var row = i + 2;

                worksheet.Cells[row, 1].Value = stats.DeviceId;
                worksheet.Cells[row, 2].Value = stats.DeviceName;
                worksheet.Cells[row, 3].Value = stats.UsageCount;
                worksheet.Cells[row, 4].Value = stats.SuccessCount;
                worksheet.Cells[row, 5].Value = stats.FailedCount;
                worksheet.Cells[row, 6].Value = stats.SuccessRate;
                worksheet.Cells[row, 7].Value = stats.AverageDurationMs;
            }

            using (var range = worksheet.Cells[1, 1, 1, headers.Length])
            {
                range.Style.Font.Bold = true;
                range.Style.Fill.PatternType = ExcelFillStyle.Solid;
                range.Style.Fill.BackgroundColor.SetColor(Color.LightCoral);
            }

            worksheet.Cells.AutoFitColumns();
        }
    }
}
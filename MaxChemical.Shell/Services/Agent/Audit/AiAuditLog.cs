using System;
using System.IO;
using System.Text;
using System.Text.Json;
using MaxChemical.Logging;

namespace MaxChemical.Shell.Services.Agent.Audit
{
    /// <summary>
    /// AI 操作审计日志:小桐的每次提问、每个工具调用(参数/是否批准/结果)与回复
    /// 逐行追加到 JSONL 文件(文档\MaxChemical\XiaoTong\audit.jsonl),可追溯可审计。
    /// 写入全程吞异常——审计故障绝不能影响对话功能;超过 50MB 轮转为 audit.1.jsonl。
    /// </summary>
    public sealed class AiAuditLog
    {
        private const long MaxFileBytes = 50L * 1024 * 1024;

        private readonly ILogService _logger = LogManager.GetLogger<AiAuditLog>();
        private readonly object _gate = new();

        public static string LogPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "MaxChemical", "XiaoTong", "audit.jsonl");

        /// <summary>一轮对话开始:来源(文本/语音)与用户原话。</summary>
        public void LogTurnStart(string source, string userText)
            => Append(new { ts = DateTime.Now, kind = "turn", source = source ?? "文本", user = Truncate(userText, 1000) });

        /// <summary>一次工具调用:参数、是否经用户批准(null=无需确认)、成败与截断后的结果。</summary>
        public void LogToolCall(string actor, string toolName, string argsJson, bool? approved, bool success, string resultText)
            => Append(new
            {
                ts = DateTime.Now,
                kind = "tool",
                actor = Truncate(actor, 60),
                tool = Truncate(toolName, 80),
                args = Truncate(argsJson, 1000),
                approved,
                success,
                result = Truncate(resultText, 500)
            });

        /// <summary>小桐的最终文字回复(截断)。</summary>
        public void LogReply(string reply)
            => Append(new { ts = DateTime.Now, kind = "reply", reply = Truncate(reply, 2000) });

        /// <summary>执行异常。</summary>
        public void LogError(string message)
            => Append(new { ts = DateTime.Now, kind = "error", error = Truncate(message, 1000) });

        /// <summary>特殊事件:急停触发、语音确认等。</summary>
        public void LogEvent(string kind, string text)
            => Append(new { ts = DateTime.Now, kind = kind ?? "event", text = Truncate(text, 1000) });

        private void Append(object record)
        {
            try
            {
                string line = JsonSerializer.Serialize(record);
                lock (_gate)
                {
                    string path = LogPath;
                    Directory.CreateDirectory(Path.GetDirectoryName(path));

                    var fi = new FileInfo(path);
                    if (fi.Exists && fi.Length > MaxFileBytes)
                    {
                        File.Move(path, Path.Combine(Path.GetDirectoryName(path), "audit.1.jsonl"), overwrite: true);
                    }

                    File.AppendAllText(path, line + "\n", Encoding.UTF8);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"写审计日志失败: {ex.Message}");
            }
        }

        private static string Truncate(string s, int len)
            => string.IsNullOrEmpty(s) || s.Length <= len ? s : s.Substring(0, len) + "…";
    }
}

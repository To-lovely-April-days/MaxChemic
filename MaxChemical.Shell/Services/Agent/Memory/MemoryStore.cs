using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using MaxChemical.Logging;
using Microsoft.Extensions.Configuration;

namespace MaxChemical.Shell.Services.Agent.Memory
{
    /// <summary>一条长期记忆。</summary>
    public sealed class MemoryFact
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Category { get; set; } = "通用";
        public string Content { get; set; } = "";
        public List<string> Tags { get; set; } = new();
        public DateTime CreatedAt { get; set; } = DateTime.Now;
    }

    public sealed class MemoryFile
    {
        public int Version { get; set; } = 1;
        public List<MemoryFact> Facts { get; set; } = new();
    }

    /// <summary>
    /// 长期记忆:用户偏好、设备别名、常用参数等跨会话事实,JSON 文件持久化。
    /// 写入由 save_memory 工具(LLM 提炼)触发;召回在每轮对话开始时按当前输入打分注入提示词。
    /// 限流与上限防止模型刷屏;检索走本地打分,离线可用。
    /// </summary>
    public sealed class MemoryStore
    {
        private const int MaxContentChars = 500;
        private const int MaxSavesPerMinute = 10;
        private const int RecallFactChars = 200;

        private readonly ILogService _logger = LogManager.GetLogger<MemoryStore>();
        private readonly object _gate = new();
        private readonly Queue<DateTime> _saveTimes = new();
        private MemoryFile _store;
        private bool _loaded;

        /// <summary>记忆条数上限(appsettings → AiMemory:MaxFacts)。</summary>
        public int MaxFacts { get; set; } = 200;

        /// <summary>每轮召回注入提示词的条数(appsettings → AiMemory:RecallTopN)。</summary>
        public int RecallTopN { get; set; } = 5;

        public static string StorePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "MaxChemical", "XiaoTong", "memory.json");

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            WriteIndented = false,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        public MemoryStore(IConfiguration configuration)
        {
            if (int.TryParse(configuration["AiMemory:MaxFacts"], out int maxFacts) && maxFacts > 0)
                MaxFacts = maxFacts;
            if (int.TryParse(configuration["AiMemory:RecallTopN"], out int topN) && topN > 0)
                RecallTopN = topN;
        }

        private MemoryFile Load()
        {
            lock (_gate)
            {
                if (_loaded) return _store;

                _store = new MemoryFile();
                try
                {
                    string path = StorePath;
                    if (!File.Exists(path))
                    {
                        string tmp = path + ".tmp";
                        if (File.Exists(tmp)) path = tmp;
                        else { _loaded = true; return _store; }
                    }
                    _store = JsonSerializer.Deserialize<MemoryFile>(File.ReadAllText(path), JsonOpts) ?? new MemoryFile();
                    foreach (var f in _store.Facts)
                    {
                        f.Tags ??= new List<string>();
                        if (string.IsNullOrEmpty(f.Id)) f.Id = Guid.NewGuid().ToString("N");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"读取长期记忆失败(将从空白开始): {ex.Message}");
                    _store = new MemoryFile();
                }
                _loaded = true;
                return _store;
            }
        }

        /// <summary>保存一条记忆。返回 (是否成功, 给模型的说明)。</summary>
        public (bool Ok, string Message) Save(string content, string category, List<string> tags)
        {
            content = content?.Trim() ?? "";
            if (content.Length == 0)
                return (false, "记忆内容为空,未保存");

            if (content.Length > MaxContentChars)
                return (false, $"记忆内容超过 {MaxContentChars} 字上限,请精简为一句话再保存");

            lock (_gate)
            {
                // 限流:一分钟内最多 10 条,防模型刷屏
                var now = DateTime.Now;
                while (_saveTimes.Count > 0 && (now - _saveTimes.Peek()).TotalSeconds > 60)
                    _saveTimes.Dequeue();
                if (_saveTimes.Count >= MaxSavesPerMinute)
                    return (false, "保存过于频繁(每分钟最多 10 条),本次未保存。挑最重要的记,其余丢弃");

                var store = Load();
                if (store.Facts.Count >= MaxFacts)
                    return (false, $"记忆已达上限 {MaxFacts} 条,本次未保存。如确需保存,请告知用户记忆已满");

                store.Facts.Add(new MemoryFact
                {
                    Category = string.IsNullOrWhiteSpace(category) ? "通用" : category.Trim(),
                    Content = content,
                    Tags = (tags ?? new List<string>()).Where(t => !string.IsNullOrWhiteSpace(t))
                                                     .Select(t => t.Trim())
                                                     .Distinct()
                                                     .Take(5)
                                                     .ToList(),
                    CreatedAt = now
                });
                _saveTimes.Enqueue(now);
                SaveLocked(store);
            }
            return (true, "已保存");
        }

        /// <summary>按当前用户输入召回相关记忆,格式化为注入提示词的文本;无相关记忆返回空串。</summary>
        public string RecallText(string userText, int topN = 0)
        {
            if (string.IsNullOrWhiteSpace(userText)) return "";
            if (topN <= 0) topN = RecallTopN;

            List<MemoryFact> facts;
            lock (_gate) facts = Load().Facts.ToList();
            if (facts.Count == 0) return "";

            var scored = facts
                .Select(f =>
                {
                    // 内容与输入的重叠度 + 标签命中加权(标签是用户自己提炼的关键词)
                    float score = TextMatchHelper.OverlapScore(userText, f.Content);
                    foreach (var tag in f.Tags)
                        if (tag.Length >= 2 && userText.IndexOf(tag, StringComparison.OrdinalIgnoreCase) >= 0)
                            score += 0.5f;
                    if (f.Category.Length >= 2 && userText.IndexOf(f.Category, StringComparison.OrdinalIgnoreCase) >= 0)
                        score += 0.25f;
                    return (Fact: f, Score: score);
                })
                .Where(t => t.Score > 0f)
                .OrderByDescending(t => t.Score)
                .Take(topN)
                .ToList();

            if (scored.Count == 0) return "";

            var sb = new StringBuilder();
            sb.AppendLine("## 长期记忆(与本轮相关的用户偏好/约定片段)");
            foreach (var (fact, _) in scored)
            {
                string content = fact.Content.Length <= RecallFactChars
                    ? fact.Content
                    : fact.Content.Substring(0, RecallFactChars) + "…";
                sb.AppendLine($"- [{fact.Category}] {content}({fact.CreatedAt:yyyy-MM-dd})");
            }
            sb.Append("以上是系统自动召回的用户长期记忆,回答时可自然引用;引用时不必解释记忆机制。");
            return sb.ToString();
        }

        private void SaveLocked(MemoryFile store)
        {
            try
            {
                string path = StorePath;
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(store, JsonOpts));
                File.Move(tmp, path, overwrite: true);
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"保存长期记忆失败: {ex.Message}");
            }
        }
    }
}

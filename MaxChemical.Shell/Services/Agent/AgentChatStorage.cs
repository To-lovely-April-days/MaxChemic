using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using MaxChemical.Logging;

namespace MaxChemical.Shell.Services.Agent
{
    /// <summary>持久化的单条聊天条目(界面展示用)。</summary>
    public sealed class ChatItemRecord
    {
        public string Kind { get; set; } = "assistant";   // user / assistant / tool / confirm / chart / choice
        public string Text { get; set; } = "";
        public string ToolName { get; set; }
        public string ToolStage { get; set; }
        public bool ToolSuccess { get; set; } = true;
        public bool IsError { get; set; }
        public bool Approved { get; set; }

        /// <summary>选择卡的已选结果(Kind=choice 时使用;旧档案无此字段,反序列化为 null)</summary>
        public string ChoiceResult { get; set; }
        public DateTime Time { get; set; } = DateTime.Now;
    }

    /// <summary>持久化的一个会话:界面条目 + 模型历史。</summary>
    public sealed class ConversationRecord
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Title { get; set; } = "新对话";
        public bool TitleLocked { get; set; }
        public string Preview { get; set; } = "";
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public DateTime LastActiveAt { get; set; } = DateTime.Now;
        public List<ChatItemRecord> Items { get; set; } = new();
        public List<AgentMessage> History { get; set; } = new();
    }

    public sealed class ChatStoreFile
    {
        public int Version { get; set; } = 1;
        public List<ConversationRecord> Conversations { get; set; } = new();
    }

    /// <summary>
    /// 小桐对话持久化:JSON 存于 文档\MaxChemical\XiaoTong\conversations.json
    /// (与工程文件同在 MaxChemical 目录下,便于备份迁移)。
    /// 读写全程吞异常——持久化故障绝不能影响对话功能。
    /// </summary>
    public sealed class AgentChatStorage
    {
        private const int MaxConversations = 50;
        private const int MaxItemsPerConversation = 200;
        private const int MaxHistoryPerConversation = 60;

        private readonly ILogService _logger = LogManager.GetLogger<AgentChatStorage>();
        private readonly object _gate = new();
        private long _seq;      // 快照提交序号(在界面线程取快照时分配)
        private long _written;  // 已落盘的最大序号,旧快照迟到时直接跳过

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            WriteIndented = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        public static string StorePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "MaxChemical", "XiaoTong", "conversations.json");

        public List<ConversationRecord> Load()
        {
            try
            {
                string path = StorePath;
                // 主文件缺失时回读 .tmp:进程若恰好死在「删旧文件→改名」之间,数据完整地留在临时文件里
                if (!File.Exists(path))
                {
                    string tmp = path + ".tmp";
                    if (File.Exists(tmp)) path = tmp;
                    else return new List<ConversationRecord>();
                }

                var file = JsonSerializer.Deserialize<ChatStoreFile>(File.ReadAllText(path), JsonOpts);
                var list = file?.Conversations ?? new List<ConversationRecord>();

                // 防御:字段缺失/顺序破坏时逐条兜底
                foreach (var c in list)
                {
                    c.Items ??= new List<ChatItemRecord>();
                    c.History ??= new List<AgentMessage>();
                    if (string.IsNullOrEmpty(c.Id)) c.Id = Guid.NewGuid().ToString("N");
                }
                return list
                    .OrderByDescending(c => c.LastActiveAt)
                    .Take(MaxConversations)
                    .ToList();
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"读取小桐会话存档失败(将从空白开始): {ex.Message}");
                return new List<ConversationRecord>();
            }
        }

        /// <summary>在取快照的线程上分配写入序号,保证「后取的快照后落盘」。</summary>
        public long NextVersion() => System.Threading.Interlocked.Increment(ref _seq);

        public void Save(List<ConversationRecord> conversations) => Save(conversations, NextVersion());

        public void Save(List<ConversationRecord> conversations, long version)
        {
            try
            {
                var trimmed = (conversations ?? new List<ConversationRecord>())
                    .OrderByDescending(c => c.LastActiveAt)
                    .Take(MaxConversations)
                    .ToList();

                foreach (var c in trimmed)
                {
                    if (c.Items.Count > MaxItemsPerConversation)
                        c.Items = c.Items.Skip(c.Items.Count - MaxItemsPerConversation).ToList();
                    if (c.History.Count > MaxHistoryPerConversation)
                        c.History = TrimHistoryPairSafe(c.History, MaxHistoryPerConversation);
                }

                string path = StorePath;
                Directory.CreateDirectory(Path.GetDirectoryName(path));

                // 先写临时文件再原子替换,避免进程中断留下半截 JSON
                string tmp = path + ".tmp";
                lock (_gate)
                {
                    // 后台保存不保证到达顺序:迟到的旧快照直接丢弃,防止覆盖更新的存档
                    if (version < _written) return;
                    _written = version;

                    File.WriteAllText(tmp, JsonSerializer.Serialize(
                        new ChatStoreFile { Conversations = trimmed }, JsonOpts));
                    File.Move(tmp, path, overwrite: true);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"保存小桐会话存档失败: {ex.Message}");
            }
        }

        /// <summary>从头裁剪历史到目标条数,但不把 tool 消息与其 assistant(tool_calls) 拆散。</summary>
        private static List<AgentMessage> TrimHistoryPairSafe(List<AgentMessage> history, int max)
        {
            var list = new List<AgentMessage>(history);
            while (list.Count > max)
            {
                list.RemoveAt(0);
                while (list.Count > 0 && list[0].Role == AgentRole.Tool)
                    list.RemoveAt(0);
            }
            return list;
        }
    }
}

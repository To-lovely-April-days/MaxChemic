using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MaxChemical.Logging;

namespace MaxChemical.Shell.Services.Agent.Knowledge
{
    /// <summary>知识库存档文件(JSON,与 conversations.json 同目录)。</summary>
    public sealed class KnowledgeStoreFile
    {
        public int Version { get; set; } = 1;
        public List<KnowledgeDoc> Documents { get; set; } = new();
    }

    /// <summary>一份已导入的文档。</summary>
    public sealed class KnowledgeDoc
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string SourcePath { get; set; } = "";
        public DateTime ImportedAt { get; set; } = DateTime.Now;
        public List<KnowledgeChunk> Chunks { get; set; } = new();
    }

    /// <summary>文档切块;VectorB64 为 float32 字节的 Base64(未嵌入时为 null)。</summary>
    public sealed class KnowledgeChunk
    {
        public string Text { get; set; } = "";
        public string VectorB64 { get; set; }
    }

    /// <summary>文档列表条目(管理界面用)。</summary>
    public sealed class KnowledgeDocumentInfo
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public int ChunkCount { get; set; }
        public DateTime ImportedAt { get; set; }
        public bool HasVectors { get; set; }
    }

    /// <summary>检索命中:文档名 + 片段 + 相关度。</summary>
    public sealed class KnowledgeSearchHit
    {
        public string DocName { get; set; }
        public string Text { get; set; }
        public float Score { get; set; }
    }

    public sealed class KnowledgeImportResult
    {
        public bool Success { get; set; }
        public string Message { get; set; } = "";
        public int ChunkCount { get; set; }
        public bool VectorsEmbedded { get; set; }
    }

    /// <summary>
    /// 知识库(RAG):文档导入、切块、向量化与检索,JSON 文件持久化。
    /// 常驻内存缓存——检索绝不读文件;仅在导入/删除时全量原子写(tmp+File.Move)。
    /// 检索策略:Embedding 已配置时用向量余弦;未配置或文档未向量化时用中文 bigram 关键词打分兜底。
    /// </summary>
    public sealed class KnowledgeBaseService
    {
        public const int MaxDocuments = 50;
        public const int MaxChunksPerDocument = 200;
        private const int HitTextMaxChars = 400;

        private readonly EmbeddingService _embedding;
        private readonly ILogService _logger = LogManager.GetLogger<KnowledgeBaseService>();
        private readonly object _gate = new();
        private KnowledgeStoreFile _store;
        private bool _loaded;

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            WriteIndented = false,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        public static string StorePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "MaxChemical", "XiaoTong", "knowledge.json");

        public bool EmbeddingConfigured => _embedding.IsConfigured;

        public KnowledgeBaseService(EmbeddingService embedding)
        {
            _embedding = embedding;
        }

        private KnowledgeStoreFile Load()
        {
            lock (_gate)
            {
                if (_loaded) return _store;

                _store = new KnowledgeStoreFile();
                try
                {
                    string path = StorePath;
                    if (!File.Exists(path))
                    {
                        string tmp = path + ".tmp";
                        if (File.Exists(tmp)) path = tmp;
                        else { _loaded = true; return _store; }
                    }

                    var file = JsonSerializer.Deserialize<KnowledgeStoreFile>(File.ReadAllText(path), JsonOpts);
                    _store = file ?? new KnowledgeStoreFile();
                    foreach (var d in _store.Documents)
                    {
                        d.Chunks ??= new List<KnowledgeChunk>();
                        if (string.IsNullOrEmpty(d.Id)) d.Id = Guid.NewGuid().ToString("N");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"读取知识库存档失败(将从空白开始): {ex.Message}");
                    _store = new KnowledgeStoreFile();
                }
                _loaded = true;
                return _store;
            }
        }

        /// <summary>导入一个文档:解析→切块→(可选)向量化→入库→落盘。</summary>
        public async Task<KnowledgeImportResult> ImportFileAsync(string path)
        {
            string ext = Path.GetExtension(path)?.ToLowerInvariant() ?? "";
            if (ext is not (".txt" or ".pdf" or ".docx"))
                return new KnowledgeImportResult
                {
                    Success = false,
                    Message = $"暂不支持 {ext} 格式(仅支持 txt / pdf / docx)"
                };

            string text;
            try { text = DocumentTextExtractor.Extract(path); }
            catch (Exception ex)
            {
                return new KnowledgeImportResult { Success = false, Message = "解析失败:" + ex.Message };
            }

            if (string.IsNullOrWhiteSpace(text))
                return new KnowledgeImportResult
                {
                    Success = false,
                    Message = "文档没有提取到文字(可能是扫描件,暂不支持 OCR),请提供文字版文档"
                };

            var chunks = TextChunker.Chunk(text);
            if (chunks.Count == 0)
                return new KnowledgeImportResult { Success = false, Message = "文档切块后为空" };

            if (chunks.Count > MaxChunksPerDocument)
                return new KnowledgeImportResult
                {
                    Success = false,
                    Message = $"文档切出 {chunks.Count} 块,超过单文档上限 {MaxChunksPerDocument} 块,请精简后重试"
                };

            // 向量化(未配 Key 或失败都降级,知识库照常可用,只是走关键词检索)
            float[][]? vectors = null;
            if (_embedding.IsConfigured)
            {
                try { vectors = await _embedding.EmbedAsync(chunks).ConfigureAwait(false); }
                catch (Exception ex)
                {
                    _logger.LogWarning($"向量化失败,该文档降级为关键词检索: {ex.Message}");
                }
            }

            var doc = new KnowledgeDoc
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = Path.GetFileName(path),
                SourcePath = path,
                ImportedAt = DateTime.Now
            };
            for (int i = 0; i < chunks.Count; i++)
            {
                doc.Chunks.Add(new KnowledgeChunk
                {
                    Text = chunks[i],
                    VectorB64 = vectors != null && i < vectors.Length && vectors[i] != null
                        ? VectorCodec.Encode(vectors[i])
                        : null
                });
            }

            lock (_gate)
            {
                var store = Load();
                if (store.Documents.Count >= MaxDocuments)
                    return new KnowledgeImportResult
                    {
                        Success = false,
                        Message = $"知识库已达上限 {MaxDocuments} 个文档,请先删除部分文档"
                    };
                if (store.Documents.Any(d => string.Equals(d.Name, doc.Name, StringComparison.OrdinalIgnoreCase)))
                    return new KnowledgeImportResult
                    {
                        Success = false,
                        Message = $"文档「{doc.Name}」已在知识库中,如需更新请先删除旧文档再导入"
                    };

                store.Documents.Add(doc);
                SaveLocked(store);
            }

            return new KnowledgeImportResult
            {
                Success = true,
                Message = $"已导入「{doc.Name}」,共 {chunks.Count} 块" + (vectors != null ? "(已向量化)" : "(关键词检索模式)"),
                ChunkCount = chunks.Count,
                VectorsEmbedded = vectors != null
            };
        }

        public bool DeleteDocument(string id)
        {
            lock (_gate)
            {
                var store = Load();
                var doc = store.Documents.FirstOrDefault(d => d.Id == id);
                if (doc == null) return false;
                store.Documents.Remove(doc);
                SaveLocked(store);
                return true;
            }
        }

        public List<KnowledgeDocumentInfo> ListDocuments()
        {
            lock (_gate)
            {
                return Load().Documents
                    .Select(d => new KnowledgeDocumentInfo
                    {
                        Id = d.Id,
                        Name = d.Name,
                        ChunkCount = d.Chunks.Count,
                        ImportedAt = d.ImportedAt,
                        HasVectors = d.Chunks.Any(c => c.VectorB64 != null)
                    })
                    .ToList();
            }
        }

        /// <summary>检索 TopK 片段。向量检索与关键词检索的得分统一到 0..1 后合并排序。</summary>
        public async Task<List<KnowledgeSearchHit>> SearchAsync(string query, int topK = 5)
        {
            var store = Load();
            var scored = new List<KnowledgeSearchHit>();

            // 查询向量:仅当 Embedding 可用时计算;失败不影响关键词兜底
            float[]? queryVector = null;
            if (_embedding.IsConfigured)
            {
                try
                {
                    var vecs = await _embedding.EmbedAsync(new[] { query }).ConfigureAwait(false);
                    if (vecs != null && vecs.Length > 0) queryVector = vecs[0];
                }
                catch { /* 降级关键词检索 */ }
            }

            lock (_gate)
            {
                foreach (var doc in store.Documents)
                {
                    foreach (var chunk in doc.Chunks)
                    {
                        float score;
                        if (queryVector != null && chunk.VectorB64 != null)
                            score = Cosine(queryVector, VectorCodec.Decode(chunk.VectorB64));
                        else
                            score = TextMatchHelper.OverlapScore(query, chunk.Text);
                        if (score <= 0f) continue;

                        scored.Add(new KnowledgeSearchHit
                        {
                            DocName = doc.Name,
                            Text = chunk.Text.Length <= HitTextMaxChars
                                ? chunk.Text
                                : chunk.Text.Substring(0, HitTextMaxChars) + "…",
                            Score = score
                        });
                    }
                }
            }

            return scored.OrderByDescending(h => h.Score).Take(Math.Clamp(topK, 1, 8)).ToList();
        }

        /// <summary>向量余弦相似度,结果截到 0..1。</summary>
        private static float Cosine(float[] a, float[] b)
        {
            if (a == null || b == null || a.Length == 0 || a.Length != b.Length) return 0f;
            double dot = 0, na = 0, nb = 0;
            for (int i = 0; i < a.Length; i++)
            {
                dot += (double)a[i] * b[i];
                na += (double)a[i] * a[i];
                nb += (double)b[i] * b[i];
            }
            if (na <= 0 || nb <= 0) return 0f;
            return (float)Math.Max(0.0, Math.Min(1.0, dot / (Math.Sqrt(na) * Math.Sqrt(nb))));
        }

        private void SaveLocked(KnowledgeStoreFile store)
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
                _logger.LogWarning($"保存知识库存档失败: {ex.Message}");
            }
        }
    }

    /// <summary>float[] 与 Base64 字符串互转(向量以 float32 字节压缩后存 JSON,控文件体积)。</summary>
    public static class VectorCodec
    {
        public static string Encode(float[] vector)
        {
            byte[] bytes = new byte[vector.Length * sizeof(float)];
            Buffer.BlockCopy(vector, 0, bytes, 0, bytes.Length);
            return Convert.ToBase64String(bytes);
        }

        public static float[] Decode(string base64)
        {
            byte[] bytes = Convert.FromBase64String(base64);
            var vector = new float[bytes.Length / sizeof(float)];
            Buffer.BlockCopy(bytes, 0, vector, 0, bytes.Length);
            return vector;
        }
    }
}

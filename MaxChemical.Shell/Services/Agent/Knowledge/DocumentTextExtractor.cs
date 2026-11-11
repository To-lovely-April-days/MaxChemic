using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using MaxChemical.Logging;

namespace MaxChemical.Shell.Services.Agent.Knowledge
{
    /// <summary>
    /// 知识库文档解析:把 txt/pdf/docx 提取为纯文本。
    /// txt 按 UTF8 严格解码、失败回退 GB2312(国标工业文档常见编码);
    /// pdf 用 PdfPig 逐页取文本;docx 用 Open XML SDK 读段落。
    /// 扫描件(无文字层)会得到空文本,由调用方如实提示,不做 OCR。
    /// </summary>
    public static class DocumentTextExtractor
    {
        private static readonly ILogService Logger = LogManager.GetLogger("DocumentTextExtractor");

        private static bool _codePagesRegistered;

        public static string Extract(string path)
        {
            string ext = Path.GetExtension(path)?.ToLowerInvariant() ?? "";
            switch (ext)
            {
                case ".txt": return ExtractTxt(path);
                case ".pdf": return ExtractPdf(path);
                case ".docx": return ExtractDocx(path);
                default:
                    throw new NotSupportedException($"暂不支持 {ext} 格式(仅支持 txt/pdf/docx)");
            }
        }

        private static string ExtractTxt(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);

            // 优先按 UTF8 严格解码;失败(GB2312 等)回退系统代码页
            try
            {
                var utf8 = new UTF8Encoding(false, true);
                return utf8.GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
                try
                {
                    if (!_codePagesRegistered)
                    {
                        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                        _codePagesRegistered = true;
                    }
                    return Encoding.GetEncoding("GB2312").GetString(bytes);
                }
                catch (Exception ex)
                {
                    Logger.LogWarning($"GB2312 解码失败({Path.GetFileName(path)}),按默认编码读取: {ex.Message}");
                    return Encoding.Default.GetString(bytes);
                }
            }
        }

        private static string ExtractPdf(string path)
        {
            var sb = new StringBuilder();
            using (var pdf = UglyToad.PdfPig.PdfDocument.Open(path))
            {
                foreach (var page in pdf.GetPages())
                {
                    string text = page.Text;
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        sb.AppendLine(text);
                        sb.AppendLine();
                    }
                }
            }
            return sb.ToString();
        }

        private static string ExtractDocx(string path)
        {
            var sb = new StringBuilder();
            using (var doc = DocumentFormat.OpenXml.Packaging.WordprocessingDocument.Open(path, false))
            {
                var body = doc.MainDocumentPart?.Document?.Body;
                if (body == null) return "";
                foreach (var para in body.Descendants<DocumentFormat.OpenXml.Wordprocessing.Paragraph>())
                {
                    string line = para.InnerText?.Trim() ?? "";
                    if (line.Length > 0) sb.AppendLine(line);
                }
            }
            return sb.ToString();
        }
    }

    /// <summary>
    /// 文本切块:按段落贪心合并到 300~800 字;超长段落按句末标点再切;
    /// 尾部不足 300 字的块也保留(资料末尾的关键结论不能丢)。
    /// </summary>
    public static class TextChunker
    {
        private static readonly Regex SentenceSplitRegex =
            new(@"(?<=[。！？!?；;])", RegexOptions.Compiled);

        public static List<string> Chunk(string text, int min = 300, int max = 800)
        {
            if (string.IsNullOrWhiteSpace(text)) return new List<string>();

            text = text.Replace("\r\n", "\n").Replace('\r', '\n');
            var paragraphs = text.Split('\n')
                                 .Select(p => p.Trim())
                                 .Where(p => p.Length > 0)
                                 .ToList();

            var chunks = new List<string>();
            var sb = new StringBuilder();

            void Flush()
            {
                if (sb.Length > 0)
                {
                    chunks.Add(sb.ToString());
                    sb.Clear();
                }
            }

            foreach (var p in paragraphs)
            {
                if (p.Length > max)
                {
                    Flush();
                    foreach (var piece in SplitLong(p, max))
                        if (piece.Length > 0)
                            chunks.Add(piece);
                    continue;
                }

                if (sb.Length == 0)
                    sb.Append(p);
                else if (sb.Length + 1 + p.Length <= max)
                    sb.Append('\n').Append(p);
                else
                {
                    Flush();
                    sb.Append(p);
                }
            }
            Flush();

            return chunks;
        }

        /// <summary>超长段落按句末标点硬切,句子本身仍超长(如表格行)时按 max 直切。</summary>
        private static IEnumerable<string> SplitLong(string text, int max)
        {
            var sb = new StringBuilder();
            foreach (var part in SentenceSplitRegex.Split(text))
            {
                if (part.Length > max)
                {
                    if (sb.Length > 0) { yield return sb.ToString(); sb.Clear(); }
                    for (int i = 0; i < part.Length; i += max)
                        yield return part.Substring(i, Math.Min(max, part.Length - i));
                    continue;
                }
                if (sb.Length + part.Length <= max)
                    sb.Append(part);
                else
                {
                    yield return sb.ToString();
                    sb.Clear();
                    sb.Append(part);
                }
            }
            if (sb.Length > 0) yield return sb.ToString();
        }
    }
}

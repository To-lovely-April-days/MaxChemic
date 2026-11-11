using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace MaxChemical.Shell.Services.Agent
{
    /// <summary>
    /// 中文文本匹配工具:知识库关键词兜底检索与长期记忆召回共用。
    /// 中文没有天然分词,用字符 bigram 重叠度量相关性;数字/字母词(报警代码、单位)整词匹配。
    /// </summary>
    public static class TextMatchHelper
    {
        private static readonly Regex PunctuationRegex = new(@"[\s\p{P}]", RegexOptions.Compiled);

        /// <summary>去掉空白与标点(中文标点一并去除)。</summary>
        public static string StripPunctuation(string text)
            => string.IsNullOrEmpty(text) ? "" : PunctuationRegex.Replace(text, "");

        /// <summary>提取文本的全部字符 bigram 集合(相邻两字)。</summary>
        public static HashSet<string> BigramSet(string text)
        {
            var set = new HashSet<string>();
            string t = StripPunctuation(text);
            for (int i = 0; i + 1 < t.Length; i++)
                set.Add(t.Substring(i, 2));
            return set;
        }

        /// <summary>
        /// 关键词重叠打分(0..1):数字/字母词整词命中权重高,中文按 bigram 重叠比例。
        /// 供未配置 Embedding 时的知识库检索与记忆召回使用。
        /// </summary>
        public static float OverlapScore(string query, string text)
        {
            if (string.IsNullOrWhiteSpace(query) || string.IsNullOrWhiteSpace(text)) return 0f;
            string q = StripPunctuation(query);
            if (q.Length == 0) return 0f;

            float score = 0f;
            float total = 0f;

            // 数字/字母词(报警代码 E5、单位 45Hz、设备型号等):整词命中,权重高
            foreach (Match m in Regex.Matches(q, @"[\p{L}\p{N}][\p{L}\p{N}\.\-/_]*"))
            {
                total += 2f;
                if (m.Value.Length >= 2 && text.IndexOf(m.Value, StringComparison.OrdinalIgnoreCase) >= 0)
                    score += 2f;
            }

            // 中文 bigram 重叠比例
            var qb = BigramSet(q);
            if (qb.Count > 0)
            {
                total += 1f;
                var tb = BigramSet(text);
                float hit = 0f;
                foreach (var b in qb) if (tb.Contains(b)) hit += 1f;
                score += hit / qb.Count;
            }

            return total <= 0f ? 0f : Math.Min(1f, score / total);
        }
    }
}

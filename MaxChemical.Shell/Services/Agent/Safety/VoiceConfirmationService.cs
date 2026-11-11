using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace MaxChemical.Shell.Services.Agent.Safety
{
    /// <summary>
    /// 语音确认:写操作确认卡弹出后,用户可直接说「确认/好的」批准、「取消/不要」拒绝,
    /// 不必点界面按钮。持有的批准/拒绝回调由 AgentChatViewModel 提供——回调内统一走
    /// UI 线程的同一解决路径(置位卡片 + TrySetResult + ClearPending),保证语音确认与
    /// 按钮点击、超时、停止取消完全等价,绝不出现卡片状态与决策不一致。
    /// 判词限制:去标点后 ≤6 字才判(「请确认:将设备…」这类播报回声天然被长度门挡住)。
    /// </summary>
    public sealed class VoiceConfirmationService
    {
        private readonly object _gate = new();

        private Action _approve;
        private Action _reject;

        private static readonly string[] ApproveWords = { "确认", "确认执行", "认执行", "好的", "可以", "执行", "确定", "同意", "是的", "对" };
        private static readonly string[] RejectWords = { "取消", "取消执行", "不要", "算了", "不行", "拒绝", "不用" };

        /// <summary>当前是否有待语音确认的写操作。</summary>
        public bool HasPending
        {
            get { lock (_gate) return _approve != null; }
        }

        /// <summary>登记待确认项(新卡顶掉旧卡——极端情况下同一时刻只可能有一张确认卡)。</summary>
        public void SetPending(Action approve, Action reject)
        {
            lock (_gate)
            {
                _approve = approve ?? throw new ArgumentNullException(nameof(approve));
                _reject = reject ?? throw new ArgumentNullException(nameof(reject));
            }
        }

        /// <summary>卡片经任何路径解决(批准/拒绝/超时/停止)后清除登记。</summary>
        public void ClearPending()
        {
            lock (_gate)
            {
                _approve = null;
                _reject = null;
            }
        }

        /// <summary>
        /// 尝试把识别文本解析为确认指令。
        /// 返回 true=批准、false=拒绝、null=不是确认词或当前无待确认项。
        /// </summary>
        public bool? TryResolve(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            string t = TextMatchHelper.StripPunctuation(text);
            if (t.Length == 0 || t.Length > 6) return null;

            Action action;
            bool verdict;
            lock (_gate)
            {
                if (_approve == null) return null;

                // 精确匹配防误触发:「不可以」含「可以」、「对不起」含「对」,包含匹配会误批准
                if (ApproveWords.Any(w => t == w))
                {
                    action = _approve;
                    verdict = true;
                }
                else if (RejectWords.Any(w => t == w))
                {
                    action = _reject;
                    verdict = false;
                }
                else
                    return null;

                // 先清空再执行:回调内若再查 HasPending 不应看到旧卡
                _approve = null;
                _reject = null;
            }

            action?.Invoke();
            return verdict;
        }

        /// <summary>
        /// 中间结果片段解析:识别流是累积的,确认词出现在片段尾部——取尾部 2~6 字窗口
        /// 精确匹配(「确认执行。确认执行」的尾 4 字就是「确认执行」)。
        /// 连续说话不留停顿、整句迟迟不定稿时,靠它也能立刻解决确认卡。
        /// 命中即批准/拒绝,与整句解析走同一条解决路径。
        /// </summary>
        public bool? TryResolvePartial(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            string t = TextMatchHelper.StripPunctuation(text);
            if (t.Length == 0) return null;

            Action action;
            bool verdict;
            lock (_gate)
            {
                if (_approve == null) return null;

                action = null;
                verdict = false;
                for (int len = Math.Min(6, t.Length); len >= 2; len--)
                {
                    string tail = t.Substring(t.Length - len);
                    if (ApproveWords.Any(w => tail == w))
                    {
                        action = _approve;
                        verdict = true;
                        break;
                    }
                    if (RejectWords.Any(w => tail == w))
                    {
                        action = _reject;
                        verdict = false;
                        break;
                    }
                }
                if (action == null) return null;

                // 先清空再执行:回调内若再查 HasPending 不应看到旧卡
                _approve = null;
                _reject = null;
            }

            action?.Invoke();
            return verdict;
        }
    }
}

using System;
using System.Diagnostics;

namespace MaxChemical.Shell.Services
{
    /// <summary>
    /// WPF 绑定诊断的「已知噪音」过滤监听器:只吞掉系统主题样式的一类无害报错,
    /// 其余绑定错误原样转发到调试输出(真实绑定问题不会被掩盖)。
    ///
    /// 噪音来源:ListBoxItem 的系统默认样式把 Horizontal/VerticalContentAlignment 绑定为
    /// RelativeSource FindAncestor(ItemsControl)。虚拟化容器被回收、或列表刷新时容器移出
    /// 可视树的瞬间,自定义 ItemContainerStyle 被摘除、样式临时回退到系统默认,而此刻元素
    /// 已不在树上,FindAncestor 必然找不到——于是每个被回收的容器刷两条失败。
    /// 这是微软默认样式的历史问题,与业务代码无关,不影响功能、渲染和性能。
    /// </summary>
    public sealed class BindingNoiseFilterListener : TraceListener
    {
        private static bool IsKnownNoise(string message) =>
            message != null &&
            message.Contains("RelativeSource FindAncestor") &&
            message.Contains("AncestorType='System.Windows.Controls.ItemsControl'") &&
            (message.Contains("HorizontalContentAlignment") || message.Contains("VerticalContentAlignment"));

        public override void TraceEvent(TraceEventCache eventCache, string source,
            TraceEventType eventType, int id, string format, params object[] args)
        {
            string msg = format;
            if (args is { Length: > 0 })
            {
                try { msg = string.Format(format, args); }
                catch (FormatException) { }
            }
            if (IsKnownNoise(msg)) return;

            // 保持与默认监听器一致的输出格式,VS 输出窗口/绑定失败面板可正常识别其余错误
            if (Debugger.IsAttached)
                Debugger.Log(0, source, $"{source} {eventType}: {id} : {msg}{Environment.NewLine}");
        }

        public override void TraceEvent(TraceEventCache eventCache, string source,
            TraceEventType eventType, int id, string message)
            => TraceEvent(eventCache, source, eventType, id, message, null);

        // 绑定诊断走 TraceEvent 通道,零散 Write 片段无上下文,直接忽略
        public override void Write(string message) { }
        public override void WriteLine(string message) { }
    }
}

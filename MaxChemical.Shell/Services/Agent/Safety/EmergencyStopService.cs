using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using DevicePlugins.Devices;
using MaxChemical.Infrastructure.DOE;
using MaxChemical.Logging;
using MaxChemical.Shell.Services.Agent.Tools;
using Prism.Ioc;

namespace MaxChemical.Shell.Services.Agent.Safety
{
    /// <summary>
    /// 急停本地化:关键词正则直接触发,不走大模型——断网、模型思考中、任何时刻都立即生效。
    /// 触发条件(纯判定,线程安全):去标点后 ≤12 字、命中急停词、且不含疑问词(「什么是急停」不触发)。
    /// 执行顺序:取消当前 Agent 轮次 → 停流程(引擎内部并发停全部设备)→ 画布可控设备兜底直停
    /// (覆盖「流程空闲但设备在手动运行」)→ 界面播报 + 语音播报 + 审计日志。
    /// 装配回调(PostToChat/InjectNote/CancelCurrentTurn)由 AgentBootstrapper 注入,避免构造期服务环。
    /// </summary>
    public sealed class EmergencyStopService
    {
        private static readonly Regex EmergencyPattern =
            new(@"紧急停止|急停|立即停机|马上停止|快停|紧急停机|立刻停机", RegexOptions.Compiled);

        private static readonly string[] QuestionWords =
            { "什么", "怎么", "如何", "为什么", "为何", "吗", "？", "?", "几" };

        private readonly IContainerProvider _container;
        private readonly AgentToolContext _ctx;
        private readonly ILogService _logger = LogManager.GetLogger<EmergencyStopService>();
        private int _running;

        /// <summary>把执行结果播报进聊天流(由 Bootstrapper 接到 chatVm.PostProactiveMessage)。</summary>
        public Action<string> PostToChat { get; set; }

        /// <summary>把执行结果写进 Agent 会话历史(assistant 角色,供后续追问引用)。</summary>
        public Action<string> InjectNote { get; set; }

        /// <summary>取消当前进行中的 Agent 轮次(停完设备后模型不得继续派工)。</summary>
        public Action CancelCurrentTurn { get; set; }

        /// <summary>审计日志(由 Bootstrapper 注入)。</summary>
        public Audit.AiAuditLog AuditLog { get; set; }

        public EmergencyStopService(IContainerProvider container)
        {
            _container = container;
            _ctx = new AgentToolContext(container);
        }

        /// <summary>纯判定:该文本是否为急停指令。任意线程可调用,无副作用。</summary>
        public bool Matches(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;

            string t = TextMatchHelper.StripPunctuation(text);
            if (t.Length == 0 || t.Length > 12) return false;

            foreach (var w in QuestionWords)
                if (t.Contains(w, StringComparison.OrdinalIgnoreCase))
                    return false;

            return EmergencyPattern.IsMatch(t);
        }

        /// <summary>执行急停。Interlocked 重入闸防双通道(语音+文本同时喊)重复停机。返回播报摘要。</summary>
        public async Task<string> ExecuteAsync(string triggerText)
        {
            if (Interlocked.Exchange(ref _running, 1) == 1)
                return "紧急停止正在执行中";

            try
            {
                var parts = new List<string>();

                // ① 取消当前 Agent 轮次:否则停完设备后模型还会继续派工重启流程
                try { CancelCurrentTurn?.Invoke(); }
                catch (Exception ex) { _logger.LogWarning($"取消当前轮次失败: {ex.Message}"); }

                // ② 停流程(引擎内部已并发停画布全部可控设备,10 秒超时)
                bool flowStopped = false;
                try
                {
                    var exec = _container.Resolve<IFlowExecutionService>();
                    if (exec.State == FlowRunState.Running || exec.State == FlowRunState.Paused)
                    {
                        await exec.StopExecutionAsync().ConfigureAwait(false);
                        flowStopped = true;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"停止流程失败(继续停设备): {ex.Message}");
                }

                // ③ 画布可控设备兜底直停:覆盖「流程空闲但设备在手动运行」的场景
                int stoppedDevices = 0;
                string deviceError = null;
                try
                {
                    var tasks = new List<Task>();
                    foreach (var d in _ctx.GetCanvasDevices())
                    {
                        if (d.Device is IControllableDevice controllable)
                            tasks.Add(SafeStopDeviceAsync(controllable));
                    }
                    if (tasks.Count > 0)
                    {
                        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                        stoppedDevices = tasks.Count;
                    }
                }
                catch (TimeoutException)
                {
                    stoppedDevices = 0;
                    deviceError = "部分设备停止超时";
                }
                catch (Exception ex)
                {
                    deviceError = "设备停止异常:" + ex.Message;
                }

                // ④ 汇总播报
                var sb = new StringBuilder();
                if (flowStopped) sb.Append("已停止当前流程");
                if (stoppedDevices > 0) sb.Append(sb.Length > 0 ? ";" : "").Append($"已向 {stoppedDevices} 台设备下发停止指令");
                if (sb.Length == 0) sb.Append("已执行(当前无运行中的流程,画布上也没有可控设备)");
                if (!string.IsNullOrEmpty(deviceError)) sb.Append(";").Append(deviceError);
                string summary = sb.ToString();

                string note = "紧急停止:" + summary;
                try { PostToChat?.Invoke(note); } catch { }
                try { InjectNote?.Invoke(note); } catch { }

                try
                {
                    var voice = _container.Resolve<IVoiceAssistantService>();
                    voice?.SpeakAsync("已执行紧急停止。" + summary);
                }
                catch { /* 语音服务不可用不影响急停本身 */ }

                AuditLog?.LogEvent("紧急停止", $"触发词:{triggerText} | {note}");
                _logger.LogWarning($"紧急停止已执行(触发词:{triggerText}): {note}");

                return summary;
            }
            finally
            {
                Interlocked.Exchange(ref _running, 0);
            }
        }

        private async Task SafeStopDeviceAsync(IControllableDevice device)
        {
            try
            {
                await device.StopAllActiveStatesAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"设备停止失败: {ex.Message}");
            }
        }
    }
}

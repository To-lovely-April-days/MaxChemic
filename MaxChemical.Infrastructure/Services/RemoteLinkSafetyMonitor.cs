using System;
using DevicePlugins.Devices.Remote;
using MaxChemical.Infrastructure.DOE;
using MaxChemical.Infrastructure.Events;
using MaxChemical.Logging;
using Prism.Events;

namespace MaxChemical.Infrastructure.Services
{
    /// <summary>
    /// 云链路安全监护。
    /// 订阅 <see cref="RemoteGatewayClient"/> 的链路健康事件：
    ///  - 链路断开且流程运行中 → 自动 HOLD(暂停)流程 + 告警；
    ///  - 链路恢复 → 通知操作员，由人工确认设备状态后手动『继续』(不自动续跑)。
    /// 对应自动化"通信丢失 → 保持(Hold) + 报警 + 人工确认恢复"的监控级策略。
    /// 真正的设备安全仍由边缘(设备/PLC)的通信丢失看门狗负责。
    /// </summary>
    public sealed class RemoteLinkSafetyMonitor : IDisposable
    {
        private readonly IFlowExecutionService _flow;
        private readonly IEventAggregator _events;
        private readonly ILogService _logger;
        private volatile bool _heldByLinkLoss;

        public RemoteLinkSafetyMonitor(IFlowExecutionService flow, IEventAggregator events)
        {
            _flow = flow;
            _events = events;
            _logger = LogManager.GetLogger<RemoteLinkSafetyMonitor>();
            RemoteGatewayClient.Instance.LinkHealthyChanged += OnLinkHealthyChanged;
            _logger.LogInformation("云链路安全监护已启动");
        }

        private void OnLinkHealthyChanged(bool healthy)
        {
            try
            {
                if (!healthy) OnLinkDown();
                else OnLinkUp();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "云链路监护处理异常");
            }
        }

        private void OnLinkDown()
        {
            _logger.LogWarning("检测到云链路断开");
            if (_flow.State == FlowRunState.Running)
            {
                _flow.PauseExecution();          // 复用现成的逐步暂停(HOLD)
                _heldByLinkLoss = true;
                Publish("⚠ 云链路断开，流程已自动暂停(HOLD)，等待链路恢复…");
                _logger.LogWarning("因云链路断开，流程已 HOLD");
            }
            else
            {
                Publish("⚠ 云链路断开");
            }
        }

        private void OnLinkUp()
        {
            _logger.LogInformation("云链路已恢复");
            if (_heldByLinkLoss)
            {
                _heldByLinkLoss = false;
                // 安全起见不自动续跑：需人工确认设备真实状态后手动继续
                Publish("✓ 云链路已恢复。请确认各设备状态正常后，手动点击『继续』恢复流程。");
            }
            else
            {
                Publish("✓ 云链路已恢复");
            }
        }

        private void Publish(string msg)
        {
            // 事件来自 SignalR 后台线程；状态消息订阅者会更新 WPF 绑定，需封送到 UI 线程
            try
            {
                var app = System.Windows.Application.Current;
                if (app?.Dispatcher != null && !app.Dispatcher.CheckAccess())
                {
                    app.Dispatcher.BeginInvoke(new Action(() =>
                        _events.GetEvent<StatusMessageChangedEvent>().Publish(msg)));
                }
                else
                {
                    _events.GetEvent<StatusMessageChangedEvent>().Publish(msg);
                }
            }
            catch (Exception ex) { _logger.LogDebug("发布状态消息失败: {E}", ex.Message); }
        }

        public void Dispose()
        {
            RemoteGatewayClient.Instance.LinkHealthyChanged -= OnLinkHealthyChanged;
        }
    }
}

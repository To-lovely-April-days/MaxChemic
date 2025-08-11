using DevicePlugins.Devices;
using MaxChemical.Modules.Designer.Event;
using MaxChemical.Modules.Designer.Models;
using MaxChemical.Modules.Designer.ViewModels;
using Prism.Events;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MaxChemical.Modules.Designer.Service.Execution
{
    public class ManualCommandExecutor
    {
        private readonly IEventAggregator _eventAggregator;
        private readonly ConcurrentDictionary<string, CancellationTokenSource> _ctsMap = new();

        public ManualCommandExecutor(IEventAggregator eventAggregator)
        {
            _eventAggregator = eventAggregator;
        }

        public bool IsRunning(string nodeId) => _ctsMap.ContainsKey(nodeId);

        public async Task StartAsync(CommandNode node)
        {
            if (node?.DeviceCommand == null)
                throw new InvalidOperationException("当前节点不是设备命令。");

            if (string.IsNullOrEmpty(node.BoundDeviceId))
                throw new InvalidOperationException("设备命令未绑定设备。");

            if (_ctsMap.ContainsKey(node.NodeId))
                throw new InvalidOperationException("该命令正在执行中。");

            // 请求画布设备
            List<CanvasDeviceViewModel> canvasDevices = null;
            _eventAggregator?.GetEvent<RequestCanvasDevicesEvent>()?.Publish(list => canvasDevices = list);

            var target = canvasDevices?.FirstOrDefault(d => d.DeviceId == node.BoundDeviceId)?.Device;
            if (target == null)
                throw new InvalidOperationException("未在画布上找到绑定的设备。");

            var cmd = target.Commands?.FirstOrDefault(c => c.Name == node.DeviceCommand.Name);
            if (cmd == null)
                throw new InvalidOperationException($"设备不支持命令：{node.DeviceCommand.Name}");

            var cts = new CancellationTokenSource();
            if (!_ctsMap.TryAdd(node.NodeId, cts))
                cts.Dispose();

            node.IsManualExecuting = true;
            node.ExecutionStatus = CommandNode.NodeExecutionStatus.Executing;

            // 动画开始
            _eventAggregator?.GetEvent<DeviceExecutionStartedEvent>()?.Publish(new DeviceExecutionEventArgs
            {
                DeviceId = node.BoundDeviceId,
                CommandName = cmd.Name
            });

            try
            {
                await Task.Run(() =>
                {
                    if (cts.IsCancellationRequested) return;

                    var input = cmd.InputParameters ?? node.DeviceCommand.InputParameters ?? new DeviceParameters();
                    // 实际命令为同步委托；如需可在命令内部检查取消
                    var _ = cmd.Action?.Invoke(input);
                }, cts.Token);

                if (cts.IsCancellationRequested)
                {
                    node.ExecutionStatus = CommandNode.NodeExecutionStatus.Failed;
                }
                else
                {
                    node.ExecutionStatus = CommandNode.NodeExecutionStatus.Completed;
                }

                // 动画结束
                _eventAggregator?.GetEvent<DeviceExecutionEndedEvent>()?.Publish(new DeviceExecutionEventArgs
                {
                    DeviceId = node.BoundDeviceId,
                    CommandName = cmd.Name,
                    IsSuccessful = !cts.IsCancellationRequested
                });
            }
            catch (Exception ex)
            {
                node.ExecutionStatus = CommandNode.NodeExecutionStatus.Failed;
                _eventAggregator?.GetEvent<DeviceExecutionEndedEvent>()?.Publish(new DeviceExecutionEventArgs
                {
                    DeviceId = node.BoundDeviceId,
                    CommandName = cmd.Name,
                    IsSuccessful = false,
                    ErrorMessage = ex.Message
                });
                throw;
            }
            finally
            {
                node.IsManualExecuting = false;
                if (_ctsMap.TryRemove(node.NodeId, out var token)) token.Dispose();
            }
        }

        public void Stop(string nodeId)
        {
            if (_ctsMap.TryGetValue(nodeId, out var cts))
            {
                cts.Cancel();
            }
        }
    }
}

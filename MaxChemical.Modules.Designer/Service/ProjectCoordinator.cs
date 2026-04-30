using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using MaxChemical.Infrastructure.Events;
using MaxChemical.Modules.Designer.Event;
using MaxChemical.Modules.Designer.Models;
using Prism.Events;

namespace MaxChemical.Modules.Designer.Service
{
    /// <summary>
    /// 项目协调器 - 负责协调ProcessDesignerView和FlowDesignerView的数据
    /// </summary>
    public class ProjectCoordinator
    {
        private readonly IEventAggregator _eventAggregator;
        private readonly ICanvasDataService _canvasDataService;

        public ProjectCoordinator(
            IEventAggregator eventAggregator,
            ICanvasDataService canvasDataService)
        {
            _eventAggregator = eventAggregator;
            _canvasDataService = canvasDataService;

            SubscribeToEvents();
        }

        private void SubscribeToEvents()
        {
            _eventAggregator?.GetEvent<GetCurrentProjectDataRequestEvent>()?.Subscribe(OnGetCurrentProjectDataRequest);
            _eventAggregator?.GetEvent<ClearProjectRequestEvent>()?.Subscribe(OnClearProjectRequest);
            _eventAggregator?.GetEvent<LoadProjectRequestEvent>()?.Subscribe(OnLoadProjectRequest);
        }

        /// <summary>
        /// 响应获取当前项目数据请求
        /// </summary>
        private void OnGetCurrentProjectDataRequest(Action<ProjectDocument> callback)
        {
            try
            {
                Debug.WriteLine("开始收集完整项目数据");

                var projectDocument = new ProjectDocument
                {
                    Name = "当前项目"
                };

                // 收集画布设计数据
                CollectCanvasDesignData(projectDocument);

                // 收集流程设计数据
                CollectFlowDesignData(projectDocument);

                callback?.Invoke(projectDocument);
                Debug.WriteLine($"项目数据收集完成: 画布设备{projectDocument.CanvasDesign.Devices.Count}个, " +
                              $"管道{projectDocument.CanvasDesign.Pipes.Count}个, " + //  新增
                              $"流程节点{projectDocument.FlowDesign.CommandNodes.Count}个");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"收集项目数据失败: {ex.Message}");
                callback?.Invoke(null);
            }
        }

        /// <summary>
        /// 收集画布设计数据
        /// </summary>
        private void CollectCanvasDesignData(ProjectDocument projectDocument)
        {
            try
            {
                Debug.WriteLine("收集画布设计数据");

                // 获取当前画布数据
                var canvasDocument = _canvasDataService.GetCurrentCanvasData();
                if (canvasDocument != null)
                {
                    // 复制基本画布数据到项目中
                    projectDocument.CanvasDesign.Id = canvasDocument.Id;
                    projectDocument.CanvasDesign.Name = canvasDocument.Name;
                    projectDocument.CanvasDesign.CreatedTime = canvasDocument.CreatedTime;
                    projectDocument.CanvasDesign.LastModifiedTime = canvasDocument.LastModifiedTime;
                    projectDocument.CanvasDesign.RegionLayout = canvasDocument.RegionLayout;
                    projectDocument.CanvasDesign.Devices = canvasDocument.Devices ?? new List<CanvasDeviceData>();
                    projectDocument.CanvasDesign.Connections = canvasDocument.Connections ?? new List<ConnectionData>();

                    //  新增：复制管道数据
                    projectDocument.CanvasDesign.Pipes = canvasDocument.Pipes ?? new List<CanvasPipeData>();

                    projectDocument.CanvasDesign.CanvasWidth = canvasDocument.CanvasWidth;
                    projectDocument.CanvasDesign.CanvasHeight = canvasDocument.CanvasHeight;
                }

                // 获取设备显示名映射
                CollectDeviceDisplayNames(projectDocument.CanvasDesign);

                // 收集监控参数状态
                CollectMonitoringParameterStates(projectDocument.CanvasDesign);

                //  新增：收集管道数据
                CollectPipeData(projectDocument.CanvasDesign);

                // 获取画布状态信息
                CollectCanvasState(projectDocument.CanvasDesign);

                Debug.WriteLine($"画布数据收集完成: 设备{projectDocument.CanvasDesign.Devices.Count}个, " +
                              $"连接{projectDocument.CanvasDesign.Connections.Count}个, " +
                              $"管道{projectDocument.CanvasDesign.Pipes.Count}个"); //  新增
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"收集画布设计数据失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 收集设备显示名映射
        /// </summary>
        private void CollectDeviceDisplayNames(CanvasDesignData canvasDesign)
        {
            try
            {
                // 请求设备显示名映射
                _eventAggregator?.GetEvent<GetDeviceDisplayNamesRequestEvent>()?.Publish(displayNames =>
                {
                    if (displayNames != null)
                    {
                        canvasDesign.DeviceDisplayNames = new Dictionary<string, string>(displayNames);
                        Debug.WriteLine($"收集到 {displayNames.Count} 个设备显示名映射");
                    }
                });

                // 请求设备名称计数器
                _eventAggregator?.GetEvent<GetDeviceNameCountersRequestEvent>()?.Publish(counters =>
                {
                    if (counters != null)
                    {
                        canvasDesign.DeviceNameCounters = new Dictionary<string, int>(counters);
                        Debug.WriteLine($"收集到 {counters.Count} 个设备名称计数器");
                    }
                });

                Debug.WriteLine("设备显示名相关数据收集完成");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"收集设备显示名失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 收集监控参数状态
        /// </summary>
        private void CollectMonitoringParameterStates(CanvasDesignData canvasDesign)
        {
            try
            {
                Debug.WriteLine("开始收集监控参数状态");

                // 收集设备监控参数选择状态
                _eventAggregator?.GetEvent<GetDeviceMonitoringSelectionsRequestEvent>()?.Publish(selections =>
                {
                    if (selections != null && selections.Count > 0)
                    {
                        canvasDesign.DeviceMonitoringSelections = new Dictionary<string, HashSet<string>>();
                        foreach (var kvp in selections)
                        {
                            canvasDesign.DeviceMonitoringSelections[kvp.Key] = new HashSet<string>(kvp.Value);
                        }
                        Debug.WriteLine($"收集到 {selections.Count} 个设备的监控参数选择状态");
                    }
                });

                // 收集监控卡片状态
                _eventAggregator?.GetEvent<GetMonitoringCardStatesRequestEvent>()?.Publish(cardStates =>
                {
                    if (cardStates != null && cardStates.Count > 0)
                    {
                        canvasDesign.DeviceMonitoringCardStates = new Dictionary<string, bool>(cardStates);
                        Debug.WriteLine($"收集到 {cardStates.Count} 个设备的监控卡片状态");
                    }
                });

                //  新增：收集监控卡片位置
                _eventAggregator?.GetEvent<GetMonitoringCardPositionsRequestEvent>()?.Publish(positions =>
                {
                    if (positions != null && positions.Count > 0)
                    {
                        canvasDesign.MonitoringCardPositions = new Dictionary<string, Point>(positions);
                        Debug.WriteLine($"收集到 {positions.Count} 个监控卡片位置");
                    }
                });

                Debug.WriteLine("监控参数状态收集完成");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"收集监控参数状态失败: {ex.Message}");
            }
        }

        /// <summary>
        ///  新增：收集管道数据
        /// </summary>
        private void CollectPipeData(CanvasDesignData canvasDesign)
        {
            try
            {
                Debug.WriteLine("========== 开始收集管道数据 ==========");

                var waitHandle = new System.Threading.ManualResetEventSlim(false);
                List<CanvasPipeData> pipeDataList = null;

                // 发布收集管道数据请求
                _eventAggregator?.GetEvent<CollectPipeDataRequestEvent>()?.Publish((pipes) =>
                {
                    pipeDataList = pipes;
                    waitHandle.Set();
                });

                // 等待收集完成（超时2秒）
                if (waitHandle.Wait(2000))
                {
                    if (pipeDataList != null && pipeDataList.Count > 0)
                    {
                        canvasDesign.Pipes = new List<CanvasPipeData>(pipeDataList);
                        Debug.WriteLine($" 收集到 {pipeDataList.Count} 个管道");

                        // 详细日志
                        foreach (var pipe in pipeDataList)
                        {
                            Debug.WriteLine($"  - {pipe.PipeType} 管道: {pipe.PipeId} at ({pipe.X:F0}, {pipe.Y:F0})");
                        }
                    }
                    else
                    {
                        canvasDesign.Pipes = new List<CanvasPipeData>();
                        Debug.WriteLine("画布上没有管道");
                    }
                }
                else
                {
                    Debug.WriteLine(" 收集管道数据超时");
                    canvasDesign.Pipes = new List<CanvasPipeData>();
                }

                waitHandle.Dispose();
                Debug.WriteLine("========== 管道数据收集完成 ==========");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($" 收集管道数据失败: {ex.Message}");
                canvasDesign.Pipes = new List<CanvasPipeData>();
            }
        }

        /// <summary>
        /// 检查是否为完整键名格式
        /// </summary>
        private bool IsCompleteKeyFormat(HashSet<string> selections)
        {
            if (selections.Count == 0) return true;

            // 如果大部分选择包含 # 分隔符，则认为是完整键名格式
            int completeKeyCount = selections.Count(s => s.Contains("#"));
            return completeKeyCount > selections.Count / 2;
        }

        /// <summary>
        /// 收集画布状态信息
        /// </summary>
        private void CollectCanvasState(CanvasDesignData canvasDesign)
        {
            try
            {
                // 获取画布缩放状态
                _eventAggregator?.GetEvent<GetCanvasZoomRequestEvent>()?.Publish(zoom =>
                {
                    canvasDesign.CanvasZoom = zoom;
                });

                // 获取选中设备
                _eventAggregator?.GetEvent<GetSelectedDeviceRequestEvent>()?.Publish(deviceId =>
                {
                    canvasDesign.SelectedDeviceId = deviceId;
                });

                Debug.WriteLine($"画布状态收集完成: 缩放{canvasDesign.CanvasZoom}, 选中设备{canvasDesign.SelectedDeviceId ?? "无"}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"收集画布状态失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 收集流程设计数据
        /// </summary>
        private void CollectFlowDesignData(ProjectDocument projectDocument)
        {
            try
            {
                Debug.WriteLine("收集流程设计数据");

                // 直接从FlowDesignerViewModel获取数据
                _eventAggregator?.GetEvent<GetCurrentFlowDataRequestEvent>()?.Publish(flowDocument =>
                {
                    if (flowDocument != null)
                    {
                        projectDocument.FlowDesign.Name = flowDocument.Name;
                        projectDocument.FlowDesign.CanvasWidth = flowDocument.CanvasWidth;
                        projectDocument.FlowDesign.CanvasHeight = flowDocument.CanvasHeight;
                        projectDocument.FlowDesign.StartNodeX = flowDocument.StartNodeX;
                        projectDocument.FlowDesign.StartNodeY = flowDocument.StartNodeY;
                        projectDocument.FlowDesign.CommandNodes = flowDocument.CommandNodes ?? new List<FlowCommandNodeData>();
                        projectDocument.FlowDesign.Connections = flowDocument.Connections ?? new List<FlowConnectionData>();
                        projectDocument.FlowDesign.FlowProperties = flowDocument.FlowProperties ?? new Dictionary<string, object>();
                        projectDocument.FlowDesign.LastModifiedTime = flowDocument.LastModifiedTime;
                    }
                });

                Debug.WriteLine($"流程数据收集完成: 节点{projectDocument.FlowDesign.CommandNodes.Count}个, 连接{projectDocument.FlowDesign.Connections.Count}个");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"收集流程设计数据失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 处理清空项目请求
        /// </summary>
        private void OnClearProjectRequest()
        {
            try
            {
                Debug.WriteLine("开始清空整个项目");

                // 清空画布
                _eventAggregator?.GetEvent<ClearCanvasRequestEvent>()?.Publish();

                // 清空流程
                _eventAggregator?.GetEvent<ClearFlowRequestEvent>()?.Publish();

                Debug.WriteLine("项目清空完成");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"清空项目失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 处理加载项目请求
        /// </summary>
        private void OnLoadProjectRequest(ProjectDocument projectDocument)
        {
            try
            {
                Debug.WriteLine($"开始加载项目: {projectDocument.Name}");

                // 先清空现有内容
                OnClearProjectRequest();

                //  显示加载进度
                _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish("正在加载项目...");

                //  异步加载，避免阻塞UI
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(async () =>
                {
                    await LoadProjectAsync(projectDocument);
                }), System.Windows.Threading.DispatcherPriority.Normal);

            }
            catch (Exception ex)
            {
                Debug.WriteLine($"加载项目失败: {ex.Message}");
            }
        }

        /// <summary>
        ///  新增：异步加载项目
        /// </summary>
        private async System.Threading.Tasks.Task LoadProjectAsync(ProjectDocument projectDocument)
        {
            try
            {
                // 步骤1：恢复基础数据（不涉及UI）
                RestoreDeviceDisplayNames(projectDocument.CanvasDesign.DeviceDisplayNames,
                    projectDocument.CanvasDesign.DeviceNameCounters);
                RestoreMonitoringParameterStates(projectDocument.CanvasDesign);

                _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish("正在加载画布...");

                // 步骤2：加载画布设计（包含设备和管道）
                LoadCanvasDesignAsync(projectDocument.CanvasDesign);
                // 提取 DeviceId → DeviceType 映射,供流程加载时按 ID 精准匹配设备类型
                var deviceIdToTypeMap = projectDocument.CanvasDesign?.Devices?
                    .Where(d => !string.IsNullOrEmpty(d.DeviceId) && !string.IsNullOrEmpty(d.DeviceType))
                    .GroupBy(d => d.DeviceId)
                    .ToDictionary(g => g.Key, g => g.First().DeviceType)
                    ?? new Dictionary<string, string>();
                _eventAggregator?.GetEvent<DeviceTypeMapUpdatedEvent>()?.Publish(deviceIdToTypeMap);
                //  等待画布加载完成
                await System.Threading.Tasks.Task.Delay(100);

                _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish("正在加载流程...");

                // 步骤3：加载流程设计
                LoadFlowDesign(projectDocument.FlowDesign);

                //  等待流程加载完成
                await System.Threading.Tasks.Task.Delay(50);

                // 步骤4：发布项目加载完成事件
                _eventAggregator?.GetEvent<ProjectLoadedEvent>()?.Publish(projectDocument);

                _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish("项目加载完成");

                Debug.WriteLine("项目加载完成");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"异步加载项目失败: {ex.Message}");
                _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish($"加载失败: {ex.Message}");
            }
        }

        /// <summary>
        ///  修改：异步加载画布设计
        /// </summary>
        private void LoadCanvasDesignAsync(CanvasDesignData canvasDesign)
        {
            try
            {
                if (canvasDesign == null) return;

                // 创建 CanvasDocument
                var canvasDocument = new CanvasDocument
                {
                    Id = canvasDesign.Id,
                    Name = canvasDesign.Name,
                    CreatedTime = canvasDesign.CreatedTime,
                    LastModifiedTime = canvasDesign.LastModifiedTime,
                    RegionLayout = canvasDesign.RegionLayout,
                    Devices = canvasDesign.Devices,
                    Connections = canvasDesign.Connections,
                    Pipes = canvasDesign.Pipes ?? new List<CanvasPipeData>(),
                    CanvasWidth = canvasDesign.CanvasWidth,
                    CanvasHeight = canvasDesign.CanvasHeight
                };

                //  发布加载请求（异步处理）
                _eventAggregator?.GetEvent<LoadCanvasRequestEvent>()?.Publish(canvasDocument);

                // 恢复区域布局
                if (canvasDesign.RegionLayout != null)
                {
                    _eventAggregator?.GetEvent<RegionLayoutUpdatedEvent>()?.Publish(canvasDesign.RegionLayout);
                    _eventAggregator?.GetEvent<RestoreRegionLayoutEvent>()?.Publish(canvasDesign.RegionLayout);
                }

                // 延迟恢复画布状态
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
                {
                    RestoreCanvasState(canvasDesign);
                }), System.Windows.Threading.DispatcherPriority.Background);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"加载画布设计失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 加载画布设计
        /// </summary>
        private void LoadCanvasDesign(CanvasDesignData canvasDesign)
        {
            try
            {
                Debug.WriteLine("开始加载画布设计");

                if (canvasDesign == null) return;

                // 创建兼容的CanvasDocument
                var canvasDocument = new CanvasDocument
                {
                    Id = canvasDesign.Id,
                    Name = canvasDesign.Name,
                    CreatedTime = canvasDesign.CreatedTime,
                    LastModifiedTime = canvasDesign.LastModifiedTime,
                    RegionLayout = canvasDesign.RegionLayout,
                    Devices = canvasDesign.Devices,
                    Connections = canvasDesign.Connections,
                    Pipes = canvasDesign.Pipes ?? new List<CanvasPipeData>(), //  包含管道数据
                    CanvasWidth = canvasDesign.CanvasWidth,
                    CanvasHeight = canvasDesign.CanvasHeight
                };

                // 恢复设备显示名映射
                RestoreDeviceDisplayNames(canvasDesign.DeviceDisplayNames, canvasDesign.DeviceNameCounters);

                // 恢复监控参数状态
                RestoreMonitoringParameterStates(canvasDesign);

                //  修改:同步发布加载画布请求(包含管道数据)
                _eventAggregator?.GetEvent<LoadCanvasRequestEvent>()?.Publish(canvasDocument);

                //  删除:不再单独调用 RestorePipeData
                // if (canvasDesign.Pipes != null && canvasDesign.Pipes.Count > 0)
                // {
                //     RestorePipeData(canvasDesign.Pipes);
                // }

                // 恢复区域布局到MainToolBarViewModel
                if (canvasDesign.RegionLayout != null)
                {
                    _eventAggregator?.GetEvent<RegionLayoutUpdatedEvent>()?.Publish(canvasDesign.RegionLayout);
                    _eventAggregator?.GetEvent<RestoreRegionLayoutEvent>()?.Publish(canvasDesign.RegionLayout);
                }

                // 延迟恢复画布状态(这个可以保留,因为是UI状态)
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
                {
                    RestoreCanvasState(canvasDesign);
                }), System.Windows.Threading.DispatcherPriority.Background);

                Debug.WriteLine("画布设计加载请求已发送");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"加载画布设计失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 恢复监控参数状态
        /// </summary>
        private void RestoreMonitoringParameterStates(CanvasDesignData canvasDesign)
        {
            try
            {
                Debug.WriteLine("开始恢复监控参数状态");

                // 步骤1：先恢复设备监控参数选择状态
                if (canvasDesign.DeviceMonitoringSelections != null && canvasDesign.DeviceMonitoringSelections.Count > 0)
                {
                    var processedSelections = ProcessMonitoringSelections(canvasDesign.DeviceMonitoringSelections);
                    _eventAggregator?.GetEvent<RestoreDeviceMonitoringSelectionsEvent>()?.Publish(processedSelections);
                    Debug.WriteLine($"发布恢复 {processedSelections.Count} 个设备的监控参数选择状态");
                }

                //  步骤2：恢复监控卡片位置（在卡片状态之前）
                if (canvasDesign.MonitoringCardPositions != null && canvasDesign.MonitoringCardPositions.Count > 0)
                {
                    _eventAggregator?.GetEvent<RestoreMonitoringCardPositionsEvent>()?.Publish(canvasDesign.MonitoringCardPositions);
                    Debug.WriteLine($"发布恢复 {canvasDesign.MonitoringCardPositions.Count} 个监控卡片位置");
                }

                // 步骤3：延迟恢复监控卡片状态，确保选择状态和位置已经恢复
                if (canvasDesign.DeviceMonitoringCardStates != null && canvasDesign.DeviceMonitoringCardStates.Count > 0)
                {
                    System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        Debug.WriteLine("第一次延迟恢复监控卡片状态");
                        _eventAggregator?.GetEvent<RestoreMonitoringCardStatesEvent>()?.Publish(canvasDesign.DeviceMonitoringCardStates);
                    }), System.Windows.Threading.DispatcherPriority.Loaded);

                    System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        Debug.WriteLine("第二次延迟恢复监控卡片状态");
                        _eventAggregator?.GetEvent<RestoreMonitoringCardStatesEvent>()?.Publish(canvasDesign.DeviceMonitoringCardStates);
                    }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);

                    System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        Debug.WriteLine("最终延迟恢复监控卡片状态");
                        _eventAggregator?.GetEvent<RestoreMonitoringCardStatesEvent>()?.Publish(canvasDesign.DeviceMonitoringCardStates);
                    }), System.Windows.Threading.DispatcherPriority.Background);
                }

                Debug.WriteLine("监控参数状态恢复发布完成");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"恢复监控参数状态失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 处理监控参数选择状态，确保格式正确
        /// </summary>
        private Dictionary<string, HashSet<string>> ProcessMonitoringSelections(Dictionary<string, HashSet<string>> originalSelections)
        {
            var processedSelections = new Dictionary<string, HashSet<string>>();

            foreach (var kvp in originalSelections)
            {
                var deviceId = kvp.Key;
                var selections = kvp.Value;

                if (IsCompleteKeyFormat(selections))
                {
                    // 已经是完整键名格式
                    processedSelections[deviceId] = new HashSet<string>(selections);
                    Debug.WriteLine($"设备 {deviceId} 已为完整键名格式: {string.Join(", ", selections)}");
                }
                else
                {
                    // 保持参数名格式，在恢复时由具体视图处理
                    processedSelections[deviceId] = new HashSet<string>(selections);
                    Debug.WriteLine($"设备 {deviceId} 为参数名格式，等待视图处理: {string.Join(", ", selections)}");
                }
            }

            return processedSelections;
        }

      
        /// <summary>
        /// 恢复设备显示名映射
        /// </summary>
        private void RestoreDeviceDisplayNames(Dictionary<string, string> displayNames, Dictionary<string, int> nameCounters)
        {
            try
            {
                if (displayNames != null && displayNames.Count > 0)
                {
                    _eventAggregator?.GetEvent<RestoreDeviceDisplayNamesEvent>()?.Publish(displayNames);
                    Debug.WriteLine($"恢复了 {displayNames.Count} 个设备显示名映射");
                }

                if (nameCounters != null && nameCounters.Count > 0)
                {
                    _eventAggregator?.GetEvent<RestoreDeviceNameCountersEvent>()?.Publish(nameCounters);
                    Debug.WriteLine($"恢复了 {nameCounters.Count} 个设备名称计数器");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"恢复设备显示名失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 恢复画布状态
        /// </summary>
        private void RestoreCanvasState(CanvasDesignData canvasDesign)
        {
            try
            {
                // 恢复缩放状态
                if (canvasDesign.CanvasZoom > 0 && Math.Abs(canvasDesign.CanvasZoom - 1.0) > 0.01)
                {
                    _eventAggregator?.GetEvent<ZoomCanvasEvent>()?.Publish(canvasDesign.CanvasZoom);
                }

                // 恢复选中设备
                if (!string.IsNullOrEmpty(canvasDesign.SelectedDeviceId))
                {
                    // 这里可能需要延迟一点时间确保设备已经创建完成
                    System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        _eventAggregator?.GetEvent<SelectDeviceEvent>()?.Publish(canvasDesign.SelectedDeviceId);
                    }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                }

                Debug.WriteLine("画布状态恢复完成");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"恢复画布状态失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 加载流程设计
        /// </summary>
        private void LoadFlowDesign(FlowDesignData flowDesign)
        {
            try
            {
                Debug.WriteLine("开始加载流程设计");

                if (flowDesign == null) return;

                // 创建流程文档
                var flowDocument = new FlowDocument
                {
                    Name = flowDesign.Name,
                    CanvasWidth = flowDesign.CanvasWidth,
                    CanvasHeight = flowDesign.CanvasHeight,
                    StartNodeX = flowDesign.StartNodeX,
                    StartNodeY = flowDesign.StartNodeY,
                    CommandNodes = flowDesign.CommandNodes,
                    Connections = flowDesign.Connections,
                    FlowProperties = flowDesign.FlowProperties,
                    LastModifiedTime = flowDesign.LastModifiedTime
                };

                // 发布加载流程请求
                _eventAggregator?.GetEvent<LoadFlowRequestEvent>()?.Publish(flowDocument);

                Debug.WriteLine("流程设计加载请求已发送");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"加载流程设计失败: {ex.Message}");
            }
        }
    }
}
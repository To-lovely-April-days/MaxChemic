using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using DevicePlugins.Devices;
using MaxChemical.Core;
using MaxChemical.Infrastructure.Services;
using MaxChemical.Modules.Designer.Event;
using MaxChemical.Modules.Designer.Models;
using MaxChemical.Modules.Designer.Views.Controls;
using Prism.Events;

namespace MaxChemical.Modules.Designer.Service
{
    public class CanvasDataService : ICanvasDataService
    {
        private readonly IEventAggregator _eventAggregator;
        private readonly IDeviceManager _deviceManager;

        public CanvasDataService(IEventAggregator eventAggregator, IDeviceManager deviceManager)
        {
            _eventAggregator = eventAggregator;
            _deviceManager = deviceManager;
        }

        #region 设备相关

        public IDevice RestoreDeviceFromData(CanvasDeviceData deviceData)
        {
            try
            {
                // 1. 从设备管理器中查找对应类型的设备
                var allDevices = _deviceManager.GetAllDevices();
                var deviceTemplate = allDevices.FirstOrDefault(d =>
                    d.GetType().Name == deviceData.DeviceType ||
                    d.Name == deviceData.DeviceName);

                if (deviceTemplate == null)
                {
                    System.Diagnostics.Debug.WriteLine($"未找到设备类型: {deviceData.DeviceType}");
                    return null;
                }

                // 2. 创建设备实例（如果设备支持克隆）
                IDevice restoredDevice = null;

                // 方法1：尝试通过反射创建新实例
                try
                {
                    var deviceType = deviceTemplate.GetType();
                    restoredDevice = (IDevice)Activator.CreateInstance(deviceType);
                }
                catch
                {
                    // 方法2：如果反射失败，使用模板设备（注意：这可能导致多个画布元素共享同一设备实例）
                    restoredDevice = deviceTemplate;
                    System.Diagnostics.Debug.WriteLine($"警告：使用模板设备实例 - {deviceData.DeviceName}");
                }

                // 3. 恢复设备参数
                if (restoredDevice != null && deviceData.Parameters != null)
                {
                    RestoreDeviceParameters(restoredDevice, deviceData.Parameters);
                }

                return restoredDevice;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"恢复设备失败: {ex.Message}");
                return null;
            }
        }

        private void RestoreDeviceParameters(IDevice device, Dictionary<string, object> parameters)
        {
            try
            {
                if (device.Parameters?.Variables == null) return;

                foreach (var param in parameters)
                {
                    var deviceParam = device.Parameters.Variables.FirstOrDefault(v => v.Name == param.Key);
                    if (deviceParam != null)
                    {
                        // 尝试转换并设置参数值
                        try
                        {
                            if (param.Value != null)
                            {
                                // 根据参数类型进行转换
                                if (deviceParam is StringParameter stringParam)
                                {
                                    stringParam.Value = param.Value.ToString();
                                }
                                else if (deviceParam is NumberParameter numberParam)
                                {
                                    if (double.TryParse(param.Value.ToString(), out double numValue))
                                    {
                                        numberParam.Value = numValue;
                                    }
                                }
                                else if (deviceParam is BooleanParameter boolParam)
                                {
                                    if (bool.TryParse(param.Value.ToString(), out bool boolValue))
                                    {
                                        boolParam.Value = boolValue;
                                    }
                                }
                                else
                                {
                                    deviceParam.Value = param.Value;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"恢复参数 {param.Key} 失败: {ex.Message}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"恢复设备参数失败: {ex.Message}");
            }
        }

        #endregion

        #region 管道相关

        /// <summary>
        ///  从管道数据恢复管道控件
        /// </summary>
        public FrameworkElement RestorePipeFromData(CanvasPipeData pipeData)
        {
            try
            {
                if (pipeData == null)
                {
                    System.Diagnostics.Debug.WriteLine("管道数据为空");
                    return null;
                }

                FrameworkElement pipeControl = null;

                // 根据类型创建管道控件
                switch (pipeData.PipeType)
                {
                    case "Straight":
                        var straightPipe = new StraightPipeControl
                        {
                            PipeId = pipeData.PipeId,
                            PipeLength = pipeData.PipeLength ?? 80,
                            PipeWidth = pipeData.PipeWidth,
                            RotationAngle = pipeData.RotationAngle,
                            IsSelected = false // 恢复时不选中
                        };
                        pipeControl = straightPipe;
                        System.Diagnostics.Debug.WriteLine($" 恢复直管道: {pipeData.PipeId}, 长度={pipeData.PipeLength}");
                        break;

                    case "Elbow":
                        var elbowPipe = new ElbowPipeControl
                        {
                            PipeId = pipeData.PipeId,
                            HorizontalLength = pipeData.HorizontalLength ?? 50,
                            VerticalLength = pipeData.VerticalLength ?? 60,
                            PipeWidth = pipeData.PipeWidth,
                            RotationAngle = pipeData.RotationAngle,
                            IsSelected = false
                        };
                        pipeControl = elbowPipe;
                        System.Diagnostics.Debug.WriteLine($" 恢复弯管道: {pipeData.PipeId}, H={pipeData.HorizontalLength}, V={pipeData.VerticalLength}");
                        break;

                    case "ZShape":
                        var zPipe = new ZShapePipeControl
                        {
                            PipeId = pipeData.PipeId,
                            TopHorizontalLength = pipeData.TopHorizontalLength ?? 80,
                            BottomHorizontalLength = pipeData.BottomHorizontalLength ?? 80,
                            VerticalOffset = pipeData.VerticalOffset ?? 60,
                            PipeWidth = pipeData.PipeWidth,
                            RotationAngle = pipeData.RotationAngle,
                            IsSelected = false
                        };
                        pipeControl = zPipe;
                        System.Diagnostics.Debug.WriteLine($" 恢复Z形管道: {pipeData.PipeId}");
                        break;

                    default:
                        System.Diagnostics.Debug.WriteLine($" 未知管道类型: {pipeData.PipeType}");
                        return null;
                }

                return pipeControl;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($" 恢复管道失败: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        ///  新增：收集画布上的所有管道数据
        /// </summary>
        public List<CanvasPipeData> CollectPipeData()
        {
            List<CanvasPipeData> pipeDataList = null;
            var waitHandle = new ManualResetEventSlim(false);

            try
            {
                // 发布收集管道数据请求事件
                _eventAggregator?.GetEvent<CollectPipeDataRequestEvent>()?.Publish((pipes) =>
                {
                    pipeDataList = pipes;
                    waitHandle.Set();
                });

                // 等待2秒
                if (!waitHandle.Wait(2000))
                {
                    System.Diagnostics.Debug.WriteLine(" 收集管道数据超时");
                    return new List<CanvasPipeData>();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($" 收集管道数据失败: {ex.Message}");
            }
            finally
            {
                waitHandle.Dispose();
            }

            return pipeDataList ?? new List<CanvasPipeData>();
        }

        /// <summary>
        ///  新增：恢复管道数据到画布
        /// </summary>
        public void RestorePipeData(List<CanvasPipeData> pipesData)
        {
            try
            {
                if (pipesData == null || !pipesData.Any())
                {
                    System.Diagnostics.Debug.WriteLine("没有管道数据需要恢复");
                    return;
                }

                System.Diagnostics.Debug.WriteLine($"开始恢复 {pipesData.Count} 个管道");

                // 发布恢复管道数据请求事件
                _eventAggregator?.GetEvent<RestorePipeDataRequestEvent>()?.Publish(pipesData);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($" 恢复管道数据失败: {ex.Message}");
            }
        }

        #endregion

        #region 画布文档管理

        public CanvasDocument CreateNewCanvas(double canvasWidth = 1200, double canvasHeight = 800)
        {
            var newCanvas = new CanvasDocument
            {
                Name = $"新建画布_{DateTime.Now:HHmmss}",
                CanvasWidth = canvasWidth,
                CanvasHeight = canvasHeight,
                RegionLayout = CreateDefaultRegionLayout(canvasWidth, canvasHeight)
            };

            return newCanvas;
        }

        public bool SaveCanvas(CanvasDocument document, string filePath = null)
        {
            try
            {
                if (string.IsNullOrEmpty(filePath))
                {
                    // 如果没有指定路径，使用默认路径
                    var defaultPath = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                        "MaxChemical",
                        "Canvas"
                    );

                    if (!Directory.Exists(defaultPath))
                        Directory.CreateDirectory(defaultPath);

                    filePath = Path.Combine(defaultPath, $"{document.Name}_{DateTime.Now:yyyyMMdd_HHmmss}.json");
                }

                document.LastModifiedTime = DateTime.Now;

                var options = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                };

                var json = JsonSerializer.Serialize(document, options);
                File.WriteAllText(filePath, json);

                System.Diagnostics.Debug.WriteLine($" 画布已保存到: {filePath}");
                return true;
            }
            catch (Exception ex)
            {
                // 记录错误日志
                System.Diagnostics.Debug.WriteLine($" 保存画布失败: {ex.Message}");
                return false;
            }
        }

        public CanvasDocument LoadCanvas(string filePath)
        {
            try
            {
                if (!File.Exists(filePath))
                {
                    System.Diagnostics.Debug.WriteLine($" 文件不存在: {filePath}");
                    return null;
                }

                var json = File.ReadAllText(filePath);
                var document = JsonSerializer.Deserialize<CanvasDocument>(json);

                System.Diagnostics.Debug.WriteLine($" 画布已加载: {filePath}");
                System.Diagnostics.Debug.WriteLine($"  - 设备数量: {document?.Devices?.Count ?? 0}");
                System.Diagnostics.Debug.WriteLine($"  - 连接数量: {document?.Connections?.Count ?? 0}");
                System.Diagnostics.Debug.WriteLine($"  - 管道数量: {document?.Pipes?.Count ?? 0}"); //  新增

                return document;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($" 加载画布失败: {ex.Message}");
                return null;
            }
        }

        public CanvasDocument GetCurrentCanvasData()
        {
            CanvasDocument currentDocument = null;
            var waitHandle = new ManualResetEventSlim(false);

            try
            {
                var getDataEvent = _eventAggregator.GetEvent<GetCurrentCanvasDataRequestEvent>();
                getDataEvent?.Publish((document) =>
                {
                    currentDocument = document;
                    waitHandle.Set();
                });

                if (!waitHandle.Wait(5000)) // 5秒超时
                {
                    System.Diagnostics.Debug.WriteLine(" 获取画布数据超时");
                    return null;
                }

                return currentDocument;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($" 获取画布数据失败: {ex.Message}");
                return null;
            }
            finally
            {
                waitHandle.Dispose();
            }
        }

        #endregion

        #region 区域布局管理

        public RegionLayout CreateDefaultRegionLayout(double canvasWidth, double canvasHeight)
        {
            // 创建一个 2x2 的默认布局，根据画布大小分配区域
            var layout = new RegionLayout
            {
                Rows = 2,
                Cols = 2,
                UseIndependentRegions = true,
                IndependentRegions = new List<IndependentRegion>()
            };

            // 计算每个区域的尺寸
            double regionWidth = canvasWidth / layout.Cols;
            double regionHeight = canvasHeight / layout.Rows;

            // 创建 2x2 布局的区域
            var regionTypes = new RegionType[,]
            {
                { RegionType.Feed, RegionType.Reaction },
                { RegionType.PostProcess, RegionType.PostProcess }
            };

            for (int row = 0; row < layout.Rows; row++)
            {
                for (int col = 0; col < layout.Cols; col++)
                {
                    var region = new IndependentRegion
                    {
                        Type = regionTypes[row, col],
                        Left = col * regionWidth,
                        Top = row * regionHeight,
                        Right = (col + 1) * regionWidth,
                        Bottom = (row + 1) * regionHeight
                    };

                    layout.IndependentRegions.Add(region);
                }
            }

            // 初始化传统的网格单元格数据（保持兼容性）
            layout.Cells = new List<RegionCell>();
            for (int row = 0; row < layout.Rows; row++)
            {
                for (int col = 0; col < layout.Cols; col++)
                {
                    layout.Cells.Add(new RegionCell
                    {
                        Row = row,
                        Col = col,
                        Type = regionTypes[row, col]
                    });
                }
            }

            // 初始化列宽和行高
            layout.ColumnWidths = new List<double>();
            layout.RowHeights = new List<double>();

            for (int col = 0; col < layout.Cols; col++)
            {
                layout.ColumnWidths.Add(regionWidth);
            }

            for (int row = 0; row < layout.Rows; row++)
            {
                layout.RowHeights.Add(regionHeight);
            }

            return layout;
        }

        #endregion
    }
}
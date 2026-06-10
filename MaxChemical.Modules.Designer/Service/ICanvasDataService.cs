using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using DevicePlugins.Devices;
using MaxChemical.Core;
using MaxChemical.Modules.Designer.Models;

namespace MaxChemical.Modules.Designer.Service
{
    public interface ICanvasDataService
    {
        /// <summary>
        /// 保存画布文档
        /// </summary>
        bool SaveCanvas(CanvasDocument document, string filePath = null);

        /// <summary>
        /// 加载画布文档
        /// </summary>
        CanvasDocument LoadCanvas(string filePath);

        /// <summary>
        /// 创建新的画布文档
        /// </summary>
        CanvasDocument CreateNewCanvas(double canvasWidth = 1200, double canvasHeight = 800);

        /// <summary>
        /// 获取当前画布数据
        /// </summary>
        CanvasDocument GetCurrentCanvasData();

        /// <summary>
        /// 根据画布尺寸创建默认区域布局
        /// </summary>
        RegionLayout CreateDefaultRegionLayout(double canvasWidth, double canvasHeight);

        /// <summary>
        /// 根据设备数据重建设备实例
        /// </summary>
        IDevice RestoreDeviceFromData(CanvasDeviceData deviceData);

        //  新增：恢复管道方法
        FrameworkElement RestorePipeFromData(CanvasPipeData pipeData);
    }
}

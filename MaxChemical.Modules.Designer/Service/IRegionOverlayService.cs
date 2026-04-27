using System;
using System.Windows;
using System.Windows.Controls;
using MaxChemical.Core;

namespace MaxChemical.Modules.Designer.Services
{
    public interface IRegionOverlayService
    {
        /// <summary>
        /// 更新区域覆盖层
        /// </summary>
        void UpdateRegionOverlay(Canvas canvas, RegionLayout layout, double currentScale = 1.0);

        /// <summary>
        /// 清理区域覆盖层
        /// </summary>
        void ClearRegionOverlay(Canvas canvas);

        /// <summary>
        /// 开始拖动操作
        /// </summary>
        void StartDragOperation(Canvas canvas, RegionLayout layout);

        /// <summary>
        /// 拖动过程中更新
        /// </summary>
        void UpdateDuringDrag(Canvas canvas, RegionLayout layout, double currentScale = 1.0);

        /// <summary>
        /// 结束拖动操作
        /// </summary>
        void EndDragOperation(Canvas canvas, RegionLayout layout);

        // 新增：设置当前上下文的方法
        void SetCurrentContext(object currentDocument, Canvas designCanvas, Dictionary<string, Border> variablePanels = null, List<object> connections = null, IConnectionService connectionService = null);

        /// <summary>
        /// 区域布局变更事件
        /// </summary>
        event Action<RegionLayout> RegionLayoutChanged;

        /// <summary>
        /// 画布尺寸变更事件
        /// </summary>
        event Action<Size> CanvasSizeChanged;
    }
}
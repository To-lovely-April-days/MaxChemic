using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MaxChemical.Modules.Designer.Service
{
    public interface ICanvasStateService
    {
        /// <summary>
        /// 画布是否有未保存的修改
        /// </summary>
        bool HasUnsavedChanges { get; }
        string CurrentFilePath { get; }
        /// <summary>
        /// 标记画布已修改
        /// </summary>
        void MarkAsModified();

        /// <summary>
        /// 标记画布已保存
        /// </summary>
        void MarkAsSaved();

        /// <summary>
        /// 重置修改状态
        /// </summary>
        void Reset();

        /// <summary>
        /// 画布修改状态变化事件
        /// </summary>
        event Action<bool> ModificationStateChanged;

        void SetCurrentFile(string filePath);
    }
}

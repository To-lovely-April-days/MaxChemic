using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MaxChemical.Modules.Designer.Models
{
    /// <summary>
    /// 倒计时对话框结果
    /// </summary>
    public class CountdownResult
    {
        public bool IsCompleted { get; set; }
        public bool IsSkipped { get; set; }
        public bool IsStopped { get; set; }
        public double ActualWaitTime { get; set; }
    }

    /// <summary>
    /// 条件对话框结果
    /// </summary>
    public class ConditionDialogResult
    {
        public bool DialogResult { get; set; }
        public bool IsContinue { get; set; }
        public bool IsStopped { get; set; }
    }
}

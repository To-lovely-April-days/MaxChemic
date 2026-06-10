using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MaxChemical.Core
{
    /// <summary>
    /// 流程开始上下文
    /// </summary>
    public class FlowStartContext
    {
        public string FlowName { get; set; }
        public DateTime StartTime { get; set; }
        public int ExpectedNodeCount { get; set; }
        public Dictionary<string, object> Properties { get; set; } = new Dictionary<string, object>();
    }
    /// <summary>
    /// 流程完成上下文
    /// </summary>
    public class FlowCompletionContext
    {
        /// <summary>
        /// 流程是否成功完成
        /// </summary>
        public bool IsSuccessful { get; set; }

        /// <summary>
        /// 流程名称
        /// </summary>
        public string FlowName { get; set; }

        /// <summary>
        /// 完成时间
        /// </summary>
        public DateTime CompletionTime { get; set; }

        /// <summary>
        /// 流程执行持续时间
        /// </summary>
        public TimeSpan Duration { get; set; }

        /// <summary>
        /// 错误信息（如果失败）
        /// </summary>
        public string ErrorMessage { get; set; }

        /// <summary>
        /// 异常对象（如果失败）
        /// </summary>
        public Exception Exception { get; set; }

        /// <summary>
        /// 额外的上下文数据
        /// </summary>
        public Dictionary<string, object> Properties { get; set; } = new Dictionary<string, object>();
    }
    /// <summary>
    /// 流程失败上下文
    /// </summary>
    public class FlowFailureContext : FlowCompletionContext
    {
        public string FailedNodeId { get; set; }
        public string FailedNodeName { get; set; }
    }
}

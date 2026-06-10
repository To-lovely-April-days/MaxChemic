using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using MaxChemical.Core;

namespace DevicePlugins.Devices
{
    public interface IFlowLifecycleAware
    {
        /// <summary>
        /// 流程开始时调用
        /// </summary>
        Task<bool> OnFlowStartedAsync(FlowStartContext context);

        /// <summary>
        /// 流程完成时调用
        /// </summary>
        Task<bool> OnFlowCompletedAsync(FlowCompletionContext context);

        /// <summary>
        /// 流程失败时调用
        /// </summary>
        Task<bool> OnFlowFailedAsync(FlowFailureContext context);
    }


}

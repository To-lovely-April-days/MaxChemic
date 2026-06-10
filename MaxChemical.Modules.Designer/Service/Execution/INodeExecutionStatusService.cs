// MaxChemical.Modules.Designer.Services.Execution.INodeExecutionStatusService.cs
using System;
using System.Collections.Generic;
using MaxChemical.Modules.Designer.Models;
using MaxChemical.Modules.Designer.ViewModels;
using static MaxChemical.Modules.Designer.Models.CommandNode;

namespace MaxChemical.Modules.Designer.Service.Execution
{
    public interface INodeExecutionStatusService
    {
        /// <summary>
        /// 设置当前流程设计器视图模型
        /// </summary>
        void SetCurrentFlowViewModel(FlowDesignerViewModel flowViewModel);
        /// <summary>
        /// 获取当前流程设计器视图模型
        /// </summary>
        FlowDesignerViewModel GetCurrentFlowViewModel();

        /// <summary>
        /// 设置节点执行状态
        /// </summary>
        void SetNodeExecutionStatus(string nodeId, NodeExecutionStatus status);

        /// <summary>
        /// 开始节点执行
        /// </summary>
        void StartNodeExecution(string nodeId);

        /// <summary>
        /// 完成节点执行
        /// </summary>
        void CompleteNodeExecution(string nodeId, bool isSuccessful = true, string errorMessage = null);
        //   /// <summary>
        //   /// 流程结束后最终化状态：将残留的 Executing/Waiting 节点重置为 Idle，
        //   /// 但保留 Completed/Failed/Skipped 状态不变
        //   /// </summary>
         void FinalizeExecutionStatus();
        /// <summary>
        /// 重置所有节点执行状态
        /// </summary>
        void ResetAllExecutionStatus();

        /// <summary>
        /// 获取节点执行状态
        /// </summary>
        NodeExecutionStatus GetNodeExecutionStatus(string nodeId);

        /// <summary>
        /// 节点执行状态变化事件
        /// </summary>
        event EventHandler<NodeExecutionStatusChangedEventArgs> NodeExecutionStatusChanged;
    }

    public class NodeExecutionStatusChangedEventArgs : EventArgs
    {
        public string NodeId { get; set; }
        public NodeExecutionStatus OldStatus { get; set; }
        public NodeExecutionStatus NewStatus { get; set; }
        public CommandNode Node { get; set; }
    }
}
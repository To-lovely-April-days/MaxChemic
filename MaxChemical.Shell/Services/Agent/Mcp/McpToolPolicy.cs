using System;
using System.Collections.Generic;
using System.Linq;

namespace MaxChemical.Shell.Services.Agent.Mcp
{
    /// <summary>
    /// 外部 MCP 客户端(WorkBuddy / Codex / Gemini CLI / Claude 等)可调用的工具白名单。
    ///
    /// ── 为什么是白名单,而不是按 RequiresConfirmation 过滤 ──────────────
    /// 直觉做法是「RequiresConfirmation==false 就是只读,可以放出去」。这条路是错的,
    /// 有三个反例:
    ///
    ///   1. **assign_* 四个派工工具是提权通道。** 它们自身 RequiresConfirmation=false,
    ///      但会启动专员子循环,而执行专员的工具清单里有 set_device_parameter、
    ///      execute_device_command、run_current_flow、stop_flow、execute_doe_batch。
    ///      放出任意一个派工工具,等于把全部写操作一起放出去,而且绕过了确认卡
    ///      (确认事件的订阅方是聊天窗 ViewModel,外部客户端根本不在那条链路上)。
    ///   2. **connect_devices 会真的开串口/建 TCP。** 标志位是 false,因为它在本地
    ///      交互里无需确认,但它对硬件有物理副作用。
    ///   3. **pause_flow 是控制动作。** 暂停正在跑的流程,标志位同样是 false。
    ///
    /// 所以这里采用**默认拒绝 + 显式放行**:不在名单里的一律拒掉。
    /// 这条性质在将来加新工具时才真正值钱——新工具默认不会被暴露出去,
    /// 必须有人主动往名单里加,而加的时候会看到这段注释。
    ///
    /// ── 当前阶段(Step 1)只放只读 ──────────────────────────────────
    /// 写操作要等评估报告里的 P0 安全项补齐(驱动流程中止不停机、硬件急停、
    /// root 弱口令、明文回传通道),并且要先有一条外部调用能走到的确认与审计通道。
    /// 在那之前 <see cref="Mode"/> 只支持 readonly / disabled 两种取值。
    /// </summary>
    public sealed class McpToolPolicy
    {
        /// <summary>只读模式:仅放行下方白名单。这是默认值。</summary>
        public const string ModeReadOnly = "readonly";

        /// <summary>完全关闭:不启动管道服务。</summary>
        public const string ModeDisabled = "disabled";

        /// <summary>
        /// 只读白名单。判据是「调用它不会改变实验室的物理状态、不写库、不写盘、不弹界面」。
        /// 分组只为可读,运行时是一个扁平集合。
        /// </summary>
        private static readonly HashSet<string> ReadOnlyAllowList = new(StringComparer.Ordinal)
        {
            // 设备与流程:纯读状态
            "list_devices",
            "get_device_status",
            "get_flow_status",
            "list_flow_wait_nodes",

            // DOE:查询与进度
            "find_doe_project",
            "get_doe_progress",
            "get_doe_autopilot_config",
            "list_factor_candidates",

            // 分析:统计与解读(只读库)
            "analyze_doe_batch",
            "analyze_doe_project",
            "query_experiment_history",
            "search_lab_history",
            "get_decision_report",

            // 预测与机理:纯计算,不落库
            "predict_response",
            "suggest_optimal_conditions",
            "fit_kinetics",
            "predict_kinetics",
            "simulate_reaction",

            // 预览:只生成内存中的预览对象,入库是另一个工具(create_doe_batch,未放行)
            "preview_doe_design",
            "preview_staircase_sweep",
            "create_next_round_preview",
        };

        /// <summary>
        /// 显式拒绝理由。只用于把「为什么拒」讲清楚——不在这张表里的工具同样会被拒,
        /// 只是回一句通用说明。给出理由是为了让现场排障时不必翻代码。
        /// </summary>
        private static readonly Dictionary<string, string> DenyReasons = new(StringComparer.Ordinal)
        {
            ["assign_design_task"] = "派工工具会启动专员子循环,专员持有写操作工具,等同提权",
            ["assign_execution_task"] = "派工工具会启动专员子循环,执行专员可直接控制设备,等同提权",
            ["assign_analysis_task"] = "派工工具会启动专员子循环,等同提权",
            ["assign_mechanism_task"] = "派工工具会启动专员子循环,专员可写入取样数据,等同提权",

            ["connect_devices"] = "会真实打开串口/建立 TCP 连接,对硬件有物理副作用",
            ["pause_flow"] = "暂停正在运行的流程属于控制动作",
            ["ask_user_choice"] = "会在现场机器上弹出选择卡,外部客户端不应打断现场操作员",

            // 未放行但也不是明确的写操作:尚未核实它是否会把拟合结果落库。
            // 拿不准时按拒绝处理——放行一个其实会写库的工具,代价远大于少放一个查询。
            ["fit_reaction_network"] = "尚未核实是否会将拟合结果写入数据库,暂不放行",

            ["inspect_experiment_file"] = "按外部传入的路径读取本地文件,存在路径穿越风险",
            ["create_import_preview"] = "依赖外部文件路径,同上",
            ["generate_project_report"] = "会向磁盘写入 PDF 文件",

            ["set_device_parameter"] = "写操作:直接改设备参数",
            ["execute_device_command"] = "写操作:直接驱动硬件动作",
            ["run_current_flow"] = "写操作:启动流程",
            ["resume_flow"] = "写操作:恢复流程",
            ["stop_flow"] = "写操作:中止流程",
            ["build_flow"] = "写操作:改动设计器画布",
            ["create_doe_batch"] = "写操作:批次入库",
            ["execute_doe_batch"] = "写操作:启动 DOE 批次执行",
            ["set_process_condition"] = "写操作:联动写入两台泵的设定值",
            ["record_run_samples"] = "写操作:取样数据入库",
            ["record_run_response"] = "写操作:实验结果回填入库",
        };

        public McpToolPolicy(string mode)
        {
            Mode = Normalize(mode);
        }

        /// <summary>当前模式(已归一化)。</summary>
        public string Mode { get; }

        public bool IsEnabled => Mode != ModeDisabled;

        /// <summary>
        /// 把配置里的字符串归一化成受支持的模式。
        /// 无法识别的取值(含将来的 "full")一律降级为只读——**安全默认**:
        /// 配置写错时应该少放权限,而不是多放。
        /// </summary>
        public static string Normalize(string configured)
        {
            if (string.IsNullOrWhiteSpace(configured)) return ModeReadOnly;
            string v = configured.Trim().ToLowerInvariant();
            return v switch
            {
                ModeDisabled => ModeDisabled,
                ModeReadOnly => ModeReadOnly,
                _ => ModeReadOnly
            };
        }

        /// <summary>判断某工具是否可暴露给外部 MCP 客户端。</summary>
        public bool IsAllowed(string toolName, out string denyReason)
        {
            denyReason = null;

            if (!IsEnabled)
            {
                denyReason = "外部 MCP 接口已关闭";
                return false;
            }

            if (string.IsNullOrWhiteSpace(toolName))
            {
                denyReason = "工具名为空";
                return false;
            }

            if (ReadOnlyAllowList.Contains(toolName)) return true;

            denyReason = DenyReasons.TryGetValue(toolName, out string r)
                ? r
                : "该工具不在外部只读白名单内";
            return false;
        }

        /// <summary>从注册表里筛出可暴露的工具。</summary>
        public IReadOnlyList<IAgentTool> Filter(IEnumerable<IAgentTool> all)
        {
            if (all == null || !IsEnabled) return Array.Empty<IAgentTool>();
            return all.Where(t => t != null && IsAllowed(t.Name, out _)).ToList();
        }

        /// <summary>
        /// 白名单里但注册表中不存在的工具名(拼写错误或工具被删)。
        /// 装配时打一条日志,免得白名单悄悄失效。
        /// </summary>
        public IReadOnlyList<string> FindStaleEntries(IEnumerable<IAgentTool> all)
        {
            var present = new HashSet<string>((all ?? Array.Empty<IAgentTool>())
                .Where(t => t != null).Select(t => t.Name), StringComparer.Ordinal);
            return ReadOnlyAllowList.Where(n => !present.Contains(n)).OrderBy(n => n).ToList();
        }

        public int AllowListCount => ReadOnlyAllowList.Count;
    }
}

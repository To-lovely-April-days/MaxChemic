using System.Collections.Generic;
using Prism.Events;

namespace MaxChemical.Infrastructure.Cloud
{
    /// <summary>
    /// 工艺上云 · DOE 联动的跨模块事件。
    /// 放在 Infrastructure(Designer 与 DOE 都引用它),避免 Designer←DOE 的引用环:
    ///  - DOE 的 DOEBatchExecutor 发布 <see cref="CloudDoeSnapshotEvent"/>(运行表 + 当前组 + 等待填结果)
    ///  - Designer 的 CloudPushService 收到后 POST 到云端;并把网页远程提交的结果
    ///    通过 <see cref="CloudDoeRemoteResponseEvent"/> 回发给 DOEBatchExecutor 完成该组。
    /// 属性用 PascalCase,由 CloudPushService 以 CamelCase 策略序列化成网页约定的 JSON。
    /// </summary>
    public class CloudDoeSnapshot
    {
        public string BatchId { get; set; } = "";
        public string BatchName { get; set; } = "";
        public string Status { get; set; } = "";
        public int CurrentRunIndex { get; set; } = -1;
        public List<string> Factors { get; set; } = new();
        public List<CloudDoeResponseDef> Responses { get; set; } = new();
        public List<CloudDoeRun> Runs { get; set; } = new();
        /// <summary>非空 = 该组已跑完、正在等待填结果(网页据此弹远程表单)。</summary>
        public CloudDoeWaiting? Waiting { get; set; }
    }

    public class CloudDoeResponseDef
    {
        public string Name { get; set; } = "";
        public string Unit { get; set; } = "";
    }

    public class CloudDoeRun
    {
        public int RunIndex { get; set; }
        public string Status { get; set; } = "";
        public Dictionary<string, object> Factors { get; set; } = new();
        public Dictionary<string, double> Responses { get; set; } = new();
    }

    public class CloudDoeWaiting
    {
        public int RunIndex { get; set; }
        public Dictionary<string, object> Factors { get; set; } = new();
        public List<CloudDoeResponseDef> Responses { get; set; } = new();
    }

    public class CloudDoeRemoteResponse
    {
        public string BatchId { get; set; } = "";
        public int RunIndex { get; set; }
        public Dictionary<string, double> Values { get; set; } = new();
    }

    /// <summary>DOE → 云端:运行表快照(状态变化时发布)。</summary>
    public class CloudDoeSnapshotEvent : PubSubEvent<CloudDoeSnapshot> { }

    /// <summary>云端 → DOE:网页远程提交的某组测量结果。</summary>
    public class CloudDoeRemoteResponseEvent : PubSubEvent<CloudDoeRemoteResponse> { }
}

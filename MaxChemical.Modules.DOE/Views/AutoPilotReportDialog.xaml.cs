using MaxChemical.Modules.DOE.Models;
using MaxChemical.Modules.DOE.Services;
using System.Windows;
using System.Windows.Input;

namespace MaxChemical.Modules.DOE.Views
{
    /// <summary>
    /// AutoPilot 决策报告弹窗
    /// 
    /// 每轮实验完成后弹出，展示:
    ///   - 本轮分析结果（R², LOF, 因子排名）
    ///   - 系统决策（淘汰因子、调范围、选设计方法）
    ///   - 决策理由
    ///   - 下一轮方案预览
    ///   - 用户操作按钮: 采纳 / 修改后采纳 / 手动决策
    /// </summary>
    public partial class AutoPilotReportDialog : Window
    {
        public AutoPilotReport Report { get; }
        public UserAction SelectedAction { get; private set; } = UserAction.None;
        public NextStepRecommendation? OverrideDecision { get; private set; }
        public string? OverrideNote { get; private set; }

        public AutoPilotReportDialog(AutoPilotReport report)
        {
            Report = report;
            DataContext = this;
            InitializeComponent();
        }

        // ── 绑定属性 ──

        public string RoundText => $"第 {Report.RoundSummary?.RoundNumber ?? 0} 轮分析报告";

        public string R2Text => Report.RoundSummary?.RSquared.HasValue == true
            ? $"{Report.RoundSummary.RSquared:F4}" : "—";

        public string R2AdjText => Report.RoundSummary?.RSquaredAdj.HasValue == true
            ? $"{Report.RoundSummary.RSquaredAdj:F4}" : "—";

        public string R2PredText => Report.RoundSummary?.RSquaredPred.HasValue == true
            ? $"{Report.RoundSummary.RSquaredPred:F4}" : "—";

        public string LofText => Report.RoundSummary?.LackOfFitP.HasValue == true
            ? $"{Report.RoundSummary.LackOfFitP:F4}" : "—";

        public string PhaseText => Report.RoundSummary?.Phase switch
        {
            DOEProjectPhase.Screening => "筛选阶段",
            DOEProjectPhase.PathSearch => "路径探索",
            DOEProjectPhase.RSM => "响应面优化",
            DOEProjectPhase.Confirmation => "验证确认",
            DOEProjectPhase.Completed => "已完成",
            _ => "—"
        };

        public string RecommendationText => Report.Recommendation switch
        {
            NextStepRecommendation.ContinueScreening => "继续筛选",
            NextStepRecommendation.SteepestAscent => "最速上升",
            NextStepRecommendation.StartRSM => "进入 RSM",
            NextStepRecommendation.AugmentDesign => "补充实验",
            NextStepRecommendation.ExpandRange => "扩展范围",
            NextStepRecommendation.ConfirmationRuns => "验证实验",
            NextStepRecommendation.Complete => "优化完成",
            _ => "需要用户决策"
        };

        public string ReasonText => Report.RecommendationReason;
        public string NextMethodText => Report.NextDesignMethodDisplay;
        public string NextPhaseText => Report.NextPhaseDisplay;
        public string NextFactorText => $"{Report.NextFactorCount} 个因子";
        public string NextRunsText => $"约 {Report.EstimatedRuns} 组实验";

        public bool IsAutoExecuted => Report.AutoExecuted;
        public bool IsCompleted => Report.ProjectCompleted;
        public bool NeedsUserAction => !IsAutoExecuted && !IsCompleted;
        public bool IsSafeguard => Report.IsSafeguardTriggered;

        public string AutoExecutedText => Report.AutoExecuted
            ? $"系统已自动创建下一轮批次 ({Report.NextBatchId})" : "";

        // ── 按钮事件 ──

        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed) DragMove();
        }

        private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();

        /// <summary>采纳系统推荐</summary>
        private void AcceptBtn_Click(object sender, RoutedEventArgs e)
        {
            SelectedAction = UserAction.Accept;
            Close();
        }

        /// <summary>手动决策（关闭弹窗，用户自己操作）</summary>
        private void ManualBtn_Click(object sender, RoutedEventArgs e)
        {
            SelectedAction = UserAction.Manual;
            Close();
        }

        /// <summary>跳过（不执行任何操作）</summary>
        private void SkipBtn_Click(object sender, RoutedEventArgs e)
        {
            SelectedAction = UserAction.Skip;
            Close();
        }

        public enum UserAction
        {
            None,
            Accept,
            Manual,
            Skip
        }
    }
}
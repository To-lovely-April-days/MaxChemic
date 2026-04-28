using MaxChemical.Infrastructure.Services;
using MaxChemical.Modules.DOE.Models;
using MaxChemical.Modules.DOE.Services;
using OxyPlot;
using Prism.Ioc;
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
        private readonly ILocalizationService _localization;

        public AutoPilotReport Report { get; }
        public UserAction SelectedAction { get; private set; } = UserAction.None;
        public NextStepRecommendation? OverrideDecision { get; private set; }
        public string? OverrideNote { get; private set; }

        public AutoPilotReportDialog(AutoPilotReport report)
        {
            Report = report;
            DataContext = this;
            InitializeComponent();

            _localization = ContainerLocator.Container.Resolve<ILocalizationService>();

            UpdateLocalizationText();
        }

        // ── 绑定属性 ──

        public string RoundText => string.Format(_localization.GetString("Doe_PilotReport_Roundreport", "第 {0} 轮分析报告"),Report.RoundSummary?.RoundNumber ?? 0);

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
            DOEProjectPhase.Screening => _localization.GetString("Doe_ProjectPhase_Screening", "筛选"),
            DOEProjectPhase.PathSearch => _localization.GetString("Doe_ProjectPhase_PathSearch", "爬坡"),
            DOEProjectPhase.RSM => _localization.GetString("Doe_ProjectPhase_RSM", "RSM"),
            DOEProjectPhase.Augmenting => _localization.GetString("Doe_ProjectPhase_Augmenting", "补点"),
            DOEProjectPhase.Confirmation => _localization.GetString("Doe_ProjectPhase_Confirmation", "验证"),
            DOEProjectPhase.Completed => _localization.GetString("Doe_ProjectPhase_Completed", "已完成"),
            _ => "—"
        };

        public string RecommendationText => Report.Recommendation switch
        {
            NextStepRecommendation.ContinueScreening => _localization.GetString("Doe_Overview_Recommend_Screening", "继续筛选不显著因子"),
            NextStepRecommendation.SteepestAscent => _localization.GetString("Doe_Overview_Recommend_Steepest", "沿梯度方向爬坡"),
            NextStepRecommendation.StartRSM => _localization.GetString("Doe_Overview_Recommend_StartRSM", "进入响应面优化"),
            NextStepRecommendation.ExpandRange => _localization.GetString("Doe_Overview_Recommend_Expand", "扩展因子范围"),
            NextStepRecommendation.ConfirmationRuns => _localization.GetString("Doe_Overview_Recommend_Confirmation", "推荐验证实验"),
            NextStepRecommendation.AugmentDesign => _localization.GetString("Doe_Overview_Recommend_Augment", "补充实验点"),
            NextStepRecommendation.Complete => _localization.GetString("Doe_Overview_Recommend_Complete", "优化完成"),
            _ => _localization.GetString("Doe_PilotReport_UseDecision", "需要用户决策")
        };

        public string ReasonText => Report.RecommendationReason;
        public string NextMethodText => Report.NextDesignMethodDisplay;
        public string NextPhaseText => Report.NextPhaseDisplay;
        public string NextFactorText => string.Format(_localization.GetString("Doe_PilotReport_CountFactor", "{0} 个因子"),Report.NextFactorCount);
        public string NextRunsText => string.Format(_localization.GetString("Doe_PilotTrport_GroupExperi", "约 {0} 组实验"),Report.EstimatedRuns);

        public bool IsAutoExecuted => Report.AutoExecuted;
        public bool IsCompleted => Report.ProjectCompleted;
        public bool NeedsUserAction => !IsAutoExecuted && !IsCompleted;
        public bool IsSafeguard => Report.IsSafeguardTriggered;

        public string AutoExecutedText => Report.AutoExecuted
            ? string.Format(_localization.GetString("Doe_pilotReport_SysCreateNext", "系统已自动创建下一轮批次 ({0})"),Report.NextBatchId)
            : "";

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

        /// <summary>
        /// 本地化标签文本
        /// </summary>
        private void UpdateLocalizationText()
        {
            TbTitle.Text = _localization.GetString("Doe_PilotReport_Title", "分析报告");
            TbSysDecision.Text = _localization.GetString("Doe_PilotReport_SysDeci", "系统决策");
            TbNextPlan.Text = _localization.GetString("Doe_PilotReport_NextPlan", "下一轮方案");
            TbDesignMethod.Text = _localization.GetString("Doe_PilotReport_DesignMethod", "设计方法");
            TbTargetPhase.Text = _localization.GetString("Doe_PilotReport_Phase", "目标阶段");
            TbFactor.Text = _localization.GetString("Doe_PilotReport_Factor", "因子数");
            TbPredict.Text = _localization.GetString("Doe_PilotReprot_Predict", "预计实验量");
            TbAutoExec.Text = _localization.GetString("Doe_PilotReprot_AutoExec", "已自动执行");
            TbOptimizeComplete.Text = _localization.GetString("Doe_PilotReport_Optimize", "优化目标达成");
            TbSuggestParam.Text = _localization.GetString("Doe_PilotReport_Suggest", "建议固化当前参数");
            BtnSkip.Content = _localization.GetString("Doe_PilotReport_Skip", "跳过");
            BtnManual.Content = _localization.GetString("Doe_PilotReport_Manual", "手动决策");
            BtnAccept.Content = _localization.GetString("Doe_PilotReport_Accept", "采纳推荐");
            BtnClose.Content = _localization.GetString("Doe_PilotReport_Close", "关闭");
        }
    }
}
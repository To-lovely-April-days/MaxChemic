using Prism.Ioc;
using MaxChemical.Modules.DOE.Models;
using System.Windows;
using System.Windows.Input;
using MaxChemical.Infrastructure.Services;

namespace MaxChemical.Modules.DOE.Views
{
    /// <summary>
    /// AutoPilot 参数设置弹窗
    /// 用户可调整所有决策参数，或一键恢复默认值
    /// </summary>
    public partial class AutoPilotConfigDialog : Window
    {
        public AutoPilotConfig Config { get; private set; }
        public bool Confirmed { get; private set; }

        public AutoPilotConfigDialog(AutoPilotConfig config)
        {
            Config = new AutoPilotConfig
            {
                ProjectId = config.ProjectId,
                Enabled = config.Enabled,
                AlphaScreen = config.AlphaScreen,
                R2Min = config.R2Min,
                R2Good = config.R2Good,
                AlphaLOF = config.AlphaLOF,
                R2ImprovementMin = config.R2ImprovementMin,
                DTarget = config.DTarget,
                ConfirmationReps = config.ConfirmationReps,
                MaxRounds = config.MaxRounds,
                MaxTotalRuns = config.MaxTotalRuns,
                RangeExpansionRatio = config.RangeExpansionRatio,
                MaxConsecutiveSameAction = config.MaxConsecutiveSameAction
            };
            DataContext = Config;
            InitializeComponent();
            UpdateLocalizationText();
        }

        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed) DragMove();
        }

        private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();

        private void ResetBtn_Click(object sender, RoutedEventArgs e)
        {
            var def = AutoPilotConfig.CreateDefault(Config.ProjectId);
            Config.AlphaScreen = def.AlphaScreen;
            Config.R2Min = def.R2Min;
            Config.R2Good = def.R2Good;
            Config.AlphaLOF = def.AlphaLOF;
            Config.R2ImprovementMin = def.R2ImprovementMin;
            Config.DTarget = def.DTarget;
            Config.ConfirmationReps = def.ConfirmationReps;
            Config.MaxRounds = def.MaxRounds;
            Config.MaxTotalRuns = def.MaxTotalRuns;
            Config.RangeExpansionRatio = def.RangeExpansionRatio;
            Config.MaxConsecutiveSameAction = def.MaxConsecutiveSameAction;
            DataContext = null;
            DataContext = Config;
        }

        private void SaveBtn_Click(object sender, RoutedEventArgs e)
        {
            Confirmed = true;
            Close();
        }


        private void UpdateLocalizationText()
        {
            ILocalizationService service = ContainerLocator.Container.Resolve<ILocalizationService>();
            if (service == null)
            {
                return;
            }
            TbTitleTxt.Text = service.GetString("Doe_Pilot_Title", "智能决策参数");
            TbSubTitleTxt.Text = service.GetString("Doe_Pilot_SecTitle", "AutoPilot 决策引擎 · 调整判断阈值与防护规则");
            TbParamHeader.Text = service.GetString("Doe_Pilot_Header_Param", "参数");
            TbInstructHeader.Text = service.GetString("Doe_Pilot_Header_Instruction", "说明");
            TbGroupHeader.Text = service.GetString("Doe_Pilot_Header_Group", "分组");
            TbValueHeader.Text = service.GetString("Doe_Pilot_Header_Value", "值");
            TbParamRow1.Text = service.GetString("Doe_Pilot_Param_Row1", "显著性 α");
            TbParamRow2.Text = service.GetString("Doe_Pilot_Param_Row2", "合格 R²");
            TbParamRow3.Text = service.GetString("Doe_pilot_Param_Row3", "优秀 R²");
            TbParamRow4.Text = service.GetString("Doe_Pilot_Param_Row4", "LOF 阈值");
            TbParamRow5.Text = service.GetString("Doe_Pilot_Param_Row5", "R² 改善增量");
            TbParamRow7.Text = service.GetString("Doe_Pilot_Param_Row7", "验证重复次数");
            TbParamRow8.Text = service.GetString("Doe_Pilot_Param_Row8", "最大迭代轮次");
            TbParamRow9.Text = service.GetString("Doe_Pilot_Param_Row9", "最大总实验组数");
            TbParamRow10.Text = service.GetString("Doe_Pilot_Param_Row10", "范围扩展比例");
            TbParamRow11.Text = service.GetString("Doe_Pilot_Param_Row11", "连续相同上限");
            TbInstrucRow1.Text = service.GetString("Doe_Pilot_Instruc_Row1", "p > α 则淘汰");
            TbInstrucRow2.Text = service.GetString("Doe_Pilot_Instruc_Row2", "R² &lt; 此值触发补点");
            TbInstrucRow3.Text = service.GetString("Doe_Pilot_Instruc_Row3", "R² ≥ 此值触发验证");
            TbInstrucRow4.Text = service.GetString("Doe_Pilot_Instruc_Row4", "p &lt; 此值表示失拟");
            TbInstrucRow5.Text = service.GetString("Doe_Pilot_Instruc_Row5", "连续不改善则暂停");
            TbInstrucRow6.Text = service.GetString("Doe_Pilot_Instruc_Row6", "D ≥ 此值视为达成");
            TbInstrucRow7.Text = service.GetString("Doe_Pilot_Instruc_Row7", "最优点重复次数");
            TbInstrucRow8.Text = service.GetString("Doe_Pilot_Instruc_Row8", "超过后强制暂停");
            TbInstrucRow9.Text = service.GetString("Doe_Pilot_Instruc_Row9", "成本控制上线");
            TbInstrucRow10.Text = service.GetString("Doe_Pilot_Instruc_Row10", "边界最优时拓展%");
            TbInstrucRow11.Text = service.GetString("Doe_Pilot_Instruc_Row11", "N 轮相同推荐则暂停");
            TbScreening.Text = service.GetString("Doe_Pilot_Screening", "筛选");
            TbGroupRow2.Text = service.GetString("Doe_Pilot_Model", "模型");
            TbGroupRow3.Text = service.GetString("Doe_Pilot_Model", "模型");
            TbGroupRow4.Text = service.GetString("Doe_Pilot_Model", "模型");
            TbGroupRow5.Text = service.GetString("Doe_Pilot_Model", "模型");
            TbGroupRow6.Text = service.GetString("Doe_Pilot_Target", "目标");
            TbGroupRow7.Text = service.GetString("Doe_Pilot_Target", "目标");
            TbGroupRow8.Text = service.GetString("Doe_Pilot_Protection", "防护");
            TbGroupRow9.Text = service.GetString("Doe_Pilot_Protection", "防护");
            TbGroupRow10.Text = service.GetString("Doe_Pilot_Protection", "防护");
            TbGroupRow11.Text = service.GetString("Doe_Pilot_Protection", "防护");
            BtnRestore.Content = service.GetString("Doe_Pilot_ResumeDefault", "恢复默认值");
            BtnCancel.Content = service.GetString("Doe_Pilot_Cancel", "取消");
            BtnSave.Content = service.GetString("Doe_Pilot_Save", "保存");
        }
    }
}
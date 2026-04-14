using MaxChemical.Modules.DOE.Models;
using System.Windows;
using System.Windows.Input;

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
    }
}
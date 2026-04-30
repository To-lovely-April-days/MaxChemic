using MaxChemical.Infrastructure.Services;
using Prism.Ioc;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace MaxChemical.Modules.DOE.Views
{
    public partial class DOEMiniPanel : UserControl
    {
        public DOEMiniPanel()
        {
            InitializeComponent();
            UpdateLocalizationText();
        }

        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                var window = Window.GetWindow(this);
                window?.DragMove();
            }
        }

        private void UpdateLocalizationText()
        {
            ILocalizationService service = ContainerLocator.Container.Resolve<ILocalizationService>();
            if (service == null)
            {
                return;
            }

            TbExecuted.Text = service.GetString("Doe_Mini_Execution", "执行中");
            TbPaused.Text = service.GetString("Doe_Mini_Paused", "已暂停");
            TbGroup.Text = service.GetString("Doe_Mini_Group", " 组 · ");
            TbWaitData.Text = service.GetString("Doe_Mini_WaitData", "等待因子数据");
            TbBest.Text = service.GetString("Doe_Mini_Best", "最优");
            BtnPause.Content = service.GetString("Doe_Mini_Pause", "暂停");
            BtnResume.Content = service.GetString("Doe_Mini_Resume", "恢复");
            BtnSuspend.Content = service.GetString("Doe_Mini_Suspend", "中止");
            BtnExpand.ToolTip = service.GetString("Doe_Mini_ExpandTip", "展开为大窗口");
        }
    }
}
using System.Windows;

namespace MaxChemical.Modules.Lab3D.Services;

public interface ILab3DWindowService
{
    bool IsOpen { get; }

    void ShowLab3D(Window? owner = null);

    /// <summary>打开或激活3D实验室,最大化窗口并保留右侧设备详情栏。</summary>
    void ShowLab3DFullScreen(Window? owner = null);

    /// <summary>退出全屏展示,恢复普通窗口大小。</summary>
    bool ExitLab3DFullScreen();

    bool CloseLab3D();
}

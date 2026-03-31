using MaxChemical.Core;
using Microsoft.Win32;
using System.Windows;

namespace MaxChemical.Infrastructure.Services;

public interface IDialogService
{
    bool ShowConfirmation(string message, string title = "确认");
    void ShowError(string message, string title = "错误");
    void ShowInfo(string message, string title = "信息");
    string? ShowSaveFileDialog(string filter = "所有文件|*.*", string? defaultExt = null);
    string? ShowOpenFileDialog(string filter = "所有文件|*.*");
    string? ShowSaveFileDialog(string filter, string? defaultExt, string? defaultFileName);


}
public interface IRegionSettingsDialogProvider
{
    bool? ShowDialog(RegionLayout currentLayout, Func<(double Width, double Height)> getCanvasSize, out RegionLayout result);
    void ShowWindow(RegionLayout currentLayout, Func<(double Width, double Height)> getDialogSize, Func<(double Width, double Height)> getCanvasSize, Action<RegionLayout> onCompleted = null);

}
public interface IProjectNameDialogProvider
{
    /// <summary>
    /// 显示项目名称输入对话框
    /// </summary>
    /// <param name="defaultName">默认项目名称</param>
    /// <param name="onCompleted">完成回调，参数为项目名称，null表示取消</param>
    void ShowDialog(string defaultName = null, Action<string> onCompleted = null);
}
public class DialogService : IDialogService
{
    public bool ShowConfirmation(string message, string title = "确认")
    {
        var result = MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Question);
        return result == MessageBoxResult.Yes;
    }

    public void ShowError(string message, string title = "错误")
    {
        MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);
    }

    public void ShowInfo(string message, string title = "信息")
    {
        MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);
    }

    public string? ShowSaveFileDialog(string filter = "所有文件|*.*", string? defaultExt = null)
    {
        var dialog = new SaveFileDialog
        {
            Filter = filter,
            DefaultExt = defaultExt
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public string? ShowOpenFileDialog(string filter = "所有文件|*.*")
    {
        var dialog = new OpenFileDialog
        {
            Filter = filter
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
    public string? ShowSaveFileDialog(string filter, string? defaultExt, string? defaultFileName)
    {
        var dialog = new SaveFileDialog
        {
            Filter = filter,
            DefaultExt = defaultExt,
            FileName = defaultFileName ?? ""
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
}
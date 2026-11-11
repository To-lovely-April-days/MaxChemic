using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MaxChemical.Modules.Lab3D.Views;

public sealed class Lab3DOverlayWindow : Window
{
    private readonly ScaleTransform _scale = new(1, 1);

    public Lab3DOverlayWindow()
    {
        Title = "3D实验室侧栏";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.Manual;
        AllowsTransparency = true;
        Background = null;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        SizeToContent = SizeToContent.Manual;
        UseLayoutRounding = true;
        FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI");
        FontSize = 12;
        Foreground = new SolidColorBrush(Color.FromRgb(0x1F, 0x23, 0x26));

        Root = new ContentControl
        {
            Background = null,
            Focusable = false,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch
        };
        PanelGrid = new Grid { Background = null, VerticalAlignment = VerticalAlignment.Stretch };
        PanelGrid.LayoutTransform = _scale;
        Root.Content = PanelGrid;
        Content = Root;
    }

    public ContentControl Root { get; }
    public Grid PanelGrid { get; }

    public void SetScale(double scale)
    {
        _scale.ScaleX = scale;
        _scale.ScaleY = scale;
    }

    /// <summary>仅主窗口关闭时置为 true；否则侧栏窗口自身的关闭请求（如 Alt+F4）会被取消并转交给主窗口。</summary>
    public bool AllowClose { get; set; }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!AllowClose)
        {
            e.Cancel = true;
            Dispatcher.BeginInvoke(new Action(() => Owner?.Close()));
            return;
        }
        base.OnClosing(e);
    }

    public bool IsOpen
    {
        get => IsVisible;
        set
        {
            if (value == IsOpen) return;
            if (value) Show();
            else Hide();
        }
    }
}

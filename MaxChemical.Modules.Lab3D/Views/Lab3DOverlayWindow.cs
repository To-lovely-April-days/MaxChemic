using System.Windows;
using System.Windows.Controls;

namespace MaxChemical.Modules.Lab3D.Views;

public sealed class Lab3DOverlayWindow : Window
{
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

        Root = new ContentControl
        {
            Background = null,
            Focusable = false,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch
        };
        PanelGrid = new Grid { Background = null, VerticalAlignment = VerticalAlignment.Stretch };
        PanelGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(240) });
        PanelGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        PanelGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(244) });
        Root.Content = PanelGrid;
        Content = Root;
    }

    public ContentControl Root { get; }
    public Grid PanelGrid { get; }

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

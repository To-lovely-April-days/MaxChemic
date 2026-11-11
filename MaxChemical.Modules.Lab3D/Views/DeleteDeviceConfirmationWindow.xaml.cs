using System.Windows;
using MaxChemical.Modules.Lab3D.Models;

namespace MaxChemical.Modules.Lab3D.Views;

public partial class DeleteDeviceConfirmationWindow : Window
{
    public DeleteDeviceConfirmationWindow(LabDevice device)
    {
        InitializeComponent();
        DeviceDisplayName = $"{device.Id} {device.Name}";
        DataContext = this;
    }

    public string DeviceDisplayName { get; }

    private void Confirm_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}

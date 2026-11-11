using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using MaxChemical.Modules.Lab3D.Models;

namespace MaxChemical.Modules.Lab3D.Views;

public partial class DeviceEditorWindow : Window, INotifyPropertyChanged
{
    private string _storageDirectory = string.Empty;

    public DeviceEditorWindow(string imagePath, string storageDirectory)
    {
        InitializeComponent();
        DeviceName = Path.GetFileNameWithoutExtension(imagePath);
        EnglishName = "CUSTOM EQUIPMENT";
        ModelHeightText = "2.0";
        StorageDirectory = storageDirectory;
        PreviewImage.Source = new BitmapImage(new Uri(imagePath, UriKind.Absolute));
        DataContext = this;
    }

    public DeviceEditorWindow(LabDevice device, string storageDirectory)
    {
        InitializeComponent();
        WindowTitle = "编辑设备";
        EditorTitle = "编辑设备";
        ConfirmButtonText = "保存修改";
        DeviceName = device.Name;
        EnglishName = device.EnglishName;
        ModelHeightText = device.ModelHeight.ToString("0.###", CultureInfo.InvariantCulture);
        StorageDirectory = storageDirectory;
        PreviewImage.Source = new BitmapImage(new Uri(device.ImagePath, UriKind.Absolute));
        DataContext = this;
    }

    public string WindowTitle { get; set; } = "新增设备";
    public string EditorTitle { get; set; } = "新增设备";
    public string ConfirmButtonText { get; set; } = "添加设备";
    public string DeviceName { get; set; }
    public string EnglishName { get; set; }
    public string ModelHeightText { get; set; }
    public string StorageDirectory
    {
        get => _storageDirectory;
        set
        {
            if (_storageDirectory == value) return;
            _storageDirectory = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StorageDirectory)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void AddDevice_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(DeviceName))
        {
            MessageBox.Show(this, "请输入设备名称。", WindowTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!double.TryParse(ModelHeightText, NumberStyles.Float, CultureInfo.InvariantCulture, out double height)
            || height is < 0.35 or > 4)
        {
            MessageBox.Show(this, "模型高度请输入 0.35–4 之间的数字。", WindowTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (string.IsNullOrWhiteSpace(StorageDirectory) || !Path.IsPathRooted(StorageDirectory))
        {
            MessageBox.Show(this, "设备库目录必须是有效的绝对路径。", WindowTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        try
        {
            Directory.CreateDirectory(StorageDirectory);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"无法创建设备库目录：{ex.Message}", WindowTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        DialogResult = true;
    }

    private void SelectFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择设备库目录",
            Multiselect = false
        };
        if (Directory.Exists(StorageDirectory)) dialog.InitialDirectory = StorageDirectory;
        if (dialog.ShowDialog(this) == true)
            StorageDirectory = dialog.FolderName;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => DragMove();
    private void Close_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}

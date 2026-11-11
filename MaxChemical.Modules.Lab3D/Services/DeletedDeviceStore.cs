using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace MaxChemical.Modules.Lab3D.Services;

public sealed class DeletedDeviceStore
{
    private readonly string _path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MaxChemical", "Lab3D", "deleted-devices.json");

    public HashSet<string> Load()
    {
        try
        {
            if (!File.Exists(_path)) return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var ids = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(_path)) ?? [];
            return new HashSet<string>(ids, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"读取 3D 已删除设备记录失败: {ex.Message}");
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    public void Add(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("设备编号不能为空。", nameof(id));
        var ids = Load();
        if (!ids.Add(id)) return;

        string? directory = Path.GetDirectoryName(_path);
        if (directory is not null) Directory.CreateDirectory(directory);
        string json = JsonSerializer.Serialize(ids.OrderBy(id => id, StringComparer.OrdinalIgnoreCase));
        string temp = _path + ".tmp";
        File.WriteAllText(temp, json);
        if (File.Exists(_path)) File.Replace(temp, _path, _path + ".bak", true);
        else File.Move(temp, _path);
    }
}

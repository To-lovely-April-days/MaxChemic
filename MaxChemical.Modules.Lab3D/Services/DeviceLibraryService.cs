using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Media.Imaging;
using MaxChemical.Modules.Lab3D.Models;

namespace MaxChemical.Modules.Lab3D.Services;

public sealed class DeviceLibraryService
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".webp", ".bmp" };
    private static readonly Dictionary<string, string> KnownImageProfiles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["highLowtemperature.png"] = "Temperature",
        ["ChilLer.png"] = "Chiller",
        ["MicroReactor.png"] = "Reactor",
        ["magneticstirrer.png"] = "Stirrer",
        ["ClimateChamber.png"] = "Climate",
        ["PenMeter_BLE.png"] = "Meter"
    };
    private static readonly Lazy<Dictionary<string, string>> KnownImageHashes = new(() =>
    {
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (fileName, kind) in KnownImageProfiles)
        {
            string path = Path.Combine(AppContext.BaseDirectory, "Lab3D", "wwwroot", "assets", "devices", fileName);
            if (File.Exists(path)) hashes[ComputeHash(path)] = kind;
        }
        return hashes;
    });
    private readonly string _settingsPath;
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public DeviceLibraryService()
    {
        string dataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MaxChemical", "Lab3D");
        _settingsPath = Path.Combine(dataDirectory, "device-library-settings.json");
        StorageDirectory = Path.Combine(dataDirectory, "DeviceLibrary");
        if (File.Exists(_settingsPath))
        {
            try
            {
                var settings = JsonSerializer.Deserialize<DeviceLibrarySettings>(File.ReadAllText(_settingsPath), _jsonOptions);
                if (!string.IsNullOrWhiteSpace(settings?.StorageDirectory)
                    && Path.IsPathRooted(settings.StorageDirectory))
                    StorageDirectory = settings.StorageDirectory;
            }
            catch
            {
            }
        }
        Directory.CreateDirectory(StorageDirectory);
    }

    public string StorageDirectory { get; private set; }
    public string? LastError { get; private set; }

    public List<DeviceLibraryEntry> LoadEntries()
    {
        LastError = null;
        string path = Path.Combine(StorageDirectory, "devices.json");
        if (!File.Exists(path)) return new List<DeviceLibraryEntry>();
        try
        {
            var entries = JsonSerializer.Deserialize<List<DeviceLibraryEntry>>(File.ReadAllText(path), _jsonOptions)
                ?? new List<DeviceLibraryEntry>();
            foreach (var entry in entries)
                entry.Kind = ResolveDeviceKind(entry.SourceImagePath, entry.Kind);
            return entries;
        }
        catch (Exception ex)
        {
            LastError = $"设备库配置读取失败：{ex.Message}";
            return new List<DeviceLibraryEntry>();
        }
    }

    public void SetStorageDirectory(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathRooted(directory))
            throw new ArgumentException("设备库目录必须是有效的绝对路径。", nameof(directory));
        directory = Path.GetFullPath(directory);
        if (SamePath(directory, StorageDirectory))
        {
            Directory.CreateDirectory(directory);
            SaveSettings();
            return;
        }

        string previousDirectory = StorageDirectory;
        var previousEntries = LoadEntries();
        ThrowIfReadFailed();
        StorageDirectory = directory;
        try
        {
            Directory.CreateDirectory(directory);
            var entries = LoadEntries();
            ThrowIfReadFailed();
            var usedIds = new HashSet<string>(entries.Select(entry => entry.Id), StringComparer.OrdinalIgnoreCase);
            var libraryIds = new HashSet<string>(entries.Select(entry => entry.LibraryId));
            foreach (var entry in previousEntries)
            {
                if (libraryIds.Contains(entry.LibraryId)) continue;
                if (!usedIds.Add(entry.Id))
                {
                    entry.Id = NextId(usedIds);
                    usedIds.Add(entry.Id);
                }
                CopyDeviceAssets(entry, directory);
                entries.Add(entry);
                libraryIds.Add(entry.LibraryId);
            }
            SaveEntries(entries);
            SaveSettings();
        }
        catch
        {
            StorageDirectory = previousDirectory;
            throw;
        }
    }

    public DeviceLibraryEntry AddDevice(
        string name,
        string englishName,
        double modelHeight,
        string sourceImagePath,
        string storageDirectory,
        double[] position,
        IEnumerable<string> existingIds,
        string? preferredId = null)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("设备名称不能为空。", nameof(name));
        if (modelHeight is < 0.35 or > 4) throw new ArgumentOutOfRangeException(nameof(modelHeight), "模型高度应在 0.35–4 米之间。");
        if (!File.Exists(sourceImagePath)) throw new FileNotFoundException("找不到上传的图片。", sourceImagePath);
        string extension = Path.GetExtension(sourceImagePath);
        if (!SupportedExtensions.Contains(extension)) throw new NotSupportedException("仅支持 PNG、JPG、WebP 和 BMP 图片。");
        if (new FileInfo(sourceImagePath).Length > 20 * 1024 * 1024)
            throw new InvalidDataException("图片大小不能超过 20 MB。");
        SetStorageDirectory(storageDirectory);
        var entries = LoadEntries();
        ThrowIfReadFailed();

        string id = preferredId ?? NextId(existingIds.Concat(entries.Select(entry => entry.Id)));
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("设备编号不能为空。", nameof(preferredId));
        if (entries.Any(entry => string.Equals(entry.Id, id, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException($"设备编号 {id} 已存在。");
        string libraryId = $"{id}-{Guid.NewGuid():N}";
        string directory = Path.Combine(StorageDirectory, libraryId);
        Directory.CreateDirectory(directory);
        try
        {
            string originalPath = Path.Combine(directory, $"original{extension.ToLowerInvariant()}");
            string frontPath = Path.Combine(directory, "front.png");
            string thumbnailPath = Path.Combine(directory, "thumbnail.png");
            File.Copy(sourceImagePath, originalPath, true);
            double aspect = SaveResizedImage(sourceImagePath, frontPath, 1600);
            SaveResizedImage(sourceImagePath, thumbnailPath, 320);
            var entry = new DeviceLibraryEntry
            {
                Id = id,
                LibraryId = libraryId,
                Name = name.Trim(),
                EnglishName = string.IsNullOrWhiteSpace(englishName) ? "CUSTOM EQUIPMENT" : englishName.Trim().ToUpperInvariant(),
                Kind = ResolveDeviceKind(sourceImagePath, "Custom"),
                ImagePath = frontPath,
                ThumbnailPath = thumbnailPath,
                SourceImagePath = originalPath,
                ModelHeight = modelHeight,
                ImageAspect = aspect,
                FocusDistance = Math.Clamp(modelHeight * 3, 4.5, 8.5),
                Position = position,
                Description = "通过设备库上传的自定义模型，未接入实时通讯。"
            };
            entries.Add(entry);
            SaveEntries(entries);
            return entry;
        }
        catch
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
            throw;
        }
    }

    public DeviceLibraryEntry UpdateDevice(
        string libraryId,
        string name,
        string englishName,
        double modelHeight,
        string storageDirectory)
    {
        if (string.IsNullOrWhiteSpace(libraryId)) throw new ArgumentException("设备库标识不能为空。", nameof(libraryId));
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("设备名称不能为空。", nameof(name));
        if (modelHeight is < 0.35 or > 4) throw new ArgumentOutOfRangeException(nameof(modelHeight), "模型高度应在 0.35–4 米之间。");

        SetStorageDirectory(storageDirectory);
        var entries = LoadEntries();
        ThrowIfReadFailed();
        var entry = entries.FirstOrDefault(item =>
            string.Equals(item.LibraryId, libraryId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException("设备库中找不到要编辑的设备。");

        entry.Name = name.Trim();
        entry.EnglishName = string.IsNullOrWhiteSpace(englishName)
            ? "CUSTOM EQUIPMENT"
            : englishName.Trim().ToUpperInvariant();
        entry.ModelHeight = modelHeight;
        entry.FocusDistance = Math.Clamp(modelHeight * 3, 4.5, 8.5);
        SaveEntries(entries);
        return entry;
    }

    public void DeleteDevice(string libraryId)
    {
        if (string.IsNullOrWhiteSpace(libraryId)) throw new ArgumentException("设备库标识不能为空。", nameof(libraryId));
        var entries = LoadEntries();
        ThrowIfReadFailed();
        var entry = entries.FirstOrDefault(item =>
            string.Equals(item.LibraryId, libraryId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException("设备库中找不到要删除的设备。");

        entries.Remove(entry);
        SaveEntries(entries);

        try
        {
            string directory = Path.Combine(StorageDirectory, libraryId);
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
        catch (Exception ex)
        {
            LastError = $"设备已从列表移除，但资源目录清理失败：{ex.Message}";
        }
    }

    private string NextId(IEnumerable<string> existingIds)
    {
        int next = existingIds
            .Select(id => id.Length > 1 && int.TryParse(id.AsSpan(1), out int value) ? value : 0)
            .DefaultIfEmpty(6)
            .Max() + 1;
        return $"D{next:00}";
    }

    private static string ComputeHash(string path)
    {
        using var input = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(input));
    }

    private static string ResolveDeviceKind(string sourceImagePath, string kind)
    {
        if (!string.Equals(kind, "Custom", StringComparison.OrdinalIgnoreCase) || !File.Exists(sourceImagePath))
            return kind;
        try
        {
            return KnownImageHashes.Value.TryGetValue(ComputeHash(sourceImagePath), out string? knownKind)
                ? knownKind
                : kind;
        }
        catch (IOException)
        {
            return kind;
        }
        catch (UnauthorizedAccessException)
        {
            return kind;
        }
    }

    private void ThrowIfReadFailed()
    {
        if (LastError is not null) throw new InvalidDataException(LastError);
    }

    private static void CopyDeviceAssets(DeviceLibraryEntry entry, string storageDirectory)
    {
        if (string.IsNullOrWhiteSpace(entry.LibraryId)
            || entry.LibraryId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || !string.Equals(Path.GetFileName(entry.LibraryId), entry.LibraryId, StringComparison.Ordinal))
            throw new InvalidDataException("设备库包含无效的设备目录标识。");
        string directory = Path.Combine(storageDirectory, entry.LibraryId);
        Directory.CreateDirectory(directory);
        entry.ImagePath = CopyLibraryFile(entry.ImagePath, directory);
        entry.ThumbnailPath = CopyLibraryFile(entry.ThumbnailPath, directory);
        entry.SourceImagePath = CopyLibraryFile(entry.SourceImagePath, directory);
    }

    private static string CopyLibraryFile(string sourcePath, string directory)
    {
        if (!File.Exists(sourcePath)) throw new FileNotFoundException("设备库中的图片文件不存在，无法迁移。", sourcePath);
        string destinationPath = Path.Combine(directory, Path.GetFileName(sourcePath));
        if (!SamePath(sourcePath, destinationPath)) File.Copy(sourcePath, destinationPath, true);
        return destinationPath;
    }

    private static bool SamePath(string first, string second) => string.Equals(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)),
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(second)),
        StringComparison.OrdinalIgnoreCase);

    private static double SaveResizedImage(string sourcePath, string destinationPath, int decodeWidth)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.DecodePixelWidth = decodeWidth;
        image.UriSource = new Uri(sourcePath, UriKind.Absolute);
        image.EndInit();
        if (image.PixelWidth == 0 || image.PixelHeight == 0) throw new InvalidDataException("无法读取上传图片的尺寸。");
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(destinationPath);
        encoder.Save(stream);
        return (double)image.PixelWidth / image.PixelHeight;
    }

    private void SaveEntries(IEnumerable<DeviceLibraryEntry> entries)
    {
        string json = JsonSerializer.Serialize(entries, _jsonOptions);
        WriteAtomic(Path.Combine(StorageDirectory, "devices.json"), json);
    }

    private void SaveSettings()
    {
        string json = JsonSerializer.Serialize(new DeviceLibrarySettings { StorageDirectory = StorageDirectory }, _jsonOptions);
        WriteAtomic(_settingsPath, json);
    }

    private static void WriteAtomic(string path, string content)
    {
        string? directory = Path.GetDirectoryName(path);
        if (directory is not null) Directory.CreateDirectory(directory);
        string temp = path + ".tmp";
        string backup = path + ".bak";
        File.WriteAllText(temp, content);
        if (File.Exists(path)) File.Replace(temp, path, backup, true);
        else File.Move(temp, path);
    }

    private sealed class DeviceLibrarySettings
    {
        public string StorageDirectory { get; set; } = string.Empty;
    }
}

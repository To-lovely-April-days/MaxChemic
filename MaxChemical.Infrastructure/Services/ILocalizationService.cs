using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using MaxChemical.Logging;

namespace MaxChemical.Infrastructure.Services;

public interface ILocalizationService
{
    string GetString(string key);
    void SetCulture(CultureInfo culture);
    CultureInfo CurrentCulture { get; }

    event EventHandler<CultureInfo>? CultureChanged;
}

/// <summary>
/// 新增
/// </summary>
public static class LocalizationExtensions
{
    /// <summary>
    /// 若本地化文本获取失败，则返回默认文本
    /// </summary>
    /// <param name="service"></param>
    /// <param name="key"></param>
    /// <param name="defaultTxt"></param>
    /// <returns></returns>
    public static string GetString(this ILocalizationService service, string key, string defaultTxt)
    {
        ArgumentNullException.ThrowIfNull(service);

        if (string.IsNullOrWhiteSpace(key))
        {
            return defaultTxt;
        }

        string txt = service.GetString(key);
        if (string.IsNullOrWhiteSpace(txt) || txt == key)
        {
            return defaultTxt;
        }
        return txt;
    }
}


public class LocalizationService : ILocalizationService
{
    private CultureInfo _currentCulture = new("zh-CN");
    private readonly Dictionary<string, Dictionary<string, string>> _translations;
    private readonly string _translationsPath;
    private readonly ILogService _logger;
    public LocalizationService(string translationsPath = "Resources/Translations")
    {
        _logger = LogManager.GetLogger<LocalizationService>();
        _translationsPath = translationsPath;
        _translations = LoadTranslations();

        // 监控文件变化，支持热更新
        WatchTranslationFiles();
    }

    public CultureInfo CurrentCulture
    {
        get => _currentCulture;
        private set
        {
            if (!_currentCulture.Equals(value))
            {
                _currentCulture = value;
                CultureChanged?.Invoke(this, value);
            }
        }
    }

    public event EventHandler<CultureInfo>? CultureChanged;

    public string GetString(string key)
    {
        if (_translations.TryGetValue(CurrentCulture.Name, out var cultureTranslations) &&
            cultureTranslations.TryGetValue(key, out var translation))
        {
            return translation;
        }

        // 回退到中文
        if (_translations.TryGetValue("zh-CN", out var fallbackTranslations) &&
            fallbackTranslations.TryGetValue(key, out var fallbackTranslation))
        {
            return fallbackTranslation;
        }

        return key;
    }

    public void SetCulture(CultureInfo culture)
    {
        CurrentCulture = culture;
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
    }

    // 重新加载翻译文件（支持热更新）
    public void ReloadTranslations()
    {
        var newTranslations = LoadTranslations();
        _translations.Clear();
        foreach (var kvp in newTranslations)
        {
            _translations[kvp.Key] = kvp.Value;
        }

        // 通知界面刷新
        CultureChanged?.Invoke(this, CurrentCulture);
    }

    private Dictionary<string, Dictionary<string, string>> LoadTranslations()
    {
        var translations = new Dictionary<string, Dictionary<string, string>>();

        if (!Directory.Exists(_translationsPath))
        {
            Directory.CreateDirectory(_translationsPath);
            return translations;
        }

        var jsonFiles = Directory.GetFiles(_translationsPath, "*.json");

        foreach (var file in jsonFiles)
        {
            try
            {
                var culture = Path.GetFileNameWithoutExtension(file);
                var jsonContent = File.ReadAllText(file);
                var cultureTranslations = JsonSerializer.Deserialize<Dictionary<string, string>>(jsonContent);

                if (cultureTranslations != null)
                {
                    translations[culture] = cultureTranslations;
                }
            }
            catch (Exception ex)
            {
                _logger.LogCritical($"加载翻译文件 {file} 失败: {ex.Message}");
            }
        }

        return translations;
    }

    private void WatchTranslationFiles()
    {
        if (!Directory.Exists(_translationsPath)) return;

        var watcher = new FileSystemWatcher(_translationsPath, "*.json")
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
            EnableRaisingEvents = true
        };

        watcher.Changed += (sender, e) => ReloadTranslations();
        watcher.Created += (sender, e) => ReloadTranslations();
        watcher.Deleted += (sender, e) => ReloadTranslations();
    }
}
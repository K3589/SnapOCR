using System.Text.Json;

namespace SnapOCR.Settings;

/// <summary>
/// 设置持久化：%LOCALAPPDATA%\SnapOCR\settings.json。
/// 只用 System.Text.Json，不引任何配置框架。
/// </summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public SettingsStore(string? directory = null)
    {
        var baseDirectory = directory;
        if (string.IsNullOrWhiteSpace(baseDirectory))
        {
            baseDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SnapOCR");
        }

        Directory.CreateDirectory(baseDirectory);
        FilePath = Path.Combine(baseDirectory, "settings.json");
    }

    public string FilePath { get; }

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return new AppSettings();
            }

            var json = File.ReadAllText(FilePath);
            if (string.IsNullOrWhiteSpace(json))
            {
                return new AppSettings();
            }

            return JsonSerializer.Deserialize<AppSettings>(json, SerializerOptions) ?? new AppSettings();
        }
        catch (Exception)
        {
            // 配置损坏不能让程序起不来，直接回到默认值。
            return new AppSettings();
        }
    }

    public bool Save(AppSettings settings, out string? error)
    {
        error = null;
        try
        {
            var json = JsonSerializer.Serialize(settings, SerializerOptions);
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, json);

            // 原子替换，避免写一半断电留下半个 JSON。
            File.Move(temp, FilePath, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}

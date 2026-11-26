using SnapOCR.Settings;
using Xunit;

namespace SnapOCR.Tests;

/// <summary>设置持久化与损坏降级测试。</summary>
public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _tempDir;

    public SettingsStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "SnapOCR.Tests." + Guid.NewGuid().ToString("N"));
    }

    [Fact]
    public void Load_WhenMissing_ReturnsDefaults()
    {
        var store = new SettingsStore(_tempDir);
        var settings = store.Load();

        Assert.Equal("Win+Shift+T", settings.Hotkey);
        Assert.Equal(string.Empty, settings.OcrLanguage);
        Assert.True(settings.AutoCopy);
        Assert.True(settings.ShowResultToast);
        Assert.False(settings.RunAtStartup);
        Assert.False(settings.WatchClipboard);
    }

    [Fact]
    public void SaveAndLoad_RoundTripsAllFieldsWithoutLeavingTempFile()
    {
        var store = new SettingsStore(_tempDir);
        var original = new AppSettings
        {
            Hotkey = "Ctrl+Alt+O",
            OcrLanguage = "zh-Hans-CN",
            AutoCopy = false,
            ShowResultToast = false,
            RunAtStartup = true,
            WatchClipboard = true,
        };

        Assert.True(store.Save(original, out var error));
        Assert.Null(error);
        Assert.True(File.Exists(store.FilePath));
        Assert.False(File.Exists(store.FilePath + ".tmp"));

        var loaded = store.Load();
        Assert.Equal(original.Hotkey, loaded.Hotkey);
        Assert.Equal(original.OcrLanguage, loaded.OcrLanguage);
        Assert.Equal(original.AutoCopy, loaded.AutoCopy);
        Assert.Equal(original.ShowResultToast, loaded.ShowResultToast);
        Assert.Equal(original.RunAtStartup, loaded.RunAtStartup);
        Assert.Equal(original.WatchClipboard, loaded.WatchClipboard);
    }

    [Fact]
    public void Load_WhenJsonCorruptedOrEmpty_FallsBackToDefaults()
    {
        var store = new SettingsStore(_tempDir);

        File.WriteAllText(store.FilePath, "{ not valid json");
        var corrupted = store.Load();
        Assert.Equal("Win+Shift+T", corrupted.Hotkey);
        Assert.True(corrupted.AutoCopy);

        File.WriteAllText(store.FilePath, "   ");
        var empty = store.Load();
        Assert.Equal("Win+Shift+T", empty.Hotkey);
    }

    [Fact]
    public void ResolveHotkey_InvalidStringFallsBackToDefault()
    {
        var settings = new AppSettings { Hotkey = "InvalidOnlyKey" };
        Assert.Equal(HotkeyDefinition.Default, settings.ResolveHotkey());
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch (Exception)
        {
        }
    }
}

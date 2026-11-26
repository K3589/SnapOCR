namespace SnapOCR.Settings;

/// <summary>
/// 本地设置。核心只有 5 项：热键、OCR 语言、自动复制、结果提示、开机自启；
/// 第 6 项 WatchClipboard 是 P6「跟随系统截图」的开关，默认关闭。
/// </summary>
public sealed class AppSettings
{
    /// <summary>热键字符串，例如 "Win+Shift+T"。</summary>
    public string Hotkey { get; set; } = "Win+Shift+T";

    /// <summary>OCR 语言 Tag，例如 "zh-Hans-CN"；空表示跟随系统。</summary>
    public string OcrLanguage { get; set; } = string.Empty;

    /// <summary>识别完成后自动写入剪贴板。</summary>
    public bool AutoCopy { get; set; } = true;

    /// <summary>复制成功后显示 1 秒轻提示。</summary>
    public bool ShowResultToast { get; set; } = true;

    /// <summary>开机自动启动（由 StartupTask 控制）。</summary>
    public bool RunAtStartup { get; set; }

    /// <summary>P6：监听剪贴板，Win+Shift+S 截完图自动取字。</summary>
    public bool WatchClipboard { get; set; }

    public AppSettings Clone() => (AppSettings)MemberwiseClone();

    /// <summary>解析热键，非法值回落到默认，保证程序永远有一个能用的热键。</summary>
    public HotkeyDefinition ResolveHotkey()
        => HotkeyDefinition.TryParse(Hotkey, out var definition) ? definition : HotkeyDefinition.Default;
}

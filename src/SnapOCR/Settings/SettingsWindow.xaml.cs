using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using SnapOCR.Capture;
using SnapOCR.Interop;
using SnapOCR.Ocr;
using Windows.Graphics;
using Windows.System;

namespace SnapOCR.Settings;

/// <summary>保存设置时带上完整的新设置对象。</summary>
public sealed class SettingsSavedEventArgs(AppSettings settings) : EventArgs
{
    public AppSettings Settings { get; } = settings;
}

/// <summary>
/// 设置窗口。5 个核心项 + P6 的「跟随系统截图」开关。
/// 不引 MVVM，直接在 code-behind 里读写控件。
/// </summary>
public sealed partial class SettingsWindow : Window
{
    /// <summary>安装简体中文 OCR 语言包的官方命令。</summary>
    public const string ChineseLanguagePackCommand =
        "Add-WindowsCapability -Online -Name Language.OCR~~~zh-CN~0.0.1.0";

    private readonly IntPtr _hwnd;
    private readonly AppSettings _working;
    private readonly IReadOnlyList<OcrLanguageInfo> _languages;
    private readonly KeyEventHandler _recordingHandler;
    private HotkeyDefinition _hotkey;
    private bool _recording;

    public SettingsWindow(AppSettings current, IReadOnlyList<OcrLanguageInfo> languages)
    {
        InitializeComponent();

        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        _working = current.Clone();
        _languages = languages;
        _hotkey = _working.ResolveHotkey();
        _recordingHandler = OnRecordingKeyDown;

        var scale = DpiHelper.ScaleFactor(DpiHelper.GetDpiForWindowSafe(_hwnd));
        AppWindow.Resize(new SizeInt32((int)Math.Round(520 * scale), (int)Math.Round(640 * scale)));

        try
        {
            var icon = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
            if (File.Exists(icon))
            {
                AppWindow.SetIcon(icon);
            }
        }
        catch (Exception)
        {
        }

        try
        {
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);
        }
        catch (Exception)
        {
        }

        try
        {
            SystemBackdrop = new MicaBackdrop();
        }
        catch (Exception)
        {
        }

        InitLanguages();
        ApplyToControls();
        UpdateAvailability();
    }

    /// <summary>用户点了保存。</summary>
    public event EventHandler<SettingsSavedEventArgs>? Saved;

    private void InitLanguages()
    {
        LanguageCombo.Items.Clear();
        LanguageCombo.Items.Add(new ComboBoxItem { Content = "跟随系统", Tag = string.Empty });

        foreach (var language in _languages)
        {
            LanguageCombo.Items.Add(new ComboBoxItem
            {
                Content = $"{language.DisplayName}（{language.Tag}）",
                Tag = language.Tag,
            });
        }
    }

    private void ApplyToControls()
    {
        HotkeyButton.Content = _hotkey.ToDisplayString();
        AutoCopySwitch.IsOn = _working.AutoCopy;
        ToastSwitch.IsOn = _working.ShowResultToast;
        StartupSwitch.IsOn = _working.RunAtStartup;
        ClipboardSwitch.IsOn = _working.WatchClipboard;

        var selectedIndex = 0;
        for (var i = 0; i < LanguageCombo.Items.Count; i++)
        {
            if (LanguageCombo.Items[i] is ComboBoxItem item
                && string.Equals(item.Tag as string, _working.OcrLanguage, StringComparison.OrdinalIgnoreCase))
            {
                selectedIndex = i;
                break;
            }
        }

        LanguageCombo.SelectedIndex = selectedIndex;
    }

    private void UpdateAvailability()
    {
        if (_languages.Count == 0)
        {
            LanguageInfoBar.IsOpen = true;
            LanguageInfoBar.Title = "系统没有任何 OCR 语言包";
            LanguageInfoBar.Message =
                $"SnapOCR 依赖系统内置识别引擎。请以管理员身份运行 PowerShell 安装中文语言包：" +
                $"\n{ChineseLanguagePackCommand}";
            StatusText.Text = "未检测到可用的 OCR 语言，识别会失败。";
            return;
        }

        var hasChinese = _languages.Any(l =>
            l.Tag.StartsWith("zh", StringComparison.OrdinalIgnoreCase));

        if (!hasChinese)
        {
            LanguageInfoBar.IsOpen = true;
            LanguageInfoBar.Title = "缺少中文 OCR 语言包";
            LanguageInfoBar.Message =
                $"当前只有：{string.Join("、", _languages.Select(l => l.Tag))}。识别中文需要安装简体中文语言包：" +
                $"\n{ChineseLanguagePackCommand}";
        }
        else
        {
            LanguageInfoBar.IsOpen = false;
        }

        OcrLanguageHint.Text = $"已安装 {_languages.Count} 种识别语言";
    }

    // ---------- 热键录制 ----------

    private void OnHotkeyButtonClick(object sender, RoutedEventArgs e)
    {
        if (_recording)
        {
            StopRecording(applied: false);
            return;
        }

        _recording = true;
        HotkeyButton.Content = "按下新的组合键…";
        StatusText.Text = "需要至少一个修饰键（Ctrl / Alt / Shift / Win）。按 Esc 取消录制。";
        Root.AddHandler(UIElement.PreviewKeyDownEvent, _recordingHandler, handledEventsToo: true);
    }

    private void OnRecordingKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!_recording)
        {
            return;
        }

        var key = e.Key;

        if (IsModifierKey(key))
        {
            var preview = BuildModifierPreview();
            HotkeyButton.Content = preview.Length == 0 ? "按下新的组合键…" : preview;
            e.Handled = true;
            return;
        }

        if (key == VirtualKey.Escape)
        {
            StopRecording(applied: false);
            e.Handled = true;
            return;
        }

        var modifiers = ReadModifiers();
        if (modifiers == HotkeyModifiers.None)
        {
            StatusText.Text = "至少要按住 Ctrl / Alt / Shift / Win 其中一个，单键会抢走正常输入。";
            e.Handled = true;
            return;
        }

        var virtualKey = (uint)key;
        if (!IsAcceptableKey(key))
        {
            StatusText.Text = "这个键不适合做全局热键，换一个。";
            e.Handled = true;
            return;
        }

        _hotkey = new HotkeyDefinition(modifiers, virtualKey);
        StopRecording(applied: true);
        e.Handled = true;
    }

    private void StopRecording(bool applied)
    {
        _recording = false;
        Root.RemoveHandler(UIElement.PreviewKeyDownEvent, _recordingHandler);
        HotkeyButton.Content = _hotkey.ToDisplayString();
        StatusText.Text = applied ? "热键已更新，点击「保存」后生效。" : string.Empty;
    }

    private static bool IsModifierKey(VirtualKey key) => key is
        VirtualKey.Shift or VirtualKey.LeftShift or VirtualKey.RightShift
        or VirtualKey.Control or VirtualKey.LeftControl or VirtualKey.RightControl
        or VirtualKey.Menu or VirtualKey.LeftMenu or VirtualKey.RightMenu
        or VirtualKey.LeftWindows or VirtualKey.RightWindows;

    private static bool IsAcceptableKey(VirtualKey key) => key is not
        (VirtualKey.Tab or VirtualKey.Enter or VirtualKey.Escape or VirtualKey.None);

    private static HotkeyModifiers ReadModifiers()
    {
        var modifiers = HotkeyModifiers.None;

        if (IsDown(0x10) || IsDown(0xA0) || IsDown(0xA1))
        {
            modifiers |= HotkeyModifiers.Shift;
        }

        if (IsDown(0x11) || IsDown(0xA2) || IsDown(0xA3))
        {
            modifiers |= HotkeyModifiers.Control;
        }

        if (IsDown(0x12) || IsDown(0xA4) || IsDown(0xA5))
        {
            modifiers |= HotkeyModifiers.Alt;
        }

        if (IsDown(0x5B) || IsDown(0x5C))
        {
            modifiers |= HotkeyModifiers.Win;
        }

        return modifiers;
    }

    private static bool IsDown(int virtualKey) => (NativeMethods.GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    private static string BuildModifierPreview()
    {
        var modifiers = ReadModifiers();
        var parts = new List<string>(4);
        if (modifiers.HasFlag(HotkeyModifiers.Win))
        {
            parts.Add("Win");
        }

        if (modifiers.HasFlag(HotkeyModifiers.Control))
        {
            parts.Add("Ctrl");
        }

        if (modifiers.HasFlag(HotkeyModifiers.Alt))
        {
            parts.Add("Alt");
        }

        if (modifiers.HasFlag(HotkeyModifiers.Shift))
        {
            parts.Add("Shift");
        }

        return parts.Count == 0 ? string.Empty : string.Join(" + ", parts) + " + …";
    }

    // ---------- 保存 / 取消 ----------

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (_recording)
        {
            StatusText.Text = "请先按 Esc 结束热键录制。";
            return;
        }

        _working.Hotkey = _hotkey.ToString();
        _working.OcrLanguage = (LanguageCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? string.Empty;
        _working.AutoCopy = AutoCopySwitch.IsOn;
        _working.ShowResultToast = ToastSwitch.IsOn;
        _working.RunAtStartup = StartupSwitch.IsOn;
        _working.WatchClipboard = ClipboardSwitch.IsOn;

        Saved?.Invoke(this, new SettingsSavedEventArgs(_working.Clone()));
        Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => Close();

    private async void OnCopyLanguageCommandClick(object sender, RoutedEventArgs e)
    {
        var copied = await ClipboardHelper.TrySetTextAsync(ChineseLanguagePackCommand, _hwnd);
        StatusText.Text = copied
            ? "命令已复制。请以管理员身份打开 PowerShell 粘贴运行。"
            : "复制失败，请手动选中下面的命令复制：" + ChineseLanguagePackCommand;
    }
}

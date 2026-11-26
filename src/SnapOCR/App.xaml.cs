using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using SnapOCR.Capture;
using SnapOCR.Interop;
using SnapOCR.Ocr;
using SnapOCR.Settings;
using SnapOCR.UI;

namespace SnapOCR;

public partial class App : Application
{
    private MainWindow? _host;
    private TrayIcon? _tray;
    private HotkeyHook? _hotkey;
    private ClipboardWatcher? _clipboardWatcher;
    private OverlayManager? _overlay;
    private ResultToast? _toast;
    private ScreenCapture? _capture;
    private WindowsOcrService? _ocr;
    private SettingsWindow? _settingsWindow;
    private SettingsStore? _store;
    private AppSettings _settings = new();
    private IReadOnlyList<CapturedFrame> _currentFrames = Array.Empty<CapturedFrame>();
    private bool _busy;
    private bool _startupHintShown;

    public App()
    {
        Instance = this;
        InitializeComponent();
    }

    /// <summary>全局单例引用（WinUI 的 Application.Current 需要强转，这里给个直白入口）。</summary>
    public static App Instance { get; private set; } = null!;

    public AppSettings Settings => _settings;

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        UnhandledException += (_, e) =>
        {
            AppLog.Write($"未处理异常：{e.Exception}");
            e.Handled = true;
        };

        _store = new SettingsStore();
        _settings = _store.Load();
        AppLog.Reset();
        AppLog.Write($"启动，热键配置={_settings.Hotkey}，语言={_settings.OcrLanguage}，自动复制={_settings.AutoCopy}");

        _host = new MainWindow();
        var hwnd = _host.Handle;

        _ocr = new WindowsOcrService();
        _capture = new ScreenCapture();
        _overlay = new OverlayManager();
        _toast = new ResultToast();

        _overlay.SelectionCompleted += OnSelectionCompleted;
        _overlay.Cancelled += OnSelectionCancelled;

        _tray = new TrayIcon(hwnd);
        _tray.CaptureRequested += (_, _) => _ = BeginCaptureAsync();
        _tray.SettingsRequested += (_, _) => ShowSettings();
        _tray.StartupToggleRequested += (_, enable) => _ = SetStartupAsync(enable);
        _tray.ExitRequested += (_, _) => ExitApp();

        _hotkey = new HotkeyHook(hwnd);
        _hotkey.Pressed += (_, _) => _ = BeginCaptureAsync();

        _clipboardWatcher = new ClipboardWatcher(hwnd);
        _clipboardWatcher.ImageAvailable += (_, _) => _ = CaptureFromClipboardAsync();

        _tray.Show("SnapOCR · 框选即取字");
        AppLog.Write($"托盘图标=Shell_NotifyIcon({_tray.IsVisible})");
        ApplyHotkey(_settings.ResolveHotkey(), announce: true);
        ApplyClipboardWatcher();
        _ = SyncStartupStateAsync();

        // 预创建所有屏幕的覆盖层窗口：第一次热键才不用等窗口创建。
        _overlay.Prepare();

        // 预抓一次，建好 GDI 的 DIB 缓存。
        _ = Task.Run(() => _capture.Warmup());

        // OCR 引擎预热（放在后台，不阻塞托盘出现）。
        _ = WarmupOcrAsync();

        // 第二次启动会激活已有实例，这里把它变成一次「立即识别」。
        try
        {
            AppInstance.FindOrRegisterForKey(Program.InstanceKey).Activated += OnInstanceActivated;
        }
        catch (Exception)
        {
            // 未打包运行时可能不支持，忽略。
        }
    }

    // ---------------- 主流程 ----------------

    private async Task BeginCaptureAsync()
    {
        AppLog.Write($"BeginCaptureAsync busy={_busy}");

        if (_busy)
        {
            return;
        }

        _busy = true;
        try
        {
            var stopwatch = Stopwatch.StartNew();
            var frames = _capture!.CaptureAll();
            var captureMs = stopwatch.ElapsedMilliseconds;

            if (frames.Count == 0)
            {
                _busy = false;
                Notify("读取屏幕失败。", isError: true);
                return;
            }

            _currentFrames = frames;

            var shown = await _overlay!.ShowAsync(frames);
            if (!shown)
            {
                _busy = false;
                Notify("无法创建框选层。", isError: true);
                return;
            }

            AppLog.Write($"热键 → 覆盖层可见：{stopwatch.ElapsedMilliseconds} ms（其中抓屏 {captureMs} ms，{frames.Count} 屏）");
            LogMemory("抓屏后");
        }
        catch (Exception ex)
        {
            _busy = false;
            Notify("抓屏失败：" + ex.Message, isError: true);
        }
    }

    private async void OnSelectionCompleted(object? sender, PixelRect rect)
    {
        var stopwatch = Stopwatch.StartNew();
        AppLog.Write($"框选完成 rect={rect.Left},{rect.Top} {rect.Width}x{rect.Height}，屏幕数={_currentFrames.Count}");
        try
        {
            var frame = FindBestFrame(rect);
            if (frame is null)
            {
                Notify("选区不在任何屏幕内。", isError: true);
                return;
            }

            var cropped = frame.Crop(rect);
            AppLog.Write($"裁剪得到 {cropped.Width}x{cropped.Height}，monitor={frame.Monitor.DeviceName} {frame.Monitor.Left},{frame.Monitor.Top}");
            if (cropped.IsEmpty)
            {
                Notify("选区太小。", isError: true);
                return;
            }

            var outcome = await _ocr!.RecognizeAsync(
                new OcrRequest(cropped.Bgra, cropped.Width, cropped.Height, cropped.Stride));
            AppLog.Write($"OCR success={outcome.Success} lines={outcome.LineCount} chars={outcome.Text.Length} error={outcome.Error}");

            if (!outcome.Success)
            {
                Notify("识别失败：" + outcome.Error, isError: true);
                return;
            }

            if (string.IsNullOrWhiteSpace(outcome.Text))
            {
                Notify("这一块没识别到文字。", isError: true);
                return;
            }

            if (_settings.AutoCopy)
            {
                var copied = await ClipboardHelper.TrySetTextAsync(outcome.Text, _host!.Handle);
                AppLog.Write($"剪贴板写入={copied}，字符数={outcome.Text.Length}");
                if (!copied)
                {
                    Notify("写入剪贴板失败，文本没有丢，可重试一次。", isError: true);
                    return;
                }

                // 自己写的剪贴板变化不要被 P6 当成用户截图。
                _clipboardWatcher?.SuppressCurrentSequence();

                if (_settings.ShowResultToast)
                {
                    _toast!.ShowToast($"已复制 {outcome.Text.Length} 个字符");
                }

                AppLog.Write($"松手 → 剪贴板：{stopwatch.ElapsedMilliseconds} ms（{outcome.LineCount} 行）");
            }
            else if (_settings.ShowResultToast)
            {
                _toast!.ShowToast($"已识别 {outcome.Text.Length} 个字符（未复制）");
            }
        }
        catch (Exception ex)
        {
            Notify("处理失败：" + ex.Message, isError: true);
        }
        finally
        {
            _busy = false;
            _currentFrames = Array.Empty<CapturedFrame>();
            LogMemory("识别结束");
        }
    }

    private static void LogMemory(string stage)
    {
        var managed = GC.GetTotalMemory(false) / 1024 / 1024;
        var workingSet = Environment.WorkingSet / 1024 / 1024;
        AppLog.Write($"内存[{stage}] 托管={managed}MB 工作集={workingSet}MB");
    }

    private void OnSelectionCancelled(object? sender, EventArgs e)
    {
        AppLog.Write("框选被取消");
        _busy = false;
        _currentFrames = Array.Empty<CapturedFrame>();
    }

    /// <summary>跨屏框选时取相交面积最大的那块屏幕，保证裁出来的像素坐标系正确。</summary>
    private CapturedFrame? FindBestFrame(PixelRect rect)
    {
        CapturedFrame? best = null;
        var bestArea = 0;

        foreach (var frame in _currentFrames)
        {
            var overlap = frame.Monitor.Intersect(rect);
            var area = overlap.IsEmpty ? 0 : overlap.Width * overlap.Height;
            if (area > bestArea)
            {
                bestArea = area;
                best = frame;
            }
        }

        return best;
    }

    // ---------------- P6：跟随系统截图 ----------------

    private async Task CaptureFromClipboardAsync()
    {
        if (_busy || !_settings.WatchClipboard)
        {
            return;
        }

        try
        {
            AppLog.Write("收到剪贴板变化，检查是否图片");
            var content = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
            var hasBitmap = content.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Bitmap);
            AppLog.Write($"剪贴板含 Bitmap 格式={hasBitmap}，格式={string.Join(",", content.AvailableFormats)}");

            OcrRequest request;

            if (hasBitmap)
            {
                _busy = true;
                var reference = await content.GetBitmapAsync();
                using var stream = await reference.OpenReadAsync();
                var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream);
                using var bitmap = await decoder.GetSoftwareBitmapAsync(
                    Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
                    Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied);

                request = ToOcrRequest(bitmap);
            }
            else
            {
                // 有些程序只写裸 CF_DIB，WinRT 看不到，走 Win32 兜底。
                var dib = ClipboardImageReader.TryReadDib(_host!.Handle);
                if (dib is null)
                {
                    return;
                }

                _busy = true;
                var (pixels, width, height) = dib.Value;
                request = new OcrRequest(pixels, width, height, width * 4);
            }

            var outcome = await _ocr!.RecognizeAsync(request);
            if (!outcome.Success || string.IsNullOrWhiteSpace(outcome.Text))
            {
                Notify("剪贴板图片里没识别到文字。", isError: true);
                return;
            }

            var copied = await ClipboardHelper.TrySetTextAsync(outcome.Text, _host!.Handle);
            if (copied)
            {
                _clipboardWatcher?.SuppressCurrentSequence();
                if (_settings.ShowResultToast)
                {
                    _toast!.ShowToast($"已复制 {outcome.Text.Length} 个字符");
                }

                AppLog.Write($"剪贴板取字成功，{outcome.Text.Length} 字");
            }
        }
        catch (Exception ex)
        {
            AppLog.Write($"剪贴板取字失败：{ex.GetType().Name} hresult=0x{ex.HResult:X8} msg={ex.Message}");
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>SoftwareBitmap → 紧排 BGRA，复用同一条识别链路。</summary>
    private static OcrRequest ToOcrRequest(Windows.Graphics.Imaging.SoftwareBitmap bitmap)
    {
        var stride = bitmap.PixelWidth * 4;
        var buffer = new Windows.Storage.Streams.Buffer((uint)(stride * bitmap.PixelHeight));
        bitmap.CopyToBuffer(buffer);

        var pixels = new byte[stride * bitmap.PixelHeight];
        using (var reader = Windows.Storage.Streams.DataReader.FromBuffer(buffer))
        {
            reader.ReadBytes(pixels);
        }

        return new OcrRequest(pixels, bitmap.PixelWidth, bitmap.PixelHeight, stride);
    }

    private void ApplyClipboardWatcher()
    {
        if (_clipboardWatcher is null)
        {
            return;
        }

        if (_settings.WatchClipboard)
        {
            _clipboardWatcher.Start();
        }
        else
        {
            _clipboardWatcher.Stop();
        }
    }

    // ---------------- 设置 ----------------

    private void ShowSettings()
    {
        if (_settingsWindow is not null)
        {
            _settingsWindow.Activate();
            return;
        }

        var window = new SettingsWindow(_settings, _ocr!.AvailableLanguages);
        window.Saved += OnSettingsSaved;
        window.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow = window;
        window.Activate();
    }

    private async void OnSettingsSaved(object? sender, SettingsSavedEventArgs e)
    {
        _settings = e.Settings;

        if (!_store!.Save(_settings, out var error))
        {
            Notify("设置保存失败：" + error, isError: true);
        }

        ApplyHotkey(_settings.ResolveHotkey(), announce: true);
        ApplyClipboardWatcher();
        await ApplyStartupAsync(_settings.RunAtStartup);

        var tag = ResolveLanguageTag();
        var ready = await _ocr!.WarmupAsync(tag);
        if (!ready)
        {
            Notify($"OCR 引擎不可用：{_ocr.AvailabilityMessage}", isError: true);
        }

        UpdateTooltip();
        SyncTrayStartupFlag();
    }

    private void ApplyHotkey(HotkeyDefinition definition, bool announce)
    {
        var result = _hotkey!.Register(definition, HotkeyDefinition.Fallback);

        // 降级成功后要把实际生效的热键写回设置，否则下次启动又会撞一次。
        _settings.Hotkey = result.Applied.ToString();
        _store!.Save(_settings, out _);

        if (_tray is not null)
        {
            _tray.CaptureHotkeyText = result.Applied.ToDisplayString();
        }

        UpdateTooltip();

        if (!result.Success)
        {
            Notify(result.Message ?? "热键注册失败。", isError: true);
        }
        else if (announce && !string.IsNullOrEmpty(result.Message))
        {
            Notify(result.Message, isError: false);
        }
    }

    private async Task SyncStartupStateAsync()
    {
        var enabled = await StartupHelper.IsEnabledAsync();
        if (enabled != _settings.RunAtStartup)
        {
            _settings.RunAtStartup = enabled;
            _store!.Save(_settings, out _);
        }

        SyncTrayStartupFlag();
    }

    private async Task SetStartupAsync(bool enable)
    {
        var ok = await StartupHelper.SetEnabledAsync(enable);

        if (!ok)
        {
            _settings.RunAtStartup = await StartupHelper.IsEnabledAsync();
            var blocked = await StartupHelper.IsBlockedByUserAsync();
            Notify(
                blocked
                    ? "开机自启被任务管理器禁用了，请在「启动应用」里手动启用。"
                    : "无法修改开机自启状态。",
                isError: true);
        }
        else
        {
            _settings.RunAtStartup = enable;
            _store!.Save(_settings, out _);
        }

        SyncTrayStartupFlag();
    }

    private async Task ApplyStartupAsync(bool enable)
    {
        var current = await StartupHelper.IsEnabledAsync();
        if (current == enable)
        {
            return;
        }

        if (!await StartupHelper.SetEnabledAsync(enable))
        {
            _settings.RunAtStartup = current;
            _store!.Save(_settings, out _);
            Notify(
                enable
                    ? "开机自启没打开（可能被任务管理器禁用）。"
                    : "开机自启没关掉。",
                isError: true);
        }

        SyncTrayStartupFlag();
    }

    private void SyncTrayStartupFlag()
    {
        if (_tray is not null)
        {
            _tray.StartupEnabled = _settings.RunAtStartup;
        }

        UpdateTooltip();
    }

    // ---------------- OCR ----------------

    private async Task WarmupOcrAsync()
    {
        try
        {
            var tag = ResolveLanguageTag();
            var ready = await _ocr!.WarmupAsync(tag);

            if (!ready)
            {
                var installed = _ocr.AvailableLanguages;
                if (installed.Count == 0)
                {
                    Notify(
                        "系统没有 OCR 语言包。请以管理员身份运行：" +
                        SettingsWindow.ChineseLanguagePackCommand,
                        isError: true);
                }
                else
                {
                    Notify($"OCR 引擎未就绪：{_ocr.AvailabilityMessage}", isError: true);
                }
            }
            else if (!_startupHintShown)
            {
                _startupHintShown = true;
                AppLog.Write($"OCR 就绪，语言 {tag}，可用 {_ocr.AvailableLanguages.Count} 种。");
            }

            UpdateTooltip();
        }
        catch (Exception ex)
        {
            AppLog.Write("预热失败：" + ex.Message);
        }
    }

    private string ResolveLanguageTag()
    {
        if (!string.IsNullOrWhiteSpace(_settings.OcrLanguage))
        {
            return _settings.OcrLanguage;
        }

        var installed = _ocr?.AvailableLanguages ?? Array.Empty<OcrLanguageInfo>();

        foreach (var language in installed)
        {
            if (language.Tag.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
            {
                return language.Tag;
            }
        }

        return installed.Count > 0 ? installed[0].Tag : string.Empty;
    }

    private void UpdateTooltip()
    {
        if (_tray is null)
        {
            return;
        }

        var hotkey = _hotkey?.Current.ToDisplayString() ?? string.Empty;
        _tray.UpdateTooltip($"SnapOCR · {hotkey} 框选取字");
    }

    // ---------------- 辅助 ----------------

    private void Notify(string message, bool isError = false)
    {
        if (_tray is not null)
        {
            _tray.ShowBalloon(isError ? "SnapOCR" : "SnapOCR 提示", message, isError);
        }

        if (isError)
        {
            AppLog.Write("[ERROR] " + message);
        }
        else
        {
            AppLog.Write("" + message);
        }
    }

    private void OnInstanceActivated(object? sender, AppActivationArguments args)
    {
        var dispatcher = _host?.DispatcherQueue;
        if (dispatcher is null)
        {
            return;
        }

        dispatcher.TryEnqueue(DispatcherQueuePriority.Normal, () => _ = BeginCaptureAsync());
    }

    private void ExitApp()
    {
        _hotkey?.Dispose();
        _tray?.Dispose();
        _clipboardWatcher?.Dispose();
        _capture?.Dispose();

        try
        {
            _toast?.Close();
        }
        catch (Exception)
        {
        }

        try
        {
            _host?.Close();
        }
        catch (Exception)
        {
        }

        Exit();
    }
}

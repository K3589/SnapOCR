using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using SnapOCR.Capture;
using SnapOCR.Ocr;
using SnapOCR.Settings;
using SnapOCR.Tools;

namespace SnapOCR.UiProbe;

/// <summary>
/// 端到端验收工具（需要 SnapOCR 已在运行）：
///   1. 截一张基准图，量一下平均亮度；
///   2. 模拟按 Win+Shift+T；
///   3. 再截一张，亮度明显下降 = 覆盖层真的弹出来了；
///   4. 在某个文字行上模拟拖拽；
///   5. 读剪贴板，确认拿到了文本。
/// 不依赖任何 UI 自动化框架，纯 SendInput + GDI。
/// </summary>
internal static class Program
{
    private const int VkLWin = 0x5B;
    private const int VkShift = 0x10;
    private const int VkT = 0x54;
    private const uint KeyeventfKeyUp = 0x0002;
    private const uint MouseeventfLeftDown = 0x0002;
    private const uint MouseeventfLeftUp = 0x0004;
    private const uint CfUnicodeText = 13;

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint dwFlags, int dx, int dy, uint dwData, UIntPtr dwExtraInfo);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetClipboardData(uint uFormat);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalFree(IntPtr hMem);

    [DllImport("user32.dll")]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        // 必须在抓屏/建窗口之前：否则本进程看到的是被系统缩放过的虚拟坐标，
        // 和 SnapOCR（Per-Monitor V2）收到的真实物理像素对不上，会差一个缩放倍率。
        _ = SetProcessDpiAwarenessContext(new IntPtr(-4));

        if (args.Length >= 2 && args[0] == "--stress")
        {
            return await StressAsync(int.Parse(args[1]));
        }

        if (args.Length >= 2 && args[0] == "--snapshot")
        {
            return await SnapshotAsync(args[1]);
        }

        if (args.Length >= 1 && args[0] == "--test-hotkey")
        {
            TestHotkey(0x0008 | 0x0004, VkT, "Win+Shift+T");
            TestHotkey(VkControl | 0x0004, VkT, "Ctrl+Shift+T");
            return 0;
        }

        if (args.Length >= 1 && args[0] == "--clipboard-p6")
        {
            return await ClipboardP6Async(useOle: true);
        }

        if (args.Length >= 1 && args[0] == "--clipboard-p6-dib")
        {
            return await ClipboardP6Async(useOle: false);
        }

        if (args.Length >= 4 && args[0] == "--click")
        {
            var x = int.Parse(args[1]);
            var y = int.Parse(args[2]);
            var right = args[3] == "right";
            SetCursorPos(x, y);
            await Task.Delay(120);
            mouse_event(right ? 0x0008 : MouseeventfLeftDown, 0, 0, 0, UIntPtr.Zero);
            if (!right)
            {
                await Task.Delay(40);
                mouse_event(MouseeventfLeftUp, 0, 0, 0, UIntPtr.Zero);
            }
            else
            {
                await Task.Delay(40);
                mouse_event(0x0010, 0, 0, 0, UIntPtr.Zero);
            }

            Console.WriteLine($"clicked {x},{y} right={right}");
            return 0;
        }

        if (args.Length >= 2 && args[0] == "--shot")
        {
            Directory.CreateDirectory(args[1]);
            using var shotCapture = new ScreenCapture();
            shotCapture.Warmup();
            var shotFrames = shotCapture.CaptureAll();
            if (shotFrames.Count == 0)
            {
                return 1;
            }

            var frame = shotFrames[0];
            var fullPath = Path.Combine(args[1], "screen.png");
            PngWriter.Write(fullPath, frame.Pixels, frame.Width, frame.Height, 2);
            Console.WriteLine("整屏 → " + fullPath);

            // 任务栏右下角（Win11 通知区域）
            var taskbar = new PixelRect(
                Math.Max(0, frame.Width - 320),
                Math.Max(0, frame.Height - 70),
                320,
                70);
            var taskbarPath = Path.Combine(args[1], "taskbar.png");
            PngWriter.WriteRegion(taskbarPath, frame.Pixels, frame.Width, frame.Height,
                taskbar.Left, taskbar.Top, taskbar.Width, taskbar.Height, scale: 1);
            Console.WriteLine("任务栏 → " + taskbarPath);
            return 0;
        }   // PER_MONITOR_AWARE_V2

        using var capture = new ScreenCapture();
        capture.Warmup();

        var service = new WindowsOcrService();
        var tag = service.AvailableLanguages.FirstOrDefault(l => l.Tag.StartsWith("zh", StringComparison.OrdinalIgnoreCase)).Tag
                  ?? (service.AvailableLanguages.Count > 0 ? service.AvailableLanguages[0].Tag : string.Empty);
        if (!await service.WarmupAsync(tag))
        {
            Console.WriteLine("OCR 未就绪：" + service.AvailabilityMessage);
            return 2;
        }

        // 先把剪贴板清干净，避免误判成上一次的结果。
        SetClipboardText("__SNAPOCR_PROBE_EMPTY__");

        var baselineFrames = capture.CaptureAll();
        if (baselineFrames.Count == 0)
        {
            Console.WriteLine("抓屏失败");
            return 2;
        }

        var baseline = baselineFrames[0];
        var baselineLuma = AverageLuma(baseline);
        Console.WriteLine($"基准亮度 = {baselineLuma:F1}");

        DumpSnapOcrWindows();

        // 找一行够长的文字，作为框选目标。
        var target = await FindTargetLineAsync(service, baseline);
        Console.WriteLine(target is null
            ? "没找到可用的文字行，退化为屏幕中央区域"
            : $"目标文字：\"{target.Value.Text}\"  矩形 {target.Value.Rect.Left},{target.Value.Rect.Top} {target.Value.Rect.Width}x{target.Value.Rect.Height}");

        var rect = target?.Rect ?? new PixelRect(
            baseline.Width / 4, baseline.Height / 2, baseline.Width / 2, 120);

        // 1) 按热键：直接读应用真实生效的配置，避免瞎猜组合（Win 键还会弹出开始菜单干扰判断）
        var hotkey = ReadConfiguredHotkey();
        SendHotkey(hotkey);
        await Task.Delay(500);

        // 2) 看覆盖层是否出现（遮罩 0xA6 黑，亮度应掉到 35% 左右）
        var overlayFrames = capture.CaptureAll();
        var overlayLuma = overlayFrames.Count > 0 ? AverageLuma(overlayFrames[0]) : baselineLuma;
        var dimmed = baselineLuma > 0 && overlayLuma / baselineLuma < 0.7;
        Console.WriteLine($"{hotkey.Display} → 亮度 {overlayLuma:F1}（{overlayLuma / Math.Max(baselineLuma, 1) * 100:F0}%）{(dimmed ? " 遮罩生效" : " 无变化")}");

        if (!dimmed)
        {
            Console.WriteLine("覆盖层没有出现，后续拖拽跳过。");
            DumpSnapOcrWindows();
            return 3;
        }

        // 3) 拖拽框选
        var dragRect = Inflate(rect, 6);
        Console.WriteLine($"拖拽 {dragRect.Left},{dragRect.Top} → {dragRect.Right},{dragRect.Bottom}");
        await DragAsync(dragRect);

        await Task.Delay(250);
        var afterDrag = capture.CaptureAll();
        var afterLuma = afterDrag.Count > 0 ? AverageLuma(afterDrag[0]) : 0;
        Console.WriteLine($"拖拽后亮度 = {afterLuma:F1} → 覆盖层{(afterLuma / Math.Max(baselineLuma, 1) < 0.7 ? "仍在（拖拽没被识别）" : "已关闭（识别流程已启动）")}");
        DumpSnapOcrWindows();

        await Task.Delay(1500);
        var clipboard = GetClipboardText();
        Console.WriteLine($"\n剪贴板长度 = {clipboard.Length}");
        Console.WriteLine("剪贴板内容（前 400 字）：");
        Console.WriteLine(clipboard.Length > 400 ? clipboard[..400] + "…" : clipboard);

        if (target is not null)
        {
            var expected = target.Value.Text;
            var matched = clipboard.Contains(expected, StringComparison.Ordinal)
                          || expected.Contains(clipboard.Trim(), StringComparison.Ordinal);
            Console.WriteLine($"\n与目标行匹配：{matched}（期望包含 \"{expected}\"）");
            return matched ? 0 : 1;
        }

        return string.IsNullOrWhiteSpace(clipboard) ? 1 : 0;
    }

    private static async Task<(string Text, PixelRect Rect)?> FindTargetLineAsync(WindowsOcrService service, CapturedFrame frame)
    {
        var cropped = frame.Crop(frame.Bounds);

        // 用引擎给出的逐行矩形。OcrOutcome 不带几何信息，这里按本机已安装语言创建底层引擎。
        var preferredTag = service.AvailableLanguages.FirstOrDefault(l => l.Tag.StartsWith("zh", StringComparison.OrdinalIgnoreCase)).Tag
                           ?? service.AvailableLanguages.FirstOrDefault().Tag;
        var engine = !string.IsNullOrWhiteSpace(preferredTag)
            ? Windows.Media.Ocr.OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language(preferredTag))
            : Windows.Media.Ocr.OcrEngine.TryCreateFromUserProfileLanguages();
        if (engine is null)
        {
            return null;
        }

        using var software = await SoftwareBitmapFactory.CreateFromBgraAsync(cropped.Bgra, cropped.Width, cropped.Height, cropped.Stride);
        var result = await engine.RecognizeAsync(software);

        foreach (var line in result.Lines)
        {
            var text = TextComposer.CollapseCjkSpaces(line.Text.Trim());
            if (text.Length < 5)
            {
                continue;
            }

            var words = line.Words;
            if (words.Count == 0)
            {
                continue;
            }

            var left = words.Min(w => w.BoundingRect.X);
            var top = words.Min(w => w.BoundingRect.Y);
            var right = words.Max(w => w.BoundingRect.X + w.BoundingRect.Width);
            var bottom = words.Max(w => w.BoundingRect.Y + w.BoundingRect.Height);

            if (right - left < 60 || bottom - top < 14)
            {
                continue;
            }

            // 太靠近屏幕边缘的行容易被任务栏/标题栏干扰，跳过去。
            if (top < 80 || left < 60)
            {
                continue;
            }

            return (text, new PixelRect((int)left, (int)top, (int)(right - left), (int)(bottom - top)));
        }

        return null;
    }

    private static PixelRect Inflate(PixelRect rect, int padding)
        => new(rect.Left - padding, rect.Top - padding, rect.Width + padding * 2, rect.Height + padding * 2);

    private static void SendHotkey((string Display, byte[] Modifiers, byte Key) hotkey)
    {
        foreach (var modifier in hotkey.Modifiers)
        {
            keybd_event(modifier, 0, 0, UIntPtr.Zero);
            Thread.Sleep(25);
        }

        keybd_event(hotkey.Key, 0, 0, UIntPtr.Zero);
        Thread.Sleep(45);
        keybd_event(hotkey.Key, 0, KeyeventfKeyUp, UIntPtr.Zero);

        for (var i = hotkey.Modifiers.Length - 1; i >= 0; i--)
        {
            Thread.Sleep(20);
            keybd_event(hotkey.Modifiers[i], 0, KeyeventfKeyUp, UIntPtr.Zero);
        }
    }

    /// <summary>从应用自己的 settings.json 里读真实生效的热键。</summary>
    private static (string Display, byte[] Modifiers, byte Key) ReadConfiguredHotkey()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SnapOCR", "settings.json"),
        };

        var packages = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages");
        if (Directory.Exists(packages))
        {
            candidates = candidates
                .Concat(Directory.GetDirectories(packages, "SnapOCR*")
                    .Select(d => Path.Combine(d, "LocalCache", "Local", "SnapOCR", "settings.json")))
                .ToArray();
        }

        foreach (var path in candidates)
        {
            try
            {
                if (!File.Exists(path))
                {
                    continue;
                }

                using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
                if (!document.RootElement.TryGetProperty("hotkey", out var element))
                {
                    continue;
                }

                var text = element.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    Console.WriteLine($"配置热键 = {text}（{path}）");
                    return ParseHotkey(text);
                }
            }
            catch (Exception)
            {
            }
        }

        Console.WriteLine("读不到配置，回退 Ctrl+Shift+T");
        return ("Ctrl+Shift+T", new byte[] { VkControl, VkShift }, VkT);
    }

    private static (string Display, byte[] Modifiers, byte Key) ParseHotkey(string text)
    {
        if (HotkeyDefinition.TryParse(text, out var definition))
        {
            var modifiers = new List<byte>(4);
            if (definition.Modifiers.HasFlag(HotkeyModifiers.Win))
            {
                modifiers.Add(VkLWin);
            }

            if (definition.Modifiers.HasFlag(HotkeyModifiers.Control))
            {
                modifiers.Add(VkControl);
            }

            if (definition.Modifiers.HasFlag(HotkeyModifiers.Alt))
            {
                modifiers.Add(0x12);
            }

            if (definition.Modifiers.HasFlag(HotkeyModifiers.Shift))
            {
                modifiers.Add(VkShift);
            }

            return (definition.ToString(), modifiers.ToArray(), (byte)definition.VirtualKey);
        }

        return ("Ctrl+Shift+T", new byte[] { VkControl, VkShift }, VkT);
    }

    /// <summary>
    /// 连续触发压测：每次都走完 热键 → 拖拽 → 识别 → 写剪贴板，
    /// 记录进程内存和顶层窗口数量，用来看有没有窗口泄漏/内存持续增长。
    /// </summary>
    /// <summary>抓两张图：覆盖层出现时的一张、Toast 出现时的一张，用于人工核对视觉。</summary>
    private static async Task<int> SnapshotAsync(string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);

        using var capture = new ScreenCapture();
        capture.Warmup();
        var hotkey = ReadConfiguredHotkey();

        SendHotkey(hotkey);
        await Task.Delay(650);

        var overlayFrames = capture.CaptureAll();
        if (overlayFrames.Count == 0)
        {
            Console.WriteLine("抓屏失败");
            return 1;
        }

        var overlayPath = Path.Combine(outputDirectory, "overlay.png");
        PngWriter.Write(overlayPath, overlayFrames[0].Pixels, overlayFrames[0].Width, overlayFrames[0].Height, 2);
        Console.WriteLine("覆盖层截图 → " + overlayPath);

        // 找一个文字行，拖出一块选区，然后马上抓 Toast。
        var service = new WindowsOcrService();
        var tag = service.AvailableLanguages.FirstOrDefault(l => l.Tag.StartsWith("zh", StringComparison.OrdinalIgnoreCase)).Tag
                  ?? (service.AvailableLanguages.Count > 0 ? service.AvailableLanguages[0].Tag : string.Empty);
        await service.WarmupAsync(tag);
        var target = await FindTargetLineAsync(service, overlayFrames[0]);
        var rect = Inflate(target?.Rect ?? new PixelRect(700, 500, 800, 160), 6);

        await DragAsync(rect);
        await Task.Delay(320);

        var toastFrames = capture.CaptureAll();
        if (toastFrames.Count > 0)
        {
            var toastPath = Path.Combine(outputDirectory, "toast.png");
            PngWriter.Write(toastPath, toastFrames[0].Pixels, toastFrames[0].Width, toastFrames[0].Height, 2);
            Console.WriteLine("Toast 截图 → " + toastPath);
            Console.WriteLine("剪贴板长度 = " + GetClipboardText().Length);

            // 找到当前可见的那块 SnapOCR 窗口（就是 Toast），1:1 裁出来看细节。
            var visible = FindVisibleSnapOcrWindow();
            if (visible is { Width: > 0 })
            {
                var cropPath = Path.Combine(outputDirectory, "toast-crop.png");
                PngWriter.WriteRegion(
                    cropPath,
                    toastFrames[0].Pixels,
                    toastFrames[0].Width,
                    toastFrames[0].Height,
                    Math.Max(0, visible.Value.Left - 10),
                    Math.Max(0, visible.Value.Top - 10),
                    visible.Value.Width + 20,
                    visible.Value.Height + 20,
                    scale: 1);
                Console.WriteLine($"Toast 窗口 {visible.Value.Left},{visible.Value.Top} {visible.Value.Width}x{visible.Value.Height} → {cropPath}");
            }
        }

        return 0;
    }

    private static PixelRect? FindVisibleSnapOcrWindow()
    {
        var processIds = Process.GetProcessesByName("SnapOCR").Select(p => (uint)p.Id).ToHashSet();
        PixelRect? result = null;

        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            if (!processIds.Contains(pid) || !IsWindowVisible(hwnd))
            {
                return true;
            }

            GetWindowRect(hwnd, out var rect);
            var width = rect.Right - rect.Left;
            var height = rect.Bottom - rect.Top;
            if (width is > 0 and < 800 && height is > 0 and < 400)
            {
                result = new PixelRect(rect.Left, rect.Top, width, height);
                return false;
            }

            return true;
        }, IntPtr.Zero);

        return result;
    }

    /// <summary>
    /// P6 验证：往剪贴板塞一张 DIB 图片，应用应该通过 WM_CLIPBOARDUPDATE 发现它，
    /// 自动 OCR 并把识别结果写回剪贴板。
    /// </summary>
    private static async Task<int> ClipboardP6Async(bool useOle)
    {
        using var capture = new ScreenCapture();
        capture.Warmup();
        var frames = capture.CaptureAll();
        if (frames.Count == 0)
        {
            Console.WriteLine("抓屏失败");
            return 1;
        }

        var service = new WindowsOcrService();
        var tag = service.AvailableLanguages.FirstOrDefault(l => l.Tag.StartsWith("zh", StringComparison.OrdinalIgnoreCase)).Tag
                  ?? (service.AvailableLanguages.Count > 0 ? service.AvailableLanguages[0].Tag : string.Empty);
        await service.WarmupAsync(tag);

        var target = await FindTargetLineAsync(service, frames[0]);
        var rect = Inflate(target?.Rect ?? new PixelRect(600, 500, 900, 120), 8);
        var cropped = frames[0].Crop(rect);

        Console.WriteLine($"把 {cropped.Width}x{cropped.Height} 的截图写进剪贴板（{(useOle ? "OLE" : "裸 CF_DIB")}），等应用自动取字…");
        if (useOle)
        {
            SetClipboardImage(cropped.Bgra, cropped.Width, cropped.Height);
        }
        else
        {
            WriteDibToClipboard(cropped.Bgra, cropped.Width, cropped.Height);
        }

        await Task.Delay(2500);

        var text = GetClipboardText();
        Console.WriteLine($"剪贴板长度 = {text.Length}");
        Console.WriteLine(text.Length > 240 ? text[..240] + "…" : text);
        return string.IsNullOrWhiteSpace(text) ? 1 : 0;
    }

    /// <summary>
    /// 用 WinForms 的 Clipboard.SetImage 写图片 —— 它走 OLE，和 Win+Shift+S 截图工具写出来的
    /// 剪贴板格式一致。裸的 CF_DIB 对 Windows.ApplicationModel.DataTransfer.Clipboard 不可见，
    /// 用它测 P6 会得到假阴性。
    /// </summary>
    private static void SetClipboardImage(byte[] bgra, int width, int height)
    {
        var thread = new Thread(() =>
        {
            var bitmap = new System.Drawing.Bitmap(
                width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            var rectangle = new System.Drawing.Rectangle(0, 0, width, height);
            var data = bitmap.LockBits(rectangle, System.Drawing.Imaging.ImageLockMode.WriteOnly,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb);

            for (var y = 0; y < height; y++)
            {
                Marshal.Copy(bgra, y * width * 4, data.Scan0 + (y * data.Stride), width * 4);
            }

            bitmap.UnlockBits(data);
            System.Windows.Forms.Clipboard.SetImage(bitmap);

            // OLE 可能是延迟渲染，等应用真读走再释放。
            Thread.Sleep(1500);
            bitmap.Dispose();
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        thread.Join();
    }

    private static void WriteDibToClipboard(byte[] bgra, int width, int height)
    {
        const int headerSize = 40;
        var imageSize = width * height * 4;
        var total = headerSize + imageSize;

        if (!OpenClipboard(IntPtr.Zero))
        {
            Console.WriteLine("打开剪贴板失败");
            return;
        }

        try
        {
            EmptyClipboard();
            var global = GlobalAlloc(0x0002, (UIntPtr)(uint)total);
            if (global == IntPtr.Zero)
            {
                return;
            }

            var pointer = GlobalLock(global);
            if (pointer == IntPtr.Zero)
            {
                GlobalFree(global);
                return;
            }

            var header = new byte[headerSize];
            BitConverter.GetBytes(40).CopyTo(header, 0);
            BitConverter.GetBytes(width).CopyTo(header, 4);
            BitConverter.GetBytes(height).CopyTo(header, 8);        // 正数 = bottom-up
            BitConverter.GetBytes((short)1).CopyTo(header, 12);
            BitConverter.GetBytes((short)32).CopyTo(header, 14);
            BitConverter.GetBytes(0).CopyTo(header, 16);            // BI_RGB
            BitConverter.GetBytes(imageSize).CopyTo(header, 20);
            Marshal.Copy(header, 0, pointer, headerSize);

            var rowBytes = width * 4;
            for (var y = 0; y < height; y++)
            {
                var sourceRow = (height - 1 - y) * rowBytes;
                Marshal.Copy(bgra, sourceRow, pointer + headerSize + (y * rowBytes), rowBytes);
            }

            GlobalUnlock(global);
            if (SetClipboardData(8, global) == IntPtr.Zero)   // CF_DIB
            {
                GlobalFree(global);
                Console.WriteLine("写入 CF_DIB 失败");
            }
        }
        finally
        {
            CloseClipboard();
        }
    }

    private static async Task<int> StressAsync(int times)
    {
        var hotkey = ReadConfiguredHotkey();

        // 压测区域用「真实有文字的行」，否则每次都识别不到，测不出真实开销。
        using var capture = new ScreenCapture();
        capture.Warmup();
        var service = new WindowsOcrService();
        var tag = service.AvailableLanguages.FirstOrDefault(l => l.Tag.StartsWith("zh", StringComparison.OrdinalIgnoreCase)).Tag
                  ?? (service.AvailableLanguages.Count > 0 ? service.AvailableLanguages[0].Tag : string.Empty);
        await service.WarmupAsync(tag);

        var frames = capture.CaptureAll();
        var target = frames.Count > 0 ? await FindTargetLineAsync(service, frames[0]) : null;
        var rect = Inflate(target?.Rect ?? new PixelRect(700, 700, 700, 120), 4);

        Console.WriteLine($"压测 {times} 次，热键 {hotkey.Display}，区域 {rect.Left},{rect.Top} {rect.Width}x{rect.Height}");
        Console.WriteLine("次数\t内存MB\t窗口数\t剪贴板长度");

        for (var i = 1; i <= times; i++)
        {
            SendHotkey(hotkey);
            await Task.Delay(280);
            await DragAsync(rect);
            await Task.Delay(420);

            var process = Process.GetProcessesByName("SnapOCR").FirstOrDefault();
            var memory = process is null ? 0 : process.WorkingSet64 / 1024 / 1024;
            var windows = CountSnapOcrWindows();
            var clipboard = GetClipboardText();
            Console.WriteLine($"{i}\t{memory}\t{windows}\t{clipboard.Length}");

            if (process is null)
            {
                Console.WriteLine("进程消失了！");
                return 1;
            }
        }

        // 静置几秒看内存是否回落
        await Task.Delay(3000);
        var final = Process.GetProcessesByName("SnapOCR").FirstOrDefault();
        Console.WriteLine($"静置后内存 = {(final is null ? 0 : final.WorkingSet64 / 1024 / 1024)} MB，窗口数 = {CountSnapOcrWindows()}");
        return 0;
    }

    /// <summary>
    /// 用 hWnd = NULL 注册热键（消息进线程队列），只关心能否注册成功。
    /// 用来判断某个组合是否已经被别的程序占用。
    /// </summary>
    private static void TestHotkey(uint modifiers, byte key, string name)
    {
        const int id = 0x7A11;
        var ok = RegisterHotKey(IntPtr.Zero, id, modifiers | 0x4000, key);
        var error = Marshal.GetLastWin32Error();
        Console.WriteLine($"{name} 注册={(ok ? "成功（当前空闲）" : $"失败 err={error}" + (error == 1409 ? " 已被其它程序占用" : string.Empty))}");

        if (ok)
        {
            UnregisterHotKey(IntPtr.Zero, id);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private static int CountSnapOcrWindows()
    {
        var processIds = Process.GetProcessesByName("SnapOCR").Select(p => (uint)p.Id).ToHashSet();
        var count = 0;

        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            if (processIds.Contains(pid))
            {
                var className = new StringBuilder(128);
                GetClassName(hwnd, className, className.Capacity);
                if (className.ToString().StartsWith("WinUIDesktop", StringComparison.Ordinal))
                {
                    count++;
                }
            }

            return true;
        }, IntPtr.Zero);

        return count;
    }

    /// <summary>把 SnapOCR 进程的所有顶层窗口打出来，用来判断覆盖层到底建没建。</summary>
    private static void DumpSnapOcrWindows()
    {
        var processIds = Process.GetProcessesByName("SnapOCR").Select(p => (uint)p.Id).ToHashSet();
        if (processIds.Count == 0)
        {
            Console.WriteLine("SnapOCR 进程未运行。");
            return;
        }

        Console.WriteLine("SnapOCR 的顶层窗口：");
        var index = 0;
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            if (!processIds.Contains(pid))
            {
                return true;
            }

            var className = new StringBuilder(256);
            GetClassName(hwnd, className, className.Capacity);
            var title = new StringBuilder(256);
            GetWindowText(hwnd, title, title.Capacity);
            GetWindowRect(hwnd, out var rect);

            Console.WriteLine($"  [{index++}] class={className} visible={IsWindowVisible(hwnd)} title=\"{title}\" rect={rect.Left},{rect.Top} {rect.Right - rect.Left}x{rect.Bottom - rect.Top}");
            return true;
        }, IntPtr.Zero);

        if (index == 0)
        {
            Console.WriteLine("  （没有找到任何窗口）");
        }
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out Rect lpRect);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private const int VkControl = 0x11;

    private static async Task DragAsync(PixelRect rect)
    {
        SetCursorPos(rect.Left, rect.Top);
        await Task.Delay(80);
        mouse_event(MouseeventfLeftDown, 0, 0, 0, UIntPtr.Zero);
        await Task.Delay(60);

        const int steps = 12;
        for (var i = 1; i <= steps; i++)
        {
            var x = rect.Left + (rect.Width * i / steps);
            var y = rect.Top + (rect.Height * i / steps);
            SetCursorPos(x, y);
            await Task.Delay(16);
        }

        await Task.Delay(80);
        mouse_event(MouseeventfLeftUp, 0, 0, 0, UIntPtr.Zero);
    }

    private static double AverageLuma(CapturedFrame frame)
    {
        long sum = 0;
        var count = 0;

        // 每 97 个像素采样一次，够用且快。
        for (var i = 0; i + 3 < frame.Pixels.Length; i += 97 * 4)
        {
            sum += frame.Pixels[i + 2] + frame.Pixels[i + 1] + frame.Pixels[i];
            count += 3;
        }

        return count == 0 ? 0 : (double)sum / count;
    }

    private static void SetClipboardText(string text)
    {
        if (!OpenClipboard(IntPtr.Zero))
        {
            return;
        }

        try
        {
            EmptyClipboard();
            var bytes = Encoding.Unicode.GetBytes(text + "\0");
            var global = GlobalAlloc(0x0002, (UIntPtr)(uint)bytes.Length);
            if (global == IntPtr.Zero)
            {
                return;
            }

            var target = GlobalLock(global);
            if (target == IntPtr.Zero)
            {
                GlobalFree(global);
                return;
            }

            Marshal.Copy(bytes, 0, target, bytes.Length);
            GlobalUnlock(global);
            SetClipboardData(CfUnicodeText, global);
        }
        finally
        {
            CloseClipboard();
        }
    }

    private static string GetClipboardText()
    {
        if (!OpenClipboard(IntPtr.Zero))
        {
            return string.Empty;
        }

        try
        {
            var handle = GetClipboardData(CfUnicodeText);
            if (handle == IntPtr.Zero)
            {
                return string.Empty;
            }

            var pointer = GlobalLock(handle);
            if (pointer == IntPtr.Zero)
            {
                return string.Empty;
            }

            try
            {
                var text = Marshal.PtrToStringUni(pointer);
                return text ?? string.Empty;
            }
            finally
            {
                GlobalUnlock(handle);
            }
        }
        finally
        {
            CloseClipboard();
        }
    }
}

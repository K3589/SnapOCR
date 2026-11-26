using System.Diagnostics;
using System.Text;
using SnapOCR.Capture;
using SnapOCR.Ocr;
using SnapOCR.Tools;

namespace SnapOCR.OcrProbe;

/// <summary>
/// 不启动 UI，直接把生产代码里的抓屏 + OCR 链路跑一遍，用来验证：
///   1. 多屏/负坐标下 BitBlt 抓到的画面是否就是那块屏幕；
///   2. SoftwareBitmap 构造 + OcrEngine 是否真的能出字；
///   3. 单次识别的耗时。
/// 用法：dotnet run --project tools/OcrProbe [-- 输出目录]
/// </summary>
internal static class Program
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        // 与主程序保持一致：Per-Monitor V2，否则拿到的是被缩放的虚拟坐标。
        _ = SetProcessDpiAwarenessContext(new IntPtr(-4));

        var outputDirectory = args.Length > 0
            ? args[0]
            : Path.Combine(Path.GetTempPath(), "snapocr-probe");
        Directory.CreateDirectory(outputDirectory);

        Console.WriteLine("== 显示器 ==");
        var monitors = MonitorEnumerator.GetMonitors();
        foreach (var monitor in monitors)
        {
            Console.WriteLine($"  {monitor.DeviceName}  {monitor.Left},{monitor.Top}  {monitor.Width}x{monitor.Height}  dpi={monitor.Dpi}  primary={monitor.IsPrimary}");
        }

        Console.WriteLine("\n== 可用 OCR 语言 ==");
        var service = new WindowsOcrService();
        foreach (var language in service.AvailableLanguages)
        {
            Console.WriteLine($"  {language.Tag}  {language.DisplayName}");
        }

        var warmupWatch = Stopwatch.StartNew();
        var tag = ResolveTag(service);
        var ready = await service.WarmupAsync(tag);
        warmupWatch.Stop();
        Console.WriteLine($"\n预热 [{tag}] = {ready}，耗时 {warmupWatch.ElapsedMilliseconds} ms，引擎可用={service.IsAvailable}");
        if (!ready)
        {
            Console.WriteLine("引擎不可用：" + service.AvailabilityMessage);
            return 2;
        }

        Console.WriteLine("\n== 抓屏 ==");
        using var capture = new ScreenCapture();
        var captureWatch = Stopwatch.StartNew();
        var frames = capture.CaptureAll();
        captureWatch.Stop();
        Console.WriteLine($"  抓到 {frames.Count} 块屏幕，总耗时 {captureWatch.ElapsedMilliseconds} ms");
        foreach (var frame in frames)
        {
            Console.WriteLine($"  {frame.Monitor.DeviceName}  {frame.Width}x{frame.Height}  {frame.Pixels.Length / 1024 / 1024.0:F1} MB");
        }

        foreach (var frame in frames)
        {
            // 落盘只为人工核对抓的是不是那块屏；产品本身全程不落盘。
            var path = Path.Combine(outputDirectory, $"screen-{Sanitize(frame.Monitor.DeviceName)}.bmp");
            WriteBmp(path, frame.Pixels, frame.Width, frame.Height);
            Console.WriteLine($"  已写出 {path}");

            // 附一张 1/2 缩略图：2560 宽的 BMP 太大，看图工具会拒绝。
            var halfPath = Path.Combine(outputDirectory, $"screen-{Sanitize(frame.Monitor.DeviceName)}-half.bmp");
            WriteHalfBmp(halfPath, frame.Pixels, frame.Width, frame.Height);
            Console.WriteLine($"  已写出 {halfPath}");

            var pngPath = Path.Combine(outputDirectory, $"screen-{Sanitize(frame.Monitor.DeviceName)}.png");
            PngWriter.Write(pngPath, frame.Pixels, frame.Width, frame.Height, scale: 2);
            Console.WriteLine($"  已写出 {pngPath}");

            // 左上角 900×1250 的 1:1 裁切，用来看设置窗口的真实排版。
            var cropPath = Path.Combine(outputDirectory, $"crop-{Sanitize(frame.Monitor.DeviceName)}.png");
            PngWriter.WriteRegion(cropPath, frame.Pixels, frame.Width, frame.Height, 100, 80, 900, 1250, scale: 1);
            Console.WriteLine($"  已写出 {cropPath}");
        }

        Console.WriteLine("\n== OCR ==");
        foreach (var frame in frames)
        {
            // 取整屏：最能暴露坐标问题。真实使用里是用户框选的小块。
            var cropped = frame.Crop(frame.Bounds);
            var watch = Stopwatch.StartNew();
            var outcome = await service.RecognizeAsync(new OcrRequest(cropped.Bgra, cropped.Width, cropped.Height, cropped.Stride));
            watch.Stop();

            Console.WriteLine($"  --- {frame.Monitor.DeviceName} {cropped.Width}x{cropped.Height} ---");
            Console.WriteLine($"  success={outcome.Success} lines={outcome.LineCount} chars={outcome.Text.Length} 耗时={watch.ElapsedMilliseconds} ms");
            if (!outcome.Success)
            {
                Console.WriteLine("  error=" + outcome.Error);
                continue;
            }

            var preview = outcome.Text.Length > 600 ? outcome.Text[..600] + "…" : outcome.Text;
            Console.WriteLine("  文本（前 600 字）：");
            Console.WriteLine(preview);
            Console.WriteLine();
        }

        return 0;
    }

    private static string ResolveTag(WindowsOcrService service)
    {
        foreach (var language in service.AvailableLanguages)
        {
            if (language.Tag.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
            {
                return language.Tag;
            }
        }

        return service.AvailableLanguages.Count > 0 ? service.AvailableLanguages[0].Tag : string.Empty;
    }

    private static string Sanitize(string name)
        => string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    /// <summary>写一张 2×2 平均降采样的 BMP，便于看图工具打开。</summary>
    private static void WriteHalfBmp(string path, byte[] bgra, int width, int height)
    {
        var halfWidth = Math.Max(1, width / 2);
        var halfHeight = Math.Max(1, height / 2);
        var stride = halfWidth * 4;
        var scaled = new byte[stride * halfHeight];

        for (var y = 0; y < halfHeight; y++)
        {
            for (var x = 0; x < halfWidth; x++)
            {
                var sumB = 0;
                var sumG = 0;
                var sumR = 0;

                for (var dy = 0; dy < 2; dy++)
                {
                    for (var dx = 0; dx < 2; dx++)
                    {
                        var index = ((y * 2 + dy) * width + (x * 2 + dx)) * 4;
                        sumB += bgra[index];
                        sumG += bgra[index + 1];
                        sumR += bgra[index + 2];
                    }
                }

                var target = y * stride + x * 4;
                scaled[target] = (byte)(sumB / 4);
                scaled[target + 1] = (byte)(sumG / 4);
                scaled[target + 2] = (byte)(sumR / 4);
                scaled[target + 3] = 255;
            }
        }

        WriteBmp(path, scaled, halfWidth, halfHeight);
    }

    /// <summary>写一张 32 位 BMP 便于人工核对。</summary>
    private static void WriteBmp(string path, byte[] bgra, int width, int height)
    {
        var stride = width * 4;
        var fileSize = 14 + 40 + (stride * height);
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);

        writer.Write((byte)'B');
        writer.Write((byte)'M');
        writer.Write(fileSize);
        writer.Write(0);
        writer.Write(54);

        writer.Write(40);
        writer.Write(width);
        writer.Write(-height);      // top-down
        writer.Write((short)1);
        writer.Write((short)32);
        writer.Write(0);
        writer.Write(stride * height);
        writer.Write(2835);
        writer.Write(2835);
        writer.Write(0);
        writer.Write(0);

        writer.Write(bgra, 0, stride * height);
    }
}

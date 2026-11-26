using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace SnapOCR.Ocr;

/// <summary>
/// BGRA 内存 → SoftwareBitmap。
/// 首选 SoftwareBitmap.CreateCopyFromBuffer（零 PNG/BMP 编解码）；
/// 只有格式/对齐不被接受时才退回 BitmapEncoder(BmpEncoderId) → BitmapDecoder 的路径。
/// </summary>
public static class SoftwareBitmapFactory
{
    /// <summary>OCR 前对小图的放大倍数上限。</summary>
    public const double MaxUpscale = 2.0;

    /// <summary>低于这个高度就先放大再识别，小字识别率提升明显。</summary>
    public const int MinOcrHeight = 200;

    public static async Task<SoftwareBitmap> CreateFromBgraAsync(byte[] pixels, int width, int height, int stride)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "图像尺寸必须为正。");
        }

        var compact = stride == width * 4 ? pixels : Repack(pixels, width, height, stride);

        try
        {
            // 快速路径：直接把托管内存按 IBuffer 交给 WinRT，内部只做一次拷贝。
            return SoftwareBitmap.CreateCopyFromBuffer(
                compact.AsBuffer(),
                BitmapPixelFormat.Bgra8,
                width,
                height,
                BitmapAlphaMode.Premultiplied);
        }
        catch (Exception)
        {
            // 兜底路径：走一次 BMP 编解码，兼容任何奇怪的格式。
            return await CreateViaEncoderAsync(compact, width, height).ConfigureAwait(false);
        }
    }

    /// <summary>把高度不足 <see cref="MinOcrHeight"/> 的图放大（最多 <see cref="MaxUpscale"/> 倍）。</summary>
    public static async Task<SoftwareBitmap> EnsureMinimumHeightAsync(SoftwareBitmap source, int minHeight = MinOcrHeight, double maxScale = MaxUpscale)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (source.PixelHeight >= minHeight || minHeight <= 0 || maxScale <= 1.0)
        {
            return source;
        }

        var scale = Math.Min(maxScale, (double)minHeight / source.PixelHeight);
        var targetWidth = (uint)Math.Max(1, Math.Round(source.PixelWidth * scale));
        var targetHeight = (uint)Math.Max(1, Math.Round(source.PixelHeight * scale));

        if (targetWidth == source.PixelWidth && targetHeight == source.PixelHeight)
        {
            return source;
        }

        var transform = new BitmapTransform
        {
            ScaledWidth = targetWidth,
            ScaledHeight = targetHeight,
            InterpolationMode = BitmapInterpolationMode.Fant,
        };

        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.BmpEncoderId, stream);
        encoder.SetSoftwareBitmap(source);
        await encoder.FlushAsync();

        stream.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(stream);
        return await decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            transform,
            ExifOrientationMode.IgnoreExifOrientation,
            ColorManagementMode.DoNotColorManage);
    }

    /// <summary>按 OcrEngine.MaxImageDimension 限制缩小（极少触发，防止超大屏全选时抛异常）。</summary>
    public static async Task<SoftwareBitmap> EnsureMaximumDimensionAsync(SoftwareBitmap source, int maxDimension)
    {
        ArgumentNullException.ThrowIfNull(source);

        var longest = Math.Max(source.PixelWidth, source.PixelHeight);
        if (maxDimension <= 0 || longest <= maxDimension)
        {
            return source;
        }

        var scale = (double)maxDimension / longest;
        var transform = new BitmapTransform
        {
            ScaledWidth = (uint)Math.Max(1, Math.Round(source.PixelWidth * scale)),
            ScaledHeight = (uint)Math.Max(1, Math.Round(source.PixelHeight * scale)),
            InterpolationMode = BitmapInterpolationMode.Fant,
        };

        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.BmpEncoderId, stream);
        encoder.SetSoftwareBitmap(source);
        await encoder.FlushAsync();

        stream.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(stream);
        return await decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            transform,
            ExifOrientationMode.IgnoreExifOrientation,
            ColorManagementMode.DoNotColorManage);
    }

    private static async Task<SoftwareBitmap> CreateViaEncoderAsync(byte[] pixels, int width, int height)
    {
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.BmpEncoderId, stream);
        encoder.SetPixelData(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            (uint)width,
            (uint)height,
            96,
            96,
            pixels);
        await encoder.FlushAsync();

        stream.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(stream);
        return await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
    }

    private static byte[] Repack(byte[] source, int width, int height, int stride)
    {
        var rowBytes = width * 4;
        if (stride < rowBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(stride), "stride 小于一行像素所需字节数。");
        }

        var required = (height - 1) * stride + rowBytes;
        if (source.Length < required)
        {
            throw new ArgumentException("像素缓冲区长度不足。", nameof(source));
        }

        var compact = new byte[rowBytes * height];
        for (var y = 0; y < height; y++)
        {
            System.Buffer.BlockCopy(source, y * stride, compact, y * rowBytes, rowBytes);
        }

        return compact;
    }
}

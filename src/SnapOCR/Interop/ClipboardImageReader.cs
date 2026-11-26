using System.Runtime.InteropServices;

namespace SnapOCR.Interop;

/// <summary>
/// 从剪贴板直接读 CF_DIB。
/// Windows 截图工具/主流截图软件走的是 OLE 格式，WinRT 的 DataPackageView 能直接拿到；
/// 但有些老程序只写裸 CF_DIB，这时 WinRT 的 AvailableFormats 是空的，必须走这里兜底。
/// </summary>
internal static class ClipboardImageReader
{
    /// <summary>读取失败返回 null（不是 DIB、格式不支持、剪贴板被占用等）。</summary>
    public static (byte[] Bgra, int Width, int Height)? TryReadDib(IntPtr ownerHwnd)
    {
        if (!NativeMethods.IsClipboardFormatAvailable(NativeMethods.CF_DIB))
        {
            return null;
        }

        if (!NativeMethods.OpenClipboard(ownerHwnd))
        {
            return null;
        }

        try
        {
            var handle = NativeMethods.GetClipboardData(NativeMethods.CF_DIB);
            if (handle == IntPtr.Zero)
            {
                return null;
            }

            var pointer = NativeMethods.GlobalLock(handle);
            if (pointer == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                return ReadFromPointer(pointer);
            }
            finally
            {
                NativeMethods.GlobalUnlock(handle);
            }
        }
        finally
        {
            NativeMethods.CloseClipboard();
        }
    }

    private static (byte[] Bgra, int Width, int Height)? ReadFromPointer(IntPtr pointer)
    {
        var header = new byte[40];
        Marshal.Copy(pointer, header, 0, header.Length);

        var headerSize = BitConverter.ToInt32(header, 0);
        var width = BitConverter.ToInt32(header, 4);
        var rawHeight = BitConverter.ToInt32(header, 8);
        var bitCount = BitConverter.ToInt16(header, 14);
        var compression = BitConverter.ToInt32(header, 16);
        var colorsUsed = BitConverter.ToInt32(header, 32);

        // 只处理最常见的 BI_RGB + 24/32 位；其它（RLE、位域）放弃。
        if (headerSize < 40 || compression != 0 || bitCount is not (24 or 32) || width <= 0 || rawHeight == 0)
        {
            return null;
        }

        var bottomUp = rawHeight > 0;
        var height = Math.Abs(rawHeight);
        var bytesPerPixel = bitCount / 8;
        var sourceStride = (((width * bitCount) + 31) / 32) * 4;
        var pixelOffset = headerSize + (colorsUsed * 4);

        var buffer = new byte[width * height * 4];
        var row = new byte[sourceStride];

        for (var y = 0; y < height; y++)
        {
            var sourceRow = bottomUp ? height - 1 - y : y;
            Marshal.Copy(pointer + pixelOffset + (sourceRow * sourceStride), row, 0, sourceStride);

            var targetRow = y * width * 4;
            for (var x = 0; x < width; x++)
            {
                var sourceIndex = x * bytesPerPixel;
                var targetIndex = targetRow + (x * 4);

                buffer[targetIndex] = row[sourceIndex];         // B
                buffer[targetIndex + 1] = row[sourceIndex + 1]; // G
                buffer[targetIndex + 2] = row[sourceIndex + 2]; // R
                buffer[targetIndex + 3] = 255;                  // A
            }
        }

        return (buffer, width, height);
    }
}

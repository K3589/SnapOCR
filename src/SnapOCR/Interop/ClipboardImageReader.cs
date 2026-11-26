namespace SnapOCR.Interop;

/// <summary>
/// 从剪贴板直接读 CF_DIB。
/// Windows 截图工具/主流截图软件走的是 OLE 格式，WinRT 的 DataPackageView 能直接拿到；
/// 但有些老程序只写裸 CF_DIB，这时 WinRT 的 AvailableFormats 是空的，必须走这里兜底。
/// </summary>
internal static class ClipboardImageReader
{
    private const ulong MaxDibBytes = 256UL * 1024 * 1024;

    /// <summary>读取失败返回 null（不是 DIB、格式不支持、剪贴板被占用等）。</summary>
    public static (byte[] Bgra, int Width, int Height)? TryReadDib(IntPtr ownerHwnd)
    {
        if (!NativeMethods.IsClipboardFormatAvailable(NativeMethods.CF_DIB))
        {
            return null;
        }

        var opened = false;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (NativeMethods.OpenClipboard(ownerHwnd))
            {
                opened = true;
                break;
            }

            if (attempt < 2)
            {
                Thread.Sleep(30);
            }
        }

        if (!opened)
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

            var size = NativeMethods.GlobalSize(handle).ToUInt64();
            if (size < 40 || size > MaxDibBytes)
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
                unsafe
                {
                    var span = new ReadOnlySpan<byte>(pointer.ToPointer(), (int)size);
                    return DibParser.TryParse(span);
                }
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
}

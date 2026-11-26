using System.Runtime.InteropServices;
using System.Text;
using Windows.ApplicationModel.DataTransfer;

namespace SnapOCR.Interop;

/// <summary>
/// 写纯文本到剪贴板。先走 WinRT（打包应用的标准路径），
/// 失败再用 Win32 兜底；两者都失败才算失败 —— 提示绝不能在写入成功之前弹出。
/// </summary>
public static class ClipboardHelper
{
    public static async Task<bool> TrySetTextAsync(string text, IntPtr ownerHwnd)
    {
        if (text is null)
        {
            return false;
        }

        // WinRT 路径：剪贴板被别的进程占用时会抛 CLIPBRD_E_CANT_OPEN，重试几次即可。
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                var package = new DataPackage
                {
                    RequestedOperation = DataPackageOperation.Copy,
                };
                package.SetText(text);
                Clipboard.SetContent(package);

                try
                {
                    // 让内容在本进程退出后仍然留在剪贴板里。
                    Clipboard.Flush();
                }
                catch (Exception)
                {
                    // Flush 不是必须的，失败不影响本次复制。
                }

                return true;
            }
            catch (Exception)
            {
                if (attempt < 2)
                {
                    await Task.Delay(40).ConfigureAwait(true);
                }
            }
        }

        return TrySetTextWin32(text, ownerHwnd);
    }

    /// <summary>Win32 兜底实现。返回 true 表示 SetClipboardData 真的成功了。</summary>
    public static bool TrySetTextWin32(string text, IntPtr ownerHwnd)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (!NativeMethods.OpenClipboard(ownerHwnd))
            {
                Thread.Sleep(30);
                continue;
            }

            var global = IntPtr.Zero;
            var handedOver = false;

            try
            {
                if (!NativeMethods.EmptyClipboard())
                {
                    continue;
                }

                var bytes = Encoding.Unicode.GetBytes(text + "\0");
                global = NativeMethods.GlobalAlloc(NativeMethods.GMEM_MOVEABLE, (UIntPtr)(uint)bytes.Length);
                if (global == IntPtr.Zero)
                {
                    return false;
                }

                var target = NativeMethods.GlobalLock(global);
                if (target == IntPtr.Zero)
                {
                    return false;
                }

                try
                {
                    Marshal.Copy(bytes, 0, target, bytes.Length);
                }
                finally
                {
                    NativeMethods.GlobalUnlock(global);
                }

                if (NativeMethods.SetClipboardData(NativeMethods.CF_UNICODETEXT, global) == IntPtr.Zero)
                {
                    return false;
                }

                // 所有权移交给系统，不能再 GlobalFree。
                handedOver = true;
                return true;
            }
            finally
            {
                if (!handedOver && global != IntPtr.Zero)
                {
                    NativeMethods.GlobalFree(global);
                }

                NativeMethods.CloseClipboard();
            }
        }

        return false;
    }
}

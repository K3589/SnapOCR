namespace SnapOCR.Interop;

/// <summary>
/// 监听 WM_CLIPBOARDUPDATE（P6「跟随系统截图」用），完全事件驱动，不做轮询。
/// 自己写剪贴板时用 <see cref="SuppressCurrentSequence"/> 抑制掉自己触发的那一次。
/// </summary>
public sealed class ClipboardWatcher : IDisposable
{
    private static readonly IntPtr SubclassId = new(0x1003);
    private static readonly NativeMethods.SubclassProc CallbackProc = Callback;
    private static readonly Dictionary<IntPtr, ClipboardWatcher> Live = new();
    private static readonly object LiveLock = new();

    private readonly IntPtr _hwnd;
    private uint _suppressedSequence;
    private bool _listening;
    private bool _disposed;

    public ClipboardWatcher(IntPtr hwnd)
    {
        _hwnd = hwnd;
        Attach();
    }

    /// <summary>剪贴板里出现了位图。</summary>
    public event EventHandler? ImageAvailable;

    public bool IsListening => _listening;

    public void Start()
    {
        if (_disposed || _listening || _hwnd == IntPtr.Zero)
        {
            return;
        }

        _listening = NativeMethods.AddClipboardFormatListener(_hwnd);
    }

    public void Stop()
    {
        if (!_listening)
        {
            return;
        }

        NativeMethods.RemoveClipboardFormatListener(_hwnd);
        _listening = false;
    }

    /// <summary>标记「这次剪贴板变化是我自己写的，别当成用户截图」。</summary>
    public void SuppressCurrentSequence() => _suppressedSequence = NativeMethods.GetClipboardSequenceNumber();

    private void Attach()
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        lock (LiveLock)
        {
            Live[_hwnd] = this;
        }

        NativeMethods.SetWindowSubclass(_hwnd, CallbackProc, SubclassId, IntPtr.Zero);
    }

    private static IntPtr Callback(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, IntPtr uIdSubclass, IntPtr dwRefData)
    {
        ClipboardWatcher? self;
        lock (LiveLock)
        {
            Live.TryGetValue(hWnd, out self);
        }

        return self is not null
            ? self.OnMessage(hWnd, uMsg, wParam, lParam)
            : NativeMethods.DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }

    private IntPtr OnMessage(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam)
    {
        if (uMsg == NativeMethods.WM_CLIPBOARDUPDATE)
        {
            var sequence = NativeMethods.GetClipboardSequenceNumber();
            if (sequence != 0 && sequence == _suppressedSequence)
            {
                // 自己写的那一次，忽略。
                return IntPtr.Zero;
            }

            if (NativeMethods.IsClipboardFormatAvailable(NativeMethods.CF_DIB)
                || NativeMethods.IsClipboardFormatAvailable(NativeMethods.CF_BITMAP))
            {
                ImageAvailable?.Invoke(this, EventArgs.Empty);
            }

            return IntPtr.Zero;
        }

        return NativeMethods.DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();

        lock (LiveLock)
        {
            if (Live.TryGetValue(_hwnd, out var current) && ReferenceEquals(current, this))
            {
                Live.Remove(_hwnd);
            }
        }
    }
}

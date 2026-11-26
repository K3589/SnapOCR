using SnapOCR.Capture;
using SnapOCR.Interop;

namespace SnapOCR.UI;

/// <summary>
/// 管理「每块显示器一个覆盖层窗口」：预创建、同步显示器变化、分发选区事件。
/// 跨屏拖拽时所有窗口共享同一个全局选区，各自裁出自己那块来画。
/// </summary>
public sealed class OverlayManager : IDisposable
{
    private readonly List<OverlayWindow> _windows = new();
    private bool _isShowing;
    private bool _disposed;

    /// <summary>用户完成框选，参数是虚拟桌面坐标下的物理矩形。</summary>
    public event EventHandler<PixelRect>? SelectionCompleted;

    /// <summary>用户按 Esc / 右键 / 失去捕获。</summary>
    public event EventHandler? Cancelled;

    public bool IsShowing => _isShowing;

    public int WindowCount => _windows.Count;

    /// <summary>启动时预创建所有覆盖层窗口（此时还不可见）。</summary>
    public void Prepare()
    {
        if (_disposed)
        {
            return;
        }

        SyncWindows();
    }

    /// <summary>
    /// 显示覆盖层。传入的是热键按下瞬间抓下来的冻结帧，必须与当前显示器一一对应。
    /// 返回 false 表示没有可用的屏幕。
    /// </summary>
    public async Task<bool> ShowAsync(IReadOnlyList<CapturedFrame> frames)
    {
        if (_disposed || frames is null || frames.Count == 0)
        {
            return false;
        }

        if (!SyncWindows())
        {
            return false;
        }

        var tasks = new List<Task>(_windows.Count);
        foreach (var window in _windows)
        {
            var frame = FindFrame(frames, window.Monitor);
            if (frame is null)
            {
                continue;
            }

            tasks.Add(window.SetFrameAsync(frame));
        }

        if (tasks.Count == 0)
        {
            return false;
        }

        // 等冻结帧全部就绪再显示，避免用户看到一帧空白。
        await Task.WhenAll(tasks).ConfigureAwait(true);

        var focusTarget = FindWindowAtCursor();
        foreach (var window in _windows)
        {
            window.ShowOverlay(activate: false);
        }

        focusTarget?.ShowOverlay(activate: true);
        _isShowing = true;
        return true;
    }

    public void Hide()
    {
        foreach (var window in _windows)
        {
            window.HideOverlay();
        }

        _isShowing = false;
    }

    private static CapturedFrame? FindFrame(IReadOnlyList<CapturedFrame> frames, MonitorBounds monitor)
    {
        foreach (var frame in frames)
        {
            if (frame.Monitor == monitor)
            {
                return frame;
            }
        }

        // 显示器名在极少数驱动上会变，退一步按几何位置匹配。
        foreach (var frame in frames)
        {
            if (frame.Monitor.Left == monitor.Left
                && frame.Monitor.Top == monitor.Top
                && frame.Monitor.Width == monitor.Width
                && frame.Monitor.Height == monitor.Height)
            {
                return frame;
            }
        }

        return null;
    }

    /// <summary>
    /// 让窗口集合与当前显示器一致；显示器数量/排列/DPI 变了就整体重建
    /// （支持热插拔和改缩放，不用重启应用）。
    /// </summary>
    private bool SyncWindows()
    {
        var monitors = MonitorEnumerator.GetMonitors();

        var same = _windows.Count == monitors.Count;
        if (same)
        {
            for (var i = 0; i < monitors.Count; i++)
            {
                if (_windows[i].Monitor != monitors[i])
                {
                    same = false;
                    break;
                }
            }
        }

        if (same)
        {
            return _windows.Count > 0;
        }

        CloseAllWindows();

        foreach (var monitor in monitors)
        {
            var window = new OverlayWindow();
            window.ApplyGeometry(monitor);
            window.DragStarted += OnDragStarted;
            window.DragUpdated += OnDragUpdated;
            window.DragCompleted += OnDragCompleted;
            window.CancelRequested += OnCancelRequested;
            _windows.Add(window);
        }

        return _windows.Count > 0;
    }

    private void CloseAllWindows()
    {
        foreach (var window in _windows)
        {
            window.DragStarted -= OnDragStarted;
            window.DragUpdated -= OnDragUpdated;
            window.DragCompleted -= OnDragCompleted;
            window.CancelRequested -= OnCancelRequested;

            try
            {
                window.Close();
            }
            catch (Exception)
            {
                // 已经关掉的窗口忽略。
            }
        }

        _windows.Clear();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _isShowing = false;
        CloseAllWindows();
    }

    private void OnDragStarted(object? sender, EventArgs e)
    {
        // 拖拽开始时把提示收起来，视觉更干净。
        foreach (var window in _windows)
        {
            window.UpdateSelectionVisual(PixelRect.Empty);
        }
    }

    private void OnDragUpdated(object? sender, PixelRect rect)
    {
        foreach (var window in _windows)
        {
            window.UpdateSelectionVisual(rect);
        }
    }

    private void OnDragCompleted(object? sender, PixelRect rect)
    {
        AppLog.Write($"OverlayManager.OnDragCompleted rect={rect.Left},{rect.Top} {rect.Width}x{rect.Height}");
        Hide();

        if (rect.IsEmpty || rect.Width < 3 || rect.Height < 3)
        {
            // 只是点了一下，没真正框选。
            AppLog.Write("选区小于 3px，按取消处理");
            Cancelled?.Invoke(this, EventArgs.Empty);
            return;
        }

        SelectionCompleted?.Invoke(this, rect);
    }

    private void OnCancelRequested(object? sender, EventArgs e)
    {
        AppLog.Write("OverlayManager.OnCancelRequested");
        Hide();
        Cancelled?.Invoke(this, EventArgs.Empty);
    }

    private OverlayWindow? FindWindowAtCursor()
    {
        if (!NativeMethods.GetCursorPos(out var point))
        {
            return _windows.FirstOrDefault();
        }

        foreach (var window in _windows)
        {
            var monitor = window.Monitor;
            if (point.X >= monitor.Left && point.X < monitor.Right
                && point.Y >= monitor.Top && point.Y < monitor.Bottom)
            {
                return window;
            }
        }

        return _windows.FirstOrDefault();
    }
}

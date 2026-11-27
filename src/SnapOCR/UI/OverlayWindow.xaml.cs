using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using SnapOCR.Capture;
using SnapOCR.Interop;
using SnapOCR.Ocr;
using SnapOCR.Settings;
using Windows.Foundation;
using Windows.Graphics;

namespace SnapOCR.UI;

/// <summary>覆盖层根容器：在 WinUI 3 中通过 ProtectedCursor 原生显示十字准星光标。</summary>
public sealed class CrosshairGrid : Grid
{
    public CrosshairGrid()
    {
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Cross);
    }
}

/// <summary>
/// 一块显示器上的框选覆盖层。窗口在启动时预创建，之后只做 Show/Hide，
/// 不再重建 —— 这是「热键到覆盖层可见 &lt; 100ms」的关键。
/// </summary>
public sealed partial class OverlayWindow : Window
{
    private static readonly IntPtr SubclassId = new(0x1004);
    private static readonly NativeMethods.SubclassProc CallbackProc = Callback;
    private static readonly Dictionary<IntPtr, OverlayWindow> Live = new();
    private static readonly object LiveLock = new();

    private readonly IntPtr _hwnd;
    private SoftwareBitmapSource? _source;
    private uint _dpi = DpiHelper.FallbackDpi;
    private double _scale = 1.0;
    private double _dipWidth = 1;
    private double _dipHeight = 1;
    private bool _dragging;
    private (int X, int Y) _originPixel;
    private bool _subclassed;

    public OverlayWindow()
    {
        InitializeComponent();

        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);

        AppWindow.IsShownInSwitchers = false;
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsAlwaysOnTop = true;
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsModal = false;
        }

        Root.PointerPressed += OnPointerPressed;
        Root.PointerMoved += OnPointerMoved;
        Root.PointerReleased += OnPointerReleased;
        Root.PointerCaptureLost += OnPointerCaptureLost;

        Attach();
        ResetSelection();
    }

    /// <summary>本窗口负责的显示器。</summary>
    public MonitorBounds Monitor { get; private set; }

    public bool IsOverlayVisible { get; private set; }

    /// <summary>开始拖拽（参数为空，由管理器统一处理）。</summary>
    public event EventHandler? DragStarted;

    /// <summary>拖拽中，参数是虚拟桌面坐标下的物理矩形。</summary>
    public event EventHandler<PixelRect>? DragUpdated;

    /// <summary>松手完成，参数是最终选区。</summary>
    public event EventHandler<PixelRect>? DragCompleted;

    /// <summary>Esc / 右键取消。</summary>
    public event EventHandler? CancelRequested;

    /// <summary>把窗口摆到指定显示器上，并记下该屏的 DPI。</summary>
    public void ApplyGeometry(MonitorBounds monitor)
    {
        Monitor = monitor;

        // 窗口摆到目标显示器后，XAML 的 DIP 基准由「窗口 DPI」决定，
        // 它未必等于显示器 DPI（混合 DPI、远程会话、虚拟显示器都可能不一致）。
        // 换算必须跟着窗口 DPI 走，否则框选会整体偏移一个缩放比例。
        AppWindow.MoveAndResize(new RectInt32(monitor.Left, monitor.Top, monitor.Width, monitor.Height));

        var windowDpi = DpiHelper.GetDpiForWindowSafe(_hwnd);
        _dpi = windowDpi == 0 ? DpiHelper.FallbackDpi : windowDpi;
        _scale = DpiHelper.ScaleFactor(_dpi);
        _dipWidth = Math.Max(1, monitor.Width / _scale);
        _dipHeight = Math.Max(1, monitor.Height / _scale);

        Root.Width = _dipWidth;
        Root.Height = _dipHeight;

        AppLog.Write($"ApplyGeometry {monitor.DeviceName} 屏幕={monitor.Left},{monitor.Top} {monitor.Width}x{monitor.Height} 显示器DPI={monitor.Dpi} 窗口DPI={windowDpi} 缩放={_scale:F2} DIP={_dipWidth:F0}x{_dipHeight:F0}");
    }

    /// <summary>把冻结帧铺到窗口上。必须在 Show 之前完成，否则会看到一帧黑底。</summary>
    public async Task SetFrameAsync(CapturedFrame frame)
    {
        var bitmap = await SoftwareBitmapFactory.CreateFromBgraAsync(
            frame.Pixels, frame.Width, frame.Height, frame.Stride).ConfigureAwait(true);

        try
        {
            if (_source is null)
            {
                _source = new SoftwareBitmapSource();
                FrameImage.Source = _source;
            }

            await _source.SetBitmapAsync(bitmap);
        }
        finally
        {
            bitmap.Dispose();
        }
    }

    public void ShowOverlay(bool activate)
    {
        AppWindow.Show();
        IsOverlayVisible = true;
        ResetSelection();
        HintBadge.Visibility = Visibility.Visible;

        if (activate)
        {
            // 只有拿到焦点的窗口才能收到 Esc；后台热键唤起时配合 SetForegroundWindow 提升抢焦成功率。
            NativeMethods.SetForegroundWindow(_hwnd);
            Activate();
        }
    }

    public void HideOverlay()
    {
        if (!IsOverlayVisible)
        {
            return;
        }

        _dragging = false;
        IsOverlayVisible = false;
        AppWindow.Hide();
    }

    public void ResetSelection()
    {
        _dragging = false;
        SetFullMask();
        SelectionBorder.Visibility = Visibility.Collapsed;
        SizeBadge.Visibility = Visibility.Collapsed;
    }

    /// <summary>按全局物理矩形刷新「选区外变暗」的视觉效果。</summary>
    public void UpdateSelectionVisual(PixelRect global)
    {
        if (global.IsEmpty)
        {
            SetFullMask();
            SelectionBorder.Visibility = Visibility.Collapsed;
            SizeBadge.Visibility = Visibility.Collapsed;
            return;
        }

        var localX = global.Left - Monitor.Left;
        var localY = global.Top - Monitor.Top;

        var left = Math.Clamp(DpiHelper.PhysicalToDip(localX, _dpi), 0, _dipWidth);
        var top = Math.Clamp(DpiHelper.PhysicalToDip(localY, _dpi), 0, _dipHeight);
        var right = Math.Clamp(DpiHelper.PhysicalToDip(localX + global.Width, _dpi), 0, _dipWidth);
        var bottom = Math.Clamp(DpiHelper.PhysicalToDip(localY + global.Height, _dpi), 0, _dipHeight);

        if (right <= left || bottom <= top)
        {
            // 选区完全落在别的屏幕上，本屏整块变暗。
            SetFullMask();
            SelectionBorder.Visibility = Visibility.Collapsed;
            SizeBadge.Visibility = Visibility.Collapsed;
            return;
        }

        var selectionWidth = right - left;
        var selectionHeight = bottom - top;

        Place(MaskTop, 0, 0, _dipWidth, top);
        Place(MaskBottom, 0, bottom, _dipWidth, Math.Max(0, _dipHeight - bottom));
        Place(MaskLeft, 0, top, left, selectionHeight);
        Place(MaskRight, right, top, Math.Max(0, _dipWidth - right), selectionHeight);

        Place(SelectionBorder, left, top, selectionWidth, selectionHeight);
        SelectionBorder.Visibility = Visibility.Visible;

        // 尺寸标签跟着选区走，贴着上边缘；贴近屏幕顶端或右边缘时自动回退到可见范围内。
        var badgeTop = top - 30;
        if (badgeTop < 0)
        {
            badgeTop = top + 6;
        }

        SizeText.Text = $"{global.Width} × {global.Height}";
        SizeBadge.Visibility = Visibility.Visible;

        var badgeWidth = SizeBadge.ActualWidth > 0 ? SizeBadge.ActualWidth : 112;
        var badgeLeft = Math.Clamp(left + 4, 4, Math.Max(4, _dipWidth - badgeWidth - 4));
        Canvas.SetLeft(SizeBadge, badgeLeft);
        Canvas.SetTop(SizeBadge, badgeTop);

        HintBadge.Visibility = Visibility.Collapsed;
    }

    private static void Place(FrameworkElement element, double x, double y, double width, double height)
    {
        element.Width = Math.Max(0, width);
        element.Height = Math.Max(0, height);
        Canvas.SetLeft(element, x);
        Canvas.SetTop(element, y);
    }

    private void SetFullMask()
    {
        Place(MaskTop, 0, 0, _dipWidth, _dipHeight);
        Place(MaskLeft, 0, 0, 0, 0);
        Place(MaskRight, 0, 0, 0, 0);
        Place(MaskBottom, 0, 0, 0, 0);
    }

    // ---------- 指针交互 ----------

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(Root);

        if (point.Properties.IsRightButtonPressed)
        {
            CancelRequested?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
            return;
        }

        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        _dragging = true;
        _originPixel = GetCursorScreenPixel(point.Position);
        Root.CapturePointer(e.Pointer);

        DragStarted?.Invoke(this, EventArgs.Empty);
        DragUpdated?.Invoke(this, PixelRect.FromCorners(_originPixel.X, _originPixel.Y, _originPixel.X, _originPixel.Y));
        e.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        var current = GetCursorScreenPixel(e.GetCurrentPoint(Root).Position);
        DragUpdated?.Invoke(this, PixelRect.FromCorners(_originPixel.X, _originPixel.Y, current.X, current.Y));
        e.Handled = true;
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        var current = GetCursorScreenPixel(e.GetCurrentPoint(Root).Position);
        Root.ReleasePointerCapture(e.Pointer);

        var rect = PixelRect.FromCorners(_originPixel.X, _originPixel.Y, current.X, current.Y);
        DragCompleted?.Invoke(this, rect);
        e.Handled = true;
    }

    private void OnPointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        CancelRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 优先用 Win32 GetCursorPos 取虚拟桌面物理坐标：混合 DPI 多屏跨屏拖拽时
    /// 不会被起始窗口的 _dpi 错误缩放；取不到时回落到窗口内 DIP 换算。
    /// </summary>
    private (int X, int Y) GetCursorScreenPixel(Point fallbackDip)
    {
        if (NativeMethods.GetCursorPos(out var cursor))
        {
            return (cursor.X, cursor.Y);
        }

        return ToScreenPixel(fallbackDip);
    }

    /// <summary>窗口内 DIP 坐标 → 虚拟桌面物理像素坐标。负数/超界都合法（拖到别的屏）。</summary>
    private (int X, int Y) ToScreenPixel(Point dip)
        => (Monitor.Left + DpiHelper.DipToPhysicalInt(dip.X, _dpi),
            Monitor.Top + DpiHelper.DipToPhysicalInt(dip.Y, _dpi));

    // ---------- 消息子类化（Esc / 右键） ----------

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

        _subclassed = NativeMethods.SetWindowSubclass(_hwnd, CallbackProc, SubclassId, IntPtr.Zero);
        Closed += (_, _) => Detach();
    }

    private void Detach()
    {
        if (!_subclassed)
        {
            return;
        }

        NativeMethods.RemoveWindowSubclass(_hwnd, CallbackProc, SubclassId);
        _subclassed = false;

        lock (LiveLock)
        {
            Live.Remove(_hwnd);
        }

        _source?.Dispose();
        _source = null;
    }

    private static IntPtr Callback(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, IntPtr uIdSubclass, IntPtr dwRefData)
    {
        OverlayWindow? self;
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
        if (uMsg == NativeMethods.WM_KEYDOWN && wParam.ToInt32() == (int)VirtualKeys.Escape)
        {
            CancelRequested?.Invoke(this, EventArgs.Empty);
            return IntPtr.Zero;
        }

        return NativeMethods.DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }
}

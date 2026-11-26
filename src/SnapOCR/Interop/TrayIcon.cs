using System.Runtime.InteropServices;

namespace SnapOCR.Interop;

/// <summary>托盘右键菜单命令。</summary>
public enum TrayCommand
{
    Capture = 1,
    Settings = 2,
    ToggleStartup = 3,
    Exit = 4,
}

/// <summary>
/// 系统托盘图标。WinUI 3 没有原生托盘控件，直接用 Shell_NotifyIcon。
/// 菜单用 Win32 的 TrackPopupMenu —— 轻量、必定出现在光标位置、不依赖 XAML。
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private static readonly IntPtr SubclassId = new(0x1002);
    private static readonly NativeMethods.SubclassProc CallbackProc = Callback;
    private static readonly Dictionary<IntPtr, TrayIcon> Live = new();
    private static readonly object LiveLock = new();

    private readonly IntPtr _hwnd;
    private readonly uint _iconId = 1;
    private IntPtr _hIcon;
    private bool _ownsIcon;
    private bool _added;
    private bool _subclassed;
    private bool _disposed;
    private string _tooltip = "SnapOCR";

    public TrayIcon(IntPtr hwnd)
    {
        _hwnd = hwnd;
        Attach();
    }

    /// <summary>双击托盘图标 / 菜单「立即识别」。</summary>
    public event EventHandler? CaptureRequested;

    /// <summary>菜单「设置」。</summary>
    public event EventHandler? SettingsRequested;

    /// <summary>菜单「开机自动启动」，参数为切换后的目标状态。</summary>
    public event EventHandler<bool>? StartupToggleRequested;

    /// <summary>菜单「退出」。</summary>
    public event EventHandler? ExitRequested;

    /// <summary>托盘菜单勾选状态由调用方同步到这里。</summary>
    public bool StartupEnabled { get; set; }

    /// <summary>热键提示文字，显示在菜单第一项右侧。</summary>
    public string CaptureHotkeyText { get; set; } = string.Empty;

    /// <summary>图标是否已成功加进通知区域。</summary>
    public bool IsVisible => _added;

    public void Show(string tooltip)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _tooltip = string.IsNullOrWhiteSpace(tooltip) ? "SnapOCR" : tooltip;
        _hIcon = LoadTrayIcon();

        var data = CreateData(NativeMethods.NIF_MESSAGE | NativeMethods.NIF_ICON | NativeMethods.NIF_TIP);
        _added = NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_ADD, ref data);
    }

    public void UpdateTooltip(string tooltip)
    {
        _tooltip = string.IsNullOrWhiteSpace(tooltip) ? "SnapOCR" : tooltip;
        if (!_added)
        {
            return;
        }

        var data = CreateData(NativeMethods.NIF_TIP);
        NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_MODIFY, ref data);
    }

    /// <summary>弹一个托盘气泡（Win11 上会折叠成通知）。</summary>
    public void ShowBalloon(string title, string message, bool isError = false)
    {
        if (!_added)
        {
            return;
        }

        var data = CreateData(NativeMethods.NIF_INFO);
        data.szInfoTitle = Truncate(title, 63);
        data.szInfo = Truncate(message, 255);
        data.dwInfoFlags = isError ? NativeMethods.NIIF_ERROR : NativeMethods.NIIF_INFO;
        data.uTimeoutOrVersion = 5000;
        NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_MODIFY, ref data);
    }

    private NativeMethods.NOTIFYICONDATA CreateData(uint flags) => new()
    {
        cbSize = Marshal.SizeOf<NativeMethods.NOTIFYICONDATA>(),
        hWnd = _hwnd,
        uID = _iconId,
        uFlags = flags,
        uCallbackMessage = NativeMethods.WM_TRAYICON,
        hIcon = _hIcon,
        szTip = Truncate(_tooltip, 127),
        szInfo = string.Empty,
        szInfoTitle = string.Empty,
        dwInfoFlags = NativeMethods.NIIF_NONE,
        guidItem = Guid.Empty,
    };

    private static string Truncate(string? value, int max)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value.Length <= max ? value : value[..max];
    }

    private IntPtr LoadTrayIcon()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
            if (File.Exists(path))
            {
                var handle = NativeMethods.LoadImage(
                    IntPtr.Zero, path, NativeMethods.IMAGE_ICON, 0, 0,
                    NativeMethods.LR_LOADFROMFILE | NativeMethods.LR_DEFAULTSIZE);
                if (handle != IntPtr.Zero)
                {
                    _ownsIcon = true;
                    return handle;
                }
            }
        }
        catch (Exception)
        {
            // 落到系统默认图标
        }

        _ownsIcon = false;
        return NativeMethods.LoadIcon(IntPtr.Zero, new IntPtr(NativeMethods.IDI_APPLICATION));
    }

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
    }

    private static IntPtr Callback(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, IntPtr uIdSubclass, IntPtr dwRefData)
    {
        TrayIcon? self;
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
        if (uMsg == NativeMethods.WM_TRAYICON)
        {
            var mouseMessage = lParam.ToInt32() & 0xFFFF;
            switch (mouseMessage)
            {
                case NativeMethods.WM_LBUTTONDBLCLK:
                    CaptureRequested?.Invoke(this, EventArgs.Empty);
                    return IntPtr.Zero;
                case NativeMethods.WM_RBUTTONUP:
                    ShowContextMenu();
                    return IntPtr.Zero;
            }
        }

        return NativeMethods.DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }

    private void ShowContextMenu()
    {
        var menu = NativeMethods.CreatePopupMenu();
        if (menu == IntPtr.Zero)
        {
            return;
        }

        try
        {
            var captureText = string.IsNullOrEmpty(CaptureHotkeyText)
                ? "立即识别"
                : $"立即识别\t{CaptureHotkeyText}";

            NativeMethods.AppendMenu(menu, NativeMethods.MF_STRING | NativeMethods.MF_DEFAULT,
                new UIntPtr((uint)TrayCommand.Capture), captureText);
            NativeMethods.AppendMenu(menu, NativeMethods.MF_SEPARATOR, UIntPtr.Zero, null);
            NativeMethods.AppendMenu(menu, NativeMethods.MF_STRING,
                new UIntPtr((uint)TrayCommand.Settings), "设置…");
            NativeMethods.AppendMenu(
                menu,
                NativeMethods.MF_STRING | (StartupEnabled ? NativeMethods.MF_CHECKED : NativeMethods.MF_UNCHECKED),
                new UIntPtr((uint)TrayCommand.ToggleStartup),
                "开机自动启动");
            NativeMethods.AppendMenu(menu, NativeMethods.MF_SEPARATOR, UIntPtr.Zero, null);
            NativeMethods.AppendMenu(menu, NativeMethods.MF_STRING,
                new UIntPtr((uint)TrayCommand.Exit), "退出 SnapOCR");

            // 没有 SetForegroundWindow，点菜单外面菜单不会消失。
            NativeMethods.SetForegroundWindow(_hwnd);
            NativeMethods.GetCursorPos(out var point);

            var command = NativeMethods.TrackPopupMenu(
                menu,
                NativeMethods.TPM_LEFTALIGN | NativeMethods.TPM_BOTTOMALIGN |
                NativeMethods.TPM_RIGHTBUTTON | NativeMethods.TPM_RETURNCMD,
                point.X, point.Y, 0, _hwnd, IntPtr.Zero);

            NativeMethods.PostMessage(_hwnd, NativeMethods.WM_NULL, IntPtr.Zero, IntPtr.Zero);

            if (command > 0)
            {
                Dispatch((TrayCommand)command);
            }
        }
        finally
        {
            NativeMethods.DestroyMenu(menu);
        }
    }

    private void Dispatch(TrayCommand command)
    {
        switch (command)
        {
            case TrayCommand.Capture:
                CaptureRequested?.Invoke(this, EventArgs.Empty);
                break;
            case TrayCommand.Settings:
                SettingsRequested?.Invoke(this, EventArgs.Empty);
                break;
            case TrayCommand.ToggleStartup:
                StartupToggleRequested?.Invoke(this, !StartupEnabled);
                break;
            case TrayCommand.Exit:
                ExitRequested?.Invoke(this, EventArgs.Empty);
                break;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_added)
        {
            var data = CreateData(NativeMethods.NIF_MESSAGE);
            NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_DELETE, ref data);
            _added = false;
        }

        if (_subclassed)
        {
            NativeMethods.RemoveWindowSubclass(_hwnd, CallbackProc, SubclassId);
            _subclassed = false;
        }

        lock (LiveLock)
        {
            if (Live.TryGetValue(_hwnd, out var current) && ReferenceEquals(current, this))
            {
                Live.Remove(_hwnd);
            }
        }

        if (_ownsIcon && _hIcon != IntPtr.Zero)
        {
            NativeMethods.DestroyIcon(_hIcon);
        }

        _hIcon = IntPtr.Zero;
    }
}

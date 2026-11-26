using System.Runtime.InteropServices;
using SnapOCR.Settings;

namespace SnapOCR.Interop;

/// <summary>注册热键的结果，包含是否发生了降级。</summary>
public readonly record struct HotkeyRegistrationResult(bool Success, HotkeyDefinition Applied, string? Message);

/// <summary>
/// 全局热键。用 RegisterHotKey + SetWindowSubclass 拦 WM_HOTKEY。
/// WinUI 3 的窗口没有暴露 WndProc，所以必须走子类化这条路。
/// </summary>
public sealed class HotkeyHook : IDisposable
{
    private const int HotkeyId = 0x534F;                 // 'SO'
    private static readonly IntPtr SubclassId = new(0x1001);
    private const int ErrorHotkeyAlreadyRegistered = 1409;

    // SetWindowSubclass 只保存函数指针，托管委托必须由静态字段保活。
    private static readonly NativeMethods.SubclassProc CallbackProc = Callback;
    private static readonly Dictionary<IntPtr, HotkeyHook> Live = new();
    private static readonly object LiveLock = new();

    private readonly IntPtr _hwnd;
    private bool _subclassed;
    private bool _registered;
    private bool _disposed;

    public HotkeyHook(IntPtr hwnd)
    {
        _hwnd = hwnd;
        Attach();
    }

    /// <summary>热键被按下（已在 UI 线程）。</summary>
    public event EventHandler? Pressed;

    /// <summary>当前实际生效的热键。</summary>
    public HotkeyDefinition Current { get; private set; } = HotkeyDefinition.Default;

    public bool IsRegistered => _registered;

    /// <summary>
    /// 注册热键。Win+… 组合可能被系统或其它软件占用，
    /// 失败时自动降级到参数给出的 fallback（默认调用方传 Ctrl+Shift+T）。
    /// </summary>
    public HotkeyRegistrationResult Register(HotkeyDefinition definition, HotkeyDefinition? fallback = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        Unregister();

        if (TryRegisterCore(definition))
        {
            Current = definition;
            return new HotkeyRegistrationResult(true, definition, null);
        }

        var error = Marshal.GetLastWin32Error();
        var reason = error == ErrorHotkeyAlreadyRegistered
            ? "该组合键已被系统或其它程序占用。"
            : $"注册失败（Win32 错误 {error}）。";

        var fallbackDefinition = fallback ?? HotkeyDefinition.Fallback;
        if (fallbackDefinition != definition && TryRegisterCore(fallbackDefinition))
        {
            Current = fallbackDefinition;
            return new HotkeyRegistrationResult(
                true,
                fallbackDefinition,
                $"{definition.ToDisplayString()} {reason} 已自动降级为 {fallbackDefinition.ToDisplayString()}。");
        }

        return new HotkeyRegistrationResult(false, definition, reason + " 降级热键同样注册失败。");
    }

    private bool TryRegisterCore(HotkeyDefinition definition)
    {
        if (_hwnd == IntPtr.Zero || definition.VirtualKey == 0)
        {
            return false;
        }

        var modifiers = (uint)definition.Modifiers | NativeMethods.MOD_NOREPEAT;
        if (!NativeMethods.RegisterHotKey(_hwnd, HotkeyId, modifiers, definition.VirtualKey))
        {
            return false;
        }

        _registered = true;
        return true;
    }

    public void Unregister()
    {
        if (_registered)
        {
            NativeMethods.UnregisterHotKey(_hwnd, HotkeyId);
            _registered = false;
        }
    }

    private void Attach()
    {
        if (_hwnd == IntPtr.Zero || _subclassed)
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
        HotkeyHook? self;
        lock (LiveLock)
        {
            Live.TryGetValue(hWnd, out self);
        }

        if (self is not null)
        {
            return self.OnMessage(hWnd, uMsg, wParam, lParam);
        }

        return NativeMethods.DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }

    private IntPtr OnMessage(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam)
    {
        if (uMsg == NativeMethods.WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            Pressed?.Invoke(this, EventArgs.Empty);
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
        Unregister();

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
    }
}

using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using SnapOCR.Capture;
using SnapOCR.Interop;
using Windows.Graphics;

namespace SnapOCR.UI;

/// <summary>
/// 复制成功后的轻提示：无边框、置顶、不抢焦点、约 1 秒淡出。
/// 同一个窗口实例反复使用，不做反复创建销毁。
/// </summary>
public sealed partial class ResultToast : Window
{
    private readonly IntPtr _hwnd;
    private Storyboard? _storyboard;

    public ResultToast()
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
        }

        // 关键：绝不抢焦点，否则用户正准备 Ctrl+V 时输入会被打断。
        var exStyle = NativeMethods.GetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        NativeMethods.SetWindowLongPtr(
            _hwnd,
            NativeMethods.GWL_EXSTYLE,
            new IntPtr(exStyle | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW));

        // Win11 圆角（DWM 负责裁切，XAML 内容不用自己做四角透明）。
        var preference = NativeMethods.DWMWCP_ROUND;
        NativeMethods.DwmSetWindowAttribute(
            _hwnd, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));

        ApplyBackdrop();
    }

    private void ApplyBackdrop()
    {
        try
        {
            // Fluent 半透明材质；系统不支持时静默退化为 XAML 里的纯色。
            SystemBackdrop = new DesktopAcrylicBackdrop();
        }
        catch (Exception)
        {
        }
    }

    public void ShowToast(string message, bool isError = false)
    {
        MessageText.Text = message;
        ToastIcon.Glyph = isError ? "\uE7BA" : "\uE73E";
        ToastIcon.Foreground = isError
            ? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 240, 160, 90))
            : new SolidColorBrush(Windows.UI.Color.FromArgb(255, 98, 210, 138));

        PositionNearCursor();

        _storyboard?.Stop();
        _storyboard = BuildStoryboard(isError ? TimeSpan.FromMilliseconds(2400) : TimeSpan.FromMilliseconds(1260));
        ToastCard.Opacity = 0;
        ToastTranslate.Y = 10;

        AppWindow.Show();
        _storyboard.Begin();
    }

    public void HideToast()
    {
        _storyboard?.Stop();
        AppWindow.Hide();
    }

    private Storyboard BuildStoryboard(TimeSpan total)
    {
        var fade = new DoubleAnimationUsingKeyFrames();
        fade.KeyFrames.Add(new LinearDoubleKeyFrame { KeyTime = KeyTime.FromTimeSpan(TimeSpan.Zero), Value = 0 });
        fade.KeyFrames.Add(new LinearDoubleKeyFrame { KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(120)), Value = 1 });
        fade.KeyFrames.Add(new LinearDoubleKeyFrame { KeyTime = KeyTime.FromTimeSpan(total - TimeSpan.FromMilliseconds(240)), Value = 1 });
        fade.KeyFrames.Add(new LinearDoubleKeyFrame { KeyTime = KeyTime.FromTimeSpan(total), Value = 0 });
        Storyboard.SetTarget(fade, ToastCard);
        Storyboard.SetTargetProperty(fade, nameof(UIElement.Opacity));

        var slide = new DoubleAnimationUsingKeyFrames();
        slide.KeyFrames.Add(new EasingDoubleKeyFrame
        {
            KeyTime = KeyTime.FromTimeSpan(TimeSpan.Zero),
            Value = 10,
        });
        slide.KeyFrames.Add(new EasingDoubleKeyFrame
        {
            KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(160)),
            Value = 0,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
        Storyboard.SetTarget(slide, ToastTranslate);
        Storyboard.SetTargetProperty(slide, "Y");

        var storyboard = new Storyboard();
        storyboard.Children.Add(fade);
        storyboard.Children.Add(slide);
        storyboard.Completed += (_, _) => AppWindow.Hide();
        return storyboard;
    }

    private void PositionNearCursor()
    {
        var monitors = MonitorEnumerator.GetMonitors();
        if (monitors.Count == 0)
        {
            return;
        }

        MonitorBounds? target = null;
        if (NativeMethods.GetCursorPos(out var cursor))
        {
            foreach (var monitor in monitors)
            {
                if (cursor.X >= monitor.Left && cursor.X < monitor.Right
                    && cursor.Y >= monitor.Top && cursor.Y < monitor.Bottom)
                {
                    target = monitor;
                    break;
                }
            }
        }

        target ??= FindPrimary(monitors);
        if (target is null)
        {
            target = monitors[0];
        }

        var monitorBounds = target.Value;
        var scale = DpiHelper.ScaleFactor(monitorBounds.Dpi);
        var width = (int)Math.Round(264 * scale);
        var height = (int)Math.Round(56 * scale);
        var x = monitorBounds.Left + ((monitorBounds.Width - width) / 2);
        var y = monitorBounds.Top + monitorBounds.Height - height - (int)Math.Round(96 * scale);

        AppWindow.MoveAndResize(new RectInt32(x, y, width, height));
    }

    private static MonitorBounds? FindPrimary(IReadOnlyList<MonitorBounds> monitors)
    {
        foreach (var monitor in monitors)
        {
            if (monitor.IsPrimary)
            {
                return monitor;
            }
        }

        return null;
    }
}

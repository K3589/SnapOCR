using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace SnapOCR;

/// <summary>
/// 仅作消息宿主的窗口：接收 WM_HOTKEY / 托盘回调 / WM_CLIPBOARDUPDATE。
/// 永远不显示，也不出现在任务栏和 Alt+Tab 里。
/// </summary>
public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        Handle = WinRT.Interop.WindowNative.GetWindowHandle(this);

        AppWindow.IsShownInSwitchers = false;

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
        }

        // 摆到屏幕外并设为 120×120：即使某些环境强制显示也不会被看到。
        AppWindow.MoveAndResize(new RectInt32(-32000, -32000, 120, 120));
    }

    /// <summary>窗口句柄，热键/托盘/剪贴板监听都挂在它上面。</summary>
    public IntPtr Handle { get; }
}

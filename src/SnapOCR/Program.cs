using System.Threading;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace SnapOCR;

/// <summary>
/// 自定义入口（csproj 里定义了 DISABLE_XAML_GENERATED_MAIN）。
/// 用 AppInstance 重定向实现单实例：第二次启动只会唤起已有进程，不会多开。
/// </summary>
internal static class Program
{
    public const string InstanceKey = "SnapOCR.MainInstance";

    [STAThread]
    private static int Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();

        if (RedirectToExistingInstance())
        {
            return 0;
        }

        Application.Start(initParams =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });

        return 0;
    }

    private static bool RedirectToExistingInstance()
    {
        try
        {
            var instance = AppInstance.FindOrRegisterForKey(InstanceKey);
            if (instance.IsCurrent)
            {
                return false;
            }

            var activatedArgs = AppInstance.GetCurrent().GetActivatedEventArgs();
            var completed = new ManualResetEventSlim(false);

            _ = RedirectAsync(instance, activatedArgs, completed);

            // 等重定向真正完成；超时也不阻塞用户（直接退出，反正已有实例在跑）。
            completed.Wait(TimeSpan.FromSeconds(10));
            return true;
        }
        catch (Exception)
        {
            // 未打包运行、或系统不支持 AppInstance 时，当作首个实例继续启动。
            return false;
        }
    }

    private static async Task RedirectAsync(AppInstance instance, AppActivationArguments args, ManualResetEventSlim completed)
    {
        try
        {
            await instance.RedirectActivationToAsync(args);
        }
        catch (Exception)
        {
        }
        finally
        {
            completed.Set();
        }
    }
}

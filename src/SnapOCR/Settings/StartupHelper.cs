using Windows.ApplicationModel;

namespace SnapOCR.Settings;

/// <summary>
/// 开机自启。打包应用必须走 StartupTask（直接写 HKCU\Run 会让 exe 脱离包标识，起不来）。
/// TaskId 必须与 Package.appxmanifest 里的 uap:StartupTask 一致。
/// </summary>
public static class StartupHelper
{
    public const string TaskId = "SnapOCRAutoStart";

    public static async Task<bool> IsEnabledAsync()
    {
        try
        {
            var task = await StartupTask.GetAsync(TaskId);
            return task.State is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>返回切换后是否真的处于期望状态。</summary>
    public static async Task<bool> SetEnabledAsync(bool enabled)
    {
        try
        {
            var task = await StartupTask.GetAsync(TaskId);

            if (enabled)
            {
                var state = await task.RequestEnableAsync();
                return state is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy;
            }

            task.Disable();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>被用户在任务管理器里禁用时，只能引导用户自己去开。</summary>
    public static async Task<bool> IsBlockedByUserAsync()
    {
        try
        {
            var task = await StartupTask.GetAsync(TaskId);
            return task.State == StartupTaskState.DisabledByUser;
        }
        catch (Exception)
        {
            return false;
        }
    }
}

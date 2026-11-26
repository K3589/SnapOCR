using System.Diagnostics;

namespace SnapOCR;

/// <summary>
/// 极简诊断日志。
/// Debug 构建默认写到 %TEMP%\snapocr-trace.log（方便开发期排查延迟和失败原因）；
/// Release 构建默认不写任何文件，只有显式设置环境变量 SNAPOCR_TRACE=1 才落盘。
/// 产品正常使用时不产生任何磁盘文件。
/// </summary>
internal static class AppLog
{
    private static readonly object Sync = new();

#if DEBUG
    private static readonly bool Enabled = true;
#else
    private static readonly bool Enabled =
        string.Equals(Environment.GetEnvironmentVariable("SNAPOCR_TRACE"), "1", StringComparison.Ordinal);
#endif

    private static readonly string LogPath = Path.Combine(Path.GetTempPath(), "snapocr-trace.log");

    public static void Write(string message)
    {
        Debug.WriteLine("[SnapOCR] " + message);

        if (!Enabled)
        {
            return;
        }

        try
        {
            lock (Sync)
            {
                File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
        }
        catch (Exception)
        {
            // 日志失败绝不影响主流程。
        }
    }

    public static void Reset()
    {
        if (!Enabled)
        {
            return;
        }

        try
        {
            lock (Sync)
            {
                File.WriteAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff} === SnapOCR start ==={Environment.NewLine}");
            }
        }
        catch (Exception)
        {
        }
    }
}

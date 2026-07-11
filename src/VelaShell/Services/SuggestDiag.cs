using System.Diagnostics;

namespace VelaShell.Services;

/// <summary>
/// 命令补全链路的临时诊断日志:设 VELASHELL_SUGGEST_DIAG=1 时把各环节写到
/// %TEMP%\velashell-suggest.log(定位"弹层不弹"这类只在真机出现的问题)。
/// 默认完全关闭,零开销。
/// </summary>
public static class SuggestDiag
{
    private static readonly bool Enabled = Environment.GetEnvironmentVariable("VELASHELL_SUGGEST_DIAG") == "1";
    private static readonly string LogPath = Path.Combine(Path.GetTempPath(), "velashell-suggest.log");
    private static readonly Lock Gate = new();

    public static void Log(string stage, string detail)
    {
        if (!Enabled)
        {
            return;
        }
        try
        {
            lock (Gate)
            {
                File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff} [{stage}] {detail}{Environment.NewLine}");
            }
        }
        catch
        {
            // 诊断日志绝不影响主流程。
        }
        Debug.WriteLine($"[suggest:{stage}] {detail}");
    }
}

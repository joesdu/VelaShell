using System.Diagnostics;
using VelaShell.Infrastructure.Persistence;

namespace VelaShell.Services;

/// <summary>
/// 远程编辑会话的诊断日志:~/.velashell/logs/remote-edit-yyyyMMdd.log。
/// </summary>
/// <remarks>
/// <b>默认常开。</b>这条链路的失败形态是「保存了但什么都没发生」—— 没有异常、没有界面反馈,
/// 用户能提供的只有一句"没上传"(#396 就是这么来的:报的是自动上传失效,真凶是双击走了
/// 另一条根本没有监视的入口,靠截图里的临时路径才认出来)。写量很小(每次保存两三行),
/// 换来的是下次这类 issue 能直接看到「watcher 有没有收到事件、上传有没有发起、失败在哪一步」。
/// 每天单独写文件,单日超过 <see cref="MaxBytes" /> 就截断当天文件;旧日期由启动清理删除。
/// </remarks>
internal static class RemoteEditLog
{
    /// <summary>单份日志的体积上限;超过就截断重开。</summary>
    private const long MaxBytes = 1024 * 1024;

    private static readonly Lock Gate = new();

    private static string LogDirectory => Path.Combine(new VelaShellStoragePaths().RootDirectory, "logs");

    private static string LogPath => Path.Combine(LogDirectory, $"remote-edit-{DateTime.Now:yyyyMMdd}.log");

    /// <summary>记一行。失败静默 —— 诊断日志绝不影响编辑与上传本身。</summary>
    /// <param name="stage">环节标签(open / watch / upload / close ...)。</param>
    /// <param name="detail">这一环节的详情。</param>
    public static void Write(string stage, string detail)
    {
        try
        {
            string path = LogPath;
            lock (Gate)
            {
                Directory.CreateDirectory(LogDirectory);
                if (File.Exists(path) && new FileInfo(path).Length > MaxBytes)
                {
                    File.WriteAllText(path, string.Empty);
                }
                File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{stage}] {detail}{Environment.NewLine}");
            }
        }
        catch
        {
            // 日志写不进去(只读目录、磁盘满)不该拖累主流程。
        }
        Debug.WriteLine($"[remote-edit:{stage}] {detail}");
    }

    /// <summary>删除超过默认保留期的远程编辑日志。</summary>
    internal static void CleanupExpired(int retentionDays = 7)
    {
        if (retentionDays < 1)
        {
            return;
        }
        try
        {
            DateTime cutoff = DateTime.Now.AddDays(-retentionDays);
            foreach (string file in Directory.EnumerateFiles(LogDirectory, "remote-edit-*.log"))
            {
                try
                {
                    if (File.GetLastWriteTime(file) < cutoff)
                    {
                        File.Delete(file);
                    }
                }
                catch
                {
                    // 单个文件占用或无权限时,下次启动再试。
                }
            }
        }
        catch
        {
            // 日志清理失败不影响启动。
        }
    }
}

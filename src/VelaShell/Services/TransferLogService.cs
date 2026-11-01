using System.Text;
using VelaShell.Core.Models;
using VelaShell.Core.Sftp;
using VelaShell.Core.Ssh;

namespace VelaShell.Services;

/// <summary>
/// 传输日志(设置 → 文件传输 → 日志记录):每天一个 transfer-yyyyMMdd.log,
/// 一行一条(时间 / 方向 / 本地路径 / 远程路径 / 结果)。写入失败静默。
/// </summary>
public static class TransferLogService
{
    private static readonly Lock Gate = new();

    /// <summary>
    /// 解析配置的日志目录("~" 与相对路径均以用户目录为基准,见 <see cref="UserPathResolver" />);
    /// 空则退回默认 ~/.velashell/logs。诊断包按它找传输记录(目录可以改到别处)。
    /// </summary>
    internal static string ResolveDirectory(string? configured) =>
        UserPathResolver.Resolve(configured, SessionLogService.LogDirectory);

    /// <summary>向当天的 transfer 日志追加一条传输记录(写入失败静默忽略)。</summary>
    public static void Append(string? configuredDirectory, TransferType type, string localPath, string remotePath, TransferStatus status)
    {
        try
        {
            string dir = ResolveDirectory(configuredDirectory);
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, $"transfer-{DateTime.Now:yyyyMMdd}.log");
            string line = string.Join('\t',
                              DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                              type switch
                              {
                                  TransferType.Upload => "UPLOAD",
                                  TransferType.Copy => "COPY",
                                  TransferType.Relay => "RELAY",
                                  _ => "DOWNLOAD",
                              },
                              localPath,
                              remotePath,
                              status.ToString().ToUpperInvariant()) +
                          Environment.NewLine;
            lock (Gate)
            {
                File.AppendAllText(file, line, Encoding.UTF8);
            }
        }
        catch
        {
            // 日志失败不影响传输本身。
        }
    }

    /// <summary>每次失败尝试的安全诊断。刻意不写异常消息、文件路径、主机或服务端回复正文。</summary>
    public static void AppendFailure(string? configuredDirectory, Guid transferId, TransferType type,
        int attempt, long startOffset, bool movedBytes, bool willRetry, Exception error)
    {
        try
        {
            string dir = ResolveDirectory(configuredDirectory);
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, $"transfer-{DateTime.Now:yyyyMMdd}.log");
            var details = new List<string>();
            // 只允许固定格式的类型标识和数值;异常消息可能带服务器路径、口令或私有协议字段。
            var pending = new Queue<Exception>();
            var seen = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
            pending.Enqueue(error);
            while (seen.Count < 8 && pending.TryDequeue(out Exception? current))
            {
                if (!seen.Add(current)) continue;
                if (current is AggregateException aggregate)
                {
                    foreach (Exception child in aggregate.InnerExceptions.Take(8)) pending.Enqueue(child);
                }
                else if (current.InnerException is { } nested)
                {
                    pending.Enqueue(nested);
                }
                string name = new([.. current.GetType().Name.Where(c => char.IsAsciiLetterOrDigit(c) || c == '_').Take(100)]);
                details.Add($"{name}:0x{current.HResult:X8}");
                if (current is RemoteTransferLengthException length)
                {
                    details.Add($"expected={length.ExpectedBytes},actual={length.ActualBytes}");
                }
                if (current is VelaSftpTransferInterruptedException interrupted)
                {
                    details.Add($"durable={interrupted.DurableLength},refused={interrupted.RefusedByServer}");
                }
                if (VelaSftpUploadFailureDetails.Get(current) is { } target)
                {
                    details.Add($"targetDurable={target.DurableLength}");
                    if (target.CleanupException is { } cleanup)
                    {
                        string cleanupName = new([.. cleanup.GetType().Name.Where(c => char.IsAsciiLetterOrDigit(c) || c == '_').Take(100)]);
                        details.Add($"cleanup={cleanupName}:0x{cleanup.HResult:X8}");
                    }
                }
            }
            if (pending.Count > 0) details.Add("truncated=True");
            string stage = error switch
            {
                RelayCacheException => "local-cache",
                RemoteTransferLengthException => "source-length",
                VelaSftpTransferInterruptedException => "target-write-or-close",
                Core.Ftp.VelaFtpTransferInterruptedException => "ftp-data-connection",
                Core.Ftp.VelaFtpClientException => "ftp-operation",
                _ => "transfer",
            };
            string line = string.Join('\t', DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                "ATTEMPT_FAILED", transferId.ToString("N"), type.ToString().ToUpperInvariant(), $"stage={stage}",
                $"attempt={attempt}", $"start={startOffset}", $"moved={movedBytes}", $"retry={willRetry}",
                $"kind={TransferRetryPolicy.Classify(error)}", string.Join(';', details)) + Environment.NewLine;
            lock (Gate)
            {
                File.AppendAllText(file, line, Encoding.UTF8);
            }
        }
        catch
        {
            // 与最终传输记录一样,日志失败不影响传输。
        }
    }

    /// <summary>删除超过保留天数的 transfer-*.log(启动时后台执行)。</summary>
    public static void CleanupExpired(string? configuredDirectory, int retentionDays)
    {
        if (retentionDays < 1)
        {
            return;
        }
        _ = Task.Run(() =>
        {
            try
            {
                string dir = ResolveDirectory(configuredDirectory);
                if (!Directory.Exists(dir))
                {
                    return;
                }
                DateTime cutoff = DateTime.Now.AddDays(-retentionDays);
                foreach (string file in Directory.EnumerateFiles(dir, "transfer-*.log"))
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
                        // 单个文件占用/无权限,跳过。
                    }
                }
            }
            catch
            {
                // 清理失败不影响启动。
            }
        });
    }
}

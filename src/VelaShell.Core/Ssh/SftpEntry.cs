namespace VelaShell.Core.Ssh;

/// <summary>
/// 库中立的远程目录条目:<see cref="ISftpClientWrapper" /> 的列目录结果不再暴露具体
/// SSH 库的文件类型(曾是 SSH.NET 的 ISftpFile),更换底层库(Tmds.Ssh/自研)时
/// 只需替换 Infrastructure 侧的映射。字段即上层实际消费的全集(见 SftpService)。
/// </summary>
public sealed record SftpEntry
{
    public required string Name { get; init; }

    /// <summary>绝对路径。</summary>
    public required string FullName { get; init; }

    /// <summary>文件字节数(目录为 0 或服务器报告值)。</summary>
    public long Length { get; init; }

    public bool IsDirectory { get; init; }

    public DateTime LastWriteTime { get; init; }

    public int UserId { get; init; }

    public int GroupId { get; init; }

    public bool OwnerCanRead { get; init; }
    public bool OwnerCanWrite { get; init; }
    public bool OwnerCanExecute { get; init; }
    public bool GroupCanRead { get; init; }
    public bool GroupCanWrite { get; init; }
    public bool GroupCanExecute { get; init; }
    public bool OthersCanRead { get; init; }
    public bool OthersCanWrite { get; init; }
    public bool OthersCanExecute { get; init; }
}

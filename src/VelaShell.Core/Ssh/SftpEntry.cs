namespace VelaShell.Core.Ssh;

/// <summary>
/// 库中立的远程目录条目:<see cref="ISftpClientWrapper" /> 的列目录结果不再暴露具体
/// SSH 库的文件类型(曾是 SSH.NET 的 ISftpFile),更换底层库(Tmds.Ssh/自研)时
/// 只需替换 Infrastructure 侧的映射。字段即上层实际消费的全集(见 SftpService)。
/// </summary>
public sealed record SftpEntry
{
    /// <summary>条目名称(不含路径)。</summary>
    public required string Name { get; init; }

    /// <summary>绝对路径。</summary>
    public required string FullName { get; init; }

    /// <summary>文件字节数(目录为 0 或服务器报告值)。</summary>
    public long Length { get; init; }

    /// <summary>该条目是否为目录。</summary>
    public bool IsDirectory { get; init; }

    /// <summary>最后修改时间。</summary>
    public DateTime LastWriteTime { get; init; }

    /// <summary>属主用户 Id(UID)。</summary>
    public int UserId { get; init; }

    /// <summary>属组 Id(GID)。</summary>
    public int GroupId { get; init; }

    /// <summary>属主是否有读权限。</summary>
    public bool OwnerCanRead { get; init; }
    /// <summary>属主是否有写权限。</summary>
    public bool OwnerCanWrite { get; init; }
    /// <summary>属主是否有执行权限。</summary>
    public bool OwnerCanExecute { get; init; }
    /// <summary>属组是否有读权限。</summary>
    public bool GroupCanRead { get; init; }
    /// <summary>属组是否有写权限。</summary>
    public bool GroupCanWrite { get; init; }
    /// <summary>属组是否有执行权限。</summary>
    public bool GroupCanExecute { get; init; }
    /// <summary>其他用户是否有读权限。</summary>
    public bool OthersCanRead { get; init; }
    /// <summary>其他用户是否有写权限。</summary>
    public bool OthersCanWrite { get; init; }
    /// <summary>其他用户是否有执行权限。</summary>
    public bool OthersCanExecute { get; init; }
}

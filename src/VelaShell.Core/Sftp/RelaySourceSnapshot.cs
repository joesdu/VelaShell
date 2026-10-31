using VelaShell.Core.Models;

namespace VelaShell.Core.Sftp;

/// <summary>
/// 中转源的一瞬间的元数据:大小与修改时间。有了它,一次批量的每一轮(自动重试、同批后续文件)
/// 都不必再回到源端 stat —— 源端只允许一条连接时,FTP 控制连接上的每一次往返都在与传输抢那条连接。
/// </summary>
/// <remarks>
/// 眼下只有测试把它交给 RemoteRelay(<c>knownSource</c>);产品里的中转不传,每一次尝试照旧在源端 stat(见 RelaySourceCache)。
/// </remarks>
/// <param name="Size">源文件大小;没问到(服务器不支持 SIZE)时为 0。</param>
/// <param name="LastModified">源文件修改时间;没取到时为 <see cref="DateTime.MinValue" />。</param>
public sealed record RelaySourceSnapshot(long Size, DateTime LastModified)
{
    /// <summary>由一次 stat 的结果建出。</summary>
    /// <param name="info">源文件的元数据。</param>
    public static RelaySourceSnapshot From(RemoteFileInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        return new(Math.Max(0, info.Size), info.LastModified);
    }
}

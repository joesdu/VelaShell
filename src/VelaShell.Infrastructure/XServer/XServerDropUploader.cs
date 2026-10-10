using System.Globalization;
using VelaShell.Core.Models;
using VelaShell.Core.Sftp;
using VelaShell.Core.Ssh;

namespace VelaShell.Infrastructure.XServer;

/// <summary>
/// 本机的文件拖进经 SSH 转发来的 X 程序的窗口(F16):文件在本机,程序在远端,远端程序打不开本机路径 ——
/// 先经那个 SSH 会话的 SFTP 传到远端的一个临时目录,再把<b>远端路径</b>交给程序(XDND 的 <c>text/uri-list</c>)。
/// 这是同时有 SSH、SFTP 与 X 服务端才做得到的结合。
/// </summary>
public interface IXServerDropUploader
{
    /// <summary>
    /// 这个连接名(X 程序的 <see cref="VelaShell.XServer.XTopLevelSnapshot.ClientLabel" />,形如 <c>user@host:22</c>)对应的 SSH 会话还连着吗
    /// —— 连着才传得上去。
    /// </summary>
    /// <param name="clientLabel">连接名。</param>
    bool CanUpload(string clientLabel);

    /// <summary>
    /// 把本机的文件与目录(目录连同里面的文件)传到远端一个新建的临时目录,返回远端路径(与 <paramref name="localPaths" /> 同序;
    /// 本机已经不存在的跳过)。会话不在时为 <see langword="null" />。传输失败抛出底层的异常。
    /// </summary>
    /// <param name="clientLabel">连接名(见 <see cref="CanUpload" />)。</param>
    /// <param name="localPaths">本机的文件或目录。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<IReadOnlyList<string>?> UploadAsync(string clientLabel, IReadOnlyList<string> localPaths, CancellationToken cancellationToken = default);
}

/// <summary>
/// <see cref="IXServerDropUploader" /> 的实现:按连接名找连着的 SSH 会话(连接器给的名字就是 <c>用户名@主机:端口</c>),
/// 经 <see cref="ISftpService" /> 传到 <c>/tmp/velashell-drop-随机串/</c>(目录权限 0700,别的用户看不到;文件留在那里,
/// 远端程序打开之后还要用,不替它删)。
/// </summary>
public sealed class SftpXServerDropUploader(ISshConnectionService connections, ISftpService sftp) : IXServerDropUploader
{
    private readonly ISshConnectionService _connections = connections ?? throw new ArgumentNullException(nameof(connections));
    private readonly ISftpService _sftp = sftp ?? throw new ArgumentNullException(nameof(sftp));

    /// <summary>连接器给连接起的名字(与 SSH 包装器的 <c>target</c> 同一个写法)。</summary>
    internal static string LabelOf(ConnectionInfo info) =>
        $"{info.Username}@{info.Host}:{info.Port.ToString(CultureInfo.InvariantCulture)}";

    private SshSession? SessionFor(string clientLabel) =>
        _connections.Sessions.FirstOrDefault(s => s.Status == SessionStatus.Connected && LabelOf(s.ConnectionInfo) == clientLabel);

    /// <inheritdoc />
    public bool CanUpload(string clientLabel) => SessionFor(clientLabel) is not null;

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>?> UploadAsync(string clientLabel, IReadOnlyList<string> localPaths, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(localPaths);
        if (SessionFor(clientLabel) is not { } session)
        {
            return null;
        }
        Guid id = session.SessionId;
        string directory = $"/tmp/velashell-drop-{Guid.NewGuid():N}";
        await _sftp.CreateDirectoryAsync(id, directory, cancellationToken).ConfigureAwait(false);
        await _sftp.SetPermissionsAsync(id, directory, 700, cancellationToken).ConfigureAwait(false);   // chmod 记法(见 ISftpService)
        List<string> remote = [];
        HashSet<string> names = new(StringComparer.Ordinal);
        foreach (string local in localPaths)
        {
            string name = UniqueName(names, Path.GetFileName(local.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));
            string target = $"{directory}/{name}";
            if (File.Exists(local))
            {
                await _sftp.UploadFileAsync(id, local, target, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            else if (Directory.Exists(local))
            {
                await UploadDirectoryAsync(id, local, target, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                continue;   // 拖着的时候还在,放下时已经没了
            }
            remote.Add(target);
        }
        return remote;
    }

    private async Task UploadDirectoryAsync(Guid id, string local, string remote, CancellationToken cancellationToken)
    {
        await _sftp.EnsureDirectoryAsync(id, remote, cancellationToken).ConfigureAwait(false);
        foreach (string file in Directory.EnumerateFiles(local))
        {
            await _sftp.UploadFileAsync(id, file, $"{remote}/{Path.GetFileName(file)}", cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        foreach (string sub in Directory.EnumerateDirectories(local))
        {
            await UploadDirectoryAsync(id, sub, $"{remote}/{Path.GetFileName(sub)}", cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>同一次拖放里同名的(不同目录下的 a.txt)加序号区分:<c>a.txt</c>、<c>a (2).txt</c>……</summary>
    private static string UniqueName(HashSet<string> names, string name)
    {
        if (name.Length == 0)
        {
            name = "dropped";
        }
        string candidate = name;
        for (int i = 2; !names.Add(candidate); i++)
        {
            candidate = $"{Path.GetFileNameWithoutExtension(name)} ({i.ToString(CultureInfo.InvariantCulture)}){Path.GetExtension(name)}";
        }
        return candidate;
    }
}

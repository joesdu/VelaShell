using VelaShell.Core.Data;
using VelaShell.Core.Import;
using VelaShell.Core.Models;
using VelaShell.Ssh.Config;

namespace VelaShell.Infrastructure.Import;

/// <summary>
/// OpenSSH <c>~/.ssh/config</c> 的 <see cref="ISessionImportService" /> 实现:定位配置文件、
/// 按 OpenSSH 的取值规则解析每个主机别名(<c>HostName</c> / <c>Port</c> / <c>User</c> /
/// <c>IdentityFile</c> / <c>ProxyJump</c>),把选中的会话写入 <see cref="ISessionRepository" />。
/// </summary>
/// <remarks>
/// <para>
/// 与 Xshell / WinSCP 两个来源不同,这里**没有密码可还原** —— OpenSSH 的配置文件从不存密码,
/// 认证要么靠 <c>IdentityFile</c> 指的私钥,要么交互式输入。因此每条会话的
/// <see cref="ImportedSession.HasEncryptedPassword" /> 恒为 <c>false</c>,
/// 有 <c>IdentityFile</c> 的则带上私钥路径以私钥认证方式落盘。
/// </para>
/// <para>
/// 只导入**字面量别名**:<c>Host *</c>、<c>Host *.internal</c> 这类通配块是给别人兜底用的模板,
/// 本身不是一台可连的机器;它们的选项会通过取值规则渗到具名别名上,这正是 OpenSSH 的语义。
/// </para>
/// </remarks>
public sealed class SshConfigImportService(ISessionRepository repository) : ISessionImportService
{
    private readonly ISessionRepository _repository = repository ?? throw new ArgumentNullException(nameof(repository));

    /// <summary>写入会话时打的标签,同时用作分组名兜底。</summary>
    internal const string Tag = "ssh-config";

    /// <inheritdoc />
    public string SourceKey => "SSH config";

    /// <inheritdoc />
    public ImportBrowseKind BrowseKind => ImportBrowseKind.File;

    /// <inheritdoc />
    public string? DetectDefaultSource()
    {
        string path = Path.Combine(SshPathResolver.SshDirectory, "config");
        return File.Exists(path) ? path : null;
    }

    /// <inheritdoc />
    public async Task<SessionImportScan> ScanAsync(string? source, CancellationToken cancellationToken = default)
    {
        string? path = string.IsNullOrWhiteSpace(source) ? DetectDefaultSource() : source;
        if (path is null || !File.Exists(path))
        {
            return new SessionImportScan { Source = path ?? string.Empty, Items = [] };
        }

        // Include 的相对路径以 ~/.ssh 为基准(OpenSSH 用户配置的规则),而不是配置文件自身所在目录 ——
        // 用户手动指定了别处的配置文件时,里面的 `Include conf.d/*` 仍指向 ~/.ssh/conf.d。
        // 解析、Include 展开、Match 判定都用 SSH 库的 SshConfigFile,宿主不另写一份。
        string baseDirectory = SshPathResolver.SshDirectory;
        IReadOnlyList<SshConfigBlock> blocks = await SshConfigFile
            .LoadAsync(path, includeDirectory: baseDirectory, cancellationToken).ConfigureAwait(false);

        List<SessionProfile> existing = await _repository.GetAllSessionsAsync().ConfigureAwait(false);
        var existingKeys = existing
            .Select(static s => DedupKey(s.Host, s.Port, s.Username))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var items = new List<ImportedSession>();
        foreach (string alias in HostAliases(blocks))
        {
            cancellationToken.ThrowIfCancellationRequested();

            // 静态导入没有「以谁的身份、从哪台机器连」的上下文:Match 里判不了的条件
            // (user、localuser、exec)按不成立处理,那种块的选项不会渗到会话上。
            SshHostConfig options = SshConfigFile.Resolve(blocks, alias);

            // HostName 缺省即别名本身 —— `Host build01` 不写 HostName 时,ssh 直接连 build01。
            string host = Value(options.First("HostName")) is { } hostName
                ? ExpandTokens(hostName, alias)
                : alias;
            if (host.Length == 0)
            {
                continue;
            }

            int port = options.Port is > 0 and <= 65535 ? options.Port : 22;
            string user = Value(options.User) ?? string.Empty;
            string? keyPath = ResolveIdentityFile(Value(options.First("IdentityFile")), baseDirectory);
            string? jump = SshConfigFile.ParseProxyJump(options.ProxyJump) is [.., SshProxyJumpHop last]
                ? last.Host
                : null;

            items.Add(new ImportedSession
            {
                Name = alias,
                Host = host,
                Port = port,
                Username = user,
                ConnectionType = ConnectionType.SSH,
                Protocol = "SSH",
                IsSupported = true,
                HasEncryptedPassword = false,
                Password = null,
                PrivateKeyPath = keyPath,
                JumpHostAlias = jump,
                AlreadyExists = existingKeys.Contains(DedupKey(host, port, user)),
                Source = path
            });
        }

        return new SessionImportScan
        {
            Source = path,
            Items = items,
            MasterPasswordEnabled = false
        };
    }

    /// <inheritdoc />
    public async Task<SessionImportOutcome> ImportAsync(IReadOnlyList<ImportedSession> items, string groupName, CancellationToken cancellationToken = default) =>
        await SessionImportWriter.WriteAsync(_repository, items, groupName, Tag, cancellationToken).ConfigureAwait(false);

    /// <summary>去掉首尾空白;空串当作没有。</summary>
    private static string? Value(string? value) => value?.Trim() is { Length: > 0 } trimmed ? trimmed : null;

    /// <summary>
    /// 可作为会话导入的主机别名:<c>Host</c> 行上不含通配与取反的模式,按首次出现去重。
    /// </summary>
    /// <remarks><c>Match</c> 块没有 <c>Host</c> 模式,自然不产生别名。</remarks>
    private static List<string> HostAliases(IReadOnlyList<SshConfigBlock> blocks)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var aliases = new List<string>();
        foreach (SshConfigBlock block in blocks)
        {
            if (block.Match is not null)
            {
                continue;
            }
            foreach (string pattern in block.Patterns)
            {
                if (IsLiteralAlias(pattern) && seen.Add(pattern))
                {
                    aliases.Add(pattern);
                }
            }
        }
        return aliases;
    }

    /// <summary>别名是否是「字面量」:不含 <c>*</c> / <c>?</c> 通配,也不是取反模式。</summary>
    private static bool IsLiteralAlias(string pattern) =>
        pattern.Length > 0 && !pattern.StartsWith('!') && pattern.AsSpan().IndexOfAny('*', '?') < 0;

    /// <summary>
    /// 展开 <c>HostName</c> 里的 <c>%h</c>(原始别名);其余记号(<c>%r</c>、<c>%p</c> 等)要到连接时
    /// 才有值,原样留着比猜一个错的强。
    /// </summary>
    private static string ExpandTokens(string value, string alias) => value.Replace("%h", alias, StringComparison.Ordinal);

    /// <summary>
    /// 解析 <c>IdentityFile</c>:展开 <c>~</c> 与相对路径。文件不存在也照样带上 ——
    /// 密钥可能在另一台机器上,或者用户正打算补进来;把路径留在配置里比悄悄丢掉更有用。
    /// </summary>
    private static string? ResolveIdentityFile(string? value, string baseDirectory)
    {
        if (value is null)
        {
            return null;
        }
        // `IdentityFile none` 是显式关闭,不是路径。
        if (value.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        string expanded = SshPathResolver.Expand(value, baseDirectory);
        return expanded.Length > 0 ? expanded : null;
    }

    private static string DedupKey(string host, int port, string user) => $"{host.Trim()}|{port}|{user.Trim()}";
}

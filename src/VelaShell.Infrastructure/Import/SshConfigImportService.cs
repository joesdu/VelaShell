using VelaShell.Core.Data;
using VelaShell.Core.Import;
using VelaShell.Core.Models;

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
        string baseDirectory = SshPathResolver.SshDirectory;
        IReadOnlyList<SshConfigBlock> blocks = await Task.Run(
            () => SshConfigParser.ParseFile(path, baseDirectory), cancellationToken).ConfigureAwait(false);

        List<SessionProfile> existing = await _repository.GetAllSessionsAsync().ConfigureAwait(false);
        var existingKeys = existing
            .Select(static s => DedupKey(s.Host, s.Port, s.Username))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var items = new List<ImportedSession>();
        foreach (string alias in SshConfigParser.CollectHostAliases(blocks))
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyDictionary<string, string> options = SshConfigParser.ResolveOptions(blocks, alias);

            // HostName 缺省即别名本身 —— `Host build01` 不写 HostName 时,ssh 直接连 build01。
            string host = Value(options, "HostName") is { Length: > 0 } hostName
                ? ExpandTokens(hostName, alias)
                : alias;
            if (host.Length == 0)
            {
                continue;
            }

            int port = int.TryParse(Value(options, "Port"), out int parsed) && parsed is > 0 and <= 65535 ? parsed : 22;
            string user = Value(options, "User") ?? string.Empty;
            string? keyPath = ResolveIdentityFile(Value(options, "IdentityFile"), baseDirectory);
            string? jump = ParseProxyJump(Value(options, "ProxyJump"));

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

    /// <summary>取一个关键字的值;不存在或为空时返回 <c>null</c>。</summary>
    private static string? Value(IReadOnlyDictionary<string, string> options, string key) =>
        options.TryGetValue(key, out string? value) && value.Trim() is { Length: > 0 } trimmed ? trimmed : null;

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

    /// <summary>
    /// 解析 <c>ProxyJump</c>,返回**直接跳板**的别名。
    /// <para>
    /// <c>ProxyJump a,b</c> 的语义是「经 a 到 b、再由 b 抵达目标」,因此离目标最近的一跳是**最后一个**;
    /// VelaShell 的跳板是逐条链式引用(每条配置各指自己的上一跳),取最后一跳才对得上。
    /// 更前面的跳板由 a、b 各自的配置继续串,前提是它们也在这份 config 里。
    /// </para>
    /// </summary>
    private static string? ParseProxyJump(string? value)
    {
        if (value is null || value.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        string last = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) is { Length: > 0 } hops
            ? hops[^1]
            : string.Empty;
        if (last.Length == 0)
        {
            return null;
        }
        // 跳板可写成 user@host:port;别名匹配只认 host 这一段。
        int at = last.LastIndexOf('@');
        if (at >= 0)
        {
            last = last[(at + 1)..];
        }
        if (last.StartsWith('['))
        {
            // IPv6 字面量:方括号内整段都是地址,后面才可能跟 :port。
            int close = last.IndexOf(']', StringComparison.Ordinal);
            last = close > 1 ? last[1..close] : string.Empty;
        }
        else if (last.IndexOf(':', StringComparison.Ordinal) is int colon and > 0 && last.LastIndexOf(':') == colon)
        {
            last = last[..colon]; // 只有一个冒号才是端口分隔符;多个冒号是没加方括号的 IPv6,整段留着。
        }
        return last.Length > 0 ? last : null;
    }

    private static string DedupKey(string host, int port, string user) => $"{host.Trim()}|{port}|{user.Trim()}";
}

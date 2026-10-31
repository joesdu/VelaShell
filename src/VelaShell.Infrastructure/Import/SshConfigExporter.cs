using System.Globalization;
using System.Text;
using VelaShell.Core.Credentials;
using VelaShell.Core.Import;
using VelaShell.Core.Models;
using VelaShell.Core.Net;
using VelaShell.Core.Ssh;
using VelaShell.Infrastructure.Ssh;

namespace VelaShell.Infrastructure.Import;

/// <summary>写成 <c>~/.ssh/config</c> 的结果。</summary>
/// <param name="Content">ssh_config 文本(每行以 <c>\n</c> 结尾)。</param>
/// <param name="Exported">写出的 Host 块数。</param>
/// <param name="Skipped">不是 SSH / SFTP、写不进 ssh_config 的连接数。</param>
public sealed record SshConfigExport(string Content, int Exported, int Skipped);

/// <summary>
/// 把一批连接写成 OpenSSH 的 <c>ssh_config</c>:<c>~/.ssh/config</c> 导入(<see cref="SshConfigImportService" />)的反方向,
/// 与命令行 ssh 共用同一份连接清单。
/// </summary>
/// <remarks>
/// <para>
/// 一条 SSH / SFTP 连接一个 <c>Host</c> 块,别名取连接名(空白与 ssh_config 里另有含义的字符换成 <c>-</c>,重名加序号);
/// 写的是导入那边认得的同一批关键字:<c>HostName</c> / <c>Port</c> / <c>User</c> / <c>IdentityFile</c> / <c>CertificateFile</c> /
/// <c>ProxyJump</c>(跳板随导出范围一起带上,按别名引用)/ <c>Compression</c> / <c>IdentityAgent</c>(agent 认证;系统默认的那个写 <c>SSH_AUTH_SOCK</c>)/
/// <c>ForwardAgent</c>(按连接指定了 agent 端点时写端点)/ <c>ForwardX11</c>(连同 <c>ForwardX11Trusted</c>)/
/// <c>ObscureKeystrokeTiming</c>(开着才写)/ 四个算法清单 / 三种转发(含 Unix 套接字,远程动态连同 <c>PermitRemoteOpen</c>)。<b>密码与口令从不写</b> —— ssh_config 本来就不存。
/// </para>
/// <para>
/// 「放开老算法」在 ssh_config 里没有对应的开关:没写自定义清单的类别写成 <c>+老算法</c>;写了 <c>+</c> 的并成一条;
/// 写了 <c>-</c> / <c>^</c> 的(OpenSSH 一条里只能有一种前缀)写成算出来的完整清单;它放开的 SHA-1 RSA 用户签名写成
/// <c>PubkeyAcceptedAlgorithms +ssh-rsa</c>。主目录下的路径写成 <c>~/…</c>,换台机器也找得到。
/// </para>
/// </remarks>
public static class SshConfigExporter
{
    /// <summary>写一份 ssh_config。</summary>
    /// <param name="archive">导出范围(<see cref="SessionArchiveBuilder.Build" /> 的产物:连接、共享凭据、隧道)。</param>
    /// <param name="homeDirectory">用户主目录(路径换成 <c>~</c>);<see langword="null" /> 取当前用户的。</param>
    public static SshConfigExport Write(SessionArchive archive, string? homeDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(archive);
        string home = homeDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        List<SessionProfile> sessions = [.. archive.Sessions.Where(static s => s.ConnectionType is ConnectionType.SSH or ConnectionType.SFTP)];
        HashSet<string> used = [with(StringComparer.OrdinalIgnoreCase)];
        Dictionary<Guid, string> aliases = [];
        foreach (SessionProfile session in sessions)
        {
            aliases[session.Id] = UniqueAlias(session, used);
        }

        StringBuilder text = new();
        text.Append("# Exported by ").Append(archive.Application ?? "VelaShell").Append(" on ")
            .Append(archive.ExportedAtUtc.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture)).Append('\n')
            .Append("# Passwords and passphrases are never written.\n");

        foreach (SessionProfile session in sessions)
        {
            text.Append('\n');
            WriteHost(text, session, aliases, archive, home);
        }

        return new SshConfigExport(text.ToString(), sessions.Count, archive.Sessions.Count - sessions.Count);
    }

    private static void WriteHost(StringBuilder text, SessionProfile session, Dictionary<Guid, string> aliases, SessionArchive archive, string home)
    {
        void Line(string keyword, string value) => text.Append("    ").Append(keyword).Append(' ').Append(value).Append('\n');

        text.Append("Host ").Append(aliases[session.Id]).Append('\n');
        Line("HostName", HostAddress.Normalize(session.Host));
        if (session.Port != 22)
        {
            Line("Port", session.Port.ToString(CultureInfo.InvariantCulture));
        }

        // 引用了共享凭据的:认证材料在凭据里(连接里那几个字段不落盘)。用户名填了的以连接为准,与连接时一致。
        SharedCredential? shared = session.CredentialSource is { } reference && reference.TryGetSharedId(out Guid id)
            ? archive.SharedCredentials?.FirstOrDefault(c => c.Id == id)
            : null;
        string user = session.Username.Length > 0 ? session.Username : shared?.Username ?? string.Empty;
        if (user.Length > 0)
        {
            Line("User", Quote(user));
        }

        AuthMethod auth = shared?.AuthMethod ?? session.AuthMethod;
        string? key = shared is null ? session.PrivateKeyPath : shared.PrivateKeyPath;
        string? certificate = shared is null ? session.CertificatePath : shared.CertificatePath;
        if (auth is AuthMethod.PrivateKey or AuthMethod.Certificate && key is { Length: > 0 })
        {
            Line("IdentityFile", Quote(Portable(key, home)));
        }
        if (auth == AuthMethod.Certificate && certificate is { Length: > 0 })
        {
            Line("CertificateFile", Quote(Portable(certificate, home)));
        }
        if (auth == AuthMethod.KeyStore && session.KeyStoreKey is { } keyStoreKey)
        {
            // 系统密钥库的钥没有文件,OpenSSH 的 IdentityFile 用不了它:写成注释,看这份配置的人知道这台原来用的是哪把钥。
            text.Append("    # ").Append(keyStoreKey.ToText())
                .Append("  (VelaShell: a Windows key store key; OpenSSH cannot use it as an IdentityFile)\n");
            // 配了证书的照写 CertificateFile:证书是公开的文件,OpenSSH 从别处(agent、PKCS#11 的智能卡)拿到同一把钥时用得上;
            // 拿不到那把钥时 ssh 不出示它。
            if (session.CertificatePath is { Length: > 0 } keyStoreCertificate)
            {
                Line("CertificateFile", Quote(Portable(keyStoreCertificate, home)));
            }
        }

        if (session.JumpHostProfileId is { } jump && aliases.TryGetValue(jump, out string? jumpAlias))
        {
            Line("ProxyJump", jumpAlias);
        }

        // 按连接指定的 agent 端点:认证走 agent 时写成 IdentityAgent;转发时 ForwardAgent 直接写端点而不是 yes ——
        // 两处都明写,读这份配置的人(与老版本的 ssh)不必去推「yes 转发的是哪个 agent」。端点只对 agent 认证与转发有意义,别的情形不写。
        string? agentEndpoint = SshAgentEndpoints.Normalize(session.Ssh?.AgentEndpoint) is { } endpoint
            ? Quote(SshAgentEndpoints.IsPipe(endpoint) ? endpoint : Portable(SshAgentEndpoints.Resolve(endpoint, home)!, home))
            : null;
        if (auth == AuthMethod.Agent)
        {
            // 系统默认的 agent 也明写成 `IdentityAgent SSH_AUTH_SOCK`(ssh_config(5):这个字面量就是「环境变量 SSH_AUTH_SOCK 指的那个」,
            // 与不写同义,ssh 照旧):不写的话这个块里既没有 IdentityFile 也没有 IdentityAgent,再导入回来就成了密码认证。
            // 导入那边认这个字面量,与环境变量设没设无关(Windows 上通常没设)。
            Line("IdentityAgent", agentEndpoint ?? "SSH_AUTH_SOCK");
        }

        if (session.Ssh is { } ssh)
        {
            if (ssh.Compression)
            {
                Line("Compression", "yes");
            }
            if (ssh.AgentForwarding)
            {
                Line("ForwardAgent", agentEndpoint ?? "yes");
            }
            if (ssh.X11Forwarding)
            {
                // 信任级别一定明写:OpenSSH 没写时是非受信任,VelaShell 的默认是受信任,两边的默认不一样。
                Line("ForwardX11", "yes");
                Line("ForwardX11Trusted", ssh.X11Trusted ? "yes" : "no");
            }
            if (ssh.ObscureKeystrokeTiming)
            {
                // 关着的不写 `no`:这个关键字 OpenSSH 9.5 才有,更老的 ssh 读到不认识的关键字整份配置报错 ——
                // 为了一项 VelaShell 没开的东西让每个 Host 块在老版本上都用不了,不划算;没写时新版 ssh 按它自己的默认(开)。
                Line("ObscureKeystrokeTiming", "yes");
            }
            foreach ((SshAlgorithmKind kind, string keyword, string? spec) in new[]
                     {
                         (SshAlgorithmKind.KeyExchange, "KexAlgorithms", ssh.KexAlgorithms),
                         (SshAlgorithmKind.HostKey, "HostKeyAlgorithms", ssh.HostKeyAlgorithms),
                         (SshAlgorithmKind.Cipher, "Ciphers", ssh.Ciphers),
                         (SshAlgorithmKind.Mac, "MACs", ssh.Macs),
                     })
            {
                if (AlgorithmLine(kind, spec, ssh.LegacyAlgorithms) is { Length: > 0 } value)
                {
                    Line(keyword, value);
                }
            }
            if (ssh.LegacyAlgorithms)
            {
                // 「允许老算法」在 VelaShell 里还放开 SHA-1 的 RSA 用户签名(SshConnectionAssembler 的 AllowSha1RsaSignatures,
                // 不论哪种认证方式;用证书时连同 RSA 证书的 SHA-1 签名)。OpenSSH 8.8 起默认不出示它,不写这一行,
                // 只认 ssh-rsa 的老设备上 KEX 谈得下来、公钥登录却必然失败。开着才写(与 ObscureKeystrokeTiming 同一个考虑:
                // 这个关键字 OpenSSH 8.5 才有)。导入那边认这一行,映射回这个开关。
                bool certificateInUse = (auth == AuthMethod.Certificate && certificate is { Length: > 0 })
                                        || (auth == AuthMethod.KeyStore && session.CertificatePath is { Length: > 0 });
                Line("PubkeyAcceptedAlgorithms", certificateInUse ? "+ssh-rsa,ssh-rsa-cert-v01@openssh.com" : "+ssh-rsa");
            }
        }

        if (archive.Tunnels?.GetValueOrDefault(session.Id) is { Count: > 0 } tunnels)
        {
            WriteForwards(text, tunnels);
        }
    }

    /// <summary>
    /// 隧道 → 三种转发关键字。套接字那头写路径(<c>LocalForward /本机/套接字 /服务器/套接字</c>);
    /// 远程动态写成不给目标的 <c>RemoteForward</c>,放行名单写成这台主机的 <c>PermitRemoteOpen</c>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>远程动态转发一定连同 <c>PermitRemoteOpen</c> 一起写</b>:ssh_config 里没写它时 OpenSSH 按 <c>any</c> 放行,
    /// 漏写一行就把隧道里收紧过的名单放宽成全放。名单是空的(手改坏了、隧道服务也不会建)的不写。
    /// </para>
    /// <para>
    /// <c>PermitRemoteOpen</c> 在 ssh_config 里是按主机的一条,这台主机的远程动态转发只能共用一份名单:
    /// 第一条的名单写出来,名单不同的那几条改写成注释 —— 合成一份(取并集)会放宽收得更紧的那一条。
    /// 只有一头是套接字的远程转发(隧道服务不建)、未来新增的隧道类型同样只写注释。
    /// </para>
    /// </remarks>
    private static void WriteForwards(StringBuilder text, IReadOnlyList<TunnelConfig> tunnels)
    {
        void Line(string keyword, string value) => text.Append("    ").Append(keyword).Append(' ').Append(value).Append('\n');
        void Skipped(string line, string why) => text.Append("    # ").Append(line).Append("  (").Append(why).Append(")\n");

        IReadOnlyList<string>? permit = null;
        foreach (TunnelConfig tunnel in tunnels)
        {
            switch (tunnel.Type)
            {
                case TunnelType.LocalForward:
                    Line("LocalForward",
                        $"{Socket(tunnel.LocalSocketPath) ?? Endpoint(tunnel.LocalHost, tunnel.LocalPort)} " +
                        $"{Socket(tunnel.RemoteSocketPath) ?? Endpoint(tunnel.RemoteHost, tunnel.RemotePort)}");
                    break;

                case TunnelType.RemoteForward when tunnel.LocalSocketPath is not null && tunnel.RemoteSocketPath is not null:
                    Line("RemoteForward", $"{Socket(tunnel.RemoteSocketPath)} {Socket(tunnel.LocalSocketPath)}");
                    break;

                case TunnelType.RemoteForward when tunnel.LocalSocketPath is null && tunnel.RemoteSocketPath is null:
                    Line("RemoteForward", $"{Endpoint(tunnel.RemoteHost, tunnel.RemotePort)} {Endpoint(tunnel.LocalHost, tunnel.LocalPort)}");
                    break;

                case TunnelType.RemoteForward:
                    Skipped($"RemoteForward {Socket(tunnel.RemoteSocketPath) ?? Endpoint(tunnel.RemoteHost, tunnel.RemotePort)} " +
                            $"{Socket(tunnel.LocalSocketPath) ?? Endpoint(tunnel.LocalHost, tunnel.LocalPort)}",
                        "VelaShell: a remote forward needs Unix sockets on both ends or on neither");
                    break;

                case TunnelType.DynamicForward:
                    Line("DynamicForward", Endpoint(tunnel.LocalHost, tunnel.LocalPort));
                    break;

                case TunnelType.RemoteDynamicForward:
                    {
                        string forward = Endpoint(tunnel.RemoteHost, tunnel.RemotePort);
                        if (tunnel.PermitRemoteOpen is not { Count: > 0 } rules)
                        {
                            Skipped($"RemoteForward {forward}", "VelaShell: no PermitRemoteOpen allow list");
                        }
                        else if (permit is null || permit.SequenceEqual(rules, StringComparer.OrdinalIgnoreCase))
                        {
                            permit ??= rules;
                            Line("RemoteForward", forward);
                        }
                        else
                        {
                            Skipped($"RemoteForward {forward}",
                                $"VelaShell: its PermitRemoteOpen ({string.Join(' ', rules)}) differs; ssh_config allows one list per host");
                        }
                        break;
                    }

                    // 别的隧道类型 ssh_config 这三个关键字表达不了,不写。
            }
        }

        if (permit is not null)
        {
            Line("PermitRemoteOpen", string.Join(' ', permit));
        }
    }

    /// <summary>套接字路径(带空白的加引号);不是套接字时为 <see langword="null" />。</summary>
    private static string? Socket(string? path) => string.IsNullOrWhiteSpace(path) ? null : Quote(path.Trim());

    /// <summary>一个算法类别写成什么;不用写(默认)时为 <see langword="null" />。规则见类型说明。</summary>
    internal static string? AlgorithmLine(SshAlgorithmKind kind, string? spec, bool legacy)
    {
        string? written = NormalizeSpec(spec);
        if (!legacy)
        {
            return written;
        }
        IReadOnlyList<string> plain = SshAlgorithmPreferences.Defaults(kind, legacy: false);
        List<string> additions = [.. SshAlgorithmPreferences.Defaults(kind, legacy: true).Where(name => !plain.Contains(name, StringComparer.Ordinal))];
        if (additions.Count == 0)
        {
            return written;
        }
        if (written is null)
        {
            return "+" + string.Join(',', additions);
        }
        return written[0] switch
        {
            // 清单里已经写了开关要追加的那几个(手写的,或者从 ssh_config 导进来的)时不重复写。
            '+' => "+" + string.Join(',', [.. additions, .. written[1..].Split(',').Where(name => !additions.Contains(name, StringComparer.Ordinal))]),
            '-' or '^' => SshAlgorithmPreferences.TryApply(kind, written, legacy: true, out IReadOnlyList<string> result, out _)
                ? string.Join(',', result)
                : written,
            _ => written,   // 整个替换:与默认清单无关,放不放开老算法都一样
        };
    }

    /// <summary>
    /// 一条清单写成 OpenSSH 读得了的样子:各项去掉空白、丢掉空项,用 <c>,</c> 连起来,开头的 <c>+</c> / <c>-</c> / <c>^</c> 留着;
    /// 一项都不剩时为 <see langword="null" />(当成没写)。
    /// </summary>
    /// <remarks>
    /// VelaShell 自己的解析(<c>SshAlgorithmSpec</c>)把逗号、空格、制表符、换行都当分隔,空项不算,所以存着的
    /// <c>a, b</c>、<c>a,b,</c> 在 VelaShell 里照样连得上(字段的悬停提示就是用 <c>", "</c> 连的默认清单,照抄过来最常见)。
    /// 原样写进 ssh_config 的话,OpenSSH 把空格后面那段当成多出来的参数、末尾的逗号当成一个空名字,报「bad configuration option」——
    /// 这对整份配置是致命的,按手册用 <c>Include</c> 并进 <c>~/.ssh/config</c> 之后,连哪台主机的 ssh 都起不来。
    /// </remarks>
    private static string? NormalizeSpec(string? spec)
    {
        if (string.IsNullOrWhiteSpace(spec))
        {
            return null;
        }
        string text = spec.Trim();
        string prefix = text[0] is '+' or '-' or '^' ? text[..1] : string.Empty;
        string[] names = text[prefix.Length..]
            .Split([',', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return names.Length == 0 ? null : prefix + string.Join(',', names);
    }

    /// <summary>别名:连接名(没有就用主机),空白与 ssh_config 里另有含义的字符换成 <c>-</c>,重名加序号。</summary>
    private static string UniqueAlias(SessionProfile session, HashSet<string> used)
    {
        string baseName = Sanitize(session.Name) is { Length: > 0 } name ? name
            : Sanitize(session.Host) is { Length: > 0 } host ? host
            : "host";
        string alias = baseName;
        for (int n = 2; !used.Add(alias); n++)
        {
            alias = string.Create(CultureInfo.InvariantCulture, $"{baseName}-{n}");
        }
        return alias;
    }

    private static string Sanitize(string value) =>
        new string([.. value.Trim().Select(static c =>
            char.IsWhiteSpace(c) || char.IsControl(c) || c is '*' or '?' or '!' or ',' or '#' or '"' or '=' or '%' ? '-' : c)])
            .Trim('-');

    /// <summary>主目录下的路径写成 <c>~/…</c>(正斜杠),换台机器、换个系统也找得到;别处的原样。</summary>
    private static string Portable(string path, string home)
    {
        if (home.Length > 0)
        {
            string prefix = home.TrimEnd('/', '\\') + Path.DirectorySeparatorChar;
            if (path.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            {
                return "~/" + path[prefix.Length..].Replace('\\', '/');
            }
        }
        return path;
    }

    /// <summary>带空白或 <c>#</c> 的值加双引号(ssh_config 认引号)。</summary>
    private static string Quote(string value) =>
        value.Any(static c => char.IsWhiteSpace(c) || c == '#') ? $"\"{value}\"" : value;

    /// <summary><c>主机:端口</c>(IPv6 加方括号);主机为空时只写端口。</summary>
    private static string Endpoint(string host, uint port) =>
        string.IsNullOrWhiteSpace(host)
            ? port.ToString(CultureInfo.InvariantCulture)
            : HostAddress.FormatEndPoint(HostAddress.Normalize(host), (int)port);
}

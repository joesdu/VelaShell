using System.Globalization;
using System.Text;
using VelaShell.Core.Data;
using VelaShell.Core.Import;
using VelaShell.Core.Models;
using VelaShell.Core.Net;
using VelaShell.Core.Resources;
using VelaShell.Core.Ssh;
using VelaShell.Core.Tunnels;
using VelaShell.Infrastructure.Ssh;
using VelaShell.Ssh.Config;
using VelaShell.Ssh.Diagnostics;

namespace VelaShell.Infrastructure.Import;

/// <summary>
/// OpenSSH <c>~/.ssh/config</c> 的 <see cref="ISessionImportService" /> 实现:定位配置文件、
/// 按 OpenSSH 的取值规则解析每个主机别名(<c>HostName</c> / <c>Port</c> / <c>User</c> /
/// <c>IdentityFile</c> / <c>CertificateFile</c> / <c>IdentityAgent</c> / <c>ProxyJump</c>、
/// <c>KexAlgorithms</c> / <c>HostKeyAlgorithms</c> / <c>Ciphers</c> / <c>MACs</c> / <c>PubkeyAcceptedAlgorithms</c>(放开 SHA-1 RSA 签名的 → 允许老算法)/
/// <c>Compression</c> / <c>ForwardAgent</c> /
/// <c>ForwardX11</c> / <c>ForwardX11Trusted</c> / <c>ObscureKeystrokeTiming</c>、
/// <c>LocalForward</c> / <c>RemoteForward</c> / <c>DynamicForward</c>),把选中的会话写入 <see cref="ISessionRepository" />。
/// </summary>
/// <remarks>
/// <para>
/// 与 Xshell / WinSCP 两个来源不同,这里**没有密码可还原** —— OpenSSH 的配置文件从不存密码,
/// 认证要么靠 <c>IdentityFile</c> 指的私钥(旁边有 <c>-cert.pub</c> 或写了 <c>CertificateFile</c> 时是证书),
/// 要么 agent,要么交互式输入。因此每条会话的 <see cref="ImportedSession.HasEncryptedPassword" /> 恒为 <c>false</c>。
/// </para>
/// <para>
/// 只导入**字面量别名**:<c>Host *</c>、<c>Host *.internal</c> 这类通配块是给别人兜底用的模板,
/// 本身不是一台可连的机器;它们的选项会通过取值规则渗到具名别名上,这正是 OpenSSH 的语义。
/// </para>
/// <para>
/// 映射不了的设置(端口 0 的转发、只有一头是套接字的远程转发、没写放行名单的远程动态转发、这台电脑上用不了的 agent 端点、
/// 与认证用的不是同一个的转发 agent、本版没实现的算法)
/// 不悄悄丢掉:写进 <see cref="ImportedSession.Notes" />,预览行里列给用户看。
/// </para>
/// </remarks>
/// <param name="repository">连接仓储。</param>
/// <param name="dataStore">通用文档存储:转发落成隧道写在这里;为 <see langword="null" /> 时不导入转发。</param>
public sealed class SshConfigImportService(ISessionRepository repository, IAppDataStore? dataStore = null) : ISessionImportService
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
            // 其中的 %h 由 SSH 库展开(SshHostConfig.HostName),宿主不另写一份。
            string host = Value(options.HostName) ?? alias;
            if (host.Length == 0)
            {
                continue;
            }

            int port = options.Port;   // SSH 库已经把配得不对的端口换成 22
            string user = Value(options.User) ?? string.Empty;
            List<string> notes = [];
            // ~ 与 %d %u %h %r 由 SSH 库展开(与连接时读私钥同一套),宿主只把相对路径落到 ~/.ssh 下。
            List<string> identities = [.. options.ExpandIdentityFiles(user)
                .Select(file => ResolveIdentityFile(file, baseDirectory)).OfType<string>()];
            (string? keyPath, string? certificatePath, AuthMethod? auth, string? identityAgent) = MapCredentials(options, user, identities, baseDirectory, notes);
            string? jump = SshConfigFile.ParseProxyJump(options.ProxyJump) is [.., SshProxyJumpHop last]
                ? last.Host
                : null;
            SshSessionOptions ssh = MapSshOptions(options, auth, identityAgent, notes);
            List<TunnelConfig> tunnels = MapForwards(options, notes);

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
                CertificatePath = certificatePath,
                AuthMethod = auth,
                Ssh = ssh.IsEmpty ? null : ssh,
                Tunnels = tunnels.Count > 0 ? tunnels : null,
                Notes = notes,
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
        await SessionImportWriter.WriteAsync(_repository, items, groupName, Tag, cancellationToken, dataStore).ConfigureAwait(false);

    /// <summary>去掉首尾空白;空串当作没有。</summary>
    private static string? Value(string? value) => value?.Trim() is { Length: > 0 } trimmed ? trimmed : null;

    /// <summary>
    /// 认证:<c>CertificateFile</c>(或 <c>IdentityFile</c> 旁边存在的 <c>-cert.pub</c>)→ 证书;<c>IdentityFile</c> → 私钥;
    /// 两样都没有而 <c>IdentityAgent</c> 指着默认的 agent、或这台电脑上用得了的别的端点 → SSH Agent(端点记进 SSH 选项);
    /// 都不沾 → 交给写入器按密码处理。
    /// </summary>
    /// <remarks>
    /// <para>
    /// OpenSSH 自己会去读 <c>&lt;IdentityFile&gt;-cert.pub</c>(ssh_config(5) 的 <c>CertificateFile</c> 一节),
    /// 所以那个文件在本机存在时同样按证书认证导入;不存在的不猜(私钥可能还没拷过来,证书就更说不准)。
    /// </para>
    /// <para>
    /// 证书配哪把私钥:<c>IdentityFile</c> 里与证书同名(去掉 <c>-cert.pub</c>)的那把优先,否则第一把;
    /// 一把都没写时按 ssh-keygen 的命名约定由证书路径反推。VelaShell 一条连接只用一种认证方式,
    /// 写了多把私钥的只取一把,其余的如实列进预览。
    /// </para>
    /// </remarks>
    private static (string? KeyPath, string? CertificatePath, AuthMethod? Auth, string? IdentityAgent) MapCredentials(
        SshHostConfig options, string user, List<string> identities, string baseDirectory, List<string> notes)
    {
        string? certificate = options.ExpandCertificateFiles(user)
            .Select(file => ResolveIdentityFile(file, baseDirectory)).OfType<string>().FirstOrDefault();
        certificate ??= identities
            .Select(identity => identity + OpenSshCertificatePaths.CertificateSuffix)
            .FirstOrDefault(FileExists);

        if (certificate is not null)
        {
            string? inferred = OpenSshCertificatePaths.InferPrivateKeyPath(certificate, static _ => true);
            string? key = identities.FirstOrDefault(identity => string.Equals(identity, inferred, StringComparison.OrdinalIgnoreCase))
                          ?? identities.FirstOrDefault()
                          ?? inferred;
            NoteExtraIdentities(identities, key, notes);
            if (key is null)
            {
                notes.Add(Strings.Get("SshCfgImport_NoteCertificateNoKey"));
            }
            return (key, certificate, AuthMethod.Certificate, IdentityAgentEndpoint(options, notes));
        }

        if (identities.Count > 0)
        {
            NoteExtraIdentities(identities, identities[0], notes);
            return (identities[0], null, null, IdentityAgentEndpoint(options, notes));
        }

        // 没有私钥与证书时,明写了 IdentityAgent(默认的那个,或这台电脑上用得了的别的端点)就按 SSH Agent 认证;
        // none、环境变量没设、用不了的端点都不用 agent(后两种进说明),交给写入器按密码处理。
        if (Value(options.First("IdentityAgent")) is not null
            && ReadIdentityAgent(options, notes) is { Usable: true } agent)
        {
            return (null, null, AuthMethod.Agent, agent.Endpoint);
        }
        return (null, null, null, null);
    }

    /// <summary>
    /// 有私钥或证书时 <c>IdentityAgent</c> 不决定认证方式(VelaShell 一条连接只用一种),但 agent 转发要用它:
    /// 与 OpenSSH 一致,<c>ForwardAgent yes</c> 转发的是 <c>IdentityAgent</c> 指的那个 agent。用不了的照样说明一句。
    /// </summary>
    private static string? IdentityAgentEndpoint(SshHostConfig options, List<string> notes) =>
        Value(options.First("IdentityAgent")) is not null && ReadIdentityAgent(options, notes) is { Usable: true } agent
            ? agent.Endpoint
            : null;

    /// <summary>
    /// 读 <c>IdentityAgent</c>(写了的前提下):<c>SSH_AUTH_SOCK</c>、等于本机 <c>SSH_AUTH_SOCK</c> 的路径 → 默认的 agent(端点 <see langword="null" />);
    /// 别的路径 / 命名管道,这台电脑上用得了 → 那个端点;<c>none</c> → 不用(<c>Usable</c> 为假,不说明);环境变量没设、端点用不了 → 不用,并说明。
    /// </summary>
    /// <remarks>路径里的 <c>~</c> 与 <c>%d %u %h %r</c>、<c>$环境变量</c> 由 SSH 库展开(<see cref="SshHostConfig.TryGetIdentityAgent" />)。</remarks>
    private static (bool Usable, string? Endpoint) ReadIdentityAgent(SshHostConfig options, List<string> notes)
    {
        string setting = Value(options.First("IdentityAgent"))!;
        if (!options.TryGetIdentityAgent(out string? endpoint))
        {
            if (setting.StartsWith('$'))
            {
                notes.Add(Strings.Format("SshCfgImport_NoteAgentVarUnset", $"IdentityAgent {setting}", setting[1..]));
            }
            return (false, null);   // IdentityAgent none:不用 agent。
        }
        return AgentEndpointFor(endpoint, $"IdentityAgent {setting}", notes);
    }

    /// <summary>
    /// 一个 agent 端点落到连接配置里的样子:没有或就是本机 <c>SSH_AUTH_SOCK</c> → 系统默认(<see langword="null" />);
    /// 这台电脑上用得了的(<see cref="SshAgentEndpoints.Check(string?)" />)→ 原样;用不了的(别的平台的路径、相对路径)→ 不用并说明。
    /// </summary>
    private static (bool Usable, string? Endpoint) AgentEndpointFor(string? endpoint, string line, List<string> notes)
    {
        if (SshAgentEndpoints.Normalize(endpoint) is not { } normalized || IsDefaultAgent(normalized))
        {
            return (true, null);
        }
        if (SshAgentEndpoints.Check(normalized) != SshAgentEndpointProblem.None)
        {
            notes.Add(Strings.Format("SshCfgImport_NoteAgentEndpointUnusable", line));
            return (false, null);
        }
        return (true, normalized);
    }

    private static void NoteExtraIdentities(List<string> identities, string? used, List<string> notes)
    {
        List<string> unused = [.. identities.Where(identity => !string.Equals(identity, used, StringComparison.OrdinalIgnoreCase))];
        if (unused.Count > 0)
        {
            notes.Add(Strings.Format("SshCfgImport_NoteExtraIdentities", string.Join(", ", unused.Select(Path.GetFileName))));
        }
    }

    /// <summary>
    /// 协议层选项:<c>Compression</c>、<c>ForwardAgent</c>、<c>ForwardX11</c> / <c>ForwardX11Trusted</c>、<c>ObscureKeystrokeTiming</c>(后三个只认明写的值),
    /// 以及四个算法清单(§134 的自定义清单,写法本来就是 ssh_config 的
    /// <c>+</c> / <c>-</c> / <c>^</c>,原样带过来;本版没实现的名字去掉,拼错的整条不导,都进预览)。
    /// </summary>
    private static SshSessionOptions MapSshOptions(SshHostConfig options, AuthMethod? auth, string? identityAgent, List<string> notes)
    {
        var ssh = new SshSessionOptions
        {
            Compression = options.Compression,
            KexAlgorithms = Algorithms(SshAlgorithmKind.KeyExchange, "KexAlgorithms", options.KexAlgorithms, notes),
            HostKeyAlgorithms = Algorithms(SshAlgorithmKind.HostKey, "HostKeyAlgorithms", options.HostKeyAlgorithms, notes),
            Ciphers = Algorithms(SshAlgorithmKind.Cipher, "Ciphers", options.Ciphers, notes),
            Macs = Algorithms(SshAlgorithmKind.Mac, "MACs", options.Macs, notes),
        };
        MapLegacyAlgorithms(options, ssh, notes);

        MapAgent(options, ssh, auth, identityAgent, notes);

        MapX11(options, ssh, notes);
        MapObscureKeystrokeTiming(options, ssh, notes);
        return ssh;
    }

    /// <summary>
    /// agent 端点(<see cref="SshSessionOptions.AgentEndpoint" />)与 <c>ForwardAgent</c>:VelaShell 一条连接只用一个 agent,「SSH Agent」认证与转发共用。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>ForwardAgent yes</c> 转发的是认证用的那个 agent —— 与 OpenSSH 一致(ssh_config(5):转发「到认证 agent 的连接」,
    /// <c>IdentityAgent</c> 换掉的正是这个 agent)。<c>ForwardAgent &lt;路径&gt;</c> / <c>$变量</c> 指的就是那个 agent;
    /// 与认证用的是同一个、或者这台机器认证不走 agent(私钥 / 证书)时照收,端点取它。
    /// </para>
    /// <para>
    /// 〔决策〕认证走 agent、转发又指了<b>另一个</b> agent 时不导转发、说明一句,不换成认证用的那个:配置作者特意给转发指了一个
    /// (多半是只装了几把钥的)agent,换掉等于多交出去几把钥。端点只在用得上时存(认证走 agent,或者开了转发)。
    /// </para>
    /// </remarks>
    private static void MapAgent(SshHostConfig options, SshSessionOptions ssh, AuthMethod? auth, string? identityAgent, List<string> notes)
    {
        string? endpoint = identityAgent;
        bool forwarding = false;
        string? forward = Value(options.First("ForwardAgent"));
        if (forward is not null && !string.Equals(forward, "no", StringComparison.OrdinalIgnoreCase))
        {
            string line = $"ForwardAgent {forward}";
            string? value = ResolveAgentValue(forward);
            if (string.Equals(forward, "yes", StringComparison.OrdinalIgnoreCase))
            {
                forwarding = true;
            }
            else if (forward.StartsWith('$') && string.IsNullOrEmpty(value))
            {
                notes.Add(Strings.Format("SshCfgImport_NoteAgentVarUnset", line, forward[1..]));
            }
            else if (AgentEndpointFor(value, line, notes) is { Usable: true } target)
            {
                if (SameEndpoint(target.Endpoint, endpoint))
                {
                    forwarding = true;
                }
                else if (auth != AuthMethod.Agent)
                {
                    endpoint = target.Endpoint;
                    forwarding = true;
                }
                else
                {
                    notes.Add(Strings.Format("SshCfgImport_NoteForwardAgentConflict", line));
                }
            }
        }

        ssh.AgentForwarding = forwarding;
        ssh.AgentEndpoint = auth == AuthMethod.Agent || forwarding ? endpoint : null;
    }

    /// <summary>
    /// 两个端点是不是同一个 agent:都是系统默认(<see langword="null" />)算同一个;否则展开 <c>~</c>、规范成完整路径再比
    /// (库展开 <c>IdentityAgent</c> 的路径,<c>ForwardAgent</c> 的是原样,<c>~/a.sock</c> 与展开后的那个是同一个)。Windows 上不分大小写。
    /// </summary>
    private static bool SameEndpoint(string? a, string? b)
    {
        if (a is null || b is null)
        {
            return a is null && b is null;
        }
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(Canonical(a), Canonical(b), comparison);

        static string Canonical(string endpoint)
        {
            string resolved = SshAgentEndpoints.Resolve(endpoint)!;
            if (SshAgentEndpoints.IsPipe(resolved))
            {
                return resolved;
            }
            try
            {
                return Path.GetFullPath(resolved);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return resolved;
            }
        }
    }

    /// <summary>
    /// <c>ForwardX11</c> / <c>ForwardX11Trusted</c>:〔决策〕只认明写的 <c>yes</c> / <c>no</c>,没写的按 VelaShell 自己的默认
    /// (不转发;转发时受信任,即 <see cref="SshSessionOptions.X11Trusted" /> 的默认)。
    /// </summary>
    /// <remarks>
    /// OpenSSH 的 <c>ForwardX11Trusted</c> 默认 <c>no</c>(非受信任,<c>-X</c>),VelaShell 默认受信任(<c>-Y</c>,Windows 上的 X 服务器
    /// 多半没有 SECURITY 扩展,非受信任模式用不了)。维护者 2026-10-25 定的口径是不按 OpenSSH 的默认换算 —— 于是只写了
    /// <c>ForwardX11 yes</c> 的那台在 VelaShell 里是受信任的,与在 ssh 里不同:这一点进导入说明,不悄悄改信任级别。
    /// 认不出的值(拼错)不导、说明一句,不当成 <c>no</c> 吞掉。
    /// </remarks>
    private static void MapX11(SshHostConfig options, SshSessionOptions ssh, List<string> notes)
    {
        bool? forward = YesNo(options, "ForwardX11", notes);
        bool? trusted = YesNo(options, "ForwardX11Trusted", notes);
        if (forward == true)
        {
            ssh.X11Forwarding = true;
        }
        if (trusted is { } value)
        {
            ssh.X11Trusted = value;
        }
        else if (ssh.X11Forwarding)
        {
            notes.Add(Strings.Get("SshCfgImport_NoteX11TrustedDefault"));
        }
    }

    /// <summary>
    /// <c>ObscureKeystrokeTiming</c>:〔决策〕只认明写的值 —— <c>yes</c> / <c>interval:N</c> 开、<c>no</c> 关,没写按 VelaShell 的默认(关)。
    /// </summary>
    /// <remarks>
    /// OpenSSH 默认开,照抄它的默认会让导入的配置全部开着(§248 定的默认关,理由是带宽代价)。VelaShell 的节拍固定 20 毫秒
    /// (OpenSSH <c>yes</c> 的值,不开放成可调):<c>interval:N</c> 按开启导入,N 不是 20 时说明一句。认不出的值不导、说明一句。
    /// </remarks>
    private static void MapObscureKeystrokeTiming(SshHostConfig options, SshSessionOptions ssh, List<string> notes)
    {
        if (Value(options.First("ObscureKeystrokeTiming")) is not { } value)
        {
            return;
        }
        if (string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase))
        {
            ssh.ObscureKeystrokeTiming = true;
        }
        else if (string.Equals(value, "no", StringComparison.OrdinalIgnoreCase))
        {
            ssh.ObscureKeystrokeTiming = false;
        }
        else if (value.StartsWith("interval:", StringComparison.OrdinalIgnoreCase)
                 && int.TryParse(value["interval:".Length..], NumberStyles.None, CultureInfo.InvariantCulture, out int ms)
                 && ms is >= 1 and <= 1000)
        {
            // 与库解析 ssh_config 时同一个范围(1–1000 毫秒)。
            ssh.ObscureKeystrokeTiming = true;
            int fixedMs = (int)VelaSshClientWrapper.KeystrokeObfuscationInterval.TotalMilliseconds;
            if (ms != fixedMs)
            {
                notes.Add(Strings.Format("SshCfgImport_NoteObscureInterval", value, fixedMs));
            }
        }
        else
        {
            notes.Add(Strings.Format("SshCfgImport_NoteValueInvalid", $"ObscureKeystrokeTiming {value}"));
        }
    }

    /// <summary>明写的 <c>yes</c> / <c>no</c>(大小写不论);没写为 <see langword="null" />,认不出的也是 <see langword="null" /> 并说明一句。</summary>
    private static bool? YesNo(SshHostConfig options, string keyword, List<string> notes)
    {
        if (Value(options.First(keyword)) is not { } value)
        {
            return null;
        }
        if (string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        if (string.Equals(value, "no", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        notes.Add(Strings.Format("SshCfgImport_NoteValueInvalid", $"{keyword} {value}"));
        return null;
    }

    /// <summary>
    /// <c>PubkeyAcceptedAlgorithms</c>(旧名 <c>PubkeyAcceptedKeyTypes</c>)里留着 SHA-1 的 RSA 签名(<c>ssh-rsa</c>,
    /// 或证书的 <c>ssh-rsa-cert-v01@openssh.com</c>)→ 打开「允许老算法」。
    /// </summary>
    /// <remarks>
    /// <para>
    /// VelaShell 没有单独放开 SHA-1 用户签名的开关,它是「允许老算法」的一部分(<c>SshConnectionAssembler</c> 的
    /// <c>AllowSha1RsaSignatures</c>),导出时写的也正是这一行(<see cref="SshConfigExporter" />)。不映射的话
    /// 只认 ssh-rsa 的老设备导进来就登不上。「留着」的口径与 SSH 库连接时一样(spec/09 §7):<c>-</c> 开头的是从默认里删
    /// (默认本来没有 <c>ssh-rsa</c>),其余写法看名字在不在。
    /// </para>
    /// <para>
    /// 这个开关同时把 SHA-1 的密钥交换、主机密钥、MAC 追加到默认清单之后:另三个清单里 <c>+</c> 写着的那几项并回开关
    /// (VelaShell 自己导出的就是这样写的,往返之后配置与原来一样);文件里没放开它们的,如实说一句,不悄悄放宽。
    /// </para>
    /// </remarks>
    private static void MapLegacyAlgorithms(SshHostConfig options, SshSessionOptions ssh, List<string> notes)
    {
        if (Value(options.PubkeyAcceptedAlgorithms) is not { } accepted || accepted[0] == '-')
        {
            return;
        }
        string[] names = accepted.TrimStart('+', '^')
            .Split([',', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (!names.Any(static name => name is "ssh-rsa" or "ssh-rsa-cert-v01@openssh.com"))
        {
            return;
        }

        ssh.LegacyAlgorithms = true;
        bool widened = false;
        ssh.KexAlgorithms = AbsorbLegacy(SshAlgorithmKind.KeyExchange, ssh.KexAlgorithms, ref widened);
        ssh.HostKeyAlgorithms = AbsorbLegacy(SshAlgorithmKind.HostKey, ssh.HostKeyAlgorithms, ref widened);
        ssh.Ciphers = AbsorbLegacy(SshAlgorithmKind.Cipher, ssh.Ciphers, ref widened);
        ssh.Macs = AbsorbLegacy(SshAlgorithmKind.Mac, ssh.Macs, ref widened);
        if (widened)
        {
            notes.Add(Strings.Format("SshCfgImport_NoteLegacyFromPubkey", $"PubkeyAcceptedAlgorithms {accepted}"));
        }
    }

    /// <summary>
    /// 一类清单在打开「允许老算法」之后怎么存:<c>+</c> 清单里开关本来就会追加的那几项去掉(剩下的照留,一个不剩就是不写);
    /// 文件的清单里没有那几项时 <paramref name="widened" /> 置真(打开开关放宽了这一类)。
    /// </summary>
    private static string? AbsorbLegacy(SshAlgorithmKind kind, string? spec, ref bool widened)
    {
        IReadOnlyList<string> plain = SshAlgorithmPreferences.Defaults(kind, legacy: false);
        string[] additions = [.. SshAlgorithmPreferences.Defaults(kind, legacy: true).Where(name => !plain.Contains(name, StringComparer.Ordinal))];
        if (additions.Length == 0)
        {
            return spec;
        }
        SshAlgorithmPreferences.TryApply(kind, spec, legacy: false, out IReadOnlyList<string> effective, out _);
        if (!additions.All(name => effective.Contains(name, StringComparer.Ordinal)))
        {
            widened = true;
        }
        if (spec is not ['+', ..])
        {
            return spec;
        }
        string[] kept = [.. spec[1..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(name => !additions.Contains(name, StringComparer.Ordinal))];
        return kept.Length == 0 ? null : "+" + string.Join(',', kept);
    }

    private static string? Algorithms(SshAlgorithmKind kind, string keyword, string? spec, List<string> notes)
    {
        string? usable = SshAlgorithmPreferences.SanitizeImported(kind, spec, out IReadOnlyList<string> dropped, out string? error);
        if (error is not null)
        {
            notes.Add(Strings.Format("SshCfgImport_NoteAlgoSkipped", keyword, error));
        }
        else if (dropped.Count > 0)
        {
            notes.Add(Strings.Format("SshCfgImport_NoteAlgoDropped", keyword, string.Join(", ", dropped)));
        }
        return usable;
    }

    /// <summary>
    /// 转发 → 隧道(停止状态,在隧道面板里启动):<c>LocalForward</c> → 本地转发、<c>RemoteForward</c> → 远程转发
    /// (不给目标时是远程动态转发,放行名单取这台主机的 <c>PermitRemoteOpen</c>)、<c>DynamicForward</c> → 动态转发(SOCKS)。
    /// 套接字写法(含 <c>/</c> 的一头)落到 <see cref="TunnelConfig.LocalSocketPath" /> / <see cref="TunnelConfig.RemoteSocketPath" />。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 没写监听地址时本地一头绑环回(<c>GatewayPorts yes</c> 时全部网卡)、远程一头交给服务端定(环回),与 ssh 的默认一致;
    /// <c>*</c> 是全部网卡。<c>ClearAllForwardings yes</c> 时一条都不导。逐行解析:一行写错不连累别的行。
    /// </para>
    /// <para>
    /// 隧道装不下、或者装进去会改变意思的如实跳过,进预览:端口 0(连接时才定的端口);远程转发只有一头是套接字
    /// (SSH 库只给了两头 TCP、两头套接字两种组合,隧道服务同样拒绝);本机用不了的套接字路径;
    /// 没写 <c>PermitRemoteOpen</c>、写了 <c>none</c> 或写错了的远程动态转发。
    /// </para>
    /// <para>
    /// 〔决策〕<b>远程动态转发没写 <c>PermitRemoteOpen</c> 时不导入</b>。OpenSSH 在这时按 <c>any</c> 处理(库的 <c>GetPermitRemoteOpen</c> 也是),
    /// 而隧道的放行名单没有默认值、不许留空(<see cref="RemoteOpenRules" />):远程动态转发等于把本机变成远端的 SOCKS 代理,
    /// 放哪些出去要用户明说。导入时替用户填上 <c>any</c>,正好是那条规则要防的「替用户当成全放」;配置里明写了的(含 <c>any</c>)照收。
    /// </para>
    /// </remarks>
    private static List<TunnelConfig> MapForwards(SshHostConfig options, List<string> notes)
    {
        List<TunnelConfig> tunnels = [];
        if (options.ClearAllForwardings)
        {
            return tunnels;
        }

        string localDefault = options.GatewayPorts ? "0.0.0.0" : "127.0.0.1";
        foreach ((SshConfigForwardKind kind, string keyword) in new[]
                 {
                     (SshConfigForwardKind.Local, "LocalForward"),
                     (SshConfigForwardKind.Remote, "RemoteForward"),
                     (SshConfigForwardKind.Dynamic, "DynamicForward"),
                 })
        {
            foreach (string value in options.All(keyword))
            {
                string line = $"{keyword} {value}";
                SshConfigForward forward;
                try
                {
                    forward = SshConfigForward.Parse(kind, value);
                }
                catch (SshConnectException)
                {
                    notes.Add(Strings.Format("SshCfgImport_NoteForwardInvalid", line));
                    continue;
                }

                if (forward.ListenSocketPath is null && forward.ListenPort == 0)
                {
                    notes.Add(Strings.Format("SshCfgImport_NoteForwardPortZero", line));
                    continue;
                }

                TunnelConfig? tunnel = kind switch
                {
                    SshConfigForwardKind.Local => MapLocalForward(forward, localDefault, line, notes),
                    SshConfigForwardKind.Remote when forward.IsRemoteDynamic => MapRemoteDynamicForward(options, forward, line, notes),
                    SshConfigForwardKind.Remote => MapRemoteForward(forward, line, notes),
                    _ => new TunnelConfig
                    {
                        Type = TunnelType.DynamicForward,
                        Name = string.Empty,
                        LocalHost = ListenHost(forward.ListenAddress, localDefault),
                        LocalPort = (uint)forward.ListenPort,
                    },
                };
                if (tunnel is not null)
                {
                    tunnels.Add(tunnel);
                }
            }
        }

        if (tunnels.Count > 0)
        {
            notes.Add(Strings.Format("SshCfgImport_NoteForwardsImported", tunnels.Count.ToString(CultureInfo.InvariantCulture)));
        }
        return tunnels;
    }

    /// <summary><c>LocalForward</c>:两头各自可以是端口或套接字(隧道与 SSH 库都支持四种组合)。</summary>
    private static TunnelConfig? MapLocalForward(SshConfigForward forward, string localDefault, string line, List<string> notes)
    {
        string? localSocket = null;
        string? remoteSocket = null;
        if ((forward.ListenSocketPath is { } listen && (localSocket = LocalSocketPath(listen)) is null)
            || (forward.TargetSocketPath is { } target && (remoteSocket = RemoteSocketPath(target)) is null))
        {
            notes.Add(Strings.Format("SshCfgImport_NoteForwardSocketPath", line));
            return null;
        }
        return new TunnelConfig
        {
            Type = TunnelType.LocalForward,
            Name = string.Empty,
            // 套接字那头的主机与端口不用(TunnelConfig 的约定:路径非空就以路径为准),照表单的样子留个环回与 0。
            LocalHost = localSocket is null ? ListenHost(forward.ListenAddress, localDefault) : "127.0.0.1",
            LocalPort = localSocket is null ? (uint)forward.ListenPort : 0,
            RemoteHost = remoteSocket is null ? HostAddress.Normalize(forward.TargetHost!) : string.Empty,
            RemotePort = remoteSocket is null ? (uint)forward.TargetPort : 0,
            LocalSocketPath = localSocket,
            RemoteSocketPath = remoteSocket,
        };
    }

    /// <summary><c>RemoteForward 监听 目标</c>:两头 TCP,或两头都是套接字;只有一头是套接字的跳过。</summary>
    private static TunnelConfig? MapRemoteForward(SshConfigForward forward, string line, List<string> notes)
    {
        if ((forward.ListenSocketPath is null) != (forward.TargetSocketPath is null))
        {
            notes.Add(Strings.Format("SshCfgImport_NoteForwardSocketMix", line));
            return null;
        }
        if (forward.ListenSocketPath is { } listen)
        {
            string? serverSocket = RemoteSocketPath(listen);
            string? localSocket = LocalSocketPath(forward.TargetSocketPath!);
            if (serverSocket is null || localSocket is null)
            {
                notes.Add(Strings.Format("SshCfgImport_NoteForwardSocketPath", line));
                return null;
            }
            return new TunnelConfig
            {
                Type = TunnelType.RemoteForward,
                Name = string.Empty,
                LocalHost = "127.0.0.1",
                LocalPort = 0,
                RemoteHost = string.Empty,
                RemotePort = 0,
                LocalSocketPath = localSocket,
                RemoteSocketPath = serverSocket,
            };
        }
        return new TunnelConfig
        {
            Type = TunnelType.RemoteForward,
            Name = string.Empty,
            LocalHost = HostAddress.Normalize(forward.TargetHost!),
            LocalPort = (uint)forward.TargetPort,
            RemoteHost = ListenHost(forward.ListenAddress, "127.0.0.1"),
            RemotePort = (uint)forward.ListenPort,
        };
    }

    /// <summary>
    /// <c>RemoteForward [监听地址:]端口</c>(不给目标)→ 远程动态转发,放行名单取这台主机的 <c>PermitRemoteOpen</c>
    /// (ssh_config 里它是按主机的一条,这台主机的每条远程动态转发共用)。为什么没写就不导,见 <see cref="MapForwards" />。
    /// </summary>
    private static TunnelConfig? MapRemoteDynamicForward(SshHostConfig options, SshConfigForward forward, string line, List<string> notes)
    {
        if (forward.ListenSocketPath is not null)
        {
            // 套接字上的远程动态转发:SSH 库与隧道都没有这一种。
            notes.Add(Strings.Format("SshCfgImport_NoteForwardSocketMix", line));
            return null;
        }

        string? permit = Value(options.First("PermitRemoteOpen"));
        if (permit is null)
        {
            notes.Add(Strings.Format("SshCfgImport_NoteRemoteDynamicNoPermit", line));
            return null;
        }
        if (string.Equals(permit, "none", StringComparison.OrdinalIgnoreCase))
        {
            // 什么都不放行的远程动态转发:建起来每个请求都被拒,导进来只是一条用不了的隧道。
            notes.Add(Strings.Format("SshCfgImport_NoteRemoteDynamicNone", line));
            return null;
        }
        if (!RemoteOpenRules.TryParse(permit, out IReadOnlyList<string> rules, out string? invalid))
        {
            notes.Add(Strings.Format("SshCfgImport_NoteRemoteDynamicInvalid", line, invalid ?? permit));
            return null;
        }

        return new TunnelConfig
        {
            Type = TunnelType.RemoteDynamicForward,
            Name = string.Empty,
            // 远程动态没有本机端口(隧道面板同样存 0),服务器上的监听地址与端口沿用远程转发的 RemoteHost / RemotePort。
            LocalHost = "127.0.0.1",
            LocalPort = 0,
            RemoteHost = ListenHost(forward.ListenAddress, "127.0.0.1"),
            RemotePort = (uint)forward.ListenPort,
            PermitRemoteOpen = rules,
        };
    }

    /// <summary>
    /// 本机套接字路径:开头的 <c>~</c> 展开成主目录;要是完整路径(相对路径落在哪全看程序的当前目录),
    /// 也不能超过 <c>sun_path</c> 的上限(Linux 与 Windows 107 字节、macOS 103 字节,与隧道表单同一把尺)。用不了时为 <see langword="null" />。
    /// </summary>
    /// <remarks>别的机器上写的配置(Windows 上导入 Linux 的 <c>/run/user/…</c>)在这里就会被拦下:那条路径在本机本来就用不了。</remarks>
    private static string? LocalSocketPath(string path)
    {
        string value = path.Trim().Trim('"');
        if (value == "~" || value.StartsWith("~/", StringComparison.Ordinal))
        {
            value = Path.Combine(SshPathResolver.HomeDirectory, value.Length > 2 ? value[2..] : string.Empty);
        }
        int limit = OperatingSystem.IsMacOS() ? 103 : 107;
        return value.Length > 0 && Path.IsPathFullyQualified(value) && Encoding.UTF8.GetByteCount(value) <= limit ? value : null;
    }

    /// <summary>服务器套接字路径:要是 <c>/</c> 开头的绝对路径(服务器上的 sshd 按它自己的文件系统解析)。用不了时为 <see langword="null" />。</summary>
    private static string? RemoteSocketPath(string path)
    {
        string value = path.Trim().Trim('"');
        return value.StartsWith('/') ? value : null;
    }

    /// <summary>监听地址:没写用默认,<c>*</c> 是全部网卡,其余原样(IPv6 去掉方括号)。</summary>
    private static string ListenHost(string? address, string fallback) => address switch
    {
        null or "" => fallback,
        "*" => "0.0.0.0",
        "localhost" => "127.0.0.1",
        _ => HostAddress.Normalize(address),
    };

    /// <summary><c>$变量</c> 取环境变量的值,其余原样(路径)。</summary>
    private static string? ResolveAgentValue(string value) =>
        value.StartsWith('$') ? Environment.GetEnvironmentVariable(value[1..]) : value;

    /// <summary>这个 agent 端点是不是 VelaShell 连的那个默认 agent(<c>SSH_AUTH_SOCK</c> 指着的)。</summary>
    private static bool IsDefaultAgent(string? endpoint) =>
        endpoint is { Length: > 0 }
        && Environment.GetEnvironmentVariable("SSH_AUTH_SOCK") is { Length: > 0 } socket
        && string.Equals(endpoint.Trim(), socket.Trim(), OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal);

    private static bool FileExists(string path)
    {
        try
        {
            return File.Exists(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

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
    /// 把 SSH 库展开好的 <c>IdentityFile</c> 落成本机绝对路径(相对路径按 <c>~/.ssh</c>)。文件不存在也照样带上 ——
    /// 密钥可能在另一台机器上,或者用户正打算补进来;把路径留在配置里比悄悄丢掉更有用。
    /// </summary>
    /// <remarks>
    /// <c>~</c> / <c>%d</c> / <c>%h</c> 的展开曾经在这里另写了一份(而且不认 <c>%h</c> / <c>%r</c>);
    /// <c>IdentityFile none</c> 由库排除。
    /// </remarks>
    private static string? ResolveIdentityFile(string? expanded, string baseDirectory)
    {
        if (expanded is null)
        {
            return null;
        }
        string path = SshPathResolver.ToAbsolute(expanded, baseDirectory);
        return path.Length > 0 ? path : null;
    }

    private static string DedupKey(string host, int port, string user) => $"{host.Trim()}|{port}|{user.Trim()}";
}

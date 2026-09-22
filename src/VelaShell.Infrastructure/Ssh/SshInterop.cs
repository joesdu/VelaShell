using VelaShell.Core.Resources;
using VelaShell.Core.Ssh;
using VelaShell.Ssh.Diagnostics;
using VelaShell.Ssh.Keys;
using VelaShell.Ssh.Sftp;

namespace VelaShell.Infrastructure.Ssh;

/// <summary>
/// VelaShell.Ssh 与 Core 中立类型之间的异常翻译。库类型只在 Infrastructure 内流动;
/// 更换底层库时替换本文件的对应映射。
/// </summary>
/// <remarks>
/// <para>
/// 与它替掉的 <c>TmdsSshInterop</c> 相比,这里**没有一处字符串解析**。
/// 旧实现只能靠 <c>"The connection could not be established - {reason} - ..."</c>
/// 这个消息前缀去切原因 —— 因为上游把 <c>ConnectFailedException</c> 声明成了
/// <c>internal</c>,原因码根本拿不到。那段切字符串的代码是**上游一次措辞改动就会静默失效**的,
/// 而失效的表现是「认证失败」被当成「连接失败」,重试与提示全部走错分支。
/// </para>
/// <para>
/// 现在原因是强类型的 <see cref="SshFailureReason" /> + <see cref="SshPhase" />,
/// 按类型和枚举匹配即可。
/// </para>
/// </remarks>
internal static class SshInterop
{
    /// <summary>
    /// VelaShell.Ssh 异常 → Core 中立异常;不认识的类型返回 <see langword="null" />
    /// (调用方原样上抛)。
    /// </summary>
    /// <remarks>
    /// 若异常是取消且 <paramref name="callerToken" /> 已请求取消,同样返回
    /// <see langword="null" />:调用方主动取消必须保留 <see cref="OperationCanceledException" />
    /// 语义,只有库内部超时(调用方未取消)才翻译成
    /// <see cref="VelaSshOperationTimeoutException" />。
    /// </remarks>
    public static Exception? Translate(Exception ex, CancellationToken callerToken = default)
    {
        if (ex is OperationCanceledException && callerToken.IsCancellationRequested)
        {
            return null;
        }

        return ex switch
        {
            // 派生类型放在基类型前面,否则后面的分支永远到不了。
            SshAuthenticationException auth => new VelaSshAuthenticationException(Describe(auth), auth),
            SshPrivateKeyException key => TranslatePrivateKey(key),
            SshCertificateException cert => new VelaSshAuthenticationException(cert.Message, cert),
            SftpTransferInterruptedException sftp => new VelaSftpOperationException(sftp.Message, sftp),
            SftpException sftp => TranslateSftp(sftp),
            SshNegotiationException negotiation => new VelaSshConnectionException(Describe(negotiation), negotiation),
            SshChannelException channel => new VelaSshClientException(channel.Message, channel),
            SshForwardException forward => new VelaSshClientException(forward.Message, forward),
            SshConnectionClosedException closed => new VelaSshConnectionException(Localize(closed), closed),
            SshConnectException connect => TranslateConnect(connect),
            SshProtocolException protocol => new VelaSshConnectionException(protocol.Message, protocol),
            OperationCanceledException => new VelaSshOperationTimeoutException(ex.Message, ex),
            SshException => new VelaSshClientException(ex.Message, ex),
            _ => null,
        };
    }

    /// <summary>
    /// 建链阶段的失败按 <see cref="SshFailureReason" /> 分流。
    /// </summary>
    /// <remarks>
    /// 超时单独分出来,是因为主窗口的自动重连只该对这一类生效:
    /// 「连不上」值得再试一次,「认证不过」再试一百次也一样。
    /// </remarks>
    private static VelaSshClientException TranslateConnect(SshConnectException ex) =>
        ex.Reason switch
        {
            SshFailureReason.Timeout or SshFailureReason.TcpTimeout or SshFailureReason.KeepAliveTimeout =>
                new VelaSshOperationTimeoutException(Localize(ex), ex),
            _ => new VelaSshConnectionException(Localize(ex), ex),
        };

    /// <summary>
    /// 按原因码给出界面语言的一句话,原文留在 <see cref="Exception.InnerException" /> 里。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 库的异常消息是写给开发者看的(而且是中文);上一版底层库给的是英文。直接透传的话,
    /// 英 / 日 / 韩界面的用户在换库之后会突然看到中文报错。
    /// </para>
    /// <para>
    /// 只翻有把握的那几类。<see cref="SshFailureReason.ProxyRefused" /> 不在其中:它的消息是
    /// <c>ProxyTransportDialer</c> 拼的,带着「经哪个代理去哪」与换 SOCKS5 的提示,比一句
    /// 泛泛的「代理拒绝」有用得多。<see cref="SshFailureReason.HostKeyRejected" /> 同理:消息是
    /// <c>VelaHostKeyPolicy</c> 用本地化文案写的拒绝理由(含新旧指纹)。认不出的原因照旧用原文。
    /// </para>
    /// <para>
    /// 尾巴上的 <c>[原因 @ 阶段]</c> 不翻译 —— 那是给提 issue 时贴日志用的,跨语言一致才好搜。
    /// </para>
    /// </remarks>
    internal static string Localize(SshException ex)
    {
        string? key = ex.Reason switch
        {
            SshFailureReason.DnsFailure => "SshErr_DnsFailure",
            SshFailureReason.TcpRefused => "SshErr_TcpRefused",
            SshFailureReason.TcpTimeout => "SshErr_TcpTimeout",
            SshFailureReason.TcpUnreachable => "SshErr_TcpUnreachable",
            SshFailureReason.ProxyAuthRequired => "SshErr_ProxyAuthRequired",
            SshFailureReason.NotAnSshServer => "SshErr_NotAnSshServer",
            SshFailureReason.VersionMismatch => "SshErr_VersionMismatch",
            SshFailureReason.HostKeyChanged => "SshErr_HostKeyChanged",
            SshFailureReason.Timeout => "SshErr_Timeout",
            SshFailureReason.KeepAliveTimeout => "SshErr_KeepAliveTimeout",
            SshFailureReason.ClosedByPeer => "SshErr_ClosedByPeer",
            SshFailureReason.Disconnected => "SshErr_Disconnected",
            _ => null,
        };

        return key is null ? ex.Message : $"{Strings.Get(key)} [{ex.Reason} @ {ex.Phase}]";
    }

    /// <summary>
    /// SFTP 的失败要分出「没这个文件」与「没权限」—— 上层据此决定是提示用户还是静默跳过。
    /// </summary>
    /// <remarks>
    /// <b>服务端原话(<see cref="SftpException.ServerMessage" />)必须带上。</b>
    /// SFTP v3 只有 9 个状态码,而码 4(Failure)承载了绝大多数真实错误 ——
    /// 「目录非空」「文件已存在」「磁盘满」「配额超限」全是同一个码,
    /// 服务端给的那段文本是唯一能区分它们的信息。
    /// </remarks>
    private static VelaSftpOperationException TranslateSftp(SftpException ex)
    {
        string message = string.IsNullOrWhiteSpace(ex.ServerMessage)
            ? ex.Message
            : $"{ex.Message}({ex.ServerMessage})";

        return ex.StatusCode switch
        {
            SftpStatusCode.NoSuchFile => new VelaSftpPathNotFoundException(message, ex),
            SftpStatusCode.PermissionDenied => new VelaSftpPermissionDeniedException(message, ex),
            _ => new VelaSftpOperationException(message, ex),
        };
    }

    /// <summary>
    /// 私钥读不出来是**认证**失败的一种,而不是连接失败 —— 上层据此弹的是凭据对话框。
    /// </summary>
    private static VelaSshAuthenticationException TranslatePrivateKey(SshPrivateKeyException ex) =>
        new(ex.Message, ex);

    /// <summary>
    /// 认证失败时把逐条尝试记录摊开。
    /// </summary>
    /// <remarks>
    /// 这是旧实现拿不到的东西。Tmds 只给一句
    /// <c>"... - AuthenticationFailed - ..."</c>,用户看不出是密钥被跳过了、
    /// 服务端根本不接受这种方法、还是口令真的错了。
    /// </remarks>
    private static string Describe(SshAuthenticationException ex)
    {
        string attempts = ex.DescribeAttempts();
        return string.IsNullOrWhiteSpace(attempts) ? ex.Message : $"{ex.Message}{Environment.NewLine}{attempts}";
    }

    /// <summary>
    /// 算法协商失败时把双方名单摊开。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 旧实现为了同一件事要**另开一条 TCP 连接回探对端的 KEXINIT**
    /// (<c>SshAlgorithmProbe</c> + <c>SshAlgorithmDiagnostics</c>,共 384 行),
    /// 因为上游没把对端名单暴露出来。现在异常自带。
    /// </para>
    /// <para>
    /// 证书变体(<c>*-cert-v01@openssh.com</c>)不列进「本端支持」:会走到这条诊断的对端
    /// 出示的是普通主机密钥,列出来既不能指导用户改配置,还把可用算法淹没在十几行里。
    /// 交集判断仍用完整名单 —— 少显示不改变判断结果。
    /// </para>
    /// </remarks>
    private static string Describe(SshNegotiationException ex)
    {
        string ours = string.Join(", ", ex.OfferedByUs.Where(IsWorthShowing));

        return string.Join(Environment.NewLine,
            Strings.Format("Ssh_AlgoMismatchTitle", ex.PeerVersion),
            DescribeCategory(ex.Category),
            Strings.Format("Ssh_AlgoMismatchPeer", string.Join(", ", ex.OfferedByPeer)),
            Strings.Format("Ssh_AlgoMismatchOurs", ours));
    }

    /// <summary>
    /// 哪一类算法没谈成。
    /// </summary>
    /// <remarks>
    /// 收发两个方向在界面上合成一类:现实里两份名单几乎总是一样,分开说只会把同一件事讲两遍。
    /// 真不一样时异常消息本身已经点明了是哪个方向。
    /// </remarks>
    private static string DescribeCategory(SshNegotiationCategory category) => category switch
    {
        SshNegotiationCategory.KeyExchange => Strings.Get("Ssh_AlgoKindKex"),
        SshNegotiationCategory.HostKey => Strings.Get("Ssh_AlgoKindHostKey"),
        SshNegotiationCategory.EncryptionClientToServer or SshNegotiationCategory.EncryptionServerToClient =>
            Strings.Get("Ssh_AlgoKindEncryption"),
        SshNegotiationCategory.MacClientToServer or SshNegotiationCategory.MacServerToClient =>
            Strings.Get("Ssh_AlgoKindMac"),
        _ => category.ToString(),
    };

    private static bool IsWorthShowing(string algorithm) =>
        !algorithm.Contains("-cert-", StringComparison.Ordinal);

    /// <summary>
    /// 从异常链里抽一句能放进界面的失败原因。
    /// </summary>
    /// <remarks>
    /// 优先用强类型的 <see cref="SshException.Reason" />;一路找不到才退回消息拼接。
    /// </remarks>
    public static string GetFailureDiagnostic(Exception ex)
    {
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            if (current is SshException { Reason: not SshFailureReason.Unknown } known)
            {
                return known.Reason.ToString();
            }
        }

        List<string> parts = [];
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            if (current.Message is { Length: > 0 } message && !parts.Contains(message))
            {
                parts.Add(message);
            }
        }
        return parts.Count > 0 ? string.Join(" → ", parts) : ex.Message;
    }
}

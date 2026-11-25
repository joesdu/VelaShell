using System.Globalization;
using VelaShell.Core.Resources;
using VelaShell.Ssh.Auth;
using VelaShell.Ssh.Channels;
using VelaShell.Ssh.Diagnostics;
using VelaShell.Ssh.Forwarding;
using VelaShell.Ssh.HostKeys;
using VelaShell.Ssh.Keys;

namespace VelaShell.Infrastructure.Ssh;

/// <summary>
/// 库的结构化诊断 → 界面语言:认证的逐条尝试记录、通道失败的建议、私钥 / 证书读不出来的原因、主机证书不合格的原因,
/// 建连时主机密钥被拒、代理握手与跳板的失败。
/// </summary>
/// <remarks>
/// <para>
/// 这几类以前直接透传库的中文原文(库 AGENTS 4.8)。库现在给出了结构化的出处 —— <see cref="SshAuthDetail" />、
/// <see cref="SshChannelException.FailureKind" />、<see cref="SshKeyProblemKind" />、<see cref="HostCertificateProblem" />、
/// <see cref="SshConnectException.FailureKind" /> ——
/// 这里只按种类与参数拼句子,<b>不解析库的句子</b>(库 AGENTS 4.5)。
/// </para>
/// <para>
/// 每个种类都要有一条文案:<c>SshInteropDiagnosticsTests</c> 逐个枚举值核对映射与五份 resx,库里加了新值而这里没跟上就红。
/// 没归类(<c>Unknown</c>)的照旧退回原因码那一层(<see cref="Localize" />)或库的原文。
/// </para>
/// <para>对端或文件里来的名字(Key ID、主体、算法名、通道类型、服务端原话)拼进来之前一律先过 <see cref="PeerText.Sanitize" />。</para>
/// </remarks>
internal static partial class SshInterop
{
    /// <summary>认证尝试记录里 <c>none</c> 探测那一步的方法名(RFC 4252 §5.2 的协议常量)。</summary>
    private const string AuthNoneMethod = "none";

    /// <summary>证书主体最多列几个,多出来的用省略号带过(与库的原文同一个数)。</summary>
    private const int MaxPrincipalsShown = 8;

    // ------------------------------------------------------------ 认证尝试记录

    /// <summary>把认证失败的逐条尝试记录拼成多行界面语言(替代库的 <see cref="SshAuthenticationException.DescribeAttempts" />)。</summary>
    internal static string DescribeAttempts(SshAuthenticationException ex) =>
        ex.Attempts.Count == 0
            ? Strings.Get("SshErr_AuthNoAttempts")
            : string.Join(Environment.NewLine, ex.Attempts.Select(DescribeAttempt));

    /// <summary>一条尝试记录:凭据(签名算法)、结果、说明。</summary>
    internal static string DescribeAttempt(SshAuthAttempt attempt)
    {
        string label = attempt.BridgedCredentialLabel is { } bridged
            ? Strings.Format("SshErr_AuthLabelViaKbd", bridged)
            : attempt.Method == AuthNoneMethod
                ? Strings.Get("SshErr_AuthLabelProbe")
                : attempt.CredentialLabel;
        if (attempt.SignatureAlgorithm is { } algorithm)
        {
            label = Strings.Format("SshErr_AuthLabelAlgorithm", label, algorithm);
        }

        string outcome = Strings.Get(AuthOutcomeKey(attempt.Outcome));
        string? detail = DescribeAttemptDetails(attempt);
        return detail is null
            ? Strings.Format("SshErr_AuthAttemptLine", label, outcome)
            : Strings.Format("SshErr_AuthAttemptLineDetail", label, outcome, detail);
    }

    /// <summary>
    /// 一步的几条说明。没有结构化说明、只有句子的记录(使用者自己造的)照原样显示 ——
    /// 那句话本来就是它给的,不是库的中文。
    /// </summary>
    private static string? DescribeAttemptDetails(SshAuthAttempt attempt)
    {
        if (attempt.Details.Count == 0)
        {
            return string.IsNullOrWhiteSpace(attempt.Detail) ? null : attempt.Detail;
        }

        string[] parts = [.. attempt.Details.Select(LocalizeAuthDetail).Where(static p => p.Length > 0)];
        return parts.Length == 0
            ? null
            : parts.Aggregate(static (joined, next) => Strings.Format("SshErr_AuthDetailJoin", joined, next));
    }

    /// <summary>尝试结果的文案键。</summary>
    internal static string AuthOutcomeKey(SshAuthOutcome outcome) => outcome switch
    {
        SshAuthOutcome.Success => "SshErr_AuthOutcomeSuccess",
        SshAuthOutcome.PartialSuccess => "SshErr_AuthOutcomePartialSuccess",
        SshAuthOutcome.SkippedNotOffered => "SshErr_AuthOutcomeSkippedNotOffered",
        SshAuthOutcome.SkippedNoMaterial => "SshErr_AuthOutcomeSkippedNoMaterial",
        _ => "SshErr_AuthOutcomeFailure",
    };

    /// <summary>尝试说明的文案键;没归类时为 <see langword="null" />。</summary>
    internal static string? AuthDetailKey(SshAuthDetailKind kind) => kind switch
    {
        SshAuthDetailKind.ServerAccepts => "SshErr_AuthDetailServerAccepts",
        SshAuthDetailKind.MethodNotOffered => "SshErr_AuthDetailMethodNotOffered",
        SshAuthDetailKind.MethodFailureLimit => "SshErr_AuthDetailMethodFailureLimit",
        SshAuthDetailKind.PublicKeyNotAccepted => "SshErr_AuthDetailPublicKeyNotAccepted",
        SshAuthDetailKind.PasswordChanged => "SshErr_AuthDetailPasswordChanged",
        SshAuthDetailKind.PasswordChangeFailed => "SshErr_AuthDetailPasswordChangeFailed",
        SshAuthDetailKind.PasswordChangeNotConfigured => "SshErr_AuthDetailPasswordChangeNotConfigured",
        SshAuthDetailKind.NewPasswordRejected => "SshErr_AuthDetailNewPasswordRejected",
        SshAuthDetailKind.NewPasswordNotProvided => "SshErr_AuthDetailNewPasswordNotProvided",
        SshAuthDetailKind.CredentialFailed => "SshErr_AuthDetailCredentialFailed",
        SshAuthDetailKind.NoUsableSignatureAlgorithm => "SshErr_AuthDetailNoUsableSignatureAlgorithm",
        SshAuthDetailKind.Sha1Fallback => "SshErr_AuthDetailSha1Fallback",
        SshAuthDetailKind.Sha1Signature => "SshErr_AuthDetailSha1Signature",
        SshAuthDetailKind.CertificateExpired => "SshErr_AuthDetailCertificateExpired",
        SshAuthDetailKind.CertificateNotYetValid => "SshErr_AuthDetailCertificateNotYetValid",
        SshAuthDetailKind.HostCertificateForLogin => "SshErr_AuthDetailHostCertificateForLogin",
        SshAuthDetailKind.PrincipalNotListed => "SshErr_AuthDetailPrincipalNotListed",
        _ => null,
    };

    /// <summary>一条尝试说明的界面语言;没归类的照库的原文。</summary>
    internal static string LocalizeAuthDetail(SshAuthDetail detail)
    {
        if (AuthDetailKey(detail.Kind) is not { } key)
        {
            return detail.ToString();
        }

        return detail.Kind switch
        {
            SshAuthDetailKind.ServerAccepts or SshAuthDetailKind.MethodNotOffered =>
                Strings.Format(key, PeerText.Sanitize(string.Join(", ", detail.Methods), 256)),
            SshAuthDetailKind.MethodFailureLimit => Strings.Format(key, detail.Method, detail.Count),
            SshAuthDetailKind.NewPasswordRejected => Strings.Format(key, detail.Count, PeerText.Sanitize(detail.ServerText, 256)),
            SshAuthDetailKind.CredentialFailed => Strings.Format(key, DescribeCredentialError(detail.Error)),
            SshAuthDetailKind.NoUsableSignatureAlgorithm => Strings.Format(key, detail.KeyType),
            SshAuthDetailKind.Sha1Fallback => Strings.Format(key, detail.Algorithm, detail.FallbackAlgorithm),
            SshAuthDetailKind.Sha1Signature => Strings.Format(key, detail.Algorithm),
            SshAuthDetailKind.CertificateExpired or SshAuthDetailKind.CertificateNotYetValid =>
                Strings.Format(key, PeerText.Sanitize(detail.KeyId, 128), FormatTime(detail.Time)),
            SshAuthDetailKind.HostCertificateForLogin => Strings.Format(key, PeerText.Sanitize(detail.KeyId, 128)),
            SshAuthDetailKind.PrincipalNotListed => Strings.Format(key, JoinPeerNames(detail.Principals), detail.UserName),
            _ => Strings.Get(key),
        };
    }

    /// <summary>凭据自己抛的异常:库的异常按原因码与结构化属性本地化,别的(宿主自己的回调)它的消息本来就是界面语言。</summary>
    private static string DescribeCredentialError(Exception? error) => error switch
    {
        null => "",
        SshException ssh => Localize(ssh),
        _ => error.Message,
    };

    // ------------------------------------------------------------ 通道

    /// <summary>通道失败的文案键;没归类时为 <see langword="null" />。sftp 子系统被拒另有一句(见 <see cref="LocalizeChannel" />)。</summary>
    internal static string? ChannelFailureKey(SshChannelFailureKind kind) => kind switch
    {
        SshChannelFailureKind.ForwardingProhibited => "SshErr_ChanForwardingProhibited",
        SshChannelFailureKind.OpenProhibited => "SshErr_ChanOpenProhibited",
        SshChannelFailureKind.ForwardTargetUnreachable => "SshErr_ChanForwardTargetUnreachable",
        SshChannelFailureKind.UnknownChannelType => "SshErr_ChanUnknownType",
        SshChannelFailureKind.ResourceShortage => "SshErr_ChanResourceShortage",
        SshChannelFailureKind.OpenRejected => "SshErr_ChanOpenRejected",
        SshChannelFailureKind.ClosedBeforeConfirmation => "SshErr_ChanClosedBeforeConfirmation",
        SshChannelFailureKind.ChannelLimitReached => "SshErr_ChanLimitReached",
        SshChannelFailureKind.WindowBudgetExhausted => "SshErr_ChanWindowBudgetExhausted",
        SshChannelFailureKind.ExecRejected => "SshErr_ChanExecRejected",
        SshChannelFailureKind.PtyRejected => "SshErr_ChanPtyRejected",
        SshChannelFailureKind.ShellRejected => "SshErr_ChanShellRejected",
        SshChannelFailureKind.SubsystemRejected => "SshErr_ChanSubsystemRejected",
        SshChannelFailureKind.ZeroMaxPacket => "SshErr_ChanZeroMaxPacket",
        _ => null,
    };

    /// <summary>通道失败:按种类给出「下一步该查什么」,服务端给了说明就带上(清洗过)。</summary>
    internal static string LocalizeChannel(SshChannelException ex)
    {
        if (ChannelFailureKey(ex.FailureKind) is not { } key)
        {
            return LocalizeByReason(ex);
        }

        string text = ex.FailureKind switch
        {
            SshChannelFailureKind.UnknownChannelType => Strings.Format(key, PeerText.Sanitize(ex.ChannelType, 64)),
            SshChannelFailureKind.OpenRejected => Strings.Format(key,
                ex.OpenFailureReason is { } code ? ((uint)code).ToString(CultureInfo.InvariantCulture) : "?"),
            SshChannelFailureKind.ChannelLimitReached => Strings.Format(key, ex.Limit),
            SshChannelFailureKind.WindowBudgetExhausted => Strings.Format(key, (ex.Limit ?? 0) / (1024 * 1024)),
            // sftp 被拒最常见,给它指到 sshd_config 的那一行;子系统名是本端请求的,不来自对端。
            SshChannelFailureKind.SubsystemRejected when ex.SubsystemName == "sftp" => Strings.Get("SshErr_ChanSftpSubsystemRejected"),
            SshChannelFailureKind.SubsystemRejected => Strings.Format(key, ex.SubsystemName),
            _ => Strings.Get(key),
        };
        if (!string.IsNullOrWhiteSpace(ex.PeerDescription))
        {
            text = Strings.Format("SshErr_PeerSaid", text, PeerText.Sanitize(ex.PeerDescription));
        }
        return $"{text} [{ex.Reason} @ {ex.Phase}]";
    }

    // ------------------------------------------------------------ 私钥 / 证书

    /// <summary>
    /// 私钥或证书问题的文案键;没归类时为 <see langword="null" />。
    /// 两边都会出现的几种(读不出、没权限、格式不对、类型不支持)按 <paramref name="certificate" /> 分开说是私钥还是证书。
    /// </summary>
    internal static string? KeyProblemKey(SshKeyProblemKind kind, bool certificate) => kind switch
    {
        SshKeyProblemKind.FileUnreadable => certificate ? "SshErr_CertFileUnreadable" : "SshErr_KeyFileUnreadable",
        SshKeyProblemKind.FileAccessDenied => certificate ? "SshErr_CertFileAccessDenied" : "SshErr_KeyFileAccessDenied",
        SshKeyProblemKind.Malformed => certificate ? "SshErr_CertMalformed" : "SshErr_KeyMalformed",
        SshKeyProblemKind.UnsupportedKeyType => certificate ? "SshErr_CertUnsupportedKeyType" : "SshErr_KeyUnsupportedType",
        SshKeyProblemKind.FormatUnrecognized => "SshErr_KeyFormatUnrecognized",
        SshKeyProblemKind.PassphraseRequired => "SshErr_KeyPassphraseRequired",
        SshKeyProblemKind.PassphraseIncorrect => "SshErr_KeyPassphraseIncorrect",
        SshKeyProblemKind.UnsupportedCurve => "SshErr_KeyUnsupportedCurve",
        SshKeyProblemKind.UnsupportedCipher => "SshErr_KeyUnsupportedCipher",
        SshKeyProblemKind.UnsupportedKdf => "SshErr_KeyUnsupportedKdf",
        SshKeyProblemKind.UnsupportedFormatVersion => "SshErr_KeyUnsupportedFormatVersion",
        SshKeyProblemKind.UnsupportedPointFormat => "SshErr_KeyUnsupportedPointFormat",
        SshKeyProblemKind.MultipleKeys => "SshErr_KeyMultipleKeys",
        SshKeyProblemKind.LegacyPemEncryption => "SshErr_KeyLegacyPemEncryption",
        SshKeyProblemKind.KdfCostTooHigh => "SshErr_KeyKdfCostTooHigh",
        SshKeyProblemKind.KeyStoreUnavailable => "SshErr_KeyStoreUnavailable",
        SshKeyProblemKind.NotACertificate => "SshErr_CertNotACertificate",
        SshKeyProblemKind.HostCertificateForLogin => "SshErr_CertHostCertificateForLogin",
        SshKeyProblemKind.CertificateKeyMismatch => "SshErr_CertKeyMismatch",
        SshKeyProblemKind.CertificateKeyNotLoaded => "SshErr_CertKeyNotLoaded",
        _ => null,
    };

    /// <summary>私钥读不出来:按种类说原因,前面带上是哪个文件(<see cref="SshPrivateKeyException.Origin" />)。</summary>
    internal static string LocalizeKeyProblem(SshPrivateKeyException ex) =>
        KeyProblemText(ex, ex.ProblemKind, certificate: false, ex.Origin, ex.AlgorithmName, keyId: null, certificateKey: null, signerKey: null);

    /// <summary>证书读不出来或用不了:同上;证书与私钥不是一对时带上两边的指纹。</summary>
    internal static string LocalizeKeyProblem(SshCertificateException ex) =>
        KeyProblemText(ex, ex.ProblemKind, certificate: true, ex.Origin, ex.AlgorithmName, ex.KeyId, ex.CertificateKey, ex.SignerKey);

    private static string KeyProblemText(
        SshException ex, SshKeyProblemKind kind, bool certificate, string? origin, string? algorithmName,
        string? keyId, SshPublicKey? certificateKey, SshPublicKey? signerKey)
    {
        if (KeyProblemKey(kind, certificate) is not { } key)
        {
            return LocalizeByReason(ex);
        }

        string text = kind switch
        {
            // IO 错误的原话来自 .NET(按系统语言),路径在前面的来源里。
            SshKeyProblemKind.FileUnreadable => Strings.Format(key, ex.InnerException?.Message ?? ex.Reason.ToString()),
            SshKeyProblemKind.UnsupportedKeyType or SshKeyProblemKind.UnsupportedCurve or SshKeyProblemKind.UnsupportedCipher
                or SshKeyProblemKind.UnsupportedKdf or SshKeyProblemKind.UnsupportedPointFormat or SshKeyProblemKind.NotACertificate =>
                Strings.Format(key, PeerText.Sanitize(algorithmName, 128)),
            SshKeyProblemKind.HostCertificateForLogin => Strings.Format(key, PeerText.Sanitize(keyId, 128)),
            SshKeyProblemKind.CertificateKeyMismatch => Strings.Format(key,
                PeerText.Sanitize(keyId, 128), DescribeKey(certificateKey), DescribeKey(signerKey)),
            _ => Strings.Get(key),
        };
        if (!string.IsNullOrEmpty(origin))
        {
            text = Strings.Format("SshErr_KeyOrigin", origin, text);
        }
        return $"{text} [{ex.Reason} @ {ex.Phase}]";
    }

    private static string DescribeKey(SshPublicKey? key) => key is null ? "?" : $"{key.KeyType} {key.Sha256Fingerprint}";

    // ------------------------------------------------------------ 主机证书

    /// <summary>主机证书不合格的文案键;没归类时为 <see langword="null" />。</summary>
    internal static string? HostCertificateProblemKey(HostCertificateProblemKind kind) => kind switch
    {
        HostCertificateProblemKind.NotHostCertificate => "SshErr_HostCertNotHostCertificate",
        HostCertificateProblemKind.AuthorityKeyUnreadable => "SshErr_HostCertAuthorityKeyUnreadable",
        HostCertificateProblemKind.AuthorityKeyTooShort => "SshErr_HostCertAuthorityKeyTooShort",
        HostCertificateProblemKind.SignatureMalformed => "SshErr_HostCertSignatureMalformed",
        HostCertificateProblemKind.SignatureAlgorithmRejected => "SshErr_HostCertSignatureAlgorithmRejected",
        HostCertificateProblemKind.SignatureInvalid => "SshErr_HostCertSignatureInvalid",
        HostCertificateProblemKind.Expired => "SshErr_HostCertExpired",
        HostCertificateProblemKind.NotYetValid => "SshErr_HostCertNotYetValid",
        HostCertificateProblemKind.NoPrincipals => "SshErr_HostCertNoPrincipals",
        HostCertificateProblemKind.HostNotListed => "SshErr_HostCertHostNotListed",
        HostCertificateProblemKind.UnknownCriticalOptions => "SshErr_HostCertUnknownCriticalOptions",
        _ => null,
    };

    /// <summary>
    /// 主机证书哪里不合格(<see cref="KnownHostLookup.CertificateProblemDetail" />)的界面语言;没归类的照库的原文。
    /// </summary>
    /// <remarks>受信主机 CA 签的证书不合格时,<see cref="VelaHostKeyPolicy" /> 的拒绝理由用它 —— 同一个证书问题只有这一套文案;证书来自对端,名字一律清洗。</remarks>
    internal static string LocalizeHostCertificateProblem(HostCertificateProblem problem)
    {
        if (HostCertificateProblemKey(problem.Kind) is not { } key)
        {
            return problem.ToString();
        }

        string keyId = PeerText.Sanitize(problem.KeyId, 128);
        return problem.Kind switch
        {
            HostCertificateProblemKind.AuthorityKeyTooShort => Strings.Format(key, problem.KeyBits, problem.MinimumKeyBits),
            HostCertificateProblemKind.SignatureAlgorithmRejected => Strings.Format(key, keyId, PeerText.Sanitize(problem.Algorithm, 64)),
            HostCertificateProblemKind.Expired or HostCertificateProblemKind.NotYetValid =>
                Strings.Format(key, keyId, FormatTime(problem.Time)),
            HostCertificateProblemKind.HostNotListed => Strings.Format(key, keyId, JoinPeerNames(problem.Principals), problem.Host),
            HostCertificateProblemKind.UnknownCriticalOptions => Strings.Format(key, keyId, JoinPeerNames(problem.CriticalOptions)),
            _ => Strings.Format(key, keyId),
        };
    }

    // ------------------------------------------------------------ 建连:主机密钥、代理、跳板

    /// <summary>
    /// 建连失败的文案键;没归类时为 <see langword="null" />。
    /// <see cref="SshConnectFailureKind.HostKeyPolicyRejected" /> 也是 <see langword="null" />:那句话是主机密钥策略
    /// (宿主的 <see cref="VelaHostKeyPolicy" />)用界面语言写好的,原样用(见 <see cref="DescribeConnectFailure" />)。
    /// </summary>
    internal static string? ConnectFailureKey(SshConnectFailureKind kind) => kind switch
    {
        SshConnectFailureKind.TcpConnectFailed => "SshErr_TcpConnectFailed",
        SshConnectFailureKind.HostKeyMalformed => "SshErr_HostKeyMalformed",
        SshConnectFailureKind.HostKeyAlgorithmMismatch => "SshErr_HostKeyAlgorithmMismatch",
        SshConnectFailureKind.HostKeyTooShort => "SshErr_HostKeyTooShort",
        SshConnectFailureKind.StrictKexRequired => "SshErr_StrictKexRequired",
        SshConnectFailureKind.HostKeySignatureInvalid => "SshErr_HostKeySignatureInvalid",
        SshConnectFailureKind.HostKeyPolicyRejectedWithoutReason => "SshErr_HostKeyPolicyRejected",
        SshConnectFailureKind.ProxyConnectFailed => "SshErr_ProxyConnectFailed",
        SshConnectFailureKind.ProxyHandshakeTimeout => "SshErr_ProxyHandshakeTimeout",
        SshConnectFailureKind.ProxyClosedDuringHandshake => "SshErr_ProxyClosedDuringHandshake",
        SshConnectFailureKind.ProxyTargetNameInvalid => "SshErr_ProxyTargetNameInvalid",
        SshConnectFailureKind.Socks5CredentialsTooLong => "SshErr_Socks5CredentialsTooLong",
        SshConnectFailureKind.Socks5VersionMismatch => "SshErr_Socks5VersionMismatch",
        SshConnectFailureKind.Socks5NoAcceptableMethod => "SshErr_Socks5NoAcceptableMethod",
        SshConnectFailureKind.Socks5UnexpectedMethod => "SshErr_Socks5UnexpectedMethod",
        SshConnectFailureKind.Socks5InvalidAddressType => "SshErr_Socks5InvalidAddressType",
        SshConnectFailureKind.Socks5ConnectRefused => "SshErr_Socks5ConnectRefused",
        SshConnectFailureKind.HttpProxyNotHttp => "SshErr_HttpProxyNotHttp",
        SshConnectFailureKind.HttpProxyResponseTooLarge => "SshErr_HttpProxyResponseTooLarge",
        SshConnectFailureKind.HttpConnectRefused => "SshErr_HttpConnectRefused",
        SshConnectFailureKind.ProxyCommandExited => "SshErr_ProxyCommandExited",
        SshConnectFailureKind.JumpConnectFailed => "SshErr_JumpConnectFailed",
        SshConnectFailureKind.JumpConnectTimeout => "SshErr_JumpConnectTimeout",
        SshConnectFailureKind.JumpForwardRefused => "SshErr_JumpForwardRefused",
        SshConnectFailureKind.JumpForwardFailed => "SshErr_JumpForwardFailed",
        SshConnectFailureKind.JumpForwardTimeout => "SshErr_JumpForwardTimeout",
        _ => null,
    };

    /// <summary>SOCKS5 应答码(RFC 1928 §6 的 <c>REP</c>)的文案键;1–8 以外的码说「未知错误」。</summary>
    internal static string Socks5ReplyKey(int? code) => code switch
    {
        1 => "SshErr_Socks5Reply1",
        2 => "SshErr_Socks5Reply2",
        3 => "SshErr_Socks5Reply3",
        4 => "SshErr_Socks5Reply4",
        5 => "SshErr_Socks5Reply5",
        6 => "SshErr_Socks5Reply6",
        7 => "SshErr_Socks5Reply7",
        8 => "SshErr_Socks5Reply8",
        _ => "SshErr_Socks5ReplyOther",
    };

    /// <summary>建连失败按种类说(<see cref="Localize" /> 的一层):句子后面照旧带 <c>[原因 @ 阶段]</c>,转述内层原因的几种除外(内层自己带着)。</summary>
    internal static string LocalizeConnect(SshConnectException ex)
    {
        string text = DescribeConnectFailure(ex);
        return CarriesInnerCause(ex.FailureKind) || ex.FailureKind == SshConnectFailureKind.HostKeyPolicyRejected
            ? text
            : $"{text} [{ex.Reason} @ {ex.Phase}]";
    }

    /// <summary>
    /// 建连失败的那句话(不带尾巴):主机密钥是库自己验不过还是策略拒的、代理回了什么、卡在哪一跳。
    /// 没归类的照库的原文;代理那一路由 <see cref="ProxyTransportDialer" /> 拼进「代理连接失败」后半句。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>「库自己拒的」与「策略拒的」要分开</b>:同是 <see cref="SshFailureReason.HostKeyRejected" />,策略拒的那句话是
    /// <see cref="VelaHostKeyPolicy" /> 用界面语言写的(含指纹与下一步),原样用;解析不了、类型不符、RSA 太短、签名验不过
    /// 这几种发生在问策略之前,消息是库的中文,这里按种类另说。曾经一律当成策略的话照用。
    /// </para>
    /// <para>代理的应答(HTTP 状态行)、主机密钥类型、跳板与目标的地址拼进来之前一律清洗 —— 前两者来自对端,地址可能是用户粘进来的一串。</para>
    /// </remarks>
    internal static string DescribeConnectFailure(SshConnectException ex)
    {
        if (ex.FailureKind == SshConnectFailureKind.HostKeyPolicyRejected)
        {
            return ex.Message;
        }
        if (ConnectFailureKey(ex.FailureKind) is not { } key)
        {
            return ex.Message;
        }

        string via = PeerText.Sanitize(ex.Via, 256);
        string target = PeerText.Sanitize(ex.Target, 256);
        string code = ex.ProxyReplyCode is { } c ? c.ToString(CultureInfo.InvariantCulture) : "?";
        string hex = ex.ProxyReplyCode is { } h ? h.ToString("X2", CultureInfo.InvariantCulture) : "?";
        return ex.FailureKind switch
        {
            // 套接字错误码的枚举名跨语言一致(与库的原文一样),也最好搜。
            SshConnectFailureKind.TcpConnectFailed => Strings.Format(key, target,
                (ex.InnerException as System.Net.Sockets.SocketException)?.SocketErrorCode.ToString() ?? "?"),
            SshConnectFailureKind.HostKeyAlgorithmMismatch => Strings.Format(key,
                PeerText.Sanitize(ex.HostKeyType, 128), PeerText.Sanitize(ex.HostKeyAlgorithm, 128)),
            SshConnectFailureKind.HostKeyTooShort => Strings.Format(key, ex.HostKeyBits, ex.MinimumHostKeyBits),
            SshConnectFailureKind.HostKeySignatureInvalid => Strings.Format(key, PeerText.Sanitize(ex.HostKeyAlgorithm, 128)),
            SshConnectFailureKind.ProxyConnectFailed or SshConnectFailureKind.JumpConnectFailed =>
                Strings.Format(key, via, DescribeCause(ex.InnerException)),
            SshConnectFailureKind.JumpForwardRefused or SshConnectFailureKind.JumpForwardFailed =>
                Strings.Format(key, via, target, DescribeCause(ex.InnerException)),
            SshConnectFailureKind.ProxyHandshakeTimeout or SshConnectFailureKind.ProxyClosedDuringHandshake
                or SshConnectFailureKind.JumpForwardTimeout => Strings.Format(key, via, target),
            SshConnectFailureKind.ProxyTargetNameInvalid => Strings.Format(key, target),
            SshConnectFailureKind.Socks5VersionMismatch or SshConnectFailureKind.Socks5UnexpectedMethod
                or SshConnectFailureKind.Socks5InvalidAddressType => Strings.Format(key, via, hex),
            SshConnectFailureKind.Socks5ConnectRefused => Strings.Format(key, via, target, Strings.Get(Socks5ReplyKey(ex.ProxyReplyCode)), code),
            SshConnectFailureKind.HttpProxyNotHttp => Strings.Format(key, via, PeerText.Sanitize(ex.ProxyReply, 200)),
            SshConnectFailureKind.HttpConnectRefused => Strings.Format(key, via, target, PeerText.Sanitize(ex.ProxyReply, 200)),
            SshConnectFailureKind.ProxyCommandExited => string.IsNullOrWhiteSpace(ex.ProxyReply)
                ? Strings.Format(key, via, target, code)
                : Strings.Format("SshErr_ProxyCommandSaid", Strings.Format(key, via, target, code), PeerText.Sanitize(ex.ProxyReply)),
            SshConnectFailureKind.Socks5NoAcceptableMethod or SshConnectFailureKind.HttpProxyResponseTooLarge
                or SshConnectFailureKind.JumpConnectTimeout => Strings.Format(key, via),
            _ => Strings.Get(key),
        };
    }

    /// <summary>这几种在句子里转述内层(那一跳自己)的原因,内层的那句已经带着 <c>[原因 @ 阶段]</c>。</summary>
    private static bool CarriesInnerCause(SshConnectFailureKind kind) => kind is
        SshConnectFailureKind.ProxyConnectFailed or SshConnectFailureKind.JumpConnectFailed
        or SshConnectFailureKind.JumpForwardRefused or SshConnectFailureKind.JumpForwardFailed;

    /// <summary>
    /// 一跳自己的失败(代理、跳板的内层异常)用界面语言说:库的异常按它完整的译法(认证失败带尝试记录、协商失败带双方名单),
    /// 别的(套接字、IO)它的消息来自 .NET,按系统语言。
    /// </summary>
    private static string DescribeCause(Exception? inner) => inner switch
    {
        null => "?",
        SshException ssh => Translate(ssh)?.Message ?? ssh.Message,
        _ => inner.Message,
    };

    // ------------------------------------------------------------ 转发器起不来

    /// <summary>转发器起不来的文案键;没归类时为 <see langword="null" />。远程监听被拒按端口是不是特权端口分开说。</summary>
    internal static string? ForwardFailureKey(SshForwardFailureKind kind, bool privilegedPort = false) => kind switch
    {
        SshForwardFailureKind.ListenFailed => "SshErr_FwdListenFailed",
        SshForwardFailureKind.Ipv6LoopbackBindFailed => "SshErr_FwdIpv6LoopbackInUse",
        SshForwardFailureKind.SocketPathExists => "SshErr_FwdSocketPathExists",
        SshForwardFailureKind.SocketPathTooLong => "SshErr_FwdSocketPathTooLong",
        SshForwardFailureKind.RemoteListenRejected => privilegedPort ? "SshErr_FwdRemoteListenRejectedPrivileged" : "SshErr_FwdRemoteListenRejected",
        SshForwardFailureKind.RemoteSocketListenRejected => "SshErr_FwdRemoteSocketListenRejected",
        SshForwardFailureKind.NoBoundPortInReply => "SshErr_FwdNoBoundPortInReply",
        SshForwardFailureKind.AgentForwardRejected => "SshErr_FwdAgentForwardRejected",
        SshForwardFailureKind.LocalAgentUnavailable => "SshErr_FwdLocalAgentUnavailable",
        SshForwardFailureKind.X11ForwardRejected => "SshErr_FwdX11ForwardRejected",
        SshForwardFailureKind.NoX11Display => "SshErr_FwdNoX11Display",
        SshForwardFailureKind.XAuthTempDirectoryFailed => "SshErr_FwdXAuthTempDirectoryFailed",
        SshForwardFailureKind.XAuthStartFailed => "SshErr_FwdXAuthStartFailed",
        SshForwardFailureKind.XAuthTimedOut => "SshErr_FwdXAuthTimedOut",
        SshForwardFailureKind.XAuthFailed => "SshErr_FwdXAuthFailed",
        SshForwardFailureKind.XAuthNoCookie => "SshErr_FwdXAuthNoCookie",
        SshForwardFailureKind.ConfigForwardFailed => "SshErr_FwdConfigForwardFailed",
        SshForwardFailureKind.UnmatchedForwardedChannel => "SshErr_FwdUnmatchedChannel",
        SshForwardFailureKind.X11ForwardingExpired => "SshErr_FwdX11Expired",
        SshForwardFailureKind.ChannelLimitReached => "SshErr_FwdChannelLimitReached",
        SshForwardFailureKind.LocalTargetUnreachable => "SshErr_FwdLocalTargetUnreachable",
        SshForwardFailureKind.Stopped => "SshErr_FwdStopped",
        _ => null,
    };

    /// <summary>
    /// 转发器起不来:按种类说哪一步、该查什么,带上监听的端点、xauth 的退出码与输出。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 端口占用(<see cref="SshFailureReason.ForwardBindFailed" />)曾经刻意不翻 —— 消息里有地址与端口,换成一句通用文案帮不上忙;
    /// 现在地址在 <see cref="SshForwardException.Endpoint" />、套接字错误码在内层,照样说得具体。隧道面板仍在它那一层按配置说(TunnelService)。
    /// </para>
    /// <para>xauth 的输出库已清洗过;地址、路径、ssh_config 的原文与 X 显示名照样过一遍清洗再进文案。</para>
    /// </remarks>
    internal static string LocalizeForward(SshForwardException ex)
    {
        bool privileged = ex.Port is > 0 and < 1024;
        if (ForwardFailureKey(ex.FailureKind, privileged) is not { } key)
        {
            return LocalizeByReason(ex);
        }

        string endpoint = PeerText.Sanitize(ex.Endpoint, 256);
        string socketError = (ex.InnerException as System.Net.Sockets.SocketException)?.SocketErrorCode.ToString()
            ?? ex.InnerException?.Message ?? ex.Reason.ToString();
        bool inUse = ex.InnerException is System.Net.Sockets.SocketException { SocketErrorCode: System.Net.Sockets.SocketError.AddressAlreadyInUse };

        string text = ex.FailureKind switch
        {
            // 被占才是「换端口 / 只听 127.0.0.1」那句建议;别的错误按普通的起不来说。
            SshForwardFailureKind.ListenFailed when inUse => Strings.Format("SshErr_FwdListenInUse", endpoint),
            SshForwardFailureKind.ListenFailed => Strings.Format(key, endpoint, socketError),
            SshForwardFailureKind.Ipv6LoopbackBindFailed when !inUse => Strings.Format("SshErr_FwdListenFailed", endpoint, socketError),
            SshForwardFailureKind.Ipv6LoopbackBindFailed or SshForwardFailureKind.SocketPathExists or SshForwardFailureKind.SocketPathTooLong
                or SshForwardFailureKind.RemoteListenRejected or SshForwardFailureKind.RemoteSocketListenRejected
                or SshForwardFailureKind.UnmatchedForwardedChannel or SshForwardFailureKind.LocalTargetUnreachable => Strings.Format(key, endpoint),
            SshForwardFailureKind.LocalAgentUnavailable => Strings.Format(key, DescribeCause(ex.InnerException)),
            SshForwardFailureKind.ConfigForwardFailed => Strings.Format(key, PeerText.Sanitize(ex.ConfigLine, 200), DescribeCause(ex.InnerException)),
            SshForwardFailureKind.XAuthTempDirectoryFailed or SshForwardFailureKind.XAuthStartFailed =>
                Strings.Format(key, ex.InnerException?.Message ?? "?"),
            SshForwardFailureKind.XAuthTimedOut => Strings.Format(key,
                ex.Timeout is { } t ? t.TotalSeconds.ToString("0", CultureInfo.InvariantCulture) : "?"),
            SshForwardFailureKind.XAuthFailed => string.IsNullOrWhiteSpace(ex.ErrorOutput)
                ? Strings.Format(key, ex.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "?")
                : Strings.Format("SshErr_FwdXAuthSaid",
                    Strings.Format(key, ex.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "?"), PeerText.SanitizeTail(ex.ErrorOutput, 1024)),
            SshForwardFailureKind.XAuthNoCookie => Strings.Format(key, PeerText.Sanitize(ex.DisplayName, 128)),
            SshForwardFailureKind.ChannelLimitReached => Strings.Format(key, ex.Limit?.ToString(CultureInfo.InvariantCulture) ?? "?"),
            _ => Strings.Get(key),
        };
        return CarriesForwardCause(ex.FailureKind) ? text : $"{text} [{ex.Reason} @ {ex.Phase}]";
    }

    /// <summary>这两种在句子里转述内层(agent 那边、那一条转发自己)的原因,内层的那句已经带着 <c>[原因 @ 阶段]</c>。</summary>
    private static bool CarriesForwardCause(SshForwardFailureKind kind) =>
        kind is SshForwardFailureKind.LocalAgentUnavailable or SshForwardFailureKind.ConfigForwardFailed;

    // ------------------------------------------------------------ ssh-agent

    /// <summary>agent 失败的文案键;没归类时为 <see langword="null" />。加钥被拒时带没带目的地约束分开说。</summary>
    internal static string? AgentFailureKey(SshAgentFailureKind kind, bool destinationConstraints = false) => kind switch
    {
        SshAgentFailureKind.NotRunning => "SshErr_AgentNotRunning",
        SshAgentFailureKind.ConnectFailed => "SshErr_AgentConnectFailed",
        SshAgentFailureKind.PipeOwnerUnreadable => "SshErr_AgentPipeOwnerUnreadable",
        SshAgentFailureKind.PipeOwnerUntrusted => "SshErr_AgentPipeOwnerUntrusted",
        SshAgentFailureKind.ConversationOutOfStep => "SshErr_AgentOutOfStep",
        SshAgentFailureKind.ClosedBeforeReply => "SshErr_AgentClosedBeforeReply",
        SshAgentFailureKind.CommunicationFailed => "SshErr_AgentCommunicationFailed",
        SshAgentFailureKind.MalformedReply => "SshErr_AgentMalformedReply",
        SshAgentFailureKind.SignatureAlgorithmMismatch => "SshErr_AgentSignatureAlgorithmMismatch",
        SshAgentFailureKind.SignRefused => "SshErr_AgentSignRefused",
        SshAgentFailureKind.AddRefused => destinationConstraints ? "SshErr_AgentAddRefusedDestination" : "SshErr_AgentAddRefused",
        SshAgentFailureKind.RemoveAllRefused => "SshErr_AgentRemoveAllRefused",
        SshAgentFailureKind.RequestTooLarge => "SshErr_AgentRequestTooLarge",
        _ => null,
    };

    /// <summary>
    /// ssh-agent 的失败:没在跑、连不上、管道属主不可信、拒签 / 拒绝加钥、签名算法对不上(太老的 agent)……按种类说。
    /// </summary>
    /// <remarks>
    /// 曾经 <see cref="SshFailureReason.AgentUnavailable" /> 与 agent 的 <see cref="SshFailureReason.Unsupported" /> 一律透传库的中文
    /// (具体信息 —— SID、IO 错误、两个算法名 —— 只在句子里)。agent 回的算法名来自 agent,清洗之后才进文案;IO 错误的原话来自 .NET,按系统语言。
    /// </remarks>
    internal static string LocalizeAgent(SshAgentException ex)
    {
        if (AgentFailureKey(ex.FailureKind, ex.HasDestinationConstraints) is not { } key)
        {
            return LocalizeByReason(ex);
        }

        string endpoint = PeerText.Sanitize(ex.Endpoint, 256);
        string text = ex.FailureKind switch
        {
            SshAgentFailureKind.ConnectFailed => Strings.Format(key, endpoint, ex.InnerException?.Message ?? "?"),
            SshAgentFailureKind.CommunicationFailed => Strings.Format(key, ex.InnerException?.Message ?? "?"),
            SshAgentFailureKind.PipeOwnerUnreadable => Strings.Format(key, endpoint),
            SshAgentFailureKind.PipeOwnerUntrusted => Strings.Format(key, endpoint, ex.PipeOwner ?? "?"),
            SshAgentFailureKind.SignatureAlgorithmMismatch => Strings.Format(key,
                PeerText.Sanitize(ex.SignedAlgorithm, 64), PeerText.Sanitize(ex.RequestedAlgorithm, 64)),
            _ => Strings.Get(key),
        };
        return $"{text} [{ex.Reason} @ {ex.Phase}]";
    }

    // ------------------------------------------------------------ 转发的单条连接失败

    /// <summary>
    /// 转发上一条连接失败的文案键(<see cref="ForwardErrorEventArgs" />)。超时与否分开说;并发上限按「拒了一条 / 拒了好几条」分开说。
    /// </summary>
    internal static string ForwardErrorKey(ForwardErrorReason reason, bool timedOut = false, bool many = false) => reason switch
    {
        ForwardErrorReason.Accept => "SshErr_FwdAccept",
        ForwardErrorReason.ConnectionLimit => many ? "SshErr_FwdConnectionLimitMany" : "SshErr_FwdConnectionLimit",
        ForwardErrorReason.SocksHandshake => timedOut ? "SshErr_FwdSocksTimeout" : "SshErr_FwdSocksInvalid",
        ForwardErrorReason.ChannelOpen => timedOut ? "SshErr_FwdChannelOpenTimeout" : "SshErr_FwdChannelOpenFailed",
        ForwardErrorReason.TargetConnect => "SshErr_FwdTargetConnect",
        ForwardErrorReason.Relay => "SshErr_FwdRelay",
        ForwardErrorReason.SetupSkipped => "SshErr_FwdSetupSkipped",
        ForwardErrorReason.TargetNotPermitted => "TunnelSvc_TargetNotPermitted",
        _ => "SshErr_FwdUnknown",
    };

    /// <summary>
    /// 一条转发连接失败、而事件里<b>没有异常可译</b>时(撞并发上限、SOCKS 握手没完成或不合法、隧道在时限内没打开)的界面语言。
    /// </summary>
    /// <remarks>
    /// 有异常的那几种由调用方照异常翻(隧道面板的 <c>TunnelService.DescribeForwardError</c>:套接字错误码、开通道的原因码、库异常的种类)。
    /// 曾经显示成「Reason:库的中文消息」。目标名来自 SOCKS 客户端(远程动态转发时来自对端),清洗之后才进文案。
    /// </remarks>
    internal static string LocalizeForwardError(ForwardErrorEventArgs e)
    {
        string key = ForwardErrorKey(e.Reason, timedOut: e.Timeout is not null, many: e.RejectedCount > 1);
        string target = PeerText.Sanitize(e.Target, 256);
        string seconds = e.Timeout is { } timeout ? timeout.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture) : "?";
        return e.Reason switch
        {
            ForwardErrorReason.ConnectionLimit => Strings.Format(key, e.Limit?.ToString(CultureInfo.InvariantCulture) ?? "?", e.RejectedCount ?? 1),
            ForwardErrorReason.SocksHandshake when e.Timeout is not null => Strings.Format(key, seconds),
            ForwardErrorReason.ChannelOpen when e.Timeout is not null => Strings.Format(key, target, seconds),
            ForwardErrorReason.ChannelOpen or ForwardErrorReason.TargetConnect or ForwardErrorReason.Relay => Strings.Format(key, target),
            _ => Strings.Get(key),
        };
    }

    // ------------------------------------------------------------ 工具

    /// <summary>证书里的时刻按 UTC 的「可排序」格式写(与库的原文一致,跨语言不变,也不受本机时区影响)。</summary>
    private static string FormatTime(DateTimeOffset? time) =>
        time is { } t ? t.ToString("u", CultureInfo.InvariantCulture) : "?";

    /// <summary>证书里的一串名字:逐个清洗,最多列 <see cref="MaxPrincipalsShown" /> 个。</summary>
    private static string JoinPeerNames(IReadOnlyList<string> names)
    {
        string joined = string.Join(", ", names.Take(MaxPrincipalsShown).Select(static n => PeerText.Sanitize(n, 64)));
        return names.Count > MaxPrincipalsShown ? $"{joined}, …" : joined;
    }
}

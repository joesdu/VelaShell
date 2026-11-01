// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   RFC 4252 §5    USERAUTH_REQUEST / FAILURE / SUCCESS,以及 partial success
//   RFC 4252 §5.4  USERAUTH_BANNER
//   RFC 4252 §7    publickey 的两段式与签名输入
//   RFC 4252 §8    password,含 PASSWD_CHANGEREQ(60)
//   RFC 4253 §10   SERVICE_REQUEST / SERVICE_ACCEPT
//   RFC 4256       keyboard-interactive,INFO_REQUEST(60) / INFO_RESPONSE(61)
//   RFC 8308 §3.1  server-sig-algs
//   行为规格:      velashell-docs/zh/ssh/spec/04-authentication.md 全部

using System.Buffers;
using System.Diagnostics;
using VelaShell.Ssh.Diagnostics;
using VelaShell.Ssh.Keys;
using VelaShell.Ssh.Protocol;
using VelaShell.Ssh.Transport;

namespace VelaShell.Ssh.Auth;

/// <summary>一次认证的结果。</summary>
/// <param name="Method">最终成功的那个方法。</param>
/// <param name="Attempts">逐条尝试记录。</param>
/// <param name="Banner">服务端发来的横幅文本（按出现顺序）。</param>
/// <param name="ServerSignatureAlgorithms">服务端通过 <c>server-sig-algs</c> 宣告的签名算法。</param>
/// <param name="ServerExtensions">服务端在 <c>EXT_INFO</c> 里宣告过的扩展名。</param>
internal sealed record SshAuthenticationResult(
    string Method,
    IReadOnlyList<SshAuthAttempt> Attempts,
    IReadOnlyList<string> Banner,
    IReadOnlyList<string> ServerSignatureAlgorithms,
    IReadOnlyList<string> ServerExtensions);

/// <summary>执行用户认证（客户端侧）。</summary>
/// <remarks>
/// <para>
/// 方法调度（velashell-docs/zh/ssh/spec/04 §2.2）：先发一次 <c>none</c> 问出服务端接受哪些方法，
/// 然后**按使用者给出的凭据顺序**逐个尝试，跳过服务端不接受的那些。
/// </para>
/// <para>
/// <b>不做任何隐式回退</b> —— 不自动读默认私钥、不自动连 ssh-agent。
/// 理由见 <see cref="SshCredential"/>。
/// </para>
/// </remarks>
/// <remarks>创建一个认证执行器。</remarks>
/// <param name="transport">已完成密钥交换的传输。</param>
/// <param name="userName">用户名。</param>
/// <param name="sessionId">会话标识（公钥签名的第一个输入）。</param>
internal sealed class SshAuthenticator(SshPacketTransport transport, string userName, byte[] sessionId)
{
    private const int MaxFieldBytes = 64 * 1024;

    /// <summary>keyboard-interactive 一轮里每个字符串（名字、说明、语言标记、提示）的上限。</summary>
    /// <remarks>
    /// 〔velashell-docs/zh/ssh/spec/04 §6.4〕4 KiB。这些文字来自还没认证的对端、要原样摆到界面上；
    /// 曾经名字与说明放到了 64 KiB（只有提示是 4 KiB），与规格不符。
    /// </remarks>
    private const int MaxKeyboardStringBytes = 4 * 1024;
    private const int MaxBannerBytes = 256 * 1024;
    private const int MaxBannerCount = 1024;

    // 60–79 是各认证方法自己的报文编号（RFC 4250 §4.1.2）：同一个数在不同方法里含义不同，
    // 所以不进 SshMessageNumber，而是按方法在这里起名。
    private const byte FirstMethodSpecificNumber = 60;
    private const byte LastMethodSpecificNumber = 79;
    private const byte PasswordChangeRequest = 60;   // password：SSH_MSG_USERAUTH_PASSWD_CHANGEREQ（RFC 4252 §8）
    private const byte PublicKeyOk = 60;             // publickey：SSH_MSG_USERAUTH_PK_OK（RFC 4252 §7）
    private const byte InfoRequest = 60;             // keyboard-interactive：SSH_MSG_USERAUTH_INFO_REQUEST（RFC 4256 §3.2）
    private const byte InfoResponse = 61;            // keyboard-interactive：SSH_MSG_USERAUTH_INFO_RESPONSE（RFC 4256 §3.4）

    /// <summary>一次 keyboard-interactive 允许的最大轮数。</summary>
    /// <remarks>
    /// 服务端理论上可以无限问下去 —— 那是拿用户注意力做的拒绝服务。
    /// </remarks>
    public const int MaxKeyboardInteractiveRounds = 64;

    /// <summary>一轮 keyboard-interactive 允许的最大提示数。</summary>
    public const int MaxKeyboardPrompts = 32;

    private readonly SshPacketTransport _transport = transport ?? throw new ArgumentNullException(nameof(transport));
    private readonly string _userName = userName ?? throw new ArgumentNullException(nameof(userName));
    private readonly byte[] _sessionId = sessionId ?? throw new ArgumentNullException(nameof(sessionId));

    private readonly List<SshAuthAttempt> _attempts = [];
    private readonly List<string> _banner = [];
    // string 的默认相等比较器就是序数比较,不必再传 StringComparer.Ordinal。
    private readonly Dictionary<string, int> _failureCounts = [];

    private string[] _serverOffered = [];
    private string[] _serverSignatureAlgorithms = [];
    private readonly List<string> _serverExtensions = [];
    private bool _partialSuccessAchieved;
    private bool _passwordChangeRequested;

    /// <summary>
    /// 同一个方法在这次认证里累计失败多少次之后不再重试（不随部分成功清零）。
    /// </summary>
    /// <remarks>
    /// 服务端通常也有自己的计数，撞满会被临时封禁。
    /// <b>只管 password 与 keyboard-interactive</b>：publickey 的每一把钥是不同的凭据，
    /// 每把只试一次，总数由服务端的 <c>MaxAuthTries</c> 决定。
    /// </remarks>
    public int MaxFailuresPerMethod { get; init; } = 3;

    /// <summary>
    /// 是否允许 RSA 降级到 SHA-1 签名（<c>ssh-rsa</c>）。
    /// </summary>
    /// <remarks>
    /// 〔决策，velashell-docs/zh/ssh/spec/04 §4.4〕**默认关闭**。无条件降级会把 Terrapin 那类
    /// 降级攻击的收益还回去。需要连老服务器的人显式打开。
    /// </remarks>
    public bool AllowSha1RsaSignatures { get; init; }

    /// <summary>横幅回调。文本来自**未认证**的对端，是注入面。</summary>
    public Func<string, CancellationToken, ValueTask>? BannerHandler { get; init; }

    /// <summary>标着 <c>always_display</c> 的 <c>SSH_MSG_DEBUG</c> 的回调（<c>SshConnectionOptions.DebugMessageHandler</c>）。</summary>
    public Func<string, CancellationToken, ValueTask>? DebugMessageHandler { get; init; }

    /// <summary>认证期间对端发起了重协商（收到 <c>KEXINIT</c>，载荷交进来）：就地把这次交换做完。</summary>
    /// <remarks>
    /// 〔RFC 4253 §9〕任何时刻都可以重协商 —— 用户找动态码花了几分钟，服务端按时间的 <c>RekeyLimit</c> 就会发起。
    /// 认证阶段只有认证器一个读者、一个写者，交换就在这条传输上原地跑（由建连代码给出，认证器不依赖会话层）。
    /// 曾经没有这条路：KEXINIT 被当成「意外的报文」，连接以协议错误失败。没给时照旧报协议错误，但说清是什么。
    /// </remarks>
    internal Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask>? PeerKexInitHandler { get; init; }

    /// <summary>首次密钥交换里服务端的身份证明；背后是 ssh-agent 的签名器拿它向 agent 声明会话（spec/07 §7.4）。</summary>
    /// <remarks><see langword="null"/> 时不声明 —— 只有绕过连接工厂直接跑认证的测试会这样。</remarks>
    public SshSessionProof? SessionProof { get; init; }

    /// <summary>执行认证。</summary>
    /// <param name="credentials">凭据，按偏好排序。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <exception cref="SshAuthenticationException">所有方法都试完仍未成功。</exception>
    public async ValueTask<SshAuthenticationResult> AuthenticateAsync(
        IReadOnlyList<SshCredential> credentials, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credentials);

        try
        {
            return await AuthenticateCoreAsync(credentials, cancellationToken).ConfigureAwait(false);
        }
        catch (SshWireFormatException ex)
        {
            // 服务端的认证报文读不通。内部的格式异常不该漏给使用者 —— 它说的是「对端违反协议」。
            throw new SshProtocolException(
                SshPhase.Authenticating, $"服务端的认证报文格式非法：{ex.Message}", ex);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // 与版本交换那一步同一个理由：这是**连接断了**，不是一个 IO 细节。
            throw new SshConnectionClosedException(
                SshFailureReason.ClosedByPeer, SshPhase.Authenticating,
                $"认证期间连接中断：{ex.Message}", ex);
        }
    }

    private async ValueTask<SshAuthenticationResult> AuthenticateCoreAsync(
        IReadOnlyList<SshCredential> credentials, CancellationToken cancellationToken)
    {
        await RequestUserAuthServiceAsync(cancellationToken).ConfigureAwait(false);

        // ① 先发 none 探一次。它几乎总会失败，但 FAILURE 里带回服务端接受的方法列表 ——
        //    那是唯一能问到这份清单的途径。它也**可能成功**（服务端配了无认证）。
        if (await TryNoneAsync(cancellationToken).ConfigureAwait(false))
        {
            return BuildResult(SshProtocolNames.AuthNone);
        }

        // ② 按**使用者给出的顺序**逐个试。
        //
        //    ⚠️ **部分成功之后从头再扫一遍。**多因素（AuthenticationMethods publickey,password）时，
        //    服务端一开始只报 publickey，排在前面的口令凭据于是被「不接受」跳过；公钥那一步成功之后
        //    服务端才开放 password —— 可循环已经走过去了，认证以「凭据试完了」失败。
        //    重扫只重试**当时因方法不被接受而跳过**的凭据：试过的（成功一步、失败、材料有问题）不再试 ——
        //    同一把钥换个时机也不会变成对的，白白耗掉服务端的尝试次数。
        HashSet<SshCredential> tried = [with(ReferenceEqualityComparer.Instance)];
        bool rescan = true;
        while (rescan)
        {
            rescan = false;

            foreach (SshCredential credential in credentials)
            {
                if (credential is NoneCredential || tried.Contains(credential))
                {
                    continue;   // none 已经试过了；别的试过一次就够
                }

                SshCredential effective = credential;

                if (!_serverOffered.Contains(effective.MethodName, StringComparer.Ordinal))
                {
                    // 服务端不接受 password 但接受 keyboard-interactive 时，改走后者
                    // （velashell-docs/zh/ssh/spec/04 §6.5）。这不是取巧 —— 大量服务器只开
                    // keyboard-interactive，而它唯一的提示就是「Password:」，
                    // OpenSSH 客户端也是这么做的。
                    KeyboardInteractiveCredential? bridged = TryBridgeToKeyboardInteractive(effective);
                    if (bridged is null)
                    {
                        Record(effective, SshAuthOutcome.SkippedNotOffered,
                            [new SshAuthDetail(SshAuthDetailKind.MethodNotOffered) { Methods = _serverOffered }]);
                        continue;
                    }
                    effective = bridged;
                }

                // 失败上限只管 password 与 keyboard-interactive：那是对**同一个秘密**的重试，
                // 连错几次会被服务端封一阵。publickey 的每一把钥是不同的凭据 —— agent 里有五把钥、
                // 对的是第四把时，第三把之后就不试了，那就永远登不上；它们的总数由服务端的
                // MaxAuthTries 管。
                if (effective.MethodName != SshProtocolNames.AuthPublicKey
                    && _failureCounts.GetValueOrDefault(effective.MethodName) >= MaxFailuresPerMethod)
                {
                    // 计数是整次认证累计的，不随部分成功清零 —— 所以不说「连续」。
                    Record(effective, SshAuthOutcome.SkippedNotOffered,
                        [new SshAuthDetail(SshAuthDetailKind.MethodFailureLimit) { Method = effective.MethodName, Count = MaxFailuresPerMethod }]);
                    continue;
                }

                tried.Add(credential);
                AuthStepResult step = await TryCredentialAsync(effective, cancellationToken).ConfigureAwait(false);

                if (step.Outcome == SshAuthOutcome.Success)
                {
                    return BuildResult(effective.MethodName);
                }

                if (step.Outcome == SshAuthOutcome.PartialSuccess)
                {
                    // **这一步成功了。**不计失败；服务端的方法列表变了，从头按新列表再挑。
                    _partialSuccessAchieved = true;
                    rescan = true;
                    break;
                }

                if (step.Outcome == SshAuthOutcome.Failure)
                {
                    _failureCounts[effective.MethodName] =
                        _failureCounts.GetValueOrDefault(effective.MethodName) + 1;
                }
            }
        }

        throw BuildExhaustedException();
    }

    /// <summary>
    /// 服务端不接受某个凭据的方法，但也许能换条路走。
    /// </summary>
    /// <returns>换用的凭据；换不了返回 <see langword="null"/>。</returns>
    private KeyboardInteractiveCredential? TryBridgeToKeyboardInteractive(SshCredential credential)
    {
        if (credential is not PasswordCredential { CanAnswerKeyboardInteractive: true } password
            || !_serverOffered.Contains(SshProtocolNames.AuthKeyboardInteractive, StringComparer.Ordinal))
        {
            return null;
        }

        // 这条桥接凭据只用于这一次键盘交互（每次桥接都新造一个），所以「答过了」记在这里就够了。
        bool answered = false;
        return new KeyboardInteractiveCredential(
            async (challenge, cancellationToken) =>
            {
                // 只在「恰好一条不回显提示」时填密码 —— 那几乎一定是「Password:」。
                //
                // 其它形状（多条提示、要回显的提示）意味着这是真正的多因素询问。
                // 此时**必须仍然回够条数**：中途放弃会把服务端晾在等应答的状态上，
                // 让整条会话卡住。回空串让服务端干脆地拒绝，我们再换下一条凭据。
                //
                // ⚠️ **同一次键盘交互里密码至多答一次**（velashell-docs/zh/ssh/spec/04 §6.5）。
                //    PAM 两步验证常见的流程是「Password:」一轮、「Verification code:」再一轮，两轮都是单条不回显。
                //    曾经只看形状不看轮次，第二轮也把密码发了出去：白耗一次失败计数，而且 pam_radius、Duo 一类模块
                //    会把这一轮的应答转发到 RADIUS 或第三方服务 —— 密码就这样离开了目标主机。
                if (answered || challenge.Prompts is not [{ Echo: false }])
                {
                    return [.. challenge.Prompts.Select(static _ => string.Empty)];
                }

                answered = true;
                return [await password.GetPasswordAsync(cancellationToken).ConfigureAwait(false)];
            },
            label: $"{password.Label}（经 keyboard-interactive）")
        {
            BridgedFrom = password,
        };
    }

    // ------------------------------------------------------------ 服务请求

    private async ValueTask RequestUserAuthServiceAsync(CancellationToken cancellationToken)
    {
        ArrayBufferWriter<byte> request = new();
        SshDataWriter writer = new(request);
        writer.WriteMessageNumber(SshMessageNumber.ServiceRequest);
        writer.WriteUtf8String(SshProtocolNames.ServiceUserAuth);

        _transport.WritePacket(request.WrittenSpan);
        await _transport.FlushAsync(cancellationToken).ConfigureAwait(false);

        SshInboundPacket packet = await ReadAsync(cancellationToken).ConfigureAwait(false);
        if (packet.MessageNumber != SshMessageNumber.ServiceAccept)
        {
            throw new SshProtocolException(
                SshPhase.Authenticating,
                $"请求 ssh-userauth 服务时期望 ServiceAccept，收到 {packet.MessageNumber}。");
        }
    }

    // ------------------------------------------------------------ 各方法

    private async ValueTask<bool> TryNoneAsync(CancellationToken cancellationToken)
    {
        NoneCredential none = new();

        ArrayBufferWriter<byte> request = new();
        SshDataWriter writer = new(request);
        WriteRequestHeader(ref writer, SshProtocolNames.AuthNone);

        _transport.WritePacket(request.WrittenSpan);
        await _transport.FlushAsync(cancellationToken).ConfigureAwait(false);

        AuthStepResult outcome = await ReadAuthOutcomeAsync(null, cancellationToken).ConfigureAwait(false);
        Record(none, outcome.Outcome, outcome.Outcome == SshAuthOutcome.Failure
            ? [new SshAuthDetail(SshAuthDetailKind.ServerAccepts) { Methods = _serverOffered }]
            : null);

        return outcome.Outcome == SshAuthOutcome.Success;
    }

    private async ValueTask<AuthStepResult> TryCredentialAsync(
        SshCredential credential, CancellationToken cancellationToken)
    {
        AuthStepResult step;

        try
        {
            step = credential switch
            {
                PasswordCredential password => await TryPasswordAsync(password, cancellationToken).ConfigureAwait(false),
                PublicKeyCredential publicKey => await TryPublicKeyAsync(publicKey, cancellationToken).ConfigureAwait(false),
                KeyboardInteractiveCredential kbd => await TryKeyboardInteractiveAsync(kbd, cancellationToken).ConfigureAwait(false),
                // SshCredential 的构造函数是 private protected，库外写不出别的子类；none 在调度之前就滤掉了。
                _ => throw new UnreachableException($"认证器不认识的凭据类型 {credential.GetType().Name}。"),
            };
        }
        catch (CredentialMaterialException ex)
        {
            // 凭据自己出问题（私钥读不出来、agent 拒签、外部签名器不可用）不该打断整条链 ——
            // 后面还有别的凭据可以试。但**必须如实记下来**：
            // 「私钥文件读不出来」与「服务端不认这把钥」是两件事。
            //
            // ⚠️ **只接凭据自己抛的**（见 CredentialMaterialException）。曾经这里按异常类型筛：
            //    「非 SshException 一律当成凭据问题」—— 结果两头都错：agent 拒签抛的
            //    SshAgentException 是 SshException，整条链被它打断；而断网的 IOException、
            //    横幅回调抛的异常反倒被当成「跳过」，此时请求已经发出、应答还没读，
            //    下一条凭据读到的是上一条的应答。
            step = new AuthStepResult(SshAuthOutcome.SkippedNoMaterial,
                [new SshAuthDetail(SshAuthDetailKind.CredentialFailed) { Error = ex.InnerException }]);
        }

        Record(credential, step.Outcome, step.Details, step.SignatureAlgorithm);
        return step;
    }

    /// <summary>凭据自己出的问题：取口令、签名、回答挑战的回调抛出来的异常。</summary>
    /// <remarks>
    /// 只在**还没有请求在途**的地方包它（发请求之前、或读完上一个应答之后），
    /// 所以跳过这条凭据、接着发下一条请求不会让应答错位。
    /// </remarks>
    private sealed class CredentialMaterialException(Exception inner) : Exception(inner.Message, inner);

    /// <summary>调用凭据的回调或签名器；它抛的异常（取消除外）一律算凭据自己的问题。</summary>
    private static async ValueTask<T> FromCredentialAsync<T>(Func<ValueTask<T>> call)
    {
        try
        {
            return await call().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new CredentialMaterialException(ex);
        }
    }

    private async ValueTask<AuthStepResult> TryPasswordAsync(
        PasswordCredential credential, CancellationToken cancellationToken)
    {
        string password = await FromCredentialAsync(() => credential.GetPasswordAsync(cancellationToken))
            .ConfigureAwait(false);

        await SendPasswordRequestAsync(password, newPassword: null, cancellationToken).ConfigureAwait(false);

        // SSH_MSG_USERAUTH_PASSWD_CHANGEREQ：服务端要求先改密码（〔velashell-docs/zh/ssh/spec/04 §5.1〕）。
        // 配了取新密码的回调就改；没配、或者没改成，**要把原因说清楚** ——
        // 「客户端直接断开且不说为什么」是用户最难自救的一种失败。
        for (int attempt = 1; ; attempt++)
        {
            string? changePrompt = null;
            AuthStepResult outcome = await ReadAuthOutcomeAsync(
                onMethodSpecific: (number, payload) =>
                {
                    if (number != PasswordChangeRequest)
                    {
                        return null;
                    }
                    changePrompt = ParsePasswordChangeRequest(payload);
                    return new AuthStepResult(SshAuthOutcome.Failure);
                },
                cancellationToken).ConfigureAwait(false);

            if (changePrompt is null)
            {
                // 认证有了结论。发过改密码请求的话，结论就是改密码的结果（RFC 4252 §8）。
                if (attempt == 1)
                {
                    return outcome;
                }
                if (outcome.Outcome is SshAuthOutcome.Success or SshAuthOutcome.PartialSuccess)
                {
                    _passwordChangeRequested = false;
                    return outcome with { Details = [new SshAuthDetail(SshAuthDetailKind.PasswordChanged)] };
                }
                return outcome with { Details = [new SshAuthDetail(SshAuthDetailKind.PasswordChangeFailed)] };
            }

            // 记下来：方法都试完时据此报 PasswordExpired，而不是笼统的「方法用尽」。
            _passwordChangeRequested = true;
            if (credential.NewPasswordProvider is not { } provider)
            {
                return new AuthStepResult(SshAuthOutcome.Failure,
                    [new SshAuthDetail(SshAuthDetailKind.PasswordChangeNotConfigured)]);
            }
            if (attempt > PasswordCredential.MaxNewPasswordAttempts)
            {
                return new AuthStepResult(SshAuthOutcome.Failure,
                [
                    new SshAuthDetail(SshAuthDetailKind.NewPasswordRejected)
                    {
                        Count = PasswordCredential.MaxNewPasswordAttempts,
                        ServerText = changePrompt,
                    },
                ]);
            }

            // 此时服务端的应答已经读完，没有请求在途：回调出错换下一条凭据不会让应答错位。
            SshPasswordChangeRequest ask = new() { Prompt = changePrompt, Attempt = attempt };
            string? newPassword = await FromCredentialAsync(() => provider(ask, cancellationToken)).ConfigureAwait(false);
            if (newPassword is null)
            {
                return new AuthStepResult(SshAuthOutcome.Failure,
                    [new SshAuthDetail(SshAuthDetailKind.NewPasswordNotProvided)]);
            }

            await SendPasswordRequestAsync(password, newPassword, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>发 <c>password</c> 请求；带 <paramref name="newPassword"/> 时是改密码请求（RFC 4252 §8）。</summary>
    private async ValueTask SendPasswordRequestAsync(string password, string? newPassword, CancellationToken cancellationToken)
    {
        ArrayBufferWriter<byte> request = new();
        SshDataWriter writer = new(request);
        WriteRequestHeader(ref writer, SshProtocolNames.AuthPassword);
        writer.WriteBoolean(newPassword is not null);   // 是不是改密码请求
        writer.WriteUtf8String(password);
        if (newPassword is not null)
        {
            writer.WriteUtf8String(newPassword);
        }

        // 请求里是明文密码：封进传输的缓冲之后就把这一份清零（velashell-docs/zh/ssh/spec/04 §5.2）。
        _transport.WritePacket(request.WrittenSpan);
        request.Clear();
        await _transport.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>解 <c>SSH_MSG_USERAUTH_PASSWD_CHANGEREQ</c>：提示文本（语言标记忽略）。</summary>
    private static string ParsePasswordChangeRequest(ReadOnlyMemory<byte> payload)
    {
        SshDataReader reader = new(new ReadOnlySequence<byte>(payload));
        reader.ReadByte();   // PasswordChangeRequest
        string prompt = reader.ReadUtf8String(MaxKeyboardStringBytes);
        _ = reader.ReadUtf8String(MaxKeyboardStringBytes);   // 语言标记
        return prompt;
    }

    private async ValueTask<AuthStepResult> TryPublicKeyAsync(
        PublicKeyCredential credential, CancellationToken cancellationToken)
    {
        // 挑不出算法（只剩被禁用的 SHA-1 ssh-rsa）或外部签名器连算法列表都给不出 ——
        // 都是这把钥自己的问题，此时还什么都没发。
        string? chosen;
        try
        {
            chosen = ChooseSignatureAlgorithm(credential.Signer);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new CredentialMaterialException(ex);
        }
        if (chosen is not { } algorithm)
        {
            return new AuthStepResult(SshAuthOutcome.SkippedNoMaterial,
                [new SshAuthDetail(SshAuthDetailKind.NoUsableSignatureAlgorithm) { KeyType = credential.Signer.PublicKey.KeyType }]);
        }

        AuthStepResult step = await TryPublicKeyWithAsync(credential, algorithm, cancellationToken).ConfigureAwait(false);
        step = step with { SignatureAlgorithm = algorithm };

        // 〔velashell-docs/zh/ssh/spec/04 §4.4〕没收到 server-sig-algs 时先试的是 SHA-2；
        // 不认 rsa-sha2-* 的老服务器会拒，使用者允许 SHA-1 的话降级重试一次 ssh-rsa。
        // 曾经不重试：「允许 SHA-1」只在 server-sig-algs 列了 ssh-rsa 时起作用，
        // 而不发 server-sig-algs 的老服务器（正是要这个开关的那些）照样只收到 rsa-sha2-512。
        if (step.Outcome == SshAuthOutcome.Failure
            && Sha1FallbackFor(credential.Signer, algorithm) is { } sha1)
        {
            AuthStepResult retry = await TryPublicKeyWithAsync(credential, sha1, cancellationToken).ConfigureAwait(false);
            step = retry with
            {
                Details = WithNotes(retry.Details,
                    [new SshAuthDetail(SshAuthDetailKind.Sha1Fallback) { Algorithm = algorithm, FallbackAlgorithm = sha1 }]),
                SignatureAlgorithm = sha1,
            };
        }
        else if (HostKeys.SshPublicKey.StripCertificateSuffix(algorithm) == SshAlgorithmNames.SshRsa)
        {
            // 没有降级、直接挑中了 SHA-1（server-sig-algs 里能用的只有它，或这把钥只给得出它）：同样记一笔。
            step = step with
            {
                Details = WithNotes(step.Details, [new SshAuthDetail(SshAuthDetailKind.Sha1Signature) { Algorithm = algorithm }]),
            };
        }

        // 〔§4.5〕证书被拒时，把本地看得出的线索写进记录：按本机时钟过期了、登录用户不在 principals 里……
        // 客户端不因此拦着不发（那是服务端的判断，本机时钟也可能不准），只是让用户不必对着 Permission denied 猜。
        if (step.Outcome == SshAuthOutcome.Failure && DescribeCertificateProblems(credential.Signer) is { } hints)
        {
            step = step with { Details = WithNotes(step.Details, hints) };
        }

        return step;
    }

    private static List<SshAuthDetail> WithNotes(IReadOnlyList<SshAuthDetail>? details, List<SshAuthDetail> notes) =>
        [.. details ?? [], .. notes];

    /// <summary>证书被拒时本地看得出的线索；不是证书、或者看不出问题时为 <see langword="null"/>。</summary>
    /// <remarks>只写事实：有效期按本机时钟判断（时钟不准时这条线索也不准，所以话里带着「按本机时钟」）。</remarks>
    private List<SshAuthDetail>? DescribeCertificateProblems(ISshSigner signer)
    {
        if (!signer.PublicKey.IsCertificate)
        {
            return null;
        }

        OpenSshCertificate certificate;
        if (signer is SshCertificateSigner certificateSigner)
        {
            certificate = certificateSigner.Certificate;
        }
        else
        {
            // agent 里的证书身份：签名器只交得出 blob。
            try
            {
                certificate = OpenSshCertificate.Decode(signer.PublicKey.Blob);
            }
            catch (SshCertificateException)
            {
                return null;
            }
        }

        string keyId = certificate.KeyId;
        List<SshAuthDetail> hints = [];
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (!certificate.IsTimeValid(now))
        {
            hints.Add(certificate.ValidBeforeTime is { } before && now >= before
                ? new SshAuthDetail(SshAuthDetailKind.CertificateExpired) { KeyId = keyId, Time = before }
                : new SshAuthDetail(SshAuthDetailKind.CertificateNotYetValid) { KeyId = keyId, Time = certificate.ValidAfterTime });
        }
        if (certificate.CertificateType != SshCertificateType.User)
        {
            hints.Add(new SshAuthDetail(SshAuthDetailKind.HostCertificateForLogin) { KeyId = keyId });
        }
        if (certificate.ValidPrincipals.Count > 0 && !certificate.ValidPrincipals.Contains(_userName, StringComparer.Ordinal))
        {
            hints.Add(new SshAuthDetail(SshAuthDetailKind.PrincipalNotListed)
            {
                Principals = certificate.ValidPrincipals,
                UserName = _userName,
            });
        }

        return hints.Count == 0 ? null : hints;
    }

    /// <summary>该不该、能不能降级为 SHA-1 的 RSA 签名重试一次；能的话给出算法名。</summary>
    private string? Sha1FallbackFor(ISshSigner signer, string attempted)
    {
        if (!AllowSha1RsaSignatures
            || _serverSignatureAlgorithms.Length > 0
            || HostKeys.SshPublicKey.StripCertificateSuffix(attempted) is not (SshAlgorithmNames.RsaSha512 or SshAlgorithmNames.RsaSha256))
        {
            return null;
        }

        return signer.SignatureAlgorithms.FirstOrDefault(
            static a => HostKeys.SshPublicKey.StripCertificateSuffix(a) == SshAlgorithmNames.SshRsa);
    }

    private async ValueTask<AuthStepResult> TryPublicKeyWithAsync(
        PublicKeyCredential credential, string algorithm, CancellationToken cancellationToken)
    {
        // 〔决策 velashell-docs/zh/ssh/spec/04 §4.1〕本地私钥直接签，省一个 RTT；
        // 外部签名（agent / PKCS#11 / HSM）先问「你认这把钥吗」——
        // 为一把服务端根本不认的密钥去让用户按硬件键是不可接受的。
        if (!credential.Signer.IsLocalAndCheap
            && await ProbePublicKeyAsync(credential, algorithm, cancellationToken).ConfigureAwait(false) is { } settled)
        {
            return settled;   // 不接受这把钥；或者服务端对探测直接回了 SUCCESS
        }

        // 被签名的数据是：string session_id ‖ 整个请求载荷（从消息编号字节起）。
        //
        // ⚠️ **先把请求载荷拼出来，再在前面拼上 session_id，整体交给签名器** ——
        //    而不是分两处各拼一遍。分两处拼是这里出错的唯一原因
        //    （velashell-docs/zh/ssh/spec/04 §4.3）。
        ArrayBufferWriter<byte> request = new();
        SshDataWriter writer = new(request);
        WriteRequestHeader(ref writer, SshProtocolNames.AuthPublicKey);
        writer.WriteBoolean(true);                                   // has_signature
        writer.WriteUtf8String(algorithm);
        writer.WriteString(credential.Signer.PublicKey.Blob.Span);

        ArrayBufferWriter<byte> signedData = new();
        SshDataWriter signedWriter = new(signedData);
        signedWriter.WriteString(_sessionId);
        signedWriter.WriteRaw(request.WrittenSpan);

        // 〔velashell-docs/zh/ssh/spec/04 §4.1、spec/07 §7.4〕钥在 ssh-agent 里：签之前先告诉 agent 这是哪个会话，
        // 它才能执行 ssh-add -h 给钥加的目的地约束。agent 不支持声明时照常签，所以这一步本身不会让认证失败。
        if (SessionProof is { } proof && credential.Signer is ISessionAwareSigner sessionAware)
        {
            _ = await FromCredentialAsync(async () =>
                {
                    await sessionAware.PrepareForSessionAsync(proof, cancellationToken).ConfigureAwait(false);
                    return true;
                })
                .ConfigureAwait(false);
        }

        // 探测的应答（PK_OK）已经读完，这里没有请求在途 —— 签名失败可以放心地换下一条凭据。
        byte[] signature = await FromCredentialAsync(
                () => credential.Signer.SignAsync(signedData.WrittenMemory, algorithm, cancellationToken))
            .ConfigureAwait(false);

        // 签名追加在请求末尾。
        ArrayBufferWriter<byte> full = new();
        SshDataWriter fullWriter = new(full);
        fullWriter.WriteRaw(request.WrittenSpan);
        fullWriter.WriteString(signature);

        _transport.WritePacket(full.WrittenSpan);
        await _transport.FlushAsync(cancellationToken).ConfigureAwait(false);

        return await ReadAuthOutcomeAsync(null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>问服务端「你认这把公钥吗」（不签名）。</summary>
    /// <returns>认（<c>PK_OK</c>）、该去签了时为 <see langword="null"/>；否则是这一步的结论。</returns>
    private async ValueTask<AuthStepResult?> ProbePublicKeyAsync(
        PublicKeyCredential credential, string algorithm, CancellationToken cancellationToken)
    {
        ArrayBufferWriter<byte> request = new();
        SshDataWriter writer = new(request);
        WriteRequestHeader(ref writer, SshProtocolNames.AuthPublicKey);
        writer.WriteBoolean(false);                                   // has_signature = false
        writer.WriteUtf8String(algorithm);
        writer.WriteString(credential.Signer.PublicKey.Blob.Span);

        _transport.WritePacket(request.WrittenSpan);
        await _transport.FlushAsync(cancellationToken).ConfigureAwait(false);

        bool accepted = false;
        AuthStepResult outcome = await ReadAuthOutcomeAsync(
            onMethodSpecific: (number, payload) =>
            {
                if (number != PublicKeyOk)
                {
                    return null;
                }
                // SSH_MSG_USERAUTH_PK_OK：服务端认这把公钥，可以去签了。
                CheckPublicKeyOkEcho(payload, algorithm, credential.Signer.PublicKey);
                accepted = true;
                return new AuthStepResult(SshAuthOutcome.PartialSuccess);
            },
            cancellationToken).ConfigureAwait(false);

        if (accepted)
        {
            return null;
        }

        // 〔AU-E5〕服务端对探测直接回了 SUCCESS（不合规，个别实现会这样）：认证已经完成。
        // RFC 4252 §5.1 说成功之后的认证请求一律忽略 —— 曾经记成「不接受这把公钥」、接着发下一条请求，
        // 一直等到认证超时。
        return outcome.Outcome == SshAuthOutcome.Success
            ? outcome
            : new AuthStepResult(SshAuthOutcome.Failure, [new SshAuthDetail(SshAuthDetailKind.PublicKeyNotAccepted)]);
    }

    /// <summary><c>PK_OK</c> 回显的必须是我们问的那一把（RFC 4252 §7：算法名与公钥 blob 都取自请求）。</summary>
    /// <remarks>
    /// 公钥 blob 对不上是协议错误：签下去的是服务端没认过的那一把。算法名除了请求里的那个，
    /// 也认这把钥自己的类型名（<c>rsa-sha2-256</c> 的请求回显成 <c>ssh-rsa</c>）—— 说的是同一把钥，签名算法照我们选的。
    /// 曾经回显什么都不看。
    /// </remarks>
    private static void CheckPublicKeyOkEcho(ReadOnlyMemory<byte> payload, string algorithm, HostKeys.SshPublicKey key)
    {
        SshDataReader reader = new(new ReadOnlySequence<byte>(payload));
        reader.ReadByte();   // PK_OK
        string echoedAlgorithm = reader.ReadUtf8String(MaxFieldBytes);
        byte[] echoedBlob = reader.ReadStringAsArray(MaxFieldBytes);

        if (!echoedBlob.AsSpan().SequenceEqual(key.Blob.Span))
        {
            throw new SshProtocolException(
                SshPhase.Authenticating, "服务端的 PK_OK 回显的公钥不是我们问的那一把。");
        }

        if (echoedAlgorithm != algorithm && echoedAlgorithm != key.KeyType)
        {
            throw new SshProtocolException(
                SshPhase.Authenticating,
                $"服务端的 PK_OK 回显的算法是 {PeerText.Sanitize(echoedAlgorithm, 64)}，我们问的是 {algorithm}。");
        }
    }

    private async ValueTask<AuthStepResult> TryKeyboardInteractiveAsync(
        KeyboardInteractiveCredential credential, CancellationToken cancellationToken)
    {
        ArrayBufferWriter<byte> request = new();
        SshDataWriter writer = new(request);
        WriteRequestHeader(ref writer, SshProtocolNames.AuthKeyboardInteractive);
        writer.WriteUtf8String("");   // 语言标记：发空，让服务端自己挑
        writer.WriteUtf8String("");   // 子方法提示：同上

        _transport.WritePacket(request.WrittenSpan);
        await _transport.FlushAsync(cancellationToken).ConfigureAwait(false);

        for (int round = 0; ; round++)
        {
            if (round >= MaxKeyboardInteractiveRounds)
            {
                throw new SshProtocolException(
                    SshPhase.Authenticating,
                    $"keyboard-interactive 超过 {MaxKeyboardInteractiveRounds} 轮 —— " +
                    "服务端可以无限问下去，那是拿用户注意力做的拒绝服务。");
            }

            SshKeyboardChallenge? challenge = null;
            AuthStepResult outcome = await ReadAuthOutcomeAsync(
                onMethodSpecific: (number, payload) =>
                {
                    if (number != InfoRequest)
                    {
                        return null;
                    }
                    challenge = ParseInfoRequest(payload);
                    return new AuthStepResult(SshAuthOutcome.PartialSuccess);
                },
                cancellationToken).ConfigureAwait(false);

            if (challenge is null)
            {
                // 不是 INFO_REQUEST —— 认证已经有结论了。
                return outcome;
            }
            // 纯展示轮：不该弹窗要输入，但**仍然把 instruction 交给回调** ——
            // 否则用户对着一个没反应的界面干等，而服务端正等他去按硬件令牌。
            //
            // 回调抛异常时服务端正等着 INFO_RESPONSE，没有应答在途；换下一条凭据时发出的新
            // USERAUTH_REQUEST 会让服务端放弃这一轮（RFC 4252 §5）。
            IReadOnlyList<string> responses = await FromCredentialAsync(() => challenge.IsInformationalOnly
                    ? NotifyInformationalAsync(credential, challenge, cancellationToken)
                    : credential.RespondAsync(challenge, cancellationToken))
                .ConfigureAwait(false);

            if (responses.Count != challenge.Prompts.Count)
            {
                throw new SshProtocolException(
                    SshPhase.Authenticating,
                    $"keyboard-interactive 的应答条数（{responses.Count}）与提示条数" +
                    $"（{challenge.Prompts.Count}）不符 —— 协议要求完全相等。");
            }

            ArrayBufferWriter<byte> response = new();
            SshDataWriter responseWriter = new(response);
            responseWriter.WriteByte(InfoResponse);
            responseWriter.WriteUInt32((uint)responses.Count);
            foreach (string answer in responses)
            {
                responseWriter.WriteUtf8String(answer);
            }

            // 应答里是明文的口令 / 动态码：封进传输的缓冲之后就把这一份清零（velashell-docs/zh/ssh/spec/04 §5.2）。
            _transport.WritePacket(response.WrittenSpan);
            response.Clear();
            await _transport.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async ValueTask<IReadOnlyList<string>> NotifyInformationalAsync(
        KeyboardInteractiveCredential credential, SshKeyboardChallenge challenge, CancellationToken cancellationToken)
    {
        // 仍然调用回调,好让界面把 Instruction 显示出来;但协议要求应答条数与提示条数
        // 相等,而这一轮没有提示 —— 所以回调返回什么都丢掉,只回一个空应答。
        _ = await credential.RespondAsync(challenge, cancellationToken).ConfigureAwait(false);
        return [];
    }

    private static SshKeyboardChallenge ParseInfoRequest(ReadOnlyMemory<byte> payload)
    {
        SshDataReader reader = new(new ReadOnlySequence<byte>(payload));
        reader.ReadByte();   // InfoRequest

        string name = reader.ReadUtf8String(MaxKeyboardStringBytes);
        string instruction = reader.ReadUtf8String(MaxKeyboardStringBytes);
        _ = reader.ReadUtf8String(MaxKeyboardStringBytes);   // 语言标记，忽略

        uint count = reader.ReadUInt32();
        if (count > MaxKeyboardPrompts)
        {
            throw new SshProtocolException(
                SshPhase.Authenticating,
                $"keyboard-interactive 一轮给了 {count} 条提示，超过上限 {MaxKeyboardPrompts}。");
        }

        List<SshKeyboardPrompt> prompts = [];
        for (uint i = 0; i < count; i++)
        {
            string text = reader.ReadUtf8String(MaxKeyboardStringBytes);
            bool echo = reader.ReadBoolean();
            prompts.Add(new SshKeyboardPrompt(text, echo));
        }

        return new SshKeyboardChallenge
        {
            Name = name,
            Instruction = instruction,
            Prompts = prompts,
        };
    }

    // ------------------------------------------------------------ 报文处理

    /// <summary>一步认证的结果，连同「为什么」。</summary>
    /// <remarks>
    /// 说明必须跟着结果一路传到尝试记录里。只传结果的话，
    /// 「服务端要求先改密码」会退化成一句没有内容的「失败」——
    /// 而这正是用户最需要知道的那一句。
    /// </remarks>
    private readonly record struct AuthStepResult(
        SshAuthOutcome Outcome, IReadOnlyList<SshAuthDetail>? Details = null, string? SignatureAlgorithm = null);

    /// <summary>读到一个认证结论（SUCCESS / FAILURE / 方法专用报文）。</summary>
    private async ValueTask<AuthStepResult> ReadAuthOutcomeAsync(
        Func<byte, ReadOnlyMemory<byte>, AuthStepResult?>? onMethodSpecific,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            SshInboundPacket packet = await ReadAsync(cancellationToken).ConfigureAwait(false);
            byte number = (byte)packet.MessageNumber;

            switch (packet.MessageNumber)
            {
                case SshMessageNumber.UserAuthSuccess:
                    return new AuthStepResult(SshAuthOutcome.Success);

                case SshMessageNumber.UserAuthFailure:
                    return new AuthStepResult(HandleFailure(packet.Payload));

                default:
                    if (number is >= FirstMethodSpecificNumber and <= LastMethodSpecificNumber && onMethodSpecific is not null)
                    {
                        AuthStepResult? result = onMethodSpecific(number, packet.Payload);
                        if (result is { } value)
                        {
                            return value;
                        }
                    }

                    throw new SshProtocolException(
                        SshPhase.Authenticating,
                        $"认证期间收到意外的报文 {packet.MessageNumber}({number})。");
            }
        }
    }

    private SshAuthOutcome HandleFailure(ReadOnlyMemory<byte> payload)
    {
        (string[] methods, bool partial) = ParseFailure(payload);
        _serverOffered = methods;
        // **partial_success 为 true 意味着这一步成功了。**
        return partial ? SshAuthOutcome.PartialSuccess : SshAuthOutcome.Failure;
    }

    private static (string[] Methods, bool PartialSuccess) ParseFailure(ReadOnlyMemory<byte> payload)
    {
        SshDataReader reader = new(new ReadOnlySequence<byte>(payload));
        reader.ReadMessageNumber(SshMessageNumber.UserAuthFailure);
        string[] methods = reader.ReadNameList(MaxFieldBytes);
        bool partial = reader.ReadBoolean();
        return (methods, partial);
    }

    /// <summary>认证器已经读过的报文数（判断 <c>EXT_INFO</c> 是不是首次 <c>NEWKEYS</c> 之后的第一个报文）。</summary>
    private int _packetsRead;

    /// <summary>收到了不在第一个位置的 <c>EXT_INFO</c>：下一个报文必须是 <c>USERAUTH_SUCCESS</c>。</summary>
    private bool _extInfoAwaitingSuccess;

    /// <summary>读下一个报文，顺手处理横幅、扩展信息与断开。</summary>
    private async ValueTask<SshInboundPacket> ReadAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            SshInboundPacket packet;
            try
            {
                packet = await _transport.ReadPacketAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Crypto.SshFrameFormatException ex) when (ex.PeerClosedMidPacket)
            {
                // 报文中途断开是连接断了，不是协议错误（同 SshKeyExchangeRunner 与 ConnectionFaults.NormalizeFault）。
                throw new SshConnectionClosedException(
                    SshFailureReason.ClosedByPeer, SshPhase.Authenticating, "对端在认证期间关闭了连接（一个报文只收到一半）。", ex);
            }
            catch (Crypto.SshFrameFormatException ex)
            {
                throw new SshProtocolException(SshPhase.Authenticating, ex.Message, ex);
            }

            if (packet.IsEndOfStream)
            {
                throw new SshConnectionClosedException(
                    SshFailureReason.ClosedByPeer, SshPhase.Authenticating,
                    "对端在认证期间关闭了连接。");
            }

            bool firstPacket = _packetsRead++ == 0;
            if (_extInfoAwaitingSuccess && packet.MessageNumber != SshMessageNumber.UserAuthSuccess)
            {
                throw new SshProtocolException(
                    SshPhase.Authenticating,
                    $"服务端在认证中途发了 EXT_INFO，后面跟的却是 {packet.MessageNumber} —— " +
                    "EXT_INFO 只能是首次 NEWKEYS 之后的第一个报文，或者紧挨着 USERAUTH_SUCCESS（RFC 8308 §2.4）。");
            }
            _extInfoAwaitingSuccess = false;

            switch (packet.MessageNumber)
            {
                case SshMessageNumber.UserAuthBanner:
                    await HandleBannerAsync(packet.Payload, cancellationToken).ConfigureAwait(false);
                    continue;

                case SshMessageNumber.ExtInfo:
                    // 〔velashell-docs/zh/ssh/spec/04 §7.1〕只有两个位置合法：首次 NEWKEYS 之后的第一个报文；
                    // 紧挨着 USERAUTH_SUCCESS 之前（下一个报文才知道是不是，所以先记下、读下一个时核对）。
                    // 曾经任何位置都照收，后到的整体覆盖 server-sig-algs —— 认证中途就能改掉 RSA 签名算法的选择。
                    _extInfoAwaitingSuccess = !firstPacket;
                    HandleExtensionInfo(packet.Payload);
                    continue;

                case SshMessageNumber.Ignore:
                    continue;

                case SshMessageNumber.Debug:
                    await DeliverDebugMessageAsync(packet.Payload, cancellationToken).ConfigureAwait(false);
                    continue;

                case SshMessageNumber.Disconnect:
                    throw BuildDisconnectException(packet.Payload);

                case SshMessageNumber.KexInit when PeerKexInitHandler is { } rekey:
                    // 对端在途的那个应答会在交换完成之后、用新密钥到来 —— 接着读就是。
                    await rekey(packet.Payload, cancellationToken).ConfigureAwait(false);
                    continue;

                case SshMessageNumber.KexInit:
                    throw new SshProtocolException(
                        SshPhase.Authenticating, "服务端在认证期间发起了密钥重协商，而这条认证路径不支持就地重协商。");

                default:
                    return packet;
            }
        }
    }

    private async ValueTask HandleBannerAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        if (_banner.Count >= MaxBannerCount || _banner.Sum(static b => b.Length) > MaxBannerBytes)
        {
            throw new SshProtocolException(
                SshPhase.Authenticating, "服务端发出了过多横幅文本。");
        }

        SshDataReader reader = new(new ReadOnlySequence<byte>(payload));
        reader.ReadMessageNumber(SshMessageNumber.UserAuthBanner);
        string text = reader.ReadUtf8String(64 * 1024);
        _banner.Add(text);

        if (BannerHandler is { } handler)
        {
            // 〔velashell-docs/zh/ssh/spec/04 §3.4〕回调自己抛的照实交还：不当成跳过，也不归成「对端断开」。
            await SshCallbackFaultException.InvokeAsync(() => handler(text, cancellationToken)).ConfigureAwait(false);
        }
    }

    /// <summary>标着 <c>always_display</c> 的调试消息清洗之后交给回调；格式不对的照 RFC 忽略。</summary>
    private async ValueTask DeliverDebugMessageAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        if (DebugMessageHandler is not { } handler)
        {
            return;
        }

        SshDebugMessage message;
        try
        {
            message = SshDebugMessage.Decode(payload);
        }
        catch (SshWireFormatException)
        {
            return;
        }

        if (message.AlwaysDisplay && PeerText.Sanitize(message.Message) is { Length: > 0 } text)
        {
            await SshCallbackFaultException.InvokeAsync(() => handler(text, cancellationToken)).ConfigureAwait(false);
        }
    }

    private void HandleExtensionInfo(ReadOnlyMemory<byte> payload)
    {
        SshDataReader reader = new(new ReadOnlySequence<byte>(payload));
        reader.ReadMessageNumber(SshMessageNumber.ExtInfo);
        uint count = reader.ReadUInt32();

        // 上限防一个畸形报文让我们空转。
        for (uint i = 0; i < count && i < 256; i++)
        {
            string name = reader.ReadUtf8String(1024);
            ReadOnlySequence<byte> value = reader.ReadString(MaxFieldBytes);
            if (!_serverExtensions.Contains(name))
            {
                _serverExtensions.Add(name);
            }

            if (name == SshProtocolNames.ExtServerSigAlgs)
            {
                // 没有它就无法安全地选 RSA 签名算法（velashell-docs/zh/ssh/spec/04 §4.4）。
                string text = System.Text.Encoding.ASCII.GetString(value.ToArray());
                _serverSignatureAlgorithms = text.Split(',', StringSplitOptions.RemoveEmptyEntries);
            }
            // 未知扩展一律忽略，不报错 —— 生态就是这么演进的。
        }
    }

    private static SshConnectionClosedException BuildDisconnectException(ReadOnlyMemory<byte> payload)
    {
        SshDisconnectReason? reason = null;
        string? description = null;

        try
        {
            SshDataReader reader = new(new ReadOnlySequence<byte>(payload));
            reader.ReadMessageNumber(SshMessageNumber.Disconnect);
            reason = (SshDisconnectReason)reader.ReadUInt32();
            description = reader.ReadUtf8String(64 * 1024);
        }
        catch (SshWireFormatException)
        {
            // 格式错也不影响结论：连接要断了。
        }

        return new SshConnectionClosedException(
            SshFailureReason.Disconnected, SshPhase.Authenticating,
            $"服务端在认证期间断开：{reason?.ToString() ?? "未知原因"}" +
            (string.IsNullOrEmpty(description) ? "" : $" —— {PeerText.Sanitize(description)}"))
        {
            DisconnectReason = reason,
            PeerDescription = description,
        };
    }

    // ------------------------------------------------------------ 工具

    private void WriteRequestHeader(ref SshDataWriter writer, string method)
    {
        writer.WriteMessageNumber(SshMessageNumber.UserAuthRequest);
        writer.WriteUtf8String(_userName);
        writer.WriteUtf8String(SshProtocolNames.ServiceConnection);
        writer.WriteUtf8String(method);
    }

    /// <summary>为一把密钥挑签名算法。</summary>
    /// <remarks>
    /// 收到 <c>server-sig-algs</c> 时取交集中我们最偏好的；
    /// 没收到时用我们自己的第一偏好（velashell-docs/zh/ssh/spec/04 §4.4）。
    /// 一个能用的都没有（只剩被禁用的 SHA-1 <c>ssh-rsa</c>）时为 <see langword="null"/>：这条凭据按「材料有问题」跳过。
    /// </remarks>
    private string? ChooseSignatureAlgorithm(ISshSigner signer)
    {
        IEnumerable<string> candidates = signer.SignatureAlgorithms;

        if (!AllowSha1RsaSignatures)
        {
            // 按去掉证书后缀的名字比：RSA 证书的 SHA-1 算法叫 ssh-rsa-cert-v01@openssh.com，
            // 只比 ssh-rsa 的话，拿证书登录时 SHA-1 照样被挑出来用。
            candidates = candidates.Where(
                static a => HostKeys.SshPublicKey.StripCertificateSuffix(a) != SshAlgorithmNames.SshRsa);
        }

        string[] usable = [.. candidates];
        if (usable.Length == 0)
        {
            return null;
        }

        if (_serverSignatureAlgorithms.Length == 0)
        {
            return usable[0];
        }

        // 〔AU-E6〕证书按去掉后缀的名字比：server-sig-algs 列的是签名算法（RFC 8308 §3.1），
        // 证书的签名算法就是不带后缀的那个（rsa-sha2-256-cert-v01@openssh.com 签出来的是 rsa-sha2-256）。
        // 曾经只按原样比，证书永远比不中、永远取第一偏好：只认 rsa-sha2-256 的服务端照样收到 512，
        // 允许 SHA-1 时只认 ssh-rsa 的服务端也收不到 SHA-1。带后缀列出来的照样认。
        foreach (string candidate in usable)
        {
            if (_serverSignatureAlgorithms.Contains(candidate, StringComparer.Ordinal)
                || _serverSignatureAlgorithms.Contains(HostKeys.SshPublicKey.StripCertificateSuffix(candidate), StringComparer.Ordinal))
            {
                return candidate;
            }
        }

        // 服务端宣告的算法里没有我们能用的。仍然试第一个 —— 宣告不完整的服务端确实存在，
        // 而失败的代价只是一次多余的往返。
        return usable[0];
    }

    private void Record(
        SshCredential credential, SshAuthOutcome outcome, IReadOnlyList<SshAuthDetail>? details, string? signatureAlgorithm = null) =>
        _attempts.Add(new SshAuthAttempt(
            credential.MethodName, credential.Label, outcome, _serverOffered, SshAuthDetail.Render(details ?? []), signatureAlgorithm)
        {
            Details = details is null ? [] : [.. details],
            BridgedCredentialLabel = (credential as KeyboardInteractiveCredential)?.BridgedFrom?.Label,
        });

    private SshAuthenticationResult BuildResult(string method) =>
        new(method, _attempts, _banner, _serverSignatureAlgorithms, _serverExtensions);

    private SshAuthenticationException BuildExhaustedException()
    {
        // 服务端只接受 keyboard-interactive 而我们没配 —— 这是一个**可判定的状态**，
        // 值得单独一个原因码，好让界面说「这台机器需要动态码」而不是
        // 「用户名或密码不正确」（velashell-docs/zh/ssh/spec/04 §9）。
        bool needsKeyboardInteractive =
            _serverOffered.Contains(SshProtocolNames.AuthKeyboardInteractive, StringComparer.Ordinal)
            && !_attempts.Any(static a =>
                a.Method == SshProtocolNames.AuthKeyboardInteractive
                && a.Outcome is SshAuthOutcome.Failure or SshAuthOutcome.PartialSuccess);

        // 「要先改密码」同样是可判定的：用户该去的是改密码的地方，不是反复重输同一个密码。
        (SshFailureReason reason, string message) = (_passwordChangeRequested, needsKeyboardInteractive) switch
        {
            (true, _) => (
                SshFailureReason.PasswordExpired,
                "服务端要求先修改密码（密码已过期或被管理员要求更换），密码没有改成。"),
            (_, true) => (
                SshFailureReason.TwoFactorRequired,
                "服务端要求键盘交互式认证（动态码 / OTP），但没有配置相应的凭据。" +
                $"服务端接受：{string.Join(", ", _serverOffered)}。"),
            _ => (
                SshFailureReason.AuthenticationMethodExhausted,
                $"所有认证方法都已尝试且未成功。服务端接受：{string.Join(", ", _serverOffered)}。"),
        };

        if (_partialSuccessAchieved)
        {
            message += " 其中至少有一步已经通过，卡在后续步骤。";
        }

        return new SshAuthenticationException(
            reason, message, _attempts, _serverOffered, _partialSuccessAchieved);
    }
}

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
using VelaShell.Ssh.Diagnostics;
using VelaShell.Ssh.Protocol;
using VelaShell.Ssh.Transport;

namespace VelaShell.Ssh.Auth;

/// <summary>一次认证的结果。</summary>
/// <param name="Method">最终成功的那个方法。</param>
/// <param name="Attempts">逐条尝试记录。</param>
/// <param name="Banner">服务端发来的横幅文本（按出现顺序）。</param>
/// <param name="ServerSignatureAlgorithms">服务端通过 <c>server-sig-algs</c> 宣告的签名算法。</param>
public sealed record SshAuthenticationResult(
    string Method,
    IReadOnlyList<SshAuthAttempt> Attempts,
    IReadOnlyList<string> Banner,
    IReadOnlyList<string> ServerSignatureAlgorithms);

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
public sealed class SshAuthenticator(SshPacketTransport transport, string userName, byte[] sessionId)
{
    private const int MaxFieldBytes = 64 * 1024;
    private const int MaxBannerBytes = 256 * 1024;
    private const int MaxBannerCount = 1024;

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
    private bool _partialSuccessAchieved;

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
            return BuildResult(SshAlgorithmNames.AuthNone);
        }

        // ② 按**使用者给出的顺序**逐个试。
        //
        //    ⚠️ **部分成功之后从头再扫一遍。**多因素（AuthenticationMethods publickey,password）时，
        //    服务端一开始只报 publickey，排在前面的口令凭据于是被「不接受」跳过；公钥那一步成功之后
        //    服务端才开放 password —— 可循环已经走过去了，认证以「凭据试完了」失败。
        //    重扫只重试**当时因方法不被接受而跳过**的凭据：试过的（成功一步、失败、材料有问题）不再试 ——
        //    同一把钥换个时机也不会变成对的，白白耗掉服务端的尝试次数。
        HashSet<SshCredential> tried = new(ReferenceEqualityComparer.Instance);
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
                            $"服务端只接受：{string.Join(", ", _serverOffered)}");
                        continue;
                    }
                    effective = bridged;
                }

                // 失败上限只管 password 与 keyboard-interactive：那是对**同一个秘密**的重试，
                // 连错几次会被服务端封一阵。publickey 的每一把钥是不同的凭据 —— agent 里有五把钥、
                // 对的是第四把时，第三把之后就不试了，那就永远登不上；它们的总数由服务端的
                // MaxAuthTries 管。
                if (effective.MethodName != SshAlgorithmNames.AuthPublicKey
                    && _failureCounts.GetValueOrDefault(effective.MethodName) >= MaxFailuresPerMethod)
                {
                    // 计数是整次认证累计的，不随部分成功清零 —— 所以不说「连续」。
                    Record(effective, SshAuthOutcome.SkippedNotOffered,
                        $"{effective.MethodName} 在这次认证里已经失败 {MaxFailuresPerMethod} 次，不再重试。");
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
        if (credential is not PasswordCredential { AlsoAnswerKeyboardInteractive: true } password
            || !_serverOffered.Contains(SshAlgorithmNames.AuthKeyboardInteractive, StringComparer.Ordinal))
        {
            return null;
        }

        return new KeyboardInteractiveCredential(
            async (challenge, cancellationToken) =>
            {
                // 只在「恰好一条不回显提示」时填密码 —— 那几乎一定是「Password:」。
                //
                // 其它形状（多条提示、要回显的提示）意味着这是真正的多因素询问。
                // 此时**必须仍然回够条数**：中途放弃会把服务端晾在等应答的状态上，
                // 让整条会话卡住。回空串让服务端干脆地拒绝，我们再换下一条凭据。
                return challenge.Prompts is not [{ Echo: false }]
                    ? [.. challenge.Prompts.Select(static _ => string.Empty)]
                    : (IReadOnlyList<string>)(string[])[await password.GetPasswordAsync(cancellationToken).ConfigureAwait(false)];
            },
            label: $"{password.Label}（经 keyboard-interactive）");
    }

    // ------------------------------------------------------------ 服务请求

    private async ValueTask RequestUserAuthServiceAsync(CancellationToken cancellationToken)
    {
        ArrayBufferWriter<byte> request = new();
        SshDataWriter writer = new(request);
        writer.WriteMessageNumber(SshMessageNumber.ServiceRequest);
        writer.WriteUtf8String(SshAlgorithmNames.ServiceUserAuth);

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
        WriteRequestHeader(ref writer, SshAlgorithmNames.AuthNone);

        _transport.WritePacket(request.WrittenSpan);
        await _transport.FlushAsync(cancellationToken).ConfigureAwait(false);

        AuthStepResult outcome = await ReadAuthOutcomeAsync(null, cancellationToken).ConfigureAwait(false);
        Record(none, outcome.Outcome, outcome.Outcome == SshAuthOutcome.Failure
            ? $"服务端接受：{string.Join(", ", _serverOffered)}"
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
                _ => throw new CredentialMaterialException(
                    new NotSupportedException($"尚未实现的认证方法：{credential.MethodName}")),
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
            step = new AuthStepResult(SshAuthOutcome.SkippedNoMaterial, ex.Message);
        }

        Record(credential, step.Outcome, step.Detail);
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

        ArrayBufferWriter<byte> request = new();
        SshDataWriter writer = new(request);
        WriteRequestHeader(ref writer, SshAlgorithmNames.AuthPassword);
        writer.WriteBoolean(false);        // 不是改密码请求
        writer.WriteUtf8String(password);

        _transport.WritePacket(request.WrittenSpan);
        await _transport.FlushAsync(cancellationToken).ConfigureAwait(false);

        // SSH_MSG_USERAUTH_PASSWD_CHANGEREQ：服务端要求先改密码。
        // 〔决策 velashell-docs/zh/ssh/spec/04 §5.1〕我们不实现改密码流程，但**要把原因说清楚** ——
        // 「客户端直接断开且不说为什么」是用户最难自救的一种失败。
        return await ReadAuthOutcomeAsync(
            onMethodSpecific: (number, payload) => number == 60
                ? new AuthStepResult(SshAuthOutcome.Failure, "服务端要求先修改密码（本库尚未实现改密码流程）。")
                : null,
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<AuthStepResult> TryPublicKeyAsync(
        PublicKeyCredential credential, CancellationToken cancellationToken)
    {
        // 挑不出算法（只剩被禁用的 SHA-1 ssh-rsa）或外部签名器连算法列表都给不出 ——
        // 都是这把钥自己的问题，此时还什么都没发。
        string algorithm;
        try
        {
            algorithm = ChooseSignatureAlgorithm(credential.Signer);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new CredentialMaterialException(ex);
        }

        // 〔决策 velashell-docs/zh/ssh/spec/04 §4.1〕本地私钥直接签，省一个 RTT；
        // 外部签名（agent / PKCS#11 / HSM）先问「你认这把钥吗」——
        // 为一把服务端根本不认的密钥去让用户按硬件键是不可接受的。
        if (!credential.Signer.IsLocalAndCheap)
        {
            bool accepted = await ProbePublicKeyAsync(credential, algorithm, cancellationToken).ConfigureAwait(false);
            if (!accepted)
            {
                return new AuthStepResult(SshAuthOutcome.Failure, "服务端不接受这把公钥。");
            }
        }

        // 被签名的数据是：string session_id ‖ 整个请求载荷（从消息编号字节起）。
        //
        // ⚠️ **先把请求载荷拼出来，再在前面拼上 session_id，整体交给签名器** ——
        //    而不是分两处各拼一遍。分两处拼是这里出错的唯一原因
        //    （velashell-docs/zh/ssh/spec/04 §4.3）。
        ArrayBufferWriter<byte> request = new();
        SshDataWriter writer = new(request);
        WriteRequestHeader(ref writer, SshAlgorithmNames.AuthPublicKey);
        writer.WriteBoolean(true);                                   // has_signature
        writer.WriteUtf8String(algorithm);
        writer.WriteString(credential.Signer.PublicKey.Blob.Span);

        ArrayBufferWriter<byte> signedData = new();
        SshDataWriter signedWriter = new(signedData);
        signedWriter.WriteString(_sessionId);
        signedWriter.WriteRaw(request.WrittenSpan);

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

    private async ValueTask<bool> ProbePublicKeyAsync(
        PublicKeyCredential credential, string algorithm, CancellationToken cancellationToken)
    {
        ArrayBufferWriter<byte> request = new();
        SshDataWriter writer = new(request);
        WriteRequestHeader(ref writer, SshAlgorithmNames.AuthPublicKey);
        writer.WriteBoolean(false);                                   // has_signature = false
        writer.WriteUtf8String(algorithm);
        writer.WriteString(credential.Signer.PublicKey.Blob.Span);

        _transport.WritePacket(request.WrittenSpan);
        await _transport.FlushAsync(cancellationToken).ConfigureAwait(false);

        bool accepted = false;
        AuthStepResult outcome = await ReadAuthOutcomeAsync(
            onMethodSpecific: (number, _) =>
            {
                if (number != 60)
                {
                    return null;
                }
                // SSH_MSG_USERAUTH_PK_OK：服务端认这把公钥，可以去签了。
                accepted = true;
                return new AuthStepResult(SshAuthOutcome.PartialSuccess);
            },
            cancellationToken).ConfigureAwait(false);

        return accepted && outcome.Outcome == SshAuthOutcome.PartialSuccess;
    }

    private async ValueTask<AuthStepResult> TryKeyboardInteractiveAsync(
        KeyboardInteractiveCredential credential, CancellationToken cancellationToken)
    {
        ArrayBufferWriter<byte> request = new();
        SshDataWriter writer = new(request);
        WriteRequestHeader(ref writer, SshAlgorithmNames.AuthKeyboardInteractive);
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
                    if (number != 60)
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
            responseWriter.WriteByte(61);   // SSH_MSG_USERAUTH_INFO_RESPONSE
            responseWriter.WriteUInt32((uint)responses.Count);
            foreach (string answer in responses)
            {
                responseWriter.WriteUtf8String(answer);
            }

            _transport.WritePacket(response.WrittenSpan);
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
        reader.ReadByte();   // 60

        string name = reader.ReadUtf8String(MaxFieldBytes);
        string instruction = reader.ReadUtf8String(MaxFieldBytes);
        _ = reader.ReadUtf8String(MaxFieldBytes);   // 语言标记，忽略

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
            string text = reader.ReadUtf8String(4 * 1024);
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
    private readonly record struct AuthStepResult(SshAuthOutcome Outcome, string? Detail = null);

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
                    if (number is >= 60 and <= 79 && onMethodSpecific is not null)
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
                // 报文中途断开是连接断了，不是协议错误（同 SshKeyExchangeRunner 与 SshConnection.NormalizeFault）。
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

            switch (packet.MessageNumber)
            {
                case SshMessageNumber.UserAuthBanner:
                    await HandleBannerAsync(packet.Payload, cancellationToken).ConfigureAwait(false);
                    continue;

                case SshMessageNumber.ExtInfo:
                    HandleExtensionInfo(packet.Payload);
                    continue;

                case SshMessageNumber.Ignore:
                case SshMessageNumber.Debug:
                    continue;

                case SshMessageNumber.Disconnect:
                    throw BuildDisconnectException(packet.Payload);

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

        if (BannerHandler is not null)
        {
            await BannerHandler(text, cancellationToken).ConfigureAwait(false);
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

            if (name == SshAlgorithmNames.ExtServerSigAlgs)
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
        writer.WriteUtf8String(SshAlgorithmNames.ServiceConnection);
        writer.WriteUtf8String(method);
    }

    /// <summary>为一把密钥挑签名算法。</summary>
    /// <remarks>
    /// 收到 <c>server-sig-algs</c> 时取交集中我们最偏好的；
    /// 没收到时用我们自己的第一偏好（velashell-docs/zh/ssh/spec/04 §4.4）。
    /// </remarks>
    private string ChooseSignatureAlgorithm(ISshSigner signer)
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
            throw new InvalidOperationException(
                $"这把 {signer.PublicKey.KeyType} 密钥没有可用的签名算法" +
                "（若需要 SHA-1 的 ssh-rsa，请显式开启 AllowSha1RsaSignatures）。");
        }

        if (_serverSignatureAlgorithms.Length == 0)
        {
            return usable[0];
        }

        foreach (string candidate in usable)
        {
            if (_serverSignatureAlgorithms.Contains(candidate, StringComparer.Ordinal))
            {
                return candidate;
            }
        }

        // 服务端宣告的算法里没有我们能用的。仍然试第一个 —— 宣告不完整的服务端确实存在，
        // 而失败的代价只是一次多余的往返。
        return usable[0];
    }

    private void Record(SshCredential credential, SshAuthOutcome outcome, string? detail) =>
        _attempts.Add(new SshAuthAttempt(
            credential.MethodName, credential.Label, outcome, _serverOffered, detail));

    private SshAuthenticationResult BuildResult(string method) =>
        new(method, _attempts, _banner, _serverSignatureAlgorithms);

    private SshAuthenticationException BuildExhaustedException()
    {
        // 服务端只接受 keyboard-interactive 而我们没配 —— 这是一个**可判定的状态**，
        // 值得单独一个原因码，好让界面说「这台机器需要动态码」而不是
        // 「用户名或密码不正确」（velashell-docs/zh/ssh/spec/04 §9）。
        bool needsKeyboardInteractive =
            _serverOffered.Contains(SshAlgorithmNames.AuthKeyboardInteractive, StringComparer.Ordinal)
            && !_attempts.Any(static a =>
                a.Method == SshAlgorithmNames.AuthKeyboardInteractive
                && a.Outcome is SshAuthOutcome.Failure or SshAuthOutcome.PartialSuccess);

        SshFailureReason reason = needsKeyboardInteractive
            ? SshFailureReason.TwoFactorRequired
            : SshFailureReason.AuthenticationMethodExhausted;

        string message = needsKeyboardInteractive
            ? $"服务端要求键盘交互式认证（动态码 / OTP），但没有配置相应的凭据。" +
              $"服务端接受：{string.Join(", ", _serverOffered)}。"
            : $"所有认证方法都已尝试且未成功。服务端接受：{string.Join(", ", _serverOffered)}。";

        if (_partialSuccessAchieved)
        {
            message += " 其中至少有一步已经通过，卡在后续步骤。";
        }

        return new SshAuthenticationException(
            reason, message, _attempts, _serverOffered, _partialSuccessAchieved);
    }
}

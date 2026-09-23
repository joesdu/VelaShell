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
public sealed class SshAuthenticator
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

    private readonly SshPacketTransport _transport;
    private readonly string _userName;
    private readonly byte[] _sessionId;

    private readonly List<SshAuthAttempt> _attempts = [];
    private readonly List<string> _banner = [];
    // string 的默认相等比较器就是序数比较,不必再传 StringComparer.Ordinal。
    private readonly Dictionary<string, int> _failureCounts = [];

    private string[] _serverOffered = [];
    private string[] _serverSignatureAlgorithms = [];
    private bool _partialSuccessAchieved;

    /// <summary>创建一个认证执行器。</summary>
    /// <param name="transport">已完成密钥交换的传输。</param>
    /// <param name="userName">用户名。</param>
    /// <param name="sessionId">会话标识（公钥签名的第一个输入）。</param>
    public SshAuthenticator(SshPacketTransport transport, string userName, byte[] sessionId)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _userName = userName ?? throw new ArgumentNullException(nameof(userName));
        _sessionId = sessionId ?? throw new ArgumentNullException(nameof(sessionId));
    }

    /// <summary>
    /// 同一个方法连续失败多少次之后不再重试。
    /// </summary>
    /// <remarks>服务端通常也有自己的计数，撞满会被临时封禁。</remarks>
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

        await RequestUserAuthServiceAsync(cancellationToken).ConfigureAwait(false);

        // ① 先发 none 探一次。它几乎总会失败，但 FAILURE 里带回服务端接受的方法列表 ——
        //    那是唯一能问到这份清单的途径。它也**可能成功**（服务端配了无认证）。
        if (await TryNoneAsync(cancellationToken).ConfigureAwait(false))
        {
            return BuildResult(SshAlgorithmNames.AuthNone);
        }

        // ② 按**使用者给出的顺序**逐个试。
        foreach (SshCredential credential in credentials)
        {
            if (credential is NoneCredential)
            {
                continue;   // 已经试过了
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

            if (_failureCounts.GetValueOrDefault(effective.MethodName) >= MaxFailuresPerMethod)
            {
                Record(effective, SshAuthOutcome.SkippedNotOffered,
                    $"{effective.MethodName} 已连续失败 {MaxFailuresPerMethod} 次，不再重试。");
                continue;
            }

            AuthStepResult step = await TryCredentialAsync(effective, cancellationToken).ConfigureAwait(false);

            switch (step.Outcome)
            {
                case SshAuthOutcome.Success:
                    return BuildResult(effective.MethodName);

                case SshAuthOutcome.PartialSuccess:
                    // **这一步成功了。**不计失败、不标记凭据失效，继续外层循环用新列表挑下一个。
                    _partialSuccessAchieved = true;
                    continue;

                case SshAuthOutcome.Failure:
                    _failureCounts[effective.MethodName] =
                        _failureCounts.GetValueOrDefault(effective.MethodName) + 1;
                    continue;

                default:
                    continue;
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
                _ => throw new NotSupportedException($"尚未实现的认证方法：{credential.MethodName}"),
            };
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or SshException))
        {
            // 凭据自己出问题（私钥读不出来、外部签名器不可用）不该打断整条链 ——
            // 后面还有别的凭据可以试。但**必须如实记下来**：
            // 「私钥文件读不出来」与「服务端不认这把钥」是两件事。
            step = new AuthStepResult(SshAuthOutcome.SkippedNoMaterial, ex.Message);
        }

        Record(credential, step.Outcome, step.Detail);
        return step;
    }

    private async ValueTask<AuthStepResult> TryPasswordAsync(
        PasswordCredential credential, CancellationToken cancellationToken)
    {
        string password = await credential.GetPasswordAsync(cancellationToken).ConfigureAwait(false);

        ArrayBufferWriter<byte> request = new();
        SshDataWriter writer = new(request);
        WriteRequestHeader(ref writer, SshAlgorithmNames.AuthPassword);
        writer.WriteBoolean(false);        // 不是改密码请求
        writer.WriteUtf8String(password);

        _transport.WritePacket(request.WrittenSpan);
        await _transport.FlushAsync(cancellationToken).ConfigureAwait(false);

        return await ReadAuthOutcomeAsync(
            onMethodSpecific: (number, payload) => number == 60
                // SSH_MSG_USERAUTH_PASSWD_CHANGEREQ：服务端要求先改密码。
                // 〔决策 velashell-docs/zh/ssh/spec/04 §5.1〕我们不实现改密码流程，但**要把原因说清楚** ——
                // 「客户端直接断开且不说为什么」是用户最难自救的一种失败。
                ? new AuthStepResult(SshAuthOutcome.Failure, "服务端要求先修改密码（本库尚未实现改密码流程）。")
                : null,
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<AuthStepResult> TryPublicKeyAsync(
        PublicKeyCredential credential, CancellationToken cancellationToken)
    {
        string algorithm = ChooseSignatureAlgorithm(credential.Signer);

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

        byte[] signature = await credential.Signer
            .SignAsync(signedData.WrittenMemory, algorithm, cancellationToken).ConfigureAwait(false);

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

            IReadOnlyList<string> responses = challenge.IsInformationalOnly
                // 纯展示轮：不该弹窗要输入，但**仍然把 instruction 交给回调** ——
                // 否则用户对着一个没反应的界面干等，而服务端正等他去按硬件令牌。
                ? await NotifyInformationalAsync(credential, challenge, cancellationToken).ConfigureAwait(false)
                : await credential.RespondAsync(challenge, cancellationToken).ConfigureAwait(false);

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
            (string.IsNullOrEmpty(description) ? "" : $" —— {description}"))
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
            candidates = candidates.Where(static a => a != SshAlgorithmNames.SshRsa);
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

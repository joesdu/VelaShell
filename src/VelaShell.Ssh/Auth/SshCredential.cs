// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   RFC 4252 §5.2  none
//   RFC 4252 §7    publickey
//   RFC 4252 §8    password
//   RFC 4256       keyboard-interactive
//   行为规格:      velashell-docs/zh/ssh/spec/04-authentication.md §2、§4、§5、§6

using VelaShell.Ssh.Protocol;

namespace VelaShell.Ssh.Auth;

/// <summary>一条认证凭据。</summary>
/// <remarks>
/// <para>
/// 〔决策，velashell-docs/zh/ssh/spec/04 §2.2〕<b>使用者配置的凭据列表就是全部，不做任何隐式回退。</b>
/// 不自动读 <c>~/.ssh/id_*</c>，不自动连 ssh-agent —— 除非使用者显式加了对应凭据。
/// </para>
/// <para>
/// 理由很具体：隐式回退在桌面客户端里是实打实的问题。用户在界面上选了「密码」，
/// 库却先拿某把默认私钥去试，于是服务器日志里出现莫名其妙的失败记录；
/// Windows 上 <c>SSH_AUTH_SOCK</c> 常指向 msys/WSL 的 Unix 套接字，
/// 自动连 agent 每次都撞一发异常。
/// </para>
/// </remarks>
public abstract class SshCredential
{
    /// <summary>这条凭据用哪种认证方法。</summary>
    public abstract string MethodName { get; }

    /// <summary>
    /// 给使用者看的标签，进认证尝试记录。
    /// </summary>
    /// <remarks><b>禁止包含任何密钥材料</b> —— 它会进异常、日志与诊断面板。</remarks>
    public virtual string Label => MethodName;
}

/// <summary>空认证：用来问出「这台机器接受哪些方法」。</summary>
/// <remarks>
/// 它几乎总会失败，但 <c>USERAUTH_FAILURE</c> 里带回服务端愿意接受的方法列表 ——
/// 那是唯一能问到这份清单的途径（velashell-docs/zh/ssh/spec/04 §2.1）。
/// <para>
/// 〔注意〕它也**可能成功**（服务端配了无认证）。不能假设它一定失败。
/// </para>
/// </remarks>
public sealed class NoneCredential : SshCredential
{
    /// <inheritdoc />
    public override string MethodName => SshAlgorithmNames.AuthNone;

    /// <inheritdoc />
    public override string Label => "none（探测可用方法）";
}

/// <summary>密码认证。</summary>
public sealed class PasswordCredential : SshCredential
{
    private readonly Func<CancellationToken, ValueTask<string>> _provider;

    /// <summary>用一个固定密码构造。</summary>
    public PasswordCredential(string password)
    {
        ArgumentNullException.ThrowIfNull(password);
        _provider = _ => ValueTask.FromResult(password);
    }

    /// <summary>
    /// 用一个**按需取值**的回调构造。
    /// </summary>
    /// <remarks>
    /// 推荐这个重载：密码可以留在保险库 / 系统密钥链里，只在真正要用时才解出来，
    /// 而不是从配置加载那一刻起就躺在托管堆上。
    /// </remarks>
    public PasswordCredential(Func<CancellationToken, ValueTask<string>> passwordProvider)
    {
        _provider = passwordProvider ?? throw new ArgumentNullException(nameof(passwordProvider));
    }

    /// <inheritdoc />
    public override string MethodName => SshAlgorithmNames.AuthPassword;

    /// <inheritdoc />
    public override string Label => "password";

    /// <summary>
    /// <c>keyboard-interactive</c> 只有一条不回显提示时，是否用这个密码自动作答。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 默认 <see langword="true"/>：很多服务器同时开放 <c>password</c> 与
    /// <c>keyboard-interactive</c>，而后者的唯一提示就是「Password:」。
    /// 这是 OpenSSH 客户端的实际行为，也是用户期望的。
    /// </para>
    /// <para>
    /// <b>真正的 2FA 场景要关掉它</b>：那里第一条提示可能就是动态码，
    /// 自动填密码只会白白消耗一次尝试（velashell-docs/zh/ssh/spec/04 §6.5）。
    /// </para>
    /// </remarks>
    public bool AlsoAnswerKeyboardInteractive { get; init; } = true;

    /// <summary>取出密码。</summary>
    public ValueTask<string> GetPasswordAsync(CancellationToken cancellationToken = default) =>
        _provider(cancellationToken);
}

/// <summary>键盘交互认证的一条提示。</summary>
/// <param name="Text">提示文本（<b>不可信</b>，展示时按不可信内容处理）。</param>
/// <param name="Echo">输入是否应当回显。<see langword="false"/> 时**必须**以密码方式采集。</param>
public readonly record struct SshKeyboardPrompt(string Text, bool Echo);

/// <summary>服务端发来的一轮键盘交互询问。</summary>
public sealed class SshKeyboardChallenge
{
    /// <summary>对话框标题（可空）。</summary>
    public required string Name { get; init; }

    /// <summary>说明文字（可空）。例如「请按下硬件令牌上的按钮」。</summary>
    public required string Instruction { get; init; }

    /// <summary>提示列表。可能为空 —— 那表示这一轮只是展示信息。</summary>
    public required IReadOnlyList<SshKeyboardPrompt> Prompts { get; init; }

    /// <summary>
    /// 这一轮是不是**纯展示**（没有提示要回答）。
    /// </summary>
    /// <remarks>
    /// 〔决策，velashell-docs/zh/ssh/spec/04 §6.4〕此时**不该**弹窗要用户输入，
    /// 但仍然要把 <see cref="Instruction"/> 交给回调 ——
    /// 否则用户会对着一个没反应的界面干等，而服务端正等他去按硬件令牌。
    /// </remarks>
    public bool IsInformationalOnly => Prompts.Count == 0;
}

/// <summary>
/// 键盘交互认证（<c>keyboard-interactive</c>）—— <b>2FA / OTP 走这条</b>。
/// </summary>
/// <remarks>
/// 堡垒机上的 Google Authenticator、Duo、RSA SecurID 都走它。
/// 服务端可以来回问任意多轮，每轮任意多条提示。
/// </remarks>
public sealed class KeyboardInteractiveCredential : SshCredential
{
    private readonly Func<SshKeyboardChallenge, CancellationToken, ValueTask<IReadOnlyList<string>>> _responder;

    /// <summary>用一个应答回调构造。</summary>
    /// <param name="responder">
    /// 收到一轮询问时被调用，返回与 <see cref="SshKeyboardChallenge.Prompts"/>
    /// <b>条数完全相等</b>的答案（纯展示轮返回空列表）。
    /// </param>
    /// <param name="label">给使用者看的标签,进认证尝试记录。</param>
    public KeyboardInteractiveCredential(
        Func<SshKeyboardChallenge, CancellationToken, ValueTask<IReadOnlyList<string>>> responder,
        string? label = null)
    {
        _responder = responder ?? throw new ArgumentNullException(nameof(responder));
        Label = label ?? "keyboard-interactive";
    }

    /// <inheritdoc />
    public override string MethodName => SshAlgorithmNames.AuthKeyboardInteractive;

    /// <inheritdoc />
    public override string Label { get; }

    /// <summary>应答一轮询问。</summary>
    public ValueTask<IReadOnlyList<string>> RespondAsync(
        SshKeyboardChallenge challenge, CancellationToken cancellationToken = default) =>
        _responder(challenge, cancellationToken);
}

/// <summary>公钥认证。</summary>
/// <remarks>
/// 私钥从哪来由 <see cref="ISshSigner"/> 决定：文件、ssh-agent、PKCS#11、HSM、
/// 云密钥服务 —— <b>私钥可以从不进程内</b>。
/// </remarks>
public sealed class PublicKeyCredential : SshCredential
{
    /// <summary>用一个签名器构造。</summary>
    /// <param name="signer">签名器。</param>
    /// <param name="label">给使用者看的标签（如私钥文件路径）。</param>
    public PublicKeyCredential(ISshSigner signer, string? label = null)
    {
        Signer = signer ?? throw new ArgumentNullException(nameof(signer));
        Label = label ?? $"publickey ({signer.PublicKey.KeyType})";
    }

    /// <inheritdoc />
    public override string MethodName => SshAlgorithmNames.AuthPublicKey;

    /// <inheritdoc />
    public override string Label { get; }

    /// <summary>签名器。</summary>
    public ISshSigner Signer { get; }
}

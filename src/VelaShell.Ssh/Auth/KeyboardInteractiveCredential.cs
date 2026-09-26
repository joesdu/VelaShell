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
    public override string MethodName => SshProtocolNames.AuthKeyboardInteractive;

    /// <inheritdoc />
    public override string Label { get; }

    /// <summary>应答一轮询问。</summary>
    public ValueTask<IReadOnlyList<string>> RespondAsync(
        SshKeyboardChallenge challenge, CancellationToken cancellationToken = default) =>
        _responder(challenge, cancellationToken);
}

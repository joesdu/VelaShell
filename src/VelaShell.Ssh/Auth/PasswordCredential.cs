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
    public override string MethodName => SshProtocolNames.AuthPassword;

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

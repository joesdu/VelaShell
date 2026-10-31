namespace VelaShell.Core.Data;

/// <summary>
/// 敏感字段(密码/口令)的静态加密。实现使用 AES-256-GCM 与本机加密密钥(系统密钥链保管,
/// 用不了时回落到本地密钥文件),密文带版本前缀;<see cref="Unprotect" /> 对无前缀的历史明文原样返回,便于平滑迁移。
/// </summary>
public interface ISecretProtector
{
    /// <summary>加密明文;null/空串原样返回。对已加密的值幂等(不重复加密)。</summary>
    string? Protect(string? plaintext);

    /// <summary>解密密文;无法识别的值视为历史明文原样返回。</summary>
    /// <remarks>
    /// 解不开(密钥不在、数据损坏)时也原样返回 —— 调用方把它再存回去时密文不会丢。
    /// 要区分「解开了」与「解不开」的调用方(要拿它去加密别的东西、或者发给第三方)用 <see cref="TryUnprotect" />。
    /// </remarks>
    string? Unprotect(string? ciphertext);

    /// <summary>
    /// 解密密文,并如实说明解没解开。无前缀的历史明文与 null/空串算解开了(原样交回)。
    /// </summary>
    /// <param name="ciphertext">存着的值。</param>
    /// <param name="plaintext">解开时为明文;解不开时为 null。</param>
    /// <returns>解开了(或本来就不是密文)为 true;是密文却解不开为 false。</returns>
    bool TryUnprotect(string? ciphertext, out string? plaintext)
    {
        plaintext = Unprotect(ciphertext);
        return true;
    }

    /// <summary>
    /// 这会儿加解密会不会卡住:本机加密密钥还没解析完(启动时在后台问系统密钥链,keyring 锁着时要等用户解锁)为 false。
    /// </summary>
    /// <remarks>
    /// 只给不能在这里干等的调用方用来绕开(UI 线程上的启动路径,见 <c>ISettingsService.GetStartupSnapshot</c>);
    /// 不查询、不触发解析。不需要解析密钥的实现恒为 true。
    /// </remarks>
    bool IsReady => true;
}

namespace VelaShell.Core.Data;

/// <summary>
/// 敏感字段(密码/口令)的静态加密。实现使用 AES-256-GCM 与本地密钥文件,
/// 密文带版本前缀;<see cref="Unprotect"/> 对无前缀的历史明文原样返回,便于平滑迁移。
/// </summary>
public interface ISecretProtector
{
    /// <summary>加密明文;null/空串原样返回。对已加密的值幂等(不重复加密)。</summary>
    string? Protect(string? plaintext);

    /// <summary>解密密文;无法识别的值视为历史明文原样返回。</summary>
    string? Unprotect(string? ciphertext);
}

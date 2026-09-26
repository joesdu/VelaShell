// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   RFC 4253 §6.2    压缩:对**载荷**压缩,每个方向一个独立的、跨报文保留的 zlib 流
//   RFC 1950/1951    zlib 与 deflate 的容器与块格式(flush 语义出自这里)
//   OpenSSH PROTOCOL zlib@openssh.com —— 认证成功之后才开始压缩
//   行为规格:        velashell-docs/zh/ssh/spec/01-transport-framing.md §压缩

using VelaShell.Ssh.Crypto.Kex;
using VelaShell.Ssh.Protocol;

namespace VelaShell.Ssh.Crypto;

/// <summary>按协商出的算法名造压缩器。</summary>
internal static class SshCompressorFactory
{
    /// <summary>这个算法名是不是「认证之后才开始压缩」的那一种。</summary>
    /// <remarks>
    /// <para>
    /// <c>zlib@openssh.com</c> 把压缩推迟到认证成功之后。
    /// </para>
    /// <para>
    /// 推迟是有理由的：认证之前的报文里有密码与公钥，而压缩会让
    /// <b>密文长度泄漏明文的可压缩性</b> —— 对着一个长度可观测的口令做
    /// 压缩旁路攻击（CRIME 那一类）是现实的。推迟之后，攻击者要先过认证
    /// 才能让我们压缩他挑的内容，而那时他已经在里面了。
    /// </para>
    /// </remarks>
    public static bool IsDelayed(string algorithm) =>
        string.Equals(algorithm, SshAlgorithmNames.ZlibOpenSsh, StringComparison.Ordinal);

    /// <summary>协商出的压缩算法必须是本库实现的；否则抛出。</summary>
    /// <remarks>
    /// 只认 <c>none</c> 与 <c>zlib@openssh.com</c>。别的名字（包括裸 <c>zlib</c>）只可能是
    /// 使用者往 <see cref="SshAlgorithmSet"/> 里塞了本库不实现的算法 —— 这时要在协商当场响亮地失败，
    /// 不能悄悄按不压缩处理：对端会按谈成的算法去压，两端在下一个报文上就错位，
    /// 而那时报出来的只是一个看不出缘由的解密或解压错误。
    /// </remarks>
    /// <exception cref="SshKeyExchangeException">算法名不是本库实现的压缩算法。</exception>
    public static void EnsureSupported(string algorithm)
    {
        if (algorithm is not (SshAlgorithmNames.None or SshAlgorithmNames.ZlibOpenSsh))
        {
            throw new SshKeyExchangeException($"尚未实现的压缩算法：{algorithm}。");
        }
    }

    /// <summary>造一个压缩器；<c>none</c> 返回直通的那个。</summary>
    /// <exception cref="SshKeyExchangeException">算法名不是本库实现的压缩算法。</exception>
    public static ISshCompressor Create(string algorithm, int level = 6)
    {
        EnsureSupported(algorithm);
        return IsDelayed(algorithm) ? new ZlibCompressor(level) : NoCompression.Instance;
    }
}

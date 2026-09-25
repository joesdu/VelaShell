// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs

using System.Text;

namespace VelaShell.Ssh.Diagnostics;

/// <summary>把对端给的文本放进异常消息之前先清一遍。</summary>
/// <remarks>
/// <para>
/// 版本标识串、<c>DISCONNECT</c> 的描述、拒绝开通道的理由、SFTP 的状态消息、代理的应答 ——
/// 这些都来自对端，很多时候来自一个<b>还没被认证</b>的对端。异常消息最后会被打到终端或界面上，
/// 原样拼进去就是一个转义序列注入面（<c>ESC ] 52</c> 改剪贴板、清屏伪造提示、<c>CR</c> 盖掉前半行）。
/// </para>
/// <para>
/// 只清<b>进消息</b>的那一份：原话照样放在专门的属性里（如 <c>PeerDescription</c>、<c>ServerMessage</c>），
/// 那些属性的文档都写明了是不可信输入。
/// </para>
/// </remarks>
internal static class PeerText
{
    /// <summary>默认最多留多少个字符。</summary>
    public const int DefaultMaxLength = 256;

    /// <summary>控制字符、<c>DEL</c>、C1 控制码与双向文本控制符换成 <c>?</c>，超长的截断。</summary>
    /// <remarks>可打印的 Unicode 原样保留 —— 服务端的中文提示是正常的。</remarks>
    public static string Sanitize(string? text, int maxLength = DefaultMaxLength)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "";
        }

        bool truncated = text.Length > maxLength;
        ReadOnlySpan<char> source = truncated ? text.AsSpan(0, maxLength) : text;

        StringBuilder builder = new(source.Length + 1);
        foreach (char c in source)
        {
            builder.Append(IsUnsafe(c) ? '?' : c);
        }

        if (truncated)
        {
            builder.Append('…');
        }
        return builder.ToString();
    }

    private static bool IsUnsafe(char c) =>
        c is < ' '                                   // C0：ESC、CR、LF、BEL …
        or >= '\u007F' and <= '\u009F'       // DEL 与 C1（\u009B 在一些终端上就是 CSI）
        or >= '‪' and <= '‮'       // 双向嵌入 / 覆盖：日志里伪造显示顺序
        or >= '⁦' and <= '⁩';      // 双向隔离
}

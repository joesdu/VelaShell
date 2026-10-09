// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs

namespace VelaShell.XServer;

/// <summary>
/// 一份剪贴板内容的几种格式:文本、HTML(富文本)、PNG 图片 —— 至少一种。X 程序复制的(<see cref="IX11ServerHost.ClipboardContentChanged" />)、
/// 宿主交给 X 的(<see cref="X11Server.SetClipboard" />)都用它;X 那边按 ICCCM 的目标给出(UTF8_STRING / STRING / TEXT / COMPOUND_TEXT、
/// <c>text/html</c>、<c>image/png</c>)。数据只读:同一份内容在服务端与宿主之间共用。
/// </summary>
public sealed record XClipboardContent
{
    /// <summary>文本;没有为 null。</summary>
    public string? Text { get; init; }

    /// <summary>HTML 片段(UTF-8 文本);没有为 null。</summary>
    public string? Html { get; init; }

    /// <summary>PNG 编码的图片;没有为空。</summary>
    public ReadOnlyMemory<byte> Png { get; init; }

    /// <summary>一种格式都没有。</summary>
    public bool IsEmpty => Text is null && Html is null && Png.IsEmpty;

    /// <summary>与 <paramref name="other" /> 的每种格式都相同(图片逐字节比)。</summary>
    public bool SameAs(XClipboardContent? other) =>
        other is not null && Text == other.Text && Html == other.Html && Png.Span.SequenceEqual(other.Png.Span);
}

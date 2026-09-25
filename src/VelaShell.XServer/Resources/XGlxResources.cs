// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   GLX Extensions for OpenGL Protocol Specification, Version 1.3 —— 渲染上下文(直接 / 间接)与 GLX 可绘对象(GLXWindow / GLXPixmap / GLXPbuffer)

using VelaShell.XServer.Gl;
using VelaShell.XServer.Server;

namespace VelaShell.XServer.Resources;

/// <summary>GLX 渲染上下文。间接的带一个 <see cref="GlContext" />;直接的只是登记(渲染发生在客户端)。</summary>
internal sealed class XGlxContext(uint id, XClient owner, GlxExtension.GlxConfig config, bool direct, GlContext? gl) : XResource(id, owner)
{
    public GlxExtension.GlxConfig Config { get; } = config;

    public bool Direct { get; } = direct;

    public GlContext? Gl { get; } = gl;

    /// <summary>当前在哪个客户端的哪个标签上;不是当前时为 null。</summary>
    public (XClient Client, uint Tag)? Current { get; set; }
}

/// <summary>GLX 可绘对象:GLXWindow / GLXPixmap / GLXPbuffer。</summary>
internal sealed class XGlxDrawable(uint id, XClient owner, GlxExtension.GlxDrawableKind kind, uint target, GlxExtension.GlxConfig config)
    : XResource(id, owner)
{
    public GlxExtension.GlxDrawableKind Kind { get; } = kind;

    /// <summary>对应的 X 窗口 / 像素图;Pbuffer 为 0。</summary>
    public uint Target { get; } = target;

    public GlxExtension.GlxConfig Config { get; } = config;

    public uint EventMask { get; set; }

    public int PbufferWidth { get; init; }

    public int PbufferHeight { get; init; }

    public bool PreservedContents { get; init; }

    public bool LargestPbuffer { get; init; }
}

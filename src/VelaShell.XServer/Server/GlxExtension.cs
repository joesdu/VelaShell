// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   GLX Extensions for OpenGL Protocol Specification, Version 1.3 ——
//   §1.5「Errors」(GLXBadContext +0、BadContextState +1、BadDrawable +2、BadPixmap +3、BadContextTag +4、BadCurrentWindow +5、
//   BadRenderRequest +6、BadLargeRequest +7、UnsupportedPrivateRequest +8、BadFBConfig +9、BadPbuffer +10、
//   BadCurrentDrawable +11、BadWindow +12)、§1.6「Events」(PbufferClobber)、§1.8「Context Tags」(标签由服务端在 MakeCurrent
//   成功时发给客户端,每客户端唯一;只有 CopyContext / SwapBuffers / MakeCurrent / MakeContextCurrent 可以带 0)、
//   §2.1「Requests for GLX Commands」(Render 1、RenderLarge 2、CreateContext 3、DestroyContext 4、MakeCurrent 5、IsDirect 6、
//   QueryVersion 7、WaitGL 8、WaitX 9、CopyContext 10、SwapBuffers 11、UseXFont 12、CreateGLXPixmap 13、GetVisualConfigs 14
//   (18 个有序属性后跟属性对)、DestroyGLXPixmap 15、VendorPrivate 16、VendorPrivateWithReply 17、QueryExtensionsString 18、
//   QueryServerString 19、ClientInfo 20、GetFBConfigs 21、CreatePixmap 22、DestroyPixmap 23、CreateNewContext 24、
//   QueryContext 25、MakeContextCurrent 26、CreatePbuffer 27、DestroyPbuffer 28、GetDrawableAttributes 29、
//   ChangeDrawableAttributes 30、CreateWindow 31、DestroyWindow 32)、§2.2「Requests for GL Non-rendering Commands」
//   (101–159;Get*v 的回复:n = 1 时值在第 16 字节,否则从第 32 字节起)、§2.3.2「Send a Large GL Rendering Command」。
//   OpenGL Graphics with the X Window System, Version 1.4 —— §3.3.3「Configuration Management」(FBConfig 属性,Table 3.1)、
//   §3.3.5「On Screen Rendering」、§3.3.7「Rendering Contexts」(第一次成为当前时视口初始化为可绘对象的尺寸)、
//   §3.3.10「Double Buffering」、§3.5「Backwards Compatibility」(GLX 1.2 的窗口可以直接当 GLX 可绘对象)。
//   Khronos EXT_texture_object —— 「GLX Protocol」一节(AreTexturesResidentEXT / DeleteTexturesEXT / GenTexturesEXT / IsTextureEXT
//   走 VendorPrivate(WithReply),厂商码 11 / 12 / 13 / 14,之后是上下文标签与参数)。
//   Khronos GLX_ARB_create_context / GLX_ARB_create_context_profile —— 「GLX Protocol」一节(SetClientInfoARB 33、
//   CreateContextAttribsARB 34:context、fbconfig、screen、share_list、isdirect、两个保留字段、num_attribs,再跟属性对、
//   SetClientInfo2ARB 35)与「Errors」一节(版本与特性组合没有定义 → BadMatch;配置给不了请求的版本 → GLXBadFBConfig;
//   不认识的属性或标志位 → BadValue;profile 掩码不合法或不支持 → GLXBadProfileARB;版本低于 3.2 时 profile 掩码不看)。
//   Khronos GLX_EXT_libglvnd —— QueryServerString 认 GLX_VENDOR_NAMES_EXT(0x20F6,按偏好排的厂商名)、GetDrawableAttributes
//   的回复带 GLX_SCREEN(「GLX Protocol」一节:不加新请求)。
//   枚举值对照 Khronos GLX API Registry(glx.xml)。
//
//   间接上下文由 Gl/GlContext 执行;直接上下文(is direct = True,比如 Mesa 在客户端用软件渲染、再经 PutImage 送像素)
//   服务端只做登记。前缓冲在请求处理完后拷进 X 窗口 / 像素图;GLX 窗口与直接当 GLX 可绘对象用的窗口共用一套缓冲。
//   GetString / QueryServerString 的串带上结尾的 NUL(STRING8 长度算在内):客户端库按 C 串使用。

using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;
using VelaShell.XServer.Drawing;
using VelaShell.XServer.Gl;
using VelaShell.XServer.Protocol;
using VelaShell.XServer.Resources;
using VelaShell.XServer.Windowing;

namespace VelaShell.XServer.Server;

/// <summary>
/// GLX 扩展:自己的状态(上下文标签、帧缓冲表面、拼到一半的 RenderLarge)与全部请求处理。自成一体 ——
/// 只经服务端少数 internal 成员碰资源表、绘图目标与损伤。只在执行线程上用。
/// </summary>
/// <remarks>
/// 上下文 ID 是全局的:任何客户端都可以拿别人的上下文当 share list(读到对方的纹理与显示列表)、MakeCurrent 别人的上下文、
/// CopyContext / DestroyContext 别人的(标记「跨客户端」的几处)。这与核心协议「客户端之间不隔离」的信任模型一致,
/// 不是越权;上下文标签按客户端分表,伪造标签不可行。做非受信的连接级别(feature-plan F2)时这几处要一起收紧(xs_plan GL-D1)。
/// </remarks>
internal sealed class GlxExtension(X11Server server)
{
    private const byte GlxBadContext = X11Server.GlxErrorBase + 0;
    private const byte GlxBadDrawable = X11Server.GlxErrorBase + 2;
    private const byte GlxBadPixmap = X11Server.GlxErrorBase + 3;
    private const byte GlxBadContextTag = X11Server.GlxErrorBase + 4;
    private const byte GlxBadCurrentWindow = X11Server.GlxErrorBase + 5;
    private const byte GlxBadCurrentDrawable = X11Server.GlxErrorBase + 11;
    private const byte GlxBadRenderRequest = X11Server.GlxErrorBase + 6;
    private const byte GlxBadLargeRequest = X11Server.GlxErrorBase + 7;
    private const byte GlxUnsupportedPrivateRequest = X11Server.GlxErrorBase + 8;
    private const byte GlxBadFBConfig = X11Server.GlxErrorBase + 9;
    private const byte GlxBadPbuffer = X11Server.GlxErrorBase + 10;
    private const byte GlxBadWindow = X11Server.GlxErrorBase + 12;
    private const byte GlxBadProfileArb = X11Server.GlxErrorBase + 13;

    // GLX 枚举(glx.xml)
    private const uint GLX_VENDOR = 1, GLX_VERSION = 2, GLX_EXTENSIONS = 3, GLX_VENDOR_NAMES_EXT = 0x20F6;
    private const uint GLX_USE_GL = 1, GLX_BUFFER_SIZE = 2, GLX_LEVEL = 3, GLX_RGBA = 4, GLX_DOUBLEBUFFER = 5, GLX_STEREO = 6,
        GLX_AUX_BUFFERS = 7, GLX_RED_SIZE = 8, GLX_GREEN_SIZE = 9, GLX_BLUE_SIZE = 10, GLX_ALPHA_SIZE = 11, GLX_DEPTH_SIZE = 12,
        GLX_STENCIL_SIZE = 13, GLX_ACCUM_RED_SIZE = 14, GLX_ACCUM_GREEN_SIZE = 15, GLX_ACCUM_BLUE_SIZE = 16, GLX_ACCUM_ALPHA_SIZE = 17,
        GLX_CONFIG_CAVEAT = 0x20, GLX_X_VISUAL_TYPE = 0x22, GLX_TRANSPARENT_TYPE = 0x23, GLX_TRANSPARENT_INDEX_VALUE = 0x24,
        GLX_TRANSPARENT_RED_VALUE = 0x25, GLX_TRANSPARENT_GREEN_VALUE = 0x26, GLX_TRANSPARENT_BLUE_VALUE = 0x27,
        GLX_TRANSPARENT_ALPHA_VALUE = 0x28, GLX_NONE = 0x8000, GLX_TRUE_COLOR = 0x8002, GLX_VISUAL_ID = 0x800B, GLX_SCREEN = 0x800C,
        GLX_DRAWABLE_TYPE = 0x8010, GLX_RENDER_TYPE = 0x8011, GLX_X_RENDERABLE = 0x8012, GLX_FBCONFIG_ID = 0x8013,
        GLX_RGBA_TYPE = 0x8014, GLX_MAX_PBUFFER_WIDTH = 0x8016, GLX_MAX_PBUFFER_HEIGHT = 0x8017, GLX_MAX_PBUFFER_PIXELS = 0x8018,
        GLX_PRESERVED_CONTENTS = 0x801B, GLX_LARGEST_PBUFFER = 0x801C, GLX_WIDTH = 0x801D, GLX_HEIGHT = 0x801E,
        GLX_EVENT_MASK = 0x801F, GLX_PBUFFER_HEIGHT = 0x8040, GLX_PBUFFER_WIDTH = 0x8041, GLX_SAMPLE_BUFFERS = 100000,
        GLX_SAMPLES = 100001, GLX_WINDOW_BIT = 1, GLX_PIXMAP_BIT = 2, GLX_PBUFFER_BIT = 4, GLX_RGBA_BIT = 1,
        GLX_PBUFFER_CLOBBER_MASK = 0x08000000, GLX_COLOR_INDEX_TYPE = 0x8015;

    // GLX_ARB_create_context / _profile
    private const uint GLX_CONTEXT_MAJOR_VERSION_ARB = 0x2091, GLX_CONTEXT_MINOR_VERSION_ARB = 0x2092, GLX_CONTEXT_FLAGS_ARB = 0x2094,
        GLX_CONTEXT_PROFILE_MASK_ARB = 0x9126, GLX_CONTEXT_DEBUG_BIT_ARB = 1, GLX_CONTEXT_FORWARD_COMPATIBLE_BIT_ARB = 2,
        GLX_CONTEXT_CORE_PROFILE_BIT_ARB = 1, GLX_CONTEXT_COMPATIBILITY_PROFILE_BIT_ARB = 2;

    private const int MaxPbufferSize = 4096;

    /// <summary>
    /// 一个 GLX 表面的像素数上限(默认 4096 × 4096):超大窗口不一次分配几个 GB。超了的可绘对象表面夹到每边至多 √上限、
    /// 只渲染它左下的那一块(Q8;原先每个 GL 请求回 BadAlloc,程序因 X 错误退出)。可以改小只为测试。
    /// </summary>
    internal long MaxSurfacePixels { get; set; } = 4096L * 4096;

    /// <summary>GLX 可绘对象的种类。</summary>
    internal enum GlxDrawableKind
    {
        Window,
        Pixmap,
        Pbuffer,
    }

    /// <summary>一个 FBConfig:对应一个 X 视觉;颜色 8/8/8(ARGB 视觉再加 8 位 alpha),深度 24、模板 8,没有累积缓冲与多重采样。</summary>
    internal sealed record GlxConfig(uint Id, uint Visual, byte Depth, bool DoubleBuffer, bool Alpha, uint Samples = 0);

    private static readonly GlxConfig[] GlxConfigs =
    [
        new(0x101, X11Server.RootVisualId, 24, DoubleBuffer: true, Alpha: false),
        new(0x102, X11Server.RootVisualId, 24, DoubleBuffer: false, Alpha: false),
        new(0x103, X11Server.ArgbVisualId, 32, DoubleBuffer: true, Alpha: true),
        new(0x104, X11Server.ArgbVisualId, 32, DoubleBuffer: false, Alpha: true),
        new(0x105, X11Server.RootVisualId, 24, DoubleBuffer: true, Alpha: false, Samples: 4),
        new(0x106, X11Server.ArgbVisualId, 32, DoubleBuffer: true, Alpha: true, Samples: 4),
    ];

    /// <summary>
    /// GLX 1.2 的视觉配置。一个 X 视觉可以对应多个 GLX 配置，
    /// 因此双缓冲与单缓冲配置都必须发布；客户端会用 DOUBLEBUFFER 和 FB_CONFIG_ID
    /// 选择它需要的那一项。
    /// </summary>
    private static readonly GlxConfig[] GlxVisualConfigs = GlxConfigs;

    /// <summary>
    /// 可绘对象的帧缓冲,按 X 窗口 / 像素图 / Pbuffer 的 ID 存(同一个窗口的各种用法共用一份),连同建表面时那个 ID 上的资源:
    /// ID 会被重用(客户端走了、编号给了下一个客户端),拿同一个 ID 的新资源不能接着用旧表面、读到上一个的内容(见 <see cref="TryGetSurface" />)。
    /// </summary>
    private readonly Dictionary<uint, SurfaceEntry> _glxSurfaces = [];

    /// <summary>
    /// 一块表面:建它时那个 ID 上的资源,以及它记在谁的账上、记了多少(xs_plan GL-S3:表面的颜色、深度、模板按像素记在
    /// 第一个要它的客户端名下,与 DBE 的后缓冲一样;丢掉表面时如数退还)。
    /// </summary>
    private sealed class SurfaceEntry(XResource source, GlSurface surface, XClient chargedTo)
    {
        public XResource Source { get; } = source;

        public GlSurface Surface { get; } = surface;

        public XClient ChargedTo { get; } = chargedTo;

        public long Charged { get; set; }
    }

    /// <summary>把 GL 对象的内存记到一个客户端的账上(xs_plan X-2 的每客户端 / 全局两道上限)。</summary>
    private sealed class ClientGlAccount(X11Server server, XClient client) : IGlMemoryAccount
    {
        public bool TryCharge(long bytes)
        {
            if (!server.CanCharge(client, bytes))
            {
                return false;
            }
            server.ChargeMemory(client, bytes);
            return true;
        }

        public void Refund(long bytes) => server.RefundMemory(client, bytes);
    }

    /// <summary>现存的 GLX 表面数(测试用)。</summary>
    internal int SurfaceCount => _glxSurfaces.Count;

    /// <summary>每个客户端的上下文标签 → 当前绑定。</summary>
    private readonly Dictionary<XClient, Dictionary<uint, GlxBinding>> _glxTags = [];

    /// <summary>每个客户端正在拼的 RenderLarge。</summary>
    private readonly Dictionary<XClient, GlxLargeCommand> _glxLarge = [];

    /// <summary>X 窗口 → 建在它上面的 GLXWindow(一个窗口只能有一个);GLXWindow 离开资源表时摘掉(<see cref="ResourceFreed" />)。</summary>
    private readonly Dictionary<uint, XGlxDrawable> _glxWindows = [];

    private uint _nextGlxTag;

    /// <summary>一个标签上的当前绑定;<see cref="DrawIsWindow" />:绘制可绘对象是窗口(GLX 1.2 的窗口或 GLXWindow),它没了时报 GLXBadCurrentWindow。</summary>
    private sealed record GlxBinding(XGlxContext Context, uint Draw, uint Read, bool DrawIsWindow);

    private sealed class GlxLargeCommand(uint tag, int total, int opcode, int length)
    {
        public uint Tag { get; } = tag;

        public int Total { get; } = total;

        public int Next { get; set; } = 2;

        public int Opcode { get; } = opcode;

        /// <summary>正文的字节数:第一段声明的长度减去 8 字节的头(长度与操作码)。</summary>
        public int Length { get; } = length;

        /// <summary>拼正文的缓冲:正文加上至多 3 字节的补齐,第一段到时一次分配。</summary>
        public byte[] Buffer { get; } = new byte[length + 3];

        /// <summary>已经拼进来的字节数。</summary>
        public int Filled { get; set; }
    }

    /// <summary>ReadPixels / GetTexImage 的回复最多这么大:再大就超过一个客户端的输出队列上限(<see cref="XClient.MaxQueuedOutputBytes" />)了。</summary>
    private const long MaxPixelReplyBytes = XClient.MaxQueuedOutputBytes / 2;

    private static XProtocolError GlxError(byte code, uint value = 0) => new((XErrorCode)code, value);

    /// <summary>一条 GLX 请求(按次操作码分派)。</summary>
    public void Handle(XClient c, XRequestReader r)
    {
        byte minor = r.Data;
        switch (minor)
        {
            case 1:
                GlxRender(c, r);
                break;
            case 2:
                GlxRenderLarge(c, r);
                break;
            case 3:   // CreateContext
                {
                    uint id = r.U32(), visual = r.U32(), screen = r.U32(), share = r.U32();
                    bool direct = r.Bool();
                    CheckGlxScreen(screen);
                    GlxConfig config = GlxVisualConfigs.FirstOrDefault(cfg => cfg.Visual == visual)
                                       ?? throw new XProtocolError(XErrorCode.Value, visual);
                    CreateGlxContext(c, id, config, share, direct);
                    break;
                }
            case 4:   // DestroyContext(跨客户端:别人的也能销毁,见类注释)
                {
                    uint id = r.U32();
                    _ = server.Use<XGlxContext>(id) ?? throw GlxError(GlxBadContext, id);
                    server.RemoveResource(id);   // 还是当前的上下文要等不再是当前时才真正释放:绑定里留着引用
                    break;
                }
            case 5:   // MakeCurrent
                {
                    uint drawable = r.U32(), context = r.U32(), oldTag = r.U32();
                    GlxMakeCurrent(c, oldTag, drawable, drawable, context);
                    break;
                }
            case 6:   // IsDirect
                {
                    uint id = r.U32();
                    XGlxContext ctx = server.Use<XGlxContext>(id) ?? throw GlxError(GlxBadContext, id);
                    c.Reply(0, w => w.Bool(ctx.Direct).Zero(23));
                    break;
                }
            case 7:   // QueryVersion:1.4
                {
                    r.U32();   // 客户端主版本
                    uint minorVersion = Math.Min(r.U32(), 4u);
                    c.Reply(0, w => w.U32(1).U32(minorVersion).Zero(16));
                    break;
                }
            case 8:   // WaitGL
            case 9:   // WaitX
                {
                    GlxBinding binding = GlxBindingOf(c, r.U32());
                    CheckCurrentDrawable(binding);
                    PresentGlx(binding);
                    break;
                }
            case 10:   // CopyContext
                {
                    uint source = r.U32(), dest = r.U32(), mask = r.U32(), tag = r.U32();
                    XGlxContext src = server.Use<XGlxContext>(source) ?? throw GlxError(GlxBadContext, source);
                    XGlxContext dst = server.Use<XGlxContext>(dest) ?? throw GlxError(GlxBadContext, dest);   // 跨客户端:见类注释
                    if (tag != 0)
                    {
                        GlxBinding current = GlxBindingOf(c, tag);
                        if (!ReferenceEquals(current.Context, src))
                        {
                            throw new XProtocolError(XErrorCode.Match);
                        }
                        CheckCurrentDrawable(current);
                    }
                    if (dst.Current is not null)
                    {
                        throw new XProtocolError(XErrorCode.Access);
                    }
                    if (src.Gl is null || dst.Gl is null)
                    {
                        throw new XProtocolError(XErrorCode.Match);   // 直接上下文的状态不在服务端
                    }
                    dst.Gl.State.Restore(src.Gl.State.Snapshot(mask), mask);
                    break;
                }
            case 11:   // SwapBuffers
                {
                    uint tag = r.U32(), drawable = r.U32();
                    if (tag != 0)
                    {
                        GlxBinding binding = GlxBindingOf(c, tag);
                        CheckCurrentDrawable(binding);
                        PresentGlx(binding);
                    }
                    (uint key, _, _) = ResolveGlxDrawable(drawable, null);
                    if (TryGetSurface(key, out GlSurface? surface) && surface.DoubleBuffered)
                    {
                        surface.Swap();
                        PresentSurface(surface);
                    }
                    break;
                }
            case 12:   // UseXFont
                GlxUseXFont(c, r);
                break;
            case 13:   // CreateGLXPixmap
                {
                    uint screen = r.U32(), visual = r.U32(), pixmap = r.U32(), glxPixmap = r.U32();
                    CheckGlxScreen(screen);
                    GlxConfig config = GlxVisualConfigs.FirstOrDefault(cfg => cfg.Visual == visual)
                                       ?? throw new XProtocolError(XErrorCode.Value, visual);
                    CreateGlxPixmap(c, glxPixmap, pixmap, config);
                    break;
                }
            case 14:   // GetVisualConfigs
                CheckGlxScreen(r.U32());
                ReplyVisualConfigs(c);
                break;
            case 15:   // DestroyGLXPixmap
            case 23:   // DestroyPixmap
                {
                    uint id = r.U32();
                    if (server.Use<XGlxDrawable>(id) is not { Kind: GlxDrawableKind.Pixmap })
                    {
                        throw GlxError(GlxBadPixmap, id);
                    }
                    server.RemoveResource(id);
                    break;
                }
            case 16:   // VendorPrivate
            case 17:   // VendorPrivateWithReply
                {
                    // GL_EXT_texture_object(扩展串里声明了)的四个非渲染命令走厂商私有请求:厂商码之后的正文(标签起)与
                    // 1.1 的 AreTexturesResident / DeleteTextures / GenTextures / IsTexture(Single 143–146)逐字节相同。
                    // 原先一律回 GLXUnsupportedPrivateRequest。
                    uint vendorCode = r.U32();
                    byte single = (minor, vendorCode) switch
                    {
                        (17, 11) => 143,   // AreTexturesResidentEXT
                        (16, 12) => 144,   // DeleteTexturesEXT
                        (17, 13) => 145,   // GenTexturesEXT
                        (17, 14) => 146,   // IsTextureEXT
                        _ => throw GlxError(GlxUnsupportedPrivateRequest, vendorCode),
                    };
                    GlxSingle(c, single, r);
                    break;
                }
            case 18:   // QueryExtensionsString
                CheckGlxScreen(r.U32());
                ReplyGlxString(c, GlxExtensionsString);
                break;
            case 19:   // QueryServerString
                {
                    CheckGlxScreen(r.U32());
                    string value = r.U32() switch
                    {
                        GLX_VENDOR => "VelaShell",
                        GLX_VERSION => "1.4",
                        GLX_EXTENSIONS => GlxExtensionsString,
                        GLX_VENDOR_NAMES_EXT => GlxVendorNames,
                        var name => throw new XProtocolError(XErrorCode.Value, name),
                    };
                    ReplyGlxString(c, value);
                    break;
                }
            case 20:   // ClientInfo:客户端的 GL 版本与扩展,只影响 GetString 的协商 —— 这里的串是固定的
            case 33:   // SetClientInfoARB:同上,另带客户端支持的 GL 版本表
            case 35:   // SetClientInfo2ARB:同上,版本表里每项多一个 profile
                break;
            case 21:   // GetFBConfigs
                CheckGlxScreen(r.U32());
                ReplyFbConfigs(c);
                break;
            case 22:   // CreatePixmap
                {
                    uint screen = r.U32(), fbconfig = r.U32(), pixmap = r.U32(), glxPixmap = r.U32();
                    CheckGlxScreen(screen);
                    CreateGlxPixmap(c, glxPixmap, pixmap, FbConfig(fbconfig));
                    break;
                }
            case 24:   // CreateNewContext
                {
                    uint id = r.U32(), fbconfig = r.U32(), screen = r.U32(), renderType = r.U32(), share = r.U32();
                    bool direct = r.Bool();
                    CheckGlxScreen(screen);
                    GlxConfig config = FbConfig(fbconfig);
                    if (renderType != GLX_RGBA_TYPE)
                    {
                        throw new XProtocolError(XErrorCode.Value, renderType);   // 没有颜色索引配置
                    }
                    CreateGlxContext(c, id, config, share, direct);
                    break;
                }
            case 25:   // QueryContext
                {
                    uint id = r.U32();
                    XGlxContext ctx = server.Use<XGlxContext>(id) ?? throw GlxError(GlxBadContext, id);
                    ReplyAttributes(c, [(GLX_FBCONFIG_ID, ctx.Config.Id), (GLX_RENDER_TYPE, GLX_RGBA_TYPE), (GLX_SCREEN, 0)]);
                    break;
                }
            case 26:   // MakeContextCurrent
                {
                    uint oldTag = r.U32(), drawable = r.U32(), read = r.U32(), context = r.U32();
                    GlxMakeCurrent(c, oldTag, drawable, read, context);
                    break;
                }
            case 27:   // CreatePbuffer
                GlxCreatePbuffer(c, r);
                break;
            case 28:   // DestroyPbuffer
                {
                    uint id = r.U32();
                    if (server.Use<XGlxDrawable>(id) is not { Kind: GlxDrawableKind.Pbuffer })
                    {
                        throw GlxError(GlxBadPbuffer, id);
                    }
                    server.RemoveResource(id);   // 表面随之丢掉(ResourceFreed)
                    break;
                }
            case 29:   // GetDrawableAttributes
                ReplyDrawableAttributes(c, r.U32());
                break;
            case 30:   // ChangeDrawableAttributes
                {
                    uint id = r.U32();
                    uint count = r.U32();
                    XGlxDrawable drawable = server.Use<XGlxDrawable>(id) ?? throw GlxError(GlxBadDrawable, id);
                    for (uint i = 0; i < count; i++)
                    {
                        uint attribute = r.U32(), value = r.U32();
                        if (attribute != GLX_EVENT_MASK || (value & ~GLX_PBUFFER_CLOBBER_MASK) != 0)
                        {
                            throw new XProtocolError(XErrorCode.Value, attribute);
                        }
                        drawable.EventMask = value;
                    }
                    break;
                }
            case 31:   // CreateWindow
                {
                    uint screen = r.U32(), fbconfig = r.U32(), window = r.U32(), glxWindow = r.U32();
                    CheckGlxScreen(screen);
                    GlxConfig config = FbConfig(fbconfig);
                    XWindow target = server.Use<XWindow>(window) ?? throw GlxError(GlxBadWindow, window);
                    if (target.Depth != config.Depth || target.IsInputOnly)
                    {
                        throw new XProtocolError(XErrorCode.Match);
                    }
                    // 一个窗口只能有一个 GLXWindow。原先每次扫一遍整张资源表(表的大小由客户端决定),现在查自己的登记。
                    if (_glxWindows.TryGetValue(window, out XGlxDrawable? existing) && ReferenceEquals(server.Lookup<XGlxDrawable>(existing.Id), existing))
                    {
                        throw new XProtocolError(XErrorCode.Alloc);
                    }
                    XGlxDrawable created = new(glxWindow, c, GlxDrawableKind.Window, window, config);
                    server.AddResource(c, created);
                    _glxWindows[window] = created;
                    break;
                }
            case 32:   // DestroyWindow
                {
                    uint id = r.U32();
                    if (server.Use<XGlxDrawable>(id) is not { Kind: GlxDrawableKind.Window })
                    {
                        throw GlxError(GlxBadWindow, id);
                    }
                    server.RemoveResource(id);
                    break;
                }
            case 34:   // CreateContextAttribsARB
                GlxCreateContextAttribs(c, r);
                break;
            case >= 101 and <= 159:
                GlxSingle(c, minor, r);
                break;
            default:
                throw new XProtocolError(XErrorCode.Request);
        }
    }

    private const string GlxExtensionsString =
        "GLX_ARB_create_context GLX_ARB_create_context_profile GLX_ARB_get_proc_address GLX_EXT_libglvnd GLX_EXT_visual_info GLX_EXT_visual_rating";

    /// <summary>
    /// GLX_EXT_libglvnd 的 GLX_VENDOR_NAMES_EXT:客户端的 libglvnd 按它挑厂商库(规范 Issue 3:名字由服务端的 GLX 实现定,通常是驱动名)。
    /// 这里的直接渲染靠客户端 Mesa 的软件渲染(drisw),间接渲染的协议编码也是 Mesa 的客户端库在做,所以报 mesa —— 远端同时装了 NVIDIA 与
    /// Mesa 时,NVIDIA 的客户端库不会被选上再因为服务端不是 NVIDIA 而失败。
    /// </summary>
    private const string GlxVendorNames = "mesa";

    private static void CheckGlxScreen(uint screen)
    {
        if (screen != 0)
        {
            throw new XProtocolError(XErrorCode.Value, screen);
        }
    }

    private static GlxConfig FbConfig(uint id) => GlxConfigs.FirstOrDefault(cfg => cfg.Id == id) ?? throw GlxError(GlxBadFBConfig, id);

    private void CreateGlxContext(XClient c, uint id, GlxConfig config, uint shareId, bool direct)
    {
        XGlxContext? share = null;
        if (shareId != 0)
        {
            share = server.Use<XGlxContext>(shareId) ?? throw GlxError(GlxBadContext, shareId);   // 跨客户端:见类注释
            if (share.Direct != direct)
            {
                throw new XProtocolError(XErrorCode.Match);   // 直接与间接上下文不在同一个地址空间
            }
        }
        if (direct)
        {
            server.AddResource(c, new XGlxContext(id, c, config, direct, null));
            return;
        }
        // 间接上下文:对象本身按 GlContext.ObjectBytes 记账(先核再建,出错的请求不留下任何效果);
        // 它此后的分配(默认纹理、图元缓冲、新建的共享组里的列表与纹理)都记在这个客户端名下。
        server.RequireMemory(c, X11Server.ResourceOverheadBytes + GlContext.ObjectBytes);
        GlContext gl = new(config.DoubleBuffer, config.Alpha, share?.Gl?.Shared, new ClientGlAccount(server, c));
        XGlxContext context = new(id, c, config, direct, gl);
        try
        {
            server.AddResource(c, context);
        }
        catch
        {
            gl.Release();   // ID 不对:共享组的引用数还回去
            throw;
        }
        server.ChargeMemory(c, GlContext.ObjectBytes);
        context.Charged += GlContext.ObjectBytes;   // 随资源离开资源表一并退还
    }

    /// <summary>
    /// CreateContextAttribsARB(GLX_ARB_create_context / _profile)。直接上下文的 GL 在客户端(drisw 之类按请求的版本与 profile 建),
    /// 服务端只登记,版本、标志与 profile 不核,别的扩展的属性(鲁棒性、释放行为……)也由客户端的驱动处理。
    /// 间接上下文由这里的软件 GL 执行,它只有 1.1 的兼容 profile:要 3.2 起的核心 profile 回 GLXBadProfileARB,
    /// 要比 1.1 高的版本回 GLXBadFBConfig(配置给不了这个版本),不认识的属性回 BadValue。
    /// </summary>
    private void GlxCreateContextAttribs(XClient c, XRequestReader r)
    {
        uint id = r.U32(), fbconfig = r.U32(), screen = r.U32(), share = r.U32();
        bool direct = r.Bool();
        r.Skip(3);   // reserved1、reserved2
        uint count = r.U32();
        if (count > (uint)(r.Remaining / 8))
        {
            throw new XProtocolError(XErrorCode.Length);
        }
        CheckGlxScreen(screen);
        GlxConfig config = FbConfig(fbconfig);
        // 缺省:1.0、无标志、核心 profile(版本低于 3.2 时不看)、RGBA。
        uint major = 1, minor = 0, flags = 0, profile = GLX_CONTEXT_CORE_PROFILE_BIT_ARB, renderType = GLX_RGBA_TYPE;
        for (uint i = 0; i < count; i++)
        {
            uint attribute = r.U32(), value = r.U32();
            switch (attribute)
            {
                case GLX_CONTEXT_MAJOR_VERSION_ARB:
                    major = value;
                    break;
                case GLX_CONTEXT_MINOR_VERSION_ARB:
                    minor = value;
                    break;
                case GLX_CONTEXT_FLAGS_ARB:
                    flags = value;
                    break;
                case GLX_CONTEXT_PROFILE_MASK_ARB:
                    profile = value;
                    break;
                case GLX_RENDER_TYPE:
                    renderType = value;
                    break;
                default:
                    if (!direct)
                    {
                        throw new XProtocolError(XErrorCode.Value, attribute);
                    }
                    break;
            }
        }
        if (renderType != GLX_RGBA_TYPE)
        {
            // 颜色索引是合法的类型,只是这几个配置都不支持;别的值不是渲染类型。
            throw renderType == GLX_COLOR_INDEX_TYPE ? new XProtocolError(XErrorCode.Match) : new XProtocolError(XErrorCode.Value, renderType);
        }
        if (!direct)
        {
            CheckIndirectVersion(fbconfig, major, minor, flags, profile);
        }
        CreateGlxContext(c, id, config, share, direct);
    }

    /// <summary>间接上下文要的版本、标志与 profile 这里的软件 GL(1.1、兼容 profile)给不给得了;给不了按扩展规范的「Errors」抛对应的错误。</summary>
    private static void CheckIndirectVersion(uint fbconfig, uint major, uint minor, uint flags, uint profile)
    {
        if ((flags & ~(GLX_CONTEXT_DEBUG_BIT_ARB | GLX_CONTEXT_FORWARD_COMPATIBLE_BIT_ARB)) != 0)
        {
            throw new XProtocolError(XErrorCode.Value, flags);   // 不认识的标志位
        }
        bool forwardCompatible = (flags & GLX_CONTEXT_FORWARD_COMPATIBLE_BIT_ARB) != 0;
        if (!IsDefinedGlVersion(major, minor) || (forwardCompatible && major < 3))
        {
            throw new XProtocolError(XErrorCode.Match);   // 版本与特性的组合没有定义(前向兼容只对 3.0 起有定义)
        }
        if (major > 3 || (major == 3 && minor >= 2))
        {
            // 3.2 起才看 profile 掩码:得正好是核心、兼容之一;核心 profile 这里没有。
            if (profile is not (GLX_CONTEXT_CORE_PROFILE_BIT_ARB or GLX_CONTEXT_COMPATIBILITY_PROFILE_BIT_ARB)
                or GLX_CONTEXT_CORE_PROFILE_BIT_ARB)
            {
                throw GlxError(GlxBadProfileArb, profile);
            }
        }
        if (major > 1 || minor > 1)
        {
            throw GlxError(GlxBadFBConfig, fbconfig);   // 版本报的是 1.1(GL_VERSION):更高的版本这个配置给不了
        }
    }

    /// <summary>OpenGL 定义过的版本:1.0–1.5、2.0–2.1、3.0–3.3、4.0–4.6。</summary>
    private static bool IsDefinedGlVersion(uint major, uint minor) => major switch
    {
        1 => minor <= 5,
        2 => minor <= 1,
        3 => minor <= 3,
        4 => minor <= 6,
        _ => false,
    };

    private void CreateGlxPixmap(XClient c, uint glxPixmap, uint pixmap, GlxConfig config)
    {
        XPixmap target = server.Use<XPixmap>(pixmap) ?? throw new XProtocolError(XErrorCode.Pixmap, pixmap);
        if (target.Depth != config.Depth)
        {
            throw new XProtocolError(XErrorCode.Match);
        }
        server.AddResource(c, new XGlxDrawable(glxPixmap, c, GlxDrawableKind.Pixmap, pixmap, config));
    }

    private void GlxCreatePbuffer(XClient c, XRequestReader r)
    {
        uint screen = r.U32(), fbconfig = r.U32(), id = r.U32(), count = r.U32();
        CheckGlxScreen(screen);
        GlxConfig config = FbConfig(fbconfig);
        int width = 0, height = 0;
        bool preserved = true, largest = false;
        for (uint i = 0; i < count; i++)
        {
            uint attribute = r.U32(), value = r.U32();
            switch (attribute)
            {
                case GLX_PBUFFER_WIDTH:
                    width = (int)value;
                    break;
                case GLX_PBUFFER_HEIGHT:
                    height = (int)value;
                    break;
                case GLX_PRESERVED_CONTENTS:
                    preserved = value != 0;
                    break;
                case GLX_LARGEST_PBUFFER:
                    largest = value != 0;
                    break;
            }
        }
        if (width is <= 0 or > MaxPbufferSize || height is <= 0 or > MaxPbufferSize)
        {
            if (!largest || width <= 0 || height <= 0)
            {
                throw new XProtocolError(XErrorCode.Alloc);
            }
            (width, height) = (Math.Min(width, MaxPbufferSize), Math.Min(height, MaxPbufferSize));
        }
        server.AddResource(c, new XGlxDrawable(id, c, GlxDrawableKind.Pbuffer, 0, config)
        {
            PbufferWidth = width,
            PbufferHeight = height,
            PreservedContents = preserved,
            LargestPbuffer = largest,
        });
    }

    // ------------------------------------------------------------------ 可绘对象与表面

    /// <summary>
    /// GLX 可绘对象 ID → (表面的键、它的 X 可绘对象尺寸、配置)。GLX 1.2 的写法里窗口本身也是 GLX 可绘对象,
    /// 那时配置是它的视觉的那一条(GetVisualConfigs 报的、双缓冲),上下文的视觉须一致 —— 原先取第一个绑上来的上下文的配置,
    /// 单缓冲的上下文先绑过,之后双缓冲的上下文绑到这块单缓冲的表面上,SwapBuffers 什么也不做、每个 Render 直接上屏。
    /// </summary>
    private (uint Key, (int Width, int Height) Size, GlxConfig? Config) ResolveGlxDrawable(uint id, GlxConfig? contextConfig)
    {
        switch (server.Use<XResource>(id))
        {
            case XGlxDrawable { Kind: GlxDrawableKind.Pbuffer } pbuffer:
                return (id, (pbuffer.PbufferWidth, pbuffer.PbufferHeight), pbuffer.Config);
            case XGlxDrawable { Kind: GlxDrawableKind.Window } glxWindow:
                {
                    XWindow window = server.Lookup<XWindow>(glxWindow.Target) ?? throw GlxError(GlxBadWindow, id);
                    return (window.Id, (window.Width, window.Height), glxWindow.Config);
                }
            case XGlxDrawable glxPixmap:
                {
                    XPixmap pixmap = server.Lookup<XPixmap>(glxPixmap.Target) ?? throw GlxError(GlxBadPixmap, id);
                    return (pixmap.Id, (pixmap.Width, pixmap.Height), glxPixmap.Config);
                }
            case XWindow window:
                if (contextConfig is not null && window.Visual != contextConfig.Visual)
                {
                    throw new XProtocolError(XErrorCode.Match);
                }
                return (window.Id, (window.Width, window.Height), GlxVisualConfigs.FirstOrDefault(cfg => cfg.Visual == window.Visual));
            default:
                throw GlxError(GlxBadDrawable, id);
        }
    }

    /// <summary>
    /// 表面(没有就按配置新建),尺寸跟上 X 可绘对象;超过 <see cref="MaxSurfacePixels" /> 的夹小、只盖住左下的一块(记一行日志)。
    /// 新建与变大都先记账(新建记在 <paramref name="requester" /> 名下,变大记在原来那个客户端名下),记不下回 BadAlloc、表面不变。
    /// </summary>
    private GlSurface SurfaceFor(XClient requester, uint key, (int Width, int Height) size, GlxConfig config)
    {
        (int width, int height) = size;
        if ((long)width * height > MaxSurfacePixels)
        {
            int side = (int)Math.Sqrt(MaxSurfacePixels);   // 颜色(前后)、深度、模板一共 13 字节 / 像素
            (width, height) = (Math.Min(width, side), Math.Min(height, side));
        }
        bool clamped = width < size.Width || height < size.Height;
        if (!TryGetEntry(key, out SurfaceEntry? entry))
        {
            XResource source = server.Lookup<XResource>(key) ?? throw GlxError(GlxBadDrawable, key);
            long bytes = GlSurface.BytesFor(width, height, config.DoubleBuffer);
            server.ChargeMemory(requester, bytes);
            GlSurface surface = new(key, width, height, config.DoubleBuffer, config.Alpha);
            surface.Resize(width, height, size.Width, size.Height);
            _glxSurfaces[key] = new SurfaceEntry(source, surface, requester) { Charged = bytes };
            if (clamped)
            {
                LogClamped(key, size, width, height);
            }
            return surface;
        }
        long resized = GlSurface.BytesFor(width, height, entry.Surface.DoubleBuffered);
        if (resized > entry.Charged)
        {
            server.ChargeMemory(entry.ChargedTo, resized - entry.Charged);
        }
        else if (resized < entry.Charged)
        {
            server.RefundMemory(entry.ChargedTo, entry.Charged - resized);
        }
        entry.Charged = resized;
        bool wasClamped = entry.Surface.Clamped;
        entry.Surface.Resize(width, height, size.Width, size.Height);
        if (clamped && !wasClamped)
        {
            LogClamped(key, size, width, height);
        }
        return entry.Surface;
    }

    private void LogClamped(uint key, (int Width, int Height) size, int width, int height) =>
        server.Log($"GLX: drawable 0x{key:X} is {size.Width}x{size.Height}, over the surface limit; rendering only its lower-left {width}x{height}");

    /// <summary>
    /// 按 ID 找表面。那个 ID 上现在的资源已经不是建表面时的那一个(原来的被释放、ID 又被重用)时作废旧表面 ——
    /// 否则新资源(可能属于另一个客户端)接着用旧的帧缓冲,读到上一个的内容。
    /// </summary>
    private bool TryGetSurface(uint key, [NotNullWhen(true)] out GlSurface? surface)
    {
        surface = TryGetEntry(key, out SurfaceEntry? entry) ? entry.Surface : null;
        return surface is not null;
    }

    private bool TryGetEntry(uint key, [NotNullWhen(true)] out SurfaceEntry? entry)
    {
        if (!_glxSurfaces.TryGetValue(key, out entry))
        {
            return false;
        }
        if (!ReferenceEquals(server.Lookup<XResource>(key), entry.Source))
        {
            DropSurface(key);
            entry = null;
            return false;
        }
        return true;
    }

    /// <summary>丢掉一块表面,退还它的账。</summary>
    private void DropSurface(uint key)
    {
        if (_glxSurfaces.Remove(key, out SurfaceEntry? entry))
        {
            server.RefundMemory(entry.ChargedTo, entry.Charged);
        }
    }

    /// <summary>
    /// 绑定的表面跟上 X 可绘对象的尺寸(窗口可能被改过大小),并交给 GL 上下文。可绘对象在上下文仍是当前时没了(窗口被销毁、
    /// 像素图被释放):渲染命令与查询照常执行、只是画不到任何地方 —— 编码规范没给 Render 与非渲染命令这种情况下的错误;
    /// 原先每个请求都回 GLXBadWindow / GLXBadDrawable,Xlib 默认的错误处理让程序直接退出。
    /// </summary>
    private void SyncGlxBinding(XClient c, GlxBinding binding)
    {
        if (!DrawableAlive(binding.Draw) || !DrawableAlive(binding.Read))
        {
            binding.Context.Gl?.Bind(null, null);
            return;
        }
        (GlSurface? draw, GlSurface? read) = BindingSurfaces(c, binding);
        binding.Context.Gl?.Bind(draw, read);
    }

    /// <summary>绑定时的可绘对象还在不在:GLX 窗口 / 像素图背后的 X 窗口 / 像素图也得还在。</summary>
    private bool DrawableAlive(uint id) => server.Lookup<XResource>(id) switch
    {
        XGlxDrawable { Kind: GlxDrawableKind.Pbuffer } => true,
        XGlxDrawable { Kind: GlxDrawableKind.Window } glxWindow => server.Lookup<XWindow>(glxWindow.Target) is not null,
        XGlxDrawable glxPixmap => server.Lookup<XPixmap>(glxPixmap.Target) is not null,
        XWindow => true,
        _ => false,
    };

    /// <summary>可绘对象是窗口:GLX 1.2 的写法直接拿 X 窗口当可绘对象,或者 GLXWindow。</summary>
    private bool IsWindowDrawable(uint id) => server.Lookup<XResource>(id) is XWindow or XGlxDrawable { Kind: GlxDrawableKind.Window };

    /// <summary>
    /// WaitGL / WaitX / 带标签的 SwapBuffers 与 CopyContext / UseXFont(编码规范 §2.1 的这几个请求列了这个错误):
    /// 当前的可绘对象已经没了时,是窗口回 GLXBadCurrentWindow,像素图之类回 GLXBadCurrentDrawable。
    /// </summary>
    private void CheckCurrentDrawable(GlxBinding binding)
    {
        uint gone = !DrawableAlive(binding.Draw) ? binding.Draw : !DrawableAlive(binding.Read) ? binding.Read : 0;
        if (gone != 0)
        {
            throw GlxError(binding.DrawIsWindow && gone == binding.Draw ? GlxBadCurrentWindow : GlxBadCurrentDrawable, gone);
        }
    }

    /// <summary>
    /// 绑定用到的绘制 / 读取表面(没有就新建,尺寸跟上 X 可绘对象)。表面太大、或客户端的内存账上记不下时抛 BadAlloc ——
    /// MakeCurrent 在改任何状态之前先调它。直接上下文没有服务端的 GL,不要表面。
    /// </summary>
    private (GlSurface? Draw, GlSurface? Read) BindingSurfaces(XClient c, GlxBinding binding)
    {
        if (binding.Context.Gl is null)
        {
            return (null, null);
        }
        GlSurface? draw = null, read = null;
        if (binding.Draw != 0)
        {
            (uint key, (int Width, int Height) size, GlxConfig? config) = ResolveGlxDrawable(binding.Draw, binding.Context.Config);
            draw = SurfaceFor(c, key, size, config ?? binding.Context.Config);
        }
        if (binding.Read != 0)
        {
            (uint key, (int Width, int Height) size, GlxConfig? config) = ResolveGlxDrawable(binding.Read, binding.Context.Config);
            read = SurfaceFor(c, key, size, config ?? binding.Context.Config);
        }
        return (draw, read);
    }

    /// <summary>把绑定的绘制表面画过的前缓冲拷进 X 可绘对象。</summary>
    private void PresentGlx(GlxBinding binding)
    {
        if (binding.Draw != 0 && binding.Context.Gl?.Draw is { FrontDirty: true } surface)
        {
            PresentSurface(surface);
        }
    }

    /// <summary>
    /// 前缓冲上次拷出后画过的那一块 → X 窗口可见部分(并记损伤)或像素图;Pbuffer 不拷。
    /// 单缓冲的程序每个 Render 请求都走这里:只拷画过的外接矩形,不拷整窗。双缓冲交换时整块都算画过,但逐行先与窗口里现有的
    /// 像素比一比(向量化),只写、只记损伤真正不一样的那一段 —— 原先每次交换整窗拷贝、整窗记损伤,宿主跟着整窗重画
    /// (xs_plan GL-P5)。比的是目标缓冲里实际的像素,窗口被别的绘图或曝光改过的地方照样补回来。
    /// 表面的 ID 上现在已是别的资源(原来的可绘对象没了、ID 被重用)时不拷。表面被夹小了时它对着可绘对象左下的那一块。
    /// </summary>
    private void PresentSurface(GlSurface surface)
    {
        XRect dirty = surface.FrontDirtyRect;
        surface.ClearFrontDirty();
        if (!TryGetSurface(surface.Drawable, out GlSurface? current) || !ReferenceEquals(current, surface))
        {
            return;
        }
        // 表面第 0 行(最上面)落在可绘对象的第 dy 行。
        int dy = surface.Clamped ? surface.DrawableHeight - surface.Height : 0;
        dirty = dirty.Offset(0, dy);
        switch (server.Lookup<XResource>(surface.Drawable))
        {
            case XWindow window:
                {
                    if (server.DrawTarget(window.Id, null) is not { } target)
                    {
                        return;
                    }
                    XRect area = dirty.Intersect(new XRect(0, dy, Math.Min(surface.Width, window.Width), Math.Min(surface.Height, window.Height - dy)));
                    if (area.IsEmpty)
                    {
                        return;
                    }
                    // 可见区域是缓存里共享的,先拷一份再裁到画过的范围(缓冲坐标)。
                    Region visible = target.Clip.Clone().Intersect(area.Offset(target.OriginX, target.OriginY));
                    uint mask = target.Buffer.DepthMask;
                    Region changed = new();
                    foreach (XRect rect in visible.Rects)
                    {
                        XRect rectChanged = default;
                        for (int y = rect.Y; y < rect.Bottom; y++)
                        {
                            ReadOnlySpan<uint> from = surface.Front.AsSpan(((y - target.OriginY - dy) * surface.Width) + rect.X - target.OriginX, rect.Width);
                            Span<uint> to = target.Buffer.Pixels.AsSpan((y * target.Buffer.Width) + rect.X, rect.Width);
                            rectChanged = Union(rectChanged, CopyChanged(from, to, mask, rect.X, y));
                        }
                        if (!rectChanged.IsEmpty)
                        {
                            changed = changed.Union(rectChanged);
                        }
                    }
                    if (target.TopLevel is { } top && !changed.IsEmpty)
                    {
                        server.MarkDamage(top, changed);
                    }
                    break;
                }
            case XPixmap pixmap:
                {
                    XRect area = dirty.Intersect(new XRect(0, dy, Math.Min(surface.Width, pixmap.Width), Math.Min(surface.Height, pixmap.Height - dy)));
                    uint mask = pixmap.Buffer.DepthMask;
                    XRect changed = default;
                    for (int y = area.Y; y < area.Bottom; y++)
                    {
                        ReadOnlySpan<uint> from = surface.Front.AsSpan(((y - dy) * surface.Width) + area.X, area.Width);
                        Span<uint> to = pixmap.Buffer.Pixels.AsSpan((y * pixmap.Width) + area.X, area.Width);
                        changed = Union(changed, CopyChanged(from, to, mask, area.X, y));
                    }
                    if (!changed.IsEmpty)
                    {
                        server.NotePixmapDrawn(pixmap, changed);
                    }
                    break;
                }
        }
    }
    /// <summary>
    /// 一行:<paramref name="from" /> 按 <paramref name="mask" /> 截掉多余的位之后与 <paramref name="to" /> 比,只把头一个与最后一个不同的
    /// 像素之间那一段写过去;返回这一段在目标里的矩形(<paramref name="x" />、<paramref name="y" /> 是这一行在目标里的起点),一样时为空。
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]   // 每次交换每行都走:不经过未优化的第 0 层(那里 Vector 的调用不内联,慢几十倍)
    private static XRect CopyChanged(ReadOnlySpan<uint> from, Span<uint> to, uint mask, int x, int y)
    {
        int n = to.Length, start = 0, end = n;
        if (Vector.IsHardwareAccelerated)
        {
            Vector<uint> vmask = new(mask);
            int lanes = Vector<uint>.Count;
            while (start + lanes <= n && Vector.EqualsAll(new Vector<uint>(from[start..]) & vmask, new Vector<uint>(to[start..])))
            {
                start += lanes;
            }
        }
        while (start < n && (from[start] & mask) == to[start])
        {
            start++;
        }
        if (start == n)
        {
            return default;
        }
        if (Vector.IsHardwareAccelerated)
        {
            Vector<uint> vmask = new(mask);
            int lanes = Vector<uint>.Count;
            while (end - lanes >= start && Vector.EqualsAll(new Vector<uint>(from[(end - lanes)..]) & vmask, new Vector<uint>(to[(end - lanes)..])))
            {
                end -= lanes;
            }
        }
        while ((from[end - 1] & mask) == to[end - 1])
        {
            end--;
        }
        for (int i = start; i < end; i++)
        {
            to[i] = from[i] & mask;
        }
        return new XRect(x + start, y, end - start, 1);
    }

    private static XRect Union(XRect a, XRect b)
    {
        if (a.IsEmpty)
        {
            return b;
        }
        if (b.IsEmpty)
        {
            return a;
        }
        int x0 = Math.Min(a.X, b.X), y0 = Math.Min(a.Y, b.Y);
        return new XRect(x0, y0, Math.Max(a.Right, b.Right) - x0, Math.Max(a.Bottom, b.Bottom) - y0);
    }

    // ------------------------------------------------------------------ 当前上下文

    private GlxBinding GlxBindingOf(XClient c, uint tag) =>
        tag != 0 && _glxTags.TryGetValue(c, out Dictionary<uint, GlxBinding>? tags) && tags.TryGetValue(tag, out GlxBinding? binding)
            ? binding
            : throw GlxError(GlxBadContextTag, tag);

    private void GlxMakeCurrent(XClient c, uint oldTag, uint drawable, uint read, uint contextId)
    {
        GlxBinding? old = oldTag != 0 ? GlxBindingOf(c, oldTag) : null;
        if (contextId == 0)
        {
            if (drawable != 0 || read != 0)
            {
                throw new XProtocolError(XErrorCode.Match);
            }
            if (old is not null)
            {
                ReleaseGlxBinding(c, oldTag, old);
            }
            c.Reply(0, w => w.U32(0).Zero(20));
            return;
        }
        XGlxContext context = server.Use<XGlxContext>(contextId) ?? throw GlxError(GlxBadContext, contextId);   // 跨客户端:见类注释
        if (drawable == 0 || read == 0)
        {
            throw new XProtocolError(XErrorCode.Match);
        }
        if (context.Current is { } current && !(ReferenceEquals(current.Client, c) && current.Tag == oldTag))
        {
            throw new XProtocolError(XErrorCode.Access);   // 已是别的线程 / 客户端的当前上下文
        }
        // 校验两个可绘对象与上下文相容(同一视觉 / 深度)。
        foreach (uint id in new[] { drawable, read })
        {
            (_, _, GlxConfig? config) = ResolveGlxDrawable(id, context.Config);
            if (config is null || config.Visual != context.Config.Visual)
            {
                throw new XProtocolError(XErrorCode.Match);
            }
        }
        // 表面先备好(太大时 BadAlloc):出错时请求不能留下任何效果(协议第 4 节)—— 原先先登记了新标签、把上下文挂上去才分配,
        // 抛出去之后上下文卡在一个客户端不知道的标签上,之后谁也 MakeCurrent 不了它。
        GlxBinding binding = new(context, drawable, read, IsWindowDrawable(drawable));
        (GlSurface? drawSurface, GlSurface? readSurface) = BindingSurfaces(c, binding);
        if (old is not null)
        {
            ReleaseGlxBinding(c, oldTag, old);
        }
        uint tag = ++_nextGlxTag;
        if (tag == 0)
        {
            tag = ++_nextGlxTag;
        }
        if (!_glxTags.TryGetValue(c, out Dictionary<uint, GlxBinding>? tags))
        {
            tags = [];
            _glxTags[c] = tags;
        }
        tags[tag] = binding;
        context.Current = (c, tag);
        context.Gl?.Bind(drawSurface, readSurface);
        c.Reply(0, w => w.U32(tag).Zero(20));
    }

    private void ReleaseGlxBinding(XClient c, uint tag, GlxBinding binding)
    {
        PresentGlx(binding);
        Unbind(binding.Context);
        if (_glxTags.TryGetValue(c, out Dictionary<uint, GlxBinding>? tags))
        {
            tags.Remove(tag);
        }
    }

    /// <summary>上下文不再是当前;它的资源已经释放了(DestroyContext 时还是当前的)的话,这时才真正释放它的 GL 对象并销账。</summary>
    private void Unbind(XGlxContext context)
    {
        context.Gl?.Bind(null, null);
        context.Current = null;
        if (!ReferenceEquals(server.Lookup<XGlxContext>(context.Id), context))
        {
            context.Gl?.Release();
        }
    }

    /// <summary>
    /// 资源离开了资源表(DestroyContext、DestroyPbuffer、客户端断开):不是当前的间接上下文当场释放 GL 对象、销账,
    /// 还是当前的等它不再是当前(见 <see cref="Unbind" />)—— 原先上下文连同共享组的列表与纹理靠垃圾回收,账上永远记着(xs_plan GL-S3);
    /// Pbuffer 的表面一并丢掉。
    /// </summary>
    public void ResourceFreed(XResource resource)
    {
        switch (resource)
        {
            case XGlxContext { Gl: { } gl, Current: null }:
                gl.Release();
                break;
            case XGlxDrawable { Kind: GlxDrawableKind.Pbuffer } pbuffer when _glxSurfaces.TryGetValue(pbuffer.Id, out SurfaceEntry? entry)
                                                                            && ReferenceEquals(entry.Source, pbuffer):
                DropSurface(pbuffer.Id);
                break;
            case XGlxDrawable { Kind: GlxDrawableKind.Window } glxWindow
                when _glxWindows.TryGetValue(glxWindow.Target, out XGlxDrawable? registered) && ReferenceEquals(registered, glxWindow):
                _glxWindows.Remove(glxWindow.Target);
                break;
        }
    }

    /// <summary>
    /// 客户端断开:它的标签作废,上下文不再是当前;拼到一半的 RenderLarge 丢掉;它的 Pbuffer、像素图、窗口上的表面,
    /// 以及记在它名下的表面释放。
    /// </summary>
    public void CleanupClient(XClient client)
    {
        DropLarge(client);
        foreach ((uint key, SurfaceEntry entry) in _glxSurfaces.ToArray())
        {
            if (ReferenceEquals(entry.Source.Owner, client) || ReferenceEquals(entry.ChargedTo, client))
            {
                DropSurface(key);   // 一块 4096² 的表面连深度、模板是两百多 MB,不能等 ID 被重用才回收
            }
        }
        if (_glxTags.Remove(client, out Dictionary<uint, GlxBinding>? tags))
        {
            foreach (GlxBinding binding in tags.Values)
            {
                Unbind(binding.Context);
            }
        }
    }

    /// <summary>窗口销毁:它的表面随之丢掉。</summary>
    public void CleanupWindow(XWindow window)
    {
        if (_glxSurfaces.TryGetValue(window.Id, out SurfaceEntry? entry) && ReferenceEquals(entry.Source, window))
        {
            DropSurface(window.Id);
        }
    }

    /// <summary>
    /// 像素图的 ID 释放了:建在它上面的表面(GLX 像素图画进的那份帧缓冲,连同表面项抓着的像素图本身)随之丢掉。
    /// 原先只在 ID 被重用、客户端断开时才回收 —— 每帧新建像素图画一张缩略图的程序,每轮漏一份表面加一份像素缓冲。
    /// 还当前着的上下文下一次渲染时找不到可绘对象,与 ID 被重用时一样回 GLXBadDrawable。
    /// </summary>
    public void CleanupPixmap(XPixmap pixmap)
    {
        if (_glxSurfaces.TryGetValue(pixmap.Id, out SurfaceEntry? entry) && ReferenceEquals(entry.Source, pixmap))
        {
            DropSurface(pixmap.Id);
        }
    }

    // ------------------------------------------------------------------ 渲染请求

    private (GlxBinding Binding, GlContext Gl) GlxRenderTarget(XClient c, uint tag)
    {
        GlxBinding binding = GlxBindingOf(c, tag);
        GlContext gl = binding.Context.Gl ?? throw GlxError(GlxBadContextState, tag);
        gl.ResetBudget();
        SyncGlxBinding(c, binding);
        return (binding, gl);
    }

    private const byte GlxBadContextState = X11Server.GlxErrorBase + 1;

    private void GlxRender(XClient c, XRequestReader r)
    {
        uint tag = r.U32();
        (GlxBinding binding, GlContext gl) = GlxRenderTarget(c, tag);
        ReadOnlySpan<byte> commands = r.Rest();
        // 先整条校验:长度要首尾相接,操作码要认识(GLXBadRenderRequest 带「出错之前的命令数」)。
        int pos = 0, index = 0;
        bool big = c.BigEndian;
        while (pos < commands.Length)
        {
            if (pos + 4 > commands.Length)
            {
                throw new XProtocolError(XErrorCode.Length);
            }
            int length = big ? BinaryPrimitives.ReadUInt16BigEndian(commands[pos..]) : BinaryPrimitives.ReadUInt16LittleEndian(commands[pos..]);
            int opcode = big ? BinaryPrimitives.ReadUInt16BigEndian(commands[(pos + 2)..]) : BinaryPrimitives.ReadUInt16LittleEndian(commands[(pos + 2)..]);
            if (length < 4 || pos + length > commands.Length)
            {
                throw new XProtocolError(XErrorCode.Length);
            }
            if (!GlContext.IsKnownRenderOpcode(opcode))
            {
                throw GlxError(GlxBadRenderRequest, (uint)index);
            }
            pos += (length + 3) & ~3;
            index++;
        }
        gl.ExecuteStream(commands, big);
        ReportUnimplemented(c, gl);
        PresentGlx(binding);
    }

    private void GlxRenderLarge(XClient c, XRequestReader r)
    {
        uint tag = r.U32();
        int number = r.U16(), total = r.U16();
        int n = (int)r.U32();
        (GlxBinding binding, GlContext gl) = GlxRenderTarget(c, tag);
        if (number == 1)
        {
            DropLarge(c);
            uint length = r.U32();
            int opcode = (int)r.U32();
            // n 是小参数的字节数;有的客户端把 8 字节的长度与操作码也算在内 —— 按请求里实际剩下的字节判断。
            int small = XWire.Pad(n) == r.Remaining ? n : n - 8;
            if (small < 0 || small > r.Remaining || total < 1 || length < 8 || !GlContext.IsKnownRenderOpcode(opcode))
            {
                throw GlxError(GlxBadLargeRequest, (uint)number);
            }
            if (length - 8 > MaxLargeCommandBytes)
            {
                throw new XProtocolError(XErrorCode.Alloc);
            }
            // 声明的长度含 8 字节的头(长度与操作码):正文就是 length − 8 字节,之后各段拼起来得正好这么多(最多再补齐 3 字节)。
            int bodyLength = (int)length - 8;
            if (small > bodyLength + 3)
            {
                throw GlxError(GlxBadLargeRequest, (uint)number);
            }
            ReadOnlySpan<byte> first = r.Rest()[..small];
            if (total == 1)
            {
                // 一段就完:直接在请求的缓冲上执行,不复制。
                if (small < bodyLength)
                {
                    throw GlxError(GlxBadLargeRequest, (uint)number);   // 命令被截断了
                }
                gl.ExecuteOrCompile(opcode, first[..bodyLength], c.BigEndian);
                ReportUnimplemented(c, gl);
                PresentGlx(binding);
                return;
            }
            // 拼正文的缓冲按声明的长度一次分配、记在客户端的内存账上,各段直接拷进来、拼完原地执行 —— 原先 List<byte> 逐段
            // AddRange(每段先复制一份,容量翻倍还要再复制)、拼完 GetRange 再 ToArray,64 MB 的命令峰值约 256 MB。
            server.ChargeMemory(c, bodyLength + 3L);
            GlxLargeCommand large = new(tag, total, opcode, bodyLength);
            first.CopyTo(large.Buffer);
            large.Filled = small;
            _glxLarge[c] = large;
            return;
        }
        if (!_glxLarge.TryGetValue(c, out GlxLargeCommand? pending) || pending.Tag != tag || pending.Next != number
            || pending.Total != total || n < 0 || n > r.Remaining)
        {
            DropLarge(c);
            throw GlxError(GlxBadLargeRequest, (uint)number);
        }
        if (pending.Filled + (long)n > pending.Length + 3)
        {
            DropLarge(c);
            throw GlxError(GlxBadLargeRequest, (uint)number);   // 拼起来比第一段声明的长度还长
        }
        r.Rest()[..n].CopyTo(pending.Buffer.AsSpan(pending.Filled));
        pending.Filled += n;
        pending.Next++;
        if (number == total)
        {
            DropLarge(c);
            if (pending.Filled < pending.Length)
            {
                throw GlxError(GlxBadLargeRequest, (uint)number);   // 比声明的短:命令被截断了
            }
            gl.ExecuteOrCompile(pending.Opcode, pending.Buffer.AsSpan(0, pending.Length), c.BigEndian);
            ReportUnimplemented(c, gl);
            PresentGlx(binding);
        }
    }

    /// <summary>丢掉这个客户端拼到一半的 RenderLarge(拼完、出错、重新开始、断开),缓冲的账退还。</summary>
    private void DropLarge(XClient c)
    {
        if (_glxLarge.Remove(c, out GlxLargeCommand? large))
        {
            server.RefundMemory(c, large.Buffer.Length);
        }
    }

    /// <summary>
    /// 程序第一次用到软件 GL 没实现的功能(选择 / 反馈模式、求值器)时记一行日志,每个上下文每样一次 ——
    /// 结果落空(拾取没有命中、曲面不画)而 GL 本身不报错,原先无迹可查。
    /// </summary>
    private void ReportUnimplemented(XClient c, GlContext gl)
    {
        if (gl.TakeUnreportedFeatures() is not GlUnimplementedFeatures.None and var features)
        {
            server.Log($"GLX: {c} uses {features}, which the indirect renderer does not implement (no hits are reported, nothing is drawn)");
        }
    }

    /// <summary>一条 RenderLarge 命令的正文上限(第一段声明的长度减去 8 字节头)。</summary>
    private const int MaxLargeCommandBytes = 64 * 1024 * 1024;

    // ------------------------------------------------------------------ 非渲染命令(101–159)

    private void GlxSingle(XClient c, byte minor, XRequestReader r)
    {
        uint tag = r.U32();
        (GlxBinding binding, GlContext gl) = GlxRenderTarget(c, tag);
        switch (minor)
        {
            case 101:   // NewList
                gl.NewList(r.U32(), r.U32());
                break;
            case 102:   // EndList
                gl.EndList();
                break;
            case 103:   // DeleteLists
                gl.DeleteLists(r.U32(), r.I32());
                break;
            case 104:   // GenLists
                {
                    uint first = gl.GenLists(r.I32());
                    c.Reply(0, w => w.U32(first).Zero(20));
                    break;
                }
            case 105:   // FeedbackBuffer:反馈模式不实现(RenderMode 回 0 条)
                break;
            case 106:   // SelectBuffer:选择数组在服务端,数据在下一次 RenderMode 的回复里
                gl.SelectBuffer(r.I32());
                break;
            case 107:   // RenderMode
                {
                    uint previous = gl.RenderModeValue;
                    uint mode = r.U32();
                    int result = gl.RenderMode(mode, out uint[] data);
                    ReportUnimplemented(c, gl);
                    // GLX 协议规范 1.3 §2.2.1「RenderMode」:之前在反馈 / 选择模式才有回复(返回值、n、新模式,再跟 n 个 CARD32 的选择数据 /
                    // FLOAT32 的反馈数据);「之前在渲染模式时没有回复」。反馈不实现,n 为 0。
                    if (previous != GlEnum.RENDER)
                    {
                        uint current = gl.RenderModeValue;
                        c.Reply(0, w =>
                        {
                            w.I32(result).U32((uint)data.Length).U32(current).Zero(12);
                            foreach (uint value in data)
                            {
                                w.U32(value);
                            }
                        });
                    }
                    break;
                }
            case 108:   // Finish
                PresentGlx(binding);
                c.Reply(0, w => w.Zero(24));
                break;
            case 109:   // PixelStoref
            case 110:   // PixelStorei:打包参数由客户端库处理(回复里的像素按附录 A.3 的固定布局)
                break;
            case 111:   // ReadPixels
                {
                    int x = r.I32(), y = r.I32(), width = r.I32(), height = r.I32();
                    uint format = r.U32(), type = r.U32();
                    bool swap = r.Bool();
                    r.Bool();   // lsb first:只对 BITMAP 有意义
                    // 回复的大小先算出来再分配:6400 万像素 × 4 个 float 就是 1 GB,远超一个客户端的输出队列上限。
                    if (GlContext.PackedSize(width, height, format, type) > MaxPixelReplyBytes)
                    {
                        throw new XProtocolError(XErrorCode.Alloc);
                    }
                    byte[] pixels = gl.ReadPixels(x, y, width, height, format, type, swap, c.BigEndian) ?? [];
                    c.Reply(0, w => w.Zero(24).Bytes(pixels));
                    break;
                }
            case 112:   // GetBooleanv
            case 114:   // GetDoublev
            case 116:   // GetFloatv
            case 117:   // GetIntegerv
                ReplyGlValues(c, minor, gl.Query(r.U32()));
                break;
            case 113:   // GetClipPlane:四个 FLOAT64
                {
                    GlContext.GlValue? value = gl.GetClipPlane(r.U32());
                    c.Reply(0, w =>
                    {
                        w.Zero(24);
                        foreach (double d in value?.Values ?? [])
                        {
                            w.U64(BitConverter.DoubleToUInt64Bits(d));
                        }
                    });
                    break;
                }
            case 115:   // GetError
                {
                    uint error = gl.GetError();
                    c.Reply(0, w => w.U32(error).Zero(20));
                    break;
                }
            case 118:   // GetLightfv
            case 119:   // GetLightiv
                ReplyGlValues(c, minor == 118 ? (byte)116 : (byte)117, gl.GetLight(r.U32(), r.U32()));
                break;
            case 120:   // GetMapdv
            case 121:   // GetMapfv
            case 122:   // GetMapiv
                ReplyGlValues(c, minor == 120 ? (byte)114 : minor == 121 ? (byte)116 : (byte)117, gl.GetMap(r.U32(), r.U32()));
                break;
            case 123:   // GetMaterialfv
            case 124:   // GetMaterialiv
                ReplyGlValues(c, minor == 123 ? (byte)116 : (byte)117, gl.GetMaterial(r.U32(), r.U32()));
                break;
            case 125:   // GetPixelMapfv
            case 126:   // GetPixelMapuiv:像素映射不实现,各表只有初值的一项 0
                r.U32();
                ReplyGlValues(c, minor == 125 ? (byte)116 : (byte)117, new GlContext.GlValue([0]));
                break;
            case 127:   // GetPixelMapusv
                r.U32();
                c.Reply(0, w => w.U32(0).U32(1).U16(0).Zero(14));
                break;
            case 128:   // GetPolygonStipple:32 行 × 4 字节,按请求的 lsbfirst 排位
                {
                    byte[] stipple = gl.PolygonStippleBytes(r.Bool());
                    c.Reply(0, w => w.Zero(24).Bytes(stipple));
                    break;
                }
            case 129:   // GetString
                {
                    string? value = GlContext.GetString(r.U32());
                    if (value is null)
                    {
                        gl.SetError(GlEnum.INVALID_ENUM);
                    }
                    byte[] bytes = value is null ? [] : [.. XWire.Latin1.GetBytes(value), 0];
                    c.Reply(0, w => w.U32(0).U32((uint)bytes.Length).Zero(16).Bytes(bytes));
                    break;
                }
            case 130:   // GetTexEnvfv
            case 131:   // GetTexEnviv
                ReplyGlValues(c, minor == 130 ? (byte)116 : (byte)117, gl.GetTexEnv(r.U32(), r.U32()));
                break;
            case 132:   // GetTexGendv
            case 133:   // GetTexGenfv
            case 134:   // GetTexGeniv
                ReplyGlValues(c, minor == 132 ? (byte)114 : minor == 133 ? (byte)116 : (byte)117, gl.GetTexGen(r.U32(), r.U32()));
                break;
            case 135:   // GetTexImage
                {
                    uint target = r.U32();
                    int level = r.I32();
                    uint format = r.U32(), type = r.U32();
                    bool swap = r.Bool();
                    // 同 ReadPixels:回复的大小先算出来再打包。2048² 的 RGBA 按 FLOAT 取是 64 MB,正好顶到输出积压上限、客户端被断开。
                    if (gl.TexLevelSize(target, level) is { } size && GlContext.PackedSize(size.Width, size.Height, format, type) > MaxPixelReplyBytes)
                    {
                        throw new XProtocolError(XErrorCode.Alloc);
                    }
                    byte[] pixels = gl.GetTexImage(target, level, format, type, swap, c.BigEndian, out int width, out int height) ?? [];
                    c.Reply(0, w => w.Zero(8).I32(width).I32(height).I32(1).Zero(4).Bytes(pixels));
                    break;
                }
            case 136:   // GetTexParameterfv
            case 137:   // GetTexParameteriv
                ReplyGlValues(c, minor == 136 ? (byte)116 : (byte)117, gl.GetTexParameter(r.U32(), r.U32()));
                break;
            case 138:   // GetTexLevelParameterfv
            case 139:   // GetTexLevelParameteriv
                {
                    uint target = r.U32();
                    int level = r.I32();
                    ReplyGlValues(c, minor == 138 ? (byte)116 : (byte)117, gl.GetTexLevelParameter(target, level, r.U32()));
                    break;
                }
            case 140:   // IsEnabled
            case 141:   // IsList
            case 146:   // IsTexture
                {
                    uint arg = r.U32();
                    bool result = minor switch
                    {
                        140 => gl.IsEnabled(arg),
                        141 => gl.IsList(arg),
                        _ => gl.IsTexture(arg),
                    };
                    c.Reply(0, w => w.U32(result ? 1u : 0).Zero(20));
                    break;
                }
            case 142:   // Flush
                PresentGlx(binding);
                break;
            case 143:   // AreTexturesResident:都常驻
                {
                    int n = Math.Max(0, r.I32());
                    uint[] names = new uint[Math.Min(n, r.Remaining / 4)];
                    for (int i = 0; i < names.Length; i++)
                    {
                        names[i] = r.U32();
                    }
                    byte[] resident = [.. names.Select(name => gl.IsTexture(name) ? (byte)1 : (byte)0)];
                    bool all = resident.All(b => b != 0);
                    c.Reply(0, w => w.U32(all ? 1u : 0).Zero(20).Bytes(resident));
                    break;
                }
            case 144:   // DeleteTextures
                {
                    int n = Math.Max(0, r.I32());
                    uint[] names = new uint[Math.Min(n, r.Remaining / 4)];
                    for (int i = 0; i < names.Length; i++)
                    {
                        names[i] = r.U32();
                    }
                    gl.DeleteTextures(names);
                    break;
                }
            case 145:   // GenTextures
                {
                    int n = r.I32();
                    if (n > 65536)
                    {
                        throw new XProtocolError(XErrorCode.Alloc);
                    }
                    uint[] names = gl.GenTextures(n) ?? throw new XProtocolError(XErrorCode.Alloc);
                    c.Reply(0, w =>
                    {
                        w.Zero(24);
                        foreach (uint name in names)
                        {
                            w.U32(name);
                        }
                    });
                    break;
                }
            default:
                // 颜色表、卷积、直方图、最值(147–159)这些 ARB_imaging 查询不实现:记 INVALID_ENUM,回 n = 0。
                gl.SetError(GlEnum.INVALID_ENUM);
                c.Reply(0, w => w.Zero(24));
                break;
        }
    }

    /// <summary>
    /// Get*v 的回复(§2.2.1):第 12 字节是个数 n;n = 1 时值在第 16 字节,否则从第 32 字节起。
    /// <paramref name="kind" /> 用请求的操作码表示类型:112 布尔、114 双精度、116 单精度、117 整数。
    /// </summary>
    private static void ReplyGlValues(XClient c, byte kind, GlContext.GlValue? value)
    {
        double[] values = value?.Values ?? [];
        bool normalized = value?.Normalized ?? false;
        int n = values.Length;
        c.Reply(0, w =>
        {
            w.Zero(4).U32((uint)n);
            if (n == 1)
            {
                WriteGlValue(w, kind, values[0], normalized);
                w.Zero(kind == 112 ? 15 : kind == 114 ? 8 : 12);
                return;
            }
            w.Zero(16);
            foreach (double v in values)
            {
                WriteGlValue(w, kind, v, normalized);
            }
        });
    }

    private static void WriteGlValue(XWriter w, byte kind, double v, bool normalized)
    {
        switch (kind)
        {
            case 112:
                w.Bool(v != 0);
                break;
            case 114:
                w.U64(BitConverter.DoubleToUInt64Bits(v));
                break;
            case 116:
                w.U32(BitConverter.SingleToUInt32Bits((float)v));
                break;
            default:
                // §6.1.2:颜色一类按 Table 4.6 的 INT 一栏线性映射,其余四舍五入(超出范围时取最近的可表示值)。
                double i = normalized ? ((4294967295.0 * Math.Clamp(v, -1, 1)) - 1) / 2 : Math.Round(v);
                w.I32((int)Math.Clamp(i, int.MinValue, int.MaxValue));
                break;
        }
    }

    // ------------------------------------------------------------------ 配置与属性回复

    private static void ReplyGlxString(XClient c, string value)
    {
        byte[] bytes = [.. XWire.Latin1.GetBytes(value), 0];
        c.Reply(0, w => w.U32(0).U32((uint)bytes.Length).Zero(16).Bytes(bytes));
    }

    private static List<(uint Attribute, uint Value)> FbConfigAttributes(GlxConfig cfg) =>
    [
        (GLX_FBCONFIG_ID, cfg.Id),
        (GLX_VISUAL_ID, cfg.Visual),
        (GLX_X_RENDERABLE, 1),
        (GLX_X_VISUAL_TYPE, GLX_TRUE_COLOR),
        (GLX_RENDER_TYPE, GLX_RGBA_BIT),
        (GLX_DRAWABLE_TYPE, GLX_WINDOW_BIT | GLX_PIXMAP_BIT | GLX_PBUFFER_BIT),
        (GLX_USE_GL, 1),
        (GLX_RGBA, 1),
        (GLX_BUFFER_SIZE, cfg.Depth),
        (GLX_LEVEL, 0),
        (GLX_DOUBLEBUFFER, cfg.DoubleBuffer ? 1u : 0),
        (GLX_STEREO, 0),
        (GLX_AUX_BUFFERS, 0),
        (GLX_RED_SIZE, 8),
        (GLX_GREEN_SIZE, 8),
        (GLX_BLUE_SIZE, 8),
        (GLX_ALPHA_SIZE, cfg.Alpha ? 8u : 0),
        (GLX_DEPTH_SIZE, 24),
        (GLX_STENCIL_SIZE, 8),
        (GLX_ACCUM_RED_SIZE, 0),
        (GLX_ACCUM_GREEN_SIZE, 0),
        (GLX_ACCUM_BLUE_SIZE, 0),
        (GLX_ACCUM_ALPHA_SIZE, 0),
        (GLX_CONFIG_CAVEAT, GLX_NONE),
        (GLX_TRANSPARENT_TYPE, GLX_NONE),
        (GLX_TRANSPARENT_INDEX_VALUE, 0),
        (GLX_TRANSPARENT_RED_VALUE, 0),
        (GLX_TRANSPARENT_GREEN_VALUE, 0),
        (GLX_TRANSPARENT_BLUE_VALUE, 0),
        (GLX_TRANSPARENT_ALPHA_VALUE, 0),
        (GLX_MAX_PBUFFER_WIDTH, MaxPbufferSize),
        (GLX_MAX_PBUFFER_HEIGHT, MaxPbufferSize),
        (GLX_MAX_PBUFFER_PIXELS, MaxPbufferSize * MaxPbufferSize),
        (GLX_SAMPLE_BUFFERS, cfg.Samples > 0 ? 1u : 0),
        (GLX_SAMPLES, cfg.Samples),
    ];

    private static void ReplyFbConfigs(XClient c)
    {
        int properties = FbConfigAttributes(GlxConfigs[0]).Count;
        c.Reply(0, w =>
        {
            w.U32((uint)GlxConfigs.Length).U32((uint)properties).Zero(16);
            foreach (GlxConfig cfg in GlxConfigs)
            {
                foreach ((uint attribute, uint value) in FbConfigAttributes(cfg))
                {
                    w.U32(attribute).U32(value);
                }
            }
        });
    }

    /// <summary>GetVisualConfigs:18 个有序属性,再跟属性对(视觉等级、透明类型、多重采样、对应的 FBConfig)。</summary>
    private static void ReplyVisualConfigs(XClient c)
    {
        static (uint, uint)[] Extra(GlxConfig cfg) =>
        [
            (GLX_CONFIG_CAVEAT, GLX_NONE),
            (GLX_TRANSPARENT_TYPE, GLX_NONE),
            (GLX_SAMPLE_BUFFERS, cfg.Samples > 0 ? 1u : 0),
            (GLX_SAMPLES, cfg.Samples),
            (GLX_FBCONFIG_ID, cfg.Id),
        ];
        int properties = 18 + (2 * Extra(GlxVisualConfigs[0]).Length);
        c.Reply(0, w =>
        {
            w.U32((uint)GlxVisualConfigs.Length).U32((uint)properties).Zero(16);
            foreach (GlxConfig cfg in GlxVisualConfigs)
            {
                w.U32(cfg.Visual).U32(4).U32(1)                                   // visual、class(TrueColor)、rgba
                    .U32(8).U32(8).U32(8).U32(cfg.Alpha ? 8u : 0)                 // 红绿蓝 alpha
                    .U32(0).U32(0).U32(0).U32(0)                                   // 累积缓冲
                    .U32(cfg.DoubleBuffer ? 1u : 0).U32(0)                         // 双缓冲、立体
                    .U32(cfg.Depth).U32(24).U32(8).U32(0).U32(0);                  // buffer size、深度、模板、辅助缓冲、level
                foreach ((uint attribute, uint value) in Extra(cfg))
                {
                    w.U32(attribute).U32(value);
                }
            }
        });
    }

    private static void ReplyAttributes(XClient c, List<(uint Attribute, uint Value)> attributes) =>
        c.Reply(0, w =>
        {
            w.U32((uint)attributes.Count).Zero(20);
            foreach ((uint attribute, uint value) in attributes)
            {
                w.U32(attribute).U32(value);
            }
        });

    private void ReplyDrawableAttributes(XClient c, uint id)
    {
        List<(uint, uint)> attributes;
        switch (server.Use<XResource>(id))
        {
            case XGlxDrawable { Kind: GlxDrawableKind.Pbuffer } p:
                attributes =
                [
                    (GLX_WIDTH, (uint)p.PbufferWidth), (GLX_HEIGHT, (uint)p.PbufferHeight), (GLX_FBCONFIG_ID, p.Config.Id),
                    (GLX_EVENT_MASK, p.EventMask), (GLX_PRESERVED_CONTENTS, p.PreservedContents ? 1u : 0),
                    (GLX_LARGEST_PBUFFER, p.LargestPbuffer ? 1u : 0), (GLX_SCREEN, 0),
                ];
                break;
            case XGlxDrawable d:
                {
                    (_, (int Width, int Height) size, _) = ResolveGlxDrawable(id, null);
                    attributes = [(GLX_WIDTH, (uint)size.Width), (GLX_HEIGHT, (uint)size.Height), (GLX_FBCONFIG_ID, d.Config.Id), (GLX_EVENT_MASK, d.EventMask),
                        (GLX_SCREEN, 0)];
                    break;
                }
            case XWindow window:
                {
                    GlxConfig? config = GlxVisualConfigs.FirstOrDefault(cfg => cfg.Visual == window.Visual);
                    attributes = [(GLX_WIDTH, (uint)window.Width), (GLX_HEIGHT, (uint)window.Height), (GLX_FBCONFIG_ID, config?.Id ?? 0), (GLX_EVENT_MASK, 0),
                        (GLX_SCREEN, 0)];
                    break;
                }
            default:
                throw GlxError(GlxBadDrawable, id);
        }
        ReplyAttributes(c, attributes);
    }

    // ------------------------------------------------------------------ UseXFont

    /// <summary>
    /// UseXFont(§2.1):从 first 开始的 count 个字形各生成一个只含一条 Bitmap 命令的显示列表;
    /// xorig = −lbearing、yorig = descent − 1、宽 = rbearing − lbearing、高 = ascent + descent、xmove = 字宽、ymove = 0。
    /// 字体里没有的字形生成空列表。
    /// </summary>
    private void GlxUseXFont(XClient c, XRequestReader r)
    {
        uint tag = r.U32(), fontId = r.U32(), first = r.U32(), count = r.U32(), listBase = r.U32();
        (GlxBinding binding, GlContext gl) = GlxRenderTarget(c, tag);
        CheckCurrentDrawable(binding);
        if (gl.IsCompiling)
        {
            throw GlxError(GlxBadContextState, tag);
        }
        Fonts.XFont font = server.Use<XFontResource>(fontId)?.Font ?? throw new XProtocolError(XErrorCode.Font, fontId);
        if (count > 65536)
        {
            throw new XProtocolError(XErrorCode.Value, count);
        }
        for (uint i = 0; i < count; i++)
        {
            List<GlCommand> list = [];
            if (font.Lookup((int)(first + i)) is { } glyph)
            {
                int width = glyph.BitmapWidth, height = glyph.BitmapHeight;
                int rowBytes = (width + 7) / 8;
                XWriter body = new(bigEndian: false, 48 + (rowBytes * height));
                body.U8(0).U8(0).U16(0).U32(0).U32(0).U32(0).U32(1)   // unused、lsb first = False、row length、skip、alignment 1
                    .I32(width).I32(height)
                    .U32(BitConverter.SingleToUInt32Bits(-glyph.Info.LeftBearing))
                    .U32(BitConverter.SingleToUInt32Bits(glyph.Info.Descent - 1))
                    .U32(BitConverter.SingleToUInt32Bits(glyph.Info.Width))
                    .U32(0);
                byte[] bits = new byte[rowBytes * height];
                for (int y = 0; y < height; y++)
                {
                    int row = height - 1 - y;   // GL 的位图第一行在最下面
                    for (int x = 0; x < width; x++)
                    {
                        if (glyph.IsSet(x, y))
                        {
                            bits[(row * rowBytes) + (x / 8)] |= (byte)(0x80 >> (x % 8));
                        }
                    }
                }
                body.Bytes(bits);
                list.Add(new GlCommand(5, body.ToArray(), BigEndian: false));
            }
            if (!gl.Shared.TrySetList(listBase + i, list))
            {
                throw new XProtocolError(XErrorCode.Alloc);   // 显示列表的账(组的上限与客户端的内存账)不能被这条请求绕过
            }
        }
        PresentGlx(binding);
    }
}

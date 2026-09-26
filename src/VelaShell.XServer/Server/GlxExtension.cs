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
//   枚举值对照 Khronos GLX API Registry(glx.xml)。
//
//   间接上下文由 Gl/GlContext 执行;直接上下文(is direct = True,比如 Mesa 在客户端用软件渲染、再经 PutImage 送像素)
//   服务端只做登记。前缓冲在请求处理完后拷进 X 窗口 / 像素图;GLX 窗口与直接当 GLX 可绘对象用的窗口共用一套缓冲。
//   GetString / QueryServerString 的串带上结尾的 NUL(STRING8 长度算在内):客户端库按 C 串使用。

using System.Buffers.Binary;
using VelaShell.XServer.Gl;
using VelaShell.XServer.Protocol;
using VelaShell.XServer.Resources;
using VelaShell.XServer.Windowing;

namespace VelaShell.XServer.Server;

/// <summary>
/// GLX 扩展:自己的状态(上下文标签、帧缓冲表面、拼到一半的 RenderLarge)与全部请求处理。自成一体 ——
/// 只经服务端少数 internal 成员碰资源表、绘图目标与损伤。只在执行线程上用。
/// </summary>
internal sealed class GlxExtension(X11Server server)
{
    private const byte GlxBadContext = X11Server.GlxErrorBase + 0;
    private const byte GlxBadDrawable = X11Server.GlxErrorBase + 2;
    private const byte GlxBadPixmap = X11Server.GlxErrorBase + 3;
    private const byte GlxBadContextTag = X11Server.GlxErrorBase + 4;
    private const byte GlxBadRenderRequest = X11Server.GlxErrorBase + 6;
    private const byte GlxBadLargeRequest = X11Server.GlxErrorBase + 7;
    private const byte GlxUnsupportedPrivateRequest = X11Server.GlxErrorBase + 8;
    private const byte GlxBadFBConfig = X11Server.GlxErrorBase + 9;
    private const byte GlxBadPbuffer = X11Server.GlxErrorBase + 10;
    private const byte GlxBadWindow = X11Server.GlxErrorBase + 12;

    // GLX 枚举(glx.xml)
    private const uint GLX_VENDOR = 1, GLX_VERSION = 2, GLX_EXTENSIONS = 3;
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
        GLX_PBUFFER_CLOBBER_MASK = 0x08000000;

    private const int MaxPbufferSize = 4096;

    /// <summary>一个 GLX 表面的像素数上限(4096 × 4096):超大窗口不一次分配几个 GB。</summary>
    private const long MaxGlxSurfacePixels = 4096L * 4096;

    /// <summary>GLX 可绘对象的种类。</summary>
    internal enum GlxDrawableKind
    {
        Window,
        Pixmap,
        Pbuffer,
    }

    /// <summary>一个 FBConfig:对应一个 X 视觉;颜色 8/8/8(ARGB 视觉再加 8 位 alpha),深度 24、模板 8,没有累积缓冲与多重采样。</summary>
    internal sealed record GlxConfig(uint Id, uint Visual, byte Depth, bool DoubleBuffer, bool Alpha);

    private static readonly GlxConfig[] GlxConfigs =
    [
        new(0x101, X11Server.RootVisualId, 24, DoubleBuffer: true, Alpha: false),
        new(0x102, X11Server.RootVisualId, 24, DoubleBuffer: false, Alpha: false),
        new(0x103, X11Server.ArgbVisualId, 32, DoubleBuffer: true, Alpha: true),
        new(0x104, X11Server.ArgbVisualId, 32, DoubleBuffer: false, Alpha: true),
    ];

    /// <summary>GLX 1.2 的视觉配置:每个视觉一条,取它的双缓冲配置。</summary>
    private static readonly GlxConfig[] GlxVisualConfigs = [GlxConfigs[0], GlxConfigs[2]];

    /// <summary>可绘对象的帧缓冲,按 X 窗口 / 像素图 / Pbuffer 的 ID 存(同一个窗口的各种用法共用一份)。</summary>
    private readonly Dictionary<uint, GlSurface> _glxSurfaces = [];

    /// <summary>每个客户端的上下文标签 → 当前绑定。</summary>
    private readonly Dictionary<XClient, Dictionary<uint, GlxBinding>> _glxTags = [];

    /// <summary>每个客户端正在拼的 RenderLarge。</summary>
    private readonly Dictionary<XClient, GlxLargeCommand> _glxLarge = [];

    private uint _nextGlxTag;

    private sealed record GlxBinding(XGlxContext Context, uint Draw, uint Read);

    private sealed class GlxLargeCommand(uint tag, int total, int opcode, int length)
    {
        public uint Tag { get; } = tag;

        public int Total { get; } = total;

        public int Next { get; set; } = 2;

        public int Opcode { get; } = opcode;

        public int Length { get; } = length;

        public List<byte> Data { get; } = [];
    }

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
            case 4:   // DestroyContext
                {
                    uint id = r.U32();
                    _ = server.Lookup<XGlxContext>(id) ?? throw GlxError(GlxBadContext, id);
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
                    XGlxContext ctx = server.Lookup<XGlxContext>(id) ?? throw GlxError(GlxBadContext, id);
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
                    PresentGlx(binding);
                    break;
                }
            case 10:   // CopyContext
                {
                    uint source = r.U32(), dest = r.U32(), mask = r.U32(), tag = r.U32();
                    XGlxContext src = server.Lookup<XGlxContext>(source) ?? throw GlxError(GlxBadContext, source);
                    XGlxContext dst = server.Lookup<XGlxContext>(dest) ?? throw GlxError(GlxBadContext, dest);
                    if (tag != 0 && !ReferenceEquals(GlxBindingOf(c, tag).Context, src))
                    {
                        throw new XProtocolError(XErrorCode.Match);
                    }
                    if (dst.Current is not null)
                    {
                        throw new XProtocolError(XErrorCode.Access);
                    }
                    if (src.Gl is null || dst.Gl is null)
                    {
                        throw new XProtocolError(XErrorCode.Match);   // 直接上下文的状态不在服务端
                    }
                    dst.Gl.State.Restore(src.Gl.State.Clone(), mask);
                    break;
                }
            case 11:   // SwapBuffers
                {
                    uint tag = r.U32(), drawable = r.U32();
                    if (tag != 0)
                    {
                        PresentGlx(GlxBindingOf(c, tag));
                    }
                    (uint key, _, _) = ResolveGlxDrawable(drawable, null);
                    if (_glxSurfaces.TryGetValue(key, out GlSurface? surface) && surface.DoubleBuffered)
                    {
                        surface.Swap();
                        PresentSurface(key, surface);
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
                    if (server.Lookup<XGlxDrawable>(id) is not { Kind: GlxDrawableKind.Pixmap })
                    {
                        throw GlxError(GlxBadPixmap, id);
                    }
                    server.RemoveResource(id);
                    break;
                }
            case 16:   // VendorPrivate
            case 17:   // VendorPrivateWithReply
                throw GlxError(GlxUnsupportedPrivateRequest, r.U32());
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
                        var name => throw new XProtocolError(XErrorCode.Value, name),
                    };
                    ReplyGlxString(c, value);
                    break;
                }
            case 20:   // ClientInfo:客户端的 GL 版本与扩展,只影响 GetString 的协商 —— 这里的串是固定的
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
                    XGlxContext ctx = server.Lookup<XGlxContext>(id) ?? throw GlxError(GlxBadContext, id);
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
                    if (server.Lookup<XGlxDrawable>(id) is not { Kind: GlxDrawableKind.Pbuffer })
                    {
                        throw GlxError(GlxBadPbuffer, id);
                    }
                    server.RemoveResource(id);
                    _glxSurfaces.Remove(id);
                    break;
                }
            case 29:   // GetDrawableAttributes
                ReplyDrawableAttributes(c, r.U32());
                break;
            case 30:   // ChangeDrawableAttributes
                {
                    uint id = r.U32();
                    uint count = r.U32();
                    XGlxDrawable drawable = server.Lookup<XGlxDrawable>(id) ?? throw GlxError(GlxBadDrawable, id);
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
                    XWindow target = server.Lookup<XWindow>(window) ?? throw GlxError(GlxBadWindow, window);
                    if (target.Depth != config.Depth || target.IsInputOnly)
                    {
                        throw new XProtocolError(XErrorCode.Match);
                    }
                    if (server.AllResources.OfType<XGlxDrawable>().Any(d => d.Kind == GlxDrawableKind.Window && d.Target == window))
                    {
                        throw new XProtocolError(XErrorCode.Alloc);   // 一个窗口只能有一个 GLXWindow
                    }
                    server.AddResource(c, new XGlxDrawable(glxWindow, c, GlxDrawableKind.Window, window, config));
                    break;
                }
            case 32:   // DestroyWindow
                {
                    uint id = r.U32();
                    if (server.Lookup<XGlxDrawable>(id) is not { Kind: GlxDrawableKind.Window })
                    {
                        throw GlxError(GlxBadWindow, id);
                    }
                    server.RemoveResource(id);
                    break;
                }
            case >= 101 and <= 159:
                GlxSingle(c, minor, r);
                break;
            default:
                throw new XProtocolError(XErrorCode.Request);
        }
    }

    private const string GlxExtensionsString = "GLX_ARB_get_proc_address GLX_EXT_visual_info GLX_EXT_visual_rating";

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
            share = server.Lookup<XGlxContext>(shareId) ?? throw GlxError(GlxBadContext, shareId);
            if (share.Direct != direct)
            {
                throw new XProtocolError(XErrorCode.Match);   // 直接与间接上下文不在同一个地址空间
            }
        }
        GlContext? gl = direct ? null : new GlContext(config.DoubleBuffer, config.Alpha, share?.Gl?.Shared);
        server.AddResource(c, new XGlxContext(id, c, config, direct, gl));
    }

    private void CreateGlxPixmap(XClient c, uint glxPixmap, uint pixmap, GlxConfig config)
    {
        XPixmap target = server.Lookup<XPixmap>(pixmap) ?? throw new XProtocolError(XErrorCode.Pixmap, pixmap);
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
    /// 那时配置取上下文的(视觉须一致)。
    /// </summary>
    private (uint Key, (int Width, int Height) Size, GlxConfig? Config) ResolveGlxDrawable(uint id, GlxConfig? contextConfig)
    {
        switch (server.Lookup<XResource>(id))
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
                return (window.Id, (window.Width, window.Height), contextConfig ?? GlxConfigs.FirstOrDefault(cfg => cfg.Visual == window.Visual));
            default:
                throw GlxError(GlxBadDrawable, id);
        }
    }

    /// <summary>表面(没有就按配置新建),尺寸跟上 X 可绘对象。</summary>
    private GlSurface SurfaceFor(uint key, (int Width, int Height) size, GlxConfig config)
    {
        if ((long)size.Width * size.Height > MaxGlxSurfacePixels)
        {
            throw new XProtocolError(XErrorCode.Alloc);   // 颜色(前后)、深度、模板一共 13 字节 / 像素
        }
        if (!_glxSurfaces.TryGetValue(key, out GlSurface? surface))
        {
            surface = new GlSurface(size.Width, size.Height, config.DoubleBuffer, config.Alpha);
            _glxSurfaces[key] = surface;
        }
        else
        {
            surface.Resize(size.Width, size.Height);
        }
        return surface;
    }

    /// <summary>绑定的表面跟上 X 可绘对象的尺寸(窗口可能被改过大小),并交给 GL 上下文。</summary>
    private void SyncGlxBinding(GlxBinding binding)
    {
        if (binding.Context.Gl is not { } gl)
        {
            return;
        }
        GlSurface? draw = null, read = null;
        if (binding.Draw != 0)
        {
            (uint key, (int Width, int Height) size, GlxConfig? config) = ResolveGlxDrawable(binding.Draw, binding.Context.Config);
            draw = SurfaceFor(key, size, config ?? binding.Context.Config);
        }
        if (binding.Read != 0)
        {
            (uint key, (int Width, int Height) size, GlxConfig? config) = ResolveGlxDrawable(binding.Read, binding.Context.Config);
            read = SurfaceFor(key, size, config ?? binding.Context.Config);
        }
        gl.Bind(draw, read);
    }

    /// <summary>把绑定的绘制表面画过的前缓冲拷进 X 可绘对象。</summary>
    private void PresentGlx(GlxBinding binding)
    {
        if (binding.Draw == 0 || binding.Context.Gl?.Draw is not { FrontDirty: true } surface)
        {
            return;
        }
        uint key = _glxSurfaces.FirstOrDefault(kv => ReferenceEquals(kv.Value, surface)).Key;
        PresentSurface(key, surface);
    }

    /// <summary>前缓冲 → X 窗口可见部分(并记损伤)或像素图;Pbuffer 不拷。</summary>
    private void PresentSurface(uint key, GlSurface surface)
    {
        surface.FrontDirty = false;
        switch (server.Lookup<XResource>(key))
        {
            case XWindow window:
                {
                    if (server.DrawTarget(window.Id, null) is not { } target)
                    {
                        return;
                    }
                    uint mask = target.Buffer.DepthMask;
                    int w = Math.Min(surface.Width, window.Width), h = Math.Min(surface.Height, window.Height);
                    foreach (XRect rect in target.Clip.Rects)
                    {
                        XRect local = rect.Offset(-target.OriginX, -target.OriginY).Intersect(new XRect(0, 0, w, h));
                        for (int y = local.Y; y < local.Bottom; y++)
                        {
                            int src = (y * surface.Width) + local.X;
                            int dst = ((y + target.OriginY) * target.Buffer.Width) + local.X + target.OriginX;
                            for (int x = 0; x < local.Width; x++)
                            {
                                target.Buffer.Pixels[dst + x] = surface.Front[src + x] & mask;
                            }
                        }
                    }
                    if (target.TopLevel is { } top)
                    {
                        server.MarkDamage(top, target.Clip);
                    }
                    break;
                }
            case XPixmap pixmap:
                {
                    uint mask = pixmap.Buffer.DepthMask;
                    int w = Math.Min(surface.Width, pixmap.Width), h = Math.Min(surface.Height, pixmap.Height);
                    for (int y = 0; y < h; y++)
                    {
                        for (int x = 0; x < w; x++)
                        {
                            pixmap.Buffer.Pixels[(y * pixmap.Width) + x] = surface.Front[(y * surface.Width) + x] & mask;
                        }
                    }
                    server.NotePixmapDrawn(pixmap, new XRect(0, 0, w, h));
                    break;
                }
            case XGlxDrawable { Kind: GlxDrawableKind.Pbuffer }:
                break;
            default:
                _glxSurfaces.Remove(key);   // X 可绘对象已经没了
                break;
        }
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
        XGlxContext context = server.Lookup<XGlxContext>(contextId) ?? throw GlxError(GlxBadContext, contextId);
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
        if (old is not null)
        {
            ReleaseGlxBinding(c, oldTag, old);
        }
        uint tag = ++_nextGlxTag;
        if (tag == 0)
        {
            tag = ++_nextGlxTag;
        }
        GlxBinding binding = new(context, drawable, read);
        if (!_glxTags.TryGetValue(c, out Dictionary<uint, GlxBinding>? tags))
        {
            tags = [];
            _glxTags[c] = tags;
        }
        tags[tag] = binding;
        context.Current = (c, tag);
        SyncGlxBinding(binding);
        c.Reply(0, w => w.U32(tag).Zero(20));
    }

    private void ReleaseGlxBinding(XClient c, uint tag, GlxBinding binding)
    {
        PresentGlx(binding);
        binding.Context.Gl?.Bind(null, null);
        binding.Context.Current = null;
        if (_glxTags.TryGetValue(c, out Dictionary<uint, GlxBinding>? tags))
        {
            tags.Remove(tag);
        }
    }

    /// <summary>客户端断开:它的标签作废,上下文不再是当前;拼到一半的 RenderLarge 丢掉。</summary>
    public void CleanupClient(XClient client)
    {
        _glxLarge.Remove(client);
        if (_glxTags.Remove(client, out Dictionary<uint, GlxBinding>? tags))
        {
            foreach (GlxBinding binding in tags.Values)
            {
                binding.Context.Gl?.Bind(null, null);
                binding.Context.Current = null;
            }
        }
    }

    /// <summary>窗口销毁:它的表面随之丢掉。</summary>
    public void CleanupWindow(XWindow window) => _glxSurfaces.Remove(window.Id);

    // ------------------------------------------------------------------ 渲染请求

    private (GlxBinding Binding, GlContext Gl) GlxRenderTarget(XClient c, uint tag)
    {
        GlxBinding binding = GlxBindingOf(c, tag);
        GlContext gl = binding.Context.Gl ?? throw GlxError(GlxBadContextState, tag);
        gl.ResetBudget();
        SyncGlxBinding(binding);
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
            _glxLarge.Remove(c);
            int length = (int)r.U32(), opcode = (int)r.U32();
            // n 是小参数的字节数;有的客户端把 8 字节的长度与操作码也算在内 —— 按请求里实际剩下的字节判断。
            int small = XWire.Pad(n) == r.Remaining ? n : n - 8;
            if (small < 0 || small > r.Remaining || total < 1 || !GlContext.IsKnownRenderOpcode(opcode))
            {
                throw GlxError(GlxBadLargeRequest, (uint)number);
            }
            GlxLargeCommand large = new(tag, total, opcode, length);
            large.Data.AddRange(r.Bytes(small));
            if (total == 1)
            {
                gl.ExecuteOrCompile(opcode, [.. large.Data], c.BigEndian);
                PresentGlx(binding);
                return;
            }
            _glxLarge[c] = large;
            return;
        }
        if (!_glxLarge.TryGetValue(c, out GlxLargeCommand? pending) || pending.Tag != tag || pending.Next != number
            || pending.Total != total || n < 0 || n > r.Remaining)
        {
            _glxLarge.Remove(c);
            throw GlxError(GlxBadLargeRequest, (uint)number);
        }
        if (pending.Data.Count + n > 64 * 1024 * 1024)
        {
            _glxLarge.Remove(c);
            throw new XProtocolError(XErrorCode.Alloc);
        }
        pending.Data.AddRange(r.Bytes(n));
        pending.Next++;
        if (number == total)
        {
            _glxLarge.Remove(c);
            gl.ExecuteOrCompile(pending.Opcode, [.. pending.Data], c.BigEndian);
            PresentGlx(binding);
        }
    }

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
            case 105:   // FeedbackBuffer
            case 106:   // SelectBuffer:选择与反馈不实现(RenderMode 回 0 条)
                break;
            case 107:   // RenderMode
                {
                    uint previous = gl.RenderModeValue;
                    uint mode = r.U32();
                    int result = gl.RenderMode(mode);
                    if (previous != GlEnum.RENDER)
                    {
                        uint current = gl.RenderModeValue;
                        c.Reply(0, w => w.I32(result).U32(0).U32(current).Zero(12));
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
                    if ((long)width * height > 64L * 1024 * 1024)
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
            case 122:   // GetMapiv:求值器不实现
                r.U32();
                r.U32();
                gl.SetError(GlEnum.INVALID_ENUM);
                ReplyGlValues(c, minor == 120 ? (byte)114 : minor == 121 ? (byte)116 : (byte)117, null);
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
            case 128:   // GetPolygonStipple:点画不实现,回全 1 的初值(32 行 × 4 字节)
                r.Bool();
                c.Reply(0, w => w.Zero(24).Bytes(Enumerable.Repeat((byte)0xFF, 128).ToArray()));
                break;
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
                    uint[] names = gl.GenTextures(n);
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
        (GLX_SAMPLE_BUFFERS, 0),
        (GLX_SAMPLES, 0),
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
            (GLX_SAMPLE_BUFFERS, 0),
            (GLX_SAMPLES, 0),
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
        switch (server.Lookup<XResource>(id))
        {
            case XGlxDrawable { Kind: GlxDrawableKind.Pbuffer } p:
                attributes =
                [
                    (GLX_WIDTH, (uint)p.PbufferWidth), (GLX_HEIGHT, (uint)p.PbufferHeight), (GLX_FBCONFIG_ID, p.Config.Id),
                    (GLX_EVENT_MASK, p.EventMask), (GLX_PRESERVED_CONTENTS, p.PreservedContents ? 1u : 0),
                    (GLX_LARGEST_PBUFFER, p.LargestPbuffer ? 1u : 0),
                ];
                break;
            case XGlxDrawable d:
                {
                    (_, (int Width, int Height) size, _) = ResolveGlxDrawable(id, null);
                    attributes = [(GLX_WIDTH, (uint)size.Width), (GLX_HEIGHT, (uint)size.Height), (GLX_FBCONFIG_ID, d.Config.Id), (GLX_EVENT_MASK, d.EventMask)];
                    break;
                }
            case XWindow window:
                {
                    GlxConfig? config = GlxVisualConfigs.FirstOrDefault(cfg => cfg.Visual == window.Visual);
                    attributes = [(GLX_WIDTH, (uint)window.Width), (GLX_HEIGHT, (uint)window.Height), (GLX_FBCONFIG_ID, config?.Id ?? 0), (GLX_EVENT_MASK, 0)];
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
        if (gl.IsCompiling)
        {
            throw GlxError(GlxBadContextState, tag);
        }
        Fonts.XFont font = server.Lookup<XFontResource>(fontId)?.Font ?? throw new XProtocolError(XErrorCode.Font, fontId);
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
            gl.Shared.Lists[listBase + i] = list;
        }
        PresentGlx(binding);
    }
}

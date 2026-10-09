// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   The OpenGL Graphics System, Version 1.5 —— §2.5「GL Errors」(每类错误一个标志,GetError 取一个并清掉)、
//   §5.4「Display Lists」(NewList 的 COMPILE / COMPILE_AND_EXECUTE、CallList 嵌套上限、CallLists 的类型与 ListBase)、
//   §3.8.12「Texture Objects」(纹理名与共享)、§5.5「Flush and Finish」。
//   GLX Extensions for OpenGL Protocol Specification, Version 1.3 —— §2.3.1「Send Multiple GL Rendering Commands」
//   (一条 glXRender 里是紧排的一串渲染命令:2 字节长度含头、2 字节操作码,补齐到 4 字节)。
//   OpenGL Graphics with the X Window System, Version 1.4 —— §2.3「Sharing」(共享显示列表与纹理对象的上下文共用名字空间)。
//
//   这是 GLX 间接渲染用的软件 GL:固定功能管线的一个子集(版本报 1.1,见 GetString)。选择模式见 GlContext.Select.cs,求值器见 GlContext.Eval.cs。没有实现的:
//   累积缓冲(配置里 0 位)、
//   反馈模式(RenderMode 切过去不画,切回时返回 0 条)、多级纹理的 LOD(总取第 0 级)、3D 纹理、
//   像素传输的缩放 / 偏置与 PixelMap(静默忽略)、深度 / 模板 / 颜色索引格式的 DrawPixels 与 CopyPixels(不画)、
//   点 / 线 / 多边形的平滑(按不平滑画)、Hint(收下不查)。认识但没实现的渲染命令照规范当作合法命令吃掉,不报错;
//   程序第一次用到反馈模式时 GLX 记一行日志(见 TakeUnreportedFeatures),结果落空不再无迹可查。

using System.Numerics;
using System.Runtime.InteropServices;
using VelaShell.XServer.Protocol;

namespace VelaShell.XServer.Gl;

/// <summary>一条记下来的渲染命令(显示列表的一项)。</summary>
internal readonly record struct GlCommand(int Opcode, byte[] Body, bool BigEndian);

/// <summary>一张纹理的一级图像:按 RGBA 每分量 1 字节存(第 0 行是 t = 0),<see cref="BaseFormat" /> 决定采样时取哪几个分量。</summary>
internal sealed class GlTexImage(int width, int height, uint internalFormat, uint baseFormat, byte[] texels)
{
    public int Width { get; } = width;

    public int Height { get; } = height;

    public uint InternalFormat { get; } = internalFormat;

    public uint BaseFormat { get; } = baseFormat;

    public byte[] Texels { get; } = texels;
}

/// <summary>纹理对象:各级图像与采样参数。</summary>
internal sealed class GlTexture(uint name)
{
    public const int MaxLevels = 12;

    public uint Name { get; } = name;

    /// <summary>第一次绑定时定下的目标(TEXTURE_1D / TEXTURE_2D);0 = 还没绑定过。</summary>
    public uint Target { get; set; }

    public GlTexImage?[] Levels { get; } = new GlTexImage?[MaxLevels];

    public GlTexImage? Base => Levels[0];

    /// <summary>
    /// 完整(§3.8.10):有第 0 级;缩小过滤要 mipmap 时各级一直到 1×1 都在,尺寸逐级减半,内部格式一致。
    /// </summary>
    public bool IsComplete
    {
        get
        {
            if (Base is not { Width: > 0, Height: > 0 } b)
            {
                return false;
            }
            if (MinFilter is GlEnum.NEAREST or GlEnum.LINEAR)
            {
                return true;
            }
            int w = b.Width, h = b.Height;
            for (int level = 1; w > 1 || h > 1; level++)
            {
                (w, h) = (Math.Max(1, w / 2), Math.Max(1, h / 2));
                if (level >= MaxLevels || Levels[level] is not { } l || l.Width != w || l.Height != h || l.InternalFormat != b.InternalFormat)
                {
                    return false;
                }
            }
            return true;
        }
    }

    public uint MinFilter { get; set; } = GlEnum.NEAREST_MIPMAP_LINEAR;

    public uint MagFilter { get; set; } = GlEnum.LINEAR;

    public uint WrapS { get; set; } = GlEnum.REPEAT;

    public uint WrapT { get; set; } = GlEnum.REPEAT;

    public Vector4 BorderColor { get; set; }

    public float Priority { get; set; } = 1;
}

/// <summary>
/// GL 对象占的内存记在谁的账上(xs_plan GL-S3)。GLX 层把它接到服务端的每客户端 / 全局内存账上(X-2);
/// 为 null 时(直接驱动 <see cref="GlContext" /> 的单元测试)不记账。
/// </summary>
internal interface IGlMemoryAccount
{
    /// <summary>记 <paramref name="bytes" />(&gt; 0);超了上限返回 false、账不变。</summary>
    bool TryCharge(long bytes);

    /// <summary>退还 <paramref name="bytes" />(&gt; 0)。</summary>
    void Refund(long bytes);
}

/// <summary>显示列表与纹理对象的名字空间;用 share list 建的上下文共用同一个。</summary>
/// <remarks>
/// 两样都记账、都有上限 —— 客户端可以一直编译列表、一直建纹理,不设上限就能把服务端的内存吃光。
/// 超了按 GL 的规矩记 OUT_OF_MEMORY(§2.5),命令不生效。每个共享组各有 <see cref="MaxListBytes" /> 与 <see cref="MaxTextureBytes" /> 两道上限,
/// 此外全部记在建这个组的客户端的内存账上(<see cref="Account" />):多建几个不共享的上下文也绕不过每客户端的总量。
/// </remarks>
internal sealed class GlShared(IGlMemoryAccount? account = null)
{
    /// <summary>显示列表合计的字节上限(含每个列表的对象开销)。</summary>
    public const long MaxListBytes = 64L * 1024 * 1024;

    /// <summary>纹理对象(有名字的)各级图像合计的字节上限:最大的 2048² 纹理连同各级 mipmap 约 22 MB。</summary>
    public const long MaxTextureBytes = 256L * 1024 * 1024;

    /// <summary>纹理名的上限。</summary>
    public const int MaxTextures = 1 << 16;

    /// <summary>每条记下的命令在正文之外按这么多字节记(对象头、数组头)。</summary>
    public const int CommandOverhead = 32;

    /// <summary>每个显示列表本身(对象、名字表的一项)按这么多字节记:空列表也占账,NewList + EndList 建不出无限多个。</summary>
    public const int ListOverhead = 64;

    /// <summary>这个组的列表与纹理记在谁的账上:建组的那个上下文的客户端。</summary>
    public IGlMemoryAccount? Account { get; } = account;

    /// <summary>用着这个名字空间的上下文数;降到 0(最后一个上下文释放)时列表与纹理一并释放、销账(<see cref="Release" />)。</summary>
    public int References { get; set; }

    public Dictionary<uint, List<GlCommand>> Lists { get; } = [];

    public Dictionary<uint, GlTexture> Textures { get; } = [];

    /// <summary>全部显示列表合计的字节数(见 <see cref="SizeOf(List{GlCommand})" />)。</summary>
    public long ListBytes { get; private set; }

    /// <summary>有名字的纹理各级图像合计的字节数。</summary>
    public long TextureBytes { get; private set; }

    /// <summary>一个列表记多少字节:对象开销,加上每条命令的正文与开销。</summary>
    public static long SizeOf(List<GlCommand> list)
    {
        long bytes = ListOverhead;
        foreach (GlCommand command in list)
        {
            bytes += command.Body.Length + CommandOverhead;
        }
        return bytes;
    }

    /// <summary>
    /// 定义(或替换)一个显示列表并记账。<paramref name="precharged" /> 字节已经记在 <see cref="Account" /> 上了(编译时逐条记的命令)。
    /// 超了组的上限或账上记不下时返回 false,什么都不变 —— 已记的 <paramref name="precharged" /> 由调用方退还。
    /// </summary>
    public bool TrySetList(uint name, List<GlCommand> list, long precharged = 0)
    {
        long size = SizeOf(list);
        long old = Lists.TryGetValue(name, out List<GlCommand>? existing) ? SizeOf(existing) : 0;
        if (ListBytes - old + size > MaxListBytes)
        {
            return false;
        }
        long extra = size - precharged;
        if (extra > 0 && Account is not null && !Account.TryCharge(extra))
        {
            return false;
        }
        if (extra < 0)
        {
            Account?.Refund(-extra);
        }
        RemoveList(name);
        Lists[name] = list;
        ListBytes += size;
        MaxListName = Math.Max(MaxListName, name);
        return true;
    }

    /// <summary>用过的最大的显示列表名(只增不减):GenLists 从它之后分配,不必每次把已占的名字排序。</summary>
    public uint MaxListName { get; private set; }

    /// <summary>用过的最大的纹理名(只增不减):GenTextures 从它之后分配,不必每次从 1 起逐个探测。</summary>
    public uint MaxTextureName { get; private set; }

    /// <summary>登记一个纹理对象。</summary>
    public void AddTexture(GlTexture texture)
    {
        Textures[texture.Name] = texture;
        MaxTextureName = Math.Max(MaxTextureName, texture.Name);
    }

    /// <summary>删掉一个显示列表,销账。</summary>
    public bool RemoveList(uint name)
    {
        if (!Lists.Remove(name, out List<GlCommand>? old))
        {
            return false;
        }
        long size = SizeOf(old);
        ListBytes -= size;
        Account?.Refund(size);
        return true;
    }

    /// <summary>有名字的纹理多占 <paramref name="bytes" />(&gt; 0):组的上限与账都记得下才记,否则返回 false、账不变。</summary>
    public bool TryChargeTexture(long bytes)
    {
        if (TextureBytes + bytes > MaxTextureBytes || (Account is not null && !Account.TryCharge(bytes)))
        {
            return false;
        }
        TextureBytes += bytes;
        return true;
    }

    /// <summary>有名字的纹理少占 <paramref name="bytes" />(&gt; 0):销账。</summary>
    public void RefundTexture(long bytes)
    {
        TextureBytes -= bytes;
        Account?.Refund(bytes);
    }

    /// <summary>最后一个用它的上下文释放了:列表与纹理全部丢掉,账一次退清。</summary>
    public void Release()
    {
        long bytes = ListBytes + TextureBytes;
        if (bytes > 0)
        {
            Account?.Refund(bytes);
        }
        Lists.Clear();
        Textures.Clear();
        ListBytes = 0;
        TextureBytes = 0;
    }

    /// <summary>纹理对象各级图像合计的字节数。</summary>
    public static long SizeOf(GlTexture texture)
    {
        long bytes = 0;
        foreach (GlTexImage? level in texture.Levels)
        {
            bytes += level?.Texels.Length ?? 0;
        }
        return bytes;
    }
}

/// <summary>程序用到、而这个软件 GL 没有实现的功能(结果落空、不报 GL 错误;GLX 第一次碰到时记一行日志)。</summary>
[Flags]
internal enum GlUnimplementedFeatures
{
    None = 0,

    /// <summary>反馈模式(RenderMode(FEEDBACK))。</summary>
    Feedback = 2,
}

/// <summary>一个间接渲染上下文:GL 状态机、固定功能管线与软件光栅化。只在服务端执行线程上用。</summary>
internal sealed partial class GlContext
{
    public const int MaxLights = 8;
    public const int MaxClipPlanes = 6;
    public const int MaxTextureSize = 2048;
    public const int MaxListNesting = 64;
    public const int MaxMatrixDepth = 32;
    public const int MaxAttribDepth = 16;

    /// <summary>
    /// 非抗锯齿线宽与点大小的上限(LINE_WIDTH_RANGE / POINT_SIZE_RANGE 报的最大值)。§3.3、§3.4:给的宽度先取整,
    /// 再夹到实现的上限 —— 不夹的话一条线宽 2^31 的线每个像素都要走 2^31 次。
    /// </summary>
    public const int MaxLineWidth = 64;

    private readonly Stack<(GlState State, uint Mask)> _attribStack = new();
    private readonly List<Matrix4x4> _modelview = [Matrix4x4.Identity];
    private readonly List<Matrix4x4> _projection = [Matrix4x4.Identity];
    private readonly List<Matrix4x4> _texture = [Matrix4x4.Identity];
    private uint _error;

    // 显示列表编译
    private uint _compileMode;
    private List<GlCommand>? _compiling;
    private long _compilingBytes;
    private int _callDepth;

    /// <summary>
    /// 一个请求里显示列表展开执行的命令数上限:列表可以互相调用,嵌套 64 层、每层调两次就是 2^64 条 ——
    /// 超出后不再展开并记 OUT_OF_MEMORY,免得一个客户端卡住执行线程。每次 CallList(包括调空列表、调不存在的列表)
    /// 也算一条:否则一个装着「CallLists 一百万个空列表」的列表被调一百万次,就是 10^12 次空转而一条都不计。
    /// </summary>
    public const long ListCommandBudget = 4_000_000;

    /// <summary>
    /// 一个上下文对象本身(状态、属性栈、矩阵栈、默认纹理对象……)记多少字节:GLX 建间接上下文时记在客户端名下,
    /// 一个 24 字节的 CreateContext 换不来不记账的几十 KB。
    /// </summary>
    public const long ObjectBytes = 64 * 1024;

    private long _budget = ListCommandBudget;

    /// <summary>每个 GLX 请求开始时由服务端调用。</summary>
    public void ResetBudget()
    {
        _budget = ListCommandBudget;
        _workExhausted = false;
    }

    /// <param name="doubleBuffered">配置是不是双缓冲(决定 DRAW_BUFFER / READ_BUFFER 的初值)。</param>
    /// <param name="hasAlpha">配置有没有 alpha 位。</param>
    /// <param name="share">共用名字空间的那个组;null 时新建一个,记在 <paramref name="account" /> 上。</param>
    /// <param name="account">本上下文自己的分配(默认纹理、图元缓冲)记在谁的账上;null 不记账。</param>
    public GlContext(bool doubleBuffered, bool hasAlpha, GlShared? share, IGlMemoryAccount? account = null)
    {
        DoubleBuffered = doubleBuffered;
        HasAlpha = hasAlpha;
        Account = account;
        Shared = share ?? new GlShared(account);
        Shared.References++;
        State.DrawBuffer = doubleBuffered ? GlEnum.BACK : GlEnum.FRONT;
        State.ReadBuffer = State.DrawBuffer;
    }

    public GlState State { get; private set; } = new();

    public GlShared Shared { get; }

    /// <summary>本上下文自己的分配记在谁的账上(共享组的列表与纹理记在 <see cref="GlShared.Account" /> 上)。</summary>
    public IGlMemoryAccount? Account { get; }

    private bool _released;

    /// <summary>
    /// 上下文没了(资源已释放、也不再是当前):默认纹理、图元缓冲、编译到一半的列表退账;共享组没有别的上下文在用时
    /// 连同它的列表与纹理一并释放。只有第一次调用起作用。
    /// </summary>
    public void Release()
    {
        if (_released)
        {
            return;
        }
        _released = true;
        long own = GlShared.SizeOf(_default1D) + GlShared.SizeOf(_default2D) + _primitiveCharged;
        if (own > 0)
        {
            Account?.Refund(own);
        }
        Array.Clear(_default1D.Levels);
        Array.Clear(_default2D.Levels);
        _primitive.Clear();
        _primitive.Capacity = 0;
        _primitiveCharged = 0;
        if (_compiling is not null && _compilingBytes > 0)
        {
            Shared.Account?.Refund(_compilingBytes);
        }
        _compiling = null;
        _compilingBytes = 0;
        if (--Shared.References == 0)
        {
            Shared.Release();
        }
    }

    public bool DoubleBuffered { get; }

    public bool HasAlpha { get; }

    /// <summary>当前的绘制 / 读取表面。</summary>
    public GlSurface? Draw { get; private set; }

    public GlSurface? Read { get; private set; }

    private bool _viewportInitialized;

    /// <summary>当前渲染模式(RENDER / FEEDBACK / SELECT)。</summary>
    public uint RenderModeValue { get; private set; } = GlEnum.RENDER;

    /// <summary>
    /// MakeCurrent:第一次绑上表面时,视口与剪裁框初始化成可绘对象的尺寸(GLX 1.4 §3.3.7;表面被夹小了也按整个可绘对象)。
    /// 可绘对象已经没了时绑 null:渲染不落到任何地方,查询照常。
    /// </summary>
    public void Bind(GlSurface? draw, GlSurface? read)
    {
        InvalidateRaster();
        Draw = draw;
        Read = read ?? draw;
        if (draw is not null && !_viewportInitialized)
        {
            _viewportInitialized = true;
            (State.ViewportX, State.ViewportY, State.ViewportWidth, State.ViewportHeight) = (0, 0, draw.DrawableWidth, draw.DrawableHeight);
            (State.ScissorX, State.ScissorY, State.ScissorWidth, State.ScissorHeight) = (0, 0, draw.DrawableWidth, draw.DrawableHeight);
        }
    }

    /// <summary>记一个 GL 错误:已有未取走的错误时不覆盖(§2.5)。</summary>
    public void SetError(uint error)
    {
        if (_error == GlEnum.NO_ERROR)
        {
            _error = error;
        }
    }

    public uint GetError()
    {
        uint e = _error;
        _error = GlEnum.NO_ERROR;
        return e;
    }

    // ------------------------------------------------------------------ 渲染命令流

    /// <summary>
    /// 执行 glXRender 里的一串渲染命令。返回 -1 表示全部执行;否则是第一条长度不对的命令的序号
    /// (GLXBadRenderRequest 的「出错之前的命令数」)。
    /// </summary>
    public int ExecuteStream(ReadOnlySpan<byte> commands, bool bigEndian)
    {
        int index = 0;
        int pos = 0;
        while (pos + 4 <= commands.Length)
        {
            GlReader header = new(commands.Slice(pos, 4), bigEndian);
            int length = header.U16();
            int opcode = header.U16();
            if (length < 4 || pos + length > commands.Length)
            {
                return index;
            }
            ExecuteOrCompile(opcode, commands.Slice(pos + 4, length - 4), bigEndian);
            if (_workExhausted)
            {
                return -1;   // 这个请求的工作量花光了:余下的命令不执行(已记 OUT_OF_MEMORY)
            }
            pos += (length + 3) & ~3;
            index++;
        }
        return pos == commands.Length ? -1 : index;
    }

    /// <summary>这个请求的工作量预算已经花光(<see cref="WorkBudget" />):余下的渲染命令一律不执行。</summary>
    private bool _workExhausted;

    /// <summary>执行(或记进正在编译的显示列表)一条渲染命令;<paramref name="body" /> 是头之后的参数。</summary>
    public void ExecuteOrCompile(int opcode, ReadOnlySpan<byte> body, bool bigEndian)
    {
        if (_compiling is not null)
        {
            // 记进列表之前先看账:全部列表加上正在编译的这一个超了组的上限、或客户端的账上记不下,这条命令不记(OUT_OF_MEMORY)。
            // 编译中的命令就记在共享组的账上(EndList 时原样转成列表的账)。
            long size = body.Length + GlShared.CommandOverhead;
            if (Shared.ListBytes + _compilingBytes + size > GlShared.MaxListBytes || (Shared.Account is { } account && !account.TryCharge(size)))
            {
                SetError(GlEnum.OUT_OF_MEMORY);
            }
            else
            {
                _compiling.Add(new GlCommand(opcode, body.ToArray(), bigEndian));
                _compilingBytes += size;
            }
            if (_compileMode != GlEnum.COMPILE_AND_EXECUTE)
            {
                return;
            }
        }
        if (_workExhausted)
        {
            return;
        }
        try
        {
            Execute(opcode, body, bigEndian);
        }
        catch (XWorkBudgetExhausted)
        {
            // 渲染命令没有回复、不报 X 错误:按 GL 的约定记 OUT_OF_MEMORY(§2.5),这个请求余下的命令不再执行。
            // 显示列表的条数预算之外,这里按真正的工作量(片元、顶点、清屏、像素)兜底:一条 CallLists 不能把执行线程卡上几小时。
            SetError(GlEnum.OUT_OF_MEMORY);
            _workExhausted = true;
        }
        finally
        {
            FlushDrawn();
        }
    }

    // ------------------------------------------------------------------ 显示列表

    public void NewList(uint list, uint mode)
    {
        if (list == 0)
        {
            SetError(GlEnum.INVALID_VALUE);
            return;
        }
        if (mode is not (GlEnum.COMPILE or GlEnum.COMPILE_AND_EXECUTE))
        {
            SetError(GlEnum.INVALID_ENUM);
            return;
        }
        if (_compiling is not null || InBeginEnd)
        {
            SetError(GlEnum.INVALID_OPERATION);
            return;
        }
        ListIndex = list;
        _compileMode = mode;
        _compiling = [];
        _compilingBytes = 0;
    }

    public void EndList()
    {
        if (_compiling is null)
        {
            SetError(GlEnum.INVALID_OPERATION);
            return;
        }
        if (!Shared.TrySetList(ListIndex, _compiling, _compilingBytes))
        {
            // 列表本身的开销记不下:这个列表不定义(原来同名的那个保持不变),编译时记的账退回去。
            SetError(GlEnum.OUT_OF_MEMORY);
            if (_compilingBytes > 0)
            {
                Shared.Account?.Refund(_compilingBytes);
            }
        }
        _compiling = null;
        _compilingBytes = 0;
        ListIndex = 0;
    }

    public bool IsCompiling => _compiling is not null;

    public uint ListIndex { get; private set; }

    public uint ListMode => _compiling is null ? 0 : _compileMode;

    /// <summary>
    /// GenLists(§5.4):找一段 <paramref name="range" /> 个连续的、没用过的名字并登记成空列表。range 为 0、
    /// 这么多个空列表记不下(每个列表按 <see cref="GlShared.ListOverhead" /> 记账,见 <see cref="GlShared.MaxListBytes" />)时
    /// 不生成任何名字,返回 0 —— range 可以到 2^31,照单全收就是几十亿个空列表。
    /// </summary>
    public uint GenLists(int range)
    {
        if (range < 0)
        {
            SetError(GlEnum.INVALID_VALUE);
            return 0;
        }
        long bytes = (long)range * GlShared.ListOverhead;
        if (range == 0 || Shared.ListBytes + bytes > GlShared.MaxListBytes)
        {
            return 0;
        }
        if (Shared.Account is { } account && !account.TryCharge(bytes))
        {
            SetError(GlEnum.OUT_OF_MEMORY);
            return 0;
        }
        // 通常直接接在用过的最大的名字之后(那之后的名字都没用过);只有接不下(名字快用到 2^32)时才把已占的名字排个序、
        // 找第一个够大的空档 —— 原先每次都排序,建几万个列表就是平方级。用 long 算:名字是 32 位,在 uint 上加会回绕成死循环。
        long start = (long)Shared.MaxListName + 1;
        if (start + range - 1 > uint.MaxValue)
        {
            start = 1;
            foreach (uint used in Shared.Lists.Keys.Order())
            {
                if (used < start)
                {
                    continue;
                }
                if (used - start >= range)
                {
                    break;
                }
                start = (long)used + 1;
            }
        }
        if (start + range - 1 > uint.MaxValue)
        {
            Shared.Account?.Refund(bytes);
            return 0;
        }
        for (long i = 0; i < range; i++)
        {
            _ = Shared.TrySetList((uint)(start + i), [], precharged: GlShared.ListOverhead);   // 上面已经整段记过账、核过组的上限
        }
        return (uint)start;
    }

    /// <summary>DeleteLists(§5.4):删掉 [list, list + range) 里存在的列表。range 比现有的列表还多时改为遍历现有的列表,不按 range 空转。</summary>
    public void DeleteLists(uint list, int range)
    {
        if (range < 0)
        {
            SetError(GlEnum.INVALID_VALUE);
            return;
        }
        ulong end = list + (ulong)range;
        if (range <= Shared.Lists.Count)
        {
            for (ulong name = list; name < end; name++)
            {
                Shared.RemoveList((uint)name);
            }
            return;
        }
        foreach (uint name in Shared.Lists.Keys.Where(name => name >= list && name < end).ToArray())
        {
            Shared.RemoveList(name);
        }
    }

    public bool IsList(uint list) => Shared.Lists.ContainsKey(list);

    /// <summary>执行一个显示列表;这一次调用本身也算预算里的一条。预算用完返回 false,调用方(CallLists)随之停下。</summary>
    private bool CallList(uint list)
    {
        if (!Spend())
        {
            return false;
        }
        if (_callDepth >= MaxListNesting || !Shared.Lists.TryGetValue(list, out List<GlCommand>? commands))
        {
            return true;
        }
        _callDepth++;
        try
        {
            // 直接遍历列表本身,不复制:能改动列表的 NewList / EndList / DeleteLists / UseXFont 都是单独的 GLX 请求,
            // 不可能在一个列表执行到一半时插进来(原先每次调用都 ToArray 一份)。
            foreach (GlCommand command in CollectionsMarshal.AsSpan(commands))
            {
                if (!Spend())
                {
                    return false;
                }
                Execute(command.Opcode, command.Body, command.BigEndian);
            }
            return _budget >= 0;
        }
        finally
        {
            _callDepth--;
        }
    }

    /// <summary>从这个请求的展开预算里花一条;花光时记一次 OUT_OF_MEMORY 并返回 false。</summary>
    private bool Spend()
    {
        if (--_budget >= 0)
        {
            return true;
        }
        if (_budget == -1)
        {
            SetError(GlEnum.OUT_OF_MEMORY);
        }
        _budget = -1;   // 停在 -1:之后的调用都直接返回,不会一路减到回绕
        return false;
    }

    // ------------------------------------------------------------------ 纹理名

    /// <summary>GenTextures(§3.8.12);名字超过 <see cref="GlShared.MaxTextures" /> 时返回 null(由 GLX 回 BadAlloc)。</summary>
    public uint[]? GenTextures(int n)
    {
        if (n < 0)
        {
            SetError(GlEnum.INVALID_VALUE);
            return [];
        }
        if (n > GlShared.MaxTextures - Shared.Textures.Count)
        {
            return null;
        }
        uint[] names = new uint[n];
        // 通常直接接在用过的最大的名字之后(那之后都没用过);快用到 2^32 时才回到 1 起逐个探测 —— 原先每次都从 1 探起,
        // 建几万个纹理就是平方级。名字总数有上限,探测总能找到空位(0 不是纹理名,跳过)。
        uint next = Shared.MaxTextureName <= uint.MaxValue - (uint)n ? Shared.MaxTextureName + 1 : 1;
        for (int i = 0; i < n; i++)
        {
            while (next == 0 || Shared.Textures.ContainsKey(next))
            {
                next++;
            }
            names[i] = next;
            Shared.AddTexture(new GlTexture(next));
            next++;
        }
        return names;
    }

    public void DeleteTextures(ReadOnlySpan<uint> names)
    {
        foreach (uint name in names)
        {
            if (name == 0)
            {
                continue;
            }
            if (Shared.Textures.Remove(name, out GlTexture? gone) && GlShared.SizeOf(gone) is > 0 and var bytes)
            {
                Shared.RefundTexture(bytes);
            }
            if (State.Texture1D == name)
            {
                State.Texture1D = 0;
            }
            if (State.Texture2D == name)
            {
                State.Texture2D = 0;
            }
        }
    }

    /// <summary>IsTexture:只有绑定过(有了目标)的名字才算纹理对象。</summary>
    public bool IsTexture(uint name) => name != 0 && Shared.Textures.TryGetValue(name, out GlTexture? t) && t.Target != 0;

    // ------------------------------------------------------------------ 没实现的功能

    private GlUnimplementedFeatures _usedFeatures, _reportedFeatures;

    /// <summary>程序用到了一样没实现的功能(结果落空,但不报 GL 错误)。</summary>
    public void NoteUnimplemented(GlUnimplementedFeatures feature) => _usedFeatures |= feature;

    /// <summary>用到了、还没报过的那些没实现的功能;取走之后不再报(每个上下文每样只记一行日志)。</summary>
    public GlUnimplementedFeatures TakeUnreportedFeatures()
    {
        GlUnimplementedFeatures fresh = _usedFeatures & ~_reportedFeatures;
        _reportedFeatures |= fresh;
        return fresh;
    }

    // ------------------------------------------------------------------ 矩阵栈

    private List<Matrix4x4> CurrentStack => State.MatrixMode switch
    {
        GlEnum.PROJECTION => _projection,
        GlEnum.TEXTURE => _texture,
        _ => _modelview,
    };

    private Matrix4x4 Modelview => _modelview[^1];

    private Matrix4x4 Projection => _projection[^1];

    private Matrix4x4 TextureMatrix => _texture[^1];

    /// <summary>
    /// 本文件的矩阵都按「行向量 × 矩阵」存(System.Numerics 的约定),即 GL 的列向量矩阵的转置:
    /// GL 的 M·v 对应这里的 v·Mᵀ,GL 的 C·N(MultMatrix)对应这里的 Nᵀ·Cᵀ。
    /// </summary>
    private void MultMatrix(Matrix4x4 transposed)
    {
        List<Matrix4x4> stack = CurrentStack;
        stack[^1] = transposed * stack[^1];
        OnMatrixChanged();
    }

    private void LoadMatrix(Matrix4x4 transposed)
    {
        List<Matrix4x4> stack = CurrentStack;
        stack[^1] = transposed;
        OnMatrixChanged();
    }

    /// <summary>GL 的列主序 16 个数 → 这里的转置存法(逐个照抄进 M11…M44 恰好就是转置)。</summary>
    private static Matrix4x4 FromColumnMajor(ReadOnlySpan<float> m) => new(
        m[0], m[1], m[2], m[3], m[4], m[5], m[6], m[7], m[8], m[9], m[10], m[11], m[12], m[13], m[14], m[15]);

    /// <summary>按数学上的行主序给出 GL 矩阵(与规范里的写法一致),返回转置存法。</summary>
    private static Matrix4x4 FromRows(
        float a00, float a01, float a02, float a03,
        float a10, float a11, float a12, float a13,
        float a20, float a21, float a22, float a23,
        float a30, float a31, float a32, float a33) => new(
        a00, a10, a20, a30,
        a01, a11, a21, a31,
        a02, a12, a22, a32,
        a03, a13, a23, a33);

    private static float[] ToColumnMajor(Matrix4x4 t) =>
        [t.M11, t.M12, t.M13, t.M14, t.M21, t.M22, t.M23, t.M24, t.M31, t.M32, t.M33, t.M34, t.M41, t.M42, t.M43, t.M44];

    private void PushMatrix()
    {
        List<Matrix4x4> stack = CurrentStack;
        if (stack.Count >= MaxMatrixDepth)
        {
            SetError(GlEnum.STACK_OVERFLOW);
            return;
        }
        stack.Add(stack[^1]);
    }

    private void PopMatrix()
    {
        List<Matrix4x4> stack = CurrentStack;
        if (stack.Count <= 1)
        {
            SetError(GlEnum.STACK_UNDERFLOW);
            return;
        }
        stack.RemoveAt(stack.Count - 1);
        OnMatrixChanged();
    }

    // ------------------------------------------------------------------ 属性栈

    /// <summary>
    /// Enable / Disable / IsEnabled 认识的开关:1.1 的全部(求值器的 MAP1_* / MAP2_* 也算 —— 求值器不实现,开关照样可以拨),
    /// 加上声明了的扩展带来的(POLYGON_OFFSET_FILL 即 EXT 的 POLYGON_OFFSET、RESCALE_NORMAL)与 MULTISAMPLE。
    /// 别的值按 §2.5 记 INVALID_ENUM、不进状态 —— 原先照单全收,16 MB 的 Enable 流把开关集合撑大,再经 PushAttrib 复制 16 份。
    /// </summary>
    private static bool IsKnownCap(uint cap) => GlCaps.BitOf(cap) >= 0;

    private void PushAttrib(uint mask)
    {
        if (_attribStack.Count >= MaxAttribDepth)
        {
            SetError(GlEnum.STACK_OVERFLOW);
            return;
        }
        _attribStack.Push((State.Snapshot(mask), mask));
    }

    private void PopAttrib()
    {
        if (!_attribStack.TryPop(out (GlState State, uint Mask) saved))
        {
            SetError(GlEnum.STACK_UNDERFLOW);
            return;
        }
        State.Restore(saved.State, saved.Mask);
    }
}

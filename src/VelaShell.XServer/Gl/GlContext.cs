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
//   这是 GLX 间接渲染用的软件 GL:固定功能管线的一个子集(版本报 1.1,见 GetString)。没有实现的:
//   求值器(Map / EvalCoord)、累积缓冲(配置里 0 位)、选择 / 反馈模式(RenderMode 切过去不画,返回 0 条)、
//   多级纹理的 LOD(总取第 0 级)、多边形 / 线的点画、3D 纹理。认识但没实现的渲染命令照规范当作合法命令吃掉,不报错。

using System.Numerics;

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

/// <summary>显示列表与纹理对象的名字空间;用 share list 建的上下文共用同一个。</summary>
internal sealed class GlShared
{
    public Dictionary<uint, List<GlCommand>> Lists { get; } = [];

    public Dictionary<uint, GlTexture> Textures { get; } = [];
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

    private readonly Stack<(GlState State, uint Mask)> _attribStack = new();
    private readonly List<Matrix4x4> _modelview = [Matrix4x4.Identity];
    private readonly List<Matrix4x4> _projection = [Matrix4x4.Identity];
    private readonly List<Matrix4x4> _texture = [Matrix4x4.Identity];
    private uint _error;

    // 显示列表编译
    private uint _compilingList;
    private uint _compileMode;
    private List<GlCommand>? _compiling;
    private int _callDepth;

    /// <summary>
    /// 一个请求里显示列表展开执行的命令数上限:列表可以互相调用,嵌套 64 层、每层调两次就是 2^64 条 ——
    /// 超出后不再展开并记 OUT_OF_MEMORY,免得一个客户端卡住执行线程。
    /// </summary>
    public const long ListCommandBudget = 4_000_000;

    private long _budget = ListCommandBudget;

    /// <summary>每个 GLX 请求开始时由服务端调用。</summary>
    public void ResetBudget() => _budget = ListCommandBudget;

    public GlContext(bool doubleBuffered, bool hasAlpha, GlShared? share)
    {
        DoubleBuffered = doubleBuffered;
        HasAlpha = hasAlpha;
        Shared = share ?? new GlShared();
        State.DrawBuffer = doubleBuffered ? GlEnum.BACK : GlEnum.FRONT;
        State.ReadBuffer = State.DrawBuffer;
    }

    public GlState State { get; private set; } = new();

    public GlShared Shared { get; }

    public bool DoubleBuffered { get; }

    public bool HasAlpha { get; }

    /// <summary>当前的绘制 / 读取表面。</summary>
    public GlSurface? Draw { get; private set; }

    public GlSurface? Read { get; private set; }

    private bool _viewportInitialized;

    /// <summary>当前渲染模式(RENDER / FEEDBACK / SELECT)。</summary>
    public uint RenderModeValue { get; private set; } = GlEnum.RENDER;

    /// <summary>MakeCurrent:第一次绑上表面时,视口与剪裁框初始化成表面的尺寸(GLX 1.4 §3.3.7)。</summary>
    public void Bind(GlSurface? draw, GlSurface? read)
    {
        Draw = draw;
        Read = read ?? draw;
        if (draw is not null && !_viewportInitialized)
        {
            _viewportInitialized = true;
            (State.ViewportX, State.ViewportY, State.ViewportWidth, State.ViewportHeight) = (0, 0, draw.Width, draw.Height);
            (State.ScissorX, State.ScissorY, State.ScissorWidth, State.ScissorHeight) = (0, 0, draw.Width, draw.Height);
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
            pos += (length + 3) & ~3;
            index++;
        }
        return pos == commands.Length ? -1 : index;
    }

    /// <summary>执行(或记进正在编译的显示列表)一条渲染命令;<paramref name="body" /> 是头之后的参数。</summary>
    public void ExecuteOrCompile(int opcode, ReadOnlySpan<byte> body, bool bigEndian)
    {
        if (_compiling is not null)
        {
            _compiling.Add(new GlCommand(opcode, body.ToArray(), bigEndian));
            if (_compileMode != GlEnum.COMPILE_AND_EXECUTE)
            {
                return;
            }
        }
        Execute(opcode, body, bigEndian);
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
        _compilingList = list;
        _compileMode = mode;
        _compiling = [];
    }

    public void EndList()
    {
        if (_compiling is null)
        {
            SetError(GlEnum.INVALID_OPERATION);
            return;
        }
        Shared.Lists[_compilingList] = _compiling;
        _compiling = null;
        _compilingList = 0;
    }

    public bool IsCompiling => _compiling is not null;

    public uint ListIndex => _compilingList;

    public uint ListMode => _compiling is null ? 0 : _compileMode;

    public uint GenLists(int range)
    {
        if (range < 0)
        {
            SetError(GlEnum.INVALID_VALUE);
            return 0;
        }
        if (range == 0)
        {
            return 0;
        }
        // 找一段连续的、没用过的名字。
        uint start = 1;
        while (true)
        {
            bool free = true;
            for (uint i = 0; i < range; i++)
            {
                if (Shared.Lists.ContainsKey(start + i))
                {
                    start += i + 1;
                    free = false;
                    break;
                }
            }
            if (free)
            {
                break;
            }
        }
        for (uint i = 0; i < range; i++)
        {
            Shared.Lists[start + i] = [];
        }
        return start;
    }

    public void DeleteLists(uint list, int range)
    {
        if (range < 0)
        {
            SetError(GlEnum.INVALID_VALUE);
            return;
        }
        for (uint i = 0; i < range; i++)
        {
            Shared.Lists.Remove(list + i);
        }
    }

    public bool IsList(uint list) => Shared.Lists.ContainsKey(list);

    private void CallList(uint list)
    {
        if (_callDepth >= MaxListNesting || !Shared.Lists.TryGetValue(list, out List<GlCommand>? commands))
        {
            return;
        }
        _callDepth++;
        try
        {
            // 执行期间列表可能被重新定义(不会,编译时不执行 NewList):照当时的快照执行。
            foreach (GlCommand command in commands.ToArray())
            {
                if (--_budget < 0)
                {
                    SetError(GlEnum.OUT_OF_MEMORY);
                    return;
                }
                Execute(command.Opcode, command.Body, command.BigEndian);
            }
        }
        finally
        {
            _callDepth--;
        }
    }

    // ------------------------------------------------------------------ 纹理名

    public uint[] GenTextures(int n)
    {
        if (n < 0)
        {
            SetError(GlEnum.INVALID_VALUE);
            return [];
        }
        uint[] names = new uint[n];
        uint next = 1;
        for (int i = 0; i < n; i++)
        {
            while (Shared.Textures.ContainsKey(next))
            {
                next++;
            }
            names[i] = next;
            Shared.Textures[next] = new GlTexture(next);
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
            Shared.Textures.Remove(name);
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

    // ------------------------------------------------------------------ 渲染模式

    /// <summary>RenderMode:返回上一模式下记录的条数。选择 / 反馈不实现,切过去后不画、记 0 条。</summary>
    public int RenderMode(uint mode)
    {
        if (mode is not (GlEnum.RENDER or GlEnum.FEEDBACK or GlEnum.SELECT))
        {
            SetError(GlEnum.INVALID_ENUM);
            return 0;
        }
        RenderModeValue = mode;
        return 0;
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

    private void PushAttrib(uint mask)
    {
        if (_attribStack.Count >= MaxAttribDepth)
        {
            SetError(GlEnum.STACK_OVERFLOW);
            return;
        }
        _attribStack.Push((State.Clone(), mask));
    }

    private void PopAttrib()
    {
        if (!_attribStack.TryPop(out var saved))
        {
            SetError(GlEnum.STACK_UNDERFLOW);
            return;
        }
        State.Restore(saved.State, saved.Mask);
    }
}

// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   The OpenGL Graphics System, Version 1.5 —— §5.2「Selection」(名字栈 InitNames / PopName / PushName / LoadName 与它们的错误、
//   深度至少 64;SelectBuffer 在选择模式里调或没调就进选择模式记 INVALID_OPERATION;点、线、多边形或 RasterPos 的合法坐标与裁剪体
//   相交就是一次命中,会被剔除的多边形不算、与 PolygonMode 无关;名字栈变动或 RenderMode 时有过命中就写一条命中记录:
//   名字个数、最小深度、最大深度、名字栈自底向上 —— 深度取命中以来各图元裁剪后各顶点的窗口 z,乘 2³²−1 取整,不加深度偏移;
//   写不下时能写多少写多少并置溢出标志,RenderMode 返回 −1;RenderMode 时清空名字栈、选择数组的指针回到开头)。
//   GLX Extensions for OpenGL Protocol Specification, Version 1.3 —— §2.2.1「RenderMode」(之前在选择模式时回复带 n 个 CARD32 的
//   选择数据)与「SelectBuffer」(只带大小,数据在下一次 RenderMode 的回复里)。
//
//   选择模式不产生片元:图元走到裁剪之后只记命中,不光栅化。CAD 程序靠 gluPickMatrix 把拾取区缩成一个小视体,再看哪些名字命中。

using System.Numerics;

namespace VelaShell.XServer.Gl;

internal sealed partial class GlContext
{
    /// <summary>名字栈的深度上限(MAX_NAME_STACK_DEPTH,规范要求至少 64)。</summary>
    public const int MaxNameStackDepth = 64;

    /// <summary>选择数组的上限(SelectBuffer 的 n;客户端给得更大时按这个截,数据要放进一条回复)。</summary>
    public const int MaxSelectBufferSize = 1 << 20;

    private readonly List<uint> _names = [];

    /// <summary>SelectBuffer 给的大小;还没给过为 −1。</summary>
    private int _selectSize = -1;

    /// <summary>这一轮选择写进选择数组的值。</summary>
    private readonly List<uint> _selected = [];

    private bool _selectOverflow, _hit;
    private int _hitRecords;
    private float _hitMinZ, _hitMaxZ;

    /// <summary>名字栈的深度(NAME_STACK_DEPTH)。</summary>
    public int NameStackDepth => _names.Count;

    /// <summary>选择数组的大小(SELECTION_BUFFER_SIZE);没给过为 0。</summary>
    public int SelectBufferSize => Math.Max(0, _selectSize);

    /// <summary>SelectBuffer:选择模式里调记 INVALID_OPERATION,大小为负记 INVALID_VALUE。</summary>
    public void SelectBuffer(int size)
    {
        if (RenderModeValue == GlEnum.SELECT)
        {
            SetError(GlEnum.INVALID_OPERATION);
            return;
        }
        if (size < 0)
        {
            SetError(GlEnum.INVALID_VALUE);
            return;
        }
        _selectSize = Math.Min(size, MaxSelectBufferSize);
    }

    /// <summary>
    /// RenderMode(§5.2):离开选择模式时先把挂着的命中写进去,返回命中记录条数(溢出时 −1),选择数据经 <paramref name="data" /> 交出
    /// (GLX 的回复带上它);清空名字栈、指针回到开头。进选择模式之前没给过 SelectBuffer 记 INVALID_OPERATION、模式不变。
    /// 反馈模式仍然没有实现:切过去不画、切回时 0 条。
    /// </summary>
    public int RenderMode(uint mode, out uint[] data)
    {
        data = [];
        if (mode is not (GlEnum.RENDER or GlEnum.FEEDBACK or GlEnum.SELECT))
        {
            SetError(GlEnum.INVALID_ENUM);
            return 0;
        }
        if (mode == GlEnum.SELECT && _selectSize < 0)
        {
            SetError(GlEnum.INVALID_OPERATION);
            return 0;
        }
        int result = 0;
        if (RenderModeValue == GlEnum.SELECT)
        {
            WriteHitRecord();
            result = _selectOverflow ? -1 : _hitRecords;
            data = [.. _selected];
        }
        _names.Clear();
        _selected.Clear();
        _selectOverflow = false;
        _hit = false;
        _hitRecords = 0;
        RenderModeValue = mode;
        InvalidateRaster();   // 选择 / 反馈模式不出片元:光栅化的目标跟着重新准备
        if (mode == GlEnum.FEEDBACK)
        {
            NoteUnimplemented(GlUnimplementedFeatures.Feedback);
        }
        return result;
    }

    /// <summary>名字栈的四条命令(渲染命令 121 InitNames、122 LoadName、124 PopName、125 PushName);不在选择模式时没有作用。</summary>
    private void NameStack(int opcode, uint name)
    {
        if (RenderModeValue != GlEnum.SELECT)
        {
            return;
        }
        WriteHitRecord();
        switch (opcode)
        {
            case 121:   // InitNames
                _names.Clear();
                break;
            case 122:   // LoadName
                if (_names.Count == 0)
                {
                    SetError(GlEnum.INVALID_OPERATION);
                    break;
                }
                _names[^1] = name;
                break;
            case 124:   // PopName
                if (_names.Count == 0)
                {
                    SetError(GlEnum.STACK_UNDERFLOW);
                    break;
                }
                _names.RemoveAt(_names.Count - 1);
                break;
            case 125:   // PushName
                if (_names.Count >= MaxNameStackDepth)
                {
                    SetError(GlEnum.STACK_OVERFLOW);
                    break;
                }
                _names.Add(name);
                break;
        }
    }

    /// <summary>一次命中:记下窗口 z 的范围(裁剪之后的顶点;选择模式下图元在这里结束,不光栅化)。</summary>
    private void Hit(float z)
    {
        z = Math.Clamp(z, 0, 1);
        if (!_hit)
        {
            _hit = true;
            (_hitMinZ, _hitMaxZ) = (z, z);
            return;
        }
        _hitMinZ = MathF.Min(_hitMinZ, z);
        _hitMaxZ = MathF.Max(_hitMaxZ, z);
    }

    private void Hit(ReadOnlySpan<RasterVertex> vertices)
    {
        foreach (RasterVertex v in vertices)
        {
            Hit(v.Z);
        }
    }

    /// <summary>有过命中就写一条记录:名字个数、最小深度、最大深度、名字栈自底向上;写不下的部分丢掉、置溢出标志。</summary>
    private void WriteHitRecord()
    {
        if (!_hit)
        {
            return;
        }
        _hit = false;
        _hitRecords++;
        Append((uint)_names.Count);
        Append(DepthValue(_hitMinZ));
        Append(DepthValue(_hitMaxZ));
        foreach (uint name in _names)
        {
            Append(name);
        }

        void Append(uint value)
        {
            if (_selected.Count < _selectSize)
            {
                _selected.Add(value);
            }
            else
            {
                _selectOverflow = true;
            }
        }
    }

    /// <summary>深度 [0, 1] 乘 2³²−1、四舍五入成无符号整数(§5.2)。</summary>
    internal static uint DepthValue(float z) => (uint)Math.Round(Math.Clamp((double)z, 0, 1) * uint.MaxValue);

    /// <summary>选择模式下的光栅位置:坐标合法就是一次命中(§5.2)。</summary>
    private void HitRasterPos(Vector4 window) => Hit(window.Z);
}

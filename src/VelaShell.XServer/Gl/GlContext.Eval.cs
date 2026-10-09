// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   The OpenGL Graphics System, Version 1.5 —— §5.1「Evaluators」(Bernstein 多项式 p(u) = Σ Bᵢⁿ(u)·Rᵢ、Map1 / Map2 的目标与每个控制点的
//   分量数(Table 5.1)、order 不在 [1, MAX_EVAL_ORDER] 或 u1 = u2 记 INVALID_VALUE;EvalCoord 时启用的各图按同维求值,顶点图最后发;
//   求值不改当前颜色 / 法线 / 纹理坐标;同类启用了几个时取坐标数最多的;MAP2 顶点图启用、AUTO_NORMAL 打开时法线取 ∂q/∂u × ∂q/∂v 归一化
//   (VERTEX_4 的 q = (x/w, y/w, z/w));没有顶点图启用时求值什么也不做;MapGrid 的 n ≤ 0 记 INVALID_VALUE;EvalMesh1 / EvalMesh2
//   等价于对应的 Begin / EvalCoord / End 序列,网格两端取到的值恰好是 u'1 / u'2;EvalPoint;初始状态:各图 order 1 的常量图)、
//   §6.1.9「Evaluators」查询(GetMap 的 COEFF / ORDER / DOMAIN)。
//   GLX Extensions for OpenGL Protocol Specification, Version 1.3 —— §2.3.3 的 Map1f / Map1d / Map2f / Map2d(控制点紧排:每点 k 个值,
//   Map2 的 R_ij 在 (i·vorder + j)·k 处;order ≤ 0 或目标不认识时命令有误)、MapGrid、EvalCoord、EvalMesh、EvalPoint 的编码,
//   §2.2 的 GetMapdv / fv / iv。
//
//   曲线、曲面(GLUT 的茶壶、GLU 的 NURBS 渲染器在求值器模式下)靠它画。颜色索引图(MAP*_INDEX)收下不用:这里只有 RGBA 模式。

using System.Numerics;
using VelaShell.XServer.Protocol;

namespace VelaShell.XServer.Gl;

internal sealed partial class GlContext
{
    /// <summary>MAX_EVAL_ORDER(规范要求至少 8)。</summary>
    public const int MaxEvalOrder = 8;

    /// <summary>一张求值图:order(二维时 u、v 各一个)、定义域、紧排的控制点(每点 <see cref="K" /> 个值)。</summary>
    private sealed class GlMap(int k, int uOrder, int vOrder, float u1, float u2, float v1, float v2, float[] points)
    {
        public int K { get; } = k;

        public int UOrder { get; } = uOrder;

        public int VOrder { get; } = vOrder;

        public float U1 { get; } = u1;

        public float U2 { get; } = u2;

        public float V1 { get; } = v1;

        public float V2 { get; } = v2;

        public float[] Points { get; } = points;
    }

    // 下标 = 目标 − MAP1_COLOR_4(或 MAP2_COLOR_4):COLOR_4、INDEX、NORMAL、TEXTURE_COORD_1..4、VERTEX_3、VERTEX_4。
    private const int MapColor = 0, MapIndex = 1, MapNormal = 2, MapTexture1 = 3, MapVertex3 = 7, MapVertex4 = 8;

    /// <summary>每种图每个控制点的分量数(Table 5.1)。</summary>
    private static readonly int[] MapComponents = [4, 1, 3, 1, 2, 3, 4, 3, 4];

    /// <summary>初始的常量图给出的值(颜色 1,1,1,1;索引 1;法线 0,0,1;纹理坐标与顶点 0,0,0,1 的前 k 个)。</summary>
    private static float[] InitialMapValue(int index) => index switch
    {
        MapColor => [1, 1, 1, 1],
        MapIndex => [1],
        MapNormal => [0, 0, 1],
        _ => [.. new float[] { 0, 0, 0, 1 }.AsSpan(0, MapComponents[index])],
    };

    private readonly GlMap?[] _maps1 = new GlMap?[9], _maps2 = new GlMap?[9];

    private GlMap Map(int dimension, int index) =>
        (dimension == 1 ? _maps1 : _maps2)[index]
        ?? new GlMap(MapComponents[index], 1, 1, 0, 1, 0, 1, InitialMapValue(index));

    /// <summary>Map1f / Map1d(<paramref name="isDouble" />):目标、u1、u2、order,再是紧排的控制点。</summary>
    private void Map1(ref GlReader r, bool isDouble)
    {
        double u1, u2;
        uint target;
        int order;
        if (isDouble)
        {
            (u1, u2, target, order) = (r.F64(), r.F64(), r.U32(), r.I32());
        }
        else
        {
            (target, u1, u2, order) = (r.U32(), r.F32(), r.F32(), r.I32());
        }
        int index = (int)(target - GlEnum.MAP1_COLOR_4);
        if (target is < GlEnum.MAP1_COLOR_4 or > GlEnum.MAP1_VERTEX_4)
        {
            SetError(GlEnum.INVALID_ENUM);
            return;
        }
        if (order is < 1 or > MaxEvalOrder || u1 == u2 || !double.IsFinite(u1) || !double.IsFinite(u2))
        {
            SetError(GlEnum.INVALID_VALUE);
            return;
        }
        if (ReadControlPoints(ref r, isDouble, order * MapComponents[index]) is not { } points)
        {
            return;
        }
        _maps1[index] = new GlMap(MapComponents[index], order, 1, (float)u1, (float)u2, 0, 1, points);
    }

    /// <summary>Map2f / Map2d:目标、u1、u2、uorder、v1、v2、vorder(双精度版的字段顺序见编码),再是紧排的控制点。</summary>
    private void Map2(ref GlReader r, bool isDouble)
    {
        double u1, u2, v1, v2;
        uint target;
        int uOrder, vOrder;
        if (isDouble)
        {
            (u1, u2, v1, v2, target, uOrder, vOrder) = (r.F64(), r.F64(), r.F64(), r.F64(), r.U32(), r.I32(), r.I32());
        }
        else
        {
            (target, u1, u2, uOrder, v1, v2, vOrder) = (r.U32(), r.F32(), r.F32(), r.I32(), r.F32(), r.F32(), r.I32());
        }
        int index = (int)(target - GlEnum.MAP2_COLOR_4);
        if (target is < GlEnum.MAP2_COLOR_4 or > GlEnum.MAP2_VERTEX_4)
        {
            SetError(GlEnum.INVALID_ENUM);
            return;
        }
        if (uOrder is < 1 or > MaxEvalOrder || vOrder is < 1 or > MaxEvalOrder || u1 == u2 || v1 == v2
            || !double.IsFinite(u1) || !double.IsFinite(u2) || !double.IsFinite(v1) || !double.IsFinite(v2))
        {
            SetError(GlEnum.INVALID_VALUE);
            return;
        }
        if (ReadControlPoints(ref r, isDouble, uOrder * vOrder * MapComponents[index]) is not { } points)
        {
            return;
        }
        _maps2[index] = new GlMap(MapComponents[index], uOrder, vOrder, (float)u1, (float)u2, (float)v1, (float)v2, points);
    }

    /// <summary>控制点:数据不够整张图时命令有误(INVALID_VALUE、不改图)。</summary>
    private float[]? ReadControlPoints(ref GlReader r, bool isDouble, int count)
    {
        if (r.Remaining < count * (isDouble ? 8 : 4))
        {
            SetError(GlEnum.INVALID_VALUE);
            return null;
        }
        float[] points = new float[count];
        for (int i = 0; i < count; i++)
        {
            points[i] = isDouble ? (float)r.F64() : r.F32();
        }
        return points;
    }

    /// <summary>MapGrid1:n ≤ 0 记 INVALID_VALUE。</summary>
    private void MapGrid1(int n, float u1, float u2)
    {
        if (n <= 0)
        {
            SetError(GlEnum.INVALID_VALUE);
            return;
        }
        (State.Grid1Segments, State.Grid1U1, State.Grid1U2) = (n, u1, u2);
    }

    /// <summary>MapGrid2:nu 或 nv ≤ 0 记 INVALID_VALUE。</summary>
    private void MapGrid2(int nu, float u1, float u2, int nv, float v1, float v2)
    {
        if (nu <= 0 || nv <= 0)
        {
            SetError(GlEnum.INVALID_VALUE);
            return;
        }
        (State.Grid2USegments, State.Grid2U1, State.Grid2U2, State.Grid2VSegments, State.Grid2V1, State.Grid2V2) = (nu, u1, u2, nv, v1, v2);
    }

    /// <summary>网格上第 i 格的坐标:两端恰好是 u'1 / u'2(§5.1 的要求)。</summary>
    private static float GridValue(int i, int n, float a, float b) => i == 0 ? a : i == n ? b : a + (i * ((b - a) / n));

    private void EvalPoint1(int i) => EvalCoord1(GridValue(i, State.Grid1Segments, State.Grid1U1, State.Grid1U2));

    private void EvalPoint2(int i, int j) =>
        EvalCoord2(GridValue(i, State.Grid2USegments, State.Grid2U1, State.Grid2U2), GridValue(j, State.Grid2VSegments, State.Grid2V1, State.Grid2V2));

    /// <summary>EvalMesh1:POINT → POINTS,LINE → LINE_STRIP;别的模式记 INVALID_ENUM。没有顶点图启用时什么也不做(不空转整个网格)。</summary>
    private void EvalMesh1(uint mode, int i1, int i2)
    {
        if (mode is not (GlEnum.POINT or GlEnum.LINE))
        {
            SetError(GlEnum.INVALID_ENUM);
            return;
        }
        if (!EnabledMap(1, MapVertex3) && !EnabledMap(1, MapVertex4))
        {
            return;
        }
        try
        {
            Begin(mode == GlEnum.POINT ? GlEnum.POINTS : GlEnum.LINE_STRIP);
            for (long i = i1; i <= i2; i++)
            {
                EvalPoint1((int)i);
            }
            End();
        }
        catch (XWorkBudgetExhausted)
        {
            AbandonPrimitive();
            throw;
        }
    }

    /// <summary>网格太大、工作量预算花光了:丢掉画到一半的图元(EvalMesh 自己开的 Begin 不能留给之后的命令)。</summary>
    private void AbandonPrimitive()
    {
        _primitiveMode = uint.MaxValue;
        _primitive.Clear();
    }

    /// <summary>EvalMesh2:FILL → 每行一条 QUAD_STRIP,LINE → 行、列各一组 LINE_STRIP,POINT → POINTS;别的模式记 INVALID_ENUM。</summary>
    private void EvalMesh2(uint mode, int i1, int i2, int j1, int j2)
    {
        if (mode is not (GlEnum.FILL or GlEnum.LINE or GlEnum.POINT))
        {
            SetError(GlEnum.INVALID_ENUM);
            return;
        }
        if (!EnabledMap(2, MapVertex3) && !EnabledMap(2, MapVertex4))
        {
            return;
        }
        try
        {
            EvalMesh2Core(mode, i1, i2, j1, j2);
        }
        catch (XWorkBudgetExhausted)
        {
            AbandonPrimitive();
            throw;
        }
    }

    private void EvalMesh2Core(uint mode, int i1, int i2, int j1, int j2)
    {
        switch (mode)
        {
            case GlEnum.FILL:
                for (long j = j1; j < j2; j++)
                {
                    Begin(GlEnum.QUAD_STRIP);
                    for (long i = i1; i <= i2; i++)
                    {
                        EvalPoint2((int)i, (int)j);
                        EvalPoint2((int)i, (int)j + 1);
                    }
                    End();
                }
                break;
            case GlEnum.LINE:
                for (long j = j1; j <= j2; j++)
                {
                    Begin(GlEnum.LINE_STRIP);
                    for (long i = i1; i <= i2; i++)
                    {
                        EvalPoint2((int)i, (int)j);
                    }
                    End();
                }
                for (long i = i1; i <= i2; i++)
                {
                    Begin(GlEnum.LINE_STRIP);
                    for (long j = j1; j <= j2; j++)
                    {
                        EvalPoint2((int)i, (int)j);
                    }
                    End();
                }
                break;
            default:   // POINT(模式已经在 EvalMesh2 里核对过)
                Begin(GlEnum.POINTS);
                for (long j = j1; j <= j2; j++)
                {
                    for (long i = i1; i <= i2; i++)
                    {
                        EvalPoint2((int)i, (int)j);
                    }
                }
                End();
                break;
        }
    }

    /// <summary>EvalCoord1:启用的一维图在 u 处求值,当作颜色 / 法线 / 纹理坐标 / 顶点发出去(不改当前值)。</summary>
    private void EvalCoord1(float u)
    {
        int vertex = EnabledMap(1, MapVertex4) ? MapVertex4 : EnabledMap(1, MapVertex3) ? MapVertex3 : -1;
        if (vertex < 0)
        {
            return;   // 没有顶点图启用:什么也不做
        }
        Span<float> value = stackalloc float[4];
        Vector4? color = EnabledMap(1, MapColor) ? Evaluate1(Map(1, MapColor), u, value).ToColor() : null;
        Vector3? normal = EnabledMap(1, MapNormal) ? Evaluate1(Map(1, MapNormal), u, value).ToNormal() : null;
        Vector4? tex = HighestTextureMap(1) is int t and >= 0 ? Evaluate1(Map(1, t), u, value).ToTexCoord(MapComponents[t]) : null;
        Vector4 position = Evaluate1(Map(1, vertex), u, value).ToPosition(MapComponents[vertex]);
        EmitEvaluated(position, color, normal, tex);
    }

    /// <summary>EvalCoord2:启用的二维图在 (u, v) 处求值;AUTO_NORMAL 打开时法线由顶点图的两个偏导数叉乘得出。</summary>
    private void EvalCoord2(float u, float v)
    {
        int vertex = EnabledMap(2, MapVertex4) ? MapVertex4 : EnabledMap(2, MapVertex3) ? MapVertex3 : -1;
        if (vertex < 0)
        {
            return;
        }
        Span<float> value = stackalloc float[4];
        Vector4? color = EnabledMap(2, MapColor) ? Evaluate2(Map(2, MapColor), u, v, value, default, default).ToColor() : null;
        Vector4? tex = HighestTextureMap(2) is int t and >= 0 ? Evaluate2(Map(2, t), u, v, value, default, default).ToTexCoord(MapComponents[t]) : null;
        Vector3? normal = EnabledMap(2, MapNormal) ? Evaluate2(Map(2, MapNormal), u, v, value, default, default).ToNormal() : null;
        Span<float> du = stackalloc float[4], dv = stackalloc float[4];
        bool autoNormal = State.Enabled.Has(GlEnum.AUTO_NORMAL);
        GlMap map = Map(2, vertex);
        Vector4 position = Evaluate2(map, u, v, value, autoNormal ? du : default, autoNormal ? dv : default).ToPosition(MapComponents[vertex]);
        if (autoNormal)
        {
            normal = AnalyticNormal(position, new EvalValue(du).ToPosition(map.K), new EvalValue(dv).ToPosition(map.K), map.K == 4);
        }
        EmitEvaluated(position, color, normal, tex);
    }

    /// <summary>∂q/∂u × ∂q/∂v 归一化;VERTEX_4 时 q = (x/w, y/w, z/w),按商的求导法则换算。长度为 0 的照样发(规范允许)。</summary>
    private static Vector3 AnalyticNormal(Vector4 p, Vector4 pu, Vector4 pv, bool rational)
    {
        Vector3 qu, qv;
        if (rational && p.W != 0)
        {
            Vector3 xyz = new(p.X, p.Y, p.Z);
            qu = ((new Vector3(pu.X, pu.Y, pu.Z) * p.W) - (xyz * pu.W)) / (p.W * p.W);
            qv = ((new Vector3(pv.X, pv.Y, pv.Z) * p.W) - (xyz * pv.W)) / (p.W * p.W);
        }
        else
        {
            (qu, qv) = (new Vector3(pu.X, pu.Y, pu.Z), new Vector3(pv.X, pv.Y, pv.Z));
        }
        Vector3 m = Vector3.Cross(qu, qv);
        float length = m.Length();
        return length > 0 ? m / length : m;
    }

    /// <summary>
    /// 把求值的结果当作对应的命令发出去:颜色、法线、纹理坐标只给这一个顶点用,发完换回原来的当前值(规范:求值不改当前值;
    /// ColorMaterial 打开时求出的颜色照样影响光照,但跟踪的材质参数也不留下改动)。
    /// </summary>
    private void EmitEvaluated(Vector4 position, Vector4? color, Vector3? normal, Vector4? tex)
    {
        if (!InBeginEnd)
        {
            return;   // 顶点在 Begin / End 之外没有作用(§2.6)
        }
        WorkBudget.Charge(VertexWork);
        (Vector4 savedColor, Vector3 savedNormal, Vector4 savedTex) = (State.Color, State.Normal, State.TexCoord);
        GlMaterial? front = null, back = null;
        if (color is { } c)
        {
            if (State.Enabled.Has(GlEnum.COLOR_MATERIAL))
            {
                (front, back) = (State.FrontMaterial.Clone(), State.BackMaterial.Clone());
            }
            SetCurrentColor(c);
        }
        if (normal is { } n)
        {
            State.Normal = n;
        }
        if (tex is { } t)
        {
            State.TexCoord = t;
        }
        Vertex(position);
        (State.Color, State.Normal, State.TexCoord) = (savedColor, savedNormal, savedTex);
        if (front is not null)
        {
            (State.FrontMaterial, State.BackMaterial) = (front, back!);
        }
    }

    private bool EnabledMap(int dimension, int index) =>
        State.Enabled.Has((dimension == 1 ? GlEnum.MAP1_COLOR_4 : GlEnum.MAP2_COLOR_4) + (uint)index);

    /// <summary>启用的纹理坐标图里坐标数最多的那个(TEXTURE_COORD_4 优先);一个都没有为 −1。</summary>
    private int HighestTextureMap(int dimension)
    {
        for (int index = MapTexture1 + 3; index >= MapTexture1; index--)
        {
            if (EnabledMap(dimension, index))
            {
                return index;
            }
        }
        return -1;
    }

    /// <summary>一维图在 u 处的值:t = (u − u1) / (u2 − u1),p(t) = Σ Bᵢⁿ(t)·Rᵢ。</summary>
    private static EvalValue Evaluate1(GlMap map, float u, Span<float> value)
    {
        Span<float> basis = stackalloc float[MaxEvalOrder];
        Bernstein(map.UOrder, (u - map.U1) / (map.U2 - map.U1), basis, null);
        value.Clear();
        for (int i = 0; i < map.UOrder; i++)
        {
            for (int c = 0; c < map.K; c++)
            {
                value[c] += basis[i] * map.Points[(i * map.K) + c];
            }
        }
        return new EvalValue(value);
    }

    /// <summary>二维图在 (u, v) 处的值;要偏导数时一并算出(对 u、v 本身求导,已除以定义域的宽度)。</summary>
    private static EvalValue Evaluate2(GlMap map, float u, float v, Span<float> value, Span<float> du, Span<float> dv)
    {
        float su = map.U2 - map.U1, sv = map.V2 - map.V1;
        Span<float> bu = stackalloc float[MaxEvalOrder], bv = stackalloc float[MaxEvalOrder];
        Span<float> dbu = stackalloc float[MaxEvalOrder], dbv = stackalloc float[MaxEvalOrder];
        bool derivatives = !du.IsEmpty;
        Bernstein(map.UOrder, (u - map.U1) / su, bu, derivatives ? dbu : null);
        Bernstein(map.VOrder, (v - map.V1) / sv, bv, derivatives ? dbv : null);
        value.Clear();
        du.Clear();
        dv.Clear();
        for (int i = 0; i < map.UOrder; i++)
        {
            for (int j = 0; j < map.VOrder; j++)
            {
                int at = ((i * map.VOrder) + j) * map.K;
                float w = bu[i] * bv[j];
                for (int c = 0; c < map.K; c++)
                {
                    float point = map.Points[at + c];
                    value[c] += w * point;
                    if (derivatives)
                    {
                        du[c] += dbu[i] / su * bv[j] * point;
                        dv[c] += bu[i] * dbv[j] / sv * point;
                    }
                }
            }
        }
        return new EvalValue(value);
    }

    /// <summary>
    /// order 个 Bernstein 基函数 Bᵢⁿ(t)(n = order − 1)在 t 处的值;<paramref name="derivative" /> 不为空时还给出 dBᵢⁿ/dt
    /// = n·(Bᵢ₋₁ⁿ⁻¹ − Bᵢⁿ⁻¹)。用 de Casteljau 式的递推,不算阶乘。
    /// </summary>
    private static void Bernstein(int order, float t, Span<float> basis, Span<float> derivative)
    {
        int n = order - 1;
        Span<float> lower = stackalloc float[MaxEvalOrder];
        basis[0] = 1;
        for (int degree = 1; degree <= n; degree++)
        {
            if (degree == n && !derivative.IsEmpty)
            {
                basis[..degree].CopyTo(lower);   // 次数 n − 1 的那一组:求导用
            }
            float previous = 0;
            for (int i = 0; i < degree; i++)
            {
                float b = basis[i];
                basis[i] = previous + ((1 - t) * b);
                previous = t * b;
            }
            basis[degree] = previous;
        }
        if (derivative.IsEmpty)
        {
            return;
        }
        for (int i = 0; i <= n; i++)
        {
            derivative[i] = n == 0 ? 0 : n * ((i > 0 ? lower[i - 1] : 0) - (i < n ? lower[i] : 0));
        }
    }

    /// <summary>求值的结果(至多 4 个分量),按用途换成颜色、法线、纹理坐标或位置。</summary>
    private readonly ref struct EvalValue(Span<float> values)
    {
        private readonly Span<float> _values = values;

        public Vector4 ToColor() => new(_values[0], _values[1], _values[2], _values[3]);

        public Vector3 ToNormal() => new(_values[0], _values[1], _values[2]);

        /// <summary>纹理坐标:k 个分量,其余按 (s, t, r, q) 的默认值 0、0、1 补。</summary>
        public Vector4 ToTexCoord(int k) => new(_values[0], k > 1 ? _values[1] : 0, k > 2 ? _values[2] : 0, k > 3 ? _values[3] : 1);

        /// <summary>顶点:VERTEX_3 补 w = 1。</summary>
        public Vector4 ToPosition(int k) => new(_values[0], _values[1], _values[2], k == 4 ? _values[3] : 1);
    }

    /// <summary>GetMap{d,f,i}v(§6.1.9):COEFF(控制点)、ORDER、DOMAIN;目标或查询不认识记 INVALID_ENUM、返回 null。</summary>
    public GlValue? GetMap(uint target, uint query)
    {
        int dimension = target is >= GlEnum.MAP1_COLOR_4 and <= GlEnum.MAP1_VERTEX_4 ? 1
            : target is >= GlEnum.MAP2_COLOR_4 and <= GlEnum.MAP2_VERTEX_4 ? 2 : 0;
        if (dimension == 0 || query is not (0x0A00 or 0x0A01 or 0x0A02))
        {
            SetError(GlEnum.INVALID_ENUM);
            return null;
        }
        GlMap map = Map(dimension, (int)(target - (dimension == 1 ? GlEnum.MAP1_COLOR_4 : GlEnum.MAP2_COLOR_4)));
        return query switch
        {
            0x0A00 => new GlValue([.. map.Points.Select(p => (double)p)]),                                     // COEFF
            0x0A01 => dimension == 1 ? new GlValue([map.UOrder]) : new GlValue([map.UOrder, map.VOrder]),     // ORDER
            _ => dimension == 1 ? new GlValue([map.U1, map.U2]) : new GlValue([map.U1, map.U2, map.V1, map.V2]),   // DOMAIN
        };
    }
}

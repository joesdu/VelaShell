// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   The OpenGL Graphics System, Version 1.5 —— §6.2「State Tables」(各状态的初值与所属属性组)、
//   Table 2.10「Summary of lighting parameters」(光源、材质、光照模型的初值;LIGHT0 的漫反射与镜面为 1,其余光源为 0)、
//   §6.1.14「Saving and Restoring State」(PushAttrib / PopAttrib 按属性组的位掩码存取)。

using System.Numerics;

namespace VelaShell.XServer.Gl;

/// <summary>
/// 能 Enable / Disable 的开关,一个开关一位:认识的开关一共 62 个(见 <see cref="BitOf" />),装得进一个 ulong。
/// 原先是 <c>HashSet&lt;uint&gt;</c>:每个顶点十几次哈希查找,PushAttrib 每次复制一份集合(xs_plan GL-P1 / P3)。
/// </summary>
internal struct GlCaps
{
    // 位的布局:0–7 LIGHT0–7、8–13 CLIP_PLANE0–5、14–17 TEXTURE_GEN_S–Q、18–26 MAP1_*、27–35 MAP2_*,其余单个的开关从 36 起。
    private const int LightBase = 0, ClipPlaneBase = 8, TexGenBase = 14;

    public ulong Bits;

    /// <summary>开关在位图里的位置;不认识的开关为 −1。</summary>
    public static int BitOf(uint cap) => cap switch
    {
        >= GlEnum.LIGHT0 and < GlEnum.LIGHT0 + GlContext.MaxLights => LightBase + (int)(cap - GlEnum.LIGHT0),
        >= GlEnum.CLIP_PLANE0 and < GlEnum.CLIP_PLANE0 + GlContext.MaxClipPlanes => ClipPlaneBase + (int)(cap - GlEnum.CLIP_PLANE0),
        >= GlEnum.TEXTURE_GEN_S and <= GlEnum.TEXTURE_GEN_Q => TexGenBase + (int)(cap - GlEnum.TEXTURE_GEN_S),
        >= GlEnum.MAP1_COLOR_4 and <= GlEnum.MAP1_VERTEX_4 => 18 + (int)(cap - GlEnum.MAP1_COLOR_4),
        >= GlEnum.MAP2_COLOR_4 and <= GlEnum.MAP2_VERTEX_4 => 27 + (int)(cap - GlEnum.MAP2_COLOR_4),
        GlEnum.POINT_SMOOTH => 36,
        GlEnum.LINE_SMOOTH => 37,
        GlEnum.LINE_STIPPLE => 38,
        GlEnum.POLYGON_SMOOTH => 39,
        GlEnum.POLYGON_STIPPLE => 40,
        GlEnum.CULL_FACE => 41,
        GlEnum.LIGHTING => 42,
        GlEnum.COLOR_MATERIAL => 43,
        GlEnum.FOG => 44,
        GlEnum.DEPTH_TEST => 45,
        GlEnum.STENCIL_TEST => 46,
        GlEnum.NORMALIZE => 47,
        GlEnum.ALPHA_TEST => 48,
        GlEnum.DITHER => 49,
        GlEnum.BLEND => 50,
        GlEnum.INDEX_LOGIC_OP => 51,
        GlEnum.COLOR_LOGIC_OP => 52,
        GlEnum.SCISSOR_TEST => 53,
        GlEnum.TEXTURE_1D => 54,
        GlEnum.TEXTURE_2D => 55,
        GlEnum.AUTO_NORMAL => 56,
        GlEnum.POLYGON_OFFSET_FILL => 57,
        GlEnum.POLYGON_OFFSET_LINE => 58,
        GlEnum.POLYGON_OFFSET_POINT => 59,
        GlEnum.RESCALE_NORMAL => 60,
        GlEnum.MULTISAMPLE => 61,
        _ => -1,
    };

    /// <summary>这些开关合成的位掩码(PopAttrib 按属性组恢复开关用)。</summary>
    public static ulong MaskOf(params ReadOnlySpan<uint> caps)
    {
        ulong mask = 0;
        foreach (uint cap in caps)
        {
            mask |= 1UL << BitOf(cap);
        }
        return mask;
    }

    public readonly bool Has(uint cap) => BitOf(cap) is >= 0 and var bit && (Bits & (1UL << bit)) != 0;

    /// <summary>打开或关上一个开关;不认识的开关不理(调用方先用 <see cref="BitOf" /> 核过)。</summary>
    public void Set(uint cap, bool on)
    {
        if (BitOf(cap) is >= 0 and var bit)
        {
            Bits = on ? Bits | (1UL << bit) : Bits & ~(1UL << bit);
        }
    }

    public readonly bool HasLight(int i) => (Bits & (1UL << (LightBase + i))) != 0;

    public readonly bool HasTexGen(int i) => (Bits & (1UL << (TexGenBase + i))) != 0;

    /// <summary>打开了的用户裁剪面:第 i 位是 CLIP_PLANE0 + i。</summary>
    public readonly int ClipPlaneMask => (int)((Bits >> ClipPlaneBase) & ((1UL << GlContext.MaxClipPlanes) - 1));

    /// <summary>打开了的光源:第 i 位是 LIGHT0 + i。</summary>
    public readonly int LightMask => (int)((Bits >> LightBase) & ((1UL << GlContext.MaxLights) - 1));

    /// <summary>打开了几个开关。</summary>
    public readonly int Count => BitOperations.PopCount(Bits);
}

/// <summary>一个光源的参数(光照计算用眼坐标:位置与聚光方向在设定时按当时的模型视图矩阵变换)。</summary>
internal sealed class GlLight
{
    public Vector4 Ambient = new(0, 0, 0, 1);
    public Vector4 Diffuse;
    public Vector4 Specular;
    public Vector4 Position = new(0, 0, 1, 0);
    public Vector3 SpotDirection = new(0, 0, -1);
    public float SpotExponent;
    public float SpotCutoff = 180;
    public float ConstantAttenuation = 1;
    public float LinearAttenuation;
    public float QuadraticAttenuation;

    public GlLight(int index)
    {
        Diffuse = index == 0 ? Vector4.One : new Vector4(0, 0, 0, 1);
        Specular = index == 0 ? Vector4.One : new Vector4(0, 0, 0, 1);
    }

    public GlLight Clone() => (GlLight)MemberwiseClone();
}

/// <summary>一面(正面或背面)的材质。</summary>
internal sealed class GlMaterial
{
    public Vector4 Ambient = new(0.2f, 0.2f, 0.2f, 1);
    public Vector4 Diffuse = new(0.8f, 0.8f, 0.8f, 1);
    public Vector4 Specular = new(0, 0, 0, 1);
    public Vector4 Emission = new(0, 0, 0, 1);
    public float Shininess;

    public GlMaterial Clone() => (GlMaterial)MemberwiseClone();
}

/// <summary>
/// PushAttrib 能存取的全部状态(矩阵栈不在内,那是 PushMatrix 的事)。字段按规范 §6.2 的属性组分块。
/// </summary>
internal sealed class GlState
{
    // CURRENT_BIT
    public Vector4 Color = Vector4.One;
    public Vector4 SecondaryColor = new(0, 0, 0, 1);
    public Vector3 Normal = new(0, 0, 1);
    public Vector4 TexCoord = new(0, 0, 0, 1);
    public bool EdgeFlag = true;
    public Vector4 RasterPos = new(0, 0, 0, 1);
    public bool RasterValid = true;
    public Vector4 RasterColor = Vector4.One;
    public Vector4 RasterTexCoord = new(0, 0, 0, 1);
    public float RasterDistance;

    // ENABLE_BIT(各组的开关都在这一张位图里;PopAttrib 按组挑)
    public GlCaps Enabled = new() { Bits = GlCaps.MaskOf(GlEnum.DITHER, GlEnum.MULTISAMPLE) };

    // VIEWPORT_BIT
    public int ViewportX, ViewportY, ViewportWidth, ViewportHeight;
    public double DepthNear, DepthFar = 1;

    // SCISSOR_BIT
    public int ScissorX, ScissorY, ScissorWidth, ScissorHeight;

    // COLOR_BUFFER_BIT
    public Vector4 ClearColor;
    public bool[] ColorMask = [true, true, true, true];
    public uint AlphaFunc = GlEnum.ALWAYS;
    public float AlphaRef;
    public uint BlendSrcRgb = GlEnum.ONE, BlendDstRgb = GlEnum.ZERO, BlendSrcAlpha = GlEnum.ONE, BlendDstAlpha = GlEnum.ZERO;
    public uint BlendEquation = GlEnum.FUNC_ADD;
    public Vector4 BlendColor;
    public uint LogicOp = 0x1503;   // COPY
    public uint DrawBuffer;

    // DEPTH_BUFFER_BIT
    public uint DepthFunc = GlEnum.LESS;
    public bool DepthMask = true;
    public double ClearDepth = 1;

    // STENCIL_BUFFER_BIT
    public uint StencilFunc = GlEnum.ALWAYS;
    public int StencilRef;
    public uint StencilValueMask = 0xFF;
    public uint StencilFail = GlEnum.KEEP, StencilDepthFail = GlEnum.KEEP, StencilDepthPass = GlEnum.KEEP;
    public uint StencilWriteMask = 0xFF;
    public int ClearStencil;

    // POLYGON_BIT
    public uint CullFaceMode = GlEnum.BACK;
    public uint FrontFace = GlEnum.CCW;
    public uint PolygonModeFront = GlEnum.FILL, PolygonModeBack = GlEnum.FILL;
    public float PolygonOffsetFactor, PolygonOffsetUnits;

    // LIGHTING_BIT
    public uint ShadeModel = GlEnum.SMOOTH;
    public GlLight[] Lights = [.. Enumerable.Range(0, GlContext.MaxLights).Select(i => new GlLight(i))];
    public GlMaterial FrontMaterial = new();
    public GlMaterial BackMaterial = new();
    public Vector4 LightModelAmbient = new(0.2f, 0.2f, 0.2f, 1);
    public bool LightModelLocalViewer;
    public bool LightModelTwoSide;
    public uint LightModelColorControl = GlEnum.SINGLE_COLOR;
    public uint ColorMaterialFace = GlEnum.FRONT_AND_BACK;
    public uint ColorMaterialMode = GlEnum.AMBIENT_AND_DIFFUSE;

    // FOG_BIT
    public uint FogMode = GlEnum.EXP;
    public float FogDensity = 1, FogStart, FogEnd = 1;
    public Vector4 FogColor;

    // POINT_BIT / LINE_BIT
    public float PointSize = 1;
    public float LineWidth = 1;

    // LINE_BIT:线的点画(§3.4.2;关着时等于全 1 的图样)
    public int LineStippleFactor = 1;
    public ushort LineStipplePattern = 0xFFFF;

    // POLYGON_STIPPLE_BIT:多边形的点画(§3.5.2),32 行、每行 32 位,第 0 行是窗口 y mod 32 = 0 那一行,第 x 位(从最低位数)是 x mod 32
    public uint[] PolygonStipple = [.. Enumerable.Repeat(uint.MaxValue, 32)];

    // TRANSFORM_BIT
    public uint MatrixMode = GlEnum.MODELVIEW;
    public Vector4[] ClipPlanes = new Vector4[GlContext.MaxClipPlanes];

    // TEXTURE_BIT
    public uint Texture1D, Texture2D;
    public uint TexEnvMode = GlEnum.MODULATE;
    public Vector4 TexEnvColor;
    public uint[] TexGenMode = [GlEnum.EYE_LINEAR, GlEnum.EYE_LINEAR, GlEnum.EYE_LINEAR, GlEnum.EYE_LINEAR];
    public Vector4[] TexGenObjectPlane = [new(1, 0, 0, 0), new(0, 1, 0, 0), Vector4.Zero, Vector4.Zero];
    public Vector4[] TexGenEyePlane = [new(1, 0, 0, 0), new(0, 1, 0, 0), Vector4.Zero, Vector4.Zero];

    // PIXEL_MODE_BIT
    public uint ReadBuffer;
    public float ZoomX = 1, ZoomY = 1;

    // LIST_BIT
    public uint ListBase;

    // PushAttrib / PopAttrib 的属性组位(§6.1.14)。
    private const uint CurrentBit = 0x1, PointBit = 0x2, LineBit = 0x4, PolygonBit = 0x8, PolygonStippleBit = 0x10, PixelModeBit = 0x20, LightingBit = 0x40,
        FogBit = 0x80, DepthBit = 0x100, StencilBit = 0x400, ViewportBit = 0x800, TransformBit = 0x1000, EnableBit = 0x2000,
        ColorBufferBit = 0x4000, EvalBit = 0x10000, ListBit = 0x20000, TextureBit = 0x40000, ScissorBit = 0x80000;

    /// <summary>
    /// 给 PushAttrib / CopyContext 存一份:值类型的状态整块照抄(一次 MemberwiseClone),引用类型的(光源与材质、颜色掩码、
    /// 裁剪面、TexGen 的数组)只有 <paramref name="mask" /> 选中的组才深拷。<see cref="Restore" /> 只从存档里读选中的组,
    /// 没选中的组与当前状态共用对象也读不到 —— 原先不看 mask 整份深拷(八个光源、两份材质、几个数组),每次两 KB 多。
    /// </summary>
    public GlState Snapshot(uint mask)
    {
        var copy = (GlState)MemberwiseClone();
        if ((mask & ColorBufferBit) != 0)
        {
            copy.ColorMask = (bool[])ColorMask.Clone();
        }
        if ((mask & LightingBit) != 0)
        {
            copy.Lights = CloneLights(Lights);
            copy.FrontMaterial = FrontMaterial.Clone();
            copy.BackMaterial = BackMaterial.Clone();
        }
        if ((mask & TransformBit) != 0)
        {
            copy.ClipPlanes = (Vector4[])ClipPlanes.Clone();
        }
        if ((mask & TextureBit) != 0)
        {
            copy.TexGenMode = (uint[])TexGenMode.Clone();
            copy.TexGenObjectPlane = (Vector4[])TexGenObjectPlane.Clone();
            copy.TexGenEyePlane = (Vector4[])TexGenEyePlane.Clone();
        }
        return copy;
    }

    private static GlLight[] CloneLights(GlLight[] lights)
    {
        var copy = new GlLight[lights.Length];
        for (int i = 0; i < lights.Length; i++)
        {
            copy[i] = lights[i].Clone();
        }
        return copy;
    }

    /// <summary>PopAttrib:把 <paramref name="saved" /> 里 <paramref name="mask" /> 选中的属性组写回本对象。</summary>
    public void Restore(GlState saved, uint mask)
    {

        if ((mask & CurrentBit) != 0)
        {
            (Color, SecondaryColor, Normal, TexCoord, EdgeFlag) = (saved.Color, saved.SecondaryColor, saved.Normal, saved.TexCoord, saved.EdgeFlag);
            (RasterPos, RasterValid, RasterColor, RasterTexCoord, RasterDistance) =
                (saved.RasterPos, saved.RasterValid, saved.RasterColor, saved.RasterTexCoord, saved.RasterDistance);
        }
        if ((mask & EnableBit) != 0)
        {
            Enabled = saved.Enabled;
        }
        else
        {
            // 各组自己的开关随组恢复。
            ulong caps = 0;
            caps |= (mask & PointBit) != 0 ? PointCaps : 0;
            caps |= (mask & LineBit) != 0 ? LineCaps : 0;
            caps |= (mask & PolygonBit) != 0 ? PolygonCaps : 0;
            caps |= (mask & LightingBit) != 0 ? LightingCaps : 0;
            caps |= (mask & FogBit) != 0 ? FogCaps : 0;
            caps |= (mask & DepthBit) != 0 ? DepthCaps : 0;
            caps |= (mask & StencilBit) != 0 ? StencilCaps : 0;
            caps |= (mask & TransformBit) != 0 ? TransformCaps : 0;
            caps |= (mask & ColorBufferBit) != 0 ? ColorBufferCaps : 0;
            caps |= (mask & TextureBit) != 0 ? TextureCaps : 0;
            caps |= (mask & ScissorBit) != 0 ? ScissorCaps : 0;
            caps |= (mask & EvalBit) != 0 ? EvalCaps : 0;
            Enabled.Bits = (Enabled.Bits & ~caps) | (saved.Enabled.Bits & caps);
        }
        if ((mask & PointBit) != 0)
        {
            PointSize = saved.PointSize;
        }
        if ((mask & LineBit) != 0)
        {
            (LineWidth, LineStippleFactor, LineStipplePattern) = (saved.LineWidth, saved.LineStippleFactor, saved.LineStipplePattern);
        }
        if ((mask & PolygonBit) != 0)
        {
            (CullFaceMode, FrontFace, PolygonModeFront, PolygonModeBack, PolygonOffsetFactor, PolygonOffsetUnits) =
                (saved.CullFaceMode, saved.FrontFace, saved.PolygonModeFront, saved.PolygonModeBack, saved.PolygonOffsetFactor, saved.PolygonOffsetUnits);
        }
        if ((mask & PolygonStippleBit) != 0)
        {
            PolygonStipple = saved.PolygonStipple;   // PolygonStipple 命令总是换一个新数组,不改旧的:共用不会串
        }
        if ((mask & PixelModeBit) != 0)
        {
            (ReadBuffer, ZoomX, ZoomY) = (saved.ReadBuffer, saved.ZoomX, saved.ZoomY);
        }
        if ((mask & LightingBit) != 0)
        {
            ShadeModel = saved.ShadeModel;
            Lights = CloneLights(saved.Lights);
            FrontMaterial = saved.FrontMaterial.Clone();
            BackMaterial = saved.BackMaterial.Clone();
            (LightModelAmbient, LightModelLocalViewer, LightModelTwoSide, LightModelColorControl) =
                (saved.LightModelAmbient, saved.LightModelLocalViewer, saved.LightModelTwoSide, saved.LightModelColorControl);
            (ColorMaterialFace, ColorMaterialMode) = (saved.ColorMaterialFace, saved.ColorMaterialMode);
        }
        if ((mask & FogBit) != 0)
        {
            (FogMode, FogDensity, FogStart, FogEnd, FogColor) = (saved.FogMode, saved.FogDensity, saved.FogStart, saved.FogEnd, saved.FogColor);
        }
        if ((mask & DepthBit) != 0)
        {
            (DepthFunc, DepthMask, ClearDepth) = (saved.DepthFunc, saved.DepthMask, saved.ClearDepth);
        }
        if ((mask & StencilBit) != 0)
        {
            (StencilFunc, StencilRef, StencilValueMask, StencilWriteMask, ClearStencil) =
                (saved.StencilFunc, saved.StencilRef, saved.StencilValueMask, saved.StencilWriteMask, saved.ClearStencil);
            (StencilFail, StencilDepthFail, StencilDepthPass) = (saved.StencilFail, saved.StencilDepthFail, saved.StencilDepthPass);
        }
        if ((mask & ViewportBit) != 0)
        {
            (ViewportX, ViewportY, ViewportWidth, ViewportHeight, DepthNear, DepthFar) =
                (saved.ViewportX, saved.ViewportY, saved.ViewportWidth, saved.ViewportHeight, saved.DepthNear, saved.DepthFar);
        }
        if ((mask & TransformBit) != 0)
        {
            MatrixMode = saved.MatrixMode;
            ClipPlanes = (Vector4[])saved.ClipPlanes.Clone();
        }
        if ((mask & ColorBufferBit) != 0)
        {
            (ClearColor, ColorMask, AlphaFunc, AlphaRef) = (saved.ClearColor, (bool[])saved.ColorMask.Clone(), saved.AlphaFunc, saved.AlphaRef);
            (BlendSrcRgb, BlendDstRgb, BlendSrcAlpha, BlendDstAlpha, BlendEquation, BlendColor) =
                (saved.BlendSrcRgb, saved.BlendDstRgb, saved.BlendSrcAlpha, saved.BlendDstAlpha, saved.BlendEquation, saved.BlendColor);
            (LogicOp, DrawBuffer) = (saved.LogicOp, saved.DrawBuffer);
        }
        if ((mask & ListBit) != 0)
        {
            ListBase = saved.ListBase;
        }
        if ((mask & TextureBit) != 0)
        {
            (Texture1D, Texture2D, TexEnvMode, TexEnvColor) = (saved.Texture1D, saved.Texture2D, saved.TexEnvMode, saved.TexEnvColor);
            TexGenMode = (uint[])saved.TexGenMode.Clone();
            TexGenObjectPlane = (Vector4[])saved.TexGenObjectPlane.Clone();
            TexGenEyePlane = (Vector4[])saved.TexGenEyePlane.Clone();
        }
        if ((mask & ScissorBit) != 0)
        {
            (ScissorX, ScissorY, ScissorWidth, ScissorHeight) = (saved.ScissorX, saved.ScissorY, saved.ScissorWidth, saved.ScissorHeight);
        }
    }

    // 各属性组自带的开关(§6.2 的表里归在这一组的那些)。
    private static readonly ulong PointCaps = GlCaps.MaskOf(GlEnum.POINT_SMOOTH);
    private static readonly ulong LineCaps = GlCaps.MaskOf(GlEnum.LINE_SMOOTH, GlEnum.LINE_STIPPLE);
    private static readonly ulong PolygonCaps = GlCaps.MaskOf(GlEnum.CULL_FACE, GlEnum.POLYGON_SMOOTH, GlEnum.POLYGON_STIPPLE,
        GlEnum.POLYGON_OFFSET_FILL, GlEnum.POLYGON_OFFSET_LINE, GlEnum.POLYGON_OFFSET_POINT);
    private static readonly ulong LightingCaps = GlCaps.MaskOf(GlEnum.LIGHTING, GlEnum.COLOR_MATERIAL, GlEnum.LIGHT0, GlEnum.LIGHT0 + 1,
        GlEnum.LIGHT0 + 2, GlEnum.LIGHT0 + 3, GlEnum.LIGHT0 + 4, GlEnum.LIGHT0 + 5, GlEnum.LIGHT0 + 6, GlEnum.LIGHT0 + 7);
    private static readonly ulong FogCaps = GlCaps.MaskOf(GlEnum.FOG);
    private static readonly ulong DepthCaps = GlCaps.MaskOf(GlEnum.DEPTH_TEST);
    private static readonly ulong StencilCaps = GlCaps.MaskOf(GlEnum.STENCIL_TEST);
    private static readonly ulong TransformCaps = GlCaps.MaskOf(GlEnum.NORMALIZE, GlEnum.RESCALE_NORMAL, GlEnum.CLIP_PLANE0, GlEnum.CLIP_PLANE0 + 1,
        GlEnum.CLIP_PLANE0 + 2, GlEnum.CLIP_PLANE0 + 3, GlEnum.CLIP_PLANE0 + 4, GlEnum.CLIP_PLANE0 + 5);
    private static readonly ulong ColorBufferCaps = GlCaps.MaskOf(GlEnum.ALPHA_TEST, GlEnum.BLEND, GlEnum.DITHER, GlEnum.COLOR_LOGIC_OP, GlEnum.INDEX_LOGIC_OP);
    private static readonly ulong TextureCaps = GlCaps.MaskOf(GlEnum.TEXTURE_1D, GlEnum.TEXTURE_2D, GlEnum.TEXTURE_GEN_S, GlEnum.TEXTURE_GEN_T,
        GlEnum.TEXTURE_GEN_R, GlEnum.TEXTURE_GEN_Q);
    private static readonly ulong ScissorCaps = GlCaps.MaskOf(GlEnum.SCISSOR_TEST);

    /// <summary>EVAL_BIT:求值器的开关(MAP1_* / MAP2_*)与 AUTO_NORMAL。</summary>
    private static readonly ulong EvalCaps = GlCaps.MaskOf(GlEnum.AUTO_NORMAL) | (((1UL << 18) - 1) << 18);
}

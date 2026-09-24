// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   The OpenGL Graphics System, Version 1.5 —— §6.2「State Tables」(各状态的初值与所属属性组)、
//   Table 2.10「Summary of lighting parameters」(光源、材质、光照模型的初值;LIGHT0 的漫反射与镜面为 1,其余光源为 0)、
//   §6.1.14「Saving and Restoring State」(PushAttrib / PopAttrib 按属性组的位掩码存取)。

using System.Numerics;

namespace VelaShell.XServer.Gl;

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

    // ENABLE_BIT(各组的开关都在这一个集合里;PopAttrib 按组挑)
    public HashSet<uint> Enabled = [GlEnum.DITHER, GlEnum.MULTISAMPLE];

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

    public GlState Clone()
    {
        GlState copy = (GlState)MemberwiseClone();
        copy.Enabled = [.. Enabled];
        copy.ColorMask = (bool[])ColorMask.Clone();
        copy.Lights = [.. Lights.Select(l => l.Clone())];
        copy.FrontMaterial = FrontMaterial.Clone();
        copy.BackMaterial = BackMaterial.Clone();
        copy.ClipPlanes = (Vector4[])ClipPlanes.Clone();
        copy.TexGenMode = (uint[])TexGenMode.Clone();
        copy.TexGenObjectPlane = (Vector4[])TexGenObjectPlane.Clone();
        copy.TexGenEyePlane = (Vector4[])TexGenEyePlane.Clone();
        return copy;
    }

    /// <summary>PopAttrib:把 <paramref name="saved" /> 里 <paramref name="mask" /> 选中的属性组写回本对象。</summary>
    public void Restore(GlState saved, uint mask)
    {
        const uint current = 0x1, point = 0x2, line = 0x4, polygon = 0x8, pixelMode = 0x20, lighting = 0x40,
            fog = 0x80, depth = 0x100, stencil = 0x400, viewport = 0x800, transform = 0x1000, enable = 0x2000,
            colorBuffer = 0x4000, list = 0x20000, texture = 0x40000, scissor = 0x80000;

        if ((mask & current) != 0)
        {
            (Color, SecondaryColor, Normal, TexCoord, EdgeFlag) = (saved.Color, saved.SecondaryColor, saved.Normal, saved.TexCoord, saved.EdgeFlag);
            (RasterPos, RasterValid, RasterColor, RasterTexCoord, RasterDistance) =
                (saved.RasterPos, saved.RasterValid, saved.RasterColor, saved.RasterTexCoord, saved.RasterDistance);
        }
        if ((mask & enable) != 0)
        {
            Enabled = [.. saved.Enabled];
        }
        else
        {
            // 各组自己的开关随组恢复。
            RestoreCaps(saved, mask & point, GlEnum.POINT_SMOOTH);
            RestoreCaps(saved, mask & line, GlEnum.LINE_SMOOTH, GlEnum.LINE_STIPPLE);
            RestoreCaps(saved, mask & polygon, GlEnum.CULL_FACE, GlEnum.POLYGON_SMOOTH, GlEnum.POLYGON_STIPPLE,
                GlEnum.POLYGON_OFFSET_FILL, GlEnum.POLYGON_OFFSET_LINE, GlEnum.POLYGON_OFFSET_POINT);
            RestoreCaps(saved, mask & lighting, [GlEnum.LIGHTING, GlEnum.COLOR_MATERIAL, .. Enumerable.Range(0, GlContext.MaxLights).Select(i => GlEnum.LIGHT0 + (uint)i)]);
            RestoreCaps(saved, mask & fog, GlEnum.FOG);
            RestoreCaps(saved, mask & depth, GlEnum.DEPTH_TEST);
            RestoreCaps(saved, mask & stencil, GlEnum.STENCIL_TEST);
            RestoreCaps(saved, mask & transform, [GlEnum.NORMALIZE, GlEnum.RESCALE_NORMAL, .. Enumerable.Range(0, GlContext.MaxClipPlanes).Select(i => GlEnum.CLIP_PLANE0 + (uint)i)]);
            RestoreCaps(saved, mask & colorBuffer, GlEnum.ALPHA_TEST, GlEnum.BLEND, GlEnum.DITHER, GlEnum.COLOR_LOGIC_OP, GlEnum.INDEX_LOGIC_OP);
            RestoreCaps(saved, mask & texture, GlEnum.TEXTURE_1D, GlEnum.TEXTURE_2D, GlEnum.TEXTURE_GEN_S, GlEnum.TEXTURE_GEN_T,
                GlEnum.TEXTURE_GEN_R, GlEnum.TEXTURE_GEN_Q);
            RestoreCaps(saved, mask & scissor, GlEnum.SCISSOR_TEST);
        }
        if ((mask & point) != 0)
        {
            PointSize = saved.PointSize;
        }
        if ((mask & line) != 0)
        {
            LineWidth = saved.LineWidth;
        }
        if ((mask & polygon) != 0)
        {
            (CullFaceMode, FrontFace, PolygonModeFront, PolygonModeBack, PolygonOffsetFactor, PolygonOffsetUnits) =
                (saved.CullFaceMode, saved.FrontFace, saved.PolygonModeFront, saved.PolygonModeBack, saved.PolygonOffsetFactor, saved.PolygonOffsetUnits);
        }
        if ((mask & pixelMode) != 0)
        {
            (ReadBuffer, ZoomX, ZoomY) = (saved.ReadBuffer, saved.ZoomX, saved.ZoomY);
        }
        if ((mask & lighting) != 0)
        {
            ShadeModel = saved.ShadeModel;
            Lights = [.. saved.Lights.Select(l => l.Clone())];
            FrontMaterial = saved.FrontMaterial.Clone();
            BackMaterial = saved.BackMaterial.Clone();
            (LightModelAmbient, LightModelLocalViewer, LightModelTwoSide, LightModelColorControl) =
                (saved.LightModelAmbient, saved.LightModelLocalViewer, saved.LightModelTwoSide, saved.LightModelColorControl);
            (ColorMaterialFace, ColorMaterialMode) = (saved.ColorMaterialFace, saved.ColorMaterialMode);
        }
        if ((mask & fog) != 0)
        {
            (FogMode, FogDensity, FogStart, FogEnd, FogColor) = (saved.FogMode, saved.FogDensity, saved.FogStart, saved.FogEnd, saved.FogColor);
        }
        if ((mask & depth) != 0)
        {
            (DepthFunc, DepthMask, ClearDepth) = (saved.DepthFunc, saved.DepthMask, saved.ClearDepth);
        }
        if ((mask & stencil) != 0)
        {
            (StencilFunc, StencilRef, StencilValueMask, StencilWriteMask, ClearStencil) =
                (saved.StencilFunc, saved.StencilRef, saved.StencilValueMask, saved.StencilWriteMask, saved.ClearStencil);
            (StencilFail, StencilDepthFail, StencilDepthPass) = (saved.StencilFail, saved.StencilDepthFail, saved.StencilDepthPass);
        }
        if ((mask & viewport) != 0)
        {
            (ViewportX, ViewportY, ViewportWidth, ViewportHeight, DepthNear, DepthFar) =
                (saved.ViewportX, saved.ViewportY, saved.ViewportWidth, saved.ViewportHeight, saved.DepthNear, saved.DepthFar);
        }
        if ((mask & transform) != 0)
        {
            MatrixMode = saved.MatrixMode;
            ClipPlanes = (Vector4[])saved.ClipPlanes.Clone();
        }
        if ((mask & colorBuffer) != 0)
        {
            (ClearColor, ColorMask, AlphaFunc, AlphaRef) = (saved.ClearColor, (bool[])saved.ColorMask.Clone(), saved.AlphaFunc, saved.AlphaRef);
            (BlendSrcRgb, BlendDstRgb, BlendSrcAlpha, BlendDstAlpha, BlendEquation, BlendColor) =
                (saved.BlendSrcRgb, saved.BlendDstRgb, saved.BlendSrcAlpha, saved.BlendDstAlpha, saved.BlendEquation, saved.BlendColor);
            (LogicOp, DrawBuffer) = (saved.LogicOp, saved.DrawBuffer);
        }
        if ((mask & list) != 0)
        {
            ListBase = saved.ListBase;
        }
        if ((mask & texture) != 0)
        {
            (Texture1D, Texture2D, TexEnvMode, TexEnvColor) = (saved.Texture1D, saved.Texture2D, saved.TexEnvMode, saved.TexEnvColor);
            TexGenMode = (uint[])saved.TexGenMode.Clone();
            TexGenObjectPlane = (Vector4[])saved.TexGenObjectPlane.Clone();
            TexGenEyePlane = (Vector4[])saved.TexGenEyePlane.Clone();
        }
        if ((mask & scissor) != 0)
        {
            (ScissorX, ScissorY, ScissorWidth, ScissorHeight) = (saved.ScissorX, saved.ScissorY, saved.ScissorWidth, saved.ScissorHeight);
        }
    }

    private void RestoreCaps(GlState saved, uint selected, params uint[] caps)
    {
        if (selected == 0)
        {
            return;
        }
        foreach (uint cap in caps)
        {
            if (saved.Enabled.Contains(cap))
            {
                Enabled.Add(cap);
            }
            else
            {
                Enabled.Remove(cap);
            }
        }
    }
}

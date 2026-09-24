// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   The OpenGL Graphics System, Version 1.5 —— §6.1.1「Simple Queries」(IsEnabled;能开关的状态也可经 GetBooleanv 等查)、
//   §6.1.2「Data Conversions」(GetIntegerv 取颜色、深度范围、深度清除值、法线时按 Table 4.6 的 INT 一栏换算,其余四舍五入)、
//   §6.1.3–6.1.4(GetClipPlane、GetLight、GetMaterial、GetTexEnv、GetTexGen、GetTexParameter、GetTexLevelParameter)、
//   §6.1.11「Pointer and String Queries」(VENDOR / RENDERER / VERSION / EXTENSIONS;版本串「主.次」开头)、
//   §6.2「State Tables」(各 pname 的值与个数)。

using System.Numerics;

namespace VelaShell.XServer.Gl;

internal sealed partial class GlContext
{
    /// <summary>一个查询结果:值,以及是不是「颜色 / 深度 / 法线」一类(GetIntegerv 线性映射而不是四舍五入)。</summary>
    public readonly record struct GlValue(double[] Values, bool Normalized = false);

    /// <summary>GL_VERSION:固定功能 1.1 的一个子集,见文件头列出的未实现部分。</summary>
    public const string Version = "1.1 VelaShell";

    /// <summary>真正实现了的扩展。</summary>
    public const string Extensions =
        "GL_EXT_abgr GL_EXT_bgra GL_EXT_blend_color GL_EXT_blend_minmax GL_EXT_blend_subtract GL_EXT_copy_texture "
        + "GL_EXT_packed_pixels GL_EXT_polygon_offset GL_EXT_rescale_normal GL_EXT_separate_specular_color "
        + "GL_EXT_subtexture GL_EXT_texture GL_EXT_texture_object GL_EXT_vertex_array GL_SGIS_texture_edge_clamp";

    public static string? GetString(uint name) => name switch
    {
        GlEnum.VENDOR => "VelaShell",
        GlEnum.RENDERER => "VelaShell.XServer software rasterizer",
        GlEnum.VERSION => Version,
        GlEnum.EXTENSIONS => Extensions,
        _ => null,
    };

    public bool IsEnabled(uint cap) => State.Enabled.Contains(cap);

    /// <summary>Get{Boolean,Integer,Float,Double}v 的值;不认识的 pname 记 INVALID_ENUM 并返回 null。</summary>
    public GlValue? Query(uint pname)
    {
        GlValue? v = QueryCore(pname);
        if (v is null)
        {
            SetError(GlEnum.INVALID_ENUM);
        }
        return v;
    }

    private GlValue? QueryCore(uint pname)
    {
        static GlValue One(double v) => new([v]);
        static GlValue Color(Vector4 c) => new([c.X, c.Y, c.Z, c.W], true);
        static GlValue Bool(bool b) => One(b ? 1 : 0);
        static GlValue Matrix(Matrix4x4 t) => new([.. ToColumnMajor(t).Select(f => (double)f)]);

        return pname switch
        {
            // 能开关的状态
            GlEnum.POINT_SMOOTH or GlEnum.LINE_SMOOTH or GlEnum.LINE_STIPPLE or GlEnum.POLYGON_SMOOTH or GlEnum.POLYGON_STIPPLE
                or GlEnum.CULL_FACE or GlEnum.LIGHTING or GlEnum.COLOR_MATERIAL or GlEnum.FOG or GlEnum.DEPTH_TEST
                or GlEnum.STENCIL_TEST or GlEnum.NORMALIZE or GlEnum.ALPHA_TEST or GlEnum.DITHER or GlEnum.BLEND
                or GlEnum.INDEX_LOGIC_OP or GlEnum.COLOR_LOGIC_OP or GlEnum.SCISSOR_TEST or GlEnum.TEXTURE_1D or GlEnum.TEXTURE_2D
                or GlEnum.TEXTURE_GEN_S or GlEnum.TEXTURE_GEN_T or GlEnum.TEXTURE_GEN_R or GlEnum.TEXTURE_GEN_Q
                or GlEnum.AUTO_NORMAL or GlEnum.POLYGON_OFFSET_FILL or GlEnum.POLYGON_OFFSET_LINE or GlEnum.POLYGON_OFFSET_POINT
                or GlEnum.RESCALE_NORMAL or GlEnum.MULTISAMPLE
                or (>= GlEnum.CLIP_PLANE0 and < GlEnum.CLIP_PLANE0 + MaxClipPlanes)
                or (>= GlEnum.LIGHT0 and < GlEnum.LIGHT0 + MaxLights) => Bool(State.Enabled.Contains(pname)),

            // 当前值
            0x0B00 => Color(State.Color),                                                   // CURRENT_COLOR
            0x0B02 => new GlValue([State.Normal.X, State.Normal.Y, State.Normal.Z], true),    // CURRENT_NORMAL
            0x0B03 => new GlValue([State.TexCoord.X, State.TexCoord.Y, State.TexCoord.Z, State.TexCoord.W]),   // CURRENT_TEXTURE_COORDS
            0x0B04 => Color(State.RasterColor),                                             // CURRENT_RASTER_COLOR
            0x0B06 => new GlValue([State.RasterTexCoord.X, State.RasterTexCoord.Y, State.RasterTexCoord.Z, State.RasterTexCoord.W]),
            0x0B07 => new GlValue([State.RasterPos.X, State.RasterPos.Y, State.RasterPos.Z, State.RasterPos.W]),   // CURRENT_RASTER_POSITION
            0x0B08 => Bool(State.RasterValid),                                               // CURRENT_RASTER_POSITION_VALID
            0x0B09 => One(State.RasterDistance),                                             // CURRENT_RASTER_DISTANCE
            0x0B43 => Bool(State.EdgeFlag),                                                  // EDGE_FLAG

            // 点、线、多边形
            0x0B11 => One(State.PointSize),                   // POINT_SIZE
            0x0B12 or 0x846D => new GlValue([1, 64]),         // POINT_SIZE_RANGE / ALIASED_POINT_SIZE_RANGE
            0x0B13 => One(1),                                 // POINT_SIZE_GRANULARITY
            0x0B21 => One(State.LineWidth),                   // LINE_WIDTH
            0x0B22 or 0x846E => new GlValue([1, 64]),         // LINE_WIDTH_RANGE / ALIASED_LINE_WIDTH_RANGE
            0x0B23 => One(1),                                 // LINE_WIDTH_GRANULARITY
            0x0B25 => One(0xFFFF),                            // LINE_STIPPLE_PATTERN
            0x0B26 => One(1),                                 // LINE_STIPPLE_REPEAT
            0x0B40 => new GlValue([State.PolygonModeFront, State.PolygonModeBack]),   // POLYGON_MODE
            0x0B45 => One(State.CullFaceMode),                // CULL_FACE_MODE
            0x0B46 => One(State.FrontFace),                   // FRONT_FACE
            0x8038 => One(State.PolygonOffsetFactor),         // POLYGON_OFFSET_FACTOR
            0x2A00 => One(State.PolygonOffsetUnits),          // POLYGON_OFFSET_UNITS

            // 列表
            0x0B30 => One(ListMode),                          // LIST_MODE
            0x0B31 => One(MaxListNesting),                    // MAX_LIST_NESTING
            0x0B32 => One(State.ListBase),                    // LIST_BASE
            0x0B33 => One(ListIndex),                         // LIST_INDEX

            // 光照
            0x0B51 => Bool(State.LightModelLocalViewer),      // LIGHT_MODEL_LOCAL_VIEWER
            0x0B52 => Bool(State.LightModelTwoSide),          // LIGHT_MODEL_TWO_SIDE
            0x0B53 => Color(State.LightModelAmbient),         // LIGHT_MODEL_AMBIENT
            GlEnum.LIGHT_MODEL_COLOR_CONTROL => One(State.LightModelColorControl),
            0x0B54 => One(State.ShadeModel),                  // SHADE_MODEL
            0x0B55 => One(State.ColorMaterialFace),           // COLOR_MATERIAL_FACE
            0x0B56 => One(State.ColorMaterialMode),           // COLOR_MATERIAL_PARAMETER

            // 雾
            0x0B61 => One(0),                                 // FOG_INDEX
            0x0B62 => One(State.FogDensity),
            0x0B63 => One(State.FogStart),
            0x0B64 => One(State.FogEnd),
            0x0B65 => One(State.FogMode),
            0x0B66 => Color(State.FogColor),

            // 深度、模板
            0x0B70 => new GlValue([State.DepthNear, State.DepthFar], true),   // DEPTH_RANGE
            0x0B72 => Bool(State.DepthMask),                  // DEPTH_WRITEMASK
            0x0B73 => new GlValue([State.ClearDepth], true),  // DEPTH_CLEAR_VALUE
            0x0B74 => One(State.DepthFunc),                   // DEPTH_FUNC
            0x0B91 => One(State.ClearStencil),                // STENCIL_CLEAR_VALUE
            0x0B92 => One(State.StencilFunc),                 // STENCIL_FUNC
            0x0B93 => One(State.StencilValueMask),            // STENCIL_VALUE_MASK
            0x0B94 => One(State.StencilFail),                 // STENCIL_FAIL
            0x0B95 => One(State.StencilDepthFail),            // STENCIL_PASS_DEPTH_FAIL
            0x0B96 => One(State.StencilDepthPass),            // STENCIL_PASS_DEPTH_PASS
            0x0B97 => One(State.StencilRef),                  // STENCIL_REF
            0x0B98 => One(State.StencilWriteMask),            // STENCIL_WRITEMASK

            // 矩阵
            0x0BA0 => One(State.MatrixMode),                  // MATRIX_MODE
            0x0BA2 => new GlValue([State.ViewportX, State.ViewportY, State.ViewportWidth, State.ViewportHeight]),   // VIEWPORT
            0x0BA3 => One(_modelview.Count),                  // MODELVIEW_STACK_DEPTH
            0x0BA4 => One(_projection.Count),                 // PROJECTION_STACK_DEPTH
            0x0BA5 => One(_texture.Count),                    // TEXTURE_STACK_DEPTH
            0x0BA6 => Matrix(Modelview),                      // MODELVIEW_MATRIX
            0x0BA7 => Matrix(Projection),                     // PROJECTION_MATRIX
            0x0BA8 => Matrix(TextureMatrix),                  // TEXTURE_MATRIX
            0x84E3 => Matrix(Matrix4x4.Transpose(Modelview)), // TRANSPOSE_MODELVIEW_MATRIX
            0x84E4 => Matrix(Matrix4x4.Transpose(Projection)),
            0x84E5 => Matrix(Matrix4x4.Transpose(TextureMatrix)),
            0x0BB0 => One(_attribStack.Count),                // ATTRIB_STACK_DEPTH

            // 颜色缓冲
            0x0BC1 => One(State.AlphaFunc),                   // ALPHA_TEST_FUNC
            0x0BC2 => new GlValue([State.AlphaRef], true),    // ALPHA_TEST_REF
            0x0BE0 => One(State.BlendDstRgb),                 // BLEND_DST
            0x0BE1 => One(State.BlendSrcRgb),                 // BLEND_SRC
            0x80C8 => One(State.BlendDstRgb),                 // BLEND_DST_RGB
            0x80C9 => One(State.BlendSrcRgb),                 // BLEND_SRC_RGB
            0x80CA => One(State.BlendDstAlpha),               // BLEND_DST_ALPHA
            0x80CB => One(State.BlendSrcAlpha),               // BLEND_SRC_ALPHA
            0x8009 => One(State.BlendEquation),               // BLEND_EQUATION
            0x8005 => Color(State.BlendColor),                // BLEND_COLOR
            0x0BF0 => One(State.LogicOp),                     // LOGIC_OP_MODE
            0x0C01 => One(State.DrawBuffer),                  // DRAW_BUFFER
            0x0C02 => One(State.ReadBuffer),                  // READ_BUFFER
            0x0C10 => new GlValue([State.ScissorX, State.ScissorY, State.ScissorWidth, State.ScissorHeight]),   // SCISSOR_BOX
            0x0C22 => Color(State.ClearColor),                // COLOR_CLEAR_VALUE
            0x0C23 => new GlValue([.. State.ColorMask.Select(b => b ? 1.0 : 0)]),   // COLOR_WRITEMASK
            0x0C30 => Bool(false),                            // INDEX_MODE
            0x0C31 => Bool(true),                             // RGBA_MODE
            0x0C32 => Bool(DoubleBuffered),                   // DOUBLEBUFFER
            0x0C33 => Bool(false),                            // STEREO
            0x0C00 => One(0),                                 // AUX_BUFFERS

            // 纹理
            0x2200 => One(State.TexEnvMode),
            0x8068 => One(State.Texture1D),                   // TEXTURE_BINDING_1D
            0x8069 => One(State.Texture2D),                   // TEXTURE_BINDING_2D
            0x84E0 => One(GlEnum.TEXTURE0),                   // ACTIVE_TEXTURE
            0x84E1 => One(GlEnum.TEXTURE0),                   // CLIENT_ACTIVE_TEXTURE

            // 像素
            0x0D16 => One(State.ZoomX),                       // ZOOM_X
            0x0D17 => One(State.ZoomY),                       // ZOOM_Y
            // 像素传输不实现,只报初值:MAP_COLOR / MAP_STENCIL / INDEX_SHIFT / INDEX_OFFSET 为 0,各 *_SCALE 为 1,各 *_BIAS 为 0
            0x0D10 or 0x0D11 or 0x0D12 or 0x0D13 => One(0),
            0x0D14 or 0x0D18 or 0x0D1A or 0x0D1C or 0x0D1E => One(1),
            0x0D15 or 0x0D19 or 0x0D1B or 0x0D1D or 0x0D1F => One(0),

            // 实现相关的上限
            0x0D30 => One(8),                                 // MAX_EVAL_ORDER
            0x0D31 => One(MaxLights),                         // MAX_LIGHTS
            0x0D32 => One(MaxClipPlanes),                     // MAX_CLIP_PLANES
            0x0D33 => One(MaxTextureSize),                    // MAX_TEXTURE_SIZE
            0x0D34 => One(256),                               // MAX_PIXEL_MAP_TABLE
            0x0D35 => One(MaxAttribDepth),                    // MAX_ATTRIB_STACK_DEPTH
            0x0D36 => One(MaxMatrixDepth),                    // MAX_MODELVIEW_STACK_DEPTH
            0x0D37 => One(64),                                // MAX_NAME_STACK_DEPTH
            0x0D38 => One(MaxMatrixDepth),                    // MAX_PROJECTION_STACK_DEPTH
            0x0D39 => One(MaxMatrixDepth),                    // MAX_TEXTURE_STACK_DEPTH
            0x0D3A => new GlValue([16384, 16384]),            // MAX_VIEWPORT_DIMS
            0x0D3B => One(16),                                // MAX_CLIENT_ATTRIB_STACK_DEPTH
            0x0D50 => One(4),                                 // SUBPIXEL_BITS
            0x0D51 => One(0),                                 // INDEX_BITS
            0x0D52 or 0x0D53 or 0x0D54 => One(8),             // RED_BITS / GREEN_BITS / BLUE_BITS
            0x0D55 => One(HasAlpha ? 8 : 0),                  // ALPHA_BITS
            0x0D56 => One(24),                                // DEPTH_BITS
            0x0D57 => One(8),                                 // STENCIL_BITS
            0x0D58 or 0x0D59 or 0x0D5A or 0x0D5B => One(0),   // ACCUM_*_BITS
            0x84E2 => One(1),                                 // MAX_TEXTURE_UNITS
            0x80A8 or 0x80A9 => One(0),                       // SAMPLE_BUFFERS / SAMPLES
            0x80E8 or 0x80E9 => One(65536),                   // MAX_ELEMENTS_VERTICES / INDICES
            _ => null,
        };
    }

    /// <summary>GetLight:位置与聚光方向是存下来的眼坐标值(§6.1.3)。</summary>
    public GlValue? GetLight(uint light, uint pname)
    {
        uint i = light - GlEnum.LIGHT0;
        if (i >= MaxLights)
        {
            SetError(GlEnum.INVALID_ENUM);
            return null;
        }
        GlLight l = State.Lights[i];
        GlValue? v = pname switch
        {
            GlEnum.AMBIENT => V4(l.Ambient, true),
            GlEnum.DIFFUSE => V4(l.Diffuse, true),
            GlEnum.SPECULAR => V4(l.Specular, true),
            GlEnum.POSITION => V4(l.Position, false),
            GlEnum.SPOT_DIRECTION => new GlValue([l.SpotDirection.X, l.SpotDirection.Y, l.SpotDirection.Z]),
            GlEnum.SPOT_EXPONENT => new GlValue([l.SpotExponent]),
            GlEnum.SPOT_CUTOFF => new GlValue([l.SpotCutoff]),
            GlEnum.CONSTANT_ATTENUATION => new GlValue([l.ConstantAttenuation]),
            GlEnum.LINEAR_ATTENUATION => new GlValue([l.LinearAttenuation]),
            GlEnum.QUADRATIC_ATTENUATION => new GlValue([l.QuadraticAttenuation]),
            _ => null,
        };
        if (v is null)
        {
            SetError(GlEnum.INVALID_ENUM);
        }
        return v;
    }

    public GlValue? GetMaterial(uint face, uint pname)
    {
        GlMaterial? m = face switch
        {
            GlEnum.FRONT => State.FrontMaterial,
            GlEnum.BACK => State.BackMaterial,
            _ => null,
        };
        GlValue? v = m is null ? null : pname switch
        {
            GlEnum.AMBIENT => V4(m.Ambient, true),
            GlEnum.DIFFUSE => V4(m.Diffuse, true),
            GlEnum.SPECULAR => V4(m.Specular, true),
            GlEnum.EMISSION => V4(m.Emission, true),
            GlEnum.SHININESS => new GlValue([m.Shininess]),
            GlEnum.COLOR_INDEXES => new GlValue([0, 1, 1]),
            _ => null,
        };
        if (v is null)
        {
            SetError(GlEnum.INVALID_ENUM);
        }
        return v;
    }

    public GlValue? GetTexEnv(uint target, uint pname)
    {
        GlValue? v = target != GlEnum.TEXTURE_ENV ? null : pname switch
        {
            GlEnum.TEXTURE_ENV_MODE => new GlValue([State.TexEnvMode]),
            GlEnum.TEXTURE_ENV_COLOR => V4(State.TexEnvColor, true),
            _ => null,
        };
        if (v is null)
        {
            SetError(GlEnum.INVALID_ENUM);
        }
        return v;
    }

    public GlValue? GetTexGen(uint coord, uint pname)
    {
        uint i = coord - GlEnum.S;
        GlValue? v = i > 3 ? null : pname switch
        {
            GlEnum.TEXTURE_GEN_MODE => new GlValue([State.TexGenMode[i]]),
            GlEnum.OBJECT_PLANE => V4(State.TexGenObjectPlane[i], false),
            GlEnum.EYE_PLANE => V4(State.TexGenEyePlane[i], false),
            _ => null,
        };
        if (v is null)
        {
            SetError(GlEnum.INVALID_ENUM);
        }
        return v;
    }

    public GlValue? GetTexParameter(uint target, uint pname)
    {
        GlTexture? t = target is GlEnum.TEXTURE_1D or GlEnum.TEXTURE_2D ? BoundTexture(target) : null;
        GlValue? v = t is null ? null : pname switch
        {
            GlEnum.TEXTURE_MIN_FILTER => new GlValue([t.MinFilter]),
            GlEnum.TEXTURE_MAG_FILTER => new GlValue([t.MagFilter]),
            GlEnum.TEXTURE_WRAP_S => new GlValue([t.WrapS]),
            GlEnum.TEXTURE_WRAP_T => new GlValue([t.WrapT]),
            GlEnum.TEXTURE_BORDER_COLOR => V4(t.BorderColor, true),
            GlEnum.TEXTURE_PRIORITY => new GlValue([t.Priority]),
            GlEnum.TEXTURE_RESIDENT => new GlValue([1]),
            _ => null,
        };
        if (v is null)
        {
            SetError(GlEnum.INVALID_ENUM);
        }
        return v;
    }

    public GlValue? GetTexLevelParameter(uint target, int level, uint pname)
    {
        GlTexture? t = target is GlEnum.TEXTURE_1D or GlEnum.TEXTURE_2D or GlEnum.PROXY_TEXTURE_1D or GlEnum.PROXY_TEXTURE_2D
            ? BoundTexture(target) : null;
        if (t is null || level < 0 || level >= GlTexture.MaxLevels)
        {
            SetError(t is null ? GlEnum.INVALID_ENUM : GlEnum.INVALID_VALUE);
            return null;
        }
        GlTexImage? image = t.Levels[level];
        uint baseFormat = image?.BaseFormat ?? 0;
        int Bits(bool present) => image is not null && present ? 8 : 0;
        GlValue? v = pname switch
        {
            GlEnum.TEXTURE_WIDTH => new GlValue([image?.Width ?? 0]),
            GlEnum.TEXTURE_HEIGHT => new GlValue([image?.Height ?? 0]),
            GlEnum.TEXTURE_INTERNAL_FORMAT => new GlValue([image?.InternalFormat ?? 1]),
            GlEnum.TEXTURE_BORDER => new GlValue([0]),
            GlEnum.TEXTURE_RED_SIZE or GlEnum.TEXTURE_GREEN_SIZE or GlEnum.TEXTURE_BLUE_SIZE =>
                new GlValue([Bits(baseFormat is GlEnum.RGB or GlEnum.RGBA)]),
            GlEnum.TEXTURE_ALPHA_SIZE => new GlValue([Bits(baseFormat is GlEnum.ALPHA or GlEnum.LUMINANCE_ALPHA or GlEnum.RGBA)]),
            GlEnum.TEXTURE_LUMINANCE_SIZE => new GlValue([Bits(baseFormat is GlEnum.LUMINANCE or GlEnum.LUMINANCE_ALPHA)]),
            GlEnum.TEXTURE_INTENSITY_SIZE => new GlValue([Bits(baseFormat == GlEnum.INTENSITY)]),
            _ => null,
        };
        if (v is null)
        {
            SetError(GlEnum.INVALID_ENUM);
        }
        return v;
    }

    public GlValue? GetClipPlane(uint plane)
    {
        uint i = plane - GlEnum.CLIP_PLANE0;
        if (i >= MaxClipPlanes)
        {
            SetError(GlEnum.INVALID_ENUM);
            return null;
        }
        return V4(State.ClipPlanes[i], false);
    }

    private static GlValue V4(Vector4 v, bool normalized) => new([v.X, v.Y, v.Z, v.W], normalized);
}

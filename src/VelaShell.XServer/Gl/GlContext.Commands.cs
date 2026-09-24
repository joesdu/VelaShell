// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   GLX Extensions for OpenGL Protocol Specification, Version 1.3 —— §2.3.3「GL Rendering Commands」(各渲染命令的操作码与参数布局:
//   参数按 C 函数的次序紧排;带 FLOAT64 的命令里双精度参数排在最前,例如 ClipPlane 是四个 FLOAT64 再跟 plane;
//   Lightfv / Materialfv / Fogfv / TexParameterfv / TexEnvfv / TexGendv 的参数个数由 pname 决定)、§2.3.4「GL Rendering Commands
//   That May Be Large」(CallLists、DrawArrays)、§2.3.6「GL Rendering Commands with Pixel Data」(像素存储头)。
//   操作码对照 Khronos OpenGL API Registry(gl.xml 里每条命令的 <glx type="render" opcode=…/>)。
//   The OpenGL Graphics System, Version 1.5 —— Table 2.6「Component conversions」(整数颜色 / 法线分量换成 [-1, 1] 或 [0, 1] 的浮点)。

using System.Numerics;

namespace VelaShell.XServer.Gl;

internal sealed partial class GlContext
{
    /// <summary>执行一条渲染命令。认识但没实现的命令照样吃掉(规范只对非法操作码报 GLXBadRenderRequest,那由调用方判断)。</summary>
    private void Execute(int opcode, ReadOnlySpan<byte> body, bool bigEndian)
    {
        GlReader r = new(body, bigEndian);
        switch (opcode)
        {
            case 1:   // CallList
                CallList(r.U32());
                break;
            case 2:   // CallLists
                CallLists(ref r);
                break;
            case 3:   // ListBase
                State.ListBase = r.U32();
                break;
            case 4:   // Begin
                Begin(r.U32());
                break;
            case 5:   // Bitmap
                Bitmap(ref r);
                break;
            case >= 6 and <= 21:   // Color3bv … Color4usv
                {
                    int n = opcode <= 13 ? 3 : 4;
                    Vector4 c = ReadColor(ref r, (opcode - 6) % 8, n);
                    SetCurrentColor(c);
                    break;
                }
            case 22:   // EdgeFlagv
                State.EdgeFlag = r.U8() != 0;
                break;
            case 23:   // End
                End();
                break;
            case >= 24 and <= 27:   // Index*:颜色索引模式不支持
                break;
            case >= 28 and <= 32:   // Normal3bv / dv / fv / iv / sv
                {
                    int type = opcode - 28;   // b d f i s
                    float x = ReadNormalComponent(ref r, type), y = ReadNormalComponent(ref r, type), z = ReadNormalComponent(ref r, type);
                    State.Normal = new Vector3(x, y, z);
                    break;
                }
            case >= 33 and <= 44:   // RasterPos2dv … 4sv
                {
                    int dims = 2 + ((opcode - 33) / 4);
                    Vector4 v = ReadCoords(ref r, (opcode - 33) % 4, dims);
                    RasterPos(v);
                    break;
                }
            case >= 45 and <= 48:   // Rectdv / fv / iv / sv
                {
                    Vector4 a = ReadCoords(ref r, opcode - 45, 2);
                    Vector4 b = ReadCoords(ref r, opcode - 45, 2);
                    Rect(a.X, a.Y, b.X, b.Y);
                    break;
                }
            case >= 49 and <= 64:   // TexCoord1dv … 4sv
                {
                    int dims = 1 + ((opcode - 49) / 4);
                    State.TexCoord = ReadCoords(ref r, (opcode - 49) % 4, dims, defaultZ: 0);
                    break;
                }
            case >= 65 and <= 76:   // Vertex2dv … 4sv
                {
                    int dims = 2 + ((opcode - 65) / 4);
                    Vertex(ReadCoords(ref r, (opcode - 65) % 4, dims));
                    break;
                }
            case 77:   // ClipPlane:四个 FLOAT64,再 plane
                {
                    Vector4 eq = new((float)r.F64(), (float)r.F64(), (float)r.F64(), (float)r.F64());
                    uint plane = r.U32();
                    if (plane - GlEnum.CLIP_PLANE0 >= MaxClipPlanes)
                    {
                        SetError(GlEnum.INVALID_ENUM);
                        break;
                    }
                    // 平面方程按设定时的模型视图矩阵的逆变换到眼坐标:p_eye = p · M⁻¹(行向量)。
                    Matrix4x4.Invert(ToGl(Modelview), out Matrix4x4 inv);
                    State.ClipPlanes[plane - GlEnum.CLIP_PLANE0] = RowTimes(eq, inv);
                    break;
                }
            case 78:   // ColorMaterial
                State.ColorMaterialFace = r.U32();
                State.ColorMaterialMode = r.U32();
                break;
            case 79:   // CullFace
                State.CullFaceMode = r.U32();
                break;
            case 80:   // Fogf
                SetFog(r.U32(), [r.F32()]);
                break;
            case 81:   // Fogfv
                {
                    uint pname = r.U32();
                    SetFog(pname, ReadFloats(ref r, pname == GlEnum.FOG_COLOR ? 4 : 1));
                    break;
                }
            case 82:   // Fogi
                SetFog(r.U32(), [r.I32()]);
                break;
            case 83:   // Fogiv
                {
                    uint pname = r.U32();
                    SetFog(pname, pname == GlEnum.FOG_COLOR ? ReadIntColor(ref r, 4) : [r.I32()]);
                    break;
                }
            case 84:   // FrontFace
                State.FrontFace = r.U32();
                break;
            case 85:   // Hint:只影响质量取舍,忽略
                break;
            case 86:   // Lightf
                SetLight(r.U32(), r.U32(), [r.F32()]);
                break;
            case 87:   // Lightfv
                {
                    uint light = r.U32(), pname = r.U32();
                    SetLight(light, pname, ReadFloats(ref r, LightParamCount(pname)));
                    break;
                }
            case 88:   // Lighti
                SetLight(r.U32(), r.U32(), [r.I32()]);
                break;
            case 89:   // Lightiv
                {
                    uint light = r.U32(), pname = r.U32();
                    int n = LightParamCount(pname);
                    SetLight(light, pname, pname is GlEnum.AMBIENT or GlEnum.DIFFUSE or GlEnum.SPECULAR ? ReadIntColor(ref r, n) : ReadInts(ref r, n));
                    break;
                }
            case 90:   // LightModelf
                SetLightModel(r.U32(), [r.F32()]);
                break;
            case 91:   // LightModelfv
                {
                    uint pname = r.U32();
                    SetLightModel(pname, ReadFloats(ref r, pname == GlEnum.LIGHT_MODEL_AMBIENT ? 4 : 1));
                    break;
                }
            case 92:   // LightModeli
                SetLightModel(r.U32(), [r.I32()]);
                break;
            case 93:   // LightModeliv
                {
                    uint pname = r.U32();
                    SetLightModel(pname, pname == GlEnum.LIGHT_MODEL_AMBIENT ? ReadIntColor(ref r, 4) : [r.I32()]);
                    break;
                }
            case 94:   // LineStipple:不实现点画
                break;
            case 95:   // LineWidth
                {
                    float width = r.F32();
                    if (width <= 0)
                    {
                        SetError(GlEnum.INVALID_VALUE);
                        break;
                    }
                    State.LineWidth = width;
                    break;
                }
            case 96:   // Materialf
                SetMaterial(r.U32(), r.U32(), [r.F32()]);
                break;
            case 97:   // Materialfv
                {
                    uint face = r.U32(), pname = r.U32();
                    SetMaterial(face, pname, ReadFloats(ref r, pname == GlEnum.SHININESS ? 1 : 4));
                    break;
                }
            case 98:   // Materiali
                SetMaterial(r.U32(), r.U32(), [r.I32()]);
                break;
            case 99:   // Materialiv
                {
                    uint face = r.U32(), pname = r.U32();
                    SetMaterial(face, pname, pname == GlEnum.SHININESS ? [r.I32()] : ReadIntColor(ref r, 4));
                    break;
                }
            case 100:   // PointSize
                {
                    float size = r.F32();
                    if (size <= 0)
                    {
                        SetError(GlEnum.INVALID_VALUE);
                        break;
                    }
                    State.PointSize = size;
                    break;
                }
            case 101:   // PolygonMode
                {
                    uint face = r.U32(), mode = r.U32();
                    if (face is GlEnum.FRONT or GlEnum.FRONT_AND_BACK)
                    {
                        State.PolygonModeFront = mode;
                    }
                    if (face is GlEnum.BACK or GlEnum.FRONT_AND_BACK)
                    {
                        State.PolygonModeBack = mode;
                    }
                    break;
                }
            case 102:   // PolygonStipple:不实现点画
                break;
            case 103:   // Scissor
                {
                    int x = r.I32(), y = r.I32(), w = r.I32(), h = r.I32();
                    if (w < 0 || h < 0)
                    {
                        SetError(GlEnum.INVALID_VALUE);
                        break;
                    }
                    (State.ScissorX, State.ScissorY, State.ScissorWidth, State.ScissorHeight) = (x, y, w, h);
                    break;
                }
            case 104:   // ShadeModel
                State.ShadeModel = r.U32();
                break;
            case 105:   // TexParameterf
                SetTexParameter(r.U32(), r.U32(), [r.F32()]);
                break;
            case 106:   // TexParameterfv
                {
                    uint target = r.U32(), pname = r.U32();
                    SetTexParameter(target, pname, ReadFloats(ref r, pname == GlEnum.TEXTURE_BORDER_COLOR ? 4 : 1));
                    break;
                }
            case 107:   // TexParameteri
                SetTexParameter(r.U32(), r.U32(), [r.I32()]);
                break;
            case 108:   // TexParameteriv
                {
                    uint target = r.U32(), pname = r.U32();
                    SetTexParameter(target, pname, pname == GlEnum.TEXTURE_BORDER_COLOR ? ReadIntColor(ref r, 4) : [r.I32()]);
                    break;
                }
            case 109:   // TexImage1D
                TexImage(ref r, oneD: true);
                break;
            case 110:   // TexImage2D
                TexImage(ref r, oneD: false);
                break;
            case 111:   // TexEnvf
                SetTexEnv(r.U32(), r.U32(), [r.F32()]);
                break;
            case 112:   // TexEnvfv
                {
                    uint target = r.U32(), pname = r.U32();
                    SetTexEnv(target, pname, ReadFloats(ref r, pname == GlEnum.TEXTURE_ENV_COLOR ? 4 : 1));
                    break;
                }
            case 113:   // TexEnvi
                SetTexEnv(r.U32(), r.U32(), [r.I32()]);
                break;
            case 114:   // TexEnviv
                {
                    uint target = r.U32(), pname = r.U32();
                    SetTexEnv(target, pname, pname == GlEnum.TEXTURE_ENV_COLOR ? ReadIntColor(ref r, 4) : [r.I32()]);
                    break;
                }
            case 115:   // TexGend:FLOAT64 在前
                {
                    double v = r.F64();
                    SetTexGen(r.U32(), r.U32(), [(float)v]);
                    break;
                }
            case 116:   // TexGendv
                {
                    uint coord = r.U32(), pname = r.U32();
                    int n = pname == GlEnum.TEXTURE_GEN_MODE ? 1 : 4;
                    float[] v = new float[n];
                    for (int i = 0; i < n; i++)
                    {
                        v[i] = (float)r.F64();
                    }
                    SetTexGen(coord, pname, v);
                    break;
                }
            case 117:   // TexGenf
            case 119:   // TexGeni
                {
                    uint coord = r.U32(), pname = r.U32();
                    SetTexGen(coord, pname, [opcode == 117 ? r.F32() : r.I32()]);
                    break;
                }
            case 118:   // TexGenfv
            case 120:   // TexGeniv
                {
                    uint coord = r.U32(), pname = r.U32();
                    int n = pname == GlEnum.TEXTURE_GEN_MODE ? 1 : 4;
                    SetTexGen(coord, pname, opcode == 118 ? ReadFloats(ref r, n) : ReadInts(ref r, n));
                    break;
                }
            case >= 121 and <= 125:   // InitNames / LoadName / PassThrough / PopName / PushName:选择与反馈模式不实现
                break;
            case 126:   // DrawBuffer
                State.DrawBuffer = r.U32();
                break;
            case 127:   // Clear
                Clear(r.U32());
                break;
            case 128:   // ClearAccum:没有累积缓冲
            case 129:   // ClearIndex
                break;
            case 130:   // ClearColor
                State.ClearColor = Vector4.Clamp(new Vector4(r.F32(), r.F32(), r.F32(), r.F32()), Vector4.Zero, Vector4.One);
                break;
            case 131:   // ClearStencil
                State.ClearStencil = r.I32();
                break;
            case 132:   // ClearDepth
                State.ClearDepth = Math.Clamp(r.F64(), 0, 1);
                break;
            case 133:   // StencilMask
                State.StencilWriteMask = r.U32();
                break;
            case 134:   // ColorMask
                State.ColorMask = [r.U8() != 0, r.U8() != 0, r.U8() != 0, r.U8() != 0];
                break;
            case 135:   // DepthMask
                State.DepthMask = r.U8() != 0;
                break;
            case 136:   // IndexMask
                break;
            case 137:   // Accum:没有累积缓冲(配置里 0 位)
                SetError(GlEnum.INVALID_OPERATION);
                break;
            case 138:   // Disable
                State.Enabled.Remove(r.U32());
                break;
            case 139:   // Enable
                Enable(r.U32());
                break;
            case 141:   // PopAttrib
                PopAttrib();
                break;
            case 142:   // PushAttrib
                PushAttrib(r.U32());
                break;
            case >= 143 and <= 158:   // Map / MapGrid / EvalCoord / EvalMesh / EvalPoint:求值器不实现
                break;
            case 159:   // AlphaFunc
                State.AlphaFunc = r.U32();
                State.AlphaRef = Math.Clamp(r.F32(), 0, 1);
                break;
            case 160:   // BlendFunc
                {
                    uint src = r.U32(), dst = r.U32();
                    (State.BlendSrcRgb, State.BlendDstRgb, State.BlendSrcAlpha, State.BlendDstAlpha) = (src, dst, src, dst);
                    break;
                }
            case 161:   // LogicOp
                State.LogicOp = r.U32();
                break;
            case 162:   // StencilFunc
                State.StencilFunc = r.U32();
                State.StencilRef = r.I32();
                State.StencilValueMask = r.U32();
                break;
            case 163:   // StencilOp
                State.StencilFail = r.U32();
                State.StencilDepthFail = r.U32();
                State.StencilDepthPass = r.U32();
                break;
            case 164:   // DepthFunc
                State.DepthFunc = r.U32();
                break;
            case 165:   // PixelZoom
                State.ZoomX = r.F32();
                State.ZoomY = r.F32();
                break;
            case >= 166 and <= 170:   // PixelTransfer / PixelMap:像素传输的缩放、偏置与查表不实现
                break;
            case 171:   // ReadBuffer
                State.ReadBuffer = r.U32();
                break;
            case 172:   // CopyPixels
                CopyPixels(r.I32(), r.I32(), r.I32(), r.I32(), r.U32());
                break;
            case 173:   // DrawPixels
                DrawPixels(ref r);
                break;
            case 174:   // DepthRange
                State.DepthNear = Math.Clamp(r.F64(), 0, 1);
                State.DepthFar = Math.Clamp(r.F64(), 0, 1);
                break;
            case 175:   // Frustum
                Frustum(r.F64(), r.F64(), r.F64(), r.F64(), r.F64(), r.F64());
                break;
            case 176:   // LoadIdentity
                LoadMatrix(Matrix4x4.Identity);
                break;
            case 177:   // LoadMatrixf
                LoadMatrix(FromColumnMajor(ReadFloats(ref r, 16)));
                break;
            case 178:   // LoadMatrixd
                LoadMatrix(FromColumnMajor(ReadDoubles(ref r, 16)));
                break;
            case 179:   // MatrixMode
                {
                    uint mode = r.U32();
                    if (mode is not (GlEnum.MODELVIEW or GlEnum.PROJECTION or GlEnum.TEXTURE))
                    {
                        SetError(GlEnum.INVALID_ENUM);
                        break;
                    }
                    State.MatrixMode = mode;
                    break;
                }
            case 180:   // MultMatrixf
                MultMatrix(FromColumnMajor(ReadFloats(ref r, 16)));
                break;
            case 181:   // MultMatrixd
                MultMatrix(FromColumnMajor(ReadDoubles(ref r, 16)));
                break;
            case 182:   // Ortho
                Ortho(r.F64(), r.F64(), r.F64(), r.F64(), r.F64(), r.F64());
                break;
            case 183:   // PopMatrix
                PopMatrix();
                break;
            case 184:   // PushMatrix
                PushMatrix();
                break;
            case 185:   // Rotated
                Rotate((float)r.F64(), (float)r.F64(), (float)r.F64(), (float)r.F64());
                break;
            case 186:   // Rotatef
                Rotate(r.F32(), r.F32(), r.F32(), r.F32());
                break;
            case 187:   // Scaled
                MultMatrix(Matrix4x4.CreateScale((float)r.F64(), (float)r.F64(), (float)r.F64()));
                break;
            case 188:   // Scalef
                MultMatrix(Matrix4x4.CreateScale(r.F32(), r.F32(), r.F32()));
                break;
            case 189:   // Translated
                MultMatrix(Matrix4x4.CreateTranslation((float)r.F64(), (float)r.F64(), (float)r.F64()));
                break;
            case 190:   // Translatef
                MultMatrix(Matrix4x4.CreateTranslation(r.F32(), r.F32(), r.F32()));
                break;
            case 191:   // Viewport
                {
                    int x = r.I32(), y = r.I32(), w = r.I32(), h = r.I32();
                    if (w < 0 || h < 0)
                    {
                        SetError(GlEnum.INVALID_VALUE);
                        break;
                    }
                    (State.ViewportX, State.ViewportY) = (x, y);
                    (State.ViewportWidth, State.ViewportHeight) = (Math.Min(w, MaxTextureSize * 8), Math.Min(h, MaxTextureSize * 8));
                    break;
                }
            case 192:   // PolygonOffset
            case 4098:  // PolygonOffsetEXT(偏移单位按 EXT 的约定,这里不区分)
                State.PolygonOffsetFactor = r.F32();
                State.PolygonOffsetUnits = r.F32();
                break;
            case 193:   // DrawArrays
            case 4116:  // DrawArraysEXT
                DrawArrays(ref r);
                break;
            case 194:   // Indexubv
                break;
            case 197:   // ActiveTexture:只有一个纹理单元
                if (r.U32() != GlEnum.TEXTURE0)
                {
                    SetError(GlEnum.INVALID_ENUM);
                }
                break;
            case 4096:   // BlendColor
                State.BlendColor = Vector4.Clamp(new Vector4(r.F32(), r.F32(), r.F32(), r.F32()), Vector4.Zero, Vector4.One);
                break;
            case 4097:   // BlendEquation
                State.BlendEquation = r.U32();
                break;
            case 4099:   // TexSubImage1D
                TexSubImage(ref r, oneD: true);
                break;
            case 4100:   // TexSubImage2D
                TexSubImage(ref r, oneD: false);
                break;
            case 4117:   // BindTexture
                BindTexture(r.U32(), r.U32());
                break;
            case 4118:   // PrioritizeTextures
                {
                    int n = r.I32();
                    uint[] names = [.. ReadInts(ref r, Math.Max(0, n)).Select(f => (uint)f)];
                    float[] priorities = ReadFloats(ref r, Math.Max(0, n));
                    for (int i = 0; i < names.Length; i++)
                    {
                        if (Shared.Textures.TryGetValue(names[i], out GlTexture? t))
                        {
                            t.Priority = Math.Clamp(priorities[i], 0, 1);
                        }
                    }
                    break;
                }
            case 4119:   // CopyTexImage1D
                CopyTexImage(r.U32(), r.I32(), r.U32(), r.I32(), r.I32(), r.I32(), 1, r.I32(), oneD: true);
                break;
            case 4120:   // CopyTexImage2D
                CopyTexImage(r.U32(), r.I32(), r.U32(), r.I32(), r.I32(), r.I32(), r.I32(), r.I32(), oneD: false);
                break;
            case 4121:   // CopyTexSubImage1D
                {
                    uint target = r.U32();
                    int level = r.I32(), xoffset = r.I32(), x = r.I32(), y = r.I32(), width = r.I32();
                    CopyTexSubImage(target, level, xoffset, 0, x, y, width, 1);
                    break;
                }
            case 4122:   // CopyTexSubImage2D
                CopyTexSubImage(r.U32(), r.I32(), r.I32(), r.I32(), r.I32(), r.I32(), r.I32(), r.I32());
                break;
            case 4134:   // BlendFuncSeparate
                State.BlendSrcRgb = r.U32();
                State.BlendDstRgb = r.U32();
                State.BlendSrcAlpha = r.U32();
                State.BlendDstAlpha = r.U32();
                break;
            default:
                // 其余(3D 纹理、颜色表、卷积、直方图、顶点属性、…)不实现,按合法命令吃掉。
                break;
        }
    }

    /// <summary>规范列出的全部渲染操作码之外的值:GLXBadRenderRequest。</summary>
    public static bool IsKnownRenderOpcode(int opcode) =>
        opcode is (>= 1 and <= 139) or (>= 141 and <= 237) or (>= 305 and <= 367) or (>= 2048 and <= 2082) or (>= 4096 and <= 4350);

    private void Enable(uint cap)
    {
        State.Enabled.Add(cap);
        if (cap == GlEnum.COLOR_MATERIAL)
        {
            ApplyColorMaterial();   // 打开的那一刻起材质就跟随当前颜色(§2.14.3)
        }
    }

    private void CallLists(ref GlReader r)
    {
        int n = r.I32();
        uint type = r.U32();
        for (int i = 0; i < n && r.Remaining > 0; i++)
        {
            uint offset = type switch
            {
                GlEnum.BYTE => (uint)r.I8(),
                GlEnum.UNSIGNED_BYTE => r.U8(),
                GlEnum.SHORT => (uint)r.I16(),
                GlEnum.UNSIGNED_SHORT => r.U16(),
                GlEnum.INT or GlEnum.UNSIGNED_INT => r.U32(),
                GlEnum.FLOAT => (uint)r.F32(),
                GlEnum.TWO_BYTES => (uint)((r.U8() << 8) | r.U8()),
                GlEnum.THREE_BYTES => (uint)((r.U8() << 16) | (r.U8() << 8) | r.U8()),
                GlEnum.FOUR_BYTES => (uint)((r.U8() << 24) | (r.U8() << 16) | (r.U8() << 8) | r.U8()),
                _ => uint.MaxValue,
            };
            if (offset == uint.MaxValue && type is not (GlEnum.INT or GlEnum.UNSIGNED_INT))
            {
                SetError(GlEnum.INVALID_ENUM);
                return;
            }
            CallList(State.ListBase + offset);
        }
    }

    // ------------------------------------------------------------------ 分量读取

    /// <summary>读 <paramref name="n" /> 个颜色分量,按 Table 2.6 换成浮点;类型序号 0..7 = b d f i s ub ui us。缺省 alpha = 1。</summary>
    private static Vector4 ReadColor(ref GlReader r, int type, int n)
    {
        Span<float> c = [0, 0, 0, 1];
        for (int i = 0; i < n; i++)
        {
            c[i] = type switch
            {
                0 => ((2 * r.I8()) + 1) / 255f,
                1 => (float)r.F64(),
                2 => r.F32(),
                3 => (float)(((2.0 * r.I32()) + 1) / 4294967295.0),
                4 => ((2 * r.I16()) + 1) / 65535f,
                5 => r.U8() / 255f,
                6 => (float)(r.U32() / 4294967295.0),
                _ => r.U16() / 65535f,
            };
        }
        return new Vector4(c[0], c[1], c[2], c[3]);
    }

    /// <summary>法线分量:类型序号 0..4 = b d f i s,整数换成 [-1, 1]。</summary>
    private static float ReadNormalComponent(ref GlReader r, int type) => type switch
    {
        0 => ((2 * r.I8()) + 1) / 255f,
        1 => (float)r.F64(),
        2 => r.F32(),
        3 => (float)(((2.0 * r.I32()) + 1) / 4294967295.0),
        _ => ((2 * r.I16()) + 1) / 65535f,
    };

    /// <summary>坐标(不归一化):类型序号 0..3 = d f i s;缺的 y、z 为 0,w 为 1。</summary>
    private static Vector4 ReadCoords(ref GlReader r, int type, int dims, float defaultZ = 0)
    {
        Span<float> v = [0, 0, defaultZ, 1];
        for (int i = 0; i < dims; i++)
        {
            v[i] = type switch
            {
                0 => (float)r.F64(),
                1 => r.F32(),
                2 => r.I32(),
                _ => r.I16(),
            };
        }
        return new Vector4(v[0], v[1], v[2], v[3]);
    }

    private static float[] ReadFloats(ref GlReader r, int n)
    {
        float[] v = new float[n];
        for (int i = 0; i < n; i++)
        {
            v[i] = r.F32();
        }
        return v;
    }

    private static float[] ReadDoubles(ref GlReader r, int n)
    {
        float[] v = new float[n];
        for (int i = 0; i < n; i++)
        {
            v[i] = (float)r.F64();
        }
        return v;
    }

    private static float[] ReadInts(ref GlReader r, int n)
    {
        float[] v = new float[n];
        for (int i = 0; i < n; i++)
        {
            v[i] = r.I32();
        }
        return v;
    }

    /// <summary>整数给的颜色参数:线性映射到 [-1, 1](Table 2.6 的 int 一栏)。</summary>
    private static float[] ReadIntColor(ref GlReader r, int n)
    {
        float[] v = new float[n];
        for (int i = 0; i < n; i++)
        {
            v[i] = (float)(((2.0 * r.I32()) + 1) / 4294967295.0);
        }
        return v;
    }

    private static int LightParamCount(uint pname) => pname switch
    {
        GlEnum.AMBIENT or GlEnum.DIFFUSE or GlEnum.SPECULAR or GlEnum.POSITION => 4,
        GlEnum.SPOT_DIRECTION => 3,
        _ => 1,
    };

    // ------------------------------------------------------------------ 矩阵命令

    private void Frustum(double l, double r, double b, double t, double n, double f)
    {
        if (n <= 0 || f <= 0 || l == r || b == t || n == f)
        {
            SetError(GlEnum.INVALID_VALUE);
            return;
        }
        MultMatrix(FromRows(
            (float)(2 * n / (r - l)), 0, (float)((r + l) / (r - l)), 0,
            0, (float)(2 * n / (t - b)), (float)((t + b) / (t - b)), 0,
            0, 0, (float)(-(f + n) / (f - n)), (float)(-2 * f * n / (f - n)),
            0, 0, -1, 0));
    }

    private void Ortho(double l, double r, double b, double t, double n, double f)
    {
        if (l == r || b == t || n == f)
        {
            SetError(GlEnum.INVALID_VALUE);
            return;
        }
        MultMatrix(FromRows(
            (float)(2 / (r - l)), 0, 0, (float)(-(r + l) / (r - l)),
            0, (float)(2 / (t - b)), 0, (float)(-(t + b) / (t - b)),
            0, 0, (float)(-2 / (f - n)), (float)(-(f + n) / (f - n)),
            0, 0, 0, 1));
    }

    /// <summary>Rotate(§2.11.2):绕 (x, y, z) 逆时针转 angle 度。</summary>
    private void Rotate(float angle, float x, float y, float z)
    {
        float len = MathF.Sqrt((x * x) + (y * y) + (z * z));
        if (len == 0)
        {
            return;
        }
        (x, y, z) = (x / len, y / len, z / len);
        float rad = angle * MathF.PI / 180;
        float c = MathF.Cos(rad), s = MathF.Sin(rad), ic = 1 - c;
        MultMatrix(FromRows(
            (x * x * ic) + c, (x * y * ic) - (z * s), (x * z * ic) + (y * s), 0,
            (y * x * ic) + (z * s), (y * y * ic) + c, (y * z * ic) - (x * s), 0,
            (x * z * ic) - (y * s), (y * z * ic) + (x * s), (z * z * ic) + c, 0,
            0, 0, 0, 1));
    }

    /// <summary>转置存法 → GL 的数学矩阵(按行)。</summary>
    private static Matrix4x4 ToGl(Matrix4x4 transposed) => Matrix4x4.Transpose(transposed);

    /// <summary>行向量 × 矩阵(按数学写法的矩阵)。</summary>
    private static Vector4 RowTimes(Vector4 v, Matrix4x4 m) => Vector4.Transform(v, m);
}

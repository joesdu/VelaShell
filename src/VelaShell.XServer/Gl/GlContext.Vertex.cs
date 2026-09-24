// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   The OpenGL Graphics System, Version 1.5 ——
//   §2.6「Begin/End Paradigm」(十种图元怎样由顶点序列组成;四边形 / 多边形拆成三角形时内部边不是边界边)、
//   §2.7「Vertex Specification」(当前颜色、法线、纹理坐标)、§2.8「Vertex Arrays」、§2.9「Rectangles」、
//   §2.11「Coordinate Transformations」(模型视图、投影、透视除法、视口:x_w = (p_x/2)x_d + o_x,z_w = ((f−n)/2)z_d + (n+f)/2;
//   法线用模型视图左上 3×3 的逆转置变换,NORMALIZE / RESCALE_NORMAL)、§2.11.4「Generating Texture Coordinates」、
//   §2.12「Clipping」(裁剪体 −w ≤ x, y, z ≤ w 与用户裁剪面;属性按 t 线性插值)、§2.13「Current Raster Position」、
//   §2.14.1「Lighting」(式 2.2–2.5:衰减、聚光、镜面的 f_i 与 h_i,alpha 取漫反射材质的 alpha)、§2.14.3「ColorMaterial」、
//   §2.14.6「Clamping or Masking」、§2.14.7「Flatshading」(Table 2.12:哪个顶点提供平直着色的颜色)、
//   §3.5.1「Basic Polygon Rasterization」(按窗口坐标的有向面积定正反面,CullFace)、§3.5.4「Options Controlling Polygon
//   Rasterization」(PolygonMode 与边界边)、§3.5.5「Depth Offset」、§3.10「Fog」(雾坐标取眼坐标到原点的距离的近似 |z_e|)。

using System.Numerics;

namespace VelaShell.XServer.Gl;

internal sealed partial class GlContext
{
    /// <summary>变换、光照之后的一个顶点(属性在裁剪时线性插值)。</summary>
    private struct GlVertex
    {
        public Vector4 Clip;
        public Vector4 Eye;
        public Vector4 Front;
        public Vector4 Back;
        public Vector3 FrontSpec;
        public Vector3 BackSpec;
        public Vector4 Tex;
        public float Fog;

        public static GlVertex Lerp(in GlVertex a, in GlVertex b, float t) => new()
        {
            Clip = Vector4.Lerp(a.Clip, b.Clip, t),
            Eye = Vector4.Lerp(a.Eye, b.Eye, t),
            Front = Vector4.Lerp(a.Front, b.Front, t),
            Back = Vector4.Lerp(a.Back, b.Back, t),
            FrontSpec = Vector3.Lerp(a.FrontSpec, b.FrontSpec, t),
            BackSpec = Vector3.Lerp(a.BackSpec, b.BackSpec, t),
            Tex = Vector4.Lerp(a.Tex, b.Tex, t),
            Fog = a.Fog + ((b.Fog - a.Fog) * t),
        };
    }

    private readonly List<GlVertex> _primitive = [];
    private uint _primitiveMode = uint.MaxValue;
    private Matrix4x4 _normalMatrix = Matrix4x4.Identity;

    public bool InBeginEnd => _primitiveMode != uint.MaxValue;

    private void OnMatrixChanged()
    {
        if (State.MatrixMode == GlEnum.MODELVIEW)
        {
            // 行向量存法下,n_eye = n · (M⁻¹) 的左上 3×3,M 是 GL 的数学矩阵 = Modelviewᵀ。
            _normalMatrix = Matrix4x4.Invert(ToGl(Modelview), out Matrix4x4 inv) ? inv : Matrix4x4.Identity;
        }
    }

    // ------------------------------------------------------------------ 当前属性

    private void SetCurrentColor(Vector4 color)
    {
        State.Color = color;
        if (State.Enabled.Contains(GlEnum.COLOR_MATERIAL))
        {
            ApplyColorMaterial();
        }
    }

    /// <summary>ColorMaterial 打开时,选定面的选定材质参数跟随当前颜色(§2.14.3)。</summary>
    private void ApplyColorMaterial()
    {
        Vector4 c = State.Color;
        foreach (GlMaterial m in MaterialsFor(State.ColorMaterialFace))
        {
            switch (State.ColorMaterialMode)
            {
                case GlEnum.AMBIENT:
                    m.Ambient = c;
                    break;
                case GlEnum.DIFFUSE:
                    m.Diffuse = c;
                    break;
                case GlEnum.SPECULAR:
                    m.Specular = c;
                    break;
                case GlEnum.EMISSION:
                    m.Emission = c;
                    break;
                case GlEnum.AMBIENT_AND_DIFFUSE:
                    m.Ambient = c;
                    m.Diffuse = c;
                    break;
            }
        }
    }

    private GlMaterial[] MaterialsFor(uint face) => face switch
    {
        GlEnum.FRONT => [State.FrontMaterial],
        GlEnum.BACK => [State.BackMaterial],
        _ => [State.FrontMaterial, State.BackMaterial],
    };

    private void SetMaterial(uint face, uint pname, float[] v)
    {
        if (face is not (GlEnum.FRONT or GlEnum.BACK or GlEnum.FRONT_AND_BACK))
        {
            SetError(GlEnum.INVALID_ENUM);
            return;
        }
        foreach (GlMaterial m in MaterialsFor(face))
        {
            switch (pname)
            {
                case GlEnum.AMBIENT:
                    m.Ambient = Vec4(v);
                    break;
                case GlEnum.DIFFUSE:
                    m.Diffuse = Vec4(v);
                    break;
                case GlEnum.SPECULAR:
                    m.Specular = Vec4(v);
                    break;
                case GlEnum.EMISSION:
                    m.Emission = Vec4(v);
                    break;
                case GlEnum.AMBIENT_AND_DIFFUSE:
                    m.Ambient = Vec4(v);
                    m.Diffuse = Vec4(v);
                    break;
                case GlEnum.SHININESS:
                    if (v[0] is < 0 or > 128)
                    {
                        SetError(GlEnum.INVALID_VALUE);
                        return;
                    }
                    m.Shininess = v[0];
                    break;
                case GlEnum.COLOR_INDEXES:
                    break;
                default:
                    SetError(GlEnum.INVALID_ENUM);
                    return;
            }
        }
    }

    private void SetLight(uint light, uint pname, float[] v)
    {
        uint index = light - GlEnum.LIGHT0;
        if (index >= MaxLights)
        {
            SetError(GlEnum.INVALID_ENUM);
            return;
        }
        GlLight l = State.Lights[index];
        switch (pname)
        {
            case GlEnum.AMBIENT:
                l.Ambient = Vec4(v);
                break;
            case GlEnum.DIFFUSE:
                l.Diffuse = Vec4(v);
                break;
            case GlEnum.SPECULAR:
                l.Specular = Vec4(v);
                break;
            case GlEnum.POSITION:
                l.Position = Vector4.Transform(Vec4(v), Modelview);   // 按设定时的模型视图矩阵变换到眼坐标
                break;
            case GlEnum.SPOT_DIRECTION:
                l.SpotDirection = Vector3.TransformNormal(new Vector3(v[0], v[1], v[2]), Modelview);
                break;
            case GlEnum.SPOT_EXPONENT:
                if (v[0] is < 0 or > 128)
                {
                    SetError(GlEnum.INVALID_VALUE);
                    return;
                }
                l.SpotExponent = v[0];
                break;
            case GlEnum.SPOT_CUTOFF:
                if (v[0] is (< 0 or > 90) and not 180)
                {
                    SetError(GlEnum.INVALID_VALUE);
                    return;
                }
                l.SpotCutoff = v[0];
                break;
            case GlEnum.CONSTANT_ATTENUATION:
            case GlEnum.LINEAR_ATTENUATION:
            case GlEnum.QUADRATIC_ATTENUATION:
                if (v[0] < 0)
                {
                    SetError(GlEnum.INVALID_VALUE);
                    return;
                }
                if (pname == GlEnum.CONSTANT_ATTENUATION)
                {
                    l.ConstantAttenuation = v[0];
                }
                else if (pname == GlEnum.LINEAR_ATTENUATION)
                {
                    l.LinearAttenuation = v[0];
                }
                else
                {
                    l.QuadraticAttenuation = v[0];
                }
                break;
            default:
                SetError(GlEnum.INVALID_ENUM);
                break;
        }
    }

    private void SetLightModel(uint pname, float[] v)
    {
        switch (pname)
        {
            case GlEnum.LIGHT_MODEL_AMBIENT:
                State.LightModelAmbient = Vec4(v);
                break;
            case GlEnum.LIGHT_MODEL_LOCAL_VIEWER:
                State.LightModelLocalViewer = v[0] != 0;
                break;
            case GlEnum.LIGHT_MODEL_TWO_SIDE:
                State.LightModelTwoSide = v[0] != 0;
                break;
            case GlEnum.LIGHT_MODEL_COLOR_CONTROL:
                State.LightModelColorControl = (uint)v[0];
                break;
            default:
                SetError(GlEnum.INVALID_ENUM);
                break;
        }
    }

    private void SetFog(uint pname, float[] v)
    {
        switch (pname)
        {
            case GlEnum.FOG_MODE:
                State.FogMode = (uint)v[0];
                break;
            case GlEnum.FOG_DENSITY:
                if (v[0] < 0)
                {
                    SetError(GlEnum.INVALID_VALUE);
                    return;
                }
                State.FogDensity = v[0];
                break;
            case GlEnum.FOG_START:
                State.FogStart = v[0];
                break;
            case GlEnum.FOG_END:
                State.FogEnd = v[0];
                break;
            case GlEnum.FOG_COLOR:
                State.FogColor = Vector4.Clamp(Vec4(v), Vector4.Zero, Vector4.One);
                break;
            case GlEnum.FOG_INDEX:
                break;
            default:
                SetError(GlEnum.INVALID_ENUM);
                break;
        }
    }

    private void SetTexEnv(uint target, uint pname, float[] v)
    {
        if (target != GlEnum.TEXTURE_ENV)
        {
            SetError(GlEnum.INVALID_ENUM);
            return;
        }
        if (pname == GlEnum.TEXTURE_ENV_MODE)
        {
            State.TexEnvMode = (uint)v[0];
        }
        else if (pname == GlEnum.TEXTURE_ENV_COLOR)
        {
            State.TexEnvColor = Vector4.Clamp(Vec4(v), Vector4.Zero, Vector4.One);
        }
        else
        {
            SetError(GlEnum.INVALID_ENUM);
        }
    }

    private void SetTexGen(uint coord, uint pname, float[] v)
    {
        uint i = coord - GlEnum.S;
        if (i > 3)
        {
            SetError(GlEnum.INVALID_ENUM);
            return;
        }
        switch (pname)
        {
            case GlEnum.TEXTURE_GEN_MODE:
                State.TexGenMode[i] = (uint)v[0];
                break;
            case GlEnum.OBJECT_PLANE:
                State.TexGenObjectPlane[i] = Vec4(v);
                break;
            case GlEnum.EYE_PLANE:
                // 眼平面按设定时模型视图矩阵的逆变换(与裁剪面相同)。
                Matrix4x4.Invert(ToGl(Modelview), out Matrix4x4 inv);
                State.TexGenEyePlane[i] = RowTimes(Vec4(v), inv);
                break;
            default:
                SetError(GlEnum.INVALID_ENUM);
                break;
        }
    }

    private static Vector4 Vec4(float[] v) => new(v[0], v.Length > 1 ? v[1] : 0, v.Length > 2 ? v[2] : 0, v.Length > 3 ? v[3] : 1);

    // ------------------------------------------------------------------ Begin / End

    private void Begin(uint mode)
    {
        if (mode > GlEnum.POLYGON)
        {
            SetError(GlEnum.INVALID_ENUM);
            return;
        }
        if (InBeginEnd)
        {
            SetError(GlEnum.INVALID_OPERATION);
            return;
        }
        _primitiveMode = mode;
        _primitive.Clear();
    }

    private void End()
    {
        if (!InBeginEnd)
        {
            SetError(GlEnum.INVALID_OPERATION);
            return;
        }
        uint mode = _primitiveMode;
        _primitiveMode = uint.MaxValue;
        if (RenderModeValue == GlEnum.RENDER && Draw is not null)
        {
            Assemble(mode, _primitive);
        }
        _primitive.Clear();
    }

    /// <summary>一个顶点:变换、光照、纹理坐标;在 Begin/End 之外的顶点被忽略(§2.6,行为未定义)。</summary>
    private void Vertex(Vector4 obj)
    {
        if (!InBeginEnd)
        {
            return;
        }
        _primitive.Add(Transform(obj));
    }

    private GlVertex Transform(Vector4 obj)
    {
        Vector4 eye = Vector4.Transform(obj, Modelview);
        GlVertex v = new()
        {
            Eye = eye,
            Clip = Vector4.Transform(eye, Projection),
            Tex = TexCoordFor(obj, eye),
            Fog = MathF.Abs(eye.W != 0 ? eye.Z / eye.W : eye.Z),
        };
        if (State.Enabled.Contains(GlEnum.LIGHTING))
        {
            Vector3 normal = EyeNormal();
            (v.Front, v.FrontSpec) = Light(eye, normal, State.FrontMaterial);
            (v.Back, v.BackSpec) = State.LightModelTwoSide ? Light(eye, -normal, State.BackMaterial) : (v.Front, v.FrontSpec);
        }
        else
        {
            v.Front = v.Back = Vector4.Clamp(State.Color, Vector4.Zero, Vector4.One);
        }
        return v;
    }

    private Vector3 EyeNormal()
    {
        Vector3 n = Vector3.TransformNormal(State.Normal, _normalMatrix);
        if (State.Enabled.Contains(GlEnum.NORMALIZE) || State.Enabled.Contains(GlEnum.RESCALE_NORMAL))
        {
            float len = n.Length();
            if (len > 0)
            {
                n /= len;
            }
        }
        return n;
    }

    /// <summary>纹理坐标:TexGen(OBJECT_LINEAR / EYE_LINEAR / SPHERE_MAP)之后乘纹理矩阵。</summary>
    private Vector4 TexCoordFor(Vector4 obj, Vector4 eye)
    {
        Vector4 tc = State.TexCoord;
        bool any = false;
        Span<float> c = [tc.X, tc.Y, tc.Z, tc.W];
        for (int i = 0; i < 4; i++)
        {
            if (!State.Enabled.Contains(GlEnum.TEXTURE_GEN_S + (uint)i))
            {
                continue;
            }
            any = true;
            switch (State.TexGenMode[i])
            {
                case GlEnum.OBJECT_LINEAR:
                    c[i] = Vector4.Dot(State.TexGenObjectPlane[i], obj);
                    break;
                case GlEnum.EYE_LINEAR:
                    c[i] = Vector4.Dot(State.TexGenEyePlane[i], eye);
                    break;
                case GlEnum.SPHERE_MAP when i < 2:
                    {
                        Vector3 u = Vector3.Normalize(new Vector3(eye.X, eye.Y, eye.Z));
                        Vector3 n = EyeNormal();
                        Vector3 rv = u - (2 * Vector3.Dot(n, u) * n);
                        float m = 2 * MathF.Sqrt((rv.X * rv.X) + (rv.Y * rv.Y) + ((rv.Z + 1) * (rv.Z + 1)));
                        c[i] = m == 0 ? 0.5f : ((i == 0 ? rv.X : rv.Y) / m) + 0.5f;
                        break;
                    }
            }
        }
        if (any)
        {
            tc = new Vector4(c[0], c[1], c[2], c[3]);
        }
        return _texture.Count == 1 && TextureMatrix.IsIdentity ? tc : Vector4.Transform(tc, TextureMatrix);
    }

    /// <summary>§2.14.1 的光照方程(RGBA 模式),返回主颜色与(分离镜面时的)副颜色,都已钳到 [0, 1]。</summary>
    private (Vector4 Primary, Vector3 Secondary) Light(Vector4 eye, Vector3 n, GlMaterial m)
    {
        Vector3 v = eye.W != 0 ? new Vector3(eye.X, eye.Y, eye.Z) / eye.W : new Vector3(eye.X, eye.Y, eye.Z);
        Vector3 color = Rgb(m.Emission) + (Rgb(m.Ambient) * Rgb(State.LightModelAmbient));
        Vector3 specular = Vector3.Zero;
        Vector3 toEye = State.LightModelLocalViewer ? SafeNormalize(-v) : Vector3.UnitZ;
        for (int i = 0; i < MaxLights; i++)
        {
            if (!State.Enabled.Contains(GlEnum.LIGHT0 + (uint)i))
            {
                continue;
            }
            GlLight l = State.Lights[i];
            Vector3 toLight;
            float att = 1;
            if (l.Position.W != 0)
            {
                Vector3 p = new Vector3(l.Position.X, l.Position.Y, l.Position.Z) / l.Position.W;
                Vector3 d = p - v;
                float dist = d.Length();
                toLight = dist > 0 ? d / dist : Vector3.Zero;
                float k = l.ConstantAttenuation + (l.LinearAttenuation * dist) + (l.QuadraticAttenuation * dist * dist);
                att = k > 0 ? 1 / k : 1;
            }
            else
            {
                toLight = SafeNormalize(new Vector3(l.Position.X, l.Position.Y, l.Position.Z));
            }
            float spot = 1;
            if (l.SpotCutoff != 180)
            {
                float cos = Vector3.Dot(-toLight, SafeNormalize(l.SpotDirection));
                spot = cos >= MathF.Cos(l.SpotCutoff * MathF.PI / 180) ? MathF.Pow(MathF.Max(cos, 0), l.SpotExponent) : 0;
            }
            float factor = att * spot;
            if (factor == 0)
            {
                continue;
            }
            float nDotL = MathF.Max(Vector3.Dot(n, toLight), 0);
            Vector3 contribution = (Rgb(m.Ambient) * Rgb(l.Ambient)) + (nDotL * Rgb(m.Diffuse) * Rgb(l.Diffuse));
            Vector3 spec = Vector3.Zero;
            if (nDotL > 0)
            {
                Vector3 h = SafeNormalize(toLight + toEye);
                float nDotH = MathF.Max(Vector3.Dot(n, h), 0);
                float power = m.Shininess == 0 ? 1 : MathF.Pow(nDotH, m.Shininess);
                spec = power * Rgb(m.Specular) * Rgb(l.Specular);
            }
            color += factor * contribution;
            specular += factor * spec;
        }
        if (State.LightModelColorControl != GlEnum.SEPARATE_SPECULAR_COLOR)
        {
            color += specular;
            specular = Vector3.Zero;
        }
        Vector3 c = Vector3.Clamp(color, Vector3.Zero, Vector3.One);
        return (new Vector4(c, Math.Clamp(m.Diffuse.W, 0, 1)), Vector3.Clamp(specular, Vector3.Zero, Vector3.One));
    }

    private static Vector3 Rgb(Vector4 c) => new(c.X, c.Y, c.Z);

    private static Vector3 SafeNormalize(Vector3 v)
    {
        float len = v.Length();
        return len > 0 ? v / len : Vector3.Zero;
    }

    // ------------------------------------------------------------------ 图元装配

    /// <summary>把 Begin/End 之间的顶点拆成点、线段、三角形(带边界边标记),处理平直着色后送去裁剪与光栅化。</summary>
    private void Assemble(uint mode, List<GlVertex> v)
    {
        int n = v.Count;
        bool flat = State.ShadeModel == GlEnum.FLAT;
        switch (mode)
        {
            case GlEnum.POINTS:
                for (int i = 0; i < n; i++)
                {
                    PointPrimitive(v[i]);
                }
                break;
            case GlEnum.LINES:
                for (int i = 0; i + 1 < n; i += 2)
                {
                    LinePrimitive(v[i], v[i + 1], flat);
                }
                break;
            case GlEnum.LINE_STRIP:
            case GlEnum.LINE_LOOP:
                for (int i = 0; i + 1 < n; i++)
                {
                    LinePrimitive(v[i], v[i + 1], flat);
                }
                if (mode == GlEnum.LINE_LOOP && n > 2)
                {
                    LinePrimitive(v[n - 1], v[0], flat);
                }
                break;
            case GlEnum.TRIANGLES:
                for (int i = 0; i + 2 < n; i += 3)
                {
                    TrianglePrimitive(v[i], v[i + 1], v[i + 2], 0b111, flat ? i + 2 : -1, v);
                }
                break;
            case GlEnum.TRIANGLE_STRIP:
                for (int i = 0; i + 2 < n; i++)
                {
                    // 奇数个三角形换两个顶点的次序,保持方向一致。
                    if ((i & 1) == 0)
                    {
                        TrianglePrimitive(v[i], v[i + 1], v[i + 2], 0b111, flat ? i + 2 : -1, v);
                    }
                    else
                    {
                        TrianglePrimitive(v[i + 1], v[i], v[i + 2], 0b111, flat ? i + 2 : -1, v);
                    }
                }
                break;
            case GlEnum.TRIANGLE_FAN:
                for (int i = 1; i + 1 < n; i++)
                {
                    TrianglePrimitive(v[0], v[i], v[i + 1], 0b111, flat ? i + 1 : -1, v);
                }
                break;
            case GlEnum.QUADS:
                for (int i = 0; i + 3 < n; i += 4)
                {
                    // 0-1-2 与 0-2-3:对角线 0-2 不是边界边。边标记位:bit0 = v0v1,bit1 = v1v2,bit2 = v2v0。
                    TrianglePrimitive(v[i], v[i + 1], v[i + 2], 0b011, flat ? i + 3 : -1, v);
                    TrianglePrimitive(v[i], v[i + 2], v[i + 3], 0b110, flat ? i + 3 : -1, v);
                }
                break;
            case GlEnum.QUAD_STRIP:
                for (int i = 0; i + 3 < n; i += 2)
                {
                    // 四边形 (i, i+1, i+3, i+2)
                    TrianglePrimitive(v[i], v[i + 1], v[i + 3], 0b011, flat ? i + 3 : -1, v);
                    TrianglePrimitive(v[i], v[i + 3], v[i + 2], 0b110, flat ? i + 3 : -1, v);
                }
                break;
            case GlEnum.POLYGON:
                for (int i = 1; i + 1 < n; i++)
                {
                    int edges = 0b010 | (i == 1 ? 0b001 : 0) | (i + 2 == n ? 0b100 : 0);
                    TrianglePrimitive(v[0], v[i], v[i + 1], edges, flat ? 0 : -1, v);
                }
                break;
        }
    }

    private void PointPrimitive(GlVertex a)
    {
        if (!InsideAll(a))
        {
            return;
        }
        RasterPoint(ToWindow(a, front: true), State.PointSize);
    }

    private void LinePrimitive(GlVertex a, GlVertex b, bool flat)
    {
        if (flat)
        {
            (a.Front, a.FrontSpec) = (b.Front, b.FrontSpec);   // 线段取第二个顶点的颜色
        }
        if (!ClipLine(ref a, ref b))
        {
            return;
        }
        RasterLine(ToWindow(a, front: true), ToWindow(b, front: true), State.LineWidth);
    }

    private void TrianglePrimitive(GlVertex a, GlVertex b, GlVertex c, int edges, int provoking, List<GlVertex> all)
    {
        if (provoking >= 0)
        {
            GlVertex p = all[provoking];
            a.Front = b.Front = c.Front = p.Front;
            a.Back = b.Back = c.Back = p.Back;
            a.FrontSpec = b.FrontSpec = c.FrontSpec = p.FrontSpec;
            a.BackSpec = b.BackSpec = c.BackSpec = p.BackSpec;
        }
        List<(GlVertex V, bool Edge)> polygon = [(a, (edges & 1) != 0), (b, (edges & 2) != 0), (c, (edges & 4) != 0)];
        polygon = ClipPolygon(polygon);
        if (polygon.Count < 3)
        {
            return;
        }
        // 正反面:窗口坐标里的有向面积(§3.5.1,式 3.6)。
        RasterVertex[] w = new RasterVertex[polygon.Count];
        for (int i = 0; i < polygon.Count; i++)
        {
            w[i] = ToWindow(polygon[i].V, front: true);
        }
        float area = 0;
        for (int i = 0; i < w.Length; i++)
        {
            RasterVertex p = w[i], q = w[(i + 1) % w.Length];
            area += (p.X * q.Y) - (q.X * p.Y);
        }
        if (area == 0)
        {
            return;
        }
        bool front = (area > 0) == (State.FrontFace == GlEnum.CCW);
        if (State.Enabled.Contains(GlEnum.CULL_FACE)
            && (State.CullFaceMode == GlEnum.FRONT_AND_BACK || (State.CullFaceMode == GlEnum.FRONT) == front))
        {
            return;
        }
        if (!front && State.LightModelTwoSide && State.Enabled.Contains(GlEnum.LIGHTING))
        {
            for (int i = 0; i < w.Length; i++)
            {
                w[i] = ToWindow(polygon[i].V, front: false);
            }
        }
        uint polygonMode = front ? State.PolygonModeFront : State.PolygonModeBack;
        switch (polygonMode)
        {
            case GlEnum.POINT:
                {
                    float offset = OffsetFor(w, GlEnum.POLYGON_OFFSET_POINT);
                    for (int i = 0; i < w.Length; i++)
                    {
                        // 只画原图元的顶点(边标记为起点的那些),裁剪产生的新顶点也算 —— 近似。
                        RasterVertex p = w[i];
                        p.Z += offset;
                        RasterPoint(p, State.PointSize);
                    }
                    break;
                }
            case GlEnum.LINE:
                {
                    float offset = OffsetFor(w, GlEnum.POLYGON_OFFSET_LINE);
                    for (int i = 0; i < w.Length; i++)
                    {
                        if (!polygon[i].Edge)
                        {
                            continue;
                        }
                        RasterVertex p = w[i], q = w[(i + 1) % w.Length];
                        p.Z += offset;
                        q.Z += offset;
                        RasterLine(p, q, State.LineWidth);
                    }
                    break;
                }
            default:
                {
                    float offset = OffsetFor(w, GlEnum.POLYGON_OFFSET_FILL);
                    for (int i = 1; i + 1 < w.Length; i++)
                    {
                        RasterVertex p0 = w[0], p1 = w[i], p2 = w[i + 1];
                        p0.Z += offset;
                        p1.Z += offset;
                        p2.Z += offset;
                        RasterTriangle(p0, p1, p2);
                    }
                    break;
                }
        }
    }

    /// <summary>深度偏移 o = m·factor + r·units(§3.5.5),m 取多边形里最大的深度斜率,r 取 24 位深度的最小可分辨量。</summary>
    private float OffsetFor(RasterVertex[] w, uint cap)
    {
        if (!State.Enabled.Contains(cap) || w.Length < 3)
        {
            return 0;
        }
        RasterVertex a = w[0], b = w[1], c = w[2];
        float det = ((b.X - a.X) * (c.Y - a.Y)) - ((c.X - a.X) * (b.Y - a.Y));
        float m = 0;
        if (det != 0)
        {
            float dzdx = (((b.Z - a.Z) * (c.Y - a.Y)) - ((c.Z - a.Z) * (b.Y - a.Y))) / det;
            float dzdy = (((c.Z - a.Z) * (b.X - a.X)) - ((b.Z - a.Z) * (c.X - a.X))) / det;
            m = MathF.Max(MathF.Abs(dzdx), MathF.Abs(dzdy));
        }
        return (m * State.PolygonOffsetFactor) + (State.PolygonOffsetUnits / (1 << 24));
    }

    // ------------------------------------------------------------------ 裁剪

    /// <summary>顶点到第 i 个裁剪面的有向距离(≥ 0 在内侧):0..5 是视体的六个面,6.. 是打开的用户裁剪面。</summary>
    private float PlaneDistance(in GlVertex v, int plane) => plane switch
    {
        0 => v.Clip.W + v.Clip.X,
        1 => v.Clip.W - v.Clip.X,
        2 => v.Clip.W + v.Clip.Y,
        3 => v.Clip.W - v.Clip.Y,
        4 => v.Clip.W + v.Clip.Z,
        5 => v.Clip.W - v.Clip.Z,
        _ => Vector4.Dot(State.ClipPlanes[plane - 6], v.Eye),
    };

    private IEnumerable<int> ActivePlanes()
    {
        for (int i = 0; i < 6; i++)
        {
            yield return i;
        }
        for (int i = 0; i < MaxClipPlanes; i++)
        {
            if (State.Enabled.Contains(GlEnum.CLIP_PLANE0 + (uint)i))
            {
                yield return 6 + i;
            }
        }
    }

    private bool InsideAll(in GlVertex v)
    {
        foreach (int plane in ActivePlanes())
        {
            if (PlaneDistance(v, plane) < 0)
            {
                return false;
            }
        }
        return v.Clip.W > 0;
    }

    private bool ClipLine(ref GlVertex a, ref GlVertex b)
    {
        float t0 = 0, t1 = 1;
        foreach (int plane in ActivePlanes())
        {
            float da = PlaneDistance(a, plane), db = PlaneDistance(b, plane);
            if (da < 0 && db < 0)
            {
                return false;
            }
            if (da < 0)
            {
                t0 = MathF.Max(t0, da / (da - db));
            }
            else if (db < 0)
            {
                t1 = MathF.Min(t1, da / (da - db));
            }
        }
        if (t0 > t1)
        {
            return false;
        }
        GlVertex na = t0 > 0 ? GlVertex.Lerp(a, b, t0) : a;
        GlVertex nb = t1 < 1 ? GlVertex.Lerp(a, b, t1) : b;
        (a, b) = (na, nb);
        return a.Clip.W > 0 && b.Clip.W > 0;
    }

    /// <summary>Sutherland–Hodgman:逐个裁剪面裁多边形;每个顶点带着「从它出发的边是不是边界边」。</summary>
    private List<(GlVertex V, bool Edge)> ClipPolygon(List<(GlVertex V, bool Edge)> input)
    {
        foreach (int plane in ActivePlanes())
        {
            bool allInside = true;
            foreach ((GlVertex v, _) in input)
            {
                if (PlaneDistance(v, plane) < 0)
                {
                    allInside = false;
                    break;
                }
            }
            if (allInside)
            {
                continue;
            }
            List<(GlVertex V, bool Edge)> output = [];
            for (int i = 0; i < input.Count; i++)
            {
                (GlVertex cur, bool edge) = input[i];
                GlVertex next = input[(i + 1) % input.Count].V;
                float dc = PlaneDistance(cur, plane), dn = PlaneDistance(next, plane);
                if (dc >= 0)
                {
                    output.Add((cur, edge));
                    if (dn < 0)
                    {
                        // 出去:交点到下一个进来的点之间是裁剪面上的新边(不是原图元的边界边)。
                        output.Add((GlVertex.Lerp(cur, next, dc / (dc - dn)), false));
                    }
                }
                else if (dn >= 0)
                {
                    output.Add((GlVertex.Lerp(cur, next, dc / (dc - dn)), edge));
                }
            }
            input = output;
            if (input.Count < 3)
            {
                return input;
            }
        }
        return input;
    }

    /// <summary>透视除法与视口变换;颜色按面选正面或背面的。</summary>
    private RasterVertex ToWindow(in GlVertex v, bool front)
    {
        float invW = 1 / v.Clip.W;
        float x = v.Clip.X * invW, y = v.Clip.Y * invW, z = v.Clip.Z * invW;
        float n = (float)State.DepthNear, f = (float)State.DepthFar;
        return new RasterVertex
        {
            X = ((x + 1) * State.ViewportWidth / 2) + State.ViewportX,
            Y = ((y + 1) * State.ViewportHeight / 2) + State.ViewportY,
            Z = Math.Clamp((z * (f - n) / 2) + ((n + f) / 2), 0, 1),
            InvW = invW,
            Color = front ? v.Front : v.Back,
            Spec = front ? v.FrontSpec : v.BackSpec,
            Tex = v.Tex,
            Fog = v.Fog,
        };
    }

    // ------------------------------------------------------------------ 矩形、光栅位置、顶点数组

    private void Rect(float x1, float y1, float x2, float y2)
    {
        if (InBeginEnd)
        {
            SetError(GlEnum.INVALID_OPERATION);
            return;
        }
        Begin(GlEnum.POLYGON);
        Vertex(new Vector4(x1, y1, 0, 1));
        Vertex(new Vector4(x2, y1, 0, 1));
        Vertex(new Vector4(x2, y2, 0, 1));
        Vertex(new Vector4(x1, y2, 0, 1));
        End();
    }

    /// <summary>RasterPos(§2.13):像顶点一样变换、光照;在裁剪体外时光栅位置无效。</summary>
    private void RasterPos(Vector4 obj)
    {
        GlVertex v = Transform(obj);
        State.RasterValid = InsideAll(v);
        if (!State.RasterValid)
        {
            return;
        }
        RasterVertex w = ToWindow(v, front: true);
        State.RasterPos = new Vector4(w.X, w.Y, w.Z, v.Clip.W);
        State.RasterColor = v.Front;
        State.RasterTexCoord = v.Tex;
        State.RasterDistance = v.Fog;
    }

    /// <summary>
    /// DrawArrays(GLX 编码 §2.3.4):n 个顶点、m 个 ARRAY_INFO(类型、分量数、数组种类),再是逐顶点紧排的数据 ——
    /// 每个顶点依次是边标记、纹理坐标、颜色、索引、法线、顶点,各自补齐到 4 字节。
    /// </summary>
    private void DrawArrays(ref GlReader r)
    {
        int count = r.I32();
        int arrays = r.I32();
        uint mode = r.U32();
        if (count < 0 || arrays < 0 || arrays > 6)
        {
            SetError(GlEnum.INVALID_VALUE);
            return;
        }
        (uint Type, int Size, int Bytes)[] info = new (uint, int, int)[6];   // 按数据里的次序:边、纹理、颜色、索引、法线、顶点
        for (int i = 0; i < arrays; i++)
        {
            uint type = r.U32();
            int size = r.I32();
            uint kind = r.U32();
            int slot = kind switch
            {
                GlEnum.EDGE_FLAG_ARRAY => 0,
                GlEnum.TEXTURE_COORD_ARRAY => 1,
                GlEnum.COLOR_ARRAY => 2,
                GlEnum.INDEX_ARRAY => 3,
                GlEnum.NORMAL_ARRAY => 4,
                GlEnum.VERTEX_ARRAY => 5,
                _ => -1,
            };
            int elem = type switch
            {
                GlEnum.BYTE or GlEnum.UNSIGNED_BYTE => 1,
                GlEnum.SHORT or GlEnum.UNSIGNED_SHORT => 2,
                GlEnum.INT or GlEnum.UNSIGNED_INT or GlEnum.FLOAT => 4,
                GlEnum.DOUBLE => 8,
                _ => 0,
            };
            if (slot < 0 || elem == 0 || size is < 1 or > 4)
            {
                SetError(GlEnum.INVALID_ENUM);
                return;
            }
            info[slot] = (type, size, elem * size);
        }
        Begin(mode);
        if (!InBeginEnd)
        {
            return;
        }
        for (int v = 0; v < count && r.Remaining > 0; v++)
        {
            Vector4 position = new(0, 0, 0, 1);
            bool hasVertex = false;
            for (int slot = 0; slot < 6; slot++)
            {
                (uint type, int size, int bytes) = info[slot];
                if (bytes == 0)
                {
                    continue;
                }
                Span<float> c = [0, 0, 0, 1];
                for (int k = 0; k < size; k++)
                {
                    c[k] = ReadArrayComponent(ref r, type, normalized: slot is 2 or 4);
                }
                r.Skip(((bytes + 3) & ~3) - bytes);
                switch (slot)
                {
                    case 0:
                        State.EdgeFlag = c[0] != 0;
                        break;
                    case 1:
                        State.TexCoord = new Vector4(c[0], size > 1 ? c[1] : 0, size > 2 ? c[2] : 0, size > 3 ? c[3] : 1);
                        break;
                    case 2:
                        SetCurrentColor(new Vector4(c[0], c[1], c[2], size > 3 ? c[3] : 1));
                        break;
                    case 4:
                        State.Normal = new Vector3(c[0], c[1], c[2]);
                        break;
                    case 5:
                        position = new Vector4(c[0], c[1], size > 2 ? c[2] : 0, size > 3 ? c[3] : 1);
                        hasVertex = true;
                        break;
                }
            }
            if (hasVertex)
            {
                Vertex(position);
            }
        }
        End();
    }

    private static float ReadArrayComponent(ref GlReader r, uint type, bool normalized) => type switch
    {
        GlEnum.BYTE => normalized ? ((2 * r.I8()) + 1) / 255f : r.I8(),
        GlEnum.UNSIGNED_BYTE => normalized ? r.U8() / 255f : r.U8(),
        GlEnum.SHORT => normalized ? ((2 * r.I16()) + 1) / 65535f : r.I16(),
        GlEnum.UNSIGNED_SHORT => normalized ? r.U16() / 65535f : r.U16(),
        GlEnum.INT => normalized ? (float)(((2.0 * r.I32()) + 1) / 4294967295.0) : r.I32(),
        GlEnum.UNSIGNED_INT => normalized ? (float)(r.U32() / 4294967295.0) : r.U32(),
        GlEnum.FLOAT => r.F32(),
        _ => (float)r.F64(),
    };
}

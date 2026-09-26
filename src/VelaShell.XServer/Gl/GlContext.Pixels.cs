// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   GLX Extensions for OpenGL Protocol Specification, Version 1.3 —— §2.3.6「GL Rendering Commands with Pixel Data」
//   (Bitmap / DrawPixels / TexImage1D / TexImage2D / TexSubImage1D / TexSubImage2D 的像素存储头与参数)、
//   附录 A.2.1 / A.2.2「Pixel Data in Rendering Commands」(行跨度 k 按 alignment 取整;第 j 行第 i 组起于
//   (j + skip rows)·k + (i + skip pixels)·nelements·nbytes;swap bytes 为假时元素按客户端字节序)、
//   附录 A.3.1「Pixel Data in Replies」(回复里每行补齐到 4 字节)。
//   The OpenGL Graphics System, Version 1.5 —— §3.6.4「Rasterization of Pixel Rectangles」(Table 3.5 / 3.6 各格式、类型;
//   Table 3.8–3.11 的紧缩类型各分量所占的位;组内分量按格式的次序对应 R、G、B、A;LUMINANCE 放进 R、G、B)、
//   §3.6.5 的缩放(PixelZoom)、§3.7「Bitmaps」、§3.8.1「Texture Image Specification」(Table 3.15 / 3.16:
//   内部格式到基本内部格式;components 1–4 的旧写法)、§3.8.2「Alternate Texture Image Specification Commands」
//   (CopyTexImage / TexSubImage / CopyTexSubImage)、§3.8.4「Texture Parameters」、§3.8.8「Texture Minification」
//   (NEAREST / LINEAR 与 REPEAT / CLAMP / CLAMP_TO_EDGE 的取址)、§3.8.13(Table 3.22 的纹理函数)、
//   §4.3.2「Reading Pixels」(读缓冲、LUMINANCE = R + G + B 再钳位)、§4.3.3「Copying Pixels」、
//   Table 4.7「Reversed component conversions」(浮点分量换成各整数类型)。
//
//   纹理只取第 0 级采样(不算 LOD),过滤用放大过滤器;像素传输的缩放、偏置与查表不实现。

using System.Buffers.Binary;
using System.Numerics;

namespace VelaShell.XServer.Gl;

internal sealed partial class GlContext
{
    private readonly GlTexture _default1D = new(0) { Target = GlEnum.TEXTURE_1D };
    private readonly GlTexture _default2D = new(0) { Target = GlEnum.TEXTURE_2D };
    private readonly GlTexture _proxy1D = new(0) { Target = GlEnum.PROXY_TEXTURE_1D };
    private readonly GlTexture _proxy2D = new(0) { Target = GlEnum.PROXY_TEXTURE_2D };

    private GlTexture? DefaultTexture(uint cap) => cap == GlEnum.TEXTURE_1D ? _default1D : _default2D;

    /// <summary>目标当前绑定的纹理对象(含名字 0 的默认纹理与代理纹理)。</summary>
    private GlTexture? BoundTexture(uint target) => target switch
    {
        GlEnum.TEXTURE_1D => State.Texture1D == 0 ? _default1D : Shared.Textures.GetValueOrDefault(State.Texture1D),
        GlEnum.TEXTURE_2D => State.Texture2D == 0 ? _default2D : Shared.Textures.GetValueOrDefault(State.Texture2D),
        GlEnum.PROXY_TEXTURE_1D => _proxy1D,
        GlEnum.PROXY_TEXTURE_2D => _proxy2D,
        _ => null,
    };

    private void BindTexture(uint target, uint name)
    {
        if (target is not (GlEnum.TEXTURE_1D or GlEnum.TEXTURE_2D))
        {
            SetError(GlEnum.INVALID_ENUM);
            return;
        }
        if (name != 0)
        {
            if (!Shared.Textures.TryGetValue(name, out GlTexture? texture))
            {
                texture = new GlTexture(name);
                Shared.Textures[name] = texture;
            }
            if (texture.Target == 0)
            {
                texture.Target = target;
            }
            else if (texture.Target != target)
            {
                SetError(GlEnum.INVALID_OPERATION);
                return;
            }
        }
        if (target == GlEnum.TEXTURE_1D)
        {
            State.Texture1D = name;
        }
        else
        {
            State.Texture2D = name;
        }
    }

    private void SetTexParameter(uint target, uint pname, float[] v)
    {
        if (target is not (GlEnum.TEXTURE_1D or GlEnum.TEXTURE_2D) || BoundTexture(target) is not { } t)
        {
            SetError(GlEnum.INVALID_ENUM);
            return;
        }
        switch (pname)
        {
            case GlEnum.TEXTURE_MIN_FILTER:
                t.MinFilter = (uint)v[0];
                break;
            case GlEnum.TEXTURE_MAG_FILTER:
                t.MagFilter = (uint)v[0];
                break;
            case GlEnum.TEXTURE_WRAP_S:
                t.WrapS = (uint)v[0];
                break;
            case GlEnum.TEXTURE_WRAP_T:
                t.WrapT = (uint)v[0];
                break;
            case GlEnum.TEXTURE_BORDER_COLOR:
                t.BorderColor = Vector4.Clamp(Vec4(v), Vector4.Zero, Vector4.One);
                break;
            case GlEnum.TEXTURE_PRIORITY:
                t.Priority = Math.Clamp(v[0], 0, 1);
                break;
            case GlEnum.GENERATE_MIPMAP:
                break;
            default:
                SetError(GlEnum.INVALID_ENUM);
                break;
        }
    }

    /// <summary>内部格式 → 基本内部格式(Table 3.15 / 3.16;1–4 是 GL 1.0 的 components 写法)。不认识的返回 0。</summary>
    private static uint BaseInternalFormat(uint internalFormat) => internalFormat switch
    {
        1 or GlEnum.LUMINANCE or (>= 0x803F and <= 0x8042) => GlEnum.LUMINANCE,
        2 or GlEnum.LUMINANCE_ALPHA or (>= 0x8043 and <= 0x8048) => GlEnum.LUMINANCE_ALPHA,
        3 or GlEnum.RGB or 0x2A10 or (>= 0x804F and <= 0x8054) => GlEnum.RGB,
        4 or GlEnum.RGBA or (>= 0x8055 and <= 0x805B) => GlEnum.RGBA,
        GlEnum.ALPHA or (>= 0x803B and <= 0x803E) => GlEnum.ALPHA,
        GlEnum.INTENSITY or (>= 0x804A and <= 0x804D) => GlEnum.INTENSITY,
        _ => 0,
    };

    // ------------------------------------------------------------------ 像素存储头

    /// <summary>渲染命令里的像素存储参数(附录 A 的 Table A.3)。</summary>
    private readonly record struct PixelStore(bool SwapBytes, bool LsbFirst, int RowLength, int SkipRows, int SkipPixels, int Alignment);

    private static PixelStore ReadPixelStore(ref GlReader r)
    {
        bool swap = r.U8() != 0;
        bool lsb = r.U8() != 0;
        r.Skip(2);
        int rowLength = r.I32(), skipRows = r.I32(), skipPixels = r.I32(), alignment = r.I32();
        return new PixelStore(swap, lsb, rowLength, skipRows, skipPixels, alignment is 1 or 2 or 4 or 8 ? alignment : 4);
    }

    /// <summary>格式里的元素个数(Table A.2);不认识的返回 0。</summary>
    private static int FormatElements(uint format) => format switch
    {
        GlEnum.RGBA or GlEnum.BGRA => 4,
        GlEnum.RGB or GlEnum.BGR => 3,
        GlEnum.LUMINANCE_ALPHA => 2,
        GlEnum.COLOR_INDEX or GlEnum.STENCIL_INDEX or GlEnum.DEPTH_COMPONENT or GlEnum.RED or GlEnum.GREEN or GlEnum.BLUE
            or GlEnum.ALPHA or GlEnum.LUMINANCE => 1,
        _ => 0,
    };

    /// <summary>类型的字节数与「一个元素装下整组」的紧缩类型(Table A.1)。不认识的返回 (0, false)。</summary>
    private static (int Bytes, bool Packed) TypeSize(uint type) => type switch
    {
        GlEnum.UNSIGNED_BYTE or GlEnum.BYTE => (1, false),
        GlEnum.UNSIGNED_SHORT or GlEnum.SHORT => (2, false),
        GlEnum.UNSIGNED_INT or GlEnum.INT or GlEnum.FLOAT => (4, false),
        0x8032 or 0x8362 => (1, true),                                                   // 3_3_2、2_3_3_REV
        0x8363 or 0x8364 or 0x8033 or 0x8365 or 0x8034 or 0x8366 => (2, true),           // 5_6_5(_REV)、4_4_4_4(_REV)、5_5_5_1、1_5_5_5_REV
        0x8035 or 0x8367 or 0x8036 or 0x8368 => (4, true),                               // 8_8_8_8(_REV)、10_10_10_2、2_10_10_10_REV
        _ => (0, false),
    };

    /// <summary>紧缩类型各分量的位宽,按「第一分量在前」的次序与它们从哪一位开始(Table 3.8–3.11)。</summary>
    private static (int Shift, int Bits)[] PackedLayout(uint type) => type switch
    {
        0x8032 => [(5, 3), (2, 3), (0, 2)],
        0x8362 => [(0, 3), (3, 3), (6, 2)],
        0x8363 => [(11, 5), (5, 6), (0, 5)],
        0x8364 => [(0, 5), (5, 6), (11, 5)],
        0x8033 => [(12, 4), (8, 4), (4, 4), (0, 4)],
        0x8365 => [(0, 4), (4, 4), (8, 4), (12, 4)],
        0x8034 => [(11, 5), (6, 5), (1, 5), (0, 1)],
        0x8366 => [(0, 5), (5, 5), (10, 5), (15, 1)],
        0x8035 => [(24, 8), (16, 8), (8, 8), (0, 8)],
        0x8367 => [(0, 8), (8, 8), (16, 8), (24, 8)],
        0x8036 => [(22, 10), (12, 10), (2, 10), (0, 2)],
        _ => [(0, 10), (10, 10), (20, 10), (30, 2)],
    };

    /// <summary>
    /// 按附录 A.2.1 解出 width × height 的像素矩形,每个像素给出 RGBA 浮点(第 0 行是图像的第一行,即 GL 里最下面一行)。
    /// 格式或类型不认识时记 INVALID_ENUM 并返回 null。
    /// </summary>
    private Vector4[]? UnpackImage(ReadOnlySpan<byte> data, PixelStore store, int width, int height, uint format, uint type, bool bigEndian)
    {
        int elements = FormatElements(format);
        (int nbytes, bool packed) = TypeSize(type);
        if (elements == 0 || nbytes == 0 || format is GlEnum.COLOR_INDEX or GlEnum.STENCIL_INDEX or GlEnum.DEPTH_COMPONENT)
        {
            SetError(GlEnum.INVALID_ENUM);
            return null;
        }
        if (width <= 0 || height <= 0)
        {
            return [];
        }
        int groupElements = packed ? 1 : elements;
        int groups = store.RowLength > 0 ? store.RowLength : width;
        int rowBytes = nbytes * groupElements * groups;
        int k = nbytes >= store.Alignment ? rowBytes : store.Alignment * ((rowBytes + store.Alignment - 1) / store.Alignment);
        bool elementBigEndian = store.SwapBytes ? !bigEndian : bigEndian;
        var result = new Vector4[width * height];
        Span<float> comp = stackalloc float[4];
        for (int j = 0; j < height; j++)
        {
            for (int i = 0; i < width; i++)
            {
                long offset = ((long)(j + store.SkipRows) * k) + ((long)(i + store.SkipPixels) * groupElements * nbytes);
                if (offset < 0 || offset + (groupElements * nbytes) > data.Length)
                {
                    continue;
                }
                ReadOnlySpan<byte> group = data.Slice((int)offset, groupElements * nbytes);
                if (packed)
                {
                    uint word = nbytes switch
                    {
                        1 => group[0],
                        2 => elementBigEndian ? BinaryPrimitives.ReadUInt16BigEndian(group) : BinaryPrimitives.ReadUInt16LittleEndian(group),
                        _ => elementBigEndian ? BinaryPrimitives.ReadUInt32BigEndian(group) : BinaryPrimitives.ReadUInt32LittleEndian(group),
                    };
                    (int Shift, int Bits)[] layout = PackedLayout(type);
                    for (int c = 0; c < 4; c++)
                    {
                        comp[c] = c < layout.Length ? ((word >> layout[c].Shift) & ((1u << layout[c].Bits) - 1)) / (float)((1u << layout[c].Bits) - 1) : 1;
                    }
                }
                else
                {
                    for (int c = 0; c < elements; c++)
                    {
                        comp[c] = ReadElement(group.Slice(c * nbytes, nbytes), type, elementBigEndian);
                    }
                }
                result[(j * width) + i] = ToRgba(format, comp);
            }
        }
        return result;
    }

    /// <summary>一个元素换成浮点(Table 2.6:无符号除以最大值,有符号 (2c + 1)/(2^b − 1))。</summary>
    private static float ReadElement(ReadOnlySpan<byte> e, uint type, bool bigEndian) => type switch
    {
        GlEnum.UNSIGNED_BYTE => e[0] / 255f,
        GlEnum.BYTE => ((2 * (sbyte)e[0]) + 1) / 255f,
        GlEnum.UNSIGNED_SHORT => (bigEndian ? BinaryPrimitives.ReadUInt16BigEndian(e) : BinaryPrimitives.ReadUInt16LittleEndian(e)) / 65535f,
        GlEnum.SHORT => ((2 * (bigEndian ? BinaryPrimitives.ReadInt16BigEndian(e) : BinaryPrimitives.ReadInt16LittleEndian(e))) + 1) / 65535f,
        GlEnum.UNSIGNED_INT => (float)((bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(e) : BinaryPrimitives.ReadUInt32LittleEndian(e)) / 4294967295.0),
        GlEnum.INT => (float)(((2.0 * (bigEndian ? BinaryPrimitives.ReadInt32BigEndian(e) : BinaryPrimitives.ReadInt32LittleEndian(e))) + 1) / 4294967295.0),
        _ => bigEndian ? BinaryPrimitives.ReadSingleBigEndian(e) : BinaryPrimitives.ReadSingleLittleEndian(e),
    };

    /// <summary>一组分量按格式对应到 RGBA(缺的颜色分量为 0,缺的 alpha 为 1;LUMINANCE 放进 R、G、B)。</summary>
    private static Vector4 ToRgba(uint format, ReadOnlySpan<float> c) => format switch
    {
        GlEnum.RGBA => new Vector4(c[0], c[1], c[2], c[3]),
        GlEnum.RGB => new Vector4(c[0], c[1], c[2], 1),
        GlEnum.BGRA => new Vector4(c[2], c[1], c[0], c[3]),
        GlEnum.BGR => new Vector4(c[2], c[1], c[0], 1),
        GlEnum.RED => new Vector4(c[0], 0, 0, 1),
        GlEnum.GREEN => new Vector4(0, c[0], 0, 1),
        GlEnum.BLUE => new Vector4(0, 0, c[0], 1),
        GlEnum.ALPHA => new Vector4(0, 0, 0, c[0]),
        GlEnum.LUMINANCE => new Vector4(c[0], c[0], c[0], 1),
        _ => new Vector4(c[0], c[0], c[0], c[1]),   // LUMINANCE_ALPHA
    };

    // ------------------------------------------------------------------ 纹理图像

    private void TexImage(ref GlReader r, bool oneD)
    {
        PixelStore store = ReadPixelStore(ref r);
        uint target = r.U32();
        int level = r.I32();
        uint internalFormat = r.U32();
        int width = r.I32();
        int height;
        if (oneD)
        {
            r.Skip(4);
            height = 1;
        }
        else
        {
            height = r.I32();
        }
        int border = r.I32();
        uint format = r.U32(), type = r.U32();
        uint[] valid = oneD ? [GlEnum.TEXTURE_1D, GlEnum.PROXY_TEXTURE_1D] : [GlEnum.TEXTURE_2D, GlEnum.PROXY_TEXTURE_2D];
        if (!valid.Contains(target))
        {
            SetError(GlEnum.INVALID_ENUM);
            return;
        }
        uint baseFormat = BaseInternalFormat(internalFormat);
        if (baseFormat == 0 || level < 0 || level >= GlTexture.MaxLevels || border is not (0 or 1) || width < 0 || height < 0)
        {
            SetError(GlEnum.INVALID_VALUE);
            return;
        }
        bool proxy = target is GlEnum.PROXY_TEXTURE_1D or GlEnum.PROXY_TEXTURE_2D;
        GlTexture texture = BoundTexture(target)!;
        int w = width - (2 * border), h = oneD ? 1 : height - (2 * border);
        if (w > MaxTextureSize || h > MaxTextureSize || w < 0 || h < 0)
        {
            if (proxy)
            {
                texture.Levels[level] = null;   // 代理:放不下时各项查询回 0
                return;
            }
            SetError(GlEnum.INVALID_VALUE);
            return;
        }
        if (proxy)
        {
            texture.Levels[level] = new GlTexImage(w, h, internalFormat, baseFormat, []);
            return;
        }
        // 数据为空(客户端传 NULL)时纹理内容未定义:这里填 0。边框像素只存内圈。
        byte[] texels = new byte[w * h * 4];
        if (r.Remaining > 0 && w > 0 && h > 0)
        {
            Vector4[]? pixels = UnpackImage(r.Rest(), store, width, oneD ? 1 : height, format, type, r.BigEndian);
            if (pixels is null)
            {
                return;
            }
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int src = ((y + (oneD ? 0 : border)) * width) + x + border;
                    if (src < pixels.Length)
                    {
                        StoreTexel(texels, (y * w) + x, pixels[src], baseFormat);
                    }
                }
            }
        }
        texture.Levels[level] = new GlTexImage(w, h, internalFormat, baseFormat, texels);
    }

    private void TexSubImage(ref GlReader r, bool oneD)
    {
        PixelStore store = ReadPixelStore(ref r);
        uint target = r.U32();
        int level = r.I32(), xoffset = r.I32(), yoffset = r.I32(), width = r.I32(), height = r.I32();
        uint format = r.U32(), type = r.U32();
        r.Skip(4);
        if (oneD)
        {
            (yoffset, height) = (0, 1);
        }
        if (target != (oneD ? GlEnum.TEXTURE_1D : GlEnum.TEXTURE_2D) || level < 0 || level >= GlTexture.MaxLevels)
        {
            SetError(GlEnum.INVALID_ENUM);
            return;
        }
        if (BoundTexture(target)?.Levels[level] is not { } image)
        {
            SetError(GlEnum.INVALID_OPERATION);
            return;
        }
        if (xoffset < 0 || yoffset < 0 || width < 0 || height < 0 || xoffset + width > image.Width || yoffset + height > image.Height)
        {
            SetError(GlEnum.INVALID_VALUE);
            return;
        }
        Vector4[]? pixels = UnpackImage(r.Rest(), store, width, height, format, type, r.BigEndian);
        if (pixels is null)
        {
            return;
        }
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                StoreTexel(image.Texels, ((y + yoffset) * image.Width) + x + xoffset, pixels[(y * width) + x], image.BaseFormat);
            }
        }
    }

    private void CopyTexImage(uint target, int level, uint internalFormat, int x, int y, int width, int height, int border, bool oneD)
    {
        if (target != (oneD ? GlEnum.TEXTURE_1D : GlEnum.TEXTURE_2D))
        {
            SetError(GlEnum.INVALID_ENUM);
            return;
        }
        uint baseFormat = BaseInternalFormat(internalFormat);
        if (baseFormat == 0 || level < 0 || level >= GlTexture.MaxLevels || border is not (0 or 1) || width < 0 || height < 0
            || width > MaxTextureSize || height > MaxTextureSize)
        {
            SetError(GlEnum.INVALID_VALUE);
            return;
        }
        int w = width - (2 * border), h = oneD ? 1 : height - (2 * border);
        byte[] texels = new byte[Math.Max(0, w * h * 4)];
        for (int j = 0; j < h; j++)
        {
            for (int i = 0; i < w; i++)
            {
                StoreTexel(texels, (j * w) + i, ReadColorPixel(x + i + border, y + j + (oneD ? 0 : border)), baseFormat);
            }
        }
        BoundTexture(target)!.Levels[level] = new GlTexImage(Math.Max(0, w), Math.Max(0, h), internalFormat, baseFormat, texels);
    }

    private void CopyTexSubImage(uint target, int level, int xoffset, int yoffset, int x, int y, int width, int height)
    {
        if (level < 0 || level >= GlTexture.MaxLevels || BoundTexture(target)?.Levels[level] is not { } image)
        {
            SetError(GlEnum.INVALID_OPERATION);
            return;
        }
        if (xoffset < 0 || yoffset < 0 || width < 0 || height < 0 || xoffset + width > image.Width || yoffset + height > image.Height)
        {
            SetError(GlEnum.INVALID_VALUE);
            return;
        }
        for (int j = 0; j < height; j++)
        {
            for (int i = 0; i < width; i++)
            {
                StoreTexel(image.Texels, ((j + yoffset) * image.Width) + i + xoffset, ReadColorPixel(x + i, y + j), image.BaseFormat);
            }
        }
    }

    /// <summary>按基本内部格式存一个纹素(每分量 1 字节):LUMINANCE / INTENSITY 取 R。</summary>
    private static void StoreTexel(byte[] texels, int index, Vector4 c, uint baseFormat)
    {
        c = Vector4.Clamp(c, Vector4.Zero, Vector4.One);
        byte r = (byte)((c.X * 255) + 0.5f), g = (byte)((c.Y * 255) + 0.5f), b = (byte)((c.Z * 255) + 0.5f), a = (byte)((c.W * 255) + 0.5f);
        (r, g, b, a) = baseFormat switch
        {
            GlEnum.ALPHA => ((byte)0, (byte)0, (byte)0, a),
            GlEnum.LUMINANCE => (r, r, r, (byte)255),
            GlEnum.LUMINANCE_ALPHA => (r, r, r, a),
            GlEnum.INTENSITY => (r, r, r, r),
            GlEnum.RGB => (r, g, b, (byte)255),
            _ => (r, g, b, a),
        };
        int o = index * 4;
        texels[o] = r;
        texels[o + 1] = g;
        texels[o + 2] = b;
        texels[o + 3] = a;
    }

    // ------------------------------------------------------------------ 采样与纹理函数

    private Vector4 ApplyTexture(GlTexture texture, Vector4 color, Vector4 tex)
    {
        GlTexImage image = texture.Base!;
        float q = tex.W == 0 ? 1 : tex.W;
        Vector4 t = Sample(texture, image, tex.X / q, tex.Y / q);
        Vector3 cf = new(color.X, color.Y, color.Z), ct = new(t.X, t.Y, t.Z), cc = new(State.TexEnvColor.X, State.TexEnvColor.Y, State.TexEnvColor.Z);
        float af = color.W, at = t.W, lt = t.X;
        (Vector3 c, float a) = (State.TexEnvMode, image.BaseFormat) switch
        {
            (GlEnum.REPLACE, GlEnum.ALPHA) => (cf, at),
            (GlEnum.REPLACE, GlEnum.LUMINANCE) => (new Vector3(lt), af),
            (GlEnum.REPLACE, GlEnum.LUMINANCE_ALPHA) => (new Vector3(lt), at),
            (GlEnum.REPLACE, GlEnum.INTENSITY) => (new Vector3(lt), lt),
            (GlEnum.REPLACE, GlEnum.RGB) => (ct, af),
            (GlEnum.REPLACE, _) => (ct, at),

            (GlEnum.DECAL, GlEnum.RGB) => (ct, af),
            (GlEnum.DECAL, GlEnum.RGBA) => ((cf * (1 - at)) + (ct * at), af),
            (GlEnum.DECAL, _) => (cf, af),

            (GlEnum.BLEND, GlEnum.ALPHA) => (cf, af * at),
            (GlEnum.BLEND, GlEnum.INTENSITY) => ((cf * (1 - lt)) + (cc * lt), (af * (1 - lt)) + (State.TexEnvColor.W * lt)),
            (GlEnum.BLEND, GlEnum.LUMINANCE) => ((cf * (1 - lt)) + (cc * lt), af),
            (GlEnum.BLEND, GlEnum.LUMINANCE_ALPHA) => ((cf * (1 - lt)) + (cc * lt), af * at),
            (GlEnum.BLEND, GlEnum.RGB) => ((cf * (Vector3.One - ct)) + (cc * ct), af),
            (GlEnum.BLEND, _) => ((cf * (Vector3.One - ct)) + (cc * ct), af * at),

            (GlEnum.ADD, GlEnum.ALPHA) => (cf, af * at),
            (GlEnum.ADD, GlEnum.INTENSITY) => (cf + new Vector3(lt), af + lt),
            (GlEnum.ADD, GlEnum.LUMINANCE) => (cf + new Vector3(lt), af),
            (GlEnum.ADD, GlEnum.LUMINANCE_ALPHA) => (cf + new Vector3(lt), af * at),
            (GlEnum.ADD, GlEnum.RGB) => (cf + ct, af),
            (GlEnum.ADD, _) => (cf + ct, af * at),

            // MODULATE
            (_, GlEnum.ALPHA) => (cf, af * at),
            (_, GlEnum.INTENSITY) => (cf * lt, af * lt),
            (_, GlEnum.LUMINANCE) => (cf * lt, af),
            (_, GlEnum.LUMINANCE_ALPHA) => (cf * lt, af * at),
            (_, GlEnum.RGB) => (cf * ct, af),
            _ => (cf * ct, af * at),
        };
        return new Vector4(c, a);
    }

    private static Vector4 Sample(GlTexture texture, GlTexImage image, float s, float t)
    {
        int w = image.Width, h = image.Height;
        if (texture.MagFilter == GlEnum.NEAREST)
        {
            return Texel(image, Wrap(texture.WrapS, (int)MathF.Floor(WrapCoord(texture.WrapS, s) * w), w),
                Wrap(texture.WrapT, (int)MathF.Floor(WrapCoord(texture.WrapT, t) * h), h));
        }
        float u = (WrapCoord(texture.WrapS, s) * w) - 0.5f, v = (WrapCoord(texture.WrapT, t) * h) - 0.5f;
        int i0 = (int)MathF.Floor(u), j0 = (int)MathF.Floor(v);
        float a = u - i0, b = v - j0;
        int x0 = Wrap(texture.WrapS, i0, w), x1 = Wrap(texture.WrapS, i0 + 1, w);
        int y0 = Wrap(texture.WrapT, j0, h), y1 = Wrap(texture.WrapT, j0 + 1, h);
        return ((1 - a) * (1 - b) * Texel(image, x0, y0)) + (a * (1 - b) * Texel(image, x1, y0))
               + ((1 - a) * b * Texel(image, x0, y1)) + (a * b * Texel(image, x1, y1));
    }

    private static float WrapCoord(uint mode, float c) => mode == GlEnum.REPEAT ? c - MathF.Floor(c) : Math.Clamp(c, 0, 1);

    private static int Wrap(uint mode, int i, int size) => mode == GlEnum.REPEAT ? ((i % size) + size) % size : Math.Clamp(i, 0, size - 1);

    private static Vector4 Texel(GlTexImage image, int x, int y)
    {
        int o = ((y * image.Width) + x) * 4;
        byte[] t = image.Texels;
        return o + 3 < t.Length ? new Vector4(t[o] / 255f, t[o + 1] / 255f, t[o + 2] / 255f, t[o + 3] / 255f) : Vector4.Zero;
    }

    // ------------------------------------------------------------------ DrawPixels / Bitmap / CopyPixels

    private void DrawPixels(ref GlReader r)
    {
        PixelStore store = ReadPixelStore(ref r);
        int width = r.I32(), height = r.I32();
        uint format = r.U32(), type = r.U32();
        if (width < 0 || height < 0)
        {
            SetError(GlEnum.INVALID_VALUE);
            return;
        }
        if (!State.RasterValid || format is GlEnum.DEPTH_COMPONENT or GlEnum.STENCIL_INDEX or GlEnum.COLOR_INDEX)
        {
            return;   // 深度 / 模板 / 颜色索引的像素矩形不实现
        }
        Vector4[]? pixels = UnpackImage(r.Rest(), store, width, height, format, type, r.BigEndian);
        if (pixels is null || pixels.Length == 0)
        {
            return;
        }
        DrawRectangle(pixels, width, height);
    }

    /// <summary>把一个像素矩形按光栅位置与 PixelZoom 画出去(§3.6.5):每个源像素覆盖一块 zoom 大小的区域。</summary>
    private void DrawRectangle(Vector4[] pixels, int width, int height)
    {
        if (!PrepareRaster())
        {
            return;
        }
        float xr = State.RasterPos.X, yr = State.RasterPos.Y, z = State.RasterPos.Z;
        float zx = State.ZoomX, zy = State.ZoomY;
        for (int j = 0; j < height; j++)
        {
            float ya = yr + (zy * j), yb = yr + (zy * (j + 1));
            int y0 = (int)MathF.Ceiling(MathF.Min(ya, yb) - 0.5f), y1 = (int)MathF.Ceiling(MathF.Max(ya, yb) - 0.5f);
            for (int i = 0; i < width; i++)
            {
                float xa = xr + (zx * i), xb = xr + (zx * (i + 1));
                int x0 = (int)MathF.Ceiling(MathF.Min(xa, xb) - 0.5f), x1 = (int)MathF.Ceiling(MathF.Max(xa, xb) - 0.5f);
                Vector4 color = pixels[(j * width) + i];
                for (int y = Math.Max(y0, _clipY0); y < Math.Min(y1, _clipY1); y++)
                {
                    for (int x = Math.Max(x0, _clipX0); x < Math.Min(x1, _clipX1); x++)
                    {
                        Fragment(x, y, z, color, Vector3.Zero, State.RasterTexCoord, State.RasterDistance);
                    }
                }
            }
        }
    }

    /// <summary>Bitmap(§3.7):置位的像素以光栅颜色生成片元,之后光栅位置按 (xmove, ymove) 前移。</summary>
    private void Bitmap(ref GlReader r)
    {
        r.Skip(1);
        bool lsbFirst = r.U8() != 0;
        r.Skip(2);
        int rowLength = r.I32(), skipRows = r.I32(), skipPixels = r.I32(), alignment = r.I32();
        int width = r.I32(), height = r.I32();
        float xorig = r.F32(), yorig = r.F32(), xmove = r.F32(), ymove = r.F32();
        if (width < 0 || height < 0)
        {
            SetError(GlEnum.INVALID_VALUE);
            return;
        }
        if (!State.RasterValid)
        {
            return;
        }
        ReadOnlySpan<byte> bits = r.Rest();
        if (width > 0 && height > 0 && PrepareRaster())
        {
            alignment = alignment is 1 or 2 or 4 or 8 ? alignment : 4;
            int groups = rowLength > 0 ? rowLength : width;
            int k = alignment * ((groups + (8 * alignment) - 1) / (8 * alignment));
            int x0 = (int)MathF.Floor(State.RasterPos.X - xorig), y0 = (int)MathF.Floor(State.RasterPos.Y - yorig);
            for (int j = 0; j < height; j++)
            {
                int y = y0 + j;
                if (y < _clipY0 || y >= _clipY1)
                {
                    continue;
                }
                for (int i = 0; i < width; i++)
                {
                    int bit = i + skipPixels;
                    int index = ((j + skipRows) * k) + (bit / 8);
                    if (index >= bits.Length)
                    {
                        continue;
                    }
                    int h = lsbFirst ? bit % 8 : 7 - (bit % 8);
                    int x = x0 + i;
                    if (((bits[index] >> h) & 1) != 0 && x >= _clipX0 && x < _clipX1)
                    {
                        Fragment(x, y, State.RasterPos.Z, State.RasterColor, Vector3.Zero, State.RasterTexCoord, State.RasterDistance);
                    }
                }
            }
        }
        State.RasterPos += new Vector4(xmove, ymove, 0, 0);
    }

    private void CopyPixels(int x, int y, int width, int height, uint type)
    {
        if (width < 0 || height < 0)
        {
            SetError(GlEnum.INVALID_VALUE);
            return;
        }
        if (type != GlEnum.COLOR || !State.RasterValid)
        {
            return;   // 深度 / 模板的拷贝不实现
        }
        var pixels = new Vector4[width * height];
        for (int j = 0; j < height; j++)
        {
            for (int i = 0; i < width; i++)
            {
                pixels[(j * width) + i] = ReadColorPixel(x + i, y + j);
            }
        }
        DrawRectangle(pixels, width, height);
    }

    // ------------------------------------------------------------------ 读缓冲

    private uint[]? ReadColorBuffer() => Read is not { } s ? null : State.ReadBuffer switch
    {
        GlEnum.BACK or GlEnum.BACK_LEFT => s.Color(back: true),
        GlEnum.FRONT or GlEnum.FRONT_LEFT or GlEnum.LEFT or GlEnum.FRONT_AND_BACK => s.Front,
        _ => null,
    };

    /// <summary>读缓冲里 GL 窗口坐标 (x, y) 的颜色;越界为 0。</summary>
    private Vector4 ReadColorPixel(int x, int y)
    {
        if (Read is not { } s || ReadColorBuffer() is not { } buffer || (uint)x >= (uint)s.Width || (uint)y >= (uint)s.Height)
        {
            return Vector4.Zero;
        }
        return Unpack(buffer[((s.Height - 1 - y) * s.Width) + x], s.HasAlpha);
    }

    /// <summary>
    /// ReadPixels:按附录 A.3.1 打包(第 0 行是 y 最小的那一行;行跨度在元素小于 4 字节时补齐到 4 字节)。
    /// 格式或类型不认识时记 INVALID_ENUM 并返回 null。
    /// </summary>
    public byte[]? ReadPixels(int x, int y, int width, int height, uint format, uint type, bool swapBytes, bool bigEndian)
    {
        if (width < 0 || height < 0)
        {
            SetError(GlEnum.INVALID_VALUE);
            return null;
        }
        int elements = FormatElements(format);
        (int nbytes, bool packed) = TypeSize(type);
        if (elements == 0 || nbytes == 0 || format == GlEnum.COLOR_INDEX)
        {
            SetError(GlEnum.INVALID_ENUM);
            return null;
        }
        int groupElements = packed ? 1 : elements;
        int rowBytes = nbytes * groupElements * width;
        int k = nbytes >= 4 ? rowBytes : (rowBytes + 3) & ~3;
        byte[] data = new byte[k * height];
        bool elementBigEndian = swapBytes ? !bigEndian : bigEndian;
        Span<float> comp = stackalloc float[4];
        for (int j = 0; j < height; j++)
        {
            for (int i = 0; i < width; i++)
            {
                int px = x + i, py = y + j;
                if (format == GlEnum.DEPTH_COMPONENT)
                {
                    comp[0] = Read is { } s && (uint)px < (uint)s.Width && (uint)py < (uint)s.Height
                        ? s.Depth[((s.Height - 1 - py) * s.Width) + px] : 0;
                }
                else if (format == GlEnum.STENCIL_INDEX)
                {
                    comp[0] = Read is { } s && (uint)px < (uint)s.Width && (uint)py < (uint)s.Height
                        ? s.Stencil[((s.Height - 1 - py) * s.Width) + px] : 0;
                }
                else
                {
                    Vector4 c = ReadColorPixel(px, py);
                    FromRgba(format, c, comp);
                }
                Span<byte> group = data.AsSpan((j * k) + (i * groupElements * nbytes), groupElements * nbytes);
                if (packed)
                {
                    (int Shift, int Bits)[] layout = PackedLayout(type);
                    uint word = 0;
                    for (int c = 0; c < layout.Length; c++)
                    {
                        uint max = (1u << layout[c].Bits) - 1;
                        word |= ((uint)((Math.Clamp(comp[c], 0, 1) * max) + 0.5f) & max) << layout[c].Shift;
                    }
                    WriteUnsigned(group, word, nbytes, elementBigEndian);
                }
                else
                {
                    for (int c = 0; c < elements; c++)
                    {
                        WriteElement(group.Slice(c * nbytes, nbytes), comp[c], type, elementBigEndian, raw: format == GlEnum.STENCIL_INDEX);
                    }
                }
            }
        }
        return data;
    }

    /// <summary>RGBA 按格式拆成分量(LUMINANCE = R + G + B 再钳位,§4.3.2)。</summary>
    private static void FromRgba(uint format, Vector4 c, Span<float> o)
    {
        float l = Math.Clamp(c.X + c.Y + c.Z, 0, 1);
        switch (format)
        {
            case GlEnum.RGBA:
                (o[0], o[1], o[2], o[3]) = (c.X, c.Y, c.Z, c.W);
                break;
            case GlEnum.RGB:
                (o[0], o[1], o[2]) = (c.X, c.Y, c.Z);
                break;
            case GlEnum.BGRA:
                (o[0], o[1], o[2], o[3]) = (c.Z, c.Y, c.X, c.W);
                break;
            case GlEnum.BGR:
                (o[0], o[1], o[2]) = (c.Z, c.Y, c.X);
                break;
            case GlEnum.RED:
                o[0] = c.X;
                break;
            case GlEnum.GREEN:
                o[0] = c.Y;
                break;
            case GlEnum.BLUE:
                o[0] = c.Z;
                break;
            case GlEnum.ALPHA:
                o[0] = c.W;
                break;
            case GlEnum.LUMINANCE:
                o[0] = l;
                break;
            default:
                (o[0], o[1]) = (l, c.W);
                break;
        }
    }

    /// <summary>一个分量按类型写出(Table 4.7);模板值(<paramref name="raw" />)不归一化。</summary>
    private static void WriteElement(Span<byte> e, float v, uint type, bool bigEndian, bool raw)
    {
        if (type == GlEnum.FLOAT)
        {
            if (bigEndian)
            {
                BinaryPrimitives.WriteSingleBigEndian(e, v);
            }
            else
            {
                BinaryPrimitives.WriteSingleLittleEndian(e, v);
            }
            return;
        }
        double c = raw ? v : Math.Clamp(v, type is GlEnum.BYTE or GlEnum.SHORT or GlEnum.INT ? -1 : 0, 1);
        uint word = type switch
        {
            GlEnum.UNSIGNED_BYTE => raw ? (uint)c & 0xFF : (uint)Math.Round(c * 255),
            GlEnum.BYTE => raw ? (uint)c & 0xFF : (uint)(sbyte)Math.Round(((c * 255) - 1) / 2),
            GlEnum.UNSIGNED_SHORT => raw ? (uint)c & 0xFFFF : (uint)Math.Round(c * 65535),
            GlEnum.SHORT => raw ? (uint)c & 0xFFFF : (ushort)(short)Math.Round(((c * 65535) - 1) / 2),
            GlEnum.UNSIGNED_INT => raw ? (uint)c : (uint)Math.Round(c * 4294967295.0),
            _ => raw ? (uint)c : (uint)(int)Math.Round(((c * 4294967295.0) - 1) / 2),
        };
        WriteUnsigned(e, word, e.Length, bigEndian);
    }

    private static void WriteUnsigned(Span<byte> e, uint word, int bytes, bool bigEndian)
    {
        switch (bytes)
        {
            case 1:
                e[0] = (byte)word;
                break;
            case 2:
                if (bigEndian)
                {
                    BinaryPrimitives.WriteUInt16BigEndian(e, (ushort)word);
                }
                else
                {
                    BinaryPrimitives.WriteUInt16LittleEndian(e, (ushort)word);
                }
                break;
            default:
                if (bigEndian)
                {
                    BinaryPrimitives.WriteUInt32BigEndian(e, word);
                }
                else
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(e, word);
                }
                break;
        }
    }

    /// <summary>GetTexImage:第 <paramref name="level" /> 级按附录 A.3.1 打包;没有这一级时返回空。</summary>
    public byte[]? GetTexImage(uint target, int level, uint format, uint type, bool swapBytes, bool bigEndian, out int width, out int height)
    {
        width = height = 0;
        if (target is not (GlEnum.TEXTURE_1D or GlEnum.TEXTURE_2D) || level < 0 || level >= GlTexture.MaxLevels)
        {
            SetError(GlEnum.INVALID_ENUM);
            return null;
        }
        if (BoundTexture(target)?.Levels[level] is not { } image)
        {
            return [];
        }
        (width, height) = (image.Width, image.Height);
        int elements = FormatElements(format);
        (int nbytes, bool packed) = TypeSize(type);
        if (elements == 0 || nbytes == 0 || format is GlEnum.COLOR_INDEX or GlEnum.STENCIL_INDEX or GlEnum.DEPTH_COMPONENT)
        {
            SetError(GlEnum.INVALID_ENUM);
            return null;
        }
        int groupElements = packed ? 1 : elements;
        int rowBytes = nbytes * groupElements * width;
        int k = nbytes >= 4 ? rowBytes : (rowBytes + 3) & ~3;
        byte[] data = new byte[k * height];
        bool elementBigEndian = swapBytes ? !bigEndian : bigEndian;
        Span<float> comp = stackalloc float[4];
        for (int j = 0; j < height; j++)
        {
            for (int i = 0; i < width; i++)
            {
                Vector4 c = Texel(image, i, j);
                if (image.BaseFormat is GlEnum.LUMINANCE or GlEnum.LUMINANCE_ALPHA or GlEnum.INTENSITY && format is GlEnum.LUMINANCE or GlEnum.LUMINANCE_ALPHA)
                {
                    c = new Vector4(c.X, 0, 0, c.W);   // 纹理的亮度直接给出,不按 R + G + B 相加
                }
                FromRgba(format, c, comp);
                Span<byte> group = data.AsSpan((j * k) + (i * groupElements * nbytes), groupElements * nbytes);
                if (packed)
                {
                    (int Shift, int Bits)[] layout = PackedLayout(type);
                    uint word = 0;
                    for (int ci = 0; ci < layout.Length; ci++)
                    {
                        uint max = (1u << layout[ci].Bits) - 1;
                        word |= ((uint)((Math.Clamp(comp[ci], 0, 1) * max) + 0.5f) & max) << layout[ci].Shift;
                    }
                    WriteUnsigned(group, word, nbytes, elementBigEndian);
                }
                else
                {
                    for (int ci = 0; ci < elements; ci++)
                    {
                        WriteElement(group.Slice(ci * nbytes, nbytes), comp[ci], type, elementBigEndian, raw: false);
                    }
                }
            }
        }
        return data;
    }
}

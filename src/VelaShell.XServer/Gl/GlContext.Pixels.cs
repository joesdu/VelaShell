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
using VelaShell.XServer.Protocol;

namespace VelaShell.XServer.Gl;

internal sealed partial class GlContext
{
    private readonly GlTexture _default1D = new(0) { Target = GlEnum.TEXTURE_1D };
    private readonly GlTexture _default2D = new(0) { Target = GlEnum.TEXTURE_2D };
    private readonly GlTexture _proxy1D = new(0) { Target = GlEnum.PROXY_TEXTURE_1D };
    private readonly GlTexture _proxy2D = new(0) { Target = GlEnum.PROXY_TEXTURE_2D };

    /// <summary>目标当前绑定的纹理对象(含名字 0 的默认纹理与代理纹理)。</summary>
    private GlTexture? BoundTexture(uint target) => target switch
    {
        GlEnum.TEXTURE_1D or GlEnum.TEXTURE_2D when TextureBinding(target) is not 0 and var name => Shared.Textures[name],
        GlEnum.TEXTURE_1D => _default1D,
        GlEnum.TEXTURE_2D => _default2D,
        GlEnum.PROXY_TEXTURE_1D => _proxy1D,
        GlEnum.PROXY_TEXTURE_2D => _proxy2D,
        _ => null,
    };

    /// <summary>
    /// TEXTURE_1D / TEXTURE_2D 当前绑定的纹理名。名字已不在共享组里时按「删掉即退回 0」处理(§3.8.12,与本上下文 DeleteTextures
    /// 的效果一样)并就地改回 0。名字悬空有三条来路:PushAttrib(TEXTURE_BIT) 之后删了当前绑定再 PopAttrib(合法的 GL 序列)、
    /// CopyContext 拷来另一个共享组的名字、共享组里另一个上下文删了它。原先 TexImage / CopyTexImage 拿到 null 抛
    /// NullReferenceException,客户端收到 BadImplementation。
    /// </summary>
    private uint TextureBinding(uint target)
    {
        ref uint bound = ref target == GlEnum.TEXTURE_1D ? ref State.Texture1D : ref State.Texture2D;
        if (bound != 0 && !Shared.Textures.ContainsKey(bound))
        {
            bound = 0;
        }
        return bound;
    }

    /// <summary>这个纹理对象是共享名字空间里有名字的那一个(记在共享组的账上;名字 0 的默认纹理记在本上下文的账上)。</summary>
    private bool IsNamed(GlTexture texture) =>
        texture.Name != 0 && Shared.Textures.TryGetValue(texture.Name, out GlTexture? named) && ReferenceEquals(named, texture);

    /// <summary>
    /// 给纹理的第 <paramref name="level" /> 级换一张 <paramref name="bytes" /> 字节的图像:分配之前先记账。有名字的记在共享组上
    /// (组的上限,加上建组客户端的账),名字 0 的默认纹理记在本上下文的账上 —— 原先不记,默认 2D 纹理 12 级、每级 2048² 就是 192 MB;
    /// 代理纹理不存纹素,不占账。记不下时记 OUT_OF_MEMORY、返回 false,账不变;变小的当场退账。之后调用方必须换上这一级。
    /// </summary>
    private bool TryAccountLevel(GlTexture texture, int level, long bytes)
    {
        long delta = bytes - (texture.Levels[level]?.Texels.Length ?? 0);
        if (delta == 0)
        {
            return true;
        }
        bool named = IsNamed(texture);
        if (delta < 0)
        {
            if (named)
            {
                Shared.RefundTexture(-delta);
            }
            else
            {
                Account?.Refund(-delta);
            }
            return true;
        }
        if (named ? Shared.TryChargeTexture(delta) : Account?.TryCharge(delta) ?? true)
        {
            return true;
        }
        SetError(GlEnum.OUT_OF_MEMORY);
        return false;
    }

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
                if (Shared.Textures.Count >= GlShared.MaxTextures)
                {
                    SetError(GlEnum.OUT_OF_MEMORY);   // 绑一个没用过的名字就建一个纹理对象:名字同样有上限
                    return;
                }
                texture = new GlTexture(name);
                Shared.AddTexture(texture);
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
        // 过滤与环绕方式只收规范列出的值(§3.8.4,含 SGIS_texture_edge_clamp 的 CLAMP_TO_EDGE),别的记 INVALID_ENUM、不改。
        uint value = EnumParam(v[0]);
        switch (pname)
        {
            case GlEnum.TEXTURE_MIN_FILTER when value is GlEnum.NEAREST or GlEnum.LINEAR or GlEnum.NEAREST_MIPMAP_NEAREST
                                                or GlEnum.LINEAR_MIPMAP_NEAREST or GlEnum.NEAREST_MIPMAP_LINEAR or GlEnum.LINEAR_MIPMAP_LINEAR:
                t.MinFilter = value;
                break;
            case GlEnum.TEXTURE_MAG_FILTER when value is GlEnum.NEAREST or GlEnum.LINEAR:
                t.MagFilter = value;
                break;
            case GlEnum.TEXTURE_WRAP_S when IsWrapMode(value):
                t.WrapS = value;
                break;
            case GlEnum.TEXTURE_WRAP_T when IsWrapMode(value):
                t.WrapT = value;
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

    private static bool IsWrapMode(uint mode) => mode is GlEnum.CLAMP or GlEnum.REPEAT or GlEnum.CLAMP_TO_EDGE;

    /// <summary>
    /// 浮点给的枚举参数(TexParameterf、Fogf、TexEnvf……)换成枚举值。负数、NaN 与超出 32 位的换成一个不是任何枚举的值 ——
    /// 浮点转无符号整数在越界时的结果与平台有关。
    /// </summary>
    private static uint EnumParam(float value) => value is >= 0 and < 4294967296f ? (uint)value : uint.MaxValue;

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
        GlEnum.RGBA or GlEnum.BGRA or GlEnum.ABGR_EXT => 4,
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
    /// 一张客户端图像在命令数据里的布局(附录 A.2.1、§3.6.4):每组几个元素、每个元素几字节、行跨度(按 Alignment 补齐)。
    /// </summary>
    private readonly record struct ImageLayout(uint Format, uint Type, int Elements, int GroupElements, int ElementBytes, bool Packed,
        long RowStride, PixelStore Store, bool ElementBigEndian)
    {
        public int GroupBytes => GroupElements * ElementBytes;

        /// <summary>第 j 行第 i 个像素(组)在数据里的偏移。</summary>
        public long Offset(int i, int j) => ((j + (long)Store.SkipRows) * RowStride) + ((i + (long)Store.SkipPixels) * GroupBytes);

        /// <summary>数据装得下整张 width × height 的图像。</summary>
        public bool Covers(int dataLength, int width, int height) =>
            width == 0 || height == 0 || Offset(width - 1, height - 1) + GroupBytes <= dataLength;
    }

    /// <summary>
    /// 按格式、类型与像素存储参数算出图像布局。格式或类型不认识时记 INVALID_ENUM、像素存储参数为负时记 INVALID_VALUE,返回 null。
    /// </summary>
    private ImageLayout? Layout(PixelStore store, int width, uint format, uint type, bool bigEndian)
    {
        int elements = FormatElements(format);
        (int nbytes, bool packed) = TypeSize(type);
        if (elements == 0 || nbytes == 0 || format is GlEnum.COLOR_INDEX or GlEnum.STENCIL_INDEX or GlEnum.DEPTH_COMPONENT)
        {
            SetError(GlEnum.INVALID_ENUM);
            return null;
        }
        if (store.RowLength < 0 || store.SkipRows < 0 || store.SkipPixels < 0)
        {
            SetError(GlEnum.INVALID_VALUE);
            return null;
        }
        int groupElements = packed ? 1 : elements;
        long groups = store.RowLength > 0 ? store.RowLength : width;
        long rowBytes = (long)nbytes * groupElements * groups;
        long k = nbytes >= store.Alignment ? rowBytes : store.Alignment * ((rowBytes + store.Alignment - 1) / store.Alignment);
        return new ImageLayout(format, type, elements, groupElements, nbytes, packed, k, store, store.SwapBytes ? !bigEndian : bigEndian);
    }

    /// <summary>读出第 j 行第 i 个像素的 RGBA;数据里没有这个像素时为 0。</summary>
    private static Vector4 ReadGroup(ReadOnlySpan<byte> data, in ImageLayout layout, int i, int j, Span<float> comp)
    {
        long offset = layout.Offset(i, j);
        int groupBytes = layout.GroupBytes;
        if (offset < 0 || offset + groupBytes > data.Length)
        {
            return Vector4.Zero;
        }
        ReadOnlySpan<byte> group = data.Slice((int)offset, groupBytes);
        comp.Clear();
        comp[3] = 1;
        if (layout.Packed)
        {
            uint word = layout.ElementBytes switch
            {
                1 => group[0],
                2 => layout.ElementBigEndian ? BinaryPrimitives.ReadUInt16BigEndian(group) : BinaryPrimitives.ReadUInt16LittleEndian(group),
                _ => layout.ElementBigEndian ? BinaryPrimitives.ReadUInt32BigEndian(group) : BinaryPrimitives.ReadUInt32LittleEndian(group),
            };
            (int Shift, int Bits)[] packing = PackedLayout(layout.Type);
            for (int c = 0; c < 4; c++)
            {
                comp[c] = c < packing.Length ? ((word >> packing[c].Shift) & ((1u << packing[c].Bits) - 1)) / (float)((1u << packing[c].Bits) - 1) : 1;
            }
        }
        else
        {
            for (int c = 0; c < layout.Elements; c++)
            {
                comp[c] = ReadElement(group.Slice(c * layout.ElementBytes, layout.ElementBytes), layout.Type, layout.ElementBigEndian);
            }
        }
        return ToRgba(layout.Format, comp);
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
        GlEnum.ABGR_EXT => new Vector4(c[3], c[2], c[1], c[0]),   // GL_EXT_abgr:分量次序 A、B、G、R
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
        if (BoundTexture(target) is not { } texture)
        {
            SetError(GlEnum.INVALID_ENUM);
            return;
        }
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
        // 格式、类型与像素存储参数先核(客户端传 NULL 时也核),再扣工作量、记账,最后才分配。
        if (Layout(store, width, format, type, r.BigEndian) is not { } layout)
        {
            return;
        }
        ReadOnlySpan<byte> data = r.Rest();
        bool hasData = data.Length > 0 && w > 0 && h > 0;
        if (hasData)
        {
            // 带了数据就得装得下整张图像(含边框),否则命令作废 —— 与 DrawPixels 一样。原先 1 字节的数据也照声明的 2050² 逐个解码。
            if (!layout.Covers(data.Length, width, oneD ? 1 : height))
            {
                SetError(GlEnum.INVALID_VALUE);
                return;
            }
            WorkBudget.Charge(2L * w * h);   // 逐个解码
        }
        if (!TryAccountLevel(texture, level, (long)w * h * 4))
        {
            return;
        }
        // 数据为空(客户端传 NULL)时纹理内容未定义:这里填 0。边框像素只存内圈。
        byte[] texels = new byte[w * h * 4];
        if (hasData)
        {
            Span<float> comp = stackalloc float[4];
            int rowOffset = oneD ? 0 : border;
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    StoreTexel(texels, (y * w) + x, ReadGroup(data, layout, x + border, y + rowOffset, comp), baseFormat);
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
        if (target != (oneD ? GlEnum.TEXTURE_1D : GlEnum.TEXTURE_2D))
        {
            SetError(GlEnum.INVALID_ENUM);
            return;
        }
        if (level is < 0 or >= GlTexture.MaxLevels)
        {
            SetError(GlEnum.INVALID_VALUE);
            return;
        }
        if (BoundTexture(target)?.Levels[level] is not { } image)
        {
            SetError(GlEnum.INVALID_OPERATION);
            return;
        }
        // 按 long 比:xoffset + width 在 int 上会溢出成负数,越界的写入就混过去了(原先抛 IndexOutOfRange 当 BadImplementation)。
        if (xoffset < 0 || yoffset < 0 || width < 0 || height < 0 || (long)xoffset + width > image.Width || (long)yoffset + height > image.Height)
        {
            SetError(GlEnum.INVALID_VALUE);
            return;
        }
        if (Layout(store, width, format, type, r.BigEndian) is not { } layout)
        {
            return;
        }
        ReadOnlySpan<byte> data = r.Rest();
        if (data.IsEmpty || width == 0 || height == 0)
        {
            return;   // 没有数据(客户端传 NULL):什么都不改
        }
        if (!layout.Covers(data.Length, width, height))
        {
            SetError(GlEnum.INVALID_VALUE);   // 数据装不下声明的矩形:命令作废(同 DrawPixels)
            return;
        }
        WorkBudget.Charge(2L * width * height);   // 逐个解码,直接写进纹素(不先整张解成浮点)
        Span<float> comp = stackalloc float[4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                StoreTexel(image.Texels, ((y + yoffset) * image.Width) + x + xoffset, ReadGroup(data, layout, x, y, comp), image.BaseFormat);
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
        if (BoundTexture(target) is not { } texture)
        {
            SetError(GlEnum.INVALID_ENUM);
            return;
        }
        (w, h) = (Math.Max(0, w), Math.Max(0, h));
        WorkBudget.Charge(2L * w * h);
        if (!TryAccountLevel(texture, level, (long)w * h * 4))
        {
            return;
        }
        byte[] texels = new byte[w * h * 4];
        for (int j = 0; j < h; j++)
        {
            for (int i = 0; i < w; i++)
            {
                StoreTexel(texels, (j * w) + i, ReadColorPixel(x + i + border, y + j + (oneD ? 0 : border)), baseFormat);
            }
        }
        texture.Levels[level] = new GlTexImage(w, h, internalFormat, baseFormat, texels);
    }

    /// <summary>
    /// CopyTexSubImage1D / 2D(§3.8.2):target 只能是 TEXTURE_1D / TEXTURE_2D —— 原先不核,代理目标的那一级只记尺寸、
    /// 纹素是空数组,往里写抛 IndexOutOfRange,客户端收到 BadImplementation。
    /// </summary>
    private void CopyTexSubImage(uint target, int level, int xoffset, int yoffset, int x, int y, int width, int height, bool oneD)
    {
        if (target != (oneD ? GlEnum.TEXTURE_1D : GlEnum.TEXTURE_2D))
        {
            SetError(GlEnum.INVALID_ENUM);
            return;
        }
        if (level is < 0 or >= GlTexture.MaxLevels)
        {
            SetError(GlEnum.INVALID_VALUE);
            return;
        }
        if (BoundTexture(target)?.Levels[level] is not { } image)
        {
            SetError(GlEnum.INVALID_OPERATION);   // 这一级没有用 TexImage 定义过
            return;
        }
        // 按 long 比:xoffset + width 在 int 上会溢出成负数,越界的写入就混过去了(原先抛 IndexOutOfRange 当 BadImplementation)。
        if (xoffset < 0 || yoffset < 0 || width < 0 || height < 0 || (long)xoffset + width > image.Width || (long)yoffset + height > image.Height)
        {
            SetError(GlEnum.INVALID_VALUE);
            return;
        }
        WorkBudget.Charge(2L * width * height);
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
        float u = (WrapCoord(texture.WrapS, s) * w) - 0.5f;
        int i0 = (int)MathF.Floor(u);
        float a = u - i0;
        int x0 = LinearIndex(texture.WrapS, i0, w), x1 = LinearIndex(texture.WrapS, i0 + 1, w);
        if (texture.Target == GlEnum.TEXTURE_1D)
        {
            // 一维纹理只沿 s 插值(t 无关,§3.8.8):不然 CLAMP 的 t 方向会把边框色混进来。
            return ((1 - a) * TexelOrBorder(texture, image, x0, 0)) + (a * TexelOrBorder(texture, image, x1, 0));
        }
        float v = (WrapCoord(texture.WrapT, t) * h) - 0.5f;
        int j0 = (int)MathF.Floor(v);
        float b = v - j0;
        int y0 = LinearIndex(texture.WrapT, j0, h), y1 = LinearIndex(texture.WrapT, j0 + 1, h);
        return ((1 - a) * (1 - b) * TexelOrBorder(texture, image, x0, y0)) + (a * (1 - b) * TexelOrBorder(texture, image, x1, y0))
               + ((1 - a) * b * TexelOrBorder(texture, image, x0, y1)) + (a * b * TexelOrBorder(texture, image, x1, y1));
    }

    private static float WrapCoord(uint mode, float c) => mode == GlEnum.REPEAT ? c - MathF.Floor(c) : Math.Clamp(c, 0, 1);

    /// <summary>NEAREST 的纹素下标:REPEAT 取模,其余夹到图像里(CLAMP 在 s = 1 时取最后一个纹素,§3.8.8)。</summary>
    private static int Wrap(uint mode, int i, int size) => mode == GlEnum.REPEAT ? ((i % size) + size) % size : Math.Clamp(i, 0, size - 1);

    /// <summary>
    /// LINEAR 取的纹素下标(§3.8.7–3.8.8):REPEAT 取模,CLAMP_TO_EDGE 夹到图像里;CLAMP 取到图像之外时给 −1,表示用边框色
    /// (TEXTURE_BORDER_COLOR)—— 原先也夹到图像里,GL_CLAMP 配 LINEAR 的边上不与边框色混合。
    /// </summary>
    private static int LinearIndex(uint mode, int i, int size) => mode switch
    {
        GlEnum.REPEAT => ((i % size) + size) % size,
        GlEnum.CLAMP => (uint)i < (uint)size ? i : -1,
        _ => Math.Clamp(i, 0, size - 1),
    };

    private static Vector4 TexelOrBorder(GlTexture texture, GlTexImage image, int x, int y) =>
        x < 0 || y < 0 ? texture.BorderColor : Texel(image, x, y);

    private static Vector4 Texel(GlTexImage image, int x, int y)
    {
        int o = ((y * image.Width) + x) * 4;
        byte[] t = image.Texels;
        return o + 3 < t.Length ? new Vector4(t[o] / 255f, t[o + 1] / 255f, t[o + 2] / 255f, t[o + 3] / 255f) : Vector4.Zero;
    }

    // ------------------------------------------------------------------ DrawPixels / Bitmap / CopyPixels

    /// <summary>
    /// DrawPixels(§3.6.4):数据得装得下整张图像,否则命令作废(INVALID_VALUE)—— 不然 width × height 就不受数据量约束。
    /// 只解、只画按 PixelZoom 放大后落进裁剪范围的那部分源像素,一行一行来,不整张解成浮点。
    /// </summary>
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
        if (Layout(store, width, format, type, r.BigEndian) is not { } layout)
        {
            return;
        }
        ReadOnlySpan<byte> data = r.Rest();
        if (!layout.Covers(data.Length, width, height))
        {
            SetError(GlEnum.INVALID_VALUE);
            return;
        }
        if (VisiblePixels(width, height) is not var (i0, i1, j0, j1))
        {
            return;
        }
        // 只解码放大之后真盖得住像素中心的源像素:列与行各自先挑出来,其余的不碰。PixelZoom 很小(1e-6)时一整行源像素
        // 可能只落进一个像素 —— 原先照样逐个解码,而 ROW_LENGTH 比 width 小时各行在数据里重叠,16 KB 就能声称 8000 × 8000。
        WorkBudget.Charge(1L + i1 - i0);
        List<int> columns = [];
        for (int i = i0; i < i1; i++)
        {
            (int x0, int x1) = PixelColumnSpan(i);
            if (x0 < x1)
            {
                columns.Add(i);
            }
        }
        if (columns.Count == 0)
        {
            return;
        }
        Span<float> comp = stackalloc float[4];
        for (int j = j0; j < j1; j++)
        {
            WorkBudget.Charge(1);
            (int y0, int y1) = PixelRowSpan(j);
            if (y0 >= y1)
            {
                continue;
            }
            WorkBudget.Charge(columns.Count);
            foreach (int i in columns)
            {
                (int x0, int x1) = PixelColumnSpan(i);
                DrawZoomedPixel(x0, x1, y0, y1, ReadGroup(data, layout, i, j, comp));
            }
        }
    }

    /// <summary>
    /// width × height 的像素矩形按光栅位置与 PixelZoom 放大之后,可能落进裁剪范围的那部分源像素 [I0, I1) × [J0, J1)
    /// (略放宽一两个像素,<see cref="DrawRow" /> 逐个像素还会再裁);一个都落不进去返回 null。
    /// </summary>
    private (int I0, int I1, int J0, int J1)? VisiblePixels(int width, int height)
    {
        if (!PrepareRaster())
        {
            return null;
        }
        (int i0, int i1) = VisibleRange(State.RasterPos.X, State.ZoomX, width, _clipX0, _clipX1);
        (int j0, int j1) = VisibleRange(State.RasterPos.Y, State.ZoomY, height, _clipY0, _clipY1);
        return i0 < i1 && j0 < j1 ? (i0, i1, j0, j1) : null;
    }

    /// <summary>源像素下标 [0, count) 里,放大 <paramref name="zoom" /> 倍、从 <paramref name="origin" /> 画起时可能落进 [lo, hi) 的一段。</summary>
    private static (int First, int Last) VisibleRange(float origin, float zoom, int count, int lo, int hi)
    {
        if (count <= 0 || lo >= hi || zoom == 0 || !float.IsFinite(zoom) || !float.IsFinite(origin))
        {
            return (0, 0);
        }
        double a = (lo - 1.0 - origin) / zoom, b = (hi + 1.0 - origin) / zoom;
        double first = Math.Floor(Math.Min(a, b)) - 1, last = Math.Ceiling(Math.Max(a, b)) + 1;
        return ((int)Math.Clamp(first, 0, count), (int)Math.Clamp(last, 0, count));
    }

    /// <summary>第 j 行源像素放大之后盖住的窗口行 [Y0, Y1)(已裁到剪裁框;空的表示一个像素中心都没盖住)。</summary>
    private (int Y0, int Y1) PixelRowSpan(int j)
    {
        float yr = State.RasterPos.Y, zy = State.ZoomY;
        float ya = yr + (zy * j), yb = yr + (zy * (j + 1));
        return ((int)Math.Clamp(MathF.Ceiling(MathF.Min(ya, yb) - 0.5f), _clipY0, _clipY1),
            (int)Math.Clamp(MathF.Ceiling(MathF.Max(ya, yb) - 0.5f), _clipY0, _clipY1));
    }

    /// <summary>第 i 列源像素放大之后盖住的窗口列 [X0, X1)。</summary>
    private (int X0, int X1) PixelColumnSpan(int i)
    {
        float xr = State.RasterPos.X, zx = State.ZoomX;
        float xa = xr + (zx * i), xb = xr + (zx * (i + 1));
        return ((int)Math.Clamp(MathF.Ceiling(MathF.Min(xa, xb) - 0.5f), _clipX0, _clipX1),
            (int)Math.Clamp(MathF.Ceiling(MathF.Max(xa, xb) - 0.5f), _clipX0, _clipX1));
    }

    /// <summary>画像素矩形的第 j 行里从第 i0 个起的一段(§3.6.5):每个源像素覆盖一块 zoom 大小的区域。</summary>
    private void DrawRow(int j, int i0, ReadOnlySpan<Vector4> colors)
    {
        (int y0, int y1) = PixelRowSpan(j);
        for (int n = 0; n < colors.Length && y0 < y1; n++)
        {
            (int x0, int x1) = PixelColumnSpan(i0 + n);
            DrawZoomedPixel(x0, x1, y0, y1, colors[n]);
        }
    }

    /// <summary>一个源像素放大后的那一块:[x0, x1) × [y0, y1) 里每个像素一个片元。</summary>
    private void DrawZoomedPixel(int x0, int x1, int y0, int y1, Vector4 color)
    {
        float z = State.RasterPos.Z;
        for (int y = y0; y < y1; y++)
        {
            for (int x = x0; x < x1; x++)
            {
                Fragment(x, y, z, color, Vector3.Zero, State.RasterTexCoord, State.RasterDistance);
            }
        }
    }

    /// <summary>
    /// PolygonStipple(§3.5.2;GLX 编码:像素存储头 lsbfirst / rowlength / skiprows / skippixels / alignment,再是位图):
    /// 32×32 的图样按 DrawPixels 的规则解包(BITMAP、COLOR_INDEX),与 <see cref="Bitmap" /> 同一套寻址;数据装不下整张时 INVALID_VALUE。
    /// 总是换一个新数组(PushAttrib 存的那份与当前的共用旧数组,不能改它)。
    /// </summary>
    private void SetPolygonStipple(ref GlReader r)
    {
        r.Skip(1);
        bool lsbFirst = r.U8() != 0;
        r.Skip(2);
        int rowLength = r.I32(), skipRows = r.I32(), skipPixels = r.I32(), alignment = r.I32();
        if (rowLength < 0 || skipRows < 0 || skipPixels < 0)
        {
            SetError(GlEnum.INVALID_VALUE);
            return;
        }
        ReadOnlySpan<byte> bits = r.Rest();
        alignment = alignment is 1 or 2 or 4 or 8 ? alignment : 4;
        long groups = rowLength > 0 ? rowLength : 32;
        long k = alignment * ((groups + (8L * alignment) - 1) / (8L * alignment));
        long needed = ((skipRows + 31L) * k) + ((skipPixels + 31L) / 8) + 1;
        if (needed > bits.Length)
        {
            SetError(GlEnum.INVALID_VALUE);
            return;
        }
        uint[] pattern = new uint[32];
        for (int j = 0; j < 32; j++)
        {
            for (int i = 0; i < 32; i++)
            {
                int bit = i + skipPixels;
                long index = ((j + (long)skipRows) * k) + (bit / 8);
                int h = lsbFirst ? bit % 8 : 7 - (bit % 8);
                if (((bits[(int)index] >> h) & 1) != 0)
                {
                    pattern[j] |= 1u << i;
                }
            }
        }
        State.PolygonStipple = pattern;
    }

    /// <summary>GetPolygonStipple 的回复数据:32 行、每行 4 字节,按请求的 lsbfirst 排位(GLX 编码附录 A 的固定布局)。</summary>
    public byte[] PolygonStippleBytes(bool lsbFirst)
    {
        byte[] bytes = new byte[128];
        for (int j = 0; j < 32; j++)
        {
            for (int i = 0; i < 32; i++)
            {
                if (((State.PolygonStipple[j] >> i) & 1) != 0)
                {
                    bytes[(j * 4) + (i / 8)] |= (byte)(1 << (lsbFirst ? i % 8 : 7 - (i % 8)));
                }
            }
        }
        return bytes;
    }

    /// <summary>
    /// Bitmap(§3.7):置位的像素以光栅颜色生成片元,之后光栅位置按 (xmove, ymove) 前移。
    /// 数据得装得下整张位图(否则 INVALID_VALUE、命令作废);只走落在裁剪范围里的那部分。
    /// </summary>
    private void Bitmap(ref GlReader r)
    {
        r.Skip(1);
        bool lsbFirst = r.U8() != 0;
        r.Skip(2);
        int rowLength = r.I32(), skipRows = r.I32(), skipPixels = r.I32(), alignment = r.I32();
        int width = r.I32(), height = r.I32();
        float xorig = r.F32(), yorig = r.F32(), xmove = r.F32(), ymove = r.F32();
        if (width < 0 || height < 0 || rowLength < 0 || skipRows < 0 || skipPixels < 0)
        {
            SetError(GlEnum.INVALID_VALUE);
            return;
        }
        if (!State.RasterValid)
        {
            return;
        }
        ReadOnlySpan<byte> bits = r.Rest();
        if (width > 0 && height > 0)
        {
            alignment = alignment is 1 or 2 or 4 or 8 ? alignment : 4;
            long groups = rowLength > 0 ? rowLength : width;
            long k = alignment * ((groups + (8L * alignment) - 1) / (8L * alignment));
            long needed = ((skipRows + (long)height - 1) * k) + ((skipPixels + (long)width - 1) / 8) + 1;
            if (needed > bits.Length)
            {
                SetError(GlEnum.INVALID_VALUE);
                return;
            }
            if (PrepareRaster())
            {
                // 位图左下角落在哪(先在浮点里夹一下,免得离谱的光栅位置转整数时溢出),只走它与裁剪范围相交的那一块。
                long x0 = (long)Math.Clamp(MathF.Floor(State.RasterPos.X - xorig), -(float)int.MaxValue, int.MaxValue);
                long y0 = (long)Math.Clamp(MathF.Floor(State.RasterPos.Y - yorig), -(float)int.MaxValue, int.MaxValue);
                int iFrom = (int)Math.Clamp(_clipX0 - x0, 0, width), iTo = (int)Math.Clamp(_clipX1 - x0, 0, width);
                int jFrom = (int)Math.Clamp(_clipY0 - y0, 0, height), jTo = (int)Math.Clamp(_clipY1 - y0, 0, height);
                for (int j = jFrom; j < jTo; j++)
                {
                    WorkBudget.Charge(1L + iTo - iFrom);
                    for (int i = iFrom; i < iTo; i++)
                    {
                        int bit = i + skipPixels;
                        long index = ((j + (long)skipRows) * k) + (bit / 8);
                        int h = lsbFirst ? bit % 8 : 7 - (bit % 8);
                        if (((bits[(int)index] >> h) & 1) != 0)
                        {
                            Fragment((int)(x0 + i), (int)(y0 + j), State.RasterPos.Z, State.RasterColor, Vector3.Zero,
                                State.RasterTexCoord, State.RasterDistance);
                        }
                    }
                }
            }
        }
        State.RasterPos += new Vector4(xmove, ymove, 0, 0);
    }

    /// <summary>
    /// CopyPixels(§4.3.3):只拷读缓冲里真有的、放大后落进裁剪范围的那部分(读缓冲之外的源像素按规范未定义,不画);
    /// 先把这部分源像素拷一份(源与目标可以在同一块缓冲里重叠),再一行一行画。
    /// </summary>
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
        if (Read is not { } s || ReadColorBuffer() is not { } buffer || VisiblePixels(width, height) is not var (i0, i1, j0, j1))
        {
            return;
        }
        // 读缓冲里真有的那部分:源像素 i 落在缓冲的第 x + i 列。全按 long 算再比 —— x 为 int.MinValue 时 −x 转回 int 会回绕成负数,
        // 原先拿它当下标抛 ArgumentOutOfRange(BadImplementation)。比完之后夹在 [i0, i1) 里,转回 int 是安全的。
        long li0 = Math.Max(i0, -(long)x), li1 = Math.Min(i1, s.Width - (long)x);
        long lj0 = Math.Max(j0, -(long)y), lj1 = Math.Min(j1, s.Height - (long)y);
        if (li0 >= li1 || lj0 >= lj1)
        {
            return;
        }
        (i0, i1, j0, j1) = ((int)li0, (int)li1, (int)lj0, (int)lj1);
        int w = i1 - i0, h = j1 - j0;
        WorkBudget.Charge(2L * w * h);
        uint[] source = new uint[w * h];
        for (int j = 0; j < h; j++)
        {
            int sy = s.Height - 1 - (y + j0 + j);   // GL 的 y 向上,缓冲的第 0 行在最上面
            Array.Copy(buffer, (sy * s.Width) + x + i0, source, j * w, w);
        }
        var row = new Vector4[w];
        for (int j = 0; j < h; j++)
        {
            for (int i = 0; i < w; i++)
            {
                row[i] = Unpack(source[(j * w) + i], s.HasAlpha);
            }
            DrawRow(j0 + j, i0, row);
        }
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

    /// <summary>ReadPixels 打包出来有多少字节(与 <see cref="ReadPixels" /> 的布局一致);格式或类型不认识时为 0。</summary>
    public static long PackedSize(int width, int height, uint format, uint type)
    {
        int elements = FormatElements(format);
        (int nbytes, bool packed) = TypeSize(type);
        if (elements == 0 || nbytes == 0 || width <= 0 || height <= 0)
        {
            return 0;
        }
        long rowBytes = (long)nbytes * (packed ? 1 : elements) * width;
        long k = nbytes >= 4 ? rowBytes : (rowBytes + 3) & ~3L;
        return k * height;
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
            case GlEnum.ABGR_EXT:
                (o[0], o[1], o[2], o[3]) = (c.W, c.Z, c.Y, c.X);
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

    /// <summary>TEXTURE_1D / TEXTURE_2D 当前绑定的纹理第 <paramref name="level" /> 级的尺寸;目标或级别不对、这一级没定义时为 null。</summary>
    public (int Width, int Height)? TexLevelSize(uint target, int level) =>
        target is GlEnum.TEXTURE_1D or GlEnum.TEXTURE_2D && level is >= 0 and < GlTexture.MaxLevels && BoundTexture(target)?.Levels[level] is { } image
            ? (image.Width, image.Height)
            : null;

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

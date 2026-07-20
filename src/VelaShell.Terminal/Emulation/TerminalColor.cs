namespace VelaShell.Terminal.Emulation;

/// <summary>
/// <see cref="TerminalColor" /> 解析为屏幕上实际颜色的方式。
/// </summary>
public enum TerminalColorKind : byte
{
    /// <summary>使用终端配置好的默认前景色/背景色。</summary>
    Default = 0,

    /// <summary>256 色调色板索引(0-15 = ANSI,16-231 = 立方图,232-255 = 灰度)。</summary>
    Indexed = 1,

    /// <summary>直接 24 位真彩色。</summary>
    Rgb = 2
}

/// <summary>
/// 与任何具体调色板都无关的颜色。渲染层会针对当前生效的 <see cref="TerminalPalette" />
/// 来解析 <see cref="TerminalColorKind.Default" /> 与 <see cref="TerminalColorKind.Indexed" />。
/// </summary>
public readonly struct TerminalColor : IEquatable<TerminalColor>
{
    /// <summary>该颜色解析为屏幕上实际颜色的方式。</summary>
    public TerminalColorKind Kind { get; }

    /// <summary>当 <see cref="Kind" /> 为 Indexed 时的调色板索引。</summary>
    public byte Index { get; }

    /// <summary>当 <see cref="Kind" /> 为 Rgb 时的红色通道。</summary>
    public byte R { get; }

    /// <summary>当 <see cref="Kind" /> 为 Rgb 时的绿色通道。</summary>
    public byte G { get; }

    /// <summary>当 <see cref="Kind" /> 为 Rgb 时的蓝色通道。</summary>
    public byte B { get; }

    private TerminalColor(TerminalColorKind kind, byte index, byte r, byte g, byte b)
    {
        Kind = kind;
        Index = index;
        R = r;
        G = g;
        B = b;
    }

    /// <summary>终端默认前景/背景色哨兵值。</summary>
    public static TerminalColor Default => new(TerminalColorKind.Default, 0, 0, 0, 0);

    /// <summary>由 256 色调色板索引创建索引色(钳制到 0-255)。</summary>
    public static TerminalColor FromIndex(int index) => new(TerminalColorKind.Indexed, (byte)Math.Clamp(index, 0, 255), 0, 0, 0);

    /// <summary>由给定的红、绿、蓝通道创建 24 位真彩色。</summary>
    public static TerminalColor FromRgb(byte r, byte g, byte b) => new(TerminalColorKind.Rgb, 0, r, g, b);

    /// <summary>该颜色是否为终端默认哨兵值。</summary>
    public bool IsDefault => Kind == TerminalColorKind.Default;

    /// <summary>判断该颜色是否与另一个颜色相等。</summary>
    public bool Equals(TerminalColor other) => Kind == other.Kind && Index == other.Index && R == other.R && G == other.G && B == other.B;

    /// <summary>判断该颜色是否与给定对象相等。</summary>
    public override bool Equals(object? obj) => obj is TerminalColor other && Equals(other);

    /// <summary>返回合并所有颜色分量的哈希码。</summary>
    public override int GetHashCode() => HashCode.Combine((byte)Kind, Index, R, G, B);

    /// <summary>判断两个颜色是否相等。</summary>
    public static bool operator ==(TerminalColor left, TerminalColor right) => left.Equals(right);

    /// <summary>判断两个颜色是否不相等。</summary>
    public static bool operator !=(TerminalColor left, TerminalColor right) => !left.Equals(right);
}

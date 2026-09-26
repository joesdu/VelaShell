// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs

namespace VelaShell.XServer;

/// <summary>
/// 一份要一次性交给服务端的键位表(<see cref="X11Server.SetKeymap" />):布局名、若干键码的键值,以及右 Alt 是不是 AltGr。
/// 没列出的键码保持服务端现有的键值(起步是 US 布局)。
/// </summary>
/// <remarks>
/// 每个键码 <see cref="KeysymsPerKeycode" /> 列键值,列序同核心协议的 ChangeKeyboardMapping:第 1 列无修饰、第 2 列 Shift;
/// 有 AltGr 层时按 XKB 规范 §17 的核心列序排成 6 列(组 1 第 1、2 级,组 2 第 1、2 级,组 1 第 3、4 级)。
/// 交给 <see cref="X11Server.SetKeymap" /> 时服务端当场拷一份,之后再改这个对象不影响服务端。
/// </remarks>
public sealed class XKeymap
{
    /// <summary>最小的键码(协议规定 8)。</summary>
    public const byte MinKeycode = 8;

    private readonly SortedDictionary<byte, uint[]> _keys = [];

    /// <summary>新建一份空的键位表。</summary>
    /// <param name="layout">XKB 布局名(如 <c>us</c>、<c>de</c>),发布在根窗口的 <c>_XKB_RULES_NAMES</c> 里给 setxkbmap 之类的工具看。</param>
    /// <param name="keysymsPerKeycode">每个键码几列键值(≥ 1)。</param>
    public XKeymap(string layout, int keysymsPerKeycode = 2)
    {
        ArgumentException.ThrowIfNullOrEmpty(layout);
        ArgumentOutOfRangeException.ThrowIfLessThan(keysymsPerKeycode, 1);
        Layout = layout;
        KeysymsPerKeycode = keysymsPerKeycode;
    }

    /// <summary>XKB 布局名。</summary>
    public string Layout { get; }

    /// <summary>每个键码几列键值。</summary>
    public int KeysymsPerKeycode { get; }

    /// <summary>
    /// 右 Alt(<see cref="XKeycodes.AltRight" />;macOS 上是右 Option)当 AltGr 用:键值 ISO_Level3_Shift、归 Mod5
    /// (服务端的四级键类型按 Mod5 选第三、四级)。false 时它是普通的 Alt_R、归 Mod1。
    /// </summary>
    public bool AltGr { get; init; }

    /// <summary>已经设了键值的键码,按键码排序。</summary>
    internal IReadOnlyDictionary<byte, uint[]> Keys => _keys;

    /// <summary>设一个键码的键值;给出的列少于 <see cref="KeysymsPerKeycode" /> 时其余列为 NoSymbol(0)。</summary>
    /// <returns>这份键位表本身,可以接着设下一个。</returns>
    public XKeymap Map(byte keycode, params ReadOnlySpan<uint> keysyms)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(keycode, MinKeycode);
        if (keysyms.Length > KeysymsPerKeycode)
        {
            throw new ArgumentException($"每个键码最多 {KeysymsPerKeycode} 列键值。", nameof(keysyms));
        }
        uint[] row = new uint[KeysymsPerKeycode];
        keysyms.CopyTo(row);
        _keys[keycode] = row;
        return this;
    }

    /// <summary>从 <paramref name="firstKeycode" /> 起连续若干键码,每个 <see cref="KeysymsPerKeycode" /> 列键值。</summary>
    /// <returns>这份键位表本身,可以接着设下一个。</returns>
    public XKeymap MapRange(byte firstKeycode, ReadOnlySpan<uint> keysyms)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(firstKeycode, MinKeycode);
        int per = KeysymsPerKeycode;
        if (keysyms.Length % per != 0 || firstKeycode + (keysyms.Length / per) - 1 > byte.MaxValue)
        {
            throw new ArgumentException("键值个数必须是每键码列数的整数倍,且不超过键码 255。", nameof(keysyms));
        }
        for (int i = 0; i < keysyms.Length / per; i++)
        {
            Map((byte)(firstKeycode + i), keysyms.Slice(i * per, per));
        }
        return this;
    }
}

// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   X Window System Protocol, X Version 11 —— 「ChangeKeyboardMapping」与「MappingNotify」(一段键码的键值变了,客户端重新取);
//   附录 A「KEYSYM Encoding」:Latin-1 键值(#x0020–#x007E、#x00A0–#x00FF)就是那个码位,其余 Unicode 字符的键值是码位加 #x01000000
//   XKB Protocol —— MapNotify(键值变了,XKB 客户端重新取映射)

using System.Text;
using VelaShell.XServer.Input;

namespace VelaShell.XServer;

/// <summary>
/// 宿主的输入法组好的文字交给 X(<see cref="InjectText" />):X 程序只认键码,没有「直接给一串字」的路子。
/// 每个字找一个空着的键码,把它的键值改成这个字(Unicode 键值),再按下、松开 —— 远端不用装 fcitx / ibus,任何工具包都能收到。
/// </summary>
/// <remarks>
/// <para>
/// 改过的键码<b>不改回去</b>:客户端收到 MappingNotify 之后才去重新取键位表(Xlib 在下一次查键值时取、XKB 客户端收到 MapNotify 时取),
/// 要是按完就改回去,它取到的已经是旧的了,按键会被当成别的字。所以一个字一旦有了键码就一直留着,下次再输入同一个字不用再改、不发通知;
/// 空着的键码用完了才挪用最久没用过的那个 —— 刚用过(<see cref="TextKeyReuseMilliseconds" /> 之内)的不挪,剩下的字等一会儿再输入。
/// </para>
/// <para>
/// 键位表里本来就有、不按修饰键就能打出来的字(英文字母、数字……)直接按那个键,不改键位表。
/// </para>
/// </remarks>
public sealed partial class X11Server
{
    /// <summary>刚用来输入过字的键码这么久之内不挪用(客户端可能还没处理完它的 MappingNotify 与按键)。</summary>
    internal const int TextKeyReuseMilliseconds = 200;

    /// <summary>字的键值 → 为它改过键值的键码。</summary>
    private readonly Dictionary<uint, byte> _textKeysyms = [];

    /// <summary>为输入字改过键值的键码 → (键值, 最后一次用它的时刻)。</summary>
    private readonly Dictionary<byte, (uint Keysym, long UsedAt)> _textKeycodes = [];

    /// <summary>测试用的时钟(毫秒);默认 <see cref="Environment.TickCount64" />。</summary>
    internal Func<long> TextInputClock { get; set; } = () => Environment.TickCount64;

    /// <summary>
    /// 输入一串字(从 <paramref name="start" /> 起)。挪用不了键码时(都刚用过)剩下的等 <see cref="TextKeyReuseMilliseconds" /> 再接着输入;
    /// 期间服务端停了就不再输入。
    /// </summary>
    private void ApplyInjectText(string text, int start)
    {
        int i = start;
        while (i < text.Length)
        {
            if (Rune.DecodeFromUtf16(text.AsSpan(i), out Rune rune, out int consumed) != System.Buffers.OperationStatus.Done)
            {
                i += Math.Max(1, consumed);   // 落单的代理项:跳过
                continue;
            }
            uint keysym = TextKeysym(rune.Value);
            if (keysym == 0)
            {
                i += consumed;
                continue;
            }
            if (TextKeycode(keysym) is not { } keycode)
            {
                int resume = i;
                _ = Task.Delay(TextKeyReuseMilliseconds, _lifetime.Token).ContinueWith(
                    t =>
                    {
                        if (!t.IsCanceled)
                        {
                            Post(null, () => ApplyInjectText(text, resume));
                        }
                    },
                    CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                return;
            }
            ApplyKey(keycode, pressed: true, repeat: false);
            ApplyKey(keycode, pressed: false, repeat: false);
            i += consumed;
        }
    }

    /// <summary>
    /// 字的键值(附录 A):Latin-1 的可见字符就是码位,其余 Unicode 字符是码位加 0x01000000;换行 / 回车按 Return、制表按 Tab;
    /// 别的控制字符(C0、DEL、C1)不输入,返回 0。
    /// </summary>
    internal static uint TextKeysym(int codePoint) => codePoint switch
    {
        '\n' or '\r' => 0xff0d,                       // Return
        '\t' => 0xff09,                               // Tab
        < 0x20 or (>= 0x7f and < 0xa0) => 0,
        < 0x100 => (uint)codePoint,
        _ => 0x01000000u + (uint)codePoint,
    };

    /// <summary>
    /// 能打出 <paramref name="keysym" /> 的键码:键位表里不按修饰键就能打出来的键;之前为它改过的键码;一个空着的键码;
    /// 最久没用过的、为别的字改过的键码。最后一种也刚用过时返回 null(等一会儿再来)。
    /// </summary>
    private byte? TextKeycode(uint keysym)
    {
        long now = TextInputClock();
        if (_textKeysyms.TryGetValue(keysym, out byte cached))
        {
            if (_keymap.Keysym(cached, 0) == keysym && (_keymap.Keysym(cached, 1) is 0 || _keymap.Keysym(cached, 1) == keysym))
            {
                _textKeycodes[cached] = (keysym, now);
                return cached;
            }
            ForgetTextKeycode(cached);   // 客户端或宿主改过这个键码了
        }
        // 按住 Shift / 开着 CapsLock 时第一列不一定是打出来的那个,只用为字专门改过的键码(两列相同)。
        if ((_modifiers & 0x3) == 0 && ExistingKeycode(keysym) is { } existing)
        {
            return existing;
        }
        byte? target = FreeKeycode();
        if (target is null)
        {
            (byte Keycode, long UsedAt)? oldest = null;
            foreach ((byte keycode, (uint _, long usedAt)) in _textKeycodes)
            {
                if (oldest is null || usedAt < oldest.Value.UsedAt)
                {
                    oldest = (keycode, usedAt);
                }
            }
            if (oldest is not { } victim || now - victim.UsedAt < TextKeyReuseMilliseconds || IsKeyDown(victim.Keycode))
            {
                return null;
            }
            ForgetTextKeycode(victim.Keycode);
            target = victim.Keycode;
        }
        byte chosen = target.Value;
        _keymap.Change(chosen, 2, [keysym, keysym]);
        _textKeysyms[keysym] = chosen;
        _textKeycodes[chosen] = (keysym, now);
        NotifyKeyboardMappingChanged(chosen, 1);
        NotifyXkbMapChanged();
        return chosen;
    }

    /// <summary>键位表里第一列就是 <paramref name="keysym" /> 的键(不按修饰键就打得出来);修饰键与按着的键不算。</summary>
    private byte? ExistingKeycode(uint keysym)
    {
        for (int keycode = Keymap.MinKeycode; keycode <= Keymap.MaxKeycode; keycode++)
        {
            byte k = (byte)keycode;
            if (_keymap.Keysym(k, 0) == keysym && _keymap.ModifierBitOf(k) == 0 && !IsKeyDown(k))
            {
                return k;
            }
        }
        return null;
    }

    /// <summary>
    /// 空着的键码:每一列都是 NoSymbol、不是修饰键、宿主的键位表里没有它、没按着。从最高的键码往下找 ——
    /// 低处是真实键盘的键,宿主换布局时可能用上。
    /// </summary>
    private byte? FreeKeycode()
    {
        for (int keycode = Keymap.MaxKeycode; keycode >= Keymap.MinKeycode; keycode--)
        {
            byte k = (byte)keycode;
            if (_textKeycodes.ContainsKey(k) || _hostKeys.ContainsKey(k) || _keymap.ModifierBitOf(k) != 0 || IsKeyDown(k))
            {
                continue;
            }
            bool empty = true;
            for (int col = 0; col < _keymap.KeysymsPerKeycode && empty; col++)
            {
                empty = _keymap.Keysym(k, col) == 0;
            }
            if (empty)
            {
                return k;
            }
        }
        return null;
    }

    private void ForgetTextKeycode(byte keycode)
    {
        if (_textKeycodes.Remove(keycode, out (uint Keysym, long UsedAt) entry) && _textKeysyms.TryGetValue(entry.Keysym, out byte k) && k == keycode)
        {
            _textKeysyms.Remove(entry.Keysym);
        }
    }
}

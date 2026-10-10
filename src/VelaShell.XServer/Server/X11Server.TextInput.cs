// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 规范依据(AGENTS.md §2 纪律 1):
//   X Window System Protocol, X Version 11 —— 「ChangeKeyboardMapping」与「MappingNotify」(一段键码的键值变了,客户端重新取);
//   第 5 节「Keyboards」(一组两个键值时 Shift / Lock 怎样选键值:Lock 当 CapsLock 时小写字母转大写);
//   附录 A「KEYSYM Encoding」:Latin-1 键值(#x0020–#x007E、#x00A0–#x00FF)就是那个码位,其余 Unicode 字符的键值是码位加 #x01000000
//   XKB Protocol —— MapNotify(键值变了,XKB 客户端重新取映射);§7「Key Types」(类型的修饰位就是被这个键消耗的修饰位;
//   「Transforming the KeySym Associated with a Key Event」:Lock 没被消耗时结果转大写)
//   Security Extension Specification 7.1 —— 第三章「Keyboard Security」(非受信客户端读不到发给受信客户端的键盘输入)

using System.Text;
using VelaShell.XServer.Input;
using VelaShell.XServer.Server;

namespace VelaShell.XServer;

/// <summary>
/// 宿主的输入法组好的文字交给 X(<see cref="InjectText" />):X 程序只认键码,没有「直接给一串字」的路子。
/// 每个字找一个空着的键码,把它的键值改成这个字(Unicode 键值),再按下、松开 —— 远端不用装 fcitx / ibus,任何工具包都能收到。
/// </summary>
/// <remarks>
/// <para>
/// <b>按顺序</b>:宿主注入的字与按键排成一队(<see cref="_pendingInput" />)。前面的字还没输入完(等键码空出来)时,后来的字与宿主的按键
/// 排在后面 —— 原先等的那一会儿里用户按的回车、输入法又上屏的字会插到前一段字的中间。
/// </para>
/// <para>
/// <b>一批一次通知</b>:一段字先把要用的键码都找好(改键位表),再发一次 MappingNotify 与一次只涉及这几个键的 XKB MapNotify,然后依次按下、松开。
/// 原先每个新字一轮通知,客户端每个字重取一遍整张键位表。
/// </para>
/// <para>
/// <b>改过的键码不改回去</b>:客户端收到 MappingNotify 之后才去重新取键位表(Xlib 在下一次查键值时取、XKB 客户端收到 MapNotify 时取),
/// 取到的是请求到达那一刻的键位表。要是按完就改回去,它取到的已经是别的了,按键会被当成别的字。所以一个字一旦有了键码就一直留着,
/// 下次再输入同一个字不用再改、不发通知;空着的键码用完了才挪用最久没用过的那个,而且它最后一次用过之后要空闲
/// <see cref="TextKeyReuseMilliseconds" />(远远长于经 SSH 的一个来回加重取键位表的时间)—— 原先只等 200 毫秒,慢链路上一段超过空键码数的字,
/// 客户端为第一个字重取键位表时那个键码已经改成了后面的字。键盘被同步抓取冻结着、还有按键排着时也不挪用。
/// </para>
/// <para>
/// <b>大小写</b>:借来的键两级都是这个字、XKB 类型是 ALPHABETIC —— Lock 算作被这个键消耗,CapsLock 开着时 XKB 客户端(Xlib、xkbcommon)
/// 不再把「é」变成「É」。只认核心协议的老客户端仍按第 5 节把小写字母转大写。
/// </para>
/// <para>
/// <b>非受信客户端</b>(SECURITY 第三章「Keyboard Security」):借来的键码的键值本身就是用户刚输入的字。键盘事件不送到非受信客户端时,
/// 改键位表的通知不发给它们,它们读键位表时这些键码是 NoSymbol(<see cref="KeysymFor" />);只有输入给非受信客户端的字(那时键盘事件本来就给它)
/// 才让它们看见。原先非受信客户端收到通知、重取键位表,就能逐字读出用户在受信程序里输入的字。
/// </para>
/// <para>
/// 键位表里本来就有、不按修饰键就能打出来的字(英文字母、数字……)直接按那个键,不改键位表;按着修饰键(NumLock 除外)时一律借键码。
/// </para>
/// </remarks>
public sealed partial class X11Server
{
    /// <summary>为字借来的键码最后一次用过之后至少空闲这么久才挪给别的字(客户端可能还没为它重取完键位表)。</summary>
    internal const int TextKeyReuseMilliseconds = 3000;

    /// <summary>挪用不了键码时,隔这么久再看一次。</summary>
    internal const int TextRetryMilliseconds = 50;

    /// <summary>NumLock 的修饰位(Mod2;见 XKB 虚拟修饰 NumLock 的绑定)。按着它不影响借键码以外的字怎么打。</summary>
    private const ushort NumLockMask = 0x10;

    /// <summary>一个为字借来的键码。</summary>
    private sealed class TextKey(uint keysym, long usedAt, bool revealed)
    {
        /// <summary>改成的键值。</summary>
        public uint Keysym { get; } = keysym;

        /// <summary>最后一次用它输入的时刻(<see cref="TextInputClock" />)。</summary>
        public long UsedAt { get; set; } = usedAt;

        /// <summary>非受信客户端看得见它:用它输入时键盘事件送到非受信客户端。没看过的,非受信客户端读键位表时这个键码是 NoSymbol。</summary>
        public bool Revealed { get; set; } = revealed;
    }

    /// <summary>宿主注入、还没处理的一项:一段字(从 <see cref="Start" /> 起),或者排在字后面的一个按键。</summary>
    private sealed class PendingInput
    {
        public string? Text { get; init; }

        public int Start { get; set; }

        public byte Keycode { get; init; }

        public bool Pressed { get; init; }

        public bool Repeat { get; init; }
    }

    /// <summary>字的键值 → 为它改过键值的键码。</summary>
    private readonly Dictionary<uint, byte> _textKeysyms = [];

    /// <summary>为输入字改过键值的键码。</summary>
    private readonly Dictionary<byte, TextKey> _textKeycodes = [];

    /// <summary>宿主注入、还没输入完的字,与排在它们后面的宿主按键(按注入的顺序)。只在执行线程上碰。</summary>
    private readonly Queue<PendingInput> _pendingInput = new();

    /// <summary>已经排了一次「过一会儿再接着输入」。</summary>
    private bool _textRetryScheduled;

    /// <summary>测试用的时钟(毫秒);默认 <see cref="Environment.TickCount64" />。</summary>
    internal Func<long> TextInputClock { get; set; } = () => Environment.TickCount64;

    /// <summary>宿主输入一段字:排进队里;前面没有在等的,马上开始输入。</summary>
    private void ApplyInjectText(string text)
    {
        _pendingInput.Enqueue(new PendingInput { Text = text });
        if (_pendingInput.Count == 1)
        {
            DrainPendingInput();
        }
    }

    /// <summary>宿主的按键(<see cref="InjectKey(byte, bool, bool)" />):前面还有没输入完的字时排在它们后面。</summary>
    private void ApplyInjectedKey(byte keycode, bool pressed, bool repeat)
    {
        if (_pendingInput.Count == 0)
        {
            ApplyKey(keycode, pressed, repeat);
            return;
        }
        _pendingInput.Enqueue(new PendingInput { Keycode = keycode, Pressed = pressed, Repeat = repeat });
    }

    /// <summary>按顺序处理排着的字与按键;某段字要等键码空出来时停下,过 <see cref="TextRetryMilliseconds" /> 再来。</summary>
    private void DrainPendingInput()
    {
        while (_pendingInput.TryPeek(out PendingInput? item))
        {
            if (item.Text is { } text)
            {
                if (item.Start == 0 && CommitThroughXim(text))
                {
                    // 焦点所在的程序经 XIM 连着(X11Server.Xim.cs):组好的字整段交过去,不动键位表。轮到它时才决定 ——
                    // 排在前面、还在等键码的字先输完,顺序不乱。
                    _pendingInput.Dequeue();
                    continue;
                }
                item.Start = TypeText(text, item.Start);
                if (item.Start < text.Length)
                {
                    ScheduleTextRetry();
                    return;
                }
            }
            else
            {
                ApplyKey(item.Keycode, item.Pressed, item.Repeat);
            }
            _pendingInput.Dequeue();
        }
    }

    /// <summary>过一会儿再接着处理队列;服务端停了就不再来。</summary>
    private void ScheduleTextRetry()
    {
        if (_textRetryScheduled)
        {
            return;
        }
        _textRetryScheduled = true;
        _ = Task.Delay(TextRetryMilliseconds, _lifetime.Token).ContinueWith(
            t =>
            {
                if (!t.IsCanceled)
                {
                    Post(null, () =>
                    {
                        _textRetryScheduled = false;
                        DrainPendingInput();
                    });
                }
            },
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>
    /// 从 <paramref name="start" /> 起输入一批字,返回停在哪里(等于长度表示输完)。先给这批字都找好键码(改键位表),通知一次,再依次按下、松开;
    /// 挪用不了键码时这批到此为止。一个可借的键码都没有(客户端把每个键码都占了)时剩下的字丢掉、记一行日志。
    /// </summary>
    private int TypeText(string text, int start)
    {
        long now = TextInputClock();
        bool untrustedReceives = KeyboardReachesUntrusted();
        List<byte> presses = [];
        HashSet<byte> inBatch = [];
        (int First, int Last) changed = (int.MaxValue, -1), revealed = (int.MaxValue, -1);
        int i = start;
        while (i < text.Length)
        {
            if (Rune.DecodeFromUtf16(text.AsSpan(i), out Rune rune, out int consumed) != System.Buffers.OperationStatus.Done)
            {
                i += Math.Max(1, consumed);   // 落单的代理项:跳过
                continue;
            }
            if (rune.Value == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
            {
                i++;   // CR LF 是一个换行(原先按了两次 Return)
                continue;
            }
            uint keysym = TextKeysym(rune.Value);
            if (keysym == 0)
            {
                i += consumed;
                continue;
            }
            TextKeyChoice choice = ChooseTextKeycode(keysym, inBatch, now, untrustedReceives);
            if (choice.Keycode is not { } keycode)
            {
                if (choice.Never)
                {
                    Log($"text input: no keycode can be borrowed (every keycode is bound); {text.Length - i} UTF-16 units dropped");
                    i = text.Length;
                }
                break;
            }
            if (choice.Changed)
            {
                changed = (Math.Min(changed.First, keycode), Math.Max(changed.Last, keycode));
            }
            else if (choice.Revealed)
            {
                revealed = (Math.Min(revealed.First, keycode), Math.Max(revealed.Last, keycode));
            }
            presses.Add(keycode);
            inBatch.Add(keycode);
            i += consumed;
        }
        if (changed.Last >= 0)
        {
            // 受信客户端都通知;非受信的只在键盘事件本来就送到非受信客户端时通知(SECURITY「Keyboard Security」)。
            NotifyTextKeysChanged(changed, client => !client.Untrusted || untrustedReceives);
        }
        if (revealed.Last >= 0)
        {
            // 以前输入给受信程序时借的键码,这次输入给非受信程序:受信客户端早就知道,只告诉非受信的。
            NotifyTextKeysChanged(revealed, static client => client.Untrusted);
        }
        foreach (byte keycode in presses)
        {
            ApplyKey(keycode, pressed: true, repeat: false);
            ApplyKey(keycode, pressed: false, repeat: false);
        }
        return i;
    }

    /// <summary>一段键码(为字借的)的键值变了:核心 MappingNotify 与只涉及这几个键的键值的 XKB MapNotify,发给 <paramref name="to" /> 认可的客户端。</summary>
    private void NotifyTextKeysChanged((int First, int Last) range, Func<XClient, bool> to)
    {
        NotifyKeyboardMappingChanged((byte)range.First, range.Last - range.First + 1, to);
        NotifyXkbMapChanged(to, ((byte)range.First, (byte)(range.Last - range.First + 1)));
    }

    /// <summary><see cref="ChooseTextKeycode" /> 的结果。</summary>
    /// <param name="Keycode">用哪个键码;null = 这会儿没有(<paramref name="Never" /> 为假时等一会儿再来)。</param>
    /// <param name="Changed">为这个字改了它的键值(要通知)。</param>
    /// <param name="Revealed">以前借的键码,这次头一回让非受信客户端看见(要通知非受信客户端)。</param>
    /// <param name="Never">一个可借的键码都没有,等也没用。</param>
    private readonly record struct TextKeyChoice(byte? Keycode, bool Changed = false, bool Revealed = false, bool Never = false);

    /// <summary>
    /// 能打出 <paramref name="keysym" /> 的键码:之前为它借过的键码;键位表里不按修饰键就能打出来的键(此刻没按着修饰键时);一个空着的键码;
    /// 最久没用过、已经空闲够久的借来的键码(这一批里用过的不挪)。
    /// </summary>
    private TextKeyChoice ChooseTextKeycode(uint keysym, HashSet<byte> inBatch, long now, bool untrustedReceives)
    {
        if (_textKeysyms.TryGetValue(keysym, out byte cached))
        {
            if (TextKeyAt(cached) is { } entry)
            {
                entry.UsedAt = now;
                bool reveal = untrustedReceives && !entry.Revealed;
                entry.Revealed |= reveal;
                return new(cached, Revealed: reveal);
            }
            ForgetTextKeycode(cached);   // 客户端或宿主改过这个键码了
        }
        // 按住 Shift / Ctrl / Alt…… 或开着 CapsLock 时,键位表里第一列不一定是打出来的那个、或者成了快捷键:只用借来的键码。
        if ((_modifiers & ~NumLockMask) == 0 && ExistingKeycode(keysym) is { } existing)
        {
            return new(existing);
        }
        byte? target = FreeKeycode();
        if (target is null)
        {
            bool anyCandidate = false;
            bool inputQueued = IsFrozen(pointer: false) || _frozenInput.Exists(e => !e.Pointer);
            (byte Keycode, long UsedAt)? oldest = null;
            List<byte>? stale = null;
            foreach ((byte keycode, TextKey entry) in _textKeycodes)
            {
                if (!MatchesTextKey(keycode, entry))
                {
                    (stale ??= []).Add(keycode);   // 客户端改过它(xmodmap、setxkbmap):不再是借来的,不能挪
                    continue;
                }
                if (IsKeyDown(keycode))
                {
                    continue;
                }
                anyCandidate = true;
                // 键盘冻结着、还有按键排着时不挪:排着的按下会按改过之后的键值解释。
                if (inputQueued || inBatch.Contains(keycode) || now - entry.UsedAt < TextKeyReuseMilliseconds)
                {
                    continue;
                }
                if (oldest is null || entry.UsedAt < oldest.Value.UsedAt)
                {
                    oldest = (keycode, entry.UsedAt);
                }
            }
            foreach (byte keycode in stale ?? [])
            {
                ForgetTextKeycode(keycode);
            }
            if (oldest is not { } victim)
            {
                return new(null, Never: !anyCandidate);
            }
            ForgetTextKeycode(victim.Keycode);
            target = victim.Keycode;
        }
        byte chosen = target.Value;
        _keymap.Change(chosen, 2, [keysym, keysym]);
        _textKeysyms[keysym] = chosen;
        _textKeycodes[chosen] = new TextKey(keysym, now, revealed: untrustedReceives);
        return new(chosen, Changed: true);
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

    /// <summary>这个键码此刻是为字借来的(键位表里仍是借时改成的样子)就给出它的记录。</summary>
    private TextKey? TextKeyAt(byte keycode) =>
        _textKeycodes.TryGetValue(keycode, out TextKey? entry) && MatchesTextKey(keycode, entry) ? entry : null;

    private bool MatchesTextKey(byte keycode, TextKey entry) =>
        _keymap.Keysym(keycode, 0) == entry.Keysym && (_keymap.Keysym(keycode, 1) is 0 || _keymap.Keysym(keycode, 1) == entry.Keysym);

    /// <summary>
    /// 客户端读键位表(GetKeyboardMapping、XI 的 GetDeviceKeyMapping)时看到的键值:为字借来、还没让非受信客户端看过的键码,
    /// 对非受信客户端是 NoSymbol —— 它的键值就是用户在受信程序里刚输入的字。
    /// </summary>
    private uint KeysymFor(XClient client, byte keycode, int column) =>
        client.Untrusted && TextKeyAt(keycode) is { Revealed: false } ? 0 : _keymap.Keysym(keycode, column);

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
        if (_textKeycodes.Remove(keycode, out TextKey? entry) && _textKeysyms.TryGetValue(entry.Keysym, out byte k) && k == keycode)
        {
            _textKeysyms.Remove(entry.Keysym);
        }
    }
}

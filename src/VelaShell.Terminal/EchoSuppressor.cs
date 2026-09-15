namespace VelaShell.Terminal;

/// <summary>
/// 注入命令的回显抑制器:连接初始化命令是作为击键注入远端 shell 的,PTY 会把它原样回显
/// (可能出现两次:内核规范模式回显 + readline 预输入重绘),需静默执行以避免干扰终端输出。
/// 本类在时间窗内从输出流里剥除这些命令的回显字节;回显可能被网络分块任意切开,
/// 因此做跨块流式匹配:块尾的部分命中(≥<see cref="MinHold" /> 字节,避免把提示符
/// 尾部空格之类的巧合扣住)先扣下,下一块续判。超时或命中数用尽后放行一切。
/// </summary>
/// <remarks>
/// <para>
/// <b>一个实例服务多条注入(<see cref="AddNeedle" />)。</b>握手时是连着发好几条的:
/// 目录上报脚本 → 初始目录 <c>cd</c> → 认证后命令。早先每条各建一个实例,
/// 而它们在**任何数据到达之前**就互相覆盖掉了 —— 只有最后一条的回显被剥,前面几条
/// 明晃晃地留在屏幕上(用户看到的那串 <c>test -n "${BASH_VERSION:-}" &amp;&amp; eval …</c>
/// 就是这么来的)。改成一个实例装多根针之后,先来后到都能剥。
/// </para>
/// <para>
/// <b>注入窗口(<see cref="OpenWindow" />)。</b>剥掉回显只解决了「看见自己注的那行字」,
/// 解决不了「那行字<b>跑出来的东西</b>」—— 受限 shell、ForceCommand 菜单、rc 里千奇百怪的
/// 设置都可能让注入吐出一两行报错,而那是用户最反感的:他没敲过这条命令,却要为它的报错买单。
/// 给了哨兵之后,抑制器在<b>吃掉第一条回显之后</b>转入吞噬阶段,把后续输出全部扣住,
/// 直到看见那条哨兵为止,从它<b>结束处</b>恢复放行 —— 中间那段(报错、噪声)一个字节都不上屏。
/// 思路抄自 electerm(<c>attach-addon-custom.js</c> 的 <c>startOutputSuppression</c>),
/// 但它等的是"任意一条集成序列",我们等的是<b>被注入的那一行自己打印的、带随机 nonce 的哨兵</b>。
/// 这一步之差正是隔离的关键:等"任意序列"会被用户 rc 里本来就有的集成序列提前关掉,
/// 也会在用户紧跟着一条命令时把人家的输出吞进窗口;等自己的哨兵则谁也影响不到谁。
/// </para>
/// <para>
/// <b>吞噬结束不等于收工。</b>窗口闭合后要**退回剥回显**继续干活 —— 后面还排着第二、
/// 第三条注入的回显没剥呢。把吞噬结束直接当成"放行一切",就会把
/// 「先钩子(带窗口)、后用户命令」这条路上的第二行漏到屏幕上。
/// </para>
/// <para>
/// <b>和 electerm 的一处关键分歧:超时之后是「放行」而不是「丢弃」。</b>electerm 超时即丢,
/// 因为它在别的生命周期点武装窗口;我们是连上就注入,而 MOTD / rc 的自定义欢迎语
/// 恰好也落在这段时间里。一旦按「丢弃」处理,注入失败的会话会把**提示符本身**一起吞掉,
/// 用户面对一块空屏,不按回车什么都不会出现 —— 那比看见一行报错糟糕得多。
/// 所以这里的约定是:成功就藏干净,失败最多退回到「和以前一样」,绝不倒扣。
/// </para>
/// <para>
/// 吞噬只装在**显示**路径上,不装在旁路记录(会话日志 / 录制)上。日志要如实记下远端到底
/// 回了什么 —— 注入的回显是我们自己的字节,该剥;而注入引发的报错是排障线索,该留。
/// </para>
/// <para>
/// <b>线程。</b>显示路径在 UI 线程、旁路记录在读线程,各持一份实例;但 <see cref="AddNeedle" />
/// 一律由 UI 线程调用,对旁路那份就是跨线程写。所以实例内部上锁 —— 这条路径只在连接后的
/// 最初几秒活着(每秒至多几百块),锁的代价可以忽略。
/// </para>
/// </remarks>
public sealed class EchoSuppressor
{
    /// <summary>块尾部分命中至少要匹配这么多字节才扣住等下一块,防止普通输出被延迟显示。</summary>
    private const int MinHold = 4;

    /// <summary>
    /// 一根抑制针至少要有这么长,否则构造会抛 —— 太短的针在流里到处都是,扣住它反而会
    /// 把正常输出剪坏。调用方据此判断"这条命令短到不值得剥回显"(比如一条 <c>w</c>),
    /// 而不是等着接异常。
    /// </summary>
    public const int MinNeedleBytes = MinHold;

    /// <summary>
    /// 吞噬窗口的默认长度。它等的是「我们这行跑完之后的下一个提示符」,不是「网络慢」——
    /// 慢 rc(nvm / conda / p10k)发生在回显<b>之前</b>,不占这个窗口。给到 2 秒是为了
    /// 兜住 precmd 链特别长的提示符;再长只会让注入失败时的黑屏更久,没有额外收益。
    /// </summary>
    public static readonly TimeSpan DefaultSwallowWindow = TimeSpan.FromSeconds(2);

    /// <summary>抑制器的阶段。</summary>
    private enum Stage
    {
        /// <summary>剥回显:命中 needle 就剥掉,其余原样放行。</summary>
        Echo,

        /// <summary>吞噬:第一条回显已吃掉,扣住一切直到看见集成序列或窗口到期。</summary>
        Swallow
    }

    /// <summary>一根抑制针:要剥的字节,以及还能剥几次。</summary>
    private sealed class Needle(byte[] bytes, int hitsLeft)
    {
        public byte[] Bytes { get; } = bytes;
        public int HitsLeft { get; set; } = hitsLeft;
    }

    private readonly Lock _sync = new();
    private readonly List<Needle> _needles = [];
    private readonly int _maxHits;
    private readonly DateTime _deadline;

    /// <summary>
    /// 注入窗口的闭合哨兵(整条转义序列);为 null 表示不开窗口,只剥回显。
    /// </summary>
    private readonly byte[]? _gateSentinel;

    /// <summary>吞噬阶段最多扣住多少字节。超过即认定注入没成功,原样放行,避免无上限缓冲。</summary>
    private readonly int _maxSwallowBytes;

    /// <summary>吞噬阶段的窗口长度。</summary>
    private readonly TimeSpan _swallowWindow;

    private Stage _stage = Stage.Echo;

    /// <summary>上一块末尾扣住的字节(某根针的前缀,等下一块续判)。</summary>
    private byte[] _held = [];

    private MemoryStream? _swallowed;
    private DateTime _swallowDeadline;

    /// <summary>
    /// 创建回显抑制器。
    /// </summary>
    /// <param name="needle">要从输出流中剥除的命令回显字节序列,长度须不小于 <see cref="MinHold" />。</param>
    /// <param name="maxHits">每根针最多剥除多少次(用尽即失效)。</param>
    /// <param name="window">抑制生效的时间窗,超时后放行一切。</param>
    /// <remarks>
    /// <b>这个构造只做"剥回显"一件事</b>,给的是<b>短</b>命令 —— 长注入请用
    /// <see cref="OpenWindow" />,理由见那边(逐字节匹配咬不住折行重绘的回显)。
    /// </remarks>
    public EchoSuppressor(byte[] needle, int maxHits, TimeSpan window)
    {
        if (needle.Length < MinHold)
        {
            throw new ArgumentException(@"Needle too short to suppress safely.", nameof(needle));
        }
        _needles.Add(new(needle, maxHits));
        _maxHits = maxHits;
        _deadline = DateTime.UtcNow + window;
        _swallowWindow = DefaultSwallowWindow;
        _maxSwallowBytes = 64 * 1024;
    }

    /// <summary>
    /// 只开注入窗口、<b>不挂任何抑制针</b>:从这一刻起扣住一切,直到哨兵出现。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>为什么内置脚本的注入不能靠抑制针。</b>抑制针是逐字节匹配"回显应该长什么样",
    /// 而那个前提只在<b>短命令</b>上成立。目录上报脚本有八九百字符,远超终端宽度,于是:
    /// </para>
    /// <list type="bullet">
    /// <item>ash / dash:回显每到行尾就插一对 <c>CR LF</c>,针从中间断掉;</item>
    /// <item>zsh:readline 折行时会 <c>CR</c> + <c>ESC[K</c> 重绘,并把断点处的字符<b>重复一遍</b>;</item>
    /// <item>fish:整行按列重新排版,插进一堆 <c>ESC[nC</c> 光标移动 —— 回显里连原文都拼不回来。</item>
    /// </list>
    /// <para>
    /// 真机上这三种全撞到了(<c>ShellIntegrationDockerTests</c>)。结论是:长注入不要去猜回显,
    /// <b>整段扣住</b>就好 —— 反正从写下这一行到哨兵返回,中间的一切本来就都是我们自己惹出来的。
    /// electerm 也是这么做的(它压根没有抑制针这一层)。
    /// </para>
    /// <para>
    /// <b>代价是这段窗口会连同其间的横幅 / MOTD 一起扣住</b>,所以宿主必须等对端<b>安静下来</b>
    /// 再注入(见 <c>SshTerminalBridge.RunWhenOutputIdle</c>):横幅先放完,窗口里就只剩我们自己的东西。
    /// </para>
    /// <para>
    /// 用户自己配的那些命令仍旧走抑制针 —— 它们短(<c>cd /srv/app</c>、<c>tmux attach</c>),
    /// 不会折行;而且它们的输出必须原样显示,不能整段扣。
    /// </para>
    /// </remarks>
    /// <param name="sentinel">窗口的闭合哨兵(整条转义序列)。</param>
    /// <param name="window">整体时间窗。</param>
    /// <param name="swallowWindow">吞噬窗口;默认 <see cref="DefaultSwallowWindow" />。</param>
    public static EchoSuppressor OpenWindow(byte[] sentinel, TimeSpan window, TimeSpan swallowWindow = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sentinel.Length, 1);
        EchoSuppressor suppressor = new(sentinel, window, swallowWindow);
        suppressor.EnterSwallow();
        return suppressor;
    }

    /// <summary>无针实例的私有构造:只为 <see cref="OpenWindow" /> 服务。</summary>
    private EchoSuppressor(byte[] sentinel, TimeSpan window, TimeSpan swallowWindow)
    {
        _maxHits = 2;
        _deadline = DateTime.UtcNow + window;
        _gateSentinel = sentinel;
        _swallowWindow = swallowWindow == default ? DefaultSwallowWindow : swallowWindow;
        _maxSwallowBytes = 64 * 1024;
    }

    /// <summary>
    /// 再挂一根针:紧接着注入的第二、第三条命令(初始目录 <c>cd</c>、认证后命令)的回显,
    /// 由同一个实例一并剥掉。
    /// </summary>
    /// <remarks>
    /// <b>不是换一个实例,而是加一根针。</b>换实例会把当前这个连同它扣住的字节一起丢掉 ——
    /// 而握手时这几条是**同一瞬间**连着发的,前一个还没见到任何数据就被顶掉了,
    /// 它那行回显于是原样留在屏幕上。窗口的开关只在构造时决定:带窗口的那条(目录上报脚本)
    /// 永远排在最前面发,后来的都不该再开窗口。
    /// </remarks>
    public void AddNeedle(byte[] needle)
    {
        if (needle.Length < MinHold)
        {
            throw new ArgumentException(@"Needle too short to suppress safely.", nameof(needle));
        }
        lock (_sync)
        {
            _needles.Add(new(needle, _maxHits));
        }
    }

    /// <summary>
    /// 抑制是否已失效;失效后 <see cref="Process" /> 放行一切。
    /// <para>
    /// 吞噬阶段<b>不算失效</b>,哪怕所有针的命中数都已用尽 —— 那时手里还扣着字节,
    /// 调用方一旦把实例丢掉就再也没人把它们放出来了。
    /// </para>
    /// </summary>
    public bool Expired
    {
        get
        {
            lock (_sync)
            {
                // 吞噬中只认整体窗口:哪怕针都用完了,手里还扣着字节,不能让调用方把实例丢掉。
                return _stage == Stage.Swallow
                    ? DateTime.UtcNow > _deadline
                    : AllHitsSpent() || DateTime.UtcNow > _deadline;
            }
        }
    }

    /// <summary>
    /// 取回并清空当前扣住的字节(块尾部分命中 + 吞噬阶段的缓冲),交还终端。
    /// <para>
    /// 扣住的字节只会在<b>下一次</b> <see cref="Process" /> 里放出来;调用方一旦因
    /// <see cref="Expired" /> 弃用本实例,就再也没有下一次——不在此处取回,那几个字节
    /// 就被永久吞掉了(抑制窗恰好在扣住的同一块内到期时发生)。
    /// </para>
    /// </summary>
    /// <returns>此前扣住、尚未喂终端的字节;没有则为空数组。</returns>
    public byte[] TakeHeld()
    {
        lock (_sync)
        {
            byte[] prefix = _held;
            _held = [];
            return Concat(prefix, TakeSwallowed());
        }
    }

    /// <summary>
    /// 强制结束吞噬:把扣住的字节交出来,并<b>退回剥回显</b>继续干活。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 吞噬阶段靠"下一块输出到来"推进,可注入失败时远端<b>恰恰不会再有输出</b> ——
    /// 报错和提示符都已经扣在手里,没人再来敲一下门,屏幕就空在那儿。宿主因此挂一个
    /// 一次性定时器,窗口到期时调用本方法把它们放出来。
    /// </para>
    /// <para>
    /// <b>退回 <see cref="Stage.Echo" /> 而不是收工:</b>后面还排着第二、第三条注入的回显没剥。
    /// 收工就等于把它们漏到屏幕上 —— 正是本类当初要解决的那个问题。
    /// </para>
    /// </remarks>
    public byte[] ForceFlush()
    {
        lock (_sync)
        {
            byte[] pending = TakeSwallowed();
            if (_stage == Stage.Swallow)
            {
                _stage = Stage.Echo;
            }
            return pending;
        }
    }

    /// <summary>
    /// 处理一块输出字节,剥除其中匹配到的命令回显;块尾的部分命中会被扣下,合并进下一块续判。
    /// 开启注入窗口时,第一条回显之后的字节会被扣住直到看见集成序列(见类注释)。
    /// </summary>
    public byte[] Process(byte[] data)
    {
        lock (_sync)
        {
            if (_stage != Stage.Swallow && (AllHitsSpent() || DateTime.UtcNow > _deadline))
            {
                return ReleaseHeld(data);
            }

            // 快路径:不在吞噬中、无扣留前缀、且整块不含任何针的首字节 → 原样返回,零分配零拷贝。
            // 抑制窗内绝大多数输出块(MOTD、提示符刷新)都走这里;无针实例(纯注入窗口)
            // 在窗口闭合后也走这里,直到 Expired 让宿主把它丢掉。
            if (_stage == Stage.Echo && _held.Length == 0 && !ContainsAnyFirstByte(data))
            {
                return data;
            }

            byte[] input = Concat(_held, data);
            _held = [];
            var output = new MemoryStream(input.Length);
            int i = 0;
            while (true)
            {
                if (_stage == Stage.Swallow)
                {
                    byte[] released = SwallowFrom(input, i);
                    if (_stage == Stage.Swallow)
                    {
                        break; // 还在扣,本块到此为止
                    }
                    // 吞噬结束:吐回来的字节要继续按回显扫 —— 第二条注入的回显可能就贴在后面。
                    input = released;
                    i = 0;
                    continue;
                }
                if (i >= input.Length)
                {
                    break;
                }
                int matched = MatchNeedleAt(input, i, out Needle? hit);
                if (hit is not null)
                {
                    // 整段命中:剥除。
                    i += matched;
                    hit.HitsLeft--;
                    continue;
                }
                if (matched > 0)
                {
                    // 块尾部分命中:整条尾巴扣住,等下一块续判。
                    _held = input[i..];
                    break;
                }
                output.WriteByte(input[i]);
                i++;
            }
            return output.ToArray();
        }
    }

    /// <summary>所有针的命中数都用尽了没有。</summary>
    private bool AllHitsSpent()
    {
        for (int n = 0; n < _needles.Count; n++)
        {
            if (_needles[n].HitsLeft > 0)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>这一块里有没有任何一根针的首字节(快路径判据)。</summary>
    private bool ContainsAnyFirstByte(byte[] data)
    {
        for (int n = 0; n < _needles.Count; n++)
        {
            Needle needle = _needles[n];
            if (needle.HitsLeft > 0 && Array.IndexOf(data, needle.Bytes[0]) >= 0)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 在 <paramref name="start" /> 处试所有针。
    /// </summary>
    /// <param name="input">输入块。</param>
    /// <param name="start">起点。</param>
    /// <param name="hit">整段命中的那根针;只有块尾部分命中或全不命中时为 null。</param>
    /// <returns>整段命中时是针的长度;块尾部分命中时是已匹配的字节数;都不是则 0。</returns>
    /// <remarks>
    /// 整段命中优先于部分命中,长针优先于短针 —— 两根针互为前缀时(重连重发同一条命令
    /// 再追加一条更长的),剥掉长的那根才不会在屏幕上留下半截尾巴。
    /// </remarks>
    private int MatchNeedleAt(byte[] input, int start, out Needle? hit)
    {
        hit = null;
        int best = 0;
        int partial = 0;
        for (int n = 0; n < _needles.Count; n++)
        {
            Needle needle = _needles[n];
            if (needle.HitsLeft <= 0 || input[start] != needle.Bytes[0])
            {
                continue;
            }
            int matched = MatchFrom(input, start, needle.Bytes);
            if (matched == needle.Bytes.Length)
            {
                if (matched > best)
                {
                    best = matched;
                    hit = needle;
                }
                continue;
            }
            // 到块尾都还在匹配,且够长 —— 值得扣住等下一块。
            if (matched >= MinHold && start + matched == input.Length && matched > partial)
            {
                partial = matched;
            }
        }
        return hit is not null ? best : partial;
    }

    /// <summary>进入吞噬阶段并起算它自己的窗口。</summary>
    private void EnterSwallow()
    {
        _stage = Stage.Swallow;
        _swallowed = new();
        _swallowDeadline = DateTime.UtcNow + _swallowWindow;
    }

    /// <summary>
    /// 吞噬阶段:扣住 <paramref name="from" /> 起的输入,直到缓冲里出现集成序列 ——
    /// 从那个位置起放行(之前的全是注入的副作用,不上屏);或到期 / 超上限 —— 原样放出来
    /// (见类注释里"绝不倒扣"那一条)。
    /// </summary>
    /// <returns>该放行的字节;仍在扣时为空数组(此时阶段仍是 <see cref="Stage.Swallow" />)。</returns>
    private byte[] SwallowFrom(byte[] input, int from)
    {
        _swallowed ??= new();
        if (from < input.Length)
        {
            _swallowed.Write(input, from, input.Length - from);
        }
        byte[] buffered = _swallowed.ToArray();
        int marker = IndexOf(buffered, _gateSentinel!);
        if (marker >= 0)
        {
            // 从哨兵**结束处**恢复放行:哨兵本身也不上屏(它是我们自己的记号,
            // 用户没理由看见),而且整条一起吞掉才不会把它的 BEL 漏成一声真响的铃。
            _stage = Stage.Echo;
            _swallowed = null;
            return buffered[(marker + _gateSentinel!.Length)..];
        }
        if (buffered.Length >= _maxSwallowBytes || DateTime.UtcNow > _swallowDeadline || DateTime.UtcNow > _deadline)
        {
            _stage = Stage.Echo;
            _swallowed = null;
            return buffered;
        }
        return [];
    }

    /// <summary>取走并清空吞噬缓冲。</summary>
    private byte[] TakeSwallowed()
    {
        if (_swallowed is not { } buffer)
        {
            return [];
        }
        _swallowed = null;
        return buffer.ToArray();
    }

    /// <summary>
    /// 在 <paramref name="haystack" /> 里找 <paramref name="needle" /> 的起点;找不到返回 -1。
    /// </summary>
    /// <remarks>
    /// 朴素扫描足够:只有吞噬阶段会调它,缓冲上限 64 KiB,而且整个阶段只活两秒。
    /// </remarks>
    internal static int IndexOf(byte[] haystack, byte[] needle)
    {
        if (needle.Length == 0 || haystack.Length < needle.Length)
        {
            return -1;
        }
        int last = haystack.Length - needle.Length;
        for (int i = 0; i <= last; i++)
        {
            if (haystack[i] != needle[0])
            {
                continue;
            }
            int k = 1;
            while (k < needle.Length && haystack[i + k] == needle[k])
            {
                k++;
            }
            if (k == needle.Length)
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>
    /// 从 <paramref name="start" /> 起与 <paramref name="needle" /> 连续匹配的字节数;
    /// 首个不匹配即返回 0。返回值等于可用长度表示"到块尾都还在匹配"。
    /// </summary>
    private static int MatchFrom(byte[] input, int start, byte[] needle)
    {
        int limit = Math.Min(needle.Length, input.Length - start);
        for (int k = 0; k < limit; k++)
        {
            if (input[start + k] != needle[k])
            {
                return 0;
            }
        }
        return limit;
    }

    private byte[] ReleaseHeld(byte[] data)
    {
        if (_held.Length == 0)
        {
            return data;
        }
        byte[] result = Concat(_held, data);
        _held = [];
        return result;
    }

    private static byte[] Concat(byte[] head, byte[] tail)
    {
        if (tail.Length == 0)
        {
            return head;
        }
        if (head.Length == 0)
        {
            return tail;
        }
        byte[] merged = new byte[head.Length + tail.Length];
        head.CopyTo(merged, 0);
        tail.CopyTo(merged, head.Length);
        return merged;
    }
}

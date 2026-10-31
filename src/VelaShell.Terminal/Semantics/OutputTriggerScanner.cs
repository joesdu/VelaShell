using VelaShell.Core.OutputRules;
using VelaShell.Terminal.Emulation;

namespace VelaShell.Terminal.Semantics;

/// <summary>
/// 输出触发器(自动应答)的判定:远端输出之后,光标所在那一行是不是一条等着输入的、命中了某条规则的提示;
/// 命中了能不能现在就应答(冷却、每分钟上限)。
/// </summary>
/// <remarks>
/// <para>
/// <b>只看光标所在的逻辑行。</b>expect 式应答对付的是「程序停下来等你回答」—— 那一刻提示就在光标所在的行上。
/// 滚过去的输出(<c>cat</c> 一个日志、<c>grep</c> 出来的行)光标早已离开,即使文字一模一样也不应答:
/// 否则 <c>cat</c> 一份含 "Continue? [y/N]" 的文件就会往 shell 里敲一个 <c>y</c> 回车。
/// 顺带这也把开销钉死在「每批输出一行」,与输出量无关。
/// </para>
/// <para>
/// <b>带着 shell 提示符标记(OSC 133 的 A / B)的行不应答。</b>文件最后一行没有换行时,bash 的提示符接在同一行上,
/// 光标并没有离开那段文字 —— 上一条规矩拦不住,只有 shell 集成报的标记说得清这一行是 shell 在等命令。
/// 没装集成时认不出来,规则写到提示的结尾(<c>\[y/N\] ?$</c>)才不会命中。
/// </para>
/// <para>
/// <b>只在主屏</b>(备用屏是全屏程序,行是界面不是对话),<b>只在远端输出之后判</b>(终端控件的
/// <c>FeedRemote</c>;本地回显、宿主写进来的提示语、录制回放都不走它)。
/// </para>
/// <para>
/// <b>用户正在这一行上敲字时不应答。</b>自上次回车以来本地有过键入(含本地回显、粘贴、快捷命令),
/// 光标行上的字就有一部分是用户自己的、被回显出来的 —— 在 shell 提示符后敲 <c>echo "(yes/no)"</c>
/// 不该被当成一次提问;用户已经开始手答一个提示时,也不该再替他答一遍。回车之后重新开始判。
/// </para>
/// <para>
/// <b>同一行只应答一次</b>(<see cref="TerminalRow.TriggerSettled" /> 记在逻辑行的首行上,整行被擦空才作废),
/// 另有每条规则的冷却与每分钟上限:冷却内出现的新提示推迟到冷却结束再判,不丢;到了上限就不再应答,
/// 报一次 <see cref="OutputTriggerHit.RateLimited" />,由用户接手 —— 对端刷屏时不会被我们刷发回去。
/// </para>
/// <para>仅在 UI 线程上使用。</para>
/// </remarks>
internal sealed class OutputTriggerScanner
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private readonly Dictionary<Guid, Budget> _budgets = [];
    private char[] _text = new char[256];

    /// <summary>当前生效的规则集。换了规则集时,已经不在集里的规则的记账一并丢掉。</summary>
    public OutputRuleSet Rules
    {
        get;
        set
        {
            field = value ?? OutputRuleSet.Empty;
            foreach (Guid id in _budgets.Keys.Where(id => !field.Triggers.Any(rule => rule.Id == id)).ToList())
            {
                _budgets.Remove(id);
            }
        }
    } = OutputRuleSet.Empty;

    /// <summary>自上次回车以来本地有没有键入过(见类型注释第三段)。</summary>
    internal bool TypedSinceSubmit { get; private set; }

    /// <summary>
    /// 记一次本地输入(终端控件的 <c>TypedInput</c> 出口,含本地回显与程序注入)。
    /// </summary>
    /// <remarks>
    /// 以最后一个回车 / 换行为界:之后没有别的字节(或只剩括号粘贴的结束标记)= 这一行交出去了,
    /// 重新开始判;否则 = 用户正在敲这一行。
    /// </remarks>
    /// <param name="data">发往对端的字节。</param>
    public void NoteTypedInput(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            return;
        }
        int lastBreak = data.LastIndexOfAny((byte)'\r', (byte)'\n');
        if (lastBreak < 0)
        {
            TypedSinceSubmit = true;
            return;
        }
        ReadOnlySpan<byte> tail = data[(lastBreak + 1)..];
        TypedSinceSubmit = !(tail.IsEmpty || tail.SequenceEqual("\e[201~"u8));
    }

    /// <summary>
    /// 光标所在的逻辑行上有没有一条命中的提示(还没应答过、本地没在敲)。
    /// </summary>
    /// <param name="emulator">仿真器。</param>
    /// <returns>命中的规则与那一行的首行;没有时为 null。</returns>
    public Candidate? FindCandidate(TerminalEmulator emulator)
    {
        if (!Rules.HasTriggers || emulator.IsAlternateScreen || TypedSinceSubmit)
        {
            return null;
        }
        TerminalScreen screen = emulator.Screen;
        int end = Math.Clamp(screen.CursorY, 0, screen.Rows - 1);
        int start = end;
        while (start > 0 && screen.ActiveLine(start - 1).Wrapped)
        {
            start--;
        }
        TerminalRow head = screen.ActiveLine(start);
        if (head.TriggerSettled)
        {
            return null;
        }
        int length = 0;
        for (int row = start; row <= end; row++)
        {
            TerminalRow line = screen.ActiveLine(row);
            // 这一行上有 shell 自己的提示符(OSC 133 的 A / B):在等的是 shell 读命令,不是程序在提问。
            // cat / printf 的最后一行没有换行时,bash 把 PS1 直接画在那段文字后面(zsh / fish 会先另起一行),
            // 光标行读出来是 "Continue? [y/N]user@host:~$ " —— 这时应答就是往 shell 里敲一条命令。
            if (CommandBlocks.CarriesShellPrompt(line))
            {
                return null;
            }
            int written;
            while ((written = line.CopyTextTo(_text.AsSpan(length))) < 0)
            {
                Array.Resize(ref _text, _text.Length * 2);
            }
            length += written;
        }
        if (length == 0)
        {
            return null;
        }
        ReadOnlySpan<char> text = _text.AsSpan(0, length);
        foreach (CompiledOutputRule rule in Rules.Triggers)
        {
            if (rule.FirstMatch(text).Length > 0)
            {
                return new(rule, head);
            }
        }
        return null;
    }

    /// <summary>候选这条规则还要等多久才能应答(冷却);0 = 现在就行。</summary>
    /// <param name="candidate">候选。</param>
    /// <param name="now">此刻。</param>
    /// <returns>还要等的时长。</returns>
    public TimeSpan WaitBeforeFiring(Candidate candidate, DateTimeOffset now)
    {
        if (!_budgets.TryGetValue(candidate.Rule.Id, out Budget? budget) || budget.Last is not { } last)
        {
            return TimeSpan.Zero;
        }
        TimeSpan left = last + candidate.Rule.Cooldown - now;
        return left > TimeSpan.Zero ? left : TimeSpan.Zero;
    }

    /// <summary>
    /// 试着应答:冷却没过返回 null(调用方到点再判);到了每分钟上限报一次 <see cref="OutputTriggerHit.RateLimited" />;
    /// 否则记账、把这一行记成已应答,交出要发的应答。
    /// </summary>
    /// <param name="candidate">候选。</param>
    /// <param name="now">此刻。</param>
    /// <returns>要报给宿主的一次应答(或一次「到上限了」);什么都不该做时为 null。</returns>
    public OutputTriggerHit? TryFire(Candidate candidate, DateTimeOffset now)
    {
        CompiledOutputRule rule = candidate.Rule;
        if (!_budgets.TryGetValue(rule.Id, out Budget? budget))
        {
            _budgets[rule.Id] = budget = new();
        }
        while (budget.Fires.Count > 0 && now - budget.Fires.Peek() >= Window)
        {
            budget.Fires.Dequeue();
        }
        if (budget.Fires.Count == 0)
        {
            budget.LimitReported = false;
        }
        if (budget.Fires.Count >= rule.MaxPerMinute)
        {
            // 这一行不再答,也不再反复判它;上限一个窗口期只报一次,别把提示刷成另一种刷屏。
            candidate.Head.TriggerSettled = true;
            if (budget.LimitReported)
            {
                return null;
            }
            budget.LimitReported = true;
            return Hit(rule, rateLimited: true);
        }
        if (budget.Last is { } last && now - last < rule.Cooldown)
        {
            return null;
        }
        budget.Fires.Enqueue(now);
        budget.Last = now;
        candidate.Head.TriggerSettled = true;
        return Hit(rule, rateLimited: false);
    }

    private static OutputTriggerHit Hit(CompiledOutputRule rule, bool rateLimited) =>
        new(rule.Id, rule.Name, rule.Response, rule.SendEnter, rule.ForwardToSyncChannel, rateLimited);

    /// <summary>一次候选:命中的规则,与那一逻辑行的首行(应答记在它上面)。</summary>
    /// <param name="Rule">命中的规则。</param>
    /// <param name="Head">逻辑行的首行。</param>
    internal sealed record Candidate(CompiledOutputRule Rule, TerminalRow Head);

    private sealed class Budget
    {
        public Queue<DateTimeOffset> Fires { get; } = new();

        public DateTimeOffset? Last { get; set; }

        public bool LimitReported { get; set; }
    }
}

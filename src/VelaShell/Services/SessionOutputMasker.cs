using System.Buffers;
using System.Text;
using System.Text.RegularExpressions;

namespace VelaShell.Services;

/// <summary>
/// 会话日志与会话录制的输出遮盖:落盘之前,把终端输出里形如口令、令牌、私钥的片段换成 <c>***</c>。只动记录的那一份,屏幕上照常显示。
/// </summary>
/// <remarks>
/// <para>
/// 只录输出、不录输入是对的(密码没有回显),但输出里照样有密钥:<c>cat .env</c>、<c>kubectl get secret -o yaml</c>、
/// <c>env | grep TOKEN</c>。会话日志与录制存的是原始字节,事后谁拿到文件谁就看得到。
/// 这里挂在桥的旁路(<c>DataReceived</c>)与记录之间,显示路径一个字节都不经过它。
/// </para>
/// <para>
/// 按字节处理:每个字节按 Latin-1 映射成一个字符再匹配,规则全是 ASCII,换出来的也只有 ASCII,
/// 终端编码是 UTF-8 还是 GBK 都原样往返;词边界也只按 ASCII 算(映射之后 UTF-8 的 0xAA、0xB5、0xBA、0xC0–0xFF 在 <c>\b</c> 眼里是字母,
/// 紧挨着汉字的令牌就认不出了)。
/// </para>
/// <para>
/// <b>先剥掉控制序列再匹配</b>:规则跑在一行的「可见文本」上(<see cref="Projection" />)—— CSI 与两三字节的 ESC 序列不占位置,
/// OSC / DCS 一类字符串序列的开头与 ST 结尾各留一个 ESC 作分界(标题里的内容照样过规则,却不会跟后面的提示符连成一个值),
/// 匹配到的位置再映射回原文。于是 <c>grep --color</c> 把关键词或令牌从中间切开(<c>PASS ESC[m ESC[K WORD=</c>、<c>ghp_ESC[m ESC[K0123…</c>)、
/// jq / bat 给分隔符与值分别上色,都与没上色的一样认。遮掉的那一段里夹着的控制序列原样留在 <c>***</c> 后面,回放时颜色与光标状态不乱。
/// </para>
/// <para>
/// 规则(大小写不敏感):
/// 名字里带 password / passwd / passphrase / secret / token / api key / access key / private key / credential 这几个词(要成词:<c>max_tokens</c>、<c>tokenizers</c> 不算;
/// token 与 secret 后面跟着 type / endpoint / name / count 一类非机密后缀的也不算,<c>token_type</c>、<c>secretName</c>,见 <see cref="KeyValue" />)的
/// <c>名字=值</c>、<c>名字: 值</c>(含 JSON 与 YAML 的写法、JSON 里转义过的 <c>\"名字\":</c>)只换值,双引号串认反斜杠转义,
/// 到行尾都没收尾的引号串从引号之后遮到行尾,转义过的 JSON 串被外层的裸引号截断时遮到那个引号之前,
/// 冒号后面跟空白、不加引号的值遮到行尾(INI 写法 <c>名字 = 值</c> 名字在行首、等号两边都有空白的同样如此;停在同一行的下一个键之前,判据见 <see cref="KeyValue" />);
/// 几个不带口令字样的键:<c>tls.key</c> 一类、kubeconfig 的 <c>client-key-data</c>、<c>.dockerconfigjson</c>、Docker <c>config.json</c> 的 <c>"auth"</c>;
/// <c>.netrc</c> 的 <c>password</c>(<c>machine … password …</c> 一行写完的,或分行写时单独一行的);
/// <c>Bearer</c> / <c>Basic</c> 后面的凭据(Basic 只认解得开、带冒号的 base64,Bearer 不认纯字母又不够长的英文单词,见 <see cref="LooksLikeCredential" />);PEM 私钥块(含 OpenPGP 的 <c>PGP PRIVATE KEY BLOCK</c>)的正文(写在一行里的,BEGIN 与 END 之间那段;
/// 多行的只认 base64 正文行,一行不像私钥块里的行就当私钥块已经结束,见 <see cref="KeyLine" />);
/// 几种自带前缀的令牌(AWS <c>AKIA…</c> / <c>ASIA…</c>、GitHub <c>ghp_…</c> 一类与 <c>github_pat_…</c>、GitLab <c>glpat-…</c>、
/// Stripe <c>sk_live_…</c> / <c>rk_live_…</c>、Google <c>AIza…</c>、npm <c>npm_…</c>、PyPI <c>pypi-…</c>、Slack <c>xox?-…</c>、OpenAI 式 <c>sk-…</c>;
/// 前缀之后的长度与字符集各自卡住,见 <see cref="PrefixedToken" />)。
/// 宁可多遮,不放过。
/// </para>
/// <para>
/// 一段输出可能在任何字节处被切开,打字回显更是一个字节一段。当前行已经放出去的原文(大约最后 <see cref="MaxContextBytes" /> 字节)留作匹配上下文,
/// 新到的字节接在它后面把全部规则(含私钥块的开头)重跑一遍,只改写值落在还没放出去的字节里的那些匹配 ——
/// 名字、控制序列、<c>Bea|rer</c>、<c>-----BEGI|N</c> 切在哪都认得出。上下文截断时不截在任何一个匹配或控制序列的中间,吞掉的长值也不进上下文:
/// 值里像「名字: 值」的字样不会被当成新的匹配、把后面真正的名字吞掉。私钥块的开头与结尾另按可见文本的最后几十个字符认。
/// 名字与分隔符当场放出,只扣<b>值</b>,扣到值结束(空白、收尾的引号、行尾……)为止;
/// 值还没开始的(<c>Password:</c>、<c>Password: </c> 这类等输入的提示)一个字节也不扣,vim、top 这类不换行的全屏程序也不受影响。
/// 扣下的值有上限,超了先放一个 <c>***</c>,值剩下的部分照它自己的结束条件吞完,尾巴不会漏出去。
/// </para>
/// <para>
/// 规则全是线性的:<see cref="Write" /> 跑在桥的读循环上,<see cref="Flush" /> 在 UI 线程上,一条会回溯爆炸的正则在这里就是界面卡死。
/// </para>
/// <para>
/// 线程:<see cref="Write" /> 跑在终端读线程上;<see cref="Flush" /> 在停止记录时从 UI 线程调,而旧桥可能还在往这里写
/// (停止记录不退订,见 <c>MainWindowViewModel.StopSessionLogging</c>),两者用一把锁串起来。
/// </para>
/// </remarks>
public sealed partial class SessionOutputMasker
{
    /// <summary>扣下的值最多攒这么多字节;超了先放一个 <c>***</c>,值剩下的部分吞到它结束为止。</summary>
    internal const int MaxHeldBytes = 4096;

    /// <summary>
    /// 当前行已放出去的部分大约留多少字节作匹配上下文:名字、分隔符、颜色序列都在这个范围里。
    /// 截断点落在一个匹配或控制序列中间时往前挪到它的开头(见 <see cref="Advance" />)。
    /// </summary>
    internal const int MaxContextBytes = 512;

    /// <summary>认被切开的 <c>-----BEGIN/END … PRIVATE KEY-----</c> 要留的可见字符数(吞私钥正文时末尾也先留这么多不吞)。</summary>
    private const int MarkerTailBytes = 64;

    /// <summary>
    /// 一个 CSI / ESC 序列最长认多少字节;更长的当成坏序列,ESC 留在可见文本里(它是控制字符,值到它为止)。
    /// 真实终端的序列远短于此;不设上限的话,一条被逐字节写来的超长序列会让上下文一直不能截断。
    /// </summary>
    private const int MaxSequenceBytes = 256;

    /// <summary>遮盖后的占位。</summary>
    private const string Mask = "***";

    /// <summary>不加引号的值到这些字符为止:空白、引号、逗号、分号,以及 C0 控制字符与 DEL(OSC 标题收尾的 BEL 不能被值吞掉)。</summary>
    private static readonly SearchValues<char> PlainValueEnds =
        SearchValues.Create(" \"',;\u007f" + string.Concat(Enumerable.Range(0, 0x20).Select(c => (char)c)));

    /// <summary><c>.netrc</c> 口令到这些字符为止:空白,以及 C0 控制字符与 DEL。</summary>
    private static readonly SearchValues<char> WordEnds =
        SearchValues.Create(" \u007f" + string.Concat(Enumerable.Range(0, 0x20).Select(c => (char)c)));

    /// <summary>
    /// 吞 <see cref="ValueKind.LineValue" /> 时,空白之后要看多远才能断定那是不是下一个键;再远的当成值的一部分吞掉。
    /// </summary>
    private const int MaxKeyProbeBytes = 256;

    /// <summary><c>Bearer</c> / <c>Basic</c> 凭据的字符。</summary>
    private static readonly SearchValues<char> CredentialChars =
        SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789._~+/-=");

    /// <summary>带前缀的令牌的字符。</summary>
    private static readonly SearchValues<char> TokenChars =
        SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789_-");

    private readonly Action<byte[]> _sink;
    private readonly Lock _gate = new();

    /// <summary>当前行已经放出去的原文(截在匹配与匹配之间、控制序列之外,见 <see cref="Advance" />):只作匹配上下文,不再输出。</summary>
    private string _context = string.Empty;

    /// <summary>
    /// 上下文截断过时,截断点前的最后一个可见字符:只给 <c>Bearer</c> 与令牌开头的「前面不是字母数字」看
    /// (<c>xBearer …</c> 截成 <c>Bearer …</c> 不能凭空多出一个凭据);<c>名字=值</c> 不看它,截在标识符中间时后半截照样当名字认。
    /// 没截断过为 null。
    /// </summary>
    private char? _contextPrev;

    /// <summary>
    /// 上下文是从当前行的行首起的(没截过,也不是吞完一个值或私钥块结尾之后在行中间重新起的):只认行首的规则(<see cref="NetrcLine" />)据此才用。
    /// </summary>
    private bool _contextAtLineStart = true;

    /// <summary>当前行处理过的部分的最后 <see cref="MarkerTailBytes" /> 个可见字符:认私钥块的开头与结尾用。</summary>
    private string _keyTail = string.Empty;

    /// <summary>扣着没放的原文(总在当前行里):写到一半的值,或者吞值时留着拼下一段的那几个字节。</summary>
    private string _held = string.Empty;

    /// <summary>
    /// 在多行私钥块里(上一行有 BEGIN 而没有 END):只遮正文行,直到出现 END 的那一行,或者来了一行私钥块里不会有的东西(见 <see cref="KeyLine" />)。
    /// </summary>
    private bool _inPrivateKey;

    /// <summary>当前行处理过的部分里出现过 BEGIN、还没见到 END:后面到行尾都是私钥正文。</summary>
    private bool _inKeyBody;

    /// <summary>值超了上限(或者收尾)已经放过一个 <c>***</c>:剩下的部分照这种值的结束条件吞掉。</summary>
    private ValueKind _swallowing;

    /// <summary>遮完的输出交给 <paramref name="sink" />(会话日志或录制的 <c>Write</c>)。</summary>
    public SessionOutputMasker(Action<byte[]> sink) => _sink = sink ?? throw new ArgumentNullException(nameof(sink));

    /// <summary>值的种类:决定它到哪结束,扣到上限之后据此把剩下的部分吞完。</summary>
    private enum ValueKind
    {
        None,

        /// <summary>不加引号的值:到空白、引号、逗号、分号或控制字符为止。</summary>
        Plain,

        /// <summary><c>名字: 值</c> 与 INI 写法 <c>名字 = 值</c> 里不加引号的值:到行尾、控制字符,或者空白之后跟着的下一个键、YAML 注释为止。</summary>
        LineValue,

        /// <summary><c>.netrc</c> 的口令:到空白或控制字符为止(引号、逗号都可能是口令的一部分)。</summary>
        Word,

        /// <summary>双引号串(含 JSON 里转义过的 <c>\"…\"</c>):到不带转义的 <c>"</c> 或行尾为止。</summary>
        DoubleQuoted,

        /// <summary>单引号串:到 <c>'</c> 或行尾为止(shell 的单引号里反斜杠不转义)。</summary>
        SingleQuoted,

        /// <summary><c>Bearer</c> / <c>Basic</c> 的凭据。</summary>
        Credential,

        /// <summary>带前缀的令牌。</summary>
        Token,

        /// <summary>同一行里 BEGIN 之后的私钥正文:到 END 或行尾为止。</summary>
        KeyBody,

        /// <summary>多行私钥块里的一行:到 END 或行尾为止。</summary>
        KeyLine
    }

    /// <summary>收下一段输出(桥的 <c>DataReceived</c>)。</summary>
    public void Write(byte[] chunk)
    {
        if (chunk.Length == 0)
        {
            return;
        }
        lock (_gate)
        {
            Process(Encoding.Latin1.GetString(chunk), final: false);
        }
    }

    /// <summary>收尾:扣着的那一点遮完放出(会话结束、停止记录时)。</summary>
    public void Flush()
    {
        lock (_gate)
        {
            if (_held.Length > 0)
            {
                Process(string.Empty, final: true);
            }
        }
    }

    /// <summary>
    /// 接着扣着的部分处理新到的输出,逐行放出;<paramref name="final" /> 时不再扣,写到一半的值当场遮完。
    /// 行尾(含 <c>\r\n</c>)原样保留,录制回放的行结构不变。
    /// </summary>
    private void Process(string text, bool final)
    {
        string data = _held + text;
        _held = string.Empty;
        var output = new StringBuilder(data.Length);
        int start = 0;
        while (start < data.Length)
        {
            int newline = data.IndexOf('\n', start);
            int end = newline < 0 ? data.Length : newline + 1;
            Segment(data[start..end], complete: newline >= 0, final, output);
            start = end;
        }
        if (output.Length > 0)
        {
            _sink(Encoding.Latin1.GetBytes(output.ToString()));
        }
    }

    /// <summary>当前行新到的一段:从已放出处起,到行尾(<paramref name="complete" />)或已收到内容的末尾。</summary>
    private void Segment(string segment, bool complete, bool final, StringBuilder output)
    {
        if (_swallowing != ValueKind.None)
        {
            int end = SwallowEnd(segment, out int keep);
            if (end < 0)
            {
                // 值还没完:吞掉(里面的控制序列照放);末尾几个字节先留着,下一段来了拼起来再看(收尾时一并丢掉)
                Consume(segment[..^keep], output);
                _held = final ? string.Empty : segment[^keep..];
                return;
            }
            bool closingQuote = (_swallowing is ValueKind.DoubleQuoted or ValueKind.SingleQuoted) && segment[end] is not ('\r' or '\n');
            Consume(segment[..end], output);
            if (closingQuote)
            {
                // 收尾的引号算在值里,由这里放出,遮完的样子是完整的 "***"
                output.Append(segment[end]);
                end++;
            }
            _swallowing = ValueKind.None;
            segment = segment[end..];
            // 上下文从值的结尾重新起:吞掉的值里像「名字: 值」的字样不能再拿来匹配
            ResetContext(atLineStart: false);
        }
        if (_inPrivateKey)
        {
            KeyLine(segment, complete, final, output);
        }
        else
        {
            Line(segment, complete, final, output);
        }
    }

    /// <summary>上下文清空,从下一个字节重新攒(行首,或者吞完一个值、私钥块结尾之后的行中间)。</summary>
    private void ResetContext(bool atLineStart)
    {
        _context = string.Empty;
        _contextPrev = null;
        _contextAtLineStart = atLineStart;
    }

    /// <summary>平常的一行:上下文接上新到的一段跑全部规则,放出已经定了的部分;伸到末尾、后面可能还有的值扣下。</summary>
    private void Line(string segment, bool complete, bool final, StringBuilder output)
    {
        string line = _context + segment;
        int from = _context.Length;
        var view = Projection.Of(line, _contextPrev);
        var markers = new List<(int End, bool Begin)>();
        var matches = new List<(int Start, int End)>();
        List<Secret> secrets = FindSecrets(view, from, complete, markers, matches);
        int pos = from;
        foreach (Secret secret in secrets)
        {
            // 开头已经放出去的(带前缀的令牌被切在前缀里):只遮还没放的那部分
            int start = Math.Max(secret.Start, from);
            output.Append(line, pos, start - pos);
            if (secret.Open)
            {
                if (!final && line.Length - start < MaxHeldBytes)
                {
                    Advance(view, from, start, markers, matches);
                    _held = line[start..];
                    return;
                }
                // 收尾或者扣到上限:不再等,先遮,值剩下的部分(包括之后才到的)照它的结束条件吞掉。
                // 从伸到末尾的那个值自己的正文(引号之后)吞起,结束条件才对得上;前面的部分一并算作遮掉了
                output.Append(secret.Start < from ? string.Empty : secret.Quote).Append(Mask);
                int content = Math.Max(secret.ContentStart, from);
                view.AppendHidden(output, start, content);
                Advance(view, from, content, markers, matches);
                _swallowing = secret.Kind;
                Segment(line[content..], complete: false, final, output);
                return;
            }
            string quote = secret.Start < from ? string.Empty : secret.Quote;
            output.Append(quote).Append(Mask);
            view.AppendHidden(output, start, secret.End);
            output.Append(quote);
            pos = secret.End;
        }
        output.Append(line, pos, line.Length - pos);
        if (complete)
        {
            // 有 BEGIN 而到行尾都没见到 END:下一行起按私钥块整行遮
            _inPrivateKey = markers.Count > 0 ? markers[^1].Begin : _inKeyBody;
            _inKeyBody = false;
            ResetContext(atLineStart: true);
            _keyTail = string.Empty;
        }
        else
        {
            Advance(view, from, line.Length, markers, matches);
        }
    }

    /// <summary>
    /// 当前行处理到 <paramref name="end" /> 为止:之前的原文留作上下文,可见文本也接进认私钥块用的尾巴,私钥块的开头与结尾记到这里为止。
    /// </summary>
    /// <remarks>
    /// 上下文只留最后 <see cref="MaxContextBytes" /> 个字节左右,但截断点不能落在任何一个匹配(名字 … 值)的中间:
    /// 从一个长值的中间截起,值里像 <c>Password:"</c> 的字样会被当成新的名字与引号,把后面真正的名字吞进去当值,
    /// 真正的值就漏了。落在中间就往前挪到那个匹配的开头 —— 从那里重新扫,与整行一起扫的结果一致。
    /// 也不能落在控制序列中间(剩下的半截会被当成可见文本)。截断点前的最后一个可见字符记到 <see cref="_contextPrev" />。
    /// </remarks>
    private void Advance(Projection view, int from, int end, List<(int End, bool Begin)> markers, List<(int Start, int End)> matches)
    {
        foreach ((int markerEnd, bool begin) in markers)
        {
            if (markerEnd <= end)
            {
                _inKeyBody = begin;
            }
        }
        int keep = Math.Max(0, end - MaxContextBytes);
        for (bool moved = true; moved;)
        {
            moved = false;
            foreach ((int start, int stop) in matches.Concat(view.Hidden))
            {
                if (start < keep && keep < stop)
                {
                    keep = start;
                    moved = true;
                }
            }
        }
        if (keep > 0)
        {
            _contextPrev = view.LastVisibleBefore(keep);
            _contextAtLineStart = false;
        }
        _context = view.Raw[keep..end];
        _keyTail = KeyTail(_keyTail + view.VisibleBetween(from, end));
    }

    /// <summary>
    /// 多行私钥块里的一行(上一行有 BEGIN 而没有 END)。只认私钥块里真会出现的几种行:
    /// <list type="bullet">
    /// <item>正文行(base64,可带 <c>cat -n</c> / <c>grep -n</c> / <c>grep -A</c> 加的行号、文件名前缀):正文换成 <c>***</c>,前缀、控制序列、行尾照放;</item>
    /// <item>空行与 PEM / OpenPGP 的头部行(<c>Proc-Type:</c>、<c>DEK-Info:</c>、<c>Version:</c>、<c>Comment:</c>…):原样;</item>
    /// <item>出现 END 的那一行:END 照放,它前面同一行的正文换成 <c>***</c>,之后回到平常。</item>
    /// </list>
    /// 别的行说明这并不是一个私钥块(<c>echo</c> 了一行 BEGIN、截断的输出):私钥块到此为止,这一行照平常处理 ——
    /// 不然一行 BEGIN 就能让之后的整段录像变成一行行 <c>***</c>,提示符也被扣着等回车。
    /// </summary>
    private void KeyLine(string segment, bool complete, bool final, StringBuilder output)
    {
        if (segment.Length == 0)
        {
            return;
        }
        var view = Projection.Of(segment, null);
        string visible = view.Visible;
        if (FirstKeyEnd(visible, visible.Length) is { } end)
        {
            int endStart = view.RawStart(end.Index);
            int stop = view.RawEnd(end.Index + end.Length);
            // END 前面同一行里还有正文(BBBB…-----END …):正文换成 ***,END 照放;只有缩进或行号、文件名前缀的照原样
            int prefix = KeyLinePrefix(visible.AsSpan(0, end.Index));
            bool hasBody = visible.AsSpan(prefix, end.Index - prefix).IndexOfAnyExcept(' ', '\t', '\r') >= 0;
            if (hasBody)
            {
                AppendMaskedSpan(output, view, prefix, end.Index);
            }
            else
            {
                output.Append(segment, 0, endStart);
            }
            output.Append(segment, endStart, stop - endStart);
            _inPrivateKey = false;
            _inKeyBody = false;
            ResetContext(atLineStart: false);
            _keyTail = KeyTail(visible[..(end.Index + end.Length)]);
            Line(segment[stop..], complete, final, output);
            return;
        }
        if (complete)
        {
            int contentEnd = visible.Length - (visible.EndsWith("\r\n", StringComparison.Ordinal) ? 2 : 1);
            switch (ClassifyKeyLine(visible.AsSpan(0, contentEnd), out int bodyStart))
            {
                case KeyLineKind.Body:
                    AppendMaskedSpan(output, view, bodyStart, contentEnd);
                    output.Append(segment, view.RawStart(contentEnd), segment.Length - view.RawStart(contentEnd));
                    break;
                case KeyLineKind.Kept:
                    // 空行、头部行:照平常的规则过一遍(Comment 里也可能有口令),私钥块还在继续
                    Line(segment, complete, final, output);
                    _inPrivateKey = true;
                    return;
                default:
                    _inPrivateKey = false;
                    Line(segment, complete, final, output);
                    return;
            }
            ResetContext(atLineStart: true);
            _keyTail = string.Empty;
            return;
        }
        if (!MayBeKeyLine(visible))
        {
            // 已经看得出不是私钥块里的行(提示符里的 @ $ # > 一类):不扣,照平常处理
            _inPrivateKey = false;
            Line(segment, complete, final, output);
            return;
        }
        // 还不知道是正文、头部还是 END 那一行:整行扣着;收尾或者扣到上限就先遮,这一行剩下的吞到 END 或行尾为止
        if (!final && segment.Length < MaxHeldBytes)
        {
            _held = segment;
            return;
        }
        output.Append(Mask);
        _swallowing = ValueKind.KeyLine;
        Segment(segment, complete: false, final, output);
    }

    /// <summary>
    /// 可见文本 <c>[start, end)</c> 换成一个 <c>***</c>:原文里落在第一个非空白字符之前的控制序列放在它前面(颜色从正文起),
    /// 其余的放在后面;<paramref name="start" /> 之前的原文(行号、文件名前缀)照放。
    /// </summary>
    private static void AppendMaskedSpan(StringBuilder output, Projection view, int start, int end)
    {
        int rawStart = start == 0 ? 0 : view.RawStart(start);
        output.Append(view.Raw, 0, rawStart);
        int text = view.Visible.AsSpan(start, end - start).IndexOfAnyExcept(' ', '\t', '\r');
        int rawText = text < 0 ? view.RawStart(end) : view.RawStart(start + text);
        int rawEnd = view.RawStart(end);
        view.AppendHidden(output, rawStart, rawText);
        output.Append(Mask);
        view.AppendHidden(output, rawText, rawEnd);
    }

    /// <summary>私钥块里的一行是什么:正文(要遮)、空行或头部行(原样),还是别的(说明私钥块并没有继续)。</summary>
    private enum KeyLineKind
    {
        Body,
        Kept,
        Other
    }

    /// <summary>
    /// 一整行(不含行尾)在私钥块里算哪种。正文:可选的行号 / 文件名前缀之后是至少 4 个 base64 字符(可带 <c>=</c> 填充),
    /// 或 OpenPGP 的校验行 <c>=XXXX</c>;前面的缩进不算前缀(连同正文一起遮,与没加这条判断时一样)。
    /// </summary>
    private static KeyLineKind ClassifyKeyLine(ReadOnlySpan<char> line, out int bodyStart)
    {
        bodyStart = 0;
        ReadOnlySpan<char> trimmed = line.Trim(" \t\r");
        if (trimmed.IsEmpty || KeyHeader().IsMatch(trimmed))
        {
            return KeyLineKind.Kept;
        }
        int prefix = KeyLinePrefix(line);
        if (KeyBodyLine().IsMatch(line[prefix..]))
        {
            bodyStart = prefix;
            return KeyLineKind.Body;
        }
        return KeyLineKind.Other;
    }

    /// <summary>行首的行号或文件名前缀(<c>cat -n</c> 的「  2\t」、<c>grep -n</c> 的「12:」、<c>grep -A</c> 的「./id_rsa-」)有多长;没有为 0。</summary>
    private static int KeyLinePrefix(ReadOnlySpan<char> line)
    {
        foreach (ValueMatch match in KeyLineNumber().EnumerateMatches(line))
        {
            return match.Length;
        }
        return 0;
    }

    /// <summary>
    /// 还没写完的一行有没有可能是私钥块里的行:以已知的头部名起头,或者只含 base64、行号与文件名前缀、END 标记里会有的字符。
    /// 提示符(<c>ops@web-01:~$</c>、<c>PS C:\&gt;</c>)里的 <c>@ ~ $ # &gt; % \ [ ]</c> 一出现就不是。
    /// </summary>
    private static bool MayBeKeyLine(string visible)
    {
        ReadOnlySpan<char> text = visible.AsSpan().TrimStart(" \t");
        return KeyHeader().IsMatch(text) || text.IndexOfAnyExcept(KeyLineChars) < 0;
    }

    /// <summary>私钥块里的行(正文、行号、文件名前缀、END 标记)会用到的字符。</summary>
    private static readonly SearchValues<char> KeyLineChars =
        SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/=:-._ \t\r");

    /// <summary>
    /// 一行里要遮的片段(原文位置),按起点排好、重叠的并成一段。只要结尾落在 <paramref name="from" /> 之后的(之前的已经处理过了);
    /// 行还没完时伸到末尾的值记为未完(后面可能还有)。顺带记下 <paramref name="from" /> 之后才完整的私钥块开头与结尾,
    /// 以及每个匹配从名字到值的整个范围(截上下文时用)。规则全跑在可见文本上,位置换算回原文。
    /// </summary>
    private List<Secret> FindSecrets(Projection view, int from, bool complete, List<(int End, bool Begin)> markers, List<(int Start, int End)> matches)
    {
        var found = new List<Secret>();
        string text = view.Visible;
        int origin = view.Origin;
        for (Match match = AuthorizationScheme().Match(text, origin); match.Success; match = match.NextMatch())
        {
            matches.Add(view.RawRange(match.Index, match.Index + match.Length));
            Group value = match.Groups["value"];
            bool reachesEnd = value.Index + value.Length == text.Length;
            if (!(reachesEnd && !complete) && !LooksLikeCredential(match.Groups["scheme"].ValueSpan, value.ValueSpan))
            {
                // Basic configuration、Bearer authentication 这类句子:不是凭据(还没写完的先扣着,写完了再判)
                continue;
            }
            Add(found, view, from, complete, value.Index, value.Index + value.Length, ValueKind.Credential, string.Empty, closed: false);
        }
        // 名字=值从上下文的第一个可见字符扫起,而且看不见截断点前的那个字符(只给 Bearer 与令牌看):
        // 截断点落在一串标识符中间时,后半截照样当名字认(值是同一个)。上下文从行首起时多认 INI 的 名字 = 值(要看得见行首)
        Regex keyValue = _contextAtLineStart ? KeyValueAtLineStart() : KeyValue();
        for (Match match = keyValue.Match(text, origin, text.Length - origin); match.Success; match = match.NextMatch())
        {
            matches.Add(view.RawRange(match.Index, match.Index + match.Length));
            Group value = match.Groups["value"];
            if (match.Groups["rest"].Success)
            {
                AddLineValue(found, view, from, complete, value.Index, value.Index + value.Length);
                continue;
            }
            char first = text[value.Index];
            string quote = first == '\\' && value.Length > 1 && text[value.Index + 1] == '"' ? "\\\""
                : first == '"' ? "\""
                : first == '\'' ? "'"
                : string.Empty;
            ValueKind kind = quote.Length == 0 ? ValueKind.Plain : quote == "'" ? ValueKind.SingleQuoted : ValueKind.DoubleQuoted;
            bool open = match.Groups["open"].Success;
            int valueEnd = value.Index + value.Length;
            if ((open && valueEnd < text.Length) || match.Groups["bare"].Success)
            {
                // 到行尾(\r 或 \n)都没收尾的引号串:引号之后遮到行尾,不跨行。转义过的 JSON 串被外层的裸引号截断
                // (截短的日志把里层的 JSON 切在半截):遮到那个裸引号之前 —— 它是外层串的收尾,不是值的一部分。
                // 两种都是开头的引号照放,也不凭空补一个收尾的引号,与扣到上限、收尾时的样子(引号 + ***)一致
                Add(found, view, from, complete, value.Index + quote.Length, valueEnd, kind, string.Empty, closed: true);
                continue;
            }
            if (quote.Length > 0 && !open && value.Length == 2 * quote.Length)
            {
                // 空串(JSON 里转义过的 \"\"):没有可遮的
                continue;
            }
            // 收了尾的引号串不会再长;没收尾的(open 那一支)与不加引号的,伸到末尾就是还没完
            Add(found, view, from, complete, value.Index, valueEnd, kind, quote, closed: quote.Length > 0 && !open);
        }
        for (Match match = PrefixedToken().Match(text, origin); match.Success; match = match.NextMatch())
        {
            matches.Add(view.RawRange(match.Index, match.Index + match.Length));
            Add(found, view, from, complete, match.Index, match.Index + match.Length, ValueKind.Token, string.Empty, closed: false);
        }
        for (Match match = NetrcEntry().Match(text, origin); match.Success; match = match.NextMatch())
        {
            matches.Add(view.RawRange(match.Index, match.Index + match.Length));
            Group value = match.Groups["value"];
            Add(found, view, from, complete, value.Index, value.Index + value.Length, ValueKind.Word, string.Empty, closed: false);
        }
        if (_contextAtLineStart && NetrcLine().Match(text) is { Success: true } netrc)
        {
            // 口令后面只能是空白与行尾:行还没完时停在口令或它后面的空白上,都要扣着等行尾(后面再来一个词就不算了)
            matches.Add(view.RawRange(netrc.Index, netrc.Index + netrc.Length));
            Group value = netrc.Groups["value"];
            Add(found, view, from, complete, value.Index, value.Index + value.Length, ValueKind.Word, string.Empty, closed: false,
                openAt: netrc.Index + netrc.Length);
        }

        // 私钥正文:BEGIN 之后到同一行的 END 为止,没有 END 就到行尾。开头与结尾按可见文本认(之前处理过的尾巴接上新到的一段,
        // 位置换算回可见文本、再换回原文);已经处理过的那部分里的 BEGIN 记在 _inKeyBody 上
        int visibleFrom = view.VisibleIndexAt(from);
        string keyText = _keyTail + text[visibleFrom..];
        int shift = visibleFrom - _keyTail.Length;
        bool inBody = _inKeyBody;
        int bodyStart = visibleFrom;
        for (Match match = PrivateKeyMarker().Match(keyText); match.Success; match = match.NextMatch())
        {
            int end = match.Index + match.Length + shift;
            if (end <= visibleFrom)
            {
                continue;
            }
            bool begin = match.Groups["begin"].Success;
            markers.Add((view.RawEnd(end), begin));
            if (begin && !inBody)
            {
                inBody = true;
                bodyStart = end;
            }
            else if (!begin && inBody)
            {
                AddKeyBody(found, view, from, complete, bodyStart, match.Index + shift);
                inBody = false;
            }
        }
        if (inBody)
        {
            int contentEnd = complete ? text.Length - (text.EndsWith("\r\n", StringComparison.Ordinal) ? 2 : 1) : text.Length;
            AddKeyBody(found, view, from, complete, bodyStart, contentEnd);
        }
        return Merge(found);
    }

    /// <summary>
    /// <c>Basic</c> / <c>Bearer</c> 后面那个词像不像凭据。Basic 的凭据是 <c>用户:口令</c> 的 base64:长度是 4 的倍数、解得开、解出来带冒号 ——
    /// <c>Basic configuration</c>、<c>Basic settings</c> 都过不了。Bearer 的令牌是不透明的,只排除纯字母、又不够长的英文单词
    /// (<c>Bearer authentication</c>):带数字或 <c>. _ ~ + / - =</c>,或者有 20 个字符以上,就算。
    /// </summary>
    private static bool LooksLikeCredential(ReadOnlySpan<char> scheme, ReadOnlySpan<char> value)
    {
        if (scheme.Equals("basic", StringComparison.OrdinalIgnoreCase))
        {
            if (value.Length > 1024)
            {
                // 解码要一块缓冲:这么长的「词」不会是句子里的英文,当凭据遮
                return true;
            }
            if (value.Length < 4 || value.Length % 4 != 0)
            {
                return false;
            }
            Span<byte> decoded = stackalloc byte[value.Length / 4 * 3];
            return Convert.TryFromBase64Chars(value, decoded, out int written) && decoded[..written].Contains((byte)':');
        }
        return value.Length >= 20 || value.ContainsAnyExcept(Letters);
    }

    /// <summary>ASCII 字母。</summary>
    private static readonly SearchValues<char> Letters =
        SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz");

    /// <summary>
    /// 记下一处要遮的片段:<paramref name="start" /> / <paramref name="end" /> 是可见文本里的位置,存的是换算回原文的位置,
    /// 结尾落在 <paramref name="from" />(原文)之后才算。<paramref name="openAt" /> 是「后面可能还有」的判据落在哪:
    /// 默认是值的结尾(伸到可见文本的末尾就是没完);值后面只能跟空白与行尾的(<see cref="NetrcLine" />、<see cref="ValueKind.LineValue" />)
    /// 传那段空白的结尾。
    /// </summary>
    private static void Add(List<Secret> found, Projection view, int from, bool complete, int start, int end, ValueKind kind, string quote, bool closed,
        int openAt = -1)
    {
        if (end <= start)
        {
            return;
        }
        int rawEnd = view.RawEnd(end);
        if (rawEnd <= from)
        {
            return;
        }
        bool open = !complete && !closed && (openAt < 0 ? end : openAt) == view.Visible.Length;
        int contentStart = start + quote.Length < end ? view.RawStart(start + quote.Length) : rawEnd;
        found.Add(new Secret(view.RawStart(start), rawEnd, kind, quote, Open: open, ContentStart: contentStart));
    }

    /// <summary>
    /// <c>名字: 值</c> 与 <c>名字 = 值</c>(INI)里到行尾的值(<see cref="ValueKind.LineValue" />):尾随的空白不遮;停在下一个键前面的,紧挨着的逗号、分号留给那个键。
    /// 值后面到已收到内容的末尾都是空白时还不算完 —— 下一段可能接着是值的下一个词,也可能是下一个键。
    /// </summary>
    private static void AddLineValue(List<Secret> found, Projection view, int from, bool complete, int start, int end)
    {
        string text = view.Visible;
        int after = text.AsSpan(end).IndexOfAnyExcept(' ', '\t');
        after = after < 0 ? text.Length : end + after;
        bool beforeKey = after > end && after < text.Length && text[after] is >= ' ' and not '\u007f' and not '#';
        if (beforeKey && end - 1 > start && text[end - 1] is ',' or ';')
        {
            end--;
        }
        Add(found, view, from, complete, start, end, ValueKind.LineValue, string.Empty, closed: false, openAt: after);
    }

    /// <summary>
    /// 私钥正文,从 BEGIN 之后第一个非空白字符算起;只有空白的不算(BEGIN 那一行通常就是这样)。
    /// 开头的空白不遮:切开写入时它们先到、已经放出去了,一次写入也得是同一个结果。
    /// </summary>
    private static void AddKeyBody(List<Secret> found, Projection view, int from, bool complete, int start, int end)
    {
        int text = end > start ? view.Visible.AsSpan(start, end - start).IndexOfAnyExcept(' ', '\t', '\r') : -1;
        if (text >= 0)
        {
            Add(found, view, from, complete, start + text, end, ValueKind.KeyBody, string.Empty, closed: false);
        }
    }

    /// <summary>
    /// 按起点排好;重叠的并成一段:套在里面的归外面那段遮,伸出去的按并集遮成 <c>***</c>。
    /// 结束条件(种类与正文起点)跟着决定结尾的那个值走:伸得最远的,或者套在一个已收尾的值里、自己却还没完的那个
    /// (<c>"…-----BEGIN … KEY-----…"</c> 停在收尾引号上时,引号收了尾,私钥正文却可能还长)。
    /// 扣到上限时从它的正文吞起,吞的过程里就不会碰到它自己的结束条件。
    /// </summary>
    private static List<Secret> Merge(List<Secret> found)
    {
        if (found.Count < 2)
        {
            return found;
        }
        found.Sort(static (a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : b.End.CompareTo(a.End));
        var merged = new List<Secret>(found.Count) { found[0] };
        for (int i = 1; i < found.Count; i++)
        {
            Secret next = found[i];
            Secret last = merged[^1];
            if (next.Start >= last.End)
            {
                merged.Add(next);
            }
            else
            {
                merged[^1] = next.End > last.End || (next.Open && !last.Open)
                    ? next with { Start = last.Start, End = Math.Max(last.End, next.End), Quote = string.Empty }
                    : last;
            }
        }
        return merged;
    }

    /// <summary>
    /// 吞掉的那部分也算当前行处理过了:它不进上下文(值吞完时上下文从头起),里面的控制序列照放(回放时颜色、光标状态不乱),
    /// 可见文本接进认私钥块用的尾巴,里面的私钥块开头与结尾照记。
    /// </summary>
    private void Consume(string text, StringBuilder output)
    {
        if (text.Length == 0)
        {
            return;
        }
        var view = Projection.Of(text, null);
        view.AppendHidden(output, 0, text.Length);
        string keyText = _keyTail + view.Visible;
        if (!_inPrivateKey)
        {
            for (Match match = PrivateKeyMarker().Match(keyText); match.Success; match = match.NextMatch())
            {
                if (match.Index + match.Length > _keyTail.Length)
                {
                    _inKeyBody = match.Groups["begin"].Success;
                }
            }
        }
        _keyTail = KeyTail(keyText);
    }

    /// <summary>
    /// 正在吞的值在 <paramref name="text" /> 里到哪为止(原文位置,结束它的那个字符不吞);-1 表示还没完,
    /// 这时末尾 <paramref name="keep" /> 个字节先留着,下一段来了拼起来再看(切在半截的转义、被切开的 END、没写完的控制序列)。
    /// 结束条件与匹配时一样按可见文本判断:值中间夹着的颜色序列不会让它提前结束。
    /// </summary>
    private int SwallowEnd(string text, out int keep)
    {
        var view = Projection.Of(text, null);
        string visible = view.Visible;
        int end = -1;
        int visibleKeep = 0;
        switch (_swallowing)
        {
            case ValueKind.Plain:
                end = visible.AsSpan().IndexOfAny(PlainValueEnds);
                break;
            case ValueKind.Word:
                end = visible.AsSpan().IndexOfAny(WordEnds);
                break;
            case ValueKind.LineValue:
                end = LineValueEnd(visible, out visibleKeep);
                break;
            case ValueKind.Credential:
                end = visible.AsSpan().IndexOfAnyExcept(CredentialChars);
                break;
            case ValueKind.Token:
                end = visible.AsSpan().IndexOfAnyExcept(TokenChars);
                break;
            case ValueKind.SingleQuoted:
                end = visible.AsSpan().IndexOfAny('\'', '\r', '\n');
                break;
            case ValueKind.DoubleQuoted:
                end = DoubleQuotedEnd(visible, out visibleKeep);
                break;
            default:
                // 私钥正文:到 END 或行尾(\r\n / \n)为止;行中间的 \r 照吞
                int newline = visible.IndexOf('\n');
                int lineEnd = newline > 0 && visible[newline - 1] == '\r' ? newline - 1 : newline;
                if (FirstKeyEnd(visible, lineEnd < 0 ? visible.Length : lineEnd) is { } marker)
                {
                    end = marker.Index;
                }
                else if (lineEnd >= 0)
                {
                    end = lineEnd;
                }
                else
                {
                    visibleKeep = Math.Min(MarkerTailBytes, visible.Length);
                }
                break;
        }
        if (end >= 0)
        {
            keep = 0;
            return view.RawStart(end);
        }
        keep = Math.Max(view.KeepFor(visibleKeep), view.PartialLength);
        return -1;
    }

    /// <summary>双引号串吞到哪:不带转义的 <c>"</c> 或行尾;切在转义中间的反斜杠留着等下一段。</summary>
    private static int DoubleQuotedEnd(string text, out int keep)
    {
        keep = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c is '"' or '\r' or '\n')
            {
                return i;
            }
            if (c == '\\')
            {
                if (i + 1 == text.Length)
                {
                    keep = 1;
                    return -1;
                }
                if (text[i + 1] is not ('\r' or '\n'))
                {
                    i++;
                }
            }
        }
        return -1;
    }

    /// <summary>
    /// 正在吞的 <see cref="ValueKind.LineValue" /> 到哪为止,与 <see cref="KeyValue" /> 的 <c>rest</c> 一支同一个判据:
    /// 行尾与控制字符;一段空白之后跟着 YAML 注释的 <c>#</c>,或者跟着下一个键(标识符紧跟 <c>=</c>,或紧跟 <c>:</c> 而冒号后是空白)——
    /// 停在那段空白前面(紧挨着的逗号、分号留给那个键)。空白之后还看不出是不是键就到了末尾的,从那段空白起留着等下一段;
    /// 要看的超过 <see cref="MaxKeyProbeBytes" /> 就不等了,当成值吞掉。
    /// </summary>
    private static int LineValueEnd(string text, out int keep)
    {
        keep = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c is not (' ' or '\t'))
            {
                if (c is < ' ' or '\u007f')
                {
                    return i;
                }
                continue;
            }
            int blanks = i;
            while (i < text.Length && text[i] is ' ' or '\t')
            {
                i++;
            }
            if (i < text.Length && text[i] == '#')
            {
                return blanks;
            }
            switch (NextKeyAt(text, i))
            {
                case true:
                    return blanks > 0 && text[blanks - 1] is ',' or ';' ? blanks - 1 : blanks;
                case null when text.Length - blanks <= MaxKeyProbeBytes:
                    keep = text.Length - blanks;
                    return -1;
            }
            // 不是键(或者看不出来、而要看的太长了):空白与后面的词都算值的一部分
            i--;
        }
        return -1;
    }

    /// <summary>
    /// <paramref name="text" /> 的 <paramref name="at" /> 处是不是一个键(可带引号的标识符,紧跟 <c>=</c>,或紧跟 <c>:</c> 而冒号后是空白);
    /// null 表示到了末尾还看不出来。与 <see cref="KeyValue" /> 里 <c>rest</c> 一支的停止条件同一个判据。
    /// </summary>
    private static bool? NextKeyAt(string text, int at)
    {
        if (at >= text.Length)
        {
            return null;
        }
        int i = at;
        if (text[i] is '"' or '\'')
        {
            i++;
        }
        if (i >= text.Length)
        {
            return null;
        }
        if (!char.IsAsciiLetter(text[i]) && text[i] != '_')
        {
            return false;
        }
        while (i < text.Length && (char.IsAsciiLetterOrDigit(text[i]) || text[i] is '_' or '.' or '-'))
        {
            i++;
        }
        if (i < text.Length && text[i] is '"' or '\'')
        {
            i++;
        }
        while (i < text.Length && text[i] is ' ' or '\t')
        {
            i++;
        }
        if (i >= text.Length)
        {
            return null;
        }
        if (text[i] == '=')
        {
            return true;
        }
        if (text[i] != ':')
        {
            return false;
        }
        return i + 1 >= text.Length ? null : text[i + 1] is ' ' or '\t' or '\r' or '\n';
    }

    /// <summary><paramref name="text" /> 前 <paramref name="limit" /> 个字符里的第一个 <c>-----END … PRIVATE KEY-----</c>。</summary>
    private static Match? FirstKeyEnd(string text, int limit)
    {
        for (Match match = PrivateKeyMarker().Match(text, 0, limit); match.Success; match = match.NextMatch())
        {
            if (!match.Groups["begin"].Success)
            {
                return match;
            }
        }
        return null;
    }

    /// <summary>认私钥块用的可见文本只留最后 <see cref="MarkerTailBytes" /> 个字符。</summary>
    private static string KeyTail(string text) => text.Length <= MarkerTailBytes ? text : text[^MarkerTailBytes..];

    /// <summary>
    /// 名字里一个词在这里结束(<see cref="KeyValue" /> 用,那条正则不区分大小写,这一段区分):紧跟在后面的不是同样大小写的字母 ——
    /// <c>token_</c>、<c>tokenId</c>、<c>TOKEN_</c>、名字结尾都算,<c>tokens</c>、<c>tokenizer</c>、<c>TOKENS</c> 不算。
    /// </summary>
    private const string WordEnd = "(?-i:(?<=[a-z])(?![a-z])|(?<=[A-Z])(?![A-Za-z]))";

    /// <summary>
    /// <see cref="KeyValue" /> 的 <c>rest</c> 一支:不加引号、可以带空格的值,一直到行尾 —— 停在控制字符、YAML 注释的 <c>#</c>,
    /// 或者一段空白之后紧跟着的下一个键(标识符可带引号,紧跟 <c>=</c>,或紧跟 <c>:</c> 而冒号后面是空白或行尾)之前。
    /// 与 <see cref="LineValueEnd" />(扣满上限之后吞剩下的部分)同一个判据。按「词 + 空白」一段段吃,词与空白都是原子的,线性。
    /// </summary>
    private const string LineValueRest =
        """(?!\\?["'])(?>[^ \t\r\n\x00-\x1f\x7f]+)(?:(?>[ \t]+)(?!\#|["']?[A-Za-z_](?>[A-Za-z0-9_.\-]*)["']?[ \t]*(?::(?=[ \t\r\n])|=))(?>[^ \t\r\n\x00-\x1f\x7f]+))*""";

    /// <summary>
    /// token / secret 后面紧跟着这些词之一(分隔符 <c>_ - .</c> 之后,或驼峰的下一个词;可带复数 <c>s</c>,要成词)的,这一处不算关键词:
    /// 名字说的是令牌或机密的某个属性,值不是它本身。只收公认不装机密的:
    /// <list type="bullet">
    /// <item><c>type</c>:OAuth 2.0 的 <c>token_type</c>(<c>Bearer</c> / <c>mac</c>,RFC 6749 §5.1)、令牌交换的 <c>subject_token_type</c> 一类(URN,RFC 8693)、
    /// 吊销的 <c>token_type_hint</c>(RFC 7009),值是枚举出来的类型名;</item>
    /// <item><c>endpoint</c>:OIDC 发现文档的 <c>token_endpoint</c>(URL)与 <c>token_endpoint_auth_methods_supported</c>(认证方式名的列表);</item>
    /// <item><c>name</c>:Kubernetes 卷与 Ingress TLS 的 <c>secretName</c>、Terraform / 云 CLI 的 <c>secret_name</c>、<c>token_name</c>,值是引用机密的名字;</item>
    /// <item><c>count</c> / <c>limit</c>:大模型接口与配置里的 <c>token_count</c>、<c>token_limit</c>,值是数;</item>
    /// <item><c>url</c> / <c>uri</c>:令牌服务的地址 —— Google 服务账号 JSON 的 <c>token_uri</c>、Spring Security 的 <c>token-uri</c>、OAuth 客户端库的 <c>token_url</c>;</item>
    /// <item><c>expires</c> / <c>expiry</c> / <c>expiration</c> / <c>ttl</c>:期限与时长 —— <c>refresh_token_expires_in</c>(GitHub)、
    /// <c>client_secret_expires_at</c>(RFC 7591)、Google oauth2client 凭据文件的 <c>token_expiry</c>、Vault 的 <c>token_ttl</c>。</item>
    /// </list>
    /// 故意不收的:<c>id</c>(Vault AppRole 的 <c>secret_id</c> 本身就是机密)、<c>key</c>(<c>secret_key</c>)、<c>value</c> / <c>data</c> / <c>string</c>
    /// (<c>secretValue</c>、<c>SecretString</c>)、<c>hash</c>(低熵口令的散列能离线猜)、<c>file</c> / <c>path</c>(值多半是路径,
    /// 但只看关键词后面的第一个词,<c>token_file_content</c> 这样的名字就会漏)、<c>signing</c> / <c>encryption</c>(<c>token_signing_key</c>)。
    /// 只给 token 与 secret:password、credential、api key 这几个词后面跟什么都照遮(维护者 2026-10-25 定的范围)。
    /// </summary>
    private const string NonSecretSuffix =
        "(?:[_.\\-]|(?-i:(?=[A-Z])))(?:type|endpoint|name|count|limit|url|uri|expires|expiry|expiration|ttl)s?" + WordEnd;

    /// <summary>
    /// <c>名字=值</c> / <c>名字: 值</c>:名字里带口令一类的词,值是引号串(双引号认反斜杠转义,含 JSON 里转义过的 <c>\"…\"</c>)
    /// 或到空白、引号、逗号、分号、控制字符为止的一段。跑在可见文本上:颜色序列已经剥掉,名字、分隔符、值上不上色都一样。
    /// 值为空的(<c>Password: </c> 提示)不动。
    /// 没收尾的引号串走 <c>open</c> 那一支:停在已收到内容的末尾的,调用方把它当成没完的值扣下;
    /// 停在行尾(<c>\r</c> 或 <c>\n</c>)的,从引号之后遮到行尾 —— 不然扣着的值等到换行就原文放了出去。
    /// 转义过的 JSON 串(<c>\"…\"</c>)遇到裸引号就停(<c>bare</c> 组):在一个外层的 JSON 串里,裸引号只能是外层串的收尾
    /// (截短的日志把里层的 JSON 切在半截,<c>"msg": "{\"password\":\"abc def", …</c>),不然值只遮到反斜杠、正文漏出去。
    /// 只在没先遇到 <c>\"</c> 时才走到裸引号,所以 <c>\"password\":\"\",\"token\":\"x\"</c> 这样的空值不会把下一个键吞进去。
    /// <para>
    /// 不加引号的值:<c>名字=值</c> 到空白、引号、逗号、分号或控制字符为止(<c>.env</c>、shell、<c>k=v k=v</c> 的日志里空格就是值的边界);
    /// <c>名字: 值</c>(冒号后面跟着空白,YAML、HTTP 头、<c>kubectl describe</c> 的写法)走 <c>rest</c> 那一支,值里可以有空格,
    /// 一直到行尾(<c>passphrase: correct horse battery staple</c>)。同一行后面还有别的键时不能把它吃掉,判据是:
    /// 空白之后紧跟着一个「键」—— 标识符(可带引号)紧跟 <c>=</c>,或紧跟 <c>:</c> 而冒号后面是空白或行尾 —— 或者 YAML 注释的 <c>#</c>,
    /// 值就停在那段空白之前(紧挨着的逗号、分号留给下一个键)。<c>10:30</c>、<c>C:\x</c>、<c>http://</c> 不算键:
    /// 前者不是标识符开头,后两者冒号后面不是空白。冒号后面没有空白的(<c>host:~$</c> 这类提示符)仍按到空白为止。
    /// INI / conf 的写法 —— 名字在行首、<c>=</c> 两边都有空白(<c>password = my pass phrase</c>)—— 同样遮到行尾,
    /// 只在上下文从行首起时由 <see cref="KeyValueAtLineStart" /> 认(理由与判据见那里);别处的 <c>名字 = 值</c> 照旧到空白为止。
    /// </para>
    /// <para>
    /// 名字也认几个不带口令字样、但值必是机密的键:<c>tls.key</c> 一类以 <c>.key</c> 结尾的(Kubernetes TLS Secret 的私钥)、
    /// <c>client-key-data</c> 一类 <c>…key-data</c>(kubeconfig)、<c>.dockerconfigjson</c> / <c>.dockercfg</c>(镜像仓库凭据);
    /// 以及 Docker <c>config.json</c> 里的 <c>"auth"</c>(base64 的 <c>用户:口令</c>)—— 这个词太常见,只认带双引号的名字、
    /// 值是引号串的那种(<c>exact</c> 组只配引号串的几支),<c>auth: true</c>、<c>"auth": {…}</c> 不动 —— 也不能先配上再丢弃:<c>{"password": …}</c> 会被这个配不上的值整段跳过。
    /// 名字紧跟在 <c>@</c> 后面的不算:那是 <c>用户@主机</c> 里的主机名(<c>ops@vault-secrets-01:~$</c>)。
    /// </para>
    /// <para>
    /// 名字里的关键词要是一个完整的词(收窄误遮):紧跟在后面的若是同一种大小写的字母(<c>max_tokens</c>、<c>tokenizers</c>、
    /// <c>passwordless</c>、<c>SECRETARY</c>),就不是这个词;驼峰的下一个词(<c>secretKey</c>、<c>tokenId</c>)、数字、分隔符、名字结尾都算边界。
    /// 复数的 <c>s</c> 除 token 以外都放过 —— <c>credentials.json</c>、<c>client_secrets.json</c> 是 Kubernetes Secret 里常见的机密文件名,
    /// 而 <c>max_tokens</c> 一类才是最常见的误遮。这条判断写在原子组里:第一个关键词不成词时接着找同一个名字里的下一个
    /// (<c>tokens_secret</c> 照样认),整个名字都不成立时这个位置就不算匹配 —— 不能先配上再丢弃,那样值里的真机密会被一起跳过。
    /// 冒号后面没有空白、值以 <c>~</c> 或 <c>/</c> 起头的也不算(<c>secret-box:~#</c> 这类没有 <c>用户@</c> 的提示符、<c>host:/path</c>)。
    /// token 与 secret 后面紧跟着「非机密后缀」(<see cref="NonSecretSuffix" />:<c>token_type</c>、<c>token_endpoint</c>、<c>secretName</c>、<c>token_count</c>…)的,
    /// 这一处也不算关键词 —— 名字说的是令牌的类型、地址、名字、数量、期限,值不是令牌本身。同样写在原子组里,接着找同一个名字里的下一个关键词
    /// (<c>secret_name_token</c> 照遮)。
    /// </para>
    /// </summary>
    /// <remarks>
    /// 名字从一串标识符字符的开头算起,先找到第一个关键词、再把整串吃完,两步都是原子的,值的各支也互不重叠 —— 整条是线性的。
    /// 旧写法在关键词前后各挂一个可回溯的 <c>[A-Za-z0-9_.\-]*</c>,4 KB 的 <c>tokentoken…</c> 就能把它拖上半分钟。
    /// 关键词后面看一眼后缀表也只看十几个字符,不改变线性。
    /// 转义过的 JSON 串里,<c>\\\"</c> 与 <c>\\\\</c> 是里层的转义,不算收尾。
    /// <c>rest</c> 一支按「词 + 空白」一段段吃,每段空白只在后面那个词的开头看一眼是不是键,词与空白都是原子的,也是线性的。
    /// 这一条在上下文从行首起时换成 <see cref="KeyValueAtLineStart" />(多认一种 INI 写法),其余完全相同。
    /// </remarks>
    [GeneratedRegex(KeyValueHead + PlainValue + ")))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex KeyValue();

    /// <summary>
    /// 上下文从行首起时用的 <see cref="KeyValue" />:多认 INI / conf 的写法 —— 名字在行首(缩进之后,或者 <c>grep -n</c> / <c>grep -r</c> /
    /// <c>cat -n</c> 加的文件名与行号之后),<c>=</c> 两边都有空白(<c>password = my pass phrase</c>),不加引号的值像 <c>名字: 值</c> 一样遮到行尾
    /// (同一个判据:停在下一个键或 YAML 注释的 <c>#</c> 之前,见 <see cref="LineValueRest" />)。INI 的值本来就是等号之后的一整行。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 只认行首,是为了不碰命令行与代码:<c>[ $password = $expected ] &amp;&amp; …</c>、<c>grep password = …</c>、
    /// <c>$Password = Read-Host …</c>、SQL 的 <c>WHERE password = $1 AND …</c> 里等号两边同样有空白,值却只是一个词,后面是别的东西 ——
    /// 这些照旧只遮等号后面的第一个词。shell 的赋值不许等号两边有空格,<c>.env</c> 与 <c>k=v k=v</c> 日志里的 <c>名字=值</c> 不受影响;
    /// 只有一边有空白的(<c>password= x</c>、<c>password =x</c>)、等号后面紧跟等号的(<c>password == x</c>)也照旧。
    /// 上下文截断过、或在行中间重新起时(<see cref="_contextAtLineStart" /> 为假)看不见行首,用 <see cref="KeyValue" />,
    /// 与一次写进来的结果一致(那时名字本来就不在行首)。
    /// </para>
    /// <para>
    /// 行首的判断写在等号那一支的条件里(<see cref="IniLineStart" />,逆向的后顾):只在名字已经配上、等号两边都有空白时才看,
    /// 名字在后顾里是原子的,前缀的几支各自至多扫到前一个空白 —— 不改变线性。
    /// </para>
    /// </remarks>
    [GeneratedRegex(KeyValueHead + "(?(?<=" + IniLineStart + ")(?<rest>" + LineValueRest + ")|" + PlainValue + ")" + ")))",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex KeyValueAtLineStart();

    /// <summary><see cref="KeyValue" /> 与 <see cref="KeyValueAtLineStart" /> 共用的部分:名字、分隔符、值的各支,直到 <c>名字=值</c> 那一支不加引号的值之前。</summary>
    private const string KeyValueHead =
        """(?:(?<![A-Za-z0-9_.\-@])(?>[A-Za-z0-9_.\-]*?(?:(?:password|passwd|passphrase|api[_\-]?key|access[_\-]?key|private[_\-]?key|credential|key[_\-]data|\.key|dockerconfigjson|dockercfg)s?"""
        + WordEnd + "|(?:secrets?|token)" + WordEnd + "(?!" + NonSecretSuffix + "))" + """)(?>[A-Za-z0-9_.\-]*)|(?<exact>(?<=")auth(?=\\?")))(?:\\?["'])?(?>[ \t]*)(?:(?<colon>:)|=)(?>(?<ws>[ \t]+)?)(?<value>"(?:[^"\\\r\n]|\\[^\r\n])+"|'[^'\r\n]+'|\\"(?:[^"\\\r\n]|\\[^"\\\r\n]|\\\\(?:[^"\\\r\n]|\\[^\r\n]))*(?:\\"|(?<bare>(?=")))|(?<open>"(?:[^"\\\r\n]|\\[^\r\n])*\\?|'[^'\r\n]*|\\"(?:[^"\\\r\n]|\\[^"\\\r\n]|\\\\(?:[^"\\\r\n]|\\[^\r\n]))*\\{0,3})(?:\z|(?=[\r\n]))|(?(exact)(?!)|(?(colon)(?(ws)(?<rest>"""
        + LineValueRest + """)|(?![~/])[^ "',;\x00-\x1f\x7f]+)|""";

    /// <summary><c>名字=值</c> 不加引号的值:到空白、引号、逗号、分号、控制字符为止(与 <see cref="PlainValueEnds" /> 同一组字符)。</summary>
    private const string PlainValue = """[^ "',;\x00-\x1f\x7f]+""";

    /// <summary>
    /// INI 写法的行首(用在值开头处的后顾里):<c>名字 = </c> 前面只有缩进,或者 <c>grep -n</c> 的 <c>文件:行号:</c> / <c>行号:</c>、
    /// 上下文行的 <c>行号-</c>、<c>grep -r</c> 的 <c>文件:</c>、<c>cat -n</c> 的 <c>行号 + 制表符</c>(与 <see cref="KeyLineNumber" /> 同一套前缀)。
    /// </summary>
    private const string IniLineStart =
        """\A[ \t]*(?:\d+[ \t]+|(?:[^ \t:]+[:\-])?\d+[:\-]|[^ \t:]+[:\-])?(?>[A-Za-z0-9_.\-]+)[ \t]+=[ \t]+""";

    /// <summary>
    /// <c>.netrc</c> 写在一行里的条目:<c>machine 主机 login 用户 password 口令</c>(或以 <c>default</c> 起头),
    /// 值到空白为止。关键词按 netrc 的写法只认小写;<c>password</c> 前面必须有 <c>machine</c> / <c>default</c> 起头、中间至少一个
    /// <c>login</c> / <c>account</c> / <c>port</c> —— 光凭「password 后跟一个词」会把 <c>[sudo] password for ops:</c> 里的 <c>for</c> 遮掉,
    /// 不要求 login 的话 <c>the machine room password policy</c> 这种句子也会中。
    /// </summary>
    /// <remarks>各段互不重叠、中间那串 login / account / port 是原子的,线性。</remarks>
    [GeneratedRegex(@"(?<![A-Za-z0-9_])(?:machine[ \t]+[^ \t\r\n]+|default)(?>(?:[ \t]+(?:login|account|port)[ \t]+[^ \t\r\n]+)+)[ \t]+password[ \t]+(?<value>[^ \t\r\n\x00-\x1f\x7f]+)",
        RegexOptions.CultureInvariant)]
    private static partial Regex NetrcEntry();

    /// <summary>
    /// <c>.netrc</c> 分行写的条目里单独的一行 <c>password 口令</c>:只认行首(缩进之后)的小写 <c>password</c>、后面恰好一个词就到行尾。
    /// 后面还有别的词的(帮助文本里的 <c>password  Set the password</c>)不算;行还没完、停在这个词上或它后面的空白上时,调用方扣下等行尾。
    /// 只在上下文从行首起的时候用(见 <see cref="_contextAtLineStart" />)。
    /// 那个词恰好是一个 <c>=</c> 的不算:那是 INI 里空着的 <c>password = </c>(模板、示例配置),不是 netrc 的口令。
    /// </summary>
    [GeneratedRegex(@"\A[ \t]*password[ \t]+(?<value>(?!=(?:[ \t\r\n]|\z))[^ \t\r\n\x00-\x1f\x7f]+)[ \t]*(?:\r?\n|\r?\z)", RegexOptions.CultureInvariant)]
    private static partial Regex NetrcLine();

    /// <summary>
    /// <c>Bearer</c> 与带前缀的令牌从哪起头:前面不是 ASCII 字母、数字或下划线(<c>xBearer</c>、<c>my_ghp_…</c> 不算)。
    /// 跑在可见文本上:颜色序列已经剥掉,上了色的令牌(bat、grep 的高亮,带色的日志)看的是序列前面那个可见字符。
    /// </summary>
    private const string TokenStart = @"(?<![A-Za-z0-9_])";

    /// <summary><c>Bearer</c> / <c>Basic</c> 后面的凭据;已收到内容的末尾停在凭据里(哪怕还不够 8 个字符)也算,调用方据此扣下。</summary>
    /// <remarks>
    /// 本类的正则里空白一律只认 ASCII 的空格与制表符:按 Latin-1 映射后,UTF-8 的续字节 0x85、0xA0 会被 <c>\s</c> 当成空白,
    /// 值就会在一个汉字中间断开。
    /// </remarks>
    [GeneratedRegex(TokenStart + @"(?<scheme>bearer|basic)(?>[ \t]+)(?<value>[A-Za-z0-9._~+/\-]{8,}=*|[A-Za-z0-9._~+/\-]+=*\z)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AuthorizationScheme();

    /// <summary>
    /// 自带前缀、不必看上下文就认得出的令牌;已收到内容的末尾停在一个前缀已齐、还可能长成令牌的串里也算,调用方据此扣下。
    /// 前缀区分大小写,前缀之后的长度与字符集按各家的格式卡住(短了、字符不对的不算,收窄误报):
    /// <list type="bullet">
    /// <item>GitHub <c>gh[pousr]_</c> + 至少 30 个 <c>[A-Za-z0-9]</c>;细粒度令牌 <c>github_pat_</c> + 至少 22 个 <c>[A-Za-z0-9_]</c>(正文里有下划线);</item>
    /// <item>GitLab 个人访问令牌 <c>glpat-</c> + 至少 20 个 <c>[A-Za-z0-9_-]</c>(老格式正好 20 个);可路由的新格式在正文后面再跟 <c>.</c> + 9 个 <c>[0-9a-z]</c>
    /// (版本与校验码),一并遮掉 —— 已收到内容的末尾停在这段尾巴的半截(含只有一个 <c>.</c>)时不当成已经完了,扣着等下一段;</item>
    /// <item>Stripe 密钥 <c>sk_live_</c> 与受限密钥 <c>rk_live_</c> + 至少 24 个 <c>[A-Za-z0-9]</c>(老格式正好 24 个,新格式近百个);
    /// 可公开的 <c>pk_live_</c> 不认;</item>
    /// <item>Google API key <c>AIza</c> + 正好 35 个 <c>[A-Za-z0-9_-]</c>;</item>
    /// <item>npm 访问令牌 <c>npm_</c> + 正好 36 个 <c>[A-Za-z0-9]</c>(30 个随机字符 + 6 个校验字符)—— <c>npm_config_cache</c>、<c>npm_package_version</c>
    /// 这类 npm 脚本的环境变量里有下划线,不会中;</item>
    /// <item>PyPI 上传令牌 <c>pypi-AgE</c> + 至少 50 个 <c>[A-Za-z0-9_-]</c>:令牌是 base64url 编码的 macaroon,第二版二进制格式以版本 2、位置字段开头,
    /// 编码出来总是 <c>AgE</c>(pypi.org 的是 <c>AgEIcHlwaS5vcmc</c>,TestPyPI 的是 <c>AgENdGVzdC5weXBpLm9yZw</c>)—— <c>pypi-attestations</c> 一类包名不会中;</item>
    /// <item>AWS <c>AKIA</c> / <c>ASIA</c> + 正好 16 个 <c>[0-9A-Z]</c>;Slack <c>xox[abprs]-</c> + 至少 10 个 <c>[A-Za-z0-9-]</c>;OpenAI 式 <c>sk-</c> + 至少 20 个 <c>[A-Za-z0-9_-]</c>。</item>
    /// </list>
    /// 后面紧跟着字母、数字、下划线的不算(那是更长的一个词)。
    /// </summary>
    /// <remarks>
    /// 线性:每个令牌从一串字符的开头起(<see cref="TokenStart" />),各支要么原子地吃完整串(<c>{n,}</c> 的几支,串够长就一定配上),要么定长。
    /// 「还没写完」那一支对定长的令牌只认到定长为止(<c>AIza</c> 35 个、<c>npm_</c> 36 个):已经比定长还长的串不会再长成令牌,不扣,当场放出;
    /// 顺带每个起点要看的字符也有了上限(<c>AIza</c> 的字符集里有 <c>-</c>,<c>-</c> 后面又能起头)。
    /// </remarks>
    [GeneratedRegex(TokenStart + @"(?:(?:(?:AKIA|ASIA)[0-9A-Z]{16}|gh[pousr]_(?>[A-Za-z0-9]{30,})|github_pat_(?>[A-Za-z0-9_]{22,})"
        + @"|glpat-(?>[A-Za-z0-9_\-]{20,})(?:\.[0-9a-z]{9}|(?!\.[0-9a-z]{0,8}\z))|[rs]k_live_(?>[A-Za-z0-9]{24,})|AIza[A-Za-z0-9_\-]{35}|npm_[A-Za-z0-9]{36}"
        + @"|pypi-AgE(?>[A-Za-z0-9_\-]{50,})|xox[abprs]-[A-Za-z0-9\-]{10,}|sk-[A-Za-z0-9_\-]{20,})(?![A-Za-z0-9_])"
        + @"|(?:(?:AKIA|ASIA)[0-9A-Z]*|gh[pousr]_[A-Za-z0-9]*|github_pat_[A-Za-z0-9_]*|glpat-(?>[A-Za-z0-9_\-]*)(?:\.[0-9a-z]{0,9})?|[rs]k_live_[A-Za-z0-9]*"
        + @"|AIza[A-Za-z0-9_\-]{0,35}|npm_[A-Za-z0-9]{0,36}|pypi-(?:AgE[A-Za-z0-9_\-]*|Ag?)?|xox[abprs]-[A-Za-z0-9\-]*|sk-[A-Za-z0-9_\-]*)\z)",
        RegexOptions.CultureInvariant)]
    private static partial Regex PrefixedToken();

    /// <summary>私钥块里的正文行(行号 / 文件名前缀之后):至少 4 个 base64 字符,可带 <c>=</c> 填充;或 OpenPGP 的校验行 <c>=XXXX</c>。</summary>
    [GeneratedRegex(@"\A[ \t]*(?:[A-Za-z0-9+/]{4,}={0,2}|=[A-Za-z0-9+/]{4})[ \t\r]*\z", RegexOptions.CultureInvariant)]
    private static partial Regex KeyBodyLine();

    /// <summary>
    /// 私钥块里一行行首的行号或文件名前缀:<c>cat -n</c> / <c>nl</c> 的「空白 + 数字 + 空白」,<c>grep -n</c> 的「数字:」,
    /// 多个文件时 <c>grep</c> 的「文件名:」「文件名-」(上下文行用 -)及「文件名:数字:」。只有缩进的不算前缀。
    /// </summary>
    /// <remarks>base64 里没有 <c>:</c> 与 <c>-</c>,前缀不会吃进正文;只从行首配一次,至多回溯一遍,线性。</remarks>
    [GeneratedRegex(@"\A[ \t]*(?:\d+[ \t]+|(?:[^ \t:]+[:\-])?\d+[:\-]|[^ \t:]+[:\-])", RegexOptions.CultureInvariant)]
    private static partial Regex KeyLineNumber();

    /// <summary>PEM(RFC 1421)与 OpenPGP 铠装(RFC 4880)私钥块里的头部行。</summary>
    [GeneratedRegex(@"\A(?:Proc-Type|DEK-Info|Version|Comment|Hash|Charset|MessageID):", RegexOptions.CultureInvariant)]
    private static partial Regex KeyHeader();

    /// <summary>PEM 私钥块的开头(<c>begin</c> 组)或结尾;OpenPGP 的 <c>PGP PRIVATE KEY BLOCK</c> 也算。</summary>
    [GeneratedRegex(@"-----(?:(?<begin>BEGIN)|END) [A-Z0-9 ]*PRIVATE KEY(?: BLOCK)?-----", RegexOptions.CultureInvariant)]
    private static partial Regex PrivateKeyMarker();

    /// <summary>
    /// 一处要遮的片段 <c>[Start, End)</c>(原文位置);<see cref="Open" /> 表示它伸到了已收到内容的末尾,后面可能还有。
    /// <see cref="Quote" /> 是遮完要留着的引号,<see cref="ContentStart" /> 是决定结尾的那个值的正文从哪开始(开头的引号之后)。
    /// </summary>
    private readonly record struct Secret(int Start, int End, ValueKind Kind, string Quote, bool Open, int ContentStart);

    /// <summary>
    /// 一段原文的可见文本:CSI(<c>ESC [ … 终止字节</c>)与两三字节的 ESC 序列(<c>ESC ( B</c>、<c>ESC 7</c>…)不占位置;
    /// OSC / DCS / SOS / PM / APC 的开头(<c>ESC ]</c> 等)与 ST(<c>ESC \</c>)各换成一个 ESC 留在可见文本里作分界,
    /// 字符串的内容照样可见(终端标题里的命令行也过规则);坏的、超长的序列 ESC 原样可见;末尾没写完的序列不占位置、长度记在 <see cref="PartialLength" />。
    /// 其余字符(含 BEL 等别的控制字符,它们照旧是值的边界)原样。
    /// </summary>
    /// <remarks>
    /// <see cref="Origin" /> 为 1 时可见文本的第 0 个字符是上下文截断点前的那个可见字符(只给 <c>Bearer</c> 与令牌的「前面不是字母数字」看),
    /// 它不在这段原文里,位置记为 -1。位置表 <c>_map</c> 是递增的,原文 → 可见文本的换算用二分。
    /// </remarks>
    private sealed class Projection
    {
        private readonly int[] _map;
        private readonly List<(int Start, int End)> _hidden;

        private Projection(string raw, string visible, int[] map, int origin, List<(int Start, int End)> hidden, int partialLength)
        {
            Raw = raw;
            Visible = visible;
            _map = map;
            Origin = origin;
            _hidden = hidden;
            PartialLength = partialLength;
        }

        /// <summary>原文。</summary>
        public string Raw { get; }

        /// <summary>可见文本。</summary>
        public string Visible { get; }

        /// <summary>可见文本里属于这段原文的部分从哪起(0,或者 1:前面补了截断点前的那个字符)。</summary>
        public int Origin { get; }

        /// <summary>不占位置的控制序列在原文里的范围(含末尾没写完的那一段)。</summary>
        public IReadOnlyList<(int Start, int End)> Hidden => _hidden;

        /// <summary>原文末尾没写完的控制序列有多长(0 表示没有):下一段来了才知道它是什么,要留着拼。</summary>
        public int PartialLength { get; }

        /// <summary>投影一段原文;<paramref name="previous" /> 是补在前面、只给令牌开头看的那个字符。</summary>
        public static Projection Of(string raw, char? previous)
        {
            var visible = new StringBuilder(raw.Length + 1);
            int[] map = new int[raw.Length + 1];
            var hidden = new List<(int Start, int End)>();
            int count = 0;
            if (previous is { } prev)
            {
                visible.Append(prev);
                map[count++] = -1;
            }
            int origin = count;
            int partial = 0;
            int i = 0;
            while (i < raw.Length)
            {
                if (raw[i] != '\u001b')
                {
                    visible.Append(raw[i]);
                    map[count++] = i;
                    i++;
                    continue;
                }
                switch (Sequence(raw, i, out int end))
                {
                    case SequenceKind.Hidden:
                        hidden.Add((i, end));
                        i = end;
                        break;
                    case SequenceKind.Partial:
                        hidden.Add((i, raw.Length));
                        partial = raw.Length - i;
                        i = raw.Length;
                        break;
                    case SequenceKind.Boundary:
                        visible.Append('\u001b');
                        map[count++] = i;
                        i = end;
                        break;
                    default:
                        visible.Append('\u001b');
                        map[count++] = i;
                        i++;
                        break;
                }
            }
            Array.Resize(ref map, count);
            return new Projection(raw, visible.ToString(), map, origin, hidden, partial);
        }

        /// <summary>可见文本第 <paramref name="index" /> 个字符在原文里从哪起。</summary>
        public int RawStart(int index) => _map[index];

        /// <summary>可见文本 <c>[…, end)</c> 在原文里到哪为止(最后一个字符之后)。</summary>
        public int RawEnd(int end) => end <= Origin ? 0 : _map[end - 1] + 1;

        /// <summary>可见文本的一段换算成原文的范围。</summary>
        public (int Start, int End) RawRange(int start, int end) => (RawStart(start), RawEnd(end));

        /// <summary>原文位置 <paramref name="raw" /> 处(或之后)第一个可见字符在可见文本里的位置;后面没有就是可见文本的长度。</summary>
        public int VisibleIndexAt(int raw)
        {
            int lo = Origin;
            int hi = _map.Length;
            while (lo < hi)
            {
                int mid = (lo + hi) >>> 1;
                if (_map[mid] < raw)
                {
                    lo = mid + 1;
                }
                else
                {
                    hi = mid;
                }
            }
            return lo;
        }

        /// <summary>原文位置 <paramref name="raw" /> 之前的最后一个可见字符(含补在前面的那个);没有为 null。</summary>
        public char? LastVisibleBefore(int raw)
        {
            int index = VisibleIndexAt(raw) - 1;
            return index >= 0 ? Visible[index] : null;
        }

        /// <summary>原文 <c>[from, end)</c> 里的可见字符。</summary>
        public string VisibleBetween(int from, int end)
        {
            int start = VisibleIndexAt(from);
            int stop = VisibleIndexAt(end);
            return stop > start ? Visible[start..stop] : string.Empty;
        }

        /// <summary>可见文本末尾 <paramref name="visibleCount" /> 个字符(连同它们后面的控制序列)在原文里占多少字节;再加上没写完的序列。</summary>
        public int KeepFor(int visibleCount) =>
            visibleCount <= 0 ? PartialLength : Raw.Length - _map[Visible.Length - visibleCount];

        /// <summary>把原文 <c>[start, end)</c> 里不占位置的完整控制序列按原样接到 <paramref name="output" /> 上(遮掉的那段里的颜色、光标序列照放)。</summary>
        public void AppendHidden(StringBuilder output, int start, int end)
        {
            int partialStart = Raw.Length - PartialLength;
            foreach ((int s, int e) in _hidden)
            {
                if (s >= start && e <= end && (PartialLength == 0 || s != partialStart))
                {
                    output.Append(Raw, s, e - s);
                }
            }
        }

        /// <summary>
        /// <paramref name="start" /> 处的 ESC 起头的是哪种序列,<paramref name="end" /> 是它之后的位置。
        /// CSI:<c>ESC [</c>、参数 0x30–0x3F、中间字节 0x20–0x2F、终止字节 0x40–0x7E;字符串序列的开头 <c>ESC ] P X ^ _</c> 与 ST <c>ESC \</c>;
        /// 其余 <c>ESC</c> + 中间字节 + 终止字节 0x30–0x7E。
        /// </summary>
        private static SequenceKind Sequence(string raw, int start, out int end)
        {
            end = start + 1;
            if (end >= raw.Length)
            {
                return SequenceKind.Partial;
            }
            char next = raw[end];
            if (next is ']' or 'P' or 'X' or '^' or '_' or '\\')
            {
                end++;
                return SequenceKind.Boundary;
            }
            int i = end;
            if (next == '[')
            {
                i++;
                while (i < raw.Length && raw[i] is >= '\u0030' and <= '\u003f')
                {
                    i++;
                }
            }
            while (i < raw.Length && raw[i] is >= '\u0020' and <= '\u002f')
            {
                i++;
            }
            if (i - start > MaxSequenceBytes)
            {
                return SequenceKind.Lone;
            }
            if (i >= raw.Length)
            {
                return SequenceKind.Partial;
            }
            char final = raw[i];
            bool valid = next == '[' ? final is >= '\u0040' and <= '\u007e' : final is >= '\u0030' and <= '\u007e';
            if (!valid)
            {
                return SequenceKind.Lone;
            }
            end = i + 1;
            return SequenceKind.Hidden;
        }

        private enum SequenceKind
        {
            /// <summary>不占位置:CSI 与两三字节的 ESC 序列。</summary>
            Hidden,

            /// <summary>到原文末尾都没写完。</summary>
            Partial,

            /// <summary>字符串序列的开头或 ST:可见文本里留一个 ESC 作分界。</summary>
            Boundary,

            /// <summary>坏的或超长的:ESC 原样可见。</summary>
            Lone
        }
    }
}

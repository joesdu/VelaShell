using System.Text.RegularExpressions;
using VelaShell.Core.Models;

namespace VelaShell.Services;

/// <summary>
/// 快捷命令变量询问框的「上一次填的值」:按快捷命令 + 占位名记,下次弹框时预填。
/// </summary>
/// <remarks>
/// <para>
/// <c>kubectl logs -f {{pod}}</c> 这类命令往往连着跑好几遍,每次都是同一个 pod;没有记忆就得每次重敲。
/// </para>
/// <para>
/// <b>只记在进程里,不落盘、不进同步载荷。</b>占位里填的东西没有任何约束:可能是 pod 名,也可能是
/// <c>{{token}}</c>、<c>{{password}}</c>。落盘就得给它们定级、加密、写进导出与同步的口径;
/// 而这项便利要解决的正是「连着重复用」,进程内足够了 —— 重启之后从默认值重新来,代价只是敲一遍。
/// </para>
/// <para>
/// 名字看起来是凭据的占位(含 pass / pw / secret / token / otp / 密码 / 口令 / 令牌 …)<b>连进程内也不记</b>:
/// 询问框是明文输入框,预填进去就等于把口令摆在屏幕上给身后的人看。宁可误伤一个叫 <c>bypass</c> 的变量。
/// 填进这类占位的值也挡在命令历史之外(<see cref="SensitiveValues" />,由终端标签按值挡):
/// 历史是落盘的,补全会把整行原样端出来。
/// </para>
/// <para>
/// 填空的那一格不记(并忘掉旧值):留空多半是想要默认值,下一次再把旧值塞回来反而要多删一次。
/// 键优先用快捷命令的 id(改了命令正文记忆照旧跟着它);没有 id 的按命令正文记。
/// </para>
/// </remarks>
public sealed partial class QuickCommandVariableMemory
{
    /// <summary>最多记多少条命令的值;再多就丢掉最早记下的那条。只是防一个跑飞的脚本把内存撑大。</summary>
    public const int MaxCommands = 200;

    /// <summary>名字里出现这些片段(不分大小写)就当作凭据,不记。</summary>
    private static readonly string[] SensitiveFragments =
    [
        "pass", "pwd", "pw", "secret", "token", "otp", "credential", "apikey", "api_key", "api-key", "privatekey", "private_key",
        "private-key", "accesskey", "access_key", "access-key", "authorization", "mfa", "2fa", "jwt", "bearer", "cookie",
        "密码", "口令", "密钥", "令牌", "验证码", "凭据", "凭证", "秘钥", "私钥",
        "密碼", "金鑰", "權杖", "驗證碼", "憑據", "憑證", "秘鑰", "私鑰",
        "パスワード", "トークン", "暗証番号", "パスコード", "秘密鍵", "認証コード",
        "비밀번호", "토큰", "암호", "비번", "패스워드", "인증번호", "인증코드", "개인키", "비밀키"
    ];

    /// <summary>
    /// 太短、作片段会误伤一大片的词(<c>ping</c>、<c>monkey</c>、<c>author</c>):名字按 <c>_</c> / <c>-</c> / 驼峰 / 数字拆成词后,
    /// 有一个词正好是它们(不分大小写)才算凭据 —— <c>pin</c>、<c>sim_pin</c>、<c>sshKey</c>、<c>auth</c> 算,<c>ping_host</c>、<c>keyspace</c> 不算。
    /// </summary>
    private static readonly string[] SensitiveWords = ["pin", "key", "auth"];

    private readonly Dictionary<string, Dictionary<string, string>> _values = [with(StringComparer.Ordinal)];
    private readonly LinkedList<string> _order = [];

    /// <summary>这个变量名是不是看起来像凭据(这类的值不记)。</summary>
    /// <param name="variableName">占位名。</param>
    /// <returns>像凭据时为 true。</returns>
    public static bool IsSensitive(string variableName)
    {
        if (SensitiveFragments.Any(fragment => variableName.Contains(fragment, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }
        foreach (ValueMatch word in NameWords().EnumerateMatches(variableName))
        {
            ReadOnlySpan<char> text = variableName.AsSpan(word.Index, word.Length);
            foreach (string sensitive in SensitiveWords)
            {
                if (text.Equals(sensitive, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>
    /// 这一次填进凭据类占位的值(没给的用默认值,与 <see cref="QuickCommandTemplate.Render" /> 同一口径;空白的不算):
    /// 提交的命令行里含其中任何一个,那一行就不进命令历史。
    /// </summary>
    /// <param name="template">解析好的模板。</param>
    /// <param name="values">询问框交回的「变量名 → 值」。</param>
    /// <returns>要挡在历史之外的值;没有凭据类占位时为空。</returns>
    public static IReadOnlyList<string> SensitiveValues(QuickCommandTemplate template, IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(values);
        var secrets = new List<string>();
        foreach (QuickCommandVariable variable in template.Variables)
        {
            if (!IsSensitive(variable.Name))
            {
                continue;
            }
            string value = values.TryGetValue(variable.Name, out string? given) ? given : variable.DefaultValue;
            // 替换时值里的换行换成了空格:按行拆开逐段认,哪一段出现在命令行里都算。
            foreach (string part in value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                if (!string.IsNullOrWhiteSpace(part))
                {
                    secrets.Add(part);
                }
            }
        }
        return secrets;
    }

    /// <summary>把占位名拆成词:连续大写(<c>PIN</c>)、首字母大写或全小写的一段(<c>Pin</c> / <c>pin</c>)、一串数字。</summary>
    [GeneratedRegex(@"\p{Lu}+(?!\p{Ll})|\p{Lu}?\p{Ll}+|\p{N}+", RegexOptions.CultureInvariant)]
    private static partial Regex NameWords();

    /// <summary>弹框时每一格的初值:记过的用记下的值,没记过的用占位自己的默认值。</summary>
    /// <param name="commandId">快捷命令 id;没有时为 null。</param>
    /// <param name="commandText">命令正文(没有 id 时作键)。</param>
    /// <param name="template">解析好的模板。</param>
    /// <returns>「变量名 → 初值」,覆盖模板里的每一个变量。</returns>
    public IReadOnlyDictionary<string, string> Prefill(Guid? commandId, string commandText, QuickCommandTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);
        _values.TryGetValue(KeyFor(commandId, commandText), out Dictionary<string, string>? remembered);
        var initial = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (QuickCommandVariable variable in template.Variables)
        {
            initial[variable.Name] = remembered is not null && remembered.TryGetValue(variable.Name, out string? value)
                ? value
                : variable.DefaultValue;
        }
        return initial;
    }

    /// <summary>用户点了发送:记下这一次填的值(凭据类与空着的格子除外)。</summary>
    /// <param name="commandId">快捷命令 id;没有时为 null。</param>
    /// <param name="commandText">命令正文(没有 id 时作键)。</param>
    /// <param name="template">解析好的模板。</param>
    /// <param name="values">询问框交回的「变量名 → 值」。</param>
    public void Remember(
        Guid? commandId,
        string commandText,
        QuickCommandTemplate template,
        IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(values);
        string key = KeyFor(commandId, commandText);
        if (!_values.TryGetValue(key, out Dictionary<string, string>? remembered))
        {
            remembered = [with(StringComparer.Ordinal)];
        }
        foreach (QuickCommandVariable variable in template.Variables)
        {
            if (IsSensitive(variable.Name)
                || !values.TryGetValue(variable.Name, out string? value)
                || string.IsNullOrWhiteSpace(value))
            {
                remembered.Remove(variable.Name);
                continue;
            }
            remembered[variable.Name] = value;
        }
        _order.Remove(key);
        if (remembered.Count == 0)
        {
            _values.Remove(key);
            return;
        }
        _values[key] = remembered;
        _order.AddLast(key);
        while (_order.Count > MaxCommands && _order.First is { } oldest)
        {
            _values.Remove(oldest.Value);
            _order.RemoveFirst();
        }
    }

    private static string KeyFor(Guid? commandId, string commandText) =>
        commandId is { } id && id != Guid.Empty ? $"id:{id:N}" : $"text:{commandText}";
}

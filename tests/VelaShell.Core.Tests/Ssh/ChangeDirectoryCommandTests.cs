using System.Text;
using VelaShell.Core.Ssh;

namespace VelaShell.Core.Tests.Ssh;

/// <summary>
/// 登录后的那一行 <c>cd</c>(配置的初始目录、「在此处打开终端」):目录名按会话字符集编出来有 ≥ 0x80 的字节时,
/// 改发只含可打印 ASCII 的 <c>cd "$(printf '\ooo…')"</c> —— C / POSIX 区域设置下 readline 可能把这些字节当 Meta 键
/// (<c>£</c> 的 <c>A3</c> 就是 M-#,当场提交整行)。纯 ASCII 的名字照旧原样引起来。
/// </summary>
/// <remarks>
/// 「shell 语义对不对」靠下面那个小解码器核:按 POSIX sh(或 fish)的引号规则与 POSIX <c>printf</c> 的格式串规则,
/// 把发出去的那一行还原成 <c>cd</c> 收到的字节,与目录名按会话字符集编出来的字节逐个比。解码器碰到任何会让 shell
/// 做别的事情的东西 —— 没加引号的元字符、变量展开、反引号、双引号里的 <c>!</c>(历史展开)、<c>printf</c> 的 <c>%</c> 指令、
/// 多出来的参数 —— 一律判失败,而不是猜一个结果。
/// </remarks>
[TestClass]
[TestCategory("Ssh")]
public sealed class ChangeDirectoryCommandTests
{
    private static readonly RemoteShellKind[] PosixFamily =
        [RemoteShellKind.Bash, RemoteShellKind.Zsh, RemoteShellKind.Fish, RemoteShellKind.PosixSh, RemoteShellKind.Unknown];

    [ClassInitialize]
    public static void RegisterCodePages(TestContext _) => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    [TestMethod]
    [DataRow("UTF-8", "/data/报表/2026 年", DisplayName = "中文目录(UTF-8)")]
    [DataRow("GBK", "/data/报表/2026 年", DisplayName = "中文目录(GBK 会话:按 GBK 的字节)")]
    [DataRow("GB18030", "/data/报表/𠀀", DisplayName = "GB18030 四字节")]
    [DataRow("Big5", "/data/報表", DisplayName = "Big5")]
    [DataRow("Shift_JIS", "/srv/表示", DisplayName = "Shift_JIS:「表」的尾字节是 0x5C")]
    [DataRow("EUC-KR", "/srv/한글", DisplayName = "EUC-KR")]
    [DataRow("ISO-8859-1", "/srv/café", DisplayName = "单字节字符集")]
    [DataRow("UTF-8", "/srv/price£list", DisplayName = "£ = C2 A3(A3 即 M-#)")]
    [DataRow("UTF-8", "/srv/中'\"\\%$`!-x ~*?;|&<>(){}#=", DisplayName = "非 ASCII 加上全部引号与元字符")]
    [DataRow("UTF-8", "-é1", DisplayName = "打头的 - 与紧跟在转义后的数字")]
    [DataRow("UTF-8", "/srv/😀 emoji", DisplayName = "代理对")]
    public void NonAsciiName_IsSentAsPrintableAscii_AndCdReceivesTheExactBytes(string encodingName, string directory)
    {
        var encoding = Encoding.GetEncoding(encodingName);
        byte[] expected = encoding.GetBytes(directory);
        Assert.IsTrue(Array.Exists(expected, static b => b >= 0x80), "用例本身得有非 ASCII 的字节");

        foreach (RemoteShellKind shell in PosixFamily)
        {
            string command = ChangeDirectoryCommand.Build(shell, directory, encoding);

            Assert.Contains("printf '", command, $"{shell}:{command}");
            AssertWholeInjectedLineIsPrintableAscii(shell, command, encoding);
            Assert.AreSequenceEqual(expected, DecodeCdArgument(command, shell, encoding), $"{shell}:{command}");
        }
    }

    /// <summary>fish 写成它自己的 <c>(printf …)</c>:3.4 之前的 fish 不认 <c>$(…)</c>;其余写成 POSIX 的 <c>"$(printf …)"</c>。</summary>
    [TestMethod]
    public void TheShape_FollowsTheLoginShell()
    {
        Assert.AreEqual(@"cd (printf '/srv/\302\243')", ChangeDirectoryCommand.Build(RemoteShellKind.Fish, "/srv/£", Encoding.UTF8));
        foreach (RemoteShellKind shell in new[] { RemoteShellKind.Bash, RemoteShellKind.Zsh, RemoteShellKind.PosixSh, RemoteShellKind.Unknown })
        {
            Assert.AreEqual(@"cd ""$(printf '/srv/\302\243')""", ChangeDirectoryCommand.Build(shell, "/srv/£", Encoding.UTF8), shell.ToString());
        }
    }

    /// <summary>同一个中文目录,GBK 会话与 UTF-8 会话发出去的字节不同:按会话字符集,不一律当 UTF-8。</summary>
    [TestMethod]
    public void TheBytes_FollowTheSessionEncoding()
    {
        var gbk = Encoding.GetEncoding("GBK");

        string viaGbk = ChangeDirectoryCommand.Build(RemoteShellKind.Bash, "/报", gbk);
        string viaUtf8 = ChangeDirectoryCommand.Build(RemoteShellKind.Bash, "/报", Encoding.UTF8);

        Assert.AreEqual(@"cd ""$(printf '/\261\250')""", viaGbk, "报 在 GBK 里是 B1 A8");
        Assert.AreEqual(@"cd ""$(printf '/\346\212\245')""", viaUtf8, "报 在 UTF-8 里是 E6 8A A5");
    }

    /// <summary>纯 ASCII 的名字照旧按登录 shell 的单引号规则原样发(回显看得懂,已有行为不变)。</summary>
    [TestMethod]
    [DataRow("/srv/it's here")]
    [DataRow("/srv/a\"b\\c%d$e`f!g")]
    [DataRow(@"/srv/a\';touch /tmp/pwn;'")]
    public void AsciiName_KeepsTheQuotedForm(string directory)
    {
        foreach (RemoteShellKind shell in PosixFamily)
        {
            string command = ChangeDirectoryCommand.Build(shell, directory, Encoding.UTF8);

            Assert.DoesNotContain("printf", command, $"{shell}:{command}");
            AssertWholeInjectedLineIsPrintableAscii(shell, command, Encoding.UTF8);
            Assert.AreSequenceEqual(Encoding.UTF8.GetBytes(directory), DecodeCdArgument(command, shell, Encoding.UTF8), $"{shell}:{command}");
        }
        Assert.AreEqual(@"cd '/srv/it'\''s here'", ChangeDirectoryCommand.Build(RemoteShellKind.Bash, "/srv/it's here", Encoding.UTF8));
    }

    /// <summary>Windows 服务器(cmd.exe / PowerShell)不是 readline,也没有 printf:照旧原样引起来发。</summary>
    [TestMethod]
    public void NonPosixServer_KeepsTheQuotedForm() =>
        Assert.AreEqual("cd 'D:/报表'", ChangeDirectoryCommand.Build(RemoteShellKind.NonPosix, "D:/报表", Encoding.UTF8));

    /// <summary>解码器自己也得会拒:它若什么都放过,上面那些「解得回来」就证明不了什么。</summary>
    [TestMethod]
    [DataRow("cd \"$(printf '%s' x)\"", DisplayName = "printf 多一个参数")]
    [DataRow("cd \"$(printf '%s')\"", DisplayName = "格式串里的指令")]
    [DataRow("cd \"$HOME/x\"", DisplayName = "变量展开")]
    [DataRow("cd \"$(printf 'a')!\"", DisplayName = "双引号里的 !")]
    [DataRow("cd \"$(printf '!a')\"", DisplayName = "双引号里套着的命令替换里的 !")]
    [DataRow("cd \"`id`\"", DisplayName = "反引号")]
    [DataRow("cd '/tmp';id", DisplayName = "没加引号的分号")]
    [DataRow("cd \"$(id)\"", DisplayName = "命令替换里不是 printf")]
    public void TheDecoder_RejectsAnythingThatWouldMakeTheShellDoMore(string line) =>
        Assert.ThrowsExactly<AssertFailedException>(() => DecodeCdArgument(line, RemoteShellKind.Bash, Encoding.UTF8));

    // ---- 发出去的整行 ----

    /// <summary>
    /// 与宿主 <c>TerminalTabViewModel.SendSilentCommand</c> 同一个组装(摘历史前缀、哨兵、前导空格、末尾换行),
    /// 按会话字符集编码之后,除末尾的换行外每个字节都得是可打印 ASCII。
    /// </summary>
    private static void AssertWholeInjectedLineIsPrintableAscii(RemoteShellKind shell, string command, Encoding encoding)
    {
        ShellIntegrationInjection injection = SilentCommand.Build(shell, null, command);
        byte[] sent = encoding.GetBytes(" " + injection.CommandLine + "\n");
        Assert.AreEqual((byte)'\n', sent[^1]);
        for (int i = 0; i < sent.Length - 1; i++)
        {
            Assert.IsTrue(sent[i] is >= 0x20 and <= 0x7E, $"{shell}:第 {i} 个字节是 0x{sent[i]:X2}:{injection.CommandLine}");
        }
    }

    // ---- 解码器:POSIX sh(或 fish)的引号 + POSIX printf 的格式串 ----

    /// <summary>把 <c>cd &lt;一个词&gt;</c> 还原成 <c>cd</c> 收到的字节。</summary>
    private static byte[] DecodeCdArgument(string line, RemoteShellKind shell, Encoding encoding)
    {
        Assert.StartsWith("cd ", line);
        bool fish = shell == RemoteShellKind.Fish;
        string word = line[3..];
        var bytes = new List<byte>();
        int i = 0;
        while (i < word.Length)
        {
            char c = word[i];
            if (c == '\'')
            {
                bytes.AddRange(encoding.GetBytes(ReadSingleQuoted(word, ref i, fish)));
            }
            else if (c == '"' && !fish)
            {
                bytes.AddRange(ReadDoubleQuoted(word, ref i, encoding));
            }
            else if (c == '(' && fish)
            {
                byte[] output = RunSubstitution(ReadParenthesised(word, ref i), fish);
                // fish 的命令替换只按换行切分:结果得正好是一个参数。
                Assert.DoesNotContain((byte)'\n', output, "fish 的命令替换会在换行处切成多个参数");
                bytes.AddRange(output);
            }
            else if (c == '\\' && !fish && i + 1 < word.Length)
            {
                bytes.AddRange(encoding.GetBytes(word[i + 1].ToString()));
                i += 2;
            }
            else if (char.IsWhiteSpace(c) || ";&|<>()$`*?[]{}#~!\"\\".Contains(c, StringComparison.Ordinal))
            {
                Assert.Fail($"没加引号的 {c}:shell 会把它当语法而不是目录名的一部分({line})");
            }
            else
            {
                bytes.AddRange(encoding.GetBytes(c.ToString()));
                i++;
            }
        }
        return [.. bytes];
    }

    /// <summary>单引号:POSIX 里一个转义都没有;fish 里只有 <c>\\</c> 与 <c>\'</c>。</summary>
    private static string ReadSingleQuoted(string text, ref int i, bool fish)
    {
        var value = new StringBuilder();
        for (i++; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\'')
            {
                i++;
                return value.ToString();
            }
            if (fish && c == '\\' && i + 1 < text.Length && text[i + 1] is '\\' or '\'')
            {
                value.Append(text[++i]);
                continue;
            }
            value.Append(c);
        }
        Assert.Fail("单引号没有收口:" + text);
        return string.Empty;
    }

    /// <summary>POSIX 双引号:<c>\</c> 只转义 <c>$ ` " \</c>;<c>$(</c> 是命令替换,其余 <c>$</c>、反引号、<c>!</c>(bash / zsh 的历史展开)判失败。</summary>
    private static List<byte> ReadDoubleQuoted(string text, ref int i, Encoding encoding)
    {
        var bytes = new List<byte>();
        for (i++; i < text.Length; i++)
        {
            char c = text[i];
            switch (c)
            {
                case '"':
                    i++;
                    return bytes;
                case '\\' when i + 1 < text.Length && text[i + 1] is '$' or '`' or '"' or '\\':
                    bytes.AddRange(encoding.GetBytes(text[++i].ToString()));
                    break;
                case '$' when i + 1 < text.Length && text[i + 1] == '(':
                    i++;
                    string substitution = ReadParenthesised(text, ref i);
                    // bash 的历史展开扫的是整行、只认双引号的状态:双引号里套着的命令替换,里面的单引号挡不住 !。
                    Assert.DoesNotContain("!", substitution, "双引号里的命令替换里有 !,交互式 bash 会做历史展开:" + text);
                    byte[] output = RunSubstitution(substitution, fish: false);
                    i--;
                    // POSIX:命令替换去掉末尾的换行。
                    int end = output.Length;
                    while (end > 0 && output[end - 1] == (byte)'\n')
                    {
                        end--;
                    }
                    bytes.AddRange(output.AsSpan(0, end).ToArray());
                    break;
                case '$' or '`':
                    Assert.Fail($"双引号里的 {c} 会展开:" + text);
                    break;
                case '!':
                    Assert.Fail("双引号里的 ! 在交互式 bash / zsh 里是历史展开:" + text);
                    break;
                default:
                    bytes.AddRange(encoding.GetBytes(c.ToString()));
                    break;
            }
        }
        Assert.Fail("双引号没有收口:" + text);
        return bytes;
    }

    /// <summary>从 <c>(</c> 读到与它配对的 <c>)</c>(单引号里的括号不算),返回中间那段;<paramref name="i" /> 停在 <c>)</c> 之后。</summary>
    private static string ReadParenthesised(string text, ref int i)
    {
        int start = i + 1;
        bool quoted = false;
        for (int j = start; j < text.Length; j++)
        {
            if (text[j] == '\'')
            {
                quoted = !quoted;
            }
            else if (text[j] == ')' && !quoted)
            {
                i = j + 1;
                return text[start..j];
            }
        }
        Assert.Fail("括号没有收口:" + text);
        return string.Empty;
    }

    /// <summary>命令替换里只认一条 <c>printf '格式串'</c>,别的一概不认。</summary>
    private static byte[] RunSubstitution(string command, bool fish)
    {
        Assert.StartsWith("printf '", command, "命令替换里只该有 printf");
        int i = "printf ".Length;
        string format = ReadSingleQuoted(command, ref i, fish);
        Assert.AreEqual(command.Length, i, "printf 只该有格式串一个参数:" + command);
        return Printf(format);
    }

    /// <summary>POSIX printf 的格式串(没有参数):<c>\ooo</c>、<c>\\</c> 与几个字母转义,<c>%%</c>;别的 <c>%</c> 判失败。</summary>
    private static byte[] Printf(string format)
    {
        var bytes = new List<byte>();
        for (int i = 0; i < format.Length; i++)
        {
            char c = format[i];
            Assert.IsLessThan(0x80, c, "格式串里不该有非 ASCII 的字符");
            if (c == '%')
            {
                Assert.IsTrue(i + 1 < format.Length && format[i + 1] == '%', "格式串里的 % 指令会去吃参数:" + format);
                bytes.Add((byte)'%');
                i++;
            }
            else if (c == '\\')
            {
                Assert.IsLessThan(format.Length, i + 1, "格式串以 \\ 结尾");
                char next = format[++i];
                if (next is >= '0' and <= '7')
                {
                    int value = 0;
                    int digits = 0;
                    while (digits < 3 && i < format.Length && format[i] is >= '0' and <= '7')
                    {
                        value = (value * 8) + (format[i] - '0');
                        digits++;
                        i++;
                    }
                    i--;
                    bytes.Add((byte)value);
                }
                else
                {
                    bytes.Add(next switch
                    {
                        '\\' => (byte)'\\',
                        'a' => 0x07,
                        'b' => 0x08,
                        'f' => 0x0C,
                        'n' => 0x0A,
                        'r' => 0x0D,
                        't' => 0x09,
                        'v' => 0x0B,
                        _ => throw new AssertFailedException($"格式串里的 \\{next} 各家 printf 解释不一:" + format)
                    });
                }
            }
            else
            {
                bytes.Add((byte)c);
            }
        }
        return [.. bytes];
    }
}

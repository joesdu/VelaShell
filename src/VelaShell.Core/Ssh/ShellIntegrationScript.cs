using System.Security.Cryptography;

namespace VelaShell.Core.Ssh;

/// <summary>
/// 按 shell 种类分派的**目录上报**脚本 —— 「SFTP 文件浏览器跟随终端目录」的数据源。
/// 连接后由 <c>MainWindowViewModel</c> 静默注入;注入窗口内的一切输出由
/// <c>EchoSuppressor</c> 吞掉,注入失败在屏幕上不留痕迹。
/// </summary>
/// <remarks>
/// <para>
/// <b>以 VS Code 的 shell 集成(OSC 633)为基础,补上它不需要的那一半。</b>VS Code 的自动注入
/// 靠的是<b>自己启动 shell</b>(bash 给 <c>--init-file</c>、zsh 换 <c>ZDOTDIR</c>、fish 加
/// <c>XDG_DATA_DIRS</c>),它的文档也白纸黑字写着"普通 <c>ssh</c> 会话用不了这招"。
/// SSH 客户端没有那个口子,只能往一个<b>已经跑起来的交互式 shell 的 PTY 里打字</b> ——
/// 于是回显、命令历史、语法报错、探测、多条注入互相顶掉,这一整类问题得自己解决。
/// 能照搬的是协议与片段写法,搬不了的是投递方式。
/// </para>
/// <para>
/// <b>为什么要按种类分派。</b>原先只有一段 bash 钩子,守卫是 <c>test -n "$BASH_VERSION"</c> ——
/// 它挡住了 fish 的解析期报错,却也把 <b>zsh 一并短路掉了</b>:zsh 用户不会看到报错,
/// 但「跟随终端目录」<b>就是不工作</b>,而 zsh 是 macOS 的默认 shell、是 oh-my-zsh 用户的全部。
/// 同理 dash / ash(Alpine、BusyBox、OpenWrt)与 ksh 连 <c>PROMPT_COMMAND</c> 这个变量都没有。
/// </para>
/// <para>
/// <b>只上报目录,不注入命令块语义。</b>VS Code 注入的是整套 OSC 633(<c>A/B/C/D/E</c> +
/// <c>P;Cwd=</c>),那需要动 <c>PS0</c> / <c>preexec</c> / <c>PS1</c>;而 <c>PS1</c> 会被
/// starship、oh-my-posh、powerlevel10k <b>每次画提示符都重写</b>,爆炸半径远大于这里要解决的问题
/// (理由详见 <c>VelaShell.Core.Models.ShellIntegration</c>,那套 OSC 133 片段是"摆出来给用户
/// 自己抄进 rc"的)。本文件因此只做一件事:报 cwd。
/// </para>
/// <para>
/// <b>一次报两条:先 OSC 7,后 OSC 633。</b>OSC 7 是 VTE / kitty / WezTerm / Windows Terminal /
/// tmux 都认的通用写法,发它是为了让注进去的钩子对用户的<b>其他</b>终端也有价值;
/// OSC 633 是我们自己要用的那条,排在后面发因此<b>后到者为准</b> —— 这正好解决路径里带
/// <c>%</c> 的歧义:OSC 7 是 URL,<c>/tmp/100%done</c> 这种名字解码时会出错,而 633 是裸路径,
/// 连路径里的分号都能由解析端重新拼回来(见 <c>TerminalEmulator.JoinFrom</c>)。
/// </para>
/// </remarks>
public static class ShellIntegrationScript
{
    /// <summary>
    /// 四段脚本共用的函数名。也是 bash 那段的去重锚点,<b>不能改</b> —— 改了之后
    /// 被旧版注入过的会话就认不出自己,那条自愈路径(见 <see cref="Bash" />)会失效。
    /// </summary>
    public const string FunctionName = "vela_shell_osc7";

    /// <summary>
    /// 注入完成哨兵的键名。整条哨兵形如 <c>OSC 633 ; P ; VelaShell=&lt;nonce&gt; BEL</c> ——
    /// 借 VS Code 那套 <c>P</c>(属性)的键值命名空间,别家终端认不得就会忽略,不会打扰谁。
    /// </summary>
    public const string SentinelKey = "VelaShell";

    /// <summary>
    /// 每次注入都发的两条上报序列(<c>printf</c> 的格式串,三个占位符依次是 主机名、PWD、PWD)。
    /// </summary>
    /// <remarks>
    /// <b>一律用 BEL(<c>\007</c>)收尾,不用 ST(<c>ESC \</c>)。</b>两种收尾解析端都认
    /// (<c>Osc7WorkingDirectoryTests</c> 各有一例),但 ST 里那个反斜杠在四种 shell 的引号规则下
    /// 分别要写成两个、四个或原样 —— 是纯粹的坑。真正非用 ST 不可的只有 tmux 透传那条
    /// (DCS 只能由 ST 收尾),那一条因此单独拎出来,并且各 shell 各写各的。
    /// </remarks>
    private const string ReportFormat = @"\033]7;file://%s%s\007\033]633;P;Cwd=%s\007";

    /// <summary>
    /// 随机 nonce:<c>[0-9a-f]{8}</c>。每次注入一枚,注入窗口据此认出"我这一行跑完了"。
    /// </summary>
    /// <remarks>
    /// 用随机数而不是固定字符串:同一个会话里可能连着注入好几条(重连、多开标签),
    /// 固定字符串会让第一条的哨兵把第二条的窗口提前关掉。
    /// </remarks>
    public static string NewNonce() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));

    /// <summary>
    /// bash:走 <c>PROMPT_COMMAND</c>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>bash 代码必须留在单引号包裹的 eval 参数里。</b>shell 会先把整行解析完再执行,
    /// 裸写的函数定义与 <c>[[ ]]</c>、<c>${var//a/b}</c> 会让 fish 在<b>解析阶段</b>就报错 ——
    /// 那时外层守卫还没来得及短路。包进单引号后 fish 只看到一个字符串,静默跳过。
    /// 现在虽然已经按种类分派、不该再发错门,这层守卫仍然留着:探测结论也可能错
    /// (用户的 <c>.bashrc</c> 里 <c>exec zsh</c> 就是一例)。
    /// </para>
    /// <para>
    /// <b>装法是「先把自己摘掉、清干净首尾、再装回去」,不是「发现装过就跳过」。</b>
    /// 后者看着更省事,实际漏了两种情况,而这两种在真机上都撞到了:
    /// </para>
    /// <list type="number">
    /// <item>原值结尾自带分号时会拼出 <c>;;</c>。pyenv-virtualenv 的初始化在 PROMPT_COMMAND 为空时
    /// 就是设成 <c>_pyenv_virtualenv_hook;</c>,于是追加后成了
    /// <c>_pyenv_virtualenv_hook;;vela_shell_osc7</c> —— <c>;;</c> 出了 case 就是语法错误,
    /// 用户**每敲一次回车**都会看到一行报错。所以追加前必须把尾部的分号与空白剪掉。</item>
    /// <item>会话一旦已经是坏的,「跳过」就永远修不回来:去重看到里面已有 <c>vela_shell_osc7</c>,
    /// 认定装过了,坏值原样留着。改成无条件重装之后,这种会话再连一次就自愈。</item>
    /// </list>
    /// <para>
    /// <b>只剪首尾,绝不动中间。</b>看着更彻底的 <c>${PROMPT_COMMAND//;;/;}</c> 会把用户
    /// PROMPT_COMMAND 里合法的 <c>case</c> 分支(<c>… ;; *) … ;; esac</c>)切坏。
    /// 摘自己时先去 <c>;vela_shell_osc7</c> 再去裸的,前者把分隔符一并带走,免得在中间留下 <c>;;</c>。
    /// </para>
    /// <para>
    /// <b>钩子必须把 <c>$?</c> 原样传下去。</b>bash 在跑完<b>全部</b> <c>PROMPT_COMMAND</c> 之后
    /// 才展开 <c>PS1</c>,那时 <c>$?</c> 是最后一个钩子的状态 —— 我们这个钩子排在最后,
    /// 不还原就等于把用户提示符里的"上条命令退出码"永久钉死成 0(<c>printf</c> 总是成功)。
    /// 进门存下、出门 <c>return</c> 回去,这个钩子对 <c>$?</c> 就是透明的。
    /// </para>
    /// <para>
    /// 这段是 shell 语义,C# 单测只断言得了字符串里有什么,拦不住「跑起来才炸」——
    /// 状态矩阵在 <c>PromptHookShellTests</c>(交给真 bash 跑),端到端在
    /// <c>ShellIntegrationDockerTests</c>(交给真 sshd + 真 bash 跑)。
    /// </para>
    /// </remarks>
    public static string Bash { get; } =
        "test -n \"${BASH_VERSION:-}\" && eval '"
        + FunctionName + "() { local __vela_rc=$?; printf \"" + ReportFormat + "\" \"${HOSTNAME:-}\" \"$PWD\" \"$PWD\"; "
        + TmuxTail("\"") + " return $__vela_rc; }; "
        + "PROMPT_COMMAND=\"${PROMPT_COMMAND:-}\"; "
        + "PROMPT_COMMAND=\"${PROMPT_COMMAND//;" + FunctionName + "/}\"; "
        + "PROMPT_COMMAND=\"${PROMPT_COMMAND//" + FunctionName + "/}\"; "
        + "while [[ -n $PROMPT_COMMAND && $PROMPT_COMMAND == [\\;[:space:]]* ]]; do PROMPT_COMMAND=${PROMPT_COMMAND#?}; done; "
        + "while [[ -n $PROMPT_COMMAND && $PROMPT_COMMAND == *[\\;[:space:]] ]]; do PROMPT_COMMAND=${PROMPT_COMMAND%?}; done; "
        + "PROMPT_COMMAND=\"${PROMPT_COMMAND:+$PROMPT_COMMAND;}" + FunctionName + "\"'";

    /// <summary>zsh:走原生的 <c>precmd_functions</c> 数组。</summary>
    /// <remarks>
    /// <para>
    /// <b>刻意不用 <c>add-zsh-hook</c>。</b>它是个 autoload 函数 —— NixOS 这类
    /// <c>fpath</c> 里没有标准函数目录的机器上,autoload <b>会静默失败</b>
    /// (注入时 stderr 本就被吞掉),钩子永远不跑,而屏幕上什么也看不到,
    /// 排查时只会得到「功能莫名其妙不工作」。直接往数组里塞则在每个 zsh 上都成立。
    /// 这一条是从 electerm 的源码注释里学来的,他们在真机上撞过。
    /// </para>
    /// <para>
    /// 去重用 zsh 的数组减法 <c>${arr:#pattern}</c>,语义比 bash 那边的字符串裁剪干净得多:
    /// 元素是数组项,不存在"分隔符拼出 <c>;;</c>"这种事。
    /// </para>
    /// </remarks>
    public static string Zsh { get; } =
        "test -n \"${ZSH_VERSION:-}\" && eval '"
        + FunctionName + "() { local __vela_rc=$?; printf \"" + ReportFormat + "\" \"${HOST:-}\" \"$PWD\" \"$PWD\"; "
        + TmuxTail("\"") + " return $__vela_rc; }; "
        + "typeset -ga precmd_functions; "
        + "precmd_functions=(${precmd_functions:#" + FunctionName + "}); "
        + "precmd_functions+=(" + FunctionName + ")'";

    /// <summary>fish:挂在 <c>PWD</c> 这个变量上,连提示符都不必碰。</summary>
    /// <remarks>
    /// <para>
    /// <c>--on-variable PWD</c> 是四段里最干净的一个:它不进 <c>PS1</c>、不进任何提示符钩子链,
    /// 只在目录<b>真的变了</b>的时候响一次 —— 连"每次回车都发一遍"的浪费都没有。
    /// 也因此,注入之后必须<b>主动调一次</b>,否则文件浏览器要等到用户第一次 <c>cd</c> 才有 cwd
    /// (那一次由 <see cref="Build" /> 统一补上,四段一视同仁)。
    /// </para>
    /// <para>
    /// <b>这段无法像另外三段那样包进 <c>eval '…'</c> 里当护身符。</b>那个技巧靠的是
    /// "别的 shell 只看到一个字符串",而这里要防的恰恰是 fish 自己的解析器 —— 它对
    /// <c>${var:-default}</c> 这种写法在解析期就报错。所以 fish 这一段的安全全靠两道闸:
    /// 探测分派(只发给真的 fish)与注入窗口的输出吞噬(发错了也看不见)。
    /// 同样的原因,发往 fish 的整行<b>不许接摘历史前缀</b>(见 <see cref="ShellHistoryScrub.SupportedBy" />)。
    /// </para>
    /// <para>
    /// 先 <c>functions -q</c> 再 <c>-e</c>:直接删一个不存在的函数,fish 会往 stderr 写一行抱怨。
    /// <c>"$hostname"</c> 必须带引号 —— fish 里未设置的变量在参数位置会<b>整个消失</b>而不是
    /// 展开成空串,少一个参数,后面那个 <c>%s</c> 就会去吃 <c>$PWD</c>,路径直接错位。
    /// </para>
    /// </remarks>
    public static string Fish { get; } =
        "functions -q " + FunctionName + "; and functions -e " + FunctionName + "; "
        + "function " + FunctionName + " --on-variable PWD; "
        + "printf '" + ReportFormat + "' \"$hostname\" \"$PWD\" \"$PWD\"; "
        + TmuxTailFish() + " end";

    /// <summary>dash / ash / ksh / mksh 等没有提示符钩子的 POSIX shell:只能在 <c>PS1</c> 上做文章。</summary>
    /// <remarks>
    /// <para>
    /// <c>PS1</c> <b>必须用单引号赋值</b>,让 <c>$(…)</c> 原样留在变量值里 —— 这些 shell 每次
    /// 画提示符时都会对 <c>PS1</c> 重做一遍展开,那一刻 <c>$(…)</c> 才执行,拿到的才是当时的
    /// <c>$PWD</c>。写成双引号就变成"注入那一刻算一次,以后永远是那个值"。
    /// </para>
    /// <para>
    /// <b>这里每画一次提示符 fork 一个子 shell,是认下来的代价。</b>省掉它要把整段格式化拆成
    /// 一串 <c>${…}</c> 塞进 <c>PS1</c>(ESC / BEL 先算进变量、tmux 分支写成 <c>${TMUX:+…}</c>
    /// 嵌套展开),换来的是一条没法与另外三段共用、也没法单独跑测的巨型字符串 ——
    /// 而这个功能栽过的两个跟头(<c>;;</c> 语法错误、fish 解析期报错)全是这类聪明写法造的。
    /// 没有提示符钩子的 shell 就付一次 fork,换四段共用同一个函数体。
    /// </para>
    /// <para>
    /// <b>这段不包 <c>eval '…'</c>。</b>它通篇是 POSIX 语法,bash / zsh 拿到也只是照做而已,
    /// 不存在要防的解析期爆炸;而包进单引号反而会和 <c>PS1</c> 里那对单引号打架。
    /// 幂等靠 <c>VELA_SHELL_OSC7</c> 这个标记变量(<b>不导出</b>:它描述的是"这个 shell 实例
    /// 装过了",不该被子进程继承)。
    /// </para>
    /// </remarks>
    public static string PosixSh { get; } =
        "test -n \"${VELA_SHELL_OSC7:-}\" || { VELA_SHELL_OSC7=1; "
        + FunctionName + "() { printf '" + ReportFormat + "' \"${HOSTNAME:-}\" \"$PWD\" \"$PWD\"; "
        + TmuxTail("'") + " }; "
        + "PS1='$(" + FunctionName + ")'\"${PS1:-$ }\"; }";

    /// <summary>
    /// 取该 shell 种类对应的注入脚本;<see cref="RemoteShellKind.Unknown" /> 与
    /// <see cref="RemoteShellKind.NonPosix" /> 返回空串 = <b>一个字节都不发</b>。
    /// </summary>
    /// <remarks>
    /// 探不出来就不注入,是这个功能的底线:宁可丢掉目录跟随,也不能把 sh 代码糊到一个
    /// 不认它的 shell 上(#305)。注入窗口的输出吞噬是第二道闸,不是放宽这一条的理由 ——
    /// 它管的是"注进去之后炸了",管不了"根本不该注"。
    /// </remarks>
    public static string For(RemoteShellKind kind) =>
        kind switch
        {
            RemoteShellKind.Bash => Bash,
            RemoteShellKind.Zsh => Zsh,
            RemoteShellKind.Fish => Fish,
            RemoteShellKind.PosixSh => PosixSh,
            _ => string.Empty
        };

    /// <summary>
    /// 组装一次完整的注入:<c>安装 → 哨兵 → 先报一次</c>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>三段的顺序是有讲究的</b>(拼装由 <see cref="SilentCommand.Build" /> 负责):
    /// </para>
    /// <list type="number">
    /// <item><b>安装</b>排最前(作为 <c>hidden</c>):它是唯一可能炸的一段,炸出来的东西全落在窗口里。</item>
    /// <item><b>哨兵</b>排中间:注入窗口看见它就闭合。<b>无论安装成功与否它都会打印</b> ——
    /// 窗口的职责是"让屏幕干净",不是"判断成败",把两件事绑在一起会让失败的会话白白多黑屏两秒。
    /// 成败另有判据:之后有没有真收到过 cwd 上报。</item>
    /// <item><b>先报一次</b>排最后(作为 <c>visible</c>):它必须落在哨兵<b>之后</b>,
    /// 否则会连同安装的噪声一起被吞掉 —— 而 fish 那段是挂在 <c>PWD</c> 变化上的,
    /// 吞了这一次,文件浏览器就得等用户第一次 <c>cd</c> 才有目录可跟。</item>
    /// </list>
    /// </remarks>
    /// <param name="kind">探针结论;认不出来时返回 <see cref="ShellIntegrationInjection.None" />。</param>
    public static ShellIntegrationInjection Build(RemoteShellKind kind) =>
        SilentCommand.Build(kind, For(kind), For(kind).Length == 0 ? null : FunctionName);

    /// <summary>
    /// tmux 透传:在 tmux 里时<b>额外</b>再发一份包进 DCS 的拷贝。
    /// </summary>
    /// <param name="quote">该 shell 下 printf 格式串用的引号(bash/zsh 用双引号,sh 用单引号)。</param>
    /// <remarks>
    /// <para>
    /// tmux 自己会<b>吃掉</b> OSC 7(拿去记 pane 的 cwd)而默认不转发给外层终端
    /// (<c>tmux/tmux#3127</c>)—— 用户一进 tmux,跟随就停。把序列包进
    /// <c>ESC P tmux; … ESC \</c> 才能穿过去,且需要用户开 <c>set -g allow-passthrough on</c>。
    /// </para>
    /// <para>
    /// <b>是"额外再发一份"而不是"改发包过的"</b>:裸的那份要留给 tmux 自己用,
    /// 不然 tmux 的 <c>#{pane_current_path}</c> 就瞎了 —— 修好自己却弄坏别人不叫修好。
    /// </para>
    /// <para>
    /// DCS 只能由 ST(<c>ESC \</c>)收尾,BEL 不行;载荷里的每个 ESC 还必须加倍。
    /// 于是这里必须直面那个反斜杠:printf 的格式串里要的是字面量 <c>\033\\</c>,
    /// 而 bash / zsh 的<b>双引号</b>会把 <c>\\\\</c> 折成 <c>\\</c>,POSIX sh 的<b>单引号</b>
    /// 则原样保留 —— 所以同一段语义在两种引号下要写成不同的字数,由这个参数决定。
    /// </para>
    /// </remarks>
    private static string TmuxTail(string quote)
    {
        // 单引号里反斜杠原样保留,双引号里要写两倍。
        string backslash = quote == "'" ? @"\\" : @"\\\\";
        string format =
            @"\033Ptmux;\033\033]7;file://%s%s\007\033" + backslash
            + @"\033Ptmux;\033\033]633;P;Cwd=%s\007\033" + backslash;
        return $"test -n \"${{TMUX:-}}\" && printf {quote}{format}{quote} \"${{HOSTNAME:-}}\" \"$PWD\" \"$PWD\";";
    }

    /// <summary>tmux 透传的 fish 版。</summary>
    /// <remarks>
    /// fish 的单引号只对 <c>\'</c> 与 <c>\\</c> 特殊:<c>\\\\</c> 会被折成 <c>\\</c>,
    /// printf 再折成一个反斜杠 —— 与 bash 的双引号<b>恰好同字数</b>,但成因完全不同,
    /// 所以还是分开写,免得哪天改动其一时误以为两边永远一致。
    /// </remarks>
    private static string TmuxTailFish()
    {
        const string format =
            @"\033Ptmux;\033\033]7;file://%s%s\007\033\\\\"
            + @"\033Ptmux;\033\033]633;P;Cwd=%s\007\033\\\\";
        return $"if set -q TMUX; printf '{format}' \"$hostname\" \"$PWD\" \"$PWD\"; end;";
    }
}

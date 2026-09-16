using System.Collections.Concurrent;

namespace VelaShell.Core.Ssh;

/// <summary>
/// 远端默认 shell 的探针:回答「这台机器的交互式 shell 是<b>哪一种</b>」,
/// 从而决定注入 <see cref="ShellIntegrationScript" /> 里的哪一段目录上报脚本,或者干脆不注入。
/// <para>
/// 目录上报(OSC 7)是「SFTP 文件浏览器跟随终端目录」的数据源。它曾经是**盲注**的:
/// 连上就写进去,对端是什么 shell 无所谓。Windows OpenSSH 的默认 shell 是 cmd.exe,
/// 那一整行于是被当成一条命令执行,屏幕上留下 <c>'test' 不是内部或外部命令</c>(#305);
/// PowerShell 作默认 shell 同理。脚本里那个 <c>test -n "$BASH_VERSION"</c> 守卫只挡得住
/// fish/csh 这类**POSIX 世界内部**的差异,挡不住根本不认 sh 语法的 shell。
/// </para>
/// <para>
/// 所以先问一句再注入。探针走**独立的 exec 通道**,不碰用户的交互式 shell,终端里一个字符都看不见;
/// 用完即关,排在开交互 shell 之前,连 <c>MaxSessions 1</c> 的服务端也只需要一个通道名额。
/// 代价是每台主机的**首次**连接多一次往返 —— 结论按主机缓存,重连与新标签直接取用。
/// </para>
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么是两道探针,而不是一条命令全搞定。</b>要同时做到「认出 fish」与「认不出
/// PowerShell」,一条命令做不到:
/// </para>
/// <list type="bullet">
/// <item>挡住 PowerShell 靠的是 <c>${var:-默认值}</c> 这种只有 POSIX 才做的展开
/// (见 <see cref="PosixProbeCommand" />)—— 而 fish 对 <c>${…}</c> 在<b>解析期</b>就报错,
/// 整条命令一个字节都不会输出。</item>
/// <item>反过来,能让 fish 开口的写法(纯 <c>$变量</c>)PowerShell 也照做不误
/// (它同样有 <c>$SHELL</c>、<c>$PWD</c>),于是一台装了 Git for Windows 的
/// PowerShell 机器会被认成 POSIX shell —— 正是 #305 要防的那一种。</item>
/// </list>
/// <para>
/// 于是拆成两步:先跑 POSIX 那条(bash / zsh / dash / ash / ksh 全在这一网里,顺带把种类
/// 一起带回来),<b>只有它失败时</b>才追问一句 fish。fish 用户多付一次往返,而且只在首连 ——
/// 换来的是 PowerShell 那条线一步都没松。
/// </para>
/// </remarks>
public static class RemoteShellProbe
{
    /// <summary>
    /// 第一道探针。要求三件只有 POSIX shell 才同时做得到的事:有 <c>printf</c>、
    /// 会做 <c>$((...))</c> 算术展开、认 <c>${var:-默认值}</c> 的默认值展开。
    /// 三样凑齐才拼得出 <see cref="PosixMarker" />(实测 bash/zsh/dash/sh 均通过):
    /// <list type="bullet">
    /// <item>cmd.exe:<c>'printf' 不是内部或外部命令</c>,退出码非 0。</item>
    /// <item>PowerShell:找不到 printf 命令,退出码非 0。</item>
    /// <item>cmd.exe 而 PATH 上恰好有 MSYS/Git 的 printf.exe(常见于装了 Git for Windows 的机器):
    /// 命令跑通了,但 cmd 两种展开都不做,打出的是字面量 —— 标记对不上,仍判为非 POSIX。</item>
    /// <item>PowerShell 而 PATH 上有 printf.exe:PowerShell 的 <c>$(...)</c> 子表达式**会**把
    /// <c>$((6*7))</c> 算成 42,但 <c>${vela_probe_ok:-ok}</c> 被它当成驱动器限定的变量名而展开为空 ——
    /// 少了后半截,标记同样对不上。两种展开缺一不可,正是为了堵住这一条。</item>
    /// <item>fish/csh 不支持这两种展开,在这一道一并落选,由 <see cref="FishProbeCommand" /> 收。</item>
    /// </list>
    /// </summary>
    /// <remarks>
    /// 标记后面那两段是**种类**:<c>$BASH_VERSION</c> 与 <c>$ZSH_VERSION</c>,谁非空就是谁。
    /// 两个都空 = dash / ash / ksh 之流,归 <see cref="RemoteShellKind.PosixSh" />。
    /// 用版本变量而不是 <c>$0</c> 判种类,是因为 <c>$0</c> 在登录 shell 上常带个前导连字符
    /// (<c>-bash</c>)、在 <c>exec</c> 通道里又可能是脚本名,不如版本变量直截了当。
    /// 分隔符用冒号:bash 的版本串长这样 <c>5.2.21(1)-release</c>,里面没有冒号。
    /// </remarks>
    public const string PosixProbeCommand =
        """
        printf 'vela-posix-%s%s:%s:%s\n' "$((6*7))" "${vela_probe_ok:-ok}" "${BASH_VERSION:-}" "${ZSH_VERSION:-}"
        """;

    /// <summary>
    /// 第二道探针,只在第一道失败后才发:对端是不是 fish。
    /// </summary>
    /// <remarks>
    /// 通篇只用 <c>$变量</c>,fish 解析得动。三种"不是 fish"的回答各自都对不上号:
    /// <list type="bullet">
    /// <item>cmd.exe:没有变量展开,原样打出 <c>vela-fish-$FISH_VERSION</c> —— 含 <c>$</c>,判否。</item>
    /// <item>PowerShell:<c>$FISH_VERSION</c> 展开为空,打出 <c>vela-fish-</c> —— 版本为空,判否。</item>
    /// <item>bash/zsh/dash:同样展开为空 —— 但它们在第一道就已经认出来了,走不到这里。</item>
    /// </list>
    /// 也就是说:**必须打出一个既非空、又不含 <c>$</c> 的版本号**才算 fish。
    /// </remarks>
    public const string FishProbeCommand =
        """
        echo vela-fish-$FISH_VERSION
        """;

    /// <summary>第一道探针输出里必须原样出现的标记(两种展开都做对了才拼得出来)。</summary>
    public const string PosixMarker = "vela-posix-42ok";

    /// <summary>第二道探针输出里的前缀;后面必须跟一个真的版本号。</summary>
    public const string FishMarker = "vela-fish-";

    /// <summary>
    /// 探针超时。给足一次 exec 通道往返即可;超时按「探不到」处理 —— 宁可丢掉目录跟随,
    /// 也不能把 sh 代码糊到一个不认它的 shell 上。
    /// </summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);

    /// <summary>主机 → 探测结论。同一台机器只问一次,重连与新标签直接取缓存。</summary>
    private static readonly ConcurrentDictionary<string, RemoteShellKind> Results =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 缓存键:同一主机换了用户可能换了默认 shell,故用户名也进键。
    /// 主机名为空(拿不到会话配置)时返回空串 = 本次不缓存,免得几个来路不明的连接
    /// 共用同一格,互相顶掉对方的结论。
    /// </summary>
    public static string CacheKey(string? host, int port, string? user) =>
        string.IsNullOrWhiteSpace(host) ? string.Empty : $"{user ?? string.Empty}@{host}:{port}";

    /// <summary>
    /// 判定一次 POSIX 探针执行的结果。退出码必须为 0 <b>且</b>标准输出里出现标记 ——
    /// 只看其一都不够:ForceCommand 会让退出码为 0 但输出的是别的东西,
    /// 而 cmd.exe 把命令原样回显时标记也对不上。
    /// </summary>
    public static bool IsPosixShell(RemoteCommandResult? result) =>
        result is { IsSuccess: true } && result.StandardOutput.Contains(PosixMarker, StringComparison.Ordinal);

    /// <summary>
    /// 把第一道探针的输出翻成 shell 种类。标记对不上 = <see cref="RemoteShellKind.Unknown" />
    /// (还要再问一句 fish,所以此处<b>不能</b>直接下 <see cref="RemoteShellKind.NonPosix" /> 的结论)。
    /// </summary>
    /// <remarks>
    /// 标记之后按冒号切两段:第一段非空 → bash,第二段非空 → zsh,都空 → 其余 POSIX shell。
    /// 先判 bash 再判 zsh 只是个固定次序,现实里两者不会同时非空。
    /// 整行可能混在 MOTD 之类的噪声里,所以是**按行找**标记,而不是要求整段输出以它开头。
    /// </remarks>
    public static RemoteShellKind ClassifyPosixProbe(RemoteCommandResult? result)
    {
        if (!IsPosixShell(result))
        {
            return RemoteShellKind.Unknown;
        }
        foreach (string line in result!.StandardOutput.Split('\n'))
        {
            int marker = line.IndexOf(PosixMarker, StringComparison.Ordinal);
            if (marker < 0)
            {
                continue;
            }
            string[] fields = line[(marker + PosixMarker.Length)..].Trim().Split(':');
            if (fields.Length > 1 && fields[1].Length > 0)
            {
                return RemoteShellKind.Bash;
            }
            return fields.Length > 2 && fields[2].Length > 0 ? RemoteShellKind.Zsh : RemoteShellKind.PosixSh;
        }
        return RemoteShellKind.PosixSh;
    }

    /// <summary>
    /// 判定第二道探针的结果:是不是 fish。要求标记后面跟着一个<b>既非空、又不含 <c>$</c></b>
    /// 的版本号 —— 前者排掉 PowerShell(展开成空),后者排掉 cmd.exe(原样回显)。
    /// </summary>
    public static bool IsFishShell(RemoteCommandResult? result)
    {
        if (result is not { IsSuccess: true })
        {
            return false;
        }
        foreach (string line in result.StandardOutput.Split('\n'))
        {
            int marker = line.IndexOf(FishMarker, StringComparison.Ordinal);
            if (marker < 0)
            {
                continue;
            }
            string version = line[(marker + FishMarker.Length)..].Trim();
            if (version.Length > 0 && !version.Contains('$', StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 探测远端 shell 种类(带缓存)。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>只有确定的结论才进缓存。</b><see cref="RemoteShellKind.Unknown" /> 是环境噪声
    /// (exec 被禁、通道开不出来、超时、连接已断)而不是「这台机器就是这样」的结论,
    /// 下次连接值得再问一次;缓存下来则意味着这台机器**这辈子**都不会再有目录跟随。
    /// </para>
    /// <para>
    /// 两道探针之间是短路关系:第一道认出来了就不发第二道 —— 绝大多数机器(Linux 服务器)
    /// 都在第一道结束,只有 fish 用户和 Windows 机器会多付一次往返。
    /// </para>
    /// </remarks>
    /// <param name="client">已连接的 SSH 客户端。</param>
    /// <param name="cacheKey">见 <see cref="CacheKey" />;空串表示不缓存。</param>
    /// <param name="cancellationToken">取消令牌(连接被取消时一并放弃探测)。</param>
    public static async Task<RemoteShellKind> DetectAsync(
        ISshClientWrapper client,
        string cacheKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        if (cacheKey.Length > 0 && Results.TryGetValue(cacheKey, out RemoteShellKind cached))
        {
            return cached;
        }
        RemoteShellKind kind = await RunProbesAsync(client, cancellationToken).ConfigureAwait(false);
        if (cacheKey.Length > 0 && kind != RemoteShellKind.Unknown)
        {
            Results[cacheKey] = kind;
        }
        return kind;
    }

    /// <summary>
    /// 依次跑两道探针。任何失败 —— exec 被禁、通道开不出来、超时、连接已断 ——
    /// 一律返回 <see cref="RemoteShellKind.Unknown" />(调用方据此不注入,且不缓存)。
    /// </summary>
    private static async Task<RemoteShellKind> RunProbesAsync(
        ISshClientWrapper client,
        CancellationToken cancellationToken)
    {
        try
        {
            RemoteCommandResult? posixResult =
                await RunAsync(client, PosixProbeCommand, cancellationToken).ConfigureAwait(false);
            RemoteShellKind posix = ClassifyPosixProbe(posixResult);
            if (posix != RemoteShellKind.Unknown)
            {
                return posix;
            }

            // 第一道没认出来:可能是 fish(它对 ${…} 解析期就报错),也可能是 cmd/PowerShell。
            // 问一句 fish —— 认出来就按 fish 注入。
            RemoteCommandResult? fishResult =
                await RunAsync(client, FishProbeCommand, cancellationToken).ConfigureAwait(false);
            if (IsFishShell(fishResult))
            {
                return RemoteShellKind.Fish;
            }

            // **两道都连结果都没拿到 = 没人回答,不是"回答了但不对"。**
            // 前者是噪声(通道开不出来、包装层返回 null),该按 Unknown 处理、不进缓存;
            // 后者才是结论(cmd.exe 把命令原样回显、PowerShell 报找不到 printf),记作 NonPosix。
            // 混为一谈的代价不只是少一次重试:NonPosix 还会连带关掉摘历史前缀
            // (见 ShellHistoryScrub.SupportedBy),让一次网络抖动把这台机器的注入行
            // 永久留在了命令历史里。
            return posixResult is null && fishResult is null
                ? RemoteShellKind.Unknown
                : RemoteShellKind.NonPosix;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[VelaShell] Remote shell probe failed: {ex.Message}");
            return RemoteShellKind.Unknown;
        }
    }

    /// <summary>跑一条探针命令,带独立超时(超时抛出,由上层统一收成 Unknown)。</summary>
    private static async Task<RemoteCommandResult?> RunAsync(
        ISshClientWrapper client,
        string command,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ProbeTimeout);
        return await client.RunCommandDetailedAsync(command, timeout.Token).ConfigureAwait(false);
    }

    /// <summary>
    /// 「对端能不能吃 POSIX 语法」—— 远端进程列表、资源指标那些采集命令的前置闸
    /// (它们在 cmd.exe 上会每秒起一个进程、吐一堆假数据,#305 同源)。
    /// </summary>
    /// <remarks>
    /// <b>fish 在这里算 false</b>,和拆分成枚举之前的行为逐字一致。理由不是"fish 不好",
    /// 而是那些采集命令通篇写着 <c>${var:-}</c>、<c>$(( ))</c> 这类 fish 不认的展开 ——
    /// 目录上报脚本能为 fish 单独写一段,采集命令没人为它重写过。
    /// 让它在这里返回 true,只会把 #305 换一种 shell 重演一遍。
    /// </remarks>
    public static async Task<bool> IsPosixShellAsync(
        ISshClientWrapper client,
        string cacheKey,
        CancellationToken cancellationToken = default) =>
        await DetectAsync(client, cacheKey, cancellationToken).ConfigureAwait(false)
            is RemoteShellKind.Bash or RemoteShellKind.Zsh or RemoteShellKind.PosixSh;

    /// <summary>清空缓存(单元测试用;主机换了默认 shell 时重启应用即可)。</summary>
    public static void ClearCache() => Results.Clear();
}

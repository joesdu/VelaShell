using VelaShell.Core.Models;
using VelaShell.Core.Resources;
using VelaShell.Core.Ssh;
using VelaShell.Ssh.Crypto;

namespace VelaShell.Infrastructure.Ssh;

/// <summary>允许自定义算法清单的四个类别。压缩由独立的按连接选项控制。</summary>
public enum SshAlgorithmKind
{
    /// <summary>密钥交换。</summary>
    KeyExchange,

    /// <summary>主机密钥。</summary>
    HostKey,

    /// <summary>加密(两个方向共用)。</summary>
    Cipher,

    /// <summary>MAC(两个方向共用)。</summary>
    Mac,
}

/// <summary>
/// 连接配置里的算法偏好(<see cref="SshSessionOptions" /> 的老算法开关与四个自定义清单)→ 库的 <see cref="SshAlgorithmSet" />。
/// </summary>
/// <remarks>
/// <para>
/// 自定义清单沿用 OpenSSH <c>ssh_config</c> 的写法,运维照着 <c>~/.ssh/config</c> 里那一行抄过来就能用:
/// <c>+a,b</c> 追加到默认之后、<c>-a,b</c> 从默认里删掉(可带 <c>*</c> / <c>?</c> 通配)、
/// <c>^a,b</c> 提到最前,不带前缀则整个替换。
/// </para>
/// <para>
/// 「能写哪些名字」以库的算法目录为准(<see cref="SshAlgorithmCatalog" />):实现了的照常收,
/// 常见却没实现的说「没实现」(写了也谈不成),都不是的说「不认识」。
/// 不在其中的一律当场报出来 —— 库在拨号前也会拒掉没实现的名字,但那时的错误指向一次连接,而不是这条配置。
/// 曾经宿主用 <c>Default.WithLegacyInterop()</c> 推算实现了哪些,再手工维护一份「认得但没实现」的名单。
/// </para>
/// </remarks>
public static class SshAlgorithmPreferences
{
    private static readonly SshAlgorithmSet Legacy = SshAlgorithmSet.Default.WithLegacyInterop();

    /// <summary>本版实现了、可以写进这一类清单的算法。</summary>
    public static IReadOnlyList<string> Available(SshAlgorithmKind kind) => SshAlgorithmCatalog.Implemented(CategoryOf(kind));

    /// <summary>宿主的类别对到库的目录类别。</summary>
    private static SshAlgorithmCategory CategoryOf(SshAlgorithmKind kind) => kind switch
    {
        SshAlgorithmKind.KeyExchange => SshAlgorithmCategory.KeyExchange,
        SshAlgorithmKind.HostKey => SshAlgorithmCategory.HostKey,
        SshAlgorithmKind.Cipher => SshAlgorithmCategory.Encryption,
        _ => SshAlgorithmCategory.Mac,
    };

    /// <summary>不写自定义清单时这一类用的清单。</summary>
    /// <param name="kind">类别。</param>
    /// <param name="legacy">是否放开了老算法。</param>
    public static IReadOnlyList<string> Defaults(SshAlgorithmKind kind, bool legacy) =>
        Select(legacy ? Legacy : SshAlgorithmSet.Default, kind);

    /// <summary>校验一条写法;成立返回 <see langword="null" />,否则返回界面语言的一句原因。</summary>
    public static string? Validate(SshAlgorithmKind kind, string? spec, bool legacy) =>
        TryApply(kind, spec, legacy, out _, out string? error) ? null : error;

    /// <summary>按 OpenSSH 的写法把一条自定义清单作用到默认清单上。</summary>
    /// <param name="kind">类别。</param>
    /// <param name="spec">写法;空 = 用默认。</param>
    /// <param name="legacy">是否放开了老算法(决定「默认」是哪一份)。</param>
    /// <param name="result">得到的清单。</param>
    /// <param name="error">不成立时的原因(界面语言)。</param>
    public static bool TryApply(SshAlgorithmKind kind, string? spec, bool legacy,
        out IReadOnlyList<string> result, out string? error)
    {
        // 写法的解析在 SSH 库里(SshAlgorithmSpec,ssh_config 的导入用的是同一份);这里只把原因翻成界面语言。
        IReadOnlyList<string> defaults = Defaults(kind, legacy);
        try
        {
            result = SshAlgorithmSpec.Apply(CategoryOf(kind), spec, defaults);
            error = null;
            return true;
        }
        catch (SshAlgorithmSpecException ex)
        {
            result = defaults;
            error = ex.Problem switch
            {
                SshAlgorithmSpecProblem.Empty => Strings.Get("Ssh_AlgoSpecEmpty"),
                SshAlgorithmSpecProblem.Unimplemented => Strings.Format("Ssh_AlgoSpecUnimplemented", ex.Name ?? ""),
                SshAlgorithmSpecProblem.NothingLeft => Strings.Get("Ssh_AlgoSpecNothingLeft"),
                _ => Strings.Format("Ssh_AlgoSpecUnknown", ex.Name ?? ""),
            };
            return false;
        }
    }

    /// <summary>
    /// 把从别处导入的一条写法(<c>~/.ssh/config</c> 的 <c>Ciphers</c> 等)收拾成本版用得了的:
    /// OpenSSH 认得、本版没实现的名字(CBC、3des、group1……)逐个去掉,其余照原样留着。
    /// </summary>
    /// <param name="kind">类别。</param>
    /// <param name="spec">原样的写法。</param>
    /// <param name="dropped">去掉了哪些名字。</param>
    /// <param name="error">整条用不了时的原因(界面语言);用得了时为 <see langword="null" />。</param>
    /// <returns>收拾好的写法;整条用不了(拼错的名字、去掉之后一个不剩)时为 <see langword="null" />。</returns>
    /// <remarks>
    /// 去掉没实现的名字不改变这条写法能谈成的结果 —— 写着它们本来也谈不成;而整条丢掉就成了「用默认」,
    /// 那正是写这一行的人想避开的。拼错的名字不猜:整条不导,原因进导入预览。
    /// </remarks>
    public static string? SanitizeImported(SshAlgorithmKind kind, string? spec,
        out IReadOnlyList<string> dropped, out string? error)
    {
        List<string> removed = [];
        dropped = removed;
        error = null;
        string? current = spec?.Trim();
        if (string.IsNullOrEmpty(current))
        {
            return null;
        }

        IReadOnlyList<string> defaults = Defaults(kind, legacy: false);
        while (true)
        {
            try
            {
                _ = SshAlgorithmSpec.Apply(CategoryOf(kind), current, defaults);
                return current;
            }
            catch (SshAlgorithmSpecException ex) when (ex.Problem == SshAlgorithmSpecProblem.Unimplemented && ex.Name is { Length: > 0 } name)
            {
                string prefix = current[0] is '+' or '-' or '^' ? current[..1] : string.Empty;
                string[] names = current[prefix.Length..].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                string[] kept = [.. names.Where(n => !string.Equals(n, name, StringComparison.Ordinal))];
                if (kept.Length == names.Length)
                {
                    // 库点名的不在清单里(不会发生,防止死循环)。
                    error = Strings.Format("Ssh_AlgoSpecUnimplemented", name);
                    return null;
                }
                removed.Add(name);
                if (kept.Length == 0)
                {
                    // 只写了本版没实现的那几个:留下一个空前缀等于什么都没写,不如说清楚。
                    error = Strings.Format("Ssh_AlgoSpecUnimplemented", string.Join(", ", removed));
                    return null;
                }
                current = prefix + string.Join(',', kept);
            }
            catch (SshAlgorithmSpecException)
            {
                TryApply(kind, current, legacy: false, out _, out error);
                return null;
            }
        }
    }

    /// <summary>按配置装出库的算法清单。</summary>
    /// <exception cref="VelaSshConnectionException">
    /// 某个自定义清单不成立。界面保存前已经挡过,走到这里的是手改过、或由别处导入的配置。
    /// </exception>
    public static SshAlgorithmSet Build(SshSessionOptions? options)
    {
        if (options is null)
        {
            return SshAlgorithmSet.Default;
        }
        bool legacy = options.LegacyAlgorithms;
        IReadOnlyList<string> cipher = Apply(SshAlgorithmKind.Cipher, options.Ciphers, legacy);
        IReadOnlyList<string> mac = Apply(SshAlgorithmKind.Mac, options.Macs, legacy);
        SshAlgorithmSet algorithms = (legacy ? Legacy : SshAlgorithmSet.Default) with
        {
            KeyExchange = Apply(SshAlgorithmKind.KeyExchange, options.KexAlgorithms, legacy),
            HostKey = Apply(SshAlgorithmKind.HostKey, options.HostKeyAlgorithms, legacy),
            EncryptionClientToServer = cipher,
            EncryptionServerToClient = cipher,
            MacClientToServer = mac,
            MacServerToClient = mac,
        };
        return options.TerminalCompression switch
        {
            SshTerminalCompressionMode.UploadOnly => algorithms.WithCompression(),
            SshTerminalCompressionMode.Bidirectional => algorithms.WithCompression(allowServerCompression: true),
            _ => algorithms,
        };
    }

    /// <summary>这一类在界面上的名字(复用协商失败诊断里的那几个词)。</summary>
    public static string Label(SshAlgorithmKind kind) => kind switch
    {
        SshAlgorithmKind.KeyExchange => Strings.Get("Ssh_AlgoKindKex"),
        SshAlgorithmKind.HostKey => Strings.Get("Ssh_AlgoKindHostKey"),
        SshAlgorithmKind.Cipher => Strings.Get("Ssh_AlgoKindEncryption"),
        _ => Strings.Get("Ssh_AlgoKindMac"),
    };

    private static IReadOnlyList<string> Apply(SshAlgorithmKind kind, string? spec, bool legacy) =>
        TryApply(kind, spec, legacy, out IReadOnlyList<string> result, out string? error)
            ? result
            : throw new VelaSshConnectionException(Strings.Format("Ssh_AlgoSpecInvalid", Label(kind), error));

    private static IReadOnlyList<string> Select(SshAlgorithmSet set, SshAlgorithmKind kind) => kind switch
    {
        SshAlgorithmKind.KeyExchange => set.KeyExchange,
        SshAlgorithmKind.HostKey => set.HostKey,
        SshAlgorithmKind.Cipher => set.EncryptionClientToServer,
        _ => set.MacClientToServer,
    };

}

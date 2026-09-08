namespace VelaShell.Terminal.Emulation;

/// <summary>
/// OSC 8 显式超链接的驻留表:把 <c>(id, URI)</c> 折叠成一个 <see cref="ushort" /> 句柄,
/// 单元格只存句柄。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么不把 URI 直接放进单元格。</b><see cref="TerminalCell" /> 是 16 字节的 blittable
/// 结构,不含任何托管引用 —— 回滚缓冲每标签页上百万格,一个引用字段就让 GC 逐格扫描
/// (见 <c>TerminalCellMemoryTests</c> 守住的两条契约)。句柄走的是与
/// <see cref="CombiningPool" /> 完全相同的路子:引用数据外置,格内只留索引。
/// </para>
/// <para>
/// <b>句柄 0 恒为「无链接」</b>,因此新分配的行/擦除后的格天然不带链接,不必额外初始化。
/// </para>
/// <para>
/// <b>去重键是 <c>id + '\0' + URI</c>,不是单独的 URI。</b>OSC 8 的 <c>id=</c> 参数用来把被
/// 换行拆开的同一条链接认回同一条,规范也允许两条 URI 相同但 id 不同的链接;
/// 按组合键去重,句柄相等即"同一条链接"。
/// </para>
/// <para>
/// <b>只在 UI 线程访问</b>(仿真器的 Feed 由 <c>SshTerminalBridge.FlushPending</c> 编组到
/// UI 线程,渲染也在 UI 线程),故不加锁 —— 与 <see cref="TerminalScreen" /> 同一线程模型。
/// </para>
/// </remarks>
public sealed class HyperlinkTable
{
    /// <summary>
    /// 表的容量上限;超出后新链接一律退化为「无链接」(仍原样显示文本,只是点不开)。
    /// </summary>
    /// <remarks>
    /// 终端输出是不可信输入,而这张表没有引用计数 —— 落在回滚区里的格随时可能引用任意一条
    /// 旧链接,没法安全地回收单条。上限即是它的内存账:4096 条 × 平均百来字节 ≈ 数百 KB,
    /// 一个标签页封顶。真要撞上限的只有 <c>ls --hyperlink</c> 刷十万个文件这类场景,
    /// 那时新链接不可点,文本本身一个字不少。RIS(硬复位)会整表清空。
    /// </remarks>
    public const int MaxEntries = 4096;

    /// <summary>单条 URI 的长度上限。OSC 8 规范建议终端至少支持 2083 字符,这里取整为上限。</summary>
    public const int MaxUriLength = 2083;

    /// <summary>
    /// 允许被驻留的 URI scheme。
    /// </summary>
    /// <remarks>
    /// <b>白名单而非黑名单</b>:OSC 8 的 URI 来自远端输出 —— 一段不可信的字节流 ——
    /// 而点开它走的是系统 shell 关联(<c>Launcher.LaunchUriAsync</c>)。放开 scheme 等于把
    /// "远端能让本机 ShellExecute 任意字符串"送出门。
    /// <para>
    /// <c>file:</c> 刻意不在列内:VelaShell 主要是 SSH 客户端,<c>ls --hyperlink</c> 报的是
    /// <b>远端</b>路径,在本机打开只会指向一个同名的本地文件(或 UNC 路径上的可执行文件);
    /// 既没有正确语义,又正好是这条攻击面上最危险的一格。
    /// </para>
    /// </remarks>
    private static readonly string[] AllowedSchemes = ["http", "https", "ftp", "ftps", "mailto"];

    private readonly Dictionary<string, ushort> _handleByKey = [with(StringComparer.Ordinal)];

    /// <summary>下标 <c>i</c> 对应句柄 <c>i + 1</c>(句柄 0 保留给「无链接」)。</summary>
    private readonly List<string> _uris = [];

    /// <summary>当前已驻留的链接条数。</summary>
    public int Count => _uris.Count;

    /// <summary>
    /// 驻留一条 OSC 8 链接,返回其句柄;URI 不合法、scheme 不在白名单内或表已满时返回 0
    /// (= 无链接)。
    /// </summary>
    /// <param name="id">OSC 8 参数里的 <c>id=</c> 值;没有则传空。</param>
    /// <param name="uri">链接目标。</param>
    public ushort Intern(string? id, string? uri)
    {
        if (!IsAcceptable(uri))
        {
            return 0;
        }
        string key = $"{id}\0{uri}";
        if (_handleByKey.TryGetValue(key, out ushort existing))
        {
            return existing;
        }
        if (_uris.Count >= MaxEntries)
        {
            return 0;
        }
        _uris.Add(uri!);
        ushort handle = (ushort)_uris.Count; // 1 基:句柄 0 是「无链接」
        _handleByKey[key] = handle;
        return handle;
    }

    /// <summary>返回句柄对应的 URI;句柄为 0 或越界时返回 null。</summary>
    public string? UriOf(ushort handle) =>
        handle != 0 && handle <= _uris.Count ? _uris[handle - 1] : null;

    /// <summary>清空整张表(RIS 硬复位)。</summary>
    public void Clear()
    {
        _handleByKey.Clear();
        _uris.Clear();
    }

    /// <summary>URI 是否可被驻留:长度合规、不含控制字符、scheme 在白名单内且能被解析为绝对 URI。</summary>
    private static bool IsAcceptable(string? uri)
    {
        if (uri is not { Length: > 0 } || uri.Length > MaxUriLength)
        {
            return false;
        }
        foreach (char c in uri)
        {
            // 控制字符不可能出现在合法 URI 里,却是终端注入类花招的常客(把提示气泡里的地址
            // 撑成另一副样子)。整条丢弃,不做净化 —— 净化过的地址与用户看到的不再是同一条。
            if (char.IsControl(c))
            {
                return false;
            }
        }
        int colon = uri.IndexOf(':');
        if (colon <= 0)
        {
            return false;
        }
        ReadOnlySpan<char> scheme = uri.AsSpan(0, colon);
        bool allowed = false;
        foreach (string candidate in AllowedSchemes)
        {
            if (scheme.Equals(candidate, StringComparison.OrdinalIgnoreCase))
            {
                allowed = true;
                break;
            }
        }
        return allowed && Uri.TryCreate(uri, UriKind.Absolute, out _);
    }
}

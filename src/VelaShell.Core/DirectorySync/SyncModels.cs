using VelaShell.Core.Models;

namespace VelaShell.Core.DirectorySync;

/// <summary>目录同步的方向(WinSCP「同步」对话框的三个方向)。</summary>
public enum SyncDirection
{
    /// <summary>双向:两边各自较新的一方覆盖另一方,缺失的互相补齐;不删除任何东西。</summary>
    Both,

    /// <summary>本地 → 远端:远端向本地看齐。</summary>
    ToRemote,

    /// <summary>远端 → 本地:本地向远端看齐。</summary>
    ToLocal,
}

/// <summary>目录同步的模式。</summary>
public enum SyncMode
{
    /// <summary>同步文件:只用较新的(或大小不同的)源文件覆盖目标,目标较新的不动。</summary>
    Synchronize,

    /// <summary>镜像文件:只要不同就用源覆盖目标,哪怕目标更新。仅单向可用。</summary>
    Mirror,

    /// <summary>仅同步时间戳:不传内容,把两边都有、大小相同但时间不同的文件的目标时间改成源时间。仅单向可用。</summary>
    Timestamps,
}

/// <summary>判定两边同名文件「不同」的依据。</summary>
[Flags]
public enum SyncCriteria
{
    /// <summary>不比内容:只看两边有没有(缺失的补齐)。</summary>
    None = 0,

    /// <summary>比修改时间。</summary>
    Time = 1,

    /// <summary>比文件大小。</summary>
    Size = 2,

    /// <summary>
    /// 两边都有、大小相同的文件先比 SHA-256:相同即视为同一文件(不看时间),不同再用时间判断哪边较新。
    /// 算不出摘要的(服务器不支持、出错、单个文件读不了)回退到 <see cref="Time" /> 与 <see cref="Size" />。
    /// </summary>
    Checksum = 4,
}

/// <summary>
/// 一个时间戳可信到哪一位。FTP 的 Unix LIST 只给到分钟,半年以前的文件只给到日期;
/// 拿秒级的本地时间去比一个分钟级的远端时间,几乎每个文件都会被判成「不同」。
/// 比较时两边一律截到较粗的那一级。
/// </summary>
public enum SyncTimePrecision
{
    /// <summary>精确到秒。</summary>
    Second,

    /// <summary>只精确到分钟(秒恒为 0)。</summary>
    Minute,

    /// <summary>只精确到日期(时分秒恒为 0)。</summary>
    Day,

    /// <summary>后端没给时间,时间依据对这一项不起作用。</summary>
    Unknown,
}

/// <summary>扫描得到的一个条目:路径相对于同步根,分隔符恒为 <c>/</c>。</summary>
/// <param name="RelativePath">相对于同步根的路径(不以 <c>/</c> 开头),两边用它配对。</param>
/// <param name="FullPath">本地绝对路径或远端绝对路径。</param>
/// <param name="IsDirectory">是否目录。</param>
/// <param name="Size">文件大小(字节);目录为 0。</param>
/// <param name="LastWriteTimeUtc">修改时间(UTC)。</param>
/// <param name="Precision">该时间可信到哪一位。</param>
public sealed record SyncItem(
    string RelativePath,
    string FullPath,
    bool IsDirectory,
    long Size,
    DateTime LastWriteTimeUtc,
    SyncTimePrecision Precision = SyncTimePrecision.Second)
{
    /// <summary>最后一段名称。</summary>
    public string Name => RelativePath[(RelativePath.LastIndexOf('/') + 1)..];

    /// <summary>
    /// 内容的 SHA-256(小写十六进制)。只有校验阶段在两边都算出来时才有值;null 表示按大小与修改时间比较。
    /// </summary>
    public string? Sha256 { get; init; }

    /// <summary>
    /// 这条时间是按哪个 UTC 偏移的日历给出的;null = 本机时区。只有 FTP 的 LIST 时间、且连接配置里设了服务器时区时才有值。
    /// </summary>
    /// <remarks>
    /// 半年以前的文件 LIST 只给日期(<see cref="SyncTimePrecision.Day" />),那是<b>服务器当地</b>的日期。
    /// 按日比较时两边都要放到这本日历里截断,否则服务器在 UTC、本机在 UTC+8 时,
    /// 晚上八点以后改的文件在两边落在相邻的两天,被判成「较新」(见 <see cref="SyncTime.Compare" />)。
    /// </remarks>
    public TimeSpan? CalendarOffset { get; init; }
}

/// <summary>一次扫描的结果。</summary>
/// <param name="Items">扫到的条目(已按文件掩码过滤)。</param>
/// <param name="SkippedLinks">因为是指向目录的链接而没有进入的条目数(不跟随链接,理由见扫描器)。</param>
public sealed record SyncTree(IReadOnlyList<SyncItem> Items, int SkippedLinks);

/// <summary>同步选项。</summary>
public sealed record SyncOptions
{
    /// <summary>方向。</summary>
    public SyncDirection Direction { get; init; } = SyncDirection.ToRemote;

    /// <summary>模式;双向时恒按 <see cref="SyncMode.Synchronize" /> 处理(见 <see cref="EffectiveMode" />)。</summary>
    public SyncMode Mode { get; init; } = SyncMode.Synchronize;

    /// <summary>比较依据。</summary>
    public SyncCriteria Criteria { get; init; } = SyncCriteria.Time | SyncCriteria.Size;

    /// <summary>删除目标上多出来的文件(仅单向、非时间戳模式生效)。</summary>
    public bool DeleteExtraneous { get; init; }

    /// <summary>只处理两边都已存在的文件,不新建。</summary>
    public bool ExistingOnly { get; init; }

    /// <summary>
    /// 路径配对是否忽略大小写。默认跟随本机文件系统:Windows / macOS 的默认卷不区分大小写,
    /// 远端的 <c>Readme.md</c> 与 <c>README.md</c> 在本地只能是同一个文件。
    /// </summary>
    public bool IgnoreCase { get; init; } = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();

    /// <summary>
    /// 时间比较的容差。1 秒吸收 FAT/exFAT 的 2 秒粒度与各端取整方式的差别;
    /// 不做成可配 —— 调大它等于允许漏掉「一秒内改完且大小不变」以外的更多改动。
    /// </summary>
    public TimeSpan TimeTolerance { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>实际生效的模式:双向时镜像与时间戳没有意义(没有「源」),按同步处理。</summary>
    public SyncMode EffectiveMode => Direction == SyncDirection.Both ? SyncMode.Synchronize : Mode;

    /// <summary>实际是否删除多余文件:双向与时间戳模式下永不删除。</summary>
    public bool EffectiveDeleteExtraneous =>
        DeleteExtraneous && Direction != SyncDirection.Both && Mode != SyncMode.Timestamps;
}

/// <summary>一对同路径条目的比较结论。</summary>
public enum SyncComparisonState
{
    /// <summary>按所选依据相同(或两边都是目录)。</summary>
    Same,

    /// <summary>只有本地有。</summary>
    LocalOnly,

    /// <summary>只有远端有。</summary>
    RemoteOnly,

    /// <summary>两边都有,本地较新。</summary>
    LocalNewer,

    /// <summary>两边都有,远端较新。</summary>
    RemoteNewer,

    /// <summary>内容不同(大小不同,或 SHA-256 不同),但时间相同或不比时间 —— 看不出哪边较新。</summary>
    Differs,

    /// <summary>无法安全处理:一边是文件一边是目录,或远端有两个只差大小写的名字而本地区分不了。</summary>
    Conflict,
}

/// <summary>一对同路径条目及其比较结论。</summary>
/// <param name="RelativePath">相对路径。</param>
/// <param name="Local">本地条目;本地没有时为 null。</param>
/// <param name="Remote">远端条目;远端没有时为 null。</param>
/// <param name="State">比较结论。</param>
public sealed record SyncComparison(string RelativePath, SyncItem? Local, SyncItem? Remote, SyncComparisonState State)
{
    /// <summary>是否目录(两边类型不一致时取本地一侧)。</summary>
    public bool IsDirectory => (Local ?? Remote)?.IsDirectory ?? false;
}

/// <summary>同步要执行的一步。</summary>
public enum SyncActionKind
{
    /// <summary>在远端建目录(让空目录也同步过去)。</summary>
    CreateRemoteDirectory,

    /// <summary>在本地建目录。</summary>
    CreateLocalDirectory,

    /// <summary>上传文件。</summary>
    Upload,

    /// <summary>下载文件。</summary>
    Download,

    /// <summary>把远端文件的修改时间改成本地的。</summary>
    SetRemoteTime,

    /// <summary>把本地文件的修改时间改成远端的。</summary>
    SetLocalTime,

    /// <summary>删除远端条目(目录连同内容)。</summary>
    DeleteRemote,

    /// <summary>删除本地条目(目录连同内容)。</summary>
    DeleteLocal,
}

/// <summary>同步计划里的一步。</summary>
/// <param name="Kind">动作类型。</param>
/// <param name="RelativePath">相对路径。</param>
/// <param name="IsDirectory">作用对象是否目录。</param>
/// <param name="Local">本地条目(可能为 null)。</param>
/// <param name="Remote">远端条目(可能为 null)。</param>
public sealed record SyncAction(SyncActionKind Kind, string RelativePath, bool IsDirectory, SyncItem? Local, SyncItem? Remote)
{
    /// <summary>这一步要搬的字节数;非传输动作为 0。</summary>
    public long Bytes => Kind switch
    {
        SyncActionKind.Upload => Local?.Size ?? 0,
        SyncActionKind.Download => Remote?.Size ?? 0,
        _ => 0,
    };
}

/// <summary>同步用到的时间换算。</summary>
public static class SyncTime
{
    /// <summary>
    /// 统一换成 UTC。<see cref="DateTimeKind.Unspecified" /> 按本地时间解释 —— FTP 的 LIST 给的就是
    /// 这种不带时区的"服务器时间",文件浏览器的时间列也是这么显示的,两处口径一致。
    /// </summary>
    public static DateTime ToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Local).ToUniversalTime(),
    };

    /// <summary>
    /// 同 <see cref="ToUtc(DateTime)" />,但不带时区的时间按 <paramref name="serverClockOffset" /> 解读(服务器时钟 = UTC + 偏移);
    /// 偏移为 null 时按本机时间解读。
    /// </summary>
    /// <remarks>
    /// <b>只挪 <see cref="DateTimeKind.Unspecified" /> 的时间</b>:那是 FTP LIST 给的服务器墙上时间。
    /// MLSD / MLST 的 <c>modify</c> 按 RFC 3659 恒为 UTC,FluentFTP 交出来时就标着 <see cref="DateTimeKind.Utc" />,
    /// 再按偏移挪一遍就错了 —— 同一台服务器上 LIST 与 MLSD 两条路径因此都对得上。
    /// <para>
    /// <b>没有时间的条目不挪,挪到头的夹住。</b>LIST / MLSD 给不出时间的条目(MLSD 不给目录的 <c>modify</c>、认不出的 LIST 行)
    /// 由 <c>FtpFileService</c> 记成 <see cref="DateTime.MinValue" />;东八区按偏移一减就越过下界,
    /// <see cref="ArgumentOutOfRangeException" /> 直接打断「比较目录」(复查 file-panes F6)。<see cref="ToUtc(DateTime)" /> 的
    /// <c>ToUniversalTime</c> 本来就会夹住,这里同样夹在 <see cref="DateTime.MinValue" /> / <see cref="DateTime.MaxValue" /> 上;
    /// 那种时间按 <see cref="InferPrecision" /> 本来就是「时间未知」,不参与比较。
    /// </para>
    /// </remarks>
    /// <param name="value">后端给的原始时间。</param>
    /// <param name="serverClockOffset">服务器时钟的 UTC 偏移;null = 与本机同一时区。</param>
    public static DateTime ToUtc(DateTime value, TimeSpan? serverClockOffset)
    {
        if (value.Kind != DateTimeKind.Unspecified || serverClockOffset is not { } offset)
        {
            return ToUtc(value);
        }
        if (value == DateTime.MinValue)
        {
            return DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc);
        }
        long ticks = value.Ticks - offset.Ticks;
        return new DateTime(Math.Clamp(ticks, DateTime.MinValue.Ticks, DateTime.MaxValue.Ticks), DateTimeKind.Utc);
    }

    /// <summary>
    /// 远端条目的这条时间要不要记日历偏移(见 <see cref="SyncItem.CalendarOffset" />):不带时区的原始时间、且配了偏移才记。
    /// </summary>
    /// <param name="raw">后端给的原始时间。</param>
    /// <param name="serverClockOffset">服务器时钟的 UTC 偏移;null = 与本机同一时区。</param>
    public static TimeSpan? CalendarOffsetFor(DateTime raw, TimeSpan? serverClockOffset) =>
        raw.Kind == DateTimeKind.Unspecified ? serverClockOffset : null;

    /// <summary>
    /// 这条连接配置的服务器时钟偏移:只对 FTP / FTPS 有意义(LIST 的时间不带时区),其余协议一律为 null。
    /// </summary>
    /// <param name="profile">连接配置。</param>
    public static TimeSpan? ServerClockOffset(SessionProfile? profile) =>
        profile is { ConnectionType: ConnectionType.FTP, Ftp.ServerUtcOffsetMinutes: { } minutes }
            ? TimeSpan.FromMinutes(minutes)
            : null;

    /// <summary>
    /// 从后端给的原始时间推断精度:没给(默认值、1970 年以前)为未知,时分秒全 0 为日,秒为 0 为分钟,否则为秒。
    /// 只对「本来就可能粗」的后端(FTP、插件协议)使用;SFTP 的 mtime 恒为秒级,不该被一个恰好落在整分的时间降级。
    /// </summary>
    public static SyncTimePrecision InferPrecision(DateTime raw)
    {
        if (raw.Year < 1971)
        {
            return SyncTimePrecision.Unknown;
        }
        if (raw.TimeOfDay == TimeSpan.Zero)
        {
            return SyncTimePrecision.Day;
        }
        return raw.Ticks % TimeSpan.TicksPerMinute == 0 ? SyncTimePrecision.Minute : SyncTimePrecision.Second;
    }

    /// <summary>
    /// 比较两个条目的修改时间:&gt;0 表示 <paramref name="a" /> 较新,&lt;0 表示 <paramref name="b" /> 较新,0 表示视为相同。
    /// 两边先按较粗的精度截断,再按容差判等。截断在 LIST 日期所属的那本日历里做:LIST 的日期是按服务器当地日历给的,
    /// 配了服务器时区(<see cref="SyncItem.CalendarOffset" />)就按那个偏移截,没配就按本机时区截(默认服务器与本机同一时区)。
    /// 两边必须在同一本日历里截,否则同一刻落在两边的相邻两天。
    /// </summary>
    public static int Compare(SyncItem a, SyncItem b, TimeSpan tolerance)
    {
        var precision = (SyncTimePrecision)Math.Max((int)a.Precision, (int)b.Precision);
        if (precision == SyncTimePrecision.Unknown)
        {
            return 0;
        }
        TimeSpan? calendar = a.CalendarOffset ?? b.CalendarOffset;
        DateTime left = Truncate(InCalendar(a.LastWriteTimeUtc, calendar), precision);
        DateTime right = Truncate(InCalendar(b.LastWriteTimeUtc, calendar), precision);
        TimeSpan diff = left - right;
        return diff.Duration() <= tolerance ? 0 : Math.Sign(diff.Ticks);
    }

    /// <summary>UTC 时间 → 某本日历上的墙上时间:给了偏移就是 UTC + 偏移,否则是本机时间。</summary>
    private static DateTime InCalendar(DateTime utc, TimeSpan? offset) =>
        offset is { } o ? DateTime.SpecifyKind(utc + o, DateTimeKind.Unspecified) : utc.ToLocalTime();

    private static DateTime Truncate(DateTime value, SyncTimePrecision precision) => precision switch
    {
        SyncTimePrecision.Day => value.Date,
        SyncTimePrecision.Minute => new(value.Ticks - (value.Ticks % TimeSpan.TicksPerMinute), value.Kind),
        _ => new(value.Ticks - (value.Ticks % TimeSpan.TicksPerSecond), value.Kind),
    };
}

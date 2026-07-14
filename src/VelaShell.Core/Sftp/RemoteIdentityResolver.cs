using System.Collections.Concurrent;
using System.Globalization;
using VelaShell.Core.Ssh;

namespace VelaShell.Core.Sftp;

/// <summary>
/// 一次远端查表得到的 UID/GID → 名称映射。查不到的 id 不入表,由取名方法回退数字。
/// </summary>
/// <param name="Users">UID → 用户名。</param>
/// <param name="Groups">GID → 组名。</param>
internal sealed record RemoteIdentityMap(IReadOnlyDictionary<int, string> Users, IReadOnlyDictionary<int, string> Groups)
{
    /// <summary>查表不可用时的空映射:所有 id 都回退为数字。</summary>
    public static readonly RemoteIdentityMap Empty = new(new Dictionary<int, string>(), new Dictionary<int, string>());

    /// <summary>取 UID 对应的用户名;查不到则回退十进制数字(与 ls -n 的显示一致)。</summary>
    public string UserName(int uid) =>
        Users.TryGetValue(uid, out string? name) ? name : uid.ToString(CultureInfo.InvariantCulture);

    /// <summary>取 GID 对应的组名;查不到则回退十进制数字。</summary>
    public string GroupName(int gid) =>
        Groups.TryGetValue(gid, out string? name) ? name : gid.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// 把 SFTP 报的数字 UID/GID 翻成名称。SFTP v3 的列目录结果里只有数字 id(见
/// <see cref="SftpEntry.UserId" />),名称得另经 SSH exec 通道查远端的 passwd/group 库。
/// 整表一次读回并按会话缓存,后续切目录零额外往返;查不到就回退数字,不阻断浏览。
/// </summary>
/// <param name="connectionService">用于按会话取 SSH 客户端执行查表命令。</param>
internal sealed class RemoteIdentityResolver(ISshConnectionService connectionService)
{
    /// <summary>passwd 段与 group 段的分隔标记(一次往返读回两张表)。</summary>
    private const string SectionSeparator = "###VELA-GROUPS###";

    /// <summary>
    /// getent 走 NSS,能覆盖 LDAP/SSSD 等非文件后端;busybox 等精简镜像没有它时回退直读
    /// /etc/passwd。两段一次执行:整个命令失败(无 exec 通道、非 Unix 主机)时
    /// <see cref="LoadAsync" /> 回退空映射。
    /// </summary>
    private const string LookupCommand =
        "{ getent passwd 2>/dev/null || cat /etc/passwd 2>/dev/null; }; " +
        "echo " + SectionSeparator + "; " +
        "{ getent group 2>/dev/null || cat /etc/group 2>/dev/null; }";

    /// <summary>
    /// 查表超时:LDAP 后端的 getent passwd 可能枚举很久甚至挂住,不能让它拖住目录列举。
    /// 超时即当作查不到,显示数字 id。
    /// </summary>
    private static readonly TimeSpan LookupTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// 按会话缓存查表任务(而非结果):并发的首次列目录共享同一次远端查询。
    /// 失败结果同样被缓存,避免每次切目录都对着没有 exec 通道的主机重试。
    /// </summary>
    private readonly ConcurrentDictionary<Guid, Task<RemoteIdentityMap>> _cache = new();

    /// <summary>取该会话的 UID/GID → 名称映射;首次调用触发远端查表,之后走缓存。</summary>
    public Task<RemoteIdentityMap> GetAsync(Guid sessionId) =>
        _cache.GetOrAdd(sessionId, static (id, self) => self.LoadAsync(id), this);

    /// <summary>丢弃会话的映射缓存(会话关闭时调用;重连后会重新查表)。</summary>
    public void Invalidate(Guid sessionId) => _cache.TryRemove(sessionId, out _);

    private async Task<RemoteIdentityMap> LoadAsync(Guid sessionId)
    {
        ISshClientWrapper? client = connectionService.GetClient(sessionId);
        if (client is null)
        {
            return RemoteIdentityMap.Empty;
        }
        try
        {
            using var timeout = new CancellationTokenSource(LookupTimeout);
            string output = await client.RunCommandAsync(LookupCommand, timeout.Token).ConfigureAwait(false);
            return Parse(output);
        }
        catch
        {
            // 仅 SFTP(无 exec 通道)、非 Unix 主机、查表超时、会话中途断开:
            // 名称查不到就显示数字 id,不该让文件浏览本身失败。
            return RemoteIdentityMap.Empty;
        }
    }

    private static RemoteIdentityMap Parse(string output)
    {
        int split = output.IndexOf(SectionSeparator, StringComparison.Ordinal);

        // 没有分隔标记 = group 段没跑出来(如 echo 被 shell 吞掉);passwd 段仍可用。
        string passwd = split >= 0 ? output[..split] : output;
        string group = split >= 0 ? output[(split + SectionSeparator.Length)..] : string.Empty;
        return new(ParseSection(passwd), ParseSection(group));
    }

    /// <summary>
    /// 解析 passwd/group 的行格式 "name:x:id:…" —— 两张表的前三列同构,故共用。
    /// 同 id 多行时以首行为准:getent 的首行即 NSS 的解析结果。
    /// </summary>
    private static Dictionary<int, string> ParseSection(string section)
    {
        Dictionary<int, string> map = [];
        foreach (string line in section.Split('\n'))
        {
            string[] parts = line.Split(':');
            if (parts.Length < 3)
            {
                continue;
            }

            // Trim 掉 CRLF 主机的 \r,否则 "1000\r" 解析不出数字。
            if (!int.TryParse(parts[2].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int id))
            {
                continue;
            }
            string name = parts[0].Trim();
            if (name.Length > 0)
            {
                map.TryAdd(id, name);
            }
        }
        return map;
    }
}

using VelaShell.Core.Net;
using VelaShell.Ssh.Session;
using VelaConnectionInfo = VelaShell.Core.Models.ConnectionInfo;

namespace VelaShell.Infrastructure.Ssh;

/// <summary>
/// 一次建连实际走过的路:经过哪个出站代理、成功连上的每一台跳板(连同那一跳的连接)。
/// </summary>
/// <remarks>
/// <para>
/// 连接信息面板要列「这条会话经过了哪几跳、每跳谈成了什么」。库的拨号器只在<b>失败</b>时把逐跳结果放进异常的 <c>Hops</c>,
/// 成功时什么都不留;而跳板的连接本来就是宿主在 <see cref="SshConnectionAssembler" /> 的回调里一跳一跳现建的,
/// 在那里记下最省事,还能拿到每一跳谈成的算法 —— 库的逐跳记录里本来也没有这些。
/// </para>
/// <para>
/// 一次装配对应一个包装器、一次连接;<see cref="Begin" /> 在每次连接开始时把上一次的记录清掉。
/// 跳板的连接归拨出来的流所有(外层连接释放时逐层断开),这里只拿着引用读属性,不负责释放。
/// </para>
/// </remarks>
internal sealed class SshRouteRecorder
{
    private readonly Lock _gate = new();
    private List<RecordedHop> _hops = [];

    /// <summary>一台连上了的跳板。</summary>
    /// <param name="Info">这一跳的连接信息。</param>
    /// <param name="Connection">这一跳的连接。</param>
    internal readonly record struct RecordedHop(VelaConnectionInfo Info, SshConnection Connection);

    /// <summary>一次连接开始:清掉上一次的记录。</summary>
    public void Begin()
    {
        lock (_gate)
        {
            _hops = [];
            Proxy = null;
        }
    }

    /// <summary>一台跳板连上了(按连上的先后,也就是从近到远)。</summary>
    public void AddHop(VelaConnectionInfo info, SshConnection connection)
    {
        lock (_gate)
        {
            _hops.Add(new(info, connection));
        }
    }

    /// <summary>最内一跳的出站走了哪条路由;直连不记。</summary>
    public void SetProxy(ProxyRoute route)
    {
        if (route.Kind == ProxyKind.None)
        {
            return;
        }
        lock (_gate)
        {
            Proxy = $"{route.Scheme} {HostAddress.FormatEndPoint(route.Host, route.Port)}";
        }
    }

    /// <summary>经过的跳板,从近到远。</summary>
    public IReadOnlyList<RecordedHop> Hops
    {
        get
        {
            lock (_gate)
            {
                return [.. _hops];
            }
        }
    }

    /// <summary>经过的出站代理(<c>socks5 主机:端口</c>);直连时为 <see langword="null" />。</summary>
    public string? Proxy
    {
        get
        {
            lock (_gate)
            {
                return field;
            }
        }

        private set;
    }
}

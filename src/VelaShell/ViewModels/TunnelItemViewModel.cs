using ReactiveUI;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;

namespace VelaShell.ViewModels;

/// <summary>隧道列表项视图模型:包装共享的 <see cref="TunnelInfo" />,向界面暴露路由、状态、流量等只读展示属性。</summary>
public class TunnelItemViewModel(TunnelInfo tunnelInfo) : ReactiveObject
{
    private readonly TunnelInfo _tunnelInfo = tunnelInfo ?? throw new ArgumentNullException(nameof(tunnelInfo));

    /// <summary>隧道唯一标识,用于在面板中定位与去重。</summary>
    public Guid Id => _tunnelInfo.Id;

    /// <summary>原始配置,编辑/重启时用来预填表单与重建转发。</summary>
    public TunnelConfig Config => _tunnelInfo.Config;

    /// <summary>该隧道所在的 SSH 会话(面板按服务器归组时用来定位)。</summary>
    public Guid SessionId => _tunnelInfo.SessionId;

    /// <summary>显示名称:未自定义名称时回退为路由描述。</summary>
    public string Name => string.IsNullOrWhiteSpace(_tunnelInfo.Config.Name)
                              ? DisplayRoute
                              : _tunnelInfo.Config.Name;

    /// <summary>隧道类型(本地/远程/动态转发)。</summary>
    public TunnelType TunnelType => _tunnelInfo.Config.Type;

    /// <summary>本地绑定主机。</summary>
    public string LocalHost => _tunnelInfo.Config.LocalHost;

    /// <summary>本地绑定端口。</summary>
    public uint LocalPort => _tunnelInfo.Config.LocalPort;

    /// <summary>远程目标主机。</summary>
    public string RemoteHost => _tunnelInfo.Config.RemoteHost;

    /// <summary>远程目标端口。</summary>
    public uint RemotePort => _tunnelInfo.Config.RemotePort;

    /// <summary>隧道创建时间(UTC),用于计算运行时长。</summary>
    public DateTime CreatedAt => _tunnelInfo.CreatedAt;

    /// <summary>路由描述:本地/动态以本地端口为起点,远程转发方向相反(设计 B3Rth)。</summary>
    public string DisplayRoute => TunnelType switch
    {
        TunnelType.RemoteForward => Strings.Format("Msg_TunnelRouteRemote", RemotePort, LocalHost, LocalPort),
        TunnelType.DynamicForward => Strings.Format("Msg_TunnelRouteDynamic", LocalHost, LocalPort),
        _ => $"{LocalHost}:{LocalPort} → {RemoteHost}:{RemotePort}"
    };

    /// <summary>类型标签(设计 B3Rth:Local/Remote/Dynamic 全词徽标)。</summary>
    public string TypeBadge => TunnelType switch
    {
        TunnelType.RemoteForward => "Remote",
        TunnelType.DynamicForward => "Dynamic",
        _ => "Local"
    };

    /// <summary>
    /// 状态直接读写共享的 <see cref="TunnelInfo" />:服务侧(会话断开、停止全部)
    /// 改的状态,界面经 <see cref="RefreshLive" /> 就能看到,不再各存一份而彼此失联。
    /// </summary>
    public TunnelStatus Status
    {
        get => _tunnelInfo.Status;
        set
        {
            if (_tunnelInfo.Status == value)
            {
                return;
            }
            _tunnelInfo.Status = value;
            RaiseLiveChanged();
        }
    }

    /// <summary>累计转发字节数(由服务侧写入共享 TunnelInfo)。</summary>
    public long BytesTransferred => _tunnelInfo.BytesTransferred;

    /// <summary>累计流量的人类可读格式(如 1.2 MB)。</summary>
    public string FormattedBytes => FormatBytes(BytesTransferred);

    /// <summary>隧道是否处于活动状态。</summary>
    public bool IsActive => Status == TunnelStatus.Active;

    /// <summary>最近一次转发通道错误(目标拒绝连接等),由服务写入共享 TunnelInfo。</summary>
    public string? LastError => _tunnelInfo.LastError;

    /// <summary>是否存在最近一次错误。</summary>
    public bool HasError => !string.IsNullOrEmpty(_tunnelInfo.LastError);

    /// <summary>状态行:活动中显示运行时长,否则显示状态文字(设计 B3Rth tunI1Stats)。</summary>
    public string StatusText => Status switch
    {
        TunnelStatus.Active => Strings.Format("Msg_TunnelRunning", FormatUptime(DateTime.UtcNow - CreatedAt)),
        TunnelStatus.Error => Strings.Get("Msg_ErrorOccurred"),
        _ => Strings.Get("Msg_Stopped")
    };

    /// <summary>由面板的时钟周期性调用:刷新运行时长、透传服务侧的状态/错误变化。</summary>
    public void RefreshLive() => RaiseLiveChanged();

    private void RaiseLiveChanged()
    {
        this.RaisePropertyChanged(nameof(Status));
        this.RaisePropertyChanged(nameof(IsActive));
        this.RaisePropertyChanged(nameof(StatusText));
        this.RaisePropertyChanged(nameof(LastError));
        this.RaisePropertyChanged(nameof(HasError));
    }

    private static string FormatUptime(TimeSpan uptime)
    {
        if (uptime < TimeSpan.Zero)
        {
            uptime = TimeSpan.Zero;
        }
        if (uptime.TotalHours >= 1)
        {
            return Strings.Format("Msg_UptimeHours", (int)uptime.TotalHours, uptime.Minutes);
        }
        if (uptime.TotalMinutes >= 1)
        {
            return Strings.Format("Msg_UptimeMinutes", (int)uptime.TotalMinutes);
        }
        return Strings.Get("Msg_UptimeUnderMinute");
    }

    /// <summary>将字节数格式化为带单位(B/KB/MB/GB/TB)的可读字符串。</summary>
    public static string FormatBytes(long bytes)
    {
        if (bytes == 0)
        {
            return "0 B";
        }
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        int i = (int)Math.Floor(Math.Log(bytes, 1024));
        i = Math.Min(i, units.Length - 1);
        return $"{bytes / Math.Pow(1024, i):F1} {units[i]}";
    }
}

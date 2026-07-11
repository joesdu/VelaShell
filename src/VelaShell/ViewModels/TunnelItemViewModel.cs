using ReactiveUI;
using VelaShell.Core.Models;

namespace VelaShell.ViewModels;

public class TunnelItemViewModel(TunnelInfo tunnelInfo) : ReactiveObject
{
    private readonly TunnelInfo _tunnelInfo = tunnelInfo ?? throw new ArgumentNullException(nameof(tunnelInfo));

    public Guid Id => _tunnelInfo.Id;

    /// <summary>原始配置,编辑/重启时用来预填表单与重建转发。</summary>
    public TunnelConfig Config => _tunnelInfo.Config;

    /// <summary>该隧道所在的 SSH 会话(面板按服务器归组时用来定位)。</summary>
    public Guid SessionId => _tunnelInfo.SessionId;

    public string Name => string.IsNullOrWhiteSpace(_tunnelInfo.Config.Name)
                              ? DisplayRoute
                              : _tunnelInfo.Config.Name;

    public TunnelType TunnelType => _tunnelInfo.Config.Type;

    public string LocalHost => _tunnelInfo.Config.LocalHost;

    public uint LocalPort => _tunnelInfo.Config.LocalPort;

    public string RemoteHost => _tunnelInfo.Config.RemoteHost;

    public uint RemotePort => _tunnelInfo.Config.RemotePort;

    public DateTime CreatedAt => _tunnelInfo.CreatedAt;

    /// <summary>路由描述:本地/动态以本地端口为起点,远程转发方向相反(设计 B3Rth)。</summary>
    public string DisplayRoute => TunnelType switch
    {
        TunnelType.RemoteForward => $"服务器:{RemotePort} → {LocalHost}:{LocalPort}",
        TunnelType.DynamicForward => $"{LocalHost}:{LocalPort} → SOCKS5 代理",
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

    public long BytesTransferred => _tunnelInfo.BytesTransferred;

    public string FormattedBytes => FormatBytes(BytesTransferred);

    public bool IsActive => Status == TunnelStatus.Active;

    /// <summary>最近一次转发通道错误(目标拒绝连接等),由服务写入共享 TunnelInfo。</summary>
    public string? LastError => _tunnelInfo.LastError;

    public bool HasError => !string.IsNullOrEmpty(_tunnelInfo.LastError);

    /// <summary>状态行:活动中显示运行时长,否则显示状态文字(设计 B3Rth tunI1Stats)。</summary>
    public string StatusText => Status switch
    {
        TunnelStatus.Active => $"运行中 • {FormatUptime(DateTime.UtcNow - CreatedAt)}",
        TunnelStatus.Error => "发生错误",
        _ => "已停止"
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
            return $"已运行 {(int)uptime.TotalHours}h {uptime.Minutes}m";
        }
        if (uptime.TotalMinutes >= 1)
        {
            return $"已运行 {(int)uptime.TotalMinutes}m";
        }
        return "已运行 <1m";
    }

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

namespace VelaShell.Core.Models;

/// <summary>隧道的运行状态。</summary>
public enum TunnelStatus
{
    /// <summary>隧道已建立并正常运行。</summary>
    Active,

    /// <summary>隧道已停止。</summary>
    Stopped,

    /// <summary>隧道因故障处于错误状态。</summary>
    Error
}

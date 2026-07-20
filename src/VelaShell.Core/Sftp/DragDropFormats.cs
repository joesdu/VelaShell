namespace VelaShell.Core.Sftp;

/// <summary>SFTP 双栏视图共用的拖放负载标记。</summary>
public static class DragDropFormats
{
    /// <summary>远端到本地拖放的前缀(远端主机上的路径)。</summary>
    public const string RemotePaths = "VFTP|";

    /// <summary>本地到远端拖放的前缀(本地文件系统上的路径)。</summary>
    public const string LocalPaths = "VFTPL|";
}

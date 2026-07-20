namespace VelaShell.Core.Sftp;

/// <summary>Shared drag-and-drop payload markers used by the SFTP dual-pane views.</summary>
public static class DragDropFormats
{
    /// <summary>Prefix for remote-to-local drags (paths on the remote host).</summary>
    public const string RemotePaths = "VFTP|";

    /// <summary>Prefix for local-to-remote drags (paths on the local filesystem).</summary>
    public const string LocalPaths = "VFTPL|";
}

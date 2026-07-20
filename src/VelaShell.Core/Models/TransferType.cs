namespace VelaShell.Core.Models;

/// <summary>传输任务的方向类型:上传、下载或远端间复制。</summary>
public enum TransferType
{
    /// <summary>上传:将本地文件传输到远端。</summary>
    Upload,
    /// <summary>下载:将远端文件传输到本地。</summary>
    Download,
    /// <summary>远端复制:在同一服务器上将文件/目录复制到另一路径。</summary>
    Copy
}

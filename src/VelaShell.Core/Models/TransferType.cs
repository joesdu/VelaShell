namespace VelaShell.Core.Models;

/// <summary>传输任务的方向类型:上传或下载。</summary>
public enum TransferType
{
    /// <summary>上传:将本地文件传输到远端。</summary>
    Upload,
    /// <summary>下载:将远端文件传输到本地。</summary>
    Download
}

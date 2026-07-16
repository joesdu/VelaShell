namespace VelaShell.Core.ZModem.Model;

/// <summary>ZMODEM 传输方向。</summary>
public enum ZModemTransferDirection
{
    /// <summary>接收:远端 <c>sz</c> 发文件到本地。</summary>
    Receive,

    /// <summary>发送:本地上传文件到远端 <c>rz</c>。</summary>
    Send
}

/// <summary>ZMODEM 会话 / 单文件的状态。</summary>
public enum ZModemTransferStatus
{
    /// <summary>尚未开始。</summary>
    Pending,

    /// <summary>正在传输。</summary>
    Transferring,

    /// <summary>已成功完成。</summary>
    Completed,

    /// <summary>被跳过(接收方拒收或文件已存在)。</summary>
    Skipped,

    /// <summary>失败(CRC 反复失败、IO 错误、协议错误)。</summary>
    Failed,

    /// <summary>被取消(用户中止或收到取消序列)。</summary>
    Cancelled
}

/// <summary>接收方对发送方所提供文件的处置决定。</summary>
public enum ZModemFileDisposition
{
    /// <summary>从头接收该文件。</summary>
    Accept,

    /// <summary>跳过该文件(回 ZSKIP)。</summary>
    Skip,

    /// <summary>中止整个会话。</summary>
    Abort
}
